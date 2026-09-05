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
    /// <summary>Update this tenant's existing LLM, agent and knowledge base in place, creating any
    /// of them that is genuinely missing. Every automatic sync uses this — a tenant editing their
    /// services must not churn Retell resources.</summary>
    Update,

    /// <summary>First connect: delete every Retell resource this tenant still owns or left
    /// detached, then build the agent, LLM and knowledge base from scratch. Only ever reached from
    /// the platform console's Connect action, and only for the one organization it was clicked on.</summary>
    Fresh,
}

public interface IRetellService
{
    Task<bool> IsConfiguredAsync();
    Task<RetellSyncResult> SyncAgentAsync(int orgId, RetellSyncMode mode = RetellSyncMode.Update,
        CancellationToken ct = default);
    Task<int> BackfillCallsAsync(int orgId, CancellationToken ct = default);

    /// <summary>Detaches this tenant's number from their agent, or points it back. Refusing the
    /// agent's tools stops it doing anything useful but the call is still answered and still costs
    /// minutes — which is no good when the reason for restricting is the bill. Unbinding the number
    /// is what actually stops the calls. Returns a message to show the operator when the change
    /// could not be made upstream, else null.</summary>
    Task<string?> SetInboundRoutingAsync(int orgId, bool enabled, CancellationToken ct = default);
}

