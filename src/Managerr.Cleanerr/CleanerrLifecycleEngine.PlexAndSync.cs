using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Managerr.Catalog;
using Managerr.Datastore;
using Managerr.Profiles;

namespace Managerr.Cleanerr;

public sealed partial class CleanerrLifecycleEngine
{
    private async Task<Dictionary<long, (long ReqId, string Username, string CreatedAt)>> LoadActiveRequestsByWorkIdAsync(CancellationToken ct)
    {
        var requestsByWorkId = new Dictionary<long, (long ReqId, string Username, string CreatedAt)>();
        await using var reqConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var reqRows = await reqConn.QueryAsync<NativeRequestJoinRow>(
            """
            SELECT r.id AS Id, r.work_id AS WorkId, u.username AS Username, r.created_at AS CreatedAt
            FROM media_requests r
            JOIN users u ON u.id = r.requester_user_id
            WHERE r.work_id IS NOT NULL AND r.status != 'cleaned_reset'
            ORDER BY r.id DESC;
            """);
        foreach (var r in reqRows)
        {
            if (r.WorkId.HasValue && !requestsByWorkId.ContainsKey(r.WorkId.Value))
            {
                requestsByWorkId[r.WorkId.Value] = (r.Id, r.Username ?? "user", r.CreatedAt);
            }
        }
        return requestsByWorkId;
    }

