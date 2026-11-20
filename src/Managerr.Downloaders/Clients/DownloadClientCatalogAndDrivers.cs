using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Managerr.Common.Net;
using Managerr.Indexers.Categories;

namespace Managerr.Downloaders.Clients;

public sealed record DownloadClientDescriptor(
    string Implementation,
    string DisplayName,
    string Protocol,
    int DefaultPort,
    string AuthStyle,
    string Description);

public sealed record DownloadClientTestResult(
    bool Ok,
    string? Version,
    string Message);

/// <summary>The driver has no implementation of an action for this client (see #38).</summary>
public sealed class DownloadClientActionNotSupportedException(string clientName, string implementation, string action)
    : InvalidOperationException($"Download client '{clientName}' ({implementation}) does not support {action} in Managerr yet.");

public sealed record ActiveDownloadQueueItem(
    long ClientId,
    string ClientName,
    string Protocol,
    string ExternalId,
    string? InfoHash,
    string Title,
    string Category,
    string MediaVertical,
    string Status,
    long TotalBytes,
    long RemainingBytes,
    string OutputPath,
    IReadOnlyList<string> ContainedFiles,
    bool IsStalled,
    bool IsMetadataStuck,
    string? ErrorMessage,
    bool CanMoveFiles = true);

public sealed record DownloadClientConnectionSpec(
    long Id,
    string Name,
    string Implementation,
    string Protocol,
    string Host,
    int Port,
    bool UseSsl,
    string? UrlBase,
    string? Username,
    string? PlainPassword,
    string? PlainApiKey,
    IReadOnlyDictionary<string, string> CategoryMappings,
    IReadOnlyDictionary<string, string> ExtraSettings);

public interface IDownloadClientDriverGateway
{
    IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers();
    Task<string> EnqueueReleaseAsync(
        DownloadClientConnectionSpec client,
        string releaseTitle,
        string downloadUrlOrMagnet,
        string resolvedCategory,
        CancellationToken ct = default);
    Task<string> EnqueueReleaseBytesAsync(
        DownloadClientConnectionSpec client,
        string releaseTitle,
        byte[] fileBytes,
        string resolvedCategory,
        CancellationToken ct = default) =>
        throw new DownloadClientActionNotSupportedException(client.Name, client.Implementation, "adding torrent/NZB file bytes");
    Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(
        DownloadClientConnectionSpec client,
        CancellationToken ct = default);
    Task RemoveDownloadAsync(
        DownloadClientConnectionSpec client,
        string externalId,
        bool deleteData,
        CancellationToken ct = default);
    Task ControlDownloadAsync(
        DownloadClientConnectionSpec client,
        string externalId,
        string action,
        CancellationToken ct = default) => Task.CompletedTask;
    Task<DownloadClientTestResult> TestConnectionAsync(
        DownloadClientConnectionSpec client,
        CancellationToken ct = default) =>
        Task.FromResult(new DownloadClientTestResult(true, null, "OK"));
}

public static class DownloadClientCatalog
{
    public static readonly IReadOnlyList<DownloadClientDescriptor> All =
    [
        // Torrent Clients (8 genuine supported drivers)
        new("qbittorrent", "qBittorrent", "torrent", 8080, "username_password", "WebAPI v2.8+ with multi-vertical categories, ratio/seed time limits, and Gluetun VPN port support."),
        new("deluge", "Deluge", "torrent", 8112, "password", "Deluge WebUI JSON-RPC with Label plugin category routing."),
        new("transmission", "Transmission", "torrent", 9091, "username_password", "Transmission RPC with per-vertical download-dir / label support."),
        new("rtorrent", "rTorrent / ruTorrent", "torrent", 8080, "username_password", "XML-RPC over HTTP/SCGI with custom1 label category support."),
        new("flood", "Flood", "torrent", 3000, "username_password", "Modern Flood REST API for rTorrent/qBittorrent/Transmission backends."),
        new("vuze", "Vuze / BiglyBT", "torrent", 9091, "username_password", "Transmission-compatible RPC adapter for Vuze/BiglyBT."),
        new("aria2", "Aria2", "torrent", 6800, "secret_token", "Aria2 JSON-RPC 2.0 (`token:...`) high-speed torrent & magnet engine."),
        new("torrent_blackhole", "Torrent Blackhole", "torrent", 0, "filesystem", "Drops .torrent / .magnet files into a watched folder and imports from completed path."),

        // Usenet Clients (4 genuine supported drivers)
        new("sabnzbd", "SABnzbd", "usenet", 8080, "api_key", "SABnzbd JSON API (`mode=addurl|queue|history`) with per-vertical categories."),
        new("nzbget", "NZBGet", "usenet", 6789, "username_password", "NZBGet JSON-RPC (`append`, `listgroups`, `history`)."),
        new("pneumatic", "Pneumatic", "usenet", 0, "filesystem", "Kodi/XBMC Pneumatic NZB drop adapter."),
        new("usenet_blackhole", "Usenet Blackhole", "usenet", 0, "filesystem", "Drops .nzb files into a watched directory and imports completed folders."),
    ];

    public static readonly IReadOnlyDictionary<string, DownloadClientDescriptor> ByKey =
        All.ToDictionary(d => d.Implementation, StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, string> DefaultCategoryMappings() => new(StringComparer.OrdinalIgnoreCase)
    {
        [MediaVerticals.Tv] = "tv-managerr",
        [MediaVerticals.Anime] = "anime-managerr",
        [MediaVerticals.Movies] = "movies-managerr",
        [MediaVerticals.Music] = "music-managerr",
        [MediaVerticals.Ebooks] = "ebooks-managerr",
        [MediaVerticals.Audiobooks] = "audiobooks-managerr",
        [MediaVerticals.Comics] = "comics-managerr",
    };
}

public sealed partial class DownloadClientDriverGateway(ISafeHttpClientFactory httpFactory) : IDownloadClientDriverGateway
{
    public IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers() => DownloadClientCatalog.All;