/// <summary>
/// Pushes each tenant's AI configuration to Retell AI (SRS §14, §15):
/// creates/updates a Retell LLM (prompt + custom tools) and the Agent bound to it.
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
    private readonly ILogger<RetellService> _logger;

    /// <summary>The model behind every tenant's agent. Sent on every sync rather than left to
    /// Retell's account default, so the model a tenant is answered by is decided here and moves
    /// for everyone at once — and so a change to the Retell account cannot quietly alter it.</summary>
    private const string LlmModel = "gemini-3.0-flash";

    // Loaded per operation from the centralized platform connection (DB, config fallback).
    private string? _apiKey;
    private string _apiBaseUrl = "https://api.retellai.com";
    private string? _webhookBaseUrl;
    private string? _defaultVoiceId;

    private static readonly JsonSerializerOptions JsonOpts = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public RetellService(HttpClient http, IRetellConnectionRepository connection, ISettingsRepository settings,
        IPromptBuilderService prompts, ICallRepository calls, ICustomerRepository customers,
        ILogger<RetellService> logger)
    {
        _http = http;
        _connection = connection;
        _settings = settings;
        _prompts = prompts;
        _calls = calls;
        _customers = customers;
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
        var prompt = await _prompts.BuildSystemPromptAsync(orgId);

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

            // 1) Push bulky, frequently-changing content (catalogues, FAQs, policies) to a
            //    Retell knowledge base so it is retrieved on demand instead of sitting in
            //    the prompt on every turn.
            var previousKbId = agent.RetellKnowledgeBaseId;
            var (kbId, kbIsNew) = await SyncKnowledgeBaseAsync(
                orgId, org.Name, previousKbId, forceCreate: mode == RetellSyncMode.Fresh, ct);
            if (kbId is not null) agent.RetellKnowledgeBaseId = kbId;

            // 2) Create or update the Retell LLM (prompt + tools + knowledge base)
            var llmPayload = new
            {
                model = LlmModel,
                general_prompt = prompt,
                begin_message = string.IsNullOrWhiteSpace(agent.Greeting) ? null : agent.Greeting,
                general_tools = BuildTools(orgId, agent.TransferNumber, IndustryTemplates.Resolve(org.Industry)),
                knowledge_base_ids = string.IsNullOrWhiteSpace(agent.RetellKnowledgeBaseId)
                    ? Array.Empty<string>()
                    : new[] { agent.RetellKnowledgeBaseId },
            };

            agent.RetellLlmId = await UpsertRemoteAsync(agent.RetellLlmId, "/update-retell-llm",
                "/create-retell-llm", llmPayload, "llm_id", orgId, "LLM", ct);

            // Persist immediately so a later failure can't orphan the created LLM/knowledge base.
            await _settings.UpsertAgentConfigAsync(agent);

            // A knowledge base that had to be replaced rather than updated in place leaves the old
            // one behind, and it is only safe to remove once the LLM points at its successor.
            if (kbIsNew && !string.IsNullOrWhiteSpace(previousKbId) && previousKbId != kbId)
                await TryDeleteKnowledgeBaseAsync(previousKbId, ct);

            // 3) Create or update the Agent bound to that LLM
            var agentPayload = new
            {
                // The org id rides along in the name so the operator's Retell dashboard shows which
                // tenant each agent serves, and so a later connect can recognise this agent as ours
                // even if the LLM it points at has been deleted. Never seen by callers or tenants.
                agent_name = $"{org.Name} — Frontly {AgentNameTag(orgId)}",
                voice_id = MapVoice(agent.Voice),
                language = string.IsNullOrWhiteSpace(agent.Language) ? "en-US" : agent.Language,
                response_engine = new { type = "retell-llm", llm_id = agent.RetellLlmId },
                webhook_url = $"{PublicBaseUrl}/api/v1/webhooks/retell",

                // On for every tenant, not a per-account option: it lets the agent colour what it
                // says — a sigh, a beat before bad news, warmth on a greeting — which is most of
                // what separates a receptionist from a recording. Retell only honours it for its
                // own platform voices, which is why MapVoice never returns anything else.
                enable_expressive_mode = true,
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
    /// Pulls recent calls for this tenant's agent from Retell and stores any that are missing.
    /// Webhooks are fire-and-forget: if the API or tunnel is down when a call ends, that call
    /// log is lost forever. This reconciles from Retell's own record, so history is complete
    /// regardless of what the webhook delivered. Returns how many calls were imported.
    /// </summary>
    public async Task<int> BackfillCallsAsync(int orgId, CancellationToken ct = default)
    {
        await LoadConnectionAsync();
        if (string.IsNullOrWhiteSpace(_apiKey)) return 0;

        var agent = await _settings.GetAgentConfigAsync(orgId);
        if (string.IsNullOrWhiteSpace(agent?.RetellAgentId)) return 0;

        var payload = new
        {
            filter_criteria = new { agent_id = new[] { agent.RetellAgentId } },
            limit = 100,
            sort_order = "descending",
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/v2/list-calls")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Retell list-calls failed ({Status}): {Body}", (int)response.StatusCode, body);
            return 0;
        }

        using var doc = JsonDocument.Parse(body);
        var calls = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : doc.RootElement.TryGetProperty("calls", out var c) ? c : default;
        if (calls.ValueKind != JsonValueKind.Array) return 0;

        var imported = 0;
        foreach (var call in calls.EnumerateArray())
        {
            var callId = GetStr(call, "call_id");
            if (callId is null || await _calls.ExistsByRetellIdAsync(orgId, callId)) continue;

            var startedMs = GetNum(call, "start_timestamp");
            var endedMs = GetNum(call, "end_timestamp");
            var from = PhoneUtil.Normalize(GetStr(call, "from_number"));
            if (from.Length == 0) from = "web-call";

            var disconnect = GetStr(call, "disconnection_reason");
            var status = disconnect is "dial_no_answer" or "dial_busy" or "dial_failed" ? "Missed"
                : disconnect == "call_transfer" ? "Transferred"
                : "Completed";

            string? summary = null;
            if (call.TryGetProperty("call_analysis", out var analysis))
                summary = GetStr(analysis, "call_summary");

            var customer = await _customers.FindReturningAsync(orgId, from, null, null);

            await _calls.CreateAsync(new Domain.CallLog
            {
                OrganizationId = orgId,
                CustomerId = customer?.Id,
                RetellCallId = callId,
                FromNumber = from,
                Status = status,
                // The same reading the live webhook takes, so a call re-imported here is never
                // billed differently from the one that arrived by itself.
                DurationSeconds = CallDuration.Resolve(startedMs, endedMs, GetNum(call, "duration_ms")),
                Transcript = GetStr(call, "transcript"),
                RecordingUrl = GetStr(call, "recording_url"),
                Summary = summary,
                StartedAt = startedMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(startedMs).UtcDateTime
                    : DateTime.UtcNow,
            });
            imported++;
        }

        if (imported > 0)
            _logger.LogInformation("Backfilled {Count} call(s) from Retell for org {OrgId}", imported, orgId);
        return imported;
    }

    private static string? GetStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long GetNum(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

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

    /// <summary>Brings the tenant's reference content to Retell and returns the knowledge base the
    /// LLM should point at, along with whether that is a newly created one the caller has to swap
    /// over to. Returns null when there is nothing to upload.
    ///
    /// An existing knowledge base is updated in place — the new documents are added, then the
    /// sources that were there before are removed — so its id survives. That matters because a
    /// sync is not rare: every tenant edit queues one and every connected tenant is re-synced when
    /// the API restarts. Creating a base per sync (which is what this used to do unconditionally)
    /// meant one abandoned knowledge base per organization per restart piling up on the shared
    /// Retell account. A base is only created when there is none, when <paramref name="forceCreate"/>
    /// asks for a clean one, or when Retell will not accept the in-place update.</summary>
    private async Task<(string? Id, bool IsNew)> SyncKnowledgeBaseAsync(
        int orgId, string orgName, string? existingId, bool forceCreate, CancellationToken ct)
    {
        var docs = await _prompts.BuildKnowledgeDocumentsAsync(orgId);
        if (docs.Count == 0) return (null, false);

        if (!forceCreate && !string.IsNullOrWhiteSpace(existingId))
        {
            try
            {
                await ReplaceKnowledgeBaseSourcesAsync(existingId, docs, ct);
                _logger.LogInformation(
                    "Retell knowledge base {KbId} updated in place for org {OrgId} with {Count} document(s)",
                    existingId, orgId, docs.Count);
                return (existingId, false);
            }
            catch (Exception ex)
            {
                // Falling back to a replacement keeps a re-sync working when the base was deleted
                // in the Retell dashboard or the update is refused; the caller deletes the old one.
                _logger.LogWarning(ex,
                    "Could not update Retell knowledge base {KbId} for org {OrgId} in place — creating a replacement.",
                    existingId, orgId);
            }
        }

        using var form = new MultipartFormDataContent
        {
            { new StringContent($"{orgName} — reference ({DateTime.UtcNow:yyyyMMddHHmmss})"), "knowledge_base_name" },
            { KnowledgeTextsContent(docs), "knowledge_base_texts" },
        };

        var response = await _http.PostAsync("/create-knowledge-base", form, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Retell API POST /create-knowledge-base failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.TryGetProperty("knowledge_base_id", out var v) ? v.GetString() : null;
        _logger.LogInformation("Retell knowledge base {KbId} created for org {OrgId} with {Count} document(s)",
            id, orgId, docs.Count);
        return (id, id is not null);
    }

    /// <summary>Swaps the contents of an existing knowledge base for the current documents. Adds
    /// before it removes: a base that briefly holds the old and new copies of a document answers
    /// callers with a duplicate, while one that briefly holds nothing answers them with nothing.</summary>
    private async Task ReplaceKnowledgeBaseSourcesAsync(
        string knowledgeBaseId, List<KnowledgeDocument> docs, CancellationToken ct)
    {
        // Reading the base first also settles whether it still exists — a stale id has to surface
        // here as a failure so the caller can fall back to creating a replacement.
        var stale = await ListKnowledgeBaseSourceIdsAsync(knowledgeBaseId, ct);

        using var form = new MultipartFormDataContent
        {
            { KnowledgeTextsContent(docs), "knowledge_base_texts" },
        };

        var response = await _http.PostAsync($"/add-knowledge-base-sources/{knowledgeBaseId}", form, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new RetellApiException(response.StatusCode,
                $"Retell API POST /add-knowledge-base-sources/{knowledgeBaseId} failed " +
                $"({(int)response.StatusCode}): {body}");
        }

        foreach (var sourceId in stale)
        {
            try
            {
                var deleted = await _http.DeleteAsync(
                    $"/delete-knowledge-base-source/{knowledgeBaseId}/{sourceId}", ct);
                if (!deleted.IsSuccessStatusCode)
                    _logger.LogWarning(
                        "Could not remove superseded source {SourceId} from Retell knowledge base {KbId}: {Status}",
                        sourceId, knowledgeBaseId, deleted.StatusCode);
            }
            catch (Exception ex)
            {
                // The new content is already in and the LLM already points here, so a source that
                // will not delete is stale content, not a broken sync.
                _logger.LogWarning(ex,
                    "Could not remove superseded source {SourceId} from Retell knowledge base {KbId}",
                    sourceId, knowledgeBaseId);
            }
        }
    }

    private async Task<List<string>> ListKnowledgeBaseSourceIdsAsync(string knowledgeBaseId, CancellationToken ct)
    {
        var response = await _http.GetAsync($"/get-knowledge-base/{knowledgeBaseId}", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new RetellApiException(response.StatusCode,
                $"Retell API GET /get-knowledge-base/{knowledgeBaseId} failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("knowledge_base_sources", out var sources) ||
            sources.ValueKind != JsonValueKind.Array)
            return [];

        return sources.EnumerateArray()
            .Select(s => GetStr(s, "source_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();
    }

    private static StringContent KnowledgeTextsContent(List<KnowledgeDocument> docs) =>
        new(JsonSerializer.Serialize(docs.Select(d => new { title = d.Title, text = d.Text })),
            Encoding.UTF8, "application/json");

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
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogInformation("Deleted Retell {Resource} {Id} for org {OrgId}.", resource, id, orgId);
                    return;
                }

                var body = await response.Content.ReadAsStringAsync(ct);
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

            // Matched against the raw JSON of the tool list so the shape of a tool never matters,
            // only the address it points at.
            var tools = llm.TryGetProperty("general_tools", out var t) ? t.GetRawText() : "";
            if (!tools.Contains(toolPath, StringComparison.Ordinal)) continue;

            llmIds.Add(id);
            if (llm.TryGetProperty("knowledge_base_ids", out var kbs) && kbs.ValueKind == JsonValueKind.Array)
                foreach (var kb in kbs.EnumerateArray())
                    if (kb.GetString() is { Length: > 0 } kbId) kbIds.Add(kbId);
        }

        var nameTag = AgentNameTag(orgId);
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
    private static string AgentNameTag(int orgId) => $"[org {orgId}]";

    private async Task TryDeleteKnowledgeBaseAsync(string knowledgeBaseId, CancellationToken ct)
    {
        try
        {
            var response = await _http.DeleteAsync($"/delete-knowledge-base/{knowledgeBaseId}", ct);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Could not delete old Retell knowledge base {KbId}: {Status}",
                    knowledgeBaseId, response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete old Retell knowledge base {KbId}", knowledgeBaseId);
        }
    }

    /// <summary>Updates the stored Retell resource, or creates a fresh one when the stored id no
    /// longer resolves. Resources can disappear behind our back — deleted in the Retell dashboard,
    /// wiped with an expired trial, or created under a since-replaced API key. Because a create is
    /// only attempted when the id is blank, a stale id would otherwise make every future sync PATCH
    /// something that does not exist and fail with the same 404 forever, with no way back.</summary>
    private async Task<string> UpsertRemoteAsync(string? existingId, string updatePath, string createPath,
        object payload, string idField, int orgId, string resource, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(existingId))
        {
            try
            {
                return await PatchAsync($"{updatePath}/{existingId}", payload, idField, ct);
            }
            catch (RetellApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Retell {Resource} {Id} for org {OrgId} no longer exists — recreating it.",
                    resource, existingId, orgId);
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

    private List<object> BuildTools(int orgId, string? transferNumber, IndustryProfile profile)
    {
        // On-site trades cannot dispatch without an address, so Retell must collect it.
        string[] requiredBookingFields = profile.IsFieldService
            ? ["name", "phone", "service", "start_time", "service_address"]
            : ["name", "phone", "service", "start_time"];

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

        var tools = new List<object>
        {
            Custom("identify_customer",
                "Look up whether the caller has been here before, by phone number. Call this near the END of the call, " +
                "once you are taking their details for a booking — never open the call by asking for a number. " +
                "Call it at most once per call.",
                new { phone = str("Caller phone number in international format") },
                ["phone"]),

            Custom("check_availability",
                "Open appointment slots on ONE date. Call this once, after the caller has named the day they want, " +
                "then keep the times it returns and work from them for the rest of the call. Do NOT call it again for " +
                "a date you have already checked, do NOT call it before the caller has named a day, and do NOT call it " +
                "to re-confirm a time you have already offered. Call it again only for a different date, or after a " +
                "booking failed because the slot was taken. Respects the business's working hours and closed days, and " +
                "counts the staff actually on duty — a time comes back only while someone is free to take it, which is " +
                "why the same time can still be free after you have just booked it for someone else.",
                new
                {
                    date = str("The date to check: YYYY-MM-DD, or a relative day the caller used such as 'today', 'tomorrow' or 'friday'"),
                    service = str("Optional service name to size the slot"),
                },
                ["date"],
                whileWaiting: "Say something short and natural like 'let me have a look' — one short phrase only, never mention checking a system."),

            Custom("book_appointment",
                "Book an appointment once the caller has confirmed a date, time and service. Never book without explicit caller confirmation. " +
                "If this business travels to the customer, the full service address is mandatory.",
                new
                {
                    name = str("Caller full name"),
                    phone = str("Caller phone number"),
                    service = str("Requested service name"),
                    date = str("The day being booked, exactly as the caller put it ('tomorrow', 'next Friday') " +
                               "or the date check_availability returned. Always send this — it decides the day, " +
                               "so you never have to work one out yourself."),
                    start_time = str("Confirmed start date-time in ISO 8601 format, e.g. 2026-07-20T14:00:00. " +
                                     "Only the time of day is taken from this when 'date' is given."),
                    service_address = str("Full address where the work will be carried out, including city and any access details (apartment, gate code). Required for on-site trades."),
                    is_emergency = str("'true' if the caller describes an urgent or emergency situation, otherwise 'false'"),
                    notes = str("Description of the problem or any other relevant detail"),
                },
                requiredBookingFields,
                whileWaiting: "Say something short like 'right, let me get that booked in' — one short phrase only."),

            Custom("cancel_appointment",
                "Cancel the caller's upcoming appointment after they confirm they want to cancel. Always collect the caller's full name to verify identity. If they have several appointments, ask which service or date they mean.",
                new
                {
                    phone = str("Caller phone number"),
                    name = str("Caller full name, used to verify identity"),
                    date = str("Optional appointment date YYYY-MM-DD if the caller has several"),
                    service = str("Optional service name if the caller has several appointments"),
                },
                ["phone", "name"]),

            Custom("reschedule_appointment",
                "Move the caller's upcoming appointment to a new confirmed time. Always collect the caller's full name to verify identity.",
                new
                {
                    phone = str("Caller phone number"),
                    name = str("Caller full name, used to verify identity"),
                    new_date = str("The day they are moving to, exactly as the caller put it ('tomorrow', " +
                                   "'next Friday') or the date check_availability returned. Always send this — " +
                                   "it decides the day, so you never have to work one out yourself."),
                    new_time = str("New start date-time in ISO 8601 format. Only the time of day is taken " +
                                   "from this when 'new_date' is given."),
                    date = str("Optional CURRENT appointment date YYYY-MM-DD, only to tell apart several bookings"),
                    service = str("Optional service name if the caller has several appointments"),
                },
                ["phone", "name", "new_time"]),

            Custom("check_appointment",
                "Check the caller's next appointment, its status and payment status.",
                new
                {
                    phone = str("Caller phone number"),
                    date = str("Optional appointment date YYYY-MM-DD"),
                    service = str("Optional service name"),
                },
                ["phone"]),

            // No pricing tool: the service and product catalogues go up as knowledge-base documents
            // on this same sync, so the agent can answer a price question from context. A tool call
            // for something already in front of it only put a silence in the middle of the answer.

            new { type = "end_call", name = "end_call", description = "End the call politely once the caller has no further requests." },
        };

        // Retell requires strict E.164 (no spaces/dashes) — normalize before sending.
        var transferE164 = PhoneUtil.Normalize(transferNumber);
        if (transferE164.Length > 0)
        {
            tools.Add(new
            {
                type = "transfer_call",
                name = "transfer_to_human",
                description = "Transfer the caller to a human when they ask for a person or when you are uncertain how to help.",
                transfer_destination = new { type = "predefined", number = transferE164 },
                transfer_option = new { type = "cold_transfer" },
            });
        }

        return tools;
    }
}
