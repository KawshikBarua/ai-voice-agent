using AiReceptionist.SuperAdmin.Models;
using Dapper;

namespace AiReceptionist.SuperAdmin.Data.Repositories;

public interface IBillingRepository
{
    Task<Subscription?> GetSubscriptionAsync(int orgId);
    Task UpsertSubscriptionAsync(Subscription subscription, int? userId);
    Task<IReadOnlyList<Payment>> ListPaymentsAsync(int orgId, int take = 24);
    Task AddPaymentAsync(Payment payment);

    /// <summary>Moves the billing period forward by one cycle. Called when a payment is recorded
    /// with "extend the period" ticked.</summary>
    Task AdvancePeriodAsync(int orgId, DateTime newPeriodStart, DateTime newPeriodEnd, int? userId);

    /// <summary>Total subscription income actually collected since <paramref name="since"/>.</summary>
    Task<decimal> CollectedSinceAsync(DateTime since);

    /// <summary>Organizations that are still enabled, opted into auto-suspend, and past their
    /// grace period. Used by the nightly sweep.</summary>
    Task<IReadOnlyList<int>> ListLapsedAutoSuspendAsync(DateTime utcNow);

    // ---- read-only views of what the tenant API owns ----
    // Tiers, closed periods and invoices are written by the API (it holds the Stripe key); this
    // console reads them directly for display and drives every change through the platform
    // endpoints, the same way it already does for Retell.

    Task<IReadOnlyList<PricingPlan>> ListPlansAsync(bool activeOnly = false);

    /// <summary>The periods that have closed, newest first — the evidence behind each charge.</summary>
    Task<IReadOnlyList<UsagePeriod>> ListUsagePeriodsAsync(int orgId, int take = 12);

    Task<IReadOnlyList<OrganizationInvoice>> ListInvoicesAsync(int orgId, int take = 12);
}

public class BillingRepository : IBillingRepository
{
    private readonly IDbConnectionFactory _db;
    public BillingRepository(IDbConnectionFactory db) => _db = db;

