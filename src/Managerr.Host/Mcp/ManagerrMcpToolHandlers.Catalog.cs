using System.Text.Json;
using System.Text.Json.Nodes;
using Managerr.Catalog;
using Managerr.Datastore.Services;
using Managerr.Host.Endpoints;
using Managerr.Metadata;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;

namespace Managerr.Host.Mcp;

public sealed partial class ManagerrMcpServer
{
    private void RegisterCatalogTools(List<McpToolDefinition> list)
    {
        list.Add(new McpToolDefinition(
            Name: "libraries_list",
            Description: "List accessible Managerr libraries (Movies, TV, Anime, Music, Books) with root folder paths and work counts.",
            RequiredAnyScopes: [PermissionScopes.CatalogRead],
            InputSchema: ObjSchema(_ => { }),
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: async (principal, _, ct) => await _catalog.ListLibrariesAsync(principal.User, ct)));

        list.Add(new McpToolDefinition(
            Name: "libraries_create",
            Description: "Create a new media library root folder.",
            RequiredAnyScopes: [PermissionScopes.CatalogWrite],
            InputSchema: ObjSchema(p =>
            {
                p["name"] = Prop("string", "Display name of the library, e.g. 'Anime' or 'Movies'.");
                p["mediaType"] = Prop("string", "Vertical type.", "movies", "tv", "anime", "music", "ebooks", "audiobooks", "comics");
                p["rootFolderPath"] = Prop("string", "Container/server filesystem path to library root folder.");
                p["plexSectionId"] = Prop("string", "Optional Plex section identifier.");
                p["defaultProfileId"] = Prop("integer", "Optional default quality profile ID.");
            }, "name", "mediaType", "rootFolderPath"),
            ReadOnlyHint: false,
            DestructiveHint: false,
            IdempotentHint: false,
            Handler: async (_, args, ct) => await _catalog.CreateLibraryAsync(
                new CreateLibraryRequest(
                    Name: GetReqString(args, "name"),
                    MediaType: GetReqString(args, "mediaType"),
                    RootFolderPath: GetReqString(args, "rootFolderPath"),
                    PlexSectionId: GetOptString(args, "plexSectionId"),
                    DefaultProfileId: GetOptLong(args, "defaultProfileId")),
                ct)));

        list.Add(new McpToolDefinition(
            Name: "catalog_list_works",
            Description: "Search, filter, sort, and paginate works in the Managerr catalog (matches the UI catalog filters).",
            RequiredAnyScopes: [PermissionScopes.CatalogRead],
            InputSchema: ObjSchema(p =>
            {
                p["q"] = Prop("string", "Search title, alt titles, year, tags, or external IDs (e.g. 'konosuba', 'spiderman', 'imdb:tt...').");
                p["libraryId"] = Prop("integer", "Filter to a single library ID.");
                p["libraryIds"] = Prop("string", "Comma-separated library IDs.");
                p["vertical"] = Prop("string", "Comma-separated verticals: movies, tv, anime, music, ebooks, audiobooks, comics.");
                p["monitored"] = Prop("boolean", "Filter by monitored status.");
                p["availability"] = Prop("string", "Comma-separated availability filter.", "missing", "complete", "continuing");
                p["classification"] = Prop("string", "Classification filter.", "anime-series", "anime-movie", "cartoon", "live-action-series", "live-action-movie");
                p["health"] = Prop("string", "Health issue filter.", "duplicates", "wrong-kind", "missing", "complete", "unmonitored");
                p["sort"] = Prop("string", "Sort field.", "title", "year", "missing", "added", "addedAt", "added_at", "date_added", "updated", "updatedAt", "updated_at");
                p["order"] = Prop("string", "Sort order.", "asc", "desc");
                p["limit"] = Prop("integer", "Max items to return (default 50, max 500).");
                p["offset"] = Prop("integer", "Zero-based pagination offset (default 0).");
                p["includeOverview"] = Prop("boolean", "Include plot overview text in items (default false to save tokens).");
            }),
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: HandleCatalogListWorksAsync));

        list.Add(new McpToolDefinition(
            Name: "catalog_get_work",
            Description: "Get full details for a catalog work including seasons/containers, episodes/units, attached files, and language verification status.",
            RequiredAnyScopes: [PermissionScopes.CatalogRead],
            InputSchema: ObjSchema(p =>
            {
                p["workId"] = Prop("integer", "Catalog work ID.");
                p["seasonNumber"] = Prop("integer", "Optional season/container number to include only that season.");
            }, "workId"),
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: HandleCatalogGetWorkAsync));

        list.Add(new McpToolDefinition(
            Name: "metadata_search",
            Description: "Search upstream metadata providers (TMDB, TVDB, AniList, MusicBrainz, OpenLibrary) for media before adding to catalog.",
            RequiredAnyScopes: [PermissionScopes.CatalogRead],
            InputSchema: ObjSchema(p =>
            {
                p["q"] = Prop("string", "Title or external ID query (e.g. 'Frieren', 'imdb:tt15239678', 'anilist:154587').");
                p["vertical"] = Prop("string", "Vertical scope (default 'all').", "all", "movies", "tv", "anime", "music", "ebooks", "audiobooks", "comics");
                p["providers"] = ArrayProp("string", "Optional list of provider keys to query.");
                p["limit"] = Prop("integer", "Max results to return (default 10, max 25).");
            }, "q"),
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: HandleMetadataSearchAsync));

        list.Add(new McpToolDefinition(
            Name: "catalog_add_work",
            Description: "Add a movie, series, anime, album, or book to the catalog. Pass 'query' to resolve metadata and auto-pick the best matching library automatically, or supply 'title' and 'libraryId' directly. Set autoSearch=true to immediately trigger an automatic release search.",
            RequiredAnyScopes: [PermissionScopes.CatalogWrite],
            InputSchema: ObjSchema(p =>
            {
                p["query"] = Prop("string", "Metadata search query to find and add the top matching work automatically.");
                p["title"] = Prop("string", "Explicit work title (required if 'query' is not provided).");
                p["libraryId"] = Prop("integer", "Target library ID (auto-selected from metadata if omitted when using 'query').");
                p["vertical"] = Prop("string", "Vertical/workKind hint.", "all", "movies", "tv", "anime", "music", "ebooks", "audiobooks", "comics");
                p["year"] = Prop("integer", "Release year hint for disambiguation.");
                p["originalLanguage"] = Prop("string", "Original ISO 639-1 language code (e.g. 'ja', 'en', 'de').");
                p["monitored"] = Prop("boolean", "Monitor the work (default true).");
                p["monitorPreset"] = Prop("string", "Episode monitor preset.", "all", "future", "missing", "existing", "firstSeason", "latestSeason", "none");
                p["qualityProfileId"] = Prop("integer", "Optional quality profile ID.");
                p["languagePolicyId"] = Prop("integer", "Optional language policy ID.");
                p["minimumAvailability"] = Prop("string", "Movie minimum availability.", "announced", "inCinemas", "released");
                p["tags"] = ArrayProp("string", "Optional catalog tags.");
                p["externalIds"] = new JsonObject { ["type"] = "object", ["description"] = "Optional map of external IDs (e.g. { \"tmdb\": \"...\", \"tvdb\": \"...\" })." };
                p["autoSearch"] = Prop("boolean", "If true, immediately run auto-search for wanted items after adding (requires indexers:search scope).");
            }),
            ReadOnlyHint: false,
            DestructiveHint: false,
            IdempotentHint: false,
            Handler: HandleCatalogAddWorkAsync));

        list.Add(new McpToolDefinition(
            Name: "catalog_update_work",
            Description: "Update work-level settings (monitored, qualityProfileId, languagePolicyId, minimumAvailability, tags, originalLanguage, monitorPreset).",
            RequiredAnyScopes: [PermissionScopes.CatalogWrite],
            InputSchema: ObjSchema(p =>
            {
                p["workId"] = Prop("integer", "Catalog work ID.");
                p["title"] = Prop("string", "New title.");
                p["monitored"] = Prop("boolean", "Monitor work.");
                p["monitorPreset"] = Prop("string", "Apply season/episode monitoring preset.", "all", "future", "missing", "existing", "firstSeason", "latestSeason", "none");
                p["minimumAvailability"] = Prop("string", "Minimum availability.", "announced", "inCinemas", "released");
                p["qualityProfileId"] = Prop("integer", "Quality profile ID.");
                p["clearQualityProfile"] = Prop("boolean", "Clear work quality profile.");
                p["languagePolicyId"] = Prop("integer", "Language policy ID.");
                p["clearLanguagePolicy"] = Prop("boolean", "Clear work language policy.");
                p["originalLanguage"] = Prop("string", "Original ISO language code.");
                p["tags"] = ArrayProp("string", "Replace work tags.");
                p["primaryProvider"] = Prop("string", "Primary metadata provider key.");
                p["episodeOrderingScheme"] = Prop("string", "Episode ordering scheme (aired, dvd, absolute).");
            }, "workId"),
            ReadOnlyHint: false,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: HandleCatalogUpdateWorkAsync));

        list.Add(new McpToolDefinition(
            Name: "catalog_set_monitoring",
            Description: "Enable or disable monitoring on a work, season/container, or specific episode/unit IDs within a work.",
            RequiredAnyScopes: [PermissionScopes.CatalogWrite],
            InputSchema: ObjSchema(p =>
            {
                p["workId"] = Prop("integer", "Catalog work ID.");
                p["monitored"] = Prop("boolean", "Target monitored state (true or false).");
                p["containerId"] = Prop("integer", "Optional season/container ID to toggle.");
                p["seasonNumber"] = Prop("integer", "Optional season number (resolved against workId if containerId omitted).");
                p["cascadeUnits"] = Prop("boolean", "When toggling a season/container, also apply to its episodes (default true).");
                p["unitIds"] = ArrayProp("integer", "Optional array of episode/unit IDs to toggle.");
                p["monitorPreset"] = Prop("string", "Optional work-level preset instead of single boolean.", "all", "future", "missing", "existing", "firstSeason", "latestSeason", "none");
            }, "workId", "monitored"),
            ReadOnlyHint: false,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: HandleCatalogSetMonitoringAsync));

        list.Add(new McpToolDefinition(
            Name: "catalog_refresh_metadata",
            Description: "Refresh metadata and episode/unit structure for a work from upstream providers.",
            RequiredAnyScopes: [PermissionScopes.CatalogWrite],
            InputSchema: ObjSchema(p =>
            {
                p["workId"] = Prop("integer", "Catalog work ID.");
                p["switchProvider"] = Prop("string", "Optional provider key override.");
                p["episodeOrderingScheme"] = Prop("string", "Optional ordering scheme.");
            }, "workId"),
            ReadOnlyHint: false,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: async (principal, args, ct) =>
            {
                var workId = GetReqLong(args, "workId");
                _ = await _catalog.GetWorkTreeAsync(workId, principal.User, ct)
                    ?? throw new KeyNotFoundException($"Work #{workId} not found.");
                return await _catalog.RefreshWorkMetadataAsync(
                    workId,
                    GetOptString(args, "switchProvider"),
                    GetOptString(args, "episodeOrderingScheme"),
                    ct);
            }));

        list.Add(new McpToolDefinition(
            Name: "catalog_auto_classify",
            Description: "Run automatic Anime vs Cartoon vs Live-Action classification and tagging across the catalog.",
            RequiredAnyScopes: [PermissionScopes.CatalogWrite],
            InputSchema: ObjSchema(_ => { }),
            ReadOnlyHint: false,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: async (principal, _, ct) =>
            {
                var report = await _catalog.AutoClassifyCatalogWorksAsync(ct);
                await _audit.RecordAsync(new AuditWriteRequest(
                    Action: "catalog.works_auto_classified",
                    ResourceType: "media_work",
                    ResourceId: "bulk",
                    ActorUserId: principal.User?.Id,
                    ActorPlexUserId: principal.User?.PlexUserId,
                    ActorUsername: principal.User?.Username,
                    ActorApiKeyPrefix: principal.ApiKeyPrefix,
                    Details: new { report.EvaluatedCount, report.UpdatedCount, via = "mcp" }), ct);
                return report;
            }));

        list.Add(new McpToolDefinition(
            Name: "catalog_delete_work",
            Description: "Delete a work from the catalog (and optionally remove its media files from disk).",
            RequiredAnyScopes: [PermissionScopes.CatalogDelete],
            InputSchema: ObjSchema(p =>
            {
                p["workId"] = Prop("integer", "Catalog work ID to delete.");
                p["deleteFilesOnDisk"] = Prop("boolean", "Also delete associated media files from disk (default false).");
            }, "workId"),
            ReadOnlyHint: false,
            DestructiveHint: true,
            IdempotentHint: false,
            Handler: async (principal, args, ct) =>
            {
                var workId = GetReqLong(args, "workId");
                var tree = await _catalog.GetWorkTreeAsync(workId, principal.User, ct)
                    ?? throw new KeyNotFoundException($"Work #{workId} not found.");
                var deleteFiles = GetOptBool(args, "deleteFilesOnDisk") == true;
                await _catalog.DeleteWorkWithOptionsAsync(workId, deleteFiles, ct);
                return new
                {
                    deleted = true,
                    workId,
                    title = tree.Work.Title,
                    deletedFilesOnDisk = deleteFiles,
                };
            }));

        list.Add(new McpToolDefinition(
            Name: "requests_manage",
            Description: "List, submit, or approve/deny user media requests.",
            RequiredAnyScopes: [PermissionScopes.CatalogRead, PermissionScopes.CatalogRequest, PermissionScopes.CatalogAutoApprove, PermissionScopes.CatalogWrite],
            InputSchema: ObjSchema(p =>
            {
                p["action"] = Prop("string", "Action to perform (default 'list').", "list", "submit", "approve", "deny");
                p["libraryId"] = Prop("integer", "Target library ID (for 'submit').");
                p["title"] = Prop("string", "Requested title (for 'submit').");
                p["vertical"] = Prop("string", "Media vertical (for 'submit').");
                p["requestedAudioLang"] = Prop("string", "Preferred audio language code (e.g. 'de', 'en').");
                p["requestedSubLang"] = Prop("string", "Preferred subtitle language code.");
                p["requestId"] = Prop("integer", "Request ID (for 'approve' or 'deny').");
            }),
            ReadOnlyHint: false,
            DestructiveHint: false,
            IdempotentHint: false,
            Handler: HandleRequestsManageAsync));
    }

