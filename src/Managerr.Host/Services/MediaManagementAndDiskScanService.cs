using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Managerr.Catalog;
using Managerr.Common.Media;
using Managerr.Datastore;
using Managerr.Downloaders.Organizer;
using Managerr.Downloaders.Parser;
using Managerr.Metadata;
using Managerr.Profiles;
using Managerr.Verification;

namespace Managerr.Host.Services;

public sealed record BulkScanAndVerifyWorksRequest(IReadOnlyList<long> WorkIds);

public sealed record WorkLanguageScanReportDto(
    long WorkId,
    string WorkTitle,
    int FilesScannedCount,
    int AutoAdoptedCount,
    int MissingRemovedCount,
    int FilesLanguageCheckedCount,
    int VerifiedPassCount,
    int LanguageMismatchCount,
    long DurationMs,
    IReadOnlyList<string> Messages);

public sealed record BulkWorkLanguageScanReportDto(
    int TargetedWorksCount,
    int TotalFilesScannedCount,
    int TotalAutoAdoptedCount,
    int TotalMissingRemovedCount,
    int TotalFilesLanguageCheckedCount,
    int TotalVerifiedPassCount,
    int TotalLanguageMismatchCount,
    long DurationMs,
    IReadOnlyList<WorkLanguageScanReportDto> Reports);

public sealed record NamingTokenDescriptorDto(
    string Token,
    string Description,
    string Example);

public sealed record NamingConfigDto(
    bool RenameMediaFiles,
    string ColonReplacement,
    IReadOnlyDictionary<string, string> Templates,
    IReadOnlyList<NamingTokenDescriptorDto> AvailableTokens,
    bool UseHardlinks = false);

public sealed record UpdateNamingConfigRequest(
    bool? RenameMediaFiles = null,
    string? ColonReplacement = null,
    IReadOnlyDictionary<string, string>? Templates = null,
    bool? UseHardlinks = null);

public sealed record NamingPreviewRequest(
    string Vertical,
    string? Template = null,
    string? ColonReplacement = null);

public sealed record NamingPreviewResultDto(
    string Vertical,
    string TemplateUsed,
    string SampleRelativePath);

public sealed record DiskScanReportDto(
    long LibraryId,
    string LibraryName,
    long? WorkId,
    int ScannedFilesCount,
    int AlreadyLinkedCount,
    int AutoAdoptedCount,
    int MissingRemovedCount,
    int UnmappedCount,
    long DurationMs,
    IReadOnlyList<string> Messages);

public sealed record UnmappedMediaFileDto(
    long Id,
    long LibraryId,
    string LibraryName,
    string MediaVertical,
    string FullPath,
    string RelativePath,
    string FileName,
    long SizeBytes,
    string ParsedTitle,
    int? ParsedSeasonNumber,
    IReadOnlyList<int> ParsedEpisodeNumbers,
    string ParsedQuality,
    IReadOnlyList<string> ParsedLanguages,
    long? SuggestedWorkId,
    string? SuggestedWorkTitle,
    string Reason,
    string DiscoveredAt);

public sealed record MapUnmappedFileItemRequest(
    long UnmappedFileId,
    long WorkId,
    int? SeasonNumber = null,
    IReadOnlyList<int>? EpisodeNumbers = null,
    IReadOnlyList<long>? UnitIds = null,
    string? QualityName = null,
    IReadOnlyList<string>? Languages = null,
    bool RenameToTemplate = true);

public sealed record MapUnmappedFilesRequest(
    IReadOnlyList<MapUnmappedFileItemRequest> Items);

public sealed record MapUnmappedFilesReportDto(
    int MappedCount,
    int FailedCount,
    IReadOnlyList<string> Messages);

public sealed record RenameFilePreviewItemDto(
    long MediaFileId,
    long WorkId,
    string WorkTitle,
    long UnitId,
    string EpisodeCode,
    string CurrentRelativePath,
    string NewRelativePath,
    bool NeedsRename,
    bool ExistsOnDisk);

public sealed record RenameExecuteRequest(
    IReadOnlyList<long>? MediaFileIds = null);

public sealed record RenameExecuteReportDto(
    int EvaluatedCount,
    int RenamedCount,
    int FailedCount,
    IReadOnlyList<string> Messages);

public interface IMediaManagementAndDiskScanService
{
    Task<NamingConfigDto> GetNamingConfigAsync(CancellationToken ct = default);
    Task<NamingConfigDto> UpdateNamingConfigAsync(UpdateNamingConfigRequest request, CancellationToken ct = default);
    Task<NamingPreviewResultDto> PreviewNamingTemplateAsync(NamingPreviewRequest request, CancellationToken ct = default);
    Task<(bool RenameEnabled, string ColonReplacement, string? TemplateOverride)> GetOrganizerNamingSettingsAsync(string vertical, CancellationToken ct = default);

    Task<DiskScanReportDto> ScanLibraryAsync(long libraryId, CancellationToken ct = default);
    Task<DiskScanReportDto> ScanWorkAsync(long workId, CancellationToken ct = default);
    Task<IReadOnlyList<DiskScanReportDto>> ScanAllLibrariesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<UnmappedMediaFileDto>> ListUnmappedFilesAsync(long? libraryId = null, CancellationToken ct = default);
    Task<MapUnmappedFilesReportDto> MapUnmappedFilesAsync(MapUnmappedFilesRequest request, CancellationToken ct = default);
    Task<bool> DismissUnmappedFileAsync(long unmappedFileId, bool deleteFromDisk = false, CancellationToken ct = default);

    Task<IReadOnlyList<RenameFilePreviewItemDto>> PreviewWorkRenameAsync(long workId, CancellationToken ct = default);
    Task<IReadOnlyList<RenameFilePreviewItemDto>> PreviewLibraryRenameAsync(long libraryId, CancellationToken ct = default);
    Task<RenameExecuteReportDto> ExecuteWorkRenameAsync(long workId, IReadOnlyList<long>? mediaFileIds = null, CancellationToken ct = default);
    Task<RenameExecuteReportDto> ExecuteLibraryRenameAsync(long libraryId, IReadOnlyList<long>? mediaFileIds = null, CancellationToken ct = default);

    Task<WorkLanguageScanReportDto> ScanAndVerifyWorkAsync(long workId, CancellationToken ct = default);
    Task<BulkWorkLanguageScanReportDto> ScanAndVerifyWorksAsync(IReadOnlyList<long> workIds, CancellationToken ct = default);
    Task ReevaluateWorkFilesAgainstPolicyAsync(long workId, CancellationToken ct = default);
}

