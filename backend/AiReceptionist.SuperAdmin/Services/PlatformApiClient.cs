using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiReceptionist.SuperAdmin.Models;

namespace AiReceptionist.SuperAdmin.Services;

/// <summary>Masked view of the platform-wide Retell credential. The key itself is never returned
/// by the API — only whether one is set and its last four characters.</summary>
public class RetellConnectionStatus
{
    public bool ApiKeyConfigured { get; set; }
    public string? ApiKeyPreview { get; set; }
    public string? ApiBaseUrl { get; set; }
    public string? WebhookBaseUrl { get; set; }
    public string? DefaultVoiceId { get; set; }
    public bool VerifySignature { get; set; }
    public DateTime? ModifiedAt { get; set; }
    /// <summary>"database" or "configuration" — a key resolving from configuration cannot be
    /// rotated from this screen, which is worth saying out loud.</summary>
    public string? KeySource { get; set; }

    /// <summary>Retell rejects localhost callbacks, so a missing or local webhook base URL means
    /// syncs will fail or the agent will never call back.</summary>
    public bool WebhookUsable =>
        !string.IsNullOrWhiteSpace(WebhookBaseUrl) &&
        !WebhookBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) &&
        !WebhookBaseUrl.Contains("127.0.0.1", StringComparison.Ordinal);
}

/// <summary>Result of setting a tenant's numbers. <see cref="Synced"/> is false when the values
/// were stored but Retell would not accept them yet.</summary>
public class AgentNumbersOutcome
{
    public string? TransferNumber { get; set; }
    public string? RetellPhoneNumber { get; set; }
    public bool Synced { get; set; }
    public string? PhoneWarning { get; set; }
}

/// <summary>The platform-wide prompt wording: what is in force, plus the built-in defaults so the
/// editor can show what a section would go back to.</summary>
public class PromptTemplateStatus
{
    public string Persona { get; set; } = "";
    public string CoreRules { get; set; } = "";
    public string ConversationGuide { get; set; } = "";
    public string FieldServiceGuide { get; set; } = "";
    public string ToolPolicy { get; set; } = "";
    public DateTime? ModifiedAt { get; set; }

    /// <summary>Human-readable names of the sections that differ from the platform default.</summary>
    public List<string> CustomisedSections { get; set; } = [];

    public PromptTemplateInput Defaults { get; set; } = new();

    public bool IsCustomised => CustomisedSections.Count > 0;
}

/// <summary>The prompt as one tenant's agent will receive it, platform wording and tenant facts
/// combined.</summary>
public class PromptPreview
{
    public int OrganizationId { get; set; }
    public string OrganizationName { get; set; } = "";
    public string Prompt { get; set; } = "";
}

public class RetellSyncOutcome
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? AgentId { get; set; }
    public string? LlmId { get; set; }
    public string? PhoneNumber { get; set; }
    public string? PhoneWarning { get; set; }
}

public interface IPlatformApiClient
{
    Task<RetellConnectionStatus?> GetRetellConnectionAsync(CancellationToken ct = default);
    Task<(bool ok, string message)> UpdateRetellConnectionAsync(RetellConnectionInput input, CancellationToken ct = default);
    Task<RetellSyncOutcome> SyncAgentAsync(int orgId, CancellationToken ct = default);
    Task<(bool ok, string message)> DisconnectAgentAsync(int orgId, CancellationToken ct = default);

    /// <summary>Sets the tenant's transfer number and Retell phone number, re-syncing the agent so
    /// they actually take effect on Retell. <c>synced</c> is false when the numbers were stored but
    /// Retell rejected the push — a partial success the operator has to act on, so the two are
    /// reported separately rather than collapsed into one flag.</summary>
    Task<(bool ok, bool synced, string message)> UpdateNumbersAsync(AgentNumbersInput input, CancellationToken ct = default);

    /// <summary>The prompt wording every tenant's agent is built from.</summary>
    Task<PromptTemplateStatus?> GetPromptTemplateAsync(CancellationToken ct = default);

