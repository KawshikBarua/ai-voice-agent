using AiReceptionist.SuperAdmin.Data.Repositories;
using AiReceptionist.SuperAdmin.Models;
using AiReceptionist.SuperAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.SuperAdmin.Controllers;

[Route("")]
public class DashboardController : PlatformControllerBase
{
    private readonly IOrganizationRepository _organizations;
    private readonly IBillingRepository _billing;
    private readonly IPlatformApiClient _api;

    public DashboardController(IOrganizationRepository organizations, IBillingRepository billing,
        IPlatformApiClient api)
    {
        _organizations = organizations;
        _billing = billing;
        _api = api;
    }

    [HttpGet("")]
    [HttpGet("dashboard")]
    public async Task<IActionResult> Index()
    {
        var organizations = await _organizations.ListAsync();
        var collected30d = await _billing.CollectedSinceAsync(DateTime.UtcNow.AddDays(-30));

        // The console stays usable when the tenant API is down — only the Retell key indicator
        // is unknown, and that is a strictly better outcome than a dead dashboard.
        var connection = await _api.GetRetellConnectionAsync();

        return View(new DashboardViewModel
        {
            Organizations = organizations,
            Summary = new PlatformSummary
            {
                TotalOrganizations = organizations.Count,
                ActiveOrganizations = organizations.Count(o => o.IsActive),
                SuspendedOrganizations = organizations.Count(o => !o.IsActive),
                ConnectedAgents = organizations.Count(o => o.IsAgentConnected),
                OverdueOrganizations = organizations.Count(o => o.BillingState == BillingState.Overdue),
                TenantRevenue = organizations.Sum(o => o.Revenue),
                SubscriptionRevenue30d = collected30d,
                TotalCalls = organizations.Sum(o => o.TotalCalls),
                Calls30d = organizations.Sum(o => o.Calls30d),
                TotalAppointments = organizations.Sum(o => o.AppointmentCount),
                RetellApiKeyConfigured = connection?.ApiKeyConfigured ?? false,
            },
        });
    }

    [Route("/home/error")]
    public IActionResult Error() => View();
}
