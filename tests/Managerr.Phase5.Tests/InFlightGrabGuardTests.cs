using Managerr.Catalog;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
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

/// <summary>
/// #64: while a download for an episode is in flight (grabbed, downloading, waiting to import), automatic searches
/// must not grab another release of it. Interactive grabs stay allowed, and a failed download still gets a replacement.
/// </summary>
public sealed class InFlightGrabGuardTests
{
    private const string ReleaseA = "Naruto.2002.S01E002.1080p.WEB-DL.H.264-GRP";
    private const string ReleaseB = "Naruto.2002.S01E002.1080p.WEB-DL.H.264-OTHER";

    [Fact]
    public async Task AutomaticSearches_DoNotGrabAnEpisodeAgain_WhileItsDownloadIsInFlight()
    {
        await using var h = await Harness.StartAsync();

        await h.WantedSearchAsync();
        Assert.Single(h.Client.Enqueued);

        // Grabbed but not yet reported by the client: still in flight
        await h.WantedSearchAsync();
        await h.RssSyncAsync();
        Assert.Single(h.Client.Enqueued);

        // The client reports it downloading
        await h.Import.ProcessCompletedDownloadsAsync();
        Assert.Equal(TrackedDownloadStates.Downloading, (await h.Import.ListTrackedDownloadsAsync()).Single().State);

        var wanted = await h.WantedSearchAsync();
        var rss = await h.RssSyncAsync();
        Assert.Single(h.Client.Enqueued);
        Assert.Equal(0, wanted.GrabbedCount);
        Assert.Equal(0, rss.GrabbedCount);
        Assert.True(rss.Decisions.Any(d => d.DecisionKind == "AlreadyDownloading"),
            string.Join("\n", rss.Decisions.Select(d => $"{d.ReleaseTitle}: {d.DecisionKind} {d.Explanation}")));
    }

    [Fact]
    public async Task FailedDownload_StillGetsAReplacement()
    {
        await using var h = await Harness.StartAsync();

        await h.WantedSearchAsync();
        await h.Import.ProcessCompletedDownloadsAsync();
        var first = Assert.Single(h.Client.Enqueued);

        h.Client.SetStatus(first.ExternalId, "Failed");
        await h.Import.ProcessCompletedDownloadsAsync();

        var tracked = await h.Import.ListTrackedDownloadsAsync();
        Assert.True(h.Client.Enqueued.Count == 2,
            string.Join("\n", tracked.Select(t => $"{t.ExternalDownloadId} {t.State}: {string.Join(" | ", t.StatusMessages)}")));
        Assert.NotEqual(first.Title, h.Client.Enqueued[1].Title);
    }

    [Fact]
    public async Task InteractiveGrab_IsAllowed_AutomaticGrabIsRefused_WhileInFlight()
    {
        await using var h = await Harness.StartAsync();
        await h.WantedSearchAsync();
        await h.Import.ProcessCompletedDownloadsAsync();

        await h.Orchestrator.GrabReleaseAsync(h.GrabRequest(ReleaseB, "interactive"));
        Assert.Equal(2, h.Client.Enqueued.Count);

        var ex = await Assert.ThrowsAsync<ReleaseAlreadyInFlightException>(() =>
            h.Orchestrator.GrabReleaseAsync(h.GrabRequest(ReleaseB, "rss_sync")));
        Assert.Contains(h.Episode2Id, ex.UnitIds);
        Assert.Equal(2, h.Client.Enqueued.Count);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"managerr-inflight-{Guid.NewGuid():N}");
        private WebApplicationFactory<Program> _factory = null!;
        private IServiceScope _scope = null!;

        public QueueingClient Client { get; } = new();

        public IUnifiedDownloadOrchestrator Orchestrator { get; private set; } = null!;

        public ICompletedDownloadImportService Import { get; private set; } = null!;

        public long WorkId { get; private set; }

        public long Episode2Id { get; private set; }

        private IWantedAndActivityService Wanted { get; set; } = null!;

        private IAutomatedRssSyncService Rss { get; set; } = null!;

