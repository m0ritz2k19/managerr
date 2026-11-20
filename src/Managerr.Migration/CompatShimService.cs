using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Dapper;
using Managerr.Catalog;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Downloaders;
using Managerr.Downloaders.Parser;
using Managerr.Indexers;
using Managerr.Indexers.Categories;
using Managerr.Indexers.Torznab;
using Managerr.Metadata;
using Managerr.Profiles;
using Managerr.Security.Users;

namespace Managerr.Migration;

public sealed record CompatTagDto(int Id, string Label);

public interface ICompatShimService
{
    object BuildSystemStatus(string emulatedApp, string emulatedVersion, string urlBase);
    Task<IReadOnlyList<object>> ListRootFoldersAsync(string verticalGroup, CancellationToken ct = default);
    Task<IReadOnlyList<object>> ListQualityProfilesAsync(string verticalGroup, CancellationToken ct = default);
    IReadOnlyList<object> ListLegacyLanguageOrMetadataProfiles(string kind);

    Task<IReadOnlyList<CompatTagDto>> ListTagsAsync(CancellationToken ct = default);
    Task<CompatTagDto?> GetTagByIdAsync(int id, CancellationToken ct = default);
    Task<CompatTagDto> GetOrCreateTagAsync(string label, CancellationToken ct = default);
    Task<CompatTagDto?> RenameTagAsync(int id, string label, CancellationToken ct = default);
    Task<PlexUserSummary?> ResolveOverseerrPlexUserAsync(
        IReadOnlyList<int>? tagIds,
        long? explicitPlexUserId,
        string? explicitUsername,
        CancellationToken ct = default);

    Task<IReadOnlyList<object>> ListSonarrSeriesAsync(int? tvdbId = null, CancellationToken ct = default);
    Task<object?> GetSonarrSeriesByIdAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<object>> LookupSonarrSeriesAsync(string? term, CancellationToken ct = default);
    Task<object> AddSonarrSeriesAsync(JsonObject body, long? headerPlexUserId, CancellationToken ct = default);
    Task<object?> UpdateSonarrSeriesAsync(long? routeId, JsonObject body, long? headerPlexUserId, CancellationToken ct = default);
    Task<object?> UpdateSonarrSeriesAsync(long? routeId, JsonObject body, long? headerPlexUserId, bool mergeCompatTagsAndMonitoring, CancellationToken ct = default);
    Task<bool> DeleteSonarrSeriesAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<object>> ListSonarrEpisodesAsync(long? seriesId, int? seasonNumber = null, CancellationToken ct = default);
    Task<IReadOnlyList<object>> ListSonarrEpisodeFilesAsync(long? seriesId, CancellationToken ct = default);
    Task<IReadOnlyList<object>> MonitorSonarrEpisodesAsync(IReadOnlyList<long> episodeIds, bool monitored, CancellationToken ct = default);
    Task<object> ExecuteSonarrCommandAsync(JsonObject body, CancellationToken ct = default);

    Task<IReadOnlyList<object>> ListRadarrMoviesAsync(int? tmdbId = null, CancellationToken ct = default);
    Task<object?> GetRadarrMovieByIdAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<object>> LookupRadarrMoviesAsync(string? term, CancellationToken ct = default);
    Task<object?> LookupRadarrMovieByTmdbAsync(int tmdbId, CancellationToken ct = default);
    Task<object?> LookupRadarrMovieByImdbAsync(string imdbId, CancellationToken ct = default);
    Task<object> AddRadarrMovieAsync(JsonObject body, long? headerPlexUserId, CancellationToken ct = default);
    Task<object?> UpdateRadarrMovieAsync(long? routeId, JsonObject body, long? headerPlexUserId, CancellationToken ct = default);
    Task<object?> UpdateRadarrMovieAsync(long? routeId, JsonObject body, long? headerPlexUserId, bool mergeCompatTags, CancellationToken ct = default);
    Task<bool> DeleteRadarrMovieAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<object>> ListRadarrMovieFilesAsync(long? movieId, CancellationToken ct = default);
    Task<object> ExecuteRadarrCommandAsync(JsonObject body, CancellationToken ct = default);

    Task<IReadOnlyList<object>> ListLidarrArtistsAsync(CancellationToken ct = default);
    Task<object> AddLidarrArtistAsync(JsonObject body, CancellationToken ct = default);
    Task<IReadOnlyList<object>> ListLidarrAlbumsAsync(long? artistId, CancellationToken ct = default);

    Task<string> ResolveReadarrTargetSlotAsync(string? querySlot, string? headerSlot, string? apiKeyPrefix, CancellationToken ct = default);
    Task<IReadOnlyList<object>> ListReadarrAuthorsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<object>> ListReadarrBooksAsync(string targetSlot, CancellationToken ct = default);
    Task<object> AddReadarrBookAsync(JsonObject body, string targetSlot, CancellationToken ct = default);

    Task<IReadOnlyList<object>> ListCompatQueueAsync(string? verticalGroup = null, CancellationToken ct = default);
    Task<IReadOnlyList<object>> ListProwlarrIndexersAsync(CancellationToken ct = default);
    Task<(string ContentType, string Payload)> ExecuteTorznabRequestAsync(
        long indexerId,
        string mode,
        string? query,
        string? catCsv,
        string? imdbId,
        int? tmdbId,
        int? tvdbId,
        int? season,
        int? episode,
        string? outputFormat,
        CancellationToken ct = default);
}

