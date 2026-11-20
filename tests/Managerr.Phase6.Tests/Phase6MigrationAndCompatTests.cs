using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dapper;
using Managerr.Catalog;
using Managerr.Downloaders;
using Managerr.Indexers;
using Managerr.Metadata;
using Managerr.Migration;
using Managerr.Profiles;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Managerr.Phase6.Tests;

public sealed class Phase6MigrationAndCompatTests
{
    [Fact]
    public async Task FullEcosystemMigration_ProwlarrZip_SonarrV5_Radarr_DualReadarr_AndOverseerr_DeduplicatesAndAdoptsZeroTouch()
    {
        var tempDir = CreateTempDir();
        try
        {
            var mediaRoot = Path.Combine(tempDir, "media");
            var tvFile = Path.Combine(mediaRoot, "tv", "Severance", "Season 01", "Severance.S01E01.mkv");
            var movieFile = Path.Combine(mediaRoot, "movies", "Dune Part Two (2024)", "Dune.Part.Two.2024.mkv");
            var ebookFile = Path.Combine(mediaRoot, "books", "Andy Weir", "Project.Hail.Mary.epub");
            var audiobookFile = Path.Combine(mediaRoot, "books", "Andy Weir", "Project.Hail.Mary.m4b");

            CreateDummyFile(tvFile, 12_345);
            CreateDummyFile(movieFile, 23_456);
            CreateDummyFile(ebookFile, 3_456);
            CreateDummyFile(audiobookFile, 45_678);

            var tvOriginalWriteUtc = File.GetLastWriteTimeUtc(tvFile);

            var backupsDir = Path.Combine(tempDir, "backups");
            Directory.CreateDirectory(backupsDir);

            // 1. prowlarr.db zipped inside prowlarr-backup.zip (2 canonical indexers + 1 FlareSolverr proxy + ApplicationIndexerMapping)
            var prowlarrRawDb = Path.Combine(backupsDir, "prowlarr.db");
            await BuildProwlarrSqliteAsync(prowlarrRawDb);
            var prowlarrZip = Path.Combine(backupsDir, "prowlarr-backup.zip");
            using (var zf = ZipFile.Open(prowlarrZip, ZipArchiveMode.Create))
            {
                zf.CreateEntryFromFile(prowlarrRawDb, "prowlarr.db");
            }
            File.Delete(prowlarrRawDb);

            // 2. nzbhydra.yml (1 canonical Usenet indexer)
            var hydraYml = Path.Combine(backupsDir, "nzbhydra.yml");
            await File.WriteAllTextAsync(hydraYml, """
                main:
                  port: 5076
                indexers:
                  - name: DrunkenSlug
                    host: https://api.drunkenslug.com
                    apiKey: slug-secret-key
                    searchModuleType: newznab
                """);

            // 3. sonarr.db v5 + radarr.db + dual readarr-ebooks.db & readarr-audiobooks.db + overseerr.sqlite3
            var sonarrDb = Path.Combine(backupsDir, "sonarr.db");
            var radarrDb = Path.Combine(backupsDir, "radarr.db");
            var readarrEbooksDb = Path.Combine(backupsDir, "readarr-ebooks.db");
            var readarrAudioDb = Path.Combine(backupsDir, "readarr-audiobooks.db");
            var overseerrDb = Path.Combine(backupsDir, "overseerr.sqlite3");

            await BuildSonarrV5SqliteAsync(sonarrDb);
            await BuildRadarrSqliteAsync(radarrDb);
            await BuildReadarrSqliteAsync(readarrEbooksDb, isAudiobookInstance: false);
            await BuildReadarrSqliteAsync(readarrAudioDb, isAudiobookInstance: true);
            await BuildOverseerrSqliteAsync(overseerrDb);

            // Managerr ships no tracker definitions: the user has provided one of the two the Prowlarr backup uses
            var customDefinitions = Path.Combine(tempDir, "config", "Definitions", "Custom");
            Directory.CreateDirectory(customDefinitions);
            await File.WriteAllTextAsync(Path.Combine(customDefinitions, "tracker-alpha.yml"), """
                id: tracker-alpha
                name: Tracker Alpha
                description: Test tracker definition provided by the user
                language: en-US
                type: public
                links:
                  - https://tracker-alpha.example/
                caps:
                  categorymappings:
                    - {id: 1, cat: Movies, desc: "Movies"}
                    - {id: 2, cat: TV, desc: "TV"}
                search:
                  paths:
                    - path: "search?q={{ .Keywords }}"
                  rows:
                    selector: "tr.result"
                  fields:
                    title:
                      selector: "a.title"
                    download:
                      selector: "a.download"
                      attribute: href
                """);

            await using var factory = CreateWebFactory(tempDir);
            using var client = await CreateAdminAuthenticatedClientAsync(factory);

            var sources = new List<AdHocMigrationSourceSpec>
            {
                new("Prowlarr Backup Zip", MigrationSourceKinds.Prowlarr, prowlarrZip),
                new("NZBHydra2 Config", MigrationSourceKinds.NzbHydra2, hydraYml),
                new("Overseerr DB", MigrationSourceKinds.Overseerr, overseerrDb),
                new("Sonarr v5", MigrationSourceKinds.Sonarr, sonarrDb),
                new("Radarr v5", MigrationSourceKinds.Radarr, radarrDb),
                new("Readarr Ebooks", MigrationSourceKinds.Readarr, readarrEbooksDb, ReadarrSlotHint: "ebook"),
                new("Readarr Audiobooks", MigrationSourceKinds.Readarr, readarrAudioDb, ReadarrSlotHint: "audiobook"),
            };

            var pathMappings = new Dictionary<string, string>
            {
                ["/tv"] = Path.Combine(mediaRoot, "tv"),
                ["/movies"] = Path.Combine(mediaRoot, "movies"),
                ["/books"] = Path.Combine(mediaRoot, "books"),
            };

            // Stage 1: Interactive Dry-Run Plan — 0 catalog mutations!
            var planResp = await client.PostAsJsonAsync(
                "/api/v1/migration/plan",
                new MigrationPlanAndExecuteRequest(sources, pathMappings, UpliftLanguageCustomFormats: true));
            Assert.Equal(HttpStatusCode.OK, planResp.StatusCode);
            var dryRun = (await planResp.Content.ReadFromJsonAsync<MigrationReportSummary>())!;

            Assert.Equal("dry_run", dryRun.Mode);
            Assert.Equal(8, dryRun.RawIndexerCount);
            Assert.Equal(2, dryRun.DeduplicatedIndexerCount);
            Assert.DoesNotContain(dryRun.Indexers, i => i.Name == "Tracker Beta");
            Assert.Contains(dryRun.Warnings, w => w.Contains("'Tracker Beta'") && w.Contains("tracker-beta.yml") && w.Contains("Definitions/Custom"));
            Assert.Equal(5, dryRun.SkippedProxyClonesCount);
            Assert.Equal(4, dryRun.RawDownloadClientCount);
            Assert.Equal(1, dryRun.ConsolidatedDownloadClientCount);
            Assert.Equal(1, dryRun.BooksUnifiedCount);
            Assert.Equal(1, dryRun.DualReadarrMergedBooksCount);
            Assert.Equal(4, dryRun.VerifiedOnDiskFilesCount);
            Assert.Equal(0L, dryRun.BytesMovedOrModified);
            Assert.Empty(dryRun.UnmappedPaths);

            // Verify dry-run wrote 0 works into catalog
            using (var checkScope = factory.Services.CreateScope())
            {
                var cat = checkScope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
                Assert.Empty(await cat.ListWorksAsync());
            }

            // Stage 2: Execute Zero-Touch Migration!
            var execResp = await client.PostAsJsonAsync(
                "/api/v1/migration/execute",
                new MigrationPlanAndExecuteRequest(sources, pathMappings, UpliftLanguageCustomFormats: true));
            Assert.Equal(HttpStatusCode.OK, execResp.StatusCode);
            var report = (await execResp.Content.ReadFromJsonAsync<MigrationReportSummary>())!;
            Assert.Equal("execute", report.Mode);
            Assert.Equal(0L, report.BytesMovedOrModified);

            // Verify Zero-Touch File Guarantee: file bytes & timestamps untouched!
            Assert.Equal(12_345, new FileInfo(tvFile).Length);
            Assert.Equal(tvOriginalWriteUtc, File.GetLastWriteTimeUtc(tvFile));

            // Verify persisted state across Indexers, Downloaders, Catalog, Crosswalk & Overseerr PlexUsers!
            using var scope = factory.Services.CreateScope();
            var idxSvc = scope.ServiceProvider.GetRequiredService<IUnifiedIndexerService>();
            var dlSvc = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var catSvc = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var crosswalk = scope.ServiceProvider.GetRequiredService<IExternalIdCrosswalkService>();
            var userSvc = scope.ServiceProvider.GetRequiredService<IPlexUserService>();

            var indexers = await idxSvc.ListIndexersAsync();
            Assert.Equal(2, indexers.Count);
            Assert.Contains(indexers, i => i.Name == "Tracker Alpha");
            Assert.Single(await idxSvc.ListProxiesAsync());

            var dlClients = await dlSvc.ListClientsAsync();
            var qbit = Assert.Single(dlClients);
            Assert.Equal("vpn", qbit.Host);
            Assert.Equal(8080, qbit.Port);
            Assert.Equal("tv-sonarr", qbit.CategoryMappings["tv"]);
            Assert.Equal("radarr", qbit.CategoryMappings["movies"]);
            Assert.Equal("readarr-ebooks", qbit.CategoryMappings["ebooks"]);
            Assert.Equal("readarr-audio", qbit.CategoryMappings["audiobooks"]);

            var works = await catSvc.ListWorksAsync();
            Assert.Equal(3, works.Count); // 1 Series (Severance) + 1 Movie (Dune Part Two) + 1 Unified Book (Project Hail Mary)

            // Dual-Readarr Unified Book has both ebook_slot (.epub) and audiobook_slot (.m4b)!
            var bookSummary = works.Single(w => w.WorkKind == "ebooks");
            var bookTree = (await catSvc.GetWorkTreeAsync(bookSummary.Id))!;
            var bookUnits = bookTree.Containers.SelectMany(c => c.Units).ToList();
            var ebookUnit = bookUnits.Single(u => u.UnitKind == "ebook_slot");
            var audioUnit = bookUnits.Single(u => u.UnitKind == "audiobook_slot");
            Assert.Single(ebookUnit.Files);
            Assert.Single(audioUnit.Files);
            Assert.Equal("ImportedUnverified", ebookUnit.Files[0].VerifiedStatus);
            Assert.Equal("ImportedUnverified", audioUnit.Files[0].VerifiedStatus);

            // Goodreads ID crosswalked to Open Library, Hardcover, and Audnexus!
            var resolvedBookIds = await crosswalk.ResolveConnectedIdsAsync(new Dictionary<string, string> { ["goodreads"] = "54493401" });
            Assert.Contains("openlibrary", resolvedBookIds.Keys);
            Assert.Contains("hardcover", resolvedBookIds.Keys);
            Assert.Contains("audnexus", resolvedBookIds.Keys);

            // Overseerr PlexUser (plexId = 889900) correlated & bound as AddedByUserId on Dune Part Two!
            var seerrPlexUser = await userSvc.GetByPlexUserIdAsync(889900);
            Assert.NotNull(seerrPlexUser);
            Assert.Equal("cinephile", seerrPlexUser!.Username);
            var movieWork = works.Single(w => w.WorkKind == "movies");
            Assert.Equal(seerrPlexUser.Id, movieWork.AddedByUserId);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task ShadowModeComparator_LogsReadOnlyLanguageUpgradeVsLegacySonarr_WithoutGrabbing()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var client = await CreateAdminAuthenticatedClientAsync(factory);

            using var scope = factory.Services.CreateScope();
            var profiles = await scope.ServiceProvider.GetRequiredService<IComposableProfileService>().ListProfilesAsync();
            var dualProfile = profiles.Single(p => p.Slug == "hd-uhd-de-en-dual");
            var dlSvc = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();

            var evalResp = await client.PostAsJsonAsync(
                "/api/v1/migration/shadow-decisions/evaluate",
                new ShadowModeEvaluateRequest(
                    MediaVertical: "tv",
                    WorkTitle: "Severance",
                    ReleaseTitle: "Severance.S01E01.German.DL.1080p.WEB-DL.DDP5.1.H.264-GRP",
                    ProfileId: dualProfile.Id,
                    LegacyApp: "sonarr",
                    LegacyDecision: "Rejected (Cutoff Met: BluRay-1080p)",
                    OriginalLanguage: "en",
                    ExistingFileQuality: "BluRay-1080p",
                    ExistingAudioLanguages: ["en"]));

            Assert.Equal(HttpStatusCode.OK, evalResp.StatusCode);
            var decision = (await evalResp.Content.ReadFromJsonAsync<ShadowModeDecisionSummary>())!;
            Assert.Equal(ProfileDecisionKinds.LanguageUpgrade, decision.ManagerrDecision);
            Assert.True(decision.WouldUpgradeLanguage);

            var listResp = await client.GetFromJsonAsync<List<ShadowModeDecisionSummary>>("/api/v1/migration/shadow-decisions");
            Assert.Single(listResp!);
            Assert.Empty(await dlSvc.GetUnifiedQueueAsync());
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task CutoverAndCompatShims_OverseerrPlexUserCorrelation_DualReadarrSlots_TorznabCaps_AndStrictRbacIsolation()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var adminClient = await CreateAdminAuthenticatedClientAsync(factory);

            // Create a Plex Friend 'cinefriend' (PlexUserId = 7788) with PreferredAudioLang = "de"
            PlexUserSummary cineFriend;
            using (var scope = factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<IPlexUserService>();
                cineFriend = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
                    7788, "uuid-7788", "cinefriend", "cine@plex.local", null, false, false, true, ["1", "2"],
                    PreferredAudioLang: "de", PreferredSubLang: "en"));
            }

