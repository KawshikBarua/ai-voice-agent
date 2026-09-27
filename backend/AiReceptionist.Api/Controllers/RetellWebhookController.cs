using System.Text.Json;
using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Domain;
using AiReceptionist.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.Api.Controllers;

/// <summary>
/// Receives Retell AI call lifecycle events (call_started / call_ended / call_analyzed).
/// Payload shape: { "event": "...", "call": { call_id, agent_id, from_number, transcript,
/// recording_url, start_timestamp, end_timestamp, call_analysis: { call_summary }, ... } }.
/// The tenant is resolved from call.agent_id; requests are verified via X-Retell-Signature.
/// </summary>
[ApiController]
[Route("api/v1/webhooks/retell")]
[AllowAnonymous]
public class RetellWebhookController : ControllerBase
{
    private readonly ICallRepository _calls;
    private readonly ICustomerRepository _customers;
    private readonly ISettingsRepository _settings;
    private readonly IRetellConnectionRepository _connection;
    private readonly ICallEntitlementService _entitlement;
    private readonly IHostEnvironment _env;
    private readonly ILogger<RetellWebhookController> _logger;

    public RetellWebhookController(ICallRepository calls, ICustomerRepository customers,
        ISettingsRepository settings, IRetellConnectionRepository connection,
        ICallEntitlementService entitlement, IHostEnvironment env,
        ILogger<RetellWebhookController> logger)
    {
        _calls = calls;
        _customers = customers;
        _settings = settings;
        _connection = connection;
        _entitlement = entitlement;
        _env = env;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Receive()
    {
        using var reader = new StreamReader(Request.Body);
        var raw = await reader.ReadToEndAsync();

        var connection = await _connection.GetEffectiveAsync();
        if (!RetellSignature.Accept(connection.ApiKey, connection.VerifySignature, raw,
                Request.Headers["X-Retell-Signature"], allowUnverified: _env.IsDevelopment()))
        {
            // Loud, and with enough detail to tell a forgery from a scheme mismatch. A rejection
            // here is invisible from the outside: Retell retries a handful of times and then the
            // call it was carrying is gone for good, so this log is the only place it can surface.
            _logger.LogWarning(
                "Rejected Retell webhook: signature did not verify. Header='{Signature}', body {Length} bytes. " +
                "If genuine deliveries are being rejected, the stored API key or the signing scheme is wrong — " +
                "every call they carried is being dropped.",
                Request.Headers["X-Retell-Signature"].ToString(), raw.Length);
            return Unauthorized();
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var evt = root.TryGetProperty("event", out var e) ? e.GetString() : null;
        if (evt is null || !root.TryGetProperty("call", out var call))
            return BadRequest(ApiResponse<object>.Fail("Malformed Retell payload."));

        var agentId = GetString(call, "agent_id");
        var config = agentId is null ? null : await _settings.GetAgentConfigByRetellAgentIdAsync(agentId);
        if (config is null)
        {
            _logger.LogWarning("Retell webhook for unknown agent {AgentId} (event {Event})", agentId, evt);
            return Ok(); // acknowledge so Retell doesn't retry forever
        }
        var orgId = config.OrganizationId;

        var retellCallId = GetString(call, "call_id");
        _logger.LogInformation("Retell webhook {Event} for org {OrgId}, call {CallId}", evt, orgId, retellCallId);

        switch (evt)
        {
            // Both carry the whole call object, finished. call_analyzed usually lands second and
            // adds the summary, but either can be the first this server hears of a call — the
            // other may have been lost — so each records the call or corrects the record it finds.
            case "call_ended":
            case "call_analyzed":
                await RecordCallAsync(orgId, RetellCallReading.From(call));
                break;

            // call_started: nothing to persist yet. Recording a call before it ends is exactly how
            // one used to be filed at zero seconds.
        }

        return Ok();
    }

    /// <summary>
    /// Answers Retell's inbound-call webhook, fired once per incoming call before the agent speaks.
    /// Its whole job here is the clock: it returns the tenant's current local date and time as the
    /// dynamic variable the system prompt reads, so the value is computed by this server, in this
    /// organization's timezone, at the moment the call starts.
    ///
    /// The keys deliberately shadow Retell's own {{current_time}} / {{current_time_&lt;iana&gt;}}:
    /// those default to America/Los_Angeles and have a history of resolving late or blank, and a
    /// blank clock is precisely what sends the agent back to guessing the date from its training
    /// data. Shadowing them means the prompt reads the same placeholder either way — ours when this
    /// webhook answers, Retell's if it ever does not.
    ///
    /// It is also the gate. This is the only hook that runs before the agent speaks, so it is the
    /// only place a call can be refused without it having already cost anyone minutes — and an
    /// organization that is on no plan, or has stopped paying, must not be answered. See
    /// <see cref="ICallEntitlementService"/>; <see cref="CallEntitlementWorker"/> detaches the
    /// number outright, and this closes the window before the next sweep runs.
    ///
    /// Payload: { "event": "call_inbound", "call_inbound": { agent_id, from_number, to_number, ... } }.
    /// </summary>
    [HttpPost("inbound")]
    public async Task<IActionResult> Inbound()
    {
        using var reader = new StreamReader(Request.Body);
        var raw = await reader.ReadToEndAsync();

        // Verified when Retell signs it, but never refused: the reply contains nothing but today's
        // date, and failing shut would silently put the agent back to inventing one. A bad signature
        // is worth knowing about, so it is logged.
        var connection = await _connection.GetEffectiveAsync();
        if (!RetellSignature.Accept(connection.ApiKey, connection.VerifySignature, raw,
                Request.Headers["X-Retell-Signature"], allowUnverified: true))
            _logger.LogWarning("Retell inbound-call webhook failed signature verification; answering anyway.");

        string? agentId = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            if (doc.RootElement.TryGetProperty("call_inbound", out var inbound))
                agentId = GetString(inbound, "agent_id");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Malformed Retell inbound-call webhook payload.");
        }

        var config = agentId is null ? null : await _settings.GetAgentConfigByRetellAgentIdAsync(agentId);
        var org = config is null ? null : await _settings.GetOrganizationAsync(config.OrganizationId);
        if (org is null)
        {
            // Answer anyway with an empty override so Retell starts the call on its own defaults.
            _logger.LogWarning("Retell inbound-call webhook for unknown agent {AgentId}", agentId);
            return Ok(new { call_inbound = new { dynamic_variables = new Dictionary<string, string>() } });
        }

        // Refused before the agent says anything. Answering an organization that is on no plan, or
        // has stopped paying, is service given away — and because an account with no allowance is
        // not metered either, those minutes were not even recorded as a debt anyone could chase.
        //
        // A non-2xx is how this webhook declines a call. Should Retell ever connect one anyway, the
        // tool endpoints refuse it a second time, so the agent can do nothing with the tenant's data.
        var entitlement = await _entitlement.EvaluateAsync(org.Id);
        if (!entitlement.IsAllowed)
        {
            _logger.LogWarning(
                "Refused an inbound call for organization {OrgId} ({Name}): {Reason}.",
                org.Id, org.Name, entitlement.Explain());
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "This organization is not entitled to take calls.",
                reason = entitlement.Block.ToString(),
            });
        }

