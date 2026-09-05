using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Shared;

namespace AiReceptionist.Api.Services;

/// <summary>Why an organization's agent is not allowed to take calls. Ordered from the most
/// deliberate to the most incidental, which is the order they are checked in — an account an
/// operator has switched off should say so, not blame the bill.</summary>
public enum CallBlock
{
    /// <summary>Nothing is wrong; the agent may answer.</summary>
    None,

    /// <summary>The whole account is suspended, so nobody can even sign in.</summary>
    AccountDisabled,

    /// <summary>The operator stopped the agent from the console while leaving the dashboard usable.</summary>
    AgentStopped,

    /// <summary>Never subscribed. There is no plan, no allowance, and nothing that would ever
    /// produce a bill for the minutes.</summary>
    NoPlan,

    /// <summary>The free trial the operator granted has run out, and no plan was taken out before
    /// it did. Separate from <see cref="NoPlan"/> because this account was given the service and
    /// has used it — which is what both the log and the customer's own billing page should say.</summary>
    TrialExpired,

    /// <summary>On a plan, but past the paid-through date plus the grace period — or Stripe says the
    /// subscription is cancelled or unpaid.</summary>
    Unpaid,
}

/// <summary>
/// Whether an organization is entitled to have its AI agent answer calls, and what to say if not.
/// </summary>
public sealed record CallEntitlement(CallBlock Block, string? Detail)
{
    public static readonly CallEntitlement Allowed = new(CallBlock.None, null);

    public bool IsAllowed => Block == CallBlock.None;

    /// <summary>What the agent is told to say when a tool is refused mid-call. Deliberately the
    /// same words for every reason: the caller is a member of the public who rang a business, and
    /// the state of that business's subscription is none of their concern.</summary>
    public const string SpokenRefusal =
        "This service is temporarily unavailable. Please apologise, tell the caller " +
        "someone will follow up, and end the call.";

    /// <summary>One line for the log and the audit trail, naming the real reason.</summary>
    public string Explain() => Block switch
    {
        CallBlock.None => "entitled",
        CallBlock.AccountDisabled => $"the account is suspended ({Detail ?? "no reason recorded"})",
        CallBlock.AgentStopped => $"the operator stopped the agent ({Detail ?? "no reason recorded"})",
        CallBlock.NoPlan => "no plan has ever been taken out on this account",
        CallBlock.TrialExpired => $"the free trial has ended ({Detail ?? "no end date recorded"})",
        CallBlock.Unpaid => $"the subscription is not paid up ({Detail ?? "past the grace period"})",
        _ => "not entitled",
    };
}

public interface ICallEntitlementService
{
    /// <summary>Decides whether this organization's agent may take a call right now.</summary>
    Task<CallEntitlement> EvaluateAsync(int orgId, CancellationToken ct = default);
}

/// <summary>
/// The one place that answers "may this organization's agent take a call".
///
/// It exists because nothing used to ask. The agent's live-call tools checked whether the account
/// was suspended or had been stopped by hand, and the inbound-call webhook — the only hook that
/// runs *before* the agent speaks — checked nothing at all: it answered every call and set the
/// clock. Neither of those flags is touched by the absence of a plan, and the overdue sweep
/// deliberately skips an organization that has never subscribed, so as not to lock a brand-new
/// customer out before they have got started. The three gaps met in the middle: an organization
/// could sign up, connect a number, never pay, and be answered indefinitely. Worse, with no
/// allowance on the account nothing was metered either, so the minutes were not even recorded as
/// a debt somebody could chase later.
///
/// The decision is deliberately not a stored flag. A flag has to be kept in step with payments,
/// cancellations, plan changes and the clock, and every one of those is a chance to leave an
/// account switched off after it has paid. Reading the answer from the subscription each time
/// cannot go stale.
/// </summary>
public class CallEntitlementService : ICallEntitlementService
{
    /// <summary>
    /// Stripe statuses that mean nobody is collecting for this subscription any more.
    ///
    /// <c>past_due</c> is deliberately absent: Stripe is still retrying the card, the customer has
    /// usually done nothing wrong, and the grace period below is the platform's own answer to a
    /// late payment. Cutting a business's phone line off the hour a renewal fails would be a far
    /// worse mistake than carrying them for a few more days.
    /// </summary>
    private static readonly HashSet<string> DeadStripeStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "canceled", "cancelled", "unpaid", "incomplete_expired",
    };

    private readonly IBillingRepository _billing;
    private readonly ISettingsRepository _settings;

    public CallEntitlementService(IBillingRepository billing, ISettingsRepository settings)
    {
        _billing = billing;
        _settings = settings;
    }

    public async Task<CallEntitlement> EvaluateAsync(int orgId, CancellationToken ct = default)
    {
        var org = await _settings.GetOrganizationAsync(orgId);
        if (org is null)
            return new CallEntitlement(CallBlock.AccountDisabled, "no such organization");

        if (!org.IsActive)
            return new CallEntitlement(CallBlock.AccountDisabled, org.SuspendedReason);

        if (org.AgentRestricted)
            return new CallEntitlement(CallBlock.AgentStopped, org.AgentRestrictedReason);

        // A trial the operator granted is the platform giving the service away deliberately, so it
        // answers on its own terms: no plan is needed, nothing is collected, and the bill is not
        // consulted. It is checked after the two switch-offs above so that suspending an account
        // or stopping its agent still holds during a trial.
        if (org.IsOnTrial)
            return CallEntitlement.Allowed;

        var sub = await _billing.GetSubscriptionAsync(orgId);

        // A subscription row can exist with nothing on it: one is created to hold the Stripe
        // customer link before Checkout, so a payment arriving by any route has somewhere to land.
        // That row is bookkeeping, not a plan (see SubscriptionRecord.HasPlan).
        if (sub is null || !sub.HasPlan)
            return org.TrialExpired
                ? new CallEntitlement(CallBlock.TrialExpired,
                    $"it ended on {org.TrialEndsAt:d MMM yyyy} and no plan was taken out")
                : new CallEntitlement(CallBlock.NoPlan, null);

        if (sub.StripeStatus is { } status && DeadStripeStatuses.Contains(status))
            return new CallEntitlement(CallBlock.Unpaid, $"Stripe reports the subscription as {status}");

        // The same paid-through line the billing page shows the customer and the overdue sweep
        // suspends on, so an account is never cut off before its own dashboard says it is at risk.
        var paidThrough = sub.CurrentPeriodEnd.AddDays(sub.GraceDays);
        if (DateTime.UtcNow > paidThrough)
            return new CallEntitlement(CallBlock.Unpaid,
                $"paid through {paidThrough:d MMM yyyy}, including {sub.GraceDays} day(s) of grace");

        return CallEntitlement.Allowed;
    }
}

