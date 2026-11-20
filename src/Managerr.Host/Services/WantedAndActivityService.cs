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

namespace Managerr.Host.Services;

public static class MediaHistoryEventTypes
{
    public const string Grabbed = "Grabbed";
    public const string DownloadFolderImported = "DownloadFolderImported";
    public const string DownloadFailed = "DownloadFailed";
    public const string MediaFileDeleted = "MediaFileDeleted";
    public const string MediaFileRenamed = "MediaFileRenamed";
}

public sealed record WantedPagedResponse<T>(
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<T> Items);

public sealed record WantedUnitItemDto(
    long UnitId,
    long WorkId,
    long LibraryId,
    string LibraryName,
    string MediaVertical,
    string WorkTitle,
    string? WorkPosterUrl,
    long ContainerId,
    int ContainerNumber,
    string ContainerTitle,
    string UnitKind,
    int UnitNumber,
    int? AbsoluteNumber,
    string UnitTitle,
    string EpisodeCode,
    string? AirOrReleaseDate,
    bool IsReleased,
    bool Monitored,
    long? EffectiveProfileId,
    string? EffectiveProfileName,
    IReadOnlyList<string> RequiredAudioLangs,
    IReadOnlyList<string> RequiredSubLangs,
    bool QualityCutoffMet,
    bool LanguageCutoffMet,
    string? ExistingQualityName,
    IReadOnlyList<string> ExistingAudioLangs,
    long? ExistingSizeBytes,
    string? CutoffUnmetReason);

public sealed record WantedFilterQuery(
    int Page = 1,
    int PageSize = 50,
    string? Vertical = null,
    long? LibraryId = null,
    bool MonitoredOnly = true,
    bool ReleasedOnly = false,
    string? Language = null,
    string? LibraryIds = null);

public sealed record WantedBulkSearchRequest(
    string Mode = "selected",
    IReadOnlyList<long>? UnitIds = null,
    string? Vertical = null,
    long? LibraryId = null,
    bool MonitoredOnly = true,
    int MaxItems = 25,
    string? LibraryIds = null);

public sealed record WantedSearchItemOutcome(
    long WorkId,
    string WorkTitle,
    IReadOnlyList<long> UnitIds,
    string Label,
    int CandidatesFound,
    bool Grabbed,
    string? SelectedRelease,
    string Message);

public sealed record WantedBulkSearchReport(
    string Mode,
    int TargetedUnitsCount,
    int SearchedGroupsCount,
    int CandidatesEvaluated,
    int GrabbedCount,
    IReadOnlyList<WantedSearchItemOutcome> Outcomes);

public sealed record EnrichedInteractiveReleaseRow(
    IndexedReleaseCandidate Release,
    ParsedReleaseInfo Parsed,
    double AgeHours,
    IReadOnlyList<string> IndexerFlags,
    bool IsBlocklisted,
    bool Approved,
    string DecisionKind,
    double TotalWeightedScore,
    string QualityName,
    IReadOnlyList<string> MatchedCustomFormats,
    IReadOnlyList<string> Rejections);

public sealed record QueueControlRequest(string Action);

public sealed record RemoveQueueItemOptions(
    bool RemoveFromClient = true,
    bool DeleteData = false,
    bool Blocklist = false,
    bool SearchReplacement = false,
    string? Title = null,
    string? InfoHash = null);

public sealed record RemoveQueueItemOutcome(
    bool RemovedFromClient,
    bool Blocklisted,
    bool ReplacementSearched,
    bool ReplacementGrabbed,
    string Message);

public sealed record MediaHistoryItemDto(
    long Id,
    string OccurredAt,
    string EventType,
    string MediaVertical,
    long? WorkId,
    string? WorkTitle,
    long? ContainerId,
    long? UnitId,
    string? EpisodeCode,
    string SourceTitle,
    string? QualityName,
    IReadOnlyList<string> Languages,
    string? IndexerName,
    long? DownloadClientId,
    string? DownloadClientName,
    string? ExternalDownloadId,
    string? InfoHash,
    string? DestinationPath);

public sealed record MediaHistoryWriteRequest(
    string EventType,
    string MediaVertical,
    string SourceTitle,
    long? WorkId = null,
    string? WorkTitle = null,
    long? ContainerId = null,
    long? UnitId = null,
    string? EpisodeCode = null,
    string? QualityName = null,
    IReadOnlyList<string>? Languages = null,
    string? IndexerName = null,
    long? DownloadClientId = null,
    string? DownloadClientName = null,
    string? ExternalDownloadId = null,
    string? InfoHash = null,
    string? DestinationPath = null,
    object? Data = null);

public sealed record MarkHistoryFailedRequest(bool SearchReplacement = true);

public sealed record MarkHistoryFailedOutcome(
    bool MarkedFailed,
    bool Blocklisted,
    bool ReplacementGrabbed,
    string Message);

public sealed record BulkDeleteBlocklistRequest(
    IReadOnlyList<string>? InfoHashes = null,
    bool ClearAll = false);

public sealed record ManualImportCandidateDto(
    string FullPath,
    string RelativePath,
    string FileName,
    long SizeBytes,
    string MediaVertical,
    long? SuggestedWorkId,
    string? SuggestedWorkTitle,
    int? SuggestedSeasonNumber,
    IReadOnlyList<int> SuggestedEpisodeNumbers,
    IReadOnlyList<long> SuggestedUnitIds,
    string QualityName,
    IReadOnlyList<string> DetectedLanguages,
    IReadOnlyList<string> Rejections);

public sealed record ManualFolderImportItemRequest(
    string SourceFilePath,
    long WorkId,
    int? SeasonNumber = null,
    IReadOnlyList<int>? EpisodeNumbers = null,
    IReadOnlyList<long>? UnitIds = null,
    string? QualityName = null,
    IReadOnlyList<string>? Languages = null);

public sealed record ExecuteManualFolderImportRequest(
    IReadOnlyList<ManualFolderImportItemRequest> Items,
    string ImportMode = "hardlink_or_copy");

public sealed record ManualFolderImportItemResult(
    string SourceFilePath,
    string? DestinationPath,
    bool Imported,
    string Message);

