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
            case "call_ended":
                await StoreCallAsync(orgId, call, retellCallId);
                break;

            case "call_analyzed":
                var summary = call.TryGetProperty("call_analysis", out var analysis)
                    ? GetString(analysis, "call_summary")
                    : null;
                if (retellCallId is not null && summary is not null)
                {
                    var updated = await _calls.UpdateAnalysisAsync(orgId, retellCallId, summary);
                    if (!updated) await StoreCallAsync(orgId, call, retellCallId, summary);
                }
                break;

            // call_started: nothing to persist yet
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

    private async Task StoreCallAsync(int orgId, JsonElement call, string? retellCallId, string? summary = null)
    {
        // Retell retries webhooks — don't store the same call twice.
        if (retellCallId is not null && await _calls.ExistsByRetellIdAsync(orgId, retellCallId))
        {
            _logger.LogInformation("Skipping duplicate Retell webhook for call {CallId}", retellCallId);
            return;
        }

        var from = PhoneUtil.Normalize(GetString(call, "from_number"));
        if (from.Length == 0) from = "unknown";
        var startedMs = GetLong(call, "start_timestamp");
        var endedMs = GetLong(call, "end_timestamp");
        var durationSec = CallDuration.Resolve(startedMs, endedMs, GetLong(call, "duration_ms"));
        var disconnection = GetString(call, "disconnection_reason");
        var status = disconnection is "dial_no_answer" or "dial_busy" or "dial_failed" ? "Missed"
            : disconnection == "call_transfer" ? "Transferred"
            : "Completed";

        // Returning-customer detection by phone (SRS §7)
        var customer = await _customers.FindReturningAsync(orgId, from, null, null);

        var callLogId = await _calls.CreateAsync(new CallLog
        {
            OrganizationId = orgId,
            CustomerId = customer?.Id,
            RetellCallId = retellCallId,
            FromNumber = from,
            Status = status,
            DurationSeconds = durationSec,
            Transcript = GetString(call, "transcript"),
            RecordingUrl = GetString(call, "recording_url"),
            Summary = summary ?? (call.TryGetProperty("call_analysis", out var a) ? GetString(a, "call_summary") : null),
            StartedAt = startedMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(startedMs).UtcDateTime : DateTime.UtcNow,
        });

        // Lost the race to a concurrent delivery of this same webhook. The check at the top of this
        // method is a read, so both deliveries can pass it; the unique index is what decides. The
        // call is on record either way, and the timeline entry below belongs to whoever won.
        if (callLogId == 0)
        {
            _logger.LogInformation(
                "A concurrent delivery recorded Retell call {CallId} first; this one added nothing.",
                retellCallId);
            return;
        }

        // A call that connected and yet measures nothing is a payload this platform could not read,
        // and it is billed as zero. Silence would make that indistinguishable from a caller who hung
        // up instantly, so it is said out loud — the alternative is minutes quietly given away.
        if (durationSec == 0 && status == "Completed")
            _logger.LogWarning(
                "Retell call {CallId} for org {OrgId} completed but carried no usable duration " +
                "(start={Start}, end={End}, duration_ms absent or zero). Recorded as zero minutes.",
                retellCallId, orgId, startedMs, endedMs);

        if (customer is not null)
        {
            await _customers.AddTimelineEventAsync(new TimelineEvent
            {
                OrganizationId = orgId,
                CustomerId = customer.Id,
                EventType = "AiCall",
                Source = "AI",
                Notes = summary ?? $"Inbound call ({status.ToLowerInvariant()}), {durationSec}s",
            });
        }
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
