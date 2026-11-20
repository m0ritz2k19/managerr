using Dapper;
using Managerr.Catalog;
using Managerr.Datastore;
using Managerr.Downloaders;
using Managerr.Downloaders.Parser;
using Managerr.Indexers;
using Managerr.Indexers.Categories;
using Managerr.Indexers.Torznab;
using Managerr.Profiles;

namespace Managerr.Host.Services;

public sealed record RssSyncDecisionItem(
    string ReleaseTitle,
    long IndexerId,
    string IndexerName,
    string Protocol,
    string MediaVertical,
    long? MatchedWorkId,
    string? MatchedWorkTitle,
    IReadOnlyList<long> TargetUnitIds,
    bool Approved,
    bool Grabbed,
    string DecisionKind,
    double TotalWeightedScore,
    string Explanation,
    string? GrabbedExternalDownloadId = null);

public sealed record AutomatedRssSyncReport(
    int PolledIndexers,
    int TotalReleases,
    IReadOnlyDictionary<string, IReadOnlyList<IndexedReleaseCandidate>> ReleasesByVertical,
    int MatchedCandidatesCount,
    int ApprovedCount,
    int GrabbedCount,
    IReadOnlyList<RssSyncDecisionItem> Decisions);

public interface IAutomatedRssSyncService
{
    Task<AutomatedRssSyncReport> ExecuteRssSyncAndGrabAsync(CancellationToken ct = default);
    Task<AutomatedRssSyncReport> EvaluatePushedReleasesAsync(IReadOnlyList<IndexedReleaseCandidate> releases, CancellationToken ct = default);
}

