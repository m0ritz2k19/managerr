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
/// #65: when a failed download cannot be mapped to episodes (no grab history, a name the parser cannot read), the
/// replacement search must not fall back to searching the series' other missing episodes or ones already on disk.
/// </summary>
public sealed class ReplacementSearchScopeTests
{
    [Fact]
    public async Task UnmappableFailedEpisode_SearchesNothing_AndSaysWhy()
    {
        await using var h = await Harness.StartAsync();
        var series = await h.AddSeriesAsync();

        var note = await h.Wanted.RedownloadFailedWorkOrUnitsAsync(series, unitIds: null, "a1b2c3d4e5f60718.nzb", "tv");

        Assert.NotNull(note);
        Assert.StartsWith("Replacement not searched", note);
        Assert.Contains("Naruto", note);
        Assert.Equal(0, h.Indexer.Searches);
        Assert.Empty(h.Client.Enqueued);
    }

    [Fact]
    public async Task MappableFailedEpisode_StillSearchesThatEpisode()
    {
        await using var h = await Harness.StartAsync();
        var series = await h.AddSeriesAsync();

        var note = await h.Wanted.RedownloadFailedWorkOrUnitsAsync(series, unitIds: null, "Naruto.2002.S01E002.480p.WEB-DL-BROKEN", "tv");

        Assert.NotNull(note);
        Assert.StartsWith("Auto-grabbed replacement", note);
        Assert.Equal(["Naruto.2002.S01E002.1080p.WEB-DL.H.264-GRP"], h.Client.Enqueued);
    }

    [Fact]
    public async Task UnmappableFailedMovie_StillSearchesTheMovie()
    {
        // A movie is a single unit, so "the missing unit" is the one that failed. The search must still only take
        // releases of this movie: the indexer also returns Naruto episodes and the 1984 Dune
        await using var h = await Harness.StartAsync();
        var movie = await h.AddMovieAsync();

        var note = await h.Wanted.RedownloadFailedWorkOrUnitsAsync(movie, unitIds: null, "a1b2c3d4e5f60718.nzb", "movies");

        Assert.NotNull(note);
        Assert.StartsWith("Auto-grabbed replacement", note);
        Assert.Equal(["Dune.Part.Two.2024.1080p.WEB-DL.H.264-GRP"], h.Client.Enqueued);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"managerr-repl-scope-{Guid.NewGuid():N}");
        private WebApplicationFactory<Program> _factory = null!;
        private IServiceScope _scope = null!;
        private IPolymorphicCatalogService _catalog = null!;
        private long _tvLibraryId;
        private long _movieLibraryId;

        public CountingIndexer Indexer { get; } = new();

        public RecordingClient Client { get; } = new();

        public IWantedAndActivityService Wanted { get; private set; } = null!;

