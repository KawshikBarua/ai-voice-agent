using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Domain;
using AiReceptionist.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.Api.Controllers;

/// <summary>
/// The team roster — the people a caller can be booked with.
///
/// This is what decides how many appointments a business can run at the same time: the AI offers a
/// slot when at least one employee is on duty and free for it, so two employees make two 12:00
/// bookings possible and an empty rota makes none. Changes are pushed to the agent (the prompt
/// states the size of the team) the same way the hours and closures are.
/// </summary>
[ApiController]
[Route("api/v1/employees")]
[Authorize]
public class EmployeesController : ControllerBase
{
    private readonly IEmployeeRepository _employees;
    private readonly ITenantProvider _tenant;
    private readonly IAuditRepository _audit;
    private readonly IRetellSyncQueue _retellSync;

    public EmployeesController(IEmployeeRepository employees, ITenantProvider tenant,
        IAuditRepository audit, IRetellSyncQueue retellSync)
    {
        _employees = employees;
        _tenant = tenant;
        _audit = audit;
        _retellSync = retellSync;
    }

    [HttpGet]
    public async Task<IActionResult> List() =>
        Ok(ApiResponse<IEnumerable<Employee>>.Ok(await _employees.ListAsync(_tenant.OrganizationId)));

    [HttpPost]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> Create(Employee employee)
    {
        if (Validate(employee) is { } problem) return BadRequest(ApiResponse<object>.Fail(problem));

        employee.OrganizationId = _tenant.OrganizationId;
        employee.Name = employee.Name.Trim();
        employee.Id = await _employees.CreateAsync(employee);

        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "EmployeeAdded", employee.Name);
        _retellSync.Enqueue(_tenant.OrganizationId);
        return Ok(ApiResponse<Employee>.Ok(employee, $"{employee.Name} added to the team."));
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> Update(int id, Employee employee)
    {
        if (Validate(employee) is { } problem) return BadRequest(ApiResponse<object>.Fail(problem));

        employee.Id = id;
        employee.OrganizationId = _tenant.OrganizationId;
        employee.Name = employee.Name.Trim();
        if (!await _employees.UpdateAsync(employee))
            return NotFound(ApiResponse<object>.Fail("Team member not found."));

        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "EmployeeUpdated", employee.Name);
        _retellSync.Enqueue(_tenant.OrganizationId);
        return Ok(ApiResponse<Employee>.Ok(employee, "Team member updated."));
    }

    /// <summary>Removes someone from the roster. Refused while they still have appointments to
    /// come: those callers are expecting that person, and freeing the slots by deleting them is
    /// not the same thing as cancelling them. Deactivating instead keeps the bookings and stops
    /// any new ones.</summary>
    [HttpDelete("{id:int}")]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> Delete(int id)
    {
        var employee = await _employees.GetAsync(_tenant.OrganizationId, id);
        if (employee is null)
            return NotFound(ApiResponse<object>.Fail("Team member not found."));

        if (!await _employees.DeleteAsync(_tenant.OrganizationId, id))
            return Conflict(ApiResponse<object>.Fail(
                $"{employee.Name} still has {employee.UpcomingAppointments} upcoming appointment" +
                $"{(employee.UpcomingAppointments == 1 ? "" : "s")}. Reassign or cancel those first, " +
                "or mark them inactive to stop new bookings."));

        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "EmployeeRemoved", employee.Name);
        _retellSync.Enqueue(_tenant.OrganizationId);
        return Ok(ApiResponse<object>.Ok(new { }, $"{employee.Name} removed from the team."));
    }

    // ---------- time off ----------

    /// <summary>Days a team member is away. The AI treats them as absent on those dates, so a slot
    /// only survives if somebody else can cover it.</summary>
    [HttpGet("time-off")]
    public async Task<IActionResult> ListTimeOff() =>
        Ok(ApiResponse<IEnumerable<EmployeeTimeOff>>.Ok(
            await _employees.ListTimeOffAsync(_tenant.OrganizationId, DateTime.UtcNow.Date.AddDays(-30))));

    [HttpPost("{id:int}/time-off")]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> AddTimeOff(int id, EmployeeTimeOff timeOff)
    {
        if (await _employees.GetAsync(_tenant.OrganizationId, id) is null)
            return NotFound(ApiResponse<object>.Fail("Team member not found."));
        if (timeOff.StartDate == default)
            return BadRequest(ApiResponse<object>.Fail("Pick the first day they are away."));

        // A blank end means one day off, which is what an owner filling in "Friday" expects.
        if (timeOff.EndDate == default) timeOff.EndDate = timeOff.StartDate;
        if (timeOff.EndDate.Date < timeOff.StartDate.Date)
            return BadRequest(ApiResponse<object>.Fail("The last day away cannot be before the first."));

        timeOff.OrganizationId = _tenant.OrganizationId;
        timeOff.EmployeeId = id;
        timeOff.Reason = string.IsNullOrWhiteSpace(timeOff.Reason) ? null : timeOff.Reason.Trim();
        timeOff.Id = await _employees.AddTimeOffAsync(timeOff);

        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "EmployeeTimeOffAdded",
            $"EmployeeId={id} {timeOff.StartDate:yyyy-MM-dd}..{timeOff.EndDate:yyyy-MM-dd}");
        return Ok(ApiResponse<EmployeeTimeOff>.Ok(timeOff, "Time off saved."));
    }

    [HttpDelete("time-off/{id:int}")]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> DeleteTimeOff(int id)
    {
        await _employees.DeleteTimeOffAsync(_tenant.OrganizationId, id);
        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "EmployeeTimeOffRemoved", $"Id={id}");
        return Ok(ApiResponse<object>.Ok(new { }, "Time off removed."));
    }

    /// <summary>Unreadable working hours would be stored happily and then read as "works whenever
    /// the business is open", so the screen would show one rota while the AI booked another.</summary>
    private static string? Validate(Employee employee)
    {
        if (string.IsNullOrWhiteSpace(employee.Name))
            return "A name is required.";
        if (!string.IsNullOrWhiteSpace(employee.WorkingHoursJson) && !IsJsonObject(employee.WorkingHoursJson))
            return "Working hours are not in a valid format.";
        return null;
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
}
