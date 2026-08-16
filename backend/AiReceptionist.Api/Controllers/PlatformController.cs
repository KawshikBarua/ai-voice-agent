using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.Api.Controllers;

public record PlatformRetellConnectionRequest(
    string? ApiKey, string? ApiBaseUrl, string? WebhookBaseUrl, string? DefaultVoiceId, bool? VerifySignature);

/// <summary>The two numbers only the platform operator may set: the Retell number that routes
/// inbound calls to this tenant's agent, and the number a cold transfer dials.</summary>
public record PlatformNumbersRequest(string? TransferNumber, string? RetellPhoneNumber);

/// <summary>The editable wording of the system prompt. A blank (or default-identical) section is
/// stored as null so it keeps tracking the built-in default rather than freezing today's text.</summary>
public record PlatformPromptTemplateRequest(
    string? Persona,
    string? CoreRules,
    string? ConversationGuide,
    string? FieldServiceGuide,
    string? ToolPolicy,
    bool? ResetToDefaults);

/// <summary>
/// Platform administration, driven by the separate super admin console
/// (AiReceptionist.SuperAdmin) and authenticated with a shared key rather than a tenant JWT.
///
/// Retell agent creation lives here rather than in the console because it depends on the
/// generated final prompt, the live-call tool definitions and the webhook URLs — all of which
/// this API already owns and re-syncs in the background when a tenant edits their knowledge
/// base. Keeping one implementation is what stops the console and the background worker from
/// drifting into producing two different agents.
/// </summary>
[ApiController]
[Route("api/v1/platform")]
[PlatformKey]
public class PlatformController : ControllerBase
{
    private readonly IRetellService _retell;
    private readonly ISettingsRepository _settings;
    private readonly IRetellConnectionRepository _connection;
    private readonly IPromptTemplateRepository _promptTemplate;
    private readonly IPromptBuilderService _prompts;
    private readonly IAuditRepository _audit;
    private readonly IConfiguration _config;
    private readonly ILogger<PlatformController> _logger;

    public PlatformController(IRetellService retell, ISettingsRepository settings,
        IRetellConnectionRepository connection, IPromptTemplateRepository promptTemplate,
        IPromptBuilderService prompts, IAuditRepository audit,
        IConfiguration config, ILogger<PlatformController> logger)
    {
        _retell = retell;
        _settings = settings;
        _connection = connection;
        _promptTemplate = promptTemplate;
        _prompts = prompts;
        _audit = audit;
        _config = config;
        _logger = logger;
    }

    // ---------- system prompt ----------

    /// <summary>The platform-wide prompt wording: what is in force, alongside the built-in defaults
    /// so the console can show which sections have actually been changed and offer to restore them.</summary>
    [HttpGet("prompt-template")]
    public async Task<IActionResult> GetPromptTemplate()
    {
        var stored = await _promptTemplate.GetAsync();
        var effective = await _promptTemplate.GetEffectiveAsync();

        var customised = new List<string>();
        if (!string.IsNullOrWhiteSpace(stored?.Persona)) customised.Add("How the agent talks");
        if (!string.IsNullOrWhiteSpace(stored?.CoreRules)) customised.Add("Rules it never breaks");
        if (!string.IsNullOrWhiteSpace(stored?.ConversationGuide)) customised.Add("Call shape (on premises)");
        if (!string.IsNullOrWhiteSpace(stored?.FieldServiceGuide)) customised.Add("Call shape (on site)");
        if (!string.IsNullOrWhiteSpace(stored?.ToolPolicy)) customised.Add("Working with the tools");

        return Ok(ApiResponse<object>.Ok(new
        {
            effective.Persona,
            effective.CoreRules,
            effective.ConversationGuide,
            effective.FieldServiceGuide,
            effective.ToolPolicy,
            effective.ModifiedAt,
            customisedSections = customised,
            defaults = new
            {
                Persona = PromptDefaults.Persona,
                CoreRules = PromptDefaults.CoreRules,
                ConversationGuide = PromptDefaults.ConversationGuide,
                FieldServiceGuide = PromptDefaults.FieldServiceGuide,
                ToolPolicy = PromptDefaults.ToolPolicy,
            },
        }));
    }

