using AiReceptionist.Api.Common;
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
    /// <summary>The exact talk time <see cref="MinutesUsed"/> was rounded up from, so the page can
    /// show both and the customer can check one against their call list.</summary>
    public long SecondsUsed { get; set; }
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
    /// <summary>The exact talk time behind <see cref="MinutesUsed"/>.</summary>
    public long SecondsUsed { get; set; }
    public int MinutesRemaining { get; set; }
    public int MinutesOver { get; set; }
    public decimal OverageRatePerMinute { get; set; }
    public decimal ProjectedOverageAmount { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    /// <summary>When the server read these numbers, so the page can say how fresh they are.</summary>
    public DateTime AsOf { get; set; } = DateTime.UtcNow;
}

/// <summary>What a Stripe subscription's status means to this platform.</summary>
public static class StripeSubscriptionStatus
{
    /// <summary>A subscription whose first payment never completed: not yet anything to bill on.</summary>
    public static bool IsUnstarted(string? status) =>
        string.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "incomplete_expired", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Gets an organization's minutes ready for the Stripe invoice that has just been opened: the
    /// period that invoice's renewal ends is closed by Stripe's clock rather than this server's, so
    /// its overrun can go on that very invoice. A subscription that predates cycle alignment is
    /// aligned first.
    /// </summary>
    Task PrepareForRenewalInvoiceAsync(int orgId, Stripe.Invoice invoice, CancellationToken ct = default);

    /// <summary>
    /// Moves an organization's minute periods onto Stripe's billing cycle, when a Stripe
    /// subscription starts (or is first seen).
    ///
    /// The period running at that moment is closed there and then, measured against the plan the
    /// customer was on while they used those minutes — so its overrun is charged at that plan's
    /// rate on the next invoice, and changing plan can never erase minutes already run up. From
    /// then on every period ends on one of Stripe's renewal dates. Must run <b>before</b> a new
    /// tier is applied. Safe to call repeatedly: a second call for the same anchor does nothing.
    /// </summary>
    Task AlignToStripeCycleAsync(int orgId, DateTime cycleAnchor, CancellationToken ct = default);

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
    private readonly ICallEntitlementService _entitlement;
    private readonly ICache _cache;
    private readonly ILogger<BillingService> _logger;

    public BillingService(IBillingRepository billing, ISettingsRepository settings,
        IStripeGateway stripe, ICallEntitlementService entitlement, ICache cache,
        ILogger<BillingService> logger)
    {
        _billing = billing;
        _settings = settings;
        _stripe = stripe;
        _entitlement = entitlement;
        _cache = cache;
        _logger = logger;
    }

    // ---------------------------------------------------------------- closing periods

    public async Task<int> CloseElapsedPeriodsAsync(int orgId, CancellationToken ct = default)
    {
        var sub = await _billing.GetSubscriptionAsync(orgId);
        if (sub is null) return 0;
        return await CloseAsync(sub, ct);
    }

    public async Task PrepareForRenewalInvoiceAsync(int orgId, Stripe.Invoice invoice,
        CancellationToken ct = default)
    {
        var sub = await _billing.GetSubscriptionAsync(orgId);
        if (sub is null) return;

        // A subscription from before periods followed Stripe's cycle: its periods still end on
        // their own dates, so this renewal's minutes would otherwise wait for a later invoice.
        if (sub.UsageCycleAnchor is null &&
            invoice.Parent?.SubscriptionDetails?.SubscriptionId is { Length: > 0 } subscriptionId)
            await AlignFromStripeAsync(orgId, subscriptionId, ct);

        // The renewal moment is the start of the service period this invoice bills. This server's
        // clock may lag Stripe's by a few seconds, which would leave the closing period open and
        // its overrun waiting a whole cycle for the next invoice.
        var asOf = DateTime.UtcNow;
        if (ServicePeriod(invoice) is { } service && service.Start > asOf)
            asOf = service.Start;

        sub = await _billing.GetSubscriptionAsync(orgId) ?? sub;
        await CloseAsync(sub, ct, asOf);
    }