    private async Task<object?> HandleCatalogListWorksAsync(
        AuthenticatedPrincipal principal,
        JsonElement args,
        CancellationToken ct)
    {
        var limit = Math.Clamp(GetOptInt(args, "limit") ?? 50, 1, 500);
        var offset = Math.Max(0, GetOptInt(args, "offset") ?? 0);
        var includeOverview = GetOptBool(args, "includeOverview") == true;

        var fullFilter = new CatalogWorkQueryFilter(
            LibraryId: GetOptLong(args, "libraryId"),
            LibraryIds: GetOptString(args, "libraryIds"),
            Vertical: GetOptString(args, "vertical"),
            Query: GetOptString(args, "q"),
            Monitored: GetOptBool(args, "monitored"),
            Availability: GetOptString(args, "availability"),
            Classification: GetOptString(args, "classification"),
            Health: GetOptString(args, "health"),
            Sort: GetOptString(args, "sort"),
            Order: GetOptString(args, "order"));

        var matched = await _catalog.QueryWorksAsync(fullFilter, principal.User, ct);
        var paged = matched.Skip(offset).Take(limit).Select(w =>
        {
            var progress = CatalogWorkFilterEvaluator.ComputeWorkProgress(w);
            return new
            {
                w.Id,
                w.LibraryId,
                w.WorkKind,
                classification = CatalogWorkFilterEvaluator.ResolveClassificationKey(w),
                w.Title,
                w.OriginalLanguage,
                w.Year,
                w.Status,
                overview = includeOverview ? w.Overview : null,
                w.Monitored,
                w.MinimumAvailability,
                w.QualityProfileId,
                w.LanguagePolicyId,
                w.ContainerCount,
                w.UnitCount,
                w.FileCount,
                monitoredUnitCount = progress.TargetUnits,
                downloadedUnitCount = progress.DownloadedUnits,
                missingMonitoredUnits = progress.MissingMonitoredUnits,
                w.Tags,
                w.ExternalIds,
                w.AddedAt,
                w.UpdatedAt,
            };
        }).ToList();

        return new
        {
            totalMatched = matched.Count,
            returnedCount = paged.Count,
            offset,
            limit,
            items = paged,
        };
    }