            // 1-Click Cutover issues scoped compat keys!
            var cutoverResp = await adminClient.PostAsJsonAsync(
                "/api/v1/migration/cutover",
                new CutoverExecuteRequest(RunFinalDeltaSync: false, GenerateScopedCompatKeys: true, GenerateSeparateReadarrAudiobookKey: true));
            Assert.Equal(HttpStatusCode.OK, cutoverResp.StatusCode);
            var cutover = (await cutoverResp.Content.ReadFromJsonAsync<CutoverExecutionResult>())!;
            Assert.Equal(6, cutover.GeneratedCompatKeys.Count);

            var sonarrKey = cutover.GeneratedCompatKeys.Single(k => k.Scope == PermissionScopes.CompatSonarr).RawApiKeyOnce;
            var radarrKey = cutover.GeneratedCompatKeys.Single(k => k.Scope == PermissionScopes.CompatRadarr).RawApiKeyOnce;
            var readarrEbookKey = cutover.GeneratedCompatKeys.Single(k => k.MountUrl.Contains("slot=ebook")).RawApiKeyOnce;
            var readarrAudioKey = cutover.GeneratedCompatKeys.Single(k => k.MountUrl.Contains("slot=audiobook")).RawApiKeyOnce;
            var prowlarrKey = cutover.GeneratedCompatKeys.Single(k => k.Scope == PermissionScopes.CompatProwlarr).RawApiKeyOnce;

