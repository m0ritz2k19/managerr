using Managerr.Catalog;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
using Managerr.Downloaders.Parser;
using Managerr.Host.Services;
using Managerr.Indexers;
using Managerr.Indexers.Torznab;
using Managerr.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Managerr.Phase5.Tests;

public sealed class RepullReleaseIdentityTests
{
    [Theory]
    [InlineData("[Naruto-Kun.Hu].One-Room.TA.S01E02.[1080p]", false)]
    [InlineData("Naruto.2002.S01E035.480p.NF.WEB-DL.H.264.AAC.2Audios-UBWEB", true)]
    [InlineData("[uP] Naruto - 030 (BDRip 1080p x264 AC3 Multi).mkv", true)]
    [InlineData("Naruto - 001 [Triple-Audio][Multi-Sub][HEVC 10Bits] 800p", true)]
    [InlineData("Naruto.Shippuden.S01E02.1080p.WEB-DL-GRP", false)]
    public void ParsedReleaseTitle_MustMatchWorkTitleOrAlias(string releaseTitle, bool expected)
    {
        var parsed = new ReleaseParserRouter().Parse(releaseTitle, "anime");

        Assert.Equal(expected, ReleaseTitleNormalizer.MatchesWork(parsed.CleanTitle, "Naruto", ["ナルト"], 2002));
    }

    [Fact]
    public async Task FailedEpisodeRepull_SkipsLargerReleaseOfDifferentSeries_AndGrabsMatchingEpisode()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-repull-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "storage", "media", "tv"));

        try
        {
            var gateway = new RecordingDriverGateway();
            var indexer = new FixedResultsIndexerService(
            [
                Candidate("[Naruto-Kun.Hu].One-Room.TA.S01E02.[1080p]", sizeBytes: 9_000_000_000),
                Candidate("Naruto.2002.S01E002.1080p.WEB-DL.H.264-GRP", sizeBytes: 1_000_000_000),
            ]);

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                        ["Managerr:MasterKey"] = "repull-identity-test-master-key-32by",
                        ["Managerr:SchedulerEnabled"] = "false",
                    });
                });
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<IDownloadClientDriverGateway>();
                    services.AddSingleton<IDownloadClientDriverGateway>(gateway);
                    services.RemoveAll<IUnifiedIndexerService>();
                    services.AddSingleton<IUnifiedIndexerService>(indexer);
                });
            });

            using var scope = factory.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var wanted = scope.ServiceProvider.GetRequiredService<IWantedAndActivityService>();

            var tvLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest(
                Name: "Anime",
                MediaType: "tv",
                RootFolderPath: Path.Combine(tempDir, "storage", "media", "tv")));

            var naruto = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: "Naruto",
                WorkKind: "series",
                AltTitles: [],
                OriginalLanguage: "ja",
                Year: 2002,
                Status: "ended",
                Monitored: true,
                ExternalIds: new Dictionary<string, string> { ["tvdb"] = "78857" },
                Containers:
                [
                    new MetadataContainerSeed(
                        ContainerKind: "season",
                        ContainerNumber: 1,
                        Title: "Season 1",
                        ReleaseDate: "2002-10-03",
                        Units:
                        [
                            new MetadataUnitSeed("episode", 1, 1, "Enter: Naruto Uzumaki!", "2002-10-03"),
                            new MetadataUnitSeed("episode", 2, 2, "My Name is Konohamaru!", "2002-10-10"),
                        ]),
                ]));

            await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SAB Repull", Implementation: "sabnzbd", Host: "sab-test", Port: 8080, ApiKey: "k"));

            var ep2 = naruto.Containers.Single().Units.Single(u => u.UnitNumber == 2);
            var note = await wanted.RedownloadFailedWorkOrUnitsAsync(
                naruto.Work.Id,
                [ep2.Id],
                "Naruto.2002.S01E002.480p.NF.WEB-DL.H.264.AAC.2Audios-UBWEB",
                "tv");

            Assert.NotNull(note);
            Assert.StartsWith("Auto-grabbed replacement", note);
            Assert.Equal(["Naruto.2002.S01E002.1080p.WEB-DL.H.264-GRP"], gateway.EnqueuedTitles);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private static IndexedReleaseCandidate Candidate(string title, long sizeBytes) =>
        new(
            Guid: $"guid-{title}",
            IndexerId: 1,
            IndexerName: "Stub Newznab",
            Protocol: "usenet",
            MediaVertical: "tv",
            Title: title,
            DownloadUrl: $"https://indexer.invalid/get/{Uri.EscapeDataString(title)}",
            MagnetUrl: null,
            InfoUrl: null,
            InfoHash: null,
            SizeBytes: sizeBytes,
            Seeders: null,
            Peers: null,
            DownloadVolumeFactor: 1,
            UploadVolumeFactor: 1,
            PublishedAt: DateTimeOffset.UtcNow.AddDays(-1),
            Categories: [5070],
            ImdbId: null,
            TmdbId: null,
            TvdbId: null);

    private sealed class FixedResultsIndexerService(IReadOnlyList<IndexedReleaseCandidate> results) : IUnifiedIndexerService
    {
        public Task<IReadOnlyList<IndexedReleaseCandidate>> SearchAsync(IndexerSearchQuery query, CancellationToken ct = default) =>
            Task.FromResult(results);

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

    private sealed class RecordingDriverGateway : IDownloadClientDriverGateway
    {
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
            return Task.FromResult($"nzo-{EnqueuedTitles.Count}");
        }

        public Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(
            DownloadClientConnectionSpec client,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ActiveDownloadQueueItem>>([]);

        public Task RemoveDownloadAsync(
            DownloadClientConnectionSpec client,
            string externalId,
            bool deleteData,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
