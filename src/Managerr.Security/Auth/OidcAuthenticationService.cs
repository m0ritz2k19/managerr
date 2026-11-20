using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Managerr.Common.Configuration;
using Managerr.Common.Net;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Microsoft.Extensions.Options;

namespace Managerr.Security.Auth;

public sealed record OidcConfigDto(
    bool Enabled,
    string DisplayName,
    string? Issuer,
    string? ClientId,
    bool HasClientSecret,
    string Scopes,
    bool AutoProvision,
    bool AutoRedirect,
    bool TrustVerifiedEmail,
    bool AllowUnclaimedOwnerBootstrap,
    string UsernameClaim,
    string EmailClaim,
    string DisplayNameClaim,
    string GroupsClaim,
    IReadOnlyList<string> AdminGroups,
    IReadOnlyList<string> ManagerGroups,
    IReadOnlyList<string> RequesterGroups);

public interface IOidcAuthenticationService
{
    Task<OidcConfigDto> GetEffectiveConfigAsync(CancellationToken ct = default);
    Task<string> BuildAuthorizationUrlAsync(string redirectUri, string? returnUrl, long? linkUserId = null, CancellationToken ct = default);
    Task<(IssuedSession Session, string SafeReturnUrl)> ProcessCallbackAsync(
        string code,
        string state,
        string redirectUri,
        string? userAgent,
        string? ipAddress,
        long? currentSessionUserId = null,
        CancellationToken ct = default);
    Task<(bool Success, string? Error, string? Issuer, string? AuthorizationEndpoint, string? TokenEndpoint, string? JwksUri)> TestConnectionAsync(
        string? issuerOverride = null,
        string? clientIdOverride = null,
        string? clientSecretOverride = null,
        CancellationToken ct = default);
}

