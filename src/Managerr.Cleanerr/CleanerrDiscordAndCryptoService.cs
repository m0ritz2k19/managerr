using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Managerr.Common.Net;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Managerr.Cleanerr;

public interface ICleanerrHttpGateway
{
    Task<(int StatusCode, string Body)> SendJsonAsync(
        HttpMethod method,
        string url,
        string? jsonBody = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default);
}

public sealed class CleanerrHttpGateway(ISafeHttpClientFactory httpFactory) : ICleanerrHttpGateway
{
    public async Task<(int StatusCode, string Body)> SendJsonAsync(
        HttpMethod method,
        string url,
        string? jsonBody = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        var client = httpFactory.CreateHomelabClient(allowPrivateLan: true, ignoreSslErrors: true);
        using var req = new HttpRequestMessage(method, url);
        if (jsonBody is not null)
        {
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                req.Headers.TryAddWithoutValidation(k, v);
            }
        }

        using var resp = await client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return ((int)resp.StatusCode, body);
    }
}

/// <summary>
/// RFC 8032 Ed25519 signature verifier &amp; deterministic keypair generator backed by BouncyCastle.Cryptography,
/// plus constant-time HMAC-SHA256 single-use expiring magic token signer for Discord Interactions &amp; Cleanerr 1-click links.
/// </summary>
public static class CleanerrCrypto
{
    public const string DefaultDiscordAppId = "";
    public const string DefaultDiscordPublicKeyHex = "";
    public const int DefaultDiscordReplayWindowSeconds = 300;
    public static readonly TimeSpan DefaultMagicTokenLifetime = TimeSpan.FromDays(7);

    private static readonly BigInteger P = (BigInteger.One << 255) - 19;
    private static readonly BigInteger L = (BigInteger.One << 252) + BigInteger.Parse("27742317777372353535851937790883648493", CultureInfo.InvariantCulture);
    private static readonly ConcurrentDictionary<string, long> ConsumedMagicTokens = new(StringComparer.Ordinal);

    public static string SignMagicToken(
        string secretKey,
        string target,
        DateTimeOffset? expiresAt = null,
        string? nonce = null)
    {
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            throw new ArgumentException("Secret key is required.", nameof(secretKey));
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("Target is required.", nameof(target));
        }

