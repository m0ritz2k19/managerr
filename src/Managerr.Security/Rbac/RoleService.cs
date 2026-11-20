using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Managerr.Datastore;

namespace Managerr.Security.Rbac;

public static class BuiltInRoleKeys
{
    public const string Admin = "system:owner";
    public const string Manager = "media:manager";
    public const string Requester = "media:requester";
    public const string Viewer = "media:viewer";
    public const string CompatToken = "automation:compat";
    public const string MetricsToken = "metrics:readonly";
}

public sealed record RoleDefinition(
    long Id,
    string Key,
    string Name,
    string Description,
    string Color,
    bool IsSystemPreset,
    bool IsImmutable,
    IReadOnlyList<string> Scopes,
    string LibraryAccessMode,
    IReadOnlyList<long> AllowedLibraryIds,
    bool AutoApproveRequests,
    int? MovieRequestQuota,
    int? TvSeasonRequestQuota,
    int? MusicBookRequestQuota,
    int AssignedUserCount,
    string CreatedAt,
    string UpdatedAt,
    int? CleanerrExclusionLimit = null);

public sealed record UpsertCustomRoleRequest(
    string Name,
    string? Key,
    string Description,
    string Color,
    IReadOnlyList<string> Scopes,
    string LibraryAccessMode = "inherit_from_plex",
    IReadOnlyList<long>? AllowedLibraryIds = null,
    bool AutoApproveRequests = false,
    int? MovieRequestQuota = null,
    int? TvSeasonRequestQuota = null,
    int? MusicBookRequestQuota = null,
    int? CleanerrExclusionLimit = null);

public interface IRoleService
{
    Task EnsureBuiltInPresetsSeededAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RoleDefinition>> ListRolesAsync(CancellationToken ct = default);
    Task<RoleDefinition?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<RoleDefinition?> GetByKeyAsync(string key, CancellationToken ct = default);
    Task<RoleDefinition> CreateCustomRoleAsync(UpsertCustomRoleRequest request, CancellationToken ct = default);
    Task<RoleDefinition> CloneRoleAsync(long sourceRoleId, string newName, string? newKey = null, CancellationToken ct = default);
    Task<RoleDefinition> UpdateRoleAsync(long id, UpsertCustomRoleRequest request, CancellationToken ct = default);
    Task DeleteCustomRoleAsync(long id, CancellationToken ct = default);
}

