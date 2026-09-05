using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.Api.Controllers;

public record PlanUpsertRequest(
    int Id, string Name, string? Description, string Currency, decimal Amount, string BillingCycle,
    int IncludedMinutes, decimal OverageRatePerMinute, int SortOrder, bool IsActive,
    bool CreateStripePrice);

public record AssignPlanRequest(
    int PlanId, int? IncludedMinutes, decimal? OverageRatePerMinute, decimal? Amount);

public record AgentRestrictionRequest(bool Restricted, string? Reason);

public record SendInvoiceRequest(int DaysUntilDue, bool IncludePlanCharge);

/// <summary>
/// Billing administration for the super admin console: the tier catalogue, what each organization
/// is on, and the Stripe actions that go with it.
///
/// It lives here, alongside the Stripe gateway and the webhook, for the same reason Retell agent
/// creation does: one implementation of the rules, one process holding the secret key. The console
/// calls these with the shared platform key rather than reimplementing any of it.
/// </summary>
[ApiController]
[Route("api/v1/platform/billing")]
[PlatformKey]
public class PlatformBillingController : ControllerBase
{
    private readonly IBillingRepository _repo;
    private readonly IBillingService _billing;
    private readonly IStripeGateway _stripe;
    private readonly ISettingsRepository _settings;
    private readonly IRetellService _retell;
    private readonly IAuditRepository _audit;
    private readonly ILogger<PlatformBillingController> _logger;

