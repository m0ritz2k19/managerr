using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Managerr.Catalog;
using Managerr.Downloaders.Parser;
using Managerr.Indexers;
using Managerr.Indexers.Categories;
using Managerr.Indexers.Torznab;
using Managerr.Metadata;
using Managerr.Migration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Managerr.Phase6.Tests;

public sealed class ReleaseSearchOrchestratorTests
{
    private static readonly ReleaseParserRouter Parser = new();

    [Theory]
    [InlineData("DesertPlanet: Part Two", "DesertPlanet Part Two")]
    [InlineData("FantasyQuest – Hero’s blessing on this wonderful world!!", "FantasyQuest Hero's blessing on this wonderful world")]
    [InlineData("Re:Zero — Starting Life in Another World!", "ReZero Starting Life in Another World")]
    [InlineData("Spy x Family \"Code: White\"", "Spy x Family Code White")]
    public void SanitizeSearchTitle_CleansPunctuationAndDashes(string input, string expected)
    {
        var sanitized = ReleaseSearchOrchestrator.SanitizeSearchTitle(input);
        Assert.Equal(expected, sanitized);
    }

    [Fact]
    public void BuildQueryStems_EpisodicWithDash_ExtractsPrefixStem()
    {
        var stems = ReleaseSearchOrchestrator.BuildQueryStems(
            "FantasyQuest – Hero’s blessing on this wonderful world!!",
            ["Subarashii Fantasy Sekai ni Shukufuku wo!"],
            isEpisodic: true);

        Assert.Contains("FantasyQuest", stems);
        Assert.Contains("FantasyQuest Hero's blessing on this wonderful world", stems);
        Assert.Contains("Subarashii Fantasy Sekai ni Shukufuku wo", stems);
    }

    [Fact]
    public void BuildQueryStems_MovieWithColon_DoesNotExtractPrefixAsKnownTitle()
    {
        var stems = ReleaseSearchOrchestrator.BuildQueryStems(
            "DesertPlanet: Part Two",
            ["DesertPlanet Part Two"],
            isEpisodic: false);

        Assert.DoesNotContain("DesertPlanet", stems);
        Assert.Contains("DesertPlanet Part Two", stems);
    }

    [Theory]
    [InlineData("FantasyQuest 2", 2)]
    [InlineData("FantasyQuest S2", 2)]
    [InlineData("FantasyQuest S02", 2)]
    [InlineData("FantasyQuest Season 2", 2)]
    [InlineData("FantasyQuest 2nd Season", 2)]
    [InlineData("FantasyQuest II", 2)]
    [InlineData("FantasyQuest III", 3)]
    [InlineData("Subarashii Fantasy Sekai ni Shukufuku wo! 2", 2)]
    public void TryInferSequelSeason_RecognizesSequelPatterns(string releaseCleanTitle, int expectedSeason)
    {
        var parsed = Parser.Parse(releaseCleanTitle, "anime");
        // TryInferSequelSeason operates on parsed releases where SeasonNumber is not already recognized by standard token parsers,
        // and CleanTitle contains the sequel indicator (e.g., from an anime group title).
        var testParsed = parsed with { CleanTitle = releaseCleanTitle, SeasonNumber = null };
        var knownTitles = new[] { "FantasyQuest", "Subarashii Fantasy Sekai ni Shukufuku wo!" };

        var ok = ReleaseSearchOrchestrator.TryInferSequelSeason(testParsed, knownTitles, out var inferredSeason);

        Assert.True(ok);
        Assert.Equal(expectedSeason, inferredSeason);
    }

    [Fact]
    public void TryInferSequelSeason_WhenKnownTitleEndsWithDigit_DoesNotFalsePositive()
    {
        var parsed = Parser.Parse("Mob Psycho 100", "anime");
        var knownTitles = new[] { "Mob Psycho 100" };

        var ok = ReleaseSearchOrchestrator.TryInferSequelSeason(parsed, knownTitles, out var inferredSeason);

        Assert.False(ok);
        Assert.Equal(0, inferredSeason);
    }

    [Theory]
    [InlineData("FantasyQuest 3 Bonus Stage - E02 (OVA)", true)]
    [InlineData("[SubsPlease] FantasyQuest S02E02 (1080p)", false)]
    [InlineData("[Erai-raws] FantasyQuest - 02 [1080p]", false)]
    [InlineData("FantasyQuest OAD Special", true)]
    [InlineData("FantasyQuest NCOP1", true)]
    public void IsSpecialOrOvaTitle_IdentifiesSpecialsAndOvas(string rawTitle, bool expectedSpecial)
    {
        var isSpecial = ReleaseSearchOrchestrator.IsSpecialOrOvaTitle(rawTitle);
        Assert.Equal(expectedSpecial, isSpecial);
    }

