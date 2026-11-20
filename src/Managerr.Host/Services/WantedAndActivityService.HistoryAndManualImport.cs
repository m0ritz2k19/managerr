using System.Text.Json;
using Dapper;
using Managerr.Catalog;
using Managerr.Datastore;
using Managerr.Downloaders;
using Managerr.Downloaders.Organizer;
using Managerr.Downloaders.Parser;
using Managerr.Indexers;
using Managerr.Indexers.Categories;
using Managerr.Indexers.Torznab;
using Managerr.Metadata;
using Managerr.Migration;
using Managerr.Profiles;
using Managerr.Security.Users;
using Managerr.Verification;

namespace Managerr.Host.Services;

public sealed partial class WantedAndActivityService
{
    public async Task<WantedPagedResponse<MediaHistoryItemDto>> GetMediaHistoryAsync(
        int page = 1,
        int pageSize = 50,
        string? eventType = null,
        string? vertical = null,
        long? workId = null,
        CancellationToken ct = default)
    {
        await EnsureLegacyHistoryBackfilledOnceAsync(ct);

        var safePage = Math.Max(1, page);
        var safeSize = Math.Clamp(pageSize, 1, 250);
        var offset = (safePage - 1) * safeSize;

        var clauses = new List<string>();
        var dyn = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(eventType))
        {
            var eventTypes = eventType
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (eventTypes.Length == 1)
            {
                clauses.Add("event_type = @EventType");
                dyn.Add("EventType", eventTypes[0]);
            }
            else if (eventTypes.Length > 1)
            {
                clauses.Add("event_type IN @EventTypes");
                dyn.Add("EventTypes", eventTypes);
            }
        }

        if (!string.IsNullOrWhiteSpace(vertical))
        {
            var verticals = vertical
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => v.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (verticals.Length == 1)
            {
                clauses.Add("media_vertical = @Vertical");
                dyn.Add("Vertical", verticals[0]);
            }
            else if (verticals.Length > 1)
            {
                clauses.Add("media_vertical IN @Verticals");
                dyn.Add("Verticals", verticals);
            }
        }

        if (workId.HasValue)
        {
            clauses.Add("work_id = @WorkId");
            dyn.Add("WorkId", workId.Value);
        }

        var whereSql = clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : "";
        dyn.Add("Limit", safeSize);
        dyn.Add("Offset", offset);

        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(1) FROM media_history {whereSql};", dyn);
        var rows = await conn.QueryAsync<HistoryRow>(
            $"""
            SELECT * FROM media_history
            {whereSql}
            ORDER BY occurred_at DESC, id DESC
            LIMIT @Limit OFFSET @Offset;
            """,
            dyn);

