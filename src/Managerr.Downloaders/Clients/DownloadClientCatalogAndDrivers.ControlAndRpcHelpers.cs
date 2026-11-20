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

public sealed partial class DownloadClientDriverGateway
{
    public async Task RemoveDownloadAsync(
        DownloadClientConnectionSpec client,
        string externalId,
        bool deleteData,
        CancellationToken ct = default)
    {
        var impl = client.Implementation.ToLowerInvariant();
        if (impl is "torrent_blackhole" or "usenet_blackhole" or "pneumatic")
        {
            return;
        }

        using var http = httpFactory.CreateHomelabClient(allowPrivateLan: true, ignoreSslErrors: true);
        var baseUrl = BuildBaseUrl(client);

        switch (impl)
        {
            case "qbittorrent":
            {
                await LoginToQbittorrentAsync(http, client, baseUrl, ct);
                using var resp = await http.PostAsync(
                    $"{baseUrl}/api/v2/torrents/delete",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["hashes"] = externalId,
                        ["deleteFiles"] = deleteData ? "true" : "false",
                    }),
                    ct);
                resp.EnsureSuccessStatusCode();
                return;
            }

            case "deluge":
            {
                await LoginToDelugeAsync(http, client, baseUrl, ct);
                await DelugeRpcAsync(http, baseUrl, client.Name, "core.remove_torrent", [externalId.ToLowerInvariant(), deleteData], ct);
                return;
            }

            case "rtorrent":
            {
                await SendRtorrentXmlRpcAsync(http, client, "d.erase", [externalId], ct);
                return;
            }

            case "flood":
            {
                await LoginToFloodAsync(http, client, baseUrl, ct);
                using var resp = await http.PostAsJsonAsync($"{baseUrl}/api/torrents/delete", new
                {
                    hashes = new[] { externalId },
                    deleteData,
                }, ct);
                resp.EnsureSuccessStatusCode();
                return;
            }

            case "sabnzbd":
            {
                var apiKey = Uri.EscapeDataString(client.PlainApiKey ?? "");
                var escId = Uri.EscapeDataString(externalId);
                var delFlag = deleteData ? 1 : 0;
                await http.GetStringAsync($"{baseUrl}/api?mode=queue&name=delete&value={escId}&del_files={delFlag}&output=json&apikey={apiKey}", ct);
                await http.GetStringAsync($"{baseUrl}/api?mode=history&name=delete&value={escId}&del_files={delFlag}&output=json&apikey={apiKey}", ct);
                return;
            }

            case "nzbget":
            {
                // The id is in the queue or, once finished, in the history: GroupFinalDelete removes a queued job with
                // its partial files and without a history entry; HistoryFinalDelete removes a finished one (#68)
                ApplyBasicAuth(http, client.Username, client.PlainPassword);
                var id = ParseNzbgetId(externalId);
                if (!await NzbgetEditQueueAsync(http, baseUrl, "GroupFinalDelete", "", id, ct))
                {
                    await NzbgetEditQueueAsync(http, baseUrl, deleteData ? "HistoryFinalDelete" : "HistoryDelete", "", id, ct);
                }

                return;
            }

            case "transmission" or "vuze":
            {
                ApplyBasicAuth(http, client.Username, client.PlainPassword);
                await SendTransmissionCommandAsync(http, $"{baseUrl}/transmission/rpc", client.Name, new
                {
                    method = "torrent-remove",
                    arguments = new Dictionary<string, object>
                    {
                        ["ids"] = new[] { externalId },
                        ["delete-local-data"] = deleteData,
                    },
                }, ct);
                return;
            }

            case "aria2":
            {
                // forceRemove only works on active/waiting downloads; removeDownloadResult clears finished ones.
                // aria2 has no way to delete the downloaded files itself.
                var removed = await Aria2CallAsync(http, baseUrl, client, "aria2.forceRemove", [externalId], ct);
                if (!removed)
                {
                    await Aria2CallAsync(http, baseUrl, client, "aria2.removeDownloadResult", [externalId], ct);
                }

                return;
            }

