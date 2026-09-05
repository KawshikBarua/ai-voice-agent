using AiReceptionist.SuperAdmin.Data.Repositories;
using AiReceptionist.SuperAdmin.Models;
using AiReceptionist.SuperAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.SuperAdmin.Controllers;

[Route("organizations")]
public class OrganizationsController : PlatformControllerBase
{
    private readonly IOrganizationRepository _organizations;
    private readonly IBillingRepository _billing;
    private readonly IPlatformApiClient _api;
    private readonly ILogger<OrganizationsController> _logger;

    public OrganizationsController(IOrganizationRepository organizations, IBillingRepository billing,
        IPlatformApiClient api, ILogger<OrganizationsController> logger)
    {
        _organizations = organizations;
        _billing = billing;
        _api = api;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string? search, string filter = "all")
    {
        var rows = await _organizations.ListAsync(search);

        // Billing state is derived in code (it depends on "now"), so filtering happens here
        // rather than in SQL to keep one definition of overdue.
        IEnumerable<OrganizationRow> filtered = filter switch
        {
            "active" => rows.Where(o => o.IsActive),
            "suspended" => rows.Where(o => !o.IsActive),
            "overdue" => rows.Where(o => o.BillingState == BillingState.Overdue),
            "unbilled" => rows.Where(o => !o.HasSubscription),
            "connected" => rows.Where(o => o.IsAgentConnected),
            "disconnected" => rows.Where(o => !o.IsAgentConnected),
            _ => rows,
        };

        return View(new OrganizationListViewModel
        {
            Organizations = filtered.ToList(),
            Search = search,
            Filter = filter,
        });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Details(int id)
    {
        var organization = await _organizations.GetAsync(id);
        if (organization is null) return NotFound();

        var connection = await _api.GetRetellConnectionAsync();

        return View(new OrganizationDetailViewModel
        {
            Organization = organization,
            Agent = await _organizations.GetAgentAsync(id),
            Subscription = await _billing.GetSubscriptionAsync(id),
            Payments = await _billing.ListPaymentsAsync(id),
            Plans = await _billing.ListPlansAsync(activeOnly: true),
            UsagePeriods = await _billing.ListUsagePeriodsAsync(id),
            Invoices = await _billing.ListInvoicesAsync(id),
            StripeConfigured = await _api.IsStripeConfiguredAsync(),
            RecentCalls = await _organizations.RecentCallsAsync(id),
            RecentAppointments = await _organizations.RecentAppointmentsAsync(id),
            CallsPerMonth = await _organizations.CallsPerMonthAsync(id, DateTime.UtcNow.Year),
            RetellApiKeyConfigured = connection?.ApiKeyConfigured ?? false,
        });
    }

    /// <summary>Puts this organization on a catalogue tier, optionally overriding the price,
    /// allowance or overage rate for this customer alone.</summary>
    [HttpPost("{id:int}/plan")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignPlan(int id, AssignPlanInput input)
    {
        input.OrganizationId = id;
        if (input.PlanId <= 0)
        {
            Warn("Choose a tier first.");
            return RedirectToAction(nameof(Details), new { id });
        }

        var (ok, message) = await _api.AssignPlanAsync(input);
        if (ok) _logger.LogInformation("Super admin {UserId} put organization {OrgId} on tier {PlanId}.",
            CurrentUserId, id, input.PlanId);

        Report(ok, message);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Starts automatic collection in Stripe against the card already on file.</summary>
    [HttpPost("{id:int}/subscribe")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Subscribe(int id)
    {
        var (ok, message) = await _api.SubscribeAsync(id);
        Report(ok, message);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:int}/cancel-subscription")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelSubscription(int id)
    {
        var (ok, message) = await _api.CancelSubscriptionAsync(id);
        if (ok) _logger.LogWarning("Super admin {UserId} cancelled the Stripe subscription for organization {OrgId}.",
            CurrentUserId, id);

        Report(ok, message);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Raises and emails a one-off Stripe invoice, sweeping in any carried-over overage.</summary>
    [HttpPost("{id:int}/send-invoice")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendInvoice(int id, int daysUntilDue = 14, bool includePlanCharge = true)
    {
        var (ok, message) = await _api.SendInvoiceAsync(id, daysUntilDue, includePlanCharge);
        Report(ok, message);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Gives this organization free days on the agent, or (0 days) ends the trial early.
    /// Once the trial lapses the agent stops taking calls until a plan is paid for.</summary>
    [HttpPost("{id:int}/trial")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetTrial(int id, int days)
    {
        var (ok, message) = await _api.SetTrialAsync(id, days);
        if (ok) _logger.LogInformation("Super admin {UserId} set a {Days}-day trial on organization {OrgId}.",
            CurrentUserId, days, id);

        Report(ok, message);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Stops this organization's agent taking calls. Narrower than disabling the account:
    /// staff can still sign in, see why, and settle the bill.</summary>
    [HttpPost("{id:int}/restrict-agent")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RestrictAgent(int id, string? reason)
    {
        var (ok, message) = await _api.SetAgentRestrictionAsync(id, restricted: true, reason);
        if (ok) _logger.LogWarning("Super admin {UserId} restricted the agent for organization {OrgId}.",
            CurrentUserId, id);

        Report(ok, message);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:int}/release-agent")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReleaseAgent(int id)
    {
        var (ok, message) = await _api.SetAgentRestrictionAsync(id, restricted: false, reason: null);
        Report(ok, message);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Disables a tenant: its users can no longer sign in and its AI agent's live-call
    /// tools are refused, so an unpaid account stops incurring cost immediately.</summary>
    [HttpPost("{id:int}/disable")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disable(int id, string? reason)
    {
        var organization = await _organizations.GetAsync(id);
        if (organization is null) return NotFound();

        await _organizations.SetActiveAsync(id, active: false,
            reason: string.IsNullOrWhiteSpace(reason) ? "Disabled by the platform administrator" : reason.Trim());
        _logger.LogWarning("Super admin {UserId} disabled organization {OrgId}.", CurrentUserId, id);
        Notify($"{organization.Name} has been disabled. Its users can no longer sign in and its AI agent will not answer.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:int}/enable")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Enable(int id)
    {
        var organization = await _organizations.GetAsync(id);
        if (organization is null) return NotFound();

        await _organizations.SetActiveAsync(id, active: true, reason: null);
        _logger.LogInformation("Super admin {UserId} enabled organization {OrgId}.", CurrentUserId, id);
        Notify($"{organization.Name} has been re-enabled.");
        return RedirectToAction(nameof(Details), new { id });
    }
}
