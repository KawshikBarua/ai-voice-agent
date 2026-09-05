using AiReceptionist.Api.Data.Repositories;
using System.Globalization;

namespace AiReceptionist.Api.Services;

/// <summary>One line of "here is why you are being charged this".</summary>
public class ChargeLine
{
    public string Label { get; set; } = "";
    /// <summary>The arithmetic, spelled out: "142 min over 500 included × $0.12".</summary>
    public string? Detail { get; set; }
    public decimal Amount { get; set; }
    /// <summary>Subscription | Overage | Other.</summary>
    public string Kind { get; set; } = InvoiceLineKinds.Other;
    /// <summary>False while the period it covers is still running — the amount can still move.</summary>
    public bool IsFinal { get; set; } = true;
}

/// <summary>A closed period as the customer reads it.</summary>
public class PeriodView
{
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public string PlanName { get; set; } = "";
    public int IncludedMinutes { get; set; }
    public int MinutesUsed { get; set; }
    public int OverageMinutes { get; set; }
    public decimal OverageRatePerMinute { get; set; }
    public decimal OverageAmount { get; set; }
    public decimal BaseAmount { get; set; }
    public decimal Total => BaseAmount + OverageAmount;
    public string Currency { get; set; } = "USD";
    public bool Billed { get; set; }
}

public class InvoiceView
{
    public string? Number { get; set; }
    public string Status { get; set; } = "";
    public decimal Total { get; set; }
    /// <summary>Still owed — zero once the invoice is settled. See
    /// <see cref="OrganizationInvoice.AmountOutstanding"/> for why this is not Stripe's amount_due.</summary>
    public decimal AmountOutstanding { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTime? IssuedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
    public string? HostedInvoiceUrl { get; set; }
    public string? InvoicePdfUrl { get; set; }
    public List<ChargeLine> Lines { get; set; } = [];
}

/// <summary>Everything the tenant's billing page shows: what they are on, what they have used,
/// what the next bill is shaping up to be and why, and what they have been charged before.</summary>
public class BillingSummary
{
    public bool HasSubscription { get; set; }
    /// <summary>The catalogue tier the account is on, so the tier list can mark the current one.
    /// Null when the numbers were set by hand rather than from a tier.</summary>
    public int? PlanId { get; set; }
    public string PlanName { get; set; } = "";
    public string BillingCycle { get; set; } = BillingCycles.Monthly;
    public string Currency { get; set; } = "USD";
    public decimal PlanAmount { get; set; }

    public int IncludedMinutes { get; set; }
    public decimal OverageRatePerMinute { get; set; }
    /// <summary>False when the plan does not meter minutes at all: no balance, never any overage.</summary>
    public bool Metered { get; set; }

    // ---- the period now running ----
    public DateTime CurrentPeriodStart { get; set; }
    public DateTime CurrentPeriodEnd { get; set; }
    public int MinutesUsed { get; set; }
    public int MinutesRemaining { get; set; }
    public int MinutesOver { get; set; }
    /// <summary>What the current overrun would cost if the period ended now. Not yet charged.</summary>
    public decimal ProjectedOverageAmount { get; set; }

    // ---- what is already owed but not yet invoiced ----
    public int PendingOverageMinutes { get; set; }
    public decimal PendingOverageAmount { get; set; }

    /// <summary>The plan charge, anything carried over, and the current overrun — the shape of the
    /// next invoice.</summary>
    public List<ChargeLine> UpcomingCharges { get; set; } = [];
    public decimal EstimatedNextInvoice { get; set; }

    public PeriodView? PreviousPeriod { get; set; }
    public List<PeriodView> History { get; set; } = [];
    public List<InvoiceView> Invoices { get; set; } = [];

    // ---- account standing ----
    public DateTime? PaidThrough { get; set; }
    public string? StripeStatus { get; set; }
    /// <summary>Stripe is collecting automatically for this organization.</summary>
    public bool AutoCollecting { get; set; }
    /// <summary>The platform has Stripe connected, so Checkout and the portal can be offered.</summary>
    public bool StripeAvailable { get; set; }
    /// <summary>A tier the customer can subscribe to themselves, if they are not already.</summary>
    public bool CanSubscribe { get; set; }
    /// <summary>Stripe is collecting, so the customer can move between tiers from the next cycle.</summary>
    public bool CanChangePlan { get; set; }
    /// <summary>A tier already queued to take effect at <see cref="CurrentPeriodEnd"/>, if any.</summary>
    public int? PendingPlanId { get; set; }
    public string? PendingPlanName { get; set; }
    public bool AgentRestricted { get; set; }
    public string? AgentRestrictedReason { get; set; }

    /// <summary>A free trial the provider granted is still running: the receptionist is answering
    /// at no charge until <see cref="TrialEndsAt"/>, with or without a plan.</summary>
    public bool OnTrial { get; set; }
    /// <summary>When the trial stops. Null for an account that was never given one.</summary>
    public DateTime? TrialEndsAt { get; set; }
    public int TrialDaysRemaining { get; set; }
    /// <summary>The trial has run out. With no plan behind it the receptionist has stopped
    /// answering, which is the one thing this page must not leave a customer to guess.</summary>
    public bool TrialExpired { get; set; }

    /// <summary>A payment this account started and that has not been confirmed yet.
    ///
    /// This is what a customer whose connection dropped on Stripe's page needs to see. The payment
    /// itself is safe either way — the webhook, their return, and the reconciliation sweep are
    /// three independent ways it lands — but a billing page that simply says "no plan" while their
    /// money is in flight gives them every reason to pay a second time.</summary>
    public PendingPaymentView? PendingPayment { get; set; }
}

/// <summary>A payment the customer has started and that has not been confirmed yet, as their
/// billing page should describe it.</summary>
public class PendingPaymentView
{
    public string PlanName { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTime StartedAt { get; set; }

    /// <summary>True once it has sat unconfirmed long enough that something has probably gone
    /// wrong — a webhook that never arrived, or a customer who closed the tab at the card form.
    /// The page says something different in each case, and neither should be alarming.</summary>
    public bool IsStale { get; set; }
}

/// <summary>
/// Minutes as they stand right now.
///
/// Deliberately small and cheap: it is polled while the page is open, so unlike
/// <see cref="BillingSummary"/> it closes no periods, raises no Stripe calls and reads no invoice
/// history. It is measured over the same <see cref="UsageWindows"/> window the bill is computed
/// from, so the balance a customer watches during a call is the one they are charged against.
/// </summary>
public class UsageSnapshot
{
    public bool HasSubscription { get; set; }
    public string PlanName { get; set; } = "";
    public string Currency { get; set; } = "USD";
    /// <summary>False when the plan does not meter minutes: there is no balance to run down.</summary>
    public bool Metered { get; set; }
    public int IncludedMinutes { get; set; }
    public int MinutesUsed { get; set; }
    public int MinutesRemaining { get; set; }
    public int MinutesOver { get; set; }
    public decimal OverageRatePerMinute { get; set; }
    public decimal ProjectedOverageAmount { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    /// <summary>When the server read these numbers, so the page can say how fresh they are.</summary>
    public DateTime AsOf { get; set; } = DateTime.UtcNow;
}

/// <summary>The outcome of applying a Checkout session. <see cref="Problem"/> is written for a
/// customer to read, so it says what happened rather than naming an internal state.</summary>
public record CheckoutOutcome(bool Applied, int? OrganizationId, string? Problem);

/// <summary>
/// One closed period's overrun, owed but not yet on any invoice — a charge that has to ride along
/// with the plan price wherever the platform asks Stripe for money.
///
/// It carries the period it came from, not just an amount: that id is what makes the charge
/// idempotent upstream and what is settled locally once it has been collected, so the same minutes
/// can never be billed twice.
/// </summary>
public record CarriedCharge(int PeriodId, decimal Amount, string Currency, string Description);

/// <summary>What a reconciliation pass found.</summary>
public class ReconciliationResult
{
    /// <summary>Paid invoices Stripe returned for the window.</summary>
    public int Examined { get; set; }
    /// <summary>Payments recorded that the webhook had never landed — the ones this recovered.</summary>
    public int Recovered { get; set; }
    /// <summary>Invoices that could not be applied, with the reason.</summary>
    public List<string> Failed { get; set; } = [];
    /// <summary>False when Stripe is not connected, so the caller can say so rather than
    /// reporting a clean run over nothing.</summary>
    public bool StripeConfigured { get; set; } = true;
}

public interface IBillingService
{
    /// <summary>Totals and files every usage window that has fully elapsed, adding any overrun to
    /// what the next invoice will carry. Safe to call repeatedly and from several places at once.</summary>
    Task<int> CloseElapsedPeriodsAsync(int orgId, CancellationToken ct = default);

