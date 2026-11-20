using System.Text.Json.Nodes;
using Managerr.Catalog;
using Managerr.Datastore.Services;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
using Managerr.Host.Services;
using Managerr.Metadata;
using Managerr.Verification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Managerr.Phase5.Tests;

public sealed class Phase7LivePlexAndImportTests
{
    [Fact]
    public async Task CompletedSabnzbdImports_MalwareKillAndBlocklist_PlusVerifiedHardlinkImport()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-p7-sab-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var moviesRoot = Path.Combine(tempDir, "storage", "media", "movies");
            var completeRoot = Path.Combine(tempDir, "storage", "usenet", "complete");
            var goodJobDir = Path.Combine(completeRoot, "Neon.Horizon.2025.German.DL.1080p.BluRay.x265-TESTLAB");
            var malwareJobDir = Path.Combine(completeRoot, "Blockbuster.Cam.2026.1080p.BluRay.x264-MALWARE");
            Directory.CreateDirectory(moviesRoot);
            Directory.CreateDirectory(goodJobDir);
            Directory.CreateDirectory(malwareJobDir);

            var goodMkv = Path.Combine(goodJobDir, "Neon.Horizon.2025.German.DL.1080p.BluRay.x265-TESTLAB.mkv");
            await File.WriteAllBytesAsync(goodMkv, new byte[8192]);
            await File.WriteAllBytesAsync(Path.Combine(malwareJobDir, "CodecUpdater.exe"), new byte[1024]);
            await File.WriteAllBytesAsync(Path.Combine(malwareJobDir, "Movie.mkv"), new byte[2048]);

