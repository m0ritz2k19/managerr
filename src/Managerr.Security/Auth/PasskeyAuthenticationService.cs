using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Managerr.Common.Configuration;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Security.Users;
using Microsoft.Extensions.Options;

namespace Managerr.Security.Auth;

public sealed class PasskeyNotFoundException(string rpId) : Exception($"Passkey credential was not found for rpId '{rpId}'.")
{
    public string RpId { get; } = rpId;
}

public sealed record PasskeyCredentialDto(
    string Id,
    string Name,
    string Aaguid,
    string CredentialDeviceType,
    bool CredentialBackedUp,
    IReadOnlyList<string> Transports,
    long RegisteredAt,
    long? LastUsedAt);

public sealed record PasskeyListResult(
    string RpId,
    string UserId,
    IReadOnlyList<PasskeyCredentialDto> Credentials);

public interface IPasskeyAuthenticationService
{
    Task<string> GetRegistrationOptionsAsync(PlexUserSummary user, bool isPromotion, string callerHost, string callerScheme, CancellationToken ct = default);
    Task<PasskeyCredentialDto> VerifyRegistrationAsync(
        PlexUserSummary user,
        AuthenticatorAttestationRawResponse rawResponse,
        string? customName,
        string callerHost,
        string callerScheme,
        string? ipAddress,
        CancellationToken ct = default);
    Task<string> GetLoginOptionsAsync(string callerHost, string callerScheme, CancellationToken ct = default);
    Task<(IssuedSession Session, PlexUserSummary User)> VerifyLoginAsync(
        AuthenticatorAssertionRawResponse rawResponse,
        string callerHost,
        string callerScheme,
        string? userAgent,
        string? ipAddress,
        CancellationToken ct = default);
    Task<PasskeyListResult> ListCredentialsAsync(PlexUserSummary user, string callerHost, CancellationToken ct = default);
    Task<PasskeyCredentialDto> RenameCredentialAsync(PlexUserSummary user, string credentialId, string newName, CancellationToken ct = default);
    Task<bool> DeleteCredentialAsync(PlexUserSummary user, string credentialId, CancellationToken ct = default);
}

