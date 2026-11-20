using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Metadata;
using Managerr.Security.Users;

namespace Managerr.Catalog;


public sealed partial class PolymorphicCatalogService
{
    public async Task<MediaWorkDetailTree> ApplyMonitorPresetAsync(
        long workId,
        string monitorPreset,
        CancellationToken ct = default)
    {
        var tree = await GetWorkTreeAsync(workId, actor: null, ct)
            ?? throw new KeyNotFoundException($"MediaWork #{workId} not found.");

        var preset = NormalizeMonitorPreset(monitorPreset);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var todayIso = today.ToString("yyyy-MM-dd");
        var recentCutoffIso = today.AddDays(-90).ToString("yyyy-MM-dd");
        var maxNonSpecialSeason = tree.Containers
            .Where(c => c.ContainerNumber > 0)
            .Select(c => c.ContainerNumber)
            .DefaultIfEmpty(1)
            .Max();

        var workMonitored = preset != "none";
        var now = DateTimeOffset.UtcNow.ToString("O");

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                if (preset is "monitorspecials" or "unmonitorspecials")
                {
                    var specialFlag = preset == "monitorspecials" ? 1 : 0;
                    if (specialFlag == 1)
                    {
                        await conn.ExecuteAsync(
                            "UPDATE media_works SET monitored = 1, updated_at = @Now WHERE id = @WorkId;",
                            new { WorkId = workId, Now = now },
                            transaction: tx);
                    }

                    foreach (var c in tree.Containers.Where(c => c.ContainerNumber == 0))
                    {
                        await conn.ExecuteAsync(
                            "UPDATE media_containers SET monitored = @Flag WHERE id = @Id;",
                            new { Id = c.Id, Flag = specialFlag },
                            transaction: tx);
                        await conn.ExecuteAsync(
                            "UPDATE media_units SET monitored = @Flag WHERE container_id = @ContainerId;",
                            new { ContainerId = c.Id, Flag = specialFlag },
                            transaction: tx);
                    }

                    return;
                }

                await conn.ExecuteAsync(
                    "UPDATE media_works SET monitored = @Mon, updated_at = @Now WHERE id = @WorkId;",
                    new { WorkId = workId, Mon = workMonitored ? 1 : 0, Now = now },
                    transaction: tx);

                foreach (var c in tree.Containers)
                {
                    var isSingleContainerWork = tree.Containers.Count == 1;
                    var anyUnitMonitored = false;

                    foreach (var u in c.Units)
                    {
                        var airIso = u.AirOrReleaseDate ?? c.ReleaseDate;
                        var uMon = workMonitored && preset switch
                        {
                            "none" => false,
                            "all" => c.ContainerNumber != 0 || isSingleContainerWork,
                            "future" => (c.ContainerNumber != 0 || isSingleContainerWork) &&
                                        (string.IsNullOrWhiteSpace(airIso) || string.Compare(airIso, todayIso, StringComparison.Ordinal) >= 0),
                            "recent" => (c.ContainerNumber != 0 || isSingleContainerWork) &&
                                        (string.IsNullOrWhiteSpace(airIso) || string.Compare(airIso, recentCutoffIso, StringComparison.Ordinal) >= 0),
                            "missing" => (c.ContainerNumber != 0 || isSingleContainerWork) && u.Files.Count == 0,
                            "existing" => u.Files.Count > 0,
                            "pilot" => c.ContainerNumber == 1 && u.UnitNumber == 1,
                            "firstseason" => c.ContainerNumber == 1,
                            "lastseason" or "latestseason" => c.ContainerNumber == maxNonSpecialSeason,
                            "ebookonly" => u.UnitKind != "audiobook_slot",
                            "audiobookonly" => u.UnitKind != "ebook_slot",
                            _ => c.ContainerNumber != 0 || isSingleContainerWork,
                        };

                        if (uMon)
                        {
                            anyUnitMonitored = true;
                        }

                        await conn.ExecuteAsync(
                            "UPDATE media_units SET monitored = @Mon WHERE id = @UnitId;",
                            new { UnitId = u.Id, Mon = uMon ? 1 : 0 },
                            transaction: tx);
                    }

                    var cMon = workMonitored && (anyUnitMonitored || (c.Units.Count == 0 && preset switch
                    {
                        "all" => c.ContainerNumber != 0 || isSingleContainerWork,
                        "firstseason" => c.ContainerNumber == 1,
                        "lastseason" or "latestseason" => c.ContainerNumber == maxNonSpecialSeason,
                        _ => false,
                    }));

                    await conn.ExecuteAsync(
                        "UPDATE media_containers SET monitored = @Mon WHERE id = @ContainerId;",
                        new { ContainerId = c.Id, Mon = cMon ? 1 : 0 },
                        transaction: tx);
                }
            },
            ct);

        return (await GetWorkTreeAsync(workId, actor: null, ct))!;
    }

    public async Task<BulkEditWorksReportDto> BulkEditWorksAsync(
        BulkEditWorksRequest request,
        CancellationToken ct = default)
    {
        var distinctIds = (request.WorkIds ?? []).Distinct().ToArray();
        var messages = new List<string>();
        if (distinctIds.Length == 0)
        {
            return new BulkEditWorksReportDto(0, 0, 0, 0, ["No works selected."]);
        }

        if (request.DeleteWorks)
        {
            var deleted = 0;
            foreach (var workId in distinctIds)
            {
                await DeleteWorkWithOptionsAsync(workId, request.DeleteFilesOnDisk, ct);
                deleted++;
            }

            messages.Add($"Deleted {deleted} work(s){(request.DeleteFilesOnDisk ? " including media files on disk" : "")}.");
            return new BulkEditWorksReportDto(distinctIds.Length, 0, deleted, 0, messages);
        }

        LibrarySummary? targetLibrary = null;
        if (request.LibraryId.HasValue)
        {
            targetLibrary = await GetLibraryByIdAsync(request.LibraryId.Value, ct)
                ?? throw new KeyNotFoundException($"Target library #{request.LibraryId.Value} not found.");
        }

        var updatedCount = 0;
        var movedFilesCount = 0;
        var incomingTags = (request.Tags ?? [])
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var tagMode = (request.TagMode ?? "").Trim().ToLowerInvariant();

        foreach (var workId in distinctIds)
        {
            var tree = await GetWorkTreeAsync(workId, actor: null, ct);
            if (tree is null)
            {
                continue;
            }

            IReadOnlyList<string>? nextTags = null;
            if (request.Tags is not null && !string.IsNullOrWhiteSpace(tagMode))
            {
                nextTags = tagMode switch
                {
                    "add" => tree.Work.Tags.Concat(incomingTags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    "remove" => tree.Work.Tags.Where(existing => !incomingTags.Contains(existing, StringComparer.OrdinalIgnoreCase)).ToArray(),
                    "replace" => incomingTags,
                    _ => null,
                };
            }

            if (targetLibrary is not null && targetLibrary.Id != tree.Work.LibraryId)
            {
                var oldLibrary = await GetLibraryByIdAsync(tree.Work.LibraryId, ct);
                if (request.MoveFilesOnLibraryChange &&
                    oldLibrary is not null &&
                    !string.IsNullOrWhiteSpace(oldLibrary.RootFolderPath) &&
                    !string.IsNullOrWhiteSpace(targetLibrary.RootFolderPath))
                {
                    foreach (var c in tree.Containers)
                    {
                        foreach (var u in c.Units)
                        {
                            foreach (var mf in u.Files)
                            {
                                var srcPath = Path.GetFullPath(Path.Combine(oldLibrary.RootFolderPath, mf.RelativePath));
                                var dstPath = Path.GetFullPath(Path.Combine(targetLibrary.RootFolderPath, mf.RelativePath));
                                if (!File.Exists(srcPath) || string.Equals(srcPath, dstPath, StringComparison.Ordinal))
                                {
                                    continue;
                                }

                                try
                                {
                                    Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                                    File.Move(srcPath, dstPath, overwrite: true);
                                    movedFilesCount++;
                                }
                                catch (IOException)
                                {
                                    Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                                    File.Copy(srcPath, dstPath, overwrite: true);
                                    File.Delete(srcPath);
                                    movedFilesCount++;
                                }
                            }
                        }
                    }
                }

                await dbFactory.ExecuteWriteAsync(
                    DatabaseKind.Core,
                    (conn, tx) => conn.ExecuteAsync(
                        "UPDATE media_works SET library_id = @LibId, updated_at = @Now WHERE id = @Id;",
                        new { Id = workId, LibId = targetLibrary.Id, Now = DateTimeOffset.UtcNow.ToString("O") },
                        transaction: tx),
                    ct);
            }

            if (request.QualityProfileId.HasValue || request.ClearQualityProfile)
            {
                var newOverride = request.ClearQualityProfile ? (long?)null : request.QualityProfileId;
                await dbFactory.ExecuteWriteAsync(
                    DatabaseKind.Core,
                    (conn, tx) => conn.ExecuteAsync(
                        "UPDATE media_units SET profile_override_id = @OverrideId WHERE work_id = @WorkId;",
                        new { WorkId = workId, OverrideId = newOverride },
                        transaction: tx),
                    ct);
            }

            await UpdateWorkAsync(
                workId,
                new UpdateMediaWorkRequest(
                    Monitored: request.Monitored,
                    Tags: nextTags,
                    MinimumAvailability: request.MinimumAvailability,
                    QualityProfileId: request.QualityProfileId,
                    ClearQualityProfile: request.ClearQualityProfile,
                    LanguagePolicyId: request.LanguagePolicyId,
                    ClearLanguagePolicy: request.ClearLanguagePolicy,
                    OriginalLanguage: request.OriginalLanguage,
                    MonitorPreset: request.MonitorPreset),
                ct);

            if (request.Monitored.HasValue && string.IsNullOrWhiteSpace(request.MonitorPreset) && !request.Monitored.Value)
            {
                await dbFactory.ExecuteWriteAsync(
                    DatabaseKind.Core,
                    async (conn, tx) =>
                    {
                        await conn.ExecuteAsync("UPDATE media_containers SET monitored = 0 WHERE work_id = @WorkId;", new { WorkId = workId }, transaction: tx);
                        await conn.ExecuteAsync("UPDATE media_units SET monitored = 0 WHERE work_id = @WorkId;", new { WorkId = workId }, transaction: tx);
                    },
                    ct);
            }

            updatedCount++;
        }

        messages.Add($"Updated {updatedCount} work(s){(movedFilesCount > 0 ? $" and moved {movedFilesCount} file(s)" : "")}.");
        return new BulkEditWorksReportDto(distinctIds.Length, updatedCount, 0, movedFilesCount, messages);
    }


    public async Task<IReadOnlyList<CalendarEntry>> GetCalendarAsync(
        DateOnly startDate,
        DateOnly endDate,
        PlexUserSummary? actor = null,
        CancellationToken ct = default)
    {
        var accessibleLibs = (await ListLibrariesAsync(actor, ct)).Select(l => l.Id).ToHashSet();
        if (accessibleLibs.Count == 0)
        {
            return [];
        }

        var startIso = startDate.ToString("yyyy-MM-dd");
        var endIso = endDate.ToString("yyyy-MM-dd") + "T23:59:59Z";

        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<CalendarRow>(
            """
            SELECT mu.id AS unit_id,
                   mu.work_id,
                   mw.library_id,
                   mw.title AS work_title,
                   mw.work_kind,
                   mc.title AS container_title,
                   mc.container_number,
                   mu.unit_kind,
                   mu.unit_number,
                   mu.title AS unit_title,
                   mu.air_or_release_date,
                   mu.monitored,
                   (SELECT COUNT(1) FROM media_files mf WHERE mf.unit_id = mu.id) AS file_count
            FROM media_units mu
            JOIN media_works mw ON mw.id = mu.work_id
            JOIN media_containers mc ON mc.id = mu.container_id
            WHERE mu.air_or_release_date IS NOT NULL
              AND mu.air_or_release_date >= @StartIso
              AND mu.air_or_release_date <= @EndIso
            ORDER BY mu.air_or_release_date ASC, mw.sort_title ASC, mu.unit_number ASC;
            """,
            new { StartIso = startIso, EndIso = endIso });

        return rows
            .Where(r => accessibleLibs.Contains(r.LibraryId))
            .Select(r => new CalendarEntry(
                UnitId: r.UnitId,
                WorkId: r.WorkId,
                LibraryId: r.LibraryId,
                WorkTitle: r.WorkTitle,
                WorkKind: r.WorkKind,
                ContainerTitle: r.ContainerTitle,
                ContainerNumber: r.ContainerNumber,
                UnitKind: r.UnitKind,
                UnitNumber: r.UnitNumber,
                UnitTitle: r.UnitTitle,
                AirOrReleaseDate: r.AirOrReleaseDate,
                Monitored: r.Monitored != 0,
                HasFile: r.FileCount > 0))
            .ToList();
    }

    public async Task<MediaRequestSummary> SubmitRequestAsync(
        PlexUserSummary requester,
        SubmitMediaRequestCommand cmd,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Title))
        {
            throw new ArgumentException("Requested media title is required.");
        }

        var library = await GetLibraryByIdAsync(cmd.LibraryId, ct)
            ?? throw new KeyNotFoundException($"Library #{cmd.LibraryId} not found.");

        if (!CanUserAccessLibrary(requester, library))
        {
            throw new UnauthorizedAccessException($"You do not have access to request media in '{library.Name}'.");
        }

        var vertical = NormalizeVertical(cmd.MediaVertical ?? library.MediaType);
        await EnforceRollingQuotaAsync(requester, vertical, ct);

        var requestedAudio = string.IsNullOrWhiteSpace(cmd.RequestedAudioLang)
            ? requester.PreferredAudioLang
            : cmd.RequestedAudioLang.Trim().ToLowerInvariant();
        var requestedSub = string.IsNullOrWhiteSpace(cmd.RequestedSubLang)
            ? requester.PreferredSubLang
            : cmd.RequestedSubLang.Trim().ToLowerInvariant();

        var autoApprove = requester.EffectivePermissions.AutoApproveRequests;
        var status = autoApprove ? "approved" : "pending";
        var extIds = cmd.ExternalIds ?? new Dictionary<string, string>();

        long? provisionedWorkId = null;
        if (autoApprove)
        {
            provisionedWorkId = await ProvisionOrPromoteRequestedWorkAsync(
                library,
                vertical,
                cmd.Title.Trim(),
                extIds,
                requestedAudio,
                requestedSub,
                requester,
                ct);
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var reqId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO media_requests (
                    requester_user_id, library_id, work_id, media_vertical,
                    title, external_ids_json, status,
                    requested_audio_lang, requested_sub_lang, created_at
                ) VALUES (
                    @UserId, @LibraryId, @WorkId, @Vertical,
                    @Title, @ExtIdsJson, @Status,
                    @AudioLang, @SubLang, @Now
                );
                SELECT last_insert_rowid();
                """,
                new
                {
                    UserId = requester.Id,
                    cmd.LibraryId,
                    WorkId = provisionedWorkId,
                    Vertical = vertical,
                    Title = cmd.Title.Trim(),
                    ExtIdsJson = JsonSerializer.Serialize(extIds, JsonOpts),
                    Status = status,
                    AudioLang = requestedAudio,
                    SubLang = requestedSub,
                    Now = now,
                },
                transaction: tx),
            ct);

        return (await GetRequestByIdAsync(reqId, ct))!;
    }

    public async Task<IReadOnlyList<MediaRequestSummary>> ListRequestsAsync(
        PlexUserSummary? actor = null,
        CancellationToken ct = default)
    {
        var accessibleLibs = (await ListLibrariesAsync(actor, ct)).Select(l => l.Id).ToHashSet();
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<RequestRow>(
            """
            SELECT mr.*, u.username AS requester_username
            FROM media_requests mr
            JOIN users u ON u.id = mr.requester_user_id
            ORDER BY mr.id DESC;
            """);

        return rows
            .Where(r => actor is null || r.RequesterUserId == actor.Id || accessibleLibs.Contains(r.LibraryId))
            .Select(MapRequest)
            .ToList();
    }

    public async Task<MediaRequestSummary> DecideRequestAsync(
        long requestId,
        bool approve,
        PlexUserSummary approver,
        CancellationToken ct = default)
    {
        var existing = await GetRequestByIdAsync(requestId, ct)
            ?? throw new KeyNotFoundException($"Request #{requestId} not found.");

        var library = await GetLibraryByIdAsync(existing.LibraryId, ct)
            ?? throw new KeyNotFoundException($"Library #{existing.LibraryId} not found.");

        if (!CanUserAccessLibrary(approver, library))
        {
            throw new UnauthorizedAccessException("Cannot approve/decline requests outside your permitted libraries.");
        }

        long? workId = existing.WorkId;
        var nextStatus = approve ? "approved" : "declined";

        if (approve && workId is null)
        {
            workId = await ProvisionOrPromoteRequestedWorkAsync(
                library,
                existing.MediaVertical,
                existing.Title,
                existing.ExternalIds,
                existing.RequestedAudioLang,
                existing.RequestedSubLang,
                approver,
                ct);
        }

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                "UPDATE media_requests SET status = @Status, work_id = @WorkId WHERE id = @Id;",
                new { Id = requestId, Status = nextStatus, WorkId = workId },
                transaction: tx),
            ct);

        return (await GetRequestByIdAsync(requestId, ct))!;
    }

    private async Task<long> ProvisionOrPromoteRequestedWorkAsync(
        LibrarySummary library,
        string vertical,
        string title,
        IReadOnlyDictionary<string, string> externalIds,
        string? requestedAudioLang,
        string? requestedSubLang,
        PlexUserSummary actor,
        CancellationToken ct)
    {
        // Check if work already exists in this library by title or external ID; if so, promote language requirements!
        var existingWorks = await ListWorksAsync(actor: null, libraryId: library.Id, ct: ct);
        var match = existingWorks.FirstOrDefault(w =>
            string.Equals(w.Title, title, StringComparison.OrdinalIgnoreCase) ||
            w.ExternalIds.Any(kv => externalIds.TryGetValue(kv.Key, out var ov) && string.Equals(kv.Value, ov, StringComparison.OrdinalIgnoreCase)));

        if (match is not null)
        {
            var tree = await GetWorkTreeAsync(match.Id, actor: null, ct);
            if (tree is not null && (!string.IsNullOrWhiteSpace(requestedAudioLang) || !string.IsNullOrWhiteSpace(requestedSubLang)))
            {
                foreach (var unit in tree.Containers.SelectMany(c => c.Units))
                {
                    var nextAudio = unit.RequiredAudioLangs.ToList();
                    if (!string.IsNullOrWhiteSpace(requestedAudioLang) &&
                        !nextAudio.Contains(requestedAudioLang, StringComparer.OrdinalIgnoreCase))
                    {
                        nextAudio.Add(requestedAudioLang);
                    }

                    var nextSubs = unit.RequiredSubLangs.ToList();
                    if (!string.IsNullOrWhiteSpace(requestedSubLang) &&
                        !nextSubs.Contains(requestedSubLang, StringComparer.OrdinalIgnoreCase))
                    {
                        nextSubs.Add(requestedSubLang);
                    }

                    // Adding a new required language resets language_cutoff_met to 0 so Phase 4 triggers a LanguageUpgrade!
                    var audioAdded = nextAudio.Count > unit.RequiredAudioLangs.Count;
                    await UpdateUnitAsync(
                        unit.Id,
                        new UpdateMediaUnitRequest(
                            RequiredAudioLangs: nextAudio,
                            RequiredSubLangs: nextSubs,
                            LanguageCutoffMet: audioAdded ? false : unit.LanguageCutoffMet),
                        ct);
                }
            }

            return match.Id;
        }

        var created = await AddWorkAsync(
            new CreateMediaWorkRequest(
                LibraryId: library.Id,
                Title: title,
                WorkKind: vertical,
                ExternalIds: externalIds,
                InitialRequiredAudioLangs: string.IsNullOrWhiteSpace(requestedAudioLang) ? [] : [requestedAudioLang],
                InitialRequiredSubLangs: string.IsNullOrWhiteSpace(requestedSubLang) ? [] : [requestedSubLang]),
            actor: null,
            ct: ct);

        return created.Work.Id;
    }

    private async Task EnforceRollingQuotaAsync(PlexUserSummary user, string vertical, CancellationToken ct)
    {
        var quota = vertical switch
        {
            MetadataVerticals.Movies => user.EffectivePermissions.MovieRequestQuota,
            MetadataVerticals.Tv or MetadataVerticals.Anime => user.EffectivePermissions.TvSeasonRequestQuota,
            _ => user.EffectivePermissions.MusicBookRequestQuota,
        };

        if (!quota.HasValue)
        {
            return;
        }

        var rawWindowDays = appSettings is not null
            ? await appSettings.GetAsync("system.quotas.rolling_window_days", ct)
            : null;
        var windowDays = int.TryParse(rawWindowDays, out var parsedWindow)
            ? Math.Clamp(parsedWindow, 1, 365)
            : 30;
        var windowStart = DateTimeOffset.UtcNow.AddDays(-windowDays).ToString("O");
        var verticalsInBucket = vertical switch
        {
            MetadataVerticals.Movies => new[] { MetadataVerticals.Movies },
            MetadataVerticals.Tv or MetadataVerticals.Anime => new[] { MetadataVerticals.Tv, MetadataVerticals.Anime },
            _ => new[] { MetadataVerticals.Music, MetadataVerticals.Ebooks, MetadataVerticals.Audiobooks, MetadataVerticals.Comics },
        };

        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var recentCount = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(1)
            FROM media_requests
            WHERE requester_user_id = @UserId
              AND media_vertical IN @Verticals
              AND status != 'declined'
              AND created_at >= @WindowStart;
            """,
            new
            {
                UserId = user.Id,
                Verticals = verticalsInBucket,
                WindowStart = windowStart,
            });

        if (recentCount >= quota.Value)
        {
            throw new InvalidOperationException(
                $"Request quota exceeded ({recentCount}/{quota.Value} {vertical} requests used in the rolling {windowDays}-day window).");
        }
    }

    private async Task<MediaRequestSummary?> GetRequestByIdAsync(long id, CancellationToken ct)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await conn.QuerySingleOrDefaultAsync<RequestRow>(
            """
            SELECT mr.*, u.username AS requester_username
            FROM media_requests mr
            JOIN users u ON u.id = mr.requester_user_id
            WHERE mr.id = @Id;
            """,
            new { Id = id });
        return row is null ? null : MapRequest(row);
    }

    private static IReadOnlyList<MetadataContainerSeed> BuildInitialContainers(
        string vertical,
        string title,
        int? year,
        IReadOnlyList<MetadataContainerSeed>? explicitContainers,
        bool dualFormatBookSlots)
    {
        if (explicitContainers is { Count: > 0 })
        {
            return explicitContainers;
        }

        var date = year.HasValue ? $"{year.Value}-01-01" : null;
        if (vertical is MetadataVerticals.Ebooks or MetadataVerticals.Audiobooks)
        {
            IReadOnlyList<MetadataUnitSeed> bookUnits = dualFormatBookSlots
                ?
                [
                    new MetadataUnitSeed("ebook_slot", 1, null, $"{title} (Ebook)", date),
                    new MetadataUnitSeed("audiobook_slot", 2, null, $"{title} (Audiobook)", date),
                ]
                : vertical == MetadataVerticals.Audiobooks
                    ? [new MetadataUnitSeed("audiobook_slot", 1, null, $"{title} (Audiobook)", date)]
                    : [new MetadataUnitSeed("ebook_slot", 1, null, $"{title} (Ebook)", date)];

            return
            [
                new MetadataContainerSeed("book", 1, title, date, bookUnits),
            ];
        }

        return vertical switch
        {
            MetadataVerticals.Tv or MetadataVerticals.Anime =>
            [
                new MetadataContainerSeed("season", 1, "Season 1", date,
                [
                    new MetadataUnitSeed("episode", 1, 1, "Episode 1", date),
                ]),
            ],
            MetadataVerticals.Music =>
            [
                new MetadataContainerSeed("album", 1, title, date,
                [
                    new MetadataUnitSeed("track", 1, 1, "Track 1", date),
                ]),
            ],
            MetadataVerticals.Comics =>
            [
                new MetadataContainerSeed("volume", 1, "Volume 1", date,
                [
                    new MetadataUnitSeed("chapter", 1, 1, "Chapter 1", date),
                ]),
            ],
            _ =>
            [
                new MetadataContainerSeed("feature_collection", 1, title, date,
                [
                    new MetadataUnitSeed("feature", 1, null, title, date),
                ]),
            ],
        };
    }

    /// <summary>
    /// Which work kinds a library of a given media type may hold: the same kind, TV and anime together, and the
    /// book family (ebooks, audiobooks, comics) together. A book in a movie library ends up in Radarr shims and
    /// movie cleanup rules.
    /// </summary>
    public static bool LibraryAcceptsWorkKind(string libraryMediaType, string workKind)
    {
        string Family(string v) => NormalizeVertical(v) switch
        {
            MetadataVerticals.Tv or MetadataVerticals.Anime => "episodic",
            MetadataVerticals.Ebooks or MetadataVerticals.Audiobooks or MetadataVerticals.Comics => "books",
            var other => other,
        };

        return Family(libraryMediaType) == Family(workKind);
    }

    private static string NormalizeVertical(string raw)
    {
        var norm = raw.Trim().ToLowerInvariant();
        return norm switch
        {
            "movie" or "movies" => MetadataVerticals.Movies,
            "tv" or "series" or "show" => MetadataVerticals.Tv,
            "anime" => MetadataVerticals.Anime,
            "music" or "audio" => MetadataVerticals.Music,
            "ebook" or "ebooks" or "book" or "books" => MetadataVerticals.Ebooks,
            "audiobook" or "audiobooks" => MetadataVerticals.Audiobooks,
            "comic" or "comics" or "manga" => MetadataVerticals.Comics,
            _ => throw new ArgumentException($"Unsupported media vertical '{raw}'."),
        };
    }

    private static string NormalizeMonitorPreset(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "all";
        }

        return raw.Trim().Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "none" or "unmonitored" => "none",
            "future" => "future",
            "missing" => "missing",
            "existing" => "existing",
            "recent" => "recent",
            "pilot" => "pilot",
            "firstseason" or "first" => "firstseason",
            "lastseason" or "latestseason" or "latest" => "lastseason",
            "monitorspecials" => "monitorspecials",
            "unmonitorspecials" => "unmonitorspecials",
            "ebookonly" => "ebookonly",
            "audiobookonly" => "audiobookonly",
            _ => "all",
        };
    }

    private static string NormalizeSlug(string? explicitSlug, string name)
    {
        var src = string.IsNullOrWhiteSpace(explicitSlug) ? name : explicitSlug;
        var slug = SlugRegex().Replace(src.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? $"lib-{Guid.NewGuid():N}"[..12] : slug;
    }

    private static string ComputeSortTitle(string title) =>
        LeadingArticleRegex().Replace(title.Trim(), "").ToLowerInvariant();

    private static string FormatExternalIdsForFts(IReadOnlyDictionary<string, string> extIds) =>
        string.Join(' ', extIds.Select(kv => $"{kv.Key}:{kv.Value} {kv.Value}"));

    private static string[] DeserializeStrings(string json) =>
        JsonSerializer.Deserialize<string[]>(json, JsonOpts) ?? [];

    private static Dictionary<string, string> DeserializeDict(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOpts)
        ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static LibrarySummary MapLibrary(LibraryRow row) =>
        new(
            Id: row.Id,
            Slug: row.Slug,
            Name: row.Name,
            MediaType: row.MediaType,
            RootFolderPath: row.RootFolderPath,
            PlexSectionId: row.PlexSectionId,
            MetadataWaterfall: DeserializeStrings(row.MetadataWaterfallJson),
            DefaultProfileId: row.DefaultProfileId,
            WorkCount: row.WorkCount,
            CreatedAt: row.CreatedAt,
            UpdatedAt: row.UpdatedAt);

    private const string WorkSummarySelectSql =
        """
        SELECT mw.*,
               (SELECT COUNT(1) FROM media_containers mc WHERE mc.work_id = mw.id) AS container_count,
               (SELECT COUNT(1) FROM media_units mu WHERE mu.work_id = mw.id) AS unit_count,
               (SELECT COUNT(1) FROM media_files mf WHERE mf.work_id = mw.id) AS file_count,
               (SELECT COUNT(1)
                FROM media_units mu
                WHERE mu.work_id = mw.id
                  AND EXISTS (SELECT 1 FROM media_files mf WHERE mf.unit_id = mu.id)) AS downloaded_unit_count,
               (SELECT COUNT(1)
                FROM media_units mu
                LEFT JOIN media_containers mc ON mc.id = mu.container_id
                WHERE mu.work_id = mw.id
                  AND (
                      EXISTS (SELECT 1 FROM media_files mf WHERE mf.unit_id = mu.id)
                      OR (
                          mu.monitored = 1
                          AND COALESCE(mc.monitored, 1) = 1
                          AND (
                              LOWER(mu.unit_kind) != 'episode'
                              OR (
                                  mu.air_or_release_date IS NOT NULL
                                  AND TRIM(mu.air_or_release_date) != ''
                                  AND SUBSTR(TRIM(mu.air_or_release_date), 1, 10) <= @TodayIso
                              )
                              OR (
                                  (mu.air_or_release_date IS NULL OR TRIM(mu.air_or_release_date) = '')
                                  AND UPPER(TRIM(COALESCE(mu.title, ''))) != 'TBA'
                                  AND NOT EXISTS (
                                      SELECT 1
                                      FROM media_units u2
                                      WHERE u2.work_id = mw.id
                                        AND u2.air_or_release_date IS NOT NULL
                                        AND TRIM(u2.air_or_release_date) != ''
                                  )
                              )
                          )
                      )
                  )) AS monitored_unit_count
        FROM media_works mw
        """;

    private static MediaWorkSummary MapWorkSummary(WorkRow row) =>
        new(
            Id: row.Id,
            LibraryId: row.LibraryId,
            WorkKind: row.WorkKind,
            PrimaryProvider: row.PrimaryProvider,
            EpisodeOrderingScheme: row.EpisodeOrderingScheme,
            Title: row.Title,
            SortTitle: row.SortTitle,
            AltTitles: DeserializeStrings(row.AltTitlesJson),
            OriginalLanguage: row.OriginalLanguage,
            Year: row.Year,
            Status: row.Status,
            Overview: row.Overview,
            PosterUrl: row.PosterUrl,
            Monitored: row.Monitored != 0,
            LockedFields: DeserializeStrings(row.LockedFieldsJson),
            ExternalIds: DeserializeDict(row.ExternalIdsJson),
            Tags: DeserializeStrings(row.TagsJson),
            PlexRatingKey: row.PlexRatingKey,
            AddedByUserId: row.AddedByUserId,
            ContainerCount: row.ContainerCount,
            UnitCount: row.UnitCount,
            FileCount: row.FileCount,
            AddedAt: row.AddedAt,
            UpdatedAt: row.UpdatedAt,
            MinimumAvailability: MinimumAvailabilityPolicy.Normalize(row.MinimumAvailability),
            QualityProfileId: row.QualityProfileId,
            LanguagePolicyId: row.LanguagePolicyId,
            MonitoredUnitCount: row.MonitoredUnitCount,
            DownloadedUnitCount: row.DownloadedUnitCount);

    private static MediaEditionSummary MapEdition(EditionRow row) =>
        new(row.Id, row.UnitId, row.EditionName, row.IsPreferred != 0, row.CreatedAt);

    private static MediaFileSummary MapFile(FileRow row) =>
        new(
            Id: row.Id,
            WorkId: row.WorkId,
            UnitId: row.UnitId,
            EditionId: row.EditionId,
            RelativePath: row.RelativePath,
            SizeBytes: row.SizeBytes,
            QualityName: row.QualityName,
            Resolution: row.Resolution,
            VideoCodec: row.VideoCodec,
            HdrFormat: row.HdrFormat,
            AudioCodec: row.AudioCodec,
            DetectedAudioLangs: DeserializeStrings(row.DetectedAudioLangsJson),
            DetectedSubLangs: DeserializeStrings(row.DetectedSubLangsJson),
            VerifiedStatus: row.VerifiedStatus,
            InodeHardlinked: row.InodeHardlinked != 0,
            ImportedAt: row.ImportedAt,
            SourceQualityName: row.SourceQualityName,
            SourceVideoCodec: row.SourceVideoCodec,
            SourceSizeBytes: row.SourceSizeBytes);

    private static MediaUnitDetail MapUnit(
        UnitRow row,
        IReadOnlyList<MediaEditionSummary> editions,
        IReadOnlyList<MediaFileSummary> files) =>
        new(
            Id: row.Id,
            WorkId: row.WorkId,
            ContainerId: row.ContainerId,
            UnitKind: row.UnitKind,
            UnitNumber: row.UnitNumber,
            AbsoluteNumber: row.AbsoluteNumber,
            Title: row.Title,
            AirOrReleaseDate: row.AirOrReleaseDate,
            Monitored: row.Monitored != 0,
            ProfileOverrideId: row.ProfileOverrideId,
            RequiredAudioLangs: DeserializeStrings(row.RequiredAudioLangsJson),
            RequiredSubLangs: DeserializeStrings(row.RequiredSubLangsJson),
            QualityCutoffMet: row.QualityCutoffMet != 0,
            LanguageCutoffMet: row.LanguageCutoffMet != 0,
            PlexRatingKey: row.PlexRatingKey,
            WatchCount: row.WatchCount,
            LastWatchedAt: row.LastWatchedAt,
            Editions: editions,
            Files: files);

    private static MediaRequestSummary MapRequest(RequestRow row) =>
        new(
            Id: row.Id,
            RequesterUserId: row.RequesterUserId,
            RequesterUsername: row.RequesterUsername,
            LibraryId: row.LibraryId,
            WorkId: row.WorkId,
            MediaVertical: row.MediaVertical,
            Title: row.Title,
            ExternalIds: DeserializeDict(row.ExternalIdsJson),
            Status: row.Status,
            RequestedAudioLang: row.RequestedAudioLang,
            RequestedSubLang: row.RequestedSubLang,
            CreatedAt: row.CreatedAt);

    public async Task<CatalogAutoClassifyReportDto> AutoClassifyCatalogWorksAsync(CancellationToken ct = default)
    {
        var libraries = await ListLibrariesAsync(actor: null, ct);
        var libById = libraries.ToDictionary(l => l.Id, l => l);

        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = (await readConn.QueryAsync<WorkRow>(
            "SELECT * FROM media_works ORDER BY id ASC;")).ToList();

        var evaluatedCount = 0;
        var updatedCount = 0;
        var countsByKind = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var items = new List<CatalogAutoClassifyItemDto>();
        var pendingUpdates = new List<(long Id, string TagsJson)>();

        foreach (var row in rows)
        {
            var vertical = NormalizeVertical(row.WorkKind);
            if (vertical is not (MetadataVerticals.Tv or MetadataVerticals.Movies or MetadataVerticals.Anime))
            {
                continue;
            }

            evaluatedCount++;
            libById.TryGetValue(row.LibraryId, out var lib);
            var extIds = DeserializeDict(row.ExternalIdsJson);
            var altTitles = DeserializeStrings(row.AltTitlesJson);
            var existingTags = DeserializeStrings(row.TagsJson);
            var lockedSet = DeserializeStrings(row.LockedFieldsJson).ToHashSet(StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<string>? hydratedGenres = null;
            string? formatHint = null;
            if (extIds.Count > 0)
            {
                try
                {
                    var resolved = await metadataFederation.ResolveWorkAsync(
                        vertical: vertical,
                        preferredProvider: row.PrimaryProvider,
                        knownExternalIds: extIds,
                        customWaterfall: lib?.MetadataWaterfall,
                        forceRefresh: false,
                        ct: ct);
                    if (resolved is not null)
                    {
                        hydratedGenres = resolved.Genres;
                        if (string.Equals(resolved.ClassificationKind, "anime-movie", StringComparison.OrdinalIgnoreCase))
                        {
                            formatHint = "MOVIE";
                        }
                    }
                }
                catch
                {
                    // Proceed with title/overview/language/ID heuristics when offline
                }
            }

            var classification = MediaTaxonomyClassifier.Classify(
                vertical: vertical,
                originalLanguage: row.OriginalLanguage,
                genres: hydratedGenres,
                externalIds: extIds,
                title: row.Title,
                altTitles: altTitles,
                formatHint: formatHint,
                overview: row.Overview,
                existingTags: existingTags,
                rootFolderPath: lib?.RootFolderPath);

            countsByKind[classification.ClassificationKind] =
                countsByKind.TryGetValue(classification.ClassificationKind, out var prev) ? prev + 1 : 1;

            var mergedTags = lockedSet.Contains("tags")
                ? existingTags
                : MediaTaxonomyClassifier.MergeWithUserTags(existingTags, classification.SuggestedTags);

            var addedTags = mergedTags
                .Except(existingTags, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var changed = !existingTags.SequenceEqual(mergedTags, StringComparer.OrdinalIgnoreCase);
            if (changed)
            {
                updatedCount++;
                pendingUpdates.Add((row.Id, JsonSerializer.Serialize(mergedTags, JsonOpts)));
            }

            items.Add(new CatalogAutoClassifyItemDto(
                WorkId: row.Id,
                Title: row.Title,
                WorkKind: vertical,
                ClassificationKind: classification.ClassificationKind,
                AddedTags: addedTags,
                AllTags: mergedTags));
        }

        if (pendingUpdates.Count > 0)
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                async (conn, tx) =>
                {
                    foreach (var (id, tagsJson) in pendingUpdates)
                    {
                        await conn.ExecuteAsync(
                            """
                            UPDATE media_works
                            SET tags_json  = @TagsJson,
                                updated_at = @Now
                            WHERE id = @Id;
                            """,
                            new { Id = id, TagsJson = tagsJson, Now = now },
                            transaction: tx);
                    }
                },
                ct);
        }

        return new CatalogAutoClassifyReportDto(
            EvaluatedCount: evaluatedCount,
            UpdatedCount: updatedCount,
            CountsByClassification: countsByKind,
            Items: items);
    }

    private sealed class LibraryRow
    {
        public long Id { get; init; }
        public string Slug { get; init; } = "";
        public string Name { get; init; } = "";
        public string MediaType { get; init; } = "";
        public string RootFolderPath { get; init; } = "";
        public string? PlexSectionId { get; init; }
        public string MetadataWaterfallJson { get; init; } = "[]";
        public long? DefaultProfileId { get; init; }
        public int WorkCount { get; init; }
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private sealed class WorkRow
    {
        public long Id { get; init; }
        public long LibraryId { get; init; }
        public string WorkKind { get; init; } = "";
        public string PrimaryProvider { get; init; } = "auto";
        public string EpisodeOrderingScheme { get; init; } = "aired";
        public string Title { get; init; } = "";
        public string SortTitle { get; init; } = "";
        public string AltTitlesJson { get; init; } = "[]";
        public string OriginalLanguage { get; init; } = "en";
        public int? Year { get; init; }
        public string Status { get; init; } = "released";
        public string Overview { get; init; } = "";
        public string? PosterUrl { get; init; }
        public int Monitored { get; init; }
        public string MinimumAvailability { get; init; } = "released";
        public long? QualityProfileId { get; init; }
        public long? LanguagePolicyId { get; init; }
        public string LockedFieldsJson { get; init; } = "[]";
        public string ExternalIdsJson { get; init; } = "{}";
        public string TagsJson { get; init; } = "[]";
        public string? PlexRatingKey { get; init; }
        public long? FtsDocId { get; init; }
        public long? AddedByUserId { get; init; }
        public int ContainerCount { get; init; }
        public int UnitCount { get; init; }
        public int FileCount { get; init; }
        public int MonitoredUnitCount { get; init; }
        public int DownloadedUnitCount { get; init; }
        public string AddedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private sealed class ContainerRow
    {
        public long Id { get; init; }
        public long WorkId { get; init; }
        public string ContainerKind { get; init; } = "";
        public int ContainerNumber { get; init; }
        public string Title { get; init; } = "";
        public string? ReleaseDate { get; init; }
        public int Monitored { get; init; }
        public string ExternalIdsJson { get; init; } = "{}";
        public string? PlexRatingKey { get; init; }
        public string CreatedAt { get; init; } = "";
    }

    private sealed class UnitRow
    {
        public long Id { get; init; }
        public long WorkId { get; init; }
        public long ContainerId { get; init; }
        public string UnitKind { get; init; } = "";
        public int UnitNumber { get; init; }
        public int? AbsoluteNumber { get; init; }
        public string Title { get; init; } = "";
        public string? AirOrReleaseDate { get; init; }
        public int Monitored { get; init; }
        public long? ProfileOverrideId { get; init; }
        public string RequiredAudioLangsJson { get; init; } = "[]";
        public string RequiredSubLangsJson { get; init; } = "[]";
        public int QualityCutoffMet { get; init; }
        public int LanguageCutoffMet { get; init; }
        public string? PlexRatingKey { get; init; }
        public int WatchCount { get; init; }
        public string? LastWatchedAt { get; init; }
        public string CreatedAt { get; init; } = "";
    }

    private sealed class EditionRow
    {
        public long Id { get; init; }
        public long UnitId { get; init; }
        public string EditionName { get; init; } = "";
        public int IsPreferred { get; init; }
        public string CreatedAt { get; init; } = "";
    }

    private sealed class FileRow
    {
        public long Id { get; init; }
        public long WorkId { get; init; }
        public long UnitId { get; init; }
        public long? EditionId { get; init; }
        public string RelativePath { get; init; } = "";
        public long SizeBytes { get; init; }
        public string QualityName { get; init; } = "";
        public string? Resolution { get; init; }
        public string? VideoCodec { get; init; }
        public string? HdrFormat { get; init; }
        public string? AudioCodec { get; init; }
        public string DetectedAudioLangsJson { get; init; } = "[]";
        public string DetectedSubLangsJson { get; init; } = "[]";
        public string VerifiedStatus { get; init; } = "unverified";
        public int InodeHardlinked { get; init; }
        public string ImportedAt { get; init; } = "";
        public string? SourceQualityName { get; init; }
        public string? SourceVideoCodec { get; init; }
        public long? SourceSizeBytes { get; init; }
    }

    private sealed class CalendarRow
    {
        public long UnitId { get; init; }
        public long WorkId { get; init; }
        public long LibraryId { get; init; }
        public string WorkTitle { get; init; } = "";
        public string WorkKind { get; init; } = "";
        public string ContainerTitle { get; init; } = "";
        public int ContainerNumber { get; init; }
        public string UnitKind { get; init; } = "";
        public int UnitNumber { get; init; }
        public string UnitTitle { get; init; } = "";
        public string AirOrReleaseDate { get; init; } = "";
        public int Monitored { get; init; }
        public int FileCount { get; init; }
    }

    private sealed class RequestRow
    {
        public long Id { get; init; }
        public long RequesterUserId { get; init; }
        public string RequesterUsername { get; init; } = "";
        public long LibraryId { get; init; }
        public long? WorkId { get; init; }
        public string MediaVertical { get; init; } = "";
        public string Title { get; init; } = "";
        public string ExternalIdsJson { get; init; } = "{}";
        public string Status { get; init; } = "";
        public string? RequestedAudioLang { get; init; }
        public string? RequestedSubLang { get; init; }
        public string CreatedAt { get; init; } = "";
    }
}
