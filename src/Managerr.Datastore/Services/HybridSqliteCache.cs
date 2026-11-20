using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Caching.Memory;

namespace Managerr.Datastore.Services;

public sealed record HybridCacheStats(
    int TotalEntries,
    int ActiveEntries,
    int ExpiredEntries);

public interface IHybridSqliteCache
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task<int> PurgeExpiredAsync(CancellationToken ct = default);
    Task<HybridCacheStats> GetStatsAsync(CancellationToken ct = default);
}

/// <summary>
/// Zero-Redis 2-tier cache combining in-process IMemoryCache (L1) + embedded /config/cache.db (L2).
/// </summary>
public sealed class HybridSqliteCache(ISqliteDbFactory dbFactory, IMemoryCache memoryCache) : IHybridSqliteCache
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan MaxMemoryL1Window = TimeSpan.FromHours(1);

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        if (memoryCache.TryGetValue(key, out T? cached))
        {
            return cached;
        }

        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Cache, ct);
        var row = await conn.QuerySingleOrDefaultAsync<(string PayloadJson, string ExpiresAt)>(
            "SELECT payload_json AS PayloadJson, expires_at AS ExpiresAt FROM cache_entries WHERE cache_key = @Key;",
            new { Key = key });

        if (row == default || !DateTimeOffset.TryParse(row.ExpiresAt, out var expiresAt))
        {
            return default;
        }

        var remaining = expiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return default;
        }

        var deserialized = JsonSerializer.Deserialize<T>(row.PayloadJson, JsonOpts);
        if (deserialized is not null)
        {
            memoryCache.Set(key, deserialized, remaining > MaxMemoryL1Window ? MaxMemoryL1Window : remaining);
        }

        return deserialized;
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(ttl);
        var payload = JsonSerializer.Serialize(value, JsonOpts);

        memoryCache.Set(key, value, ttl > MaxMemoryL1Window ? MaxMemoryL1Window : ttl);

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Cache,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO cache_entries (cache_key, payload_json, expires_at, updated_at)
                    VALUES (@Key, @Payload, @ExpiresAt, @UpdatedAt)
                    ON CONFLICT(cache_key) DO UPDATE SET
                        payload_json = excluded.payload_json,
                        expires_at   = excluded.expires_at,
                        updated_at   = excluded.updated_at;
                    """,
                    new
                    {
                        Key = key,
                        Payload = payload,
                        ExpiresAt = expiresAt.ToString("O"),
                        UpdatedAt = now.ToString("O"),
                    },
                    transaction: tx);
            },
            ct);
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        memoryCache.Remove(key);
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Cache,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    "DELETE FROM cache_entries WHERE cache_key = @Key;",
                    new { Key = key },
                    transaction: tx);
            },
            ct);
    }

    public Task<int> PurgeExpiredAsync(CancellationToken ct = default) =>
        dbFactory.ExecuteWriteAsync(
            DatabaseKind.Cache,
            (conn, tx) => conn.ExecuteAsync(
                "DELETE FROM cache_entries WHERE expires_at <= @Now;",
                new { Now = DateTimeOffset.UtcNow.ToString("O") },
                transaction: tx),
            ct);

    public async Task<HybridCacheStats> GetStatsAsync(CancellationToken ct = default)
    {
        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Cache, ct);
        var row = await conn.QuerySingleAsync<(int Total, int Active)>(
            """
            SELECT COUNT(1) AS Total,
                   COALESCE(SUM(CASE WHEN expires_at > @Now THEN 1 ELSE 0 END), 0) AS Active
            FROM cache_entries;
            """,
            new { Now = nowIso });
        return new HybridCacheStats(row.Total, row.Active, Math.Max(0, row.Total - row.Active));
    }
}
