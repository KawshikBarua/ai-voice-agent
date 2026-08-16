using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Domain;

namespace AiReceptionist.Api.Services;

/// <summary>Polls for transferred calls and mines their transcripts for caller intent
/// (SRS §15/§19). Each transcript is turned into a suggested Book/Cancel/Reschedule
/// action that staff confirm from the Calls screen — the AI never books off a transfer.
/// No-ops when no Anthropic API key is configured.</summary>
public class CallIntentWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CallIntentWorker> _logger;

    public CallIntentWorker(IServiceScopeFactory scopes, ILogger<CallIntentWorker> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Call intent worker batch failed");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var intent = scope.ServiceProvider.GetRequiredService<ICallIntentService>();
        if (!intent.IsConfigured) return; // no API key → feature disabled

        var suggestions = scope.ServiceProvider.GetRequiredService<ICallActionSuggestionRepository>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalogRepository>();

        var calls = (await suggestions.GetTransferredCallsNeedingExtractionAsync(10)).ToList();
        if (calls.Count == 0) return;

        foreach (var call in calls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var org = await settings.GetOrganizationAsync(call.OrganizationId);
                if (org is null)
                {
                    await suggestions.MarkCallProcessedAsync(call.OrganizationId, call.Id);
                    continue;
                }

                var services = (await catalog.ListServicesAsync(call.OrganizationId, 1, 100)).Items.ToList();
                var result = await intent.ExtractAsync(call, org, services, ct);
                if (result is null) continue; // transient failure — retry next cycle

                if (!string.Equals(result.Action, "None", StringComparison.OrdinalIgnoreCase))
                {
                    await suggestions.CreateAsync(new CallActionSuggestion
                    {
                        OrganizationId = call.OrganizationId,
                        CallLogId = call.Id,
                        Action = result.Action,
                        Confidence = result.Confidence,
                        CustomerName = result.CustomerName,
                        Phone = result.Phone,
                        ServiceName = result.ServiceName,
                        StartAtLocal = result.StartAtLocal,
                        Reasoning = result.Reasoning,
                    });
                    _logger.LogInformation("Captured {Action} suggestion from transferred call {CallId} (org {OrgId})",
                        result.Action, call.Id, call.OrganizationId);
                }

                await suggestions.MarkCallProcessedAsync(call.OrganizationId, call.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Intent extraction failed for call {CallId}", call.Id);
            }
        }
    }
}
