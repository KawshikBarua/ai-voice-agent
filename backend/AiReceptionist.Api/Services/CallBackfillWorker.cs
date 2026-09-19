namespace AiReceptionist.Api.Services;

/// <summary>
/// When the call import last ran and when it runs next, shared between the worker that does it and
/// the dashboard that counts down to it.
///
/// A singleton holding four fields rather than anything persisted: the schedule belongs to this
/// process and dies with it, and a restart re-establishes it within a second by running a pass.
/// Writes are single-threaded (one worker) and reads are racy by nature, so the fields are plain
/// volatile-enough properties rather than a lock nobody would contend for.
/// </summary>
public sealed class CallSyncStatus
{
    /// <summary>The gap between passes, as configured.</summary>
    public TimeSpan Interval { get; set; }

    /// <summary>When a pass last finished — by the worker, or by somebody pressing Sync.</summary>
    public DateTime? LastRunAt { get; set; }

    /// <summary>When the worker's next pass is due. Null until the first one has run.</summary>
    public DateTime? NextRunAt { get; set; }

    /// <summary>How many calls the last pass recovered. Usually zero, which is the healthy case:
    /// it means the webhook delivered them first.</summary>
    public int LastImported { get; set; }

    /// <summary>Seconds until the next pass, measured on the server so a browser with a wrong
    /// clock still counts down correctly. Zero once it is due.</summary>
    public int SecondsUntilNext => NextRunAt is { } next
        ? (int)Math.Max(0, Math.Ceiling((next - DateTime.UtcNow).TotalSeconds))
        : 0;
}

/// <summary>
/// Pulls finished calls from Retell on a timer, for every tenant with a connected agent.
///
/// Calls are supposed to arrive by webhook the moment they end. In practice they do not always:
/// a deployment behind a tunnel whose URL has rotated, a restart during a call, a delivery Retell
/// gave up on. Until the row exists the call is invisible and — because minutes are summed from
/// <c>CallLogs</c> — so are its minutes, which is what made the dashboard look stuck until
/// somebody pressed Sync by hand.
///
/// So this runs the same import that button runs, every few minutes, for everyone. The webhook is
/// still the fast path and this is the net underneath it: <see cref="IRetellService.BackfillCallsAsync"/>
/// skips any call already stored, so a pass after a quiet five minutes writes nothing at all.
/// </summary>
public class CallBackfillWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly CallSyncStatus _status;
    private readonly ILogger<CallBackfillWorker> _logger;
    private readonly TimeSpan _interval;

    public CallBackfillWorker(IServiceScopeFactory scopes, CallSyncStatus status,
        IConfiguration config, ILogger<CallBackfillWorker> logger)
    {
        _scopes = scopes;
        _status = status;
        _logger = logger;
        // Configurable mostly so it can be turned down in a busy deployment or up while
        // debugging; five minutes is short enough that nobody reaches for the button.
        _interval = TimeSpan.FromMinutes(Math.Max(1, config.GetValue("Retell:CallSyncMinutes", 5)));
        _status.Interval = _interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);

        // The first pass runs immediately rather than after the first interval: a restart is
        // exactly when a webhook is most likely to have been missed.
        do
        {
            try
            {
                await RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let one bad pass end the timer — the next one may well work.
                _logger.LogError(ex, "Call backfill pass failed");
            }
            finally
            {
                // Recorded even for a pass that threw: the timer is still running, so the next
                // one really is due then, and a dashboard counting down to it is telling the
                // truth about when this will next be attempted.
                _status.LastRunAt = DateTime.UtcNow;
                _status.NextRunAt = DateTime.UtcNow + _interval;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunPassAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var retell = scope.ServiceProvider.GetRequiredService<IRetellService>();
        if (!await retell.IsConfiguredAsync()) return;

        var settings = scope.ServiceProvider.GetRequiredService<Data.Repositories.ISettingsRepository>();
        var imported = 0;

        foreach (var orgId in await settings.GetConnectedOrganizationIdsAsync())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                imported += await retell.BackfillCallsAsync(orgId, ct);
            }
            catch (Exception ex)
            {
                // One tenant's failure must not cost the rest their sweep.
                _logger.LogWarning(ex, "Call backfill failed for org {OrgId}", orgId);
            }
        }

        _status.LastImported = imported;

        // Only worth a line when it actually recovered something; a silent pass is the normal case.
        if (imported > 0)
            _logger.LogInformation("Call backfill recovered {Count} call(s) the webhook did not deliver", imported);
    }
}
