using System.Threading.Channels;

namespace AiReceptionist.Api.Services;

/// <summary>In-process queue of organizations whose Retell agent needs re-syncing
/// (SRS §21 background processing — "AI Synchronization / Retell Updates").
/// Controllers enqueue after knowledge base / catalog / settings mutations.</summary>
public interface IRetellSyncQueue
{
    void Enqueue(int orgId);
    ChannelReader<int> Reader { get; }
}

public class RetellSyncQueue : IRetellSyncQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();
    public ChannelReader<int> Reader => _channel.Reader;
    public void Enqueue(int orgId) => _channel.Writer.TryWrite(orgId);
}

public class RetellSyncWorker : BackgroundService
{
    private readonly IRetellSyncQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RetellSyncWorker> _logger;

    public RetellSyncWorker(IRetellSyncQueue queue, IServiceScopeFactory scopes, ILogger<RetellSyncWorker> logger)
    {
        _queue = queue;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Re-sync every connected tenant on startup so agents pick up a changed public
        // base URL (quick-tunnel URLs like ngrok rotate on every restart).
        try
        {
            using var scope = _scopes.CreateScope();
            var retell = scope.ServiceProvider.GetRequiredService<IRetellService>();
            if (await retell.IsConfiguredAsync())
            {
                var settings = scope.ServiceProvider.GetRequiredService<Data.Repositories.ISettingsRepository>();
                foreach (var orgId in await settings.GetConnectedOrganizationIdsAsync())
                {
                    _queue.Enqueue(orgId);
                    // Recover any calls whose webhook was missed while we were down.
                    try { await retell.BackfillCallsAsync(orgId, stoppingToken); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Call backfill failed for org {OrgId}", orgId); }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup Retell re-sync enqueue failed");
        }

        await foreach (var orgId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var retell = scope.ServiceProvider.GetRequiredService<IRetellService>();
                var settings = scope.ServiceProvider.GetRequiredService<Data.Repositories.ISettingsRepository>();

                if (!await retell.IsConfiguredAsync()) continue;

                // Only auto-sync tenants that already connected an agent —
                // never create Retell resources implicitly.
                var agent = await settings.GetAgentConfigAsync(orgId);
                if (string.IsNullOrWhiteSpace(agent?.RetellAgentId)) continue;

                // Always an update, never a fresh build: only the platform console's Connect
                // action may create or replace Retell resources.
                var result = await retell.SyncAgentAsync(orgId, RetellSyncMode.Update, stoppingToken);
                if (!result.Success)
                    _logger.LogWarning("Background Retell sync failed for org {OrgId}: {Error}", orgId, result.Error);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background Retell sync crashed for org {OrgId}", orgId);
            }
        }
    }
}
