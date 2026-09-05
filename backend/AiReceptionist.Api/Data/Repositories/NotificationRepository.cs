using AiReceptionist.Api.Domain;
using Dapper;

namespace AiReceptionist.Api.Data.Repositories;

public interface INotificationRepository
{
    // ---- devices ----
    /// <summary>Registers a browser, or refreshes the row when this endpoint is already known.
    /// Subscribing twice from the same browser yields the same endpoint, so this is idempotent —
    /// which matters, because the page re-subscribes on every load.</summary>
    Task<int> UpsertDeviceAsync(PushDevice device, bool? urgentOnly = null);
    Task<IEnumerable<PushDevice>> ListDevicesAsync(int orgId, int? userId = null);
    /// <summary>The devices to notify for one alert. Urgent reaches everyone; a routine booking
    /// skips the devices that asked to be disturbed only when it matters.</summary>
    Task<IEnumerable<PushDevice>> ListTargetsAsync(int orgId, bool urgent);
    Task RemoveDeviceAsync(string endpoint);
    Task RemoveDeviceAsync(int orgId, int id);
    Task MarkDeviceNotifiedAsync(IEnumerable<int> ids);
    Task RecordDeviceFailureAsync(int id);

    // ---- alerts ----
    Task<int> CreateAlertAsync(Alert alert);
    Task<Alert?> GetAlertAsync(int orgId, int id);
    /// <summary>The dashboard's "needs attention" list, and recent history behind it.</summary>
    Task<IEnumerable<Alert>> ListAlertsAsync(int orgId, bool pendingOnly, int take = 50);
    Task<bool> AcknowledgeAsync(int orgId, int id, int? userId);
    /// <summary>Acknowledges everything outstanding — the "I have seen all of this" button.</summary>
    Task<int> AcknowledgeAllAsync(int orgId, int? userId);
    Task MarkAlertNotifiedAsync(int id);
    /// <summary>Urgent alerts nobody has acknowledged that are due another nudge. Oldest first, so
    /// the longest-ignored emergency is chased ahead of a newer one.</summary>
    Task<IEnumerable<Alert>> ListDueForEscalationAsync(TimeSpan interval, int maxAttempts, int take = 50);
}

public class NotificationRepository : INotificationRepository
{
    private readonly IDbConnectionFactory _db;
    public NotificationRepository(IDbConnectionFactory db) => _db = db;

