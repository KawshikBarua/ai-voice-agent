using AiReceptionist.Api.Common;
using AiReceptionist.Api.Domain;
using AiReceptionist.Api.Services;
using Dapper;

namespace AiReceptionist.Api.Data.Repositories;

/// <summary>The single, platform-wide wording of the agent prompt (one row, edited by SuperAdmin).
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
    private readonly ICache _cache;

    public PromptTemplateRepository(IDbConnectionFactory db, ICache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<PromptTemplate?> GetAsync()
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<PromptTemplate>(
            "SELECT TOP 1 * FROM PromptTemplate ORDER BY Id");
    }

    public async Task<PromptTemplate> GetEffectiveAsync()
    {
        // Read on every agent sync, and a restart syncs every connected tenant at once. The stored
        // row is what gets cached — an empty one when nothing is customised, so that case is cached
        // too — never the merged text, so a deploy that improves a default reaches the very next sync.
        var row = await _cache.GetOrSetAsync(CacheKeys.PromptTemplate, CacheTtl.Config,
            async () => await GetAsync() ?? new PromptTemplate()) ?? new PromptTemplate();

        return new PromptTemplate
        {
            Id = row.Id,
            Persona = Coalesce(row.Persona, PromptDefaults.Persona),
            CoreRules = Coalesce(row.CoreRules, PromptDefaults.CoreRules),
            TriageState = Coalesce(row.TriageState, PromptDefaults.TriageState),
            SchedulingState = Coalesce(row.SchedulingState, PromptDefaults.SchedulingState),
            FieldSchedulingState = Coalesce(row.FieldSchedulingState, PromptDefaults.FieldSchedulingState),
            BookingState = Coalesce(row.BookingState, PromptDefaults.BookingState),
            ExistingBookingState = Coalesce(row.ExistingBookingState, PromptDefaults.ExistingBookingState),
            ModifiedAt = row.ModifiedAt,
            ModifiedByUserId = row.ModifiedByUserId,
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
            template.TriageState,
            template.SchedulingState,
            template.FieldSchedulingState,
            template.BookingState,
            template.ExistingBookingState,
            userId,
            id,
        };

        if (id is null)
            await conn.ExecuteAsync(@"
                INSERT INTO PromptTemplate (Persona, CoreRules, TriageState, SchedulingState, FieldSchedulingState,
                    BookingState, ExistingBookingState, ModifiedAt, ModifiedByUserId)
                VALUES (@Persona, @CoreRules, @TriageState, @SchedulingState, @FieldSchedulingState,
                    @BookingState, @ExistingBookingState, GETUTCDATE(), @userId)", p);
        else
            await conn.ExecuteAsync(@"
                UPDATE PromptTemplate SET Persona=@Persona, CoreRules=@CoreRules, TriageState=@TriageState,
                    SchedulingState=@SchedulingState, FieldSchedulingState=@FieldSchedulingState,
                    BookingState=@BookingState, ExistingBookingState=@ExistingBookingState,
                    ModifiedAt=GETUTCDATE(), ModifiedByUserId=@userId
                WHERE Id=@id", p);

        await _cache.RemoveAsync(CacheKeys.PromptTemplate);
    }

    private static string Coalesce(string? stored, string fallback) =>
        string.IsNullOrWhiteSpace(stored) ? fallback : stored.Trim();
}
