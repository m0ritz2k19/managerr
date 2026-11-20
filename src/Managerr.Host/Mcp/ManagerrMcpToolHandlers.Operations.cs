using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Managerr.Catalog;
using Managerr.Downloaders;
using Managerr.Host.Endpoints;
using Managerr.Host.Services;
using Managerr.Indexers;
using Managerr.Metadata;
using Managerr.Migration;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;

namespace Managerr.Host.Mcp;

public sealed partial class ManagerrMcpServer
{
    private void RegisterOperationsTools(List<McpToolDefinition> list)
    {
        list.Add(new McpToolDefinition(
            Name: "releases_auto_search",
            Description: "Run automatic indexer search and best-match grab for a work, season/container, or single episode/unit.",
            RequiredAnyScopes: [PermissionScopes.IndexersSearch],
            InputSchema: ObjSchema(p =>
            {
                p["workId"] = Prop("integer", "Catalog work ID.");
                p["containerId"] = Prop("integer", "Optional season/container ID.");
                p["unitId"] = Prop("integer", "Optional episode/unit ID (takes precedence over containerId).");
            }, "workId"),
            ReadOnlyHint: false,
            DestructiveHint: false,
            IdempotentHint: false,
            Handler: HandleReleasesAutoSearchAsync));

        list.Add(new McpToolDefinition(
            Name: "releases_interactive_search",
            Description: "Search indexers with quality/language scoring, rejection reasons, and optional protocol/approval/sort/limit filtering.",
            RequiredAnyScopes: [PermissionScopes.IndexersSearch],
            InputSchema: ObjSchema(p =>
            {
                p["workId"] = Prop("integer", "Optional catalog work ID for scoped search and scoring.");
                p["containerId"] = Prop("integer", "Optional season/container ID.");
                p["unitId"] = Prop("integer", "Optional episode/unit ID.");
                p["query"] = Prop("string", "Free-text release search query (required when workId is omitted).");
                p["vertical"] = Prop("string", "Vertical hint (movies, tv, anime, music, ebooks, audiobooks, comics).");
                p["season"] = Prop("integer", "Season number.");
                p["episode"] = Prop("integer", "Episode number.");
                p["q"] = Prop("string", "Filter matched release titles, indexer names, languages, or qualities.");
                p["protocol"] = Prop("string", "Filter protocol.", "torrent", "usenet");
                p["approvedOnly"] = Prop("boolean", "If true, exclude rejected and blocklisted releases.");
                p["hideBlocklisted"] = Prop("boolean", "If true, exclude blocklisted releases.");
                p["sort"] = Prop("string", "Sort results.", "score", "age", "size", "seeders", "title");
                p["order"] = Prop("string", "Sort direction.", "asc", "desc");
                p["limit"] = Prop("integer", "Max releases to return (default 25, max 100).");
            }),
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: HandleReleasesInteractiveSearchAsync));

        list.Add(new McpToolDefinition(
            Name: "releases_grab",
            Description: "Send a specific release candidate from releases_interactive_search to the configured download client.",
            RequiredAnyScopes: [PermissionScopes.IndexersSearch],
            InputSchema: ObjSchema(p =>
            {
                p["title"] = Prop("string", "Release title.");
                p["protocol"] = Prop("string", "Protocol.", "torrent", "usenet");
                p["mediaVertical"] = Prop("string", "Media vertical (movies, tv, anime, music, ebooks, audiobooks, comics).");
                p["downloadUrlOrMagnet"] = Prop("string", "Magnet URI or NZB/torrent download URL.");
                p["infoHash"] = Prop("string", "Optional torrent infoHash.");
                p["workId"] = Prop("integer", "Optional catalog work ID.");
                p["containerId"] = Prop("integer", "Optional container ID.");
                p["unitIds"] = ArrayProp("integer", "Optional target unit IDs.");
                p["indexerId"] = Prop("integer", "Optional indexer ID.");
                p["releaseGuid"] = Prop("string", "Optional release GUID.");
                p["indexerName"] = Prop("string", "Optional indexer name.");
                p["sizeBytes"] = Prop("integer", "Optional release size in bytes.");
                p["forceGrab"] = Prop("boolean", "Bypass duplicate in-flight check.");
            }, "title", "protocol", "mediaVertical", "downloadUrlOrMagnet"),
            ReadOnlyHint: false,
            DestructiveHint: false,
            IdempotentHint: false,
            Handler: HandleReleasesGrabAsync));

        list.Add(new McpToolDefinition(
            Name: "wanted_list",
            Description: "List monitored missing units or quality/language cutoff-unmet units.",
            RequiredAnyScopes: [PermissionScopes.CatalogRead],
            InputSchema: ObjSchema(p =>
            {
                p["mode"] = Prop("string", "List mode (default 'missing').", "missing", "cutoff_unmet");
                p["page"] = Prop("integer", "1-based page number (default 1).");
                p["pageSize"] = Prop("integer", "Page size (default 50, max 200).");
                p["vertical"] = Prop("string", "Filter vertical.");
                p["libraryId"] = Prop("integer", "Filter single library ID.");
                p["libraryIds"] = Prop("string", "Comma-separated library IDs.");
                p["monitoredOnly"] = Prop("boolean", "Only monitored items (default true).");
                p["releasedOnly"] = Prop("boolean", "Only already released/aired items (default false).");
                p["language"] = Prop("string", "Filter missing language code.");
            }),
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: async (principal, args, ct) =>
            {
                var mode = (GetOptString(args, "mode") ?? "missing").Trim().ToLowerInvariant();
                var filter = new WantedFilterQuery(
                    Page: Math.Max(1, GetOptInt(args, "page") ?? 1),
                    PageSize: Math.Clamp(GetOptInt(args, "pageSize") ?? 50, 1, 200),
                    Vertical: GetOptString(args, "vertical"),
                    LibraryId: GetOptLong(args, "libraryId"),
                    MonitoredOnly: GetOptBool(args, "monitoredOnly") ?? true,
                    ReleasedOnly: GetOptBool(args, "releasedOnly") ?? false,
                    Language: GetOptString(args, "language"),
                    LibraryIds: GetOptString(args, "libraryIds"));

                return mode is "cutoff" or "cutoff_unmet" or "cutoff-unmet"
                    ? await _wantedService.GetWantedCutoffUnmetAsync(filter, principal.User, ct)
                    : await _wantedService.GetWantedMissingAsync(filter, principal.User, ct);
            }));

        list.Add(new McpToolDefinition(
            Name: "wanted_bulk_search",
            Description: "Trigger automated search across selected units, all wanted missing units, or cutoff-unmet units.",
            RequiredAnyScopes: [PermissionScopes.IndexersSearch],
            InputSchema: ObjSchema(p =>
            {
                p["mode"] = Prop("string", "Search mode.", "selected", "missing", "cutoff");
                p["unitIds"] = ArrayProp("integer", "Specific unit IDs (for 'selected').");
                p["vertical"] = Prop("string", "Filter vertical.");
                p["libraryId"] = Prop("integer", "Filter library ID.");
                p["libraryIds"] = Prop("string", "Comma-separated library IDs.");
                p["monitoredOnly"] = Prop("boolean", "Only monitored units (default true).");
                p["maxItems"] = Prop("integer", "Maximum search dispatches (default 25, max 100).");
            }),
            ReadOnlyHint: false,
            DestructiveHint: false,
            IdempotentHint: false,
            Handler: async (principal, args, ct) => await _wantedService.ExecuteBulkWantedSearchAsync(
                new WantedBulkSearchRequest(
                    Mode: GetOptString(args, "mode") ?? "selected",
                    UnitIds: GetOptLongArray(args, "unitIds"),
                    Vertical: GetOptString(args, "vertical"),
                    LibraryId: GetOptLong(args, "libraryId"),
                    MonitoredOnly: GetOptBool(args, "monitoredOnly") ?? true,
                    MaxItems: Math.Clamp(GetOptInt(args, "maxItems") ?? 25, 1, 100),
                    LibraryIds: GetOptString(args, "libraryIds")),
                principal.User,
                ct)));

        list.Add(new McpToolDefinition(
            Name: "queue_list",
            Description: "Inspect the active download queue across configured Torrent/Usenet clients plus CDH tracked download states.",
            RequiredAnyScopes: [PermissionScopes.QueueRead],
            InputSchema: ObjSchema(p =>
            {
                p["includeTracked"] = Prop("boolean", "Include tracked CDH import states (default true).");
                p["trackedLimit"] = Prop("integer", "Max tracked items (default 50).");
            }),
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: async (_, args, ct) =>
            {
                var queue = await _downloadOrchestrator.GetUnifiedQueueAsync(ct);
                var includeTracked = GetOptBool(args, "includeTracked") ?? true;
                var tracked = includeTracked
                    ? await _importService.ListTrackedDownloadsAsync(Math.Clamp(GetOptInt(args, "trackedLimit") ?? 50, 1, 200), ct)
                    : [];
                return new { queueCount = queue.Count, queue, trackedCount = tracked.Count, tracked };
            }));

        list.Add(new McpToolDefinition(
            Name: "queue_control",
            Description: "Manage queued or completed downloads: pause, resume, remove (with optional blocklist & replacement search), sweep stalled items, process completed downloads, retry tracked import, or ignore tracked item.",
            RequiredAnyScopes: [PermissionScopes.QueueManage],
            InputSchema: ObjSchema(p =>
            {
                p["action"] = Prop("string", "Action to execute.", "pause", "resume", "remove", "sweep", "process_completed", "retry_tracked_import", "ignore_tracked");
                p["clientId"] = Prop("integer", "Download client ID (for pause, resume, remove).");
                p["externalId"] = Prop("string", "External download hash/ID (for pause, resume, remove).");
                p["removeFromClient"] = Prop("boolean", "Remove from download client (for 'remove', default true).");
                p["deleteData"] = Prop("boolean", "Delete downloaded files (for 'remove', default false).");
                p["blocklist"] = Prop("boolean", "Add release to blocklist (for 'remove', default false).");
                p["searchReplacement"] = Prop("boolean", "Immediately search replacement release (for 'remove', default false).");
                p["title"] = Prop("string", "Optional title for blocklist entry.");
                p["infoHash"] = Prop("string", "Optional infoHash for blocklist entry.");
                p["trackedId"] = Prop("integer", "Tracked download ID (for retry_tracked_import / ignore_tracked).");
                p["strikeThreshold"] = Prop("integer", "Strike threshold for queue sweep (default 3).");
            }, "action"),
            ReadOnlyHint: false,
            DestructiveHint: false,
            IdempotentHint: false,
            Handler: HandleQueueControlAsync));

        list.Add(new McpToolDefinition(
            Name: "manual_import_preview",
            Description: "Scan a completed download folder to match media files to catalog works/episodes before manual import.",
            RequiredAnyScopes: [PermissionScopes.QueueRead],
            InputSchema: ObjSchema(p =>
            {
                p["folder"] = Prop("string", "Folder path to scan.");
                p["vertical"] = Prop("string", "Optional media vertical hint.");
                p["workId"] = Prop("integer", "Optional target work ID override.");
            }, "folder"),
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: async (_, args, ct) => await _wantedService.ScanManualImportFolderAsync(
                GetReqString(args, "folder"),
                GetOptString(args, "vertical"),
                GetOptLong(args, "workId"),
                ct)));

        list.Add(new McpToolDefinition(
            Name: "manual_import_execute",
            Description: "Import mapped files from a folder into the catalog via hardlink or copy.",
            RequiredAnyScopes: [PermissionScopes.QueueManage],
            InputSchema: ObjSchema(p =>
            {
                p["importMode"] = Prop("string", "Import mode.", "hardlink_or_copy", "move", "copy");
                p["items"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Files to import: [{ sourceFilePath, workId, seasonNumber?, episodeNumbers?, unitIds?, qualityName?, languages? }].",
                    ["items"] = ObjSchema(ip =>
                    {
                        ip["sourceFilePath"] = Prop("string", "Full source file path.");
                        ip["workId"] = Prop("integer", "Target catalog work ID.");
                        ip["seasonNumber"] = Prop("integer", "Optional season number.");
                        ip["episodeNumbers"] = ArrayProp("integer", "Optional episode numbers.");
                        ip["unitIds"] = ArrayProp("integer", "Optional unit IDs.");
                        ip["qualityName"] = Prop("string", "Optional quality name.");
                        ip["languages"] = ArrayProp("string", "Optional audio language codes.");
                    }, "sourceFilePath", "workId"),
                };
            }, "items"),
            ReadOnlyHint: false,
            DestructiveHint: false,
            IdempotentHint: false,
            Handler: HandleManualImportExecuteAsync));

        list.Add(new McpToolDefinition(
            Name: "activity_history",
            Description: "Inspect grab, import, failed download, and language verification activity history.",
            RequiredAnyScopes: [PermissionScopes.QueueRead],
            InputSchema: ObjSchema(p =>
            {
                p["page"] = Prop("integer", "1-based page number (default 1).");
                p["pageSize"] = Prop("integer", "Page size (default 50, max 200).");
                p["eventType"] = Prop("string", "Optional eventType filter (grabbed, imported, failed, quarantined, deleted).");
                p["vertical"] = Prop("string", "Optional vertical filter.");
                p["workId"] = Prop("integer", "Optional work ID filter.");
            }),
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: async (_, args, ct) => await _wantedService.GetMediaHistoryAsync(
                page: Math.Max(1, GetOptInt(args, "page") ?? 1),
                pageSize: Math.Clamp(GetOptInt(args, "pageSize") ?? 50, 1, 200),
                eventType: GetOptString(args, "eventType"),
                vertical: GetOptString(args, "vertical"),
                workId: GetOptLong(args, "workId"),
                ct: ct)));

        list.Add(new McpToolDefinition(
            Name: "activity_blocklist",
            Description: "List, filter, or remove entries from the release blocklist.",
            RequiredAnyScopes: [PermissionScopes.QueueRead, PermissionScopes.QueueManage],
            InputSchema: ObjSchema(p =>
            {
                p["action"] = Prop("string", "Operation (default 'list').", "list", "remove", "clear");
                p["q"] = Prop("string", "Search title, reason, infoHash, or indexer (for 'list').");
                p["protocol"] = Prop("string", "Filter protocol (for 'list').", "torrent", "usenet");
                p["workId"] = Prop("integer", "Filter work ID (for 'list').");
                p["limit"] = Prop("integer", "Max items (default 50).");
                p["offset"] = Prop("integer", "Zero-based offset (default 0).");
                p["infoHash"] = Prop("string", "Single infoHash/key to remove (for 'remove').");
                p["infoHashes"] = ArrayProp("string", "Multiple infoHashes/keys to remove (for 'remove').");
            }),
            ReadOnlyHint: false,
            DestructiveHint: false,
            IdempotentHint: false,
            Handler: HandleBlocklistAsync));

        list.Add(new McpToolDefinition(
            Name: "catalog_scan_disk",
            Description: "Scan a work, a library, or all libraries on disk for new/missing files, optionally running multi-tier audio/subtitle language verification.",
            RequiredAnyScopes: [PermissionScopes.CatalogWrite],
            InputSchema: ObjSchema(p =>
            {
                p["workId"] = Prop("integer", "Scan a single catalog work ID.");
                p["libraryId"] = Prop("integer", "Scan an entire library ID.");
                p["verifyLanguages"] = Prop("boolean", "When scanning a workId, also run 3-tier audio/subtitle language verification (default false).");
            }),
            ReadOnlyHint: false,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: HandleCatalogScanDiskAsync));

        list.Add(new McpToolDefinition(
            Name: "quarantine_list",
            Description: "List quarantined media imports awaiting manual language review or resolved historically.",
            RequiredAnyScopes: [PermissionScopes.QuarantineReview],
            InputSchema: ObjSchema(p =>
            {
                p["status"] = Prop("string", "Optional status filter.", "pending", "approved", "rejected");
            }),
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: async (_, args, ct) => await _languageVerification.ListQuarantineAsync(GetOptString(args, "status"), ct)));

        list.Add(new McpToolDefinition(
            Name: "quarantine_resolve",
            Description: "Approve a quarantined import with its confirmed audio language (auto-patching MKV headers), or reject and blocklist it.",
            RequiredAnyScopes: [PermissionScopes.QuarantineReview],
            InputSchema: ObjSchema(p =>
            {
                p["quarantineId"] = Prop("integer", "Quarantine record ID.");
                p["decision"] = Prop("string", "Review decision.", "approve", "reject");
                p["confirmedLanguage"] = Prop("string", "Confirmed ISO 639-1 language code when approving (default 'de').");
            }, "quarantineId", "decision"),
            ReadOnlyHint: false,
            DestructiveHint: false,
            IdempotentHint: false,
            Handler: HandleQuarantineResolveAsync));

        list.Add(new McpToolDefinition(
            Name: "profiles_list",
            Description: "List configured quality profiles and language verification policies (with tier cutoffs and bonuses).",
            RequiredAnyScopes: [PermissionScopes.ProfilesRead, PermissionScopes.CatalogRead, PermissionScopes.CatalogWrite],
            InputSchema: ObjSchema(_ => { }),
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: async (_, _, ct) => new
            {
                qualityProfiles = await _profiles.ListProfilesAsync(ct),
                languagePolicies = await _profiles.ListLanguagePoliciesAsync(ct),
            }));

        list.Add(new McpToolDefinition(
            Name: "indexers_and_downloaders_list",
            Description: "List configured indexers and download clients visible to your scopes.",
            RequiredAnyScopes: [PermissionScopes.IndexersRead, PermissionScopes.DownloadersRead],
            InputSchema: ObjSchema(_ => { }),
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: async (principal, _, ct) =>
            {
                var indexers = HasAnyScope(principal, [PermissionScopes.IndexersRead])
                    ? await _indexerService.ListIndexersAsync(ct)
                    : [];
                var downloaders = HasAnyScope(principal, [PermissionScopes.DownloadersRead])
                    ? await _downloadOrchestrator.ListClientsAsync(ct)
                    : [];
                return new { indexers, downloaders };
            }));

        list.Add(new McpToolDefinition(
            Name: "filesystem_browse",
            Description: "Browse server directories allowed to Managerr (useful when picking library root folders or manual import folders).",
            RequiredAnyScopes:
            [
                PermissionScopes.CatalogWrite,
                PermissionScopes.QueueManage,
                PermissionScopes.DownloadersWrite,
                PermissionScopes.SystemAdmin,
                PermissionScopes.MigrationExecute,
            ],
            InputSchema: ObjSchema(p =>
            {
                p["path"] = Prop("string", "Directory path to browse (omit for root/allowed mounts).");
                p["includeFiles"] = Prop("boolean", "Include regular files alongside subdirectories (default false).");
            }),
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: async (principal, args, ct) => await _filesystemBrowse.BrowseAsync(
                GetOptString(args, "path"),
                GetOptBool(args, "includeFiles") == true,
                principal.User,
                ct)));

        list.Add(new McpToolDefinition(
            Name: "system_status_and_tasks",
            Description: "Inspect Managerr version, system health checks, and background scheduled tasks, or trigger a task / resolve a health issue.",
            RequiredAnyScopes: [PermissionScopes.QueueRead, PermissionScopes.SystemAdmin, PermissionScopes.CatalogWrite],
            InputSchema: ObjSchema(p =>
            {
                p["runTaskKey"] = Prop("string", "Optional taskKey from the task list to trigger immediately (requires system:admin).");
                p["resolveHealthActionKey"] = Prop("string", "Optional health check actionKey to resolve automatically (requires catalog:write or system:admin).");
            }),
            ReadOnlyHint: false,
            DestructiveHint: false,
            IdempotentHint: false,
            Handler: HandleSystemStatusAndTasksAsync));
    }

