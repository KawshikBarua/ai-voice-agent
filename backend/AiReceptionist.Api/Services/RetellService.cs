using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;

namespace AiReceptionist.Api.Services;

/// <summary>A non-success response from Retell. Carries the status code so callers can tell
/// "this resource is gone" (404) apart from a transient or genuine failure.</summary>
public class RetellApiException : InvalidOperationException
{
    public RetellApiException(HttpStatusCode statusCode, string message) : base(message) =>
        StatusCode = statusCode;

    public HttpStatusCode StatusCode { get; }

    /// <summary>Retell is saying the resource does not exist for this API key.</summary>
    public bool IsMissingAsset => IsMissing(StatusCode, Message);

    /// <summary>
    /// Whether a Retell response means "no such resource under this key".
    ///
    /// Retell answers that with 422 ("Cannot find requested asset under given api key"), not only
    /// 404 — and 422 is exactly what every stored id returns after the platform moves to a new
    /// Retell account. Reading only 404 made every sync after a key change PATCH ids from the old
    /// account and fail, so nothing was ever created on the new one. A 422 for any other reason
    /// (an invalid payload) is not a missing resource and is still reported as a failure, so this
    /// can never turn a bad request into a duplicate agent.
    /// </summary>
    public static bool IsMissing(HttpStatusCode status, string? body) =>
        status == HttpStatusCode.NotFound
        || (status == HttpStatusCode.UnprocessableEntity && body is not null
            && (body.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
                || body.Contains("not found", StringComparison.OrdinalIgnoreCase)));
}

/// <summary>What one pass of the call import found and did.</summary>
public class CallSyncResult
{
    /// <summary>Finished calls that were not on record and now are.</summary>
    public int Imported { get; set; }

    /// <summary>Recorded calls whose length was wrong and has been corrected from Retell.</summary>
    public int Corrected { get; set; }

    /// <summary>Calls still in progress, left for the webhook or the next pass to record once they
    /// have a final length.</summary>
    public int InProgress { get; set; }

    /// <summary>Calls already invoiced at a length Retell does not agree with. Left as issued —
    /// a closed period is never re-billed — and logged so the difference can be credited.</summary>
    public int InvoicedMismatches { get; set; }
}

public class RetellSyncResult
{
    public bool Success { get; set; }
    public string? AgentId { get; set; }
    public string? LlmId { get; set; }
    public string? PhoneNumber { get; set; }
    /// <summary>Set when the agent synced but its phone number could not be attached. The agent
    /// is still usable (web calls, and any number already bound), so this never fails the sync.</summary>
    public string? PhoneWarning { get; set; }

    /// <summary>Set when a fresh connect could not delete something this organization already had
    /// on Retell. The sync succeeded, but a resource was left behind that the operator has to
    /// remove by hand — the one case where the account ends up with a duplicate anyway.</summary>
    public string? CleanupWarning { get; set; }

    public string? Error { get; set; }
    public DateTime? SyncedAt { get; set; }
}

/// <summary>Which of the two things a sync is: the first connect, or an update to what is already
/// there. They are not interchangeable, because the cost of getting it wrong lands on a shared
/// Retell account that every tenant's agent lives on.</summary>
public enum RetellSyncMode
{
    /// <summary>Update this tenant's existing LLM and agent in place, creating any
    /// of them that is genuinely missing. Every automatic sync uses this — a tenant editing their
    /// services must not churn Retell resources.</summary>
    Update,

    /// <summary>First connect: delete every Retell resource this tenant still owns or left
    /// detached, then build the agent and LLM from scratch. Only ever reached from
    /// the platform console's Connect action, and only for the one organization it was clicked on.</summary>
    Fresh,
}

public interface IRetellService
{
    Task<bool> IsConfiguredAsync();
    Task<RetellSyncResult> SyncAgentAsync(int orgId, RetellSyncMode mode = RetellSyncMode.Update,
        CancellationToken ct = default);
    /// <summary>
    /// Records finished calls the webhook did not deliver, and corrects any recorded call whose
    /// length disagrees with Retell's. <paramref name="deep"/> walks back through the whole call
    /// history (the Sync button); otherwise only the newest page is read (the timer).
    /// </summary>
    Task<CallSyncResult> BackfillCallsAsync(int orgId, bool deep = false, CancellationToken ct = default);

    /// <summary>Detaches this tenant's number from their agent, or points it back. Refusing the
    /// agent's tools stops it doing anything useful but the call is still answered and still costs
    /// minutes — which is no good when the reason for restricting is the bill. Unbinding the number
    /// is what actually stops the calls. Returns a message to show the operator when the change
    /// could not be made upstream, else null.</summary>
    Task<string?> SetInboundRoutingAsync(int orgId, bool enabled, CancellationToken ct = default);
}

/// <summary>
/// Pushes each tenant's AI configuration to Retell AI (SRS §14, §15):
/// creates/updates a multi-prompt Retell LLM (general prompt, per-state prompts and tools) and the
/// Agent bound to it.
/// The custom tools point back at this API's /api/v1/ai/tools endpoints so the
/// agent retrieves live data instead of hallucinating (SRS §19).
/// </summary>
public class RetellService : IRetellService
{
    private readonly HttpClient _http;
    private readonly IRetellConnectionRepository _connection;
    private readonly ISettingsRepository _settings;
    private readonly IPromptBuilderService _prompts;
    private readonly ICallRepository _calls;
    private readonly ICustomerRepository _customers;
    private readonly IBusinessLocationRepository _locations;
    private readonly ILogger<RetellService> _logger;

    /// <summary>The model behind every tenant's agent. Sent on every sync rather than left to
    /// Retell's account default, so the model a tenant is answered by is decided here and moves
    /// for everyone at once — and so a change to the Retell account cannot quietly alter it.</summary>
    private const string LlmModel = "gemini-3.0-flash";

    /// <summary>How easily the caller's voice stops the agent mid-sentence, from 0 (never) to 1.
    /// Retell's default is 1, at which a "yeah", a cough or a television in the room cuts the agent
    /// off — calls came out as half-sentences stitched between the caller's acknowledgements. At
    /// this level it takes a caller genuinely speaking up to stop it, so "no, wait" still works.</summary>
    private const double InterruptionSensitivity = 0.5;

    /// <summary>Retell's standard noise filter, which costs nothing extra. Sent rather than left to
    /// the account default so nobody switches every tenant onto the background-speech filter by
    /// accident: that one is billed as an add-on on every minute of every call.</summary>
    private const string DenoisingMode = "noise-cancellation";

    // Loaded per operation from the centralized platform connection (DB, config fallback).
    private string? _apiKey;
    private string _apiBaseUrl = "https://api.retellai.com";
    private string? _webhookBaseUrl;
    private string? _defaultVoiceId;

    private static readonly JsonSerializerOptions JsonOpts = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public RetellService(HttpClient http, IRetellConnectionRepository connection, ISettingsRepository settings,
        IPromptBuilderService prompts, ICallRepository calls, ICustomerRepository customers,
        IBusinessLocationRepository locations,
        ILogger<RetellService> logger)
    {
        _http = http;
        _connection = connection;
        _settings = settings;
        _prompts = prompts;
        _calls = calls;
        _customers = customers;
        _locations = locations;
        _logger = logger;
    }