public sealed class OidcAuthenticationService(
    ISqliteDbFactory dbFactory,
    IAppSettingsService appSettings,
    IOptionsMonitor<ManagerrAuthOptions> authOptions,
    ISafeHttpClientFactory safeHttpClientFactory,
    IPlexUserService userService,
    IPlexAuthAndSessionService sessionService,
    IRoleService roleService,
    IAuditLogService auditLog,
    IConfigSourceInspector? inspector = null) : IOidcAuthenticationService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public const string SettingKeyEnabled = "auth.oidc.enabled";
    public const string SettingKeyDisplayName = "auth.oidc.display_name";
    public const string SettingKeyIssuer = "auth.oidc.issuer";
    public const string SettingKeyClientId = "auth.oidc.client_id";
    public const string SettingKeyClientSecretEnc = "auth.oidc.client_secret";
    public const string SettingKeyScopes = "auth.oidc.scopes";
    public const string SettingKeyAutoProvision = "auth.oidc.auto_provision";
    public const string SettingKeyAutoRedirect = "auth.oidc.auto_redirect";
    public const string SettingKeyTrustVerifiedEmail = "auth.oidc.trust_verified_email";
    public const string SettingKeyUsernameClaim = "auth.oidc.username_claim";
    public const string SettingKeyEmailClaim = "auth.oidc.email_claim";
    public const string SettingKeyDisplayNameClaim = "auth.oidc.display_name_claim";
    public const string SettingKeyGroupsClaim = "auth.oidc.groups_claim";
    public const string SettingKeyAdminGroupsJson = "auth.oidc.admin_groups_json";
    public const string SettingKeyManagerGroupsJson = "auth.oidc.manager_groups_json";
    public const string SettingKeyRequesterGroupsJson = "auth.oidc.requester_groups_json";

    public async Task<OidcConfigDto> GetEffectiveConfigAsync(CancellationToken ct = default)
    {
        var raw = await ResolveEffectiveConfigAsync(ct);
        return new OidcConfigDto(
            Enabled: raw.Enabled,
            DisplayName: raw.DisplayName,
            Issuer: raw.Issuer,
            ClientId: raw.ClientId,
            HasClientSecret: !string.IsNullOrWhiteSpace(raw.ClientSecret),
            Scopes: raw.Scopes,
            AutoProvision: raw.AutoProvision,
            AutoRedirect: raw.AutoRedirect,
            TrustVerifiedEmail: raw.TrustVerifiedEmail,
            AllowUnclaimedOwnerBootstrap: raw.AllowUnclaimedOwnerBootstrap,
            UsernameClaim: raw.UsernameClaim,
            EmailClaim: raw.EmailClaim,
            DisplayNameClaim: raw.DisplayNameClaim,
            GroupsClaim: raw.GroupsClaim,
            AdminGroups: raw.AdminGroups,
            ManagerGroups: raw.ManagerGroups,
            RequesterGroups: raw.RequesterGroups);
    }

    private sealed record RawOidcConfig(
        bool Enabled,
        string DisplayName,
        string? Issuer,
        string? ClientId,
        string? ClientSecret,
        string Scopes,
        bool AutoProvision,
        bool AutoRedirect,
        bool TrustVerifiedEmail,
        bool AllowUnclaimedOwnerBootstrap,
        string UsernameClaim,
        string EmailClaim,
        string DisplayNameClaim,
        string GroupsClaim,
        IReadOnlyList<string> AdminGroups,
        IReadOnlyList<string> ManagerGroups,
        IReadOnlyList<string> RequesterGroups);

    private async Task<RawOidcConfig> ResolveEffectiveConfigAsync(CancellationToken ct)
    {
        var opt = authOptions.CurrentValue.Oidc;

        var dbEnabledStr = await appSettings.GetAsync(SettingKeyEnabled, ct);
        var dbDisplayName = await appSettings.GetAsync(SettingKeyDisplayName, ct);
        var dbIssuer = await appSettings.GetAsync(SettingKeyIssuer, ct);
        var dbClientId = await appSettings.GetAsync(SettingKeyClientId, ct);
        var dbClientSecret = await appSettings.GetSecretAsync(SettingKeyClientSecretEnc, ct);
        var dbScopes = await appSettings.GetAsync(SettingKeyScopes, ct);
        var dbAutoProvisionStr = await appSettings.GetAsync(SettingKeyAutoProvision, ct);
        var dbAutoRedirectStr = await appSettings.GetAsync(SettingKeyAutoRedirect, ct);
        var dbTrustVerifiedEmailStr = await appSettings.GetAsync(SettingKeyTrustVerifiedEmail, ct);
        var dbUsernameClaim = await appSettings.GetAsync(SettingKeyUsernameClaim, ct);
        var dbEmailClaim = await appSettings.GetAsync(SettingKeyEmailClaim, ct);
        var dbDisplayNameClaim = await appSettings.GetAsync(SettingKeyDisplayNameClaim, ct);
        var dbGroupsClaim = await appSettings.GetAsync(SettingKeyGroupsClaim, ct);
        var dbAdminGroupsJson = await appSettings.GetAsync(SettingKeyAdminGroupsJson, ct);
        var dbManagerGroupsJson = await appSettings.GetAsync(SettingKeyManagerGroupsJson, ct);
        var dbRequesterGroupsJson = await appSettings.GetAsync(SettingKeyRequesterGroupsJson, ct);

        bool IsDbManaged(string key) => (inspector?.GetSource(key) ?? "database") == "database";

        bool ResolveBool(string sourceKey, string? dbValue, bool optValue) =>
            IsDbManaged(sourceKey) && bool.TryParse(dbValue, out var parsed)
                ? parsed
                : optValue;

        string ResolveString(string sourceKey, string? dbValue, string optValue) =>
            IsDbManaged(sourceKey) && !string.IsNullOrWhiteSpace(dbValue)
                ? dbValue
                : optValue;

        string? ResolveNullableString(string sourceKey, string? dbValue, string? optValue) =>
            IsDbManaged(sourceKey) && !string.IsNullOrWhiteSpace(dbValue)
                ? dbValue
                : optValue;

        IReadOnlyList<string> ResolveGroups(string sourceKey, string? dbJson, string[]? optGroups) =>
            IsDbManaged(sourceKey) && !string.IsNullOrWhiteSpace(dbJson)
                ? DeserializeGroups(dbJson)
                : (optGroups ?? Array.Empty<string>());

        bool enabled = ResolveBool("oidc.enabled", dbEnabledStr, opt.Enabled);
        string displayName = ResolveString("oidc.display_name", dbDisplayName, opt.DisplayName);
        string? issuer = ResolveNullableString("oidc.issuer", dbIssuer, opt.Issuer);
        string? clientId = ResolveNullableString("oidc.client_id", dbClientId, opt.ClientId);
        string? clientSecret = ResolveNullableString("oidc.client_secret", dbClientSecret, opt.ClientSecret);
        string scopes = ResolveString("oidc.scopes", dbScopes, opt.Scopes);
        bool autoProvision = ResolveBool("oidc.auto_provision", dbAutoProvisionStr, opt.AutoProvision);
        bool autoRedirect = ResolveBool("oidc.auto_redirect", dbAutoRedirectStr, opt.AutoRedirect);
        bool trustVerifiedEmail = ResolveBool("oidc.trust_verified_email", dbTrustVerifiedEmailStr, opt.TrustVerifiedEmail);
        bool allowUnclaimedOwner = opt.AllowUnclaimedOwnerBootstrap;

        string usernameClaim = ResolveString("oidc.username_claim", dbUsernameClaim, opt.UsernameClaim);
        string emailClaim = ResolveString("oidc.email_claim", dbEmailClaim, opt.EmailClaim);
        string displayNameClaim = ResolveString("oidc.display_name_claim", dbDisplayNameClaim, opt.DisplayNameClaim);
        string groupsClaim = ResolveString("oidc.groups_claim", dbGroupsClaim, opt.GroupsClaim);

        var adminGroups = ResolveGroups("oidc.admin_groups_json", dbAdminGroupsJson, opt.AdminGroups);
        var managerGroups = ResolveGroups("oidc.manager_groups_json", dbManagerGroupsJson, opt.ManagerGroups);
        var requesterGroups = ResolveGroups("oidc.requester_groups_json", dbRequesterGroupsJson, opt.RequesterGroups);

        return new RawOidcConfig(
            Enabled: enabled,
            DisplayName: displayName,
            Issuer: issuer?.Trim(),
            ClientId: clientId?.Trim(),
            ClientSecret: clientSecret?.Trim(),
            Scopes: scopes,
            AutoProvision: autoProvision,
            AutoRedirect: autoRedirect,
            TrustVerifiedEmail: trustVerifiedEmail,
            AllowUnclaimedOwnerBootstrap: allowUnclaimedOwner,
            UsernameClaim: usernameClaim,
            EmailClaim: emailClaim,
            DisplayNameClaim: displayNameClaim,
            GroupsClaim: groupsClaim,
            AdminGroups: adminGroups,
            ManagerGroups: managerGroups,
            RequesterGroups: requesterGroups);
    }

    private static IReadOnlyList<string> DeserializeGroups(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOpts) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public async Task<string> BuildAuthorizationUrlAsync(string redirectUri, string? returnUrl, long? linkUserId = null, CancellationToken ct = default)
    {
        var cfg = await ResolveEffectiveConfigAsync(ct);
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.Issuer) || string.IsNullOrWhiteSpace(cfg.ClientId))
        {
            throw new InvalidOperationException("OpenID Connect authentication is not enabled or incomplete.");
        }

        var discovery = await FetchDiscoveryDocAsync(cfg.Issuer, ct);
        var authEndpoint = discovery.AuthorizationEndpoint
            ?? throw new InvalidOperationException("OIDC discovery metadata missing authorization_endpoint.");

        var stateBytes = RandomNumberGenerator.GetBytes(32);
        var nonceBytes = RandomNumberGenerator.GetBytes(32);
        var verifierBytes = RandomNumberGenerator.GetBytes(32);

        var state = Base64Url.Encode(stateBytes);
        var nonce = Base64Url.Encode(nonceBytes);
        var codeVerifier = Base64Url.Encode(verifierBytes);

        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var codeChallenge = Base64Url.Encode(challengeBytes);

        var payload = JsonSerializer.Serialize(new
        {
            state,
            nonce,
            codeVerifier,
            returnUrl = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl,
            redirectUri,
            issuer = cfg.Issuer,
            linkUserId
        }, JsonOpts);

        var now = DateTimeOffset.UtcNow;
        var nowIso = now.ToString("O");
        var expiresAt = now.AddMinutes(10).ToString("O");

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Cache,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync("DELETE FROM auth_challenges WHERE expires_at <= @Now;", new { Now = nowIso }, transaction: tx);
                await conn.ExecuteAsync(
                    """
                    INSERT INTO auth_challenges (challenge_key, kind, payload_json, expires_at, consumed_at)
                    VALUES (@Key, 'oidc', @Payload, @ExpiresAt, NULL)
                    ON CONFLICT(challenge_key) DO UPDATE SET
                        payload_json = excluded.payload_json,
                        expires_at = excluded.expires_at,
                        consumed_at = NULL;
                    """,
                    new { Key = state, Payload = payload, ExpiresAt = expiresAt },
                    transaction: tx);
            },
            ct);

        var sb = new StringBuilder(authEndpoint);
        sb.Append(authEndpoint.Contains('?') ? '&' : '?');
        sb.Append("response_type=code");
        sb.Append("&client_id=").Append(Uri.EscapeDataString(cfg.ClientId));
        sb.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        sb.Append("&scope=").Append(Uri.EscapeDataString(cfg.Scopes));
        sb.Append("&state=").Append(Uri.EscapeDataString(state));
        sb.Append("&nonce=").Append(Uri.EscapeDataString(nonce));
        sb.Append("&code_challenge=").Append(Uri.EscapeDataString(codeChallenge));
        sb.Append("&code_challenge_method=S256");

        return sb.ToString();
    }

    public async Task<(IssuedSession Session, string SafeReturnUrl)> ProcessCallbackAsync(
        string code,
        string state,
        string redirectUri,
        string? userAgent,
        string? ipAddress,
        long? currentSessionUserId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            throw new ArgumentException("Missing authorization code or state in callback.");
        }

        var cfg = await ResolveEffectiveConfigAsync(ct);
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.Issuer) || string.IsNullOrWhiteSpace(cfg.ClientId))
        {
            throw new InvalidOperationException("OpenID Connect is not enabled.");
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        string? challengePayload = null;

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Cache,
            async (conn, tx) =>
            {
                challengePayload = await conn.QuerySingleOrDefaultAsync<string>(
                    """
                    SELECT payload_json FROM auth_challenges
                    WHERE challenge_key = @Key AND kind = 'oidc' AND consumed_at IS NULL AND expires_at > @Now;
                    """,
                    new { Key = state, Now = now },
                    transaction: tx);

                if (challengePayload != null)
                {
                    await conn.ExecuteAsync(
                        "UPDATE auth_challenges SET consumed_at = @Now WHERE challenge_key = @Key;",
                        new { Key = state, Now = now },
                        transaction: tx);
                }
            },
            ct);

        if (challengePayload is null)
        {
            throw new InvalidOperationException("Invalid, expired, or already-consumed OIDC state.");
        }

        using var payloadDoc = JsonDocument.Parse(challengePayload);
        var expectedNonce = payloadDoc.RootElement.GetProperty("nonce").GetString()!;
        var codeVerifier = payloadDoc.RootElement.GetProperty("codeVerifier").GetString()!;
        var storedRedirectUri = payloadDoc.RootElement.TryGetProperty("redirectUri", out var redirProp) ? redirProp.GetString() : null;
        var storedIssuer = payloadDoc.RootElement.TryGetProperty("issuer", out var issStoredProp) ? issStoredProp.GetString() : null;
        var returnUrl = payloadDoc.RootElement.TryGetProperty("returnUrl", out var rProp) ? rProp.GetString() ?? "/" : "/";
        long? linkUserId = payloadDoc.RootElement.TryGetProperty("linkUserId", out var linkProp) && linkProp.ValueKind == JsonValueKind.Number
            ? linkProp.GetInt64()
            : null;

        if (!string.Equals(storedRedirectUri, redirectUri, StringComparison.Ordinal))
        {
            throw new SecurityException("OIDC callback redirect_uri does not match authorization request.");
        }

        if (string.IsNullOrWhiteSpace(storedIssuer) ||
            !string.Equals(storedIssuer.TrimEnd('/'), cfg.Issuer.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException("OIDC callback issuer does not match authorization request.");
        }

        if (linkUserId.HasValue && (!currentSessionUserId.HasValue || currentSessionUserId.Value != linkUserId.Value))
        {
            throw new UnauthorizedAccessException("Active user session does not match OIDC link target.");
        }

        var discovery = await FetchDiscoveryDocAsync(cfg.Issuer, ct);
        var tokenEndpoint = discovery.TokenEndpoint
            ?? throw new InvalidOperationException("OIDC discovery metadata missing token_endpoint.");

        if (!string.IsNullOrWhiteSpace(cfg.ClientSecret))
        {
            if (!Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out var tokenUri) ||
                (tokenUri.Scheme != Uri.UriSchemeHttps && !tokenUri.IsLoopback))
            {
                throw new SecurityException("OIDC token endpoint must use HTTPS when transmitting a client secret.");
            }
        }

        var client = safeHttpClientFactory.CreateHomelabClient(allowPrivateLan: true);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = cfg.ClientId,
            ["code_verifier"] = codeVerifier
        };
        if (!string.IsNullOrWhiteSpace(cfg.ClientSecret))
        {
            form["client_secret"] = cfg.ClientSecret;
        }

        using var tokenReq = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        using var tokenResp = await client.SendAsync(tokenReq, ct);
        if (!tokenResp.IsSuccessStatusCode)
        {
            var err = await tokenResp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Token exchange failed ({tokenResp.StatusCode}): {err}");
        }

        using var tokenJson = await tokenResp.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty response from token endpoint.");

        var idToken = tokenJson.RootElement.GetProperty("id_token").GetString()
            ?? throw new InvalidOperationException("Token response missing id_token.");

        string? accessToken = tokenJson.RootElement.TryGetProperty("access_token", out var atProp) ? atProp.GetString() : null;

        // Verify id_token JWT
        var claims = await ValidateJwtAsync(idToken, cfg.Issuer, cfg.ClientId, expectedNonce, discovery.JwksUri, ct);

        var sub = claims.TryGetValue("sub", out var subEl) && subEl.ValueKind == JsonValueKind.String ? subEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(sub))
        {
            throw new SecurityException("OIDC ID token missing required 'sub' claim.");
        }

        // Optional UserInfo enrichment (must match ID token 'sub' per OIDC Core 5.3.4)
        if (!string.IsNullOrWhiteSpace(discovery.UserinfoEndpoint) && !string.IsNullOrWhiteSpace(accessToken))
        {
            try
            {
                using var uiReq = new HttpRequestMessage(HttpMethod.Get, discovery.UserinfoEndpoint);
                uiReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                using var uiResp = await client.SendAsync(uiReq, ct);
                if (uiResp.IsSuccessStatusCode)
                {
                    using var uiJson = await uiResp.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct);
                    if (uiJson != null &&
                        uiJson.RootElement.TryGetProperty("sub", out var uiSubEl) &&
                        uiSubEl.ValueKind == JsonValueKind.String &&
                        string.Equals(uiSubEl.GetString(), sub, StringComparison.Ordinal))
                    {
                        foreach (var prop in uiJson.RootElement.EnumerateObject())
                        {
                            if (!claims.ContainsKey(prop.Name))
                            {
                                claims[prop.Name] = prop.Value.Clone();
                            }
                        }
                    }
                }
            }
            catch
            {
                // Non-fatal if UserInfo endpoint is unreachable
            }
        }

        var username = claims.TryGetValue(cfg.UsernameClaim, out var unEl) && unEl.ValueKind == JsonValueKind.String
            ? unEl.GetString()!
            : (claims.TryGetValue("preferred_username", out var puEl) ? puEl.GetString() ?? sub : sub);

        var email = claims.TryGetValue(cfg.EmailClaim, out var emEl) && emEl.ValueKind == JsonValueKind.String
            ? emEl.GetString()
            : null;

        var isEmailVerified = claims.TryGetValue("email_verified", out var evEl) &&
            (evEl.ValueKind == JsonValueKind.True ||
             (evEl.ValueKind == JsonValueKind.String && string.Equals(evEl.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

        var displayName = claims.TryGetValue(cfg.DisplayNameClaim, out var dnEl) && dnEl.ValueKind == JsonValueKind.String
            ? dnEl.GetString()
            : username;

        var groups = ExtractGroups(claims, cfg.GroupsClaim);

        // Role mapping
        await roleService.EnsureBuiltInPresetsSeededAsync(ct);
        var adminRole = await roleService.GetByKeyAsync(BuiltInRoleKeys.Admin, ct)!;
        var managerRole = await roleService.GetByKeyAsync(BuiltInRoleKeys.Manager, ct)!;
        var requesterRole = await roleService.GetByKeyAsync(BuiltInRoleKeys.Requester, ct)!;

        long groupMappedRoleId = requesterRole?.Id ?? 1;
        if (groups.Any(g => cfg.AdminGroups.Contains(g, StringComparer.OrdinalIgnoreCase)))
        {
            groupMappedRoleId = adminRole?.Id ?? groupMappedRoleId;
        }
        else if (groups.Any(g => cfg.ManagerGroups.Contains(g, StringComparer.OrdinalIgnoreCase)))
        {
            groupMappedRoleId = managerRole?.Id ?? groupMappedRoleId;
        }
        else if (groups.Any(g => cfg.RequesterGroups.Contains(g, StringComparer.OrdinalIgnoreCase)))
        {
            groupMappedRoleId = requesterRole?.Id ?? groupMappedRoleId;
        }

        var hasOwner = await userService.HasServerOwnerAsync(ct);
        var isBootstrapOwner = cfg.AllowUnclaimedOwnerBootstrap && !hasOwner;

        // Provision, link, or resolve user
        var user = await UpsertOidcUserAsync(
            sub: sub,
            issuer: cfg.Issuer,
            username: username,
            email: email,
            displayName: displayName,
            groups: groups,
            groupMappedRoleId: groupMappedRoleId,
            adminRoleId: adminRole?.Id ?? groupMappedRoleId,
            isServerOwner: isBootstrapOwner,
            trustVerifiedEmail: cfg.TrustVerifiedEmail && isEmailVerified,
            autoProvision: cfg.AutoProvision,
            linkUserId: linkUserId,
            ct: ct);

        var session = await sessionService.CreateSessionForUserAsync(user, userAgent, ipAddress, ct);

        await auditLog.RecordAsync(new AuditWriteRequest(
            Action: linkUserId.HasValue ? "auth.oidc.linked" : "auth.oidc.login",
            ResourceType: "user",
            ResourceId: user.Id.ToString(),
            Details: new { sub, issuer = cfg.Issuer, username, email, groups, linkedExistingUser = linkUserId.HasValue },
            ActorUserId: user.Id,
            ActorUsername: user.Username,
            IpAddress: ipAddress), ct);

        var safeReturn = string.IsNullOrWhiteSpace(returnUrl)
            || !returnUrl.StartsWith('/')
            || returnUrl.StartsWith("//")
            || returnUrl.StartsWith("/\\")
            || returnUrl.Any(char.IsControl)
            ? "/"
            : returnUrl;

        return (session, safeReturn);
    }

    private static List<string> ExtractGroups(Dictionary<string, JsonElement> claims, string groupsClaim)
    {
        var list = new List<string>();
        if (claims.TryGetValue(groupsClaim, out var el))
        {
            if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                    }
                }
            }
            else if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    list.AddRange(s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }
        }
        return list;
    }

    private async Task<PlexUserSummary> UpsertOidcUserAsync(
        string sub,
        string issuer,
        string username,
        string? email,
        string? displayName,
        IReadOnlyList<string> groups,
        long groupMappedRoleId,
        long adminRoleId,
        bool isServerOwner,
        bool trustVerifiedEmail,
        bool autoProvision,
        long? linkUserId,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var groupsJson = JsonSerializer.Serialize(groups, JsonOpts);

        var userId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                // 0. Explicit account-linking flow (?mode=link) for an authenticated user
                if (linkUserId.HasValue)
                {
                    var boundOtherId = await conn.QuerySingleOrDefaultAsync<long?>(
                        """
                        SELECT user_id FROM user_identities
                        WHERE provider = 'oidc' AND issuer = @Issuer AND subject_id = @Sub;
                        """,
                        new { Issuer = issuer, Sub = sub },
                        transaction: tx);

                    if (boundOtherId.HasValue && boundOtherId.Value != linkUserId.Value)
                    {
                        throw new InvalidOperationException("This OIDC identity is already linked to another Managerr user.");
                    }

                    await conn.ExecuteAsync(
                        """
                        INSERT INTO user_identities (
                            user_id, provider, issuer, subject_id, external_username, external_email, groups_json, last_login_at, created_at
                        ) VALUES (
                            @Id, 'oidc', @Issuer, @Sub, @Username, @Email, @GroupsJson, @Now, @Now
                        )
                        ON CONFLICT(provider, issuer, subject_id) DO UPDATE SET
                            external_username = excluded.external_username,
                            external_email = excluded.external_email,
                            groups_json = excluded.groups_json,
                            last_login_at = excluded.last_login_at;
                        """,
                        new { Id = linkUserId.Value, Issuer = issuer, Sub = sub, Username = username, Email = email, GroupsJson = groupsJson, Now = now },
                        transaction: tx);

                    await conn.ExecuteAsync(
                        "UPDATE users SET last_login_at = @Now, updated_at = @Now WHERE id = @Id;",
                        new { Id = linkUserId.Value, Now = now },
                        transaction: tx);

                    return linkUserId.Value;
                }

                // 1. Look up by user_identities
                var existingUserId = await conn.QuerySingleOrDefaultAsync<long?>(
                    """
                    SELECT user_id FROM user_identities
                    WHERE provider = 'oidc' AND issuer = @Issuer AND subject_id = @Sub;
                    """,
                    new { Issuer = issuer, Sub = sub },
                    transaction: tx);

                if (existingUserId.HasValue)
                {
                    var id = existingUserId.Value;
                    await conn.ExecuteAsync(
                        """
                        UPDATE users
                        SET last_login_at = @Now,
                            display_name = COALESCE(@DisplayName, display_name),
                            updated_at = @Now
                        WHERE id = @Id;
                        """,
                        new { Id = id, DisplayName = displayName, Now = now },
                        transaction: tx);

                    await conn.ExecuteAsync(
                        """
                        UPDATE user_identities
                        SET external_username = @Username,
                            external_email = @Email,
                            groups_json = @GroupsJson,
                            last_login_at = @Now
                        WHERE user_id = @Id AND provider = 'oidc' AND issuer = @Issuer AND subject_id = @Sub;
                        """,
                        new { Id = id, Issuer = issuer, Sub = sub, Username = username, Email = email, GroupsJson = groupsJson, Now = now },
                        transaction: tx);

                    return id;
                }

                // 2. Look up by email if enabled
                if (trustVerifiedEmail && !string.IsNullOrWhiteSpace(email))
                {
                    var emailUserId = await conn.QuerySingleOrDefaultAsync<long?>(
                        "SELECT id FROM users WHERE LOWER(email) = LOWER(@Email);",
                        new { Email = email.Trim() },
                        transaction: tx);

                    if (emailUserId.HasValue)
                    {
                        var id = emailUserId.Value;
                        await conn.ExecuteAsync(
                            """
                            INSERT OR IGNORE INTO user_identities (
                                user_id, provider, issuer, subject_id, external_username, external_email, groups_json, last_login_at, created_at
                            ) VALUES (
                                @Id, 'oidc', @Issuer, @Sub, @Username, @Email, @GroupsJson, @Now, @Now
                            );
                            """,
                            new { Id = id, Issuer = issuer, Sub = sub, Username = username, Email = email, GroupsJson = groupsJson, Now = now },
                            transaction: tx);

                        await conn.ExecuteAsync(
                            "UPDATE users SET last_login_at = @Now, updated_at = @Now WHERE id = @Id;",
                            new { Id = id, Now = now },
                            transaction: tx);

                        return id;
                    }
                }

                // 3. Atomic owner check + auto-provision check inside Core write transaction
                var existingOwnerCount = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(1) FROM users WHERE is_server_owner = 1;",
                    transaction: tx);
                var actualIsOwner = isServerOwner && existingOwnerCount == 0;
                if (!autoProvision && !actualIsOwner)
                {
                    throw new UnauthorizedAccessException("User auto-provisioning is disabled for OpenID Connect.");
                }

                var assignedRoleId = actualIsOwner ? adminRoleId : groupMappedRoleId;
                var passkeyUserId = Base64Url.GeneratePasskeyUserId();

                var newId = await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO users (
                        plex_user_id, plex_uuid, username, display_name, passkey_user_id, email, avatar_url,
                        is_server_owner, is_home_user, is_active, shared_plex_section_ids_json,
                        preferred_audio_lang, preferred_sub_lang, break_glass_password_hash,
                        last_login_at, created_at, updated_at
                    ) VALUES (
                        NULL, '', @Username, @DisplayName, @PasskeyUserId, @Email, NULL,
                        @IsOwner, 0, 1, '["*"]',
                        NULL, NULL, NULL,
                        @Now, @Now, @Now
                    );
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        Username = username,
                        DisplayName = displayName ?? username,
                        PasskeyUserId = passkeyUserId,
                        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant(),
                        IsOwner = actualIsOwner ? 1 : 0,
                        Now = now
                    },
                    transaction: tx);

                await conn.ExecuteAsync(
                    "INSERT OR IGNORE INTO user_roles (user_id, role_id, assigned_at) VALUES (@UserId, @RoleId, @Now);",
                    new { UserId = newId, RoleId = assignedRoleId, Now = now },
                    transaction: tx);

                await conn.ExecuteAsync(
                    """
                    INSERT INTO user_identities (
                        user_id, provider, issuer, subject_id, external_username, external_email, groups_json, last_login_at, created_at
                    ) VALUES (
                        @UserId, 'oidc', @Issuer, @Sub, @Username, @Email, @GroupsJson, @Now, @Now
                    );
                    """,
                    new { UserId = newId, Issuer = issuer, Sub = sub, Username = username, Email = email, GroupsJson = groupsJson, Now = now },
                    transaction: tx);

                return newId;
            },
            ct);

        return (await userService.GetByIdAsync(userId, ct))!;
    }

    private sealed record OidcDiscoveryDoc(
        string? Issuer,
        string? AuthorizationEndpoint,
        string? TokenEndpoint,
        string? UserinfoEndpoint,
        string? JwksUri);

    private async Task<OidcDiscoveryDoc> FetchDiscoveryDocAsync(string issuer, CancellationToken ct)
    {
        var discoveryUrl = issuer.TrimEnd('/') + "/.well-known/openid-configuration";
        var client = safeHttpClientFactory.CreateHomelabClient(allowPrivateLan: true);

        using var resp = await client.GetAsync(discoveryUrl, ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed to fetch OIDC discovery document from '{discoveryUrl}' ({resp.StatusCode}).");
        }

        using var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty discovery document.");

        var root = doc.RootElement;
        var discoveredIssuer = root.TryGetProperty("issuer", out var issProp) ? issProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(discoveredIssuer) ||
            !string.Equals(discoveredIssuer.TrimEnd('/'), issuer.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"OIDC discovery issuer '{discoveredIssuer}' does not match configured issuer '{issuer}'.");
        }

        return new OidcDiscoveryDoc(
            Issuer: discoveredIssuer,
            AuthorizationEndpoint: root.TryGetProperty("authorization_endpoint", out var authProp) ? authProp.GetString() : null,
            TokenEndpoint: root.TryGetProperty("token_endpoint", out var tokProp) ? tokProp.GetString() : null,
            UserinfoEndpoint: root.TryGetProperty("userinfo_endpoint", out var uiProp) ? uiProp.GetString() : null,
            JwksUri: root.TryGetProperty("jwks_uri", out var jwksProp) ? jwksProp.GetString() : null);
    }

    private static readonly HashSet<string> AllowedJwsAlgorithms = new(StringComparer.Ordinal)
    {
        "RS256", "RS384", "RS512", "ES256", "ES384", "ES512"
    };

    private async Task<Dictionary<string, JsonElement>> ValidateJwtAsync(
        string jwt,
        string expectedIssuer,
        string expectedAudience,
        string expectedNonce,
        string? jwksUri,
        CancellationToken ct)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            throw new SecurityException("Malformed JWT (expected 3 parts).");
        }

        var headerJson = Encoding.UTF8.GetString(Base64Url.Decode(parts[0]));
        var payloadJson = Encoding.UTF8.GetString(Base64Url.Decode(parts[1]));
        var sigBytes = Base64Url.Decode(parts[2]);
        var signedBytes = Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]);

        using var headerDoc = JsonDocument.Parse(headerJson);
        using var payloadDoc = JsonDocument.Parse(payloadJson);

        var alg = headerDoc.RootElement.TryGetProperty("alg", out var algProp) && algProp.ValueKind == JsonValueKind.String
            ? algProp.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(alg) || !AllowedJwsAlgorithms.Contains(alg))
        {
            throw new SecurityException($"Unsupported or missing JWT signature algorithm '{alg}'.");
        }

        var kid = headerDoc.RootElement.TryGetProperty("kid", out var kidProp) && kidProp.ValueKind == JsonValueKind.String
            ? kidProp.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(jwksUri))
        {
            throw new SecurityException("Missing jwks_uri in OIDC discovery document.");
        }

        var client = safeHttpClientFactory.CreateHomelabClient(allowPrivateLan: true);
        using var jwksResp = await client.GetAsync(jwksUri, ct);
        if (!jwksResp.IsSuccessStatusCode)
        {
            throw new SecurityException($"Failed to fetch JWKS from '{jwksUri}' ({jwksResp.StatusCode}).");
        }

        using var jwksDoc = await jwksResp.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct)
            ?? throw new SecurityException("Empty JWKS response.");

        var expectedKty = alg.StartsWith("RS", StringComparison.Ordinal) ? "RSA" : "EC";
        var keys = jwksDoc.RootElement.GetProperty("keys");
        JsonElement matchingKey = default;
        var found = false;

        foreach (var k in keys.EnumerateArray())
        {
            var kty = k.TryGetProperty("kty", out var kt) ? kt.GetString() : null;
            if (!string.Equals(kty, expectedKty, StringComparison.Ordinal))
            {
                continue;
            }

            if (k.TryGetProperty("use", out var useProp) &&
                useProp.ValueKind == JsonValueKind.String &&
                !string.Equals(useProp.GetString(), "sig", StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(kid))
            {
                if (k.TryGetProperty("kid", out var kKid) && string.Equals(kKid.GetString(), kid, StringComparison.Ordinal))
                {
                    matchingKey = k;
                    found = true;
                    break;
                }
            }
            else
            {
                matchingKey = k;
                found = true;
                break;
            }
        }

        if (!found)
        {
            throw new SecurityException($"No matching signing key found in JWKS for kid '{kid}' and alg '{alg}'.");
        }

        var verified = false;
        if (alg.StartsWith("RS", StringComparison.Ordinal))
        {
            var nB64 = matchingKey.GetProperty("n").GetString()!;
            var eB64 = matchingKey.GetProperty("e").GetString()!;

            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = Base64Url.Decode(nB64),
                Exponent = Base64Url.Decode(eB64)
            });

            var hashAlg = alg switch
            {
                "RS384" => HashAlgorithmName.SHA384,
                "RS512" => HashAlgorithmName.SHA512,
                _ => HashAlgorithmName.SHA256
            };
            verified = rsa.VerifyData(signedBytes, sigBytes, hashAlg, RSASignaturePadding.Pkcs1);
        }
        else if (alg.StartsWith("ES", StringComparison.Ordinal))
        {
            var crv = matchingKey.GetProperty("crv").GetString()!;
            var xB64 = matchingKey.GetProperty("x").GetString()!;
            var yB64 = matchingKey.GetProperty("y").GetString()!;

            var (expectedCrv, curve, hashAlg) = alg switch
            {
                "ES256" => ("P-256", ECCurve.NamedCurves.nistP256, HashAlgorithmName.SHA256),
                "ES384" => ("P-384", ECCurve.NamedCurves.nistP384, HashAlgorithmName.SHA384),
                "ES512" => ("P-521", ECCurve.NamedCurves.nistP521, HashAlgorithmName.SHA512),
                _ => throw new SecurityException($"Unsupported EC algorithm '{alg}'.")
            };

            if (!string.Equals(crv, expectedCrv, StringComparison.Ordinal))
            {
                throw new SecurityException($"JWKS EC curve '{crv}' does not match algorithm '{alg}'.");
            }

            using var ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = curve,
                Q = new ECPoint
                {
                    X = Base64Url.Decode(xB64),
                    Y = Base64Url.Decode(yB64)
                }
            });

            verified = ecdsa.VerifyData(signedBytes, sigBytes, hashAlg, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        if (!verified)
        {
            throw new SecurityException("JWT ID token signature verification failed.");
        }

        var claims = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in payloadDoc.RootElement.EnumerateObject())
        {
            claims[prop.Name] = prop.Value.Clone();
        }

        // Validate mandatory OIDC Core 3.1.3.7 claims unconditionally
        if (!claims.TryGetValue("iss", out var issEl) || issEl.ValueKind != JsonValueKind.String)
        {
            throw new SecurityException("JWT missing required 'iss' claim.");
        }
        var iss = issEl.GetString()?.TrimEnd('/');
        if (!string.Equals(iss, expectedIssuer.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"JWT issuer '{iss}' does not match expected '{expectedIssuer}'.");
        }

        if (!claims.TryGetValue("sub", out var subClaimEl) || subClaimEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(subClaimEl.GetString()))
        {
            throw new SecurityException("JWT missing required 'sub' claim.");
        }

        if (!claims.TryGetValue("aud", out var audEl))
        {
            throw new SecurityException("JWT missing required 'aud' claim.");
        }

        var audMatches = false;
        var audCount = 0;
        if (audEl.ValueKind == JsonValueKind.String)
        {
            audCount = 1;
            audMatches = string.Equals(audEl.GetString(), expectedAudience, StringComparison.Ordinal);
        }
        else if (audEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in audEl.EnumerateArray())
            {
                audCount++;
                if (string.Equals(a.GetString(), expectedAudience, StringComparison.Ordinal))
                {
                    audMatches = true;
                }
            }
        }
        if (!audMatches)
        {
            throw new SecurityException($"JWT audience does not contain expected client ID '{expectedAudience}'.");
        }

        if (audCount > 1 || claims.ContainsKey("azp"))
        {
            if (!claims.TryGetValue("azp", out var azpEl) ||
                azpEl.ValueKind != JsonValueKind.String ||
                !string.Equals(azpEl.GetString(), expectedAudience, StringComparison.Ordinal))
            {
                throw new SecurityException($"JWT 'azp' claim does not match expected client ID '{expectedAudience}'.");
            }
        }

        if (!claims.TryGetValue("exp", out var expEl) || !expEl.TryGetInt64(out var exp))
        {
            throw new SecurityException("JWT missing or invalid required 'exp' claim.");
        }
        if (DateTimeOffset.FromUnixTimeSeconds(exp) < DateTimeOffset.UtcNow.AddMinutes(-5))
        {
            throw new SecurityException("JWT has expired.");
        }

        if (claims.TryGetValue("nbf", out var nbfEl))
        {
            if (!nbfEl.TryGetInt64(out var nbf) || DateTimeOffset.FromUnixTimeSeconds(nbf) > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                throw new SecurityException("JWT is not yet valid ('nbf').");
            }
        }

        if (!claims.TryGetValue("nonce", out var nonceEl) || nonceEl.ValueKind != JsonValueKind.String)
        {
            throw new SecurityException("JWT missing required 'nonce' claim.");
        }
        var actualNonce = nonceEl.GetString() ?? "";
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(actualNonce),
                Encoding.UTF8.GetBytes(expectedNonce)))
        {
            throw new SecurityException("JWT nonce mismatch.");
        }

        return claims;
    }

    public async Task<(bool Success, string? Error, string? Issuer, string? AuthorizationEndpoint, string? TokenEndpoint, string? JwksUri)> TestConnectionAsync(
        string? issuerOverride = null,
        string? clientIdOverride = null,
        string? clientSecretOverride = null,
        CancellationToken ct = default)
    {
        var cfg = await ResolveEffectiveConfigAsync(ct);
        var targetIssuer = !string.IsNullOrWhiteSpace(issuerOverride) ? issuerOverride.Trim() : cfg.Issuer;

        if (string.IsNullOrWhiteSpace(targetIssuer))
        {
            return (false, "Issuer URL is not configured.", null, null, null, null);
        }

        try
        {
            var discovery = await FetchDiscoveryDocAsync(targetIssuer, ct);
            if (string.IsNullOrWhiteSpace(discovery.JwksUri))
            {
                return (false, "Discovery metadata missing jwks_uri.", discovery.Issuer, discovery.AuthorizationEndpoint, discovery.TokenEndpoint, null);
            }

            var client = safeHttpClientFactory.CreateHomelabClient(allowPrivateLan: true);
            using var jwksResp = await client.GetAsync(discovery.JwksUri, ct);
            if (!jwksResp.IsSuccessStatusCode)
            {
                return (false, $"JWKS URI returned HTTP {jwksResp.StatusCode}.", discovery.Issuer, discovery.AuthorizationEndpoint, discovery.TokenEndpoint, discovery.JwksUri);
            }

            return (true, null, discovery.Issuer, discovery.AuthorizationEndpoint, discovery.TokenEndpoint, discovery.JwksUri);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, null, null, null);
        }
    }
}
