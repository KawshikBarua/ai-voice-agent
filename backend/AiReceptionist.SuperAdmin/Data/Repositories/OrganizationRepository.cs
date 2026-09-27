using AiReceptionist.SuperAdmin.Models;
using Dapper;

namespace AiReceptionist.SuperAdmin.Data.Repositories;

public interface IOrganizationRepository
{
    Task<IReadOnlyList<OrganizationRow>> ListAsync(string? search = null);
    Task<OrganizationRow?> GetAsync(int orgId);
    Task<OrganizationAgent?> GetAgentAsync(int orgId);

    /// <summary>Enables or disables a tenant account. Disabling blocks sign-in, token refresh and
    /// the AI agent's live-call tools in the tenant API.</summary>
    Task SetActiveAsync(int orgId, bool active, string? reason);

    Task<IReadOnlyList<CallRow>> RecentCallsAsync(int orgId, int take = 10);
    Task<IReadOnlyList<AppointmentRow>> RecentAppointmentsAsync(int orgId, int take = 10);
    Task<IReadOnlyList<MonthlyPoint>> CallsPerMonthAsync(int orgId, int year);
}

public class OrganizationRepository : IOrganizationRepository
{
    private readonly IDbConnectionFactory _db;
    public OrganizationRepository(IDbConnectionFactory db) => _db = db;

    // Aggregates are correlated subqueries rather than joins so that one organization with many
    // calls cannot inflate another metric's row count.
    private const string RowSelect = @"
        SELECT o.Id, o.Name, o.Industry, o.Email, o.Phone, o.Currency, o.Timezone, o.CreatedAt,
               o.IsActive, o.SuspendedAt, o.SuspendedReason,
               o.AgentRestricted, o.AgentRestrictedAt, o.AgentRestrictedReason,
               o.TrialStartedAt, o.TrialEndsAt,
               (SELECT COUNT(*) FROM Users u
                  WHERE u.OrganizationId = o.Id AND u.IsDeleted = 0 AND u.Role <> 'SuperAdmin') AS UserCount,
               (SELECT COUNT(*) FROM Customers c
                  WHERE c.OrganizationId = o.Id AND c.IsDeleted = 0) AS CustomerCount,
               (SELECT COUNT(*) FROM Appointments a
                  WHERE a.OrganizationId = o.Id AND a.IsDeleted = 0) AS AppointmentCount,
               (SELECT COUNT(*) FROM Appointments a
                  WHERE a.OrganizationId = o.Id AND a.IsDeleted = 0
                    AND a.StartAt > GETUTCDATE() AND a.Status IN ('Scheduled','Confirmed')) AS UpcomingAppointments,
               (SELECT ISNULL(SUM(a.Amount),0) FROM Appointments a
                  WHERE a.OrganizationId = o.Id AND a.IsDeleted = 0 AND a.PaymentStatus = 'Paid') AS Revenue,
               (SELECT ISNULL(SUM(a.Amount),0) FROM Appointments a
                  WHERE a.OrganizationId = o.Id AND a.IsDeleted = 0 AND a.PaymentStatus = 'Paid'
                    AND a.StartAt >= @since) AS Revenue30d,
               (SELECT ISNULL(SUM(a.Amount),0) FROM Appointments a
                  WHERE a.OrganizationId = o.Id AND a.IsDeleted = 0 AND a.PaymentStatus = 'Unpaid'
                    AND a.Status <> 'Cancelled') AS Outstanding,
               (SELECT COUNT(*) FROM CallLogs cl
                  WHERE cl.OrganizationId = o.Id AND cl.IsDeleted = 0) AS TotalCalls,
               (SELECT COUNT(*) FROM CallLogs cl
                  WHERE cl.OrganizationId = o.Id AND cl.IsDeleted = 0 AND cl.StartedAt >= @since) AS Calls30d,
               (SELECT COUNT(*) FROM CallLogs cl
                  WHERE cl.OrganizationId = o.Id AND cl.IsDeleted = 0 AND cl.Status = 'Missed') AS MissedCalls,
               (SELECT COUNT(*) FROM CallLogs cl
                  WHERE cl.OrganizationId = o.Id AND cl.IsDeleted = 0 AND cl.Status = 'Transferred') AS TransferredCalls,
               (SELECT MAX(cl.StartedAt) FROM CallLogs cl
                  WHERE cl.OrganizationId = o.Id AND cl.IsDeleted = 0) AS LastCallAt,
               ac.RetellAgentId, ac.RetellPhoneNumber, ac.LastSyncedAt,
               ISNULL(ac.Enabled, 0) AS AgentEnabled,
               s.PlanName, s.BillingCycle, s.Amount AS SubscriptionAmount,
               s.Currency AS SubscriptionCurrency, s.CurrentPeriodEnd, s.GraceDays, s.AutoSuspend,
               s.PlanId, s.IncludedMinutes, s.OverageRatePerMinute, s.StripeStatus,
               s.StripeSubscriptionId, s.PendingOverageMinutes, s.PendingOverageAmount,
               s.CurrentPeriodStart AS SubscriptionPeriodStart
               -- Minutes used are filled in afterwards by FillUsageAsync, not here: see there.
        FROM Organizations o
        LEFT JOIN AgentConfig ac ON ac.OrganizationId = o.Id
        LEFT JOIN OrganizationSubscriptions s ON s.OrganizationId = o.Id
        WHERE o.IsDeleted = 0";

