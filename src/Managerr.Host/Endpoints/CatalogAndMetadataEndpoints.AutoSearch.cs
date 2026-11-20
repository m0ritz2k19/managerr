using Managerr.Catalog;
using Managerr.Downloaders;
using Managerr.Indexers;
using Managerr.Metadata;
using Managerr.Migration;
using Managerr.Profiles;
using Managerr.Security.Users;

namespace Managerr.Host.Endpoints;

public static partial class CatalogAndMetadataEndpoints
{
    internal static async Task<CatalogAutoSearchResultDto?> ExecuteWorkAutoSearchAsync(
        long id,
        PlexUserSummary? actor,
        IPolymorphicCatalogService catalog,
        IComposableProfileService profileService,
        IReleaseSearchOrchestrator searchOrchestrator,
        IUnifiedDownloadOrchestrator orchestrator,
        CancellationToken ct)
    {
        var tree = await catalog.GetWorkTreeAsync(id, actor, ct);
        if (tree is null)
        {
            return null;
        }

        var w = tree.Work;
        var monitoredUnits = tree.Containers.SelectMany(c => c.Units).Where(u => u.Monitored).ToList();
        var wantedUnits = monitoredUnits
            .Where(u => u.Files.Count == 0 || !u.QualityCutoffMet || !u.LanguageCutoffMet)
            .ToList();
        if (wantedUnits.Count == 0)
        {
            return new CatalogAutoSearchResultDto(
                WorkId: id,
                CandidatesFound: 0,
                Grabbed: false,
                Message: $"All monitored units for '{tree.Work.Title}' already have on-disk files meeting cutoff — 0 searches dispatched.");
        }

        var isEpisodic = w.WorkKind is MetadataVerticals.Tv or MetadataVerticals.Anime or "series";
        var blocklist = await orchestrator.GetBlocklistSnapshotAsync(ct);

        int totalCandidatesFound = 0;
        var grabbedReleases = new List<string>();
        GrabReleaseOutcome? firstGrabOutcome = null;
        string? firstSelectedRelease = null;

        if (isEpisodic)
        {
            var seasonGroups = wantedUnits
                .GroupBy(u => u.ContainerId)
                .Select(g => (Container: tree.Containers.FirstOrDefault(c => c.Id == g.Key), Units: g.ToList()))
                .Where(g => g.Container is not null)
                .ToList();

            var grabbedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (container, units) in seasonGroups)
            {
                var seasonNum = container!.ContainerNumber;
                var seasonProfile = await ResolveEffectiveProfileAsync(
                    profileService,
                    catalog,
                    w,
                    units.Select(u => u.ProfileOverrideId).FirstOrDefault(p => p.HasValue),
                    ct);
                var reqAudio = units.SelectMany(u => u.RequiredAudioLangs).ToHashSet(StringComparer.OrdinalIgnoreCase);

                var packGrabbed = false;
                if (units.Count >= 2 && seasonNum > 0)
                {
                    var packScoped = await searchOrchestrator.SearchScopedAsync(
                        new OrchestratedReleaseSearchRequest(
                            Query: new IndexerSearchQuery(
                                Query: w.Title,
                                MediaVertical: w.WorkKind,
                                Season: seasonNum,
                                Interactive: false,
                                WorkId: id),
                            WorkTree: tree,
                            ContainerId: container.Id,
                            IncludeSeasonPacks: true,
                            Actor: actor),
                        ct);
                    totalCandidatesFound += packScoped.Count;

                    var packCandidates = packScoped
                        .Where(x => x.IsSeasonPack && x.EffectiveSeason == seasonNum)
                        .Where(x => !blocklist.IsBlocked(x.Release))
                        .Select(x => (x.Release, x.Parsed, Score: ScoreReleaseCandidate(x.Release, x.Parsed, reqAudio, w.OriginalLanguage, seasonProfile) + 250))
                        .Where(x => x.Score >= 0)
                        .OrderByDescending(x => x.Score)
                        .ThenByDescending(x => x.Release.SizeBytes)
                        .ToList();

                    if (packCandidates.Count > 0 && container.Units.All(u => u.Files.Count == 0))
                    {
                        var bestPack = packCandidates[0];
                        var dl = bestPack.Release.MagnetUrl ?? bestPack.Release.DownloadUrl;
                        if (grabbedUrls.Add(dl))
                        {
                            try
                            {
                                var outcome = await orchestrator.GrabReleaseAsync(
                                    new GrabReleaseRequest(
                                        Title: bestPack.Release.Title,
                                        Protocol: bestPack.Release.Protocol,
                                        MediaVertical: w.WorkKind,
                                        DownloadUrlOrMagnet: dl,
                                        InfoHash: bestPack.Release.InfoHash,
                                        WorkId: id,
                                        ContainerId: container.Id,
                                        UnitIds: container.Units.Select(u => u.Id).ToArray(),
                                        IndexerId: bestPack.Release.IndexerId,
                                        ReleaseGuid: bestPack.Release.Guid,
                                        SourceType: "auto_search",
                                        IndexerName: bestPack.Release.IndexerName,
                                        SizeBytes: bestPack.Release.SizeBytes,
                                        PublishedAt: bestPack.Release.PublishedAt),
                                    ct);
                                grabbedReleases.Add(bestPack.Release.Title);
                                firstGrabOutcome ??= outcome;
                                firstSelectedRelease ??= bestPack.Release.Title;
                                packGrabbed = true;
                            }
                            catch
                            {
                                // Fall through to episode searches
                            }
                        }
                    }
                }

                if (packGrabbed)
                {
                    continue;
                }

                foreach (var unit in units)
                {
                    var unitProfile = unit.ProfileOverrideId.HasValue
                        ? await ResolveEffectiveProfileAsync(profileService, catalog, w, unit.ProfileOverrideId, ct)
                        : seasonProfile;
                    var unitAudio = unit.RequiredAudioLangs.ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var epScoped = await searchOrchestrator.SearchScopedAsync(
                        new OrchestratedReleaseSearchRequest(
                            Query: new IndexerSearchQuery(
                                Query: w.Title,
                                MediaVertical: w.WorkKind,
                                Season: seasonNum,
                                Episode: unit.UnitNumber,
                                Interactive: false,
                                WorkId: id,
                                UnitId: unit.Id),
                            WorkTree: tree,
                            ContainerId: container.Id,
                            UnitId: unit.Id,
                            AbsoluteEpisodeNumber: unit.AbsoluteNumber,
                            IncludeSeasonPacks: false,
                            Actor: actor),
                        ct);
                    totalCandidatesFound += epScoped.Count;

                    var epCandidates = epScoped
                        .Where(x => !blocklist.IsBlocked(x.Release))
                        .Where(x => (!x.IsSeasonPack && x.EffectiveSeason == seasonNum && x.EffectiveEpisodes.Contains(unit.UnitNumber)) ||
                                    (!x.EffectiveSeason.HasValue && unit.AbsoluteNumber is { } abs && x.Parsed.AbsoluteEpisodeNumbers.Contains(abs)))
                        .Select(x => (x.Release, x.Parsed, Score: ScoreReleaseCandidate(x.Release, x.Parsed, unitAudio, w.OriginalLanguage, unitProfile)))
                        .Where(x => x.Score > -2000)
                        .OrderByDescending(x => x.Score)
                        .ThenByDescending(x => x.Release.SizeBytes)
                        .ToList();

                    if (epCandidates.Count > 0)
                    {
                        var best = epCandidates[0];
                        var dl = best.Release.MagnetUrl ?? best.Release.DownloadUrl;
                        if (grabbedUrls.Add(dl))
                        {
                            try
                            {
                                var outcome = await orchestrator.GrabReleaseAsync(
                                    new GrabReleaseRequest(
                                        Title: best.Release.Title,
                                        Protocol: best.Release.Protocol,
                                        MediaVertical: w.WorkKind,
                                        DownloadUrlOrMagnet: dl,
                                        InfoHash: best.Release.InfoHash,
                                        WorkId: id,
                                        ContainerId: container.Id,
                                        UnitIds: [unit.Id],
                                        IndexerId: best.Release.IndexerId,
                                        ReleaseGuid: best.Release.Guid,
                                        SourceType: "auto_search",
                                        IndexerName: best.Release.IndexerName,
                                        SizeBytes: best.Release.SizeBytes,
                                        PublishedAt: best.Release.PublishedAt),
                                    ct);
                                grabbedReleases.Add(best.Release.Title);
                                firstGrabOutcome ??= outcome;
                                firstSelectedRelease ??= best.Release.Title;
                            }
                            catch
                            {
                                // ignore individual grab failure
                            }
                        }
                    }
                }
            }
        }
        else
        {
            var unitOverrideId = wantedUnits.Select(u => u.ProfileOverrideId).FirstOrDefault(pid => pid.HasValue);
            var effectiveProfile = await ResolveEffectiveProfileAsync(profileService, catalog, w, unitOverrideId, ct);
            var reqAudio = wantedUnits
                .SelectMany(u => u.RequiredAudioLangs)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var scoped = await searchOrchestrator.SearchScopedAsync(
                new OrchestratedReleaseSearchRequest(
                    Query: new IndexerSearchQuery(
                        Query: w.Title,
                        MediaVertical: w.WorkKind,
                        Interactive: false,
                        WorkId: id),
                    WorkTree: tree,
                    IncludeSeasonPacks: false,
                    Actor: actor),
                ct);
            totalCandidatesFound = scoped.Count;

            var scored = scoped
                .Where(r => !blocklist.IsBlocked(r.Release))
                .Select(r => (r.Release, r.Parsed, Score: ScoreReleaseCandidate(r.Release, r.Parsed, reqAudio, w.OriginalLanguage, effectiveProfile)))
                .Where(x => x.Score > -2000)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Release.SizeBytes)
                .ToList();