    Task<int> CloseAllElapsedPeriodsAsync(CancellationToken ct = default);

    Task<BillingSummary> GetSummaryAsync(int orgId, CancellationToken ct = default);

    /// <summary>Just the minutes, cheaply enough to be polled while a page is open.</summary>
    Task<UsageSnapshot> GetUsageAsync(int orgId, CancellationToken ct = default);

    /// <summary>
    /// Puts an organization on the tier a completed Checkout session paid for, and records the
    /// Stripe linkage.
    ///
    /// Both the webhook and the customer's own return from Stripe come through here, so a purchase
    /// lands exactly once and identically whichever arrives first. <paramref name="expectedOrgId"/>
    /// is set when the caller is a signed-in tenant: a session id travels in a URL, and it must
    /// never be usable to apply a purchase to somebody else's account.
    /// </summary>
    Task<CheckoutOutcome> ApplyCheckoutSessionAsync(Stripe.Checkout.Session session,
        int? expectedOrgId, CancellationToken ct = default);

    /// <summary>Puts the carried-over overage onto a Stripe invoice as its own line, then clears
    /// the carry-over. Called when Stripe raises the next invoice.</summary>
    Task<bool> AttachPendingOverageAsync(int orgId, string? stripeInvoiceId, CancellationToken ct = default);

    /// <summary>
    /// What this organization owes on top of its plan: one entry per closed period whose overrun
    /// has not reached an invoice yet, worded exactly as it will read on the bill.
    ///
    /// Every route to Stripe asks for this — the invoice an operator sends, the subscription they
    /// start, and the Checkout page a customer pays on — so that what is collected is the plan
    /// price plus everything already carried over, whichever way the money is taken. Elapsed
    /// periods are closed first, so a customer about to pay is charged for every period that has
    /// actually finished rather than whatever was last swept.
    /// </summary>
    Task<IReadOnlyList<CarriedCharge>> ListCarriedChargesAsync(int orgId, CancellationToken ct = default);

    /// <summary>Copies a Stripe invoice and its lines into the local mirror.</summary>
    Task<int?> MirrorInvoiceAsync(Stripe.Invoice invoice, CancellationToken ct = default);

    /// <summary>Replays every invoice Stripe has marked paid in the recent past through the same
    /// path the webhook uses, so a delivery that never arrived is still collected. Returns how many
    /// payments this pass recorded that were not recorded before.</summary>
    Task<ReconciliationResult> ReconcileAsync(int lookbackDays, CancellationToken ct = default);

    /// <summary>Records the payment, moves the access period on, and lifts an automatic
    /// suspension. Idempotent: a redelivered webhook changes nothing. True when this call is what
    /// recorded the payment, so reconciliation can report what it actually recovered.</summary>
    Task<bool> HandleInvoicePaidAsync(Stripe.Invoice invoice, CancellationToken ct = default);

    /// <summary>Closes the attempt behind a Checkout session that expired unpaid. Nothing was
    /// charged; this is only so the trail says what became of it rather than trailing off.</summary>
    Task HandleCheckoutExpiredAsync(Stripe.Checkout.Session session, CancellationToken ct = default);

    /// <summary>Records that Stripe could not collect, against the attempt that was waiting on it.
    /// The subscription itself is left alone — Stripe keeps retrying the card, and the platform's
    /// own grace period is what decides when a late payment becomes an unpaid one.</summary>
    Task HandlePaymentFailedAsync(Stripe.Invoice invoice, CancellationToken ct = default);

    /// <summary>Writes a refund against the payment it reverses, so what the customer was left
    /// paying is what the record shows. The payment row is kept: a payment made and then given
    /// back is two facts, and erasing the first loses the history behind the second.</summary>
    Task HandleRefundAsync(Stripe.Charge charge, CancellationToken ct = default);
}

public class BillingService : IBillingService
{
    private readonly IBillingRepository _billing;
    private readonly ISettingsRepository _settings;
    private readonly IStripeGateway _stripe;
    private readonly ILogger<BillingService> _logger;

    public BillingService(IBillingRepository billing, ISettingsRepository settings,
        IStripeGateway stripe, ILogger<BillingService> logger)
    {
        _billing = billing;
        _settings = settings;
        _stripe = stripe;
        _logger = logger;
    }

    // ---------------------------------------------------------------- closing periods

    public async Task<int> CloseElapsedPeriodsAsync(int orgId, CancellationToken ct = default)
    {
        var sub = await _billing.GetSubscriptionAsync(orgId);
        if (sub is null) return 0;
        return await CloseAsync(sub, ct);
    }

    public async Task<int> CloseAllElapsedPeriodsAsync(CancellationToken ct = default)
    {
        var closed = 0;
        foreach (var sub in await _billing.ListSubscriptionsAsync())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                closed += await CloseAsync(sub, ct);
            }
            catch (Exception ex)
            {
                // One organization's bad data must not stop the rest being billed.
                _logger.LogError(ex, "Could not close billing periods for organization {OrgId}.",
                    sub.OrganizationId);
            }
        }
        return closed;
    }