public sealed record ExecuteManualFolderImportReport(
    int ImportedCount,
    int FailedCount,
    IReadOnlyList<ManualFolderImportItemResult> Results);

public interface IWantedAndActivityService
{
    Task<WantedPagedResponse<WantedUnitItemDto>> GetWantedMissingAsync(WantedFilterQuery filter, PlexUserSummary? actor = null, CancellationToken ct = default);
    Task<WantedPagedResponse<WantedUnitItemDto>> GetWantedCutoffUnmetAsync(WantedFilterQuery filter, PlexUserSummary? actor = null, CancellationToken ct = default);
    Task<WantedBulkSearchReport> ExecuteBulkWantedSearchAsync(WantedBulkSearchRequest request, PlexUserSummary? actor = null, CancellationToken ct = default);

    Task<IReadOnlyList<EnrichedInteractiveReleaseRow>> EnrichInteractiveReleasesAsync(
        IReadOnlyList<(IndexedReleaseCandidate Release, ParsedReleaseInfo Parsed)> candidates,
        long? workId = null,
        long? containerId = null,
        long? unitId = null,
        long? profileId = null,
        PlexUserSummary? actor = null,
        CancellationToken ct = default);

    Task<RemoveQueueItemOutcome> RemoveQueueItemWithOptionsAsync(
        long clientId,
        string externalId,
        RemoveQueueItemOptions options,
        CancellationToken ct = default);

    Task<WantedPagedResponse<MediaHistoryItemDto>> GetMediaHistoryAsync(
        int page = 1,
        int pageSize = 50,
        string? eventType = null,
        string? vertical = null,
        long? workId = null,
        CancellationToken ct = default);

    Task<long> RecordMediaHistoryEventAsync(MediaHistoryWriteRequest request, CancellationToken ct = default);
    Task<MarkHistoryFailedOutcome> MarkHistoryItemFailedAsync(long historyId, MarkHistoryFailedRequest request, CancellationToken ct = default);

    Task<string?> RedownloadFailedWorkOrUnitsAsync(
        long? workId,
        IReadOnlyList<long>? unitIds,
        string? fallbackReleaseTitle,
        string mediaVertical,
        CancellationToken ct = default) => Task.FromResult<string?>(null);

    Task<IReadOnlyList<ManualImportCandidateDto>> ScanManualImportFolderAsync(
        string folderPath,
        string? verticalHint = null,
        long? workIdOverride = null,
        CancellationToken ct = default);

    Task<ExecuteManualFolderImportReport> ExecuteManualFolderImportAsync(
        ExecuteManualFolderImportRequest request,
        CancellationToken ct = default);
}

