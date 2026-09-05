using AiReceptionist.SuperAdmin.Data.Repositories;
using AiReceptionist.SuperAdmin.Models;
using AiReceptionist.SuperAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.SuperAdmin.Controllers;

/// <summary>
/// Retell AI is wired up here and nowhere else: tenants have no access to the credential or to
/// agent creation. Each organization is either not connected — in which case the only available
/// action is Connect — or connected, after which the admin may only Re-sync or Disconnect.
/// </summary>
[Route("retell")]
public class RetellController : PlatformControllerBase
{
    private readonly IPlatformApiClient _api;
    private readonly IOrganizationRepository _organizations;
    private readonly ILogger<RetellController> _logger;

    public RetellController(IPlatformApiClient api, IOrganizationRepository organizations,
        ILogger<RetellController> logger)
    {
        _api = api;
        _organizations = organizations;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        ViewBag.Connection = await _api.GetRetellConnectionAsync();
        return View(await _organizations.ListAsync());
    }

    [HttpPost("connection")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveConnection(RetellConnectionInput input)
    {
        var (ok, message) = await _api.UpdateRetellConnectionAsync(input);
        if (ok) _logger.LogInformation("Super admin {UserId} updated the Retell connection.", CurrentUserId);
        Report(ok, message);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Sets the tenant's transfer number and the Retell number its agent answers. Both
    /// are read-only in the tenant app: the Retell number decides which tenant a number routes to
    /// on the shared Retell account, so letting tenants edit it would let one take another's calls.</summary>
    [HttpPost("{orgId:int}/numbers")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveNumbers(int orgId, AgentNumbersInput input)
    {
        var organization = await _organizations.GetAsync(orgId);
        if (organization is null) return NotFound();

        input.OrganizationId = orgId;
        var (ok, synced, message) = await _api.UpdateNumbersAsync(input);
        if (ok)
            _logger.LogInformation("Super admin {UserId} changed the phone numbers for organization {OrgId}.",
                CurrentUserId, orgId);

        // A connected tenant whose numbers were stored but rejected by Retell is a warning, not a
        // success: the console would show the new numbers while calls still route the old way, and
        // that is exactly the state an operator must not mistake for "done". An unconnected tenant
        // has nothing to push, so storing the numbers is the whole job there.
        Report(ok && (synced || !organization.IsAgentConnected), $"{organization.Name}: {message}");
        return RedirectToAction("Details", "Organizations", new { id = orgId });
    }

    /// <summary>First-time connect. Refused when an agent already exists so a second agent can
    /// never be created for the same tenant by double-submitting the form. Builds the agent and
    /// knowledge base from scratch, deleting anything this organization left on the Retell account
    /// when it was last disconnected — otherwise every reconnect strands another pair there.</summary>
    [HttpPost("{orgId:int}/connect")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Connect(int orgId, string? returnTo) =>
        SyncAsync(orgId, returnTo, mustAlreadyBeConnected: false);

    /// <summary>Updates the agent, LLM and knowledge base this organization already has. Nothing
    /// is deleted and nothing new is created, so re-syncing as often as you like costs the Retell
    /// account nothing.</summary>
    [HttpPost("{orgId:int}/resync")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Resync(int orgId, string? returnTo) =>
        SyncAsync(orgId, returnTo, mustAlreadyBeConnected: true);

    [HttpPost("{orgId:int}/disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disconnect(int orgId, string? returnTo)
    {
        var organization = await _organizations.GetAsync(orgId);
        if (organization is null) return NotFound();

        if (!organization.IsAgentConnected)
        {
            Warn($"{organization.Name} has no Retell agent to disconnect.");
            return Back(orgId, returnTo);
        }

        var (ok, message) = await _api.DisconnectAgentAsync(orgId);
        if (ok)
        {
            _logger.LogInformation("Super admin {UserId} disconnected Retell for organization {OrgId}.",
                CurrentUserId, orgId);
            Notify($"{organization.Name} disconnected from Retell. Its agent and knowledge base still " +
                   "exist on the Retell account; connecting again deletes them and builds a fresh pair, " +
                   "so nothing is left stranded there.");
        }
        else
        {
            Warn(message);
        }

        return Back(orgId, returnTo);
    }

    private async Task<IActionResult> SyncAsync(int orgId, string? returnTo, bool mustAlreadyBeConnected)
    {
        var organization = await _organizations.GetAsync(orgId);
        if (organization is null) return NotFound();

        // The two entry points are deliberately not interchangeable: Connect creates the agent,
        // Re-sync updates the one that exists. Checking here (not only in the view) is what makes
        // "once configured, only disconnect or re-sync" an actual rule.
        if (mustAlreadyBeConnected && !organization.IsAgentConnected)
        {
            Warn($"{organization.Name} is not connected to Retell yet — use Connect.");
            return Back(orgId, returnTo);
        }
        if (!mustAlreadyBeConnected && organization.IsAgentConnected)
        {
            Warn($"{organization.Name} is already connected. Use Re-sync to push the latest configuration.");
            return Back(orgId, returnTo);
        }

        // Connect rebuilds from scratch, Re-sync updates what is there. Both carry the single
        // orgId this form was submitted for — no other organization is read, synced or touched.
        var outcome = await _api.SyncAgentAsync(orgId, fresh: !mustAlreadyBeConnected);
        if (!outcome.Success)
        {
            Warn($"{organization.Name}: {outcome.Message ?? "Retell sync failed."}");
            return Back(orgId, returnTo);
        }

        _logger.LogInformation("Super admin {UserId} synced Retell for organization {OrgId} (agent {AgentId}).",
            CurrentUserId, orgId, outcome.AgentId);

        var verb = mustAlreadyBeConnected ? "re-synced" : "connected";
        var message = $"{organization.Name} {verb} successfully (agent {outcome.AgentId}).";

        // Leftovers first: a phone number that did not attach is an inconvenience, but an old agent
        // that would not delete means this organization now has two on the account — which is the
        // thing the operator came here to avoid and the only one they must act on.
        var warning = new[] { outcome.CleanupWarning, outcome.PhoneWarning }
            .FirstOrDefault(w => !string.IsNullOrWhiteSpace(w));
        if (warning is not null)
        {
            // The agent is live either way — neither problem is a failed sync.
            Warn($"{message} {warning}");
            return Back(orgId, returnTo);
        }

        Notify(message);
        return Back(orgId, returnTo);
    }

    private IActionResult Back(int orgId, string? returnTo) =>
        string.Equals(returnTo, "details", StringComparison.OrdinalIgnoreCase)
            ? RedirectToAction("Details", "Organizations", new { id = orgId })
            : RedirectToAction(nameof(Index));
}