    public async Task<IReadOnlyList<OrganizationRow>> ListAsync(string? search = null)
    {
        using var conn = _db.Create();
        var sql = RowSelect;
        if (!string.IsNullOrWhiteSpace(search))
            sql += " AND (o.Name LIKE @term OR o.Email LIKE @term OR o.Industry LIKE @term OR o.Phone LIKE @term)";
        sql += " ORDER BY o.Name";

        var rows = (await conn.QueryAsync<OrganizationRow>(sql,
            new { since = Since30d(), term = $"%{search?.Trim()}%" })).ToList();
        await FillUsageAsync(conn, rows);
        return rows;
    }

    public async Task<OrganizationRow?> GetAsync(int orgId)
    {
        using var conn = _db.Create();
        var row = await conn.QuerySingleOrDefaultAsync<OrganizationRow>(
            RowSelect + " AND o.Id = @orgId", new { since = Since30d(), orgId });
        if (row is not null) await FillUsageAsync(conn, [row]);
        return row;
    }

    /// <summary>
    /// Minutes used this period, counted exactly as the tenant API counts them for the customer's
    /// own dashboard and invoice.
    ///
    /// This console used to count by date — calls started since the last closed period — while the
    /// API counts by the unbilled marker (<see cref="BillingSchema.UnbilledPredicate"/>). The two
    /// disagree as soon as a call is recorded after its period closed, and the per-call rounding was
    /// copied here separately too, so the operator and the customer could be looking at two
    /// different figures for the same bill. Both now come from <see cref="BillingSchema"/>.
    ///
    /// An account with no plan is bounded to the monthly window it is in, as the API's usage
    /// endpoint bounds it (nothing is ever closed for it, so "unbilled" alone would be its whole
    /// history). That window is worked out here with the same <see cref="UsageWindows"/> the API
    /// uses rather than re-expressed in SQL, where month-end dates would drift from it.
    /// </summary>
    private static async Task FillUsageAsync(System.Data.IDbConnection conn, IReadOnlyList<OrganizationRow> rows)
    {
        var now = DateTime.UtcNow;

        foreach (var chunk in rows.Chunk(500))
        {
            var parameters = new DynamicParameters();
            var bounds = new List<string>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                parameters.Add($"o{i}", chunk[i].Id, System.Data.DbType.Int32);
                parameters.Add($"f{i}", CountFrom(chunk[i], now), System.Data.DbType.DateTime2);
                bounds.Add($"(@o{i}, @f{i})");
            }

            var usage = await conn.QueryAsync<(int OrgId, int Minutes, long Seconds)>($@"
                SELECT b.OrgId,
                       (SELECT {BillingSchema.MinutesExpression} FROM CallLogs
                         WHERE OrganizationId = b.OrgId AND {BillingSchema.UsagePredicate("b.CountFrom")}) AS Minutes,
                       (SELECT {BillingSchema.SecondsExpression} FROM CallLogs
                         WHERE OrganizationId = b.OrgId AND {BillingSchema.UsagePredicate("b.CountFrom")}) AS Seconds
                FROM (VALUES {string.Join(", ", bounds)}) AS b(OrgId, CountFrom)", parameters);

            var byOrg = usage.ToDictionary(u => u.OrgId);
            foreach (var row in chunk)
            {
                if (!byOrg.TryGetValue(row.Id, out var u)) continue;
                row.MinutesUsedThisPeriod = u.Minutes;
                row.SecondsUsedThisPeriod = u.Seconds;
            }
        }
    }

