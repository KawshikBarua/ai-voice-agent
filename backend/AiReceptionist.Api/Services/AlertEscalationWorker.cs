using AiReceptionist.Api.Data.Repositories;

namespace AiReceptionist.Api.Services;

/// <summary>
/// Chases urgent alerts nobody has acknowledged.
///
/// A single notification is not a safety net. It can land while the phone is face down, be swiped
/// away with a pile of others, or arrive during a haircut — and an emergency booking that nobody
/// reads is exactly the case this whole feature exists for. So an urgent alert is re-sent every
/// few minutes until a human opens the dashboard and acknowledges it.
///
/// It stops on two conditions: somebody acknowledges (the point), or the attempt cap is reached
/// (so a business closed for the night is not buzzed until morning about something that will not
/// be actioned until then — it is still waiting on the dashboard in the morning either way).
/// </summary>
public class AlertEscalationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly PushOptions _options;
    private readonly ILogger<AlertEscalationWorker> _logger;

    public AlertEscalationWorker(IServiceScopeFactory scopes, PushOptions options,
        ILogger<AlertEscalationWorker> logger)
    {
        _scopes = scopes;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Configured)
        {
            _logger.LogInformation(
                "Alert escalation is idle: no VAPID keys are configured, so nothing can be pushed. " +
                "Urgent alerts still queue on the dashboard.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.EscalateAfterMinutes));

        // A minute is fine as a tick: the query is indexed on exactly its predicate and matches
        // nothing at all in the ordinary case, which is most minutes of most days.
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<INotificationRepository>();
                var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();

                var due = await repository.ListDueForEscalationAsync(interval, _options.MaxEscalations);
                foreach (var alert in due)
                {
                    _logger.LogInformation(
                        "Re-sending unacknowledged urgent alert {AlertId} for organization {OrgId} " +
                        "(attempt {Attempt} of {Max}).",
                        alert.Id, alert.OrganizationId, alert.NotifiedCount + 1, _options.MaxEscalations);
                    await notifications.DeliverAsync(alert, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed sweep must not take the worker down — the next tick tries again.
                _logger.LogError(ex, "Alert escalation sweep failed.");
            }
        }
    }
}