    private async Task<int> CloseAsync(SubscriptionRecord sub, CancellationToken ct)
    {
        var anchor = await _billing.LastClosedPeriodEndAsync(sub.OrganizationId)
            ?? sub.CurrentPeriodStart;

        var closed = 0;
        foreach (var (start, end) in UsageWindows.Elapsed(anchor, sub.BillingCycle, DateTime.UtcNow))
        {
            ct.ThrowIfCancellationRequested();

            // Claims the calls as it counts them, so a call recorded after this period closed is
            // carried into the next close rather than falling down the gap between the two.
            var used = await _billing.ClaimMinutesForPeriodAsync(sub.OrganizationId, end);

            // An unmetered plan (no included minutes) never overruns — it is priced on the flat
            // fee alone, so the period is filed for the record with nothing to charge.
            var overMinutes = sub.IsMetered ? Math.Max(0, used - sub.IncludedMinutes) : 0;
            var overAmount = StripeMoney.Round(overMinutes * sub.OverageRatePerMinute, sub.Currency);

            var period = new UsagePeriod
            {
                OrganizationId = sub.OrganizationId,
                PeriodStart = start,
                PeriodEnd = end,
                PlanName = sub.PlanName,
                BaseAmount = sub.Amount,
                Currency = sub.Currency,
                IncludedMinutes = sub.IncludedMinutes,
                MinutesUsed = used,
                OverageMinutes = overMinutes,
                OverageRatePerMinute = sub.OverageRatePerMinute,
                OverageAmount = overAmount,
                // Nothing to collect closes as settled immediately; only a real charge waits for
                // an invoice to attach itself to.
                Status = overAmount > 0 ? UsagePeriodStatus.Pending : UsagePeriodStatus.Billed,
            };

            if (!await _billing.TryAddUsagePeriodAsync(period))
                continue;   // another caller filed this period first

            closed++;

            if (overAmount > 0)
            {
                await _billing.AddPendingOverageAsync(sub.OrganizationId, overMinutes, overAmount);
                _logger.LogInformation(
                    "Organization {OrgId} used {Used} of {Included} minutes in the period ending {End:d}: " +
                    "{Over} minute(s) over, {Amount} {Currency} carried to the next invoice.",
                    sub.OrganizationId, used, sub.IncludedMinutes, end, overMinutes, overAmount, sub.Currency);
            }
        }

        // A tier the customer chose mid-period takes effect only now, with every period it was
        // waiting behind closed and measured against the allowance they actually held at the time.
        // Nothing closed means the period is still running, so the change stays parked.
        if (closed > 0 && sub.PendingPlanId is > 0)
            await ApplyPendingPlanAsync(sub);

        return closed;
    }

    /// <summary>Moves an organization onto the tier it queued, now that the period it chose during
    /// has closed.</summary>
    private async Task ApplyPendingPlanAsync(SubscriptionRecord sub)
    {
        var plan = await _billing.GetPlanAsync(sub.PendingPlanId!.Value);

        if (plan is null || !plan.IsActive)
        {
            // Withdrawn from the catalogue while the customer was waiting for it. Drop the request
            // rather than moving them onto something no longer offered, and leave them where they
            // are — which is a tier they have been paying for and understand.
            await _billing.SetPendingPlanAsync(sub.OrganizationId, null);
            _logger.LogWarning(
                "Organization {OrgId} was queued to move to tier {PlanId}, which is no longer available. " +
                "It stays on {Current}.",
                sub.OrganizationId, sub.PendingPlanId, sub.PlanName);
            return;
        }

        await _billing.ApplyPlanAsync(sub.OrganizationId, plan, null, null, null);
        _logger.LogInformation(
            "Organization {OrgId} moved from {Old} to {New} at the period boundary: {Minutes} minutes " +
            "included, {Rate} {Currency} per extra minute.",
            sub.OrganizationId, sub.PlanName, plan.Name, plan.IncludedMinutes,
            plan.OverageRatePerMinute, plan.Currency);
    }

    // ---------------------------------------------------------------- charging the overrun

    public async Task<IReadOnlyList<CarriedCharge>> ListCarriedChargesAsync(int orgId,
        CancellationToken ct = default)
    {
        // Catch up first: a bill about to be raised marks the end of a period, and that period's
        // overrun belongs on it rather than waiting another whole cycle.
        await CloseElapsedPeriodsAsync(orgId, ct);

        return (await _billing.ListUsagePeriodsAsync(orgId))
            .Where(p => !p.IsBilled && p.OverageAmount > 0)
            .OrderBy(p => p.PeriodStart)
            .Select(p => new CarriedCharge(p.Id, p.OverageAmount, p.Currency, DescribeOverage(p)))
            .ToList();
    }

    public async Task<bool> AttachPendingOverageAsync(int orgId, string? stripeInvoiceId,
        CancellationToken ct = default)
    {
        var carried = await ListCarriedChargesAsync(orgId, ct);

        var sub = await _billing.GetSubscriptionAsync(orgId);
        if (sub is null || sub.PendingOverageAmount <= 0) return false;

        if (!_stripe.IsConfigured || string.IsNullOrWhiteSpace(sub.StripeCustomerId))
        {
            // Nothing is lost: the carry-over stays pending and the console shows it as due, so a
            // manually billed customer is still charged — just not by Stripe.
            _logger.LogInformation(
                "Organization {OrgId} has {Amount} {Currency} of overage pending but no Stripe customer; " +
                "it stays on the account for manual billing.",
                orgId, sub.PendingOverageAmount, sub.Currency);
            return false;
        }

        // One invoice item per closed period, rather than a single merged line for the whole
        // carry-over.
        //
        // The merged line could not be made safe to retry. Its idempotency key would have to be
        // derived from the total, and the total legitimately changes the moment another period
        // closes — so an attempt whose reply was lost, followed by an attempt for a larger sum,
        // would carry a different key and charge the customer for the first set of minutes twice.
        // A period's id never changes, so keying on it is exact and permanent. It reads better on
        // the invoice too: each line names the dates it covers.
        var charged = false;

        foreach (var charge in carried)
        {
            await _stripe.AddInvoiceItemAsync(sub.StripeCustomerId, stripeInvoiceId,
                charge.Amount, charge.Currency, charge.Description,
                idempotencyKey: OverageIdempotencyKey(charge.PeriodId), ct);

            // Settled one at a time, immediately after Stripe accepts each item. A failure part-way
            // through leaves the periods already charged marked as such and the rest still pending,
            // so the next attempt picks up exactly where this one stopped — it neither loses a
            // charge nor repeats one.
            await _billing.SettleUsagePeriodAsync(charge.PeriodId, stripeInvoiceId ?? "");
            charged = true;

            _logger.LogInformation(
                "Charged organization {OrgId} {Amount} {Currency} on invoice {InvoiceId} — {Detail}",
                orgId, charge.Amount, charge.Currency, stripeInvoiceId ?? "(next invoice)",
                charge.Description);
        }

        if (!charged)
            // The carry-over says something is owed but no closed period accounts for it. Zeroing it
            // silently would write off money; leaving it alone keeps the console showing it as due.
            _logger.LogError(
                "Organization {OrgId} carries {Amount} {Currency} of pending overage that no closed " +
                "period accounts for. It has not been charged — this needs a human.",
                orgId, sub.PendingOverageAmount, sub.Currency);

        return charged;
    }

    /// <summary>The sentence the customer reads on their invoice. It carries the whole
    /// justification — how many minutes, over what allowance, at what rate, for which dates — so
    /// the charge needs no other explanation to make sense.</summary>
    /// <summary>Keyed on the period rather than the amount: a period's id never changes, so a
    /// retry after a lost reply charges the same minutes once, whatever else has closed since.</summary>
    private static string OverageIdempotencyKey(int periodId) => $"overage-period-{periodId}";

    private static string DescribeOverage(UsagePeriod period) =>
        $"{OverageCharge.DescriptionPrefix}: {period.OverageMinutes:N0} min beyond the " +
        $"{period.IncludedMinutes:N0} included, at " +
        $"{period.OverageRatePerMinute.ToString("0.####", CultureInfo.InvariantCulture)} " +
        $"{period.Currency} per minute " +
        $"({period.PeriodStart:d MMM} – {period.PeriodEnd:d MMM yyyy})";

