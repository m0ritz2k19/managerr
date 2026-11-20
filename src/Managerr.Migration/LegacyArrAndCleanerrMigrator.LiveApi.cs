using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Managerr.Catalog;
using Managerr.Common.Crypto;
using Managerr.Common.Net;
using Managerr.Datastore;
using Managerr.Downloaders;
using Managerr.Indexers;
using Managerr.Indexers.Cardigann;
using Managerr.Metadata;
using Managerr.Profiles;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Microsoft.Data.Sqlite;

namespace Managerr.Migration;


public sealed partial class LegacyArrAndCleanerrMigrator
{
    public async Task<ArrApiTokenProbeResult> ProbeArrApiSourceAsync(
        ArrApiTokenProbeRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            throw new ArgumentException("BaseUrl is required to probe an *arr instance.");
        }
        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            throw new ArgumentException("API token (apiKey) is required to probe an *arr instance.");
        }

        using var http = safeHttpFactory.CreateHomelabClient(allowPrivateLan: true, ignoreSslErrors: true);
        var cleanKey = request.ApiKey.Trim();
        http.DefaultRequestHeaders.Remove("X-Api-Key");
        http.DefaultRequestHeaders.Add("X-Api-Key", cleanKey);

        var (detectedKind, appName, version, instanceName, normalizedBaseUrl, probeError) =
            await DetectLiveArrApplicationAsync(http, request.BaseUrl.Trim(), request.SourceKind, cleanKey, ct);

        if (!string.IsNullOrWhiteSpace(probeError))
        {
            return new ArrApiTokenProbeResult(
                Ok: false,
                DetectedKind: detectedKind,
                AppName: appName,
                Version: version,
                InstanceName: instanceName,
                NormalizedBaseUrl: normalizedBaseUrl,
                RootFolders: [],
                CatalogItemsCount: 0,
                MediaFilesCount: 0,
                IndexersCount: 0,
                DownloadClientsCount: 0,
                CustomFormatsCount: 0,
                BlocklistCount: 0,
                Message: probeError);
        }

        var existingLibs = await catalogService.ListLibrariesAsync(null, ct);
        var effPathMap = MergePathMaps(request.PathMappings ?? new Dictionary<string, string>(), new Dictionary<string, string>(), existingLibs);
        var apiPrefix = detectedKind is MigrationSourceKinds.Sonarr or MigrationSourceKinds.Radarr ? "/api/v3" : "/api/v1";

        async Task<JsonNode?> QuickGetAsync(string relPath)
        {
            try
            {
                var url = $"{normalizedBaseUrl}{relPath}";
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                using var resp = await http.GetAsync(url, cts.Token);
                if (!resp.IsSuccessStatusCode) return null;
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
            }
            catch
            {
                return null;
            }
        }

        var rootTask = QuickGetAsync($"{apiPrefix}/rootfolder");
        var dcTask = QuickGetAsync($"{apiPrefix}/downloadclient");
        var ixTask = QuickGetAsync($"{apiPrefix}/indexer");
        var cfTask = QuickGetAsync($"{apiPrefix}/customformat");
        var blTask = QuickGetAsync($"{apiPrefix}/blocklist?page=1&pageSize=1");

        var catalogEndpoint = detectedKind switch
        {
            MigrationSourceKinds.Sonarr => "/api/v3/series",
            MigrationSourceKinds.Radarr => "/api/v3/movie",
            MigrationSourceKinds.Lidarr => "/api/v1/artist",
            MigrationSourceKinds.Readarr => "/api/v1/book",
            MigrationSourceKinds.Overseerr => "/api/v1/request?take=100&skip=0",
            _ => null,
        };
        var catTask = catalogEndpoint is not null ? QuickGetAsync(catalogEndpoint) : Task.FromResult<JsonNode?>(null);

        await Task.WhenAll(rootTask, dcTask, ixTask, cfTask, blTask, catTask);

        var roots = new List<string>();
        if (rootTask.Result is JsonArray rfArr)
        {
            foreach (var rf in rfArr.OfType<JsonObject>())
            {
                var p = GetJsonObjString(rf, "path");
                if (!string.IsNullOrWhiteSpace(p))
                {
                    roots.Add(RemapPath(p, effPathMap));
                }
            }
        }

        var dcCount = (dcTask.Result as JsonArray)?.Count ?? 0;
        var ixCount = (ixTask.Result as JsonArray)?.Count ?? 0;
        var cfCount = (cfTask.Result as JsonArray)?.Count ?? 0;
        var blNode = blTask.Result;
        var blCount = GetJsonObjInt(blNode as JsonObject, "totalRecords")
                      ?? ((blNode as JsonObject)?["records"] as JsonArray)?.Count
                      ?? (blNode as JsonArray)?.Count
                      ?? 0;

        var catalogCount = 0;
        var filesCount = 0;

        if (detectedKind == MigrationSourceKinds.Prowlarr)
        {
            catalogCount = ixCount;
        }
        else if (detectedKind == MigrationSourceKinds.Sonarr && catTask.Result is JsonArray sArr)
        {
            catalogCount = sArr.Count;
            var anyStatsPresent = false;
            foreach (var s in sArr.OfType<JsonObject>())
            {
                if (s["statistics"] is JsonObject statsObj)
                {
                    anyStatsPresent = true;
                    filesCount += GetJsonObjInt(statsObj, "episodeFileCount") ?? 0;
                }
            }

            if (!anyStatsPresent && sArr.Count <= 15)
            {
                foreach (var s in sArr.OfType<JsonObject>())
                {
                    var sid = GetJsonObjInt(s, "id") ?? 0;
                    if (sid > 0 && await QuickGetAsync($"{apiPrefix}/episodefile?seriesId={sid}") is JsonArray efArr)
                    {
                        filesCount += efArr.Count;
                    }
                }
            }
        }
        else if (detectedKind == MigrationSourceKinds.Radarr && catTask.Result is JsonArray mArr)
        {
            catalogCount = mArr.Count;
            foreach (var m in mArr.OfType<JsonObject>())
            {
                if (m["movieFile"] is JsonObject || (GetJsonObjBool(m, "hasFile") ?? false))
                {
                    filesCount++;
                }
            }
        }
        else if (catTask.Result is JsonArray genArr)
        {
            catalogCount = genArr.Count;
            foreach (var item in genArr.OfType<JsonObject>())
            {
                filesCount += GetJsonObjInt(item["statistics"] as JsonObject, "trackFileCount")
                              ?? GetJsonObjInt(item["statistics"] as JsonObject, "bookFileCount")
                              ?? 0;
            }
        }
        else if (catTask.Result is JsonObject pageObj && pageObj["results"] is JsonArray resArr)
        {
            catalogCount = GetJsonObjInt(pageObj["pageInfo"] as JsonObject, "results") ?? resArr.Count;
        }

        return new ArrApiTokenProbeResult(
            Ok: true,
            DetectedKind: detectedKind,
            AppName: appName,
            Version: version,
            InstanceName: instanceName,
            NormalizedBaseUrl: normalizedBaseUrl,
            RootFolders: roots,
            CatalogItemsCount: catalogCount,
            MediaFilesCount: filesCount,
            IndexersCount: ixCount,
            DownloadClientsCount: dcCount,
            CustomFormatsCount: cfCount,
            BlocklistCount: blCount,
            Message: $"Connected to {appName} v{version} ({instanceName}) via {normalizedBaseUrl}: {catalogCount} items, {filesCount} files, {ixCount} indexers, {dcCount} download clients ready to import.");
    }

    public async Task<MigrationReportSummary> ImportFromArrApiTokenAsync(
        ArrApiTokenImportRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            throw new ArgumentException("BaseUrl is required to import from an *arr instance.");
        }
        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            throw new ArgumentException("API token (apiKey) is required to import from an *arr instance.");
        }

        using var http = safeHttpFactory.CreateHomelabClient(allowPrivateLan: true, ignoreSslErrors: true);
        var cleanKey = request.ApiKey.Trim();
        http.DefaultRequestHeaders.Remove("X-Api-Key");
        http.DefaultRequestHeaders.Add("X-Api-Key", cleanKey);

        var (detectedKind, appName, _, instanceName, normalizedBaseUrl, probeError) =
            await DetectLiveArrApplicationAsync(http, request.BaseUrl.Trim(), request.SourceKind, cleanKey, ct);

        if (!string.IsNullOrWhiteSpace(probeError))
        {
            throw new InvalidOperationException(probeError);
        }

        var sourceName = !string.IsNullOrWhiteSpace(request.Name)
            ? request.Name.Trim()
            : !string.IsNullOrWhiteSpace(instanceName)
                ? instanceName
                : $"{appName} Live API";

        if (request.SaveAsSource && !request.DryRun)
        {
            var existing = (await ListSourcesAsync(ct))
                .FirstOrDefault(s =>
                    s.FileOrUrl.Equals(normalizedBaseUrl, StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase));

            await UpsertSourceAsync(
                new UpsertMigrationSourceRequest(
                    Name: sourceName,
                    SourceKind: detectedKind,
                    FileOrUrl: normalizedBaseUrl,
                    TransportMode: "live_api",
                    ApiKey: cleanKey,
                    ReadarrSlotHint: request.ReadarrSlotHint,
                    PathMappings: request.PathMappings,
                    IsEnabled: true),
                existing?.Id,
                ct);
        }

        return await RunPipelineInternalAsync(
            new MigrationPlanAndExecuteRequest(
                Sources:
                [
                    new AdHocMigrationSourceSpec(
                        Name: sourceName,
                        SourceKind: detectedKind,
                        FileOrUrl: normalizedBaseUrl,
                        TransportMode: "live_api",
                        ApiKey: cleanKey,
                        ReadarrSlotHint: request.ReadarrSlotHint),
                ],
                GlobalPathMappings: request.PathMappings,
                UpliftLanguageCustomFormats: request.UpliftLanguageCustomFormats),
            executeChanges: !request.DryRun,
            ct: ct);
    }


    private static async Task<(string DetectedKind, string AppName, string Version, string InstanceName, string NormalizedBaseUrl, string? Error)>
        DetectLiveArrApplicationAsync(
            HttpClient http,
            string rawBaseUrl,
            string? requestedKind,
            string apiKey,
            CancellationToken ct)
    {
        var normalizedBaseUrl = rawBaseUrl.Trim().TrimEnd('/');
        foreach (var suffix in new[] { "/api/v3/system/status", "/api/v1/system/status", "/api/v3", "/api/v1" })
        {
            if (normalizedBaseUrl.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalizedBaseUrl = normalizedBaseUrl[..^suffix.Length].TrimEnd('/');
            }
        }

        if (Uri.TryCreate(normalizedBaseUrl, UriKind.Absolute, out var rawUri) && !string.IsNullOrWhiteSpace(rawUri.UserInfo))
        {
            var basicBytes = System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(rawUri.UserInfo));
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(basicBytes));
            normalizedBaseUrl = $"{rawUri.Scheme}://{rawUri.Host}{(rawUri.IsDefaultPort ? "" : $":{rawUri.Port}")}{rawUri.AbsolutePath}".TrimEnd('/');
        }

        var kindHint = string.IsNullOrWhiteSpace(requestedKind) || requestedKind.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : requestedKind.Trim().ToLowerInvariant();

        // Only the URL the user entered is contacted: the API key is never sent to guessed hosts.
        string[] candidateBaseUrls = [normalizedBaseUrl];
        var candidatePaths = new List<string>
        {
            "/api/v3/system/status",
            "/api/v1/system/status",
            "/api/v1/status",
        };

        Exception? lastEx = null;
        int? lastHttpCode = null;
        var sawReverseProxyBasicAuth = false;
        var sawArrUnauthorized = false;

        foreach (var candidateBase in candidateBaseUrls)
        {
            foreach (var path in candidatePaths)
            {
                try
                {
                    var url = $"{candidateBase}{path}";
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(6.0));
                    using var resp = await http.GetAsync(url, cts.Token);
                    var code = (int)resp.StatusCode;
                    lastHttpCode = code;

                    if (code is 401 or 403)
                    {
                        var wwwAuth = resp.Headers.WwwAuthenticate.ToString();
                        if (wwwAuth.Contains("Basic", StringComparison.OrdinalIgnoreCase) ||
                            wwwAuth.Contains("traefik", StringComparison.OrdinalIgnoreCase))
                        {
                            sawReverseProxyBasicAuth = true;
                            // Reverse proxy blocked this hostname before reaching *arr; skip remaining paths on this base URL
                            break;
                        }
                        sawArrUnauthorized = true;
                        continue;
                    }

                    if (!resp.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    var rawJson = await resp.Content.ReadAsStringAsync(cts.Token);
                    var node = JsonNode.Parse(rawJson);

                    if (node is JsonObject obj)
                    {
                        var appName = GetJsonObjString(obj, "appName")
                                      ?? GetJsonObjString(obj, "applicationTitle")
                                      ?? (kindHint is not null ? char.ToUpperInvariant(kindHint[0]) + kindHint[1..] : null);
                        var version = GetJsonObjString(obj, "version") ?? "unknown";
                        var instanceName = GetJsonObjString(obj, "instanceName") ?? appName ?? "Live *arr";

                        var detectedKind = kindHint ?? InferArrKindFromAppName(appName, path, obj);
                        if (!string.IsNullOrWhiteSpace(detectedKind))
                        {
                            var resolvedAppName = appName ?? detectedKind switch
                            {
                                MigrationSourceKinds.Sonarr => "Sonarr",
                                MigrationSourceKinds.Radarr => "Radarr",
                                MigrationSourceKinds.Prowlarr => "Prowlarr",
                                MigrationSourceKinds.Lidarr => "Lidarr",
                                MigrationSourceKinds.Readarr => "Readarr",
                                MigrationSourceKinds.Overseerr => "Overseerr / Seerr",
                                _ => "Servarr",
                            };
                            return (detectedKind, resolvedAppName, version, instanceName, candidateBase, null);
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    // If connection refused/timed out on this base URL, move to the next candidate base URL immediately
                    break;
                }
            }
        }

        if (kindHint is not null && lastHttpCode is null && lastEx is null)
        {
            return (kindHint, kindHint, "unknown", $"{kindHint} Live", normalizedBaseUrl, null);
        }

        string errDetail;
        if (sawArrUnauthorized)
        {
            errDetail = $"Authentication rejected by *arr instance (HTTP 401/403) — verify your *arr API token in Settings -> General -> Security.";
        }
        else if (sawReverseProxyBasicAuth)
        {
            errDetail = $"'{normalizedBaseUrl}' is protected by reverse-proxy HTTP Basic Auth — use the app's internal address (e.g. http://sonarr:8989) or put the proxy credentials in the URL (https://user:password@host).";
        }
        else
        {
            errDetail = $"Could not reach a valid *arr /system/status endpoint at '{normalizedBaseUrl}'" +
                        (lastHttpCode.HasValue ? $" (HTTP {lastHttpCode})" : "") +
                        (lastEx is not null ? $": {lastEx.Message}" : ".");
        }

        return (kindHint ?? MigrationSourceKinds.Sonarr, "Unknown", "unknown", "Unknown", normalizedBaseUrl, errDetail);
    }

    private static string? InferArrKindFromAppName(string? appName, string matchedPath, JsonObject statusObj)
    {
        if (!string.IsNullOrWhiteSpace(appName))
        {
            if (appName.Contains("sonarr", StringComparison.OrdinalIgnoreCase)) return MigrationSourceKinds.Sonarr;
            if (appName.Contains("radarr", StringComparison.OrdinalIgnoreCase)) return MigrationSourceKinds.Radarr;
            if (appName.Contains("prowlarr", StringComparison.OrdinalIgnoreCase)) return MigrationSourceKinds.Prowlarr;
            if (appName.Contains("lidarr", StringComparison.OrdinalIgnoreCase)) return MigrationSourceKinds.Lidarr;
            if (appName.Contains("readarr", StringComparison.OrdinalIgnoreCase) ||
                appName.Contains("chaptarr", StringComparison.OrdinalIgnoreCase) ||
                appName.Contains("bookshelf", StringComparison.OrdinalIgnoreCase)) return MigrationSourceKinds.Readarr;
            if (appName.Contains("seerr", StringComparison.OrdinalIgnoreCase)) return MigrationSourceKinds.Overseerr;
        }

        if (matchedPath == "/api/v1/status" && statusObj.ContainsKey("commitTag"))
        {
            return MigrationSourceKinds.Overseerr;
        }

        return null;
    }

    private static async Task<(Dictionary<int, string> DownloadClientSettingsById, Dictionary<int, string> IndexerSettingsById)>
        TryLoadUnmaskedArrBackupSettingsAsync(
            HttpClient http,
            string baseUrl,
            string apiPrefix,
            string apiKey,
            CancellationToken ct)
    {
        var dcMap = new Dictionary<int, string>();
        var ixMap = new Dictionary<int, string>();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var listUrl = $"{baseUrl}{apiPrefix}/system/backup";
            using var resp = await http.GetAsync(listUrl, cts.Token);
            if (!resp.IsSuccessStatusCode) return (dcMap, ixMap);

            var raw = await resp.Content.ReadAsStringAsync(cts.Token);
            if (JsonNode.Parse(raw) is not JsonArray backups || backups.Count == 0)
            {
                return (dcMap, ixMap);
            }

            HttpResponseMessage? successfulResp = null;
            foreach (var b in backups.OfType<JsonObject>())
            {
                var pathRaw = GetJsonObjString(b, "path");
                var name = GetJsonObjString(b, "name");
                var type = GetJsonObjString(b, "type");
                var size = GetJsonObjLong(b, "size");

                if (size.HasValue && (size.Value < 0 || size.Value >= 64_000_000L))
                {
                    continue;
                }

                var candidates = new List<string>();
                if (IsSafeBackupCandidateSegment(pathRaw))
                {
                    string unescapedPath;
                    try { unescapedPath = Uri.UnescapeDataString(pathRaw!); } catch { unescapedPath = pathRaw!; }
                    var normPath = unescapedPath.Replace('\\', '/');
                    if (normPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                        !normPath.Contains("..", StringComparison.Ordinal) &&
                        !normPath.StartsWith("//", StringComparison.Ordinal) &&
                        normPath.IndexOfAny(['?', '#', ':']) < 0)
                    {
                        var candidate = normPath.StartsWith('/') ? normPath : "/" + normPath;
                        if (!candidate.StartsWith("//", StringComparison.Ordinal))
                        {
                            candidates.Add(candidate);
                        }
                    }
                }

                if (IsSafeBackupCandidateSegment(name) && IsSafeBackupCandidateSegment(type))
                {
                    string unescapedName;
                    string unescapedType;
                    try { unescapedName = Uri.UnescapeDataString(name!); } catch { unescapedName = name!; }
                    try { unescapedType = Uri.UnescapeDataString(type!); } catch { unescapedType = type!; }

                    if (unescapedName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                        unescapedName.IndexOfAny(['/', '\\', '?', '#', ':']) < 0 &&
                        !unescapedName.Contains("..", StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(unescapedType))
                    {
                        var lowerType = unescapedType.Trim().ToLowerInvariant();
                        if (lowerType is "scheduled" or "manual" or "update")
                        {
                            candidates.Add($"/backup/{lowerType}/{unescapedName}");
                            candidates.Add($"{apiPrefix}/system/backup/{lowerType}/{unescapedName}");
                        }
                    }
                }

                foreach (var candidate in candidates)
                {
                    var dlUrl = $"{baseUrl}{candidate}";
                    try
                    {
                        var zipResp = await http.GetAsync(dlUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        if (!zipResp.IsSuccessStatusCode || zipResp.Content.Headers.ContentLength is > 64_000_000L)
                        {
                            zipResp.Dispose();
                            continue;
                        }

                        var mediaType = zipResp.Content.Headers.ContentType?.MediaType;
                        if (mediaType is not null && (mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase) ||
                                                      mediaType.Contains("application/json", StringComparison.OrdinalIgnoreCase)))
                        {
                            zipResp.Dispose();
                            continue;
                        }

                        successfulResp = zipResp;
                        break;
                    }
                    catch
                    {
                        // Candidate download probe error ignored
                    }
                }

                if (successfulResp is not null)
                {
                    break;
                }
            }

            if (successfulResp is null)
            {
                return (dcMap, ixMap);
            }

            using var respToDispose = successfulResp;
            var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-arr-bkp-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var zipFile = Path.Combine(tempDir, "backup.zip");
                await using (var netStream = await respToDispose.Content.ReadAsStreamAsync(cts.Token))
                await using (var fs = File.Create(zipFile))
                    {
                        var buffer = new byte[65536];
                        long totalRead = 0;
                        int read;
                        while ((read = await netStream.ReadAsync(buffer, cts.Token)) > 0)
                        {
                            totalRead += read;
                            if (totalRead > 64_000_000L)
                            {
                                throw new InvalidOperationException("Backup archive exceeded 64 MB safety cap.");
                            }
                            await fs.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                        }
                    }

                    ExtractArchiveSafely(zipFile, tempDir);
                var dbFile = Directory.GetFiles(tempDir, "*.db", SearchOption.AllDirectories).FirstOrDefault();
                if (dbFile is not null)
                {
                    await using var conn = OpenReadOnlySqlite(dbFile);
                    await conn.OpenAsync(cts.Token);
                    var tables = (await conn.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table';"))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    if (tables.Contains("DownloadClients"))
                    {
                        var dcs = await conn.QueryAsync<LegacySettingRow>("SELECT Id, Settings FROM DownloadClients;");
                        foreach (var dc in dcs)
                        {
                            if (!string.IsNullOrWhiteSpace(dc.Settings))
                            {
                                dcMap[dc.Id] = dc.Settings;
                            }
                        }
                    }

                    if (tables.Contains("Indexers"))
                    {
                        var ixs = await conn.QueryAsync<LegacyIndexerRow>("SELECT Id, Settings FROM Indexers;");
                        foreach (var ix in ixs)
                        {
                            if (!string.IsNullOrWhiteSpace(ix.Settings))
                            {
                                ixMap[ix.Id] = ix.Settings;
                            }
                        }
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
        catch
        {
            // Best-effort backup secret unmasking
        }

        return (dcMap, ixMap);
    }

    private static string? ExtractArrPosterUrl(JsonObject node)
    {
        if (node["images"] is JsonArray imgs)
        {
            foreach (var img in imgs.OfType<JsonObject>())
            {
                var coverType = GetJsonObjString(img, "coverType");
                if (string.Equals(coverType, "poster", StringComparison.OrdinalIgnoreCase))
                {
                    var remote = GetJsonObjString(img, "remoteUrl") ?? GetJsonObjString(img, "url");
                    if (!string.IsNullOrWhiteSpace(remote) && remote.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        return remote;
                    }
                }
            }
        }
        return null;
    }

    private async Task<(int LanguageCfUpliftDelta, int QualityProfilesDelta)> InspectLiveApiSourceAsync(
        ResolvedMigrationSource src,
        IReadOnlyDictionary<string, string> pathMap,
        List<SourceInspectionSummary> inspected,
        HashSet<string> unmappedPaths,
        List<StageIndexerCandidate> rawIndexers,
        HashSet<int> prowlarrMappedAppIndexerIds,
        List<UpsertIndexerProxyRequest> importedProxies,
        List<StageDownloadClientCandidate> rawClients,
        List<CreateRemotePathMappingRequest> rawRemoteMappings,
        List<string> customFormatPayloads,
        Dictionary<string, (string Title, string Reason)> blocklistHashes,
        List<StageSonarrSeries> sonarrSeries,
        List<StageRadarrMovie> radarrMovies,
        List<StageLidarrArtist> lidarrArtists,
        List<StageReadarrBook> readarrBooks,
        List<StageOverseerrUser> overseerrUsers,
        List<StageOverseerrRequest> overseerrRequests,
        List<string> warnings,
        List<string> logs,
        CancellationToken ct)
    {
        var languageUpliftDelta = 0;
        var qualityProfilesDelta = 0;

        try
        {
            using var http = safeHttpFactory.CreateHomelabClient(allowPrivateLan: true, ignoreSslErrors: true);
            var cleanApiKey = src.PlainApiKey?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(cleanApiKey))
            {
                http.DefaultRequestHeaders.Remove("X-Api-Key");
                http.DefaultRequestHeaders.Add("X-Api-Key", cleanApiKey);
            }

            var (detectedKind, appName, version, _, baseUrl, _) =
                await DetectLiveArrApplicationAsync(http, src.OriginalFileOrUrl, src.SourceKind, cleanApiKey, ct);
            var effectiveKind = string.IsNullOrWhiteSpace(src.SourceKind) || src.SourceKind.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? detectedKind
                : src.SourceKind.Trim().ToLowerInvariant();
            var effectiveSrc = src with { SourceKind = effectiveKind, OriginalFileOrUrl = baseUrl };

            var apiPrefix = effectiveKind is MigrationSourceKinds.Sonarr or MigrationSourceKinds.Radarr
                ? "/api/v3"
                : "/api/v1";

            var discoveredRoots = new List<string>();
            var items = 0;
            var files = 0;

            async Task<JsonNode?> TryGetJsonNodeAsync(string relativeEndpoint)
            {
                try
                {
                    var url = $"{baseUrl}{relativeEndpoint}";
                    using var resp = await http.GetAsync(url, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        return null;
                    }
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
                }
                catch
                {
                    return null;
                }
            }

            // 1. Common Servarr resources (RootFolders, DownloadClients, RemotePathMappings, IndexerProxies, Indexers, CustomFormats, QualityProfiles, Blocklist)
            if (effectiveKind is MigrationSourceKinds.Sonarr
                or MigrationSourceKinds.Radarr
                or MigrationSourceKinds.Prowlarr
                or MigrationSourceKinds.Lidarr
                or MigrationSourceKinds.Readarr)
            {
                var (unmaskedDcSettings, unmaskedIxSettings) =
                    await TryLoadUnmaskedArrBackupSettingsAsync(http, baseUrl, apiPrefix, cleanApiKey, ct);

                if (await TryGetJsonNodeAsync($"{apiPrefix}/rootfolder") is JsonArray rootArr)
                {
                    foreach (var rf in rootArr.OfType<JsonObject>())
                    {
                        var rfPath = GetJsonObjString(rf, "path");
                        if (!string.IsNullOrWhiteSpace(rfPath))
                        {
                            var mapped = RemapPath(rfPath, pathMap);
                            discoveredRoots.Add(mapped);
                            if (!Directory.Exists(mapped))
                            {
                                unmappedPaths.Add($"{src.Name}: {rfPath} -> {mapped}");
                            }
                        }
                    }
                }

                bool? vpnResolvesPrivateMemo = null;
                if (await TryGetJsonNodeAsync($"{apiPrefix}/downloadclient") is JsonArray dcArr)
                {
                    foreach (var dcNode in dcArr.OfType<JsonObject>())
                    {
                        var dcId = GetJsonObjInt(dcNode, "id") ?? 0;
                        var settingsJson = unmaskedDcSettings.TryGetValue(dcId, out var unmaskedDc)
                            ? unmaskedDc
                            : ConvertArrFieldsArrayToSettingsJson(dcNode);

                        var dcRow = new LegacySettingRow
                        {
                            Id = dcId,
                            Name = GetJsonObjString(dcNode, "name"),
                            Implementation = GetJsonObjString(dcNode, "implementation"),
                            Enable = (GetJsonObjBool(dcNode, "enable") ?? true) ? 1 : 0,
                            Settings = settingsJson,
                        };
                        var parsedDc = ParseLegacyDownloadClient(effectiveSrc, dcRow);
                        if (parsedDc is not null)
                        {
                            var srcUri = new Uri(baseUrl);
                            static bool IsLoopback(string h) =>
                                h.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                                h.Equals("127.0.0.1", StringComparison.Ordinal) ||
                                h.Equals("::1", StringComparison.Ordinal) ||
                                h.Equals("[::1]", StringComparison.Ordinal);

                            var isSrcLoopback = IsLoopback(srcUri.Host);
                            var isDcLoopback = IsLoopback(parsedDc.Host);

                            if (!isSrcLoopback && isDcLoopback)
                            {
                                if (!vpnResolvesPrivateMemo.HasValue)
                                {
                                    vpnResolvesPrivateMemo = await CheckVpnResolvesPrivateAsync(safeHttpFactory, ct);
                                }

                                if (vpnResolvesPrivateMemo.Value)
                                {
                                    parsedDc = parsedDc with { Host = "vpn" };
                                    logs.Add($"Remapped shared-VPN download client '{parsedDc.Name}' host from '{parsedDc.Host}' to 'vpn'.");
                                }
                                else
                                {
                                    warnings.Add($"Download client '{parsedDc.Name}' uses loopback host '{parsedDc.Host}' inside {src.Name}; update its Host in Settings → Acquisition if Managerr runs in a separate container.");
                                }
                            }

                            rawClients.Add(parsedDc);
                        }
                    }
                }

                if (await TryGetJsonNodeAsync($"{apiPrefix}/remotepathmapping") is JsonArray rpmArr)
                {
                    foreach (var rpm in rpmArr.OfType<JsonObject>())
                    {
                        var host = GetJsonObjString(rpm, "host");
                        var remotePath = GetJsonObjString(rpm, "remotePath");
                        var localPath = GetJsonObjString(rpm, "localPath");
                        if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(remotePath) && !string.IsNullOrWhiteSpace(localPath))
                        {
                            var remappedLocal = RemapPath(localPath.Trim(), pathMap);
                            if (PolymorphicCatalogService.IsForbiddenSystemRoot(remappedLocal))
                            {
                                warnings.Add($"Skipping RemotePathMapping with forbidden local path '{remappedLocal}' for host '{host}'.");
                                continue;
                            }

                            rawRemoteMappings.Add(new CreateRemotePathMappingRequest(
                                null,
                                host.Trim(),
                                remotePath.Trim(),
                                remappedLocal));
                        }
                    }
                }

                if (await TryGetJsonNodeAsync($"{apiPrefix}/indexerProxy") is JsonArray pxArr ||
                    await TryGetJsonNodeAsync($"{apiPrefix}/indexerproxy") is JsonArray pxArrLower)
                {
                    var activePxArr = (await TryGetJsonNodeAsync($"{apiPrefix}/indexerProxy") as JsonArray)
                                      ?? (await TryGetJsonNodeAsync($"{apiPrefix}/indexerproxy") as JsonArray);
                    if (activePxArr is not null)
                    {
                        foreach (var pxNode in activePxArr.OfType<JsonObject>())
                        {
                            var pxRow = new LegacySettingRow
                            {
                                Id = GetJsonObjInt(pxNode, "id") ?? 0,
                                Name = GetJsonObjString(pxNode, "name"),
                                Implementation = GetJsonObjString(pxNode, "implementation"),
                                Enable = 1,
                                Settings = ConvertArrFieldsArrayToSettingsJson(pxNode),
                            };
                            var parsedPx = ParseLegacyIndexerProxy(pxRow);
                            if (parsedPx is not null)
                            {
                                importedProxies.Add(parsedPx);
                            }
                        }
                    }
                }

                if (await TryGetJsonNodeAsync($"{apiPrefix}/indexer") is JsonArray ixArr)
                {
                    foreach (var ixNode in ixArr.OfType<JsonObject>())
                    {
                        var ixId = GetJsonObjInt(ixNode, "id") ?? 0;
                        var ixName = GetJsonObjString(ixNode, "name") ?? $"Indexer-{ixId}";
                        var ixImpl = GetJsonObjString(ixNode, "implementation") ?? "Torznab";
                        var ixProto = GetJsonObjString(ixNode, "protocol") ??
                                      (ixImpl.Contains("Newznab", StringComparison.OrdinalIgnoreCase) ? "usenet" : "torrent");
                        var defName = GetJsonObjString(ixNode, "definitionName");
                        var settingsJson = unmaskedIxSettings.TryGetValue(ixId, out var unmaskedIx)
                            ? unmaskedIx
                            : ConvertArrFieldsArrayToSettingsJson(ixNode, defName);

                        var enableRss = GetJsonObjBool(ixNode, "enableRss");
                        var enableAuto = GetJsonObjBool(ixNode, "enableAutomaticSearch");
                        var enableInteractive = GetJsonObjBool(ixNode, "enableInteractiveSearch");
                        var priority = GetJsonObjInt(ixNode, "priority");

                        var staged = ParseLegacyIndexer(
                            effectiveSrc,
                            new LegacyIndexerRow
                            {
                                Id = ixId,
                                Name = ixName,
                                Implementation = ixImpl,
                                ConfigContract = GetJsonObjString(ixNode, "configContract"),
                                Settings = settingsJson,
                                EnableRss = enableRss,
                                EnableAutomaticSearch = enableAuto,
                                EnableInteractiveSearch = enableInteractive,
                                Priority = priority,
                            });

                        // If importing from Prowlarr and the upstream indexer's API key is masked ("********") or it uses Prowlarr's native feed,
                        // wire the indexer to Prowlarr's built-in Newznab/Torznab endpoint (${baseUrl}/${ixId}/api) with the Prowlarr API token if needed
                        if (effectiveKind == MigrationSourceKinds.Prowlarr)
                        {
                            var hasUsableDirectUrl = !string.IsNullOrWhiteSpace(staged.Request.BaseUrl) &&
                                                     !staged.Request.BaseUrl.Equals("https://indexer.local", StringComparison.OrdinalIgnoreCase);
                            var hasUnmaskedKey = !string.IsNullOrWhiteSpace(staged.Request.ApiKey) &&
                                                 !IsMaskedArrSecret(staged.Request.ApiKey);

                            if (!hasUsableDirectUrl || (!hasUnmaskedKey && !staged.Request.Implementation.Equals("Cardigann", StringComparison.OrdinalIgnoreCase)))
                            {
                                var feedImpl = ixProto.Equals("usenet", StringComparison.OrdinalIgnoreCase) ? "Newznab" : "Torznab";
                                staged = staged with
                                {
                                    Request = staged.Request with
                                    {
                                        Implementation = feedImpl,
                                        Protocol = ixProto.Equals("usenet", StringComparison.OrdinalIgnoreCase) ? "usenet" : "torrent",
                                        BaseUrl = $"{baseUrl}/{ixId}/api",
                                        ApiKey = cleanApiKey,
                                    },
                                };
                            }
                        }

                        rawIndexers.Add(staged);
                        if (effectiveKind == MigrationSourceKinds.Prowlarr)
                        {
                            items++;
                        }
                    }
                }

                if (await TryGetJsonNodeAsync($"{apiPrefix}/customformat") is JsonArray cfArr)
                {
                    foreach (var cfNode in cfArr.OfType<JsonObject>())
                    {
                        var cfId = GetJsonObjInt(cfNode, "id") ?? 0;
                        var cfName = GetJsonObjString(cfNode, "name") ?? $"CF-{cfId}";
                        var includeRename = GetJsonObjBool(cfNode, "includeCustomFormatWhenRenaming") ?? false;
                        var specsNode = cfNode["specifications"]?.DeepClone() ?? new JsonArray();
                        var specsStr = specsNode.ToJsonString();
                        if (specsStr.Contains("LanguageSpecification", StringComparison.OrdinalIgnoreCase))
                        {
                            languageUpliftDelta++;
                        }

                        var cfObj = new
                        {
                            trash_id = $"migrated-{effectiveKind}-cf-{cfId}",
                            name = cfName,
                            includeCustomFormatWhenRenaming = includeRename,
                            specifications = specsNode,
                        };
                        customFormatPayloads.Add(JsonSerializer.Serialize(cfObj, JsonOpts));
                    }
                }

                if (await TryGetJsonNodeAsync($"{apiPrefix}/qualityprofile") is JsonArray qpArr)
                {
                    qualityProfilesDelta += qpArr.Count;
                }

                var blNode = await TryGetJsonNodeAsync($"{apiPrefix}/blocklist?page=1&pageSize=500");
                var blRecords = (blNode as JsonObject)?["records"] as JsonArray ?? blNode as JsonArray;
                if (blRecords is not null)
                {
                    foreach (var bl in blRecords.OfType<JsonObject>())
                    {
                        var sourceTitle = GetJsonObjString(bl, "sourceTitle");
                        var torrentHash = GetJsonObjString(bl, "torrentInfoHash");
                        var proto = GetJsonObjString(bl, "protocol");
                        if (!string.IsNullOrWhiteSpace(torrentHash))
                        {
                            blocklistHashes[torrentHash.Trim().ToLowerInvariant()] =
                                (sourceTitle ?? "Migrated Blocklist Entry", $"Migrated from {src.Name} Live API");
                        }
                        else if (!string.IsNullOrWhiteSpace(sourceTitle))
                        {
                            var normNzbTitle = ReleaseBlocklistSnapshot.NormalizeTitleKey(sourceTitle);
                            if (normNzbTitle.Length >= 4)
                            {
                                var nzbKey = ReleaseBlocklistSnapshot.ComputeNzbKeyFromToken($"legacy_nzb:{normNzbTitle}");
                                blocklistHashes[nzbKey] = (sourceTitle.Trim(), $"Migrated Usenet NZB ({proto ?? "usenet"}) from {src.Name} Live API");
                            }
                        }
                    }
                }
            }

            // 2. Vertical-specific catalog & media file extraction via Live REST API
            if (effectiveKind == MigrationSourceKinds.Radarr)
            {
                if (await TryGetJsonNodeAsync("/api/v3/movie") is JsonArray movieArr)
                {
                    foreach (var node in movieArr.OfType<JsonObject>())
                    {
                        var title = GetJsonObjString(node, "title") ?? "Untitled";
                        var year = GetJsonObjInt(node, "year");
                        var tmdbId = GetJsonObjInt(node, "tmdbId") ?? 0;
                        var rawMoviePath = GetJsonObjString(node, "path") ?? "/movies";
                        var mappedMoviePath = RemapPath(rawMoviePath, pathMap);
                        var rawRoot = GetJsonObjString(node, "rootFolderPath") ?? Path.GetDirectoryName(rawMoviePath) ?? "/movies";
                        var mappedRoot = RemapPath(rawRoot, pathMap);

                        string? fullDiskPath = null;
                        string? relFilePath = null;
                        var fileSize = 0L;
                        var qualityName = "WEBDL-1080p";
                        IReadOnlyList<string>? audioLangs = null;
                        IReadOnlyList<string>? subLangs = null;

                        if (node["movieFile"] is JsonObject mf)
                        {
                            var (aLangs, sLangs) = ExtractArrFileLanguages(mf);
                            audioLangs = aLangs;
                            subLangs = sLangs;
                            var mfRel = GetJsonObjString(mf, "relativePath");
                            var mfPath = GetJsonObjString(mf, "path");
                            fileSize = GetJsonObjLong(mf, "size") ?? 0L;
                            qualityName = ParseQualityLabel(mf["quality"]?.ToJsonString());
                            relFilePath = mfRel ?? (!string.IsNullOrWhiteSpace(mfPath) ? Path.GetFileName(mfPath) : null);
                            fullDiskPath = !string.IsNullOrWhiteSpace(mfPath)
                                ? RemapPath(mfPath, pathMap)
                                : !string.IsNullOrWhiteSpace(relFilePath)
                                    ? Path.Combine(mappedMoviePath, relFilePath)
                                    : null;
                            if (fullDiskPath is not null)
                            {
                                files++;
                            }
                        }

                        var minAvail = NormalizeRadarrMinimumAvailability(GetJsonObjString(node, "minimumAvailability"));
                        var origLang = ExtractArrOriginalLanguageIso2(node["originalLanguage"]);
                        var radarrAltTitles = ExtractArrAlternateTitles(node, title, includeOriginalTitle: true);

                        radarrMovies.Add(new StageRadarrMovie(
                            Title: title,
                            Year: year,
                            TmdbId: tmdbId,
                            ImdbId: GetJsonObjString(node, "imdbId"),
                            Monitored: GetJsonObjBool(node, "monitored") ?? true,
                            RootFolderPath: mappedRoot,
                            FullDiskPath: fullDiskPath,
                            RelativeFilePath: relFilePath,
                            FileSize: fileSize,
                            QualityName: qualityName,
                            PosterUrl: ExtractArrPosterUrl(node),
                            Overview: GetJsonObjString(node, "overview"),
                            MoviePath: mappedMoviePath,
                            AudioLanguages: audioLangs,
                            SubtitleLanguages: subLangs,
                            MinimumAvailability: minAvail,
                            OriginalLanguage: origLang,
                            AltTitles: radarrAltTitles));
                        items++;
                    }
                }
            }
            else if (effectiveKind == MigrationSourceKinds.Sonarr)
            {
                if (await TryGetJsonNodeAsync("/api/v3/series") is JsonArray seriesArr)
                {
                    var seriesNodes = seriesArr.OfType<JsonObject>().ToList();
                    using var gate = new SemaphoreSlim(8, 8);
                    var seriesTasks = seriesNodes.Select(async node =>
                    {
                        await gate.WaitAsync(ct);
                        try
                        {
                            var seriesId = GetJsonObjInt(node, "id") ?? 0;
                            var title = GetJsonObjString(node, "title") ?? "Untitled";
                            var tvdbId = GetJsonObjInt(node, "tvdbId") ?? 0;
                            var rawSeriesPath = GetJsonObjString(node, "path") ?? "/tv";
                            var mappedSeriesPath = RemapPath(rawSeriesPath, pathMap);
                            var rawRoot = GetJsonObjString(node, "rootFolderPath") ?? Path.GetDirectoryName(rawSeriesPath) ?? "/tv";
                            var mappedRoot = RemapPath(rawRoot, pathMap);

                            var seasonMonitored = new Dictionary<int, bool>();
                            if (node["seasons"] is JsonArray seasonsArr)
                            {
                                foreach (var sn in seasonsArr.OfType<JsonObject>())
                                {
                                    var sNum = GetJsonObjInt(sn, "seasonNumber");
                                    var sMon = GetJsonObjBool(sn, "monitored");
                                    if (sNum.HasValue && sMon.HasValue)
                                    {
                                        seasonMonitored[sNum.Value] = sMon.Value;
                                    }
                                }
                            }

                            var epFilesById = new Dictionary<long, (string? Path, string? RelativePath, long Size, string QualityName, IReadOnlyList<string> AudioLanguages, IReadOnlyList<string> SubtitleLanguages)>();
                            if (seriesId > 0)
                            {
                                var efTask = TryGetJsonNodeAsync($"/api/v3/episodefile?seriesId={seriesId}");
                                var epTask = TryGetJsonNodeAsync($"/api/v3/episode?seriesId={seriesId}");
                                await Task.WhenAll(efTask, epTask);

                                if (efTask.Result is JsonArray efArr)
                                {
                                    foreach (var ef in efArr.OfType<JsonObject>())
                                    {
                                        var efId = GetJsonObjLong(ef, "id") ?? 0;
                                        if (efId <= 0) continue;
                                        var efPath = GetJsonObjString(ef, "path");
                                        var efRel = GetJsonObjString(ef, "relativePath");
                                        var efSize = GetJsonObjLong(ef, "size") ?? 0L;
                                        var efQual = ParseQualityLabel(ef["quality"]?.ToJsonString());
                                        var (audioLangs, subLangs) = ExtractArrFileLanguages(ef);
                                        epFilesById[efId] = (efPath, efRel, efSize, efQual, audioLangs, subLangs);
                                    }
                                }

                                var stagedEpisodes = new List<StageSonarrEpisode>();
                                var seriesFilesCount = 0;
                                if (epTask.Result is JsonArray epArr)
                                {
                                    foreach (var ep in epArr.OfType<JsonObject>())
                                    {
                                        var sNum = GetJsonObjInt(ep, "seasonNumber") ?? 1;
                                        var eNum = GetJsonObjInt(ep, "episodeNumber") ?? 1;
                                        var absNum = GetJsonObjInt(ep, "absoluteEpisodeNumber");
                                        var epTitle = GetJsonObjString(ep, "title") ?? $"Episode {eNum}";
                                        var airDate = GetJsonObjString(ep, "airDate") ?? GetJsonObjString(ep, "airDateUtc");
                                        var epMon = GetJsonObjBool(ep, "monitored") ?? true;
                                        var efId = GetJsonObjLong(ep, "episodeFileId") ?? 0L;

                                        string? relPath = null;
                                        string? fullDiskPath = null;
                                        var fSize = 0L;
                                        var qName = "WEBDL-1080p";
                                        IReadOnlyList<string>? epAudioLangs = null;
                                        IReadOnlyList<string>? epSubLangs = null;

                                        if (efId > 0 && epFilesById.TryGetValue(efId, out var efInfo))
                                        {
                                            relPath = efInfo.RelativePath ?? (!string.IsNullOrWhiteSpace(efInfo.Path) ? Path.GetFileName(efInfo.Path) : null);
                                            fullDiskPath = !string.IsNullOrWhiteSpace(efInfo.Path)
                                                ? RemapPath(efInfo.Path, pathMap)
                                                : !string.IsNullOrWhiteSpace(relPath)
                                                    ? Path.Combine(mappedSeriesPath, relPath)
                                                    : null;
                                            fSize = efInfo.Size;
                                            qName = efInfo.QualityName;
                                            epAudioLangs = efInfo.AudioLanguages;
                                            epSubLangs = efInfo.SubtitleLanguages;
                                            if (fullDiskPath is not null)
                                            {
                                                seriesFilesCount++;
                                            }
                                        }

                                        stagedEpisodes.Add(new StageSonarrEpisode(
                                            SeasonNumber: sNum,
                                            EpisodeNumber: eNum,
                                            Title: epTitle,
                                            AirDate: airDate,
                                            Monitored: epMon,
                                            RelativePathInSeries: relPath,
                                            FullDiskPath: fullDiskPath,
                                            FileSize: fSize,
                                            QualityName: qName,
                                            AudioLanguages: epAudioLangs,
                                            SubtitleLanguages: epSubLangs,
                                            AbsoluteEpisodeNumber: absNum));
                                    }
                                }

                                var origLang = ExtractArrOriginalLanguageIso2(node["originalLanguage"]);
                                var sonarrAltTitles = ExtractArrAlternateTitles(node, title, includeOriginalTitle: false);

                                return (
                                    Series: new StageSonarrSeries(
                                        Title: title,
                                        Year: GetJsonObjInt(node, "year"),
                                        TvdbId: tvdbId,
                                        ImdbId: GetJsonObjString(node, "imdbId"),
                                        SeriesType: GetJsonObjString(node, "seriesType") ?? "standard",
                                        Monitored: GetJsonObjBool(node, "monitored") ?? true,
                                        RootFolderPath: mappedRoot,
                                        SeriesPath: mappedSeriesPath,
                                        Episodes: stagedEpisodes,
                                        PosterUrl: ExtractArrPosterUrl(node),
                                        Overview: GetJsonObjString(node, "overview"),
                                        SeasonMonitored: seasonMonitored,
                                        OriginalLanguage: origLang,
                                        AltTitles: sonarrAltTitles),
                                    FilesCount: seriesFilesCount);
                            }

                            var fallbackOrigLang = ExtractArrOriginalLanguageIso2(node["originalLanguage"]);
                            var fallbackAltTitles = ExtractArrAlternateTitles(node, title, includeOriginalTitle: false);

                            return (
                                Series: new StageSonarrSeries(
                                    Title: title,
                                    Year: GetJsonObjInt(node, "year"),
                                    TvdbId: tvdbId,
                                    ImdbId: GetJsonObjString(node, "imdbId"),
                                    SeriesType: GetJsonObjString(node, "seriesType") ?? "standard",
                                    Monitored: GetJsonObjBool(node, "monitored") ?? true,
                                    RootFolderPath: mappedRoot,
                                    SeriesPath: mappedSeriesPath,
                                    Episodes: [],
                                    PosterUrl: ExtractArrPosterUrl(node),
                                    Overview: GetJsonObjString(node, "overview"),
                                    SeasonMonitored: seasonMonitored,
                                    OriginalLanguage: fallbackOrigLang,
                                    AltTitles: fallbackAltTitles),
                                FilesCount: 0);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    });

                    var results = await Task.WhenAll(seriesTasks);
                    foreach (var r in results)
                    {
                        sonarrSeries.Add(r.Series);
                        items++;
                        files += r.FilesCount;
                    }
                }
            }
            else if (effectiveKind == MigrationSourceKinds.Lidarr)
            {
                if (await TryGetJsonNodeAsync("/api/v1/artist") is JsonArray artistArr)
                {
                    foreach (var node in artistArr.OfType<JsonObject>())
                    {
                        var artistId = GetJsonObjInt(node, "id") ?? 0;
                        var artistName = GetJsonObjString(node, "artistName") ?? GetJsonObjString(node, "title") ?? "Unknown Artist";
                        var rawPath = GetJsonObjString(node, "path") ?? "/music";
                        var rawRoot = GetJsonObjString(node, "rootFolderPath") ?? Path.GetDirectoryName(rawPath) ?? "/music";
                        var mappedRoot = RemapPath(rawRoot, pathMap);

                        var tracks = new List<StageLidarrTrack>();
                        if (artistId > 0 && await TryGetJsonNodeAsync($"/api/v1/trackfile?artistId={artistId}") is JsonArray tfArr)
                        {
                            var tNum = 0;
                            foreach (var tf in tfArr.OfType<JsonObject>())
                            {
                                var tfPath = GetJsonObjString(tf, "path");
                                var tfSize = GetJsonObjLong(tf, "size") ?? 0L;
                                var mappedTfPath = !string.IsNullOrWhiteSpace(tfPath) ? RemapPath(tfPath, pathMap) : null;
                                if (mappedTfPath is not null)
                                {
                                    files++;
                                }
                                tracks.Add(new StageLidarrTrack(
                                    TrackNumber: ++tNum,
                                    Title: !string.IsNullOrWhiteSpace(tfPath) ? Path.GetFileNameWithoutExtension(tfPath) : $"Track {tNum}",
                                    FullDiskPath: mappedTfPath,
                                    FileSize: tfSize));
                            }
                        }

                        lidarrArtists.Add(new StageLidarrArtist(
                            Name: artistName,
                            ForeignArtistId: GetJsonObjString(node, "foreignArtistId"),
                            Monitored: GetJsonObjBool(node, "monitored") ?? true,
                            RootFolderPath: mappedRoot,
                            Tracks: tracks));
                        items++;
                    }
                }
            }
            else if (effectiveKind == MigrationSourceKinds.Readarr)
            {
                var authorsById = new Dictionary<int, (string Name, string RootPath)>();
                if (await TryGetJsonNodeAsync("/api/v1/author") is JsonArray authorArr)
                {
                    foreach (var aNode in authorArr.OfType<JsonObject>())
                    {
                        var aId = GetJsonObjInt(aNode, "id") ?? 0;
                        var aName = GetJsonObjString(aNode, "authorName") ?? "Unknown Author";
                        var aPath = GetJsonObjString(aNode, "path") ?? "/books";
                        var aRoot = GetJsonObjString(aNode, "rootFolderPath") ?? Path.GetDirectoryName(aPath) ?? "/books";
                        authorsById[aId] = (aName, RemapPath(aRoot, pathMap));
                    }
                }

                if (await TryGetJsonNodeAsync("/api/v1/book") is JsonArray bookArr)
                {
                    foreach (var bNode in bookArr.OfType<JsonObject>())
                    {
                        var bId = GetJsonObjInt(bNode, "id") ?? 0;
                        var aId = GetJsonObjInt(bNode, "authorId") ?? 0;
                        var (authorName, rootFolder) = authorsById.TryGetValue(aId, out var aInfo)
                            ? aInfo
                            : (GetJsonObjString(bNode["author"] as JsonObject, "authorName") ?? "Unknown Author", "/books");
                        var bookTitle = GetJsonObjString(bNode, "title") ?? $"Book {bId}";
                        var foreignBookId = GetJsonObjString(bNode, "foreignBookId");
                        string? isbn13 = null;
                        if (bNode["editions"] is JsonArray edArr)
                        {
                            isbn13 = edArr.OfType<JsonObject>()
                                .Select(e => GetJsonObjString(e, "isbn13"))
                                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                        }

                        var bookFiles = new List<StageReadarrBookFile>();
                        if (bId > 0 && await TryGetJsonNodeAsync($"/api/v1/bookfile?bookId={bId}") is JsonArray bfArr)
                        {
                            foreach (var bf in bfArr.OfType<JsonObject>())
                            {
                                var bfPath = GetJsonObjString(bf, "path");
                                if (string.IsNullOrWhiteSpace(bfPath)) continue;
                                var mappedBf = RemapPath(bfPath, pathMap);
                                var ext = Path.GetExtension(mappedBf);
                                var slotKind = AudiobookExtensions.Contains(ext) ||
                                               src.ReadarrSlotHint.Equals("audiobook", StringComparison.OrdinalIgnoreCase)
                                    ? "audiobook_slot"
                                    : "ebook_slot";
                                bookFiles.Add(new StageReadarrBookFile(
                                    SlotKind: slotKind,
                                    FullDiskPath: mappedBf,
                                    SizeBytes: GetJsonObjLong(bf, "size") ?? 0L,
                                    QualityName: ParseQualityLabel(bf["quality"]?.ToJsonString())));
                                files++;
                            }
                        }

                        readarrBooks.Add(new StageReadarrBook(
                            SourceName: src.Name,
                            SlotHint: src.ReadarrSlotHint,
                            AuthorName: authorName,
                            BookTitle: bookTitle,
                            ForeignBookId: foreignBookId,
                            Isbn13: isbn13,
                            Monitored: GetJsonObjBool(bNode, "monitored") ?? true,
                            RootFolderPath: rootFolder,
                            Files: bookFiles));
                        items++;
                    }
                }
            }
            else if (effectiveKind == MigrationSourceKinds.Overseerr)
            {
                var uNode = await TryGetJsonNodeAsync("/api/v1/user?take=200");
                var uResults = (uNode as JsonObject)?["results"] as JsonArray ?? uNode as JsonArray;
                if (uResults is not null)
                {
                    foreach (var u in uResults.OfType<JsonObject>())
                    {
                        var plexId = GetJsonObjLong(u, "plexId") ?? 0L;
                        if (plexId <= 0) continue;
                        overseerrUsers.Add(new StageOverseerrUser(
                            SeerrUserId: GetJsonObjLong(u, "id") ?? 0L,
                            PlexId: plexId,
                            Username: GetJsonObjString(u, "plexUsername") ?? GetJsonObjString(u, "displayName") ?? $"plex-{plexId}",
                            Email: GetJsonObjString(u, "email"),
                            Avatar: GetJsonObjString(u, "avatar")));
                    }
                }

                var reqNode = await TryGetJsonNodeAsync("/api/v1/request?take=500");
                var reqResults = (reqNode as JsonObject)?["results"] as JsonArray ?? reqNode as JsonArray;
                if (reqResults is not null)
                {
                    foreach (var r in reqResults.OfType<JsonObject>())
                    {
                        var mediaObj = r["media"] as JsonObject;
                        var reqByObj = r["requestedBy"] as JsonObject;
                        overseerrRequests.Add(new StageOverseerrRequest(
                            SeerrRequestId: GetJsonObjLong(r, "id") ?? 0L,
                            MediaType: GetJsonObjString(r, "type") ?? "movie",
                            Status: GetJsonObjInt(r, "status") ?? 2,
                            RequestedBySeerrUserId: GetJsonObjLong(reqByObj, "id") ?? 0L,
                            TmdbId: GetJsonObjInt(mediaObj, "tmdbId"),
                            TvdbId: GetJsonObjInt(mediaObj, "tvdbId")));
                        items++;
                    }
                }
            }

            inspected.Add(new SourceInspectionSummary(
                Name: src.Name,
                SourceKind: effectiveKind,
                TransportMode: src.TransportMode,
                DetectedSchemaVersion: null,
                DiscoveredRootFolders: discoveredRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ItemsDiscovered: items,
                FilesDiscovered: files));
            logs.Add($"Connected to live {appName} v{version} API '{src.Name}': {items} catalog items, {files} media files, {rawIndexers.Count} indexers, {rawClients.Count} download clients inspected.");
        }
        catch (Exception ex)
        {
            warnings.Add($"Live API source '{src.Name}' failed: {ex.Message}");
        }

        return (languageUpliftDelta, qualityProfilesDelta);
    }

    private static string ConvertArrFieldsArrayToSettingsJson(JsonObject resourceNode, string? definitionFallback = null)
    {
        var settingsObj = new JsonObject();
        if (resourceNode["settings"] is JsonObject directSettings)
        {
            foreach (var kv in directSettings)
            {
                if (kv.Value is JsonValue jv && jv.TryGetValue<string>(out var strVal))
                {
                    var k = kv.Key.ToLowerInvariant();
                    if ((k.Contains("key") || k.Contains("pass") || k.Contains("token") || k.Contains("secret") || IsMaskedArrSecret(strVal)) && IsMaskedArrSecret(strVal))
                    {
                        continue;
                    }
                }
                settingsObj[kv.Key] = kv.Value?.DeepClone();
            }
        }

        if (resourceNode["fields"] is JsonArray fieldsArr)
        {
            foreach (var field in fieldsArr.OfType<JsonObject>())
            {
                var fName = GetJsonObjString(field, "name");
                if (string.IsNullOrWhiteSpace(fName)) continue;
                var valNode = field["value"];
                if (valNode is null) continue;

                if (valNode is JsonValue jv && jv.TryGetValue<string>(out var strVal) && IsMaskedArrSecret(strVal))
                {
                    continue;
                }

                settingsObj[fName] = valNode.DeepClone();
            }
        }

        if (!string.IsNullOrWhiteSpace(definitionFallback) && !settingsObj.ContainsKey("definitionFile"))
        {
            settingsObj["definitionFile"] = definitionFallback;
        }

        return settingsObj.ToJsonString();
    }

    private static bool IsSafeBackupCandidateSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (HasDangerousSegmentChars(value)) return false;

        try
        {
            var unescaped = Uri.UnescapeDataString(value);
            if (HasDangerousSegmentChars(unescaped)) return false;
        }
        catch
        {
            return false;
        }

        return true;

        static bool HasDangerousSegmentChars(string s)
        {
            if (s.Contains("..", StringComparison.Ordinal)) return true;
            if (s.Contains('\\', StringComparison.Ordinal)) return true;
            if (s.StartsWith("//", StringComparison.Ordinal)) return true;
            foreach (var c in s)
            {
                if (char.IsControl(c)) return true;
            }
            return false;
        }
    }

    private static async Task<bool> CheckVpnResolvesPrivateAsync(
        ISafeHttpClientFactory safeHttpFactory,
        CancellationToken ct)
    {
        try
        {
            using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dnsCts.CancelAfter(TimeSpan.FromSeconds(2));
            var addrs = await Dns.GetHostAddressesAsync("vpn", dnsCts.Token);
            if (addrs is null || addrs.Length == 0) return false;

            foreach (var addr in addrs)
            {
                try
                {
                    safeHttpFactory.EnsureAddressPermitted(addr, "vpn", allowPrivateLan: true);
                }
                catch
                {
                    return false;
                }

                if (!IsPrivateIpAddress(addr))
                {
                    return false;
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPrivateIpAddress(IPAddress address)
    {
        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            return (bytes[0] & 0xfe) == 0xfc; // fc00::/7 ULA
        }

        var v4 = ip.GetAddressBytes();
        return v4[0] == 10
            || (v4[0] == 172 && v4[1] >= 16 && v4[1] <= 31)
            || (v4[0] == 192 && v4[1] == 168);
    }

    private static IReadOnlyList<string>? ExtractArrAlternateTitles(JsonObject node, string primaryTitle, bool includeOriginalTitle)
    {
        var results = new List<string>();
        var primaryTrimmed = primaryTitle.Trim();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var trimmed = raw.Trim();
            if (string.Equals(trimmed, primaryTrimmed, StringComparison.OrdinalIgnoreCase)) return;
            if (seen.Add(trimmed))
            {
                results.Add(trimmed);
            }
        }

        if (includeOriginalTitle)
        {
            TryAdd(GetJsonObjString(node, "originalTitle"));
        }

        if (node["alternateTitles"] is JsonArray altArr)
        {
            foreach (var item in altArr.OfType<JsonObject>())
            {
                TryAdd(GetJsonObjString(item, "title"));
            }
        }

        return results.Count > 0 ? results : null;
    }

    private static string? GetJsonObjString(JsonObject? obj, string key)
    {
        if (obj is null) return null;
        foreach (var kv in obj)
        {
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue jv)
            {
                if (jv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        }
        return null;
    }

    private static int? GetJsonObjInt(JsonObject? obj, string key)
    {
        if (obj is null) return null;
        foreach (var kv in obj)
        {
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue jv)
            {
                if (jv.TryGetValue<int>(out var i)) return i;
                if (jv.TryGetValue<long>(out var l)) return (int)l;
                if (jv.TryGetValue<string>(out var s) && int.TryParse(s, out var pi)) return pi;
            }
        }
        return null;
    }

    private static long? GetJsonObjLong(JsonObject? obj, string key)
    {
        if (obj is null) return null;
        foreach (var kv in obj)
        {
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue jv)
            {
                if (jv.TryGetValue<long>(out var l)) return l;
                if (jv.TryGetValue<int>(out var i)) return i;
                if (jv.TryGetValue<string>(out var s) && long.TryParse(s, out var pl)) return pl;
            }
        }
        return null;
    }

    private static bool? GetJsonObjBool(JsonObject? obj, string key)
    {
        if (obj is null) return null;
        foreach (var kv in obj)
        {
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue jv)
            {
                if (jv.TryGetValue<bool>(out var b)) return b;
                if (jv.TryGetValue<string>(out var s) && bool.TryParse(s, out var pb)) return pb;
            }
        }
        return null;
    }

}
