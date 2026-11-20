using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Managerr.Catalog;
using Managerr.Datastore;
using Managerr.Datastore.Services;

namespace Managerr.Verification;

public interface ILanguageVerificationService
{
    Task<MediaFileVerificationReport> VerifyStagedFileAsync(RunVerificationRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<MediaFileVerificationReport>> ListRecentReportsAsync(int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<QuarantinedImportSummary>> ListQuarantineAsync(string? statusFilter = null, CancellationToken ct = default);
    Task<QuarantinedImportSummary> ApproveQuarantineAsync(long quarantineId, long reviewerUserId, string confirmedLanguage, CancellationToken ct = default);
    Task<QuarantinedImportSummary> RejectAndBlocklistQuarantineAsync(long quarantineId, long reviewerUserId, CancellationToken ct = default);
    Task<int> PruneOrphanedQuarantineSnippetsAsync(CancellationToken ct = default) => Task.FromResult(0);
}

/// <summary>
/// Full 3-Tier Post-Download Language Verification & Quarantine Orchestrator.
/// Guarantees no mislabeled MULTI/und release overwrites an existing library file or satisfies LanguageCutoffMet prematurely.
/// </summary>
public sealed class LanguageVerificationService(
    ISqliteDbFactory dbFactory,
    IContainerProbeAdapter containerProbe,
    ITextLanguageDetector textDetector,
    ISubtitleOcrAdapter subtitleOcr,
    IAudioSpeechLidAdapter speechAdapter,
    IMkvHeaderPatcher headerPatcher,
    IPolymorphicCatalogService catalogService,
    Managerr.Common.Paths.IAppPaths? appPaths = null,
    IAppSettingsService? appSettings = null) : ILanguageVerificationService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Deletes /config/quarantine/job-* folders that no quarantine row (open or reviewed) references any more.
    /// </summary>
    public async Task<int> PruneOrphanedQuarantineSnippetsAsync(CancellationToken ct = default)
    {
        var root = appPaths is null ? null : Path.Combine(appPaths.ConfigDirectory, "quarantine");
        if (root is null || !Directory.Exists(root))
        {
            return 0;
        }

        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var referenced = (await conn.QueryAsync<string>("SELECT snippet_paths_json FROM quarantined_imports;"))
            .SelectMany(json => JsonSerializer.Deserialize<string[]>(json, JsonOpts) ?? [])
            .Select(p => Path.GetFullPath(Path.GetDirectoryName(p) ?? ""))
            .ToHashSet(StringComparer.Ordinal);

        var pruned = 0;
        foreach (var dir in Directory.EnumerateDirectories(root, "job-*"))
        {
            if (referenced.Contains(Path.GetFullPath(dir)))
            {
                continue;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
                pruned++;
            }
            catch
            {
                // Best-effort; retried on the next health check
            }
        }

        return pruned;
    }

    public async Task<MediaFileVerificationReport> VerifyStagedFileAsync(
        RunVerificationRequest request,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var origLang = string.IsNullOrWhiteSpace(request.OriginalLanguage) ? "en" : request.OriginalLanguage.Trim().ToLowerInvariant();
        var expectedAudio = (request.ExpectedAudioLanguages ?? [])
            .Select(x => ResolveToken(x, origLang))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var expectedSubs = (request.ExpectedSubtitleLanguages ?? [])
            .Select(x => ResolveToken(x, origLang))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Music vertical bypasses spoken-speech Whisper LID per spec Section 4 (singing LID is unreliable vs AcoustID/tags)
        if (string.Equals(request.MediaVertical, "music", StringComparison.OrdinalIgnoreCase))
        {
            return await PersistReportAndActAsync(
                request,
                verdict: VerificationVerdicts.MusicPolicyPass,
                overallConfidence: 0.99,
                headerPatched: false,
                audioLangs: [origLang],
                subLangs: [],
                streams: [],
                failureReason: null,
                elapsedMs: Math.Max(1, sw.ElapsedMilliseconds),
                ct);
        }

        // Stage 0 & Stage 1: Container & Embedded Header Probe
        ContainerProbeResult probe;
        if (request.SyntheticProbeOverride is not null)
        {
            probe = request.SyntheticProbeOverride;
        }
        else
        {
            var metaProbe = await containerProbe.ProbeMetadataOnlyAsync(request.StagedFilePath, ct, request.ExpectedRuntimeSeconds);
            if (!metaProbe.IntegrityPassed)
            {
                probe = metaProbe;
            }
            else
            {
                var anyUntaggedAudio = metaProbe.Streams.Any(s => s.StreamType == "audio" &&
                    (string.IsNullOrWhiteSpace(s.DeclaredLanguage) || LanguageCodeNormalizer.NormalizeToIso2(s.DeclaredLanguage) == "und"));
                var anyUntaggedTextSub = metaProbe.Streams.Any(s => s.StreamType == "subtitle" && !s.IsBitmapSubtitle &&
                    (string.IsNullOrWhiteSpace(s.DeclaredLanguage) || LanguageCodeNormalizer.NormalizeToIso2(s.DeclaredLanguage) == "und"));

                var mode = request.VerificationMode?.ToLowerInvariant() ?? "verify_content_on_upgrades_and_ambiguous";
                var needsPayloadExtraction = mode == "always_verify_content"
                    || (!request.InPlaceLibraryScan && mode != "trust_explicit_tags")
                    || anyUntaggedAudio
                    || anyUntaggedTextSub;

                probe = needsPayloadExtraction
                    ? await containerProbe.ProbeAsync(request.StagedFilePath, request.ExpectedRuntimeSeconds, ct)
                    : metaProbe;
            }
        }

        if (!probe.IntegrityPassed)
        {
            return await PersistReportAndActAsync(
                request,
                verdict: VerificationVerdicts.VerifiedFail,
                overallConfidence: 1.0,
                headerPatched: false,
                audioLangs: [],
                subLangs: [],
                streams: [],
                failureReason: probe.IntegrityFailureReason ?? "Stage 0 container integrity check failed.",
                elapsedMs: Math.Max(1, sw.ElapsedMilliseconds),
                ct: ct,
                failureCode: probe.FailureCodeOrDefault ?? "corrupt_container");
        }

        var streamReports = new List<VerifiedStreamReport>();
        var verifiedAudioLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var verifiedFullSubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var verifiedAnySubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var headerAutoPatched = false;
        var audioTrackOrdinal = 0;

        // Track waveforms by stream index to catch fake duplicate MULTI dubs!
        var audioWaveforms = new List<(int StreamIndex, string Lang, float[]? Pcm)>();

        var verifyMode = request.VerificationMode?.ToLowerInvariant() ?? "verify_content_on_upgrades_and_ambiguous";
        var probedAnyUntaggedAudio = probe.Streams.Any(s => s.StreamType == "audio" &&
            (string.IsNullOrWhiteSpace(s.DeclaredLanguage) || LanguageCodeNormalizer.NormalizeToIso2(s.DeclaredLanguage) == "und"));
        var probedAnyUntaggedTextSub = probe.Streams.Any(s => s.StreamType == "subtitle" && !s.IsBitmapSubtitle &&
            (string.IsNullOrWhiteSpace(s.DeclaredLanguage) || LanguageCodeNormalizer.NormalizeToIso2(s.DeclaredLanguage) == "und"));

        var trustExplicitTagsOnly = verifyMode != "always_verify_content" && (
            (string.Equals(verifyMode, "trust_explicit_tags", StringComparison.OrdinalIgnoreCase) && !probedAnyUntaggedAudio)
            || (request.InPlaceLibraryScan && !probedAnyUntaggedAudio && !probedAnyUntaggedTextSub));

        var (autoPassConfidence, quarantineMinConfidence, autoPatchHeaderConfidence, fakeDubCorrelationThreshold) =
            await LoadVerificationThresholdsAsync(ct);

        foreach (var rawStream in probe.Streams)
        {
            var stream = rawStream with
            {
                DeclaredLanguage = LanguageCodeNormalizer.NormalizeToIso2(rawStream.DeclaredLanguage),
            };
            if (stream.StreamType is "subtitle" or "ebook_text")
            {
                if (trustExplicitTagsOnly && stream.DeclaredLanguage != "und")
                {
                    var cls = stream.IsForcedDisposition ? "forced" : "full";
                    streamReports.Add(new VerifiedStreamReport(
                        stream.StreamIndex,
                        stream.StreamType,
                        stream.Codec,
                        stream.Channels,
                        stream.DeclaredLanguage,
                        stream.DeclaredLanguage,
                        0.90,
                        "stage1-explicit-tag",
                        CoverageClassification: cls));
                    if (stream.StreamType == "ebook_text")
                    {
                        verifiedAudioLangs.Add(stream.DeclaredLanguage);
                    }
                    else
                    {
                        verifiedAnySubs.Add(stream.DeclaredLanguage);
                        if (cls == "full") verifiedFullSubs.Add(stream.DeclaredLanguage);
                    }
                    continue;
                }

                var isBitmap = stream.IsBitmapSubtitle;
                var explicitSubNorm = stream.DeclaredLanguage;
                if (isBitmap && (stream.BitmapFrames is null || stream.BitmapFrames.Count == 0) && !string.IsNullOrWhiteSpace(explicitSubNorm) && explicitSubNorm != "und")
                {
                    var bitmapCoverageClass = stream.IsForcedDisposition ? "forced" : "full_dialogue";
                    var bitmapDetLang = explicitSubNorm;
                    var bitmapConf = 0.80;
                    var bitmapMethod = "stage1-bitmap-explicit-tag";

                    streamReports.Add(new VerifiedStreamReport(
                        StreamIndex: stream.StreamIndex,
                        StreamType: stream.StreamType,
                        Codec: stream.Codec,
                        Channels: stream.Channels,
                        DeclaredLanguage: stream.DeclaredLanguage,
                        DetectedLanguage: bitmapDetLang,
                        Confidence: bitmapConf,
                        Method: bitmapMethod,
                        CueCount: null,
                        CoverageClassification: bitmapCoverageClass));

                    verifiedAnySubs.Add(bitmapDetLang);
                    if (bitmapCoverageClass is "full" or "full_dialogue")
                    {
                        verifiedFullSubs.Add(bitmapDetLang);
                    }
                    continue;
                }

                // Stage 2: Interior Text / Subtitle LID (+ Bitmap OCR for PGS/VobSub)
                var textPayload = stream.InlineTextPayload ?? "";
                var method = "lingua-ngram+script";
                if (string.IsNullOrWhiteSpace(textPayload) && stream.BitmapFrames is { Count: > 0 })
                {
                    textPayload = await subtitleOcr.ExtractBitmapSubtitleTextAsync(stream.BitmapFrames, ct);
                    method = "tesseract-ocr+lingua";
                }

                var isSub = stream.StreamType == "subtitle";
                var (cueCount, coverageClass, interiorSlice) = textDetector.AnalyzeSubtitleOrBookPayload(textPayload, isSub);
                if (stream.IsForcedDisposition && isSub)
                {
                    coverageClass = "forced";
                }

                var (detLang, conf) = string.IsNullOrWhiteSpace(interiorSlice)
                    ? (stream.DeclaredLanguage, stream.DeclaredLanguage == "und" ? 0.40 : 0.82)
                    : textDetector.IdentifyTextLanguage(interiorSlice);

                streamReports.Add(new VerifiedStreamReport(
                    StreamIndex: stream.StreamIndex,
                    StreamType: stream.StreamType,
                    Codec: stream.Codec,
                    Channels: stream.Channels,
                    DeclaredLanguage: stream.DeclaredLanguage,
                    DetectedLanguage: detLang,
                    Confidence: conf,
                    Method: method,
                    CueCount: isSub ? cueCount : null,
                    CoverageClassification: coverageClass));

                if (stream.StreamType == "ebook_text" && detLang != "und")
                {
                    verifiedAudioLangs.Add(detLang);
                }
                else if (isSub && detLang != "und")
                {
                    verifiedAnySubs.Add(detLang);
                    if (coverageClass is "full" or "full_dialogue")
                    {
                        verifiedFullSubs.Add(detLang);
                    }
                }
            }
            else if (stream.StreamType == "audio")
            {
                audioTrackOrdinal++;
                if (trustExplicitTagsOnly && stream.DeclaredLanguage != "und")
                {
                    verifiedAudioLangs.Add(stream.DeclaredLanguage);
                    streamReports.Add(new VerifiedStreamReport(
                        stream.StreamIndex,
                        "audio",
                        stream.Codec,
                        stream.Channels,
                        stream.DeclaredLanguage,
                        stream.DeclaredLanguage,
                        0.90,
                        "stage1-explicit-tag"));
                    continue;
                }

                // Stage 3: Stratified 5-Window Speech VAD + Neural Speech LID
                var windows = await speechAdapter.AnalyzeAudioStreamAsync(
                    request.StagedFilePath,
                    stream,
                    probe.DurationSeconds ?? request.ExpectedRuntimeSeconds ?? 3600.0,
                    ct);

                var (aggLang, aggConf, isHybridExtendedDub) = AggregateSpeechWindows(windows, origLang);

                // Acoustic Duplicate Fake-Dub Check across previously inspected audio streams!
                var currentPcm = windows.FirstOrDefault(w => w.PcmFingerprint is not null)?.PcmFingerprint ?? stream.AudioWaveformPreview;
                var isFakeDuplicateDub = false;
                if (currentPcm is not null)
                {
                    foreach (var prev in audioWaveforms)
                    {
                        if (!string.Equals(prev.Lang, aggLang, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(prev.Lang, stream.DeclaredLanguage, StringComparison.OrdinalIgnoreCase))
                        {
                            var corr = DefaultAudioSpeechLidAdapter.ComputePearsonWaveformCorrelation(prev.Pcm, currentPcm);
                            if (corr >= fakeDubCorrelationThreshold)
                            {
                                isFakeDuplicateDub = true;
                                aggLang = prev.Lang;
                                break;
                            }
                        }
                    }

                    audioWaveforms.Add((stream.StreamIndex, aggLang, currentPcm));
                }

                string? appliedPatch = null;
                if (!request.InPlaceLibraryScan && !isFakeDuplicateDub && stream.DeclaredLanguage == "und" && aggLang != "und" && aggConf >= autoPatchHeaderConfidence)
                {
                    var iso3 = LanguageCodeNormalizer.ToIso6392ThreeLetter(aggLang);
                    if (await headerPatcher.TryPatchAudioTrackLanguageAsync(request.StagedFilePath, audioTrackOrdinal, iso3, ct))
                    {
                        appliedPatch = iso3;
                        headerAutoPatched = true;
                    }
                }

                if (!isFakeDuplicateDub && aggLang != "und")
                {
                    verifiedAudioLangs.Add(aggLang);
                }

                streamReports.Add(new VerifiedStreamReport(
                    StreamIndex: stream.StreamIndex,
                    StreamType: "audio",
                    Codec: stream.Codec,
                    Channels: stream.Channels,
                    DeclaredLanguage: stream.DeclaredLanguage,
                    DetectedLanguage: aggLang,
                    Confidence: aggConf,
                    Method: "silero-vad+whisper-ggml",
                    IsHybridExtendedCutDub: isHybridExtendedDub,
                    IsDuplicateFakeDub: isFakeDuplicateDub,
                    AppliedTagPatch: appliedPatch,
                    Windows: windows));
            }
        }

        // Stage 4: Policy Verdict Matrix
        var overallConf = streamReports.Count == 0
            ? 0.50
            : Math.Round(streamReports.Average(s => s.Confidence), 3);

        var hasFakeDub = streamReports.Any(s => s.IsDuplicateFakeDub);
        var missingExpectedAudio = expectedAudio.Where(req => !verifiedAudioLangs.Contains(req)).ToList();
        var subPool = request.RequireFullSubtitles ? verifiedFullSubs : verifiedAnySubs;
        var missingExpectedSubs = expectedSubs.Where(req => !subPool.Contains(req)).ToList();
        var contradictingDeclared = expectedAudio.Count == 0 && expectedSubs.Count == 0
            ? streamReports.FirstOrDefault(s =>
                s.DeclaredLanguage != "und" &&
                s.DetectedLanguage != "und" &&
                s.Confidence >= autoPassConfidence &&
                !string.Equals(s.DeclaredLanguage, s.DetectedLanguage, StringComparison.OrdinalIgnoreCase))
            : null;

        string verdict;
        string? failureReason = null;

        if (hasFakeDub)
        {
            verdict = VerificationVerdicts.VerifiedFail;
            failureReason = "Detected duplicate fake-dub audio waveform across distinct language tracks.";
        }
        else if (contradictingDeclared is not null)
        {
            verdict = VerificationVerdicts.VerifiedFail;
            failureReason = $"LanguageMismatch [Declared: {contradictingDeclared.DeclaredLanguage}; Detected: {contradictingDeclared.DetectedLanguage} ({contradictingDeclared.Confidence:F2})]";
        }
        else if (missingExpectedAudio.Count > 0 || missingExpectedSubs.Count > 0)
        {
            if ((overallConf >= quarantineMinConfidence && overallConf < autoPassConfidence) || streamReports.All(s => s.DetectedLanguage == "und"))
            {
                verdict = VerificationVerdicts.QuarantineReview;
                failureReason = $"Ambiguous / low-confidence language detection ({overallConf:F2}) while verifying [{string.Join(',', expectedAudio.Concat(expectedSubs))}].";
            }
            else
            {
                verdict = VerificationVerdicts.VerifiedFail;
                var missingDesc = string.Join(',', missingExpectedAudio.Concat(missingExpectedSubs.Select(s => $"sub:{s}")));
                var actualDesc = string.Join(',', verifiedAudioLangs.Concat(verifiedAnySubs.Select(s => $"sub:{s}")));
                failureReason = $"LanguageMismatch [Expected missing: {missingDesc}; Actual: {actualDesc}]";
            }
        }
        else if (overallConf < autoPassConfidence)
        {
            verdict = VerificationVerdicts.QuarantineReview;
            failureReason = $"Confidence {overallConf:F2} is below automatic pass threshold ({autoPassConfidence:F2}).";
        }
        else
        {
            verdict = headerAutoPatched
                ? VerificationVerdicts.VerifiedPassAutoTagged
                : VerificationVerdicts.VerifiedPass;
        }

        return await PersistReportAndActAsync(
            request,
            verdict,
            overallConf,
            headerAutoPatched,
            verifiedAudioLangs.OrderBy(x => x).ToList(),
            verifiedAnySubs.OrderBy(x => x).ToList(),
            streamReports,
            failureReason,
            Math.Max(1, sw.ElapsedMilliseconds),
            ct,
            failureCode: verdict == VerificationVerdicts.VerifiedFail ? "language_mismatch" : null);
    }

    public async Task<IReadOnlyList<MediaFileVerificationReport>> ListRecentReportsAsync(
        int limit = 50,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<string>(
            "SELECT report_json FROM verification_jobs ORDER BY id DESC LIMIT @Limit;",
            new { Limit = Math.Clamp(limit, 1, 200) });

        return rows
            .Select(r => JsonSerializer.Deserialize<MediaFileVerificationReport>(r, JsonOpts))
            .Where(r => r is not null)
            .Cast<MediaFileVerificationReport>()
            .ToList();
    }

    public async Task<IReadOnlyList<QuarantinedImportSummary>> ListQuarantineAsync(
        string? statusFilter = null,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = string.IsNullOrWhiteSpace(statusFilter)
            ? await conn.QueryAsync<QuarantineRow>("SELECT * FROM quarantined_imports ORDER BY id DESC;")
            : await conn.QueryAsync<QuarantineRow>(
                "SELECT * FROM quarantined_imports WHERE status = @Status ORDER BY id DESC;",
                new { Status = statusFilter.Trim().ToLowerInvariant() });

        return rows.Select(MapQuarantine).ToList();
    }

    public async Task<QuarantinedImportSummary> ApproveQuarantineAsync(
        long quarantineId,
        long reviewerUserId,
        string confirmedLanguage,
        CancellationToken ct = default)
    {
        var item = await GetQuarantineByIdAsync(quarantineId, ct)
            ?? throw new KeyNotFoundException($"Quarantined import #{quarantineId} not found.");

        if (item.Status.StartsWith("rejected", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Quarantined import #{quarantineId} has already been rejected ({item.Status}) and cannot be approved.");
        }

        if (string.IsNullOrWhiteSpace(confirmedLanguage))
        {
            throw new ArgumentException("A valid confirmed audio language code is required.", nameof(confirmedLanguage));
        }

        var normLang = LanguageCodeNormalizer.NormalizeToIso2(confirmedLanguage);
        if (string.Equals(normLang, "und", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unrecognized or undetermined language code '{confirmedLanguage}'.", nameof(confirmedLanguage));
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var detJson = JsonSerializer.Serialize(new[] { normLang }, JsonOpts);
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                UPDATE quarantined_imports
                SET status = 'approved',
                    reviewed_by_user_id = @ReviewerId,
                    reviewed_at = @Now,
                    detected_languages_json = @DetJson
                WHERE id = @Id;
                """,
                new { Id = quarantineId, ReviewerId = reviewerUserId, Now = now, DetJson = detJson },
                transaction: tx),
            ct);

        return (await GetQuarantineByIdAsync(quarantineId, ct))!;
    }

    public async Task<QuarantinedImportSummary> RejectAndBlocklistQuarantineAsync(
        long quarantineId,
        long reviewerUserId,
        CancellationToken ct = default)
    {
        var item = await GetQuarantineByIdAsync(quarantineId, ct)
            ?? throw new KeyNotFoundException($"Quarantined import #{quarantineId} not found.");

        if (string.Equals(item.Status, "approved", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Quarantined import #{quarantineId} has already been approved and cannot be rejected.");
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE quarantined_imports
                    SET status = 'rejected_blocklisted',
                        reviewed_by_user_id = @ReviewerId,
                        reviewed_at = @Now
                    WHERE id = @Id;
                    """,
                    new { Id = quarantineId, ReviewerId = reviewerUserId, Now = now },
                    transaction: tx);

                var resolvedQuarantineHash = !string.IsNullOrWhiteSpace(item.InfoHash)
                    ? item.InfoHash.Trim().ToLowerInvariant()
                    : Managerr.Downloaders.ReleaseBlocklistSnapshot.ComputeNzbKeyFromToken(
                        $"quarantine:{Managerr.Downloaders.ReleaseBlocklistSnapshot.NormalizeTitleKey(item.ReleaseTitle)}");
                var quarantineProtocol = Managerr.Downloaders.ReleaseBlocklistSnapshot.IsSyntheticOrUsenetHash(resolvedQuarantineHash)
                    ? "usenet"
                    : "torrent";
                await conn.ExecuteAsync(
                    """
                    INSERT OR REPLACE INTO recurring_blocklist_hashes (
                        info_hash, title, reason, blocked_at, protocol, normalized_title
                    ) VALUES (
                        @Hash, @Title, @Reason, @Now, @Proto, @NormTitle
                    );
                    """,
                    new
                    {
                        Hash = resolvedQuarantineHash,
                        Title = item.ReleaseTitle,
                        Reason = $"Quarantine rejected: {item.Reason}",
                        Now = now,
                        Proto = quarantineProtocol,
                        NormTitle = Managerr.Downloaders.ReleaseBlocklistSnapshot.NormalizeTitleKey(item.ReleaseTitle),
                    },
                    transaction: tx);
            },
            ct);

        return (await GetQuarantineByIdAsync(quarantineId, ct))!;
    }

    private async Task<MediaFileVerificationReport> PersistReportAndActAsync(
        RunVerificationRequest request,
        string verdict,
        double overallConfidence,
        bool headerPatched,
        IReadOnlyList<string> audioLangs,
        IReadOnlyList<string> subLangs,
        IReadOnlyList<VerifiedStreamReport> streams,
        string? failureReason,
        long elapsedMs,
        CancellationToken ct,
        string? failureCode = null,
        int? matchedPolicyTier = null,
        string? matchedPolicyTierLabel = null,
        bool? satisfiesLanguageCutoff = null)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var hashBlocklisted = false;
        long? quarantineId = null;

        if (verdict == VerificationVerdicts.VerifiedFail && string.IsNullOrWhiteSpace(failureCode))
        {
            failureCode = "language_mismatch";
        }

        // First insert job row to obtain JobId
        var jobId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO verification_jobs (
                    unit_id, media_file_id, staged_file_path, media_vertical, verdict,
                    overall_confidence, header_auto_patched,
                    detected_audio_langs_json, detected_sub_langs_json,
                    report_json, duration_ms, created_at
                ) VALUES (
                    @UnitId, @MediaFileId, @Path, @Vertical, @Verdict,
                    @Conf, @Patched,
                    @AudioJson, @SubJson,
                    '{}', @DurMs, @Now
                );
                SELECT last_insert_rowid();
                """,
                new
                {
                    request.UnitId,
                    MediaFileId = request.MediaFileId,
                    Path = request.StagedFilePath,
                    Vertical = request.MediaVertical,
                    Verdict = verdict,
                    Conf = overallConfidence,
                    Patched = headerPatched ? 1 : 0,
                    AudioJson = JsonSerializer.Serialize(audioLangs, JsonOpts),
                    SubJson = JsonSerializer.Serialize(subLangs, JsonOpts),
                    DurMs = elapsedMs,
                    Now = now,
                },
                transaction: tx),
            ct);

        if (!request.InPlaceLibraryScan &&
            verdict == VerificationVerdicts.VerifiedFail &&
            (!string.IsNullOrWhiteSpace(request.InfoHash) || !string.IsNullOrWhiteSpace(request.ReleaseTitle)))
        {
            var effTitle = string.IsNullOrWhiteSpace(request.ReleaseTitle)
                ? Path.GetFileName(request.StagedFilePath)
                : request.ReleaseTitle;
            var effHash = !string.IsNullOrWhiteSpace(request.InfoHash)
                ? request.InfoHash.Trim().ToLowerInvariant()
                : Managerr.Downloaders.ReleaseBlocklistSnapshot.ComputeNzbKeyFromToken(
                    $"verifiedfail:{Managerr.Downloaders.ReleaseBlocklistSnapshot.NormalizeTitleKey(effTitle)}");
            var effProto = Managerr.Downloaders.ReleaseBlocklistSnapshot.IsSyntheticOrUsenetHash(effHash)
                ? "usenet"
                : "torrent";

            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                (conn, tx) => conn.ExecuteAsync(
                    """
                    INSERT OR REPLACE INTO recurring_blocklist_hashes (
                        info_hash, title, reason, blocked_at, protocol, normalized_title
                    ) VALUES (
                        @Hash, @Title, @Reason, @Now, @Proto, @NormTitle
                    );
                    """,
                    new
                    {
                        Hash = effHash,
                        Title = effTitle,
                        Reason = failureReason ?? "LanguageMismatch",
                        Now = now,
                        Proto = effProto,
                        NormTitle = Managerr.Downloaders.ReleaseBlocklistSnapshot.NormalizeTitleKey(effTitle),
                    },
                    transaction: tx),
                ct);
            hashBlocklisted = true;
        }
        else if (!request.InPlaceLibraryScan && verdict == VerificationVerdicts.QuarantineReview)
        {
            var snippets = await headerPatcher.GenerateOpusQuarantineSnippetsAsync(request.StagedFilePath, jobId, ct);
            var quarantineArgs = new
            {
                JobId = jobId,
                request.UnitId,
                Title = string.IsNullOrWhiteSpace(request.ReleaseTitle) ? Path.GetFileName(request.StagedFilePath) : request.ReleaseTitle,
                Hash = request.InfoHash?.Trim().ToLowerInvariant(),
                Path = request.StagedFilePath,
                ExpJson = JsonSerializer.Serialize((request.ExpectedAudioLanguages ?? []).Concat(request.ExpectedSubtitleLanguages ?? []).ToArray(), JsonOpts),
                DetJson = JsonSerializer.Serialize(audioLangs.Concat(subLangs).ToArray(), JsonOpts),
                Conf = overallConfidence,
                Reason = failureReason ?? "Borderline confidence",
                SnippetsJson = JsonSerializer.Serialize(snippets, JsonOpts),
                Now = now,
            };

            // One open review per download: re-verifying (CDH re-poll, manual retry) refreshes the existing entry
            var (qId, supersededSnippetsJson) = await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                async (conn, tx) =>
                {
                    var existing = await conn.QueryFirstOrDefaultAsync<QuarantineRow>(
                        """
                        SELECT * FROM quarantined_imports
                        WHERE status = 'quarantined'
                          AND ((@Hash IS NOT NULL AND info_hash = @Hash) OR staged_file_path = @Path)
                        ORDER BY id DESC LIMIT 1;
                        """,
                        quarantineArgs,
                        transaction: tx);

                    if (existing is not null)
                    {
                        await conn.ExecuteAsync(
                            """
                            UPDATE quarantined_imports
                            SET verification_job_id = @JobId, unit_id = @UnitId, release_title = @Title,
                                info_hash = @Hash, staged_file_path = @Path,
                                expected_languages_json = @ExpJson, detected_languages_json = @DetJson,
                                confidence = @Conf, reason = @Reason, snippet_paths_json = @SnippetsJson
                            WHERE id = @Id;
                            """,
                            new
                            {
                                quarantineArgs.JobId, quarantineArgs.UnitId, quarantineArgs.Title, quarantineArgs.Hash,
                                quarantineArgs.Path, quarantineArgs.ExpJson, quarantineArgs.DetJson,
                                quarantineArgs.Conf, quarantineArgs.Reason, quarantineArgs.SnippetsJson,
                                existing.Id,
                            },
                            transaction: tx);
                        return (existing.Id, existing.SnippetPathsJson);
                    }

                    var newId = await conn.ExecuteScalarAsync<long>(
                        """
                        INSERT INTO quarantined_imports (
                            verification_job_id, unit_id, release_title, info_hash,
                            staged_file_path, expected_languages_json, detected_languages_json,
                            confidence, reason, snippet_paths_json, status, created_at
                        ) VALUES (
                            @JobId, @UnitId, @Title, @Hash,
                            @Path, @ExpJson, @DetJson,
                            @Conf, @Reason, @SnippetsJson, 'quarantined', @Now
                        );
                        SELECT last_insert_rowid();
                        """,
                        quarantineArgs,
                        transaction: tx);
                    return (newId, (string?)null);
                },
                ct);
            quarantineId = qId;

            if (supersededSnippetsJson is not null)
            {
                DeleteSupersededSnippets(supersededSnippetsJson, keep: snippets);
            }
        }
        else if (!request.InPlaceLibraryScan &&
                 (verdict is VerificationVerdicts.VerifiedPass or VerificationVerdicts.VerifiedPassAutoTagged or VerificationVerdicts.MusicPolicyPass) &&
                 request.UnitId.HasValue)
        {
            await catalogService.UpdateUnitAsync(
                request.UnitId.Value,
                new UpdateMediaUnitRequest(LanguageCutoffMet: true),
                ct);
        }

        var report = new MediaFileVerificationReport(
            JobId: jobId,
            UnitId: request.UnitId,
            StagedFilePath: request.StagedFilePath,
            MediaVertical: request.MediaVertical,
            Verdict: verdict,
            OverallConfidence: overallConfidence,
            HeaderAutoPatched: headerPatched,
            ExistingFilePreservedOnFailure: verdict is VerificationVerdicts.VerifiedFail or VerificationVerdicts.QuarantineReview,
            HashBlocklisted: hashBlocklisted,
            QuarantineId: quarantineId,
            DurationMs: elapsedMs,
            VerifiedAudioLanguages: audioLangs,
            VerifiedSubtitleLanguages: subLangs,
            Streams: streams,
            VerifiedAt: now,
            FailureReason: failureReason,
            FailureCode: failureCode,
            MatchedPolicyTier: matchedPolicyTier,
            MatchedPolicyTierLabel: matchedPolicyTierLabel,
            SatisfiesLanguageCutoff: satisfiesLanguageCutoff);

        var reportJson = JsonSerializer.Serialize(report, JsonOpts);
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                "UPDATE verification_jobs SET report_json = @Json WHERE id = @Id;",
                new { Id = jobId, Json = reportJson },
                transaction: tx),
            ct);

        return report;
    }

    private static (string Language, double Confidence, bool IsHybridExtendedDub) AggregateSpeechWindows(
        IReadOnlyList<SpeechWindowSample> windows,
        string origLang)
    {
        var speechWindows = windows.Where(w => w.SpeechSec >= 1.2 && w.DetectedLanguage != "und").ToList();
        if (speechWindows.Count == 0)
        {
            var nonSpeechConcrete = windows
                .Where(w => w.DetectedLanguage != "und" && w.Probability >= 0.80)
                .ToList();
            if (nonSpeechConcrete.Count > 0)
            {
                return (
                    nonSpeechConcrete[0].DetectedLanguage.ToLowerInvariant(),
                    Math.Round(nonSpeechConcrete.Average(w => w.Probability), 3),
                    false);
            }

            var fallbackConf = windows.Count > 0
                ? Math.Clamp(Math.Round(windows.Average(w => w.Probability), 3), 0.25, 0.75)
                : 0.35;
            return ("und", fallbackConf, false);
        }

        var totalSpeechWeight = speechWindows.Sum(w => Math.Max(1.0, w.SpeechSec));
        var grouped = speechWindows
            .GroupBy(w => w.DetectedLanguage, StringComparer.OrdinalIgnoreCase)
            .Select(g => (
                Lang: g.Key.ToLowerInvariant(),
                Count: g.Count(),
                AvgProb: g.Average(x => x.Probability),
                WeightedSum: g.Sum(x => x.Probability * Math.Max(1.0, x.SpeechSec))))
            .OrderByDescending(g => g.WeightedSum)
            .ToList();

        var winner = grouped[0];

        // German / Localized Extended-Cut Hybrid Dub Rule (Section 3.3 of docs/05):
        // >= 3/5 windows in dubbed language (e.g. 'de', p > 0.85) + 1..2/5 windows in original ('en') -> Hybrid Extended Cut Dub!
        var isHybrid = winner.Count >= 3 &&
                       winner.AvgProb >= 0.85 &&
                       !string.Equals(winner.Lang, origLang, StringComparison.OrdinalIgnoreCase) &&
                       grouped.Any(g => string.Equals(g.Lang, origLang, StringComparison.OrdinalIgnoreCase));

        var baseConf = isHybrid
            ? winner.AvgProb
            : winner.WeightedSum / totalSpeechWeight;

        // Unanimous multi-window agreement across >= 2 independent speech windows boosts confidence slightly
        if (!isHybrid && grouped.Count == 1 && winner.Count >= 2 && baseConf >= 0.80)
        {
            baseConf = Math.Min(0.96, baseConf + 0.05);
        }

        return (winner.Lang, Math.Clamp(Math.Round(baseConf, 3), 0.0, 0.999), isHybrid);
    }

    private async Task<QuarantinedImportSummary?> GetQuarantineByIdAsync(long id, CancellationToken ct)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await conn.QuerySingleOrDefaultAsync<QuarantineRow>(
            "SELECT * FROM quarantined_imports WHERE id = @Id;",
            new { Id = id });
        return row is null ? null : MapQuarantine(row);
    }

    private static QuarantinedImportSummary MapQuarantine(QuarantineRow row) =>
        new(
            Id: row.Id,
            VerificationJobId: row.VerificationJobId,
            UnitId: row.UnitId,
            ReleaseTitle: row.ReleaseTitle,
            InfoHash: row.InfoHash,
            StagedFilePath: row.StagedFilePath,
            ExpectedLanguages: JsonSerializer.Deserialize<string[]>(row.ExpectedLanguagesJson, JsonOpts) ?? [],
            DetectedLanguages: JsonSerializer.Deserialize<string[]>(row.DetectedLanguagesJson, JsonOpts) ?? [],
            Confidence: row.Confidence,
            Reason: row.Reason,
            SnippetPaths: JsonSerializer.Deserialize<string[]>(row.SnippetPathsJson, JsonOpts) ?? [],
            Status: row.Status,
            ReviewedByUserId: row.ReviewedByUserId,
            ReviewedAt: row.ReviewedAt,
            CreatedAt: row.CreatedAt);

    private async Task<(double AutoPass, double QuarantineMin, double AutoPatchHeader, double FakeDubCorr)> LoadVerificationThresholdsAsync(CancellationToken ct)
    {
        if (appSettings is null)
        {
            return (0.80, 0.45, 0.85, 0.97);
        }

        var autoPass = ParseThreshold(await appSettings.GetAsync("verification.auto_pass_confidence", ct), 0.80, 0.50, 0.99);
        var quarantineMin = ParseThreshold(await appSettings.GetAsync("verification.quarantine_min_confidence", ct), 0.45, 0.10, Math.Max(0.10, autoPass - 0.01));
        var autoPatch = ParseThreshold(await appSettings.GetAsync("verification.auto_patch_header_confidence", ct), 0.85, 0.50, 0.99);
        var fakeDub = ParseThreshold(await appSettings.GetAsync("verification.fake_dub_correlation_threshold", ct), 0.97, 0.80, 0.999);
        return (autoPass, quarantineMin, autoPatch, fakeDub);
    }

    private static double ParseThreshold(string? raw, double fallback, double min, double max) =>
        !string.IsNullOrWhiteSpace(raw) && double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var val)
            ? Math.Clamp(val, min, max)
            : fallback;

    private static string ResolveToken(string token, string origLang) =>
        string.Equals(token, "original", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(token, "orig", StringComparison.OrdinalIgnoreCase)
            ? origLang
            : LanguageCodeNormalizer.NormalizeToIso2(token);

    private static void DeleteSupersededSnippets(string snippetPathsJson, IReadOnlyCollection<string> keep)
    {
        var paths = JsonSerializer.Deserialize<string[]>(snippetPathsJson, JsonOpts) ?? [];
        foreach (var path in paths.Where(p => !keep.Contains(p)))
        {
            try
            {
                File.Delete(path);
                var dir = Path.GetDirectoryName(path);
                if (dir is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch
            {
                // Snippet cleanup is best-effort; a leftover file never blocks the review
            }
        }
    }

    private sealed class QuarantineRow
    {
        public long Id { get; init; }
        public long VerificationJobId { get; init; }
        public long? UnitId { get; init; }
        public string ReleaseTitle { get; init; } = "";
        public string? InfoHash { get; init; }
        public string StagedFilePath { get; init; } = "";
        public string ExpectedLanguagesJson { get; init; } = "[]";
        public string DetectedLanguagesJson { get; init; } = "[]";
        public double Confidence { get; init; }
        public string Reason { get; init; } = "";
        public string SnippetPathsJson { get; init; } = "[]";
        public string Status { get; init; } = "quarantined";
        public long? ReviewedByUserId { get; init; }
        public string? ReviewedAt { get; init; }
        public string CreatedAt { get; init; } = "";
    }
}
