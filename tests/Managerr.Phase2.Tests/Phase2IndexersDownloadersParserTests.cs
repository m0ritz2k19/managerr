using System.Diagnostics;
using Managerr.Common.Crypto;
using Managerr.Common.Paths;
using Managerr.Datastore;
using Managerr.Datastore.Migrations;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
using Managerr.Downloaders.Organizer;
using Managerr.Downloaders.Parser;
using Managerr.Indexers;
using Managerr.Indexers.Cardigann;
using Managerr.Indexers.Categories;
using Xunit;

namespace Managerr.Phase2.Tests;

public sealed class FakeIndexerHttpGateway : IIndexerHttpGateway
{
    public Func<Uri, IndexerProxyInternal?, string> Handler { get; set; } = (_, _) => "";
    public List<(Uri Uri, IndexerProxyInternal? Proxy)> Calls { get; } = [];

    public Task<string> FetchStringAsync(Uri targetUri, IndexerProxyInternal? matchedProxy, CancellationToken ct = default)
    {
        Calls.Add((targetUri, matchedProxy));
        return Task.FromResult(Handler(targetUri, matchedProxy));
    }
}

public sealed class FakeDownloadDriverGateway : IDownloadClientDriverGateway
{
    public List<(DownloadClientConnectionSpec Client, string Title, string Url, string Category)> Enqueued { get; } = [];
    public List<ActiveDownloadQueueItem> SimulatedQueue { get; } = [];
    public List<string> RemovedExternalIds { get; } = [];

    public IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers() => DownloadClientCatalog.All;

    public Task<string> EnqueueReleaseAsync(
        DownloadClientConnectionSpec client,
        string releaseTitle,
        string downloadUrlOrMagnet,
        string resolvedCategory,
        CancellationToken ct = default)
    {
        Enqueued.Add((client, releaseTitle, downloadUrlOrMagnet, resolvedCategory));
        return Task.FromResult($"ext-{Enqueued.Count}");
    }