    private async Task<object?> HandleCatalogGetWorkAsync(
        AuthenticatedPrincipal principal,
        JsonElement args,
        CancellationToken ct)
    {
        var workId = GetReqLong(args, "workId");
        var tree = await _catalog.GetWorkTreeAsync(workId, principal.User, ct)
            ?? throw new KeyNotFoundException($"Work #{workId} not found.");

        var seasonFilter = GetOptInt(args, "seasonNumber");
        if (!seasonFilter.HasValue)
        {
            return tree;
        }

        return tree with
        {
            Containers = tree.Containers.Where(c => c.ContainerNumber == seasonFilter.Value).ToList(),
        };
    }

    private async Task<object?> HandleMetadataSearchAsync(
        AuthenticatedPrincipal _,
        JsonElement args,
        CancellationToken ct)
    {
        var query = GetReqString(args, "q");
        var vertical = GetOptString(args, "vertical") ?? "all";
        var providers = GetOptStringArray(args, "providers");
        var limit = Math.Clamp(GetOptInt(args, "limit") ?? 10, 1, 25);

        var results = await _federation.SearchWaterfallAsync(query, vertical, providers, ct);
        return results.Take(limit).Select(c => new
        {
            c.ProviderKey,
            c.ProviderItemId,
            c.Vertical,
            c.Title,
            c.AltTitles,
            c.OriginalLanguage,
            c.Year,
            c.Status,
            c.Overview,
            c.PosterUrl,
            c.ExternalIds,
            c.ClassificationKind,
            c.SuggestedTags,
            containerCount = c.Containers.Count,
            unitCount = c.Containers.Sum(cnt => cnt.Units.Count),
        }).ToList();
    }