public sealed class PasskeyAuthenticationService(
    ISqliteDbFactory dbFactory,
    IAppSettingsService appSettings,
    IOptionsMonitor<ManagerrAuthOptions> authOptions,
    IPlexUserService userService,
    IPlexAuthAndSessionService sessionService,
    IAuditLogService auditLog,
    IConfigSourceInspector? inspector = null) : IPasskeyAuthenticationService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private async Task<(string RpId, string RpName, HashSet<string> Origins, bool RequireUv, Fido2 Fido2)> CreateFido2Async(string callerHost, string callerScheme, CancellationToken ct)
    {
        var opt = authOptions.CurrentValue.Passkeys;
        var dbRpId = await appSettings.GetAsync("auth.passkeys.rp_id", ct);
        var dbRpName = await appSettings.GetAsync("auth.passkeys.rp_name", ct);
        var dbRequireUv = await appSettings.GetAsync("auth.passkeys.require_user_verification", ct);

        // Normalize callerHost (strip port if present for rpId)
        var hostName = callerHost;
        var colonIdx = hostName.IndexOf(':');
        if (colonIdx > 0)
        {
            hostName = hostName[..colonIdx];
        }

        var rpIdSource = inspector?.GetSource("passkeys.rp_id") ?? "database";
        var rpId = rpIdSource == "database" && !string.IsNullOrWhiteSpace(dbRpId)
            ? dbRpId.Trim()
            : (!string.IsNullOrWhiteSpace(opt.RpId) ? opt.RpId.Trim() : hostName);

        var rpNameSource = inspector?.GetSource("passkeys.rp_name") ?? "database";
        var rpName = rpNameSource == "database" && !string.IsNullOrWhiteSpace(dbRpName)
            ? dbRpName.Trim()
            : (!string.IsNullOrWhiteSpace(opt.RpName) ? opt.RpName.Trim() : "Managerr");

        var uvSource = inspector?.GetSource("passkeys.require_user_verification") ?? "database";
        var requireUv = uvSource == "database" && bool.TryParse(dbRequireUv, out var parsedUv)
            ? parsedUv
            : opt.RequireUserVerification;

        var scheme = !string.IsNullOrWhiteSpace(callerScheme) ? callerScheme.ToLowerInvariant() : "http";
        var defaultOrigin = $"{scheme}://{callerHost}";

        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (opt.Origins is { Length: > 0 })
        {
            foreach (var o in opt.Origins)
            {
                if (!string.IsNullOrWhiteSpace(o)) origins.Add(o.Trim());
            }
        }
        if (origins.Count == 0)
        {
            origins.Add(defaultOrigin);
        }

        var fido2 = new Fido2(new Fido2Configuration
        {
            RPID = rpId,
            RPName = rpName,
            Origins = origins
        });

        return (rpId, rpName, origins, requireUv, fido2);
    }

    public async Task<string> GetRegistrationOptionsAsync(PlexUserSummary user, bool isPromotion, string callerHost, string callerScheme, CancellationToken ct = default)
    {
        var (_, _, _, requireUv, fido2) = await CreateFido2Async(callerHost, callerScheme, ct);

        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var existingCredIds = (await conn.QueryAsync<string>(
            "SELECT credential_id FROM webauthn_credentials WHERE user_id = @UserId;",
            new { UserId = user.Id })).ToList();

        var excludeList = existingCredIds
            .Select(cId => new PublicKeyCredentialDescriptor(Base64Url.Decode(cId)))
            .ToList();

        var passkeyUserIdBytes = !string.IsNullOrEmpty(user.PasskeyUserId)
            ? Base64Url.Decode(user.PasskeyUserId)
            : RandomNumberGenerator.GetBytes(16);

        var fidoUser = new Fido2User
        {
            Id = passkeyUserIdBytes,
            Name = user.Username,
            DisplayName = user.DisplayName ?? user.Username
        };

        var authenticatorSelection = new AuthenticatorSelection
        {
            ResidentKey = ResidentKeyRequirement.Required,
            UserVerification = requireUv ? UserVerificationRequirement.Required : UserVerificationRequirement.Preferred,
            AuthenticatorAttachment = isPromotion ? AuthenticatorAttachment.Platform : null
        };

        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = fidoUser,
            ExcludeCredentials = excludeList,
            AuthenticatorSelection = authenticatorSelection,
            AttestationPreference = AttestationConveyancePreference.None
        });

        var challengeKey = Base64Url.Encode(options.Challenge);
        var optionsJson = options.ToJson();
        var now = DateTimeOffset.UtcNow;
        var nowIso = now.ToString("O");
        var expiresAt = now.AddMinutes(5).ToString("O");

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Cache,
            async (c, tx) =>
            {
                await c.ExecuteAsync("DELETE FROM auth_challenges WHERE expires_at <= @Now;", new { Now = nowIso }, transaction: tx);
                await c.ExecuteAsync(
                    """
                    INSERT INTO auth_challenges (challenge_key, kind, payload_json, expires_at, consumed_at)
                    VALUES (@Key, 'passkey_register', @Payload, @ExpiresAt, NULL)
                    ON CONFLICT(challenge_key) DO UPDATE SET
                        payload_json = excluded.payload_json,
                        expires_at = excluded.expires_at,
                        consumed_at = NULL;
                    """,
                    new { Key = challengeKey, Payload = optionsJson, ExpiresAt = expiresAt },
                    transaction: tx);
            },
            ct);

        return optionsJson;
    }

    public async Task<PasskeyCredentialDto> VerifyRegistrationAsync(
        PlexUserSummary user,
        AuthenticatorAttestationRawResponse rawResponse,
        string? customName,
        string callerHost,
        string callerScheme,
        string? ipAddress,
        CancellationToken ct = default)
    {
        var (_, _, _, _, fido2) = await CreateFido2Async(callerHost, callerScheme, ct);

        var clientDataJson = Encoding.UTF8.GetString(rawResponse.Response.ClientDataJson);
        using var doc = JsonDocument.Parse(clientDataJson);
        var challengeB64 = doc.RootElement.GetProperty("challenge").GetString()!;

        var now = DateTimeOffset.UtcNow.ToString("O");
        string? challengePayload = null;

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Cache,
            async (conn, tx) =>
            {
                challengePayload = await conn.QuerySingleOrDefaultAsync<string>(
                    """
                    SELECT payload_json FROM auth_challenges
                    WHERE challenge_key = @Key AND kind = 'passkey_register' AND consumed_at IS NULL AND expires_at > @Now;
                    """,
                    new { Key = challengeB64, Now = now },
                    transaction: tx);

                if (challengePayload != null)
                {
                    await conn.ExecuteAsync(
                        "UPDATE auth_challenges SET consumed_at = @Now WHERE challenge_key = @Key;",
                        new { Key = challengeB64, Now = now },
                        transaction: tx);
                }
            },
            ct);

        if (challengePayload is null)
        {
            throw new InvalidOperationException("Invalid or expired passkey registration challenge.");
        }

        var origOptions = CredentialCreateOptions.FromJson(challengePayload);
        if (!string.IsNullOrEmpty(user.PasskeyUserId))
        {
            var expectedUserIdBytes = Base64Url.Decode(user.PasskeyUserId);
            if (!origOptions.User.Id.AsSpan().SequenceEqual(expectedUserIdBytes))
            {
                throw new InvalidOperationException("Passkey registration challenge was issued for a different user.");
            }
        }

        var result = await fido2.MakeNewCredentialAsync(
            new MakeNewCredentialParams
            {
                AttestationResponse = rawResponse,
                OriginalOptions = origOptions,
                IsCredentialIdUniqueToUserCallback = async (args, cToken) =>
                {
                    var cId = Base64Url.Encode(args.CredentialId);
                    await using var c = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, cToken);
                    var count = await c.ExecuteScalarAsync<int>(
                        "SELECT COUNT(1) FROM webauthn_credentials WHERE credential_id = @CredId;",
                        new { CredId = cId });
                    return count == 0;
                }
            },
            cancellationToken: ct);

        var credentialId = Base64Url.Encode(result.Id);
        var publicKeyB64 = Convert.ToBase64String(result.PublicKey);
        var signCount = (long)result.SignCount;
        var devType = result.Type.ToString();
        var isBackedUp = result.IsBackedUp ? 1 : 0;
        var aaguid = result.AaGuid.ToString("D");
        var transports = result.Transports?.Select(t => t.ToString()).ToArray() ?? [];
        var transportsJson = JsonSerializer.Serialize(transports, JsonOpts);
        var registeredAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var name = !string.IsNullOrWhiteSpace(customName)
            ? customName.Trim()
            : (result.IsBackedUp ? "Passkey" : "Security Key");

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                INSERT INTO webauthn_credentials (
                    credential_id, user_id, public_key_b64, sign_count, device_type,
                    is_backed_up, aaguid, name, transports_json, registered_at, last_used_at
                ) VALUES (
                    @CredentialId, @UserId, @PublicKeyB64, @SignCount, @DeviceType,
                    @IsBackedUp, @Aaguid, @Name, @TransportsJson, @RegisteredAt, NULL
                );
                """,
                new
                {
                    CredentialId = credentialId,
                    UserId = user.Id,
                    PublicKeyB64 = publicKeyB64,
                    SignCount = signCount,
                    DeviceType = devType,
                    IsBackedUp = isBackedUp,
                    Aaguid = aaguid,
                    Name = name,
                    TransportsJson = transportsJson,
                    RegisteredAt = registeredAt
                },
                transaction: tx),
            ct);

        await auditLog.RecordAsync(new AuditWriteRequest(
            Action: "auth.passkeys.registered",
            ResourceType: "passkey",
            ResourceId: credentialId,
            Details: new { name, aaguid, devType, isBackedUp },
            ActorUserId: user.Id,
            ActorUsername: user.Username,
            IpAddress: ipAddress), ct);

        return new PasskeyCredentialDto(
            Id: credentialId,
            Name: name,
            Aaguid: aaguid,
            CredentialDeviceType: devType,
            CredentialBackedUp: result.IsBackedUp,
            Transports: transports,
            RegisteredAt: registeredAt,
            LastUsedAt: null);
    }

    public async Task<string> GetLoginOptionsAsync(string callerHost, string callerScheme, CancellationToken ct = default)
    {
        var (_, _, _, requireUv, fido2) = await CreateFido2Async(callerHost, callerScheme, ct);

        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = requireUv ? UserVerificationRequirement.Required : UserVerificationRequirement.Preferred
        });

        var challengeKey = Base64Url.Encode(options.Challenge);
        var optionsJson = options.ToJson();
        var now = DateTimeOffset.UtcNow;
        var nowIso = now.ToString("O");
        var expiresAt = now.AddMinutes(5).ToString("O");

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Cache,
            async (c, tx) =>
            {
                await c.ExecuteAsync("DELETE FROM auth_challenges WHERE expires_at <= @Now;", new { Now = nowIso }, transaction: tx);
                await c.ExecuteAsync(
                    """
                    INSERT INTO auth_challenges (challenge_key, kind, payload_json, expires_at, consumed_at)
                    VALUES (@Key, 'passkey_login', @Payload, @ExpiresAt, NULL)
                    ON CONFLICT(challenge_key) DO UPDATE SET
                        payload_json = excluded.payload_json,
                        expires_at = excluded.expires_at,
                        consumed_at = NULL;
                    """,
                    new { Key = challengeKey, Payload = optionsJson, ExpiresAt = expiresAt },
                    transaction: tx);
            },
            ct);

        return optionsJson;
    }

    public async Task<(IssuedSession Session, PlexUserSummary User)> VerifyLoginAsync(
        AuthenticatorAssertionRawResponse rawResponse,
        string callerHost,
        string callerScheme,
        string? userAgent,
        string? ipAddress,
        CancellationToken ct = default)
    {
        var (rpId, _, _, _, fido2) = await CreateFido2Async(callerHost, callerScheme, ct);

        var credId = !string.IsNullOrWhiteSpace(rawResponse.Id) ? rawResponse.Id : Base64Url.Encode(rawResponse.RawId);

        WebauthnCredentialRow? credRow;
        await using (var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            credRow = await conn.QuerySingleOrDefaultAsync<WebauthnCredentialRow>(
                "SELECT * FROM webauthn_credentials WHERE credential_id = @CredId;",
                new { CredId = credId });
        }

        if (credRow is null)
        {
            throw new PasskeyNotFoundException(rpId);
        }

        var user = await userService.GetByIdAsync(credRow.UserId, ct)
            ?? throw new InvalidOperationException($"User #{credRow.UserId} not found.");
        if (!user.IsActive)
        {
            throw new UnauthorizedAccessException("User account is inactive.");
        }

        var clientDataJson = Encoding.UTF8.GetString(rawResponse.Response.ClientDataJson);
        using var doc = JsonDocument.Parse(clientDataJson);
        var challengeB64 = doc.RootElement.GetProperty("challenge").GetString()!;

        var now = DateTimeOffset.UtcNow.ToString("O");
        string? challengePayload = null;

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Cache,
            async (conn, tx) =>
            {
                challengePayload = await conn.QuerySingleOrDefaultAsync<string>(
                    """
                    SELECT payload_json FROM auth_challenges
                    WHERE challenge_key = @Key AND kind = 'passkey_login' AND consumed_at IS NULL AND expires_at > @Now;
                    """,
                    new { Key = challengeB64, Now = now },
                    transaction: tx);

                if (challengePayload != null)
                {
                    await conn.ExecuteAsync(
                        "UPDATE auth_challenges SET consumed_at = @Now WHERE challenge_key = @Key;",
                        new { Key = challengeB64, Now = now },
                        transaction: tx);
                }
            },
            ct);

        if (challengePayload is null)
        {
            throw new InvalidOperationException("Invalid or expired passkey assertion challenge.");
        }

        var origOptions = AssertionOptions.FromJson(challengePayload);

        var res = await fido2.MakeAssertionAsync(
            new MakeAssertionParams
            {
                AssertionResponse = rawResponse,
                OriginalOptions = origOptions,
                StoredPublicKey = Convert.FromBase64String(credRow.PublicKeyB64),
                StoredSignatureCounter = (uint)credRow.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                {
                    if (args.UserHandle is null || args.UserHandle.Length == 0)
                    {
                        return Task.FromResult(false);
                    }
                    var handleB64 = Base64Url.Encode(args.UserHandle);
                    return Task.FromResult(string.Equals(handleB64, user.PasskeyUserId, StringComparison.Ordinal));
                }
            },
            cancellationToken: ct);

        var newSignCount = (long)res.SignCount;
        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                UPDATE webauthn_credentials
                SET sign_count = @SignCount,
                    last_used_at = @NowSec
                WHERE credential_id = @CredId;
                """,
                new { SignCount = newSignCount, NowSec = nowSec, CredId = credId },
                transaction: tx),
            ct);

        var session = await sessionService.CreateSessionForUserAsync(user, userAgent, ipAddress, ct);

        await auditLog.RecordAsync(new AuditWriteRequest(
            Action: "auth.passkeys.login",
            ResourceType: "user",
            ResourceId: user.Id.ToString(),
            Details: new { credId, user.Username },
            ActorUserId: user.Id,
            ActorUsername: user.Username,
            IpAddress: ipAddress), ct);

        return (session, user);
    }

    public async Task<PasskeyListResult> ListCredentialsAsync(PlexUserSummary user, string callerHost, CancellationToken ct = default)
    {
        var (rpId, _, _, _, _) = await CreateFido2Async(callerHost, "https", ct);

        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = (await conn.QueryAsync<WebauthnCredentialRow>(
            "SELECT * FROM webauthn_credentials WHERE user_id = @UserId ORDER BY registered_at DESC;",
            new { UserId = user.Id })).ToList();

        var dtos = rows.Select(r => new PasskeyCredentialDto(
            Id: r.CredentialId,
            Name: r.Name,
            Aaguid: r.Aaguid,
            CredentialDeviceType: r.DeviceType,
            CredentialBackedUp: r.IsBackedUp != 0,
            Transports: DeserializeTransports(r.TransportsJson),
            RegisteredAt: r.RegisteredAt,
            LastUsedAt: r.LastUsedAt)).ToList();

        return new PasskeyListResult(
            RpId: rpId,
            UserId: user.PasskeyUserId,
            Credentials: dtos);
    }

    public async Task<PasskeyCredentialDto> RenameCredentialAsync(PlexUserSummary user, string credentialId, string newName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("Passkey name cannot be empty.", nameof(newName));
        }

        var affected = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                "UPDATE webauthn_credentials SET name = @Name WHERE credential_id = @CredId AND user_id = @UserId;",
                new { Name = newName.Trim(), CredId = credentialId, UserId = user.Id },
                transaction: tx),
            ct);

        if (affected == 0)
        {
            throw new KeyNotFoundException($"Credential '{credentialId}' not found for user.");
        }

        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var r = await readConn.QuerySingleAsync<WebauthnCredentialRow>(
            "SELECT * FROM webauthn_credentials WHERE credential_id = @CredId;",
            new { CredId = credentialId });

        return new PasskeyCredentialDto(
            Id: r.CredentialId,
            Name: r.Name,
            Aaguid: r.Aaguid,
            CredentialDeviceType: r.DeviceType,
            CredentialBackedUp: r.IsBackedUp != 0,
            Transports: DeserializeTransports(r.TransportsJson),
            RegisteredAt: r.RegisteredAt,
            LastUsedAt: r.LastUsedAt);
    }

    public async Task<bool> DeleteCredentialAsync(PlexUserSummary user, string credentialId, CancellationToken ct = default)
    {
        var affected = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                "DELETE FROM webauthn_credentials WHERE credential_id = @CredId AND user_id = @UserId;",
                new { CredId = credentialId, UserId = user.Id },
                transaction: tx),
            ct);

        if (affected == 0)
        {
            throw new KeyNotFoundException($"Credential '{credentialId}' not found for user.");
        }

        return true;
    }

    private static IReadOnlyList<string> DeserializeTransports(string json)
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

    private sealed class WebauthnCredentialRow
    {
        public long Id { get; init; }
        public string CredentialId { get; init; } = "";
        public long UserId { get; init; }
        public string PublicKeyB64 { get; init; } = "";
        public long SignCount { get; init; }
        public string DeviceType { get; init; } = "";
        public int IsBackedUp { get; init; }
        public string Aaguid { get; init; } = "";
        public string Name { get; init; } = "";
        public string TransportsJson { get; init; } = "[]";
        public long RegisteredAt { get; init; }
        public long? LastUsedAt { get; init; }
    }
}
