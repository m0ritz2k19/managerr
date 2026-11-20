using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Managerr.Catalog;
using Managerr.Common.Crypto;
using Managerr.Common.Net;
using Managerr.Common.Notifications;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Metadata;

namespace Managerr.Host.Services;

public sealed record NotificationConnectionDto(
    long Id,
    string Name,
    string ProviderType,
    IReadOnlyDictionary<string, string> Settings,
    bool OnGrab,
    bool OnImport,
    bool OnUpgrade,
    bool OnRename,
    bool OnDelete,
    bool OnHealthIssue,
    bool OnApplicationUpdate,
    IReadOnlyList<string> Tags,
    bool IsEnabled,
    string CreatedAt,
    string UpdatedAt);

public sealed record UpsertNotificationConnectionRequest(
    string Name,
    string ProviderType,
    IReadOnlyDictionary<string, string>? Settings = null,
    bool OnGrab = true,
    bool OnImport = true,
    bool OnUpgrade = true,
    bool OnRename = false,
    bool OnDelete = true,
    bool OnHealthIssue = true,
    bool OnApplicationUpdate = false,
    IReadOnlyList<string>? Tags = null,
    bool IsEnabled = true);

public sealed record NotificationDispatchResultDto(
    bool Success,
    string ProviderType,
    int? StatusCode,
    string Message);

public interface INotificationConnectionService : IMediaLifecycleNotificationSink
{
    Task<IReadOnlyList<NotificationConnectionDto>> ListConnectionsAsync(CancellationToken ct = default);
    Task<NotificationConnectionDto> UpsertConnectionAsync(UpsertNotificationConnectionRequest request, long? id = null, CancellationToken ct = default);
    Task DeleteConnectionAsync(long id, CancellationToken ct = default);
    Task<NotificationDispatchResultDto> TestDraftAsync(UpsertNotificationConnectionRequest request, CancellationToken ct = default);
    Task<NotificationDispatchResultDto> TestConnectionAsync(long id, CancellationToken ct = default);
}

public sealed record ImportListSummaryDto(
    long Id,
    string Name,
    string ListType,
    string MediaVertical,
    long LibraryId,
    long? QualityProfileId,
    string MonitorMode,
    string MinimumAvailability,
    bool SearchOnAdd,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<string> Tags,
    int SyncIntervalHours,
    bool IsEnabled,
    string? LastSyncedAt,
    string? LastResultMessage,
    string CreatedAt,
    string UpdatedAt);

public sealed record UpsertImportListRequest(
    string Name,
    string ListType,
    string MediaVertical,
    long LibraryId,
    long? QualityProfileId = null,
    string MonitorMode = "all",
    string MinimumAvailability = "released",
    bool SearchOnAdd = false,
    IReadOnlyDictionary<string, string>? Settings = null,
    IReadOnlyList<string>? Tags = null,
    int SyncIntervalHours = 12,
    bool IsEnabled = true);

public sealed record ImportListExclusionDto(
    long Id,
    string ExternalKey,
    string Title,
    string MediaVertical,
    int? Year,
    string CreatedAt);

public sealed record CreateImportListExclusionRequest(
    string ExternalKey,
    string Title,
    string MediaVertical = "movies",
    int? Year = null);

public sealed record ImportListSyncReportDto(
    long ListId,
    string ListName,
    int FetchedCount,
    int AddedCount,
    int SkippedExistingCount,
    int SkippedExcludedCount,
    string Message);

public interface IImportListSyncService
{
    Task<IReadOnlyList<ImportListSummaryDto>> ListImportListsAsync(CancellationToken ct = default);
    Task<ImportListSummaryDto> UpsertImportListAsync(UpsertImportListRequest request, long? id = null, CancellationToken ct = default);
    Task DeleteImportListAsync(long id, CancellationToken ct = default);

    Task<ImportListSyncReportDto> SyncImportListAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<ImportListSyncReportDto>> SyncAllEnabledListsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ImportListExclusionDto>> ListExclusionsAsync(CancellationToken ct = default);
    Task<ImportListExclusionDto> AddExclusionAsync(CreateImportListExclusionRequest request, CancellationToken ct = default);
    Task DeleteExclusionAsync(long id, CancellationToken ct = default);
}

public sealed record CalendarFeedFilter(
    int PastDays = 7,
    int FutureDays = 60,
    string? Vertical = null,
    bool IncludeUnmonitored = false,
    IReadOnlyList<string>? Tags = null);

public interface ICalendarFeedService
{
    Task<string> RenderIcsFeedAsync(CalendarFeedFilter filter, CancellationToken ct = default);
}

