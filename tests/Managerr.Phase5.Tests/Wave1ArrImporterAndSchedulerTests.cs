using System.Net.Http.Json;
using System.Text.Json;
using Managerr.Catalog;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
using Managerr.Host.Services;
using Managerr.Indexers;
using Managerr.Metadata;
using Managerr.Profiles;
using Managerr.Verification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Managerr.Phase5.Tests;

public sealed class Wave1ArrImporterAndSchedulerTests
{
    [Fact]
    public async Task SonarrCdhPipeline_ZeroTouchAutoImportsSingleAndMultiEpisode_SeedingHardlink_AndBlocksUnknownEpisode()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-wave1-cdh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var tvRoot = Path.Combine(tempDir, "storage", "media", "tv");
            var sabCompleteDir = Path.Combine(tempDir, "storage", "usenet", "complete", "tv", "Chronicle.Seven.Realms.S01E13.FiNAL.MULTi.1080p.WEB.H264-SUPPLY");
            var qbitSeedingDir = Path.Combine(tempDir, "storage", "torrents", "complete", "tv", "Chronicle.Seven.Realms.S01E01E02.German.DL.1080p.WEB.H265-GRP");
            var unknownEpDir = Path.Combine(tempDir, "storage", "usenet", "complete", "tv", "Chronicle.Seven.Realms.S01E99.1080p.WEB.H264-UNKNOWN");
            Directory.CreateDirectory(tvRoot);
            Directory.CreateDirectory(sabCompleteDir);
            Directory.CreateDirectory(qbitSeedingDir);
            Directory.CreateDirectory(unknownEpDir);

            var ep13Mkv = Path.Combine(sabCompleteDir, "Chronicle.Seven.Realms.S01E13.FiNAL.MULTi.1080p.WEB.H264-SUPPLY.mkv");
            var ep13Srt = Path.Combine(sabCompleteDir, "Chronicle.Seven.Realms.S01E13.FiNAL.MULTi.1080p.WEB.H264-SUPPLY.de.srt");
            await File.WriteAllBytesAsync(ep13Mkv, new byte[16_384]);
            await File.WriteAllTextAsync(ep13Srt, "1\n00:00:01,000 --> 00:00:03,000\nHallo Welt!\n");

            var multiEpMkv = Path.Combine(qbitSeedingDir, "Chronicle.Seven.Realms.S01E01E02.German.DL.1080p.WEB.H265-GRP.mkv");
            await File.WriteAllBytesAsync(multiEpMkv, new byte[16_384]);

            var unknownEpMkv = Path.Combine(unknownEpDir, "Chronicle.Seven.Realms.S01E99.1080p.WEB.H264-UNKNOWN.mkv");
            await File.WriteAllBytesAsync(unknownEpMkv, new byte[16_384]);