    private async Task<object?> HandleCatalogAddWorkAsync(
        AuthenticatedPrincipal principal,
        JsonElement args,
        CancellationToken ct)
    {
        var autoSearch = GetOptBool(args, "autoSearch") == true;
        if (autoSearch)
        {
            RequireAnyScope(principal, PermissionScopes.IndexersSearch);
        }

        var query = GetOptString(args, "query");
        var explicitTitle = GetOptString(args, "title");
        var libraryId = GetOptLong(args, "libraryId");
        var vertical = GetOptString(args, "vertical");
        var year = GetOptInt(args, "year");
        var originalLanguage = GetOptString(args, "originalLanguage");
        var monitored = GetOptBool(args, "monitored") ?? true;
        var monitorPreset = GetOptString(args, "monitorPreset");
        var qualityProfileId = GetOptLong(args, "qualityProfileId");
        var languagePolicyId = GetOptLong(args, "languagePolicyId");
        var minimumAvailability = GetOptString(args, "minimumAvailability") ?? "released";
        var explicitTags = GetOptStringArray(args, "tags");
        var explicitExternalIds = GetOptStringDict(args, "externalIds");

        MetadataWorkCandidate? candidate = null;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var candidates = await _federation.SearchWaterfallAsync(query, vertical ?? "all", null, ct);
            if (candidates.Count == 0)
            {
                throw new KeyNotFoundException($"No metadata results found for '{query}'.");
            }

            candidate = (year.HasValue
                ? candidates.FirstOrDefault(c => c.Year == year.Value)
                : null) ?? candidates[0];
        }