        public static async Task<Harness> StartAsync()
        {
            var h = new Harness();
            Directory.CreateDirectory(Path.Combine(h._tempDir, "media", "tv"));
            Directory.CreateDirectory(Path.Combine(h._tempDir, "media", "movies"));
            h._factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = Path.Combine(h._tempDir, "config"),
                    ["Managerr:MasterKey"] = "replacement-scope-test-master-key-32b",
                    ["Managerr:SchedulerEnabled"] = "false",
                }));
                b.ConfigureServices(s =>
                {
                    s.RemoveAll<IDownloadClientDriverGateway>();
                    s.AddSingleton<IDownloadClientDriverGateway>(h.Client);
                    s.RemoveAll<IUnifiedIndexerService>();
                    s.AddSingleton<IUnifiedIndexerService>(h.Indexer);
                });
            });

            h._scope = h._factory.Services.CreateScope();
            var sp = h._scope.ServiceProvider;
            h._catalog = sp.GetRequiredService<IPolymorphicCatalogService>();
            h.Wanted = sp.GetRequiredService<IWantedAndActivityService>();
            h._tvLibraryId = (await h._catalog.CreateLibraryAsync(new CreateLibraryRequest("TV", "tv", Path.Combine(h._tempDir, "media", "tv")))).Id;
            h._movieLibraryId = (await h._catalog.CreateLibraryAsync(new CreateLibraryRequest("Movies", "movies", Path.Combine(h._tempDir, "media", "movies")))).Id;
            await sp.GetRequiredService<IUnifiedDownloadOrchestrator>().CreateClientAsync(
                new UpsertDownloadClientRequest(Name: "SAB", Implementation: "sabnzbd", Host: "sab", Port: 8080, ApiKey: "k"));
            return h;
        }

        public async Task<long> AddSeriesAsync()
        {
            var tree = await _catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: _tvLibraryId, Title: "Naruto", WorkKind: "series", AltTitles: [], OriginalLanguage: "ja",
                Year: 2002, Status: "ended", Monitored: true,
                ExternalIds: new Dictionary<string, string> { ["tvdb"] = "78857" },
                Containers:
                [
                    new MetadataContainerSeed("season", 1, "Season 1", "2002-10-03",
                    [
                        new MetadataUnitSeed("episode", 1, 1, "Enter: Naruto Uzumaki!", "2002-10-03"),
                        new MetadataUnitSeed("episode", 2, 2, "My Name is Konohamaru!", "2002-10-10"),
                        new MetadataUnitSeed("episode", 3, 3, "Sasuke and Sakura", "2002-10-17"),
                    ]),
                ]));
            return tree.Work.Id;
        }

        public async Task<long> AddMovieAsync()
        {
            var tree = await _catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: _movieLibraryId, Title: "Dune: Part Two", WorkKind: "movies", AltTitles: ["Dune Part Two"], OriginalLanguage: "en",
                Year: 2024, Status: "released", Monitored: true,
                ExternalIds: new Dictionary<string, string> { ["tmdb"] = "693134" },
                Containers:
                [
                    new MetadataContainerSeed("movie", 1, "Dune: Part Two", "2024-03-01",
                        [new MetadataUnitSeed("movie", 1, null, "Dune: Part Two", "2024-03-01")]),
                ]));
            return tree.Work.Id;
        }

        public async ValueTask DisposeAsync()
        {
            _scope.Dispose();
            await _factory.DisposeAsync();
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static IndexedReleaseCandidate Candidate(string title, string vertical) =>
        new(Guid: $"guid-{title}", IndexerId: 1, IndexerName: "Stub Newznab", Protocol: "usenet", MediaVertical: vertical,
            Title: title, DownloadUrl: $"https://indexer.invalid/get/{Uri.EscapeDataString(title)}", MagnetUrl: null, InfoUrl: null,
            InfoHash: null, SizeBytes: 2_000_000_000, Seeders: null, Peers: null, DownloadVolumeFactor: 1, UploadVolumeFactor: 1,
            PublishedAt: DateTimeOffset.UtcNow.AddDays(-1), Categories: [vertical == "tv" ? 5070 : 2040], ImdbId: null, TmdbId: null, TvdbId: null);

    /// <summary>Answers every search with one release per episode and the movie, and counts the searches.</summary>
    private sealed class CountingIndexer : IUnifiedIndexerService
    {
        public int Searches { get; private set; }

        public Task<IReadOnlyList<IndexedReleaseCandidate>> SearchAsync(IndexerSearchQuery query, CancellationToken ct = default)
        {
            Searches++;
            return Task.FromResult<IReadOnlyList<IndexedReleaseCandidate>>(
            [
                Candidate("Naruto.2002.S01E001.1080p.WEB-DL.H.264-GRP", "tv"),
                Candidate("Naruto.2002.S01E002.1080p.WEB-DL.H.264-GRP", "tv"),
                Candidate("Naruto.2002.S01E003.1080p.WEB-DL.H.264-GRP", "tv"),
                Candidate("Dune.1984.1080p.BluRay.x264-OLDIE", "movies"),
                Candidate("Dune.Part.Two.2024.1080p.WEB-DL.H.264-GRP", "movies"),
            ]);
        }

        public Task<MultiCategoryRssSyncReport> SyncMultiCategoryRssAsync(CancellationToken ct = default) => throw new NotImplementedException();
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

    private sealed class RecordingClient : IDownloadClientDriverGateway
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
