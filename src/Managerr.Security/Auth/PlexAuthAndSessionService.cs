using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Managerr.Common.Crypto;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Security.Plex;
using Managerr.Security.Rbac;
using Managerr.Security.Users;

namespace Managerr.Security.Auth;

public static class GeneralSettingsKeys
{
    public const string InstanceName = "system.general.instance_name";
    public const string InstanceApiKeyEnc = "system.general.api_key";
    public const string InstanceApiKeyHash = "system.general.api_key_hash";
}

public static class PlexSettingsKeys
{
    public const string BoundMachineId = "plex.bound_machine_id";
    public const string BoundServerName = "plex.bound_server_name";
    public const string BoundServerUrl = "plex.bound_server_url";
    public const string OwnerAdminTokenEnc = "plex.owner_admin_token";
}

public sealed record IssuedSession(
    string SessionId,
    string RawSessionToken,
    string CsrfToken,
    DateTimeOffset ExpiresAtUtc,
    PlexUserSummary User);

public sealed record AuthenticatedPrincipal(
    PlexUserSummary? User,
    string? ApiKeyPrefix,
    string? CsrfToken,
    IReadOnlySet<string> Scopes,
    bool IsCookieSession);

public sealed record CreatedApiKeyResult(
    long Id,
    string Name,
    string PublicPrefix,
    string RawTokenOnce,
    string MaskedHint,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<long> AllowedLibraryIds,
    IReadOnlyList<string> AllowedCidrs,
    string? ExpiresAt);

public sealed record ApiKeySummary(
    long Id,
    long OwnerUserId,
    string Name,
    string PublicPrefix,
    string MaskedHint,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<long> AllowedLibraryIds,
    IReadOnlyList<string> AllowedCidrs,
    string? ExpiresAt,
    string? LastUsedAt,
    string? RevokedAt,
    string CreatedAt);

public sealed record PlexAuthOutcome(
    bool Claimed,
    bool RequiresServerSelection,
    IReadOnlyList<PlexServerResource> AvailableOwnedServers,
    IssuedSession? Session,
    string? DeniedReason);

public interface IPlexAuthAndSessionService
{
    Task<(bool IsClaimed, string? BoundServerName, string? BoundMachineId)> GetSetupStatusAsync(CancellationToken ct = default);
    Task<PlexPinInfo> StartPlexPinAsync(string? forwardUrl = null, CancellationToken ct = default);
    Task<PlexAuthOutcome> CompletePlexPinAsync(
        long pinId,
        string? expectedCode,
        string? selectedMachineId,
        string? userAgent,
        string? ipAddress,
        CancellationToken ct = default);
    Task<IssuedSession> SwitchHomeUserAsync(
        long currentOwnerUserId,
        string targetHomeUserUuid,
        string? pin,
        string? userAgent,
        string? ipAddress,
        CancellationToken ct = default);
    Task<int> SyncPlexServerUsersAsync(CancellationToken ct = default);
    Task<IssuedSession> CreateSessionForUserAsync(PlexUserSummary user, string? userAgent, string? ipAddress, CancellationToken ct = default);
    Task<AuthenticatedPrincipal?> ValidateSessionTokenAsync(string rawToken, bool isCookie, CancellationToken ct = default);
    Task RevokeSessionAsync(string rawToken, CancellationToken ct = default);
    Task RevokeUserSessionsAsync(long userId, string? exceptRawToken = null, CancellationToken ct = default);
    Task<CreatedApiKeyResult> CreateApiKeyAsync(
        long ownerUserId,
        string name,
        IReadOnlyList<string> scopes,
        IReadOnlyList<long>? allowedLibraryIds = null,
        IReadOnlyList<string>? allowedCidrs = null,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default);
    Task<IReadOnlyList<ApiKeySummary>> ListApiKeysAsync(long? ownerUserId = null, CancellationToken ct = default);
    Task RevokeApiKeyAsync(long id, CancellationToken ct = default);
    Task<AuthenticatedPrincipal?> ValidateApiKeyAsync(string rawApiKey, IPAddress? callerIp, CancellationToken ct = default);
    Task<string> GetInstanceApiKeyAsync(CancellationToken ct = default);
    Task<string> ResetInstanceApiKeyAsync(CancellationToken ct = default);
}

