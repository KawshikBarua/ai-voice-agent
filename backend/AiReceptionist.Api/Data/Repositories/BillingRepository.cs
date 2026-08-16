using Dapper;

namespace AiReceptionist.Api.Data.Repositories;

/// <summary>
/// Reads and writes the billing tables from the tenant API's side: what a customer is on, how many
/// minutes they have used, what has been closed and charged, and the Stripe linkage.
///
/// The super admin console owns the same tables from the other direction (tiers, manual payments,
/// suspension). Both go through <c>Shared/BillingSchema.cs</c>, so neither needs to know which one
/// created a table.
/// </summary>
public interface IBillingRepository
{
    // ---------- subscription ----------
    Task<SubscriptionRecord?> GetSubscriptionAsync(int orgId);
    Task<SubscriptionRecord?> FindByStripeCustomerAsync(string stripeCustomerId);
    Task<SubscriptionRecord?> FindByStripeSubscriptionAsync(string stripeSubscriptionId);
    Task<IReadOnlyList<SubscriptionRecord>> ListSubscriptionsAsync();

    Task SetStripeCustomerAsync(int orgId, string stripeCustomerId);
    Task SetStripeSubscriptionAsync(int orgId, string? stripeSubscriptionId, string? status);
    Task SetStripeStatusAsync(int orgId, string? status);

    /// <summary>Moves the <b>access</b> window on by one cycle — what recording a payment does.
    /// Usage windows are separate and move on their own (see <see cref="UsageWindows"/>).</summary>
    Task AdvanceAccessPeriodAsync(int orgId, DateTime newPeriodStart, DateTime newPeriodEnd);

    // ---------- usage ----------
    /// <summary>AI minutes started inside the half-open window [start, end).</summary>
    Task<int> MinutesUsedAsync(int orgId, DateTime startUtc, DateTime endUtc);

    /// <summary>End of the most recently closed period — the anchor the next usage window runs
    /// from. Null before any period has closed.</summary>
    Task<DateTime?> LastClosedPeriodEndAsync(int orgId);

    /// <summary>Files a closed period. Returns false if one was already filed for this period end,
    /// which is how the worker and the two webhooks can all attempt the same close safely.</summary>
    Task<bool> TryAddUsagePeriodAsync(UsagePeriod period);

    Task<IReadOnlyList<UsagePeriod>> ListUsagePeriodsAsync(int orgId, int take = 24);

    /// <summary>Adds a closed period's overrun to what the next invoice will carry.</summary>
    Task AddPendingOverageAsync(int orgId, int minutes, decimal amount);

    /// <summary>Called once the pending overage has been attached to a Stripe invoice: zeroes the
    /// carry-over and marks the periods it came from as billed against that invoice.</summary>
    Task SettlePendingOverageAsync(int orgId, string stripeInvoiceId);

    // ---------- invoices ----------
    Task UpsertInvoiceAsync(OrganizationInvoice invoice, IReadOnlyList<OrganizationInvoiceLine> lines);
    Task<IReadOnlyList<OrganizationInvoice>> ListInvoicesAsync(int orgId, int take = 24);

    // ---------- payments ----------
    /// <summary>Records a Stripe-collected payment. Returns false when this invoice has already
    /// been recorded — Stripe redelivers webhooks, and a customer must not be credited twice.</summary>
    Task<bool> TryAddStripePaymentAsync(PaymentRecord payment);

    // ---------- tiers ----------
    Task<IReadOnlyList<PricingPlan>> ListPlansAsync(bool activeOnly = false);
    Task<PricingPlan?> GetPlanAsync(int planId);
    Task<int> UpsertPlanAsync(PricingPlan plan, int? userId);
    Task SetPlanStripeIdsAsync(int planId, string? productId, string? priceId);
    Task SoftDeletePlanAsync(int planId);

