using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Managerr.Catalog;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
using Managerr.Host.Services;
using Managerr.Indexers;
using Managerr.Metadata;
using Managerr.Profiles;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Managerr.Phase5.Tests;

public sealed class Wave2WantedAndActivityTests
{
    [Fact]
    public async Task WantedMissingAndCutoffUnmet_BulkSearch_InteractiveRejectionBreakdown_AndForceGrab()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-wave2-wanted-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var tvRoot = Path.Combine(tempDir, "storage", "media", "tv");
            Directory.CreateDirectory(tvRoot);

            var driverGateway = new Wave2FakeDriverGateway();
            var indexerGateway = new Wave2FakeTorznabGateway();

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                        ["Managerr:MasterKey"] = "wave2-wanted-test-master-key-32b",
                        ["Managerr:SchedulerEnabled"] = "false",
                        ["Managerr:TestbedBootstrap:Enabled"] = "true",
                        ["Managerr:TestbedBootstrap:Username"] = "testadmin",
                        ["Managerr:TestbedBootstrap:Password"] = "Wave2AdminPass!2026",
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
                Title: "Avatar: Seven Havens",
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
                            new MetadataUnitSeed("episode", 1, 1, "Pilot", "2026-02-01"),
                            new MetadataUnitSeed("episode", 2, 2, "The Awakening", "2026-02-08"),
                            new MetadataUnitSeed("episode", 3, 3, "Low Quality Copy", "2026-02-15"),
                            new MetadataUnitSeed("episode", 4, 4, "Unmonitored Bonus", "2026-02-22"),
                        ]),
                ]));

            var units = workTree.Containers[0].Units;
            var ep1 = units.Single(u => u.UnitNumber == 1);
            var ep3 = units.Single(u => u.UnitNumber == 3);
            var ep4 = units.Single(u => u.UnitNumber == 4);

            await catalog.UpdateUnitAsync(ep4.Id, new UpdateMediaUnitRequest(Monitored: false));
            await catalog.AttachFileAsync(new AttachMediaFileRequest(
                UnitId: ep3.Id,
                RelativePath: "Avatar Seven Havens/Season 01/S01E03.mkv",
                SizeBytes: 500_000_000,
                QualityName: "HDTV-720p",
                DetectedAudioLangs: ["en"]));
            await catalog.UpdateUnitAsync(ep3.Id, new UpdateMediaUnitRequest(
                QualityCutoffMet: false,
                LanguageCutoffMet: false));

            await indexers.CreateIndexerAsync(new UpsertIndexerRequest(
                Name: "Wave2 Torznab",
                Implementation: "torznab",
                DefinitionId: null,
                Protocol: "torrent",
                BaseUrl: "http://torznab.wave2.local",
                ApiKey: "wave2-key",
                Categories: [5000, 5040]));

            await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "qBittorrent Wave2",
                Implementation: "qbittorrent",
                Host: "qbit-wave2",
                Port: 8080,
                Username: "admin",
                Password: "secret"));

            await orchestrator.BlocklistReleaseHashAsync(
                "badhash11111111111111111111111111111111",
                "Avatar.Seven.Havens.S01E01.1080p.WEB.H264-BLOCKED",
                "stalled_download");

            using var client = await CreateAuthenticatedClientAsync(factory, "Wave2AdminPass!2026");

            // 1. Wanted Missing (monitoredOnly=true -> ep1 & ep2)
            var missingResp = await client.GetFromJsonAsync<WantedPagedResponse<WantedUnitItemDto>>(
                "/api/v1/wanted/missing?monitoredOnly=true");
            Assert.NotNull(missingResp);
            Assert.Equal(2, missingResp.TotalCount);
            Assert.Contains(missingResp.Items, i => i.EpisodeCode == "S01E01");
            Assert.Contains(missingResp.Items, i => i.EpisodeCode == "S01E02");

            // 2. Wanted Cutoff Unmet -> ep3
            var cutoffResp = await client.GetFromJsonAsync<WantedPagedResponse<WantedUnitItemDto>>(
                "/api/v1/wanted/cutoff-unmet?monitoredOnly=true");
            Assert.NotNull(cutoffResp);
            Assert.Single(cutoffResp.Items);
            Assert.Equal("S01E03", cutoffResp.Items[0].EpisodeCode);
            Assert.Equal("QualityAndLanguageCutoffUnmet", cutoffResp.Items[0].CutoffUnmetReason);

            // 3. Interactive Search Rejection Breakdown via GET /api/v1/catalog/works/{id}/releases
            var releases = await client.GetFromJsonAsync<List<EnrichedInteractiveReleaseRow>>(
                $"/api/v1/catalog/works/{workTree.Work.Id}/releases?unitId={ep1.Id}");
            Assert.NotNull(releases);
            Assert.True(releases.Count >= 2);

            var blockedRow = releases.Single(r => r.Release.InfoHash == "badhash11111111111111111111111111111111");
            Assert.True(blockedRow.IsBlocklisted);
            Assert.False(blockedRow.Approved);
            Assert.Contains(blockedRow.Rejections, msg => msg.Contains("Blocklisted", StringComparison.OrdinalIgnoreCase));

            var goodRow = releases.First(r => r.Release.Title.Contains("German.DL.1080p", StringComparison.OrdinalIgnoreCase));
            Assert.True(goodRow.Approved);
            Assert.True(goodRow.TotalWeightedScore > 0);
            Assert.Contains("Freeleech", goodRow.IndexerFlags);

            // 4. Bulk Wanted Search ("selected" on ep1)
            var bulkSearchResp = await client.PostAsJsonAsync("/api/v1/wanted/search", new WantedBulkSearchRequest(
                Mode: "selected",
                UnitIds: [ep1.Id]));
            Assert.Equal(HttpStatusCode.OK, bulkSearchResp.StatusCode);
            var bulkReport = await bulkSearchResp.Content.ReadFromJsonAsync<WantedBulkSearchReport>();
            Assert.NotNull(bulkReport);
            Assert.Equal(1, bulkReport.GrabbedCount);
            Assert.Contains("German.DL.1080p", bulkReport.Outcomes[0].SelectedRelease);

            // A second automatic search leaves ep1 alone while that download is in flight (#64)
            var repeatSearchResp = await client.PostAsJsonAsync("/api/v1/wanted/search", new WantedBulkSearchRequest(
                Mode: "selected",
                UnitIds: [ep1.Id]));
            var repeatReport = (await repeatSearchResp.Content.ReadFromJsonAsync<WantedBulkSearchReport>())!;
            Assert.Equal(0, repeatReport.GrabbedCount);
            Assert.Contains(repeatReport.Outcomes, o => o.Message.Contains("still in flight", StringComparison.Ordinal));

            // 5. Normal grab on blocklisted release fails, an interactive ForceGrab succeeds even while ep1 is in flight
            var blockedGrabResp = await client.PostAsJsonAsync("/api/v1/downloaders/grab", new GrabReleaseRequest(
                Title: blockedRow.Release.Title,
                Protocol: "torrent",
                MediaVertical: "tv",
                DownloadUrlOrMagnet: blockedRow.Release.DownloadUrl,
                InfoHash: blockedRow.Release.InfoHash,
                WorkId: workTree.Work.Id,
                UnitIds: [ep1.Id],
                ForceGrab: false));
            Assert.False(blockedGrabResp.IsSuccessStatusCode);

            var forceGrabResp = await client.PostAsJsonAsync("/api/v1/downloaders/grab", new GrabReleaseRequest(
                Title: blockedRow.Release.Title,
                Protocol: "torrent",
                MediaVertical: "tv",
                DownloadUrlOrMagnet: blockedRow.Release.DownloadUrl,
                InfoHash: blockedRow.Release.InfoHash,
                WorkId: workTree.Work.Id,
                UnitIds: [ep1.Id],
                ForceGrab: true));
            Assert.Equal(HttpStatusCode.OK, forceGrabResp.StatusCode);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task ActivityQueueControls_MediaHistoryMarkFailed_BlocklistCrud_AndManualFolderImport()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-wave2-activity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var tvRoot = Path.Combine(tempDir, "storage", "media", "tv");
            var manualStagingDir = Path.Combine(tempDir, "storage", "manual-drop");
            Directory.CreateDirectory(tvRoot);
            Directory.CreateDirectory(manualStagingDir);

            var autoMatchedMkv = Path.Combine(manualStagingDir, "Avatar.Seven.Havens.S01E02.German.DL.1080p.WEB.H264.mkv");
            var mysteryMkv = Path.Combine(manualStagingDir, "Mystery_Episode_RawRip_1080p.mkv");
            await File.WriteAllBytesAsync(autoMatchedMkv, new byte[12_288]);
            await File.WriteAllBytesAsync(mysteryMkv, new byte[12_288]);

            var driverGateway = new Wave2FakeDriverGateway();
            var indexerGateway = new Wave2FakeTorznabGateway();

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                        ["Managerr:MasterKey"] = "wave2-activity-test-master-key32",
                        ["Managerr:SchedulerEnabled"] = "false",
                        ["Managerr:TestbedBootstrap:Enabled"] = "true",
                        ["Managerr:TestbedBootstrap:Username"] = "testadmin",
                        ["Managerr:TestbedBootstrap:Password"] = "Wave2AdminPass!2026",
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
                Title: "Avatar: Seven Havens",
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
                            new MetadataUnitSeed("episode", 1, 1, "Pilot", "2026-02-01"),
                            new MetadataUnitSeed("episode", 2, 2, "The Awakening", "2026-02-08"),
                            new MetadataUnitSeed("episode", 4, 4, "Manual Target", "2026-02-22"),
                        ]),
                ]));

            var ep1 = workTree.Containers[0].Units.Single(u => u.UnitNumber == 1);
            var ep2 = workTree.Containers[0].Units.Single(u => u.UnitNumber == 2);
            var ep4 = workTree.Containers[0].Units.Single(u => u.UnitNumber == 4);

            await indexers.CreateIndexerAsync(new UpsertIndexerRequest(
                Name: "Wave2 Torznab",
                Implementation: "torznab",
                DefinitionId: null,
                Protocol: "torrent",
                BaseUrl: "http://torznab.wave2.local",
                ApiKey: "wave2-key",
                Categories: [5000, 5040]));

            var qbit = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "qBittorrent Wave2",
                Implementation: "qbittorrent",
                Host: "qbit-wave2",
                Port: 8080,
                Username: "admin",
                Password: "secret"));

            var initialGrab = await orchestrator.GrabReleaseAsync(new GrabReleaseRequest(
                Title: "Avatar.Seven.Havens.S01E01.720p.WEBRip-STALLED",
                Protocol: "torrent",
                MediaVertical: "tv",
                DownloadUrlOrMagnet: "http://torznab.wave2.local/dl/stalled.torrent",
                InfoHash: "stalledhash22222222222222222222222222222",
                WorkId: workTree.Work.Id,
                ContainerId: workTree.Containers[0].Id,
                UnitIds: [ep1.Id]));

            using var client = await CreateAuthenticatedClientAsync(factory, "Wave2AdminPass!2026");

            // 1. Real-time Queue Controls (pause, resume, top_priority)
            foreach (var action in new[] { "pause", "resume", "top_priority" })
            {
                var ctrlResp = await client.PostAsJsonAsync(
                    $"/api/v1/downloaders/queue/{qbit.Id}/{Uri.EscapeDataString(initialGrab.ExternalDownloadId)}/control",
                    new QueueControlRequest(action));
                Assert.Equal(HttpStatusCode.OK, ctrlResp.StatusCode);
            }

            Assert.Equal(["pause", "resume", "top_priority"], driverGateway.ControlActions);

            // 2. Remove Queue Item with Blocklist + Replacement Search
            var delResp = await client.DeleteAsync(
                $"/api/v1/downloaders/queue/{qbit.Id}/{Uri.EscapeDataString(initialGrab.ExternalDownloadId)}?removeFromClient=true&deleteData=true&blocklist=true&searchReplacement=true");
            Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);
            var delOutcome = await delResp.Content.ReadFromJsonAsync<RemoveQueueItemOutcome>();
            Assert.NotNull(delOutcome);
            Assert.True(delOutcome.RemovedFromClient);
            Assert.True(delOutcome.Blocklisted);
            Assert.True(delOutcome.ReplacementGrabbed);

            // 3. Media History Ledger & Mark as Failed
            var histPage = await client.GetFromJsonAsync<WantedPagedResponse<MediaHistoryItemDto>>("/api/v1/activity/history");
            Assert.NotNull(histPage);
            Assert.Contains(histPage.Items, h => h.EventType == MediaHistoryEventTypes.Grabbed);
            Assert.Contains(histPage.Items, h => h.EventType == MediaHistoryEventTypes.DownloadFailed);

            var grabbedHist = histPage.Items.First(h => h.EventType == MediaHistoryEventTypes.Grabbed);
            var markFailedResp = await client.PostAsJsonAsync(
                $"/api/v1/activity/history/{grabbedHist.Id}/mark-failed",
                new MarkHistoryFailedRequest(SearchReplacement: false));
            Assert.Equal(HttpStatusCode.OK, markFailedResp.StatusCode);

            // 4. Blocklist CRUD (single delete & bulk clear)
            var blocklist = await client.GetFromJsonAsync<List<RecurringBlockedHashEntry>>("/api/v1/activity/blocklist");
            Assert.NotNull(blocklist);
            Assert.True(blocklist.Count >= 2);

            var firstHash = blocklist[0].InfoHash;
            var singleDel = await client.DeleteAsync($"/api/v1/activity/blocklist/{Uri.EscapeDataString(firstHash)}");
            Assert.Equal(HttpStatusCode.NoContent, singleDel.StatusCode);

            var bulkClear = await client.PostAsJsonAsync(
                "/api/v1/activity/blocklist/bulk-delete",
                new BulkDeleteBlocklistRequest(ClearAll: true));
            Assert.Equal(HttpStatusCode.OK, bulkClear.StatusCode);
            var afterClear = await client.GetFromJsonAsync<List<RecurringBlockedHashEntry>>("/api/v1/activity/blocklist");
            Assert.NotNull(afterClear);
            Assert.Empty(afterClear);

            // 5. Interactive Manual Folder Import (Scan + Override Import)
            var scanItems = await client.GetFromJsonAsync<List<ManualImportCandidateDto>>(
                $"/api/v1/downloaders/manual-import?folder={Uri.EscapeDataString(manualStagingDir)}");
            Assert.NotNull(scanItems);
            Assert.Equal(2, scanItems.Count);

            var scannedEp2 = scanItems.Single(s => s.FileName.Contains("S01E02", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(workTree.Work.Id, scannedEp2.SuggestedWorkId);
            Assert.Equal([2], scannedEp2.SuggestedEpisodeNumbers);
            Assert.Empty(scannedEp2.Rejections);

            var scannedMystery = scanItems.Single(s => s.FileName.Contains("Mystery", StringComparison.OrdinalIgnoreCase));
            Assert.NotEmpty(scannedMystery.Rejections);

            var importExecResp = await client.PostAsJsonAsync("/api/v1/downloaders/manual-import", new ExecuteManualFolderImportRequest(
                Items:
                [
                    new ManualFolderImportItemRequest(
                        SourceFilePath: scannedEp2.FullPath,
                        WorkId: workTree.Work.Id,
                        SeasonNumber: 1,
                        EpisodeNumbers: [2],
                        QualityName: "WEBDL-1080p",
                        Languages: ["de", "en"]),
                    new ManualFolderImportItemRequest(
                        SourceFilePath: scannedMystery.FullPath,
                        WorkId: workTree.Work.Id,
                        SeasonNumber: 1,
                        EpisodeNumbers: [4],
                        QualityName: "WEBDL-1080p",
                        Languages: ["en"]),
                ],
                ImportMode: "hardlink_or_copy"));

            Assert.Equal(HttpStatusCode.OK, importExecResp.StatusCode);
            var importReport = await importExecResp.Content.ReadFromJsonAsync<ExecuteManualFolderImportReport>();
            Assert.NotNull(importReport);
            Assert.Equal(2, importReport.ImportedCount);
            Assert.Equal(0, importReport.FailedCount);

            var refreshedTree = await catalog.GetWorkTreeAsync(workTree.Work.Id);
            Assert.Single(refreshedTree!.Containers[0].Units.Single(u => u.Id == ep2.Id).Files);
            Assert.Single(refreshedTree.Containers[0].Units.Single(u => u.Id == ep4.Id).Files);

            var finalHist = await client.GetFromJsonAsync<WantedPagedResponse<MediaHistoryItemDto>>(
                "/api/v1/activity/history?eventType=DownloadFolderImported");
            Assert.NotNull(finalHist);
            Assert.Equal(2, finalHist.TotalCount);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(WebApplicationFactory<Program> factory, string password)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/breakglass/login", new
        {
            usernameOrEmail = "testadmin",
            password,
        });
        loginResp.EnsureSuccessStatusCode();
        var body = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
        if (body.TryGetProperty("csrfToken", out var csrf) && csrf.GetString() is { Length: > 0 } token)
        {
            client.DefaultRequestHeaders.Add("X-Managerr-Csrf", token);
        }

        return client;
    }

    private sealed class Wave2FakeDriverGateway : IDownloadClientDriverGateway
    {
        private readonly List<ActiveDownloadQueueItem> _queue = [];
        public List<string> ControlActions { get; } = [];

        public IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers() => DownloadClientCatalog.All;

        public Task<string> EnqueueReleaseAsync(
            DownloadClientConnectionSpec client,
            string releaseTitle,
            string downloadUrlOrMagnet,
            string resolvedCategory,
            CancellationToken ct = default)
        {
            var extId = $"ext-{Guid.NewGuid():N}"[..16];
            _queue.Add(new ActiveDownloadQueueItem(
                ClientId: client.Id,
                ClientName: client.Name,
                Protocol: client.Protocol,
                ExternalId: extId,
                InfoHash: null,
                Title: releaseTitle,
                Category: resolvedCategory,
                MediaVertical: "tv",
                Status: "Downloading",
                TotalBytes: 1_500_000_000,
                RemainingBytes: 750_000_000,
                OutputPath: "",
                ContainedFiles: [],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: false));
            return Task.FromResult(extId);
        }

        public Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(
            DownloadClientConnectionSpec client,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActiveDownloadQueueItem>>(_queue.Where(q => q.ClientId == client.Id).ToList());

        public Task RemoveDownloadAsync(
            DownloadClientConnectionSpec client,
            string externalId,
            bool deleteData,
            CancellationToken ct = default)
        {
            _queue.RemoveAll(q => q.ClientId == client.Id && q.ExternalId == externalId);
            return Task.CompletedTask;
        }

        public Task ControlDownloadAsync(
            DownloadClientConnectionSpec client,
            string externalId,
            string action,
            CancellationToken ct = default)
        {
            ControlActions.Add(action);
            return Task.CompletedTask;
        }
    }

    private sealed class Wave2FakeTorznabGateway : IIndexerHttpGateway
    {
        public Task<string> FetchStringAsync(Uri targetUri, IndexerProxyInternal? matchedProxy, CancellationToken ct = default)
        {
            var nowRfc = DateTimeOffset.UtcNow.AddHours(-3).ToString("R");
            var xml = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
                  <channel>
                    <title>Wave2 Torznab</title>
                    <item>
                      <title>Avatar.Seven.Havens.S01E01.1080p.WEB.H264-BLOCKED</title>
                      <guid>guid-blocked-s01e01</guid>
                      <link>http://torznab.wave2.local/dl/blocked.torrent</link>
                      <pubDate>{nowRfc}</pubDate>
                      <size>1400000000</size>
                      <torznab:attr name="category" value="5040" />
                      <torznab:attr name="infohash" value="badhash11111111111111111111111111111111" />
                      <torznab:attr name="seeders" value="25" />
                      <torznab:attr name="peers" value="30" />
                    </item>
                    <item>
                      <title>Avatar.Seven.Havens.S01E01.German.DL.1080p.WEB-DL.H265-GOOD</title>
                      <guid>guid-good-s01e01</guid>
                      <link>http://torznab.wave2.local/dl/good.torrent</link>
                      <pubDate>{nowRfc}</pubDate>
                      <size>1850000000</size>
                      <torznab:attr name="category" value="5040" />
                      <torznab:attr name="infohash" value="goodhash99999999999999999999999999999999" />
                      <torznab:attr name="seeders" value="80" />
                      <torznab:attr name="peers" value="85" />
                      <torznab:attr name="downloadvolumefactor" value="0" />
                      <torznab:attr name="uploadvolumefactor" value="2" />
                    </item>
                  </channel>
                </rss>
                """;
            return Task.FromResult(xml);
        }
    }
}
