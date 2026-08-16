using AiReceptionist.Api.Domain;
using AiReceptionist.Api.Services;
using Dapper;

namespace AiReceptionist.Api.Data.Repositories;

/// <summary>The single, platform-wide wording of the system prompt (one row, edited by SuperAdmin).
/// <see cref="GetEffectiveAsync"/> merges it with the built-in defaults so a section left blank
/// keeps tracking the platform default instead of freezing whatever text was current when it was
/// first saved.</summary>
public interface IPromptTemplateRepository
{
    /// <summary>The stored row exactly as saved — null sections mean "use the default".
    /// Used by the admin screen, which has to show which sections are actually customised.</summary>
    Task<PromptTemplate?> GetAsync();

    /// <summary>Runtime view: every section filled, defaults where nothing is stored. Never null.</summary>
    Task<PromptTemplate> GetEffectiveAsync();

    Task UpsertAsync(PromptTemplate template, int? userId);
}

public class PromptTemplateRepository : IPromptTemplateRepository
{
    private readonly IDbConnectionFactory _db;

    public PromptTemplateRepository(IDbConnectionFactory db) => _db = db;

    public async Task<PromptTemplate?> GetAsync()
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<PromptTemplate>(
            "SELECT TOP 1 * FROM PromptTemplate ORDER BY Id");
    }

    public async Task<PromptTemplate> GetEffectiveAsync()
    {
        var row = await GetAsync();
        return new PromptTemplate
        {
            Id = row?.Id ?? 0,
            Persona = Coalesce(row?.Persona, PromptDefaults.Persona),
            CoreRules = Coalesce(row?.CoreRules, PromptDefaults.CoreRules),
            ConversationGuide = Coalesce(row?.ConversationGuide, PromptDefaults.ConversationGuide),
            FieldServiceGuide = Coalesce(row?.FieldServiceGuide, PromptDefaults.FieldServiceGuide),
            ToolPolicy = Coalesce(row?.ToolPolicy, PromptDefaults.ToolPolicy),
            ModifiedAt = row?.ModifiedAt,
            ModifiedByUserId = row?.ModifiedByUserId,
        };
    }

    public async Task UpsertAsync(PromptTemplate template, int? userId)
    {
        using var conn = _db.Create();
        var id = await conn.ExecuteScalarAsync<int?>("SELECT TOP 1 Id FROM PromptTemplate ORDER BY Id");

        // Blank and default-identical sections are stored as NULL by the caller, so the row only
        // ever holds genuine overrides.
        var p = new
        {
            template.Persona,
            template.CoreRules,
            template.ConversationGuide,
            template.FieldServiceGuide,
            template.ToolPolicy,
            userId,
            id,
        };

        if (id is null)
            await conn.ExecuteAsync(@"
                INSERT INTO PromptTemplate (Persona, CoreRules, ConversationGuide, FieldServiceGuide, ToolPolicy, ModifiedAt, ModifiedByUserId)
                VALUES (@Persona, @CoreRules, @ConversationGuide, @FieldServiceGuide, @ToolPolicy, GETUTCDATE(), @userId)", p);
        else
            await conn.ExecuteAsync(@"
                UPDATE PromptTemplate SET Persona=@Persona, CoreRules=@CoreRules,
                    ConversationGuide=@ConversationGuide, FieldServiceGuide=@FieldServiceGuide,
                    ToolPolicy=@ToolPolicy, ModifiedAt=GETUTCDATE(), ModifiedByUserId=@userId
                WHERE Id=@id", p);
    }

    private static string Coalesce(string? stored, string fallback) =>
        string.IsNullOrWhiteSpace(stored) ? fallback : stored.Trim();
}
