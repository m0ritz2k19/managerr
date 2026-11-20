using System.Data;
using Dapper;
using Managerr.Common.Paths;
using Microsoft.Data.Sqlite;

namespace Managerr.Datastore;

public enum DatabaseKind
{
    Core = 1,
    Cache = 2,
    Telemetry = 3,
}

public sealed record DatabaseStorageInfo(
    string Kind,
    string FilePath,
    long SizeBytes,
    long WalSizeBytes,
    string JournalMode,
    int SchemaVersion);

public interface ISqliteDbFactory : IAsyncDisposable
{
    string GetDatabasePath(DatabaseKind kind);
    Task<SqliteConnection> OpenReadConnectionAsync(DatabaseKind kind = DatabaseKind.Core, CancellationToken ct = default);
    Task<T> ExecuteWriteAsync<T>(DatabaseKind kind, Func<SqliteConnection, SqliteTransaction, Task<T>> action, CancellationToken ct = default);
    Task ExecuteWriteAsync(DatabaseKind kind, Func<SqliteConnection, SqliteTransaction, Task> action, CancellationToken ct = default);
    Task<IReadOnlyList<DatabaseStorageInfo>> GetStorageStatsAsync(CancellationToken ct = default);
}

/// <summary>
/// Manages Managerr's 3-file embedded SQLite WAL datastore (/config/managerr.db, cache.db, telemetry.db)
/// with zero external database dependencies and per-file serialized write coordination to eliminate SQLITE_BUSY.
/// </summary>
public sealed class SqliteDbFactory : ISqliteDbFactory
{
    private readonly IAppPaths _paths;
    private readonly Dictionary<DatabaseKind, SemaphoreSlim> _writeLocks = new()
    {
        [DatabaseKind.Core] = new SemaphoreSlim(1, 1),
        [DatabaseKind.Cache] = new SemaphoreSlim(1, 1),
        [DatabaseKind.Telemetry] = new SemaphoreSlim(1, 1),
    };

    static SqliteDbFactory()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public SqliteDbFactory(IAppPaths paths)
    {
        _paths = paths;
    }

    public string GetDatabasePath(DatabaseKind kind) => kind switch
    {
        DatabaseKind.Core => _paths.CoreDatabasePath,
        DatabaseKind.Cache => _paths.CacheDatabasePath,
        DatabaseKind.Telemetry => _paths.TelemetryDatabasePath,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public async Task<SqliteConnection> OpenReadConnectionAsync(
        DatabaseKind kind = DatabaseKind.Core,
        CancellationToken ct = default)
    {
        var conn = CreateConnection(kind);
        await conn.OpenAsync(ct);
        await ApplyConnectionPragmasAsync(conn, ct);
        return conn;
    }

    public async Task<T> ExecuteWriteAsync<T>(
        DatabaseKind kind,
        Func<SqliteConnection, SqliteTransaction, Task<T>> action,
        CancellationToken ct = default)
    {
        var gate = _writeLocks[kind];
        await gate.WaitAsync(ct);
        try
        {
            await using var conn = CreateConnection(kind);
            await conn.OpenAsync(ct);
            await ApplyConnectionPragmasAsync(conn, ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var result = await action(conn, tx);
            await tx.CommitAsync(ct);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ExecuteWriteAsync(
        DatabaseKind kind,
        Func<SqliteConnection, SqliteTransaction, Task> action,
        CancellationToken ct = default)
    {
        await ExecuteWriteAsync<int>(
            kind,
            async (conn, tx) =>
            {
                await action(conn, tx);
                return 0;
            },
            ct);
    }

    public async Task<IReadOnlyList<DatabaseStorageInfo>> GetStorageStatsAsync(CancellationToken ct = default)
    {
        var list = new List<DatabaseStorageInfo>(3);
        foreach (var kind in Enum.GetValues<DatabaseKind>())
        {
            var path = GetDatabasePath(kind);
            await using var conn = await OpenReadConnectionAsync(kind, ct);
            var journalMode = await conn.ExecuteScalarAsync<string>("PRAGMA journal_mode;") ?? "unknown";
            var hasMigTable = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='schema_migrations';");
            var version = hasMigTable > 0
                ? await conn.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(version), 0) FROM schema_migrations;")
                : 0;

            var size = File.Exists(path) ? new FileInfo(path).Length : 0L;
            var walPath = path + "-wal";
            var walSize = File.Exists(walPath) ? new FileInfo(walPath).Length : 0L;

            list.Add(new DatabaseStorageInfo(
                Kind: kind.ToString().ToLowerInvariant(),
                FilePath: path,
                SizeBytes: size,
                WalSizeBytes: walSize,
                JournalMode: journalMode,
                SchemaVersion: version));
        }

        return list;
    }

    public ValueTask DisposeAsync()
    {
        foreach (var sem in _writeLocks.Values)
        {
            sem.Dispose();
        }
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    private SqliteConnection CreateConnection(DatabaseKind kind)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = GetDatabasePath(kind),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 10,
        };
        return new SqliteConnection(builder.ToString());
    }

    private static async Task ApplyConnectionPragmasAsync(SqliteConnection conn, CancellationToken ct)
    {
        const string pragmas = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 10000;
            PRAGMA temp_store = MEMORY;
            PRAGMA mmap_size = 268435456;
            PRAGMA foreign_keys = ON;
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = pragmas;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