    public async Task<bool> IsConfiguredAsync()
    {
        var c = await _connection.GetEffectiveAsync();
        return !string.IsNullOrWhiteSpace(c.ApiKey);
    }

    /// <summary>Loads the centralized Retell connection into the per-call fields and points the
    /// HttpClient at it. Called at the start of each operation so key/URL changes made in the
    /// admin screen take effect on the next sync without a restart. A fresh RetellService is
    /// resolved per DI scope (per request / per worker iteration), so these fields are never
    /// shared across concurrent operations.</summary>
    private async Task LoadConnectionAsync()
    {
        var c = await _connection.GetEffectiveAsync();
        _apiKey = c.ApiKey;
        _apiBaseUrl = string.IsNullOrWhiteSpace(c.ApiBaseUrl) ? "https://api.retellai.com" : c.ApiBaseUrl.Trim();
        _webhookBaseUrl = c.WebhookBaseUrl;
        _defaultVoiceId = c.DefaultVoiceId;

        _http.BaseAddress ??= new Uri(_apiBaseUrl);
        _http.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(_apiKey)
            ? null : new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    private string PublicBaseUrl =>
        (string.IsNullOrWhiteSpace(_webhookBaseUrl) ? "http://localhost:5200" : _webhookBaseUrl).Trim().TrimEnd('/');

    // One sync at a time per organization: prevents the manual button + background worker
    // racing each other into creating duplicate Retell LLMs/agents.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> SyncLocks = new();

    public async Task<RetellSyncResult> SyncAgentAsync(int orgId, RetellSyncMode mode = RetellSyncMode.Update,
        CancellationToken ct = default)
    {
        // The gate is keyed on the organization, so this only ever serialises one tenant against
        // itself. A connect or disconnect on one organization never touches, blocks or resyncs
        // another — nothing in this service reaches beyond the orgId it was handed.
        var gate = SyncLocks.GetOrAdd(orgId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await SyncAgentCoreAsync(orgId, mode, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RetellSyncResult> SyncAgentCoreAsync(int orgId, RetellSyncMode mode, CancellationToken ct)
    {
        await LoadConnectionAsync();
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new RetellSyncResult { Error = "Retell API key is not configured. Set it in the platform Retell connection settings." };

        // Retell rejects localhost tool/webhook URLs — a public HTTPS base URL is required.
        if (PublicBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase))
            return new RetellSyncResult
            {
                Error = "Retell requires a public HTTPS URL for webhooks and live-call tools. " +
                        "Expose this API publicly (e.g. 'ngrok http 5200' or a Cloudflare tunnel), then set " +
                        "Retell:WebhookBaseUrl in appsettings.Local.json to that URL and sync again.",
            };

        var org = await _settings.GetOrganizationAsync(orgId);
        if (org is null) return new RetellSyncResult { Error = "Organization not found." };

        var agent = await _settings.GetAgentConfigAsync(orgId) ?? new Domain.AgentConfig { OrganizationId = orgId };
        var prompt = await _prompts.BuildAgentPromptAsync(orgId);

        // Only fill in a greeting the tenant never set — a custom one must reach Retell
        // exactly as written. The default is resolved here (not just on save) so a brand-new
        // or renamed organization still gets a correctly named greeting on the first sync.
        if (string.IsNullOrWhiteSpace(agent.Greeting))
            agent.Greeting = GreetingBuilder.For(org.Name);

        try
        {
            // 0) A first connect starts from nothing. Anything this tenant still owns on Retell —
            //    including the set a previous disconnect detached but left alive — is deleted here,
            //    before a single resource is created. Skipping this is what used to leave a spare
            //    agent, LLM and knowledge base behind on every connect/disconnect cycle.
            //    Scoped entirely to this organization's own stored ids: no listing, no name
            //    matching, nothing that could reach another tenant's resources.
            var purgeFailures = mode == RetellSyncMode.Fresh
                ? await PurgeRemoteResourcesAsync(agent, ct)
                : [];

            // 1) Create or update the Retell LLM: a multi-prompt LLM whose general prompt holds what
            //    applies on every turn, with one state per stage of the call (see BuildCallFlow).
            //    Reference content (catalogues, FAQs, policies, hours) is not sent at all: the
            //    lookup_info tool serves it from this platform's own database when a caller asks.
            var hasServiceArea = (await _locations.ListActiveAsync(orgId)).Count > 0;
            var (generalTools, states) = BuildCallFlow(orgId, agent.TransferNumber,
                IndustryTemplates.Resolve(org.Industry), hasServiceArea, prompt);
            var llmPayload = new
            {
                model = LlmModel,
                start_speaker = "agent",
                general_prompt = prompt.General,
                begin_message = string.IsNullOrWhiteSpace(agent.Greeting) ? null : agent.Greeting,
                general_tools = generalTools,
                states,
                starting_state = AgentStates.Triage,

                // Sent empty, never left out: an LLM built before lookup_info still points at a
                // Retell knowledge base, and every minute it does is billed the knowledge-base
                // add-on — plus the chunks it retrieves, counted on every turn.
                knowledge_base_ids = Array.Empty<string>(),
            };

            agent.RetellLlmId = await UpsertRemoteAsync(agent.RetellLlmId, "/update-retell-llm",
                "/create-retell-llm", llmPayload, "llm_id", orgId, "LLM", ct);

            // Persist immediately so a later failure can't orphan the created LLM.
            await _settings.UpsertAgentConfigAsync(agent);

            // 2) With nothing pointing at it any more, the knowledge base this tenant used to have
            //    is deleted, so it stops counting against the account.
            await RetireKnowledgeBasesAsync(agent, org.Name, ct);

            // 3) Create or update the Agent bound to that LLM
            var agentPayload = new
            {
                // The org id rides along in the name so the operator's Retell dashboard shows which
                // tenant each agent serves, and so a later connect can recognise this agent as ours
                // even if the LLM it points at has been deleted. Never seen by callers or tenants.
                agent_name = $"{org.Name} — Frontly {OrgTag(orgId)}",
                voice_id = MapVoice(agent.Voice),
                language = string.IsNullOrWhiteSpace(agent.Language) ? "en-US" : agent.Language,
                response_engine = new { type = "retell-llm", llm_id = agent.RetellLlmId },
                webhook_url = $"{PublicBaseUrl}/api/v1/webhooks/retell",

                // On for every tenant, not a per-account option: it lets the agent colour what it
                // says — a sigh, a beat before bad news, warmth on a greeting — which is most of
                // what separates a receptionist from a recording. Retell only honours it for its
                // own platform voices, which is why MapVoice never returns anything else.
                enable_expressive_mode = true,

                interruption_sensitivity = InterruptionSensitivity,
                denoising_mode = DenoisingMode,
            };

            agent.RetellAgentId = await UpsertRemoteAsync(agent.RetellAgentId, "/update-agent",
                "/create-agent", agentPayload, "agent_id", orgId, "agent", ct);

            agent.LastSyncedAt = DateTime.UtcNow;
            await _settings.UpsertAgentConfigAsync(agent);

            // 4) Route the tenant's phone number to this agent. Deliberately non-fatal: the agent
            //    itself is synced and answering (web calls, previously bound numbers) even when
            //    the number cannot be attached, so a telephony problem must not undo the sync.
            //
            //    Unless the operator has restricted this tenant. Syncs are triggered by ordinary
            //    tenant edits (a knowledge base change queues one), so binding here would quietly
            //    put a restricted customer's calls back on within minutes of them being stopped.
            string? phoneWarning;
            if (org.AgentRestricted)
            {
                phoneWarning = "This organization's agent is restricted, so its phone number was left detached.";
                _logger.LogInformation(
                    "Skipped phone binding for org {OrgId}: the agent is restricted by the platform operator.",
                    orgId);
            }
            else
            {
                try
                {
                    phoneWarning = await BindPhoneNumberAsync(agent, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Retell phone number binding failed for org {OrgId}", orgId);
                    phoneWarning = $"The phone number could not be attached to the agent: {ex.Message}";
                }
            }

            _logger.LogInformation("Retell sync complete for org {OrgId}: agent {AgentId}, llm {LlmId}",
                orgId, agent.RetellAgentId, agent.RetellLlmId);

            return new RetellSyncResult
            {
                Success = true,
                AgentId = agent.RetellAgentId,
                LlmId = agent.RetellLlmId,
                PhoneNumber = agent.RetellPhoneNumber,
                PhoneWarning = phoneWarning,
                SyncedAt = agent.LastSyncedAt,

                // Said out loud rather than logged away: a connect that built the new agent but
                // could not remove the old one has left a duplicate on the account, and the
                // operator is the only one who can go and finish the job.
                CleanupWarning = purgeFailures.Count == 0
                    ? null
                    : $"The new agent is live, but {string.Join("; ", purgeFailures)}. " +
                      "Remove the leftovers in the Retell dashboard so this organization keeps one agent.",
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retell sync failed for org {OrgId}", orgId);
            return new RetellSyncResult { Error = ex.Message };
        }
    }

    /// <summary>
    /// Reconciles this tenant's calls against Retell's own record of them.
    ///
    /// Webhooks are fire-and-forget: if the API or tunnel is down when a call ends, that call is
    /// never delivered. This reads Retell's list and records what is missing, so history — and
    /// the minutes summed from it — is complete regardless of what the webhook delivered.
    ///
    /// Two rules keep it from ever writing a wrong length:
    /// <list type="bullet">
    /// <item>A call still in progress is skipped. It has a start but no end, so recording it then
    /// filed it at zero seconds — and the finished call arriving later was thrown away as a
    /// duplicate, leaving it at zero minutes for good.</item>
    /// <item>A call already on record is compared with Retell's finished reading and corrected when
    /// its length differs, which is what repairs any row written that way before this fix. A call
    /// whose minutes are already on a closed, invoiced period is left exactly as billed and
    /// reported instead.</item>
    /// </list>
    /// </summary>
    public async Task<CallSyncResult> BackfillCallsAsync(int orgId, bool deep = false, CancellationToken ct = default)
    {
        var result = new CallSyncResult();

        await LoadConnectionAsync();
        if (string.IsNullOrWhiteSpace(_apiKey)) return result;

        var agent = await _settings.GetAgentConfigAsync(orgId);
        if (string.IsNullOrWhiteSpace(agent?.RetellAgentId)) return result;

        // The timer reads the newest page every few minutes, which is all a missed webhook needs.
        // A deep pass walks back through the history so every recorded call is checked; it is
        // bounded, so an account with an enormous history cannot turn one click into a runaway.
        var pageSize = deep ? 1000 : 100;
        var maxPages = deep ? 20 : 1;
        string? paginationKey = null;

        for (var page = 0; page < maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();

            var listed = await ListCallsPageAsync(agent.RetellAgentId, pageSize, paginationKey, ct);
            if (listed is null) break;   // the failure is logged; a partial pass is still useful

            var readings = listed.Where(r => r.CallId is not null).ToList();
            if (readings.Count == 0) break;

            await ReconcilePageAsync(orgId, readings, deep, result, ct);

            if (listed.Count < pageSize) break;
            paginationKey = readings[^1].CallId;
        }

        if (result.Imported > 0 || result.Corrected > 0)
            _logger.LogInformation(
                "Call import for org {OrgId}: {Imported} recorded, {Corrected} corrected from Retell.",
                orgId, result.Imported, result.Corrected);
        if (result.InvoicedMismatches > 0 && deep)
            _logger.LogWarning(
                "Call import for org {OrgId}: {Count} call(s) were invoiced at a length Retell disagrees " +
                "with. Their closed periods were left as issued.", orgId, result.InvoicedMismatches);

        return result;
    }

    /// <summary>One page of <c>/v2/list-calls</c> for an agent, newest first. Null on failure.</summary>
    private async Task<List<RetellCallReading>?> ListCallsPageAsync(
        string agentId, int limit, string? paginationKey, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>
        {
            ["filter_criteria"] = new { agent_id = new[] { agentId } },
            ["limit"] = limit,
            ["sort_order"] = "descending",
        };
        if (paginationKey is not null) payload["pagination_key"] = paginationKey;

        var request = new HttpRequestMessage(HttpMethod.Post, "/v2/list-calls")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Retell list-calls failed ({Status}): {Body}", (int)response.StatusCode, body);
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        var calls = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : doc.RootElement.TryGetProperty("calls", out var c) ? c : default;
        if (calls.ValueKind != JsonValueKind.Array) return null;

        // Read while the document is alive; the readings hold no reference to it.
        return calls.EnumerateArray().Select(RetellCallReading.From).ToList();
    }

    private async Task ReconcilePageAsync(int orgId, List<RetellCallReading> readings, bool deep,
        CallSyncResult result, CancellationToken ct)
    {
        // One read for the whole page rather than one per call.
        var recorded = await _calls.GetRecordedAsync(orgId, readings.Select(r => r.CallId!).ToList());

        foreach (var reading in readings)
        {
            ct.ThrowIfCancellationRequested();

            if (!reading.IsFinished)
            {
                result.InProgress++;
                continue;
            }

            if (recorded.TryGetValue(reading.CallId!, out var onRecord))
            {
                if (onRecord.InvoicedAtWrongLength(reading))
                {
                    result.InvoicedMismatches++;
                    if (deep)
                        _logger.LogWarning(
                            "Retell call {CallId} for org {OrgId} was invoiced at {Recorded}s but Retell reports " +
                            "{Actual}s; the closed period ending {PeriodEnd:u} was left as issued.",
                            reading.CallId, orgId, onRecord.DurationSeconds, reading.DurationSeconds,
                            onRecord.BilledPeriodEnd);
                }

                if (!onRecord.DiffersFrom(reading)) continue;

                var correction = await _calls.ReconcileAsync(orgId, reading);
                if (correction is { DurationChanged: true })
                {
                    result.Corrected++;
                    _logger.LogWarning(
                        "Corrected the length of Retell call {CallId} for org {OrgId} from {Before}s to {After}s.",
                        reading.CallId, orgId, correction.PreviousSeconds, correction.CurrentSeconds);
                }
                continue;
            }

            var from = reading.FromNumber.Length == 0 ? "web-call" : reading.FromNumber;
            var customer = await _customers.FindReturningAsync(orgId, from, null, null);

            var created = await _calls.CreateAsync(new Domain.CallLog
            {
                OrganizationId = orgId,
                CustomerId = customer?.Id,
                RetellCallId = reading.CallId,
                FromNumber = from,
                Status = reading.Status ?? "Completed",
                // The same reading the live webhook takes, so a call recorded here is never
                // billed differently from one that arrived by itself.
                DurationSeconds = reading.DurationSeconds,
                Transcript = reading.Transcript,
                RecordingUrl = reading.RecordingUrl,
                Summary = reading.Summary,
                StartedAt = reading.StartedAtUtc,
            });

            if (created > 0)
                result.Imported++;
            else
                // The webhook recorded it between the read above and this insert. Its reading is
                // final too, but make sure the two agree rather than assume it.
                await _calls.ReconcileAsync(orgId, reading);
        }
    }

    private static string? GetStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public async Task<string?> SetInboundRoutingAsync(int orgId, bool enabled, CancellationToken ct = default)
    {
        var agent = await _settings.GetAgentConfigAsync(orgId);
        if (agent is null || string.IsNullOrWhiteSpace(agent.RetellAgentId))
            return null;   // never connected, so there is nothing routing to detach

        var number = PhoneUtil.ToE164(agent.RetellPhoneNumber);
        if (number is null)
            return null;   // web-test-call only, or a number attached by hand in the dashboard

        await LoadConnectionAsync();
        if (string.IsNullOrWhiteSpace(_apiKey))
            return "Retell is not connected, so inbound routing was left as it is.";

        try
        {
            if (enabled)
            {
                // Only reclaim the number if nothing else has taken it in the meantime — the
                // Retell account is shared, and stealing it back would reroute another tenant.
                var numbers = await ListPhoneNumbersAsync(ct);
                var target = numbers.FirstOrDefault(n => n.PhoneNumber == number);
                if (target is null)
                    return $"{number} is no longer on the Retell account, so calls were not restored.";

                if (!string.IsNullOrWhiteSpace(target.InboundAgentId) &&
                    target.InboundAgentId != agent.RetellAgentId)
                    return $"{number} now answers for another organization and was left untouched.";

                await UpdateInboundAgentAsync(number, agent.RetellAgentId, ct);
                _logger.LogInformation("Restored inbound routing for org {OrgId} on {Phone}.", orgId, number);
            }
            else
            {
                await UpdateInboundAgentAsync(number, null, ct);
                _logger.LogWarning("Detached inbound number {Phone} from org {OrgId}'s agent.", number, orgId);
            }

            return null;
        }
        catch (Exception ex)
        {
            // The database flag is still authoritative — the agent's tools are refused either way,
            // so a failure here degrades to "answers but can do nothing" rather than losing the
            // restriction entirely. Say so instead of reporting success.
            _logger.LogError(ex, "Could not change inbound routing for organization {OrgId}.", orgId);
            return enabled
                ? $"Calls could not be restored on Retell: {ex.Message} Re-sync the agent to retry."
                : $"The number could not be detached on Retell: {ex.Message} The agent will still answer, " +
                  "but it is blocked from booking or reading anything.";
        }
    }

    private record RetellPhone(string PhoneNumber, string? InboundAgentId);

    /// <summary>
    /// Points the tenant's configured number at this tenant's agent so inbound calls are answered.
    /// Only ever re-points numbers that already exist on the Retell account — buying a number is a
    /// billable action and stays a deliberate step in the Retell dashboard.
    /// A number this agent still holds but no longer wants is released first, so changing the
    /// number in Settings actually moves the calls. When the field is empty nothing is touched:
    /// numbers attached by hand in the dashboard (the only way this worked before) must keep
    /// working for tenants who never fill the field in.
    /// Returns a message to show the tenant when the number could not be attached, else null.
    /// </summary>
    private async Task<string?> BindPhoneNumberAsync(Domain.AgentConfig agent, CancellationToken ct)
    {
        var configured = agent.RetellPhoneNumber?.Trim();
        if (string.IsNullOrEmpty(configured)) return null;

        var wanted = PhoneUtil.ToE164(configured);
        if (wanted is null)
            return $"'{configured}' is not a valid international number, so it was not attached. " +
                   "Use E.164 format, e.g. +15551234567.";

        var numbers = await ListPhoneNumbersAsync(ct);

        // Release anything else still answering with this agent, so the old number stops taking
        // calls the moment the tenant switches. Failures here are logged, never fatal: the point
        // of this sync is to get the *new* number working.
        foreach (var stale in numbers.Where(n => n.InboundAgentId == agent.RetellAgentId && n.PhoneNumber != wanted))
        {
            try
            {
                await UpdateInboundAgentAsync(stale.PhoneNumber, null, ct);
                _logger.LogInformation("Released Retell number {Phone} from org {OrgId}'s agent",
                    stale.PhoneNumber, agent.OrganizationId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not release Retell number {Phone}", stale.PhoneNumber);
            }
        }

        var target = numbers.FirstOrDefault(n => n.PhoneNumber == wanted);
        if (target is null)
            return $"{wanted} is not on the connected Retell account. Buy or import it in the Retell " +
                   "dashboard first, then sync again.";

        // The Retell account is shared by every tenant, so a number could already be answering for
        // someone else. Never take it: that would silently reroute another organization's calls.
        var alreadyOurs = target.InboundAgentId == agent.RetellAgentId;
        if (!alreadyOurs && !string.IsNullOrWhiteSpace(target.InboundAgentId))
        {
            var owner = await _settings.GetAgentConfigByRetellAgentIdAsync(target.InboundAgentId);
            if (owner is not null && owner.OrganizationId != agent.OrganizationId)
                return $"{wanted} already answers for another organization on this Retell account " +
                       "and was left untouched.";
        }

        // Sent even when the number already points at this agent: the same call registers the
        // inbound-call webhook, and numbers bound before that existed would otherwise never get it.
        await UpdateInboundAgentAsync(wanted, agent.RetellAgentId, ct);
        _logger.LogInformation(alreadyOurs
                ? "Retell number {Phone} still routes to org {OrgId}'s agent {AgentId}; inbound webhook refreshed"
                : "Retell number {Phone} now routes to org {OrgId}'s agent {AgentId}",
            wanted, agent.OrganizationId, agent.RetellAgentId);
        return null;
    }

    private async Task<List<RetellPhone>> ListPhoneNumbersAsync(CancellationToken ct)
    {
        var response = await _http.GetAsync("/list-phone-numbers", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new RetellApiException(response.StatusCode,
                $"Retell API GET /list-phone-numbers failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var array = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : doc.RootElement.TryGetProperty("phone_numbers", out var p) ? p : default;

        var numbers = new List<RetellPhone>();
        if (array.ValueKind != JsonValueKind.Array) return numbers;

        foreach (var element in array.EnumerateArray())
        {
            // Compare in the same canonical form the tenant's number is stored in.
            var number = PhoneUtil.ToE164(GetStr(element, "phone_number"));
            if (number is not null)
                numbers.Add(new RetellPhone(number, GetStr(element, "inbound_agent_id")));
        }
        return numbers;
    }

    /// <summary>Sets (or clears, with a null agent id) which agent answers calls to a number, and
    /// points the number at our inbound-call webhook. That webhook is what tells the agent the
    /// current date and time in this tenant's timezone at the moment the call is answered, so it is
    /// registered on the number itself rather than left to the operator to set in the dashboard.</summary>
    private async Task UpdateInboundAgentAsync(string e164, string? inboundAgentId, CancellationToken ct)
    {
        // Every caller reaches this through PhoneUtil.ToE164, so e164 is '+' plus digits only and
        // cannot inject a path or query into the request URL.
        // The body is written by hand because the shared JsonOpts omits nulls, while clearing the
        // agent depends on sending an explicit null.
        var json = inboundAgentId is null
            ? """{"inbound_agent_id":null,"inbound_webhook_url":null}"""
            : JsonSerializer.Serialize(new
            {
                inbound_agent_id = inboundAgentId,
                inbound_webhook_url = $"{PublicBaseUrl}/api/v1/webhooks/retell/inbound",
            });

        var request = new HttpRequestMessage(HttpMethod.Patch, $"/update-phone-number/{e164}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new RetellApiException(response.StatusCode,
                $"Retell API PATCH /update-phone-number failed ({(int)response.StatusCode}): {body}");
        }
    }

    /// <summary>
    /// Deletes the Retell knowledge bases this organization had before reference content moved to
    /// lookup_info, once the LLM no longer points at them. Each one is billed while it exists — and
    /// one still attached to an LLM is billed on every minute of every call — so nothing is left.
    ///
    /// Runs only while the stored id says there is something to retire, which is the first sync
    /// after the switch. The stored id is cleared only once the whole pass has succeeded, so a
    /// sync that could not finish leaves it set and the next one tries again.
    ///
    /// Ownership is the tag this service wrote into each base's name — carrying the organization
    /// id, so it survives a rename and cannot be shared with another tenant. Bases created before
    /// that tag existed can only be recognised by the name they were given, so those are matched by
    /// their old naming pattern and only when no other organization goes by the same name. A tenant
    /// deleting a namesake's content is the one outcome worth being slow about.
    /// </summary>
    private async Task RetireKnowledgeBasesAsync(Domain.AgentConfig agent, string orgName, CancellationToken ct)
    {
        var orgId = agent.OrganizationId;
        if (string.IsNullOrWhiteSpace(agent.RetellKnowledgeBaseId)) return;

        try
        {
            var retired = await TryDeleteKnowledgeBaseAsync(agent.RetellKnowledgeBaseId, ct);

            var tag = OrgTag(orgId);
            // What an older naming scheme produced: "<org name> — reference (20250910143000)".
            // Built from the name exactly as stored, because that is what was interpolated into it.
            var legacyPrefix = $"{orgName} — reference (";
            var matchLegacy = !string.IsNullOrWhiteSpace(orgName) &&
                              !await _settings.AnyOtherOrganizationNamedAsync(orgId, orgName);

            foreach (var kb in await ListKnowledgeBasesAsync(ct))
            {
                var id = GetStr(kb, "knowledge_base_id");
                var name = GetStr(kb, "knowledge_base_name") ?? "";
                var ours = name.Contains(tag, StringComparison.Ordinal) ||
                           (matchLegacy && name.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase));
                if (id is null || !ours) continue;

                if (await TryDeleteKnowledgeBaseAsync(id, ct))
                    _logger.LogInformation("Deleted Retell knowledge base {KbId} ('{Name}') for org {OrgId}.", id, name, orgId);
                else
                    retired = false;
            }

            if (!retired) return;
            agent.RetellKnowledgeBaseId = null;
            _logger.LogInformation("Org {OrgId} no longer has a Retell knowledge base; reference content is " +
                                   "served by lookup_info.", orgId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Housekeeping only: the LLM already points at no base, so the agent is right either way.
            // What is left behind costs storage, and the next sync tries again.
            _logger.LogWarning(ex, "Could not retire Retell knowledge bases for org {OrgId}", orgId);
        }
    }

    /// <summary>Every knowledge base on the Retell account. Retell has returned this listing both
    /// as a bare array and inside a wrapper, so both are read — a listing that quietly came back
    /// empty would look exactly like an account with nothing left to clean up.</summary>
    private async Task<List<JsonElement>> ListKnowledgeBasesAsync(CancellationToken ct)
    {
        var response = await _http.GetAsync("/list-knowledge-bases", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new RetellApiException(response.StatusCode,
                $"Retell API GET /list-knowledge-bases failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var array = root;

        if (root.ValueKind == JsonValueKind.Object)
        {
            array = default;
            foreach (var name in new[] { "knowledge_bases", "items", "data", "results" })
                if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
                {
                    array = v;
                    break;
                }
        }

        // Cloned because the JsonDocument backing them is disposed on the way out.
        return array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(e => e.Clone()).ToList()
            : [];
    }

    /// <summary>Deletes every Retell resource that belongs to this one organization, then clears
    /// the stored ids so the sync that follows builds a single fresh set.
    ///
    /// The stored ids are not enough on their own to promise no duplicates. They go missing:
    /// disconnects before this cleanup existed threw them away, and a sync that creates a resource
    /// and then fails before the row is written leaves one behind with nothing pointing at it. So
    /// this also asks Retell what is actually on the account and works out what is ours, which is
    /// the only way to guarantee an organization ends up with exactly one agent.
    ///
    /// Ownership is decided by evidence that cannot be shared with another tenant, never by name:
    /// every LLM we build carries this organization's id inside its tool URLs, and an agent is ours
    /// when it responds with one of those LLMs (or still wears our name tag, which catches an agent
    /// whose LLM has already gone). Two tenants called the same thing can never collide.</summary>
    private async Task<List<string>> PurgeRemoteResourcesAsync(Domain.AgentConfig agent, CancellationToken ct)
    {
        var orgId = agent.OrganizationId;
        var failures = new List<string>();

        var agentIds = new HashSet<string>(StringComparer.Ordinal);
        var llmIds = new HashSet<string>(StringComparer.Ordinal);
        var kbIds = new HashSet<string>(StringComparer.Ordinal);

        Add(agentIds, agent.RetellAgentId, agent.DetachedRetellAgentId);
        Add(llmIds, agent.RetellLlmId, agent.DetachedRetellLlmId);
        Add(kbIds, agent.RetellKnowledgeBaseId, agent.DetachedRetellKnowledgeBaseId);

        // Sweeping the account is what catches the resources no id points at any more. A failure
        // here is reported but not fatal: the ids we do hold still get cleaned up, and a connect
        // must not be blocked because a list endpoint was unavailable.
        try
        {
            await DiscoverOwnedResourcesAsync(orgId, agentIds, llmIds, kbIds, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not sweep the Retell account for org {OrgId}'s existing resources", orgId);
            failures.Add("the Retell account could not be searched for older resources belonging to this " +
                         $"organization ({ex.Message})");
        }

        if (agentIds.Count == 0 && llmIds.Count == 0 && kbIds.Count == 0)
            return failures;

        // Agents first: Retell rejects deleting an LLM while an agent still responds with it, so
        // an agent left in place would keep its whole chain alive behind it.
        foreach (var id in agentIds)
            await DeleteAsync($"/delete-agent/{id}", "agent", id);

        // force_delete because an agent we could not remove — or one belonging to nobody — must not
        // strand the LLM. The agents that reference it are this organization's own, and are gone.
        foreach (var id in llmIds)
            await DeleteAsync($"/delete-retell-llm/{id}?force_delete=true", "LLM", id);

        foreach (var id in kbIds)
            await DeleteAsync($"/delete-knowledge-base/{id}", "knowledge base", id);

        _logger.LogInformation(
            "Cleared Retell resources for org {OrgId} ahead of a fresh connect: {Agents} agent(s), " +
            "{Llms} LLM(s), {Kbs} knowledge base(s), {Failures} failure(s).",
            orgId, agentIds.Count, llmIds.Count, kbIds.Count, failures.Count);

        agent.RetellAgentId = null;
        agent.RetellLlmId = null;
        agent.RetellKnowledgeBaseId = null;
        agent.DetachedRetellAgentId = null;
        agent.DetachedRetellLlmId = null;
        agent.DetachedRetellKnowledgeBaseId = null;
        await _settings.UpsertAgentConfigAsync(agent);

        return failures;

        static void Add(HashSet<string> set, params string?[] ids)
        {
            foreach (var id in ids)
                if (!string.IsNullOrWhiteSpace(id)) set.Add(id);
        }

        // A resource Retell has already forgotten is a success — that is the state we wanted. Any
        // other refusal is collected and shown to the operator rather than logged and lost, because
        // silently leaving a duplicate behind is the one outcome this whole routine exists to stop.
        async Task DeleteAsync(string path, string resource, string id)
        {
            try
            {
                var response = await _http.DeleteAsync(path, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode || RetellApiException.IsMissing(response.StatusCode, body))
                {
                    _logger.LogInformation("Deleted Retell {Resource} {Id} for org {OrgId}.", resource, id, orgId);
                    return;
                }

                _logger.LogWarning("Could not delete Retell {Resource} {Id} for org {OrgId} ({Status}): {Body}",
                    resource, id, orgId, response.StatusCode, body);
                failures.Add($"{resource} {id} could not be deleted ({(int)response.StatusCode})");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete Retell {Resource} {Id} for org {OrgId}", resource, id, orgId);
                failures.Add($"{resource} {id} could not be deleted ({ex.Message})");
            }
        }
    }

    /// <summary>Adds to the given sets every agent, LLM and knowledge base on the Retell account
    /// that demonstrably belongs to this organization, including ones no stored id refers to.</summary>
    private async Task DiscoverOwnedResourcesAsync(int orgId, HashSet<string> agentIds,
        HashSet<string> llmIds, HashSet<string> kbIds, CancellationToken ct)
    {
        // The proof of ownership: every tool we generate is addressed to this organization's own
        // endpoint. The trailing slash matters — without it org 1 would claim org 12's LLM.
        var toolPath = $"/api/v1/ai/tools/{orgId}/";

        foreach (var llm in await ListAllAsync("/v2/list-retell-llms", "/list-retell-llms", HttpMethod.Get, ct))
        {
            var id = GetStr(llm, "llm_id");
            if (id is null) continue;

            // Matched against the raw JSON of the tool lists so the shape of a tool never matters,
            // only the address it points at. Tools sit in general_tools on a single-prompt LLM and in
            // each state on a multi-prompt one; only tool lists are read, never a prompt, because a
            // prompt carries tenant-written text that could name another organization's path.
            if (!ToolLists(llm).Any(tools => tools.Contains(toolPath, StringComparison.Ordinal))) continue;

            llmIds.Add(id);
            if (llm.TryGetProperty("knowledge_base_ids", out var kbs) && kbs.ValueKind == JsonValueKind.Array)
                foreach (var kb in kbs.EnumerateArray())
                    if (kb.GetString() is { Length: > 0 } kbId) kbIds.Add(kbId);
        }

        var nameTag = OrgTag(orgId);

        // Knowledge bases wear the same tag, which is what still identifies one after the LLM that
        // pointed at it is gone — the shape every abandoned base from a failed sync ends up in.
        foreach (var kb in await ListKnowledgeBasesAsync(ct))
            if (GetStr(kb, "knowledge_base_name")?.Contains(nameTag, StringComparison.Ordinal) == true &&
                GetStr(kb, "knowledge_base_id") is { Length: > 0 } taggedKbId)
                kbIds.Add(taggedKbId);

        foreach (var listed in await ListAllAsync("/v2/list-agents", "/list-agents", HttpMethod.Post, ct))
        {
            var id = GetStr(listed, "agent_id");
            if (id is null || agentIds.Contains(id)) continue;

            // The name tag settles most of them without another call, and is the only thing that
            // still identifies an agent whose LLM was deleted out from under it.
            if (GetStr(listed, "agent_name")?.Contains(nameTag, StringComparison.Ordinal) == true)
            {
                agentIds.Add(id);
                continue;
            }

            // The list response carries no response engine, so ownership by LLM needs the agent
            // itself. Only reached on a connect, against one Retell account's worth of agents.
            if (llmIds.Count == 0) continue;
            try
            {
                var response = await _http.GetAsync($"/get-agent/{id}", ct);
                if (!response.IsSuccessStatusCode) continue;

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("response_engine", out var engine) &&
                    GetStr(engine, "llm_id") is { } engineLlmId && llmIds.Contains(engineLlmId))
                    agentIds.Add(id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read Retell agent {AgentId} while checking org {OrgId}'s resources",
                    id, orgId);
            }
        }
    }

    /// <summary>The raw JSON of every tool list on a Retell LLM, whichever shape it was built in.</summary>
    private static IEnumerable<string> ToolLists(JsonElement llm)
    {
        if (llm.TryGetProperty("general_tools", out var general)) yield return general.GetRawText();
        if (llm.TryGetProperty("states", out var states) && states.ValueKind == JsonValueKind.Array)
            foreach (var state in states.EnumerateArray())
                if (state.ValueKind == JsonValueKind.Object && state.TryGetProperty("tools", out var tools))
                    yield return tools.GetRawText();
    }

    /// <summary>Reads every page of a Retell list endpoint. Falls back to the unversioned path so a
    /// connect still works against an account or key that predates the v2 listings, and accepts a
    /// bare array or any of the wrappers Retell has used, because a listing that quietly returns
    /// nothing would look exactly like an account with nothing on it.</summary>
    private async Task<List<JsonElement>> ListAllAsync(string path, string fallbackPath, HttpMethod method,
        CancellationToken ct)
    {
        var items = new List<JsonElement>();
        string? paginationKey = null;

        for (var page = 0; page < 50; page++)
        {
            var query = $"?limit=1000{(paginationKey is null ? "" : $"&pagination_key={Uri.EscapeDataString(paginationKey)}")}";
            var response = await SendListAsync(path + query, method, ct);

            if (response.StatusCode == HttpStatusCode.NotFound && page == 0)
            {
                response.Dispose();
                response = await SendListAsync(fallbackPath + query, method, ct);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new RetellApiException(response.StatusCode,
                        $"Retell API {method} {path} failed ({(int)response.StatusCode}): {body}");

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var array = root.ValueKind == JsonValueKind.Array ? root : Wrapped(root);
                if (array.ValueKind != JsonValueKind.Array) return items;

                // Cloned because the JsonDocument backing them is disposed at the end of this page.
                items.AddRange(array.EnumerateArray().Select(e => e.Clone()));

                var more = root.ValueKind == JsonValueKind.Object &&
                           root.TryGetProperty("has_more", out var h) && h.ValueKind == JsonValueKind.True;
                paginationKey = more && root.TryGetProperty("pagination_key", out var k) ? k.GetString() : null;
                if (paginationKey is null) return items;
            }
        }

        return items;

        static JsonElement Wrapped(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) return default;
            foreach (var name in new[] { "items", "data", "results", "agents", "llms", "retell_llms" })
                if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array) return v;
            return default;
        }
    }

    private Task<HttpResponseMessage> SendListAsync(string pathAndQuery, HttpMethod method, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, pathAndQuery);
        // Retell's v2 listings are POSTs that take an optional filter body; an empty object asks
        // for everything, and sending it keeps servers that require a JSON body happy.
        if (method == HttpMethod.Post)
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return _http.SendAsync(request, ct);
    }

    /// <summary>The marker written into every agent's name so the operator's Retell dashboard says
    /// which tenant an agent serves, and so this code can still recognise its own work after the
    /// LLM behind an agent is gone. Organization ids are stable; names are not.</summary>
    private static string OrgTag(int orgId) => $"[org {orgId}]";

    /// <summary>Removes a knowledge base we no longer want, reporting whether it is gone. A base
    /// Retell has already forgotten counts as gone — that is the state being asked for. A refusal
    /// is never thrown: the sync that called this has already succeeded, and the pruning pass will
    /// come back for it.</summary>
    private async Task<bool> TryDeleteKnowledgeBaseAsync(string knowledgeBaseId, CancellationToken ct)
    {
        try
        {
            var response = await _http.DeleteAsync($"/delete-knowledge-base/{knowledgeBaseId}", ct);
            if (response.IsSuccessStatusCode || RetellApiException.IsMissing(
                    response.StatusCode, await response.Content.ReadAsStringAsync(ct)))
                return true;

            _logger.LogWarning("Could not delete old Retell knowledge base {KbId}: {Status}",
                knowledgeBaseId, response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete old Retell knowledge base {KbId}", knowledgeBaseId);
            return false;
        }
    }

    /// <summary>Updates the stored Retell resource, or creates a fresh one when the stored id no
    /// longer resolves. Resources can disappear behind our back — deleted in the Retell dashboard,
    /// wiped with an expired trial, or created under a since-replaced API key (see
    /// <see cref="RetellApiException.IsMissing"/>). Because a create is only attempted when the id is
    /// blank, a stale id would otherwise make every future sync PATCH something that does not exist
    /// and fail the same way forever, with no way back.</summary>
    private async Task<string> UpsertRemoteAsync(string? existingId, string updatePath, string createPath,
        object payload, string idField, int orgId, string resource, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(existingId))
        {
            try
            {
                return await PatchAsync($"{updatePath}/{existingId}", payload, idField, ct);
            }
            catch (RetellApiException ex) when (ex.IsMissingAsset)
            {
                _logger.LogWarning("Retell {Resource} {Id} for org {OrgId} does not exist on the connected " +
                    "Retell account — creating it there.", resource, existingId, orgId);
            }
        }

        return await PostAsync(createPath, payload, idField, ct);
    }

    private async Task<string> PostAsync(string path, object payload, string idField, CancellationToken ct) =>
        await SendAsync(HttpMethod.Post, path, payload, idField, ct);

    private async Task<string> PatchAsync(string path, object payload, string idField, CancellationToken ct) =>
        await SendAsync(HttpMethod.Patch, path, payload, idField, ct);

    private async Task<string> SendAsync(HttpMethod method, string path, object payload, string idField, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json"),
        };
        var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new RetellApiException(response.StatusCode,
                $"Retell API {method} {path} failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty(idField, out var id)
            ? id.GetString() ?? throw new InvalidOperationException($"Retell API returned empty {idField}.")
            : throw new InvalidOperationException($"Retell API response missing {idField}: {body}");
    }

    /// <summary>Settles a tenant's stored voice onto one of the platform voices this product
    /// offers, falling back to the operator's default. Everything the agent is given has to come
    /// from that set: expressive mode is only honoured for platform voices, so a voice from any
    /// other provider would take it away without saying so.</summary>
    private string MapVoice(string? voice) => RetellVoices.Resolve(voice, _defaultVoiceId);

    /// <summary>
    /// The call as Retell runs it: the tools every state may use, and the states themselves — each
    /// with its own prompt, only the tools that stage of the call needs, and the edges it may leave
    /// by. Fewer tools in front of the model on a turn means fewer wrong calls and a faster reply.
    ///
    /// Edges carry no parameters on purpose. Retell would substitute them into later state prompts
    /// as dynamic variables, which puts words the caller chose into the instructions the agent
    /// obeys. The conversation already holds the time they picked; nothing needs copying out of it.
    /// </summary>
    private (List<object> GeneralTools, List<object> States) BuildCallFlow(int orgId, string? transferNumber,
        IndustryProfile profile, bool hasServiceArea, AgentPrompt prompt)
    {
        // On-site trades cannot dispatch without an address, so Retell must collect it. The phone is
        // not required: when the caller wants the number they are calling from, use_caller_number
        // stands in for it and the server reads the number off the call.
        string[] requiredBookingFields = profile.IsFieldService
            ? ["name", "service", "start_time", "service_address"]
            : ["name", "service", "start_time"];

        var toolBase = $"{PublicBaseUrl}/api/v1/ai/tools/{orgId}";
        // Retell does not sign custom-function calls, so the shared secret rides in the URL.
        var token = RetellSignature.ToolToken(_apiKey!, orgId);

        // speak_during_execution makes the agent fill the silence while a tool runs. On a lookup
        // that returns instantly it produces a stock "one moment please" on every single turn,
        // which is most of what makes an agent sound like software — so it is opt-in per tool,
        // and the filler is phrased for that specific wait.
        object Custom(string name, string description, object properties, string[] required,
            string? whileWaiting = null) => new
        {
            type = "custom",
            name,
            description,
            url = $"{toolBase}/{name}?k={token}",
            speak_during_execution = whileWaiting is not null,
            speak_after_execution = true,
            execution_message_description = whileWaiting,
            parameters = new { type = "object", properties, required },
        };

        var str = (string desc) => new { type = "string", description = desc };

        // Never a number the model has to come up with: it cannot see the caller's, and asked for
        // "the one I'm calling from" it used to read out whatever number it could find.
        var useCallerNumber = str("'true' to use the number they are calling from; then leave phone out.");
        var phoneGiven = str("A different number they gave, if any.");

        var identifyCustomer = Custom("identify_customer",
            "Check whether the caller has been here before, by confirmed name and chosen number. Call it once while " +
            "taking booking details, or as soon as they say the job is where it was last time — it returns that address.",
            new { name = str("Full name, as confirmed"), use_caller_number = useCallerNumber, phone = phoneGiven },
            ["name"]);

        var checkAvailability = Custom("check_availability",
            "Open slots on ONE date. Call it once the caller has named a day, then keep the times for the rest of " +
            "the call. Call it again only for a different date, or after a booking failed because the slot went. " +
            "It only offers times inside opening hours, on open days, with someone free to take them.",
            new
            {
                date = str("YYYY-MM-DD, or the caller's own words: 'today', 'tomorrow', 'friday'"),
                service = str("Service name, to size the slot"),
            },
            ["date"],
            whileWaiting: "Say something short and natural like 'let me have a look' — one short phrase only, never mention checking a system.");

        var bookAppointment = Custom("book_appointment",
            "Book the appointment, only after the caller has said yes to the day, time and service read back to them.",
            new
            {
                name = str("Full name, exactly as they confirmed it"),
                use_caller_number = useCallerNumber,
                phone = phoneGiven,
                service = str("Requested service name"),
                date = str("The day as the caller put it ('tomorrow', 'next Friday') or the date check_availability " +
                           "returned. Always send it — it decides the day."),
                start_time = str("The agreed local time, 24-hour, e.g. 14:30 — exactly the time the caller agreed, " +
                                 "with no timezone or 'Z'."),
                service_address = str("Full address of the job with any access details (flat, gate code). Required for on-site trades."),
                is_emergency = str("'true' if it is urgent"),
                notes = str("The problem, and anything else the visit needs"),
            },
            requiredBookingFields,
            whileWaiting: "Say something short like 'right, let me get that booked in' — one short phrase only.");

        var cancelAppointment = Custom("cancel_appointment",
            "Cancel their appointment once they confirm. Needs the name on it; if they have several, ask which.",
            new
            {
                use_caller_number = useCallerNumber,
                phone = phoneGiven,
                name = str("Full name on the booking"),
                date = str("Appointment date, only if they have several"),
                service = str("Service, only if they have several"),
            },
            ["name"]);

        var rescheduleAppointment = Custom("reschedule_appointment",
            "Move their appointment to a new agreed time. Needs the name on it.",
            new
            {
                use_caller_number = useCallerNumber,
                phone = phoneGiven,
                name = str("Full name on the booking"),
                new_date = str("The new day as the caller put it ('tomorrow', 'next Friday') or the date " +
                               "check_availability returned. Always send it — it decides the day."),
                new_time = str("The agreed new local time, 24-hour, e.g. 09:15 — no timezone or 'Z'."),
                date = str("CURRENT appointment date, only if they have several"),
                service = str("Service, only if they have several"),
            },
            ["name", "new_time"]);

        var checkAppointment = Custom("check_appointment",
            "Check the caller's next appointment, its status and payment status.",
            new
            {
                use_caller_number = useCallerNumber,
                phone = phoneGiven,
                name = str("Full name on the booking"),
                date = str("Appointment date, only if they have several"),
                service = str("Service, only if they have several"),
            },
            Array.Empty<string>());

        // Prices, services, hours, policies and FAQs, served from this platform's database when the
        // caller asks. Offered in every state, because "how much is that?" can come at any point —
        // and with no filler line: the lookup is a few milliseconds, so a "one moment" would be the
        // only pause in it.
        var lookupInfo = Custom("lookup_info",
            "Look up a fact about this business — a price, a service, opening hours or closures, a policy, an FAQ. " +
            "Call it when the caller asks something you have not already been told on this call. Never answer " +
            "one of these from memory.",
            new { question = str("What they want to know, in a few words. Use a name from your topic list where one fits.") },
            ["question"]);

        var generalTools = new List<object>
        {
            lookupInfo,
            new { type = "end_call", name = "end_call", description = "End the call politely once the caller has no further requests." },
        };

        // Retell requires strict E.164 (no spaces/dashes) — normalize before sending.
        var transferE164 = PhoneUtil.Normalize(transferNumber);
        if (transferE164.Length > 0)
        {
            generalTools.Add(new
            {
                type = "transfer_call",
                name = "transfer_to_human",
                description = "Transfer the caller to a human when they ask for a person or when you are uncertain how to help.",
                transfer_destination = new { type = "predefined", number = transferE164 },
                transfer_option = new { type = "cold_transfer" },
            });
        }

        // Finding a time is where an address is taken, so the area check lives there — offered only
        // to businesses that have drawn a coverage area, so nobody else's agent changes — along with
        // the customer lookup wherever "the same place as last time" can come up.
        var schedulingTools = new List<object> { checkAvailability };
        if (hasServiceArea)
            schedulingTools.Add(Custom("check_service_area",
                "Check an address is real and inside the area this business covers, and say what to do next. Call it " +
                "as soon as the caller gives an address — even just a street and number, it searches this business's " +
                "own area first — and again only when they correct or add to it.",
                new { address = str("The address as the caller said it, however partial. Include the town only if they gave one.") },
                ["address"],
                whileWaiting: "Say something short and natural like 'let me just check we come out that way' — one short phrase only."));
        if (hasServiceArea || profile.IsFieldService)
            schedulingTools.Add(identifyCustomer);

        // Retell rejects a state machine it cannot resolve with a flat 4xx on the sync, naming
        // nothing. These two say which edge or which state is at fault, so a wrong name here is a
        // line number rather than an afternoon.
        object Edge(string to, string when) => AgentStates.All.Contains(to)
            ? new { destination_state_name = to, description = when }
            : throw new InvalidOperationException($"Retell edge points at unknown state '{to}'.");

        object State(string name, object[] stateTools, params object[] edges) => new
        {
            name,
            state_prompt = prompt.States.TryGetValue(name, out var statePrompt) ? statePrompt
                : throw new InvalidOperationException($"No prompt was built for Retell state '{name}'."),
            tools = stateTools,
            edges,
        };

        var states = new List<object>
        {
            State(AgentStates.Triage, [],
                Edge(AgentStates.Scheduling, "The caller wants to book a new appointment or visit."),
                Edge(AgentStates.ExistingBooking,
                    "The caller wants to check, move or cancel an appointment they already have.")),

            State(AgentStates.Scheduling, schedulingTools.ToArray(),
                Edge(AgentStates.Booking, profile.IsFieldService
                    ? "The caller has agreed an arrival window from the ones check_availability returned, and the address is taken."
                    : "The caller has agreed a time from the ones check_availability returned."),
                Edge(AgentStates.ExistingBooking, "The caller actually means an appointment they already have.")),

            State(AgentStates.Booking, [identifyCustomer, bookAppointment],
                Edge(AgentStates.Scheduling,
                    "The agreed time was taken before it could be booked, the caller wants a different time, " +
                    "or, once this booking is done, they want another new one."),
                Edge(AgentStates.ExistingBooking,
                    "The caller wants to check, move or cancel an appointment they already have.")),

            State(AgentStates.ExistingBooking, [checkAppointment, rescheduleAppointment, cancelAppointment, checkAvailability],
                Edge(AgentStates.Scheduling, "The caller wants to book a new appointment as well.")),
        };

        return (generalTools, states);
    }
}