    private async Task<object?> HandleReleasesAutoSearchAsync(
        AuthenticatedPrincipal principal,
        JsonElement args,
        CancellationToken ct)
    {
        var workId = GetReqLong(args, "workId");
        var unitId = GetOptLong(args, "unitId");
        var containerId = GetOptLong(args, "containerId");

        CatalogAutoSearchResultDto? result;
        if (unitId.HasValue)
        {
            result = await CatalogAndMetadataEndpoints.ExecuteUnitAutoSearchAsync(
                workId,
                unitId.Value,
                principal.User,
                _catalog,
                _profiles,
                _searchOrchestrator,
                _downloadOrchestrator,
                ct);
        }
        else if (containerId.HasValue)
        {
            result = await CatalogAndMetadataEndpoints.ExecuteContainerAutoSearchAsync(
                workId,
                containerId.Value,
                principal.User,
                _catalog,
                _profiles,
                _searchOrchestrator,
                _downloadOrchestrator,
                ct);
        }
        else
        {
            result = await CatalogAndMetadataEndpoints.ExecuteWorkAutoSearchAsync(
                workId,
                principal.User,
                _catalog,
                _profiles,
                _searchOrchestrator,
                _downloadOrchestrator,
                ct);
        }

        return result ?? throw new KeyNotFoundException($"Work/container/unit target not found for work #{workId}.");
    }