    /// <summary>
    /// Rewrites the prompt for the whole platform.
    ///
    /// This stores the wording and nothing else. It deliberately does NOT push to Retell: a save
    /// here would otherwise rewrite every tenant's live agent at once, which is far too much to
    /// hang off one button — a bad edit would reach every organization on the platform before
    /// anyone had looked at it. The new wording reaches an agent the next time that tenant syncs
    /// (the Re-sync button, or any tenant edit that already triggers a background sync).
    /// </summary>
    [HttpPut("prompt-template")]
    public async Task<IActionResult> UpdatePromptTemplate(PlatformPromptTemplateRequest request)
    {
        var reset = request.ResetToDefaults == true;

        var template = new Domain.PromptTemplate
        {
            Persona = reset ? null : Override(request.Persona, PromptDefaults.Persona),
            CoreRules = reset ? null : Override(request.CoreRules, PromptDefaults.CoreRules),
            ConversationGuide = reset ? null : Override(request.ConversationGuide, PromptDefaults.ConversationGuide),
            FieldServiceGuide = reset ? null : Override(request.FieldServiceGuide, PromptDefaults.FieldServiceGuide),
            ToolPolicy = reset ? null : Override(request.ToolPolicy, PromptDefaults.ToolPolicy),
        };

        await _promptTemplate.UpsertAsync(template, userId: null);

        // Nothing is pushed to Retell here. The count is reported so the operator knows how many
        // agents are still answering with the previous wording, and can re-sync the ones they mean to.
        var connected = (await _settings.GetConnectedOrganizationIdsAsync()).ToList();

        _logger.LogInformation(
            "Platform prompt template {Action} from the super admin console. {Count} connected agent(s) " +
            "keep their current prompt until they are next synced.",
            reset ? "reset to defaults" : "updated", connected.Count);

        var what = reset ? "Prompt restored to the platform defaults." : "Prompt saved.";
        var howMany = connected.Count switch
        {
            0 => "No agents are connected yet, so it will apply the first time one is.",
            1 => "The one connected agent keeps its current prompt until you re-sync it.",
            _ => $"The {connected.Count} connected agents keep their current prompt until you re-sync them.",
        };

        return Ok(ApiResponse<object>.Ok(new { connectedAgents = connected.Count }, $"{what} {howMany}"));
    }

    /// <summary>The final prompt one tenant's agent will actually receive — the platform wording with
    /// that tenant's industry, hours, closures and numbers filled in. The console shows it read-only:
    /// prompt changes are easy to get wrong in the abstract and obvious in the concrete.</summary>
    [HttpGet("prompt-template/preview/{orgId:int}")]
    public async Task<IActionResult> PreviewPrompt(int orgId)
    {
        var org = await _settings.GetOrganizationAsync(orgId);
        if (org is null) return NotFound(ApiResponse<object>.Fail("Unknown organization."));

        return Ok(ApiResponse<object>.Ok(new
        {
            organizationId = orgId,
            organizationName = org.Name,
            prompt = await _prompts.BuildSystemPromptAsync(orgId),
        }));
    }