    // A browser that rotates its keys keeps the same endpoint, so the keys are overwritten rather
    // than trusted: a stale P256dh would make every later push undecryptable, and silently so.
    public async Task<int> UpsertDeviceAsync(PushDevice d, bool? urgentOnly = null)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>(@"
            MERGE PushDevices AS t
            USING (SELECT @Endpoint AS Endpoint) AS s ON t.Endpoint = s.Endpoint
            WHEN MATCHED THEN UPDATE SET
                OrganizationId = @OrganizationId, UserId = @UserId, P256dh = @P256dh, Auth = @Auth,
                Label = @Label, UrgentOnly = ISNULL(@UrgentOnly, t.UrgentOnly), FailureCount = 0, IsDeleted = 0
            -- ISNULL here too, not just on the update above. A first enrolment carries no
            -- preference to keep (there is no row yet), and the column is NOT NULL, so passing the
            -- NULL straight through failed every attempt to register a device.
            WHEN NOT MATCHED THEN INSERT (OrganizationId, UserId, Endpoint, P256dh, Auth, Label, UrgentOnly)
                VALUES (@OrganizationId, @UserId, @Endpoint, @P256dh, @Auth, @Label, ISNULL(@UrgentOnly, 0))
            OUTPUT INSERTED.Id;",
            new
            {
                d.OrganizationId, d.UserId, d.Endpoint, d.P256dh, d.Auth, d.Label,
                // NULL on a refresh (keep what is stored); a real value only when the owner is
                // actually changing the setting. ISNULL in the MERGE above does the rest.
                UrgentOnly = urgentOnly,
            });
    }

    public async Task<IEnumerable<PushDevice>> ListDevicesAsync(int orgId, int? userId = null)
    {
        using var conn = _db.Create();
        return await conn.QueryAsync<PushDevice>(@"
            SELECT * FROM PushDevices
            WHERE OrganizationId = @orgId AND IsDeleted = 0
              AND (@userId IS NULL OR UserId = @userId)
            ORDER BY CreatedAt DESC", new { orgId, userId });
    }

    public async Task<IEnumerable<PushDevice>> ListTargetsAsync(int orgId, bool urgent)
    {
        using var conn = _db.Create();
        return await conn.QueryAsync<PushDevice>(@"
            SELECT * FROM PushDevices
            WHERE OrganizationId = @orgId AND IsDeleted = 0
              AND (@urgent = 1 OR UrgentOnly = 0)", new { orgId, urgent });
    }

    // Hard delete, not soft: the subscription is gone at the push service, and the unique index
    // covers live rows only — a tombstone would just stop that browser ever subscribing again.
    public async Task RemoveDeviceAsync(string endpoint)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("DELETE FROM PushDevices WHERE Endpoint = @endpoint", new { endpoint });
    }

    public async Task RemoveDeviceAsync(int orgId, int id)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("DELETE FROM PushDevices WHERE OrganizationId = @orgId AND Id = @id",
            new { orgId, id });
    }

    public async Task MarkDeviceNotifiedAsync(IEnumerable<int> ids)
    {
        var list = ids.ToList();
        if (list.Count == 0) return;
        using var conn = _db.Create();
        await conn.ExecuteAsync(
            "UPDATE PushDevices SET LastNotifiedAt = GETUTCDATE(), FailureCount = 0 WHERE Id IN @list",
            new { list });
    }

    public async Task RecordDeviceFailureAsync(int id)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(
            "UPDATE PushDevices SET FailureCount = FailureCount + 1 WHERE Id = @id", new { id });
    }

    public async Task<int> CreateAlertAsync(Alert a)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO Alerts (OrganizationId, Kind, Severity, Title, Body, Url, AppointmentId)
            OUTPUT INSERTED.Id
            VALUES (@OrganizationId, @Kind, @Severity, @Title, @Body, @Url, @AppointmentId)", a);
    }

    private const string SelectAlertSql = @"
        SELECT a.*, u.FullName AS AcknowledgedByName
        FROM Alerts a
        LEFT JOIN Users u ON u.Id = a.AcknowledgedByUserId AND u.OrganizationId = a.OrganizationId";

    public async Task<Alert?> GetAlertAsync(int orgId, int id)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<Alert>(
            $"{SelectAlertSql} WHERE a.OrganizationId = @orgId AND a.Id = @id", new { orgId, id });
    }

    public async Task<IEnumerable<Alert>> ListAlertsAsync(int orgId, bool pendingOnly, int take = 50)
    {
        using var conn = _db.Create();
        var where = pendingOnly ? " AND a.AcknowledgedAt IS NULL" : "";
        return await conn.QueryAsync<Alert>($@"
            {SelectAlertSql}
            WHERE a.OrganizationId = @orgId{where}
            ORDER BY CASE WHEN a.AcknowledgedAt IS NULL THEN 0 ELSE 1 END,
                     CASE WHEN a.Severity = 'Urgent' THEN 0 ELSE 1 END,
                     a.CreatedAt DESC
            OFFSET 0 ROWS FETCH NEXT @take ROWS ONLY", new { orgId, take });
    }

    // Only the first acknowledgement counts, so two people opening the dashboard at the same
    // moment do not overwrite each other's name on it.
    public async Task<bool> AcknowledgeAsync(int orgId, int id, int? userId)
    {
        using var conn = _db.Create();
        var rows = await conn.ExecuteAsync(@"
            UPDATE Alerts SET AcknowledgedAt = GETUTCDATE(), AcknowledgedByUserId = @userId
            WHERE OrganizationId = @orgId AND Id = @id AND AcknowledgedAt IS NULL",
            new { orgId, id, userId });
        return rows > 0;
    }

    public async Task<int> AcknowledgeAllAsync(int orgId, int? userId)
    {
        using var conn = _db.Create();
        return await conn.ExecuteAsync(@"
            UPDATE Alerts SET AcknowledgedAt = GETUTCDATE(), AcknowledgedByUserId = @userId
            WHERE OrganizationId = @orgId AND AcknowledgedAt IS NULL", new { orgId, userId });
    }

    public async Task MarkAlertNotifiedAsync(int id)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(
            "UPDATE Alerts SET NotifiedCount = NotifiedCount + 1, LastNotifiedAt = GETUTCDATE() WHERE Id = @id",
            new { id });
    }

    public async Task<IEnumerable<Alert>> ListDueForEscalationAsync(TimeSpan interval, int maxAttempts, int take = 50)
    {
        using var conn = _db.Create();
        return await conn.QueryAsync<Alert>(@"
            SELECT TOP (@take) a.* FROM Alerts a
            WHERE a.AcknowledgedAt IS NULL AND a.Severity = 'Urgent'
              AND a.NotifiedCount < @maxAttempts
              AND (a.LastNotifiedAt IS NULL OR a.LastNotifiedAt < DATEADD(second, -@seconds, GETUTCDATE()))
              -- Nothing to chase with. Without this the worker would pick the same alert up every
              -- minute forever for an organization that has never registered a device, delivering
              -- nothing and saying so in the log each time.
              AND EXISTS (SELECT 1 FROM PushDevices d
                          WHERE d.OrganizationId = a.OrganizationId AND d.IsDeleted = 0)
            ORDER BY a.CreatedAt",
            new { take, maxAttempts, seconds = (int)interval.TotalSeconds });
    }
}