    private async Task<object?> HandleReleasesInteractiveSearchAsync(
        AuthenticatedPrincipal principal,
        JsonElement args,
        CancellationToken ct)
    {
        var workId = GetOptLong(args, "workId");
        var containerId = GetOptLong(args, "containerId");
        var unitId = GetOptLong(args, "unitId");
        var rawQuery = GetOptString(args, "query");
        var vertical = GetOptString(args, "vertical");
        var season = GetOptInt(args, "season");
        var episode = GetOptInt(args, "episode");

        MediaWorkDetailTree? tree = null;
        MediaContainerDetail? targetContainer = null;
        MediaUnitDetail? targetUnit = null;

        if (workId.HasValue)
        {
            tree = await _catalog.GetWorkTreeAsync(workId.Value, principal.User, ct)
                ?? throw new KeyNotFoundException($"Work #{workId.Value} not found.");

            if (unitId.HasValue)
            {
                foreach (var c in tree.Containers)
                {
                    var found = c.Units.FirstOrDefault(u => u.Id == unitId.Value);
                    if (found is not null)
                    {
                        targetContainer = c;
                        targetUnit = found;
                        break;
                    }
                }
            }
            else if (containerId.HasValue)
            {
                targetContainer = tree.Containers.FirstOrDefault(c => c.Id == containerId.Value);
            }
            else if (season.HasValue)
            {
                targetContainer = tree.Containers.FirstOrDefault(c => c.ContainerNumber == season.Value);
            }

            rawQuery ??= tree.Work.Title;
            vertical ??= tree.Work.WorkKind;
            var isEpisodic = tree.Work.WorkKind is MetadataVerticals.Tv or MetadataVerticals.Anime;
            season ??= isEpisodic ? targetContainer?.ContainerNumber : null;
            episode ??= isEpisodic && targetUnit is not null ? targetUnit.UnitNumber : null;
        }
        else if (string.IsNullOrWhiteSpace(rawQuery))
        {
            throw new ArgumentException("Either 'workId' or 'query' must be provided.");
        }

        var searchQuery = new IndexerSearchQuery(
            Query: rawQuery,
            MediaVertical: vertical ?? "tv",
            Season: season,
            Episode: episode,
            Interactive: true,
            WorkId: workId,
            UnitId: targetUnit?.Id ?? unitId);

        var scoped = await _searchOrchestrator.SearchScopedAsync(
            new OrchestratedReleaseSearchRequest(
                Query: searchQuery,
                WorkTree: tree,
                ContainerId: targetContainer?.Id ?? containerId,
                UnitId: targetUnit?.Id ?? unitId,
                AbsoluteEpisodeNumber: targetUnit?.AbsoluteNumber,
                IncludeSeasonPacks: true,
                Actor: principal.User),
            ct);

        var enriched = await _wantedService.EnrichInteractiveReleasesAsync(
            scoped.Select(s => (s.Release, s.Parsed)).ToList(),
            workId: workId,
            containerId: targetContainer?.Id ?? containerId,
            unitId: targetUnit?.Id ?? unitId,
            profileId: null,
            actor: principal.User,
            ct: ct);

        var defaultOrdered = enriched
            .OrderByDescending(x => x.Approved && !x.IsBlocklisted)
            .ThenByDescending(x => x.TotalWeightedScore)
            .ThenByDescending(x => x.Release.Seeders ?? 0)
            .ThenByDescending(x => x.Release.SizeBytes)
            .ToList();

        var limit = Math.Clamp(GetOptInt(args, "limit") ?? 25, 1, 100);
        var filtered = InteractiveReleaseFilterHelper.ApplyReleaseFilters(
            defaultOrdered,
            q: GetOptString(args, "q"),
            protocol: GetOptString(args, "protocol"),
            approvedOnly: GetOptBool(args, "approvedOnly"),
            hideBlocklisted: GetOptBool(args, "hideBlocklisted"),
            sort: GetOptString(args, "sort"),
            order: GetOptString(args, "order"),
            limit: limit);

        return new
        {
            totalCandidates = defaultOrdered.Count,
            returnedCount = filtered.Count,
            releases = filtered.Select(r => new
            {
                r.Release.Guid,
                r.Release.IndexerId,
                r.Release.IndexerName,
                r.Release.Protocol,
                r.Release.MediaVertical,
                r.Release.Title,
                r.Release.DownloadUrl,
                r.Release.MagnetUrl,
                r.Release.InfoHash,
                r.Release.SizeBytes,
                r.Release.Seeders,
                r.Release.Peers,
                ageHours = Math.Round(r.AgeHours, 2),
                r.Approved,
                r.IsBlocklisted,
                r.DecisionKind,
                r.TotalWeightedScore,
                r.QualityName,
                detectedLanguages = r.Parsed.DetectedLanguages,
                isSeasonPack = r.Parsed.IsFullSeasonPack,
                r.Parsed.SeasonNumber,
                r.Parsed.EpisodeNumbers,
                r.MatchedCustomFormats,
                r.Rejections,
            }).ToList(),
        };
    }

