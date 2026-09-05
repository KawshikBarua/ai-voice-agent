using AiReceptionist.SuperAdmin.Models;
using Dapper;

namespace AiReceptionist.SuperAdmin.Data.Repositories;

public interface ILandingContentRepository
{
    /// <summary>The current content, or null if the singleton row is missing — which only happens
    /// when the schema has not been initialized yet.</summary>
    Task<LandingContent?> GetAsync();

    Task SaveAsync(LandingContentInput input, int? userId);
}

/// <summary>Reads and writes the one row that drives the public marketing site.</summary>
public class LandingContentRepository : ILandingContentRepository
{
    private readonly IDbConnectionFactory _db;
    public LandingContentRepository(IDbConnectionFactory db) => _db = db;

    private const string Columns = @"
        HeroEyebrow, HeroHeadline, HeroSubheadline, HeroCtaText, HeroCtaSecondaryText,
        ModelScale, AutoRotateSpeed, Roughness, Metalness, Accent,
        AmbientTint, AmbientIntensity, DirectionalTint, DirectionalIntensity,
        ContactTitle, ContactSubtitle,
        UpdatedAt, UpdatedByUserId";

    public async Task<LandingContent?> GetAsync()
    {
        using var db = _db.Create();
        return await db.QuerySingleOrDefaultAsync<LandingContent>(
            $"SELECT {Columns} FROM LandingContent WHERE Id = @id",
            new { id = LandingSchema.SingletonId });
    }

    public async Task SaveAsync(LandingContentInput input, int? userId)
    {
        using var db = _db.Create();
        await db.ExecuteAsync(
            """
            UPDATE LandingContent SET
                HeroEyebrow          = @HeroEyebrow,
                HeroHeadline         = @HeroHeadline,
                HeroSubheadline      = @HeroSubheadline,
                HeroCtaText          = @HeroCtaText,
                HeroCtaSecondaryText = @HeroCtaSecondaryText,
                ModelScale           = @ModelScale,
                AutoRotateSpeed      = @AutoRotateSpeed,
                Roughness            = @Roughness,
                Metalness            = @Metalness,
                Accent               = @Accent,
                AmbientTint          = @AmbientTint,
                AmbientIntensity     = @AmbientIntensity,
                DirectionalTint      = @DirectionalTint,
                DirectionalIntensity = @DirectionalIntensity,
                ContactTitle         = @ContactTitle,
                ContactSubtitle      = @ContactSubtitle,
                UpdatedAt            = SYSUTCDATETIME(),
                UpdatedByUserId      = @UserId
            WHERE Id = @Id
            """,
            new
            {
                input.HeroEyebrow,
                input.HeroHeadline,
                input.HeroSubheadline,
                input.HeroCtaText,
                input.HeroCtaSecondaryText,
                input.ModelScale,
                input.AutoRotateSpeed,
                input.Roughness,
                input.Metalness,
                input.Accent,
                input.AmbientTint,
                input.AmbientIntensity,
                input.DirectionalTint,
                input.DirectionalIntensity,
                input.ContactTitle,
                input.ContactSubtitle,
                UserId = userId,
                Id = LandingSchema.SingletonId,
            });
    }
}
