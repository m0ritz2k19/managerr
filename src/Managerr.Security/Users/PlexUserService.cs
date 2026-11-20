using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Managerr.Datastore;
using Managerr.Security.Rbac;

namespace Managerr.Security.Users;

public sealed record EffectiveUserPermissions(
    bool HasAllLibrariesAccess,
    IReadOnlyList<string> SharedPlexSectionIds,
    IReadOnlyList<long> ExplicitAllowedLibraryIds,
    IReadOnlyList<string> Scopes,
    bool AutoApproveRequests,
    int? MovieRequestQuota,
    int? TvSeasonRequestQuota,
    int? MusicBookRequestQuota,
    int? CleanerrExclusionLimit = null);

public sealed record PlexUserSummary(
    long Id,
    long PlexUserId,
    string PlexUuid,
    string Username,
    string? Email,
    string? AvatarUrl,
    bool IsServerOwner,
    bool IsHomeUser,
    bool IsActive,
    bool HasBreakGlassPassword,
    IReadOnlyList<string> SharedPlexSectionIds,
    string? PreferredAudioLang,
    string? PreferredSubLang,
    string? DiscordUserId,
    string? DiscordUsername,
    long? SeerrUserId,
    IReadOnlyList<RoleDefinition> Roles,
    EffectiveUserPermissions EffectivePermissions,
    string? LastLoginAt,
    string CreatedAt,
    string? DisplayName = null,
    string PasskeyUserId = "",
    IReadOnlyList<string>? Providers = null,
    int PasskeyCount = 0);

public sealed record UpsertPlexUserCommand(
    long PlexUserId,
    string PlexUuid,
    string Username,
    string? Email,
    string? AvatarUrl,
    bool IsServerOwner,
    bool IsHomeUser,
    bool IsActive,
    IReadOnlyList<string> SharedPlexSectionIds,
    string? PreferredAudioLang = null,
    string? PreferredSubLang = null,
    bool RecordLoginTimestamp = false);

public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    public static string GeneratePasskeyUserId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Encode(bytes);
    }
}

public interface IPlexUserService
{
    Task<bool> HasServerOwnerAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PlexUserSummary>> ListUsersAsync(CancellationToken ct = default);
    Task<PlexUserSummary?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<PlexUserSummary?> GetByPlexUserIdAsync(long plexUserId, CancellationToken ct = default);
    Task<PlexUserSummary?> GetByDiscordUserIdAsync(string discordUserId, CancellationToken ct = default);
    Task<PlexUserSummary> UpsertFromPlexAsync(UpsertPlexUserCommand cmd, CancellationToken ct = default);
    Task<PlexUserSummary> AssignRolesAsync(long userId, IReadOnlyList<long> roleIds, CancellationToken ct = default);
    Task<PlexUserSummary> UpdatePreferencesAndLinksAsync(
        long userId,
        string? preferredAudioLang,
        string? preferredSubLang,
        string? discordUserId,
        string? discordUsername,
        bool? isActive = null,
        CancellationToken ct = default);
    Task SetBreakGlassPasswordAsync(long userId, string newPassword, CancellationToken ct = default);
    Task<PlexUserSummary?> VerifyBreakGlassLoginAsync(string usernameOrEmail, string password, CancellationToken ct = default);
    Task<PlexUserSummary> ClaimLocalOwnerAsync(string username, string? email, string? displayName, string password, CancellationToken ct = default);
    Task<PlexUserSummary> CreateLocalUserAsync(string username, string? email, string? displayName, string password, IReadOnlyList<long>? roleIds, CancellationToken ct = default);
    Task<PlexUserSummary> UpdateUserProfileAsync(long userId, string username, string? displayName, string? email, CancellationToken ct = default);
    Task ChangeLocalPasswordAsync(long userId, string currentPassword, string newPassword, CancellationToken ct = default);
    Task<PlexUserSummary?> VerifyLocalLoginAsync(string usernameOrEmail, string password, CancellationToken ct = default);
}

