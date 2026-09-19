using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace AiReceptionist.Api.Common;

/// <summary>
/// Read-through cache in front of the database.
///
/// Redis when <c>ConnectionStrings:Redis</c> is set, an in-process dictionary when it is not (see
/// Program.cs) — so a developer needs no extra service running, and a deployment gets one cache
/// shared by every instance.
///
/// The contract that matters is what happens when the cache is unavailable: every operation here
/// swallows its own failures and falls through to the loader. A cache that can take the API down
/// when Redis blinks is worse than no cache at all, and Redis blinking is a routine event —
/// failover, a restart, a network hiccup. Losing the cache should cost latency, never requests.
/// </summary>
public interface ICache
{
    /// <summary>Returns the cached value, or loads it, stores it and returns that. Nulls are not
    /// cached: a miss for a row that does not exist is cheap, and negative caching is one more
    /// thing to invalidate.</summary>
    Task<T?> GetOrSetAsync<T>(string key, TimeSpan ttl, Func<Task<T?>> load, CancellationToken ct = default);

    /// <summary>Drops keys after a write. Call it for anything whose staleness would be wrong
    /// rather than merely late.</summary>
    Task RemoveAsync(params string[] keys);
}

public sealed class Cache : ICache
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<Cache> _logger;

    // Web defaults so the payload matches the casing the rest of the app serialises with; it also
    // makes a cached entry readable with `redis-cli GET`, which is worth a lot when diagnosing.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Cache(IDistributedCache cache, ILogger<Cache> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<T?> GetOrSetAsync<T>(string key, TimeSpan ttl, Func<Task<T?>> load, CancellationToken ct = default)
    {
        try
        {
            if (await _cache.GetAsync(key, ct) is { } hit)
                return JsonSerializer.Deserialize<T>(hit, Json);
        }
        catch (Exception ex)
        {
            // Includes a payload written by an older build whose shape has since changed: a
            // deserialisation failure is a miss, not an error the caller should see.
            _logger.LogDebug(ex, "Cache read failed for {Key}", key);
        }

        var value = await load();
        if (value is null) return value;

        try
        {
            await _cache.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(value, Json),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cache write failed for {Key}", key);
        }

        return value;
    }

    public async Task RemoveAsync(params string[] keys)
    {
        foreach (var key in keys)
        {
            try
            {
                await _cache.RemoveAsync(key);
            }
            catch (Exception ex)
            {
                // The entry expires on its own soon enough; the write itself has already landed.
                _logger.LogWarning(ex, "Cache invalidation failed for {Key}", key);
            }
        }
    }
}

/// <summary>
/// Every key in one place, prefixed so one Redis instance can serve several environments without
/// them reading each other's rows, and so `SCAN ai:*` shows what this application put there.
/// </summary>
public static class CacheKeys
{
    private const string Prefix = "ai:";

    /// <summary>Tenant settings row — read by the prompt builder, the dashboard and every live
    /// call tool.</summary>
    public static string Org(int orgId) => $"{Prefix}org:{orgId}";

    /// <summary>Retell agent linkage — read by every webhook and every sync pass.</summary>
    public static string Agent(int orgId) => $"{Prefix}agent:{orgId}";

    /// <summary>The platform-wide Retell connection. One row, read on every outbound call.</summary>
    public const string RetellConnection = Prefix + "retell:connection";

    /// <summary>Minutes, as the billing page polls them.</summary>
    public static string Usage(int orgId) => $"{Prefix}usage:{orgId}";

    /// <summary>Sidebar badge counts.</summary>
    public static string SidebarCounts(int orgId) => $"{Prefix}counts:{orgId}";

    /// <summary>Dashboard aggregates. Keyed by the tenant-local day as well as the organization,
    /// because the window every figure is measured over rolls at the tenant's midnight, not at
    /// UTC's — without the date a tenant would read yesterday's "today" until the entry expired.</summary>
    public static string DashboardStats(int orgId, DateTime localDate) =>
        $"{Prefix}stats:{orgId}:{localDate:yyyyMMdd}";
}

/// <summary>How long each kind of entry is allowed to be stale.</summary>
public static class CacheTtl
{
    /// <summary>Configuration rows. Long, because every path that writes them also drops the
    /// key — the expiry is a backstop for a write that happened somewhere else.</summary>
    public static readonly TimeSpan Config = TimeSpan.FromMinutes(5);

    /// <summary>Aggregates nobody can write directly. Short, because the only correctness
    /// argument for them is "recent enough", and a dashboard is read far more than it changes.</summary>
    public static readonly TimeSpan Stats = TimeSpan.FromSeconds(60);

    /// <summary>Counters behind a poll. Long enough to collapse several open tabs into one
    /// query, short enough that nobody notices the lag.</summary>
    public static readonly TimeSpan Counters = TimeSpan.FromSeconds(20);

    /// <summary>Minutes used. The page polls this every 15 seconds and a call has to end before
    /// its minutes exist at all, so ten seconds costs nothing and removes most of the traffic.</summary>
    public static readonly TimeSpan Usage = TimeSpan.FromSeconds(10);
}