public sealed partial class WantedAndActivityService(
    ISqliteDbFactory dbFactory,
    IPolymorphicCatalogService catalogService,
    IComposableProfileService profileService,
    IUnifiedIndexerService indexerService,
    IReleaseParserRouter parserRouter,
    IUnifiedDownloadOrchestrator downloadOrchestrator,
    IAtomicFileOrganizer organizer,
    IReleaseSearchOrchestrator? searchOrchestrator = null) : IWantedAndActivityService
{
    private readonly IReleaseSearchOrchestrator _searchOrchestrator = searchOrchestrator ?? new ReleaseSearchOrchestrator(indexerService, parserRouter, catalogService);
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private int _historyBackfillChecked;

    private static readonly HashSet<string> PrimaryMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".ts", ".webm",
        ".flac", ".mp3", ".m4b", ".m4a", ".opus", ".ogg",
        ".epub", ".pdf", ".cbz", ".cbr", ".azw3",
    };

    private static readonly string[] IncompleteStagingSuffixes = [".part", ".partial", ".!qb", ".tmp", ".crdownload"];

    public async Task<WantedPagedResponse<WantedUnitItemDto>> GetWantedMissingAsync(
        WantedFilterQuery filter,
        PlexUserSummary? actor = null,
        CancellationToken ct = default)
    {
        var all = await CollectWantedUnitsAsync(cutoffUnmetMode: false, filter, actor, ct);
        return Paginate(all, filter.Page, filter.PageSize);
    }

    public async Task<WantedPagedResponse<WantedUnitItemDto>> GetWantedCutoffUnmetAsync(
        WantedFilterQuery filter,
        PlexUserSummary? actor = null,
        CancellationToken ct = default)
    {
        var all = await CollectWantedUnitsAsync(cutoffUnmetMode: true, filter, actor, ct);
        return Paginate(all, filter.Page, filter.PageSize);
    }

    private async Task<List<WantedUnitItemDto>> CollectWantedUnitsAsync(
        bool cutoffUnmetMode,
        WantedFilterQuery filter,
        PlexUserSummary? actor,
        CancellationToken ct)
    {
        var libraries = await catalogService.ListLibrariesAsync(actor, ct);
        var librariesById = libraries.ToDictionary(l => l.Id);
        var profiles = await profileService.ListProfilesAsync(ct);
        var profilesById = profiles.ToDictionary(p => p.Id);

        var works = await catalogService.ListWorksAsync(actor, filter.LibraryId, filter.Vertical, ct);
        HashSet<long>? multiLibraryIds = null;
        if (!string.IsNullOrWhiteSpace(filter.LibraryIds))
        {
            var parsedIds = filter.LibraryIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => long.TryParse(s, out var v) ? v : (long?)null)
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToHashSet();
            if (parsedIds.Count > 0)
            {
                multiLibraryIds = parsedIds;
            }
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var results = new List<WantedUnitItemDto>();

        foreach (var workSummary in works)
        {
            if (multiLibraryIds is not null && !multiLibraryIds.Contains(workSummary.LibraryId))
            {
                continue;
            }

            if (filter.MonitoredOnly && !workSummary.Monitored)
            {
                continue;
            }

            if (!librariesById.TryGetValue(workSummary.LibraryId, out var lib))
            {
                continue;
            }

            var tree = await catalogService.GetWorkTreeAsync(workSummary.Id, actor, ct);
            if (tree is null)
            {
                continue;
            }

            foreach (var container in tree.Containers)
            {
                foreach (var unit in container.Units)
                {
                    var effectiveMonitored = tree.Work.Monitored && container.Monitored && unit.Monitored;
                    if (filter.MonitoredOnly && !effectiveMonitored)
                    {
                        continue;
                    }

                    var isReleased = string.Equals(tree.Work.WorkKind, MetadataVerticals.Movies, StringComparison.OrdinalIgnoreCase)
                        ? MinimumAvailabilityPolicy.IsWorkAvailableForAutoGrab(tree.Work, unit.AirOrReleaseDate)
                        : IsUnitReleased(unit.AirOrReleaseDate, today);
                    if (filter.ReleasedOnly && !isReleased)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(filter.Language))
                    {
                        var lang = filter.Language.Trim();
                        var hasReqLang = unit.RequiredAudioLangs.Contains(lang, StringComparer.OrdinalIgnoreCase) ||
                                         unit.RequiredSubLangs.Contains(lang, StringComparer.OrdinalIgnoreCase) ||
                                         string.Equals(tree.Work.OriginalLanguage, lang, StringComparison.OrdinalIgnoreCase);
                        if (!hasReqLang)
                        {
                            continue;
                        }
                    }

                    var hasFile = unit.Files.Count > 0;
                    if (!cutoffUnmetMode && hasFile)
                    {
                        continue;
                    }

                    if (cutoffUnmetMode && !hasFile)
                    {
                        continue;
                    }

                    var effectiveProfileId = ResolveEffectiveProfileId(unit, tree.Work, lib, profiles);
                    var effectiveProfile = effectiveProfileId.HasValue && profilesById.TryGetValue(effectiveProfileId.Value, out var prof)
                        ? prof
                        : null;

                    var primaryFile = unit.Files.OrderByDescending(f => f.SizeBytes).FirstOrDefault();
                    var qualityCutoffMet = unit.QualityCutoffMet;
                    var languageCutoffMet = unit.LanguageCutoffMet;

                    string? cutoffReason = null;
                    if (cutoffUnmetMode)
                    {
                        if (qualityCutoffMet && languageCutoffMet)
                        {
                            continue;
                        }

                        cutoffReason = (!qualityCutoffMet && !languageCutoffMet)
                            ? "QualityAndLanguageCutoffUnmet"
                            : !qualityCutoffMet
                                ? "QualityCutoffUnmet"
                                : "LanguageCutoffUnmet";
                    }

                    var epCode = FormatEpisodeOrUnitCode(tree.Work.WorkKind, container, unit);
                    results.Add(new WantedUnitItemDto(
                        UnitId: unit.Id,
                        WorkId: tree.Work.Id,
                        LibraryId: lib.Id,
                        LibraryName: lib.Name,
                        MediaVertical: tree.Work.WorkKind,
                        WorkTitle: tree.Work.Title,
                        WorkPosterUrl: tree.Work.PosterUrl,
                        ContainerId: container.Id,
                        ContainerNumber: container.ContainerNumber,
                        ContainerTitle: container.Title,
                        UnitKind: unit.UnitKind,
                        UnitNumber: unit.UnitNumber,
                        AbsoluteNumber: unit.AbsoluteNumber,
                        UnitTitle: unit.Title,
                        EpisodeCode: epCode,
                        AirOrReleaseDate: unit.AirOrReleaseDate,
                        IsReleased: isReleased,
                        Monitored: effectiveMonitored,
                        EffectiveProfileId: effectiveProfile?.Id,
                        EffectiveProfileName: effectiveProfile?.Name,
                        RequiredAudioLangs: unit.RequiredAudioLangs,
                        RequiredSubLangs: unit.RequiredSubLangs,
                        QualityCutoffMet: qualityCutoffMet,
                        LanguageCutoffMet: languageCutoffMet,
                        ExistingQualityName: primaryFile?.QualityName,
                        ExistingAudioLangs: primaryFile?.DetectedAudioLangs ?? [],
                        ExistingSizeBytes: primaryFile?.SizeBytes,
                        CutoffUnmetReason: cutoffReason));
                }
            }
        }

        return results
            .OrderByDescending(r => r.AirOrReleaseDate ?? "")
            .ThenBy(r => r.WorkTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ContainerNumber)
            .ThenBy(r => r.UnitNumber)
            .ToList();
    }

    public async Task<WantedBulkSearchReport> ExecuteBulkWantedSearchAsync(
        WantedBulkSearchRequest request,
        PlexUserSummary? actor = null,
        CancellationToken ct = default)
    {
        var mode = (request.Mode ?? "selected").Trim().ToLowerInvariant();
        var maxItems = Math.Clamp(request.MaxItems <= 0 ? 25 : request.MaxItems, 1, 200);

        List<WantedUnitItemDto> targets;
        if (mode == "all_cutoff_unmet")
        {
            targets = await CollectWantedUnitsAsync(
                cutoffUnmetMode: true,
                new WantedFilterQuery(1, maxItems, request.Vertical, request.LibraryId, request.MonitoredOnly, LibraryIds: request.LibraryIds),
                actor,
                ct);
        }
        else if (mode == "all_missing")
        {
            targets = await CollectWantedUnitsAsync(
                cutoffUnmetMode: false,
                new WantedFilterQuery(1, maxItems, request.Vertical, request.LibraryId, request.MonitoredOnly, LibraryIds: request.LibraryIds),
                actor,
                ct);
        }
        else
        {
            var wantedIdSet = (request.UnitIds ?? []).ToHashSet();
            if (wantedIdSet.Count == 0)
            {
                return new WantedBulkSearchReport(mode, 0, 0, 0, 0, []);
            }

            var missing = await CollectWantedUnitsAsync(
                cutoffUnmetMode: false,
                new WantedFilterQuery(1, 5000, request.Vertical, request.LibraryId, MonitoredOnly: false),
                actor,
                ct);
            var cutoff = await CollectWantedUnitsAsync(
                cutoffUnmetMode: true,
                new WantedFilterQuery(1, 5000, request.Vertical, request.LibraryId, MonitoredOnly: false),
                actor,
                ct);

            targets = missing.Concat(cutoff)
                .Where(u => wantedIdSet.Contains(u.UnitId))
                .GroupBy(u => u.UnitId)
                .Select(g => g.First())
                .Take(maxItems)
                .ToList();
        }

        targets = targets.Take(maxItems).ToList();
        if (targets.Count == 0)
        {
            return new WantedBulkSearchReport(mode, 0, 0, 0, 0, []);
        }

        var blocklist = await downloadOrchestrator.GetBlocklistSnapshotAsync(ct);
        var imported = await downloadOrchestrator.GetImportedReleaseSnapshotAsync(ct);

        var outcomes = new List<WantedSearchItemOutcome>();

        // Units whose download is still coming stay missing until the import; searching them again would grab a
        // second copy (#64). The failed-download replacement is unaffected: a failed download no longer counts.
        var unitsInFlight = await downloadOrchestrator.GetUnitIdsInFlightAsync(ct);
        foreach (var busy in targets.Where(t => unitsInFlight.Contains(t.UnitId)))
        {
            outcomes.Add(new WantedSearchItemOutcome(
                WorkId: busy.WorkId,
                WorkTitle: busy.WorkTitle,
                UnitIds: [busy.UnitId],
                Label: $"{busy.WorkTitle} {busy.EpisodeCode}",
                CandidatesFound: 0,
                Grabbed: false,
                SelectedRelease: null,
                Message: $"Not searched: a download for {busy.WorkTitle} {busy.EpisodeCode} is still in flight."));
        }

        targets = targets.Where(t => !unitsInFlight.Contains(t.UnitId)).ToList();
        var searchedGroups = 0;
        var totalCandidates = 0;
        var grabbedCount = 0;

        // Group by (WorkId, ContainerId) so multi-episode season searches check season packs first
        var groups = targets.GroupBy(t => (t.WorkId, t.ContainerId)).ToList();
        for (var gi = 0; gi < groups.Count; gi++)
        {
            if (gi > 0)
            {
                await Task.Delay(75, ct);
            }

            var grp = groups[gi].ToList();
            var first = grp[0];
            var tree = await catalogService.GetWorkTreeAsync(first.WorkId, actor, ct);
            if (tree is null)
            {
                continue;
            }

            var (imdbId, tmdbId, tvdbId) = ExtractExternalNumericIds(tree.Work.ExternalIds);
            var isEpisodic = first.MediaVertical is MetadataVerticals.Tv or MetadataVerticals.Anime ||
                             first.UnitKind.Equals("episode", StringComparison.OrdinalIgnoreCase);

            // If >= 2 units in the same episodic season are wanted, try a season-level search first
            if (isEpisodic && grp.Count >= 2 && first.ContainerNumber > 0)
            {
                searchedGroups++;
                var seasonScoped = await _searchOrchestrator.SearchScopedAsync(
                    new OrchestratedReleaseSearchRequest(
                        Query: new IndexerSearchQuery(
                            Query: first.WorkTitle,
                            MediaVertical: first.MediaVertical,
                            Season: first.ContainerNumber,
                            Interactive: false,
                            WorkId: first.WorkId),
                        WorkTree: tree,
                        ContainerId: first.ContainerId,
                        IncludeSeasonPacks: true),
                    ct);
                totalCandidates += seasonScoped.Count;

                var packCandidates = seasonScoped
                    .Where(x => x.IsSeasonPack && x.EffectiveSeason == first.ContainerNumber)
                    .Where(x => !blocklist.IsBlocked(x.Release))
                    .Where(x => imported.FindImportedFor(x.Release, UnitsWithFiles(tree, grp.Select(u => u.UnitId))) is null)
                    .ToList();

                if (packCandidates.Count > 0 && first.EffectiveProfileId.HasValue)
                {
                    var scoredPacks = new List<(IndexedReleaseCandidate Candidate, ReleaseSimulationScorecard Scorecard)>();
                    foreach (var p in packCandidates)
                    {
                        IReadOnlyList<string>? expAudio = p.Release.IndexerLanguages is { Count: > 0 }
                            ? ReleaseSearchOrchestrator.MergeLanguages(p.Release, p.Parsed, tree.Work.OriginalLanguage)
                            : null;
                        IReadOnlyList<string>? expSub = p.Release.IndexerSubtitles is { Count: > 0 }
                            ? ReleaseSearchOrchestrator.MergeSubtitles(p.Release, p.Parsed)
                            : null;

                        var sc = await profileService.SimulateAsync(
                            new SimulateReleaseRequest(
                                ProfileId: first.EffectiveProfileId.Value,
                                ReleaseTitle: p.Release.Title,
                                OriginalLanguage: tree.Work.OriginalLanguage,
                                WorkId: tree.Work.Id,
                                UnitId: first.UnitId,
                                SizeBytes: p.Release.SizeBytes > 0 ? p.Release.SizeBytes : null,
                                ExplicitAudioLanguages: expAudio,
                                ExplicitSubtitleLanguages: expSub),
                            ct);
                        if (sc.Approved)
                        {
                            scoredPacks.Add((p.Release, sc));
                        }
                    }

                    var bestPack = scoredPacks
                        .OrderByDescending(x => x.Scorecard.TotalWeightedScore)
                        .ThenByDescending(x => x.Candidate.SizeBytes)
                        .FirstOrDefault();

                    if (bestPack.Candidate is not null)
                    {
                        var dl = bestPack.Candidate.MagnetUrl ?? bestPack.Candidate.DownloadUrl;
                        try
                        {
                            var grabOutcome = await downloadOrchestrator.GrabReleaseAsync(
                                new GrabReleaseRequest(
                                    Title: bestPack.Candidate.Title,
                                    Protocol: bestPack.Candidate.Protocol,
                                    MediaVertical: first.MediaVertical,
                                    DownloadUrlOrMagnet: dl,
                                    InfoHash: bestPack.Candidate.InfoHash,
                                    WorkId: first.WorkId,
                                    ContainerId: first.ContainerId,
                                    UnitIds: grp.Select(u => u.UnitId).ToArray(),
                                    IndexerId: bestPack.Candidate.IndexerId,
                                    ReleaseGuid: bestPack.Candidate.Guid,
                                    SourceType: "wanted_search",
                                    IndexerName: bestPack.Candidate.IndexerName,
                                    SizeBytes: bestPack.Candidate.SizeBytes,
                                    PublishedAt: bestPack.Candidate.PublishedAt),
                                ct);
                            grabbedCount++;
                            outcomes.Add(new WantedSearchItemOutcome(
                                WorkId: first.WorkId,
                                WorkTitle: first.WorkTitle,
                                UnitIds: grp.Select(u => u.UnitId).ToArray(),
                                Label: $"{first.WorkTitle} S{first.ContainerNumber:D2} ({grp.Count} episodes)",
                                CandidatesFound: seasonScoped.Count,
                                Grabbed: true,
                                SelectedRelease: bestPack.Candidate.Title,
                                Message: $"Grabbed season pack '{bestPack.Candidate.Title}' via {grabOutcome.ClientName}."));
                            continue;
                        }
                        catch
                        {
                            // Fall through to individual unit search if season pack grab fails
                        }
                    }
                }
            }

            foreach (var item in grp)
            {
                searchedGroups++;
                var unitOutcome = await SearchAndGrabSingleUnitAsync(tree, item, imdbId, tmdbId, tvdbId, blocklist, imported, ct);
                totalCandidates += unitOutcome.CandidatesFound;
                if (unitOutcome.Grabbed)
                {
                    grabbedCount++;
                }

                outcomes.Add(unitOutcome);
            }
        }

        return new WantedBulkSearchReport(
            Mode: mode,
            TargetedUnitsCount: targets.Count,
            SearchedGroupsCount: searchedGroups,
            CandidatesEvaluated: totalCandidates,
            GrabbedCount: grabbedCount,
            Outcomes: outcomes);
    }

    private async Task<WantedSearchItemOutcome> SearchAndGrabSingleUnitAsync(
        MediaWorkDetailTree tree,
        WantedUnitItemDto item,
        string? imdbId,
        int? tmdbId,
        int? tvdbId,
        ReleaseBlocklistSnapshot blocklist,
        ImportedReleaseSnapshot imported,
        CancellationToken ct)
    {
        var isEpisodic = item.MediaVertical is MetadataVerticals.Tv or MetadataVerticals.Anime ||
                         item.UnitKind.Equals("episode", StringComparison.OrdinalIgnoreCase);
        var searchVertical = item.UnitKind switch
        {
            "audiobook_slot" => MetadataVerticals.Audiobooks,
            "ebook_slot" => MetadataVerticals.Ebooks,
            _ => item.MediaVertical,
        };

        var scoped = await _searchOrchestrator.SearchScopedAsync(
            new OrchestratedReleaseSearchRequest(
                Query: new IndexerSearchQuery(
                    Query: item.WorkTitle,
                    MediaVertical: searchVertical,
                    Season: isEpisodic ? item.ContainerNumber : null,
                    Episode: isEpisodic ? item.UnitNumber : null,
                    Interactive: false,
                    WorkId: item.WorkId,
                    UnitId: item.UnitId),
                WorkTree: tree,
                ContainerId: item.ContainerId,
                UnitId: item.UnitId,
                AbsoluteEpisodeNumber: item.AbsoluteNumber,
                IncludeSeasonPacks: false),
            ct);

        var unitDetail = tree.Containers
            .SelectMany(c => c.Units)
            .FirstOrDefault(u => u.Id == item.UnitId);
        var existingFile = unitDetail?.Files.OrderByDescending(f => f.SizeBytes).FirstOrDefault();

        var matching = scoped
            .Where(x => !blocklist.IsBlocked(x.Release))
            .Where(x => imported.FindImportedFor(x.Release, UnitsWithFiles(tree, [item.UnitId])) is null)
            .Where(x => !isEpisodic || MatchesEpisode(x.Parsed, item.ContainerNumber, item.UnitNumber, item.AbsoluteNumber, tree))
            .ToList();

        if (matching.Count == 0)
        {
            return new WantedSearchItemOutcome(
                WorkId: item.WorkId,
                WorkTitle: item.WorkTitle,
                UnitIds: [item.UnitId],
                Label: $"{item.WorkTitle} {item.EpisodeCode}",
                CandidatesFound: scoped.Count,
                Grabbed: false,
                SelectedRelease: null,
                Message: $"0 matching non-blocklisted releases found for {item.WorkTitle} {item.EpisodeCode}.");
        }

        IndexedReleaseCandidate? winner = null;
        double bestScore = double.MinValue;

        if (item.EffectiveProfileId.HasValue)
        {
            foreach (var m in matching)
            {
                IReadOnlyList<string>? expAudio = m.Release.IndexerLanguages is { Count: > 0 }
                    ? ReleaseSearchOrchestrator.MergeLanguages(m.Release, m.Parsed, tree.Work.OriginalLanguage)
                    : null;
                IReadOnlyList<string>? expSub = m.Release.IndexerSubtitles is { Count: > 0 }
                    ? ReleaseSearchOrchestrator.MergeSubtitles(m.Release, m.Parsed)
                    : null;

                var sc = await profileService.SimulateAsync(
                    new SimulateReleaseRequest(
                        ProfileId: item.EffectiveProfileId.Value,
                        ReleaseTitle: m.Release.Title,
                        OriginalLanguage: tree.Work.OriginalLanguage,
                        WorkId: tree.Work.Id,
                        UnitId: item.UnitId,
                        SizeBytes: m.Release.SizeBytes > 0 ? m.Release.SizeBytes : null,
                        ExplicitAudioLanguages: expAudio,
                        ExplicitSubtitleLanguages: expSub,
                        ExistingFileQuality: existingFile?.QualityName,
                        ExistingFileAudioLanguages: existingFile?.DetectedAudioLangs,
                        ExistingFileSubtitleLanguages: existingFile?.DetectedSubLangs,
                        ExistingSourceQuality: existingFile?.SourceQualityName,
                        ExistingVideoCodec: existingFile?.VideoCodec),
                    ct);
                if (sc.Approved && sc.TotalWeightedScore > bestScore)
                {
                    bestScore = sc.TotalWeightedScore;
                    winner = m.Release;
                }
            }
        }
        else
        {
            winner = matching.OrderByDescending(x => x.Release.SizeBytes).First().Release;
        }

        if (winner is null)
        {
            return new WantedSearchItemOutcome(
                WorkId: item.WorkId,
                WorkTitle: item.WorkTitle,
                UnitIds: [item.UnitId],
                Label: $"{item.WorkTitle} {item.EpisodeCode}",
                CandidatesFound: scoped.Count,
                Grabbed: false,
                SelectedRelease: null,
                Message: $"Evaluated {matching.Count} candidate(s) for {item.WorkTitle} {item.EpisodeCode}, but none passed profile/cutoff rules.");
        }

        var dl = winner.MagnetUrl ?? winner.DownloadUrl;
        try
        {
            var outcome = await downloadOrchestrator.GrabReleaseAsync(
                new GrabReleaseRequest(
                    Title: winner.Title,
                    Protocol: winner.Protocol,
                    MediaVertical: searchVertical,
                    DownloadUrlOrMagnet: dl,
                    InfoHash: winner.InfoHash,
                    WorkId: item.WorkId,
                    ContainerId: item.ContainerId,
                    UnitIds: [item.UnitId],
                    IndexerId: winner.IndexerId,
                    ReleaseGuid: winner.Guid,
                    SourceType: "wanted_search",
                    IndexerName: winner.IndexerName,
                    SizeBytes: winner.SizeBytes,
                    PublishedAt: winner.PublishedAt),
                ct);

            return new WantedSearchItemOutcome(
                WorkId: item.WorkId,
                WorkTitle: item.WorkTitle,
                UnitIds: [item.UnitId],
                Label: $"{item.WorkTitle} {item.EpisodeCode}",
                CandidatesFound: scoped.Count,
                Grabbed: true,
                SelectedRelease: winner.Title,
                Message: $"Grabbed '{winner.Title}' via {outcome.ClientName}.");
        }
        catch (Exception ex)
        {
            return new WantedSearchItemOutcome(
                WorkId: item.WorkId,
                WorkTitle: item.WorkTitle,
                UnitIds: [item.UnitId],
                Label: $"{item.WorkTitle} {item.EpisodeCode}",
                CandidatesFound: scoped.Count,
                Grabbed: false,
                SelectedRelease: winner.Title,
                Message: $"Grab failed for '{winner.Title}': {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<EnrichedInteractiveReleaseRow>> EnrichInteractiveReleasesAsync(
        IReadOnlyList<(IndexedReleaseCandidate Release, ParsedReleaseInfo Parsed)> candidates,
        long? workId = null,
        long? containerId = null,
        long? unitId = null,
        long? profileId = null,
        PlexUserSummary? actor = null,
        CancellationToken ct = default)
    {
        var blocklistRows = await downloadOrchestrator.ListBlocklistedHashesAsync(ct);
        var blocklistSnapshot = new ReleaseBlocklistSnapshot(blocklistRows);

        MediaWorkDetailTree? tree = null;
        LibrarySummary? library = null;
        MediaUnitDetail? targetUnit = null;
        if (workId.HasValue)
        {
            tree = await catalogService.GetWorkTreeAsync(workId.Value, actor, ct);
            if (tree is not null)
            {
                library = await catalogService.GetLibraryByIdAsync(tree.Work.LibraryId, ct);
                if (unitId.HasValue)
                {
                    targetUnit = tree.Containers.SelectMany(c => c.Units).FirstOrDefault(u => u.Id == unitId.Value);
                }
                else if (containerId.HasValue)
                {
                    var container = tree.Containers.FirstOrDefault(c => c.Id == containerId.Value);
                    targetUnit = container?.Units.FirstOrDefault(u => u.Monitored && (u.Files.Count == 0 || !u.QualityCutoffMet || !u.LanguageCutoffMet)) ??
                                 container?.Units.FirstOrDefault(u => u.Monitored) ??
                                 container?.Units.FirstOrDefault();
                }
                else
                {
                    targetUnit = tree.Containers.SelectMany(c => c.Units).FirstOrDefault(u => u.Monitored) ??
                                 tree.Containers.SelectMany(c => c.Units).FirstOrDefault();
                }
            }
        }

        var profiles = await profileService.ListProfilesAsync(ct);
        var effectiveProfileId = profileId;
        if (!effectiveProfileId.HasValue && library is not null)
        {
            effectiveProfileId = ResolveEffectiveProfileId(targetUnit, tree?.Work, library, profiles);
        }

        var existingFile = targetUnit?.Files.OrderByDescending(f => f.SizeBytes).FirstOrDefault();
        var originalLanguage = tree?.Work.OriginalLanguage ?? "en";

        var imported = await downloadOrchestrator.GetImportedReleaseSnapshotAsync(ct);
        var guardedUnitIds = tree is null
            ? []
            : unitId.HasValue
                ? UnitsWithFiles(tree, [unitId.Value]).ToList()
                : containerId.HasValue
                    ? UnitsWithFiles(tree, tree.Containers.Where(c => c.Id == containerId.Value).SelectMany(c => c.Units).Select(u => u.Id)).ToList()
                    : [];

        var enriched = new List<EnrichedInteractiveReleaseRow>(candidates.Count);
        foreach (var (release, parsed) in candidates)
        {
            var rejections = new List<string>();
            var isBlocklisted = blocklistSnapshot.IsBlocked(release);
            if (isBlocklisted)
            {
                var normRelTitle = ReleaseBlocklistSnapshot.NormalizeTitleKey(release.Title);
                var sRelGuid = ReleaseBlocklistSnapshot.SanitizeReleaseIdentityToken(release.Guid);
                var sRelUrl = ReleaseBlocklistSnapshot.SanitizeReleaseIdentityToken(release.DownloadUrl);
                var blockedEntry = blocklistRows.FirstOrDefault(b =>
                    (!string.IsNullOrWhiteSpace(release.InfoHash) && string.Equals(b.InfoHash, release.InfoHash.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                    (sRelGuid.Length > 0 && string.Equals(ReleaseBlocklistSnapshot.SanitizeReleaseIdentityToken(b.ReleaseGuid), sRelGuid, StringComparison.OrdinalIgnoreCase)) ||
                    (sRelUrl.Length > 0 && string.Equals(ReleaseBlocklistSnapshot.SanitizeReleaseIdentityToken(b.DownloadUrl), sRelUrl, StringComparison.OrdinalIgnoreCase)) ||
                    (normRelTitle.Length >= 4 && string.Equals(ReleaseBlocklistSnapshot.NormalizeTitleKey(b.Title), normRelTitle, StringComparison.OrdinalIgnoreCase)));
                rejections.Add($"Blocklisted: Release '{release.Title}' was blocked ({blockedEntry?.Reason ?? "previous NZB/download failure"}).");
            }

            var ageHours = Math.Round(Math.Max(0, (DateTimeOffset.UtcNow - release.PublishedAt).TotalHours), 1);
            var flags = ExtractIndexerFlags(release);

            var qualityName = BuildFallbackQualityLabel(parsed);
            var approved = !isBlocklisted;
            var decisionKind = isBlocklisted ? "Blocklisted" : "Unscored";
            var totalScore = 0.0;
            IReadOnlyList<string> customFormats = [];

            if (effectiveProfileId.HasValue)
            {
                try
                {
                    IReadOnlyList<string>? explicitAudioLangs = release.IndexerLanguages is { Count: > 0 }
                        ? ReleaseSearchOrchestrator.MergeLanguages(release, parsed, originalLanguage)
                        : null;
                    IReadOnlyList<string>? explicitSubLangs = release.IndexerSubtitles is { Count: > 0 }
                        ? ReleaseSearchOrchestrator.MergeSubtitles(release, parsed)
                        : null;

                    var scorecard = await profileService.SimulateAsync(
                        new SimulateReleaseRequest(
                            ProfileId: effectiveProfileId.Value,
                            ReleaseTitle: release.Title,
                            OriginalLanguage: originalLanguage,
                            WorkId: tree?.Work.Id,
                            UnitId: targetUnit?.Id,
                            SizeBytes: release.SizeBytes > 0 ? release.SizeBytes : null,
                            ExplicitAudioLanguages: explicitAudioLangs,
                            ExplicitSubtitleLanguages: explicitSubLangs,
                            ExistingFileQuality: existingFile?.QualityName,
                            ExistingFileAudioLanguages: existingFile?.DetectedAudioLangs,
                            ExistingFileSubtitleLanguages: existingFile?.DetectedSubLangs,
                            ExistingSourceQuality: existingFile?.SourceQualityName,
                            ExistingVideoCodec: existingFile?.VideoCodec),
                        ct);

                    qualityName = scorecard.CanonicalQualityName;
                    totalScore = Math.Round(scorecard.TotalWeightedScore, 1);
                    customFormats = scorecard.MatchedTraitRules;
                    decisionKind = isBlocklisted ? "Blocklisted" : scorecard.DecisionKind;
                    approved = !isBlocklisted && scorecard.Approved;

                    foreach (var gate in scorecard.HardGateFailures)
                    {
                        rejections.Add(gate);
                    }

                    if (!scorecard.Approved && scorecard.HardGateFailures.Count == 0 && !string.IsNullOrWhiteSpace(scorecard.Explanation))
                    {
                        rejections.Add($"{scorecard.DecisionKind}: {scorecard.Explanation}");
                    }
                }
                catch (Exception ex)
                {
                    rejections.Add($"ProfileEvaluation: {ex.Message}");
                    approved = false;
                }
            }

            // Shown as a rejection; the interactive Grab button still lets the user force it
            if (imported.FindImportedFor(release, guardedUnitIds) is { } alreadyImported)
            {
                rejections.Insert(0, ImportedReleaseSnapshot.DescribeRejection(alreadyImported));
                approved = false;
                decisionKind = "AlreadyImported";
            }

            enriched.Add(new EnrichedInteractiveReleaseRow(
                Release: release,
                Parsed: parsed,
                AgeHours: ageHours,
                IndexerFlags: flags,
                IsBlocklisted: isBlocklisted,
                Approved: approved,
                DecisionKind: decisionKind,
                TotalWeightedScore: totalScore,
                QualityName: qualityName,
                MatchedCustomFormats: customFormats,
                Rejections: rejections));
        }

        return enriched
            .OrderByDescending(r => r.Approved)
            .ThenByDescending(r => r.TotalWeightedScore)
            .ThenByDescending(r => r.Release.Seeders ?? 0)
            .ThenByDescending(r => r.Release.SizeBytes)
            .ToList();
    }

    private static readonly string[] TransientDownloaderClientFaultPhrases =
    [
        "no active",
        "missing server",
        "no server",
        "server connection",
        "disk full",
        "no space left",
        "permission denied",
        "read-only file system",
        "login failed",
        "authentication failed",
        "connection refused",
        "network is unreachable"
    ];

    public static bool IsTransientDownloaderClientFault(string? errorOrStatusMessage)
    {
        if (string.IsNullOrWhiteSpace(errorOrStatusMessage))
        {
            return false;
        }

        foreach (var phrase in TransientDownloaderClientFaultPhrases)
        {
            if (errorOrStatusMessage.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<RemoveQueueItemOutcome> RemoveQueueItemWithOptionsAsync(
        long clientId,
        string externalId,
        RemoveQueueItemOptions options,
        CancellationToken ct = default)
    {
        var queue = await downloadOrchestrator.GetUnifiedQueueAsync(ct);
        var queueItem = queue.FirstOrDefault(q =>
            q.ClientId == clientId &&
            string.Equals(q.ExternalId, externalId, StringComparison.OrdinalIgnoreCase));

        var resolvedTitleHint = !string.IsNullOrWhiteSpace(options.Title)
            ? options.Title!
            : queueItem?.Title;
        var grab = await downloadOrchestrator.FindGrabHistoryByItemAsync(
            clientId,
            externalId,
            options.InfoHash ?? queueItem?.InfoHash,
            resolvedTitleHint,
            ct);
        var resolvedTitle = !string.IsNullOrWhiteSpace(options.Title)
            ? options.Title!
            : queueItem?.Title ?? grab?.ReleaseTitle ?? externalId;
        var resolvedHash = !string.IsNullOrWhiteSpace(options.InfoHash)
            ? options.InfoHash!.Trim().ToLowerInvariant()
            : queueItem?.InfoHash ?? grab?.InfoHash;
        var resolvedVertical = queueItem?.MediaVertical ?? grab?.MediaVertical ?? MetadataVerticals.Movies;

        var removedFromClient = false;
        if (options.RemoveFromClient)
        {
            await downloadOrchestrator.RemoveDownloadFromClientAsync(clientId, externalId, options.DeleteData, ct);
            removedFromClient = true;
        }

        var errorOrStatus = queueItem?.ErrorMessage ?? queueItem?.Status;
        var isClientFault = IsTransientDownloaderClientFault(errorOrStatus);

        var blocklisted = false;
        if (options.Blocklist && !isClientFault)
        {
            await downloadOrchestrator.BlocklistFailedReleaseAsync(
                resolvedHash,
                grab?.ReleaseGuid,
                resolvedTitle,
                "manual_queue_remove",
                ct,
                new FailedReleaseBlocklistMetadata(
                    Protocol: queueItem?.Protocol,
                    DownloadUrl: grab?.DownloadUrl,
                    IndexerId: grab?.IndexerId,
                    IndexerName: grab?.IndexerName,
                    SizeBytes: grab?.SizeBytes ?? (queueItem?.TotalBytes > 0 ? queueItem.TotalBytes : null),
                    PublishedAt: grab?.PublishedAt,
                    WorkId: grab?.WorkId));
            blocklisted = true;

            await RecordMediaHistoryEventAsync(
                new MediaHistoryWriteRequest(
                    EventType: MediaHistoryEventTypes.DownloadFailed,
                    MediaVertical: resolvedVertical,
                    SourceTitle: resolvedTitle,
                    WorkId: grab?.WorkId,
                    ContainerId: grab?.ContainerId,
                    UnitId: grab?.UnitIds.FirstOrDefault(),
                    DownloadClientId: clientId,
                    DownloadClientName: queueItem?.ClientName,
                    ExternalDownloadId: externalId,
                    InfoHash: resolvedHash,
                    Data: new { reason = "manual_queue_remove_blocklist", options.DeleteData }),
                ct);
        }
        else if (options.Blocklist && isClientFault)
        {
            await RecordMediaHistoryEventAsync(
                new MediaHistoryWriteRequest(
                    EventType: MediaHistoryEventTypes.DownloadFailed,
                    MediaVertical: resolvedVertical,
                    SourceTitle: resolvedTitle,
                    WorkId: grab?.WorkId,
                    ContainerId: grab?.ContainerId,
                    UnitId: grab?.UnitIds.FirstOrDefault(),
                    DownloadClientId: clientId,
                    DownloadClientName: queueItem?.ClientName,
                    ExternalDownloadId: externalId,
                    InfoHash: resolvedHash,
                    Data: new { reason = "transient_downloader_client_fault", error = errorOrStatus, options.DeleteData }),
                ct);
        }

        // Record the outcome on the tracked download, creating the row if the import service never saw this item.
        // CDH skips Failed/Ignored rows, and the in-flight check (#64) sees the download as finished, so the
        // replacement search below is not blocked by the grab that was just removed.
        var now = DateTimeOffset.UtcNow.ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                INSERT INTO tracked_downloads (
                    download_client_id, client_name, external_download_id, protocol, info_hash,
                    title, media_vertical, work_id, unit_ids_json, state, tracked_status,
                    status_messages_json, first_seen_at, updated_at
                ) VALUES (
                    @ClientId, @ClientName, @ExtId, @Protocol, @Hash,
                    @Title, @Vertical, @WorkId, @UnitIdsJson, @State, @Status,
                    @MsgsJson, @Now, @Now
                )
                ON CONFLICT(download_client_id, external_download_id) DO UPDATE SET
                    state = excluded.state,
                    tracked_status = excluded.tracked_status,
                    status_messages_json = excluded.status_messages_json,
                    updated_at = excluded.updated_at;
                """,
                new
                {
                    ClientId = clientId,
                    ClientName = queueItem?.ClientName ?? "",
                    ExtId = externalId,
                    Protocol = queueItem?.Protocol ?? "usenet",
                    Hash = resolvedHash,
                    Title = resolvedTitle,
                    Vertical = resolvedVertical,
                    WorkId = grab?.WorkId,
                    UnitIdsJson = JsonSerializer.Serialize(grab?.UnitIds ?? [], JsonOpts),
                    State = (blocklisted || isClientFault) ? TrackedDownloadStates.Failed : TrackedDownloadStates.Ignored,
                    Status = (blocklisted || isClientFault) ? "error" : "ok",
                    MsgsJson = JsonSerializer.Serialize(new[] { isClientFault ? $"Transient downloader fault: {errorOrStatus}" : "Removed from the queue by a user." }, JsonOpts),
                    Now = now,
                },
                transaction: tx),
            ct);

        var replacementSearched = false;
        var replacementGrabbed = false;
        if (options.SearchReplacement && !isClientFault)
        {
            replacementSearched = true;
            var repNote = await RedownloadFailedWorkOrUnitsAsync(
                grab?.WorkId,
                grab?.UnitIds,
                resolvedTitle,
                resolvedVertical,
                ct);
            replacementGrabbed = repNote is not null && repNote.StartsWith("Auto-grabbed replacement", StringComparison.OrdinalIgnoreCase);
        }

        var msg = $"Queue item '{resolvedTitle}' processed (removedFromClient={removedFromClient}, blocklisted={blocklisted}, replacementGrabbed={replacementGrabbed}).";
        return new RemoveQueueItemOutcome(removedFromClient, blocklisted, replacementSearched, replacementGrabbed, msg);
    }

}