public sealed partial class RoleService(ISqliteDbFactory dbFactory) : IRoleService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"[^a-z0-9:_-]+")]
    private static partial Regex SlugSanitizer();

    public async Task EnsureBuiltInPresetsSeededAsync(CancellationToken ct = default)
    {
        var presets = new (string Key, string Name, string Desc, string Color, bool Immutable, string[] Scopes, string LibMode, bool AutoApprove)[]
        {
            (
                Key: BuiltInRoleKeys.Admin,
                Name: "Admin",
                Desc: "Plex Server Owner — full platform, indexers, downloaders, secrets & RBAC control.",
                Color: "#f59e0b",
                Immutable: true,
                Scopes: PermissionScopes.AllValidScopes.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                LibMode: "all_libraries",
                AutoApprove: true
            ),
            (
                Key: BuiltInRoleKeys.Manager,
                Name: "Manager",
                Desc: "Co-Admin / Curator — manage catalog, profiles, manual searches & language quarantine across permitted libraries (no secrets by default).",
                Color: "#8b5cf6",
                Immutable: false,
                Scopes:
                [
                    PermissionScopes.CatalogRead,
                    PermissionScopes.CatalogRequest,
                    PermissionScopes.CatalogAutoApprove,
                    PermissionScopes.CatalogWrite,
                    PermissionScopes.CatalogDelete,
                    PermissionScopes.ProfilesRead,
                    PermissionScopes.ProfilesWrite,
                    PermissionScopes.IndexersRead,
                    PermissionScopes.IndexersSearch,
                    PermissionScopes.DownloadersRead,
                    PermissionScopes.QueueRead,
                    PermissionScopes.QueueManage,
                    PermissionScopes.QuarantineReview,
                    PermissionScopes.LangVerifyRead,
                    PermissionScopes.LangVerifyExecute,
                    PermissionScopes.CleanerrRead,
                    PermissionScopes.CleanerrRescue,
                    PermissionScopes.CleanerrExclude,
                ],
                LibMode: "inherit_from_plex",
                AutoApprove: true
            ),
            (
                Key: BuiltInRoleKeys.Requester,
                Name: "Requester",
                Desc: "Default for imported Plex Friends & Home Users — browse & request media in shared Plex libraries + personal language preferences (zero indexer/downloader access).",
                Color: "#10b981",
                Immutable: false,
                Scopes:
                [
                    PermissionScopes.CatalogRead,
                    PermissionScopes.CatalogRequest,
                ],
                LibMode: "inherit_from_plex",
                AutoApprove: false
            ),
            (
                Key: BuiltInRoleKeys.Viewer,
                Name: "Viewer",
                Desc: "Read-only household/guest profile — browse catalog & calendar in shared Plex libraries without requesting or modifying items.",
                Color: "#64748b",
                Immutable: false,
                Scopes:
                [
                    PermissionScopes.CatalogRead,
                ],
                LibMode: "inherit_from_plex",
                AutoApprove: false
            ),
            (
                Key: BuiltInRoleKeys.CompatToken,
                Name: "Compat Token",
                Desc: "Preset for scoped ecosystem API keys (Overseerr, Bazarr, Recyclarr, nzb360).",
                Color: "#0ea5e9",
                Immutable: false,
                Scopes:
                [
                    PermissionScopes.CatalogRead,
                    PermissionScopes.CatalogRequest,
                    PermissionScopes.CatalogWrite,
                    PermissionScopes.ProfilesRead,
                    PermissionScopes.CompatSonarr,
                    PermissionScopes.CompatRadarr,
                ],
                LibMode: "all_libraries",
                AutoApprove: true
            ),
            (
                Key: BuiltInRoleKeys.MetricsToken,
                Name: "Metrics Token",
                Desc: "Read-only health, queue counts & Prometheus /metrics token preset for Homepage / Grafana.",
                Color: "#475569",
                Immutable: false,
                Scopes:
                [
                    PermissionScopes.MetricsRead,
                ],
                LibMode: "all_libraries",
                AutoApprove: false
            ),
        };

        var now = DateTimeOffset.UtcNow.ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                foreach (var p in presets)
                {
                    var scopesJson = JsonSerializer.Serialize(p.Scopes, JsonOpts);
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO roles (
                            key, name, description, color, is_system_preset, is_immutable,
                            scopes_json, library_access_mode, allowed_library_ids_json,
                            auto_approve_requests, created_at, updated_at
                        ) VALUES (
                            @Key, @Name, @Desc, @Color, 1, @Immutable,
                            @ScopesJson, @LibMode, '[]',
                            @AutoApprove, @Now, @Now
                        )
                        ON CONFLICT(key) DO UPDATE SET
                            scopes_json = CASE WHEN roles.is_immutable = 1 THEN excluded.scopes_json ELSE roles.scopes_json END;
                        """,
                        new
                        {
                            p.Key,
                            p.Name,
                            p.Desc,
                            p.Color,
                            Immutable = p.Immutable ? 1 : 0,
                            ScopesJson = scopesJson,
                            p.LibMode,
                            AutoApprove = p.AutoApprove ? 1 : 0,
                            Now = now,
                        },
                        transaction: tx);
                }
            },
            ct);
    }

    public async Task<IReadOnlyList<RoleDefinition>> ListRolesAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<RoleRow>(
            """
            SELECT r.*,
                   (SELECT COUNT(1) FROM user_roles ur WHERE ur.role_id = r.id) AS assigned_user_count
            FROM roles r
            ORDER BY r.is_system_preset DESC, r.id ASC;
            """);
        return rows.Select(MapRow).ToList();
    }

    public async Task<RoleDefinition?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await conn.QuerySingleOrDefaultAsync<RoleRow>(
            """
            SELECT r.*,
                   (SELECT COUNT(1) FROM user_roles ur WHERE ur.role_id = r.id) AS assigned_user_count
            FROM roles r
            WHERE r.id = @Id;
            """,
            new { Id = id });
        return row is null ? null : MapRow(row);
    }

    public async Task<RoleDefinition?> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await conn.QuerySingleOrDefaultAsync<RoleRow>(
            """
            SELECT r.*,
                   (SELECT COUNT(1) FROM user_roles ur WHERE ur.role_id = r.id) AS assigned_user_count
            FROM roles r
            WHERE r.key = @Key;
            """,
            new { Key = key });
        return row is null ? null : MapRow(row);
    }

    public async Task<RoleDefinition> CreateCustomRoleAsync(UpsertCustomRoleRequest request, CancellationToken ct = default)
    {
        ValidateRequest(request);
        var key = NormalizeCustomKey(request.Key, request.Name);
        var scopes = NormalizeScopes(request.Scopes);
        var libMode = NormalizeLibraryMode(request.LibraryAccessMode);
        var libIds = request.AllowedLibraryIds?.Distinct().OrderBy(x => x).ToArray() ?? [];
        var now = DateTimeOffset.UtcNow.ToString("O");

        var existing = await GetByKeyAsync(key, ct);
        if (existing is not null)
        {
            throw new InvalidOperationException($"A role with key '{key}' already exists.");
        }

        var id = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO roles (
                    key, name, description, color, is_system_preset, is_immutable,
                    scopes_json, library_access_mode, allowed_library_ids_json,
                    auto_approve_requests, movie_request_quota, tv_season_request_quota,
                    music_book_request_quota, cleanerr_exclusion_limit, created_at, updated_at
                ) VALUES (
                    @Key, @Name, @Description, @Color, 0, 0,
                    @ScopesJson, @LibMode, @LibIdsJson,
                    @AutoApprove, @MovieQuota, @TvQuota,
                    @MusicBookQuota, @CleanerrLimit, @Now, @Now
                );
                SELECT last_insert_rowid();
                """,
                new
                {
                    Key = key,
                    Name = request.Name.Trim(),
                    Description = request.Description.Trim(),
                    Color = string.IsNullOrWhiteSpace(request.Color) ? "#3b82f6" : request.Color.Trim(),
                    ScopesJson = JsonSerializer.Serialize(scopes, JsonOpts),
                    LibMode = libMode,
                    LibIdsJson = JsonSerializer.Serialize(libIds, JsonOpts),
                    AutoApprove = request.AutoApproveRequests ? 1 : 0,
                    MovieQuota = request.MovieRequestQuota,
                    TvQuota = request.TvSeasonRequestQuota,
                    MusicBookQuota = request.MusicBookRequestQuota,
                    CleanerrLimit = request.CleanerrExclusionLimit,
                    Now = now,
                },
                transaction: tx),
            ct);

        return (await GetByIdAsync(id, ct))!;
    }

    public async Task<RoleDefinition> CloneRoleAsync(
        long sourceRoleId,
        string newName,
        string? newKey = null,
        CancellationToken ct = default)
    {
        var source = await GetByIdAsync(sourceRoleId, ct)
            ?? throw new KeyNotFoundException($"Source role #{sourceRoleId} not found.");

        return await CreateCustomRoleAsync(
            new UpsertCustomRoleRequest(
                Name: newName,
                Key: newKey,
                Description: $"Cloned from '{source.Name}' — {source.Description}",
                Color: source.Color,
                Scopes: source.Scopes,
                LibraryAccessMode: source.LibraryAccessMode,
                AllowedLibraryIds: source.AllowedLibraryIds,
                AutoApproveRequests: source.AutoApproveRequests,
                MovieRequestQuota: source.MovieRequestQuota,
                TvSeasonRequestQuota: source.TvSeasonRequestQuota,
                MusicBookRequestQuota: source.MusicBookRequestQuota,
                CleanerrExclusionLimit: source.CleanerrExclusionLimit),
            ct);
    }

    public async Task<RoleDefinition> UpdateRoleAsync(
        long id,
        UpsertCustomRoleRequest request,
        CancellationToken ct = default)
    {
        ValidateRequest(request);
        var current = await GetByIdAsync(id, ct)
            ?? throw new KeyNotFoundException($"Role #{id} not found.");

        if (current.IsImmutable)
        {
            throw new InvalidOperationException("The built-in Admin (system:owner) role is immutable to prevent lock-out.");
        }

        var scopes = NormalizeScopes(request.Scopes);
        var libMode = NormalizeLibraryMode(request.LibraryAccessMode);
        var libIds = request.AllowedLibraryIds?.Distinct().OrderBy(x => x).ToArray() ?? [];
        var now = DateTimeOffset.UtcNow.ToString("O");

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                UPDATE roles
                SET name                     = @Name,
                    description              = @Description,
                    color                    = @Color,
                    scopes_json              = @ScopesJson,
                    library_access_mode      = @LibMode,
                    allowed_library_ids_json = @LibIdsJson,
                    auto_approve_requests    = @AutoApprove,
                    movie_request_quota      = @MovieQuota,
                    tv_season_request_quota  = @TvQuota,
                    music_book_request_quota = @MusicBookQuota,
                    cleanerr_exclusion_limit = @CleanerrLimit,
                    updated_at               = @Now
                WHERE id = @Id;
                """,
                new
                {
                    Id = id,
                    Name = request.Name.Trim(),
                    Description = request.Description.Trim(),
                    Color = string.IsNullOrWhiteSpace(request.Color) ? current.Color : request.Color.Trim(),
                    ScopesJson = JsonSerializer.Serialize(scopes, JsonOpts),
                    LibMode = libMode,
                    LibIdsJson = JsonSerializer.Serialize(libIds, JsonOpts),
                    AutoApprove = request.AutoApproveRequests ? 1 : 0,
                    MovieQuota = request.MovieRequestQuota,
                    TvQuota = request.TvSeasonRequestQuota,
                    MusicBookQuota = request.MusicBookRequestQuota,
                    CleanerrLimit = request.CleanerrExclusionLimit,
                    Now = now,
                },
                transaction: tx),
            ct);

        return (await GetByIdAsync(id, ct))!;
    }

    public async Task DeleteCustomRoleAsync(long id, CancellationToken ct = default)
    {
        var current = await GetByIdAsync(id, ct)
            ?? throw new KeyNotFoundException($"Role #{id} not found.");

        if (current.IsSystemPreset || current.IsImmutable)
        {
            throw new InvalidOperationException($"Built-in preset role '{current.Name}' cannot be deleted.");
        }

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync("DELETE FROM roles WHERE id = @Id;", new { Id = id }, transaction: tx),
            ct);
    }

    private static void ValidateRequest(UpsertCustomRoleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Role name is required.");
        }

        foreach (var scope in request.Scopes)
        {
            if (!PermissionScopes.AllValidScopes.Contains(scope))
            {
                throw new ArgumentException($"Unknown permission scope '{scope}'.");
            }
        }
    }

    private static string NormalizeCustomKey(string? explicitKey, string name)
    {
        var raw = string.IsNullOrWhiteSpace(explicitKey)
            ? $"custom:{name.Trim().ToLowerInvariant().Replace(' ', '-')}"
            : explicitKey.Trim().ToLowerInvariant();
        var cleaned = SlugSanitizer().Replace(raw, "-").Trim('-');
        if (!cleaned.Contains(':'))
        {
            cleaned = $"custom:{cleaned}";
        }
        return cleaned;
    }

    private static string[] NormalizeScopes(IReadOnlyList<string> scopes) =>
        scopes.Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeLibraryMode(string mode) => mode switch
    {
        "all_libraries" => "all_libraries",
        "explicit_libraries" => "explicit_libraries",
        _ => "inherit_from_plex",
    };

    private static RoleDefinition MapRow(RoleRow row) =>
        new(
            Id: row.Id,
            Key: row.Key,
            Name: row.Name,
            Description: row.Description,
            Color: row.Color,
            IsSystemPreset: row.IsSystemPreset != 0,
            IsImmutable: row.IsImmutable != 0,
            // Stored roles can still carry scopes of modules this build doesn't ship (e.g. the split-out
            // Cleanerr/compression scopes); hide them so they are neither granted nor rejected on save.
            Scopes: (JsonSerializer.Deserialize<string[]>(row.ScopesJson, JsonOpts) ?? [])
                .Where(PermissionScopes.AllValidScopes.Contains)
                .ToArray(),
            LibraryAccessMode: row.LibraryAccessMode,
            AllowedLibraryIds: JsonSerializer.Deserialize<long[]>(row.AllowedLibraryIdsJson, JsonOpts) ?? [],
            AutoApproveRequests: row.AutoApproveRequests != 0,
            MovieRequestQuota: row.MovieRequestQuota,
            TvSeasonRequestQuota: row.TvSeasonRequestQuota,
            MusicBookRequestQuota: row.MusicBookRequestQuota,
            AssignedUserCount: row.AssignedUserCount,
            CreatedAt: row.CreatedAt,
            UpdatedAt: row.UpdatedAt,
            CleanerrExclusionLimit: row.CleanerrExclusionLimit);

    private sealed class RoleRow
    {
        public long Id { get; init; }
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string Color { get; init; } = "#3b82f6";
        public int IsSystemPreset { get; init; }
        public int IsImmutable { get; init; }
        public string ScopesJson { get; init; } = "[]";
        public string LibraryAccessMode { get; init; } = "inherit_from_plex";
        public string AllowedLibraryIdsJson { get; init; } = "[]";
        public int AutoApproveRequests { get; init; }
        public int? MovieRequestQuota { get; init; }
        public int? TvSeasonRequestQuota { get; init; }
        public int? MusicBookRequestQuota { get; init; }
        public int? CleanerrExclusionLimit { get; init; }
        public int AssignedUserCount { get; init; }
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }
}