    /// <summary>Mirrors BillingService.LoadUsageAsync in the API: a plan counts every unbilled call;
    /// no plan counts from the start of the monthly window now running.</summary>
    private static DateTime? CountFrom(OrganizationRow row, DateTime now) =>
        row.HasSubscription && row.HasPlan
            ? null
            : UsageWindows.Current(row.SubscriptionPeriodStart ?? now, BillingCycles.Monthly, now).Start;

    public async Task<OrganizationAgent?> GetAgentAsync(int orgId)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<OrganizationAgent>(@"
            SELECT OrganizationId, Voice, Language, TransferNumber, RetellAgentId, RetellLlmId,
                   RetellKnowledgeBaseId, RetellPhoneNumber, LastSyncedAt, Enabled
            FROM AgentConfig WHERE OrganizationId = @orgId", new { orgId });
    }

    public async Task SetActiveAsync(int orgId, bool active, string? reason)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE Organizations
            SET IsActive = @active,
                SuspendedAt = CASE WHEN @active = 1 THEN NULL ELSE ISNULL(SuspendedAt, GETUTCDATE()) END,
                SuspendedReason = CASE WHEN @active = 1 THEN NULL ELSE @reason END
            WHERE Id = @orgId", new { orgId, active, reason });
    }

    public async Task<IReadOnlyList<CallRow>> RecentCallsAsync(int orgId, int take = 10)
    {
        using var conn = _db.Create();
        var rows = await conn.QueryAsync<CallRow>(@"
            SELECT TOP (@take) cl.Id, cl.FromNumber, cl.Status, cl.DurationSeconds, cl.Summary,
                   cl.StartedAt, c.Name AS CustomerName
            FROM CallLogs cl
            LEFT JOIN Customers c ON c.Id = cl.CustomerId
            WHERE cl.OrganizationId = @orgId AND cl.IsDeleted = 0
            ORDER BY cl.StartedAt DESC", new { orgId, take });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<AppointmentRow>> RecentAppointmentsAsync(int orgId, int take = 10)
    {
        using var conn = _db.Create();
        var rows = await conn.QueryAsync<AppointmentRow>(@"
            SELECT TOP (@take) a.Id, a.StartAt, a.Status, a.PaymentStatus, a.Amount,
                   c.Name AS CustomerName, s.Name AS ServiceName
            FROM Appointments a
            LEFT JOIN Customers c ON c.Id = a.CustomerId
            LEFT JOIN Services s ON s.Id = a.ServiceId
            WHERE a.OrganizationId = @orgId AND a.IsDeleted = 0
            ORDER BY a.StartAt DESC", new { orgId, take });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<MonthlyPoint>> CallsPerMonthAsync(int orgId, int year)
    {
        using var conn = _db.Create();
        var rows = await conn.QueryAsync<MonthlyPoint>(@"
            SELECT MONTH(StartedAt) AS Month, COUNT(*) AS Value
            FROM CallLogs
            WHERE OrganizationId = @orgId AND IsDeleted = 0 AND YEAR(StartedAt) = @year
            GROUP BY MONTH(StartedAt) ORDER BY Month", new { orgId, year });
        return rows.ToList();
    }

    private static DateTime Since30d() => DateTime.UtcNow.AddDays(-30);
}