    // ---------------------------------------------------------------- taking up a plan

    public async Task<CheckoutOutcome> ApplyCheckoutSessionAsync(Stripe.Checkout.Session session,
        int? expectedOrgId, CancellationToken ct = default)
    {
        // ClientReferenceId is written when the session is created, so it answers even before
        // anything here points at Stripe.
        var orgId = int.TryParse(session.ClientReferenceId, out var fromReference)
            ? fromReference
            : ReadOrganizationId(session.Metadata) ?? await ResolveByCustomerAsync(session.CustomerId);

        if (orgId is null)
        {
            _logger.LogWarning(
                "Checkout session {SessionId} completed but could not be matched to an organization.",
                session.Id);
            return new CheckoutOutcome(false, null, "This payment could not be matched to your account.");
        }

        // A session id is not a credential. Applying one account's purchase to another because it
        // was pasted into the URL would be a real hole, so the tenant asking has to own it.
        if (expectedOrgId is not null && orgId != expectedOrgId)
        {
            _logger.LogWarning(
                "Organization {OrgId} tried to confirm Checkout session {SessionId}, which belongs to {OwnerId}.",
                expectedOrgId, session.Id, orgId);
            return new CheckoutOutcome(false, null, "This payment belongs to a different account.");
        }

        // Only once Stripe has actually collected. A session abandoned at the card form is
        // "complete" in no sense that should hand anyone a plan. A subscription that starts on a
        // trial legitimately needs no payment, and does count.
        var collected =
            string.Equals(session.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(session.PaymentStatus, "no_payment_required", StringComparison.OrdinalIgnoreCase);

        if (!collected)
            return new CheckoutOutcome(false, orgId, "Your payment has not completed yet.");

        // Closes the row opened when the customer was sent to Stripe. Only ever moves an attempt
        // still marked Started, so a redelivery lands on nothing.
        await _billing.SettlePaymentAttemptAsync(session.Id, PaymentAttemptStatus.Paid,
            $"Stripe collected the payment ({session.PaymentStatus}).",
            stripeSubscriptionId: session.SubscriptionId,
            stripePaymentIntentId: session.PaymentIntentId);

        var existing = await _billing.GetSubscriptionAsync(orgId.Value);

        // Already done — by the webhook, or by an earlier press of the same button. Re-applying the
        // tier named on an old session would quietly move a customer back off a plan they have
        // since changed to, so this is where a repeat stops.
        var alreadyApplied = existing is { } s && s.HasPlan &&
            string.Equals(s.StripeSubscriptionId, session.SubscriptionId, StringComparison.Ordinal);

        if (!alreadyApplied && ReadPlanId(session.Metadata) is { } planId)
        {
            var plan = await _billing.GetPlanAsync(planId);
            if (plan is null)
                // The money is collected either way, so this is not a failure to report back to the
                // customer — but an operator needs to see it, because nobody is on a tier.
                _logger.LogError(
                    "Checkout session {SessionId} paid for tier {PlanId}, which no longer exists. " +
                    "Organization {OrgId} has been charged and is on no plan.",
                    session.Id, planId, orgId);
            else
                await _billing.ApplyPlanAsync(orgId.Value, plan, null, null, null);
        }

        // The overrun the customer has just paid for alongside the plan. Settling it here is what
        // stops the next invoice charging for the same minutes again; the ids were written into the
        // session, so this clears exactly what was collected and nothing that has closed since.
        // Only a period still pending is counted off, so a redelivery settles nothing twice.
        foreach (var periodId in ReadOveragePeriodIds(session.Metadata))
            await _billing.SettleUsagePeriodAsync(periodId, session.InvoiceId ?? "");

        if (!string.IsNullOrWhiteSpace(session.CustomerId))
            await _billing.SetStripeCustomerAsync(orgId.Value, session.CustomerId);

        if (!string.IsNullOrWhiteSpace(session.SubscriptionId))
            await _billing.SetStripeSubscriptionAsync(orgId.Value, session.SubscriptionId, "active");

        if (!alreadyApplied)
            _logger.LogInformation(
                "Organization {OrgId} subscribed through Stripe Checkout (customer {CustomerId}).",
                orgId, session.CustomerId);

        return new CheckoutOutcome(true, orgId, null);
    }

    private static int? ReadOrganizationId(IDictionary<string, string>? metadata) =>
        metadata is not null && metadata.TryGetValue("organizationId", out var raw) &&
        int.TryParse(raw, out var id) ? id : null;

    /// <summary>The closed periods whose overrun was charged on the Checkout page, as written into
    /// the session when it was created. Empty for a session that carried none.</summary>
    private static IEnumerable<int> ReadOveragePeriodIds(IDictionary<string, string>? metadata) =>
        metadata is not null && metadata.TryGetValue("overagePeriodIds", out var raw)
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(part => int.TryParse(part, out var id) ? id : 0)
                 .Where(id => id > 0)
            : [];

    private static int? ReadPlanId(IDictionary<string, string>? metadata) =>
        metadata is not null && metadata.TryGetValue("planId", out var raw) &&
        int.TryParse(raw, out var id) ? id : null;

    private async Task<int?> ResolveByCustomerAsync(string? stripeCustomerId) =>
        string.IsNullOrWhiteSpace(stripeCustomerId)
            ? null
            : (await _billing.FindByStripeCustomerAsync(stripeCustomerId))?.OrganizationId;

    // ---------------------------------------------------------------- mirroring Stripe

    public async Task<int?> MirrorInvoiceAsync(Stripe.Invoice invoice, CancellationToken ct = default)
    {
        var orgId = await ResolveOrganizationAsync(invoice, ct);
        if (orgId is null)
        {
            _logger.LogWarning(
                "Stripe invoice {InvoiceId} is for customer {CustomerId}, which is not linked to any " +
                "organization here. Ignoring it.", invoice.Id, invoice.CustomerId);
            return null;
        }

        var currency = (invoice.Currency ?? "usd").ToUpperInvariant();

        var lines = (invoice.Lines?.Data ?? [])
            .Select((l, i) => new OrganizationInvoiceLine
            {
                Description = l.Description ?? "",
                Quantity = (int)Math.Max(1, l.Quantity ?? 1),
                Amount = StripeMoney.FromMinorUnits(l.Amount, currency),
                // The overage line is the one this platform wrote, and it is recognised by the
                // wording it was written with; everything else on a subscription invoice is the
                // plan itself.
                Kind = (l.Description ?? "").StartsWith(OverageCharge.DescriptionPrefix, StringComparison.OrdinalIgnoreCase)
                    ? InvoiceLineKinds.Overage
                    : InvoiceLineKinds.Subscription,
                SortOrder = i,
            })
            .ToList();

        await _billing.UpsertInvoiceAsync(new OrganizationInvoice
        {
            OrganizationId = orgId.Value,
            StripeInvoiceId = invoice.Id,
            Number = invoice.Number,
            Status = invoice.Status ?? "draft",
            Currency = currency,
            Subtotal = StripeMoney.FromMinorUnits(invoice.Subtotal, currency),
            Total = StripeMoney.FromMinorUnits(invoice.Total, currency),
            AmountPaid = StripeMoney.FromMinorUnits(invoice.AmountPaid, currency),
            AmountDue = StripeMoney.FromMinorUnits(invoice.AmountDue, currency),
            PeriodStart = invoice.PeriodStart,
            PeriodEnd = invoice.PeriodEnd,
            HostedInvoiceUrl = invoice.HostedInvoiceUrl,
            InvoicePdfUrl = invoice.InvoicePdf,
            IssuedAt = invoice.Created,
            PaidAt = invoice.StatusTransitions?.PaidAt,
        }, lines);

        return orgId;
    }

    public async Task<bool> HandleInvoicePaidAsync(Stripe.Invoice invoice, CancellationToken ct = default)
    {
        var orgId = await MirrorInvoiceAsync(invoice, ct);
        if (orgId is null)
            // Throwing, not returning: Stripe does not order its deliveries, so an invoice.paid
            // routinely lands before the checkout.session.completed that creates the link. Swallowing
            // it silently dropped a payment that would have resolved on the very next attempt.
            throw new InvalidOperationException(
                $"Stripe invoice {invoice.Id} (customer {invoice.CustomerId}) is not linked to any " +
                "organization yet. Retrying — the link may still be arriving.");

        var sub = await _billing.GetSubscriptionAsync(orgId.Value);

        // A first subscription whose Checkout session never reached us. Read the tier off Stripe
        // rather than throwing: the customer has been charged, and refusing the payment for want of
        // a local row would leave them paying for an account with no plan and no minutes on it.
        if (sub is null || !sub.HasPlan)
            sub = await AdoptPlanFromStripeAsync(orgId.Value, invoice, ct) ?? sub;

        if (sub is null)
            throw new InvalidOperationException(
                $"Organization {orgId} has no subscription row to record Stripe invoice {invoice.Id} against.");

        var currency = (invoice.Currency ?? sub.Currency).ToUpperInvariant();
        var amount = StripeMoney.FromMinorUnits(invoice.AmountPaid, currency);

        var recorded = await _billing.TryAddStripePaymentAsync(new PaymentRecord
        {
            OrganizationId = orgId.Value,
            Amount = amount,
            Currency = currency,
            PaidAt = invoice.StatusTransitions?.PaidAt ?? DateTime.UtcNow,
            // The payment is filed against the period that was closing, so a customer's payment
            // record lines up with the periods on their billing page.
            PeriodStart = sub.CurrentPeriodStart,
            PeriodEnd = sub.CurrentPeriodEnd,
            Method = "Card",
            Reference = invoice.Number ?? invoice.Id,
            Source = PaymentSources.Stripe,
            StripeInvoiceId = invoice.Id,
            Notes = null,
        });

        // The charge behind the invoice, attached after the fact. A refund and a chargeback are
        // both raised against the charge rather than the invoice, so without this reference a
        // reversal arriving later cannot be matched to the payment it undoes. Deliberately outside
        // the "was it recorded" branch: an earlier delivery may have recorded the payment before
        // the charge existed to be read.
        if (_stripe.IsConfigured)
        {
            var refs = await _stripe.GetInvoicePaymentRefsAsync(invoice.Id, ct);
            if (refs.PaymentIntentId is not null || refs.ChargeId is not null)
                await _billing.AttachPaymentReferencesAsync(
                    invoice.Id, refs.PaymentIntentId, refs.ChargeId, refs.ReceiptUrl);
        }

        if (!recorded)
        {
            // Stripe redelivers until acknowledged, and the reconciliation sweep re-reads the same
            // invoices on purpose. Everything past this point has already been done for this
            // invoice, and doing it again would push the period forward twice.
            _logger.LogDebug("Stripe invoice {InvoiceId} was already recorded; nothing further to do.",
                invoice.Id);
            return false;
        }

        // Access moves on by exactly one cycle — the same rule the console's manual "record a
        // payment" uses, so an account billed both ways never ends up with two different notions
        // of what it has paid for.
        var (accessStart, accessEnd) = NextAccessWindow(sub, invoice);
        await _billing.AdvanceAccessPeriodAsync(orgId.Value, accessStart, accessEnd);

        if (await _billing.ReactivateIfOverdueSuspendedAsync(orgId.Value))
            _logger.LogInformation(
                "Organization {OrgId} was re-enabled: the overdue subscription has been paid.", orgId);

        _logger.LogInformation("Recorded Stripe payment of {Amount} {Currency} for organization {OrgId}.",
            amount, currency, orgId);
        return true;
    }

    // ---------------------------------------------------------------- when it does not go through

    public async Task HandleCheckoutExpiredAsync(Stripe.Checkout.Session session,
        CancellationToken ct = default)
    {
        await _billing.SettlePaymentAttemptAsync(session.Id, PaymentAttemptStatus.Abandoned,
            "The Checkout session expired before payment was completed. Nothing was charged.");

        _logger.LogInformation(
            "Checkout session {SessionId} expired unpaid. Nothing was charged.", session.Id);
    }

    public async Task HandlePaymentFailedAsync(Stripe.Invoice invoice, CancellationToken ct = default)
    {
        var orgId = await ResolveOrganizationAsync(invoice, ct);
        if (orgId is null) return;

        // Stripe's failure events name an invoice, never the Checkout session that started things,
        // so a first payment that was refused is attached to whichever attempt this organization
        // most recently left open. Bounded by age, so an unrelated abandoned attempt from weeks ago
        // is not mislabelled as this failure.
        var reason = invoice.LastFinalizationError?.Message
            ?? $"Stripe could not collect invoice {invoice.Number ?? invoice.Id}.";

        var attached = await _billing.SettleLatestOpenAttemptAsync(orgId.Value,
            PaymentAttemptStatus.Failed, reason, invoice.Id, TimeSpan.FromDays(2));

        _logger.LogWarning(
            "Stripe could not collect invoice {InvoiceId} for organization {OrgId}: {Reason} " +
            "{Attached}",
            invoice.Id, orgId, reason,
            attached
                ? "It has been recorded against the payment attempt that was waiting on it."
                : "No open payment attempt matched it; this is a renewal rather than a first payment.");
    }

    public async Task HandleRefundAsync(Stripe.Charge charge, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(charge.PaymentIntentId))
        {
            _logger.LogWarning(
                "Stripe charge {ChargeId} was refunded but names no payment intent, so it cannot be " +
                "matched to a payment here.", charge.Id);
            return;
        }

        var currency = (charge.Currency ?? "usd").ToUpperInvariant();
        var refunded = StripeMoney.FromMinorUnits(charge.AmountRefunded, currency);

        // The running total Stripe holds, not this refund's own amount — a second partial refund
        // arrives as the new cumulative figure, and adding them would double it.
        var matched = await _billing.RecordRefundAsync(charge.PaymentIntentId, refunded, DateTime.UtcNow);

        if (!matched)
        {
            // Nothing to correct. Most often a charge collected outside this platform, or one
            // recorded before payment references were being kept.
            _logger.LogWarning(
                "Stripe refunded {Amount} {Currency} on charge {ChargeId}, which matches no payment " +
                "recorded here.", refunded, currency, charge.Id);
            return;
        }

        // Access is deliberately not withdrawn. A refund is issued for many reasons — a goodwill
        // gesture, a billing correction, a duplicate charge — and cutting a customer's phone line
        // off as a side effect of one would be the wrong call to make automatically. The overdue
        // sweep still catches an account that genuinely stops paying.
        _logger.LogWarning(
            "Recorded a refund of {Amount} {Currency} against charge {ChargeId}. Access was left as " +
            "it is; withdraw it from the console if that is what was meant.",
            refunded, currency, charge.Id);
    }

