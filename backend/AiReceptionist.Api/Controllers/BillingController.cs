using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Domain;
using AiReceptionist.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.Api.Controllers;

public record CheckoutRequest(int? PlanId);

public record ChangePlanRequest(int PlanId);

/// <summary>
/// What a customer can see and do about their own bill: what they are on, what they have used,
/// what the next invoice is shaping up to be and why, and what they were charged before.
///
/// A customer can only ever put themselves on a tier the operator published: pricing, allowances
/// and the overage rate are set in the super admin console, and nothing here lets a customer edit
/// those numbers. Choosing a tier takes effect once Stripe confirms the payment, not before.
/// </summary>
[ApiController]
[Route("api/v1/billing")]
[Authorize]
public class BillingController : ControllerBase
{
    private readonly IBillingService _billing;
    private readonly IBillingRepository _repo;
    private readonly ISettingsRepository _settings;
    private readonly IStripeGateway _stripe;
    private readonly ITenantProvider _tenant;
    private readonly ILogger<BillingController> _logger;

    public BillingController(IBillingService billing, IBillingRepository repo,
        ISettingsRepository settings, IStripeGateway stripe, ITenantProvider tenant,
        ILogger<BillingController> logger)
    {
        _billing = billing;
        _repo = repo;
        _settings = settings;
        _stripe = stripe;
        _tenant = tenant;
        _logger = logger;
    }

    /// <summary>The whole billing page in one call: plan, usage, the next bill itemised, the
    /// periods that have closed and the invoices raised.</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct) =>
        Ok(ApiResponse<BillingSummary>.Ok(await _billing.GetSummaryAsync(_tenant.OrganizationId, ct)));

    /// <summary>The periods that have closed, oldest charge explained the same way as the newest.</summary>
    [HttpGet("periods")]
    public async Task<IActionResult> Periods([FromQuery] int take = 24)
    {
        var periods = await _repo.ListUsagePeriodsAsync(_tenant.OrganizationId, Math.Clamp(take, 1, 60));
        return Ok(ApiResponse<IReadOnlyList<UsagePeriod>>.Ok(periods));
    }

    [HttpGet("invoices")]
    public async Task<IActionResult> Invoices([FromQuery] int take = 24)
    {
        var invoices = await _repo.ListInvoicesAsync(_tenant.OrganizationId, Math.Clamp(take, 1, 60));
        return Ok(ApiResponse<IReadOnlyList<OrganizationInvoice>>.Ok(invoices));
    }

    /// <summary>Every tier on offer, whether or not Stripe can collect for it.
    ///
    /// The tiers are what the customer is choosing between, so they are shown even when the
    /// platform has no Stripe connection and even when a particular tier has no Stripe price:
    /// hiding them leaves a customer staring at an empty page with no idea what is available,
    /// and those tiers are still perfectly billable by hand. <c>canPayOnline</c> says which ones
    /// Checkout can actually take money for, so the page offers the button only where it works.</summary>
    [HttpGet("plans")]
    public async Task<IActionResult> Plans()
    {
        var plans = (await _repo.ListPlansAsync(activeOnly: true))
            .Select(p => new
            {
                p.Id, p.Name, p.Description, p.Currency, p.Amount, p.BillingCycle,
                p.IncludedMinutes, p.OverageRatePerMinute,
                CanPayOnline = _stripe.IsConfigured && p.IsStripeReady,
            })
            .ToList();

        return Ok(ApiResponse<object>.Ok(plans));
    }

    /// <summary>Starts Stripe Checkout and hands back the URL to send the customer to. Card
    /// details are entered on Stripe's page and never reach this application.</summary>
    [HttpPost("checkout-session")]
    [Authorize(Roles = $"{Roles.OrgAdmin},{Roles.Manager}")]
    public async Task<IActionResult> CreateCheckoutSession(CheckoutRequest request, CancellationToken ct)
    {
        if (!_stripe.IsConfigured)
            return BadRequest(ApiResponse<object>.Fail(
                "Online payment is not set up on this platform yet. Please contact support to arrange billing."));

        var orgId = _tenant.OrganizationId;
        var org = await _settings.GetOrganizationAsync(orgId);
        if (org is null) return NotFound(ApiResponse<object>.Fail("Unknown organization."));

        var sub = await _repo.GetSubscriptionAsync(orgId);

        // Which tier: the one asked for, otherwise the one already on the account.
        var plan = request.PlanId is > 0
            ? await _repo.GetPlanAsync(request.PlanId.Value)
            : sub?.PlanId is > 0 ? await _repo.GetPlanAsync(sub.PlanId.Value) : null;

        if (plan is null || !plan.IsActive)
            return BadRequest(ApiResponse<object>.Fail(
                "Please choose one of the plans shown before continuing to payment."));

        if (!plan.IsStripeReady)
            return BadRequest(ApiResponse<object>.Fail(
                "This plan is not set up for online payment yet. Please contact support."));

        if (sub is not null && sub.IsStripeLinked)
            return BadRequest(ApiResponse<object>.Fail(
                "Your subscription is already active. Use “Manage payment method” to change your card or plan."));

        var customerId = await _stripe.EnsureCustomerAsync(
            orgId, org.Name, org.Email, sub?.StripeCustomerId, ct);

        // Stored before Checkout, not after: if the customer completes payment and the webhook
        // arrives before anything else, the linkage has to already exist to be found.
        if (sub is not null && !string.Equals(sub.StripeCustomerId, customerId, StringComparison.Ordinal))
            await _repo.SetStripeCustomerAsync(orgId, customerId);

        var url = await _stripe.CreateCheckoutSessionAsync(orgId, plan.Id, customerId, plan.StripePriceId!, ct);

        _logger.LogInformation("Organization {OrgId} started Stripe Checkout for tier {Plan}.", orgId, plan.Name);
        return Ok(ApiResponse<object>.Ok(new { url }));
    }

