using AiReceptionist.Api.Domain;

namespace AiReceptionist.Api.Common;

/// <summary>
/// The organization's bookable people, and the rule for who can take a given slot.
///
/// Capacity used to be one number for the whole business (Organizations.MaxConcurrentAppointments).
/// That cannot express the thing an organization with staff actually needs: two callers may both
/// have 12:00 when James and Alex are both working, and neither may have it when neither of them
/// is. So capacity is worked out per slot, from the people on duty for it.
///
/// "On duty" is the intersection of three things — the employee is active, their weekly hours
/// cover the whole slot, and they are not on leave that day. Whether they are already booked is
/// settled in the database, inside the same transaction as the insert, because that is the only
/// place it can be settled safely against a second call happening at the same moment.
///
/// An organization with no employees on file has <see cref="Enabled"/> false and everything falls
/// back to the old single-capacity rule, so nothing changes for a tenant who never builds a roster.
/// </summary>
public sealed class StaffRoster
{
    private readonly List<Employee> _employees;
    private readonly List<EmployeeTimeOff> _timeOff;

    public StaffRoster(IEnumerable<Employee> employees, IEnumerable<EmployeeTimeOff> timeOff)
    {
        // Only active people are ever bookable, so the filter belongs here rather than at each
        // call site — an inactive employee must not leak into a capacity count anywhere.
        _employees = employees.Where(e => e.IsActive && !e.IsDeleted).ToList();
        _timeOff = timeOff.ToList();
    }

    public static StaffRoster Empty { get; } = new([], []);

    /// <summary>True once the tenant has at least one active employee. False keeps the whole
    /// system on the pre-roster capacity rule.</summary>
    public bool Enabled => _employees.Count > 0;

    public IReadOnlyList<Employee> Employees => _employees;

    /// <summary>Everyone who could take this exact local slot, ignoring existing bookings:
    /// their hours cover it end to end and they are not away that day. An empty result is the
    /// answer to "can anyone see them at noon?" being no — nobody is in, so nothing is booked.</summary>
    public IReadOnlyList<Employee> OnDuty(DateTime startLocal, DateTime endLocal) =>
        _employees.Where(e => !IsAway(e.Id, startLocal.Date) && Covers(e, startLocal, endLocal)).ToList();

    /// <summary>Everyone in at any point on a local date. Used to tell a caller "nobody is
    /// working on Friday" rather than the much less helpful "nothing is free on Friday".</summary>
    public IReadOnlyList<Employee> OnDutyOnDate(DateTime localDate) =>
        _employees.Where(e => !IsAway(e.Id, localDate.Date) && WorksOn(e, localDate)).ToList();

    private bool IsAway(int employeeId, DateTime localDate) =>
        _timeOff.Any(t => t.EmployeeId == employeeId && !t.IsDeleted &&
                          localDate.Date >= t.StartDate.Date && localDate.Date <= t.EndDate.Date);

    /// <summary>Whether the employee's own weekly hours contain the slot.
    ///
    /// No hours of their own means they work whenever the business is open, and the caller has
    /// already confined the slot to the business hours — so there is nothing left to check.
    /// The end is measured from the slot's own date, so a slot running to midnight is 24:00 and
    /// correctly fails to fit inside any window rather than reading as 00:00 and fitting all of them.</summary>
    private static bool Covers(Employee e, DateTime startLocal, DateTime endLocal)
    {
        if (string.IsNullOrWhiteSpace(e.WorkingHoursJson)) return true;

        var window = BusinessHours.For(e.WorkingHoursJson, startLocal.DayOfWeek);
        if (window is null) return false;

        var start = startLocal.TimeOfDay;
        var end = endLocal - startLocal.Date;
        return start >= window.Start && end <= window.End;
    }

    private static bool WorksOn(Employee e, DateTime localDate) =>
        string.IsNullOrWhiteSpace(e.WorkingHoursJson) ||
        BusinessHours.For(e.WorkingHoursJson, localDate.DayOfWeek) is not null;

    /// <summary>How many of the people on duty are still free, given what is already booked over
    /// the slot. Appointments with nobody assigned — taken before the tenant had a roster, or
    /// entered by hand without naming anyone — still occupy one of them: ignoring those would
    /// quietly double-book whoever ends up covering them.</summary>
    public static int FreeCount(IReadOnlyList<Employee> onDuty, IEnumerable<Appointment> overlapping)
    {
        var busy = new HashSet<int>();
        var unassigned = 0;
        foreach (var appointment in overlapping)
        {
            if (appointment.EmployeeId is { } id) busy.Add(id);
            else unassigned++;
        }
        return Math.Max(0, onDuty.Count(e => !busy.Contains(e.Id)) - unassigned);
    }
}
