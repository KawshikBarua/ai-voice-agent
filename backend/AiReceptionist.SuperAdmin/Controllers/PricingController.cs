using AiReceptionist.SuperAdmin.Data.Repositories;
using AiReceptionist.SuperAdmin.Models;
using AiReceptionist.SuperAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.SuperAdmin.Controllers;

/// <summary>
/// The tier catalogue: what a customer can be put on, at what price, with how many AI minutes
/// included and what each minute past that costs.
///
/// Tiers are read straight from the database for display and written through the tenant API, which
/// holds the Stripe key — creating a tier and creating the Stripe price behind it are one action,
/// and splitting them across two applications would leave the two able to disagree.
/// </summary>
[Route("pricing")]
public class PricingController : PlatformControllerBase
{
    private readonly IBillingRepository _billing;
    private readonly IPlatformApiClient _api;
    private readonly ILogger<PricingController> _logger;

    public PricingController(IBillingRepository billing, IPlatformApiClient api,
        ILogger<PricingController> logger)
    {
        _billing = billing;
        _api = api;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(int? edit)
    {
        var plans = await _billing.ListPlansAsync();
        return View(new PricingViewModel
        {
            Plans = plans,
            Editing = edit is > 0 ? plans.FirstOrDefault(p => p.Id == edit) : null,
            StripeConfigured = await _api.IsStripeConfiguredAsync(),
        });
    }

    [HttpPost("save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(PlanInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            Warn("Give the tier a name.");
            return RedirectToAction(nameof(Index), new { edit = input.Id > 0 ? input.Id : (int?)null });
        }
        if (input.Amount < 0 || input.OverageRatePerMinute < 0)
        {
            Warn("Prices cannot be negative.");
            return RedirectToAction(nameof(Index), new { edit = input.Id > 0 ? input.Id : (int?)null });
        }

        // A metered tier with no rate would record every overrun and charge for none of it —
        // almost always a slip rather than a deliberate free allowance.
        if (input.IncludedMinutes > 0 && input.OverageRatePerMinute == 0)
            Warn("This tier meters minutes but charges nothing for going over them. " +
                 "Set a rate per minute, or set the included minutes to 0 for a flat-fee tier.");

        var (ok, message) = await _api.SavePlanAsync(input);
        if (ok) _logger.LogInformation("Super admin {UserId} saved pricing tier '{Name}'.", CurrentUserId, input.Name);

        Report(ok, message);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:int}/retire")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Retire(int id)
    {
        var (ok, message) = await _api.RetirePlanAsync(id);
        if (ok) _logger.LogInformation("Super admin {UserId} retired pricing tier {PlanId}.", CurrentUserId, id);

        Report(ok, message);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Totals every elapsed period now, so a pricing change can be seen taking effect
    /// without waiting for the sweep.</summary>
    [HttpPost("close-periods")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClosePeriods()
    {
        var (ok, message) = await _api.ClosePeriodsAsync();
        Report(ok, message);
        return RedirectToAction(nameof(Index));
    }
}
