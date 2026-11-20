using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Managerr.Catalog;
using Managerr.Metadata;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Managerr.Phase6.Tests;

public sealed class TmdbCollapsedAnimeSeasonAlignmentTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"managerr-p6-anime-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _http = null!;
    private string _apiKey = null!;
    private readonly FakeMetadataFederation _metaFed = new();

    public async Task InitializeAsync()
    {
        var tvRoot = Path.Combine(_tempDir, "media", "tv");
        var animeRoot = Path.Combine(_tempDir, "media", "anime");
        Directory.CreateDirectory(tvRoot);
        Directory.CreateDirectory(animeRoot);

        // Seed Metadata Candidates
        // 1. Anime 1 (TMDB single-season collapse: 1 x 60 on TMDB vs 3 x 24 on TVDB)
        var tvdbAnime1 = new MetadataWorkCandidate(
            "tvdb", "1001", "anime", "Bleach: Thousand-Year Blood War", [], "ja", 2022, "continuing",
            "Substitute Soul Reaper Ichigo Kurosaki...", null,
            new Dictionary<string, string> { ["tvdb"] = "1001", ["tmdb"] = "2001" },
            [
                CreateSeason(0, 2, "2022-01-01"),
                CreateSeason(1, 24, "2022-10-11"),
                CreateSeason(2, 24, "2023-07-08"),
                CreateSeason(3, 24, "2024-10-05"),
            ],
            Genres: ["Action", "Anime"]);

        var tmdbAnime1 = new MetadataWorkCandidate(
            "tmdb", "2001", "anime", "Bleach: Thousand-Year Blood War", [], "ja", 2022, "continuing",
            "Substitute Soul Reaper Ichigo Kurosaki...", null,
            new Dictionary<string, string> { ["tmdb"] = "2001" },
            [CreateSeason(1, 60, "2022-10-11")],
            Genres: ["Action", "Anime"]);

        _metaFed.Candidates["tvdb:tvdb:1001"] = tvdbAnime1;
        _metaFed.Candidates["any:tvdb:1001"] = tvdbAnime1;
        _metaFed.Candidates["tmdb:tmdb:2001"] = tmdbAnime1;
        _metaFed.Candidates["any:tmdb:2001"] = tmdbAnime1;

        // 2. Anime 2 (Multi-season on both: 3 x 24 on TVDB and TMDB)
        var tvdbAnime2 = new MetadataWorkCandidate(
            "tvdb", "1002", "anime", "My Hero Academia", [], "ja", 2016, "continuing",
            "A superhero-loving boy without powers...", null,
            new Dictionary<string, string> { ["tvdb"] = "1002", ["tmdb"] = "2002" },
            [
                CreateSeason(1, 24, "2016-04-03"),
                CreateSeason(2, 24, "2017-03-25"),
                CreateSeason(3, 24, "2018-04-07"),
            ],
            Genres: ["Action", "Anime"]);

        var tmdbAnime2 = new MetadataWorkCandidate(
            "tmdb", "2002", "anime", "My Hero Academia", [], "ja", 2016, "continuing",
            "A superhero-loving boy without powers...", null,
            new Dictionary<string, string> { ["tmdb"] = "2002" },
            [
                CreateSeason(1, 24, "2016-04-03"),
                CreateSeason(2, 24, "2017-03-25"),
                CreateSeason(3, 24, "2018-04-07"),
            ],
            Genres: ["Action", "Anime"]);

        _metaFed.Candidates["tvdb:tvdb:1002"] = tvdbAnime2;
        _metaFed.Candidates["any:tvdb:1002"] = tvdbAnime2;
        _metaFed.Candidates["tmdb:tmdb:2002"] = tmdbAnime2;
        _metaFed.Candidates["any:tmdb:2002"] = tmdbAnime2;

        // 3. Standard TV (3 x 24 on TVDB vs 1 x 60 on TMDB)
        var tvdbTv3 = new MetadataWorkCandidate(
            "tvdb", "1003", "tv", "Standard Drama Show", [], "en", 2020, "ended",
            "A standard TV drama...", null,
            new Dictionary<string, string> { ["tvdb"] = "1003", ["tmdb"] = "2003" },
            [
                CreateSeason(1, 24, "2020-01-01"),
                CreateSeason(2, 24, "2021-01-01"),
                CreateSeason(3, 24, "2022-01-01"),
            ],
            Genres: ["Drama"]);

        var tmdbTv3 = new MetadataWorkCandidate(
            "tmdb", "2003", "tv", "Standard Drama Show", [], "en", 2020, "ended",
            "A standard TV drama...", null,
            new Dictionary<string, string> { ["tmdb"] = "2003" },
            [CreateSeason(1, 60, "2020-01-01")],
            Genres: ["Drama"]);

        _metaFed.Candidates["tvdb:tvdb:1003"] = tvdbTv3;
        _metaFed.Candidates["any:tvdb:1003"] = tvdbTv3;
        _metaFed.Candidates["tmdb:tmdb:2003"] = tmdbTv3;
        _metaFed.Candidates["any:tmdb:2003"] = tmdbTv3;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Managerr:ConfigDirectory"] = Path.Combine(_tempDir, "config"),
                ["Managerr:MasterKey"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                ["Managerr:SchedulerEnabled"] = "false",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IMetadataFederationService>();
                s.AddSingleton<IMetadataFederationService>(_metaFed);
            });
        });

        using (var scope = _factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var catalog = sp.GetRequiredService<IPolymorphicCatalogService>();
            await catalog.CreateLibraryAsync(new CreateLibraryRequest("Anime", "anime", animeRoot));
            await catalog.CreateLibraryAsync(new CreateLibraryRequest("TV Shows", "tv", tvRoot));

            var users = sp.GetRequiredService<IPlexUserService>();
            var auth = sp.GetRequiredService<IPlexAuthAndSessionService>();
            var owner = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
                1, "owner-uuid", "plexowner", "owner@plex.local", null, true, true, true, ["1", "2", "3", "4"]));
            var key = await auth.CreateApiKeyAsync(owner.Id, "TestKey", PermissionScopes.AllValidScopes.ToList());
            _apiKey = key.RawTokenOnce;
        }

        _http = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _factory.DisposeAsync();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private static MetadataContainerSeed CreateSeason(int number, int episodes, string? firstAired = "2023-01-01") =>
        new("season", number, number == 0 ? "Specials" : $"Season {number}", firstAired,
            Enumerable.Range(1, episodes)
                .Select(e => new MetadataUnitSeed("episode", e, number == 0 ? null : (int?)((number - 1) * 24 + e), number == 0 ? $"Special {e}" : $"Episode {e}", firstAired))
                .ToList());

    [Fact]
    public async Task AddSeries_TmdbSingleSeasonCollapsedAnime_ExpandsMonitoringAcrossAllTvdbSeasons()
    {
        var resp = await _http.PostAsJsonAsync($"/compat/sonarr/api/v3/series?apikey={_apiKey}", new
        {
            title = "Bleach: Thousand-Year Blood War",
            seriesType = "anime",
            tvdbId = 1001,
            tmdbId = 2001,
            monitored = true,
            rootFolderPath = Path.Combine(_tempDir, "media", "anime"),
            seasons = new[]
            {
                new { seasonNumber = 0, monitored = false },
                new { seasonNumber = 1, monitored = true },
                new { seasonNumber = 2, monitored = false },
                new { seasonNumber = 3, monitored = false },
            },
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(json);

        var seasons = (json["seasons"] as JsonArray)!;
        var s0 = seasons.FirstOrDefault(s => s!["seasonNumber"]?.GetValue<int>() == 0);
        Assert.NotNull(s0);
        Assert.False(s0["monitored"]?.GetValue<bool>());

        var s1 = seasons.FirstOrDefault(s => s!["seasonNumber"]?.GetValue<int>() == 1);
        Assert.NotNull(s1);
        Assert.True(s1["monitored"]?.GetValue<bool>());

        var s2 = seasons.FirstOrDefault(s => s!["seasonNumber"]?.GetValue<int>() == 2);
        Assert.NotNull(s2);
        Assert.True(s2["monitored"]?.GetValue<bool>());

        var s3 = seasons.FirstOrDefault(s => s!["seasonNumber"]?.GetValue<int>() == 3);
        Assert.NotNull(s3);
        Assert.True(s3["monitored"]?.GetValue<bool>());

        // Verify underlying catalog structure preserved TVDB's 3 x 24 container structure
        var seriesId = json["id"]!.GetValue<long>();
        using var scope = _factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
        var tree = await catalog.GetWorkTreeAsync(seriesId);
        Assert.NotNull(tree);
        var regularContainers = tree.Containers.Where(c => c.ContainerNumber > 0).OrderBy(c => c.ContainerNumber).ToList();
        Assert.Equal(3, regularContainers.Count);
        Assert.All(regularContainers, c => Assert.Equal(24, c.Units.Count));
        Assert.All(regularContainers, c => Assert.True(c.Monitored));
    }

    [Fact]
    public async Task AddSeries_TmdbMultiSeasonAnime_KeepsSeasons2And3Unmonitored()
    {
        var resp = await _http.PostAsJsonAsync($"/compat/sonarr/api/v3/series?apikey={_apiKey}", new
        {
            title = "My Hero Academia",
            seriesType = "anime",
            tvdbId = 1002,
            tmdbId = 2002,
            monitored = true,
            rootFolderPath = Path.Combine(_tempDir, "media", "anime"),
            seasons = new[]
            {
                new { seasonNumber = 1, monitored = true },
                new { seasonNumber = 2, monitored = false },
                new { seasonNumber = 3, monitored = false },
            },
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(json);

        var seasons = (json["seasons"] as JsonArray)!;
        var s1 = seasons.FirstOrDefault(s => s!["seasonNumber"]?.GetValue<int>() == 1);
        Assert.True(s1!["monitored"]?.GetValue<bool>());

        var s2 = seasons.FirstOrDefault(s => s!["seasonNumber"]?.GetValue<int>() == 2);
        Assert.False(s2!["monitored"]?.GetValue<bool>());

        var s3 = seasons.FirstOrDefault(s => s!["seasonNumber"]?.GetValue<int>() == 3);
        Assert.False(s3!["monitored"]?.GetValue<bool>());
    }

    [Fact]
    public async Task AddSeries_StandardTv_KeepsSeasons2And3Unmonitored()
    {
        var resp = await _http.PostAsJsonAsync($"/compat/sonarr/api/v3/series?apikey={_apiKey}", new
        {
            title = "Standard Drama Show",
            seriesType = "standard",
            tvdbId = 1003,
            tmdbId = 2003,
            monitored = true,
            rootFolderPath = Path.Combine(_tempDir, "media", "tv"),
            seasons = new[]
            {
                new { seasonNumber = 1, monitored = true },
                new { seasonNumber = 2, monitored = false },
                new { seasonNumber = 3, monitored = false },
            },
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(json);

        var seasons = (json["seasons"] as JsonArray)!;
        var s1 = seasons.FirstOrDefault(s => s!["seasonNumber"]?.GetValue<int>() == 1);
        Assert.True(s1!["monitored"]?.GetValue<bool>());

        var s2 = seasons.FirstOrDefault(s => s!["seasonNumber"]?.GetValue<int>() == 2);
        Assert.False(s2!["monitored"]?.GetValue<bool>());

        var s3 = seasons.FirstOrDefault(s => s!["seasonNumber"]?.GetValue<int>() == 3);
        Assert.False(s3!["monitored"]?.GetValue<bool>());
    }

    [Fact]
    public void BuiltInMetadataProviders_ParsesTmdbSnakeCaseSeasonsAccurately()
    {
        var tmdbJson = """
        {
            "id": 12345,
            "name": "Frieren: Beyond Journey's End",
            "first_air_date": "2023-09-29",
            "overview": "An elf and her journey.",
            "seasons": [
                {
                    "season_number": 0,
                    "episode_count": 0,
                    "name": "Specials",
                    "air_date": null
                },
                {
                    "season_number": 1,
                    "episode_count": 28,
                    "name": "Season 1",
                    "air_date": "2023-09-29"
                }
            ]
        }
        """;

        using var doc = JsonDocument.Parse(tmdbJson);
        var candidate = BuiltInMetadataProviderCatalog.ParseCandidateFromJson(doc.RootElement, providerKey: "tmdb", vertical: MetadataVerticals.Anime);

        Assert.NotNull(candidate);
        Assert.Equal("Frieren: Beyond Journey's End", candidate.Title);
        Assert.Single(candidate.Containers);
        var s1 = candidate.Containers[0];
        Assert.Equal(1, s1.ContainerNumber);
        Assert.Equal("Season 1", s1.Title);
        Assert.Equal("2023-09-29", s1.ReleaseDate);
        Assert.Equal(28, s1.Units.Count);
        Assert.Equal("Episode 1", s1.Units[0].Title);
        Assert.Equal("2023-09-29", s1.Units[0].AirOrReleaseDate);
        Assert.Equal("Episode 28", s1.Units[27].Title);
    }

    private sealed class FakeMetadataFederation : IMetadataFederationService
    {
        public Dictionary<string, MetadataWorkCandidate> Candidates { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<MetadataProviderDescriptor> ListBuiltInProviders(string? vertical = null) => [];
        public IReadOnlyList<string> GetDefaultWaterfall(string vertical) => [];
        public Task<IReadOnlyList<MetadataWorkCandidate>> SearchWaterfallAsync(string query, string vertical, IReadOnlyList<string>? customWaterfall = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataWorkCandidate>>([]);

        public Task<MetadataWorkCandidate?> ResolveWorkAsync(string vertical, string? preferredProvider, IReadOnlyDictionary<string, string> knownExternalIds, IReadOnlyList<string>? customWaterfall = null, CancellationToken ct = default) =>
            ResolveWorkAsync(vertical, preferredProvider, knownExternalIds, customWaterfall, false, ct);

        public Task<MetadataWorkCandidate?> ResolveWorkAsync(string vertical, string? preferredProvider, IReadOnlyDictionary<string, string> knownExternalIds, IReadOnlyList<string>? customWaterfall, bool forceRefresh, CancellationToken ct = default)
        {
            foreach (var (k, v) in knownExternalIds)
            {
                var key = $"{preferredProvider ?? "any"}:{k}:{v}";
                if (Candidates.TryGetValue(key, out var cand)) return Task.FromResult<MetadataWorkCandidate?>(cand);
                var fallbackKey = $"any:{k}:{v}";
                if (Candidates.TryGetValue(fallbackKey, out var cand2)) return Task.FromResult<MetadataWorkCandidate?>(cand2);
            }
            return Task.FromResult<MetadataWorkCandidate?>(null);
        }

        public MergedWorkMetadataFields ApplyFieldMergeWithLocks(MergedWorkMetadataFields current, MetadataWorkCandidate incoming, IReadOnlySet<string> lockedFields) =>
            new(current.Title, incoming.AltTitles, incoming.OriginalLanguage, incoming.Year, incoming.Status, incoming.Overview, incoming.PosterUrl, incoming.ExternalIds);
    }
}
