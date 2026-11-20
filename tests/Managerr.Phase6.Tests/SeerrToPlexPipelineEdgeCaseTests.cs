using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Managerr.Catalog;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
using Managerr.Host.Services;
using Managerr.Indexers;
using Managerr.Indexers.Torznab;
using Managerr.Metadata;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Managerr.Verification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Managerr.Phase6.Tests;

public sealed class SeerrToPlexPipelineEdgeCaseTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task E2E_01_Movie_SeerrRequest_UserLanguagePromotion_HardlinkWithSrtSidecar_TargetedPlexRefresh_AndSeerrAvailability()
    {
        await using var rig = await PipelineTestRig.StartAsync(useHardlinksGlobally: true);

        // 1. Create Seerr user tag for Plex user 101 ("germanviewer" with preferred audio "de")
        var tagResp = await rig.SendRadarrAsync("POST", "/tag", body: new { label = "101 - germanviewer" });
        Assert.Equal(201, tagResp.Status);
        var tagId = tagResp.Body?["id"]?.GetValue<int>() ?? 0;
        Assert.True(tagId > 0);

        // Configure indexer candidate for auto-search
        var releaseTitle = "Dune.Part.Two.2024.German.DL.1080p.BluRay.x264-GRP";
        rig.Indexers.Candidates.Add(new IndexedReleaseCandidate(
            Guid: "dune-german-dl-guid",
            IndexerId: 1,
            IndexerName: "UsenetLab",
            Protocol: "usenet",
            MediaVertical: "movies",
            Title: releaseTitle,
            DownloadUrl: "https://usenetlab.invalid/get/dune-dl",
            MagnetUrl: null,
            InfoUrl: null,
            InfoHash: "nzo:dune-german-dl",
            SizeBytes: 8_192,
            Seeders: null,
            Peers: null,
            DownloadVolumeFactor: 1.0,
            UploadVolumeFactor: 1.0,
            PublishedAt: DateTimeOffset.UtcNow,
            Categories: [2000],
            ImdbId: "tt15239678",
            TmdbId: 693134,
            TvdbId: null));

        // 2. Lookup movie and POST /compat/radarr/api/v3/movie with searchForMovie = true
        var moviePost = await rig.SendRadarrAsync("POST", "/movie", body: new
        {
            title = "Dune: Part Two",
            tmdbId = 693134,
            year = 2024,
            monitored = true,
            rootFolderPath = rig.MoviesRoot,
            tags = new[] { tagId },
            addOptions = new { searchForMovie = true }
        });
        Assert.Equal(201, moviePost.Status);
        var movieId = moviePost.Body?["id"]?.GetValue<long>() ?? 0;
        Assert.True(movieId > 0);

        // Verify release was grabbed and enqueued
        var enqueued = Assert.Single(rig.Gateway.EnqueuedGrabs);
        Assert.Equal(releaseTitle, enqueued.Title);

        // 3. Downloader finishes: stage movie .mkv + companion .de.srt in SABnzbd complete folder
        var jobDir = Path.Combine(rig.TempDir, "staging", "sabnzbd", releaseTitle);
        Directory.CreateDirectory(jobDir);
        var sourceMkv = Path.Combine(jobDir, $"{releaseTitle}.mkv");
        var sourceSrt = Path.Combine(jobDir, $"{releaseTitle}.de.srt");
        await File.WriteAllBytesAsync(sourceMkv, new byte[8192]);
        await File.WriteAllTextAsync(sourceSrt, "1\n00:00:01,000 --> 00:00:05,000\nHallo Wüste.");

        var queueItem = Assert.Single(rig.Gateway.QueueItems);
        rig.Gateway.QueueItems[0] = queueItem with
        {
            Status = "Completed",
            OutputPath = jobDir,
            ContainedFiles = [sourceMkv, sourceSrt],
            RemainingBytes = 0
        };

        // 4. Configure probe to confirm German DL
        rig.ContainerProbe.CustomProbe = _ => new ContainerProbeResult(
            IntegrityPassed: true,
            IntegrityFailureReason: null,
            ContainerFormat: "matroska",
            DurationSeconds: 120,
            Streams:
            [
                new ProbedRawStream(0, "audio", "eac3", 6, "de", "German DD+ 5.1", AudioWaveformPreview: [0.1f, 0.2f, -0.3f, 0.4f]),
                new ProbedRawStream(1, "audio", "eac3", 6, "en", "English DD+ 5.1", AudioWaveformPreview: [-0.2f, 0.3f, 0.1f, -0.4f]),
            ]);

        // Process completed downloads
        var report = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(1, report.ImportedCount);
        var imported = Assert.Single(report.Items);
        Assert.Equal(VerificationVerdicts.VerifiedPass, imported.Verdict);
        Assert.True(imported.Hardlinked);

        // Destination files exist and were hardlinked
        var destMkv = imported.DestinationFilePath!;
        var destDir = Path.GetDirectoryName(destMkv)!;
        var destSrt = Path.Combine(destDir, Path.GetFileNameWithoutExtension(destMkv) + ".de.srt");
        Assert.True(File.Exists(destMkv), $"Destination MKV missing: {destMkv}");
        Assert.True(File.Exists(destSrt), $"Destination SRT sidecar missing: {destSrt}");

        // Plex refresh was triggered targeting Section 1 (Movies)
        var plexRefresh = Assert.Single(rig.MockPlex.Requests, r => r.Path.Contains("/library/sections/1/refresh"));
        var unescapedQuery = Uri.UnescapeDataString(plexRefresh.Query);
        Assert.Contains("Dune", unescapedQuery);
        Assert.Contains("2024", unescapedQuery);

        // Queue is cleared in compat API
        var radarrQueue = await rig.SendRadarrAsync("GET", "/queue");
        Assert.Equal(200, radarrQueue.Status);
        var qArray = PipelineTestRig.GetQueueRecords(radarrQueue);
        Assert.Empty(qArray);

        // Movie in Radarr compat flips to hasFile: true, movieFileId > 0, sizeOnDisk > 0
        var movieGet = await rig.SendRadarrAsync("GET", $"/movie/{movieId}");
        Assert.Equal(200, movieGet.Status);
        Assert.True(movieGet.Body?["hasFile"]?.GetValue<bool>());
        Assert.True(movieGet.Body?["movieFileId"]?.GetValue<long>() > 0);
        Assert.True(movieGet.Body?["sizeOnDisk"]?.GetValue<long>() > 0);
    }

    [Fact]
    public async Task E2E_02_TvCombinedMultiEpisode_S01E01E02_QueueShowsBothEpisodes_AndImportMarksBothAvailableForSeerr()
    {
        await using var rig = await PipelineTestRig.StartAsync();

        // 1. Add Severance Season 1 via Seerr
        var seriesPost = await rig.SendSonarrAsync("POST", "/series", body: new
        {
            title = "Severance",
            tvdbId = PipelineTestRig.SeveranceTvdbId,
            seriesType = "standard",
            monitored = true,
            rootFolderPath = rig.TvRoot,
            seasons = new[] { new { seasonNumber = 1, monitored = true }, new { seasonNumber = 2, monitored = false } }
        });
        Assert.Equal(201, seriesPost.Status);
        var seriesId = seriesPost.Body?["id"]?.GetValue<long>() ?? 0;

        // 2. Enqueue multi-episode download: Show.S01E01E02.1080p.WEB-DL.mkv
        var releaseTitle = "Severance.S01E01E02.1080p.WEB-DL";
        var stagingDir = Path.Combine(rig.TempDir, "staging", releaseTitle);
        Directory.CreateDirectory(stagingDir);
        var mediaFile = Path.Combine(stagingDir, $"{releaseTitle}.mkv");
        await File.WriteAllBytesAsync(mediaFile, new byte[8192]);

        var qItem = new ActiveDownloadQueueItem(
            ClientId: rig.SabClient.Id,
            ClientName: rig.SabClient.Name,
            Protocol: "usenet",
            ExternalId: "nzo-sev-multiep",
            InfoHash: "nzo:severance-s01e01e02",
            Title: releaseTitle,
            Category: "tv-managerr",
            MediaVertical: "tv",
            Status: "Downloading",
            TotalBytes: 10_000_000,
            RemainingBytes: 5_000_000,
            OutputPath: stagingDir,
            ContainedFiles: [mediaFile],
            IsStalled: false,
            IsMetadataStuck: false,
            ErrorMessage: null);
        rig.Gateway.QueueItems.Add(qItem);

        // Record grab history for correlation
        using (var scope = rig.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var tree = (await catalog.GetWorkTreeAsync(seriesId))!;
            var s1 = tree.Containers.Single(c => c.ContainerNumber == 1);
            var e1 = s1.Units.Single(u => u.UnitNumber == 1);
            var e2 = s1.Units.Single(u => u.UnitNumber == 2);

            await rig.RecordGrabHistoryAsync(
                clientId: rig.SabClient.Id,
                externalDownloadId: "nzo-sev-multiep",
                infoHash: "nzo:severance-s01e01e02",
                releaseTitle: releaseTitle,
                mediaVertical: "tv",
                workId: seriesId,
                unitIds: [e1.Id, e2.Id],
                downloadUrl: "https://indexer.invalid/get/sev-s01e01e02",
                sourceType: "seerr_sonarr_compat");
        }

        // 3. /compat/sonarr/api/v3/queue?includeEpisode=true returns 2 records (E01 and E02)
        var queueResp = await rig.SendSonarrAsync("GET", "/queue", new Dictionary<string, object?> { ["includeEpisode"] = true });
        Assert.Equal(200, queueResp.Status);
        var records = PipelineTestRig.GetQueueRecords(queueResp);
        Assert.Equal(2, records.Count);
        var eps = records.Select(r => r?["episode"]?["episodeNumber"]?.GetValue<int>()).OrderBy(x => x).ToList();
        Assert.Equal([1, 2], eps);

        // 4. Downloader completes
        rig.Gateway.QueueItems[0] = qItem with { Status = "Completed", RemainingBytes = 0 };
        var report = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(1, report.ImportedCount);

        // 5. Query episodes: both E01 and E02 have hasFile == true
        var epResp = await rig.SendSonarrAsync("GET", "/episode", new Dictionary<string, object?> { ["seriesId"] = seriesId });
        Assert.Equal(200, epResp.Status);
        var epList = (epResp.Body as JsonArray)!;
        var ep1 = epList.Single(e => e?["seasonNumber"]?.GetValue<int>() == 1 && e?["episodeNumber"]?.GetValue<int>() == 1);
        var ep2 = epList.Single(e => e?["seasonNumber"]?.GetValue<int>() == 1 && e?["episodeNumber"]?.GetValue<int>() == 2);
        Assert.True(ep1?["hasFile"]?.GetValue<bool>());
        Assert.True(ep2?["hasFile"]?.GetValue<bool>());

        // 6. Series reports episodeFileCount == 2
        var seriesGet = await rig.SendSonarrAsync("GET", $"/series/{seriesId}");
        Assert.Equal(200, seriesGet.Status);
        var s1Stats = (seriesGet.Body?["seasons"] as JsonArray)?
            .Single(s => s?["seasonNumber"]?.GetValue<int>() == 1)?["statistics"];
        Assert.Equal(2, s1Stats?["episodeFileCount"]?.GetValue<int>());
    }

    [Fact]
    public async Task E2E_03_TmdbCollapsedAnime_1x60_Vs_Tvdb_3x24_ExpandsSeasons_AndMapsEpisode25ToSeason2Episode1()
    {
        await using var rig = await PipelineTestRig.StartAsync();

        // 1. Seerr requests Anime 1 (1x60 on TMDB vs 3x24 on TVDB), specifying season 1 monitored
        var seriesPost = await rig.SendSonarrAsync("POST", "/series", body: new
        {
            title = "Bleach: Thousand-Year Blood War",
            seriesType = "anime",
            tvdbId = 1001,
            tmdbId = 2001,
            monitored = true,
            rootFolderPath = rig.AnimeRoot,
            seasons = new[]
            {
                new { seasonNumber = 0, monitored = false },
                new { seasonNumber = 1, monitored = true },
                new { seasonNumber = 2, monitored = false },
                new { seasonNumber = 3, monitored = false }
            }
        });
        Assert.Equal(201, seriesPost.Status);
        var seriesId = seriesPost.Body?["id"]?.GetValue<long>() ?? 0;

        // Verify seasons expansion: S01, S02, S03 are promoted to monitored: true; S00 is unmonitored
        var seasons = (seriesPost.Body?["seasons"] as JsonArray)!;
        var s0 = seasons.Single(s => s?["seasonNumber"]?.GetValue<int>() == 0);
        var s1 = seasons.Single(s => s?["seasonNumber"]?.GetValue<int>() == 1);
        var s2 = seasons.Single(s => s?["seasonNumber"]?.GetValue<int>() == 2);
        var s3 = seasons.Single(s => s?["seasonNumber"]?.GetValue<int>() == 3);
        Assert.False(s0?["monitored"]?.GetValue<bool>());
        Assert.True(s1?["monitored"]?.GetValue<bool>());
        Assert.True(s2?["monitored"]?.GetValue<bool>());
        Assert.True(s3?["monitored"]?.GetValue<bool>());

        // 2. Download completes: [SubsPlease] Bleach Thousand-Year Blood War - S01E25 (1080p).mkv
        var releaseTitle = "[SubsPlease] Bleach Thousand-Year Blood War - S01E25 (1080p)";
        var stagingDir = Path.Combine(rig.TempDir, "staging", releaseTitle);
        Directory.CreateDirectory(stagingDir);
        var mediaFile = Path.Combine(stagingDir, $"{releaseTitle}.mkv");
        await File.WriteAllBytesAsync(mediaFile, new byte[8192]);

        rig.Gateway.QueueItems.Add(new ActiveDownloadQueueItem(
            ClientId: rig.SabClient.Id,
            ClientName: rig.SabClient.Name,
            Protocol: "usenet",
            ExternalId: "nzo-bleach-e25",
            InfoHash: "nzo:bleach-e25",
            Title: releaseTitle,
            Category: "anime-managerr",
            MediaVertical: "anime",
            Status: "Completed",
            TotalBytes: 8192,
            RemainingBytes: 0,
            OutputPath: stagingDir,
            ContainedFiles: [mediaFile],
            IsStalled: false,
            IsMetadataStuck: false,
            ErrorMessage: null));

        var report = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(1, report.ImportedCount);

        // 3. Verify ResolveTargetUnitsStrict mapped it to Season 2 Episode 1 (AbsoluteNumber == 25)
        using (var scope = rig.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var tree = (await catalog.GetWorkTreeAsync(seriesId))!;
            var season2 = tree.Containers.Single(c => c.ContainerNumber == 2);
            var ep1OfS2 = season2.Units.Single(u => u.UnitNumber == 1);
            Assert.Equal(25, ep1OfS2.AbsoluteNumber);
            Assert.Single(ep1OfS2.Files);
        }

        // Season 2 statistics reports episodeFileCount == 1
        var seriesGet = await rig.SendSonarrAsync("GET", $"/series/{seriesId}");
        var s2Stats = (seriesGet.Body?["seasons"] as JsonArray)?
            .Single(s => s?["seasonNumber"]?.GetValue<int>() == 2)?["statistics"];
        Assert.Equal(1, s2Stats?["episodeFileCount"]?.GetValue<int>());
    }

    [Fact]
    public async Task E2E_04_ConcurrentSeerrDuplicateRequests_RaceCondition_DeduplicatesWorkAndPreservesAllTagsAndMonitoredSeasons()
    {
        await using var rig = await PipelineTestRig.StartAsync();
        // Configure 40ms delay in ResolveWorkAsync to expose concurrency races
        rig.Metadata.AsyncLookupDelayMs = 40;

        // 1. Create 6 distinct tags concurrently
        var tagTasks = Enumerable.Range(1, 6)
            .Select(i => rig.SendSonarrAsync("POST", "/tag", body: new { label = $"Tag {i}" }))
            .ToList();
        var tagResults = await Task.WhenAll(tagTasks);
        Assert.All(tagResults, r => Assert.Equal(201, r.Status));
        var tagIds = tagResults.Select(r => r.Body!["id"]!.GetValue<int>()).ToList();

        // 2. Fire 6 concurrent POST /series calls (3 with Tag 1 requesting Season 1, 3 with Tag 2 requesting Season 2)
        var seriesTasks = Enumerable.Range(0, 6).Select(i =>
        {
            var tagIndex = i < 3 ? 0 : 1;
            var seasonReq = i < 3 ? 1 : 2;
            return rig.SendSonarrAsync("POST", "/series", body: new
            {
                title = "Severance",
                tvdbId = PipelineTestRig.SeveranceTvdbId,
                monitored = true,
                rootFolderPath = rig.TvRoot,
                tags = new[] { tagIds[tagIndex] },
                seasons = new[] { new { seasonNumber = seasonReq, monitored = true } }
            });
        }).ToList();

        // 3. Fire 6 concurrent POST /movie calls for Dune: Part Two with Tags 3..6
        var movieTasks = Enumerable.Range(0, 6).Select(i =>
        {
            var tagIndex = 2 + (i % 4);
            return rig.SendRadarrAsync("POST", "/movie", body: new
            {
                title = "Dune: Part Two",
                tmdbId = 693134,
                year = 2024,
                monitored = true,
                rootFolderPath = rig.MoviesRoot,
                tags = new[] { tagIds[tagIndex] }
            });
        }).ToList();

        var allSeriesResults = await Task.WhenAll(seriesTasks);
        var allMovieResults = await Task.WhenAll(movieTasks);

        Assert.All(allSeriesResults, r => Assert.True(r.Status is 200 or 201));
        Assert.All(allMovieResults, r => Assert.True(r.Status is 200 or 201));

        // 4. Assert exactly 1 series and exactly 1 movie in catalog
        using (var scope = rig.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var allWorks = await catalog.ListWorksAsync();
            var seriesWorks = allWorks.Where(w => w.WorkKind is "tv" or "anime").ToList();
            var movieWorks = allWorks.Where(w => w.WorkKind == "movies").ToList();

            Assert.Single(seriesWorks);
            Assert.Single(movieWorks);

            // Series preserved both Tag 1 and Tag 2, and both Season 1 and Season 2 monitored
            var seriesTree = (await catalog.GetWorkTreeAsync(seriesWorks[0].Id))!;
            Assert.Contains("Tag 1", seriesTree.Work.Tags);
            Assert.Contains("Tag 2", seriesTree.Work.Tags);

            var s1 = seriesTree.Containers.Single(c => c.ContainerNumber == 1);
            var s2 = seriesTree.Containers.Single(c => c.ContainerNumber == 2);
            Assert.True(s1.Monitored, "Season 1 must be monitored");
            Assert.True(s2.Monitored, "Season 2 must be monitored");

            // Movie preserved its tags
            var movieTree = (await catalog.GetWorkTreeAsync(movieWorks[0].Id))!;
            Assert.True(movieTree.Work.Tags.Count >= 1);
        }
    }

    [Fact]
    public async Task E2E_05_TransientDownloaderClientFault_MissingServer_DoesNotBlocklistRelease_AndImportsCleanlyAfterRecovery()
    {
        await using var rig = await PipelineTestRig.StartAsync();

        // Add Movie to catalog
        await rig.SendRadarrAsync("POST", "/movie", body: new
        {
            title = "Dune: Part Two",
            tmdbId = 693134,
            year = 2024,
            monitored = true,
            rootFolderPath = rig.MoviesRoot
        });

        var releaseTitle = "Dune.Part.Two.2024.1080p.WEB-DL-GRP";
        var stagingDir = Path.Combine(rig.TempDir, "staging", releaseTitle);
        Directory.CreateDirectory(stagingDir);
        var mediaFile = Path.Combine(stagingDir, $"{releaseTitle}.mkv");
        await File.WriteAllBytesAsync(mediaFile, new byte[8192]);

        // Downloader reports transient client fault
        var faultyItem = new ActiveDownloadQueueItem(
            ClientId: rig.SabClient.Id,
            ClientName: rig.SabClient.Name,
            Protocol: "usenet",
            ExternalId: "nzo-transient-fault",
            InfoHash: "nzo:dune-transient-fault",
            Title: releaseTitle,
            Category: "movies-managerr",
            MediaVertical: "movies",
            Status: "Failed",
            TotalBytes: 8192,
            RemainingBytes: 8192,
            OutputPath: stagingDir,
            ContainedFiles: [mediaFile],
            IsStalled: false,
            IsMetadataStuck: false,
            ErrorMessage: "Missing server: No active Usenet servers configured");
        rig.Gateway.QueueItems.Add(faultyItem);

        // Run CompletedDownloadImportService
        var report1 = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(0, report1.ImportedCount);

        // Assert release is NOT in recurring_blocklist_hashes!
        using (var scope = rig.Services.CreateScope())
        {
            var orch = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var blocklist = await orch.ListBlocklistedHashesAsync();
            Assert.DoesNotContain(blocklist, b => b.InfoHash == "nzo:dune-transient-fault");
        }

        // Compat queue shows warning
        var queueResp = await rig.SendRadarrAsync("GET", "/queue");
        var qRecords = PipelineTestRig.GetQueueRecords(queueResp);
        Assert.Single(qRecords);
        Assert.Equal("warning", qRecords[0]?["trackedDownloadStatus"]?.GetValue<string>());

        // Clear fault: client recovers and completes the same release
        rig.Gateway.QueueItems[0] = faultyItem with
        {
            Status = "Completed",
            RemainingBytes = 0,
            ErrorMessage = null
        };

        // Second poll imports cleanly into Plex
        var report2 = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(1, report2.ImportedCount);
        var outcome = Assert.Single(report2.Items);
        Assert.Equal(VerificationVerdicts.VerifiedPass, outcome.Verdict);

        var movieGet = await rig.SendRadarrAsync("GET", "/movie");
        var movieObj = (movieGet.Body as JsonArray)!.Single();
        Assert.True(movieObj?["hasFile"]?.GetValue<bool>());
    }

    [Fact]
    public async Task E2E_06_PermanentCorruptUnpackOrMissingArticles_BlocklistsBadRelease_AndAutomaticallyRepullsSecondCandidate()
    {
        await using var rig = await PipelineTestRig.StartAsync();

        // Indexer has Candidate A (corrupt) and Candidate B (clean)
        var candA = new IndexedReleaseCandidate(
            Guid: "cand-a-corrupt",
            IndexerId: 1,
            IndexerName: "IndexOne",
            Protocol: "usenet",
            MediaVertical: "movies",
            Title: "Dune.Part.Two.2024.1080p.Corrupt-GRP",
            DownloadUrl: "https://indexer.invalid/get/cand-a",
            MagnetUrl: null,
            InfoUrl: null,
            InfoHash: "nzo:cand-a-hash",
            SizeBytes: 12_000, // larger size gives higher score
            Seeders: null,
            Peers: null,
            DownloadVolumeFactor: 1.0,
            UploadVolumeFactor: 1.0,
            PublishedAt: DateTimeOffset.UtcNow,
            Categories: [2000],
            ImdbId: "tt15239678",
            TmdbId: 693134,
            TvdbId: null);

        var candB = new IndexedReleaseCandidate(
            Guid: "cand-b-clean",
            IndexerId: 1,
            IndexerName: "IndexOne",
            Protocol: "usenet",
            MediaVertical: "movies",
            Title: "Dune.Part.Two.2024.1080p.Clean-GRP",
            DownloadUrl: "https://indexer.invalid/get/cand-b",
            MagnetUrl: null,
            InfoUrl: null,
            InfoHash: "nzo:cand-b-hash",
            SizeBytes: 10_000,
            Seeders: null,
            Peers: null,
            DownloadVolumeFactor: 1.0,
            UploadVolumeFactor: 1.0,
            PublishedAt: DateTimeOffset.UtcNow,
            Categories: [2000],
            ImdbId: "tt15239678",
            TmdbId: 693134,
            TvdbId: null);

        rig.Indexers.Candidates.Add(candA);
        rig.Indexers.Candidates.Add(candB);

        // Add movie to catalog with auto-search enabled -> grabs Candidate A
        await rig.SendRadarrAsync("POST", "/movie", body: new
        {
            title = "Dune: Part Two",
            tmdbId = 693134,
            year = 2024,
            monitored = true,
            rootFolderPath = rig.MoviesRoot,
            addOptions = new { searchForMovie = true }
        });

        // Verify Candidate A was enqueued
        var firstGrab = Assert.Single(rig.Gateway.EnqueuedGrabs);
        Assert.Equal(candA.Title, firstGrab.Title);

        // Candidate A fails permanently: staging directory has _FAILED_ marker
        var failedDir = Path.Combine(rig.TempDir, "staging", "_FAILED_Dune.Part.Two.2024.1080p.Corrupt-GRP");
        Directory.CreateDirectory(failedDir);
        rig.Gateway.QueueItems[0] = rig.Gateway.QueueItems[0] with
        {
            Status = "Completed",
            OutputPath = failedDir,
            ContainedFiles = []
        };

        // Run import -> fails Candidate A, blocklists it, removes from client, and auto-repulls Candidate B!
        var report1 = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(0, report1.ImportedCount);

        using (var scope = rig.Services.CreateScope())
        {
            var orch = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var blocklist = await orch.ListBlocklistedHashesAsync();
            Assert.Contains(blocklist, b => b.ReleaseGuid == "cand-a-corrupt" || b.Title.Contains("Corrupt-GRP"));
        }

        Assert.Contains(firstGrab.ExternalId, rig.Gateway.RemovedExternalIds);

        // Assert Candidate B was automatically grabbed as replacement!
        Assert.Equal(2, rig.Gateway.EnqueuedGrabs.Count);
        var secondGrab = rig.Gateway.EnqueuedGrabs[1];
        Assert.Equal(candB.Title, secondGrab.Title);

        // Complete Candidate B
        var cleanDir = Path.Combine(rig.TempDir, "staging", candB.Title);
        Directory.CreateDirectory(cleanDir);
        var cleanMkv = Path.Combine(cleanDir, $"{candB.Title}.mkv");
        await File.WriteAllBytesAsync(cleanMkv, new byte[8192]);

        var candBItem = rig.Gateway.QueueItems.Single(q => q.ExternalId == secondGrab.ExternalId);
        var itemIdx = rig.Gateway.QueueItems.IndexOf(candBItem);
        rig.Gateway.QueueItems[itemIdx] = candBItem with
        {
            Status = "Completed",
            OutputPath = cleanDir,
            ContainedFiles = [cleanMkv],
            RemainingBytes = 0
        };

        // Second poll imports Candidate B into Plex
        var report2 = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(1, report2.ImportedCount);
        var outcome = Assert.Single(report2.Items);
        Assert.Equal(candB.Title, outcome.Title);
        Assert.Equal(VerificationVerdicts.VerifiedPass, outcome.Verdict);
    }

    [Fact]
    public async Task E2E_07_FakeDuplicateDub_VerifiedFail_BlocklistsRelease_LeavesLibraryClean_AndAllowsRepull()
    {
        await using var rig = await PipelineTestRig.StartAsync();

        await rig.SendSonarrAsync("POST", "/series", body: new
        {
            title = "Severance",
            tvdbId = PipelineTestRig.SeveranceTvdbId,
            monitored = true,
            rootFolderPath = rig.TvRoot,
            seasons = new[] { new { seasonNumber = 1, monitored = true } }
        });

        var releaseTitle = "Severance.S01E01.German.DL.1080p.WEB.mkv";
        var stagingDir = Path.Combine(rig.TempDir, "staging", "sev-fake-dub");
        Directory.CreateDirectory(stagingDir);
        var mediaFile = Path.Combine(stagingDir, releaseTitle);
        await File.WriteAllBytesAsync(mediaFile, new byte[8192]);

        rig.Gateway.QueueItems.Add(new ActiveDownloadQueueItem(
            ClientId: rig.SabClient.Id,
            ClientName: rig.SabClient.Name,
            Protocol: "usenet",
            ExternalId: "nzo-sev-fake-dub",
            InfoHash: "nzo:sev-fake-dub-hash",
            Title: releaseTitle,
            Category: "tv-managerr",
            MediaVertical: "tv",
            Status: "Completed",
            TotalBytes: 8192,
            RemainingBytes: 0,
            OutputPath: stagingDir,
            ContainedFiles: [mediaFile],
            IsStalled: false,
            IsMetadataStuck: false,
            ErrorMessage: null));

        // Two audio tracks with identical acoustic waveforms (Pearson correlation >= 0.97) -> triggers VerifiedFail
        var pcm = new float[] { 0.1f, -0.2f, 0.3f, -0.4f, 0.5f, -0.6f, 0.7f, -0.8f };
        rig.ContainerProbe.CustomProbe = _ => new ContainerProbeResult(
            IntegrityPassed: true,
            IntegrityFailureReason: null,
            ContainerFormat: "matroska",
            DurationSeconds: 120,
            Streams:
            [
                new ProbedRawStream(0, "audio", "eac3", 6, "en", "English", AudioWaveformPreview: pcm),
                new ProbedRawStream(1, "audio", "eac3", 6, "de", "German", AudioWaveformPreview: pcm)
            ]);

        var report = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(0, report.ImportedCount);
        Assert.Equal(1, report.RejectedCount);
        var outcome = Assert.Single(report.Items);
        Assert.Equal(VerificationVerdicts.VerifiedFail, outcome.Verdict);

        // Library root is clean: no files written
        var libraryFiles = Directory.EnumerateFiles(rig.TvRoot, "*", SearchOption.AllDirectories).ToList();
        Assert.Empty(libraryFiles);

        // Release hash is added to recurring_blocklist_hashes
        using (var scope = rig.Services.CreateScope())
        {
            var orch = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var blocklist = await orch.ListBlocklistedHashesAsync();
            Assert.Contains(blocklist, b => b.InfoHash == "nzo:sev-fake-dub-hash");
        }

        // Removed from client
        Assert.Contains("nzo-sev-fake-dub", rig.Gateway.RemovedExternalIds);
    }

    [Fact]
    public async Task E2E_08_AmbiguousUndAudio_QuarantineReview_SurfacesImportBlockedInSeerrQueue_ThenApproveImportsToPlex()
    {
        await using var rig = await PipelineTestRig.StartAsync();

        // 1. Add Dune movie with German language requirement
        var moviePost = await rig.SendRadarrAsync("POST", "/movie", body: new
        {
            title = "Dune: Part Two",
            tmdbId = 693134,
            year = 2024,
            monitored = true,
            rootFolderPath = rig.MoviesRoot
        });
        var movieId = moviePost.Body!["id"]!.GetValue<long>();

        // Set unit language requirement to "de"
        using (var scope = rig.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var tree = (await catalog.GetWorkTreeAsync(movieId))!;
            var unit = tree.Containers[0].Units[0];
            await catalog.UpdateUnitAsync(unit.Id, new UpdateMediaUnitRequest(RequiredAudioLangs: ["de"]));
        }

        // 2. Download completes with ambiguous 'und' audio stream
        var releaseTitle = "Dune.Part.Two.2024.1080p.WEB-DL-AMBIGUOUS";
        var stagingDir = Path.Combine(rig.TempDir, "staging", releaseTitle);
        Directory.CreateDirectory(stagingDir);
        var mediaFile = Path.Combine(stagingDir, $"{releaseTitle}.mkv");
        await File.WriteAllBytesAsync(mediaFile, new byte[8192]);

        rig.Gateway.QueueItems.Add(new ActiveDownloadQueueItem(
            ClientId: rig.SabClient.Id,
            ClientName: rig.SabClient.Name,
            Protocol: "usenet",
            ExternalId: "nzo-dune-ambiguous",
            InfoHash: "nzo:dune-ambiguous-hash",
            Title: releaseTitle,
            Category: "movies-managerr",
            MediaVertical: "movies",
            Status: "Completed",
            TotalBytes: 8192,
            RemainingBytes: 0,
            OutputPath: stagingDir,
            ContainedFiles: [mediaFile],
            IsStalled: false,
            IsMetadataStuck: false,
            ErrorMessage: null));

        rig.ContainerProbe.CustomProbe = _ => new ContainerProbeResult(
            IntegrityPassed: true,
            IntegrityFailureReason: null,
            ContainerFormat: "matroska",
            DurationSeconds: 120,
            Streams: [new ProbedRawStream(0, "audio", "eac3", 6, "und", "Unknown Track", AudioWaveformPreview: null)]);

        // 3. Process completed downloads -> lands in QuarantineReview
        var report1 = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(0, report1.ImportedCount);
        Assert.Equal(1, report1.QuarantinedCount);
        var qItem = Assert.Single(report1.Items);
        Assert.Equal(VerificationVerdicts.QuarantineReview, qItem.Verdict);
        var quarantineId = qItem.QuarantineId!.Value;

        // Radarr compat queue returns trackedDownloadState == "importBlocked", trackedDownloadStatus == "warning", non-empty statusMessages
        var queueResp = await rig.SendRadarrAsync("GET", "/queue");
        var queueRecords = PipelineTestRig.GetQueueRecords(queueResp);
        Assert.Single(queueRecords);
        var qr = queueRecords[0]!;
        Assert.Equal("importBlocked", qr["trackedDownloadState"]?.GetValue<string>());
        Assert.Equal("warning", qr["trackedDownloadStatus"]?.GetValue<string>());
        var statusMsgs = (qr["statusMessages"] as JsonArray)!;
        Assert.NotEmpty(statusMsgs);

        // hasFile is still false
        var movieGet1 = await rig.SendRadarrAsync("GET", $"/movie/{movieId}");
        Assert.False(movieGet1.Body?["hasFile"]?.GetValue<bool>());

        // 4a. Unrecognized language typo is rejected with 400 Bad Request instead of stamping 'und'
        var badLangResp = await rig.Client.PostAsJsonAsync($"/api/v1/quarantine/{quarantineId}/approve?apikey={rig.ApiKey}", new
        {
            confirmedLanguage = "germa-invalid"
        });
        Assert.Equal(HttpStatusCode.BadRequest, badLangResp.StatusCode);

        // 4b. Operator calls POST /api/v1/quarantine/{id}/approve (confirmedLanguage: "de")
        var approveResp = await rig.Client.PostAsJsonAsync($"/api/v1/quarantine/{quarantineId}/approve?apikey={rig.ApiKey}", new
        {
            confirmedLanguage = "de"
        });
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);

        // Rejecting an already-approved quarantine item returns 409 Conflict
        var rejectAfterApproveResp = await rig.Client.PostAsync($"/api/v1/quarantine/{quarantineId}/reject?apikey={rig.ApiKey}", null);
        Assert.Equal(HttpStatusCode.Conflict, rejectAfterApproveResp.StatusCode);

        // 5. Next poll imports cleanly to Plex!
        var report2 = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(1, report2.ImportedCount);

        // Plex refreshed
        Assert.Contains(rig.MockPlex.Requests, r => r.Path.Contains("/library/sections/1/refresh"));

        // Queue is cleared
        var queueResp2 = await rig.SendRadarrAsync("GET", "/queue");
        var qArr2 = PipelineTestRig.GetQueueRecords(queueResp2);
        Assert.Empty(qArr2);

        // hasFile is now true
        var movieGet2 = await rig.SendRadarrAsync("GET", $"/movie/{movieId}");
        Assert.True(movieGet2.Body?["hasFile"]?.GetValue<bool>());
    }

    [Fact]
    public async Task E2E_09_QuarantineReject_PreservesExistingLibraryFile_BlocklistsCandidate_AndMarksTrackedFailed()
    {
        await using var rig = await PipelineTestRig.StartAsync();

        // 1. Add movie and attach existing 1080p file on disk
        var moviePost = await rig.SendRadarrAsync("POST", "/movie", body: new
        {
            title = "Dune: Part Two",
            tmdbId = 693134,
            year = 2024,
            monitored = true,
            rootFolderPath = rig.MoviesRoot
        });
        var movieId = moviePost.Body!["id"]!.GetValue<long>();

        var existingFileDir = Path.Combine(rig.MoviesRoot, "Dune: Part Two");
        Directory.CreateDirectory(existingFileDir);
        var existingFilePath = Path.Combine(existingFileDir, "Dune.Part.Two.2024.1080p.WEB-DL.mkv");
        await File.WriteAllBytesAsync(existingFilePath, new byte[8192]);

        using (var scope = rig.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var tree = (await catalog.GetWorkTreeAsync(movieId))!;
            await catalog.AttachFileAsync(new AttachMediaFileRequest(
                UnitId: tree.Containers[0].Units[0].Id,
                RelativePath: Path.GetRelativePath(rig.MoviesRoot, existingFilePath),
                SizeBytes: 8192,
                QualityName: "WEBDL-1080p",
                DetectedAudioLangs: ["en"],
                VerifiedStatus: VerificationVerdicts.VerifiedPass));
            await catalog.UpdateUnitAsync(tree.Containers[0].Units[0].Id, new UpdateMediaUnitRequest(RequiredAudioLangs: ["de"]));
        }

        // 2. Upgrade candidate arrives and lands in QuarantineReview
        var releaseTitle = "Dune.Part.Two.2024.2160p.WEB-DL.AMBIGUOUS";
        var stagingDir = Path.Combine(rig.TempDir, "staging", releaseTitle);
        Directory.CreateDirectory(stagingDir);
        var mediaFile = Path.Combine(stagingDir, $"{releaseTitle}.mkv");
        await File.WriteAllBytesAsync(mediaFile, new byte[16384]);

        rig.Gateway.QueueItems.Add(new ActiveDownloadQueueItem(
            ClientId: rig.SabClient.Id,
            ClientName: rig.SabClient.Name,
            Protocol: "usenet",
            ExternalId: "nzo-dune-upgrade-amb",
            InfoHash: "nzo:dune-upgrade-amb-hash",
            Title: releaseTitle,
            Category: "movies-managerr",
            MediaVertical: "movies",
            Status: "Completed",
            TotalBytes: 16384,
            RemainingBytes: 0,
            OutputPath: stagingDir,
            ContainedFiles: [mediaFile],
            IsStalled: false,
            IsMetadataStuck: false,
            ErrorMessage: null));

        rig.ContainerProbe.CustomProbe = _ => new ContainerProbeResult(
            IntegrityPassed: true,
            IntegrityFailureReason: null,
            ContainerFormat: "matroska",
            DurationSeconds: 120,
            Streams: [new ProbedRawStream(0, "audio", "eac3", 6, "und", "Unknown Track", AudioWaveformPreview: null)]);

        var report1 = await rig.ProcessCompletedDownloadsAsync();
        var qId = Assert.Single(report1.Items).QuarantineId!.Value;

        // Original file on disk is untouched
        Assert.True(File.Exists(existingFilePath));

        // 3. Operator rejects quarantine
        var rejectResp = await rig.Client.PostAsync($"/api/v1/quarantine/{qId}/reject?apikey={rig.ApiKey}", null);
        Assert.Equal(HttpStatusCode.OK, rejectResp.StatusCode);

        // Approving an already-rejected quarantine item returns 409 Conflict
        var approveAfterRejectResp = await rig.Client.PostAsJsonAsync($"/api/v1/quarantine/{qId}/approve?apikey={rig.ApiKey}", new
        {
            confirmedLanguage = "de"
        });
        Assert.Equal(HttpStatusCode.Conflict, approveAfterRejectResp.StatusCode);

        // 4. Run ProcessCompletedDownloadsAsync
        await rig.ProcessCompletedDownloadsAsync();

        // Candidate hash is blocklisted
        using (var scope = rig.Services.CreateScope())
        {
            var orch = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var blocklist = await orch.ListBlocklistedHashesAsync();
            Assert.Contains(blocklist, b => b.InfoHash == "nzo:dune-upgrade-amb-hash");
        }

        // Original file on disk is STILL untouched
        Assert.True(File.Exists(existingFilePath));

        // Compat queue no longer lists it as active downloading
        var queueResp = await rig.SendRadarrAsync("GET", "/queue");
        var qArr = PipelineTestRig.GetQueueRecords(queueResp);
        var trackedItem = qArr.Single();
        Assert.Equal("failed", trackedItem?["trackedDownloadState"]?.GetValue<string>());
    }

    [Fact]
    public async Task E2E_10_DualCutoffUpgrade_EnglishBaseline_RemainsLanguageWanted_UntilGermanDualAudioReplacesFile()
    {
        await using var rig = await PipelineTestRig.StartAsync();

        // 1. Add Severance Season 1
        var seriesPost = await rig.SendSonarrAsync("POST", "/series", body: new
        {
            title = "Severance",
            tvdbId = PipelineTestRig.SeveranceTvdbId,
            monitored = true,
            rootFolderPath = rig.TvRoot,
            seasons = new[] { new { seasonNumber = 1, monitored = true } }
        });
        var seriesId = seriesPost.Body!["id"]!.GetValue<long>();

        // Set S01E01 RequiredAudioLangs to ["de"]
        long unitId;
        using (var scope = rig.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var tree = (await catalog.GetWorkTreeAsync(seriesId))!;
            unitId = tree.Containers.Single(c => c.ContainerNumber == 1).Units.Single(u => u.UnitNumber == 1).Id;
            await catalog.UpdateUnitAsync(unitId, new UpdateMediaUnitRequest(RequiredAudioLangs: ["de"]));
        }

        // 2. Import English baseline: Severance.S01E01.1080p.WEB-DL.English
        var enTitle = "Severance.S01E01.1080p.WEB-DL.English";
        var enDir = Path.Combine(rig.TempDir, "staging", enTitle);
        Directory.CreateDirectory(enDir);
        var enMkv = Path.Combine(enDir, $"{enTitle}.mkv");
        await File.WriteAllBytesAsync(enMkv, new byte[8192]);

        rig.Gateway.QueueItems.Add(new ActiveDownloadQueueItem(
            ClientId: rig.SabClient.Id,
            ClientName: rig.SabClient.Name,
            Protocol: "usenet",
            ExternalId: "nzo-sev-en",
            InfoHash: "nzo:sev-en-hash",
            Title: enTitle,
            Category: "tv-managerr",
            MediaVertical: "tv",
            Status: "Completed",
            TotalBytes: 8192,
            RemainingBytes: 0,
            OutputPath: enDir,
            ContainedFiles: [enMkv],
            IsStalled: false,
            IsMetadataStuck: false,
            ErrorMessage: null));

        rig.ContainerProbe.CustomProbe = _ => new ContainerProbeResult(
            IntegrityPassed: true,
            IntegrityFailureReason: null,
            ContainerFormat: "matroska",
            DurationSeconds: 120,
            Streams: [new ProbedRawStream(0, "audio", "eac3", 6, "en", "English DD+ 5.1", AudioWaveformPreview: [-0.2f, 0.3f, 0.1f, -0.4f])]);

        var report1 = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(1, report1.ImportedCount);

        // Verify hasFile == true AND LanguageCutoffMet == false
        using (var scope = rig.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var tree = (await catalog.GetWorkTreeAsync(seriesId))!;
            var u = tree.Containers[0].Units.Single(u => u.Id == unitId);
            Assert.Single(u.Files);
            Assert.False(u.LanguageCutoffMet, "English-only release must not satisfy German language cutoff");
        }

        // 3. Import upgrade: Severance.S01E01.1080p.BluRay.German.DL
        var deTitle = "Severance.S01E01.1080p.BluRay.German.DL";
        var deDir = Path.Combine(rig.TempDir, "staging", deTitle);
        Directory.CreateDirectory(deDir);
        var deMkv = Path.Combine(deDir, $"{deTitle}.mkv");
        await File.WriteAllBytesAsync(deMkv, new byte[16384]);

        rig.Gateway.QueueItems.Add(new ActiveDownloadQueueItem(
            ClientId: rig.SabClient.Id,
            ClientName: rig.SabClient.Name,
            Protocol: "usenet",
            ExternalId: "nzo-sev-de",
            InfoHash: "nzo:sev-de-hash",
            Title: deTitle,
            Category: "tv-managerr",
            MediaVertical: "tv",
            Status: "Completed",
            TotalBytes: 16384,
            RemainingBytes: 0,
            OutputPath: deDir,
            ContainedFiles: [deMkv],
            IsStalled: false,
            IsMetadataStuck: false,
            ErrorMessage: null));

        rig.ContainerProbe.CustomProbe = _ => new ContainerProbeResult(
            IntegrityPassed: true,
            IntegrityFailureReason: null,
            ContainerFormat: "matroska",
            DurationSeconds: 120,
            Streams:
            [
                new ProbedRawStream(0, "audio", "eac3", 6, "de", "German DD+ 5.1", AudioWaveformPreview: [0.1f, 0.2f, -0.3f, 0.4f]),
                new ProbedRawStream(1, "audio", "eac3", 6, "en", "English DD+ 5.1", AudioWaveformPreview: [-0.2f, 0.3f, 0.1f, -0.4f]),
            ]);

        var report2 = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(1, report2.ImportedCount);

        // Old file replaced, LanguageCutoffMet == true, DetectedAudioLangs includes "de"
        using (var scope = rig.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var tree = (await catalog.GetWorkTreeAsync(seriesId))!;
            var u = tree.Containers[0].Units.Single(u => u.Id == unitId);
            var file = Assert.Single(u.Files);
            Assert.Contains("de", file.DetectedAudioLangs);
            Assert.True(u.LanguageCutoffMet, "German DL release must satisfy German language cutoff");
        }

        // Plex refresh triggered
        Assert.Contains(rig.MockPlex.Requests, r => r.Path.Contains("/library/sections/2/refresh"));
    }

    [Fact]
    public async Task E2E_11_ActiveSeedingTorrent_StalledUpStatus_MapsValidOpenApiQueueStatus_HardlinksWithoutDeletingSource_AndClearsCompatQueueAfterImport()
    {
        await using var rig = await PipelineTestRig.StartAsync(useHardlinksGlobally: false);

        // Add movie
        await rig.SendRadarrAsync("POST", "/movie", body: new
        {
            title = "Dune: Part Two",
            tmdbId = 693134,
            year = 2024,
            monitored = true,
            rootFolderPath = rig.MoviesRoot
        });

        var torrentTitle = "Dune.Part.Two.2024.1080p.BluRay.Torrent";
        var torrentDir = Path.Combine(rig.TempDir, "staging", "qbit-seeding");
        Directory.CreateDirectory(torrentDir);
        var sourceMkv = Path.Combine(torrentDir, $"{torrentTitle}.mkv");
        await File.WriteAllBytesAsync(sourceMkv, new byte[8192]);

        // Active seeding torrent in qBittorrent: Status = "stalledUP", CanMoveFiles = false
        var seedingItem = new ActiveDownloadQueueItem(
            ClientId: rig.QbitClient.Id,
            ClientName: rig.QbitClient.Name,
            Protocol: "torrent",
            ExternalId: "qbit-seeding-1",
            InfoHash: "qbit-seeding-hash-0000000000000000000000",
            Title: torrentTitle,
            Category: "movies-managerr",
            MediaVertical: "movies",
            Status: "stalledUP",
            TotalBytes: 8192,
            RemainingBytes: 0,
            OutputPath: torrentDir,
            ContainedFiles: [sourceMkv],
            IsStalled: false,
            IsMetadataStuck: false,
            ErrorMessage: null,
            CanMoveFiles: false);
        rig.Gateway.QueueItems.Add(seedingItem);

        // 1. Before import, /compat/radarr/api/v3/queue maps status == "completed" (valid OpenAPI enum)
        var queueBefore = await rig.SendRadarrAsync("GET", "/queue");
        var qArr = PipelineTestRig.GetQueueRecords(queueBefore);
        Assert.Single(qArr);
        Assert.Equal("completed", qArr[0]?["status"]?.GetValue<string>());

        // 2. Run ProcessCompletedDownloadsAsync (even with UseHardlinks = false globally)
        var report = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(1, report.ImportedCount);
        var outcome = Assert.Single(report.Items);
        Assert.True(outcome.Hardlinked);

        // Seeding source file still exists on disk!
        Assert.True(File.Exists(sourceMkv), "Seeding torrent file must not be deleted from disk");

        // RemoveDownloadFromClientAsync is NOT called
        Assert.DoesNotContain("qbit-seeding-1", rig.Gateway.RemovedExternalIds);

        // /compat/radarr/api/v3/queue omits already-imported seeding torrent!
        var queueAfter = await rig.SendRadarrAsync("GET", "/queue");
        var qArrAfter = PipelineTestRig.GetQueueRecords(queueAfter);
        Assert.Empty(qArrAfter);
    }

    [Fact]
    public async Task E2E_12_ObfuscatedUsenetFolderAndForeignReleaseTitle_CorrelatesViaGrabHistory_AndIgnoresSampleStub()
    {
        await using var rig = await PipelineTestRig.StartAsync();

        // Add Severance series
        var seriesPost = await rig.SendSonarrAsync("POST", "/series", body: new
        {
            title = "Severance",
            tvdbId = PipelineTestRig.SeveranceTvdbId,
            monitored = true,
            rootFolderPath = rig.TvRoot,
            seasons = new[] { new { seasonNumber = 1, monitored = true } }
        });
        var seriesId = seriesPost.Body!["id"]!.GetValue<long>();

        // Find S01E03 unit id
        long unitId;
        using (var scope = rig.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var tree = (await catalog.GetWorkTreeAsync(seriesId))!;
            unitId = tree.Containers[0].Units.Single(u => u.UnitNumber == 3).Id;
        }

        // Grab release with German/alternate title: Abgetrennt.S01E03.German.DL.1080p.WEB.h264-GRP
        var releaseTitle = "Abgetrennt.S01E03.German.DL.1080p.WEB.h264-GRP";
        var extId = "nzo-obfuscated-sev";
        var hash = "nzo:obfuscated-sev-hash";

        await rig.RecordGrabHistoryAsync(
            clientId: rig.SabClient.Id,
            externalDownloadId: extId,
            infoHash: hash,
            releaseTitle: releaseTitle,
            mediaVertical: "tv",
            workId: seriesId,
            unitIds: [unitId],
            downloadUrl: "https://usenet.invalid/get/sev-s01e03",
            sourceType: "interactive");

        // Completed folder is obfuscated: /complete/tv/9f8a7b6c5d4e containing 9f8a7b6c5d4e.mkv + Sample stub
        var obfuscatedDir = Path.Combine(rig.TempDir, "staging", "complete", "tv", "9f8a7b6c5d4e");
        var sampleDir = Path.Combine(obfuscatedDir, "Sample");
        Directory.CreateDirectory(sampleDir);

        var mainMkv = Path.Combine(obfuscatedDir, "9f8a7b6c5d4e.mkv");
        var sampleMkv = Path.Combine(sampleDir, "9f8a7b6c5d4e-sample.mkv");
        await File.WriteAllBytesAsync(mainMkv, new byte[250_000]);
        await File.WriteAllBytesAsync(sampleMkv, new byte[5_000]);

        rig.Gateway.QueueItems.Add(new ActiveDownloadQueueItem(
            ClientId: rig.SabClient.Id,
            ClientName: rig.SabClient.Name,
            Protocol: "usenet",
            ExternalId: extId,
            InfoHash: hash,
            Title: releaseTitle,
            Category: "tv-managerr",
            MediaVertical: "tv",
            Status: "Completed",
            TotalBytes: 255_000,
            RemainingBytes: 0,
            OutputPath: obfuscatedDir,
            ContainedFiles: [mainMkv, sampleMkv],
            IsStalled: false,
            IsMetadataStuck: false,
            ErrorMessage: null));

        var report = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(1, report.ImportedCount);
        var outcome = Assert.Single(report.Items);
        Assert.Equal(VerificationVerdicts.VerifiedPass, outcome.Verdict);

        // Verify destination file belongs to Severance S01E03
        Assert.Contains("Severance", outcome.DestinationFilePath!);
        Assert.Contains("Season 01", outcome.DestinationFilePath!);
        Assert.Contains("S01E03", Path.GetFileName(outcome.DestinationFilePath!));

        // S01E03 hasFile == true
        var epResp = await rig.SendSonarrAsync("GET", "/episode", new Dictionary<string, object?> { ["seriesId"] = seriesId });
        var epList = (epResp.Body as JsonArray)!;
        var ep3 = epList.Single(e => e?["seasonNumber"]?.GetValue<int>() == 1 && e?["episodeNumber"]?.GetValue<int>() == 3);
        Assert.True(ep3?["hasFile"]?.GetValue<bool>());
    }

    [Fact]
    public async Task E2E_13_MalwareExecutableInBundle_BlockedPreImport_PurgedFromClient_AndBlocklisted()
    {
        await using var rig = await PipelineTestRig.StartAsync();

        await rig.SendSonarrAsync("POST", "/series", body: new
        {
            title = "Severance",
            tvdbId = PipelineTestRig.SeveranceTvdbId,
            monitored = true,
            rootFolderPath = rig.TvRoot,
            seasons = new[] { new { seasonNumber = 1, monitored = true } }
        });

        var releaseTitle = "Severance.S01E01.1080p.HDTV.MalwareBundle";
        var stagingDir = Path.Combine(rig.TempDir, "staging", releaseTitle);
        Directory.CreateDirectory(stagingDir);
        var mkv = Path.Combine(stagingDir, "Severance.S01E01.mkv");
        // Name the executable with a transient-fault phrase ('connection refused') to verify attackers
        // cannot spoof IsTransientDownloaderClientFault via untrusted archive filenames to bypass blocklisting.
        var scr = Path.Combine(stagingDir, "connection refused.scr");
        await File.WriteAllBytesAsync(mkv, new byte[8192]);
        await File.WriteAllBytesAsync(scr, new byte[1024]);

        rig.Gateway.QueueItems.Add(new ActiveDownloadQueueItem(
            ClientId: rig.SabClient.Id,
            ClientName: rig.SabClient.Name,
            Protocol: "usenet",
            ExternalId: "nzo-malware-bundle",
            InfoHash: "nzo:malware-bundle-hash",
            Title: releaseTitle,
            Category: "tv-managerr",
            MediaVertical: "tv",
            Status: "Completed",
            TotalBytes: 9216,
            RemainingBytes: 0,
            OutputPath: stagingDir,
            ContainedFiles: [mkv, scr],
            IsStalled: false,
            IsMetadataStuck: false,
            ErrorMessage: null));

        var report = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(0, report.ImportedCount);
        Assert.Equal(1, report.RejectedCount);
        var outcome = Assert.Single(report.Items);
        Assert.Equal("BlockedMalware", outcome.Verdict);

        // Zero files written to library
        var libFiles = Directory.EnumerateFiles(rig.TvRoot, "*", SearchOption.AllDirectories).ToList();
        Assert.Empty(libFiles);

        // Purged from client
        Assert.Contains("nzo-malware-bundle", rig.Gateway.RemovedExternalIds);

        // Hash blocklisted
        using (var scope = rig.Services.CreateScope())
        {
            var orch = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var blocklist = await orch.ListBlocklistedHashesAsync();
            Assert.Contains(blocklist, b => b.InfoHash == "nzo:malware-bundle-hash");
        }
    }

    [Fact]
    public async Task E2E_14_SpecialsAndOvaRelease_DynamicallyProvisionsSeason00_WithoutAffectingSeason1Monitoring()
    {
        await using var rig = await PipelineTestRig.StartAsync();

        // Initially has only Season 1 monitored
        var seriesPost = await rig.SendSonarrAsync("POST", "/series", body: new
        {
            title = "Severance",
            tvdbId = PipelineTestRig.SeveranceTvdbId,
            monitored = true,
            rootFolderPath = rig.TvRoot,
            seasons = new[] { new { seasonNumber = 1, monitored = true } }
        });
        var seriesId = seriesPost.Body!["id"]!.GetValue<long>();

        // Verify Season 0 does not exist yet
        using (var scope = rig.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var tree = (await catalog.GetWorkTreeAsync(seriesId))!;
            Assert.DoesNotContain(tree.Containers, c => c.ContainerNumber == 0);
        }

        // Completed OVA arrives: [SubsPlease] Severance - OVA 01 (1080p).mkv
        var releaseTitle = "[SubsPlease] Severance - OVA 01 (1080p)";
        var stagingDir = Path.Combine(rig.TempDir, "staging", releaseTitle);
        Directory.CreateDirectory(stagingDir);
        var mediaFile = Path.Combine(stagingDir, $"{releaseTitle}.mkv");
        await File.WriteAllBytesAsync(mediaFile, new byte[8192]);

        rig.Gateway.QueueItems.Add(new ActiveDownloadQueueItem(
            ClientId: rig.SabClient.Id,
            ClientName: rig.SabClient.Name,
            Protocol: "usenet",
            ExternalId: "nzo-sev-ova1",
            InfoHash: "nzo:sev-ova1-hash",
            Title: releaseTitle,
            Category: "tv-managerr",
            MediaVertical: "tv",
            Status: "Completed",
            TotalBytes: 8192,
            RemainingBytes: 0,
            OutputPath: stagingDir,
            ContainedFiles: [mediaFile],
            IsStalled: false,
            IsMetadataStuck: false,
            ErrorMessage: null));

        var report = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(1, report.ImportedCount);
        var outcome = Assert.Single(report.Items);
        Assert.Equal(VerificationVerdicts.VerifiedPass, outcome.Verdict);

        // Verify Season 0 and S00E01 dynamically provisioned, and Season 1 remains monitored: true
        using (var scope = rig.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var tree = (await catalog.GetWorkTreeAsync(seriesId))!;
            var s0 = Assert.Single(tree.Containers, c => c.ContainerNumber == 0);
            Assert.Equal("Specials", s0.Title);
            var ep01 = Assert.Single(s0.Units, u => u.UnitNumber == 1);
            Assert.Single(ep01.Files);

            var s1 = Assert.Single(tree.Containers, c => c.ContainerNumber == 1);
            Assert.True(s1.Monitored, "Season 1 monitoring must remain preserved");
        }
    }

    [Fact]
    public async Task E2E_15_CrossContainerPlexMountTranslation_AndPlex503Resilience()
    {
        await using var rig = await PipelineTestRig.StartAsync(tvPlexSectionId: "7");

        // 1. Configure Mock Plex /library/sections to return Section 7 with path "/plex-container/tv"
        rig.MockPlex.CustomSectionsJson = """
        {
            "MediaContainer": {
                "size": 1,
                "Directory": [
                    {
                        "key": "7",
                        "type": "show",
                        "title": "TV Shows",
                        "Location": [
                            { "path": "/plex-container/tv" }
                        ]
                    }
                ]
            }
        }
        """;

        // Add Severance series
        var seriesPost = await rig.SendSonarrAsync("POST", "/series", body: new
        {
            title = "Severance",
            tvdbId = PipelineTestRig.SeveranceTvdbId,
            monitored = true,
            rootFolderPath = rig.TvRoot,
            seasons = new[] { new { seasonNumber = 1, monitored = true } }
        });
        var seriesId = seriesPost.Body!["id"]!.GetValue<long>();

        // 2. Import Episode 1
        var release1 = "Severance.S01E01.1080p.WEB-DL";
        var dir1 = Path.Combine(rig.TempDir, "staging", release1);
        Directory.CreateDirectory(dir1);
        var mkv1 = Path.Combine(dir1, $"{release1}.mkv");
        await File.WriteAllBytesAsync(mkv1, new byte[8192]);

        rig.Gateway.QueueItems.Add(new ActiveDownloadQueueItem(
            ClientId: rig.SabClient.Id,
            ClientName: rig.SabClient.Name,
            Protocol: "usenet",
            ExternalId: "nzo-sev-ep1",
            InfoHash: "nzo:sev-ep1-hash",
            Title: release1,
            Category: "tv-managerr",
            MediaVertical: "tv",
            Status: "Completed",
            TotalBytes: 8192,
            RemainingBytes: 0,
            OutputPath: dir1,
            ContainedFiles: [mkv1],
            IsStalled: false,
            IsMetadataStuck: false,
            ErrorMessage: null));

        var report1 = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(1, report1.ImportedCount);

        // Verify Plex received /library/sections/7/refresh?path=%2Fplex-container%2Ftv%2F...
        var refreshReq = Assert.Single(rig.MockPlex.Requests, r => r.Path.Contains("/library/sections/7/refresh"));
        var queryPath = Uri.UnescapeDataString(refreshReq.Query);
        Assert.Contains("/plex-container/tv/Severance (2022)", queryPath);

        // 3. Flip Mock Plex to return 503 Service Unavailable
        rig.MockPlex.Return503 = true;

        // Import Episode 2
        var release2 = "Severance.S01E02.1080p.WEB-DL";
        var dir2 = Path.Combine(rig.TempDir, "staging", release2);
        Directory.CreateDirectory(dir2);
        var mkv2 = Path.Combine(dir2, $"{release2}.mkv");
        await File.WriteAllBytesAsync(mkv2, new byte[8192]);

        rig.Gateway.QueueItems.Add(new ActiveDownloadQueueItem(
            ClientId: rig.SabClient.Id,
            ClientName: rig.SabClient.Name,
            Protocol: "usenet",
            ExternalId: "nzo-sev-ep2",
            InfoHash: "nzo:sev-ep2-hash",
            Title: release2,
            Category: "tv-managerr",
            MediaVertical: "tv",
            Status: "Completed",
            TotalBytes: 8192,
            RemainingBytes: 0,
            OutputPath: dir2,
            ContainedFiles: [mkv2],
            IsStalled: false,
            IsMetadataStuck: false,
            ErrorMessage: null));

        // Import still succeeds cleanly with VerifiedPass without throwing!
        var report2 = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(1, report2.ImportedCount);
        var outcome2 = Assert.Single(report2.Items);
        Assert.Equal(VerificationVerdicts.VerifiedPass, outcome2.Verdict);

        var epResp = await rig.SendSonarrAsync("GET", "/episode", new Dictionary<string, object?> { ["seriesId"] = seriesId });
        var epList = (epResp.Body as JsonArray)!;
        var ep2 = epList.Single(e => e?["seasonNumber"]?.GetValue<int>() == 1 && e?["episodeNumber"]?.GetValue<int>() == 2);
        Assert.True(ep2?["hasFile"]?.GetValue<bool>());
    }

    [Fact]
    public async Task E2E_16_UnpackingDirectoryAndIncompletePartFiles_HeldInImportPending_UntilExtractionCompletes()
    {
        await using var rig = await PipelineTestRig.StartAsync();

        await rig.SendRadarrAsync("POST", "/movie", body: new
        {
            title = "Dune: Part Two",
            tmdbId = 693134,
            year = 2024,
            monitored = true,
            rootFolderPath = rig.MoviesRoot
        });

        // 1. Download completed in downloader, but sits in _UNPACK_ staging directory with .part file
        var unpackDir = Path.Combine(rig.TempDir, "staging", "_UNPACK_Dune.Part.Two.2024.1080p");
        Directory.CreateDirectory(unpackDir);
        var partFile = Path.Combine(unpackDir, "Dune.Part.Two.2024.1080p.mkv.part");
        await File.WriteAllBytesAsync(partFile, new byte[8192]);

        var qItem = new ActiveDownloadQueueItem(
            ClientId: rig.SabClient.Id,
            ClientName: rig.SabClient.Name,
            Protocol: "usenet",
            ExternalId: "nzo-dune-unpacking",
            InfoHash: "nzo:dune-unpacking-hash",
            Title: "Dune.Part.Two.2024.1080p",
            Category: "movies-managerr",
            MediaVertical: "movies",
            Status: "Completed",
            TotalBytes: 8192,
            RemainingBytes: 0,
            OutputPath: unpackDir,
            ContainedFiles: [partFile],
            IsStalled: false,
            IsMetadataStuck: false,
            ErrorMessage: null);
        rig.Gateway.QueueItems.Add(qItem);

        // 2. First poll holds it in ImportPending
        var report1 = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(0, report1.ImportedCount);
        Assert.Equal(0, report1.RejectedCount);
        var deferred = Assert.Single(report1.Items);
        Assert.Equal("DeferredUnpacking", deferred.Verdict);

        // Queue reports trackedDownloadState == "importPending"
        var queueResp = await rig.SendRadarrAsync("GET", "/queue");
        var qArr = PipelineTestRig.GetQueueRecords(queueResp);
        Assert.Single(qArr);
        Assert.Equal("importPending", qArr[0]?["trackedDownloadState"]?.GetValue<string>());

        // 3. Extraction completes: rename folder to final clean path and .mkv file
        var finalDir = Path.Combine(rig.TempDir, "staging", "Dune.Part.Two.2024.1080p");
        Directory.Move(unpackDir, finalDir);
        var finalMkv = Path.Combine(finalDir, "Dune.Part.Two.2024.1080p.mkv");
        File.Move(Path.Combine(finalDir, "Dune.Part.Two.2024.1080p.mkv.part"), finalMkv);

        rig.Gateway.QueueItems[0] = qItem with
        {
            OutputPath = finalDir,
            ContainedFiles = [finalMkv]
        };

        // 4. Second poll cleanly imports the completed movie!
        var report2 = await rig.ProcessCompletedDownloadsAsync();
        Assert.Equal(1, report2.ImportedCount);
        var finalOutcome = Assert.Single(report2.Items);
        Assert.Equal(VerificationVerdicts.VerifiedPass, finalOutcome.Verdict);

        var movieGet = await rig.SendRadarrAsync("GET", "/movie");
        var movieObj = (movieGet.Body as JsonArray)!.Single();
        Assert.True(movieObj?["hasFile"]?.GetValue<bool>());
    }

    // =========================================================================================
    // PIPELINE TEST RIG & ISOLATED HARNESS
    // =========================================================================================

    public sealed class PipelineTestRig : IAsyncDisposable
    {
        public const int SeveranceTvdbId = 371980;
        public const int DuneTmdbId = 693134;

        public string TempDir { get; } = Path.Combine(Path.GetTempPath(), $"managerr-e2e-rig-{Guid.NewGuid():N}");
        public string TvRoot => Path.Combine(TempDir, "media", "tv");
        public string MoviesRoot => Path.Combine(TempDir, "media", "movies");
        public string AnimeRoot => Path.Combine(TempDir, "media", "anime");

        public MockPlexServer MockPlex { get; } = new();
        public ControllableMetadataFederation Metadata { get; } = new();
        public ControllableUnifiedIndexerService Indexers { get; } = new();
        public ControllableDownloadClientGateway Gateway { get; } = new();
        public ControllableContainerProbeAdapter ContainerProbe { get; } = new();

        public WebApplicationFactory<Program> Factory { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public string ApiKey { get; private set; } = null!;

        public DownloadClientSummary SabClient { get; private set; } = null!;
        public DownloadClientSummary QbitClient { get; private set; } = null!;

        public IServiceProvider Services => Factory.Services;

        public static async Task<PipelineTestRig> StartAsync(bool useHardlinksGlobally = false, string? tvPlexSectionId = "2")
        {
            var rig = new PipelineTestRig();
            Directory.CreateDirectory(rig.TvRoot);
            Directory.CreateDirectory(rig.MoviesRoot);
            Directory.CreateDirectory(rig.AnimeRoot);

            rig.Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = Path.Combine(rig.TempDir, "config"),
                    ["Managerr:MasterKey"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                    ["Managerr:SchedulerEnabled"] = "false",
                    ["naming.use_hardlinks"] = useHardlinksGlobally ? "true" : "false",
                }));

                b.ConfigureServices(services =>
                {
                    services.RemoveAll<IMetadataFederationService>();
                    services.AddSingleton<IMetadataFederationService>(rig.Metadata);

                    services.RemoveAll<IUnifiedIndexerService>();
                    services.AddSingleton<IUnifiedIndexerService>(rig.Indexers);

                    services.RemoveAll<IDownloadClientDriverGateway>();
                    services.AddSingleton<IDownloadClientDriverGateway>(rig.Gateway);

                    services.RemoveAll<IContainerProbeAdapter>();
                    services.AddSingleton<IContainerProbeAdapter>(rig.ContainerProbe);
                });
            });

            using (var scope = rig.Factory.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var catalog = sp.GetRequiredService<IPolymorphicCatalogService>();
                var appSettings = sp.GetRequiredService<IAppSettingsService>();
                var orchestrator = sp.GetRequiredService<IUnifiedDownloadOrchestrator>();
                var users = sp.GetRequiredService<IPlexUserService>();
                var auth = sp.GetRequiredService<IPlexAuthAndSessionService>();

                // Configure Plex server settings
                await appSettings.SetAsync(PlexSettingsKeys.BoundServerUrl, rig.MockPlex.Url, isSecret: false);
                await appSettings.SetAsync(PlexSettingsKeys.OwnerAdminTokenEnc, "mock-plex-token", isSecret: true);
                if (useHardlinksGlobally)
                {
                    await appSettings.SetAsync("naming.use_hardlinks", "true", isSecret: false);
                }

                // Create Libraries
                await catalog.CreateLibraryAsync(new CreateLibraryRequest("Movies", "movies", rig.MoviesRoot, PlexSectionId: "1"));
                await catalog.CreateLibraryAsync(new CreateLibraryRequest("TV Shows", "tv", rig.TvRoot, PlexSectionId: tvPlexSectionId));
                await catalog.CreateLibraryAsync(new CreateLibraryRequest("Anime", "anime", rig.AnimeRoot, PlexSectionId: "3"));

                // Create Download Clients
                rig.SabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                    Name: "SABnzbd",
                    Implementation: "sabnzbd",
                    Host: "sab",
                    Port: 8080,
                    ApiKey: "k",
                    RemoveCompleted: true,
                    RemoveFailed: true));

                rig.QbitClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                    Name: "qBittorrent",
                    Implementation: "qbittorrent",
                    Host: "qbit",
                    Port: 8080,
                    ApiKey: "k",
                    RemoveCompleted: true,
                    RemoveFailed: true));

                // Seed Plex users
                var owner = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
                    PlexUserId: 1,
                    PlexUuid: "owner-uuid",
                    Username: "plexowner",
                    Email: "owner@plex.local",
                    AvatarUrl: null,
                    IsServerOwner: true,
                    IsHomeUser: true,
                    IsActive: true,
                    SharedPlexSectionIds: ["1", "2", "3", "7"]));

                // German viewer user (ID 101)
                await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
                    PlexUserId: 101,
                    PlexUuid: "user-101-uuid",
                    Username: "germanviewer",
                    Email: "german@plex.local",
                    AvatarUrl: null,
                    IsServerOwner: false,
                    IsHomeUser: false,
                    IsActive: true,
                    SharedPlexSectionIds: ["1", "2", "3", "7"],
                    PreferredAudioLang: "de",
                    PreferredSubLang: "de"));

                // Mint API Key for Seerr and test operations
                var apiKey = await auth.CreateApiKeyAsync(owner.Id, "RigKey", PermissionScopes.AllValidScopes.ToList());
                rig.ApiKey = apiKey.RawTokenOnce;
            }

            rig.Client = rig.Factory.CreateClient();
            rig.Client.DefaultRequestHeaders.Add("X-Api-Key", rig.ApiKey);
            return rig;
        }

        public async Task RecordGrabHistoryAsync(
            long clientId,
            string externalDownloadId,
            string? infoHash,
            string releaseTitle,
            string mediaVertical,
            long workId,
            IReadOnlyList<long>? unitIds = null,
            string? downloadUrl = null,
            string sourceType = "interactive")
        {
            var db = Factory.Services.GetRequiredService<ISqliteDbFactory>();
            await db.ExecuteWriteAsync(DatabaseKind.Core, async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO download_grab_history (
                        download_client_id, external_download_id, info_hash, release_title,
                        media_vertical, work_id, unit_ids_json, download_url, source_type, grabbed_at
                    ) VALUES (
                        @ClientId, @ExtId, @Hash, @Title,
                        @Vert, @WorkId, @UnitIdsJson, @DlUrl, @SourceType, @Now
                    );
                    """,
                    new
                    {
                        ClientId = clientId,
                        ExtId = externalDownloadId,
                        Hash = infoHash,
                        Title = releaseTitle,
                        Vert = mediaVertical,
                        WorkId = workId,
                        UnitIdsJson = JsonSerializer.Serialize(unitIds ?? [], JsonOpts),
                        DlUrl = downloadUrl,
                        SourceType = sourceType,
                        Now = DateTimeOffset.UtcNow.ToString("O")
                    }, tx);
            });
        }

        public async Task<SeerrObservation> SendSonarrAsync(string method, string path, IReadOnlyDictionary<string, object?>? query = null, object? body = null) =>
            await SendAsync("sonarr", method, path, query, body);

        public async Task<SeerrObservation> SendRadarrAsync(string method, string path, IReadOnlyDictionary<string, object?>? query = null, object? body = null) =>
            await SendAsync("radarr", method, path, query, body);

        private async Task<SeerrObservation> SendAsync(string app, string method, string path, IReadOnlyDictionary<string, object?>? query, object? body)
        {
            var qs = new StringBuilder($"apikey={Uri.EscapeDataString(ApiKey)}");
            foreach (var (k, v) in query ?? new Dictionary<string, object?>())
            {
                qs.Append('&').Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(FormatQueryVal(v)));
            }

            var url = $"/compat/{app}/api/v3{path}?{qs}";
            using var req = new HttpRequestMessage(new HttpMethod(method), url);
            if (body is not null)
            {
                req.Content = body is JsonNode node
                    ? new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json")
                    : JsonContent.Create(body);
            }

            using var resp = await Client.SendAsync(req);
            var raw = await resp.Content.ReadAsStringAsync();
            return new SeerrObservation(url, (int)resp.StatusCode, ParseBody(raw));
        }

        public async Task<ProcessCompletedDownloadsReport> ProcessCompletedDownloadsAsync()
        {
            using var scope = Factory.Services.CreateScope();
            var importSvc = scope.ServiceProvider.GetRequiredService<ICompletedDownloadImportService>();
            return await importSvc.ProcessCompletedDownloadsAsync();
        }

        public static JsonArray GetQueueRecords(SeerrObservation obs) =>
            obs.Body?["records"] as JsonArray ?? (obs.Body as JsonArray) ?? [];

        private static string FormatQueryVal(object? v) => v switch
        {
            null => "",
            bool b => b ? "true" : "false",
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
        };

        private static JsonNode? ParseBody(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try { return JsonNode.Parse(raw); } catch { return null; }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
            await MockPlex.DisposeAsync();
            if (Directory.Exists(TempDir))
            {
                try { Directory.Delete(TempDir, recursive: true); } catch { }
            }
        }
    }

    public sealed record SeerrObservation(string Url, int Status, JsonNode? Body);

    // =========================================================================================
    // MOCK PLEX SERVER (Kestrel / HttpListener loopback)
    // =========================================================================================

    public sealed record PlexRecordedRequest(string Method, string Path, string Query, string FullUrl, string? Token);

    public sealed class MockPlexServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private Task? _listenTask;

        public string Url { get; }
        public int Port { get; }
        public List<PlexRecordedRequest> Requests { get; } = [];
        public volatile bool Return503;
        public string? CustomSectionsJson;

        public MockPlexServer()
        {
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            }

            Url = $"http://127.0.0.1:{Port}";
            _listener.Prefixes.Add($"{Url}/");
            _listener.Start();
            _listenTask = Task.Run(ListenLoopAsync);
        }

        private async Task ListenLoopAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(context));
                }
                catch when (_cts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Listener stopped
                }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                var path = req.Url?.AbsolutePath ?? "";
                var query = req.Url?.Query ?? "";
                var fullUrl = req.Url?.ToString() ?? "";
                var token = req.Headers["X-Plex-Token"];

                lock (Requests)
                {
                    Requests.Add(new PlexRecordedRequest(req.HttpMethod, path, query, fullUrl, token));
                }

                if (Return503)
                {
                    ctx.Response.StatusCode = 503;
                    ctx.Response.StatusDescription = "Service Unavailable";
                    ctx.Response.Close();
                    return;
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";

                if (path.Equals("/library/sections", StringComparison.OrdinalIgnoreCase))
                {
                    var json = CustomSectionsJson ?? """
                    {
                        "MediaContainer": {
                            "size": 3,
                            "Directory": [
                                {
                                    "key": "1",
                                    "type": "movie",
                                    "title": "Movies",
                                    "Location": [
                                        { "path": "/media/movies" }
                                    ]
                                },
                                {
                                    "key": "2",
                                    "type": "show",
                                    "title": "TV Shows",
                                    "Location": [
                                        { "path": "/media/tv" }
                                    ]
                                },
                                {
                                    "key": "3",
                                    "type": "show",
                                    "title": "Anime",
                                    "Location": [
                                        { "path": "/media/anime" }
                                    ]
                                }
                            ]
                        }
                    }
                    """;
                    var bytes = Encoding.UTF8.GetBytes(json);
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    var ok = """{"status": "ok"}"""u8.ToArray();
                    ctx.Response.ContentLength64 = ok.Length;
                    ctx.Response.OutputStream.Write(ok, 0, ok.Length);
                }

                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            if (_listenTask is not null)
            {
                try { await _listenTask; } catch { }
            }
        }
    }

    // =========================================================================================
    // CONTROLLABLE TEST SERVICES (METADATA, INDEXERS, DOWNLOADERS, VERIFICATION)
    // =========================================================================================

    public sealed class ControllableMetadataFederation : IMetadataFederationService
    {
        public volatile int AsyncLookupDelayMs;

        public Dictionary<string, MetadataWorkCandidate> Candidates { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ControllableMetadataFederation()
        {
            // Seed Severance (TV)
            var sev = new MetadataWorkCandidate(
                "tvdb", PipelineTestRig.SeveranceTvdbId.ToString(), "tv", "Severance", ["Abgetrennt"], "en", 2022, "continuing",
                "Office workers with divided memories.", null,
                new Dictionary<string, string> { ["tvdb"] = PipelineTestRig.SeveranceTvdbId.ToString(), ["tmdb"] = "95396", ["imdb"] = "tt11280740" },
                [
                    CreateSeason(1, 9, "2022-02-18"),
                    CreateSeason(2, 10, "2025-01-17")
                ],
                Genres: ["Drama", "Sci-Fi"]);

            // Seed Dune: Part Two (Movie)
            var dune = new MetadataWorkCandidate(
                "tmdb", PipelineTestRig.DuneTmdbId.ToString(), "movies", "Dune: Part Two", [], "en", 2024, "released",
                "Paul Atreides unites with Chani and the Fremen.", null,
                new Dictionary<string, string> { ["tmdb"] = PipelineTestRig.DuneTmdbId.ToString(), ["imdb"] = "tt15239678" },
                [new MetadataContainerSeed("movie", 1, "Dune: Part Two", "2024-03-01", [new MetadataUnitSeed("movie", 1, null, "Dune: Part Two", "2024-03-01")])],
                Genres: ["Science Fiction"]);

            // Seed Bleach TYBW Anime: TVDB has 3x24 (+ S00 with 2 specials), TMDB has 1x60
            var bleachTvdb = new MetadataWorkCandidate(
                "tvdb", "1001", "anime", "Bleach: Thousand-Year Blood War", ["Bleach"], "ja", 2022, "continuing",
                "Soul Reapers.", null,
                new Dictionary<string, string> { ["tvdb"] = "1001", ["tmdb"] = "2001" },
                [
                    CreateSeason(0, 2, "2022-01-01"),
                    CreateSeason(1, 24, "2022-10-11"),
                    CreateSeason(2, 24, "2023-07-08"),
                    CreateSeason(3, 24, "2024-10-05"),
                ],
                Genres: ["Anime", "Action"]);

            var bleachTmdb = new MetadataWorkCandidate(
                "tmdb", "2001", "anime", "Bleach: Thousand-Year Blood War", ["Bleach"], "ja", 2022, "continuing",
                "Soul Reapers.", null,
                new Dictionary<string, string> { ["tmdb"] = "2001" },
                [CreateSeason(1, 60, "2022-10-11")],
                Genres: ["Anime", "Action"]);

            Candidates["tvdb:371980"] = sev;
            Candidates["tmdb:95396"] = sev;
            Candidates["imdb:tt11280740"] = sev;
            Candidates["title:severance"] = sev;
            Candidates["title:abgetrennt"] = sev;
            Candidates["tvdb:1001"] = bleachTvdb;
            Candidates["tmdb:2001"] = bleachTmdb;
            Candidates["title:bleach"] = bleachTvdb;

            Candidates["tmdb:693134"] = dune;
            Candidates["imdb:tt15239678"] = dune;
            Candidates["title:dune: part two"] = dune;

            Candidates["tvdb:1001"] = bleachTvdb;
            Candidates["tmdb:2001"] = bleachTmdb;
            Candidates["title:bleach: thousand-year blood war"] = bleachTvdb;
        }

        private static MetadataContainerSeed CreateSeason(int number, int episodes, string firstAired) =>
            new("season", number, number == 0 ? "Specials" : $"Season {number}", firstAired,
                Enumerable.Range(1, episodes)
                    .Select(e => new MetadataUnitSeed("episode", e, number == 0 ? null : (int?)((number - 1) * 24 + e), number == 0 ? $"Special {e}" : $"Episode {e}", firstAired))
                    .ToList());

        public IReadOnlyList<MetadataProviderDescriptor> ListBuiltInProviders(string? vertical = null) => [];
        public IReadOnlyList<string> GetDefaultWaterfall(string vertical) => [];

        public Task<IReadOnlyList<MetadataWorkCandidate>> SearchWaterfallAsync(string query, string vertical, IReadOnlyList<string>? customWaterfall = null, CancellationToken ct = default)
        {
            var q = query.Trim().ToLowerInvariant();
            if (Candidates.TryGetValue(q, out var hit)) return Task.FromResult<IReadOnlyList<MetadataWorkCandidate>>([hit]);
            if (Candidates.TryGetValue($"title:{q}", out hit)) return Task.FromResult<IReadOnlyList<MetadataWorkCandidate>>([hit]);
            var any = Candidates.Values.FirstOrDefault(c => c.Title.Equals(query, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult<IReadOnlyList<MetadataWorkCandidate>>(any is not null ? [any] : []);
        }

        public async Task<MetadataWorkCandidate?> ResolveWorkAsync(string vertical, string? preferredProvider, IReadOnlyDictionary<string, string> knownExternalIds, IReadOnlyList<string>? customWaterfall = null, CancellationToken ct = default)
        {
            return await ResolveWorkAsync(vertical, preferredProvider, knownExternalIds, customWaterfall, false, ct);
        }

        public async Task<MetadataWorkCandidate?> ResolveWorkAsync(string vertical, string? preferredProvider, IReadOnlyDictionary<string, string> knownExternalIds, IReadOnlyList<string>? customWaterfall, bool forceRefresh, CancellationToken ct = default)
        {
            if (AsyncLookupDelayMs > 0)
            {
                await Task.Delay(AsyncLookupDelayMs, ct);
            }

            foreach (var (k, v) in knownExternalIds)
            {
                if (preferredProvider is not null && Candidates.TryGetValue($"{preferredProvider}:{v}", out var hitPref))
                {
                    return hitPref;
                }
                if (Candidates.TryGetValue($"{k.ToLowerInvariant()}:{v}", out var hit))
                {
                    return hit;
                }
            }

            return null;
        }

        public MergedWorkMetadataFields ApplyFieldMergeWithLocks(MergedWorkMetadataFields current, MetadataWorkCandidate incoming, IReadOnlySet<string> lockedFields) =>
            new(
                lockedFields.Contains("title") ? current.Title : incoming.Title,
                incoming.AltTitles,
                incoming.OriginalLanguage,
                incoming.Year,
                incoming.Status,
                incoming.Overview,
                incoming.PosterUrl,
                incoming.ExternalIds);
    }

    public sealed class ControllableUnifiedIndexerService : IUnifiedIndexerService
    {
        public List<IndexedReleaseCandidate> Candidates { get; } = [];

        public Task<IReadOnlyList<IndexedReleaseCandidate>> SearchAsync(IndexerSearchQuery query, CancellationToken ct = default)
        {
            var results = Candidates.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(query.ImdbId))
            {
                results = results.Where(r => string.Equals(r.ImdbId, query.ImdbId, StringComparison.OrdinalIgnoreCase));
            }
            else if (query.TmdbId.HasValue)
            {
                results = results.Where(r => r.TmdbId == query.TmdbId.Value);
            }
            else if (query.TvdbId.HasValue)
            {
                results = results.Where(r => r.TvdbId == query.TvdbId.Value);
            }
            else if (!string.IsNullOrWhiteSpace(query.Query))
            {
                var qClean = query.Query.Trim().ToLowerInvariant();
                results = results.Where(r => r.Title.ToLowerInvariant().Contains(qClean));
            }

            return Task.FromResult<IReadOnlyList<IndexedReleaseCandidate>>(results.ToList());
        }

        public Task<IReadOnlyList<IndexerProxySummary>> ListProxiesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IndexerProxySummary>>([]);
        public Task<IndexerProxySummary> CreateProxyAsync(UpsertIndexerProxyRequest req, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteProxyAsync(long id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<IndexerSummary>> ListIndexersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IndexerSummary>>([]);
        public Task<IndexerSummary> CreateIndexerAsync(UpsertIndexerRequest req, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IndexerSummary> UpdateIndexerAsync(long id, UpsertIndexerRequest req, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(bool Ok, string Message)> TestIndexerAsync(UpsertIndexerRequest req, long? existingId = null, CancellationToken ct = default) => Task.FromResult((true, "ok"));
        public Task DeleteIndexerAsync(long id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordSuccessAsync(long indexerId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<DateTimeOffset> RecordFailureAsync(long indexerId, string errorMessage, CancellationToken ct = default) => Task.FromResult(DateTimeOffset.UtcNow);
        public Task<MultiCategoryRssSyncReport> SyncMultiCategoryRssAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    public sealed class ControllableDownloadClientGateway : IDownloadClientDriverGateway
    {
        public List<ActiveDownloadQueueItem> QueueItems { get; } = [];
        public List<(long ClientId, string Title, string ExternalId, string DownloadUrl)> EnqueuedGrabs { get; } = [];
        public List<string> RemovedExternalIds { get; } = [];

        public IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers() => DownloadClientCatalog.All;

        public Task<string> EnqueueReleaseAsync(DownloadClientConnectionSpec client, string releaseTitle, string downloadUrlOrMagnet, string resolvedCategory, CancellationToken ct = default)
        {
            var extId = $"ext-{Guid.NewGuid():N}";
            var proto = (client.Implementation == "sabnzbd" || downloadUrlOrMagnet.EndsWith(".nzb")) ? "usenet" : "torrent";
            var infoHash = proto == "usenet" ? $"nzo:{extId}" : extId.PadRight(40, '0');

            var item = new ActiveDownloadQueueItem(
                ClientId: client.Id,
                ClientName: client.Name,
                Protocol: proto,
                ExternalId: extId,
                InfoHash: infoHash,
                Title: releaseTitle,
                Category: resolvedCategory,
                MediaVertical: resolvedCategory,
                Status: "Downloading",
                TotalBytes: 10_000_000,
                RemainingBytes: 5_000_000,
                OutputPath: "",
                ContainedFiles: [],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: proto == "usenet");

            QueueItems.Add(item);
            EnqueuedGrabs.Add((client.Id, releaseTitle, extId, downloadUrlOrMagnet));
            return Task.FromResult(extId);
        }

        public Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(DownloadClientConnectionSpec client, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActiveDownloadQueueItem>>(QueueItems.Where(q => q.ClientId == client.Id).ToList());

        public Task RemoveDownloadAsync(DownloadClientConnectionSpec client, string externalId, bool deleteData, CancellationToken ct = default)
        {
            RemovedExternalIds.Add(externalId);
            QueueItems.RemoveAll(q => q.ExternalId == externalId && q.ClientId == client.Id);
            return Task.CompletedTask;
        }
    }

    public sealed class ControllableContainerProbeAdapter : IContainerProbeAdapter
    {
        public Func<string, ContainerProbeResult>? CustomProbe { get; set; }

        public Task<ContainerProbeResult> ProbeAsync(string filePath, double? expectedDurationSeconds = null, CancellationToken ct = default)
        {
            if (CustomProbe is not null)
            {
                return Task.FromResult(CustomProbe(filePath));
            }

            return Task.FromResult(new ContainerProbeResult(
                IntegrityPassed: true,
                IntegrityFailureReason: null,
                ContainerFormat: "matroska",
                DurationSeconds: 120,
                Streams:
                [
                    new ProbedRawStream(0, "audio", "eac3", 6, "de", "German DD+ 5.1", AudioWaveformPreview: [0.1f, 0.2f, -0.3f, 0.4f]),
                    new ProbedRawStream(1, "audio", "eac3", 6, "en", "English DD+ 5.1", AudioWaveformPreview: [-0.2f, 0.3f, 0.1f, -0.4f]),
                    new ProbedRawStream(2, "audio", "aac", 2, "ja", "Japanese Stereo", AudioWaveformPreview: [0.05f, -0.05f, 0.15f]),
                    new ProbedRawStream(3, "subtitle", "subrip", 0, "de", "German Subs", InlineTextPayload: "1\n00:00:01,000 --> 00:00:05,000\nHallo Welt und guten Tag.")
                ]));
        }
    }
}
