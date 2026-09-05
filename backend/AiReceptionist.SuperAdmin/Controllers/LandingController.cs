using System.Text.RegularExpressions;
using AiReceptionist.SuperAdmin.Data.Repositories;
using AiReceptionist.SuperAdmin.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.SuperAdmin.Controllers;

/// <summary>
/// The public marketing site's content: the hero wording, the look of the 3D sculpture on the
/// landing page, its lighting, and the heading above the demo request form.
///
/// The site itself is a separate React application that owns none of this — it reads the values
/// from <see cref="Content"/> and re-reads them periodically, so a save here reaches a visitor's
/// browser without anyone redeploying or restarting anything.
///
/// Everything on this screen is public-facing copy. There is no staging step: what is saved is
/// what the next visitor sees.
/// </summary>
[Route("landing")]
public partial class LandingController : PlatformControllerBase
{
    /// <summary>Name of the CORS policy that lets the landing site's origin read
    /// <see cref="Content"/>. Configured in Program.cs from Landing:AllowedOrigins.</summary>
    public const string CorsPolicy = "LandingSite";

    private readonly ILandingContentRepository _content;
    private readonly ILogger<LandingController> _logger;

    public LandingController(ILandingContentRepository content, ILogger<LandingController> logger)
    {
        _content = content;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var row = await _content.GetAsync();
        if (row is null)
        {
            Warn("The landing content table has not been created yet. Restart this app so the " +
                 "schema initializer can run.");
            return View(LandingContentInput.Defaults());
        }

        return View(ToInput(row));
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(LandingContentInput input)
    {
        await _content.SaveAsync(Normalize(input), CurrentUserId);
        _logger.LogInformation("Super admin {UserId} updated the landing page content.", CurrentUserId);
        Notify("Landing page updated. Open tabs pick the change up within a few seconds.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Puts every field back to the wording and settings the product ships with.</summary>
    [HttpPost("reset")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reset()
    {
        await _content.SaveAsync(LandingContentInput.Defaults(), CurrentUserId);
        _logger.LogInformation("Super admin {UserId} reset the landing page content.", CurrentUserId);
        Notify("Landing page restored to the shipped defaults.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// What the public site reads. Anonymous by design — this is the marketing page's own copy,
    /// already visible to anyone who loads the site, and the browser fetching it has no session
    /// here. It exposes nothing about tenants, billing or operators.
    /// </summary>
    [AllowAnonymous]
    [EnableCors(CorsPolicy)]
    [HttpGet("/api/landing-content")]
    [Produces("application/json")]
    public async Task<IActionResult> Content()
    {
        var row = await _content.GetAsync();

        // A missing row means the schema has not initialized. Saying so with a 503 lets the site
        // keep its built-in defaults rather than rendering whatever a half-built row contained.
        if (row is null) return StatusCode(StatusCodes.Status503ServiceUnavailable);

        // The site polls this; a cached response would defeat the point of polling.
        Response.Headers.CacheControl = "no-store";
        return Json(LandingContentResponse.From(row));
    }

    // ---------- helpers ----------

    private static LandingContentInput ToInput(LandingContent row) => new()
    {
        HeroEyebrow = row.HeroEyebrow,
        HeroHeadline = row.HeroHeadline,
        HeroSubheadline = row.HeroSubheadline,
        HeroCtaText = row.HeroCtaText,
        HeroCtaSecondaryText = row.HeroCtaSecondaryText,
        ModelScale = row.ModelScale,
        AutoRotateSpeed = row.AutoRotateSpeed,
        Roughness = row.Roughness,
        Metalness = row.Metalness,
        Accent = row.Accent,
        AmbientTint = row.AmbientTint,
        AmbientIntensity = row.AmbientIntensity,
        DirectionalTint = row.DirectionalTint,
        DirectionalIntensity = row.DirectionalIntensity,
        ContactTitle = row.ContactTitle,
        ContactSubtitle = row.ContactSubtitle,
    };

    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexColour();

    /// <summary>
    /// Brings a submitted form back inside the range the site can render.
    ///
    /// The sliders and colour pickers constrain values in the browser, but a form post is just an
    /// HTTP request — a hand-crafted one could store a negative scale or a colour string that
    /// three.js cannot parse, and the failure would land on the public site rather than here.
    /// Clamping is quiet on purpose: these are not values an operator types, so there is nothing
    /// useful to report back about them.
    /// </summary>
    private static LandingContentInput Normalize(LandingContentInput input)
    {
        static double Clamp(double value, double min, double max) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : min;

        static string Colour(string? value, string fallback) =>
            !string.IsNullOrWhiteSpace(value) && HexColour().IsMatch(value.Trim())
                ? value.Trim().ToLowerInvariant()
                : fallback;

        // Blank text falls back to the shipped wording rather than being stored empty: an empty
        // headline is a broken page, and the column is NOT NULL anyway.
        var defaults = LandingContentInput.Defaults();
        static string Text(string? value, string fallback, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            var trimmed = value.Trim();
            // Normalize CRLF so the site's newline-driven line breaks behave the same whichever
            // browser posted the form.
            trimmed = trimmed.Replace("\r\n", "\n").Replace('\r', '\n');
            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
        }

        return new LandingContentInput
        {
            HeroEyebrow = Text(input.HeroEyebrow, defaults.HeroEyebrow, 80),
            HeroHeadline = Text(input.HeroHeadline, defaults.HeroHeadline, 200),
            HeroSubheadline = Text(input.HeroSubheadline, defaults.HeroSubheadline, 500),
            HeroCtaText = Text(input.HeroCtaText, defaults.HeroCtaText, 60),
            HeroCtaSecondaryText = Text(input.HeroCtaSecondaryText, defaults.HeroCtaSecondaryText, 60),

            // Ranges match the editor's sliders. Scale is floored above zero because a zero-scale
            // model is an invisible one, which reads as a broken page rather than a design choice.
            ModelScale = Clamp(input.ModelScale, 0.4, 2.0),
            AutoRotateSpeed = Clamp(input.AutoRotateSpeed, 0, 2),
            Roughness = Clamp(input.Roughness, 0, 1),
            Metalness = Clamp(input.Metalness, 0, 1),
            Accent = Colour(input.Accent, defaults.Accent),

            AmbientTint = Colour(input.AmbientTint, defaults.AmbientTint),
            AmbientIntensity = Clamp(input.AmbientIntensity, 0, 2),
            DirectionalTint = Colour(input.DirectionalTint, defaults.DirectionalTint),
            DirectionalIntensity = Clamp(input.DirectionalIntensity, 0, 4),

            ContactTitle = Text(input.ContactTitle, defaults.ContactTitle, 200),
            ContactSubtitle = Text(input.ContactSubtitle, defaults.ContactSubtitle, 500),
        };
    }
}