    private async Task<object?> HandleReleasesGrabAsync(
        AuthenticatedPrincipal principal,
        JsonElement args,
        CancellationToken ct)
    {
        var workId = GetOptLong(args, "workId");
        if (workId.HasValue)
        {
            _ = await _catalog.GetWorkTreeAsync(workId.Value, principal.User, ct)
                ?? throw new KeyNotFoundException($"Work #{workId.Value} not found.");
        }

        return await _downloadOrchestrator.GrabReleaseAsync(
            new GrabReleaseRequest(
                Title: GetReqString(args, "title"),
                Protocol: GetReqString(args, "protocol"),
                MediaVertical: GetReqString(args, "mediaVertical"),
                DownloadUrlOrMagnet: GetReqString(args, "downloadUrlOrMagnet"),
                InfoHash: GetOptString(args, "infoHash"),
                WorkId: workId,
                ContainerId: GetOptLong(args, "containerId"),
                UnitIds: GetOptLongArray(args, "unitIds"),
                IndexerId: GetOptLong(args, "indexerId"),
                ReleaseGuid: GetOptString(args, "releaseGuid"),
                SourceType: "mcp_interactive",
                ForceGrab: GetOptBool(args, "forceGrab") == true,
                IndexerName: GetOptString(args, "indexerName"),
                SizeBytes: GetOptLong(args, "sizeBytes")),
            ct);
    }

