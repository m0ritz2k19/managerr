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
using Managerr.Downloaders.Parser;
using Managerr.Indexers;
using Managerr.Indexers.Cardigann;
using Managerr.Metadata;
using Managerr.Profiles;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Microsoft.Data.Sqlite;

namespace Managerr.Migration;

public interface ILegacyMigrationService
{
    Task<IReadOnlyList<MigrationSourceSummary>> ListSourcesAsync(CancellationToken ct = default);
    Task<MigrationSourceSummary> UpsertSourceAsync(UpsertMigrationSourceRequest request, long? id = null, CancellationToken ct = default);
    Task DeleteSourceAsync(long id, CancellationToken ct = default);

    Task<ArrApiTokenProbeResult> ProbeArrApiSourceAsync(ArrApiTokenProbeRequest request, CancellationToken ct = default);
    Task<MigrationReportSummary> ImportFromArrApiTokenAsync(ArrApiTokenImportRequest request, CancellationToken ct = default);
    Task<MigrationRollbackResult> RollbackMigratedDataAsync(MigrationRollbackRequest? request = null, CancellationToken ct = default);

    Task<MigrationReportSummary> PlanMigrationAsync(MigrationPlanAndExecuteRequest request, CancellationToken ct = default);
    Task<MigrationReportSummary> ExecuteMigrationAsync(MigrationPlanAndExecuteRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<MigrationReportSummary>> ListMigrationRunsAsync(int limit = 25, CancellationToken ct = default);

    Task<ShadowModeDecisionSummary> EvaluateShadowModeReleaseAsync(ShadowModeEvaluateRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ShadowModeDecisionSummary>> ListShadowModeDecisionsAsync(int limit = 50, CancellationToken ct = default);

    Task<CutoverExecutionResult> ExecuteCutoverAsync(long ownerUserId, CutoverExecuteRequest request, CancellationToken ct = default);
}

#pragma warning disable CS9113
public sealed partial class LegacyArrAndCleanerrMigrator(
    ISqliteDbFactory dbFactory,
    ISecretProtector secretProtector,
    ISafeHttpClientFactory safeHttpFactory,
    IUnifiedIndexerService indexerService,
    ICardigannDefinitionCatalog cardigannCatalog,
    IUnifiedDownloadOrchestrator downloadOrchestrator,
    ITrashGuidesImporter trashImporter,
    IComposableProfileService profileService,
    IExternalIdCrosswalkService crosswalkService,
    IPolymorphicCatalogService catalogService,
    IPlexUserService plexUserService,
    IPlexAuthAndSessionService authService,
    IReleaseParserRouter? parserRouter = null,
    ICatalogDiskScanHook? diskScanHook = null) : ILegacyMigrationService
#pragma warning restore CS9113
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> EbookExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".azw3", ".mobi", ".pdf", ".cbz", ".cbr", ".kepub",
    };

