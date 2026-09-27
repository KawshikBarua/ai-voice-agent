using AiReceptionist.SuperAdmin.Data.Repositories;
using AiReceptionist.SuperAdmin.Models;

namespace AiReceptionist.SuperAdmin.Services;

public interface IBillingService
{
    /// <summary>Records a payment and, unless told otherwise, rolls the billing period forward by
    /// one cycle. An account an earlier version disabled for being overdue is re-enabled once it is
    /// paid up again.</summary>
    Task<(bool ok, string message)> RecordPaymentAsync(PaymentInput input, int? userId);
}

public class BillingService : IBillingService
{
    private readonly IBillingRepository _billing;
    private readonly IOrganizationRepository _organizations;

    public BillingService(IBillingRepository billing, IOrganizationRepository organizations)
    {
        _billing = billing;
        _organizations = organizations;
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
}
