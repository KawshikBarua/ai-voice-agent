using AiReceptionist.SuperAdmin.Data.Repositories;
using AiReceptionist.SuperAdmin.Models;
using AiReceptionist.SuperAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.SuperAdmin.Controllers;

/// <summary>Subscriptions and payments. Payment is recorded by hand, so "paid" means the current
/// billing period (plus its grace days) has not run out.</summary>
[Route("billing")]
public class BillingController : PlatformControllerBase
{
    private readonly IOrganizationRepository _organizations;
    private readonly IBillingRepository _billing;
    private readonly IBillingService _service;
    private readonly ILogger<BillingController> _logger;

    public BillingController(IOrganizationRepository organizations, IBillingRepository billing,
        IBillingService service, ILogger<BillingController> logger)
    {
        _organizations = organizations;
        _billing = billing;
        _service = service;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var rows = await _organizations.ListAsync();
        ViewBag.Collected30d = await _billing.CollectedSinceAsync(DateTime.UtcNow.AddDays(-30));
        // Worst standing first: overdue, then due soon, then unbilled, then paid.
        return View(rows.OrderByDescending(o => o.BillingState switch
        {
            BillingState.Overdue => 3,
            BillingState.DueSoon => 2,
            BillingState.NotConfigured => 1,
            _ => 0,
        }).ThenBy(o => o.Name).ToList());
    }

    [HttpPost("subscription")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSubscription(SubscriptionInput input)
    {
        var organization = await _organizations.GetAsync(input.OrganizationId);
        if (organization is null) return NotFound();

        if (input.CurrentPeriodEnd <= input.CurrentPeriodStart)
        {
            Warn("The period end must fall after the period start.");
            return RedirectToAction("Details", "Organizations", new { id = input.OrganizationId });
        }
        if (input.Amount < 0)
        {
            Warn("The subscription amount cannot be negative.");
            return RedirectToAction("Details", "Organizations", new { id = input.OrganizationId });
        }

        var cycle = BillingCycles.All.Contains(input.BillingCycle, StringComparer.OrdinalIgnoreCase)
            ? input.BillingCycle
            : BillingCycles.Monthly;

        await _billing.UpsertSubscriptionAsync(new Subscription
        {
            OrganizationId = input.OrganizationId,
            PlanName = string.IsNullOrWhiteSpace(input.PlanName) ? "Standard" : input.PlanName.Trim(),
            BillingCycle = cycle,
            Amount = input.Amount,
            Currency = string.IsNullOrWhiteSpace(input.Currency) ? organization.Currency : input.Currency.Trim(),
            CurrentPeriodStart = input.CurrentPeriodStart,
            CurrentPeriodEnd = input.CurrentPeriodEnd,
            GraceDays = Math.Clamp(input.GraceDays, 0, 90),
            AutoSuspend = input.AutoSuspend,
            IncludedMinutes = Math.Max(0, input.IncludedMinutes),
            Notes = input.Notes,
        }, CurrentUserId);

        _logger.LogInformation("Super admin {UserId} saved the subscription for organization {OrgId}.",
            CurrentUserId, input.OrganizationId);
        Notify($"Subscription saved for {organization.Name}.");
        return RedirectToAction("Details", "Organizations", new { id = input.OrganizationId });
    }

    [HttpPost("payment")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RecordPayment(PaymentInput input)
    {
        var organization = await _organizations.GetAsync(input.OrganizationId);
        if (organization is null) return NotFound();

        if (!PaymentMethods.All.Contains(input.Method, StringComparer.OrdinalIgnoreCase))
            input.Method = "Other";

        var (ok, message) = await _service.RecordPaymentAsync(input, CurrentUserId);
        if (ok)
            _logger.LogInformation("Super admin {UserId} recorded a payment of {Amount} for organization {OrgId}.",
                CurrentUserId, input.Amount, input.OrganizationId);

        Report(ok, ok ? $"{organization.Name}: {message}" : message);
        return RedirectToAction("Details", "Organizations", new { id = input.OrganizationId });
    }

    /// <summary>Runs the overdue sweep now instead of waiting for the timer.</summary>
    [HttpPost("sweep")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sweep()
    {
        var count = await _service.SuspendLapsedAsync();
        Notify(count == 0
            ? "No organizations are past their grace period."
            : $"Disabled {count} organization(s) past their grace period.");
        return RedirectToAction(nameof(Index));
    }
}
