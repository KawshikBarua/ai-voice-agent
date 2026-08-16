using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Domain;
using AiReceptionist.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.Api.Controllers;

[ApiController]
[Route("api/v1/calls")]
[Authorize]
public class CallsController : ControllerBase
{
    private readonly ICallRepository _calls;
    private readonly IRetellService _retell;
    private readonly ITenantProvider _tenant;
    private readonly ICallActionSuggestionRepository _suggestions;
    private readonly ISettingsRepository _settings;
    private readonly IAiToolsRepository _ai;
    private readonly ICustomerRepository _customers;
    private readonly IAppointmentRepository _appointments;

    public CallsController(ICallRepository calls, IRetellService retell, ITenantProvider tenant,
        ICallActionSuggestionRepository suggestions, ISettingsRepository settings, IAiToolsRepository ai,
        ICustomerRepository customers, IAppointmentRepository appointments)
    {
        _calls = calls;
        _retell = retell;
        _tenant = tenant;
        _suggestions = suggestions;
        _settings = settings;
        _ai = ai;
        _customers = customers;
        _appointments = appointments;
    }

    /// <summary>Imports any calls Retell recorded that never reached us via webhook
    /// (tunnel down, API restarting, webhook retry exhausted).</summary>
    [HttpPost("sync")]
    [Authorize(Roles = Roles.Staff)]
    public async Task<IActionResult> Sync()
    {
        var imported = await _retell.BackfillCallsAsync(_tenant.OrganizationId);
        return Ok(ApiResponse<object>.Ok(new { imported },
            imported == 0 ? "No new calls found." : $"Imported {imported} call(s) from Retell."));
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20) =>
        Ok(ApiResponse<PagedResult<CallLog>>.Ok(
            await _calls.ListAsync(_tenant.OrganizationId, page, Math.Clamp(pageSize, 1, 100))));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var call = await _calls.GetAsync(_tenant.OrganizationId, id);
        return call is null
            ? NotFound(ApiResponse<object>.Fail("Call not found."))
            : Ok(ApiResponse<CallLog>.Ok(call));
    }

    // ---------- Transferred-call action suggestions ----------

    /// <summary>Pending Book/Cancel/Reschedule actions inferred from transferred calls,
    /// awaiting staff confirmation.</summary>
    [HttpGet("suggestions")]
    public async Task<IActionResult> Suggestions() =>
        Ok(ApiResponse<IEnumerable<CallActionSuggestion>>.Ok(
            await _suggestions.GetPendingByOrgAsync(_tenant.OrganizationId)));

    public class ConfirmSuggestionRequest
    {
        public string? Action { get; set; }
        public string? Name { get; set; }
        public string? Phone { get; set; }
        public string? Service { get; set; }
        /// <summary>Local wall-clock start, "yyyy-MM-ddTHH:mm".</summary>
        public string? StartAtLocal { get; set; }
    }

    /// <summary>Executes a suggested action after staff review (staff may correct the
    /// fields first). Reuses the same tenant-scoped, capacity-checked booking logic the
    /// AI uses during live calls.</summary>
    [HttpPost("suggestions/{id:int}/confirm")]
    [Authorize(Roles = Roles.Staff)]
    public async Task<IActionResult> ConfirmSuggestion(int id, [FromBody] ConfirmSuggestionRequest? req)
    {
        var orgId = _tenant.OrganizationId;
        var suggestion = await _suggestions.GetAsync(orgId, id);
        if (suggestion is null) return NotFound(ApiResponse<object>.Fail("Suggestion not found."));
        if (suggestion.Status != "Pending")
            return BadRequest(ApiResponse<object>.Fail($"Suggestion already {suggestion.Status.ToLowerInvariant()}."));

        req ??= new ConfirmSuggestionRequest();
        var action = (req.Action ?? suggestion.Action).Trim();
        var name = Coalesce(req.Name, suggestion.CustomerName);
        var phone = Coalesce(req.Phone, suggestion.Phone);
        var serviceName = Coalesce(req.Service, suggestion.ServiceName);
        var startLocal = ParseLocal(req.StartAtLocal) ?? suggestion.StartAtLocal;

        var org = await _settings.GetOrganizationAsync(orgId);
        if (org is null) return NotFound(ApiResponse<object>.Fail("Organization not found."));
        var tz = TenantTime.Resolve(org.Timezone);

        return action.ToLowerInvariant() switch
        {
            "book" => await ConfirmBookAsync(orgId, tz, id, name, phone, serviceName, startLocal),
            "cancel" => await ConfirmCancelAsync(orgId, tz, id, phone, serviceName, startLocal),
            "reschedule" => await ConfirmRescheduleAsync(orgId, tz, id, phone, serviceName, startLocal),
            _ => BadRequest(ApiResponse<object>.Fail($"Cannot confirm action '{action}'.")),
        };
    }

    [HttpPost("suggestions/{id:int}/dismiss")]
    [Authorize(Roles = Roles.Staff)]
    public async Task<IActionResult> DismissSuggestion(int id)
    {
        var orgId = _tenant.OrganizationId;
        var suggestion = await _suggestions.GetAsync(orgId, id);
        if (suggestion is null) return NotFound(ApiResponse<object>.Fail("Suggestion not found."));
        if (suggestion.Status != "Pending")
            return BadRequest(ApiResponse<object>.Fail($"Suggestion already {suggestion.Status.ToLowerInvariant()}."));

        await _suggestions.ResolveAsync(orgId, id, "Dismissed", _tenant.UserId, null);
        return Ok(ApiResponse<object>.Ok(new { id }, "Suggestion dismissed."));
    }

    // ---------- confirm handlers ----------

    private async Task<IActionResult> ConfirmBookAsync(
        int orgId, TimeZoneInfo tz, int suggestionId, string? name, string? phone, string? serviceName, DateTime? startLocal)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(phone) ||
            string.IsNullOrWhiteSpace(serviceName) || startLocal is null)
            return BadRequest(ApiResponse<object>.Fail("Booking needs a name, phone, service and start time."));

        var service = await _ai.FindServiceByNameAsync(orgId, serviceName);
        if (service is null)
            return BadRequest(ApiResponse<object>.Fail($"No service matching '{serviceName}'."));

        var startUtc = TenantTime.ToUtc(startLocal.Value, tz);
        var endUtc = startUtc.AddMinutes(service.DurationMinutes);

        var customer = await _customers.FindReturningAsync(orgId, phone, null, name);
        var customerId = customer?.Id ?? await _customers.CreateAsync(new Customer
        {
            OrganizationId = orgId, Name = name!, Phone = phone!,
        });

        var appointmentId = await _appointments.TryCreateAsync(new Appointment
        {
            OrganizationId = orgId,
            CustomerId = customerId,
            ServiceId = service.Id,
            StartAt = startUtc,
            EndAt = endUtc,
            Status = AppointmentStatus.Scheduled,
            PaymentStatus = "Unpaid",
            Amount = service.MinPrice,
            IsEmergency = service.IsEmergency,
            Notes = "[AI] Confirmed from transferred call",
        });
        if (appointmentId is null)
            return Conflict(ApiResponse<object>.Fail("That time is no longer available. Pick another slot."));

        await _customers.AddTimelineEventAsync(new TimelineEvent
        {
            OrganizationId = orgId, CustomerId = customerId,
            EventType = "AppointmentBooked", Source = "User", UserId = _tenant.UserId,
            Notes = $"{service.Name} on {startLocal:yyyy-MM-dd HH:mm} (from transferred call)",
        });

        await _suggestions.ResolveAsync(orgId, suggestionId, "Confirmed", _tenant.UserId, appointmentId);
        return Ok(ApiResponse<object>.Ok(new { appointmentId }, "Appointment booked."));
    }

    private async Task<IActionResult> ConfirmCancelAsync(
        int orgId, TimeZoneInfo tz, int suggestionId, string? phone, string? serviceName, DateTime? dateLocal)
    {
        var (appt, error) = await ResolveCallerAppointmentAsync(orgId, tz, phone, serviceName, dateLocal);
        if (appt is null) return BadRequest(ApiResponse<object>.Fail(error!));

        await _appointments.UpdateStatusAsync(orgId, appt.Id, AppointmentStatus.Cancelled);
        await _customers.AddTimelineEventAsync(new TimelineEvent
        {
            OrganizationId = orgId, CustomerId = appt.CustomerId,
            EventType = "AppointmentCancelled", Source = "User", UserId = _tenant.UserId,
            Notes = $"{appt.ServiceName} (cancelled from transferred call)",
        });

        await _suggestions.ResolveAsync(orgId, suggestionId, "Confirmed", _tenant.UserId, appt.Id);
        return Ok(ApiResponse<object>.Ok(new { appointmentId = appt.Id }, "Appointment cancelled."));
    }

    private async Task<IActionResult> ConfirmRescheduleAsync(
        int orgId, TimeZoneInfo tz, int suggestionId, string? phone, string? serviceName, DateTime? startLocal)
    {
        if (startLocal is null)
            return BadRequest(ApiResponse<object>.Fail("Rescheduling needs a new start time."));

        var (appt, error) = await ResolveCallerAppointmentAsync(orgId, tz, phone, serviceName, null);
        if (appt is null) return BadRequest(ApiResponse<object>.Fail(error!));

        var newStartUtc = TenantTime.ToUtc(startLocal.Value, tz);
        var newEndUtc = newStartUtc + (appt.EndAt - appt.StartAt);

        if (!await _appointments.TryRescheduleAsync(orgId, appt.Id, newStartUtc, newEndUtc))
            return Conflict(ApiResponse<object>.Fail("That time is not available. Pick another slot."));

        await _customers.AddTimelineEventAsync(new TimelineEvent
        {
            OrganizationId = orgId, CustomerId = appt.CustomerId,
            EventType = "AppointmentRescheduled", Source = "User", UserId = _tenant.UserId,
            Notes = $"Moved to {startLocal:yyyy-MM-dd HH:mm} (from transferred call)",
        });

        await _suggestions.ResolveAsync(orgId, suggestionId, "Confirmed", _tenant.UserId, appt.Id);
        return Ok(ApiResponse<object>.Ok(new { appointmentId = appt.Id }, "Appointment rescheduled."));
    }

    /// <summary>Finds the single upcoming appointment the caller means, disambiguated by
    /// phone, and optionally service/date. Returns an error message when 0 or many match.</summary>
    private async Task<(Appointment? appt, string? error)> ResolveCallerAppointmentAsync(
        int orgId, TimeZoneInfo tz, string? phone, string? serviceName, DateTime? dateLocal)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return (null, "A phone number is required to find the appointment.");

        var customer = await _customers.FindReturningAsync(orgId, phone, null, null);
        if (customer is null) return (null, "No customer found with that phone number.");

        DateTime? fromUtc = null, toUtc = null;
        if (dateLocal is { } d)
        {
            fromUtc = TenantTime.ToUtc(d.Date, tz);
            toUtc = TenantTime.ToUtc(d.Date.AddDays(1), tz);
        }

        var matches = (await _ai.UpcomingAppointmentsAsync(orgId, customer.Id,
            fromUtc, toUtc, string.IsNullOrWhiteSpace(serviceName) ? null : serviceName)).ToList();

        return matches.Count switch
        {
            0 => (null, $"{customer.Name} has no matching upcoming appointment."),
            1 => (matches[0], null),
            _ => (null, "Multiple matching appointments — open the customer to pick the right one manually."),
        };
    }

    private static string? Coalesce(string? primary, string? fallback) =>
        !string.IsNullOrWhiteSpace(primary) ? primary.Trim() : fallback;

    private static DateTime? ParseLocal(string? value) =>
        DateTime.TryParse(value, out var d) ? DateTime.SpecifyKind(d, DateTimeKind.Unspecified) : null;
}