        var libraries = await _catalog.ListLibrariesAsync(principal.User, ct);
        if (!libraryId.HasValue)
        {
            if (candidate is not null)
            {
                libraryId = PickBestLibraryForCandidate(candidate, libraries)?.Id;
            }
            else if (!string.IsNullOrWhiteSpace(vertical) && !string.Equals(vertical, "all", StringComparison.OrdinalIgnoreCase))
            {
                var normVertical = CatalogWorkFilterEvaluator.NormalizeVertical(vertical);
                libraryId = libraries.FirstOrDefault(l =>
                    string.Equals(CatalogWorkFilterEvaluator.NormalizeVertical(l.MediaType), normVertical, StringComparison.OrdinalIgnoreCase))?.Id;
            }

            if (!libraryId.HasValue && libraries.Count == 1)
            {
                libraryId = libraries[0].Id;
            }
        }

        if (!libraryId.HasValue)
        {
            throw new ArgumentException("Could not automatically determine 'libraryId'. Pass an explicit libraryId from libraries_list.");
        }

        var finalTitle = explicitTitle ?? candidate?.Title;
        if (string.IsNullOrWhiteSpace(finalTitle))
        {
            throw new ArgumentException("Either 'query' or 'title' must be provided.");
        }

        var mergedTags = explicitTags?.ToList() ?? [];
        if (candidate?.SuggestedTags is { Count: > 0 })
        {
            foreach (var tag in candidate.SuggestedTags)
            {
                if (!mergedTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    mergedTags.Add(tag);
                }
            }
        }