    [Fact]
    public void EvaluateCandidateScope_AnimeSequelNumeralWithoutExplicitSeasonToken_MatchesSeason2Episode2()
    {
        // Anime scene release without explicit S02 token
        var candidate = new IndexedReleaseCandidate(
            Guid: "anime-s2-ep2",
            IndexerId: 1,
            IndexerName: "AnimeTracker",
            Protocol: "torrent",
            MediaVertical: "anime",
            Title: "[SubsPlease] FantasyQuest 2 - 02 (1080p) [12345678].mkv",
            DownloadUrl: "https://example.invalid/dl/1",
            MagnetUrl: null,
            InfoUrl: null,
            InfoHash: null,
            SizeBytes: 1_400_000_000,
            Seeders: 25,
            Peers: 5,
            DownloadVolumeFactor: 0,
            UploadVolumeFactor: 1,
            PublishedAt: DateTimeOffset.UtcNow,
            Categories: [5070],
            ImdbId: null,
            TmdbId: null,
            TvdbId: null);

        var parsed = Parser.Parse(candidate.Title, "anime");
        var knownTitles = new[] { "FantasyQuest", "FantasyQuest – Hero’s blessing on this wonderful world!!", "Subarashii Fantasy Sekai ni Shukufuku wo!" };

        var ok = ReleaseSearchOrchestrator.EvaluateCandidateScope(
            candidate,
            parsed,
            workTitle: "FantasyQuest – Hero’s blessing on this wonderful world!!",
            knownTitles: knownTitles,
            workYear: 2016,
            workOriginalLanguage: "ja",
            isEpisodic: true,
            isAnime: true,
            isMovie: false,
            workImdbId: null,
            workTmdbId: null,
            workTvdbId: 305603,
            workTvMazeId: null,
            requestedSeason: 2,
            requestedEpisode: 2,
            absoluteEpisodeNumber: 12,
            includeSeasonPacks: true,
            out var scoped);

        Assert.True(ok);
        Assert.NotNull(scoped);
        Assert.Equal(2, scoped.EffectiveSeason);
        Assert.Contains(2, scoped.EffectiveEpisodes);
        Assert.False(scoped.IsSeasonPack);
        Assert.True(scoped.IdentityVerified);
    }

    [Fact]
    public void EvaluateCandidateScope_Season1EpisodeWithoutSeasonTokenInSeason2Search_IsRejected()
    {
        // S1 release without S01 token: e.g. "FantasyQuest.E02.720p.2016" when searching Season 2
        var candidate = new IndexedReleaseCandidate(
            Guid: "anime-s1-ep2",
            IndexerId: 1,
            IndexerName: "UsenetIndexer",
            Protocol: "usenet",
            MediaVertical: "anime",
            Title: "FantasyQuest.E02.720p.2016",
            DownloadUrl: "https://example.invalid/dl/2",
            MagnetUrl: null,
            InfoUrl: null,
            InfoHash: null,
            SizeBytes: 500_000_000,
            Seeders: null,
            Peers: null,
            DownloadVolumeFactor: 1,
            UploadVolumeFactor: 1,
            PublishedAt: DateTimeOffset.UtcNow,
            Categories: [5070],
            ImdbId: null,
            TmdbId: null,
            TvdbId: null);

        var parsed = Parser.Parse(candidate.Title, "anime");
        var knownTitles = new[] { "FantasyQuest", "FantasyQuest – Hero’s blessing on this wonderful world!!" };

        var ok = ReleaseSearchOrchestrator.EvaluateCandidateScope(
            candidate,
            parsed,
            workTitle: "FantasyQuest – Hero’s blessing on this wonderful world!!",
            knownTitles: knownTitles,
            workYear: 2016,
            workOriginalLanguage: "ja",
            isEpisodic: true,
            isAnime: true,
            isMovie: false,
            workImdbId: null,
            workTmdbId: null,
            workTvdbId: 305603,
            workTvMazeId: null,
            requestedSeason: 2,
            requestedEpisode: 2,
            absoluteEpisodeNumber: 12,
            includeSeasonPacks: true,
            out _);

        // Must be rejected because it lacks Season 2 identity and has no matching absolute episode number
        Assert.False(ok);
    }

    [Fact]
    public void EvaluateCandidateScope_OlderAdaptationYear_InSequelMovieSearch_IsRejected()
    {
        var candidate = new IndexedReleaseCandidate(
            Guid: "desertplanet-1984",
            IndexerId: 1,
            IndexerName: "IndexHub",
            Protocol: "usenet",
            MediaVertical: "movies",
            Title: "DesertPlanet.1984.1080p.BluRay.x264-OLDIE",
            DownloadUrl: "https://example.invalid/dl/3",
            MagnetUrl: null,
            InfoUrl: null,
            InfoHash: null,
            SizeBytes: 2_000_000_000,
            Seeders: null,
            Peers: null,
            DownloadVolumeFactor: 1,
            UploadVolumeFactor: 1,
            PublishedAt: DateTimeOffset.UtcNow,
            Categories: [2040],
            ImdbId: null,
            TmdbId: null,
            TvdbId: null);

        var parsed = Parser.Parse(candidate.Title, "movies");
        var knownTitles = new[] { "DesertPlanet: Part Two", "DesertPlanet Part Two" };

        var ok = ReleaseSearchOrchestrator.EvaluateCandidateScope(
            candidate,
            parsed,
            workTitle: "DesertPlanet: Part Two",
            knownTitles: knownTitles,
            workYear: 2024,
            workOriginalLanguage: "en",
            isEpisodic: false,
            isAnime: false,
            isMovie: true,
            workImdbId: null,
            workTmdbId: 693134,
            workTvdbId: null,
            workTvMazeId: null,
            requestedSeason: null,
            requestedEpisode: null,
            absoluteEpisodeNumber: null,
            includeSeasonPacks: false,
            out _);

        Assert.False(ok);
    }

    [Fact]
    public void MergeLanguages_CombinesParsedDualAudioAndIndexerLanguages()
    {
        var candidate = new IndexedReleaseCandidate(
            Guid: "anime-dual",
            IndexerId: 1,
            IndexerName: "Indexer",
            Protocol: "torrent",
            MediaVertical: "anime",
            Title: "[SubsPlease] Show - S02E02 [Dual-Audio] [1080p]",
            DownloadUrl: "https://example.invalid/dl/4",
            MagnetUrl: null,
            InfoUrl: null,
            InfoHash: null,
            SizeBytes: 1_000_000,
            Seeders: 10,
            Peers: 2,
            DownloadVolumeFactor: 0,
            UploadVolumeFactor: 1,
            PublishedAt: DateTimeOffset.UtcNow,
            Categories: [5070],
            ImdbId: null,
            TmdbId: null,
            TvdbId: null,
            IndexerLanguages: ["German", "Japanese"]);

        var parsed = Parser.Parse(candidate.Title, "anime");
        var merged = ReleaseSearchOrchestrator.MergeLanguages(candidate, parsed, originalLanguage: "ja");

        Assert.Contains("de", merged);
        Assert.Contains("ja", merged);
    }