    private static readonly HashSet<string> AudiobookExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m4b", ".mp3", ".flac", ".opus", ".ogg", ".m4a", ".aac",
    };

    public async Task<IReadOnlyList<MigrationSourceSummary>> ListSourcesAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<MigrationSourceRow>("SELECT * FROM migration_sources ORDER BY id ASC;");
        return rows.Select(MapSourceRow).ToList();
    }

    public async Task<MigrationSourceSummary> UpsertSourceAsync(
        UpsertMigrationSourceRequest request,
        long? id = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.FileOrUrl))
        {
            throw new ArgumentException("Migration source name and fileOrUrl are required.");
        }

        var kind = request.SourceKind.Trim().ToLowerInvariant();
        var transport = string.IsNullOrWhiteSpace(request.TransportMode) ? "sqlite_file" : request.TransportMode.Trim().ToLowerInvariant();
        if (!string.Equals(transport, "live_api", StringComparison.OrdinalIgnoreCase) &&
            PolymorphicCatalogService.IsForbiddenSystemRoot(request.FileOrUrl))
        {
            throw new ArgumentException($"Migration source path '{request.FileOrUrl}' points to a protected system or configuration location.");
        }
        var slotHint = string.IsNullOrWhiteSpace(request.ReadarrSlotHint) ? "auto" : request.ReadarrSlotHint.Trim().ToLowerInvariant();
        var mappingsJson = JsonSerializer.Serialize(request.PathMappings ?? new Dictionary<string, string>(), JsonOpts);
        var now = DateTimeOffset.UtcNow.ToString("O");

        var savedId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                string? apiKeyEnc = null;
                if (!string.IsNullOrWhiteSpace(request.ApiKey))
                {
                    apiKeyEnc = secretProtector.Protect(request.ApiKey.Trim());
                }
                else if (id.HasValue)
                {
                    apiKeyEnc = await conn.ExecuteScalarAsync<string?>(
                        "SELECT api_key_enc FROM migration_sources WHERE id = @Id;",
                        new { Id = id.Value },
                        transaction: tx);
                }

                if (id.HasValue)
                {
                    await conn.ExecuteAsync(
                        """
                        UPDATE migration_sources
                        SET name = @Name,
                            source_kind = @Kind,
                            transport_mode = @Transport,
                            file_or_url = @FileOrUrl,
                            api_key_enc = @ApiKeyEnc,
                            readarr_slot_hint = @SlotHint,
                            path_mappings_json = @MappingsJson,
                            is_enabled = @Enabled,
                            updated_at = @Now
                        WHERE id = @Id;
                        """,
                        new
                        {
                            Id = id.Value,
                            Name = request.Name.Trim(),
                            Kind = kind,
                            Transport = transport,
                            FileOrUrl = request.FileOrUrl.Trim(),
                            ApiKeyEnc = apiKeyEnc,
                            SlotHint = slotHint,
                            MappingsJson = mappingsJson,
                            Enabled = request.IsEnabled ? 1 : 0,
                            Now = now,
                        },
                        transaction: tx);
                    return id.Value;
                }

                return await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO migration_sources (
                        name, source_kind, transport_mode, file_or_url, api_key_enc,
                        readarr_slot_hint, path_mappings_json, is_enabled, created_at, updated_at
                    ) VALUES (
                        @Name, @Kind, @Transport, @FileOrUrl, @ApiKeyEnc,
                        @SlotHint, @MappingsJson, @Enabled, @Now, @Now
                    );
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        Name = request.Name.Trim(),
                        Kind = kind,
                        Transport = transport,
                        FileOrUrl = request.FileOrUrl.Trim(),
                        ApiKeyEnc = apiKeyEnc,
                        SlotHint = slotHint,
                        MappingsJson = mappingsJson,
                        Enabled = request.IsEnabled ? 1 : 0,
                        Now = now,
                    },
                    transaction: tx);
            },
            ct);

        var all = await ListSourcesAsync(ct);
        return all.Single(s => s.Id == savedId);
    }

    public Task DeleteSourceAsync(long id, CancellationToken ct = default) =>
        dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync("DELETE FROM migration_sources WHERE id = @Id;", new { Id = id }, transaction: tx),
            ct);


    public Task<MigrationReportSummary> PlanMigrationAsync(
        MigrationPlanAndExecuteRequest request,
        CancellationToken ct = default) =>
        RunPipelineInternalAsync(request, executeChanges: false, ct);

    public Task<MigrationReportSummary> ExecuteMigrationAsync(
        MigrationPlanAndExecuteRequest request,
        CancellationToken ct = default) =>
        RunPipelineInternalAsync(request, executeChanges: true, ct);

    public async Task<IReadOnlyList<MigrationReportSummary>> ListMigrationRunsAsync(int limit = 25, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<MigrationRunRow>(
            "SELECT * FROM migration_runs ORDER BY id DESC LIMIT @Limit;",
            new { Limit = Math.Clamp(limit, 1, 200) });

        var list = new List<MigrationReportSummary>();
        foreach (var r in rows)
        {
            var parsed = JsonSerializer.Deserialize<MigrationReportSummary>(r.summary_json, JsonOpts);
            if (parsed is not null)
            {
                list.Add(parsed with { RunId = r.id });
            }
        }
        return list;
    }

    public async Task<MigrationRollbackResult> RollbackMigratedDataAsync(
        MigrationRollbackRequest? request = null,
        CancellationToken ct = default)
    {
        var removeSources = request?.RemoveSavedSources ?? true;
        var runs = await ListMigrationRunsAsync(200, ct);

        var migratedIndexerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var migratedClientNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in runs.Where(x => string.Equals(x.Mode, "execute", StringComparison.OrdinalIgnoreCase)))
        {
            if (r.Indexers is not null)
            {
                foreach (var ix in r.Indexers)
                {
                    if (!string.IsNullOrWhiteSpace(ix.Name))
                    {
                        migratedIndexerNames.Add(ix.Name.Trim());
                    }
                }
            }

            if (r.DownloadClients is not null)
            {
                foreach (var dc in r.DownloadClients)
                {
                    if (!string.IsNullOrWhiteSpace(dc.Name))
                    {
                        migratedClientNames.Add(dc.Name.Trim());
                    }
                }
            }
        }

        // 1. Identify all migrated works (in "Migrated %" libraries or with "migrated-arr" tag) and delete DB records (0 disk changes)
        var libs = await catalogService.ListLibrariesAsync(null, ct);
        var migratedLibIds = libs
            .Where(l => l.Name.StartsWith("Migrated ", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Id)
            .ToHashSet();

        List<long> workIdsToDelete;
        List<long> preExistingWorkIdsToCleanFiles;
        await using (var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            var allWorks = (await readConn.QueryAsync<(long Id, long LibraryId, string? TagsJson)>(
                "SELECT id, library_id, tags_json AS TagsJson FROM media_works;")).ToList();
            var migratedFileWorkIds = (await readConn.QueryAsync<long>(
                "SELECT DISTINCT work_id FROM media_files WHERE verified_status IN ('ImportedUnverified', 'ImportedVerifiedOnDisk');")).ToHashSet();

            workIdsToDelete = allWorks
                .Where(w => migratedLibIds.Contains(w.LibraryId) || (w.TagsJson?.Contains("\"migrated-arr\"", StringComparison.OrdinalIgnoreCase) ?? false))
                .Select(w => w.Id)
                .Distinct()
                .ToList();

            var workIdsToDeleteSet = workIdsToDelete.ToHashSet();
            preExistingWorkIdsToCleanFiles = migratedFileWorkIds
                .Where(id => !workIdsToDeleteSet.Contains(id))
                .ToList();
        }

        var worksRemoved = 0;
        foreach (var workId in workIdsToDelete)
        {
            await catalogService.DeleteWorkAsync(workId, ct);
            worksRemoved++;
        }

        // For pre-existing works that were not deleted, remove only the backfilled media_files attached during migration
        if (preExistingWorkIdsToCleanFiles.Count > 0)
        {
            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                (conn, tx) => conn.ExecuteAsync(
                    "DELETE FROM media_files WHERE work_id IN @WorkIds AND verified_status IN ('ImportedUnverified', 'ImportedVerifiedOnDisk');",
                    new { WorkIds = preExistingWorkIdsToCleanFiles.ToArray() },
                    transaction: tx),
                ct);
        }

        // 2. Remove empty "Migrated %" libraries, migrated indexers, download clients, custom formats, blocklist hashes, and sources/runs
        var libsRemoved = 0;
        var indexersRemoved = 0;
        var clientsRemoved = 0;
        var cfsRemoved = 0;
        var blocklistRemoved = 0;
        var sourcesRemoved = 0;
        var runsCleared = 0;

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                foreach (var libId in migratedLibIds)
                {
                    var remaining = await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(1) FROM media_works WHERE library_id = @LibId;",
                        new { LibId = libId },
                        transaction: tx);
                    if (remaining == 0)
                    {
                        libsRemoved += await conn.ExecuteAsync(
                            "DELETE FROM libraries WHERE id = @LibId;",
                            new { LibId = libId },
                            transaction: tx);
                    }
                }

                if (migratedIndexerNames.Count > 0)
                {
                    indexersRemoved += await conn.ExecuteAsync(
                        "DELETE FROM indexers WHERE name IN @Names AND tags_json LIKE '%\"migrated-arr\"%';",
                        new { Names = migratedIndexerNames.ToArray() },
                        transaction: tx);
                }

                if (migratedClientNames.Count > 0)
                {
                    clientsRemoved += await conn.ExecuteAsync(
                        "DELETE FROM download_clients WHERE name IN @Names AND extra_settings_json LIKE '%\"migrated_by\":\"managerr_migration\"%';",
                        new { Names = migratedClientNames.ToArray() },
                        transaction: tx);
                }

                cfsRemoved += await conn.ExecuteAsync(
                    "DELETE FROM custom_formats WHERE trash_id LIKE 'legacy-%' OR trash_id LIKE 'migrated-%';",
                    transaction: tx);

                blocklistRemoved += await conn.ExecuteAsync(
                    "DELETE FROM recurring_blocklist_hashes WHERE reason LIKE 'Migrated from %' OR reason LIKE 'Migrated Usenet NZB%';",
                    transaction: tx);

                if (removeSources)
                {
                    sourcesRemoved += await conn.ExecuteAsync("DELETE FROM migration_sources;", transaction: tx);
                    runsCleared += await conn.ExecuteAsync("DELETE FROM migration_runs;", transaction: tx);
                }
            },
            ct);

        return new MigrationRollbackResult(
            WorksRemovedCount: worksRemoved,
            LibrariesRemovedCount: libsRemoved,
            IndexersRemovedCount: indexersRemoved,
            DownloadClientsRemovedCount: clientsRemoved,
            CustomFormatsRemovedCount: cfsRemoved,
            BlocklistHashesRemovedCount: blocklistRemoved,
            SourcesRemovedCount: sourcesRemoved,
            RunsClearedCount: runsCleared,
            BytesMovedOrModified: 0L,
            Message: $"Removed {worksRemoved} migrated works, {libsRemoved} migrated libraries, {indexersRemoved} indexers, {clientsRemoved} download clients, {cfsRemoved} custom formats, and {blocklistRemoved} blocklist entries (0 bytes touched on disk).");
    }


    // -------------------------------------------------------------------------
    // Core Deterministic 7-Phase Pipeline
    // -------------------------------------------------------------------------

    private async Task<MigrationReportSummary> RunPipelineInternalAsync(
        MigrationPlanAndExecuteRequest request,
        bool executeChanges,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var tempExtractDirs = new List<string>();

        try
        {
            var resolvedSources = await ResolveActiveSourcesAsync(request, tempExtractDirs, ct);
            var globalMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (request.GlobalPathMappings is not null)
            {
                foreach (var kv in request.GlobalPathMappings)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    {
                        globalMappings[kv.Key.Trim()] = kv.Value.Trim();
                    }
                }
            }

            var logs = new List<string>();
            var warnings = new List<string>();
            var unmappedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inspectedSources = new List<SourceInspectionSummary>();

            var rawIndexers = new List<StageIndexerCandidate>();
            var prowlarrMappedAppIndexerIds = new HashSet<int>();
            var importedProxies = new List<UpsertIndexerProxyRequest>();
            var rawClients = new List<StageDownloadClientCandidate>();
            var rawRemoteMappings = new List<CreateRemotePathMappingRequest>();
            var customFormatPayloads = new List<string>();
            var languageUpliftCount = 0;
            var compiledProfilesCount = 0;
            var blocklistHashes = new Dictionary<string, (string Title, string Reason)>(StringComparer.OrdinalIgnoreCase);

            var sonarrSeriesItems = new List<StageSonarrSeries>();
            var radarrMovieItems = new List<StageRadarrMovie>();
            var lidarrArtistItems = new List<StageLidarrArtist>();
            var readarrBookItems = new List<StageReadarrBook>();
            var overseerrUsers = new List<StageOverseerrUser>();
            var overseerrRequests = new List<StageOverseerrRequest>();

            var existingLibraries = await catalogService.ListLibrariesAsync(null, ct);

            // Order sources deterministically so Prowlarr/NZBHydra2 & Overseerr run before *arr verticals
            var orderedSources = resolvedSources
                .OrderBy(s => s.SourceKind switch
                {
                    MigrationSourceKinds.Prowlarr => 0,
                    MigrationSourceKinds.NzbHydra2 => 1,
                    MigrationSourceKinds.Overseerr => 2,
                    MigrationSourceKinds.Sonarr => 3,
                    MigrationSourceKinds.Radarr => 4,
                    MigrationSourceKinds.Lidarr => 5,
                    MigrationSourceKinds.Readarr => 6,
                    _ => 9,
                })
                .ToList();

            foreach (var src in orderedSources)
            {
                var effectivePathMap = MergePathMaps(globalMappings, src.PathMappings, existingLibraries);

                if (string.Equals(src.TransportMode, "live_api", StringComparison.OrdinalIgnoreCase))
                {
                    var (cfUpliftDelta, qpCountDelta) = await InspectLiveApiSourceAsync(
                        src,
                        effectivePathMap,
                        inspectedSources,
                        unmappedPaths,
                        rawIndexers,
                        prowlarrMappedAppIndexerIds,
                        importedProxies,
                        rawClients,
                        rawRemoteMappings,
                        customFormatPayloads,
                        blocklistHashes,
                        sonarrSeriesItems,
                        radarrMovieItems,
                        lidarrArtistItems,
                        readarrBookItems,
                        overseerrUsers,
                        overseerrRequests,
                        warnings,
                        logs,
                        ct);
                    languageUpliftCount += cfUpliftDelta;
                    compiledProfilesCount += qpCountDelta;
                    continue;
                }

                if (src.SourceKind == MigrationSourceKinds.NzbHydra2 ||
                    src.ResolvedLocalPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                    src.ResolvedLocalPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                {
                    await ParseNzbHydraYamlAsync(src, inspectedSources, rawIndexers, warnings, logs, ct);
                    continue;
                }

                if (!File.Exists(src.ResolvedLocalPath))
                {
                    warnings.Add($"Source '{src.Name}' file not found at '{src.ResolvedLocalPath}'.");
                    continue;
                }

                await using var legacyConn = OpenReadOnlySqlite(src.ResolvedLocalPath);
                await legacyConn.OpenAsync(ct);

                var tables = (await legacyConn.QueryAsync<string>(
                    "SELECT name FROM sqlite_master WHERE type = 'table';")).ToHashSet(StringComparer.OrdinalIgnoreCase);

                int? schemaVersion = null;
                if (tables.Contains("VersionInfo"))
                {
                    schemaVersion = await legacyConn.ExecuteScalarAsync<int?>("SELECT MAX(Version) FROM VersionInfo;");
                }

                var discoveredRoots = new List<string>();
                if (tables.Contains("RootFolders"))
                {
                    var roots = await legacyConn.QueryAsync<string>("SELECT Path FROM RootFolders WHERE Path IS NOT NULL;");
                    foreach (var r in roots)
                    {
                        var mapped = RemapPath(r, effectivePathMap);
                        discoveredRoots.Add(mapped);
                        if (!Directory.Exists(mapped))
                        {
                            unmappedPaths.Add($"{src.Name}: {r} -> {mapped}");
                        }
                    }
                }

                // Common Servarr tables: DownloadClients, RemotePathMappings, Indexers, CustomFormats, QualityProfiles, Blocklist
                if (tables.Contains("DownloadClients"))
                {
                    var dcRows = await legacyConn.QueryAsync<LegacySettingRow>(
                        "SELECT Id, Name, Implementation, Settings, Enable FROM DownloadClients;");
                    foreach (var dc in dcRows)
                    {
                        var parsed = ParseLegacyDownloadClient(src, dc);
                        if (parsed is not null)
                        {
                            rawClients.Add(parsed);
                        }
                    }
                }

                if (tables.Contains("RemotePathMappings"))
                {
                    var rpmRows = await legacyConn.QueryAsync<LegacyRemotePathRow>(
                        "SELECT Host, RemotePath, LocalPath FROM RemotePathMappings;");
                    foreach (var rpm in rpmRows)
                    {
                        if (!string.IsNullOrWhiteSpace(rpm.Host) && !string.IsNullOrWhiteSpace(rpm.RemotePath) && !string.IsNullOrWhiteSpace(rpm.LocalPath))
                        {
                            var remappedLocal = RemapPath(rpm.LocalPath.Trim(), effectivePathMap);
                            if (PolymorphicCatalogService.IsForbiddenSystemRoot(remappedLocal))
                            {
                                warnings.Add($"Skipping RemotePathMapping with forbidden local path '{remappedLocal}' for host '{rpm.Host}'.");
                                continue;
                            }

                            rawRemoteMappings.Add(new CreateRemotePathMappingRequest(
                                null,
                                rpm.Host.Trim(),
                                rpm.RemotePath.Trim(),
                                remappedLocal));
                        }
                    }
                }

                if (tables.Contains("IndexerProxies"))
                {
                    var pxRows = await legacyConn.QueryAsync<LegacySettingRow>(
                        "SELECT Id, Name, Implementation, Settings, 1 AS Enable FROM IndexerProxies;");
                    foreach (var px in pxRows)
                    {
                        var parsedPx = ParseLegacyIndexerProxy(px);
                        if (parsedPx is not null)
                        {
                            importedProxies.Add(parsedPx);
                        }
                    }
                }

                if (src.SourceKind == MigrationSourceKinds.Prowlarr && tables.Contains("ApplicationIndexerMapping"))
                {
                    var appMappings = await legacyConn.QueryAsync<int>("SELECT AppIndexerId FROM ApplicationIndexerMapping;");
                    foreach (var appIdxId in appMappings)
                    {
                        prowlarrMappedAppIndexerIds.Add(appIdxId);
                    }
                }

                if (tables.Contains("Indexers"))
                {
                    var idxRows = await legacyConn.QueryAsync<LegacyIndexerRow>(
                        "SELECT Id, Name, Implementation, Settings, ConfigContract FROM Indexers;");
                    foreach (var idx in idxRows)
                    {
                        rawIndexers.Add(ParseLegacyIndexer(src, idx));
                    }
                }

                if (tables.Contains("CustomFormats"))
                {
                    var cfRows = await legacyConn.QueryAsync<LegacyCustomFormatRow>(
                        "SELECT Id, Name, IncludeCustomFormatWhenRenaming, Specifications FROM CustomFormats;");
                    foreach (var cf in cfRows)
                    {
                        var specsJson = string.IsNullOrWhiteSpace(cf.Specifications) ? "[]" : cf.Specifications;
                        if (specsJson.Contains("LanguageSpecification", StringComparison.OrdinalIgnoreCase))
                        {
                            languageUpliftCount++;
                        }

                        var cfObj = new
                        {
                            trash_id = $"migrated-{src.SourceKind}-cf-{cf.Id}",
                            name = cf.Name,
                            includeCustomFormatWhenRenaming = cf.IncludeCustomFormatWhenRenaming != 0,
                            specifications = JsonNode.Parse(specsJson),
                        };
                        customFormatPayloads.Add(JsonSerializer.Serialize(cfObj, JsonOpts));
                    }
                }

                if (tables.Contains("QualityProfiles"))
                {
                    var qpCount = await legacyConn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM QualityProfiles;");
                    compiledProfilesCount += qpCount;
                }

                if (tables.Contains("Blocklist"))
                {
                    var cols = await GetTableColumnsAsync(legacyConn, "Blocklist");
                    if (cols.Contains("TorrentInfoHash"))
                    {
                        var blRows = await legacyConn.QueryAsync<LegacyBlocklistRow>(
                            "SELECT SourceTitle, TorrentInfoHash FROM Blocklist;");
                        foreach (var bl in blRows)
                        {
                            if (!string.IsNullOrWhiteSpace(bl.TorrentInfoHash))
                            {
                                blocklistHashes[bl.TorrentInfoHash.Trim().ToLowerInvariant()] =
                                    (bl.SourceTitle ?? "Migrated Blocklist Entry", $"Migrated from {src.Name}");
                            }
                            else if (!string.IsNullOrWhiteSpace(bl.SourceTitle))
                            {
                                var normNzbTitle = Managerr.Downloaders.ReleaseBlocklistSnapshot.NormalizeTitleKey(bl.SourceTitle);
                                if (normNzbTitle.Length >= 4)
                                {
                                    var nzbKey = Managerr.Downloaders.ReleaseBlocklistSnapshot.ComputeNzbKeyFromToken($"legacy_nzb:{normNzbTitle}");
                                    blocklistHashes[nzbKey] = (bl.SourceTitle.Trim(), $"Migrated Usenet NZB from {src.Name}");
                                }
                            }
                        }
                    }
                }

                // Vertical-specific extraction
                var itemsFound = 0;
                var filesFound = 0;

                switch (src.SourceKind)
                {
                    case MigrationSourceKinds.Sonarr:
                        (itemsFound, filesFound) = await ExtractSonarrCatalogAsync(legacyConn, tables, effectivePathMap, sonarrSeriesItems);
                        break;
                    case MigrationSourceKinds.Radarr:
                        (itemsFound, filesFound) = await ExtractRadarrCatalogAsync(legacyConn, tables, effectivePathMap, radarrMovieItems);
                        break;
                    case MigrationSourceKinds.Lidarr:
                        (itemsFound, filesFound) = await ExtractLidarrCatalogAsync(legacyConn, tables, effectivePathMap, lidarrArtistItems);
                        break;
                    case MigrationSourceKinds.Readarr:
                        (itemsFound, filesFound) = await ExtractReadarrCatalogAsync(src, legacyConn, tables, effectivePathMap, readarrBookItems);
                        break;
                    case MigrationSourceKinds.Overseerr:
                        (itemsFound, filesFound) = await ExtractOverseerrStateAsync(legacyConn, tables, overseerrUsers, overseerrRequests);
                        break;
                    case MigrationSourceKinds.Prowlarr:
                        itemsFound = rawIndexers.Count(x => x.SourceName == src.Name);
                        break;
                }

                inspectedSources.Add(new SourceInspectionSummary(
                    Name: src.Name,
                    SourceKind: src.SourceKind,
                    TransportMode: src.TransportMode,
                    DetectedSchemaVersion: schemaVersion,
                    DiscoveredRootFolders: discoveredRoots,
                    ItemsDiscovered: itemsFound,
                    FilesDiscovered: filesFound));

                logs.Add($"Inspected {src.Name} ({src.SourceKind}, schema v{schemaVersion?.ToString() ?? "n/a"}): {itemsFound} items, {filesFound} files.");
            }

            // -----------------------------------------------------------------
            // Phase 2: Consolidate Download Clients by (Implementation, Host, Port, Username)
            // -----------------------------------------------------------------
            var consolidatedClients = ConsolidateDownloadClients(rawClients);
            var supportedDriverKeys = downloadOrchestrator.ListSupportedDrivers()
                .Select(d => d.Implementation)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unsupportedClients = consolidatedClients
                .Where(x => !supportedDriverKeys.Contains(x.Request.Implementation))
                .ToList();
            foreach (var (req, sources) in unsupportedClients)
            {
                warnings.Add(
                    $"Download client '{req.Name}' ({req.Implementation}) from {string.Join(", ", sources)} is not supported by Managerr and was skipped.");
            }

            consolidatedClients = consolidatedClients.Except(unsupportedClients).ToList();

            // -----------------------------------------------------------------
            // Phase 3: Deduplicate Indexers & Skip (Prowlarr)/(NZBHydra2) Proxy Clones
            // -----------------------------------------------------------------
            var (dedupedIndexers, skippedProxyClones) = DeduplicateIndexers(rawIndexers, prowlarrMappedAppIndexerIds);

            // Managerr ships no Cardigann tracker definitions, so a migrated Cardigann indexer only works when the user
            // has put its YAML into Definitions/Custom. The rest are left out of the import and named in the report.
            var missingDefinitions = dedupedIndexers
                .Where(x => x.Request.Implementation.Equals("Cardigann", StringComparison.OrdinalIgnoreCase) &&
                            (string.IsNullOrWhiteSpace(x.Request.DefinitionId) || cardigannCatalog.GetDefinition(x.Request.DefinitionId) is null))
                .ToList();
            foreach (var (req, source) in missingDefinitions)
            {
                warnings.Add(
                    $"Indexer '{req.Name}' from {source} uses the Cardigann definition '{req.DefinitionId ?? "(none)"}', which Managerr does not ship. " +
                    $"It was not imported: put {req.DefinitionId ?? "<definition>"}.yml into Definitions/Custom in the config folder and run the migration again, " +
                    "or add the indexer as Newznab/Torznab.");
            }

            dedupedIndexers = dedupedIndexers.Except(missingDefinitions).ToList();

            // -----------------------------------------------------------------
            // Phase 5b: Merge Dual Readarr (Ebook + Audiobook) Books by ForeignBookId / Author+Title
            // -----------------------------------------------------------------
            var (unifiedBooks, dualMergePreviews) = MergeReadarrBooks(readarrBookItems);

            // Count on-disk verified files WITHOUT moving or touching a single byte!
            var verifiedOnDiskCount = CountVerifiedStagedFilesOnDisk(
                sonarrSeriesItems,
                radarrMovieItems,
                lidarrArtistItems,
                unifiedBooks,
                existingLibraries);

            // -----------------------------------------------------------------
            // Execute Persistence (Only when executeChanges == true)
            // -----------------------------------------------------------------
            if (executeChanges)
            {
                await ApplyMigrationStateAsync(
                    request.UpliftLanguageCustomFormats,
                    importedProxies,
                    dedupedIndexers,
                    consolidatedClients,
                    rawRemoteMappings,
                    customFormatPayloads,
                    blocklistHashes,
                    overseerrUsers,
                    sonarrSeriesItems,
                    radarrMovieItems,
                    lidarrArtistItems,
                    unifiedBooks,
                    overseerrRequests,
                    logs,
                    ct);
            }

            var finishedAt = DateTimeOffset.UtcNow;
            var report = new MigrationReportSummary(
                RunId: null,
                Mode: executeChanges ? "execute" : "dry_run",
                UpliftLanguageCustomFormats: request.UpliftLanguageCustomFormats,
                StartedAt: startedAt.ToString("O"),
                FinishedAt: finishedAt.ToString("O"),
                InspectedSources: inspectedSources,
                RawIndexerCount: rawIndexers.Count,
                DeduplicatedIndexerCount: dedupedIndexers.Count,
                SkippedProxyClonesCount: skippedProxyClones,
                Indexers: dedupedIndexers.Select(x => new DeduplicatedIndexerPreview(
                    x.Request.Name,
                    x.Request.Implementation,
                    x.Request.Protocol,
                    x.Request.BaseUrl,
                    x.CanonicalSource)).ToList(),
                ProxiesImportedCount: importedProxies.Count,
                RawDownloadClientCount: rawClients.Count,
                ConsolidatedDownloadClientCount: consolidatedClients.Count,
                DownloadClients: consolidatedClients.Select(x => new ConsolidatedDownloadClientPreview(
                    x.Request.Name,
                    x.Request.Implementation,
                    x.Request.Host,
                    x.Request.Port,
                    x.Request.CategoryMappings ?? new Dictionary<string, string>(),
                    x.MergedSources)).ToList(),
                RemotePathMappingsCount: rawRemoteMappings.Count,
                QualityProfilesCompiledCount: compiledProfilesCount,
                LanguageCustomFormatsUpliftedCount: request.UpliftLanguageCustomFormats ? languageUpliftCount : 0,
                TvSeriesCount: sonarrSeriesItems.Count,
                TvEpisodeFilesCount: sonarrSeriesItems.Sum(s => s.Episodes.Count(e => e.FullDiskPath is not null)),
                MoviesCount: radarrMovieItems.Count,
                MovieFilesCount: radarrMovieItems.Count(m => m.FullDiskPath is not null),
                MusicArtistsCount: lidarrArtistItems.Count,
                MusicTrackFilesCount: lidarrArtistItems.Sum(a => a.Tracks.Count(t => t.FullDiskPath is not null)),
                BooksUnifiedCount: unifiedBooks.Count,
                DualReadarrMergedBooksCount: dualMergePreviews.Count,
                BookFilesCount: unifiedBooks.Sum(b => b.Files.Count),
                DualReadarrMerges: dualMergePreviews,
                OverseerrUsersCorrelatedCount: overseerrUsers.Count,
                OverseerrRequestsCorrelatedCount: overseerrRequests.Count,
                BlocklistHashesImportedCount: blocklistHashes.Count,
                VerifiedOnDiskFilesCount: verifiedOnDiskCount,
                BytesMovedOrModified: 0L, // Strict zero-touch in-place adoption guarantee!
                UnmappedPaths: unmappedPaths.OrderBy(x => x).ToList(),
                Warnings: warnings,
                Logs: logs);

            var runId = await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                (conn, tx) => conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO migration_runs (
                        mode, uplift_language_cfs, status, started_at, finished_at, summary_json
                    ) VALUES (
                        @Mode, @Uplift, 'completed', @Started, @Finished, @SummaryJson
                    );
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        Mode = report.Mode,
                        Uplift = request.UpliftLanguageCustomFormats ? 1 : 0,
                        Started = report.StartedAt,
                        Finished = report.FinishedAt,
                        SummaryJson = JsonSerializer.Serialize(report, JsonOpts),
                    },
                    transaction: tx),
                ct);

            return report with { RunId = runId };
        }
        finally
        {
            foreach (var dir in tempExtractDirs)
            {
                try
                {
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch
                {
                    // Best-effort cleanup of extracted backup zips
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Extraction Helpers for Real Legacy SQLite / YAML / Live API Sources
    // -------------------------------------------------------------------------

    private async Task<List<ResolvedMigrationSource>> ResolveActiveSourcesAsync(
        MigrationPlanAndExecuteRequest request,
        List<string> tempDirs,
        CancellationToken ct)
    {
        var list = new List<ResolvedMigrationSource>();

        if (request.Sources is { Count: > 0 })
        {
            foreach (var s in request.Sources)
            {
                list.Add(PrepareLocalOrArchiveSource(
                    s.Name.Trim(),
                    s.SourceKind.Trim().ToLowerInvariant(),
                    s.TransportMode.Trim().ToLowerInvariant(),
                    s.FileOrUrl.Trim(),
                    s.ApiKey,
                    s.ReadarrSlotHint.Trim().ToLowerInvariant(),
                    new Dictionary<string, string>(),
                    tempDirs));
            }
            return list;
        }

        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<MigrationSourceRow>("SELECT * FROM migration_sources WHERE is_enabled = 1 ORDER BY id ASC;");
        foreach (var r in rows)
        {
            var apiKey = string.IsNullOrWhiteSpace(r.api_key_enc) ? null : secretProtector.Unprotect(r.api_key_enc);
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(r.path_mappings_json, JsonOpts) ?? new Dictionary<string, string>();
            list.Add(PrepareLocalOrArchiveSource(
                r.name,
                r.source_kind,
                r.transport_mode,
                r.file_or_url,
                apiKey,
                r.readarr_slot_hint,
                map,
                tempDirs));
        }

        return list;
    }

    private static ResolvedMigrationSource PrepareLocalOrArchiveSource(
        string name,
        string sourceKind,
        string transportMode,
        string fileOrUrl,
        string? plainApiKey,
        string readarrSlotHint,
        IReadOnlyDictionary<string, string> pathMappings,
        List<string> tempDirs)
    {
        if (!string.Equals(transportMode, "live_api", StringComparison.OrdinalIgnoreCase) &&
            PolymorphicCatalogService.IsForbiddenSystemRoot(fileOrUrl))
        {
            throw new ArgumentException($"Migration source path '{fileOrUrl}' points to a protected system or configuration location.");
        }

        var resolvedPath = fileOrUrl;
        if (!string.Equals(transportMode, "live_api", StringComparison.OrdinalIgnoreCase) &&
            fileOrUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(fileOrUrl))
        {
            var extractDir = Path.Combine(Path.GetTempPath(), $"managerr-mig-{Guid.NewGuid():N}");
            Directory.CreateDirectory(extractDir);
            tempDirs.Add(extractDir);
            ExtractArchiveSafely(fileOrUrl, extractDir);

            var extractedFiles = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories);
            resolvedPath = extractedFiles.FirstOrDefault(f =>
                               f.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ||
                               f.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase) ||
                               f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                               f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                           ?? fileOrUrl;
        }

        return new ResolvedMigrationSource(
            Name: name,
            SourceKind: sourceKind,
            TransportMode: transportMode,
            OriginalFileOrUrl: fileOrUrl,
            ResolvedLocalPath: resolvedPath,
            PlainApiKey: plainApiKey,
            ReadarrSlotHint: readarrSlotHint,
            PathMappings: pathMappings);
    }


    private MigrationSourceSummary MapSourceRow(MigrationSourceRow r) =>
        new(
            Id: r.id,
            Name: r.name,
            SourceKind: r.source_kind,
            TransportMode: r.transport_mode,
            FileOrUrl: r.file_or_url,
            ApiKeySecret: string.IsNullOrWhiteSpace(r.api_key_enc)
                ? new MaskedSecret(false, null)
                : new MaskedSecret(true, "***configured***"),
            ReadarrSlotHint: r.readarr_slot_hint,
            PathMappings: JsonSerializer.Deserialize<Dictionary<string, string>>(r.path_mappings_json, JsonOpts)
                          ?? new Dictionary<string, string>(),
            IsEnabled: r.is_enabled != 0,
            LastSyncedAt: r.last_synced_at,
            CreatedAt: r.created_at,
            UpdatedAt: r.updated_at);

    public static string NormalizeSingleLanguageNameToIso2(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "en";
        var clean = raw.Trim().ToLowerInvariant();
        return clean switch
        {
            "english" or "eng" or "en" => "en",
            "german" or "deutsch" or "deu" or "ger" or "de" => "de",
            "japanese" or "jpn" or "ja" => "ja",
            "french" or "fra" or "fre" or "fr" => "fr",
            "spanish" or "spa" or "es" => "es",
            "italian" or "ita" or "it" => "it",
            "portuguese" or "por" or "pt" => "pt",
            "dutch" or "nld" or "dut" or "nl" => "nl",
            "polish" or "pol" or "pl" => "pl",
            "russian" or "rus" or "ru" => "ru",
            "turkish" or "tur" or "tr" => "tr",
            "korean" or "kor" or "ko" => "ko",
            "chinese" or "zho" or "chi" or "zh" => "zh",
            "swedish" or "swe" or "sv" => "sv",
            "danish" or "dan" or "da" => "da",
            "norwegian" or "nor" or "no" or "nob" or "nno" => "no",
            "finnish" or "fin" or "fi" => "fi",
            _ => "en",
        };
    }

    public static string ExtractArrOriginalLanguageIso2(JsonNode? langNode)
    {
        if (langNode is null) return "en";
        if (langNode is JsonObject obj)
        {
            var name = obj["name"]?.GetValue<string>() ?? obj["name"]?.ToString();
            return NormalizeSingleLanguageNameToIso2(name);
        }
        if (langNode is JsonValue val)
        {
            var str = val.TryGetValue<string>(out var s) ? s : val.ToString();
            return NormalizeSingleLanguageNameToIso2(str);
        }
        return "en";
    }

    public static string NormalizeRadarrMinimumAvailability(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "released";
        var clean = raw.Trim().ToLowerInvariant();
        return clean switch
        {
            "0" or "1" or "tba" or "announced" => "announced",
            "2" or "incinemas" or "in_cinemas" or "in cinemas" => "inCinemas",
            _ => "released",
        };
    }

    internal static bool IsMaskedArrSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return true;
        if (trimmed.All(c => c == '*' || c == '•')) return true;
        return trimmed.Equals("********", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("••••••••", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("<redacted>", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("__REDACTED__", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("***configured***", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> MergePathMaps(
        IReadOnlyDictionary<string, string> globalMap,
        IReadOnlyDictionary<string, string> sourceMap,
        IReadOnlyList<LibrarySummary>? existingLibraries = null)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Common *arr container mounts (/tv, /movies, ...) mapped onto Managerr's layout when only the latter exists here.
        (string LegacyPrefix, string[] Candidates)[] defaultContainerMounts =
        [
            ("/tv", ["/media/series", "/media/tv", "/storage/media/tv", "/data/media/tv"]),
            ("/series", ["/media/series", "/storage/media/tv", "/data/media/tv"]),
            ("/anime", ["/media/anime", "/storage/media/anime", "/data/media/anime"]),
            ("/movies", ["/media/movies", "/storage/media/movies", "/data/media/movies"]),
            ("/animemovs", ["/media/animefilme", "/media/anime", "/storage/media/anime"]),
            ("/animemovies", ["/media/animefilme", "/media/anime", "/storage/media/anime"]),
            ("/animefilme", ["/media/animefilme", "/media/anime", "/storage/media/anime"]),
            ("/music", ["/media/music", "/storage/media/music", "/data/media/music"]),
            ("/books", ["/media/books", "/storage/media/books", "/storage/media/ebooks", "/data/media/books"]),
            ("/ebooks", ["/media/books", "/storage/media/books", "/storage/media/ebooks", "/data/media/books"]),
        ];

        foreach (var (legacyPrefix, candidates) in defaultContainerMounts)
        {
            if (!merged.ContainsKey(legacyPrefix) && !Directory.Exists(legacyPrefix))
            {
                foreach (var candidate in candidates)
                {
                    if (Directory.Exists(candidate))
                    {
                        merged[legacyPrefix] = candidate;
                        break;
                    }
                }
            }
        }

        (string LegacyPrefix, string MediaType)[] standardMediaPrefixes =
        [
            ("/tv", "tv"),
            ("/series", "tv"),
            ("/anime", "anime"),
            ("/movies", "movies"),
            ("/animemovs", "movies"),
            ("/animemovies", "movies"),
            ("/animefilme", "movies"),
            ("/music", "music"),
            ("/books", "ebooks"),
            ("/ebooks", "ebooks"),
        ];

        if (existingLibraries is { Count: > 0 })
        {
            foreach (var (legacyPrefix, mediaType) in standardMediaPrefixes)
            {
                if (!merged.ContainsKey(legacyPrefix) && !Directory.Exists(legacyPrefix))
                {
                    if ((legacyPrefix is "/animemovs" or "/animemovies" or "/animefilme") &&
                        Directory.Exists("/media/animefilme"))
                    {
                        continue;
                    }

                    var matches = existingLibraries.Where(l =>
                        string.Equals(l.MediaType, mediaType, StringComparison.OrdinalIgnoreCase) &&
                        Directory.Exists(l.RootFolderPath)).ToList();
                    if (matches.Count == 1)
                    {
                        merged[legacyPrefix] = matches[0].RootFolderPath;
                    }
                }
            }
        }

        foreach (var kv in globalMap)
        {
            if (kv.Value.Equals("/data/media/tv", StringComparison.OrdinalIgnoreCase) &&
                !Directory.Exists("/data/media/tv") &&
                merged.ContainsKey(kv.Key))
            {
                continue;
            }
            merged[kv.Key] = kv.Value;
        }
        foreach (var kv in sourceMap)
        {
            if (kv.Value.Equals("/data/media/tv", StringComparison.OrdinalIgnoreCase) &&
                !Directory.Exists("/data/media/tv") &&
                merged.ContainsKey(kv.Key))
            {
                continue;
            }
            merged[kv.Key] = kv.Value;
        }
        return merged;
    }

    private static int CountVerifiedStagedFilesOnDisk(
        IReadOnlyList<StageSonarrSeries> sonarrSeries,
        IReadOnlyList<StageRadarrMovie> radarrMovies,
        IReadOnlyList<StageLidarrArtist> lidarrArtists,
        IReadOnlyList<UnifiedStagedBook> readarrBooks,
        IReadOnlyList<LibrarySummary> existingLibraries)
    {
        var verified = 0;

        LibrarySummary? FindLibrary(string vertical, string rootFolder)
        {
            return existingLibraries.FirstOrDefault(l =>
                       string.Equals(l.MediaType, vertical, StringComparison.OrdinalIgnoreCase) &&
                       (string.Equals(l.RootFolderPath, rootFolder, StringComparison.OrdinalIgnoreCase) ||
                        l.RootFolderPath.TrimEnd('/', '\\').Equals(rootFolder.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)))
                   ?? existingLibraries.FirstOrDefault(l =>
                       string.Equals(l.MediaType, vertical, StringComparison.OrdinalIgnoreCase) &&
                       Directory.Exists(l.RootFolderPath))
                   ?? existingLibraries.FirstOrDefault(l =>
                       string.Equals(l.MediaType, vertical, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var s in sonarrSeries)
        {
            var vertical = s.SeriesType == "anime" || s.RootFolderPath.Contains("anime", StringComparison.OrdinalIgnoreCase) ? "anime" : "tv";
            var matchingLib = FindLibrary(vertical, s.RootFolderPath);
            var effectiveRoot = matchingLib?.RootFolderPath ?? s.RootFolderPath;

            foreach (var ep in s.Episodes.Where(e => e.FullDiskPath is not null))
            {
                var resolvedOnSource = TryResolveSafeTargetFilePath(s.RootFolderPath, s.RootFolderPath, s.SeriesPath, ep.RelativePathInSeries, ep.FullDiskPath!);
                if (resolvedOnSource.HasValue && File.Exists(resolvedOnSource.Value.FullResolvedPath))
                {
                    verified++;
                    continue;
                }

                var resolvedOnLib = TryResolveSafeTargetFilePath(effectiveRoot, s.RootFolderPath, s.SeriesPath, ep.RelativePathInSeries, ep.FullDiskPath!);
                if (resolvedOnLib.HasValue && File.Exists(resolvedOnLib.Value.FullResolvedPath))
                {
                    verified++;
                }
            }
        }

        foreach (var m in radarrMovies.Where(m => m.FullDiskPath is not null))
        {
            var matchingLib = FindLibrary("movies", m.RootFolderPath);
            var effectiveRoot = matchingLib?.RootFolderPath ?? m.RootFolderPath;

            var resolvedOnSource = TryResolveSafeTargetFilePath(m.RootFolderPath, m.RootFolderPath, m.MoviePath, m.RelativeFilePath, m.FullDiskPath!);
            if (resolvedOnSource.HasValue && File.Exists(resolvedOnSource.Value.FullResolvedPath))
            {
                verified++;
                continue;
            }

            var resolvedOnLib = TryResolveSafeTargetFilePath(effectiveRoot, m.RootFolderPath, m.MoviePath, m.RelativeFilePath, m.FullDiskPath!);
            if (resolvedOnLib.HasValue && File.Exists(resolvedOnLib.Value.FullResolvedPath))
            {
                verified++;
            }
        }

        foreach (var a in lidarrArtists)
        {
            var matchingLib = FindLibrary("music", a.RootFolderPath);
            var effectiveRoot = matchingLib?.RootFolderPath ?? a.RootFolderPath;
            foreach (var t in a.Tracks.Where(t => t.FullDiskPath is not null))
            {
                var resolvedOnSource = TryResolveSafeTargetFilePath(a.RootFolderPath, a.RootFolderPath, null, null, t.FullDiskPath!);
                if (resolvedOnSource.HasValue && File.Exists(resolvedOnSource.Value.FullResolvedPath))
                {
                    verified++;
                    continue;
                }

                var resolvedOnLib = TryResolveSafeTargetFilePath(effectiveRoot, a.RootFolderPath, null, null, t.FullDiskPath!);
                if (resolvedOnLib.HasValue && File.Exists(resolvedOnLib.Value.FullResolvedPath))
                {
                    verified++;
                }
            }
        }

        foreach (var b in readarrBooks)
        {
            var matchingLib = FindLibrary("ebooks", b.RootFolderPath);
            var effectiveRoot = matchingLib?.RootFolderPath ?? b.RootFolderPath;
            foreach (var f in b.Files.Where(f => !string.IsNullOrWhiteSpace(f.FullDiskPath)))
            {
                var resolvedOnSource = TryResolveSafeTargetFilePath(b.RootFolderPath, b.RootFolderPath, null, null, f.FullDiskPath);
                if (resolvedOnSource.HasValue && File.Exists(resolvedOnSource.Value.FullResolvedPath))
                {
                    verified++;
                    continue;
                }

                var resolvedOnLib = TryResolveSafeTargetFilePath(effectiveRoot, b.RootFolderPath, null, null, f.FullDiskPath);
                if (resolvedOnLib.HasValue && File.Exists(resolvedOnLib.Value.FullResolvedPath))
                {
                    verified++;
                }
            }
        }

        return verified;
    }

    private static IReadOnlyList<string> NormalizeArrLanguageTokens(IEnumerable<string?> rawTokens)
    {
        var result = new List<string>();
        foreach (var raw in rawTokens)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var clean = raw.Trim().ToLowerInvariant();
            var mapped = clean switch
            {
                "english" or "eng" or "en" => "en",
                "german" or "deutsch" or "deu" or "ger" or "de" => "de",
                "japanese" or "jpn" or "ja" => "ja",
                "french" or "fra" or "fre" or "fr" => "fr",
                "spanish" or "spa" or "es" => "es",
                "italian" or "ita" or "it" => "it",
                "portuguese" or "por" or "pt" => "pt",
                "dutch" or "nld" or "dut" or "nl" => "nl",
                "polish" or "pol" or "pl" => "pl",
                "russian" or "rus" or "ru" => "ru",
                "turkish" or "tur" or "tr" => "tr",
                "korean" or "kor" or "ko" => "ko",
                "chinese" or "zho" or "chi" or "zh" => "zh",
                "unknown" or "any" or "original" => null,
                _ => null,
            };
            if (!string.IsNullOrEmpty(mapped) && !result.Contains(mapped, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(mapped);
            }
        }
        return result;
    }

    private static (IReadOnlyList<string> AudioLangs, IReadOnlyList<string> SubLangs) ExtractArrFileLanguages(JsonObject? fileNode)
    {
        if (fileNode is null)
        {
            return (Array.Empty<string>(), Array.Empty<string>());
        }

        var rawAudioTokens = new List<string>();
        var rawSubTokens = new List<string>();

        if (fileNode["languages"] is JsonArray langsArr)
        {
            foreach (var elem in langsArr)
            {
                if (elem is JsonObject obj)
                {
                    var name = obj["name"]?.GetValue<string?>();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        rawAudioTokens.Add(name);
                    }
                }
                else if (elem is not null)
                {
                    var val = elem.ToString();
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        rawAudioTokens.Add(val);
                    }
                }
            }
        }
        else if (fileNode["languages"] is JsonObject singleObj)
        {
            var name = singleObj["name"]?.GetValue<string?>();
            if (!string.IsNullOrWhiteSpace(name))
            {
                rawAudioTokens.Add(name);
            }
        }

        if (fileNode["mediaInfo"] is JsonObject mi)
        {
            if (mi["audioLanguages"] is JsonValue audioVal)
            {
                var s = audioVal.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    rawAudioTokens.AddRange(s.Split(new[] { '/', ',', ';', '+', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }
            else if (mi["audioLanguages"] is JsonArray audioArr)
            {
                foreach (var a in audioArr)
                {
                    if (a is not null)
                    {
                        rawAudioTokens.Add(a.ToString());
                    }
                }
            }

            if (mi["subtitles"] is JsonValue subVal)
            {
                var s = subVal.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    rawSubTokens.AddRange(s.Split(new[] { '/', ',', ';', '+', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }
            else if (mi["subtitles"] is JsonArray subArr)
            {
                foreach (var sub in subArr)
                {
                    if (sub is not null)
                    {
                        rawSubTokens.Add(sub.ToString());
                    }
                }
            }
        }

        var audioLangs = NormalizeArrLanguageTokens(rawAudioTokens);
        var subLangs = NormalizeArrLanguageTokens(rawSubTokens);
        return (audioLangs, subLangs);
    }

    private static IReadOnlyList<string> DetectSidecarSubLanguagesOnDisk(string fullMediaFilePath)
    {
        if (string.IsNullOrWhiteSpace(fullMediaFilePath))
        {
            return Array.Empty<string>();
        }

        try
        {
            var dir = Path.GetDirectoryName(fullMediaFilePath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return Array.Empty<string>();
            }

            var stem = Path.GetFileNameWithoutExtension(fullMediaFilePath);
            var subExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt",
            };

            var rawTokens = new List<string>();
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
            {
                var ext = Path.GetExtension(file);
                if (!subExts.Contains(ext)) continue;

                var candName = Path.GetFileName(file);
                if (!candName.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;

                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(file);
                if (fileNameWithoutExt.Length > stem.Length)
                {
                    var suffix = fileNameWithoutExt[stem.Length..].TrimStart('.', '_', '-');
                    var parts = suffix.Split(new[] { '.', '_', '-', '[', ']', '(', ')', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    rawTokens.AddRange(parts);
                }
            }

            return NormalizeArrLanguageTokens(rawTokens);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool ContainsParentTraversalSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var seg in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == "..")
            {
                return true;
            }
        }

        return false;
    }

    private static void ExtractArchiveSafely(string zipFilePath, string destinationDir)
    {
        var fullDest = Path.GetFullPath(destinationDir);
        var fullDestWithSep = fullDest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipFilePath);
        if (archive.Entries.Count > 512)
        {
            throw new InvalidOperationException($"Archive contains too many entries ({archive.Entries.Count} > 512).");
        }

        long cumulativeBytes = 0;
        const long maxCumulativeBytes = 256L * 1024 * 1024;
        var buffer = new byte[65536];

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var targetPath = Path.GetFullPath(Path.Combine(fullDest, entry.FullName.Replace('\\', Path.DirectorySeparatorChar)));
            if (!targetPath.StartsWith(fullDestWithSep, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Archive entry '{entry.FullName}' escaped extraction directory.");
            }

            var parentDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            using var entryStream = entry.Open();
            using var outStream = File.Create(targetPath);
            int read;
            while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cumulativeBytes += read;
                if (cumulativeBytes > maxCumulativeBytes)
                {
                    throw new InvalidOperationException("Archive exceeded 256 MB uncompressed safety limit.");
                }
                outStream.Write(buffer, 0, read);
            }
        }
    }

    private static bool HasSymlinkEscapeOutsideRoot(string fullRoot, string fullRootWithSep, string resolvedFull)
    {
        try
        {
            var physicalRoot = fullRoot;
            var rootDirInfo = new DirectoryInfo(fullRoot);
            if (rootDirInfo.Exists && rootDirInfo.ResolveLinkTarget(returnFinalTarget: true) is { } rootTarget)
            {
                physicalRoot = Path.GetFullPath(rootTarget.FullName);
            }
            var physicalRootWithSep = physicalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            var rel = Path.GetRelativePath(fullRoot, resolvedFull);
            var walk = fullRoot;
            foreach (var part in rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                walk = Path.Combine(walk, part);
                FileSystemInfo fsi = Directory.Exists(walk) ? new DirectoryInfo(walk) : new FileInfo(walk);
                if (fsi.LinkTarget is null)
                {
                    continue;
                }

                var target = fsi.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null)
                {
                    return true;
                }

                var targetFull = Path.GetFullPath(target.FullName);
                if (!targetFull.Equals(physicalRoot, StringComparison.Ordinal) &&
                    !targetFull.StartsWith(physicalRootWithSep, StringComparison.Ordinal) &&
                    !targetFull.StartsWith(fullRootWithSep, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private static (string SafeRelativePath, string FullResolvedPath)? TryResolveSafeTargetFilePath(
        string targetLibraryRoot,
        string? legacyRootFolder,
        string? legacyItemPath,
        string? relativeInsideItem,
        string fullDiskPath,
        List<string>? warnings = null)
    {
        if (string.IsNullOrWhiteSpace(targetLibraryRoot) || string.IsNullOrWhiteSpace(fullDiskPath))
        {
            return null;
        }

        if (PolymorphicCatalogService.IsForbiddenSystemRoot(targetLibraryRoot))
        {
            warnings?.Add($"Rejected protected system target library root '{targetLibraryRoot}'.");
            return null;
        }

        if (ContainsParentTraversalSegment(fullDiskPath) || ContainsParentTraversalSegment(relativeInsideItem))
        {
            warnings?.Add($"Rejected path traversal segment in migrated file path '{fullDiskPath}'.");
            return null;
        }

        string candidateRelative;
        try
        {
            var relToTarget = Path.GetRelativePath(targetLibraryRoot, fullDiskPath);
            if (!Path.IsPathRooted(relToTarget) && !relToTarget.StartsWith("..", StringComparison.Ordinal))
            {
                candidateRelative = relToTarget;
            }
            else if (!string.IsNullOrWhiteSpace(legacyRootFolder) &&
                     !Path.IsPathRooted(Path.GetRelativePath(legacyRootFolder, fullDiskPath)) &&
                     !Path.GetRelativePath(legacyRootFolder, fullDiskPath).StartsWith("..", StringComparison.Ordinal))
            {
                candidateRelative = Path.GetRelativePath(legacyRootFolder, fullDiskPath);
            }
            else if (!string.IsNullOrWhiteSpace(legacyItemPath) && !string.IsNullOrWhiteSpace(relativeInsideItem))
            {
                var itemDir = Path.GetFileName(legacyItemPath.TrimEnd('/', '\\'));
                if (string.IsNullOrWhiteSpace(itemDir) || itemDir is "." or "..")
                {
                    warnings?.Add($"Rejected invalid migrated item directory '{legacyItemPath}'.");
                    return null;
                }
                candidateRelative = Path.Combine(itemDir, relativeInsideItem.TrimStart('/', '\\'));
            }
            else
            {
                warnings?.Add($"Rejected out-of-bounds migrated file path '{fullDiskPath}' outside target root '{targetLibraryRoot}' and legacy root '{legacyRootFolder}'.");
                return null;
            }
        }
        catch
        {
            return null;
        }

        var fullRoot = Path.GetFullPath(targetLibraryRoot);
        var fullRootWithSep = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolvedFull = Path.GetFullPath(Path.Combine(fullRoot, candidateRelative.Replace('\\', Path.DirectorySeparatorChar)));
        if (!resolvedFull.StartsWith(fullRootWithSep, StringComparison.Ordinal))
        {
            warnings?.Add($"Rejected out-of-bounds migrated file path '{fullDiskPath}' outside library root '{fullRoot}'.");
            return null;
        }

        if (HasSymlinkEscapeOutsideRoot(fullRoot, fullRootWithSep, resolvedFull))
        {
            warnings?.Add($"Rejected symlink escape in migrated file path '{resolvedFull}' outside library root '{fullRoot}'.");
            return null;
        }

        var safeRel = Path.GetRelativePath(fullRoot, resolvedFull);
        return (safeRel, resolvedFull);
    }

    private static string RemapPath(string originalPath, IReadOnlyDictionary<string, string> pathMap)
    {
        var norm = originalPath.Replace('\\', '/');
        foreach (var kv in pathMap.OrderByDescending(k => k.Key.Length))
        {
            var fromPrefix = kv.Key.Replace('\\', '/').TrimEnd('/');
            var toPrefix = kv.Value.Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(fromPrefix))
            {
                continue;
            }

            if (norm.Equals(fromPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return toPrefix;
            }
            if (norm.StartsWith(fromPrefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                return toPrefix + norm[fromPrefix.Length..];
            }
        }
        return norm;
    }

    private static string MakeRelativeSafe(string rootFolder, string fullPath)
    {
        try
        {
            var rel = Path.GetRelativePath(rootFolder, fullPath);
            return rel.StartsWith("..", StringComparison.Ordinal) ? Path.GetFileName(fullPath) : rel;
        }
        catch
        {
            return Path.GetFileName(fullPath);
        }
    }

    private static JsonObject ParseSettingsJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new JsonObject();
        }
        try
        {
            return JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static string? GetStringSetting(JsonObject obj, string key)
    {
        foreach (var kv in obj)
        {
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue jv && jv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            {
                return s.Trim();
            }
        }
        return null;
    }

    private static int? GetIntSetting(JsonObject obj, string key)
    {
        foreach (var kv in obj)
        {
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue jv)
            {
                if (jv.TryGetValue<int>(out var i))
                {
                    return i;
                }
                if (jv.TryGetValue<string>(out var s) && int.TryParse(s, out var pi))
                {
                    return pi;
                }
            }
        }
        return null;
    }

    private static bool GetBoolSetting(JsonObject obj, string key)
    {
        foreach (var kv in obj)
        {
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue jv && jv.TryGetValue<bool>(out var b))
            {
                return b;
            }
        }
        return false;
    }

    private static List<int> ExtractIntArraySetting(JsonObject obj, string key)
    {
        var res = new List<int>();
        foreach (var kv in obj)
        {
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && kv.Value is JsonArray arr)
            {
                foreach (var el in arr)
                {
                    if (el is JsonValue jv && jv.TryGetValue<int>(out var v))
                    {
                        res.Add(v);
                    }
                }
            }
        }
        return res;
    }

    private static string ParseQualityLabel(string? rawQualityJson)
    {
        if (string.IsNullOrWhiteSpace(rawQualityJson))
        {
            return "WEBDL-1080p";
        }

        if (!rawQualityJson.TrimStart().StartsWith('{'))
        {
            return rawQualityJson.Trim();
        }

        try
        {
            var node = JsonNode.Parse(rawQualityJson);
            var name = node?["quality"]?["name"]?.GetValue<string>()
                       ?? node?["Name"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(name) ? "WEBDL-1080p" : name;
        }
        catch
        {
            return "WEBDL-1080p";
        }
    }

    // -------------------------------------------------------------------------
    // Internal Staging Records & Dapper Row Classes (Property-Init Only!)
    // -------------------------------------------------------------------------

    private sealed record ResolvedMigrationSource(
        string Name,
        string SourceKind,
        string TransportMode,
        string OriginalFileOrUrl,
        string ResolvedLocalPath,
        string? PlainApiKey,
        string ReadarrSlotHint,
        IReadOnlyDictionary<string, string> PathMappings);

    private sealed record StageIndexerCandidate(
        string SourceName,
        string SourceKind,
        int LegacyId,
        bool IsCanonicalHub,
        bool IsSyntheticProxyClone,
        UpsertIndexerRequest Request);

    private sealed record StageDownloadClientCandidate(
        string SourceName,
        string Name,
        string Implementation,
        string Host,
        int Port,
        bool UseSsl,
        string? UrlBase,
        string? Username,
        string? Password,
        string? ApiKey,
        bool Enabled,
        Dictionary<string, string> CategoryMappings);

    private sealed record StageSonarrEpisode(
        int SeasonNumber,
        int EpisodeNumber,
        string Title,
        string? AirDate,
        bool Monitored,
        string? RelativePathInSeries,
        string? FullDiskPath,
        long FileSize,
        string QualityName,
        IReadOnlyList<string>? AudioLanguages = null,
        IReadOnlyList<string>? SubtitleLanguages = null,
        int? AbsoluteEpisodeNumber = null);

    private sealed record StageSonarrSeries(
        string Title,
        int? Year,
        int TvdbId,
        string? ImdbId,
        string SeriesType,
        bool Monitored,
        string RootFolderPath,
        string SeriesPath,
        List<StageSonarrEpisode> Episodes,
        string? PosterUrl = null,
        string? Overview = null,
        IReadOnlyDictionary<int, bool>? SeasonMonitored = null,
        string OriginalLanguage = "en",
        IReadOnlyList<string>? AltTitles = null);

    private sealed record StageRadarrMovie(
        string Title,
        int? Year,
        int TmdbId,
        string? ImdbId,
        bool Monitored,
        string RootFolderPath,
        string? FullDiskPath,
        string? RelativeFilePath,
        long FileSize,
        string QualityName,
        string? PosterUrl = null,
        string? Overview = null,
        string? MoviePath = null,
        IReadOnlyList<string>? AudioLanguages = null,
        IReadOnlyList<string>? SubtitleLanguages = null,
        string MinimumAvailability = "released",
        string OriginalLanguage = "en",
        IReadOnlyList<string>? AltTitles = null);

    private sealed record StageLidarrTrack(int TrackNumber, string Title, string? FullDiskPath, long FileSize);

    private sealed record StageLidarrArtist(
        string Name,
        string? ForeignArtistId,
        bool Monitored,
        string RootFolderPath,
        List<StageLidarrTrack> Tracks);

    private sealed record StageReadarrBookFile(
        string SlotKind,
        string FullDiskPath,
        long SizeBytes,
        string QualityName);

    private sealed record StageReadarrBook(
        string SourceName,
        string SlotHint,
        string AuthorName,
        string BookTitle,
        string? ForeignBookId,
        string? Isbn13,
        bool Monitored,
        string RootFolderPath,
        List<StageReadarrBookFile> Files);

    private sealed record UnifiedStagedBook(
        string AuthorName,
        string BookTitle,
        string? ForeignBookId,
        string? Isbn13,
        bool Monitored,
        string RootFolderPath,
        List<StageReadarrBookFile> Files);

    private sealed record StageOverseerrUser(
        long SeerrUserId,
        long PlexId,
        string Username,
        string? Email,
        string? Avatar);

    private sealed record StageOverseerrRequest(
        long SeerrRequestId,
        string MediaType,
        int Status,
        long RequestedBySeerrUserId,
        int? TmdbId,
        int? TvdbId);

    private sealed class MigrationSourceRow
    {
        public long id { get; init; }
        public string name { get; init; } = "";
        public string source_kind { get; init; } = "";
        public string transport_mode { get; init; } = "";
        public string file_or_url { get; init; } = "";
        public string? api_key_enc { get; init; }
        public string readarr_slot_hint { get; init; } = "auto";
        public string path_mappings_json { get; init; } = "{}";
        public int is_enabled { get; init; }
        public string? last_synced_at { get; init; }
        public string created_at { get; init; } = "";
        public string updated_at { get; init; } = "";
    }

    private sealed class MigrationRunRow
    {
        public long id { get; init; }
        public string mode { get; init; } = "";
        public int uplift_language_cfs { get; init; }
        public string status { get; init; } = "";
        public string started_at { get; init; } = "";
        public string finished_at { get; init; } = "";
        public string summary_json { get; init; } = "{}";
    }

}