            using var anonClient = factory.CreateClient();

            // 1. Strict RBAC Scope Isolation: radarrKey works on /compat/radarr (via X-Api-Key AND ?apikey=), 403 on /compat/sonarr & /api/v1/users!
            Assert.Equal(HttpStatusCode.OK, (await anonClient.GetAsync($"/compat/radarr/api/v3/system/status?apikey={radarrKey}")).StatusCode);

            using var radarrClient = factory.CreateClient();
            radarrClient.DefaultRequestHeaders.Add("X-Api-Key", radarrKey);
            Assert.Equal(HttpStatusCode.OK, (await radarrClient.GetAsync("/compat/radarr/api/v3/system/status")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await radarrClient.GetAsync("/compat/sonarr/api/v3/system/status")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await radarrClient.GetAsync("/api/v1/users")).StatusCode);

            // 2. Overseerr Tag ("7788 - cinefriend") + Movie Request via /compat/radarr correlates PlexUser & promotes PreferredAudioLang ("de")!
            var tagResp = await radarrClient.PostAsJsonAsync("/compat/radarr/api/v3/tag", new { label = "7788 - cinefriend" });
            Assert.Equal(HttpStatusCode.Created, tagResp.StatusCode);
            var createdTag = (await tagResp.Content.ReadFromJsonAsync<CompatTagDto>())!;

            var addMovieResp = await radarrClient.PostAsJsonAsync("/compat/radarr/api/v3/movie", new
            {
                title = "Blade Runner 2049",
                year = 2017,
                tmdbId = 335984,
                imdbId = "tt1856101",
                monitored = true,
                rootFolderPath = Path.Combine(tempDir, "movies"),
                tags = new[] { createdTag.Id },
            });
            Assert.Equal(HttpStatusCode.Created, addMovieResp.StatusCode);
            var addedMovieJson = (await addMovieResp.Content.ReadFromJsonAsync<JsonObject>())!;
            var movieId = addedMovieJson["id"]!.GetValue<long>();
            Assert.Equal(cineFriend.Id, addedMovieJson["addedByPlexUserId"]!.GetValue<long>());

            using (var scope = factory.Services.CreateScope())
            {
                var cat = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
                var tree = (await cat.GetWorkTreeAsync(movieId))!;
                var movieUnit = tree.Containers.SelectMany(c => c.Units).Single();
                Assert.Contains("de", movieUnit.RequiredAudioLangs);
            }

            // 3. Sonarr v3 Shim round-trip
            using var sonarrClient = factory.CreateClient();
            sonarrClient.DefaultRequestHeaders.Add("X-Api-Key", sonarrKey);
            var addSeriesResp = await sonarrClient.PostAsJsonAsync("/compat/sonarr/api/v3/series", new
            {
                title = "Dark",
                year = 2017,
                tvdbId = 334824,
                monitored = true,
                rootFolderPath = Path.Combine(tempDir, "tv"),
            });
            Assert.Equal(HttpStatusCode.Created, addSeriesResp.StatusCode);

            // 4. Dual-Slot Readarr Shims: Ebook key + Audiobook key bind to the SAME unified Book container!
            using var ebookClient = factory.CreateClient();
            ebookClient.DefaultRequestHeaders.Add("X-Api-Key", readarrEbookKey);
            var addEbookResp = await ebookClient.PostAsJsonAsync("/compat/readarr/api/v1/book", new
            {
                title = "Dune",
                foreignBookId = "OL-DUNE-1965",
                authorTitle = "Frank Herbert",
                rootFolderPath = Path.Combine(tempDir, "books"),
            });
            Assert.Equal(HttpStatusCode.Created, addEbookResp.StatusCode);
            var ebookObj = (await addEbookResp.Content.ReadFromJsonAsync<JsonObject>())!;
            var duneWorkId = ebookObj["id"]!.GetValue<long>();
            Assert.Equal("ebook_slot", ebookObj["targetSlot"]!.GetValue<string>());

