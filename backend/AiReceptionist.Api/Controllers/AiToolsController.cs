using System.Globalization;
using System.Text.Json;
using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Domain;
using AiReceptionist.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.Api.Controllers;

/// <summary>
/// Custom-function endpoints invoked by the Retell agent DURING live calls (SRS §15, §19).
/// Retell POSTs {call, name, args}; the JSON we return is read by the LLM and spoken to the caller.
/// Authenticated via Retell's X-Retell-Signature (HMAC of the raw body with the API key).
/// All times spoken to callers are in the tenant's timezone; storage is UTC.
/// </summary>
[ApiController]
[Route("api/v1/ai/tools/{orgId:int}")]
[AllowAnonymous]
public class AiToolsController : ControllerBase
{
    private readonly IAiToolsRepository _ai;
    private readonly ICustomerRepository _customers;
    private readonly IAppointmentRepository _appointments;
    private readonly IEmployeeRepository _employees;
    private readonly INotificationService _notifications;
    private readonly ISettingsRepository _settings;
    private readonly IHolidayRepository _holidays;
    private readonly IRetellConnectionRepository _connection;
    private readonly ICallEntitlementService _entitlement;
    private readonly IHostEnvironment _env;
    private readonly ILogger<AiToolsController> _logger;

    public AiToolsController(IAiToolsRepository ai, ICustomerRepository customers,
        IAppointmentRepository appointments, IEmployeeRepository employees,
        INotificationService notifications, ISettingsRepository settings, IHolidayRepository holidays,
        IRetellConnectionRepository connection, ICallEntitlementService entitlement,
        IHostEnvironment env, ILogger<AiToolsController> logger)
    {
        _entitlement = entitlement;
        _ai = ai;
        _customers = customers;
        _appointments = appointments;
        _employees = employees;
        _notifications = notifications;
        _settings = settings;
        _holidays = holidays;
        _connection = connection;
        _env = env;
        _logger = logger;
    }

    // ---------- plumbing ----------

    private sealed record ToolContext(JsonElement Args, Organization Org, TimeZoneInfo Tz);

