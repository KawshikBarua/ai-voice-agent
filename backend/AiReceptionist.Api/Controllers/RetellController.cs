using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.Api.Controllers;

/// <summary>
/// Read-only view of the tenant's own Retell agent.
///
/// Connecting, re-syncing and disconnecting are platform operations and live in the separate
/// super admin console (AiReceptionist.SuperAdmin), which drives them through
/// <see cref="PlatformController"/>. Nothing here mutates anything: there is no super admin
/// inside a tenant any more, so a tenant can see whether its AI agent is live but cannot
/// create, change or detach it.
/// </summary>
[ApiController]
[Route("api/v1/retell")]
[Authorize]
public class RetellController : ControllerBase
{
    private readonly ISettingsRepository _settings;
    private readonly IRetellConnectionRepository _connection;
    private readonly ITenantProvider _tenant;

    public RetellController(ISettingsRepository settings, IRetellConnectionRepository connection,
        ITenantProvider tenant)
    {
        _settings = settings;
        _connection = connection;
        _tenant = tenant;
    }

    /// <summary>Connection status for the current tenant's Retell agent. Only booleans, ids and
    /// timestamps — never the platform credential.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var agent = await _settings.GetAgentConfigAsync(_tenant.OrganizationId);
        var connection = await _connection.GetEffectiveAsync();

        return Ok(ApiResponse<object>.Ok(new
        {
            apiKeyConfigured = !string.IsNullOrWhiteSpace(connection.ApiKey),
            connected = !string.IsNullOrWhiteSpace(agent?.RetellAgentId),
            retellAgentId = agent?.RetellAgentId,
            retellPhoneNumber = agent?.RetellPhoneNumber,
            lastSyncedAt = agent?.LastSyncedAt,
        }));
    }
}