            var fakeSabDriver = new FakeSabnzbdDriverGateway(goodJobDir, malwareJobDir);
            var fakeProbe = new FakeGermanPassContainerProbe();

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                        ["Managerr:MasterKey"] = "phase7-sabnzbd-master-key-32byte",
                    });
                });
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<IDownloadClientDriverGateway>();
                    services.AddSingleton<IDownloadClientDriverGateway>(fakeSabDriver);
                    services.RemoveAll<IContainerProbeAdapter>();
                    services.AddSingleton<IContainerProbeAdapter>(fakeProbe);
                });
            });

            using var scope = factory.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var importSvc = scope.ServiceProvider.GetRequiredService<ICompletedDownloadImportService>();

            await catalog.CreateLibraryAsync(new CreateLibraryRequest(
                Name: "Movies",
                MediaType: "movies",
                RootFolderPath: moviesRoot));

            await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd Test",
                Implementation: "sabnzbd",
                Host: "sab-test",
                Port: 8080,
                ApiKey: "test-sab-key"));

            var report = await importSvc.ProcessCompletedDownloadsAsync();
            Assert.Equal(2, report.EvaluatedCount);
            Assert.Equal(1, report.ImportedCount);
            Assert.Equal(1, report.RejectedCount);

            var malwareOutcome = report.Items.Single(i => i.ExternalDownloadId == "SABnzbd_nzo_malware");
            Assert.Equal("BlockedMalware", malwareOutcome.Verdict);
            Assert.Contains("SABnzbd_nzo_malware", fakeSabDriver.RemovedExternalIds);

            var blocklist = await orchestrator.ListBlocklistedHashesAsync();
            Assert.Contains(blocklist, b => b.InfoHash == "nzo:sabnzbd_nzo_malware");

            var goodOutcome = report.Items.Single(i => i.ExternalDownloadId == "SABnzbd_nzo_good");
            Assert.Equal(VerificationVerdicts.VerifiedPass, goodOutcome.Verdict);
            // Default import transfer mode is atomic move (UseHardlinks = false)
            Assert.False(goodOutcome.Hardlinked);
            Assert.NotNull(goodOutcome.DestinationFilePath);
            Assert.True(File.Exists(goodOutcome.DestinationFilePath!));
            // Source file was moved into the library destination
            Assert.False(File.Exists(goodMkv));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task SeriesSpecials_AreAllocatedToSeriesSeason00_AndHardlinkOptionWorks()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-p7-specials-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var animeRoot = Path.Combine(tempDir, "storage", "media", "anime");
            var completeRoot = Path.Combine(tempDir, "storage", "usenet", "complete");
            var specialJobDir = Path.Combine(completeRoot, "[SubsPlease] Bleach - S00E01 (1080p)");
            Directory.CreateDirectory(animeRoot);
            Directory.CreateDirectory(specialJobDir);

            var specialMkv = Path.Combine(specialJobDir, "[SubsPlease] Bleach - S00E01 - Memories in the Rain (1080p).mkv");
            await File.WriteAllBytesAsync(specialMkv, new byte[8192]);

            var fakeSabDriver = new FakeSingleItemSabDriverGateway(
                externalId: "SABnzbd_nzo_bleach_special",
                title: "[SubsPlease] Bleach - S00E01 (1080p)",
                category: "anime-managerr",
                vertical: "anime",
                outputDir: specialJobDir);
            var fakeProbe = new FakeGermanPassContainerProbe();

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                        ["Managerr:MasterKey"] = "phase7-specials-master-key-32byt",
                    });
                });
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<IDownloadClientDriverGateway>();
                    services.AddSingleton<IDownloadClientDriverGateway>(fakeSabDriver);
                    services.RemoveAll<IContainerProbeAdapter>();
                    services.AddSingleton<IContainerProbeAdapter>(fakeProbe);
                });
            });

            using var scope = factory.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var importSvc = scope.ServiceProvider.GetRequiredService<ICompletedDownloadImportService>();
            var namingSvc = scope.ServiceProvider.GetRequiredService<IMediaManagementAndDiskScanService>();

            // Enable hardlinks explicitly to test the toggle
            var updatedNaming = await namingSvc.UpdateNamingConfigAsync(new UpdateNamingConfigRequest(UseHardlinks: true));
            Assert.True(updatedNaming.UseHardlinks);

            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest(
                Name: "Anime",
                MediaType: "anime",
                RootFolderPath: animeRoot));

            // Create the Bleach series work with only Season 1 initially (no Season 0 yet)
            var seriesWork = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "Bleach",
                WorkKind: "anime",
                PrimaryProvider: "tvdb",
                AltTitles: ["Bleach"],
                OriginalLanguage: "en",
                Year: 2004,
                Status: "Ended",
                Overview: "Soul Reaper series",
                PosterUrl: null,
                Monitored: true,
                ExternalIds: new Dictionary<string, string> { ["tvdb"] = "74796", ["anidb"] = "2369" },
                Containers:
                [
                    new MetadataContainerSeed(
                        ContainerKind: "season",
                        ContainerNumber: 1,
                        Title: "Season 1",
                        ReleaseDate: "2004-10-05",
                        Units:
                        [
                            new MetadataUnitSeed("episode", 1, 1, "The Day I Became a Shinigami", "2004-10-05"),
                        ]),
                ]));

            await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd Anime",
                Implementation: "sabnzbd",
                Host: "sab-test",
                Port: 8080,
                ApiKey: "test-sab-key"));

            var report = await importSvc.ProcessCompletedDownloadsAsync();
            Assert.Equal(1, report.ImportedCount);

            var outcome = Assert.Single(report.Items);
            Assert.Equal(VerificationVerdicts.VerifiedPass, outcome.Verdict);
            Assert.True(outcome.Hardlinked);
            Assert.NotNull(outcome.DestinationFilePath);
            Assert.Contains(Path.Combine("Bleach", "Season 00"), outcome.DestinationFilePath!);
            Assert.Contains("S00E01", Path.GetFileName(outcome.DestinationFilePath!));

            // Verify Specials were allocated to the existing Bleach series work (no duplicate work created)
            var allWorks = await catalog.ListWorksAsync(null, lib.Id);
            Assert.Single(allWorks);

            var detail = await catalog.GetWorkTreeAsync(seriesWork.Work.Id);
            Assert.NotNull(detail);
            var specialsContainer = Assert.Single(detail!.Containers, c => c.ContainerNumber == 0);
            Assert.Equal("Specials", specialsContainer.Title);
            var specialUnit = Assert.Single(specialsContainer.Units, u => u.UnitNumber == 1);
            Assert.Single(specialUnit.Files);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private sealed class FakeSingleItemSabDriverGateway(
        string externalId,
        string title,
        string category,
        string vertical,
        string outputDir) : IDownloadClientDriverGateway
    {
        public IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers() => DownloadClientCatalog.All;

        public Task<string> EnqueueReleaseAsync(DownloadClientConnectionSpec client, string releaseTitle, string downloadUrlOrMagnet, string resolvedCategory, CancellationToken ct = default) =>
            Task.FromResult(externalId);

        public Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(DownloadClientConnectionSpec client, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActiveDownloadQueueItem>>(
            [
                new(
                    ClientId: client.Id,
                    ClientName: client.Name,
                    Protocol: "usenet",
                    ExternalId: externalId,
                    InfoHash: $"nzo:{externalId}",
                    Title: title,
                    Category: category,
                    MediaVertical: vertical,
                    Status: "Completed",
                    TotalBytes: 8192,
                    RemainingBytes: 0,
                    OutputPath: outputDir,
                    ContainedFiles: [],
                    IsStalled: false,
                    IsMetadataStuck: false,
                    ErrorMessage: null),
            ]);

        public Task RemoveDownloadAsync(DownloadClientConnectionSpec client, string externalId, bool deleteData, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeSabnzbdDriverGateway(string goodDir, string malwareDir) : IDownloadClientDriverGateway
    {
        public List<string> RemovedExternalIds { get; } = [];

        public IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers() => DownloadClientCatalog.All;

        public Task<string> EnqueueReleaseAsync(DownloadClientConnectionSpec client, string releaseTitle, string downloadUrlOrMagnet, string resolvedCategory, CancellationToken ct = default) =>
            Task.FromResult("SABnzbd_nzo_test");

        public Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(DownloadClientConnectionSpec client, CancellationToken ct = default)
        {
            IReadOnlyList<ActiveDownloadQueueItem> items =
            [
                new(
                    ClientId: client.Id,
                    ClientName: client.Name,
                    Protocol: "usenet",
                    ExternalId: "SABnzbd_nzo_malware",
                    InfoHash: "nzo:SABnzbd_nzo_malware",
                    Title: "Blockbuster.Cam.2026.1080p.BluRay.x264-MALWARE",
                    Category: "movies-managerr",
                    MediaVertical: "movies",
                    Status: "Completed",
                    TotalBytes: 3072,
                    RemainingBytes: 0,
                    OutputPath: malwareDir,
                    ContainedFiles: [],
                    IsStalled: false,
                    IsMetadataStuck: false,
                    ErrorMessage: null),
                new(
                    ClientId: client.Id,
                    ClientName: client.Name,
                    Protocol: "usenet",
                    ExternalId: "SABnzbd_nzo_good",
                    InfoHash: "nzo:SABnzbd_nzo_good",
                    Title: "Neon.Horizon.2025.German.DL.1080p.BluRay.x265-TESTLAB",
                    Category: "movies-managerr",
                    MediaVertical: "movies",
                    Status: "Completed",
                    TotalBytes: 8192,
                    RemainingBytes: 0,
                    OutputPath: goodDir,
                    ContainedFiles: [],
                    IsStalled: false,
                    IsMetadataStuck: false,
                    ErrorMessage: null),
            ];
            return Task.FromResult(items);
        }

        public Task RemoveDownloadAsync(DownloadClientConnectionSpec client, string externalId, bool deleteData, CancellationToken ct = default)
        {
            RemovedExternalIds.Add(externalId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGermanPassContainerProbe : IContainerProbeAdapter
    {
        public Task<ContainerProbeResult> ProbeAsync(string filePath, double? expectedDurationSeconds = null, CancellationToken ct = default)
        {
            var germanSrt = string.Join(
                "\n\n",
                Enumerable.Range(1, 120).Select(i => $"{i}\n00:00:{i % 59:D2},000 --> 00:00:{(i % 59) + 1:D2},000\nDas ist ein deutscher Film und wir gehen heute nach Hause in die Stadt."));
            return Task.FromResult(new ContainerProbeResult(
                IntegrityPassed: true,
                IntegrityFailureReason: null,
                ContainerFormat: "matroska",
                DurationSeconds: 120,
                Streams:
                [
                    new ProbedRawStream(0, "audio", "eac3", 6, "de", "Deutsch DD+ 5.1", AudioWaveformPreview: [0.2f, -0.3f, 0.5f, -0.1f, 0.4f]),
                    new ProbedRawStream(1, "audio", "eac3", 6, "en", "English DD+ 5.1", AudioWaveformPreview: [-0.4f, 0.1f, 0.2f, -0.5f, 0.1f]),
                    new ProbedRawStream(2, "subtitle", "subrip", 0, "de", "German Full", InlineTextPayload: germanSrt),
                ]));
        }
    }
}
