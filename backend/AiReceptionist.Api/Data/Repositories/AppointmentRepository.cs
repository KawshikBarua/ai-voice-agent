using AiReceptionist.Api.Common;
using AiReceptionist.Api.Domain;
using Dapper;

namespace AiReceptionist.Api.Data.Repositories;

public interface IAppointmentRepository
{
    Task<PagedResult<Appointment>> ListAsync(int orgId, DateTime? from, DateTime? to, string? status, int page, int pageSize);
    Task<Appointment?> GetAsync(int orgId, int id);
    /// <summary>Atomically checks capacity/conflicts and inserts. Returns the new id, or null if the slot is taken.</summary>
    Task<int?> TryCreateAsync(Appointment appt);
    /// <summary>Atomically checks capacity/conflicts and moves the appointment. Returns false if the new slot is taken.</summary>
    Task<bool> TryRescheduleAsync(int orgId, int id, DateTime startAt, DateTime endAt);
    Task UpdateStatusAsync(int orgId, int id, string status);
    Task UpdatePaymentStatusAsync(int orgId, int id, string paymentStatus);
    Task<SidebarCounts> GetSidebarCountsAsync(int orgId);
}

/// <summary>Live badge counts shown next to the sidebar nav items.</summary>
public class SidebarCounts
{
    public int Appointments { get; set; }
    public int Calls { get; set; }
    public int Customers { get; set; }
}

public class AppointmentRepository : IAppointmentRepository
{
    private readonly IDbConnectionFactory _db;
    public AppointmentRepository(IDbConnectionFactory db) => _db = db;

    private const string SelectSql = @"
        SELECT a.*, c.Name AS CustomerName, c.Phone AS CustomerPhone, s.Name AS ServiceName, u.FullName AS StaffName
        FROM Appointments a
        JOIN Customers c ON c.Id = a.CustomerId
        LEFT JOIN Services s ON s.Id = a.ServiceId
        LEFT JOIN Users u ON u.Id = a.StaffUserId";

    public async Task<PagedResult<Appointment>> ListAsync(int orgId, DateTime? from, DateTime? to, string? status, int page, int pageSize)
    {
        using var conn = _db.Create();
        var where = "a.OrganizationId = @orgId AND a.IsDeleted = 0";
        if (from.HasValue) where += " AND a.StartAt >= @from";
        if (to.HasValue) where += " AND a.StartAt < @to";
        if (!string.IsNullOrWhiteSpace(status)) where += " AND a.Status = @status";
        var p = new { orgId, from, to, status, skip = (page - 1) * pageSize, take = pageSize };

        var total = await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM Appointments a WHERE {where}", p);
        var items = await conn.QueryAsync<Appointment>(
            $"{SelectSql} WHERE {where} ORDER BY a.StartAt DESC, a.Id DESC OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY", p);

        return new PagedResult<Appointment> { Items = items, Page = page, PageSize = pageSize, TotalCount = total };
    }

