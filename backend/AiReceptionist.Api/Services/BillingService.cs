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
}

public interface IBillingService
{
    /// <summary>Totals and files every usage window that has fully elapsed, adding any overrun to
    /// what the next invoice will carry. Safe to call repeatedly and from several places at once.</summary>
    Task<int> CloseElapsedPeriodsAsync(int orgId, CancellationToken ct = default);

    Task<int> CloseAllElapsedPeriodsAsync(CancellationToken ct = default);

    Task<BillingSummary> GetSummaryAsync(int orgId, CancellationToken ct = default);

    /// <summary>Puts the carried-over overage onto a Stripe invoice as its own line, then clears
    /// the carry-over. Called when Stripe raises the next invoice.</summary>
    Task<bool> AttachPendingOverageAsync(int orgId, string? stripeInvoiceId, CancellationToken ct = default);

    /// <summary>Copies a Stripe invoice and its lines into the local mirror.</summary>
    Task<int?> MirrorInvoiceAsync(Stripe.Invoice invoice, CancellationToken ct = default);

    /// <summary>Records the payment, moves the access period on, and lifts an automatic
    /// suspension. Idempotent: a redelivered webhook changes nothing.</summary>
    Task HandleInvoicePaidAsync(Stripe.Invoice invoice, CancellationToken ct = default);
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

            var used = await _billing.MinutesUsedAsync(sub.OrganizationId, start, end);

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

    public async Task<bool> AttachPendingOverageAsync(int orgId, string? stripeInvoiceId,
        CancellationToken ct = default)
    {
        // Catch up first: the invoice Stripe has just raised marks the end of a period, and that
        // period's overrun belongs on it rather than waiting another whole cycle.
        await CloseElapsedPeriodsAsync(orgId, ct);

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

        var periods = (await _billing.ListUsagePeriodsAsync(orgId))
            .Where(p => !p.IsBilled && p.OverageAmount > 0)
            .OrderBy(p => p.PeriodStart)
            .ToList();

        var description = DescribeOverage(periods, sub);

        await _stripe.AddInvoiceItemAsync(sub.StripeCustomerId, stripeInvoiceId,
            sub.PendingOverageAmount, sub.Currency, description, ct);

        // Only settled after Stripe has accepted the item, so a failure here leaves the charge
        // pending for the next attempt rather than quietly writing it off.
        await _billing.SettlePendingOverageAsync(orgId, stripeInvoiceId ?? "");

        _logger.LogInformation(
            "Charged organization {OrgId} {Amount} {Currency} for {Minutes} overage minute(s) on invoice {InvoiceId}.",
            orgId, sub.PendingOverageAmount, sub.Currency, sub.PendingOverageMinutes,
            stripeInvoiceId ?? "(next invoice)");

        return true;
    }

    /// <summary>The sentence the customer reads on their invoice. It carries the whole
    /// justification — how many minutes, over what allowance, at what rate, for which dates — so
    /// the charge needs no other explanation to make sense.</summary>
    private static string DescribeOverage(IReadOnlyList<UsagePeriod> periods, SubscriptionRecord sub)
    {
        var minutes = periods.Sum(p => p.OverageMinutes);
        var rate = periods.LastOrDefault()?.OverageRatePerMinute ?? sub.OverageRatePerMinute;
        var included = periods.LastOrDefault()?.IncludedMinutes ?? sub.IncludedMinutes;

        var span = periods.Count == 0
            ? ""
            : $" ({periods[0].PeriodStart:d MMM} – {periods[^1].PeriodEnd:d MMM yyyy})";

        return $"{OverageCharge.DescriptionPrefix}: {minutes:N0} min beyond the {included:N0} included, " +
               $"at {rate.ToString("0.####", CultureInfo.InvariantCulture)} {sub.Currency} per minute{span}";
    }

    // ---------------------------------------------------------------- mirroring Stripe

    public async Task<int?> MirrorInvoiceAsync(Stripe.Invoice invoice, CancellationToken ct = default)
    {
        var orgId = await ResolveOrganizationAsync(invoice);
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

    public async Task HandleInvoicePaidAsync(Stripe.Invoice invoice, CancellationToken ct = default)
    {
        var orgId = await MirrorInvoiceAsync(invoice, ct);
        if (orgId is null) return;

        var sub = await _billing.GetSubscriptionAsync(orgId.Value);
        if (sub is null) return;

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

        if (!recorded)
        {
            // Stripe redelivers until acknowledged. Everything past this point has already been
            // done for this invoice, and doing it again would push the period forward twice.
            _logger.LogInformation("Stripe invoice {InvoiceId} was already recorded; nothing further to do.",
                invoice.Id);
            return;
        }

        // Access moves on by exactly one cycle — the same rule the console's manual "record a
        // payment" uses, so an account billed both ways never ends up with two different notions
        // of what it has paid for.
        await _billing.AdvanceAccessPeriodAsync(orgId.Value,
            sub.CurrentPeriodEnd, sub.NextPeriodEnd());

        if (await _billing.ReactivateIfOverdueSuspendedAsync(orgId.Value))
            _logger.LogInformation(
                "Organization {OrgId} was re-enabled: the overdue subscription has been paid.", orgId);

        _logger.LogInformation("Recorded Stripe payment of {Amount} {Currency} for organization {OrgId}.",
            amount, currency, orgId);
    }

    private async Task<int?> ResolveOrganizationAsync(Stripe.Invoice invoice)
    {
        if (!string.IsNullOrWhiteSpace(invoice.CustomerId) &&
            await _billing.FindByStripeCustomerAsync(invoice.CustomerId) is { } byCustomer)
            return byCustomer.OrganizationId;

        // Falls back to the id written onto the Stripe object when it was created, which covers
        // an invoice arriving before the local link has been stored.
        if (invoice.Metadata is not null &&
            invoice.Metadata.TryGetValue("organizationId", out var raw) &&
            int.TryParse(raw, out var fromMetadata))
            return fromMetadata;

        return null;
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
            Currency = sub?.Currency ?? org?.Currency ?? "USD",
        };

        if (sub is null)
        {
            // No plan on file. The page says so rather than inventing a zero-cost subscription —
            // but the tiers are still offered, so a customer can put themselves on one.
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
        summary.MinutesUsed = await _billing.MinutesUsedAsync(orgId, start, end);
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