    /// <summary>Saves the wording, or (with <paramref name="resetToDefaults"/>) throws the overrides
    /// away. Storing only: no agent on Retell changes until it is next synced, and the returned
    /// message says how many are still running the old wording.</summary>
    Task<(bool ok, string message)> UpdatePromptTemplateAsync(PromptTemplateInput input,
        bool resetToDefaults = false, CancellationToken ct = default);

    Task<PromptPreview?> PreviewPromptAsync(int orgId, CancellationToken ct = default);

    // ---------- billing ----------
    // Stripe lives in the tenant API (it holds the secret key and serves the webhook), so every
    // action that touches Stripe goes through it rather than being reimplemented here.

    /// <summary>Whether Stripe is connected at all, so this console can grey out what it cannot do
    /// instead of offering buttons that fail.</summary>
    Task<bool> IsStripeConfiguredAsync(CancellationToken ct = default);

    Task<(bool ok, string message)> SavePlanAsync(PlanInput input, CancellationToken ct = default);
    Task<(bool ok, string message)> RetirePlanAsync(int planId, CancellationToken ct = default);
    Task<(bool ok, string message)> AssignPlanAsync(AssignPlanInput input, CancellationToken ct = default);

    /// <summary>Subscribes the organization in Stripe against the card already on file.</summary>
    Task<(bool ok, string message)> SubscribeAsync(int orgId, CancellationToken ct = default);
    Task<(bool ok, string message)> CancelSubscriptionAsync(int orgId, CancellationToken ct = default);

    /// <summary>Raises and emails a one-off Stripe invoice, sweeping in any carried-over overage.</summary>
    Task<(bool ok, string message)> SendInvoiceAsync(int orgId, int daysUntilDue, bool includePlanCharge,
        CancellationToken ct = default);

    /// <summary>Stops (or restores) the organization's agent without touching sign-in.</summary>
    Task<(bool ok, string message)> SetAgentRestrictionAsync(int orgId, bool restricted, string? reason,
        CancellationToken ct = default);

    /// <summary>Totals every elapsed billing period now instead of waiting for the sweep.</summary>
    Task<(bool ok, string message)> ClosePeriodsAsync(CancellationToken ct = default);
}