    private async Task<object?> HandleQueueControlAsync(
        AuthenticatedPrincipal _,
        JsonElement args,
        CancellationToken ct)
    {
        var action = GetReqString(args, "action").ToLowerInvariant();
        switch (action)
        {
            case "pause" or "resume":
            {
                var clientId = GetReqLong(args, "clientId");
                var externalId = GetReqString(args, "externalId");
                await _downloadOrchestrator.ControlQueueItemAsync(clientId, externalId, action, ct);
                return new { ok = true, action, clientId, externalId };
            }
            case "remove":
            {
                var clientId = GetReqLong(args, "clientId");
                var externalId = GetReqString(args, "externalId");
                return await _wantedService.RemoveQueueItemWithOptionsAsync(
                    clientId,
                    externalId,
                    new RemoveQueueItemOptions(
                        RemoveFromClient: GetOptBool(args, "removeFromClient") ?? true,
                        DeleteData: GetOptBool(args, "deleteData") == true,
                        Blocklist: GetOptBool(args, "blocklist") == true,
                        SearchReplacement: GetOptBool(args, "searchReplacement") == true,
                        Title: GetOptString(args, "title"),
                        InfoHash: GetOptString(args, "infoHash")),
                    ct);
            }
            case "sweep":
                return await _downloadOrchestrator.EvaluateQueueHealthAndStrikesAsync(
                    Math.Clamp(GetOptInt(args, "strikeThreshold") ?? 3, 1, 20),
                    ct);
            case "process_completed":
                return await _importService.ProcessCompletedDownloadsAsync(ct);
            case "retry_tracked_import":
                return await _importService.RetryTrackedDownloadImportAsync(GetReqLong(args, "trackedId"), null, ct);
            case "ignore_tracked":
            {
                var trackedId = GetReqLong(args, "trackedId");
                return await _importService.IgnoreTrackedDownloadAsync(trackedId, ct)
                    ?? throw new KeyNotFoundException($"Tracked download #{trackedId} not found.");
            }
            default:
                throw new ArgumentException($"Unsupported queue_control action '{action}'.");
        }
    }