    /// <summary>The tier a Stripe price belongs to. This is how a subscription changed in Stripe is
    /// turned back into a tier here, so the allowance follows what the customer is actually paying.
    /// Repricing mints a new price and leaves the old one live, so the lookup is not unique — the
    /// tier currently carrying the price wins, and an old price resolves to nothing.</summary>
    Task<PricingPlan?> FindPlanByStripePriceAsync(string stripePriceId);

    /// <summary>Puts an organization on a tier, copying the tier's numbers onto its subscription.
    /// Creates the subscription if there is none yet.</summary>
    Task ApplyPlanAsync(int orgId, PricingPlan plan, int? includedMinutesOverride,
        decimal? overageRateOverride, decimal? amountOverride);

    /// <summary>Parks a tier change until the period in force has closed. Null clears it.</summary>
    Task SetPendingPlanAsync(int orgId, int? planId);

    // ---------- account state ----------
    /// <summary>Lifts a suspension that the overdue sweep applied, once the customer has paid.
    /// A suspension entered by hand carries a different reason and is deliberately left alone.</summary>
    Task<bool> ReactivateIfOverdueSuspendedAsync(int orgId);

    /// <summary>Stops (or restores) the AI agent for one organization without touching sign-in, so
    /// a customer who has been cut off can still log in, see why, and settle the bill.</summary>
    Task SetAgentRestrictedAsync(int orgId, bool restricted, string? reason);

    // ---------- webhook idempotency ----------
    /// <summary>Claims a Stripe event id. False means it has been seen before and must be skipped.</summary>
    Task<bool> TryClaimWebhookEventAsync(string eventId, string type);
    Task MarkWebhookDoneAsync(string eventId, string? error);
}

public class BillingRepository : IBillingRepository
{
    private readonly IDbConnectionFactory _db;
    public BillingRepository(IDbConnectionFactory db) => _db = db;

    private const string SubscriptionColumns = @"
        Id, OrganizationId, PlanName, BillingCycle, Amount, Currency, StartedAt,
        CurrentPeriodStart, CurrentPeriodEnd, GraceDays, AutoSuspend, IncludedMinutes, Notes,
        ModifiedAt, ModifiedByUserId, PlanId, OverageRatePerMinute, StripeCustomerId,
        StripeSubscriptionId, StripeStatus, PendingOverageMinutes, PendingOverageAmount,
        PendingPlanId";

    // ---------- subscription ----------