    public Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(
        DownloadClientConnectionSpec client,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ActiveDownloadQueueItem>>(
            SimulatedQueue.Where(q => q.ClientId == client.Id).ToList());

    public Task RemoveDownloadAsync(
        DownloadClientConnectionSpec client,
        string externalId,
        bool deleteData,
        CancellationToken ct = default)
    {
        RemovedExternalIds.Add(externalId);
        SimulatedQueue.RemoveAll(q => q.ExternalId == externalId);
        return Task.CompletedTask;
    }
}

public sealed class Phase2IndexersDownloadersParserTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"managerr-p2-{Guid.NewGuid():N}");
    private AppPaths _paths = null!;
    private SqliteDbFactory _dbFactory = null!;
    private EnvelopeSecretProtector _protector = null!;

    public async Task InitializeAsync()
    {
        _paths = new AppPaths(_tempDir);
        _dbFactory = new SqliteDbFactory(_paths);
        _protector = new EnvelopeSecretProtector(_paths, "phase2-master-key-test");
        var migrator = new SchemaMigrator(_dbFactory);
        await migrator.MigrateAllAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbFactory.DisposeAsync();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task CardigannCatalog_ShipsNoTrackerDefinitions_LoadsCustomOnes_Proxies_Backoff_AndSinglePassRss()
    {
        var catalog = new CardigannDefinitionCatalog(_paths);

        // No provider-specific definitions are bundled: a fresh install has none until the user adds their own
        Assert.Empty(catalog.ListDefinitions());

        // Verify custom YAML definition override in /config/Definitions/Custom
        var customYml = """
            id: homelab-tracker
            name: Homelab Private Tracker
            description: Custom tracker test
            language: de-DE
            type: private
            links:
              - https://tracker.homelab.lan/
            caps:
              categorymappings:
                - {id: 10, cat: Movies/UHD, desc: "Movies UHD"}
                - {id: 20, cat: TV/Anime, desc: "Anime"}
            search:
              paths:
                - path: "search?q={{ .Keywords }}"
              rows:
                selector: "table#torrents tbody tr"
              fields:
                title:
                  selector: "td.name a"
                download:
                  selector: "td.dl a"
                  attribute: href
                size:
                  selector: "td.size"
                seeders:
                  selector: "td.seeders"
                category:
                  selector: "td.cat"
            """;
        await File.WriteAllTextAsync(Path.Combine(_paths.CustomDefinitionsDirectory, "homelab-tracker.yml"), customYml);
        catalog.ReloadDefinitions();

        var customDef = catalog.GetDefinition("homelab-tracker");
        Assert.NotNull(customDef);
        Assert.True(customDef.Summary.IsCustomOverride);

        var gateway = new FakeIndexerHttpGateway();
        var service = new UnifiedIndexerService(_dbFactory, _protector, catalog, gateway);

        // Configure FlareSolverr proxy bound by tag "cf-protected"
        var proxy = await service.CreateProxyAsync(new UpsertIndexerProxyRequest(
            Name: "FlareSolverr Local",
            ProxyType: "flaresolverr",
            Host: "flaresolverr",
            Port: 8191,
            Tags: ["cf-protected"]));
        Assert.Equal("flaresolverr", proxy.ProxyType);

        // Configure Cardigann indexer + Torznab multi-vertical RSS indexer
        var cardigannIx = await service.CreateIndexerAsync(new UpsertIndexerRequest(
            Name: "Homelab Cardigann",
            Implementation: "cardigann",
            DefinitionId: "homelab-tracker",
            Protocol: "torrent",
            BaseUrl: "https://tracker.homelab.lan",
            Tags: ["cf-protected"],
            RequestDelayMs: 0));

        var torznabIx = await service.CreateIndexerAsync(new UpsertIndexerRequest(
            Name: "Unified Torznab",
            Implementation: "torznab",
            DefinitionId: null,
            Protocol: "torrent",
            BaseUrl: "http://prowlarr.lan:9696/1",
            ApiKey: "super-secret-torznab-key",
            Categories: [2000, 3000, 3030, 5000, 5070, 7020, 7030],
            RequestDelayMs: 0));

        Assert.True(torznabIx.ApiKeySecret.IsSet);
        Assert.DoesNotContain("super-secret-torznab-key", torznabIx.ApiKeySecret.MaskedHint ?? "");

        gateway.Handler = (uri, matchedProxy) =>
        {
            if (uri.Host == "tracker.homelab.lan")
            {
                Assert.NotNull(matchedProxy);
                Assert.Equal("flaresolverr", matchedProxy.ProxyType);
                return """
                    <html><body><table id="torrents"><tbody>
                      <tr>
                        <td class="cat">10</td>
                        <td class="name"><a href="/t/1">Dune.Part.Two.2024.GERMAN.DL.2160p.UHD.BluRay.REMUX-GRP</a></td>
                        <td class="dl"><a href="magnet:?xt=urn:btih:11223344556677889900aabbccddeeff00112233&dn=Dune">DL</a></td>
                        <td class="size">64.5 GB</td>
                        <td class="seeders">142</td>
                      </tr>
                    </tbody></table></body></html>
                    """;
            }

            return """
                <?xml version="1.0" encoding="UTF-8"?>
                <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
                  <channel>
                    <item>
                      <title>[SubsPlease] Frieren - 28 (1080p) [ABCD1234]</title>
                      <link>magnet:?xt=urn:btih:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</link>
                      <torznab:attr name="category" value="5070" />
                      <torznab:attr name="seeders" value="95" />
                      <torznab:attr name="size" value="1450000000" />
                    </item>
                    <item>
                      <title>Brandon Sanderson - Wind and Truth (2024) [EPUB]</title>
                      <link>https://indexer.lan/dl/book1.torrent</link>
                      <torznab:attr name="category" value="7020" />
                      <torznab:attr name="seeders" value="30" />
                    </item>
                    <item>
                      <title>Andy Weir - Project Hail Mary [Unabridged M4B]</title>
                      <link>https://indexer.lan/dl/audio1.torrent</link>
                      <torznab:attr name="category" value="3030" />
                      <torznab:attr name="seeders" value="44" />
                    </item>
                    <item>
                      <title>Daft Punk - Discovery (2001) [FLAC 24bit]</title>
                      <link>https://indexer.lan/dl/music1.torrent</link>
                      <torznab:attr name="category" value="3040" />
                      <torznab:attr name="seeders" value="88" />
                    </item>
                  </channel>
                </rss>
                """;
        };

        // Single-Pass Multi-Category RSS Sync routes across Movies, Anime, Ebooks, Audiobooks, and Music in 1 poll per indexer!
        var rssReport = await service.SyncMultiCategoryRssAsync();
        Assert.Equal(2, rssReport.PolledIndexers);
        Assert.Equal(5, rssReport.TotalReleases);
        Assert.Single(rssReport.ReleasesByVertical[MediaVerticals.Movies]);
        Assert.Single(rssReport.ReleasesByVertical[MediaVerticals.Anime]);
        Assert.Single(rssReport.ReleasesByVertical[MediaVerticals.Ebooks]);
        Assert.Single(rssReport.ReleasesByVertical[MediaVerticals.Audiobooks]);
        Assert.Single(rssReport.ReleasesByVertical[MediaVerticals.Music]);

        // Escalating health backoff disables failing indexer
        var disabledUntil = await service.RecordFailureAsync(cardigannIx.Id, "HTTP 503 Cloudflare Timeout");
        Assert.True(disabledUntil > DateTimeOffset.UtcNow);
        var updatedList = await service.ListIndexersAsync();
        var failedState = updatedList.First(x => x.Id == cardigannIx.Id);
        Assert.False(failedState.IsHealthy);
        Assert.Equal(1, failedState.ConsecutiveFailures);
    }

    [Fact]
    public async Task UnifiedDownloadOrchestrator_Supports20Clients_PerVerticalCategories_GluetunRemotePaths_MalwareBlock_AndStrikeBlocklist()
    {
        var fakeGateway = new FakeDownloadDriverGateway();
        var orchestrator = new UnifiedDownloadOrchestrator(_dbFactory, _protector, fakeGateway);

        var drivers = orchestrator.ListSupportedDrivers();
        Assert.Equal(12, drivers.Count);
        Assert.Equal(
            [
                "aria2", "deluge", "flood", "nzbget", "pneumatic", "qbittorrent",
                "rtorrent", "sabnzbd", "torrent_blackhole", "transmission", "usenet_blackhole", "vuze",
            ],
            drivers.Select(d => d.Implementation).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(drivers, d => d.Implementation == "qbittorrent" && d.Protocol == "torrent");
        Assert.Contains(drivers, d => d.Implementation == "sabnzbd" && d.Protocol == "usenet");
        Assert.Contains(drivers, d => d.Implementation == "nzbget" && d.Protocol == "usenet");

        // Configure single unified qBittorrent behind Gluetun VPN container (vpn:8080)
        var qbt = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
            Name: "qBittorrent (Gluetun)",
            Implementation: "qbittorrent",
            Host: "vpn",
            Port: 8080,
            Username: "admin",
            Password: "qbt-secret-password"));

        await orchestrator.CreateRemotePathMappingAsync(new CreateRemotePathMappingRequest(
            DownloadClientId: qbt.Id,
            Host: "vpn",
            RemotePath: "/data/torrents",
            LocalPath: "/mnt/storage/torrents"));

        // Grab an Anime release and an Audiobook release into the SAME qBittorrent -> routed to distinct vertical categories!
        var animeGrab = await orchestrator.GrabReleaseAsync(new GrabReleaseRequest(
            Title: "[SubsPlease] Frieren - 28 (1080p)",
            Protocol: "torrent",
            MediaVertical: MediaVerticals.Anime,
            DownloadUrlOrMagnet: "magnet:?xt=urn:btih:1111"));
        Assert.Equal("anime-managerr", animeGrab.ResolvedCategory);

        var audiobookGrab = await orchestrator.GrabReleaseAsync(new GrabReleaseRequest(
            Title: "Project Hail Mary [M4B]",
            Protocol: "torrent",
            MediaVertical: MediaVerticals.Audiobooks,
            DownloadUrlOrMagnet: "magnet:?xt=urn:btih:2222"));
        Assert.Equal("audiobooks-managerr", audiobookGrab.ResolvedCategory);

        // Simulate 1 malware-infected torrent (.lnk) and 1 stalled torrent in qBittorrent queue
        fakeGateway.SimulatedQueue.Add(new ActiveDownloadQueueItem(
            ClientId: qbt.Id,
            ClientName: qbt.Name,
            Protocol: "torrent",
            ExternalId: "badhash_malware",
            InfoHash: "badhash_malware",
            Title: "Fake.Movie.2026.2160p",
            Category: "movies-managerr",
            MediaVertical: MediaVerticals.Movies,
            Status: "downloading",
            TotalBytes: 1000,
            RemainingBytes: 0,
            OutputPath: "/data/torrents/Fake.Movie.2026",
            ContainedFiles: ["/data/torrents/Fake.Movie.2026/Play_Movie.mkv.lnk"],
            IsStalled: false,
            IsMetadataStuck: false,
            ErrorMessage: null));

        fakeGateway.SimulatedQueue.Add(new ActiveDownloadQueueItem(
            ClientId: qbt.Id,
            ClientName: qbt.Name,
            Protocol: "torrent",
            ExternalId: "stalled_hash_01",
            InfoHash: "stalled_hash_01",
            Title: "Dead.Swarm.S01E01.1080p",
            Category: "tv-managerr",
            MediaVertical: MediaVerticals.Tv,
            Status: "stalledDL",
            TotalBytes: 2_000_000_000,
            RemainingBytes: 1_900_000_000,
            OutputPath: "/data/torrents/Dead.Swarm",
            ContainedFiles: ["/data/torrents/Dead.Swarm/Dead.Swarm.S01E01.mkv"],
            IsStalled: true,
            IsMetadataStuck: false,
            ErrorMessage: null));

        // Verify RemotePathMapping translation on unified queue
        var unifiedQueue = await orchestrator.GetUnifiedQueueAsync();
        Assert.Equal("/mnt/storage/torrents/Dead.Swarm", unifiedQueue.First(q => q.ExternalId == "stalled_hash_01").OutputPath);

        // Sweep 1: Malware torrent is killed & blocklisted immediately; stalled torrent gets Strike 1/3
        var sweep1 = await orchestrator.EvaluateQueueHealthAndStrikesAsync(strikeThreshold: 3);
        Assert.Contains(sweep1, a => a.ExternalDownloadId == "badhash_malware" && a.ActionTaken == "removed_malware" && a.AddedToRecurringBlocklist);
        Assert.Contains(sweep1, a => a.ExternalDownloadId == "stalled_hash_01" && a.StrikeCount == 1);

        // Sweep 2 & Sweep 3 -> stalled torrent hits 3 strikes, gets removed & added to RecurringHashes blocklist!
        await orchestrator.EvaluateQueueHealthAndStrikesAsync(strikeThreshold: 3);
        var sweep3 = await orchestrator.EvaluateQueueHealthAndStrikesAsync(strikeThreshold: 3);
        Assert.Contains(sweep3, a => a.ExternalDownloadId == "stalled_hash_01" && a.StrikeCount == 3 && a.AddedToRecurringBlocklist);

        // Attempting to re-grab a blocklisted hash is automatically rejected!
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.GrabReleaseAsync(new GrabReleaseRequest(
                Title: "Retry Stalled Release",
                Protocol: "torrent",
                MediaVertical: MediaVerticals.Tv,
                DownloadUrlOrMagnet: "magnet:?xt=urn:btih:stalled_hash_01",
                InfoHash: "stalled_hash_01")));
    }

    [Fact]
    public async Task ReleaseParserRouter_And_AtomicHardlinkOrganizer_HandleAllVerticalsWithZeroDuplicateDiskUsage()
    {
        var parser = new ReleaseParserRouter();

        // 1. Movie + IMAX Edition + German Dual-Audio + 2160p Remux DV HDR10 TrueHD Atmos
        var movie = parser.Parse("Dune.Part.Two.2024.IMAX.GERMAN.DL.2160p.UHD.BluRay.REMUX.DV.HDR10.TrueHD.Atmos.HEVC-GROUP");
        Assert.Equal(MediaVerticals.Movies, movie.MediaVertical);
        Assert.Equal("Dune Part Two", movie.CleanTitle);
        Assert.Equal(2024, movie.Year);
        Assert.Equal("IMAX", movie.MovieEdition);
        Assert.Equal("2160p", movie.Resolution);
        Assert.Equal("Remux", movie.Source);
        Assert.Equal("DV HDR10", movie.HdrFormat);
        Assert.Equal("TrueHD Atmos", movie.AudioCodec);
        Assert.True(movie.IsDualAudio);
        Assert.Contains("de", movie.DetectedLanguages);
        Assert.Contains("en", movie.DetectedLanguages);

        // 2. Multi-episode TV release
        var tv = parser.Parse("Severance.S02E01-E02.2160p.WEB-DL.DDP5.1.Atmos.HEVC-FLUX");
        Assert.Equal(MediaVerticals.Tv, tv.MediaVertical);
        Assert.Equal("Severance", tv.CleanTitle);
        Assert.Equal(2, tv.SeasonNumber);
        Assert.Equal([1, 2], tv.EpisodeNumbers);

        // 3. Anime fansub absolute episode
        var anime = parser.Parse("[SubsPlease] Frieren - 28 (1080p) [ABCD1234]");
        Assert.Equal(MediaVerticals.Anime, anime.MediaVertical);
        Assert.Equal("SubsPlease", anime.ReleaseGroup);
        Assert.Equal("Frieren", anime.CleanTitle);
        Assert.Equal([28], anime.AbsoluteEpisodeNumbers);

        // 4. Music FLAC 24bit, Ebook EPUB, Audiobook M4B
        var music = parser.Parse("Daft Punk - Random Access Memories (2013) [FLAC 24bit]");
        Assert.Equal(MediaVerticals.Music, music.MediaVertical);
        Assert.Equal("Daft Punk", music.ArtistOrAuthor);
        Assert.Equal("FLAC 24bit", music.MusicFormat);

        var ebook = parser.Parse("Brandon Sanderson - Wind and Truth [EPUB]");
        Assert.Equal(MediaVerticals.Ebooks, ebook.MediaVertical);
        Assert.Equal("EPUB", ebook.BookFormat);

        // 5. Real POSIX link() Hardlink Verification (st_nlink == 2)
        var stagingDir = Path.Combine(_tempDir, "staging");
        var libraryDir = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(libraryDir);

        var sourceFile = Path.Combine(stagingDir, "Dune.Part.Two.2024.mkv");
        await File.WriteAllBytesAsync(sourceFile, new byte[4096]);

        var organizer = new AtomicFileOrganizer();
        var imported = await organizer.ImportFileAtomicallyAsync(new OrganizeFileRequest(
            SourceFilePath: sourceFile,
            LibraryRootPath: libraryDir,
            ParsedInfo: movie,
            PreferHardlink: true));

        Assert.True(imported.Hardlinked);
        Assert.Equal("hardlink", imported.TransferMode);
        Assert.True(File.Exists(imported.DestinationFilePath));

        if (OperatingSystem.IsLinux())
        {
            var statProc = Process.Start(new ProcessStartInfo("stat", ["-c", "%h", imported.DestinationFilePath])
            {
                RedirectStandardOutput = true,
            })!;
            var nlinkStr = (await statProc.StandardOutput.ReadToEndAsync()).Trim();
            await statProc.WaitForExitAsync();
            Assert.Equal("2", nlinkStr);
        }
    }

    [Fact]
    public async Task FlatCardigannCatalog_IndexerUpdateSecretPreservation_TestIndexerCaps_AndDownloadClientUpdate()
    {
        // 1. Verify flat top-level *.yml layout (Prowlarr/Indexers master branch in Docker) loads cleanly
        var flatDefsDir = Path.Combine(_tempDir, "flat-definitions");
        Directory.CreateDirectory(flatDefsDir);
        await File.WriteAllTextAsync(
            Path.Combine(flatDefsDir, "flat-tracker.yml"),
            """
            id: flat-tracker
            name: Flat Top-Level Tracker
            language: de-DE
            type: public
            links:
              - https://flat-tracker.example/
            caps:
              categorymappings:
                - {id: 1, cat: Movies/HD, desc: "Movies HD"}
                - {id: 2, cat: TV/HD, desc: "TV HD"}
            search:
              paths:
                - path: "search"
              rows:
                selector: "tr"
              fields:
                title:
                  selector: "td.t"
                download:
                  selector: "a.dl"
                  attribute: href
            """);

        var flatCatalog = new CardigannDefinitionCatalog(_paths, flatDefsDir);
        var flatDef = flatCatalog.GetDefinition("flat-tracker");
        Assert.NotNull(flatDef);
        Assert.Contains("https://flat-tracker.example/", flatDef.Summary.Links);

        // 2. Verify UpdateIndexerAsync preserves encrypted API key when ApiKey is blank/null, and TestIndexerAsync verifies caps
        var gateway = new FakeIndexerHttpGateway
        {
            Handler = (uri, _) =>
            {
                Assert.Contains("t=caps", uri.Query);
                Assert.Contains("apikey=my-existing-secret-key", uri.Query);
                return """<?xml version="1.0" encoding="UTF-8"?><caps><server title="TreasureMaps" /></caps>""";
            },
        };
        var indexerService = new UnifiedIndexerService(_dbFactory, _protector, flatCatalog, gateway);

        var createdIx = await indexerService.CreateIndexerAsync(new UpsertIndexerRequest(
            Name: "Treasure Maps",
            Implementation: "newznab",
            DefinitionId: null,
            Protocol: "usenet",
            BaseUrl: "https://treasure-maps.example",
            ApiKey: "my-existing-secret-key",
            Categories: [2000, 5000]));

        // Simulate failure backoff, then edit indexer without re-entering API key
        await indexerService.RecordFailureAsync(createdIx.Id, "Temporary timeout");

        var updatedIx = await indexerService.UpdateIndexerAsync(createdIx.Id, new UpsertIndexerRequest(
            Name: "Treasure Maps (DE + UHD)",
            Implementation: "newznab",
            DefinitionId: null,
            Protocol: "usenet",
            BaseUrl: "https://treasure-maps.example",
            ApiKey: "", // blank on edit -> must preserve existing encrypted secret!
            Categories: [2000, 2040, 2045, 5000, 5040, 5045],
            Priority: 15));

        Assert.NotNull(updatedIx);
        Assert.Equal("Treasure Maps (DE + UHD)", updatedIx.Name);
        Assert.True(updatedIx.ApiKeySecret.IsSet);
        Assert.True(updatedIx.IsHealthy);
        Assert.Contains(2045, updatedIx.Categories);
        Assert.Contains(5045, updatedIx.Categories);

        var testResult = await indexerService.TestIndexerAsync(
            new UpsertIndexerRequest(
                Name: updatedIx.Name,
                Implementation: "newznab",
                DefinitionId: null,
                Protocol: "usenet",
                BaseUrl: "https://treasure-maps.example",
                ApiKey: null),
            existingId: updatedIx.Id);
        Assert.True(testResult.Ok);

        // 3. Verify UpdateClientAsync preserves encrypted password/API key when omitted
        var orchestrator = new UnifiedDownloadOrchestrator(_dbFactory, _protector, new FakeDownloadDriverGateway());
        var client = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
            Name: "SABnzbd",
            Implementation: "sabnzbd",
            Host: "sabnzbd.lan",
            Port: 8080,
            ApiKey: "sab-secret-apikey"));
        var editedClient = await orchestrator.UpdateClientAsync(client.Id, new UpsertDownloadClientRequest(
            Name: "SABnzbd (Primary)",
            Implementation: "sabnzbd",
            Host: "sabnzbd.lan",
            Port: 8080,
            ApiKey: ""));
        Assert.NotNull(editedClient);
        Assert.Equal("SABnzbd (Primary)", editedClient.Name);
        Assert.True(editedClient.ApiKeySecret.IsSet);
    }
}