    // ---------------------------------------------------------------- reconciliation

    public async Task<ReconciliationResult> ReconcileAsync(int lookbackDays,
        CancellationToken ct = default)
    {
        var result = new ReconciliationResult();

        if (!_stripe.IsConfigured)
        {
            result.StripeConfigured = false;
            return result;
        }

        // Wider than Stripe's own retry window (roughly three days), so an outage that outlasted
        // every redelivery is still inside the net when the platform comes back.
        var since = DateTime.UtcNow.AddDays(-Math.Max(1, lookbackDays));

        var invoices = await _stripe.ListPaidInvoicesSinceAsync(since, ct);
        result.Examined = invoices.Count;

        foreach (var invoice in invoices)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // The same call the webhook makes. Everything downstream is guarded on the unique
                // StripeInvoiceId, so an invoice already collected costs one no-op and nothing else.
                if (await HandleInvoicePaidAsync(invoice, ct))
                {
                    result.Recovered++;
                    _logger.LogWarning(
                        "Reconciliation recorded Stripe invoice {InvoiceId} ({Number}), which no webhook " +
                        "had applied. Check the webhook endpoint and signing secret.",
                        invoice.Id, invoice.Number);
                }
            }
            catch (Exception ex)
            {
                // One unmatched invoice must not stop the rest being recovered. Most often this is
                // an invoice for a Stripe customer that belongs to a different environment sharing
                // the same Stripe account.
                result.Failed.Add($"{invoice.Id}: {ex.Message}");
                _logger.LogError(ex, "Reconciliation could not apply Stripe invoice {InvoiceId}.", invoice.Id);
            }
        }

