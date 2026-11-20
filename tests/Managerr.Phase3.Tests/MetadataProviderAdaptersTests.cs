using System.Collections.Concurrent;
using Managerr.Common.Crypto;
using Managerr.Common.Paths;
using Managerr.Datastore;
using Managerr.Datastore.Migrations;
using Managerr.Datastore.Services;
using Managerr.Metadata;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Managerr.Phase3.Tests;

public sealed class MetadataProviderAdaptersTests
{
    [Fact]
    public async Task KeyGatedProviders_ReportNotConfigured_UntilApiKeyIsProvided()
    {
        var tempDir = CreateTempDir();
        try
        {
            var recorded = new ConcurrentQueue<MetadataHttpRequest>();
            var h = await CreateAdapterHarnessAsync(tempDir, req =>
            {
                recorded.Enqueue(req);
                return "{}";
            });

            var keyGatedKeys = new[] { "discogs", "comicvine", "acoustid", "anidb", "trakt", "hardcover" };
            var zeroKeyKeys = new[] { "dnb", "bookbrainz", "tvdb", "tmdb", "openlibrary", "musicbrainz", "anilist", "mangadex" };

            var statusesBefore = await h.Federation.ListProviderStatusesAsync();
            foreach (var key in keyGatedKeys)
            {
                var status = Assert.Single(statusesBefore, s => s.Key == key);
                Assert.True(status.RequiresApiKey);
                Assert.Equal(MetadataProviderConfigurationStates.NotConfigured, status.ConfigurationStatus);

                var descriptor = Assert.Single(h.Federation.ListBuiltInProviders(), d => d.Key == key);
                Assert.True(descriptor.RequiresApiKey);
                Assert.Equal(MetadataProviderConfigurationStates.NotConfigured, descriptor.ConfigurationStatus);
            }

            foreach (var key in zeroKeyKeys)
            {
                var status = Assert.Single(statusesBefore, s => s.Key == key);
                Assert.False(status.RequiresApiKey);
                Assert.Equal(MetadataProviderConfigurationStates.Ready, status.ConfigurationStatus);
            }

            // Searching unconfigured key-gated providers must make 0 HTTP requests and return empty
            Assert.Empty(await h.Federation.SearchWaterfallAsync("Pink Floyd", MetadataVerticals.Music, ["discogs", "acoustid"]));
            Assert.Empty(await h.Federation.SearchWaterfallAsync("Batman", MetadataVerticals.Comics, ["comicvine"]));
            Assert.Empty(await h.Federation.SearchWaterfallAsync("Cowboy Bebop", MetadataVerticals.Anime, ["anidb"]));
            Assert.Empty(await h.Federation.SearchWaterfallAsync("Severance", MetadataVerticals.Tv, ["trakt"]));
            Assert.Empty(await h.Federation.SearchWaterfallAsync("Dune", MetadataVerticals.Ebooks, ["hardcover"]));
            Assert.Empty(recorded);

            // Configure API keys / client credentials and verify transition to "ready"
            foreach (var key in keyGatedKeys)
            {
                await h.Settings.SetAsync($"metadata.{key}.api_key", $"secret-{key}-token", isSecret: true);
            }

            var statusesAfter = await h.Federation.ListProviderStatusesAsync();
            foreach (var key in keyGatedKeys)
            {
                var status = Assert.Single(statusesAfter, s => s.Key == key);
                Assert.Equal(MetadataProviderConfigurationStates.Ready, status.ConfigurationStatus);

                var descriptor = Assert.Single(h.Federation.ListBuiltInProviders(), d => d.Key == key);
                Assert.Equal(MetadataProviderConfigurationStates.Ready, descriptor.ConfigurationStatus);
            }
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Dnb_SruXmlSearchAndLookup_WorkWithoutApiKey()
    {
        var tempDir = CreateTempDir();
        try
        {
            var requests = new List<MetadataHttpRequest>();
            const string dnbSruXml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <zs:searchRetrieveResponse xmlns:zs="http://www.loc.gov/zing/srw/"
                                           xmlns:oai_dc="http://www.openarchives.org/OAI/2.0/oai_dc/"
                                           xmlns:dc="http://purl.org/dc/elements/1.1/">
                  <zs:version>1.1</zs:version>
                  <zs:numberOfRecords>1</zs:numberOfRecords>
                  <zs:records>
                    <zs:record>
                      <zs:recordSchema>oai_dc</zs:recordSchema>
                      <zs:recordPacking>xml</zs:recordPacking>
                      <zs:recordData>
                        <oai_dc:dc>
                          <dc:title>Der Prozess / Franz Kafka</dc:title>
                          <dc:creator>Kafka, Franz [Verfasser]</dc:creator>
                          <dc:date>2019</dc:date>
                          <dc:language>ger</dc:language>
                          <dc:identifier>http://d-nb.info/118559234X</dc:identifier>
                          <dc:identifier>978-3-15-009876-9</dc:identifier>
                          <dc:subject>Belletristik</dc:subject>
                          <dc:description>Klassischer Roman in neuer Studienausgabe.</dc:description>
                        </oai_dc:dc>
                      </zs:recordData>
                    </zs:record>
                  </zs:records>
                </zs:searchRetrieveResponse>
                """;

            var h = await CreateAdapterHarnessAsync(tempDir, req =>
            {
                requests.Add(req);
                return dnbSruXml;
            });

            var searchResults = await h.Federation.SearchWaterfallAsync("Der Prozess", MetadataVerticals.Ebooks, ["dnb"]);
            var hit = Assert.Single(searchResults);
            Assert.Contains(
                requests,
                r => r.Url == "https://services.dnb.de/sru/dnb?version=1.1&operation=searchRetrieve&query=Der+Prozess&maximumRecords=15&recordSchema=oai_dc");
            Assert.Equal("Der Prozess", hit.Title);
            Assert.Equal("118559234X", hit.ProviderItemId);
            Assert.Equal("118559234X", hit.ExternalIds["dnb"]);
            Assert.Equal("9783150098769", hit.ExternalIds["isbn13"]);
            Assert.Equal("de", hit.OriginalLanguage);
            Assert.Equal(2019, hit.Year);
            Assert.NotNull(hit.Genres);
            Assert.Contains("Belletristik", hit.Genres!);

            var lookupHit = await h.LookupAsync("dnb", "118559234X", MetadataVerticals.Ebooks);
            Assert.NotNull(lookupHit);
            Assert.Contains(
                requests,
                r => r.Url == "https://services.dnb.de/sru/dnb?version=1.1&operation=searchRetrieve&query=idn%3d118559234X&maximumRecords=1&recordSchema=oai_dc" ||
                     r.Url == "https://services.dnb.de/sru/dnb?version=1.1&operation=searchRetrieve&query=idn%3D118559234X&maximumRecords=1&recordSchema=oai_dc");
            Assert.Equal("Der Prozess", lookupHit!.Title);
            Assert.Equal("9783150098769", lookupHit.ExternalIds["isbn13"]);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task BookBrainz_WorkSearchAndLookup_ParseAliasesLanguagesAndIdentifiers()
    {
        var tempDir = CreateTempDir();
        try
        {
            var requests = new List<MetadataHttpRequest>();
            const string bbJson = """
                {
                  "results": [
                    {
                      "bbid": "a7b8c9d0-1234-4567-89ab-cdef01234567",
                      "defaultAlias": {
                        "name": "The Left Hand of Darkness",
                        "language": "eng"
                      },
                      "aliasSet": {
                        "aliases": [
                          { "name": "The Left Hand of Darkness", "language": "eng" },
                          { "name": "Winterplanet", "language": "deu" }
                        ]
                      },
                      "releaseYear": 1969,
                      "disambiguation": { "comment": "Ursula K. Le Guin Hainish Cycle novel" },
                      "identifierSet": {
                        "identifiers": [
                          { "value": "9780441478125", "type": { "label": "ISBN-13" } },
                          { "value": "OL59864W", "type": { "label": "OpenLibrary Work ID" } }
                        ]
                      }
                    }
                  ]
                }
                """;

            var h = await CreateAdapterHarnessAsync(tempDir, req =>
            {
                requests.Add(req);
                return bbJson;
            });

            var hits = await h.Federation.SearchWaterfallAsync("Left Hand of Darkness", MetadataVerticals.Ebooks, ["bookbrainz"]);
            var hit = Assert.Single(hits);
            Assert.Contains(requests, r => r.Url == "https://api.bookbrainz.org/1/search?q=Left+Hand+of+Darkness&collection=work");
            Assert.Equal("The Left Hand of Darkness", hit.Title);
            Assert.Contains("Winterplanet", hit.AltTitles);
            Assert.Equal(1969, hit.Year);
            Assert.Equal("en", hit.OriginalLanguage);
            Assert.Equal("a7b8c9d0-1234-4567-89ab-cdef01234567", hit.ExternalIds["bookbrainz"]);
            Assert.Equal("9780441478125", hit.ExternalIds["isbn13"]);
            Assert.Equal("OL59864W", hit.ExternalIds["openlibrary"]);

            var lookedUp = await h.LookupAsync(
                "bookbrainz",
                "a7b8c9d0-1234-4567-89ab-cdef01234567",
                MetadataVerticals.Ebooks);
            Assert.NotNull(lookedUp);
            Assert.Contains(requests, r => r.Url == "https://api.bookbrainz.org/1/work/a7b8c9d0-1234-4567-89ab-cdef01234567");
            Assert.Equal("The Left Hand of Darkness", lookedUp!.Title);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Discogs_SearchAndMasterReleaseLookup_SendTokenHeaderAndParseTracklist()
    {
        var tempDir = CreateTempDir();
        try
        {
            var requests = new List<MetadataHttpRequest>();
            var h = await CreateAdapterHarnessAsync(tempDir, req =>
            {
                requests.Add(req);
                if (req.Url.Contains("/database/search", StringComparison.Ordinal))
                {
                    return """
                        {
                          "results": [
                            {
                              "id": 10362,
                              "master_id": 10362,
                              "title": "Pink Floyd (2) - The Dark Side Of The Moon",
                              "year": "1973",
                              "genre": ["Rock"],
                              "style": ["Prog Rock", "Psychedelic Rock"],
                              "cover_image": "https://img.discogs.com/dsotm.jpg"
                            }
                          ]
                        }
                        """;
                }

                return """
                    {
                      "id": 10362,
                      "main_release": 249504,
                      "title": "The Dark Side Of The Moon",
                      "year": 1973,
                      "artists": [{ "name": "Pink Floyd (2)" }],
                      "genres": ["Rock"],
                      "styles": ["Prog Rock"],
                      "images": [{ "type": "primary", "uri": "https://img.discogs.com/dsotm-master.jpg" }],
                      "notes": "Recorded at Abbey Road Studios.",
                      "tracklist": [
                        { "position": "A1", "type_": "track", "title": "Speak To Me", "duration": "1:30" },
                        { "position": "A2", "type_": "track", "title": "Breathe", "duration": "2:43" },
                        { "position": "A3", "type_": "track", "title": "Time", "duration": "6:53" }
                      ]
                    }
                    """;
            });

            await h.Settings.SetAsync("metadata.discogs.api_key", "my-discogs-pat", isSecret: true);

            var searchHits = await h.Federation.SearchWaterfallAsync("Dark Side of the Moon", MetadataVerticals.Music, ["discogs"]);
            var hit = Assert.Single(searchHits);
            var searchReq = Assert.Single(requests);
            Assert.Equal("https://api.discogs.com/database/search?q=Dark+Side+of+the+Moon&type=master", searchReq.Url);
            Assert.Equal("Discogs token=my-discogs-pat", searchReq.Headers!["Authorization"]);
            Assert.Equal("Pink Floyd — The Dark Side Of The Moon", hit.Title);
            Assert.Equal(1973, hit.Year);
            Assert.Equal("10362", hit.ExternalIds["discogs"]);
            Assert.NotNull(hit.Genres);
            Assert.Contains("Prog Rock", hit.Genres!);

            var masterLookup = await h.LookupAsync("discogs", "10362", MetadataVerticals.Music);
            Assert.NotNull(masterLookup);
            Assert.Contains(requests, r => r.Url == "https://api.discogs.com/masters/10362");
            Assert.Equal(3, masterLookup!.Containers[0].Units.Count);
            Assert.Equal("Time", masterLookup.Containers[0].Units[2].Title);

            var releaseLookup = await h.LookupAsync("discogs", "release:249504", MetadataVerticals.Music);
            Assert.NotNull(releaseLookup);
            Assert.Contains(requests, r => r.Url == "https://api.discogs.com/releases/249504");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task ComicVine_VolumeSearchAndLookup_ParseVolumeAndIssues()
    {
        var tempDir = CreateTempDir();
        try
        {
            var requests = new List<MetadataHttpRequest>();
            var h = await CreateAdapterHarnessAsync(tempDir, req =>
            {
                requests.Add(req);
                if (req.Url.Contains("/search/", StringComparison.Ordinal))
                {
                    return """
                        {
                          "error": "OK",
                          "status_code": 1,
                          "results": [
                            {
                              "id": 796,
                              "name": "Batman",
                              "start_year": "1940",
                              "count_of_issues": 5,
                              "aliases": "The Batman\nBatman Vol. 1",
                              "deck": "The Dark Knight's flagship title.",
                              "publisher": { "name": "DC Comics" },
                              "image": { "super_url": "https://comicvine.gamespot.com/batman.jpg" }
                            }
                          ]
                        }
                        """;
                }

                return """
                    {
                      "error": "OK",
                      "status_code": 1,
                      "results": {
                        "id": 796,
                        "name": "Batman",
                        "start_year": "1940",
                        "aliases": "The Batman\nBatman Vol. 1",
                        "description": "<p>Full volume history of <b>Batman</b>.</p>",
                        "publisher": { "name": "DC Comics" },
                        "image": { "original_url": "https://comicvine.gamespot.com/batman-orig.jpg" },
                        "issues": [
                          { "id": 1001, "issue_number": "1", "name": "The Legend of the Batman", "cover_date": "1940-04-01" },
                          { "id": 1002, "issue_number": "2", "name": "The Joker Returns", "cover_date": "1940-07-01" }
                        ]
                      }
                    }
                    """;
            });

            await h.Settings.SetAsync("metadata.comicvine.api_key", "cv-key-123", isSecret: true);

            var hits = await h.Federation.SearchWaterfallAsync("Batman", MetadataVerticals.Comics, ["comicvine"]);
            var hit = Assert.Single(hits);
            Assert.Contains(
                requests,
                r => r.Url == "https://comicvine.gamespot.com/api/search/?api_key=cv-key-123&format=json&resources=volume&query=Batman");
            Assert.Equal("Batman", hit.Title);
            Assert.Contains("The Batman", hit.AltTitles);
            Assert.Equal(1940, hit.Year);
            Assert.Equal("796", hit.ExternalIds["comicvine"]);
            Assert.Equal("4050-796", hit.ExternalIds["comicvine_volume"]);
            Assert.NotNull(hit.Genres);
            Assert.Contains("DC Comics", hit.Genres!);
            Assert.Equal(5, hit.Containers[0].Units.Count);

            var lookedUp = await h.LookupAsync("comicvine", "796", MetadataVerticals.Comics);
            Assert.NotNull(lookedUp);
            Assert.Contains(
                requests,
                r => r.Url == "https://comicvine.gamespot.com/api/volume/4050-796/?api_key=cv-key-123&format=json");
            Assert.Contains("Full volume history of Batman", lookedUp!.Overview);
            Assert.Equal(2, lookedUp.Containers[0].Units.Count);
            Assert.Equal("The Legend of the Batman", lookedUp.Containers[0].Units[0].Title);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task AniDb_HttpApiXmlParser_CrosswalkSearch_AndPoliteRateLimit_Work()
    {
        Assert.True(MetadataHttpGateway.ResolveMinIntervalForHost("api.anidb.net") >= TimeSpan.FromMilliseconds(2100));

        var tempDir = CreateTempDir();
        try
        {
            var requests = new List<MetadataHttpRequest>();
            const string aniDbXml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <anime id="1" restricted="false">
                  <type>TV Series</type>
                  <episodecount>26</episodecount>
                  <startdate>1998-04-03</startdate>
                  <enddate>1999-04-24</enddate>
                  <titles>
                    <title xml:lang="ja" type="main">Cowboy Bebop</title>
                    <title xml:lang="en" type="official">Cowboy Bebop</title>
                    <title xml:lang="ja" type="official">カウボーイビバップ</title>
                    <title xml:lang="en" type="synonym">Bebop</title>
                  </titles>
                  <resources>
                    <resource type="2">
                      <externalentity>
                        <identifier>1</identifier>
                      </externalentity>
                    </resource>
                  </resources>
                  <description>In 2071, http://anidb.net/ch1 [Spike Spiegel] and Jet Black hunt bounties across the solar system.</description>
                  <picture>12345.jpg</picture>
                  <tags>
                    <tag id="10" weight="600"><name>space western</name></tag>
                  </tags>
                  <episodes>
                    <episode id="101">
                      <epno type="1">1</epno>
                      <airdate>1998-04-03</airdate>
                      <title xml:lang="en">Asteroid Blues</title>
                      <title xml:lang="x-jat">Asteroid Blues</title>
                    </episode>
                    <episode id="102">
                      <epno type="1">2</epno>
                      <airdate>1998-04-10</airdate>
                      <title xml:lang="en">Stray Dog Strut</title>
                    </episode>
                    <episode id="199">
                      <epno type="2">S1</epno>
                      <airdate>1998-06-26</airdate>
                      <title xml:lang="en">Session XX: Mish-Mash Blues</title>
                    </episode>
                  </episodes>
                </anime>
                """;

            var h = await CreateAdapterHarnessAsync(tempDir, req =>
            {
                requests.Add(req);
                return aniDbXml;
            });

            await h.Settings.SetAsync("metadata.anidb.api_key", "managerrclient:1", isSecret: true);

            // Title search resolves "Cowboy Bebop" via crosswalk to aid=1 and queries AniDB HTTP API safely
            var searchHits = await h.Federation.SearchWaterfallAsync("Cowboy Bebop", MetadataVerticals.Anime, ["anidb"]);
            var hit = Assert.Single(searchHits);
            Assert.Contains(
                requests,
                r => r.Url == "http://api.anidb.net:9001/httpapi?request=anime&client=managerrclient&clientver=1&protover=1&aid=1");
            Assert.Equal("Cowboy Bebop", hit.Title);
            Assert.Contains("Bebop", hit.AltTitles);
            Assert.Equal(1998, hit.Year);
            Assert.Equal("ended", hit.Status);
            Assert.Equal("1", hit.ExternalIds["anidb"]);
            Assert.Equal("1", hit.ExternalIds["mal"]);
            Assert.Contains("Spike Spiegel", hit.Overview);
            Assert.DoesNotContain("http://anidb.net", hit.Overview);
            Assert.Equal("https://cdn-eu.anidb.net/images/main/12345.jpg", hit.PosterUrl);
            Assert.Equal(2, hit.Containers[0].Units.Count);
            Assert.Equal("Asteroid Blues", hit.Containers[0].Units[0].Title);
            Assert.Equal("Stray Dog Strut", hit.Containers[0].Units[1].Title);

            // Direct lookup by aid
            var lookedUp = await h.LookupAsync("anidb", "1", MetadataVerticals.Anime);
            Assert.NotNull(lookedUp);
            Assert.Equal("Cowboy Bebop", lookedUp!.Title);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task AcoustId_TrackUuidAndFingerprintLookup_WorkAndRejectBogusTextSearch()
    {
        var tempDir = CreateTempDir();
        try
        {
            var requests = new List<MetadataHttpRequest>();
            const string acoustIdJson = """
                {
                  "status": "ok",
                  "results": [
                    {
                      "id": "e9348665-9614-4d1a-898a-8c3e18f7822f",
                      "score": 0.99,
                      "recordings": [
                        {
                          "id": "b10bbbfc-cf9e-42e0-be17-e2c3e1d2600d",
                          "title": "Time",
                          "duration": 413,
                          "artists": [{ "name": "Pink Floyd" }],
                          "releasegroups": [
                            {
                              "id": "f5093c06-23e3-404f-aeaa-40f72885ee3a",
                              "type": "Album",
                              "title": "The Dark Side of the Moon",
                              "artists": [{ "name": "Pink Floyd" }]
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
                """;

            var h = await CreateAdapterHarnessAsync(tempDir, req =>
            {
                requests.Add(req);
                return acoustIdJson;
            });

            await h.Settings.SetAsync("metadata.acoustid.api_key", "acoustid-client-key", isSecret: true);

            // Plain text search must be rejected without sending a bogus request to AcoustID
            var bogusTextHits = await h.Federation.SearchWaterfallAsync("Pink Floyd Time", MetadataVerticals.Music, ["acoustid"]);
            Assert.Empty(bogusTextHits);
            Assert.Empty(requests);

            // Fingerprint query (fp:<duration>:<fingerprint>) must call /v2/lookup with duration & fingerprint
            var fpHits = await h.Federation.SearchWaterfallAsync("fp:413:AQADtEmSJCSZ", MetadataVerticals.Music, ["acoustid"]);
            var fpHit = Assert.Single(fpHits);
            Assert.Contains(
                requests,
                r => r.Url == "https://api.acoustid.org/v2/lookup?client=acoustid-client-key&meta=recordings+releasegroups&duration=413&fingerprint=AQADtEmSJCSZ");
            Assert.Equal("Pink Floyd — The Dark Side of the Moon", fpHit.Title);
            Assert.Equal("e9348665-9614-4d1a-898a-8c3e18f7822f", fpHit.ExternalIds["acoustid"]);
            Assert.Equal("f5093c06-23e3-404f-aeaa-40f72885ee3a", fpHit.ExternalIds["musicbrainz"]);
            Assert.Equal("b10bbbfc-cf9e-42e0-be17-e2c3e1d2600d", fpHit.ExternalIds["musicbrainz_recording"]);

            // UUID trackid lookup
            var uuidHit = await h.LookupAsync(
                "acoustid",
                "e9348665-9614-4d1a-898a-8c3e18f7822f",
                MetadataVerticals.Music);
            Assert.NotNull(uuidHit);
            Assert.Contains(
                requests,
                r => r.Url == "https://api.acoustid.org/v2/lookup?client=acoustid-client-key&meta=recordings+releasegroups&trackid=e9348665-9614-4d1a-898a-8c3e18f7822f");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Trakt_And_Hardcover_Lookups_UseRealEndpointsInsteadOfDummyLookupUrls()
    {
        var tempDir = CreateTempDir();
        try
        {
            var requests = new List<MetadataHttpRequest>();
            var h = await CreateAdapterHarnessAsync(tempDir, req =>
            {
                requests.Add(req);
                if (req.Url.Contains("api.trakt.tv/shows/", StringComparison.Ordinal))
                {
                    return """
                        {
                          "title": "Severance",
                          "year": 2022,
                          "ids": {
                            "trakt": 153999,
                            "slug": "severance",
                            "tvdb": 371980,
                            "imdb": "tt11280740",
                            "tmdb": 95396
                          },
                          "overview": "Mark leads a team of office workers.",
                          "status": "returning series",
                          "language": "en",
                          "genres": ["drama", "sci-fi"]
                        }
                        """;
                }

                if (req.Url.Contains("api.trakt.tv/movies/", StringComparison.Ordinal))
                {
                    return """
                        {
                          "title": "Dune: Part Two",
                          "year": 2024,
                          "ids": {
                            "trakt": 654321,
                            "slug": "dune-part-two-2024",
                            "imdb": "tt15239678",
                            "tmdb": 693134
                          },
                          "overview": "Paul Atreides unites with Chani.",
                          "status": "released",
                          "language": "en"
                        }
                        """;
                }

                if (req.Url.Contains("api.hardcover.app/v1/graphql", StringComparison.Ordinal))
                {
                    return """
                        {
                          "data": {
                            "books_by_pk": {
                              "id": 4242,
                              "slug": "project-hail-mary",
                              "title": "Project Hail Mary",
                              "release_year": 2021,
                              "description": "Ryland Grace is the sole survivor on a desperate mission.",
                              "image": { "url": "https://assets.hardcover.app/phm.jpg" },
                              "contributions": [{ "author": { "name": "Andy Weir" } }]
                            }
                          }
                        }
                        """;
                }

                return null;
            });

            await h.Settings.SetAsync("metadata.trakt.api_key", "trakt-client-id", isSecret: true);
            await h.Settings.SetAsync("metadata.hardcover.api_key", "hc-bearer-token", isSecret: true);

            var traktShow = await h.LookupAsync("trakt", "severance", MetadataVerticals.Tv);
            Assert.NotNull(traktShow);
            Assert.Contains(requests, r => r.Url == "https://api.trakt.tv/shows/severance?extended=full" && r.Headers!["trakt-api-key"] == "trakt-client-id");
            Assert.Equal("Severance", traktShow!.Title);
            Assert.Equal("371980", traktShow.ExternalIds["tvdb"]);
            Assert.Equal("tt11280740", traktShow.ExternalIds["imdb"]);

            var traktMovie = await h.LookupAsync("trakt", "dune-part-two-2024", MetadataVerticals.Movies);
            Assert.NotNull(traktMovie);
            Assert.Contains(requests, r => r.Url == "https://api.trakt.tv/movies/dune-part-two-2024?extended=full");
            Assert.Equal("Dune: Part Two", traktMovie!.Title);
            Assert.Equal("693134", traktMovie.ExternalIds["tmdb"]);

            var hcBook = await h.LookupAsync("hardcover", "4242", MetadataVerticals.Ebooks);
            Assert.NotNull(hcBook);
            var hcReq = Assert.Single(requests, r => r.Url == "https://api.hardcover.app/v1/graphql");
            Assert.Equal(HttpMethod.Post, hcReq.Method);
            Assert.Equal("Bearer hc-bearer-token", hcReq.Headers!["Authorization"]);
            Assert.Contains("books_by_pk", hcReq.JsonBody);
            Assert.Equal("Project Hail Mary", hcBook!.Title);
            Assert.Equal(2021, hcBook.Year);
            Assert.Equal("https://assets.hardcover.app/phm.jpg", hcBook.PosterUrl);

            Assert.DoesNotContain(requests, r => r.Url.Contains("/lookup/", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private sealed record AdapterHarness(
        IAppSettingsService Settings,
        IMetadataFederationService Federation,
        IReadOnlyList<IMetadataProvider> Providers)
    {
        public Task<MetadataWorkCandidate?> LookupAsync(string providerKey, string externalId, string vertical) =>
            Providers.Single(p => p.Descriptor.Key == providerKey).GetByExternalIdAsync(externalId, vertical);
    }

    private static async Task<AdapterHarness> CreateAdapterHarnessAsync(
        string tempDir,
        Func<MetadataHttpRequest, string?> responder)
    {
        var paths = new AppPaths(tempDir);
        var protector = new EnvelopeSecretProtector(paths, "unit-test-master-key-32bytes-ok!");
        var dbFactory = new SqliteDbFactory(paths);
        var migrator = new SchemaMigrator(dbFactory);
        await migrator.MigrateAllAsync();

        var cache = new HybridSqliteCache(dbFactory, new MemoryCache(new MemoryCacheOptions()));
        var settings = new AppSettingsService(dbFactory, protector);
        var crosswalk = new ExternalIdCrosswalkService(dbFactory);
        await crosswalk.EnsureSeededAsync();

        var sidecars = new SidecarMetadataService();
        var httpGateway = new RecordingMetadataHttpGateway(responder);
        var webhooks = new CustomHttpMetadataProviderService(dbFactory, protector, httpGateway);
        var providers = BuiltInMetadataProviderCatalog.CreateAll(httpGateway, settings, sidecars);
        var federation = new MetadataFederationService(providers, webhooks, crosswalk, cache);

        return new AdapterHarness(settings, federation, providers);
    }

    private sealed class RecordingMetadataHttpGateway(Func<MetadataHttpRequest, string?> responder) : IMetadataHttpGateway
    {
        public Task<string?> SendAsync(MetadataHttpRequest request, CancellationToken ct = default) =>
            Task.FromResult(responder(request));
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"managerr-phase3-adapters-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
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
            // Ignore cleanup errors
        }
    }
}