    private async Task<(ToolContext? ctx, IActionResult? error)> ReadAsync(int orgId)
    {
        using var reader = new StreamReader(Request.Body);
        var raw = await reader.ReadToEndAsync();

        // Tool calls are authenticated by the ?k= token in the registered URL
        // (Retell does not sign custom-function calls); a signature is still accepted.
        var signature = Request.Headers["X-Retell-Signature"].ToString();
        var token = Request.Query["k"].ToString();
        var connection = await _connection.GetEffectiveAsync();
        if (!RetellSignature.AcceptToolCall(connection.ApiKey, connection.VerifySignature, orgId, raw,
                signature, token, allowUnverified: _env.IsDevelopment()))
        {
            _logger.LogWarning(
                "Rejected AI tool call for org {OrgId} on {Path}: tokenPresent={TokenPresent}, signaturePresent={SigPresent}",
                orgId, Request.Path, !string.IsNullOrWhiteSpace(token), !string.IsNullOrWhiteSpace(signature));
            return (null, Unauthorized(new { result = "Unauthorized." }));
        }

        var org = await _settings.GetOrganizationAsync(orgId);
        if (org is null)
            return (null, NotFound(new { result = "Unknown organization." }));

        // An organization that is not entitled to be answered must not be able to book, cancel or
        // read customer data through its agent. That covers a suspended account and one the
        // operator has stopped by hand, one that is on no plan at all or has stopped paying, and one
        // whose free trial has run out — see ICallEntitlementService for why they are one decision.
        //
        // This is the second line, not the first: the inbound webhook refuses the call before it is
        // ever set up. It stays because that refusal depends on Retell honouring it, and a call
        // that is somehow already in progress must still not be able to touch the data.
        //
        // The refusal is phrased for the agent to say out loud, because this reply is spoken to a
        // live caller — who is a member of the public and has no business hearing about a bill.
        var entitlement = await _entitlement.EvaluateAsync(orgId);
        if (!entitlement.IsAllowed)
        {
            _logger.LogWarning("Refused AI tool call for organization {OrgId} on {Path}: {Reason}.",
                orgId, Request.Path, entitlement.Explain());
            return (null, StatusCode(StatusCodes.Status403Forbidden,
                new { result = CallEntitlement.SpokenRefusal }));
        }

        var root = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw).RootElement;
        // Retell wraps parameters in "args"; accept a bare object too for manual testing.
        var args = root.TryGetProperty("args", out var a) ? a : root;
        return (new ToolContext(args, org, TenantTime.Resolve(org.Timezone)), null);
    }

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>Returns a reply for the agent to read, with the tenant's current local date and
    /// time attached to it. Dynamic variables are resolved once, when the call starts, and can be
    /// lost to a Retell-side fault; this is our own channel and cannot be. Restating the clock on
    /// every tool reply re-anchors the agent several times a call, so a long conversation cannot
    /// drift back to the date the model assumes from its training data.</summary>
    private IActionResult Speak(object payload, ToolContext? ctx = null)
    {
        if (ctx is null) return Ok(payload);

        var body = JsonSerializer.SerializeToNode(payload)?.AsObject();
        if (body is null) return Ok(payload);

        body["current_datetime"] = $"{TenantTime.Describe(TenantTime.NowLocal(ctx.Tz))} ({ctx.Org.Timezone})";
        return Ok(body);
    }

    private static string SpokenTime(DateTime utc, TimeZoneInfo tz) =>
        TenantTime.ToLocal(utc, tz).ToString("dddd, MMMM d 'at' HH:mm");

    /// <summary>The bookable window for one local date: the weekday's business hours, unless the
    /// date is a marked closure, which shuts the day regardless of the weekly schedule. When there
    /// is no window the second value is the sentence for the agent to say — the caller hears a
    /// reason ("closed for Christmas Day") rather than a bare refusal.</summary>
    private async Task<(DayWindow? Window, string? Closed)> ResolveDayAsync(int orgId, Organization org, DateTime local)
    {
        if (await _holidays.FindAsync(orgId, local.Date) is { } holiday)
            return (null, $"The business is closed all day on {local:yyyy-MM-dd} for {holiday.Name}. " +
                          "Tell the caller and offer another day.");

        var window = BusinessHours.For(org.BusinessHoursJson, local.DayOfWeek);
        return window is null
            ? (null, $"The business is closed on {local:dddd}s. Offer another day.")
            : (window, null);
    }

    /// <summary>
    /// Announces something the AI did while nobody was watching, and never lets that get in the
    /// way of the call.
    ///
    /// The caller is on the phone at this point and the appointment is already committed. If the
    /// push service is unreachable, or notifications were never set up, the right outcome is a log
    /// line and a booking that still stands — not an apology to someone whose booking actually
    /// worked. The alert row is written first regardless, so the dashboard queue is complete even
    /// when delivery is not.
    /// </summary>
    private async Task AnnounceAsync(Alert alert)
    {
        try
        {
            await _notifications.RaiseAsync(alert);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not raise {Kind} alert for organization {OrgId}.",
                alert.Kind, alert.OrganizationId);
        }
    }

    /// <summary>Something happening today cannot wait for the next time somebody signs in, so it
    /// is chased like an emergency is. Anything further out is sent once and left alone.</summary>
    private static string SeverityFor(DateTime startLocal, DateTime nowLocal) =>
        startLocal.Date == nowLocal.Date ? AlertSeverity.Urgent : AlertSeverity.Info;

    /// <summary>Loose name check so callers can't cancel strangers' appointments with just a
    /// phone number: at least one token (≥3 chars) of the provided name must appear in the
    /// stored customer name.</summary>
    private static bool NameMatches(string? provided, string stored)
    {
        if (string.IsNullOrWhiteSpace(provided)) return false;
        var storedLower = stored.ToLowerInvariant();
        return provided.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(t => t.Length >= 3 && storedLower.Contains(t));
    }

    // ---------- tools ----------

    [HttpPost("identify_customer")]
    public async Task<IActionResult> IdentifyCustomer(int orgId)
    {
        var (ctx, error) = await ReadAsync(orgId);
        if (ctx is null) return error!;

        var customer = await _customers.FindReturningAsync(orgId,
            Str(ctx.Args, "phone"), Str(ctx.Args, "email"), Str(ctx.Args, "name"));
        if (customer is null)
            return Speak(new { result = "No existing customer found — treat the caller as a new customer." }, ctx);

        return Speak(new
        {
            result = $"Returning customer: {customer.Name}, {customer.TotalVisits} previous visits.",
            customer = new { customer.Name, customer.Phone, customer.TotalVisits, lastVisit = customer.LastVisit?.ToString("yyyy-MM-dd") },
        }, ctx);
    }

    [HttpPost("check_availability")]
    public async Task<IActionResult> CheckAvailability(int orgId)
    {
        var (ctx, error) = await ReadAsync(orgId);
        if (ctx is null) return error!;

        var nowLocal = TenantTime.NowLocal(ctx.Tz);
        if (ResolveRequestedDate(Str(ctx.Args, "date"), nowLocal) is not { } date)
            return Speak(new { result = "That is not a day I can read. Ask the caller which day suits them, then check that." }, ctx);

        if (date.Date < nowLocal.Date)
            return Speak(new
            {
                result = $"{date:dddd d MMMM yyyy} has already passed — today is {nowLocal:dddd d MMMM yyyy}. " +
                         "Ask the caller for a day that has not gone yet.",
            }, ctx);

        var (window, closed) = await ResolveDayAsync(orgId, ctx.Org, date);
        if (window is null) return Speak(new { result = closed }, ctx);

        var duration = 30;
        if (Str(ctx.Args, "service") is { } serviceName &&
            await _ai.FindServiceByNameAsync(orgId, serviceName) is { } service)
            duration = service.DurationMinutes;

        var dayStartUtc = TenantTime.ToUtc(date.Date + window.Start, ctx.Tz);
        var dayEndUtc = TenantTime.ToUtc(date.Date + window.End, ctx.Tz);
        var taken = (await _ai.AppointmentsBetweenAsync(orgId, dayStartUtc, dayEndUtc)).ToList();

        // How many appointments may run at once is a property of the slot, not of the business:
        // it is however many employees are on duty at that moment and not already with someone.
        // Two people working means two callers can both have noon; nobody working means neither
        // can. Organizations with no roster keep the single business-wide capacity.
        var roster = await _employees.LoadRosterAsync(orgId, date.Date, date.Date);
        var capacity = Math.Max(1, ctx.Org.MaxConcurrentAppointments);

        // Nobody in at all is a different answer from "we are full", and the more useful one:
        // there is no point walking the caller through other times on a day with no staff.
        if (roster.Enabled && roster.OnDutyOnDate(date).Count == 0)
            return Speak(new
            {
                date = date.ToString("yyyy-MM-dd"),
                result = $"No one is working on {date:dddd d MMMM yyyy}, so nothing can be booked that day. " +
                         "Tell the caller and offer another day.",
            }, ctx);

        var open = new List<string>();
        for (var local = date.Date + window.Start; local.AddMinutes(duration) <= date.Date + window.End; local = local.AddMinutes(30))
        {
            if (date.Date == nowLocal.Date && local <= nowLocal) continue; // no past slots today
            var slotStart = TenantTime.ToUtc(local, ctx.Tz);
            var slotEnd = slotStart.AddMinutes(duration);
            var overlapping = taken.Where(a => a.StartAt < slotEnd && a.EndAt > slotStart).ToList();

            var free = roster.Enabled
                ? StaffRoster.FreeCount(roster.OnDuty(local, local.AddMinutes(duration)), overlapping)
                : capacity - overlapping.Count;
            if (free > 0) open.Add(local.ToString("HH:mm"));
        }

        // The reply is read out by the agent, so it says the day the way a person would and tells it
        // what to do with the list. Handing over eight times with no instruction is what produces
        // the recital ("we have nine, nine thirty, ten, ten thirty…") that gives a bot away.
        return Speak(open.Count == 0
            ? new
            {
                date = date.ToString("yyyy-MM-dd"),
                result = $"Nothing free on {date:dddd d MMMM yyyy}. Tell the caller and offer another day.",
            }
            : new
            {
                date = date.ToString("yyyy-MM-dd"),
                result = $"Free on {date:dddd d MMMM yyyy}: {string.Join(", ", open.Take(8))}. " +
                         "Offer two or three of these, not the whole list, and keep them for the rest " +
                         "of the call — this date is now checked, do not check it again. When you book, " +
                         $"send this date back as it is written here: {date:yyyy-MM-dd}.",
                slots = open,
            }, ctx);
    }

    /// <summary>
    /// The confirmed start of a booking, in the tenant's local time.
    ///
    /// The agent is dependable about the time of day — the caller says it out loud — and much less
    /// so about which date that time falls on, because it has to hold the day in its head across
    /// the conversation. So when the agent also passes the day in the caller's own words, that day
    /// wins and only the clock time is taken from <paramref name="startTime"/>. Booking then does
    /// no date arithmetic in the model at all: the same reader that answered check_availability
    /// decides the date, against this server's clock.
    /// </summary>
    private static DateTime? ResolveStartLocal(string? startTime, string? date, DateTime nowLocal)
    {
        if (!DateTime.TryParse(startTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            return null;

        return ResolveRequestedDate(date, nowLocal) is { } day
            ? day.Date + start.TimeOfDay
            : start;
    }

    /// <summary>
    /// Turns whatever the caller said into a local date: "2026-08-14", "tomorrow", "friday",
    /// "next tuesday".
    ///
    /// The agent's own clock cannot be relied on for this: the prompt is told the current time at
    /// the start of each call, but left to work out "tomorrow" from it, the model either dates it
    /// from its training data or checks several days until one lands — which is exactly the
    /// repeated-lookup behaviour that makes a call drag. Resolving the caller's own words here, in
    /// the tenant's timezone, keeps "can you do Friday?" to a single lookup and puts the date
    /// beyond the model's reach. Returns null when nothing usable was given.
    /// </summary>
    private static DateTime? ResolveRequestedDate(string? value, DateTime nowLocal)
    {
        var text = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text)) return null;

        var today = nowLocal.Date;

        // Anything with a digit is a real date the agent (or caller) spelled out. Words are handled
        // below: parsing them as dates is what turns "may" into the first of May.
        if (text.Any(char.IsDigit))
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)
                ? exact.Date
                : null;

        if (text.StartsWith("today") || text.StartsWith("tonight") || text.StartsWith("this ")) return today;
        if (text.StartsWith("tomorrow")) return today.AddDays(1);
        if (text.Contains("day after tomorrow")) return today.AddDays(2);

        // "next friday", "on friday", "coming friday" — the qualifier only matters when the caller
        // names the day it already is, where "next" is the one reading that clearly means a week out.
        var wantsNextWeek = text.StartsWith("next ");
        var word = text.Replace("next ", "").Replace("this ", "").Replace("coming ", "")
            .Replace("on ", "").Trim();

        var weekday = Enum.GetValues<DayOfWeek>()
            .Cast<DayOfWeek?>()
            .FirstOrDefault(d => word.StartsWith(d!.Value.ToString().ToLowerInvariant()[..3]));
        if (weekday is null) return null;

        var ahead = ((int)weekday.Value - (int)today.DayOfWeek + 7) % 7;
        if (ahead == 0 && wantsNextWeek) ahead = 7;
        return today.AddDays(ahead);
    }

    [HttpPost("book_appointment")]
    public async Task<IActionResult> BookAppointment(int orgId)
    {
        var (ctx, error) = await ReadAsync(orgId);
        if (ctx is null) return error!;

        var nowLocal = TenantTime.NowLocal(ctx.Tz);
        var name = Str(ctx.Args, "name");
        var phone = Str(ctx.Args, "phone");
        var serviceName = Str(ctx.Args, "service");
        if (name is null || phone is null || serviceName is null ||
            ResolveStartLocal(Str(ctx.Args, "start_time"), Str(ctx.Args, "date"), nowLocal) is not { } startLocal)
            return Speak(new { result = "Missing details — ask for name, phone, service and a confirmed start time." }, ctx);

        if (startLocal <= nowLocal)
            return Speak(new
            {
                result = $"{startLocal:dddd d MMMM yyyy} at {startLocal:HH:mm} has already passed — it is now " +
                         $"{TenantTime.Describe(nowLocal)}. Ask for a day and time still to come.",
            }, ctx);

        // Field-service trades dispatch a technician — a job address is mandatory.
        var profile = IndustryTemplates.Resolve(ctx.Org.Industry);
        var serviceAddress = Str(ctx.Args, "service_address");
        if (profile.IsFieldService && string.IsNullOrWhiteSpace(serviceAddress))
            return Speak(new
            {
                result = "A service address is required before booking. Ask for the full street " +
                         "address, city, and any access details such as apartment number or gate code.",
            }, ctx);

        var (window, closed) = await ResolveDayAsync(orgId, ctx.Org, startLocal);
        if (window is null) return Speak(new { result = closed }, ctx);

        var service = await _ai.FindServiceByNameAsync(orgId, serviceName);
        if (service is null)
            return Speak(new { result = $"No service matching '{serviceName}' was found. Ask which listed service they want." }, ctx);

        if (startLocal.TimeOfDay < window.Start || startLocal.TimeOfDay + TimeSpan.FromMinutes(service.DurationMinutes) > window.End)
            return Speak(new { result = $"That time is outside business hours ({window.Start:hh\\:mm}–{window.End:hh\\:mm}). Offer a time within hours." }, ctx);

        var startUtc = TenantTime.ToUtc(startLocal, ctx.Tz);
        var endUtc = startUtc.AddMinutes(service.DurationMinutes);

        // Who could take this slot. The database picks which of them actually does, in the same
        // transaction as the insert, so two calls landing on the last free person cannot both win.
        var roster = await _employees.LoadRosterAsync(orgId, startLocal.Date, startLocal.Date);
        SlotAssignment? assignment = null;
        if (roster.Enabled)
        {
            var onDuty = roster.OnDuty(startLocal, startLocal.AddMinutes(service.DurationMinutes));
            if (onDuty.Count == 0)
                return Speak(new
                {
                    result = $"No one is working at {startLocal:HH:mm} on {startLocal:dddd d MMMM yyyy}, so that " +
                             "cannot be booked. Use check_availability for that day and offer a time it gives back.",
                }, ctx);
            assignment = SlotAssignment.Any(onDuty.Select(e => e.Id));
        }

        var customer = await _customers.FindReturningAsync(orgId, phone, null, name);
        var customerId = customer?.Id ?? await _customers.CreateAsync(new Customer
        {
            OrganizationId = orgId, Name = name, Phone = phone,
        });

        // Atomic capacity-checked insert — safe against concurrent calls booking the same slot.
        var appointment = new Appointment
        {
            OrganizationId = orgId,
            CustomerId = customerId,
            ServiceId = service.Id,
            StartAt = startUtc,
            EndAt = endUtc,
            Status = AppointmentStatus.Scheduled,
            PaymentStatus = "Unpaid",
            Amount = service.MinPrice,
            ServiceAddress = string.IsNullOrWhiteSpace(serviceAddress) ? null : serviceAddress,
            IsEmergency = service.IsEmergency ||
                          string.Equals(Str(ctx.Args, "is_emergency"), "true", StringComparison.OrdinalIgnoreCase),
            Notes = Str(ctx.Args, "notes") is { } n ? $"[AI] {n}" : "[AI] Booked during phone call",
        };
        var appointmentId = await _appointments.TryCreateAsync(appointment, assignment);
        if (appointmentId is null)
            return Speak(new { result = "That time was just taken. Use check_availability to offer alternatives." }, ctx);

        // TryCreateAsync writes back whoever it settled on, so the caller is told who they are
        // seeing rather than just that they are in the diary.
        var assignedTo = roster.Employees.FirstOrDefault(e => e.Id == appointment.EmployeeId)?.Name;

        await _customers.AddTimelineEventAsync(new TimelineEvent
        {
            OrganizationId = orgId, CustomerId = customerId,
            EventType = "AppointmentBooked", Source = "AI",
            Notes = $"{service.Name} on {startLocal:yyyy-MM-dd HH:mm}",
        });

        // Nobody is watching the dashboard at 2am. This is the moment the business finds out.
        appointment.Id = appointmentId.Value;
        await AnnounceAsync(NotificationService.ForBooking(
            orgId, appointment, ctx.Org, service.Name, name, startLocal, nowLocal, assignedTo));

        return Speak(new
        {
            result = $"Booked: {service.Name} on {SpokenTime(startUtc, ctx.Tz)} for {name}." +
                     (assignedTo is null ? "" : $" {assignedTo} will be looking after them.") +
                     (string.IsNullOrWhiteSpace(serviceAddress) ? "" : $" Technician will attend {serviceAddress}.") +
                     $" Confirmation number {appointmentId}.",
            appointmentId,
            staff = assignedTo,
        }, ctx);
    }

    [HttpPost("cancel_appointment")]
    public async Task<IActionResult> CancelAppointment(int orgId)
    {
        var (ctx, error) = await ReadAsync(orgId);
        if (ctx is null) return error!;

        var (appt, message) = await FindCallerAppointmentAsync(orgId, ctx, requireName: true);
        if (appt is null) return Speak(new { result = message }, ctx);

        await _appointments.UpdateStatusAsync(orgId, appt.Id, AppointmentStatus.Cancelled);
        await _customers.AddTimelineEventAsync(new TimelineEvent
        {
            OrganizationId = orgId, CustomerId = appt.CustomerId,
            EventType = "AppointmentCancelled", Source = "AI",
            Notes = $"{appt.ServiceName} on {SpokenTime(appt.StartAt, ctx.Tz)}",
        });

        // A cancellation for later today matters as much as a booking for later today: there is a
        // gap in the diary now, and somebody may be about to travel to it.
        var cancelledLocal = TenantTime.ToLocal(appt.StartAt, ctx.Tz);
        await AnnounceAsync(new Alert
        {
            OrganizationId = orgId,
            Kind = AlertKind.AppointmentCancelled,
            Severity = SeverityFor(cancelledLocal, TenantTime.NowLocal(ctx.Tz)),
            Title = $"Cancelled — {cancelledLocal:ddd d MMM} at {cancelledLocal:HH:mm}",
            Body = $"{appt.CustomerName ?? "A caller"} cancelled {appt.ServiceName}.",
            Url = "/appointments",
            AppointmentId = appt.Id,
        });

        return Speak(new { result = $"Cancelled the {appt.ServiceName} appointment on {SpokenTime(appt.StartAt, ctx.Tz)}." }, ctx);
    }

    [HttpPost("reschedule_appointment")]
    public async Task<IActionResult> RescheduleAppointment(int orgId)
    {
        var (ctx, error) = await ReadAsync(orgId);
        if (ctx is null) return error!;

        var nowLocal = TenantTime.NowLocal(ctx.Tz);
        if (ResolveStartLocal(Str(ctx.Args, "new_time"), Str(ctx.Args, "new_date"), nowLocal) is not { } newLocal)
            return Speak(new { result = "Ask for the new date and time before rescheduling." }, ctx);
        if (newLocal <= nowLocal)
            return Speak(new
            {
                result = $"{newLocal:dddd d MMMM yyyy} at {newLocal:HH:mm} has already passed — it is now " +
                         $"{TenantTime.Describe(nowLocal)}. Ask for a day and time still to come.",
            }, ctx);

        var (window, closed) = await ResolveDayAsync(orgId, ctx.Org, newLocal);
        if (window is null) return Speak(new { result = closed }, ctx);

        var (appt, message) = await FindCallerAppointmentAsync(orgId, ctx, requireName: true);
        if (appt is null) return Speak(new { result = message }, ctx);

        var newStartUtc = TenantTime.ToUtc(newLocal, ctx.Tz);
        var newEndUtc = newStartUtc + (appt.EndAt - appt.StartAt);

        // Whoever has the appointment keeps it if they are working the new time and free then;
        // otherwise it moves to someone who is. Being moved to a different day should not have to
        // mean being moved to a different person.
        var roster = await _employees.LoadRosterAsync(orgId, newLocal.Date, newLocal.Date);
        SlotAssignment? assignment = null;
        if (roster.Enabled)
        {
            var onDuty = roster.OnDuty(newLocal, newLocal + (appt.EndAt - appt.StartAt));
            if (onDuty.Count == 0)
                return Speak(new
                {
                    result = $"No one is working at {newLocal:HH:mm} on {newLocal:dddd d MMMM yyyy}. " +
                             "Use check_availability for that day and offer a time it gives back.",
                }, ctx);
            assignment = SlotAssignment.Any(onDuty.Select(e => e.Id)).Preferring(appt.EmployeeId);
        }

        if (!await _appointments.TryRescheduleAsync(orgId, appt.Id, newStartUtc, newEndUtc, assignment))
            return Speak(new { result = "The new time is not available. Use check_availability to offer alternatives." }, ctx);

        await _customers.AddTimelineEventAsync(new TimelineEvent
        {
            OrganizationId = orgId, CustomerId = appt.CustomerId,
            EventType = "AppointmentRescheduled", Source = "AI",
            Notes = $"Moved to {newLocal:yyyy-MM-dd HH:mm}",
        });

        // Urgent if either end of the move is today — the old slot has just emptied, or the new
        // one is about to be needed.
        var movedFromLocal = TenantTime.ToLocal(appt.StartAt, ctx.Tz);
        var today = TenantTime.NowLocal(ctx.Tz);
        await AnnounceAsync(new Alert
        {
            OrganizationId = orgId,
            Kind = AlertKind.AppointmentRescheduled,
            Severity = SeverityFor(newLocal, today) == AlertSeverity.Urgent || SeverityFor(movedFromLocal, today) == AlertSeverity.Urgent
                ? AlertSeverity.Urgent
                : AlertSeverity.Info,
            Title = $"Moved to {newLocal:ddd d MMM} at {newLocal:HH:mm}",
            Body = $"{appt.CustomerName ?? "A caller"} moved {appt.ServiceName} from " +
                   $"{movedFromLocal:ddd d MMM} at {movedFromLocal:HH:mm}.",
            Url = "/appointments",
            AppointmentId = appt.Id,
        });

        return Speak(new { result = $"Rescheduled to {SpokenTime(newStartUtc, ctx.Tz)}." }, ctx);
    }

    [HttpPost("check_appointment")]
    public async Task<IActionResult> CheckAppointment(int orgId)
    {
        var (ctx, error) = await ReadAsync(orgId);
        if (ctx is null) return error!;

        var (appt, message) = await FindCallerAppointmentAsync(orgId, ctx, requireName: false);
        if (appt is null) return Speak(new { result = message }, ctx);

        return Speak(new
        {
            result = $"Next appointment: {appt.ServiceName} on {SpokenTime(appt.StartAt, ctx.Tz)}. " +
                     $"Status: {appt.Status}. Payment: {appt.PaymentStatus}.",
        }, ctx);
    }

    // There is deliberately no pricing tool here. The service and product catalogues are uploaded to
    // the Retell knowledge base on every sync, so the agent already has them in context and a
    // round-trip per price question bought nothing but a pause in the conversation.

    /// <summary>Resolves which appointment the caller means. Requires phone; optionally
    /// verifies the caller's name (cancel/reschedule); disambiguates by date and/or
    /// service, and lists the options when several appointments match.</summary>
    private async Task<(Appointment? appt, string message)> FindCallerAppointmentAsync(
        int orgId, ToolContext ctx, bool requireName)
    {
        var phone = Str(ctx.Args, "phone");
        if (phone is null)
            return (null, "Ask for the caller's phone number first.");

        var customer = await _customers.FindReturningAsync(orgId, phone, null, null);
        if (customer is null)
            return (null, "No customer found with that phone number.");

        if (requireName && !NameMatches(Str(ctx.Args, "name"), customer.Name))
            return (null, "The name does not match this phone number's record. Ask the caller to confirm " +
                          "the full name on the appointment; if it still does not match, transfer to a human.");

        DateTime? fromUtc = null, toUtc = null;
        if (DateTime.TryParse(Str(ctx.Args, "date"), out var d))
        {
            fromUtc = TenantTime.ToUtc(d.Date, ctx.Tz);
            toUtc = TenantTime.ToUtc(d.Date.AddDays(1), ctx.Tz);
        }

        var matches = (await _ai.UpcomingAppointmentsAsync(orgId, customer.Id,
            fromUtc, toUtc, Str(ctx.Args, "service"))).ToList();

        return matches.Count switch
        {
            0 => (null, $"{customer.Name} has no matching upcoming appointments."),
            1 => (matches[0], ""),
            _ => (null, "Multiple appointments found — ask which one they mean: " +
                        string.Join("; ", matches.Select(m => $"{m.ServiceName} on {SpokenTime(m.StartAt, ctx.Tz)}"))),
        };
    }
}