/// <summary>
/// Pluggable Connect / Notification Provider subsystem (Discord, Telegram, Gotify, Ntfy, Apprise, Generic Webhook + HMAC, Plex, Jellyfin/Emby)
/// implementing IMediaLifecycleNotificationSink across Grab, Import, Upgrade, Rename, Delete, Health, and ApplicationUpdate events.
/// </summary>
public sealed class NotificationConnectionService(
    ISqliteDbFactory dbFactory,
    ISecretProtector secretProtector,
    ISafeHttpClientFactory httpClientFactory,
    ILogger<NotificationConnectionService> logger,
    Func<HttpClient>? httpClientOverride = null,
    IAppSettingsService? appSettings = null) : INotificationConnectionService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<NotificationConnectionDto>> ListConnectionsAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<NotificationConnectionRow>("SELECT * FROM notification_connections ORDER BY id ASC;");
        return rows.Select(MapRow).ToList();
    }

    public async Task<NotificationConnectionDto> UpsertConnectionAsync(
        UpsertNotificationConnectionRequest request,
        long? id = null,
        CancellationToken ct = default)
    {
        var name = string.IsNullOrWhiteSpace(request.Name) ? "Notification Connection" : request.Name.Trim();
        var provider = NormalizeProviderType(request.ProviderType);
        var cleanSettings = (request.Settings ?? new Dictionary<string, string>())
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value ?? "", StringComparer.OrdinalIgnoreCase);

        if (id.HasValue)
        {
            await using var existingConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
            var existingRow = await existingConn.QuerySingleOrDefaultAsync<NotificationConnectionRow>(
                "SELECT * FROM notification_connections WHERE id = @Id;",
                new { Id = id.Value });
            if (existingRow is not null)
            {
                var existingSettings = UnpackSettings(existingRow.SettingsJson);
                foreach (var (k, v) in existingSettings)
                {
                    if (!cleanSettings.TryGetValue(k, out var incoming) || incoming == "********")
                    {
                        cleanSettings[k] = v;
                    }
                }
            }
        }

        var encryptedSettings = secretProtector.Protect(JsonSerializer.Serialize(cleanSettings, JsonOpts));
        var tags = (request.Tags ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var now = DateTimeOffset.UtcNow.ToString("O");

        var savedId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                if (id.HasValue)
                {
                    var affected = await conn.ExecuteAsync(
                        """
                        UPDATE notification_connections
                        SET name                  = @Name,
                            provider_type         = @Provider,
                            settings_json         = @SettingsJson,
                            on_grab               = @OnGrab,
                            on_import             = @OnImport,
                            on_upgrade            = @OnUpgrade,
                            on_rename             = @OnRename,
                            on_delete             = @OnDelete,
                            on_health_issue       = @OnHealth,
                            on_application_update = @OnAppUpdate,
                            tags_json             = @TagsJson,
                            is_enabled            = @Enabled,
                            updated_at            = @Now
                        WHERE id = @Id;
                        """,
                        new
                        {
                            Id = id.Value,
                            Name = name,
                            Provider = provider,
                            SettingsJson = encryptedSettings,
                            OnGrab = request.OnGrab ? 1 : 0,
                            OnImport = request.OnImport ? 1 : 0,
                            OnUpgrade = request.OnUpgrade ? 1 : 0,
                            OnRename = request.OnRename ? 1 : 0,
                            OnDelete = request.OnDelete ? 1 : 0,
                            OnHealth = request.OnHealthIssue ? 1 : 0,
                            OnAppUpdate = request.OnApplicationUpdate ? 1 : 0,
                            TagsJson = JsonSerializer.Serialize(tags, JsonOpts),
                            Enabled = request.IsEnabled ? 1 : 0,
                            Now = now,
                        },
                        transaction: tx);
                    if (affected == 0)
                    {
                        throw new KeyNotFoundException($"Notification connection #{id.Value} not found.");
                    }

                    return id.Value;
                }

                return await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO notification_connections (
                        name, provider_type, settings_json,
                        on_grab, on_import, on_upgrade, on_rename, on_delete, on_health_issue, on_application_update,
                        tags_json, is_enabled, created_at, updated_at
                    ) VALUES (
                        @Name, @Provider, @SettingsJson,
                        @OnGrab, @OnImport, @OnUpgrade, @OnRename, @OnDelete, @OnHealth, @OnAppUpdate,
                        @TagsJson, @Enabled, @Now, @Now
                    );
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        Name = name,
                        Provider = provider,
                        SettingsJson = encryptedSettings,
                        OnGrab = request.OnGrab ? 1 : 0,
                        OnImport = request.OnImport ? 1 : 0,
                        OnUpgrade = request.OnUpgrade ? 1 : 0,
                        OnRename = request.OnRename ? 1 : 0,
                        OnDelete = request.OnDelete ? 1 : 0,
                        OnHealth = request.OnHealthIssue ? 1 : 0,
                        OnAppUpdate = request.OnApplicationUpdate ? 1 : 0,
                        TagsJson = JsonSerializer.Serialize(tags, JsonOpts),
                        Enabled = request.IsEnabled ? 1 : 0,
                        Now = now,
                    },
                    transaction: tx);
            },
            ct);

        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await readConn.QuerySingleAsync<NotificationConnectionRow>(
            "SELECT * FROM notification_connections WHERE id = @Id;",
            new { Id = savedId });
        return MapRow(row);
    }

    public async Task DeleteConnectionAsync(long id, CancellationToken ct = default)
    {
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                var affected = await conn.ExecuteAsync(
                    "DELETE FROM notification_connections WHERE id = @Id;",
                    new { Id = id },
                    transaction: tx);
                if (affected == 0)
                {
                    throw new KeyNotFoundException($"Notification connection #{id} not found.");
                }
            },
            ct);
    }

    public Task<NotificationDispatchResultDto> TestDraftAsync(
        UpsertNotificationConnectionRequest request,
        CancellationToken ct = default)
    {
        var provider = NormalizeProviderType(request.ProviderType);
        var settings = (request.Settings ?? new Dictionary<string, string>())
            .ToDictionary(kv => kv.Key, kv => kv.Value ?? "", StringComparer.OrdinalIgnoreCase);

        var testEvent = new MediaLifecycleNotificationEvent(
            EventType: MediaNotificationEventTypes.Test,
            Title: "Managerr — Test Notification",
            Message: $"Test connection '{request.Name}' ({provider}) succeeded from Managerr.",
            WorkTitle: "Sample Media Work",
            MediaVertical: "tv",
            EpisodeCode: "S01E01",
            QualityName: "WEBDL-1080p",
            Languages: ["de", "en"]);

        return DispatchSingleAsync(provider, settings, testEvent, ct);
    }

    public async Task<NotificationDispatchResultDto> TestConnectionAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await conn.QuerySingleOrDefaultAsync<NotificationConnectionRow>(
            "SELECT * FROM notification_connections WHERE id = @Id;",
            new { Id = id })
            ?? throw new KeyNotFoundException($"Notification connection #{id} not found.");

        var dto = MapRow(row);
        var settings = UnpackSettings(row.SettingsJson);
        var testEvent = new MediaLifecycleNotificationEvent(
            EventType: MediaNotificationEventTypes.Test,
            Title: $"Managerr — Test ({dto.Name})",
            Message: $"Verified Connect notification provider '{dto.Name}' ({dto.ProviderType}).",
            WorkTitle: "Sample Media Work",
            MediaVertical: "tv",
            EpisodeCode: "S01E01",
            QualityName: "WEBDL-1080p",
            Languages: ["de", "en"]);

        return await DispatchSingleAsync(dto.ProviderType, settings, testEvent, ct);
    }

    public async Task PublishAsync(MediaLifecycleNotificationEvent notification, CancellationToken ct = default)
    {
        try
        {
            await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
            var rows = (await conn.QueryAsync<NotificationConnectionRow>(
                "SELECT * FROM notification_connections WHERE is_enabled = 1 ORDER BY id ASC;")).ToList();
            if (rows.Count == 0)
            {
                return;
            }

            var workTags = (notification.WorkTags ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                if (!MatchesTrigger(row, notification))
                {
                    continue;
                }

                var connTags = JsonSerializer.Deserialize<string[]>(row.TagsJson, JsonOpts) ?? [];
                if (connTags.Length > 0 &&
                    notification.EventType is not (MediaNotificationEventTypes.Test or MediaNotificationEventTypes.OnHealthIssue or MediaNotificationEventTypes.OnApplicationUpdate) &&
                    !connTags.Any(workTags.Contains))
                {
                    continue;
                }

                var settings = UnpackSettings(row.SettingsJson);
                var result = await DispatchSingleAsync(row.ProviderType, settings, notification, ct);
                if (!result.Success)
                {
                    logger.LogWarning(
                        "Notification connection '{Name}' ({Provider}) returned warning for {Event}: {Message}",
                        row.Name,
                        row.ProviderType,
                        notification.EventType,
                        result.Message);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Non-fatal notification dispatch failure for {EventType}.", notification.EventType);
        }
    }

    private static bool MatchesTrigger(NotificationConnectionRow row, MediaLifecycleNotificationEvent ev) =>
        ev.EventType switch
        {
            MediaNotificationEventTypes.Test => true,
            MediaNotificationEventTypes.OnGrab => row.OnGrab != 0,
            MediaNotificationEventTypes.OnImport => ev.IsUpgrade ? (row.OnUpgrade != 0 || row.OnImport != 0) : row.OnImport != 0,
            MediaNotificationEventTypes.OnUpgrade => row.OnUpgrade != 0,
            MediaNotificationEventTypes.OnRename => row.OnRename != 0,
            MediaNotificationEventTypes.OnDelete => row.OnDelete != 0,
            MediaNotificationEventTypes.OnHealthIssue => row.OnHealthIssue != 0,
            MediaNotificationEventTypes.OnApplicationUpdate => row.OnApplicationUpdate != 0,
            _ => false,
        };

    private async Task<NotificationDispatchResultDto> DispatchSingleAsync(
        string providerType,
        Dictionary<string, string> settings,
        MediaLifecycleNotificationEvent ev,
        CancellationToken ct)
    {
        var provider = NormalizeProviderType(providerType);
        using var client = httpClientOverride is not null
            ? httpClientOverride()
            : httpClientFactory.CreateHomelabClient(allowPrivateLan: true, ignoreSslErrors: true);

        try
        {
            HttpRequestMessage req;
            switch (provider)
            {
                case "discord":
                {
                    var webhookUrl = GetRequiredSetting(settings, "webhookUrl", "url");
                    var color = ev.EventType switch
                    {
                        MediaNotificationEventTypes.OnGrab => 0x35C5F4,
                        MediaNotificationEventTypes.OnImport or MediaNotificationEventTypes.OnUpgrade => 0x27AE60,
                        MediaNotificationEventTypes.OnHealthIssue or MediaNotificationEventTypes.OnDelete => 0xE74C3C,
                        _ => 0x5D9CEC,
                    };
                    var fields = new List<object>();
                    if (!string.IsNullOrWhiteSpace(ev.WorkTitle))
                    {
                        fields.Add(new { name = "Work", value = ev.WorkTitle, inline = true });
                    }
                    if (!string.IsNullOrWhiteSpace(ev.EpisodeCode))
                    {
                        fields.Add(new { name = "Unit", value = ev.EpisodeCode, inline = true });
                    }
                    if (!string.IsNullOrWhiteSpace(ev.QualityName))
                    {
                        fields.Add(new { name = "Quality", value = ev.QualityName, inline = true });
                    }
                    if (ev.Languages is { Count: > 0 })
                    {
                        fields.Add(new { name = "Languages", value = string.Join(", ", ev.Languages), inline = true });
                    }

                    var body = new
                    {
                        username = settings.TryGetValue("username", out var u) && !string.IsNullOrWhiteSpace(u) ? u : "Managerr",
                        embeds = new[]
                        {
                            new
                            {
                                title = ev.Title,
                                description = ev.Message,
                                color,
                                fields,
                                timestamp = DateTimeOffset.UtcNow.ToString("O"),
                            },
                        },
                    };
                    req = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json"),
                    };
                    break;
                }

                case "telegram":
                {
                    var botToken = GetRequiredSetting(settings, "botToken", "token");
                    var chatId = GetRequiredSetting(settings, "chatId", "chat_id");
                    var baseUrl = settings.TryGetValue("apiBaseUrl", out var b) && !string.IsNullOrWhiteSpace(b)
                        ? b.TrimEnd('/')
                        : "https://api.telegram.org";
                    var payload = new
                    {
                        chat_id = chatId,
                        text = $"*{ev.Title}*\n{ev.Message}",
                        parse_mode = "Markdown",
                    };
                    req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/bot{botToken}/sendMessage")
                    {
                        Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json"),
                    };
                    break;
                }

                case "gotify":
                {
                    var serverUrl = GetRequiredSetting(settings, "serverUrl", "url").TrimEnd('/');
                    var appToken = GetRequiredSetting(settings, "appToken", "token");
                    var priority = settings.TryGetValue("priority", out var pr) && int.TryParse(pr, out var pInt) ? pInt : 5;
                    var payload = new { title = ev.Title, message = ev.Message, priority };
                    req = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/message?token={Uri.EscapeDataString(appToken)}")
                    {
                        Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json"),
                    };
                    break;
                }

                case "ntfy":
                {
                    var serverUrl = (settings.TryGetValue("serverUrl", out var sUrl) && !string.IsNullOrWhiteSpace(sUrl)
                        ? sUrl
                        : "https://ntfy.sh").TrimEnd('/');
                    var topic = GetRequiredSetting(settings, "topic");
                    req = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/{topic.TrimStart('/')}")
                    {
                        Content = new StringContent(ev.Message, Encoding.UTF8, "text/plain"),
                    };
                    req.Headers.TryAddWithoutValidation("Title", ev.Title);
                    if (settings.TryGetValue("accessToken", out var tok) && !string.IsNullOrWhiteSpace(tok))
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tok.Trim());
                    }
                    break;
                }

                case "apprise":
                {
                    var apiUrl = GetRequiredSetting(settings, "apiUrl", "url");
                    var urls = settings.TryGetValue("urls", out var uList) ? uList : "";
                    var payload = new
                    {
                        urls,
                        title = ev.Title,
                        body = ev.Message,
                        type = ev.EventType == MediaNotificationEventTypes.OnHealthIssue ? "warning" : "info",
                    };
                    req = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json"),
                    };
                    break;
                }

                case "plex":
                {
                    var serverUrl = GetRequiredSetting(settings, "serverUrl", "url").TrimEnd('/');
                    var token = GetRequiredSetting(settings, "token", "plexToken");
                    var section = settings.TryGetValue("sectionId", out var sec) && !string.IsNullOrWhiteSpace(sec)
                        ? sec.Trim()
                        : "all";

                    string? targetDir = null;
                    var candidatePath = !string.IsNullOrWhiteSpace(ev.DestinationPath)
                        ? ev.DestinationPath
                        : (ev.ExtraMetadata != null && (ev.ExtraMetadata.TryGetValue("path", out var ep) ||
                                                        ev.ExtraMetadata.TryGetValue("targetPath", out ep) ||
                                                        ev.ExtraMetadata.TryGetValue("directory", out ep) ||
                                                        ev.ExtraMetadata.TryGetValue("folder", out ep)) && !string.IsNullOrWhiteSpace(ep))
                            ? ep
                            : null;

                    if (!string.IsNullOrWhiteSpace(candidatePath))
                    {
                        try
                        {
                            if (Directory.Exists(candidatePath))
                            {
                                targetDir = Path.GetFullPath(candidatePath);
                            }
                            else if (File.Exists(candidatePath))
                            {
                                targetDir = Path.GetDirectoryName(Path.GetFullPath(candidatePath));
                            }
                            else
                            {
                                targetDir = Path.HasExtension(candidatePath)
                                    ? Path.GetDirectoryName(candidatePath)
                                    : candidatePath;
                            }
                        }
                        catch
                        {
                            targetDir = Path.HasExtension(candidatePath)
                                ? Path.GetDirectoryName(candidatePath)
                                : candidatePath;
                        }
                    }

                    var refreshUrl = $"{serverUrl}/library/sections/{section}/refresh";
                    if (!string.IsNullOrWhiteSpace(targetDir))
                    {
                        var separator = refreshUrl.Contains('?') ? '&' : '?';
                        refreshUrl += $"{separator}path={Uri.EscapeDataString(targetDir)}";
                    }

                    req = new HttpRequestMessage(HttpMethod.Get, refreshUrl);
                    req.Headers.TryAddWithoutValidation("X-Plex-Token", token);
                    break;
                }

                case "jellyfin":
                {
                    var serverUrl = GetRequiredSetting(settings, "serverUrl", "url").TrimEnd('/');
                    var apiKey = GetRequiredSetting(settings, "apiKey", "token");
                    req = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/Library/Refresh")
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                    };
                    req.Headers.TryAddWithoutValidation("X-Emby-Token", apiKey);
                    break;
                }

                default: // "webhook"
                {
                    var url = GetRequiredSetting(settings, "url", "webhookUrl");
                    var method = settings.TryGetValue("method", out var m) && string.Equals(m, "PUT", StringComparison.OrdinalIgnoreCase)
                        ? HttpMethod.Put
                        : HttpMethod.Post;
                    var configuredInstanceName = appSettings is not null
                        ? await appSettings.GetAsync("system.general.instance_name", ct)
                        : null;
                    var instanceName = string.IsNullOrWhiteSpace(configuredInstanceName)
                        ? "Managerr"
                        : configuredInstanceName.Trim();
                    var payloadObj = new
                    {
                        eventType = ev.EventType,
                        instanceName,
                        timestamp = DateTimeOffset.UtcNow.ToString("O"),
                        title = ev.Title,
                        message = ev.Message,
                        workId = ev.WorkId,
                        workTitle = ev.WorkTitle,
                        mediaVertical = ev.MediaVertical,
                        episodeCode = ev.EpisodeCode,
                        releaseTitle = ev.ReleaseTitle,
                        quality = ev.QualityName,
                        languages = ev.Languages ?? [],
                        isUpgrade = ev.IsUpgrade,
                        destinationPath = ev.DestinationPath,
                        extra = ev.ExtraMetadata ?? new Dictionary<string, string>(),
                    };
                    var json = JsonSerializer.Serialize(payloadObj, JsonOpts);
                    req = new HttpRequestMessage(method, url)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                    if (settings.TryGetValue("hmacSecret", out var secret) && !string.IsNullOrWhiteSpace(secret))
                    {
                        var sigBytes = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(json));
                        req.Headers.TryAddWithoutValidation("X-Managerr-Signature", $"sha256={Convert.ToHexStringLower(sigBytes)}");
                    }
                    break;
                }
            }

            using var resp = await client.SendAsync(req, ct);
            var code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
            {
                return new NotificationDispatchResultDto(true, provider, code, $"Delivered ({code}).");
            }

            var errBody = await resp.Content.ReadAsStringAsync(ct);
            return new NotificationDispatchResultDto(
                false,
                provider,
                code,
                $"Remote endpoint returned HTTP {code}: {(errBody.Length > 180 ? errBody[..180] : errBody)}");
        }
        catch (Exception ex)
        {
            return new NotificationDispatchResultDto(false, provider, null, ex.Message);
        }
    }

    private static string GetRequiredSetting(Dictionary<string, string> settings, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (settings.TryGetValue(k, out var val) && !string.IsNullOrWhiteSpace(val))
            {
                return val.Trim();
            }
        }

        throw new ArgumentException($"Missing required connection setting '{keys[0]}'.");
    }

    private static string NormalizeProviderType(string? raw) =>
        (raw ?? "webhook").Trim().ToLowerInvariant() switch
        {
            "discord" => "discord",
            "telegram" => "telegram",
            "gotify" => "gotify",
            "ntfy" => "ntfy",
            "apprise" => "apprise",
            "plex" => "plex",
            "jellyfin" or "emby" => "jellyfin",
            _ => "webhook",
        };

    private Dictionary<string, string> UnpackSettings(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var json = secretProtector.IsProtectedEnvelope(rawJson)
            ? secretProtector.Unprotect(rawJson)
            : rawJson;

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOpts) ?? new Dictionary<string, string>();
        return new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
    }

    private NotificationConnectionDto MapRow(NotificationConnectionRow row)
    {
        var unpacked = UnpackSettings(row.SettingsJson);
        var masked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in unpacked)
        {
            // A Discord/Slack webhook URL carries its own token: whoever has it can post to the channel
            var isSensitive = k.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                              k.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                              k.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
                              k.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                              k.Contains("webhook", StringComparison.OrdinalIgnoreCase);
            masked[k] = isSensitive && !string.IsNullOrEmpty(v) ? "********" : v;
        }

        return new NotificationConnectionDto(
            Id: row.Id,
            Name: row.Name,
            ProviderType: row.ProviderType,
            Settings: masked,
            OnGrab: row.OnGrab != 0,
            OnImport: row.OnImport != 0,
            OnUpgrade: row.OnUpgrade != 0,
            OnRename: row.OnRename != 0,
            OnDelete: row.OnDelete != 0,
            OnHealthIssue: row.OnHealthIssue != 0,
            OnApplicationUpdate: row.OnApplicationUpdate != 0,
            Tags: JsonSerializer.Deserialize<string[]>(row.TagsJson, JsonOpts) ?? [],
            IsEnabled: row.IsEnabled != 0,
            CreatedAt: row.CreatedAt,
            UpdatedAt: row.UpdatedAt);
    }

    private sealed class NotificationConnectionRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string ProviderType { get; init; } = "webhook";
        public string SettingsJson { get; init; } = "{}";
        public int OnGrab { get; init; }
        public int OnImport { get; init; }
        public int OnUpgrade { get; init; }
        public int OnRename { get; init; }
        public int OnDelete { get; init; }
        public int OnHealthIssue { get; init; }
        public int OnApplicationUpdate { get; init; }
        public string TagsJson { get; init; } = "[]";
        public int IsEnabled { get; init; }
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }
}