public sealed partial class CompatShimService(
    ISqliteDbFactory dbFactory,
    IAppSettingsService settingsService,
    IPolymorphicCatalogService catalogService,
    IComposableProfileService profileService,
    IUnifiedIndexerService indexerService,
    IUnifiedDownloadOrchestrator downloadOrchestrator,
    IPlexUserService plexUserService,
    IMetadataFederationService metadataFederation,
    IReleaseParserRouter parserRouter,
    ICatalogDiskScanHook? diskScanHook = null) : ICompatShimService
{
    private const string TagsSettingKey = "compat.tags_json";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim TagGate = new(1, 1);
    private static readonly SemaphoreSlim AddWorkGate = new(1, 1);

    public object BuildSystemStatus(string emulatedApp, string emulatedVersion, string urlBase) =>
        new
        {
            appName = emulatedApp,
            instanceName = $"Managerr ({emulatedApp} Compatibility Shim)",
            version = emulatedVersion,
            buildTime = "2026-11-01T00:00:00Z",
            isDebug = false,
            isProduction = true,
            isAdmin = true,
            isUserInteractive = false,
            startupPath = "/app",
            appData = "/config",
            osName = "linux",
            isDocker = true,
            runtimeVersion = Environment.Version.ToString(),
            runtimeName = ".NET",
            databaseType = "sqLite",
            databaseVersion = "3.46.1",
            authentication = "external",
            migrationVersion = 243,
            urlBase,
        };

    public async Task<IReadOnlyList<object>> ListRootFoldersAsync(string verticalGroup, CancellationToken ct = default)
    {
        var libs = await catalogService.ListLibrariesAsync(null, ct);
        var filtered = libs.Where(l => MatchesVerticalGroup(l.MediaType, verticalGroup)).ToList();

        if (filtered.Count == 0)
        {
            var fallbackPath = verticalGroup switch
            {
                "sonarr" => "/tv",
                "radarr" => "/movies",
                "lidarr" => "/music",
                "readarr" => "/books",
                _ => "/media",
            };
            var (fbFree, fbTotal, fbAccessible) = ProbeDiskSpace(fallbackPath);
            return
            [
                new
                {
                    id = 1L,
                    path = fallbackPath,
                    accessible = fbAccessible,
                    freeSpace = fbFree,
                    totalSpace = fbTotal,
                    unmappedFolders = Array.Empty<object>(),
                },
            ];
        }

        return filtered.Select(l =>
        {
            var (freeSpace, totalSpace, accessible) = ProbeDiskSpace(l.RootFolderPath);
            return (object)new
            {
                id = l.Id,
                path = l.RootFolderPath,
                accessible,
                freeSpace,
                totalSpace,
                unmappedFolders = Array.Empty<object>(),
            };
        }).ToList();
    }

    // Sonarr and Radarr name the sources of a quality differently ("web" vs "webdl", "television" vs "tv")
    private static string QualitySource(string qualityName, string verticalGroup)
    {
        var n = qualityName.ToLowerInvariant();
        var sonarr = verticalGroup == "sonarr";
        return n switch
        {
            _ when n.StartsWith("remux") || n.Contains("bluray-") && n.Contains("remux") => sonarr ? "blurayRaw" : "bluray",
            _ when n.StartsWith("bluray") => "bluray",
            _ when n.StartsWith("webrip") => sonarr ? "webRip" : "webrip",
            _ when n.StartsWith("webdl") || n.StartsWith("web") => sonarr ? "web" : "webdl",
            _ when n.StartsWith("rawhd") => sonarr ? "televisionRaw" : "tv",
            _ when n.StartsWith("hdtv") || n.StartsWith("sdtv") || n.StartsWith("sd") => sonarr ? "television" : "tv",
            _ when n.StartsWith("dvd") => "dvd",
            _ => "unknown",
        };
    }

    private static int QualityResolution(string qualityName)
    {
        var m = Regex.Match(qualityName, @"(\d{3,4})p", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    public async Task<IReadOnlyList<object>> ListQualityProfilesAsync(string verticalGroup, CancellationToken ct = default)
    {
        var profiles = await profileService.ListProfilesAsync(ct);
        var filtered = profiles.Where(p => MatchesProfileVerticalGroup(p.MediaVertical, verticalGroup)).ToList();
        if (filtered.Count == 0)
        {
            filtered = profiles.ToList();
        }

        return filtered.Select(p => (object)new
        {
            id = p.Id,
            name = p.Name,
            upgradeAllowed = true,
            cutoff = 1,
            minFormatScore = p.MinTraitScore,
            cutoffFormatScore = 1000,
            language = new { id = 1, name = p.LanguagePolicy?.Name ?? "Any" },
            items = p.AllowedQualities.Select((q, idx) => new
            {
                id = idx + 1,
                quality = new { id = idx + 1, name = q.Name, source = QualitySource(q.Name, verticalGroup), resolution = QualityResolution(q.Name) },
                items = Array.Empty<object>(),
                allowed = true,
            }).ToArray(),
            formatItems = p.CustomFormatScores.Select(kv => new
            {
                format = kv.Key,
                name = $"CF-{kv.Key}",
                score = kv.Value,
            }).ToArray(),
        }).ToList();
    }

    public IReadOnlyList<object> ListLegacyLanguageOrMetadataProfiles(string kind) =>
    [
        new
        {
            id = 1,
            name = kind == "language" ? "Managerr Unified Language Policy" : "Standard Metadata Profile",
            upgradeAllowed = true,
            cutoff = new { id = 1, name = "Any" },
            languages = new[]
            {
                new
                {
                    language = new { id = 1, name = "Any" },
                    allowed = true,
                },
            },
        },
    ];

    public async Task<IReadOnlyList<CompatTagDto>> ListTagsAsync(CancellationToken ct = default)
    {
        var raw = await settingsService.GetAsync(TagsSettingKey, ct);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }
        return JsonSerializer.Deserialize<List<CompatTagDto>>(raw, JsonOpts) ?? [];
    }

    public async Task<CompatTagDto?> GetTagByIdAsync(int id, CancellationToken ct = default)
    {
        var tags = await ListTagsAsync(ct);
        return tags.FirstOrDefault(t => t.Id == id);
    }

    public async Task<CompatTagDto> GetOrCreateTagAsync(string label, CancellationToken ct = default)
    {
        await TagGate.WaitAsync(ct);
        try
        {
            var clean = label.Trim();
            var existing = (await ListTagsAsync(ct)).ToList();
            var found = existing.FirstOrDefault(t => t.Label.Equals(clean, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                return found;
            }

            var nextId = existing.Count == 0 ? 1 : existing.Max(t => t.Id) + 1;
            var created = new CompatTagDto(nextId, clean);
            existing.Add(created);
            await settingsService.SetAsync(TagsSettingKey, JsonSerializer.Serialize(existing, JsonOpts), isSecret: false, ct);
            return created;
        }
        finally
        {
            TagGate.Release();
        }
    }

    /// <summary>PUT /tag/{id}: Seerr renames its per-user tags ("1 - alice") when a username changes.</summary>
    public async Task<CompatTagDto?> RenameTagAsync(int id, string label, CancellationToken ct = default)
    {
        await TagGate.WaitAsync(ct);
        try
        {
            var tags = (await ListTagsAsync(ct)).ToList();
            var index = tags.FindIndex(t => t.Id == id);
            if (index < 0)
            {
                return null;
            }

            var renamed = new CompatTagDto(id, label.Trim());
            tags[index] = renamed;
            await settingsService.SetAsync(TagsSettingKey, JsonSerializer.Serialize(tags, JsonOpts), isSecret: false, ct);
            return renamed;
        }
        finally
        {
            TagGate.Release();
        }
    }

    public async Task<PlexUserSummary?> ResolveOverseerrPlexUserAsync(
        IReadOnlyList<int>? tagIds,
        long? explicitPlexUserId,
        string? explicitUsername,
        CancellationToken ct = default)
    {
        if (explicitPlexUserId.HasValue && explicitPlexUserId.Value > 0)
        {
            var byId = await plexUserService.GetByPlexUserIdAsync(explicitPlexUserId.Value, ct);
            if (byId is not null)
            {
                return byId;
            }
        }

        var allUsers = await plexUserService.ListUsersAsync(ct);
        if (!string.IsNullOrWhiteSpace(explicitUsername))
        {
            var byName = allUsers.FirstOrDefault(u => u.Username.Equals(explicitUsername.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName;
            }
        }

        if (tagIds is { Count: > 0 })
        {
            var tags = await ListTagsAsync(ct);
            var matchedLabels = tags.Where(t => tagIds.Contains(t.Id)).Select(t => t.Label).ToList();

            foreach (var label in matchedLabels)
            {
                // Overseerr formats user tags as "<plexId> - <username>", "plexid-<plexId>", or "<username>"
                var numMatch = Regex.Match(label, @"(?:plexid[-_:]?|^)(\d+)\b", RegexOptions.IgnoreCase);
                if (numMatch.Success && long.TryParse(numMatch.Groups[1].Value, out var pid))
                {
                    var u = allUsers.FirstOrDefault(x => x.PlexUserId == pid || x.SeerrUserId == pid);
                    if (u is not null)
                    {
                        return u;
                    }
                }

                foreach (var u in allUsers)
                {
                    if (label.Equals(u.Username, StringComparison.OrdinalIgnoreCase) ||
                        label.EndsWith($" - {u.Username}", StringComparison.OrdinalIgnoreCase) ||
                        label.EndsWith($"-{u.Username}", StringComparison.OrdinalIgnoreCase))
                    {
                        return u;
                    }
                }
            }
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // Sonarr v3 Shim (/compat/sonarr/api/v3/*)
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<object>> ListSonarrSeriesAsync(int? tvdbId = null, CancellationToken ct = default)
    {
        var works = (await catalogService.ListWorksAsync(null, null, null, ct))
            .Where(w => w.WorkKind is "tv" or "anime")
            .ToList();

        if (tvdbId.HasValue && tvdbId.Value > 0)
        {
            var tvdbStr = tvdbId.Value.ToString(CultureInfo.InvariantCulture);
            works = works
                .Where(w => string.Equals(w.ExternalIds.GetValueOrDefault("tvdb"), tvdbStr, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var libs = (await catalogService.ListLibrariesAsync(null, ct)).ToDictionary(l => l.Id);
        var allTags = await ListTagsAsync(ct);
        var results = new List<object>(works.Count);

        foreach (var w in works)
        {
            var tree = await catalogService.GetWorkTreeAsync(w.Id, null, ct);
            if (tree is not null)
            {
                results.Add(FormatSonarrSeriesResource(tree, libs.GetValueOrDefault(w.LibraryId), allTags));
            }
        }

        return results;
    }

    public async Task<object?> GetSonarrSeriesByIdAsync(long id, CancellationToken ct = default)
    {
        var tree = await catalogService.GetWorkTreeAsync(id, null, ct);
        if (tree is null || tree.Work.WorkKind is not ("tv" or "anime"))
        {
            return null;
        }
        var lib = await catalogService.GetLibraryByIdAsync(tree.Work.LibraryId, ct);
        var allTags = await ListTagsAsync(ct);
        return FormatSonarrSeriesResource(tree, lib, allTags);
    }

    public async Task<IReadOnlyList<object>> LookupSonarrSeriesAsync(string? term, CancellationToken ct = default)
    {
        var cleanTerm = (term ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleanTerm))
        {
            return [];
        }

        var existingWorks = (await catalogService.ListWorksAsync(null, null, null, ct))
            .Where(w => w.WorkKind is "tv" or "anime")
            .ToList();
        var libs = (await catalogService.ListLibrariesAsync(null, ct)).ToDictionary(l => l.Id);
        var allTags = await ListTagsAsync(ct);

        // 1. Direct ID lookup check against local catalog first (e.g. "tvdb:460458", "imdb:tt...", "tmdb:...")
        var localIdMatch = FindExistingSeriesByLookupTerm(existingWorks, cleanTerm);
        if (localIdMatch is not null)
        {
            var localTree = await catalogService.GetWorkTreeAsync(localIdMatch.Id, null, ct);
            if (localTree is not null)
            {
                return [FormatSonarrSeriesResource(localTree, libs.GetValueOrDefault(localIdMatch.LibraryId), allTags)];
            }
        }

        // 2. Query federated metadata providers and hydrate seasons[]
        var hits = await metadataFederation.SearchWaterfallAsync(cleanTerm, "tv", null, ct);
        var output = new List<object>(hits.Count);

        for (var idx = 0; idx < hits.Count; idx++)
        {
            var h = hits[idx];
            var matchedExisting = FindMatchingCatalogWork(existingWorks, h.ExternalIds, titleFallback: null);
            if (matchedExisting is not null)
            {
                var localTree = await catalogService.GetWorkTreeAsync(matchedExisting.Id, null, ct);
                if (localTree is not null)
                {
                    output.Add(FormatSonarrSeriesResource(localTree, libs.GetValueOrDefault(matchedExisting.LibraryId), allTags));
                    continue;
                }
            }

            var candidate = h;
            if (candidate.Containers.Count == 0 && candidate.ExternalIds.Count > 0 && idx < 5)
            {
                try
                {
                    var resolved = await metadataFederation.ResolveWorkAsync("tv", candidate.ProviderKey, candidate.ExternalIds, null, ct);
                    if (resolved is not null && resolved.Containers.Count > 0)
                    {
                        candidate = resolved;
                    }
                }
                catch
                {
                    // Fallback to synthesized Season 1 below when offline
                }
            }

            output.Add(FormatSonarrLookupCandidate(candidate));
        }

        return output;
    }

    public async Task<object> AddSonarrSeriesAsync(JsonObject body, long? headerPlexUserId, CancellationToken ct = default)
    {
        await AddWorkGate.WaitAsync(ct);
        try
        {
            var title = body["title"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Untitled Series";
        }

        var tvdbId = body["tvdbId"]?.GetValue<int?>() ?? 0;
        var tmdbId = body["tmdbId"]?.GetValue<int?>() ?? 0;
        var imdbId = body["imdbId"]?.GetValue<string>()?.Trim();
        var year = body["year"]?.GetValue<int?>();
        var overview = body["overview"]?.GetValue<string>();
        var posterUrl = ExtractPosterUrlFromJson(body);
        var monitored = body["monitored"]?.GetValue<bool?>() ?? true;
        var qualityProfileId = body["qualityProfileId"]?.GetValue<long?>() ?? body["profileId"]?.GetValue<long?>();
        var rootFolderPath = body["rootFolderPath"]?.GetValue<string>();
        var seriesType = body["seriesType"]?.GetValue<string>() ?? "standard";
        var tagIds = ExtractIntList(body["tags"]);
        var bodyPlexId = body["plexUserId"]?.GetValue<long?>() ?? headerPlexUserId;
        var requestedBy = body["requestedByUsername"]?.GetValue<string>();
        var requestedSeasons = ExtractSonarrSeasonSpecs(body["seasons"]);
        var addOptions = body["addOptions"] as JsonObject;
        var monitorOption = addOptions?["monitor"]?.GetValue<string>();
        var searchForMissing = addOptions?["searchForMissingEpisodes"]?.GetValue<bool?>() ?? false;

        var extIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tvdbId > 0)
        {
            extIds["tvdb"] = tvdbId.ToString(CultureInfo.InvariantCulture);
        }
        if (tmdbId > 0)
        {
            extIds["tmdb"] = tmdbId.ToString(CultureInfo.InvariantCulture);
        }
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            extIds["imdb"] = imdbId;
        }

        // Deduplicate against existing TV/Anime works by tvdbId / imdbId / tmdbId
        var existingSeries = (await catalogService.ListWorksAsync(null, null, null, ct))
            .Where(w => w.WorkKind is "tv" or "anime")
            .ToList();
        var duplicate = FindMatchingCatalogWork(existingSeries, extIds, titleFallback: null);
        if (duplicate is not null)
        {
            var merged = await UpdateSonarrSeriesAsync(duplicate.Id, body, headerPlexUserId, mergeCompatTagsAndMonitoring: true, ct);
            if (merged is not null)
            {
                return merged;
            }
        }

        var vertical = seriesType.Equals("anime", StringComparison.OrdinalIgnoreCase) ? "anime" : "tv";
        var lib = await EnsureShimLibraryAsync(vertical, rootFolderPath ?? "/tv", ct);
        var requester = await ResolveOverseerrPlexUserAsync(tagIds, bodyPlexId, requestedBy, ct);
        var allTags = await ListTagsAsync(ct);
        var resolvedTagLabels = ResolveTagLabels(tagIds, allTags);
        var validProfileOverrideId = await ResolveValidProfileIdAsync(qualityProfileId, ct);

        // Hydrate real seasons & episodes from MetadataFederationService when external IDs are known
        MetadataWorkCandidate? hydrated = null;
        if (extIds.Count > 0)
        {
            try
            {
                hydrated = await metadataFederation.ResolveWorkAsync(vertical, "tvdb", extIds, lib.MetadataWaterfall, ct);
                if ((hydrated is null || hydrated.Containers.Count == 0) && tvdbId > 0)
                {
                    var wf = await metadataFederation.SearchWaterfallAsync($"tvdb:{tvdbId}", vertical, lib.MetadataWaterfall, ct);
                    hydrated = wf.FirstOrDefault();
                }
            }
            catch
            {
                // Keep request payload fields if metadata lookup is offline
            }
        }

        if (hydrated?.ExternalIds is not null)
        {
            foreach (var (k, v) in hydrated.ExternalIds)
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    extIds.TryAdd(k, v);
                }
            }
        }

        requestedSeasons = await ExpandTmdbCollapsedAnimeSeasonsIfApplicableAsync(
            vertical,
            extIds,
            AdaptContainers(hydrated?.Containers),
            requestedSeasons,
            ct);

        var seedContainers = BuildMergedSonarrSeedContainers(hydrated?.Containers, requestedSeasons);

        var tree = await catalogService.AddWorkAsync(
            new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: !string.IsNullOrWhiteSpace(hydrated?.Title) && title == "Untitled Series" ? hydrated.Title : title,
                WorkKind: vertical,
                PrimaryProvider: tvdbId > 0 ? "tvdb" : "auto",
                AltTitles: hydrated?.AltTitles,
                OriginalLanguage: hydrated?.OriginalLanguage ?? "en",
                Year: year ?? hydrated?.Year,
                Status: hydrated?.Status ?? "continuing",
                Overview: !string.IsNullOrWhiteSpace(overview) ? overview : (hydrated?.Overview ?? ""),
                PosterUrl: !string.IsNullOrWhiteSpace(posterUrl) ? posterUrl : hydrated?.PosterUrl,
                Monitored: monitored,
                ExternalIds: extIds,
                Tags: resolvedTagLabels,
                Containers: seedContainers,
                MonitorMode: MapSonarrMonitorMode(monitorOption, monitored),
                InitialProfileOverrideId: validProfileOverrideId),
            null,
            ct);

        if (requestedSeasons.Count > 0)
        {
            await ApplyRequestedSeasonMonitoringAsync(tree, requestedSeasons, validProfileOverrideId, ct: ct);
            tree = (await catalogService.GetWorkTreeAsync(tree.Work.Id, null, ct))!;
        }

        if (requester is not null)
        {
            await RecordCompatRequestAndLanguagePromotionAsync(tree.Work.Id, lib.Id, vertical, tree.Work.Title, extIds, requester, ct);
            tree = (await catalogService.GetWorkTreeAsync(tree.Work.Id, null, ct))!;
        }

        if (searchForMissing && tree.Work.Monitored)
        {
            var targetSeasons = tree.Containers.Where(c => c.Monitored).Select(c => c.ContainerNumber).ToHashSet();
            await TryAutoSearchSeriesAsync(tree, lib, targetSeasons, ct);
        }

        allTags = await ListTagsAsync(ct);
        return FormatSonarrSeriesResource(tree, lib, allTags);
    }
    finally
    {
        AddWorkGate.Release();
    }
}

    public Task<object?> UpdateSonarrSeriesAsync(long? routeId, JsonObject body, long? headerPlexUserId, CancellationToken ct = default) =>
        UpdateSonarrSeriesAsync(routeId, body, headerPlexUserId, false, ct);

    public async Task<object?> UpdateSonarrSeriesAsync(long? routeId, JsonObject body, long? headerPlexUserId, bool mergeCompatTagsAndMonitoring, CancellationToken ct = default)
    {
        var id = routeId ?? body["id"]?.GetValue<long?>();
        MediaWorkDetailTree? tree = null;

        if (id is > 0)
        {
            tree = await catalogService.GetWorkTreeAsync(id.Value, null, ct);
        }

        if (tree is null)
        {
            var tvdbId = body["tvdbId"]?.GetValue<int?>() ?? 0;
            if (tvdbId > 0)
            {
                var existing = (await catalogService.ListWorksAsync(null, null, null, ct))
                    .FirstOrDefault(w => w.WorkKind is "tv" or "anime" &&
                                         w.ExternalIds.GetValueOrDefault("tvdb") == tvdbId.ToString(CultureInfo.InvariantCulture));
                if (existing is not null)
                {
                    tree = await catalogService.GetWorkTreeAsync(existing.Id, null, ct);
                }
            }
        }

        if (tree is null || tree.Work.WorkKind is not ("tv" or "anime"))
        {
            return null;
        }

        var monitored = body["monitored"]?.GetValue<bool?>() ?? tree.Work.Monitored;
        var qualityProfileId = body["qualityProfileId"]?.GetValue<long?>() ?? body["profileId"]?.GetValue<long?>();
        var validProfileOverrideId = await ResolveValidProfileIdAsync(qualityProfileId, ct);

        var allTags = await ListTagsAsync(ct);
        IReadOnlyList<string>? updatedTagLabels = null;
        var tagIds = ExtractIntList(body["tags"]);
        if (body.ContainsKey("tags"))
        {
            var incomingLabels = ResolveTagLabels(tagIds, allTags);
            if (mergeCompatTagsAndMonitoring)
            {
                updatedTagLabels = tree.Work.Tags.Concat(incomingLabels).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            else
            {
                // Preserve non-compat native tags while reflecting the updated compat tag list from Seerr
                var compatKnownLabels = allTags.Select(t => t.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var preservedNative = tree.Work.Tags.Where(t => !compatKnownLabels.Contains(t));
                updatedTagLabels = preservedNative.Concat(incomingLabels).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        tree = await catalogService.UpdateWorkAsync(
            tree.Work.Id,
            new UpdateMediaWorkRequest(
                Monitored: monitored,
                Tags: updatedTagLabels),
            ct);

        var requestedSeasons = ExtractSonarrSeasonSpecs(body["seasons"]);
        var previouslyMonitored = tree.Containers.Where(c => c.Monitored).Select(c => c.ContainerNumber).ToHashSet();

        if (previouslyMonitored.Count == 0 && requestedSeasons.Count > 0)
        {
            requestedSeasons = await ExpandTmdbCollapsedAnimeSeasonsIfApplicableAsync(
                tree.Work.WorkKind,
                tree.Work.ExternalIds,
                AdaptContainers(tree.Containers),
                requestedSeasons,
                ct);
        }

        if (requestedSeasons.Count > 0)
        {
            var hadSpecialsBefore = tree.Containers.Any(c => c.ContainerNumber == 0);

            // Ensure any newly requested season numbers exist in the work tree
            var missingSeasonNums = requestedSeasons
                .Select(s => s.SeasonNumber)
                .Where(sn => tree.Containers.All(c => c.ContainerNumber != sn))
                .ToList();

            if (missingSeasonNums.Count > 0)
            {
                try
                {
                    tree = await catalogService.RefreshWorkMetadataAsync(tree.Work.Id, null, null, ct);
                    if (!hadSpecialsBefore && requestedSeasons.All(s => s.SeasonNumber != 0))
                    {
                        var unrequestedSpecials = tree.Containers.FirstOrDefault(c => c.ContainerNumber == 0 && c.Units.All(u => u.Files.Count == 0));
                        if (unrequestedSpecials is not null)
                        {
                            await dbFactory.ExecuteWriteAsync(
                                DatabaseKind.Core,
                                async (conn, tx) =>
                                {
                                    await conn.ExecuteAsync("DELETE FROM media_units WHERE container_id = @Cid;", new { Cid = unrequestedSpecials.Id }, transaction: tx);
                                    await conn.ExecuteAsync("DELETE FROM media_containers WHERE id = @Cid;", new { Cid = unrequestedSpecials.Id }, transaction: tx);
                                },
                                ct);
                            tree = (await catalogService.GetWorkTreeAsync(tree.Work.Id, null, ct))!;
                        }
                    }
                }
                catch
                {
                    // Offline fallback handled below
                }

                var stillMissing = requestedSeasons
                    .Where(s => tree.Containers.All(c => c.ContainerNumber != s.SeasonNumber))
                    .ToList();

                if (stillMissing.Count > 0)
                {
                    await InsertPlaceholderSeasonsAsync(tree.Work.Id, stillMissing, validProfileOverrideId, ct);
                    tree = (await catalogService.GetWorkTreeAsync(tree.Work.Id, null, ct))!;
                }
            }

            await ApplyRequestedSeasonMonitoringAsync(tree, requestedSeasons, validProfileOverrideId, preserveExistingMonitoring: mergeCompatTagsAndMonitoring, ct: ct);
            tree = (await catalogService.GetWorkTreeAsync(tree.Work.Id, null, ct))!;
        }

        var bodyPlexId = body["plexUserId"]?.GetValue<long?>() ?? headerPlexUserId;
        var requestedBy = body["requestedByUsername"]?.GetValue<string>();
        var requester = await ResolveOverseerrPlexUserAsync(tagIds, bodyPlexId, requestedBy, ct);
        if (requester is not null)
        {
            var extCopy = new Dictionary<string, string>(tree.Work.ExternalIds, StringComparer.OrdinalIgnoreCase);
            await RecordCompatRequestAndLanguagePromotionAsync(tree.Work.Id, tree.Work.LibraryId, tree.Work.WorkKind, tree.Work.Title, extCopy, requester, ct);
            tree = (await catalogService.GetWorkTreeAsync(tree.Work.Id, null, ct))!;
        }

        var searchForMissing = (body["addOptions"] as JsonObject)?["searchForMissingEpisodes"]?.GetValue<bool?>() ?? false;
        var lib = await catalogService.GetLibraryByIdAsync(tree.Work.LibraryId, ct);

        if (searchForMissing && tree.Work.Monitored)
        {
            var newlyMonitored = tree.Containers
                .Where(c => c.Monitored && !previouslyMonitored.Contains(c.ContainerNumber))
                .Select(c => c.ContainerNumber)
                .ToHashSet();
            if (newlyMonitored.Count == 0)
            {
                newlyMonitored = tree.Containers.Where(c => c.Monitored).Select(c => c.ContainerNumber).ToHashSet();
            }
            await TryAutoSearchSeriesAsync(tree, lib, newlyMonitored, ct);
        }

        allTags = await ListTagsAsync(ct);
        return FormatSonarrSeriesResource(tree, lib, allTags);
    }

    public async Task<bool> DeleteSonarrSeriesAsync(long id, CancellationToken ct = default)
    {
        var tree = await catalogService.GetWorkTreeAsync(id, null, ct);
        if (tree is null || tree.Work.WorkKind is not ("tv" or "anime"))
        {
            return false;
        }
        await catalogService.DeleteWorkAsync(id, ct);
        return true;
    }

    public async Task<IReadOnlyList<object>> ListSonarrEpisodesAsync(long? seriesId, int? seasonNumber = null, CancellationToken ct = default)
    {
        var works = seriesId.HasValue
            ? [await catalogService.GetWorkTreeAsync(seriesId.Value, null, ct)]
            : await LoadAllSeriesTreesAsync(ct);
        return EnumerateSonarrEpisodes(works, seasonNumber).Select(e => e.Resource).ToList();
    }

    private static IEnumerable<(long Id, object Resource)> EnumerateSonarrEpisodes(IEnumerable<MediaWorkDetailTree?> works, int? seasonNumber)
    {
        foreach (var tree in works.Where(t => t is not null))
        {
            foreach (var c in tree!.Containers)
            {
                if (seasonNumber.HasValue && c.ContainerNumber != seasonNumber.Value)
                {
                    continue;
                }

                foreach (var u in c.Units)
                {
                    yield return (u.Id, new
                    {
                        id = u.Id,
                        seriesId = tree.Work.Id,
                        seasonNumber = c.ContainerNumber,
                        episodeNumber = u.UnitNumber,
                        absoluteEpisodeNumber = u.AbsoluteNumber,
                        title = u.Title,
                        airDateUtc = u.AirOrReleaseDate,
                        hasFile = u.Files.Count > 0,
                        episodeFileId = u.Files.FirstOrDefault()?.Id ?? 0,
                        monitored = u.Monitored,
                    });
                }
            }
        }
    }

    /// <summary>
    /// PUT /episode/monitor: Seerr re-monitors the episodes of a season a user requests again. Like Sonarr it answers
    /// with the updated episodes; unknown ids are skipped.
    /// </summary>
    public async Task<IReadOnlyList<object>> MonitorSonarrEpisodesAsync(IReadOnlyList<long> episodeIds, bool monitored, CancellationToken ct = default)
    {
        var updated = new HashSet<long>();
        foreach (var id in episodeIds.Distinct())
        {
            try
            {
                await catalogService.UpdateUnitAsync(id, new UpdateMediaUnitRequest(Monitored: monitored), ct);
                updated.Add(id);
            }
            catch (KeyNotFoundException)
            {
                // Sonarr ignores ids it does not know as well
            }
        }

        return EnumerateSonarrEpisodes(await LoadAllSeriesTreesAsync(ct), null)
            .Where(e => updated.Contains(e.Id))
            .Select(e => e.Resource)
            .ToList();
    }

    public async Task<IReadOnlyList<object>> ListSonarrEpisodeFilesAsync(long? seriesId, CancellationToken ct = default)
    {
        var works = seriesId.HasValue
            ? [await catalogService.GetWorkTreeAsync(seriesId.Value, null, ct)]
            : await LoadAllSeriesTreesAsync(ct);
        var libs = (await catalogService.ListLibrariesAsync(null, ct)).ToDictionary(l => l.Id);

        var files = new List<object>();
        foreach (var tree in works.Where(t => t is not null))
        {
            var root = libs.GetValueOrDefault(tree!.Work.LibraryId)?.RootFolderPath ?? "/tv";
            foreach (var c in tree.Containers)
            {
                foreach (var u in c.Units)
                {
                    foreach (var f in u.Files)
                    {
                        files.Add(new
                        {
                            id = f.Id,
                            seriesId = tree.Work.Id,
                            seasonNumber = c.ContainerNumber,
                            relativePath = f.RelativePath,
                            path = Path.Combine(root, f.RelativePath),
                            size = f.SizeBytes,
                            quality = new { quality = new { id = 1, name = f.QualityName } },
                            languages = f.DetectedAudioLangs.Select((l, idx) => new { id = idx + 1, name = l }).ToArray(),
                        });
                    }
                }
            }
        }
        return files;
    }

    public async Task<object> ExecuteSonarrCommandAsync(JsonObject body, CancellationToken ct = default)
    {
        var name = body["name"]?.GetValue<string>() ?? "SeriesSearch";
        var seriesId = body["seriesId"]?.GetValue<long?>();
        var seasonNumber = body["seasonNumber"]?.GetValue<int?>();
        var episodeIds = ExtractLongList(body["episodeIds"]);
        var now = DateTimeOffset.UtcNow.ToString("O");

        if (string.Equals(name, "RefreshSeries", StringComparison.OrdinalIgnoreCase) && seriesId is > 0)
        {
            try
            {
                await catalogService.RefreshWorkMetadataAsync(seriesId.Value, null, null, ct);
            }
            catch
            {
                // Non-fatal in compat command handler
            }
        }
        else if (string.Equals(name, "RescanSeries", StringComparison.OrdinalIgnoreCase) && seriesId is > 0)
        {
            if (diskScanHook is not null)
            {
                await diskScanHook.ScanWorkDiskFilesAsync(seriesId.Value, ct);
            }
        }
        else if (string.Equals(name, "SeriesSearch", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name, "SeasonSearch", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name, "MissingEpisodeSearch", StringComparison.OrdinalIgnoreCase))
        {
            if (seriesId is > 0)
            {
                var tree = await catalogService.GetWorkTreeAsync(seriesId.Value, null, ct);
                if (tree is not null)
                {
                    var lib = await catalogService.GetLibraryByIdAsync(tree.Work.LibraryId, ct);
                    var targetSeasons = seasonNumber.HasValue
                        ? new HashSet<int> { seasonNumber.Value }
                        : tree.Containers.Where(c => c.Monitored).Select(c => c.ContainerNumber).ToHashSet();
                    await TryAutoSearchSeriesAsync(tree, lib, targetSeasons, ct);
                }
            }
        }
        else if (string.Equals(name, "EpisodeSearch", StringComparison.OrdinalIgnoreCase) && episodeIds.Count > 0)
        {
            var trees = await LoadAllSeriesTreesAsync(ct);
            foreach (var tree in trees.Where(t => t is not null))
            {
                var matchingUnits = tree!.Containers
                    .SelectMany(c => c.Units.Select(u => (Container: c, Unit: u)))
                    .Where(x => episodeIds.Contains(x.Unit.Id))
                    .ToList();
                if (matchingUnits.Count > 0)
                {
                    var lib = await catalogService.GetLibraryByIdAsync(tree.Work.LibraryId, ct);
                    var seasons = matchingUnits.Select(x => x.Container.ContainerNumber).ToHashSet();
                    await TryAutoSearchSeriesAsync(tree, lib, seasons, ct, episodeIds.ToHashSet());
                }
            }
        }
        else if (string.Equals(name, "DownloadedEpisodesScan", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name, "RefreshMonitoredDownloads", StringComparison.OrdinalIgnoreCase))
        {
            if (seriesId is > 0 && diskScanHook is not null)
            {
                await diskScanHook.ScanWorkDiskFilesAsync(seriesId.Value, ct);
            }
            downloadOrchestrator.RequestImmediateDownloadCheck();
        }

        return BuildCompatCommandResponse(1, name, now);
    }

    // -------------------------------------------------------------------------
    // Radarr v3 Shim (/compat/radarr/api/v3/*)
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<object>> ListRadarrMoviesAsync(int? tmdbId = null, CancellationToken ct = default)
    {
        var works = await catalogService.ListWorksAsync(null, null, "movies", ct);
        if (tmdbId.HasValue && tmdbId.Value > 0)
        {
            var tmdbStr = tmdbId.Value.ToString(CultureInfo.InvariantCulture);
            works = works
                .Where(w => string.Equals(w.ExternalIds.GetValueOrDefault("tmdb"), tmdbStr, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var libs = (await catalogService.ListLibrariesAsync(null, ct)).ToDictionary(l => l.Id);
        var allTags = await ListTagsAsync(ct);
        var results = new List<object>(works.Count);

        foreach (var w in works)
        {
            var tree = await catalogService.GetWorkTreeAsync(w.Id, null, ct);
            results.Add(FormatRadarrMovieResource(w, tree, libs.GetValueOrDefault(w.LibraryId), allTags));
        }

        return results;
    }

    public async Task<object?> GetRadarrMovieByIdAsync(long id, CancellationToken ct = default)
    {
        var tree = await catalogService.GetWorkTreeAsync(id, null, ct);
        if (tree is null || tree.Work.WorkKind != "movies")
        {
            return null;
        }
        var lib = await catalogService.GetLibraryByIdAsync(tree.Work.LibraryId, ct);
        var allTags = await ListTagsAsync(ct);
        return FormatRadarrMovieResource(tree.Work, tree, lib, allTags);
    }

    public async Task<IReadOnlyList<object>> LookupRadarrMoviesAsync(string? term, CancellationToken ct = default)
    {
        var cleanTerm = (term ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleanTerm))
        {
            return [];
        }

        var existingMovies = await catalogService.ListWorksAsync(null, null, "movies", ct);
        var libs = (await catalogService.ListLibrariesAsync(null, ct)).ToDictionary(l => l.Id);
        var allTags = await ListTagsAsync(ct);

        var localMatch = FindExistingMovieByLookupTerm(existingMovies, cleanTerm);
        if (localMatch is not null)
        {
            var localTree = await catalogService.GetWorkTreeAsync(localMatch.Id, null, ct);
            return [FormatRadarrMovieResource(localMatch, localTree, libs.GetValueOrDefault(localMatch.LibraryId), allTags)];
        }

        var hits = await metadataFederation.SearchWaterfallAsync(cleanTerm, "movies", null, ct);
        var output = new List<object>(hits.Count);

        foreach (var h in hits)
        {
            var matchedExisting = FindMatchingCatalogWork(existingMovies, h.ExternalIds, titleFallback: null);
            if (matchedExisting is not null)
            {
                var localTree = await catalogService.GetWorkTreeAsync(matchedExisting.Id, null, ct);
                output.Add(FormatRadarrMovieResource(matchedExisting, localTree, libs.GetValueOrDefault(matchedExisting.LibraryId), allTags));
            }
            else
            {
                output.Add(FormatRadarrLookupCandidate(h));
            }
        }

        return output;
    }

    public async Task<object?> LookupRadarrMovieByTmdbAsync(int tmdbId, CancellationToken ct = default)
    {
        if (tmdbId <= 0)
        {
            return null;
        }

        var matches = await LookupRadarrMoviesAsync($"tmdb:{tmdbId}", ct);
        if (matches.Count > 0)
        {
            return matches[0];
        }

        // Synthesize minimal Radarr lookup resource if external metadata provider has no API key configured in offline mode
        return new
        {
            id = 0L,
            title = $"TMDB Movie {tmdbId}",
            originalTitle = $"TMDB Movie {tmdbId}",
            sortTitle = $"tmdb movie {tmdbId}",
            titleSlug = $"tmdb-movie-{tmdbId}",
            status = "released",
            overview = "",
            year = DateTime.UtcNow.Year,
            hasFile = false,
            movieFileId = 0L,
            sizeOnDisk = 0L,
            monitored = false,
            minimumAvailability = "released",
            isAvailable = true,
            qualityProfileId = 1,
            profileId = 1,
            tmdbId,
            imdbId = (string?)null,
            images = Array.Empty<object>(),
            tags = Array.Empty<int>(),
        };
    }

    public async Task<object?> LookupRadarrMovieByImdbAsync(string imdbId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return null;
        }

        var cleanImdb = imdbId.Trim();
        var matches = await LookupRadarrMoviesAsync($"imdb:{cleanImdb}", ct);
        return matches.FirstOrDefault();
    }

    public async Task<object> AddRadarrMovieAsync(JsonObject body, long? headerPlexUserId, CancellationToken ct = default)
    {
        await AddWorkGate.WaitAsync(ct);
        try
        {
            var title = body["title"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Untitled Movie";
        }

        var tmdbId = body["tmdbId"]?.GetValue<int?>() ?? 0;
        var imdbId = body["imdbId"]?.GetValue<string>()?.Trim();
        var year = body["year"]?.GetValue<int?>();
        var overview = body["overview"]?.GetValue<string>();
        var posterUrl = ExtractPosterUrlFromJson(body);
        var monitored = body["monitored"]?.GetValue<bool?>() ?? true;
        var qualityProfileId = body["qualityProfileId"]?.GetValue<long?>() ?? body["profileId"]?.GetValue<long?>();
        var rootFolderPath = body["rootFolderPath"]?.GetValue<string>() ?? "/movies";
        var tagIds = ExtractIntList(body["tags"]);
        var bodyPlexId = body["plexUserId"]?.GetValue<long?>() ?? headerPlexUserId;
        var requestedBy = body["requestedByUsername"]?.GetValue<string>();
        var searchForMovie = (body["addOptions"] as JsonObject)?["searchForMovie"]?.GetValue<bool?>() ?? false;

        var extIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tmdbId > 0)
        {
            extIds["tmdb"] = tmdbId.ToString(CultureInfo.InvariantCulture);
        }
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            extIds["imdb"] = imdbId;
        }

        // Deduplicate by tmdbId / imdbId if movie is already in catalog
        var existingMovies = await catalogService.ListWorksAsync(null, null, "movies", ct);
        var duplicate = FindMatchingCatalogWork(existingMovies, extIds, titleFallback: null);
        if (duplicate is not null)
        {
            var updated = await UpdateRadarrMovieAsync(duplicate.Id, body, headerPlexUserId, mergeCompatTags: true, ct);
            if (updated is not null)
            {
                return updated;
            }
        }

        var lib = await EnsureShimLibraryAsync("movies", rootFolderPath, ct);
        var requester = await ResolveOverseerrPlexUserAsync(tagIds, bodyPlexId, requestedBy, ct);
        var allTags = await ListTagsAsync(ct);
        var resolvedTagLabels = ResolveTagLabels(tagIds, allTags);
        var validProfileOverrideId = await ResolveValidProfileIdAsync(qualityProfileId, ct);

        MetadataWorkCandidate? hydrated = null;
        if (extIds.Count > 0 && (string.IsNullOrWhiteSpace(overview) || string.IsNullOrWhiteSpace(posterUrl) || year is null))
        {
            try
            {
                hydrated = await metadataFederation.ResolveWorkAsync("movies", "tmdb", extIds, lib.MetadataWaterfall, ct);
            }
            catch
            {
                // Non-fatal when metadata provider is offline
            }
        }

        var tree = await catalogService.AddWorkAsync(
            new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: !string.IsNullOrWhiteSpace(hydrated?.Title) && title == "Untitled Movie" ? hydrated.Title : title,
                WorkKind: "movies",
                PrimaryProvider: "tmdb",
                AltTitles: hydrated?.AltTitles,
                OriginalLanguage: hydrated?.OriginalLanguage ?? "en",
                Year: year ?? hydrated?.Year,
                Status: hydrated?.Status ?? "released",
                Overview: !string.IsNullOrWhiteSpace(overview) ? overview : (hydrated?.Overview ?? ""),
                PosterUrl: !string.IsNullOrWhiteSpace(posterUrl) ? posterUrl : hydrated?.PosterUrl,
                Monitored: monitored,
                ExternalIds: extIds,
                Tags: resolvedTagLabels,
                InitialProfileOverrideId: validProfileOverrideId),
            null,
            ct);

        if (requester is not null)
        {
            await RecordCompatRequestAndLanguagePromotionAsync(tree.Work.Id, lib.Id, "movies", tree.Work.Title, extIds, requester, ct);
            tree = (await catalogService.GetWorkTreeAsync(tree.Work.Id, null, ct))!;
        }

        if (searchForMovie && tree.Work.Monitored)
        {
            await TryAutoSearchMovieAsync(tree, lib, ct);
        }

        allTags = await ListTagsAsync(ct);
        return FormatRadarrMovieResource(tree.Work, tree, lib, allTags);
    }
    finally
    {
        AddWorkGate.Release();
    }
}

    public Task<object?> UpdateRadarrMovieAsync(long? routeId, JsonObject body, long? headerPlexUserId, CancellationToken ct = default) =>
        UpdateRadarrMovieAsync(routeId, body, headerPlexUserId, false, ct);

    public async Task<object?> UpdateRadarrMovieAsync(long? routeId, JsonObject body, long? headerPlexUserId, bool mergeCompatTags, CancellationToken ct = default)
    {
        var id = routeId ?? body["id"]?.GetValue<long?>();
        MediaWorkDetailTree? tree = null;

        if (id is > 0)
        {
            tree = await catalogService.GetWorkTreeAsync(id.Value, null, ct);
        }

        if (tree is null)
        {
            var tmdbId = body["tmdbId"]?.GetValue<int?>() ?? 0;
            if (tmdbId > 0)
            {
                var existing = (await catalogService.ListWorksAsync(null, null, "movies", ct))
                    .FirstOrDefault(w => w.ExternalIds.GetValueOrDefault("tmdb") == tmdbId.ToString(CultureInfo.InvariantCulture));
                if (existing is not null)
                {
                    tree = await catalogService.GetWorkTreeAsync(existing.Id, null, ct);
                }
            }
        }

        if (tree is null || tree.Work.WorkKind != "movies")
        {
            return null;
        }

        var monitored = body["monitored"]?.GetValue<bool?>() ?? tree.Work.Monitored;
        var qualityProfileId = body["qualityProfileId"]?.GetValue<long?>() ?? body["profileId"]?.GetValue<long?>();
        var validProfileOverrideId = await ResolveValidProfileIdAsync(qualityProfileId, ct);

        var allTags = await ListTagsAsync(ct);
        IReadOnlyList<string>? updatedTagLabels = null;
        var tagIds = ExtractIntList(body["tags"]);
        if (body.ContainsKey("tags"))
        {
            var incomingLabels = ResolveTagLabels(tagIds, allTags);
            if (mergeCompatTags)
            {
                updatedTagLabels = tree.Work.Tags.Concat(incomingLabels).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            else
            {
                var compatKnownLabels = allTags.Select(t => t.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var preservedNative = tree.Work.Tags.Where(t => !compatKnownLabels.Contains(t));
                updatedTagLabels = preservedNative.Concat(incomingLabels).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        tree = await catalogService.UpdateWorkAsync(
            tree.Work.Id,
            new UpdateMediaWorkRequest(
                Monitored: monitored,
                Tags: updatedTagLabels),
            ct);

        foreach (var unit in tree.Containers.SelectMany(c => c.Units))
        {
            if (unit.Monitored != monitored || (validProfileOverrideId.HasValue && unit.ProfileOverrideId != validProfileOverrideId))
            {
                await catalogService.UpdateUnitAsync(
                    unit.Id,
                    new UpdateMediaUnitRequest(
                        Monitored: monitored,
                        ProfileOverrideId: validProfileOverrideId ?? unit.ProfileOverrideId),
                    ct);
            }
        }

        var bodyPlexId = body["plexUserId"]?.GetValue<long?>() ?? headerPlexUserId;
        var requestedBy = body["requestedByUsername"]?.GetValue<string>();
        var requester = await ResolveOverseerrPlexUserAsync(tagIds, bodyPlexId, requestedBy, ct);
        if (requester is not null)
        {
            var extCopy = new Dictionary<string, string>(tree.Work.ExternalIds, StringComparer.OrdinalIgnoreCase);
            await RecordCompatRequestAndLanguagePromotionAsync(tree.Work.Id, tree.Work.LibraryId, "movies", tree.Work.Title, extCopy, requester, ct);
        }

        tree = (await catalogService.GetWorkTreeAsync(tree.Work.Id, null, ct))!;
        var lib = await catalogService.GetLibraryByIdAsync(tree.Work.LibraryId, ct);

        var searchForMovie = (body["addOptions"] as JsonObject)?["searchForMovie"]?.GetValue<bool?>() ?? false;
        if (searchForMovie && tree.Work.Monitored)
        {
            await TryAutoSearchMovieAsync(tree, lib, ct);
        }

        allTags = await ListTagsAsync(ct);
        return FormatRadarrMovieResource(tree.Work, tree, lib, allTags);
    }

    public async Task<bool> DeleteRadarrMovieAsync(long id, CancellationToken ct = default)
    {
        var tree = await catalogService.GetWorkTreeAsync(id, null, ct);
        if (tree is null || tree.Work.WorkKind != "movies")
        {
            return false;
        }
        await catalogService.DeleteWorkAsync(id, ct);
        return true;
    }

    public async Task<IReadOnlyList<object>> ListRadarrMovieFilesAsync(long? movieId, CancellationToken ct = default)
    {
        var works = await catalogService.ListWorksAsync(null, null, "movies", ct);
        if (movieId.HasValue)
        {
            works = works.Where(w => w.Id == movieId.Value).ToList();
        }
        var libs = (await catalogService.ListLibrariesAsync(null, ct)).ToDictionary(l => l.Id);

        var files = new List<object>();
        foreach (var w in works)
        {
            var tree = await catalogService.GetWorkTreeAsync(w.Id, null, ct);
            if (tree is null)
            {
                continue;
            }
            var root = libs.GetValueOrDefault(w.LibraryId)?.RootFolderPath ?? "/movies";
            foreach (var f in tree.Containers.SelectMany(c => c.Units).SelectMany(u => u.Files))
            {
                files.Add(new
                {
                    id = f.Id,
                    movieId = w.Id,
                    relativePath = f.RelativePath,
                    path = Path.Combine(root, f.RelativePath),
                    size = f.SizeBytes,
                    quality = new { quality = new { id = 1, name = f.QualityName } },
                    languages = f.DetectedAudioLangs.Select((l, idx) => new { id = idx + 1, name = l }).ToArray(),
                });
            }
        }
        return files;
    }

    public async Task<object> ExecuteRadarrCommandAsync(JsonObject body, CancellationToken ct = default)
    {
        var name = body["name"]?.GetValue<string>() ?? "MoviesSearch";
        var movieIds = ExtractLongList(body["movieIds"]);
        if (body["movieId"]?.GetValue<long?>() is > 0 and var singleId && !movieIds.Contains(singleId))
        {
            movieIds.Add(singleId);
        }

        var now = DateTimeOffset.UtcNow.ToString("O");

        if (string.Equals(name, "RefreshMovie", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var mid in movieIds)
            {
                try
                {
                    await catalogService.RefreshWorkMetadataAsync(mid, null, null, ct);
                }
                catch
                {
                    // Non-fatal in compat command handler
                }
            }
        }
        else if (string.Equals(name, "MoviesSearch", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var mid in movieIds)
            {
                var tree = await catalogService.GetWorkTreeAsync(mid, null, ct);
                if (tree is not null)
                {
                    var lib = await catalogService.GetLibraryByIdAsync(tree.Work.LibraryId, ct);
                    await TryAutoSearchMovieAsync(tree, lib, ct);
                }
            }
        }
        else if (string.Equals(name, "DownloadedMoviesScan", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name, "RefreshMonitoredDownloads", StringComparison.OrdinalIgnoreCase))
        {
            downloadOrchestrator.RequestImmediateDownloadCheck();
        }

        return BuildCompatCommandResponse(1, name, now);
    }


    private async Task<IReadOnlyList<MediaWorkDetailTree?>> LoadAllSeriesTreesAsync(CancellationToken ct)
    {
        var works = (await catalogService.ListWorksAsync(null, null, null, ct))
            .Where(w => w.WorkKind is "tv" or "anime")
            .ToList();
        var trees = new List<MediaWorkDetailTree?>();
        foreach (var w in works)
        {
            trees.Add(await catalogService.GetWorkTreeAsync(w.Id, null, ct));
        }
        return trees;
    }

    private static object FormatSonarrSeriesResource(
        MediaWorkDetailTree tree,
        LibrarySummary? lib,
        IReadOnlyList<CompatTagDto> allTags)
    {
        var w = tree.Work;
        var tvdb = int.TryParse(w.ExternalIds.GetValueOrDefault("tvdb"), out var parsedTvdb) ? parsedTvdb : 0;
        var tvMaze = int.TryParse(w.ExternalIds.GetValueOrDefault("tvmaze"), out var parsedMaze) ? parsedMaze : 0;
        var overrideId = tree.Containers
            .SelectMany(c => c.Units)
            .Select(u => u.ProfileOverrideId)
            .FirstOrDefault(pid => pid.HasValue);
        var profileId = (int)(overrideId ?? lib?.DefaultProfileId ?? 1L);
        var rootFolder = lib?.RootFolderPath ?? "/tv";
        var tagIds = ResolveTagIds(w.Tags, allTags);

        var seasons = tree.Containers.Count > 0
            ? tree.Containers
                .OrderBy(c => c.ContainerNumber)
                .Select(c =>
                {
                    var totalEps = c.Units.Count;
                    var fileEps = c.Units.Count(u => u.Files.Count > 0);
                    var targetEps = c.Units.Count(u => u.Files.Count > 0 || (c.Monitored && u.Monitored));
                    var seasonBytes = c.Units.SelectMany(u => u.Files).Sum(f => f.SizeBytes);
                    var pct = targetEps > 0 ? Math.Round(100.0 * fileEps / targetEps, 1) : 0.0;
                    return (object)new
                    {
                        seasonNumber = c.ContainerNumber,
                        monitored = c.Monitored,
                        statistics = new
                        {
                            episodeFileCount = fileEps,
                            episodeCount = targetEps,
                            totalEpisodeCount = totalEps,
                            sizeOnDisk = seasonBytes,
                            percentOfEpisodes = pct,
                        },
                    };
                })
                .ToArray()
            :
            [
                (object)new
                {
                    seasonNumber = 1,
                    monitored = w.Monitored,
                    statistics = new
                    {
                        episodeFileCount = w.DownloadedUnitCount ?? w.FileCount,
                        episodeCount = w.MonitoredUnitCount ?? Math.Max(1, w.UnitCount),
                        totalEpisodeCount = Math.Max(1, w.UnitCount),
                        sizeOnDisk = 0L,
                        percentOfEpisodes = (w.MonitoredUnitCount ?? w.UnitCount) > 0
                            ? Math.Round(100.0 * (w.DownloadedUnitCount ?? w.FileCount) / (w.MonitoredUnitCount ?? w.UnitCount), 1)
                            : 0.0,
                    },
                },
            ];

        var allUnits = tree.Containers.SelectMany(c => c.Units).ToList();
        var totalUnitCount = allUnits.Count > 0 ? allUnits.Count : w.UnitCount;
        var totalFileCount = allUnits.Count > 0 ? allUnits.Count(u => u.Files.Count > 0) : (w.DownloadedUnitCount ?? w.FileCount);
        var targetEpisodeCount = w.MonitoredUnitCount ?? (allUnits.Count > 0
            ? tree.Containers.Sum(c => c.Units.Count(u => u.Files.Count > 0 || (c.Monitored && u.Monitored)))
            : totalUnitCount);
        var totalBytes = allUnits.SelectMany(u => u.Files).Sum(f => f.SizeBytes);
        var totalPct = targetEpisodeCount > 0 ? Math.Round(100.0 * totalFileCount / targetEpisodeCount, 1) : 0.0;

        return new
        {
            id = w.Id,
            title = w.Title,
            sortTitle = w.SortTitle,
            status = string.IsNullOrWhiteSpace(w.Status) ? "continuing" : w.Status,
            ended = string.Equals(w.Status, "ended", StringComparison.OrdinalIgnoreCase),
            overview = w.Overview,
            year = w.Year ?? 2024,
            path = Path.Combine(rootFolder, w.Title),
            rootFolderPath = rootFolder,
            qualityProfileId = profileId,
            profileId,
            languageProfileId = 1,
            seasonFolder = true,
            monitored = w.Monitored,
            useSceneNumbering = false,
            runtime = 45,
            seriesType = w.WorkKind == "anime" || lib?.MediaType == "anime" ? "anime" : "standard",
            cleanTitle = NormalizeTitleKey(w.Title),
            titleSlug = BuildTitleSlug(w.Title, tvdb > 0 ? tvdb : (int)w.Id),
            tvdbId = tvdb,
            tvRageId = 0,
            tvMazeId = tvMaze,
            imdbId = w.ExternalIds.GetValueOrDefault("imdb"),
            images = BuildCoverImages(w.PosterUrl),
            remotePoster = w.PosterUrl,
            seasons,
            tags = tagIds,
            added = w.AddedAt,
            addedByPlexUserId = w.AddedByUserId,
            statistics = new
            {
                seasonCount = seasons.Length,
                episodeCount = targetEpisodeCount,
                episodeFileCount = totalFileCount,
                totalEpisodeCount = totalUnitCount,
                sizeOnDisk = totalBytes,
                percentOfEpisodes = totalPct,
            },
        };
    }

    private static object FormatSonarrLookupCandidate(MetadataWorkCandidate c)
    {
        var tvdb = int.TryParse(c.ExternalIds.GetValueOrDefault("tvdb"), out var parsedTvdb) ? parsedTvdb : 0;
        var tvMaze = int.TryParse(c.ExternalIds.GetValueOrDefault("tvmaze"), out var parsedMaze) ? parsedMaze : 0;

        var seasons = c.Containers.Count > 0
            ? c.Containers
                .OrderBy(s => s.ContainerNumber)
                .Select(s =>
                {
                    var epCount = Math.Max(1, s.Units.Count);
                    return (object)new
                    {
                        seasonNumber = s.ContainerNumber,
                        monitored = s.ContainerNumber > 0,
                        statistics = new
                        {
                            episodeFileCount = 0,
                            episodeCount = epCount,
                            totalEpisodeCount = epCount,
                            sizeOnDisk = 0L,
                            percentOfEpisodes = 0.0,
                        },
                    };
                })
                .ToArray()
            :
            [
                (object)new
                {
                    seasonNumber = 1,
                    monitored = true,
                    statistics = new
                    {
                        episodeFileCount = 0,
                        episodeCount = 1,
                        totalEpisodeCount = 1,
                        sizeOnDisk = 0L,
                        percentOfEpisodes = 0.0,
                    },
                },
            ];

        var totalEps = c.Containers.Count > 0 ? Math.Max(1, c.Containers.Sum(s => s.Units.Count)) : 1;

        return new
        {
            id = 0L,
            title = c.Title,
            sortTitle = c.Title.ToLowerInvariant(),
            status = string.IsNullOrWhiteSpace(c.Status) ? "continuing" : c.Status,
            ended = string.Equals(c.Status, "ended", StringComparison.OrdinalIgnoreCase),
            overview = c.Overview,
            year = c.Year ?? 2024,
            qualityProfileId = 1,
            profileId = 1,
            languageProfileId = 1,
            seasonFolder = true,
            monitored = true,
            useSceneNumbering = false,
            runtime = 45,
            seriesType = string.Equals(c.Vertical, "anime", StringComparison.OrdinalIgnoreCase) ? "anime" : "standard",
            cleanTitle = NormalizeTitleKey(c.Title),
            titleSlug = BuildTitleSlug(c.Title, tvdb),
            tvdbId = tvdb,
            tvRageId = 0,
            tvMazeId = tvMaze,
            imdbId = c.ExternalIds.GetValueOrDefault("imdb"),
            images = BuildCoverImages(c.PosterUrl),
            remotePoster = c.PosterUrl,
            seasons,
            tags = Array.Empty<int>(),
            statistics = new
            {
                seasonCount = seasons.Length,
                episodeCount = totalEps,
                episodeFileCount = 0,
                totalEpisodeCount = totalEps,
                sizeOnDisk = 0L,
                percentOfEpisodes = 0.0,
            },
        };
    }

}