        return result;
    }

    /// <summary>
    /// The access window a paid invoice buys.
    ///
    /// One invoice grants one cycle, which is right as long as every invoice is seen. It is not
    /// self-correcting when one is missed: a lost delivery used to leave the account permanently a
    /// cycle behind, and because the overdue sweep disables anything past
    /// <c>GraceDays + CurrentPeriodEnd</c>, a customer who had paid every invoice could still be
    /// suspended for non-payment.
    ///
    /// So Stripe gets the final say. Where the invoice states the period it covers and that period
    /// runs past the cycle-advance, the invoice wins — it cannot grant more access than Stripe
    /// actually billed for, and it closes any gap left by a delivery that never arrived.
    /// </summary>
    private static (DateTime Start, DateTime End) NextAccessWindow(
        SubscriptionRecord sub, Stripe.Invoice invoice)
    {
        var start = sub.CurrentPeriodEnd;
        var end = sub.NextPeriodEnd();

        var billedThrough = invoice.PeriodEnd;
        if (billedThrough > end)
            return (invoice.PeriodStart > DateTime.MinValue && invoice.PeriodStart < billedThrough
                ? invoice.PeriodStart
                : start, billedThrough);

        return (start, end);
    }

    /// <summary>
    /// Whose invoice this is, asked four ways.
    ///
    /// Stripe does not order its deliveries, so an invoice can arrive before anything local points
    /// at the customer it belongs to. Every route is therefore tried before giving up, ending with
    /// the organization id written onto the subscription when Checkout created it — which exists
    /// from the very first moment and needs nothing stored here at all.
    /// </summary>
    private async Task<int?> ResolveOrganizationAsync(Stripe.Invoice invoice, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(invoice.CustomerId) &&
            await _billing.FindByStripeCustomerAsync(invoice.CustomerId) is { } byCustomer)
            return byCustomer.OrganizationId;

        var subscriptionDetails = invoice.Parent?.SubscriptionDetails;

        if (!string.IsNullOrWhiteSpace(subscriptionDetails?.SubscriptionId) &&
            await _billing.FindByStripeSubscriptionAsync(subscriptionDetails.SubscriptionId) is { } bySubscription)
            return bySubscription.OrganizationId;

        // Falls back to the id written onto the Stripe object when it was created, which covers
        // an invoice arriving before the local link has been stored.
        var claimed = ReadOrganizationId(invoice.Metadata)
            ?? ReadOrganizationId(subscriptionDetails?.Metadata);

        // Last: the customer itself. EnsureCustomerAsync stamps every customer it creates with its
        // organization id, so this identifies any payer this platform has ever set up — including
        // one whose local link was never written, which is how an already-collected payment can
        // end up belonging to nobody.
        if (claimed is null && !string.IsNullOrWhiteSpace(invoice.CustomerId) && _stripe.IsConfigured)
            claimed = ReadOrganizationId((await _stripe.GetCustomerAsync(invoice.CustomerId, ct))?.Metadata);

        // Metadata is a claim, not proof: ids from a different environment sharing one Stripe test
        // account resolve to organizations that do not exist here, and following one blindly turns
        // a foreign invoice into a foreign-key error on every sweep, forever.
        if (claimed is not null && await _settings.GetOrganizationAsync(claimed.Value) is null)
        {
            _logger.LogWarning(
                "Stripe invoice {InvoiceId} names organization {OrgId}, which does not exist here. " +
                "It most likely belongs to another environment on the same Stripe account.",
                invoice.Id, claimed);
            return null;
        }

        return claimed;
    }

    /// <summary>
    /// Puts an organization on the tier Stripe is charging it for, when nothing here says what that
    /// is yet.
    ///
    /// This is the safety net under a first subscription. The plan normally arrives with the
    /// Checkout session; if that never lands — no reachable webhook endpoint, or a delivery lost
    /// past every retry — the payment still turns up, and a customer who has paid must not be left
    /// on nothing. The price Stripe is charging is the authority on what they bought.
    /// </summary>
    private async Task<SubscriptionRecord?> AdoptPlanFromStripeAsync(int orgId,
        Stripe.Invoice invoice, CancellationToken ct)
    {
        var subscriptionId = invoice.Parent?.SubscriptionDetails?.SubscriptionId;
        if (string.IsNullOrWhiteSpace(subscriptionId) || !_stripe.IsConfigured) return null;

        var stripeSub = await _stripe.GetSubscriptionAsync(subscriptionId, ct);
        var priceId = stripeSub?.Items?.Data?.FirstOrDefault()?.Price?.Id;
        if (string.IsNullOrWhiteSpace(priceId)) return null;

        var plan = await _billing.FindPlanByStripePriceAsync(priceId);
        if (plan is null)
        {
            _logger.LogError(
                "Organization {OrgId} has paid Stripe invoice {InvoiceId} on price {PriceId}, which " +
                "matches no tier here. Nobody can be put on a plan for it.",
                orgId, invoice.Id, priceId);
            return null;
        }

        await _billing.ApplyPlanAsync(orgId, plan, null, null, null);
        await _billing.SetStripeSubscriptionAsync(orgId, subscriptionId, stripeSub!.Status);

        _logger.LogWarning(
            "Organization {OrgId} was put on tier {Plan} from paid invoice {InvoiceId}: the payment " +
            "arrived before anything recorded what they had bought. Check that " +
            "checkout.session.completed is being delivered.",
            orgId, plan.Name, invoice.Id);

        return await _billing.GetSubscriptionAsync(orgId);
    }

    // ---------------------------------------------------------------- the customer's view

    public async Task<BillingSummary> GetSummaryAsync(int orgId, CancellationToken ct = default)
    {
        // Bring the record up to date first, so a customer opening the page the morning after a
        // period ended sees the closed period rather than a stale open one.
        await CloseElapsedPeriodsAsync(orgId, ct);

        var org = await _settings.GetOrganizationAsync(orgId);
        var sub = await _billing.GetSubscriptionAsync(orgId);

        var summary = new BillingSummary
        {
            StripeAvailable = _stripe.IsConfigured,
            AgentRestricted = org?.AgentRestricted ?? false,
            AgentRestrictedReason = org?.AgentRestrictedReason,
            OnTrial = org?.IsOnTrial ?? false,
            TrialEndsAt = org?.TrialEndsAt,
            TrialDaysRemaining = org?.TrialDaysRemaining ?? 0,
            TrialExpired = org?.TrialExpired ?? false,
            // A row that exists only to hold a Stripe customer link carries a default currency
            // nobody chose, so the organization's own is the honest answer until a tier sets one.
            Currency = (sub is { HasPlan: true } ? sub.Currency : null) ?? org?.Currency ?? "USD",
            PendingPayment = await FindPendingPaymentAsync(orgId),
        };

        if (sub is null || !sub.HasPlan)
        {
            // No plan on file. The page says so rather than inventing a zero-cost subscription —
            // but the tiers are still offered, so a customer can put themselves on one. A row
            // already existing here means only that Checkout has been started once.
            summary.HasSubscription = false;
            summary.CanSubscribe = _stripe.IsConfigured;
            return summary;
        }

        summary.HasSubscription = true;
        summary.PlanId = sub.PlanId;
        summary.PlanName = sub.PlanName;
        summary.BillingCycle = sub.BillingCycle;
        summary.PlanAmount = sub.Amount;
        summary.IncludedMinutes = sub.IncludedMinutes;
        summary.OverageRatePerMinute = sub.OverageRatePerMinute;
        summary.Metered = sub.IsMetered;
        summary.PaidThrough = sub.CurrentPeriodEnd.AddDays(sub.GraceDays);
        summary.StripeStatus = sub.StripeStatus;
        summary.AutoCollecting = sub.IsStripeLinked;
        summary.CanSubscribe = _stripe.IsConfigured && !sub.IsStripeLinked;
        summary.CanChangePlan = _stripe.IsConfigured && sub.IsStripeLinked;
        summary.PendingOverageMinutes = sub.PendingOverageMinutes;
        summary.PendingOverageAmount = sub.PendingOverageAmount;

        if (sub.PendingPlanId is > 0)
        {
            summary.PendingPlanId = sub.PendingPlanId;
            summary.PendingPlanName = (await _billing.GetPlanAsync(sub.PendingPlanId.Value))?.Name;
        }

        var anchor = await _billing.LastClosedPeriodEndAsync(orgId) ?? sub.CurrentPeriodStart;
        var (start, end) = UsageWindows.Current(anchor, sub.BillingCycle, DateTime.UtcNow);

        summary.CurrentPeriodStart = start;
        summary.CurrentPeriodEnd = end;
        summary.MinutesUsed = await _billing.UnbilledMinutesAsync(orgId);
        summary.MinutesRemaining = sub.IsMetered
            ? Math.Max(0, sub.IncludedMinutes - summary.MinutesUsed) : 0;
        summary.MinutesOver = sub.IsMetered
            ? Math.Max(0, summary.MinutesUsed - sub.IncludedMinutes) : 0;
        summary.ProjectedOverageAmount =
            StripeMoney.Round(summary.MinutesOver * sub.OverageRatePerMinute, sub.Currency);

        summary.UpcomingCharges = BuildUpcomingCharges(sub, summary);
        summary.EstimatedNextInvoice = summary.UpcomingCharges.Sum(c => c.Amount);

        var history = await _billing.ListUsagePeriodsAsync(orgId, 13);
        summary.History = history.Select(ToView).ToList();
        summary.PreviousPeriod = summary.History.FirstOrDefault();

        var invoices = await _billing.ListInvoicesAsync(orgId, 12);
        summary.Invoices = invoices.Select(ToView).ToList();

        return summary;
    }

    /// <summary>
    /// A payment this account started and that has not been confirmed yet.
    ///
    /// The window is deliberately generous. A Checkout session lives for 24 hours, and an attempt
    /// that is still open is either genuinely in flight or was abandoned at the card form — the
    /// page can say both of those things kindly, and saying nothing at all is what leaves a
    /// customer whose connection dropped believing their money has vanished.
    /// </summary>
    private async Task<PendingPaymentView?> FindPendingPaymentAsync(int orgId)
    {
        var attempts = await _billing.ListPaymentAttemptsAsync(orgId, 5);

        var open = attempts.FirstOrDefault(a =>
            !a.IsSettled && a.StartedAt > DateTime.UtcNow.AddHours(-24));

        if (open is null) return null;

        return new PendingPaymentView
        {
            PlanName = open.PlanName,
            Amount = open.Amount,
            Currency = open.Currency,
            StartedAt = open.StartedAt,
            // Long enough that both Stripe's redelivery and the customer's own return have had
            // every chance. Past this it is worth telling them to check rather than keep waiting.
            IsStale = open.StartedAt < DateTime.UtcNow.AddMinutes(-15),
        };
    }

    public async Task<UsageSnapshot> GetUsageAsync(int orgId, CancellationToken ct = default)
    {
        var sub = await _billing.GetSubscriptionAsync(orgId);

        if (sub is null || !sub.HasPlan)
        {
            // Still counted, and still shown. Minutes an organization has run up before it is on a
            // tier are real, and hiding them until someone subscribes would make the page look
            // broken during exactly the window this platform is being evaluated in.
            var (openStart, openEnd) = UsageWindows.Current(
                sub?.CurrentPeriodStart ?? DateTime.UtcNow, BillingCycles.Monthly, DateTime.UtcNow);

            return new UsageSnapshot
            {
                HasSubscription = false,
                Currency = sub?.Currency ?? "USD",
                PeriodStart = openStart,
                PeriodEnd = openEnd,
                // Bounded to the window shown. With no plan nothing has ever been closed, so every
                // call ever made is technically unbilled — reporting that against a one-month
                // window would be a number the page cannot justify.
                MinutesUsed = await _billing.UnbilledMinutesAsync(orgId, openStart),
            };
        }

        // The same anchor GetSummaryAsync and the period close use, so a customer watching this
        // number tick up during a call is watching the one their bill is computed from. No periods
        // are closed here: this is polled, and closing is the sweep's job.
        var anchor = await _billing.LastClosedPeriodEndAsync(orgId) ?? sub.CurrentPeriodStart;
        var (start, end) = UsageWindows.Current(anchor, sub.BillingCycle, DateTime.UtcNow);

        var used = await _billing.UnbilledMinutesAsync(orgId);
        var over = sub.IsMetered ? Math.Max(0, used - sub.IncludedMinutes) : 0;

        return new UsageSnapshot
        {
            HasSubscription = true,
            PlanName = sub.PlanName,
            Currency = sub.Currency,
            Metered = sub.IsMetered,
            IncludedMinutes = sub.IncludedMinutes,
            MinutesUsed = used,
            MinutesRemaining = sub.IsMetered ? Math.Max(0, sub.IncludedMinutes - used) : 0,
            MinutesOver = over,
            OverageRatePerMinute = sub.OverageRatePerMinute,
            ProjectedOverageAmount = StripeMoney.Round(over * sub.OverageRatePerMinute, sub.Currency),
            PeriodStart = start,
            PeriodEnd = end,
        };
    }

    /// <summary>The next invoice, itemised. Three things can be on it: the plan, an overrun
    /// already closed and waiting, and the overrun building up right now.</summary>
    private static List<ChargeLine> BuildUpcomingCharges(SubscriptionRecord sub, BillingSummary s)
    {
        var lines = new List<ChargeLine>
        {
            new()
            {
                Label = $"{sub.PlanName} plan",
                Detail = BillingCycles.IsYearly(sub.BillingCycle)
                    ? "Yearly subscription"
                    : "Monthly subscription",
                Amount = sub.Amount,
                Kind = InvoiceLineKinds.Subscription,
                IsFinal = true,
            },
        };

        if (s.PendingOverageAmount > 0)
            lines.Add(new ChargeLine
            {
                Label = "Extra minutes from your last period",
                Detail = $"{s.PendingOverageMinutes:N0} min beyond the {sub.IncludedMinutes:N0} included, " +
                         $"at {Rate(sub)} per minute",
                Amount = s.PendingOverageAmount,
                Kind = InvoiceLineKinds.Overage,
                IsFinal = true,
            });

        if (s.MinutesOver > 0 && s.ProjectedOverageAmount > 0)
            lines.Add(new ChargeLine
            {
                Label = "Extra minutes so far this period",
                Detail = $"{s.MinutesOver:N0} min beyond the {sub.IncludedMinutes:N0} included, " +
                         $"at {Rate(sub)} per minute — still counting until {s.CurrentPeriodEnd:d MMM}",
                Amount = s.ProjectedOverageAmount,
                Kind = InvoiceLineKinds.Overage,
                // The period is still open, so this number can still move.
                IsFinal = false,
            });

        return lines;
    }

    private static string Rate(SubscriptionRecord sub) =>
        $"{sub.OverageRatePerMinute.ToString("0.####", CultureInfo.InvariantCulture)} {sub.Currency}";

    private static PeriodView ToView(UsagePeriod p) => new()
    {
        PeriodStart = p.PeriodStart,
        PeriodEnd = p.PeriodEnd,
        PlanName = p.PlanName,
        IncludedMinutes = p.IncludedMinutes,
        MinutesUsed = p.MinutesUsed,
        OverageMinutes = p.OverageMinutes,
        OverageRatePerMinute = p.OverageRatePerMinute,
        OverageAmount = p.OverageAmount,
        BaseAmount = p.BaseAmount,
        Currency = p.Currency,
        Billed = p.IsBilled,
    };

    private static InvoiceView ToView(OrganizationInvoice i) => new()
    {
        Number = i.Number,
        Status = i.Status,
        Total = i.Total,
        AmountOutstanding = i.AmountOutstanding,
        Currency = i.Currency,
        IssuedAt = i.IssuedAt,
        PaidAt = i.PaidAt,
        PeriodStart = i.PeriodStart,
        PeriodEnd = i.PeriodEnd,
        HostedInvoiceUrl = i.HostedInvoiceUrl,
        InvoicePdfUrl = i.InvoicePdfUrl,
        Lines = i.Lines.Select(l => new ChargeLine
        {
            Label = l.Description,
            Amount = l.Amount,
            Kind = l.Kind,
            IsFinal = true,
        }).ToList(),
    };
}

