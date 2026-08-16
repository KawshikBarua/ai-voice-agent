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
    private readonly ISettingsRepository _settings;
    private readonly IHolidayRepository _holidays;
    private readonly IRetellConnectionRepository _connection;
    private readonly IHostEnvironment _env;
    private readonly ILogger<AiToolsController> _logger;

    public AiToolsController(IAiToolsRepository ai, ICustomerRepository customers,
        IAppointmentRepository appointments, ISettingsRepository settings, IHolidayRepository holidays,
        IRetellConnectionRepository connection, IHostEnvironment env, ILogger<AiToolsController> logger)
    {
        _ai = ai;
        _customers = customers;
        _appointments = appointments;
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

        // A disabled tenant (unpaid, or suspended from the super admin console) must not be able
        // to book, cancel or read customer data through its agent. The refusal is phrased for the
        // agent to say out loud, because this reply is spoken to a live caller.
        // AgentRestricted is the narrower of the two: the account is fine and staff can still sign
        // in, but the operator has stopped the agent — typically over an unpaid or run-away bill.
        // Either way the agent must not book, cancel or read customer data.
        if (!org.IsActive || org.AgentRestricted)
        {
            _logger.LogWarning("Refused AI tool call for {State} organization {OrgId} on {Path}.",
                org.IsActive ? "restricted" : "disabled", orgId, Request.Path);
            return (null, StatusCode(StatusCodes.Status403Forbidden, new
            {
                result = "This service is temporarily unavailable. Please apologise, tell the caller " +
                         "someone will follow up, and end the call.",
            }));
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

    private IActionResult Speak(object payload) => Ok(payload);

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
            return Speak(new { result = "No existing customer found — treat the caller as a new customer." });

        return Speak(new
        {
            result = $"Returning customer: {customer.Name}, {customer.TotalVisits} previous visits.",
            customer = new { customer.Name, customer.Phone, customer.TotalVisits, lastVisit = customer.LastVisit?.ToString("yyyy-MM-dd") },
        });
    }

    [HttpPost("check_availability")]
    public async Task<IActionResult> CheckAvailability(int orgId)
    {
        var (ctx, error) = await ReadAsync(orgId);
        if (ctx is null) return error!;

        var nowLocal = TenantTime.NowLocal(ctx.Tz);
        if (ResolveRequestedDate(Str(ctx.Args, "date"), nowLocal) is not { } date)
            return Speak(new { result = "That is not a day I can read. Ask the caller which day suits them, then check that." });

        if (date.Date < nowLocal.Date)
            return Speak(new
            {
                result = $"{date:dddd d MMMM} has already passed — today is {nowLocal:dddd d MMMM}. " +
                         "Ask the caller for a day that has not gone yet.",
            });

        var (window, closed) = await ResolveDayAsync(orgId, ctx.Org, date);
        if (window is null) return Speak(new { result = closed });

        var duration = 30;
        if (Str(ctx.Args, "service") is { } serviceName &&
            await _ai.FindServiceByNameAsync(orgId, serviceName) is { } service)
            duration = service.DurationMinutes;

        var dayStartUtc = TenantTime.ToUtc(date.Date + window.Start, ctx.Tz);
        var dayEndUtc = TenantTime.ToUtc(date.Date + window.End, ctx.Tz);
        var taken = (await _ai.AppointmentsBetweenAsync(orgId, dayStartUtc, dayEndUtc)).ToList();
        var capacity = Math.Max(1, ctx.Org.MaxConcurrentAppointments);

        var open = new List<string>();
        for (var local = date.Date + window.Start; local.AddMinutes(duration) <= date.Date + window.End; local = local.AddMinutes(30))
        {
            if (date.Date == nowLocal.Date && local <= nowLocal) continue; // no past slots today
            var slotStart = TenantTime.ToUtc(local, ctx.Tz);
            var slotEnd = slotStart.AddMinutes(duration);
            if (taken.Count(a => a.StartAt < slotEnd && a.EndAt > slotStart) < capacity)
                open.Add(local.ToString("HH:mm"));
        }

        // The reply is read out by the agent, so it says the day the way a person would and tells it
        // what to do with the list. Handing over eight times with no instruction is what produces
        // the recital ("we have nine, nine thirty, ten, ten thirty…") that gives a bot away.
        return Speak(open.Count == 0
            ? new
            {
                date = date.ToString("yyyy-MM-dd"),
                result = $"Nothing free on {date:dddd d MMMM}. Tell the caller and offer another day.",
            }
            : new
            {
                date = date.ToString("yyyy-MM-dd"),
                result = $"Free on {date:dddd d MMMM}: {string.Join(", ", open.Take(8))}. " +
                         "Offer two or three of these, not the whole list, and keep them for the rest " +
                         "of the call — this date is now checked, do not check it again.",
                slots = open,
            });
    }

    /// <summary>
    /// Turns whatever the caller said into a local date: "2026-08-14", "tomorrow", "friday",
    /// "next tuesday".
    ///
    /// The agent has no clock — its prompt is built when the tenant syncs, not per call — so it
    /// cannot resolve "tomorrow" on its own. Left to guess, it either invents a date or checks
    /// several until one lands, which is exactly the repeated-lookup behaviour that makes a call
    /// drag. Resolving the caller's own words here, in the tenant's timezone, keeps "can you do
    /// Friday?" to a single lookup. Returns null when nothing usable was given.
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

        var name = Str(ctx.Args, "name");
        var phone = Str(ctx.Args, "phone");
        var serviceName = Str(ctx.Args, "service");
        if (name is null || phone is null || serviceName is null ||
            !DateTime.TryParse(Str(ctx.Args, "start_time"), out var startLocal))
            return Speak(new { result = "Missing details — ask for name, phone, service and a confirmed start time." });

        if (startLocal <= TenantTime.NowLocal(ctx.Tz))
            return Speak(new { result = "That time is in the past. Ask for a future date and time." });

        // Field-service trades dispatch a technician — a job address is mandatory.
        var profile = IndustryTemplates.Resolve(ctx.Org.Industry);
        var serviceAddress = Str(ctx.Args, "service_address");
        if (profile.IsFieldService && string.IsNullOrWhiteSpace(serviceAddress))
            return Speak(new
            {
                result = "A service address is required before booking. Ask for the full street " +
                         "address, city, and any access details such as apartment number or gate code.",
            });

        var (window, closed) = await ResolveDayAsync(orgId, ctx.Org, startLocal);
        if (window is null) return Speak(new { result = closed });

        var service = await _ai.FindServiceByNameAsync(orgId, serviceName);
        if (service is null)
            return Speak(new { result = $"No service matching '{serviceName}' was found. Ask which listed service they want." });

        if (startLocal.TimeOfDay < window.Start || startLocal.TimeOfDay + TimeSpan.FromMinutes(service.DurationMinutes) > window.End)
            return Speak(new { result = $"That time is outside business hours ({window.Start:hh\\:mm}–{window.End:hh\\:mm}). Offer a time within hours." });

        var startUtc = TenantTime.ToUtc(startLocal, ctx.Tz);
        var endUtc = startUtc.AddMinutes(service.DurationMinutes);

        var customer = await _customers.FindReturningAsync(orgId, phone, null, name);
        var customerId = customer?.Id ?? await _customers.CreateAsync(new Customer
        {
            OrganizationId = orgId, Name = name, Phone = phone,
        });

        // Atomic capacity-checked insert — safe against concurrent calls booking the same slot.
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
            ServiceAddress = string.IsNullOrWhiteSpace(serviceAddress) ? null : serviceAddress,
            IsEmergency = service.IsEmergency ||
                          string.Equals(Str(ctx.Args, "is_emergency"), "true", StringComparison.OrdinalIgnoreCase),
            Notes = Str(ctx.Args, "notes") is { } n ? $"[AI] {n}" : "[AI] Booked during phone call",
        });
        if (appointmentId is null)
            return Speak(new { result = "That time was just taken. Use check_availability to offer alternatives." });

        await _customers.AddTimelineEventAsync(new TimelineEvent
        {
            OrganizationId = orgId, CustomerId = customerId,
            EventType = "AppointmentBooked", Source = "AI",
            Notes = $"{service.Name} on {startLocal:yyyy-MM-dd HH:mm}",
        });

        return Speak(new
        {
            result = $"Booked: {service.Name} on {SpokenTime(startUtc, ctx.Tz)} for {name}." +
                     (string.IsNullOrWhiteSpace(serviceAddress) ? "" : $" Technician will attend {serviceAddress}.") +
                     $" Confirmation number {appointmentId}.",
            appointmentId,
        });
    }

    [HttpPost("cancel_appointment")]
    public async Task<IActionResult> CancelAppointment(int orgId)
    {
        var (ctx, error) = await ReadAsync(orgId);
        if (ctx is null) return error!;

        var (appt, message) = await FindCallerAppointmentAsync(orgId, ctx, requireName: true);
        if (appt is null) return Speak(new { result = message });

        await _appointments.UpdateStatusAsync(orgId, appt.Id, AppointmentStatus.Cancelled);
        await _customers.AddTimelineEventAsync(new TimelineEvent
        {
            OrganizationId = orgId, CustomerId = appt.CustomerId,
            EventType = "AppointmentCancelled", Source = "AI",
            Notes = $"{appt.ServiceName} on {SpokenTime(appt.StartAt, ctx.Tz)}",
        });
        return Speak(new { result = $"Cancelled the {appt.ServiceName} appointment on {SpokenTime(appt.StartAt, ctx.Tz)}." });
    }

    [HttpPost("reschedule_appointment")]
    public async Task<IActionResult> RescheduleAppointment(int orgId)
    {
        var (ctx, error) = await ReadAsync(orgId);
        if (ctx is null) return error!;

        if (!DateTime.TryParse(Str(ctx.Args, "new_time"), out var newLocal))
            return Speak(new { result = "Ask for the new date and time before rescheduling." });
        if (newLocal <= TenantTime.NowLocal(ctx.Tz))
            return Speak(new { result = "The new time is in the past. Ask for a future date and time." });

        var (window, closed) = await ResolveDayAsync(orgId, ctx.Org, newLocal);
        if (window is null) return Speak(new { result = closed });

        var (appt, message) = await FindCallerAppointmentAsync(orgId, ctx, requireName: true);
        if (appt is null) return Speak(new { result = message });

        var newStartUtc = TenantTime.ToUtc(newLocal, ctx.Tz);
        var newEndUtc = newStartUtc + (appt.EndAt - appt.StartAt);

        if (!await _appointments.TryRescheduleAsync(orgId, appt.Id, newStartUtc, newEndUtc))
            return Speak(new { result = "The new time is not available. Use check_availability to offer alternatives." });

        await _customers.AddTimelineEventAsync(new TimelineEvent
        {
            OrganizationId = orgId, CustomerId = appt.CustomerId,
            EventType = "AppointmentRescheduled", Source = "AI",
            Notes = $"Moved to {newLocal:yyyy-MM-dd HH:mm}",
        });
        return Speak(new { result = $"Rescheduled to {SpokenTime(newStartUtc, ctx.Tz)}." });
    }

    [HttpPost("check_appointment")]
    public async Task<IActionResult> CheckAppointment(int orgId)
    {
        var (ctx, error) = await ReadAsync(orgId);
        if (ctx is null) return error!;

        var (appt, message) = await FindCallerAppointmentAsync(orgId, ctx, requireName: false);
        if (appt is null) return Speak(new { result = message });

        return Speak(new
        {
            result = $"Next appointment: {appt.ServiceName} on {SpokenTime(appt.StartAt, ctx.Tz)}. " +
                     $"Status: {appt.Status}. Payment: {appt.PaymentStatus}.",
        });
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