/// <summary>
/// Calls the tenant API's platform endpoints, authenticated with the shared Platform:AdminKey.
///
/// Retell agent creation depends on the generated final prompt, the tool definitions and the
/// webhook URLs — all of which the tenant API already owns and re-syncs in the background when a
/// tenant edits their knowledge base. Re-implementing that here would leave two prompt builders
/// to keep identical, so this app drives the API instead of talking to Retell directly.
/// </summary>
public class PlatformApiClient : IPlatformApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<PlatformApiClient> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public PlatformApiClient(HttpClient http, ILogger<PlatformApiClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<RetellConnectionStatus?> GetRetellConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("api/v1/platform/retell/connection", ct);
            var envelope = await ReadEnvelopeAsync<RetellConnectionStatus>(response, ct);
            return envelope.Data;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the Retell connection from the tenant API.");
            return null;
        }
    }

    public async Task<(bool ok, string message)> UpdateRetellConnectionAsync(
        RetellConnectionInput input, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PutAsync("api/v1/platform/retell/connection", Json(input), ct);
            var envelope = await ReadEnvelopeAsync<RetellConnectionStatus>(response, ct);
            return (envelope.Success, envelope.Message ?? (envelope.Success ? "Saved." : "Could not save."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save the Retell connection.");
            return (false, Unreachable(ex));
        }
    }

    public async Task<RetellSyncOutcome> SyncAgentAsync(int orgId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync($"api/v1/platform/retell/{orgId}/sync", Empty(), ct);
            var envelope = await ReadEnvelopeAsync<RetellSyncOutcome>(response, ct);
            var outcome = envelope.Data ?? new RetellSyncOutcome();
            outcome.Success = envelope.Success;
            outcome.Message = envelope.Message;
            return outcome;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retell sync failed for organization {OrgId}.", orgId);
            return new RetellSyncOutcome { Success = false, Message = Unreachable(ex) };
        }
    }

    public async Task<(bool ok, bool synced, string message)> UpdateNumbersAsync(
        AgentNumbersInput input, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PutAsync($"api/v1/platform/retell/{input.OrganizationId}/numbers",
                Json(new { input.TransferNumber, input.RetellPhoneNumber }), ct);
            var envelope = await ReadEnvelopeAsync<AgentNumbersOutcome>(response, ct);
            var synced = envelope.Success && envelope.Data?.Synced == true;
            return (envelope.Success, synced,
                envelope.Message ?? (envelope.Success ? "Saved." : "Could not save."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save the phone numbers for organization {OrgId}.", input.OrganizationId);
            return (false, false, Unreachable(ex));
        }
    }

    public async Task<PromptTemplateStatus?> GetPromptTemplateAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("api/v1/platform/prompt-template", ct);
            var envelope = await ReadEnvelopeAsync<PromptTemplateStatus>(response, ct);
            return envelope.Data;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the prompt template from the tenant API.");
            return null;
        }
    }

    public async Task<(bool ok, string message)> UpdatePromptTemplateAsync(PromptTemplateInput input,
        bool resetToDefaults = false, CancellationToken ct = default)
    {
        try
        {
            var body = new
            {
                input.Persona,
                input.CoreRules,
                input.ConversationGuide,
                input.FieldServiceGuide,
                input.ToolPolicy,
                ResetToDefaults = resetToDefaults,
            };
            var response = await _http.PutAsync("api/v1/platform/prompt-template", Json(body), ct);
            var envelope = await ReadEnvelopeAsync<object>(response, ct);
            return (envelope.Success, envelope.Message ?? (envelope.Success ? "Saved." : "Could not save."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save the prompt template.");
            return (false, Unreachable(ex));
        }
    }

    public async Task<PromptPreview?> PreviewPromptAsync(int orgId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"api/v1/platform/prompt-template/preview/{orgId}", ct);
            var envelope = await ReadEnvelopeAsync<PromptPreview>(response, ct);
            return envelope.Data;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not build the prompt preview for organization {OrgId}.", orgId);
            return null;
        }
    }

    public async Task<(bool ok, string message)> DisconnectAgentAsync(int orgId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync($"api/v1/platform/retell/{orgId}/disconnect", Empty(), ct);
            var envelope = await ReadEnvelopeAsync<object>(response, ct);
            return (envelope.Success, envelope.Message ?? (envelope.Success ? "Disconnected." : "Could not disconnect."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retell disconnect failed for organization {OrgId}.", orgId);
            return (false, Unreachable(ex));
        }
    }

    // ---------- billing ----------

    private sealed class StripeStatusPayload
    {
        public bool StripeConfigured { get; set; }
    }

    public async Task<bool> IsStripeConfiguredAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("api/v1/platform/billing/status", ct);
            var envelope = await ReadEnvelopeAsync<StripeStatusPayload>(response, ct);
            return envelope.Data?.StripeConfigured ?? false;
        }
        catch (Exception ex)
        {
            // Unreachable is treated as "not connected": the screens then offer manual billing,
            // which always works, rather than Stripe buttons that cannot succeed.
            _logger.LogError(ex, "Could not read the Stripe status from the tenant API.");
            return false;
        }
    }

    public Task<(bool ok, string message)> SavePlanAsync(PlanInput input, CancellationToken ct = default) =>
        PostAsync("api/v1/platform/billing/plans", input, "Tier saved.", "Could not save the tier.", ct);

    public async Task<(bool ok, string message)> RetirePlanAsync(int planId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.DeleteAsync($"api/v1/platform/billing/plans/{planId}", ct);
            var envelope = await ReadEnvelopeAsync<object>(response, ct);
            return (envelope.Success, envelope.Message ?? (envelope.Success ? "Tier retired." : "Could not retire the tier."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not retire tier {PlanId}.", planId);
            return (false, Unreachable(ex));
        }
    }

    public Task<(bool ok, string message)> AssignPlanAsync(AssignPlanInput input, CancellationToken ct = default) =>
        PostAsync($"api/v1/platform/billing/{input.OrganizationId}/plan",
            new { input.PlanId, input.IncludedMinutes, input.OverageRatePerMinute, input.Amount },
            "Plan assigned.", "Could not assign the plan.", ct);

    public Task<(bool ok, string message)> SubscribeAsync(int orgId, CancellationToken ct = default) =>
        PostAsync($"api/v1/platform/billing/{orgId}/subscribe", new { },
            "Subscribed in Stripe.", "Could not subscribe this organization.", ct);

    public Task<(bool ok, string message)> CancelSubscriptionAsync(int orgId, CancellationToken ct = default) =>
        PostAsync($"api/v1/platform/billing/{orgId}/cancel", new { },
            "Subscription cancelled.", "Could not cancel the subscription.", ct);

    public Task<(bool ok, string message)> SendInvoiceAsync(int orgId, int daysUntilDue,
        bool includePlanCharge, CancellationToken ct = default) =>
        PostAsync($"api/v1/platform/billing/{orgId}/invoice",
            new { DaysUntilDue = daysUntilDue, IncludePlanCharge = includePlanCharge },
            "Invoice sent.", "Could not send the invoice.", ct);

    public Task<(bool ok, string message)> SetAgentRestrictionAsync(int orgId, bool restricted,
        string? reason, CancellationToken ct = default) =>
        PostAsync($"api/v1/platform/billing/{orgId}/agent-restriction",
            new { Restricted = restricted, Reason = reason },
            restricted ? "Agent restricted." : "Agent released.", "Could not change the restriction.", ct);

    public Task<(bool ok, string message)> ClosePeriodsAsync(CancellationToken ct = default) =>
        PostAsync("api/v1/platform/billing/close-periods", new { },
            "Billing periods closed.", "Could not close the billing periods.", ct);

    // ---------- plumbing ----------

    /// <summary>The shape every billing action shares: post a body, read the envelope, fall back to
    /// a sensible sentence when the API did not supply one.</summary>
    private async Task<(bool ok, string message)> PostAsync<T>(string path, T body,
        string okFallback, string failFallback, CancellationToken ct)
    {
        try
        {
            var response = await _http.PostAsync(path, Json(body), ct);
            var envelope = await ReadEnvelopeAsync<object>(response, ct);
            return (envelope.Success, envelope.Message ?? (envelope.Success ? okFallback : failFallback));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Billing call to {Path} failed.", path);
            return (false, Unreachable(ex));
        }
    }

    private sealed class Envelope<T>
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public T? Data { get; set; }
        public List<string>? Errors { get; set; }
    }

    private static StringContent Json<T>(T body) =>
        new(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");

    private static StringContent Empty() => new("{}", Encoding.UTF8, "application/json");

    private async Task<Envelope<T>> ReadEnvelopeAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);

        // A 401 here means the two apps disagree about Platform:AdminKey — a configuration
        // mistake, not a Retell problem, so say which one it is.
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return new Envelope<T>
            {
                Success = false,
                Message = "The tenant API rejected the platform key. Set the same Platform:AdminKey in both apps.",
            };

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound && !body.TrimStart().StartsWith('{'))
            return new Envelope<T>
            {
                Success = false,
                Message = "The tenant API has no platform endpoints. Update and restart AiReceptionist.Api.",
            };

        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope<T>>(body, JsonOpts);
            if (envelope is not null) return envelope;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Unreadable response from the tenant API: {Body}", Truncate(body));
        }

        return new Envelope<T>
        {
            Success = false,
            Message = $"Unexpected response from the tenant API ({(int)response.StatusCode}).",
        };
    }

    private static string Unreachable(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException
            ? "Could not reach the tenant API. Make sure AiReceptionist.Api is running."
            : $"Request failed: {ex.Message}";

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500] + "…";
}