    private async Task<object?> HandleManualImportExecuteAsync(
        AuthenticatedPrincipal principal,
        JsonElement args,
        CancellationToken ct)
    {
        if (!args.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Argument 'items' must be a non-empty array.");
        }

        var items = new List<ManualFolderImportItemRequest>();
        foreach (var itemEl in itemsEl.EnumerateArray())
        {
            var wid = GetReqLong(itemEl, "workId");
            _ = await _catalog.GetWorkTreeAsync(wid, principal.User, ct)
                ?? throw new KeyNotFoundException($"Work #{wid} not found.");

            var eps = GetOptLongArray(itemEl, "episodeNumbers")?.Select(n => (int)n).ToList();
            items.Add(new ManualFolderImportItemRequest(
                SourceFilePath: GetReqString(itemEl, "sourceFilePath"),
                WorkId: wid,
                SeasonNumber: GetOptInt(itemEl, "seasonNumber"),
                EpisodeNumbers: eps,
                UnitIds: GetOptLongArray(itemEl, "unitIds"),
                QualityName: GetOptString(itemEl, "qualityName"),
                Languages: GetOptStringArray(itemEl, "languages")));
        }

        return await _wantedService.ExecuteManualFolderImportAsync(
            new ExecuteManualFolderImportRequest(
                Items: items,
                ImportMode: GetOptString(args, "importMode") ?? "hardlink_or_copy"),
            ct);
    }

