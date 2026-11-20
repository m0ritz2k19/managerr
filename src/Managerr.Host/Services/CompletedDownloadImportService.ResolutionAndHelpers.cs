using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Managerr.Catalog;
using Managerr.Common.Net;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
using Managerr.Downloaders.Organizer;
using Managerr.Downloaders.Parser;
using Managerr.Host.Hubs;
using Managerr.Indexers;
using Managerr.Indexers.Torznab;
using Managerr.Migration;
using Managerr.Profiles;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Verification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Managerr.Host.Services;

public sealed partial class CompletedDownloadImportService
{
    private static MediaWorkSummary? ResolveCorrelatedOrTitleMatchedWork(
        IReadOnlyList<MediaWorkSummary> works,
        LibrarySummary? preferredLibrary,
        ParsedReleaseInfo parsed,
        DownloadGrabHistoryEntry? grabHistory,
        out string? grabMismatchReason,
        string? rawItemTitle = null,
        string? outputPath = null,
        IReleaseParserRouter? parser = null)
    {
        grabMismatchReason = null;

        if (grabHistory?.WorkId is { } gwId)
        {
            var byGrab = works.FirstOrDefault(w => w.Id == gwId);
            if (byGrab is not null)
            {
                if (!string.IsNullOrWhiteSpace(parsed.CleanTitle) &&
                    !MatchesWorkTitles(parsed.CleanTitle, parsed.Year, byGrab, parsed) &&
                    !IsAuthenticGrabCorrelation(rawItemTitle, outputPath, parsed, grabHistory, byGrab, parser))
                {
                    grabMismatchReason = $"Download correlated to '{byGrab.Title}' via GrabHistory, but release parsed as '{parsed.CleanTitle}'. Confirm via Manual/Retry Import.";
                    return byGrab;
                }

                return byGrab;
            }
        }

        // First search within preferred library for vertical, then across matching vertical works,
        // and if parsed is tv or anime, also include anime or tv works
        IEnumerable<MediaWorkSummary> candidateWorks;
        if (preferredLibrary is not null)
        {
            candidateWorks = works.Where(w => w.LibraryId == preferredLibrary.Id)
                .Concat(works.Where(w => w.LibraryId != preferredLibrary.Id && string.Equals(w.WorkKind, parsed.MediaVertical, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            candidateWorks = works.Where(w => string.Equals(w.WorkKind, parsed.MediaVertical, StringComparison.OrdinalIgnoreCase));
        }

        if (parsed.MediaVertical is "tv" or "anime")
        {
            candidateWorks = candidateWorks.Concat(works.Where(w => w.WorkKind is "tv" or "anime"));
        }

        candidateWorks = candidateWorks.Concat(works);
        var scopedWorks = candidateWorks.DistinctBy(w => w.Id);

        foreach (var w in scopedWorks)
        {
            if (MatchesWorkTitles(parsed.CleanTitle, parsed.Year, w, parsed))
            {
                return w;
            }
        }

        return null;
    }

    private static bool IsAuthenticGrabCorrelation(
        string? rawItemTitle,
        string? outputPath,
        ParsedReleaseInfo parsed,
        DownloadGrabHistoryEntry grabHistory,
        MediaWorkSummary byGrab,
        IReleaseParserRouter? parser)
    {
        if (string.IsNullOrWhiteSpace(grabHistory.ReleaseTitle))
        {
            return false;
        }

        if (string.Equals(byGrab.WorkKind, "movies", StringComparison.OrdinalIgnoreCase) &&
            parsed.Year.HasValue &&
            byGrab.Year.HasValue &&
            Math.Abs(parsed.Year.Value - byGrab.Year.Value) > 1)
        {
            return false;
        }

        var normGrab = ReleaseBlocklistSnapshot.NormalizeTitleKey(grabHistory.ReleaseTitle);
        if (normGrab.Length > 0)
        {
            var folderBase = !string.IsNullOrWhiteSpace(outputPath)
                ? Path.GetFileName(outputPath.TrimEnd('/', '\\'))
                : null;

            foreach (var candidate in new[] { rawItemTitle, folderBase })
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var cleaned = StripTrailingCollisionSuffix(candidate.Trim());
                var normCand = ReleaseBlocklistSnapshot.NormalizeTitleKey(cleaned);
                if (normCand.Length == 0)
                {
                    continue;
                }

                if (string.Equals(normCand, normGrab, StringComparison.OrdinalIgnoreCase) ||
                    (normGrab.Length >= 6 && (normCand.StartsWith(normGrab + " ", StringComparison.OrdinalIgnoreCase) ||
                                              normCand.Contains(" " + normGrab + " ", StringComparison.OrdinalIgnoreCase))) ||
                    (normCand.Length >= 6 && (normGrab.StartsWith(normCand + " ", StringComparison.OrdinalIgnoreCase) ||
                                              normGrab.Contains(" " + normCand + " ", StringComparison.OrdinalIgnoreCase))))
                {
                    return true;
                }
            }
        }

        if (parser is not null)
        {
            var grabParsed = parser.Parse(grabHistory.ReleaseTitle, parsed.MediaVertical);
            if (!string.IsNullOrWhiteSpace(grabParsed.CleanTitle) &&
                ReleaseTitleNormalizer.TitlesMatch(parsed.CleanTitle, grabParsed.CleanTitle, grabParsed.Year ?? byGrab.Year))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripTrailingCollisionSuffix(string value)
    {
        var match = TrailingNumericCollisionSuffixRegex().Match(value);
        if (!match.Success)
        {
            return value;
        }

        var digitsLen = match.Length - 1;
        return digitsLen <= 2 ? value[..match.Index] : value;
    }

    internal static IReadOnlyList<string> GetKnownWorkTitles(string title, string? sortTitle, IReadOnlyList<string> altTitles)
    {
        var list = new List<string>();

        void AddWithStem(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var trimmed = raw.Trim();
            if (!list.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(trimmed);
            }

            string[] delims = [" – ", " — ", " - ", ": "];
            int delimIdx = -1;
            foreach (var d in delims)
            {
                var idx = trimmed.IndexOf(d, StringComparison.Ordinal);
                if (idx >= 4 && (delimIdx < 0 || idx < delimIdx))
                {
                    delimIdx = idx;
                }
            }
            if (delimIdx >= 4)
            {
                var stem = trimmed[..delimIdx].Trim();
                if (stem.Length >= 4 && !list.Contains(stem, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(stem);
                }
            }
        }

        AddWithStem(title);
        AddWithStem(sortTitle);
        foreach (var alt in altTitles)
        {
            AddWithStem(alt);
        }

        return list;
    }

    internal static IReadOnlyList<string> GetKnownWorkTitles(MediaWorkSummary work) =>
        GetKnownWorkTitles(work.Title, work.SortTitle, work.AltTitles);

    private static bool MatchesWorkTitles(string candidateTitle, int? candidateYear, MediaWorkSummary work, ParsedReleaseInfo? parsed = null)
    {
        if (MatchesWorkTitlesCore(candidateTitle, candidateYear, work))
        {
            return true;
        }

        // Also match when release title carries a trailing 'Specials' / 'Special' / 'OVA' / 'OAD' suffix
        var stripped = TrailingSpecialsSuffixRegex().Replace(candidateTitle.Trim(), "").Trim();
        if (stripped.Length > 0 &&
            !string.Equals(stripped, candidateTitle, StringComparison.Ordinal) &&
            MatchesWorkTitlesCore(stripped, candidateYear, work))
        {
            return true;
        }

        if (work.WorkKind is "tv" or "anime")
        {
            var p = (parsed ?? new ParsedReleaseInfo(
                RawTitle: candidateTitle,
                MediaVertical: work.WorkKind,
                CleanTitle: candidateTitle,
                ArtistOrAuthor: null,
                Year: candidateYear,
                SeasonNumber: null,
                EpisodeNumbers: [],
                AbsoluteEpisodeNumbers: [],
                IsFullSeasonPack: false,
                MovieEdition: null,
                Resolution: null,
                Source: null,
                VideoCodec: null,
                HdrFormat: null,
                AudioCodec: null,
                MusicFormat: null,
                BookFormat: null,
                DetectedLanguages: [],
                IsDualAudio: false,
                IsMultiSubs: false,
                ReleaseGroup: null)) with { SeasonNumber = null };

            if (ReleaseSearchOrchestrator.TryInferSequelSeason(p, GetKnownWorkTitles(work), out _))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesWorkTitlesCore(string candidateTitle, int? candidateYear, MediaWorkSummary work)
    {
        var isEpisodic = work.WorkKind is "tv" or "anime";
        var yearMatches = isEpisodic || !candidateYear.HasValue || !work.Year.HasValue || Math.Abs(candidateYear.Value - work.Year.Value) <= 1;

        if (!yearMatches)
        {
            return false;
        }

        foreach (var known in GetKnownWorkTitles(work))
        {
            if (ReleaseTitleNormalizer.TitlesMatch(candidateTitle, known, work.Year))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractSpecialEpisodeNumber(
        string fileStem,
        string rawJobTitle,
        IReadOnlyList<int> parsedAbs,
        out int specialEp)
    {
        specialEp = 1;
        foreach (var src in new[] { fileStem, rawJobTitle })
        {
            if (string.IsNullOrWhiteSpace(src))
            {
                continue;
            }

            // Explicit 'OVA 01', 'Special 02', 'Specials - 01', 'OAD 1', 'SP01', 'S00E01'
            var m = SpecialEpisodeNumberRegex().Match(src);
            if (m.Success && int.TryParse(m.Groups["ep"].Value, out var ep) && ep > 0)
            {
                specialEp = ep;
                return true;
            }

            if (SpecialKeywordTokenRegex().IsMatch(src))
            {
                specialEp = parsedAbs.FirstOrDefault(1);
                return true;
            }
        }

        return false;
    }

    private async Task<MediaWorkDetailTree> EnsureSpecialsContainerAndUnitsAsync(
        MediaWorkDetailTree workTree,
        IReadOnlyList<int> episodeNumbers,
        CancellationToken ct)
    {
        var specialsContainer = workTree.Containers.FirstOrDefault(c => c.ContainerNumber == 0);
        var missingEpNums = episodeNumbers
            .Where(ep => ep > 0 && (specialsContainer is null || specialsContainer.Units.All(u => u.UnitNumber != ep)))
            .Distinct()
            .OrderBy(ep => ep)
            .ToList();

        if (specialsContainer is not null && missingEpNums.Count == 0)
        {
            return workTree;
        }

        var defaultAudio = workTree.Containers
            .SelectMany(c => c.Units)
            .Select(u => u.RequiredAudioLangs)
            .FirstOrDefault(a => a.Count > 0)
            ?? [];
        var defaultSubs = workTree.Containers
            .SelectMany(c => c.Units)
            .Select(u => u.RequiredSubLangs)
            .FirstOrDefault(s => s.Count > 0)
            ?? [];
        var defaultProfileOverride = workTree.Containers
            .SelectMany(c => c.Units)
            .Select(u => u.ProfileOverrideId)
            .FirstOrDefault(p => p.HasValue);

        var now = DateTimeOffset.UtcNow.ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                var containerId = specialsContainer?.Id ?? await conn.ExecuteScalarAsync<long?>(
                    "SELECT id FROM media_containers WHERE work_id = @WorkId AND container_number = 0 LIMIT 1;",
                    new { WorkId = workTree.Work.Id },
                    transaction: tx) ?? await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO media_containers (
                        work_id, container_kind, container_number, title,
                        release_date, monitored, external_ids_json, created_at
                    ) VALUES (
                        @WorkId, 'season', 0, 'Specials',
                        NULL, 1, '{}', @Now
                    );
                    SELECT last_insert_rowid();
                    """,
                    new { WorkId = workTree.Work.Id, Now = now },
                    transaction: tx);

                foreach (var epNum in missingEpNums)
                {
                    var exists = await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(1) FROM media_units WHERE container_id = @Cid AND unit_number = @Ep;",
                        new { Cid = containerId, Ep = epNum },
                        transaction: tx);
                    if (exists > 0)
                    {
                        continue;
                    }

                    await conn.ExecuteAsync(
                        """
                        INSERT INTO media_units (
                            work_id, container_id, unit_kind, unit_number, absolute_number,
                            title, air_or_release_date, monitored, profile_override_id,
                            required_audio_langs_json, required_sub_langs_json,
                            quality_cutoff_met, language_cutoff_met, created_at
                        ) VALUES (
                            @WorkId, @ContainerId, 'episode', @Num, NULL,
                            @Title, NULL, 1, @ProfileOverrideId,
                            @AudioJson, @SubJson,
                            0, 0, @Now
                        );
                        """,
                        new
                        {
                            WorkId = workTree.Work.Id,
                            ContainerId = containerId,
                            Num = epNum,
                            Title = $"Special {epNum}",
                            ProfileOverrideId = defaultProfileOverride,
                            AudioJson = JsonSerializer.Serialize(defaultAudio, JsonOpts),
                            SubJson = JsonSerializer.Serialize(defaultSubs, JsonOpts),
                            Now = now,
                        },
                        transaction: tx);
                }
            },
            ct);

        return (await catalogService.GetWorkTreeAsync(workTree.Work.Id, null, ct))!;
    }

    private static (IReadOnlyList<MediaUnitDetail> Units, string? Error) ResolveTargetUnitsStrict(
        MediaWorkDetailTree tree,
        ParsedReleaseInfo parsed,
        DownloadGrabHistoryEntry? grabHistory,
        ManualOverrideImportRequest? manualOverride,
        bool isSingleFileJob)
    {
        var isEpisodic = tree.Work.WorkKind is "tv" or "anime";
        if (!isEpisodic)
        {
            var single = tree.Containers.SelectMany(c => c.Units).FirstOrDefault();
            return single is not null ? ([single], null) : ([], $"Work '{tree.Work.Title}' has no media unit slot.");
        }

        // 1. Explicit season + episode numbers (never fall back to S01E01!)
        if (parsed.SeasonNumber.HasValue && parsed.EpisodeNumbers.Count > 0)
        {
            var season = tree.Containers.FirstOrDefault(c => c.ContainerNumber == parsed.SeasonNumber.Value);
            if (season is null)
            {
                return ([], $"Season {parsed.SeasonNumber.Value} does not exist in '{tree.Work.Title}'. Refresh metadata or use Manual Import.");
            }

            var matched = new List<MediaUnitDetail>();
            foreach (var epNum in parsed.EpisodeNumbers)
            {
                var unit = season.Units.FirstOrDefault(u => u.UnitNumber == epNum);
                if (unit is null &&
                    string.Equals(tree.Work.WorkKind, "anime", StringComparison.OrdinalIgnoreCase) &&
                    parsed.SeasonNumber == 1 &&
                    epNum > season.Units.Count)
                {
                    unit = tree.Containers
                        .Where(c => c.ContainerNumber > 0)
                        .SelectMany(c => c.Units)
                        .FirstOrDefault(u => u.AbsoluteNumber == epNum);
                }

                if (unit is null)
                {
                    return ([], $"Episode S{parsed.SeasonNumber.Value:D2}E{epNum:D2} was not found in '{tree.Work.Title}' ({season.Title}). Refresh series metadata or select episode via Manual Import.");
                }

                matched.Add(unit);
            }

            return (matched, null);
        }

        // 2. Absolute episode numbering (Anime / Absolute order)
        if (parsed.AbsoluteEpisodeNumbers.Count > 0)
        {
            var allUnits = tree.Containers.SelectMany(c => c.Units).ToList();
            var matched = new List<MediaUnitDetail>();
            foreach (var abs in parsed.AbsoluteEpisodeNumbers)
            {
                var unit = allUnits.FirstOrDefault(u => u.AbsoluteNumber == abs)
                           ?? (tree.Containers.Count == 1 ? allUnits.FirstOrDefault(u => u.UnitNumber == abs) : null);
                if (unit is null)
                {
                    return ([], $"Absolute episode #{abs} was not found in '{tree.Work.Title}'.");
                }

                matched.Add(unit);
            }

            return (matched, null);
        }

        // 3. Correlated single-unit grab history fallback
        if (isSingleFileJob && grabHistory?.UnitIds is { Count: > 0 } grabbedIds)
        {
            var allUnits = tree.Containers.SelectMany(c => c.Units).ToDictionary(u => u.Id);
            var matched = grabbedIds.Where(allUnits.ContainsKey).Select(id => allUnits[id]).ToList();
            if (matched.Count > 0)
            {
                return (matched, null);
            }
        }

        return ([], $"Could not determine Season/Episode number from '{parsed.RawTitle}' for '{tree.Work.Title}'; use Manual/Retry Import.");
    }

    private async Task<(bool QualityMet, bool LanguageMet)> EvaluateImportedCutoffsAsync(
        MediaUnitDetail unit,
        long? profileId,
        string originalLanguage,
        string releaseTitle,
        IReadOnlyList<string> verifiedAudio,
        IReadOnlyList<string> verifiedSubs,
        long? languagePolicyId,
        LanguagePolicySummary? effectiveLanguagePolicy,
        CancellationToken ct)
    {
        var qualMet = true;
        var langMet = false;

        if (profileService is not null)
        {
            var policyToUse = effectiveLanguagePolicy;
            if (policyToUse is null && languagePolicyId.HasValue)
            {
                try
                {
                    var allPolicies = await profileService.ListLanguagePoliciesAsync(ct);
                    policyToUse = allPolicies.FirstOrDefault(p => p.Id == languagePolicyId.Value);
                }
                catch
                {
                    // Fall back to profile evaluation
                }
            }

            if (policyToUse is not null)
            {
                try
                {
                    var eval = profileService.EvaluateOnDiskLanguagesAgainstPolicy(policyToUse, originalLanguage, verifiedAudio, verifiedSubs);
                    langMet = eval.SatisfiesCutoff;

                    if (profileId.HasValue)
                    {
                        var card = await profileService.SimulateAsync(
                            new SimulateReleaseRequest(
                                ProfileId: profileId.Value,
                                ReleaseTitle: releaseTitle,
                                UnitId: unit.Id,
                                OriginalLanguage: originalLanguage,
                                ExplicitAudioLanguages: verifiedAudio,
                                ExplicitSubtitleLanguages: verifiedSubs),
                            ct);
                        qualMet = card.CandidateSatisfiesQualityCutoff;
                    }
                    else
                    {
                        qualMet = true;
                    }
                }
                catch
                {
                    // Fall back to profile evaluation
                }
            }
            else if (profileId.HasValue)
            {
                try
                {
                    var card = await profileService.SimulateAsync(
                        new SimulateReleaseRequest(
                            ProfileId: profileId.Value,
                            ReleaseTitle: releaseTitle,
                            UnitId: unit.Id,
                            OriginalLanguage: originalLanguage,
                            ExplicitAudioLanguages: verifiedAudio,
                            ExplicitSubtitleLanguages: verifiedSubs),
                        ct);
                    qualMet = card.CandidateSatisfiesQualityCutoff;
                    langMet = card.CandidateSatisfiesLanguageCutoff;
                }
                catch
                {
                    // Fall back to direct audio check below
                }
            }
        }

        var origIso = LanguageCodeNormalizer.NormalizeToIso2(originalLanguage);
        var unitReqs = unit.RequiredAudioLangs
            .Select(r => string.Equals(r, "original", StringComparison.OrdinalIgnoreCase) ? origIso : LanguageCodeNormalizer.NormalizeToIso2(r))
            .Where(r => r != "und")
            .ToList();

        if (profileService is null || (!languagePolicyId.HasValue && !profileId.HasValue && effectiveLanguagePolicy is null))
        {
            langMet = unitReqs.Count == 0 || unitReqs.All(req => verifiedAudio.Contains(req, StringComparer.OrdinalIgnoreCase));
        }

        if (!langMet)
        {
            var primaryWanted = ExtractPrimaryWantedAudioLanguages(effectiveLanguagePolicy, origIso);
            if (primaryWanted.Count > 0 &&
                primaryWanted.All(pw => verifiedAudio.Contains(pw, StringComparer.OrdinalIgnoreCase)) &&
                (unitReqs.Count == 0 || unitReqs.All(ur => verifiedAudio.Contains(ur, StringComparer.OrdinalIgnoreCase))))
            {
                langMet = true;
            }
        }

        return (qualMet, langMet);
    }

    private async Task<ProcessedDownloadItemOutcome> HandleTerminalDownloadFailureAsync(
        ActiveDownloadQueueItem item,
        DownloadClientSummary? clientCfg,
        DownloadGrabHistoryEntry? grabHistory,
        IReadOnlyList<MediaWorkSummary> works,
        IReadOnlyList<LibrarySummary> libraries,
        string failMsg,
        string ledgerVerdict,
        bool forceRemoveFromClient,
        long? explicitWorkId,
        string? explicitWorkTitle,
        IReadOnlyList<long>? explicitUnitIds,
        string sourceFilePath,
        CancellationToken ct)
    {
        var resolvedWorkId = explicitWorkId ?? grabHistory?.WorkId;
        var resolvedWorkTitle = explicitWorkTitle ?? works.FirstOrDefault(w => w.Id == resolvedWorkId)?.Title;
        var resolvedUnitIds = explicitUnitIds ?? grabHistory?.UnitIds ?? [];
        var resolvedReleaseTitle = grabHistory?.ReleaseTitle ?? item.Title;
        var resolvedVertical = grabHistory?.MediaVertical ?? item.MediaVertical;

        await UpsertTrackedDownloadAsync(
            item,
            resolvedWorkId,
            resolvedWorkTitle,
            resolvedUnitIds,
            TrackedDownloadStates.FailedPending,
            "error",
            [failMsg],
            null,
            ct);

        var isTransientClientFault =
            string.Equals(ledgerVerdict, "ClientFailed", StringComparison.OrdinalIgnoreCase) &&
            WantedAndActivityService.IsTransientDownloaderClientFault(failMsg);

        if (!isTransientClientFault)
        {
            await downloadOrchestrator.BlocklistFailedReleaseAsync(
                item.InfoHash ?? grabHistory?.InfoHash,
                grabHistory?.ReleaseGuid,
                resolvedReleaseTitle,
                failMsg,
                ct,
                new FailedReleaseBlocklistMetadata(
                    Protocol: item.Protocol,
                    DownloadUrl: grabHistory?.DownloadUrl,
                    IndexerId: grabHistory?.IndexerId,
                    IndexerName: grabHistory?.IndexerName,
                    SizeBytes: grabHistory?.SizeBytes ?? (item.TotalBytes > 0 ? item.TotalBytes : null),
                    PublishedAt: grabHistory?.PublishedAt,
                    WorkId: resolvedWorkId));
        }

        // A client that refuses the removal (403, timeout, offline) must not stop the failure handling: without the
        // ledger row and the Failed state below, every poll would repeat the blocklist + removal and never search again.
        string? removalNote = null;
        if (forceRemoveFromClient || clientCfg?.RemoveFailed == true)
        {
            try
            {
                await downloadOrchestrator.RemoveDownloadFromClientAsync(item.ClientId, item.ExternalId, deleteData: true, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                removalNote = $"Could not remove the download from {item.ClientName}: {ex.GetType().Name}: {ex.Message}";
                logger?.LogWarning(ex, "Removing failed download {ExternalId} ({Title}) from {Client} failed; continuing failure handling.",
                    item.ExternalId, item.Title, item.ClientName);
            }

            if (item.CanMoveFiles)
            {
                try
                {
                    TryDeleteImportedUsenetJobFolder(item, libraries);
                }
                catch
                {
                    // Best effort
                }
            }
        }

        string? redownloadNote = null;
        if (wantedService is not null)
        {
            redownloadNote = await wantedService.RedownloadFailedWorkOrUnitsAsync(
                resolvedWorkId,
                resolvedUnitIds,
                resolvedReleaseTitle,
                resolvedVertical,
                ct);
        }
        else
        {
            redownloadNote = await TryRedownloadFailedWorkAsync(grabHistory, works, libraries, ct);
        }

        var finalMsgs = new[] { failMsg, removalNote, redownloadNote }
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m!)
            .ToArray();
        var combinedMsg = string.Join(' ', finalMsgs);

        await RecordImportLedgerAsync(item.ClientId, item.ExternalId, resolvedReleaseTitle, ledgerVerdict, null, false, combinedMsg, ct);
        await UpsertTrackedDownloadAsync(
            item,
            resolvedWorkId,
            resolvedWorkTitle,
            resolvedUnitIds,
            TrackedDownloadStates.Failed,
            "error",
            finalMsgs,
            null,
            ct);

        if (notificationSink is not null)
        {
            try
            {
                await notificationSink.PublishAsync(
                    new Managerr.Common.Notifications.MediaLifecycleNotificationEvent(
                        EventType: Managerr.Common.Notifications.MediaNotificationEventTypes.OnHealthIssue,
                        Title: $"Download Failed — {resolvedReleaseTitle}",
                        Message: combinedMsg,
                        WorkId: resolvedWorkId,
                        MediaVertical: resolvedVertical,
                        ReleaseTitle: resolvedReleaseTitle),
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The failure is already recorded (ledger + Failed state); a broken notifier must not undo that
                logger?.LogWarning(ex, "Failed-download notification for {Title} could not be sent.", resolvedReleaseTitle);
            }
        }

        return new ProcessedDownloadItemOutcome(
            ClientId: item.ClientId,
            ClientName: item.ClientName,
            ExternalDownloadId: item.ExternalId,
            Title: item.Title,
            MediaVertical: resolvedVertical,
            Verdict: ledgerVerdict,
            SourceFilePath: sourceFilePath,
            DestinationFilePath: null,
            Hardlinked: false,
            QuarantineId: null,
            Message: combinedMsg);
    }

    private async Task<string?> TryRedownloadFailedWorkAsync(
        DownloadGrabHistoryEntry? grabHistory,
        IReadOnlyList<MediaWorkSummary> works,
        IReadOnlyList<LibrarySummary> libraries,
        CancellationToken ct)
    {
        if (indexerService is null || grabHistory?.WorkId is not { } workId)
        {
            return null;
        }

        var work = works.FirstOrDefault(w => w.Id == workId);
        if (work is null || !work.Monitored)
        {
            return null;
        }

        try
        {
            var tree = await catalogService.GetWorkTreeAsync(work.Id, null, ct);
            if (tree is null) return null;

            int? season = null;
            int? episode = null;
            if (grabHistory.UnitIds.Count == 1)
            {
                foreach (var c in tree.Containers)
                {
                    var u = c.Units.FirstOrDefault(x => x.Id == grabHistory.UnitIds[0]);
                    if (u is not null)
                    {
                        season = c.ContainerNumber;
                        episode = u.UnitNumber;
                        break;
                    }
                }
            }

            var blocklist = await downloadOrchestrator.GetBlocklistSnapshotAsync(ct);
            var candidates = await indexerService.SearchAsync(
                new IndexerSearchQuery(
                    Query: work.Title,
                    MediaVertical: work.WorkKind,
                    Season: season,
                    Episode: episode,
                    Interactive: false),
                ct);

            var isEpisodic = work.WorkKind is "tv" or "anime" or "series";
            foreach (var cand in candidates)
            {
                if (blocklist.IsBlocked(cand))
                {
                    continue;
                }

                if (isEpisodic)
                {
                    var parsed = parserRouter.Parse(cand.Title, work.WorkKind);
                    if (!ReleaseTitleNormalizer.MatchesWork(parsed.CleanTitle, work.Title, work.AltTitles, work.Year))
                    {
                        continue;
                    }

                    if (season.HasValue && episode.HasValue &&
                        !(parsed.SeasonNumber == season && parsed.EpisodeNumbers.Contains(episode.Value)))
                    {
                        continue;
                    }
                }

                if (string.Equals(
                    ReleaseBlocklistSnapshot.NormalizeTitleKey(cand.Title),
                    ReleaseBlocklistSnapshot.NormalizeTitleKey(grabHistory.ReleaseTitle),
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var grabbed = await downloadOrchestrator.GrabReleaseAsync(
                    new GrabReleaseRequest(
                        Title: cand.Title,
                        Protocol: cand.Protocol,
                        MediaVertical: work.WorkKind,
                        DownloadUrlOrMagnet: cand.MagnetUrl ?? cand.DownloadUrl,
                        InfoHash: cand.InfoHash,
                        WorkId: work.Id,
                        ContainerId: grabHistory.ContainerId,
                        UnitIds: grabHistory.UnitIds,
                        IndexerId: cand.IndexerId,
                        ReleaseGuid: cand.Guid,
                        SourceType: "redownload_failed",
                        IndexerName: cand.IndexerName,
                        SizeBytes: cand.SizeBytes,
                        PublishedAt: cand.PublishedAt),
                    ct);
                return $"Auto-grabbed replacement release '{cand.Title}' via {grabbed.ClientName}.";
            }
        }
        catch
        {
            // Best-effort redownload
        }

        return null;
    }

    private async Task UpsertTrackedDownloadAsync(
        ActiveDownloadQueueItem item,
        long? workId,
        string? workTitle,
        IReadOnlyList<long> unitIds,
        string state,
        string trackedStatus,
        IReadOnlyList<string> statusMessages,
        long? quarantineId,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                INSERT INTO tracked_downloads (
                    download_client_id, client_name, external_download_id, protocol, info_hash,
                    title, media_vertical, work_id, work_title, unit_ids_json,
                    state, tracked_status, status_messages_json, output_path, quarantine_id,
                    can_move_files, first_seen_at, updated_at
                ) VALUES (
                    @ClientId, @ClientName, @ExtId, @Proto, @Hash,
                    @Title, @Vert, @WorkId, @WorkTitle, @UnitIdsJson,
                    @State, @TrackedStatus, @MsgsJson, @OutPath, @QuarantineId,
                    @CanMove, @Now, @Now
                )
                ON CONFLICT(download_client_id, external_download_id) DO UPDATE SET
                    client_name = excluded.client_name,
                    info_hash = COALESCE(excluded.info_hash, tracked_downloads.info_hash),
                    title = excluded.title,
                    media_vertical = excluded.media_vertical,
                    work_id = COALESCE(excluded.work_id, tracked_downloads.work_id),
                    work_title = COALESCE(excluded.work_title, tracked_downloads.work_title),
                    unit_ids_json = CASE WHEN excluded.unit_ids_json != '[]' THEN excluded.unit_ids_json ELSE tracked_downloads.unit_ids_json END,
                    state = excluded.state,
                    tracked_status = excluded.tracked_status,
                    status_messages_json = excluded.status_messages_json,
                    output_path = CASE WHEN excluded.output_path != '' THEN excluded.output_path ELSE tracked_downloads.output_path END,
                    quarantine_id = excluded.quarantine_id,
                    can_move_files = excluded.can_move_files,
                    updated_at = excluded.updated_at;
                """,
                new
                {
                    item.ClientId,
                    item.ClientName,
                    ExtId = item.ExternalId,
                    Proto = item.Protocol,
                    Hash = item.InfoHash,
                    item.Title,
                    Vert = item.MediaVertical,
                    WorkId = workId,
                    WorkTitle = workTitle,
                    UnitIdsJson = JsonSerializer.Serialize(unitIds, JsonOpts),
                    State = state,
                    TrackedStatus = trackedStatus,
                    MsgsJson = JsonSerializer.Serialize(statusMessages, JsonOpts),
                    OutPath = item.OutputPath,
                    QuarantineId = quarantineId,
                    CanMove = item.CanMoveFiles ? 1 : 0,
                    Now = now,
                },
                transaction: tx),
            ct);
    }

    private async Task RecordImportLedgerAsync(
        long clientId,
        string extId,
        string title,
        string verdict,
        string? destPath,
        bool hardlinked,
        string message,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var historyEventType = verdict switch
        {
            VerificationVerdicts.VerifiedPass
                or VerificationVerdicts.VerifiedPassAutoTagged
                or VerificationVerdicts.MusicPolicyPass => "DownloadFolderImported",
            "ClientFailed"
                or "BlockedMalware"
                or VerificationVerdicts.VerifiedFail => "DownloadFailed",
            _ => null,
        };

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    """
                    INSERT OR REPLACE INTO processed_download_imports (
                        download_client_id, external_download_id, title, verdict, destination_path, hardlinked, message, processed_at
                    ) VALUES (
                        @ClientId, @ExtId, @Title, @Verdict, @Dest, @Hl, @Msg, @Now
                    );
                    """,
                    new
                    {
                        ClientId = clientId,
                        ExtId = extId,
                        Title = title,
                        Verdict = verdict,
                        Dest = destPath,
                        Hl = hardlinked ? 1 : 0,
                        Msg = message,
                        Now = now,
                    },
                    transaction: tx);

                if (historyEventType is not null)
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO media_history (
                            occurred_at, event_type, media_vertical, work_id, work_title,
                            container_id, unit_id, episode_code, source_title, quality_name,
                            languages_json, indexer_name, download_client_id, download_client_name,
                            external_download_id, info_hash, destination_path, data_json
                        ) VALUES (
                            @Now,
                            @EventType,
                            COALESCE(
                                (SELECT media_vertical FROM tracked_downloads WHERE download_client_id = @ClientId AND external_download_id = @ExtId LIMIT 1),
                                (SELECT media_vertical FROM download_grab_history WHERE download_client_id = @ClientId AND external_download_id = @ExtId ORDER BY id DESC LIMIT 1),
                                'movies'
                            ),
                            COALESCE(
                                (SELECT work_id FROM tracked_downloads WHERE download_client_id = @ClientId AND external_download_id = @ExtId LIMIT 1),
                                (SELECT work_id FROM download_grab_history WHERE download_client_id = @ClientId AND external_download_id = @ExtId ORDER BY id DESC LIMIT 1)
                            ),
                            COALESCE(
                                (SELECT work_title FROM tracked_downloads WHERE download_client_id = @ClientId AND external_download_id = @ExtId LIMIT 1),
                                (SELECT mw.title FROM media_works mw WHERE mw.id = (SELECT work_id FROM download_grab_history WHERE download_client_id = @ClientId AND external_download_id = @ExtId ORDER BY id DESC LIMIT 1))
                            ),
                            (SELECT container_id FROM download_grab_history WHERE download_client_id = @ClientId AND external_download_id = @ExtId ORDER BY id DESC LIMIT 1),
                            NULL,
                            NULL,
                            @Title,
                            NULL,
                            '[]',
                            (SELECT i.name FROM indexers i WHERE i.id = (SELECT indexer_id FROM download_grab_history WHERE download_client_id = @ClientId AND external_download_id = @ExtId ORDER BY id DESC LIMIT 1)),
                            @ClientId,
                            COALESCE(
                                (SELECT client_name FROM tracked_downloads WHERE download_client_id = @ClientId AND external_download_id = @ExtId LIMIT 1),
                                (SELECT name FROM download_clients WHERE id = @ClientId)
                            ),
                            @ExtId,
                            COALESCE(
                                (SELECT info_hash FROM tracked_downloads WHERE download_client_id = @ClientId AND external_download_id = @ExtId LIMIT 1),
                                (SELECT info_hash FROM download_grab_history WHERE download_client_id = @ClientId AND external_download_id = @ExtId ORDER BY id DESC LIMIT 1)
                            ),
                            @Dest,
                            @DataJson
                        );
                        """,
                        new
                        {
                            Now = now,
                            EventType = historyEventType,
                            ClientId = clientId,
                            ExtId = extId,
                            Title = title,
                            Dest = destPath,
                            DataJson = JsonSerializer.Serialize(new { verdict, hardlinked, message }, JsonOpts),
                        },
                        transaction: tx);
                }
            },
            ct);

        if (notificationSink is not null && historyEventType == "DownloadFolderImported")
        {
            var isUpgrade = message.Contains("upgrade", StringComparison.OrdinalIgnoreCase) ||
                            message.Contains("replaced", StringComparison.OrdinalIgnoreCase);
            await notificationSink.PublishAsync(
                new Managerr.Common.Notifications.MediaLifecycleNotificationEvent(
                    EventType: isUpgrade
                        ? Managerr.Common.Notifications.MediaNotificationEventTypes.OnUpgrade
                        : Managerr.Common.Notifications.MediaNotificationEventTypes.OnImport,
                    Title: $"{(isUpgrade ? "Upgraded" : "Imported")} — {title}",
                    Message: message,
                    ReleaseTitle: title,
                    IsUpgrade: isUpgrade,
                    DestinationPath: destPath),
                ct);
        }
    }

    private async Task TryBroadcastAsync(string scope, string eventName, object payload, CancellationToken ct)
    {
        try
        {
            if (realtime is not null)
            {
                await realtime.PublishScopedEventAsync(scope, eventName, payload, ct);
            }
        }
        catch
        {
            // Best-effort SignalR push
        }
    }

    private async Task TryTriggerPlexSectionRefreshAsync(
        string? plexSectionId,
        string? libraryRootPath = null,
        string? firstImportedRelativePath = null,
        CancellationToken ct = default)
    {
        try
        {
            var plexUrl = (await appSettings.GetAsync(PlexSettingsKeys.BoundServerUrl, ct)
                           ?? Environment.GetEnvironmentVariable("MANAGERR_PLEX_URL"))?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(plexUrl))
            {
                return;
            }

            var token = await appSettings.GetSecretAsync(PlexSettingsKeys.OwnerAdminTokenEnc, ct)
                        ?? Environment.GetEnvironmentVariable("MANAGERR_PLEX_TOKEN");

            using var http = httpFactory.CreateHomelabClient(allowPrivateLan: true, ignoreSslErrors: true);
            if (!string.IsNullOrWhiteSpace(token))
            {
                http.DefaultRequestHeaders.TryAddWithoutValidation("X-Plex-Token", token);
            }
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");

            var relDir = !string.IsNullOrWhiteSpace(firstImportedRelativePath)
                ? Path.GetDirectoryName(firstImportedRelativePath)?.Replace('\\', '/').Trim('/')
                : null;
            if (string.IsNullOrWhiteSpace(relDir) ||
                relDir.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(seg => seg is "." or ".."))
            {
                relDir = null;
            }

            JsonElement secDoc = default;
            var fetchedSections = false;
            try
            {
                var doc = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<JsonElement>(http, $"{plexUrl}/library/sections", ct);
                if (doc.ValueKind == JsonValueKind.Object)
                {
                    secDoc = doc;
                    fetchedSections = true;
                }
            }
            catch
            {
                fetchedSections = false;
            }

            var refreshedAny = false;

            if (fetchedSections &&
                secDoc.TryGetProperty("MediaContainer", out var mc) &&
                mc.TryGetProperty("Directory", out var dirs) &&
                dirs.ValueKind == JsonValueKind.Array)
            {
                var targetDirs = new List<JsonElement>();
                foreach (var d in dirs.EnumerateArray())
                {
                    var key = d.TryGetProperty("key", out var kEl) ? kEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(plexSectionId))
                    {
                        if (string.Equals(key, plexSectionId, StringComparison.OrdinalIgnoreCase))
                        {
                            targetDirs.Add(d);
                        }
                    }
                    else
                    {
                        targetDirs.Add(d);
                    }
                }

                foreach (var d in targetDirs)
                {
                    var key = d.GetProperty("key").GetString()!;
                    var plexLocPaths = new List<string>();

                    if (d.TryGetProperty("Location", out var locProp))
                    {
                        if (locProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var loc in locProp.EnumerateArray())
                            {
                                if (loc.TryGetProperty("path", out var pEl) && pEl.GetString() is { Length: > 0 } p)
                                {
                                    plexLocPaths.Add(p);
                                }
                            }
                        }
                        else if (locProp.ValueKind == JsonValueKind.Object)
                        {
                            if (locProp.TryGetProperty("path", out var pEl) && pEl.GetString() is { Length: > 0 } p)
                            {
                                plexLocPaths.Add(p);
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(relDir) && plexLocPaths.Count > 0)
                    {
                        foreach (var plexLocPath in plexLocPaths)
                        {
                            var sep = plexLocPath.StartsWith('/') ? "/" : "\\";
                            var dirPart = plexLocPath.StartsWith('/') ? relDir : relDir.Replace('/', '\\');
                            var mappedPlexDir = $"{plexLocPath.TrimEnd('/', '\\')}{sep}{dirPart}";
                            try
                            {
                                await http.GetStringAsync($"{plexUrl}/library/sections/{Uri.EscapeDataString(key)}/refresh?path={Uri.EscapeDataString(mappedPlexDir)}", ct);
                            }
                            catch
                            {
                                await http.GetStringAsync($"{plexUrl}/library/sections/{Uri.EscapeDataString(key)}/refresh", ct);
                            }
                            refreshedAny = true;
                        }
                    }
                    else
                    {
                        await http.GetStringAsync($"{plexUrl}/library/sections/{Uri.EscapeDataString(key)}/refresh", ct);
                        refreshedAny = true;
                    }
                }
            }

            if (!refreshedAny && !string.IsNullOrWhiteSpace(plexSectionId))
            {
                await http.GetStringAsync($"{plexUrl}/library/sections/{Uri.EscapeDataString(plexSectionId)}/refresh", ct);
            }
        }
        catch
        {
            // Best-effort Plex library refresh
        }
    }

    private static bool IsCompletedStatus(string status) =>
        status.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("uploading", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("stalledUP", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("pausedUP", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("stoppedUP", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("queuedUP", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("forcedUP", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnpackingClientStatus(string status) =>
        status.Equals("Extracting", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Verifying", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Repairing", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Moving", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Running", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailedClientStatus(ActiveDownloadQueueItem item) =>
        item.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
        item.Status.Equals("error", StringComparison.OrdinalIgnoreCase) ||
        item.Status.Equals("missingFiles", StringComparison.OrdinalIgnoreCase) ||
        item.Status.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase) ||
        item.Status.Contains("BAD", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailedUnpackPath(string outputPath, out string reason)
    {
        if (outputPath.Contains("_FAILED_", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Unpack/extraction failed in staging directory '{Path.GetFileName(outputPath)}' (_FAILED_ marker).";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool IsUnpackingPathOrFiles(string outputPath, IReadOnlyList<string> containedFiles, out string reason)
    {
        if (outputPath.Contains("_UNPACK_", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Staging folder '{Path.GetFileName(outputPath)}' is still unpacking (_UNPACK_ marker).";
            return true;
        }

        if (containedFiles.Count > 0 &&
            containedFiles.All(f => IncompleteStagingSuffixes.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase))))
        {
            reason = "All files in output directory have incomplete (.part/.partial/.!qB) extensions.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool CheckLibraryFreeSpace(string libraryRootPath, IReadOnlyList<string> mediaFiles, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(libraryRootPath);
            var drive = new DriveInfo(Path.GetFullPath(libraryRootPath));
            var totalBytes = mediaFiles.Where(File.Exists).Sum(f => new FileInfo(f).Length);
            if (drive.TotalSize > 0 && drive.AvailableFreeSpace < totalBytes + (32L * 1024 * 1024))
            {
                // Allow if on same root volume where hardlink requires 0 additional data blocks
                var srcDrive = File.Exists(mediaFiles[0]) ? new DriveInfo(Path.GetFullPath(mediaFiles[0])) : null;
                if (srcDrive is null || !string.Equals(srcDrive.Name, drive.Name, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Insufficient free disk space on '{libraryRootPath}' ({drive.AvailableFreeSpace / (1024 * 1024)} MB available, {(totalBytes / (1024 * 1024)) + 32} MB required).";
                    return false;
                }
            }
        }
        catch
        {
            // Non-fatal on virtual/tmpfs mounts without DriveInfo support
        }

        return true;
    }

    private static IReadOnlyList<string> SelectAllImportableMediaFiles(string outputPath, IReadOnlyList<string> containedFiles)
    {
        var candidates = containedFiles.Count > 0 && containedFiles.Any(f => PrimaryMediaExtensions.Contains(Path.GetExtension(f)))
            ? containedFiles
            : File.Exists(outputPath)
                ? [outputPath]
                : Directory.Exists(outputPath)
                    ? Directory.EnumerateFiles(outputPath, "*", SearchOption.AllDirectories).ToArray()
                    : [];

        var valid = candidates
            .Where(File.Exists)
            .Where(f => PrimaryMediaExtensions.Contains(Path.GetExtension(f)))
            .Where(f => !IsSampleFileName(f))
            .ToList();

        if (valid.Count > 1)
        {
            var maxSize = valid.Max(f => new FileInfo(f).Length);
            // Filter tiny (<20 MB and <10% of largest episode) sample/featurette stubs in multi-file folders
            valid = valid
                .Where(f =>
                {
                    var len = new FileInfo(f).Length;
                    return !(maxSize >= 100L * 1024 * 1024 && len < 20L * 1024 * 1024 && len * 10 < maxSize);
                })
                .ToList();
        }

        return valid.OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? SelectPrimaryMediaFile(string outputPath, IReadOnlyList<string> containedFiles)
    {
        var candidates = containedFiles.Count > 0 && containedFiles.Any(f => PrimaryMediaExtensions.Contains(Path.GetExtension(f)))
            ? containedFiles
            : File.Exists(outputPath)
                ? [outputPath]
                : Directory.Exists(outputPath)
                    ? Directory.EnumerateFiles(outputPath, "*", SearchOption.AllDirectories).ToArray()
                    : [];

        return candidates
            .Where(File.Exists)
            .Where(f => PrimaryMediaExtensions.Contains(Path.GetExtension(f)))
            .Where(f => !IsSampleFileName(f))
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
    }

    private static bool IsSampleFileName(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        if (stem.EndsWith(".sample", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("sample-", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("sample", StringComparison.OrdinalIgnoreCase) ||
            stem.Contains("-sample", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parentDir = Path.GetFileName(Path.GetDirectoryName(filePath) ?? "");
        return parentDir.Equals("Sample", StringComparison.OrdinalIgnoreCase) ||
               parentDir.Equals("Samples", StringComparison.OrdinalIgnoreCase);
    }

    private static LibrarySummary? ResolveTargetLibrary(IReadOnlyList<LibrarySummary> libraries, string vertical)
    {
        return libraries.FirstOrDefault(l => string.Equals(l.MediaType, vertical, StringComparison.OrdinalIgnoreCase))
               ?? (vertical == "anime" ? libraries.FirstOrDefault(l => string.Equals(l.MediaType, "tv", StringComparison.OrdinalIgnoreCase)) : null)
               ?? libraries.FirstOrDefault();
    }

    private static readonly HashSet<string> SharedFolderBasenames = new(StringComparer.OrdinalIgnoreCase)
    {
        "downloads", "usenet", "torrents", "complete", "completed", "incomplete",
        "tv", "series", "movies", "anime", "music", "books", "ebooks", "audiobooks",
        "comics", "default", "sabnzbd", "nzbget"
    };

    [System.Text.RegularExpressions.GeneratedRegex(@"^(_FAILED_|_UNPACK_)+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex LeadingFailedOrUnpackPrefixRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\.\d+$")]
    private static partial System.Text.RegularExpressions.Regex TrailingNumericCollisionSuffixRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"[^a-zA-Z0-9]")]
    private static partial System.Text.RegularExpressions.Regex NonAlphanumericRegex();

    /// <summary>
    /// Hardlink imports leave the source behind and removing the client's history entry did not reliably remove
    /// the job folder (testlab re-downloads landed in '&lt;name&gt;.1'). Usenet jobs are never seeded, so once every
    /// file is imported the folder is dead weight.
    /// </summary>
    public static void TryDeleteImportedUsenetJobFolder(
        ActiveDownloadQueueItem item,
        IReadOnlyList<LibrarySummary> libraries,
        string? jobTitle = null,
        string? clientCategory = null)
    {
        if (!string.Equals(item.Protocol, "usenet", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(item.OutputPath))
        {
            return;
        }

        var sep = Path.DirectorySeparatorChar;

        // If OutputPath is a single file: NEVER call Directory.Delete! Only File.Delete(fullFile)
        if (File.Exists(item.OutputPath))
        {
            var fullFile = Path.GetFullPath(item.OutputPath);
            try
            {
                var fi = new FileInfo(fullFile);
                if (fi.LinkTarget is not null || (fi.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }
            }
            catch
            {
                return;
            }

            if (fullFile.Count(c => c == sep) < 3 ||
                TryResolveCanonicalPath(fullFile) is not { } canonicalFile ||
                canonicalFile.Count(c => c == sep) < 3)
            {
                return;
            }

            foreach (var lib in libraries)
            {
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(lib.RootFolderPath));
                if (string.Equals(root, fullFile, StringComparison.Ordinal) ||
                    fullFile.StartsWith(root + sep, StringComparison.Ordinal))
                {
                    return;
                }

                if (TryResolveCanonicalPath(lib.RootFolderPath) is { } canonicalRoot &&
                    (string.Equals(canonicalRoot, canonicalFile, StringComparison.Ordinal) ||
                     canonicalFile.StartsWith(canonicalRoot + sep, StringComparison.Ordinal)))
                {
                    return;
                }
            }

            try
            {
                File.Delete(fullFile);
            }
            catch
            {
                // Best effort
            }
            return;
        }

        if (!Directory.Exists(item.OutputPath))
        {
            return;
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.OutputPath));
        try
        {
            var di = new DirectoryInfo(full);
            if (di.LinkTarget is not null || (di.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }
        }
        catch
        {
            return;
        }

        // Guard rails: never something near the filesystem root, a library root, inside one, or above one
        if (full.Count(c => c == sep) < 3 ||
            TryResolveCanonicalPath(full) is not { } canonicalFull ||
            canonicalFull.Count(c => c == sep) < 3)
        {
            return;
        }

        foreach (var lib in libraries)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(lib.RootFolderPath));
            if (string.Equals(root, full, StringComparison.Ordinal) ||
                root.StartsWith(full + sep, StringComparison.Ordinal) ||
                full.StartsWith(root + sep, StringComparison.Ordinal))
            {
                return;
            }

            if (TryResolveCanonicalPath(lib.RootFolderPath) is { } canonicalRoot &&
                (string.Equals(canonicalRoot, canonicalFull, StringComparison.Ordinal) ||
                 canonicalRoot.StartsWith(canonicalFull + sep, StringComparison.Ordinal) ||
                 canonicalFull.StartsWith(canonicalRoot + sep, StringComparison.Ordinal)))
            {
                return;
            }
        }

        var baseName = Path.GetFileName(full);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return;
        }

        // Shared/category roots rejection
        var effectiveCategory = clientCategory ?? item.Category;
        if (SharedFolderBasenames.Contains(baseName) ||
            (!string.IsNullOrWhiteSpace(effectiveCategory) && string.Equals(baseName, effectiveCategory, StringComparison.OrdinalIgnoreCase)) ||
            baseName.EndsWith("-managerr", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(item.MediaVertical) && string.Equals(baseName, $"{item.MediaVertical}-managerr", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // Require positive job-folder correlation
        var effectiveTitle = jobTitle ?? item.Title;
        if (string.IsNullOrWhiteSpace(effectiveTitle))
        {
            return;
        }

        var cleanDirName = LeadingFailedOrUnpackPrefixRegex().Replace(baseName, "");
        cleanDirName = TrailingNumericCollisionSuffixRegex().Replace(cleanDirName, "");
        var normDir = NonAlphanumericRegex().Replace(cleanDirName, "").ToLowerInvariant();

        var normTitle = NonAlphanumericRegex().Replace(effectiveTitle, "").ToLowerInvariant();

        if (normDir.Length < 4 || normTitle.Length < 4)
        {
            return;
        }

        var matches = normDir == normTitle ||
                      normDir.StartsWith(normTitle, StringComparison.Ordinal) ||
                      normTitle.StartsWith(normDir, StringComparison.Ordinal) ||
                      normDir.Contains(normTitle, StringComparison.Ordinal) ||
                      normTitle.Contains(normDir, StringComparison.Ordinal);

        if (!matches)
        {
            return;
        }

        try
        {
            Directory.Delete(full, recursive: true);
        }
        catch
        {
            // Best effort; the client may still hold the folder
        }
    }

    public static NamingContextMetadata BuildExistingWorkNamingContext(
        MediaWorkDetailTree workTree,
        string libraryRootPath,
        int? seasonNumber,
        string? episodeTitle = null)
    {
        var allFiles = workTree.Containers
            .SelectMany(c => c.Units)
            .SelectMany(u => u.Files)
            .Where(f => !string.IsNullOrWhiteSpace(f.RelativePath))
            .ToList();

        string? existingWorkFolder = null;
        var topSegments = allFiles
            .Select(f => f.RelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Where(segs => segs.Length >= 2 &&
                           !segs[0].StartsWith("Season ", StringComparison.OrdinalIgnoreCase) &&
                           !segs[0].Equals("Specials", StringComparison.OrdinalIgnoreCase))
            .Select(segs => segs[0])
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(topSegments))
        {
            existingWorkFolder = topSegments;
        }

        if (existingWorkFolder is null && Directory.Exists(libraryRootPath))
        {
            try
            {
                var candidateTarget1 = AtomicFileOrganizer.SanitizeSegment(workTree.Work.Title.Replace('’', '\'').Replace('‘', '\'').Replace('`', '\''), "space").Trim();
                var candidateTarget2 = workTree.Work.Year.HasValue
                    ? AtomicFileOrganizer.SanitizeSegment($"{workTree.Work.Title.Replace('’', '\'').Replace('‘', '\'').Replace('`', '\'')} ({workTree.Work.Year.Value})", "space").Trim()
                    : null;

                var dirs = Directory.GetDirectories(libraryRootPath);
                var matchedDirs = dirs
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Where(name =>
                    {
                        var normName = AtomicFileOrganizer.SanitizeSegment(name!.Replace('’', '\'').Replace('‘', '\'').Replace('`', '\''), "space").Trim();
                        return string.Equals(normName, candidateTarget1, StringComparison.OrdinalIgnoreCase) ||
                               (candidateTarget2 is not null && string.Equals(normName, candidateTarget2, StringComparison.OrdinalIgnoreCase));
                    })
                    .ToList();

                if (matchedDirs.Count == 1)
                {
                    existingWorkFolder = matchedDirs[0];
                }
            }
            catch
            {
                // Best effort directory enumeration
            }
        }

        string? existingSeasonFolder = null;
        var preferSeasonalAnime = false;

        if (workTree.Work.WorkKind is "tv" or "anime" && seasonNumber.HasValue)
        {
            var currentContainer = workTree.Containers.FirstOrDefault(c => c.ContainerNumber == seasonNumber.Value);
            if (currentContainer is not null)
            {
                var seasonFiles = currentContainer.Units
                    .SelectMany(u => u.Files)
                    .Where(f => !string.IsNullOrWhiteSpace(f.RelativePath))
                    .Select(f => f.RelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
                    .Where(segs => segs.Length >= 3)
                    .Select(segs => segs[1])
                    .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(seasonFiles))
                {
                    existingSeasonFolder = seasonFiles;
                }
            }

            foreach (var c in workTree.Containers)
            {
                foreach (var u in c.Units)
                {
                    foreach (var f in u.Files)
                    {
                        if (string.IsNullOrWhiteSpace(f.RelativePath)) continue;
                        var segs = f.RelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                        if (segs.Length >= 3 || Regex.IsMatch(Path.GetFileName(f.RelativePath), @"S\d{2}E\d{2}", RegexOptions.IgnoreCase))
                        {
                            preferSeasonalAnime = true;
                            break;
                        }
                    }
                    if (preferSeasonalAnime) break;
                }
                if (preferSeasonalAnime) break;
            }

            if (existingSeasonFolder is null && preferSeasonalAnime)
            {
                var allSeasonSegments = workTree.Containers
                    .SelectMany(c => c.Units)
                    .SelectMany(u => u.Files)
                    .Where(f => !string.IsNullOrWhiteSpace(f.RelativePath))
                    .Select(f => f.RelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
                    .Where(segs => segs.Length >= 3)
                    .Select(segs => segs[1])
                    .ToList();

                var hasUnpaddedSiblings = allSeasonSegments.Any(s => Regex.IsMatch(s, @"^Season [1-9]$", RegexOptions.IgnoreCase));

                if (seasonNumber.Value == 0)
                {
                    if (!string.IsNullOrWhiteSpace(existingWorkFolder) && Directory.Exists(libraryRootPath))
                    {
                        var seriesPath = Path.Combine(libraryRootPath, existingWorkFolder);
                        if (Directory.Exists(seriesPath))
                        {
                            if (Directory.Exists(Path.Combine(seriesPath, "Specials")))
                            {
                                existingSeasonFolder = "Specials";
                            }
                            else if (Directory.Exists(Path.Combine(seriesPath, "Season 0")))
                            {
                                existingSeasonFolder = "Season 0";
                            }
                            else if (Directory.Exists(Path.Combine(seriesPath, "Season 00")))
                            {
                                existingSeasonFolder = "Season 00";
                            }
                        }
                    }
                    existingSeasonFolder ??= "Season 00";
                }
                else if (seasonNumber.Value > 0 && hasUnpaddedSiblings)
                {
                    existingSeasonFolder = $"Season {seasonNumber.Value}";
                }
            }

            if (existingSeasonFolder is null && !string.IsNullOrWhiteSpace(existingWorkFolder) && Directory.Exists(libraryRootPath))
            {
                var seriesPath = Path.Combine(libraryRootPath, existingWorkFolder);
                if (Directory.Exists(seriesPath))
                {
                    var seasonDirUnpadded = Path.Combine(seriesPath, $"Season {seasonNumber.Value}");
                    var seasonDirPadded = Path.Combine(seriesPath, $"Season {seasonNumber.Value:D2}");
                    if (Directory.Exists(seasonDirUnpadded))
                    {
                        existingSeasonFolder = $"Season {seasonNumber.Value}";
                        preferSeasonalAnime = true;
                    }
                    else if (Directory.Exists(seasonDirPadded))
                    {
                        existingSeasonFolder = $"Season {seasonNumber.Value:D2}";
                        preferSeasonalAnime = true;
                    }
                }
            }
        }

        if (!AtomicFileOrganizer.IsSafeSinglePathSegment(existingWorkFolder))
        {
            existingWorkFolder = null;
        }

        if (!AtomicFileOrganizer.IsSafeSinglePathSegment(existingSeasonFolder))
        {
            existingSeasonFolder = null;
        }

        return new NamingContextMetadata(
            EpisodeTitle: episodeTitle,
            ExistingWorkFolderName: existingWorkFolder,
            ExistingSeasonFolderName: existingSeasonFolder,
            PreferSeasonalAnimeLayout: preferSeasonalAnime);
    }

    public static IReadOnlyList<string> ExtractPrimaryWantedAudioLanguages(LanguagePolicySummary? policy, string origIso)
    {
        if (policy is null || policy.Tiers.Count == 0)
        {
            return [];
        }

        var cutoffTiers = policy.Tiers
            .Where(t => t.SatisfiesLanguageCutoff)
            .OrderBy(t => t.Tier)
            .ToList();

        foreach (var tier in cutoffTiers)
        {
            var singleLangs = tier.AudioContainsAll.Concat(tier.AudioContainsAny)
                .Where(l => !string.Equals(l, "original", StringComparison.OrdinalIgnoreCase))
                .Select(l => LanguageCodeNormalizer.NormalizeToIso2(l))
                .Where(l => l != "und")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (singleLangs.Count == 1)
            {
                return singleLangs;
            }
        }

        var tier1 = policy.Tiers.OrderBy(t => t.Tier).FirstOrDefault();
        if (tier1 is not null)
        {
            var nonOrig = tier1.AudioContainsAll
                .Where(l => !string.Equals(l, "original", StringComparison.OrdinalIgnoreCase))
                .Select(l => LanguageCodeNormalizer.NormalizeToIso2(l))
                .Where(l => l != "und")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (nonOrig.Count > 0)
            {
                return nonOrig;
            }

            if (tier1.AudioContainsAll.Any(l => string.Equals(l, "original", StringComparison.OrdinalIgnoreCase)) ||
                tier1.AudioContainsAny.Any(l => string.Equals(l, "original", StringComparison.OrdinalIgnoreCase)))
            {
                return origIso != "und" ? [origIso] : [];
            }
        }

        return [];
    }

    public static IReadOnlyList<string> BuildExpectedAudioLanguages(
        ParsedReleaseInfo parsed,
        IReadOnlyList<MediaUnitDetail> targetUnits,
        string originalLanguage,
        LanguagePolicySummary? effectiveLanguagePolicy = null)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasMultiToken = false;
        var origIso = LanguageCodeNormalizer.NormalizeToIso2(originalLanguage);

        foreach (var l in parsed.DetectedLanguages)
        {
            if (string.Equals(l, "multi", StringComparison.OrdinalIgnoreCase))
            {
                hasMultiToken = true;
                continue;
            }

            var iso = LanguageCodeNormalizer.NormalizeToIso2(l);
            if (iso != "und") set.Add(iso);
        }

        if (parsed.IsDualAudio)
        {
            if (set.Contains("de") && set.Contains("en") && !ReleaseParserRouter.HasExplicitEnglishToken(parsed.RawTitle) && origIso is not ("und" or "de"))
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

        var primaryWanted = ExtractPrimaryWantedAudioLanguages(effectiveLanguagePolicy, origIso);
        if (primaryWanted.Count > 0 && primaryWanted.All(set.Contains))
        {
            return primaryWanted.ToList();
        }

        // If the release explicitly declared its language(s) in its title (e.g. .English. or .German.DL),
        // we only verify what the release claims. We do NOT demand that an explicitly English release
        // contains German, as that would reject genuine lower-tier language cutoff baselines (#dual cutoff upgrade).
        if (set.Count > 0)
        {
            return set.ToList();
        }

        foreach (var unit in targetUnits)
        {
            foreach (var req in unit.RequiredAudioLangs)
            {
                var iso = string.Equals(req, "original", StringComparison.OrdinalIgnoreCase)
                    ? origIso
                    : LanguageCodeNormalizer.NormalizeToIso2(req);
                if (iso != "und") set.Add(iso);
            }
        }

        if (set.Count > 0)
        {
            return set.ToList();
        }

        // For MULTi releases without explicit single-language title tags or unit-level promotions, accept any genuine verified container audio track(s)
        if (hasMultiToken)
        {
            return [];
        }

        return [origIso == "und" ? "en" : origIso];
    }

    private static TrackedDownloadDto MapTrackedDto(TrackedRow r) =>
        new(
            Id: r.Id,
            DownloadClientId: r.DownloadClientId,
            ClientName: r.ClientName,
            ExternalDownloadId: r.ExternalDownloadId,
            Protocol: r.Protocol,
            Title: r.Title,
            MediaVertical: r.MediaVertical,
            WorkId: r.WorkId,
            WorkTitle: r.WorkTitle,
            UnitIds: JsonSerializer.Deserialize<long[]>(r.UnitIdsJson, JsonOpts) ?? [],
            State: r.State,
            TrackedStatus: r.TrackedStatus,
            StatusMessages: JsonSerializer.Deserialize<string[]>(r.StatusMessagesJson, JsonOpts) ?? [],
            OutputPath: r.OutputPath,
            QuarantineId: r.QuarantineId,
            CanMoveFiles: r.CanMoveFiles != 0,
            FirstSeenAt: r.FirstSeenAt,
            UpdatedAt: r.UpdatedAt);

    private sealed class TrackedRow
    {
        public long Id { get; init; }
        public long DownloadClientId { get; init; }
        public string ClientName { get; init; } = "";
        public string ExternalDownloadId { get; init; } = "";
        public string Protocol { get; init; } = "usenet";
        public string? InfoHash { get; init; }
        public string Title { get; init; } = "";
        public string MediaVertical { get; init; } = "movies";
        public long? WorkId { get; init; }
        public string? WorkTitle { get; init; }
        public string UnitIdsJson { get; init; } = "[]";
        public string State { get; init; } = TrackedDownloadStates.Downloading;
        public string TrackedStatus { get; init; } = "ok";
        public string StatusMessagesJson { get; init; } = "[]";
        public string? OutputPath { get; init; }
        public long? QuarantineId { get; init; }
        public int CanMoveFiles { get; init; } = 1;
        public string FirstSeenAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private sealed class LedgerRow
    {
        public long DownloadClientId { get; init; }
        public string ClientName { get; init; } = "";
        public string ExternalDownloadId { get; init; } = "";
        public string Title { get; init; } = "";
        public string Verdict { get; init; } = "";
        public string? DestinationPath { get; init; }
        public int Hardlinked { get; init; }
        public string Message { get; init; } = "";
    }

    internal static bool IsForeignClientCategoryItem(ActiveDownloadQueueItem item, DownloadClientSummary? clientCfg) =>
        !UnifiedDownloadOrchestrator.IsClientCategoryOwnedByManagerr(item.Category, clientCfg?.CategoryMappings);

    private static readonly HashSet<string> KnownForeignCategorySegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "series", "sonarr", "radarr", "lidarr", "readarr", "prowlarr", "whisparr", "bazarr",
        "tv", "movies", "anime", "music", "books", "ebooks", "audiobooks", "comics"
    };

    private static bool IsOutputPathUnderForeignCategory(string? outputPath, DownloadClientSummary? clientCfg)
    {
        if (string.IsNullOrWhiteSpace(outputPath) || clientCfg is null || clientCfg.CategoryMappings.Count == 0)
        {
            return false;
        }

        var ownedCategories = clientCfg.CategoryMappings.Values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var segments = outputPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 1)
        {
            return false;
        }

        if (segments.Any(s => ownedCategories.Contains(s)))
        {
            return false;
        }

        // Check parent directory segments for unowned mapped keys or foreign *arr category folders
        foreach (var seg in segments.Take(segments.Length - 1))
        {
            if (clientCfg.CategoryMappings.ContainsKey(seg) || KnownForeignCategorySegments.Contains(seg))
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryResolveCanonicalPath(string path)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var root = Path.GetPathRoot(full) ?? Path.DirectorySeparatorChar.ToString();
            if (string.Equals(full, Path.TrimEndingDirectorySeparator(root), StringComparison.Ordinal))
            {
                return full;
            }

            var relative = full[root.Length..];
            var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            const int maxHops = 32;

            foreach (var seg in segments)
            {
                current = Path.Combine(current, seg);
                var hops = 0;
                while (hops < maxHops)
                {
                    FileSystemInfo? fsi = Directory.Exists(current)
                        ? new DirectoryInfo(current)
                        : File.Exists(current)
                            ? new FileInfo(current)
                            : null;

                    if (fsi?.LinkTarget is null)
                    {
                        break;
                    }

                    var resolved = fsi.ResolveLinkTarget(returnFinalTarget: false);
                    if (resolved is null)
                    {
                        break;
                    }

                    current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved.FullName));
                    hops++;
                }

                if (hops >= maxHops)
                {
                    return null;
                }
            }

            return Path.TrimEndingDirectorySeparator(current);
        }
        catch
        {
            return null;
        }
    }

    internal async Task<bool> TryReconcileExternallyMovedDownloadAsync(
        long workId,
        IReadOnlyList<long>? expectedUnitIds,
        string? grabbedAtIso = null,
        CancellationToken ct = default)
    {
        var diskScanSvc = serviceProvider?.GetService<IMediaManagementAndDiskScanService>();
        if (diskScanSvc is null)
        {
            return false;
        }

        try
        {
            await diskScanSvc.ScanAndVerifyWorkAsync(workId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "ScanAndVerifyWorkAsync failed during external move reconciliation for work {WorkId}.", workId);
            return false;
        }

        var tree = await catalogService.GetWorkTreeAsync(workId, null, ct);
        if (tree is null)
        {
            return false;
        }

        var lib = await catalogService.GetLibraryByIdAsync(tree.Work.LibraryId, ct);
        var passingStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            VerificationVerdicts.VerifiedPass,
            VerificationVerdicts.VerifiedPassAutoTagged,
            VerificationVerdicts.MusicPolicyPass
        };

        IReadOnlyList<MediaUnitDetail> targetUnits;
        if (expectedUnitIds is { Count: > 0 })
        {
            var expectedSet = expectedUnitIds.ToHashSet();
            targetUnits = tree.Containers.SelectMany(c => c.Units).Where(u => expectedSet.Contains(u.Id)).ToList();
            if (targetUnits.Count < expectedSet.Count)
            {
                return false;
            }
        }
        else
        {
            targetUnits = tree.Containers.SelectMany(c => c.Units).ToList();
            if (targetUnits.Count == 0)
            {
                return false;
            }
        }

        var hasGrabTime = DateTimeOffset.TryParse(grabbedAtIso, out var grabbedAt);
        var minReconcileTime = hasGrabTime ? grabbedAt.AddSeconds(-2) : DateTimeOffset.MinValue;

        bool IsReconciledPostGrabFile(MediaFileSummary f)
        {
            if (!passingStatuses.Contains(f.VerifiedStatus))
            {
                return false;
            }

            if (!hasGrabTime)
            {
                return true;
            }

            if (DateTimeOffset.TryParse(f.ImportedAt, out var importedAt) && importedAt >= minReconcileTime)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(lib?.RootFolderPath))
            {
                try
                {
                    var fullPath = Path.GetFullPath(Path.Combine(lib.RootFolderPath, f.RelativePath));
                    if (File.Exists(fullPath) && File.GetLastWriteTimeUtc(fullPath) >= minReconcileTime.UtcDateTime)
                    {
                        return true;
                    }
                }
                catch
                {
                    // Ignore path probe failures
                }
            }

            return false;
        }

        return targetUnits.All(u => u.Files.Any(IsReconciledPostGrabFile));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\s._-])[\s._-]+(?:Specials?|OVAs?|OADs?|OAVs?)(?:[\s._-]+\d{1,3})?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase, 250)]
    private static partial System.Text.RegularExpressions.Regex TrailingSpecialsSuffixRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:Specials?|OVAs?|OADs?|OAVs?|SP)[\s._-]*(?:[-–][\s._-]*)?#?(?<ep>\d{1,3})\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase, 250)]
    private static partial System.Text.RegularExpressions.Regex SpecialEpisodeNumberRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:Specials?|OVAs?|OADs?|OAVs?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase, 250)]
    private static partial System.Text.RegularExpressions.Regex SpecialKeywordTokenRegex();
}