    public async Task<SubscriptionRecord?> GetSubscriptionAsync(int orgId)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<SubscriptionRecord>(
            $"SELECT {SubscriptionColumns} FROM OrganizationSubscriptions WHERE OrganizationId = @orgId",
            new { orgId });
    }

    public async Task<SubscriptionRecord?> FindByStripeCustomerAsync(string stripeCustomerId)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<SubscriptionRecord>(
            $"SELECT {SubscriptionColumns} FROM OrganizationSubscriptions WHERE StripeCustomerId = @stripeCustomerId",
            new { stripeCustomerId });
    }

    public async Task<SubscriptionRecord?> FindByStripeSubscriptionAsync(string stripeSubscriptionId)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<SubscriptionRecord>(
            $"SELECT {SubscriptionColumns} FROM OrganizationSubscriptions WHERE StripeSubscriptionId = @stripeSubscriptionId",
            new { stripeSubscriptionId });
    }

    public async Task<IReadOnlyList<SubscriptionRecord>> ListSubscriptionsAsync()
    {
        using var conn = _db.Create();
        // Deleted or suspended organizations still get their periods closed: the record of what
        // they used has to be complete, whether or not anyone ends up collecting for it.
        var rows = await conn.QueryAsync<SubscriptionRecord>($@"
            SELECT {SubscriptionColumns} FROM OrganizationSubscriptions s
            WHERE EXISTS (SELECT 1 FROM Organizations o WHERE o.Id = s.OrganizationId AND o.IsDeleted = 0)");
        return rows.ToList();
    }

    public async Task SetStripeCustomerAsync(int orgId, string stripeCustomerId)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(
            "UPDATE OrganizationSubscriptions SET StripeCustomerId = @stripeCustomerId WHERE OrganizationId = @orgId",
            new { orgId, stripeCustomerId });
    }

    public async Task SetStripeSubscriptionAsync(int orgId, string? stripeSubscriptionId, string? status)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE OrganizationSubscriptions
            SET StripeSubscriptionId = @stripeSubscriptionId, StripeStatus = @status, ModifiedAt = GETUTCDATE()
            WHERE OrganizationId = @orgId",
            new { orgId, stripeSubscriptionId, status });
    }

    public async Task SetStripeStatusAsync(int orgId, string? status)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(
            "UPDATE OrganizationSubscriptions SET StripeStatus = @status WHERE OrganizationId = @orgId",
            new { orgId, status });
    }

    public async Task AdvanceAccessPeriodAsync(int orgId, DateTime newPeriodStart, DateTime newPeriodEnd)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE OrganizationSubscriptions
            SET CurrentPeriodStart = @newPeriodStart, CurrentPeriodEnd = @newPeriodEnd, ModifiedAt = GETUTCDATE()
            WHERE OrganizationId = @orgId",
            new { orgId, newPeriodStart, newPeriodEnd });
    }

    // ---------- usage ----------

    public async Task<int> MinutesUsedAsync(int orgId, DateTime startUtc, DateTime endUtc)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>($@"
            SELECT {BillingSchema.MinutesExpression} FROM CallLogs
            WHERE OrganizationId = @orgId AND IsDeleted = 0
              AND StartedAt >= @startUtc AND StartedAt < @endUtc",
            new { orgId, startUtc, endUtc });
    }

    public async Task<DateTime?> LastClosedPeriodEndAsync(int orgId)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<DateTime?>(
            "SELECT MAX(PeriodEnd) FROM OrganizationUsagePeriods WHERE OrganizationId = @orgId",
            new { orgId });
    }

    public async Task<bool> TryAddUsagePeriodAsync(UsagePeriod p)
    {
        using var conn = _db.Create();
        // The guard makes the common case cheap; the unique index is what actually holds if two
        // closes race (the worker and a webhook arriving at the same moment).
        try
        {
            var inserted = await conn.ExecuteAsync(@"
                IF NOT EXISTS (SELECT 1 FROM OrganizationUsagePeriods
                               WHERE OrganizationId = @OrganizationId AND PeriodEnd = @PeriodEnd)
                INSERT INTO OrganizationUsagePeriods
                    (OrganizationId, PeriodStart, PeriodEnd, PlanName, BaseAmount, Currency,
                     IncludedMinutes, MinutesUsed, OverageMinutes, OverageRatePerMinute,
                     OverageAmount, Status, StripeInvoiceId)
                VALUES (@OrganizationId, @PeriodStart, @PeriodEnd, @PlanName, @BaseAmount, @Currency,
                        @IncludedMinutes, @MinutesUsed, @OverageMinutes, @OverageRatePerMinute,
                        @OverageAmount, @Status, @StripeInvoiceId);", p);
            return inserted > 0;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // Lost the race; the other caller filed this period.
            return false;
        }
    }

    public async Task<IReadOnlyList<UsagePeriod>> ListUsagePeriodsAsync(int orgId, int take = 24)
    {
        using var conn = _db.Create();
        var rows = await conn.QueryAsync<UsagePeriod>(@"
            SELECT TOP (@take) * FROM OrganizationUsagePeriods
            WHERE OrganizationId = @orgId ORDER BY PeriodEnd DESC", new { orgId, take });
        return rows.ToList();
    }

    public async Task AddPendingOverageAsync(int orgId, int minutes, decimal amount)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE OrganizationSubscriptions
            SET PendingOverageMinutes = PendingOverageMinutes + @minutes,
                PendingOverageAmount = PendingOverageAmount + @amount
            WHERE OrganizationId = @orgId", new { orgId, minutes, amount });
    }

    public async Task SettlePendingOverageAsync(int orgId, string stripeInvoiceId)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync($@"
            UPDATE OrganizationUsagePeriods
            SET Status = '{UsagePeriodStatus.Billed}', StripeInvoiceId = @stripeInvoiceId
            WHERE OrganizationId = @orgId AND Status = '{UsagePeriodStatus.Pending}';

            UPDATE OrganizationSubscriptions
            SET PendingOverageMinutes = 0, PendingOverageAmount = 0
            WHERE OrganizationId = @orgId;",
            new { orgId, stripeInvoiceId });
    }

    // ---------- invoices ----------

    public async Task UpsertInvoiceAsync(OrganizationInvoice inv, IReadOnlyList<OrganizationInvoiceLine> lines)
    {
        using var conn = _db.Create();
        conn.Open();
        using var tx = conn.BeginTransaction();

        var invoiceId = await conn.ExecuteScalarAsync<int>(@"
            UPDATE OrganizationInvoices
            SET Number = @Number, Status = @Status, Currency = @Currency, Subtotal = @Subtotal,
                Total = @Total, AmountPaid = @AmountPaid, AmountDue = @AmountDue,
                PeriodStart = @PeriodStart, PeriodEnd = @PeriodEnd,
                HostedInvoiceUrl = @HostedInvoiceUrl, InvoicePdfUrl = @InvoicePdfUrl,
                IssuedAt = @IssuedAt, PaidAt = @PaidAt, UpdatedAt = GETUTCDATE()
            WHERE StripeInvoiceId = @StripeInvoiceId;

            IF @@ROWCOUNT = 0
            INSERT INTO OrganizationInvoices
                (OrganizationId, StripeInvoiceId, Number, Status, Currency, Subtotal, Total,
                 AmountPaid, AmountDue, PeriodStart, PeriodEnd, HostedInvoiceUrl, InvoicePdfUrl,
                 IssuedAt, PaidAt, UpdatedAt)
            VALUES (@OrganizationId, @StripeInvoiceId, @Number, @Status, @Currency, @Subtotal, @Total,
                    @AmountPaid, @AmountDue, @PeriodStart, @PeriodEnd, @HostedInvoiceUrl, @InvoicePdfUrl,
                    @IssuedAt, @PaidAt, GETUTCDATE());

            SELECT Id FROM OrganizationInvoices WHERE StripeInvoiceId = @StripeInvoiceId;",
            inv, tx);

        // Stripe is the authority on what is on an invoice, so the mirrored lines are replaced
        // wholesale rather than merged — a line removed upstream must not linger here.
        await conn.ExecuteAsync("DELETE FROM OrganizationInvoiceLines WHERE InvoiceId = @invoiceId",
            new { invoiceId }, tx);

        if (lines.Count > 0)
            await conn.ExecuteAsync(@"
                INSERT INTO OrganizationInvoiceLines (InvoiceId, Description, Quantity, Amount, Kind, SortOrder)
                VALUES (@InvoiceId, @Description, @Quantity, @Amount, @Kind, @SortOrder)",
                lines.Select((l, i) => new
                {
                    InvoiceId = invoiceId,
                    l.Description,
                    l.Quantity,
                    l.Amount,
                    l.Kind,
                    SortOrder = i,
                }), tx);

        tx.Commit();
    }

    public async Task<IReadOnlyList<OrganizationInvoice>> ListInvoicesAsync(int orgId, int take = 24)
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

    // ---------- payments ----------

    public async Task<bool> TryAddStripePaymentAsync(PaymentRecord p)
    {
        using var conn = _db.Create();
        try
        {
            var inserted = await conn.ExecuteAsync(@"
                IF NOT EXISTS (SELECT 1 FROM OrganizationPayments WHERE StripeInvoiceId = @StripeInvoiceId)
                INSERT INTO OrganizationPayments
                    (OrganizationId, Amount, Currency, PaidAt, PeriodStart, PeriodEnd, Method,
                     Reference, Notes, RecordedByUserId, Source, StripeInvoiceId, StripePaymentIntentId)
                VALUES (@OrganizationId, @Amount, @Currency, @PaidAt, @PeriodStart, @PeriodEnd, @Method,
                        @Reference, @Notes, NULL, @Source, @StripeInvoiceId, @StripePaymentIntentId);", p);
            return inserted > 0;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
        {
            return false;
        }
    }

    // ---------- tiers ----------

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

    public async Task<PricingPlan?> GetPlanAsync(int planId)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<PricingPlan>(
            "SELECT * FROM PricingPlans WHERE Id = @planId AND IsDeleted = 0", new { planId });
    }

    public async Task<PricingPlan?> FindPlanByStripePriceAsync(string stripePriceId)
    {
        using var conn = _db.Create();
        // QueryFirst, not QuerySingle: a price id should identify one tier, but a duplicated id
        // from a botched repricing must not throw in the middle of a webhook and cost the delivery.
        return await conn.QueryFirstOrDefaultAsync<PricingPlan>(
            "SELECT * FROM PricingPlans WHERE StripePriceId = @stripePriceId AND IsDeleted = 0",
            new { stripePriceId });
    }

    public async Task<int> UpsertPlanAsync(PricingPlan p, int? userId)
    {
        using var conn = _db.Create();
        if (p.Id > 0)
        {
            await conn.ExecuteAsync(@"
                UPDATE PricingPlans
                SET Name = @Name, Description = @Description, Currency = @Currency, Amount = @Amount,
                    BillingCycle = @BillingCycle, IncludedMinutes = @IncludedMinutes,
                    OverageRatePerMinute = @OverageRatePerMinute, SortOrder = @SortOrder,
                    IsActive = @IsActive, ModifiedAt = GETUTCDATE(), ModifiedByUserId = @userId
                WHERE Id = @Id AND IsDeleted = 0",
                new
                {
                    p.Id, p.Name, p.Description, p.Currency, p.Amount, p.BillingCycle,
                    p.IncludedMinutes, p.OverageRatePerMinute, p.SortOrder, p.IsActive, userId,
                });
            return p.Id;
        }

        return await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO PricingPlans
                (Name, Description, Currency, Amount, BillingCycle, IncludedMinutes,
                 OverageRatePerMinute, SortOrder, IsActive, ModifiedAt, ModifiedByUserId)
            OUTPUT INSERTED.Id
            VALUES (@Name, @Description, @Currency, @Amount, @BillingCycle, @IncludedMinutes,
                    @OverageRatePerMinute, @SortOrder, @IsActive, GETUTCDATE(), @userId)",
            new
            {
                p.Name, p.Description, p.Currency, p.Amount, p.BillingCycle,
                p.IncludedMinutes, p.OverageRatePerMinute, p.SortOrder, p.IsActive, userId,
            });
    }

    public async Task SetPlanStripeIdsAsync(int planId, string? productId, string? priceId)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE PricingPlans SET StripeProductId = @productId, StripePriceId = @priceId,
                                    ModifiedAt = GETUTCDATE()
            WHERE Id = @planId", new { planId, productId, priceId });
    }

    public async Task SoftDeletePlanAsync(int planId)
    {
        using var conn = _db.Create();
        // Organizations already on the tier keep their copied numbers; only the catalogue entry
        // goes, so an old plan can be retired without disturbing anyone billing on it.
        await conn.ExecuteAsync(
            "UPDATE PricingPlans SET IsDeleted = 1, IsActive = 0, ModifiedAt = GETUTCDATE() WHERE Id = @planId",
            new { planId });
    }

    public async Task ApplyPlanAsync(int orgId, PricingPlan plan, int? includedMinutesOverride,
        decimal? overageRateOverride, decimal? amountOverride)
    {
        var amount = amountOverride ?? plan.Amount;
        var includedMinutes = includedMinutesOverride ?? plan.IncludedMinutes;
        var overageRate = overageRateOverride ?? plan.OverageRatePerMinute;
        var cycle = BillingCycles.Normalize(plan.BillingCycle);

        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE OrganizationSubscriptions
            SET PlanId = @planId, PlanName = @planName, BillingCycle = @cycle, Amount = @amount,
                Currency = @currency, IncludedMinutes = @includedMinutes,
                OverageRatePerMinute = @overageRate, ModifiedAt = GETUTCDATE(),
                -- Whatever was queued has either just been applied or been overtaken by this.
                PendingPlanId = NULL
            WHERE OrganizationId = @orgId;

            IF @@ROWCOUNT = 0
            INSERT INTO OrganizationSubscriptions
                (OrganizationId, PlanId, PlanName, BillingCycle, Amount, Currency, StartedAt,
                 CurrentPeriodStart, CurrentPeriodEnd, IncludedMinutes, OverageRatePerMinute, ModifiedAt)
            VALUES (@orgId, @planId, @planName, @cycle, @amount, @currency, GETUTCDATE(),
                    GETUTCDATE(), @firstPeriodEnd, @includedMinutes, @overageRate, GETUTCDATE());",
            new
            {
                orgId,
                planId = plan.Id,
                planName = plan.Name,
                cycle,
                amount,
                currency = plan.Currency,
                includedMinutes,
                overageRate,
                firstPeriodEnd = BillingCycles.Advance(DateTime.UtcNow, cycle),
            });
    }

    public async Task SetPendingPlanAsync(int orgId, int? planId)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE OrganizationSubscriptions
            SET PendingPlanId = @planId, ModifiedAt = GETUTCDATE()
            WHERE OrganizationId = @orgId",
            new { orgId, planId });
    }

    // ---------- account state ----------

    public async Task<bool> ReactivateIfOverdueSuspendedAsync(int orgId)
    {
        using var conn = _db.Create();
        var updated = await conn.ExecuteAsync(@"
            UPDATE Organizations
            SET IsActive = 1, SuspendedAt = NULL, SuspendedReason = NULL
            WHERE Id = @orgId AND IsActive = 0 AND SuspendedReason = @reason",
            new { orgId, reason = SuspensionReasons.Overdue });
        return updated > 0;
    }

    public async Task SetAgentRestrictedAsync(int orgId, bool restricted, string? reason)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE Organizations
            SET AgentRestricted = @restricted,
                AgentRestrictedAt = CASE WHEN @restricted = 1 THEN ISNULL(AgentRestrictedAt, GETUTCDATE()) ELSE NULL END,
                AgentRestrictedReason = CASE WHEN @restricted = 1 THEN @reason ELSE NULL END
            WHERE Id = @orgId", new { orgId, restricted, reason });
    }

    // ---------- webhook idempotency ----------

    public async Task<bool> TryClaimWebhookEventAsync(string eventId, string type)
    {
        using var conn = _db.Create();
        try
        {
            var inserted = await conn.ExecuteAsync(@"
                IF NOT EXISTS (SELECT 1 FROM StripeWebhookEvents WHERE Id = @eventId)
                INSERT INTO StripeWebhookEvents (Id, Type) VALUES (@eventId, @type);",
                new { eventId, type });
            return inserted > 0;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
        {
            return false;
        }
    }

    public async Task MarkWebhookDoneAsync(string eventId, string? error)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(
            "UPDATE StripeWebhookEvents SET ProcessedAt = GETUTCDATE(), Error = @error WHERE Id = @eventId",
            new { eventId, error = error?.Length > 1000 ? error[..1000] : error });
    }
}