    private async Task<object?> HandleBlocklistAsync(
        AuthenticatedPrincipal principal,
        JsonElement args,
        CancellationToken ct)
    {
        var action = (GetOptString(args, "action") ?? "list").Trim().ToLowerInvariant();
        if (action == "list")
        {
            RequireAnyScope(principal, PermissionScopes.QueueRead);
            var rows = await _downloadOrchestrator.ListBlocklistedHashesAsync(ct);
            return InteractiveReleaseFilterHelper.ApplyBlocklistFilters(
                rows,
                GetOptString(args, "q"),
                GetOptString(args, "protocol"),
                GetOptLong(args, "workId"),
                GetOptInt(args, "limit") ?? 50,
                GetOptInt(args, "offset"));
        }

        RequireAnyScope(principal, PermissionScopes.QueueManage);
        if (action == "clear")
        {
            var cleared = await _downloadOrchestrator.BulkRemoveBlocklistedHashesAsync(null, clearAll: true, ct);
            return new { deletedCount = cleared };
        }

        if (action == "remove")
        {
            var hashes = GetOptStringArray(args, "infoHashes")?.ToList() ?? [];
            if (GetOptString(args, "infoHash") is { } single && !string.IsNullOrWhiteSpace(single))
            {
                if (hashes.Count == 0)
                {
                    var removed = await _downloadOrchestrator.RemoveBlocklistedHashAsync(single, ct);
                    return new { deletedCount = removed ? 1 : 0 };
                }

                hashes.Add(single);
            }

            if (hashes.Count == 0)
            {
                throw new ArgumentException("Provide 'infoHash' or 'infoHashes' when action='remove'.");
            }

            var deletedCount = await _downloadOrchestrator.BulkRemoveBlocklistedHashesAsync(hashes, clearAll: false, ct);
            return new { deletedCount };
        }

        throw new ArgumentException($"Unsupported activity_blocklist action '{action}'.");
    }