        var expUnix = (expiresAt ?? DateTimeOffset.UtcNow.Add(DefaultMagicTokenLifetime)).ToUnixTimeSeconds();
        var tokenNonce = string.IsNullOrWhiteSpace(nonce)
            ? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))
            : nonce.Trim().ToLowerInvariant();
        var keyBytes = Encoding.UTF8.GetBytes(secretKey);
        var msgBytes = Encoding.UTF8.GetBytes($"{target}|{expUnix}|{tokenNonce}");
        var mac = HMACSHA256.HashData(keyBytes, msgBytes);
        return $"{expUnix}.{tokenNonce}.{Convert.ToHexStringLower(mac)}";
    }

    public static bool VerifyMagicToken(
        string secretKey,
        string target,
        string? signature,
        DateTimeOffset? referenceNow = null,
        bool consumeToken = false) =>
        VerifyMagicTokenCore(secretKey, target, signature, referenceNow, consumeToken, out _, out _);

    public static bool TryConsumeMagicToken(
        string secretKey,
        string target,
        string? signature,
        DateTimeOffset? referenceNow = null) =>
        VerifyMagicTokenCore(secretKey, target, signature, referenceNow, consumeToken: true, out _, out _);

    internal static bool VerifyMagicTokenCore(
        string secretKey,
        string target,
        string? signature,
        DateTimeOffset? referenceNow,
        bool consumeToken,
        out string tokenHashHex,
        out long expUnix)
    {
        tokenHashHex = "";
        expUnix = 0;

        if (string.IsNullOrWhiteSpace(secretKey) ||
            string.IsNullOrWhiteSpace(target) ||
            string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        var parts = signature.Trim().Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var expStr = parts[0];
        var nonceStr = parts[1].Trim().ToLowerInvariant();
        var macHex = parts[2].Trim();

        if (!long.TryParse(expStr, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out expUnix) ||
            nonceStr.Length < 4 ||
            macHex.Length != 64)
        {
            return false;
        }

        byte[] actualDigest;
        try
        {
            actualDigest = Convert.FromHexString(macHex);
        }
        catch (FormatException)
        {
            return false;
        }

        var keyBytes = Encoding.UTF8.GetBytes(secretKey);
        var canonicalPayload = $"{target}|{expUnix}|{nonceStr}";
        var expectedDigest = HMACSHA256.HashData(keyBytes, Encoding.UTF8.GetBytes(canonicalPayload));

        if (actualDigest.Length != expectedDigest.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedDigest, actualDigest))
        {
            return false;
        }

        var nowUnix = (referenceNow ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        if (expUnix <= nowUnix)
        {
            return false;
        }

        tokenHashHex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload)));

        if (ConsumedMagicTokens.ContainsKey(tokenHashHex))
        {
            return false;
        }

        if (consumeToken && !ConsumedMagicTokens.TryAdd(tokenHashHex, expUnix))
        {
            return false;
        }

        return true;
    }

    public static bool VerifyEd25519SignatureHex(
        string publicKeyHex,
        string signatureHex,
        byte[] message)
    {
        if (string.IsNullOrWhiteSpace(publicKeyHex) ||
            string.IsNullOrWhiteSpace(signatureHex) ||
            message is null)
        {
            return false;
        }

        byte[] pubBytes;
        byte[] sigBytes;
        try
        {
            pubBytes = Convert.FromHexString(publicKeyHex.Trim());
            sigBytes = Convert.FromHexString(signatureHex.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        return VerifyEd25519Signature(pubBytes, sigBytes, message);
    }

    public static bool VerifyEd25519Signature(
        byte[] pubBytes,
        byte[] sigBytes,
        byte[] message)
    {
        if (pubBytes is not { Length: 32 } ||
            sigBytes is not { Length: 64 } ||
            message is null)
        {
            return false;
        }

        // Strict RFC 8032 canonical scalar check: 0 <= S < L
        var s = new BigInteger(sigBytes.AsSpan(32, 32), isUnsigned: true, isBigEndian: false);
        if (s >= L)
        {
            return false;
        }

        // Strict RFC 8032 canonical point encoding check on A (public key) and R (sig[0..32])
        if (!IsCanonicalPointEncoding(pubBytes) || !IsCanonicalPointEncoding(sigBytes.AsSpan(0, 32)))
        {
            return false;
        }

        try
        {
            var pubKeyParams = new Ed25519PublicKeyParameters(pubBytes, 0);
            var verifier = new Ed25519Signer();
            verifier.Init(forSigning: false, pubKeyParams);
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(sigBytes);
        }
        catch
        {
            return false;
        }
    }

    public static bool VerifyDiscordEd25519Signature(
        string publicKeyHex,
        string signatureHex,
        string timestamp,
        byte[] rawBody,
        int maxClockSkewSeconds = DefaultDiscordReplayWindowSeconds,
        DateTimeOffset? referenceNow = null)
    {
        if (string.IsNullOrWhiteSpace(publicKeyHex) ||
            string.IsNullOrWhiteSpace(signatureHex) ||
            string.IsNullOrWhiteSpace(timestamp) ||
            rawBody is null)
        {
            return false;
        }

        var trimmedTs = timestamp.Trim();
        if (!long.TryParse(trimmedTs, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            return false;
        }

        if (maxClockSkewSeconds > 0)
        {
            var now = referenceNow ?? DateTimeOffset.UtcNow;
            DateTimeOffset tsDto;
            try
            {
                tsDto = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }

            if (Math.Abs((now - tsDto).TotalSeconds) > maxClockSkewSeconds)
            {
                return false;
            }
        }

        var tsBytes = Encoding.UTF8.GetBytes(trimmedTs);
        var message = new byte[tsBytes.Length + rawBody.Length];
        Buffer.BlockCopy(tsBytes, 0, message, 0, tsBytes.Length);
        Buffer.BlockCopy(rawBody, 0, message, tsBytes.Length, rawBody.Length);

        return VerifyEd25519SignatureHex(publicKeyHex, signatureHex, message);
    }

    public static (string PublicKeyHex, byte[] Seed32) GenerateEd25519KeyPair(byte[]? deterministicSeed = null)
    {
        var seed = deterministicSeed is { Length: 32 }
            ? (byte[])deterministicSeed.Clone()
            : RandomNumberGenerator.GetBytes(32);
        var privKey = new Ed25519PrivateKeyParameters(seed, 0);
        var pubKey = privKey.GeneratePublicKey();
        return (Convert.ToHexStringLower(pubKey.GetEncoded()), seed);
    }

    public static string SignEd25519(byte[] seed32, byte[] message)
    {
        ArgumentNullException.ThrowIfNull(seed32);
        ArgumentNullException.ThrowIfNull(message);
        if (seed32.Length != 32)
        {
            throw new ArgumentException("Ed25519 private seed must be 32 bytes.", nameof(seed32));
        }

        var privKey = new Ed25519PrivateKeyParameters(seed32, 0);
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, privKey);
        signer.BlockUpdate(message, 0, message.Length);
        return Convert.ToHexStringLower(signer.GenerateSignature());
    }

    public static string SignDiscordEd25519Payload(byte[] seed32, string timestamp, byte[] rawBody)
    {
        var tsBytes = Encoding.UTF8.GetBytes(timestamp);
        var message = new byte[tsBytes.Length + rawBody.Length];
        Buffer.BlockCopy(tsBytes, 0, message, 0, tsBytes.Length);
        Buffer.BlockCopy(rawBody, 0, message, tsBytes.Length, rawBody.Length);
        return SignEd25519(seed32, message);
    }

    private static bool IsCanonicalPointEncoding(ReadOnlySpan<byte> encoded32)
    {
        if (encoded32.Length != 32)
        {
            return false;
        }

        Span<byte> copy = stackalloc byte[32];
        encoded32.CopyTo(copy);
        var sign = (copy[31] >> 7) & 1;
        copy[31] &= 0x7F;
        var y = new BigInteger(copy, isUnsigned: true, isBigEndian: false);
        if (y >= P)
        {
            return false;
        }

        // Reject non-canonical negative zero (x == 0 with sign bit == 1 when y == 1 or y == P - 1)
        if ((y == BigInteger.One || y == P - BigInteger.One) && sign == 1)
        {
            return false;
        }

        return true;
    }
}

public sealed record CleanerrDiscordRuntimeCallbacks(
    Func<CancellationToken, Task<CleanerrOverviewStatsDto>> GetOverviewStatsAsync,
    Func<CancellationToken, Task<IReadOnlyList<CleanerrMediaItem>>> ListMediaItemsAsync,
    Func<string?, CancellationToken, Task<IReadOnlyList<CleanerrDeletionQueueItem>>> ListQueueAsync,
    Func<CancellationToken, Task<IReadOnlyList<CleanerrExclusionRule>>> ListExclusionsAsync,
    Func<IReadOnlyList<long>, string, string, long?, int?, string?, CancellationToken, Task<IReadOnlyList<string>>> ExcludeMediaIdsAsync,
    Func<long?, string, long?, int?, CancellationToken, Task<int>> ExcludeAllStagedAsync,
    Func<IReadOnlyList<long>, int, string, CancellationToken, Task<IReadOnlyList<CleanerrDeletionQueueItem>>> PostponeMediaIdsAsync,
    Func<long?, int, string, CancellationToken, Task<int>> PostponeAllStagedAsync,
    Func<long, CancellationToken, Task<CleanerrTestMediaResult>> ExplainMediaAsync,
    Func<long, CancellationToken, Task> DeleteExclusionAsync,
    Func<long?, bool?, CancellationToken, Task<CleanerrWorkflowRunResult?>>? RunWorkflowAsync = null,
    Func<CancellationToken, Task<IReadOnlyList<CleanerrWorkflowSummary>>>? ListWorkflowsAsync = null);

public interface ICleanerrDiscordService
{
    bool IsGatewayConnected { get; set; }
    string? GatewayBotUsername { get; }
    string? GatewayLastError { get; }
    event Action? ConfigChanged;
    void SetGatewayStatus(bool connected, string? botUsername = null, string? lastError = null);
    void RequestGatewayReconnect();

    Task<CleanerrDiscordSettings> GetSettingsAsync(CancellationToken ct = default);
    Task<CleanerrDiscordSettings> UpdateSettingsAsync(UpdateCleanerrDiscordSettingsRequest request, CancellationToken ct = default);
    Task<string> GetMagicSecretAsync(CancellationToken ct = default);
    Task<bool> VerifyAndConsumeMagicTokenAsync(string target, string? signature, DateTimeOffset? referenceNow = null, CancellationToken ct = default);
    string BuildMagicExcludeUrl(string baseUrl, long mediaId, string secret, DateTimeOffset? expiresAt = null);
    string BuildMagicRescueUrl(string baseUrl, long mediaId, string secret, DateTimeOffset? expiresAt = null);
    string BuildMagicPostponeUrl(string baseUrl, long mediaId, int days, string secret, DateTimeOffset? expiresAt = null);
    string BuildMagicExcludeAllUrl(string baseUrl, long? workflowId, string secret, DateTimeOffset? expiresAt = null);

    Task<CleanerrDiscordDiscoveryResult> DiscoverGuildsAndChannelsAsync(CancellationToken ct = default);
    Task<CleanerrDiscordCommandSyncResult> SyncSlashCommandsAsync(CancellationToken ct = default);
    Task<JsonObject> SendTestNotificationAsync(CancellationToken ct = default);

    JsonObject BuildDiscordStagedPayload(
        IReadOnlyList<CleanerrMediaItem> items,
        int graceDays,
        string collectionName,
        string publicUrl,
        string magicSecret,
        long? workflowId = null,
        string workflowName = "Cleanerr Cleanup",
        bool includeComponents = true,
        int pageIndex = 0,
        string? latestActivityBanner = null,
        IReadOnlySet<long>? protectedMediaIds = null,
        int defaultPostponeDays = 7,
        string webhookBotUsername = "Cleanerr");

    Task<JsonObject> SendStagedNotificationAsync(
        IReadOnlyList<CleanerrMediaItem> items,
        int graceDays,
        string collectionName,
        long? workflowId = null,
        string workflowName = "Cleanerr Cleanup",
        string? nodeWebhookOverride = null,
        CancellationToken ct = default);

    Task<JsonObject> SendFinalWarningNotificationAsync(
        IReadOnlyList<CleanerrMediaItem> items,
        DateTimeOffset earliestDeleteAt,
        CancellationToken ct = default);

    Task<JsonObject> SendDeletedSummaryNotificationAsync(
        IReadOnlyList<CleanerrMediaItem> deletedItems,
        long reclaimedBytes,
        CancellationToken ct = default);

    Task<JsonObject> SendRescuedNotificationAsync(
        CleanerrMediaItem item,
        string reason,
        CancellationToken ct = default);

    Task<JsonObject> HandleDiscordInteractionPayloadAsync(
        JsonObject data,
        Func<IReadOnlyList<long>, string, string, long?, int?, CancellationToken, Task<IReadOnlyList<string>>> excludeMediaIdsCallback,
        Func<long?, string, long?, int?, CancellationToken, Task<int>> excludeAllStagedCallback,
        CancellationToken ct = default,
        CleanerrDiscordRuntimeCallbacks? runtimeCallbacks = null);
}

public sealed class CleanerrDiscordService(
    IAppSettingsService settings,
    IPlexUserService userService,
    ICleanerrHttpGateway httpGateway,
    ISqliteDbFactory? dbFactory = null,
    IConfiguration? configuration = null) : ICleanerrDiscordService
{
    private const int PageSizeTargets = 10;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public bool IsGatewayConnected { get; set; }
    public string? GatewayBotUsername { get; private set; }
    public string? GatewayLastError { get; private set; }
    public event Action? ConfigChanged;

    public void SetGatewayStatus(bool connected, string? botUsername = null, string? lastError = null)
    {
        IsGatewayConnected = connected;
        if (botUsername is not null)
        {
            GatewayBotUsername = string.IsNullOrWhiteSpace(botUsername) ? null : botUsername.Trim();
        }
        else if (!connected)
        {
            GatewayBotUsername = null;
        }

        GatewayLastError = string.IsNullOrWhiteSpace(lastError) ? null : lastError;
    }

    public void RequestGatewayReconnect() => ConfigChanged?.Invoke();

    public async Task<CleanerrDiscordSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        var wh = await GetSettingWithFallbackAsync("cleanerr.discord_webhook_url", "Managerr:Cleanerr:Discord:WebhookUrl", ct) ?? "";
        var dbBotTok = await settings.GetSecretAsync("cleanerr.discord_bot_token", ct);
        var rawTok = !string.IsNullOrWhiteSpace(dbBotTok)
            ? dbBotTok.Trim()
            : (configuration?["Managerr:Cleanerr:Discord:BotToken"] ?? "").Trim();

        string masked;
        if (!string.IsNullOrWhiteSpace(dbBotTok))
        {
            var maskedObj = await settings.GetMaskedSecretAsync("cleanerr.discord_bot_token", ct);
            masked = maskedObj.MaskedHint ?? "";
        }
        else
        {
            masked = MaskSecretValue(rawTok);
        }

        var chId = await GetSettingWithFallbackAsync("cleanerr.discord_channel_id", "Managerr:Cleanerr:Discord:ChannelId", ct) ?? "";
        var appId = await GetSettingWithFallbackAsync("cleanerr.discord_app_id", "Managerr:Cleanerr:Discord:ApplicationId", ct) ?? CleanerrCrypto.DefaultDiscordAppId;
        var pubKey = await GetSettingWithFallbackAsync("cleanerr.discord_public_key", "Managerr:Cleanerr:Discord:PublicKey", ct) ?? CleanerrCrypto.DefaultDiscordPublicKeyHex;

        var pubUrl = await GetSettingWithFallbackAsync("cleanerr.discord_public_url", "Managerr:Cleanerr:Discord:PublicBaseUrl", ct);
        if (string.IsNullOrWhiteSpace(pubUrl))
        {
            pubUrl = await settings.GetAsync("cleanerr.public_url", ct);
        }
        pubUrl = string.IsNullOrWhiteSpace(pubUrl) ? "https://managerr.local" : pubUrl.Trim();

        var passive = string.Equals(await settings.GetAsync("cleanerr.discord_passive_mode", ct), "true", StringComparison.OrdinalIgnoreCase);
        var guildId = await GetSettingWithFallbackAsync("cleanerr.discord_guild_id", "Managerr:Cleanerr:Discord:GuildId", ct) ?? "";
        var authMode = await GetSettingWithFallbackAsync("cleanerr.discord_auth_mode", "Managerr:Cleanerr:Discord:AuthMode", ct);
        var rolesRaw = await settings.GetAsync("cleanerr.discord_allowed_roles", ct) ?? "[]";
        var allowedRoles = ParseStringArraySafe(rolesRaw);
        var notifyStaged = !string.Equals(await settings.GetAsync("cleanerr.discord_notify_staged", ct), "false", StringComparison.OrdinalIgnoreCase);
        var notifyRescued = string.Equals(await settings.GetAsync("cleanerr.discord_notify_rescued", ct), "true", StringComparison.OrdinalIgnoreCase);
        var notifyDeleted = !string.Equals(await settings.GetAsync("cleanerr.discord_notify_deleted", ct), "false", StringComparison.OrdinalIgnoreCase);
        var notifyFinalWarning = !string.Equals(await settings.GetAsync("cleanerr.discord_notify_final_warning", ct), "false", StringComparison.OrdinalIgnoreCase);
        var finalWarningHoursRaw = await settings.GetAsync("cleanerr.discord_final_warning_hours", ct);
        var finalWarningHours = int.TryParse(finalWarningHoursRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHours)
            ? Math.Clamp(parsedHours, 1, 168)
            : 24;
        var webhookBotUsernameRaw = await GetSettingWithFallbackAsync("cleanerr.discord_webhook_username", "Managerr:Cleanerr:Discord:WebhookBotUsername", ct);
        var webhookBotUsername = string.IsNullOrWhiteSpace(webhookBotUsernameRaw) ? "Cleanerr" : webhookBotUsernameRaw.Trim();

        return new CleanerrDiscordSettings(
            WebhookUrl: wh,
            HasBotToken: !string.IsNullOrWhiteSpace(rawTok),
            MaskedBotToken: masked,
            ChannelId: chId,
            ApplicationId: appId,
            PublicKeyHex: pubKey,
            PublicBaseUrl: pubUrl,
            GatewayPassiveShadowMode: passive,
            GatewayConnected: IsGatewayConnected,
            GuildId: guildId,
            AuthMode: NormalizeAuthMode(authMode),
            AllowedRoleIds: allowedRoles,
            NotifyOnStaged: notifyStaged,
            NotifyOnRescued: notifyRescued,
            NotifyOnDeleted: notifyDeleted,
            NotifyOnFinalWarning: notifyFinalWarning,
            FinalWarningHours: finalWarningHours,
            GatewayBotUsername: GatewayBotUsername,
            GatewayLastError: GatewayLastError,
            WebhookBotUsername: webhookBotUsername);
    }

    public async Task<CleanerrDiscordSettings> UpdateSettingsAsync(
        UpdateCleanerrDiscordSettingsRequest request,
        CancellationToken ct = default)
    {
        var shouldReconnectGateway = false;

        if (request.WebhookUrl is not null)
        {
            await settings.SetAsync("cleanerr.discord_webhook_url", request.WebhookUrl.Trim(), false, ct);
        }

        if (request.BotToken is not null && !string.IsNullOrWhiteSpace(request.BotToken))
        {
            var tok = request.BotToken.Trim();
            if (tok.StartsWith("bot ", StringComparison.OrdinalIgnoreCase))
            {
                tok = tok[4..].Trim();
            }

            await settings.SetAsync("cleanerr.discord_bot_token", tok, isSecret: true, ct);
            shouldReconnectGateway = true;
        }

        if (request.ChannelId is not null)
        {
            await settings.SetAsync("cleanerr.discord_channel_id", request.ChannelId.Trim(), false, ct);
        }

        if (request.ApplicationId is not null)
        {
            await settings.SetAsync("cleanerr.discord_app_id", request.ApplicationId.Trim(), false, ct);
            shouldReconnectGateway = true;
        }

        if (request.PublicKeyHex is not null)
        {
            await settings.SetAsync("cleanerr.discord_public_key", request.PublicKeyHex.Trim(), false, ct);
        }

        if (request.PublicBaseUrl is not null)
        {
            await settings.SetAsync("cleanerr.discord_public_url", request.PublicBaseUrl.Trim(), false, ct);
        }

        if (request.GatewayPassiveShadowMode.HasValue)
        {
            await settings.SetAsync("cleanerr.discord_passive_mode", request.GatewayPassiveShadowMode.Value ? "true" : "false", false, ct);
            shouldReconnectGateway = true;
        }

        if (request.GuildId is not null)
        {
            await settings.SetAsync("cleanerr.discord_guild_id", request.GuildId.Trim(), false, ct);
            shouldReconnectGateway = true;
        }

        if (request.AuthMode is not null)
        {
            await settings.SetAsync("cleanerr.discord_auth_mode", NormalizeAuthMode(request.AuthMode), false, ct);
        }

        if (request.AllowedRoleIds is not null)
        {
            var cleanedRoles = request.AllowedRoleIds.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            await settings.SetAsync("cleanerr.discord_allowed_roles", JsonSerializer.Serialize(cleanedRoles, JsonOpts), false, ct);
        }

        if (request.NotifyOnStaged.HasValue)
        {
            await settings.SetAsync("cleanerr.discord_notify_staged", request.NotifyOnStaged.Value ? "true" : "false", false, ct);
        }

        if (request.NotifyOnRescued.HasValue)
        {
            await settings.SetAsync("cleanerr.discord_notify_rescued", request.NotifyOnRescued.Value ? "true" : "false", false, ct);
        }

        if (request.NotifyOnDeleted.HasValue)
        {
            await settings.SetAsync("cleanerr.discord_notify_deleted", request.NotifyOnDeleted.Value ? "true" : "false", false, ct);
        }

        if (request.NotifyOnFinalWarning.HasValue)
        {
            await settings.SetAsync("cleanerr.discord_notify_final_warning", request.NotifyOnFinalWarning.Value ? "true" : "false", false, ct);
        }

        if (request.FinalWarningHours.HasValue)
        {
            var clamped = Math.Clamp(request.FinalWarningHours.Value, 1, 168);
            await settings.SetAsync("cleanerr.discord_final_warning_hours", clamped.ToString(CultureInfo.InvariantCulture), false, ct);
        }

        if (request.WebhookBotUsername is not null)
        {
            var botUsername = string.IsNullOrWhiteSpace(request.WebhookBotUsername)
                ? "Cleanerr"
                : request.WebhookBotUsername.Trim();
            await settings.SetAsync("cleanerr.discord_webhook_username", botUsername, false, ct);
        }

        var updated = await GetSettingsAsync(ct);
        if (shouldReconnectGateway)
        {
            ConfigChanged?.Invoke();
        }

        return updated;
    }

    private async Task<int> GetDefaultPostponeDaysAsync(CancellationToken ct = default)
    {
        var raw = await settings.GetAsync("cleanerr.default_postpone_days", ct);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 1, 365)
            : 7;
    }

    public async Task<string> GetMagicSecretAsync(CancellationToken ct = default)
    {
        var existing = await settings.GetSecretAsync("cleanerr.magic_link_secret", ct);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var generated = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        await settings.SetAsync("cleanerr.magic_link_secret", generated, isSecret: true, ct);
        return generated;
    }

    public async Task<bool> VerifyAndConsumeMagicTokenAsync(
        string target,
        string? signature,
        DateTimeOffset? referenceNow = null,
        CancellationToken ct = default)
    {
        var secret = await GetMagicSecretAsync(ct);
        if (!CleanerrCrypto.VerifyMagicTokenCore(secret, target, signature, referenceNow, consumeToken: false, out var tokenHashHex, out var expUnix))
        {
            return false;
        }

        if (dbFactory is null)
        {
            return CleanerrCrypto.VerifyMagicTokenCore(secret, target, signature, referenceNow, consumeToken: true, out _, out _);
        }

        var now = referenceNow ?? DateTimeOffset.UtcNow;
        var nowUnix = now.ToUnixTimeSeconds();
        var nowIso = now.ToString("O");

        var inserted = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    "DELETE FROM cleanerr_consumed_magic_tokens WHERE expires_at_unix <= @NowUnix;",
                    new { NowUnix = nowUnix },
                    transaction: tx);
                return await conn.ExecuteAsync(
                    """
                    INSERT OR IGNORE INTO cleanerr_consumed_magic_tokens (token_hash, expires_at_unix, consumed_at)
                    VALUES (@Hash, @Exp, @NowIso);
                    """,
                    new { Hash = tokenHashHex, Exp = expUnix, NowIso = nowIso },
                    transaction: tx);
            },
            ct);

        return inserted > 0;
    }

    public string BuildMagicExcludeUrl(string baseUrl, long mediaId, string secret, DateTimeOffset? expiresAt = null)
    {
        var b = baseUrl.TrimEnd('/');
        var sig = CleanerrCrypto.SignMagicToken(secret, $"exclude:{mediaId}", expiresAt);
        return $"{b}/api/v1/cleanerr/webhooks/magic/exclude?media_id={mediaId}&sig={sig}";
    }

    public string BuildMagicRescueUrl(string baseUrl, long mediaId, string secret, DateTimeOffset? expiresAt = null)
    {
        var b = baseUrl.TrimEnd('/');
        var sig = CleanerrCrypto.SignMagicToken(secret, $"rescue:{mediaId}", expiresAt);
        return $"{b}/api/v1/cleanerr/webhooks/magic/rescue?media_id={mediaId}&sig={sig}";
    }

    public string BuildMagicPostponeUrl(string baseUrl, long mediaId, int days, string secret, DateTimeOffset? expiresAt = null)
    {
        var b = baseUrl.TrimEnd('/');
        var sig = CleanerrCrypto.SignMagicToken(secret, $"postpone:{mediaId}:{days}", expiresAt);
        return $"{b}/api/v1/cleanerr/webhooks/magic/postpone?media_id={mediaId}&days={days}&sig={sig}";
    }

    public string BuildMagicExcludeAllUrl(string baseUrl, long? workflowId, string secret, DateTimeOffset? expiresAt = null)
    {
        var b = baseUrl.TrimEnd('/');
        var wfVal = workflowId ?? 0;
        var sig = CleanerrCrypto.SignMagicToken(secret, $"exclude_all:{wfVal}", expiresAt);
        return $"{b}/api/v1/cleanerr/webhooks/magic/exclude-all?workflow_id={wfVal}&sig={sig}";
    }

    public async Task<CleanerrDiscordDiscoveryResult> DiscoverGuildsAndChannelsAsync(CancellationToken ct = default)
    {
        var botTok = await GetEffectiveBotTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(botTok))
        {
            return new CleanerrDiscordDiscoveryResult(false, null, null, [], [], "Discord Bot Token is not configured.");
        }

        if (botTok.StartsWith("mock://", StringComparison.OrdinalIgnoreCase))
        {
            return new CleanerrDiscordDiscoveryResult(
                Ok: true,
                BotUserId: "mock-bot-id",
                BotUsername: "CleanerrMockBot",
                Guilds: [new CleanerrDiscordGuildOption("mock-guild-1", "Mock Homelab Server", true)],
                Channels: [new CleanerrDiscordChannelOption("mock-channel-1", "mock-guild-1", "cleanerr-leaving-soon", 0)]);
        }

        var headers = new Dictionary<string, string> { ["Authorization"] = $"Bot {botTok}" };

        var (meStatus, meBody) = await httpGateway.SendJsonAsync(
            HttpMethod.Get,
            "https://discord.com/api/v10/users/@me",
            null,
            headers,
            ct);

        if (meStatus is < 200 or >= 300)
        {
            return new CleanerrDiscordDiscoveryResult(
                Ok: false,
                BotUserId: null,
                BotUsername: null,
                Guilds: [],
                Channels: [],
                Error: $"Discord /users/@me returned HTTP {meStatus}: {TruncateSnippet(meBody, 180)}");
        }

        string? botUserId = null;
        string? botUsername = null;
        try
        {
            var meObj = JsonNode.Parse(meBody)?.AsObject();
            botUserId = meObj?["id"]?.GetValue<string>();
            botUsername = meObj?["username"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return new CleanerrDiscordDiscoveryResult(false, null, null, [], [], "Malformed JSON from Discord /users/@me.");
        }

        var (guildsStatus, guildsBody) = await httpGateway.SendJsonAsync(
            HttpMethod.Get,
            "https://discord.com/api/v10/users/@me/guilds",
            null,
            headers,
            ct);

        if (guildsStatus is < 200 or >= 300)
        {
            return new CleanerrDiscordDiscoveryResult(
                Ok: false,
                BotUserId: botUserId,
                BotUsername: botUsername,
                Guilds: [],
                Channels: [],
                Error: $"Discord /users/@me/guilds returned HTTP {guildsStatus}: {TruncateSnippet(guildsBody, 180)}");
        }

        var guilds = new List<CleanerrDiscordGuildOption>();
        try
        {
            var guildArr = JsonNode.Parse(guildsBody)?.AsArray();
            if (guildArr is not null)
            {
                foreach (var node in guildArr.Take(10))
                {
                    var obj = node?.AsObject();
                    var gId = obj?["id"]?.GetValue<string>()?.Trim();
                    var gName = obj?["name"]?.GetValue<string>()?.Trim();
                    if (string.IsNullOrWhiteSpace(gId) || string.IsNullOrWhiteSpace(gName)) continue;
                    var isOwner = obj?["owner"]?.GetValue<bool>() ?? false;
                    guilds.Add(new CleanerrDiscordGuildOption(gId, gName, isOwner));
                }
            }
        }
        catch (JsonException)
        {
            return new CleanerrDiscordDiscoveryResult(false, botUserId, botUsername, [], [], "Malformed JSON from Discord /users/@me/guilds.");
        }

        var channels = new List<CleanerrDiscordChannelOption>();
        foreach (var guild in guilds)
        {
            var (chStatus, chBody) = await httpGateway.SendJsonAsync(
                HttpMethod.Get,
                $"https://discord.com/api/v10/guilds/{Uri.EscapeDataString(guild.Id)}/channels",
                null,
                headers,
                ct);

            if (chStatus is < 200 or >= 300) continue;

            try
            {
                var chArr = JsonNode.Parse(chBody)?.AsArray();
                if (chArr is null) continue;

                var guildChannels = new List<(int Position, CleanerrDiscordChannelOption Option)>();
                foreach (var node in chArr)
                {
                    var obj = node?.AsObject();
                    var chType = obj?["type"]?.GetValue<int>() ?? -1;
                    if (chType is not (0 or 5)) continue;

                    var chId = obj?["id"]?.GetValue<string>()?.Trim();
                    var chName = obj?["name"]?.GetValue<string>()?.Trim();
                    if (string.IsNullOrWhiteSpace(chId) || string.IsNullOrWhiteSpace(chName)) continue;

                    var pos = obj?["position"]?.GetValue<int>() ?? 0;
                    guildChannels.Add((pos, new CleanerrDiscordChannelOption(chId, guild.Id, chName, chType)));
                }

                channels.AddRange(guildChannels
                    .OrderBy(x => x.Position)
                    .ThenBy(x => x.Option.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.Option));
            }
            catch (JsonException)
            {
                // Skip malformed guild channel payload and continue remaining guilds
            }
        }

        return new CleanerrDiscordDiscoveryResult(
            Ok: true,
            BotUserId: botUserId,
            BotUsername: botUsername,
            Guilds: guilds,
            Channels: channels);
    }

    public static JsonArray BuildSlashCommandsManifest() =>
        new()
        {
            new JsonObject
            {
                ["name"] = "cleanerr",
                ["type"] = 1,
                ["description"] = "Managerr Cleanerr — inspect Leaving Soon media, explain rules, exclude, postpone, or run",
                ["options"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = 1,
                        ["name"] = "status",
                        ["description"] = "Show Cleanerr storage overview, staged Leaving Soon count, and exclusions",
                    },
                    new JsonObject
                    {
                        ["type"] = 1,
                        ["name"] = "leaving-soon",
                        ["description"] = "View items staged for deletion with interactive Exclude & Postpone buttons",
                        ["options"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = 4,
                                ["name"] = "page",
                                ["description"] = "Page number (1-indexed)",
                                ["required"] = false,
                            },
                        },
                    },
                    new JsonObject
                    {
                        ["type"] = 1,
                        ["name"] = "why",
                        ["description"] = "Run Maintainerr Test Media Explainer to see why a movie or series matched or is protected",
                        ["options"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = 3,
                                ["name"] = "title",
                                ["description"] = "Movie or TV series title",
                                ["required"] = true,
                                ["autocomplete"] = true,
                            },
                        },
                    },
                    new JsonObject
                    {
                        ["type"] = 1,
                        ["name"] = "exclude",
                        ["description"] = "Exclude a movie or whole TV series from Cleanerr cleanup",
                        ["options"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = 3,
                                ["name"] = "title",
                                ["description"] = "Movie or TV series title to protect",
                                ["required"] = true,
                                ["autocomplete"] = true,
                            },
                            new JsonObject
                            {
                                ["type"] = 3,
                                ["name"] = "duration",
                                ["description"] = "Protection duration (defaults to permanent)",
                                ["required"] = false,
                                ["choices"] = new JsonArray
                                {
                                    new JsonObject { ["name"] = "Permanent", ["value"] = "permanent" },
                                    new JsonObject { ["name"] = "30 Days", ["value"] = "30d" },
                                    new JsonObject { ["name"] = "90 Days", ["value"] = "90d" },
                                    new JsonObject { ["name"] = "365 Days", ["value"] = "365d" },
                                },
                            },
                            new JsonObject
                            {
                                ["type"] = 3,
                                ["name"] = "reason",
                                ["description"] = "Optional reason logged in Cleanerr Exclusions",
                                ["required"] = false,
                            },
                        },
                    },
                    new JsonObject
                    {
                        ["type"] = 1,
                        ["name"] = "postpone",
                        ["description"] = "Push back the deletion date of a staged Leaving Soon movie or series",
                        ["options"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = 3,
                                ["name"] = "title",
                                ["description"] = "Staged movie or TV series title",
                                ["required"] = true,
                                ["autocomplete"] = true,
                            },
                            new JsonObject
                            {
                                ["type"] = 4,
                                ["name"] = "days",
                                ["description"] = "Days to extend grace period (default: 7)",
                                ["required"] = false,
                                ["choices"] = new JsonArray
                                {
                                    new JsonObject { ["name"] = "+7 Days", ["value"] = 7 },
                                    new JsonObject { ["name"] = "+14 Days", ["value"] = 14 },
                                    new JsonObject { ["name"] = "+30 Days", ["value"] = 30 },
                                },
                            },
                        },
                    },
                    new JsonObject
                    {
                        ["type"] = 1,
                        ["name"] = "exclusions",
                        ["description"] = "List active Cleanerr exclusions and revoke them directly",
                        ["options"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = 4,
                                ["name"] = "page",
                                ["description"] = "Page number (1-indexed)",
                                ["required"] = false,
                            },
                        },
                    },
                    new JsonObject
                    {
                        ["type"] = 1,
                        ["name"] = "run",
                        ["description"] = "Execute an enabled Cleanerr cleanup workflow",
                        ["options"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = 3,
                                ["name"] = "workflow",
                                ["description"] = "Workflow name or ID (defaults to first enabled workflow)",
                                ["required"] = false,
                                ["autocomplete"] = true,
                            },
                            new JsonObject
                            {
                                ["type"] = 5,
                                ["name"] = "dry_run",
                                ["description"] = "Override dry-run mode for this run",
                                ["required"] = false,
                            },
                        },
                    },
                },
            },
        };

    public async Task<CleanerrDiscordCommandSyncResult> SyncSlashCommandsAsync(CancellationToken ct = default)
    {
        var cfg = await GetSettingsAsync(ct);
        var botTok = await GetEffectiveBotTokenAsync(ct);
        var manifest = BuildSlashCommandsManifest();

        if (cfg.WebhookUrl.StartsWith("mock://", StringComparison.OrdinalIgnoreCase) || botTok.StartsWith("mock://", StringComparison.OrdinalIgnoreCase))
        {
            return new CleanerrDiscordCommandSyncResult(
                Ok: true,
                Scope: string.IsNullOrWhiteSpace(cfg.GuildId) ? "global" : $"guild:{cfg.GuildId}",
                CommandsRegistered: 7,
                Message: "Registered /cleanerr (status, leaving-soon, why, exclude, postpone, exclusions, run) in mock mode.");
        }

        if (string.IsNullOrWhiteSpace(botTok) || string.IsNullOrWhiteSpace(cfg.ApplicationId))
        {
            return new CleanerrDiscordCommandSyncResult(
                Ok: false,
                Scope: "none",
                CommandsRegistered: 0,
                Message: "Bot Token and Application ID are required to register Discord slash commands.");
        }

        var scopeLabel = string.IsNullOrWhiteSpace(cfg.GuildId) ? "global" : $"guild:{cfg.GuildId}";
        var url = string.IsNullOrWhiteSpace(cfg.GuildId)
            ? $"https://discord.com/api/v10/applications/{Uri.EscapeDataString(cfg.ApplicationId)}/commands"
            : $"https://discord.com/api/v10/applications/{Uri.EscapeDataString(cfg.ApplicationId)}/guilds/{Uri.EscapeDataString(cfg.GuildId)}/commands";

        var (status, body) = await httpGateway.SendJsonAsync(
            HttpMethod.Put,
            url,
            manifest.ToJsonString(),
            new Dictionary<string, string> { ["Authorization"] = $"Bot {botTok}" },
            ct);

        if (status is >= 200 and < 300)
        {
            return new CleanerrDiscordCommandSyncResult(
                Ok: true,
                Scope: scopeLabel,
                CommandsRegistered: 7,
                Message: $"Successfully registered /cleanerr (7 subcommands) via Discord {scopeLabel} API.");
        }

        var snippet = TruncateSnippet(body, 180);
        return new CleanerrDiscordCommandSyncResult(
            Ok: false,
            Scope: scopeLabel,
            CommandsRegistered: 0,
            Message: $"Discord API returned HTTP {status}: {snippet}");
    }

    public async Task<JsonObject> SendTestNotificationAsync(CancellationToken ct = default)
    {
        var cfg = await GetSettingsAsync(ct);
        var sampleItems = await LoadTestPreviewItemsAsync(ct);
        var dedupSamples = DeduplicateBySeriesOrMovie(sampleItems, new HashSet<long>());

        var previewLines = new List<string>
        {
            "Interactive Cleanerr notifications and exclusions are active (**zero Plex Collections created**).",
            "",
            "**Available Slash Commands (7):**",
            "• `/cleanerr status` — Storage & Leaving Soon summary",
            "• `/cleanerr leaving-soon` — Browse staged items with **Exclude** & **+7d Postpone** buttons",
            "• `/cleanerr why <title>` — Maintainerr rule-by-rule diagnostic explainer",
            "• `/cleanerr exclude <title> [duration]` — Protect a movie or whole TV series",
            "• `/cleanerr postpone <title> [days]` — Extend grace countdown",
            "• `/cleanerr exclusions` — Inspect and revoke active exclusion rules",
            "• `/cleanerr run [workflow] [dry_run]` — Trigger a cleanup workflow",
            "",
            "**Interactive Preview Items (Non-Mutating Test):**",
        };

        for (var i = 0; i < dedupSamples.Count; i++)
        {
            var tgt = dedupSamples[i];
            var gb = tgt.Bytes / (1024.0 * 1024.0 * 1024.0);
            var icon = tgt.IsSeries ? "📺" : "🎬";
            previewLines.Add($"{i + 1}. {icon} **{tgt.Label}** (`{gb:F1} GB`)");
        }

        var selectOptions = new JsonArray();
        var seenVals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tgt in dedupSamples.Take(25))
        {
            var val = $"test:{tgt.Id}";
            if (!seenVals.Add(val)) continue;
            var gb = tgt.Bytes / (1024.0 * 1024.0 * 1024.0);
            var optDesc = tgt.IsSeries
                ? $"PREVIEW SERIES ({gb:F1} GB) — Non-mutating component test"
                : $"PREVIEW MOVIE ({gb:F1} GB) — Non-mutating component test";
            selectOptions.Add(new JsonObject
            {
                ["label"] = TruncateText(tgt.Label, 95),
                ["value"] = val,
                ["description"] = TruncateText(optDesc, 95),
                ["emoji"] = new JsonObject { ["name"] = "🧪" },
            });
        }

        var components = new JsonArray();
        if (selectOptions.Count > 0)
        {
            components.Add(new JsonObject
            {
                ["type"] = 1,
                ["components"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = 3,
                        ["custom_id"] = "clnr_test_sel",
                        ["placeholder"] = "🧪 Test Interactive Dropdown (Preview Only — No DB Changes)...",
                        ["min_values"] = 1,
                        ["max_values"] = selectOptions.Count,
                        ["options"] = selectOptions,
                    },
                },
            });
        }

        var btnRow = new JsonArray
        {
            new JsonObject
            {
                ["type"] = 2,
                ["style"] = 1,
                ["label"] = "🧪 Test Interactive Button",
                ["custom_id"] = "clnr_test_btn",
            },
        };

        if (TryNormalizeHttpUrl(cfg.PublicBaseUrl, out var validPublicUrl))
        {
            btnRow.Add(new JsonObject
            {
                ["type"] = 2,
                ["style"] = 5,
                ["label"] = "Cleanerr UI",
                ["url"] = validPublicUrl,
            });
        }

        components.Add(new JsonObject { ["type"] = 1, ["components"] = btnRow });

        var payload = new JsonObject
        {
            ["username"] = string.IsNullOrWhiteSpace(cfg.WebhookBotUsername) ? "Cleanerr" : cfg.WebhookBotUsername,
            ["embeds"] = new JsonArray
            {
                new JsonObject
                {
                    ["title"] = "✅ Managerr Cleanerr — Discord Bot Connected",
                    ["description"] = TruncateText(string.Join('\n', previewLines), 4090),
                    ["color"] = 0x10B981,
                    ["fields"] = new JsonArray
                    {
                        new JsonObject { ["name"] = "🔐 Permission Mode", ["value"] = $"`{cfg.AuthMode}`", ["inline"] = true },
                        new JsonObject { ["name"] = "🌐 Gateway WS", ["value"] = cfg.GatewayConnected ? "`Connected`" : "`Standby`", ["inline"] = true },
                        new JsonObject { ["name"] = "🤖 Bot Identity", ["value"] = !string.IsNullOrWhiteSpace(cfg.GatewayBotUsername) ? $"`@{cfg.GatewayBotUsername}`" : "`Webhook / Standby`", ["inline"] = true },
                    },
                    ["footer"] = new JsonObject { ["text"] = "Managerr Cleanerr • Native SQLite + Interactive Discord Components" },
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                },
            },
            ["components"] = components,
        };

        return await DeliverPayloadToDiscordAsync(
            payload,
            includeComponents: true,
            cfg,
            null,
            ct,
            fallbackWithoutComponentsFactory: () =>
            {
                var fb = payload.DeepClone().AsObject();
                fb.Remove("components");
                return fb;
            });
    }

    public JsonObject BuildDiscordStagedPayload(
        IReadOnlyList<CleanerrMediaItem> items,
        int graceDays,
        string collectionName,
        string publicUrl,
        string magicSecret,
        long? workflowId = null,
        string workflowName = "Cleanerr Cleanup",
        bool includeComponents = true,
        int pageIndex = 0,
        string? latestActivityBanner = null,
        IReadOnlySet<long>? protectedMediaIds = null,
        int defaultPostponeDays = 7,
        string webhookBotUsername = "Cleanerr")
    {
        _ = collectionName;
        var postponeDays = Math.Clamp(defaultPostponeDays, 1, 365);
        var botUsername = string.IsNullOrWhiteSpace(webhookBotUsername) ? "Cleanerr" : webhookBotUsername.Trim();
        var protectedIds = protectedMediaIds ?? new HashSet<long>();
        var activeItems = items.Where(x => !protectedIds.Contains(x.Id)).ToList();
        var totalGb = activeItems.Sum(x => x.SizeBytes) / (1024.0 * 1024.0 * 1024.0);
        var hasValidPublicUrl = TryNormalizeHttpUrl(publicUrl, out var normalizedPublicUrl);
        var deleteUnix = DateTimeOffset.UtcNow.AddDays(Math.Max(1, graceDays)).ToUnixTimeSeconds();

        var dedupAll = DeduplicateBySeriesOrMovie(items, protectedIds);
        var totalPages = Math.Max(1, (int)Math.Ceiling(dedupAll.Count / (double)PageSizeTargets));
        var safePage = Math.Clamp(pageIndex, 0, totalPages - 1);
        var pageTargets = dedupAll.Skip(safePage * PageSizeTargets).Take(PageSizeTargets).ToList();

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(latestActivityBanner))
        {
            lines.Add(latestActivityBanner!);
            lines.Add("");
        }

        for (var i = 0; i < pageTargets.Count; i++)
        {
            var tgt = pageTargets[i];
            var displayIdx = (safePage * PageSizeTargets) + i + 1;
            var gb = tgt.Bytes / (1024.0 * 1024.0 * 1024.0);
            var icon = tgt.IsSeries ? "📺" : "🎬";
            var classBadge = !string.IsNullOrWhiteSpace(tgt.Classification) ? $" • `{tgt.Classification}`" : "";

            if (tgt.IsProtected)
            {
                lines.Add($"{displayIdx}. ✅ ~~**{tgt.Label}**~~ (`{gb:F1} GB`) — **Protected**");
            }
            else
            {
                var countNote = tgt.IsSeries && tgt.Count > 1 ? $" ({tgt.Count} items)" : "";
                if (hasValidPublicUrl && !string.IsNullOrWhiteSpace(magicSecret))
                {
                    var exUrl = BuildMagicExcludeUrl(normalizedPublicUrl, tgt.Id, magicSecret);
                    var ppUrl = BuildMagicPostponeUrl(normalizedPublicUrl, tgt.Id, postponeDays, magicSecret);
                    var rsUrl = BuildMagicRescueUrl(normalizedPublicUrl, tgt.Id, magicSecret);
                    lines.Add($"{displayIdx}. {icon} **{tgt.Label}**{countNote} (`{gb:F1} GB`{classBadge})\n   ↳ [**🛡️ Exclude**]({exUrl})  •  [**⏳ +{postponeDays}d**]({ppUrl})  •  [**🛟 Rescue**]({rsUrl})");
                }
                else
                {
                    lines.Add($"{displayIdx}. {icon} **{tgt.Label}**{countNote} (`{gb:F1} GB`{classBadge})");
                }
            }
        }

        if (dedupAll.Count > pageTargets.Count)
        {
            lines.Add($"\n*Showing page **{safePage + 1}/{totalPages}** ({dedupAll.Count} series/movies • {items.Count} total item(s)).*");
        }

        if (hasValidPublicUrl && !string.IsNullOrWhiteSpace(magicSecret))
        {
            var excludeAllUrl = BuildMagicExcludeAllUrl(normalizedPublicUrl, workflowId, magicSecret);
            lines.Add($"\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n👉 [**🛡️ Exclude Whole List ({activeItems.Count} active)**]({excludeAllUrl})  •  [**Open Managerr Cleanerr UI**]({normalizedPublicUrl})");
        }
        else if (hasValidPublicUrl)
        {
            lines.Add($"\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n👉 [**Open Managerr Cleanerr UI**]({normalizedPublicUrl})");
        }

        var desc = string.Join('\n', lines);
        if (desc.Length > 4090) desc = desc[..4090];

        var allProtected = activeItems.Count == 0 && items.Count > 0;
        var payload = new JsonObject
        {
            ["username"] = botUsername,
            ["embeds"] = new JsonArray
            {
                new JsonObject
                {
                    ["title"] = allProtected
                        ? $"🛡️ {workflowName} — All Staged Items Protected"
                        : $"🧹 {workflowName} — {activeItems.Count} Item(s) Leaving Soon",
                    ["description"] = desc,
                    ["color"] = allProtected ? 0x22C55E : 0xF0AD4E,
                    ["fields"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "⏳ Scheduled Cleanup",
                            ["value"] = allProtected ? "**None (All Protected)**" : $"<t:{deleteUnix}:R> (**{graceDays}d** grace)",
                            ["inline"] = true,
                        },
                        new JsonObject
                        {
                            ["name"] = "💾 Reclaimable Space",
                            ["value"] = $"**{totalGb:F2} GB**",
                            ["inline"] = true,
                        },
                        new JsonObject
                        {
                            ["name"] = "💡 In-Chat Controls",
                            ["value"] = $"Click **🛡️ Exclude**, **⏳ Postpone +{postponeDays}d**, or use `/cleanerr why <title>` — **no Plex collections created**.",
                            ["inline"] = false,
                        },
                    },
                    ["footer"] = new JsonObject { ["text"] = $"Managerr Cleanerr • Page {safePage + 1}/{totalPages}" },
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                },
            },
        };

        var activePageTargets = pageTargets.Where(t => !t.IsProtected).ToList();
        if (includeComponents && activeItems.Count > 0)
        {
            var components = new JsonArray();
            var selectOptions = new JsonArray();
            var seenSelectValues = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tgt in dedupAll.Where(t => !t.IsProtected))
            {
                if (selectOptions.Count >= 25) break;
                var val = tgt.Id.ToString(CultureInfo.InvariantCulture);
                if (!seenSelectValues.Add(val)) continue;

                var gb = tgt.Bytes / (1024.0 * 1024.0 * 1024.0);
                var optDesc = tgt.IsSeries
                    ? $"SERIES ({tgt.Count} item(s) • {gb:F1} GB) — Excludes whole series"
                    : $"MOVIE • {gb:F1} GB — Click to exclude permanently";
                selectOptions.Add(new JsonObject
                {
                    ["label"] = TruncateText(tgt.Label, 95),
                    ["value"] = val,
                    ["description"] = TruncateText(optDesc, 95),
                    ["emoji"] = new JsonObject { ["name"] = "🛡️" },
                });
            }

            if (selectOptions.Count > 0)
            {
                components.Add(new JsonObject
                {
                    ["type"] = 1,
                    ["components"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = 3,
                            ["custom_id"] = $"clnr_sel_excl:{workflowId ?? 0}:{safePage}",
                            ["placeholder"] = "🛡️ Select series/movies to exclude (excludes whole series)...",
                            ["min_values"] = 1,
                            ["max_values"] = selectOptions.Count,
                            ["options"] = selectOptions,
                        },
                    },
                });
            }

            // Rows 2-3: Up to 10 quick Exclude buttons for current page (5 per row)
            for (var rowStart = 0; rowStart < activePageTargets.Count && components.Count < 3; rowStart += 5)
            {
                var rowBtns = new JsonArray();
                var chunk = activePageTargets.Skip(rowStart).Take(5).ToList();
                foreach (var tgt in chunk)
                {
                    var shortTitle = tgt.Label.Length > 20 ? tgt.Label[..19] + "…" : tgt.Label;
                    rowBtns.Add(new JsonObject
                    {
                        ["type"] = 2,
                        ["style"] = 2,
                        ["label"] = $"🛡️ #{tgt.DisplayNumber} {shortTitle}",
                        ["custom_id"] = $"clnr_excl:{tgt.Id}",
                    });
                }
                components.Add(new JsonObject { ["type"] = 1, ["components"] = rowBtns });
            }

            // Row 4: Quick Postpone buttons for top 5 active items on page
            if (activePageTargets.Count > 0 && components.Count < 4)
            {
                var ppBtns = new JsonArray();
                foreach (var tgt in activePageTargets.Take(5))
                {
                    var shortTitle = tgt.Label.Length > 18 ? tgt.Label[..17] + "…" : tgt.Label;
                    ppBtns.Add(new JsonObject
                    {
                        ["type"] = 2,
                        ["style"] = 1,
                        ["label"] = $"⏳ +{postponeDays}d #{tgt.DisplayNumber} {shortTitle}",
                        ["custom_id"] = $"clnr_postpone:{tgt.Id}:{postponeDays}",
                    });
                }
                components.Add(new JsonObject { ["type"] = 1, ["components"] = ppBtns });
            }

            // Row 5 (footer, max 5 buttons): optional Prev/Next pagination, Exclude All, Postpone All, and Cleanerr UI link button
            var footerRow = new JsonArray();
            if (totalPages > 1)
            {
                footerRow.Add(new JsonObject
                {
                    ["type"] = 2,
                    ["style"] = 2,
                    ["label"] = "◀ Prev",
                    ["custom_id"] = $"clnr_page:{workflowId ?? 0}:{Math.Max(0, safePage - 1)}",
                    ["disabled"] = safePage <= 0,
                });
                footerRow.Add(new JsonObject
                {
                    ["type"] = 2,
                    ["style"] = 2,
                    ["label"] = $"Next ▶ ({safePage + 1}/{totalPages})",
                    ["custom_id"] = $"clnr_page:{workflowId ?? 0}:{Math.Min(totalPages - 1, safePage + 1)}",
                    ["disabled"] = safePage >= totalPages - 1,
                });
            }

            footerRow.Add(new JsonObject
            {
                ["type"] = 2,
                ["style"] = 3,
                ["label"] = $"🛡️ Exclude All ({activeItems.Count})",
                ["custom_id"] = $"clnr_excl_all:{workflowId ?? 0}",
            });
            footerRow.Add(new JsonObject
            {
                ["type"] = 2,
                ["style"] = 1,
                ["label"] = $"⏳ Postpone All +{postponeDays}d",
                ["custom_id"] = $"clnr_postpone_all:{workflowId ?? 0}:{postponeDays}",
            });

            if (hasValidPublicUrl && footerRow.Count < 5)
            {
                footerRow.Add(new JsonObject
                {
                    ["type"] = 2,
                    ["style"] = 5,
                    ["label"] = "Cleanerr UI",
                    ["url"] = normalizedPublicUrl,
                });
            }

            components.Add(new JsonObject { ["type"] = 1, ["components"] = footerRow });
            payload["components"] = components;
        }
        else if (includeComponents && allProtected)
        {
            payload["components"] = new JsonArray();
        }

        return payload;
    }

    public async Task<JsonObject> SendStagedNotificationAsync(
        IReadOnlyList<CleanerrMediaItem> items,
        int graceDays,
        string collectionName,
        long? workflowId = null,
        string workflowName = "Cleanerr Cleanup",
        string? nodeWebhookOverride = null,
        CancellationToken ct = default)
    {
        var cfg = await GetSettingsAsync(ct);
        if (!cfg.NotifyOnStaged)
        {
            return new JsonObject { ["sent"] = false, ["reason"] = "notify_on_staged_disabled" };
        }

        if (items.Count == 0)
        {
            return new JsonObject { ["sent"] = false, ["reason"] = "no_webhook_or_items" };
        }

        var magicSecret = await GetMagicSecretAsync(ct);
        var defaultPostponeDays = await GetDefaultPostponeDaysAsync(ct);
        var payloadWithComp = BuildDiscordStagedPayload(items, graceDays, collectionName, cfg.PublicBaseUrl, magicSecret, workflowId, workflowName, includeComponents: true, defaultPostponeDays: defaultPostponeDays, webhookBotUsername: cfg.WebhookBotUsername);
        var result = await DeliverPayloadToDiscordAsync(payloadWithComp, includeComponents: true, cfg, nodeWebhookOverride, ct,
            fallbackWithoutComponentsFactory: () => BuildDiscordStagedPayload(items, graceDays, collectionName, cfg.PublicBaseUrl, magicSecret, workflowId, workflowName, includeComponents: false, defaultPostponeDays: defaultPostponeDays, webhookBotUsername: cfg.WebhookBotUsername));

        if (result["sent"]?.GetValue<bool>() == true &&
            result["message_id"]?.GetValue<string>() is { Length: > 0 } msgId &&
            result["channel_id"]?.GetValue<string>() is { Length: > 0 } chId)
        {
            await SaveDiscordMessageStateAsync(msgId, chId, workflowId, workflowName, graceDays, 0, items.Select(i => i.Id).ToList(), "staged", ct);
        }

        result["items_count"] = items.Count;
        return result;
    }

    public async Task<JsonObject> SendFinalWarningNotificationAsync(
        IReadOnlyList<CleanerrMediaItem> items,
        DateTimeOffset earliestDeleteAt,
        CancellationToken ct = default)
    {
        var cfg = await GetSettingsAsync(ct);
        if (!cfg.NotifyOnFinalWarning)
        {
            return new JsonObject { ["sent"] = false, ["reason"] = "notify_on_final_warning_disabled" };
        }

        if (items.Count == 0)
        {
            return new JsonObject { ["sent"] = false, ["reason"] = "no_items" };
        }

        var magicSecret = await GetMagicSecretAsync(ct);
        var defaultPostponeDays = await GetDefaultPostponeDaysAsync(ct);
        var deleteUnix = earliestDeleteAt.ToUnixTimeSeconds();
        var hoursRemaining = (earliestDeleteAt - DateTimeOffset.UtcNow).TotalHours;
        var approxDays = Math.Max(1, (int)Math.Ceiling(Math.Max(1.0, hoursRemaining) / 24.0));
        var urgentColor = hoursRemaining <= 6.0 ? 0xEF4444 : 0xF59E0B;
        var banner = $"⚠️ **FINAL WARNING:** **{items.Count}** staged item(s) will be permanently deleted <t:{deleteUnix}:R> unless excluded or postponed!";

        JsonObject BuildWarningPayload(bool includeComp)
        {
            var p = BuildDiscordStagedPayload(
                items,
                approxDays,
                "Final Warning",
                cfg.PublicBaseUrl,
                magicSecret,
                workflowId: null,
                workflowName: "FINAL WARNING — Leaving Soon",
                includeComponents: includeComp,
                pageIndex: 0,
                latestActivityBanner: banner,
                defaultPostponeDays: defaultPostponeDays,
                webhookBotUsername: cfg.WebhookBotUsername);

            var embed = p["embeds"]?.AsArray().FirstOrDefault()?.AsObject();
            if (embed is not null)
            {
                embed["title"] = $"⚠️ FINAL WARNING — {items.Count} Item(s) Deleting Imminently";
                embed["color"] = urgentColor;
                var fields = embed["fields"]?.AsArray();
                if (fields is not null && fields.Count > 0 && fields[0] is JsonObject firstField)
                {
                    firstField["name"] = "⏰ Imminent Deletion";
                    firstField["value"] = $"<t:{deleteUnix}:R> (<t:{deleteUnix}:f>)";
                }
            }

            return p;
        }

        var payloadWithComp = BuildWarningPayload(includeComp: true);
        var result = await DeliverPayloadToDiscordAsync(
            payloadWithComp,
            includeComponents: true,
            cfg,
            null,
            ct,
            fallbackWithoutComponentsFactory: () => BuildWarningPayload(includeComp: false));

        if (result["sent"]?.GetValue<bool>() == true &&
            result["message_id"]?.GetValue<string>() is { Length: > 0 } msgId &&
            result["channel_id"]?.GetValue<string>() is { Length: > 0 } chId)
        {
            await SaveDiscordMessageStateAsync(
                msgId,
                chId,
                null,
                "FINAL WARNING — Leaving Soon",
                approxDays,
                0,
                items.Select(i => i.Id).ToList(),
                "final_warning",
                ct);
        }

        result["items_count"] = items.Count;
        result["earliest_delete_at"] = earliestDeleteAt.ToString("O");
        return result;
    }

    public async Task<JsonObject> SendDeletedSummaryNotificationAsync(
        IReadOnlyList<CleanerrMediaItem> deletedItems,
        long reclaimedBytes,
        CancellationToken ct = default)
    {
        var cfg = await GetSettingsAsync(ct);
        if (!cfg.NotifyOnDeleted || deletedItems.Count == 0)
        {
            return new JsonObject { ["sent"] = false, ["reason"] = "disabled_or_empty" };
        }

        var gb = reclaimedBytes / (1024.0 * 1024.0 * 1024.0);
        var lines = deletedItems.Take(15).Select(i => $"• 🗑️ **{FormatItemTitle(i)}** (`{i.SizeBytes / (1024.0 * 1024.0 * 1024.0):F1} GB`)").ToList();
        if (deletedItems.Count > 15)
        {
            lines.Add($"*...and {deletedItems.Count - 15} more item(s).*");
        }

        var payload = new JsonObject
        {
            ["username"] = string.IsNullOrWhiteSpace(cfg.WebhookBotUsername) ? "Cleanerr" : cfg.WebhookBotUsername,
            ["embeds"] = new JsonArray
            {
                new JsonObject
                {
                    ["title"] = $"🗑️ Cleanerr Executed — Reclaimed {gb:F2} GB ({deletedItems.Count} item(s))",
                    ["description"] = string.Join('\n', lines),
                    ["color"] = 0xEF4444,
                    ["footer"] = new JsonObject { ["text"] = "Managerr Cleanerr • Expired Grace Queue Processed" },
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                },
            },
        };

        return await DeliverPayloadToDiscordAsync(payload, includeComponents: false, cfg, null, ct);
    }

    public async Task<JsonObject> SendRescuedNotificationAsync(
        CleanerrMediaItem item,
        string reason,
        CancellationToken ct = default)
    {
        var cfg = await GetSettingsAsync(ct);
        if (!cfg.NotifyOnRescued)
        {
            return new JsonObject { ["sent"] = false, ["reason"] = "notify_on_rescued_disabled" };
        }

        var payload = new JsonObject
        {
            ["username"] = string.IsNullOrWhiteSpace(cfg.WebhookBotUsername) ? "Cleanerr" : cfg.WebhookBotUsername,
            ["embeds"] = new JsonArray
            {
                new JsonObject
                {
                    ["title"] = $"🛟 Auto-Rescued from Leaving Soon: {FormatItemTitle(item)}",
                    ["description"] = $"Reason: **{reason}**",
                    ["color"] = 0x38BDF8,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                },
            },
        };

        return await DeliverPayloadToDiscordAsync(payload, includeComponents: false, cfg, null, ct);
    }

    public async Task<JsonObject> HandleDiscordInteractionPayloadAsync(
        JsonObject data,
        Func<IReadOnlyList<long>, string, string, long?, int?, CancellationToken, Task<IReadOnlyList<string>>> excludeMediaIdsCallback,
        Func<long?, string, long?, int?, CancellationToken, Task<int>> excludeAllStagedCallback,
        CancellationToken ct = default,
        CleanerrDiscordRuntimeCallbacks? runtimeCallbacks = null)
    {
        var interactionType = data["type"]?.GetValue<int>() ?? 0;

        // Type 1: Discord PING verification handshake
        if (interactionType == 1)
        {
            return new JsonObject { ["type"] = 1 };
        }

        var cfg = await GetSettingsAsync(ct);
        var userObj = data["member"]?["user"]?.AsObject() ?? data["user"]?.AsObject();
        var discordUserId = userObj?["id"]?.GetValue<string>()?.Trim();
        var discordUsername = userObj?["username"]?.GetValue<string>()?.Trim() ?? "discord_user";

        var allUsers = await userService.ListUsersAsync(ct);
        var activeUsers = allUsers.Where(u => u.IsActive).ToList();
        var matchedUser = (!string.IsNullOrWhiteSpace(discordUserId)
                ? activeUsers.FirstOrDefault(u => string.Equals(u.DiscordUserId?.Trim(), discordUserId, StringComparison.OrdinalIgnoreCase))
                : null)
            ?? (!string.IsNullOrWhiteSpace(discordUsername)
                ? activeUsers.FirstOrDefault(u =>
                    string.IsNullOrWhiteSpace(u.DiscordUserId) &&
                    string.Equals(u.DiscordUsername?.Trim(), discordUsername, StringComparison.OrdinalIgnoreCase))
                : null);

        var requiredScope = ResolveRequiredDiscordScope(interactionType, data);
        var (authorized, denialReason) = CheckDiscordAuthorization(cfg, data, matchedUser, discordUsername, requiredScope);
        if (!authorized)
        {
            if (interactionType == 4)
            {
                return new JsonObject
                {
                    ["type"] = 8,
                    ["data"] = new JsonObject { ["choices"] = new JsonArray() },
                };
            }

            return BuildEphemeralMessage(denialReason!);
        }

        // Type 4: Slash command autocomplete (/cleanerr why, exclude, postpone, run)
        if (interactionType == 4 && runtimeCallbacks is not null)
        {
            return await HandleAutocompleteInteractionAsync(data, runtimeCallbacks, ct);
        }

        var actorUsername = matchedUser?.Username ?? $"@{discordUsername}";
        var actorUserId = matchedUser?.Id;
        var quotaCap = cfg.AuthMode == "linked_plex_rbac"
            ? matchedUser?.EffectivePermissions.CleanerrExclusionLimit
            : null;

        // Type 2: Slash Commands (/cleanerr status | leaving-soon | why | exclude | postpone | exclusions | run)
        if (interactionType == 2 && runtimeCallbacks is not null)
        {
            return await HandleSlashCommandInteractionAsync(
                data,
                cfg,
                discordUsername,
                actorUsername,
                actorUserId,
                quotaCap,
                runtimeCallbacks,
                ct);
        }

        // Type 3: Interactive Message Components (Buttons & Multi-Select Dropdown)
        if (interactionType == 3)
        {
            var compData = data["data"]?.AsObject();
            var customId = compData?["custom_id"]?.GetValue<string>() ?? "";
            var hasMessageEnvelope = data["message"] is JsonObject;

            try
            {
                if (string.Equals(customId, "clnr_test_btn", StringComparison.Ordinal))
                {
                    return BuildEphemeralMessage($"✅ **Interactive Button Works!** Received test click from **@{discordUsername}** (preview only — no database changes were made).");
                }

                if (string.Equals(customId, "clnr_test_sel", StringComparison.Ordinal))
                {
                    var selected = compData?["values"]?.AsArray()
                        .Select(v => v?.GetValue<string>() ?? "")
                        .Where(s => s.Length > 0)
                        .ToList() ?? [];
                    var previewList = selected.Count > 0 ? string.Join(", ", selected.Select(s => $"`{s}`")) : "`none`";
                    return BuildEphemeralMessage($"✅ **Interactive Dropdown Works!** Selected {previewList} (preview only — no database changes were made).");
                }

                if (string.Equals(customId, "clnr_del_excl", StringComparison.Ordinal) && runtimeCallbacks is not null)
                {
                    var ruleIds = compData?["values"]?.AsArray()
                        .Select(v => long.TryParse(v?.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rid) ? rid : 0)
                        .Where(rid => rid > 0)
                        .Distinct()
                        .ToList() ?? [];

                    foreach (var rid in ruleIds)
                    {
                        await runtimeCallbacks.DeleteExclusionAsync(rid, ct);
                    }

                    return BuildEphemeralMessage($"🗑️ **Revoked {ruleIds.Count} Cleanerr exclusion rule(s)** (by **@{discordUsername}**).");
                }

                if (customId.StartsWith("clnr_excl:", StringComparison.Ordinal))
                {
                    if (long.TryParse(customId["clnr_excl:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid))
                    {
                        var titles = runtimeCallbacks is not null
                            ? await runtimeCallbacks.ExcludeMediaIdsAsync(
                                [mid],
                                actorUsername,
                                $"Excluded in Discord by @{discordUsername}",
                                actorUserId,
                                quotaCap,
                                null,
                                ct)
                            : await excludeMediaIdsCallback(
                                [mid],
                                actorUsername,
                                $"Excluded in Discord by @{discordUsername}",
                                actorUserId,
                                quotaCap,
                                ct);

                        var label = titles.FirstOrDefault() ?? $"Item #{mid}";
                        var banner = $"🛡️ **Excluded Permanently:** `{label}` (by **@{discordUsername}** — removed from Deletion Queue!)";

                        if (hasMessageEnvelope && runtimeCallbacks is not null)
                        {
                            var inPlace = await TryBuildInPlaceUpdatedMessageAsync(data, cfg, null, banner, ct, runtimeCallbacks);
                            if (inPlace is not null) return inPlace;
                        }

                        return new JsonObject
                        {
                            ["type"] = 4,
                            ["data"] = new JsonObject { ["content"] = banner },
                        };
                    }
                }
                else if (customId.StartsWith("clnr_sel_excl:", StringComparison.Ordinal))
                {
                    var parts = customId.Split(':');
                    var parsedPage = parts.Length >= 3 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pg) ? pg : (int?)null;
                    var vals = compData?["values"]?.AsArray()
                        .Select(v => long.TryParse(v?.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
                        .Where(n => n > 0)
                        .ToList() ?? [];

                    var titles = runtimeCallbacks is not null
                        ? await runtimeCallbacks.ExcludeMediaIdsAsync(
                            vals,
                            actorUsername,
                            $"Excluded in Discord dropdown by @{discordUsername}",
                            actorUserId,
                            quotaCap,
                            null,
                            ct)
                        : await excludeMediaIdsCallback(
                            vals,
                            actorUsername,
                            $"Excluded in Discord dropdown by @{discordUsername}",
                            actorUserId,
                            quotaCap,
                            ct);

                    var joined = titles.Count > 0 ? string.Join(", ", titles.Select(t => $"`{t}`")) : $"{vals.Count} items";
                    var banner = $"🛡️ **Excluded {titles.Count} Item(s) Permanently:** {joined} (by **@{discordUsername}**)";

                    if (hasMessageEnvelope && runtimeCallbacks is not null)
                    {
                        var inPlace = await TryBuildInPlaceUpdatedMessageAsync(data, cfg, parsedPage, banner, ct, runtimeCallbacks);
                        if (inPlace is not null) return inPlace;
                    }

                    return new JsonObject
                    {
                        ["type"] = 4,
                        ["data"] = new JsonObject { ["content"] = banner },
                    };
                }
                else if (customId.StartsWith("clnr_excl_all:", StringComparison.Ordinal))
                {
                    long? wfId = long.TryParse(customId["clnr_excl_all:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWf) && parsedWf > 0 ? parsedWf : null;
                    var cnt = runtimeCallbacks is not null
                        ? await runtimeCallbacks.ExcludeAllStagedAsync(
                            wfId,
                            $"Bulk Excluded in Discord by @{discordUsername}",
                            actorUserId,
                            quotaCap,
                            ct)
                        : await excludeAllStagedCallback(
                            wfId,
                            $"Bulk Excluded in Discord by @{discordUsername}",
                            actorUserId,
                            quotaCap,
                            ct);

                    var banner = $"🛡️ **Excluded Whole Staged List ({cnt} items)** permanently (by **@{discordUsername}**)";
                    if (hasMessageEnvelope && runtimeCallbacks is not null)
                    {
                        var inPlace = await TryBuildInPlaceUpdatedMessageAsync(data, cfg, 0, banner, ct, runtimeCallbacks);
                        if (inPlace is not null) return inPlace;
                    }

                    return new JsonObject
                    {
                        ["type"] = 4,
                        ["data"] = new JsonObject { ["content"] = banner },
                    };
                }
                else if (customId.StartsWith("clnr_postpone:", StringComparison.Ordinal) && runtimeCallbacks is not null)
                {
                    var parts = customId.Split(':');
                    if (parts.Length >= 3 &&
                        long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid) &&
                        int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
                    {
                        var updated = await runtimeCallbacks.PostponeMediaIdsAsync(
                            [mid],
                            Math.Clamp(days, 1, 365),
                            $"Postponed +{days}d in Discord by @{discordUsername}",
                            ct);
                        var first = updated.FirstOrDefault();
                        var title = first?.MediaTitle ?? $"Item #{mid}";
                        var banner = $"⏳ **Postponed +{days}d:** `{title}` (by **@{discordUsername}**)";

                        if (hasMessageEnvelope)
                        {
                            var inPlace = await TryBuildInPlaceUpdatedMessageAsync(data, cfg, null, banner, ct, runtimeCallbacks);
                            if (inPlace is not null) return inPlace;
                        }

                        return new JsonObject
                        {
                            ["type"] = 4,
                            ["data"] = new JsonObject { ["content"] = banner },
                        };
                    }
                }
                else if (customId.StartsWith("clnr_postpone_all:", StringComparison.Ordinal) && runtimeCallbacks is not null)
                {
                    var parts = customId.Split(':');
                    long? wfId = parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pwf) && pwf > 0 ? pwf : null;
                    var defaultPostponeDays = await GetDefaultPostponeDaysAsync(ct);
                    var days = parts.Length >= 3 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pds) ? Math.Clamp(pds, 1, 365) : defaultPostponeDays;
                    var cnt = await runtimeCallbacks.PostponeAllStagedAsync(
                        wfId,
                        days,
                        $"Bulk Postponed +{days}d in Discord by @{discordUsername}",
                        ct);
                    var banner = $"⏳ **Postponed {cnt} Staged Item(s) by +{days} Days** (by **@{discordUsername}**)";

                    if (hasMessageEnvelope)
                    {
                        var inPlace = await TryBuildInPlaceUpdatedMessageAsync(data, cfg, null, banner, ct, runtimeCallbacks);
                        if (inPlace is not null) return inPlace;
                    }

                    return new JsonObject
                    {
                        ["type"] = 4,
                        ["data"] = new JsonObject { ["content"] = banner },
                    };
                }
                else if (customId.StartsWith("clnr_page:", StringComparison.Ordinal) && runtimeCallbacks is not null)
                {
                    var parts = customId.Split(':');
                    var targetPage = parts.Length >= 3 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pg) ? Math.Max(0, pg) : 0;
                    var inPlace = await TryBuildInPlaceUpdatedMessageAsync(data, cfg, targetPage, null, ct, runtimeCallbacks);
                    if (inPlace is not null) return inPlace;
                }
            }
            catch (InvalidOperationException quotaEx)
            {
                return BuildEphemeralMessage($"⚠️ **Exclusion Quota Reached:** {quotaEx.Message}");
            }
        }

        return BuildEphemeralMessage("✅ Managerr Cleanerr received your Discord interaction.");
    }

    private async Task<JsonObject> HandleAutocompleteInteractionAsync(
        JsonObject data,
        CleanerrDiscordRuntimeCallbacks callbacks,
        CancellationToken ct)
    {
        var opts = data["data"]?["options"]?.AsArray();
        var subObj = opts?.FirstOrDefault()?.AsObject();
        var subName = (subObj?["name"]?.GetValue<string>() ?? "").ToLowerInvariant();
        var subOpts = subObj?["options"]?.AsArray();
        var focusedOpt = subOpts?
            .Select(o => o?.AsObject())
            .FirstOrDefault(o => o?["focused"]?.GetValue<bool>() == true);
        var focusedName = (focusedOpt?["name"]?.GetValue<string>() ?? "").ToLowerInvariant();
        var focusedVal = focusedOpt?["value"]?.ToString()?.Trim() ?? "";

        var choices = new JsonArray();

        if (subName == "run" && focusedName == "workflow" && callbacks.ListWorkflowsAsync is not null)
        {
            var workflows = await callbacks.ListWorkflowsAsync(ct);
            var filtered = string.IsNullOrWhiteSpace(focusedVal)
                ? workflows
                : workflows.Where(w =>
                    w.Name.Contains(focusedVal, StringComparison.OrdinalIgnoreCase) ||
                    w.Id.ToString(CultureInfo.InvariantCulture).Contains(focusedVal, StringComparison.OrdinalIgnoreCase));

            foreach (var w in filtered.Take(25))
            {
                var modeLabel = w.DryRun ? "Dry-Run" : "LIVE";
                var stateLabel = w.Enabled ? "Enabled" : "Disabled";
                var name = TruncateText($"{w.Name} ({stateLabel} • {modeLabel})", 98);
                choices.Add(new JsonObject
                {
                    ["name"] = name,
                    ["value"] = $"id:{w.Id}",
                });
            }

            return new JsonObject
            {
                ["type"] = 8,
                ["data"] = new JsonObject { ["choices"] = choices },
            };
        }

        var allMedia = await callbacks.ListMediaItemsAsync(ct);
        IEnumerable<CleanerrMediaItem> candidates = allMedia;

        if (subName == "postpone")
        {
            var staged = await callbacks.ListQueueAsync("staged", ct);
            var stagedIds = staged.Select(s => s.MediaItemId).ToHashSet();
            candidates = allMedia.Where(m => stagedIds.Contains(m.Id));
        }
        else if (subName is "exclude" or "why")
        {
            candidates = allMedia.Where(m => m.MediaType is "movie" or "show");
        }

        var dedup = DeduplicateBySeriesOrMovie(candidates.ToList(), new HashSet<long>());
        if (!string.IsNullOrWhiteSpace(focusedVal))
        {
            dedup = dedup
                .Where(d => d.Label.Contains(focusedVal, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        foreach (var d in dedup.Take(25))
        {
            var gb = d.Bytes / (1024.0 * 1024.0 * 1024.0);
            var kindLabel = d.IsSeries ? "TV Series" : "Movie";
            var name = TruncateText($"{d.Label} ({kindLabel} • {gb:F1} GB)", 98);
            choices.Add(new JsonObject
            {
                ["name"] = name,
                ["value"] = $"id:{d.Id}",
            });
        }

        return new JsonObject
        {
            ["type"] = 8,
            ["data"] = new JsonObject { ["choices"] = choices },
        };
    }

    private async Task<JsonObject> HandleSlashCommandInteractionAsync(
        JsonObject data,
        CleanerrDiscordSettings cfg,
        string discordUsername,
        string actorUsername,
        long? actorUserId,
        int? quotaCap,
        CleanerrDiscordRuntimeCallbacks callbacks,
        CancellationToken ct)
    {
        var cmdData = data["data"]?.AsObject();
        var subObj = cmdData?["options"]?.AsArray().FirstOrDefault()?.AsObject();
        var subName = (subObj?["name"]?.GetValue<string>() ?? "status").ToLowerInvariant();
        var subOpts = subObj?["options"]?.AsArray() ?? [];

        string? GetStrOpt(string key) =>
            subOpts.Select(o => o?.AsObject()).FirstOrDefault(o => string.Equals(o?["name"]?.GetValue<string>(), key, StringComparison.OrdinalIgnoreCase))?["value"]?.ToString();

        int? GetIntOpt(string key) =>
            int.TryParse(GetStrOpt(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv) ? iv : null;

        bool? GetBoolOpt(string key)
        {
            var opt = subOpts.Select(o => o?.AsObject()).FirstOrDefault(o => string.Equals(o?["name"]?.GetValue<string>(), key, StringComparison.OrdinalIgnoreCase));
            var valNode = opt?["value"];
            if (valNode is null) return null;
            if (valNode.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
            {
                return valNode.GetValue<bool>();
            }
            return bool.TryParse(valNode.ToString(), out var bv) ? bv : null;
        }

        try
        {
            switch (subName)
            {
                case "status":
                {
                    var stats = await callbacks.GetOverviewStatsAsync(ct);
                    var totalGb = stats.TotalMediaSizeBytes / (1024.0 * 1024.0 * 1024.0);
                    var stagedGb = stats.StagedReclaimableBytes / (1024.0 * 1024.0 * 1024.0);
                    return new JsonObject
                    {
                        ["type"] = 4,
                        ["data"] = new JsonObject
                        {
                            ["embeds"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["title"] = "📊 Managerr Cleanerr — Lifecycle Status",
                                    ["color"] = 0x38BDF8,
                                    ["fields"] = new JsonArray
                                    {
                                        new JsonObject { ["name"] = "📚 Tracked Media", ["value"] = $"**{stats.TotalMediaItems}** items (`{totalGb:F1} GB`)", ["inline"] = true },
                                        new JsonObject { ["name"] = "🧹 Leaving Soon", ["value"] = $"**{stats.StagedCount}** staged (`{stagedGb:F1} GB` reclaimable)", ["inline"] = true },
                                        new JsonObject { ["name"] = "🛡️ Exclusions", ["value"] = $"**{stats.ActiveExclusionsCount}** active", ["inline"] = true },
                                        new JsonObject { ["name"] = "💿 Free Disk Space", ["value"] = $"**{stats.DiskFreeGb:F1} GB** (`{stats.DiskFreePercent:F1}%`)", ["inline"] = true },
                                        new JsonObject { ["name"] = "⚙️ Active Rules", ["value"] = $"**{stats.EnabledWorkflowsCount}** enabled", ["inline"] = true },
                                        new JsonObject { ["name"] = "🛟 Rescued / 🗑️ Deleted", ["value"] = $"**{stats.RescuedTotalCount}** / **{stats.DeletedTotalCount}**", ["inline"] = true },
                                    },
                                    ["footer"] = new JsonObject { ["text"] = "Zero Plex Collections • Use /cleanerr leaving-soon or /cleanerr why" },
                                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                                },
                            },
                        },
                    };
                }

                case "leaving-soon":
                {
                    var page = Math.Max(0, (GetIntOpt("page") ?? 1) - 1);
                    var staged = await callbacks.ListQueueAsync("staged", ct);
                    if (staged.Count == 0)
                    {
                        return BuildEphemeralMessage("✨ **Leaving Soon is empty!** Nothing is currently staged for cleanup.");
                    }

                    var allMedia = await callbacks.ListMediaItemsAsync(ct);
                    var byId = allMedia.ToDictionary(m => m.Id);
                    var stagedMedia = staged
                        .Select(q => byId.GetValueOrDefault(q.MediaItemId))
                        .Where(m => m is not null)
                        .Select(m => m!)
                        .ToList();
                    var minGrace = staged.Min(q => q.GraceDays);
                    var magicSecret = await GetMagicSecretAsync(ct);
                    var defaultPostponeDays = await GetDefaultPostponeDaysAsync(ct);
                    var payload = BuildDiscordStagedPayload(
                        stagedMedia,
                        minGrace,
                        "Leaving Soon",
                        cfg.PublicBaseUrl,
                        magicSecret,
                        workflowId: null,
                        workflowName: "Leaving Soon Queue",
                        includeComponents: true,
                        pageIndex: page,
                        defaultPostponeDays: defaultPostponeDays,
                        webhookBotUsername: cfg.WebhookBotUsername);

                    return new JsonObject
                    {
                        ["type"] = 4,
                        ["data"] = new JsonObject
                        {
                            ["embeds"] = payload["embeds"]?.DeepClone(),
                            ["components"] = payload["components"]?.DeepClone() ?? new JsonArray(),
                        },
                    };
                }

                case "why":
                {
                    var query = GetStrOpt("title") ?? GetStrOpt("query") ?? "";
                    var allMedia = await callbacks.ListMediaItemsAsync(ct);
                    var target = ResolveMediaFromSlashQuery(allMedia, query);
                    if (target is null)
                    {
                        return BuildEphemeralMessage($"🔍 No tracked movie or series matched `{query}`.");
                    }

                    var trace = await callbacks.ExplainMediaAsync(target.Id, ct);
                    var lines = new List<string>
                    {
                        $"**Verdict:** {(trace.WouldMatch ? "⚠️ **WOULD MATCH CLEANUP**" : "✅ **PROTECTED / NOT MATCHED**")}",
                    };
                    if (trace.IsExcluded)
                    {
                        lines.Add($"🛡️ **Excluded:** {trace.ExclusionReason}");
                    }
                    if (trace.IsCurrentlyStaged)
                    {
                        lines.Add($"⏳ **Currently Staged:** Deletes `{trace.StagedDeleteAt}`");
                    }
                    if (!string.IsNullOrWhiteSpace(trace.ClassificationKind))
                    {
                        lines.Add($"🏷️ **Classification:** `{trace.ClassificationKind}`");
                    }

                    foreach (var sec in trace.Sections.Take(4))
                    {
                        lines.Add($"\n**Section {sec.SectionIndex + 1} ({sec.SectionOperator})** — {(sec.SectionPassed ? "MATCHED" : "NOT MATCHED")}");
                        foreach (var r in sec.Rules.Take(8))
                        {
                            var icon = r.Passed ? "✅" : "❌";
                            var conn = r.RuleConnector is not null ? $"`{r.RuleConnector}` " : "";
                            lines.Add($"{icon} {conn}**{r.FieldLabel}**: `{r.ActualValue}` {r.Comparator} `{r.ExpectedTarget ?? ""}`");
                        }
                    }

                    foreach (var note in trace.GuardNotes.Take(3))
                    {
                        lines.Add($"ℹ️ {note}");
                    }

                    var desc = string.Join('\n', lines);
                    if (desc.Length > 3950) desc = desc[..3950] + "…";

                    return new JsonObject
                    {
                        ["type"] = 4,
                        ["data"] = new JsonObject
                        {
                            ["flags"] = 64,
                            ["embeds"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["title"] = $"🔎 Test Media Explainer — {trace.Title} ({trace.MediaType})",
                                    ["description"] = desc,
                                    ["color"] = trace.IsExcluded || !trace.WouldMatch ? 0x22C55E : 0xF59E0B,
                                    ["footer"] = new JsonObject { ["text"] = trace.Summary },
                                },
                            },
                        },
                    };
                }

                case "exclude":
                {
                    var query = GetStrOpt("title") ?? GetStrOpt("query") ?? "";
                    var duration = (GetStrOpt("duration") ?? "permanent").ToLowerInvariant();
                    var customReason = GetStrOpt("reason");
                    var allMedia = await callbacks.ListMediaItemsAsync(ct);
                    var target = ResolveMediaFromSlashQuery(allMedia, query);
                    if (target is null)
                    {
                        return BuildEphemeralMessage($"🔍 Could not find any tracked media matching `{query}`.");
                    }

                    string? expiresAt = duration switch
                    {
                        "30d" => DateTimeOffset.UtcNow.AddDays(30).ToString("O"),
                        "90d" => DateTimeOffset.UtcNow.AddDays(90).ToString("O"),
                        "365d" => DateTimeOffset.UtcNow.AddDays(365).ToString("O"),
                        _ => null,
                    };

                    var reason = string.IsNullOrWhiteSpace(customReason)
                        ? $"Excluded via /cleanerr exclude ({duration}) by @{discordUsername}"
                        : $"{customReason} (via Discord @{discordUsername})";

                    var titles = await callbacks.ExcludeMediaIdsAsync(
                        [target.Id],
                        actorUsername,
                        reason,
                        actorUserId,
                        quotaCap,
                        expiresAt,
                        ct);

                    var label = titles.FirstOrDefault() ?? FormatSeriesOrMovieTitle(target);
                    var ttlText = expiresAt is null ? "Permanently" : $"for {duration}";
                    return new JsonObject
                    {
                        ["type"] = 4,
                        ["data"] = new JsonObject
                        {
                            ["content"] = $"🛡️ **Excluded {ttlText}:** `{label}` (by **@{discordUsername}** — protected from Cleanerr!)",
                        },
                    };
                }

                case "postpone":
                {
                    var query = GetStrOpt("title") ?? GetStrOpt("query") ?? "";
                    var defaultPostponeDays = await GetDefaultPostponeDaysAsync(ct);
                    var days = Math.Clamp(GetIntOpt("days") ?? defaultPostponeDays, 1, 365);
                    var allMedia = await callbacks.ListMediaItemsAsync(ct);
                    var target = ResolveMediaFromSlashQuery(allMedia, query);
                    if (target is null)
                    {
                        return BuildEphemeralMessage($"🔍 Could not find any staged media matching `{query}`.");
                    }

                    var updated = await callbacks.PostponeMediaIdsAsync(
                        [target.Id],
                        days,
                        $"Postponed +{days}d via /cleanerr postpone by @{discordUsername}",
                        ct);

                    if (updated.Count == 0)
                    {
                        return BuildEphemeralMessage($"ℹ️ `{FormatSeriesOrMovieTitle(target)}` is not currently in the Leaving Soon queue.");
                    }

                    var nextDelete = DateTimeOffset.TryParse(updated[0].DeleteAt, out var dto)
                        ? $"<t:{dto.ToUnixTimeSeconds()}:R>"
                        : updated[0].DeleteAt;
                    return new JsonObject
                    {
                        ["type"] = 4,
                        ["data"] = new JsonObject
                        {
                            ["content"] = $"⏳ **Postponed +{days} Days:** `{FormatSeriesOrMovieTitle(target)}` ({updated.Count} item(s)) — now scheduled {nextDelete} (by **@{discordUsername}**).",
                        },
                    };
                }

                case "exclusions":
                {
                    var page = Math.Max(0, (GetIntOpt("page") ?? 1) - 1);
                    var rules = await callbacks.ListExclusionsAsync(ct);
                    if (rules.Count == 0)
                    {
                        return BuildEphemeralMessage("ℹ️ No Cleanerr exclusions are currently configured.");
                    }

                    const int pageSize = 12;
                    var totalPages = Math.Max(1, (int)Math.Ceiling(rules.Count / (double)pageSize));
                    var safePage = Math.Clamp(page, 0, totalPages - 1);
                    var slice = rules.Skip(safePage * pageSize).Take(pageSize).ToList();
                    var lines = slice.Select((r, idx) =>
                    {
                        var exp = string.IsNullOrWhiteSpace(r.ExpiresAt)
                            ? "Permanent"
                            : DateTimeOffset.TryParse(r.ExpiresAt, out var d) ? $"Until <t:{d.ToUnixTimeSeconds()}:d>" : r.ExpiresAt;
                        return $"{(safePage * pageSize) + idx + 1}. 🛡️ **{r.Value}** (`{r.RuleType}` • `{r.Granularity}`) — *{exp}* by `{r.CreatedBy}`";
                    });

                    var delSelectOpts = new JsonArray();
                    foreach (var r in slice.Take(25))
                    {
                        delSelectOpts.Add(new JsonObject
                        {
                            ["label"] = TruncateText($"{r.Value} ({r.RuleType})", 95),
                            ["value"] = r.Id.ToString(CultureInfo.InvariantCulture),
                            ["description"] = TruncateText($"By {r.CreatedBy} • {r.Granularity}", 95),
                            ["emoji"] = new JsonObject { ["name"] = "🗑️" },
                        });
                    }

                    var components = new JsonArray();
                    if (delSelectOpts.Count > 0)
                    {
                        components.Add(new JsonObject
                        {
                            ["type"] = 1,
                            ["components"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["type"] = 3,
                                    ["custom_id"] = "clnr_del_excl",
                                    ["placeholder"] = "🗑️ Select exclusion rule(s) to revoke...",
                                    ["min_values"] = 1,
                                    ["max_values"] = delSelectOpts.Count,
                                    ["options"] = delSelectOpts,
                                },
                            },
                        });
                    }

                    return new JsonObject
                    {
                        ["type"] = 4,
                        ["data"] = new JsonObject
                        {
                            ["flags"] = 64,
                            ["embeds"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["title"] = $"🛡️ Active Cleanerr Exclusions ({rules.Count})",
                                    ["description"] = string.Join('\n', lines),
                                    ["color"] = 0xA855F7,
                                    ["footer"] = new JsonObject { ["text"] = $"Page {safePage + 1}/{totalPages}" },
                                },
                            },
                            ["components"] = components,
                        },
                    };
                }

                case "run":
                {
                    if (callbacks.RunWorkflowAsync is null)
                    {
                        return BuildEphemeralMessage("⚠️ Workflow execution is not configured for Discord interactions.");
                    }

                    var wfArg = GetStrOpt("workflow")?.Trim();
                    var dryRunOverride = GetBoolOpt("dry_run");
                    long? targetWfId = null;

                    if (!string.IsNullOrWhiteSpace(wfArg))
                    {
                        if (wfArg.StartsWith("id:", StringComparison.OrdinalIgnoreCase) &&
                            long.TryParse(wfArg[3..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
                        {
                            targetWfId = parsedId;
                        }
                        else if (long.TryParse(wfArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numId))
                        {
                            targetWfId = numId;
                        }
                        else if (callbacks.ListWorkflowsAsync is not null)
                        {
                            var workflows = await callbacks.ListWorkflowsAsync(ct);
                            var matched = workflows.FirstOrDefault(w => string.Equals(w.Name, wfArg, StringComparison.OrdinalIgnoreCase))
                                          ?? workflows.FirstOrDefault(w => w.Name.Contains(wfArg, StringComparison.OrdinalIgnoreCase));
                            if (matched is null)
                            {
                                return BuildEphemeralMessage($"🔍 No Cleanerr workflow matched `{wfArg}`.");
                            }

                            targetWfId = matched.Id;
                        }
                    }
                    else if (callbacks.ListWorkflowsAsync is not null)
                    {
                        var workflows = await callbacks.ListWorkflowsAsync(ct);
                        var firstEnabled = workflows.FirstOrDefault(w => w.Enabled) ?? workflows.FirstOrDefault();
                        if (firstEnabled is null)
                        {
                            return BuildEphemeralMessage("ℹ️ No Cleanerr workflows are currently configured.");
                        }

                        targetWfId = firstEnabled.Id;
                    }

                    var runResult = await callbacks.RunWorkflowAsync(targetWfId, dryRunOverride, ct);
                    if (runResult is null)
                    {
                        return BuildEphemeralMessage("ℹ️ No matching enabled workflow was found to execute.");
                    }

                    var reclaimGb = (runResult.Summary.ReclaimableSpaceBytes > 0
                        ? runResult.Summary.ReclaimableSpaceBytes
                        : runResult.Summary.SpaceBytes) / (1024.0 * 1024.0 * 1024.0);
                    return new JsonObject
                    {
                        ["type"] = 4,
                        ["data"] = new JsonObject
                        {
                            ["embeds"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["title"] = $"{(runResult.DryRun ? "🧪" : "🧹")} Cleanerr Workflow Executed — {runResult.Status}",
                                    ["color"] = runResult.DryRun ? 0x38BDF8 : 0xF59E0B,
                                    ["fields"] = new JsonArray
                                    {
                                        new JsonObject { ["name"] = "⚙️ Workflow ID", ["value"] = $"`{runResult.WorkflowId?.ToString(CultureInfo.InvariantCulture) ?? "default"}`", ["inline"] = true },
                                        new JsonObject { ["name"] = "🛡️ Mode", ["value"] = runResult.DryRun ? "`Dry-Run`" : "`LIVE`", ["inline"] = true },
                                        new JsonObject { ["name"] = "⏳ Staged", ["value"] = $"**{runResult.Summary.StagedCount}**", ["inline"] = true },
                                        new JsonObject { ["name"] = "🗑️ Deleted", ["value"] = $"**{runResult.Summary.DeletedCount}**", ["inline"] = true },
                                        new JsonObject { ["name"] = "💾 Space", ["value"] = $"**{reclaimGb:F2} GB**", ["inline"] = true },
                                        new JsonObject { ["name"] = "👤 Triggered By", ["value"] = $"`@{discordUsername}`", ["inline"] = true },
                                    },
                                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                                },
                            },
                        },
                    };
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            return BuildEphemeralMessage($"⚠️ {ex.Message}");
        }

        return BuildEphemeralMessage("ℹ️ Unknown `/cleanerr` subcommand.");
    }

    private async Task<JsonObject?> TryBuildInPlaceUpdatedMessageAsync(
        JsonObject interactionData,
        CleanerrDiscordSettings cfg,
        int? requestedPage,
        string? latestActivityBanner,
        CancellationToken ct,
        CleanerrDiscordRuntimeCallbacks callbacks)
    {
        var msgId = interactionData["message"]?["id"]?.GetValue<string>();
        var allMedia = await callbacks.ListMediaItemsAsync(ct);
        var byId = allMedia.ToDictionary(m => m.Id);
        var stagedNow = await callbacks.ListQueueAsync("staged", ct);
        var stagedIdsSet = stagedNow.Select(q => q.MediaItemId).ToHashSet();

        IReadOnlyList<CleanerrMediaItem> originalItems = [];
        var wfName = "Leaving Soon Queue";
        var graceDays = stagedNow.Count > 0 ? stagedNow.Min(q => q.GraceDays) : 7;
        long? wfId = null;
        var pageIdx = requestedPage ?? 0;

        if (!string.IsNullOrWhiteSpace(msgId) && dbFactory is not null)
        {
            await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
            var tracked = await conn.QuerySingleOrDefaultAsync<TrackedDiscordMessageRow>(
                "SELECT * FROM cleanerr_discord_messages WHERE message_id = @MsgId;",
                new { MsgId = msgId });

            if (tracked is not null)
            {
                wfId = tracked.WorkflowId;
                wfName = tracked.WorkflowName;
                graceDays = tracked.GraceDays;
                pageIdx = requestedPage ?? tracked.PageIndex;
                var trackedIds = JsonSerializer.Deserialize<long[]>(tracked.StagedItemIdsJson, JsonOpts) ?? [];
                originalItems = trackedIds
                    .Select(id => byId.GetValueOrDefault(id))
                    .Where(m => m is not null)
                    .Select(m => m!)
                    .ToList();

                if (requestedPage.HasValue && requestedPage.Value != tracked.PageIndex)
                {
                    await SaveDiscordMessageStateAsync(msgId, tracked.ChannelId, wfId, wfName, graceDays, pageIdx, trackedIds, tracked.MessageKind, ct);
                }
            }
        }

        if (originalItems.Count == 0)
        {
            originalItems = stagedNow
                .Select(q => byId.GetValueOrDefault(q.MediaItemId))
                .Where(m => m is not null)
                .Select(m => m!)
                .ToList();
        }

        var protectedIds = originalItems
            .Where(i => !stagedIdsSet.Contains(i.Id))
            .Select(i => i.Id)
            .ToHashSet();

        var magicSecret = await GetMagicSecretAsync(ct);
        var defaultPostponeDays = await GetDefaultPostponeDaysAsync(ct);
        var rebuilt = BuildDiscordStagedPayload(
            originalItems,
            graceDays,
            "Leaving Soon",
            cfg.PublicBaseUrl,
            magicSecret,
            wfId,
            wfName,
            includeComponents: true,
            pageIndex: pageIdx,
            latestActivityBanner: latestActivityBanner,
            protectedMediaIds: protectedIds,
            defaultPostponeDays: defaultPostponeDays,
            webhookBotUsername: cfg.WebhookBotUsername);

        // Discord Interaction Callback Type 7 = UPDATE_MESSAGE (in-place embed & button refresh!)
        return new JsonObject
        {
            ["type"] = 7,
            ["data"] = new JsonObject
            {
                ["embeds"] = rebuilt["embeds"]?.DeepClone(),
                ["components"] = rebuilt["components"]?.DeepClone() ?? new JsonArray(),
            },
        };
    }

    private async Task<JsonObject> DeliverPayloadToDiscordAsync(
        JsonObject payload,
        bool includeComponents,
        CleanerrDiscordSettings cfg,
        string? webhookOverride,
        CancellationToken ct,
        Func<JsonObject>? fallbackWithoutComponentsFactory = null)
    {
        var botTok = await GetEffectiveBotTokenAsync(ct);
        var whClean = (!string.IsNullOrWhiteSpace(webhookOverride) ? webhookOverride : cfg.WebhookUrl).Trim();
        var chClean = cfg.ChannelId.Trim();

        if (string.IsNullOrWhiteSpace(whClean) && (string.IsNullOrWhiteSpace(botTok) || string.IsNullOrWhiteSpace(chClean)))
        {
            if (botTok.StartsWith("mock://", StringComparison.OrdinalIgnoreCase))
            {
                return new JsonObject
                {
                    ["sent"] = true,
                    ["mock"] = true,
                    ["delivery_mode"] = "mock",
                    ["message_id"] = $"mock-msg-{Guid.NewGuid():N}",
                    ["channel_id"] = string.IsNullOrWhiteSpace(chClean) ? "mock-channel" : chClean,
                    ["payload"] = payload,
                };
            }
            return new JsonObject { ["sent"] = false, ["reason"] = "no_webhook_or_items" };
        }

        if (whClean.StartsWith("mock://", StringComparison.OrdinalIgnoreCase) || botTok.StartsWith("mock://", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject
            {
                ["sent"] = true,
                ["mock"] = true,
                ["delivery_mode"] = "mock",
                ["message_id"] = $"mock-msg-{Guid.NewGuid():N}",
                ["channel_id"] = string.IsNullOrWhiteSpace(chClean) ? "mock-channel" : chClean,
                ["payload"] = payload,
            };
        }

        string? botHint = null;

        // Tier 1: Bot REST API with explicit ChannelId OR auto-discovered channel_id from webhook URL
        if (!string.IsNullOrWhiteSpace(botTok) && botTok.Contains('.'))
        {
            var resolvedChannel = chClean;
            if (string.IsNullOrWhiteSpace(resolvedChannel) && whClean.Contains("/api/webhooks/", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var (st, whBody) = await httpGateway.SendJsonAsync(HttpMethod.Get, whClean.Split('?')[0], null, null, ct);
                    if (st is >= 200 and < 300)
                    {
                        var whDoc = JsonNode.Parse(whBody)?.AsObject();
                        resolvedChannel = whDoc?["channel_id"]?.GetValue<string>()?.Trim() ?? "";
                    }
                }
                catch
                {
                    // Ignore webhook metadata inspection error
                }
            }

            if (!string.IsNullOrWhiteSpace(resolvedChannel))
            {
                var botPayload = payload.DeepClone().AsObject();
                botPayload.Remove("username");
                var (botStatus, botRespBody) = await httpGateway.SendJsonAsync(
                    HttpMethod.Post,
                    $"https://discord.com/api/v10/channels/{resolvedChannel}/messages",
                    botPayload.ToJsonString(),
                    new Dictionary<string, string> { ["Authorization"] = $"Bot {botTok}" },
                    ct);

                if (botStatus is >= 200 and < 300)
                {
                    string? msgId = null;
                    try
                    {
                        msgId = JsonNode.Parse(botRespBody)?["id"]?.GetValue<string>();
                    }
                    catch (JsonException)
                    {
                        // Ignore malformed response body
                    }

                    return new JsonObject
                    {
                        ["sent"] = true,
                        ["interactive_buttons"] = includeComponents,
                        ["delivery_mode"] = "bot_api",
                        ["channel_id"] = resolvedChannel,
                        ["message_id"] = msgId,
                        ["status_code"] = botStatus,
                    };
                }

                botHint = $"Bot API returned HTTP {botStatus}: {TruncateSnippet(botRespBody, 100)}";
            }
        }

        // Tier 2: App-linked Webhook with ?with_components=true
        if (!string.IsNullOrWhiteSpace(whClean))
        {
            if (includeComponents)
            {
                var whWithComp = AppendWithComponentsQuery(whClean);
                var (st2, body2) = await httpGateway.SendJsonAsync(HttpMethod.Post, whWithComp, payload.ToJsonString(), null, ct);
                if (st2 is >= 200 and < 300)
                {
                    string? msgId = null;
                    try { msgId = JsonNode.Parse(body2)?["id"]?.GetValue<string>(); } catch (JsonException) { /* ignore */ }
                    return new JsonObject
                    {
                        ["sent"] = true,
                        ["interactive_buttons"] = true,
                        ["delivery_mode"] = "app_webhook",
                        ["message_id"] = msgId,
                        ["status_code"] = st2,
                    };
                }
            }

            // Tier 3: Standard Webhook fallback (Rich Embed + HMAC Magic Links, no component rows)
            var fallbackPayload = fallbackWithoutComponentsFactory is not null ? fallbackWithoutComponentsFactory() : payload;
            var (st3, _) = await httpGateway.SendJsonAsync(HttpMethod.Post, whClean, fallbackPayload.ToJsonString(), null, ct);
            return new JsonObject
            {
                ["sent"] = st3 is >= 200 and < 300,
                ["interactive_buttons"] = false,
                ["delivery_mode"] = "standard_webhook",
                ["hint"] = botHint,
                ["status_code"] = st3,
            };
        }

        return new JsonObject { ["sent"] = false, ["reason"] = "bot_delivery_failed", ["hint"] = botHint };
    }

    private async Task SaveDiscordMessageStateAsync(
        string messageId,
        string channelId,
        long? workflowId,
        string workflowName,
        int graceDays,
        int pageIndex,
        IReadOnlyList<long> stagedItemIds,
        string messageKind,
        CancellationToken ct)
    {
        if (dbFactory is null || string.IsNullOrWhiteSpace(messageId)) return;
        var now = DateTimeOffset.UtcNow.ToString("O");
        var idsJson = JsonSerializer.Serialize(stagedItemIds, JsonOpts);

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                INSERT INTO cleanerr_discord_messages (
                    message_id, channel_id, workflow_id, workflow_name, grace_days, page_index, staged_item_ids_json, message_kind, created_at, updated_at
                ) VALUES (
                    @MsgId, @ChId, @WfId, @WfName, @Grace, @Page, @Ids, @Kind, @Now, @Now
                )
                ON CONFLICT(message_id) DO UPDATE SET
                    page_index = excluded.page_index,
                    staged_item_ids_json = excluded.staged_item_ids_json,
                    message_kind = excluded.message_kind,
                    updated_at = excluded.updated_at;
                """,
                new
                {
                    MsgId = messageId,
                    ChId = channelId,
                    WfId = workflowId,
                    WfName = workflowName,
                    Grace = graceDays,
                    Page = pageIndex,
                    Ids = idsJson,
                    Kind = messageKind,
                    Now = now,
                },
                transaction: tx),
            ct);
    }

    private async Task<IReadOnlyList<CleanerrMediaItem>> LoadTestPreviewItemsAsync(CancellationToken ct)
    {
        if (dbFactory is not null)
        {
            try
            {
                await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
                var rows = (await conn.QueryAsync<SampleMediaRow>(
                    """
                    SELECT id, plex_rating_key, parent_rating_key, grandparent_rating_key, parent_title, grandparent_title,
                           library_id, work_id, unit_id, media_type, title, year, season_number, episode_number,
                           size_bytes, resolution, added_at, last_viewed_at, view_count, updated_at, classification_kind
                    FROM cleanerr_media_items
                    WHERE media_type IN ('movie', 'show')
                    ORDER BY size_bytes DESC
                    LIMIT 5;
                    """)).ToList();

                if (rows.Count > 0)
                {
                    return rows.Select(r => new CleanerrMediaItem(
                        Id: r.Id,
                        PlexRatingKey: r.PlexRatingKey,
                        ParentRatingKey: r.ParentRatingKey,
                        GrandparentRatingKey: r.GrandparentRatingKey,
                        ParentTitle: r.ParentTitle,
                        GrandparentTitle: r.GrandparentTitle,
                        LibraryId: r.LibraryId,
                        WorkId: r.WorkId,
                        UnitId: r.UnitId,
                        MediaType: r.MediaType,
                        Title: r.Title,
                        Year: r.Year,
                        SeasonNumber: r.SeasonNumber,
                        EpisodeNumber: r.EpisodeNumber,
                        SizeBytes: r.SizeBytes,
                        Resolution: r.Resolution,
                        Rating: null,
                        Genres: [],
                        Tags: [],
                        Monitored: true,
                        AddedAt: r.AddedAt,
                        LastViewedAt: r.LastViewedAt,
                        ViewCount: r.ViewCount,
                        ViewedBy: [],
                        ActiveStreamCount: 0,
                        SeerrId: null,
                        SeerrRequestedBy: null,
                        SeerrRequestedAt: null,
                        RequesterWatched: false,
                        UpdatedAt: r.UpdatedAt,
                        ClassificationKind: r.ClassificationKind)).ToList();
                }
            }
            catch
            {
                // Fall back to synthetic preview items if DB is uninitialized or empty
            }
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        return
        [
            new CleanerrMediaItem(1001, "preview-1", null, null, null, null, null, null, null, "movie", "Blade Runner 2049", 2017, null, null, 24L * 1024 * 1024 * 1024, "2160p", null, [], [], true, now, null, 0, [], 0, null, null, null, false, now),
            new CleanerrMediaItem(1002, "preview-2", null, null, null, null, null, null, null, "show", "Severance", 2022, null, null, 42L * 1024 * 1024 * 1024, "1080p", null, [], [], true, now, null, 0, [], 0, null, null, null, false, now),
            new CleanerrMediaItem(1003, "preview-3", null, null, null, null, null, null, null, "movie", "Dune: Part Two", 2024, null, null, 18L * 1024 * 1024 * 1024, "2160p", null, [], [], true, now, null, 0, [], 0, null, null, null, false, now),
        ];
    }

    private async Task<string?> GetSettingWithFallbackAsync(string dbKey, string configKey, CancellationToken ct)
    {
        var dbVal = await settings.GetAsync(dbKey, ct);
        if (!string.IsNullOrWhiteSpace(dbVal))
        {
            return dbVal.Trim();
        }

        var cfgVal = configuration?[configKey];
        return string.IsNullOrWhiteSpace(cfgVal) ? null : cfgVal.Trim();
    }

    private async Task<string> GetEffectiveBotTokenAsync(CancellationToken ct)
    {
        var dbTok = await settings.GetSecretAsync("cleanerr.discord_bot_token", ct);
        if (!string.IsNullOrWhiteSpace(dbTok))
        {
            return dbTok.Trim();
        }

        return (configuration?["Managerr:Cleanerr:Discord:BotToken"] ?? "").Trim();
    }

    private static string ResolveRequiredDiscordScope(int interactionType, JsonObject data)
    {
        if (interactionType == 4)
        {
            return PermissionScopes.CleanerrRead;
        }

        if (interactionType == 2)
        {
            var subName = data["data"]?["options"]?.AsArray().FirstOrDefault()?["name"]?.GetValue<string>()?.ToLowerInvariant();
            return subName switch
            {
                "status" or "leaving-soon" or "why" or "exclusions" => PermissionScopes.CleanerrRead,
                "postpone" => PermissionScopes.CleanerrRescue,
                "exclude" => PermissionScopes.CleanerrExclude,
                "run" => PermissionScopes.CleanerrExecute,
                _ => PermissionScopes.CleanerrRead,
            };
        }

        if (interactionType == 3)
        {
            var customId = data["data"]?["custom_id"]?.GetValue<string>() ?? "";
            if (customId.StartsWith("clnr_page:", StringComparison.Ordinal) ||
                customId is "clnr_test_btn" or "clnr_test_sel")
            {
                return PermissionScopes.CleanerrRead;
            }

            if (customId.StartsWith("clnr_postpone:", StringComparison.Ordinal) ||
                customId.StartsWith("clnr_postpone_all:", StringComparison.Ordinal))
            {
                return PermissionScopes.CleanerrRescue;
            }

            if (customId.StartsWith("clnr_sel_excl:", StringComparison.Ordinal) ||
                customId.StartsWith("clnr_excl:", StringComparison.Ordinal) ||
                customId.StartsWith("clnr_excl_all:", StringComparison.Ordinal))
            {
                return PermissionScopes.CleanerrExclude;
            }

            if (string.Equals(customId, "clnr_del_excl", StringComparison.Ordinal))
            {
                return PermissionScopes.CleanerrWrite;
            }
        }

        return PermissionScopes.CleanerrRead;
    }

    private static (bool Authorized, string? DenialMessage) CheckDiscordAuthorization(
        CleanerrDiscordSettings cfg,
        JsonObject data,
        PlexUserSummary? matchedUser,
        string discordUsername,
        string requiredScope)
    {
        var mode = NormalizeAuthMode(cfg.AuthMode);

        // Mode 1: Cleanerr frictionless channel-member mode — anyone in the configured Discord channel can view, exclude & postpone!
        if (mode == "channel_members")
        {
            if (!string.IsNullOrWhiteSpace(cfg.ChannelId))
            {
                var interactionChannel = data["channel_id"]?.GetValue<string>() ?? data["channel"]?["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(interactionChannel) ||
                    !string.Equals(interactionChannel.Trim(), cfg.ChannelId.Trim(), StringComparison.Ordinal))
                {
                    return (false, $"⛔ **Permission Denied:** Cleanerr Discord interactions are restricted to the configured Cleanerr channel (<#{cfg.ChannelId}>).");
                }
            }
            return (true, null);
        }

        // Mode 2: Discord Role Allowlist (or Discord Server Administrator bit 0x8)
        if (mode == "discord_roles")
        {
            var permsStr = data["member"]?["permissions"]?.GetValue<string>();
            if (ulong.TryParse(permsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var permBits) && (permBits & 0x8UL) == 0x8UL)
            {
                return (true, null);
            }

            var memberRoles = data["member"]?["roles"]?.AsArray()
                .Select(r => r?.GetValue<string>()?.Trim() ?? "")
                .Where(r => r.Length > 0)
                .ToHashSet(StringComparer.Ordinal) ?? [];

            var allowed = cfg.AllowedRoleIds ?? [];
            if (allowed.Any(memberRoles.Contains))
            {
                return (true, null);
            }

            return (false, $"⛔ **Permission Denied:** Discord account `@{discordUsername}` does not hold any of the configured Cleanerr Discord roles.");
        }

        // Mode 3: Linked Managerr / Plex User RBAC
        if (matchedUser is null || !matchedUser.IsActive)
        {
            return (false, $"⛔ **Permission Denied:** Discord account `@{discordUsername}` is either not linked to an active Plex user or lacks `{requiredScope}` permission (Cleanerr is Plex Admin only by default).");
        }

        if (!matchedUser.EffectivePermissions.Scopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase))
        {
            return (false, $"⛔ **Permission Denied:** Linked user `{matchedUser.Username}` lacks `{requiredScope}` permission.");
        }

        return (true, null);
    }

    private static CleanerrMediaItem? ResolveMediaFromSlashQuery(IReadOnlyList<CleanerrMediaItem> allMedia, string rawQuery)
    {
        var q = rawQuery.Trim();
        if (string.IsNullOrWhiteSpace(q)) return null;

        if (q.StartsWith("id:", StringComparison.OrdinalIgnoreCase) && long.TryParse(q[3..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var exactId))
        {
            return allMedia.FirstOrDefault(m => m.Id == exactId);
        }

        if (long.TryParse(q, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numId))
        {
            var byNum = allMedia.FirstOrDefault(m => m.Id == numId);
            if (byNum is not null) return byNum;
        }

        return allMedia
            .Where(m => m.MediaType is "movie" or "show" or "season" or "episode")
            .OrderBy(m => m.MediaType switch { "show" => 0, "movie" => 1, "season" => 2, _ => 3 })
            .FirstOrDefault(m =>
                string.Equals(m.Title, q, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(FormatItemTitle(m), q, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(FormatSeriesOrMovieTitle(m), q, StringComparison.OrdinalIgnoreCase))
            ?? allMedia
                .OrderBy(m => m.MediaType switch { "show" => 0, "movie" => 1, "season" => 2, _ => 3 })
                .FirstOrDefault(m =>
                    m.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    FormatSeriesOrMovieTitle(m).Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record DeduplicatedTarget(
        int DisplayNumber,
        long Id,
        string Label,
        bool IsSeries,
        int Count,
        long Bytes,
        bool IsProtected,
        string? Classification);

    private static List<DeduplicatedTarget> DeduplicateBySeriesOrMovie(
        IReadOnlyList<CleanerrMediaItem> items,
        IReadOnlySet<long> protectedIds)
    {
        var raw = new List<(long Id, string Label, bool IsSeries, int Count, long Bytes, bool AllProtected, string? Classification)>();
        var seriesIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenMovieIds = new HashSet<long>();

        foreach (var it in items)
        {
            var isProt = protectedIds.Contains(it.Id);
            if (it.MediaType is "season" or "episode" or "show")
            {
                var sName = FormatSeriesOrMovieTitle(it);
                if (!seriesIndex.TryGetValue(sName, out var idx))
                {
                    seriesIndex[sName] = raw.Count;
                    raw.Add((it.Id, sName, true, 1, it.SizeBytes, isProt, it.ClassificationKind));
                }
                else
                {
                    var cur = raw[idx];
                    raw[idx] = (cur.Id, cur.Label, true, cur.Count + 1, cur.Bytes + it.SizeBytes, cur.AllProtected && isProt, cur.Classification ?? it.ClassificationKind);
                }
            }
            else if (seenMovieIds.Add(it.Id))
            {
                raw.Add((it.Id, FormatItemTitle(it), false, 1, it.SizeBytes, isProt, it.ClassificationKind));
            }
        }

        return raw
            .Select((r, i) => new DeduplicatedTarget(i + 1, r.Id, r.Label, r.IsSeries, r.Count, r.Bytes, r.AllProtected, r.Classification))
            .ToList();
    }

    private static JsonObject BuildEphemeralMessage(string content) =>
        new()
        {
            ["type"] = 4,
            ["data"] = new JsonObject
            {
                ["content"] = content,
                ["flags"] = 64,
            },
        };

    private static string NormalizeAuthMode(string? raw) =>
        (raw ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "channel_members" or "channel" or "cleanerr" => "channel_members",
            "discord_roles" or "roles" => "discord_roles",
            "linked_plex_rbac" or "plex_rbac" or "rbac" => "linked_plex_rbac",
            _ => "linked_plex_rbac",
        };

    private static bool TryNormalizeHttpUrl(string? rawUrl, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(rawUrl)) return false;
        var trimmed = rawUrl.Trim().TrimEnd('/');
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
        {
            normalized = trimmed;
            return true;
        }
        return false;
    }

    private static string MaskSecretValue(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return "";
        if (secret.Length <= 8) return "••••";
        return $"{secret[..4]}••••{secret[^4..]}";
    }

    private static string TruncateText(string value, int maxLen) =>
        value.Length > maxLen ? value[..maxLen] : value;

    private static string TruncateSnippet(string value, int maxLen) =>
        value.Length > maxLen ? value[..maxLen] : value;

    private static IReadOnlyList<string> ParseStringArraySafe(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(raw, JsonOpts) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string FormatItemTitle(CleanerrMediaItem it)
    {
        if (it.MediaType == "season")
            return $"{it.ParentTitle ?? it.GrandparentTitle ?? "Series"} — {it.Title}";
        if (it.MediaType == "episode")
        {
            var gp = it.GrandparentTitle ?? it.ParentTitle ?? "Series";
            var s = it.SeasonNumber.HasValue ? $"S{it.SeasonNumber.Value:D2}" : "";
            var e = it.EpisodeNumber.HasValue ? $"E{it.EpisodeNumber.Value:D2}" : "";
            return $"{gp} {s}{e} — {it.Title}".Trim();
        }
        return it.Year.HasValue ? $"{it.Title} ({it.Year.Value})" : it.Title;
    }

    public static string FormatSeriesOrMovieTitle(CleanerrMediaItem it) =>
        it.MediaType switch
        {
            "season" or "episode" => it.GrandparentTitle ?? it.ParentTitle ?? it.Title,
            "show" => it.Title,
            _ => it.Year.HasValue ? $"{it.Title} ({it.Year.Value})" : it.Title,
        };

    private static string AppendWithComponentsQuery(string webhookUrl)
    {
        if (!webhookUrl.Contains("discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase) &&
            !webhookUrl.Contains("discordapp.com/api/webhooks/", StringComparison.OrdinalIgnoreCase))
        {
            return webhookUrl;
        }

        if (webhookUrl.Contains("with_components=true", StringComparison.OrdinalIgnoreCase))
        {
            return webhookUrl;
        }

        return webhookUrl.Contains('?') ? $"{webhookUrl}&with_components=true" : $"{webhookUrl}?with_components=true";
    }

    private sealed class TrackedDiscordMessageRow
    {
        public long Id { get; init; }
        public string MessageId { get; init; } = "";
        public string ChannelId { get; init; } = "";
        public long? WorkflowId { get; init; }
        public string WorkflowName { get; init; } = "";
        public int GraceDays { get; init; }
        public int PageIndex { get; init; }
        public string StagedItemIdsJson { get; init; } = "[]";
        public string MessageKind { get; init; } = "staged";
    }

    private sealed class SampleMediaRow
    {
        public long Id { get; init; }
        public string PlexRatingKey { get; init; } = "";
        public string? ParentRatingKey { get; init; }
        public string? GrandparentRatingKey { get; init; }
        public string? ParentTitle { get; init; }
        public string? GrandparentTitle { get; init; }
        public long? LibraryId { get; init; }
        public long? WorkId { get; init; }
        public long? UnitId { get; init; }
        public string MediaType { get; init; } = "movie";
        public string Title { get; init; } = "";
        public int? Year { get; init; }
        public int? SeasonNumber { get; init; }
        public int? EpisodeNumber { get; init; }
        public long SizeBytes { get; init; }
        public string? Resolution { get; init; }
        public string AddedAt { get; init; } = "";
        public string? LastViewedAt { get; init; }
        public int ViewCount { get; init; }
        public string UpdatedAt { get; init; } = "";
        public string? ClassificationKind { get; init; }
    }
}

/// <summary>
/// Zero-port-forwarding Outbound Discord Gateway WebSocket client (wss://gateway.discord.gg/?v=10&amp;encoding=json, intents=0)
/// receiving Slash Commands, Autocomplete, and Button/Dropdown clicks with auto slash-command registration and immediate reconnect on config change.
/// </summary>
public sealed class CleanerrDiscordGatewayHostedService(
    IAppSettingsService settings,
    ICleanerrDiscordService discordService,
    ICleanerrLifecycleEngine lifecycleEngine,
    ICleanerrHttpGateway httpGateway,
    ILogger<CleanerrDiscordGatewayHostedService> logger,
    IConfiguration? configuration = null) : BackgroundService
{
    private const string GatewayUrl = "wss://gateway.discord.gg/?v=10&encoding=json";
    private readonly Lock _reconnectSync = new();
    private CancellationTokenSource? _cycleCts;
    private string? _lastSyncedScopeSignature;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void OnConfigChanged()
        {
            lock (_reconnectSync)
            {
                try
                {
                    _cycleCts?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Cycle CTS already disposed
                }
            }
        }

        discordService.ConfigChanged += OnConfigChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                lock (_reconnectSync)
                {
                    _cycleCts = cycleCts;
                }
                var cycleToken = cycleCts.Token;

                try
                {
                    var passive = string.Equals(await settings.GetAsync("cleanerr.discord_passive_mode", cycleToken), "true", StringComparison.OrdinalIgnoreCase);
                    var dbTok = (await settings.GetSecretAsync("cleanerr.discord_bot_token", cycleToken) ?? "").Trim();
                    var rawTok = !string.IsNullOrWhiteSpace(dbTok)
                        ? dbTok
                        : (configuration?["Managerr:Cleanerr:Discord:BotToken"] ?? "").Trim();

                    if (passive || rawTok.Length < 40 || !rawTok.Contains('.'))
                    {
                        discordService.SetGatewayStatus(false, null, passive ? "Gateway passive shadow mode enabled." : null);
                        await Task.Delay(TimeSpan.FromSeconds(5), cycleToken);
                        continue;
                    }

                    // Auto-sync /cleanerr slash commands once per (appId + guildId) configuration change
                    var cfg = await discordService.GetSettingsAsync(cycleToken);
                    var sig = $"{cfg.ApplicationId}:{cfg.GuildId}";
                    if (!string.IsNullOrWhiteSpace(cfg.ApplicationId) && _lastSyncedScopeSignature != sig)
                    {
                        try
                        {
                            var syncRes = await discordService.SyncSlashCommandsAsync(cycleToken);
                            if (syncRes.Ok) _lastSyncedScopeSignature = sig;
                        }
                        catch (Exception syncEx) when (syncEx is not OperationCanceledException)
                        {
                            logger.LogDebug(syncEx, "Best-effort Discord slash command sync deferred.");
                        }
                    }

                    using var ws = new ClientWebSocket();
                    await ws.ConnectAsync(new Uri(GatewayUrl), cycleToken);

                    var helloJson = await ReceiveTextMessageAsync(ws, cycleToken);
                    var helloObj = JsonNode.Parse(helloJson)?.AsObject();
                    var hbIntervalMs = helloObj?["d"]?["heartbeat_interval"]?.GetValue<int>() ?? 41250;
                    int? seq = null;

                    var identify = new JsonObject
                    {
                        ["op"] = 2,
                        ["d"] = new JsonObject
                        {
                            ["token"] = rawTok,
                            ["intents"] = 0,
                            ["properties"] = new JsonObject
                            {
                                ["os"] = "linux",
                                ["browser"] = "managerr-cleanerr",
                                ["device"] = "managerr-cleanerr",
                            },
                        },
                    };
                    await SendTextMessageAsync(ws, identify.ToJsonString(), cycleToken);

                    using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(cycleToken);
                    var hbTask = Task.Run(async () =>
                    {
                        while (!hbCts.Token.IsCancellationRequested && ws.State == WebSocketState.Open)
                        {
                            await Task.Delay(hbIntervalMs, hbCts.Token);
                            var hb = new JsonObject { ["op"] = 1, ["d"] = seq.HasValue ? JsonValue.Create(seq.Value) : null };
                            await SendTextMessageAsync(ws, hb.ToJsonString(), hbCts.Token);
                        }
                    }, hbCts.Token);

                    try
                    {
                        while (ws.State == WebSocketState.Open && !cycleToken.IsCancellationRequested)
                        {
                            var msgRaw = await ReceiveTextMessageAsync(ws, cycleToken);
                            var msg = JsonNode.Parse(msgRaw)?.AsObject();
                            if (msg is null) continue;

                            if (msg["s"] is not null && msg["s"]!.GetValueKind() == JsonValueKind.Number)
                            {
                                seq = msg["s"]!.GetValue<int>();
                            }

                            var op = msg["op"]?.GetValue<int>() ?? -1;
                            if (op == 0)
                            {
                                var eventName = msg["t"]?.GetValue<string>();
                                if (string.Equals(eventName, "READY", StringComparison.Ordinal))
                                {
                                    var botUsername = msg["d"]?["user"]?["username"]?.GetValue<string>();
                                    discordService.SetGatewayStatus(true, botUsername, null);
                                }
                                else if (string.Equals(eventName, "INTERACTION_CREATE", StringComparison.Ordinal))
                                {
                                    var evData = msg["d"]?.AsObject();
                                    if (evData is not null)
                                    {
                                        var intId = evData["id"]?.GetValue<string>();
                                        var intToken = evData["token"]?.GetValue<string>();
                                        if (!string.IsNullOrWhiteSpace(intId) && !string.IsNullOrWhiteSpace(intToken))
                                        {
                                            // Dispatch concurrently so Discord Gateway heartbeats never block
                                            _ = Task.Run(async () =>
                                            {
                                                try
                                                {
                                                    var callbackPayload = await lifecycleEngine.HandleDiscordInteractionAsync(evData, stoppingToken);
                                                    await httpGateway.SendJsonAsync(
                                                        HttpMethod.Post,
                                                        $"https://discord.com/api/v10/interactions/{intId}/{intToken}/callback",
                                                        callbackPayload.ToJsonString(),
                                                        null,
                                                        stoppingToken);
                                                }
                                                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                                                {
                                                    // Host shutting down
                                                }
                                                catch (Exception dispatchEx)
                                                {
                                                    logger.LogWarning(dispatchEx, "Cleanerr Discord Gateway interaction dispatch failed (id={InteractionId}).", intId);
                                                }
                                            }, stoppingToken);
                                        }
                                    }
                                }
                            }
                            else if (op == 1)
                            {
                                var hb = new JsonObject { ["op"] = 1, ["d"] = seq.HasValue ? JsonValue.Create(seq.Value) : null };
                                await SendTextMessageAsync(ws, hb.ToJsonString(), cycleToken);
                            }
                            else if (op is 7 or 9)
                            {
                                break;
                            }
                        }
                    }
                    finally
                    {
                        await hbCts.CancelAsync();
                        try { await hbTask; } catch { /* Expected cancellation */ }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    // ConfigChanged triggered immediate reconnect
                    discordService.SetGatewayStatus(false, null, null);
                }
                catch (Exception ex)
                {
                    discordService.SetGatewayStatus(false, null, ex.Message);
                    logger.LogDebug(ex, "Cleanerr Discord Gateway loop reconnecting after transient fault.");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), cycleToken);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // ConfigChanged interrupted backoff delay for immediate reconnect
                    }
                }
                finally
                {
                    lock (_reconnectSync)
                    {
                        if (ReferenceEquals(_cycleCts, cycleCts))
                        {
                            _cycleCts = null;
                        }
                    }
                }
            }
        }
        finally
        {
            discordService.ConfigChanged -= OnConfigChanged;
            discordService.SetGatewayStatus(false, null, null);
        }
    }

    private static async Task SendTextMessageAsync(ClientWebSocket ws, string payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task<string> ReceiveTextMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var res = await ws.ReceiveAsync(buf, ct);
            if (res.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Discord Gateway closed connection.");
            }

            ms.Write(buf, 0, res.Count);
            if (res.EndOfMessage) break;
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