        var tz = TenantTime.Resolve(org.Timezone);
        var spoken = $"{TenantTime.Describe(TenantTime.NowLocal(tz))} ({org.Timezone})";

        _logger.LogInformation("Retell inbound call for org {OrgId}: clock set to {Now}", org.Id, spoken);

        return Ok(new
        {
            call_inbound = new
            {
                dynamic_variables = new Dictionary<string, string>
                {
                    [$"current_time_{TenantTime.IanaId(org.Timezone)}"] = spoken,
                    ["current_time"] = spoken,
                },
            },
        });
    }

    /// <summary>
    /// Records a finished call, or — when it is already on record — makes the record agree with it.
    ///
    /// "Already on record" used to mean "ignore this delivery". But the row may have been written by
    /// the call import while the call was still in progress, with no end time and so zero seconds;
    /// discarding the finished call then left it at zero minutes for good. The existing row is now
    /// corrected from the final reading instead. A retry of an identical delivery changes nothing.
    /// </summary>
    private async Task RecordCallAsync(int orgId, RetellCallReading reading)
    {
        if (reading.CallId is not null && await _calls.ExistsByRetellIdAsync(orgId, reading.CallId))
        {
            await CorrectRecordedCallAsync(orgId, reading);
            return;
        }

        var from = reading.FromNumber.Length == 0 ? "unknown" : reading.FromNumber;
        var status = reading.Status ?? "Completed";
        var durationSec = reading.DurationSeconds;

        // Returning-customer detection by phone (SRS §7)
        var customer = await _customers.FindReturningAsync(orgId, from, null, null);

        var callLogId = await _calls.CreateAsync(new CallLog
        {
            OrganizationId = orgId,
            CustomerId = customer?.Id,
            RetellCallId = reading.CallId,
            FromNumber = from,
            Status = status,
            DurationSeconds = durationSec,
            Transcript = reading.Transcript,
            RecordingUrl = reading.RecordingUrl,
            Summary = reading.Summary,
            StartedAt = reading.StartedAtUtc,
        });

        // Lost the race to a concurrent delivery (or the import) recording this same call. The
        // existence check above is a read, so both can pass it; the unique index is what decides.
        // The row that won may be the one written mid-call, so it is corrected rather than trusted.
        if (callLogId == 0)
        {
            _logger.LogInformation(
                "Retell call {CallId} was recorded concurrently; reconciling the existing record.",
                reading.CallId);
            await CorrectRecordedCallAsync(orgId, reading);
            return;
        }

        // A call that connected and yet measures nothing is a payload this platform could not read,
        // and it is billed as zero. Silence would make that indistinguishable from a caller who hung
        // up instantly, so it is said out loud — the alternative is minutes quietly given away.
        if (durationSec == 0 && status == "Completed")
            _logger.LogWarning(
                "Retell call {CallId} for org {OrgId} completed but carried no usable duration " +
                "(start={Start}, end={End}, duration_ms absent or zero). Recorded as zero minutes.",
                reading.CallId, orgId, reading.StartTimestampMs, reading.EndTimestampMs);

        if (customer is not null)
        {
            await _customers.AddTimelineEventAsync(new TimelineEvent
            {
                OrganizationId = orgId,
                CustomerId = customer.Id,
                EventType = "AiCall",
                Source = "AI",
                Notes = reading.Summary ?? $"Inbound call ({status.ToLowerInvariant()}), {durationSec}s",
            });
        }
    }

    private async Task CorrectRecordedCallAsync(int orgId, RetellCallReading reading)
    {
        var correction = await _calls.ReconcileAsync(orgId, reading);
        if (correction is null) return;

        if (correction.DurationChanged)
        {
            _logger.LogWarning(
                "Corrected the length of Retell call {CallId} for org {OrgId} from {Before}s to {After}s " +
                "(it had been recorded before it finished).",
                reading.CallId, orgId, correction.PreviousSeconds, correction.CurrentSeconds);
        }
        else if (correction.LockedByInvoice && reading.IsFinished
                 && reading.DurationSeconds > 0 && reading.DurationSeconds != correction.CurrentSeconds)
        {
            _logger.LogWarning(
                "Retell call {CallId} for org {OrgId} was invoiced at {Recorded}s but Retell reports {Actual}s. " +
                "The closed period ending {PeriodEnd:u} was left as issued; credit the difference by hand if needed.",
                reading.CallId, orgId, correction.CurrentSeconds, reading.DurationSeconds, correction.BilledPeriodEnd);
        }
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
