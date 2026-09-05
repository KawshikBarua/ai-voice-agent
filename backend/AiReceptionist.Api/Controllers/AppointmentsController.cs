using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.Api.Controllers;

public record RescheduleRequest(DateTime StartAt, DateTime EndAt);

[ApiController]
[Route("api/v1/appointments")]
[Authorize]
public class AppointmentsController : ControllerBase
{
    private readonly IAppointmentRepository _appointments;
    private readonly ICustomerRepository _customers;
    private readonly IEmployeeRepository _employees;
    private readonly ISettingsRepository _settings;
    private readonly ITenantProvider _tenant;
    private readonly IAuditRepository _audit;

    public AppointmentsController(IAppointmentRepository appointments, ICustomerRepository customers,
        IEmployeeRepository employees, ISettingsRepository settings, ITenantProvider tenant,
        IAuditRepository audit)
    {
        _appointments = appointments;
        _customers = customers;
        _employees = employees;
        _settings = settings;
        _tenant = tenant;
        _audit = audit;
    }

    /// <summary>
    /// Who can take a slot booked by hand from the dashboard, under the same roster rules the AI
    /// works to — a receptionist typing a booking in and the agent taking one over the phone must
    /// not be able to reach different answers about the same slot.
    ///
    /// Returns null once the organization has no roster, which puts the write back on the older
    /// business-wide capacity rule. The message is the one shown in the dialog, so it says what to
    /// do about it rather than just refusing.
    /// </summary>
    private async Task<(SlotAssignment? Assignment, string? Refusal)> ResolveStaffAsync(
        DateTime startAtUtc, DateTime endAtUtc, int? requestedEmployeeId)
    {
        var org = await _settings.GetOrganizationAsync(_tenant.OrganizationId);
        var tz = TenantTime.Resolve(org?.Timezone);
        var startLocal = TenantTime.ToLocal(startAtUtc, tz);
        var endLocal = TenantTime.ToLocal(endAtUtc, tz);

        var roster = await _employees.LoadRosterAsync(_tenant.OrganizationId, startLocal.Date, startLocal.Date);
        if (!roster.Enabled) return (null, null);

        var onDuty = roster.OnDuty(startLocal, endLocal);
        if (onDuty.Count == 0)
            return (null, $"No one is working at {startLocal:HH:mm} on {startLocal:dddd d MMMM yyyy}. " +
                          "Check the team's working hours and time off, or pick another time.");

        var assignment = SlotAssignment.Any(onDuty.Select(e => e.Id));
        if (requestedEmployeeId is not { } requested) return (assignment, null);

        var person = roster.Employees.FirstOrDefault(e => e.Id == requested);
        return onDuty.Any(e => e.Id == requested)
            ? (assignment.Only(requested), null)
            : (null, $"{person?.Name ?? "That team member"} is not working at " +
                     $"{startLocal:HH:mm} on {startLocal:dddd d MMMM yyyy}.");
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20) =>
        Ok(ApiResponse<PagedResult<Appointment>>.Ok(
            await _appointments.ListAsync(_tenant.OrganizationId, from, to, status, page, Math.Clamp(pageSize, 1, 100))));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var appt = await _appointments.GetAsync(_tenant.OrganizationId, id);
        return appt is null
            ? NotFound(ApiResponse<object>.Fail("Appointment not found."))
            : Ok(ApiResponse<Appointment>.Ok(appt));
    }

    [HttpPost]
    [Authorize(Roles = Roles.Staff)]
    public async Task<IActionResult> Create(Appointment appt)
    {
        appt.OrganizationId = _tenant.OrganizationId;
        if (appt.EndAt <= appt.StartAt)
            return BadRequest(ApiResponse<object>.Fail("EndAt must be after StartAt."));

        var (assignment, refusal) = await ResolveStaffAsync(appt.StartAt, appt.EndAt, appt.EmployeeId);
        if (refusal is not null) return Conflict(ApiResponse<object>.Fail(refusal));

        // With a roster the employee is chosen inside the write, from ids this tenant owns. Without
        // one there is nobody to assign, so a value posted here is dropped rather than stored — it
        // was never checked against anything.
        if (assignment is null) appt.EmployeeId = null;

        var created = await _appointments.TryCreateAsync(appt, assignment);
        if (created is null)
            return Conflict(ApiResponse<object>.Fail("The selected time slot is not available."));
        var id = created.Value;
        await _customers.AddTimelineEventAsync(new TimelineEvent
        {
            OrganizationId = _tenant.OrganizationId,
            CustomerId = appt.CustomerId,
            EventType = "AppointmentBooked",
            Source = "User",
            UserId = _tenant.UserId,
            Notes = appt.Notes
        });
        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "AppointmentCreated", $"AppointmentId={id}");
        appt.Id = id;
        return Ok(ApiResponse<Appointment>.Ok(appt, "Appointment created successfully."));
    }

    [HttpPost("{id:int}/cancel")]
    [Authorize(Roles = Roles.Staff)]
    public async Task<IActionResult> Cancel(int id)
    {
        await _appointments.UpdateStatusAsync(_tenant.OrganizationId, id, AppointmentStatus.Cancelled);
        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "AppointmentCancelled", $"AppointmentId={id}");
        return Ok(ApiResponse<object>.Ok(new { }, "Appointment cancelled."));
    }

    [HttpPost("{id:int}/confirm")]
    [Authorize(Roles = Roles.Staff)]
    public async Task<IActionResult> Confirm(int id)
    {
        await _appointments.UpdateStatusAsync(_tenant.OrganizationId, id, AppointmentStatus.Confirmed);
        return Ok(ApiResponse<object>.Ok(new { }, "Appointment confirmed."));
    }

    /// <summary>Marks payment received. Only allowed once the appointment is confirmed —
    /// an unconfirmed booking should not be taking money.</summary>
    [HttpPost("{id:int}/mark-paid")]
    [Authorize(Roles = Roles.Staff)]
    public async Task<IActionResult> MarkPaid(int id)
    {
        var appt = await _appointments.GetAsync(_tenant.OrganizationId, id);
        if (appt is null)
            return NotFound(ApiResponse<object>.Fail("Appointment not found."));
        if (appt.Status is not (AppointmentStatus.Confirmed or AppointmentStatus.Completed))
            return BadRequest(ApiResponse<object>.Fail(
                "Confirm the appointment before marking it paid."));
        if (appt.PaymentStatus == "Paid")
            return Ok(ApiResponse<object>.Ok(new { }, "Already marked as paid."));

        await _appointments.UpdatePaymentStatusAsync(_tenant.OrganizationId, id, "Paid");
        await _customers.AddTimelineEventAsync(new TimelineEvent
        {
            OrganizationId = _tenant.OrganizationId,
            CustomerId = appt.CustomerId,
            EventType = "PaymentCompleted",
            Source = "User",
            UserId = _tenant.UserId,
            Notes = $"{appt.ServiceName} — {appt.Amount:0.##}",
        });
        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "AppointmentPaid", $"AppointmentId={id}");
        return Ok(ApiResponse<object>.Ok(new { }, "Payment recorded."));
    }

    /// <summary>Marks the appointment completed and updates the customer's visit history.</summary>
    [HttpPost("{id:int}/complete")]
    [Authorize(Roles = Roles.Staff)]
    public async Task<IActionResult> Complete(int id)
    {
        await _appointments.UpdateStatusAsync(_tenant.OrganizationId, id, AppointmentStatus.Completed);
        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "AppointmentCompleted", $"AppointmentId={id}");
        return Ok(ApiResponse<object>.Ok(new { }, "Appointment completed."));
    }

    [HttpPost("{id:int}/reschedule")]
    [Authorize(Roles = Roles.Staff)]
    public async Task<IActionResult> Reschedule(int id, RescheduleRequest request)
    {
        if (request.EndAt <= request.StartAt)
            return BadRequest(ApiResponse<object>.Fail("EndAt must be after StartAt."));

        var existing = await _appointments.GetAsync(_tenant.OrganizationId, id);
        if (existing is null)
            return NotFound(ApiResponse<object>.Fail("Appointment not found."));

        var (assignment, refusal) = await ResolveStaffAsync(request.StartAt, request.EndAt, null);
        if (refusal is not null) return Conflict(ApiResponse<object>.Fail(refusal));

        // Whoever has it keeps it when they are free at the new time; otherwise it moves to
        // someone who is, rather than failing outright.
        if (!await _appointments.TryRescheduleAsync(_tenant.OrganizationId, id, request.StartAt, request.EndAt,
                assignment?.Preferring(existing.EmployeeId)))
            return Conflict(ApiResponse<object>.Fail("The selected time slot is not available."));

        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "AppointmentRescheduled", $"AppointmentId={id}");
        return Ok(ApiResponse<object>.Ok(new { }, "Appointment rescheduled."));
    }
}