    public async Task<Subscription?> GetSubscriptionAsync(int orgId)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<Subscription>(
            "SELECT * FROM OrganizationSubscriptions WHERE OrganizationId = @orgId", new { orgId });
    }

    public async Task UpsertSubscriptionAsync(Subscription s, int? userId)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE OrganizationSubscriptions
            SET PlanName = @PlanName, BillingCycle = @BillingCycle, Amount = @Amount, Currency = @Currency,
                CurrentPeriodStart = @CurrentPeriodStart, CurrentPeriodEnd = @CurrentPeriodEnd,
                GraceDays = @GraceDays, AutoSuspend = @AutoSuspend, IncludedMinutes = @IncludedMinutes,
                OverageRatePerMinute = @OverageRatePerMinute,
                Notes = @Notes, ModifiedAt = GETUTCDATE(), ModifiedByUserId = @userId
            WHERE OrganizationId = @OrganizationId;

            IF @@ROWCOUNT = 0
            INSERT INTO OrganizationSubscriptions
                (OrganizationId, PlanName, BillingCycle, Amount, Currency, StartedAt,
                 CurrentPeriodStart, CurrentPeriodEnd, GraceDays, AutoSuspend, IncludedMinutes,
                 OverageRatePerMinute, Notes, ModifiedAt, ModifiedByUserId)
            VALUES (@OrganizationId, @PlanName, @BillingCycle, @Amount, @Currency, GETUTCDATE(),
                    @CurrentPeriodStart, @CurrentPeriodEnd, @GraceDays, @AutoSuspend, @IncludedMinutes,
                    @OverageRatePerMinute, @Notes, GETUTCDATE(), @userId);",
            new
            {
                s.OrganizationId, s.PlanName, s.BillingCycle, s.Amount, s.Currency,
                s.CurrentPeriodStart, s.CurrentPeriodEnd, s.GraceDays, s.AutoSuspend,
                s.IncludedMinutes, s.OverageRatePerMinute, s.Notes, userId,
            });
    }

    public async Task<IReadOnlyList<Payment>> ListPaymentsAsync(int orgId, int take = 24)
    {
        using var conn = _db.Create();
        var rows = await conn.QueryAsync<Payment>(@"
            SELECT TOP (@take) p.*, u.FullName AS RecordedByName
            FROM OrganizationPayments p
            LEFT JOIN Users u ON u.Id = p.RecordedByUserId
            WHERE p.OrganizationId = @orgId
            ORDER BY p.PaidAt DESC, p.Id DESC", new { orgId, take });
        return rows.ToList();
    }

    public async Task AddPaymentAsync(Payment p)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            INSERT INTO OrganizationPayments
                (OrganizationId, Amount, Currency, PaidAt, PeriodStart, PeriodEnd, Method, Reference, Notes, RecordedByUserId)
            VALUES (@OrganizationId, @Amount, @Currency, @PaidAt, @PeriodStart, @PeriodEnd, @Method, @Reference, @Notes, @RecordedByUserId)",
            p);
    }

    public async Task AdvancePeriodAsync(int orgId, DateTime newPeriodStart, DateTime newPeriodEnd, int? userId)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE OrganizationSubscriptions
            SET CurrentPeriodStart = @newPeriodStart, CurrentPeriodEnd = @newPeriodEnd,
                ModifiedAt = GETUTCDATE(), ModifiedByUserId = @userId
            WHERE OrganizationId = @orgId", new { orgId, newPeriodStart, newPeriodEnd, userId });
    }

    public async Task<decimal> CollectedSinceAsync(DateTime since)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<decimal>(
            "SELECT ISNULL(SUM(Amount),0) FROM OrganizationPayments WHERE PaidAt >= @since", new { since });
    }

    public async Task<IReadOnlyList<PricingPlan>> ListPlansAsync(bool activeOnly = false)
    {
        using var conn = _db.Create();
        var rows = await conn.QueryAsync<PricingPlan>($@"
            SELECT p.*,
                   (SELECT COUNT(*) FROM OrganizationSubscriptions s WHERE s.PlanId = p.Id) AS SubscriberCount
            FROM PricingPlans p
            WHERE p.IsDeleted = 0 {(activeOnly ? "AND p.IsActive = 1" : "")}
            ORDER BY p.SortOrder, p.Amount, p.Name");
        return rows.ToList();
    }

    public async Task<IReadOnlyList<UsagePeriod>> ListUsagePeriodsAsync(int orgId, int take = 12)
    {
        using var conn = _db.Create();
        var rows = await conn.QueryAsync<UsagePeriod>(@"
            SELECT TOP (@take) * FROM OrganizationUsagePeriods
            WHERE OrganizationId = @orgId ORDER BY PeriodEnd DESC", new { orgId, take });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<OrganizationInvoice>> ListInvoicesAsync(int orgId, int take = 12)
    {
        using var conn = _db.Create();
        var invoices = (await conn.QueryAsync<OrganizationInvoice>(@"
            SELECT TOP (@take) * FROM OrganizationInvoices
            WHERE OrganizationId = @orgId
            ORDER BY ISNULL(IssuedAt, CreatedAt) DESC, Id DESC", new { orgId, take })).ToList();

        if (invoices.Count == 0) return invoices;

        var ids = invoices.Select(i => i.Id).ToArray();
        var lines = await conn.QueryAsync<OrganizationInvoiceLine>(
            "SELECT * FROM OrganizationInvoiceLines WHERE InvoiceId IN @ids ORDER BY InvoiceId, SortOrder",
            new { ids });

        var byInvoice = lines.GroupBy(l => l.InvoiceId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var invoice in invoices)
            if (byInvoice.TryGetValue(invoice.Id, out var own)) invoice.Lines = own;

        return invoices;
    }

    public async Task<IReadOnlyList<int>> ListLapsedAutoSuspendAsync(DateTime utcNow)
    {
        using var conn = _db.Create();
        var ids = await conn.QueryAsync<int>(@"
            SELECT s.OrganizationId
            FROM OrganizationSubscriptions s
            JOIN Organizations o ON o.Id = s.OrganizationId
            WHERE o.IsDeleted = 0 AND o.IsActive = 1 AND s.AutoSuspend = 1
              -- On a plan at all. A subscription row also exists purely to hold the Stripe
              -- customer link of someone who has not chosen a tier yet (SubscriptionRecord.HasPlan),
              -- and suspending an organization for not paying an invoice nobody has raised would
              -- lock a brand-new customer out before they ever got started.
              AND (s.PlanId IS NOT NULL OR s.Amount > 0 OR s.IncludedMinutes > 0)
              AND DATEADD(day, s.GraceDays, s.CurrentPeriodEnd) < @utcNow", new { utcNow });
        return ids.ToList();
    }
}
