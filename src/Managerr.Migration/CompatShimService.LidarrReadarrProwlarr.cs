using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Dapper;
using Managerr.Catalog;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Downloaders;
using Managerr.Downloaders.Parser;
using Managerr.Indexers;
using Managerr.Indexers.Categories;
using Managerr.Indexers.Torznab;
using Managerr.Metadata;
using Managerr.Profiles;
using Managerr.Security.Users;

namespace Managerr.Migration;


public sealed partial class CompatShimService
{
    // -------------------------------------------------------------------------
    // Lidarr v1 Shim (/compat/lidarr/api/v1/*)
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<object>> ListLidarrArtistsAsync(CancellationToken ct = default)
    {
        var works = await catalogService.ListWorksAsync(null, null, "music", ct);
        var libs = (await catalogService.ListLibrariesAsync(null, ct)).ToDictionary(l => l.Id);

        return works.Select(w => (object)new
        {
            id = w.Id,
            artistName = w.Title,
            foreignArtistId = w.ExternalIds.GetValueOrDefault("musicbrainz") ?? $"mbid-{w.Id}",
            monitored = w.Monitored,
            path = Path.Combine(libs.GetValueOrDefault(w.LibraryId)?.RootFolderPath ?? "/music", w.Title),
            qualityProfileId = 1,
            metadataProfileId = 1,
            statistics = new
            {
                albumCount = w.ContainerCount,
                trackCount = w.UnitCount,
                trackFileCount = w.FileCount,
            },
        }).ToList();
    }

    public async Task<object> AddLidarrArtistAsync(JsonObject body, CancellationToken ct = default)
    {
        var name = body["artistName"]?.GetValue<string>() ?? "Untitled Artist";
        var mbid = body["foreignArtistId"]?.GetValue<string>();
        var rootFolder = body["rootFolderPath"]?.GetValue<string>() ?? "/music";
        var monitored = body["monitored"]?.GetValue<bool?>() ?? true;

        var lib = await EnsureShimLibraryAsync("music", rootFolder, ct);
        var ext = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(mbid))
        {
            ext["musicbrainz"] = mbid;
        }

