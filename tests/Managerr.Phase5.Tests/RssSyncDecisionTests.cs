using Managerr.Catalog;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
using Managerr.Host.Services;
using Managerr.Indexers;
using Managerr.Indexers.Torznab;
using Managerr.Metadata;
using Managerr.Phase5.Tests.Support;
using Managerr.Profiles;
using Xunit;

namespace Managerr.Phase5.Tests;

/// <summary>
/// #67: the RSS sync decision paths beyond the ones other tests cover (blocklisted, already imported, already
/// downloading): releases for titles nobody wants, a season pack beating single episodes, and delay profiles.
/// </summary>
public sealed class RssSyncDecisionTests
{
    [Fact]
    public async Task ReleaseForAnUnknownTitle_IsIgnoredWithoutADecision()
    {
        // Feeds carry thousands of releases for titles nobody has; recording a decision for each would drown the rest
        await using var s = await Setup.StartAsync(Release("Some.Other.Show.S01E01.1080p.WEB-DL.H.264-GRP"));

        var report = await s.RssAsync();

        Assert.Equal(0, report.GrabbedCount);
        Assert.Empty(report.Decisions);
        Assert.Empty(s.Client.Enqueued);
    }

    [Fact]
    public async Task ReleaseForAnUnmonitoredEpisode_IsNotWanted_AndNotGrabbed()
    {
        await using var s = await Setup.StartAsync(Release("Naruto.2002.S01E01.1080p.WEB-DL.H.264-GRP"));
        var catalog = s.Host.Get<IPolymorphicCatalogService>();
        var ep1 = (await catalog.GetWorkTreeAsync(s.WorkId))!.Containers.Single().Units.Single(u => u.UnitNumber == 1);
        await catalog.UpdateUnitAsync(ep1.Id, new UpdateMediaUnitRequest(Monitored: false));

        var report = await s.RssAsync();

        Assert.Equal(0, report.GrabbedCount);
        Assert.Equal("NotWanted", Assert.Single(report.Decisions).DecisionKind);
        Assert.Empty(s.Client.Enqueued);
    }

    [Fact]
    public async Task SeasonPack_IsGrabbedOnce_AndTheSingleEpisodeItCovers_IsSuperseded()
    {
        await using var s = await Setup.StartAsync(
            Release("Naruto.2002.S01E02.1080p.WEB-DL.H.264-GRP"),
            Release("Naruto.2002.S01.1080p.WEB-DL.H.264-GRP"));

        var report = await s.RssAsync();

        Assert.Equal(1, report.GrabbedCount);
        Assert.Equal(["Naruto.2002.S01.1080p.WEB-DL.H.264-GRP"], s.Client.Enqueued);
        var single = report.Decisions.Single(d => d.ReleaseTitle.Contains("S01E02"));
        Assert.False(single.Grabbed);
        Assert.Contains("Superseded", single.Explanation);
    }

    [Fact]
    public async Task DelayProfile_HoldsAFreshRelease_UntilItsDelayHasPassed()
    {
        await using var s = await Setup.StartAsync(
            Release("Naruto.2002.S01E02.1080p.WEB-DL.H.264-GRP", publishedAgo: TimeSpan.FromMinutes(5)));
        // Delay profiles apply through tags; the series carries "slow"
        await s.Host.Get<IComposableProfileService>().UpsertDelayProfileAsync(new UpsertDelayProfileRequest(
            "Wait for better releases", PreferredProtocol: "usenet", UsenetDelayMinutes: 120, BypassIfHighestQuality: false,
            Tags: ["slow"], SortOrder: 1));

        var report = await s.RssAsync();

        Assert.Equal(0, report.GrabbedCount);
        Assert.Equal("PendingDelay", Assert.Single(report.Decisions).DecisionKind);

        s.Indexer.Releases = [Release("Naruto.2002.S01E02.1080p.WEB-DL.H.264-GRP", publishedAgo: TimeSpan.FromHours(3))];
        var later = await s.RssAsync();
        Assert.Equal(1, later.GrabbedCount);
    }