    /// <summary>Blank, or word-for-word the default, means "keep following the default" — stored as
    /// null so a later improvement to the built-in text still reaches this platform.</summary>
    private static string? Override(string? value, string @default)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed == @default.Trim() ? null : trimmed;
    }

    /// <summary>The platform-wide Retell connection. The key is never returned in full — only
    /// whether one is set and its last four characters.</summary>
    [HttpGet("retell/connection")]
    public async Task<IActionResult> GetRetellConnection()
    {
        var stored = await _connection.GetAsync();
        var effective = await _connection.GetEffectiveAsync();

        return Ok(ApiResponse<object>.Ok(new
        {
            apiKeyConfigured = !string.IsNullOrWhiteSpace(effective.ApiKey),
            apiKeyPreview = Mask(effective.ApiKey),
            apiBaseUrl = effective.ApiBaseUrl,
            webhookBaseUrl = effective.WebhookBaseUrl,
            defaultVoiceId = effective.DefaultVoiceId,
            verifySignature = effective.VerifySignature,
            modifiedAt = effective.ModifiedAt,
            // A key coming from appsettings cannot be rotated from the console, which is worth
            // surfacing rather than leaving the operator to wonder why saving appears to do nothing.
            keySource = !string.IsNullOrWhiteSpace(stored?.ApiKey) ? "database"
                : !string.IsNullOrWhiteSpace(_config["Retell:ApiKey"]) ? "configuration"
                : null,
        }));
    }

    [HttpPut("retell/connection")]
    public async Task<IActionResult> UpdateRetellConnection(PlatformRetellConnectionRequest request)
    {
        var existing = await _connection.GetAsync();
        var updated = new Domain.RetellConnection
        {
            // Blank key = keep the one already stored, so URLs can be edited without the operator
            // having to re-enter (and therefore re-handle) the secret.
            ApiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? existing?.ApiKey : request.ApiKey.Trim(),
            ApiBaseUrl = string.IsNullOrWhiteSpace(request.ApiBaseUrl)
                ? existing?.ApiBaseUrl ?? "https://api.retellai.com" : request.ApiBaseUrl.Trim(),
            WebhookBaseUrl = request.WebhookBaseUrl ?? existing?.WebhookBaseUrl,
            DefaultVoiceId = request.DefaultVoiceId ?? existing?.DefaultVoiceId,
            VerifySignature = request.VerifySignature ?? existing?.VerifySignature ?? true,
        };

        await _connection.UpsertAsync(updated, userId: null);
        _logger.LogInformation("Platform Retell connection updated from the super admin console.");

        return Ok(ApiResponse<object>.Ok(new
        {
            apiKeyConfigured = !string.IsNullOrWhiteSpace(updated.ApiKey),
            apiKeyPreview = Mask(updated.ApiKey),
            apiBaseUrl = updated.ApiBaseUrl,
            webhookBaseUrl = updated.WebhookBaseUrl,
            defaultVoiceId = updated.DefaultVoiceId,
            verifySignature = updated.VerifySignature,
            keySource = "database",
        }, "Retell connection updated."));
    }

    /// <summary>Creates (first connect) or updates one tenant's Retell LLM and agent from its
    /// generated final prompt, voice, greeting, transfer number and live-call tools.</summary>
    [HttpPost("retell/{orgId:int}/sync")]
    public async Task<IActionResult> SyncAgent(int orgId)
    {
        if (await _settings.GetOrganizationAsync(orgId) is null)
            return NotFound(ApiResponse<object>.Fail("Unknown organization."));

        var result = await _retell.SyncAgentAsync(orgId);
        if (!result.Success)
            return BadRequest(ApiResponse<object>.Fail(result.Error ?? "Retell sync failed."));

        await _audit.LogAsync(orgId, null, "RetellAgentSynced", $"AgentId={result.AgentId} (platform console)");

        return Ok(ApiResponse<object>.Ok(new
        {
            agentId = result.AgentId,
            llmId = result.LlmId,
            phoneNumber = result.PhoneNumber,
            phoneWarning = result.PhoneWarning,
        }, "Retell agent synced."));
    }

    /// <summary>
    /// Sets the tenant's transfer number and Retell phone number. Both are platform property: the
    /// Retell number decides which tenant's agent answers it on the shared Retell account, and the
    /// transfer number is dialled by Retell when the agent hands a live caller to a human.
    ///
    /// A connected tenant is re-synced immediately, because neither number takes effect until the
    /// agent is pushed to Retell — saving without syncing would look like it worked while inbound
    /// calls carried on going nowhere.
    /// </summary>
    [HttpPut("retell/{orgId:int}/numbers")]
    public async Task<IActionResult> UpdateNumbers(int orgId, PlatformNumbersRequest request)
    {
        var org = await _settings.GetOrganizationAsync(orgId);
        if (org is null)
            return NotFound(ApiResponse<object>.Fail("Unknown organization."));

        var agent = await _settings.GetAgentConfigAsync(orgId);
        if (agent is null)
            return NotFound(ApiResponse<object>.Fail(
                "This organization has no agent configuration yet. Connect it to Retell first."));

        // Both numbers reach an external API (and the Retell number reaches a URL), so they are
        // parsed strictly rather than stripped of stray characters: "+15551234567?x=1" must be
        // rejected, not quietly turned into a different valid-looking number.
        var transfer = request.TransferNumber?.Trim();
        if (string.IsNullOrEmpty(transfer))
        {
            agent.TransferNumber = null;
        }
        else
        {
            var e164 = PhoneUtil.ToE164(transfer);
            if (e164 is null)
                return BadRequest(ApiResponse<object>.Fail(
                    "Enter the transfer number in international format, e.g. +15551234567."));
            agent.TransferNumber = e164;
        }

        var phone = request.RetellPhoneNumber?.Trim();
        if (string.IsNullOrEmpty(phone))
        {
            agent.RetellPhoneNumber = null;
        }
        else
        {
            var e164 = PhoneUtil.ToE164(phone);
            if (e164 is null)
                return BadRequest(ApiResponse<object>.Fail(
                    "Enter the Retell phone number in international format, e.g. +15551234567."));

            // Every tenant shares one Retell account, so a number can only answer for one of them.
            if (await _settings.FindOrganizationByRetellPhoneNumberAsync(e164, orgId) is not null)
                return BadRequest(ApiResponse<object>.Fail(
                    "That phone number is already assigned to another organization."));

            agent.RetellPhoneNumber = e164;
        }

        await _settings.UpsertAgentConfigAsync(agent);
        await _audit.LogAsync(orgId, null, "AgentNumbersChanged",
            $"Transfer={agent.TransferNumber ?? "none"}, Retell={agent.RetellPhoneNumber ?? "none"} (platform console)");

        if (string.IsNullOrWhiteSpace(agent.RetellAgentId))
            return Ok(ApiResponse<object>.Ok(new
            {
                transferNumber = agent.TransferNumber,
                retellPhoneNumber = agent.RetellPhoneNumber,
                synced = false,
            }, "Numbers saved. They take effect when the organization is connected to Retell."));

        var result = await _retell.SyncAgentAsync(orgId);
        if (!result.Success)
            return Ok(ApiResponse<object>.Ok(new
            {
                transferNumber = agent.TransferNumber,
                retellPhoneNumber = agent.RetellPhoneNumber,
                synced = false,
                phoneWarning = result.Error,
            }, $"Numbers saved, but the agent could not be re-synced: {result.Error} Use Re-sync to retry."));

        return Ok(ApiResponse<object>.Ok(new
        {
            transferNumber = agent.TransferNumber,
            retellPhoneNumber = agent.RetellPhoneNumber,
            synced = true,
            phoneWarning = result.PhoneWarning,
        }, result.PhoneWarning is null
            ? "Numbers saved and pushed to Retell."
            : $"Numbers saved and pushed to Retell. {result.PhoneWarning}"));
    }

    /// <summary>Detaches the stored Retell agent/LLM ids from this tenant. The resources remain on
    /// the Retell account, so a disconnect is reversible by connecting again.</summary>
    [HttpPost("retell/{orgId:int}/disconnect")]
    public async Task<IActionResult> DisconnectAgent(int orgId)
    {
        var agent = await _settings.GetAgentConfigAsync(orgId);
        if (agent is null)
            return NotFound(ApiResponse<object>.Fail("No agent configuration found for this organization."));

        agent.RetellAgentId = null;
        agent.RetellLlmId = null;
        agent.RetellKnowledgeBaseId = null;
        agent.LastSyncedAt = null;
        await _settings.UpsertAgentConfigAsync(agent);

        await _audit.LogAsync(orgId, null, "RetellAgentDisconnected", "platform console");
        _logger.LogInformation("Retell agent disconnected for organization {OrgId}.", orgId);

        return Ok(ApiResponse<object>.Ok(new { }, "Retell agent disconnected."));
    }

    private static string? Mask(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : key.Length <= 4 ? "****" : $"****{key[^4..]}";
}