    public PlatformBillingController(IBillingRepository repo, IBillingService billing,
        IStripeGateway stripe, ISettingsRepository settings, IRetellService retell,
        IAuditRepository audit, ILogger<PlatformBillingController> logger)
    {
        _repo = repo;
        _billing = billing;
        _stripe = stripe;
        _settings = settings;
        _retell = retell;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>Whether Stripe is connected at all. The console greys out what it cannot do rather
    /// than offering buttons that fail.</summary>
    [HttpGet("status")]
    public IActionResult Status() =>
        Ok(ApiResponse<object>.Ok(new { stripeConfigured = _stripe.IsConfigured }));

    // ---------------------------------------------------------------- the tier catalogue

    [HttpGet("plans")]
    public async Task<IActionResult> Plans() =>
        Ok(ApiResponse<IReadOnlyList<PricingPlan>>.Ok(await _repo.ListPlansAsync()));

    [HttpPost("plans")]
    public async Task<IActionResult> SavePlan(PlanUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(ApiResponse<object>.Fail("Give the tier a name."));
        if (request.Amount < 0)
            return BadRequest(ApiResponse<object>.Fail("The price cannot be negative."));
        if (request.OverageRatePerMinute < 0)
            return BadRequest(ApiResponse<object>.Fail("The overage rate cannot be negative."));

        var existing = request.Id > 0 ? await _repo.GetPlanAsync(request.Id) : null;

        var plan = new PricingPlan
        {
            Id = request.Id,
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Currency = (string.IsNullOrWhiteSpace(request.Currency) ? "USD" : request.Currency.Trim()).ToUpperInvariant(),
            Amount = request.Amount,
            BillingCycle = BillingCycles.Normalize(request.BillingCycle),
            IncludedMinutes = Math.Max(0, request.IncludedMinutes),
            OverageRatePerMinute = request.OverageRatePerMinute,
            SortOrder = request.SortOrder,
            IsActive = request.IsActive,
            StripeProductId = existing?.StripeProductId,
            StripePriceId = existing?.StripePriceId,
        };

        plan.Id = await _repo.UpsertPlanAsync(plan, userId: null);

        // A Stripe price is immutable, so a repriced tier needs a new one. The old price keeps
        // serving the subscriptions already on it — changing what an existing customer pays is a
        // deliberate act (move them to the new tier), never a side effect of an edit here.
        var repriced = existing is not null &&
            (existing.Amount != plan.Amount ||
             !string.Equals(existing.Currency, plan.Currency, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(existing.BillingCycle, plan.BillingCycle, StringComparison.OrdinalIgnoreCase));

        var message = "Tier saved.";

        if (_stripe.IsConfigured && (request.CreateStripePrice || (repriced && plan.IsStripeReady)))
        {
            try
            {
                var (productId, priceId) = await _stripe.CreatePriceAsync(plan);
                await _repo.SetPlanStripeIdsAsync(plan.Id, productId, priceId);
                plan.StripeProductId = productId;
                plan.StripePriceId = priceId;
                message = repriced
                    ? "Tier saved with a new Stripe price. Customers already subscribed keep the old one until you move them."
                    : "Tier saved and a Stripe price created.";
            }
            catch (Stripe.StripeException ex)
            {
                _logger.LogError(ex, "Could not create a Stripe price for tier {PlanId}.", plan.Id);
                message = $"Tier saved, but Stripe rejected the price: {ex.Message}";
            }
        }
        else if (request.CreateStripePrice && !_stripe.IsConfigured)
        {
            message = "Tier saved. Stripe is not connected, so no price was created — bill this tier by hand for now.";
        }

        return Ok(ApiResponse<PricingPlan>.Ok(plan, message));
    }

    [HttpDelete("plans/{planId:int}")]
    public async Task<IActionResult> RetirePlan(int planId)
    {
        if (await _repo.GetPlanAsync(planId) is null)
            return NotFound(ApiResponse<object>.Fail("Unknown tier."));

        await _repo.SoftDeletePlanAsync(planId);
        return Ok(ApiResponse<object>.Ok(new { },
            "Tier retired. Organizations already on it keep their price and allowance."));
    }

    // ---------------------------------------------------------------- one organization

    /// <summary>Everything the console shows for one customer's billing: plan, minutes used this
    /// period, what is carried over, the periods that closed and the invoices raised.</summary>
    [HttpGet("{orgId:int}")]
    public async Task<IActionResult> Organization(int orgId, CancellationToken ct)
    {
        if (await _settings.GetOrganizationAsync(orgId) is null)
            return NotFound(ApiResponse<object>.Fail("Unknown organization."));

        return Ok(ApiResponse<BillingSummary>.Ok(await _billing.GetSummaryAsync(orgId, ct)));
    }

    /// <summary>Puts an organization on a tier. The tier's numbers are copied onto the
    /// subscription, and any of them can be overridden for this customer alone.</summary>
    [HttpPost("{orgId:int}/plan")]
    public async Task<IActionResult> AssignPlan(int orgId, AssignPlanRequest request)
    {
        var org = await _settings.GetOrganizationAsync(orgId);
        if (org is null) return NotFound(ApiResponse<object>.Fail("Unknown organization."));

        var plan = await _repo.GetPlanAsync(request.PlanId);
        if (plan is null) return NotFound(ApiResponse<object>.Fail("Unknown tier."));

        if (request.IncludedMinutes is < 0)
            return BadRequest(ApiResponse<object>.Fail("Included minutes cannot be negative."));
        if (request.OverageRatePerMinute is < 0)
            return BadRequest(ApiResponse<object>.Fail("The overage rate cannot be negative."));
        if (request.Amount is < 0)
            return BadRequest(ApiResponse<object>.Fail("The price cannot be negative."));

        await _repo.ApplyPlanAsync(orgId, plan, request.IncludedMinutes,
            request.OverageRatePerMinute, request.Amount);

        await _audit.LogAsync(orgId, null, "SubscriptionPlanAssigned",
            $"Plan={plan.Name} (platform console)");

        var overridden = request.IncludedMinutes is not null || request.OverageRatePerMinute is not null
            || request.Amount is not null;

        return Ok(ApiResponse<object>.Ok(new { plan.Id, plan.Name },
            overridden
                ? $"{org.Name} is on {plan.Name}, with the values you overrode for this customer."
                : $"{org.Name} is on {plan.Name}."));
    }

    /// <summary>Subscribes the organization in Stripe directly, charging the payment method already
    /// on file. This is the operator-driven path; a customer without a card should be sent through
    /// Checkout instead.</summary>
    [HttpPost("{orgId:int}/subscribe")]
    public async Task<IActionResult> Subscribe(int orgId, CancellationToken ct)
    {
        if (!_stripe.IsConfigured)
            return BadRequest(ApiResponse<object>.Fail("Stripe is not connected (Stripe:SecretKey)."));

        var org = await _settings.GetOrganizationAsync(orgId);
        if (org is null) return NotFound(ApiResponse<object>.Fail("Unknown organization."));

        var sub = await _repo.GetSubscriptionAsync(orgId);
        if (sub is null)
            return BadRequest(ApiResponse<object>.Fail("Put this organization on a tier first."));
        if (sub.IsStripeLinked)
            return BadRequest(ApiResponse<object>.Fail("This organization already has a Stripe subscription."));
        if (sub.PlanId is not > 0)
            return BadRequest(ApiResponse<object>.Fail("Put this organization on a tier first."));

        var plan = await _repo.GetPlanAsync(sub.PlanId.Value);
        if (plan?.StripePriceId is null)
            return BadRequest(ApiResponse<object>.Fail(
                "That tier has no Stripe price yet. Create one on the Pricing screen."));

        try
        {
            var customerId = await _stripe.EnsureCustomerAsync(orgId, org.Name, org.Email, sub.StripeCustomerId, ct);
            await _repo.SetStripeCustomerAsync(orgId, customerId);

            var created = await _stripe.CreateSubscriptionAsync(orgId, customerId, plan.StripePriceId, ct);
            await _repo.SetStripeSubscriptionAsync(orgId, created.Id, created.Status);

            await _audit.LogAsync(orgId, null, "StripeSubscriptionCreated",
                $"SubscriptionId={created.Id} (platform console)");

            return Ok(ApiResponse<object>.Ok(new { subscriptionId = created.Id, status = created.Status },
                $"{org.Name} is now subscribed in Stripe ({created.Status})."));
        }
        catch (Stripe.StripeException ex)
        {
            // The commonest cause by far is no payment method on the customer. Say so, rather
            // than surfacing Stripe's wording alone.
            _logger.LogError(ex, "Stripe refused to create a subscription for organization {OrgId}.", orgId);
            return BadRequest(ApiResponse<object>.Fail(
                $"Stripe refused: {ex.Message} If the customer has no card on file, send them through Checkout instead."));
        }
    }

    /// <summary>Raises and emails a one-off Stripe invoice — the plan charge if asked for, plus any
    /// overage carried over. For customers who pay by invoice rather than card.</summary>
    [HttpPost("{orgId:int}/invoice")]
    public async Task<IActionResult> SendInvoice(int orgId, SendInvoiceRequest request, CancellationToken ct)
    {
        if (!_stripe.IsConfigured)
            return BadRequest(ApiResponse<object>.Fail("Stripe is not connected (Stripe:SecretKey)."));

        var org = await _settings.GetOrganizationAsync(orgId);
        if (org is null) return NotFound(ApiResponse<object>.Fail("Unknown organization."));

        var sub = await _repo.GetSubscriptionAsync(orgId);
        if (sub is null)
            return BadRequest(ApiResponse<object>.Fail("Put this organization on a tier first."));

        try
        {
            var customerId = await _stripe.EnsureCustomerAsync(orgId, org.Name, org.Email, sub.StripeCustomerId, ct);
            if (!string.Equals(sub.StripeCustomerId, customerId, StringComparison.Ordinal))
                await _repo.SetStripeCustomerAsync(orgId, customerId);

            if (request.IncludePlanCharge && sub.Amount > 0)
                await _stripe.AddInvoiceItemAsync(customerId, null, sub.Amount, sub.Currency,
                    $"{sub.PlanName} plan — {sub.CurrentPeriodStart:d MMM yyyy} to {sub.CurrentPeriodEnd:d MMM yyyy}",
                    ct);

            // Anything the customer ran over in a closed period goes on the same invoice, with
            // the line that explains it. Passing no invoice id leaves it pending, and the
            // CreateAndSendInvoice call below sweeps every pending item onto the new invoice.
            await _billing.AttachPendingOverageAsync(orgId, null, ct);

            var invoice = await _stripe.CreateAndSendInvoiceAsync(customerId,
                Math.Clamp(request.DaysUntilDue <= 0 ? 14 : request.DaysUntilDue, 1, 120), ct);

            await _billing.MirrorInvoiceAsync(invoice, ct);
            await _audit.LogAsync(orgId, null, "StripeInvoiceSent", $"InvoiceId={invoice.Id} (platform console)");

            return Ok(ApiResponse<object>.Ok(
                new { invoiceId = invoice.Id, invoice.Number, invoice.HostedInvoiceUrl },
                $"Invoice {invoice.Number ?? invoice.Id} sent to {org.Email ?? org.Name}."));
        }
        catch (Stripe.StripeException ex)
        {
            _logger.LogError(ex, "Could not send a Stripe invoice for organization {OrgId}.", orgId);
            return BadRequest(ApiResponse<object>.Fail($"Stripe refused: {ex.Message}"));
        }
    }

    /// <summary>Ends the Stripe subscription. History stays; only the automatic collection stops.</summary>
    [HttpPost("{orgId:int}/cancel")]
    public async Task<IActionResult> Cancel(int orgId, CancellationToken ct)
    {
        var sub = await _repo.GetSubscriptionAsync(orgId);
        if (sub is null || !sub.IsStripeLinked)
            return BadRequest(ApiResponse<object>.Fail("This organization has no Stripe subscription."));

        try
        {
            await _stripe.CancelSubscriptionAsync(sub.StripeSubscriptionId!, ct);
        }
        catch (Stripe.StripeException ex)
        {
            // Already gone upstream is not a failure — the local link should still be cleared.
            _logger.LogWarning(ex, "Stripe would not cancel subscription {SubId}; clearing the link anyway.",
                sub.StripeSubscriptionId);
        }

        await _repo.SetStripeSubscriptionAsync(orgId, null, "canceled");
        await _audit.LogAsync(orgId, null, "StripeSubscriptionCancelled", "platform console");

        return Ok(ApiResponse<object>.Ok(new { },
            "Stripe subscription cancelled. Nothing further will be collected automatically."));
    }

    // ---------------------------------------------------------------- restriction

    /// <summary>Stops the AI agent for this organization while leaving the dashboard usable, so a
    /// restricted customer can still sign in, read why, and pay.</summary>
    [HttpPost("{orgId:int}/agent-restriction")]
    public async Task<IActionResult> SetAgentRestriction(int orgId, AgentRestrictionRequest request,
        CancellationToken ct)
    {
        var org = await _settings.GetOrganizationAsync(orgId);
        if (org is null) return NotFound(ApiResponse<object>.Fail("Unknown organization."));

        var reason = string.IsNullOrWhiteSpace(request.Reason)
            ? "Restricted by the platform operator"
            : request.Reason.Trim();

        // The flag goes first and is what the agent's live-call tools read, so the restriction is
        // in force the moment this returns even if Retell is unreachable below.
        await _repo.SetAgentRestrictedAsync(orgId, request.Restricted, request.Restricted ? reason : null);

        // Refusing the tools alone would leave the agent answering — and burning the very minutes
        // the restriction is usually about. Detaching the number is what actually stops the calls.
        var routingWarning = await _retell.SetInboundRoutingAsync(orgId, enabled: !request.Restricted, ct);

        await _audit.LogAsync(orgId, null,
            request.Restricted ? "AgentRestricted" : "AgentRestrictionLifted",
            request.Restricted ? reason : "platform console");

        _logger.LogWarning("AI agent for organization {OrgId} was {Action} from the platform console.",
            orgId, request.Restricted ? "restricted" : "released");

        var message = request.Restricted
            ? $"{org.Name}'s agent has stopped taking calls. Their dashboard still works."
            : $"{org.Name}'s agent is taking calls again.";

        return Ok(ApiResponse<object>.Ok(
            new { restricted = request.Restricted, routingWarning },
            routingWarning is null ? message : $"{message} {routingWarning}"));
    }

    // ---------------------------------------------------------------- usage

    /// <summary>Runs the period close now instead of waiting for the sweep — used after changing a
    /// plan, so the console shows the effect immediately.</summary>
    [HttpPost("close-periods")]
    public async Task<IActionResult> ClosePeriods(CancellationToken ct)
    {
        var closed = await _billing.CloseAllElapsedPeriodsAsync(ct);
        return Ok(ApiResponse<object>.Ok(new { closed },
            closed == 0 ? "No billing periods were due to close." : $"Closed {closed} billing period(s)."));
    }

    // ---------------------------------------------------------------- reconciliation

    /// <summary>Asks Stripe what it has collected and applies anything missing, instead of waiting
    /// for the sweep. This is the answer to "the customer says they paid but it is not showing".</summary>
    [HttpPost("reconcile")]
    public async Task<IActionResult> Reconcile([FromQuery] int lookbackDays, CancellationToken ct)
    {
        var result = await _billing.ReconcileAsync(lookbackDays <= 0 ? 14 : lookbackDays, ct);

        if (!result.StripeConfigured)
            return Ok(ApiResponse<object>.Ok(result, "Stripe is not connected, so there is nothing to reconcile."));

        var message = result.Recovered > 0
            ? $"Recovered {result.Recovered} payment(s) that no webhook had applied, out of " +
              $"{result.Examined} paid invoice(s). Check the webhook endpoint and signing secret."
            : $"Checked {result.Examined} paid invoice(s); all were already recorded.";

        return Ok(ApiResponse<object>.Ok(result, message));
    }

    /// <summary>Webhook deliveries that never completed. A non-empty list here is the signal that
    /// something is wrong with the Stripe integration rather than with any one customer.</summary>
    [HttpGet("webhook-failures")]
    public async Task<IActionResult> WebhookFailures([FromQuery] int take)
    {
        var stuck = await _repo.ListUnfinishedWebhookEventsAsync(take <= 0 ? 50 : take);
        return Ok(ApiResponse<object>.Ok(stuck, stuck.Count == 0
            ? "Every Stripe webhook delivery has been applied."
            : $"{stuck.Count} Stripe webhook delivery(s) never completed."));
    }
}
