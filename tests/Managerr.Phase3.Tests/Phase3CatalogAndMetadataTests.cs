using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Managerr.Catalog;
using Managerr.Common.Crypto;
using Managerr.Common.Paths;
using Managerr.Datastore;
using Managerr.Datastore.Migrations;
using Managerr.Datastore.Services;
using Managerr.Metadata;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Managerr.Phase3.Tests;

public sealed class Phase3CatalogAndMetadataTests
{
    [Fact]
    public async Task DualSlotBooks_4TierCatalog_FtsCrosswalk_AndCalendar_WorkEndToEnd()
    {
        var tempDir = CreateTempDir();
        try
        {
            var h = await CreateHarnessAsync(tempDir);

            var bookLib = await h.Catalog.CreateLibraryAsync(new CreateLibraryRequest(
                Name: "Books & Audiobooks",
                MediaType: "ebooks",
                RootFolderPath: Path.Combine(tempDir, "books"),
                PlexSectionId: "4"));

            Assert.Contains("openlibrary", bookLib.MetadataWaterfall);
            Assert.Contains("hardcover", bookLib.MetadataWaterfall);

            var created = await h.Catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: bookLib.Id,
                Title: "Project Hail Mary",
                Year: 2021,
                ExternalIds: new Dictionary<string, string> { ["openlibrary"] = "OL20885734W" },
                DualFormatBookSlots: true));

            // Crosswalk seed should automatically enrich ASIN, ISBN13, Hardcover & DNB IDs
            Assert.Equal("B08G9PRS1K", created.Work.ExternalIds["asin"]);
            Assert.Equal("9780593135204", created.Work.ExternalIds["isbn13"]);
            Assert.Equal("1228636895", created.Work.ExternalIds["dnb"]);

            // Single Book container must contain independent ebook_slot and audiobook_slot units
            var bookContainer = Assert.Single(created.Containers);
            Assert.Equal(2, bookContainer.Units.Count);
            var ebookUnit = Assert.Single(bookContainer.Units, u => u.UnitKind == "ebook_slot");
            var audioUnit = Assert.Single(bookContainer.Units, u => u.UnitKind == "audiobook_slot");

            // Toggle monitoring & profile override independently per slot
            var updatedAudio = await h.Catalog.UpdateUnitAsync(
                audioUnit.Id,
                new UpdateMediaUnitRequest(Monitored: false, ProfileOverrideId: 42, RequiredAudioLangs: ["de", "en"]));
            Assert.False(updatedAudio.Monitored);
            Assert.Equal(42, updatedAudio.ProfileOverrideId);
            Assert.Contains("de", updatedAudio.RequiredAudioLangs);

            var refreshedTree = (await h.Catalog.GetWorkTreeAsync(created.Work.Id))!;
            Assert.True(refreshedTree.Containers[0].Units.Single(u => u.UnitKind == "ebook_slot").Monitored);
            Assert.False(refreshedTree.Containers[0].Units.Single(u => u.UnitKind == "audiobook_slot").Monitored);

            // Attach an EPUB edition file to ebook_slot
            var attachedFile = await h.Catalog.AttachFileAsync(new AttachMediaFileRequest(
                UnitId: ebookUnit.Id,
                RelativePath: "Andy Weir/Project Hail Mary (2021)/Project Hail Mary.epub",
                SizeBytes: 2_450_000,
                QualityName: "EPUB-Retail",
                EditionName: "1st Hardcover Retail",
                DetectedSubLangs: ["en"]));
            Assert.Equal("EPUB-Retail", attachedFile.QualityName);

            // FTS5 searches by both title and crosswalked ASIN/ISBN13
            var byAsin = await h.Fts.SearchAsync("B08G9PRS1K");
            Assert.Contains(byAsin, hit => hit.Title == "Project Hail Mary");

            // Calendar returns both units for 2021-01-01
            var cal = await h.Catalog.GetCalendarAsync(new DateOnly(2020, 12, 31), new DateOnly(2021, 1, 2));
            Assert.Equal(2, cal.Count);
            Assert.Contains(cal, c => c.UnitKind == "ebook_slot" && c.HasFile);
            Assert.Contains(cal, c => c.UnitKind == "audiobook_slot" && !c.HasFile);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task MetadataFederation_20Providers_Sidecars_Webhooks_AndLockedFieldMerging_Work()
    {
        var tempDir = CreateTempDir();
        try
        {
            var h = await CreateHarnessAsync(tempDir, req =>
            {
                if (req.Url.Contains("graphql.anilist.co", StringComparison.Ordinal))
                {
                    return """
                        {
                          "data": {
                            "Page": {
                              "media": [
                                {
                                  "id": 154587,
                                  "idMal": 52991,
                                  "title": {
                                    "romaji": "Sousou no Frieren",
                                    "english": "Frieren: Beyond Journey's End",
                                    "native": "葬送のフリーレン"
                                  },
                                  "startDate": { "year": 2023 },
                                  "status": "FINISHED",
                                  "description": "AniList fantasy journey overview.",
                                  "coverImage": { "large": "https://img.anilist.co/frieren.jpg" }
                                }
                              ]
                            }
                          }
                        }
                        """;
                }

                if (req.Url.Contains("custom.lan/meta/lookup", StringComparison.Ordinal))
                {
                    return """
                        {
                          "id": "frieren-de",
                          "title": "Frieren - Nach dem Ende der Reise",
                          "altTitles": ["Frieren DE"],
                          "year": 2023,
                          "originalLanguage": "ja",
                          "overview": "Deutsche Beschreibung aus eigenem Webhook.",
                          "externalIds": { "tvdb": "424536" }
                        }
                        """;
                }

                return null;
            });

            // All 20 built-in default + alternative providers must be registered
            var allProviders = h.Federation.ListBuiltInProviders();
            Assert.Equal(20, allProviders.Count);
            Assert.Contains(allProviders, p => p.Key == "tvdb" && p.IsBuiltInArrDefault);
            Assert.Contains(allProviders, p => p.Key == "tmdb" && p.IsBuiltInArrDefault);
            Assert.Contains(allProviders, p => p.Key == "musicbrainz" && p.IsBuiltInArrDefault);
            Assert.Contains(allProviders, p => p.Key == "openlibrary" && p.IsBuiltInArrDefault);
            Assert.Contains(allProviders, p => p.Key == "hardcover" && !p.IsBuiltInArrDefault);
            Assert.Contains(allProviders, p => p.Key == "audnexus" && !p.IsBuiltInArrDefault);
            Assert.Contains(allProviders, p => p.Key == "anilist" && !p.IsBuiltInArrDefault);
            Assert.Contains(allProviders, p => p.Key == "dnb" && !p.IsBuiltInArrDefault);
            Assert.Contains(allProviders, p => p.Key == "mangadex" && !p.IsBuiltInArrDefault);

            // Search AniList and verify crosswalk automatically resolves tvdb + imdb + anidb + tmdb
            var hits = await h.Federation.SearchWaterfallAsync("Frieren", MetadataVerticals.Anime, ["anilist"]);
            var hit = Assert.Single(hits);
            Assert.Equal("Frieren: Beyond Journey's End", hit.Title);
            Assert.Contains("Sousou no Frieren", hit.AltTitles);
            Assert.Equal("424536", hit.ExternalIds["tvdb"]);
            Assert.Equal("tt22248376", hit.ExternalIds["imdb"]);

            // Register custom HTTP JSON webhook provider and test locked_fields_json protection on refresh
            var webhook = await h.Webhooks.CreateAsync(new UpsertCustomMetadataWebhookRequest(
                ProviderKey: "custom_de_catalog",
                DisplayName: "Self-Hosted DE Catalog",
                SupportedVerticals: ["anime"],
                SearchUrlTemplate: "https://custom.lan/meta/search?q={query}",
                LookupUrlTemplate: "https://custom.lan/meta/lookup/{id}",
                ApiKey: "secret-webhook-key"));

            var animeLib = await h.Catalog.CreateLibraryAsync(new CreateLibraryRequest(
                Name: "Anime",
                MediaType: "anime",
                RootFolderPath: Path.Combine(tempDir, "anime"),
                MetadataWaterfall: [webhook.ProviderKey, "anilist"]));

            var work = await h.Catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: animeLib.Id,
                Title: "My Custom Locked Title",
                WorkKind: "anime",
                LockedFields: ["title"],
                ExternalIds: new Dictionary<string, string>
                {
                    [webhook.ProviderKey] = "frieren-de",
                    ["anilist"] = "154587",
                }));

