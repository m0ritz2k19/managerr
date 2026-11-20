using Dapper;
using Managerr.Common.Crypto;

namespace Managerr.Datastore.Services;

public interface IAppSettingsService
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task<string?> GetSecretAsync(string key, CancellationToken ct = default);
    Task<MaskedSecret> GetMaskedSecretAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, bool isSecret = false, CancellationToken ct = default);
}

public sealed class AppSettingsService(ISqliteDbFactory dbFactory, ISecretProtector secretProtector) : IAppSettingsService
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await conn.QuerySingleOrDefaultAsync<(string Value, int IsSecret)>(
            "SELECT value AS Value, is_secret AS IsSecret FROM app_settings WHERE key = @Key;",
            new { Key = key });

        if (row == default)
        {
            return null;
        }

        return row.IsSecret != 0 && secretProtector.IsProtectedEnvelope(row.Value)
            ? secretProtector.Unprotect(row.Value)
            : row.Value;
    }

    public Task<string?> GetSecretAsync(string key, CancellationToken ct = default) => GetAsync(key, ct);

    public async Task<MaskedSecret> GetMaskedSecretAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var stored = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT value FROM app_settings WHERE key = @Key;",
            new { Key = key });
        return secretProtector.Mask(stored);
    }

    public Task SetAsync(string key, string value, bool isSecret = false, CancellationToken ct = default)
    {
        var stored = isSecret ? secretProtector.Protect(value) : value;
        return dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                INSERT INTO app_settings (key, value, is_secret, updated_at)
                VALUES (@Key, @Value, @IsSecret, @UpdatedAt)
                ON CONFLICT(key) DO UPDATE SET
                    value      = excluded.value,
                    is_secret  = excluded.is_secret,
                    updated_at = excluded.updated_at;
                """,
                new
                {
                    Key = key,
                    Value = stored,
                    IsSecret = isSecret ? 1 : 0,
                    UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
                },
                transaction: tx),
            ct);
    }
}