            var driverGateway = new MultiClientWave1DriverGateway();
            var multiProbe = new MultiLanguageContainerProbe();

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                        ["Managerr:MasterKey"] = "wave1-cdh-test-master-key-32bytes",
                        ["Managerr:SchedulerEnabled"] = "true",
                        ["Managerr:SchedulerInitialDelayMs"] = "250",
                        ["Managerr:SchedulerTickMs"] = "80",
                        ["Managerr:DownloadMonitorIntervalSeconds"] = "5",
                    });
                });
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<IDownloadClientDriverGateway>();
                    services.AddSingleton<IDownloadClientDriverGateway>(driverGateway);
                    services.RemoveAll<IContainerProbeAdapter>();
                    services.AddSingleton<IContainerProbeAdapter>(multiProbe);
                });
            });

            using var scope = factory.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var profiles = scope.ServiceProvider.GetRequiredService<IComposableProfileService>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var importSvc = scope.ServiceProvider.GetRequiredService<ICompletedDownloadImportService>();

            var allProfiles = await profiles.ListProfilesAsync();
            var germanBestTv = allProfiles.FirstOrDefault(p => p.Slug == "german-first-best-quality")
                               ?? allProfiles.First(p => p.MediaVertical is "video" or "tv");

            var tvLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest(
                Name: "TV Shows",
                MediaType: "tv",
                RootFolderPath: tvRoot,
                DefaultProfileId: germanBestTv.Id));

            var chronicleTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: "Chronicle: Seven Realms",
                WorkKind: "series",
                AltTitles: ["Chronicle Seven Realms"],
                OriginalLanguage: "en",
                Year: 2026,
                Status: "continuing",
                Monitored: true,
                ExternalIds: new Dictionary<string, string> { ["tvdb"] = "450001" },
                Containers:
                [
                    new MetadataContainerSeed(
                        ContainerKind: "season",
                        ContainerNumber: 1,
                        Title: "Season 1",
                        ReleaseDate: "2026-02-01",
                        Units:
                        [
                            new MetadataUnitSeed("episode", 1, 1, "Pilot", "2026-02-01"),
                            new MetadataUnitSeed("episode", 2, 2, "The Awakening", "2026-02-08"),
                            new MetadataUnitSeed("episode", 3, 3, "Unforgiven", "2026-02-15"),
                            new MetadataUnitSeed("episode", 13, 13, "The Root Chakra", "2026-05-01"),
                        ]),
                ]));

            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd TestLab",
                Implementation: "sabnzbd",
                Host: "sab-test",
                Port: 8080,
                ApiKey: "sab-key",
                RemoveCompleted: true));

            var qbitClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "qBittorrent TestLab",
                Implementation: "qbittorrent",
                Host: "qbit-test",
                Port: 8080,
                Username: "admin",
                Password: "password",
                RemoveCompleted: true));

            driverGateway.QueueItems.AddRange(
            [
                new ActiveDownloadQueueItem(
                    ClientId: sabClient.Id,
                    ClientName: sabClient.Name,
                    Protocol: "usenet",
                    ExternalId: "ae4ec201-f9be-4c19-81cf-d23391df16d6",
                    InfoHash: "nzo:ae4ec201",
                    Title: "Chronicle.Seven.Realms.S01E13.FiNAL.MULTi.1080p.WEB.H264-SUPPLY",
                    Category: "tv-managerr",
                    MediaVertical: "tv",
                    Status: "Completed",
                    TotalBytes: 16_384,
                    RemainingBytes: 0,
                    OutputPath: sabCompleteDir,
                    ContainedFiles: [ep13Mkv, ep13Srt],
                    IsStalled: false,
                    IsMetadataStuck: false,
                    ErrorMessage: null,
                    CanMoveFiles: true),
                new ActiveDownloadQueueItem(
                    ClientId: qbitClient.Id,
                    ClientName: qbitClient.Name,
                    Protocol: "torrent",
                    ExternalId: "deadbeefcafe0102",
                    InfoHash: "deadbeefcafe0102",
                    Title: "Chronicle.Seven.Realms.S01E01E02.German.DL.1080p.WEB.H265-GRP",
                    Category: "tv-managerr",
                    MediaVertical: "tv",
                    Status: "uploading",
                    TotalBytes: 16_384,
                    RemainingBytes: 0,
                    OutputPath: qbitSeedingDir,
                    ContainedFiles: [multiEpMkv],
                    IsStalled: false,
                    IsMetadataStuck: false,
                    ErrorMessage: null,
                    CanMoveFiles: false),
                new ActiveDownloadQueueItem(
                    ClientId: sabClient.Id,
                    ClientName: sabClient.Name,
                    Protocol: "usenet",
                    ExternalId: "sab-unknown-s01e99",
                    InfoHash: "nzo:sab-unknown-s01e99",
                    Title: "Chronicle.Seven.Realms.S01E99.1080p.WEB.H264-UNKNOWN",
                    Category: "tv-managerr",
                    MediaVertical: "tv",
                    Status: "Completed",
                    TotalBytes: 16_384,
                    RemainingBytes: 0,
                    OutputPath: unknownEpDir,
                    ContainedFiles: [unknownEpMkv],
                    IsStalled: false,
                    IsMetadataStuck: false,
                    ErrorMessage: null,
                    CanMoveFiles: true),
            ]);

            // Wait for zero-touch BackgroundTaskSchedulerService to run RefreshMonitoredDownloads automatically!
            // All three items must settle (the assertions below cover each); the generous deadline only matters
            // on slow CI runners, the loop exits as soon as they are done.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            IReadOnlyList<TrackedDownloadDto> tracked = [];
            while (DateTimeOffset.UtcNow < deadline)
            {
                tracked = await importSvc.ListTrackedDownloadsAsync();
                if (tracked.Count >= 3 &&
                    tracked.All(t => t.State is TrackedDownloadStates.Imported or TrackedDownloadStates.ImportBlocked or TrackedDownloadStates.Failed))
                {
                    break;
                }

                await Task.Delay(100);
            }

            // 1. Verify Chronicle: Seven Realms S01E13 auto-imported zero-touch + standalone .WEB. -> WEB-DL + companion .srt imported
            var updatedTree = await catalog.GetWorkTreeAsync(chronicleTree.Work.Id);
            Assert.NotNull(updatedTree);

            var ep13Unit = updatedTree!.Containers.Single(c => c.ContainerNumber == 1).Units.Single(u => u.UnitNumber == 13);
            Assert.Single(ep13Unit.Files);
            var ep13File = ep13Unit.Files[0];
            Assert.Equal("WEBDL-1080p", ep13File.QualityName);
            var ep13DestAbs = Path.Combine(tvRoot, ep13File.RelativePath);
            Assert.True(File.Exists(ep13DestAbs));
            var expectedSidecar = Path.ChangeExtension(ep13DestAbs, ".de.srt");
            Assert.True(File.Exists(expectedSidecar), $"Expected companion subtitle sidecar at {expectedSidecar}");

            // Completed Usenet item with CanMoveFiles=true & RemoveCompleted=true was cleaned from SABnzbd,
            // and its job folder is gone too (the hardlinked library file stays)
            Assert.Contains("ae4ec201-f9be-4c19-81cf-d23391df16d6", driverGateway.RemovedExternalIds);
            Assert.False(Directory.Exists(sabCompleteDir), $"Imported usenet job folder should be deleted: {sabCompleteDir}");
            Assert.True(File.Exists(ep13DestAbs));

            // 2. Verify multi-episode S01E01E02 seeding torrent attached to BOTH Episode 1 and Episode 2, WITHOUT removing seeding torrent!
            var ep1Unit = updatedTree.Containers.Single(c => c.ContainerNumber == 1).Units.Single(u => u.UnitNumber == 1);
            var ep2Unit = updatedTree.Containers.Single(c => c.ContainerNumber == 1).Units.Single(u => u.UnitNumber == 2);
            var ep3Unit = updatedTree.Containers.Single(c => c.ContainerNumber == 1).Units.Single(u => u.UnitNumber == 3);
            Assert.Single(ep1Unit.Files);
            Assert.Single(ep2Unit.Files);
            Assert.Empty(ep3Unit.Files);
            Assert.Contains("S01E01E02", ep1Unit.Files[0].RelativePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(ep1Unit.Files[0].RelativePath, ep2Unit.Files[0].RelativePath);
            Assert.DoesNotContain("deadbeefcafe0102", driverGateway.RemovedExternalIds);
            Assert.True(File.Exists(multiEpMkv));

            // 3. Verify unknown episode S01E99 was safely ImportBlocked (NO S01E01 fallback overwrite, NO orphan work created!)
            var allWorksAfter = await catalog.ListWorksAsync();
            Assert.Single(allWorksAfter); // No duplicate/orphan TV work created!

            var blockedS01E99 = tracked.Single(t => t.ExternalDownloadId == "sab-unknown-s01e99");
            Assert.Equal(TrackedDownloadStates.ImportBlocked, blockedS01E99.State);
            Assert.Equal("warning", blockedS01E99.TrackedStatus);
            Assert.NotEmpty(blockedS01E99.StatusMessages);
            // Blocked jobs keep their folder for manual import
            Assert.True(File.Exists(unknownEpMkv));

            // 4. Verify Sonarr-style Manual Override Retry Import maps S01E99 onto Episode 3 cleanly!
            var manualOutcome = await importSvc.RetryTrackedDownloadImportAsync(
                blockedS01E99.Id,
                new ManualOverrideImportRequest(
                    WorkId: chronicleTree.Work.Id,
                    SeasonNumber: 1,
                    EpisodeNumbers: [3],
                    BypassUpgradeCheck: true,
                    ConfirmedAudioLanguages: ["de", "en"]));

            Assert.Equal(VerificationVerdicts.VerifiedPass, manualOutcome.Verdict);
            var treeAfterOverride = await catalog.GetWorkTreeAsync(chronicleTree.Work.Id);
            var ep3After = treeAfterOverride!.Containers.Single(c => c.ContainerNumber == 1).Units.Single(u => u.UnitNumber == 3);
            Assert.Single(ep3After.Files);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task TaskManagerAndAutomatedRssSync_MatchesWantedCatalog_ScoresProfiles_GrabsWinner_AndDeduplicates()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-wave1-rss-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var tvRoot = Path.Combine(tempDir, "storage", "media", "tv");
            Directory.CreateDirectory(tvRoot);

            var driverGateway = new MultiClientWave1DriverGateway();
            var rssGateway = new FakeRssTorznabGateway();

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                        ["Managerr:MasterKey"] = "wave1-rss-test-master-key-32bytes",
                        ["Managerr:SchedulerEnabled"] = "false",
                        ["Managerr:TestbedBootstrap:Enabled"] = "true",
                        ["Managerr:TestbedBootstrap:Username"] = "testadmin",
                        ["Managerr:TestbedBootstrap:Password"] = "Wave1AdminPass!2026",
                    });
                });
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<IDownloadClientDriverGateway>();
                    services.AddSingleton<IDownloadClientDriverGateway>(driverGateway);
                    services.RemoveAll<IIndexerHttpGateway>();
                    services.AddSingleton<IIndexerHttpGateway>(rssGateway);
                });
            });

            using var scope = factory.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var profiles = scope.ServiceProvider.GetRequiredService<IComposableProfileService>();
            var indexers = scope.ServiceProvider.GetRequiredService<IUnifiedIndexerService>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();

            var allProfiles = await profiles.ListProfilesAsync();
            var tvProfile = allProfiles.FirstOrDefault(p => p.Slug == "german-first-best-quality")
                            ?? allProfiles.First(p => p.MediaVertical is "video" or "tv");
            var tvLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest(
                Name: "TV Shows",
                MediaType: "tv",
                RootFolderPath: tvRoot,
                DefaultProfileId: tvProfile.Id));

            var workTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: "Chronicle: Seven Realms",
                WorkKind: "series",
                OriginalLanguage: "en",
                Year: 2026,
                Monitored: true,
                ExternalIds: new Dictionary<string, string> { ["tvdb"] = "450001" },
                Containers:
                [
                    new MetadataContainerSeed(
                        ContainerKind: "season",
                        ContainerNumber: 1,
                        Title: "Season 1",
                        ReleaseDate: "2026-02-01",
                        Units:
                        [
                            new MetadataUnitSeed("episode", 5, 5, "The Spirit Bridge", "2026-03-01"),
                            new MetadataUnitSeed("episode", 6, 6, "Unmonitored Filler", "2026-03-08"),
                        ]),
                ]));

            var ep6 = workTree.Containers[0].Units.Single(u => u.UnitNumber == 6);
            await catalog.UpdateUnitAsync(ep6.Id, new UpdateMediaUnitRequest(Monitored: false));

            await indexers.CreateIndexerAsync(new UpsertIndexerRequest(
                Name: "Test Newznab",
                Implementation: "newznab",
                DefinitionId: null,
                Protocol: "usenet",
                BaseUrl: "http://newznab.test.local",
                ApiKey: "rss-test-key",
                Categories: [5000, 5040],
                RssEnabled: true));

            await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd RSS",
                Implementation: "sabnzbd",
                Host: "sab-test",
                Port: 8080,
                ApiKey: "sab-key"));

            using var client = factory.CreateClient();
            var loginResp = await client.PostAsJsonAsync("/api/v1/auth/breakglass/login", new
            {
                usernameOrEmail = "testadmin",
                password = "Wave1AdminPass!2026",
            });
            loginResp.EnsureSuccessStatusCode();

            var loginJson = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
            var csrfToken = loginJson.GetProperty("csrfToken").GetString();
            if (loginResp.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", setCookies.Select(c => c.Split(';')[0])));
            }

            client.DefaultRequestHeaders.Add("X-Managerr-Csrf", csrfToken);

            // 1. Verify GET /api/v1/system/tasks & GET /api/v1/system/health/checks
            var tasks = await client.GetFromJsonAsync<List<ScheduledTaskSummary>>("/api/v1/system/tasks");
            Assert.NotNull(tasks);
            Assert.Contains(tasks!, t => t.TaskKey == ScheduledTaskKeys.RefreshMonitoredDownloads);
            Assert.Contains(tasks!, t => t.TaskKey == ScheduledTaskKeys.RssSync);

            var health = await client.GetFromJsonAsync<List<SystemHealthCheckItem>>("/api/v1/system/health/checks");
            Assert.NotNull(health);
            Assert.Contains(health!, h => h.Severity == "ok");

            // 2. Trigger RssSync via POST /api/v1/indexers/rss-sync -> picks higher-scoring 1080p German DL over 720p, skips unmonitored S01E06
            var rssResp = await client.PostAsync("/api/v1/indexers/rss-sync", null);
            rssResp.EnsureSuccessStatusCode();
            var rssReport = await rssResp.Content.ReadFromJsonAsync<JsonElement>();

            Assert.Equal(1, rssReport.GetProperty("polledIndexers").GetInt32());
            Assert.Equal(3, rssReport.GetProperty("totalReleases").GetInt32());
            Assert.Equal(1, rssReport.GetProperty("grabbedCount").GetInt32());
            Assert.Single(driverGateway.EnqueuedTitles);
            Assert.Equal("Chronicle.Seven.Realms.S01E05.German.DL.1080p.WEB.H265-WINNER", driverGateway.EnqueuedTitles[0]);

            var grabHistory = await orchestrator.ListGrabHistoryAsync();
            Assert.Single(grabHistory);
            Assert.Equal("rss_sync", grabHistory[0].SourceType);
            Assert.Equal(workTree.Work.Id, grabHistory[0].WorkId);

            // 3. Trigger POST /api/v1/system/tasks/RssSync/run a second time -> deduplicated (0 duplicate grabs!)
            var taskRunResp = await client.PostAsync($"/api/v1/system/tasks/{ScheduledTaskKeys.RssSync}/run", null);
            taskRunResp.EnsureSuccessStatusCode();
            var taskSummary = await taskRunResp.Content.ReadFromJsonAsync<ScheduledTaskSummary>();
            Assert.NotNull(taskSummary);
            Assert.Equal("completed", taskSummary!.LastStatus);
            Assert.Single(driverGateway.EnqueuedTitles); // Still 1!
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task FailedDownloadHandling_UsenetNzb_UnpackFailed_AndThreeStrikeTorrent_BlocklistAndAutoRepullReplacement()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-fdh-repull-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var tvRoot = Path.Combine(tempDir, "storage", "media", "tv");
            var failedUnpackDir = Path.Combine(tempDir, "downloads", "_FAILED_Chronicle.Seven.Realms.S01E02.German.DL.1080p.WEB.H265-BADRAR");
            Directory.CreateDirectory(tvRoot);
            Directory.CreateDirectory(failedUnpackDir);

            var driverGateway = new MultiClientWave1DriverGateway();
            var indexerGateway = new FakeFailedDownloadIndexerGateway();

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                        ["Managerr:MasterKey"] = "wave1-fdh-test-master-key-32bytes",
                        ["Managerr:SchedulerEnabled"] = "false",
                        ["Managerr:TestbedBootstrap:Enabled"] = "true",
                        ["Managerr:TestbedBootstrap:Username"] = "testadmin",
                        ["Managerr:TestbedBootstrap:Password"] = "Wave1AdminPass!2026",
                    });
                });
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<IDownloadClientDriverGateway>();
                    services.AddSingleton<IDownloadClientDriverGateway>(driverGateway);
                    services.RemoveAll<IIndexerHttpGateway>();
                    services.AddSingleton<IIndexerHttpGateway>(indexerGateway);
                });
            });

            using var scope = factory.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var profiles = scope.ServiceProvider.GetRequiredService<IComposableProfileService>();
            var indexers = scope.ServiceProvider.GetRequiredService<IUnifiedIndexerService>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var importSvc = scope.ServiceProvider.GetRequiredService<ICompletedDownloadImportService>();
            var taskMgr = scope.ServiceProvider.GetRequiredService<ITaskManagerService>();
            var activitySvc = scope.ServiceProvider.GetRequiredService<IWantedAndActivityService>();

            var allProfiles = await profiles.ListProfilesAsync();
            var tvProfile = allProfiles.FirstOrDefault(p => p.Slug == "german-first-best-quality")
                            ?? allProfiles.First(p => p.MediaVertical is "video" or "tv");
            var tvLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest(
                Name: "TV Shows",
                MediaType: "tv",
                RootFolderPath: tvRoot,
                DefaultProfileId: tvProfile.Id));

            var workTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: "Chronicle: Seven Realms",
                WorkKind: "tv",
                OriginalLanguage: "en",
                Year: 2026,
                Monitored: true,
                ExternalIds: new Dictionary<string, string> { ["tvdb"] = "450001" },
                Containers:
                [
                    new MetadataContainerSeed(
                        ContainerKind: "season",
                        ContainerNumber: 1,
                        Title: "Season 1",
                        ReleaseDate: "2026-02-01",
                        Units:
                        [
                            new MetadataUnitSeed("episode", 1, 1, "Episode 1", "2026-02-01"),
                            new MetadataUnitSeed("episode", 2, 2, "Episode 2", "2026-02-08"),
                            new MetadataUnitSeed("episode", 3, 3, "Episode 3", "2026-02-15"),
                        ]),
                ]));

            var ep1 = workTree.Containers[0].Units.Single(u => u.UnitNumber == 1);
            var ep2 = workTree.Containers[0].Units.Single(u => u.UnitNumber == 2);
            var ep3 = workTree.Containers[0].Units.Single(u => u.UnitNumber == 3);

            var newznab = await indexers.CreateIndexerAsync(new UpsertIndexerRequest(
                Name: "FDH Newznab",
                Implementation: "newznab",
                DefinitionId: null,
                Protocol: "usenet",
                BaseUrl: "http://newznab.fdh.local",
                ApiKey: "nzb-key",
                Categories: [5000, 5040],
                RssEnabled: true));

            var torznab = await indexers.CreateIndexerAsync(new UpsertIndexerRequest(
                Name: "FDH Torznab",
                Implementation: "torznab",
                DefinitionId: null,
                Protocol: "torrent",
                BaseUrl: "http://torznab.fdh.local",
                ApiKey: "tor-key",
                Categories: [5000, 5040],
                RssEnabled: true));

            var sab = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd FDH",
                Implementation: "sabnzbd",
                Host: "sab-fdh",
                Port: 8080,
                ApiKey: "sab-key",
                RemoveFailed: true));

            var qbt = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "qBittorrent FDH",
                Implementation: "qbittorrent",
                Host: "qbt-fdh",
                Port: 8080,
                Username: "admin",
                Password: "password",
                RemoveFailed: true));

            // 1. Grab Usenet NZB for S01E01 (InfoHash == null on Newznab, SABnzbd assigns nzo-grab-1)
            var initialNzbGrab = await orchestrator.GrabReleaseAsync(new GrabReleaseRequest(
                Title: "Chronicle.Seven.Realms.S01E01.German.DL.1080p.WEB.H265-BROKEN",
                Protocol: "usenet",
                MediaVertical: "tv",
                DownloadUrlOrMagnet: "http://newznab.fdh.local/getnzb/guid-e01-broken.nzb",
                InfoHash: null,
                WorkId: workTree.Work.Id,
                ContainerId: workTree.Containers[0].Id,
                UnitIds: [ep1.Id],
                IndexerId: newznab.Id,
                ReleaseGuid: "guid-e01-broken",
                SourceType: "wanted_search"));

            driverGateway.EnqueuedTitles.Clear();

            // Simulate (A) SABnzbd reporting S01E01 as Failed, AND (B) S01E02 externally added with _FAILED_ unpack folder
            driverGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sab.Id,
                ClientName: sab.Name,
                Protocol: "usenet",
                ExternalId: initialNzbGrab.ExternalDownloadId,
                InfoHash: $"nzo:{initialNzbGrab.ExternalDownloadId}",
                Title: "Chronicle.Seven.Realms.S01E01.German.DL.1080p.WEB.H265-BROKEN",
                Category: "tv-managerr",
                MediaVertical: "tv",
                Status: "Failed",
                TotalBytes: 2_200_000_000,
                RemainingBytes: 0,
                OutputPath: "",
                ContainedFiles: [],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: "Unpack failed, out of articles",
                CanMoveFiles: true));

            driverGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sab.Id,
                ClientName: sab.Name,
                Protocol: "usenet",
                ExternalId: "sab-unpack-fail-e02",
                InfoHash: "nzo:sab-unpack-fail-e02",
                Title: "Chronicle.Seven.Realms.S01E02.German.DL.1080p.WEB.H265-BADRAR",
                Category: "tv-managerr",
                MediaVertical: "tv",
                Status: "Completed",
                TotalBytes: 2_200_000_000,
                RemainingBytes: 0,
                OutputPath: failedUnpackDir,
                ContainedFiles: [],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            var importReport = await importSvc.ProcessCompletedDownloadsAsync();
            Assert.Equal(2, importReport.RejectedCount);

            // Both failed Usenet items were removed from SABnzbd, blocklisted, and skipped during replacement search!
            Assert.Contains(initialNzbGrab.ExternalDownloadId, driverGateway.RemovedExternalIds);
            Assert.Contains("sab-unpack-fail-e02", driverGateway.RemovedExternalIds);
            Assert.Contains("Chronicle.Seven.Realms.S01E01.German.DL.1080p.WEB.H265-HEALTHY", driverGateway.EnqueuedTitles);
            Assert.Contains("Chronicle.Seven.Realms.S01E02.German.DL.1080p.WEB.H265-GOODRAR", driverGateway.EnqueuedTitles);
            Assert.DoesNotContain("Chronicle.Seven.Realms.S01E01.German.DL.1080p.WEB.H265-BROKEN", driverGateway.EnqueuedTitles);
            Assert.DoesNotContain("Chronicle.Seven.Realms.S01E02.German.DL.1080p.WEB.H265-BADRAR", driverGateway.EnqueuedTitles);

            var trackedAfterFdh = await importSvc.ListTrackedDownloadsAsync();
            var trackedE02 = trackedAfterFdh.Single(t => t.ExternalDownloadId == "sab-unpack-fail-e02");
            Assert.Equal(TrackedDownloadStates.Failed, trackedE02.State); // NOT stuck in ImportPending!

            // 2. Grab stalled torrent for S01E03 and run QueueHealthCheck 3 times -> Strike 3 removes, blocklists, and auto-repulls ACTIVESWARM!
            driverGateway.EnqueuedTitles.Clear();
            await orchestrator.GrabReleaseAsync(new GrabReleaseRequest(
                Title: "Chronicle.Seven.Realms.S01E03.German.DL.1080p.WEB.H265-DEADSWARM",
                Protocol: "torrent",
                MediaVertical: "tv",
                DownloadUrlOrMagnet: "magnet:?xt=urn:btih:deadswarmhash0003",
                InfoHash: "deadswarmhash0003",
                WorkId: workTree.Work.Id,
                ContainerId: workTree.Containers[0].Id,
                UnitIds: [ep3.Id],
                IndexerId: torznab.Id,
                ReleaseGuid: "guid-e03-deadswarm",
                SourceType: "wanted_search"));
            driverGateway.EnqueuedTitles.Clear();

            driverGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: qbt.Id,
                ClientName: qbt.Name,
                Protocol: "torrent",
                ExternalId: "deadswarmhash0003",
                InfoHash: "deadswarmhash0003",
                Title: "Chronicle.Seven.Realms.S01E03.German.DL.1080p.WEB.H265-DEADSWARM",
                Category: "tv-managerr",
                MediaVertical: "tv",
                Status: "stalledDL",
                TotalBytes: 2_200_000_000,
                RemainingBytes: 1_900_000_000,
                OutputPath: Path.Combine(tempDir, "downloads", "deadswarm"),
                ContainedFiles: [],
                IsStalled: true,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: false));

            await taskMgr.RunTaskNowAsync(ScheduledTaskKeys.QueueHealthCheck);
            await taskMgr.RunTaskNowAsync(ScheduledTaskKeys.QueueHealthCheck);
            Assert.Empty(driverGateway.EnqueuedTitles);

            var thirdSweepSummary = await taskMgr.RunTaskNowAsync(ScheduledTaskKeys.QueueHealthCheck);
            Assert.Contains("auto-grabbed 1 replacement", thirdSweepSummary.LastMessage ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Contains("deadswarmhash0003", driverGateway.RemovedExternalIds);
            Assert.Contains("Chronicle.Seven.Realms.S01E03.German.DL.1080p.WEB.H265-ACTIVESWARM", driverGateway.EnqueuedTitles);

            var failedHistory = await activitySvc.GetMediaHistoryAsync(1, 50, eventType: MediaHistoryEventTypes.DownloadFailed);
            Assert.True(failedHistory.TotalCount >= 3);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private sealed class MultiClientWave1DriverGateway : IDownloadClientDriverGateway
    {
        public List<ActiveDownloadQueueItem> QueueItems { get; } = [];
        public List<string> RemovedExternalIds { get; } = [];
        public List<string> EnqueuedTitles { get; } = [];

        public IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers() => DownloadClientCatalog.All;

        public Task<string> EnqueueReleaseAsync(
            DownloadClientConnectionSpec client,
            string releaseTitle,
            string downloadUrlOrMagnet,
            string resolvedCategory,
            CancellationToken ct = default)
        {
            EnqueuedTitles.Add(releaseTitle);
            return Task.FromResult($"nzo-grab-{EnqueuedTitles.Count}");
        }

        public Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(
            DownloadClientConnectionSpec client,
            CancellationToken ct = default)
        {
            var list = QueueItems.Where(q => q.ClientId == client.Id).ToList();
            return Task.FromResult<IReadOnlyList<ActiveDownloadQueueItem>>(list);
        }

        public Task RemoveDownloadAsync(
            DownloadClientConnectionSpec client,
            string externalId,
            bool deleteData,
            CancellationToken ct = default)
        {
            RemovedExternalIds.Add(externalId);
            QueueItems.RemoveAll(q => q.ClientId == client.Id && q.ExternalId == externalId);
            return Task.CompletedTask;
        }
    }

    private sealed class MultiLanguageContainerProbe : IContainerProbeAdapter
    {
        public Task<ContainerProbeResult> ProbeAsync(
            string filePath,
            double? expectedDurationSeconds = null,
            CancellationToken ct = default)
        {
            var germanSrt = string.Join(
                "\n\n",
                Enumerable.Range(1, 80).Select(i => $"{i}\n00:00:{i % 59:D2},000 --> 00:00:{(i % 59) + 1:D2},000\nDas ist eine echte deutsche Episode der Serie und wir sind bereit."));
            return Task.FromResult(new ContainerProbeResult(
                IntegrityPassed: true,
                IntegrityFailureReason: null,
                ContainerFormat: "matroska",
                DurationSeconds: 1420,
                Streams:
                [
                    new ProbedRawStream(0, "audio", "eac3", 6, "ger", "Deutsch DD+ 5.1", AudioWaveformPreview: [0.31f, -0.22f, 0.48f, -0.15f, 0.39f]),
                    new ProbedRawStream(1, "audio", "eac3", 6, "eng", "English Original", AudioWaveformPreview: [-0.41f, 0.19f, 0.12f, -0.52f, 0.08f]),
                    new ProbedRawStream(2, "audio", "eac3", 2, "fin", "Suomi", AudioWaveformPreview: [0.05f, 0.44f, -0.33f, 0.21f, -0.19f]),
                    new ProbedRawStream(3, "subtitle", "subrip", 0, "ger", "Deutsch", InlineTextPayload: germanSrt),
                ]));
        }
    }

    private sealed class FakeRssTorznabGateway : IIndexerHttpGateway
    {
        public Task<string> FetchStringAsync(Uri targetUri, IndexerProxyInternal? matchedProxy, CancellationToken ct = default)
        {
            const string xml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
                  <channel>
                    <title>Test Newznab RSS</title>
                    <item>
                      <title>Chronicle.Seven.Realms.S01E05.720p.HDTV.x264-LOW</title>
                      <guid>guid-chronicle-s01e05-720p</guid>
                      <link>http://newznab.test.local/getnzb/guid-chronicle-s01e05-720p.nzb</link>
                      <pubDate>Mon, 02 Nov 2026 19:00:00 +0000</pubDate>
                      <enclosure url="http://newznab.test.local/getnzb/guid-chronicle-s01e05-720p.nzb" length="650000000" type="application/x-nzb" />
                      <newznab:attr name="category" value="5040" />
                      <newznab:attr name="tvdbid" value="450001" />
                    </item>
                    <item>
                      <title>Chronicle.Seven.Realms.S01E05.German.DL.1080p.WEB.H265-WINNER</title>
                      <guid>guid-chronicle-s01e05-1080p-de</guid>
                      <link>http://newznab.test.local/getnzb/guid-chronicle-s01e05-1080p-de.nzb</link>
                      <pubDate>Mon, 02 Nov 2026 19:05:00 +0000</pubDate>
                      <enclosure url="http://newznab.test.local/getnzb/guid-chronicle-s01e05-1080p-de.nzb" length="1450000000" type="application/x-nzb" />
                      <newznab:attr name="category" value="5040" />
                      <newznab:attr name="tvdbid" value="450001" />
                    </item>
                    <item>
                      <title>Chronicle.Seven.Realms.S01E06.German.DL.1080p.WEB.H265-UNMONITORED</title>
                      <guid>guid-chronicle-s01e06-unmonitored</guid>
                      <link>http://newznab.test.local/getnzb/guid-chronicle-s01e06-unmonitored.nzb</link>
                      <pubDate>Mon, 02 Nov 2026 19:10:00 +0000</pubDate>
                      <enclosure url="http://newznab.test.local/getnzb/guid-chronicle-s01e06-unmonitored.nzb" length="1450000000" type="application/x-nzb" />
                      <newznab:attr name="category" value="5040" />
                      <newznab:attr name="tvdbid" value="450001" />
                    </item>
                  </channel>
                </rss>
                """;
            return Task.FromResult(xml);
        }
    }

    private sealed class FakeFailedDownloadIndexerGateway : IIndexerHttpGateway
    {
        public Task<string> FetchStringAsync(Uri targetUri, IndexerProxyInternal? matchedProxy, CancellationToken ct = default)
        {
            if (targetUri.Host.Contains("torznab", StringComparison.OrdinalIgnoreCase))
            {
                const string torznabXml = """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
                      <channel>
                        <title>FDH Torznab</title>
                        <item>
                          <title>Chronicle.Seven.Realms.S01E03.German.DL.1080p.WEB.H265-DEADSWARM</title>
                          <guid>guid-e03-deadswarm</guid>
                          <link>magnet:?xt=urn:btih:deadswarmhash0003</link>
                          <pubDate>Mon, 02 Nov 2026 19:00:00 +0000</pubDate>
                          <enclosure url="magnet:?xt=urn:btih:deadswarmhash0003" length="2200000000" type="application/x-bittorrent" />
                          <torznab:attr name="category" value="5040" />
                          <torznab:attr name="infohash" value="deadswarmhash0003" />
                          <torznab:attr name="seeders" value="25" />
                        </item>
                        <item>
                          <title>Chronicle.Seven.Realms.S01E03.German.DL.1080p.WEB.H265-ACTIVESWARM</title>
                          <guid>guid-e03-activeswarm</guid>
                          <link>magnet:?xt=urn:btih:activeswarmhash03</link>
                          <pubDate>Mon, 02 Nov 2026 19:05:00 +0000</pubDate>
                          <enclosure url="magnet:?xt=urn:btih:activeswarmhash03" length="1450000000" type="application/x-bittorrent" />
                          <torznab:attr name="category" value="5040" />
                          <torznab:attr name="infohash" value="activeswarmhash03" />
                          <torznab:attr name="seeders" value="40" />
                        </item>
                      </channel>
                    </rss>
                    """;
                return Task.FromResult(torznabXml);
            }

            const string newznabXml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
                  <channel>
                    <title>FDH Newznab</title>
                    <item>
                      <title>Chronicle.Seven.Realms.S01E01.German.DL.1080p.WEB.H265-BROKEN</title>
                      <guid>guid-e01-broken</guid>
                      <link>http://newznab.fdh.local/getnzb/guid-e01-broken.nzb</link>
                      <pubDate>Mon, 02 Nov 2026 19:00:00 +0000</pubDate>
                      <enclosure url="http://newznab.fdh.local/getnzb/guid-e01-broken.nzb" length="2200000000" type="application/x-nzb" />
                      <newznab:attr name="category" value="5040" />
                      <newznab:attr name="tvdbid" value="450001" />
                    </item>
                    <item>
                      <title>Chronicle.Seven.Realms.S01E01.German.DL.1080p.WEB.H265-HEALTHY</title>
                      <guid>guid-e01-healthy</guid>
                      <link>http://newznab.fdh.local/getnzb/guid-e01-healthy.nzb</link>
                      <pubDate>Mon, 02 Nov 2026 19:05:00 +0000</pubDate>
                      <enclosure url="http://newznab.fdh.local/getnzb/guid-e01-healthy.nzb" length="1450000000" type="application/x-nzb" />
                      <newznab:attr name="category" value="5040" />
                      <newznab:attr name="tvdbid" value="450001" />
                    </item>
                    <item>
                      <title>Chronicle.Seven.Realms.S01E02.German.DL.1080p.WEB.H265-BADRAR</title>
                      <guid>guid-e02-badrar</guid>
                      <link>http://newznab.fdh.local/getnzb/guid-e02-badrar.nzb</link>
                      <pubDate>Mon, 02 Nov 2026 19:10:00 +0000</pubDate>
                      <enclosure url="http://newznab.fdh.local/getnzb/guid-e02-badrar.nzb" length="2200000000" type="application/x-nzb" />
                      <newznab:attr name="category" value="5040" />
                      <newznab:attr name="tvdbid" value="450001" />
                    </item>
                    <item>
                      <title>Chronicle.Seven.Realms.S01E02.German.DL.1080p.WEB.H265-GOODRAR</title>
                      <guid>guid-e02-goodrar</guid>
                      <link>http://newznab.fdh.local/getnzb/guid-e02-goodrar.nzb</link>
                      <pubDate>Mon, 02 Nov 2026 19:15:00 +0000</pubDate>
                      <enclosure url="http://newznab.fdh.local/getnzb/guid-e02-goodrar.nzb" length="1450000000" type="application/x-nzb" />
                      <newznab:attr name="category" value="5040" />
                      <newznab:attr name="tvdbid" value="450001" />
                    </item>
                  </channel>
                </rss>
                """;
            return Task.FromResult(newznabXml);
        }
    }

    [Fact]
    public async Task FailedNzbTracking_PersistsMultiKeyFingerprints_BlocksRotatedApiKeys_TitleRewrites_SameNzb_AndRemoveFailedRetention()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-failed-nzb-ledger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var tvRoot = Path.Combine(tempDir, "storage", "media", "tv");
            Directory.CreateDirectory(tvRoot);

            var driverGateway = new MultiClientWave1DriverGateway();
            var indexerGateway = new FakeMultiIndexerNzbTrackingGateway();

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                        ["Managerr:MasterKey"] = "wave1-nzb-ledger-master-key-32b!",
                        ["Managerr:SchedulerEnabled"] = "false",
                        ["Managerr:TestbedBootstrap:Enabled"] = "true",
                        ["Managerr:TestbedBootstrap:Username"] = "testadmin",
                        ["Managerr:TestbedBootstrap:Password"] = "Wave1AdminPass!2026",
                    });
                });
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<IDownloadClientDriverGateway>();
                    services.AddSingleton<IDownloadClientDriverGateway>(driverGateway);
                    services.RemoveAll<IIndexerHttpGateway>();
                    services.AddSingleton<IIndexerHttpGateway>(indexerGateway);
                });
            });

            using var scope = factory.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var profiles = scope.ServiceProvider.GetRequiredService<IComposableProfileService>();
            var indexers = scope.ServiceProvider.GetRequiredService<IUnifiedIndexerService>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var importSvc = scope.ServiceProvider.GetRequiredService<ICompletedDownloadImportService>();
            var rssSync = scope.ServiceProvider.GetRequiredService<IAutomatedRssSyncService>();

            var allProfiles = await profiles.ListProfilesAsync();
            var tvProfile = allProfiles.FirstOrDefault(p => p.Slug == "german-first-best-quality")
                            ?? allProfiles.First(p => p.MediaVertical is "video" or "tv");
            var tvLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest(
                Name: "TV Shows",
                MediaType: "tv",
                RootFolderPath: tvRoot,
                DefaultProfileId: tvProfile.Id));

            var workTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: "Chronicle: Seven Realms",
                WorkKind: "tv",
                OriginalLanguage: "en",
                Year: 2026,
                Monitored: true,
                ExternalIds: new Dictionary<string, string> { ["tvdb"] = "450001" },
                Containers:
                [
                    new MetadataContainerSeed(
                        ContainerKind: "season",
                        ContainerNumber: 1,
                        Title: "Season 1",
                        ReleaseDate: "2026-02-01",
                        Units:
                        [
                            new MetadataUnitSeed("episode", 1, 1, "Episode 1", "2026-02-01"),
                        ]),
                ]));

            var ep1 = workTree.Containers[0].Units.Single(u => u.UnitNumber == 1);

            var primaryNewznab = await indexers.CreateIndexerAsync(new UpsertIndexerRequest(
                Name: "Primary Geek NZB",
                Implementation: "newznab",
                DefinitionId: null,
                Protocol: "usenet",
                BaseUrl: "http://primary-nzb.local",
                ApiKey: "ROTATED_NEW_KEY_999",
                Categories: [5000, 5040],
                RssEnabled: true));

            var mirrorNewznab = await indexers.CreateIndexerAsync(new UpsertIndexerRequest(
                Name: "Mirror Slug NZB",
                Implementation: "newznab",
                DefinitionId: null,
                Protocol: "usenet",
                BaseUrl: "http://mirror-nzb.local",
                ApiKey: "MIRROR_KEY_777",
                Categories: [5000, 5040],
                RssEnabled: true));

            // Configure SABnzbd with RemoveFailed = FALSE so failed NZBs remain in SABnzbd history across cycles
            var sab = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd KeepHistory",
                Implementation: "sabnzbd",
                Host: "sab-keep",
                Port: 8080,
                ApiKey: "sab-key",
                RemoveFailed: false));

            var publishedAt = new DateTimeOffset(2026, 11, 2, 19, 0, 0, TimeSpan.Zero);
            const long brokenNzbSize = 2_345_678_900L;

            // 1. Grab NZB initially with OLD_SECRET_KEY_123 in GUID & DownloadUrl
            await orchestrator.GrabReleaseAsync(new GrabReleaseRequest(
                Title: "Chronicle.Seven.Realms.S01E01.German.DL.1080p.WEB.H265-BROKEN",
                Protocol: "usenet",
                MediaVertical: "tv",
                DownloadUrlOrMagnet: "http://primary-nzb.local/api?t=get&id=99887766&apikey=OLD_SECRET_KEY_123",
                InfoHash: null,
                WorkId: workTree.Work.Id,
                ContainerId: workTree.Containers[0].Id,
                UnitIds: [ep1.Id],
                IndexerId: primaryNewznab.Id,
                IndexerName: primaryNewznab.Name,
                SizeBytes: brokenNzbSize,
                PublishedAt: publishedAt,
                ReleaseGuid: "http://primary-nzb.local/details/99887766?apikey=OLD_SECRET_KEY_123",
                SourceType: "wanted_search"));

            driverGateway.EnqueuedTitles.Clear();

            // 2. SABnzbd reports job failed with a DIFFERENT external NZO ID AND a SABnzbd-rewritten job title
            //    (spaces instead of dots, _FAILED_ prefix, .nzb suffix) -> exercises normalized-title grab_history correlation!
            indexerGateway.AllowHealthyReplacement = false;
            driverGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sab.Id,
                ClientName: sab.Name,
                Protocol: "usenet",
                ExternalId: "SABnzbd_nzo_rewritten_99",
                InfoHash: "nzo:SABnzbd_nzo_rewritten_99",
                Title: "_FAILED_ Chronicle Seven Realms S01E01 German DL 1080p WEB H265 BROKEN.nzb",
                Category: "tv-managerr",
                MediaVertical: "tv",
                Status: "Failed",
                TotalBytes: brokenNzbSize,
                RemainingBytes: 0,
                OutputPath: "",
                ContainedFiles: [],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: "Aborted, cannot be completed (missing Usenet articles)",
                CanMoveFiles: true));

            var firstImportReport = await importSvc.ProcessCompletedDownloadsAsync();
            Assert.Equal(1, firstImportReport.RejectedCount);
            Assert.Empty(driverGateway.RemovedExternalIds); // RemoveFailed == false honored!
            Assert.Empty(driverGateway.EnqueuedTitles); // Broken variants in feed were NOT retried!

            // 3. Inspect persisted failed-NZB blocklist row: deterministic nzb: key, sanitized URLs (no API key leaked!), full metadata
            var blocklistEntries = await orchestrator.ListBlocklistedHashesAsync();
            var nzbEntry = Assert.Single(blocklistEntries);
            Assert.Equal("usenet", nzbEntry.Protocol);
            Assert.StartsWith("nzb:", nzbEntry.InfoHash);
            Assert.Equal("http://primary-nzb.local/details/99887766", nzbEntry.ReleaseGuid);
            Assert.Equal("http://primary-nzb.local/api?id=99887766&t=get", nzbEntry.DownloadUrl);
            Assert.DoesNotContain("OLD_SECRET_KEY_123", nzbEntry.ReleaseGuid ?? "");
            Assert.DoesNotContain("OLD_SECRET_KEY_123", nzbEntry.DownloadUrl ?? "");
            Assert.Equal(primaryNewznab.Id, nzbEntry.IndexerId);
            Assert.Equal(brokenNzbSize, nzbEntry.SizeBytes);
            Assert.Equal("chronicle seven realms s01e01 german dl 1080p web h265 broken", nzbEntry.NormalizedTitle);

            // 4. Re-running RefreshMonitoredDownloads while failed NZB stays in SABnzbd history (RemoveFailed=false) is a 100% no-op!
            var secondImportReport = await importSvc.ProcessCompletedDownloadsAsync();
            Assert.Equal(0, secondImportReport.EvaluatedCount);
            Assert.Equal(0, secondImportReport.RejectedCount);

            // 5. Direct GrabReleaseAsync rejects ALL disguised variants of the failed NZB:
            //    (a) Same GUID/URL with a rotated API key & renamed title
            await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.GrabReleaseAsync(new GrabReleaseRequest(
                Title: "Completely.Renamed.Title.S01E01.1080p",
                Protocol: "usenet",
                MediaVertical: "tv",
                DownloadUrlOrMagnet: "http://primary-nzb.local/api?t=get&id=99887766&apikey=ROTATED_NEW_KEY_999",
                ReleaseGuid: "http://primary-nzb.local/details/99887766?apikey=ROTATED_NEW_KEY_999")));

            //    (b) Cross-indexer post on Mirror Newznab with underscores instead of dots
            await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.GrabReleaseAsync(new GrabReleaseRequest(
                Title: "Chronicle_Seven_Realms_S01E01_German_DL_1080p_WEB_H265_BROKEN",
                Protocol: "usenet",
                MediaVertical: "tv",
                DownloadUrlOrMagnet: "http://mirror-nzb.local/getnzb/different-guid-555.nzb",
                IndexerId: mirrorNewznab.Id,
                ReleaseGuid: "mirror-guid-555")));

            //    (c) Sonarr SameNzb obfuscated title on same indexer sharing (IndexerId, PublishedAt, SizeBytes)
            await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.GrabReleaseAsync(new GrabReleaseRequest(
                Title: "9f8e7d6c5b4a3210.S01E01.German.DL.1080p.WEB.H265",
                Protocol: "usenet",
                MediaVertical: "tv",
                DownloadUrlOrMagnet: "http://primary-nzb.local/api?t=get&id=11111111&apikey=ROTATED_NEW_KEY_999",
                IndexerId: primaryNewznab.Id,
                SizeBytes: brokenNzbSize,
                PublishedAt: publishedAt,
                ReleaseGuid: "http://primary-nzb.local/details/11111111")));

            // 6. Enable a genuinely healthy replacement NZB in RSS feed -> RSS Sync blocks all 3 broken variants and ONLY grabs CLEANPOST!
            indexerGateway.AllowHealthyReplacement = true;
            var rssReport = await rssSync.ExecuteRssSyncAndGrabAsync();
            Assert.Equal(1, rssReport.GrabbedCount);
            Assert.Single(driverGateway.EnqueuedTitles);
            Assert.Equal("Chronicle.Seven.Realms.S01E01.German.DL.1080p.WEB.H265-CLEANPOST", driverGateway.EnqueuedTitles[0]);
            Assert.Equal(3, rssReport.Decisions.Count(d => d.DecisionKind == "Blocklisted"));

            // 7. Explicit ForceGrab overrides when user intentionally clicks Force Grab, and RemoveBlocklistedHashAsync deletes cleanly
            var forced = await orchestrator.GrabReleaseAsync(new GrabReleaseRequest(
                Title: "Chronicle.Seven.Realms.S01E01.German.DL.1080p.WEB.H265-BROKEN",
                Protocol: "usenet",
                MediaVertical: "tv",
                DownloadUrlOrMagnet: "http://primary-nzb.local/api?t=get&id=99887766&apikey=ROTATED_NEW_KEY_999",
                ReleaseGuid: "http://primary-nzb.local/details/99887766?apikey=ROTATED_NEW_KEY_999",
                ForceGrab: true));
            Assert.NotEmpty(forced.ExternalDownloadId);
            Assert.NotEmpty(forced.ExternalDownloadId);

            Assert.True(await orchestrator.RemoveBlocklistedHashAsync(nzbEntry.InfoHash));
            Assert.Empty(await orchestrator.ListBlocklistedHashesAsync());
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private sealed class FakeMultiIndexerNzbTrackingGateway : IIndexerHttpGateway
    {
        public bool AllowHealthyReplacement { get; set; }

        public Task<string> FetchStringAsync(Uri targetUri, IndexerProxyInternal? matchedProxy, CancellationToken ct = default)
        {
            if (targetUri.Host.Contains("mirror-nzb", StringComparison.OrdinalIgnoreCase))
            {
                var healthyItem = AllowHealthyReplacement
                    ? """
                      <item>
                        <title>Chronicle.Seven.Realms.S01E01.German.DL.1080p.WEB.H265-CLEANPOST</title>
                        <guid>http://mirror-nzb.local/details/clean-777?apikey=MIRROR_KEY_777</guid>
                        <link>http://mirror-nzb.local/get/clean-777.nzb?apikey=MIRROR_KEY_777</link>
                        <pubDate>Mon, 02 Nov 2026 20:30:00 +0000</pubDate>
                        <enclosure url="http://mirror-nzb.local/get/clean-777.nzb?apikey=MIRROR_KEY_777" length="1650000000" type="application/x-nzb" />
                        <newznab:attr name="category" value="5040" />
                        <newznab:attr name="tvdbid" value="450001" />
                      </item>
                      """
                    : "";

                var mirrorXml = $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
                      <channel>
                        <title>Mirror Slug NZB</title>
                        <item>
                          <title>Chronicle_Seven_Realms_S01E01_German_DL_1080p_WEB_H265_BROKEN</title>
                          <guid>http://mirror-nzb.local/details/mirror-dup-1?apikey=MIRROR_KEY_777</guid>
                          <link>http://mirror-nzb.local/get/mirror-dup-1.nzb?apikey=MIRROR_KEY_777</link>
                          <pubDate>Mon, 02 Nov 2026 19:45:00 +0000</pubDate>
                          <enclosure url="http://mirror-nzb.local/get/mirror-dup-1.nzb?apikey=MIRROR_KEY_777" length="2345600000" type="application/x-nzb" />
                          <newznab:attr name="category" value="5040" />
                          <newznab:attr name="tvdbid" value="450001" />
                        </item>
                        {healthyItem}
                      </channel>
                    </rss>
                    """;
                return Task.FromResult(mirrorXml);
            }

            const string primaryXml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
                  <channel>
                    <title>Primary Geek NZB</title>
                    <item>
                      <title>Chronicle.Seven.Realms.S01E01.German.DL.1080p.WEB.H265-BROKEN</title>
                      <guid>http://primary-nzb.local/details/99887766?apikey=ROTATED_NEW_KEY_999</guid>
                      <link>http://primary-nzb.local/api?t=get&amp;id=99887766&amp;apikey=ROTATED_NEW_KEY_999</link>
                      <pubDate>Mon, 02 Nov 2026 19:00:00 +0000</pubDate>
                      <enclosure url="http://primary-nzb.local/api?t=get&amp;id=99887766&amp;apikey=ROTATED_NEW_KEY_999" length="2345678900" type="application/x-nzb" />
                      <newznab:attr name="category" value="5040" />
                      <newznab:attr name="tvdbid" value="450001" />
                    </item>
                    <item>
                      <title>Chronicle.Seven.Realms.S01E01.German.DL.1080p.WEB.H265-OBFUSCATED99</title>
                      <guid>http://primary-nzb.local/details/obfuscated-repost-42?apikey=ROTATED_NEW_KEY_999</guid>
                      <link>http://primary-nzb.local/api?t=get&amp;id=424242&amp;apikey=ROTATED_NEW_KEY_999</link>
                      <pubDate>Mon, 02 Nov 2026 19:00:00 +0000</pubDate>
                      <enclosure url="http://primary-nzb.local/api?t=get&amp;id=424242&amp;apikey=ROTATED_NEW_KEY_999" length="2345678900" type="application/x-nzb" />
                      <newznab:attr name="category" value="5040" />
                      <newznab:attr name="tvdbid" value="450001" />
                    </item>
                  </channel>
                </rss>
                """;
            return Task.FromResult(primaryXml);
        }
    }
}
