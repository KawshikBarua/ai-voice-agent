using AiReceptionist.SuperAdmin.Data.Repositories;
using AiReceptionist.SuperAdmin.Models;
using AiReceptionist.SuperAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.SuperAdmin.Controllers;

/// <summary>
/// The words every tenant's AI receptionist is built from — how it talks, the rules it cannot
/// break, the shape of a call and when it is allowed to reach for a tool.
///
/// It lives here rather than in the tenant app because it is one text for the whole platform.
/// Tenants supply their own facts (services, hours, knowledge base); they do not get to rewrite
/// the receptionist.
///
/// Saving stores the wording and stops there. Rolling it out to a live agent is a separate,
/// deliberate act on the Retell AI screen — one platform-wide button that rewrote every tenant's
/// agent would put a bad edit in front of every caller before anyone had read it back.
/// </summary>
[Route("prompt")]
public class PromptController : PlatformControllerBase
{
    private readonly IPlatformApiClient _api;
    private readonly IOrganizationRepository _organizations;
    private readonly ILogger<PromptController> _logger;

    public PromptController(IPlatformApiClient api, IOrganizationRepository organizations,
        ILogger<PromptController> logger)
    {
        _api = api;
        _organizations = organizations;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(int? preview)
    {
        var organizations = await _organizations.ListAsync();
        ViewBag.Organizations = organizations;

        // Prompt edits are hard to judge in the abstract, so the screen shows the finished article
        // for a real tenant — industry, hours and closures already filled in.
        if (preview is { } orgId)
            ViewBag.Preview = await _api.PreviewPromptAsync(orgId);

        return View(await _api.GetPromptTemplateAsync());
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(PromptTemplateInput input)
    {
        var (ok, message) = await _api.UpdatePromptTemplateAsync(input);
        if (ok) _logger.LogInformation("Super admin {UserId} rewrote the platform prompt.", CurrentUserId);
        Report(ok, message);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Throws away every override so the platform text goes back to what ships with the
    /// product — and keeps tracking it as the product's own wording improves. Like a save, this
    /// only changes what the next sync will build; live agents are untouched.</summary>
    [HttpPost("reset")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reset()
    {
        var (ok, message) = await _api.UpdatePromptTemplateAsync(new PromptTemplateInput(), resetToDefaults: true);
        if (ok) _logger.LogInformation("Super admin {UserId} reset the platform prompt to defaults.", CurrentUserId);
        Report(ok, message);
        return RedirectToAction(nameof(Index));
    }
}