    public async Task<string> EnqueueReleaseAsync(
        DownloadClientConnectionSpec client,
        string releaseTitle,
        string downloadUrlOrMagnet,
        string resolvedCategory,
        CancellationToken ct = default)
    {
        if (TryDecodeInlineBase64Payload(downloadUrlOrMagnet, out var inlineBytes))
        {
            return await EnqueueReleaseBytesAsync(client, releaseTitle, inlineBytes, resolvedCategory, ct);
        }

        var impl = client.Implementation.ToLowerInvariant();
        if (impl is "torrent_blackhole" or "usenet_blackhole" or "pneumatic")
        {
            var watchFolder = client.ExtraSettings.GetValueOrDefault("watchFolder")
                ?? Path.Combine(Path.GetTempPath(), "managerr-blackhole", impl);
            Directory.CreateDirectory(watchFolder);
            var safeFile = string.Concat(releaseTitle.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var ext = client.Protocol == "usenet" ? ".nzb" : (downloadUrlOrMagnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? ".magnet" : ".url");
            var fullPath = Path.Combine(watchFolder, $"{safeFile}{ext}");
            await File.WriteAllTextAsync(fullPath, downloadUrlOrMagnet, ct);
            return Path.GetFileName(fullPath);
        }

        using var http = httpFactory.CreateHomelabClient(allowPrivateLan: true, ignoreSslErrors: true);
        var baseUrl = BuildBaseUrl(client);

        switch (impl)
        {
            case "qbittorrent":
            {
                await LoginToQbittorrentAsync(http, client, baseUrl, ct);

                using var addResp = await http.PostAsync(
                    $"{baseUrl}/api/v2/torrents/add",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["urls"] = downloadUrlOrMagnet,
                        ["category"] = resolvedCategory,
                    }),
                    ct);
                addResp.EnsureSuccessStatusCode();
                var extractedHash = TryExtractBtihFromMagnet(downloadUrlOrMagnet);
                return extractedHash ?? $"qbt:{Guid.NewGuid():N}";
            }

            case "deluge":
            {
                await LoginToDelugeAsync(http, client, baseUrl, ct);
                var options = new Dictionary<string, object>();
                JsonElement addResult;
                if (downloadUrlOrMagnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        addResult = await DelugeRpcAsync(http, baseUrl, client.Name, "core.add_torrent_url", [downloadUrlOrMagnet, options], ct);
                    }
                    catch (InvalidOperationException)
                    {
                        addResult = await DelugeRpcAsync(http, baseUrl, client.Name, "core.add_torrent_magnet", [downloadUrlOrMagnet, options], ct);
                    }
                }
                else
                {
                    addResult = await DelugeRpcAsync(http, baseUrl, client.Name, "core.add_torrent_url", [downloadUrlOrMagnet, options], ct);
                }

                var hash = (addResult.ValueKind == JsonValueKind.String ? addResult.GetString() : null)?.ToLowerInvariant()
                           ?? TryExtractBtihFromMagnet(downloadUrlOrMagnet);
                if (!string.IsNullOrWhiteSpace(hash) && !string.IsNullOrWhiteSpace(resolvedCategory))
                {
                    await TrySetDelugeLabelAsync(http, baseUrl, client.Name, hash, resolvedCategory, ct);
                }

                return !string.IsNullOrWhiteSpace(hash) ? hash : $"deluge:{Guid.NewGuid():N}";
            }

            case "rtorrent":
            {
                var rpcParams = new List<object> { "", downloadUrlOrMagnet };
                if (!string.IsNullOrWhiteSpace(resolvedCategory))
                {
                    rpcParams.Add($"d.custom1.set={resolvedCategory}");
                }

                await SendRtorrentXmlRpcAsync(http, client, "load.start", rpcParams, ct);
                return TryExtractBtihFromMagnet(downloadUrlOrMagnet) ?? $"rtorrent:{Guid.NewGuid():N}";
            }

            case "flood":
            {
                await LoginToFloodAsync(http, client, baseUrl, ct);
                var tags = string.IsNullOrWhiteSpace(resolvedCategory) ? Array.Empty<string>() : new[] { resolvedCategory };
                using var resp = await http.PostAsJsonAsync($"{baseUrl}/api/torrents/add-urls", new
                {
                    urls = new[] { downloadUrlOrMagnet },
                    tags,
                    start = true,
                }, ct);
                resp.EnsureSuccessStatusCode();
                return TryExtractBtihFromMagnet(downloadUrlOrMagnet) ?? $"flood:{Guid.NewGuid():N}";
            }

            case "sabnzbd":
            {
                var uri = $"{baseUrl}/api?mode=addurl&output=json&apikey={Uri.EscapeDataString(client.PlainApiKey ?? "")}" +
                          $"&name={Uri.EscapeDataString(downloadUrlOrMagnet)}&nzbname={Uri.EscapeDataString(releaseTitle)}&cat={Uri.EscapeDataString(resolvedCategory)}";
                var json = await http.GetFromJsonAsync<JsonElement>(uri, ct);
                if (json.TryGetProperty("nzo_ids", out var ids) && ids.GetArrayLength() > 0)
                {
                    return ids[0].GetString() ?? $"sab:{Guid.NewGuid():N}";
                }
                return $"sab:{Guid.NewGuid():N}";
            }

            case "nzbget":
            {
                ApplyBasicAuth(http, client.Username, client.PlainPassword);
                using var resp = await http.PostAsJsonAsync($"{baseUrl}/jsonrpc", new
                {
                    method = "append",
                    @params = new object[] { releaseTitle, downloadUrlOrMagnet, resolvedCategory, 0, false, false, "", 0, "SCORE", new object[0] },
                }, ct);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
                return json.TryGetProperty("result", out var res) ? res.ToString() : $"nzbget:{Guid.NewGuid():N}";
            }

            case "transmission" or "vuze":
            {
                ApplyBasicAuth(http, client.Username, client.PlainPassword);
                var rpcResp = await SendTransmissionRpcAsync(http, $"{baseUrl}/transmission/rpc", new
                {
                    method = "torrent-add",
                    arguments = new { filename = downloadUrlOrMagnet, labels = new[] { resolvedCategory } },
                }, ct);
                // torrent-add answers {"torrent-added": {...}} or {"torrent-duplicate": {...}} with the hashString the queue
                // reports later, so use that as the download id
                if (rpcResp.TryGetProperty("arguments", out var args))
                {
                    foreach (var key in new[] { "torrent-added", "torrent-duplicate" })
                    {
                        if (args.TryGetProperty(key, out var added) &&
                            added.TryGetProperty("hashString", out var hashEl) &&
                            !string.IsNullOrWhiteSpace(hashEl.GetString()))
                        {
                            return hashEl.GetString()!.ToLowerInvariant();
                        }
                    }
                }

                return TryExtractBtihFromMagnet(downloadUrlOrMagnet) ?? $"tr:{Guid.NewGuid():N}";
            }