    private static IndexedReleaseCandidate Release(string title, TimeSpan? publishedAgo = null) =>
        new(Guid: $"guid-{title}", IndexerId: 1, IndexerName: "Stub Newznab", Protocol: "usenet", MediaVertical: "tv",
            Title: title, DownloadUrl: $"https://indexer.invalid/get/{Uri.EscapeDataString(title)}", MagnetUrl: null, InfoUrl: null,
            InfoHash: null, SizeBytes: title.Contains("S01.") ? 6_000_000_000 : 2_000_000_000, Seeders: null, Peers: null,
            DownloadVolumeFactor: 1, UploadVolumeFactor: 1, PublishedAt: DateTimeOffset.UtcNow - (publishedAgo ?? TimeSpan.FromDays(1)),
            Categories: [5070], ImdbId: null, TmdbId: null, TvdbId: null);

    private sealed class Setup : IAsyncDisposable
    {
        public HostTestHarness Host { get; private set; } = null!;

        public RssIndexer Indexer { get; } = new();

        public EnqueueRecorder Client { get; } = new();

        public long WorkId { get; private set; }

        public static async Task<Setup> StartAsync(params IndexedReleaseCandidate[] releases)
        {
            var s = new Setup();
            s.Indexer.Releases = releases;
            s.Host = await HostTestHarness.StartAsync(indexer: s.Indexer, downloadClient: s.Client);
            var tv = await s.Host.LibraryAsync("tv");
            var work = await s.Host.Get<IPolymorphicCatalogService>().AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: tv.Id, Title: "Naruto", WorkKind: "tv", AltTitles: [], OriginalLanguage: "ja", Year: 2002,
                Status: "ended", Monitored: true, ExternalIds: new Dictionary<string, string> { ["tvdb"] = "78857" },
                Tags: ["slow"],
                Containers:
                [
                    new MetadataContainerSeed("season", 1, "Season 1", "2002-10-03",
                    [
                        new MetadataUnitSeed("episode", 1, 1, "Enter: Naruto Uzumaki!", "2002-10-03"),
                        new MetadataUnitSeed("episode", 2, 2, "My Name is Konohamaru!", "2002-10-10"),
                        new MetadataUnitSeed("episode", 3, 3, "Sasuke and Sakura", "2002-10-17"),
                    ]),
                ]));
            s.WorkId = work.Work.Id;
            await s.Host.Get<IUnifiedDownloadOrchestrator>().CreateClientAsync(
                new UpsertDownloadClientRequest(Name: "SAB", Implementation: "sabnzbd", Host: "sab", Port: 8080, ApiKey: "k"));
            return s;
        }

        public Task<AutomatedRssSyncReport> RssAsync() => Host.Get<IAutomatedRssSyncService>().ExecuteRssSyncAndGrabAsync();

        public async ValueTask DisposeAsync() => await Host.DisposeAsync();
    }

    private sealed class RssIndexer : IUnifiedIndexerService
    {
        public IReadOnlyList<IndexedReleaseCandidate> Releases { get; set; } = [];

        public Task<MultiCategoryRssSyncReport> SyncMultiCategoryRssAsync(CancellationToken ct = default) =>
            Task.FromResult(new MultiCategoryRssSyncReport(1, Releases.Count,
                new Dictionary<string, IReadOnlyList<IndexedReleaseCandidate>> { ["tv"] = Releases }));

        public Task<IReadOnlyList<IndexedReleaseCandidate>> SearchAsync(IndexerSearchQuery query, CancellationToken ct = default) => Task.FromResult(Releases);
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

    private sealed class EnqueueRecorder : IDownloadClientDriverGateway
    {
        public List<string> Enqueued { get; } = [];

        public IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers() => DownloadClientCatalog.All;

        public Task<string> EnqueueReleaseAsync(DownloadClientConnectionSpec client, string releaseTitle, string downloadUrlOrMagnet, string resolvedCategory, CancellationToken ct = default)
        {
            Enqueued.Add(releaseTitle);
            return Task.FromResult($"nzo-{Enqueued.Count}");
        }

        public Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(DownloadClientConnectionSpec client, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActiveDownloadQueueItem>>([]);

        public Task RemoveDownloadAsync(DownloadClientConnectionSpec client, string externalId, bool deleteData, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