    public async Task AlignToStripeCycleAsync(int orgId, DateTime cycleAnchor, CancellationToken ct = default)
    {
        var sub = await _billing.GetSubscriptionAsync(orgId);
        if (sub is null || sub.UsageCycleAnchor == cycleAnchor) return;

        // A future anchor (a subscription starting on a Stripe trial) needs no closing now: once it
        // is recorded, the period running ends on that date by itself.
        if (cycleAnchor <= DateTime.UtcNow)
        {
            // Anything that ran its full length on the old schedule before Stripe's cycle began
            // is closed on that schedule first.
            await CloseAsync(sub, ct, cycleAnchor);
            sub = await _billing.GetSubscriptionAsync(orgId) ?? sub;

            var from = await _billing.LastClosedPeriodEndAsync(orgId) ?? sub.CurrentPeriodStart;
            if (from < cycleAnchor && await FilePeriodAsync(sub, from, cycleAnchor))
                _logger.LogInformation(
                    "Organization {OrgId}'s minutes now follow its Stripe billing cycle: the period from " +
                    "{From:u} was closed at {Anchor:u} on the {Plan} plan it was used under.",
                    orgId, from, cycleAnchor, sub.PlanName);
        }

        await _billing.SetUsageCycleAnchorAsync(orgId, cycleAnchor);
    }

    /// <summary>Reads a subscription's billing cycle anchor from Stripe and aligns to it. Skipped
    /// for a subscription whose first payment has not gone through: nothing has started yet.</summary>
    private async Task AlignFromStripeAsync(int orgId, string subscriptionId, CancellationToken ct)
    {
        if (!_stripe.IsConfigured) return;

        var stripeSub = await _stripe.GetSubscriptionAsync(subscriptionId, ct);
        if (stripeSub is null || StripeSubscriptionStatus.IsUnstarted(stripeSub.Status)) return;

        await AlignToStripeCycleAsync(orgId, stripeSub.BillingCycleAnchor, ct);
    }

