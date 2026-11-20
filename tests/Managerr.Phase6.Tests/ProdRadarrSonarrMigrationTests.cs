using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dapper;
using Managerr.Catalog;
using Managerr.Common.Crypto;
using Managerr.Common.Net;
using Managerr.Datastore;
using Managerr.Downloaders;
using Managerr.Indexers;
using Managerr.Metadata;
using Managerr.Migration;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Managerr.Phase6.Tests;

public sealed class ProdRadarrSonarrMigrationTests
{
    [Fact]
    public async Task UnifiedLiveApiMigration_SonarrAndRadarr_WithAnimeMovies_ConsolidatesAndPreservesMetadata()
    {
        var tempDir = CreateTempDir();
        try
        {
            var mediaRoot = Path.Combine(tempDir, "media");
            var tvRoot = Path.Combine(mediaRoot, "tv");
            var animeRoot = Path.Combine(mediaRoot, "anime");
            var movieRoot = Path.Combine(mediaRoot, "movies");
            var animeMovieRoot = Path.Combine(mediaRoot, "animefilme");

            var tvFile = Path.Combine(tvRoot, "Silo", "Season 01", "Silo.S01E01.mkv");
            var animeTvFile = Path.Combine(animeRoot, "Frieren", "Season 01", "Frieren.S01E01.mkv");
            var movieFile = Path.Combine(movieRoot, "Civil War (2024)", "Civil.War.2024.mkv");
            var animeMovieFile = Path.Combine(animeMovieRoot, "Spirited Away (2001)", "Spirited.Away.2001.mkv");

            CreateDummyFile(tvFile, 4096);
            CreateDummyFile(animeTvFile, 4096);
            CreateDummyFile(movieFile, 8192);
            CreateDummyFile(animeMovieFile, 8192);

            var sonarrBackupZipBytes = await BuildSonarrBackupZipBytesAsync(tempDir);
            var fakeSafeHttp = new FakeProdArrLiveApiSafeHttpClientFactory(sonarrBackupZipBytes);

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
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
                    services.AddSingleton<ISafeHttpClientFactory>(fakeSafeHttp);
                });
            });

            using var adminClient = await CreateAdminAuthenticatedClientAsync(factory);

            var pathMappings = new Dictionary<string, string>
            {
                ["/tv"] = tvRoot,
                ["/anime"] = animeRoot,
                ["/movies"] = movieRoot,
                ["/animemovs"] = animeMovieRoot,
            };

            // 1. Import Sonarr via API Token (contains /tv + /anime, Japanese originalLanguage, scheduled backup .zip without size)
            var sonarrImportResp = await adminClient.PostAsJsonAsync(
                "/api/v1/migration/arr-api/import",
                new ArrApiTokenImportRequest(
                    BaseUrl: "http://sonarr.prod.lan:8989",
                    ApiKey: "sonarr-prod-secret-111",
                    SourceKind: "sonarr",
                    PathMappings: pathMappings,
                    DryRun: false,
                    SaveAsSource: true));
            sonarrImportResp.EnsureSuccessStatusCode();
            var sonarrReport = (await sonarrImportResp.Content.ReadFromJsonAsync<MigrationReportSummary>())!;

            Assert.Equal(2, sonarrReport.TvSeriesCount);
            Assert.Equal(2, sonarrReport.TvEpisodeFilesCount);
            Assert.Equal(2, sonarrReport.ConsolidatedDownloadClientCount);
            Assert.Equal(0L, sonarrReport.BytesMovedOrModified);

            // 2. Import Radarr via API Token (contains /movies + /animemovs, inCinemas minimumAvailability, Japanese originalLanguage)
            var radarrImportResp = await adminClient.PostAsJsonAsync(
                "/api/v1/migration/arr-api/import",
                new ArrApiTokenImportRequest(
                    BaseUrl: "http://radarr.prod.lan:7878",
                    ApiKey: "radarr-prod-secret-222",
                    SourceKind: "radarr",
                    PathMappings: pathMappings,
                    DryRun: false,
                    SaveAsSource: true));
            radarrImportResp.EnsureSuccessStatusCode();
            var radarrReport = (await radarrImportResp.Content.ReadFromJsonAsync<MigrationReportSummary>())!;

            Assert.Equal(2, radarrReport.MoviesCount);
            Assert.Equal(2, radarrReport.MovieFilesCount);
            Assert.Equal(0L, radarrReport.BytesMovedOrModified);

            // 3. Verify Database State
            using var scope = factory.Services.CreateScope();
            var catalogSvc = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var dlOrchestrator = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var indexerSvc = scope.ServiceProvider.GetRequiredService<IUnifiedIndexerService>();

            var libraries = await catalogSvc.ListLibrariesAsync();
            var tvLib = Assert.Single(libraries, l => l.Name == "Migrated TV");
            Assert.Equal("tv", tvLib.MediaType);

            var animeTvLib = Assert.Single(libraries, l => l.Name == "Migrated Anime");
            Assert.Equal("anime", animeTvLib.MediaType);

            var movieLib = Assert.Single(libraries, l => l.Name == "Migrated Movies");
            Assert.Equal("movies", movieLib.MediaType);

            // /animemovs must create "Migrated Anime Movies" under "movies" vertical, NOT colliding with "Migrated Anime"
            var animeMovieLib = Assert.Single(libraries, l => l.Name == "Migrated Anime Movies");
            Assert.Equal("movies", animeMovieLib.MediaType);
            Assert.Equal(animeMovieRoot, animeMovieLib.RootFolderPath);

            // Verify Works & Japanese originalLanguage + inCinemas minimumAvailability
            var works = await catalogSvc.ListWorksAsync();
            Assert.Equal(4, works.Count);

            var frieren = Assert.Single(works, w => w.Title == "Sousou no Frieren");
            Assert.Equal(animeTvLib.Id, frieren.LibraryId);
            Assert.Equal("ja", frieren.OriginalLanguage);

            var spiritedAway = Assert.Single(works, w => w.Title == "Spirited Away");
            Assert.Equal(animeMovieLib.Id, spiritedAway.LibraryId);
            Assert.Equal("movies", spiritedAway.WorkKind);
            Assert.Equal("ja", spiritedAway.OriginalLanguage);
            Assert.Equal("inCinemas", spiritedAway.MinimumAvailability);

            var civilWar = Assert.Single(works, w => w.Title == "Civil War");
            Assert.Equal(movieLib.Id, civilWar.LibraryId);
            Assert.Equal("en", civilWar.OriginalLanguage);
            Assert.Equal("announced", civilWar.MinimumAvailability);

            // Verify Download Clients: consolidated across Sonarr & Radarr with unmasked secrets and merged categories
            var clients = await dlOrchestrator.ListClientsAsync();
            Assert.Equal(2, clients.Count);

            var sab = Assert.Single(clients, c => c.Implementation.Equals("Sabnzbd", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("sabnzbd.lan", sab.Host);
            Assert.Equal(8080, sab.Port);
            Assert.True(sab.ApiKeySecret.IsSet);
            Assert.True(sab.CategoryMappings.ContainsKey("tv"), "SABnzbd missing tv category");
            Assert.True(sab.CategoryMappings.ContainsKey("movies"), "SABnzbd missing movies category");

            var qb = Assert.Single(clients, c => c.Implementation.Equals("QBittorrent", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("qbittorrent.lan", qb.Host);
            Assert.Equal(8080, qb.Port);
            Assert.True(qb.PasswordSecret.IsSet);
            Assert.True(qb.CategoryMappings.ContainsKey("tv"), "qBittorrent missing tv category");
            Assert.True(qb.CategoryMappings.ContainsKey("movies"), "qBittorrent missing movies category");

            // Zero indexer searches and zero background scans: files attached in-place as ImportedUnverified
            var frierenTree = await catalogSvc.GetWorkTreeAsync(frieren.Id);
            var frierenEpFile = Assert.Single(frierenTree!.Containers.SelectMany(c => c.Units).SelectMany(u => u.Files));
            Assert.Equal("ImportedUnverified", frierenEpFile.VerifiedStatus);
            Assert.True(frierenEpFile.InodeHardlinked);

            var spiritedAwayTree = await catalogSvc.GetWorkTreeAsync(spiritedAway.Id);
            var spiritedAwayFile = Assert.Single(spiritedAwayTree!.Containers.SelectMany(c => c.Units).SelectMany(u => u.Files));
            Assert.Equal("ImportedUnverified", spiritedAwayFile.VerifiedStatus);
            Assert.True(spiritedAwayFile.InodeHardlinked);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task ExistingWorksAndClients_AreIdempotent_AndBackfillMissingFilesWithoutDuplicates()
    {
        var tempDir = CreateTempDir();
        try
        {
            var mediaRoot = Path.Combine(tempDir, "media");
            var tvRoot = Path.Combine(mediaRoot, "tv");
            var animeRoot = Path.Combine(mediaRoot, "anime");
            var movieRoot = Path.Combine(mediaRoot, "movies");
            var animeMovieRoot = Path.Combine(mediaRoot, "animefilme");

            var tvFile = Path.Combine(tvRoot, "Silo", "Season 01", "Silo.S01E01.mkv");
            var animeTvFile = Path.Combine(animeRoot, "Frieren", "Season 01", "Frieren.S01E01.mkv");
            var movieFile = Path.Combine(movieRoot, "Civil War (2024)", "Civil.War.2024.mkv");
            var animeMovieFile = Path.Combine(animeMovieRoot, "Spirited Away (2001)", "Spirited.Away.2001.mkv");

            CreateDummyFile(tvFile, 4096);
            CreateDummyFile(animeTvFile, 4096);
            CreateDummyFile(movieFile, 8192);
            CreateDummyFile(animeMovieFile, 8192);

            var sonarrBackupZipBytes = await BuildSonarrBackupZipBytesAsync(tempDir);
            var fakeSafeHttp = new FakeProdArrLiveApiSafeHttpClientFactory(sonarrBackupZipBytes);

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
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
                    services.AddSingleton<ISafeHttpClientFactory>(fakeSafeHttp);
                });
            });

            using var adminClient = await CreateAdminAuthenticatedClientAsync(factory);

            // Pre-seed existing libraries and existing works (as if requested via Overseerr / Seerr with tvdb/tmdb externalIds and 0 files)
            using (var scope = factory.Services.CreateScope())
            {
                var catalogSvc = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
                var dlOrchestrator = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();

                var tvLib = await catalogSvc.CreateLibraryAsync(new CreateLibraryRequest("Migrated TV", "tv", tvRoot));
                var animeTvLib = await catalogSvc.CreateLibraryAsync(new CreateLibraryRequest("Migrated Anime", "anime", animeRoot));
                var movieLib = await catalogSvc.CreateLibraryAsync(new CreateLibraryRequest("Migrated Movies", "movies", movieRoot));
                var animeMovieLib = await catalogSvc.CreateLibraryAsync(new CreateLibraryRequest("Migrated Anime Movies", "movies", animeMovieRoot));

                // Pre-seed Frieren with 0 files
                await catalogSvc.AddWorkAsync(new CreateMediaWorkRequest(
                    LibraryId: animeTvLib.Id,
                    Title: "Sousou no Frieren",
                    WorkKind: "anime",
                    PrimaryProvider: "tvdb",
                    ExternalIds: new Dictionary<string, string> { ["tvdb"] = "412345" },
                    Containers:
                    [
                        new MetadataContainerSeed(
                            ContainerKind: "season",
                            ContainerNumber: 1,
                            Title: "Season 1",
                            ReleaseDate: "2023-09-29",
                            Units: [new MetadataUnitSeed("episode", 1, null, "The End of the Journey", "2023-09-29")])
                    ],
                    SkipUpstreamHydration: true));

                // Pre-seed Spirited Away with 0 files
                await catalogSvc.AddWorkAsync(new CreateMediaWorkRequest(
                    LibraryId: animeMovieLib.Id,
                    Title: "Spirited Away",
                    WorkKind: "movie",
                    PrimaryProvider: "tmdb",
                    ExternalIds: new Dictionary<string, string> { ["tmdb"] = "129" },
                    SkipUpstreamHydration: true));

                // Pre-seed existing SABnzbd download client
                await dlOrchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                    Name: "SABnzbd Live",
                    Implementation: "Sabnzbd",
                    Host: "sabnzbd.lan",
                    Port: 8080,
                    ApiKey: "preseeded-sab-key"));
            }

            var pathMappings = new Dictionary<string, string>
            {
                ["/tv"] = tvRoot,
                ["/anime"] = animeRoot,
                ["/movies"] = movieRoot,
                ["/animemovs"] = animeMovieRoot,
            };

            // Run initial migration
            var sonarrResp = await adminClient.PostAsJsonAsync(
                "/api/v1/migration/arr-api/import",
                new ArrApiTokenImportRequest(
                    BaseUrl: "http://sonarr.prod.lan:8989",
                    ApiKey: "sonarr-prod-secret-111",
                    SourceKind: "sonarr",
                    PathMappings: pathMappings,
                    DryRun: false,
                    SaveAsSource: true));
            sonarrResp.EnsureSuccessStatusCode();

            var radarrResp = await adminClient.PostAsJsonAsync(
                "/api/v1/migration/arr-api/import",
                new ArrApiTokenImportRequest(
                    BaseUrl: "http://radarr.prod.lan:7878",
                    ApiKey: "radarr-prod-secret-222",
                    SourceKind: "radarr",
                    PathMappings: pathMappings,
                    DryRun: false,
                    SaveAsSource: true));
            radarrResp.EnsureSuccessStatusCode();

            // Verify backfill without work duplicates
            using (var scope = factory.Services.CreateScope())
            {
                var catalogSvc = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
                var dlOrchestrator = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();

                var works = await catalogSvc.ListWorksAsync();
                Assert.Equal(4, works.Count);

                // Frieren should have its missing file backfilled
                var frieren = Assert.Single(works, w => w.Title == "Sousou no Frieren");
                var frierenTree = await catalogSvc.GetWorkTreeAsync(frieren.Id);
                var frierenEpUnit = Assert.Single(frierenTree!.Containers.SelectMany(c => c.Units));
                var frierenFile = Assert.Single(frierenEpUnit.Files);
                Assert.Equal("ImportedUnverified", frierenFile.VerifiedStatus);
                Assert.True(frierenEpUnit.QualityCutoffMet);
                Assert.True(frierenEpUnit.LanguageCutoffMet);

                // Spirited Away should have its movie file backfilled
                var spiritedAway = Assert.Single(works, w => w.Title == "Spirited Away");
                var spiritedAwayTree = await catalogSvc.GetWorkTreeAsync(spiritedAway.Id);
                var movieUnit = Assert.Single(spiritedAwayTree!.Containers.SelectMany(c => c.Units));
                var movieFileSummary = Assert.Single(movieUnit.Files);
                Assert.Equal("ImportedUnverified", movieFileSummary.VerifiedStatus);
                Assert.True(movieUnit.QualityCutoffMet);
                Assert.True(movieUnit.LanguageCutoffMet);

                // Clients count: SABnzbd was merged, qBittorrent added -> total 2
                var clients = await dlOrchestrator.ListClientsAsync();
                Assert.Equal(2, clients.Count);
            }

            // Re-run migration a second time: test 100% idempotency
            var rerunSonarr = await adminClient.PostAsJsonAsync(
                "/api/v1/migration/arr-api/import",
                new ArrApiTokenImportRequest(
                    BaseUrl: "http://sonarr.prod.lan:8989",
                    ApiKey: "sonarr-prod-secret-111",
                    SourceKind: "sonarr",
                    PathMappings: pathMappings,
                    DryRun: false,
                    SaveAsSource: true));
            rerunSonarr.EnsureSuccessStatusCode();

            var rerunRadarr = await adminClient.PostAsJsonAsync(
                "/api/v1/migration/arr-api/import",
                new ArrApiTokenImportRequest(
                    BaseUrl: "http://radarr.prod.lan:7878",
                    ApiKey: "radarr-prod-secret-222",
                    SourceKind: "radarr",
                    PathMappings: pathMappings,
                    DryRun: false,
                    SaveAsSource: true));
            rerunRadarr.EnsureSuccessStatusCode();

            using (var scope = factory.Services.CreateScope())
            {
                var catalogSvc = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
                var dlOrchestrator = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();

                var works = await catalogSvc.ListWorksAsync();
                Assert.Equal(4, works.Count);

                var clients = await dlOrchestrator.ListClientsAsync();
                Assert.Equal(2, clients.Count);
            }
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Theory]
    [InlineData("0", "announced")]
    [InlineData("1", "announced")]
    [InlineData("tba", "announced")]
    [InlineData("announced", "announced")]
    [InlineData("2", "inCinemas")]
    [InlineData("incinemas", "inCinemas")]
    [InlineData("in_cinemas", "inCinemas")]
    [InlineData("in cinemas", "inCinemas")]
    [InlineData("released", "released")]
    [InlineData(null, "released")]
    [InlineData("3", "released")]
    public void NormalizeRadarrMinimumAvailability_MapsCorrectly(string? input, string expected)
    {
        var result = LegacyArrAndCleanerrMigrator.NormalizeRadarrMinimumAvailability(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("japanese", "ja")]
    [InlineData("jpn", "ja")]
    [InlineData("ja", "ja")]
    [InlineData("german", "de")]
    [InlineData("deutsch", "de")]
    [InlineData("deu", "de")]
    [InlineData("ger", "de")]
    [InlineData("de", "de")]
    [InlineData("english", "en")]
    [InlineData("eng", "en")]
    [InlineData("en", "en")]
    [InlineData("french", "fr")]
    [InlineData("fra", "fr")]
    [InlineData("spanish", "es")]
    [InlineData("korean", "ko")]
    [InlineData("chinese", "zh")]
    [InlineData(null, "en")]
    [InlineData("unknown_lang", "en")]
    public void NormalizeSingleLanguageNameToIso2_MapsCorrectly(string? input, string expected)
    {
        var result = LegacyArrAndCleanerrMigrator.NormalizeSingleLanguageNameToIso2(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task SharedVpnDualClient_RemotePathMappings_SABAndQBit_RouteToCorrectLocalPaths()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
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
            });

            using var scope = factory.Services.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();

            // 1. Create SABnzbd and qBittorrent on shared host "vpn"
            var sab = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd (vpn:8085)",
                Implementation: "sabnzbd",
                Host: "vpn",
                Port: 8085,
                ApiKey: "sab-secret-123"));

            var qbit = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "qBittorrent (vpn:8080)",
                Implementation: "qbittorrent",
                Host: "vpn",
                Port: 8080,
                Username: "admin",
                Password: "qbit-secret-456"));

            // 2. Add client-specific remote path mappings for shared host "vpn"
            var sabMapping = await orchestrator.CreateRemotePathMappingAsync(new CreateRemotePathMappingRequest(
                DownloadClientId: sab.Id,
                Host: "vpn",
                RemotePath: "/downloads/",
                LocalPath: "/downloads/usenet/"));

            var qbitMapping = await orchestrator.CreateRemotePathMappingAsync(new CreateRemotePathMappingRequest(
                DownloadClientId: qbit.Id,
                Host: "vpn",
                RemotePath: "/downloads/",
                LocalPath: "/downloads/torrents/"));

            Assert.Equal(sab.Id, sabMapping.DownloadClientId);
            Assert.Equal(qbit.Id, qbitMapping.DownloadClientId);

            // 3. Test TranslateRemotePathAsync with downloadClientId routing
            var sabTranslated = await orchestrator.TranslateRemotePathAsync("vpn", "/downloads/completed/Show.S01E01", downloadClientId: sab.Id);
            Assert.Equal("/downloads/usenet/completed/Show.S01E01", sabTranslated);

            var qbitTranslated = await orchestrator.TranslateRemotePathAsync("vpn", "/downloads/completed/Show.S01E01", downloadClientId: qbit.Id);
            Assert.Equal("/downloads/torrents/completed/Show.S01E01", qbitTranslated);

            // 4. Verify system root paths are rejected
            await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.CreateRemotePathMappingAsync(new CreateRemotePathMappingRequest(
                DownloadClientId: sab.Id,
                Host: "vpn",
                RemotePath: "/remote/",
                LocalPath: "/etc")));

            await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.CreateRemotePathMappingAsync(new CreateRemotePathMappingRequest(
                DownloadClientId: sab.Id,
                Host: "vpn",
                RemotePath: "/remote/",
                LocalPath: "/root")));

            await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.CreateRemotePathMappingAsync(new CreateRemotePathMappingRequest(
                DownloadClientId: sab.Id,
                Host: "vpn",
                RemotePath: "/remote/",
                LocalPath: "/media/../etc/passwd")));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Consolidation_UnmaskedSecretWins_And_RollbackPreservesPreExistingClientsAndWorks()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
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
            });

            using (var scope = factory.Services.CreateScope())
            {
                var orchestrator = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
                var catalogSvc = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
                var migrator = scope.ServiceProvider.GetRequiredService<ILegacyMigrationService>();
                var dbFactory = scope.ServiceProvider.GetRequiredService<ISqliteDbFactory>();

                // 1. Create pre-existing download client with custom name and password
                var preExistingClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                    Name: "Operator SAB",
                    Implementation: "sabnzbd",
                    Host: "vpn",
                    Port: 8085,
                    ApiKey: "pre-existing-operator-key",
                    CategoryMappings: new Dictionary<string, string> { ["tv"] = "operator-tv" }));

                // 2. Create pre-existing work in a user library (e.g. from Jellyseerr)
                var userLib = await catalogSvc.CreateLibraryAsync(new CreateLibraryRequest("User Series", "tv", "/storage/media/tv"));
                var preExistingWorkTree = await catalogSvc.AddWorkAsync(new CreateMediaWorkRequest(
                    LibraryId: userLib.Id,
                    Title: "Severance",
                    WorkKind: "series",
                    Year: 2022,
                    ExternalIds: new Dictionary<string, string> { ["tvdb"] = "371980" }));
                var preExistingWork = preExistingWorkTree.Work;

                var unit = preExistingWorkTree.Containers.SelectMany(c => c.Units).FirstOrDefault();
                Assert.NotNull(unit);

                // Attach a VerifiedOnDisk file
                await catalogSvc.AttachFileAsync(new AttachMediaFileRequest(
                    UnitId: unit.Id,
                    RelativePath: "Season 01/Severance.S01E01.mkv",
                    SizeBytes: 1048576,
                    QualityName: "Bluray-1080p",
                    VerifiedStatus: "VerifiedOnDisk"));

                // Attach a second media file with 'ImportedUnverified' to simulate migration backfill on this work
                await catalogSvc.AttachFileAsync(new AttachMediaFileRequest(
                    UnitId: unit.Id,
                    RelativePath: "Season 01/Severance.S01E02.mkv",
                    SizeBytes: 1048576,
                    QualityName: "Bluray-1080p",
                    VerifiedStatus: "ImportedUnverified"));

                // Also create a purely migrated work in "Migrated TV"
                var migLib = await catalogSvc.CreateLibraryAsync(new CreateLibraryRequest("Migrated TV", "tv", "/media/tv"));
                var migratedWorkTree = await catalogSvc.AddWorkAsync(new CreateMediaWorkRequest(
                    LibraryId: migLib.Id,
                    Title: "Migrated Show",
                    WorkKind: "series",
                    Year: 2024,
                    Tags: ["migrated-arr"]));
                var migratedWork = migratedWorkTree.Work;

                // Record a migration run so rollback can inspect it
                await dbFactory.ExecuteWriteAsync(
                    DatabaseKind.Core,
                    (conn, tx) => conn.ExecuteAsync("""
                        INSERT INTO migration_runs (mode, uplift_language_cfs, status, started_at, finished_at, summary_json)
                        VALUES ('execute', 1, 'completed', '2026-11-14T00:00:00Z', '2026-11-14T00:00:00Z',
                                '{"Mode":"execute","DownloadClients":[{"Name":"SABnzbd (vpn:8085)","Implementation":"sabnzbd","Host":"vpn","Port":8085,"CategoryMappings":{},"MergedFromSources":["Source1","Source2"]}],"Indexers":[]}');
                        """, transaction: tx));

                // 3. Verify pre-existing client was NOT overwritten in name
                var clientsBeforeRollback = await orchestrator.ListClientsAsync();
                var opClient = Assert.Single(clientsBeforeRollback, c => c.Host == "vpn" && c.Port == 8085);
                Assert.Equal("Operator SAB", opClient.Name);
                Assert.True(opClient.ApiKeySecret.IsSet);

                // 4. Run rollback
                var rollbackResult = await migrator.RollbackMigratedDataAsync();

                // 5. Verify Operator SAB client survived rollback!
                var clientsAfterRollback = await orchestrator.ListClientsAsync();
                var opClientAfter = Assert.Single(clientsAfterRollback, c => c.Host == "vpn" && c.Port == 8085);
                Assert.Equal("Operator SAB", opClientAfter.Name);

                // 6. Verify pre-existing work "Severance" survived rollback!
                var severanceTreeAfter = await catalogSvc.GetWorkTreeAsync(preExistingWork.Id);
                Assert.NotNull(severanceTreeAfter);
                Assert.Equal("Severance", severanceTreeAfter.Work.Title);

                // And verify its backfilled ImportedUnverified file was deleted, while the VerifiedOnDisk file survived!
                var allFiles = severanceTreeAfter.Containers.SelectMany(c => c.Units).SelectMany(u => u.Files).ToList();
                var survivingFile = Assert.Single(allFiles);
                Assert.Equal("VerifiedOnDisk", survivingFile.VerifiedStatus);
                Assert.Equal("Season 01/Severance.S01E01.mkv", survivingFile.RelativePath);

                // And verify the purely migrated work was deleted
                var migratedAfter = await catalogSvc.GetWorkTreeAsync(migratedWork.Id);
                Assert.Null(migratedAfter);
            }
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task SameTitleSameYear_SeriesAndMovie_DoNotCollideAcrossVerticals()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
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
            });

            using var scope = factory.Services.CreateScope();
            var catalogSvc = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();

            var tvLib = await catalogSvc.CreateLibraryAsync(new CreateLibraryRequest("TV Shows", "tv", "/media/tv"));
            var movieLib = await catalogSvc.CreateLibraryAsync(new CreateLibraryRequest("Movies", "movies", "/media/movies"));

            // Pre-seed a TV series: "Solo Leveling" (2024), WorkKind = "series"
            var tvSeriesTree = await catalogSvc.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: "Solo Leveling",
                WorkKind: "series",
                Year: 2024,
                ExternalIds: new Dictionary<string, string> { ["tvdb"] = "404535" }));
            var tvSeries = tvSeriesTree.Work;

            // Pre-seed a Movie: "Solo Leveling" (2024), WorkKind = "movie"
            var movieTree = await catalogSvc.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: movieLib.Id,
                Title: "Solo Leveling",
                WorkKind: "movie",
                Year: 2024,
                ExternalIds: new Dictionary<string, string> { ["tmdb"] = "1371727" }));
            var movie = movieTree.Work;

            Assert.NotEqual(tvSeries.Id, movie.Id);
            Assert.Equal(tvLib.Id, tvSeries.LibraryId);
            Assert.Equal(movieLib.Id, movie.LibraryId);

            var allWorks = await catalogSvc.ListWorksAsync();
            Assert.Equal(2, allWorks.Count);

            // Filter for TV lookup (same condition as ShadowAndCutover Step 6a)
            var tvMatch = allWorks.FirstOrDefault(w =>
                (w.WorkKind is "series" or "tv" or "anime") &&
                (w.Title.Equals("Solo Leveling", StringComparison.OrdinalIgnoreCase) && w.Year == 2024));
            Assert.NotNull(tvMatch);
            Assert.Equal(tvSeries.Id, tvMatch.Id);
            Assert.Equal("tv", tvMatch.WorkKind);

            // Filter for Movie lookup (same condition as ShadowAndCutover Step 6b)
            var movieMatch = allWorks.FirstOrDefault(w =>
                (w.WorkKind is "movie" or "movies") &&
                (w.Title.Equals("Solo Leveling", StringComparison.OrdinalIgnoreCase) && w.Year == 2024));
            Assert.NotNull(movieMatch);
            Assert.Equal(movie.Id, movieMatch.Id);
            Assert.Equal("movies", movieMatch.WorkKind);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private static async Task<HttpClient> CreateAdminAuthenticatedClientAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IPlexUserService>();
        var auth = scope.ServiceProvider.GetRequiredService<IPlexAuthAndSessionService>();

        var owner = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
            1, "owner-uuid", "plexowner", "owner@plex.local", null, true, true, true, ["1", "2", "3", "4"]));
        var key = await auth.CreateApiKeyAsync(owner.Id, "Admin Test Key", PermissionScopes.AllValidScopes.ToList());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.RawTokenOnce);
        return client;
    }

    private static string CreateTempDir()
    {
        var p = Path.Combine(Path.GetTempPath(), $"managerr-prod-arr-test-{Guid.NewGuid():N}");
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
        catch { }
    }

    private static void CreateDummyFile(string path, int byteLength)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var buf = new byte[byteLength];
        Random.Shared.NextBytes(buf);
        File.WriteAllBytes(path, buf);
    }

    private static async Task<byte[]> BuildSonarrBackupZipBytesAsync(string tempDir)
    {
        var dbPath = Path.Combine(tempDir, "sonarr-bkp.db");
        await using (var conn = new SqliteConnection($"Data Source={dbPath};"))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("""
                CREATE TABLE DownloadClients (Id INTEGER PRIMARY KEY, Name TEXT, Implementation TEXT, Settings TEXT, Enable INTEGER);
                INSERT INTO DownloadClients VALUES
                    (1, 'SABnzbd Live', 'Sabnzbd', '{"host":"sabnzbd.lan","port":8080,"apiKey":"unmasked-sab-api-key-9999","tvCategory":"tv"}', 1),
                    (2, 'qBittorrent Live', 'QBittorrent', '{"host":"qbittorrent.lan","port":8080,"username":"admin","password":"unmasked-qb-pass-8888","tvCategory":"tv"}', 1);
                CREATE TABLE Indexers (Id INTEGER PRIMARY KEY, Name TEXT, Implementation TEXT, Settings TEXT);
                """);
        }

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("sonarr.db");
            using var entryStream = entry.Open();
            using var fs = File.OpenRead(dbPath);
            fs.CopyTo(entryStream);
        }
        return ms.ToArray();
    }

    private sealed class FakeProdArrLiveApiSafeHttpClientFactory(byte[] sonarrBackupZipBytes) : ISafeHttpClientFactory
    {
        public HttpClient CreatePublicClient(bool ignoreSslErrors = false) =>
            new(new FakeProdArrLiveApiHandler(sonarrBackupZipBytes), disposeHandler: true);

        public HttpClient CreateHomelabClient(bool allowPrivateLan = true, bool ignoreSslErrors = false) =>
            new(new FakeProdArrLiveApiHandler(sonarrBackupZipBytes), disposeHandler: true);

        public void EnsureAddressPermitted(IPAddress address, string? host = null, bool allowPrivateLan = false) { }
    }

    private sealed class FakeProdArrLiveApiHandler(byte[] sonarrBackupZipBytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var host = uri.Host.ToLowerInvariant();
            var path = uri.AbsolutePath.ToLowerInvariant();
            var query = uri.Query;

            var apiKey = request.Headers.TryGetValues("X-Api-Key", out var vals)
                ? vals.FirstOrDefault()
                : null;

            if (host == "sonarr.prod.lan")
            {
                if (apiKey != "sonarr-prod-secret-111")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

                if (path == "/backup/scheduled/sonarr_backup.zip")
                {
                    var zipResp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(sonarrBackupZipBytes)
                    };
                    zipResp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                    return Task.FromResult(zipResp);
                }

                string? json = path switch
                {
                    "/api/v3/system/status" => """{"appName":"Sonarr","instanceName":"Homelab Sonarr","version":"4.0.10.2544"}""",
                    "/api/v3/rootfolder" => """[{"id":1,"path":"/tv"},{"id":2,"path":"/anime"}]""",
                    "/api/v3/system/backup" => """
                        [
                          {
                            "name": "sonarr_backup.zip",
                            "path": "C:\\ProgramData\\Sonarr\\Backups\\scheduled\\sonarr_backup.zip",
                            "type": "scheduled"
                          }
                        ]
                        """,
                    "/api/v3/downloadclient" => """
                        [
                          {
                            "id": 1,
                            "name": "SABnzbd Live",
                            "implementation": "Sabnzbd",
                            "enable": true,
                            "fields": [
                              { "name": "host", "value": "sabnzbd.lan" },
                              { "name": "port", "value": 8080 },
                              { "name": "apiKey", "value": "********" },
                              { "name": "tvCategory", "value": "tv" }
                            ]
                          },
                          {
                            "id": 2,
                            "name": "qBittorrent Live",
                            "implementation": "QBittorrent",
                            "enable": true,
                            "fields": [
                              { "name": "host", "value": "qbittorrent.lan" },
                              { "name": "port", "value": 8080 },
                              { "name": "username", "value": "admin" },
                              { "name": "password", "value": "********" },
                              { "name": "tvCategory", "value": "tv" }
                            ]
                          }
                        ]
                        """,
                    "/api/v3/indexer" => "[]",
                    "/api/v3/customformat" => "[]",
                    "/api/v3/blocklist" => """{"page":1,"pageSize":500,"records":[]}""",
                    "/api/v3/series" => """
                        [
                          {
                            "id": 42,
                            "title": "Silo",
                            "year": 2023,
                            "tvdbId": 403245,
                            "imdbId": "tt14688458",
                            "seriesType": "standard",
                            "monitored": true,
                            "path": "/tv/Silo",
                            "rootFolderPath": "/tv",
                            "originalLanguage": { "id": 1, "name": "English" }
                          },
                          {
                            "id": 43,
                            "title": "Sousou no Frieren",
                            "year": 2023,
                            "tvdbId": 412345,
                            "seriesType": "anime",
                            "monitored": true,
                            "path": "/anime/Frieren",
                            "rootFolderPath": "/anime",
                            "originalLanguage": { "id": 8, "name": "Japanese" }
                          }
                        ]
                        """,
                    "/api/v3/episodefile" when query.Contains("seriesId=42", StringComparison.OrdinalIgnoreCase) => """
                        [
                          {
                            "id": 501,
                            "seriesId": 42,
                            "seasonNumber": 1,
                            "relativePath": "Season 01/Silo.S01E01.mkv",
                            "path": "/tv/Silo/Season 01/Silo.S01E01.mkv",
                            "size": 4096,
                            "quality": { "quality": { "name": "WEBDL-2160p" } }
                          }
                        ]
                        """,
                    "/api/v3/episode" when query.Contains("seriesId=42", StringComparison.OrdinalIgnoreCase) => """
                        [
                          {
                            "id": 1001,
                            "seriesId": 42,
                            "seasonNumber": 1,
                            "episodeNumber": 1,
                            "title": "Freedom Day",
                            "airDate": "2023-05-05",
                            "monitored": true,
                            "episodeFileId": 501
                          }
                        ]
                        """,
                    "/api/v3/episodefile" when query.Contains("seriesId=43", StringComparison.OrdinalIgnoreCase) => """
                        [
                          {
                            "id": 502,
                            "seriesId": 43,
                            "seasonNumber": 1,
                            "relativePath": "Season 01/Frieren.S01E01.mkv",
                            "path": "/anime/Frieren/Season 01/Frieren.S01E01.mkv",
                            "size": 4096,
                            "quality": { "quality": { "name": "WEBDL-1080p" } }
                          }
                        ]
                        """,
                    "/api/v3/episode" when query.Contains("seriesId=43", StringComparison.OrdinalIgnoreCase) => """
                        [
                          {
                            "id": 1002,
                            "seriesId": 43,
                            "seasonNumber": 1,
                            "episodeNumber": 1,
                            "title": "The Journey Begins",
                            "airDate": "2023-09-29",
                            "monitored": true,
                            "episodeFileId": 502
                          }
                        ]
                        """,
                    _ => null
                };

                if (json is null)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                });
            }

            if (host == "radarr.prod.lan")
            {
                if (apiKey != "radarr-prod-secret-222")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

                string? json = path switch
                {
                    "/api/v3/system/status" => """{"appName":"Radarr","instanceName":"Homelab Radarr","version":"5.14.0.9383"}""",
                    "/api/v3/rootfolder" => """[{"id":1,"path":"/movies"},{"id":2,"path":"/animemovs"}]""",
                    "/api/v3/system/backup" => "[]",
                    "/api/v3/downloadclient" => """
                        [
                          {
                            "id": 1,
                            "name": "SABnzbd Live",
                            "implementation": "Sabnzbd",
                            "enable": true,
                            "fields": [
                              { "name": "host", "value": "sabnzbd.lan" },
                              { "name": "port", "value": 8080 },
                              { "name": "apiKey", "value": "********" },
                              { "name": "movieCategory", "value": "movies" }
                            ]
                          },
                          {
                            "id": 2,
                            "name": "qBittorrent Live",
                            "implementation": "QBittorrent",
                            "enable": true,
                            "fields": [
                              { "name": "host", "value": "qbittorrent.lan" },
                              { "name": "port", "value": 8080 },
                              { "name": "username", "value": "admin" },
                              { "name": "password", "value": "********" },
                              { "name": "movieCategory", "value": "movies" }
                            ]
                          }
                        ]
                        """,
                    "/api/v3/indexer" => "[]",
                    "/api/v3/customformat" => "[]",
                    "/api/v3/blocklist" => """{"page":1,"pageSize":500,"records":[]}""",
                    "/api/v3/movie" => """
                        [
                          {
                            "id": 77,
                            "title": "Civil War",
                            "year": 2024,
                            "tmdbId": 929590,
                            "imdbId": "tt17279496",
                            "monitored": true,
                            "minimumAvailability": "announced",
                            "originalLanguage": { "id": 1, "name": "English" },
                            "path": "/movies/Civil War (2024)",
                            "rootFolderPath": "/movies",
                            "movieFile": {
                              "id": 801,
                              "relativePath": "Civil.War.2024.mkv",
                              "path": "/movies/Civil War (2024)/Civil.War.2024.mkv",
                              "size": 8192,
                              "quality": { "quality": { "name": "Bluray-2160p" } }
                            }
                          },
                          {
                            "id": 78,
                            "title": "Spirited Away",
                            "year": 2001,
                            "tmdbId": 129,
                            "imdbId": "tt0245429",
                            "monitored": true,
                            "minimumAvailability": "inCinemas",
                            "originalLanguage": { "id": 8, "name": "Japanese" },
                            "path": "/animemovs/Spirited Away (2001)",
                            "rootFolderPath": "/animemovs",
                            "movieFile": {
                              "id": 802,
                              "relativePath": "Spirited.Away.2001.mkv",
                              "path": "/animemovs/Spirited Away (2001)/Spirited.Away.2001.mkv",
                              "size": 8192,
                              "quality": { "quality": { "name": "Bluray-1080p" } }
                            }
                          }
                        ]
                        """,
                    _ => null
                };

                if (json is null)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
