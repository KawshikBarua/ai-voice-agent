using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Stripe;

namespace AiReceptionist.Api.Controllers;

/// <summary>
/// Stripe's callbacks: the only place invoices, payments and subscription status enter this system.
///
/// The endpoint is deliberately on this application rather than the super admin console — it has to
/// be reachable from the internet, which this one already is. It is exempt from payload encryption
/// by path (<c>/api/v1/webhooks/*</c>), because the signature is computed over the raw body and
/// rewriting it would make every delivery fail verification.
///
/// Every handler is written to be safe to run twice: Stripe retries until it gets a 2xx, and a
/// retry that charged a customer again or moved their billing period twice would be far worse than
/// a delivery that arrives late.
/// </summary>
[ApiController]
[Route("api/v1/webhooks/stripe")]
public class StripeWebhookController : ControllerBase
{
    private readonly IStripeGateway _stripe;
    private readonly IBillingService _billing;
    private readonly IBillingRepository _repo;
    private readonly ILogger<StripeWebhookController> _logger;

    public StripeWebhookController(IStripeGateway stripe, IBillingService billing,
        IBillingRepository repo, ILogger<StripeWebhookController> logger)
    {
        _stripe = stripe;
        _billing = billing;
        _repo = repo;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync(ct);

        Event stripeEvent;
        try
        {
            stripeEvent = _stripe.ConstructEvent(payload, Request.Headers["Stripe-Signature"].ToString());
        }
        catch (StripeException ex)
        {
            // Either a forged request or a mismatched webhook secret. Both are the caller's
            // problem to fix, and neither should be retried.
            _logger.LogWarning(ex, "Rejected a Stripe webhook: the signature did not verify.");
            return BadRequest(new { error = "Invalid signature." });
        }

        // Claim the event id before doing anything. A redelivery of something already handled ends
        // here, which is what keeps "record the payment" and "move the period on" happening once.
        if (!await _repo.TryClaimWebhookEventAsync(stripeEvent.Id, stripeEvent.Type))
        {
            _logger.LogInformation("Stripe event {EventId} ({Type}) has already been handled.",
                stripeEvent.Id, stripeEvent.Type);
            return Ok(new { received = true, duplicate = true });
        }

        try
        {
            await DispatchAsync(stripeEvent, ct);
            await _repo.MarkWebhookDoneAsync(stripeEvent.Id, null);
        }
        catch (Exception ex)
        {
            // The failure is recorded against the event, but the response is still 200: Stripe's
            // retry would be refused by the claim above, so asking for one is pointless. The row
            // in StripeWebhookEvents carries the error for whoever investigates.
            _logger.LogError(ex, "Handling Stripe event {EventId} ({Type}) failed.",
                stripeEvent.Id, stripeEvent.Type);
            await _repo.MarkWebhookDoneAsync(stripeEvent.Id, ex.Message);
            return Ok(new { received = true, handled = false });
        }

        return Ok(new { received = true });
    }

    private async Task DispatchAsync(Event e, CancellationToken ct)
    {
        switch (e.Type)
        {
            // Stripe has opened the next invoice. This is the moment the previous period's
            // overrun has to be added, because once the invoice finalizes it is fixed.
            case "invoice.created":
                if (e.Data.Object is Invoice created)
                {
                    var orgId = await ResolveAsync(created.CustomerId);
                    if (orgId is not null)
                        await _billing.AttachPendingOverageAsync(orgId.Value, created.Id, ct);

                    // Read it back: the totals changed if a line was just added.
                    var refreshed = await _stripe.GetInvoiceAsync(created.Id, ct) ?? created;
                    await _billing.MirrorInvoiceAsync(refreshed, ct);
                }
                break;

            case "invoice.finalized":
            case "invoice.updated":
            case "invoice.payment_failed":
            case "invoice.marked_uncollectible":
            case "invoice.voided":
                if (e.Data.Object is Invoice changed)
                {
                    await _billing.MirrorInvoiceAsync(changed, ct);
                    if (e.Type == "invoice.payment_failed")
                        _logger.LogWarning(
                            "Stripe could not collect invoice {InvoiceId} for customer {CustomerId}.",
                            changed.Id, changed.CustomerId);
                }
                break;

            case "invoice.paid":
            case "invoice.payment_succeeded":
                if (e.Data.Object is Invoice paid)
                    await _billing.HandleInvoicePaidAsync(paid, ct);
                break;

            // The customer finished Checkout. This is where a self-serve subscription first
            // becomes known here, so the linkage is written from the session.
            case "checkout.session.completed":
                if (e.Data.Object is Stripe.Checkout.Session session)
                    await LinkFromCheckoutAsync(session);
                break;

            case "customer.subscription.created":
            case "customer.subscription.updated":
                if (e.Data.Object is Subscription sub)
                {
                    var orgId = await ResolveSubscriptionOwnerAsync(sub);
                    if (orgId is not null)
                    {
                        await _repo.SetStripeSubscriptionAsync(orgId.Value, sub.Id, sub.Status);
                        await SyncTierFromPriceAsync(orgId.Value, sub);
                    }
                }
                break;

            case "customer.subscription.deleted":
                if (e.Data.Object is Subscription ended)
                {
                    var orgId = await ResolveSubscriptionOwnerAsync(ended);
                    if (orgId is not null)
                    {
                        // The link is dropped, not the history: invoices and closed periods stay,
                        // and the console can put the organization back on a tier later.
                        await _repo.SetStripeSubscriptionAsync(orgId.Value, null, ended.Status);
                        _logger.LogInformation(
                            "Stripe subscription for organization {OrgId} ended ({Status}).",
                            orgId, ended.Status);
                    }
                }
                break;

            default:
                _logger.LogDebug("Ignoring Stripe event type {Type}.", e.Type);
                break;
        }
    }