            case "aria2":
            {
                var rpcParams = new List<object>();
                if (!string.IsNullOrWhiteSpace(client.PlainApiKey))
                {
                    rpcParams.Add($"token:{client.PlainApiKey}");
                }
                rpcParams.Add(new[] { downloadUrlOrMagnet });
                using var resp = await http.PostAsJsonAsync($"{baseUrl}/jsonrpc", new
                {
                    jsonrpc = "2.0",
                    id = "managerr",
                    method = "aria2.addUri",
                    @params = rpcParams,
                }, ct);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
                return json.TryGetProperty("result", out var gid) ? gid.GetString() ?? "aria2" : "aria2";
            }

            default:
                throw new DownloadClientActionNotSupportedException(client.Name, impl, "adding downloads");
        }
    }

    public async Task<string> EnqueueReleaseBytesAsync(
        DownloadClientConnectionSpec client,
        string releaseTitle,
        byte[] fileBytes,
        string resolvedCategory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);
        var impl = client.Implementation.ToLowerInvariant();

        if (impl is "torrent_blackhole" or "usenet_blackhole" or "pneumatic")
        {
            var watchFolder = client.ExtraSettings.GetValueOrDefault("watchFolder")
                ?? Path.Combine(Path.GetTempPath(), "managerr-blackhole", impl);
            Directory.CreateDirectory(watchFolder);
            var safeFile = string.Concat(releaseTitle.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var ext = client.Protocol == "usenet" ? ".nzb" : ".torrent";
            var fullPath = Path.Combine(watchFolder, $"{safeFile}{ext}");
            await File.WriteAllBytesAsync(fullPath, fileBytes, ct);
            return Path.GetFileName(fullPath);
        }

        using var http = httpFactory.CreateHomelabClient(allowPrivateLan: true, ignoreSslErrors: true);
        var baseUrl = BuildBaseUrl(client);
        var base64Payload = Convert.ToBase64String(fileBytes);
        var extractedHash = TryExtractBtihFromTorrentBytes(fileBytes);

        switch (impl)
        {
            case "qbittorrent":
            {
                await LoginToQbittorrentAsync(http, client, baseUrl, ct);
                using var form = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(fileBytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
                form.Add(fileContent, "torrents", $"{releaseTitle}.torrent");
                if (!string.IsNullOrWhiteSpace(resolvedCategory))
                {
                    form.Add(new StringContent(resolvedCategory), "category");
                }

                using var resp = await http.PostAsync($"{baseUrl}/api/v2/torrents/add", form, ct);
                resp.EnsureSuccessStatusCode();
                return extractedHash ?? $"qbt:{Guid.NewGuid():N}";
            }

            case "deluge":
            {
                await LoginToDelugeAsync(http, client, baseUrl, ct);
                var fileName = releaseTitle.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
                    ? releaseTitle
                    : $"{releaseTitle}.torrent";
                var addResult = await DelugeRpcAsync(
                    http,
                    baseUrl,
                    client.Name,
                    "core.add_torrent_file",
                    [fileName, base64Payload, new Dictionary<string, object>()],
                    ct);
                var hash = (addResult.ValueKind == JsonValueKind.String ? addResult.GetString() : null)?.ToLowerInvariant()
                           ?? extractedHash;
                if (!string.IsNullOrWhiteSpace(hash) && !string.IsNullOrWhiteSpace(resolvedCategory))
                {
                    await TrySetDelugeLabelAsync(http, baseUrl, client.Name, hash, resolvedCategory, ct);
                }

                return !string.IsNullOrWhiteSpace(hash) ? hash : $"deluge:{Guid.NewGuid():N}";
            }

            case "rtorrent":
            {
                var rpcParams = new List<object> { "", fileBytes };
                if (!string.IsNullOrWhiteSpace(resolvedCategory))
                {
                    rpcParams.Add($"d.custom1.set={resolvedCategory}");
                }

                await SendRtorrentXmlRpcAsync(http, client, "load.raw_start", rpcParams, ct);
                return extractedHash ?? $"rtorrent:{Guid.NewGuid():N}";
            }

            case "flood":
            {
                await LoginToFloodAsync(http, client, baseUrl, ct);
                var tags = string.IsNullOrWhiteSpace(resolvedCategory) ? Array.Empty<string>() : new[] { resolvedCategory };
                using var resp = await http.PostAsJsonAsync($"{baseUrl}/api/torrents/add-files", new
                {
                    files = new[] { base64Payload },
                    tags,
                    start = true,
                }, ct);
                resp.EnsureSuccessStatusCode();
                return extractedHash ?? $"flood:{Guid.NewGuid():N}";
            }

            case "transmission" or "vuze":
            {
                ApplyBasicAuth(http, client.Username, client.PlainPassword);
                var rpcResp = await SendTransmissionRpcAsync(http, $"{baseUrl}/transmission/rpc", new
                {
                    method = "torrent-add",
                    arguments = new { metainfo = base64Payload, labels = new[] { resolvedCategory } },
                }, ct);
                if (rpcResp.TryGetProperty("arguments", out var args))
                {
                    foreach (var key in new[] { "torrent-added", "torrent-duplicate" })
                    {
                        if (args.TryGetProperty(key, out var added) &&
                            added.TryGetProperty("hashString", out var hashEl) &&
                            !string.IsNullOrWhiteSpace(hashEl.GetString()))
                        {
                            return hashEl.GetString()!.ToLowerInvariant();
                        }
                    }
                }

                return extractedHash ?? $"tr:{Guid.NewGuid():N}";
            }

            case "aria2":
            {
                var rpcParams = new List<object>();
                if (!string.IsNullOrWhiteSpace(client.PlainApiKey))
                {
                    rpcParams.Add($"token:{client.PlainApiKey}");
                }
                rpcParams.Add(base64Payload);
                using var resp = await http.PostAsJsonAsync($"{baseUrl}/jsonrpc", new
                {
                    jsonrpc = "2.0",
                    id = "managerr",
                    method = "aria2.addTorrent",
                    @params = rpcParams,
                }, ct);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
                return json.TryGetProperty("result", out var gid) ? gid.GetString() ?? "aria2" : "aria2";
            }

            case "nzbget":
            {
                ApplyBasicAuth(http, client.Username, client.PlainPassword);
                var nzbFileName = releaseTitle.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase)
                    ? releaseTitle
                    : $"{releaseTitle}.nzb";
                using var resp = await http.PostAsJsonAsync($"{baseUrl}/jsonrpc", new
                {
                    method = "append",
                    @params = new object[] { nzbFileName, base64Payload, resolvedCategory, 0, false, false, "", 0, "SCORE", new object[0] },
                }, ct);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
                return json.TryGetProperty("result", out var res) ? res.ToString() : $"nzbget:{Guid.NewGuid():N}";
            }

            default:
                throw new DownloadClientActionNotSupportedException(client.Name, impl, "adding torrent/NZB file bytes");
        }
    }

    public async Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(
        DownloadClientConnectionSpec client,
        CancellationToken ct = default)
    {
        var impl = client.Implementation.ToLowerInvariant();
        if (impl is "torrent_blackhole" or "usenet_blackhole" or "pneumatic")
        {
            return [];
        }

        using var http = httpFactory.CreateHomelabClient(allowPrivateLan: true, ignoreSslErrors: true);
        var baseUrl = BuildBaseUrl(client);

        if (impl == "qbittorrent")
        {
            await LoginToQbittorrentAsync(http, client, baseUrl, ct);
            var items = await http.GetFromJsonAsync<JsonElement>($"{baseUrl}/api/v2/torrents/info", ct);
            if (items.ValueKind != JsonValueKind.Array) return [];
            var result = new List<ActiveDownloadQueueItem>();
            foreach (var t in items.EnumerateArray())
            {
                var hash = t.GetProperty("hash").GetString() ?? "";
                var name = t.GetProperty("name").GetString() ?? "";
                var cat = t.TryGetProperty("category", out var cEl) ? cEl.GetString() ?? "" : "";
                var state = t.TryGetProperty("state", out var sEl) ? sEl.GetString() ?? "" : "";
                var size = t.TryGetProperty("size", out var szEl) ? szEl.GetInt64() : 0L;
                var rem = t.TryGetProperty("amount_left", out var amEl) ? amEl.GetInt64() : 0L;
                var contentPath = t.TryGetProperty("content_path", out var cpEl) ? cpEl.GetString() ?? "" : "";
                var canMoveTorrent = state.Equals("pausedUP", StringComparison.OrdinalIgnoreCase) ||
                                     state.Equals("stoppedUP", StringComparison.OrdinalIgnoreCase) ||
                                     state.Equals("Completed", StringComparison.OrdinalIgnoreCase);
                result.Add(new ActiveDownloadQueueItem(
                    ClientId: client.Id,
                    ClientName: client.Name,
                    Protocol: "torrent",
                    ExternalId: hash,
                    InfoHash: hash.ToLowerInvariant(),
                    Title: name,
                    Category: cat,
                    MediaVertical: ResolveVerticalFromClientCategory(client, cat),
                    Status: state,
                    TotalBytes: size,
                    RemainingBytes: rem,
                    OutputPath: contentPath,
                    ContainedFiles: string.IsNullOrEmpty(contentPath) ? [] : [contentPath],
                    IsStalled: state.Contains("stalledDL", StringComparison.OrdinalIgnoreCase),
                    IsMetadataStuck: state.Equals("metaDL", StringComparison.OrdinalIgnoreCase),
                    ErrorMessage: state.Contains("error", StringComparison.OrdinalIgnoreCase) ? state : null,
                    CanMoveFiles: canMoveTorrent));
            }
            return result;
        }

        if (impl == "deluge")
        {
            await LoginToDelugeAsync(http, client, baseUrl, ct);
            var uiResult = await DelugeRpcAsync(
                http,
                baseUrl,
                client.Name,
                "web.update_ui",
                [
                    new[]
                    {
                        "hash", "name", "state", "progress", "eta", "message",
                        "is_finished", "save_path", "total_size", "total_done",
                        "total_remaining", "label", "files", "time_added",
                    },
                    new Dictionary<string, object>(),
                ],
                ct);

            var torrentsObj = uiResult.ValueKind == JsonValueKind.Object &&
                              uiResult.TryGetProperty("torrents", out var nestedTorrents) &&
                              nestedTorrents.ValueKind == JsonValueKind.Object
                ? nestedTorrents
                : uiResult;

            if (torrentsObj.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var list = new List<ActiveDownloadQueueItem>();
            foreach (var prop in torrentsObj.EnumerateObject())
            {
                var t = prop.Value;
                if (t.ValueKind != JsonValueKind.Object) continue;
                var hash = (t.TryGetProperty("hash", out var hEl) ? hEl.GetString() : prop.Name)?.ToLowerInvariant() ?? prop.Name.ToLowerInvariant();
                var name = t.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                var state = t.TryGetProperty("state", out var sEl) ? sEl.GetString() ?? "Downloading" : "Downloading";
                var label = t.TryGetProperty("label", out var lEl) ? lEl.GetString() ?? "" : "";
                var savePath = t.TryGetProperty("save_path", out var spEl) ? spEl.GetString() ?? "" : "";
                var message = t.TryGetProperty("message", out var mEl) ? mEl.GetString() : null;
                var totalSize = ParseJsonInt64(t, "total_size");
                var totalDone = ParseJsonInt64(t, "total_done");
                var totalRem = t.TryGetProperty("total_remaining", out _)
                    ? ParseJsonInt64(t, "total_remaining")
                    : Math.Max(0L, totalSize - totalDone);
                var isFinished = (t.TryGetProperty("is_finished", out var finEl) && finEl.ValueKind == JsonValueKind.True) ||
                                 (totalSize > 0 && totalRem == 0 && state.Equals("Seeding", StringComparison.OrdinalIgnoreCase));

                var files = new List<string>();
                if (t.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in filesEl.EnumerateArray())
                    {
                        if (f.ValueKind == JsonValueKind.Object && f.TryGetProperty("path", out var fpEl) && fpEl.GetString() is { Length: > 0 } fp)
                        {
                            files.Add(string.IsNullOrWhiteSpace(savePath) ? fp : Path.Combine(savePath, fp));
                        }
                    }
                }

                var isError = state.Equals("Error", StringComparison.OrdinalIgnoreCase);
                var mappedStatus = isError
                    ? "error"
                    : isFinished
                        ? "Completed"
                        : state;
                var outputPath = string.IsNullOrWhiteSpace(savePath)
                    ? ""
                    : (string.IsNullOrWhiteSpace(name) ? savePath : Path.Combine(savePath, name));

                list.Add(new ActiveDownloadQueueItem(
                    ClientId: client.Id,
                    ClientName: client.Name,
                    Protocol: "torrent",
                    ExternalId: hash,
                    InfoHash: hash,
                    Title: name,
                    Category: label,
                    MediaVertical: ResolveVerticalFromClientCategory(client, label),
                    Status: mappedStatus,
                    TotalBytes: totalSize,
                    RemainingBytes: isFinished ? 0L : totalRem,
                    OutputPath: outputPath,
                    ContainedFiles: files,
                    IsStalled: false,
                    IsMetadataStuck: totalSize == 0 && totalDone == 0 && state.Equals("Downloading", StringComparison.OrdinalIgnoreCase),
                    ErrorMessage: isError ? (string.IsNullOrWhiteSpace(message) ? "Deluge torrent error" : message) : null,
                    CanMoveFiles: isFinished && state.Equals("Paused", StringComparison.OrdinalIgnoreCase)));
            }

            return list;
        }

        if (impl == "rtorrent")
        {
            var valEl = await SendRtorrentXmlRpcAsync(
                http,
                client,
                "d.multicall2",
                ["", "main", "d.hash=", "d.name=", "d.size_bytes=", "d.bytes_done=", "d.state=", "d.directory=", "d.custom1=", "d.message="],
                ct);

            var list = new List<ActiveDownloadQueueItem>();
            var outerData = valEl.Descendants().FirstOrDefault(e => e.Name.LocalName == "data");
            if (outerData is null)
            {
                return list;
            }

            foreach (var rowVal in outerData.Elements().Where(e => e.Name.LocalName == "value"))
            {
                var rowData = rowVal.Descendants().FirstOrDefault(e => e.Name.LocalName == "data");
                if (rowData is null) continue;
                var cells = rowData.Elements().Where(e => e.Name.LocalName == "value").Select(ExtractXmlRpcScalarString).ToList();
                if (cells.Count < 8) continue;

                var rawHash = cells[0].Trim();
                var hash = rawHash.ToLowerInvariant();
                var name = cells[1];
                var sizeBytes = long.TryParse(cells[2], out var sb) ? sb : 0L;
                var bytesDone = long.TryParse(cells[3], out var bd) ? bd : 0L;
                var state = int.TryParse(cells[4], out var st) ? st : 1;
                var directory = cells[5];
                var rawLabel = cells[6];
                var category = string.IsNullOrWhiteSpace(rawLabel) ? "" : Uri.UnescapeDataString(rawLabel);
                var message = cells[7];

                var remBytes = Math.Max(0L, sizeBytes - bytesDone);
                var isCompleted = sizeBytes > 0 && remBytes == 0;
                var hasError = !string.IsNullOrWhiteSpace(message) &&
                               !message.Equals("Tried all trackers.", StringComparison.OrdinalIgnoreCase);

                var mappedStatus = hasError && !isCompleted
                    ? "error"
                    : isCompleted
                        ? "Completed"
                        : state == 0
                            ? "Paused"
                            : "Downloading";

                var outputPath = string.IsNullOrWhiteSpace(directory)
                    ? ""
                    : (!string.IsNullOrWhiteSpace(name) && !directory.EndsWith(name, StringComparison.Ordinal)
                        ? Path.Combine(directory, name)
                        : directory);

                list.Add(new ActiveDownloadQueueItem(
                    ClientId: client.Id,
                    ClientName: client.Name,
                    Protocol: "torrent",
                    ExternalId: rawHash,
                    InfoHash: hash,
                    Title: name,
                    Category: category,
                    MediaVertical: ResolveVerticalFromClientCategory(client, category),
                    Status: mappedStatus,
                    TotalBytes: sizeBytes,
                    RemainingBytes: remBytes,
                    OutputPath: outputPath,
                    ContainedFiles: string.IsNullOrWhiteSpace(outputPath) ? [] : [outputPath],
                    IsStalled: false,
                    IsMetadataStuck: sizeBytes == 0 && bytesDone == 0 && state != 0,
                    ErrorMessage: hasError ? message : null,
                    CanMoveFiles: isCompleted && state == 0));
            }

            return list;
        }

        if (impl == "flood")
        {
            await LoginToFloodAsync(http, client, baseUrl, ct);
            var doc = await http.GetFromJsonAsync<JsonElement>($"{baseUrl}/api/torrents", ct);
            var list = new List<ActiveDownloadQueueItem>();

            var torrentsEl = doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty("torrents", out var tProp)
                ? tProp
                : doc;

            IEnumerable<(string KeyHash, JsonElement Item)> EnumerateFloodTorrents()
            {
                if (torrentsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in torrentsEl.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.Object)
                        {
                            yield return (p.Name, p.Value);
                        }
                    }
                }
                else if (torrentsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in torrentsEl.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.Object)
                        {
                            var h = el.TryGetProperty("hash", out var hEl) ? hEl.GetString() ?? "" : "";
                            yield return (h, el);
                        }
                    }
                }
            }

            foreach (var (keyHash, t) in EnumerateFloodTorrents())
            {
                var hash = (t.TryGetProperty("hash", out var hEl) ? hEl.GetString() : keyHash)?.ToLowerInvariant() ?? keyHash.ToLowerInvariant();
                var name = t.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                var sizeBytes = ParseJsonInt64(t, "sizeBytes");
                var bytesDone = ParseJsonInt64(t, "bytesDone");
                var remBytes = Math.Max(0L, sizeBytes - bytesDone);
                var directory = t.TryGetProperty("directory", out var dEl) ? dEl.GetString() ?? "" : "";
                var message = t.TryGetProperty("message", out var mEl) ? mEl.GetString() : null;

                var cat = "";
                if (t.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array && tagsEl.GetArrayLength() > 0)
                {
                    cat = tagsEl[0].GetString() ?? "";
                }

                var statusFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (t.TryGetProperty("status", out var stEl))
                {
                    if (stEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in stEl.EnumerateArray())
                        {
                            if (s.ValueKind == JsonValueKind.String && s.GetString() is { Length: > 0 } sv)
                            {
                                statusFlags.Add(sv);
                            }
                        }
                    }
                    else if (stEl.ValueKind == JsonValueKind.String && stEl.GetString() is { Length: > 0 } singleSt)
                    {
                        statusFlags.Add(singleSt);
                    }
                }

                var isError = statusFlags.Contains("error");
                var isCompleted = statusFlags.Contains("complete") ||
                                  statusFlags.Contains("seeding") ||
                                  (sizeBytes > 0 && remBytes == 0);
                var isStopped = statusFlags.Contains("stopped") || statusFlags.Contains("paused");
                var mappedStatus = isError
                    ? "error"
                    : isCompleted
                        ? "Completed"
                        : isStopped
                            ? "Paused"
                            : "Downloading";

                var outputPath = string.IsNullOrWhiteSpace(directory)
                    ? ""
                    : (!string.IsNullOrWhiteSpace(name) && !directory.EndsWith(name, StringComparison.Ordinal)
                        ? Path.Combine(directory, name)
                        : directory);

                list.Add(new ActiveDownloadQueueItem(
                    ClientId: client.Id,
                    ClientName: client.Name,
                    Protocol: "torrent",
                    ExternalId: hash,
                    InfoHash: hash,
                    Title: name,
                    Category: cat,
                    MediaVertical: ResolveVerticalFromClientCategory(client, cat),
                    Status: mappedStatus,
                    TotalBytes: sizeBytes,
                    RemainingBytes: remBytes,
                    OutputPath: outputPath,
                    ContainedFiles: string.IsNullOrWhiteSpace(outputPath) ? [] : [outputPath],
                    IsStalled: statusFlags.Contains("stalled") && remBytes > 0,
                    IsMetadataStuck: sizeBytes == 0 && bytesDone == 0 && !isStopped,
                    ErrorMessage: isError ? (string.IsNullOrWhiteSpace(message) ? "Flood torrent error" : message) : null,
                    CanMoveFiles: isCompleted && isStopped));
            }

            return list;
        }

        if (impl == "aria2")
        {
            var list = new List<ActiveDownloadQueueItem>();
            var seenGids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var active = await Aria2CallResultAsync(http, baseUrl, client, "aria2.tellActive", [], ct);
            var waiting = await Aria2CallResultAsync(http, baseUrl, client, "aria2.tellWaiting", [0, 100], ct);
            var stopped = await Aria2CallResultAsync(http, baseUrl, client, "aria2.tellStopped", [0, 100], ct);

            foreach (var batch in new[] { active, waiting, stopped })
            {
                if (batch is not { ValueKind: JsonValueKind.Array } arr) continue;
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var gid = item.TryGetProperty("gid", out var gidEl) ? gidEl.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(gid) || !seenGids.Add(gid)) continue;

                    var rawStatus = item.TryGetProperty("status", out var stEl) ? stEl.GetString() ?? "active" : "active";
                    if (rawStatus.Equals("removed", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Skip transient magnet metadata download once aria2 has spawned the real follow-up GID
                    if (rawStatus.Equals("complete", StringComparison.OrdinalIgnoreCase) &&
                        item.TryGetProperty("followedBy", out var fbEl) &&
                        fbEl.ValueKind == JsonValueKind.Array &&
                        fbEl.GetArrayLength() > 0)
                    {
                        continue;
                    }

                    var infoHash = item.TryGetProperty("infoHash", out var ihEl) && !string.IsNullOrWhiteSpace(ihEl.GetString())
                        ? ihEl.GetString()!.ToLowerInvariant()
                        : $"aria2:{gid.ToLowerInvariant()}";

                    string? btName = null;
                    if (item.TryGetProperty("bittorrent", out var btEl) &&
                        btEl.ValueKind == JsonValueKind.Object &&
                        btEl.TryGetProperty("info", out var btInfo) &&
                        btInfo.ValueKind == JsonValueKind.Object &&
                        btInfo.TryGetProperty("name", out var btNameEl))
                    {
                        btName = btNameEl.GetString();
                    }

                    var files = new List<string>();
                    if (item.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var f in filesEl.EnumerateArray())
                        {
                            if (f.ValueKind == JsonValueKind.Object &&
                                f.TryGetProperty("path", out var pEl) &&
                                pEl.GetString() is { Length: > 0 } pVal)
                            {
                                files.Add(pVal);
                            }
                        }
                    }

                    var title = !string.IsNullOrWhiteSpace(btName)
                        ? btName!
                        : (files.Count > 0 ? Path.GetFileName(files[0]) : gid);

                    var totalBytes = ParseJsonInt64(item, "totalLength");
                    var completedBytes = ParseJsonInt64(item, "completedLength");
                    var remBytes = Math.Max(0L, totalBytes - completedBytes);
                    var dir = item.TryGetProperty("dir", out var dirEl) ? dirEl.GetString() ?? "" : "";
                    var errCode = item.TryGetProperty("errorCode", out var ecEl) ? ecEl.ToString() : "0";
                    var errMsg = item.TryGetProperty("errorMessage", out var emEl) ? emEl.GetString() : null;

                    var isError = rawStatus.Equals("error", StringComparison.OrdinalIgnoreCase) ||
                                  (!string.IsNullOrWhiteSpace(errCode) && errCode != "0" && rawStatus.Equals("stopped", StringComparison.OrdinalIgnoreCase));
                    var isComplete = rawStatus.Equals("complete", StringComparison.OrdinalIgnoreCase) ||
                                     (totalBytes > 0 && remBytes == 0 && rawStatus.Equals("active", StringComparison.OrdinalIgnoreCase));

                    var mappedStatus = isError
                        ? "error"
                        : isComplete
                            ? "Completed"
                            : rawStatus.Equals("paused", StringComparison.OrdinalIgnoreCase)
                                ? "Paused"
                                : rawStatus.Equals("waiting", StringComparison.OrdinalIgnoreCase)
                                    ? "Queued"
                                    : "Downloading";

                    var outputPath = files.Count == 1
                        ? files[0]
                        : (!string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(btName)
                            ? Path.Combine(dir, btName)
                            : dir);

                    var dirSegment = string.IsNullOrWhiteSpace(dir) ? "" : Path.GetFileName(dir.TrimEnd('/', '\\'));
                    var category = client.CategoryMappings.Values.Any(v => string.Equals(v, dirSegment, StringComparison.OrdinalIgnoreCase))
                        ? dirSegment
                        : "";

                    list.Add(new ActiveDownloadQueueItem(
                        ClientId: client.Id,
                        ClientName: client.Name,
                        Protocol: "torrent",
                        ExternalId: gid,
                        InfoHash: infoHash,
                        Title: title,
                        Category: category,
                        MediaVertical: ResolveVerticalFromClientCategory(client, category),
                        Status: mappedStatus,
                        TotalBytes: totalBytes,
                        RemainingBytes: isComplete ? 0L : remBytes,
                        OutputPath: outputPath,
                        ContainedFiles: files,
                        IsStalled: false,
                        IsMetadataStuck: title.StartsWith("[METADATA]", StringComparison.OrdinalIgnoreCase) || (totalBytes == 0 && completedBytes == 0 && rawStatus == "active"),
                        ErrorMessage: isError ? (string.IsNullOrWhiteSpace(errMsg) ? $"aria2 error {errCode}" : errMsg) : null,
                        CanMoveFiles: rawStatus.Equals("complete", StringComparison.OrdinalIgnoreCase)));
                }
            }

            return list;
        }

        if (impl == "sabnzbd")
        {
            var apiKey = Uri.EscapeDataString(client.PlainApiKey ?? "");
            var list = new List<ActiveDownloadQueueItem>();
            var seenNzoIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var queueUri = $"{baseUrl}/api?mode=queue&output=json&apikey={apiKey}";
            var doc = await http.GetFromJsonAsync<JsonElement>(queueUri, ct);
            if (doc.TryGetProperty("queue", out var qObj) && qObj.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in slots.EnumerateArray())
                {
                    var nzoId = s.TryGetProperty("nzo_id", out var idEl) ? idEl.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(nzoId) || !seenNzoIds.Add(nzoId)) continue;
                    var filename = s.TryGetProperty("filename", out var fnEl) ? fnEl.GetString() ?? "" : "";
                    var cat = s.TryGetProperty("cat", out var cEl) ? cEl.GetString() ?? "" : "";
                    var status = s.TryGetProperty("status", out var stEl) ? stEl.GetString() ?? "" : "";
                    var totalMb = ParseSabMb(s, "mb");
                    var leftMb = ParseSabMb(s, "mbleft");
                    var totalBytes = (long)(totalMb * 1024 * 1024);
                    var remBytes = (long)(leftMb * 1024 * 1024);
                    var missing = s.TryGetProperty("missing", out var mEl) && int_TryGetInt32(mEl, out var missCnt) ? missCnt : 0;

                    list.Add(new ActiveDownloadQueueItem(
                        ClientId: client.Id,
                        ClientName: client.Name,
                        Protocol: "usenet",
                        ExternalId: nzoId,
                        InfoHash: $"nzo:{nzoId.ToLowerInvariant()}",
                        Title: filename,
                        Category: cat,
                        MediaVertical: ResolveVerticalFromClientCategory(client, cat),
                        Status: status,
                        TotalBytes: totalBytes,
                        RemainingBytes: remBytes,
                        OutputPath: "",
                        ContainedFiles: [],
                        IsStalled: status.Equals("Paused", StringComparison.OrdinalIgnoreCase) && missing > 0,
                        IsMetadataStuck: false,
                        ErrorMessage: missing > 50 ? $"Missing {missing} Usenet articles" : null,
                        CanMoveFiles: false));
                }
            }

            var histUri = $"{baseUrl}/api?mode=history&limit=50&output=json&apikey={apiKey}";
            var histDoc = await http.GetFromJsonAsync<JsonElement>(histUri, ct);
            if (histDoc.TryGetProperty("history", out var hObj) && hObj.TryGetProperty("slots", out var hSlots) && hSlots.ValueKind == JsonValueKind.Array)
            {
                foreach (var hs in hSlots.EnumerateArray())
                {
                    var nzoId = hs.TryGetProperty("nzo_id", out var idEl) ? idEl.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(nzoId) || !seenNzoIds.Add(nzoId)) continue;
                    var name = hs.TryGetProperty("name", out var nmEl) ? nmEl.GetString() ?? "" : "";
                    var cat = hs.TryGetProperty("category", out var cEl) ? cEl.GetString() ?? "" : "";
                    var status = hs.TryGetProperty("status", out var stEl) ? stEl.GetString() ?? "" : "";
                    var bytes = hs.TryGetProperty("bytes", out var bEl) && bEl.TryGetInt64(out var bv) ? bv : 0L;
                    var storage = hs.TryGetProperty("storage", out var stgEl) ? stgEl.GetString() ?? "" : "";
                    var failMsg = hs.TryGetProperty("fail_message", out var fmEl) ? fmEl.GetString() : null;
                    var isFailed = status.Equals("Failed", StringComparison.OrdinalIgnoreCase);

                    list.Add(new ActiveDownloadQueueItem(
                        ClientId: client.Id,
                        ClientName: client.Name,
                        Protocol: "usenet",
                        ExternalId: nzoId,
                        InfoHash: $"nzo:{nzoId.ToLowerInvariant()}",
                        Title: name,
                        Category: cat,
                        MediaVertical: ResolveVerticalFromClientCategory(client, cat),
                        Status: status,
                        TotalBytes: bytes,
                        RemainingBytes: 0,
                        OutputPath: storage,
                        ContainedFiles: [],
                        IsStalled: false,
                        IsMetadataStuck: false,
                        ErrorMessage: isFailed ? (string.IsNullOrWhiteSpace(failMsg) ? "SABnzbd job failed" : failMsg) : null,
                        CanMoveFiles: status.Equals("Completed", StringComparison.OrdinalIgnoreCase)));
                }
            }

            return list;
        }

        if (impl == "nzbget")
        {
            ApplyBasicAuth(http, client.Username, client.PlainPassword);
            var list = new List<ActiveDownloadQueueItem>();

            using var groupsResp = await http.PostAsJsonAsync($"{baseUrl}/jsonrpc", new { method = "listgroups", @params = Array.Empty<object>() }, ct);
            if (groupsResp.IsSuccessStatusCode)
            {
                var gJson = await groupsResp.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (gJson.TryGetProperty("result", out var groups) && groups.ValueKind == JsonValueKind.Array)
                {
                    foreach (var g in groups.EnumerateArray())
                    {
                        var nzbId = g.TryGetProperty("NZBID", out var idEl) ? idEl.ToString() : "";
                        var name = g.TryGetProperty("NZBName", out var nEl) ? nEl.GetString() ?? "" : "";
                        var cat = g.TryGetProperty("Category", out var cEl) ? cEl.GetString() ?? "" : "";
                        var status = g.TryGetProperty("Status", out var stEl) ? stEl.GetString() ?? "DOWNLOADING" : "DOWNLOADING";
                        var destDir = g.TryGetProperty("DestDir", out var dEl) ? dEl.GetString() ?? "" : "";
                        var fileSizeMb = g.TryGetProperty("FileSizeMB", out var fsEl) && fsEl.TryGetInt64(out var fsm) ? fsm : 0L;
                        var remSizeMb = g.TryGetProperty("RemainingSizeMB", out var rsEl) && rsEl.TryGetInt64(out var rsm) ? rsm : 0L;

                        list.Add(new ActiveDownloadQueueItem(
                            ClientId: client.Id,
                            ClientName: client.Name,
                            Protocol: "usenet",
                            ExternalId: nzbId,
                            InfoHash: $"nzbget:{nzbId}",
                            Title: name,
                            Category: cat,
                            MediaVertical: ResolveVerticalFromClientCategory(client, cat),
                            Status: status,
                            TotalBytes: fileSizeMb * 1024L * 1024L,
                            RemainingBytes: remSizeMb * 1024L * 1024L,
                            OutputPath: destDir,
                            ContainedFiles: [],
                            IsStalled: false,
                            IsMetadataStuck: false,
                            ErrorMessage: null,
                            CanMoveFiles: false));
                    }
                }
            }

            using var histResp = await http.PostAsJsonAsync($"{baseUrl}/jsonrpc", new { method = "history", @params = new object[] { false } }, ct);
            if (histResp.IsSuccessStatusCode)
            {
                var hJson = await histResp.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (hJson.TryGetProperty("result", out var hist) && hist.ValueKind == JsonValueKind.Array)
                {
                    foreach (var h in hist.EnumerateArray().Take(50))
                    {
                        var status = h.TryGetProperty("Status", out var stEl) ? stEl.GetString() ?? "" : "";
                        var isSuccess = status.StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase);
                        var isFailure = status.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase) ||
                                        status.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase) && status.Contains("BAD", StringComparison.OrdinalIgnoreCase);
                        if (!isSuccess && !isFailure)
                        {
                            continue;
                        }

                        var nzbId = h.TryGetProperty("NZBID", out var idEl) ? idEl.ToString() : "";
                        var name = h.TryGetProperty("Name", out var nEl) ? nEl.GetString() ?? "" : "";
                        var cat = h.TryGetProperty("Category", out var cEl) ? cEl.GetString() ?? "" : "";
                        var destDir = h.TryGetProperty("FinalDir", out var fdEl) && !string.IsNullOrWhiteSpace(fdEl.GetString())
                            ? fdEl.GetString()!
                            : (h.TryGetProperty("DestDir", out var ddEl) ? ddEl.GetString() ?? "" : "");
                        var fileSizeMb = h.TryGetProperty("FileSizeMB", out var fsEl) && fsEl.TryGetInt64(out var fsm) ? fsm : 0L;

                        list.Add(new ActiveDownloadQueueItem(
                            ClientId: client.Id,
                            ClientName: client.Name,
                            Protocol: "usenet",
                            ExternalId: nzbId,
                            InfoHash: $"nzbget:{nzbId}",
                            Title: name,
                            Category: cat,
                            MediaVertical: ResolveVerticalFromClientCategory(client, cat),
                            Status: isFailure ? "Failed" : "Completed",
                            TotalBytes: fileSizeMb * 1024L * 1024L,
                            RemainingBytes: 0,
                            OutputPath: destDir,
                            ContainedFiles: [],
                            IsStalled: false,
                            IsMetadataStuck: false,
                            ErrorMessage: isFailure ? $"NZBGet status: {status}" : null,
                            CanMoveFiles: isSuccess));
                    }
                }
            }

            return list;
        }

        if (impl is "transmission" or "vuze")
        {
            ApplyBasicAuth(http, client.Username, client.PlainPassword);
            var rpcResp = await SendTransmissionRpcAsync(http, $"{baseUrl}/transmission/rpc", new
            {
                method = "torrent-get",
                arguments = new
                {
                    fields = new[]
                    {
                        "id", "hashString", "name", "status", "totalSize", "leftUntilDone",
                        "downloadDir", "error", "errorString", "isStalled", "labels",
                    },
                },
            }, ct);

            var list = new List<ActiveDownloadQueueItem>();
            if (rpcResp.TryGetProperty("arguments", out var args) &&
                args.TryGetProperty("torrents", out var torrents) &&
                torrents.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in torrents.EnumerateArray())
                {
                    var hash = t.TryGetProperty("hashString", out var hEl) ? (hEl.GetString() ?? "").ToLowerInvariant() : "";
                    var name = t.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                    var errCode = t.TryGetProperty("error", out var eEl) && eEl.TryGetInt32(out var ec) ? ec : 0;
                    var errStr = t.TryGetProperty("errorString", out var esEl) ? esEl.GetString() : null;
                    var trStatus = t.TryGetProperty("status", out var stEl) && stEl.TryGetInt32(out var sc) ? sc : 4;
                    var total = t.TryGetProperty("totalSize", out var tsEl) && tsEl.TryGetInt64(out var tb) ? tb : 0L;
                    var left = t.TryGetProperty("leftUntilDone", out var lEl) && lEl.TryGetInt64(out var lb) ? lb : 0L;
                    var dlDir = t.TryGetProperty("downloadDir", out var dEl) ? dEl.GetString() ?? "" : "";
                    var stalled = t.TryGetProperty("isStalled", out var isEl) && isEl.ValueKind == JsonValueKind.True;
                    var cat = "";
                    if (t.TryGetProperty("labels", out var lblEl) && lblEl.ValueKind == JsonValueKind.Array && lblEl.GetArrayLength() > 0)
                    {
                        cat = lblEl[0].GetString() ?? "";
                    }

                    // Transmission error >= 2 means tracker/local error; status 6 = seeding, left == 0 && total > 0 = completed
                    var mappedStatus = errCode >= 2 && !string.IsNullOrWhiteSpace(errStr)
                        ? "error"
                        : (left == 0 && total > 0 && trStatus is 0 or 5 or 6)
                            ? "Completed"
                            : "Downloading";

                    list.Add(new ActiveDownloadQueueItem(
                        ClientId: client.Id,
                        ClientName: client.Name,
                        Protocol: "torrent",
                        ExternalId: hash,
                        InfoHash: hash,
                        Title: name,
                        Category: cat,
                        MediaVertical: ResolveVerticalFromClientCategory(client, cat),
                        Status: mappedStatus,
                        TotalBytes: total,
                        RemainingBytes: left,
                        OutputPath: string.IsNullOrWhiteSpace(dlDir) ? "" : Path.Combine(dlDir, name),
                        ContainedFiles: [],
                        IsStalled: stalled && left > 0,
                        IsMetadataStuck: total == 0 && left == 0 && trStatus == 4,
                        ErrorMessage: errCode > 0 && !string.IsNullOrWhiteSpace(errStr) ? errStr : null,
                        CanMoveFiles: false));
                }
            }

            return list;
        }

        throw new DownloadClientActionNotSupportedException(client.Name, impl, "polling the download queue");
    }

}