            var refreshed = await h.Catalog.RefreshWorkMetadataAsync(work.Work.Id, switchProvider: webhook.ProviderKey);
            // Title was locked so it MUST NOT be overwritten, while overview & altTitles ARE enriched!
            Assert.Equal("My Custom Locked Title", refreshed.Work.Title);
            Assert.Equal("Deutsche Beschreibung aus eigenem Webhook.", refreshed.Work.Overview);
            Assert.Contains("Frieren DE", refreshed.Work.AltTitles);

            // Round-trip Kodi .nfo and Calibre metadata.opf sidecars
            var nfoXml = h.Sidecars.WriteKodiJellyfinNfo(hit);
            var parsedNfo = h.Sidecars.ParseKodiJellyfinNfo(nfoXml, MetadataVerticals.Anime);
            Assert.Equal(hit.Title, parsedNfo.Title);
            Assert.Equal("424536", parsedNfo.ExternalIds["tvdb"]);

            var opfXml = h.Sidecars.WriteCalibreOpf(hit with { Vertical = MetadataVerticals.Ebooks });
            var parsedOpf = h.Sidecars.ParseCalibreOpf(opfXml);
            Assert.Equal(hit.Title, parsedOpf.Title);
            Assert.Equal(2, parsedOpf.Containers[0].Units.Count);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task ArrStyleZeroKeySkyHookSearch_IdPrefixLookup_EpisodeHydration_AndMonitorModes_Work()
    {
        var tempDir = CreateTempDir();
        try
        {
            var h = await CreateHarnessAsync(tempDir, req =>
            {
                if (req.Url.Contains("api.radarr.video/v1/search", StringComparison.Ordinal) ||
                    req.Url.Contains("api.radarr.video/v1/movie/imdb/tt15239678", StringComparison.Ordinal))
                {
                    var movieJson = """
                        {
                          "Title": "Dune: Part Two",
                          "Year": 2024,
                          "TmdbId": 693134,
                          "ImdbId": "tt15239678",
                          "Overview": "Paul Atreides unites with Chani and the Fremen.",
                          "OriginalLanguage": { "Name": "English" },
                          "Status": "released",
                          "Images": [
                            { "CoverType": "poster", "RemoteUrl": "https://image.tmdb.org/t/p/original/dune2.jpg" }
                          ]
                        }
                        """;
                    return req.Url.Contains("/search", StringComparison.Ordinal) ? $"[{movieJson}]" : movieJson;
                }

                if (req.Url.Contains("skyhook.sonarr.tv/v2/tvdb/search", StringComparison.Ordinal))
                {
                    return """
                        [
                          {
                            "tvdbId": 371980,
                            "title": "Severance",
                            "firstAired": "2022-02-18",
                            "overview": "Mark leads a team of office workers whose memories have been surgically divided.",
                            "status": "Continuing",
                            "imdbId": "tt11280740",
                            "tmdbId": 95396,
                            "originalLanguage": "eng",
                            "images": [
                              { "coverType": "poster", "url": "https://artworks.thetvdb.com/banners/v4/series/371980/posters/sev.jpg" }
                            ]
                          }
                        ]
                        """;
                }

                if (req.Url.Contains("skyhook.sonarr.tv/v2/tvdb/shows/eng/371980/official", StringComparison.Ordinal))
                {
                    return """
                        {
                          "tvdbId": 371980,
                          "title": "Severance",
                          "firstAired": "2022-02-18",
                          "overview": "Mark leads a team of office workers.",
                          "status": "Continuing",
                          "imdbId": "tt11280740",
                          "tmdbId": 95396,
                          "images": [
                            { "coverType": "poster", "url": "https://artworks.thetvdb.com/banners/v4/series/371980/posters/sev.jpg" }
                          ],
                          "seasons": [
                            { "seasonNumber": 1 },
                            { "seasonNumber": 2 }
                          ],
                          "episodes": [
                            { "seasonNumber": 1, "episodeNumber": 1, "title": "Good News About Hell", "airDate": "2022-02-18" },
                            { "seasonNumber": 1, "episodeNumber": 2, "title": "Half Loop", "airDate": "2022-02-18" },
                            { "seasonNumber": 2, "episodeNumber": 1, "title": "Hello, Ms. Cobel", "airDate": "2025-01-17" }
                          ]
                        }
                        """;
                }

                if (req.Url.Contains("openlibrary.org/search.json", StringComparison.Ordinal))
                {
                    return """
                        {
                          "docs": [
                            {
                              "key": "/works/OL893415W",
                              "title": "Dune",
                              "author_name": ["Frank Herbert"],
                              "first_publish_year": 1965,
                              "cover_i": 8234151,
                              "isbn": ["9780441172719"]
                            }
                          ]
                        }
                        """;
                }

                return null;
            });

            // 1. Zero-key Radarr SkyHook movie search + IMDb prefix direct lookup
            var movieHits = await h.Federation.SearchWaterfallAsync("Dune", MetadataVerticals.Movies);
            var dune2 = Assert.Single(movieHits);
            Assert.Equal("Dune: Part Two", dune2.Title);
            Assert.Equal("693134", dune2.ExternalIds["tmdb"]);
            Assert.Equal("tt15239678", dune2.ExternalIds["imdb"]);
            Assert.Equal("https://image.tmdb.org/t/p/w500/dune2.jpg", dune2.PosterUrl);

            var imdbHits = await h.Federation.SearchWaterfallAsync("imdb:tt15239678", "all");
            var byImdb = Assert.Single(imdbHits);
            Assert.Equal("Dune: Part Two", byImdb.Title);

            // 2. Cross-vertical "all" search interleaves Movies + E-Books with OpenLibrary cover art
            var omniHits = await h.Federation.SearchWaterfallAsync("Dune", "all");
            Assert.Contains(omniHits, c => c.Vertical == MetadataVerticals.Movies && c.Title == "Dune: Part Two");
            Assert.Contains(omniHits, c => c.Vertical == MetadataVerticals.Ebooks && c.PosterUrl == "https://covers.openlibrary.org/b/id/8234151-L.jpg");

            // 3. Zero-key Sonarr SkyHook v2 series search + episode hydration + MonitorMode = "firstSeason"
            var tvLib = await h.Catalog.CreateLibraryAsync(new CreateLibraryRequest(
                Name: "TV Shows",
                MediaType: "tv",
                RootFolderPath: Path.Combine(tempDir, "tv")));

            var tvHits = await h.Federation.SearchWaterfallAsync("Severance", MetadataVerticals.Tv);
            var sevHit = Assert.Single(tvHits);
            Assert.Equal("371980", sevHit.ExternalIds["tvdb"]);

            var addedTv = await h.Catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: sevHit.Title,
                Year: sevHit.Year,
                PosterUrl: sevHit.PosterUrl,
                PrimaryProvider: "tvdb",
                ExternalIds: sevHit.ExternalIds,
                MonitorMode: "firstSeason"));

