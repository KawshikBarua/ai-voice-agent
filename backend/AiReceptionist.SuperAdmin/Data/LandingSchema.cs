namespace AiReceptionist.SuperAdmin.Data;

/// <summary>
/// The marketing site's editable content: hero wording, the parameters of the 3D sculpture and
/// its lighting, and the heading above the demo form.
///
/// Unlike <see cref="Shared.BillingSchema"/> this is not shared with the tenant API. Only the
/// super admin console writes it, and only the public landing site reads it, so it lives here
/// rather than in Shared.
///
/// It is a single-row table keyed on a constant Id. The content is one document — there is one
/// landing page — and a one-row table keeps reads free of ordering and "which row wins" questions
/// while still being a plain relational object anyone can inspect and back up.
/// </summary>
public static class LandingSchema
{
    /// <summary>The only row's primary key. The table is constrained so no second row can exist.</summary>
    public const int SingletonId = 1;

    /// <summary>Each statement runs as its own batch: a column added by an earlier statement is
    /// not visible to a later one within the same batch.</summary>
    public static readonly string[] Statements =
    [
        """
        IF OBJECT_ID('LandingContent') IS NULL
        CREATE TABLE LandingContent (
            Id                      INT NOT NULL PRIMARY KEY,

            -- Hero. Headline may contain newlines; the site treats each as a deliberate
            -- line break rather than reflowing the text itself.
            HeroEyebrow             NVARCHAR(80)   NOT NULL,
            HeroHeadline            NVARCHAR(200)  NOT NULL,
            HeroSubheadline         NVARCHAR(500)  NOT NULL,
            HeroCtaText             NVARCHAR(60)   NOT NULL,
            HeroCtaSecondaryText    NVARCHAR(60)   NOT NULL,

            -- 3D sculpture. Stored as FLOAT because they are continuous slider values, not money.
            ModelScale              FLOAT          NOT NULL,
            AutoRotateSpeed         FLOAT          NOT NULL,
            Roughness               FLOAT          NOT NULL,
            Metalness               FLOAT          NOT NULL,
            Accent                  NVARCHAR(9)    NOT NULL,

            -- Lighting.
            AmbientTint             NVARCHAR(9)    NOT NULL,
            AmbientIntensity        FLOAT          NOT NULL,
            DirectionalTint         NVARCHAR(9)    NOT NULL,
            DirectionalIntensity    FLOAT          NOT NULL,

            -- Demo section heading.
            ContactTitle            NVARCHAR(200)  NOT NULL,
            ContactSubtitle         NVARCHAR(500)  NOT NULL,

            UpdatedAt               DATETIME2      NOT NULL,
            UpdatedByUserId         INT            NULL,

            -- One landing page, one row. Without this a stray insert would make every read
            -- ambiguous, and the site would show whichever row happened to come back first.
            CONSTRAINT CK_LandingContent_Singleton CHECK (Id = 1)
        );
        """,

        // Seed the row with the same defaults the site ships with, so an install that has never
        // been edited serves exactly what the React app would render on its own.
        $"""
        IF NOT EXISTS (SELECT 1 FROM LandingContent WHERE Id = {SingletonId})
        INSERT INTO LandingContent (
            Id,
            HeroEyebrow, HeroHeadline, HeroSubheadline, HeroCtaText, HeroCtaSecondaryText,
            ModelScale, AutoRotateSpeed, Roughness, Metalness, Accent,
            AmbientTint, AmbientIntensity, DirectionalTint, DirectionalIntensity,
            ContactTitle, ContactSubtitle,
            UpdatedAt
        )
        VALUES (
            {SingletonId},
            N'AI receptionist',
            N'Never miss' + CHAR(10) + N'another call.',
            N'Frontly answers every call in your voice, books the appointment, and hands you the notes before the line goes quiet.',
            N'Request a demo',
            N'Hear it answer',
            1.0, 0.35, 0.12, 0.92, N'#0097b2',
            N'#dff1f5', 0.55, N'#ffffff', 1.6,
            N'See it answer your phone.',
            N'Tell us a little about your business and we will set up a live line you can call within a day.',
            SYSUTCDATETIME()
        );
        """,
    ];
}
