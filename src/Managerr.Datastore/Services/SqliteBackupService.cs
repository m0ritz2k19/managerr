using Managerr.Common.Paths;

namespace Managerr.Datastore.Services;

public sealed record BackupFileMetadata(
    string FileName,
    string FullPath,
    long SizeBytes,
    DateTimeOffset CreatedAtUtc);

public interface ISqliteBackupService
{
    Task<BackupFileMetadata> CreateHotBackupAsync(string? reason = null, int maxRetainedBackups = 10, CancellationToken ct = default);
    IReadOnlyList<BackupFileMetadata> ListBackups();
}

/// <summary>
/// Performs online non-blocking hot backups of /config/managerr.db using SQLite's atomic VACUUM INTO command.
/// Ephemeral cache.db and rolling telemetry.db are intentionally excluded so backups stay compact and fast.
/// </summary>
public sealed class SqliteBackupService(ISqliteDbFactory dbFactory, IAppPaths paths) : ISqliteBackupService
{
    public async Task<BackupFileMetadata> CreateHotBackupAsync(
        string? reason = null,
        int maxRetainedBackups = 10,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(paths.BackupsDirectory);

        var slug = string.IsNullOrWhiteSpace(reason)
            ? "manual"
            : new string(reason.Where(c => char.IsLetterOrDigit(c) || c == '-').Take(24).ToArray()).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "backup";
        }

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var fileName = $"managerr-{stamp}-{slug}.db";
        var targetPath = Path.Combine(paths.BackupsDirectory, fileName);

        await using (var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            var escapedTarget = targetPath.Replace("'", "''", StringComparison.Ordinal);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"VACUUM INTO '{escapedTarget}';";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        PruneOldBackups(maxRetainedBackups);

        var info = new FileInfo(targetPath);
        return new BackupFileMetadata(
            FileName: info.Name,
            FullPath: info.FullName,
            SizeBytes: info.Length,
            CreatedAtUtc: new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero));
    }

    public IReadOnlyList<BackupFileMetadata> ListBackups()
    {
        if (!Directory.Exists(paths.BackupsDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(paths.BackupsDirectory, "managerr-*.db")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => new BackupFileMetadata(
                FileName: f.Name,
                FullPath: f.FullName,
                SizeBytes: f.Length,
                CreatedAtUtc: new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)))
            .ToList();
    }

    private void PruneOldBackups(int maxRetained)
    {
        if (maxRetained <= 0 || !Directory.Exists(paths.BackupsDirectory))
        {
            return;
        }

        var existing = Directory.EnumerateFiles(paths.BackupsDirectory, "managerr-*.db")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .Skip(maxRetained)
            .ToList();

        foreach (var stale in existing)
        {
            try
            {
                File.Delete(stale);
            }
            catch
            {
                // Ignore best-effort cleanup race
            }
        }
    }
}