            if (scored.Count > 0)
            {
                var best = scored[0];
                try
                {
                    var outcome = await orchestrator.GrabReleaseAsync(
                        new GrabReleaseRequest(
                            Title: best.Release.Title,
                            Protocol: best.Release.Protocol,
                            MediaVertical: w.WorkKind,
                            DownloadUrlOrMagnet: best.Release.MagnetUrl ?? best.Release.DownloadUrl,
                            InfoHash: best.Release.InfoHash,
                            WorkId: id,
                            UnitIds: wantedUnits.Select(u => u.Id).ToArray(),
                            IndexerId: best.Release.IndexerId,
                            ReleaseGuid: best.Release.Guid,
                            SourceType: "auto_search",
                            IndexerName: best.Release.IndexerName,
                            SizeBytes: best.Release.SizeBytes,
                            PublishedAt: best.Release.PublishedAt),
                        ct);
                    grabbedReleases.Add(best.Release.Title);
                    firstGrabOutcome = outcome;
                    firstSelectedRelease = best.Release.Title;
                }
                catch (Exception ex)
                {
                    return new CatalogAutoSearchResultDto(
                        WorkId: id,
                        CandidatesFound: totalCandidatesFound,
                        Grabbed: false,
                        Message: $"Found {scored.Count} release(s) (best: '{best.Release.Title}'), but download client grab failed: {ex.Message}",
                        SelectedRelease: best.Release.Title);
                }
            }
        }

        if (grabbedReleases.Count > 0)
        {
            return new CatalogAutoSearchResultDto(
                WorkId: id,
                CandidatesFound: totalCandidatesFound,
                Grabbed: true,
                Message: grabbedReleases.Count == 1
                    ? $"Grabbed '{firstSelectedRelease}' via {firstGrabOutcome?.ClientName} (category: {firstGrabOutcome?.ResolvedCategory})."
                    : $"Grabbed {grabbedReleases.Count} releases (first: '{firstSelectedRelease}').",
                SelectedRelease: firstSelectedRelease,
                GrabOutcome: firstGrabOutcome);
        }

        return new CatalogAutoSearchResultDto(
            WorkId: id,
            CandidatesFound: totalCandidatesFound,
            Grabbed: false,
            Message: totalCandidatesFound == 0
                ? $"Searched configured indexers for '{w.Title}' — 0 releases found."
                : $"Found {totalCandidatesFound} release(s) for '{w.Title}', but 0 approved matching releases met criteria.",
            SelectedRelease: null);
    }

    internal static async Task<CatalogAutoSearchResultDto?> ExecuteContainerAutoSearchAsync(
        long id,
        long containerId,
        PlexUserSummary? actor,
        IPolymorphicCatalogService catalog,
        IComposableProfileService profileService,
        IReleaseSearchOrchestrator searchOrchestrator,
        IUnifiedDownloadOrchestrator orchestrator,
        CancellationToken ct)
    {
        var tree = await catalog.GetWorkTreeAsync(id, actor, ct);
        var container = tree?.Containers.FirstOrDefault(c => c.Id == containerId);
        if (tree is null || container is null)
        {
            return null;
        }

        var w = tree.Work;
        var seasonNum = container.ContainerNumber;
        var seasonOverrideId = container.Units.Select(u => u.ProfileOverrideId).FirstOrDefault(pid => pid.HasValue);
        var seasonProfile = await ResolveEffectiveProfileAsync(profileService, catalog, w, seasonOverrideId, ct);
        var blocklist = await orchestrator.GetBlocklistSnapshotAsync(ct);

        var wantedInSeason = container.Units
            .Where(u => u.Monitored && (u.Files.Count == 0 || !u.QualityCutoffMet || !u.LanguageCutoffMet))
            .ToList();
        if (wantedInSeason.Count == 0)
        {
            return new CatalogAutoSearchResultDto(
                WorkId: id,
                CandidatesFound: 0,
                Grabbed: false,
                Message: $"All monitored episodes in '{container.Title}' already have on-disk files meeting cutoff — 0 searches dispatched.",
                ContainerId: containerId);
        }

        var seasonScoped = await searchOrchestrator.SearchScopedAsync(
            new OrchestratedReleaseSearchRequest(
                Query: new IndexerSearchQuery(
                    Query: w.Title,
                    MediaVertical: w.WorkKind,
                    Season: seasonNum,
                    Interactive: false,
                    WorkId: id),
                WorkTree: tree,
                ContainerId: containerId,
                IncludeSeasonPacks: true,
                Actor: actor),
            ct);

        var reqAudio = container.Units
            .Where(u => u.Monitored)
            .SelectMany(u => u.RequiredAudioLangs)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var seasonPacks = seasonScoped
            .Where(r => !blocklist.IsBlocked(r.Release))
            .Where(x => x.IsSeasonPack && x.EffectiveSeason == seasonNum)
            .Select(x => (x.Release, x.Parsed, Score: ScoreReleaseCandidate(x.Release, x.Parsed, reqAudio, w.OriginalLanguage, seasonProfile) + 250))
            .Where(x => x.Score > -2000)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Release.SizeBytes)
            .ToList();

        if (seasonPacks.Count > 0 && seasonPacks[0].Score >= 0 && container.Units.All(u => u.Files.Count == 0))
        {
            var bestPack = seasonPacks[0];
            try
            {
                var outcome = await orchestrator.GrabReleaseAsync(
                    new GrabReleaseRequest(
                        Title: bestPack.Release.Title,
                        Protocol: bestPack.Release.Protocol,
                        MediaVertical: w.WorkKind,
                        DownloadUrlOrMagnet: bestPack.Release.MagnetUrl ?? bestPack.Release.DownloadUrl,
                        InfoHash: bestPack.Release.InfoHash,
                        WorkId: id,
                        ContainerId: containerId,
                        UnitIds: container.Units.Select(u => u.Id).ToArray(),
                        IndexerId: bestPack.Release.IndexerId,
                        ReleaseGuid: bestPack.Release.Guid,
                        SourceType: "auto_search",
                        IndexerName: bestPack.Release.IndexerName,
                        SizeBytes: bestPack.Release.SizeBytes,
                        PublishedAt: bestPack.Release.PublishedAt),
                    ct);

                return new CatalogAutoSearchResultDto(
                    WorkId: id,
                    CandidatesFound: seasonScoped.Count,
                    Grabbed: true,
                    Message: $"Grabbed season pack '{bestPack.Release.Title}' for {container.Title} via {outcome.ClientName} (category: {outcome.ResolvedCategory}).",
                    SelectedRelease: bestPack.Release.Title,
                    ContainerId: containerId,
                    SeasonNumber: seasonNum,
                    GrabbedCount: 1,
                    GrabOutcome: outcome);
            }
            catch (Exception ex)
            {
                return new CatalogAutoSearchResultDto(
                    WorkId: id,
                    CandidatesFound: seasonScoped.Count,
                    Grabbed: false,
                    Message: $"Found season pack '{bestPack.Release.Title}', but download client grab failed: {ex.Message}",
                    SelectedRelease: bestPack.Release.Title,
                    ContainerId: containerId,
                    SeasonNumber: seasonNum,
                    GrabbedCount: 0);
            }
        }

        var grabbedTitles = new List<string>();
        var grabbedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalCandidates = seasonScoped.Count;
        string? lastError = null;

        foreach (var unit in wantedInSeason)
        {
            var unitProfile = unit.ProfileOverrideId.HasValue
                ? await ResolveEffectiveProfileAsync(profileService, catalog, w, unit.ProfileOverrideId, ct)
                : seasonProfile;
            var unitAudio = unit.RequiredAudioLangs.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var epCandidates = seasonScoped
                .Where(r => !blocklist.IsBlocked(r.Release))
                .Where(x => (!x.IsSeasonPack && x.EffectiveSeason == seasonNum && x.EffectiveEpisodes.Contains(unit.UnitNumber)) ||
                            (!x.EffectiveSeason.HasValue && unit.AbsoluteNumber is { } abs && x.Parsed.AbsoluteEpisodeNumbers.Contains(abs)))
                .Select(x => (x.Release, x.Parsed))
                .ToList();

            if (epCandidates.Count == 0)
            {
                var epFetched = await searchOrchestrator.SearchScopedAsync(
                    new OrchestratedReleaseSearchRequest(
                        Query: new IndexerSearchQuery(
                            Query: w.Title,
                            MediaVertical: w.WorkKind,
                            Season: seasonNum,
                            Episode: unit.UnitNumber,
                            Interactive: false,
                            WorkId: id,
                            UnitId: unit.Id),
                        WorkTree: tree,
                        ContainerId: containerId,
                        UnitId: unit.Id,
                        AbsoluteEpisodeNumber: unit.AbsoluteNumber,
                        IncludeSeasonPacks: false,
                        Actor: actor),
                    ct);
                totalCandidates += epFetched.Count;
                epCandidates = epFetched
                    .Where(r => !blocklist.IsBlocked(r.Release))
                    .Where(x => (!x.IsSeasonPack && x.EffectiveSeason == seasonNum && x.EffectiveEpisodes.Contains(unit.UnitNumber)) ||
                                (!x.EffectiveSeason.HasValue && unit.AbsoluteNumber is { } abs && x.Parsed.AbsoluteEpisodeNumbers.Contains(abs)))
                    .Select(x => (x.Release, x.Parsed))
                    .ToList();
            }

            if (epCandidates.Count == 0)
            {
                continue;
            }

            var scoredEps = epCandidates
                .Select(x => (x.Release, x.Parsed, Score: ScoreReleaseCandidate(x.Release, x.Parsed, unitAudio, w.OriginalLanguage, unitProfile)))
                .Where(x => x.Score > -2000)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Release.SizeBytes)
                .ToList();

            if (scoredEps.Count == 0)
            {
                continue;
            }

            var bestEp = scoredEps[0];
            var dlKey = bestEp.Release.MagnetUrl ?? bestEp.Release.DownloadUrl;
            if (!grabbedUrls.Add(dlKey))
            {
                continue;
            }

            try
            {
                await orchestrator.GrabReleaseAsync(
                    new GrabReleaseRequest(
                        Title: bestEp.Release.Title,
                        Protocol: bestEp.Release.Protocol,
                        MediaVertical: w.WorkKind,
                        DownloadUrlOrMagnet: dlKey,
                        InfoHash: bestEp.Release.InfoHash,
                        WorkId: id,
                        ContainerId: containerId,
                        UnitIds: [unit.Id],
                        IndexerId: bestEp.Release.IndexerId,
                        ReleaseGuid: bestEp.Release.Guid,
                        SourceType: "auto_search",
                        IndexerName: bestEp.Release.IndexerName,
                        SizeBytes: bestEp.Release.SizeBytes,
                        PublishedAt: bestEp.Release.PublishedAt),
                    ct);
                grabbedTitles.Add(bestEp.Release.Title);
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        if (grabbedTitles.Count > 0)
        {
            var summary = grabbedTitles.Count == 1
                ? $"Grabbed '{grabbedTitles[0]}' for {container.Title}."
                : $"Grabbed {grabbedTitles.Count} episode release(s) for {container.Title} (first: '{grabbedTitles[0]}').";
            return new CatalogAutoSearchResultDto(
                WorkId: id,
                CandidatesFound: totalCandidates,
                Grabbed: true,
                Message: summary,
                SelectedRelease: grabbedTitles[0],
                ContainerId: containerId,
                SeasonNumber: seasonNum,
                GrabbedCount: grabbedTitles.Count);
        }

        return new CatalogAutoSearchResultDto(
            WorkId: id,
            CandidatesFound: totalCandidates,
            Grabbed: false,
            Message: lastError is not null
                ? $"Found candidate(s) for {container.Title}, but grab failed: {lastError}"
                : $"Searched configured indexers for '{w.Title}' — {container.Title} (S{seasonNum:D2}) — 0 matching releases found.",
            ContainerId: containerId,
            SeasonNumber: seasonNum,
            GrabbedCount: 0);
    }

    internal static async Task<CatalogAutoSearchResultDto?> ExecuteUnitAutoSearchAsync(
        long id,
        long unitId,
        PlexUserSummary? actor,
        IPolymorphicCatalogService catalog,
        IComposableProfileService profileService,
        IReleaseSearchOrchestrator searchOrchestrator,
        IUnifiedDownloadOrchestrator orchestrator,
        CancellationToken ct)
    {
        var tree = await catalog.GetWorkTreeAsync(id, actor, ct);
        if (tree is null)
        {
            return null;
        }

        MediaContainerDetail? parentContainer = null;
        MediaUnitDetail? targetUnit = null;
        foreach (var c in tree.Containers)
        {
            var found = c.Units.FirstOrDefault(u => u.Id == unitId);
            if (found is not null)
            {
                parentContainer = c;
                targetUnit = found;
                break;
            }
        }

        if (parentContainer is null || targetUnit is null)
        {
            return null;
        }

        var w = tree.Work;
        var unitProfile = await ResolveEffectiveProfileAsync(profileService, catalog, w, targetUnit.ProfileOverrideId, ct);
        var blocklist = await orchestrator.GetBlocklistSnapshotAsync(ct);
        var isEpisodic = w.WorkKind is MetadataVerticals.Tv or MetadataVerticals.Anime ||
                         string.Equals(targetUnit.UnitKind, "episode", StringComparison.OrdinalIgnoreCase);

        var searchVertical = targetUnit.UnitKind switch
        {
            "audiobook_slot" => MetadataVerticals.Audiobooks,
            "ebook_slot" => MetadataVerticals.Ebooks,
            _ => w.WorkKind,
        };

        var targetLabel = isEpisodic
            ? $"{w.Title} S{parentContainer.ContainerNumber:D2}E{targetUnit.UnitNumber:D2}"
            : $"{w.Title} ({targetUnit.Title})";

        var scopedCandidates = await searchOrchestrator.SearchScopedAsync(
            new OrchestratedReleaseSearchRequest(
                Query: new IndexerSearchQuery(
                    Query: w.Title,
                    MediaVertical: searchVertical,
                    Season: isEpisodic ? parentContainer.ContainerNumber : null,
                    Episode: isEpisodic ? targetUnit.UnitNumber : null,
                    Interactive: false,
                    WorkId: id,
                    UnitId: targetUnit.Id),
                WorkTree: tree,
                ContainerId: parentContainer.Id,
                UnitId: targetUnit.Id,
                AbsoluteEpisodeNumber: targetUnit.AbsoluteNumber,
                IncludeSeasonPacks: true,
                Actor: actor),
            ct);

        var reqAudio = targetUnit.RequiredAudioLangs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seasonNum = parentContainer.ContainerNumber;
        var scored = scopedCandidates
            .Where(r => !blocklist.IsBlocked(r.Release))
            .Where(x => !isEpisodic ||
                        (!x.IsSeasonPack && x.EffectiveSeason == seasonNum && x.EffectiveEpisodes.Contains(targetUnit.UnitNumber)) ||
                        (!x.EffectiveSeason.HasValue && targetUnit.AbsoluteNumber is { } abs && x.Parsed.AbsoluteEpisodeNumbers.Contains(abs)) ||
                        (x.IsSeasonPack && x.EffectiveSeason == seasonNum))
            .Select(x =>
            {
                var score = ScoreReleaseCandidate(x.Release, x.Parsed, reqAudio, w.OriginalLanguage, unitProfile);
                if (isEpisodic)
                {
                    var exactEp = (!x.IsSeasonPack && x.EffectiveSeason == seasonNum && x.EffectiveEpisodes.Contains(targetUnit.UnitNumber)) ||
                                  (!x.EffectiveSeason.HasValue && targetUnit.AbsoluteNumber is { } abs && x.Parsed.AbsoluteEpisodeNumbers.Contains(abs));
                    if (exactEp)
                    {
                        score += 350;
                    }
                    else if (x.IsSeasonPack && x.EffectiveSeason == seasonNum)
                    {
                        score += 50;
                    }
                }

                return (x.Release, x.Parsed, Score: score);
            })
            .Where(x => x.Score > -2000)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Release.SizeBytes)
            .ToList();

        if (scored.Count == 0)
        {
            return new CatalogAutoSearchResultDto(
                WorkId: id,
                CandidatesFound: scopedCandidates.Count,
                Grabbed: false,
                Message: $"Searched configured indexers for '{targetLabel}' — 0 non-blocklisted matching releases found.",
                ContainerId: parentContainer.Id,
                UnitId: targetUnit.Id);
        }

        var best = scored[0];
        try
        {
            var outcome = await orchestrator.GrabReleaseAsync(
                new GrabReleaseRequest(
                    Title: best.Release.Title,
                    Protocol: best.Release.Protocol,
                    MediaVertical: searchVertical,
                    DownloadUrlOrMagnet: best.Release.MagnetUrl ?? best.Release.DownloadUrl,
                    InfoHash: best.Release.InfoHash,
                    WorkId: id,
                    ContainerId: parentContainer.Id,
                    UnitIds: [targetUnit.Id],
                    IndexerId: best.Release.IndexerId,
                    ReleaseGuid: best.Release.Guid,
                    SourceType: "auto_search",
                    IndexerName: best.Release.IndexerName,
                    SizeBytes: best.Release.SizeBytes,
                    PublishedAt: best.Release.PublishedAt),
                ct);

            return new CatalogAutoSearchResultDto(
                WorkId: id,
                CandidatesFound: scored.Count,
                Grabbed: true,
                Message: $"Grabbed '{best.Release.Title}' for {targetLabel} via {outcome.ClientName} (category: {outcome.ResolvedCategory}).",
                SelectedRelease: best.Release.Title,
                ContainerId: parentContainer.Id,
                UnitId: targetUnit.Id,
                GrabOutcome: outcome);
        }
        catch (Exception ex)
        {
            return new CatalogAutoSearchResultDto(
                WorkId: id,
                CandidatesFound: scored.Count,
                Grabbed: false,
                Message: $"Found {scored.Count} release(s) for {targetLabel} (best: '{best.Release.Title}'), but grab failed: {ex.Message}",
                SelectedRelease: best.Release.Title,
                ContainerId: parentContainer.Id,
                UnitId: targetUnit.Id);
        }
    }
}