            Assert.Equal(2, addedTv.Containers.Count);
            var s1 = Assert.Single(addedTv.Containers, c => c.ContainerNumber == 1);
            var s2 = Assert.Single(addedTv.Containers, c => c.ContainerNumber == 2);
            Assert.True(s1.Monitored);
            Assert.False(s2.Monitored);
            Assert.Equal(2, s1.Units.Count);
            Assert.All(s1.Units, u => Assert.True(u.Monitored));
            Assert.All(s2.Units, u => Assert.False(u.Monitored));
            Assert.Contains(s1.Units, u => u.Title == "Good News About Hell");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task PlexUserLibraryScoping_Quotas_RequestLanguagePromotion_AndHttpEndpoints_EnforceRbac()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b =>
                {
                    b.ConfigureAppConfiguration((_, cfg) =>
                    {
                        cfg.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Managerr:ConfigDirectory"] = tempDir,
                            ["Managerr:MasterKey"] = "phase3-integration-test-master-key-32bytes!",
                        });
                    });
                });

            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IPlexUserService>();
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var auth = scope.ServiceProvider.GetRequiredService<IPlexAuthAndSessionService>();

            var owner = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
                PlexUserId: 1001,
                PlexUuid: "owner-uuid",
                Username: "plexadmin",
                Email: "owner@example.com",
                AvatarUrl: null,
                IsServerOwner: true,
                IsHomeUser: true,
                IsActive: true,
                SharedPlexSectionIds: ["1", "99"]));

            var quotaRole = await roles.CreateCustomRoleAsync(new UpsertCustomRoleRequest(
                Name: "Limited Friend",
                Key: "custom:limited-friend",
                Description: "1 movie/month in shared Plex libraries",
                Color: "#22c55e",
                Scopes: [PermissionScopes.CatalogRead, PermissionScopes.CatalogRequest],
                LibraryAccessMode: "inherit_from_plex",
                AutoApproveRequests: false,
                MovieRequestQuota: 1));

            var friend = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
                PlexUserId: 2002,
                PlexUuid: "friend-uuid",
                Username: "germanfriend",
                Email: "friend@example.com",
                AvatarUrl: null,
                IsServerOwner: false,
                IsHomeUser: false,
                IsActive: true,
                SharedPlexSectionIds: ["1"],
                PreferredAudioLang: "de",
                PreferredSubLang: "de"));
            friend = await users.AssignRolesAsync(friend.Id, [quotaRole.Id]);

            var ownerKey = await auth.CreateApiKeyAsync(owner.Id, "Owner Key", PermissionScopes.AllValidScopes.ToList(), null, null, null);
            var friendSession = await auth.CreateSessionForUserAsync(friend, "xunit", "127.0.0.1");

            // Admin creates Shared Movies (Section 1) and Private Vault (Section 99)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerKey.RawTokenOnce);
            var sharedResp = await client.PostAsJsonAsync("/api/v1/libraries", new CreateLibraryRequest("Movies", "movies", Path.Combine(tempDir, "m1"), PlexSectionId: "1"));
            Assert.Equal(HttpStatusCode.Created, sharedResp.StatusCode);
            var sharedLib = (await sharedResp.Content.ReadFromJsonAsync<LibrarySummary>())!;

            var vaultResp = await client.PostAsJsonAsync("/api/v1/libraries", new CreateLibraryRequest("Private 4K Vault", "movies", Path.Combine(tempDir, "m99"), PlexSectionId: "99"));
            Assert.Equal(HttpStatusCode.Created, vaultResp.StatusCode);
            var vaultLib = (await vaultResp.Content.ReadFromJsonAsync<LibrarySummary>())!;

            // Admin adds an English movie to Shared Movies
            var addMovieResp = await client.PostAsJsonAsync("/api/v1/catalog/works", new CreateMediaWorkRequest(
                LibraryId: sharedLib.Id,
                Title: "Dune: Part Two",
                Year: 2024,
                InitialRequiredAudioLangs: ["en"]));
            Assert.Equal(HttpStatusCode.Created, addMovieResp.StatusCode);
            var duneTree = (await addMovieResp.Content.ReadFromJsonAsync<MediaWorkDetailTree>())!;

            // Switch to Plex Friend: sees ONLY Section 1, blocked (403) from requesting in Section 99
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", friendSession.RawSessionToken);
            var friendLibs = await client.GetFromJsonAsync<List<LibrarySummary>>("/api/v1/libraries");
            Assert.Single(friendLibs!);
            Assert.Equal(sharedLib.Id, friendLibs![0].Id);

            var forbiddenReq = await client.PostAsJsonAsync("/api/v1/requests", new SubmitMediaRequestCommand(vaultLib.Id, "Secret Vault Film"));
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenReq.StatusCode);

            // Friend requests Dune: Part Two -> captures preferredAudioLang = "de" as pending
            var okReq = await client.PostAsJsonAsync("/api/v1/requests", new SubmitMediaRequestCommand(sharedLib.Id, "Dune: Part Two"));
            Assert.Equal(HttpStatusCode.Created, okReq.StatusCode);
            var createdReq = (await okReq.Content.ReadFromJsonAsync<MediaRequestSummary>())!;
            Assert.Equal("pending", createdReq.Status);
            Assert.Equal("de", createdReq.RequestedAudioLang);

            // Second movie request hits MovieRequestQuota = 1 -> 409 Conflict
            var quotaBlocked = await client.PostAsJsonAsync("/api/v1/requests", new SubmitMediaRequestCommand(sharedLib.Id, "Another Movie"));
            Assert.Equal(HttpStatusCode.Conflict, quotaBlocked.StatusCode);

            // Admin approves friend's request -> promotes "de" onto Dune: Part Two's MediaUnit RequiredAudioLangs!
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerKey.RawTokenOnce);
            var approveResp = await client.PostAsJsonAsync($"/api/v1/requests/{createdReq.Id}/decide", new { approve = true });
            Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);

            var updatedDune = await client.GetFromJsonAsync<MediaWorkDetailTree>($"/api/v1/catalog/works/{duneTree.Work.Id}");
            var duneUnit = Assert.Single(updatedDune!.Containers[0].Units);
            Assert.Contains("en", duneUnit.RequiredAudioLangs);
            Assert.Contains("de", duneUnit.RequiredAudioLangs);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task SeasonAndEpisodeSearch_ContainerMonitorCascade_AndReleaseParser_WorkEndToEnd()
    {
        var tempDir = CreateTempDir();
        try
        {
            var parser = new Managerr.Downloaders.Parser.ReleaseParserRouter();
            var altEp = parser.Parse("Severance.1x02.Half.Loop.1080p.WEB-DL-GRP", "tv");
            Assert.Equal(1, altEp.SeasonNumber);
            Assert.Equal([2], altEp.EpisodeNumbers);
            Assert.False(altEp.IsFullSeasonPack);

            var gerPack = parser.Parse("Dark.Staffel.02.German.DL.1080p.BluRay.x265-GRP", "tv");
            Assert.Equal(2, gerPack.SeasonNumber);
            Assert.True(gerPack.IsFullSeasonPack);

            var uncategorizedTvWithYear = parser.Parse("Pioneer.One.2010.S01E02.The.Man.From.Mars.German.DL.1080p.WEB-DL.H264-TESTLAB", "movies");
            Assert.Equal("tv", uncategorizedTvWithYear.MediaVertical);
            Assert.Equal("Pioneer One", uncategorizedTvWithYear.CleanTitle);
            Assert.Equal(2010, uncategorizedTvWithYear.Year);
            Assert.Equal(1, uncategorizedTvWithYear.SeasonNumber);
            Assert.Equal([2], uncategorizedTvWithYear.EpisodeNumbers);

            var grabbedRequests = new List<Managerr.Downloaders.GrabReleaseRequest>();
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b =>
                {
                    b.ConfigureAppConfiguration((_, cfg) =>
                    {
                        cfg.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Managerr:ConfigDirectory"] = tempDir,
                            ["Managerr:MasterKey"] = "phase3-season-episode-master-key-32b!",
                        });
                    });
                    b.ConfigureServices(services =>
                    {
                        services.AddSingleton<Managerr.Indexers.IIndexerHttpGateway>(new StubNewznabGateway());
                        services.AddSingleton<Managerr.Downloaders.IUnifiedDownloadOrchestrator>(
                            new CapturingDownloadOrchestrator(grabbedRequests));
                    });
                });

            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IPlexUserService>();
            var auth = scope.ServiceProvider.GetRequiredService<IPlexAuthAndSessionService>();

            var owner = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
                PlexUserId: 9001,
                PlexUuid: "owner-tv-uuid",
                Username: "tvadmin",
                Email: "tv@example.com",
                AvatarUrl: null,
                IsServerOwner: true,
                IsHomeUser: true,
                IsActive: true,
                SharedPlexSectionIds: ["2"]));
            var ownerKey = await auth.CreateApiKeyAsync(owner.Id, "TV Owner Key", PermissionScopes.AllValidScopes.ToList(), null, null, null);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerKey.RawTokenOnce);

            // Configure TV library + Newznab indexer
            var libResp = await client.PostAsJsonAsync("/api/v1/libraries", new CreateLibraryRequest("TV Shows", "tv", Path.Combine(tempDir, "tv"), PlexSectionId: "2"));
            var tvLib = (await libResp.Content.ReadFromJsonAsync<LibrarySummary>())!;

            var ixResp = await client.PostAsJsonAsync("/api/v1/indexers", new Managerr.Indexers.UpsertIndexerRequest(
                Name: "Stub TV Newznab",
                Implementation: "newznab",
                DefinitionId: null,
                Protocol: "usenet",
                BaseUrl: "http://stub-newznab.local/api",
                ApiKey: "stub-key",
                Categories: [5000, 5040],
                RequestDelayMs: 0));
            Assert.Equal(HttpStatusCode.Created, ixResp.StatusCode);

            // Add 2-season TV series
            var addSeriesResp = await client.PostAsJsonAsync("/api/v1/catalog/works", new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: "Pioneer One",
                WorkKind: "tv",
                Year: 2010,
                ExternalIds: new Dictionary<string, string> { ["tvdb"] = "170551" },
                Containers:
                [
                    new MetadataContainerSeed("season", 1, "Season 1", "2010-06-16",
                    [
                        new MetadataUnitSeed("episode", 1, 1, "Earthbound", "2010-06-16"),
                        new MetadataUnitSeed("episode", 2, 2, "The Man From Mars", "2010-12-15"),
                    ]),
                    new MetadataContainerSeed("season", 2, "Season 2", "2011-09-10",
                    [
                        new MetadataUnitSeed("episode", 1, 3, "Triangulation", "2011-09-10"),
                    ]),
                ]));
            Assert.Equal(HttpStatusCode.Created, addSeriesResp.StatusCode);
            var seriesTree = (await addSeriesResp.Content.ReadFromJsonAsync<MediaWorkDetailTree>())!;
            var s1 = seriesTree.Containers.Single(c => c.ContainerNumber == 1);
            var s2 = seriesTree.Containers.Single(c => c.ContainerNumber == 2);
            var s1e2 = s1.Units.Single(u => u.UnitNumber == 2);

            // 1. Toggle Season 2 monitor off -> cascades to all Season 2 episodes; toggle back on -> restores
            var unmonResp = await client.PatchAsJsonAsync($"/api/v1/catalog/containers/{s2.Id}", new UpdateMediaContainerRequest(Monitored: false));
            Assert.Equal(HttpStatusCode.OK, unmonResp.StatusCode);
            var afterUnmon = (await unmonResp.Content.ReadFromJsonAsync<MediaWorkDetailTree>())!;
            Assert.False(afterUnmon.Containers.Single(c => c.Id == s2.Id).Monitored);
            Assert.All(afterUnmon.Containers.Single(c => c.Id == s2.Id).Units, u => Assert.False(u.Monitored));

            var remonResp = await client.PatchAsJsonAsync($"/api/v1/catalog/containers/{s2.Id}", new UpdateMediaContainerRequest(Monitored: true));
            Assert.Equal(HttpStatusCode.OK, remonResp.StatusCode);
            var afterRemon = (await remonResp.Content.ReadFromJsonAsync<MediaWorkDetailTree>())!;
            Assert.True(afterRemon.Containers.Single(c => c.Id == s2.Id).Monitored);
            Assert.All(afterRemon.Containers.Single(c => c.Id == s2.Id).Units, u => Assert.True(u.Monitored));

            // 2. Scoped Interactive Episode Search (S01E02): ranks exact S01E02 first, keeps S01 pack second, filters out S01E01 & S02E01!
            var interactiveEpResp = await client.PostAsJsonAsync("/api/v1/indexers/search", new Managerr.Indexers.IndexerSearchQuery(
                Query: "Pioneer One",
                MediaVertical: "tv",
                TvdbId: 170551,
                Season: 1,
                Episode: 2,
                Interactive: true));
            Assert.Equal(HttpStatusCode.OK, interactiveEpResp.StatusCode);
            var epRows = await interactiveEpResp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal(2, epRows.GetArrayLength());
            Assert.Contains("S01E02", epRows[0].GetProperty("release").GetProperty("title").GetString());
            Assert.Contains("S01.German", epRows[1].GetProperty("release").GetProperty("title").GetString());

            // 3. Automatic Episode Search on S01E02 -> prefers single-episode S01E02 release over S01 pack!
            var epAutoResp = await client.PostAsync($"/api/v1/catalog/works/{seriesTree.Work.Id}/units/{s1e2.Id}/auto-search", null);
            Assert.Equal(HttpStatusCode.OK, epAutoResp.StatusCode);
            Assert.Single(grabbedRequests);
            Assert.Contains("S01E02", grabbedRequests[0].Title);

            // 4. Automatic Season Search on Season 1 -> grabs full Season 01 pack!
            grabbedRequests.Clear();
            var s1AutoResp = await client.PostAsync($"/api/v1/catalog/works/{seriesTree.Work.Id}/containers/{s1.Id}/auto-search", null);
            Assert.Equal(HttpStatusCode.OK, s1AutoResp.StatusCode);
            Assert.Single(grabbedRequests);
            Assert.Equal("Pioneer.One.2010.S01.German.DL.1080p.WEB-DL.H264-TESTLAB", grabbedRequests[0].Title);

            // 5. Automatic Season Search on Season 2 (which has NO season pack, only S02E01) -> falls back to grabbing missing monitored episode S02E01!
            grabbedRequests.Clear();
            var s2AutoResp = await client.PostAsync($"/api/v1/catalog/works/{seriesTree.Work.Id}/containers/{s2.Id}/auto-search", null);
            Assert.Equal(HttpStatusCode.OK, s2AutoResp.StatusCode);
            Assert.Single(grabbedRequests);
            Assert.Contains("S02E01", grabbedRequests[0].Title);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task EnglishLatinNormalization_RelevanceRanking_AndSqliteAntiSpamCache_WorkEndToEnd()
    {
        var tempDir = CreateTempDir();
        try
        {
            var upstreamCallCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var h = await CreateHarnessAsync(tempDir, req =>
            {
                var host = new Uri(req.Url).Host;
                upstreamCallCounts[host] = upstreamCallCounts.GetValueOrDefault(host) + 1;

                if (req.Url.Contains("skyhook.sonarr.tv/v2/tvdb/search", StringComparison.Ordinal))
                {
                    // Simulate Sonarr SkyHook returning fuzzy slug "Mukodono" (index 0) ahead of "Mikadono" (index 1)
                    return """
                        [
                          {
                            "tvdbId": 81101,
                            "title": "My Husband",
                            "alternateTitles": [{ "title": "Mukodono" }, { "title": "ムコ殿" }],
                            "firstAired": "2001-04-12",
                            "status": "Ended",
                            "originalLanguage": "jpn"
                          },
                          {
                            "tvdbId": 452100,
                            "title": "Dealing with Mikadono Sisters Is a Breeze",
                            "firstAired": "2025-07-10",
                            "status": "Continuing",
                            "originalLanguage": "jpn",
                            "images": [{ "coverType": "poster", "url": "https://artworks.thetvdb.com/banners/v4/series/452100/posters/mikadono.jpg" }]
                          }
                        ]
                        """;
                }

                if (req.Url.Contains("graphql.anilist.co", StringComparison.Ordinal))
                {
                    if (req.JsonBody?.Contains("Media(id:", StringComparison.Ordinal) == true)
                    {
                        return """
                            {
                              "data": {
                                "Media": {
                                  "id": 178025,
                                  "idMal": 59210,
                                  "episodes": 12,
                                  "title": {
                                    "romaji": "Mikadono Sanshimai wa Angai, Choroi.",
                                    "english": "Dealing with Mikadono Sisters Is a Breeze",
                                    "native": "帝乃三姉妹は案外、チョロい。"
                                  },
                                  "synonyms": ["Mikadono Sisters"],
                                  "startDate": { "year": 2025 },
                                  "status": "RELEASING",
                                  "description": "Yu Ayase moves in with the three prodigy Mikadono sisters.",
                                  "coverImage": { "extraLarge": "https://s4.anilist.co/file/anilistcdn/media/anime/cover/mikadono.jpg" }
                                }
                              }
                            }
                            """;
                    }

                    return """
                        {
                          "data": {
                            "Page": {
                              "media": [
                                {
                                  "id": 178025,
                                  "idMal": 59210,
                                  "episodes": 12,
                                  "title": {
                                    "romaji": "Mikadono Sanshimai wa Angai, Choroi.",
                                    "english": "Dealing with Mikadono Sisters Is a Breeze",
                                    "native": "帝乃三姉妹は案外、チョロい。"
                                  },
                                  "synonyms": ["Mikadono Sisters", "帝乃三姉妹"],
                                  "startDate": { "year": 2025 },
                                  "status": "RELEASING",
                                  "description": "Yu Ayase moves in with the three prodigy Mikadono sisters.",
                                  "coverImage": { "large": "https://s4.anilist.co/file/anilistcdn/media/anime/cover/mikadono.jpg" }
                                }
                              ]
                            }
                          }
                        }
                        """;
                }

                if (req.Url.Contains("musicbrainz.org/ws/2/release-group", StringComparison.Ordinal))
                {
                    return """
                        {
                          "release-groups": [
                            {
                              "id": "e8d29d3f-22a1-4a7d-9b1c-mikadono0001",
                              "title": "帝乃三姉妹と優の、キャラソン。",
                              "first-release-date": "2025-10-01",
                              "artist-credit": [
                                {
                                  "name": "帝乃一輝",
                                  "artist": {
                                    "id": "artist-kazuki",
                                    "name": "帝乃一輝",
                                    "sort-name": "Mikadono, Kazuki"
                                  }
                                }
                              ],
                              "releases": [
                                {
                                  "id": "rel-jp",
                                  "title": "帝乃三姉妹と優の、キャラソン。",
                                  "status": "Official",
                                  "text-representation": { "language": "jpn", "script": "Jpan" }
                                },
                                {
                                  "id": "rel-en-pseudo",
                                  "title": "Character Songs of Mikadono Sisters and Yu",
                                  "status": "Pseudo-Release",
                                  "text-representation": { "language": "eng", "script": "Latn" }
                                }
                              ]
                            }
                          ]
                        }
                        """;
                }

                // Return empty 0-hit JSON for movies (Radarr SkyHook) and ebooks (OpenLibrary) to verify 30m negative search caching
                if (req.Url.Contains("api.radarr.video/v1/search", StringComparison.Ordinal))
                {
                    return "[]";
                }

                if (req.Url.Contains("openlibrary.org/search.json", StringComparison.Ordinal))
                {
                    return """{"docs":[]}""";
                }

                return "[]";
            });

            // 1. Unified "all" search includes Anime + TV + Music, merges TVDB + AniList, ranks exact Mikadono #1, and converts MusicBrainz Kanji to English
            var results = await h.Federation.SearchWaterfallAsync("mikadono", "all");
            Assert.NotEmpty(results);
            Assert.Equal("Dealing with Mikadono Sisters Is a Breeze", results[0].Title);
            Assert.Contains(results[0].AltTitles, a => a.StartsWith("Mikadono Sanshimai", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("452100", results[0].ExternalIds["tvdb"]);
            Assert.Equal("178025", results[0].ExternalIds["anilist"]);

            var mbHit = Assert.Single(results, r => r.ProviderKey == "musicbrainz");
            Assert.Equal("Kazuki Mikadono — Character Songs of Mikadono Sisters and Yu", mbHit.Title);
            Assert.Equal("ja", mbHit.OriginalLanguage);

            // Every returned title and altTitle must be clean Latin characters (zero CJK/Kana/Hangul/Cyrillic leakage)
            Assert.All(results, r =>
            {
                Assert.False(LatinTitleNormalizer.ContainsNonLatinScript(r.Title), $"Non-Latin primary title leaked: {r.Title}");
                Assert.All(r.AltTitles, alt => Assert.False(LatinTitleNormalizer.ContainsNonLatinScript(alt), $"Non-Latin altTitle leaked: {alt}"));
            });

            // 2. Repeating the exact same search must hit HybridSqliteCache (0 additional upstream calls, even for 0-hit movie/book providers!)
            var snapshotAfterFirstSearch = new Dictionary<string, int>(upstreamCallCounts);
            var cachedResults = await h.Federation.SearchWaterfallAsync("mikadono", "all");
            Assert.Equal(results.Count, cachedResults.Count);
            Assert.Equal(snapshotAfterFirstSearch, upstreamCallCounts);

            // 3. AniList GraphQL Media(id) lookup caches for 7d by default and bypasses cache when forceRefresh = true
            var resolved1 = await h.Federation.ResolveWorkAsync(
                MetadataVerticals.Anime,
                preferredProvider: "anilist",
                knownExternalIds: new Dictionary<string, string> { ["anilist"] = "178025" },
                customWaterfall: ["anilist"]);
            Assert.NotNull(resolved1);
            Assert.Equal("Dealing with Mikadono Sisters Is a Breeze", resolved1!.Title);
            Assert.Equal(12, resolved1.Containers[0].Units.Count);

            var anilistCallsAfterLookup1 = upstreamCallCounts["graphql.anilist.co"];
            var resolvedCached = await h.Federation.ResolveWorkAsync(
                MetadataVerticals.Anime,
                preferredProvider: "anilist",
                knownExternalIds: new Dictionary<string, string> { ["anilist"] = "178025" },
                customWaterfall: ["anilist"],
                forceRefresh: false);
            Assert.NotNull(resolvedCached);
            Assert.Equal(anilistCallsAfterLookup1, upstreamCallCounts["graphql.anilist.co"]);

            var resolvedForced = await h.Federation.ResolveWorkAsync(
                MetadataVerticals.Anime,
                preferredProvider: "anilist",
                knownExternalIds: new Dictionary<string, string> { ["anilist"] = "178025" },
                customWaterfall: ["anilist"],
                forceRefresh: true);
            Assert.NotNull(resolvedForced);
            Assert.Equal(anilistCallsAfterLookup1 + 1, upstreamCallCounts["graphql.anilist.co"]);

            // 4. Deterministic romanization fallback for Kana, Hangul, and Cyrillic when no English translation exists
            Assert.Equal("furiren", LatinTitleNormalizer.TransliterateToLatin("フリーレン").ToLowerInvariant());
            Assert.Equal("na honjaman rebel-eop", LatinTitleNormalizer.TransliterateToLatin("나 혼자만 레벨-업").ToLowerInvariant());
            Assert.Equal("brat", LatinTitleNormalizer.TransliterateToLatin("Брат").ToLowerInvariant());
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task MediaTaxonomyClassifier_And_AutoClassifyCatalogWorks_TagAnimeCartoonLiveActionMovieAndTv()
    {
        var tempDir = CreateTempDir();
        try
        {
            // 1. Direct classifier matrix verification across all 6 canonical taxonomy buckets
            var animeSeries = MediaTaxonomyClassifier.Classify(
                vertical: "anime",
                originalLanguage: "ja",
                genres: ["Animation", "Action", "Fantasy"],
                externalIds: new Dictionary<string, string> { ["anilist"] = "154587" },
                title: "Frieren: Beyond Journey's End");
            Assert.Equal("anime-series", animeSeries.ClassificationKind);
            Assert.Contains("anime", animeSeries.SuggestedTags);
            Assert.Contains("anime-series", animeSeries.SuggestedTags);

            var animeMovieFromMoviesVertical = MediaTaxonomyClassifier.Classify(
                vertical: "movies",
                originalLanguage: "ja",
                genres: ["Animation", "Drama"],
                externalIds: new Dictionary<string, string> { ["tmdb"] = "372058" },
                title: "Your Name.");
            Assert.Equal("anime-movie", animeMovieFromMoviesVertical.ClassificationKind);
            Assert.Contains("anime", animeMovieFromMoviesVertical.SuggestedTags);
            Assert.Contains("anime-movie", animeMovieFromMoviesVertical.SuggestedTags);
            Assert.Contains("movie", animeMovieFromMoviesVertical.SuggestedTags);

            var animeMovieFromAniListFormat = MediaTaxonomyClassifier.Classify(
                vertical: "anime",
                originalLanguage: "ja",
                genres: ["Action"],
                externalIds: new Dictionary<string, string> { ["anilist"] = "131573" },
                title: "Jujutsu Kaisen 0",
                formatHint: "MOVIE");
            Assert.Equal("anime-movie", animeMovieFromAniListFormat.ClassificationKind);
            Assert.Contains("anime-movie", animeMovieFromAniListFormat.SuggestedTags);

            var cartoonSeries = MediaTaxonomyClassifier.Classify(
                vertical: "tv",
                originalLanguage: "en",
                genres: ["Animation", "Adventure"],
                externalIds: new Dictionary<string, string> { ["tvdb"] = "445566" },
                title: "Avatar: Seven Havens");
            Assert.Equal("cartoon-series", cartoonSeries.ClassificationKind);
            Assert.Contains("cartoon", cartoonSeries.SuggestedTags);
            Assert.Contains("cartoon-series", cartoonSeries.SuggestedTags);
            Assert.DoesNotContain("anime", cartoonSeries.SuggestedTags);

            var cartoonMovie = MediaTaxonomyClassifier.Classify(
                vertical: "movies",
                originalLanguage: "en",
                genres: ["Animation", "Family"],
                externalIds: new Dictionary<string, string> { ["tmdb"] = "1022789" },
                title: "Inside Out 2");
            Assert.Equal("cartoon-movie", cartoonMovie.ClassificationKind);
            Assert.Contains("cartoon", cartoonMovie.SuggestedTags);
            Assert.Contains("cartoon-movie", cartoonMovie.SuggestedTags);

            var liveActionTv = MediaTaxonomyClassifier.Classify(
                vertical: "tv",
                originalLanguage: "en",
                genres: ["Sci-Fi", "Drama"],
                externalIds: new Dictionary<string, string> { ["tvdb"] = "371980" },
                title: "Severance");
            Assert.Equal("live-action-series", liveActionTv.ClassificationKind);
            Assert.Contains("live-action", liveActionTv.SuggestedTags);
            Assert.Contains("live-action-series", liveActionTv.SuggestedTags);
            Assert.Contains("tv", liveActionTv.SuggestedTags);

            var liveActionMovie = MediaTaxonomyClassifier.Classify(
                vertical: "movies",
                originalLanguage: "en",
                genres: ["Sci-Fi", "Action"],
                externalIds: new Dictionary<string, string> { ["tmdb"] = "27205" },
                title: "Inception");
            Assert.Equal("live-action-movie", liveActionMovie.ClassificationKind);
            Assert.Contains("live-action", liveActionMovie.SuggestedTags);
            Assert.Contains("live-action-movie", liveActionMovie.SuggestedTags);
            Assert.Contains("movie", liveActionMovie.SuggestedTags);

            // 2. Catalog integration: AddWorkAsync + AutoClassifyCatalogWorksAsync preserves custom tags and adds taxonomy tags
            var h = await CreateHarnessAsync(tempDir);
            var tvLib = await h.Catalog.CreateLibraryAsync(new CreateLibraryRequest("Series", "tv", "/media/series"));
            var movieLib = await h.Catalog.CreateLibraryAsync(new CreateLibraryRequest("Anime Movies", "movies", "/media/animefilme"));

            var avatarWork = await h.Catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: "Avatar: Seven Havens",
                WorkKind: "tv",
                OriginalLanguage: "en",
                Overview: "An animated sequel series set after Korra.",
                Tags: ["user-favorite"]));
            Assert.Contains("user-favorite", avatarWork.Work.Tags);
            Assert.Contains("cartoon", avatarWork.Work.Tags);
            Assert.Contains("cartoon-series", avatarWork.Work.Tags);

            var akiraWork = await h.Catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: movieLib.Id,
                Title: "Akira",
                WorkKind: "movies",
                OriginalLanguage: "ja",
                Year: 1988));
            Assert.Contains("anime", akiraWork.Work.Tags);
            Assert.Contains("anime-movie", akiraWork.Work.Tags);

            var report = await h.Catalog.AutoClassifyCatalogWorksAsync();
            Assert.Equal(2, report.EvaluatedCount);
            Assert.True(report.CountsByClassification.ContainsKey("cartoon-series"));
            Assert.True(report.CountsByClassification.ContainsKey("anime-movie"));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task SearchDefaultPopularityOrder_And_SpecialsSeason00AllocatedTogetherWithSeries()
    {
        var tempDir = CreateTempDir();
        try
        {
            var h = await CreateHarnessAsync(tempDir, req =>
            {
                if (req.Url.Contains("skyhook.sonarr.tv", StringComparison.Ordinal))
                {
                    // Return two matching shows where the second has higher popularity (Ratings.Votes) and includes Season 0 (Specials)
                    return """
                        [
                          {
                            "tvdbId": 90001,
                            "title": "Galactic Frontier",
                            "year": 2018,
                            "status": "Ended",
                            "ratings": { "votes": 140, "value": 6.4 },
                            "seasons": [{ "seasonNumber": 1 }]
                          },
                          {
                            "tvdbId": 90002,
                            "title": "Galactic Frontier: Prime",
                            "year": 2022,
                            "status": "Continuing",
                            "ratings": { "votes": 48500, "value": 9.1 },
                            "seasons": [
                              { "seasonNumber": 0 },
                              { "seasonNumber": 1 }
                            ],
                            "episodes": [
                              { "seasonNumber": 0, "episodeNumber": 1, "title": "The Zero Hour Special" },
                              { "seasonNumber": 1, "episodeNumber": 1, "title": "Pilot" },
                              { "seasonNumber": 1, "episodeNumber": 2, "title": "Second Jump" }
                            ]
                          }
                        ]
                        """;
                }

                return "[]";
            });

            var hits = await h.Federation.SearchWaterfallAsync("Galactic Frontier", MetadataVerticals.Tv, customWaterfall: ["tvdb"]);
            Assert.Equal(2, hits.Count);

            // Default search order ranks higher-popularity candidate first
            Assert.Equal("Galactic Frontier: Prime", hits[0].Title);
            Assert.Equal(48500, hits[0].Popularity);
            Assert.Equal("Galactic Frontier", hits[1].Title);
            Assert.Equal(140, hits[1].Popularity);

            // Specials (Season 0) are allocated together on the series candidate alongside Season 1
            Assert.Equal(2, hits[0].Containers.Count);
            var specials = Assert.Single(hits[0].Containers, c => c.ContainerNumber == 0);
            Assert.Equal("Specials", specials.Title);
            Assert.Single(specials.Units);
            Assert.Equal("The Zero Hour Special", specials.Units[0].Title);

            var season1 = Assert.Single(hits[0].Containers, c => c.ContainerNumber == 1);
            Assert.Equal("Season 1", season1.Title);
            Assert.Equal(2, season1.Units.Count);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private sealed record Harness(
        IPolymorphicCatalogService Catalog,
        IMetadataFederationService Federation,
        ICustomHttpMetadataProviderService Webhooks,
        ISidecarMetadataService Sidecars,
        IFtsSearchService Fts);

    private static async Task<Harness> CreateHarnessAsync(
        string tempDir,
        Func<MetadataHttpRequest, string?>? mockHttp = null)
    {
        var paths = new AppPaths(tempDir);
        var protector = new EnvelopeSecretProtector(paths, "unit-test-master-key-32bytes-ok!");
        var dbFactory = new SqliteDbFactory(paths);
        var migrator = new SchemaMigrator(dbFactory);
        await migrator.MigrateAllAsync();

        var cache = new HybridSqliteCache(dbFactory, new MemoryCache(new MemoryCacheOptions()));
        var settings = new AppSettingsService(dbFactory, protector);
        var fts = new FtsSearchService(dbFactory);
        var crosswalk = new ExternalIdCrosswalkService(dbFactory);
        await crosswalk.EnsureSeededAsync();

        var sidecars = new SidecarMetadataService();
        var gateway = new StubMetadataHttpGateway(mockHttp ?? (_ => null));
        var webhooks = new CustomHttpMetadataProviderService(dbFactory, protector, gateway);
        var builtIns = BuiltInMetadataProviderCatalog.CreateAll(gateway, settings, sidecars);
        var federation = new MetadataFederationService(builtIns, webhooks, crosswalk, cache);
        var catalog = new PolymorphicCatalogService(dbFactory, fts, federation, crosswalk);

        return new Harness(catalog, federation, webhooks, sidecars, fts);
    }

    private sealed class StubMetadataHttpGateway(Func<MetadataHttpRequest, string?> responder) : IMetadataHttpGateway
    {
        public Task<string?> SendAsync(MetadataHttpRequest request, CancellationToken ct = default) =>
            Task.FromResult(responder(request));
    }

    private sealed class StubNewznabGateway : Managerr.Indexers.IIndexerHttpGateway
    {
        public Task<string> FetchStringAsync(Uri targetUri, Managerr.Indexers.IndexerProxyInternal? matchedProxy, CancellationToken ct = default)
        {
            var qs = System.Web.HttpUtility.ParseQueryString(targetUri.Query);
            var season = qs["season"];
            var ep = qs["ep"];
            var q = (qs["q"] ?? "").ToLowerInvariant();

            var items = new List<(string Id, string Title, long Size)>
            {
                ("rel-s01-pack", "Pioneer.One.2010.S01.German.DL.1080p.WEB-DL.H264-TESTLAB", 4_200_000_000L),
                ("rel-s01e01", "Pioneer.One.2010.S01E01.Earthbound.1080p.WEB-DL.H264-TESTLAB", 1_400_000_000L),
                ("rel-s01e02", "Pioneer.One.2010.S01E02.The.Man.From.Mars.German.DL.1080p.WEB-DL.H264-TESTLAB", 1_500_000_000L),
                ("rel-s02e01", "Pioneer.One.2010.S02E01.Triangulation.1080p.WEB-DL.ENG.H264-TESTLAB", 1_350_000_000L),
            };

            var filtered = items.Where(it =>
            {
                var tl = it.Title.ToLowerInvariant();
                if (!string.IsNullOrEmpty(season))
                {
                    var s = int.Parse(season);
                    if (!string.IsNullOrEmpty(ep))
                    {
                        var e = int.Parse(ep);
                        return tl.Contains($"s{s:d2}e{e:d2}") || tl.Contains($".s{s:d2}.");
                    }

                    return tl.Contains($".s{s:d2}.") || tl.Contains($".s{s:d2}e");
                }

                var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return tokens.All(tok => tl.Contains(tok));
            }).ToList();

            var xmlItems = string.Join("\n", filtered.Select(it =>
                $"""
                <item>
                  <title>{it.Title}</title>
                  <guid>{it.Id}</guid>
                  <link>http://stub-newznab.local/get/{it.Id}.nzb</link>
                  <pubDate>Sun, 01 Nov 2026 12:00:00 +0000</pubDate>
                  <enclosure url="http://stub-newznab.local/get/{it.Id}.nzb" length="{it.Size}" type="application/x-nzb" />
                  <newznab:attr name="category" value="5040" />
                  <newznab:attr name="size" value="{it.Size}" />
                </item>
                """));

            var xml =
                $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
                  <channel>
                    <title>Stub TV Newznab</title>
                    {xmlItems}
                  </channel>
                </rss>
                """;
            return Task.FromResult(xml);
        }
    }

    private sealed class CapturingDownloadOrchestrator(List<Managerr.Downloaders.GrabReleaseRequest> captured)
        : Managerr.Downloaders.IUnifiedDownloadOrchestrator
    {
        public event Action? DownloadMonitorTriggerRequested;
        public void RequestImmediateDownloadCheck() => DownloadMonitorTriggerRequested?.Invoke();
        public IReadOnlyList<Managerr.Downloaders.Clients.DownloadClientDescriptor> ListSupportedDrivers() => [];
        public Task<IReadOnlyList<Managerr.Downloaders.DownloadClientSummary>> ListClientsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Managerr.Downloaders.DownloadClientSummary>>([]);
        public Task<Managerr.Downloaders.DownloadClientSummary> CreateClientAsync(Managerr.Downloaders.UpsertDownloadClientRequest req, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Managerr.Downloaders.DownloadClientSummary> UpdateClientAsync(long id, Managerr.Downloaders.UpsertDownloadClientRequest req, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteClientAsync(long id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Managerr.Downloaders.RemotePathMappingDto>> ListRemotePathMappingsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Managerr.Downloaders.RemotePathMappingDto>>([]);
        public Task<Managerr.Downloaders.RemotePathMappingDto> CreateRemotePathMappingAsync(Managerr.Downloaders.CreateRemotePathMappingRequest req, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteRemotePathMappingAsync(long id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> TranslateRemotePathAsync(string host, string remoteOutputPath, CancellationToken ct = default, long? downloadClientId = null) => Task.FromResult(remoteOutputPath);
        public Managerr.Downloaders.FileSafetyInspectionResult InspectFilesBeforeImport(IEnumerable<string> filePaths, string mediaVertical) => new(true, [], null);
        public Task<bool> IsHashBlocklistedAsync(string? infoHash, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<Managerr.Downloaders.RecurringBlockedHashEntry>> ListBlocklistedHashesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Managerr.Downloaders.RecurringBlockedHashEntry>>([]);
        public Task<Managerr.Downloaders.GrabReleaseOutcome> GrabReleaseAsync(Managerr.Downloaders.GrabReleaseRequest req, CancellationToken ct = default)
        {
            captured.Add(req);
            return Task.FromResult(new Managerr.Downloaders.GrabReleaseOutcome(1, "SABnzbd Stub", $"sab-{captured.Count}", req.MediaVertical));
        }

        public Task<Managerr.Downloaders.DownloadGrabHistoryEntry?> FindGrabHistoryAsync(long downloadClientId, string externalDownloadId, string? infoHash = null, CancellationToken ct = default) => Task.FromResult<Managerr.Downloaders.DownloadGrabHistoryEntry?>(null);
        public Task<IReadOnlyList<Managerr.Downloaders.DownloadGrabHistoryEntry>> ListGrabHistoryAsync(int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Managerr.Downloaders.DownloadGrabHistoryEntry>>([]);
        public Task<IReadOnlyList<Managerr.Downloaders.Clients.ActiveDownloadQueueItem>> GetUnifiedQueueAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Managerr.Downloaders.Clients.ActiveDownloadQueueItem>>([]);
        public Task<IReadOnlyList<Managerr.Downloaders.QueueSweepAction>> EvaluateQueueHealthAndStrikesAsync(int strikeThreshold = 3, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Managerr.Downloaders.QueueSweepAction>>([]);
        public Task RemoveDownloadFromClientAsync(long clientId, string externalId, bool deleteData, CancellationToken ct = default) => Task.CompletedTask;
        public Task BlocklistReleaseHashAsync(string infoHash, string title, string reason, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"managerr-p3-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup race
        }
    }
}