    private async Task LinkFromCheckoutAsync(Stripe.Checkout.Session session)
    {
        // ClientReferenceId is set when the session is created, so it is the reliable answer even
        // before any customer record here points at Stripe.
        if (!int.TryParse(session.ClientReferenceId, out var orgId))
        {
            var resolved = await ResolveAsync(session.CustomerId);
            if (resolved is null)
            {
                _logger.LogWarning(
                    "Checkout session {SessionId} completed but could not be matched to an organization.",
                    session.Id);
                return;
            }
            orgId = resolved.Value;
        }

        // The tier the customer chose, applied only now that Stripe has taken the payment. It runs
        // before the linkage below because those are UPDATEs: an organization subscribing for the
        // first time has no subscription row until this creates one, and they would find nothing.
        if (session.Metadata is not null &&
            session.Metadata.TryGetValue("planId", out var rawPlanId) &&
            int.TryParse(rawPlanId, out var planId))
        {
            var plan = await _repo.GetPlanAsync(planId);
            if (plan is not null)
                await _repo.ApplyPlanAsync(orgId, plan, null, null, null);
            else
                _logger.LogWarning(
                    "Checkout session {SessionId} named tier {PlanId}, which no longer exists.",
                    session.Id, planId);
        }

        if (!string.IsNullOrWhiteSpace(session.CustomerId))
            await _repo.SetStripeCustomerAsync(orgId, session.CustomerId);

        if (!string.IsNullOrWhiteSpace(session.SubscriptionId))
            await _repo.SetStripeSubscriptionAsync(orgId, session.SubscriptionId, "active");

        _logger.LogInformation(
            "Organization {OrgId} subscribed through Stripe Checkout (customer {CustomerId}).",
            orgId, session.CustomerId);
    }

    /// <summary>
    /// Brings the tier held here into line with the price Stripe is actually charging.
    ///
    /// This matters more than it looks: the minute allowance and the overage rate are read from the
    /// local subscription, not from Stripe. A customer who upgrades to escape an overrun and does
    /// not get this sync keeps the smaller allowance and the higher rate while paying the larger
    /// price — the opposite of what they bought.
    ///
    /// It deliberately does nothing when the price still maps to the tier already recorded, so
    /// per-customer overrides an operator has set are not flattened by an unrelated status change.
    /// </summary>
    private async Task SyncTierFromPriceAsync(int orgId, Subscription sub)
    {
        var priceId = sub.Items?.Data?.FirstOrDefault()?.Price?.Id;
        if (string.IsNullOrWhiteSpace(priceId)) return;

        var plan = await _repo.FindPlanByStripePriceAsync(priceId);
        if (plan is null)
        {
            // A price from before a repricing, or one created outside the catalogue. The money is
            // Stripe's to collect either way; there is just no tier here to line it up with.
            _logger.LogWarning(
                "Organization {OrgId} is on Stripe price {PriceId}, which matches no tier here. " +
                "Its allowance is unchanged.",
                orgId, priceId);
            return;
        }

        var current = await _repo.GetSubscriptionAsync(orgId);
        if (current?.PlanId == plan.Id) return;

        // The customer asked for this change through the application, and it is already queued to
        // land when their period closes. Stripe reports the new price the moment it is set — even
        // with proration off, because only the charge waits for the renewal — so applying it here
        // would hand them the larger allowance mid-period and erase the overrun they are upgrading
        // to get out of. Leave it to the period close, which is the only place that knows the
        // allowance the closing period must be measured against.
        if (current?.PendingPlanId == plan.Id)
        {
            _logger.LogInformation(
                "Organization {OrgId} is already queued to move to tier {Plan} at its period boundary; " +
                "leaving the allowance alone until then.",
                orgId, plan.Name);
            return;
        }

        await _repo.ApplyPlanAsync(orgId, plan, null, null, null);
        _logger.LogInformation(
            "Organization {OrgId} is now on tier {Plan} ({Minutes} minutes, {Rate} {Currency}/extra min), " +
            "following Stripe price {PriceId}.",
            orgId, plan.Name, plan.IncludedMinutes, plan.OverageRatePerMinute, plan.Currency, priceId);
    }

    private async Task<int?> ResolveSubscriptionOwnerAsync(Subscription sub)
    {
        if (sub.Metadata is not null &&
            sub.Metadata.TryGetValue("organizationId", out var raw) &&
            int.TryParse(raw, out var fromMetadata))
            return fromMetadata;

        if (await _repo.FindByStripeSubscriptionAsync(sub.Id) is { } bySubscription)
            return bySubscription.OrganizationId;

        return await ResolveAsync(sub.CustomerId);
    }

    private async Task<int?> ResolveAsync(string? stripeCustomerId) =>
        string.IsNullOrWhiteSpace(stripeCustomerId)
            ? null
            : (await _repo.FindByStripeCustomerAsync(stripeCustomerId))?.OrganizationId;
}
