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
               -- Minutes in the window now running. It starts where the last closed period ended,
               -- the same anchor the API bills from, so this column and the customer's own billing
               -- page never disagree about how much they have used.
               (SELECT ISNULL(SUM(CEILING(cl.DurationSeconds / 60.0)), 0) FROM CallLogs cl
                  WHERE cl.OrganizationId = o.Id AND cl.IsDeleted = 0
                    AND cl.StartedAt >= ISNULL(
                        (SELECT MAX(up.PeriodEnd) FROM OrganizationUsagePeriods up
                           WHERE up.OrganizationId = o.Id),
                        s.CurrentPeriodStart)) AS MinutesUsedThisPeriod
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

        var rows = await conn.QueryAsync<OrganizationRow>(sql,
            new { since = Since30d(), term = $"%{search?.Trim()}%" });
        return rows.ToList();
    }

    public async Task<OrganizationRow?> GetAsync(int orgId)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<OrganizationRow>(
            RowSelect + " AND o.Id = @orgId", new { since = Since30d(), orgId });
    }

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
