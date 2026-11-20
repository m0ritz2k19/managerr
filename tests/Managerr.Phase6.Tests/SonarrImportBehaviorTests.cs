using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dapper;
using Managerr.Catalog;
using Managerr.Common.Net;
using Managerr.Downloaders;
using Managerr.Host.Services;
using Managerr.Indexers;
using Managerr.Migration;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Managerr.Verification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Managerr.Phase6.Tests;

public sealed class SonarrImportBehaviorTests
{
    [Fact]
    public async Task SonarrLiveApiImport_DoesNotSearchIndexers_PreservesSeasonAndEpisodeMonitoring_VerifiesRealFilesAndAdoptsExtraDiskFiles()
    {
        var tempDir = CreateTempDir();
        try
        {
            var tvRoot = Path.Combine(tempDir, "storage-tv");
            Directory.CreateDirectory(tvRoot);

            // Pre-populate real files on disk inside tvRoot/Breaking Bad/Season 01/
            var bbDir = Path.Combine(tvRoot, "Breaking Bad", "Season 01");
            Directory.CreateDirectory(bbDir);

            // S01E01 real file (15,000 bytes) + sidecar .en.srt (500 bytes)
            var s01e01Mkv = Path.Combine(bbDir, "Breaking.Bad.S01E01.German.DL.1080p.BluRay.x264.mkv");
            var s01e01Srt = Path.Combine(bbDir, "Breaking.Bad.S01E01.German.DL.1080p.BluRay.x264.en.srt");
            CreateDummyFile(s01e01Mkv, 15_000);
            CreateDummyFile(s01e01Srt, 500);

            // S01E03 real file on disk (18,500 bytes), NOT present in Sonarr /api/v3/episodefile response
            var s01e03Mkv = Path.Combine(bbDir, "Breaking.Bad.S01E03.German.DL.1080p.BluRay.x264.mkv");
            CreateDummyFile(s01e03Mkv, 18_500);

            var httpHandler = new FakeSonarrAndTorznabHttpHandler();
            var fakeSafeHttp = new FakeSafeHttpClientFactory(httpHandler);

            await using var factory = CreateTestWebFactory(tempDir, fakeSafeHttp);
            var (adminClient, _) = await CreateAdminAuthenticatedClientAsync(factory);

            using (var initScope = factory.Services.CreateScope())
            {
                var sp = initScope.ServiceProvider;
                var catalogSvc = sp.GetRequiredService<IPolymorphicCatalogService>();
                var indexerSvc = sp.GetRequiredService<IUnifiedIndexerService>();
                var dlSvc = sp.GetRequiredService<IUnifiedDownloadOrchestrator>();

                // Create Managerr's TV library ALREADY mapped to tvRoot
                await catalogSvc.CreateLibraryAsync(new CreateLibraryRequest("TV", "tv", tvRoot));

                // Register enabled Torznab indexer before import
                await indexerSvc.CreateIndexerAsync(new UpsertIndexerRequest(
                    Name: "Torznab Spy",
                    Implementation: "torznab",
                    DefinitionId: null,
                    Protocol: "torrent",
                    BaseUrl: "http://torznab.lan/api",
                    ApiKey: "spy-key",
                    Categories: [5000, 5030, 5040],
                    Priority: 10,
                    RssEnabled: true,
                    InteractiveSearchEnabled: true,
                    AutoSearchEnabled: true,
                    RequestDelayMs: 0));

                // Configure enabled torrent download client so RSS grab dispatch has a recipient client
                await dlSvc.CreateClientAsync(new UpsertDownloadClientRequest(
                    Name: "Mock Torrent Client",
                    Implementation: "qbittorrent",
                    Host: "127.0.0.1",
                    Port: 8080,
                    Username: "admin",
                    Password: "password",
                    IsEnabled: true,
                    CategoryMappings: new Dictionary<string, string> { ["tv"] = "tv", ["anime"] = "anime" }));
            }

            // Execute Live API import without path mappings (Sonarr reports /tv, Managerr auto-detects existing TV library)
            var importResp = await adminClient.PostAsJsonAsync(
                "/api/v1/migration/arr-api/import",
                new ArrApiTokenImportRequest(
                    BaseUrl: "http://sonarr.lan:8989",
                    ApiKey: "sonarr-token",
                    SourceKind: "sonarr",
                    PathMappings: null,
                    DryRun: false,
                    SaveAsSource: false));
            importResp.EnsureSuccessStatusCode();
            var importReport = (await importResp.Content.ReadFromJsonAsync<MigrationReportSummary>())!;
            Assert.DoesNotContain(importReport.Logs, l => l.Contains("Rejected symlink escape", StringComparison.OrdinalIgnoreCase));

            using var verifyScope = factory.Services.CreateScope();
            var cat = verifyScope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var dl = verifyScope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var scanSvc = verifyScope.ServiceProvider.GetRequiredService<IMediaManagementAndDiskScanService>();

            // Assertion 1: 0 indexer searches occurred during import and download queue is empty
            Assert.Equal(0, httpHandler.IndexerSearchCount);
            var queue = await dl.GetUnifiedQueueAsync();
            Assert.Empty(queue);

            // Assertion 2: Staged file attached as ImportedUnverified without scanning disk
            var works = await cat.ListWorksAsync();
            var bbWork = Assert.Single(works, w => w.Title == "Breaking Bad");
            var bbTree = (await cat.GetWorkTreeAsync(bbWork.Id))!;
            var s1 = Assert.Single(bbTree.Containers, c => c.ContainerNumber == 1);

            var s01e01Unit = Assert.Single(s1.Units, u => u.UnitNumber == 1);
            Assert.Single(s01e01Unit.Files);
            var s01e01File = s01e01Unit.Files[0];
            Assert.Equal("ImportedUnverified", s01e01File.VerifiedStatus);

            // Run dedicated ScanAndVerifyWorkAsync to probe disk and verify languages
            var scanReport = await scanSvc.ScanAndVerifyWorkAsync(bbWork.Id);
            Assert.NotNull(scanReport);

            bbTree = (await cat.GetWorkTreeAsync(bbWork.Id))!;
            s1 = Assert.Single(bbTree.Containers, c => c.ContainerNumber == 1);
            s01e01Unit = Assert.Single(s1.Units, u => u.UnitNumber == 1);
            Assert.Single(s01e01Unit.Files);
            s01e01File = s01e01Unit.Files[0];
            Assert.Equal(15_000, s01e01File.SizeBytes);
            Assert.True(s01e01Unit.QualityCutoffMet);

            // Assertion 3: Ghost deleted file was NOT attached to missing episode after scan
            var s01e02Unit = Assert.Single(s1.Units, u => u.UnitNumber == 2);
            Assert.Empty(s01e02Unit.Files);

            // Assertion 4: Extra on-disk file adopted by series folder check and verified
            var s01e03Unit = Assert.Single(s1.Units, u => u.UnitNumber == 3);
            Assert.Single(s01e03Unit.Files);
            var s01e03File = s01e03Unit.Files[0];
            Assert.True(s01e03File.VerifiedStatus is "ProbeError" or VerificationVerdicts.VerifiedFail);
            Assert.Equal(18_500, s01e03File.SizeBytes);
            Assert.True(s01e03Unit.QualityCutoffMet);
            Assert.False(s01e03Unit.LanguageCutoffMet);

            // Assertion 5: Exact Sonarr monitoring preserved across seasons and episodes
            Assert.True(s1.Monitored);
            Assert.True(s01e01Unit.Monitored);
            Assert.False(s01e02Unit.Monitored);
            Assert.True(s01e03Unit.Monitored);

            var s01e04Unit = Assert.Single(s1.Units, u => u.UnitNumber == 4);
            Assert.True(s01e04Unit.Monitored);

            var s2 = Assert.Single(bbTree.Containers, c => c.ContainerNumber == 2);
            Assert.False(s2.Monitored);
            var s02e01Unit = Assert.Single(s2.Units, u => u.UnitNumber == 1);
            Assert.False(s02e01Unit.Monitored);

            var cpWork = Assert.Single(works, w => w.Title == "Cyberpunk Edgerunners");
            Assert.Equal("anime", cpWork.WorkKind);
            Assert.False(cpWork.Monitored);
            var cpTree = (await cat.GetWorkTreeAsync(cpWork.Id))!;
            Assert.All(cpTree.Containers.SelectMany(c => c.Units), u => Assert.False(u.Monitored));

            // Assertion 6: AutomatedRssSyncService does NOT re-grab imported files or unmonitored episodes
            var rssSyncSvc = verifyScope.ServiceProvider.GetRequiredService<IAutomatedRssSyncService>();
            var syncReport = await rssSyncSvc.ExecuteRssSyncAndGrabAsync();
            Assert.Equal(0, syncReport.GrabbedCount);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task SonarrSqliteImport_RejectsPathTraversal_SkipsGhostFiles_VerifiesDiskFiles_AndRollsBackCleanly()
    {
        var tempDir = CreateTempDir();
        try
        {
            var tvRoot = Path.Combine(tempDir, "storage-tv");
            Directory.CreateDirectory(tvRoot);

            // Pre-populate real file on disk for E01
            var andorDir = Path.Combine(tvRoot, "Andor", "Season 01");
            Directory.CreateDirectory(andorDir);
            var e01File = Path.Combine(andorDir, "Andor.S01E01.1080p.WEB-DL.mkv");
            CreateDummyFile(e01File, 22_000);
            var e01OriginalWriteTime = File.GetLastWriteTimeUtc(e01File);

            // Create file outside library root to test traversal boundary
            var outsideSecret = Path.Combine(tempDir, "secret.mkv");
            CreateDummyFile(outsideSecret, 10_000);

            // Build Sonarr SQLite DB (nzbdrone.db)
            var dbPath = Path.Combine(tempDir, "sonarr.db");
            await using (var conn = OpenWriteSqlite(dbPath))
            {
                await conn.ExecuteAsync("""
                    CREATE TABLE VersionInfo (Version INTEGER NOT NULL);
                    INSERT INTO VersionInfo VALUES (215);

                    CREATE TABLE RootFolders (Id INTEGER PRIMARY KEY, Path TEXT);
                    INSERT INTO RootFolders VALUES (1, '/tv');

                    CREATE TABLE Series (Id INTEGER PRIMARY KEY, TvdbId INTEGER, ImdbId TEXT, Title TEXT, Year INTEGER, Path TEXT, Monitored INTEGER, SeriesType INTEGER, Seasons TEXT);
                    INSERT INTO Series VALUES (1, 385800, 'tt9253284', 'Andor', 2022, '/tv/Andor', 1, 0, '[{"seasonNumber":1,"monitored":true},{"seasonNumber":2,"monitored":false}]');

                    CREATE TABLE EpisodeFiles (Id INTEGER PRIMARY KEY, RelativePath TEXT, Size INTEGER, Quality TEXT);
                    INSERT INTO EpisodeFiles VALUES
                        (10, 'Season 01/Andor.S01E01.1080p.WEB-DL.mkv', 22000, '{"quality":{"name":"WEBDL-1080p"}}'),
                        (20, '../../../../etc/passwd', 1234, '{"quality":{"name":"WEBDL-1080p"}}');

                    CREATE TABLE Episodes (Id INTEGER PRIMARY KEY, SeriesId INTEGER, SeasonNumber INTEGER, EpisodeNumber INTEGER, Title TEXT, AirDateUtc TEXT, Monitored INTEGER, EpisodeFileId INTEGER);
                    INSERT INTO Episodes VALUES
                        (101, 1, 1, 1, 'Kassa', '2022-09-21', 1, 10),
                        (102, 1, 1, 2, 'That Would Be Me', '2022-09-21', 1, 20),
                        (203, 1, 2, 3, 'Announcement', '2022-10-19', 1, 0);
                    """);
            }

            var httpHandler = new FakeSonarrAndTorznabHttpHandler();
            var fakeSafeHttp = new FakeSafeHttpClientFactory(httpHandler);

            await using var factory = CreateTestWebFactory(tempDir, fakeSafeHttp);
            var (adminClient, _) = await CreateAdminAuthenticatedClientAsync(factory);

            using (var initScope = factory.Services.CreateScope())
            {
                var catalogSvc = initScope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
                await catalogSvc.CreateLibraryAsync(new CreateLibraryRequest("TV", "tv", tvRoot));
            }

            // Execute zero-touch SQLite migration
            var execResp = await adminClient.PostAsJsonAsync(
                "/api/v1/migration/execute",
                new MigrationPlanAndExecuteRequest(
                    Sources: [new AdHocMigrationSourceSpec("Sonarr DB", MigrationSourceKinds.Sonarr, dbPath)],
                    GlobalPathMappings: new Dictionary<string, string> { ["/tv"] = tvRoot },
                    UpliftLanguageCustomFormats: true));
            execResp.EnsureSuccessStatusCode();

            // Assert 0 indexer searches occurred during SQLite import
            Assert.Equal(0, httpHandler.IndexerSearchCount);

            using (var verifyScope = factory.Services.CreateScope())
            {
                var cat = verifyScope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
                var scanSvc = verifyScope.ServiceProvider.GetRequiredService<IMediaManagementAndDiskScanService>();
                var works = await cat.ListWorksAsync();
                var andorWork = Assert.Single(works, w => w.Title == "Andor");
                var andorTree = (await cat.GetWorkTreeAsync(andorWork.Id))!;

                var s1 = Assert.Single(andorTree.Containers, c => c.ContainerNumber == 1);
                var e01 = Assert.Single(s1.Units, u => u.UnitNumber == 1);
                Assert.Single(e01.Files);
                Assert.Equal(22_000, e01.Files[0].SizeBytes);
                Assert.Equal("ImportedUnverified", e01.Files[0].VerifiedStatus);
                Assert.Equal(e01OriginalWriteTime, File.GetLastWriteTimeUtc(e01File));

                // Path traversal file was rejected
                var e02 = Assert.Single(s1.Units, u => u.UnitNumber == 2);
                Assert.Empty(e02.Files);

                // Now execute dedicated scan & verify
                var scanReport = await scanSvc.ScanAndVerifyWorkAsync(andorWork.Id);
                Assert.NotNull(scanReport);

                // Season 2 and S02E03 are unmonitored
                var s2 = Assert.Single(andorTree.Containers, c => c.ContainerNumber == 2);
                Assert.False(s2.Monitored);
                var s02e03 = Assert.Single(s2.Units, u => u.UnitNumber == 3);
                Assert.False(s02e03.Monitored);
            }

            // Rollback migration
            var rollbackResp = await adminClient.PostAsJsonAsync(
                "/api/v1/migration/rollback",
                new MigrationRollbackRequest(RemoveSavedSources: true));
            rollbackResp.EnsureSuccessStatusCode();
            var rollbackResult = (await rollbackResp.Content.ReadFromJsonAsync<MigrationRollbackResult>())!;

            Assert.True(rollbackResult.WorksRemovedCount >= 1);

            using (var checkScope = factory.Services.CreateScope())
            {
                var cat = checkScope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
                Assert.Empty(await cat.ListWorksAsync());
            }

            // Real file still exists untouched on disk
            Assert.True(File.Exists(e01File));
            Assert.Equal(22_000, new FileInfo(e01File).Length);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task SonarrCompatAddSeries_ChecksExistingFilesOnDisk_NeverSearchesByDefault_AndSkipsAlreadyPresentSeasonWhenSearchRequested()
    {
        var tempDir = CreateTempDir();
        try
        {
            var tvRoot = Path.Combine(tempDir, "storage-tv");
            Directory.CreateDirectory(tvRoot);

            // Pre-populate Season 1 Episode 1 for Severance
            var sevDir = Path.Combine(tvRoot, "Severance", "Season 01");
            Directory.CreateDirectory(sevDir);
            var sevE01 = Path.Combine(sevDir, "Severance.S01E01.1080p.WEB-DL.mkv");
            CreateDummyFile(sevE01, 16_000);

            // Pre-populate Season 1 Episode 1 for MiniShow
            var miniDir = Path.Combine(tvRoot, "MiniShow", "Season 01");
            Directory.CreateDirectory(miniDir);
            var miniE01 = Path.Combine(miniDir, "MiniShow.S01E01.1080p.WEB-DL.mkv");
            CreateDummyFile(miniE01, 18_000);

            var httpHandler = new FakeSonarrAndTorznabHttpHandler();
            var fakeSafeHttp = new FakeSafeHttpClientFactory(httpHandler);

            await using var factory = CreateTestWebFactory(tempDir, fakeSafeHttp);
            var (adminClient, apiKey) = await CreateAdminAuthenticatedClientAsync(factory);

            using (var initScope = factory.Services.CreateScope())
            {
                var sp = initScope.ServiceProvider;
                var catalogSvc = sp.GetRequiredService<IPolymorphicCatalogService>();
                var indexerSvc = sp.GetRequiredService<IUnifiedIndexerService>();
                var dlSvc = sp.GetRequiredService<IUnifiedDownloadOrchestrator>();

                await catalogSvc.CreateLibraryAsync(new CreateLibraryRequest("TV", "tv", tvRoot));

                await indexerSvc.CreateIndexerAsync(new UpsertIndexerRequest(
                    Name: "Torznab Spy",
                    Implementation: "torznab",
                    DefinitionId: null,
                    Protocol: "torrent",
                    BaseUrl: "http://torznab.lan/api",
                    ApiKey: "spy-key",
                    Categories: [5000, 5030, 5040],
                    Priority: 10,
                    RssEnabled: true,
                    InteractiveSearchEnabled: true,
                    AutoSearchEnabled: true,
                    RequestDelayMs: 0));

                await dlSvc.CreateClientAsync(new UpsertDownloadClientRequest(
                    Name: "Mock Torrent Client",
                    Implementation: "qbittorrent",
                    Host: "127.0.0.1",
                    Port: 8080,
                    Username: "admin",
                    Password: "password",
                    IsEnabled: true,
                    CategoryMappings: new Dictionary<string, string> { ["tv"] = "tv" }));
            }

            // Part A: POST /compat/sonarr/api/v3/series with searchForMissingEpisodes: false (or omitted)
            httpHandler.IndexerSearchCount = 0;
            var addRespA = await adminClient.PostAsJsonAsync($"/compat/sonarr/api/v3/series?apikey={apiKey}", new
            {
                title = "Severance",
                year = 2022,
                tvdbId = 371980,
                monitored = true,
                rootFolderPath = tvRoot,
                addOptions = new { searchForMissingEpisodes = false },
            });
            addRespA.EnsureSuccessStatusCode();
            var jsonA = (await addRespA.Content.ReadFromJsonAsync<JsonObject>())!;

            // Assert 0 indexer searches occurred and returned JSON reports 0 files initially (no automatic scan on add)
            Assert.Equal(0, httpHandler.IndexerSearchCount);
            var statsA = jsonA["statistics"] as JsonObject;
            Assert.NotNull(statsA);
            Assert.Equal(0, statsA!["episodeFileCount"]!.GetValue<int>());

            // Trigger explicit RescanSeries command
            var seriesIdA = jsonA["id"]!.GetValue<long>();
            var rescanRespA = await adminClient.PostAsJsonAsync($"/compat/sonarr/api/v3/command?apikey={apiKey}", new
            {
                name = "RescanSeries",
                seriesId = seriesIdA,
            });
            rescanRespA.EnsureSuccessStatusCode();

            var getRespA = await adminClient.GetAsync($"/compat/sonarr/api/v3/series/{seriesIdA}?apikey={apiKey}");
            getRespA.EnsureSuccessStatusCode();
            var updatedJsonA = (await getRespA.Content.ReadFromJsonAsync<JsonObject>())!;
            var updatedStatsA = updatedJsonA["statistics"] as JsonObject;
            Assert.NotNull(updatedStatsA);
            Assert.True(updatedStatsA!["episodeFileCount"]!.GetValue<int>() >= 1);

            // Part B: RescanSeries adopts disk files so missing episode search skips already-present season
            httpHandler.IndexerSearchCount = 0;
            var addRespB = await adminClient.PostAsJsonAsync($"/compat/sonarr/api/v3/series?apikey={apiKey}", new
            {
                title = "MiniShow",
                year = 2023,
                tvdbId = 999123,
                monitored = true,
                rootFolderPath = tvRoot,
                addOptions = new { searchForMissingEpisodes = false },
            });
            addRespB.EnsureSuccessStatusCode();
            var jsonB = (await addRespB.Content.ReadFromJsonAsync<JsonObject>())!;
            var seriesIdB = jsonB["id"]!.GetValue<long>();

            var rescanRespB = await adminClient.PostAsJsonAsync($"/compat/sonarr/api/v3/command?apikey={apiKey}", new
            {
                name = "RescanSeries",
                seriesId = seriesIdB,
            });
            rescanRespB.EnsureSuccessStatusCode();

            var getRespB = await adminClient.GetAsync($"/compat/sonarr/api/v3/series/{seriesIdB}?apikey={apiKey}");
            getRespB.EnsureSuccessStatusCode();
            var updatedJsonB = (await getRespB.Content.ReadFromJsonAsync<JsonObject>())!;
            var statsB = updatedJsonB["statistics"] as JsonObject;
            Assert.NotNull(statsB);
            Assert.True(statsB!["episodeFileCount"]!.GetValue<int>() >= 1);

            // Now triggering SeriesSearch causes 0 grabs because season 1 files are adopted
            httpHandler.IndexerSearchCount = 0;
            var searchCmdResp = await adminClient.PostAsJsonAsync($"/compat/sonarr/api/v3/command?apikey={apiKey}", new
            {
                name = "SeriesSearch",
                seriesId = seriesIdB,
            });
            searchCmdResp.EnsureSuccessStatusCode();

            using (var verifyScope = factory.Services.CreateScope())
            {
                var dl = verifyScope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
                var queue = await dl.GetUnifiedQueueAsync();
                Assert.Empty(queue);
            }
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private static WebApplicationFactory<Program> CreateTestWebFactory(string tempDir, ISafeHttpClientFactory safeHttp) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                    ["Managerr:MasterKey"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                    ["Managerr:SchedulerEnabled"] = "false",
                });
            });
            b.ConfigureServices(services =>
            {
                services.AddSingleton<ISafeHttpClientFactory>(safeHttp);
            });
        });

    private static async Task<(HttpClient Client, string ApiKey)> CreateAdminAuthenticatedClientAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IPlexUserService>();
        var auth = scope.ServiceProvider.GetRequiredService<IPlexAuthAndSessionService>();

        var owner = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
            1, "owner-uuid", "plexowner", "owner@plex.local", null, true, true, true, ["1", "2", "3", "4"]));
        var key = await auth.CreateApiKeyAsync(owner.Id, "Admin Test Key", PermissionScopes.AllValidScopes.ToList());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.RawTokenOnce);
        return (client, key.RawTokenOnce);
    }

    private static SqliteConnection OpenWriteSqlite(string dbPath)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        return conn;
    }

    private static void CreateDummyFile(string path, int byteLength)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var buf = new byte[byteLength];
        Random.Shared.NextBytes(buf);
        File.WriteAllBytes(path, buf);
    }

    private static string CreateTempDir()
    {
        var p = Path.Combine(Path.GetTempPath(), $"managerr-sonarr-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(p);
        return p;
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup lock races in test teardown
        }
    }

    private sealed class FakeSafeHttpClientFactory : ISafeHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public FakeSafeHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreatePublicClient(bool ignoreSslErrors = false) =>
            new(_handler, disposeHandler: false);

        public HttpClient CreateHomelabClient(bool allowPrivateLan = true, bool ignoreSslErrors = false) =>
            new(_handler, disposeHandler: false);

        public void EnsureAddressPermitted(System.Net.IPAddress address, string? host = null, bool allowPrivateLan = false)
        {
        }
    }

    private sealed class FakeSonarrAndTorznabHttpHandler : HttpMessageHandler
    {
        private int _indexerSearchCount;
        public int IndexerSearchCount
        {
            get => Volatile.Read(ref _indexerSearchCount);
            set => Volatile.Write(ref _indexerSearchCount, value);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var host = uri.Host.ToLowerInvariant();
            var path = uri.AbsolutePath.ToLowerInvariant();
            var query = uri.Query;

            if (host == "torznab.lan")
            {
                Interlocked.Increment(ref _indexerSearchCount);

                var xml = """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
                      <channel>
                        <title>Mock Torznab</title>
                        <item>
                          <title>Breaking.Bad.S01E01.German.DL.1080p.BluRay.x264</title>
                          <guid>urn:guid:bb-s01e01</guid>
                          <link>http://torznab.lan/dl/bb-s01e01.torrent</link>
                          <pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate>
                          <enclosure url="http://torznab.lan/dl/bb-s01e01.torrent" length="15000" type="application/x-bittorrent" />
                          <torznab:attr name="category" value="5000" />
                          <torznab:attr name="category" value="5040" />
                          <torznab:attr name="seeders" value="100" />
                        </item>
                        <item>
                          <title>Breaking.Bad.S01E02.German.DL.1080p.BluRay.x264</title>
                          <guid>urn:guid:bb-s01e02</guid>
                          <link>http://torznab.lan/dl/bb-s01e02.torrent</link>
                          <pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate>
                          <enclosure url="http://torznab.lan/dl/bb-s01e02.torrent" length="15000" type="application/x-bittorrent" />
                          <torznab:attr name="category" value="5000" />
                          <torznab:attr name="category" value="5040" />
                          <torznab:attr name="seeders" value="100" />
                        </item>
                        <item>
                          <title>Breaking.Bad.S01E03.German.DL.1080p.BluRay.x264</title>
                          <guid>urn:guid:bb-s01e03</guid>
                          <link>http://torznab.lan/dl/bb-s01e03.torrent</link>
                          <pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate>
                          <enclosure url="http://torznab.lan/dl/bb-s01e03.torrent" length="18500" type="application/x-bittorrent" />
                          <torznab:attr name="category" value="5000" />
                          <torznab:attr name="category" value="5040" />
                          <torznab:attr name="seeders" value="100" />
                        </item>
                        <item>
                          <title>Breaking.Bad.S02E01.German.DL.1080p.BluRay.x264</title>
                          <guid>urn:guid:bb-s02e01</guid>
                          <link>http://torznab.lan/dl/bb-s02e01.torrent</link>
                          <pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate>
                          <enclosure url="http://torznab.lan/dl/bb-s02e01.torrent" length="15000" type="application/x-bittorrent" />
                          <torznab:attr name="category" value="5000" />
                          <torznab:attr name="category" value="5040" />
                          <torznab:attr name="seeders" value="100" />
                        </item>
                        <item>
                          <title>Severance.S01E01.1080p.WEB-DL</title>
                          <guid>urn:guid:sev-s01e01</guid>
                          <link>http://torznab.lan/dl/sev-s01e01.torrent</link>
                          <pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate>
                          <enclosure url="http://torznab.lan/dl/sev-s01e01.torrent" length="16000" type="application/x-bittorrent" />
                          <torznab:attr name="category" value="5000" />
                          <torznab:attr name="category" value="5040" />
                          <torznab:attr name="seeders" value="100" />
                        </item>
                        <item>
                          <title>MiniShow.S01E01.1080p.WEB-DL</title>
                          <guid>urn:guid:mini-s01e01</guid>
                          <link>http://torznab.lan/dl/mini-s01e01.torrent</link>
                          <pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate>
                          <enclosure url="http://torznab.lan/dl/mini-s01e01.torrent" length="18000" type="application/x-bittorrent" />
                          <torznab:attr name="category" value="5000" />
                          <torznab:attr name="category" value="5040" />
                          <torznab:attr name="seeders" value="100" />
                        </item>
                      </channel>
                    </rss>
                    """;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/xml"),
                });
            }

            if (host == "sonarr.lan")
            {
                string? json = path switch
                {
                    "/api/v3/system/status" => """{"appName":"Sonarr","instanceName":"Sonarr","version":"4.0.10.2544"}""",
                    "/api/v3/rootfolder" => """[{"id":1,"path":"/tv"}]""",
                    "/api/v3/downloadclient" => "[]",
                    "/api/v3/indexer" => "[]",
                    "/api/v3/customformat" => "[]",
                    "/api/v3/qualityprofile" => """[{"id":1,"name":"Bluray-1080p"}]""",
                    "/api/v3/blocklist" => """{"page":1,"pageSize":500,"records":[]}""",
                    "/api/v3/series" => """
                        [
                          {
                            "id": 10,
                            "title": "Breaking Bad",
                            "year": 2008,
                            "tvdbId": 81189,
                            "imdbId": "tt0903747",
                            "seriesType": "standard",
                            "monitored": true,
                            "path": "/tv/Breaking Bad",
                            "rootFolderPath": "/tv",
                            "seasons": [
                              { "seasonNumber": 1, "monitored": true },
                              { "seasonNumber": 2, "monitored": false }
                            ]
                          },
                          {
                            "id": 20,
                            "title": "Cyberpunk Edgerunners",
                            "year": 2022,
                            "tvdbId": 384024,
                            "imdbId": "tt12590266",
                            "seriesType": "anime",
                            "monitored": false,
                            "path": "/tv/Cyberpunk Edgerunners",
                            "rootFolderPath": "/tv",
                            "seasons": [
                              { "seasonNumber": 1, "monitored": false }
                            ]
                          }
                        ]
                        """,
                    "/api/v3/episodefile" when query.Contains("seriesId=10", StringComparison.OrdinalIgnoreCase) => """
                        [
                          {
                            "id": 501,
                            "seriesId": 10,
                            "seasonNumber": 1,
                            "relativePath": "Season 01/Breaking.Bad.S01E01.German.DL.1080p.BluRay.x264.mkv",
                            "path": "/tv/Breaking Bad/Season 01/Breaking.Bad.S01E01.German.DL.1080p.BluRay.x264.mkv",
                            "size": 999,
                            "quality": { "quality": { "name": "Bluray-1080p" } },
                            "mediaInfo": { "audioLanguages": "German" }
                          },
                          {
                            "id": 502,
                            "seriesId": 10,
                            "seasonNumber": 1,
                            "relativePath": "Season 01/Breaking.Bad.S01E02.DELETED.mkv",
                            "path": "/tv/Breaking Bad/Season 01/Breaking.Bad.S01E02.DELETED.mkv",
                            "size": 999,
                            "quality": { "quality": { "name": "Bluray-1080p" } }
                          }
                        ]
                        """,
                    "/api/v3/episode" when query.Contains("seriesId=10", StringComparison.OrdinalIgnoreCase) => """
                        [
                          {
                            "id": 101,
                            "seriesId": 10,
                            "seasonNumber": 1,
                            "episodeNumber": 1,
                            "title": "Pilot",
                            "airDate": "2008-01-20",
                            "monitored": true,
                            "episodeFileId": 501
                          },
                          {
                            "id": 102,
                            "seriesId": 10,
                            "seasonNumber": 1,
                            "episodeNumber": 2,
                            "title": "Cat's in the Bag...",
                            "airDate": "2008-01-27",
                            "monitored": false,
                            "episodeFileId": 502
                          },
                          {
                            "id": 103,
                            "seriesId": 10,
                            "seasonNumber": 1,
                            "episodeNumber": 3,
                            "title": "...And the Bag's in the River",
                            "airDate": "2008-02-10",
                            "monitored": true,
                            "episodeFileId": 0
                          },
                          {
                            "id": 104,
                            "seriesId": 10,
                            "seasonNumber": 1,
                            "episodeNumber": 4,
                            "title": "Cancer Man",
                            "airDate": "2008-02-17",
                            "monitored": true,
                            "episodeFileId": 0
                          },
                          {
                            "id": 201,
                            "seriesId": 10,
                            "seasonNumber": 2,
                            "episodeNumber": 1,
                            "title": "Seven Thirty-Seven",
                            "airDate": "2009-03-08",
                            "monitored": true,
                            "episodeFileId": 0
                          }
                        ]
                        """,
                    "/api/v3/episodefile" when query.Contains("seriesId=20", StringComparison.OrdinalIgnoreCase) => "[]",
                    "/api/v3/episode" when query.Contains("seriesId=20", StringComparison.OrdinalIgnoreCase) => """
                        [
                          {
                            "id": 301,
                            "seriesId": 20,
                            "seasonNumber": 1,
                            "episodeNumber": 1,
                            "title": "Let You Down",
                            "airDate": "2022-09-13",
                            "monitored": false,
                            "episodeFileId": 0
                          }
                        ]
                        """,
                    _ => "[]"
                };

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