/// <summary>
/// Renders RFC 5545 VCALENDAR / VEVENT iCal (.ics) streams for external calendar apps (Google Calendar, Apple Calendar, Nextcloud, Outlook).
/// </summary>
public sealed class CalendarFeedService(IPolymorphicCatalogService catalogService) : ICalendarFeedService
{
    public async Task<string> RenderIcsFeedAsync(CalendarFeedFilter filter, CancellationToken ct = default)
    {
        var pastDays = Math.Clamp(filter.PastDays, 0, 365);
        var futureDays = Math.Clamp(filter.FutureDays, 1, 365);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = today.AddDays(-pastDays);
        var end = today.AddDays(futureDays);

        var entries = await catalogService.GetCalendarAsync(start, end, actor: null, ct);

        HashSet<long>? tagFilteredWorkIds = null;
        if (filter.Tags is { Count: > 0 })
        {
            var reqTags = filter.Tags
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (reqTags.Count > 0)
            {
                var allWorks = await catalogService.ListWorksAsync(null, null, null, ct);
                tagFilteredWorkIds = allWorks
                    .Where(w => w.Tags.Any(reqTags.Contains))
                    .Select(w => w.Id)
                    .ToHashSet();
            }
        }

        var filtered = entries
            .Where(e => filter.IncludeUnmonitored || e.Monitored)
            .Where(e => string.IsNullOrWhiteSpace(filter.Vertical) ||
                        string.Equals(e.WorkKind, filter.Vertical.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(e => tagFilteredWorkIds is null || tagFilteredWorkIds.Contains(e.WorkId))
            .OrderBy(e => e.AirOrReleaseDate, StringComparer.Ordinal)
            .ThenBy(e => e.WorkTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ContainerNumber)
            .ThenBy(e => e.UnitNumber)
            .ToList();

        var sb = new StringBuilder();
        var dtStamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");

        AppendCrlf(sb, "BEGIN:VCALENDAR");
        AppendCrlf(sb, "VERSION:2.0");
        AppendCrlf(sb, "PRODID:-//Managerr//Unified Media Orchestrator//EN");
        AppendCrlf(sb, "CALSCALE:GREGORIAN");
        AppendCrlf(sb, "METHOD:PUBLISH");
        AppendCrlf(sb, "X-WR-CALNAME:Managerr Media Calendar");
        AppendCrlf(sb, "X-PUBLISHED-TTL:PT1H");

        foreach (var item in filtered)
        {
            if (string.IsNullOrWhiteSpace(item.AirOrReleaseDate) ||
                item.AirOrReleaseDate.Length < 10 ||
                !DateOnly.TryParse(item.AirOrReleaseDate[..10], out var eventDate))
            {
                continue;
            }

            var startStr = eventDate.ToString("yyyyMMdd");
            var endStr = eventDate.AddDays(1).ToString("yyyyMMdd");

            var isEpisodic = item.WorkKind.Equals("tv", StringComparison.OrdinalIgnoreCase) ||
                             item.WorkKind.Equals("anime", StringComparison.OrdinalIgnoreCase);
            var summary = isEpisodic
                ? $"{item.WorkTitle} - S{item.ContainerNumber:00}E{item.UnitNumber:00} - {item.UnitTitle}"
                : string.Equals(item.UnitTitle, item.WorkTitle, StringComparison.OrdinalIgnoreCase)
                    ? item.WorkTitle
                    : $"{item.WorkTitle} — {item.UnitTitle}";

            var statusLabel = item.HasFile ? "Downloaded" : item.Monitored ? "Missing (Monitored)" : "Unmonitored";
            var description = $"Work: {item.WorkTitle}\\nVertical: {item.WorkKind}\\nContainer: {item.ContainerTitle}\\nStatus: {statusLabel}";

            AppendCrlf(sb, "BEGIN:VEVENT");
            AppendCrlf(sb, $"UID:managerr-unit-{item.UnitId}@managerr.local");
            AppendCrlf(sb, $"DTSTAMP:{dtStamp}");
            AppendCrlf(sb, $"DTSTART;VALUE=DATE:{startStr}");
            AppendCrlf(sb, $"DTEND;VALUE=DATE:{endStr}");
            AppendCrlf(sb, $"SUMMARY:{EscapeIcsText(summary)}");
            AppendCrlf(sb, $"DESCRIPTION:{description}");
            AppendCrlf(sb, $"CATEGORIES:{EscapeIcsText(item.WorkKind.ToUpperInvariant())}");
            AppendCrlf(sb, "STATUS:CONFIRMED");
            AppendCrlf(sb, "TRANSP:TRANSPARENT");
            AppendCrlf(sb, "END:VEVENT");
        }

        AppendCrlf(sb, "END:VCALENDAR");
        return sb.ToString();
    }

    private static void AppendCrlf(StringBuilder sb, string line)
    {
        sb.Append(line);
        sb.Append("\r\n");
    }

    private static string EscapeIcsText(string input) =>
        (input ?? "")
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}

/// <summary>
/// Automated Import Lists engine (TMDB Popular/Trending, StevenLu, AniList Trending, Trakt Popular, OpenLibrary Subject, Custom JSON URL)
/// with persistent Import List Exclusions and optional auto-search on add.
/// </summary>
public sealed partial class ImportListSyncService(
    ISqliteDbFactory dbFactory,
    ISecretProtector secretProtector,
    IAppSettingsService appSettings,
    ISafeHttpClientFactory httpClientFactory,
    IPolymorphicCatalogService catalogService,
    IMetadataFederationService metadataFederation,
    IWantedAndActivityService wantedService,
    ILogger<ImportListSyncService> logger,
    Func<HttpClient>? httpClientOverride = null) : IImportListSyncService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex AlphaNumSanitizer();

    public async Task<IReadOnlyList<ImportListSummaryDto>> ListImportListsAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<ImportListRow>("SELECT * FROM import_lists ORDER BY id ASC;");
        return rows.Select(MapListRow).ToList();
    }