public sealed class PlexAuthAndSessionService(
    ISqliteDbFactory dbFactory,
    IAppSettingsService appSettings,
    IPlexApiClient plexApi,
    IPlexUserService userService,
    ISecretProtector secretProtector,
    IAuditLogService auditLog) : IPlexAuthAndSessionService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly byte[] _tokenHmacKey = secretProtector.DeriveSubkey("managerr.auth.token.hmac.v1");
    private readonly SemaphoreSlim _instanceApiKeyLock = new(1, 1);

    public async Task<(bool IsClaimed, string? BoundServerName, string? BoundMachineId)> GetSetupStatusAsync(CancellationToken ct = default)
    {
        var hasOwner = await userService.HasServerOwnerAsync(ct);
        var serverName = await appSettings.GetAsync(PlexSettingsKeys.BoundServerName, ct);
        var machineId = await appSettings.GetAsync(PlexSettingsKeys.BoundMachineId, ct);
        return (hasOwner, serverName, machineId);
    }

    public Task<PlexPinInfo> StartPlexPinAsync(string? forwardUrl = null, CancellationToken ct = default) =>
        plexApi.CreateStrongPinAsync(forwardUrl, ct);

    public async Task<PlexAuthOutcome> CompletePlexPinAsync(
        long pinId,
        string? expectedCode,
        string? selectedMachineId,
        string? userAgent,
        string? ipAddress,
        CancellationToken ct = default)
    {
        var poll = await plexApi.CheckPinAsync(pinId, expectedCode, ct);
        if (!poll.Claimed || string.IsNullOrWhiteSpace(poll.AuthToken) || poll.Account is null)
        {
            return new PlexAuthOutcome(false, false, [], null, null);
        }

        var account = poll.Account;
        var (isClaimed, _, boundMachineId) = await GetSetupStatusAsync(ct);
        var boundToken = await appSettings.GetSecretAsync(PlexSettingsKeys.OwnerAdminTokenEnc, ct)
            ?? await appSettings.GetAsync(PlexSettingsKeys.OwnerAdminTokenEnc, ct);
        var isPlexBound = !string.IsNullOrWhiteSpace(boundMachineId) && !string.IsNullOrWhiteSpace(boundToken);
        var hasOwner = await userService.HasServerOwnerAsync(ct);

        // Case 1: First-Run Server Owner Bootstrap or Initial Plex Server Binding
        if (!isPlexBound && (!hasOwner || !string.IsNullOrWhiteSpace(boundMachineId)))
        {
            var ownedServers = poll.Servers.Where(s => s.Owned).ToList();
            if (ownedServers.Count == 0)
            {
                return new PlexAuthOutcome(
                    Claimed: true,
                    RequiresServerSelection: false,
                    AvailableOwnedServers: [],
                    Session: null,
                    DeniedReason: "Initial setup must be claimed by a Plex account that owns a Plex Media Server.");
            }

            PlexServerResource? chosen = null;
            if (!string.IsNullOrWhiteSpace(selectedMachineId))
            {
                chosen = ownedServers.FirstOrDefault(s =>
                    string.Equals(s.MachineIdentifier, selectedMachineId, StringComparison.OrdinalIgnoreCase));
            }
            else if (!string.IsNullOrWhiteSpace(boundMachineId))
            {
                chosen = ownedServers.FirstOrDefault(s =>
                    string.Equals(s.MachineIdentifier, boundMachineId, StringComparison.OrdinalIgnoreCase));
            }

            if (chosen is null && string.IsNullOrWhiteSpace(selectedMachineId))
            {
                if (ownedServers.Count == 1)
                {
                    chosen = ownedServers[0];
                }
                else
                {
                    return new PlexAuthOutcome(
                        Claimed: true,
                        RequiresServerSelection: true,
                        AvailableOwnedServers: ownedServers,
                        Session: null,
                        DeniedReason: null);
                }
            }

            if (chosen is null)
            {
                return new PlexAuthOutcome(true, false, ownedServers, null, "Selected Plex Media Server was not found among owned servers.");
            }

            var preferredConn = chosen.Connections.FirstOrDefault(c => c.Local)?.DirectUrl
                ?? chosen.Connections.FirstOrDefault()?.Uri
                ?? "";

            var existingBoundUrl = await appSettings.GetAsync(PlexSettingsKeys.BoundServerUrl, ct);
            var preserveExistingUrl = !string.IsNullOrWhiteSpace(existingBoundUrl) &&
                (string.IsNullOrWhiteSpace(boundMachineId) ||
                 string.Equals(chosen.MachineIdentifier, boundMachineId, StringComparison.OrdinalIgnoreCase));
            var boundUrl = preserveExistingUrl
                ? existingBoundUrl!
                : preferredConn;

            await appSettings.SetAsync(PlexSettingsKeys.BoundMachineId, chosen.MachineIdentifier, isSecret: false, ct);
            await appSettings.SetAsync(PlexSettingsKeys.BoundServerName, chosen.Name, isSecret: false, ct);
            await appSettings.SetAsync(PlexSettingsKeys.BoundServerUrl, boundUrl, isSecret: false, ct);
            await appSettings.SetAsync(PlexSettingsKeys.OwnerAdminTokenEnc, poll.AuthToken, isSecret: true, ct);

            var ownerUser = await userService.UpsertFromPlexAsync(
                new UpsertPlexUserCommand(
                    PlexUserId: account.PlexUserId,
                    PlexUuid: account.Uuid,
                    Username: account.Username,
                    Email: account.Email,
                    AvatarUrl: account.AvatarUrl,
                    IsServerOwner: true,
                    IsHomeUser: false,
                    IsActive: true,
                    SharedPlexSectionIds: ["*"],
                    PreferredAudioLang: account.DefaultAudioLanguage,
                    PreferredSubLang: account.DefaultSubtitleLanguage,
                    RecordLoginTimestamp: true),
                ct);

            // Best-effort initial sync of shared Plex Friends & Home Users
            try
            {
                await SyncPlexServerUsersAsync(ct);
            }
            catch
            {
                // Non-fatal during bootstrap
            }

            var ownerSession = await CreateSessionForUserAsync(ownerUser, userAgent, ipAddress, ct);
            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "auth.plex.owner_claimed",
                ResourceType: "plex_server",
                ResourceId: chosen.MachineIdentifier,
                Details: new { chosen.Name, PreferredUrl = preferredConn },
                ActorUserId: ownerUser.Id,
                ActorPlexUserId: ownerUser.PlexUserId,
                ActorUsername: ownerUser.Username,
                IpAddress: ipAddress), ct);

            return new PlexAuthOutcome(true, false, [], ownerSession, null);
        }

        // Case 2: Subsequent Sign-In — Enforce Strict PMS Server-Membership Gate!
        var existingUser = await userService.GetByPlexUserIdAsync(account.PlexUserId, ct);
        if (existingUser is { IsServerOwner: true })
        {
            await appSettings.SetAsync(PlexSettingsKeys.OwnerAdminTokenEnc, poll.AuthToken, isSecret: true, ct);
            var refreshedOwner = await userService.UpsertFromPlexAsync(
                new UpsertPlexUserCommand(
                    PlexUserId: account.PlexUserId,
                    PlexUuid: account.Uuid,
                    Username: account.Username,
                    Email: account.Email,
                    AvatarUrl: account.AvatarUrl,
                    IsServerOwner: true,
                    IsHomeUser: false,
                    IsActive: true,
                    SharedPlexSectionIds: ["*"],
                    PreferredAudioLang: account.DefaultAudioLanguage,
                    PreferredSubLang: account.DefaultSubtitleLanguage,
                    RecordLoginTimestamp: true),
                ct);
            var session = await CreateSessionForUserAsync(refreshedOwner, userAgent, ipAddress, ct);
            return new PlexAuthOutcome(true, false, [], session, null);
        }

        // Verify against PMS shared users list using stored Owner Admin Token (or poll.Servers match)
        var ownerToken = await appSettings.GetSecretAsync(PlexSettingsKeys.OwnerAdminTokenEnc, ct);
        PlexSharedUserEntry? sharedMatch = null;
        if (!string.IsNullOrWhiteSpace(ownerToken) && !string.IsNullOrWhiteSpace(boundMachineId))
        {
            var sharedList = await plexApi.GetSharedServerUsersAsync(ownerToken, boundMachineId, ct);
            sharedMatch = sharedList.FirstOrDefault(u => u.PlexUserId == account.PlexUserId);
        }

        var hasDirectServerResource = !string.IsNullOrWhiteSpace(boundMachineId) &&
            poll.Servers.Any(s => string.Equals(s.MachineIdentifier, boundMachineId, StringComparison.OrdinalIgnoreCase));

        if (sharedMatch is null && !hasDirectServerResource)
        {
            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "auth.plex.denied_not_shared",
                ResourceType: "user",
                ResourceId: account.PlexUserId.ToString(),
                Details: new { account.Username, BoundMachineId = boundMachineId },
                ActorPlexUserId: account.PlexUserId,
                ActorUsername: account.Username,
                IpAddress: ipAddress), ct);

            return new PlexAuthOutcome(
                Claimed: true,
                RequiresServerSelection: false,
                AvailableOwnedServers: [],
                Session: null,
                DeniedReason: "Your Plex account does not have shared access to this Plex Media Server.");
        }

        var sections = sharedMatch is { AllLibrariesShared: true }
            ? ["*"]
            : (sharedMatch?.SharedSectionIds ?? existingUser?.SharedPlexSectionIds ?? []);

        var syncedFriend = await userService.UpsertFromPlexAsync(
            new UpsertPlexUserCommand(
                PlexUserId: account.PlexUserId,
                PlexUuid: account.Uuid,
                Username: account.Username,
                Email: account.Email ?? sharedMatch?.Email,
                AvatarUrl: account.AvatarUrl ?? sharedMatch?.AvatarUrl,
                IsServerOwner: false,
                IsHomeUser: sharedMatch?.IsHomeUser ?? false,
                IsActive: true,
                SharedPlexSectionIds: sections,
                PreferredAudioLang: account.DefaultAudioLanguage,
                PreferredSubLang: account.DefaultSubtitleLanguage,
                RecordLoginTimestamp: true),
            ct);

        var friendSession = await CreateSessionForUserAsync(syncedFriend, userAgent, ipAddress, ct);
        await auditLog.RecordAsync(new AuditWriteRequest(
            Action: "auth.plex.login",
            ResourceType: "user",
            ResourceId: syncedFriend.Id.ToString(),
            ActorUserId: syncedFriend.Id,
            ActorPlexUserId: syncedFriend.PlexUserId,
            ActorUsername: syncedFriend.Username,
            IpAddress: ipAddress), ct);

        return new PlexAuthOutcome(true, false, [], friendSession, null);
    }

    public async Task<IssuedSession> SwitchHomeUserAsync(
        long currentOwnerUserId,
        string targetHomeUserUuid,
        string? pin,
        string? userAgent,
        string? ipAddress,
        CancellationToken ct = default)
    {
        var ownerToken = await appSettings.GetSecretAsync(PlexSettingsKeys.OwnerAdminTokenEnc, ct)
            ?? throw new InvalidOperationException("Plex server admin token is not configured.");
        var boundMachineId = await appSettings.GetAsync(PlexSettingsKeys.BoundMachineId, ct)
            ?? throw new InvalidOperationException("No Plex server bound.");

        var switchedToken = await plexApi.SwitchHomeUserAsync(ownerToken, targetHomeUserUuid, pin, ct);
        var profile = await plexApi.GetUserProfileAsync(switchedToken, ct);

        var sharedUsers = await plexApi.GetSharedServerUsersAsync(ownerToken, boundMachineId, ct);
        var homeEntry = sharedUsers.FirstOrDefault(u => u.PlexUserId == profile.PlexUserId);
        var isOwner = (await userService.GetByPlexUserIdAsync(profile.PlexUserId, ct))?.IsServerOwner == true;

        if (!isOwner && homeEntry is null)
        {
            throw new UnauthorizedAccessException("Target Plex Home user does not have access to the bound Plex server.");
        }

        var user = await userService.UpsertFromPlexAsync(
            new UpsertPlexUserCommand(
                PlexUserId: profile.PlexUserId,
                PlexUuid: profile.Uuid,
                Username: profile.Username,
                Email: profile.Email,
                AvatarUrl: profile.AvatarUrl,
                IsServerOwner: isOwner,
                IsHomeUser: !isOwner,
                IsActive: true,
                SharedPlexSectionIds: isOwner || homeEntry?.AllLibrariesShared == true ? ["*"] : (homeEntry?.SharedSectionIds ?? []),
                PreferredAudioLang: profile.DefaultAudioLanguage,
                PreferredSubLang: profile.DefaultSubtitleLanguage,
                RecordLoginTimestamp: true),
            ct);

        return await CreateSessionForUserAsync(user, userAgent, ipAddress, ct);
    }

    public async Task<int> SyncPlexServerUsersAsync(CancellationToken ct = default)
    {
        var ownerToken = await appSettings.GetSecretAsync(PlexSettingsKeys.OwnerAdminTokenEnc, ct)
            ?? throw new InvalidOperationException("Plex server owner token is not configured yet.");
        var boundMachineId = await appSettings.GetAsync(PlexSettingsKeys.BoundMachineId, ct)
            ?? throw new InvalidOperationException("No Plex Media Server bound yet.");

        var sharedUsers = await plexApi.GetSharedServerUsersAsync(ownerToken, boundMachineId, ct);
        var activeSharedIds = sharedUsers.Select(u => u.PlexUserId).ToHashSet();

        foreach (var entry in sharedUsers)
        {
            await userService.UpsertFromPlexAsync(
                new UpsertPlexUserCommand(
                    PlexUserId: entry.PlexUserId,
                    PlexUuid: entry.Uuid,
                    Username: entry.Username,
                    Email: entry.Email,
                    AvatarUrl: entry.AvatarUrl,
                    IsServerOwner: false,
                    IsHomeUser: entry.IsHomeUser,
                    IsActive: true,
                    SharedPlexSectionIds: entry.AllLibrariesShared ? ["*"] : entry.SharedSectionIds),
                ct);
        }

        // Deactivate any previously imported non-owner Plex users who were unshared on PMS
        var localUsers = await userService.ListUsersAsync(ct);
        foreach (var local in localUsers.Where(u => u.PlexUserId > 0 && !u.IsServerOwner && u.IsActive && !activeSharedIds.Contains(u.PlexUserId)))
        {
            await userService.UpdatePreferencesAndLinksAsync(
                local.Id,
                local.PreferredAudioLang,
                local.PreferredSubLang,
                local.DiscordUserId,
                local.DiscordUsername,
                isActive: false,
                ct);
        }

        return sharedUsers.Count;
    }

    public async Task<IssuedSession> CreateSessionForUserAsync(
        PlexUserSummary user,
        string? userAgent,
        string? ipAddress,
        CancellationToken ct = default)
    {
        if (!user.IsActive)
        {
            throw new UnauthorizedAccessException("User account is inactive.");
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var rawToken = $"mgr_sess_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))}";
        var csrfToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var tokenHash = HashToken(rawToken);
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddDays(14);

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                INSERT INTO user_sessions (id, user_id, token_hash, csrf_token, user_agent, ip_address, expires_at, created_at)
                VALUES (@Id, @UserId, @TokenHash, @CsrfToken, @UserAgent, @IpAddress, @ExpiresAt, @CreatedAt);
                """,
                new
                {
                    Id = sessionId,
                    UserId = user.Id,
                    TokenHash = tokenHash,
                    CsrfToken = csrfToken,
                    UserAgent = userAgent,
                    IpAddress = ipAddress,
                    ExpiresAt = expiresAt.ToString("O"),
                    CreatedAt = now.ToString("O"),
                },
                transaction: tx),
            ct);

        return new IssuedSession(sessionId, rawToken, csrfToken, expiresAt, user);
    }

    public async Task<AuthenticatedPrincipal?> ValidateSessionTokenAsync(
        string rawToken,
        bool isCookie,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return null;
        }

        var tokenHash = HashToken(rawToken);
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var sess = await conn.QuerySingleOrDefaultAsync<(long UserId, string CsrfToken, string ExpiresAt, string? RevokedAt)>(
            """
            SELECT user_id AS UserId, csrf_token AS CsrfToken, expires_at AS ExpiresAt, revoked_at AS RevokedAt
            FROM user_sessions
            WHERE token_hash = @TokenHash;
            """,
            new { TokenHash = tokenHash });

        if (sess == default || sess.RevokedAt is not null)
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(sess.ExpiresAt, out var exp) || exp <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        var user = await userService.GetByIdAsync(sess.UserId, ct);
        if (user is null || !user.IsActive)
        {
            return null;
        }

        return new AuthenticatedPrincipal(
            User: user,
            ApiKeyPrefix: null,
            CsrfToken: sess.CsrfToken,
            Scopes: user.EffectivePermissions.Scopes.ToHashSet(StringComparer.Ordinal),
            IsCookieSession: isCookie);
    }

    public Task RevokeSessionAsync(string rawToken, CancellationToken ct = default)
    {
        var tokenHash = HashToken(rawToken);
        return dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                "UPDATE user_sessions SET revoked_at = @Now WHERE token_hash = @TokenHash AND revoked_at IS NULL;",
                new { TokenHash = tokenHash, Now = DateTimeOffset.UtcNow.ToString("O") },
                transaction: tx),
            ct);
    }

    public Task RevokeUserSessionsAsync(long userId, string? exceptRawToken = null, CancellationToken ct = default)
    {
        var exceptHash = !string.IsNullOrWhiteSpace(exceptRawToken) ? HashToken(exceptRawToken) : null;
        return dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                UPDATE user_sessions
                SET revoked_at = @Now
                WHERE user_id = @UserId
                  AND revoked_at IS NULL
                  AND (@ExceptHash IS NULL OR token_hash != @ExceptHash);
                """,
                new { UserId = userId, ExceptHash = exceptHash, Now = DateTimeOffset.UtcNow.ToString("O") },
                transaction: tx),
            ct);
    }

    public async Task<CreatedApiKeyResult> CreateApiKeyAsync(
        long ownerUserId,
        string name,
        IReadOnlyList<string> scopes,
        IReadOnlyList<long>? allowedLibraryIds = null,
        IReadOnlyList<string>? allowedCidrs = null,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("API key name is required.");
        }

        var owner = await userService.GetByIdAsync(ownerUserId, ct)
            ?? throw new KeyNotFoundException($"Owner user #{ownerUserId} not found.");

        var ownerScopes = owner.EffectivePermissions.Scopes.ToHashSet(StringComparer.Ordinal);
        var cleanScopes = scopes.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        if (cleanScopes.Length == 0)
        {
            throw new ArgumentException("At least one scope is required for a scoped API key.");
        }

        foreach (var s in cleanScopes)
        {
            if (!PermissionScopes.AllValidScopes.Contains(s))
            {
                throw new ArgumentException($"Invalid scope '{s}'.");
            }
            if (!ownerScopes.Contains(s))
            {
                throw new UnauthorizedAccessException($"Cannot issue an API key with scope '{s}' that the owner does not hold.");
            }
        }

        var cidrs = allowedCidrs?.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToArray() ?? [];
        foreach (var cidr in cidrs)
        {
            if (!IPNetwork.TryParse(cidr, out _))
            {
                throw new ArgumentException($"Invalid CIDR notation '{cidr}'.");
            }
        }

        var pubId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var secretPart = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var publicPrefix = $"mgr_live_{pubId}";
        var rawToken = $"{publicPrefix}_{secretPart}";
        var maskedHint = $"{publicPrefix}_****{secretPart[^4..]}";
        var secretHash = HashToken(rawToken);
        var libIds = allowedLibraryIds?.Distinct().OrderBy(x => x).ToArray() ?? [];
        var expStr = expiresAt?.ToString("O");
        var now = DateTimeOffset.UtcNow.ToString("O");

        var id = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO api_keys (
                    owner_user_id, name, public_prefix, secret_hash, masked_hint,
                    scopes_json, allowed_library_ids_json, allowed_cidrs_json, expires_at, created_at
                ) VALUES (
                    @OwnerId, @Name, @Prefix, @SecretHash, @MaskedHint,
                    @ScopesJson, @LibIdsJson, @CidrsJson, @ExpiresAt, @Now
                );
                SELECT last_insert_rowid();
                """,
                new
                {
                    OwnerId = ownerUserId,
                    Name = name.Trim(),
                    Prefix = publicPrefix,
                    SecretHash = secretHash,
                    MaskedHint = maskedHint,
                    ScopesJson = JsonSerializer.Serialize(cleanScopes, JsonOpts),
                    LibIdsJson = JsonSerializer.Serialize(libIds, JsonOpts),
                    CidrsJson = JsonSerializer.Serialize(cidrs, JsonOpts),
                    ExpiresAt = expStr,
                    Now = now,
                },
                transaction: tx),
            ct);

        return new CreatedApiKeyResult(id, name.Trim(), publicPrefix, rawToken, maskedHint, cleanScopes, libIds, cidrs, expStr);
    }

    public async Task<IReadOnlyList<ApiKeySummary>> ListApiKeysAsync(long? ownerUserId = null, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var sql = ownerUserId.HasValue
            ? "SELECT * FROM api_keys WHERE owner_user_id = @OwnerId ORDER BY id DESC;"
            : "SELECT * FROM api_keys ORDER BY id DESC;";
        var rows = await conn.QueryAsync<ApiKeyRow>(sql, new { OwnerId = ownerUserId });
        return rows.Select(r => new ApiKeySummary(
            Id: r.Id,
            OwnerUserId: r.OwnerUserId,
            Name: r.Name,
            PublicPrefix: r.PublicPrefix,
            MaskedHint: r.MaskedHint,
            Scopes: JsonSerializer.Deserialize<string[]>(r.ScopesJson, JsonOpts) ?? [],
            AllowedLibraryIds: JsonSerializer.Deserialize<long[]>(r.AllowedLibraryIdsJson, JsonOpts) ?? [],
            AllowedCidrs: JsonSerializer.Deserialize<string[]>(r.AllowedCidrsJson, JsonOpts) ?? [],
            ExpiresAt: r.ExpiresAt,
            LastUsedAt: r.LastUsedAt,
            RevokedAt: r.RevokedAt,
            CreatedAt: r.CreatedAt)).ToList();
    }

    public Task RevokeApiKeyAsync(long id, CancellationToken ct = default) =>
        dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                "UPDATE api_keys SET revoked_at = @Now WHERE id = @Id AND revoked_at IS NULL;",
                new { Id = id, Now = DateTimeOffset.UtcNow.ToString("O") },
                transaction: tx),
            ct);

    public async Task<string> GetInstanceApiKeyAsync(CancellationToken ct = default)
    {
        var existing = await appSettings.GetSecretAsync(GeneralSettingsKeys.InstanceApiKeyEnc, ct);
        var existingHash = await appSettings.GetAsync(GeneralSettingsKeys.InstanceApiKeyHash, ct);

        if (!string.IsNullOrWhiteSpace(existing) && IsLowerHex32(existing))
        {
            var expectedHash = HashToken(existing);
            if (string.Equals(existingHash, expectedHash, StringComparison.Ordinal))
            {
                return existing;
            }
        }

        await _instanceApiKeyLock.WaitAsync(ct);
        try
        {
            existing = await appSettings.GetSecretAsync(GeneralSettingsKeys.InstanceApiKeyEnc, ct);
            if (!string.IsNullOrWhiteSpace(existing) && IsLowerHex32(existing))
            {
                var expectedHash = HashToken(existing);
                var curHash = await appSettings.GetAsync(GeneralSettingsKeys.InstanceApiKeyHash, ct);
                if (!string.Equals(curHash, expectedHash, StringComparison.Ordinal))
                {
                    await appSettings.SetAsync(GeneralSettingsKeys.InstanceApiKeyHash, expectedHash, isSecret: false, ct);
                }
                return existing;
            }

            var rawKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var encKey = secretProtector.Protect(rawKey);
            var hash = HashToken(rawKey);
            var now = DateTimeOffset.UtcNow.ToString("O");

            const string sql = """
                INSERT INTO app_settings (key, value, is_secret, updated_at)
                VALUES (@Key, @Value, @IsSecret, @UpdatedAt)
                ON CONFLICT(key) DO UPDATE SET
                    value      = excluded.value,
                    is_secret  = excluded.is_secret,
                    updated_at = excluded.updated_at;
                """;

            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                async (conn, tx) =>
                {
                    await conn.ExecuteAsync(sql, new { Key = GeneralSettingsKeys.InstanceApiKeyEnc, Value = encKey, IsSecret = 1, UpdatedAt = now }, tx);
                    await conn.ExecuteAsync(sql, new { Key = GeneralSettingsKeys.InstanceApiKeyHash, Value = hash, IsSecret = 0, UpdatedAt = now }, tx);
                },
                ct);

            return rawKey;
        }
        finally
        {
            _instanceApiKeyLock.Release();
        }
    }

    public async Task<string> ResetInstanceApiKeyAsync(CancellationToken ct = default)
    {
        await _instanceApiKeyLock.WaitAsync(ct);
        try
        {
            var previous = await appSettings.GetSecretAsync(GeneralSettingsKeys.InstanceApiKeyEnc, ct);
            string rawKey;
            do
            {
                rawKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            } while (!string.IsNullOrWhiteSpace(previous) && string.Equals(rawKey, previous, StringComparison.Ordinal));

            var encKey = secretProtector.Protect(rawKey);
            var hash = HashToken(rawKey);
            var now = DateTimeOffset.UtcNow.ToString("O");

            const string sql = """
                INSERT INTO app_settings (key, value, is_secret, updated_at)
                VALUES (@Key, @Value, @IsSecret, @UpdatedAt)
                ON CONFLICT(key) DO UPDATE SET
                    value      = excluded.value,
                    is_secret  = excluded.is_secret,
                    updated_at = excluded.updated_at;
                """;

            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                async (conn, tx) =>
                {
                    await conn.ExecuteAsync(sql, new { Key = GeneralSettingsKeys.InstanceApiKeyEnc, Value = encKey, IsSecret = 1, UpdatedAt = now }, tx);
                    await conn.ExecuteAsync(sql, new { Key = GeneralSettingsKeys.InstanceApiKeyHash, Value = hash, IsSecret = 0, UpdatedAt = now }, tx);
                },
                ct);

            return rawKey;
        }
        finally
        {
            _instanceApiKeyLock.Release();
        }
    }

    public async Task<AuthenticatedPrincipal?> ValidateApiKeyAsync(
        string rawApiKey,
        IPAddress? callerIp,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawApiKey))
        {
            return null;
        }

        var trimmed = rawApiKey.Trim();
        if (trimmed.StartsWith("mgr_live_", StringComparison.Ordinal))
        {
            var hash = HashToken(trimmed);
            await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
            var row = await conn.QuerySingleOrDefaultAsync<ApiKeyRow>(
                "SELECT * FROM api_keys WHERE secret_hash = @Hash;",
                new { Hash = hash });

            if (row is null || row.RevokedAt is not null)
            {
                return null;
            }

            if (row.ExpiresAt is not null && DateTimeOffset.TryParse(row.ExpiresAt, out var exp) && exp <= DateTimeOffset.UtcNow)
            {
                return null;
            }

            var cidrs = JsonSerializer.Deserialize<string[]>(row.AllowedCidrsJson, JsonOpts) ?? [];
            if (cidrs.Length > 0)
            {
                if (callerIp is null)
                {
                    return null;
                }
                var normalizedIp = callerIp.IsIPv4MappedToIPv6 ? callerIp.MapToIPv4() : callerIp;
                var matched = cidrs.Any(c => IPNetwork.TryParse(c, out var net) && net.Contains(normalizedIp));
                if (!matched)
                {
                    return null;
                }
            }

            var owner = await userService.GetByIdAsync(row.OwnerUserId, ct);
            if (owner is null || !owner.IsActive)
            {
                return null;
            }

            var keyScopes = JsonSerializer.Deserialize<string[]>(row.ScopesJson, JsonOpts) ?? [];
            var effectiveScopes = keyScopes
                .Intersect(owner.EffectivePermissions.Scopes, StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);

            return new AuthenticatedPrincipal(
                User: owner,
                ApiKeyPrefix: row.PublicPrefix,
                CsrfToken: null,
                Scopes: effectiveScopes,
                IsCookieSession: false);
        }
        else if (IsLowerHex32(trimmed))
        {
            var storedHash = await appSettings.GetAsync(GeneralSettingsKeys.InstanceApiKeyHash, ct);
            if (string.IsNullOrWhiteSpace(storedHash) || storedHash.Length != 64 || !storedHash.All(IsLowerHexChar))
            {
                return null;
            }

            var candidateMac = HMACSHA256.HashData(_tokenHmacKey, Encoding.UTF8.GetBytes(trimmed));
            var storedMac = Convert.FromHexString(storedHash);
            if (!CryptographicOperations.FixedTimeEquals(candidateMac, storedMac))
            {
                return null;
            }

            var users = await userService.ListUsersAsync(ct);
            var adminUser = users.FirstOrDefault(u => u.IsActive && u.IsServerOwner)
                ?? users.FirstOrDefault(u => u.IsActive && u.EffectivePermissions.Scopes.Contains(PermissionScopes.SystemAdmin));
            if (adminUser is null)
            {
                return null;
            }

            return new AuthenticatedPrincipal(
                User: adminUser,
                ApiKeyPrefix: $"general_{trimmed[..8]}",
                CsrfToken: null,
                Scopes: PermissionScopes.AllValidScopes,
                IsCookieSession: false);
        }

        return null;
    }

    private static bool IsLowerHex32(string s) => s.Length == 32 && s.All(IsLowerHexChar);
    private static bool IsLowerHexChar(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');

    private string HashToken(string raw)
    {
        var mac = HMACSHA256.HashData(_tokenHmacKey, Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(mac);
    }

    private sealed class ApiKeyRow
    {
        public long Id { get; init; }
        public long OwnerUserId { get; init; }
        public string Name { get; init; } = "";
        public string PublicPrefix { get; init; } = "";
        public string SecretHash { get; init; } = "";
        public string MaskedHint { get; init; } = "";
        public string ScopesJson { get; init; } = "[]";
        public string AllowedLibraryIdsJson { get; init; } = "[]";
        public string AllowedCidrsJson { get; init; } = "[]";
        public string? ExpiresAt { get; init; }
        public string? LastUsedAt { get; init; }
        public string? RevokedAt { get; init; }
        public string CreatedAt { get; init; } = "";
    }
}