        var createReq = new CreateMediaWorkRequest(
            LibraryId: libraryId.Value,
            Title: finalTitle,
            WorkKind: (vertical is not null && !string.Equals(vertical, "all", StringComparison.OrdinalIgnoreCase))
                ? vertical
                : candidate?.Vertical,
            PrimaryProvider: candidate?.ProviderKey ?? "auto",
            AltTitles: candidate?.AltTitles,
            OriginalLanguage: originalLanguage ?? candidate?.OriginalLanguage ?? "en",
            Year: year ?? candidate?.Year,
            Status: candidate?.Status ?? "released",
            Overview: candidate?.Overview ?? string.Empty,
            PosterUrl: candidate?.PosterUrl,
            Monitored: monitored,
            ExternalIds: explicitExternalIds ?? candidate?.ExternalIds,
            Tags: mergedTags.Count > 0 ? mergedTags : null,
            Containers: candidate?.Containers,
            MonitorMode: monitorPreset,
            MinimumAvailability: minimumAvailability,
            QualityProfileId: qualityProfileId,
            LanguagePolicyId: languagePolicyId);

        var created = await _catalog.AddWorkAsync(createReq, principal.User, ct);
        await _audit.RecordAsync(new AuditWriteRequest(
            Action: "catalog.work_added",
            ResourceType: "media_work",
            ResourceId: created.Work.Id.ToString(),
            ActorUserId: principal.User?.Id,
            ActorPlexUserId: principal.User?.PlexUserId,
            ActorUsername: principal.User?.Username,
            ActorApiKeyPrefix: principal.ApiKeyPrefix,
            Details: new { created.Work.Title, created.Work.WorkKind, created.Work.LibraryId, via = "mcp" }), ct);

        CatalogAutoSearchResultDto? autoSearchResult = null;
        if (autoSearch)
        {
            autoSearchResult = await CatalogAndMetadataEndpoints.ExecuteWorkAutoSearchAsync(
                created.Work.Id,
                principal.User,
                _catalog,
                _profiles,
                _searchOrchestrator,
                _downloadOrchestrator,
                ct);
        }

