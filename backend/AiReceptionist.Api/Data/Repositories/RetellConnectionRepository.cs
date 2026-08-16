using AiReceptionist.Api.Domain;
using Dapper;

namespace AiReceptionist.Api.Data.Repositories;

/// <summary>Single, platform-wide Retell connection (one shared account for all tenants).
/// Stored as a single row; <see cref="GetEffectiveAsync"/> merges it with appsettings so the
/// key can live in the database while still honouring an appsettings value as a fallback.</summary>
public interface IRetellConnectionRepository
{
    /// <summary>The stored row exactly as saved (null before the first save). Used by the
    /// admin screens that edit the connection.</summary>
    Task<RetellConnection?> GetAsync();

    /// <summary>Runtime view: the stored row with any empty field filled from appsettings
    /// (Retell:*). Never null. Used everywhere the key/URLs are actually needed.</summary>
    Task<RetellConnection> GetEffectiveAsync();

    Task UpsertAsync(RetellConnection connection, int? userId);
}

public class RetellConnectionRepository : IRetellConnectionRepository
{
    private readonly IDbConnectionFactory _db;
    private readonly IConfiguration _config;

    public RetellConnectionRepository(IDbConnectionFactory db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public async Task<RetellConnection?> GetAsync()
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<RetellConnection>(
            "SELECT TOP 1 * FROM RetellConnection ORDER BY Id");
    }

    public async Task<RetellConnection> GetEffectiveAsync()
    {
        var row = await GetAsync();
        return new RetellConnection
        {
            Id = row?.Id ?? 0,
            ApiKey = Coalesce(row?.ApiKey, _config["Retell:ApiKey"]),
            ApiBaseUrl = Coalesce(row?.ApiBaseUrl, _config["Retell:ApiBaseUrl"]) ?? "https://api.retellai.com",
            WebhookBaseUrl = Coalesce(row?.WebhookBaseUrl, _config["Retell:WebhookBaseUrl"]),
            DefaultVoiceId = Coalesce(row?.DefaultVoiceId, _config["Retell:DefaultVoiceId"]),
            VerifySignature = row?.VerifySignature ?? _config.GetValue("Retell:VerifySignature", true),
            ModifiedAt = row?.ModifiedAt,
            ModifiedByUserId = row?.ModifiedByUserId,
        };
    }

    public async Task UpsertAsync(RetellConnection c, int? userId)
    {
        using var conn = _db.Create();
        var id = await conn.ExecuteScalarAsync<int?>("SELECT TOP 1 Id FROM RetellConnection ORDER BY Id");
        var p = new
        {
            c.ApiKey,
            ApiBaseUrl = string.IsNullOrWhiteSpace(c.ApiBaseUrl) ? "https://api.retellai.com" : c.ApiBaseUrl.Trim(),
            c.WebhookBaseUrl,
            c.DefaultVoiceId,
            c.VerifySignature,
            userId,
            id,
        };

        if (id is null)
            await conn.ExecuteAsync(@"
                INSERT INTO RetellConnection (ApiKey, ApiBaseUrl, WebhookBaseUrl, DefaultVoiceId, VerifySignature, ModifiedAt, ModifiedByUserId)
                VALUES (@ApiKey, @ApiBaseUrl, @WebhookBaseUrl, @DefaultVoiceId, @VerifySignature, GETUTCDATE(), @userId)", p);
        else
            await conn.ExecuteAsync(@"
                UPDATE RetellConnection SET ApiKey=@ApiKey, ApiBaseUrl=@ApiBaseUrl, WebhookBaseUrl=@WebhookBaseUrl,
                    DefaultVoiceId=@DefaultVoiceId, VerifySignature=@VerifySignature,
                    ModifiedAt=GETUTCDATE(), ModifiedByUserId=@userId
                WHERE Id=@id", p);
    }

    private static string? Coalesce(string? primary, string? fallback) =>
        string.IsNullOrWhiteSpace(primary) ? (string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim()) : primary.Trim();
}
