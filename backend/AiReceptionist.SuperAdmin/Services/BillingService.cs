using AiReceptionist.SuperAdmin.Data.Repositories;
using AiReceptionist.SuperAdmin.Models;

namespace AiReceptionist.SuperAdmin.Services;

public interface IBillingService
{
    /// <summary>Records a payment and, unless told otherwise, rolls the billing period forward by
    /// one cycle. An account disabled by the overdue sweep is re-enabled once it is paid up again.</summary>
    Task<(bool ok, string message)> RecordPaymentAsync(PaymentInput input, int? userId);

    /// <summary>Disables every enabled organization whose grace period has lapsed and which is
    /// opted into auto-suspend. Returns how many were disabled.</summary>
    Task<int> SuspendLapsedAsync(CancellationToken ct = default);
}

public class BillingService : IBillingService
{
    private readonly IBillingRepository _billing;
    private readonly IOrganizationRepository _organizations;
    private readonly ILogger<BillingService> _logger;

    public BillingService(IBillingRepository billing, IOrganizationRepository organizations,
        ILogger<BillingService> logger)
    {
        _billing = billing;
        _organizations = organizations;
        _logger = logger;
    }

    public async Task<(bool ok, string message)> RecordPaymentAsync(PaymentInput input, int? userId)
    {
        var subscription = await _billing.GetSubscriptionAsync(input.OrganizationId);
        if (subscription is null)
            return (false, "Set up a subscription for this organization before recording a payment.");

        if (input.Amount <= 0)
            return (false, "Payment amount must be greater than zero.");

        // The payment always covers the period that is closing; only the subscription's own
        // period moves on, so a back-dated payment is still filed against the right dates.
        await _billing.AddPaymentAsync(new Payment
        {
            OrganizationId = input.OrganizationId,
            Amount = input.Amount,
            Currency = subscription.Currency,
            PaidAt = input.PaidAt,
            PeriodStart = subscription.CurrentPeriodStart,
            PeriodEnd = subscription.CurrentPeriodEnd,
            Method = input.Method,
            Reference = input.Reference,
            Notes = input.Notes,
            RecordedByUserId = userId,
        });

        if (!input.AdvancePeriod)
            return (true, $"Payment of {input.Amount:0.##} {subscription.Currency} recorded. Billing period unchanged.");

        var newStart = subscription.CurrentPeriodEnd;
        var newEnd = subscription.NextPeriodEnd();
        await _billing.AdvancePeriodAsync(input.OrganizationId, newStart, newEnd, userId);

        var message = $"Payment recorded. Paid through {newEnd:d MMM yyyy}.";

        // Paying up undoes an automatic suspension; a manual one is left alone deliberately.
        var org = await _organizations.GetAsync(input.OrganizationId);
        if (org is { IsActive: false } && org.SuspendedReason == SuspensionReasons.Overdue)
        {
            await _organizations.SetActiveAsync(input.OrganizationId, active: true, reason: null);
            message += " The account was re-enabled.";
        }

        return (true, message);
    }

    public async Task<int> SuspendLapsedAsync(CancellationToken ct = default)
    {
        var lapsed = await _billing.ListLapsedAutoSuspendAsync(DateTime.UtcNow);
        foreach (var orgId in lapsed)
        {
            ct.ThrowIfCancellationRequested();
            await _organizations.SetActiveAsync(orgId, active: false, reason: SuspensionReasons.Overdue);
            _logger.LogWarning("Disabled organization {OrgId}: subscription past its grace period.", orgId);
        }
        return lapsed.Count;
    }
}

/// <summary>Runs the overdue sweep on a timer so an unpaid account stops working without anyone
/// having to open the console.</summary>
public class AutoSuspendWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<AutoSuspendWorker> _logger;

    public AutoSuspendWorker(IServiceProvider services, IConfiguration config, ILogger<AutoSuspendWorker> logger)
    {
        _services = services;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("Platform:AutoSuspendSweepEnabled", true))
        {
            _logger.LogInformation("Automatic suspension sweep is disabled.");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _config.GetValue("Platform:AutoSuspendSweepHours", 6)));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var billing = scope.ServiceProvider.GetRequiredService<IBillingService>();
                var count = await billing.SuspendLapsedAsync(stoppingToken);
                if (count > 0)
                    _logger.LogInformation("Overdue sweep disabled {Count} organization(s).", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A database blip must not kill the worker for the lifetime of the process.
                _logger.LogError(ex, "Overdue sweep failed; retrying at the next interval.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
