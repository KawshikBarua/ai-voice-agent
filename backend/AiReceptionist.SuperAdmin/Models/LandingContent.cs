namespace AiReceptionist.SuperAdmin.Models;

/// <summary>
/// The marketing site's editable content, as stored. One row exists for the whole platform —
/// there is one landing page.
///
/// The property names deliberately mirror the SQL columns rather than the shape the React app
/// consumes; <see cref="LandingContentResponse"/> does that translation, so the database is free
/// to stay flat while the site receives something structured.
/// </summary>
public class LandingContent
{
    public string HeroEyebrow { get; set; } = "";
    public string HeroHeadline { get; set; } = "";
    public string HeroSubheadline { get; set; } = "";
    public string HeroCtaText { get; set; } = "";
    public string HeroCtaSecondaryText { get; set; } = "";

    public double ModelScale { get; set; }
    public double AutoRotateSpeed { get; set; }
    public double Roughness { get; set; }
    public double Metalness { get; set; }
    public string Accent { get; set; } = "";

    public string AmbientTint { get; set; } = "";
    public double AmbientIntensity { get; set; }
    public string DirectionalTint { get; set; } = "";
    public double DirectionalIntensity { get; set; }

    public string ContactTitle { get; set; } = "";
    public string ContactSubtitle { get; set; } = "";

    public DateTime UpdatedAt { get; set; }
    public int? UpdatedByUserId { get; set; }
}

/// <summary>What the editor submits. Same shape as the stored row minus the audit columns, which
/// the console fills in itself — a form post must never be able to backdate a change or claim it
/// was made by another operator.</summary>
public class LandingContentInput
{
    public string HeroEyebrow { get; set; } = "";
    public string HeroHeadline { get; set; } = "";
    public string HeroSubheadline { get; set; } = "";
    public string HeroCtaText { get; set; } = "";
    public string HeroCtaSecondaryText { get; set; } = "";

    public double ModelScale { get; set; } = 1;
    public double AutoRotateSpeed { get; set; } = 0.35;
    public double Roughness { get; set; } = 0.12;
    public double Metalness { get; set; } = 0.92;
    public string Accent { get; set; } = "#0097b2";

    public string AmbientTint { get; set; } = "#dff1f5";
    public double AmbientIntensity { get; set; } = 0.55;
    public string DirectionalTint { get; set; } = "#ffffff";
    public double DirectionalIntensity { get; set; } = 1.6;

    public string ContactTitle { get; set; } = "";
    public string ContactSubtitle { get; set; } = "";

    /// <summary>The wording and settings the product ships with, used by "restore defaults".
    /// Mirrors the seed in <see cref="Data.LandingSchema"/> — keep both in step, since the seed
    /// is what a fresh install gets and this is what a reset returns to.</summary>
    public static LandingContentInput Defaults() => new()
    {
        HeroEyebrow = "AI receptionist",
        HeroHeadline = "Never miss\nanother call.",
        HeroSubheadline =
            "Frontly answers every call in your voice, books the appointment, and hands you the " +
            "notes before the line goes quiet.",
        HeroCtaText = "Request a demo",
        HeroCtaSecondaryText = "Hear it answer",
        ContactTitle = "See it answer your phone.",
        ContactSubtitle =
            "Tell us a little about your business and we will set up a live line you can call " +
            "within a day.",
        // The numeric and colour fields already carry the shipped values as property defaults.
    };
}

/// <summary>
/// The public JSON contract the landing site reads.
///
/// Grouped into the same four sections the site's content store expects, so the browser does no
/// reshaping. This is a separate type from the stored row on purpose: it is a published API, and
/// renaming a database column should not silently change what an anonymous endpoint returns.
/// </summary>
public class LandingContentResponse
{
    public required HeroSection Hero { get; init; }
    public required SceneSection Scene { get; init; }
    public required LightingSection Lighting { get; init; }
    public required ContactSection Contact { get; init; }
    public required DateTime UpdatedAt { get; init; }

    public class HeroSection
    {
        public required string Eyebrow { get; init; }
        public required string Headline { get; init; }
        public required string Subheadline { get; init; }
        public required string CtaText { get; init; }
        public required string CtaSecondaryText { get; init; }
    }

    public class SceneSection
    {
        public required double ModelScale { get; init; }
        public required double AutoRotateSpeed { get; init; }
        public required double Roughness { get; init; }
        public required double Metalness { get; init; }
        public required string Accent { get; init; }
    }

    public class LightingSection
    {
        public required string AmbientTint { get; init; }
        public required double AmbientIntensity { get; init; }
        public required string DirectionalTint { get; init; }
        public required double DirectionalIntensity { get; init; }
    }

    public class ContactSection
    {
        public required string Title { get; init; }
        public required string Subtitle { get; init; }
    }

    public static LandingContentResponse From(LandingContent row) => new()
    {
        Hero = new HeroSection
        {
            Eyebrow = row.HeroEyebrow,
            Headline = row.HeroHeadline,
            Subheadline = row.HeroSubheadline,
            CtaText = row.HeroCtaText,
            CtaSecondaryText = row.HeroCtaSecondaryText,
        },
        Scene = new SceneSection
        {
            ModelScale = row.ModelScale,
            AutoRotateSpeed = row.AutoRotateSpeed,
            Roughness = row.Roughness,
            Metalness = row.Metalness,
            Accent = row.Accent,
        },
        Lighting = new LightingSection
        {
            AmbientTint = row.AmbientTint,
            AmbientIntensity = row.AmbientIntensity,
            DirectionalTint = row.DirectionalTint,
            DirectionalIntensity = row.DirectionalIntensity,
        },
        Contact = new ContactSection
        {
            Title = row.ContactTitle,
            Subtitle = row.ContactSubtitle,
        },
        UpdatedAt = row.UpdatedAt,
    };
}