    /// <summary>
    /// Moves an already-subscribed customer onto a different tier, from their next billing cycle.
    ///
    /// Nothing changes today: the price change is queued in Stripe with proration off, and the new
    /// allowance is parked here until the period in force closes. That ordering is what makes an
    /// upgrade honest in both directions — the minutes already run up this period are still
    /// measured against the allowance the customer actually had, so the overrun they are
    /// upgrading to escape is still billed, and the larger allowance starts with the cycle they
    /// are paying the larger price for.
    /// </summary>
    [HttpPost("change-plan")]
    [Authorize(Roles = $"{Roles.OrgAdmin},{Roles.Manager}")]
    public async Task<IActionResult> ChangePlan(ChangePlanRequest request, CancellationToken ct)
    {
        var orgId = _tenant.OrganizationId;
        var sub = await _repo.GetSubscriptionAsync(orgId);

        if (sub is null || !sub.IsStripeLinked)
            return BadRequest(ApiResponse<object>.Fail(
                "There is no active subscription to move. Choose a plan and set up payment first."));

        var plan = await _repo.GetPlanAsync(request.PlanId);
        if (plan is null || !plan.IsActive)
            return BadRequest(ApiResponse<object>.Fail("That plan is not available."));

        if (!plan.IsStripeReady)
            return BadRequest(ApiResponse<object>.Fail(
                "That plan is not set up for online payment yet. Please contact support."));

        // Choosing the tier already in force means one of two things. With a move queued it is a
        // change of mind, and undoing it is the whole point of asking — so the queued tier is
        // dropped and Stripe is put back on the price the customer is actually on. With nothing
        // queued there is simply nothing to do.
        if (plan.Id == sub.PlanId && sub.PendingPlanId is null)
            return BadRequest(ApiResponse<object>.Fail($"You are already on {plan.Name}."));

        // Parked *before* Stripe is told, not after. Stripe emits customer.subscription.updated the
        // moment the price changes, and that webhook reads this field to know the move is already
        // scheduled; with the write afterwards, a webhook that beat it would see nothing pending
        // and apply the new allowance mid-period — exactly the outcome this endpoint exists to
        // avoid. Written first, the worst case is a pending tier that Stripe then rejects, which
        // the catch below undoes.
        var cancelling = plan.Id == sub.PlanId;

        await _repo.SetPendingPlanAsync(orgId, cancelling ? null : plan.Id);

        try
        {
            await _stripe.UpdateSubscriptionPriceAsync(sub.StripeSubscriptionId!, plan.StripePriceId!, ct);
        }
        catch (Stripe.StripeException ex)
        {
            await _repo.SetPendingPlanAsync(orgId, sub.PendingPlanId);
            _logger.LogError(ex, "Stripe refused to move organization {OrgId} to tier {Plan}.", orgId, plan.Name);
            return BadRequest(ApiResponse<object>.Fail(
                "Your plan could not be changed just now. Please try again, or contact support."));
        }

        if (cancelling)
        {
            _logger.LogInformation(
                "Organization {OrgId} cancelled its queued move and stays on {Plan}.", orgId, plan.Name);

            return Ok(ApiResponse<object>.Ok(new
            {
                planName = plan.Name,
                effectiveFrom = (DateTime?)null,
                message = $"Your plan change has been cancelled — you stay on {plan.Name}.",
            }));
        }

        _logger.LogInformation(
            "Organization {OrgId} will move from {Old} to {New} at its next billing cycle.",
            orgId, sub.PlanName, plan.Name);

        return Ok(ApiResponse<object>.Ok(new
        {
            planName = plan.Name,
            effectiveFrom = sub.CurrentPeriodEnd,
            message = $"You will move to {plan.Name} at the start of your next billing cycle. "
                    + "Nothing changes before then, and nothing is charged today.",
        }));
    }

    /// <summary>Opens the Stripe billing portal, where the customer changes their card, reads their
    /// invoices and cancels. Stripe owns that screen, so nothing here handles card details.</summary>
    [HttpPost("portal-session")]
    [Authorize(Roles = $"{Roles.OrgAdmin},{Roles.Manager}")]
    public async Task<IActionResult> CreatePortalSession(CancellationToken ct)
    {
        if (!_stripe.IsConfigured)
            return BadRequest(ApiResponse<object>.Fail("Online payment is not set up on this platform yet."));

        var sub = await _repo.GetSubscriptionAsync(_tenant.OrganizationId);
        if (sub is null || !sub.HasStripeCustomer)
            return BadRequest(ApiResponse<object>.Fail(
                "There is no payment method on file for your account yet."));

        var url = await _stripe.CreatePortalSessionAsync(sub.StripeCustomerId!, ct);
        return Ok(ApiResponse<object>.Ok(new { url }));
    }
}