    [Fact]
    public void ParseXmlFeed_NewznabErrorElement_ThrowsIndexerApiException()
    {
        const string errorXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <error code="100" description="Incorrect user credentials"/>
            """;

        var ex = Assert.Throws<IndexerApiException>(() =>
            TorznabFeedParser.ParseXmlFeed(errorXml, 4, "SceneNZBs", "usenet"));

        Assert.Equal(100, ex.ErrorCode);
        Assert.Contains("Incorrect user credentials", ex.Description);
    }

    [Fact]
    public async Task PostIndexersSearch_AnimeSequelEpisode_SceneNzbsAndNzbGeekParity_FindsGermanAndSequelReleasesAndExcludesNoise()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"anime-search-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var gateway = new FakeNewznabParityGateway();
            const string pass = "AnimeSearch!2026";

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                    ["Managerr:MasterKey"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                    ["Managerr:SchedulerEnabled"] = "false",
                    ["Managerr:TestbedBootstrap:Enabled"] = "true",
                    ["Managerr:TestbedBootstrap:Username"] = "testadmin",
                    ["Managerr:TestbedBootstrap:Password"] = pass,
                }));
                b.ConfigureServices(s =>
                {
                    s.RemoveAll<IIndexerHttpGateway>();
                    s.AddSingleton<IIndexerHttpGateway>(gateway);
                });
            });

            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
            var loginResp = await client.PostAsJsonAsync("/api/v1/auth/breakglass/login", new
            {
                usernameOrEmail = "testadmin",
                password = pass,
            });
            loginResp.EnsureSuccessStatusCode();
            var loginBody = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
            if (loginBody.TryGetProperty("csrfToken", out var csrf) && csrf.GetString() is { Length: > 0 } token)
            {
                client.DefaultRequestHeaders.Add("X-Managerr-Csrf", token);
            }

            long workId;
            long s02e02UnitId;

            using (var scope = factory.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var catalog = sp.GetRequiredService<IPolymorphicCatalogService>();
                var indexers = sp.GetRequiredService<IUnifiedIndexerService>();

                var animeRoot = Path.Combine(tempDir, "media", "anime");
                Directory.CreateDirectory(animeRoot);
                var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Anime", "anime", animeRoot));

                // Configure NZBGeek and SceneNZBs matching production categories & priorities
                await indexers.CreateIndexerAsync(new UpsertIndexerRequest(
                    Name: "NZBGeek",
                    Implementation: "newznab",
                    DefinitionId: null,
                    Protocol: "usenet",
                    BaseUrl: "https://api.nzbgeek.info/",
                    ApiKey: "geek-test-key",
                    Categories: [2000, 2040, 2045, 3000, 3030, 5000, 5030, 5040, 5045, 5070, 7000, 7020, 8000],
                    Priority: 50,
                    RequestDelayMs: 0));

                await indexers.CreateIndexerAsync(new UpsertIndexerRequest(
                    Name: "SceneNZBs",
                    Implementation: "newznab",
                    DefinitionId: null,
                    Protocol: "usenet",
                    BaseUrl: "https://treasure-maps.com",
                    ApiKey: "scene-test-key",
                    Categories: [2000, 2040, 2045, 2100, 3000, 3030, 5000, 5030, 5040, 5045, 5070, 5100, 7000, 7020, 7100, 8000],
                    Priority: 25,
                    RequestDelayMs: 0));

                var tree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                    LibraryId: lib.Id,
                    Title: "FantasyQuest – Hero’s blessing on this wonderful world!!",
                    WorkKind: "anime",
                    AltTitles: ["FantasyQuest", "Subarashii Fantasy Sekai ni Shukufuku wo!"],
                    OriginalLanguage: "ja",
                    Year: 2016,
                    Status: "continuing",
                    Monitored: true,
                    ExternalIds: new Dictionary<string, string>
                    {
                        ["tvdb"] = "303867",
                        ["imdb"] = "tt5370118",
                        ["tmdb"] = "65844",
                        ["tvmaze"] = "10772",
                    },
                    Containers:
                    [
                        new MetadataContainerSeed("season", 1, "Season 1", "2016-01-14",
                        [
                            new MetadataUnitSeed("episode", 2, 2, "An Explosion for This Chunibyo!", "2016-01-21"),
                        ]),
                        new MetadataContainerSeed("season", 2, "Season 2", "2017-01-12",
                        [
                            new MetadataUnitSeed("episode", 2, 13, "A Friend for This Crimson Demon Girl!", "2017-01-19"),
                        ]),
                    ]));

                workId = tree.Work.Id;
                s02e02UnitId = tree.Containers.Single(c => c.ContainerNumber == 2).Units.Single(u => u.UnitNumber == 2).Id;
            }

            // Fire the exact Interactive Search request sent by CatalogView.tsx for FantasyQuest S02E02
            var resp = await client.PostAsJsonAsync("/api/v1/indexers/search", new
            {
                query = "FantasyQuest – Hero’s blessing on this wonderful world!!",
                mediaVertical = "anime",
                imdbId = "tt5370118",
                tmdbId = 65844,
                tvdbId = 303867,
                tvMazeId = 10772,
                season = 2,
                episode = 2,
                interactive = true,
                workId,
                unitId = s02e02UnitId,
            });
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            var items = (body.ValueKind == JsonValueKind.Array ? body : body.GetProperty("items")).EnumerateArray().ToList();
            var titles = items.Select(i => i.GetProperty("release").GetProperty("title").GetString()!).ToList();

            // 1. Verify NZBGeek never received unsupported imdbid/tmdbid or regional 51xx categories on tvsearch,
            //    and never mixed q=... with tvdbid=303867!
            var geekTvQueries = gateway.RequestedUris
                .Where(u => u.Host.Contains("nzbgeek.info") && u.Query.Contains("t=tvsearch"))
                .ToList();
            Assert.NotEmpty(geekTvQueries);
            foreach (var gUri in geekTvQueries.Where(u => u.Query.Contains("tvdbid=")))
            {
                Assert.DoesNotContain("imdbid=", gUri.Query);
                Assert.DoesNotContain("tmdbid=", gUri.Query);
                Assert.DoesNotContain("q=", gUri.Query);
                Assert.DoesNotContain("5170", gUri.Query);
                Assert.DoesNotContain("5100", gUri.Query);
            }

            // 2. Verify SceneNZBs received regional DE categories (5100/5140/5170) alongside 5040/5070, and NO q= when querying tvdbid!
            var sceneIdQueries = gateway.RequestedUris
                .Where(u => u.Host.Contains("treasure-maps.com") && u.Query.Contains("tvdbid=303867"))
                .ToList();
            Assert.NotEmpty(sceneIdQueries);
            foreach (var sUri in sceneIdQueries)
            {
                Assert.DoesNotContain("q=", sUri.Query);
                Assert.DoesNotContain("imdbid=", sUri.Query);
                Assert.DoesNotContain("tmdbid=", sUri.Query);
                Assert.Contains("5170", sUri.Query);
                Assert.Contains("5140", sUri.Query);
            }

            // 3. Verify all valid S02E02 German, Dual-Audio, Sequel-Numbered, RFC-2047 Base64, and HTML-entity releases are found!
            Assert.Contains("FantasyQuest.S02E02.German.ANIME.DTS.DL.1080p.BluRay.x264-Fuuko", titles);
            Assert.Contains("FantasyQuest.S02E02.German.DL.DTS.1080p.BluRay.x265-ABJ", titles);
            Assert.Contains("FantasyQuest.2.E02.German.AC3.DL.1080p.BluRay.x264-Fuuko", titles);
            Assert.Contains("FantasyQuest.2.E02.Yunyuns.Book.of.Friends.German.2017.ANIME.DL.1080p.BluRay.x264-sUppLeX", titles);
            Assert.Contains("[HorribleSubs] Subarashii Fantasy Sekai ni Shukufuku wo! 2 - 02 [1080p]", titles);
            Assert.Contains("[Foxtrot] Subarashii Fantasy Sekai ni Shukufuku wo! S2 - 02 [BD 1080p]", titles);
            Assert.Contains("FantasyQuest - Hero's Blessing on This Wonderful World! - S02E02 - Dual-Audio BluRay-1080p", titles);
            Assert.Contains("[BlackRabbit] FantasyQuest Hero's Blessing on This Wonderful World! - S02 (BDRip 1920x1080p AV1 10-bit OPUS) [Ger & Jap Dub]", titles);

            // 4. Verify S01E02, seasonless 2016 S1 E02, and OVA noise are strictly excluded!
            Assert.DoesNotContain(titles, t => t.Contains("S01E02", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(titles, t => t.Contains("Eine.Explosion.fuer.diesen.Chunibyo", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(titles, t => t.Contains("Bonus Stage", StringComparison.OrdinalIgnoreCase));

            // 5. Verify top-ranked item is an approved German/Dual-Audio S02E02 single-episode release (ahead of the S02 pack)
            var topItem = items[0];
            Assert.True(topItem.GetProperty("approved").GetBoolean());
            Assert.DoesNotContain("- S02 (", topItem.GetProperty("release").GetProperty("title").GetString()!);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    private sealed class FakeNewznabParityGateway : IIndexerHttpGateway
    {
        public List<Uri> RequestedUris { get; } = [];

        public Task<string> FetchStringAsync(Uri targetUri, IndexerProxyInternal? matchedProxy, CancellationToken ct = default)
        {
            lock (RequestedUris)
            {
                RequestedUris.Add(targetUri);
            }

            var q = targetUri.Query;
            var isGeek = targetUri.Host.Contains("nzbgeek.info", StringComparison.OrdinalIgnoreCase);

            if (q.Contains("t=caps", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(isGeek ? GeekCapsXml : SceneNzbsCapsXml);
            }

            // Simulate NZBGeek strict parameter bug: returns 0 items if imdbid/tmdbid or unsanitized q is mixed with tvdbid!
            if (isGeek)
            {
                if (q.Contains("imdbid=", StringComparison.OrdinalIgnoreCase) ||
                    q.Contains("tmdbid=", StringComparison.OrdinalIgnoreCase) ||
                    (q.Contains("tvdbid=", StringComparison.OrdinalIgnoreCase) && q.Contains("q=", StringComparison.OrdinalIgnoreCase)))
                {
                    return Task.FromResult(EmptyRssXml);
                }

                if (q.Contains("tvdbid=303867", StringComparison.OrdinalIgnoreCase) && q.Contains("ep=2", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(GeekS02E02Xml);
                }

                if (q.Contains("Subarashii+Fantasy", StringComparison.OrdinalIgnoreCase) || q.Contains("Subarashii%20Fantasy", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(GeekSceneAliasXml);
                }

                return Task.FromResult(EmptyRssXml);
            }

            // Simulate SceneNZBs: if q=FantasyQuest...wonderful world is mixed with cat=5070, only return 1 S01E02 item (reproducing the exact prod bug!)
            if (q.Contains("tvdbid=303867", StringComparison.OrdinalIgnoreCase) && q.Contains("q=", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(SceneBuggySingleS01ItemXml);
            }

            if (q.Contains("tvdbid=303867", StringComparison.OrdinalIgnoreCase) && q.Contains("season=2", StringComparison.OrdinalIgnoreCase) && q.Contains("ep=2", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(SceneTvdbS02E02Xml);
            }

            if (q.Contains("tvdbid=303867", StringComparison.OrdinalIgnoreCase) && q.Contains("season=2", StringComparison.OrdinalIgnoreCase) && !q.Contains("ep=", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(SceneTvdbS02PackXml);
            }

            return Task.FromResult(EmptyRssXml);
        }

        private const string EmptyRssXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
              <channel></channel>
            </rss>
            """;

        private const string GeekCapsXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <caps>
              <searching>
                <search available="yes" supportedParams="q"/>
                <tv-search available="yes" supportedParams="q,rid,tvdbid,tvmazeid,season,ep"/>
                <movie-search available="yes" supportedParams="q,imdbid,genre"/>
                <audio-search available="yes" supportedParams="q"/>
              </searching>
              <categories>
                <category id="2000" name="Movies"><subcat id="2040" name="HD"/><subcat id="2045" name="UHD"/></category>
                <category id="5000" name="TV"><subcat id="5030" name="SD"/><subcat id="5040" name="HD"/><subcat id="5045" name="UHD"/><subcat id="5070" name="Anime"/></category>
              </categories>
            </caps>
            """;

        private const string SceneNzbsCapsXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <caps>
              <searching>
                <search available="yes" supportedParams="q"/>
                <tv-search available="yes" supportedParams="q,vid,tvdbid,traktid,rid,tvmazeid,imdbid,tmdbid,season,ep"/>
                <movie-search available="yes" supportedParams="q,imdbid,tmdbid,traktid,genre"/>
                <book-search available="yes" supportedParams="q,title,author"/>
              </searching>
              <categories>
                <category id="5000" name="TV - INT"><subcat id="5030" name="SD"/><subcat id="5040" name="HD"/><subcat id="5045" name="UHD"/><subcat id="5070" name="Anime"/></category>
                <category id="5100" name="TV - DE"><subcat id="5130" name="SD"/><subcat id="5140" name="HD"/><subcat id="5145" name="UHD"/><subcat id="5170" name="Anime"/></category>
              </categories>
            </caps>
            """;

        private const string SceneBuggySingleS01ItemXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
              <channel>
                <item>
                  <title>FantasyQuest - Hero's Blessing on This Wonderful World! S01E02 1080p CR WEB-DL MULTI AAC2.0 H.264-OldT</title>
                  <guid>scene-s01-oldt</guid>
                  <link>https://treasure-maps.com/getnzb/scene-s01-oldt.nzb</link>
                  <enclosure url="https://treasure-maps.com/getnzb/scene-s01-oldt.nzb" length="1932735283" type="application/x-nzb"/>
                  <newznab:attr name="category" value="5070"/>
                  <newznab:attr name="tvdbid" value="303867"/>
                </item>
              </channel>
            </rss>
            """;

        private const string SceneTvdbS02E02Xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
              <channel>
                <item>
                  <title>FantasyQuest.S02E02.German.ANIME.DTS.DL.1080p.BluRay.x264-Fuuko</title>
                  <guid>scene-fuuko-s02e02</guid>
                  <link>https://treasure-maps.com/getnzb/scene-fuuko-s02e02.nzb</link>
                  <enclosure url="https://treasure-maps.com/getnzb/scene-fuuko-s02e02.nzb" length="2899102924" type="application/x-nzb"/>
                  <newznab:attr name="category" value="5100"/>
                  <newznab:attr name="category" value="5170"/>
                  <newznab:attr name="tvdbid" value="303867"/>
                  <newznab:attr name="language" value="German,Japanese"/>
                  <newznab:attr name="subs" value="German"/>
                </item>
                <item>
                  <title>FantasyQuest.S02E02.German.DL.DTS.1080p.BluRay.x265-ABJ</title>
                  <guid>scene-abj-s02e02</guid>
                  <link>https://treasure-maps.com/getnzb/scene-abj-s02e02.nzb</link>
                  <enclosure url="https://treasure-maps.com/getnzb/scene-abj-s02e02.nzb" length="2791728742" type="application/x-nzb"/>
                  <newznab:attr name="category" value="5100"/>
                  <newznab:attr name="category" value="5140"/>
                  <newznab:attr name="tvdbid" value="303867"/>
                  <newznab:attr name="language" value="German,Japanese"/>
                </item>
                <item>
                  <title>FantasyQuest.2.E02.German.AC3.DL.1080p.BluRay.x264-Fuuko</title>
                  <guid>scene-fuuko-2-e02</guid>
                  <link>https://treasure-maps.com/getnzb/scene-fuuko-2-e02.nzb</link>
                  <enclosure url="https://treasure-maps.com/getnzb/scene-fuuko-2-e02.nzb" length="2362232012" type="application/x-nzb"/>
                  <newznab:attr name="category" value="5170"/>
                  <newznab:attr name="tvdbid" value="303867"/>
                  <newznab:attr name="language" value="German,Japanese"/>
                </item>
                <item>
                  <title>FantasyQuest.2.E02.Yunyuns.Book.of.Friends.German.2017.ANIME.DL.1080p.BluRay.x264-sUppLeX</title>
                  <guid>scene-supplex-2-e02</guid>
                  <link>https://treasure-maps.com/getnzb/scene-supplex-2-e02.nzb</link>
                  <enclosure url="https://treasure-maps.com/getnzb/scene-supplex-2-e02.nzb" length="1288490188" type="application/x-nzb"/>
                  <newznab:attr name="category" value="5170"/>
                  <newznab:attr name="tvdbid" value="303867"/>
                  <newznab:attr name="language" value="German,Japanese"/>
                </item>
                <item>
                  <title>FantasyQuest - Hero's Blessing on This Wonderful World! S01E02 1080p CR WEB-DL MULTI AAC2.0 H.264-OldT</title>
                  <guid>scene-s01e02-noise</guid>
                  <link>https://treasure-maps.com/getnzb/scene-s01e02-noise.nzb</link>
                  <enclosure url="https://treasure-maps.com/getnzb/scene-s01e02-noise.nzb" length="1932735283" type="application/x-nzb"/>
                  <newznab:attr name="category" value="5070"/>
                  <newznab:attr name="tvdbid" value="303867"/>
                </item>
                <item>
                  <title>FantasyQuest.E02.Eine.Explosion.fuer.diesen.Chunibyo.German.2016.ANIME.DL.1080p.BluRay.x264-STARS</title>
                  <guid>scene-s01-bare-e02-noise</guid>
                  <link>https://treasure-maps.com/getnzb/scene-s01-bare-e02-noise.nzb</link>
                  <enclosure url="https://treasure-maps.com/getnzb/scene-s01-bare-e02-noise.nzb" length="1500000000" type="application/x-nzb"/>
                  <newznab:attr name="category" value="5170"/>
                  <newznab:attr name="tvdbid" value="303867"/>
                  <newznab:attr name="season" value="1"/>
                  <newznab:attr name="episode" value="E02"/>
                </item>
                <item>
                  <title>FantasyQuest - Hero's Blessing on This Wonderful World! 3 Bonus Stage - E02 (OVA) [1080p]</title>
                  <guid>scene-s03-ova-noise</guid>
                  <link>https://treasure-maps.com/getnzb/scene-s03-ova-noise.nzb</link>
                  <enclosure url="https://treasure-maps.com/getnzb/scene-s03-ova-noise.nzb" length="1400000000" type="application/x-nzb"/>
                  <newznab:attr name="category" value="5070"/>
                  <newznab:attr name="tvdbid" value="303867"/>
                </item>
              </channel>
            </rss>
            """;

        private static readonly string SceneTvdbS02PackXml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
              <channel>
                <item>
                  <title>=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes("[BlackRabbit] FantasyQuest Hero's Blessing on This Wonderful World! - S02 (BDRip 1920x1080p AV1 10-bit OPUS) [Ger & Jap Dub].nzb"))}?=</title>
                  <guid>scene-blackrabbit-s02-pack</guid>
                  <link>https://treasure-maps.com/getnzb/scene-blackrabbit-s02-pack.nzb</link>
                  <enclosure url="https://treasure-maps.com/getnzb/scene-blackrabbit-s02-pack.nzb" length="9500000000" type="application/x-nzb"/>
                  <newznab:attr name="category" value="5170"/>
                  <newznab:attr name="tvdbid" value="303867"/>
                  <newznab:attr name="language" value="German,Japanese"/>
                </item>
              </channel>
            </rss>
            """;

        private const string GeekS02E02Xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
              <channel>
                <item>
                  <title><![CDATA[FantasyQuest - Hero&#039;s Blessing on This Wonderful World! - S02E02 - Dual-Audio BluRay-1080p]]></title>
                  <guid>geek-dual-s02e02</guid>
                  <link>https://api.nzbgeek.info/api?t=get&amp;id=geek-dual-s02e02</link>
                  <enclosure url="https://api.nzbgeek.info/api?t=get&amp;id=geek-dual-s02e02" length="3100000000" type="application/x-nzb"/>
                  <newznab:attr name="category" value="5040"/>
                  <newznab:attr name="tvdbid" value="303867"/>
                  <newznab:attr name="language" value="English,Japanese"/>
                </item>
                <item>
                  <title>[Foxtrot] Subarashii Fantasy Sekai ni Shukufuku wo! S2 - 02 [BD 1080p]</title>
                  <guid>geek-foxtrot-s2-02</guid>
                  <link>https://api.nzbgeek.info/api?t=get&amp;id=geek-foxtrot-s2-02</link>
                  <enclosure url="https://api.nzbgeek.info/api?t=get&amp;id=geek-foxtrot-s2-02" length="1900000000" type="application/x-nzb"/>
                  <newznab:attr name="category" value="5070"/>
                  <newznab:attr name="tvdbid" value="303867"/>
                </item>
              </channel>
            </rss>
            """;

        private const string GeekSceneAliasXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
              <channel>
                <item>
                  <title>[HorribleSubs] Subarashii Fantasy Sekai ni Shukufuku wo! 2 - 02 [1080p]</title>
                  <guid>geek-horriblesubs-2-02</guid>
                  <link>https://api.nzbgeek.info/api?t=get&amp;id=geek-horriblesubs-2-02</link>
                  <enclosure url="https://api.nzbgeek.info/api?t=get&amp;id=geek-horriblesubs-2-02" length="750000000" type="application/x-nzb"/>
                  <newznab:attr name="category" value="5070"/>
                </item>
              </channel>
            </rss>
            """;
    }

    [Fact]
    public void F02_IndexerSupportsCategories_DirectionalParentChildInheritance()
    {
        var tvCats = NewznabCategoryMapper.GetCategoriesForVertical("tv");
        Assert.False(NewznabCategoryMapper.IndexerSupportsCategories(tvCats, [5070]));

        var musicCats = NewznabCategoryMapper.GetCategoriesForVertical("music");
        Assert.False(NewznabCategoryMapper.IndexerSupportsCategories(musicCats, [3030]));

        var ebookCats = NewznabCategoryMapper.GetCategoriesForVertical("ebooks");
        Assert.False(NewznabCategoryMapper.IndexerSupportsCategories(ebookCats, [7030]));

        // Conversely, parent in indexer supports desired child
        Assert.True(NewznabCategoryMapper.IndexerSupportsCategories([5070], [5000]));
    }

    [Theory]
    [InlineData("https://indexer.example/api", "../admin")]
    [InlineData("https://indexer.example/api", "api\\torznab")]
    [InlineData("https://indexer.example/api", "torznab?foo=bar")]
    [InlineData("https://indexer.example/api", "torznab#hash")]
    public void F03_BuildEndpointUri_RejectsInvalidApiPath(string baseUrl, string apiPath)
    {
        Assert.Throws<ArgumentException>(() => TorznabFeedParser.BuildEndpointUri(baseUrl, apiPath));
    }

    [Theory]
    [InlineData("ftp://indexer.example/api")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not-a-url")]
    public void F03_BuildEndpointUri_RejectsNonHttpScheme(string baseUrl)
    {
        Assert.Throws<ArgumentException>(() => TorznabFeedParser.BuildEndpointUri(baseUrl));
    }

    [Fact]
    public void F05_TryInferSequelSeason_CompletesFastOnAdversarialLongTitle()
    {
        var longTitle = "A" + new string('.', 10_000) + "Z";
        var parsed = Parser.Parse(longTitle, "anime");
        var testParsed = parsed with { CleanTitle = longTitle, SeasonNumber = null };
        var knownTitles = new[] { "Attack on Titan", "Shingeki no Kyojin" };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ok = ReleaseSearchOrchestrator.TryInferSequelSeason(testParsed, knownTitles, out var inferredSeason);
        sw.Stop();

        Assert.False(ok);
        Assert.Equal(0, inferredSeason);
        Assert.True(sw.ElapsedMilliseconds < 50, $"Elapsed was {sw.ElapsedMilliseconds}ms, expected < 50ms");
    }

    [Fact]
    public void F06c_SearchingS02E01_WithAbsoluteNumber13_DoesNotMatchS02E13()
    {
        var candidate = new IndexedReleaseCandidate(
            Guid: "guid-s02e13",
            IndexerId: 1,
            IndexerName: "AnimeTracker",
            Protocol: "torrent",
            MediaVertical: "anime",
            Title: "[SubsPlease] FantasyQuest S02E13 (1080p)",
            DownloadUrl: "https://example.invalid/dl/13",
            MagnetUrl: null,
            InfoUrl: null,
            InfoHash: null,
            SizeBytes: 1_400_000_000,
            Seeders: 25,
            Peers: 5,
            DownloadVolumeFactor: 0,
            UploadVolumeFactor: 1,
            PublishedAt: DateTimeOffset.UtcNow,
            Categories: [5070],
            ImdbId: null,
            TmdbId: null,
            TvdbId: null);

        var parsed = Parser.Parse(candidate.Title, "anime");
        var knownTitles = new[] { "FantasyQuest" };

        var ok = ReleaseSearchOrchestrator.EvaluateCandidateScope(
            candidate,
            parsed,
            workTitle: "FantasyQuest",
            knownTitles: knownTitles,
            workYear: 2017,
            workOriginalLanguage: "ja",
            isEpisodic: true,
            isAnime: true,
            isMovie: false,
            workImdbId: null,
            workTmdbId: null,
            workTvdbId: 305603,
            workTvMazeId: null,
            requestedSeason: 2,
            requestedEpisode: 1,
            absoluteEpisodeNumber: 13,
            includeSeasonPacks: false,
            out var scoped);

        Assert.False(ok, "S02E13 should not match search for S02E01 even when S02E01 has AbsoluteNumber = 13");
        Assert.Null(scoped);
    }

    [Fact]
    public async Task F01_NewznabErrorHandling_Code300DoesNotFailIndexer_Code100FailsWithRedactedError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"f01-error-handling-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var fakeGateway = new FakeErrorCodeGateway();
            const string pass = "SearchAdminPass!2026";

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                    ["Managerr:MasterKey"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                    ["Managerr:SchedulerEnabled"] = "false",
                    ["Managerr:TestbedBootstrap:Enabled"] = "true",
                    ["Managerr:TestbedBootstrap:Username"] = "testadmin",
                    ["Managerr:TestbedBootstrap:Password"] = pass,
                }));
                b.ConfigureServices(s =>
                {
                    s.RemoveAll<IIndexerHttpGateway>();
                    s.AddSingleton<IIndexerHttpGateway>(fakeGateway);
                });
            });

            using var scope = factory.Services.CreateScope();
            var indexerService = scope.ServiceProvider.GetRequiredService<IUnifiedIndexerService>();

            // 1. Create an indexer that returns code="300" on tvsearch (e.g. Wave 1 external ID search)
            //    and returns a valid item on Wave 2 text query.
            var ix300 = await indexerService.CreateIndexerAsync(new UpsertIndexerRequest(
                Name: "Indexer300",
                Implementation: "newznab",
                DefinitionId: null,
                Protocol: "usenet",
                BaseUrl: "https://indexer300.example/api",
                ApiKey: "my-secret-key-300",
                Categories: [5000, 5030, 5040],
                Priority: 25,
                RequestDelayMs: 0));

            // Query with TvdbId -> returns <error code="300" description="No such item"/>
            var results300 = await indexerService.SearchAsync(new IndexerSearchQuery(
                Query: "Test Show",
                MediaVertical: "tv",
                TvdbId: 12345,
                Interactive: true));

            Assert.Empty(results300);

            // Verify ConsecutiveFailures is 0 and DisabledUntil is null!
            var indexersList = await indexerService.ListIndexersAsync();
            var summary300 = indexersList.Single(x => x.Id == ix300.Id);
            Assert.Equal(0, summary300.ConsecutiveFailures);
            Assert.Null(summary300.DisabledUntil);
            Assert.True(summary300.IsHealthy);

            // Now perform Wave 2 text search on this healthy indexer -> succeeds!
            fakeGateway.ReturnValidOnText = true;
            var textResults = await indexerService.SearchAsync(new IndexerSearchQuery(
                Query: "Test Show",
                MediaVertical: "tv",
                Interactive: true));

            Assert.NotEmpty(textResults);

            // 2. Query against an indexer returning <error code="100" description="Invalid apikey=secret123"/>
            var ix100 = await indexerService.CreateIndexerAsync(new UpsertIndexerRequest(
                Name: "Indexer100",
                Implementation: "newznab",
                DefinitionId: null,
                Protocol: "usenet",
                BaseUrl: "https://indexer100.example/api",
                ApiKey: "secret123",
                Categories: [5000],
                Priority: 25,
                RequestDelayMs: 0));

            fakeGateway.ReturnValidOnText = false;
            var results100 = await indexerService.SearchAsync(new IndexerSearchQuery(
                Query: "Fail Show",
                MediaVertical: "tv",
                Interactive: true));

            Assert.Empty(results100);

            var listAfter100 = await indexerService.ListIndexersAsync();
            var summary100 = listAfter100.Single(x => x.Id == ix100.Id);
            Assert.True(summary100.ConsecutiveFailures > 0);
            Assert.NotNull(summary100.DisabledUntil);
            Assert.False(summary100.IsHealthy);
            Assert.NotNull(summary100.LastError);
            Assert.Contains("[REDACTED]", summary100.LastError);
            Assert.DoesNotContain("secret123", summary100.LastError);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    private sealed class FakeErrorCodeGateway : IIndexerHttpGateway
    {
        public bool ReturnValidOnText { get; set; }

        public Task<string> FetchStringAsync(Uri targetUri, IndexerProxyInternal? matchedProxy, CancellationToken ct = default)
        {
            var q = targetUri.Query;
            if (q.Contains("t=caps", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult("""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <caps>
                      <searching>
                        <search available="yes" supportedParams="q"/>
                        <tv-search available="yes" supportedParams="q,tvdbid,season,ep"/>
                      </searching>
                      <categories>
                        <category id="5000" name="TV"><subcat id="5030" name="SD"/><subcat id="5040" name="HD"/></category>
                      </categories>
                    </caps>
                    """);
            }

            if (targetUri.Host.Contains("indexer100", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult("""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <error code="100" description="Invalid apikey=secret123"/>
                    """);
            }

            if (targetUri.Host.Contains("indexer300", StringComparison.OrdinalIgnoreCase))
            {
                if (q.Contains("tvdbid=", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult("""
                        <?xml version="1.0" encoding="UTF-8"?>
                        <error code="300" description="No such item"/>
                        """);
                }

                if (ReturnValidOnText)
                {
                    return Task.FromResult("""
                        <?xml version="1.0" encoding="UTF-8"?>
                        <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
                          <channel>
                            <item>
                              <title>Test.Show.S01E01.1080p.WEB-DL</title>
                              <guid>test-show-s01e01</guid>
                              <link>https://indexer300.example/get/1</link>
                              <enclosure url="https://indexer300.example/get/1" length="1000000" type="application/x-nzb"/>
                              <newznab:attr name="category" value="5040"/>
                            </item>
                          </channel>
                        </rss>
                        """);
                }
            }

            return Task.FromResult("""
                <?xml version="1.0" encoding="UTF-8"?>
                <rss version="2.0">
                  <channel></channel>
                </rss>
                """);
        }
    }

    [Fact]
    public async Task IndexerHttpGateway_SendsDefaultUserAgent_PreventingNzbGeekError109_AndHonorsCustomCardigannUserAgent()
    {
        var seenUserAgents = new List<string>();
        var handler = new RecordingUserAgentHandler(seenUserAgents);
        var fakeSafeHttp = new CapturingSafeHttpClientFactory(handler);
        var gateway = new IndexerHttpGateway(fakeSafeHttp);

        var defaultResp = await gateway.SendAsync(
            new IndexerHttpRequest(new Uri("https://api.nzbgeek.example/api?t=tvsearch&cat=5070&tvdbid=303867")),
            matchedProxy: null);
        Assert.Equal(200, defaultResp.StatusCode);

        var customResp = await gateway.SendAsync(
            new IndexerHttpRequest(
                new Uri("https://tracker.example/search"),
                Headers: new Dictionary<string, string> { ["User-Agent"] = "ManagerrCardigann/2.0" }),
            matchedProxy: null);
        Assert.Equal(200, customResp.StatusCode);

        Assert.Equal(2, seenUserAgents.Count);
        Assert.Equal(Managerr.Common.Net.SafeHttpClientFactory.DefaultUserAgent, seenUserAgents[0]);
        Assert.Equal("ManagerrCardigann/2.0", seenUserAgents[1]);

        using var prodClient = new Managerr.Common.Net.SafeHttpClientFactory(
            Microsoft.Extensions.Options.Options.Create(new Managerr.Common.Net.SafeHttpEgressOptions()))
            .CreateHomelabClient();
        Assert.Equal(
            Managerr.Common.Net.SafeHttpClientFactory.DefaultUserAgent,
            prodClient.DefaultRequestHeaders.UserAgent.ToString());
    }

    private sealed class RecordingUserAgentHandler(List<string> seenUserAgents) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            seenUserAgents.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("<rss version=\"2.0\"><channel></channel></rss>", System.Text.Encoding.UTF8, "application/xml"),
            });
        }
    }

    private sealed class CapturingSafeHttpClientFactory(HttpMessageHandler handler) : Managerr.Common.Net.ISafeHttpClientFactory
    {
        public HttpClient CreatePublicClient(bool ignoreSslErrors = false) => CreateClient();
        public HttpClient CreateHomelabClient(bool allowPrivateLan = true, bool ignoreSslErrors = false) => CreateClient();
        public void EnsureAddressPermitted(System.Net.IPAddress address, string? targetHost, bool allowPrivateLan) { }

        private HttpClient CreateClient()
        {
            var client = new HttpClient(handler, disposeHandler: false);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(Managerr.Common.Net.SafeHttpClientFactory.DefaultUserAgent);
            return client;
        }
    }
}