        var tree = await catalogService.AddWorkAsync(
            new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: name,
                WorkKind: "music",
                PrimaryProvider: "musicbrainz",
                Monitored: monitored,
                ExternalIds: ext),
            null,
            ct);

        return new
        {
            id = tree.Work.Id,
            artistName = tree.Work.Title,
            foreignArtistId = mbid ?? $"mbid-{tree.Work.Id}",
            monitored = tree.Work.Monitored,
            path = Path.Combine(lib.RootFolderPath, tree.Work.Title),
        };
    }

    public async Task<IReadOnlyList<object>> ListLidarrAlbumsAsync(long? artistId, CancellationToken ct = default)
    {
        var works = await catalogService.ListWorksAsync(null, null, "music", ct);
        if (artistId.HasValue)
        {
            works = works.Where(w => w.Id == artistId.Value).ToList();
        }

        var albums = new List<object>();
        foreach (var w in works)
        {
            var tree = await catalogService.GetWorkTreeAsync(w.Id, null, ct);
            if (tree is null)
            {
                continue;
            }
            foreach (var c in tree.Containers)
            {
                albums.Add(new
                {
                    id = c.Id,
                    artistId = w.Id,
                    title = c.Title,
                    foreignAlbumId = c.ExternalIds.GetValueOrDefault("musicbrainz") ?? $"album-{c.Id}",
                    monitored = c.Monitored,
                    releaseDate = c.ReleaseDate,
                });
            }
        }
        return albums;
    }

    // -------------------------------------------------------------------------
    // Readarr v1 Shim (/compat/readarr/api/v1/*) — Dual Ebook vs Audiobook Slot Aware!
    // -------------------------------------------------------------------------

    public async Task<string> ResolveReadarrTargetSlotAsync(
        string? querySlot,
        string? headerSlot,
        string? apiKeyPrefix,
        CancellationToken ct = default)
    {
        var explicitHint = !string.IsNullOrWhiteSpace(querySlot) ? querySlot : headerSlot;
        if (!string.IsNullOrWhiteSpace(explicitHint))
        {
            return explicitHint.Contains("audio", StringComparison.OrdinalIgnoreCase) ? "audiobook_slot" : "ebook_slot";
        }

        if (!string.IsNullOrWhiteSpace(apiKeyPrefix))
        {
            await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
            var keyName = await conn.ExecuteScalarAsync<string?>(
                "SELECT name FROM api_keys WHERE public_prefix = @Prefix;",
                new { Prefix = apiKeyPrefix });
            if (!string.IsNullOrWhiteSpace(keyName) && keyName.Contains("audio", StringComparison.OrdinalIgnoreCase))
            {
                return "audiobook_slot";
            }
        }

        return "ebook_slot";
    }

    public async Task<IReadOnlyList<object>> ListReadarrAuthorsAsync(CancellationToken ct = default)
    {
        var works = (await catalogService.ListWorksAsync(null, null, null, ct))
            .Where(w => w.WorkKind is "ebooks" or "audiobooks")
            .ToList();

        return works.Select(w => (object)new
        {
            id = w.Id,
            authorName = ExtractAuthorFromOverview(w.Overview),
            foreignAuthorId = $"author-{w.Id}",
            monitored = w.Monitored,
        }).ToList();
    }

    public async Task<IReadOnlyList<object>> ListReadarrBooksAsync(string targetSlot, CancellationToken ct = default)
    {
        var works = (await catalogService.ListWorksAsync(null, null, null, ct))
            .Where(w => w.WorkKind is "ebooks" or "audiobooks")
            .ToList();

        var books = new List<object>();
        foreach (var w in works)
        {
            var tree = await catalogService.GetWorkTreeAsync(w.Id, null, ct);
            if (tree is null)
            {
                continue;
            }

            var slotUnit = tree.Containers.SelectMany(c => c.Units).FirstOrDefault(u => u.UnitKind == targetSlot)
                           ?? tree.Containers.SelectMany(c => c.Units).FirstOrDefault();
            var slotFiles = slotUnit?.Files ?? [];

            books.Add(new
            {
                id = w.Id,
                title = w.Title,
                authorTitle = ExtractAuthorFromOverview(w.Overview),
                foreignBookId = w.ExternalIds.GetValueOrDefault("openlibrary")
                                ?? w.ExternalIds.GetValueOrDefault("goodreads")
                                ?? $"book-{w.Id}",
                targetSlot,
                monitored = slotUnit?.Monitored ?? w.Monitored,
                grabbed = slotFiles.Count > 0,
                statistics = new
                {
                    bookCount = 1,
                    bookFileCount = slotFiles.Count,
                    sizeOnDisk = slotFiles.Sum(f => f.SizeBytes),
                },
            });
        }

        return books;
    }

    public async Task<object> AddReadarrBookAsync(JsonObject body, string targetSlot, CancellationToken ct = default)
    {
        var title = body["title"]?.GetValue<string>() ?? "Untitled Book";
        var foreignBookId = body["foreignBookId"]?.GetValue<string>();
        var authorName = body["author"]?["authorName"]?.GetValue<string>()
                         ?? body["authorTitle"]?.GetValue<string>()
                         ?? "Unknown Author";
        var rootFolder = body["rootFolderPath"]?.GetValue<string>() ?? "/books";

        var lib = await EnsureShimLibraryAsync("ebooks", rootFolder, ct);

        // Reuse existing unified Book MediaWork if already added by the other slot's Readarr shim!
        var existingWorks = (await catalogService.ListWorksAsync(null, lib.Id, null, ct))
            .Where(w => w.WorkKind is "ebooks" or "audiobooks")
            .ToList();

        var match = existingWorks.FirstOrDefault(w =>
            (!string.IsNullOrWhiteSpace(foreignBookId) &&
             (w.ExternalIds.Values.Contains(foreignBookId, StringComparer.OrdinalIgnoreCase))) ||
            w.Title.Equals(title, StringComparison.OrdinalIgnoreCase));

        MediaWorkDetailTree tree;
        if (match is not null)
        {
            tree = (await catalogService.GetWorkTreeAsync(match.Id, null, ct))!;
            var targetUnit = tree.Containers.SelectMany(c => c.Units).FirstOrDefault(u => u.UnitKind == targetSlot);
            if (targetUnit is not null && !targetUnit.Monitored)
            {
                await catalogService.UpdateUnitAsync(targetUnit.Id, new UpdateMediaUnitRequest(Monitored: true), ct);
                tree = (await catalogService.GetWorkTreeAsync(match.Id, null, ct))!;
            }
        }
        else
        {
            var ext = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(foreignBookId))
            {
                ext["openlibrary"] = foreignBookId;
            }

            tree = await catalogService.AddWorkAsync(
                new CreateMediaWorkRequest(
                    LibraryId: lib.Id,
                    Title: title,
                    WorkKind: "ebooks",
                    PrimaryProvider: "openlibrary",
                    Overview: $"Author: {authorName}",
                    Monitored: true,
                    ExternalIds: ext,
                    DualFormatBookSlots: true),
                null,
                ct);

            // Only monitor the requested slot initially if desired; keep other slot unmonitored until requested
            foreach (var unit in tree.Containers.SelectMany(c => c.Units))
            {
                if (unit.UnitKind != targetSlot)
                {
                    await catalogService.UpdateUnitAsync(unit.Id, new UpdateMediaUnitRequest(Monitored: false), ct);
                }
            }
            tree = (await catalogService.GetWorkTreeAsync(tree.Work.Id, null, ct))!;
        }

        var slotState = tree.Containers.SelectMany(c => c.Units).FirstOrDefault(u => u.UnitKind == targetSlot);
        return new
        {
            id = tree.Work.Id,
            title = tree.Work.Title,
            foreignBookId = foreignBookId ?? $"book-{tree.Work.Id}",
            targetSlot,
            monitored = slotState?.Monitored ?? true,
            DualSlotsProvisioned = tree.Containers.SelectMany(c => c.Units).Count(),
        };
    }

    // -------------------------------------------------------------------------
    // Shared Queue + Prowlarr v1 + Torznab/Newznab XML & JSON Feed
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<object>> ListCompatQueueAsync(string? verticalGroup = null, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var trackedRows = await conn.QueryAsync<TrackedDownloadCompatRow>(
            """
            SELECT download_client_id AS DownloadClientId,
                   external_download_id AS ExternalDownloadId,
                   state AS State,
                   tracked_status AS TrackedStatus,
                   status_messages_json AS StatusMessagesJson,
                   work_id AS WorkId,
                   unit_ids_json AS UnitIdsJson
            FROM tracked_downloads
            """);
        var trackedMap = new Dictionary<(long ClientId, string ExternalId), TrackedDownloadCompatRow>(ClientAndExternalIdComparer.Instance);
        foreach (var row in trackedRows)
        {
            trackedMap[(row.DownloadClientId, row.ExternalDownloadId)] = row;
        }

        var items = await downloadOrchestrator.GetUnifiedQueueAsync(ct);
        var allWorks = await catalogService.ListWorksAsync(null, null, null, ct);
        var seriesTrees = verticalGroup == "sonarr"
            ? (await LoadAllSeriesTreesAsync(ct)).Where(t => t is not null).Select(t => t!).ToList()
            : [];
        var treeCache = new Dictionary<long, MediaWorkDetailTree?>();

        async Task<MediaWorkDetailTree?> TreeAsync(long workId)
        {
            if (!treeCache.TryGetValue(workId, out var tree))
            {
                tree = await catalogService.GetWorkTreeAsync(workId, null, ct);
                treeCache[workId] = tree;
            }

            return tree;
        }

        var output = new List<object>();
        var idx = 0;

        foreach (var q in items)
        {
            if (!string.IsNullOrWhiteSpace(verticalGroup) &&
                !string.IsNullOrWhiteSpace(q.MediaVertical) &&
                !MatchesVerticalGroup(q.MediaVertical, verticalGroup))
            {
                continue;
            }

            trackedMap.TryGetValue((q.ClientId, q.ExternalId), out var tracked);
            if (tracked is not null &&
                (tracked.State.Equals("Imported", StringComparison.OrdinalIgnoreCase) ||
                 tracked.State.Equals("Removed", StringComparison.OrdinalIgnoreCase) ||
                 tracked.State.Equals("Ignored", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var parsed = parserRouter.Parse(q.Title, q.MediaVertical ?? (verticalGroup == "sonarr" ? "tv" : "movies"));
            var cleanNorm = NormalizeTitleKey(parsed.CleanTitle);
            var estSeconds = q.RemainingBytes > 0 ? Math.Clamp(q.RemainingBytes / (25L * 1024L * 1024L), 10L, 7200L) : 0L;
            var etaIso = estSeconds > 0
                ? DateTimeOffset.UtcNow.AddSeconds(estSeconds).ToString("O")
                : (string?)null;
            var timeLeft = estSeconds > 0
                ? TimeSpan.FromSeconds(estSeconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
                : "00:00:00";

            var status = NormalizeCompatQueueStatus(q.Status);
            var trackedDownloadState = ResolveTrackedDownloadState(tracked, status);
            var trackedStatus = ResolveTrackedDownloadStatus(tracked, q.IsStalled, q.ErrorMessage);

            object[] statusMessages = Array.Empty<object>();
            if (!string.IsNullOrWhiteSpace(tracked?.StatusMessagesJson))
            {
                try
                {
                    var msgs = JsonSerializer.Deserialize<string[]>(tracked.StatusMessagesJson, JsonOpts);
                    if (msgs is { Length: > 0 })
                    {
                        statusMessages = [new { title = q.Title, messages = msgs }];
                    }
                }
                catch
                {
                    statusMessages = Array.Empty<object>();
                }
            }

            // Grab history knows which work and units Managerr grabbed this for, whatever the release is called
            // (German or alternate titles, obfuscated NZB names). Title matching is only the fallback for downloads
            // Managerr did not grab (#66).
            var grab = await downloadOrchestrator.FindGrabHistoryByItemAsync(q.ClientId, q.ExternalId, q.InfoHash, q.Title, ct);

            long? correlatedWorkId = grab?.WorkId ?? (tracked?.WorkId > 0 ? tracked.WorkId : null);
            IReadOnlyList<long> correlatedUnitIds = grab?.UnitIds ?? [];
            if (correlatedUnitIds.Count == 0 && !string.IsNullOrWhiteSpace(tracked?.UnitIdsJson))
            {
                try
                {
                    correlatedUnitIds = JsonSerializer.Deserialize<List<long>>(tracked.UnitIdsJson, JsonOpts) ?? [];
                }
                catch
                {
                    // Ignore JSON deserialization error
                }
            }

            if (verticalGroup == "sonarr")
            {
                MediaWorkDetailTree? tree = correlatedWorkId is { } grabWorkId ? await TreeAsync(grabWorkId) : null;
                tree ??= seriesTrees.FirstOrDefault(t =>
                    NormalizeTitleKey(t.Work.Title) == cleanNorm ||
                    t.Work.AltTitles.Any(a => NormalizeTitleKey(a) == cleanNorm));

                var units = new List<(int Season, MediaUnitDetail? Unit)>();
                if (tree is not null)
                {
                    foreach (var c in tree.Containers)
                    {
                        foreach (var u in c.Units)
                        {
                            var matches = correlatedUnitIds.Count > 0
                                ? correlatedUnitIds.Contains(u.Id)
                                : parsed.SeasonNumber == c.ContainerNumber &&
                                  (parsed.IsFullSeasonPack ||
                                   parsed.EpisodeNumbers.Contains(u.UnitNumber) ||
                                   (u.AbsoluteNumber is { } abs && parsed.AbsoluteEpisodeNumbers.Contains(abs)));
                            if (matches)
                            {
                                units.Add((c.ContainerNumber, u));
                            }
                        }
                    }
                }

                // Like Sonarr: one record per episode of the download, so multi-episode files and season packs show
                // progress on every episode they cover
                if (units.Count == 0)
                {
                    units.Add((parsed.SeasonNumber ?? 1, null));
                }

                foreach (var (season, unit) in units)
                {
                    idx++;
                    output.Add(new
                    {
                        id = idx,
                        seriesId = tree?.Work.Id ?? 0,
                        episodeId = unit?.Id ?? 0,
                        seasonNumber = season,
                        episode = unit is not null
                            ? new
                            {
                                id = unit.Id,
                                seriesId = tree!.Work.Id,
                                seasonNumber = season,
                                episodeNumber = unit.UnitNumber,
                                absoluteEpisodeNumber = unit.AbsoluteNumber,
                                title = unit.Title,
                                airDateUtc = unit.AirOrReleaseDate,
                                monitored = unit.Monitored,
                                hasFile = unit.Files.Count > 0,
                            }
                            : null,
                        title = q.Title,
                        status,
                        trackedDownloadStatus = trackedStatus,
                        trackedDownloadState,
                        statusMessages,
                        size = q.TotalBytes,
                        sizeleft = q.RemainingBytes,
                        timeleft = timeLeft,
                        estimatedCompletionTime = etaIso,
                        protocol = q.Protocol,
                        downloadId = q.ExternalId,
                        downloadClient = q.ClientName,
                    });
                }
            }
            else if (verticalGroup == "radarr")
            {
                idx++;
                var matchedMovie = correlatedWorkId is { } grabWorkId
                    ? allWorks.FirstOrDefault(w => w.Id == grabWorkId && w.WorkKind == "movies")
                    : null;
                matchedMovie ??= allWorks.FirstOrDefault(w =>
                    w.WorkKind == "movies" &&
                    (NormalizeTitleKey(w.Title) == cleanNorm ||
                     w.AltTitles.Any(a => NormalizeTitleKey(a) == cleanNorm)));

                output.Add(new
                {
                    id = idx,
                    movieId = matchedMovie?.Id ?? 0,
                    title = q.Title,
                    status,
                    trackedDownloadStatus = trackedStatus,
                    trackedDownloadState,
                    statusMessages,
                    size = q.TotalBytes,
                    sizeleft = q.RemainingBytes,
                    timeleft = timeLeft,
                    estimatedCompletionTime = etaIso,
                    protocol = q.Protocol,
                    downloadId = q.ExternalId,
                    downloadClient = q.ClientName,
                });
            }
            else
            {
                idx++;
                output.Add(new
                {
                    id = idx,
                    title = q.Title,
                    status,
                    trackedDownloadStatus = trackedStatus,
                    trackedDownloadState,
                    statusMessages,
                    size = q.TotalBytes,
                    sizeleft = q.RemainingBytes,
                    timeleft = timeLeft,
                    estimatedCompletionTime = etaIso,
                    protocol = q.Protocol,
                    downloadId = q.ExternalId,
                    downloadClient = q.ClientName,
                });
            }
        }

        return output;
    }

    private static string NormalizeCompatQueueStatus(string? rawStatus)
    {
        var s = (rawStatus ?? string.Empty).Trim();
        if (s.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("uploading", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("stalledUP", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("pausedUP", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("stoppedUP", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("queuedUP", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("forcedUP", StringComparison.OrdinalIgnoreCase))
        {
            return "completed";
        }

        if (s.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("error", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("missingFiles", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase))
        {
            return "failed";
        }

        if (s.Equals("Paused", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("pausedDL", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("stoppedDL", StringComparison.OrdinalIgnoreCase))
        {
            return "paused";
        }

        if (s.Equals("Queued", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("queuedDL", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("allocating", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("checkingDL", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("checkingUP", StringComparison.OrdinalIgnoreCase))
        {
            return "queued";
        }

        if (s.Equals("Delay", StringComparison.OrdinalIgnoreCase))
        {
            return "delay";
        }

        return "downloading";
    }

    private static string ResolveTrackedDownloadState(TrackedDownloadCompatRow? tracked, string status)
    {
        if (tracked is not null)
        {
            var s = tracked.State;
            if (s.Equals("ImportBlocked", StringComparison.OrdinalIgnoreCase)) return "importBlocked";
            if (s.Equals("ImportPending", StringComparison.OrdinalIgnoreCase)) return "importPending";
            if (s.Equals("Importing", StringComparison.OrdinalIgnoreCase)) return "importing";
            if (s.Equals("FailedPending", StringComparison.OrdinalIgnoreCase)) return "failedPending";
            if (s.Equals("Failed", StringComparison.OrdinalIgnoreCase)) return "failed";
            return "downloading";
        }

        if (status == "completed")
        {
            return "importPending";
        }

        if (status == "failed")
        {
            return "failedPending";
        }

        return "downloading";
    }

    private static string ResolveTrackedDownloadStatus(TrackedDownloadCompatRow? tracked, bool isStalled, string? errorMessage)
    {
        var hasWarning = isStalled || !string.IsNullOrWhiteSpace(errorMessage);
        if (tracked is not null)
        {
            var ts = tracked.TrackedStatus?.ToLowerInvariant();
            if (ts is "ok" or "warning" or "error")
            {
                if (ts == "ok" && hasWarning)
                {
                    return "warning";
                }
                return ts;
            }
        }

        return hasWarning ? "warning" : "ok";
    }

    private sealed record TrackedDownloadCompatRow(
        long DownloadClientId,
        string ExternalDownloadId,
        string State,
        string TrackedStatus,
        string? StatusMessagesJson,
        long? WorkId,
        string? UnitIdsJson);

    private sealed class ClientAndExternalIdComparer : IEqualityComparer<(long ClientId, string ExternalId)>
    {
        public static readonly ClientAndExternalIdComparer Instance = new();
        public bool Equals((long ClientId, string ExternalId) x, (long ClientId, string ExternalId) y) =>
            x.ClientId == y.ClientId && string.Equals(x.ExternalId, y.ExternalId, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((long ClientId, string ExternalId) obj) =>
            HashCode.Combine(obj.ClientId, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ExternalId));
    }

    public async Task<IReadOnlyList<object>> ListProwlarrIndexersAsync(CancellationToken ct = default)
    {
        var indexers = await indexerService.ListIndexersAsync(ct);
        return indexers.Select(i => (object)new
        {
            id = i.Id,
            name = i.Name,
            protocol = i.Protocol,
            implementation = i.Implementation,
            enable = i.RssEnabled || i.InteractiveSearchEnabled,
            priority = i.Priority,
            torznabUrl = $"/api/v1/torznab/{i.Id}/api",
            capabilities = new
            {
                categories = i.Categories.Select(c => new { id = c, name = $"Cat-{c}" }).ToArray(),
            },
        }).ToList();
    }

    public async Task<(string ContentType, string Payload)> ExecuteTorznabRequestAsync(
        long indexerId,
        string mode,
        string? query,
        string? catCsv,
        string? imdbId,
        int? tmdbId,
        int? tvdbId,
        int? season,
        int? episode,
        string? outputFormat,
        CancellationToken ct = default)
    {
        var indexers = await indexerService.ListIndexersAsync(ct);
        var targetIndexer = indexerId == 0
            ? null // indexerId 0 == aggregate all indexers (like /prowlarr/all/api)!
            : indexers.FirstOrDefault(i => i.Id == indexerId)
              ?? throw new KeyNotFoundException($"Indexer #{indexerId} not found.");

        if (string.Equals(mode, "caps", StringComparison.OrdinalIgnoreCase))
        {
            var capsXml = BuildTorznabCapsXml(targetIndexer?.Name ?? "Managerr Aggregate Torznab");
            return ("application/xml; charset=utf-8", capsXml);
        }

        List<int>? parsedCats = null;
        if (!string.IsNullOrWhiteSpace(catCsv))
        {
            parsedCats = catCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var c) ? c : -1)
                .Where(c => c > 0)
                .ToList();
        }

        var vertical = mode.ToLowerInvariant() switch
        {
            "tvsearch" => MediaVerticals.Tv,
            "movie" => MediaVerticals.Movies,
            "music" or "audio" => MediaVerticals.Music,
            "book" => MediaVerticals.Ebooks,
            _ => null,
        };

        var results = await indexerService.SearchAsync(
            new IndexerSearchQuery(
                Query: query,
                MediaVertical: vertical,
                Categories: parsedCats,
                ImdbId: imdbId,
                TmdbId: tmdbId,
                TvdbId: tvdbId,
                Season: season,
                Episode: episode,
                Interactive: true),
            ct);

        if (targetIndexer is not null)
        {
            results = results.Where(r => r.IndexerId == targetIndexer.Id).ToList();
        }

        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            var jsonPayload = JsonSerializer.Serialize(new
            {
                channel = new
                {
                    title = targetIndexer?.Name ?? "Managerr Torznab",
                    item = results.Select(r => new
                    {
                        title = r.Title,
                        guid = r.Guid,
                        link = r.DownloadUrl,
                        size = r.SizeBytes,
                        seeders = r.Seeders ?? 0,
                        peers = r.Peers ?? 0,
                        infoHash = r.InfoHash,
                        category = r.Categories,
                    }).ToArray(),
                },
            }, JsonOpts);
            return ("application/json; charset=utf-8", jsonPayload);
        }

        XNamespace torznabNs = "http://torznab.com/schemas/2015/feed";
        XNamespace newznabNs = "http://www.newznab.com/DTD/2010/feeds/attributes/";

        var itemsXml = results.Select(r =>
        {
            var mime = r.Protocol == "usenet" ? "application/x-nzb" : "application/x-bittorrent";
            var attrs = new List<XElement>
            {
                new(torznabNs + "attr", new XAttribute("name", "size"), new XAttribute("value", r.SizeBytes)),
                new(torznabNs + "attr", new XAttribute("name", "downloadvolumefactor"), new XAttribute("value", r.DownloadVolumeFactor.ToString(CultureInfo.InvariantCulture))),
                new(torznabNs + "attr", new XAttribute("name", "uploadvolumefactor"), new XAttribute("value", r.UploadVolumeFactor.ToString(CultureInfo.InvariantCulture))),
            };
            foreach (var c in r.Categories.DefaultIfEmpty(2000))
            {
                attrs.Add(new XElement(torznabNs + "attr", new XAttribute("name", "category"), new XAttribute("value", c)));
            }
            if (r.Seeders.HasValue)
            {
                attrs.Add(new XElement(torznabNs + "attr", new XAttribute("name", "seeders"), new XAttribute("value", r.Seeders.Value)));
            }
            if (r.Peers.HasValue)
            {
                attrs.Add(new XElement(torznabNs + "attr", new XAttribute("name", "peers"), new XAttribute("value", r.Peers.Value)));
            }
            if (!string.IsNullOrWhiteSpace(r.InfoHash))
            {
                attrs.Add(new XElement(torznabNs + "attr", new XAttribute("name", "infohash"), new XAttribute("value", r.InfoHash)));
            }

            return new XElement("item",
                new XElement("title", r.Title),
                new XElement("guid", r.Guid),
                new XElement("link", r.DownloadUrl),
                new XElement("pubDate", r.PublishedAt.ToString("R")),
                new XElement("size", r.SizeBytes),
                new XElement("enclosure",
                    new XAttribute("url", r.DownloadUrl),
                    new XAttribute("length", r.SizeBytes),
                    new XAttribute("type", mime)),
                attrs);
        });

        var rssDoc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("rss",
                new XAttribute("version", "2.0"),
                new XAttribute(XNamespace.Xmlns + "torznab", torznabNs),
                new XAttribute(XNamespace.Xmlns + "newznab", newznabNs),
                new XElement("channel",
                    new XElement("title", targetIndexer?.Name ?? "Managerr Torznab"),
                    new XElement("description", "Managerr Unified Torznab/Newznab Compatibility Feed"),
                    itemsXml)));

        return ("application/rss+xml; charset=utf-8", rssDoc.ToString());
    }

    // -------------------------------------------------------------------------
    // Internal Formatting Helpers
    // -------------------------------------------------------------------------

    private static string BuildTorznabCapsXml(string serverTitle)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("caps",
                new XElement("server",
                    new XAttribute("version", "1.0"),
                    new XAttribute("title", serverTitle)),
                new XElement("limits",
                    new XAttribute("max", "100"),
                    new XAttribute("default", "50")),
                new XElement("searching",
                    new XElement("search", new XAttribute("available", "yes"), new XAttribute("supportedParams", "q")),
                    new XElement("tv-search", new XAttribute("available", "yes"), new XAttribute("supportedParams", "q,tvdbid,season,ep")),
                    new XElement("movie-search", new XAttribute("available", "yes"), new XAttribute("supportedParams", "q,imdbid,tmdbid")),
                    new XElement("music-search", new XAttribute("available", "yes"), new XAttribute("supportedParams", "q,artist,album")),
                    new XElement("book-search", new XAttribute("available", "yes"), new XAttribute("supportedParams", "q,title,author"))),
                new XElement("categories",
                    new XElement("category", new XAttribute("id", "2000"), new XAttribute("name", "Movies")),
                    new XElement("category", new XAttribute("id", "3000"), new XAttribute("name", "Audio")),
                    new XElement("category", new XAttribute("id", "3030"), new XAttribute("name", "Audio/Audiobook")),
                    new XElement("category", new XAttribute("id", "5000"), new XAttribute("name", "TV")),
                    new XElement("category", new XAttribute("id", "5070"), new XAttribute("name", "TV/Anime")),
                    new XElement("category", new XAttribute("id", "7000"), new XAttribute("name", "Books")),
                    new XElement("category", new XAttribute("id", "7020"), new XAttribute("name", "Books/EBook")))));
        return doc.ToString();
    }

    private async Task<LibrarySummary> EnsureShimLibraryAsync(string mediaType, string rootFolderPath, CancellationToken ct)
    {
        var libs = await catalogService.ListLibrariesAsync(null, ct);
        var existing = libs.FirstOrDefault(l =>
            l.MediaType.Equals(mediaType, StringComparison.OrdinalIgnoreCase) &&
            l.RootFolderPath.Equals(rootFolderPath, StringComparison.OrdinalIgnoreCase))
            ?? libs.FirstOrDefault(l => l.MediaType.Equals(mediaType, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return existing;
        }

        if (PolymorphicCatalogService.IsForbiddenSystemRoot(rootFolderPath))
        {
            throw new ArgumentException($"Root folder path '{rootFolderPath}' points to a protected system directory.");
        }

        return await catalogService.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: $"Compat {mediaType.ToUpperInvariant()}",
                MediaType: mediaType,
                RootFolderPath: rootFolderPath),
            ct);
    }

    private async Task RecordCompatRequestAndLanguagePromotionAsync(
        long workId,
        long libraryId,
        string vertical,
        string title,
        Dictionary<string, string> extIds,
        PlexUserSummary requester,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    "UPDATE media_works SET added_by_user_id = @UserId WHERE id = @WorkId;",
                    new { UserId = requester.Id, WorkId = workId },
                    transaction: tx);

                await conn.ExecuteAsync(
                    """
                    INSERT INTO media_requests (
                        requester_user_id, library_id, work_id, media_vertical,
                        title, external_ids_json, status, requested_audio_lang, requested_sub_lang, created_at
                    ) VALUES (
                        @UserId, @LibId, @WorkId, @Vertical,
                        @Title, @ExtJson, 'approved', @AudioLang, @SubLang, @Now
                    );
                    """,
                    new
                    {
                        UserId = requester.Id,
                        LibId = libraryId,
                        WorkId = workId,
                        Vertical = vertical,
                        Title = title,
                        ExtJson = JsonSerializer.Serialize(extIds, JsonOpts),
                        AudioLang = requester.PreferredAudioLang,
                        SubLang = requester.PreferredSubLang,
                        Now = now,
                    },
                    transaction: tx);
            },
            ct);

        if (!string.IsNullOrWhiteSpace(requester.PreferredAudioLang) || !string.IsNullOrWhiteSpace(requester.PreferredSubLang))
        {
            await profileService.PromotePlexUserLanguageAsync(workId, requester, ct);
        }
    }


    private static object FormatRadarrMovieResource(
        MediaWorkSummary w,
        MediaWorkDetailTree? tree,
        LibrarySummary? lib,
        IReadOnlyList<CompatTagDto> allTags)
    {
        var tmdb = int.TryParse(w.ExternalIds.GetValueOrDefault("tmdb"), out var parsedTmdb) ? parsedTmdb : 0;
        var allUnits = tree?.Containers.SelectMany(c => c.Units).ToList() ?? [];
        var allFiles = allUnits.SelectMany(u => u.Files).ToList();
        var overrideId = allUnits.Select(u => u.ProfileOverrideId).FirstOrDefault(pid => pid.HasValue);
        var profileId = (int)(overrideId ?? lib?.DefaultProfileId ?? 1L);
        var rootFolder = lib?.RootFolderPath ?? "/movies";
        var tagIds = ResolveTagIds(w.Tags, allTags);

        return new
        {
            id = w.Id,
            title = w.Title,
            originalTitle = w.Title,
            sortTitle = w.SortTitle,
            titleSlug = BuildTitleSlug(w.Title, tmdb > 0 ? tmdb : (int)w.Id),
            status = string.IsNullOrWhiteSpace(w.Status) ? "released" : w.Status,
            overview = w.Overview,
            year = w.Year ?? 2024,
            hasFile = w.FileCount > 0 || allFiles.Count > 0,
            movieFileId = allFiles.FirstOrDefault()?.Id ?? 0L,
            sizeOnDisk = allFiles.Sum(f => f.SizeBytes),
            path = Path.Combine(rootFolder, w.Title),
            rootFolderPath = rootFolder,
            qualityProfileId = profileId,
            profileId,
            monitored = w.Monitored,
            minimumAvailability = "released",
            isAvailable = true,
            cleanTitle = NormalizeTitleKey(w.Title),
            tmdbId = tmdb,
            imdbId = w.ExternalIds.GetValueOrDefault("imdb"),
            images = BuildCoverImages(w.PosterUrl),
            remotePoster = w.PosterUrl,
            tags = tagIds,
            added = w.AddedAt,
            addedByPlexUserId = w.AddedByUserId,
        };
    }

    private static object FormatRadarrLookupCandidate(MetadataWorkCandidate c)
    {
        var tmdb = int.TryParse(c.ExternalIds.GetValueOrDefault("tmdb"), out var parsedTmdb) ? parsedTmdb : 0;
        return new
        {
            id = 0L,
            title = c.Title,
            originalTitle = c.Title,
            sortTitle = c.Title.ToLowerInvariant(),
            titleSlug = BuildTitleSlug(c.Title, tmdb),
            status = string.IsNullOrWhiteSpace(c.Status) ? "released" : c.Status,
            overview = c.Overview,
            year = c.Year ?? 2024,
            hasFile = false,
            movieFileId = 0L,
            sizeOnDisk = 0L,
            monitored = false,
            minimumAvailability = "released",
            isAvailable = true,
            qualityProfileId = 1,
            profileId = 1,
            cleanTitle = NormalizeTitleKey(c.Title),
            tmdbId = tmdb,
            imdbId = c.ExternalIds.GetValueOrDefault("imdb"),
            images = BuildCoverImages(c.PosterUrl),
            remotePoster = c.PosterUrl,
            tags = Array.Empty<int>(),
        };
    }

    private static object BuildCompatCommandResponse(int id, string name, string now) =>
        new
        {
            id,
            name,
            commandName = name,
            message = $"Executed {name}",
            status = "completed",
            queued = now,
            started = now,
            ended = now,
            trigger = "manual",
        };

    private async Task<long?> ResolveValidProfileIdAsync(long? requestedProfileId, CancellationToken ct)
    {
        if (requestedProfileId is not > 0)
        {
            return null;
        }
        var profile = await profileService.GetProfileByIdAsync(requestedProfileId.Value, ct);
        return profile?.Id;
    }

    private sealed record ContainerSummaryForExpansion(int ContainerNumber, int EpisodeCount, IReadOnlyList<string?> UnitAirDates);

    private static IReadOnlyList<ContainerSummaryForExpansion> AdaptContainers(IEnumerable<MetadataContainerSeed>? seeds) =>
        seeds?.Select(s => new ContainerSummaryForExpansion(s.ContainerNumber, s.Units.Count, s.Units.Select(u => u.AirOrReleaseDate).ToList())).ToList()
        ?? [];

    private static IReadOnlyList<ContainerSummaryForExpansion> AdaptContainers(IEnumerable<MediaContainerDetail>? details) =>
        details?.Select(d => new ContainerSummaryForExpansion(d.ContainerNumber, d.Units.Count, d.Units.Select(u => u.AirOrReleaseDate).ToList())).ToList()
        ?? [];

    private async Task<List<(int SeasonNumber, bool Monitored)>> ExpandTmdbCollapsedAnimeSeasonsIfApplicableAsync(
        string vertical,
        IReadOnlyDictionary<string, string> extIds,
        IReadOnlyList<ContainerSummaryForExpansion> tvdbContainers,
        List<(int SeasonNumber, bool Monitored)> requestedSeasons,
        CancellationToken ct)
    {
        // Guard 1: Only anime
        if (!string.Equals(vertical, "anime", StringComparison.OrdinalIgnoreCase))
        {
            return requestedSeasons;
        }

        // Guard 2:
        // Regular monitored seasons in requestedSeasons (SeasonNumber > 0 && Monitored) must be exclusively [1]
        var regularMonitored = requestedSeasons.Where(s => s.SeasonNumber > 0 && s.Monitored).ToList();
        if (regularMonitored.Count != 1 || regularMonitored[0].SeasonNumber != 1)
        {
            return requestedSeasons;
        }

        // requestedSeasons must contain at least one SeasonNumber >= 2 && !Monitored
        if (!requestedSeasons.Any(s => s.SeasonNumber >= 2 && !s.Monitored))
        {
            return requestedSeasons;
        }

        // TVDB/hydrated regular seasons (ContainerNumber > 0) must have count >= 2
        var tvdbRegular = tvdbContainers
            .Where(c => c.ContainerNumber > 0)
            .OrderBy(c => c.ContainerNumber)
            .ToList();

        if (tvdbRegular.Count < 2)
        {
            return requestedSeasons;
        }

        // Guard 3: Verify TMDB single-season collapse
        var rawSetting = await settingsService.GetAsync("compat.sonarr.expand_single_season_anime", ct);
        var forceExpand = string.Equals(rawSetting?.Trim(), "true", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(rawSetting?.Trim(), "1", StringComparison.OrdinalIgnoreCase);

        bool tmdbResolved = false;
        if (extIds.TryGetValue("tmdb", out var tmdbId) && !string.IsNullOrWhiteSpace(tmdbId))
        {
            try
            {
                var tmdbCandidate = await metadataFederation.ResolveWorkAsync(
                    "anime",
                    "tmdb",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tmdb"] = tmdbId },
                    ["tmdb"],
                    ct);

                if (tmdbCandidate is not null &&
                    string.Equals(tmdbCandidate.ProviderKey, "tmdb", StringComparison.OrdinalIgnoreCase) &&
                    tmdbCandidate.Containers.Count > 0)
                {
                    tmdbResolved = true;
                    var tmdbRegular = tmdbCandidate.Containers.Where(c => c.ContainerNumber > 0).ToList();
                    if (tmdbRegular.Count != 1 || tmdbRegular[0].ContainerNumber != 1)
                    {
                        return requestedSeasons;
                    }

                    var tmdbS1Count = tmdbRegular[0].Units.Count;
                    var tvdbS1Count = tvdbRegular.FirstOrDefault(c => c.ContainerNumber == 1)?.EpisodeCount ?? 0;
                    if (tmdbS1Count <= tvdbS1Count)
                    {
                        return requestedSeasons;
                    }

                    var todayIso = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
                    var expandedSeasonNums = new HashSet<int>();
                    var cumulativeOffset = 0;
                    foreach (var season in tvdbRegular)
                    {
                        var hasAired = season.UnitAirDates.Any(d =>
                            !string.IsNullOrWhiteSpace(d) &&
                            string.CompareOrdinal(d[..Math.Min(10, d.Length)], todayIso) <= 0);

                        if (cumulativeOffset < tmdbS1Count || hasAired)
                        {
                            expandedSeasonNums.Add(season.ContainerNumber);
                        }

                        cumulativeOffset += season.EpisodeCount;
                    }

                    return ApplyExpansion(requestedSeasons, expandedSeasonNums);
                }
            }
            catch
            {
                // Fallback to forceExpand check
            }
        }

        if (!tmdbResolved && forceExpand)
        {
            var todayIso = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            var expandedSeasonNums = new HashSet<int> { 1 };
            foreach (var season in tvdbRegular)
            {
                if (season.ContainerNumber == 1) continue;
                var hasAired = season.UnitAirDates.Any(d =>
                    !string.IsNullOrWhiteSpace(d) &&
                    string.CompareOrdinal(d[..Math.Min(10, d.Length)], todayIso) <= 0);

                if (hasAired)
                {
                    expandedSeasonNums.Add(season.ContainerNumber);
                }
            }

            return ApplyExpansion(requestedSeasons, expandedSeasonNums);
        }

        return requestedSeasons;
    }

    private static List<(int SeasonNumber, bool Monitored)> ApplyExpansion(
        List<(int SeasonNumber, bool Monitored)> requestedSeasons,
        HashSet<int> expandedSeasonNums)
    {
        var result = new List<(int SeasonNumber, bool Monitored)>();
        var seenSeasons = new HashSet<int>();
        foreach (var (sn, mon) in requestedSeasons)
        {
            seenSeasons.Add(sn);
            if (sn > 0 && expandedSeasonNums.Contains(sn))
            {
                result.Add((sn, true));
            }
            else
            {
                result.Add((sn, mon));
            }
        }

        foreach (var sn in expandedSeasonNums.OrderBy(x => x))
        {
            if (seenSeasons.Add(sn))
            {
                result.Add((sn, true));
            }
        }

        return result;
    }

    private static IReadOnlyList<MetadataContainerSeed> BuildMergedSonarrSeedContainers(
        IReadOnlyList<MetadataContainerSeed>? hydrated,
        IReadOnlyList<(int SeasonNumber, bool Monitored)> requestedSeasons)
    {
        var list = new List<MetadataContainerSeed>();
        if (hydrated is { Count: > 0 })
        {
            var includeSpecials = requestedSeasons.Count == 0 || requestedSeasons.Any(s => s.SeasonNumber == 0);
            list.AddRange(includeSpecials ? hydrated : hydrated.Where(c => c.ContainerNumber > 0));
        }

        foreach (var (sNum, _) in requestedSeasons)
        {
            if (list.All(c => c.ContainerNumber != sNum))
            {
                list.Add(new MetadataContainerSeed(
                    "season",
                    sNum,
                    sNum == 0 ? "Specials" : $"Season {sNum}",
                    null,
                    [new MetadataUnitSeed("episode", 1, null, "Episode 1", null)]));
            }
        }

        if (list.Count == 0)
        {
            list.Add(new MetadataContainerSeed(
                "season",
                1,
                "Season 1",
                null,
                [new MetadataUnitSeed("episode", 1, 1, "Episode 1", null)]));
        }

        return list.OrderBy(c => c.ContainerNumber).ToList();
    }

    private async Task ApplyRequestedSeasonMonitoringAsync(
        MediaWorkDetailTree tree,
        IReadOnlyList<(int SeasonNumber, bool Monitored)> requestedSeasons,
        long? profileOverrideId,
        bool preserveExistingMonitoring = false,
        CancellationToken ct = default)
    {
        var byNum = requestedSeasons
            .GroupBy(s => s.SeasonNumber)
            .ToDictionary(g => g.Key, g => g.Last().Monitored);

        foreach (var c in tree.Containers)
        {
            if (byNum.TryGetValue(c.ContainerNumber, out var shouldMonitor))
            {
                if (preserveExistingMonitoring && c.Monitored)
                {
                    shouldMonitor = true;
                }

                if (c.Monitored != shouldMonitor)
                {
                    await catalogService.UpdateContainerAsync(
                        c.Id,
                        new UpdateMediaContainerRequest(Monitored: shouldMonitor, CascadeUnits: true),
                        ct);
                }
            }

            if (profileOverrideId.HasValue)
            {
                foreach (var u in c.Units.Where(u => u.ProfileOverrideId != profileOverrideId))
                {
                    await catalogService.UpdateUnitAsync(
                        u.Id,
                        new UpdateMediaUnitRequest(ProfileOverrideId: profileOverrideId),
                        ct);
                }
            }
        }
    }

    private async Task InsertPlaceholderSeasonsAsync(
        long workId,
        IReadOnlyList<(int SeasonNumber, bool Monitored)> missingSeasons,
        long? profileOverrideId,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                foreach (var (sNum, mon) in missingSeasons)
                {
                    var containerId = await conn.ExecuteScalarAsync<long>(
                        """
                        INSERT INTO media_containers (
                            work_id, container_kind, container_number, title,
                            release_date, monitored, external_ids_json, created_at
                        ) VALUES (
                            @WorkId, 'season', @Num, @Title,
                            NULL, @Monitored, '{}', @Now
                        );
                        SELECT last_insert_rowid();
                        """,
                        new
                        {
                            WorkId = workId,
                            Num = sNum,
                            Title = sNum == 0 ? "Specials" : $"Season {sNum}",
                            Monitored = mon ? 1 : 0,
                            Now = now,
                        },
                        transaction: tx);

                    await conn.ExecuteAsync(
                        """
                        INSERT INTO media_units (
                            work_id, container_id, unit_kind, unit_number, absolute_number,
                            title, air_or_release_date, monitored, profile_override_id,
                            required_audio_langs_json, required_sub_langs_json,
                            quality_cutoff_met, language_cutoff_met, created_at
                        ) VALUES (
                            @WorkId, @ContainerId, 'episode', 1, NULL,
                            'Episode 1', NULL, @Monitored, @ProfileOverrideId,
                            '[]', '[]',
                            0, 0, @Now
                        );
                        """,
                        new
                        {
                            WorkId = workId,
                            ContainerId = containerId,
                            Monitored = mon ? 1 : 0,
                            ProfileOverrideId = profileOverrideId,
                            Now = now,
                        },
                        transaction: tx);
                }
            },
            ct);
    }

    private async Task TryAutoSearchMovieAsync(MediaWorkDetailTree tree, LibrarySummary? lib, CancellationToken ct)
    {
        try
        {
            if (tree.Containers.SelectMany(c => c.Units).All(u => u.Files.Count > 0)) return;

            var w = tree.Work;
            var searchOrchestrator = new ReleaseSearchOrchestrator(indexerService, parserRouter, catalogService);
            var scoped = await searchOrchestrator.SearchScopedAsync(
                new OrchestratedReleaseSearchRequest(
                    Query: new IndexerSearchQuery(
                        Query: w.Title,
                        MediaVertical: "movies",
                        Interactive: false,
                        WorkId: w.Id),
                    WorkTree: tree,
                    IncludeSeasonPacks: false),
                ct);

            if (scoped.Count == 0)
            {
                return;
            }

            var units = tree.Containers.SelectMany(c => c.Units).ToList();
            var overrideId = units.Select(u => u.ProfileOverrideId).FirstOrDefault(p => p.HasValue) ?? lib?.DefaultProfileId;
            var profile = overrideId.HasValue ? await profileService.GetProfileByIdAsync(overrideId.Value, ct) : null;
            var reqAudio = units.SelectMany(u => u.RequiredAudioLangs).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var blocklist = await downloadOrchestrator.GetBlocklistSnapshotAsync(ct);

            var best = scoped
                .Where(r => !blocklist.IsBlocked(r.Release))
                .Select(r => (Release: r.Release, Score: ScoreCompatCandidate(r.Release, r.Parsed, reqAudio, w.OriginalLanguage, profile)))
                .Where(x => x.Score > -2000)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Release.SizeBytes)
                .FirstOrDefault();

            if (best.Release is not null)
            {
                await downloadOrchestrator.GrabReleaseAsync(
                    new GrabReleaseRequest(
                        Title: best.Release.Title,
                        Protocol: best.Release.Protocol,
                        MediaVertical: "movies",
                        DownloadUrlOrMagnet: best.Release.MagnetUrl ?? best.Release.DownloadUrl,
                        InfoHash: best.Release.InfoHash,
                        WorkId: w.Id,
                        ContainerId: tree.Containers.FirstOrDefault()?.Id,
                        UnitIds: units.Select(u => u.Id).ToArray(),
                        IndexerId: best.Release.IndexerId,
                        ReleaseGuid: best.Release.Guid,
                        SourceType: "seerr_radarr_compat",
                        IndexerName: best.Release.IndexerName,
                        SizeBytes: best.Release.SizeBytes,
                        PublishedAt: best.Release.PublishedAt),
                    ct);
            }
        }
        catch
        {
            // Best-effort automatic search: never fail Seerr request creation if indexers/downloaders are unreachable
        }
    }

    private async Task TryAutoSearchSeriesAsync(
        MediaWorkDetailTree tree,
        LibrarySummary? lib,
        IReadOnlySet<int> targetSeasons,
        CancellationToken ct,
        IReadOnlySet<long>? targetEpisodeIds = null)
    {
        try
        {
            var w = tree.Work;
            var blocklist = await downloadOrchestrator.GetBlocklistSnapshotAsync(ct);
            var searchOrchestrator = new ReleaseSearchOrchestrator(indexerService, parserRouter, catalogService);

            foreach (var container in tree.Containers.Where(c => targetSeasons.Contains(c.ContainerNumber)))
            {
                var unitsToSearch = container.Units
                    .Where(u => u.Files.Count == 0 && (targetEpisodeIds is not null ? targetEpisodeIds.Contains(u.Id) : u.Monitored))
                    .ToList();
                if (unitsToSearch.Count == 0)
                {
                    continue;
                }

                var seasonNum = container.ContainerNumber;
                var overrideId = container.Units.Select(u => u.ProfileOverrideId).FirstOrDefault(p => p.HasValue) ?? lib?.DefaultProfileId;
                var profile = overrideId.HasValue ? await profileService.GetProfileByIdAsync(overrideId.Value, ct) : null;
                var reqAudio = container.Units.Where(u => u.Monitored).SelectMany(u => u.RequiredAudioLangs).ToHashSet(StringComparer.OrdinalIgnoreCase);

                var seasonScoped = await searchOrchestrator.SearchScopedAsync(
                    new OrchestratedReleaseSearchRequest(
                        Query: new IndexerSearchQuery(
                            Query: w.Title,
                            MediaVertical: w.WorkKind,
                            Season: seasonNum,
                            Interactive: false,
                            WorkId: w.Id),
                        WorkTree: tree,
                        ContainerId: container.Id,
                        IncludeSeasonPacks: true),
                    ct);

                // Prefer full-season pack when searching an entire season (not single-episode targeted)
                if (targetEpisodeIds is null && container.Units.All(u => u.Files.Count == 0))
                {
                    var bestPack = seasonScoped
                        .Where(r => !blocklist.IsBlocked(r.Release))
                        .Where(x => x.EffectiveSeason == seasonNum && x.IsSeasonPack)
                        .Select(x => (x.Release, Score: ScoreCompatCandidate(x.Release, x.Parsed, reqAudio, w.OriginalLanguage, profile) + 250))
                        .Where(x => x.Score >= 0)
                        .OrderByDescending(x => x.Score)
                        .ThenByDescending(x => x.Release.SizeBytes)
                        .FirstOrDefault();

                    if (bestPack.Release is not null)
                    {
                        await downloadOrchestrator.GrabReleaseAsync(
                            new GrabReleaseRequest(
                                Title: bestPack.Release.Title,
                                Protocol: bestPack.Release.Protocol,
                                MediaVertical: w.WorkKind,
                                DownloadUrlOrMagnet: bestPack.Release.MagnetUrl ?? bestPack.Release.DownloadUrl,
                                InfoHash: bestPack.Release.InfoHash,
                                WorkId: w.Id,
                                ContainerId: container.Id,
                                UnitIds: container.Units.Select(u => u.Id).ToArray(),
                                IndexerId: bestPack.Release.IndexerId,
                                ReleaseGuid: bestPack.Release.Guid,
                                SourceType: "seerr_sonarr_compat",
                                IndexerName: bestPack.Release.IndexerName,
                                SizeBytes: bestPack.Release.SizeBytes,
                                PublishedAt: bestPack.Release.PublishedAt),
                            ct);
                        continue;
                    }
                }

                var grabbedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var unit in unitsToSearch)
                {
                    var epMatches = seasonScoped
                        .Where(r => !blocklist.IsBlocked(r.Release))
                        .Where(x => (!x.IsSeasonPack && x.EffectiveSeason == seasonNum && x.EffectiveEpisodes.Contains(unit.UnitNumber)) ||
                                    (!x.EffectiveSeason.HasValue && unit.AbsoluteNumber is { } abs && x.Parsed.AbsoluteEpisodeNumbers.Contains(abs)))
                        .Select(x => (x.Release, x.Parsed))
                        .ToList();

                    if (epMatches.Count == 0)
                    {
                        var epFetched = await searchOrchestrator.SearchScopedAsync(
                            new OrchestratedReleaseSearchRequest(
                                Query: new IndexerSearchQuery(
                                    Query: w.Title,
                                    MediaVertical: w.WorkKind,
                                    Season: seasonNum,
                                    Episode: unit.UnitNumber,
                                    Interactive: false,
                                    WorkId: w.Id,
                                    UnitId: unit.Id),
                                WorkTree: tree,
                                ContainerId: container.Id,
                                UnitId: unit.Id,
                                AbsoluteEpisodeNumber: unit.AbsoluteNumber,
                                IncludeSeasonPacks: false),
                            ct);

                        epMatches = epFetched
                            .Where(r => !blocklist.IsBlocked(r.Release))
                            .Where(x => (!x.IsSeasonPack && x.EffectiveSeason == seasonNum && x.EffectiveEpisodes.Contains(unit.UnitNumber)) ||
                                        (!x.EffectiveSeason.HasValue && unit.AbsoluteNumber is { } abs && x.Parsed.AbsoluteEpisodeNumbers.Contains(abs)))
                            .Select(x => (x.Release, x.Parsed))
                            .ToList();
                    }

                    var bestEp = epMatches
                        .Select(x => (x.Release, Score: ScoreCompatCandidate(x.Release, x.Parsed, reqAudio, w.OriginalLanguage, profile)))
                        .Where(x => x.Score > -2000)
                        .OrderByDescending(x => x.Score)
                        .ThenByDescending(x => x.Release.SizeBytes)
                        .FirstOrDefault();

                    if (bestEp.Release is not null)
                    {
                        var dlUrl = bestEp.Release.MagnetUrl ?? bestEp.Release.DownloadUrl;
                        if (grabbedUrls.Add(dlUrl))
                        {
                            await downloadOrchestrator.GrabReleaseAsync(
                                new GrabReleaseRequest(
                                    Title: bestEp.Release.Title,
                                    Protocol: bestEp.Release.Protocol,
                                    MediaVertical: w.WorkKind,
                                    DownloadUrlOrMagnet: dlUrl,
                                    InfoHash: bestEp.Release.InfoHash,
                                    WorkId: w.Id,
                                    ContainerId: container.Id,
                                    UnitIds: [unit.Id],
                                    IndexerId: bestEp.Release.IndexerId,
                                    ReleaseGuid: bestEp.Release.Guid,
                                    SourceType: "seerr_sonarr_compat",
                                    IndexerName: bestEp.Release.IndexerName,
                                    SizeBytes: bestEp.Release.SizeBytes,
                                    PublishedAt: bestEp.Release.PublishedAt),
                                ct);
                        }
                    }
                }
            }
        }
        catch
        {
            // Best-effort automatic search
        }
    }

    private static int ScoreCompatCandidate(
        IndexedReleaseCandidate release,
        ParsedReleaseInfo parsed,
        HashSet<string> reqAudio,
        string? originalLanguage,
        QualityProfileSummary? profile)
    {
        if (release.Title.Contains("FAKE", StringComparison.OrdinalIgnoreCase) ||
            release.Title.Contains("INCOMPLETE", StringComparison.OrdinalIgnoreCase))
        {
            return -5000;
        }

        var origLang = string.IsNullOrWhiteSpace(originalLanguage) ? "en" : originalLanguage.Trim().ToLowerInvariant();
        var canonicalQuality = parsed.ToQualityName();

        var candAudio = parsed.DetectedLanguages.Select(l => l.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (release.IndexerLanguages is { Count: > 0 })
        {
            foreach (var l in release.IndexerLanguages)
            {
                if (!string.IsNullOrWhiteSpace(l)) candAudio.Add(l.Trim().ToLowerInvariant());
            }
        }
        if (candAudio.Count == 0)
        {
            candAudio.Add(origLang);
        }

        var score = 0.0;
        if (profile is not null)
        {
            var mq = profile.AllowedQualities.FirstOrDefault(q => string.Equals(q.Name, canonicalQuality, StringComparison.OrdinalIgnoreCase));
            score += mq is not null ? mq.Score * profile.WeightQuality : -1500;

            if (profile.LanguagePolicy is { Tiers.Count: > 0 } lp)
            {
                LanguagePolicyTier? tierHit = null;
                foreach (var tier in lp.Tiers.OrderBy(t => t.Tier))
                {
                    bool ResolveTok(string tok) => candAudio.Contains(
                        tok.Equals("original", StringComparison.OrdinalIgnoreCase) || tok.Equals("orig", StringComparison.OrdinalIgnoreCase)
                            ? origLang
                            : tok.ToLowerInvariant());

                    if ((tier.AudioContainsAll.Count == 0 || tier.AudioContainsAll.All(ResolveTok)) &&
                        (tier.AudioContainsAny.Count == 0 || tier.AudioContainsAny.Any(ResolveTok)))
                    {
                        tierHit = tier;
                        break;
                    }
                }
                score += tierHit is not null ? tierHit.Score * profile.WeightLanguage : -1200;
            }
        }
        else
        {
            score += (parsed.Resolution ?? "1080p") == "2160p" ? 900 : 700;
        }

        if (reqAudio.Count > 0 && reqAudio.All(candAudio.Contains))
        {
            score += 1200;
        }
        else if (reqAudio.Count > 0 && reqAudio.Any(candAudio.Contains))
        {
            score += 400;
        }

        return (int)Math.Round(score);
    }

    private static MediaWorkSummary? FindExistingSeriesByLookupTerm(IReadOnlyList<MediaWorkSummary> works, string cleanTerm)
    {
        if (cleanTerm.StartsWith("tvdb:", StringComparison.OrdinalIgnoreCase) ||
            cleanTerm.StartsWith("tvdbid:", StringComparison.OrdinalIgnoreCase))
        {
            var id = cleanTerm[(cleanTerm.IndexOf(':') + 1)..].Trim();
            return works.FirstOrDefault(w => string.Equals(w.ExternalIds.GetValueOrDefault("tvdb"), id, StringComparison.OrdinalIgnoreCase));
        }

        if (cleanTerm.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase) ||
            cleanTerm.StartsWith("tmdbid:", StringComparison.OrdinalIgnoreCase))
        {
            var id = cleanTerm[(cleanTerm.IndexOf(':') + 1)..].Trim();
            return works.FirstOrDefault(w => string.Equals(w.ExternalIds.GetValueOrDefault("tmdb"), id, StringComparison.OrdinalIgnoreCase));
        }

        if (cleanTerm.StartsWith("imdb:", StringComparison.OrdinalIgnoreCase) ||
            (cleanTerm.StartsWith("tt", StringComparison.OrdinalIgnoreCase) && cleanTerm.Length >= 8))
        {
            var id = cleanTerm.Contains(':') ? cleanTerm[(cleanTerm.IndexOf(':') + 1)..].Trim() : cleanTerm;
            return works.FirstOrDefault(w => string.Equals(w.ExternalIds.GetValueOrDefault("imdb"), id, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static MediaWorkSummary? FindExistingMovieByLookupTerm(IReadOnlyList<MediaWorkSummary> works, string cleanTerm)
    {
        if (cleanTerm.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase) ||
            cleanTerm.StartsWith("tmdbid:", StringComparison.OrdinalIgnoreCase))
        {
            var id = cleanTerm[(cleanTerm.IndexOf(':') + 1)..].Trim();
            return works.FirstOrDefault(w => string.Equals(w.ExternalIds.GetValueOrDefault("tmdb"), id, StringComparison.OrdinalIgnoreCase));
        }

        if (cleanTerm.StartsWith("imdb:", StringComparison.OrdinalIgnoreCase) ||
            (cleanTerm.StartsWith("tt", StringComparison.OrdinalIgnoreCase) && cleanTerm.Length >= 8))
        {
            var id = cleanTerm.Contains(':') ? cleanTerm[(cleanTerm.IndexOf(':') + 1)..].Trim() : cleanTerm;
            return works.FirstOrDefault(w => string.Equals(w.ExternalIds.GetValueOrDefault("imdb"), id, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static MediaWorkSummary? FindMatchingCatalogWork(
        IReadOnlyList<MediaWorkSummary> works,
        IReadOnlyDictionary<string, string> extIds,
        string? titleFallback)
    {
        foreach (var key in new[] { "tvdb", "tmdb", "imdb" })
        {
            if (extIds.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val))
            {
                var hit = works.FirstOrDefault(w => string.Equals(w.ExternalIds.GetValueOrDefault(key), val, StringComparison.OrdinalIgnoreCase));
                if (hit is not null)
                {
                    return hit;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(titleFallback))
        {
            return works.FirstOrDefault(w => string.Equals(w.Title, titleFallback, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static List<string> ResolveTagLabels(IReadOnlyList<int> tagIds, IReadOnlyList<CompatTagDto> allTags) =>
        tagIds
            .Select(id => allTags.FirstOrDefault(t => t.Id == id)?.Label)
            .Where(lbl => !string.IsNullOrWhiteSpace(lbl))
            .Select(lbl => lbl!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int[] ResolveTagIds(IReadOnlyList<string> workTagLabels, IReadOnlyList<CompatTagDto> allTags) =>
        workTagLabels
            .Select(lbl => allTags.FirstOrDefault(t => t.Label.Equals(lbl, StringComparison.OrdinalIgnoreCase))?.Id ?? 0)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();

    private static List<(int SeasonNumber, bool Monitored)> ExtractSonarrSeasonSpecs(JsonNode? node)
    {
        var list = new List<(int SeasonNumber, bool Monitored)>();
        if (node is JsonArray arr)
        {
            foreach (var item in arr.OfType<JsonObject>())
            {
                var sn = item["seasonNumber"]?.GetValue<int?>();
                if (sn.HasValue)
                {
                    var mon = item["monitored"]?.GetValue<bool?>() ?? (sn.Value > 0);
                    list.Add((sn.Value, mon));
                }
            }
        }
        return list;
    }

    private static string? ExtractPosterUrlFromJson(JsonObject body)
    {
        var direct = body["remotePoster"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (body["images"] is JsonArray imgs)
        {
            foreach (var img in imgs.OfType<JsonObject>())
            {
                var coverType = img["coverType"]?.GetValue<string>();
                if (string.Equals(coverType, "poster", StringComparison.OrdinalIgnoreCase))
                {
                    return img["remoteUrl"]?.GetValue<string>() ?? img["url"]?.GetValue<string>();
                }
            }
        }

        return null;
    }

    private static object[] BuildCoverImages(string? posterUrl) =>
        string.IsNullOrWhiteSpace(posterUrl)
            ? []
            :
            [
                new
                {
                    coverType = "poster",
                    url = posterUrl,
                    remoteUrl = posterUrl,
                },
            ];

    private static string BuildTitleSlug(string title, int externalId)
    {
        var slug = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "item";
        }
        return externalId > 0 ? $"{slug}-{externalId}" : slug;
    }

    private static string NormalizeTitleKey(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? ""
            : Regex.Replace(raw.ToLowerInvariant(), @"[^a-z0-9]+", "");

    private static string MapSonarrMonitorMode(string? monitorOption, bool monitored)
    {
        if (!monitored)
        {
            return "none";
        }

        return (monitorOption ?? "all").Trim().ToLowerInvariant() switch
        {
            "none" => "none",
            "future" => "future",
            "firstseason" => "firstseason",
            "lastseason" or "latestseason" => "latestseason",
            _ => "all",
        };
    }

    private static (long FreeSpace, long TotalSpace, bool Accessible) ProbeDiskSpace(string path)
    {
        try
        {
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
            // Fall through to safe defaults on virtual/container mounts
        }

        return (500_000_000_000L, 1_000_000_000_000L, Directory.Exists(path));
    }

    private static bool MatchesVerticalGroup(string mediaType, string group) =>
        group switch
        {
            "sonarr" => mediaType is "tv" or "anime",
            "radarr" => mediaType == "movies",
            "lidarr" => mediaType == "music",
            "readarr" => mediaType is "ebooks" or "audiobooks" or "comics",
            _ => true,
        };

    private static bool MatchesProfileVerticalGroup(string mediaVertical, string group) =>
        group switch
        {
            "sonarr" => mediaVertical is "video" or "tv" or "anime",
            "radarr" => mediaVertical is "video" or "movies",
            "lidarr" => mediaVertical == "music",
            "readarr" => mediaVertical is "ebooks" or "audiobooks" or "comics",
            _ => true,
        };

    private static List<int> ExtractIntList(JsonNode? node)
    {
        var list = new List<int>();
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonValue jv && jv.TryGetValue<int>(out var v))
                {
                    list.Add(v);
                }
            }
        }
        return list;
    }

    private static List<long> ExtractLongList(JsonNode? node)
    {
        var list = new List<long>();
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonValue jv)
                {
                    if (jv.TryGetValue<long>(out var lv))
                    {
                        list.Add(lv);
                    }
                    else if (jv.TryGetValue<int>(out var iv))
                    {
                        list.Add(iv);
                    }
                }
            }
        }
        return list;
    }

    private static string ExtractAuthorFromOverview(string overview) =>
        overview.StartsWith("Author: ", StringComparison.OrdinalIgnoreCase)
            ? overview["Author: ".Length..].Trim()
            : "Unknown Author";
}
