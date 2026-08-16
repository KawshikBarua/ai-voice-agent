using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Domain;
using AiReceptionist.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.Api.Controllers;

[ApiController]
[Route("api/v1/settings")]
[Authorize]
public class SettingsController : ControllerBase
{
    private readonly ISettingsRepository _settings;
    private readonly IHolidayRepository _holidays;
    private readonly ITenantProvider _tenant;
    private readonly IAuditRepository _audit;
    private readonly IRetellSyncQueue _retellSync;

    public SettingsController(ISettingsRepository settings, IHolidayRepository holidays,
        ITenantProvider tenant, IAuditRepository audit, IRetellSyncQueue retellSync)
    {
        _settings = settings;
        _holidays = holidays;
        _tenant = tenant;
        _audit = audit;
        _retellSync = retellSync;
    }

    [HttpGet("organization")]
    public async Task<IActionResult> GetOrganization()
    {
        var org = await _settings.GetOrganizationAsync(_tenant.OrganizationId);
        return org is null
            ? NotFound(ApiResponse<object>.Fail("Organization not found."))
            : Ok(ApiResponse<Organization>.Ok(org));
    }

    [HttpPut("organization")]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> UpdateOrganization(Organization org)
    {
        org.Id = _tenant.OrganizationId;

        // Unparseable hours would be stored happily and then silently fall back to 09:00–17:00
        // every day, so the screen would show one schedule while the agent booked another.
        if (!string.IsNullOrWhiteSpace(org.BusinessHoursJson) && !IsJsonObject(org.BusinessHoursJson))
            return BadRequest(ApiResponse<object>.Fail("Business hours are not in a valid format."));

        await _settings.UpdateOrganizationAsync(org);
        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "OrganizationSettingsUpdated");
        _retellSync.Enqueue(_tenant.OrganizationId);
        return Ok(ApiResponse<Organization>.Ok(org, "Settings updated successfully."));
    }

    private static bool IsJsonObject(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    // ---------- holiday closures ----------

    /// <summary>Dates the business is shut. These override the weekly hours: the agent will not
    /// offer or book a slot on them, and they are stated in the prompt and knowledge base.</summary>
    [HttpGet("holidays")]
    public async Task<IActionResult> GetHolidays() =>
        Ok(ApiResponse<IEnumerable<Holiday>>.Ok(await _holidays.ListAsync(_tenant.OrganizationId)));

    [HttpPost("holidays")]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> AddHoliday(Holiday holiday)
    {
        if (holiday.Date == default)
            return BadRequest(ApiResponse<object>.Fail("Pick the date the business is closed."));

        holiday.OrganizationId = _tenant.OrganizationId;
        holiday.Name = string.IsNullOrWhiteSpace(holiday.Name) ? "Closed" : holiday.Name.Trim();
        holiday.Id = await _holidays.UpsertAsync(holiday);

        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "HolidayAdded",
            $"{holiday.Date:yyyy-MM-dd} {holiday.Name}");
        _retellSync.Enqueue(_tenant.OrganizationId);
        return Ok(ApiResponse<Holiday>.Ok(holiday, "Closure saved."));
    }

    [HttpDelete("holidays/{id:int}")]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> DeleteHoliday(int id)
    {
        await _holidays.DeleteAsync(_tenant.OrganizationId, id);
        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "HolidayRemoved", $"HolidayId={id}");
        _retellSync.Enqueue(_tenant.OrganizationId);
        return Ok(ApiResponse<object>.Ok(new { }, "Closure removed."));
    }

    [HttpGet("agent")]
    public async Task<IActionResult> GetAgent() =>
        Ok(ApiResponse<AgentConfig?>.Ok(await _settings.GetAgentConfigAsync(_tenant.OrganizationId)));

    [HttpPut("agent")]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> UpdateAgent(AgentConfig config)
    {
        config.OrganizationId = _tenant.OrganizationId;

        var existing = await _settings.GetAgentConfigAsync(_tenant.OrganizationId);

        // Preserve everything the tenant does not own. The two phone numbers are platform
        // property: both are set from the super admin console (PlatformController), because the
        // Retell number decides which agent answers a number on the shared Retell account and the
        // transfer number is dialled by Retell on a cold transfer. Taking them from the stored row
        // rather than the request means a hand-crafted PUT cannot change either — the tenant UI
        // showing them as read-only is presentation, this is the actual rule.
        if (existing is not null)
        {
            config.TransferNumber = existing.TransferNumber;
            config.RetellPhoneNumber = existing.RetellPhoneNumber;
            config.RetellAgentId ??= existing.RetellAgentId;
            config.RetellLlmId ??= existing.RetellLlmId;
            config.LastSyncedAt = existing.LastSyncedAt;
        }
        else
        {
            config.TransferNumber = null;
            config.RetellPhoneNumber = null;
        }

        await _settings.UpsertAgentConfigAsync(config);
        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "AiConfigurationChanged");
        _retellSync.Enqueue(_tenant.OrganizationId);
        return Ok(ApiResponse<AgentConfig>.Ok(config, "AI agent configuration updated."));
    }
}