public sealed partial class MediaManagementAndDiskScanService(
    ISqliteDbFactory dbFactory,
    IPolymorphicCatalogService catalogService,
    IReleaseParserRouter parserRouter,
    IAtomicFileOrganizer fileOrganizer,
    ILanguageVerificationService languageVerifier,
    IComposableProfileService profileService,
    Managerr.Common.Notifications.IMediaLifecycleNotificationSink? notificationSink = null) : IMediaManagementAndDiskScanService, ICatalogDiskScanHook
{
    Task ICatalogDiskScanHook.ScanWorkDiskFilesAsync(long workId, CancellationToken ct) =>
        ScanWorkAsync(workId, ct);
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim VerificationGate = new(2, 2);

    private static readonly string[] VideoExtensions = [".mkv", ".mp4", ".avi", ".m4v", ".ts", ".webm", ".mov"];
    private static readonly string[] MusicExtensions = [".flac", ".mp3", ".m4a", ".opus", ".ogg", ".wav", ".alac"];
    private static readonly string[] BookAndComicExtensions = [".epub", ".pdf", ".mobi", ".azw3", ".m4b", ".mp3", ".cbz", ".cbr"];
    private static readonly string[] SubtitleSidecarExtensions = [".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt"];

    private static readonly IReadOnlyDictionary<string, string> DefaultTemplates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [MetadataVerticals.Tv] = "{Series TitleYear}/Season {Season:00}/{Series TitleYear} - S{Season:00}E{Episode:00} [{Quality Full}]",
        [MetadataVerticals.Movies] = "{Movie TitleYear}/{Movie TitleYear} [{Edition Tags}] [{Quality Full}]",
        [MetadataVerticals.Anime] = "{Series Title}/{Series Title} - {Absolute:000} [{Quality Full}]",
        [MetadataVerticals.Music] = "{Artist Name}/{Album Title} ({Release Year})/{Track:00} - {Track Title}",
        [MetadataVerticals.Ebooks] = "{Author Name}/{Book Title} ({Release Year}) - {Author Name}",
        [MetadataVerticals.Audiobooks] = "{Author Name}/{Book Title} ({Release Year})/{Track:00} - {Track Title}",
        [MetadataVerticals.Comics] = "{Comic Title} ({Release Year})/Issue #{Issue:000}",
    };

    private static readonly IReadOnlyList<NamingTokenDescriptorDto> TokenCatalog =
    [
        new("{Series TitleYear}", "Series title with release year", "Avatar Seven Havens (2026)"),
        new("{Series Title}", "Series clean title", "Avatar Seven Havens"),
        new("{Movie TitleYear}", "Movie title with release year", "Dune Part Two (2024)"),
        new("{Movie Title}", "Movie clean title", "Dune Part Two"),
        new("{Title}", "Generic work clean title", "Dune Part Two"),
        new("{Artist Name}", "Music artist name", "Daft Punk"),
        new("{Author Name}", "Book / comic author name", "Andy Weir"),
        new("{Album Title}", "Music album title", "Random Access Memories"),
        new("{Track:00}", "2-digit padded track number", "01"),
        new("{Track Title}", "Music / audiobook track title", "Give Life Back to Music"),
        new("{Book Title}", "Book or audiobook title", "Project Hail Mary"),
        new("{Comic Title}", "Comic or manga title", "Saga Compendium One"),
        new("{Issue:000}", "3-digit padded comic issue number", "012"),
        new("{Issue:00}", "2-digit padded comic issue number", "12"),
        new("{Release Year}", "4-digit release year", "2026"),
        new("{Year}", "4-digit year alias", "2026"),
        new("{Season:00}", "2-digit padded season number", "01"),
        new("{Episode:00}", "2-digit padded episode number(s)", "13"),
        new("{Absolute:000}", "3-digit anime absolute episode number", "013"),
        new("{Episode Title}", "Episode / unit title", "The Convergence"),
        new("{Quality Full}", "Source + resolution + codec + HDR", "WEB-DL 1080p AVC"),
        new("{Quality Title}", "Source + resolution tier", "WEB-DL-1080p"),
        new("{MediaInfo VideoCodec}", "Detected video codec", "AVC"),
        new("{MediaInfo AudioCodec}", "Detected audio codec", "EAC3"),
        new("{MediaInfo AudioLanguages}", "Bracketed uppercase audio languages", "[DE+EN]"),
        new("{MediaInfo VideoDynamicRangeType}", "HDR / DV format tag", "DV"),
        new("{Custom Formats}", "Matched custom format names", "German DL"),
        new("{Edition Tags}", "Movie edition (Director's Cut, IMAX)", "IMAX"),
        new("{Release Group}", "Scene / P2P release group", "WAREZCX"),
        new("{imdb-{imdbId}}", "Plex/Jellyfin IMDb folder tag", "{imdb-tt1234567}"),
        new("{tmdb-{tmdbId}}", "Plex/Jellyfin TMDb folder tag", "{tmdb-98765}"),
        new("{tvdb-{tvdbId}}", "Plex/Jellyfin TVDB folder tag", "{tvdb-412345}"),
    ];

    [GeneratedRegex(@"(?:season|staffel|s)\s*0*(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderRegex();

    public async Task<NamingConfigDto> GetNamingConfigAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = (await conn.QueryAsync<(string Key, string ValueJson)>(
            "SELECT key AS Key, value AS ValueJson FROM app_settings WHERE key LIKE 'naming.%';")).ToList();

        var map = rows.ToDictionary(r => r.Key, r => UnwrapSettingString(r.ValueJson), StringComparer.OrdinalIgnoreCase);
        var renameFiles = !map.TryGetValue("naming.rename_media_files", out var renRaw) ||
                          !bool.TryParse(renRaw, out var renParsed) ||
                          renParsed;
        var useHardlinks = map.TryGetValue("naming.use_hardlinks", out var hlRaw) &&
                           bool.TryParse(hlRaw, out var hlParsed) &&
                           hlParsed;
        var colonMode = NormalizeColonMode(map.GetValueOrDefault("naming.colon_replacement"));

        var templates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (vertical, defaultTpl) in DefaultTemplates)
        {
            if (map.TryGetValue($"naming.template.{vertical}", out var custom) && !string.IsNullOrWhiteSpace(custom))
            {
                templates[vertical] = custom;
            }
            else
            {
                templates[vertical] = defaultTpl;
            }
        }

        return new NamingConfigDto(renameFiles, colonMode, templates, TokenCatalog, useHardlinks);
    }

    public async Task<NamingConfigDto> UpdateNamingConfigAsync(UpdateNamingConfigRequest request, CancellationToken ct = default)
    {
        var current = await GetNamingConfigAsync(ct);
        var renameFiles = request.RenameMediaFiles ?? current.RenameMediaFiles;
        var useHardlinks = request.UseHardlinks ?? current.UseHardlinks;
        var colonMode = request.ColonReplacement is not null
            ? NormalizeColonMode(request.ColonReplacement)
            : current.ColonReplacement;

        var mergedTemplates = new Dictionary<string, string>(current.Templates, StringComparer.OrdinalIgnoreCase);
        if (request.Templates is not null)
        {
            foreach (var (vertical, tpl) in request.Templates)
            {
                var key = vertical.Trim().ToLowerInvariant();
                if (!DefaultTemplates.ContainsKey(key))
                {
                    continue;
                }

                mergedTemplates[key] = string.IsNullOrWhiteSpace(tpl) ? DefaultTemplates[key] : tpl.Trim();
            }
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                await UpsertSettingAsync(conn, tx, "naming.rename_media_files", renameFiles ? "true" : "false", now);
                await UpsertSettingAsync(conn, tx, "naming.use_hardlinks", useHardlinks ? "true" : "false", now);
                await UpsertSettingAsync(conn, tx, "naming.colon_replacement", colonMode, now);
                foreach (var (vertical, tpl) in mergedTemplates)
                {
                    await UpsertSettingAsync(conn, tx, $"naming.template.{vertical}", tpl, now);
                }
            },
            ct);

        return await GetNamingConfigAsync(ct);
    }

    public async Task<NamingPreviewResultDto> PreviewNamingTemplateAsync(NamingPreviewRequest request, CancellationToken ct = default)
    {
        var cfg = await GetNamingConfigAsync(ct);
        var vertical = string.IsNullOrWhiteSpace(request.Vertical) ? MetadataVerticals.Tv : request.Vertical.Trim().ToLowerInvariant();
        if (!DefaultTemplates.ContainsKey(vertical))
        {
            vertical = MetadataVerticals.Tv;
        }

        var template = !string.IsNullOrWhiteSpace(request.Template)
            ? request.Template!.Trim()
            : cfg.Templates[vertical];
        var colonMode = !string.IsNullOrWhiteSpace(request.ColonReplacement)
            ? NormalizeColonMode(request.ColonReplacement)
            : cfg.ColonReplacement;

        var sampleTitle = vertical switch
        {
            MetadataVerticals.Movies => "Dune: Part Two",
            MetadataVerticals.Music => "Random Access Memories",
            MetadataVerticals.Ebooks or MetadataVerticals.Audiobooks => "Project Hail Mary",
            MetadataVerticals.Comics => "Saga: Compendium One",
            _ => "Avatar: Seven Havens",
        };
        var sampleCreator = vertical switch
        {
            MetadataVerticals.Music => "Daft Punk",
            MetadataVerticals.Ebooks or MetadataVerticals.Audiobooks => "Andy Weir",
            MetadataVerticals.Comics => "Brian K Vaughan",
            _ => null,
        };
        var sampleExt = vertical switch
        {
            MetadataVerticals.Music => ".flac",
            MetadataVerticals.Ebooks => ".epub",
            MetadataVerticals.Audiobooks => ".m4b",
            MetadataVerticals.Comics => ".cbz",
            _ => ".mkv",
        };

        var sampleInfo = new ParsedReleaseInfo(
            RawTitle: sampleTitle,
            MediaVertical: vertical,
            CleanTitle: sampleTitle,
            ArtistOrAuthor: sampleCreator,
            Year: vertical == MetadataVerticals.Movies ? 2024 : 2026,
            SeasonNumber: 1,
            EpisodeNumbers: [13],
            AbsoluteEpisodeNumbers: [13],
            IsFullSeasonPack: false,
            MovieEdition: vertical == MetadataVerticals.Movies ? "IMAX" : null,
            Resolution: vertical is MetadataVerticals.Music or MetadataVerticals.Ebooks or MetadataVerticals.Audiobooks or MetadataVerticals.Comics ? null : "1080p",
            Source: vertical is MetadataVerticals.Music or MetadataVerticals.Ebooks or MetadataVerticals.Audiobooks or MetadataVerticals.Comics ? null : "WEB-DL",
            VideoCodec: vertical is MetadataVerticals.Music or MetadataVerticals.Ebooks or MetadataVerticals.Audiobooks or MetadataVerticals.Comics ? null : "AVC",
            HdrFormat: vertical == MetadataVerticals.Movies ? "DV" : null,
            AudioCodec: vertical is MetadataVerticals.Music or MetadataVerticals.Ebooks or MetadataVerticals.Audiobooks or MetadataVerticals.Comics ? null : "EAC3",
            MusicFormat: vertical == MetadataVerticals.Music ? "FLAC 24bit" : null,
            BookFormat: vertical == MetadataVerticals.Ebooks ? "EPUB" : (vertical == MetadataVerticals.Audiobooks ? "M4B" : (vertical == MetadataVerticals.Comics ? "CBZ" : null)),
            DetectedLanguages: ["de", "en"],
            IsDualAudio: true,
            IsMultiSubs: true,
            ReleaseGroup: "WAREZCX");

        var ctx = new NamingContextMetadata(
            EpisodeTitle: vertical is MetadataVerticals.Music or MetadataVerticals.Audiobooks
                ? "Give Life Back to Music"
                : "The Convergence",
            QualityTitle: "WEB-DL-1080p",
            CustomFormats: ["German DL"],
            ExternalIds: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["imdb"] = "tt15239678",
                ["tmdb"] = "693134",
                ["tvdb"] = "421099",
            },
            ColonReplacement: colonMode,
            AlbumTitle: vertical == MetadataVerticals.Music ? "Random Access Memories" : null,
            UnitNumber: vertical is MetadataVerticals.Music or MetadataVerticals.Audiobooks ? 1 : (vertical == MetadataVerticals.Comics ? 12 : null));

        var rendered = fileOrganizer.RenderRelativeDestinationPathWithContext(sampleInfo, sampleExt, template, ctx);
        return new NamingPreviewResultDto(vertical, template, rendered);
    }

    public async Task<(bool RenameEnabled, string ColonReplacement, string? TemplateOverride)> GetOrganizerNamingSettingsAsync(
        string vertical,
        CancellationToken ct = default)
    {
        var cfg = await GetNamingConfigAsync(ct);
        var key = vertical.Trim().ToLowerInvariant();
        cfg.Templates.TryGetValue(key, out var tpl);
        return (cfg.RenameMediaFiles, cfg.ColonReplacement, tpl);
    }

    public async Task<IReadOnlyList<DiskScanReportDto>> ScanAllLibrariesAsync(CancellationToken ct = default)
    {
        var libs = await catalogService.ListLibrariesAsync(actor: null, ct);
        var reports = new List<DiskScanReportDto>(libs.Count);
        foreach (var lib in libs)
        {
            reports.Add(await ScanLibraryAsync(lib.Id, ct));
        }

        return reports;
    }

    public Task<DiskScanReportDto> ScanLibraryAsync(long libraryId, CancellationToken ct = default) =>
        ScanCoreAsync(libraryId, specificWorkId: null, ct);

    public async Task<DiskScanReportDto> ScanWorkAsync(long workId, CancellationToken ct = default)
    {
        var tree = await catalogService.GetWorkTreeAsync(workId, actor: null, ct)
            ?? throw new KeyNotFoundException($"MediaWork #{workId} not found.");
        return await ScanCoreAsync(tree.Work.LibraryId, specificWorkId: workId, ct);
    }

    public async Task<WorkLanguageScanReportDto> ScanAndVerifyWorkAsync(long workId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var diskReport = await ScanWorkAsync(workId, ct);

        var tree = await catalogService.GetWorkTreeAsync(workId, actor: null, ct)
            ?? throw new KeyNotFoundException($"MediaWork #{workId} not found.");
        var libs = await catalogService.ListLibrariesAsync(actor: null, ct);
        var lib = libs.FirstOrDefault(l => l.Id == tree.Work.LibraryId)
            ?? throw new KeyNotFoundException($"Library #{tree.Work.LibraryId} not found.");

        var defaultProfileId = tree.Work.QualityProfileId ?? lib.DefaultProfileId;
        if (!defaultProfileId.HasValue && lib.MediaType is MetadataVerticals.Movies or MetadataVerticals.Tv or MetadataVerticals.Anime)
        {
            defaultProfileId = await ResolveFallbackProfileIdAsync(ct);
        }

        LanguagePolicySummary? workLanguagePolicy = null;
        if (tree.Work.LanguagePolicyId.HasValue)
        {
            var policies = await profileService.ListLanguagePoliciesAsync(ct);
            workLanguagePolicy = policies.FirstOrDefault(p => p.Id == tree.Work.LanguagePolicyId.Value);
        }

        var profileCache = new Dictionary<long, QualityProfileSummary?>();

        async Task<QualityProfileSummary?> GetCachedProfileAsync(long? pid)
        {
            if (!pid.HasValue)
            {
                return null;
            }

            if (!profileCache.TryGetValue(pid.Value, out var cached))
            {
                cached = await profileService.GetProfileByIdAsync(pid.Value, ct);
                profileCache[pid.Value] = cached;
            }

            return cached;
        }

        var origLang = tree.Work.OriginalLanguage ?? "en";

        var filesChecked = 0;
        var verifiedPass = 0;
        var languageMismatch = 0;
        var messages = new List<string>(diskReport.Messages);
        var fullRoot = Path.GetFullPath(lib.RootFolderPath);
        var rootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var unit in tree.Containers.SelectMany(c => c.Units))
        {
            var unitProfileId = unit.ProfileOverrideId ?? defaultProfileId;
            var unitProfile = await GetCachedProfileAsync(unitProfileId);
            var effectiveLanguagePolicy = workLanguagePolicy ?? unitProfile?.LanguagePolicy;
            var verificationMode = effectiveLanguagePolicy?.VerificationMode ?? "verify_content_on_upgrades_and_ambiguous";

            var unitCheckedFiles = 0;
            var anyFileLangCutoffMet = false;

            foreach (var file in unit.Files)
            {
                var fullPath = Path.GetFullPath(Path.Combine(fullRoot, file.RelativePath));
                if (HasSymlinkEscapeOrTraversal(fullRoot, rootPrefix, fullPath) ||
                    !File.Exists(fullPath))
                {
                    continue;
                }

                filesChecked++;
                unitCheckedFiles++;

                // Pass ONLY unit's required audio & subs, NEVER RetainAudioLangs / RetainSubLangs!
                var expectedAudio = unit.RequiredAudioLangs.ToArray();
                var expectedSubs = unit.RequiredSubLangs.ToArray();

                var sidecarSubs = DetectSidecarSubtitleLanguages(fullPath);

                MediaFileVerificationReport vReport;
                await VerificationGate.WaitAsync(ct);
                try
                {
                    vReport = await languageVerifier.VerifyStagedFileAsync(new RunVerificationRequest(
                        StagedFilePath: fullPath,
                        MediaVertical: lib.MediaType,
                        ReleaseTitle: Path.GetFileName(fullPath),
                        UnitId: unit.Id,
                        ProfileId: unitProfileId,
                        OriginalLanguage: origLang,
                        ExpectedAudioLanguages: expectedAudio,
                        ExpectedSubtitleLanguages: expectedSubs,
                        VerificationMode: verificationMode,
                        InPlaceLibraryScan: true,
                        MediaFileId: file.Id), ct);
                }
                finally
                {
                    VerificationGate.Release();
                }

                // If container is corrupt or file unreadable, mark ProbeError and don't count as language mismatch
                var isProbeError = !string.IsNullOrEmpty(vReport.FailureCode) && !string.Equals(vReport.FailureCode, "language_mismatch", StringComparison.OrdinalIgnoreCase);

                if (isProbeError)
                {
                    await dbFactory.ExecuteWriteAsync(
                        DatabaseKind.Core,
                        (conn, tx) => conn.ExecuteAsync(
                            "UPDATE media_files SET verified_status = 'ProbeError' WHERE id = @FileId;",
                            new { FileId = file.Id },
                            transaction: tx),
                        ct);
                    continue;
                }

                var parsed = ParseDiskFileWithFolderContext(file.RelativePath, Path.GetFileName(fullPath), lib.MediaType);

                var mergedAudio = vReport.VerifiedAudioLanguages;
                if (mergedAudio.Count == 0 && parsed.DetectedLanguages.Count > 0)
                {
                    mergedAudio = parsed.DetectedLanguages;
                }

                var mergedSubs = vReport.VerifiedSubtitleLanguages
                    .Concat(sidecarSubs)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var policyEval = profileService.EvaluateOnDiskLanguagesAgainstPolicy(
                    effectiveLanguagePolicy,
                    origLang,
                    mergedAudio,
                    mergedSubs);

                var explicitAudioSatisfied = expectedAudio.Length == 0 || expectedAudio.All(ea => mergedAudio.Contains(ea, StringComparer.OrdinalIgnoreCase));
                var explicitSubsSatisfied = expectedSubs.Length == 0 || expectedSubs.All(es => mergedSubs.Contains(es, StringComparer.OrdinalIgnoreCase));
                var noDuplicateFakeDub = !vReport.Streams.Any(s => s.IsDuplicateFakeDub);

                var allowed = vReport.Streams.Count > 0
                    && policyEval.PolicyAllowed
                    && explicitAudioSatisfied
                    && explicitSubsSatisfied
                    && noDuplicateFakeDub;
                string verifiedStatus;
                bool satisfiesCutoff;
                if (allowed)
                {
                    verifiedStatus = VerificationVerdicts.VerifiedPass;
                    satisfiesCutoff = policyEval.SatisfiesCutoff;
                    verifiedPass++;
                    anyFileLangCutoffMet |= satisfiesCutoff;
                }
                else
                {
                    verifiedStatus = VerificationVerdicts.VerifiedFail;
                    satisfiesCutoff = false;
                    languageMismatch++;
                }

                var finalFailureReason = allowed ? null : (vReport.Streams.Count == 0 && !string.IsNullOrWhiteSpace(vReport.FailureReason) ? vReport.FailureReason : (policyEval.FailureReason ?? (explicitAudioSatisfied && explicitSubsSatisfied ? (noDuplicateFakeDub ? null : "Duplicate fake audio stream detected.") : "Missing unit required language")));
                var finalFailureCode = allowed ? null : (vReport.Streams.Count == 0 && !string.IsNullOrWhiteSpace(vReport.FailureCode) ? vReport.FailureCode : "language_mismatch");

                var updatedReport = vReport with
                {
                    Verdict = verifiedStatus,
                    MatchedPolicyTier = policyEval.MatchedTier,
                    MatchedPolicyTierLabel = policyEval.MatchedTierLabel,
                    SatisfiesLanguageCutoff = satisfiesCutoff,
                    FailureReason = finalFailureReason,
                    FailureCode = finalFailureCode,
                };

                var updatedReportJson = JsonSerializer.Serialize(updatedReport, JsonOpts);
                await dbFactory.ExecuteWriteAsync(
                    DatabaseKind.Core,
                    (conn, tx) => conn.ExecuteAsync(
                        "UPDATE verification_jobs SET verdict = @Verdict, report_json = @ReportJson WHERE id = @JobId;",
                        new { Verdict = verifiedStatus, ReportJson = updatedReportJson, JobId = vReport.JobId },
                        transaction: tx),
                    ct);

                var videoCodec = !string.IsNullOrWhiteSpace(file.VideoCodec)
                    ? file.VideoCodec
                    : (vReport.Streams.FirstOrDefault(s => s.StreamType == "video")?.Codec ?? parsed.VideoCodec);
                var audioCodec = !string.IsNullOrWhiteSpace(file.AudioCodec)
                    ? file.AudioCodec
                    : (vReport.Streams.FirstOrDefault(s => s.StreamType == "audio")?.Codec ?? parsed.AudioCodec);
                var resolution = !string.IsNullOrWhiteSpace(file.Resolution)
                    ? file.Resolution
                    : parsed.Resolution;

                await dbFactory.ExecuteWriteAsync(
                    DatabaseKind.Core,
                    (conn, tx) => conn.ExecuteAsync(
                        """
                        UPDATE media_files
                        SET detected_audio_langs_json = @AudioJson,
                            detected_sub_langs_json = @SubJson,
                            verified_status = @Verdict,
                            video_codec = CASE WHEN video_codec IS NULL OR video_codec = '' THEN @VideoCodec ELSE video_codec END,
                            audio_codec = CASE WHEN audio_codec IS NULL OR audio_codec = '' THEN @AudioCodec ELSE audio_codec END,
                            resolution = CASE WHEN resolution IS NULL OR resolution = '' THEN @Resolution ELSE resolution END
                        WHERE id = @FileId;
                        """,
                        new
                        {
                            AudioJson = JsonSerializer.Serialize(mergedAudio, JsonOpts),
                            SubJson = JsonSerializer.Serialize(mergedSubs, JsonOpts),
                            Verdict = verifiedStatus,
                            VideoCodec = videoCodec,
                            AudioCodec = audioCodec,
                            Resolution = resolution,
                            FileId = file.Id,
                        },
                        transaction: tx),
                    ct);
            }

            if (unitCheckedFiles > 0)
            {
                await catalogService.UpdateUnitAsync(
                    unit.Id,
                    new UpdateMediaUnitRequest(LanguageCutoffMet: anyFileLangCutoffMet),
                    ct);
            }
        }

        messages.Add($"Checked {filesChecked} files: {verifiedPass} verified/passed language requirements, {languageMismatch} mismatches.");

        return new WorkLanguageScanReportDto(
            WorkId: workId,
            WorkTitle: tree.Work.Title,
            FilesScannedCount: diskReport.ScannedFilesCount,
            AutoAdoptedCount: diskReport.AutoAdoptedCount,
            MissingRemovedCount: diskReport.MissingRemovedCount,
            FilesLanguageCheckedCount: filesChecked,
            VerifiedPassCount: verifiedPass,
            LanguageMismatchCount: languageMismatch,
            DurationMs: sw.ElapsedMilliseconds,
            Messages: messages);
    }

    public async Task ReevaluateWorkFilesAgainstPolicyAsync(long workId, CancellationToken ct = default)
    {
        var tree = await catalogService.GetWorkTreeAsync(workId, actor: null, ct);
        if (tree is null) return;

        var libs = await catalogService.ListLibrariesAsync(actor: null, ct);
        var lib = libs.FirstOrDefault(l => l.Id == tree.Work.LibraryId);
        if (lib is null) return;

        var defaultProfileId = tree.Work.QualityProfileId ?? lib.DefaultProfileId;
        if (!defaultProfileId.HasValue && lib.MediaType is MetadataVerticals.Movies or MetadataVerticals.Tv or MetadataVerticals.Anime)
        {
            defaultProfileId = await ResolveFallbackProfileIdAsync(ct);
        }

        LanguagePolicySummary? workLanguagePolicy = null;
        if (tree.Work.LanguagePolicyId.HasValue)
        {
            var policies = await profileService.ListLanguagePoliciesAsync(ct);
            workLanguagePolicy = policies.FirstOrDefault(p => p.Id == tree.Work.LanguagePolicyId.Value);
        }

        var profileCache = new Dictionary<long, QualityProfileSummary?>();
        async Task<QualityProfileSummary?> GetCachedProfileAsync(long? pid)
        {
            if (!pid.HasValue) return null;
            if (!profileCache.TryGetValue(pid.Value, out var cached))
            {
                cached = await profileService.GetProfileByIdAsync(pid.Value, ct);
                profileCache[pid.Value] = cached;
            }
            return cached;
        }

        var origLang = tree.Work.OriginalLanguage ?? "en";

        // Fetch files for this work whose verified_status IN ('VerifiedPass', 'VerifiedFail')
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var filesToReevaluate = (await conn.QueryAsync<(
            long FileId,
            long UnitId,
            string AudioJson,
            string SubJson,
            string VerifiedStatus,
            long? ProfileOverrideId,
            string ReqAudioJson,
            string ReqSubJson)>(
            """
            SELECT mf.id AS FileId,
                   mf.unit_id AS UnitId,
                   mf.detected_audio_langs_json AS AudioJson,
                   mf.detected_sub_langs_json AS SubJson,
                   mf.verified_status AS VerifiedStatus,
                   mu.profile_override_id AS ProfileOverrideId,
                   mu.required_audio_langs_json AS ReqAudioJson,
                   mu.required_sub_langs_json AS ReqSubJson
            FROM media_files mf
            JOIN media_units mu ON mu.id = mf.unit_id
            WHERE mf.work_id = @WorkId
              AND mf.verified_status IN ('VerifiedPass', 'VerifiedFail');
            """,
            new { WorkId = workId })).ToList();

        if (filesToReevaluate.Count == 0) return;

        var filesByUnit = filesToReevaluate.GroupBy(f => f.UnitId);

        foreach (var unitGroup in filesByUnit)
        {
            var unitId = unitGroup.Key;
            var anyCutoffMet = false;

            foreach (var f in unitGroup)
            {
                var unitProfileId = f.ProfileOverrideId ?? defaultProfileId;
                var unitProfile = await GetCachedProfileAsync(unitProfileId);
                var effectiveLanguagePolicy = workLanguagePolicy ?? unitProfile?.LanguagePolicy;

                var audioLangs = (JsonSerializer.Deserialize<string[]>(f.AudioJson, JsonOpts) ?? []).ToList();
                var subLangs = (JsonSerializer.Deserialize<string[]>(f.SubJson, JsonOpts) ?? []).ToList();
                var reqAudio = (JsonSerializer.Deserialize<string[]>(f.ReqAudioJson, JsonOpts) ?? []).ToList();
                var reqSubs = (JsonSerializer.Deserialize<string[]>(f.ReqSubJson, JsonOpts) ?? []).ToList();

                var eval = profileService.EvaluateOnDiskLanguagesAgainstPolicy(
                    effectiveLanguagePolicy,
                    origLang,
                    audioLangs,
                    subLangs);

                var explicitAudioSatisfied = reqAudio.Count == 0 || reqAudio.All(ea => audioLangs.Contains(ResolveLangToken(ea, origLang), StringComparer.OrdinalIgnoreCase));
                var explicitSubsSatisfied = reqSubs.Count == 0 || reqSubs.All(es => subLangs.Contains(ResolveLangToken(es, origLang), StringComparer.OrdinalIgnoreCase));

                var allowed = eval.PolicyAllowed && explicitAudioSatisfied && explicitSubsSatisfied;
                var newVerdict = allowed ? VerificationVerdicts.VerifiedPass : VerificationVerdicts.VerifiedFail;
                var satisfiesCutoff = allowed && eval.SatisfiesCutoff;
                anyCutoffMet |= satisfiesCutoff;

                await dbFactory.ExecuteWriteAsync(
                    DatabaseKind.Core,
                    (wConn, tx) => wConn.ExecuteAsync(
                        "UPDATE media_files SET verified_status = @Status WHERE id = @Id;",
                        new { Status = newVerdict, Id = f.FileId },
                        transaction: tx),
                    ct);

                // Update latest verification job
                var latestJob = await conn.QueryFirstOrDefaultAsync<(long Id, string ReportJson)>(
                    "SELECT id AS Id, report_json AS ReportJson FROM verification_jobs WHERE media_file_id = @FileId ORDER BY id DESC LIMIT 1;",
                    new { FileId = f.FileId });

                if (latestJob != default && !string.IsNullOrWhiteSpace(latestJob.ReportJson) && latestJob.ReportJson != "{}")
                {
                    try
                    {
                        var report = JsonSerializer.Deserialize<MediaFileVerificationReport>(latestJob.ReportJson, JsonOpts);
                        if (report is not null)
                        {
                            var finalFailureReason = allowed ? null : (eval.FailureReason ?? (explicitAudioSatisfied && explicitSubsSatisfied ? null : "Missing unit required language"));
                            var finalFailureCode = allowed ? null : "language_mismatch";

                            var updatedReport = report with
                            {
                                Verdict = newVerdict,
                                MatchedPolicyTier = eval.MatchedTier,
                                MatchedPolicyTierLabel = eval.MatchedTierLabel,
                                SatisfiesLanguageCutoff = satisfiesCutoff,
                                FailureReason = finalFailureReason,
                                FailureCode = finalFailureCode,
                            };
                            var updatedJson = JsonSerializer.Serialize(updatedReport, JsonOpts);
                            await dbFactory.ExecuteWriteAsync(
                                DatabaseKind.Core,
                                (wConn, tx) => wConn.ExecuteAsync(
                                    "UPDATE verification_jobs SET verdict = @Verdict, report_json = @ReportJson WHERE id = @JobId;",
                                    new { Verdict = newVerdict, ReportJson = updatedJson, JobId = latestJob.Id },
                                    transaction: tx),
                                ct);
                        }
                    }
                    catch
                    {
                        // Ignore malformed report json
                    }
                }
            }

            await catalogService.UpdateUnitAsync(
                unitId,
                new UpdateMediaUnitRequest(LanguageCutoffMet: anyCutoffMet),
                ct);
        }
    }

    public async Task<BulkWorkLanguageScanReportDto> ScanAndVerifyWorksAsync(IReadOnlyList<long> workIds, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var reports = new List<WorkLanguageScanReportDto>();
        var distinctIds = (workIds ?? []).Distinct().ToList();

        foreach (var wid in distinctIds)
        {
            try
            {
                var rep = await ScanAndVerifyWorkAsync(wid, ct);
                reports.Add(rep);
            }
            catch (KeyNotFoundException)
            {
                // Deleted or missing work ID during batch run - ignore gracefully
            }
        }

        return new BulkWorkLanguageScanReportDto(
            TargetedWorksCount: distinctIds.Count,
            TotalFilesScannedCount: reports.Sum(r => r.FilesScannedCount),
            TotalAutoAdoptedCount: reports.Sum(r => r.AutoAdoptedCount),
            TotalMissingRemovedCount: reports.Sum(r => r.MissingRemovedCount),
            TotalFilesLanguageCheckedCount: reports.Sum(r => r.FilesLanguageCheckedCount),
            TotalVerifiedPassCount: reports.Sum(r => r.VerifiedPassCount),
            TotalLanguageMismatchCount: reports.Sum(r => r.LanguageMismatchCount),
            DurationMs: sw.ElapsedMilliseconds,
            Reports: reports);
    }

    private async Task<long?> ResolveFallbackProfileIdAsync(CancellationToken ct)
    {
        string? configuredSlug = null;
        try
        {
            await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
            var raw = await conn.ExecuteScalarAsync<string?>(
                "SELECT value FROM app_settings WHERE key = 'catalog.default_profile_slug' LIMIT 1;");
            if (!string.IsNullOrWhiteSpace(raw))
            {
                configuredSlug = UnwrapSettingString(raw);
            }
        }
        catch
        {
            // Ignore settings lookup errors when db schema is minimal in unit tests
        }

        var allProfiles = await profileService.ListProfilesAsync(ct);
        if (!string.IsNullOrWhiteSpace(configuredSlug))
        {
            var configured = allProfiles.FirstOrDefault(p =>
                string.Equals(p.Slug, configuredSlug, StringComparison.OrdinalIgnoreCase));
            if (configured is not null)
            {
                return configured.Id;
            }
        }

        return (allProfiles.FirstOrDefault(p =>
                string.Equals(p.Slug, "german-first-best-quality", StringComparison.OrdinalIgnoreCase))
            ?? allProfiles.FirstOrDefault())?.Id;
    }

    private static string ResolveLangToken(string token, string origLang) =>
        string.Equals(token, "original", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(token, "orig", StringComparison.OrdinalIgnoreCase)
            ? origLang.ToLowerInvariant()
            : token.ToLowerInvariant();

    private async Task<DiskScanReportDto> ScanCoreAsync(long libraryId, long? specificWorkId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var lib = await catalogService.GetLibraryByIdAsync(libraryId, ct)
            ?? throw new KeyNotFoundException($"Library #{libraryId} not found.");

        var messages = new List<string>();
        var rootPath = lib.RootFolderPath;
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            sw.Stop();
            messages.Add($"Root folder '{rootPath}' does not exist or is not mounted.");
            return new DiskScanReportDto(lib.Id, lib.Name, specificWorkId, 0, 0, 0, 0, 0, sw.ElapsedMilliseconds, messages);
        }

        var fullRoot = Path.GetFullPath(rootPath);
        var worksInLib = await catalogService.ListWorksAsync(actor: null, libraryId: lib.Id, vertical: null, ct);
        var workTrees = new List<MediaWorkDetailTree>(worksInLib.Count);
        foreach (var ws in worksInLib)
        {
            if (specificWorkId.HasValue && ws.Id != specificWorkId.Value)
            {
                continue;
            }

            var tree = await catalogService.GetWorkTreeAsync(ws.Id, actor: null, ct);
            if (tree is not null)
            {
                workTrees.Add(tree);
            }
        }

        // Load all work trees in library for matching if scanning full library; if scanning 1 work, we already loaded it.
        var allLibraryWorkTrees = specificWorkId.HasValue
            ? workTrees
            : workTrees;

        // Step 1: Prune media_files that were deleted from disk (or point outside library root)
        var missingRemoved = 0;
        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        var rootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var tree in workTrees)
        {
            foreach (var container in tree.Containers)
            {
                foreach (var unit in container.Units)
                {
                    foreach (var mf in unit.Files)
                    {
                        var expectedFull = Path.GetFullPath(Path.Combine(fullRoot, mf.RelativePath));
                        if (HasSymlinkEscapeOrTraversal(fullRoot, rootPrefix, expectedFull))
                        {
                            missingRemoved++;
                            await dbFactory.ExecuteWriteAsync(
                                DatabaseKind.Core,
                                async (conn, tx) =>
                                {
                                    await conn.ExecuteAsync("DELETE FROM media_files WHERE id = @Id;", new { mf.Id }, transaction: tx);
                                    var remainingCount = await conn.ExecuteScalarAsync<int>(
                                        "SELECT COUNT(1) FROM media_files WHERE unit_id = @UnitId;",
                                        new { UnitId = unit.Id },
                                        transaction: tx);
                                    if (remainingCount <= 0)
                                    {
                                        await conn.ExecuteAsync(
                                            "UPDATE media_units SET quality_cutoff_met = 0, language_cutoff_met = 0 WHERE id = @UnitId;",
                                            new { UnitId = unit.Id },
                                            transaction: tx);
                                    }
                                },
                                ct);
                            continue;
                        }

                        if (File.Exists(expectedFull))
                        {
                            var actualLen = new FileInfo(expectedFull).Length;
                            if (actualLen != mf.SizeBytes)
                            {
                                await dbFactory.ExecuteWriteAsync(
                                    DatabaseKind.Core,
                                    (conn, tx) => conn.ExecuteAsync(
                                        "UPDATE media_files SET size_bytes = @Len WHERE id = @Id;",
                                        new { Len = actualLen, mf.Id },
                                        transaction: tx),
                                    ct);
                            }
                            continue;
                        }

                        missingRemoved++;
                        await dbFactory.ExecuteWriteAsync(
                            DatabaseKind.Core,
                            async (conn, tx) =>
                            {
                                await conn.ExecuteAsync("DELETE FROM media_files WHERE id = @Id;", new { mf.Id }, transaction: tx);
                                var remainingCount = await conn.ExecuteScalarAsync<int>(
                                    "SELECT COUNT(1) FROM media_files WHERE unit_id = @UnitId;",
                                    new { UnitId = unit.Id },
                                    transaction: tx);
                                if (remainingCount <= 0)
                                {
                                    await conn.ExecuteAsync(
                                        "UPDATE media_units SET quality_cutoff_met = 0, language_cutoff_met = 0 WHERE id = @UnitId;",
                                        new { UnitId = unit.Id },
                                        transaction: tx);
                                }

                                await conn.ExecuteAsync(
                                    """
                                    INSERT INTO media_history (
                                        occurred_at, event_type, media_vertical, work_id, work_title,
                                        container_id, unit_id, source_title, quality_name, destination_path, data_json
                                    ) VALUES (
                                        @Now, 'MediaFileDeleted', @Vertical, @WorkId, @WorkTitle,
                                        @ContainerId, @UnitId, @SourceTitle, @Quality, @DestPath, '{"reason":"MissingOnDiskDuringScan"}'
                                    );
                                    """,
                                    new
                                    {
                                        Now = nowIso,
                                        Vertical = tree.Work.WorkKind,
                                        WorkId = tree.Work.Id,
                                        WorkTitle = tree.Work.Title,
                                        ContainerId = container.Id,
                                        UnitId = unit.Id,
                                        SourceTitle = Path.GetFileName(mf.RelativePath),
                                        Quality = mf.QualityName,
                                        DestPath = expectedFull,
                                    },
                                    transaction: tx);
                            },
                            ct);
                    }
                }
            }
        }

        // Refresh linked relative paths set across the ENTIRE library so we never duplicate an already-attached file
        HashSet<string> linkedRelPaths;
        await using (var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            var relRows = await conn.QueryAsync<string>(
                """
                SELECT mf.relative_path
                FROM media_files mf
                INNER JOIN media_works mw ON mw.id = mf.work_id
                WHERE mw.library_id = @LibId;
                """,
                new { LibId = lib.Id });
            linkedRelPaths = relRows.Select(NormalizeRelPathKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        // Determine scan directory
        var scanRoot = fullRoot;
        if (specificWorkId.HasValue && workTrees.Count == 1)
        {
            var targetWork = workTrees[0].Work;
            var candidateDir = FindExistingWorkSubfolder(fullRoot, targetWork);
            if (candidateDir is null)
            {
                sw.Stop();
                if (missingRemoved > 0)
                {
                    messages.Add($"Removed {missingRemoved} missing database file record(s).");
                }
                else
                {
                    messages.Add($"No dedicated folder found yet on disk for '{targetWork.Title}'.");
                }

                return new DiskScanReportDto(lib.Id, lib.Name, specificWorkId, 0, 0, 0, missingRemoved, 0, sw.ElapsedMilliseconds, messages);
            }

            scanRoot = candidateDir;
        }

        var allowedExts = GetAllowedExtensions(lib.MediaType);
        var scannedCount = 0;
        var alreadyLinkedCount = 0;
        var autoAdoptedCount = 0;
        var unmappedCount = 0;
        var seenUnmappedRelKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Work trees are a snapshot from before the scan; remember units this scan already gave a file
        var unitsFilledThisScan = new HashSet<long>();

        var enumOpts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
        };

        foreach (var filePath in Directory.EnumerateFiles(scanRoot, "*", enumOpts))
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(filePath);
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName.StartsWith('.') ||
                fileName.Contains(".managerr-tmp-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ext = Path.GetExtension(fileName);
            if (!allowedExts.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var fi = new FileInfo(filePath);
            if (!fi.Exists || fi.LinkTarget is not null)
            {
                continue;
            }

            // Skip tiny sample clips (< 80 MB with 'sample' in stem)
            if (fi.Length < 80L * 1024 * 1024 &&
                Path.GetFileNameWithoutExtension(fileName).Contains("sample", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            scannedCount++;
            var relPath = Path.GetRelativePath(fullRoot, fi.FullName);
            var relKey = NormalizeRelPathKey(relPath);
            if (linkedRelPaths.Contains(relKey))
            {
                alreadyLinkedCount++;
                await dbFactory.ExecuteWriteAsync(
                    DatabaseKind.Core,
                    (conn, tx) => conn.ExecuteAsync(
                        "DELETE FROM unmapped_media_files WHERE library_id = @LibId AND relative_path = @RelPath;",
                        new { LibId = lib.Id, RelPath = relPath },
                        transaction: tx),
                    ct);
                continue;
            }

            var parsed = ParseDiskFileWithFolderContext(relPath, fileName, lib.MediaType);
            var matchedWork = MatchWorkForDiskFile(relPath, parsed, allLibraryWorkTrees);
            var qualityLabel = BuildQualityLabel(parsed, ext);

            string? conflictReason = null;
            if (matchedWork is not null)
            {
                var targetUnits = ResolveUnitsForDiskFile(matchedWork, parsed, relPath);

                // A second file for an episode that already has one is a duplicate, not something to adopt;
                // it stays unmapped so the user decides which one to keep
                var occupied = targetUnits.FirstOrDefault(u => unitsFilledThisScan.Contains(u.Id) || HasFileForEdition(u, parsed.MovieEdition));
                if (occupied is not null)
                {
                    var existingPath = occupied.Files.FirstOrDefault()?.RelativePath ?? "a file adopted earlier in this scan";
                    conflictReason = $"Duplicate: '{matchedWork.Work.Title}' {FormatSeasonEp(parsed)} already has '{existingPath}'.";
                    targetUnits = [];
                }

                if (targetUnits.Count > 0)
                {
                    var detectedSubLangs = DetectSidecarSubtitleLanguages(fi.FullName);
                    foreach (var unit in targetUnits)
                    {
                        var audioMet = unit.RequiredAudioLangs.Count == 0 ||
                                       unit.RequiredAudioLangs.All(req => parsed.DetectedLanguages.Contains(req, StringComparer.OrdinalIgnoreCase));
                        var subMet = unit.RequiredSubLangs.Count == 0 ||
                                     unit.RequiredSubLangs.All(req =>
                                         detectedSubLangs.Contains(req, StringComparer.OrdinalIgnoreCase) ||
                                         parsed.DetectedLanguages.Contains(req, StringComparer.OrdinalIgnoreCase) ||
                                         parsed.IsMultiSubs);
                        var langCutoffMet = audioMet && subMet;

                        await catalogService.AttachFileAsync(
                            new AttachMediaFileRequest(
                                UnitId: unit.Id,
                                RelativePath: relPath,
                                SizeBytes: fi.Length,
                                QualityName: qualityLabel,
                                EditionName: parsed.MovieEdition,
                                Resolution: parsed.Resolution,
                                VideoCodec: parsed.VideoCodec,
                                HdrFormat: parsed.HdrFormat,
                                AudioCodec: parsed.AudioCodec,
                                DetectedAudioLangs: parsed.DetectedLanguages,
                                DetectedSubLangs: detectedSubLangs,
                                VerifiedStatus: "disk_scan_adopted",
                                InodeHardlinked: false),
                            ct);

                        await catalogService.UpdateUnitAsync(
                            unit.Id,
                            new UpdateMediaUnitRequest(QualityCutoffMet: true, LanguageCutoffMet: langCutoffMet),
                            ct);

                        await dbFactory.ExecuteWriteAsync(
                            DatabaseKind.Core,
                            (conn, tx) => conn.ExecuteAsync(
                                """
                                INSERT INTO media_history (
                                    occurred_at, event_type, media_vertical, work_id, work_title,
                                    container_id, unit_id, source_title, quality_name, languages_json,
                                    destination_path, data_json
                                ) VALUES (
                                    @Now, 'DownloadFolderImported', @Vertical, @WorkId, @WorkTitle,
                                    @ContainerId, @UnitId, @SourceTitle, @Quality, @LangsJson,
                                    @DestPath, '{"source":"DiskScanAutoAdopt"}'
                                );
                                """,
                                new
                                {
                                    Now = nowIso,
                                    Vertical = matchedWork.Work.WorkKind,
                                    WorkId = matchedWork.Work.Id,
                                    WorkTitle = matchedWork.Work.Title,
                                    ContainerId = unit.ContainerId,
                                    UnitId = unit.Id,
                                    SourceTitle = fileName,
                                    Quality = qualityLabel,
                                    LangsJson = JsonSerializer.Serialize(parsed.DetectedLanguages, JsonOpts),
                                    DestPath = fi.FullName,
                                },
                                transaction: tx),
                            ct);
                    }

                    linkedRelPaths.Add(relKey);
                    unitsFilledThisScan.UnionWith(targetUnits.Select(u => u.Id));
                    autoAdoptedCount++;
                    await dbFactory.ExecuteWriteAsync(
                        DatabaseKind.Core,
                        (conn, tx) => conn.ExecuteAsync(
                            "DELETE FROM unmapped_media_files WHERE library_id = @LibId AND relative_path = @RelPath;",
                            new { LibId = lib.Id, RelPath = relPath },
                            transaction: tx),
                        ct);
                    continue;
                }
            }

            // Unmapped file: persist for interactive mapping in Settings -> Media Management
            unmappedCount++;
            seenUnmappedRelKeys.Add(relKey);
            var reason = conflictReason ?? (matchedWork is not null
                ? $"Matched work '{matchedWork.Work.Title}' but could not match season/episode ({FormatSeasonEp(parsed)}) to catalog units."
                : $"No matching catalog work found in library '{lib.Name}' for parsed title '{parsed.CleanTitle}'.");

            await UpsertUnmappedFileAsync(
                lib.Id,
                fi.FullName,
                relPath,
                fileName,
                fi.Length,
                parsed,
                qualityLabel,
                matchedWork?.Work.Id,
                matchedWork?.Work.Title,
                reason,
                nowIso,
                ct);
        }

        // Clean stale unmapped_media_files rows in this library whose files disappeared from disk
        if (!specificWorkId.HasValue)
        {
            var existingUnmapped = await ListUnmappedFilesAsync(lib.Id, ct);
            foreach (var um in existingUnmapped)
            {
                if (!File.Exists(um.FullPath) || linkedRelPaths.Contains(NormalizeRelPathKey(um.RelativePath)))
                {
                    await DismissUnmappedFileAsync(um.Id, deleteFromDisk: false, ct);
                }
            }
        }

        sw.Stop();
        messages.Add(
            $"Scanned {scannedCount} media file(s) in '{lib.Name}': {autoAdoptedCount} auto-adopted, {alreadyLinkedCount} already linked, {missingRemoved} missing pruned, {unmappedCount} unmapped.");

        return new DiskScanReportDto(
            LibraryId: lib.Id,
            LibraryName: lib.Name,
            WorkId: specificWorkId,
            ScannedFilesCount: scannedCount,
            AlreadyLinkedCount: alreadyLinkedCount,
            AutoAdoptedCount: autoAdoptedCount,
            MissingRemovedCount: missingRemoved,
            UnmappedCount: unmappedCount,
            DurationMs: sw.ElapsedMilliseconds,
            Messages: messages);
    }

    public async Task<IReadOnlyList<UnmappedMediaFileDto>> ListUnmappedFilesAsync(long? libraryId = null, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<UnmappedFileRow>(
            """
            SELECT u.*,
                   l.name AS library_name,
                   l.media_type AS media_vertical
            FROM unmapped_media_files u
            INNER JOIN libraries l ON l.id = u.library_id
            WHERE (@LibId IS NULL OR u.library_id = @LibId)
            ORDER BY u.discovered_at DESC, u.id DESC;
            """,
            new { LibId = libraryId });

        return rows.Select(r => new UnmappedMediaFileDto(
            Id: r.Id,
            LibraryId: r.LibraryId,
            LibraryName: r.LibraryName,
            MediaVertical: r.MediaVertical,
            FullPath: r.FullPath,
            RelativePath: r.RelativePath,
            FileName: r.FileName,
            SizeBytes: r.SizeBytes,
            ParsedTitle: r.ParsedTitle,
            ParsedSeasonNumber: r.ParsedSeason,
            ParsedEpisodeNumbers: JsonSerializer.Deserialize<int[]>(r.ParsedEpisodesJson, JsonOpts) ?? [],
            ParsedQuality: r.ParsedQuality,
            ParsedLanguages: JsonSerializer.Deserialize<string[]>(r.ParsedLanguagesJson, JsonOpts) ?? [],
            SuggestedWorkId: r.SuggestedWorkId,
            SuggestedWorkTitle: r.SuggestedWorkTitle,
            Reason: r.Reason,
            DiscoveredAt: r.DiscoveredAt)).ToList();
    }

    public async Task<MapUnmappedFilesReportDto> MapUnmappedFilesAsync(MapUnmappedFilesRequest request, CancellationToken ct = default)
    {
        var items = request.Items ?? [];
        var mapped = 0;
        var failed = 0;
        var messages = new List<string>();
        var namingCfg = await GetNamingConfigAsync(ct);

        foreach (var item in items)
        {
            UnmappedFileRow? row;
            await using (var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
            {
                row = await conn.QuerySingleOrDefaultAsync<UnmappedFileRow>(
                    """
                    SELECT u.*, l.name AS library_name, l.media_type AS media_vertical
                    FROM unmapped_media_files u
                    INNER JOIN libraries l ON l.id = u.library_id
                    WHERE u.id = @Id;
                    """,
                    new { Id = item.UnmappedFileId });
            }

            if (row is null || !File.Exists(row.FullPath))
            {
                failed++;
                messages.Add($"Unmapped file #{item.UnmappedFileId} was not found on disk.");
                continue;
            }

            var tree = await catalogService.GetWorkTreeAsync(item.WorkId, actor: null, ct);
            if (tree is null)
            {
                failed++;
                messages.Add($"Target work #{item.WorkId} does not exist.");
                continue;
            }

            var lib = await catalogService.GetLibraryByIdAsync(tree.Work.LibraryId, ct);
            if (lib is null)
            {
                failed++;
                messages.Add($"Library #{tree.Work.LibraryId} does not exist.");
                continue;
            }

            var resolvedUnits = ResolveUnitsFromExplicitOverride(tree, item, row);
            if (resolvedUnits.Count == 0)
            {
                failed++;
                messages.Add($"Could not resolve target unit(s) in '{tree.Work.Title}' for '{row.FileName}'.");
                continue;
            }

            var parsed = parserRouter.Parse(row.FileName, tree.Work.WorkKind);
            var qualityName = !string.IsNullOrWhiteSpace(item.QualityName)
                ? item.QualityName!.Trim()
                : (!string.IsNullOrWhiteSpace(row.ParsedQuality) ? row.ParsedQuality : BuildQualityLabel(parsed, Path.GetExtension(row.FileName)));
            var langs = item.Languages is { Count: > 0 }
                ? item.Languages.Select(x => x.Trim().ToLowerInvariant()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray()
                : (JsonSerializer.Deserialize<string[]>(row.ParsedLanguagesJson, JsonOpts) ?? []);

            var finalRelPath = Path.GetRelativePath(Path.GetFullPath(lib.RootFolderPath), Path.GetFullPath(row.FullPath));
            var fullSource = Path.GetFullPath(row.FullPath);

            if (item.RenameToTemplate && namingCfg.RenameMediaFiles)
            {
                var firstUnit = resolvedUnits[0];
                var firstContainer = tree.Containers.First(c => c.Id == firstUnit.ContainerId);
                namingCfg.Templates.TryGetValue(tree.Work.WorkKind, out var tpl);
                var synthInfo = BuildParsedInfoForWorkUnit(tree.Work, firstContainer, resolvedUnits, parsed, qualityName, langs);
                var ctx = new NamingContextMetadata(
                    EpisodeTitle: firstUnit.Title,
                    QualityTitle: qualityName,
                    CustomFormats: [],
                    ExternalIds: tree.Work.ExternalIds,
                    ColonReplacement: namingCfg.ColonReplacement,
                    AlbumTitle: string.Equals(tree.Work.WorkKind, MetadataVerticals.Music, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(firstContainer.Title)
                        ? firstContainer.Title
                        : tree.Work.Title,
                    UnitNumber: firstUnit.UnitNumber);
                var targetRel = fileOrganizer.RenderRelativeDestinationPathWithContext(synthInfo, Path.GetExtension(fullSource), tpl, ctx);
                var targetFull = Path.GetFullPath(Path.Combine(lib.RootFolderPath, targetRel));

                if (!string.Equals(fullSource, targetFull, StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetFull)!);
                    if (File.Exists(targetFull))
                    {
                        File.Delete(targetFull);
                    }

                    File.Move(fullSource, targetFull);
                    finalRelPath = targetRel;
                    fullSource = targetFull;
                }
            }
            else if (finalRelPath.StartsWith("..", StringComparison.Ordinal))
            {
                // File came from another library root; move into target library root
                var firstUnit = resolvedUnits[0];
                var firstContainer = tree.Containers.First(c => c.Id == firstUnit.ContainerId);
                namingCfg.Templates.TryGetValue(tree.Work.WorkKind, out var tpl);
                var synthInfo = BuildParsedInfoForWorkUnit(tree.Work, firstContainer, resolvedUnits, parsed, qualityName, langs);
                var targetRel = fileOrganizer.RenderRelativeDestinationPath(synthInfo, Path.GetExtension(fullSource), tpl);
                var targetFull = Path.GetFullPath(Path.Combine(lib.RootFolderPath, targetRel));
                Directory.CreateDirectory(Path.GetDirectoryName(targetFull)!);
                File.Move(fullSource, targetFull, overwrite: true);
                finalRelPath = targetRel;
            }

            var fi = new FileInfo(fullSource);
            foreach (var unit in resolvedUnits)
            {
                await catalogService.AttachFileAsync(
                    new AttachMediaFileRequest(
                        UnitId: unit.Id,
                        RelativePath: finalRelPath,
                        SizeBytes: fi.Length,
                        QualityName: qualityName,
                        EditionName: parsed.MovieEdition,
                        Resolution: parsed.Resolution,
                        VideoCodec: parsed.VideoCodec,
                        HdrFormat: parsed.HdrFormat,
                        AudioCodec: parsed.AudioCodec,
                        DetectedAudioLangs: langs,
                        DetectedSubLangs: [],
                        VerifiedStatus: "manual_mapped",
                        InodeHardlinked: false),
                    ct);

                await catalogService.UpdateUnitAsync(unit.Id, new UpdateMediaUnitRequest(QualityCutoffMet: true), ct);
            }

            await DismissUnmappedFileAsync(row.Id, deleteFromDisk: false, ct);
            mapped++;
            messages.Add($"Mapped '{row.FileName}' -> '{tree.Work.Title}' ({finalRelPath}).");
        }

        return new MapUnmappedFilesReportDto(mapped, failed, messages);
    }

    public async Task<bool> DismissUnmappedFileAsync(long unmappedFileId, bool deleteFromDisk = false, CancellationToken ct = default)
    {
        string? fullPath = null;
        if (deleteFromDisk)
        {
            await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
            fullPath = await conn.ExecuteScalarAsync<string?>("SELECT full_path FROM unmapped_media_files WHERE id = @Id;", new { Id = unmappedFileId });
        }

        if (deleteFromDisk && !string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath))
        {
            try { File.Delete(fullPath); } catch { }
        }

        var affected = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync("DELETE FROM unmapped_media_files WHERE id = @Id;", new { Id = unmappedFileId }, transaction: tx),
            ct);

        return affected > 0;
    }

    public async Task<IReadOnlyList<RenameFilePreviewItemDto>> PreviewWorkRenameAsync(long workId, CancellationToken ct = default)
    {
        var tree = await catalogService.GetWorkTreeAsync(workId, actor: null, ct)
            ?? throw new KeyNotFoundException($"MediaWork #{workId} not found.");
        var lib = await catalogService.GetLibraryByIdAsync(tree.Work.LibraryId, ct)
            ?? throw new KeyNotFoundException($"Library #{tree.Work.LibraryId} not found.");
        var cfg = await GetNamingConfigAsync(ct);

        return BuildWorkRenamePreview(tree, lib, cfg);
    }

    public async Task<IReadOnlyList<RenameFilePreviewItemDto>> PreviewLibraryRenameAsync(long libraryId, CancellationToken ct = default)
    {
        var lib = await catalogService.GetLibraryByIdAsync(libraryId, ct)
            ?? throw new KeyNotFoundException($"Library #{libraryId} not found.");
        var works = await catalogService.ListWorksAsync(actor: null, libraryId: libraryId, vertical: null, ct);
        var cfg = await GetNamingConfigAsync(ct);
        var list = new List<RenameFilePreviewItemDto>();

        foreach (var w in works)
        {
            var tree = await catalogService.GetWorkTreeAsync(w.Id, actor: null, ct);
            if (tree is not null)
            {
                list.AddRange(BuildWorkRenamePreview(tree, lib, cfg));
            }
        }

        return list;
    }

    public async Task<RenameExecuteReportDto> ExecuteWorkRenameAsync(long workId, IReadOnlyList<long>? mediaFileIds = null, CancellationToken ct = default)
    {
        var tree = await catalogService.GetWorkTreeAsync(workId, actor: null, ct)
            ?? throw new KeyNotFoundException($"MediaWork #{workId} not found.");
        var lib = await catalogService.GetLibraryByIdAsync(tree.Work.LibraryId, ct)
            ?? throw new KeyNotFoundException($"Library #{tree.Work.LibraryId} not found.");
        var previews = await PreviewWorkRenameAsync(workId, ct);

        return await ExecuteRenameItemsAsync(lib, previews, mediaFileIds, ct);
    }

    public async Task<RenameExecuteReportDto> ExecuteLibraryRenameAsync(long libraryId, IReadOnlyList<long>? mediaFileIds = null, CancellationToken ct = default)
    {
        var lib = await catalogService.GetLibraryByIdAsync(libraryId, ct)
            ?? throw new KeyNotFoundException($"Library #{libraryId} not found.");
        var previews = await PreviewLibraryRenameAsync(libraryId, ct);

        return await ExecuteRenameItemsAsync(lib, previews, mediaFileIds, ct);
    }

    private IReadOnlyList<RenameFilePreviewItemDto> BuildWorkRenamePreview(
        MediaWorkDetailTree tree,
        LibrarySummary lib,
        NamingConfigDto cfg)
    {
        var items = new List<RenameFilePreviewItemDto>();
        cfg.Templates.TryGetValue(tree.Work.WorkKind, out var template);
        var seenFileIds = new HashSet<long>();

        // Group units sharing the same relative_path so multi-episode files render E01E02 / 01-02 properly!
        var fileUnitMap = new Dictionary<string, List<(MediaContainerDetail Container, MediaUnitDetail Unit, MediaFileSummary File)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in tree.Containers)
        {
            foreach (var u in c.Units)
            {
                foreach (var mf in u.Files)
                {
                    var key = NormalizeRelPathKey(mf.RelativePath);
                    if (!fileUnitMap.TryGetValue(key, out var bucket))
                    {
                        bucket = [];
                        fileUnitMap[key] = bucket;
                    }

                    bucket.Add((c, u, mf));
                }
            }
        }

        foreach (var c in tree.Containers)
        {
            foreach (var u in c.Units)
            {
                foreach (var mf in u.Files)
                {
                    if (!seenFileIds.Add(mf.Id))
                    {
                        continue;
                    }

                    var bucket = fileUnitMap[NormalizeRelPathKey(mf.RelativePath)];
                    var groupedUnits = bucket.Select(b => b.Unit).DistinctBy(x => x.Id).OrderBy(x => x.UnitNumber).ToList();
                    var parsedFromDisk = parserRouter.Parse(Path.GetFileName(mf.RelativePath), tree.Work.WorkKind);

                    var editionName = u.Editions.FirstOrDefault(e => e.Id == mf.EditionId)?.EditionName ?? parsedFromDisk.MovieEdition;
                    var sourceToken = InferSourceFromQualityName(mf.QualityName) ?? parsedFromDisk.Source;
                    var resToken = mf.Resolution ?? parsedFromDisk.Resolution;
                    var vCodec = mf.VideoCodec ?? parsedFromDisk.VideoCodec;
                    var aCodec = mf.AudioCodec ?? parsedFromDisk.AudioCodec;
                    var hdr = mf.HdrFormat ?? parsedFromDisk.HdrFormat;

                    var synthInfo = new ParsedReleaseInfo(
                        RawTitle: Path.GetFileName(mf.RelativePath),
                        MediaVertical: tree.Work.WorkKind,
                        CleanTitle: tree.Work.Title,
                        ArtistOrAuthor: parsedFromDisk.ArtistOrAuthor,
                        Year: tree.Work.Year,
                        SeasonNumber: c.ContainerNumber,
                        EpisodeNumbers: groupedUnits.Select(x => x.UnitNumber).ToArray(),
                        AbsoluteEpisodeNumbers: groupedUnits.Where(x => x.AbsoluteNumber.HasValue).Select(x => x.AbsoluteNumber!.Value).ToArray(),
                        IsFullSeasonPack: false,
                        MovieEdition: editionName,
                        Resolution: resToken,
                        Source: sourceToken,
                        VideoCodec: vCodec,
                        HdrFormat: hdr,
                        AudioCodec: aCodec,
                        MusicFormat: tree.Work.WorkKind == MetadataVerticals.Music ? mf.QualityName : null,
                        BookFormat: tree.Work.WorkKind is MetadataVerticals.Ebooks or MetadataVerticals.Audiobooks or MetadataVerticals.Comics ? mf.QualityName : null,
                        DetectedLanguages: mf.DetectedAudioLangs,
                        IsDualAudio: mf.DetectedAudioLangs.Count > 1,
                        IsMultiSubs: mf.DetectedSubLangs.Count > 1,
                        ReleaseGroup: parsedFromDisk.ReleaseGroup);

                    var ctx = new NamingContextMetadata(
                        EpisodeTitle: groupedUnits.Count == 1 ? u.Title : string.Join(" + ", groupedUnits.Select(x => x.Title)),
                        QualityTitle: mf.QualityName,
                        CustomFormats: [],
                        ExternalIds: tree.Work.ExternalIds,
                        ColonReplacement: cfg.ColonReplacement,
                        AlbumTitle: string.Equals(tree.Work.WorkKind, MetadataVerticals.Music, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Title)
                            ? c.Title
                            : tree.Work.Title,
                        UnitNumber: u.UnitNumber);

                    var ext = Path.GetExtension(mf.RelativePath);
                    if (string.IsNullOrWhiteSpace(ext))
                    {
                        ext = ".mkv";
                    }

                    var newRel = fileOrganizer.RenderRelativeDestinationPathWithContext(synthInfo, ext, template, ctx);
                    var currentFull = Path.GetFullPath(Path.Combine(lib.RootFolderPath, mf.RelativePath));
                    var existsOnDisk = File.Exists(currentFull);
                    var needsRename = !string.Equals(
                        NormalizeRelPathKey(mf.RelativePath),
                        NormalizeRelPathKey(newRel),
                        StringComparison.Ordinal);

                    var epCode = tree.Work.WorkKind switch
                    {
                        MetadataVerticals.Movies => tree.Work.Year?.ToString() ?? "Feature",
                        MetadataVerticals.Anime when u.AbsoluteNumber.HasValue => $"#{u.AbsoluteNumber.Value:D3}",
                        _ => $"S{c.ContainerNumber:D2}E{u.UnitNumber:D2}",
                    };

                    items.Add(new RenameFilePreviewItemDto(
                        MediaFileId: mf.Id,
                        WorkId: tree.Work.Id,
                        WorkTitle: tree.Work.Title,
                        UnitId: u.Id,
                        EpisodeCode: epCode,
                        CurrentRelativePath: mf.RelativePath,
                        NewRelativePath: newRel,
                        NeedsRename: needsRename,
                        ExistsOnDisk: existsOnDisk));
                }
            }
        }

        return items;
    }

    private async Task<RenameExecuteReportDto> ExecuteRenameItemsAsync(
        LibrarySummary lib,
        IReadOnlyList<RenameFilePreviewItemDto> previews,
        IReadOnlyList<long>? filterIds,
        CancellationToken ct)
    {
        var filterSet = filterIds is { Count: > 0 } ? filterIds.ToHashSet() : null;
        var targets = previews
            .Where(p => p.NeedsRename && (filterSet is null || filterSet.Contains(p.MediaFileId)))
            .ToList();

        var renamed = 0;
        var failed = 0;
        var messages = new List<string>();
        var fullRoot = Path.GetFullPath(lib.RootFolderPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var nowIso = DateTimeOffset.UtcNow.ToString("O");

        // Deduplicate physical moves when multiple media_files rows point to the same multi-episode file
        var movedPhysicalMap = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var item in targets)
        {
            var srcFull = Path.GetFullPath(Path.Combine(lib.RootFolderPath, item.CurrentRelativePath));
            var dstFull = Path.GetFullPath(Path.Combine(lib.RootFolderPath, item.NewRelativePath));

            if (!srcFull.StartsWith(fullRoot, StringComparison.Ordinal) ||
                !dstFull.StartsWith(fullRoot, StringComparison.Ordinal))
            {
                failed++;
                messages.Add($"Skipped '{item.CurrentRelativePath}': path outside library root.");
                continue;
            }

            try
            {
                if (!movedPhysicalMap.ContainsKey(srcFull))
                {
                    if (!File.Exists(srcFull))
                    {
                        failed++;
                        messages.Add($"Source file missing on disk: '{item.CurrentRelativePath}'.");
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(dstFull)!);
                    MoveMatchingSubtitleSidecars(srcFull, dstFull);
                    File.Move(srcFull, dstFull, overwrite: true);
                    TryPruneEmptyParentDirs(Path.GetDirectoryName(srcFull), fullRoot);
                    movedPhysicalMap[srcFull] = dstFull;
                }

                await dbFactory.ExecuteWriteAsync(
                    DatabaseKind.Core,
                    async (conn, tx) =>
                    {
                        await conn.ExecuteAsync(
                            "UPDATE media_files SET relative_path = @NewRel WHERE id = @Id;",
                            new { Id = item.MediaFileId, NewRel = item.NewRelativePath },
                            transaction: tx);

                        await conn.ExecuteAsync(
                            """
                            INSERT INTO media_history (
                                occurred_at, event_type, media_vertical, work_id, work_title,
                                unit_id, episode_code, source_title, destination_path, data_json
                            ) VALUES (
                                @Now, 'MediaFileRenamed', @Vertical, @WorkId, @WorkTitle,
                                @UnitId, @EpCode, @OldRel, @NewRel, @DataJson
                            );
                            """,
                            new
                            {
                                Now = nowIso,
                                Vertical = lib.MediaType,
                                item.WorkId,
                                item.WorkTitle,
                                item.UnitId,
                                EpCode = item.EpisodeCode,
                                OldRel = item.CurrentRelativePath,
                                NewRel = item.NewRelativePath,
                                DataJson = JsonSerializer.Serialize(new { oldRelativePath = item.CurrentRelativePath, newRelativePath = item.NewRelativePath }, JsonOpts),
                            },
                            transaction: tx);
                    },
                    ct);

                renamed++;
            }
            catch (Exception ex)
            {
                failed++;
                messages.Add($"Failed to rename '{item.CurrentRelativePath}': {ex.Message}");
            }
        }

        if (renamed > 0 || failed == 0)
        {
            messages.Insert(0, $"Renamed {renamed} of {targets.Count} media file(s) in '{lib.Name}'.");
        }

        if (renamed > 0 && notificationSink is not null)
        {
            var first = targets[0];
            await notificationSink.PublishAsync(
                new Managerr.Common.Notifications.MediaLifecycleNotificationEvent(
                    EventType: Managerr.Common.Notifications.MediaNotificationEventTypes.OnRename,
                    Title: $"Renamed {renamed} file(s) — {first.WorkTitle}",
                    Message: $"Renamed {renamed} media file(s) in library '{lib.Name}' to match naming template.",
                    WorkId: first.WorkId,
                    WorkTitle: first.WorkTitle,
                    MediaVertical: lib.MediaType,
                    EpisodeCode: first.EpisodeCode,
                    DestinationPath: first.NewRelativePath),
                ct);
        }

        return new RenameExecuteReportDto(targets.Count, renamed, failed, messages);
    }

    public static string[] DetectSidecarSubtitleLanguages(string mediaFileFullPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(mediaFileFullPath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                return [];
            }

            var stem = Path.GetFileNameWithoutExtension(mediaFileFullPath);
            if (string.IsNullOrWhiteSpace(stem))
            {
                return [];
            }

            var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var splitChars = new[] { '.', '_', '-', '[', ']', '(', ')', ' ' };

            foreach (var candidate in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
            {
                var ext = Path.GetExtension(candidate);
                if (!SubtitleSidecarExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var candName = Path.GetFileName(candidate);
                if (!candName.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var nameWithoutExt = Path.GetFileNameWithoutExtension(candidate);
                if (nameWithoutExt.Length <= stem.Length)
                {
                    continue;
                }

                var suffix = nameWithoutExt[stem.Length..];
                var tokens = suffix.Split(splitChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var token in tokens)
                {
                    var lang = token.ToLowerInvariant() switch
                    {
                        "de" or "deu" or "ger" or "german" or "deutsch" => "de",
                        "en" or "eng" or "english" => "en",
                        "ja" or "jpn" or "japanese" => "ja",
                        "fr" or "fra" or "fre" or "french" => "fr",
                        "es" or "spa" or "spanish" => "es",
                        "it" or "ita" or "italian" => "it",
                        "pt" or "por" or "portuguese" => "pt",
                        "nl" or "nld" or "dut" or "dutch" => "nl",
                        "pl" or "pol" or "polish" => "pl",
                        "ru" or "rus" or "russian" => "ru",
                        "tr" or "tur" or "turkish" => "tr",
                        "ko" or "kor" or "korean" => "ko",
                        "zh" or "zho" or "chi" or "chinese" => "zh",
                        _ => null,
                    };

                    if (lang is not null)
                    {
                        detected.Add(lang);
                    }
                }
            }

            return detected.ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static void MoveMatchingSubtitleSidecars(string srcMediaFull, string dstMediaFull)
    {
        var srcDir = Path.GetDirectoryName(srcMediaFull);
        var dstDir = Path.GetDirectoryName(dstMediaFull);
        if (string.IsNullOrWhiteSpace(srcDir) || string.IsNullOrWhiteSpace(dstDir) || !Directory.Exists(srcDir))
        {
            return;
        }

        var srcStem = Path.GetFileNameWithoutExtension(srcMediaFull);
        var dstStem = Path.GetFileNameWithoutExtension(dstMediaFull);
        foreach (var candidate in Directory.EnumerateFiles(srcDir, "*", SearchOption.TopDirectoryOnly))
        {
            var ext = Path.GetExtension(candidate);
            if (!SubtitleSidecarExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var candName = Path.GetFileName(candidate);
            if (!candName.StartsWith(srcStem, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = candName[srcStem.Length..];
            var targetSub = Path.Combine(dstDir, dstStem + suffix);
            try
            {
                File.Move(candidate, targetSub, overwrite: true);
            }
            catch
            {
                // Non-fatal sidecar rename failure
            }
        }
    }

    private static void TryPruneEmptyParentDirs(string? startDir, string fullRootWithSep)
    {
        var cur = startDir;
        while (!string.IsNullOrWhiteSpace(cur))
        {
            var norm = Path.GetFullPath(cur);
            if (!norm.StartsWith(fullRootWithSep, StringComparison.Ordinal))
            {
                break;
            }

            if (Directory.Exists(norm) && !Directory.EnumerateFileSystemEntries(norm).Any())
            {
                try { Directory.Delete(norm, recursive: false); } catch { break; }
                cur = Path.GetDirectoryName(norm);
            }
            else
            {
                break;
            }
        }
    }

    private ParsedReleaseInfo ParseDiskFileWithFolderContext(string relPath, string fileName, string mediaVertical)
    {
        var parsed = parserRouter.Parse(Path.GetFileNameWithoutExtension(fileName), mediaVertical);
        var segments = relPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        int? folderSeason = null;
        foreach (var seg in segments.Take(Math.Max(0, segments.Length - 1)))
        {
            if (seg.Equals("Specials", StringComparison.OrdinalIgnoreCase) ||
                seg.Equals("Special", StringComparison.OrdinalIgnoreCase) ||
                seg.Equals("OVAs", StringComparison.OrdinalIgnoreCase))
            {
                folderSeason = 0;
                continue;
            }

            var m = SeasonFolderRegex().Match(seg);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var sNum))
            {
                folderSeason = sNum;
            }
        }

        if (!parsed.SeasonNumber.HasValue && folderSeason.HasValue)
        {
            parsed = parsed with { SeasonNumber = folderSeason };
        }

        // If filename is minimal like 'S01E05.mkv' where CleanTitle ended up empty or generic, inherit work folder name!
        if ((string.IsNullOrWhiteSpace(parsed.CleanTitle) ||
             parsed.CleanTitle.Length <= 2 ||
             string.Equals(parsed.CleanTitle, "Unknown", StringComparison.OrdinalIgnoreCase)) &&
            segments.Length >= 2)
        {
            var topFolderParsed = parserRouter.Parse(segments[0], mediaVertical);
            if (!string.IsNullOrWhiteSpace(topFolderParsed.CleanTitle))
            {
                parsed = parsed with
                {
                    CleanTitle = topFolderParsed.CleanTitle,
                    Year = parsed.Year ?? topFolderParsed.Year,
                };
            }
        }

        return parsed;
    }

    private static MediaWorkDetailTree? MatchWorkForDiskFile(
        string relPath,
        ParsedReleaseInfo parsed,
        IReadOnlyList<MediaWorkDetailTree> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var segments = relPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var topFolder = segments.Length >= 2 ? segments[0] : null;

        // 1. Folder name match (highest confidence inside an organized root folder!)
        if (!string.IsNullOrWhiteSpace(topFolder))
        {
            foreach (var tree in candidates)
            {
                if (ReleaseTitleNormalizer.TitlesMatch(topFolder, tree.Work.Title, tree.Work.Year) ||
                    tree.Work.AltTitles.Any(alt => ReleaseTitleNormalizer.TitlesMatch(topFolder, alt, tree.Work.Year)))
                {
                    return tree;
                }
            }
        }

        // 2. Parsed filename title match
        foreach (var tree in candidates)
        {
            if (ReleaseTitleNormalizer.TitlesMatch(parsed.CleanTitle, tree.Work.Title, tree.Work.Year) ||
                tree.Work.AltTitles.Any(alt => ReleaseTitleNormalizer.TitlesMatch(parsed.CleanTitle, alt, tree.Work.Year)))
            {
                if (parsed.Year.HasValue && tree.Work.Year.HasValue && Math.Abs(parsed.Year.Value - tree.Work.Year.Value) > 1)
                {
                    continue;
                }

                return tree;
            }
        }

        return null;
    }

    private static IReadOnlyList<MediaUnitDetail> ResolveUnitsForDiskFile(
        MediaWorkDetailTree workTree,
        ParsedReleaseInfo parsed,
        string relPath)
    {
        var allUnits = workTree.Containers.SelectMany(c => c.Units).ToList();
        if (allUnits.Count == 0)
        {
            return [];
        }

        if (string.Equals(workTree.Work.WorkKind, MetadataVerticals.Movies, StringComparison.OrdinalIgnoreCase))
        {
            return [allUnits[0]];
        }

        if (workTree.Work.WorkKind is MetadataVerticals.Tv or MetadataVerticals.Anime)
        {
            if (parsed.EpisodeNumbers.Count > 0)
            {
                var targetSeason = parsed.SeasonNumber ?? 1;
                var seasonContainer = workTree.Containers.FirstOrDefault(c => c.ContainerNumber == targetSeason);
                if (seasonContainer is not null)
                {
                    var matched = seasonContainer.Units
                        .Where(u => parsed.EpisodeNumbers.Contains(u.UnitNumber))
                        .ToList();
                    if (matched.Count > 0)
                    {
                        return matched;
                    }
                }
            }

            if (parsed.AbsoluteEpisodeNumbers.Count > 0)
            {
                var absMatched = allUnits
                    .Where(u => u.AbsoluteNumber.HasValue && parsed.AbsoluteEpisodeNumbers.Contains(u.AbsoluteNumber.Value))
                    .ToList();
                if (absMatched.Count > 0)
                {
                    return absMatched;
                }
            }

            return [];
        }

        if (allUnits.Count == 1)
        {
            return [allUnits[0]];
        }

        if (parsed.EpisodeNumbers.Count > 0)
        {
            var matched = allUnits.Where(u => parsed.EpisodeNumbers.Contains(u.UnitNumber)).ToList();
            if (matched.Count > 0)
            {
                return matched;
            }
        }

        return [];
    }

    private static IReadOnlyList<MediaUnitDetail> ResolveUnitsFromExplicitOverride(
        MediaWorkDetailTree tree,
        MapUnmappedFileItemRequest item,
        UnmappedFileRow row)
    {
        var allUnits = tree.Containers.SelectMany(c => c.Units).ToList();
        if (item.UnitIds is { Count: > 0 })
        {
            var set = item.UnitIds.ToHashSet();
            return allUnits.Where(u => set.Contains(u.Id)).ToList();
        }

        if (string.Equals(tree.Work.WorkKind, MetadataVerticals.Movies, StringComparison.OrdinalIgnoreCase))
        {
            return allUnits.Take(1).ToList();
        }

        var epNums = item.EpisodeNumbers is { Count: > 0 }
            ? item.EpisodeNumbers
            : (JsonSerializer.Deserialize<int[]>(row.ParsedEpisodesJson, JsonOpts) ?? []);
        var seasonNum = item.SeasonNumber ?? row.ParsedSeason ?? 1;

        if (epNums.Count > 0)
        {
            var season = tree.Containers.FirstOrDefault(c => c.ContainerNumber == seasonNum) ?? tree.Containers.FirstOrDefault();
            if (season is not null)
            {
                var matched = season.Units.Where(u => epNums.Contains(u.UnitNumber)).ToList();
                if (matched.Count > 0)
                {
                    return matched;
                }
            }
        }

        return allUnits.Count == 1 ? [allUnits[0]] : [];
    }

    private static ParsedReleaseInfo BuildParsedInfoForWorkUnit(
        MediaWorkSummary work,
        MediaContainerDetail container,
        IReadOnlyList<MediaUnitDetail> units,
        ParsedReleaseInfo parsed,
        string qualityName,
        IReadOnlyList<string> langs) =>
        new(
            RawTitle: parsed.RawTitle,
            MediaVertical: work.WorkKind,
            CleanTitle: work.Title,
            ArtistOrAuthor: parsed.ArtistOrAuthor,
            Year: work.Year,
            SeasonNumber: container.ContainerNumber,
            EpisodeNumbers: units.Select(u => u.UnitNumber).ToArray(),
            AbsoluteEpisodeNumbers: units.Where(u => u.AbsoluteNumber.HasValue).Select(u => u.AbsoluteNumber!.Value).ToArray(),
            IsFullSeasonPack: false,
            MovieEdition: parsed.MovieEdition,
            Resolution: parsed.Resolution,
            Source: InferSourceFromQualityName(qualityName) ?? parsed.Source,
            VideoCodec: parsed.VideoCodec,
            HdrFormat: parsed.HdrFormat,
            AudioCodec: parsed.AudioCodec,
            MusicFormat: work.WorkKind == MetadataVerticals.Music ? qualityName : null,
            BookFormat: work.WorkKind is MetadataVerticals.Ebooks or MetadataVerticals.Audiobooks or MetadataVerticals.Comics ? qualityName : null,
            DetectedLanguages: langs,
            IsDualAudio: langs.Count > 1,
            IsMultiSubs: false,
            ReleaseGroup: parsed.ReleaseGroup);

    private static string? FindExistingWorkSubfolder(string fullRoot, MediaWorkSummary work)
    {
        if (!Directory.Exists(fullRoot))
        {
            return null;
        }

        foreach (var dir in Directory.EnumerateDirectories(fullRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (new DirectoryInfo(dir).LinkTarget is not null)
            {
                continue;
            }

            var folderName = Path.GetFileName(dir);
            if (ReleaseTitleNormalizer.TitlesMatch(folderName, work.Title, work.Year) ||
                work.AltTitles.Any(a => ReleaseTitleNormalizer.TitlesMatch(folderName, a, work.Year)))
            {
                return dir;
            }
        }

        return null;
    }

    private static bool HasSymlinkEscapeOrTraversal(string fullRoot, string rootPrefix, string targetFullPath)
    {
        if (!targetFullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var physicalRoot = fullRoot;
            var rootDirInfo = new DirectoryInfo(fullRoot);
            if (rootDirInfo.Exists && rootDirInfo.ResolveLinkTarget(returnFinalTarget: true) is { } rootTarget)
            {
                physicalRoot = Path.GetFullPath(rootTarget.FullName);
            }
            var physicalRootWithSep = physicalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            var rel = Path.GetRelativePath(fullRoot, targetFullPath);
            var walk = fullRoot;
            foreach (var part in rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                walk = Path.Combine(walk, part);
                FileSystemInfo fsi = Directory.Exists(walk) ? new DirectoryInfo(walk) : new FileInfo(walk);
                if (fsi.LinkTarget is null)
                {
                    continue;
                }

                var resolved = fsi.ResolveLinkTarget(returnFinalTarget: true);
                if (resolved is null)
                {
                    return true;
                }

                var resolvedFull = Path.GetFullPath(resolved.FullName);
                if (!resolvedFull.Equals(physicalRoot, StringComparison.Ordinal) &&
                    !resolvedFull.StartsWith(physicalRootWithSep, StringComparison.Ordinal) &&
                    !resolvedFull.StartsWith(rootPrefix, StringComparison.Ordinal))
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

    private async Task UpsertUnmappedFileAsync(
        long libraryId,
        string fullPath,
        string relPath,
        string fileName,
        long sizeBytes,
        ParsedReleaseInfo parsed,
        string qualityLabel,
        long? suggestedWorkId,
        string? suggestedWorkTitle,
        string reason,
        string nowIso,
        CancellationToken ct)
    {
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                INSERT INTO unmapped_media_files (
                    library_id, full_path, relative_path, file_name, size_bytes,
                    parsed_title, parsed_season, parsed_episodes_json, parsed_quality,
                    parsed_languages_json, suggested_work_id, suggested_work_title,
                    reason, discovered_at
                ) VALUES (
                    @LibId, @FullPath, @RelPath, @FileName, @SizeBytes,
                    @ParsedTitle, @ParsedSeason, @EpisodesJson, @Quality,
                    @LangsJson, @SuggestedWorkId, @SuggestedWorkTitle,
                    @Reason, @Now
                )
                ON CONFLICT(library_id, relative_path) DO UPDATE SET
                    full_path            = excluded.full_path,
                    size_bytes           = excluded.size_bytes,
                    parsed_title         = excluded.parsed_title,
                    parsed_season        = excluded.parsed_season,
                    parsed_episodes_json = excluded.parsed_episodes_json,
                    parsed_quality       = excluded.parsed_quality,
                    parsed_languages_json= excluded.parsed_languages_json,
                    suggested_work_id    = excluded.suggested_work_id,
                    suggested_work_title = excluded.suggested_work_title,
                    reason               = excluded.reason,
                    discovered_at        = excluded.discovered_at;
                """,
                new
                {
                    LibId = libraryId,
                    FullPath = fullPath,
                    RelPath = relPath,
                    FileName = fileName,
                    SizeBytes = sizeBytes,
                    ParsedTitle = parsed.CleanTitle,
                    ParsedSeason = parsed.SeasonNumber,
                    EpisodesJson = JsonSerializer.Serialize(parsed.EpisodeNumbers, JsonOpts),
                    Quality = qualityLabel,
                    LangsJson = JsonSerializer.Serialize(parsed.DetectedLanguages, JsonOpts),
                    SuggestedWorkId = suggestedWorkId,
                    SuggestedWorkTitle = suggestedWorkTitle,
                    Reason = reason,
                    Now = nowIso,
                },
                transaction: tx),
            ct);
    }

    private static string[] GetAllowedExtensions(string vertical) =>
        vertical.ToLowerInvariant() switch
        {
            MetadataVerticals.Music => MusicExtensions,
            MetadataVerticals.Ebooks or MetadataVerticals.Audiobooks or MetadataVerticals.Comics => BookAndComicExtensions,
            _ => VideoExtensions,
        };

    // Different movie editions (Extended vs Theatrical) legitimately share a unit; same edition (or none) does not
    private static bool HasFileForEdition(MediaUnitDetail unit, string? edition)
    {
        if (unit.Files.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(edition))
        {
            return true;
        }

        var editionIds = unit.Editions
            .Where(e => string.Equals(e.EditionName, edition, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Id)
            .ToHashSet();
        return unit.Files.Any(f => f.EditionId is { } id && editionIds.Contains(id));
    }

    private static string BuildQualityLabel(ParsedReleaseInfo parsed, string ext)
    {
        // Same naming as imports, so an adopted file never looks like a different quality than its release
        if (!string.IsNullOrWhiteSpace(parsed.Source) || !string.IsNullOrWhiteSpace(parsed.Resolution) ||
            !string.IsNullOrWhiteSpace(parsed.MusicFormat) || !string.IsNullOrWhiteSpace(parsed.BookFormat))
        {
            return parsed.ToQualityName();
        }

        // A bare filename says nothing about quality; the extension is a container, not a quality
        return QualityNames.Unknown;
    }

    private static string? InferSourceFromQualityName(string? qualityName)
    {
        if (string.IsNullOrWhiteSpace(qualityName))
        {
            return null;
        }

        var upper = qualityName.ToUpperInvariant();
        if (upper.Contains("REMUX")) return "Remux";
        if (upper.Contains("BLURAY") || upper.Contains("BLU-RAY")) return "BluRay";
        if (upper.Contains("WEBDL") || upper.Contains("WEB-DL")) return "WEB-DL";
        if (upper.Contains("WEBRIP") || upper.Contains("WEB-RIP")) return "WEBRip";
        if (upper.Contains("HDTV")) return "HDTV";
        return null;
    }

    private static string FormatSeasonEp(ParsedReleaseInfo parsed)
    {
        if (parsed.SeasonNumber.HasValue || parsed.EpisodeNumbers.Count > 0)
        {
            var s = parsed.SeasonNumber ?? 1;
            var e = parsed.EpisodeNumbers.Count > 0 ? string.Join(",", parsed.EpisodeNumbers) : "?";
            return $"S{s:D2}E{e}";
        }

        return "no season/episode token";
    }

    private static string NormalizeRelPathKey(string relPath) =>
        relPath.Replace('\\', '/').TrimStart('/');

    private static string NormalizeColonMode(string? raw) =>
        (raw?.Trim().ToLowerInvariant()) switch
        {
            "delete" => "delete",
            "dash" => "dash",
            "space_dash" => "space_dash",
            _ => "space",
        };

    private static string UnwrapSettingString(string valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
        {
            return string.Empty;
        }

        try
        {
            if (valueJson.StartsWith('"'))
            {
                return JsonSerializer.Deserialize<string>(valueJson) ?? string.Empty;
            }
        }
        catch
        {
        }

        return valueJson.Trim();
    }

    private static Task UpsertSettingAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        string key,
        string value,
        string nowIso) =>
        conn.ExecuteAsync(
            """
            INSERT INTO app_settings (key, value, is_secret, updated_at)
            VALUES (@Key, @Val, 0, @Now)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, is_secret = 0, updated_at = excluded.updated_at;
            """,
            new { Key = key, Val = value, Now = nowIso },
            transaction: tx);

    private sealed class UnmappedFileRow
    {
        public long Id { get; init; }
        public long LibraryId { get; init; }
        public string LibraryName { get; init; } = "";
        public string MediaVertical { get; init; } = "";
        public string FullPath { get; init; } = "";
        public string RelativePath { get; init; } = "";
        public string FileName { get; init; } = "";
        public long SizeBytes { get; init; }
        public string ParsedTitle { get; init; } = "";
        public int? ParsedSeason { get; init; }
        public string ParsedEpisodesJson { get; init; } = "[]";
        public string ParsedQuality { get; init; } = "";
        public string ParsedLanguagesJson { get; init; } = "[]";
        public long? SuggestedWorkId { get; init; }
        public string? SuggestedWorkTitle { get; init; }
        public string Reason { get; init; } = "";
        public string DiscoveredAt { get; init; } = "";
    }
}
