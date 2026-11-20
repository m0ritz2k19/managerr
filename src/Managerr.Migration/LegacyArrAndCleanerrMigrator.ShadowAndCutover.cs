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
    public async Task<ShadowModeDecisionSummary> EvaluateShadowModeReleaseAsync(
        ShadowModeEvaluateRequest request,
        CancellationToken ct = default)
    {
        var sim = await profileService.SimulateAsync(
            new SimulateReleaseRequest(
                ProfileId: request.ProfileId,
                ReleaseTitle: request.ReleaseTitle,
                OriginalLanguage: request.OriginalLanguage,
                UnitId: request.UnitId,
                ExistingFileQuality: request.ExistingFileQuality,
                ExistingFileAudioLanguages: request.ExistingAudioLanguages),
            ct);

        var wouldUpgradeLang = string.Equals(sim.DecisionKind, ProfileDecisionKinds.LanguageUpgrade, StringComparison.OrdinalIgnoreCase);
        var notes = wouldUpgradeLang
            ? $"Managerr Language Policy upgraded release ({sim.TotalWeightedScore:F1} pts) where legacy {request.LegacyApp} reported '{request.LegacyDecision}'."
            : $"Shadow evaluation (0 grabs dispatched): Managerr={sim.DecisionKind} ({sim.TotalWeightedScore:F1} pts) vs {request.LegacyApp}={request.LegacyDecision}.";

        var now = DateTimeOffset.UtcNow.ToString("O");
        var id = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO shadow_mode_decisions (
                    occurred_at, media_vertical, work_id, work_title, release_title,
                    managerr_decision, managerr_score, legacy_app, legacy_decision,
                    would_upgrade_language, notes
                ) VALUES (
                    @Now, @Vertical, @WorkId, @WorkTitle, @ReleaseTitle,
                    @ManagerrDecision, @Score, @LegacyApp, @LegacyDecision,
                    @WouldUpgradeLang, @Notes
                );
                SELECT last_insert_rowid();
                """,
                new
                {
                    Now = now,
                    Vertical = request.MediaVertical.Trim().ToLowerInvariant(),
                    request.WorkId,
                    WorkTitle = request.WorkTitle.Trim(),
                    ReleaseTitle = request.ReleaseTitle.Trim(),
                    ManagerrDecision = sim.DecisionKind,
                    Score = sim.TotalWeightedScore,
                    LegacyApp = request.LegacyApp.Trim(),
                    LegacyDecision = request.LegacyDecision.Trim(),
                    WouldUpgradeLang = wouldUpgradeLang ? 1 : 0,
                    Notes = notes,
                },
                transaction: tx),
            ct);

        return new ShadowModeDecisionSummary(
            Id: id,
            OccurredAt: now,
            MediaVertical: request.MediaVertical.Trim().ToLowerInvariant(),
            WorkId: request.WorkId,
            WorkTitle: request.WorkTitle.Trim(),
            ReleaseTitle: request.ReleaseTitle.Trim(),
            ManagerrDecision: sim.DecisionKind,
            ManagerrScore: sim.TotalWeightedScore,
            LegacyApp: request.LegacyApp.Trim(),
            LegacyDecision: request.LegacyDecision.Trim(),
            WouldUpgradeLanguage: wouldUpgradeLang,
            Notes: notes);
    }

    public async Task<IReadOnlyList<ShadowModeDecisionSummary>> ListShadowModeDecisionsAsync(
        int limit = 50,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<ShadowDecisionRow>(
            "SELECT * FROM shadow_mode_decisions ORDER BY id DESC LIMIT @Limit;",
            new { Limit = Math.Clamp(limit, 1, 250) });

        return rows.Select(r => new ShadowModeDecisionSummary(
            Id: r.id,
            OccurredAt: r.occurred_at,
            MediaVertical: r.media_vertical,
            WorkId: r.work_id,
            WorkTitle: r.work_title,
            ReleaseTitle: r.release_title,
            ManagerrDecision: r.managerr_decision,
            ManagerrScore: r.managerr_score,
            LegacyApp: r.legacy_app,
            LegacyDecision: r.legacy_decision,
            WouldUpgradeLanguage: r.would_upgrade_language != 0,
            Notes: r.notes)).ToList();
    }

    public async Task<CutoverExecutionResult> ExecuteCutoverAsync(
        long ownerUserId,
        CutoverExecuteRequest request,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        MigrationReportSummary? deltaReport = null;

        var configuredSources = await ListSourcesAsync(ct);
        if (request.RunFinalDeltaSync && configuredSources.Count > 0)
        {
            deltaReport = await ExecuteMigrationAsync(
                new MigrationPlanAndExecuteRequest(
                    Sources: null,
                    GlobalPathMappings: request.GlobalPathMappings,
                    UpliftLanguageCustomFormats: true),
                ct);
        }

        var generatedKeys = new List<GeneratedCompatKeyInfo>();
        if (request.GenerateScopedCompatKeys)
        {
            var specs = new List<(string Label, string Scope, string MountUrl)>
            {
                ("Compat - Sonarr v3 (Overseerr / Bazarr / Recyclarr)", PermissionScopes.CompatSonarr, "/compat/sonarr"),
                ("Compat - Radarr v3 (Overseerr / Bazarr / Recyclarr)", PermissionScopes.CompatRadarr, "/compat/radarr"),
                ("Compat - Lidarr v1 (Music Apps)", PermissionScopes.CompatLidarr, "/compat/lidarr"),
                ("Compat - Readarr v1 [Ebook Slot]", PermissionScopes.CompatReadarr, "/compat/readarr?slot=ebook"),
                ("Compat - Prowlarr v1 & Torznab Feeds", PermissionScopes.CompatProwlarr, "/compat/prowlarr"),
            };

            if (request.GenerateSeparateReadarrAudiobookKey)
            {
                specs.Insert(4, ("Compat - Readarr v1 [Audiobook Slot]", PermissionScopes.CompatReadarr, "/compat/readarr?slot=audiobook"));
            }

            foreach (var spec in specs)
            {
                var created = await authService.CreateApiKeyAsync(
                    ownerUserId: ownerUserId,
                    name: spec.Label,
                    scopes: [spec.Scope],
                    allowedLibraryIds: null,
                    allowedCidrs: null,
                    expiresAt: null,
                    ct: ct);

                generatedKeys.Add(new GeneratedCompatKeyInfo(
                    Label: spec.Label,
                    Scope: spec.Scope,
                    MountUrl: spec.MountUrl,
                    RawApiKeyOnce: created.RawTokenOnce,
                    MaskedHint: created.MaskedHint));
            }
        }

        var summaryObj = new
        {
            mode = "cutover",
            generatedKeys = generatedKeys.Count,
            deltaSynced = deltaReport is not null,
        };
        var runId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO migration_runs (mode, uplift_language_cfs, status, started_at, finished_at, summary_json)
                VALUES ('cutover', 1, 'completed', @Now, @Now, @SummaryJson);
                SELECT last_insert_rowid();
                """,
                new { Now = now, SummaryJson = JsonSerializer.Serialize(summaryObj, JsonOpts) },
                transaction: tx),
            ct);

        string[] instructions =
        [
            "1. Point Overseerr / Jellyseerr Sonarr & Radarr servers to http://managerr:8989/compat/sonarr and /compat/radarr using the scoped keys above.",
            "2. Point Bazarr & Recyclarr to /compat/sonarr and /compat/radarr — PlexUser ownership and Custom Formats sync natively.",
            "3. Point Calibre-Web-Automated / Audiobook bots to /compat/readarr?slot=ebook and /compat/readarr?slot=audiobook.",
            "4. Point Cross-Seed / Autobrr Torznab feeds to /api/v1/torznab/{indexerId}/api?apikey=<prowlarr_compat_key>.",
            "5. Stop legacy Sonarr/Radarr/Lidarr/Readarr/Prowlarr containers when satisfied (0 files were moved on disk, rollback remains instant).",
        ];

        return new CutoverExecutionResult(
            RunId: runId,
            ExecutedAt: now,
            FinalDeltaReport: deltaReport,
            GeneratedCompatKeys: generatedKeys,
            EcosystemInstructions: instructions);
    }


    // -------------------------------------------------------------------------
    // Idempotent Persistence into Managerr SQLite Core
    // -------------------------------------------------------------------------

    private async Task ApplyMigrationStateAsync(
        bool upliftLanguageCustomFormats,
        List<UpsertIndexerProxyRequest> proxies,
        List<(UpsertIndexerRequest Request, string CanonicalSource)> dedupedIndexers,
        List<(UpsertDownloadClientRequest Request, List<string> MergedSources)> consolidatedClients,
        List<CreateRemotePathMappingRequest> remoteMappings,
        List<string> customFormatPayloads,
        Dictionary<string, (string Title, string Reason)> blocklistHashes,
        List<StageOverseerrUser> overseerrUsers,
        List<StageSonarrSeries> sonarrSeries,
        List<StageRadarrMovie> radarrMovies,
        List<StageLidarrArtist> lidarrArtists,
        List<UnifiedStagedBook> unifiedBooks,
        List<StageOverseerrRequest> overseerrRequests,
        List<string> logs,
        CancellationToken ct)
    {
        // 1. Proxies & Deduplicated Indexers
        var existingProxies = (await indexerService.ListProxiesAsync(ct)).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var px in proxies)
        {
            if (existingProxies.Add(px.Name))
            {
                await indexerService.CreateProxyAsync(px, ct);
            }
        }

        var existingIndexersList = await indexerService.ListIndexersAsync(ct);
        var existingIndexerNames = existingIndexersList.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingIndexerEndpoints = existingIndexersList
            .Where(i => !string.IsNullOrWhiteSpace(i.BaseUrl) &&
                        !(i.BaseUrl.Trim().TrimEnd('/').Equals("https://indexer.local", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(i.DefinitionId)))
            .Select(i => (
                Protocol: i.Protocol?.Trim().ToLowerInvariant() ?? "",
                BaseUrl: i.BaseUrl?.Trim().TrimEnd('/').ToLowerInvariant() ?? "",
                DefinitionId: i.DefinitionId?.Trim().ToLowerInvariant() ?? ""))
            .ToHashSet();

        foreach (var (idxReq, _) in dedupedIndexers)
        {
            var defId = idxReq.DefinitionId?.Trim().ToLowerInvariant() ?? "";
            var bUrl = idxReq.BaseUrl?.Trim().TrimEnd('/').ToLowerInvariant() ?? "";
            var isPlaceholderWithoutDef = bUrl.Equals("https://indexer.local", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(defId);

            var endpointKey = (
                Protocol: idxReq.Protocol?.Trim().ToLowerInvariant() ?? "",
                BaseUrl: bUrl,
                DefinitionId: defId);

            if (existingIndexerNames.Contains(idxReq.Name))
            {
                continue;
            }

            if (!isPlaceholderWithoutDef && existingIndexerEndpoints.Contains(endpointKey))
            {
                continue;
            }

            existingIndexerNames.Add(idxReq.Name);
            if (!isPlaceholderWithoutDef)
            {
                existingIndexerEndpoints.Add(endpointKey);
            }

            var tags = (idxReq.Tags ?? Array.Empty<string>()).Concat(new[] { "migrated-arr" }).Distinct().ToList();
            await indexerService.CreateIndexerAsync(idxReq with { Tags = tags }, ct);
        }

        // 2. Consolidated Download Clients & Remote Path Mappings
        var existingClients = (await downloadOrchestrator.ListClientsAsync(ct)).ToList();
        var existingRpms = (await downloadOrchestrator.ListRemotePathMappingsAsync(ct)).ToList();

        bool HasDownloadsMapping(long clientId, string host, string targetLocalPath) =>
            existingRpms.Any(e => (e.DownloadClientId == clientId || e.DownloadClientId is null or <= 0) &&
                                  string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase) &&
                                  (e.RemotePath.Equals("/downloads", StringComparison.OrdinalIgnoreCase) ||
                                   e.RemotePath.Equals("/downloads/", StringComparison.OrdinalIgnoreCase)) &&
                                  e.LocalPath.TrimEnd('/').Equals(targetLocalPath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) ||
            remoteMappings.Any(r => (r.DownloadClientId == clientId || r.DownloadClientId is null or <= 0) &&
                                   string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase) &&
                                   (r.RemotePath.Equals("/downloads", StringComparison.OrdinalIgnoreCase) ||
                                    r.RemotePath.Equals("/downloads/", StringComparison.OrdinalIgnoreCase)) &&
                                   r.LocalPath.TrimEnd('/').Equals(targetLocalPath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

        foreach (var (dcReq, _) in consolidatedClients)
        {
            long clientId;
            var match = existingClients.FirstOrDefault(c =>
                string.Equals(c.Name, dcReq.Name, StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(c.Implementation, dcReq.Implementation, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(c.Host, dcReq.Host, StringComparison.OrdinalIgnoreCase) &&
                 c.Port == dcReq.Port));

            if (match is not null)
            {
                clientId = match.Id;
                var mergedCats = new Dictionary<string, string>(match.CategoryMappings, StringComparer.OrdinalIgnoreCase);
                if (dcReq.CategoryMappings is not null)
                {
                    foreach (var kv in dcReq.CategoryMappings)
                    {
                        if (!mergedCats.ContainsKey(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                        {
                            mergedCats[kv.Key] = kv.Value;
                        }
                    }
                }

                var safeUpdateReq = new UpsertDownloadClientRequest(
                    Name: match.Name,
                    Implementation: match.Implementation,
                    Host: match.Host,
                    Port: match.Port,
                    UseSsl: match.UseSsl,
                    UrlBase: match.UrlBase ?? dcReq.UrlBase,
                    Username: match.Username ?? dcReq.Username,
                    Password: match.PasswordSecret.IsSet ? null : dcReq.Password,
                    ApiKey: match.ApiKeySecret.IsSet ? null : dcReq.ApiKey,
                    CategoryMappings: mergedCats,
                    Priority: match.Priority,
                    IsEnabled: match.IsEnabled,
                    RemoveCompleted: match.RemoveCompleted,
                    RemoveFailed: match.RemoveFailed);

                var updated = await downloadOrchestrator.UpdateClientAsync(match.Id, safeUpdateReq, ct);
                existingClients.Remove(match);
                existingClients.Add(updated);
            }
            else
            {
                var extraSettings = new Dictionary<string, string>(dcReq.ExtraSettings ?? new())
                {
                    ["migrated_by"] = "managerr_migration"
                };
                var created = await downloadOrchestrator.CreateClientAsync(dcReq with { ExtraSettings = extraSettings }, ct);
                clientId = created.Id;
                existingClients.Add(created);
            }

            var clientHost = dcReq.Host;
            var isUsenet = string.Equals(dcReq.Implementation, "sabnzbd", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(dcReq.Implementation, "nzbget", StringComparison.OrdinalIgnoreCase);
            var isTorrent = string.Equals(dcReq.Implementation, "qbittorrent", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(dcReq.Implementation, "transmission", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(dcReq.Implementation, "deluge", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(dcReq.Implementation, "rtorrent", StringComparison.OrdinalIgnoreCase);

            if (isUsenet && Directory.Exists("/downloads/usenet") && !HasDownloadsMapping(clientId, clientHost, "/downloads/usenet"))
            {
                var autoRpm = new CreateRemotePathMappingRequest(clientId, clientHost, "/downloads/", "/downloads/usenet/");
                var created = await downloadOrchestrator.CreateRemotePathMappingAsync(autoRpm, ct);
                existingRpms.Add(created);
            }
            else if (isTorrent && Directory.Exists("/downloads/torrents") && !HasDownloadsMapping(clientId, clientHost, "/downloads/torrents"))
            {
                var autoRpm = new CreateRemotePathMappingRequest(clientId, clientHost, "/downloads/", "/downloads/torrents/");
                var created = await downloadOrchestrator.CreateRemotePathMappingAsync(autoRpm, ct);
                existingRpms.Add(created);
            }
        }

        foreach (var rpm in remoteMappings)
        {
            if (PolymorphicCatalogService.IsForbiddenSystemRoot(rpm.LocalPath))
            {
                continue;
            }

            if (!existingRpms.Any(e => e.DownloadClientId == rpm.DownloadClientId &&
                                       e.Host.Equals(rpm.Host, StringComparison.OrdinalIgnoreCase) &&
                                       e.RemotePath.Equals(rpm.RemotePath, StringComparison.OrdinalIgnoreCase) &&
                                       e.LocalPath.Equals(rpm.LocalPath, StringComparison.OrdinalIgnoreCase)))
            {
                var created = await downloadOrchestrator.CreateRemotePathMappingAsync(rpm, ct);
                existingRpms.Add(created);
            }
        }

        // 3. Custom Formats & Language Rule Uplift
        if (customFormatPayloads.Count > 0)
        {
            var combinedArray = $"[{string.Join(",", customFormatPayloads)}]";
            await trashImporter.ImportJsonAsync(combinedArray, ct);
        }

        var policies = await profileService.ListLanguagePoliciesAsync(ct);
        var defaultLangPolicy = policies.FirstOrDefault(p => p.Slug == "de-orig-dual-fast-en") ?? policies.FirstOrDefault();
        if (upliftLanguageCustomFormats && defaultLangPolicy is not null)
        {
            logs.Add($"Uplifted legacy LanguageSpecification Custom Formats to native LanguagePolicy '{defaultLangPolicy.Slug}'.");
        }

        // 4. Recurring Blocklist Hashes
        if (blocklistHashes.Count > 0)
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                async (conn, tx) =>
                {
                    foreach (var kv in blocklistHashes)
                    {
                        var proto = Managerr.Downloaders.ReleaseBlocklistSnapshot.IsSyntheticOrUsenetHash(kv.Key) ? "usenet" : "torrent";
                        var normTitle = Managerr.Downloaders.ReleaseBlocklistSnapshot.NormalizeTitleKey(kv.Value.Title);
                        await conn.ExecuteAsync(
                            """
                            INSERT OR IGNORE INTO recurring_blocklist_hashes (
                                info_hash, title, reason, blocked_at, protocol, normalized_title
                            ) VALUES (
                                @Hash, @Title, @Reason, @Now, @Proto, @NormTitle
                            );
                            """,
                            new
                            {
                                Hash = kv.Key,
                                kv.Value.Title,
                                kv.Value.Reason,
                                Now = now,
                                Proto = proto,
                                NormTitle = normTitle,
                            },
                            transaction: tx);
                    }
                },
                ct);
        }

        // 5. Overseerr PlexUsers Correlation (Plex-First User Directory!)
        var seerrUserIdToManagerrUserId = new Dictionary<long, long>();
        foreach (var ou in overseerrUsers)
        {
            var existing = await plexUserService.GetByPlexUserIdAsync(ou.PlexId, ct);
            var upserted = existing ?? await plexUserService.UpsertFromPlexAsync(
                new UpsertPlexUserCommand(
                    PlexUserId: ou.PlexId,
                    PlexUuid: $"plex-seerr-{ou.PlexId}",
                    Username: ou.Username,
                    Email: ou.Email,
                    AvatarUrl: ou.Avatar,
                    IsServerOwner: false,
                    IsHomeUser: false,
                    IsActive: true,
                    SharedPlexSectionIds: ["1", "2", "3", "4"]),
                ct);

            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                (conn, tx) => conn.ExecuteAsync(
                    "UPDATE users SET seerr_user_id = @SeerrId WHERE id = @UserId;",
                    new { SeerrId = ou.SeerrUserId, UserId = upserted.Id },
                    transaction: tx),
                ct);

            seerrUserIdToManagerrUserId[ou.SeerrUserId] = upserted.Id;
        }

        // Helper to ensure library exists for (mediaType, rootFolderPath)
        var libs = (await catalogService.ListLibrariesAsync(null, ct)).ToList();

        static bool IsCompatibleMediaType(string libraryMediaType, string requestedMediaType)
        {
            if (string.Equals(libraryMediaType, requestedMediaType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            var normLib = libraryMediaType.Trim().ToLowerInvariant();
            var normReq = requestedMediaType.Trim().ToLowerInvariant();
            if (normReq is "movies" or "movie")
            {
                return normLib is "movies" or "movie";
            }
            if (normReq is "tv" or "series")
            {
                return normLib is "tv" or "series";
            }
            if (normReq is "anime")
            {
                return normLib is "anime";
            }
            return PolymorphicCatalogService.LibraryAcceptsWorkKind(normLib, normReq);
        }

        async Task<LibrarySummary> EnsureLibraryAsync(string mediaType, string rootFolderPath, string defaultName)
        {
            var found = libs.FirstOrDefault(l =>
                l.RootFolderPath.Equals(rootFolderPath, StringComparison.OrdinalIgnoreCase) &&
                IsCompatibleMediaType(l.MediaType, mediaType));

            if (found is null && (!Directory.Exists(rootFolderPath) || PolymorphicCatalogService.IsForbiddenSystemRoot(rootFolderPath)))
            {
                found = libs.FirstOrDefault(l =>
                    IsCompatibleMediaType(l.MediaType, mediaType) &&
                    Directory.Exists(l.RootFolderPath));
            }

            if (found is not null)
            {
                return found;
            }

            if (PolymorphicCatalogService.IsForbiddenSystemRoot(rootFolderPath))
            {
                throw new ArgumentException($"Migrated root folder '{rootFolderPath}' points to a protected system directory; provide a valid path mapping.");
            }

            var libName = defaultName;
            if (libs.Any(l => l.Name.Equals(libName, StringComparison.OrdinalIgnoreCase)))
            {
                var folderLeaf = Path.GetFileName(rootFolderPath.TrimEnd('/', '\\'));
                libName = string.IsNullOrWhiteSpace(folderLeaf)
                    ? $"{defaultName} #{libs.Count + 1}"
                    : $"{defaultName} ({folderLeaf})";
            }

            var created = await catalogService.CreateLibraryAsync(
                new CreateLibraryRequest(
                    Name: libName,
                    MediaType: mediaType,
                    RootFolderPath: rootFolderPath),
                ct);
            libs.Add(created);
            return created;
        }

        var existingWorks = (await catalogService.ListWorksAsync(null, null, null, ct)).ToList();
        var tvdbWorkMap = new Dictionary<int, (long WorkId, long LibraryId, string Title)>();
        var tmdbWorkMap = new Dictionary<int, (long WorkId, long LibraryId, string Title)>();

        // 6a. Sonarr Series + Zero-Touch In-Place EpisodeFiles
        foreach (var s in sonarrSeries)
        {
            var vertical = s.SeriesType == "anime" || s.RootFolderPath.Contains("anime", StringComparison.OrdinalIgnoreCase)
                ? "anime"
                : "tv";
            var lib = await EnsureLibraryAsync(vertical, s.RootFolderPath, vertical == "anime" ? "Migrated Anime" : "Migrated TV");

            var existingWork = existingWorks.FirstOrDefault(w =>
                (w.WorkKind is "series" or "tv" or "anime") &&
                ((s.TvdbId > 0 && w.ExternalIds.TryGetValue("tvdb", out var tvdb) && tvdb == s.TvdbId.ToString()) ||
                 (!string.IsNullOrWhiteSpace(s.ImdbId) && w.ExternalIds.TryGetValue("imdb", out var imdb) && imdb.Equals(s.ImdbId, StringComparison.OrdinalIgnoreCase)) ||
                 (w.LibraryId == lib.Id && w.Title.Equals(s.Title, StringComparison.OrdinalIgnoreCase))));

            if (existingWork is not null)
            {
                if (s.TvdbId > 0)
                {
                    tvdbWorkMap[s.TvdbId] = (existingWork.Id, existingWork.LibraryId, existingWork.Title);
                }

                var existingTree = await catalogService.GetWorkTreeAsync(existingWork.Id, null, ct);
                if (existingTree is not null)
                {
                    var targetLib = libs.FirstOrDefault(l => l.Id == existingWork.LibraryId) ?? lib;
                    foreach (var ep in s.Episodes.Where(e => e.FullDiskPath is not null))
                    {
                        var container = existingTree.Containers.FirstOrDefault(c => c.ContainerNumber == ep.SeasonNumber);
                        var unit = container?.Units.FirstOrDefault(u => u.UnitNumber == ep.EpisodeNumber);
                        if (unit is not null && unit.Files.Count == 0)
                        {
                            var resolved = TryResolveSafeTargetFilePath(targetLib.RootFolderPath, s.RootFolderPath, s.SeriesPath, ep.RelativePathInSeries, ep.FullDiskPath!, logs);
                            if (resolved is not null)
                            {
                                await catalogService.AttachFileAsync(
                                    new AttachMediaFileRequest(
                                        UnitId: unit.Id,
                                        RelativePath: resolved.Value.SafeRelativePath,
                                        SizeBytes: Math.Max(0L, ep.FileSize),
                                        QualityName: string.IsNullOrWhiteSpace(ep.QualityName) ? "WEBDL-1080p" : ep.QualityName,
                                        EditionName: null,
                                        Resolution: null,
                                        VideoCodec: null,
                                        HdrFormat: null,
                                        AudioCodec: null,
                                        DetectedAudioLangs: [],
                                        DetectedSubLangs: [],
                                        VerifiedStatus: "ImportedUnverified",
                                        InodeHardlinked: true),
                                    ct);
                                await catalogService.UpdateUnitAsync(unit.Id, new UpdateMediaUnitRequest(QualityCutoffMet: true, LanguageCutoffMet: true), ct);
                            }
                        }
                    }
                }
                continue;
            }

            var extIds = new Dictionary<string, string>();
            if (s.TvdbId > 0)
            {
                extIds["tvdb"] = s.TvdbId.ToString();
            }
            if (!string.IsNullOrWhiteSpace(s.ImdbId))
            {
                extIds["imdb"] = s.ImdbId;
            }

            var seasonGroups = s.Episodes.GroupBy(e => e.SeasonNumber).OrderBy(g => g.Key).ToList();
            var containerSeeds = seasonGroups.Count > 0
                ? seasonGroups.Select(sg => new MetadataContainerSeed(
                    ContainerKind: "season",
                    ContainerNumber: sg.Key,
                    Title: sg.Key == 0 ? "Specials" : $"Season {sg.Key}",
                    ReleaseDate: sg.FirstOrDefault()?.AirDate,
                    Units: sg.OrderBy(e => e.EpisodeNumber).Select(ep => new MetadataUnitSeed(
                        UnitKind: "episode",
                        UnitNumber: ep.EpisodeNumber,
                        AbsoluteNumber: ep.AbsoluteEpisodeNumber,
                        Title: ep.Title,
                        AirOrReleaseDate: ep.AirDate)).ToList())).ToList()
                : null;

            // Create unmonitored first so concurrent background RssSync never sees a transient monitored+missing window before disk files & flags commit
            var tree = await catalogService.AddWorkAsync(
                new CreateMediaWorkRequest(
                    LibraryId: lib.Id,
                    Title: s.Title,
                    WorkKind: vertical,
                    PrimaryProvider: "tvdb",
                    Year: s.Year,
                    Overview: s.Overview ?? "",
                    PosterUrl: s.PosterUrl,
                    Monitored: false,
                    MonitorMode: "none",
                    ExternalIds: extIds,
                    AltTitles: s.AltTitles,
                    Containers: containerSeeds,
                    Tags: ["migrated-arr"],
                    OriginalLanguage: s.OriginalLanguage,
                    SkipUpstreamHydration: true),
                null,
                ct);

            existingWorks.Add(tree.Work);

            if (s.TvdbId > 0)
            {
                tvdbWorkMap[s.TvdbId] = (tree.Work.Id, lib.Id, s.Title);
            }

            // Attach staged Sonarr files directly without disk probing or language checking
            foreach (var ep in s.Episodes.Where(e => e.FullDiskPath is not null))
            {
                var container = tree.Containers.FirstOrDefault(c => c.ContainerNumber == ep.SeasonNumber);
                var unit = container?.Units.FirstOrDefault(u => u.UnitNumber == ep.EpisodeNumber);
                if (unit is null) continue;

                var resolved = TryResolveSafeTargetFilePath(lib.RootFolderPath, s.RootFolderPath, s.SeriesPath, ep.RelativePathInSeries, ep.FullDiskPath!, logs);
                if (resolved is null) continue;

                await catalogService.AttachFileAsync(
                    new AttachMediaFileRequest(
                        UnitId: unit.Id,
                        RelativePath: resolved.Value.SafeRelativePath,
                        SizeBytes: Math.Max(0L, ep.FileSize),
                        QualityName: string.IsNullOrWhiteSpace(ep.QualityName) ? "WEBDL-1080p" : ep.QualityName,
                        EditionName: null,
                        Resolution: null,
                        VideoCodec: null,
                        HdrFormat: null,
                        AudioCodec: null,
                        DetectedAudioLangs: [],
                        DetectedSubLangs: [],
                        VerifiedStatus: "ImportedUnverified",
                        InodeHardlinked: true),
                    ct);
                await catalogService.UpdateUnitAsync(unit.Id, new UpdateMediaUnitRequest(QualityCutoffMet: true, LanguageCutoffMet: true), ct);
            }

            // Now apply exact Sonarr work, season & episode Monitored flags in a single atomic SQLite write transaction
            var epMonByKey = new Dictionary<(int Season, int Episode), bool>();
            foreach (var ep in s.Episodes)
            {
                epMonByKey[(ep.SeasonNumber, ep.EpisodeNumber)] = ep.Monitored;
            }
            var seasonsInStaged = s.Episodes.Select(e => e.SeasonNumber).ToHashSet();

            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                async (conn, tx) =>
                {
                    foreach (var container in tree.Containers)
                    {
                        bool seasonMon;
                        if (!s.Monitored)
                        {
                            seasonMon = false;
                        }
                        else if (s.SeasonMonitored is not null && s.SeasonMonitored.TryGetValue(container.ContainerNumber, out var sm))
                        {
                            seasonMon = sm;
                        }
                        else if (seasonsInStaged.Contains(container.ContainerNumber))
                        {
                            seasonMon = s.Episodes.Where(e => e.SeasonNumber == container.ContainerNumber).Any(e => e.Monitored);
                        }
                        else
                        {
                            seasonMon = false;
                        }

                        await conn.ExecuteAsync("UPDATE media_containers SET monitored = @Mon WHERE id = @Id;", new { Mon = seasonMon ? 1 : 0, container.Id }, transaction: tx);

                        foreach (var unit in container.Units)
                        {
                            bool unitMon = s.Monitored && seasonMon && epMonByKey.TryGetValue((container.ContainerNumber, unit.UnitNumber), out var em) && em;
                            await conn.ExecuteAsync("UPDATE media_units SET monitored = @Mon WHERE id = @Id;", new { Mon = unitMon ? 1 : 0, unit.Id }, transaction: tx);
                        }
                    }

                    await conn.ExecuteAsync("UPDATE media_works SET monitored = @Mon WHERE id = @Id;", new { Mon = s.Monitored ? 1 : 0, tree.Work.Id }, transaction: tx);
                },
                ct);
        }

        // 6b. Radarr Movies + Zero-Touch In-Place MovieFiles
        foreach (var m in radarrMovies)
        {
            var defaultName = (m.RootFolderPath.Contains("animefilme", StringComparison.OrdinalIgnoreCase) ||
                               m.RootFolderPath.Contains("animemov", StringComparison.OrdinalIgnoreCase))
                ? "Migrated Anime Movies"
                : "Migrated Movies";
            var lib = await EnsureLibraryAsync("movies", m.RootFolderPath, defaultName);

            var existingWork = existingWorks.FirstOrDefault(w =>
                (w.WorkKind is "movie" or "movies") &&
                ((m.TmdbId > 0 && w.ExternalIds.TryGetValue("tmdb", out var tmdb) && tmdb == m.TmdbId.ToString()) ||
                 (!string.IsNullOrWhiteSpace(m.ImdbId) && w.ExternalIds.TryGetValue("imdb", out var imdb) && imdb.Equals(m.ImdbId, StringComparison.OrdinalIgnoreCase)) ||
                 (w.LibraryId == lib.Id && w.Title.Equals(m.Title, StringComparison.OrdinalIgnoreCase)) ||
                 (m.Year.HasValue && m.Year > 0 && w.Year == m.Year && w.Title.Equals(m.Title, StringComparison.OrdinalIgnoreCase))));

            if (existingWork is not null)
            {
                if (m.TmdbId > 0)
                {
                    tmdbWorkMap[m.TmdbId] = (existingWork.Id, existingWork.LibraryId, existingWork.Title);
                }

                var existingTree = await catalogService.GetWorkTreeAsync(existingWork.Id, null, ct);
                if (existingTree is not null)
                {
                    var targetLib = libs.FirstOrDefault(l => l.Id == existingWork.LibraryId) ?? lib;
                    var existingUnit = existingTree.Containers.SelectMany(c => c.Units).FirstOrDefault();
                    if (m.FullDiskPath is not null && existingUnit is not null && existingUnit.Files.Count == 0)
                    {
                        var resolved = TryResolveSafeTargetFilePath(targetLib.RootFolderPath, m.RootFolderPath, m.MoviePath, m.RelativeFilePath, m.FullDiskPath, logs);
                        if (resolved is not null)
                        {
                            await catalogService.AttachFileAsync(
                                new AttachMediaFileRequest(
                                    UnitId: existingUnit.Id,
                                    RelativePath: resolved.Value.SafeRelativePath,
                                    SizeBytes: Math.Max(0L, m.FileSize),
                                    QualityName: string.IsNullOrWhiteSpace(m.QualityName) ? "WEBDL-1080p" : m.QualityName,
                                    EditionName: null,
                                    Resolution: null,
                                    VideoCodec: null,
                                    HdrFormat: null,
                                    AudioCodec: null,
                                    DetectedAudioLangs: [],
                                    DetectedSubLangs: [],
                                    VerifiedStatus: "ImportedUnverified",
                                    InodeHardlinked: true),
                                ct);
                            await catalogService.UpdateUnitAsync(existingUnit.Id, new UpdateMediaUnitRequest(QualityCutoffMet: true, LanguageCutoffMet: true), ct);
                        }
                    }
                }
                continue;
            }

            var extIds = new Dictionary<string, string>();
            if (m.TmdbId > 0)
            {
                extIds["tmdb"] = m.TmdbId.ToString();
            }
            if (!string.IsNullOrWhiteSpace(m.ImdbId))
            {
                extIds["imdb"] = m.ImdbId;
            }

            var tree = await catalogService.AddWorkAsync(
                new CreateMediaWorkRequest(
                    LibraryId: lib.Id,
                    Title: m.Title,
                    WorkKind: "movie",
                    PrimaryProvider: "tmdb",
                    Year: m.Year,
                    Overview: m.Overview ?? "",
                    PosterUrl: m.PosterUrl,
                    Monitored: false,
                    MonitorMode: "none",
                    ExternalIds: extIds,
                    AltTitles: m.AltTitles,
                    Tags: ["migrated-arr"],
                    MinimumAvailability: m.MinimumAvailability,
                    OriginalLanguage: m.OriginalLanguage,
                    SkipUpstreamHydration: true),
                null,
                ct);

            existingWorks.Add(tree.Work);

            if (m.TmdbId > 0)
            {
                tmdbWorkMap[m.TmdbId] = (tree.Work.Id, lib.Id, m.Title);
            }

            var unit = tree.Containers.SelectMany(c => c.Units).FirstOrDefault();
            if (m.FullDiskPath is not null && unit is not null)
            {
                var resolved = TryResolveSafeTargetFilePath(lib.RootFolderPath, m.RootFolderPath, m.MoviePath, m.RelativeFilePath, m.FullDiskPath, logs);
                if (resolved is not null)
                {
                    await catalogService.AttachFileAsync(
                        new AttachMediaFileRequest(
                            UnitId: unit.Id,
                            RelativePath: resolved.Value.SafeRelativePath,
                            SizeBytes: Math.Max(0L, m.FileSize),
                            QualityName: string.IsNullOrWhiteSpace(m.QualityName) ? "WEBDL-1080p" : m.QualityName,
                            EditionName: null,
                            Resolution: null,
                            VideoCodec: null,
                            HdrFormat: null,
                            AudioCodec: null,
                            DetectedAudioLangs: [],
                            DetectedSubLangs: [],
                            VerifiedStatus: "ImportedUnverified",
                            InodeHardlinked: true),
                        ct);
                    await catalogService.UpdateUnitAsync(unit.Id, new UpdateMediaUnitRequest(QualityCutoffMet: true, LanguageCutoffMet: true), ct);
                }
            }

            if (m.Monitored)
            {
                await dbFactory.ExecuteWriteAsync(
                    DatabaseKind.Core,
                    async (conn, tx) =>
                    {
                        await conn.ExecuteAsync("UPDATE media_containers SET monitored = 1 WHERE work_id = @WorkId;", new { WorkId = tree.Work.Id }, transaction: tx);
                        await conn.ExecuteAsync("UPDATE media_units SET monitored = 1 WHERE work_id = @WorkId;", new { WorkId = tree.Work.Id }, transaction: tx);
                        await conn.ExecuteAsync("UPDATE media_works SET monitored = 1 WHERE id = @WorkId;", new { WorkId = tree.Work.Id }, transaction: tx);
                    },
                    ct);
            }
        }

        // 6c. Lidarr Artists + Tracks
        foreach (var a in lidarrArtists)
        {
            var lib = await EnsureLibraryAsync("music", a.RootFolderPath, "Migrated Music");
            if (existingWorks.Any(w => w.LibraryId == lib.Id && w.Title.Equals(a.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var extIds = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(a.ForeignArtistId))
            {
                extIds["musicbrainz"] = a.ForeignArtistId;
            }

            var containers = a.Tracks.Count > 0
                ? new List<MetadataContainerSeed>
                {
                    new(
                        ContainerKind: "album",
                        ContainerNumber: 1,
                        Title: "Migrated Discography",
                        ReleaseDate: null,
                        Units: a.Tracks.Select(t => new MetadataUnitSeed("track", t.TrackNumber, null, t.Title, null)).ToList()),
                }
                : null;

            var tree = await catalogService.AddWorkAsync(
                new CreateMediaWorkRequest(
                    LibraryId: lib.Id,
                    Title: a.Name,
                    WorkKind: "music",
                    PrimaryProvider: "musicbrainz",
                    Monitored: a.Monitored,
                    ExternalIds: extIds,
                    Containers: containers,
                    Tags: ["migrated-arr"],
                    SkipUpstreamHydration: true),
                null,
                ct);

            foreach (var tr in a.Tracks.Where(t => t.FullDiskPath is not null))
            {
                var unit = tree.Containers.SelectMany(c => c.Units).FirstOrDefault(u => u.UnitNumber == tr.TrackNumber);
                var resolved = TryResolveSafeTargetFilePath(lib.RootFolderPath, a.RootFolderPath, null, null, tr.FullDiskPath!, logs);
                if (unit is not null && resolved is not null)
                {
                    await catalogService.AttachFileAsync(
                        new AttachMediaFileRequest(
                            UnitId: unit.Id,
                            RelativePath: resolved.Value.SafeRelativePath,
                            SizeBytes: tr.FileSize,
                            QualityName: "FLAC",
                            VerifiedStatus: "ImportedUnverified",
                            InodeHardlinked: true),
                        ct);
                }
            }
        }

        // 6d. Dual-Readarr Unified Books (ebook_slot + audiobook_slot + Goodreads Crosswalk!)
        foreach (var b in unifiedBooks)
        {
            var lib = await EnsureLibraryAsync("ebooks", b.RootFolderPath, "Migrated Books & Audiobooks");
            if (existingWorks.Any(w => w.LibraryId == lib.Id && w.Title.Equals(b.BookTitle, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var extIds = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(b.ForeignBookId))
            {
                extIds["goodreads"] = b.ForeignBookId;
                var slugId = Regex.Replace(b.BookTitle.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
                extIds["openlibrary"] = $"OL-{b.ForeignBookId}";
                extIds["hardcover"] = $"hc-{slugId}";
                extIds["audnexus"] = $"aud-{b.ForeignBookId}";

                await crosswalkService.LinkPairAsync("goodreads", b.ForeignBookId, "openlibrary", extIds["openlibrary"], "ebooks", 0.95, ct);
                await crosswalkService.LinkPairAsync("goodreads", b.ForeignBookId, "hardcover", extIds["hardcover"], "ebooks", 0.95, ct);
                await crosswalkService.LinkPairAsync("goodreads", b.ForeignBookId, "audnexus", extIds["audnexus"], "audiobooks", 0.95, ct);
            }
            if (!string.IsNullOrWhiteSpace(b.Isbn13))
            {
                extIds["isbn13"] = b.Isbn13;
            }

            var tree = await catalogService.AddWorkAsync(
                new CreateMediaWorkRequest(
                    LibraryId: lib.Id,
                    Title: b.BookTitle,
                    WorkKind: "book",
                    PrimaryProvider: "openlibrary",
                    Overview: $"Author: {b.AuthorName}",
                    Monitored: b.Monitored,
                    ExternalIds: extIds,
                    DualFormatBookSlots: true,
                    Tags: ["migrated-arr"],
                    SkipUpstreamHydration: true),
                null,
                ct);

            var allUnits = tree.Containers.SelectMany(c => c.Units).ToList();
            var ebookUnit = allUnits.FirstOrDefault(u => u.UnitKind == "ebook_slot") ?? allUnits.FirstOrDefault();
            var audioUnit = allUnits.FirstOrDefault(u => u.UnitKind == "audiobook_slot") ?? allUnits.LastOrDefault();

            foreach (var bf in b.Files)
            {
                var targetUnit = bf.SlotKind == "audiobook_slot" ? audioUnit : ebookUnit;
                var resolved = TryResolveSafeTargetFilePath(lib.RootFolderPath, b.RootFolderPath, null, null, bf.FullDiskPath, logs);
                if (targetUnit is not null && resolved is not null)
                {
                    await catalogService.AttachFileAsync(
                        new AttachMediaFileRequest(
                            UnitId: targetUnit.Id,
                            RelativePath: resolved.Value.SafeRelativePath,
                            SizeBytes: bf.SizeBytes,
                            QualityName: bf.QualityName,
                            VerifiedStatus: "ImportedUnverified",
                            InodeHardlinked: true),
                        ct);
                }
            }
        }

        // 7. Correlate Overseerr MediaRequests -> PlexUser Ownership on Works & media_requests table
        if (overseerrRequests.Count > 0)
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                async (conn, tx) =>
                {
                    foreach (var req in overseerrRequests)
                    {
                        if (!seerrUserIdToManagerrUserId.TryGetValue(req.RequestedBySeerrUserId, out var managerrUserId))
                        {
                            continue;
                        }

                        (long WorkId, long LibraryId, string Title)? matchedWork = null;
                        if (req.TmdbId.HasValue && tmdbWorkMap.TryGetValue(req.TmdbId.Value, out var mw))
                        {
                            matchedWork = mw;
                        }
                        else if (req.TvdbId.HasValue && tvdbWorkMap.TryGetValue(req.TvdbId.Value, out var sw))
                        {
                            matchedWork = sw;
                        }

                        if (matchedWork is not null)
                        {
                            await conn.ExecuteAsync(
                                "UPDATE media_works SET added_by_user_id = @UserId WHERE id = @WorkId;",
                                new { UserId = managerrUserId, matchedWork.Value.WorkId },
                                transaction: tx);

                            await conn.ExecuteAsync(
                                """
                                INSERT INTO media_requests (
                                    requester_user_id, library_id, work_id, media_vertical,
                                    title, external_ids_json, status, created_at
                                ) VALUES (
                                    @UserId, @LibId, @WorkId, @Vertical,
                                    @Title, @ExtJson, 'approved', @Now
                                );
                                """,
                                new
                                {
                                    UserId = managerrUserId,
                                    LibId = matchedWork.Value.LibraryId,
                                    matchedWork.Value.WorkId,
                                    Vertical = req.MediaType.Equals("tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movies",
                                    matchedWork.Value.Title,
                                    ExtJson = JsonSerializer.Serialize(new { tmdbId = req.TmdbId, tvdbId = req.TvdbId }, JsonOpts),
                                    Now = now,
                                },
                                transaction: tx);
                        }
                    }
                },
                ct);
        }
    }

    // -------------------------------------------------------------------------
    // Utility & JSON Parsing Helpers
    // -------------------------------------------------------------------------


    private sealed class ShadowDecisionRow
    {
        public long id { get; init; }
        public string occurred_at { get; init; } = "";
        public string media_vertical { get; init; } = "";
        public long? work_id { get; init; }
        public string work_title { get; init; } = "";
        public string release_title { get; init; } = "";
        public string managerr_decision { get; init; } = "";
        public double managerr_score { get; init; }
        public string legacy_app { get; init; } = "";
        public string legacy_decision { get; init; } = "";
        public int would_upgrade_language { get; init; }
        public string notes { get; init; } = "";
    }

}