    public async Task<ImportListSummaryDto> UpsertImportListAsync(
        UpsertImportListRequest request,
        long? id = null,
        CancellationToken ct = default)
    {
        var name = string.IsNullOrWhiteSpace(request.Name) ? "Import List" : request.Name.Trim();
        var listType = (request.ListType ?? "custom_json").Trim().ToLowerInvariant();
        var vertical = string.IsNullOrWhiteSpace(request.MediaVertical) ? "movies" : request.MediaVertical.Trim().ToLowerInvariant();
        var library = await catalogService.GetLibraryByIdAsync(request.LibraryId, ct)
            ?? throw new ArgumentException($"Library #{request.LibraryId} does not exist.");

        var cleanSettings = (request.Settings ?? new Dictionary<string, string>())
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value ?? "", StringComparer.OrdinalIgnoreCase);

        if (id.HasValue)
        {
            await using var existingConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
            var existingRow = await existingConn.QuerySingleOrDefaultAsync<ImportListRow>(
                "SELECT * FROM import_lists WHERE id = @Id;",
                new { Id = id.Value });
            if (existingRow is not null)
            {
                var existingSettings = UnpackSettings(existingRow.SettingsJson);
                foreach (var (k, v) in existingSettings)
                {
                    if (!cleanSettings.TryGetValue(k, out var incoming) || incoming == "********")
                    {
                        cleanSettings[k] = v;
                    }
                }
            }
        }

