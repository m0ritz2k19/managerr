using System.IO.Compression;
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
    private static SqliteConnection OpenReadOnlySqlite(string dbPath)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        return new SqliteConnection(csb.ToString());
    }

    private static async Task<HashSet<string>> GetTableColumnsAsync(SqliteConnection conn, string tableName)
    {
        var rows = await conn.QueryAsync<PragmaColumnRow>($"PRAGMA table_info(\"{tableName.Replace("\"", "")}\");");
        return rows.Select(r => r.name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string tableName)
    {
        var exists = await conn.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND lower(name) = lower(@Name) LIMIT 1;",
            new { Name = tableName });
        return exists.HasValue;
    }

    private static async Task ParseNzbHydraYamlAsync(
        ResolvedMigrationSource src,
        List<SourceInspectionSummary> inspected,
        List<StageIndexerCandidate> rawIndexers,
        List<string> warnings,
        List<string> logs,
        CancellationToken ct)
    {
        if (!File.Exists(src.ResolvedLocalPath))
        {
            warnings.Add($"NZBHydra2 YAML file not found at '{src.ResolvedLocalPath}'.");
            return;
        }

        var lines = await File.ReadAllLinesAsync(src.ResolvedLocalPath, ct);
        string? currentName = null;
        string? currentHost = null;
        string? currentApiKey = null;
        string? currentType = null;
        var count = 0;

        void FlushCurrent()
        {
            if (!string.IsNullOrWhiteSpace(currentName) && !string.IsNullOrWhiteSpace(currentHost))
            {
                var proto = (currentType ?? "").Contains("torznab", StringComparison.OrdinalIgnoreCase) ? "torrent" : "usenet";
                var impl = proto == "torrent" ? "Torznab" : "Newznab";
                rawIndexers.Add(new StageIndexerCandidate(
                    SourceName: src.Name,
                    SourceKind: MigrationSourceKinds.NzbHydra2,
                    LegacyId: ++count,
                    IsCanonicalHub: true,
                    IsSyntheticProxyClone: false,
                    Request: new UpsertIndexerRequest(
                        Name: currentName.Trim(),
                        Implementation: impl,
                        DefinitionId: null,
                        Protocol: proto,
                        BaseUrl: currentHost.Trim(),
                        ApiKey: currentApiKey)));
            }
            currentName = null;
            currentHost = null;
            currentApiKey = null;
            currentType = null;
        }

        var inIndexers = false;
        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith('#') || string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (!rawLine.StartsWith(' ') && trimmed.StartsWith("indexers:", StringComparison.OrdinalIgnoreCase))
            {
                inIndexers = true;
                continue;
            }

            if (!rawLine.StartsWith(' ') && !trimmed.StartsWith('-'))
            {
                inIndexers = false;
            }

            if (!inIndexers)
            {
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushCurrent();
                trimmed = trimmed[2..].Trim();
            }

            var sep = trimmed.IndexOf(':');
            if (sep <= 0)
            {
                continue;
            }

            var key = trimmed[..sep].Trim();
            var val = trimmed[(sep + 1)..].Trim().Trim('"', '\'');
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                currentName = val;
            }
            else if (key.Equals("host", StringComparison.OrdinalIgnoreCase))
            {
                currentHost = val;
            }
            else if (key.Equals("apiKey", StringComparison.OrdinalIgnoreCase))
            {
                currentApiKey = val;
            }
            else if (key.Equals("searchModuleType", StringComparison.OrdinalIgnoreCase))
            {
                currentType = val;
            }
        }
        FlushCurrent();

        inspected.Add(new SourceInspectionSummary(src.Name, src.SourceKind, src.TransportMode, 2, [], count, 0));
        logs.Add($"Parsed NZBHydra2 config '{src.Name}': {count} canonical indexers.");
    }


    private static bool? GetNullableBoolSetting(JsonObject obj, string key)
    {
        foreach (var kv in obj)
        {
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue jv)
            {
                if (jv.TryGetValue<bool>(out var b))
                {
                    return b;
                }
                if (jv.TryGetValue<string>(out var s) && bool.TryParse(s, out var pb))
                {
                    return pb;
                }
            }
        }
        return null;
    }

    private static StageIndexerCandidate ParseLegacyIndexer(ResolvedMigrationSource src, LegacyIndexerRow row)
    {
        var settings = ParseSettingsJson(row.Settings);
        var baseUrl = GetStringSetting(settings, "baseUrl") ?? GetStringSetting(settings, "host") ?? "https://indexer.local";
        var apiKey = GetStringSetting(settings, "apiKey");
        if (IsMaskedArrSecret(apiKey))
        {
            apiKey = null;
        }
        var defFile = GetStringSetting(settings, "definitionFile");
        var cats = ExtractIntArraySetting(settings, "categories")
            .Concat(ExtractIntArraySetting(settings, "animeCategories"))
            .Distinct()
            .OrderBy(c => c)
            .ToList();

        var apiPath = GetStringSetting(settings, "apiPath");
        Dictionary<string, string>? extraSettings = null;
        if (!string.IsNullOrWhiteSpace(apiPath) && !apiPath.Equals("/api", StringComparison.OrdinalIgnoreCase))
        {
            extraSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["apiPath"] = apiPath.Trim(),
            };
        }

        var enableRss = row.EnableRss ?? GetNullableBoolSetting(settings, "enableRss") ?? true;
        var enableAuto = row.EnableAutomaticSearch ?? GetNullableBoolSetting(settings, "enableAutomaticSearch") ?? true;
        var enableInteractive = row.EnableInteractiveSearch ?? GetNullableBoolSetting(settings, "enableInteractiveSearch") ?? true;
        var priority = row.Priority ?? GetIntSetting(settings, "priority") ?? 25;

        var impl = string.IsNullOrWhiteSpace(row.Implementation) ? "Torznab" : row.Implementation.Trim();
        var protocol = impl.Contains("Newznab", StringComparison.OrdinalIgnoreCase) ||
                       impl.Contains("Nzb", StringComparison.OrdinalIgnoreCase)
            ? "usenet"
            : "torrent";

        if (impl.Equals("Cardigann", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(defFile))
        {
            impl = "Cardigann";
        }

        var isHub = src.SourceKind is MigrationSourceKinds.Prowlarr or MigrationSourceKinds.NzbHydra2;
        var name = string.IsNullOrWhiteSpace(row.Name) ? $"Indexer-{row.Id}" : row.Name.Trim();

        var isProxyClone = !isHub && (
            name.Contains("(Prowlarr)", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("(NZBHydra", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains("/prowlarr", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains(":9696/", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains(":5076/", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains("nzbhydra", StringComparison.OrdinalIgnoreCase));

        return new StageIndexerCandidate(
            SourceName: src.Name,
            SourceKind: src.SourceKind,
            LegacyId: row.Id,
            IsCanonicalHub: isHub,
            IsSyntheticProxyClone: isProxyClone,
            Request: new UpsertIndexerRequest(
                Name: Regex.Replace(name, @"\s*\((Prowlarr|NZBHydra2?)\)\s*$", "", RegexOptions.IgnoreCase).Trim(),
                Implementation: impl,
                DefinitionId: defFile,
                Protocol: protocol,
                BaseUrl: baseUrl,
                ApiKey: apiKey,
                ExtraSettings: extraSettings,
                Categories: cats.Count > 0 ? cats : null,
                Priority: priority,
                RssEnabled: enableRss,
                AutoSearchEnabled: enableAuto,
                InteractiveSearchEnabled: enableInteractive));
    }

    private static UpsertIndexerProxyRequest? ParseLegacyIndexerProxy(LegacySettingRow row)
    {
        var settings = ParseSettingsJson(row.Settings);
        var impl = (row.Implementation ?? "").Trim();
        var host = GetStringSetting(settings, "host") ?? "localhost";
        var port = GetIntSetting(settings, "port") ?? 8191;
        var user = GetStringSetting(settings, "username");
        var pass = GetStringSetting(settings, "password");
        if (IsMaskedArrSecret(pass))
        {
            pass = null;
        }

        if (impl.Contains("FlareSolverr", StringComparison.OrdinalIgnoreCase))
        {
            var reqUrl = GetStringSetting(settings, "host") ?? "http://flaresolverr:8191";
            if (Uri.TryCreate(reqUrl, UriKind.Absolute, out var uri))
            {
                host = uri.Host;
                port = uri.Port > 0 ? uri.Port : 8191;
            }
            return new UpsertIndexerProxyRequest(row.Name ?? "FlareSolverr", "flaresolverr", host, port);
        }

        var proxyType = impl.Contains("Socks5", StringComparison.OrdinalIgnoreCase) ? "socks5"
            : impl.Contains("Socks4", StringComparison.OrdinalIgnoreCase) ? "socks4"
            : "http";
        return new UpsertIndexerProxyRequest(row.Name ?? $"Proxy-{row.Id}", proxyType, host, port, user, pass);
    }

    private static StageDownloadClientCandidate? ParseLegacyDownloadClient(ResolvedMigrationSource src, LegacySettingRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Implementation))
        {
            return null;
        }

        var settings = ParseSettingsJson(row.Settings);
        var host = (GetStringSetting(settings, "host") ?? "localhost").Trim();
        var port = GetIntSetting(settings, "port") ?? 8080;
        var useSsl = GetBoolSetting(settings, "useSsl");
        var urlBase = GetStringSetting(settings, "urlBase");
        var username = GetStringSetting(settings, "username");
        var password = GetStringSetting(settings, "password");
        if (IsMaskedArrSecret(password))
        {
            password = null;
        }
        var apiKey = GetStringSetting(settings, "apiKey");
        if (IsMaskedArrSecret(apiKey))
        {
            apiKey = null;
        }

        var catMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tvCat = GetStringSetting(settings, "tvCategory") ?? (src.SourceKind == MigrationSourceKinds.Sonarr ? GetStringSetting(settings, "category") : null);
        var movCat = GetStringSetting(settings, "movieCategory") ?? (src.SourceKind == MigrationSourceKinds.Radarr ? GetStringSetting(settings, "category") : null);
        var musCat = GetStringSetting(settings, "musicCategory") ?? (src.SourceKind == MigrationSourceKinds.Lidarr ? GetStringSetting(settings, "category") : null);
        var bookCat = GetStringSetting(settings, "bookCategory") ?? (src.SourceKind == MigrationSourceKinds.Readarr ? GetStringSetting(settings, "category") : null);

        if (!string.IsNullOrWhiteSpace(tvCat))
        {
            catMap["tv"] = tvCat;
        }
        if (!string.IsNullOrWhiteSpace(movCat))
        {
            catMap["movies"] = movCat;
        }
        if (!string.IsNullOrWhiteSpace(musCat))
        {
            catMap["music"] = musCat;
        }
        if (!string.IsNullOrWhiteSpace(bookCat))
        {
            var slotKey = src.ReadarrSlotHint == "audiobook" ? "audiobooks" : "ebooks";
            catMap[slotKey] = bookCat;
        }

        var normalizedImpl = row.Implementation.Trim() switch
        {
            var s when s.Equals("TorrentBlackhole", StringComparison.OrdinalIgnoreCase) => "torrent_blackhole",
            var s when s.Equals("UsenetBlackhole", StringComparison.OrdinalIgnoreCase) => "usenet_blackhole",
            var s => s,
        };

        return new StageDownloadClientCandidate(
            SourceName: src.Name,
            Name: row.Name ?? row.Implementation,
            Implementation: normalizedImpl,
            Host: host.Trim(),
            Port: port,
            UseSsl: useSsl,
            UrlBase: urlBase,
            Username: username,
            Password: password,
            ApiKey: apiKey,
            Enabled: row.Enable != 0,
            CategoryMappings: catMap);
    }

    private static List<(UpsertDownloadClientRequest Request, List<string> MergedSources)> ConsolidateDownloadClients(
        List<StageDownloadClientCandidate> rawClients)
    {
        var groups = rawClients.GroupBy(c =>
            $"{c.Implementation.ToLowerInvariant()}|{c.Host.ToLowerInvariant()}|{c.Port}|{(c.Username ?? "").ToLowerInvariant()}");

        var result = new List<(UpsertDownloadClientRequest, List<string>)>();
        foreach (var g in groups)
        {
            var first = g.First();
            var mergedCats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sources = new List<string>();

            foreach (var item in g)
            {
                if (!sources.Contains(item.SourceName, StringComparer.OrdinalIgnoreCase))
                {
                    sources.Add(item.SourceName);
                }

                foreach (var kv in item.CategoryMappings)
                {
                    mergedCats[kv.Key] = kv.Value;
                }
            }

            var unifiedName = sources.Count > 1
                ? $"{first.Implementation} ({first.Host}:{first.Port})"
                : first.Name;

            var unmaskedPassword = g.Select(x => x.Password).FirstOrDefault(p => !IsMaskedArrSecret(p));
            var unmaskedApiKey = g.Select(x => x.ApiKey).FirstOrDefault(k => !IsMaskedArrSecret(k));
            var username = g.Select(x => x.Username).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)) ?? first.Username;
            var urlBase = g.Select(x => x.UrlBase).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)) ?? first.UrlBase;

            result.Add((
                new UpsertDownloadClientRequest(
                    Name: unifiedName,
                    Implementation: first.Implementation,
                    Host: first.Host,
                    Port: first.Port,
                    UseSsl: first.UseSsl,
                    UrlBase: urlBase,
                    Username: username,
                    Password: unmaskedPassword,
                    ApiKey: unmaskedApiKey,
                    CategoryMappings: mergedCats,
                    IsEnabled: g.Any(x => x.Enabled)),
                sources));
        }

        return result;
    }

    private static (List<(UpsertIndexerRequest Request, string CanonicalSource)> Deduped, int SkippedClones) DeduplicateIndexers(
        List<StageIndexerCandidate> rawIndexers,
        HashSet<int> prowlarrMappedAppIndexerIds)
    {
        var deduped = new List<(UpsertIndexerRequest Request, string CanonicalSource)>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        var hasCanonicalHubInBatch = rawIndexers.Any(x => x.IsCanonicalHub);

        foreach (var idx in rawIndexers)
        {
            if (!idx.IsCanonicalHub &&
                ((hasCanonicalHubInBatch && idx.IsSyntheticProxyClone) || prowlarrMappedAppIndexerIds.Contains(idx.LegacyId)))
            {
                skipped++;
                continue;
            }

            var key = $"{idx.Request.Implementation.ToLowerInvariant()}|{idx.Request.BaseUrl.TrimEnd('/').ToLowerInvariant()}|{(idx.Request.DefinitionId ?? idx.Request.Name).ToLowerInvariant()}";
            if (!seenKeys.Add(key) || !seenNames.Add(idx.Request.Name))
            {
                if (!idx.IsCanonicalHub)
                {
                    skipped++;
                }

                if (!IsMaskedArrSecret(idx.Request.ApiKey))
                {
                    var existingIdx = deduped.FindIndex(d =>
                        $"{d.Request.Implementation.ToLowerInvariant()}|{d.Request.BaseUrl.TrimEnd('/').ToLowerInvariant()}|{(d.Request.DefinitionId ?? d.Request.Name).ToLowerInvariant()}" == key ||
                        string.Equals(d.Request.Name, idx.Request.Name, StringComparison.OrdinalIgnoreCase));
                    if (existingIdx >= 0 && IsMaskedArrSecret(deduped[existingIdx].Request.ApiKey))
                    {
                        var req = deduped[existingIdx].Request;
                        deduped[existingIdx] = (req with { ApiKey = idx.Request.ApiKey }, deduped[existingIdx].CanonicalSource);
                    }
                }
                continue;
            }

            deduped.Add((idx.Request, idx.SourceName));
        }

        return (deduped, skipped);
    }

    private static async Task<(int Items, int Files)> ExtractSonarrCatalogAsync(
        SqliteConnection conn,
        HashSet<string> tables,
        IReadOnlyDictionary<string, string> pathMap,
        List<StageSonarrSeries> target)
    {
        if (!tables.Contains("Series"))
        {
            return (0, 0);
        }

        var epFilesById = new Dictionary<long, LegacyMediaFileRow>();
        if (tables.Contains("EpisodeFiles"))
        {
            var efCols = await GetTableColumnsAsync(conn, "EpisodeFiles");
            var langExpr = efCols.Contains("Languages") ? "Languages" : "NULL AS Languages";
            var miExpr = efCols.Contains("MediaInfo") ? "MediaInfo" : "NULL AS MediaInfo";
            var efRows = await conn.QueryAsync<LegacyMediaFileRow>(
                $"SELECT Id, RelativePath, Size, Quality, {langExpr}, {miExpr} FROM EpisodeFiles;");
            foreach (var ef in efRows)
            {
                epFilesById[ef.Id] = ef;
            }
        }

        var epsBySeries = new Dictionary<long, List<LegacyEpisodeRow>>();
        if (tables.Contains("Episodes"))
        {
            var epCols = await GetTableColumnsAsync(conn, "Episodes");
            var absExpr = epCols.Contains("AbsoluteEpisodeNumber") ? "AbsoluteEpisodeNumber" : "NULL AS AbsoluteEpisodeNumber";
            var epRows = await conn.QueryAsync<LegacyEpisodeRow>(
                $"SELECT Id, SeriesId, SeasonNumber, EpisodeNumber, Title, AirDateUtc, Monitored, EpisodeFileId, {absExpr} FROM Episodes;");
            foreach (var ep in epRows)
            {
                if (!epsBySeries.TryGetValue(ep.SeriesId, out var list))
                {
                    list = [];
                    epsBySeries[ep.SeriesId] = list;
                }
                list.Add(ep);
            }
        }

        var seriesCols = await GetTableColumnsAsync(conn, "Series");
        var seriesTypeExpr = seriesCols.Contains("SeriesType") ? "CAST(SeriesType AS TEXT)" : "'standard'";
        var seasonsExpr = seriesCols.Contains("Seasons") ? "Seasons" : "NULL AS Seasons";
        var origLangExpr = seriesCols.Contains("OriginalLanguage") ? "CAST(OriginalLanguage AS TEXT) AS OriginalLanguage" : "NULL AS OriginalLanguage";
        var origTitleExpr = seriesCols.Contains("OriginalTitle") ? "OriginalTitle" : "NULL AS OriginalTitle";
        var seriesRows = (await conn.QueryAsync<LegacySeriesRow>(
            $"SELECT Id, TvdbId, ImdbId, Title, Year, Path, Monitored, {seriesTypeExpr} AS SeriesType, {seasonsExpr}, {origLangExpr}, {origTitleExpr} FROM Series;")).ToList();

        var sceneAltsByTvdb = new Dictionary<int, List<string>>();
        if (tables.Contains("SceneMappings") && await TableExistsAsync(conn, "SceneMappings"))
        {
            var smCols = await GetTableColumnsAsync(conn, "SceneMappings");
            if (smCols.Contains("TvdbId") && (smCols.Contains("Title") || smCols.Contains("SearchTerm")))
            {
                var smTitleExpr = smCols.Contains("Title") ? "Title" : "NULL AS Title";
                var smSearchExpr = smCols.Contains("SearchTerm") ? "SearchTerm" : "NULL AS SearchTerm";
                var smRows = await conn.QueryAsync<(int TvdbId, string? Title, string? SearchTerm)>(
                    $"SELECT TvdbId, {smTitleExpr}, {smSearchExpr} FROM SceneMappings;");
                foreach (var sm in smRows)
                {
                    if (sm.TvdbId <= 0) continue;
                    if (!sceneAltsByTvdb.TryGetValue(sm.TvdbId, out var list))
                    {
                        list = [];
                        sceneAltsByTvdb[sm.TvdbId] = list;
                    }
                    if (!string.IsNullOrWhiteSpace(sm.Title)) list.Add(sm.Title.Trim());
                    if (!string.IsNullOrWhiteSpace(sm.SearchTerm)) list.Add(sm.SearchTerm.Trim());
                }
            }
        }

        var fileTotal = 0;
        foreach (var s in seriesRows)
        {
            var mappedSeriesPath = RemapPath(s.Path ?? "/tv/Unknown", pathMap);
            var rootFolder = Path.GetDirectoryName(mappedSeriesPath) ?? "/tv";
            var stagedEps = new List<StageSonarrEpisode>();

            Dictionary<int, bool>? seasonMonitored = null;
            if (!string.IsNullOrWhiteSpace(s.Seasons))
            {
                try
                {
                    if (JsonNode.Parse(s.Seasons) is JsonArray sArr)
                    {
                        seasonMonitored = new Dictionary<int, bool>();
                        foreach (var elem in sArr.OfType<JsonObject>())
                        {
                            var sn = GetJsonObjInt(elem, "seasonNumber");
                            var mon = GetJsonObjBool(elem, "monitored");
                            if (sn.HasValue && mon.HasValue)
                            {
                                seasonMonitored[sn.Value] = mon.Value;
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore parse errors on legacy SQLite seasons JSON
                }
            }

            if (epsBySeries.TryGetValue(s.Id, out var eps))
            {
                foreach (var ep in eps)
                {
                    string? fullDiskPath = null;
                    string? relPath = null;
                    long size = 0;
                    var quality = "WEBDL-1080p";
                    IReadOnlyList<string>? epAudioLangs = null;
                    IReadOnlyList<string>? epSubLangs = null;

                    if (ep.EpisodeFileId > 0 && epFilesById.TryGetValue(ep.EpisodeFileId, out var ef) && !string.IsNullOrWhiteSpace(ef.RelativePath))
                    {
                        relPath = ef.RelativePath.TrimStart('/', '\\');
                        fullDiskPath = Path.Combine(mappedSeriesPath, relPath);
                        size = ef.Size;
                        quality = ParseQualityLabel(ef.Quality);

                        if (!string.IsNullOrWhiteSpace(ef.Languages) || !string.IsNullOrWhiteSpace(ef.MediaInfo))
                        {
                            var fObj = new JsonObject();
                            if (!string.IsNullOrWhiteSpace(ef.Languages))
                            {
                                try { fObj["languages"] = JsonNode.Parse(ef.Languages); } catch { }
                            }
                            if (!string.IsNullOrWhiteSpace(ef.MediaInfo))
                            {
                                try { fObj["mediaInfo"] = JsonNode.Parse(ef.MediaInfo); } catch { }
                            }
                            var (aLangs, sLangs) = ExtractArrFileLanguages(fObj);
                            epAudioLangs = aLangs;
                            epSubLangs = sLangs;
                        }

                        fileTotal++;
                    }

                    stagedEps.Add(new StageSonarrEpisode(
                        SeasonNumber: ep.SeasonNumber,
                        EpisodeNumber: ep.EpisodeNumber,
                        Title: string.IsNullOrWhiteSpace(ep.Title) ? $"Episode {ep.EpisodeNumber}" : ep.Title,
                        AirDate: ep.AirDateUtc,
                        Monitored: ep.Monitored != 0,
                        RelativePathInSeries: relPath,
                        FullDiskPath: fullDiskPath,
                        FileSize: size,
                        QualityName: quality,
                        AudioLanguages: epAudioLangs,
                        SubtitleLanguages: epSubLangs,
                        AbsoluteEpisodeNumber: ep.AbsoluteEpisodeNumber));
                }
            }

            var sType = s.SeriesType switch
            {
                "2" or "Anime" or "anime" => "anime",
                _ => "standard",
            };

            var origLang = "en";
            if (!string.IsNullOrWhiteSpace(s.OriginalLanguage))
            {
                try
                {
                    var node = JsonNode.Parse(s.OriginalLanguage);
                    origLang = ExtractArrOriginalLanguageIso2(node);
                }
                catch
                {
                    origLang = NormalizeSingleLanguageNameToIso2(s.OriginalLanguage);
                }
            }

            var primaryTitle = s.Title ?? $"Series {s.Id}";
            var seriesAltList = new List<string>();
            if (!string.IsNullOrWhiteSpace(s.OriginalTitle))
            {
                seriesAltList.Add(s.OriginalTitle.Trim());
            }
            if (s.TvdbId > 0 && sceneAltsByTvdb.TryGetValue(s.TvdbId, out var sceneAlts))
            {
                seriesAltList.AddRange(sceneAlts);
            }
            var dedupedSeriesAlts = seriesAltList
                .Where(a => !string.IsNullOrWhiteSpace(a) && !string.Equals(a, primaryTitle, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            target.Add(new StageSonarrSeries(
                Title: primaryTitle,
                Year: s.Year > 0 ? s.Year : null,
                TvdbId: s.TvdbId,
                ImdbId: s.ImdbId,
                SeriesType: sType,
                Monitored: s.Monitored != 0,
                RootFolderPath: rootFolder,
                SeriesPath: mappedSeriesPath,
                Episodes: stagedEps,
                SeasonMonitored: seasonMonitored,
                OriginalLanguage: origLang,
                AltTitles: dedupedSeriesAlts.Length > 0 ? dedupedSeriesAlts : null));
        }

        return (seriesRows.Count, fileTotal);
    }

    private static async Task<(int Items, int Files)> ExtractRadarrCatalogAsync(
        SqliteConnection conn,
        HashSet<string> tables,
        IReadOnlyDictionary<string, string> pathMap,
        List<StageRadarrMovie> target)
    {
        if (!tables.Contains("Movies"))
        {
            return (0, 0);
        }

        var movieFiles = new Dictionary<long, LegacyMediaFileRow>();
        if (tables.Contains("MovieFiles"))
        {
            var mfCols = await GetTableColumnsAsync(conn, "MovieFiles");
            var langExpr = mfCols.Contains("Languages") ? "Languages" : "NULL AS Languages";
            var miExpr = mfCols.Contains("MediaInfo") ? "MediaInfo" : "NULL AS MediaInfo";
            var mfRows = await conn.QueryAsync<LegacyMediaFileRow>(
                $"SELECT Id, RelativePath, Size, Quality, {langExpr}, {miExpr} FROM MovieFiles;");
            foreach (var mf in mfRows)
            {
                movieFiles[mf.Id] = mf;
            }
        }

        var moviesCols = await GetTableColumnsAsync(conn, "Movies");
        var minAvailExpr = moviesCols.Contains("MinimumAvailability") ? "CAST(m.MinimumAvailability AS TEXT) AS MinimumAvailability" : "NULL AS MinimumAvailability";
        var hasMovieMetadata = tables.Contains("MovieMetadata") && await TableExistsAsync(conn, "MovieMetadata");

        List<LegacyMovieRow> rows;
        if (hasMovieMetadata)
        {
            var mmCols = await GetTableColumnsAsync(conn, "MovieMetadata");
            var origLangExpr = moviesCols.Contains("OriginalLanguage")
                ? "CAST(m.OriginalLanguage AS TEXT) AS OriginalLanguage"
                : (mmCols.Contains("OriginalLanguage") ? "CAST(mm.OriginalLanguage AS TEXT) AS OriginalLanguage" : "NULL AS OriginalLanguage");
            var origTitleExpr = mmCols.Contains("OriginalTitle")
                ? "mm.OriginalTitle AS OriginalTitle"
                : (moviesCols.Contains("OriginalTitle") ? "m.OriginalTitle AS OriginalTitle" : "NULL AS OriginalTitle");

            rows = (await conn.QueryAsync<LegacyMovieRow>(
                $"""
                SELECT m.Id, mm.Id AS MovieMetadataId, mm.TmdbId, mm.ImdbId, mm.Title, mm.Year, m.Path, m.Monitored, m.MovieFileId,
                       {minAvailExpr}, {origLangExpr}, {origTitleExpr}
                FROM Movies m
                JOIN MovieMetadata mm ON mm.Id = m.MovieMetadataId;
                """)).ToList();
        }
        else
        {
            var minAvailSimpleExpr = moviesCols.Contains("MinimumAvailability") ? "CAST(MinimumAvailability AS TEXT) AS MinimumAvailability" : "NULL AS MinimumAvailability";
            var origLangSimpleExpr = moviesCols.Contains("OriginalLanguage") ? "CAST(OriginalLanguage AS TEXT) AS OriginalLanguage" : "NULL AS OriginalLanguage";
            var origTitleSimpleExpr = moviesCols.Contains("OriginalTitle") ? "OriginalTitle" : "NULL AS OriginalTitle";

            rows = (await conn.QueryAsync<LegacyMovieRow>(
                $"SELECT Id, 0 AS MovieMetadataId, TmdbId, ImdbId, Title, Year, Path, Monitored, MovieFileId, {minAvailSimpleExpr}, {origLangSimpleExpr}, {origTitleSimpleExpr} FROM Movies;")).ToList();
        }

        var movieAltsByKey = new Dictionary<long, List<string>>();
        if (tables.Contains("AlternativeTitles") && await TableExistsAsync(conn, "AlternativeTitles"))
        {
            var atCols = await GetTableColumnsAsync(conn, "AlternativeTitles");
            var keyCol = hasMovieMetadata && atCols.Contains("MovieMetadataId")
                ? "MovieMetadataId"
                : (atCols.Contains("MovieId") ? "MovieId" : (atCols.Contains("MovieMetadataId") ? "MovieMetadataId" : null));
            if (keyCol is not null && atCols.Contains("Title"))
            {
                var atRows = await conn.QueryAsync<(long KeyId, string? Title)>(
                    $"SELECT {keyCol} AS KeyId, Title FROM AlternativeTitles;");
                foreach (var at in atRows)
                {
                    if (at.KeyId <= 0 || string.IsNullOrWhiteSpace(at.Title)) continue;
                    if (!movieAltsByKey.TryGetValue(at.KeyId, out var list))
                    {
                        list = [];
                        movieAltsByKey[at.KeyId] = list;
                    }
                    list.Add(at.Title.Trim());
                }
            }
        }

        var movieTransByKey = new Dictionary<long, List<string>>();
        if (tables.Contains("MovieTranslations") && await TableExistsAsync(conn, "MovieTranslations"))
        {
            var mtCols = await GetTableColumnsAsync(conn, "MovieTranslations");
            var keyCol = hasMovieMetadata && mtCols.Contains("MovieMetadataId")
                ? "MovieMetadataId"
                : (mtCols.Contains("MovieId") ? "MovieId" : (mtCols.Contains("MovieMetadataId") ? "MovieMetadataId" : null));
            if (keyCol is not null && mtCols.Contains("Title"))
            {
                var mtRows = await conn.QueryAsync<(long KeyId, string? Title)>(
                    $"SELECT {keyCol} AS KeyId, Title FROM MovieTranslations;");
                foreach (var mt in mtRows)
                {
                    if (mt.KeyId <= 0 || string.IsNullOrWhiteSpace(mt.Title)) continue;
                    if (!movieTransByKey.TryGetValue(mt.KeyId, out var list))
                    {
                        list = [];
                        movieTransByKey[mt.KeyId] = list;
                    }
                    list.Add(mt.Title.Trim());
                }
            }
        }

        var filesCount = 0;
        foreach (var m in rows)
        {
            var mappedMovieDir = RemapPath(m.Path ?? "/movies/Unknown", pathMap);
            var rootFolder = Path.GetDirectoryName(mappedMovieDir) ?? "/movies";
            string? fullDisk = null;
            string? relPath = null;
            long size = 0;
            var qual = "BluRay-1080p";
            IReadOnlyList<string>? audioLangs = null;
            IReadOnlyList<string>? subLangs = null;

            if (m.MovieFileId > 0 && movieFiles.TryGetValue(m.MovieFileId, out var mf) && !string.IsNullOrWhiteSpace(mf.RelativePath))
            {
                var fileName = mf.RelativePath.TrimStart('/', '\\');
                fullDisk = Path.Combine(mappedMovieDir, fileName);
                relPath = Path.Combine(Path.GetFileName(mappedMovieDir), fileName);
                size = mf.Size;
                qual = ParseQualityLabel(mf.Quality);

                if (!string.IsNullOrWhiteSpace(mf.Languages) || !string.IsNullOrWhiteSpace(mf.MediaInfo))
                {
                    var fObj = new JsonObject();
                    if (!string.IsNullOrWhiteSpace(mf.Languages))
                    {
                        try { fObj["languages"] = JsonNode.Parse(mf.Languages); } catch { }
                    }
                    if (!string.IsNullOrWhiteSpace(mf.MediaInfo))
                    {
                        try { fObj["mediaInfo"] = JsonNode.Parse(mf.MediaInfo); } catch { }
                    }
                    var (aLangs, sLangs) = ExtractArrFileLanguages(fObj);
                    audioLangs = aLangs;
                    subLangs = sLangs;
                }

                filesCount++;
            }

            var minAvail = NormalizeRadarrMinimumAvailability(m.MinimumAvailability);
            var origLang = "en";
            if (!string.IsNullOrWhiteSpace(m.OriginalLanguage))
            {
                try
                {
                    var node = JsonNode.Parse(m.OriginalLanguage);
                    origLang = ExtractArrOriginalLanguageIso2(node);
                }
                catch
                {
                    origLang = NormalizeSingleLanguageNameToIso2(m.OriginalLanguage);
                }
            }

            var primaryTitle = m.Title ?? $"Movie {m.Id}";
            var movieAltList = new List<string>();
            if (!string.IsNullOrWhiteSpace(m.OriginalTitle))
            {
                movieAltList.Add(m.OriginalTitle.Trim());
            }
            var lookupKey = m.MovieMetadataId > 0 ? m.MovieMetadataId : m.Id;
            if (movieAltsByKey.TryGetValue(lookupKey, out var altRows))
            {
                movieAltList.AddRange(altRows);
            }
            else if (lookupKey != m.Id && movieAltsByKey.TryGetValue(m.Id, out var fallbackAltRows))
            {
                movieAltList.AddRange(fallbackAltRows);
            }
            if (movieTransByKey.TryGetValue(lookupKey, out var transRows))
            {
                movieAltList.AddRange(transRows);
            }
            else if (lookupKey != m.Id && movieTransByKey.TryGetValue(m.Id, out var fallbackTransRows))
            {
                movieAltList.AddRange(fallbackTransRows);
            }

            var dedupedMovieAlts = movieAltList
                .Where(a => !string.IsNullOrWhiteSpace(a) && !string.Equals(a, primaryTitle, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            target.Add(new StageRadarrMovie(
                Title: primaryTitle,
                Year: m.Year > 0 ? m.Year : null,
                TmdbId: m.TmdbId,
                ImdbId: m.ImdbId,
                Monitored: m.Monitored != 0,
                RootFolderPath: rootFolder,
                FullDiskPath: fullDisk,
                RelativeFilePath: relPath,
                FileSize: size,
                QualityName: qual,
                MoviePath: mappedMovieDir,
                AudioLanguages: audioLangs,
                SubtitleLanguages: subLangs,
                MinimumAvailability: minAvail,
                OriginalLanguage: origLang,
                AltTitles: dedupedMovieAlts.Length > 0 ? dedupedMovieAlts : null));
        }

        return (rows.Count, filesCount);
    }

    private static async Task<(int Items, int Files)> ExtractLidarrCatalogAsync(
        SqliteConnection conn,
        HashSet<string> tables,
        IReadOnlyDictionary<string, string> pathMap,
        List<StageLidarrArtist> target)
    {
        if (!tables.Contains("Artists"))
        {
            return (0, 0);
        }

        var trackFiles = new Dictionary<long, LegacyMediaFileRow>();
        if (tables.Contains("TrackFiles"))
        {
            var tfCols = await GetTableColumnsAsync(conn, "TrackFiles");
            var pathCol = tfCols.Contains("Path") ? "Path AS RelativePath" : "RelativePath";
            var tfRows = await conn.QueryAsync<LegacyMediaFileRow>($"SELECT Id, {pathCol}, Size, Quality FROM TrackFiles;");
            foreach (var tf in tfRows)
            {
                trackFiles[tf.Id] = tf;
            }
        }

        List<LegacyArtistRow> artists;
        if (tables.Contains("ArtistMetadata"))
        {
            artists = (await conn.QueryAsync<LegacyArtistRow>(
                """
                SELECT a.Id, am.ForeignArtistId, am.Name, a.Path, a.Monitored
                FROM Artists a
                JOIN ArtistMetadata am ON am.Id = a.ArtistMetadataId;
                """)).ToList();
        }
        else
        {
            artists = (await conn.QueryAsync<LegacyArtistRow>(
                "SELECT Id, ForeignArtistId, Name, Path, Monitored FROM Artists;")).ToList();
        }

        var filesCount = 0;
        foreach (var a in artists)
        {
            var mappedDir = RemapPath(a.Path ?? "/music/Unknown", pathMap);
            var root = Path.GetDirectoryName(mappedDir) ?? "/music";
            var tracks = new List<StageLidarrTrack>();

            if (tables.Contains("Tracks"))
            {
                var trkCols = await GetTableColumnsAsync(conn, "Tracks");
                if (trkCols.Contains("ArtistMetadataId") || trkCols.Contains("ArtistId"))
                {
                    var fkCol = trkCols.Contains("ArtistId") ? "ArtistId" : "ArtistMetadataId";
                    var trRows = await conn.QueryAsync<LegacyTrackRow>(
                        $"SELECT Id, Title, TrackFileId FROM Tracks WHERE {fkCol} = @ArtistId;",
                        new { ArtistId = a.Id });
                    var num = 0;
                    foreach (var tr in trRows)
                    {
                        num++;
                        string? diskPath = null;
                        long sz = 0;
                        if (tr.TrackFileId > 0 && trackFiles.TryGetValue(tr.TrackFileId, out var tf) && !string.IsNullOrWhiteSpace(tf.RelativePath))
                        {
                            diskPath = Path.IsPathRooted(tf.RelativePath)
                                ? RemapPath(tf.RelativePath, pathMap)
                                : Path.Combine(mappedDir, tf.RelativePath);
                            sz = tf.Size;
                            filesCount++;
                        }
                        tracks.Add(new StageLidarrTrack(num, tr.Title ?? $"Track {num}", diskPath, sz));
                    }
                }
            }

            target.Add(new StageLidarrArtist(
                Name: a.Name ?? $"Artist {a.Id}",
                ForeignArtistId: a.ForeignArtistId,
                Monitored: a.Monitored != 0,
                RootFolderPath: root,
                Tracks: tracks));
        }

        return (artists.Count, filesCount);
    }

    private static async Task<(int Items, int Files)> ExtractReadarrCatalogAsync(
        ResolvedMigrationSource src,
        SqliteConnection conn,
        HashSet<string> tables,
        IReadOnlyDictionary<string, string> pathMap,
        List<StageReadarrBook> target)
    {
        if (!tables.Contains("Books"))
        {
            return (0, 0);
        }

        var authorsById = new Dictionary<long, (string Name, string Path)>();
        if (tables.Contains("Authors"))
        {
            if (tables.Contains("AuthorMetadata"))
            {
                var aRows = await conn.QueryAsync<LegacyAuthorRow>(
                    """
                    SELECT a.Id, am.Name, a.Path
                    FROM Authors a
                    JOIN AuthorMetadata am ON am.Id = a.AuthorMetadataId;
                    """);
                foreach (var a in aRows)
                {
                    authorsById[a.Id] = (a.Name ?? "Unknown Author", RemapPath(a.Path ?? "/books/Unknown", pathMap));
                }
            }
            else
            {
                var aRows = await conn.QueryAsync<LegacyAuthorRow>("SELECT Id, Name, Path FROM Authors;");
                foreach (var a in aRows)
                {
                    authorsById[a.Id] = (a.Name ?? "Unknown Author", RemapPath(a.Path ?? "/books/Unknown", pathMap));
                }
            }
        }

        var bookCols = await GetTableColumnsAsync(conn, "Books");
        var authorFk = bookCols.Contains("AuthorId") ? "AuthorId" : "AuthorMetadataId";
        var books = (await conn.QueryAsync<LegacyBookRow>(
            $"SELECT Id, {authorFk} AS AuthorId, ForeignBookId, Title, Monitored FROM Books;")).ToList();

        var filesByEditionOrBook = new List<LegacyBookFileRow>();
        if (tables.Contains("BookFiles"))
        {
            var bfCols = await GetTableColumnsAsync(conn, "BookFiles");
            var bookKeyExpr = bfCols.Contains("BookId") ? "BookId" : "EditionId AS BookId";
            filesByEditionOrBook = (await conn.QueryAsync<LegacyBookFileRow>(
                $"SELECT Id, {bookKeyExpr}, Path, Size, Quality FROM BookFiles;")).ToList();
        }

        // Map EditionId -> BookId when Editions table exists and BookFiles uses EditionId
        var editionToBook = new Dictionary<long, (long BookId, string? Isbn13)>();
        if (tables.Contains("Editions"))
        {
            var edCols = await GetTableColumnsAsync(conn, "Editions");
            var isbnExpr = edCols.Contains("Isbn13") ? "Isbn13" : "NULL AS Isbn13";
            var edRows = await conn.QueryAsync<LegacyEditionRow>($"SELECT Id, BookId, {isbnExpr} FROM Editions;");
            foreach (var ed in edRows)
            {
                editionToBook[ed.Id] = (ed.BookId, ed.Isbn13);
            }
        }

        var filesCount = 0;
        foreach (var b in books)
        {
            var (authorName, authorPath) = authorsById.TryGetValue(b.AuthorId, out var auth)
                ? auth
                : ("Unknown Author", RemapPath("/books/Unknown", pathMap));
            var rootFolder = Path.GetDirectoryName(authorPath) ?? "/books";

            var isbn = editionToBook.Values.FirstOrDefault(x => x.BookId == b.Id).Isbn13;
            var matchedFiles = filesByEditionOrBook
                .Where(bf => bf.BookId == b.Id || (editionToBook.TryGetValue(bf.BookId, out var eb) && eb.BookId == b.Id))
                .ToList();

            var stagedFiles = new List<StageReadarrBookFile>();
            foreach (var bf in matchedFiles)
            {
                if (string.IsNullOrWhiteSpace(bf.Path))
                {
                    continue;
                }

                var mappedFile = Path.IsPathRooted(bf.Path)
                    ? RemapPath(bf.Path, pathMap)
                    : Path.Combine(authorPath, bf.Path);

                var ext = Path.GetExtension(mappedFile);
                var isAudio = src.ReadarrSlotHint == "audiobook" ||
                              AudiobookExtensions.Contains(ext) ||
                              (!EbookExtensions.Contains(ext) && src.Name.Contains("audio", StringComparison.OrdinalIgnoreCase));

                stagedFiles.Add(new StageReadarrBookFile(
                    SlotKind: isAudio ? "audiobook_slot" : "ebook_slot",
                    FullDiskPath: mappedFile,
                    SizeBytes: bf.Size,
                    QualityName: isAudio ? "M4B-Audiobook" : "EPUB-Retail"));
                filesCount++;
            }

            target.Add(new StageReadarrBook(
                SourceName: src.Name,
                SlotHint: src.ReadarrSlotHint,
                AuthorName: authorName,
                BookTitle: b.Title ?? $"Book {b.Id}",
                ForeignBookId: b.ForeignBookId,
                Isbn13: isbn,
                Monitored: b.Monitored != 0,
                RootFolderPath: rootFolder,
                Files: stagedFiles));
        }

        return (books.Count, filesCount);
    }

    private static (List<UnifiedStagedBook> Unified, List<DualReadarrMergePreview> Previews) MergeReadarrBooks(
        List<StageReadarrBook> rawBooks)
    {
        var groups = rawBooks.GroupBy(b =>
            !string.IsNullOrWhiteSpace(b.ForeignBookId)
                ? $"fid:{b.ForeignBookId.Trim().ToLowerInvariant()}"
                : !string.IsNullOrWhiteSpace(b.Isbn13)
                    ? $"isbn:{b.Isbn13.Trim()}"
                    : $"at:{b.AuthorName.Trim().ToLowerInvariant()}::{b.BookTitle.Trim().ToLowerInvariant()}");

        var unified = new List<UnifiedStagedBook>();
        var previews = new List<DualReadarrMergePreview>();

        foreach (var g in groups)
        {
            var first = g.First();
            var allFiles = g.SelectMany(x => x.Files)
                .GroupBy(f => f.FullDiskPath, StringComparer.OrdinalIgnoreCase)
                .Select(fg => fg.First())
                .ToList();

            var distinctSources = g.Select(x => x.SourceName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var hasEbook = allFiles.Any(f => f.SlotKind == "ebook_slot");
            var hasAudio = allFiles.Any(f => f.SlotKind == "audiobook_slot");

            if (distinctSources > 1 || (hasEbook && hasAudio))
            {
                previews.Add(new DualReadarrMergePreview(
                    BookTitle: first.BookTitle,
                    AuthorName: first.AuthorName,
                    HasEbookSlotFile: hasEbook,
                    HasAudiobookSlotFile: hasAudio,
                    GoodreadsIdCrosswalked: first.ForeignBookId));
            }

            unified.Add(new UnifiedStagedBook(
                AuthorName: first.AuthorName,
                BookTitle: first.BookTitle,
                ForeignBookId: first.ForeignBookId,
                Isbn13: g.Select(x => x.Isbn13).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                Monitored: g.Any(x => x.Monitored),
                RootFolderPath: first.RootFolderPath,
                Files: allFiles));
        }

        return (unified, previews);
    }

    private static async Task<(int Items, int Files)> ExtractOverseerrStateAsync(
        SqliteConnection conn,
        HashSet<string> tables,
        List<StageOverseerrUser> users,
        List<StageOverseerrRequest> requests)
    {
        if (tables.Contains("user"))
        {
            var uCols = await GetTableColumnsAsync(conn, "user");
            var plexUserCol = uCols.Contains("plexUsername") ? "plexUsername" : uCols.Contains("username") ? "username AS plexUsername" : "email AS plexUsername";
            var uRows = await conn.QueryAsync<LegacyOverseerrUserRow>(
                $"SELECT id, plexId, {plexUserCol}, email, avatar FROM \"user\" WHERE plexId IS NOT NULL AND plexId > 0;");
            foreach (var u in uRows)
            {
                users.Add(new StageOverseerrUser(
                    SeerrUserId: u.id,
                    PlexId: u.plexId,
                    Username: string.IsNullOrWhiteSpace(u.plexUsername) ? $"plex-{u.plexId}" : u.plexUsername,
                    Email: u.email,
                    Avatar: u.avatar));
            }
        }

        if (tables.Contains("media_request"))
        {
            var mrCols = await GetTableColumnsAsync(conn, "media_request");
            if (tables.Contains("media") && mrCols.Contains("mediaId"))
            {
                var reqRows = await conn.QueryAsync<LegacyOverseerrRequestRow>(
                    """
                    SELECT r.id, r.type, r.status, r.requestedById, m.tmdbId, m.tvdbId
                    FROM media_request r
                    LEFT JOIN media m ON m.id = r.mediaId;
                    """);
                foreach (var r in reqRows)
                {
                    requests.Add(new StageOverseerrRequest(
                        SeerrRequestId: r.id,
                        MediaType: r.type ?? "movie",
                        Status: r.status,
                        RequestedBySeerrUserId: r.requestedById,
                        TmdbId: r.tmdbId,
                        TvdbId: r.tvdbId));
                }
            }
            else if (mrCols.Contains("tmdbId"))
            {
                var reqRows = await conn.QueryAsync<LegacyOverseerrRequestRow>(
                    "SELECT id, type, status, requestedById, tmdbId, NULL AS tvdbId FROM media_request;");
                foreach (var r in reqRows)
                {
                    requests.Add(new StageOverseerrRequest(
                        r.id, r.type ?? "movie", r.status, r.requestedById, r.tmdbId, null));
                }
            }
        }

        return (users.Count + requests.Count, 0);
    }


    private sealed class PragmaColumnRow
    {
        public string name { get; init; } = "";
    }

    private sealed class LegacySettingRow
    {
        public int Id { get; init; }
        public string? Name { get; init; }
        public string? Implementation { get; init; }
        public string? Settings { get; init; }
        public int Enable { get; init; }
    }

    private sealed class LegacyRemotePathRow
    {
        public string? Host { get; init; }
        public string? RemotePath { get; init; }
        public string? LocalPath { get; init; }
    }

    private sealed class LegacyIndexerRow
    {
        public int Id { get; init; }
        public string? Name { get; init; }
        public string? Implementation { get; init; }
        public string? Settings { get; init; }
        public string? ConfigContract { get; init; }
        public bool? EnableRss { get; init; }
        public bool? EnableAutomaticSearch { get; init; }
        public bool? EnableInteractiveSearch { get; init; }
        public int? Priority { get; init; }
    }

    private sealed class LegacyCustomFormatRow
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public int IncludeCustomFormatWhenRenaming { get; init; }
        public string? Specifications { get; init; }
    }

    private sealed class LegacyBlocklistRow
    {
        public string? SourceTitle { get; init; }
        public string? TorrentInfoHash { get; init; }
    }

    private sealed class LegacyMediaFileRow
    {
        public long Id { get; init; }
        public string? RelativePath { get; init; }
        public long Size { get; init; }
        public string? Quality { get; init; }
        public string? Languages { get; init; }
        public string? MediaInfo { get; init; }
    }

    private sealed class LegacySeriesRow
    {
        public long Id { get; init; }
        public int TvdbId { get; init; }
        public string? ImdbId { get; init; }
        public string? Title { get; init; }
        public int Year { get; init; }
        public string? Path { get; init; }
        public int Monitored { get; init; }
        public string? SeriesType { get; init; }
        public string? Seasons { get; init; }
        public string? OriginalLanguage { get; init; }
        public string? OriginalTitle { get; init; }
    }

    private sealed class LegacyEpisodeRow
    {
        public long Id { get; init; }
        public long SeriesId { get; init; }
        public int SeasonNumber { get; init; }
        public int EpisodeNumber { get; init; }
        public int? AbsoluteEpisodeNumber { get; init; }
        public string? Title { get; init; }
        public string? AirDateUtc { get; init; }
        public int Monitored { get; init; }
        public long EpisodeFileId { get; init; }
    }

    private sealed class LegacyMovieRow
    {
        public long Id { get; init; }
        public long MovieMetadataId { get; init; }
        public int TmdbId { get; init; }
        public string? ImdbId { get; init; }
        public string? Title { get; init; }
        public int Year { get; init; }
        public string? Path { get; init; }
        public int Monitored { get; init; }
        public long MovieFileId { get; init; }
        public string? MinimumAvailability { get; init; }
        public string? OriginalLanguage { get; init; }
        public string? OriginalTitle { get; init; }
    }

    private sealed class LegacyArtistRow
    {
        public long Id { get; init; }
        public string? ForeignArtistId { get; init; }
        public string? Name { get; init; }
        public string? Path { get; init; }
        public int Monitored { get; init; }
    }

    private sealed class LegacyTrackRow
    {
        public long Id { get; init; }
        public string? Title { get; init; }
        public long TrackFileId { get; init; }
    }

    private sealed class LegacyAuthorRow
    {
        public long Id { get; init; }
        public string? Name { get; init; }
        public string? Path { get; init; }
    }

    private sealed class LegacyBookRow
    {
        public long Id { get; init; }
        public long AuthorId { get; init; }
        public string? ForeignBookId { get; init; }
        public string? Title { get; init; }
        public int Monitored { get; init; }
    }

    private sealed class LegacyEditionRow
    {
        public long Id { get; init; }
        public long BookId { get; init; }
        public string? Isbn13 { get; init; }
    }

    private sealed class LegacyBookFileRow
    {
        public long Id { get; init; }
        public long BookId { get; init; }
        public string? Path { get; init; }
        public long Size { get; init; }
        public string? Quality { get; init; }
    }

    private sealed class LegacyKeyValueRow
    {
        public string? key { get; init; }
        public string? value { get; init; }
    }

    private sealed class LegacyOverseerrUserRow
    {
        public long id { get; init; }
        public long plexId { get; init; }
        public string? plexUsername { get; init; }
        public string? email { get; init; }
        public string? avatar { get; init; }
    }

    private sealed class LegacyOverseerrRequestRow
    {
        public long id { get; init; }
        public string? type { get; init; }
        public int status { get; init; }
        public long requestedById { get; init; }
        public int? tmdbId { get; init; }
        public int? tvdbId { get; init; }
    }
}