/// <summary>
/// Keeps Retell's inbound routing in step with what each organization is entitled to.
///
/// Refusing the inbound webhook stops a call being set up, and refusing the agent's tools stops it
/// doing anything useful — but both of those depend on Retell reaching this API, and the second
/// still lets the call connect and burn minutes. Detaching the number is the only stop that holds
/// when this application is unreachable, and it is the one the operator's own restrict button
/// already uses. This applies the same treatment automatically, on the reasons the platform can
/// determine for itself.
///
/// It restores routing as well. An account that pays is entitled again within one sweep, without
/// anybody in the console having to notice — which matters, because the alternative is a customer
/// who has paid sitting with a dead phone line until someone reads a log.
/// </summary>
public class CallEntitlementWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<CallEntitlementWorker> _logger;

    /// <summary>Remembers what routing was last set to, so a steady state costs no Retell calls.
    /// Deliberately in memory: a restart re-asserts every organization once, which is the right
    /// thing to do after a deployment that may have missed changes while it was down.</summary>
    private readonly Dictionary<int, bool> _lastApplied = [];

    public CallEntitlementWorker(IServiceProvider services, IConfiguration config,
        ILogger<CallEntitlementWorker> logger)
    {
        _services = services;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("Billing:EnforceEntitlementRouting", true))
        {
            _logger.LogInformation(
                "Automatic call entitlement enforcement is off (Billing:EnforceEntitlementRouting). " +
                "Unentitled organizations will still be refused at the inbound webhook and in the " +
                "agent's tools, but their number stays attached.");
            return;
        }

        var interval = TimeSpan.FromMinutes(
            Math.Clamp(_config.GetValue("Billing:EntitlementSweepMinutes", 30), 5, 720));

        // Behind the schema bootstrap, like the other billing workers.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Call entitlement sweep failed; retrying at the next interval.");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        var entitlement = scope.ServiceProvider.GetRequiredService<ICallEntitlementService>();
        var retell = scope.ServiceProvider.GetRequiredService<IRetellService>();

        // Only organizations with an agent connected: everyone else has no routing to change.
        foreach (var orgId in await settings.GetConnectedOrganizationIdsAsync())
        {
            ct.ThrowIfCancellationRequested();

            var decision = await entitlement.EvaluateAsync(orgId, ct);

            // An agent stopped from the console already had its number detached by the button that
            // stopped it, and putting it back is that operator's decision to make — not a sweep's.
            if (decision.Block == CallBlock.AgentStopped) continue;

            var shouldRoute = decision.IsAllowed;
            if (_lastApplied.TryGetValue(orgId, out var applied) && applied == shouldRoute) continue;

            try
            {
                var warning = await retell.SetInboundRoutingAsync(orgId, shouldRoute, ct);

                // Only remember it once it actually took. A failed detach that is recorded as done
                // is an unentitled number left answering until the process restarts.
                if (warning is null)
                {
                    _lastApplied[orgId] = shouldRoute;

                    // Worded for what is actually known. A null answer covers both "the number was
                    // re-pointed" and "there was no number to re-point" — a tenant on web test calls
                    // only, or one whose number is attached by hand in the Retell dashboard — and
                    // claiming a line was cut in that second case would send an operator looking for
                    // something that never happened. The refusals below hold either way.
                    if (!shouldRoute)
                        _logger.LogWarning(
                            "Organization {OrgId} is no longer entitled to take calls: {Reason}. Any " +
                            "number of theirs has been detached, and the inbound webhook refuses the " +
                            "call regardless. Their dashboard still works, so they can sign in and " +
                            "settle it.", orgId, decision.Explain());
                    else
                        _logger.LogInformation(
                            "Organization {OrgId} is entitled again; its calls have been restored.", orgId);
                }
                else
                {
                    _logger.LogWarning(
                        "Could not change inbound routing for organization {OrgId} ({Reason}): {Warning}",
                        orgId, decision.Explain(), warning);
                }
            }
            catch (Exception ex)
            {
                // One tenant's Retell problem must not stop the rest being enforced.
                _logger.LogError(ex, "Could not enforce call entitlement for organization {OrgId}.", orgId);
            }
        }
    }
}