    private async Task PruneLegacySeasonAndEpisodeRowsAsync(CancellationToken ct)
    {
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE cleanerr_exclusion_rules
                    SET media_item_id = (
                            SELECT s.id
                            FROM cleanerr_media_items child
                            JOIN cleanerr_media_items s ON s.media_type = 'show'
                              AND (
                                (child.grandparent_rating_key IS NOT NULL AND s.plex_rating_key = child.grandparent_rating_key)
                                OR (child.parent_rating_key IS NOT NULL AND s.plex_rating_key = child.parent_rating_key)
                                OR (child.work_id IS NOT NULL AND s.work_id = child.work_id)
                              )
                            WHERE child.id = cleanerr_exclusion_rules.media_item_id
                            ORDER BY CASE
                                WHEN child.grandparent_rating_key IS NOT NULL AND s.plex_rating_key = child.grandparent_rating_key THEN 0
                                WHEN child.parent_rating_key IS NOT NULL AND s.plex_rating_key = child.parent_rating_key THEN 1
                                ELSE 2
                            END
                            LIMIT 1
                        ),
                        external_id = COALESCE((
                            SELECT s.plex_rating_key
                            FROM cleanerr_media_items child
                            JOIN cleanerr_media_items s ON s.media_type = 'show'
                              AND (
                                (child.grandparent_rating_key IS NOT NULL AND s.plex_rating_key = child.grandparent_rating_key)
                                OR (child.parent_rating_key IS NOT NULL AND s.plex_rating_key = child.parent_rating_key)
                                OR (child.work_id IS NOT NULL AND s.work_id = child.work_id)
                              )
                            WHERE child.id = cleanerr_exclusion_rules.media_item_id
                            ORDER BY CASE
                                WHEN child.grandparent_rating_key IS NOT NULL AND s.plex_rating_key = child.grandparent_rating_key THEN 0
                                WHEN child.parent_rating_key IS NOT NULL AND s.plex_rating_key = child.parent_rating_key THEN 1
                                ELSE 2
                            END
                            LIMIT 1
                        ), external_id),
                        value = COALESCE((
                            SELECT s.title
                            FROM cleanerr_media_items child
                            JOIN cleanerr_media_items s ON s.media_type = 'show'
                              AND (
                                (child.grandparent_rating_key IS NOT NULL AND s.plex_rating_key = child.grandparent_rating_key)
                                OR (child.parent_rating_key IS NOT NULL AND s.plex_rating_key = child.parent_rating_key)
                                OR (child.work_id IS NOT NULL AND s.work_id = child.work_id)
                              )
                            WHERE child.id = cleanerr_exclusion_rules.media_item_id
                            ORDER BY CASE
                                WHEN child.grandparent_rating_key IS NOT NULL AND s.plex_rating_key = child.grandparent_rating_key THEN 0
                                WHEN child.parent_rating_key IS NOT NULL AND s.plex_rating_key = child.parent_rating_key THEN 1
                                ELSE 2
                            END
                            LIMIT 1
                        ), value),
                        granularity = 'series'
                    WHERE media_item_id IN (
                        SELECT id FROM cleanerr_media_items WHERE media_type IN ('season', 'episode')
                    )
                      AND EXISTS (
                        SELECT 1
                        FROM cleanerr_media_items child
                        JOIN cleanerr_media_items s ON s.media_type = 'show'
                          AND (
                            (child.grandparent_rating_key IS NOT NULL AND s.plex_rating_key = child.grandparent_rating_key)
                            OR (child.parent_rating_key IS NOT NULL AND s.plex_rating_key = child.parent_rating_key)
                            OR (child.work_id IS NOT NULL AND s.work_id = child.work_id)
                          )
                        WHERE child.id = cleanerr_exclusion_rules.media_item_id
                      );
                    """,
                    transaction: tx);

                await conn.ExecuteAsync(
                    "DELETE FROM cleanerr_media_items WHERE media_type IN ('season', 'episode');",
                    transaction: tx);
            },
            ct);
    }

    public async Task SyncCatalogItemsIntoCleanerrAsync(CancellationToken ct = default)
    {
        var generalSettings = await GetGeneralSettingsAsync(ct);
        var libraries = (await catalogService.ListLibrariesAsync(null, ct)).ToDictionary(l => l.Id);
        var works = await catalogService.ListWorksAsync(null, null, null, ct);
        var torrentFileCache = BuildTorrentDownloadFileCache(generalSettings.TorrentDownloadsPath);
        var requestsByWorkId = await LoadActiveRequestsByWorkIdAsync(ct);

        foreach (var w in works)
        {
            var tree = await catalogService.GetWorkTreeAsync(w.Id, null, ct);
            if (tree is null) continue;

            var rk = !string.IsNullOrWhiteSpace(w.PlexRatingKey) ? w.PlexRatingKey! : $"mgr-work-{w.Id}";
            var allUnits = tree.Containers.SelectMany(c => c.Units).ToList();
            var allFiles = allUnits.SelectMany(u => u.Files).ToList();
            var firstUnit = allUnits.FirstOrDefault();
            var firstFile = allFiles.FirstOrDefault();
            libraries.TryGetValue(w.LibraryId, out var lib);

            string? resolvedPath = null;
            if (firstFile is not null && lib is not null)
            {
                resolvedPath = Path.GetFullPath(Path.Combine(lib.RootFolderPath, firstFile.RelativePath));
            }

            var totalBytes = allFiles.Sum(f => f.SizeBytes);
            var audioLangs = allFiles.SelectMany(f => f.DetectedAudioLangs).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var isHardlinked = allFiles.Any(f =>
                f.InodeHardlinked ||
                (lib is not null && !string.IsNullOrWhiteSpace(f.RelativePath) &&
                 IsFileHardlinkedOnDisk(Path.GetFullPath(Path.Combine(lib.RootFolderPath, f.RelativePath)), torrentFileCache, generalSettings.TorrentDownloadsPath)));
            var cfScore = await ComputeCustomFormatScoreAsync(w, lib, firstUnit, firstFile, ct);
            var cleanType = w.WorkKind switch
            {
                "movie" or "movies" => "movie",
                "series" or "tv" or "anime" => "show",
                "artist" or "music" => "music",
                _ => "ebook",
            };
            var classification = InferClassificationFromTagsAndType(w.Tags, cleanType);
            var nativeWatchCount = allUnits.Sum(u => u.WatchCount);
            var nativeLastWatched = allUnits
                .Select(u => u.LastWatchedAt)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .OrderByDescending(s => s, StringComparer.Ordinal)
                .FirstOrDefault();

            requestsByWorkId.TryGetValue(w.Id, out var reqInfo);

            MediaItemRow? existingRow;
            await using (var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
            {
                existingRow = await conn.QueryFirstOrDefaultAsync<MediaItemRow>(
                    """
                    SELECT *
                    FROM cleanerr_media_items
                    WHERE plex_rating_key = @Rk
                       OR (work_id = @WorkId AND media_type = @MediaType)
                    LIMIT 1;
                    """,
                    new { Rk = rk, WorkId = w.Id, MediaType = cleanType });
            }

            if (existingRow is not null)
            {
                var existingTags = JsonSerializer.Deserialize<string[]>(existingRow.TagsJson, JsonOpts) ?? [];
                var mergedTags = MergeTagLists(existingTags, w.Tags);
                await dbFactory.ExecuteWriteAsync(
                    DatabaseKind.Core,
                    (wConn, tx) => wConn.ExecuteAsync(
                        """
                        UPDATE cleanerr_media_items
                        SET tags_json = @TagsJson,
                            classification_kind = COALESCE(@Classification, classification_kind),
                            work_status = COALESCE(@WorkStatus, work_status),
                            poster_url = COALESCE(@PosterUrl, poster_url),
                            video_codec = COALESCE(@VideoCodec, video_codec),
                            file_path = COALESCE(file_path, @FilePath),
                            size_bytes = CASE WHEN @SizeBytes > 0 THEN @SizeBytes ELSE size_bytes END,
                            view_count = CASE WHEN @WatchCount > view_count THEN @WatchCount ELSE view_count END,
                            last_viewed_at = CASE
                                WHEN @LastWatched IS NOT NULL AND (last_viewed_at IS NULL OR last_viewed_at = '' OR @LastWatched > last_viewed_at)
                                THEN @LastWatched
                                ELSE last_viewed_at
                            END,
                            seerr_id = COALESCE(seerr_id, @ReqId),
                            seerr_requested_by = COALESCE(seerr_requested_by, @ReqUser),
                            seerr_requested_at = COALESCE(seerr_requested_at, @ReqAt),
                            audio_languages_json = CASE WHEN @AudioLangsJson = '[]' THEN audio_languages_json ELSE @AudioLangsJson END,
                            is_hardlinked = CASE WHEN @IsHardlinked = 1 THEN 1 ELSE is_hardlinked END,
                            custom_format_score = CASE WHEN @CustomFormatScore != 0 THEN @CustomFormatScore ELSE custom_format_score END
                        WHERE id = @Id;
                        """,
                        new
                        {
                            existingRow.Id,
                            TagsJson = JsonSerializer.Serialize(mergedTags, JsonOpts),
                            Classification = classification,
                            WorkStatus = w.Status,
                            w.PosterUrl,
                            firstFile?.VideoCodec,
                            FilePath = resolvedPath,
                            SizeBytes = totalBytes,
                            WatchCount = nativeWatchCount,
                            LastWatched = nativeLastWatched,
                            ReqId = reqInfo.ReqId > 0 ? (long?)reqInfo.ReqId : null,
                            ReqUser = reqInfo.Username,
                            ReqAt = reqInfo.CreatedAt,
                            AudioLangsJson = JsonSerializer.Serialize(audioLangs, JsonOpts),
                            IsHardlinked = isHardlinked ? 1 : 0,
                            CustomFormatScore = cfScore,
                        },
                        transaction: tx),
                    ct);
            }
            else
            {
                await UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                    PlexRatingKey: rk,
                    MediaType: cleanType,
                    Title: w.Title,
                    LibraryId: w.LibraryId,
                    WorkId: w.Id,
                    UnitId: cleanType == "movie" ? firstUnit?.Id : null,
                    Year: w.Year,
                    SizeBytes: totalBytes,
                    Resolution: firstFile?.Resolution ?? "1080p",
                    Tags: w.Tags,
                    Monitored: w.Monitored,
                    AddedAt: w.AddedAt,
                    LastViewedAt: nativeLastWatched,
                    ViewCount: nativeWatchCount,
                    SeerrId: reqInfo.ReqId > 0 ? reqInfo.ReqId : null,
                    SeerrRequestedBy: reqInfo.Username,
                    SeerrRequestedAt: reqInfo.CreatedAt,
                    RequesterWatched: reqInfo.ReqId > 0 && nativeWatchCount > 0,
                    FilePath: resolvedPath,
                    VideoCodec: firstFile?.VideoCodec,
                    AudioLanguages: audioLangs,
                    IsHardlinked: isHardlinked,
                    CustomFormatScore: cfScore,
                    ClassificationKind: classification,
                    WorkStatus: w.Status,
                    PosterUrl: w.PosterUrl), ct);
            }
        }

        await PruneLegacySeasonAndEpisodeRowsAsync(ct);
    }

    public async Task<CleanerrPlexSyncResult> SyncFromPlexServerAsync(
        SyncCleanerrPlexRequest? request = null,
        CancellationToken ct = default)
    {
        var serverUrl = (request?.PlexServerUrl
                         ?? (appSettings is not null ? await appSettings.GetAsync("plex.bound_server_url", ct) : null)
                         ?? Environment.GetEnvironmentVariable("MANAGERR_PLEX_URL"))?.Trim().TrimEnd('/');

        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw new InvalidOperationException("No Plex Media Server URL is configured (plex.bound_server_url or MANAGERR_PLEX_URL).");
        }

        var token = (request?.PlexToken
                     ?? (appSettings is not null ? await appSettings.GetSecretAsync("plex.owner_admin_token", ct) : null)
                     ?? Environment.GetEnvironmentVariable("MANAGERR_PLEX_TOKEN"))?.Trim();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/json",
        };
        if (!string.IsNullOrWhiteSpace(token))
        {
            headers["X-Plex-Token"] = token;
        }

        // 1. Inspect active playback sessions (/status/sessions) so active-stream guard reflects live streams
        var activeStreamsByRk = new Dictionary<string, int>(StringComparer.Ordinal);
        var latestSessionsByMediaRk = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var activeStreamsTotal = 0;
        var sessionsSucceeded = false;
        try
        {
            var (sessStatus, sessBody) = await httpGateway.SendJsonAsync(HttpMethod.Get, $"{serverUrl}/status/sessions", null, headers, ct);
            if (sessStatus is >= 200 and < 300)
            {
                if (!string.IsNullOrWhiteSpace(sessBody))
                {
                    using var sessDoc = JsonDocument.Parse(sessBody);
                    if (sessDoc.RootElement.TryGetProperty("MediaContainer", out var smc) &&
                        smc.TryGetProperty("Metadata", out var smeta) &&
                        smeta.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var sItem in smeta.EnumerateArray())
                        {
                            activeStreamsTotal++;
                            var ratingKey = GetJsonString(sItem, "ratingKey");
                            var grandparentRk = GetJsonString(sItem, "grandparentRatingKey");
                            var parentRk = GetJsonString(sItem, "parentRatingKey");
                            var mediaRk = !string.IsNullOrWhiteSpace(grandparentRk)
                                ? grandparentRk
                                : !string.IsNullOrWhiteSpace(parentRk)
                                    ? parentRk
                                    : ratingKey;
                            if (string.IsNullOrWhiteSpace(mediaRk)) continue;

                            var sessionKey = !string.IsNullOrWhiteSpace(ratingKey) ? ratingKey : mediaRk;
                            if (!latestSessionsByMediaRk.TryGetValue(mediaRk, out var set))
                            {
                                set = new HashSet<string>(StringComparer.Ordinal);
                                latestSessionsByMediaRk[mediaRk] = set;
                            }
                            set.Add(sessionKey);
                        }
                    }
                }

                foreach (var (mediaRk, keys) in latestSessionsByMediaRk)
                {
                    activeStreamsByRk[mediaRk] = keys.Count;
                }
                sessionsSucceeded = true;
            }
        }
        catch
        {
            // Continue if /status/sessions is unavailable; preserve existing DB active_stream_count
        }

        // 2. Enumerate Plex library sections
        var (secStatus, secBody) = await httpGateway.SendJsonAsync(HttpMethod.Get, $"{serverUrl}/library/sections", null, headers, ct);
        if (secStatus is < 200 or >= 300)
        {
            throw new InvalidOperationException($"Plex library sections query failed with HTTP {secStatus}.");
        }

        var generalSettings = await GetGeneralSettingsAsync(ct);
        var libraries = await catalogService.ListLibrariesAsync(null, ct);
        var works = await catalogService.ListWorksAsync(null, null, null, ct);
        var torrentFileCache = BuildTorrentDownloadFileCache(generalSettings.TorrentDownloadsPath);
        var requestsByWorkId = await LoadActiveRequestsByWorkIdAsync(ct);

        var sectionsScanned = 0;
        var moviesSynced = 0;
        var showsSynced = 0;
        const int seasonsSynced = 0;
        const int episodesSynced = 0;
        var pendingUpserts = new List<UpsertCleanerrMediaItemRequest>();

        using var secDoc = JsonDocument.Parse(secBody);
        if (secDoc.RootElement.TryGetProperty("MediaContainer", out var mc) &&
            mc.TryGetProperty("Directory", out var dirs) &&
            dirs.ValueKind == JsonValueKind.Array)
        {
            foreach (var dir in dirs.EnumerateArray())
            {
                var secKey = dir.TryGetProperty("key", out var kEl) ? kEl.GetString() : null;
                var secType = dir.TryGetProperty("type", out var tEl) ? tEl.GetString()?.ToLowerInvariant() : null;
                if (string.IsNullOrWhiteSpace(secKey) || secType is not ("movie" or "show"))
                {
                    continue;
                }

                sectionsScanned++;
                var matchedLib = libraries.FirstOrDefault(l => string.Equals(l.PlexSectionId, secKey, StringComparison.OrdinalIgnoreCase))
                                 ?? libraries.FirstOrDefault(l =>
                                     (secType == "movie" && l.MediaType == "movies") ||
                                     (secType == "show" && l.MediaType is "tv" or "anime"));

                if (secType == "movie")
                {
                    var (_, allBody) = await httpGateway.SendJsonAsync(HttpMethod.Get, $"{serverUrl}/library/sections/{Uri.EscapeDataString(secKey)}/all", null, headers, ct);
                    var rawMovies = EnumeratePlexMetadata(allBody).ToList();
                    var detailTagsByRk = await FetchPlexDetailTagsByRatingKeyAsync(serverUrl, headers, rawMovies, ct);
                    foreach (var m in rawMovies)
                    {
                        var rk = GetJsonString(m, "ratingKey");
                        var title = GetJsonString(m, "title");
                        if (string.IsNullOrWhiteSpace(rk) || string.IsNullOrWhiteSpace(title)) continue;

                        var (sizeBytes, resolution, filePath) = ExtractPlexMediaPartInfo(m);
                        var matchedWork = works.FirstOrDefault(w =>
                            string.Equals(w.PlexRatingKey, rk, StringComparison.Ordinal) ||
                            string.Equals(w.Title, title, StringComparison.OrdinalIgnoreCase));
                        MediaUnitDetail? firstUnit = null;
                        MediaFileSummary? firstFile = null;
                        if (matchedWork is not null)
                        {
                            var tree = await catalogService.GetWorkTreeAsync(matchedWork.Id, null, ct);
                            var allUnits = tree?.Containers.SelectMany(c => c.Units).ToList() ?? [];
                            firstUnit = allUnits.FirstOrDefault();
                            firstFile = allUnits.SelectMany(u => u.Files).FirstOrDefault();
                        }

                        var movieCfScore = matchedWork is not null
                            ? await ComputeCustomFormatScoreAsync(matchedWork, matchedLib, firstUnit, firstFile, ct)
                            : 0;
                        var mergedTags = MergeTagLists(ExtractPlexLabelsAndCollections(m), detailTagsByRk.GetValueOrDefault(rk));

                        pendingUpserts.Add(new UpsertCleanerrMediaItemRequest(
                            PlexRatingKey: rk,
                            MediaType: "movie",
                            Title: title,
                            LibraryId: matchedLib?.Id,
                            WorkId: matchedWork?.Id,
                            UnitId: firstUnit?.Id,
                            Year: GetJsonInt(m, "year"),
                            SizeBytes: sizeBytes,
                            Resolution: resolution ?? "1080p",
                            Rating: GetJsonDouble(m, "audienceRating") ?? GetJsonDouble(m, "rating"),
                            Genres: ExtractPlexTagArray(m, "Genre"),
                            Tags: mergedTags,
                            Monitored: matchedWork?.Monitored ?? true,
                            AddedAt: EpochToIso(GetJsonLong(m, "addedAt")),
                            LastViewedAt: EpochToIso(GetJsonLong(m, "lastViewedAt")),
                            ViewCount: GetJsonInt(m, "viewCount") ?? 0,
                            ActiveStreamCount: 0,
                            FilePath: filePath,
                            ViewOffsetMs: GetJsonLong(m, "viewOffset") ?? 0L,
                            IsHardlinked: IsFileHardlinkedOnDisk(filePath, torrentFileCache, generalSettings.TorrentDownloadsPath),
                            CustomFormatScore: movieCfScore));
                        moviesSynced++;
                    }
                }
                else if (secType == "show")
                {
                    var (_, showsBody) = await httpGateway.SendJsonAsync(HttpMethod.Get, $"{serverUrl}/library/sections/{Uri.EscapeDataString(secKey)}/all?type=2", null, headers, ct);
                    var (_, episodesBody) = await httpGateway.SendJsonAsync(HttpMethod.Get, $"{serverUrl}/library/sections/{Uri.EscapeDataString(secKey)}/all?type=4", null, headers, ct);

                    var rawShows = EnumeratePlexMetadata(showsBody).ToList();
                    var parsedEpisodes = EnumeratePlexMetadata(episodesBody).ToList();
                    var showDetailTags = await FetchPlexDetailTagsByRatingKeyAsync(serverUrl, headers, rawShows, ct);

                    var epRollupByShowRk = new Dictionary<string, ShowEpisodeRollup>(StringComparer.Ordinal);
                    foreach (var ep in parsedEpisodes)
                    {
                        var grandRk = GetJsonString(ep, "grandparentRatingKey");
                        if (string.IsNullOrWhiteSpace(grandRk)) continue;

                        if (!epRollupByShowRk.TryGetValue(grandRk, out var agg))
                        {
                            agg = new ShowEpisodeRollup();
                            epRollupByShowRk[grandRk] = agg;
                        }

                        var (epBytes, epRes, epPath) = ExtractPlexMediaPartInfo(ep);
                        agg.SizeBytes += epBytes;

                        var epLastViewed = GetJsonLong(ep, "lastViewedAt") ?? 0L;
                        if (epLastViewed > agg.MaxLastViewedEpoch)
                        {
                            agg.MaxLastViewedEpoch = epLastViewed;
                        }

                        var epViewOffset = GetJsonLong(ep, "viewOffset") ?? 0L;
                        if (epViewOffset > agg.MaxViewOffsetMs)
                        {
                            agg.MaxViewOffsetMs = epViewOffset;
                        }

                        agg.SumViewCount += GetJsonInt(ep, "viewCount") ?? 0;

                        if (!agg.AnyHardlinked && IsFileHardlinkedOnDisk(epPath, torrentFileCache, generalSettings.TorrentDownloadsPath))
                        {
                            agg.AnyHardlinked = true;
                        }

                        if (string.IsNullOrWhiteSpace(agg.FirstResolution) && !string.IsNullOrWhiteSpace(epRes))
                        {
                            agg.FirstResolution = epRes;
                        }

                        if (string.IsNullOrWhiteSpace(agg.FirstFilePath) && !string.IsNullOrWhiteSpace(epPath))
                        {
                            agg.FirstFilePath = epPath;
                        }
                    }

                    foreach (var sh in rawShows)
                    {
                        var rk = GetJsonString(sh, "ratingKey");
                        var title = GetJsonString(sh, "title");
                        if (string.IsNullOrWhiteSpace(rk) || string.IsNullOrWhiteSpace(title)) continue;

                        var matchedWork = works.FirstOrDefault(w =>
                            string.Equals(w.PlexRatingKey, rk, StringComparison.Ordinal) ||
                            string.Equals(w.Title, title, StringComparison.OrdinalIgnoreCase));

                        MediaWorkDetailTree? tree = null;
                        if (matchedWork is not null)
                        {
                            tree = await catalogService.GetWorkTreeAsync(matchedWork.Id, null, ct);
                        }

                        var allUnits = tree?.Containers.SelectMany(c => c.Units).ToList() ?? [];
                        var allFiles = allUnits.SelectMany(u => u.Files).ToList();
                        epRollupByShowRk.TryGetValue(rk, out var epAgg);

                        var sizeBytes = epAgg is not null && epAgg.SizeBytes > 0
                            ? epAgg.SizeBytes
                            : allFiles.Sum(f => f.SizeBytes);

                        var showLastViewedEpoch = GetJsonLong(sh, "lastViewedAt") ?? 0L;
                        var latestLastViewedEpoch = Math.Max(showLastViewedEpoch, epAgg?.MaxLastViewedEpoch ?? 0L);

                        var viewCount = Math.Max(
                            GetJsonInt(sh, "viewedLeafCount") ?? GetJsonInt(sh, "viewCount") ?? 0,
                            epAgg?.SumViewCount ?? 0);

                        var isHardlinked = (epAgg?.AnyHardlinked ?? false) || allFiles.Any(f =>
                            f.InodeHardlinked ||
                            (matchedLib is not null && !string.IsNullOrWhiteSpace(f.RelativePath) &&
                             IsFileHardlinkedOnDisk(Path.GetFullPath(Path.Combine(matchedLib.RootFolderPath, f.RelativePath)), torrentFileCache, generalSettings.TorrentDownloadsPath)));
                        var showCfScore = matchedWork is not null
                            ? await ComputeCustomFormatScoreAsync(matchedWork, matchedLib, allUnits.FirstOrDefault(), allFiles.FirstOrDefault(), ct)
                            : 0;

                        var resolution = epAgg?.FirstResolution
                                         ?? allFiles.Select(f => f.Resolution).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))
                                         ?? "1080p";

                        var audioLangs = allFiles
                            .SelectMany(f => f.DetectedAudioLangs)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        string? firstCatalogPath = null;
                        if (matchedLib is not null && allFiles.FirstOrDefault() is { } firstCatalogFile && !string.IsNullOrWhiteSpace(firstCatalogFile.RelativePath))
                        {
                            firstCatalogPath = Path.GetFullPath(Path.Combine(matchedLib.RootFolderPath, firstCatalogFile.RelativePath));
                        }

                        var mergedTags = MergeTagLists(
                            MergeTagLists(ExtractPlexLabelsAndCollections(sh), showDetailTags.GetValueOrDefault(rk)),
                            matchedWork?.Tags);

                        (long ReqId, string Username, string CreatedAt) reqInfo = default;
                        if (matchedWork is not null)
                        {
                            requestsByWorkId.TryGetValue(matchedWork.Id, out reqInfo);
                        }

                        pendingUpserts.Add(new UpsertCleanerrMediaItemRequest(
                            PlexRatingKey: rk,
                            MediaType: "show",
                            Title: title,
                            LibraryId: matchedLib?.Id,
                            WorkId: matchedWork?.Id,
                            Year: GetJsonInt(sh, "year"),
                            SizeBytes: sizeBytes,
                            Resolution: resolution,
                            Rating: GetJsonDouble(sh, "audienceRating") ?? GetJsonDouble(sh, "rating"),
                            Genres: ExtractPlexTagArray(sh, "Genre"),
                            Tags: mergedTags,
                            Monitored: matchedWork?.Monitored ?? true,
                            AddedAt: EpochToIso(GetJsonLong(sh, "addedAt")),
                            LastViewedAt: latestLastViewedEpoch > 0 ? EpochToIso(latestLastViewedEpoch) : null,
                            ViewCount: viewCount,
                            ActiveStreamCount: 0,
                            SeerrId: reqInfo.ReqId > 0 ? reqInfo.ReqId : null,
                            SeerrRequestedBy: reqInfo.Username,
                            SeerrRequestedAt: reqInfo.CreatedAt,
                            RequesterWatched: reqInfo.ReqId > 0 && viewCount > 0,
                            FilePath: epAgg?.FirstFilePath ?? firstCatalogPath,
                            VideoCodec: allFiles.FirstOrDefault()?.VideoCodec,
                            ViewOffsetMs: epAgg?.MaxViewOffsetMs ?? 0L,
                            AudioLanguages: audioLangs,
                            IsHardlinked: isHardlinked,
                            CustomFormatScore: showCfScore,
                            ClassificationKind: InferClassificationFromTagsAndType(mergedTags, "show"),
                            WorkStatus: matchedWork?.Status,
                            PosterUrl: matchedWork?.PosterUrl));
                        showsSynced++;
                    }
                }
            }
        }

        await _activeStreamSyncGate.WaitAsync(ct);
        try
        {
            Dictionary<string, int>? existingActiveStreams = null;
            if (sessionsSucceeded)
            {
                _activeStreamKeysByMediaRk.Clear();
                foreach (var (mediaRk, keys) in latestSessionsByMediaRk)
                {
                    _activeStreamKeysByMediaRk[mediaRk] = new HashSet<string>(keys, StringComparer.Ordinal);
                }
            }
            else
            {
                await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
                var rows = await readConn.QueryAsync<(string PlexRatingKey, int ActiveStreamCount)>(
                    "SELECT plex_rating_key, active_stream_count FROM cleanerr_media_items;");
                existingActiveStreams = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var (rk, count) in rows)
                {
                    if (!string.IsNullOrWhiteSpace(rk))
                    {
                        existingActiveStreams[rk] = count;
                    }
                }
            }

            foreach (var itemReq in pendingUpserts)
            {
                var streamCount = sessionsSucceeded
                    ? activeStreamsByRk.GetValueOrDefault(itemReq.PlexRatingKey)
                    : existingActiveStreams!.GetValueOrDefault(itemReq.PlexRatingKey, 0);
                await UpsertMediaItemAsync(itemReq with { ActiveStreamCount = streamCount }, ct);
            }

            await PruneLegacySeasonAndEpisodeRowsAsync(ct);
        }
        finally
        {
            _activeStreamSyncGate.Release();
        }

        return new CleanerrPlexSyncResult(
            ServerUrl: serverUrl,
            SectionsScanned: sectionsScanned,
            MoviesSynced: moviesSynced,
            ShowsSynced: showsSynced,
            SeasonsSynced: seasonsSynced,
            EpisodesSynced: episodesSynced,
            ActiveStreamsDetected: activeStreamsTotal);
    }

    public CleanerrDiscordRuntimeCallbacks BuildDiscordRuntimeCallbacks() =>
        new(
            GetOverviewStatsAsync: GetOverviewStatsAsync,
            ListMediaItemsAsync: async token =>
            {
                await SyncCatalogItemsIntoCleanerrAsync(token);
                return await ListMediaItemsAsync(token);
            },
            ListQueueAsync: ListDeletionQueueAsync,
            ListExclusionsAsync: ListExclusionRulesAsync,
            ExcludeMediaIdsAsync: ExcludeMediaIdsWithFamilyEscalationAsync,
            ExcludeAllStagedAsync: ExcludeAllStagedAsync,
            PostponeMediaIdsAsync: PostponeMediaIdsWithFamilyAsync,
            PostponeAllStagedAsync: PostponeAllStagedAsync,
            ExplainMediaAsync: (mediaId, token) => TestMediaAsync(new TestCleanerrMediaRequest(mediaId), token),
            DeleteExclusionAsync: DeleteExclusionRuleAsync,
            RunWorkflowAsync: async (workflowId, dryRunOverride, token) =>
            {
                long? targetId = workflowId;
                if (!targetId.HasValue)
                {
                    var allWfs = await ListWorkflowsAsync(token);
                    targetId = (allWfs.FirstOrDefault(w => w.Enabled) ?? allWfs.FirstOrDefault())?.Id;
                }
                if (!targetId.HasValue)
                {
                    return null;
                }

                return await ExecuteWorkflowAsync(targetId.Value, dryRunOverride, "discord", null, null, token);
            },
            ListWorkflowsAsync: ListWorkflowsAsync);

    public Task<JsonObject> HandleDiscordInteractionAsync(JsonObject interactionPayload, CancellationToken ct = default) =>
        discordService.HandleDiscordInteractionPayloadAsync(
            interactionPayload,
            (ids, user, reason, uid, cap, token) => ExcludeMediaIdsWithFamilyEscalationAsync(ids, user, reason, uid, cap, null, token),
            ExcludeAllStagedAsync,
            ct,
            BuildDiscordRuntimeCallbacks());

    public async Task<JsonObject> HandleInboundWebhookAsync(string source, JsonObject payload, CancellationToken ct = default)
    {
        var srcLower = source.Trim().ToLowerInvariant();
        long? updatedMediaId = null;
        CleanerrQueueProcessResult? rescueSummary = null;
        var excludedItems = new List<string>();
        var inboundEvent = (GetString(payload, "event")
                            ?? GetString(payload, "eventType")
                            ?? GetString(payload, "notification_type")
                            ?? GetString(payload, "type")
                            ?? "").Trim().ToLowerInvariant();

        if (srcLower == "plex")
        {
            if (string.IsNullOrWhiteSpace(inboundEvent))
            {
                inboundEvent = "media.scrobble";
            }
            var ratingKey = payload["rating_key"]?.ToString()
                            ?? payload["ratingKey"]?.ToString()
                            ?? payload["Metadata"]?["ratingKey"]?.ToString();
            var grandparentRk = payload["grandparent_rating_key"]?.ToString()
                                ?? payload["grandparentRatingKey"]?.ToString()
                                ?? payload["Metadata"]?["grandparentRatingKey"]?.ToString();
            var parentRk = payload["parent_rating_key"]?.ToString()
                           ?? payload["parentRatingKey"]?.ToString()
                           ?? payload["Metadata"]?["parentRatingKey"]?.ToString();
            var grandparentTitle = payload["Metadata"]?["grandparentTitle"]?.ToString();
            var metaType = payload["Metadata"]?["type"]?.ToString()?.Trim().ToLowerInvariant();
            var seasonNum = GetIntFromMetadata(payload, "parentIndex");
            var epNum = GetIntFromMetadata(payload, "index");
            var mediaId = GetLong(payload, "media_item_id");
            var username = payload["user"]?.ToString()
                           ?? payload["Account"]?["title"]?.ToString()
                           ?? "plex_webhook_user";
            var plexEvent = (payload["event"]?.ToString() ?? "media.scrobble").Trim().ToLowerInvariant();

            await _activeStreamSyncGate.WaitAsync(ct);
            try
            {
                var all = await ListMediaItemsAsync(ct);
                CleanerrMediaItem? target = null;
                if (mediaId.HasValue)
                {
                    target = all.FirstOrDefault(m => m.Id == mediaId.Value && m.MediaType is not ("season" or "episode"));
                }
                if (target is null && !string.IsNullOrWhiteSpace(grandparentRk))
                {
                    target = all.FirstOrDefault(m => m.MediaType == "show" && string.Equals(m.PlexRatingKey, grandparentRk, StringComparison.Ordinal));
                }
                if (target is null && !string.IsNullOrWhiteSpace(ratingKey))
                {
                    target = all.FirstOrDefault(m => string.Equals(m.PlexRatingKey, ratingKey, StringComparison.Ordinal) && m.MediaType is not ("season" or "episode"));
                }
                if (target is null && !string.IsNullOrWhiteSpace(parentRk))
                {
                    target = all.FirstOrDefault(m => m.MediaType == "show" && string.Equals(m.PlexRatingKey, parentRk, StringComparison.Ordinal));
                }
                if (target is null && metaType is "episode" or "season" && !string.IsNullOrWhiteSpace(grandparentTitle))
                {
                    target = all.FirstOrDefault(m => m.MediaType == "show" && string.Equals(m.Title, grandparentTitle, StringComparison.OrdinalIgnoreCase));
                }

                if (target is not null)
                {
                    var viewers = target.ViewedBy.ToList();
                    var isStop = plexEvent == "media.stop";
                    var isPlayOrResume = plexEvent is "media.play" or "media.resume";
                    if (!isStop && !viewers.Contains(username, StringComparer.OrdinalIgnoreCase))
                    {
                        viewers.Add(username);
                    }

                    var reqWatched = target.RequesterWatched ||
                                     (!isStop &&
                                      !string.IsNullOrWhiteSpace(target.SeerrRequestedBy) &&
                                      string.Equals(target.SeerrRequestedBy, username, StringComparison.OrdinalIgnoreCase));

                    var sessionKey = !string.IsNullOrWhiteSpace(ratingKey) ? ratingKey : target.PlexRatingKey;
                    int nextActiveStreams;
                    if (isPlayOrResume)
                    {
                        if (!_activeStreamKeysByMediaRk.TryGetValue(target.PlexRatingKey, out var set))
                        {
                            set = new HashSet<string>(StringComparer.Ordinal);
                            _activeStreamKeysByMediaRk[target.PlexRatingKey] = set;
                        }
                        set.Add(sessionKey);
                        nextActiveStreams = Math.Max(1, set.Count);
                    }
                    else if (isStop)
                    {
                        if (_activeStreamKeysByMediaRk.TryGetValue(target.PlexRatingKey, out var set))
                        {
                            set.Remove(sessionKey);
                            nextActiveStreams = set.Count;
                            if (set.Count == 0)
                            {
                                _activeStreamKeysByMediaRk.Remove(target.PlexRatingKey);
                            }
                        }
                        else
                        {
                            nextActiveStreams = string.Equals(sessionKey, target.PlexRatingKey, StringComparison.Ordinal)
                                ? 0
                                : Math.Max(0, target.ActiveStreamCount - 1);
                        }
                    }
                    else
                    {
                        nextActiveStreams = target.ActiveStreamCount;
                    }

                    var nextLastViewedAt = isStop
                        ? target.LastViewedAt
                        : DateTimeOffset.UtcNow.ToString("O");
                    var nextViewCount = isStop
                        ? target.ViewCount
                        : isPlayOrResume
                            ? Math.Max(1, target.ViewCount)
                            : target.ViewCount + 1;

                    var nowIso = DateTimeOffset.UtcNow.ToString("O");
                    await dbFactory.ExecuteWriteAsync(
                        DatabaseKind.Core,
                        async (conn, tx) =>
                        {
                            await conn.ExecuteAsync(
                                """
                                UPDATE cleanerr_media_items
                                SET last_viewed_at = @LastViewedAt,
                                    view_count = @ViewCount,
                                    viewed_by_json = @ViewedByJson,
                                    active_stream_count = @ActiveStreamCount,
                                    requester_watched = @RequesterWatched,
                                    updated_at = @Now
                                WHERE id = @Id;
                                """,
                                new
                                {
                                    target.Id,
                                    LastViewedAt = nextLastViewedAt,
                                    ViewCount = nextViewCount,
                                    ViewedByJson = JsonSerializer.Serialize(viewers, JsonOpts),
                                    ActiveStreamCount = nextActiveStreams,
                                    RequesterWatched = reqWatched ? 1 : 0,
                                    Now = nowIso,
                                },
                                transaction: tx);

                            if (!isStop)
                            {
                                if (target.UnitId.HasValue)
                                {
                                    await conn.ExecuteAsync(
                                        """
                                        UPDATE media_units
                                        SET watch_count = CASE WHEN @IsPlayOrResume = 1 THEN MAX(1, watch_count) ELSE watch_count + 1 END,
                                            last_watched_at = @LastViewedAt
                                        WHERE id = @UnitId;
                                        """,
                                        new
                                        {
                                            UnitId = target.UnitId.Value,
                                            IsPlayOrResume = isPlayOrResume ? 1 : 0,
                                            LastViewedAt = nextLastViewedAt,
                                        },
                                        transaction: tx);
                                }
                                else if (target.MediaType == "show" && target.WorkId.HasValue && seasonNum.HasValue && epNum.HasValue)
                                {
                                    await conn.ExecuteAsync(
                                        """
                                        UPDATE media_units
                                        SET watch_count = CASE WHEN @IsPlayOrResume = 1 THEN MAX(1, watch_count) ELSE watch_count + 1 END,
                                            last_watched_at = @LastViewedAt
                                        WHERE id IN (
                                            SELECT u.id
                                            FROM media_units u
                                            JOIN media_containers c ON c.id = u.container_id
                                            WHERE c.work_id = @Wid AND c.container_number = @Sn AND u.unit_number = @En
                                        );
                                        """,
                                        new
                                        {
                                            Wid = target.WorkId.Value,
                                            Sn = seasonNum.Value,
                                            En = epNum.Value,
                                            IsPlayOrResume = isPlayOrResume ? 1 : 0,
                                            LastViewedAt = nextLastViewedAt,
                                        },
                                        transaction: tx);
                                }
                            }
                        },
                        ct);

                    updatedMediaId = target.Id;
                }
            }
            finally
            {
                _activeStreamSyncGate.Release();
            }

            if (updatedMediaId.HasValue)
            {
                rescueSummary = await ProcessDeletionQueueAsync(null, ct);
            }
        }
        else if (srcLower is "discord" or "external" or "exclude" or "n8n")
        {
            var action = (GetString(payload, "action") ?? "exclude").ToLowerInvariant();
            var reason = GetString(payload, "reason") ?? $"Excluded via {srcLower} webhook";
            var user = GetString(payload, "user") ?? srcLower;
            var tagVal = GetString(payload, "tag");
            var queueId = GetLong(payload, "queue_id");
            var mediaId = GetLong(payload, "media_item_id");

            if (!string.IsNullOrWhiteSpace(tagVal))
            {
                await CreateExclusionRuleAsync(new CreateCleanerrExclusionRequest(RuleType: "tag", Value: tagVal, Reason: reason), user, null, null, ct);
                excludedItems.Add($"tag:{tagVal}");
            }

            if (queueId.HasValue)
            {
                if (action == "rescue")
                    await RescueQueueItemAsync(queueId.Value, reason, ct);
                else
                    await ExcludeQueueItemAsync(queueId.Value, user, null, null, reason, null, ct);
                excludedItems.Add($"queue_id:{queueId.Value}");
            }

            if (mediaId.HasValue)
            {
                var titles = await ExcludeMediaIdsWithFamilyEscalationAsync([mediaId.Value], user, reason, null, null, null, ct);
                excludedItems.AddRange(titles);
                rescueSummary = await ProcessDeletionQueueAsync(null, ct);
            }
        }
        else if (srcLower is "managerr" or "seerr" or "overseerr" or "jellyseerr" or "radarr" or "sonarr")
        {
            var action = (GetString(payload, "action") ?? "").Trim().ToLowerInvariant();
            var reason = GetString(payload, "reason") ?? $"Webhook event from {srcLower}";
            var actor = GetString(payload, "user")
                        ?? payload["request"]?["requestedBy"]?["username"]?.ToString()
                        ?? payload["requestedBy"]?.ToString()
                        ?? srcLower;

            var mediaId = GetLong(payload, "media_item_id") ?? GetLong(payload, "mediaItemId");
            var workId = GetLong(payload, "work_id") ?? GetLong(payload, "workId");
            var ratingKey = GetString(payload, "rating_key") ?? GetString(payload, "ratingKey");
            var titleCandidate = payload["movie"]?["title"]?.ToString()
                                 ?? payload["series"]?["title"]?.ToString()
                                 ?? GetString(payload, "subject")
                                 ?? GetString(payload, "title");

            var all = await ListMediaItemsAsync(ct);
            CleanerrMediaItem? target = null;
            if (mediaId.HasValue)
                target = all.FirstOrDefault(m => m.Id == mediaId.Value);
            if (target is null && workId.HasValue)
                target = all.FirstOrDefault(m => m.WorkId == workId.Value);
            if (target is null && !string.IsNullOrWhiteSpace(ratingKey))
                target = all.FirstOrDefault(m => string.Equals(m.PlexRatingKey, ratingKey, StringComparison.Ordinal));
            if (target is null && !string.IsNullOrWhiteSpace(titleCandidate))
                target = all.FirstOrDefault(m => string.Equals(m.Title, titleCandidate.Trim(), StringComparison.OrdinalIgnoreCase));

            if (target is not null)
            {
                updatedMediaId = target.Id;

                if (srcLower is "seerr" or "overseerr" or "jellyseerr")
                {
                    var reqBy = payload["request"]?["requestedBy"]?["username"]?.ToString()
                                ?? payload["request"]?["requestedBy"]?["plexUsername"]?.ToString()
                                ?? GetString(payload, "requestedBy");
                    long? seerrReqId = long.TryParse(payload["request"]?["id"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid)
                        ? srid
                        : GetLong(payload, "seerr_id");
                    if (!string.IsNullOrWhiteSpace(reqBy) || seerrReqId.HasValue)
                    {
                        var nowIso = DateTimeOffset.UtcNow.ToString("O");
                        await dbFactory.ExecuteWriteAsync(
                            DatabaseKind.Core,
                            (conn, tx) => conn.ExecuteAsync(
                                """
                                UPDATE cleanerr_media_items
                                SET seerr_id = COALESCE(@SeerrId, seerr_id),
                                    seerr_requested_by = COALESCE(@ReqBy, seerr_requested_by),
                                    seerr_requested_at = COALESCE(seerr_requested_at, @Now),
                                    updated_at = @Now
                                WHERE id = @Id;
                                """,
                                new { target.Id, SeerrId = seerrReqId, ReqBy = reqBy?.Trim(), Now = nowIso },
                                transaction: tx),
                            ct);
                    }
                }

                if (action == "exclude")
                {
                    var titles = await ExcludeMediaIdsWithFamilyEscalationAsync([target.Id], actor, reason, null, null, null, ct);
                    excludedItems.AddRange(titles);
                    rescueSummary = await ProcessDeletionQueueAsync(null, ct);
                }
                else if (action == "rescue")
                {
                    var rescued = await RescueMediaIdsWithFamilyAsync([target.Id], reason, ct);
                    if (rescued.Count > 0)
                    {
                        excludedItems.AddRange(rescued.Select(r => r.MediaTitle));
                    }
                }
            }
        }

        // Trigger any enabled workflows with a matching trigger.webhook node
        var workflows = (await ListWorkflowsAsync(ct)).Where(w => w.Enabled).ToList();
        var triggered = new JsonArray();
        foreach (var wf in workflows)
        {
            var nodes = wf.GraphJson["nodes"]?.AsArray()?.OfType<JsonObject>() ?? [];
            foreach (var n in nodes)
            {
                var ntype = n["data"]?["node_type"]?.GetValue<string>() ?? n["node_type"]?.GetValue<string>();
                if (ntype == "trigger.webhook")
                {
                    var cfgObj = n["data"]?["config"]?.AsObject() ?? n["config"]?.AsObject();
                    var cfgSrc = (cfgObj?["source"]?.GetValue<string>() ?? "any").Trim().ToLowerInvariant();
                    var cfgEvent = cfgObj?["event"]?.GetValue<string>()?.Trim().ToLowerInvariant();

                    var normalizedCfgSrc = cfgSrc is "overseerr" or "jellyseerr" ? "seerr" : cfgSrc;
                    var normalizedSrc = srcLower is "overseerr" or "jellyseerr" ? "seerr" : srcLower;
                    var sourceMatches = normalizedCfgSrc is "" or "any" or "*" || normalizedCfgSrc == normalizedSrc;
                    var eventMatches = string.IsNullOrWhiteSpace(cfgEvent) ||
                                       cfgEvent is "any" or "*" ||
                                       (!string.IsNullOrWhiteSpace(inboundEvent) &&
                                        string.Equals(cfgEvent, inboundEvent, StringComparison.OrdinalIgnoreCase));

                    if (sourceMatches && eventMatches)
                    {
                        await ExecuteWorkflowAsync(wf.Id, wf.DryRun, "webhook", null, null, ct);
                        triggered.Add(wf.Id);
                        break;
                    }
                }
            }
        }

        return new JsonObject
        {
            ["status"] = "processed",
            ["source"] = srcLower,
            ["event"] = inboundEvent,
            ["updated_media_id"] = updatedMediaId.HasValue ? JsonValue.Create(updatedMediaId.Value) : null,
            ["excluded_items"] = new JsonArray(excludedItems.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
            ["rescued_count"] = rescueSummary?.RescuedCount ?? 0,
            ["triggered_workflows"] = triggered,
        };
    }

    private async Task<int> ComputeCustomFormatScoreAsync(
        MediaWorkSummary work,
        LibrarySummary? library,
        MediaUnitDetail? firstUnit,
        MediaFileSummary? firstFile,
        CancellationToken ct)
    {
        if (profileService is null || firstFile is null || string.IsNullOrWhiteSpace(firstFile.RelativePath))
        {
            return 0;
        }

        var profileId = firstUnit?.ProfileOverrideId ?? work.QualityProfileId ?? library?.DefaultProfileId;
        if (!profileId.HasValue || profileId.Value <= 0)
        {
            return 0;
        }

        try
        {
            var releaseTitle = Path.GetFileNameWithoutExtension(firstFile.RelativePath);
            if (string.IsNullOrWhiteSpace(releaseTitle))
            {
                releaseTitle = work.Title;
            }

            var scorecard = await profileService.SimulateAsync(
                new SimulateReleaseRequest(
                    ProfileId: profileId.Value,
                    ReleaseTitle: releaseTitle,
                    OriginalLanguage: work.OriginalLanguage,
                    UnitId: firstUnit?.Id,
                    SizeBytes: firstFile.SizeBytes,
                    ExplicitAudioLanguages: firstFile.DetectedAudioLangs,
                    ExplicitSubtitleLanguages: firstFile.DetectedSubLangs,
                    WorkTags: work.Tags,
                    MediaVertical: library?.MediaType,
                    WorkId: work.Id),
                ct);
            return (int)Math.Round(scorecard.TraitScore);
        }
        catch
        {
            return 0;
        }
    }

    private async Task TryExecutePlexItemActionAsync(
        CleanerrMediaItem item,
        string mode,
        CancellationToken ct)
    {
        try
        {
            var (serverUrl, headers) = await TryResolvePlexConnectionAsync(ct);
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                return;
            }

            var normalizedMode = mode.Trim().ToLowerInvariant();
            if (normalizedMode == "remove_media")
            {
                normalizedMode = "delete_from_plex";
            }

            if (normalizedMode is "refresh_library" or "empty_trash" or "refresh_library_and_empty_trash")
            {
                var sectionKeys = await ResolvePlexSectionKeysForItemAsync(serverUrl, headers, item, ct);
                if (normalizedMode is "refresh_library" or "refresh_library_and_empty_trash")
                {
                    if (sectionKeys.Count > 0)
                    {
                        foreach (var secKey in sectionKeys)
                        {
                            await httpGateway.SendJsonAsync(HttpMethod.Get, $"{serverUrl}/library/sections/{Uri.EscapeDataString(secKey)}/refresh", null, headers, ct);
                        }
                    }
                    else
                    {
                        await httpGateway.SendJsonAsync(HttpMethod.Get, $"{serverUrl}/library/sections/all/refresh", null, headers, ct);
                    }
                }

                if (normalizedMode is "empty_trash" or "refresh_library_and_empty_trash")
                {
                    foreach (var secKey in sectionKeys)
                    {
                        await httpGateway.SendJsonAsync(HttpMethod.Put, $"{serverUrl}/library/sections/{Uri.EscapeDataString(secKey)}/emptyTrash", null, headers, ct);
                    }
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(item.PlexRatingKey) || !item.PlexRatingKey.All(char.IsDigit))
            {
                return;
            }

            var rk = Uri.EscapeDataString(item.PlexRatingKey);
            switch (normalizedMode)
            {
                case "unmatch":
                    await httpGateway.SendJsonAsync(HttpMethod.Put, $"{serverUrl}/library/metadata/{rk}/unmatch", null, headers, ct);
                    break;
                case "delete_from_plex":
                    await httpGateway.SendJsonAsync(HttpMethod.Delete, $"{serverUrl}/library/metadata/{rk}", null, headers, ct);
                    break;
                default:
                    await httpGateway.SendJsonAsync(HttpMethod.Put, $"{serverUrl}/library/metadata/{rk}/refresh", null, headers, ct);
                    break;
            }
        }
        catch
        {
            // Never fail core cleanup if Plex HTTP fails
        }
    }

    private async Task<List<string>> ResolvePlexSectionKeysForItemAsync(
        string serverUrl,
        IReadOnlyDictionary<string, string> headers,
        CleanerrMediaItem item,
        CancellationToken ct)
    {
        var keys = new List<string>();
        if (item.LibraryId.HasValue)
        {
            var libs = await catalogService.ListLibrariesAsync(null, ct);
            var matched = libs.FirstOrDefault(l => l.Id == item.LibraryId.Value);
            if (!string.IsNullOrWhiteSpace(matched?.PlexSectionId))
            {
                keys.Add(matched.PlexSectionId.Trim());
                return keys;
            }
        }

        try
        {
            var (st, secBody) = await httpGateway.SendJsonAsync(HttpMethod.Get, $"{serverUrl}/library/sections", null, headers, ct);
            if (st is >= 200 and < 300 && !string.IsNullOrWhiteSpace(secBody))
            {
                using var doc = JsonDocument.Parse(secBody);
                if (doc.RootElement.TryGetProperty("MediaContainer", out var mc) &&
                    mc.TryGetProperty("Directory", out var dirs) &&
                    dirs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in dirs.EnumerateArray())
                    {
                        var key = GetJsonString(d, "key");
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            keys.Add(key);
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore section discovery failure
        }

        return keys;
    }

    private async Task<(string? ServerUrl, Dictionary<string, string> Headers)> TryResolvePlexConnectionAsync(CancellationToken ct)
    {
        var serverUrl = ((appSettings is not null ? await appSettings.GetAsync("plex.bound_server_url", ct) : null)
                         ?? Environment.GetEnvironmentVariable("MANAGERR_PLEX_URL"))?.Trim().TrimEnd('/');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/json",
        };
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return (null, headers);
        }

        var token = ((appSettings is not null ? await appSettings.GetSecretAsync("plex.owner_admin_token", ct) : null)
                     ?? Environment.GetEnvironmentVariable("MANAGERR_PLEX_TOKEN"))?.Trim();
        if (!string.IsNullOrWhiteSpace(token))
        {
            headers["X-Plex-Token"] = token;
        }
        return (serverUrl, headers);
    }

    private async Task TryRefreshPlexSectionsAsync(CancellationToken ct)
    {
        try
        {
            var (serverUrl, headers) = await TryResolvePlexConnectionAsync(ct);
            if (string.IsNullOrWhiteSpace(serverUrl)) return;
            await httpGateway.SendJsonAsync(HttpMethod.Get, $"{serverUrl}/library/sections/all/refresh", null, headers, ct);
        }
        catch
        {
            // Best-effort refresh
        }
    }

    private async Task<Dictionary<string, IReadOnlyList<string>>> FetchPlexDetailTagsByRatingKeyAsync(
        string serverUrl,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyList<JsonElement> summaryItems,
        CancellationToken ct)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var ratingKeys = summaryItems
            .Select(m => GetJsonString(m, "ratingKey"))
            .Where(rk => !string.IsNullOrWhiteSpace(rk) && rk.All(char.IsDigit))
            .Select(rk => rk!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var chunk in ratingKeys.Chunk(40))
        {
            try
            {
                var ids = string.Join(",", chunk);
                var (st, body) = await httpGateway.SendJsonAsync(HttpMethod.Get, $"{serverUrl}/library/metadata/{ids}", null, headers, ct);
                if (st is < 200 or >= 300 || string.IsNullOrWhiteSpace(body)) continue;
                foreach (var detail in EnumeratePlexMetadata(body))
                {
                    var rk = GetJsonString(detail, "ratingKey");
                    if (!string.IsNullOrWhiteSpace(rk))
                    {
                        map[rk] = ExtractPlexLabelsAndCollections(detail);
                    }
                }
            }
            catch
            {
                // Ignore if Plex server does not return /library/metadata/{ids}
            }
        }

        return map;
    }

    private static IReadOnlyList<string> MergeTagLists(IReadOnlyList<string>? first, IReadOnlyList<string>? second)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var t in (first ?? []).Concat(second ?? []))
        {
            if (!string.IsNullOrWhiteSpace(t) && set.Add(t.Trim()))
            {
                list.Add(t.Trim());
            }
        }
        return list;
    }

    private static IReadOnlyList<JsonElement> EnumeratePlexMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("MediaContainer", out var mc) &&
                mc.TryGetProperty("Metadata", out var meta) &&
                meta.ValueKind == JsonValueKind.Array)
            {
                return meta.EnumerateArray().Select(e => e.Clone()).ToList();
            }
        }
        catch
        {
            // Ignore malformed response
        }
        return [];
    }

    private static (long SizeBytes, string? Resolution, string? FilePath) ExtractPlexMediaPartInfo(JsonElement meta)
    {
        long totalBytes = 0;
        string? res = null;
        string? filePath = null;

        if (meta.TryGetProperty("Media", out var mediaArr) && mediaArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var media in mediaArr.EnumerateArray())
            {
                if (res is null && GetJsonString(media, "videoResolution") is { Length: > 0 } vr)
                {
                    res = vr.ToLowerInvariant() switch
                    {
                        "4k" or "2160" or "2160p" => "2160p",
                        "1080" or "1080p" => "1080p",
                        "720" or "720p" => "720p",
                        "480" or "480p" or "sd" => "480p",
                        _ => vr.EndsWith("p", StringComparison.OrdinalIgnoreCase) ? vr : $"{vr}p",
                    };
                }

                if (media.TryGetProperty("Part", out var partArr) && partArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in partArr.EnumerateArray())
                    {
                        totalBytes += GetJsonLong(part, "size") ?? 0L;
                        filePath ??= GetJsonString(part, "file");
                    }
                }
            }
        }

        return (totalBytes, res, filePath);
    }

    private static IReadOnlyList<string> ExtractPlexTagArray(JsonElement meta, string propName)
    {
        if (!meta.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<string>();
        foreach (var el in arr.EnumerateArray())
        {
            var tag = GetJsonString(el, "tag");
            if (!string.IsNullOrWhiteSpace(tag))
            {
                list.Add(tag.Trim());
            }
        }
        return list;
    }

    private static IReadOnlyList<string> ExtractPlexLabelsAndCollections(JsonElement meta) =>
        MergeTagLists(ExtractPlexTagArray(meta, "Label"), ExtractPlexTagArray(meta, "Collection"));

    private static string? EpochToIso(long? epochSeconds) =>
        epochSeconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(epochSeconds.Value).ToString("O") : null;

    private static string? GetJsonString(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static int? GetJsonInt(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var iv)) return iv;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sv)) return sv;
        return null;
    }

    private static long? GetJsonLong(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var lv)) return lv;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sv)) return sv;
        return null;
    }

    private static double? GetJsonDouble(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var dv)) return dv;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var sv)) return sv;
        return null;
    }

    private static int? GetIntFromMetadata(JsonObject payload, string prop)
    {
        var node = payload["Metadata"]?[prop];
        return node is not null && int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }

    private sealed class ShowEpisodeRollup
    {
        public long SizeBytes { get; set; }
        public long MaxLastViewedEpoch { get; set; }
        public long MaxViewOffsetMs { get; set; }
        public int SumViewCount { get; set; }
        public bool AnyHardlinked { get; set; }
        public string? FirstResolution { get; set; }
        public string? FirstFilePath { get; set; }
    }
}