    private async Task<object?> HandleCatalogScanDiskAsync(
        AuthenticatedPrincipal principal,
        JsonElement args,
        CancellationToken ct)
    {
        var workId = GetOptLong(args, "workId");
        var libraryId = GetOptLong(args, "libraryId");
        var verifyLanguages = GetOptBool(args, "verifyLanguages") == true;

        if (workId.HasValue)
        {
            _ = await _catalog.GetWorkTreeAsync(workId.Value, principal.User, ct)
                ?? throw new KeyNotFoundException($"Work #{workId.Value} not found.");

            return verifyLanguages
                ? await _diskScanService.ScanAndVerifyWorkAsync(workId.Value, ct)
                : await _diskScanService.ScanWorkAsync(workId.Value, ct);
        }

        if (libraryId.HasValue)
        {
            var libs = await _catalog.ListLibrariesAsync(principal.User, ct);
            if (!libs.Any(l => l.Id == libraryId.Value))
            {
                throw new KeyNotFoundException($"Library #{libraryId.Value} not found or inaccessible.");
            }

            return await _diskScanService.ScanLibraryAsync(libraryId.Value, ct);
        }

        var accessibleLibs = await _catalog.ListLibrariesAsync(principal.User, ct);
        var reports = new List<DiskScanReportDto>(accessibleLibs.Count);
        foreach (var lib in accessibleLibs)
        {
            reports.Add(await _diskScanService.ScanLibraryAsync(lib.Id, ct));
        }

        return reports;
    }

    private async Task<object?> HandleQuarantineResolveAsync(
        AuthenticatedPrincipal principal,
        JsonElement args,
        CancellationToken ct)
    {
        var quarantineId = GetReqLong(args, "quarantineId");
        var decision = GetReqString(args, "decision").ToLowerInvariant();
        var reviewerId = principal.User?.Id ?? 0;

        if (decision == "approve")
        {
            var confirmedLang = GetOptString(args, "confirmedLanguage") ?? "de";
            return await _languageVerification.ApproveQuarantineAsync(quarantineId, reviewerId, confirmedLang, ct);
        }

        if (decision is "reject" or "blocklist")
        {
            return await _languageVerification.RejectAndBlocklistQuarantineAsync(quarantineId, reviewerId, ct);
        }

        throw new ArgumentException($"Unsupported quarantine decision '{decision}'. Expected 'approve' or 'reject'.");
    }

    private async Task<object?> HandleSystemStatusAndTasksAsync(
        AuthenticatedPrincipal principal,
        JsonElement args,
        CancellationToken ct)
    {
        var runTaskKey = GetOptString(args, "runTaskKey");
        if (!string.IsNullOrWhiteSpace(runTaskKey))
        {
            RequireAnyScope(principal, PermissionScopes.SystemAdmin);
            var taskSummary = await _taskManager.RunTaskNowAsync(runTaskKey, ct);
            return new { triggeredTask = taskSummary };
        }

        var resolveActionKey = GetOptString(args, "resolveHealthActionKey");
        if (!string.IsNullOrWhiteSpace(resolveActionKey))
        {
            RequireAnyScope(principal, PermissionScopes.CatalogWrite, PermissionScopes.SystemAdmin);
            var resolution = await _taskManager.ResolveHealthIssueAsync(resolveActionKey, ct);
            return new { resolvedHealthIssue = resolution };
        }

        RequireAnyScope(principal, PermissionScopes.QueueRead, PermissionScopes.SystemAdmin);
        var (version, informationalVersion, gitCommit) = SystemEndpoints.ResolveBuildVersionInfo();
        var healthChecks = await _taskManager.GetSystemHealthChecksAsync(ct);
        var tasks = await _taskManager.ListTasksAsync(ct);

        return new
        {
            status = "healthy",
            version,
            informationalVersion,
            gitCommit,
            healthChecks,
            tasks,
        };
    }
}