        return new
        {
            work = created.Work,
            containerCount = created.Containers.Count,
            unitCount = created.Containers.Sum(c => c.Units.Count),
            autoSearchResult,
        };
    }

    private async Task<object?> HandleCatalogUpdateWorkAsync(
        AuthenticatedPrincipal principal,
        JsonElement args,
        CancellationToken ct)
    {
        var workId = GetReqLong(args, "workId");
        _ = await _catalog.GetWorkTreeAsync(workId, principal.User, ct)
            ?? throw new KeyNotFoundException($"Work #{workId} not found.");

        var qualityProfileId = GetOptLong(args, "qualityProfileId");
        var clearQualityProfile = GetOptBool(args, "clearQualityProfile") == true;
        var languagePolicyId = GetOptLong(args, "languagePolicyId");
        var clearLanguagePolicy = GetOptBool(args, "clearLanguagePolicy") == true;
        var originalLanguage = GetOptString(args, "originalLanguage");

        var updated = await _catalog.UpdateWorkAsync(
            workId,
            new UpdateMediaWorkRequest(
                Title: GetOptString(args, "title"),
                Monitored: GetOptBool(args, "monitored"),
                PrimaryProvider: GetOptString(args, "primaryProvider"),
                EpisodeOrderingScheme: GetOptString(args, "episodeOrderingScheme"),
                Tags: GetOptStringArray(args, "tags"),
                MinimumAvailability: GetOptString(args, "minimumAvailability"),
                QualityProfileId: qualityProfileId,
                ClearQualityProfile: clearQualityProfile,
                MonitorPreset: GetOptString(args, "monitorPreset"),
                LanguagePolicyId: languagePolicyId,
                ClearLanguagePolicy: clearLanguagePolicy,
                OriginalLanguage: originalLanguage),
            ct);

        if (languagePolicyId.HasValue || clearLanguagePolicy || qualityProfileId.HasValue || clearQualityProfile || !string.IsNullOrWhiteSpace(originalLanguage))
        {
            await _diskScanService.ReevaluateWorkFilesAgainstPolicyAsync(workId, ct);
            updated = await _catalog.GetWorkTreeAsync(workId, principal.User, ct) ?? updated;
        }

        return updated;
    }

    private async Task<object?> HandleCatalogSetMonitoringAsync(
        AuthenticatedPrincipal principal,
        JsonElement args,
        CancellationToken ct)
    {
        var workId = GetReqLong(args, "workId");
        var monitored = GetOptBool(args, "monitored")
            ?? throw new ArgumentException("Argument 'monitored' (boolean) is required.");
        var tree = await _catalog.GetWorkTreeAsync(workId, principal.User, ct)
            ?? throw new KeyNotFoundException($"Work #{workId} not found.");

        var monitorPreset = GetOptString(args, "monitorPreset");
        if (!string.IsNullOrWhiteSpace(monitorPreset))
        {
            return await _catalog.UpdateWorkAsync(workId, new UpdateMediaWorkRequest(MonitorPreset: monitorPreset), ct);
        }

        var containerId = GetOptLong(args, "containerId");
        var seasonNumber = GetOptInt(args, "seasonNumber");
        var unitIds = GetOptLongArray(args, "unitIds");

        if (unitIds is { Count: > 0 })
        {
            var validUnitIds = tree.Containers.SelectMany(c => c.Units).Select(u => u.Id).ToHashSet();
            foreach (var uid in unitIds)
            {
                if (!validUnitIds.Contains(uid))
                {
                    throw new KeyNotFoundException($"Unit #{uid} does not belong to work #{workId}.");
                }
            }

            foreach (var uid in unitIds)
            {
                await _catalog.UpdateUnitAsync(uid, new UpdateMediaUnitRequest(Monitored: monitored), ct);
            }

            return await _catalog.GetWorkTreeAsync(workId, principal.User, ct);
        }

        if (containerId.HasValue || seasonNumber.HasValue)
        {
            var targetContainer = containerId.HasValue
                ? tree.Containers.FirstOrDefault(c => c.Id == containerId.Value)
                : tree.Containers.FirstOrDefault(c => c.ContainerNumber == seasonNumber!.Value);

            if (targetContainer is null)
            {
                throw new KeyNotFoundException($"Target container/season not found on work #{workId}.");
            }

            var cascade = GetOptBool(args, "cascadeUnits") ?? true;
            return await _catalog.UpdateContainerAsync(targetContainer.Id, new UpdateMediaContainerRequest(monitored, cascade), ct);
        }

        return await _catalog.UpdateWorkAsync(workId, new UpdateMediaWorkRequest(Monitored: monitored), ct);
    }

    private async Task<object?> HandleRequestsManageAsync(
        AuthenticatedPrincipal principal,
        JsonElement args,
        CancellationToken ct)
    {
        var action = (GetOptString(args, "action") ?? "list").Trim().ToLowerInvariant();
        if (action == "list")
        {
            RequireAnyScope(principal, PermissionScopes.CatalogRead);
            return await _catalog.ListRequestsAsync(principal.User, ct);
        }

        if (principal.User is null)
        {
            throw new ArgumentException("Submitting or deciding media requests requires a user-linked session or API key.");
        }

        if (action == "submit")
        {
            RequireAnyScope(principal, PermissionScopes.CatalogRequest);
            return await _catalog.SubmitRequestAsync(
                principal.User,
                new SubmitMediaRequestCommand(
                    LibraryId: GetReqLong(args, "libraryId"),
                    Title: GetReqString(args, "title"),
                    MediaVertical: GetOptString(args, "vertical"),
                    RequestedAudioLang: GetOptString(args, "requestedAudioLang"),
                    RequestedSubLang: GetOptString(args, "requestedSubLang")),
                ct);
        }

        if (action is "approve" or "deny")
        {
            RequireAnyScope(principal, PermissionScopes.CatalogAutoApprove, PermissionScopes.CatalogWrite);
            return await _catalog.DecideRequestAsync(GetReqLong(args, "requestId"), approve: action == "approve", principal.User, ct);
        }

        throw new ArgumentException($"Unsupported requests_manage action '{action}'.");
    }

    private static LibrarySummary? PickBestLibraryForCandidate(
        MetadataWorkCandidate cand,
        IReadOnlyList<LibrarySummary> libraries)
    {
        if (libraries.Count == 0)
        {
            return null;
        }

        var ck = cand.ClassificationKind?.Trim().ToLowerInvariant();
        var tags = (cand.SuggestedTags ?? []).Select(t => t.Trim().ToLowerInvariant()).ToHashSet();
        var isAnimeMovie = ck == "anime-movie" || tags.Contains("anime-movie");
        var isAnimeSeries = ck == "anime-series" || tags.Contains("anime-series") ||
                            string.Equals(cand.Vertical, MetadataVerticals.Anime, StringComparison.OrdinalIgnoreCase);

        if (isAnimeMovie)
        {
            return libraries.FirstOrDefault(l =>
                       l.RootFolderPath.Contains("animefilme", StringComparison.OrdinalIgnoreCase) ||
                       l.RootFolderPath.Contains("animemov", StringComparison.OrdinalIgnoreCase) ||
                       l.Name.Contains("animefilme", StringComparison.OrdinalIgnoreCase) ||
                       l.Name.Contains("animemov", StringComparison.OrdinalIgnoreCase))
                   ?? libraries.FirstOrDefault(l => string.Equals(l.MediaType, MetadataVerticals.Movies, StringComparison.OrdinalIgnoreCase))
                   ?? libraries[0];
        }

        if (isAnimeSeries)
        {
            return libraries.FirstOrDefault(l =>
                       string.Equals(l.MediaType, MetadataVerticals.Anime, StringComparison.OrdinalIgnoreCase) ||
                       l.RootFolderPath.Contains("/anime", StringComparison.OrdinalIgnoreCase))
                   ?? libraries.FirstOrDefault(l => string.Equals(l.MediaType, MetadataVerticals.Tv, StringComparison.OrdinalIgnoreCase))
                   ?? libraries[0];
        }

        var candVert = CatalogWorkFilterEvaluator.NormalizeVertical(cand.Vertical);
        if (candVert == MetadataVerticals.Movies)
        {
            var movieLibs = libraries.Where(l => string.Equals(CatalogWorkFilterEvaluator.NormalizeVertical(l.MediaType), MetadataVerticals.Movies, StringComparison.OrdinalIgnoreCase)).ToList();
            if (movieLibs.Count > 0)
            {
                return movieLibs.FirstOrDefault(l =>
                           !l.RootFolderPath.Contains("animefilme", StringComparison.OrdinalIgnoreCase) &&
                           !l.RootFolderPath.Contains("animemov", StringComparison.OrdinalIgnoreCase) &&
                           !l.Name.Contains("animefilme", StringComparison.OrdinalIgnoreCase))
                       ?? movieLibs[0];
            }
        }

        return libraries.FirstOrDefault(l =>
                   string.Equals(CatalogWorkFilterEvaluator.NormalizeVertical(l.MediaType), candVert, StringComparison.OrdinalIgnoreCase))
               ?? libraries[0];
    }
}