            using var audioClient = factory.CreateClient();
            audioClient.DefaultRequestHeaders.Add("X-Api-Key", readarrAudioKey);
            var addAudioResp = await audioClient.PostAsJsonAsync("/compat/readarr/api/v1/book", new
            {
                title = "Dune",
                foreignBookId = "OL-DUNE-1965",
                authorTitle = "Frank Herbert",
                rootFolderPath = Path.Combine(tempDir, "books"),
            });
            Assert.Equal(HttpStatusCode.Created, addAudioResp.StatusCode);
            var audioObj = (await addAudioResp.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Equal(duneWorkId, audioObj["id"]!.GetValue<long>());
            Assert.Equal("audiobook_slot", audioObj["targetSlot"]!.GetValue<string>());

            // 5. Torznab XML Capabilities Feed via ?apikey= query param
            var capsResp = await anonClient.GetAsync($"/api/v1/torznab/0/api?t=caps&apikey={prowlarrKey}");
            Assert.Equal(HttpStatusCode.OK, capsResp.StatusCode);
            var capsXml = await capsResp.Content.ReadAsStringAsync();
            Assert.Contains("<caps>", capsXml);
            Assert.Contains("Audio/Audiobook", capsXml);
            Assert.Contains("Books/EBook", capsXml);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task SeerrWorkflow_DualScopeKey_TrailingSlashNormalization_RadarrAndSonarrLookupAddUpdateAndDelete_FullContractParity()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var adminClient = await CreateAdminAuthenticatedClientAsync(factory);

            // 1. Mint a single Seerr dual-scope API key (compat:sonarr + compat:radarr) via POST /api/v1/auth/tokens
            var tokenResp = await adminClient.PostAsJsonAsync("/api/v1/auth/tokens", new
            {
                name = "Seerr (Sonarr + Radarr)",
                scopes = new[] { PermissionScopes.CompatSonarr, PermissionScopes.CompatRadarr },
            });
            Assert.Equal(HttpStatusCode.Created, tokenResp.StatusCode);
            var tokenBody = (await tokenResp.Content.ReadFromJsonAsync<JsonObject>())!;
            var seerrKey = tokenBody["rawTokenOnce"]!.GetValue<string>();
            Assert.StartsWith("mgr_live_", seerrKey);

            // Seerr's ExternalAPI.ts authenticates exclusively via ?apikey= query string (no X-Api-Key header)
            using var seerrHttp = factory.CreateClient();

            // 2. Trailing-slash Base URL normalization (/compat/radarr//api/v3 & /compat/sonarr//api/v3) + rootfolder + profiles
            var radarrStatusResp = await seerrHttp.GetAsync($"/compat/radarr//api/v3/system/status?apikey={seerrKey}");
            Assert.Equal(HttpStatusCode.OK, radarrStatusResp.StatusCode);
            var radarrStatus = (await radarrStatusResp.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Equal("Radarr", radarrStatus["appName"]!.GetValue<string>());

            var sonarrStatusResp = await seerrHttp.GetAsync($"/compat/sonarr//api/v3/system/status?apikey={seerrKey}");
            Assert.Equal(HttpStatusCode.OK, sonarrStatusResp.StatusCode);
            var sonarrStatus = (await sonarrStatusResp.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Equal("Sonarr", sonarrStatus["appName"]!.GetValue<string>());

            var radarrRoots = (await seerrHttp.GetFromJsonAsync<JsonArray>($"/compat/radarr/api/v3/rootfolder?apikey={seerrKey}"))!;
            Assert.NotEmpty(radarrRoots);
            Assert.True(radarrRoots[0]!["freeSpace"]!.GetValue<long>() > 0);
            Assert.True(radarrRoots[0]!["totalSpace"]!.GetValue<long>() > 0);

            var sonarrLangProfiles = (await seerrHttp.GetFromJsonAsync<JsonArray>($"/compat/sonarr/api/v3/languageprofile?apikey={seerrKey}"))!;
            Assert.NotEmpty(sonarrLangProfiles);
            Assert.NotNull(sonarrLangProfiles[0]!["languages"] as JsonArray);

            var radarrProfiles = (await seerrHttp.GetFromJsonAsync<JsonArray>($"/compat/radarr/api/v3/qualityprofile?apikey={seerrKey}"))!;
            Assert.NotEmpty(radarrProfiles);
            var selectedProfileId = radarrProfiles[0]!["id"]!.GetValue<int>();

            // 3. Radarr Seerr Lookup -> Tag -> Add -> Deduplicated Re-Add -> Local Lookup -> PUT Re-Monitor -> Command -> Delete
            var preLookupMovie = (await seerrHttp.GetFromJsonAsync<JsonObject>(
                $"/compat/radarr/api/v3/movie/lookup/tmdb?tmdbId=693134&apikey={seerrKey}"))!;
            Assert.Equal(0L, preLookupMovie["id"]!.GetValue<long>());
            Assert.Equal(693134, preLookupMovie["tmdbId"]!.GetValue<int>());
            Assert.NotNull(preLookupMovie["tags"] as JsonArray);

            var tagCreateResp = await seerrHttp.PostAsJsonAsync(
                $"/compat/radarr/api/v3/tag?apikey={seerrKey}",
                new { label = "seerr-request" });
            Assert.Equal(HttpStatusCode.Created, tagCreateResp.StatusCode);
            var tagId = ((await tagCreateResp.Content.ReadFromJsonAsync<JsonObject>())!)["id"]!.GetValue<int>();

            var addMovieResp = await seerrHttp.PostAsJsonAsync(
                $"/compat/radarr/api/v3/movie?apikey={seerrKey}",
                new
                {
                    title = "Dune: Part Two",
                    year = 2024,
                    tmdbId = 693134,
                    imdbId = "tt15239678",
                    qualityProfileId = selectedProfileId,
                    rootFolderPath = Path.Combine(tempDir, "movies"),
                    monitored = false,
                    tags = new[] { tagId },
                    addOptions = new { searchForMovie = false },
                });
            Assert.Equal(HttpStatusCode.Created, addMovieResp.StatusCode);
            var addedMovie = (await addMovieResp.Content.ReadFromJsonAsync<JsonObject>())!;
            var movieId = addedMovie["id"]!.GetValue<long>();
            Assert.True(movieId > 0);
            Assert.False(addedMovie["monitored"]!.GetValue<bool>());
            Assert.Equal(selectedProfileId, addedMovie["qualityProfileId"]!.GetValue<int>());
            Assert.Equal(selectedProfileId, addedMovie["profileId"]!.GetValue<int>());
            Assert.Contains(tagId, (addedMovie["tags"] as JsonArray)!.Select(x => x!.GetValue<int>()));

            // Lookup by TMDB after adding returns local catalog work (id > 0) so Seerr detects existing movie!
            var postLookupMovie = (await seerrHttp.GetFromJsonAsync<JsonObject>(
                $"/compat/radarr/api/v3/movie/lookup/tmdb?tmdbId=693134&apikey={seerrKey}"))!;
            Assert.Equal(movieId, postLookupMovie["id"]!.GetValue<long>());

            // GET /movie?tmdbId= filters strictly by tmdbId
            var byTmdbHit = (await seerrHttp.GetFromJsonAsync<JsonArray>(
                $"/compat/radarr/api/v3/movie?tmdbId=693134&apikey={seerrKey}"))!;
            Assert.Single(byTmdbHit);
            var byTmdbMiss = (await seerrHttp.GetFromJsonAsync<JsonArray>(
                $"/compat/radarr/api/v3/movie?tmdbId=999999&apikey={seerrKey}"))!;
            Assert.Empty(byTmdbMiss);

            // PUT /movie flips monitored=true when Seerr re-requests an unmonitored movie
            var putMovieResp = await seerrHttp.PutAsJsonAsync(
                $"/compat/radarr/api/v3/movie?apikey={seerrKey}",
                new
                {
                    id = movieId,
                    tmdbId = 693134,
                    monitored = true,
                    qualityProfileId = selectedProfileId,
                    tags = new[] { tagId },
                    addOptions = new { searchForMovie = true },
                });
            Assert.Equal(HttpStatusCode.Accepted, putMovieResp.StatusCode);
            var updatedMovie = (await putMovieResp.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.True(updatedMovie["monitored"]!.GetValue<bool>());

            // Duplicate POST /movie with same tmdbId deduplicates cleanly
            var dupMovieResp = await seerrHttp.PostAsJsonAsync(
                $"/compat/radarr/api/v3/movie?apikey={seerrKey}",
                new { title = "Dune: Part Two", tmdbId = 693134, monitored = true });
            var dupMovie = (await dupMovieResp.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Equal(movieId, dupMovie["id"]!.GetValue<long>());

            var radarrCmdResp = await seerrHttp.PostAsJsonAsync(
                $"/compat/radarr/api/v3/command?apikey={seerrKey}",
                new { name = "MoviesSearch", movieIds = new[] { movieId } });
            Assert.Equal(HttpStatusCode.Created, radarrCmdResp.StatusCode);

            // 4. Sonarr Seerr Add Series -> Local Lookup with seasons[] -> PUT Season 2 + New Season 3 -> SeasonSearch Command
            var addSeriesResp = await seerrHttp.PostAsJsonAsync(
                $"/compat/sonarr/api/v3/series?apikey={seerrKey}",
                new
                {
                    title = "Severance",
                    year = 2022,
                    tvdbId = 371980,
                    qualityProfileId = selectedProfileId,
                    languageProfileId = 1,
                    rootFolderPath = Path.Combine(tempDir, "tv"),
                    monitored = true,
                    seasonFolder = true,
                    tags = new[] { tagId },
                    seasons = new[]
                    {
                        new { seasonNumber = 1, monitored = true },
                        new { seasonNumber = 2, monitored = false },
                    },
                    addOptions = new { monitor = "all", searchForMissingEpisodes = true },
                });
            Assert.Equal(HttpStatusCode.Created, addSeriesResp.StatusCode);
            var addedSeries = (await addSeriesResp.Content.ReadFromJsonAsync<JsonObject>())!;
            var seriesId = addedSeries["id"]!.GetValue<long>();
            Assert.True(seriesId > 0);
            Assert.Contains(tagId, (addedSeries["tags"] as JsonArray)!.Select(x => x!.GetValue<int>()));

            var initialSeasons = (addedSeries["seasons"] as JsonArray)!;
            Assert.Equal(2, initialSeasons.Count);
            Assert.True(initialSeasons.Single(s => s!["seasonNumber"]!.GetValue<int>() == 1)!["monitored"]!.GetValue<bool>());
            Assert.False(initialSeasons.Single(s => s!["seasonNumber"]!.GetValue<int>() == 2)!["monitored"]!.GetValue<bool>());
            Assert.NotNull(initialSeasons[0]!["statistics"]);

            // GET /series/lookup?term=tvdb:371980 returns local series with id > 0 and seasons[]
            var seriesLookup = (await seerrHttp.GetFromJsonAsync<JsonArray>(
                $"/compat/sonarr/api/v3/series/lookup?term=tvdb:371980&apikey={seerrKey}"))!;
            var lookedUpSeries = Assert.Single(seriesLookup)!.AsObject();
            Assert.Equal(seriesId, lookedUpSeries["id"]!.GetValue<long>());
            Assert.Equal(2, (lookedUpSeries["seasons"] as JsonArray)!.Count);

            // GET /series?tvdbId= filters strictly by tvdbId
            Assert.Single((await seerrHttp.GetFromJsonAsync<JsonArray>($"/compat/sonarr/api/v3/series?tvdbId=371980&apikey={seerrKey}"))!);
            Assert.Empty((await seerrHttp.GetFromJsonAsync<JsonArray>($"/compat/sonarr/api/v3/series?tvdbId=999999&apikey={seerrKey}"))!);

            // PUT /series when user requests Season 2 + newly aired Season 3 in Seerr
            var putSeriesResp = await seerrHttp.PutAsJsonAsync(
                $"/compat/sonarr/api/v3/series?apikey={seerrKey}",
                new
                {
                    id = seriesId,
                    tvdbId = 371980,
                    monitored = true,
                    tags = new[] { tagId },
                    seasons = new[]
                    {
                        new { seasonNumber = 1, monitored = true },
                        new { seasonNumber = 2, monitored = true },
                        new { seasonNumber = 3, monitored = true },
                    },
                    addOptions = new { searchForMissingEpisodes = true },
                });
            Assert.Equal(HttpStatusCode.Accepted, putSeriesResp.StatusCode);
            var updatedSeries = (await putSeriesResp.Content.ReadFromJsonAsync<JsonObject>())!;
            var updatedSeasons = (updatedSeries["seasons"] as JsonArray)!;
            Assert.Equal(3, updatedSeasons.Count);
            Assert.All(updatedSeasons, s => Assert.True(s!["monitored"]!.GetValue<bool>()));

            var sonarrCmdResp = await seerrHttp.PostAsJsonAsync(
                $"/compat/sonarr/api/v3/command?apikey={seerrKey}",
                new { name = "SeasonSearch", seriesId, seasonNumber = 2 });
            Assert.Equal(HttpStatusCode.Created, sonarrCmdResp.StatusCode);

            // Queue & Delete checks
            Assert.Equal(HttpStatusCode.OK, (await seerrHttp.GetAsync($"/compat/sonarr/api/v3/queue?apikey={seerrKey}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await seerrHttp.GetAsync($"/compat/radarr/api/v3/queue?apikey={seerrKey}")).StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await seerrHttp.DeleteAsync($"/compat/sonarr/api/v3/series/{seriesId}?apikey={seerrKey}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await seerrHttp.DeleteAsync($"/compat/radarr/api/v3/movie/{movieId}?apikey={seerrKey}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await seerrHttp.GetAsync($"/compat/sonarr/api/v3/series/{seriesId}?apikey={seerrKey}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await seerrHttp.GetAsync($"/compat/radarr/api/v3/movie/{movieId}?apikey={seerrKey}")).StatusCode);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    // -------------------------------------------------------------------------
    // Test Fixture Builders for Real SQLite Legacy Databases
    // -------------------------------------------------------------------------

    private static async Task BuildProwlarrSqliteAsync(string dbPath)
    {
        await using var conn = OpenWriteSqlite(dbPath);
        await conn.ExecuteAsync("""
            CREATE TABLE VersionInfo (Version INTEGER NOT NULL);
            INSERT INTO VersionInfo VALUES (62);

            CREATE TABLE IndexerProxies (Id INTEGER PRIMARY KEY, Name TEXT, Implementation TEXT, Settings TEXT);
            INSERT INTO IndexerProxies VALUES (1, 'Homelab FlareSolverr', 'FlareSolverr', '{"host":"http://flaresolverr:8191"}');

            CREATE TABLE Indexers (Id INTEGER PRIMARY KEY, Name TEXT, Implementation TEXT, Settings TEXT, ConfigContract TEXT);
            INSERT INTO Indexers VALUES
                (1, 'Tracker Alpha', 'Cardigann', '{"definitionFile":"tracker-alpha","baseUrl":"https://tracker-alpha.example","categories":[2000,5000]}', 'CardigannSettings'),
                (2, 'Tracker Beta', 'Cardigann', '{"definitionFile":"tracker-beta","baseUrl":"https://tracker-beta.example","categories":[5070]}', 'CardigannSettings');

            CREATE TABLE ApplicationIndexerMapping (Id INTEGER PRIMARY KEY, IndexerId INTEGER, AppIndexerId INTEGER);
            INSERT INTO ApplicationIndexerMapping VALUES (1, 1, 101), (2, 2, 102);
            """);
    }

    private static async Task BuildSonarrV5SqliteAsync(string dbPath)
    {
        await using var conn = OpenWriteSqlite(dbPath);
        await conn.ExecuteAsync("""
            CREATE TABLE VersionInfo (Version INTEGER NOT NULL);
            INSERT INTO VersionInfo VALUES (215);

            CREATE TABLE RootFolders (Id INTEGER PRIMARY KEY, Path TEXT);
            INSERT INTO RootFolders VALUES (1, '/tv');

            CREATE TABLE DownloadClients (Id INTEGER PRIMARY KEY, Name TEXT, Implementation TEXT, Settings TEXT, Enable INTEGER);
            INSERT INTO DownloadClients VALUES
                (1, 'qBittorrent (Sonarr)', 'QBittorrent', '{"host":"vpn","port":8080,"username":"admin","tvCategory":"tv-sonarr"}', 1);

            CREATE TABLE RemotePathMappings (Id INTEGER PRIMARY KEY, Host TEXT, RemotePath TEXT, LocalPath TEXT);
            INSERT INTO RemotePathMappings VALUES (1, 'vpn', '/downloads/tv', '/tv');

            CREATE TABLE Indexers (Id INTEGER PRIMARY KEY, Name TEXT, Implementation TEXT, Settings TEXT, ConfigContract TEXT);
            INSERT INTO Indexers VALUES
                (101, 'Tracker Alpha (Prowlarr)', 'Torznab', '{"baseUrl":"http://prowlarr:9696/1/"}', 'TorznabSettings'),
                (102, 'Tracker Beta (Prowlarr)', 'Torznab', '{"baseUrl":"http://prowlarr:9696/2/"}', 'TorznabSettings');

            CREATE TABLE CustomFormats (Id INTEGER PRIMARY KEY, Name TEXT, IncludeCustomFormatWhenRenaming INTEGER, Specifications TEXT);
            INSERT INTO CustomFormats VALUES
                (1, 'German Dual Audio', 0, '[{"name":"German","implementation":"LanguageSpecification","negate":false,"required":true,"fields":{"value":4}}]');

            CREATE TABLE QualityProfiles (Id INTEGER PRIMARY KEY, Name TEXT);
            INSERT INTO QualityProfiles VALUES (1, 'HD-1080p/2160p');

            CREATE TABLE Blocklist (Id INTEGER PRIMARY KEY, SourceTitle TEXT, TorrentInfoHash TEXT);
            INSERT INTO Blocklist VALUES (1, 'Fake.Dub.1080p', 'deadbeef11223344556677889900aabbccddeeff');

            CREATE TABLE Series (Id INTEGER PRIMARY KEY, TvdbId INTEGER, ImdbId TEXT, Title TEXT, Year INTEGER, Path TEXT, Monitored INTEGER, SeriesType INTEGER);
            INSERT INTO Series VALUES (1, 371980, 'tt11280740', 'Severance', 2022, '/tv/Severance', 1, 0);

            CREATE TABLE EpisodeFiles (Id INTEGER PRIMARY KEY, RelativePath TEXT, Size INTEGER, Quality TEXT);
            INSERT INTO EpisodeFiles VALUES (10, 'Season 01/Severance.S01E01.mkv', 12345, '{"quality":{"name":"WEBDL-2160p"}}');

            CREATE TABLE Episodes (Id INTEGER PRIMARY KEY, SeriesId INTEGER, SeasonNumber INTEGER, EpisodeNumber INTEGER, Title TEXT, AirDateUtc TEXT, Monitored INTEGER, EpisodeFileId INTEGER);
            INSERT INTO Episodes VALUES (100, 1, 1, 1, 'Good News About Hell', '2022-02-18', 1, 10);
            """);
    }

    private static async Task BuildRadarrSqliteAsync(string dbPath)
    {
        await using var conn = OpenWriteSqlite(dbPath);
        await conn.ExecuteAsync("""
            CREATE TABLE VersionInfo (Version INTEGER NOT NULL);
            INSERT INTO VersionInfo VALUES (243);

            CREATE TABLE RootFolders (Id INTEGER PRIMARY KEY, Path TEXT);
            INSERT INTO RootFolders VALUES (1, '/movies');

            CREATE TABLE DownloadClients (Id INTEGER PRIMARY KEY, Name TEXT, Implementation TEXT, Settings TEXT, Enable INTEGER);
            INSERT INTO DownloadClients VALUES
                (1, 'qBittorrent (Radarr)', 'QBittorrent', '{"host":"vpn","port":8080,"username":"admin","movieCategory":"radarr"}', 1);

            CREATE TABLE Indexers (Id INTEGER PRIMARY KEY, Name TEXT, Implementation TEXT, Settings TEXT, ConfigContract TEXT);
            INSERT INTO Indexers VALUES
                (201, 'Tracker Alpha (Prowlarr)', 'Torznab', '{"baseUrl":"http://prowlarr:9696/1/"}', 'TorznabSettings');

            CREATE TABLE MovieMetadata (Id INTEGER PRIMARY KEY, TmdbId INTEGER, ImdbId TEXT, Title TEXT, Year INTEGER);
            INSERT INTO MovieMetadata VALUES (1, 693134, 'tt15239678', 'Dune: Part Two', 2024);

            CREATE TABLE MovieFiles (Id INTEGER PRIMARY KEY, RelativePath TEXT, Size INTEGER, Quality TEXT);
            INSERT INTO MovieFiles VALUES (20, 'Dune.Part.Two.2024.mkv', 23456, '{"quality":{"name":"Remux-2160p"}}');

            CREATE TABLE Movies (Id INTEGER PRIMARY KEY, MovieMetadataId INTEGER, Path TEXT, Monitored INTEGER, MovieFileId INTEGER);
            INSERT INTO Movies VALUES (1, 1, '/movies/Dune Part Two (2024)', 1, 20);
            """);
    }

    private static async Task BuildReadarrSqliteAsync(string dbPath, bool isAudiobookInstance)
    {
        var category = isAudiobookInstance ? "readarr-audio" : "readarr-ebooks";
        var fileName = isAudiobookInstance ? "Project.Hail.Mary.m4b" : "Project.Hail.Mary.epub";
        var fileSize = isAudiobookInstance ? 45678 : 3456;

        await using var conn = OpenWriteSqlite(dbPath);
        await conn.ExecuteAsync($$"""
            CREATE TABLE VersionInfo (Version INTEGER NOT NULL);
            INSERT INTO VersionInfo VALUES (38);

            CREATE TABLE RootFolders (Id INTEGER PRIMARY KEY, Path TEXT);
            INSERT INTO RootFolders VALUES (1, '/books');

            CREATE TABLE DownloadClients (Id INTEGER PRIMARY KEY, Name TEXT, Implementation TEXT, Settings TEXT, Enable INTEGER);
            INSERT INTO DownloadClients VALUES
                (1, 'qBittorrent (Readarr)', 'QBittorrent', '{"host":"vpn","port":8080,"username":"admin","bookCategory":"{{category}}"}', 1);

            CREATE TABLE Indexers (Id INTEGER PRIMARY KEY, Name TEXT, Implementation TEXT, Settings TEXT, ConfigContract TEXT);
            INSERT INTO Indexers VALUES
                (301, 'DrunkenSlug (NZBHydra2)', 'Newznab', '{"baseUrl":"http://nzbhydra2:5076/"}', 'NewznabSettings');

            CREATE TABLE AuthorMetadata (Id INTEGER PRIMARY KEY, Name TEXT);
            INSERT INTO AuthorMetadata VALUES (1, 'Andy Weir');

            CREATE TABLE Authors (Id INTEGER PRIMARY KEY, AuthorMetadataId INTEGER, Path TEXT);
            INSERT INTO Authors VALUES (1, 1, '/books/Andy Weir');

            CREATE TABLE Books (Id INTEGER PRIMARY KEY, AuthorMetadataId INTEGER, ForeignBookId TEXT, Title TEXT, Monitored INTEGER);
            INSERT INTO Books VALUES (1, 1, '54493401', 'Project Hail Mary', 1);

            CREATE TABLE Editions (Id INTEGER PRIMARY KEY, BookId INTEGER, Isbn13 TEXT);
            INSERT INTO Editions VALUES (10, 1, '9780593135204');

            CREATE TABLE BookFiles (Id INTEGER PRIMARY KEY, EditionId INTEGER, Path TEXT, Size INTEGER, Quality TEXT);
            INSERT INTO BookFiles VALUES (50, 10, '/books/Andy Weir/{{fileName}}', {{fileSize}}, 'Retail');
            """);
    }

    private static async Task BuildOverseerrSqliteAsync(string dbPath)
    {
        await using var conn = OpenWriteSqlite(dbPath);
        await conn.ExecuteAsync("""
            CREATE TABLE "user" (id INTEGER PRIMARY KEY, plexId INTEGER, plexUsername TEXT, email TEXT, avatar TEXT);
            INSERT INTO "user" VALUES (5, 889900, 'cinephile', 'cinephile@plex.local', NULL);

            CREATE TABLE media (id INTEGER PRIMARY KEY, tmdbId INTEGER, tvdbId INTEGER);
            INSERT INTO media VALUES (12, 693134, NULL);

            CREATE TABLE media_request (id INTEGER PRIMARY KEY, type TEXT, status INTEGER, mediaId INTEGER, requestedById INTEGER);
            INSERT INTO media_request VALUES (99, 'movie', 2, 12, 5);
            """);
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

    private static WebApplicationFactory<Program> CreateWebFactory(string tempDir) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                    ["Managerr:MasterKey"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                });
            });
        });

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
        var p = Path.Combine(Path.GetTempPath(), $"managerr-p6-test-{Guid.NewGuid():N}");
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

    [Fact]
    public async Task ArrApiTokenImporter_AutoDetectsAndPullsSonarrRadarrAndProwlarrViaApiKey_InPlace()
    {
        var tempDir = CreateTempDir();
        try
        {
            var tvRoot = Path.Combine(tempDir, "media", "tv");
            var movieRoot = Path.Combine(tempDir, "media", "movies");
            var epFileOnDisk = Path.Combine(tvRoot, "Silo", "Season 01", "Silo.S01E01.German.DL.2160p.WEB-DL.mkv");
            var movieFileOnDisk = Path.Combine(movieRoot, "Civil War (2024)", "Civil.War.2024.German.DL.2160p.UHD.BluRay.mkv");
            CreateDummyFile(epFileOnDisk, 8192);
            CreateDummyFile(movieFileOnDisk, 16384);

            var fakeSafeHttp = new FakeArrLiveApiSafeHttpClientFactory();

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
                    services.AddSingleton<Managerr.Common.Net.ISafeHttpClientFactory>(fakeSafeHttp);
                });
            });

            using var adminClient = await CreateAdminAuthenticatedClientAsync(factory);

            var pathMappings = new Dictionary<string, string>
            {
                ["/tv"] = tvRoot,
                ["/movies"] = movieRoot,
            };

            // 1. A URL behind reverse-proxy Basic Auth fails with a clear message; the API key is not sent anywhere else
            FakeArrLiveApiHandler.ContactedHosts.Clear();
            var proxiedResp = await adminClient.PostAsJsonAsync(
                "/api/v1/migration/arr-api/probe",
                new ArrApiTokenProbeRequest(
                    BaseUrl: "https://sonarr.example.com/",
                    ApiKey: "sonarr-secret-token-111",
                    SourceKind: "auto",
                    PathMappings: pathMappings));
            var proxiedResult = (await proxiedResp.Content.ReadFromJsonAsync<ArrApiTokenProbeResult>())!;
            Assert.False(proxiedResult.Ok);
            Assert.All(FakeArrLiveApiHandler.ContactedHosts, h => Assert.Equal("sonarr.example.com", h));

            // 2. Probe Sonarr at its internal address
            var probeResp = await adminClient.PostAsJsonAsync(
                "/api/v1/migration/arr-api/probe",
                new ArrApiTokenProbeRequest(
                    BaseUrl: "http://sonarr.lan:8989/",
                    ApiKey: "sonarr-secret-token-111",
                    SourceKind: "auto",
                    PathMappings: pathMappings));
            probeResp.EnsureSuccessStatusCode();
            var probeResult = (await probeResp.Content.ReadFromJsonAsync<ArrApiTokenProbeResult>())!;
            Assert.True(probeResult.Ok);
            Assert.Equal("sonarr", probeResult.DetectedKind);
            Assert.Equal("Sonarr", probeResult.AppName);
            Assert.Equal("4.0.10.2544", probeResult.Version);
            Assert.Equal("http://sonarr.lan:8989", probeResult.NormalizedBaseUrl);
            Assert.Equal(1, probeResult.CatalogItemsCount);
            Assert.Equal(1, probeResult.MediaFilesCount);
            Assert.Equal(1, probeResult.IndexersCount);
            Assert.Equal(1, probeResult.DownloadClientsCount);
            Assert.Equal(2, probeResult.BlocklistCount);

            // 3. Import Prowlarr via API token (auto-detects Prowlarr and wires masked-key Usenet indexer to Prowlarr feed URL with token)
            var prowlarrImportResp = await adminClient.PostAsJsonAsync(
                "/api/v1/migration/arr-api/import",
                new ArrApiTokenImportRequest(
                    BaseUrl: "http://prowlarr.lan:9696",
                    ApiKey: "prowlarr-secret-token-999",
                    SourceKind: "auto",
                    DryRun: false,
                    SaveAsSource: true));
            prowlarrImportResp.EnsureSuccessStatusCode();
            var prowlarrReport = (await prowlarrImportResp.Content.ReadFromJsonAsync<MigrationReportSummary>())!;
            Assert.Equal(1, prowlarrReport.DeduplicatedIndexerCount);

            // 4. Import Sonarr via API token (pulls Series + Season/Episode + EpisodeFile in-place, DownloadClient, CustomFormat, and NZB Blocklist)
            var sonarrImportResp = await adminClient.PostAsJsonAsync(
                "/api/v1/migration/arr-api/import",
                new ArrApiTokenImportRequest(
                    BaseUrl: "http://sonarr.lan:8989/",
                    ApiKey: "sonarr-secret-token-111",
                    SourceKind: "auto",
                    PathMappings: pathMappings,
                    DryRun: false,
                    SaveAsSource: true));
            sonarrImportResp.EnsureSuccessStatusCode();
            var sonarrReport = (await sonarrImportResp.Content.ReadFromJsonAsync<MigrationReportSummary>())!;
            Assert.Equal(1, sonarrReport.TvSeriesCount);
            Assert.Equal(1, sonarrReport.TvEpisodeFilesCount);
            Assert.Equal(1, sonarrReport.ConsolidatedDownloadClientCount);
            Assert.Equal(2, sonarrReport.BlocklistHashesImportedCount);
            Assert.Equal(0L, sonarrReport.BytesMovedOrModified);

            // 5. Import Radarr via API token (pulls Movie + embedded MovieFile in-place)
            var radarrImportResp = await adminClient.PostAsJsonAsync(
                "/api/v1/migration/arr-api/import",
                new ArrApiTokenImportRequest(
                    BaseUrl: "http://radarr.lan:7878",
                    ApiKey: "radarr-secret-token-222",
                    SourceKind: "auto",
                    PathMappings: pathMappings,
                    DryRun: false,
                    SaveAsSource: true));
            radarrImportResp.EnsureSuccessStatusCode();
            var radarrReport = (await radarrImportResp.Content.ReadFromJsonAsync<MigrationReportSummary>())!;
            Assert.Equal(1, radarrReport.MoviesCount);
            Assert.Equal(1, radarrReport.MovieFilesCount);
            Assert.Equal(0L, radarrReport.BytesMovedOrModified);

            // 6. Verify imported state in Managerr Catalog, Indexers, Download Clients, Blocklist, and Migration Sources
            using var scope = factory.Services.CreateScope();
            var catalogSvc = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var indexerSvc = scope.ServiceProvider.GetRequiredService<IUnifiedIndexerService>();
            var dlOrchestrator = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var migSvc = scope.ServiceProvider.GetRequiredService<ILegacyMigrationService>();

            var works = await catalogSvc.ListWorksAsync(null, null, null);
            var siloWork = Assert.Single(works, w => w.Title == "Silo");
            var civilWarWork = Assert.Single(works, w => w.Title == "Civil War");
            var siloTree = (await catalogSvc.GetWorkTreeAsync(siloWork.Id))!;
            var civilWarTree = (await catalogSvc.GetWorkTreeAsync(civilWarWork.Id))!;
            Assert.Single(siloTree.Containers.SelectMany(c => c.Units).SelectMany(u => u.Files));
            Assert.Single(civilWarTree.Containers.SelectMany(c => c.Units).SelectMany(u => u.Files));

            var indexers = await indexerSvc.ListIndexersAsync();
            Assert.Contains(indexers, i => i.Name == "NZBGeek" && i.BaseUrl == "http://prowlarr.lan:9696/7/api");

            var clients = await dlOrchestrator.ListClientsAsync();
            Assert.Contains(clients, c => c.Name == "SABnzbd Live" && c.Host == "sabnzbd.lan");

            var blocklist = await dlOrchestrator.ListBlocklistedHashesAsync();
            Assert.Contains(blocklist, b => b.Protocol == "usenet" && b.Title.Contains("Silo.S01E01.BROKEN", StringComparison.OrdinalIgnoreCase));

            var savedSources = await migSvc.ListSourcesAsync();
            Assert.Equal(3, savedSources.Count(s => s.TransportMode == "live_api"));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private sealed class FakeArrLiveApiSafeHttpClientFactory : Managerr.Common.Net.ISafeHttpClientFactory
    {
        public HttpClient CreatePublicClient(bool ignoreSslErrors = false) =>
            new(new FakeArrLiveApiHandler(), disposeHandler: true);

        public HttpClient CreateHomelabClient(bool allowPrivateLan = true, bool ignoreSslErrors = false) =>
            new(new FakeArrLiveApiHandler(), disposeHandler: true);

        public void EnsureAddressPermitted(System.Net.IPAddress address, string? host = null, bool allowPrivateLan = false)
        {
        }
    }

    private sealed class FakeArrLiveApiHandler : HttpMessageHandler
    {
        public static readonly System.Collections.Concurrent.ConcurrentBag<string> ContactedHosts = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var host = uri.Host.ToLowerInvariant();
            var path = uri.AbsolutePath.ToLowerInvariant();
            var query = uri.Query;

            ContactedHosts.Add(host);
            if (host == "sonarr.example.com")
            {
                var traefik401 = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                traefik401.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Basic", "realm=\"traefik\""));
                return Task.FromResult(traefik401);
            }