            default:
                throw new DownloadClientActionNotSupportedException(client.Name, impl, "removing downloads");
        }
    }

    public async Task ControlDownloadAsync(
        DownloadClientConnectionSpec client,
        string externalId,
        string action,
        CancellationToken ct = default)
    {
        var impl = client.Implementation.ToLowerInvariant();
        if (impl is "torrent_blackhole" or "usenet_blackhole" or "pneumatic")
        {
            return;
        }

        var normAction = action.Trim().ToLowerInvariant();
        if (normAction is not ("pause" or "resume" or "force_start" or "top_priority"))
        {
            throw new ArgumentException($"Unsupported queue control action '{action}'.");
        }

        using var http = httpFactory.CreateHomelabClient(allowPrivateLan: true, ignoreSslErrors: true);
        var baseUrl = BuildBaseUrl(client);

        switch (impl)
        {
            case "qbittorrent":
            {
                await LoginToQbittorrentAsync(http, client, baseUrl, ct);

                // qBittorrent 5 (WebAPI 2.11) renamed pause/resume to stop/start and removed the old endpoints (#69)
                var stopStart = normAction is "pause" or "resume" && await QbittorrentUsesStopStartAsync(http, baseUrl, ct);
                var endpoint = normAction switch
                {
                    "pause" => stopStart ? "/api/v2/torrents/stop" : "/api/v2/torrents/pause",
                    "resume" => stopStart ? "/api/v2/torrents/start" : "/api/v2/torrents/resume",
                    "force_start" => "/api/v2/torrents/setForceStart",
                    _ => "/api/v2/torrents/topPrio",
                };

                var form = new Dictionary<string, string> { ["hashes"] = externalId };
                if (normAction == "force_start")
                {
                    form["value"] = "true";
                }

                using var resp = await http.PostAsync($"{baseUrl}{endpoint}", new FormUrlEncodedContent(form), ct);
                resp.EnsureSuccessStatusCode();
                return;
            }

            case "deluge":
            {
                await LoginToDelugeAsync(http, client, baseUrl, ct);
                var hash = externalId.ToLowerInvariant();
                switch (normAction)
                {
                    case "pause":
                        await DelugeRpcAsync(http, baseUrl, client.Name, "core.pause_torrent", [hash], ct);
                        break;
                    case "resume" or "force_start":
                        await DelugeRpcAsync(http, baseUrl, client.Name, "core.resume_torrent", [hash], ct);
                        break;
                    default:
                        await DelugeRpcAsync(http, baseUrl, client.Name, "core.queue_top", [new[] { hash }], ct);
                        break;
                }
                return;
            }

            case "rtorrent":
            {
                switch (normAction)
                {
                    case "pause":
                        await SendRtorrentXmlRpcAsync(http, client, "d.pause", [externalId], ct);
                        break;
                    case "resume" or "force_start":
                        await SendRtorrentXmlRpcAsync(http, client, "d.resume", [externalId], ct);
                        break;
                    default:
                        await SendRtorrentXmlRpcAsync(http, client, "d.priority.set", [externalId, 3], ct);
                        break;
                }
                return;
            }

            case "flood":
            {
                await LoginToFloodAsync(http, client, baseUrl, ct);
                if (normAction == "top_priority")
                {
                    using var prioResp = await http.PatchAsJsonAsync($"{baseUrl}/api/torrents/priority", new
                    {
                        hashes = new[] { externalId },
                        priority = 3,
                    }, ct);
                    prioResp.EnsureSuccessStatusCode();
                    return;
                }

                var endpoint = normAction == "pause" ? "/api/torrents/stop" : "/api/torrents/start";
                using var resp = await http.PostAsJsonAsync($"{baseUrl}{endpoint}", new
                {
                    hashes = new[] { externalId },
                }, ct);
                resp.EnsureSuccessStatusCode();
                return;
            }

            case "sabnzbd":
            {
                var apiKey = Uri.EscapeDataString(client.PlainApiKey ?? "");
                var escId = Uri.EscapeDataString(externalId);
                var sabQuery = normAction switch
                {
                    "pause" => $"mode=queue&name=pause&value={escId}",
                    "resume" => $"mode=queue&name=resume&value={escId}",
                    _ => $"mode=queue&name=priority&value={escId}&value2=2",
                };

                await http.GetStringAsync($"{baseUrl}/api?{sabQuery}&output=json&apikey={apiKey}", ct);
                return;
            }

            case "nzbget":
            {
                ApplyBasicAuth(http, client.Username, client.PlainPassword);
                var id = ParseNzbgetId(externalId);
                var (command, param) = normAction switch
                {
                    "pause" => ("GroupPause", ""),
                    "resume" => ("GroupResume", ""),
                    "force_start" => ("GroupSetPriority", "900"),
                    _ => ("GroupMoveTop", ""),
                };
                if (!await NzbgetEditQueueAsync(http, baseUrl, command, param, id, ct))
                {
                    throw new InvalidOperationException($"NZBGet '{client.Name}' rejected '{command}' for download {externalId}.");
                }

                return;
            }

            case "transmission" or "vuze":
            {
                ApplyBasicAuth(http, client.Username, client.PlainPassword);
                var method = normAction switch
                {
                    "pause" => "torrent-stop",
                    "resume" => "torrent-start",
                    "force_start" => "torrent-start-now",
                    _ => "queue-move-top",
                };
                await SendTransmissionCommandAsync(http, $"{baseUrl}/transmission/rpc", client.Name, new
                {
                    method,
                    arguments = new { ids = new[] { externalId } },
                }, ct);
                return;
            }

            case "aria2":
            {
                var ok = normAction switch
                {
                    "pause" => await Aria2CallAsync(http, baseUrl, client, "aria2.pause", [externalId], ct),
                    "resume" or "force_start" => await Aria2CallAsync(http, baseUrl, client, "aria2.unpause", [externalId], ct),
                    _ => await Aria2CallAsync(http, baseUrl, client, "aria2.changePosition", [externalId, 0, "POS_SET"], ct),
                };
                if (!ok)
                {
                    throw new InvalidOperationException($"aria2 '{client.Name}' rejected '{normAction}' for download {externalId}.");
                }

                return;
            }

            default:
                throw new DownloadClientActionNotSupportedException(client.Name, impl, $"the '{normAction}' queue action");
        }
    }

    public async Task<DownloadClientTestResult> TestConnectionAsync(
        DownloadClientConnectionSpec client,
        CancellationToken ct = default)
    {
        var impl = client.Implementation.ToLowerInvariant();
        if (impl is "torrent_blackhole" or "usenet_blackhole" or "pneumatic")
        {
            var watchFolder = client.ExtraSettings.GetValueOrDefault("watchFolder")
                ?? Path.Combine(Path.GetTempPath(), "managerr-blackhole", impl);
            Directory.CreateDirectory(watchFolder);
            return new DownloadClientTestResult(true, "filesystem", $"Watch folder '{watchFolder}' is accessible.");
        }

        using var http = httpFactory.CreateHomelabClient(allowPrivateLan: true, ignoreSslErrors: true);
        var baseUrl = BuildBaseUrl(client);

        switch (impl)
        {
            case "qbittorrent":
            {
                await LoginToQbittorrentAsync(http, client, baseUrl, ct);
                var version = (await http.GetStringAsync($"{baseUrl}/api/v2/app/version", ct)).Trim();
                return new DownloadClientTestResult(true, version, $"Connected to qBittorrent {version}.");
            }

            case "deluge":
            {
                await LoginToDelugeAsync(http, client, baseUrl, ct);
                var info = await DelugeRpcAsync(http, baseUrl, client.Name, "daemon.info", [], ct);
                var ver = info.ValueKind == JsonValueKind.String ? info.GetString() : "2.x";
                return new DownloadClientTestResult(true, ver, $"Connected to Deluge {ver}.");
            }

            case "rtorrent":
            {
                var valEl = await SendRtorrentXmlRpcAsync(http, client, "system.client_version", [], ct);
                var ver = ExtractXmlRpcScalarString(valEl);
                return new DownloadClientTestResult(true, ver, $"Connected to rTorrent {ver}.");
            }

            case "flood":
            {
                await LoginToFloodAsync(http, client, baseUrl, ct);
                using var resp = await http.GetAsync($"{baseUrl}/api/client/connection-test", ct);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    using var verifyResp = await http.GetAsync($"{baseUrl}/api/auth/verify", ct);
                    verifyResp.EnsureSuccessStatusCode();
                    return new DownloadClientTestResult(true, "flood", "Connected to Flood.");
                }

                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
                var connected = !json.TryGetProperty("isConnected", out var icEl) || icEl.ValueKind == JsonValueKind.True;
                return new DownloadClientTestResult(connected, "flood", connected ? "Connected to Flood backend." : "Flood backend is disconnected.");
            }

            case "transmission" or "vuze":
            {
                ApplyBasicAuth(http, client.Username, client.PlainPassword);
                var rpcResp = await SendTransmissionRpcAsync(http, $"{baseUrl}/transmission/rpc", new
                {
                    method = "session-get",
                }, ct);
                var ver = rpcResp.TryGetProperty("arguments", out var args) && args.TryGetProperty("version", out var vEl)
                    ? vEl.GetString()
                    : "rpc";
                return new DownloadClientTestResult(true, ver, $"Connected to {client.DisplayNameOrImpl()} {ver}.");
            }

            case "aria2":
            {
                var res = await Aria2CallResultAsync(http, baseUrl, client, "aria2.getVersion", [], ct);
                var ver = res is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty("version", out var vEl)
                    ? vEl.GetString()
                    : null;
                if (ver is null)
                {
                    throw new InvalidOperationException($"aria2 '{client.Name}' failed version check.");
                }

                return new DownloadClientTestResult(true, ver, $"Connected to aria2 {ver}.");
            }

            case "sabnzbd":
            {
                var apiKey = Uri.EscapeDataString(client.PlainApiKey ?? "");
                var doc = await http.GetFromJsonAsync<JsonElement>($"{baseUrl}/api?mode=version&output=json&apikey={apiKey}", ct);
                var ver = doc.TryGetProperty("version", out var vEl) ? vEl.GetString() : "sabnzbd";
                return new DownloadClientTestResult(true, ver, $"Connected to SABnzbd {ver}.");
            }

            case "nzbget":
            {
                ApplyBasicAuth(http, client.Username, client.PlainPassword);
                using var resp = await http.PostAsJsonAsync($"{baseUrl}/jsonrpc", new { method = "version", @params = Array.Empty<object>() }, ct);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
                var ver = json.TryGetProperty("result", out var rEl) ? rEl.GetString() : "nzbget";
                return new DownloadClientTestResult(true, ver, $"Connected to NZBGet {ver}.");
            }

            default:
                throw new DownloadClientActionNotSupportedException(client.Name, impl, "testing connections");
        }
    }

    private static async Task<bool> QbittorrentUsesStopStartAsync(HttpClient http, string baseUrl, CancellationToken ct)
    {
        try
        {
            var raw = (await http.GetStringAsync($"{baseUrl}/api/v2/app/webapiVersion", ct)).Trim();
            return Version.TryParse(raw, out var v) && v >= new Version(2, 11);
        }
        catch (HttpRequestException)
        {
            // Very old WebUIs have no version endpoint; they only know pause/resume
            return false;
        }
    }

    // Queue items carry NZBGet's numeric NZBID; older grabs may carry the "nzbget:" prefix from InfoHash
    private static int ParseNzbgetId(string externalId)
    {
        var raw = externalId.StartsWith("nzbget:", StringComparison.OrdinalIgnoreCase) ? externalId["nzbget:".Length..] : externalId;
        return int.TryParse(raw, out var id)
            ? id
            : throw new ArgumentException($"'{externalId}' is not an NZBGet download id.");
    }

    /// <summary>NZBGet JSON-RPC editqueue(Command, Param, IDs); returns NZBGet's success flag.</summary>
    private static async Task<bool> NzbgetEditQueueAsync(HttpClient http, string baseUrl, string command, string param, int id, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync($"{baseUrl}/jsonrpc", new
        {
            method = "editqueue",
            @params = new object[] { command, param, new[] { id } },
        }, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.True;
    }

    /// <summary>aria2 JSON-RPC call with the optional secret token; false when aria2 answers with an error.</summary>
    private static async Task<bool> Aria2CallAsync(HttpClient http, string baseUrl, DownloadClientConnectionSpec client, string method, object[] args, CancellationToken ct)
    {
        var result = await Aria2CallResultAsync(http, baseUrl, client, method, args, ct);
        return result.HasValue;
    }

    private static async Task<JsonElement?> Aria2CallResultAsync(
        HttpClient http,
        string baseUrl,
        DownloadClientConnectionSpec client,
        string method,
        object[] args,
        CancellationToken ct)
    {
        var rpcParams = new List<object>();
        if (!string.IsNullOrWhiteSpace(client.PlainApiKey))
        {
            rpcParams.Add($"token:{client.PlainApiKey}");
        }

        rpcParams.AddRange(args);
        using var resp = await http.PostAsJsonAsync($"{baseUrl}/jsonrpc", new
        {
            jsonrpc = "2.0",
            id = "managerr",
            method,
            @params = rpcParams,
        }, ct);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (resp.IsSuccessStatusCode && json.TryGetProperty("result", out var res) && !json.TryGetProperty("error", out _))
        {
            return res;
        }

        return null;
    }

    private static async Task LoginToDelugeAsync(HttpClient http, DownloadClientConnectionSpec client, string baseUrl, CancellationToken ct)
    {
        var password = client.PlainPassword ?? client.PlainApiKey ?? "";
        var res = await DelugeRpcAsync(http, baseUrl, client.Name, "auth.login", [password], ct);
        if (res.ValueKind == JsonValueKind.False)
        {
            throw new HttpRequestException(
                $"Deluge '{client.Name}' rejected the WebUI password.",
                null,
                HttpStatusCode.Unauthorized);
        }
    }

    private static async Task TrySetDelugeLabelAsync(
        HttpClient http,
        string baseUrl,
        string clientName,
        string torrentHash,
        string label,
        CancellationToken ct)
    {
        var normLabel = label.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normLabel))
        {
            return;
        }

        try
        {
            await DelugeRpcAsync(http, baseUrl, clientName, "label.set_torrent", [torrentHash, normLabel], ct);
        }
        catch (Exception)
        {
            // Label plugin is optional in Deluge; ignore if not enabled or label does not exist
        }
    }

    private static async Task<JsonElement> DelugeRpcAsync(
        HttpClient http,
        string baseUrl,
        string clientName,
        string method,
        object[] parameters,
        CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync($"{baseUrl}/json", new
        {
            id = 1,
            method,
            @params = parameters,
        }, ct);
        resp.EnsureSuccessStatusCode();

        if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            AttachCookiesHeader(http, setCookies);
        }

        var rawBytes = await resp.Content.ReadAsByteArrayAsync(ct);
        string jsonText;
        if (rawBytes.Length >= 2 && rawBytes[0] == 0x1f && rawBytes[1] == 0x8b)
        {
            using var ms = new MemoryStream(rawBytes);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var sr = new StreamReader(gz, Encoding.UTF8);
            jsonText = await sr.ReadToEndAsync(ct);
        }
        else
        {
            jsonText = Encoding.UTF8.GetString(rawBytes);
        }

        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement.Clone();
        if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
        {
            var errMsg = errEl.TryGetProperty("message", out var mEl) ? mEl.GetString() : errEl.ToString();
            throw new InvalidOperationException($"Deluge '{clientName}' rejected '{method}': {errMsg}");
        }

        return root.TryGetProperty("result", out var resEl) ? resEl : default;
    }

    private static async Task LoginToFloodAsync(HttpClient http, DownloadClientConnectionSpec client, string baseUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(client.Username))
        {
            return;
        }

        using var resp = await http.PostAsJsonAsync($"{baseUrl}/api/auth/authenticate", new
        {
            username = client.Username,
            password = client.PlainPassword ?? "",
        }, ct);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity)
        {
            throw new HttpRequestException(
                $"Flood '{client.Name}' rejected the username or password.",
                null,
                HttpStatusCode.Unauthorized);
        }

        resp.EnsureSuccessStatusCode();
        if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            AttachCookiesHeader(http, setCookies);
        }
    }

    private static void AttachCookiesHeader(HttpClient http, IEnumerable<string> setCookieHeaders)
    {
        var pairs = new List<string>();
        foreach (var header in setCookieHeaders)
        {
            var firstPart = header.Split(';', 2)[0].Trim();
            if (!string.IsNullOrWhiteSpace(firstPart))
            {
                pairs.Add(firstPart);
            }
        }

        if (pairs.Count > 0)
        {
            http.DefaultRequestHeaders.Remove("Cookie");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", string.Join("; ", pairs));
        }
    }

    private static async Task<XElement> SendRtorrentXmlRpcAsync(
        HttpClient http,
        DownloadClientConnectionSpec client,
        string methodName,
        IReadOnlyList<object> parameters,
        CancellationToken ct)
    {
        ApplyBasicAuth(http, client.Username, client.PlainPassword);
        var baseUrl = BuildBaseUrl(client);
        var rpcUrl = string.IsNullOrWhiteSpace(client.UrlBase) ? $"{baseUrl}/RPC2" : baseUrl;

        var callDoc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("methodCall",
                new XElement("methodName", methodName),
                new XElement("params",
                    parameters.Select(p => new XElement("param", BuildXmlRpcValueElement(p))))));

        using var content = new StringContent(callDoc.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml");
        using var resp = await http.PostAsync(rpcUrl, content, ct);
        resp.EnsureSuccessStatusCode();

        var respXml = await resp.Content.ReadAsStringAsync(ct);
        var doc = XDocument.Parse(respXml);
        var faultEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "fault");
        if (faultEl is not null)
        {
            var members = faultEl.Descendants().Where(e => e.Name.LocalName == "member").ToList();
            var faultString = members
                .FirstOrDefault(m => m.Elements().FirstOrDefault(x => x.Name.LocalName == "name")?.Value == "faultString")
                ?.Elements().FirstOrDefault(x => x.Name.LocalName == "value")?.Value ?? "XML-RPC fault";
            throw new InvalidOperationException($"rTorrent '{client.Name}' XML-RPC '{methodName}' failed: {faultString}");
        }

        return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "value") ?? new XElement("value");
    }

    private static XElement BuildXmlRpcValueElement(object value) =>
        value switch
        {
            byte[] bytes => new XElement("value", new XElement("base64", Convert.ToBase64String(bytes))),
            int i => new XElement("value", new XElement("i4", i)),
            long l => new XElement("value", new XElement("i8", l)),
            bool b => new XElement("value", new XElement("boolean", b ? "1" : "0")),
            IEnumerable<string> arr => new XElement("value",
                new XElement("array",
                    new XElement("data", arr.Select(s => new XElement("value", new XElement("string", s)))))),
            _ => new XElement("value", new XElement("string", value.ToString() ?? "")),
        };

    private static string ExtractXmlRpcScalarString(XElement valueEl)
    {
        var child = valueEl.Elements().FirstOrDefault();
        return child is not null ? child.Value : valueEl.Value;
    }

    /// <summary>
    /// Starts a qBittorrent WebUI session on <paramref name="http"/>. The SID cookie stays in that client's cookie
    /// container, so every call made on it afterwards is authenticated. Without a configured username nothing is sent,
    /// for WebUIs that bypass authentication for whitelisted subnets. qBittorrent answers bad credentials with
    /// 200 "Fails." and a banned IP with 403, so both are turned into a clear error here (#63).
    /// </summary>
    private static async Task LoginToQbittorrentAsync(HttpClient http, DownloadClientConnectionSpec client, string baseUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(client.Username))
        {
            return;
        }

        using var resp = await http.PostAsync(
            $"{baseUrl}/api/v2/auth/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = client.Username,
                ["password"] = client.PlainPassword ?? "",
            }),
            ct);

        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new HttpRequestException(
                $"qBittorrent '{client.Name}' refused the login (403): too many failed attempts have banned this IP; check the WebUI's ban settings.",
                null,
                HttpStatusCode.Forbidden);
        }

        resp.EnsureSuccessStatusCode();
        var body = (await resp.Content.ReadAsStringAsync(ct)).Trim();
        if (body.Equals("Fails.", StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException(
                $"qBittorrent '{client.Name}' rejected the username or password.",
                null,
                HttpStatusCode.Unauthorized);
        }
    }

    private static long ParseJsonInt64(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el)) return 0L;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var s)) return s;
        return 0L;
    }

    private static double ParseSabMb(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el)) return 0;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var s)) return s;
        return 0;
    }

    private static bool int_TryGetInt32(JsonElement el, out int val)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out val)) return true;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out val)) return true;
        val = 0;
        return false;
    }

    private static string? TryExtractBtihFromMagnet(string? urlOrMagnet)
    {
        if (string.IsNullOrWhiteSpace(urlOrMagnet))
        {
            return null;
        }

        var marker = "urn:btih:";
        var idx = urlOrMagnet.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var start = idx + marker.Length;
        var end = urlOrMagnet.IndexOfAny(['&', '/', '?', '#', ' '], start);
        var token = (end < 0 ? urlOrMagnet[start..] : urlOrMagnet[start..end]).Trim();
        return token.Length >= 16 ? token.ToLowerInvariant() : null;
    }

    private static bool TryDecodeInlineBase64Payload(string? input, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        const string dataMarker = ";base64,";
        if (input.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var idx = input.IndexOf(dataMarker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                return TryFromBase64(input[(idx + dataMarker.Length)..], out bytes);
            }
        }
        else if (input.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            return TryFromBase64(input["base64:".Length..], out bytes);
        }

        return false;
    }

    private static bool TryFromBase64(string b64, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(b64.Trim());
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static string? TryExtractBtihFromTorrentBytes(byte[] bytes)
    {
        // Locate "4:infod" in bencoded .torrent payload and hash the bencoded info dictionary when present
        ReadOnlySpan<byte> marker = "4:infod"u8;
        var span = bytes.AsSpan();
        var idx = span.IndexOf(marker);
        if (idx < 0)
        {
            return null;
        }

        var infoStart = idx + 6; // points at 'd' of info dictionary
        var pos = infoStart;
        if (!TrySkipBencodeValue(span, ref pos) || pos <= infoStart)
        {
            return null;
        }

        return Convert.ToHexStringLower(SHA1.HashData(span[infoStart..pos]));
    }

    private static bool TrySkipBencodeValue(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos >= data.Length) return false;
        var b = data[pos];
        if (b == (byte)'i')
        {
            pos++;
            while (pos < data.Length && data[pos] != (byte)'e') pos++;
            if (pos >= data.Length) return false;
            pos++;
            return true;
        }

        if (b is (byte)'l' or (byte)'d')
        {
            pos++;
            while (pos < data.Length && data[pos] != (byte)'e')
            {
                if (!TrySkipBencodeValue(data, ref pos)) return false;
            }
            if (pos >= data.Length) return false;
            pos++;
            return true;
        }

        if (b is >= (byte)'0' and <= (byte)'9')
        {
            var len = 0;
            while (pos < data.Length && data[pos] is >= (byte)'0' and <= (byte)'9')
            {
                len = (len * 10) + (data[pos] - '0');
                if (len < 0 || len > data.Length) return false;
                pos++;
            }
            if (pos >= data.Length || data[pos] != (byte)':') return false;
            pos++;
            if (pos + len > data.Length) return false;
            pos += len;
            return true;
        }

        return false;
    }

    private static string ResolveVerticalFromClientCategory(DownloadClientConnectionSpec client, string category)
    {
        foreach (var kv in client.CategoryMappings)
        {
            if (string.Equals(kv.Value, category, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Key;
            }
        }

        var norm = category.Trim().ToLowerInvariant();
        return norm switch
        {
            "tv" or "series" or "sonarr" or "tv-sonarr" or "tv-managerr" or "shows" => MediaVerticals.Tv,
            "anime" or "anime-sonarr" or "anime-managerr" => MediaVerticals.Anime,
            "movies" or "movie" or "radarr" or "films" or "movies-radarr" or "movies-managerr" => MediaVerticals.Movies,
            "music" or "audio" or "lidarr" or "music-lidarr" or "music-managerr" => MediaVerticals.Music,
            "ebooks" or "ebook" or "books" or "readarr" or "ebooks-managerr" => MediaVerticals.Ebooks,
            "audiobooks" or "audiobook" or "audiobooks-managerr" => MediaVerticals.Audiobooks,
            "comics" or "comic" or "manga" or "comics-managerr" => MediaVerticals.Comics,
            _ => MediaVerticals.Movies,
        };
    }

    private static string BuildBaseUrl(DownloadClientConnectionSpec c)
    {
        var scheme = c.UseSsl ? "https" : "http";
        var urlBase = string.IsNullOrWhiteSpace(c.UrlBase) ? "" : "/" + c.UrlBase.Trim('/');
        return $"{scheme}://{c.Host}:{c.Port}{urlBase}";
    }

    private static void ApplyBasicAuth(HttpClient http, string? user, string? pass)
    {
        if (!string.IsNullOrWhiteSpace(user))
        {
            var bytes = Encoding.UTF8.GetBytes($"{user}:{pass ?? ""}");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
        }
    }

    // Transmission reports RPC failures as HTTP 200 with a "result" other than "success"
    private static async Task SendTransmissionCommandAsync(HttpClient http, string url, string clientName, object payload, CancellationToken ct)
    {
        var resp = await SendTransmissionRpcAsync(http, url, payload, ct);
        var result = resp.TryGetProperty("result", out var r) ? r.GetString() : null;
        if (!string.Equals(result, "success", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Transmission '{clientName}' answered '{result ?? "(no result)"}'.");
        }
    }

    private static async Task<JsonElement> SendTransmissionRpcAsync(HttpClient http, string url, object payload, CancellationToken ct)
    {
        using var r1 = await http.PostAsJsonAsync(url, payload, ct);
        if ((int)r1.StatusCode == 409 && r1.Headers.TryGetValues("X-Transmission-Session-Id", out var sidVals))
        {
            http.DefaultRequestHeaders.Remove("X-Transmission-Session-Id");
            http.DefaultRequestHeaders.Add("X-Transmission-Session-Id", sidVals.First());
            using var r2 = await http.PostAsJsonAsync(url, payload, ct);
            r2.EnsureSuccessStatusCode();
            return await r2.Content.ReadFromJsonAsync<JsonElement>(ct);
        }
        r1.EnsureSuccessStatusCode();
        return await r1.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
}

internal static class DownloadClientSpecExtensions
{
    public static string DisplayNameOrImpl(this DownloadClientConnectionSpec c) =>
        DownloadClientCatalog.ByKey.TryGetValue(c.Implementation, out var d) ? d.DisplayName : c.Implementation;
}