/// <summary>Closes elapsed billing periods on a timer, so an overrun is totalled and waiting on the
/// account even for a customer Stripe is not collecting from — and so the number on the tenant's
/// billing page is never stale by more than one sweep.</summary>
public class BillingPeriodWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<BillingPeriodWorker> _logger;

    public BillingPeriodWorker(IServiceProvider services, IConfiguration config,
        ILogger<BillingPeriodWorker> logger)
    {
        _services = services;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, _config.GetValue("Billing:PeriodSweepHours", 6)));

        // A moment's grace at startup: the database bootstrap runs on the same signal and a
        // half-created schema would only produce a scary log line on every boot.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var billing = scope.ServiceProvider.GetRequiredService<IBillingService>();
                var closed = await billing.CloseAllElapsedPeriodsAsync(stoppingToken);
                if (closed > 0)
                    _logger.LogInformation("Closed {Count} billing period(s).", closed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A database blip must not kill the worker for the lifetime of the process.
                _logger.LogError(ex, "Billing period sweep failed; retrying at the next interval.");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

/// <summary>
/// Re-reads what Stripe says it has collected and applies anything the webhook did not.
///
/// The webhook is the fast path, not the guarantee. It can be missed outright — a signing secret
/// rotated without updating configuration rejects every delivery, and an endpoint that is down
/// past Stripe's retry window never hears about the payment at all. In both cases Stripe has the
/// customer's money and this platform does not know, which means a paid account keeps counting
/// down to the overdue sweep and is eventually suspended for non-payment.
///
/// This closes that hole: Stripe is the source of truth for money, so it is asked directly, on a
/// timer, and every recovery is logged loudly enough to point at the real fault.
/// </summary>
public class BillingReconciliationWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<BillingReconciliationWorker> _logger;

    public BillingReconciliationWorker(IServiceProvider services, IConfiguration config,
        ILogger<BillingReconciliationWorker> logger)
    {
        _services = services;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("Billing:ReconcileEnabled", true))
        {
            _logger.LogInformation("Stripe reconciliation is disabled (Billing:ReconcileEnabled).");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _config.GetValue("Billing:ReconcileSweepHours", 12)));
        var lookbackDays = Math.Max(1, _config.GetValue("Billing:ReconcileLookbackDays", 14));

        // Behind the period sweep's own startup delay, so a cold start closes periods before it
        // starts asking Stripe what it has collected against them.
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var billing = scope.ServiceProvider.GetRequiredService<IBillingService>();
                var result = await billing.ReconcileAsync(lookbackDays, stoppingToken);

                if (!result.StripeConfigured)
                {
                    _logger.LogInformation(
                        "Stripe is not connected; there is nothing to reconcile. Stopping the sweep.");
                    return;
                }

                if (result.Recovered > 0)
                    _logger.LogWarning(
                        "Reconciliation recovered {Recovered} payment(s) out of {Examined} paid invoice(s) " +
                        "in the last {Days} day(s). Every one of these is a webhook that did not arrive.",
                        result.Recovered, result.Examined, lookbackDays);
                else
                    _logger.LogInformation(
                        "Reconciliation checked {Examined} paid invoice(s); all were already recorded.",
                        result.Examined);

                foreach (var failure in result.Failed)
                    _logger.LogError("Reconciliation could not apply {Failure}", failure);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Stripe being unreachable is not a reason to stop checking for the rest of the
                // process lifetime.
                _logger.LogError(ex, "Stripe reconciliation sweep failed; retrying at the next interval.");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