    public async Task<Appointment?> GetAsync(int orgId, int id)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<Appointment>(
            $"{SelectSql} WHERE a.OrganizationId = @orgId AND a.Id = @id AND a.IsDeleted = 0", new { orgId, id });
    }

    // Range locks (UPDLOCK/HOLDLOCK) inside a transaction make check + insert atomic,
    // so two concurrent AI calls cannot double-book the last free slot. Capacity comes
    // from Organizations.MaxConcurrentAppointments (e.g. number of staff/rooms).
    private const string SlotIsFreeSql = @"
        (SELECT COUNT(*) FROM Appointments WITH (UPDLOCK, HOLDLOCK)
         WHERE OrganizationId = @OrganizationId AND IsDeleted = 0
           AND Status NOT IN ('Cancelled','Missed')
           AND StartAt < @EndAt AND EndAt > @StartAt
           AND (@ExcludeId IS NULL OR Id <> @ExcludeId))
        < (SELECT ISNULL(MaxConcurrentAppointments, 1) FROM Organizations WHERE Id = @OrganizationId)
        AND (@StaffUserId IS NULL OR NOT EXISTS
            (SELECT 1 FROM Appointments WITH (UPDLOCK, HOLDLOCK)
             WHERE OrganizationId = @OrganizationId AND IsDeleted = 0
               AND Status NOT IN ('Cancelled','Missed') AND StaffUserId = @StaffUserId
               AND StartAt < @EndAt AND EndAt > @StartAt
               AND (@ExcludeId IS NULL OR Id <> @ExcludeId)))";

    public async Task<int?> TryCreateAsync(Appointment a)
    {
        using var conn = _db.Create();
        conn.Open();
        using var tx = conn.BeginTransaction();
        var id = await conn.ExecuteScalarAsync<int?>($@"
            INSERT INTO Appointments (OrganizationId, CustomerId, ServiceId, StaffUserId, StartAt, EndAt, Status, PaymentStatus, Amount, ServiceAddress, IsEmergency, Notes)
            OUTPUT INSERTED.Id
            SELECT @OrganizationId, @CustomerId, @ServiceId, @StaffUserId, @StartAt, @EndAt, @Status, @PaymentStatus, @Amount, @ServiceAddress, @IsEmergency, @Notes
            WHERE {SlotIsFreeSql}",
            new
            {
                a.OrganizationId, a.CustomerId, a.ServiceId, a.StaffUserId, a.StartAt, a.EndAt,
                a.Status, a.PaymentStatus, a.Amount, a.ServiceAddress, a.IsEmergency, a.Notes,
                ExcludeId = (int?)null,
            }, tx);
        tx.Commit();
        return id;
    }

    public async Task<bool> TryRescheduleAsync(int orgId, int id, DateTime startAt, DateTime endAt)
    {
        using var conn = _db.Create();
        conn.Open();
        using var tx = conn.BeginTransaction();
        var rows = await conn.ExecuteAsync($@"
            UPDATE Appointments SET StartAt = @StartAt, EndAt = @EndAt, Status = 'Scheduled', ModifiedAt = GETUTCDATE()
            WHERE OrganizationId = @OrganizationId AND Id = @ExcludeId AND IsDeleted = 0
              AND {SlotIsFreeSql}",
            new
            {
                OrganizationId = orgId, ExcludeId = (int?)id, StartAt = startAt, EndAt = endAt,
                StaffUserId = (int?)null,
            }, tx);
        tx.Commit();
        return rows > 0;
    }

    public async Task UpdatePaymentStatusAsync(int orgId, int id, string paymentStatus)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE Appointments SET PaymentStatus=@paymentStatus, ModifiedAt=GETUTCDATE()
            WHERE OrganizationId=@orgId AND Id=@id AND IsDeleted=0",
            new { orgId, id, paymentStatus });
    }

    /// <summary>Badge counts for the sidebar: appointments awaiting confirmation,
    /// missed calls needing follow-up, and total active customers.</summary>
    public async Task<SidebarCounts> GetSidebarCountsAsync(int orgId)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleAsync<SidebarCounts>(@"
            SELECT
              (SELECT COUNT(*) FROM Appointments
               WHERE OrganizationId=@orgId AND IsDeleted=0
                 AND Status = 'Scheduled' AND StartAt >= GETUTCDATE()) AS Appointments,
              (SELECT COUNT(*) FROM CallLogs
               WHERE OrganizationId=@orgId AND IsDeleted=0 AND Status='Missed') AS Calls,
              (SELECT COUNT(*) FROM Customers
               WHERE OrganizationId=@orgId AND IsDeleted=0) AS Customers",
            new { orgId });
    }

    public async Task UpdateStatusAsync(int orgId, int id, string status)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE Appointments SET Status=@status, ModifiedAt=GETUTCDATE()
            WHERE OrganizationId=@orgId AND Id=@id AND IsDeleted=0;

            -- Completed visits feed returning-customer history (SRS §7)
            IF @status = 'Completed'
            UPDATE c SET
                LastVisit = a.StartAt,
                FirstVisit = ISNULL(c.FirstVisit, a.StartAt),
                TotalVisits = c.TotalVisits + 1
            FROM Customers c
            JOIN Appointments a ON a.CustomerId = c.Id
            WHERE a.OrganizationId=@orgId AND a.Id=@id;",
            new { orgId, id, status });
    }
}