    public async Task<int> CloseAllElapsedPeriodsAsync(CancellationToken ct = default)
    {
        var closed = 0;
        foreach (var listed in await _billing.ListSubscriptionsAsync())
        {
            ct.ThrowIfCancellationRequested();
            var sub = listed;
            try
            {
                // Subscriptions Stripe was already collecting for before periods followed its cycle
                // are moved onto it here, once each.
                if (_stripe.IsConfigured && sub.IsStripeLinked && sub.UsageCycleAnchor is null)
                {
                    await AlignFromStripeAsync(sub.OrganizationId, sub.StripeSubscriptionId!, ct);
                    sub = await _billing.GetSubscriptionAsync(sub.OrganizationId) ?? sub;
                }

                closed += await CloseAsync(sub, ct);

                if (_stripe.IsConfigured && sub.IsStripeLinked)
                    await ChargeStaleOverageAsync(sub.OrganizationId, ct);
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

    /// <summary>
    /// Closes every window that has fully elapsed by <paramref name="asOf"/> (now, unless the caller
    /// is acting on Stripe's clock), filing each against the plan in force.
    /// </summary>
    private async Task<int> CloseAsync(SubscriptionRecord sub, CancellationToken ct, DateTime? asOf = null)
    {
        var anchor = await _billing.LastClosedPeriodEndAsync(sub.OrganizationId)
            ?? sub.CurrentPeriodStart;

        var closed = 0;
        foreach (var (start, end) in UsageWindows.Elapsed(anchor, sub.BillingCycle,
                     asOf ?? DateTime.UtcNow, sub.UsageCycleAnchor))
        {
            ct.ThrowIfCancellationRequested();
            if (await FilePeriodAsync(sub, start, end))
                closed++;
        }

        // A tier the customer chose mid-period takes effect only now, with every period it was
        // waiting behind closed and measured against the allowance they actually held at the time.
        // Nothing closed means the period is still running, so the change stays parked.
        if (closed > 0 && sub.PendingPlanId is > 0)
            await ApplyPendingPlanAsync(sub);

        return closed;
    }

    /// <summary>Totals one period against <paramref name="sub"/>'s allowance and files it. False
    /// when another caller filed the same period first.</summary>
    private async Task<bool> FilePeriodAsync(SubscriptionRecord sub, DateTime start, DateTime end)
    {
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
            return false;   // another caller filed this period first

        // The polled minutes are cached for a few seconds; a customer who has just paid should not
        // see the period they paid to close still counting against them.
        await _cache.RemoveAsync(CacheKeys.Usage(sub.OrganizationId));

        if (overAmount > 0)
        {
            await _billing.AddPendingOverageAsync(sub.OrganizationId, overMinutes, overAmount);
            _logger.LogInformation(
                "Organization {OrgId} used {Used} of {Included} minutes in the period ending {End:d}: " +
                "{Over} minute(s) over, {Amount} {Currency} carried to the next invoice.",
                sub.OrganizationId, used, sub.IncludedMinutes, end, overMinutes, overAmount, sub.Currency);
        }

        return true;
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
        await _cache.RemoveAsync(CacheKeys.Usage(sub.OrganizationId));
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
        CancellationToken ct = default) =>
        await ChargeCarriedAsync(orgId, await ListCarriedChargesAsync(orgId, ct), stripeInvoiceId, ct);

    /// <summary>
    /// The safety net under <c>invoice.created</c>, which is the only thing that otherwise puts a
    /// Stripe customer's overrun on a bill. Where that webhook never arrives — no reachable
    /// endpoint, a rotated signing secret — the overrun used to sit pending for ever while Stripe
    /// renewed the plan without it. Anything still pending a couple of hours after its period
    /// closed is added as a pending item, which Stripe puts on the customer's next invoice.
    ///
    /// The wait is what keeps the two from colliding: inside it, a renewal invoice opened at the
    /// period boundary is still a draft and the webhook can put the charge on that one instead.
    /// </summary>
    private async Task ChargeStaleOverageAsync(int orgId, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddHours(-2);
        var stale = (await _billing.ListUsagePeriodsAsync(orgId))
            .Where(p => !p.IsBilled && p.OverageAmount > 0 && p.ClosedAt < cutoff)
            .OrderBy(p => p.PeriodStart)
            .Select(p => new CarriedCharge(p.Id, p.OverageAmount, p.Currency, DescribeOverage(p)))
            .ToList();

        if (stale.Count == 0) return;

        if (await ChargeCarriedAsync(orgId, stale, null, ct))
            _logger.LogWarning(
                "Organization {OrgId} had {Count} period(s) of overage that no Stripe invoice picked up; " +
                "added as pending items for its next invoice. Check that invoice.created is being delivered.",
                orgId, stale.Count);
    }

    private async Task<bool> ChargeCarriedAsync(int orgId, IReadOnlyList<CarriedCharge> carried,
        string? stripeInvoiceId, CancellationToken ct)
    {
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
            await _billing.SettleUsagePeriodAsync(charge.PeriodId,
                stripeInvoiceId ?? UsagePeriod.AwaitingNextInvoice);
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

        // Before the new tier goes on, not after: the period running until now is closed against
        // the plan those minutes were used under. Applying the tier first re-measured them against
        // its larger allowance, so a customer could run over on a cheap plan and have the overrun
        // vanish by subscribing. From here on, periods end on Stripe's renewal dates.
        if (!string.IsNullOrWhiteSpace(session.SubscriptionId))
        {
            var stripeSub = await _stripe.GetSubscriptionAsync(session.SubscriptionId, ct);
            // Unreadable just now: the payment moment is within minutes of Stripe's anchor, and the
            // next subscription event corrects it to the exact one.
            await AlignToStripeCycleAsync(orgId.Value, stripeSub?.BillingCycleAnchor ?? DateTime.UtcNow, ct);
        }

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

        // The plan alone does not put the account back on the air: the call gate reads the access
        // window, and that only moves when a paid invoice is recorded. Left to invoice.paid, a
        // customer clearing an overdue bill was told "your plan is active" and then found their
        // agent still refusing calls — indefinitely, wherever the webhook cannot reach this API.
        // The invoice Checkout just paid is recorded here instead. It is keyed on the invoice id,
        // so whichever of this and the webhook lands second does nothing.
        await RecordCheckoutInvoiceAsync(session, ct);

        // The new allowance has to be on screen when the page redraws, not a few seconds later.
        await _cache.RemoveAsync(CacheKeys.Usage(orgId.Value));

        return new CheckoutOutcome(true, orgId, null);
    }

    private async Task RecordCheckoutInvoiceAsync(Stripe.Checkout.Session session, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(session.InvoiceId) || !_stripe.IsConfigured) return;

        var invoice = await _stripe.GetInvoiceAsync(session.InvoiceId, ct);
        if (invoice is null || !string.Equals(invoice.Status, "paid", StringComparison.OrdinalIgnoreCase))
            return;   // not collected yet; invoice.paid or the reconciliation sweep will record it

        try
        {
            await HandleInvoicePaidAsync(invoice, ct);
        }
        catch (Exception ex)
        {
            // The plan is applied and the money is collected; the access window catching up is
            // something the webhook and the sweep both retry. Failing the customer's confirmation
            // over it would tell them a payment that went through did not.
            _logger.LogError(ex,
                "Checkout session {SessionId} was applied, but its invoice {InvoiceId} could not be " +
                "recorded yet. The reconciliation sweep will retry it.", session.Id, invoice.Id);
        }
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

        // Overruns handed to Stripe for "the next invoice" are matched to the one that carried
        // them, by the wording each was charged with — after which they are history, not part of
        // the bill still to come.
        var overageLines = lines
            .Where(l => l.Kind == InvoiceLineKinds.Overage)
            .Select(l => l.Description)
            .ToHashSet(StringComparer.Ordinal);

        if (overageLines.Count > 0)
            foreach (var period in (await _billing.ListUsagePeriodsAsync(orgId.Value))
                         .Where(p => p.IsAwaitingNextInvoice && overageLines.Contains(DescribeOverage(p))))
                await _billing.SetUsagePeriodInvoiceAsync(period.Id, invoice.Id);

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

        // Worked out before the payment is filed, so the payment is filed against the access it
        // actually bought rather than the window that happened to be in force when it landed.
        // Null means it buys none: an invoice for overage alone settles minutes already used, and
        // an older invoice processed late must not pull access back behind a newer one.
        var access = BuysAccess(invoice) ? NextAccessWindow(sub, invoice) : null;

        var recorded = await _billing.TryAddStripePaymentAsync(new PaymentRecord
        {
            OrganizationId = orgId.Value,
            Amount = amount,
            Currency = currency,
            PaidAt = invoice.StatusTransitions?.PaidAt ?? DateTime.UtcNow,
            PeriodStart = access?.Start ?? sub.CurrentPeriodStart,
            PeriodEnd = access?.End ?? sub.CurrentPeriodEnd,
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

        if (access is { } bought)
            await _billing.AdvanceAccessPeriodAsync(orgId.Value, bought.Start, bought.End);

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
    /// The access window a paid invoice buys, or null when it moves nothing.
    ///
    /// A subscription invoice buys exactly the service period on its subscription line: Stripe is
    /// the authority on what it charged for, so access and Stripe's renewals fall on the same
    /// dates. Two things used to go wrong here, both from adding one cycle to the old window:
    /// <list type="bullet">
    /// <item>A customer who paid late was sold the old, unpaid window — mostly already in the past
    /// — while Stripe began a fresh cycle on the day they paid. They were cut off a week before
    /// every renewal from then on, while paying on time. (The invoice-level period could not
    /// correct it: on a subscription invoice it looks <i>back</i> a cycle, and on the first one it
    /// is a single instant.)</item>
    /// <item>A first subscription was given its first month twice — once when the tier was applied
    /// and again when the invoice was paid — so a customer whose card then failed kept answering
    /// for a month they never paid for.</item>
    /// </list>
    /// A service period that ends no later than access already runs is an older invoice processed
    /// late (Stripe does not order its deliveries, and reconciliation replays them), and it must
    /// never pull access backwards.
    ///
    /// A one-off invoice — one the operator raised with the plan charge on it — has no service
    /// period, and buys one cycle on from the current window, the same rule as a payment recorded
    /// by hand in the console.
    /// </summary>
    private static (DateTime Start, DateTime End)? NextAccessWindow(
        SubscriptionRecord sub, Stripe.Invoice invoice)
    {
        if (ServicePeriod(invoice) is { } service)
            return service.End > sub.CurrentPeriodEnd ? service : null;

        return (sub.CurrentPeriodEnd, sub.NextPeriodEnd());
    }

    /// <summary>The stretch of time a subscription invoice bills for, read from its longest-running
    /// line. One-off lines (a carried overage) cover no span and are ignored.</summary>
    private static (DateTime Start, DateTime End)? ServicePeriod(Stripe.Invoice invoice)
    {
        var period = (invoice.Lines?.Data ?? [])
            .Select(l => l.Period)
            .Where(p => p is not null && p.End > p.Start)
            .MaxBy(p => p!.End);

        return period is null ? null : (period.Start, period.End);
    }

    /// <summary>
    /// Whether paying this invoice pays for the service, rather than only for minutes already used.
    /// An operator can invoice the overrun on its own; paying that settles a debt, and must not buy
    /// the account another month of calls. An invoice whose lines were not sent is given the benefit
    /// of the doubt, which is what every invoice was given before this was asked.
    /// </summary>
    private static bool BuysAccess(Stripe.Invoice invoice)
    {
        var lines = invoice.Lines?.Data;
        if (lines is null || lines.Count == 0) return true;

        return lines.Any(l => !(l.Description ?? "").StartsWith(
            OverageCharge.DescriptionPrefix, StringComparison.OrdinalIgnoreCase));
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

        await AlignToStripeCycleAsync(orgId, stripeSub!.BillingCycleAnchor, ct);
        await _billing.ApplyPlanAsync(orgId, plan, null, null, null);
        await _billing.SetStripeSubscriptionAsync(orgId, subscriptionId, stripeSub.Status);

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

        // Stripe is collecting and the renewal date has passed with no payment recorded here. Either
        // the card was declined — and the customer needs the open invoice in front of them to pay
        // it — or it was paid and the webhook never arrived, which left a paying customer reading
        // that they were overdue. Asking Stripe directly settles which, before the page is drawn.
        if (sub is { IsStripeLinked: true, HasStripeCustomer: true } && _stripe.IsConfigured &&
            DateTime.UtcNow > sub.CurrentPeriodEnd)
        {
            await CatchUpWithStripeAsync(sub.StripeCustomerId!, ct);
            sub = await _billing.GetSubscriptionAsync(orgId);
        }

        // Whether calls are being answered is the call gate's decision, not a flag on the row: an
        // agent stopped automatically for an overdue bill is just as stopped as one an operator
        // paused, and the customer needs to be told either way. Trial and no-plan have notices of
        // their own on this page.
        var gate = await _entitlement.EvaluateAsync(orgId, ct);
        var stopped = gate.Block is CallBlock.AgentStopped or CallBlock.Unpaid;

        var summary = new BillingSummary
        {
            StripeAvailable = _stripe.IsConfigured,
            AgentRestricted = stopped,
            AgentRestrictedReason = stopped ? gate.CustomerMessage : null,
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

        PricingPlan? pendingPlan = null;
        if (sub.PendingPlanId is > 0)
        {
            pendingPlan = await _billing.GetPlanAsync(sub.PendingPlanId.Value);
            summary.PendingPlanId = sub.PendingPlanId;
            summary.PendingPlanName = pendingPlan?.Name;
        }

        var anchor = await _billing.LastClosedPeriodEndAsync(orgId) ?? sub.CurrentPeriodStart;
        var (start, end) = UsageWindows.Current(anchor, sub.BillingCycle, DateTime.UtcNow, sub.UsageCycleAnchor);

        summary.CurrentPeriodStart = start;
        summary.CurrentPeriodEnd = end;
        (summary.MinutesUsed, summary.SecondsUsed) = await _billing.UnbilledUsageAsync(orgId);
        summary.MinutesRemaining = sub.IsMetered
            ? Math.Max(0, sub.IncludedMinutes - summary.MinutesUsed) : 0;
        summary.MinutesOver = sub.IsMetered
            ? Math.Max(0, summary.MinutesUsed - sub.IncludedMinutes) : 0;
        summary.ProjectedOverageAmount =
            StripeMoney.Round(summary.MinutesOver * sub.OverageRatePerMinute, sub.Currency);

        var periods = await _billing.ListUsagePeriodsAsync(orgId);
        // Including overruns already handed to Stripe for its next invoice: they are on the next
        // bill just as surely, and leaving them off understated it.
        var awaitingInvoice = periods
            .Where(p => p.OverageAmount > 0 && (!p.IsBilled || p.IsAwaitingNextInvoice))
            .OrderBy(p => p.PeriodStart)
            .ToList();

        summary.UpcomingCharges = BuildUpcomingCharges(sub, summary, pendingPlan, awaitingInvoice);
        summary.EstimatedNextInvoice = summary.UpcomingCharges.Sum(c => c.Amount);

        var history = periods.Take(13).ToList();
        summary.History = history.Select(ToView).ToList();
        summary.PreviousPeriod = summary.History.FirstOrDefault();

        var invoices = await _billing.ListInvoicesAsync(orgId, 12);
        summary.Invoices = invoices.Select(ToView).ToList();

        return summary;
    }

    /// <summary>
    /// Brings this customer's recent Stripe invoices in by asking for them, rather than waiting on
    /// webhooks: a paid one is recorded exactly as invoice.paid would record it (which is
    /// idempotent), and an open one is mirrored so the page can offer to pay it. Oldest first, so
    /// access advances in the order the customer paid.
    /// </summary>
    private async Task CatchUpWithStripeAsync(string stripeCustomerId, CancellationToken ct)
    {
        var invoices = await _stripe.ListRecentInvoicesAsync(stripeCustomerId, 5, ct);

        foreach (var invoice in invoices.Reverse())
        {
            try
            {
                if (string.Equals(invoice.Status, "paid", StringComparison.OrdinalIgnoreCase))
                {
                    if (await HandleInvoicePaidAsync(invoice, ct))
                        _logger.LogWarning(
                            "Recorded Stripe invoice {InvoiceId} when its customer opened the billing page; " +
                            "no webhook had applied it. Check the webhook endpoint and signing secret.",
                            invoice.Id);
                }
                else
                {
                    await MirrorInvoiceAsync(invoice, ct);
                }
            }
            catch (Exception ex)
            {
                // The page still renders from what is recorded; the sweep retries the rest.
                _logger.LogError(ex, "Could not catch up on Stripe invoice {InvoiceId}.", invoice.Id);
            }
        }
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

        // An attempt started before one that has since been paid was abandoned in favour of it:
        // the customer went back and chose again. Reporting it as "not confirmed yet" beside a plan
        // they have just paid for invites them to pay a second time.
        var lastPaid = attempts.Where(a => a.Succeeded).Select(a => (DateTime?)a.StartedAt).Max();

        var open = attempts.FirstOrDefault(a =>
            !a.IsSettled && a.StartedAt > DateTime.UtcNow.AddHours(-24) &&
            (lastPaid is null || a.StartedAt > lastPaid));

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

    /// <summary>
    /// Cached for ten seconds. Every open dashboard and billing page polls this every fifteen,
    /// and a call has to finish before a single one of its minutes exists — so there is nothing
    /// finer-grained to catch, and the window mostly serves to stop one customer's three open
    /// tabs running three sets of aggregates against the call log.
    /// </summary>
    public async Task<UsageSnapshot> GetUsageAsync(int orgId, CancellationToken ct = default) =>
        await _cache.GetOrSetAsync(CacheKeys.Usage(orgId), CacheTtl.Usage,
            () => LoadUsageAsync(orgId, ct), ct) ?? await LoadUsageAsync(orgId, ct);

    private async Task<UsageSnapshot> LoadUsageAsync(int orgId, CancellationToken ct)
    {
        var sub = await _billing.GetSubscriptionAsync(orgId);

        if (sub is null || !sub.HasPlan)
        {
            // Still counted, and still shown. Minutes an organization has run up before it is on a
            // tier are real, and hiding them until someone subscribes would make the page look
            // broken during exactly the window this platform is being evaluated in.
            var (openStart, openEnd) = UsageWindows.Current(
                sub?.CurrentPeriodStart ?? DateTime.UtcNow, BillingCycles.Monthly, DateTime.UtcNow);
            var openUsage = await _billing.UnbilledUsageAsync(orgId, openStart);

            return new UsageSnapshot
            {
                HasSubscription = false,
                Currency = sub?.Currency ?? "USD",
                PeriodStart = openStart,
                PeriodEnd = openEnd,
                // Bounded to the window shown. With no plan nothing has ever been closed, so every
                // call ever made is technically unbilled — reporting that against a one-month
                // window would be a number the page cannot justify.
                MinutesUsed = openUsage.Minutes,
                SecondsUsed = openUsage.Seconds,
            };
        }

        // The same anchor GetSummaryAsync and the period close use, so a customer watching this
        // number tick up during a call is watching the one their bill is computed from. No periods
        // are closed here: this is polled, and closing is the sweep's job.
        var anchor = await _billing.LastClosedPeriodEndAsync(orgId) ?? sub.CurrentPeriodStart;
        var (start, end) = UsageWindows.Current(anchor, sub.BillingCycle, DateTime.UtcNow, sub.UsageCycleAnchor);

        var (used, usedSeconds) = await _billing.UnbilledUsageAsync(orgId);
        var over = sub.IsMetered ? Math.Max(0, used - sub.IncludedMinutes) : 0;

        return new UsageSnapshot
        {
            HasSubscription = true,
            PlanName = sub.PlanName,
            Currency = sub.Currency,
            Metered = sub.IsMetered,
            IncludedMinutes = sub.IncludedMinutes,
            MinutesUsed = used,
            SecondsUsed = usedSeconds,
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
    private static List<ChargeLine> BuildUpcomingCharges(SubscriptionRecord sub, BillingSummary s,
        PricingPlan? pendingPlan, IReadOnlyList<UsagePeriod> awaitingInvoice)
    {
        // A move already queued is what the renewal charges for: Stripe switched the price when the
        // customer asked, and only the charge waits for the new cycle. Quoting the old price here
        // estimated a bill the customer was never going to receive.
        var plan = pendingPlan is not null
            ? new ChargeLine
            {
                Label = $"{pendingPlan.Name} plan",
                Detail = $"From your next cycle, replacing {sub.PlanName}",
                Amount = pendingPlan.Amount,
                Kind = InvoiceLineKinds.Subscription,
                IsFinal = true,
            }
            : new ChargeLine
            {
                Label = $"{sub.PlanName} plan",
                Detail = BillingCycles.IsYearly(sub.BillingCycle)
                    ? "Yearly subscription"
                    : "Monthly subscription",
                Amount = sub.Amount,
                Kind = InvoiceLineKinds.Subscription,
                IsFinal = true,
            };

        var lines = new List<ChargeLine> { plan };

        // One line per closed period, each in its own terms. They were one line worded with today's
        // allowance and rate, which misdescribes every overrun the moment the plan changes — the
        // customer read "27 min beyond the 500 included, at 0.12" against a charge that was really
        // 27 min beyond 150 at 1.12. The period records what was actually agreed at the time.
        foreach (var period in awaitingInvoice)
            lines.Add(new ChargeLine
            {
                Label = $"Extra minutes, {period.PeriodStart:d MMM} – {period.PeriodEnd:d MMM}",
                Detail = $"{period.OverageMinutes:N0} min beyond the {period.IncludedMinutes:N0} included on " +
                         $"{period.PlanName}, at {period.OverageRatePerMinute.ToString("0.####", CultureInfo.InvariantCulture)} " +
                         $"{period.Currency} per minute",
                Amount = period.OverageAmount,
                Kind = InvoiceLineKinds.Overage,
                IsFinal = true,
            });

        // A carry-over no closed period accounts for (entered by hand, or from before periods were
        // filed) is still owed, so it is still shown.
        var unexplained = s.PendingOverageAmount -
            awaitingInvoice.Where(p => !p.IsBilled).Sum(p => p.OverageAmount);
        if (unexplained > 0)
            lines.Add(new ChargeLine
            {
                Label = "Extra minutes from earlier periods",
                Amount = unexplained,
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
