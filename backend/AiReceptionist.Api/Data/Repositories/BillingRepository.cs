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
    /// <summary>
    /// Minutes run up but not yet charged for — what the next close will bill. This is the live
    /// figure every screen shows, and it is read from the same rows the close claims, so the number
    /// a customer watches is by construction the number they are billed.
    ///
    /// <paramref name="startedOnOrAfter"/> is for accounts with no plan, where nothing has ever
    /// been closed and "unbilled" would otherwise mean every call the account has ever made — shown
    /// against a window one month long. There the figure is informational, so it is bounded to the
    /// window on screen. An account on a plan passes null and gets the exact billable total.
    /// </summary>
    Task<int> UnbilledMinutesAsync(int orgId, DateTime? startedOnOrAfter = null);

    /// <summary>
    /// Marks every unbilled call that started before <paramref name="periodEndUtc"/> as belonging to
    /// the period closing there, and returns the minutes claimed.
    ///
    /// Safe to call twice — the second call claims nothing and returns the same total — which is
    /// what lets the period close be driven by a worker and two Stripe webhooks at once. Calls
    /// recorded after their own period closed carry no mark, so the next close sweeps them up
    /// instead of losing them.
    /// </summary>
    Task<int> ClaimMinutesForPeriodAsync(int orgId, DateTime periodEndUtc);

    /// <summary>End of the most recently closed period — the anchor the next usage window runs
    /// from. Null before any period has closed.</summary>
    Task<DateTime?> LastClosedPeriodEndAsync(int orgId);

    /// <summary>Files a closed period. Returns false if one was already filed for this period end,
    /// which is how the worker and the two webhooks can all attempt the same close safely.</summary>
    Task<bool> TryAddUsagePeriodAsync(UsagePeriod period);

    Task<IReadOnlyList<UsagePeriod>> ListUsagePeriodsAsync(int orgId, int take = 24);

    /// <summary>Adds a closed period's overrun to what the next invoice will carry.</summary>
    Task AddPendingOverageAsync(int orgId, int minutes, decimal amount);

    /// <summary>
    /// Called once one closed period's overrun has reached a Stripe invoice: marks that period
    /// billed and takes its minutes and money off the carry-over.
    ///
    /// One period at a time, not the whole carry-over at once, because each is charged by its own
    /// idempotent request — settling them together would mean a failure half way through either
    /// losing the charges already made or repeating them. Only a period still Pending is counted
    /// off, so running this twice for the same period cannot drive the carry-over negative.
    /// </summary>
    Task SettleUsagePeriodAsync(int usagePeriodId, string stripeInvoiceId);

    // ---------- invoices ----------
    Task UpsertInvoiceAsync(OrganizationInvoice invoice, IReadOnlyList<OrganizationInvoiceLine> lines);
    Task<IReadOnlyList<OrganizationInvoice>> ListInvoicesAsync(int orgId, int take = 24);

    // ---------- payments ----------
    /// <summary>Records a Stripe-collected payment. Returns false when this invoice has already
    /// been recorded — Stripe redelivers webhooks, and a customer must not be credited twice.</summary>
    Task<bool> TryAddStripePaymentAsync(PaymentRecord payment);

    /// <summary>Fills in the charge, payment intent and receipt behind a payment already recorded.
    /// Separate from the insert because these are read from Stripe in a second call, and a payment
    /// must never fail to be recorded because the enrichment did.</summary>
    Task AttachPaymentReferencesAsync(string stripeInvoiceId, string? paymentIntentId,
        string? chargeId, string? receiptUrl);

    /// <summary>Records money given back, against the payment the charge belongs to. Returns false
    /// when no payment here matches — a refund for something collected outside this platform.</summary>
    Task<bool> RecordRefundAsync(string paymentIntentId, decimal amountRefunded, DateTime refundedAt);

    // ---------- payment attempts ----------
    /// <summary>Opens the record of an attempt, before the customer leaves for Stripe. Doing it
    /// first is the whole point: an attempt that never comes back is the one worth having a row
    /// for. Safe to call twice for one session — the second call changes nothing.</summary>
    Task StartPaymentAttemptAsync(PaymentAttempt attempt);

    /// <summary>Closes an attempt with what became of it. Only ever moves one that is still open,
    /// so a late webhook cannot overwrite the outcome an earlier one already recorded.</summary>
    Task SettlePaymentAttemptAsync(string stripeSessionId, string status, string? outcome,
        string? stripeSubscriptionId = null, string? stripeInvoiceId = null,
        string? stripePaymentIntentId = null);

    /// <summary>Closes the attempt this organization most recently left open. Stripe's
    /// payment-failure events name an invoice, not the Checkout session that started it, so this is
    /// how a first payment that was refused gets attached to the attempt that made it.</summary>
    Task<bool> SettleLatestOpenAttemptAsync(int orgId, string status, string? outcome,
        string? stripeInvoiceId, TimeSpan within);

    Task<IReadOnlyList<PaymentAttempt>> ListPaymentAttemptsAsync(int orgId, int take = 20);

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

    /// <summary>
    /// Sets the moment an organization's free trial runs out. Passing a time in the past ends a
    /// trial there and then; the row is kept rather than cleared, so the account reads as "tried
    /// and lapsed" instead of "never had one".
    ///
    /// The start date is only re-dated when no trial is currently running, so extending one keeps
    /// the day it began and re-granting after it lapsed starts a fresh one.
    /// </summary>
    Task SetTrialAsync(int orgId, DateTime endsAtUtc);

    // ---------- webhook idempotency ----------
    /// <summary>
    /// Claims a Stripe event id for processing. False means it has already been applied
    /// successfully, or has failed so many times that it has been set aside, and must be skipped.
    ///
    /// A previous attempt that failed or was abandoned mid-flight is re-claimable — that is the
    /// whole point. Idempotency here has to mean "do not apply twice", not "do not try twice",
    /// because the second reading throws away money the platform has already collected.
    /// </summary>
    Task<WebhookClaim> TryClaimWebhookEventAsync(string eventId, string type, int maxAttempts,
        int staleMinutes);

    /// <summary>Marks the claim finished. A non-null <paramref name="error"/> leaves the event
    /// eligible for another attempt.</summary>
    Task MarkWebhookDoneAsync(string eventId, string? error);

    /// <summary>Events that never completed — the operator's "what is stuck" view.</summary>
    Task<IReadOnlyList<StuckWebhookEvent>> ListUnfinishedWebhookEventsAsync(int take = 50);
}