        public static async Task<Harness> StartAsync()
        {
            var h = new Harness();
            Directory.CreateDirectory(Path.Combine(h._tempDir, "media", "tv"));
            var indexer = new TwoReleaseIndexer([Candidate(ReleaseA, 1_000_000_000), Candidate(ReleaseB, 700_000_000)]);

            h._factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = Path.Combine(h._tempDir, "config"),
                    ["Managerr:MasterKey"] = "inflight-grab-guard-test-master-key32",
                    ["Managerr:SchedulerEnabled"] = "false",
                }));
                b.ConfigureServices(s =>
                {
                    s.RemoveAll<IDownloadClientDriverGateway>();
                    s.AddSingleton<IDownloadClientDriverGateway>(h.Client);
                    s.RemoveAll<IUnifiedIndexerService>();
                    s.AddSingleton<IUnifiedIndexerService>(indexer);
                });
            });

            h._scope = h._factory.Services.CreateScope();
            var sp = h._scope.ServiceProvider;
            var catalog = sp.GetRequiredService<IPolymorphicCatalogService>();
            h.Orchestrator = sp.GetRequiredService<IUnifiedDownloadOrchestrator>();
            h.Import = sp.GetRequiredService<ICompletedDownloadImportService>();
            h.Wanted = sp.GetRequiredService<IWantedAndActivityService>();
            h.Rss = sp.GetRequiredService<IAutomatedRssSyncService>();

            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Anime", "tv", Path.Combine(h._tempDir, "media", "tv")));
            var naruto = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
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
                    new MetadataContainerSeed("season", 1, "Season 1", "2002-10-03",
                    [
                        new MetadataUnitSeed("episode", 1, 1, "Enter: Naruto Uzumaki!", "2002-10-03"),
                        new MetadataUnitSeed("episode", 2, 2, "My Name is Konohamaru!", "2002-10-10"),
                    ]),
                ]));
            h.WorkId = naruto.Work.Id;
            h.Episode2Id = naruto.Containers.Single().Units.Single(u => u.UnitNumber == 2).Id;

            // Episode 1 is on disk, so only episode 2 is wanted
            var ep1 = naruto.Containers.Single().Units.Single(u => u.UnitNumber == 1);
            await catalog.UpdateUnitAsync(ep1.Id, new UpdateMediaUnitRequest(Monitored: false));

            var client = await h.Orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SAB", Implementation: "sabnzbd", Host: "sab", Port: 8080, ApiKey: "k", RemoveFailed: false));
            h.Client.ClientId = client.Id;
            h.Client.ClientName = client.Name;
            return h;
        }

        public Task<WantedBulkSearchReport> WantedSearchAsync() =>
            Wanted.ExecuteBulkWantedSearchAsync(new WantedBulkSearchRequest(
                Mode: "selected", UnitIds: [Episode2Id], MonitoredOnly: false, MaxItems: 10));

        public Task<AutomatedRssSyncReport> RssSyncAsync() => Rss.ExecuteRssSyncAndGrabAsync();

        public GrabReleaseRequest GrabRequest(string title, string sourceType) =>
            new(title, "usenet", "tv", $"https://indexer.invalid/get/{Uri.EscapeDataString(title)}",
                WorkId: WorkId, UnitIds: [Episode2Id], IndexerId: 1, ReleaseGuid: $"guid-{title}", SourceType: sourceType);

        public async ValueTask DisposeAsync()
        {
            _scope.Dispose();
            await _factory.DisposeAsync();
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
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

    /// <summary>Returns both releases for searches and the RSS feed alike.</summary>
    private sealed class TwoReleaseIndexer(IReadOnlyList<IndexedReleaseCandidate> results) : IUnifiedIndexerService
    {
        public Task<IReadOnlyList<IndexedReleaseCandidate>> SearchAsync(IndexerSearchQuery query, CancellationToken ct = default) =>
            Task.FromResult(results);

        public Task<MultiCategoryRssSyncReport> SyncMultiCategoryRssAsync(CancellationToken ct = default) =>
            Task.FromResult(new MultiCategoryRssSyncReport(1, results.Count,
                new Dictionary<string, IReadOnlyList<IndexedReleaseCandidate>> { ["tv"] = results }));

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
    }

    /// <summary>A download client whose queue holds everything enqueued, as "Downloading" until told otherwise.</summary>
    public sealed class QueueingClient : IDownloadClientDriverGateway
    {
        private readonly Dictionary<string, string> _status = [];

        public long ClientId { get; set; }

        public string ClientName { get; set; } = "";

        public List<(string ExternalId, string Title)> Enqueued { get; } = [];

        public void SetStatus(string externalId, string status) => _status[externalId] = status;

        public IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers() => DownloadClientCatalog.All;

        public Task<string> EnqueueReleaseAsync(DownloadClientConnectionSpec client, string releaseTitle, string downloadUrlOrMagnet, string resolvedCategory, CancellationToken ct = default)
        {
            var id = $"nzo-{Enqueued.Count + 1}";
            Enqueued.Add((id, releaseTitle));
            _status[id] = "Downloading";
            return Task.FromResult(id);
        }

        public Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(DownloadClientConnectionSpec client, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActiveDownloadQueueItem>>(Enqueued
                .Select(e => new ActiveDownloadQueueItem(
                    ClientId, ClientName, "usenet", e.ExternalId, $"nzo:{e.ExternalId}", e.Title, "tv", "tv",
                    _status[e.ExternalId], 1_000_000, 500_000, "", [], false, false, null))
                .ToList());

        public Task RemoveDownloadAsync(DownloadClientConnectionSpec client, string externalId, bool deleteData, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