            var apiKey = request.Headers.TryGetValues("X-Api-Key", out var vals)
                ? vals.FirstOrDefault()
                : null;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            string? json = null;

            if (host == "prowlarr.lan")
            {
                if (apiKey != "prowlarr-secret-token-999")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

                json = path switch
                {
                    "/api/v1/system/status" => """{"appName":"Prowlarr","instanceName":"Homelab Prowlarr","version":"1.24.2.4743"}""",
                    "/api/v1/indexer" => """
                        [
                          {
                            "id": 7,
                            "name": "NZBGeek",
                            "implementation": "Newznab",
                            "protocol": "usenet",
                            "enable": true,
                            "fields": [
                              { "name": "baseUrl", "value": "https://api.nzbgeek.info" },
                              { "name": "apiKey", "value": "********" },
                              { "name": "categories", "value": [5030, 5040, 2000] }
                            ]
                          }
                        ]
                        """,
                    _ => "[]"
                };
            }
            else if (host == "sonarr.lan")
            {
                if (apiKey != "sonarr-secret-token-111")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

                json = path switch
                {
                    "/api/v3/system/status" => """{"appName":"Sonarr","instanceName":"Homelab Sonarr","version":"4.0.10.2544"}""",
                    "/api/v3/rootfolder" => """[{"id":1,"path":"/tv"}]""",
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
                          }
                        ]
                        """,
                    "/api/v3/indexer" => """
                        [
                          {
                            "id": 10,
                            "name": "NZBGeek (Prowlarr)",
                            "implementation": "Newznab",
                            "protocol": "usenet",
                            "fields": [
                              { "name": "baseUrl", "value": "http://prowlarr:9696/7/" },
                              { "name": "categories", "value": [5030, 5040] }
                            ]
                          }
                        ]
                        """,
                    "/api/v3/customformat" => """
                        [
                          {
                            "id": 1,
                            "name": "German DL",
                            "includeCustomFormatWhenRenaming": false,
                            "specifications": [
                              { "name": "German", "implementation": "LanguageSpecification", "negate": false, "required": true, "fields": { "value": 4 } }
                            ]
                          }
                        ]
                        """,
                    "/api/v3/qualityprofile" => """[{"id":1,"name":"WEB-2160p"}]""",
                    "/api/v3/blocklist" => """
                        {
                          "page": 1,
                          "pageSize": 500,
                          "records": [
                            { "id": 1, "sourceTitle": "Silo.S01E01.BROKEN.German.DL.1080p-BADNZB", "protocol": "usenet" },
                            { "id": 2, "sourceTitle": "Silo.S01E01.FAKE.1080p", "protocol": "torrent", "torrentInfoHash": "11223344556677889900aabbccddeeff00112233" }
                          ]
                        }
                        """,
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
                            "rootFolderPath": "/tv"
                          }
                        ]
                        """,
                    "/api/v3/episodefile" when query.Contains("seriesId=42", StringComparison.OrdinalIgnoreCase) => """
                        [
                          {
                            "id": 501,
                            "seriesId": 42,
                            "seasonNumber": 1,
                            "relativePath": "Season 01/Silo.S01E01.German.DL.2160p.WEB-DL.mkv",
                            "path": "/tv/Silo/Season 01/Silo.S01E01.German.DL.2160p.WEB-DL.mkv",
                            "size": 8192,
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
                    _ => "[]"
                };
            }
            else if (host == "radarr.lan")
            {
                if (apiKey != "radarr-secret-token-222")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

                json = path switch
                {
                    "/api/v3/system/status" => """{"appName":"Radarr","instanceName":"Homelab Radarr","version":"5.14.0.9383"}""",
                    "/api/v3/rootfolder" => """[{"id":1,"path":"/movies"}]""",
                    "/api/v3/movie" => """
                        [
                          {
                            "id": 77,
                            "title": "Civil War",
                            "year": 2024,
                            "tmdbId": 929590,
                            "imdbId": "tt17279496",
                            "monitored": true,
                            "path": "/movies/Civil War (2024)",
                            "rootFolderPath": "/movies",
                            "movieFile": {
                              "id": 801,
                              "relativePath": "Civil.War.2024.German.DL.2160p.UHD.BluRay.mkv",
                              "path": "/movies/Civil War (2024)/Civil.War.2024.German.DL.2160p.UHD.BluRay.mkv",
                              "size": 16384,
                              "quality": { "quality": { "name": "Bluray-2160p" } }
                            }
                          }
                        ]
                        """,
                    _ => "[]"
                };
            }

            if (json is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}