/// <summary>The outcome of asking for a claim on a Stripe event.</summary>
public enum WebhookClaim
{
    /// <summary>Nobody has applied this event; the caller owns it.</summary>
    Granted,
    /// <summary>Applied successfully already. Acknowledge and do nothing.</summary>
    AlreadyApplied,
    /// <summary>Failed too many times. Set aside for a human rather than retried forever.</summary>
    Abandoned,
}

/// <summary>A webhook delivery that never finished cleanly.</summary>
public class StuckWebhookEvent
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public int Attempts { get; set; }
    public bool Abandoned { get; set; }
    public string? Error { get; set; }
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
        // Upsert, because an organization that has never been on a plan has no row yet and this is
        // the first thing that needs to be written about it. An UPDATE alone silently did nothing
        // there, which left a customer who was about to pay unfindable by their Stripe customer id
        // — so the invoice.paid that followed could not be matched to them.
        //
        // The row it creates carries no plan (see SubscriptionRecord.HasPlan): it is a place to
        // hold the linkage until Checkout completes and the real tier is applied over it.
        await conn.ExecuteAsync(@"
            UPDATE OrganizationSubscriptions
            SET StripeCustomerId = @stripeCustomerId, ModifiedAt = GETUTCDATE()
            WHERE OrganizationId = @orgId;

            IF @@ROWCOUNT = 0
            INSERT INTO OrganizationSubscriptions
                (OrganizationId, PlanName, BillingCycle, Amount, Currency, StartedAt,
                 CurrentPeriodStart, CurrentPeriodEnd, IncludedMinutes, OverageRatePerMinute,
                 StripeCustomerId, ModifiedAt)
            SELECT @orgId, 'No plan', @cycle, 0, ISNULL(o.Currency, 'USD'), GETUTCDATE(),
                   GETUTCDATE(), GETUTCDATE(), 0, 0, @stripeCustomerId, GETUTCDATE()
            FROM Organizations o WHERE o.Id = @orgId;",
            new { orgId, stripeCustomerId, cycle = BillingCycles.Monthly });
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

    public async Task<int> UnbilledMinutesAsync(int orgId, DateTime? startedOnOrAfter = null)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>($@"
            SELECT {BillingSchema.MinutesExpression} FROM CallLogs
            WHERE OrganizationId = @orgId AND {BillingSchema.UnbilledPredicate}
              AND (@startedOnOrAfter IS NULL OR StartedAt >= @startedOnOrAfter)",
            new { orgId, startedOnOrAfter });
    }

    public async Task<int> ClaimMinutesForPeriodAsync(int orgId, DateTime periodEndUtc)
    {
        using var conn = _db.Create();
        conn.Open();
        using var tx = conn.BeginTransaction();

        // Claim first, total second, both inside one transaction. The other order leaves a window
        // in which a call recorded between the two is stamped as billed without ever being added
        // up — a minute the customer used, marked paid for, and charged to nobody.
        await conn.ExecuteAsync(@"
            UPDATE CallLogs SET BilledPeriodEnd = @periodEndUtc
            WHERE OrganizationId = @orgId
              AND IsDeleted = 0 AND BilledPeriodEnd IS NULL
              AND StartedAt < @periodEndUtc",
            new { orgId, periodEndUtc }, tx);

        // Reads back what is stamped rather than reusing the update's row count, which makes this
        // safe to run again: a close interrupted after claiming but before the period row was
        // filed re-runs, claims nothing new, and still returns the total it was going to file.
        var minutes = await conn.ExecuteScalarAsync<int>($@"
            SELECT {BillingSchema.MinutesExpression} FROM CallLogs
            WHERE OrganizationId = @orgId AND IsDeleted = 0
              AND BilledPeriodEnd = @periodEndUtc",
            new { orgId, periodEndUtc }, tx);

        tx.Commit();
        return minutes;
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

    public async Task SettleUsagePeriodAsync(int usagePeriodId, string stripeInvoiceId)
    {
        using var conn = _db.Create();

        // The UPDATE is its own guard: it only matches a period still Pending, so @@ROWCOUNT is 0
        // on a repeat and the carry-over below is left alone. Without that, a redelivered webhook
        // would subtract the same overrun again and leave the account showing a credit it has not
        // earned.
        await conn.ExecuteAsync($@"
            UPDATE OrganizationUsagePeriods
            SET Status = '{UsagePeriodStatus.Billed}', StripeInvoiceId = @stripeInvoiceId
            WHERE Id = @usagePeriodId AND Status = '{UsagePeriodStatus.Pending}';

            IF @@ROWCOUNT > 0
            UPDATE s
            SET PendingOverageMinutes =
                    CASE WHEN s.PendingOverageMinutes < p.OverageMinutes
                         THEN 0 ELSE s.PendingOverageMinutes - p.OverageMinutes END,
                PendingOverageAmount =
                    CASE WHEN s.PendingOverageAmount < p.OverageAmount
                         THEN 0 ELSE s.PendingOverageAmount - p.OverageAmount END,
                ModifiedAt = GETUTCDATE()
            FROM OrganizationSubscriptions s
            JOIN OrganizationUsagePeriods p ON p.OrganizationId = s.OrganizationId
            WHERE p.Id = @usagePeriodId;",
            new { usagePeriodId, stripeInvoiceId });
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

    public async Task AttachPaymentReferencesAsync(string stripeInvoiceId, string? paymentIntentId,
        string? chargeId, string? receiptUrl)
    {
        using var conn = _db.Create();
        // COALESCE, so a later delivery carrying less detail than an earlier one cannot blank out a
        // reference already recorded.
        await conn.ExecuteAsync(@"
            UPDATE OrganizationPayments
            SET StripePaymentIntentId = COALESCE(@paymentIntentId, StripePaymentIntentId),
                StripeChargeId        = COALESCE(@chargeId, StripeChargeId),
                ReceiptUrl            = COALESCE(@receiptUrl, ReceiptUrl)
            WHERE StripeInvoiceId = @stripeInvoiceId",
            new { stripeInvoiceId, paymentIntentId, chargeId, receiptUrl });
    }

    public async Task<bool> RecordRefundAsync(string paymentIntentId, decimal amountRefunded,
        DateTime refundedAt)
    {
        using var conn = _db.Create();
        // Set, not added to: Stripe reports the running total refunded on the charge, so a second
        // partial refund arrives as the new cumulative figure. Adding would double it.
        var rows = await conn.ExecuteAsync(@"
            UPDATE OrganizationPayments
            SET AmountRefunded = @amountRefunded,
                RefundedAt = CASE WHEN @amountRefunded > 0 THEN @refundedAt ELSE NULL END
            WHERE StripePaymentIntentId = @paymentIntentId",
            new { paymentIntentId, amountRefunded, refundedAt });
        return rows > 0;
    }

    // ---------- payment attempts ----------

    public async Task StartPaymentAttemptAsync(PaymentAttempt a)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            IF NOT EXISTS (SELECT 1 FROM OrganizationPaymentAttempts WHERE StripeSessionId = @StripeSessionId)
            INSERT INTO OrganizationPaymentAttempts
                (OrganizationId, StripeSessionId, PlanId, PlanName, Amount, Currency, Status,
                 StartedByUserId, StartedAt)
            VALUES (@OrganizationId, @StripeSessionId, @PlanId, @PlanName, @Amount, @Currency,
                    @Status, @StartedByUserId, GETUTCDATE());", a);
    }

    public async Task SettlePaymentAttemptAsync(string stripeSessionId, string status, string? outcome,
        string? stripeSubscriptionId = null, string? stripeInvoiceId = null,
        string? stripePaymentIntentId = null)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync($@"
            UPDATE OrganizationPaymentAttempts
            SET Status = @status,
                Outcome = @outcome,
                SettledAt = GETUTCDATE(),
                StripeSubscriptionId  = COALESCE(@stripeSubscriptionId, StripeSubscriptionId),
                StripeInvoiceId       = COALESCE(@stripeInvoiceId, StripeInvoiceId),
                StripePaymentIntentId = COALESCE(@stripePaymentIntentId, StripePaymentIntentId)
            WHERE StripeSessionId = @stripeSessionId
              AND Status = '{PaymentAttemptStatus.Started}';",
            new { stripeSessionId, status, outcome, stripeSubscriptionId, stripeInvoiceId, stripePaymentIntentId });
    }

    public async Task<bool> SettleLatestOpenAttemptAsync(int orgId, string status, string? outcome,
        string? stripeInvoiceId, TimeSpan within)
    {
        using var conn = _db.Create();
        // Bounded by age on purpose. An attempt left open months ago is not what this failure is
        // about, and attaching an unrelated old row to it would be worse than attaching nothing.
        var rows = await conn.ExecuteAsync($@"
            UPDATE OrganizationPaymentAttempts
            SET Status = @status, Outcome = @outcome, SettledAt = GETUTCDATE(),
                StripeInvoiceId = COALESCE(@stripeInvoiceId, StripeInvoiceId)
            WHERE Id = (
                SELECT TOP 1 Id FROM OrganizationPaymentAttempts
                WHERE OrganizationId = @orgId AND Status = '{PaymentAttemptStatus.Started}'
                  AND StartedAt >= @since
                ORDER BY StartedAt DESC);",
            new { orgId, status, outcome, stripeInvoiceId, since = DateTime.UtcNow - within });
        return rows > 0;
    }

    public async Task<IReadOnlyList<PaymentAttempt>> ListPaymentAttemptsAsync(int orgId, int take = 20)
    {
        using var conn = _db.Create();
        var rows = await conn.QueryAsync<PaymentAttempt>(@"
            SELECT TOP (@take) * FROM OrganizationPaymentAttempts
            WHERE OrganizationId = @orgId ORDER BY StartedAt DESC", new { orgId, take });
        return rows.ToList();
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
        // `hadNoPlan` reads the row as it was before this statement — every SET expression in an
        // UPDATE sees the original values — so it identifies the placeholder row that was created
        // to hold a Stripe customer link before Checkout. That row carries no access window, and
        // taking it over is the moment the customer's first period actually begins. Rows that were
        // already on a plan keep the window they have: a tier change must not silently re-date
        // what someone has paid through.
        await conn.ExecuteAsync(@"
            DECLARE @hadNoPlan BIT = (
                SELECT CASE WHEN PlanId IS NULL AND Amount = 0 AND IncludedMinutes = 0 THEN 1 ELSE 0 END
                FROM OrganizationSubscriptions WHERE OrganizationId = @orgId);

            UPDATE OrganizationSubscriptions
            SET PlanId = @planId, PlanName = @planName, BillingCycle = @cycle, Amount = @amount,
                Currency = @currency, IncludedMinutes = @includedMinutes,
                OverageRatePerMinute = @overageRate, ModifiedAt = GETUTCDATE(),
                StartedAt = CASE WHEN @hadNoPlan = 1 THEN GETUTCDATE() ELSE StartedAt END,
                CurrentPeriodStart = CASE WHEN @hadNoPlan = 1 THEN GETUTCDATE() ELSE CurrentPeriodStart END,
                CurrentPeriodEnd = CASE WHEN @hadNoPlan = 1 THEN @firstPeriodEnd ELSE CurrentPeriodEnd END,
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

    public async Task SetTrialAsync(int orgId, DateTime endsAtUtc)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE Organizations
            SET TrialStartedAt = CASE WHEN TrialEndsAt > GETUTCDATE() THEN ISNULL(TrialStartedAt, GETUTCDATE())
                                      ELSE GETUTCDATE() END,
                TrialEndsAt = @endsAtUtc
            WHERE Id = @orgId", new { orgId, endsAtUtc });
    }

    // ---------- webhook idempotency ----------

    public async Task<WebhookClaim> TryClaimWebhookEventAsync(string eventId, string type,
        int maxAttempts, int staleMinutes)
    {
        using var conn = _db.Create();
        try
        {
            // One statement, so two workers racing the same redelivery cannot both win. The UPDATE
            // takes the row lock; the INSERT only runs when there is no row to lock, and a loser
            // there surfaces as a duplicate-key which the catch below reads as "someone else has it".
            var outcome = await conn.ExecuteScalarAsync<int>(@"
                SET NOCOUNT ON;
                DECLARE @result INT;

                UPDATE StripeWebhookEvents
                SET Attempts = Attempts + 1, ClaimedAt = GETUTCDATE(), ProcessedAt = NULL, Error = NULL
                WHERE Id = @eventId
                  AND Abandoned = 0
                  AND (
                        -- the last attempt failed outright
                        Error IS NOT NULL
                        -- or it was claimed and never finished: the process holding it is gone
                        OR (ProcessedAt IS NULL
                            AND (ClaimedAt IS NULL OR ClaimedAt < DATEADD(minute, -@staleMinutes, GETUTCDATE())))
                      );

                IF @@ROWCOUNT > 0
                BEGIN
                    -- One attempt too many. Set it aside so it stops consuming deliveries, and
                    -- leave it visible: the reconciliation sweep is what recovers the money.
                    UPDATE StripeWebhookEvents SET Abandoned = 1, ProcessedAt = GETUTCDATE()
                    WHERE Id = @eventId AND Attempts > @maxAttempts;

                    SET @result = CASE WHEN @@ROWCOUNT > 0 THEN 2 ELSE 0 END;
                END
                ELSE IF EXISTS (SELECT 1 FROM StripeWebhookEvents WHERE Id = @eventId)
                    SET @result = (SELECT CASE WHEN Abandoned = 1 THEN 2 ELSE 1 END
                                   FROM StripeWebhookEvents WHERE Id = @eventId);
                ELSE
                BEGIN
                    INSERT INTO StripeWebhookEvents (Id, Type, Attempts, ClaimedAt)
                    VALUES (@eventId, @type, 1, GETUTCDATE());
                    SET @result = 0;
                END

                SELECT @result;",
                new { eventId, type, maxAttempts, staleMinutes });

            return outcome switch
            {
                0 => WebhookClaim.Granted,
                2 => WebhookClaim.Abandoned,
                _ => WebhookClaim.AlreadyApplied,
            };
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // Another delivery of the same event inserted first. Theirs is in flight, so this one
            // steps aside rather than running the handler alongside it.
            return WebhookClaim.AlreadyApplied;
        }
    }

    public async Task MarkWebhookDoneAsync(string eventId, string? error)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(
            "UPDATE StripeWebhookEvents SET ProcessedAt = GETUTCDATE(), Error = @error WHERE Id = @eventId",
            new { eventId, error = error?.Length > 1000 ? error[..1000] : error });
    }

    public async Task<IReadOnlyList<StuckWebhookEvent>> ListUnfinishedWebhookEventsAsync(int take = 50)
    {
        using var conn = _db.Create();
        var rows = await conn.QueryAsync<StuckWebhookEvent>(@"
            SELECT TOP (@take) Id, Type, ReceivedAt, ClaimedAt, Attempts, Abandoned, Error
            FROM StripeWebhookEvents
            WHERE Abandoned = 1 OR Error IS NOT NULL OR ProcessedAt IS NULL
            ORDER BY ReceivedAt DESC", new { take });
        return rows.ToList();
    }
}
