using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Metadata;
using Managerr.Security.Users;

namespace Managerr.Catalog;

public interface ICatalogDiskScanHook
{
    Task ScanWorkDiskFilesAsync(long workId, CancellationToken ct = default);
}

public interface IPolymorphicCatalogService
{
    bool CanUserAccessLibrary(PlexUserSummary? user, LibrarySummary library);
    Task<LibrarySummary> CreateLibraryAsync(CreateLibraryRequest request, CancellationToken ct = default);
    Task<LibrarySummary> UpdateLibraryAsync(long id, UpdateLibraryRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<LibrarySummary>> ListLibrariesAsync(PlexUserSummary? actor = null, CancellationToken ct = default);
    Task<LibrarySummary?> GetLibraryByIdAsync(long id, CancellationToken ct = default);
    Task DeleteLibraryAsync(long id, CancellationToken ct = default);

    Task<MediaWorkDetailTree> AddWorkAsync(CreateMediaWorkRequest request, PlexUserSummary? actor = null, CancellationToken ct = default);
    Task<IReadOnlyList<MediaWorkSummary>> ListWorksAsync(PlexUserSummary? actor = null, long? libraryId = null, string? vertical = null, CancellationToken ct = default);
    Task<IReadOnlyList<MediaWorkSummary>> QueryWorksAsync(CatalogWorkQueryFilter filter, PlexUserSummary? actor = null, CancellationToken ct = default) =>
        ListWorksAsync(actor, filter.LibraryId, filter.Vertical, ct);
    Task<MediaWorkDetailTree?> GetWorkTreeAsync(long workId, PlexUserSummary? actor = null, CancellationToken ct = default);
    Task<MediaWorkDetailTree> UpdateWorkAsync(long workId, UpdateMediaWorkRequest request, CancellationToken ct = default);
    Task<MediaWorkDetailTree> ApplyMonitorPresetAsync(long workId, string monitorPreset, CancellationToken ct = default) =>
        UpdateWorkAsync(workId, new UpdateMediaWorkRequest(MonitorPreset: monitorPreset), ct);
    Task<MediaWorkDetailTree> RefreshWorkMetadataAsync(long workId, string? switchProvider = null, string? episodeOrderingScheme = null, CancellationToken ct = default);
    Task DeleteWorkAsync(long workId, CancellationToken ct = default);
    Task DeleteWorkWithOptionsAsync(long workId, bool deleteFilesOnDisk = false, CancellationToken ct = default) =>
        DeleteWorkAsync(workId, ct);
    Task<BulkEditWorksReportDto> BulkEditWorksAsync(BulkEditWorksRequest request, CancellationToken ct = default) =>
        Task.FromResult(new BulkEditWorksReportDto(request.WorkIds.Count, 0, 0, 0, []));
    Task<CatalogAutoClassifyReportDto> AutoClassifyCatalogWorksAsync(CancellationToken ct = default) =>
        Task.FromResult(new CatalogAutoClassifyReportDto(0, 0, new Dictionary<string, int>(), []));

    Task<MediaWorkDetailTree> UpdateContainerAsync(long containerId, UpdateMediaContainerRequest request, CancellationToken ct = default);
    Task<MediaUnitDetail> UpdateUnitAsync(long unitId, UpdateMediaUnitRequest request, CancellationToken ct = default);
    Task<MediaFileSummary> AttachFileAsync(AttachMediaFileRequest request, CancellationToken ct = default);
    Task<MediaFileSummary?> GetMediaFileByIdAsync(long mediaFileId, CancellationToken ct = default);
    Task<IReadOnlyList<CalendarEntry>> GetCalendarAsync(DateOnly startDate, DateOnly endDate, PlexUserSummary? actor = null, CancellationToken ct = default);

    Task<MediaRequestSummary> SubmitRequestAsync(PlexUserSummary requester, SubmitMediaRequestCommand cmd, CancellationToken ct = default);
    Task<IReadOnlyList<MediaRequestSummary>> ListRequestsAsync(PlexUserSummary? actor = null, CancellationToken ct = default);
    Task<MediaRequestSummary> DecideRequestAsync(long requestId, bool approve, PlexUserSummary approver, CancellationToken ct = default);
}

public sealed partial class PolymorphicCatalogService(
    ISqliteDbFactory dbFactory,
    IFtsSearchService ftsSearch,
    IMetadataFederationService metadataFederation,
    IExternalIdCrosswalkService crosswalkService,
    Managerr.Common.Notifications.IMediaLifecycleNotificationSink? notificationSink = null,
    IAppSettingsService? appSettings = null) : IPolymorphicCatalogService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"[^a-z0-9-]+")]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"^(the|a|an|der|die|das)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingArticleRegex();

    private static string NormalizeOriginalLanguage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "en";
        var l = raw.Trim().ToLowerInvariant();
        return l switch
        {
            "ger" or "deu" or "german" or "deutsch" or "de-de" => "de",
            "eng" or "english" or "en-us" or "en-gb" => "en",
            "jpn" or "jap" or "japanese" => "ja",
            "fra" or "fre" or "french" => "fr",
            "spa" or "esp" or "spanish" or "castellano" => "es",
            "ita" or "italian" => "it",
            "kor" or "korean" => "ko",
            "zho" or "chi" or "chinese" or "cmn" => "zh",
            "rus" or "russian" => "ru",
            "por" or "portuguese" => "pt",
            "nld" or "dut" or "dutch" => "nl",
            "pol" or "polish" => "pl",
            _ => l.Length > 2 && l.Contains('-') ? l.Split('-')[0] : l
        };
    }

    public bool CanUserAccessLibrary(PlexUserSummary? user, LibrarySummary library)
    {
        if (user is null)
        {
            return true;
        }

        if (user.IsServerOwner || user.EffectivePermissions.HasAllLibrariesAccess)
        {
            return true;
        }

        if (user.EffectivePermissions.ExplicitAllowedLibraryIds.Contains(library.Id))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(library.PlexSectionId) &&
            user.EffectivePermissions.SharedPlexSectionIds.Contains(library.PlexSectionId, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static bool IsForbiddenSystemRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return true;
        }

        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (IsForbiddenPathValue(full, tempRoot))
        {
            return true;
        }

        try
        {
            var current = string.Empty;
            foreach (var seg in full.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = string.IsNullOrEmpty(current) ? "/" + seg : current + "/" + seg;
                FileSystemInfo fsi = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (fsi.LinkTarget is not null)
                {
                    var resolved = fsi.ResolveLinkTarget(returnFinalTarget: true);
                    if (resolved is null)
                    {
                        return true;
                    }

                    var resolvedFull = Path.GetFullPath(resolved.FullName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (IsForbiddenPathValue(resolvedFull, tempRoot))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    private static bool IsForbiddenPathValue(string normalizedFull, string tempRoot)
    {
        if (string.IsNullOrEmpty(normalizedFull))
        {
            return true; // "/"
        }

        if (!string.IsNullOrEmpty(tempRoot) &&
            (normalizedFull.Equals(tempRoot, StringComparison.OrdinalIgnoreCase) ||
             normalizedFull.StartsWith(tempRoot + "/", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        ReadOnlySpan<string> forbiddenPrefixes =
        [
            "/etc", "/proc", "/sys", "/dev", "/root", "/boot",
            "/usr", "/bin", "/sbin", "/lib", "/lib64", "/app",
            "/config", "/var", "/run",
        ];

        foreach (var prefix in forbiddenPrefixes)
        {
            if (normalizedFull.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                normalizedFull.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<LibrarySummary> CreateLibraryAsync(CreateLibraryRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.RootFolderPath))
        {
            throw new ArgumentException("Library name and rootFolderPath are required.");
        }

        if (IsForbiddenSystemRoot(request.RootFolderPath))
        {
            throw new ArgumentException($"Root folder path '{request.RootFolderPath}' points to a protected system or configuration directory.");
        }

        var mediaType = NormalizeVertical(request.MediaType);
        var slug = NormalizeSlug(request.Slug, request.Name);
        var waterfall = (request.MetadataWaterfall is { Count: > 0 }
                ? request.MetadataWaterfall
                : metadataFederation.GetDefaultWaterfall(mediaType))
            .Select(p => p.Trim().ToLowerInvariant())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var resolvedDefaultProfileId = request.DefaultProfileId;
        if (!resolvedDefaultProfileId.HasValue && mediaType is MetadataVerticals.Movies or MetadataVerticals.Tv or MetadataVerticals.Anime)
        {
            await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
            resolvedDefaultProfileId = await readConn.ExecuteScalarAsync<long?>(
                "SELECT id FROM quality_profiles WHERE slug = 'german-first-best-quality' LIMIT 1;");
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var id = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO libraries (
                    slug, name, media_type, root_folder_path, plex_section_id,
                    metadata_waterfall_json, default_profile_id, created_at, updated_at
                ) VALUES (
                    @Slug, @Name, @MediaType, @RootPath, @PlexSectionId,
                    @WaterfallJson, @DefaultProfileId, @Now, @Now
                );
                SELECT last_insert_rowid();
                """,
                new
                {
                    Slug = slug,
                    Name = request.Name.Trim(),
                    MediaType = mediaType,
                    RootPath = request.RootFolderPath.Trim(),
                    PlexSectionId = string.IsNullOrWhiteSpace(request.PlexSectionId) ? null : request.PlexSectionId.Trim(),
                    WaterfallJson = JsonSerializer.Serialize(waterfall, JsonOpts),
                    DefaultProfileId = resolvedDefaultProfileId,
                    Now = now,
                },
                transaction: tx),
            ct);

        return (await GetLibraryByIdAsync(id, ct))!;
    }

    public async Task<LibrarySummary> UpdateLibraryAsync(long id, UpdateLibraryRequest request, CancellationToken ct = default)
    {
        var existing = await GetLibraryByIdAsync(id, ct)
            ?? throw new KeyNotFoundException($"Library #{id} not found.");

        if (!string.IsNullOrWhiteSpace(request.RootFolderPath) && IsForbiddenSystemRoot(request.RootFolderPath))
        {
            throw new ArgumentException($"Root folder path '{request.RootFolderPath}' points to a protected system or configuration directory.");
        }

        var waterfall = request.MetadataWaterfall is { Count: > 0 }
            ? request.MetadataWaterfall.Select(x => x.Trim().ToLowerInvariant()).Distinct().ToArray()
            : existing.MetadataWaterfall.ToArray();

        var now = DateTimeOffset.UtcNow.ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                UPDATE libraries
                SET name                     = @Name,
                    root_folder_path         = @RootPath,
                    plex_section_id          = @PlexSectionId,
                    metadata_waterfall_json  = @WaterfallJson,
                    default_profile_id       = @DefaultProfileId,
                    updated_at               = @Now
                WHERE id = @Id;
                """,
                new
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim(),
                    RootPath = string.IsNullOrWhiteSpace(request.RootFolderPath) ? existing.RootFolderPath : request.RootFolderPath.Trim(),
                    PlexSectionId = request.PlexSectionId ?? existing.PlexSectionId,
                    WaterfallJson = JsonSerializer.Serialize(waterfall, JsonOpts),
                    DefaultProfileId = request.DefaultProfileId ?? existing.DefaultProfileId,
                    Now = now,
                },
                transaction: tx),
            ct);

        return (await GetLibraryByIdAsync(id, ct))!;
    }

    public async Task<IReadOnlyList<LibrarySummary>> ListLibrariesAsync(PlexUserSummary? actor = null, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<LibraryRow>(
            """
            SELECT l.*,
                   (SELECT COUNT(1) FROM media_works mw WHERE mw.library_id = l.id) AS work_count
            FROM libraries l
            ORDER BY l.name COLLATE NOCASE ASC;
            """);

        return rows
            .Select(MapLibrary)
            .Select(EnrichWithDiskSpace)
            .Where(lib => CanUserAccessLibrary(actor, lib))
            .ToList();
    }

    public async Task<LibrarySummary?> GetLibraryByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await conn.QuerySingleOrDefaultAsync<LibraryRow>(
            """
            SELECT l.*,
                   (SELECT COUNT(1) FROM media_works mw WHERE mw.library_id = l.id) AS work_count
            FROM libraries l
            WHERE l.id = @Id;
            """,
            new { Id = id });
        return row is null ? null : EnrichWithDiskSpace(MapLibrary(row));
    }

    private static (long? FreeSpace, long? TotalSpace, bool Accessible) ProbeDiskSpace(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return (null, null, false);
            }

            var probeTarget = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            while (!string.IsNullOrWhiteSpace(probeTarget) && !Directory.Exists(probeTarget))
            {
                probeTarget = Path.GetDirectoryName(probeTarget);
            }

            if (!string.IsNullOrWhiteSpace(probeTarget) && Directory.Exists(probeTarget))
            {
                var root = Path.GetPathRoot(Path.GetFullPath(probeTarget)) ?? "/";
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    return (drive.AvailableFreeSpace, drive.TotalSize, Directory.Exists(path));
                }
            }
        }
        catch
        {
            // Fall through to safe defaults
        }

        return (null, null, false);
    }

    private static LibrarySummary EnrichWithDiskSpace(LibrarySummary library)
    {
        var (free, total, accessible) = ProbeDiskSpace(library.RootFolderPath);
        return library with
        {
            FreeSpaceBytes = free,
            TotalSpaceBytes = total,
            RootAccessible = accessible
        };
    }

    public Task DeleteLibraryAsync(long id, CancellationToken ct = default) =>
        dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync("DELETE FROM libraries WHERE id = @Id;", new { Id = id }, transaction: tx),
            ct);

    public async Task<MediaWorkDetailTree> AddWorkAsync(
        CreateMediaWorkRequest request,
        PlexUserSummary? actor = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ArgumentException("Work title is required.");
        }

        var library = await GetLibraryByIdAsync(request.LibraryId, ct)
            ?? throw new KeyNotFoundException($"Library #{request.LibraryId} not found.");

        if (actor is not null && !CanUserAccessLibrary(actor, library))
        {
            throw new UnauthorizedAccessException($"Access denied to library '{library.Name}'.");
        }

        var vertical = NormalizeVertical(request.WorkKind ?? library.MediaType);
        if (!LibraryAcceptsWorkKind(library.MediaType, vertical))
        {
            throw new ArgumentException(
                $"A '{vertical}' work does not belong in library '{library.Name}' ({library.MediaType}); pick a matching library.");
        }

        var seedExternalIds = request.ExternalIds ?? new Dictionary<string, string>();
        await crosswalkService.LinkClusterAsync(seedExternalIds, vertical, 1.0, ct);
        var expandedIds = new Dictionary<string, string>(
            await crosswalkService.ResolveConnectedIdsAsync(seedExternalIds, ct),
            StringComparer.OrdinalIgnoreCase);

        var primaryTitleTrimmed = request.Title.Trim();
        var altTitles = (request.AltTitles ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t) && !string.Equals(t.Trim(), primaryTitleTrimmed, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var lockedFields = (request.LockedFields ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var tags = (request.Tags ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        IReadOnlyList<MetadataContainerSeed>? seedContainers = request.Containers;
        string? hydratedPosterUrl = request.PosterUrl;
        string? hydratedOverview = request.Overview;
        int? hydratedYear = request.Year;
        IReadOnlyList<string>? hydratedGenres = null;
        string? hydratedFormatHint = null;
        var needsContainerHydration = vertical is MetadataVerticals.Tv or MetadataVerticals.Anime &&
            (seedContainers is null ||
             seedContainers.All(c => c.Units.Count <= 1 && (c.Units.Count == 0 || c.Units[0].Title.StartsWith("TBA", StringComparison.Ordinal) || c.Units[0].Title == "Episode 1")));
        var needsPosterHydration = string.IsNullOrWhiteSpace(hydratedPosterUrl);
        var needsAltTitlesHydration = altTitles.Length == 0;

        if (!request.SkipUpstreamHydration && expandedIds.Count > 0 && (needsContainerHydration || needsPosterHydration || needsAltTitlesHydration))
        {
            try
            {
                var hydrated = await metadataFederation.ResolveWorkAsync(
                    vertical,
                    request.PrimaryProvider,
                    expandedIds,
                    library.MetadataWaterfall,
                    ct);
                if (hydrated is not null)
                {
                    if (needsContainerHydration && hydrated.Containers is { Count: > 0 } hc && hc.Any(c => c.Units.Count > 0))
                    {
                        seedContainers = hc;
                    }

                    if (string.IsNullOrWhiteSpace(hydratedPosterUrl) && !string.IsNullOrWhiteSpace(hydrated.PosterUrl))
                    {
                        hydratedPosterUrl = hydrated.PosterUrl;
                    }

                    if (string.IsNullOrWhiteSpace(hydratedOverview) && !string.IsNullOrWhiteSpace(hydrated.Overview))
                    {
                        hydratedOverview = hydrated.Overview;
                    }

                    if (hydrated.AltTitles is { Count: > 0 })
                    {
                        altTitles = altTitles
                            .Concat(hydrated.AltTitles)
                            .Where(t => !string.IsNullOrWhiteSpace(t) && !string.Equals(t.Trim(), primaryTitleTrimmed, StringComparison.OrdinalIgnoreCase))
                            .Select(t => t.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    }

                    foreach (var (hk, hv) in hydrated.ExternalIds)
                    {
                        if (!string.IsNullOrWhiteSpace(hv))
                        {
                            expandedIds.TryAdd(hk, hv);
                        }
                    }

                    hydratedYear ??= hydrated.Year;
                    hydratedGenres = hydrated.Genres;
                    if (string.Equals(hydrated.ClassificationKind, "anime-movie", StringComparison.OrdinalIgnoreCase))
                    {
                        hydratedFormatHint = "MOVIE";
                    }
                }
            }
            catch
            {
                // Keep seed/default metadata if live upstream hydration is offline
            }
        }

        var ftsDocId = await ftsSearch.IndexItemAsync(
            mediaType: vertical,
            title: primaryTitleTrimmed,
            altTitles: string.Join(" | ", altTitles),
            year: hydratedYear,
            externalIds: FormatExternalIdsForFts(expandedIds),
            ct: ct);

        var autoClassification = MediaTaxonomyClassifier.Classify(
            vertical: vertical,
            originalLanguage: request.OriginalLanguage,
            genres: hydratedGenres,
            externalIds: expandedIds,
            title: request.Title,
            altTitles: altTitles,
            formatHint: hydratedFormatHint,
            overview: hydratedOverview,
            existingTags: tags,
            rootFolderPath: library.RootFolderPath);

        if (autoClassification.SuggestedTags.Count > 0 &&
            !lockedFields.Contains("tags", StringComparer.OrdinalIgnoreCase))
        {
            tags = MediaTaxonomyClassifier.MergeWithUserTags(tags, autoClassification.SuggestedTags).ToArray();
        }

        var monitorMode = NormalizeMonitorPreset(request.MonitorMode ?? (request.Monitored ? "all" : "none"));
        var workMonitored = monitorMode != "none" && request.Monitored;
        var effectiveDualSlots = monitorMode is "ebookonly" or "audiobookonly" ? true : request.DualFormatBookSlots;
        var minAvailability = MinimumAvailabilityPolicy.Normalize(request.MinimumAvailability);
        var effectiveQualityProfileId = request.QualityProfileId ?? request.InitialProfileOverrideId;

        var containers = BuildInitialContainers(
            vertical,
            request.Title.Trim(),
            hydratedYear,
            seedContainers,
            effectiveDualSlots);

        var nonSpecialMaxSeason = containers.Where(c => c.ContainerNumber > 0).Select(c => c.ContainerNumber).DefaultIfEmpty(1).Max();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var todayIso = today.ToString("yyyy-MM-dd");
        var recentCutoffIso = today.AddDays(-90).ToString("yyyy-MM-dd");

        bool IsContainerMonitored(MetadataContainerSeed c) =>
            workMonitored && monitorMode switch
            {
                "none" => false,
                "existing" => false, // newly added work has 0 attached files yet
                "firstseason" => c.ContainerNumber == 1,
                "lastseason" or "latestseason" => c.ContainerNumber == nonSpecialMaxSeason,
                "pilot" => c.ContainerNumber == 1,
                "monitorspecials" => true,
                "unmonitorspecials" => c.ContainerNumber != 0,
                _ => c.ContainerNumber != 0 || containers.Count == 1,
            };

        bool IsUnitMonitored(MetadataContainerSeed c, MetadataUnitSeed u)
        {
            if (!workMonitored || monitorMode == "none")
            {
                return false;
            }

            if (monitorMode == "monitorspecials")
            {
                return true;
            }

            if (monitorMode == "unmonitorspecials")
            {
                return c.ContainerNumber != 0;
            }

            if (!IsContainerMonitored(c))
            {
                return false;
            }

            var airIso = u.AirOrReleaseDate ?? c.ReleaseDate;
            return monitorMode switch
            {
                "ebookonly" => u.UnitKind != "audiobook_slot",
                "audiobookonly" => u.UnitKind != "ebook_slot",
                "existing" => false,
                "missing" => true,
                "pilot" => c.ContainerNumber == 1 && u.UnitNumber == 1,
                "future" => string.IsNullOrWhiteSpace(airIso) ||
                            string.Compare(airIso, todayIso, StringComparison.Ordinal) >= 0,
                "recent" => string.IsNullOrWhiteSpace(airIso) ||
                            string.Compare(airIso, recentCutoffIso, StringComparison.Ordinal) >= 0,
                _ => true,
            };
        }

        var reqAudio = (request.InitialRequiredAudioLangs ?? [])
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();

        var reqSubs = (request.InitialRequiredSubLangs ?? [])
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();

        var now = DateTimeOffset.UtcNow.ToString("O");
        var sortTitle = ComputeSortTitle(request.Title.Trim());

        var workId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                if (request.LanguagePolicyId.HasValue)
                {
                    if (request.LanguagePolicyId.Value <= 0)
                    {
                        throw new ArgumentException($"Language policy #{request.LanguagePolicyId.Value} does not exist.");
                    }
                    var lpExists = await conn.ExecuteScalarAsync<int?>(
                        "SELECT 1 FROM language_policies WHERE id = @Id;",
                        new { Id = request.LanguagePolicyId.Value },
                        transaction: tx);
                    if (!lpExists.HasValue)
                    {
                        throw new ArgumentException($"Language policy #{request.LanguagePolicyId.Value} does not exist.");
                    }
                }

                if (request.QualityProfileId.HasValue)
                {
                    if (request.QualityProfileId.Value <= 0)
                    {
                        throw new ArgumentException($"Quality profile #{request.QualityProfileId.Value} does not exist.");
                    }
                    var qpExists = await conn.ExecuteScalarAsync<int?>(
                        "SELECT 1 FROM quality_profiles WHERE id = @Id;",
                        new { Id = request.QualityProfileId.Value },
                        transaction: tx);
                    if (!qpExists.HasValue)
                    {
                        throw new ArgumentException($"Quality profile #{request.QualityProfileId.Value} does not exist.");
                    }
                }

                var createdWorkId = await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO media_works (
                        library_id, work_kind, primary_provider, episode_ordering_scheme,
                        title, sort_title, alt_titles_json, original_language, year, status,
                        overview, poster_url, monitored, minimum_availability, quality_profile_id,
                        language_policy_id,
                        locked_fields_json, external_ids_json, tags_json, plex_rating_key,
                        fts_doc_id, added_by_user_id, added_at, updated_at
                    ) VALUES (
                        @LibraryId, @WorkKind, @PrimaryProvider, @Ordering,
                        @Title, @SortTitle, @AltTitlesJson, @OrigLang, @Year, @Status,
                        @Overview, @PosterUrl, @Monitored, @MinimumAvailability, @QualityProfileId,
                        @LanguagePolicyId,
                        @LockedJson, @ExtIdsJson, @TagsJson, @PlexKey,
                        @FtsDocId, @AddedBy, @Now, @Now
                    );
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        request.LibraryId,
                        WorkKind = vertical,
                        PrimaryProvider = string.IsNullOrWhiteSpace(request.PrimaryProvider) ? "auto" : request.PrimaryProvider.Trim().ToLowerInvariant(),
                        Ordering = string.IsNullOrWhiteSpace(request.EpisodeOrderingScheme) ? "aired" : request.EpisodeOrderingScheme.Trim().ToLowerInvariant(),
                        Title = request.Title.Trim(),
                        SortTitle = sortTitle,
                        AltTitlesJson = JsonSerializer.Serialize(altTitles, JsonOpts),
                        OrigLang = NormalizeOriginalLanguage(request.OriginalLanguage),
                        Year = hydratedYear,
                        Status = string.IsNullOrWhiteSpace(request.Status) ? "released" : request.Status.Trim().ToLowerInvariant(),
                        Overview = hydratedOverview ?? "",
                        PosterUrl = hydratedPosterUrl,
                        Monitored = workMonitored ? 1 : 0,
                        MinimumAvailability = minAvailability,
                        QualityProfileId = effectiveQualityProfileId,
                        LanguagePolicyId = request.LanguagePolicyId,
                        LockedJson = JsonSerializer.Serialize(lockedFields, JsonOpts),
                        ExtIdsJson = JsonSerializer.Serialize(expandedIds, JsonOpts),
                        TagsJson = JsonSerializer.Serialize(tags, JsonOpts),
                        PlexKey = request.PlexRatingKey,
                        FtsDocId = ftsDocId,
                        AddedBy = actor?.Id,
                        Now = now,
                    },
                    transaction: tx);

                foreach (var c in containers)
                {
                    var cMon = IsContainerMonitored(c);
                    var containerId = await conn.ExecuteScalarAsync<long>(
                        """
                        INSERT INTO media_containers (
                            work_id, container_kind, container_number, title,
                            release_date, monitored, external_ids_json, created_at
                        ) VALUES (
                            @WorkId, @Kind, @Num, @Title,
                            @ReleaseDate, @Monitored, @ExtIdsJson, @Now
                        );
                        SELECT last_insert_rowid();
                        """,
                        new
                        {
                            WorkId = createdWorkId,
                            Kind = c.ContainerKind,
                            Num = c.ContainerNumber,
                            c.Title,
                            c.ReleaseDate,
                            Monitored = cMon ? 1 : 0,
                            ExtIdsJson = JsonSerializer.Serialize(c.ExternalIds ?? new Dictionary<string, string>(), JsonOpts),
                            Now = now,
                        },
                        transaction: tx);

                    foreach (var u in c.Units)
                    {
                        var uMon = IsUnitMonitored(c, u);
                        await conn.ExecuteAsync(
                            """
                            INSERT INTO media_units (
                                work_id, container_id, unit_kind, unit_number, absolute_number,
                                title, air_or_release_date, monitored, profile_override_id,
                                required_audio_langs_json, required_sub_langs_json,
                                quality_cutoff_met, language_cutoff_met, created_at
                            ) VALUES (
                                @WorkId, @ContainerId, @Kind, @Num, @AbsNum,
                                @Title, @AirDate, @Monitored, @ProfileOverrideId,
                                @AudioJson, @SubJson,
                                0, 0, @Now
                            );
                            """,
                            new
                            {
                                WorkId = createdWorkId,
                                ContainerId = containerId,
                                Kind = u.UnitKind,
                                Num = u.UnitNumber,
                                AbsNum = u.AbsoluteNumber,
                                u.Title,
                                AirDate = u.AirOrReleaseDate ?? c.ReleaseDate,
                                Monitored = uMon ? 1 : 0,
                                ProfileOverrideId = request.InitialProfileOverrideId,
                                AudioJson = JsonSerializer.Serialize(reqAudio, JsonOpts),
                                SubJson = JsonSerializer.Serialize(reqSubs, JsonOpts),
                                Now = now,
                            },
                            transaction: tx);
                    }
                }

                return createdWorkId;
            },
            ct);

        return (await GetWorkTreeAsync(workId, actor: null, ct))!;
    }

    public Task<IReadOnlyList<MediaWorkSummary>> ListWorksAsync(
        PlexUserSummary? actor = null,
        long? libraryId = null,
        string? vertical = null,
        CancellationToken ct = default) =>
        QueryWorksAsync(new CatalogWorkQueryFilter(LibraryId: libraryId, Vertical: vertical), actor, ct);

    public async Task<IReadOnlyList<MediaWorkSummary>> QueryWorksAsync(
        CatalogWorkQueryFilter filter,
        PlexUserSummary? actor = null,
        CancellationToken ct = default)
    {
        var accessibleLibs = (await ListLibrariesAsync(actor, ct)).ToDictionary(l => l.Id);
        if (filter.LibraryId.HasValue && !accessibleLibs.ContainsKey(filter.LibraryId.Value))
        {
            if (actor is not null)
            {
                var rawLib = await GetLibraryByIdAsync(filter.LibraryId.Value, ct);
                if (rawLib is not null)
                {
                    throw new UnauthorizedAccessException($"Access denied to library '{rawLib.Name}'.");
                }
            }

            return [];
        }

        if (accessibleLibs.Count == 0)
        {
            return [];
        }

        var todayIso = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<WorkRow>(
            $"""
            {WorkSummarySelectSql}
            ORDER BY mw.sort_title COLLATE NOCASE ASC;
            """,
            new { TodayIso = todayIso });

        var summaries = rows.Select(MapWorkSummary);
        return CatalogWorkFilterEvaluator.Apply(summaries, accessibleLibs, filter);
    }

    public async Task<MediaWorkDetailTree?> GetWorkTreeAsync(
        long workId,
        PlexUserSummary? actor = null,
        CancellationToken ct = default)
    {
        var todayIso = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var workRow = await conn.QuerySingleOrDefaultAsync<WorkRow>(
            $"""
            {WorkSummarySelectSql}
            WHERE mw.id = @Id;
            """,
            new { Id = workId, TodayIso = todayIso });

        if (workRow is null)
        {
            return null;
        }

        if (actor is not null)
        {
            var lib = await GetLibraryByIdAsync(workRow.LibraryId, ct);
            if (lib is null || !CanUserAccessLibrary(actor, lib))
            {
                throw new UnauthorizedAccessException("You do not have permission to view this library's media.");
            }
        }

        var containerRows = (await conn.QueryAsync<ContainerRow>(
            "SELECT * FROM media_containers WHERE work_id = @WorkId ORDER BY container_number ASC, id ASC;",
            new { WorkId = workId })).ToList();

        var unitRows = (await conn.QueryAsync<UnitRow>(
            "SELECT * FROM media_units WHERE work_id = @WorkId ORDER BY container_id ASC, unit_number ASC, id ASC;",
            new { WorkId = workId })).ToList();

        var unitIds = unitRows.Select(u => u.Id).ToArray();
        var editionRows = unitIds.Length == 0
            ? []
            : (await conn.QueryAsync<EditionRow>(
                "SELECT * FROM media_editions WHERE unit_id IN @UnitIds ORDER BY id ASC;",
                new { UnitIds = unitIds })).ToList();

        var fileRows = (await conn.QueryAsync<FileRow>(
            "SELECT * FROM media_files WHERE work_id = @WorkId ORDER BY id ASC;",
            new { WorkId = workId })).ToList();

        var fileIds = fileRows.Select(f => f.Id).ToArray();
        List<VerificationJobRow> vjRows = [];
        if (fileIds.Length > 0 && unitIds.Length > 0)
        {
            vjRows = (await conn.QueryAsync<VerificationJobRow>(
                """
                SELECT * FROM verification_jobs
                WHERE (media_file_id IN @FileIds OR unit_id IN @UnitIds)
                ORDER BY id DESC;
                """,
                new { FileIds = fileIds, UnitIds = unitIds })).ToList();
        }
        else if (fileIds.Length > 0)
        {
            vjRows = (await conn.QueryAsync<VerificationJobRow>(
                """
                SELECT * FROM verification_jobs
                WHERE media_file_id IN @FileIds
                ORDER BY id DESC;
                """,
                new { FileIds = fileIds })).ToList();
        }
        else if (unitIds.Length > 0)
        {
            vjRows = (await conn.QueryAsync<VerificationJobRow>(
                """
                SELECT * FROM verification_jobs
                WHERE unit_id IN @UnitIds
                ORDER BY id DESC;
                """,
                new { UnitIds = unitIds })).ToList();
        }

        var verificationsByFileId = new Dictionary<long, MediaFileVerificationSummary>();
        var verificationsByUnitId = new Dictionary<long, MediaFileVerificationSummary>();
        foreach (var vj in vjRows)
        {
            var details = ExtractDetailsFromReportJson(vj.ReportJson);
            var summary = new MediaFileVerificationSummary(
                Verdict: vj.Verdict,
                OverallConfidence: vj.OverallConfidence,
                AudioMethod: details.AudioMethod,
                SubMethod: details.SubMethod,
                DetectedAudioLanguages: DeserializeStrings(vj.DetectedAudioLangsJson),
                DetectedSubLanguages: DeserializeStrings(vj.DetectedSubLangsJson),
                DurationMs: vj.DurationMs,
                VerifiedAt: vj.CreatedAt,
                FailureReason: details.FailureReason,
                FailureCode: details.FailureCode,
                MatchedPolicyTier: details.MatchedPolicyTier,
                MatchedPolicyTierLabel: details.MatchedPolicyTierLabel,
                SatisfiesLanguageCutoff: details.SatisfiesLanguageCutoff);

            if (vj.MediaFileId.HasValue && !verificationsByFileId.ContainsKey(vj.MediaFileId.Value))
            {
                verificationsByFileId[vj.MediaFileId.Value] = summary;
            }
            if (vj.UnitId.HasValue && !verificationsByUnitId.ContainsKey(vj.UnitId.Value))
            {
                verificationsByUnitId[vj.UnitId.Value] = summary;
            }
        }

        var editionsByUnit = editionRows.GroupBy(e => e.UnitId).ToDictionary(g => g.Key, g => g.Select(MapEdition).ToList());
        var filesByUnit = fileRows
            .Select(f =>
            {
                var summary = verificationsByFileId.GetValueOrDefault(f.Id)
                    ?? verificationsByUnitId.GetValueOrDefault(f.UnitId);
                return MapFile(f) with { LanguageVerification = summary };
            })
            .GroupBy(f => f.UnitId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<MediaFileSummary>)g.ToList());
        var unitsByContainer = unitRows
            .GroupBy(u => u.ContainerId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<MediaUnitDetail>)g.Select(u => MapUnit(
                    u,
                    editionsByUnit.GetValueOrDefault(u.Id) ?? [],
                    filesByUnit.GetValueOrDefault(u.Id) ?? [])).ToList());

        var containers = containerRows
            .Select(c => new MediaContainerDetail(
                Id: c.Id,
                WorkId: c.WorkId,
                ContainerKind: c.ContainerKind,
                ContainerNumber: c.ContainerNumber,
                Title: c.Title,
                ReleaseDate: c.ReleaseDate,
                Monitored: c.Monitored != 0,
                ExternalIds: DeserializeDict(c.ExternalIdsJson),
                PlexRatingKey: c.PlexRatingKey,
                Units: unitsByContainer.GetValueOrDefault(c.Id) ?? []))
            .ToList();

        return new MediaWorkDetailTree(MapWorkSummary(workRow), containers);
    }

    public async Task<MediaWorkDetailTree> UpdateWorkAsync(
        long workId,
        UpdateMediaWorkRequest request,
        CancellationToken ct = default)
    {
        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await readConn.QuerySingleOrDefaultAsync<WorkRow>(
            "SELECT * FROM media_works WHERE id = @Id;",
            new { Id = workId })
            ?? throw new KeyNotFoundException($"MediaWork #{workId} not found.");

        var title = string.IsNullOrWhiteSpace(request.Title) ? row.Title : request.Title.Trim();
        var overview = request.Overview ?? row.Overview;
        var poster = request.PosterUrl ?? row.PosterUrl;
        var monitored = request.Monitored ?? (row.Monitored != 0);
        var minAvailability = string.IsNullOrWhiteSpace(request.MinimumAvailability)
            ? MinimumAvailabilityPolicy.Normalize(row.MinimumAvailability)
            : MinimumAvailabilityPolicy.Normalize(request.MinimumAvailability);
        var qualityProfileId = request.ClearQualityProfile
            ? null
            : (request.QualityProfileId ?? row.QualityProfileId);
        var langPolicyId = request.ClearLanguagePolicy
            ? null
            : (request.LanguagePolicyId ?? row.LanguagePolicyId);

        var provider = string.IsNullOrWhiteSpace(request.PrimaryProvider) ? row.PrimaryProvider : request.PrimaryProvider.Trim().ToLowerInvariant();
        var ordering = string.IsNullOrWhiteSpace(request.EpisodeOrderingScheme) ? row.EpisodeOrderingScheme : request.EpisodeOrderingScheme.Trim().ToLowerInvariant();
        var locked = request.LockedFields is not null
            ? request.LockedFields.Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray()
            : DeserializeStrings(row.LockedFieldsJson);

        var origLang = row.OriginalLanguage;
        if (!string.IsNullOrWhiteSpace(request.OriginalLanguage))
        {
            origLang = NormalizeOriginalLanguage(request.OriginalLanguage);
            if (!locked.Contains("originalLanguage", StringComparer.OrdinalIgnoreCase))
            {
                locked = locked.Concat(["originalLanguage"]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        var tags = request.Tags is not null
            ? request.Tags.Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray()
            : DeserializeStrings(row.TagsJson);

        var now = DateTimeOffset.UtcNow.ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                if (langPolicyId.HasValue && request.LanguagePolicyId.HasValue)
                {
                    if (langPolicyId.Value <= 0)
                    {
                        throw new ArgumentException($"Language policy #{langPolicyId.Value} does not exist.");
                    }
                    var lpExists = await conn.ExecuteScalarAsync<int?>(
                        "SELECT 1 FROM language_policies WHERE id = @Id;",
                        new { Id = langPolicyId.Value },
                        transaction: tx);
                    if (!lpExists.HasValue)
                    {
                        throw new ArgumentException($"Language policy #{langPolicyId.Value} does not exist.");
                    }
                }

                if (qualityProfileId.HasValue && request.QualityProfileId.HasValue)
                {
                    if (qualityProfileId.Value <= 0)
                    {
                        throw new ArgumentException($"Quality profile #{qualityProfileId.Value} does not exist.");
                    }
                    var qpExists = await conn.ExecuteScalarAsync<int?>(
                        "SELECT 1 FROM quality_profiles WHERE id = @Id;",
                        new { Id = qualityProfileId.Value },
                        transaction: tx);
                    if (!qpExists.HasValue)
                    {
                        throw new ArgumentException($"Quality profile #{qualityProfileId.Value} does not exist.");
                    }
                }

                await conn.ExecuteAsync(
                    """
                    UPDATE media_works
                    SET title                   = @Title,
                        sort_title              = @SortTitle,
                        overview                = @Overview,
                        poster_url              = @Poster,
                        monitored               = @Monitored,
                        minimum_availability    = @MinimumAvailability,
                        quality_profile_id      = @QualityProfileId,
                        language_policy_id      = @LanguagePolicyId,
                        original_language       = @OriginalLanguage,
                        primary_provider        = @Provider,
                        episode_ordering_scheme = @Ordering,
                        locked_fields_json      = @LockedJson,
                        tags_json               = @TagsJson,
                        updated_at              = @Now
                    WHERE id = @Id;
                    """,
                    new
                    {
                        Id = workId,
                        Title = title,
                        SortTitle = ComputeSortTitle(title),
                        Overview = overview,
                        Poster = poster,
                        Monitored = monitored ? 1 : 0,
                        MinimumAvailability = minAvailability,
                        QualityProfileId = qualityProfileId,
                        LanguagePolicyId = langPolicyId,
                        OriginalLanguage = origLang,
                        Provider = provider,
                        Ordering = ordering,
                        LockedJson = JsonSerializer.Serialize(locked, JsonOpts),
                        TagsJson = JsonSerializer.Serialize(tags, JsonOpts),
                        Now = now,
                    },
                    transaction: tx);
            },
            ct);

        if (row.FtsDocId.HasValue)
        {
            var alts = DeserializeStrings(row.AltTitlesJson);
            var extIds = DeserializeDict(row.ExternalIdsJson);
            await ftsSearch.UpdateItemAsync(
                row.FtsDocId.Value,
                row.WorkKind,
                title,
                string.Join(" | ", alts),
                row.Year,
                FormatExternalIdsForFts(extIds),
                ct);
        }

        if (!string.IsNullOrWhiteSpace(request.MonitorPreset))
        {
            return await ApplyMonitorPresetAsync(workId, request.MonitorPreset, ct);
        }

        return (await GetWorkTreeAsync(workId, actor: null, ct))!;
    }

    public async Task<MediaWorkDetailTree> RefreshWorkMetadataAsync(
        long workId,
        string? switchProvider = null,
        string? episodeOrderingScheme = null,
        CancellationToken ct = default)
    {
        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await readConn.QuerySingleOrDefaultAsync<WorkRow>(
            "SELECT * FROM media_works WHERE id = @Id;",
            new { Id = workId })
            ?? throw new KeyNotFoundException($"MediaWork #{workId} not found.");

        var lib = await GetLibraryByIdAsync(row.LibraryId, ct)
            ?? throw new KeyNotFoundException($"Library #{row.LibraryId} not found.");

        var targetProvider = !string.IsNullOrWhiteSpace(switchProvider)
            ? switchProvider.Trim().ToLowerInvariant()
            : row.PrimaryProvider;
        var targetOrdering = !string.IsNullOrWhiteSpace(episodeOrderingScheme)
            ? episodeOrderingScheme.Trim().ToLowerInvariant()
            : row.EpisodeOrderingScheme;

        var currentExtIds = DeserializeDict(row.ExternalIdsJson);
        var resolved = await metadataFederation.ResolveWorkAsync(
            vertical: row.WorkKind,
            preferredProvider: targetProvider,
            knownExternalIds: currentExtIds,
            customWaterfall: lib.MetadataWaterfall,
            forceRefresh: true,
            ct: ct);

        if (resolved is null)
        {
            return await UpdateWorkAsync(
                workId,
                new UpdateMediaWorkRequest(PrimaryProvider: targetProvider, EpisodeOrderingScheme: targetOrdering),
                ct);
        }

        var lockedSet = DeserializeStrings(row.LockedFieldsJson).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var merged = metadataFederation.ApplyFieldMergeWithLocks(
            new MergedWorkMetadataFields(
                Title: row.Title,
                AltTitles: DeserializeStrings(row.AltTitlesJson),
                OriginalLanguage: row.OriginalLanguage,
                Year: row.Year,
                Status: row.Status,
                Overview: row.Overview,
                PosterUrl: row.PosterUrl,
                ExternalIds: currentExtIds),
            resolved,
            lockedSet);

        var currentTags = DeserializeStrings(row.TagsJson);
        var refreshedClassification = MediaTaxonomyClassifier.Classify(
            vertical: row.WorkKind,
            originalLanguage: merged.OriginalLanguage,
            genres: resolved.Genres,
            externalIds: merged.ExternalIds,
            title: merged.Title,
            altTitles: merged.AltTitles,
            formatHint: string.Equals(resolved.ClassificationKind, "anime-movie", StringComparison.OrdinalIgnoreCase) ? "MOVIE" : null,
            overview: merged.Overview,
            existingTags: currentTags,
            rootFolderPath: lib.RootFolderPath);

        var mergedTags = lockedSet.Contains("tags")
            ? currentTags
            : MediaTaxonomyClassifier.MergeWithUserTags(currentTags, refreshedClassification.SuggestedTags);

        var now = DateTimeOffset.UtcNow.ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE media_works
                    SET primary_provider        = @Provider,
                        episode_ordering_scheme = @Ordering,
                        title                   = @Title,
                        sort_title              = @SortTitle,
                        alt_titles_json         = @AltJson,
                        original_language       = @OrigLang,
                        year                    = @Year,
                        status                  = @Status,
                        overview                = @Overview,
                        poster_url              = @Poster,
                        external_ids_json       = @ExtIdsJson,
                        tags_json               = @TagsJson,
                        updated_at              = @Now
                    WHERE id = @Id;
                    """,
                    new
                    {
                        Id = workId,
                        Provider = targetProvider,
                        Ordering = targetOrdering,
                        merged.Title,
                        SortTitle = ComputeSortTitle(merged.Title),
                        AltJson = JsonSerializer.Serialize(merged.AltTitles, JsonOpts),
                        OrigLang = merged.OriginalLanguage,
                        merged.Year,
                        merged.Status,
                        merged.Overview,
                        Poster = merged.PosterUrl,
                        ExtIdsJson = JsonSerializer.Serialize(merged.ExternalIds, JsonOpts),
                        TagsJson = JsonSerializer.Serialize(mergedTags, JsonOpts),
                        Now = now,
                    },
                    transaction: tx);

                if (row.WorkKind is MetadataVerticals.Tv or MetadataVerticals.Anime &&
                    resolved.Containers is { Count: > 0 } rcList &&
                    !(rcList.Count == 1 && rcList[0].Units.Count == 1 && rcList[0].Units[0].Title == "Episode 1"))
                {
                    var existingContainers = (await conn.QueryAsync<(long Id, int ContainerNumber, int Monitored)>(
                        "SELECT id AS Id, container_number AS ContainerNumber, monitored AS Monitored FROM media_containers WHERE work_id = @WorkId;",
                        new { WorkId = workId },
                        transaction: tx)).ToDictionary(x => x.ContainerNumber);

                    var sampleUnit = await conn.QueryFirstOrDefaultAsync<(long? ProfileOverrideId, string AudioJson, string SubJson)>(
                        "SELECT profile_override_id AS ProfileOverrideId, required_audio_langs_json AS AudioJson, required_sub_langs_json AS SubJson FROM media_units WHERE work_id = @WorkId LIMIT 1;",
                        new { WorkId = workId },
                        transaction: tx);

                    var defaultAudioJson = string.IsNullOrWhiteSpace(sampleUnit.AudioJson) ? "[]" : sampleUnit.AudioJson;
                    var defaultSubJson = string.IsNullOrWhiteSpace(sampleUnit.SubJson) ? "[]" : sampleUnit.SubJson;

                    foreach (var rc in rcList)
                    {
                        long containerId;
                        int containerMonitored;
                        if (existingContainers.TryGetValue(rc.ContainerNumber, out var existingC))
                        {
                            containerId = existingC.Id;
                            containerMonitored = existingC.Monitored;
                        }
                        else
                        {
                            containerMonitored = row.Monitored != 0 && rc.ContainerNumber > 0 ? 1 : 0;
                            containerId = await conn.ExecuteScalarAsync<long>(
                                """
                                INSERT INTO media_containers (
                                    work_id, container_kind, container_number, title,
                                    release_date, monitored, external_ids_json, created_at
                                ) VALUES (
                                    @WorkId, @Kind, @Num, @Title,
                                    @ReleaseDate, @Monitored, @ExtIdsJson, @Now
                                );
                                SELECT last_insert_rowid();
                                """,
                                new
                                {
                                    WorkId = workId,
                                    Kind = rc.ContainerKind,
                                    Num = rc.ContainerNumber,
                                    rc.Title,
                                    rc.ReleaseDate,
                                    Monitored = containerMonitored,
                                    ExtIdsJson = JsonSerializer.Serialize(rc.ExternalIds ?? new Dictionary<string, string>(), JsonOpts),
                                    Now = now,
                                },
                                transaction: tx);
                        }

                        var existingUnitNums = (await conn.QueryAsync<int>(
                            "SELECT unit_number FROM media_units WHERE container_id = @Cid;",
                            new { Cid = containerId },
                            transaction: tx)).ToHashSet();

                        foreach (var ru in rc.Units)
                        {
                            if (existingUnitNums.Contains(ru.UnitNumber))
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
                                    @WorkId, @ContainerId, @Kind, @Num, @AbsNum,
                                    @Title, @AirDate, @Monitored, @ProfileOverrideId,
                                    @AudioJson, @SubJson,
                                    0, 0, @Now
                                );
                                """,
                                new
                                {
                                    WorkId = workId,
                                    ContainerId = containerId,
                                    Kind = ru.UnitKind,
                                    Num = ru.UnitNumber,
                                    AbsNum = ru.AbsoluteNumber,
                                    ru.Title,
                                    AirDate = ru.AirOrReleaseDate ?? rc.ReleaseDate,
                                    Monitored = containerMonitored,
                                    sampleUnit.ProfileOverrideId,
                                    AudioJson = defaultAudioJson,
                                    SubJson = defaultSubJson,
                                    Now = now,
                                },
                                transaction: tx);
                        }
                    }
                }
            },
            ct);

        if (row.FtsDocId.HasValue)
        {
            await ftsSearch.UpdateItemAsync(
                row.FtsDocId.Value,
                row.WorkKind,
                merged.Title,
                string.Join(" | ", merged.AltTitles),
                merged.Year,
                FormatExternalIdsForFts(merged.ExternalIds),
                ct);
        }

        return (await GetWorkTreeAsync(workId, actor: null, ct))!;
    }

    public Task DeleteWorkAsync(long workId, CancellationToken ct = default) =>
        DeleteWorkWithOptionsAsync(workId, deleteFilesOnDisk: false, ct);

    public async Task DeleteWorkWithOptionsAsync(long workId, bool deleteFilesOnDisk = false, CancellationToken ct = default)
    {
        var tree = deleteFilesOnDisk ? await GetWorkTreeAsync(workId, actor: null, ct) : null;
        LibrarySummary? lib = null;
        if (tree is not null)
        {
            lib = await GetLibraryByIdAsync(tree.Work.LibraryId, ct);
        }

        if (deleteFilesOnDisk && tree is not null && lib is not null && !string.IsNullOrWhiteSpace(lib.RootFolderPath))
        {
            var rootFull = Path.GetFullPath(lib.RootFolderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidateDirs = new HashSet<string>(StringComparer.Ordinal);
            var nowIso = DateTimeOffset.UtcNow.ToString("O");

            foreach (var container in tree.Containers)
            {
                foreach (var unit in container.Units)
                {
                    foreach (var mf in unit.Files)
                    {
                        var fullFile = Path.GetFullPath(Path.Combine(lib.RootFolderPath, mf.RelativePath));
                        if (!fullFile.StartsWith(rootFull, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        try
                        {
                            if (File.Exists(fullFile))
                            {
                                File.Delete(fullFile);
                            }

                            var dir = Path.GetDirectoryName(fullFile);
                            if (!string.IsNullOrWhiteSpace(dir))
                            {
                                candidateDirs.Add(dir);
                                var parentDir = Path.GetDirectoryName(dir);
                                if (!string.IsNullOrWhiteSpace(parentDir))
                                {
                                    candidateDirs.Add(parentDir);
                                }
                            }

                            await dbFactory.ExecuteWriteAsync(
                                DatabaseKind.Core,
                                (conn, tx) => conn.ExecuteAsync(
                                    """
                                    INSERT INTO media_history (
                                        occurred_at, event_type, media_vertical, work_id, work_title,
                                        container_id, unit_id, source_title, quality_name, destination_path, data_json
                                    ) VALUES (
                                        @Now, 'MediaFileDeleted', @Vertical, @WorkId, @WorkTitle,
                                        @ContainerId, @UnitId, @SourceTitle, @Quality, @DestPath, '{"reason":"DeleteWorkWithFiles"}'
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
                                        DestPath = fullFile,
                                    },
                                    transaction: tx),
                                ct);
                        }
                        catch
                        {
                            // Best-effort disk deletion
                        }
                    }
                }
            }

            foreach (var dir in candidateDirs.OrderByDescending(d => d.Length))
            {
                var normDir = Path.GetFullPath(dir);
                if (normDir.StartsWith(rootFull, StringComparison.Ordinal) &&
                    Directory.Exists(normDir) &&
                    !Directory.EnumerateFileSystemEntries(normDir).Any())
                {
                    try { Directory.Delete(normDir, recursive: false); } catch { }
                }
            }
        }

        var deletedSummary = tree?.Work;
        if (deletedSummary is null)
        {
            var existingTree = await GetWorkTreeAsync(workId, actor: null, ct);
            deletedSummary = existingTree?.Work;
        }

        long? ftsDocId;
        await using (var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            ftsDocId = await readConn.ExecuteScalarAsync<long?>(
                "SELECT fts_doc_id FROM media_works WHERE id = @Id;",
                new { Id = workId });
        }

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync("DELETE FROM media_works WHERE id = @Id;", new { Id = workId }, transaction: tx),
            ct);

        if (ftsDocId.HasValue)
        {
            await ftsSearch.DeleteItemAsync(ftsDocId.Value, ct);
        }

        if (notificationSink is not null && deletedSummary is not null)
        {
            await notificationSink.PublishAsync(
                new Managerr.Common.Notifications.MediaLifecycleNotificationEvent(
                    EventType: Managerr.Common.Notifications.MediaNotificationEventTypes.OnDelete,
                    Title: $"Deleted — {deletedSummary.Title}",
                    Message: deleteFilesOnDisk
                        ? $"Deleted '{deletedSummary.Title}' and removed media files from disk."
                        : $"Removed '{deletedSummary.Title}' from catalog (files kept on disk).",
                    WorkId: deletedSummary.Id,
                    WorkTitle: deletedSummary.Title,
                    MediaVertical: deletedSummary.WorkKind,
                    WorkTags: deletedSummary.Tags),
                ct);
        }
    }


    public async Task<MediaWorkDetailTree> UpdateContainerAsync(
        long containerId,
        UpdateMediaContainerRequest request,
        CancellationToken ct = default)
    {
        ContainerRow current;
        await using (var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            current = await readConn.QuerySingleOrDefaultAsync<ContainerRow>(
                "SELECT * FROM media_containers WHERE id = @Id;",
                new { Id = containerId })
                ?? throw new KeyNotFoundException($"MediaContainer #{containerId} not found.");
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    "UPDATE media_containers SET monitored = @Monitored WHERE id = @Id;",
                    new { Id = containerId, Monitored = request.Monitored ? 1 : 0 },
                    transaction: tx);

                if (request.CascadeUnits)
                {
                    await conn.ExecuteAsync(
                        "UPDATE media_units SET monitored = @Monitored WHERE container_id = @ContainerId;",
                        new { ContainerId = containerId, Monitored = request.Monitored ? 1 : 0 },
                        transaction: tx);
                }

                await conn.ExecuteAsync(
                    "UPDATE media_works SET updated_at = @Now WHERE id = @WorkId;",
                    new { WorkId = current.WorkId, Now = now },
                    transaction: tx);
            },
            ct);

        return (await GetWorkTreeAsync(current.WorkId, actor: null, ct))!;
    }

    public async Task<MediaUnitDetail> UpdateUnitAsync(
        long unitId,
        UpdateMediaUnitRequest request,
        CancellationToken ct = default)
    {
        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var current = await readConn.QuerySingleOrDefaultAsync<UnitRow>(
            "SELECT * FROM media_units WHERE id = @Id;",
            new { Id = unitId })
            ?? throw new KeyNotFoundException($"MediaUnit #{unitId} not found.");

        var monitored = request.Monitored ?? (current.Monitored != 0);
        var profileOverride = request.ClearProfileOverride ? null : (request.ProfileOverrideId ?? current.ProfileOverrideId);
        var reqAudio = request.RequiredAudioLangs is not null
            ? request.RequiredAudioLangs.Select(x => x.Trim().ToLowerInvariant()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray()
            : DeserializeStrings(current.RequiredAudioLangsJson);
        var reqSubs = request.RequiredSubLangs is not null
            ? request.RequiredSubLangs.Select(x => x.Trim().ToLowerInvariant()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray()
            : DeserializeStrings(current.RequiredSubLangsJson);
        var qCutoff = request.QualityCutoffMet ?? (current.QualityCutoffMet != 0);
        var lCutoff = request.LanguageCutoffMet ?? (current.LanguageCutoffMet != 0);

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                UPDATE media_units
                SET monitored                 = @Monitored,
                    profile_override_id       = @ProfileOverride,
                    required_audio_langs_json = @AudioJson,
                    required_sub_langs_json   = @SubJson,
                    quality_cutoff_met        = @QCutoff,
                    language_cutoff_met       = @LCutoff,
                    plex_rating_key           = COALESCE(@PlexKey, plex_rating_key),
                    watch_count               = COALESCE(@WatchCount, watch_count),
                    last_watched_at           = COALESCE(@LastWatched, last_watched_at)
                WHERE id = @Id;
                """,
                new
                {
                    Id = unitId,
                    Monitored = monitored ? 1 : 0,
                    ProfileOverride = profileOverride,
                    AudioJson = JsonSerializer.Serialize(reqAudio, JsonOpts),
                    SubJson = JsonSerializer.Serialize(reqSubs, JsonOpts),
                    QCutoff = qCutoff ? 1 : 0,
                    LCutoff = lCutoff ? 1 : 0,
                    PlexKey = request.PlexRatingKey,
                    request.WatchCount,
                    LastWatched = request.LastWatchedAt,
                },
                transaction: tx),
            ct);

        await using var afterConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var updated = await afterConn.QuerySingleAsync<UnitRow>("SELECT * FROM media_units WHERE id = @Id;", new { Id = unitId });
        var editions = (await afterConn.QueryAsync<EditionRow>("SELECT * FROM media_editions WHERE unit_id = @Id;", new { Id = unitId }))
            .Select(MapEdition).ToList();
        var files = (await afterConn.QueryAsync<FileRow>("SELECT * FROM media_files WHERE unit_id = @Id;", new { Id = unitId }))
            .Select(MapFile).ToList();

        return MapUnit(updated, editions, files);
    }

    public async Task<MediaFileSummary> AttachFileAsync(AttachMediaFileRequest request, CancellationToken ct = default)
    {
        var relTrimmed = request.RelativePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(relTrimmed) ||
            relTrimmed.Contains('\0') ||
            Path.IsPathRooted(relTrimmed) ||
            relTrimmed.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Contains(".."))
        {
            throw new ArgumentException($"Invalid or unsafe relative file path '{request.RelativePath}'.");
        }

        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var unit = await readConn.QuerySingleOrDefaultAsync<UnitRow>(
            "SELECT * FROM media_units WHERE id = @Id;",
            new { Id = request.UnitId })
            ?? throw new KeyNotFoundException($"MediaUnit #{request.UnitId} not found.");

        var now = DateTimeOffset.UtcNow.ToString("O");
        var audioLangs = (request.DetectedAudioLangs ?? []).Select(x => x.Trim().ToLowerInvariant()).Distinct().ToArray();
        var subLangs = (request.DetectedSubLangs ?? []).Select(x => x.Trim().ToLowerInvariant()).Distinct().ToArray();

        var fileId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                long? editionId = null;
                if (!string.IsNullOrWhiteSpace(request.EditionName))
                {
                    editionId = await conn.ExecuteScalarAsync<long>(
                        """
                        INSERT INTO media_editions (unit_id, edition_name, is_preferred, created_at)
                        VALUES (@UnitId, @Name, 1, @Now);
                        SELECT last_insert_rowid();
                        """,
                        new { request.UnitId, Name = request.EditionName.Trim(), Now = now },
                        transaction: tx);
                }

                return await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO media_files (
                        work_id, unit_id, edition_id, relative_path, size_bytes,
                        quality_name, resolution, video_codec, hdr_format, audio_codec,
                        detected_audio_langs_json, detected_sub_langs_json,
                        verified_status, inode_hardlinked, imported_at,
                        source_quality_name, source_video_codec, source_size_bytes
                    ) VALUES (
                        @WorkId, @UnitId, @EditionId, @RelPath, @SizeBytes,
                        @Quality, @Resolution, @VideoCodec, @Hdr, @AudioCodec,
                        @AudioJson, @SubJson,
                        @Verified, @Hardlinked, @Now,
                        @SourceQuality, @SourceCodec, @SourceSizeBytes
                    );
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        unit.WorkId,
                        request.UnitId,
                        EditionId = editionId,
                        RelPath = relTrimmed,
                        request.SizeBytes,
                        Quality = Managerr.Common.Media.QualityNames.Canonicalize(request.QualityName),
                        request.Resolution,
                        request.VideoCodec,
                        Hdr = request.HdrFormat,
                        request.AudioCodec,
                        AudioJson = JsonSerializer.Serialize(audioLangs, JsonOpts),
                        SubJson = JsonSerializer.Serialize(subLangs, JsonOpts),
                        Verified = request.VerifiedStatus,
                        Hardlinked = request.InodeHardlinked ? 1 : 0,
                        Now = now,
                        SourceQuality = Managerr.Common.Media.QualityNames.Canonicalize(request.SourceQualityName ?? request.QualityName),
                        SourceCodec = request.SourceVideoCodec ?? request.VideoCodec,
                        SourceSizeBytes = request.SourceSizeBytes ?? request.SizeBytes,
                    },
                    transaction: tx);
            },
            ct);

        await using var afterConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var fileRow = await afterConn.QuerySingleAsync<FileRow>("SELECT * FROM media_files WHERE id = @Id;", new { Id = fileId });
        return MapFile(fileRow);
    }

    public async Task<MediaFileSummary?> GetMediaFileByIdAsync(long mediaFileId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await conn.QuerySingleOrDefaultAsync<FileRow>(
            "SELECT * FROM media_files WHERE id = @Id;",
            new { Id = mediaFileId });
        return row is null ? null : MapFile(row);
    }

    private sealed record ReportJsonExtraction(
        string? AudioMethod,
        string? SubMethod,
        string? FailureReason,
        string? FailureCode,
        int? MatchedPolicyTier,
        string? MatchedPolicyTierLabel,
        bool? SatisfiesLanguageCutoff);

    private static ReportJsonExtraction ExtractDetailsFromReportJson(string? reportJson)
    {
        if (string.IsNullOrWhiteSpace(reportJson) || reportJson == "{}")
        {
            return new ReportJsonExtraction(null, null, null, null, null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(reportJson);
            var root = doc.RootElement;
            JsonElement streamsElement = default;
            if (root.TryGetProperty("streams", out var s) || root.TryGetProperty("Streams", out s))
            {
                streamsElement = s;
            }

            string? audioMethod = null;
            string? subMethod = null;

            if (streamsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streamsElement.EnumerateArray())
                {
                    var streamType = stream.TryGetProperty("streamType", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() :
                                     stream.TryGetProperty("StreamType", out st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
                    var method = stream.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() :
                                 stream.TryGetProperty("Method", out m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

                    if (string.Equals(streamType, "audio", StringComparison.OrdinalIgnoreCase) && audioMethod is null)
                    {
                        audioMethod = method;
                    }
                    else if (string.Equals(streamType, "subtitle", StringComparison.OrdinalIgnoreCase) && subMethod is null)
                    {
                        subMethod = method;
                    }
                }
            }

            string? failureReason = root.TryGetProperty("failureReason", out var fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() :
                                    root.TryGetProperty("FailureReason", out fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() : null;

            string? failureCode = root.TryGetProperty("failureCode", out var fc) && fc.ValueKind == JsonValueKind.String ? fc.GetString() :
                                  root.TryGetProperty("FailureCode", out fc) && fc.ValueKind == JsonValueKind.String ? fc.GetString() : null;

            int? matchedPolicyTier = root.TryGetProperty("matchedPolicyTier", out var mpt) && mpt.ValueKind == JsonValueKind.Number && mpt.TryGetInt32(out var tVal) ? tVal :
                                     root.TryGetProperty("MatchedPolicyTier", out mpt) && mpt.ValueKind == JsonValueKind.Number && mpt.TryGetInt32(out tVal) ? tVal : null;

            string? matchedPolicyTierLabel = root.TryGetProperty("matchedPolicyTierLabel", out var mptl) && mptl.ValueKind == JsonValueKind.String ? mptl.GetString() :
                                             root.TryGetProperty("MatchedPolicyTierLabel", out mptl) && mptl.ValueKind == JsonValueKind.String ? mptl.GetString() : null;

            bool? satisfiesLanguageCutoff = root.TryGetProperty("satisfiesLanguageCutoff", out var slc) && slc.ValueKind is JsonValueKind.True or JsonValueKind.False ? slc.GetBoolean() :
                                            root.TryGetProperty("SatisfiesLanguageCutoff", out slc) && slc.ValueKind is JsonValueKind.True or JsonValueKind.False ? slc.GetBoolean() : null;

            return new ReportJsonExtraction(audioMethod, subMethod, failureReason, failureCode, matchedPolicyTier, matchedPolicyTierLabel, satisfiesLanguageCutoff);
        }
        catch
        {
            return new ReportJsonExtraction(null, null, null, null, null, null, null);
        }
    }

    private sealed class VerificationJobRow
    {
        public long Id { get; set; }
        public long? UnitId { get; set; }
        public long? MediaFileId { get; set; }
        public string StagedFilePath { get; set; } = "";
        public string MediaVertical { get; set; } = "";
        public string Verdict { get; set; } = "";
        public double OverallConfidence { get; set; }
        public int HeaderAutoPatched { get; set; }
        public string DetectedAudioLangsJson { get; set; } = "[]";
        public string DetectedSubLangsJson { get; set; } = "[]";
        public string ReportJson { get; set; } = "{}";
        public long DurationMs { get; set; }
        public string CreatedAt { get; set; } = "";
    }

}