public sealed class PlexUserService(ISqliteDbFactory dbFactory, IRoleService roleService) : IPlexUserService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private const int Pbkdf2Iterations = 210_000;

    public async Task<bool> HasServerOwnerAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM users WHERE is_server_owner = 1;");
        return count > 0;
    }

    public async Task<IReadOnlyList<PlexUserSummary>> ListUsersAsync(CancellationToken ct = default)
    {
        var allRoles = (await roleService.ListRolesAsync(ct)).ToDictionary(r => r.Id);
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var users = (await conn.QueryAsync<UserRow>("SELECT * FROM users ORDER BY is_server_owner DESC, username COLLATE NOCASE ASC;")).ToList();
        var links = (await conn.QueryAsync<(long UserId, long RoleId)>("SELECT user_id AS UserId, role_id AS RoleId FROM user_roles;")).ToList();
        var idLinks = (await conn.QueryAsync<(long UserId, string Provider)>("SELECT user_id AS UserId, provider AS Provider FROM user_identities;")).ToList();
        var passkeys = (await conn.QueryAsync<(long UserId, int Count)>("SELECT user_id AS UserId, COUNT(1) AS Count FROM webauthn_credentials GROUP BY user_id;")).ToDictionary(x => x.UserId, x => x.Count);

        var rolesByUser = links
            .GroupBy(x => x.UserId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<RoleDefinition>)g
                    .Select(x => allRoles.GetValueOrDefault(x.RoleId))
                    .Where(r => r is not null)
                    .Cast<RoleDefinition>()
                    .ToList());

        var providersByUser = idLinks
            .GroupBy(x => x.UserId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(x => x.Provider).Distinct().OrderBy(p => p).ToList());

        return users.Select(u => MapUser(u, rolesByUser.GetValueOrDefault(u.Id) ?? [], providersByUser.GetValueOrDefault(u.Id) ?? [], passkeys.GetValueOrDefault(u.Id, 0))).ToList();
    }

    public async Task<PlexUserSummary?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>("SELECT * FROM users WHERE id = @Id;", new { Id = id });
        return row is null ? null : await HydrateUserAsync(conn, row, ct);
    }

    public async Task<PlexUserSummary?> GetByPlexUserIdAsync(long plexUserId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
            "SELECT * FROM users WHERE plex_user_id = @PlexUserId;",
            new { PlexUserId = plexUserId });
        return row is null ? null : await HydrateUserAsync(conn, row, ct);
    }

    public async Task<PlexUserSummary?> GetByDiscordUserIdAsync(string discordUserId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
            "SELECT * FROM users WHERE discord_user_id = @DiscordId;",
            new { DiscordId = discordUserId });
        return row is null ? null : await HydrateUserAsync(conn, row, ct);
    }

    public async Task<PlexUserSummary> UpsertFromPlexAsync(UpsertPlexUserCommand cmd, CancellationToken ct = default)
    {
        await roleService.EnsureBuiltInPresetsSeededAsync(ct);
        var adminRole = await roleService.GetByKeyAsync(BuiltInRoleKeys.Admin, ct)
            ?? throw new InvalidOperationException("Built-in Admin role missing.");
        var requesterRole = await roleService.GetByKeyAsync(BuiltInRoleKeys.Requester, ct)
            ?? throw new InvalidOperationException("Built-in Requester role missing.");

        var now = DateTimeOffset.UtcNow.ToString("O");
        var sectionsJson = JsonSerializer.Serialize(cmd.SharedPlexSectionIds.Distinct().OrderBy(s => s).ToArray(), JsonOpts);

        var userId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                var existing = await conn.QuerySingleOrDefaultAsync<UserRow>(
                    "SELECT * FROM users WHERE plex_user_id = @PlexUserId;",
                    new { cmd.PlexUserId },
                    transaction: tx);

                long id;
                if (existing is null)
                {
                    var passkeyUserId = Base64Url.GeneratePasskeyUserId();
                    id = await conn.ExecuteScalarAsync<long>(
                        """
                        INSERT INTO users (
                            plex_user_id, plex_uuid, username, display_name, passkey_user_id, email, avatar_url,
                            is_server_owner, is_home_user, is_active, shared_plex_section_ids_json,
                            preferred_audio_lang, preferred_sub_lang, last_login_at, created_at, updated_at
                        ) VALUES (
                            @PlexUserId, @PlexUuid, @Username, @DisplayName, @PasskeyUserId, @Email, @AvatarUrl,
                            @IsOwner, @IsHome, @IsActive, @SectionsJson,
                            @AudioLang, @SubLang, @LastLogin, @Now, @Now
                        );
                        SELECT last_insert_rowid();
                        """,
                        new
                        {
                            cmd.PlexUserId,
                            cmd.PlexUuid,
                            cmd.Username,
                            DisplayName = cmd.Username,
                            PasskeyUserId = passkeyUserId,
                            cmd.Email,
                            cmd.AvatarUrl,
                            IsOwner = cmd.IsServerOwner ? 1 : 0,
                            IsHome = cmd.IsHomeUser ? 1 : 0,
                            IsActive = cmd.IsActive ? 1 : 0,
                            SectionsJson = sectionsJson,
                            AudioLang = cmd.PreferredAudioLang,
                            SubLang = cmd.PreferredSubLang,
                            LastLogin = cmd.RecordLoginTimestamp ? now : null,
                            Now = now,
                        },
                        transaction: tx);

                    var defaultRoleId = cmd.IsServerOwner ? adminRole.Id : requesterRole.Id;
                    await conn.ExecuteAsync(
                        "INSERT OR IGNORE INTO user_roles (user_id, role_id, assigned_at) VALUES (@UserId, @RoleId, @Now);",
                        new { UserId = id, RoleId = defaultRoleId, Now = now },
                        transaction: tx);

                    await conn.ExecuteAsync(
                        """
                        INSERT OR IGNORE INTO user_identities (
                            user_id, provider, issuer, subject_id, external_username, external_email, groups_json, last_login_at, created_at
                        ) VALUES (
                            @UserId, 'plex', '', @SubjectId, @Username, @Email, '[]', @LastLogin, @Now
                        );
                        """,
                        new
                        {
                            UserId = id,
                            SubjectId = cmd.PlexUserId.ToString(),
                            cmd.Username,
                            cmd.Email,
                            LastLogin = cmd.RecordLoginTimestamp ? now : null,
                            Now = now
                        },
                        transaction: tx);
                }
                else
                {
                    id = existing.Id;
                    await conn.ExecuteAsync(
                        """
                        UPDATE users
                        SET plex_uuid                    = @PlexUuid,
                            username                     = @Username,
                            email                        = COALESCE(@Email, email),
                            avatar_url                   = COALESCE(@AvatarUrl, avatar_url),
                            is_server_owner              = CASE WHEN is_server_owner = 1 OR @IsOwner = 1 THEN 1 ELSE 0 END,
                            is_home_user                 = @IsHome,
                            is_active                    = @IsActive,
                            shared_plex_section_ids_json = @SectionsJson,
                            preferred_audio_lang         = COALESCE(preferred_audio_lang, @AudioLang),
                            preferred_sub_lang           = COALESCE(preferred_sub_lang, @SubLang),
                            last_login_at                = CASE WHEN @RecordLogin = 1 THEN @Now ELSE last_login_at END,
                            updated_at                   = @Now
                        WHERE id = @Id;
                        """,
                        new
                        {
                            Id = id,
                            cmd.PlexUuid,
                            cmd.Username,
                            cmd.Email,
                            cmd.AvatarUrl,
                            IsOwner = cmd.IsServerOwner ? 1 : 0,
                            IsHome = cmd.IsHomeUser ? 1 : 0,
                            IsActive = cmd.IsActive ? 1 : 0,
                            SectionsJson = sectionsJson,
                            AudioLang = cmd.PreferredAudioLang,
                            SubLang = cmd.PreferredSubLang,
                            RecordLogin = cmd.RecordLoginTimestamp ? 1 : 0,
                            Now = now,
                        },
                        transaction: tx);

                    if (cmd.IsServerOwner || existing.IsServerOwner != 0)
                    {
                        await conn.ExecuteAsync(
                            "INSERT OR IGNORE INTO user_roles (user_id, role_id, assigned_at) VALUES (@UserId, @RoleId, @Now);",
                            new { UserId = id, RoleId = adminRole.Id, Now = now },
                            transaction: tx);
                    }

                    await conn.ExecuteAsync(
                        """
                        INSERT OR IGNORE INTO user_identities (
                            user_id, provider, issuer, subject_id, external_username, external_email, groups_json, last_login_at, created_at
                        ) VALUES (
                            @UserId, 'plex', '', @SubjectId, @Username, @Email, '[]', @LastLogin, @Now
                        );
                        """,
                        new
                        {
                            UserId = id,
                            SubjectId = cmd.PlexUserId.ToString(),
                            cmd.Username,
                            cmd.Email,
                            LastLogin = cmd.RecordLoginTimestamp ? now : null,
                            Now = now
                        },
                        transaction: tx);

                    // If a friend was deactivated (removed from Plex server), immediately revoke their active sessions
                    if (!cmd.IsActive)
                    {
                        await conn.ExecuteAsync(
                            "UPDATE user_sessions SET revoked_at = @Now WHERE user_id = @UserId AND revoked_at IS NULL;",
                            new { UserId = id, Now = now },
                            transaction: tx);
                    }
                }

                return id;
            },
            ct);

        return (await GetByIdAsync(userId, ct))!;
    }

    public async Task<PlexUserSummary> AssignRolesAsync(
        long userId,
        IReadOnlyList<long> roleIds,
        CancellationToken ct = default)
    {
        var user = await GetByIdAsync(userId, ct)
            ?? throw new KeyNotFoundException($"User #{userId} not found.");
        var adminRole = await roleService.GetByKeyAsync(BuiltInRoleKeys.Admin, ct)
            ?? throw new InvalidOperationException("Built-in Admin role missing.");

        var distinctRoleIds = roleIds.Distinct().ToHashSet();
        if (user.IsServerOwner && !distinctRoleIds.Contains(adminRole.Id))
        {
            throw new InvalidOperationException("Cannot strip the Admin (system:owner) role from the bound Plex Server Owner.");
        }

        if (distinctRoleIds.Count == 0)
        {
            throw new ArgumentException("At least one role must be assigned to a user.");
        }

        var allRoles = (await roleService.ListRolesAsync(ct)).Select(r => r.Id).ToHashSet();
        foreach (var rid in distinctRoleIds)
        {
            if (!allRoles.Contains(rid))
            {
                throw new KeyNotFoundException($"Role #{rid} does not exist.");
            }
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync("DELETE FROM user_roles WHERE user_id = @UserId;", new { UserId = userId }, transaction: tx);
                foreach (var rid in distinctRoleIds)
                {
                    await conn.ExecuteAsync(
                        "INSERT INTO user_roles (user_id, role_id, assigned_at) VALUES (@UserId, @RoleId, @Now);",
                        new { UserId = userId, RoleId = rid, Now = now },
                        transaction: tx);
                }
            },
            ct);

        return (await GetByIdAsync(userId, ct))!;
    }

    public async Task<PlexUserSummary> UpdatePreferencesAndLinksAsync(
        long userId,
        string? preferredAudioLang,
        string? preferredSubLang,
        string? discordUserId,
        string? discordUsername,
        bool? isActive = null,
        CancellationToken ct = default)
    {
        var user = await GetByIdAsync(userId, ct)
            ?? throw new KeyNotFoundException($"User #{userId} not found.");

        if (user.IsServerOwner && isActive == false)
        {
            throw new InvalidOperationException("Cannot deactivate the Plex Server Owner account.");
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE users
                    SET preferred_audio_lang = @AudioLang,
                        preferred_sub_lang   = @SubLang,
                        discord_user_id      = @DiscordId,
                        discord_username     = @DiscordName,
                        is_active            = @IsActive,
                        updated_at           = @Now
                    WHERE id = @Id;
                    """,
                    new
                    {
                        Id = userId,
                        AudioLang = string.IsNullOrWhiteSpace(preferredAudioLang) ? null : preferredAudioLang.Trim().ToLowerInvariant(),
                        SubLang = string.IsNullOrWhiteSpace(preferredSubLang) ? null : preferredSubLang.Trim().ToLowerInvariant(),
                        DiscordId = string.IsNullOrWhiteSpace(discordUserId) ? null : discordUserId.Trim(),
                        DiscordName = string.IsNullOrWhiteSpace(discordUsername) ? null : discordUsername.Trim(),
                        IsActive = (isActive ?? user.IsActive) ? 1 : 0,
                        Now = now,
                    },
                    transaction: tx);

                if (isActive == false)
                {
                    await conn.ExecuteAsync(
                        "UPDATE user_sessions SET revoked_at = @Now WHERE user_id = @UserId AND revoked_at IS NULL;",
                        new { UserId = userId, Now = now },
                        transaction: tx);
                }
            },
            ct);

        return (await GetByIdAsync(userId, ct))!;
    }

    public async Task SetBreakGlassPasswordAsync(long userId, string newPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 10)
        {
            throw new ArgumentException("Break-glass password must be at least 10 characters long.");
        }

        var user = await GetByIdAsync(userId, ct)
            ?? throw new KeyNotFoundException($"User #{userId} not found.");
        if (!user.EffectivePermissions.Scopes.Contains(PermissionScopes.SystemAdmin))
        {
            throw new InvalidOperationException("Only users with system:admin permission can configure a break-glass password.");
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(newPassword, salt, Pbkdf2Iterations, HashAlgorithmName.SHA512, 32);
        var encoded = $"pbkdf2-sha512:{Pbkdf2Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                "UPDATE users SET break_glass_password_hash = @Hash, updated_at = @Now WHERE id = @Id;",
                new { Id = userId, Hash = encoded, Now = DateTimeOffset.UtcNow.ToString("O") },
                transaction: tx),
            ct);
    }

    public async Task<PlexUserSummary?> VerifyBreakGlassLoginAsync(
        string usernameOrEmail,
        string password,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(usernameOrEmail) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
            """
            SELECT * FROM users
            WHERE is_active = 1
              AND break_glass_password_hash IS NOT NULL
              AND (LOWER(username) = LOWER(@Ident) OR LOWER(email) = LOWER(@Ident));
            """,
            new { Ident = usernameOrEmail.Trim() });

        if (row?.BreakGlassPasswordHash is null)
        {
            return null;
        }

        var parts = row.BreakGlassPasswordHash.Split(':');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha512" || !int.TryParse(parts[1], out var iterations))
        {
            return null;
        }

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, expected.Length);

        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return null;
        }

        return await HydrateUserAsync(conn, row, ct);
    }

    public async Task<PlexUserSummary> ClaimLocalOwnerAsync(
        string username,
        string? email,
        string? displayName,
        string password,
        CancellationToken ct = default)
    {
        if (await HasServerOwnerAsync(ct))
        {
            throw new InvalidOperationException("Server owner has already been claimed.");
        }

        if (string.IsNullOrWhiteSpace(username) || username.Length < 3)
        {
            throw new ArgumentException("Username must be at least 3 characters long.", nameof(username));
        }

        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            throw new ArgumentException("Password must be at least 8 characters long.", nameof(password));
        }

        await roleService.EnsureBuiltInPresetsSeededAsync(ct);
        var adminRole = await roleService.GetByKeyAsync(BuiltInRoleKeys.Admin, ct)
            ?? throw new InvalidOperationException("Built-in Admin role missing.");

        var now = DateTimeOffset.UtcNow.ToString("O");
        var passkeyUserId = Base64Url.GeneratePasskeyUserId();
        var passwordHash = HashPassword(password);
        var dispName = string.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim();

        var userId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                var existingOwners = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(1) FROM users WHERE is_server_owner = 1;",
                    transaction: tx);
                if (existingOwners > 0)
                {
                    throw new InvalidOperationException("Server owner has already been claimed.");
                }

                var existingAdmins = await conn.ExecuteScalarAsync<int>(
                    """
                    SELECT COUNT(1)
                    FROM user_roles ur
                    JOIN roles r ON r.id = ur.role_id
                    JOIN users u ON u.id = ur.user_id
                    WHERE r.key = @AdminKey AND u.is_active = 1;
                    """,
                    new { AdminKey = BuiltInRoleKeys.Admin },
                    transaction: tx);
                if (existingAdmins > 0)
                {
                    throw new InvalidOperationException("An active administrator account already exists.");
                }

                var id = await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO users (
                        plex_user_id, plex_uuid, username, display_name, passkey_user_id, email, avatar_url,
                        is_server_owner, is_home_user, is_active, shared_plex_section_ids_json,
                        preferred_audio_lang, preferred_sub_lang, break_glass_password_hash,
                        last_login_at, created_at, updated_at
                    ) VALUES (
                        NULL, '', @Username, @DisplayName, @PasskeyUserId, @Email, NULL,
                        1, 0, 1, '["*"]',
                        NULL, NULL, @PasswordHash,
                        @Now, @Now, @Now
                    );
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        Username = username.Trim(),
                        DisplayName = dispName,
                        PasskeyUserId = passkeyUserId,
                        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant(),
                        PasswordHash = passwordHash,
                        Now = now
                    },
                    transaction: tx);

                await conn.ExecuteAsync(
                    "INSERT OR IGNORE INTO user_roles (user_id, role_id, assigned_at) VALUES (@UserId, @RoleId, @Now);",
                    new { UserId = id, RoleId = adminRole.Id, Now = now },
                    transaction: tx);

                await conn.ExecuteAsync(
                    """
                    INSERT OR IGNORE INTO user_identities (
                        user_id, provider, issuer, subject_id, external_username, external_email, groups_json, last_login_at, created_at
                    ) VALUES (
                        @UserId, 'local', '', @SubjectId, @Username, @Email, '[]', @Now, @Now
                    );
                    """,
                    new
                    {
                        UserId = id,
                        SubjectId = id.ToString(),
                        Username = username.Trim(),
                        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant(),
                        Now = now
                    },
                    transaction: tx);

                return id;
            },
            ct);

        return (await GetByIdAsync(userId, ct))!;
    }

    public async Task<PlexUserSummary> CreateLocalUserAsync(
        string username,
        string? email,
        string? displayName,
        string password,
        IReadOnlyList<long>? roleIds,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length < 3)
        {
            throw new ArgumentException("Username must be at least 3 characters long.", nameof(username));
        }

        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            throw new ArgumentException("Password must be at least 8 characters long.", nameof(password));
        }

        await roleService.EnsureBuiltInPresetsSeededAsync(ct);
        var requesterRole = await roleService.GetByKeyAsync(BuiltInRoleKeys.Requester, ct)
            ?? throw new InvalidOperationException("Built-in Requester role missing.");

        var now = DateTimeOffset.UtcNow.ToString("O");
        var passkeyUserId = Base64Url.GeneratePasskeyUserId();
        var passwordHash = HashPassword(password);
        var dispName = string.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim();

        var chosenRoles = roleIds is { Count: > 0 } ? roleIds : [requesterRole.Id];

        var userId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                var existingCount = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(1) FROM users WHERE LOWER(username) = LOWER(@Username);",
                    new { Username = username.Trim() },
                    transaction: tx);
                if (existingCount > 0)
                {
                    throw new InvalidOperationException($"Username '{username}' is already in use.");
                }

                var id = await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO users (
                        plex_user_id, plex_uuid, username, display_name, passkey_user_id, email, avatar_url,
                        is_server_owner, is_home_user, is_active, shared_plex_section_ids_json,
                        preferred_audio_lang, preferred_sub_lang, break_glass_password_hash,
                        last_login_at, created_at, updated_at
                    ) VALUES (
                        NULL, '', @Username, @DisplayName, @PasskeyUserId, @Email, NULL,
                        0, 0, 1, '[]',
                        NULL, NULL, @PasswordHash,
                        NULL, @Now, @Now
                    );
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        Username = username.Trim(),
                        DisplayName = dispName,
                        PasskeyUserId = passkeyUserId,
                        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant(),
                        PasswordHash = passwordHash,
                        Now = now
                    },
                    transaction: tx);

                foreach (var rid in chosenRoles)
                {
                    await conn.ExecuteAsync(
                        "INSERT OR IGNORE INTO user_roles (user_id, role_id, assigned_at) VALUES (@UserId, @RoleId, @Now);",
                        new { UserId = id, RoleId = rid, Now = now },
                        transaction: tx);
                }

                await conn.ExecuteAsync(
                    """
                    INSERT OR IGNORE INTO user_identities (
                        user_id, provider, issuer, subject_id, external_username, external_email, groups_json, last_login_at, created_at
                    ) VALUES (
                        @UserId, 'local', '', @SubjectId, @Username, @Email, '[]', NULL, @Now
                    );
                    """,
                    new
                    {
                        UserId = id,
                        SubjectId = id.ToString(),
                        Username = username.Trim(),
                        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant(),
                        Now = now
                    },
                    transaction: tx);

                return id;
            },
            ct);

        return (await GetByIdAsync(userId, ct))!;
    }

    public async Task<PlexUserSummary> UpdateUserProfileAsync(
        long userId,
        string username,
        string? displayName,
        string? email,
        CancellationToken ct = default)
    {
        var user = await GetByIdAsync(userId, ct)
            ?? throw new KeyNotFoundException($"User #{userId} not found.");

        if (string.IsNullOrWhiteSpace(username) || username.Length < 3)
        {
            throw new ArgumentException("Username must be at least 3 characters long.", nameof(username));
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var dispName = string.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim();
        var cleanEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                var conflict = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(1) FROM users WHERE LOWER(username) = LOWER(@Username) AND id != @UserId;",
                    new { Username = username.Trim(), UserId = userId },
                    transaction: tx);
                if (conflict > 0)
                {
                    throw new InvalidOperationException($"Username '{username}' is already in use.");
                }

                await conn.ExecuteAsync(
                    """
                    UPDATE users
                    SET username = @Username,
                        display_name = @DisplayName,
                        email = @Email,
                        updated_at = @Now
                    WHERE id = @UserId;
                    """,
                    new
                    {
                        UserId = userId,
                        Username = username.Trim(),
                        DisplayName = dispName,
                        Email = cleanEmail,
                        Now = now
                    },
                    transaction: tx);

                await conn.ExecuteAsync(
                    """
                    UPDATE user_identities
                    SET external_username = @Username,
                        external_email = @Email
                    WHERE user_id = @UserId AND provider = 'local';
                    """,
                    new
                    {
                        UserId = userId,
                        Username = username.Trim(),
                        Email = cleanEmail
                    },
                    transaction: tx);
            },
            ct);

        return (await GetByIdAsync(userId, ct))!;
    }

    public async Task ChangeLocalPasswordAsync(
        long userId,
        string currentPassword,
        string newPassword,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
        {
            throw new ArgumentException("New password must be at least 8 characters long.", nameof(newPassword));
        }

        await using (var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            var row = await conn.QuerySingleOrDefaultAsync<UserRow>("SELECT * FROM users WHERE id = @Id;", new { Id = userId });
            if (row is null)
            {
                throw new KeyNotFoundException($"User #{userId} not found.");
            }

            if (!string.IsNullOrEmpty(row.BreakGlassPasswordHash))
            {
                if (!VerifyPassword(currentPassword, row.BreakGlassPasswordHash))
                {
                    throw new InvalidOperationException("Current password does not match.");
                }
            }
        }

        var newHash = HashPassword(newPassword);
        var now = DateTimeOffset.UtcNow.ToString("O");

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    "UPDATE users SET break_glass_password_hash = @Hash, updated_at = @Now WHERE id = @Id;",
                    new { Id = userId, Hash = newHash, Now = now },
                    transaction: tx);

                await conn.ExecuteAsync(
                    """
                    INSERT OR IGNORE INTO user_identities (
                        user_id, provider, issuer, subject_id, external_username, external_email, groups_json, last_login_at, created_at
                    ) VALUES (
                        @UserId, 'local', '', @SubjectId, '', NULL, '[]', NULL, @Now
                    );
                    """,
                    new { UserId = userId, SubjectId = userId.ToString(), Now = now },
                    transaction: tx);
            },
            ct);
    }

    public Task<PlexUserSummary?> VerifyLocalLoginAsync(string usernameOrEmail, string password, CancellationToken ct = default) =>
        VerifyBreakGlassLoginAsync(usernameOrEmail, password, ct);

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA512, 32);
        return $"pbkdf2-sha512:{Pbkdf2Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string storedHash)
    {
        var parts = storedHash.Split(':');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha512" || !int.TryParse(parts[1], out var iterations))
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private async Task<PlexUserSummary> HydrateUserAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        UserRow row,
        CancellationToken ct)
    {
        var allRoles = (await roleService.ListRolesAsync(ct)).ToDictionary(r => r.Id);
        var roleIds = await conn.QueryAsync<long>(
            "SELECT role_id FROM user_roles WHERE user_id = @UserId;",
            new { UserId = row.Id });
        var roles = roleIds
            .Select(rid => allRoles.GetValueOrDefault(rid))
            .Where(r => r is not null)
            .Cast<RoleDefinition>()
            .ToList();

        var providers = (await conn.QueryAsync<string>(
            "SELECT DISTINCT provider FROM user_identities WHERE user_id = @UserId ORDER BY provider;",
            new { UserId = row.Id })).ToList();

        var passkeyCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM webauthn_credentials WHERE user_id = @UserId;",
            new { UserId = row.Id });

        return MapUser(row, roles, providers, passkeyCount);
    }

    private static PlexUserSummary MapUser(
        UserRow row,
        IReadOnlyList<RoleDefinition> roles,
        IReadOnlyList<string>? providers = null,
        int passkeyCount = 0)
    {
        var sections = JsonSerializer.Deserialize<string[]>(row.SharedPlexSectionIdsJson, JsonOpts) ?? [];
        var scopes = roles
            .SelectMany(r => r.Scopes)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        var hasAllLibs = row.IsServerOwner != 0 || roles.Any(r => r.LibraryAccessMode == "all_libraries");
        var explicitLibs = roles
            .Where(r => r.LibraryAccessMode == "explicit_libraries")
            .SelectMany(r => r.AllowedLibraryIds)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        var autoApprove = row.IsServerOwner != 0 || roles.Any(r => r.AutoApproveRequests) || scopes.Contains(PermissionScopes.CatalogAutoApprove);
        var movieQuota = MaxNullableQuota(roles.Select(r => r.MovieRequestQuota));
        var tvQuota = MaxNullableQuota(roles.Select(r => r.TvSeasonRequestQuota));
        var musicBookQuota = MaxNullableQuota(roles.Select(r => r.MusicBookRequestQuota));
        int? cleanerrLimit = null;
        if (row.IsServerOwner == 0 && scopes.Contains(PermissionScopes.CleanerrExclude))
        {
            var excludeRoles = roles.Where(r => r.Scopes.Contains(PermissionScopes.CleanerrExclude)).ToList();
            if (excludeRoles.Count > 0 && excludeRoles.All(r => r.CleanerrExclusionLimit.HasValue))
            {
                cleanerrLimit = excludeRoles.Max(r => r.CleanerrExclusionLimit!.Value);
            }
        }

        var effective = new EffectiveUserPermissions(
            HasAllLibrariesAccess: hasAllLibs,
            SharedPlexSectionIds: sections,
            ExplicitAllowedLibraryIds: explicitLibs,
            Scopes: scopes,
            AutoApproveRequests: autoApprove,
            MovieRequestQuota: movieQuota,
            TvSeasonRequestQuota: tvQuota,
            MusicBookRequestQuota: musicBookQuota,
            CleanerrExclusionLimit: cleanerrLimit);

        return new PlexUserSummary(
            Id: row.Id,
            PlexUserId: row.PlexUserId ?? 0,
            PlexUuid: row.PlexUuid,
            Username: row.Username,
            Email: row.Email,
            AvatarUrl: row.AvatarUrl,
            IsServerOwner: row.IsServerOwner != 0,
            IsHomeUser: row.IsHomeUser != 0,
            IsActive: row.IsActive != 0,
            HasBreakGlassPassword: !string.IsNullOrEmpty(row.BreakGlassPasswordHash),
            SharedPlexSectionIds: sections,
            PreferredAudioLang: row.PreferredAudioLang,
            PreferredSubLang: row.PreferredSubLang,
            DiscordUserId: row.DiscordUserId,
            DiscordUsername: row.DiscordUsername,
            SeerrUserId: row.SeerrUserId,
            Roles: roles,
            EffectivePermissions: effective,
            LastLoginAt: row.LastLoginAt,
            CreatedAt: row.CreatedAt,
            DisplayName: string.IsNullOrWhiteSpace(row.DisplayName) ? row.Username : row.DisplayName,
            PasskeyUserId: row.PasskeyUserId,
            Providers: providers ?? [],
            PasskeyCount: passkeyCount);
    }

    private static int? MaxNullableQuota(IEnumerable<int?> quotas)
    {
        var materialized = quotas.Where(q => q.HasValue).Select(q => q!.Value).ToList();
        return materialized.Count == 0 ? null : materialized.Max();
    }

    private sealed class UserRow
    {
        public long Id { get; init; }
        public long? PlexUserId { get; init; }
        public string PlexUuid { get; init; } = "";
        public string Username { get; init; } = "";
        public string? DisplayName { get; init; }
        public string PasskeyUserId { get; init; } = "";
        public string? Email { get; init; }
        public string? AvatarUrl { get; init; }
        public int IsServerOwner { get; init; }
        public int IsHomeUser { get; init; }
        public int IsActive { get; init; }
        public string SharedPlexSectionIdsJson { get; init; } = "[]";
        public string? PreferredAudioLang { get; init; }
        public string? PreferredSubLang { get; init; }
        public string? DiscordUserId { get; init; }
        public string? DiscordUsername { get; init; }
        public long? SeerrUserId { get; init; }
        public string? BreakGlassPasswordHash { get; init; }
        public string? LastLoginAt { get; init; }
        public string CreatedAt { get; init; } = "";
    }
}