/// <summary>
/// Full Sonarr/Radarr-equivalent RSS Sync -> Release Parser -> Monitored Missing/Cutoff-Unmet Matcher
/// -> ComposableProfileService Decision Engine -> UnifiedDownloadOrchestrator Automatic Grab loop.
/// </summary>
public sealed class AutomatedRssSyncService(
    IUnifiedIndexerService indexerService,
    IReleaseParserRouter parserRouter,
    IPolymorphicCatalogService catalogService,
    IComposableProfileService profileService,
    IUnifiedDownloadOrchestrator downloadOrchestrator) : IAutomatedRssSyncService
{
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    public async Task<AutomatedRssSyncReport> ExecuteRssSyncAndGrabAsync(CancellationToken ct = default)
    {
        await _syncGate.WaitAsync(ct);
        try
        {
            var rssReport = await indexerService.SyncMultiCategoryRssAsync(ct);
            return await EvaluateReleasesCoreAsync(rssReport.PolledIndexers, rssReport.TotalReleases, rssReport.ReleasesByVertical, ct);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public async Task<AutomatedRssSyncReport> EvaluatePushedReleasesAsync(
        IReadOnlyList<IndexedReleaseCandidate> releases,
        CancellationToken ct = default)
    {
        await _syncGate.WaitAsync(ct);
        try
        {
            var grouped = releases
                .GroupBy(r => string.IsNullOrWhiteSpace(r.MediaVertical) ? "tv" : r.MediaVertical.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<IndexedReleaseCandidate>)g.ToList(), StringComparer.OrdinalIgnoreCase);
            return await EvaluateReleasesCoreAsync(1, releases.Count, grouped, ct);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task<AutomatedRssSyncReport> EvaluateReleasesCoreAsync(
        int polledIndexers,
        int totalReleases,
        IReadOnlyDictionary<string, IReadOnlyList<IndexedReleaseCandidate>> releasesByVertical,
        CancellationToken ct)
    {
        {
            var rssReport = new { PolledIndexers = polledIndexers, TotalReleases = totalReleases, ReleasesByVertical = releasesByVertical };
            if (rssReport.TotalReleases == 0)
            {
                return new AutomatedRssSyncReport(
                    PolledIndexers: rssReport.PolledIndexers,
                    TotalReleases: 0,
                    ReleasesByVertical: rssReport.ReleasesByVertical,
                    MatchedCandidatesCount: 0,
                    ApprovedCount: 0,
                    GrabbedCount: 0,
                    Decisions: []);
            }

            var libraries = await catalogService.ListLibrariesAsync(null, ct);
            var librariesById = libraries.ToDictionary(l => l.Id);

            var allWorks = await catalogService.ListWorksAsync(null, null, null, ct);
            var monitoredWorks = allWorks.Where(w => w.Monitored).ToList();
            if (monitoredWorks.Count == 0)
            {
                return new AutomatedRssSyncReport(
                    PolledIndexers: rssReport.PolledIndexers,
                    TotalReleases: rssReport.TotalReleases,
                    ReleasesByVertical: rssReport.ReleasesByVertical,
                    MatchedCandidatesCount: 0,
                    ApprovedCount: 0,
                    GrabbedCount: 0,
                    Decisions: []);
            }

            var profiles = await profileService.ListProfilesAsync(ct);
            var indexers = await indexerService.ListIndexersAsync(ct);
            var indexerPriorityById = indexers.ToDictionary(i => i.Id, i => i.Priority);

            var enabledClients = (await downloadOrchestrator.ListClientsAsync(ct))
                .Where(c => c.IsEnabled)
                .ToList();
            var enabledProtocols = enabledClients
                .Select(c => c.Protocol)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var blocklist = await downloadOrchestrator.GetBlocklistSnapshotAsync(ct);
            var imported = await downloadOrchestrator.GetImportedReleaseSnapshotAsync(ct);

            var grabHistory = await downloadOrchestrator.ListGrabHistoryAsync(500, ct);
            var grabbedGuids = grabHistory
                .Select(g => ReleaseBlocklistSnapshot.SanitizeReleaseIdentityToken(g.ReleaseGuid))
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var grabbedHashes = grabHistory
                .Select(g => g.InfoHash)
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Select(h => h!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var grabbedTitles = grabHistory
                .Select(g => ReleaseBlocklistSnapshot.NormalizeTitleKey(g.ReleaseTitle))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<Managerr.Downloaders.Clients.ActiveDownloadQueueItem> activeQueue = [];
            if (enabledClients.Count > 0)
            {
                try
                {
                    activeQueue = await downloadOrchestrator.GetUnifiedQueueAsync(ct);
                }
                catch
                {
                    activeQueue = [];
                }
            }

            var activeTitles = activeQueue
                .Select(q => ReleaseBlocklistSnapshot.NormalizeTitleKey(q.Title))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var treeCache = new Dictionary<long, MediaWorkDetailTree>();
            async Task<MediaWorkDetailTree?> GetCachedWorkTreeAsync(long workId)
            {
                if (treeCache.TryGetValue(workId, out var existing))
                {
                    return existing;
                }

                var loaded = await catalogService.GetWorkTreeAsync(workId, null, ct);
                if (loaded is not null)
                {
                    treeCache[workId] = loaded;
                }

                return loaded;
            }

            var rawCandidates = rssReport.ReleasesByVertical
                .SelectMany(kv => kv.Value)
                .GroupBy(c => !string.IsNullOrWhiteSpace(c.Guid) ? c.Guid : c.DownloadUrl, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var evaluatedCandidates = new List<EvaluatedRssCandidate>();
            var rejectedDecisions = new List<RssSyncDecisionItem>();

            foreach (var candidate in rawCandidates)
            {
                var parsed = parserRouter.Parse(candidate.Title, candidate.MediaVertical);
                var matchedWork = MatchMonitoredWork(candidate, parsed, monitoredWorks, librariesById);
                if (matchedWork is null)
                {
                    continue;
                }

                if (blocklist.IsBlocked(candidate))
                {
                    rejectedDecisions.Add(new RssSyncDecisionItem(
                        ReleaseTitle: candidate.Title,
                        IndexerId: candidate.IndexerId,
                        IndexerName: candidate.IndexerName,
                        Protocol: candidate.Protocol,
                        MediaVertical: candidate.MediaVertical,
                        MatchedWorkId: matchedWork.Id,
                        MatchedWorkTitle: matchedWork.Title,
                        TargetUnitIds: [],
                        Approved: false,
                        Grabbed: false,
                        DecisionKind: "Blocklisted",
                        TotalWeightedScore: 0,
                        Explanation: $"Release '{candidate.Title}' is on the recurring blocklist."));
                    continue;
                }

                var sanitizedCandGuid = ReleaseBlocklistSnapshot.SanitizeReleaseIdentityToken(candidate.Guid);
                var normalizedCandTitle = ReleaseBlocklistSnapshot.NormalizeTitleKey(candidate.Title);
                if ((sanitizedCandGuid.Length > 0 && grabbedGuids.Contains(sanitizedCandGuid)) ||
                    (!string.IsNullOrWhiteSpace(candidate.InfoHash) && grabbedHashes.Contains(candidate.InfoHash)) ||
                    (normalizedCandTitle.Length > 0 && grabbedTitles.Contains(normalizedCandTitle)) ||
                    (normalizedCandTitle.Length > 0 && activeTitles.Contains(normalizedCandTitle)))
                {
                    rejectedDecisions.Add(new RssSyncDecisionItem(
                        ReleaseTitle: candidate.Title,
                        IndexerId: candidate.IndexerId,
                        IndexerName: candidate.IndexerName,
                        Protocol: candidate.Protocol,
                        MediaVertical: candidate.MediaVertical,
                        MatchedWorkId: matchedWork.Id,
                        MatchedWorkTitle: matchedWork.Title,
                        TargetUnitIds: [],
                        Approved: false,
                        Grabbed: false,
                        DecisionKind: "AlreadyGrabbed",
                        TotalWeightedScore: 0,
                        Explanation: "Release was already grabbed or is currently active in the download queue."));
                    continue;
                }

                var tree = await GetCachedWorkTreeAsync(matchedWork.Id);
                if (tree is null || !librariesById.TryGetValue(tree.Work.LibraryId, out var library))
                {
                    continue;
                }

                var (container, targetUnits, skipReason) = ResolveWantedTargetUnits(tree, library.MediaType, parsed);
                if (targetUnits.Count == 0)
                {
                    if (!string.IsNullOrWhiteSpace(skipReason))
                    {
                        rejectedDecisions.Add(new RssSyncDecisionItem(
                            ReleaseTitle: candidate.Title,
                            IndexerId: candidate.IndexerId,
                            IndexerName: candidate.IndexerName,
                            Protocol: candidate.Protocol,
                            MediaVertical: library.MediaType,
                            MatchedWorkId: tree.Work.Id,
                            MatchedWorkTitle: tree.Work.Title,
                            TargetUnitIds: [],
                            Approved: false,
                            Grabbed: false,
                            DecisionKind: "NotWanted",
                            TotalWeightedScore: 0,
                            Explanation: skipReason));
                    }

                    continue;
                }

                // The 500-grab window above forgets old grabs; this one remembers every import per unit
                if (imported.FindImportedFor(candidate, targetUnits.Where(u => u.Files.Count > 0).Select(u => u.Id)) is { } alreadyImported)
                {
                    rejectedDecisions.Add(new RssSyncDecisionItem(
                        ReleaseTitle: candidate.Title,
                        IndexerId: candidate.IndexerId,
                        IndexerName: candidate.IndexerName,
                        Protocol: candidate.Protocol,
                        MediaVertical: library.MediaType,
                        MatchedWorkId: tree.Work.Id,
                        MatchedWorkTitle: tree.Work.Title,
                        TargetUnitIds: targetUnits.Select(u => u.Id).ToList(),
                        Approved: false,
                        Grabbed: false,
                        DecisionKind: "AlreadyImported",
                        TotalWeightedScore: 0,
                        Explanation: ImportedReleaseSnapshot.DescribeRejection(alreadyImported)));
                    continue;
                }

                var primaryUnit = targetUnits[0];
                var effectiveProfileId = ResolveEffectiveProfileId(primaryUnit, tree.Work, library, profiles);
                if (!effectiveProfileId.HasValue)
                {
                    rejectedDecisions.Add(new RssSyncDecisionItem(
                        ReleaseTitle: candidate.Title,
                        IndexerId: candidate.IndexerId,
                        IndexerName: candidate.IndexerName,
                        Protocol: candidate.Protocol,
                        MediaVertical: library.MediaType,
                        MatchedWorkId: tree.Work.Id,
                        MatchedWorkTitle: tree.Work.Title,
                        TargetUnitIds: targetUnits.Select(u => u.Id).ToList(),
                        Approved: false,
                        Grabbed: false,
                        DecisionKind: "NoQualityProfile",
                        TotalWeightedScore: 0,
                        Explanation: "No quality profile is assigned to the target library or unit."));
                    continue;
                }

                var existingFile = primaryUnit.Files
                    .OrderByDescending(f => f.SizeBytes)
                    .FirstOrDefault();

                var candidateFlags = new List<string>();
                if (candidate.DownloadVolumeFactor <= 0.001)
                {
                    candidateFlags.Add("freeleech");
                }
                else if (candidate.DownloadVolumeFactor <= 0.5)
                {
                    candidateFlags.Add("halfleech");
                }

                if (candidate.UploadVolumeFactor >= 2.0)
                {
                    candidateFlags.Add("doubleupload");
                }

                var scorecard = await profileService.SimulateAsync(
                    new SimulateReleaseRequest(
                        ProfileId: effectiveProfileId.Value,
                        ReleaseTitle: candidate.Title,
                        OriginalLanguage: tree.Work.OriginalLanguage,
                        UnitId: primaryUnit.Id,
                        SizeBytes: candidate.SizeBytes > 0 ? candidate.SizeBytes : null,
                        ExistingFileQuality: existingFile?.QualityName,
                        ExistingFileAudioLanguages: existingFile?.DetectedAudioLangs,
                        ExistingFileSubtitleLanguages: existingFile?.DetectedSubLangs,
                        ExistingSourceQuality: existingFile?.SourceQualityName,
                        ExistingVideoCodec: existingFile?.VideoCodec,
                        IndexerFlags: candidateFlags,
                        IndexerId: candidate.IndexerId,
                        WorkTags: tree.Work.Tags,
                        MediaVertical: library.MediaType),
                    ct);

                if (!scorecard.Approved)
                {
                    rejectedDecisions.Add(new RssSyncDecisionItem(
                        ReleaseTitle: candidate.Title,
                        IndexerId: candidate.IndexerId,
                        IndexerName: candidate.IndexerName,
                        Protocol: candidate.Protocol,
                        MediaVertical: library.MediaType,
                        MatchedWorkId: tree.Work.Id,
                        MatchedWorkTitle: tree.Work.Title,
                        TargetUnitIds: targetUnits.Select(u => u.Id).ToList(),
                        Approved: false,
                        Grabbed: false,
                        DecisionKind: scorecard.DecisionKind,
                        TotalWeightedScore: scorecard.TotalWeightedScore,
                        Explanation: scorecard.Explanation));
                    continue;
                }

                var delayDecision = await profileService.EvaluateDelayAsync(
                    protocol: candidate.Protocol,
                    publishedAt: candidate.PublishedAt,
                    workTags: tree.Work.Tags,
                    scorecard: scorecard,
                    ct: ct);

                if (delayDecision.IsDelayed)
                {
                    rejectedDecisions.Add(new RssSyncDecisionItem(
                        ReleaseTitle: candidate.Title,
                        IndexerId: candidate.IndexerId,
                        IndexerName: candidate.IndexerName,
                        Protocol: candidate.Protocol,
                        MediaVertical: library.MediaType,
                        MatchedWorkId: tree.Work.Id,
                        MatchedWorkTitle: tree.Work.Title,
                        TargetUnitIds: targetUnits.Select(u => u.Id).ToList(),
                        Approved: false,
                        Grabbed: false,
                        DecisionKind: "PendingDelay",
                        TotalWeightedScore: scorecard.TotalWeightedScore,
                        Explanation: delayDecision.Reason));
                    continue;
                }

                var scopeKey = parsed.IsFullSeasonPack && container is not null
                    ? $"work:{tree.Work.Id}:season-pack:{container.Id}"
                    : $"work:{tree.Work.Id}:units:{string.Join(",", targetUnits.Select(u => u.Id).OrderBy(id => id))}";

                var priority = indexerPriorityById.TryGetValue(candidate.IndexerId, out var p) ? p : 25;
                evaluatedCandidates.Add(new EvaluatedRssCandidate(
                    Candidate: candidate,
                    Parsed: parsed,
                    Work: tree.Work,
                    Library: library,
                    Container: container,
                    TargetUnits: targetUnits,
                    Scorecard: scorecard,
                    ScopeKey: scopeKey,
                    IndexerPriority: priority,
                    ProtocolPreferred: delayDecision.ProtocolPreferred));
            }

            var finalDecisions = new List<RssSyncDecisionItem>(rejectedDecisions);
            var claimedUnitIds = new HashSet<long>();
            var unitsInFlight = await downloadOrchestrator.GetUnitIdsInFlightAsync(ct);
            var grabbedCount = 0;

            // Process season packs before individual episodes when higher/equal score so we don't grab redundant single episodes
            var orderedGroups = evaluatedCandidates
                .GroupBy(e => e.ScopeKey, StringComparer.Ordinal)
                .Select(g => g
                    .OrderByDescending(x => x.Scorecard.TotalWeightedScore)
                    .ThenByDescending(x => x.ProtocolPreferred)
                    .ThenByDescending(x => x.Candidate.Seeders ?? 0)
                    .ThenBy(x => x.IndexerPriority)
                    .ToList())
                .OrderByDescending(list => list[0].Parsed.IsFullSeasonPack)
                .ThenByDescending(list => list[0].Scorecard.TotalWeightedScore)
                .ToList();

            foreach (var group in orderedGroups)
            {
                var winner = group[0];
                var unitIds = winner.TargetUnits.Select(u => u.Id).ToList();

                if (unitIds.All(id => claimedUnitIds.Contains(id)))
                {
                    foreach (var item in group)
                    {
                        finalDecisions.Add(new RssSyncDecisionItem(
                            ReleaseTitle: item.Candidate.Title,
                            IndexerId: item.Candidate.IndexerId,
                            IndexerName: item.Candidate.IndexerName,
                            Protocol: item.Candidate.Protocol,
                            MediaVertical: item.Library.MediaType,
                            MatchedWorkId: item.Work.Id,
                            MatchedWorkTitle: item.Work.Title,
                            TargetUnitIds: unitIds,
                            Approved: true,
                            Grabbed: false,
                            DecisionKind: item.Scorecard.DecisionKind,
                            TotalWeightedScore: item.Scorecard.TotalWeightedScore,
                            Explanation: "Superseded by a higher-ranked season pack or release grabbed in the same RSS sync pass."));
                    }

                    continue;
                }

                // A download for one of these units is still coming: grabbing another release would download it twice (#64)
                var busyUnits = unitIds.Where(unitsInFlight.Contains).ToList();
                if (busyUnits.Count > 0)
                {
                    foreach (var item in group)
                    {
                        finalDecisions.Add(new RssSyncDecisionItem(
                            ReleaseTitle: item.Candidate.Title,
                            IndexerId: item.Candidate.IndexerId,
                            IndexerName: item.Candidate.IndexerName,
                            Protocol: item.Candidate.Protocol,
                            MediaVertical: item.Library.MediaType,
                            MatchedWorkId: item.Work.Id,
                            MatchedWorkTitle: item.Work.Title,
                            TargetUnitIds: unitIds,
                            Approved: false,
                            Grabbed: false,
                            DecisionKind: "AlreadyDownloading",
                            TotalWeightedScore: item.Scorecard.TotalWeightedScore,
                            Explanation: $"Not grabbed: a download for {busyUnits.Count} of its {unitIds.Count} unit(s) is still in flight (downloading or waiting for import)."));
                    }

                    continue;
                }

                string? grabbedExternalId = null;
                var wasGrabbed = false;
                var explanation = winner.Scorecard.Explanation;

                var downloadUrlOrMagnet = winner.Candidate.Protocol.Equals("torrent", StringComparison.OrdinalIgnoreCase) &&
                                          !string.IsNullOrWhiteSpace(winner.Candidate.MagnetUrl) &&
                                          string.IsNullOrWhiteSpace(winner.Candidate.DownloadUrl)
                    ? winner.Candidate.MagnetUrl!
                    : winner.Candidate.DownloadUrl;

                if (string.IsNullOrWhiteSpace(downloadUrlOrMagnet))
                {
                    explanation = $"{winner.Scorecard.Explanation} (Skipped grab: candidate has no download URL or magnet URI.)";
                }
                else if (!enabledProtocols.Contains(winner.Candidate.Protocol))
                {
                    explanation = $"{winner.Scorecard.Explanation} (Approved, awaiting enabled '{winner.Candidate.Protocol}' download client.)";
                }
                else
                {
                    try
                    {
                        var grabOutcome = await downloadOrchestrator.GrabReleaseAsync(
                            new GrabReleaseRequest(
                                Title: winner.Candidate.Title,
                                Protocol: winner.Candidate.Protocol,
                                MediaVertical: winner.Library.MediaType,
                                DownloadUrlOrMagnet: downloadUrlOrMagnet,
                                InfoHash: winner.Candidate.InfoHash,
                                WorkId: winner.Work.Id,
                                ContainerId: winner.Container?.Id,
                                UnitIds: unitIds,
                                IndexerId: winner.Candidate.IndexerId,
                                ReleaseGuid: winner.Candidate.Guid,
                                SourceType: "rss_sync",
                                IndexerName: winner.Candidate.IndexerName,
                                SizeBytes: winner.Candidate.SizeBytes,
                                PublishedAt: winner.Candidate.PublishedAt),
                            ct);

                        wasGrabbed = true;
                        grabbedCount++;
                        grabbedExternalId = grabOutcome.ExternalDownloadId;
                        foreach (var uid in unitIds)
                        {
                            claimedUnitIds.Add(uid);
                        }
                    }
                    catch (Exception ex)
                    {
                        explanation = $"{winner.Scorecard.Explanation} (Grab dispatch failed: {ex.Message})";
                    }
                }

                finalDecisions.Add(new RssSyncDecisionItem(
                    ReleaseTitle: winner.Candidate.Title,
                    IndexerId: winner.Candidate.IndexerId,
                    IndexerName: winner.Candidate.IndexerName,
                    Protocol: winner.Candidate.Protocol,
                    MediaVertical: winner.Library.MediaType,
                    MatchedWorkId: winner.Work.Id,
                    MatchedWorkTitle: winner.Work.Title,
                    TargetUnitIds: unitIds,
                    Approved: true,
                    Grabbed: wasGrabbed,
                    DecisionKind: winner.Scorecard.DecisionKind,
                    TotalWeightedScore: winner.Scorecard.TotalWeightedScore,
                    Explanation: explanation,
                    GrabbedExternalDownloadId: grabbedExternalId));

                for (var i = 1; i < group.Count; i++)
                {
                    var runnerUp = group[i];
                    finalDecisions.Add(new RssSyncDecisionItem(
                        ReleaseTitle: runnerUp.Candidate.Title,
                        IndexerId: runnerUp.Candidate.IndexerId,
                        IndexerName: runnerUp.Candidate.IndexerName,
                        Protocol: runnerUp.Candidate.Protocol,
                        MediaVertical: runnerUp.Library.MediaType,
                        MatchedWorkId: runnerUp.Work.Id,
                        MatchedWorkTitle: runnerUp.Work.Title,
                        TargetUnitIds: unitIds,
                        Approved: true,
                        Grabbed: false,
                        DecisionKind: runnerUp.Scorecard.DecisionKind,
                        TotalWeightedScore: runnerUp.Scorecard.TotalWeightedScore,
                        Explanation: $"Approved ({runnerUp.Scorecard.TotalWeightedScore:F1} pts), superseded by higher-scoring release '{winner.Candidate.Title}' ({winner.Scorecard.TotalWeightedScore:F1} pts)."));
                }
            }

            var matchedCount = rejectedDecisions.Count + evaluatedCandidates.Count;
            var approvedCount = evaluatedCandidates.Count;

            return new AutomatedRssSyncReport(
                PolledIndexers: rssReport.PolledIndexers,
                TotalReleases: rssReport.TotalReleases,
                ReleasesByVertical: rssReport.ReleasesByVertical,
                MatchedCandidatesCount: matchedCount,
                ApprovedCount: approvedCount,
                GrabbedCount: grabbedCount,
                Decisions: finalDecisions);
        }
    }

    private static MediaWorkSummary? MatchMonitoredWork(
        IndexedReleaseCandidate candidate,
        ParsedReleaseInfo parsed,
        IReadOnlyList<MediaWorkSummary> monitoredWorks,
        IReadOnlyDictionary<long, LibrarySummary> librariesById)
    {
        var verticalWorks = monitoredWorks
            .Where(w => librariesById.TryGetValue(w.LibraryId, out var lib) &&
                        AreVerticalsCompatible(candidate.MediaVertical, lib.MediaType))
            .ToList();

        if (verticalWorks.Count == 0)
        {
            return null;
        }

        // 1. Exact External ID match (TVDB / TMDB / IMDb)
        if (candidate.TvdbId.HasValue && candidate.TvdbId.Value > 0)
        {
            var tvdbStr = candidate.TvdbId.Value.ToString();
            var byTvdb = verticalWorks.FirstOrDefault(w =>
                (w.ExternalIds.TryGetValue("tvdb", out var v1) && v1 == tvdbStr) ||
                (w.ExternalIds.TryGetValue("tvdbId", out var v2) && v2 == tvdbStr));
            if (byTvdb is not null)
            {
                return byTvdb;
            }
        }

        if (candidate.TmdbId.HasValue && candidate.TmdbId.Value > 0)
        {
            var tmdbStr = candidate.TmdbId.Value.ToString();
            var byTmdb = verticalWorks.FirstOrDefault(w =>
                (w.ExternalIds.TryGetValue("tmdb", out var v1) && v1 == tmdbStr) ||
                (w.ExternalIds.TryGetValue("tmdbId", out var v2) && v2 == tmdbStr));
            if (byTmdb is not null)
            {
                return byTmdb;
            }
        }

        if (!string.IsNullOrWhiteSpace(candidate.ImdbId))
        {
            var normalizedImdb = candidate.ImdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
                ? candidate.ImdbId
                : $"tt{candidate.ImdbId}";
            var byImdb = verticalWorks.FirstOrDefault(w =>
                (w.ExternalIds.TryGetValue("imdb", out var v1) && string.Equals(v1, normalizedImdb, StringComparison.OrdinalIgnoreCase)) ||
                (w.ExternalIds.TryGetValue("imdbId", out var v2) && string.Equals(v2, normalizedImdb, StringComparison.OrdinalIgnoreCase)));
            if (byImdb is not null)
            {
                return byImdb;
            }
        }

        // 2. Normalized CleanSeriesTitle / AltTitles match (Sonarr ParsingService parity)
        if (string.IsNullOrWhiteSpace(parsed.CleanTitle))
        {
            return null;
        }

        var titleMatches = verticalWorks
            .Where(w =>
            {
                var names = new List<string>(2 + w.AltTitles.Count) { w.Title, w.SortTitle };
                names.AddRange(w.AltTitles);
                if (!names.Any(n => ReleaseTitleNormalizer.TitlesMatch(parsed.CleanTitle, n, w.Year)))
                {
                    return false;
                }

                // For standalone movies, enforce +-1 year guard if both release and work declare a year
                if (librariesById.TryGetValue(w.LibraryId, out var lib) &&
                    lib.MediaType.Equals(MediaVerticals.Movies, StringComparison.OrdinalIgnoreCase) &&
                    parsed.Year.HasValue &&
                    w.Year.HasValue &&
                    Math.Abs(parsed.Year.Value - w.Year.Value) > 1)
                {
                    return false;
                }

                return true;
            })
            .OrderByDescending(w => parsed.Year.HasValue && w.Year == parsed.Year ? 1 : 0)
            .ToList();

        return titleMatches.FirstOrDefault();
    }

    private static (MediaContainerDetail? Container, IReadOnlyList<MediaUnitDetail> TargetUnits, string? SkipReason)
        ResolveWantedTargetUnits(
            MediaWorkDetailTree tree,
            string libraryMediaType,
            ParsedReleaseInfo parsed)
    {
        var isEpisodic = libraryMediaType.Equals(MediaVerticals.Tv, StringComparison.OrdinalIgnoreCase) ||
                         libraryMediaType.Equals(MediaVerticals.Anime, StringComparison.OrdinalIgnoreCase);

        if (isEpisodic)
        {
            if (parsed.SeasonNumber.HasValue)
            {
                var season = tree.Containers.FirstOrDefault(c => c.ContainerNumber == parsed.SeasonNumber.Value);
                if (season is null)
                {
                    return (null, [], $"Season {parsed.SeasonNumber.Value} does not exist in catalog for '{tree.Work.Title}'.");
                }

                if (!season.Monitored)
                {
                    return (season, [], $"Season {parsed.SeasonNumber.Value} is unmonitored.");
                }

                if (parsed.IsFullSeasonPack)
                {
                    var wantedInSeason = season.Units
                        .Where(u => u.Monitored && IsUnitWanted(u))
                        .ToList();
                    return wantedInSeason.Count > 0
                        ? (season, wantedInSeason, null)
                        : (season, [], $"All monitored episodes in Season {parsed.SeasonNumber.Value} already satisfy quality and language cutoffs.");
                }

                if (parsed.EpisodeNumbers.Count == 0)
                {
                    return (season, [], "Episodic release did not specify episode numbers or a full season pack.");
                }

                var matchedEpisodes = season.Units
                    .Where(u => parsed.EpisodeNumbers.Contains(u.UnitNumber))
                    .OrderBy(u => u.UnitNumber)
                    .ToList();

                if (matchedEpisodes.Count != parsed.EpisodeNumbers.Count)
                {
                    return (season, [], $"Requested episode(s) S{parsed.SeasonNumber.Value:D2}E{string.Join("E", parsed.EpisodeNumbers.Select(e => e.ToString("D2")))} are not present in '{tree.Work.Title}'.");
                }

                if (matchedEpisodes.All(u => !u.Monitored))
                {
                    return (season, [], "Target episode(s) are unmonitored.");
                }

                var wantedEpisodes = matchedEpisodes.Where(IsUnitWanted).ToList();
                return wantedEpisodes.Count > 0
                    ? (season, wantedEpisodes, null)
                    : (season, [], "Target episode(s) already satisfy quality and language cutoffs.");
            }

            if (parsed.AbsoluteEpisodeNumbers.Count > 0)
            {
                var absTarget = parsed.AbsoluteEpisodeNumbers[0];
                foreach (var c in tree.Containers.Where(c => c.Monitored))
                {
                    var absUnit = c.Units.FirstOrDefault(u =>
                        u.AbsoluteNumber == absTarget ||
                        (tree.Containers.Count == 1 && u.UnitNumber == absTarget));
                    if (absUnit is not null)
                    {
                        if (!absUnit.Monitored)
                        {
                            return (c, [], $"Absolute episode {absTarget} is unmonitored.");
                        }

                        return IsUnitWanted(absUnit)
                            ? (c, [absUnit], null)
                            : (c, [], $"Absolute episode {absTarget} already satisfies quality and language cutoffs.");
                    }
                }
            }

            return (null, [], "Could not resolve season/episode numbers from episodic RSS title.");
        }

        // Movies, Music, Ebooks, Audiobooks, Comics
        foreach (var container in tree.Containers.Where(c => c.Monitored))
        {
            var wantedUnits = container.Units
                .Where(u => u.Monitored && IsUnitWanted(u))
                .ToList();
            if (wantedUnits.Count > 0)
            {
                var targetUnit = wantedUnits[0];
                if (!MinimumAvailabilityPolicy.IsWorkAvailableForAutoGrab(tree.Work, targetUnit.AirOrReleaseDate))
                {
                    return (container, [], $"Movie '{tree.Work.Title}' has not reached Minimum Availability '{tree.Work.MinimumAvailability}' (status: '{tree.Work.Status}').");
                }

                return (container, [targetUnit], null);
            }
        }

        return (null, [], $"All monitored units for '{tree.Work.Title}' already satisfy quality and language cutoffs.");
    }

    private static bool IsUnitWanted(MediaUnitDetail unit) =>
        unit.Files.Count == 0 || !unit.QualityCutoffMet || !unit.LanguageCutoffMet;

    private static long? ResolveEffectiveProfileId(
        MediaUnitDetail unit,
        MediaWorkSummary work,
        LibrarySummary library,
        IReadOnlyList<QualityProfileSummary> profiles)
    {
        if (unit.ProfileOverrideId.HasValue)
        {
            return unit.ProfileOverrideId.Value;
        }

        if (work.QualityProfileId.HasValue)
        {
            return work.QualityProfileId.Value;
        }

        if (library.DefaultProfileId.HasValue)
        {
            return library.DefaultProfileId.Value;
        }

        var isVideo = library.MediaType.Equals(MediaVerticals.Movies, StringComparison.OrdinalIgnoreCase) ||
                      library.MediaType.Equals(MediaVerticals.Tv, StringComparison.OrdinalIgnoreCase) ||
                      library.MediaType.Equals(MediaVerticals.Anime, StringComparison.OrdinalIgnoreCase);

        if (isVideo)
        {
            var germanBest = profiles.FirstOrDefault(p =>
                p.Slug.Equals("german-first-best-quality", StringComparison.OrdinalIgnoreCase));
            if (germanBest is not null)
            {
                return germanBest.Id;
            }
        }

        var verticalMatch = profiles.FirstOrDefault(p =>
            p.MediaVertical.Equals(library.MediaType, StringComparison.OrdinalIgnoreCase) ||
            (isVideo && p.MediaVertical.Equals("video", StringComparison.OrdinalIgnoreCase)));
        return verticalMatch?.Id ?? profiles.FirstOrDefault()?.Id;
    }

    private static bool AreVerticalsCompatible(string candidateVertical, string libraryMediaType)
    {
        if (string.Equals(candidateVertical, libraryMediaType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if ((candidateVertical.Equals(MediaVerticals.Tv, StringComparison.OrdinalIgnoreCase) &&
             libraryMediaType.Equals(MediaVerticals.Anime, StringComparison.OrdinalIgnoreCase)) ||
            (candidateVertical.Equals(MediaVerticals.Anime, StringComparison.OrdinalIgnoreCase) &&
             libraryMediaType.Equals(MediaVerticals.Tv, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private sealed record EvaluatedRssCandidate(
        IndexedReleaseCandidate Candidate,
        ParsedReleaseInfo Parsed,
        MediaWorkSummary Work,
        LibrarySummary Library,
        MediaContainerDetail? Container,
        IReadOnlyList<MediaUnitDetail> TargetUnits,
        ReleaseSimulationScorecard Scorecard,
        string ScopeKey,
        int IndexerPriority,
        bool ProtocolPreferred = true);
}