        var items = rows.Select(MapHistoryRow).ToList();
        return new WantedPagedResponse<MediaHistoryItemDto>(safePage, safeSize, total, items);
    }

    public async Task<long> RecordMediaHistoryEventAsync(MediaHistoryWriteRequest request, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        return await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO media_history (
                    occurred_at, event_type, media_vertical, work_id, work_title,
                    container_id, unit_id, episode_code, source_title, quality_name,
                    languages_json, indexer_name, download_client_id, download_client_name,
                    external_download_id, info_hash, destination_path, data_json
                ) VALUES (
                    @Now, @EventType, @Vert, @WorkId,
                    COALESCE(@WorkTitle, (SELECT title FROM media_works WHERE id = @WorkId)),
                    @ContainerId, @UnitId, @EpCode, @SourceTitle, @QualityName,
                    @LangsJson, @IndexerName, @ClientId, @ClientName,
                    @ExtId, @Hash, @Dest, @DataJson
                );
                SELECT last_insert_rowid();
                """,
                new
                {
                    Now = now,
                    request.EventType,
                    Vert = request.MediaVertical.Trim().ToLowerInvariant(),
                    request.WorkId,
                    request.WorkTitle,
                    request.ContainerId,
                    request.UnitId,
                    EpCode = request.EpisodeCode,
                    request.SourceTitle,
                    request.QualityName,
                    LangsJson = JsonSerializer.Serialize(request.Languages ?? [], JsonOpts),
                    request.IndexerName,
                    ClientId = request.DownloadClientId,
                    ClientName = request.DownloadClientName,
                    ExtId = request.ExternalDownloadId,
                    Hash = request.InfoHash?.Trim().ToLowerInvariant(),
                    Dest = request.DestinationPath,
                    DataJson = JsonSerializer.Serialize(request.Data ?? new { }, JsonOpts),
                },
                transaction: tx),
            ct);
    }

    public async Task<MarkHistoryFailedOutcome> MarkHistoryItemFailedAsync(
        long historyId,
        MarkHistoryFailedRequest request,
        CancellationToken ct = default)
    {
        HistoryRow? row;
        await using (var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            row = await conn.QuerySingleOrDefaultAsync<HistoryRow>(
                "SELECT * FROM media_history WHERE id = @Id;",
                new { Id = historyId });
        }

        if (row is null)
        {
            throw new KeyNotFoundException($"Media history event #{historyId} not found.");
        }

        DownloadGrabHistoryEntry? grab = null;
        if (row.DownloadClientId.HasValue && !string.IsNullOrWhiteSpace(row.ExternalDownloadId))
        {
            grab = await downloadOrchestrator.FindGrabHistoryByItemAsync(
                row.DownloadClientId.Value,
                row.ExternalDownloadId,
                row.InfoHash,
                row.SourceTitle,
                ct);
        }

        await downloadOrchestrator.BlocklistFailedReleaseAsync(
            row.InfoHash ?? grab?.InfoHash,
            grab?.ReleaseGuid,
            row.SourceTitle,
            "marked_failed_from_history",
            ct,
            new FailedReleaseBlocklistMetadata(
                Protocol: string.IsNullOrWhiteSpace(row.InfoHash ?? grab?.InfoHash) ||
                          ReleaseBlocklistSnapshot.IsSyntheticOrUsenetHash(row.InfoHash ?? grab?.InfoHash)
                    ? "usenet"
                    : "torrent",
                DownloadUrl: grab?.DownloadUrl,
                IndexerId: grab?.IndexerId,
                IndexerName: row.IndexerName ?? grab?.IndexerName,
                SizeBytes: grab?.SizeBytes,
                PublishedAt: grab?.PublishedAt,
                WorkId: row.WorkId ?? grab?.WorkId));

        await RecordMediaHistoryEventAsync(
            new MediaHistoryWriteRequest(
                EventType: MediaHistoryEventTypes.DownloadFailed,
                MediaVertical: row.MediaVertical,
                SourceTitle: row.SourceTitle,
                WorkId: row.WorkId ?? grab?.WorkId,
                WorkTitle: row.WorkTitle,
                ContainerId: row.ContainerId ?? grab?.ContainerId,
                UnitId: row.UnitId ?? grab?.UnitIds.FirstOrDefault(),
                EpisodeCode: row.EpisodeCode,
                QualityName: row.QualityName,
                IndexerName: row.IndexerName,
                DownloadClientId: row.DownloadClientId,
                DownloadClientName: row.DownloadClientName,
                ExternalDownloadId: row.ExternalDownloadId,
                InfoHash: row.InfoHash ?? grab?.InfoHash,
                Data: new { markedFromHistoryId = historyId }),
            ct);

        var replacementGrabbed = false;
        if (request.SearchReplacement)
        {
            IReadOnlyList<long>? unitIds = row.UnitId.HasValue
                ? [row.UnitId.Value]
                : grab is { UnitIds.Count: > 0 }
                    ? grab.UnitIds
                    : null;

            var repNote = await RedownloadFailedWorkOrUnitsAsync(
                row.WorkId ?? grab?.WorkId,
                unitIds,
                row.SourceTitle,
                row.MediaVertical,
                ct);
            replacementGrabbed = repNote is not null && repNote.StartsWith("Auto-grabbed replacement", StringComparison.OrdinalIgnoreCase);
        }

        return new MarkHistoryFailedOutcome(
            MarkedFailed: true,
            Blocklisted: true,
            ReplacementGrabbed: replacementGrabbed,
            Message: $"Marked '{row.SourceTitle}' as failed, added to blocklist, replacementGrabbed={replacementGrabbed}.");
    }

    public async Task<string?> RedownloadFailedWorkOrUnitsAsync(
        long? workId,
        IReadOnlyList<long>? unitIds,
        string? fallbackReleaseTitle,
        string mediaVertical,
        CancellationToken ct = default)
    {
        try
        {
            ParsedReleaseInfo? parsedFallback = null;
            if (!string.IsNullOrWhiteSpace(fallbackReleaseTitle))
            {
                parsedFallback = parserRouter.Parse(fallbackReleaseTitle, mediaVertical);
            }

            if (!workId.HasValue && parsedFallback is not null && !string.IsNullOrWhiteSpace(parsedFallback.CleanTitle))
            {
                var targetClean = ReleaseTitleNormalizer.CleanSeriesTitle(parsedFallback.CleanTitle);
                if (targetClean.Length >= 2)
                {
                    var works = await catalogService.ListWorksAsync(null, null, null, ct);
                    var matched = works
                        .Where(w => w.Monitored)
                        .FirstOrDefault(w =>
                            string.Equals(ReleaseTitleNormalizer.CleanSeriesTitle(w.Title), targetClean, StringComparison.OrdinalIgnoreCase) ||
                            w.AltTitles.Any(alt => string.Equals(ReleaseTitleNormalizer.CleanSeriesTitle(alt), targetClean, StringComparison.OrdinalIgnoreCase)));
                    workId = matched?.Id;
                }
            }

            if (!workId.HasValue)
            {
                return null;
            }

            var tree = await catalogService.GetWorkTreeAsync(workId.Value, null, ct);
            if (tree is null || !tree.Work.Monitored)
            {
                return null;
            }

            var resolvedUnitIds = new List<long>();
            if (unitIds is { Count: > 0 })
            {
                resolvedUnitIds.AddRange(unitIds);
            }
            else if (parsedFallback is not null)
            {
                int? effectiveSeason = parsedFallback.SeasonNumber;
                if (!effectiveSeason.HasValue)
                {
                    var knownTitles = new List<string> { tree.Work.Title };
                    knownTitles.AddRange(tree.Work.AltTitles);
                    if (ReleaseSearchOrchestrator.TryInferSequelSeason(parsedFallback, knownTitles, out var seqSeason))
                    {
                        effectiveSeason = seqSeason;
                    }
                }

                if (parsedFallback.IsFullSeasonPack && effectiveSeason.HasValue)
                {
                    var seasonContainer = tree.Containers.FirstOrDefault(c => c.ContainerNumber == effectiveSeason.Value);
                    if (seasonContainer is not null)
                    {
                        resolvedUnitIds.AddRange(seasonContainer.Units.Where(u => u.Monitored).Select(u => u.Id));
                    }
                }
                else if (parsedFallback.EpisodeNumbers.Count > 0 || parsedFallback.AbsoluteEpisodeNumbers.Count > 0)
                {
                    foreach (var c in tree.Containers)
                    {
                        foreach (var u in c.Units)
                        {
                            if (MatchesEpisode(parsedFallback, c.ContainerNumber, u.UnitNumber, u.AbsoluteNumber, tree))
                            {
                                resolvedUnitIds.Add(u.Id);
                            }
                        }
                    }
                }
            }

            if (resolvedUnitIds.Count == 0)
            {
                // Neither grab history nor the release name says which episodes failed. Searching the series' missing
                // episodes instead would grab unrelated ones (#65), so leave it to the user. A movie or book is a single
                // title, so its missing unit is the one that failed.
                var isEpisodic = tree.Work.WorkKind is "tv" or "anime" or "series" ||
                                 tree.Containers.SelectMany(c => c.Units).Any(u => u.UnitKind.Equals("episode", StringComparison.OrdinalIgnoreCase));
                if (isEpisodic)
                {
                    return $"Replacement not searched: could not tell which episode(s) '{fallbackReleaseTitle}' was for; search {tree.Work.Title} manually.";
                }

                // Never units that already have a file: a failure is no reason to re-download what is on disk
                resolvedUnitIds.AddRange(
                    tree.Containers
                        .SelectMany(c => c.Units)
                        .Where(u => u.Monitored && u.Files.Count == 0)
                        .Select(u => u.Id)
                        .Take(5));
            }

            if (resolvedUnitIds.Count == 0)
            {
                return null;
            }

            var distinctUnitIds = resolvedUnitIds.Distinct().ToArray();
            var report = await ExecuteBulkWantedSearchAsync(
                new WantedBulkSearchRequest(
                    Mode: "selected",
                    UnitIds: distinctUnitIds,
                    MonitoredOnly: false,
                    MaxItems: Math.Max(10, distinctUnitIds.Length)),
                actor: null,
                ct: ct);

            if (report.GrabbedCount > 0)
            {
                var firstGrabbed = report.Outcomes.First(o => o.Grabbed);
                return $"Auto-grabbed replacement ({report.GrabbedCount}): '{firstGrabbed.SelectedRelease}'.";
            }

            if (report.TargetedUnitsCount > 0)
            {
                return $"Replacement search ran for '{tree.Work.Title}' ({report.CandidatesEvaluated} candidate(s) evaluated, 0 non-blocklisted matches approved).";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Replacement auto-search failed: {ex.Message}";
        }

        return null;
    }

    public async Task<IReadOnlyList<ManualImportCandidateDto>> ScanManualImportFolderAsync(
        string folderPath,
        string? verticalHint = null,
        long? workIdOverride = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new ArgumentException("Folder path is required.");
        }

        var trimmed = folderPath.Trim();
        var files = new List<FileInfo>();
        string baseRoot;

        if (File.Exists(trimmed))
        {
            var fi = new FileInfo(trimmed);
            files.Add(fi);
            baseRoot = fi.DirectoryName ?? Path.GetTempPath();
        }
        else if (Directory.Exists(trimmed))
        {
            baseRoot = trimmed;
            foreach (var f in Directory.EnumerateFiles(trimmed, "*", SearchOption.AllDirectories).Take(500))
            {
                files.Add(new FileInfo(f));
            }
        }
        else
        {
            return [];
        }

        var allWorks = await catalogService.ListWorksAsync(null, null, verticalHint, ct);
        var treeCache = new Dictionary<long, MediaWorkDetailTree>();

        async Task<MediaWorkDetailTree?> GetTreeAsync(long id)
        {
            if (treeCache.TryGetValue(id, out var existing))
            {
                return existing;
            }

            var loaded = await catalogService.GetWorkTreeAsync(id, null, ct);
            if (loaded is not null)
            {
                treeCache[id] = loaded;
            }

            return loaded;
        }

        var candidates = new List<ManualImportCandidateDto>();
        foreach (var fi in files.OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
        {
            if (IncompleteStagingSuffixes.Any(sfx => fi.Name.EndsWith(sfx, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var ext = fi.Extension;
            if (!PrimaryMediaExtensions.Contains(ext))
            {
                continue;
            }

            var inferredVertical = !string.IsNullOrWhiteSpace(verticalHint)
                ? verticalHint.Trim().ToLowerInvariant()
                : InferVerticalFromExtension(ext);

            var safety = downloadOrchestrator.InspectFilesBeforeImport([fi.FullName], inferredVertical);
            var rejections = new List<string>();
            if (!safety.IsSafe && !string.IsNullOrWhiteSpace(safety.RejectionReason))
            {
                rejections.Add(safety.RejectionReason);
            }

            var nameNoExt = Path.GetFileNameWithoutExtension(fi.Name);
            var parsed = parserRouter.Parse(nameNoExt, inferredVertical);
            var dirCleanName = fi.Directory is not null
                ? System.Text.RegularExpressions.Regex.Replace(fi.Directory.Name.Trim(), @"\.\d{1,2}$", "")
                : null;
            var dirParsed = !string.IsNullOrWhiteSpace(dirCleanName)
                ? parserRouter.Parse(dirCleanName, inferredVertical)
                : null;

            if (string.IsNullOrWhiteSpace(parsed.CleanTitle) && dirParsed is not null)
            {
                parsed = dirParsed;
            }

            MediaWorkSummary? matchedWork = null;
            if (workIdOverride.HasValue)
            {
                matchedWork = allWorks.FirstOrDefault(w => w.Id == workIdOverride.Value);
            }
            else
            {
                matchedWork = allWorks.FirstOrDefault(w =>
                    (!string.IsNullOrWhiteSpace(parsed.CleanTitle) &&
                     ReleaseTitleNormalizer.MatchesWork(parsed.CleanTitle, w.Title, w.AltTitles, w.Year)) ||
                    (dirParsed is not null && !string.IsNullOrWhiteSpace(dirParsed.CleanTitle) &&
                     ReleaseTitleNormalizer.MatchesWork(dirParsed.CleanTitle, w.Title, w.AltTitles, w.Year)) ||
                    (!string.IsNullOrWhiteSpace(dirCleanName) &&
                     ReleaseTitleNormalizer.MatchesWork(dirCleanName, w.Title, w.AltTitles, w.Year)));
            }

            int? suggestedSeason = parsed.SeasonNumber ?? dirParsed?.SeasonNumber;
            var suggestedEpisodes = parsed.EpisodeNumbers.Count > 0
                ? parsed.EpisodeNumbers.ToList()
                : (dirParsed?.EpisodeNumbers.ToList() ?? []);
            var suggestedAbs = parsed.AbsoluteEpisodeNumbers.Count > 0
                ? parsed.AbsoluteEpisodeNumbers
                : (dirParsed?.AbsoluteEpisodeNumbers ?? []);
            var suggestedUnitIds = new List<long>();

            if (matchedWork is null)
            {
                rejections.Add("Unknown Series/Work — select target Work before importing.");
            }
            else
            {
                var tree = await GetTreeAsync(matchedWork.Id);
                if (tree is not null)
                {
                    var resolvedUnits = ResolveUnitsFromTree(tree, suggestedSeason, suggestedEpisodes, suggestedAbs);
                    suggestedUnitIds.AddRange(resolvedUnits.Select(u => u.Id));
                    if (suggestedUnitIds.Count == 0)
                    {
                        rejections.Add("Unresolved Episode/Unit — select Season/Episode or target Unit before importing.");
                    }
                }
            }

            var relPath = Path.GetRelativePath(baseRoot, fi.FullName).Replace('\\', '/');
            var qualityName = BuildFallbackQualityLabel(parsed);
            var detectedLangs = parsed.DetectedLanguages.Count > 0 ? parsed.DetectedLanguages : ["en"];

            candidates.Add(new ManualImportCandidateDto(
                FullPath: fi.FullName,
                RelativePath: relPath,
                FileName: fi.Name,
                SizeBytes: fi.Length,
                MediaVertical: matchedWork?.WorkKind ?? parsed.MediaVertical,
                SuggestedWorkId: matchedWork?.Id,
                SuggestedWorkTitle: matchedWork?.Title,
                SuggestedSeasonNumber: suggestedSeason,
                SuggestedEpisodeNumbers: suggestedEpisodes,
                SuggestedUnitIds: suggestedUnitIds,
                QualityName: qualityName,
                DetectedLanguages: detectedLangs,
                Rejections: rejections));
        }

        return candidates;
    }

    public async Task<ExecuteManualFolderImportReport> ExecuteManualFolderImportAsync(
        ExecuteManualFolderImportRequest request,
        CancellationToken ct = default)
    {
        if (request.Items is null || request.Items.Count == 0)
        {
            return new ExecuteManualFolderImportReport(0, 0, []);
        }

        var moveMode = string.Equals(request.ImportMode, "move", StringComparison.OrdinalIgnoreCase);
        var results = new List<ManualFolderImportItemResult>();
        var importedCount = 0;
        var failedCount = 0;

        foreach (var item in request.Items)
        {
            if (string.IsNullOrWhiteSpace(item.SourceFilePath) || !File.Exists(item.SourceFilePath))
            {
                failedCount++;
                results.Add(new ManualFolderImportItemResult(item.SourceFilePath ?? "", null, false, "Source file does not exist on disk."));
                continue;
            }

            var tree = await catalogService.GetWorkTreeAsync(item.WorkId, null, ct);
            if (tree is null)
            {
                failedCount++;
                results.Add(new ManualFolderImportItemResult(item.SourceFilePath, null, false, $"Catalog work #{item.WorkId} not found."));
                continue;
            }

            var library = await catalogService.GetLibraryByIdAsync(tree.Work.LibraryId, ct);
            if (library is null)
            {
                failedCount++;
                results.Add(new ManualFolderImportItemResult(item.SourceFilePath, null, false, $"Library #{tree.Work.LibraryId} not found."));
                continue;
            }

            var safety = downloadOrchestrator.InspectFilesBeforeImport([item.SourceFilePath], tree.Work.WorkKind);
            if (!safety.IsSafe)
            {
                failedCount++;
                results.Add(new ManualFolderImportItemResult(item.SourceFilePath, null, false, safety.RejectionReason ?? "Blocked unsafe file extension."));
                continue;
            }

            List<MediaUnitDetail> targetUnits;
            if (item.UnitIds is { Count: > 0 })
            {
                var idSet = item.UnitIds.ToHashSet();
                targetUnits = tree.Containers.SelectMany(c => c.Units).Where(u => idSet.Contains(u.Id)).ToList();
            }
            else
            {
                targetUnits = ResolveUnitsFromTree(tree, item.SeasonNumber, item.EpisodeNumbers ?? [], []);
            }

            if (targetUnits.Count == 0)
            {
                failedCount++;
                results.Add(new ManualFolderImportItemResult(item.SourceFilePath, null, false, "Could not resolve any target Episode/Unit on the selected Work."));
                continue;
            }

            var fi = new FileInfo(item.SourceFilePath);
            var baseParsed = parserRouter.Parse(Path.GetFileNameWithoutExtension(fi.Name), tree.Work.WorkKind);
            var primaryContainer = tree.Containers.FirstOrDefault(c => c.Id == targetUnits[0].ContainerId);
            var effectiveSeason = item.SeasonNumber ?? primaryContainer?.ContainerNumber ?? baseParsed.SeasonNumber;
            var effectiveEps = item.EpisodeNumbers is { Count: > 0 }
                ? item.EpisodeNumbers
                : targetUnits.Select(u => u.UnitNumber).ToArray();
            IReadOnlyList<string> effectiveLangs;
            if (item.Languages is { Count: > 0 })
            {
                effectiveLangs = item.Languages;
            }
            else
            {
                var origIso = LanguageCodeNormalizer.NormalizeToIso2(tree.Work.OriginalLanguage);
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in baseParsed.DetectedLanguages)
                {
                    var iso = LanguageCodeNormalizer.NormalizeToIso2(l);
                    if (iso != "und") set.Add(iso);
                }

                if (baseParsed.IsDualAudio)
                {
                    if (set.Contains("de") && set.Contains("en") && !ReleaseParserRouter.HasExplicitEnglishToken(baseParsed.RawTitle) && origIso is not ("und" or "de"))
                    {
                        set.Remove("en");
                        set.Add(origIso);
                    }
                    else if (set.Count == 1 && origIso != "und" && !set.Contains(origIso))
                    {
                        set.Add(origIso);
                    }
                    else if (set.Count == 0 && origIso is not ("und" or "en"))
                    {
                        set.Add("en");
                        set.Add(origIso);
                    }
                }

                effectiveLangs = set.Count > 0
                    ? set.ToList()
                    : (baseParsed.DetectedLanguages.Count > 0 ? baseParsed.DetectedLanguages : [tree.Work.OriginalLanguage]);
            }

            var normalizedParsed = baseParsed with
            {
                MediaVertical = tree.Work.WorkKind,
                CleanTitle = tree.Work.Title,
                Year = tree.Work.Year ?? baseParsed.Year,
                SeasonNumber = effectiveSeason,
                EpisodeNumbers = effectiveEps,
                DetectedLanguages = effectiveLangs,
            };

            try
            {
                Directory.CreateDirectory(library.RootFolderPath);
                var organized = await organizer.ImportFileAtomicallyAsync(
                    new OrganizeFileRequest(
                        SourceFilePath: fi.FullName,
                        LibraryRootPath: library.RootFolderPath,
                        ParsedInfo: normalizedParsed,
                        PreferHardlink: !moveMode,
                        CanMoveSource: moveMode,
                        ImportCompanionSubtitles: true,
                        NamingContext: CompletedDownloadImportService.BuildExistingWorkNamingContext(tree, library.RootFolderPath, normalizedParsed.SeasonNumber, targetUnits[0].Title)),
                    ct);

                var relDest = Path.GetRelativePath(library.RootFolderPath, organized.DestinationFilePath).Replace('\\', '/');
                var destInfo = new FileInfo(organized.DestinationFilePath);
                var qualityLabel = !string.IsNullOrWhiteSpace(item.QualityName)
                    ? item.QualityName!
                    : BuildFallbackQualityLabel(normalizedParsed);

                foreach (var unit in targetUnits)
                {
                    await catalogService.AttachFileAsync(
                        new AttachMediaFileRequest(
                            UnitId: unit.Id,
                            RelativePath: relDest,
                            SizeBytes: destInfo.Exists ? destInfo.Length : fi.Length,
                            QualityName: qualityLabel,
                            EditionName: normalizedParsed.MovieEdition,
                            Resolution: normalizedParsed.Resolution,
                            VideoCodec: normalizedParsed.VideoCodec,
                            HdrFormat: normalizedParsed.HdrFormat,
                            AudioCodec: normalizedParsed.AudioCodec,
                            DetectedAudioLangs: effectiveLangs,
                            DetectedSubLangs: [],
                            VerifiedStatus: "manual_import",
                            InodeHardlinked: organized.Hardlinked),
                        ct);

                    var audioSet = effectiveLangs.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var origIso = LanguageCodeNormalizer.NormalizeToIso2(tree.Work.OriginalLanguage);
                    var unitReqs = unit.RequiredAudioLangs
                        .Select(r => string.Equals(r, "original", StringComparison.OrdinalIgnoreCase) ? origIso : LanguageCodeNormalizer.NormalizeToIso2(r))
                        .Where(r => r != "und")
                        .ToList();
                    var langMet = unitReqs.Count == 0 || unitReqs.All(req => audioSet.Contains(req));
                    await catalogService.UpdateUnitAsync(
                        unit.Id,
                        new UpdateMediaUnitRequest(QualityCutoffMet: true, LanguageCutoffMet: langMet),
                        ct);
                }

                var epCode = primaryContainer is not null
                    ? FormatEpisodeOrUnitCode(tree.Work.WorkKind, primaryContainer, targetUnits[0])
                    : null;

                await RecordMediaHistoryEventAsync(
                    new MediaHistoryWriteRequest(
                        EventType: MediaHistoryEventTypes.DownloadFolderImported,
                        MediaVertical: tree.Work.WorkKind,
                        SourceTitle: fi.Name,
                        WorkId: tree.Work.Id,
                        WorkTitle: tree.Work.Title,
                        ContainerId: primaryContainer?.Id,
                        UnitId: targetUnits[0].Id,
                        EpisodeCode: epCode,
                        QualityName: qualityLabel,
                        Languages: effectiveLangs,
                        DestinationPath: organized.DestinationFilePath,
                        Data: new { manualImport = true, organized.TransferMode, organized.Hardlinked }),
                    ct);

                importedCount++;
                results.Add(new ManualFolderImportItemResult(
                    fi.FullName,
                    organized.DestinationFilePath,
                    true,
                    $"Imported to '{relDest}' ({organized.TransferMode})."));
            }
            catch (Exception ex)
            {
                failedCount++;
                results.Add(new ManualFolderImportItemResult(fi.FullName, null, false, $"Import failed: {ex.Message}"));
            }
        }

        return new ExecuteManualFolderImportReport(importedCount, failedCount, results);
    }

    private async Task EnsureLegacyHistoryBackfilledOnceAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _historyBackfillChecked, 1, 0) != 0)
        {
            return;
        }

        int existingCount;
        await using (var rConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            existingCount = await rConn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM media_history;");
        }

        if (existingCount > 0)
        {
            return;
        }

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (wConn, tx) =>
            {
                var checkAgain = await wConn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM media_history;", transaction: tx);
                if (checkAgain > 0)
                {
                    return;
                }

                await wConn.ExecuteAsync(
                    """
                    INSERT INTO media_history (
                        occurred_at, event_type, media_vertical, work_id, work_title,
                        container_id, unit_id, episode_code, source_title, quality_name,
                        languages_json, indexer_name, download_client_id, download_client_name,
                        external_download_id, info_hash, destination_path, data_json
                    )
                    SELECT
                        g.grabbed_at,
                        'Grabbed',
                        g.media_vertical,
                        g.work_id,
                        (SELECT mw.title FROM media_works mw WHERE mw.id = g.work_id),
                        g.container_id,
                        NULL,
                        NULL,
                        g.release_title,
                        NULL,
                        '[]',
                        (SELECT i.name FROM indexers i WHERE i.id = g.indexer_id),
                        g.download_client_id,
                        (SELECT dc.name FROM download_clients dc WHERE dc.id = g.download_client_id),
                        g.external_download_id,
                        g.info_hash,
                        NULL,
                        '{"backfilled":true}'
                    FROM download_grab_history g;
                    """,
                    transaction: tx);

                await wConn.ExecuteAsync(
                    """
                    INSERT INTO media_history (
                        occurred_at, event_type, media_vertical, work_id, work_title,
                        container_id, unit_id, episode_code, source_title, quality_name,
                        languages_json, indexer_name, download_client_id, download_client_name,
                        external_download_id, info_hash, destination_path, data_json
                    )
                    SELECT
                        p.processed_at,
                        CASE
                            WHEN p.verdict IN ('VerifiedPass', 'VerifiedPassAutoTagged', 'MusicPolicyPass') THEN 'DownloadFolderImported'
                            ELSE 'DownloadFailed'
                        END,
                        COALESCE((SELECT t.media_vertical FROM tracked_downloads t WHERE t.download_client_id = p.download_client_id AND t.external_download_id = p.external_download_id LIMIT 1), 'movies'),
                        (SELECT t.work_id FROM tracked_downloads t WHERE t.download_client_id = p.download_client_id AND t.external_download_id = p.external_download_id LIMIT 1),
                        (SELECT t.work_title FROM tracked_downloads t WHERE t.download_client_id = p.download_client_id AND t.external_download_id = p.external_download_id LIMIT 1),
                        NULL,
                        NULL,
                        NULL,
                        p.title,
                        NULL,
                        '[]',
                        NULL,
                        p.download_client_id,
                        (SELECT dc.name FROM download_clients dc WHERE dc.id = p.download_client_id),
                        p.external_download_id,
                        (SELECT t.info_hash FROM tracked_downloads t WHERE t.download_client_id = p.download_client_id AND t.external_download_id = p.external_download_id LIMIT 1),
                        p.destination_path,
                        '{"backfilled":true}'
                    FROM processed_download_imports p
                    WHERE p.verdict IN ('VerifiedPass', 'VerifiedPassAutoTagged', 'MusicPolicyPass', 'ClientFailed', 'BlockedMalware', 'VerifiedFail');
                    """,
                    transaction: tx);
            },
            ct);
    }

    private async Task<IReadOnlyList<IndexedReleaseCandidate>> SearchWithFallbackAsync(
        string title,
        string vertical,
        string? imdbId,
        int? tmdbId,
        int? tvdbId,
        int? season,
        int? episode,
        CancellationToken ct)
    {
        var results = (await indexerService.SearchAsync(
            new IndexerSearchQuery(
                Query: title,
                MediaVertical: vertical,
                ImdbId: imdbId,
                TmdbId: tmdbId,
                TvdbId: tvdbId,
                Season: season,
                Episode: episode,
                Interactive: false),
            ct)).ToList();

        if (results.Count == 0 && season.HasValue)
        {
            var token = episode.HasValue ? $"S{season.Value:D2}E{episode.Value:D2}" : $"S{season.Value:D2}";
            var fallback = await indexerService.SearchAsync(
                new IndexerSearchQuery(
                    Query: $"{title} {token}",
                    MediaVertical: vertical,
                    Interactive: false),
                ct);
            results.AddRange(fallback);
        }
        else if (results.Count == 0 && (imdbId is not null || tmdbId.HasValue || tvdbId.HasValue))
        {
            var fallback = await indexerService.SearchAsync(
                new IndexerSearchQuery(
                    Query: title,
                    MediaVertical: vertical,
                    Interactive: false),
                ct);
            results.AddRange(fallback);
        }

        return results
            .GroupBy(r => !string.IsNullOrWhiteSpace(r.Guid) ? r.Guid : r.DownloadUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static List<MediaUnitDetail> ResolveUnitsFromTree(
        MediaWorkDetailTree tree,
        int? seasonNumber,
        IReadOnlyList<int> episodeNumbers,
        IReadOnlyList<int> absoluteNumbers)
    {
        var isEpisodic = tree.Work.WorkKind is MetadataVerticals.Tv or MetadataVerticals.Anime;
        if (!isEpisodic)
        {
            var single = tree.Containers.SelectMany(c => c.Units).FirstOrDefault();
            return single is null ? [] : [single];
        }

        if (seasonNumber.HasValue && episodeNumbers.Count > 0)
        {
            var container = tree.Containers.FirstOrDefault(c => c.ContainerNumber == seasonNumber.Value);
            if (container is not null)
            {
                var epSet = episodeNumbers.ToHashSet();
                return container.Units.Where(u => epSet.Contains(u.UnitNumber)).ToList();
            }
        }

        if (absoluteNumbers.Count > 0)
        {
            var absSet = absoluteNumbers.ToHashSet();
            return tree.Containers.SelectMany(c => c.Units)
                .Where(u => u.AbsoluteNumber.HasValue && absSet.Contains(u.AbsoluteNumber.Value))
                .ToList();
        }

        if (episodeNumbers.Count > 0 && tree.Containers.Count == 1)
        {
            var epSet = episodeNumbers.ToHashSet();
            return tree.Containers[0].Units.Where(u => epSet.Contains(u.UnitNumber)).ToList();
        }

        return [];
    }

    // The already-imported rule only protects units that still have a file; a deleted file may be re-grabbed
    private static IEnumerable<long> UnitsWithFiles(MediaWorkDetailTree tree, IEnumerable<long> unitIds)
    {
        var ids = unitIds.ToHashSet();
        return tree.Containers.SelectMany(c => c.Units).Where(u => ids.Contains(u.Id) && u.Files.Count > 0).Select(u => u.Id);
    }

    // Series search queries are keyword based, so indexers also return other shows sharing a word or group name
    private static bool MatchesWorkIdentity(ParsedReleaseInfo parsed, MediaWorkSummary work) =>
        ReleaseTitleNormalizer.MatchesWork(parsed.CleanTitle, work.Title, work.AltTitles, work.Year);

    private static bool MatchesEpisode(ParsedReleaseInfo parsed, int seasonNumber, int episodeNumber, int? absoluteNumber, MediaWorkDetailTree? tree = null)
    {
        if (parsed.SeasonNumber == seasonNumber && parsed.EpisodeNumbers.Contains(episodeNumber))
        {
            return true;
        }

        if (absoluteNumber.HasValue && parsed.AbsoluteEpisodeNumbers.Contains(absoluteNumber.Value))
        {
            return true;
        }

        if (parsed.IsFullSeasonPack && parsed.SeasonNumber == seasonNumber)
        {
            return true;
        }

        if (!parsed.SeasonNumber.HasValue && tree is not null)
        {
            var knownTitles = new List<string> { tree.Work.Title };
            knownTitles.AddRange(tree.Work.AltTitles);
            if (!string.IsNullOrWhiteSpace(tree.Work.Title))
            {
                var dashIdx = -1;
                string[] delims = [" – ", " — "];
                foreach (var d in delims)
                {
                    var idx = tree.Work.Title.IndexOf(d, StringComparison.Ordinal);
                    if (idx >= 4 && (dashIdx < 0 || idx < dashIdx))
                    {
                        dashIdx = idx;
                    }
                }
                if (dashIdx >= 4)
                {
                    var pfx = tree.Work.Title[..dashIdx].Trim();
                    if (pfx.Length >= 4 && !knownTitles.Contains(pfx, StringComparer.OrdinalIgnoreCase))
                    {
                        knownTitles.Add(pfx);
                    }
                }
            }

            if (ReleaseSearchOrchestrator.TryInferSequelSeason(parsed, knownTitles, out var seqSeason) && seqSeason == seasonNumber)
            {
                var eps = parsed.EpisodeNumbers.Count > 0 ? parsed.EpisodeNumbers : parsed.AbsoluteEpisodeNumbers;
                if (eps.Contains(episodeNumber) || parsed.IsFullSeasonPack)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static long? ResolveEffectiveProfileId(
        MediaUnitDetail? unit,
        MediaWorkSummary? work,
        LibrarySummary library,
        IReadOnlyList<QualityProfileSummary> profiles)
    {
        if (unit?.ProfileOverrideId is { } overrideId && profiles.Any(p => p.Id == overrideId))
        {
            return overrideId;
        }

        if (work?.QualityProfileId is { } workProfId && profiles.Any(p => p.Id == workProfId))
        {
            return workProfId;
        }

        if (library.DefaultProfileId is { } libProfId && profiles.Any(p => p.Id == libProfId))
        {
            return libProfId;
        }

        var isVideo = library.MediaType is MediaVerticals.Movies or MediaVerticals.Tv or MediaVerticals.Anime;
        var matched = profiles.FirstOrDefault(p =>
            string.Equals(p.MediaVertical, library.MediaType, StringComparison.OrdinalIgnoreCase) ||
            (isVideo && string.Equals(p.MediaVertical, "video", StringComparison.OrdinalIgnoreCase) &&
             (library.MediaType == MediaVerticals.Anime
                 ? p.Slug.Contains("anime", StringComparison.OrdinalIgnoreCase)
                 : p.Slug.Equals("german-first-best-quality", StringComparison.OrdinalIgnoreCase))));

        matched ??= profiles.FirstOrDefault(p =>
            isVideo && string.Equals(p.MediaVertical, "video", StringComparison.OrdinalIgnoreCase));

        return matched?.Id ?? profiles.FirstOrDefault()?.Id;
    }

    private static bool IsUnitReleased(string? airOrReleaseDate, DateOnly todayUtc)
    {
        if (string.IsNullOrWhiteSpace(airOrReleaseDate))
        {
            return true;
        }

        if (DateOnly.TryParse(airOrReleaseDate.Length >= 10 ? airOrReleaseDate[..10] : airOrReleaseDate, out var d))
        {
            return d <= todayUtc;
        }

        if (DateTimeOffset.TryParse(airOrReleaseDate, out var dto))
        {
            return DateOnly.FromDateTime(dto.UtcDateTime) <= todayUtc;
        }

        return true;
    }

    private static string FormatEpisodeOrUnitCode(string workKind, MediaContainerDetail container, MediaUnitDetail unit) =>
        workKind.ToLowerInvariant() switch
        {
            MetadataVerticals.Tv or MetadataVerticals.Anime => $"S{container.ContainerNumber:D2}E{unit.UnitNumber:D2}",
            MetadataVerticals.Movies => "Movie",
            MetadataVerticals.Music => $"Track {unit.UnitNumber:D2}",
            _ => unit.UnitKind switch
            {
                "audiobook_slot" => "Audiobook",
                "ebook_slot" => "Ebook",
                _ => $"#{unit.UnitNumber}",
            },
        };

    private static IReadOnlyList<string> ExtractIndexerFlags(IndexedReleaseCandidate r)
    {
        var flags = new List<string>();
        if (r.Protocol.Equals("torrent", StringComparison.OrdinalIgnoreCase))
        {
            if (r.DownloadVolumeFactor <= 0.001)
            {
                flags.Add("Freeleech");
            }
            else if (r.DownloadVolumeFactor <= 0.5)
            {
                flags.Add("Halfleech");
            }

            if (r.UploadVolumeFactor >= 2.0)
            {
                flags.Add("2xUpload");
            }
        }

        if (r.Title.Contains("PROPER", StringComparison.OrdinalIgnoreCase))
        {
            flags.Add("Proper");
        }

        if (r.Title.Contains("REPACK", StringComparison.OrdinalIgnoreCase))
        {
            flags.Add("Repack");
        }

        if (r.Title.Contains("INTERNAL", StringComparison.OrdinalIgnoreCase))
        {
            flags.Add("Internal");
        }

        return flags;
    }

    private static string BuildFallbackQualityLabel(ParsedReleaseInfo parsed)
    {
        if (!string.IsNullOrWhiteSpace(parsed.MusicFormat))
        {
            return parsed.MusicFormat;
        }

        if (!string.IsNullOrWhiteSpace(parsed.BookFormat))
        {
            return parsed.BookFormat.ToUpperInvariant();
        }

        var src = parsed.Source switch
        {
            "BluRay Remux" => "Remux",
            "WEB-DL" or "WEB" => "WEBDL",
            "WEBRip" => "WEBRip",
            "BluRay" => "BluRay",
            "HDTV" => "HDTV",
            _ => "WEBDL",
        };
        var res = string.IsNullOrWhiteSpace(parsed.Resolution) ? "1080p" : parsed.Resolution;
        return $"{src}-{res}";
    }

    private static string InferVerticalFromExtension(string ext) =>
        ext.ToLowerInvariant() switch
        {
            ".flac" or ".mp3" or ".ogg" or ".opus" => MetadataVerticals.Music,
            ".m4b" => MetadataVerticals.Audiobooks,
            ".epub" or ".pdf" or ".azw3" => MetadataVerticals.Ebooks,
            ".cbz" or ".cbr" => MetadataVerticals.Comics,
            _ => MetadataVerticals.Tv,
        };

    private static (string? ImdbId, int? TmdbId, int? TvdbId) ExtractExternalNumericIds(IReadOnlyDictionary<string, string> ext)
    {
        ext.TryGetValue("imdb", out var imdb);
        int? tmdb = ext.TryGetValue("tmdb", out var tmdbRaw) && int.TryParse(tmdbRaw, out var tm) ? tm : null;
        int? tvdb = ext.TryGetValue("tvdb", out var tvdbRaw) && int.TryParse(tvdbRaw, out var tv) ? tv : null;
        return (string.IsNullOrWhiteSpace(imdb) ? null : imdb, tmdb, tvdb);
    }

    private static WantedPagedResponse<T> Paginate<T>(IReadOnlyList<T> source, int page, int pageSize)
    {
        var safePage = Math.Max(1, page);
        var safeSize = Math.Clamp(pageSize, 1, 250);
        var slice = source.Skip((safePage - 1) * safeSize).Take(safeSize).ToList();
        return new WantedPagedResponse<T>(safePage, safeSize, source.Count, slice);
    }

    private static MediaHistoryItemDto MapHistoryRow(HistoryRow r) =>
        new(
            Id: r.Id,
            OccurredAt: r.OccurredAt,
            EventType: r.EventType,
            MediaVertical: r.MediaVertical,
            WorkId: r.WorkId,
            WorkTitle: r.WorkTitle,
            ContainerId: r.ContainerId,
            UnitId: r.UnitId,
            EpisodeCode: r.EpisodeCode,
            SourceTitle: r.SourceTitle,
            QualityName: r.QualityName,
            Languages: JsonSerializer.Deserialize<string[]>(r.LanguagesJson, JsonOpts) ?? [],
            IndexerName: r.IndexerName,
            DownloadClientId: r.DownloadClientId,
            DownloadClientName: r.DownloadClientName,
            ExternalDownloadId: r.ExternalDownloadId,
            InfoHash: r.InfoHash,
            DestinationPath: r.DestinationPath);

    private sealed class HistoryRow
    {
        public long Id { get; init; }
        public string OccurredAt { get; init; } = "";
        public string EventType { get; init; } = "";
        public string MediaVertical { get; init; } = "";
        public long? WorkId { get; init; }
        public string? WorkTitle { get; init; }
        public long? ContainerId { get; init; }
        public long? UnitId { get; init; }
        public string? EpisodeCode { get; init; }
        public string SourceTitle { get; init; } = "";
        public string? QualityName { get; init; }
        public string LanguagesJson { get; init; } = "[]";
        public string? IndexerName { get; init; }
        public long? DownloadClientId { get; init; }
        public string? DownloadClientName { get; init; }
        public string? ExternalDownloadId { get; init; }
        public string? InfoHash { get; init; }
        public string? DestinationPath { get; init; }
        public string DataJson { get; init; } = "{}";
    }
}
