using System.Collections.Concurrent;
using Managerr.Catalog;
using Managerr.Common.Crypto;
using Managerr.Common.Paths;
using Managerr.Datastore;
using Managerr.Datastore.Migrations;
using Managerr.Datastore.Services;
using Managerr.Metadata;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Managerr.Phase3.Tests;

public sealed class LocalizedMetadataTranslationsTests
{
    [Fact]
    public async Task SkyHookMovie_HarvestsTranslations_AndOrdersTier1BeforeTier2()
    {
        var tempDir = CreateTempDir();
        try
        {
            var requests = new ConcurrentQueue<MetadataHttpRequest>();
            var h = await CreateHarnessAsync(tempDir, req =>
            {
                requests.Enqueue(req);
                if (req.Url.Contains("api.radarr.video/v1/movie/990001", StringComparison.Ordinal))
                {
                    return """
                        {
                          "TmdbId": 990001,
                          "ImdbId": "tt9900001",
                          "Title": "Solar Drifters",
                          "OriginalTitle": "Taiyou no Hyouryuusha",
                          "OriginalLanguage": { "Name": "Japanese" },
                          "Year": 2025,
                          "Overview": "A voyage across orbital currents.",
                          "AlternativeTitles": [
                            { "Title": "Drifters of the Sun" }
                          ],
                          "Translations": [
                            { "Title": "Errantes del Sol", "Language": "es-ES" },
                            { "Title": "Deriveurs Solaires", "Language": "fr-FR" },
                            { "Title": "Sonnenwanderer", "Language": "de-DE" },
                            { "Title": "Taiyou no Hyouryuusha", "Language": "ja-JP" }
                          ]
                        }
                        """;
                }

                return null;
            });

            var candidate = await h.LookupAsync("tmdb", "990001", MetadataVerticals.Movies);
            Assert.NotNull(candidate);
            Assert.Contains(requests, r => r.Url == "https://api.radarr.video/v1/movie/990001");
            Assert.Equal("Solar Drifters", candidate!.Title);
            Assert.DoesNotContain("Solar Drifters", candidate.AltTitles, StringComparer.OrdinalIgnoreCase);

            var idxOrig = IndexOfIgnoreCase(candidate.AltTitles, "Taiyou no Hyouryuusha");
            var idxExplicit = IndexOfIgnoreCase(candidate.AltTitles, "Drifters of the Sun");
            var idxDe = IndexOfIgnoreCase(candidate.AltTitles, "Sonnenwanderer");
            var idxEs = IndexOfIgnoreCase(candidate.AltTitles, "Errantes del Sol");
            var idxFr = IndexOfIgnoreCase(candidate.AltTitles, "Deriveurs Solaires");

            Assert.True(idxOrig >= 0 && idxExplicit >= 0 && idxDe >= 0 && idxEs >= 0 && idxFr >= 0);
            Assert.True(idxOrig < idxEs && idxExplicit < idxEs && idxDe < idxEs,
                $"Expected Tier-1 titles before Tier-2; got [{string.Join(", ", candidate.AltTitles)}]");
            Assert.True(idxDe < idxFr);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task SkyHookTv_HarvestsTranslations_AndPreservesZeroKeyUrl()
    {
        var tempDir = CreateTempDir();
        try
        {
            var requests = new ConcurrentQueue<MetadataHttpRequest>();
            var h = await CreateHarnessAsync(tempDir, req =>
            {
                requests.Enqueue(req);
                if (req.Url.Contains("skyhook.sonarr.tv/v2/tvdb/shows/eng/880001/official", StringComparison.Ordinal))
                {
                    return """
                        {
                          "tvdbId": 880001,
                          "title": "Solar Drifters",
                          "originalLanguage": "ja",
                          "year": 2024,
                          "translations": [
                            { "title": "Errantes del Sol", "language": "spa" },
                            { "title": "Sonnenwanderer", "language": "deu" },
                            { "title": "Solaris Drifter", "language": "jpn" }
                          ]
                        }
                        """;
                }

                return null;
            });

            var candidate = await h.LookupAsync("tvdb", "880001", MetadataVerticals.Tv);
            Assert.NotNull(candidate);
            Assert.Contains(requests, r => r.Url == "https://skyhook.sonarr.tv/v2/tvdb/shows/eng/880001/official");

            var idxDe = IndexOfIgnoreCase(candidate!.AltTitles, "Sonnenwanderer");
            var idxJa = IndexOfIgnoreCase(candidate.AltTitles, "Solaris Drifter");
            var idxEs = IndexOfIgnoreCase(candidate.AltTitles, "Errantes del Sol");

            Assert.True(idxDe >= 0 && idxJa >= 0 && idxEs >= 0);
            Assert.True(idxDe < idxEs && idxJa < idxEs,
                $"Expected Tier-1 (deu + originalLanguage jpn) before spa; got [{string.Join(", ", candidate.AltTitles)}]");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task TmdbV3Lookup_RequestsTranslationsAndExternalIds_AndParsesTvAndMovieFormats()
    {
        var tempDir = CreateTempDir();
        try
        {
            var requests = new ConcurrentQueue<MetadataHttpRequest>();
            var h = await CreateHarnessAsync(tempDir, req =>
            {
                requests.Enqueue(req);
                if (req.Url.Contains("/movie/770001", StringComparison.Ordinal))
                {
                    return """
                        {
                          "id": 770001,
                          "title": "Solar Drifters",
                          "original_title": "Solar Drifters Origin",
                          "original_language": "en",
                          "release_date": "2025-04-10",
                          "external_ids": {
                            "imdb_id": "tt7700001",
                            "tvdb_id": 0
                          },
                          "alternative_titles": {
                            "titles": [
                              { "iso_3166_1": "US", "title": "Solar Drifters Movie" }
                            ]
                          },
                          "translations": {
                            "translations": [
                              { "iso_639_1": "es", "iso_3166_1": "ES", "data": { "title": "Errantes del Sol" } },
                              { "iso_639_1": "de", "iso_3166_1": "DE", "data": { "title": "Sonnenwanderer" } }
                            ]
                          }
                        }
                        """;
                }

                if (req.Url.Contains("/tv/770002", StringComparison.Ordinal))
                {
                    return """
                        {
                          "id": 770002,
                          "name": "Orbital Horizon",
                          "original_name": "Kidou Chihei",
                          "original_language": "ja",
                          "first_air_date": "2024-01-12",
                          "external_ids": {
                            "imdb_id": "tt7700002",
                            "tvdb_id": 554433
                          },
                          "alternative_titles": {
                            "results": [
                              { "iso_3166_1": "DE", "name": "Horizont im Orbit" }
                            ]
                          },
                          "translations": {
                            "translations": [
                              { "iso_639_1": "fr", "iso_3166_1": "FR", "data": { "name": "Horizon Orbital" } },
                              { "iso_639_1": "de", "iso_3166_1": "DE", "data": { "name": "Orbitaler Horizont" } }
                            ]
                          }
                        }
                        """;
                }

                return null;
            });

            await h.Settings.SetAsync("metadata.tmdb.api_key", "tmdb-test-key", isSecret: true);

            var movie = await h.LookupAsync("tmdb", "770001", MetadataVerticals.Movies);
            Assert.NotNull(movie);
            Assert.Contains(requests, r => r.Url.Contains("append_to_response=alternative_titles,translations,external_ids", StringComparison.Ordinal));
            Assert.Equal("tt7700001", movie!.ExternalIds["imdb"]);
            Assert.True(IndexOfIgnoreCase(movie.AltTitles, "Sonnenwanderer") < IndexOfIgnoreCase(movie.AltTitles, "Errantes del Sol"));
            Assert.Contains("Solar Drifters Movie", movie.AltTitles);

            var tv = await h.LookupAsync("tmdb", "770002", MetadataVerticals.Tv);
            Assert.NotNull(tv);
            Assert.Equal("554433", tv!.ExternalIds["tvdb"]);
            Assert.Equal("tt7700002", tv.ExternalIds["imdb"]);
            Assert.Contains("Horizont im Orbit", tv.AltTitles);
            Assert.True(IndexOfIgnoreCase(tv.AltTitles, "Orbitaler Horizont") < IndexOfIgnoreCase(tv.AltTitles, "Horizon Orbital"));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task TvdbV4DirectLookup_UsesExtendedTranslationsEndpoint_AndParsesNameTranslations()
    {
        var tempDir = CreateTempDir();
        try
        {
            var requests = new ConcurrentQueue<MetadataHttpRequest>();
            var h = await CreateHarnessAsync(tempDir, req =>
            {
                requests.Enqueue(req);
                if (req.Url.Contains("/series/660001/extended?meta=translations", StringComparison.Ordinal))
                {
                    return """
                        {
                          "status": "success",
                          "data": {
                            "id": 660001,
                            "name": "Solar Drifters",
                            "originalLanguage": "eng",
                            "firstAired": "2024-05-01",
                            "aliases": [
                              { "name": "Solar Drifters Chronicles", "language": "eng" }
                            ],
                            "translations": {
                              "nameTranslations": [
                                { "name": "Errantes del Sol", "language": "spa" },
                                { "name": "Sonnenwanderer", "language": "deu" }
                              ]
                            }
                          }
                        }
                        """;
                }

                if (req.Url.Contains("/movies/660002/extended?meta=translations", StringComparison.Ordinal))
                {
                    return """
                        {
                          "status": "success",
                          "data": {
                            "id": 660002,
                            "name": "Lunar Drift",
                            "originalLanguage": "eng",
                            "year": 2025,
                            "nameTranslations": [
                              { "name": "Deriva Lunar", "language": "spa" },
                              { "name": "Monddrift", "language": "ger" }
                            ]
                          }
                        }
                        """;
                }

                return null;
            });

            await h.Settings.SetAsync("metadata.tvdb.api_key", "tvdb-v4-bearer", isSecret: true);

            var series = await h.LookupAsync("tvdb", "660001", MetadataVerticals.Tv);
            Assert.NotNull(series);
            Assert.Contains(requests, r => r.Url == "https://api4.thetvdb.com/v4/series/660001/extended?meta=translations" && r.Headers!["Authorization"] == "Bearer tvdb-v4-bearer");
            Assert.Contains("Solar Drifters Chronicles", series!.AltTitles);
            Assert.True(IndexOfIgnoreCase(series.AltTitles, "Sonnenwanderer") < IndexOfIgnoreCase(series.AltTitles, "Errantes del Sol"));

            var movie = await h.LookupAsync("tvdb", "660002", MetadataVerticals.Movies);
            Assert.NotNull(movie);
            Assert.Contains(requests, r => r.Url == "https://api4.thetvdb.com/v4/movies/660002/extended?meta=translations");
            Assert.True(IndexOfIgnoreCase(movie!.AltTitles, "Monddrift") < IndexOfIgnoreCase(movie.AltTitles, "Deriva Lunar"));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task AddWorkAsync_And_RefreshWorkMetadataAsync_PersistAndRespectAltTitlesLocks()
    {
        var tempDir = CreateTempDir();
        try
        {
            var germanTitle = "Sonnenwanderer";
            var h = await CreateHarnessAsync(tempDir, req =>
            {
                if (req.Url.Contains("api.radarr.video/v1/movie/990010", StringComparison.Ordinal))
                {
                    return $$"""
                        {
                          "TmdbId": 990010,
                          "ImdbId": "tt9900010",
                          "Title": "Solar Drifters",
                          "OriginalTitle": "Taiyou no Hyouryuusha",
                          "OriginalLanguage": "ja",
                          "Year": 2025,
                          "Translations": [
                            { "Title": "{{germanTitle}}", "Language": "de-DE" },
                            { "Title": "Errantes del Sol", "Language": "es-ES" }
                          ]
                        }
                        """;
                }

                if (req.Url.Contains("skyhook.sonarr.tv/v2/tvdb/shows/eng/880010/official", StringComparison.Ordinal))
                {
                    return """
                        {
                          "tvdbId": 880010,
                          "title": "Orbital Horizon",
                          "originalLanguage": "en",
                          "year": 2024,
                          "translations": [
                            { "title": "Orbitaler Horizont", "language": "deu" },
                            { "title": "Horizonte Orbital", "language": "spa" }
                          ],
                          "episodes": [
                            { "seasonNumber": 1, "episodeNumber": 1, "title": "Launch", "airDate": "2024-01-01" }
                          ]
                        }
                        """;
                }

                return null;
            });

            var movieLib = await h.Catalog.CreateLibraryAsync(new CreateLibraryRequest("Movies", "movies", Path.Combine(tempDir, "movies")));
            var tvLib = await h.Catalog.CreateLibraryAsync(new CreateLibraryRequest("Series", "tv", Path.Combine(tempDir, "tv")));

            // Movie with PosterUrl already populated and empty AltTitles must still hydrate AltTitles & FTS!
            var createdMovie = await h.Catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: movieLib.Id,
                Title: "Solar Drifters",
                WorkKind: "movie",
                Year: 2025,
                PosterUrl: "https://image.example/poster.jpg",
                ExternalIds: new Dictionary<string, string> { ["tmdb"] = "990010" }));

            Assert.Contains("Sonnenwanderer", createdMovie.Work.AltTitles);
            Assert.Contains("Errantes del Sol", createdMovie.Work.AltTitles);
            Assert.Equal("tt9900010", createdMovie.Work.ExternalIds["imdb"]);

            var ftsHits = await h.Fts.SearchAsync("Sonnenwanderer");
            Assert.Contains(ftsHits, hit => hit.Title == "Solar Drifters");

            // Series added with SkipUpstreamHydration=true (like migration) heals AltTitles on RefreshWorkMetadataAsync
            var migratedSeries = await h.Catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: "Orbital Horizon",
                WorkKind: "tv",
                Year: 2024,
                Status: "ended",
                ExternalIds: new Dictionary<string, string> { ["tvdb"] = "880010" },
                SkipUpstreamHydration: true));

            Assert.Empty(migratedSeries.Work.AltTitles);

            var refreshedSeries = await h.Catalog.RefreshWorkMetadataAsync(migratedSeries.Work.Id);
            Assert.Contains("Orbitaler Horizont", refreshedSeries.Work.AltTitles);
            var seriesFtsHits = await h.Fts.SearchAsync("Orbitaler Horizont");
            Assert.Contains(seriesFtsHits, hit => hit.Title == "Orbital Horizon");

            // Locking altTitles prevents RefreshWorkMetadataAsync from overwriting custom AltTitles
            var lockedMovie = await h.Catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: movieLib.Id,
                Title: "Solar Drifters Locked",
                WorkKind: "movie",
                Year: 2025,
                AltTitles: ["Custom Alias"],
                LockedFields: ["altTitles"],
                ExternalIds: new Dictionary<string, string> { ["tmdb"] = "990010" },
                SkipUpstreamHydration: true));

            var refreshedLocked = await h.Catalog.RefreshWorkMetadataAsync(lockedMovie.Work.Id);
            Assert.Equal(["Custom Alias"], refreshedLocked.Work.AltTitles);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private static int IndexOfIgnoreCase(IReadOnlyList<string> list, string target)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], target, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private sealed record TestHarness(
        IAppSettingsService Settings,
        PolymorphicCatalogService Catalog,
        MetadataFederationService Federation,
        IReadOnlyList<IMetadataProvider> Providers,
        FtsSearchService Fts)
    {
        public Task<MetadataWorkCandidate?> LookupAsync(string providerKey, string externalId, string vertical) =>
            Providers.Single(p => p.Descriptor.Key == providerKey).GetByExternalIdAsync(externalId, vertical);
    }

    private static async Task<TestHarness> CreateHarnessAsync(
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
        var fts = new FtsSearchService(dbFactory);
        var crosswalk = new ExternalIdCrosswalkService(dbFactory);
        await crosswalk.EnsureSeededAsync();

        var sidecars = new SidecarMetadataService();
        var gateway = new StubMetadataHttpGateway(responder);
        var webhooks = new CustomHttpMetadataProviderService(dbFactory, protector, gateway);
        var providers = BuiltInMetadataProviderCatalog.CreateAll(gateway, settings, sidecars);
        var federation = new MetadataFederationService(providers, webhooks, crosswalk, cache);
        var catalog = new PolymorphicCatalogService(dbFactory, fts, federation, crosswalk);

        return new TestHarness(settings, catalog, federation, providers, fts);
    }

    private sealed class StubMetadataHttpGateway(Func<MetadataHttpRequest, string?> responder) : IMetadataHttpGateway
    {
        public Task<string?> SendAsync(MetadataHttpRequest request, CancellationToken ct = default) =>
            Task.FromResult(responder(request));
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"managerr-phase3-translations-{Guid.NewGuid():N}");
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