        var encryptedSettings = secretProtector.Protect(JsonSerializer.Serialize(cleanSettings, JsonOpts));
        var tags = (request.Tags ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var intervalHours = Math.Clamp(request.SyncIntervalHours <= 0 ? 12 : request.SyncIntervalHours, 1, 168);
        var minAvail = MinimumAvailabilityPolicy.Normalize(request.MinimumAvailability);
        var monitorMode = string.IsNullOrWhiteSpace(request.MonitorMode) ? "all" : request.MonitorMode.Trim();
        var now = DateTimeOffset.UtcNow.ToString("O");

        var savedId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                if (id.HasValue)
                {
                    var affected = await conn.ExecuteAsync(
                        """
                        UPDATE import_lists
                        SET name                 = @Name,
                            list_type            = @ListType,
                            media_vertical       = @Vertical,
                            library_id           = @LibraryId,
                            quality_profile_id   = @ProfileId,
                            monitor_mode         = @MonitorMode,
                            minimum_availability = @MinAvail,
                            search_on_add        = @SearchOnAdd,
                            settings_json        = @SettingsJson,
                            tags_json            = @TagsJson,
                            sync_interval_hours  = @IntervalHours,
                            is_enabled           = @Enabled,
                            updated_at           = @Now
                        WHERE id = @Id;
                        """,
                        new
                        {
                            Id = id.Value,
                            Name = name,
                            ListType = listType,
                            Vertical = string.IsNullOrWhiteSpace(vertical) ? library.MediaType : vertical,
                            LibraryId = library.Id,
                            ProfileId = request.QualityProfileId is > 0 ? request.QualityProfileId : null,
                            MonitorMode = monitorMode,
                            MinAvail = minAvail,
                            SearchOnAdd = request.SearchOnAdd ? 1 : 0,
                            SettingsJson = encryptedSettings,
                            TagsJson = JsonSerializer.Serialize(tags, JsonOpts),
                            IntervalHours = intervalHours,
                            Enabled = request.IsEnabled ? 1 : 0,
                            Now = now,
                        },
                        transaction: tx);
                    if (affected == 0)
                    {
                        throw new KeyNotFoundException($"Import List #{id.Value} not found.");
                    }

                    return id.Value;
                }

                return await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO import_lists (
                        name, list_type, media_vertical, library_id, quality_profile_id,
                        monitor_mode, minimum_availability, search_on_add,
                        settings_json, tags_json, sync_interval_hours, is_enabled,
                        created_at, updated_at
                    ) VALUES (
                        @Name, @ListType, @Vertical, @LibraryId, @ProfileId,
                        @MonitorMode, @MinAvail, @SearchOnAdd,
                        @SettingsJson, @TagsJson, @IntervalHours, @Enabled,
                        @Now, @Now
                    );
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        Name = name,
                        ListType = listType,
                        Vertical = string.IsNullOrWhiteSpace(vertical) ? library.MediaType : vertical,
                        LibraryId = library.Id,
                        ProfileId = request.QualityProfileId is > 0 ? request.QualityProfileId : null,
                        MonitorMode = monitorMode,
                        MinAvail = minAvail,
                        SearchOnAdd = request.SearchOnAdd ? 1 : 0,
                        SettingsJson = encryptedSettings,
                        TagsJson = JsonSerializer.Serialize(tags, JsonOpts),
                        IntervalHours = intervalHours,
                        Enabled = request.IsEnabled ? 1 : 0,
                        Now = now,
                    },
                    transaction: tx);
            },
            ct);

        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await readConn.QuerySingleAsync<ImportListRow>("SELECT * FROM import_lists WHERE id = @Id;", new { Id = savedId });
        return MapListRow(row);
    }

    public async Task DeleteImportListAsync(long id, CancellationToken ct = default)
    {
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                var affected = await conn.ExecuteAsync("DELETE FROM import_lists WHERE id = @Id;", new { Id = id }, transaction: tx);
                if (affected == 0)
                {
                    throw new KeyNotFoundException($"Import List #{id} not found.");
                }
            },
            ct);
    }

    public async Task<IReadOnlyList<ImportListExclusionDto>> ListExclusionsAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<ExclusionRow>("SELECT * FROM import_list_exclusions ORDER BY id DESC;");
        return rows.Select(r => new ImportListExclusionDto(r.Id, r.ExternalKey, r.Title, r.MediaVertical, r.Year, r.CreatedAt)).ToList();
    }

    public async Task<ImportListExclusionDto> AddExclusionAsync(CreateImportListExclusionRequest request, CancellationToken ct = default)
    {
        var title = string.IsNullOrWhiteSpace(request.Title) ? "Excluded Work" : request.Title.Trim();
        var extKey = string.IsNullOrWhiteSpace(request.ExternalKey)
            ? $"title:{NormalizeTitle(title)}:{request.Year ?? 0}"
            : request.ExternalKey.Trim().ToLowerInvariant();
        var vertical = string.IsNullOrWhiteSpace(request.MediaVertical) ? "movies" : request.MediaVertical.Trim().ToLowerInvariant();
        var now = DateTimeOffset.UtcNow.ToString("O");

        var savedId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO import_list_exclusions (external_key, title, media_vertical, year, created_at)
                    VALUES (@Key, @Title, @Vertical, @Year, @Now)
                    ON CONFLICT(external_key) DO UPDATE SET
                        title = excluded.title,
                        media_vertical = excluded.media_vertical,
                        year = excluded.year;
                    """,
                    new { Key = extKey, Title = title, Vertical = vertical, request.Year, Now = now },
                    transaction: tx);

                return await conn.ExecuteScalarAsync<long>(
                    "SELECT id FROM import_list_exclusions WHERE external_key = @Key;",
                    new { Key = extKey },
                    transaction: tx);
            },
            ct);

        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await readConn.QuerySingleAsync<ExclusionRow>("SELECT * FROM import_list_exclusions WHERE id = @Id;", new { Id = savedId });
        return new ImportListExclusionDto(row.Id, row.ExternalKey, row.Title, row.MediaVertical, row.Year, row.CreatedAt);
    }

    public async Task DeleteExclusionAsync(long id, CancellationToken ct = default)
    {
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                var affected = await conn.ExecuteAsync("DELETE FROM import_list_exclusions WHERE id = @Id;", new { Id = id }, transaction: tx);
                if (affected == 0)
                {
                    throw new KeyNotFoundException($"Import list exclusion #{id} not found.");
                }
            },
            ct);
    }

    public async Task<IReadOnlyList<ImportListSyncReportDto>> SyncAllEnabledListsAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var enabledRows = (await conn.QueryAsync<(long Id, string Name, string? LastSyncedAt, int SyncIntervalHours)>(
            """
            SELECT id AS Id,
                   name AS Name,
                   last_synced_at AS LastSyncedAt,
                   sync_interval_hours AS SyncIntervalHours
            FROM import_lists
            WHERE is_enabled = 1
            ORDER BY id ASC;
            """)).ToList();

        var now = DateTimeOffset.UtcNow;
        var reports = new List<ImportListSyncReportDto>(enabledRows.Count);
        foreach (var item in enabledRows)
        {
            if (!string.IsNullOrWhiteSpace(item.LastSyncedAt) &&
                DateTimeOffset.TryParse(item.LastSyncedAt, out var lastSynced))
            {
                var interval = TimeSpan.FromHours(Math.Clamp(item.SyncIntervalHours <= 0 ? 12 : item.SyncIntervalHours, 1, 168));
                if (lastSynced + interval > now)
                {
                    continue;
                }
            }

            try
            {
                reports.Add(await SyncImportListAsync(item.Id, ct));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Import list #{Id} sync failed.", item.Id);
                reports.Add(new ImportListSyncReportDto(item.Id, item.Name, 0, 0, 0, 0, $"Sync error: {ex.Message}"));
            }
        }

        return reports;
    }

    public async Task<ImportListSyncReportDto> SyncImportListAsync(long id, CancellationToken ct = default)
    {
        ImportListRow row;
        await using (var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            row = await readConn.QuerySingleOrDefaultAsync<ImportListRow>(
                "SELECT * FROM import_lists WHERE id = @Id;",
                new { Id = id })
                ?? throw new KeyNotFoundException($"Import List #{id} not found.");
        }

        var settings = UnpackSettings(row.SettingsJson);
        var listTags = JsonSerializer.Deserialize<string[]>(row.TagsJson, JsonOpts) ?? [];
        var discovered = await FetchCandidatesAsync(row.ListType, row.MediaVertical, settings, ct);

        var exclusions = await ListExclusionsAsync(ct);
        var excludedKeys = exclusions
            .Select(e => e.ExternalKey.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedTitles = exclusions
            .Select(e => NormalizeTitle(e.Title))
            .Where(t => t.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingWorks = await catalogService.ListWorksAsync(null, null, null, ct);
        var existingExtPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingNormTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in existingWorks)
        {
            existingNormTitles.Add(NormalizeTitle(w.Title));
            foreach (var (k, v) in w.ExternalIds)
            {
                if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v))
                {
                    existingExtPairs.Add($"{k.Trim().ToLowerInvariant()}:{v.Trim().ToLowerInvariant()}");
                }
            }
        }

        var addedCount = 0;
        var skippedExisting = 0;
        var skippedExcluded = 0;
        var newlyAddedUnitIds = new List<long>();

        foreach (var cand in discovered)
        {
            if (string.IsNullOrWhiteSpace(cand.Title))
            {
                continue;
            }

            var normTitle = NormalizeTitle(cand.Title);
            var candidateKeys = BuildCandidateKeys(cand, normTitle);

            if (candidateKeys.Any(excludedKeys.Contains) || excludedTitles.Contains(normTitle))
            {
                skippedExcluded++;
                continue;
            }

            if (candidateKeys.Any(existingExtPairs.Contains) || existingNormTitles.Contains(normTitle))
            {
                skippedExisting++;
                continue;
            }

            IReadOnlyList<MetadataContainerSeed>? hydratedContainers = null;
            IReadOnlyList<string>? hydratedAltTitles = null;
            var overview = cand.Overview ?? "";
            var posterUrl = cand.PosterUrl;
            var year = cand.Year;
            var origLang = cand.OriginalLanguage ?? "en";

            if (row.MediaVertical is "tv" or "anime")
            {
                try
                {
                    var hydrated = cand.ExternalIds.Count > 0
                        ? await metadataFederation.ResolveWorkAsync(row.MediaVertical, null, cand.ExternalIds, null, ct)
                        : null;
                    if (hydrated is null)
                    {
                        var searchHits = await metadataFederation.SearchWaterfallAsync(cand.Title, row.MediaVertical, null, ct);
                        hydrated = searchHits.FirstOrDefault();
                    }

                    if (hydrated is not null)
                    {
                        hydratedContainers = hydrated.Containers;
                        hydratedAltTitles = hydrated.AltTitles;
                        if (string.IsNullOrWhiteSpace(overview)) overview = hydrated.Overview;
                        posterUrl ??= hydrated.PosterUrl;
                        year ??= hydrated.Year;
                        if (!string.IsNullOrWhiteSpace(hydrated.OriginalLanguage)) origLang = hydrated.OriginalLanguage;
                    }
                }
                catch
                {
                    // Fall back to direct work creation when upstream metadata provider is offline
                }
            }

            var createdTree = await catalogService.AddWorkAsync(
                new CreateMediaWorkRequest(
                    LibraryId: row.LibraryId,
                    Title: cand.Title.Trim(),
                    WorkKind: row.MediaVertical,
                    AltTitles: hydratedAltTitles,
                    OriginalLanguage: origLang,
                    Year: year,
                    Status: "released",
                    Overview: overview,
                    PosterUrl: posterUrl,
                    Monitored: !string.Equals(row.MonitorMode, "none", StringComparison.OrdinalIgnoreCase),
                    ExternalIds: cand.ExternalIds,
                    Tags: listTags,
                    Containers: hydratedContainers,
                    MonitorMode: row.MonitorMode,
                    MinimumAvailability: row.MinimumAvailability,
                    QualityProfileId: row.QualityProfileId,
                    SkipUpstreamHydration: row.MediaVertical is not ("tv" or "anime") && !string.IsNullOrWhiteSpace(posterUrl)),
                actor: null,
                ct);

            addedCount++;
            existingNormTitles.Add(normTitle);
            foreach (var ck in candidateKeys)
            {
                existingExtPairs.Add(ck);
            }

            if (row.SearchOnAdd != 0)
            {
                newlyAddedUnitIds.AddRange(
                    createdTree.Containers
                        .SelectMany(c => c.Units)
                        .Where(u => u.Monitored)
                        .Select(u => u.Id));
            }
        }

        if (row.SearchOnAdd != 0 && newlyAddedUnitIds.Count > 0)
        {
            try
            {
                await wantedService.ExecuteBulkWantedSearchAsync(
                    new WantedBulkSearchRequest(
                        Mode: "selected",
                        UnitIds: newlyAddedUnitIds.Take(25).ToList(),
                        MonitoredOnly: true,
                        MaxItems: 25),
                    actor: null,
                    ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Non-fatal SearchOnAdd failure for Import List #{Id}.", id);
            }
        }

        var msg = $"Fetched {discovered.Count}, added {addedCount}, skipped {skippedExisting} existing and {skippedExcluded} excluded.";
        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                UPDATE import_lists
                SET last_synced_at      = @Now,
                    last_result_message = @Msg,
                    updated_at          = @Now
                WHERE id = @Id;
                """,
                new { Id = id, Now = nowIso, Msg = msg },
                transaction: tx),
            ct);

        return new ImportListSyncReportDto(
            ListId: id,
            ListName: row.Name,
            FetchedCount: discovered.Count,
            AddedCount: addedCount,
            SkippedExistingCount: skippedExisting,
            SkippedExcludedCount: skippedExcluded,
            Message: msg);
    }

    private async Task<List<DiscoveredListEntry>> FetchCandidatesAsync(
        string listType,
        string mediaVertical,
        Dictionary<string, string> settings,
        CancellationToken ct)
    {
        var limit = settings.TryGetValue("limit", out var limStr) && int.TryParse(limStr, out var lVal)
            ? Math.Clamp(lVal, 1, 500)
            : 25;

        if (settings.TryGetValue("itemsJson", out var inlineJson) && !string.IsNullOrWhiteSpace(inlineJson))
        {
            return ParseGenericJsonEntries(inlineJson, limit);
        }

        using var client = httpClientOverride is not null
            ? httpClientOverride()
            : httpClientFactory.CreateHomelabClient(allowPrivateLan: true, ignoreSslErrors: true);

        switch (listType.ToLowerInvariant())
        {
            case "stevenlu":
            {
                var url = settings.TryGetValue("url", out var u) && !string.IsNullOrWhiteSpace(u)
                    ? u.Trim()
                    : "https://popular-movies-data.stevenlu.com/movies.json";
                var json = await client.GetStringAsync(url, ct);
                return ParseGenericJsonEntries(json, limit);
            }

            case "tmdb_popular":
            case "tmdb_trending":
            {
                var apiKey = settings.TryGetValue("apiKey", out var k) && !string.IsNullOrWhiteSpace(k)
                    ? k.Trim()
                    : await appSettings.GetAsync("metadata.tmdb.api_key", ct);
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new InvalidOperationException("TMDB API key is required for TMDB Import Lists (configure in list settings or Metadata settings).");
                }

                var isTv = mediaVertical.Equals("tv", StringComparison.OrdinalIgnoreCase);
                var endpoint = listType.Equals("tmdb_trending", StringComparison.OrdinalIgnoreCase)
                    ? $"https://api.themoviedb.org/3/trending/{(isTv ? "tv" : "movie")}/week?api_key={Uri.EscapeDataString(apiKey)}"
                    : $"https://api.themoviedb.org/3/{(isTv ? "tv" : "movie")}/popular?api_key={Uri.EscapeDataString(apiKey)}";

                var json = await client.GetStringAsync(endpoint, ct);
                using var doc = JsonDocument.Parse(json);
                var list = new List<DiscoveredListEntry>();
                if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in results.EnumerateArray().Take(limit))
                    {
                        var title = el.TryGetProperty("title", out var tEl) ? tEl.GetString()
                            : el.TryGetProperty("name", out var nEl) ? nEl.GetString()
                            : null;
                        if (string.IsNullOrWhiteSpace(title)) continue;

                        var ext = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (el.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var tmdbId))
                        {
                            ext["tmdb"] = tmdbId.ToString();
                        }

                        int? yr = null;
                        var dateRaw = el.TryGetProperty("release_date", out var rd) ? rd.GetString()
                            : el.TryGetProperty("first_air_date", out var fad) ? fad.GetString()
                            : null;
                        if (!string.IsNullOrWhiteSpace(dateRaw) && dateRaw.Length >= 4 && int.TryParse(dateRaw[..4], out var parsedYr))
                        {
                            yr = parsedYr;
                        }

                        var overview = el.TryGetProperty("overview", out var ov) ? ov.GetString() : null;
                        var poster = el.TryGetProperty("poster_path", out var pp) && pp.GetString() is { Length: > 0 } pPath
                            ? $"https://image.tmdb.org/t/p/w500{pPath}"
                            : null;
                        var origLang = el.TryGetProperty("original_language", out var ol) ? ol.GetString() : "en";

                        list.Add(new DiscoveredListEntry(title!, yr, ext, overview, poster, origLang));
                    }
                }

                return list;
            }

            case "anilist_trending":
            {
                var gql = new
                {
                    query = "query ($perPage: Int) { Page(page: 1, perPage: $perPage) { media(type: ANIME, sort: TRENDING_DESC) { id idMal title { english romaji } startDate { year } description coverImage { large } } } }",
                    variables = new { perPage = limit },
                };
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co")
                {
                    Content = new StringContent(JsonSerializer.Serialize(gql, JsonOpts), Encoding.UTF8, "application/json"),
                };
                using var resp = await client.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var list = new List<DiscoveredListEntry>();
                if (doc.RootElement.TryGetProperty("data", out var dEl) &&
                    dEl.TryGetProperty("Page", out var pEl) &&
                    pEl.TryGetProperty("media", out var mArr) &&
                    mArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in mArr.EnumerateArray())
                    {
                        string? title = null;
                        if (el.TryGetProperty("title", out var tObj) && tObj.ValueKind == JsonValueKind.Object)
                        {
                            title = (tObj.TryGetProperty("english", out var en) ? en.GetString() : null)
                                    ?? (tObj.TryGetProperty("romaji", out var ro) ? ro.GetString() : null);
                        }
                        if (string.IsNullOrWhiteSpace(title)) continue;

                        var ext = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (el.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var aniId)) ext["anilist"] = aniId.ToString();
                        if (el.TryGetProperty("idMal", out var malEl) && malEl.TryGetInt64(out var malId)) ext["mal"] = malId.ToString();

                        int? yr = null;
                        if (el.TryGetProperty("startDate", out var sd) && sd.ValueKind == JsonValueKind.Object &&
                            sd.TryGetProperty("year", out var yrEl) && yrEl.TryGetInt32(out var yVal))
                        {
                            yr = yVal;
                        }

                        var poster = el.TryGetProperty("coverImage", out var ci) && ci.ValueKind == JsonValueKind.Object &&
                                     ci.TryGetProperty("large", out var lg)
                            ? lg.GetString()
                            : null;
                        list.Add(new DiscoveredListEntry(title!, yr, ext, null, poster, "ja"));
                    }
                }

                return list;
            }

            case "trakt_popular":
            {
                var clientId = settings.TryGetValue("clientId", out var cid) && !string.IsNullOrWhiteSpace(cid)
                    ? cid.Trim()
                    : throw new InvalidOperationException("Trakt Client ID is required for Trakt Import Lists.");
                var isTv = mediaVertical.Equals("tv", StringComparison.OrdinalIgnoreCase);
                var listPath = settings.TryGetValue("listPath", out var lp) && !string.IsNullOrWhiteSpace(lp)
                    ? lp.Trim().TrimStart('/')
                    : $"{(isTv ? "shows" : "movies")}/popular?limit={limit}";
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.trakt.tv/{listPath}");
                req.Headers.TryAddWithoutValidation("trakt-api-version", "2");
                req.Headers.TryAddWithoutValidation("trakt-api-key", clientId);
                using var resp = await client.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct);
                return ParseGenericJsonEntries(json, limit);
            }

            case "openlibrary_subject":
            {
                var subject = settings.TryGetValue("subject", out var sub) && !string.IsNullOrWhiteSpace(sub)
                    ? sub.Trim().ToLowerInvariant()
                    : "science_fiction";
                var json = await client.GetStringAsync($"https://openlibrary.org/subjects/{Uri.EscapeDataString(subject)}.json?limit={limit}", ct);
                using var doc = JsonDocument.Parse(json);
                var list = new List<DiscoveredListEntry>();
                if (doc.RootElement.TryGetProperty("works", out var worksArr) && worksArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var w in worksArr.EnumerateArray().Take(limit))
                    {
                        var title = w.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(title)) continue;
                        var ext = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (w.TryGetProperty("key", out var kEl) && kEl.GetString() is { Length: > 0 } olKey)
                        {
                            ext["openlibrary"] = olKey.Replace("/works/", "", StringComparison.OrdinalIgnoreCase);
                        }
                        int? yr = w.TryGetProperty("first_publish_year", out var fy) && fy.TryGetInt32(out var yVal) ? yVal : null;
                        list.Add(new DiscoveredListEntry(title!, yr, ext, null, null, "en"));
                    }
                }
                return list;
            }

            default: // "custom_json"
            {
                if (!settings.TryGetValue("url", out var url) || string.IsNullOrWhiteSpace(url))
                {
                    throw new ArgumentException("Custom JSON Import List requires a 'url' or 'itemsJson' setting.");
                }

                var json = await client.GetStringAsync(url.Trim(), ct);
                return ParseGenericJsonEntries(json, limit);
            }
        }
    }

    private static List<DiscoveredListEntry> ParseGenericJsonEntries(string rawJson, int limit)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var arrayEl = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array
                ? itemsProp
                : doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("results", out var resProp) && resProp.ValueKind == JsonValueKind.Array
                    ? resProp
                    : default;

        var list = new List<DiscoveredListEntry>();
        if (arrayEl.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var rawEl in arrayEl.EnumerateArray().Take(limit))
        {
            var el = rawEl;
            if (rawEl.ValueKind == JsonValueKind.Object)
            {
                if (rawEl.TryGetProperty("movie", out var movieSub) && movieSub.ValueKind == JsonValueKind.Object) el = movieSub;
                else if (rawEl.TryGetProperty("show", out var showSub) && showSub.ValueKind == JsonValueKind.Object) el = showSub;
            }

            if (el.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = (el.TryGetProperty("title", out var tEl) ? tEl.GetString() : null)
                        ?? (el.TryGetProperty("name", out var nEl) ? nEl.GetString() : null);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            int? year = null;
            if (el.TryGetProperty("year", out var yrEl) && yrEl.TryGetInt32(out var yInt))
            {
                year = yInt;
            }

            var ext = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ExtractExtId(el, "imdb_id", "imdb", ext);
            ExtractExtId(el, "imdbId", "imdb", ext);
            ExtractExtId(el, "tmdb_id", "tmdb", ext);
            ExtractExtId(el, "tmdbId", "tmdb", ext);
            ExtractExtId(el, "tvdb_id", "tvdb", ext);
            ExtractExtId(el, "tvdbId", "tvdb", ext);
            if (el.TryGetProperty("ids", out var idsObj) && idsObj.ValueKind == JsonValueKind.Object)
            {
                ExtractExtId(idsObj, "imdb", "imdb", ext);
                ExtractExtId(idsObj, "tmdb", "tmdb", ext);
                ExtractExtId(idsObj, "tvdb", "tvdb", ext);
            }

            var poster = (el.TryGetProperty("poster_url", out var pu) ? pu.GetString() : null)
                         ?? (el.TryGetProperty("posterUrl", out var pu2) ? pu2.GetString() : null);
            var overview = el.TryGetProperty("overview", out var ov) ? ov.GetString() : null;
            var origLang = el.TryGetProperty("originalLanguage", out var ol) ? ol.GetString() : "en";

            list.Add(new DiscoveredListEntry(title!, year, ext, overview, poster, origLang));
        }

        return list;
    }

    private static void ExtractExtId(JsonElement obj, string propName, string targetKey, Dictionary<string, string> dest)
    {
        if (!obj.TryGetProperty(propName, out var prop))
        {
            return;
        }

        var val = prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(val))
        {
            dest[targetKey] = val.Trim();
        }
    }

    private static List<string> BuildCandidateKeys(DiscoveredListEntry cand, string normTitle)
    {
        var keys = new List<string>();
        foreach (var (k, v) in cand.ExternalIds)
        {
            if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v))
            {
                keys.Add($"{k.Trim().ToLowerInvariant()}:{v.Trim().ToLowerInvariant()}");
            }
        }

        keys.Add($"title:{normTitle}:{cand.Year ?? 0}");
        keys.Add($"title:{normTitle}");
        return keys;
    }

    private static string NormalizeTitle(string? title) =>
        string.IsNullOrWhiteSpace(title)
            ? ""
            : AlphaNumSanitizer().Replace(title.ToLowerInvariant(), "");

    private Dictionary<string, string> UnpackSettings(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var json = secretProtector.IsProtectedEnvelope(rawJson)
            ? secretProtector.Unprotect(rawJson)
            : rawJson;

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOpts) ?? new Dictionary<string, string>();
        return new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
    }

    private ImportListSummaryDto MapListRow(ImportListRow row)
    {
        var unpacked = UnpackSettings(row.SettingsJson);
        var masked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in unpacked)
        {
            var isSensitive = k.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
                              k.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                              k.Contains("secret", StringComparison.OrdinalIgnoreCase);
            masked[k] = isSensitive && !string.IsNullOrEmpty(v) ? "********" : v;
        }

        return new ImportListSummaryDto(
            Id: row.Id,
            Name: row.Name,
            ListType: row.ListType,
            MediaVertical: row.MediaVertical,
            LibraryId: row.LibraryId,
            QualityProfileId: row.QualityProfileId,
            MonitorMode: row.MonitorMode,
            MinimumAvailability: row.MinimumAvailability,
            SearchOnAdd: row.SearchOnAdd != 0,
            Settings: masked,
            Tags: JsonSerializer.Deserialize<string[]>(row.TagsJson, JsonOpts) ?? [],
            SyncIntervalHours: row.SyncIntervalHours,
            IsEnabled: row.IsEnabled != 0,
            LastSyncedAt: row.LastSyncedAt,
            LastResultMessage: row.LastResultMessage,
            CreatedAt: row.CreatedAt,
            UpdatedAt: row.UpdatedAt);
    }

    private sealed record DiscoveredListEntry(
        string Title,
        int? Year,
        IReadOnlyDictionary<string, string> ExternalIds,
        string? Overview,
        string? PosterUrl,
        string? OriginalLanguage);

    private sealed class ImportListRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string ListType { get; init; } = "custom_json";
        public string MediaVertical { get; init; } = "movies";
        public long LibraryId { get; init; }
        public long? QualityProfileId { get; init; }
        public string MonitorMode { get; init; } = "all";
        public string MinimumAvailability { get; init; } = "released";
        public int SearchOnAdd { get; init; }
        public string SettingsJson { get; init; } = "{}";
        public string TagsJson { get; init; } = "[]";
        public int SyncIntervalHours { get; init; } = 12;
        public int IsEnabled { get; init; }
        public string? LastSyncedAt { get; init; }
        public string? LastResultMessage { get; init; }
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private sealed class ExclusionRow
    {
        public long Id { get; init; }
        public string ExternalKey { get; init; } = "";
        public string Title { get; init; } = "";
        public string MediaVertical { get; init; } = "movies";
        public int? Year { get; init; }
        public string CreatedAt { get; init; } = "";
    }
}
