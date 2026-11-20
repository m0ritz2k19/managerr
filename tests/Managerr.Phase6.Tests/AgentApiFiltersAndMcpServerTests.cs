using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Managerr.Catalog;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Downloaders;
using Managerr.Indexers;
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

public sealed class AgentApiFiltersAndMcpServerTests : IDisposable
{
    private readonly string _testRoot;

    public AgentApiFiltersAndMcpServerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"managerr-agent-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in temp directory
        }
    }

    private WebApplicationFactory<Program> CreateWebFactory(IIndexerHttpGateway? fakeIndexerGateway = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = Path.Combine(_testRoot, "config"),
                    ["Managerr:MasterKey"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                    ["Managerr:SchedulerEnabled"] = "false",
                    ["AllowMissingPrunePackageData"] = "true",
                });
            });

            if (fakeIndexerGateway is not null)
            {
                b.ConfigureServices(s =>
                {
                    s.RemoveAll<IIndexerHttpGateway>();
                    s.AddSingleton<IIndexerHttpGateway>(fakeIndexerGateway);
                });
            }
        });

    private static async Task<(PlexUserSummary Owner, string FullKey)> SeedOwnerAndApiKeyAsync(
        WebApplicationFactory<Program> factory,
        IReadOnlyList<string>? scopes = null)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IPlexUserService>();
        var auth = scope.ServiceProvider.GetRequiredService<IPlexAuthAndSessionService>();

        var owner = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
            PlexUserId: 4242,
            PlexUuid: "agent-test-owner-uuid",
            Username: "agentowner",
            Email: "agentowner@test.local",
            AvatarUrl: null,
            IsServerOwner: true,
            IsHomeUser: true,
            IsActive: true,
            SharedPlexSectionIds: ["1", "2", "3", "4"]));

        var key = await auth.CreateApiKeyAsync(
            owner.Id,
            $"AgentKey-{Guid.NewGuid():N}",
            (scopes ?? PermissionScopes.AllValidScopes.ToArray()).ToList());

        return (owner, key.RawTokenOnce);
    }

    private static async Task<string> CreateScopedKeyAsync(
        WebApplicationFactory<Program> factory,
        long userId,
        params string[] scopes)
    {
        using var scope = factory.Services.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IPlexAuthAndSessionService>();
        var key = await auth.CreateApiKeyAsync(userId, $"Scoped-{Guid.NewGuid():N}", scopes.ToList());
        return key.RawTokenOnce;
    }

    private static HttpClient CreateBearerClient(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent JsonBody(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static async Task<JsonObject> PostMcpAsync(
        HttpClient client,
        string url,
        object rpcPayload)
    {
        using var resp = await client.PostAsync(url, JsonBody(rpcPayload));
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"Expected 200 OK from {url}, got {(int)resp.StatusCode}: {raw}");
        return JsonNode.Parse(raw)!.AsObject();
    }

    [Fact]
    public async Task CatalogWorks_GetEndpoint_SupportsServerSideFiltersSearchSortAndPagination()
    {
        await using var factory = CreateWebFactory();
        var (_, apiKey) = await SeedOwnerAndApiKeyAsync(factory);
        using var client = CreateBearerClient(factory, apiKey);

        var moviesRoot = Path.Combine(_testRoot, "lib-movies");
        var tvRoot = Path.Combine(_testRoot, "lib-tv");
        var animeRoot = Path.Combine(_testRoot, "lib-anime");
        var animeMoviesRoot = Path.Combine(_testRoot, "lib-anime-movies");
        Directory.CreateDirectory(moviesRoot);
        Directory.CreateDirectory(tvRoot);
        Directory.CreateDirectory(animeRoot);
        Directory.CreateDirectory(animeMoviesRoot);

        using var scope = factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();

        var moviesLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Filme", "movies", moviesRoot));
        var tvLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Serien", "tv", tvRoot));
        var animeLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Anime", "anime", animeRoot));
        var animeMoviesLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Animefilme", "movies", animeMoviesRoot));

        // Work A: KonoSuba (anime series, continuing, 2 monitored + 1 unmonitored special, 1 file -> 1/2 missing=1)
        var konosuba = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
            LibraryId: animeLib.Id,
            Title: "KonoSuba – God's blessing on this wonderful world!!",
            WorkKind: "anime",
            OriginalLanguage: "ja",
            Year: 2016,
            Status: "continuing",
            Monitored: true,
            Tags: ["anime", "anime-series"],
            ExternalIds: new Dictionary<string, string> { ["tvdb"] = "305603" },
            Containers:
            [
                new MetadataContainerSeed("season", 1, "Season 1", "2016-01-14",
                [
                    new MetadataUnitSeed("episode", 1, 1, "This Self-Proclaimed Goddess and Reincarnation in Another World!", "2016-01-14"),
                    new MetadataUnitSeed("episode", 2, 2, "An Explosion for This Chunibyo!", "2016-01-21"),
                    new MetadataUnitSeed("episode", 3, 3, "Unmonitored OVA Special", "2016-06-24"),
                ]),
            ],
            SkipUpstreamHydration: true));
        await catalog.UpdateUnitAsync(konosuba.Containers[0].Units[2].Id, new UpdateMediaUnitRequest(Monitored: false));
        await catalog.AttachFileAsync(new AttachMediaFileRequest(
            UnitId: konosuba.Containers[0].Units[0].Id,
            RelativePath: "Season 01/KonoSuba S01E01.mkv",
            SizeBytes: 900_000_000,
            QualityName: "Bluray-1080p"));

        // Work B: Spider-Man: Into the Spider-Verse (cartoon movie, complete, duplicate files -> health=duplicates)
        var spiderVerse = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
            LibraryId: moviesLib.Id,
            Title: "Spider-Man: Into the Spider-Verse",
            WorkKind: "movies",
            OriginalLanguage: "en",
            Year: 2018,
            Status: "released",
            Monitored: true,
            Tags: ["cartoon"],
            Containers:
            [
                new MetadataContainerSeed("feature", 1, "Feature", "2018-12-14",
                [
                    new MetadataUnitSeed("movie", 1, null, "Spider-Man: Into the Spider-Verse", "2018-12-14"),
                ]),
            ],
            SkipUpstreamHydration: true));
        await catalog.AttachFileAsync(new AttachMediaFileRequest(
            UnitId: spiderVerse.Containers[0].Units[0].Id,
            RelativePath: "Spider-Verse.1080p.mkv",
            SizeBytes: 4_000_000_000,
            QualityName: "Bluray-1080p"));
        await catalog.AttachFileAsync(new AttachMediaFileRequest(
            UnitId: spiderVerse.Containers[0].Units[0].Id,
            RelativePath: "Spider-Verse.2160p.mkv",
            SizeBytes: 12_000_000_000,
            QualityName: "Bluray-2160p"));

        // Work C: Suzume (anime movie, missing)
        var suzume = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
            LibraryId: animeMoviesLib.Id,
            Title: "Suzume",
            WorkKind: "movies",
            OriginalLanguage: "ja",
            Year: 2022,
            Status: "released",
            Monitored: true,
            ExternalIds: new Dictionary<string, string> { ["anilist"] = "142770" },
            Tags: ["anime", "anime-movie"],
            Containers:
            [
                new MetadataContainerSeed("feature", 1, "Feature", "2022-11-11",
                [
                    new MetadataUnitSeed("movie", 1, null, "Suzume", "2022-11-11"),
                ]),
            ],
            SkipUpstreamHydration: true));

        // Work D: Unmonitored Archive Film
        var archive = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
            LibraryId: moviesLib.Id,
            Title: "Unmonitored Archive Film",
            WorkKind: "movies",
            OriginalLanguage: "de",
            Year: 1999,
            Status: "released",
            Monitored: false,
            Containers:
            [
                new MetadataContainerSeed("feature", 1, "Feature", "1999-01-01",
                [
                    new MetadataUnitSeed("movie", 1, null, "Unmonitored Archive Film", "1999-01-01"),
                ]),
            ],
            SkipUpstreamHydration: true));

        // Work E: Cyberpunk: Edgerunners (anime work placed inside TV library -> health=wrong-kind;
        // 2 monitored units + 1 unmonitored unit; 2 files attached -> complete (2/2 monitored!) NOT missing!)
        var edgerunners = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
            LibraryId: tvLib.Id,
            Title: "Cyberpunk: Edgerunners",
            WorkKind: "anime",
            OriginalLanguage: "ja",
            Year: 2022,
            Status: "released",
            Monitored: true,
            Tags: ["anime", "anime-series"],
            Containers:
            [
                new MetadataContainerSeed("season", 1, "Season 1", "2022-09-13",
                [
                    new MetadataUnitSeed("episode", 1, 1, "Let You Down", "2022-09-13"),
                    new MetadataUnitSeed("episode", 2, 2, "Like A Boy", "2022-09-13"),
                    new MetadataUnitSeed("episode", 3, 3, "Unmonitored Bonus Recap", "2022-09-13"),
                ]),
            ],
            SkipUpstreamHydration: true));
        await catalog.UpdateUnitAsync(edgerunners.Containers[0].Units[2].Id, new UpdateMediaUnitRequest(Monitored: false));
        await catalog.AttachFileAsync(new AttachMediaFileRequest(
            UnitId: edgerunners.Containers[0].Units[0].Id,
            RelativePath: "Edgerunners S01E01.mkv",
            SizeBytes: 1_500_000_000,
            QualityName: "WEBDL-1080p"));
        await catalog.AttachFileAsync(new AttachMediaFileRequest(
            UnitId: edgerunners.Containers[0].Units[1].Id,
            RelativePath: "Edgerunners S01E02.mkv",
            SizeBytes: 1_500_000_000,
            QualityName: "WEBDL-1080p"));

        static async Task<List<MediaWorkSummary>> GetWorksAsync(HttpClient http, string url)
        {
            var res = await http.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            return (await res.Content.ReadFromJsonAsync<List<MediaWorkSummary>>())!;
        }

        // Backward-compatible unfiltered call
        var allWorks = await GetWorksAsync(client, "/api/v1/catalog/works");
        Assert.Equal(5, allWorks.Count);

        // Search query (normal + collapsed punctuation + external ID)
        Assert.Equal([konosuba.Work.Id], (await GetWorksAsync(client, "/api/v1/catalog/works?q=konosuba")).Select(w => w.Id).ToArray());
        Assert.Equal([konosuba.Work.Id], (await GetWorksAsync(client, "/api/v1/catalog/works?q=305603")).Select(w => w.Id).ToArray());
        Assert.Equal([spiderVerse.Work.Id], (await GetWorksAsync(client, "/api/v1/catalog/works?q=spiderman")).Select(w => w.Id).ToArray());

        // libraryIds + vertical
        var libFiltered = await GetWorksAsync(client, $"/api/v1/catalog/works?libraryIds={animeLib.Id},{animeMoviesLib.Id}");
        Assert.Equal([konosuba.Work.Id, suzume.Work.Id], libFiltered.Select(w => w.Id).OrderBy(x => x).ToArray());

        var verticalFiltered = await GetWorksAsync(client, "/api/v1/catalog/works?vertical=anime");
        Assert.Equal(
            new[] { konosuba.Work.Id, edgerunners.Work.Id }.OrderBy(x => x).ToArray(),
            verticalFiltered.Select(w => w.Id).OrderBy(x => x).ToArray());

        // monitored flag
        Assert.Equal([archive.Work.Id], (await GetWorksAsync(client, "/api/v1/catalog/works?monitored=false")).Select(w => w.Id).ToArray());

        // availability (continuing, complete using monitored X/Y semantics, missing)
        Assert.Equal([konosuba.Work.Id], (await GetWorksAsync(client, "/api/v1/catalog/works?availability=continuing")).Select(w => w.Id).ToArray());
        var complete = await GetWorksAsync(client, "/api/v1/catalog/works?availability=complete");
        Assert.Equal(
            new[] { spiderVerse.Work.Id, edgerunners.Work.Id }.OrderBy(x => x).ToArray(),
            complete.Select(w => w.Id).OrderBy(x => x).ToArray());
        var monitoredMissing = await GetWorksAsync(client, "/api/v1/catalog/works?monitored=true&availability=missing");
        Assert.Equal(
            new[] { konosuba.Work.Id, suzume.Work.Id }.OrderBy(x => x).ToArray(),
            monitoredMissing.Select(w => w.Id).OrderBy(x => x).ToArray());

        // classification
        Assert.Equal([suzume.Work.Id], (await GetWorksAsync(client, "/api/v1/catalog/works?classification=anime-movie")).Select(w => w.Id).ToArray());
        Assert.Equal([spiderVerse.Work.Id], (await GetWorksAsync(client, "/api/v1/catalog/works?classification=cartoon")).Select(w => w.Id).ToArray());
        Assert.Equal(
            new[] { konosuba.Work.Id, edgerunners.Work.Id }.OrderBy(x => x).ToArray(),
            (await GetWorksAsync(client, "/api/v1/catalog/works?classification=anime-series")).Select(w => w.Id).OrderBy(x => x).ToArray());

        // health
        Assert.Equal([spiderVerse.Work.Id], (await GetWorksAsync(client, "/api/v1/catalog/works?health=duplicates")).Select(w => w.Id).ToArray());
        Assert.Equal([edgerunners.Work.Id], (await GetWorksAsync(client, "/api/v1/catalog/works?health=wrong-kind")).Select(w => w.Id).ToArray());

        // sort + order + pagination
        var sortedByYear = await GetWorksAsync(client, "/api/v1/catalog/works?sort=year&order=asc");
        Assert.Equal([1999, 2016, 2018, 2022, 2022], sortedByYear.Select(w => w.Year!.Value).ToArray());

        var paged = await GetWorksAsync(client, "/api/v1/catalog/works?sort=year&order=asc&limit=2&offset=1");
        Assert.Equal([2016, 2018], paged.Select(w => w.Year!.Value).ToArray());

        var missingDesc = await GetWorksAsync(client, "/api/v1/catalog/works?sort=missing&order=desc");
        Assert.Contains(missingDesc[0].Id, new[] { konosuba.Work.Id, suzume.Work.Id, archive.Work.Id });
    }

    [Fact]
    public async Task CatalogWorks_AndMcp_SupportSortByDateAddedAliasesAndInstantPrecision()
    {
        await using var factory = CreateWebFactory();
        var (_, apiKey) = await SeedOwnerAndApiKeyAsync(factory);
        using var client = CreateBearerClient(factory, apiKey);

        var moviesRoot = Path.Combine(_testRoot, "sort-added-movies");
        Directory.CreateDirectory(moviesRoot);

        long wOldestId;
        long wMiddleId;
        long wNewestId;

        using (var scope = factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var catalog = sp.GetRequiredService<IPolymorphicCatalogService>();
            var db = sp.GetRequiredService<ISqliteDbFactory>();
            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Movies", "movies", moviesRoot));

            wOldestId = (await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "Zulu Oldest",
                WorkKind: "movies",
                Year: 2020,
                SkipUpstreamHydration: true))).Work.Id;

            wMiddleId = (await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "Alpha Middle",
                WorkKind: "movies",
                Year: 2021,
                SkipUpstreamHydration: true))).Work.Id;

            wNewestId = (await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "Beta Newest",
                WorkKind: "movies",
                Year: 2022,
                SkipUpstreamHydration: true))).Work.Id;

            // Mix Z, +02:00 offset, and sub-millisecond .NET "O" precision:
            // Oldest: 14:37:37.0000000+02:00 == 12:37:37.0000000Z
            // Middle: 2026-11-14T12:37:37.1000000+00:00
            // Newest: 2026-11-14T12:37:37.6074613Z (note ASCII 'Z' vs '+' / '.' would misorder under Ordinal!)
            await db.ExecuteWriteAsync(DatabaseKind.Core, async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    "UPDATE media_works SET added_at = @AddedAt WHERE id = @Id",
                    new { Id = wOldestId, AddedAt = "2026-11-14T14:37:37.0000000+02:00" },
                    transaction: tx);
                await conn.ExecuteAsync(
                    "UPDATE media_works SET added_at = @AddedAt WHERE id = @Id",
                    new { Id = wMiddleId, AddedAt = "2026-11-14T12:37:37.1000000+00:00" },
                    transaction: tx);
                await conn.ExecuteAsync(
                    "UPDATE media_works SET added_at = @AddedAt WHERE id = @Id",
                    new { Id = wNewestId, AddedAt = "2026-11-14T12:37:37.6074613Z" },
                    transaction: tx);
            });
        }

        static async Task<long[]> GetSortedIdsAsync(HttpClient http, string url)
        {
            var res = await http.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var works = (await res.Content.ReadFromJsonAsync<List<MediaWorkSummary>>())!;
            return works.Select(w => w.Id).ToArray();
        }

        Assert.Equal([wNewestId, wMiddleId, wOldestId], await GetSortedIdsAsync(client, "/api/v1/catalog/works?sort=addedAt&order=desc"));
        Assert.Equal([wOldestId, wMiddleId, wNewestId], await GetSortedIdsAsync(client, "/api/v1/catalog/works?sort=added&order=asc"));
        Assert.Equal([wNewestId, wMiddleId, wOldestId], await GetSortedIdsAsync(client, "/api/v1/catalog/works?sort=date_added&order=desc"));

        var mcpResp = await PostMcpAsync(client, "/api/v1/mcp", new
        {
            jsonrpc = "2.0",
            id = 42,
            method = "tools/call",
            @params = new
            {
                name = "catalog_list_works",
                arguments = new { sort = "addedAt", order = "desc", limit = 10 },
            },
        });
        var mcpResult = mcpResp["result"]!.AsObject();
        Assert.False(mcpResult["isError"]?.GetValue<bool>() ?? false);
        var mcpPayload = JsonNode.Parse(mcpResult["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        var mcpIds = mcpPayload["items"]!.AsArray().Select(item => item!["id"]!.GetValue<long>()).ToArray();
        Assert.Equal([wNewestId, wMiddleId, wOldestId], mcpIds);
    }

    [Fact]
    public async Task ReleaseSearchAndBlocklist_SupportFilterSortAndPaginationParams()
    {
        var fakeGateway = new FakeReleaseSearchHttpGateway();
        await using var factory = CreateWebFactory(fakeGateway);
        var (_, apiKey) = await SeedOwnerAndApiKeyAsync(factory);
        using var client = CreateBearerClient(factory, apiKey);

        var moviesRoot = Path.Combine(_testRoot, "movies-rel");
        Directory.CreateDirectory(moviesRoot);

        long workId;
        long torrentIndexerId;
        using (var scope = factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var catalog = sp.GetRequiredService<IPolymorphicCatalogService>();
            var indexerSvc = sp.GetRequiredService<IUnifiedIndexerService>();
            var db = sp.GetRequiredService<ISqliteDbFactory>();

            var moviesLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Movies", "movies", moviesRoot));
            var dune = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: moviesLib.Id,
                Title: "Dune: Part Two",
                WorkKind: "movies",
                OriginalLanguage: "en",
                Year: 2024,
                Monitored: true,
                ExternalIds: new Dictionary<string, string> { ["imdb"] = "tt15239678" },
                Containers:
                [
                    new MetadataContainerSeed("feature", 1, "Feature", "2024-03-01",
                    [
                        new MetadataUnitSeed("movie", 1, null, "Dune: Part Two", "2024-03-01"),
                    ]),
                ],
                SkipUpstreamHydration: true));
            workId = dune.Work.Id;

            var torIx = await indexerSvc.CreateIndexerAsync(new UpsertIndexerRequest(
                Name: "MockTorrentIndexer",
                Implementation: "torznab",
                DefinitionId: null,
                Protocol: "torrent",
                BaseUrl: "https://mock-tor.example/api",
                ApiKey: "tor-key",
                Categories: [2000, 2040, 2045],
                RequestDelayMs: 0));
            torrentIndexerId = torIx.Id;

            await indexerSvc.CreateIndexerAsync(new UpsertIndexerRequest(
                Name: "MockUsenetIndexer",
                Implementation: "newznab",
                DefinitionId: null,
                Protocol: "usenet",
                BaseUrl: "https://mock-nzb.example/api",
                ApiKey: "nzb-key",
                Categories: [2000, 2040, 2045],
                RequestDelayMs: 0));

            await db.ExecuteWriteAsync(DatabaseKind.Core, (conn, tx) => conn.ExecuteAsync(
                """
                INSERT INTO recurring_blocklist_hashes (
                    info_hash, title, reason, blocked_at, protocol, release_guid, normalized_title, indexer_id, indexer_name, work_id
                ) VALUES
                (
                    '1111111111111111111111111111111111111111',
                    'Dune.Part.Two.2024.2160p.REMUX-GRP2',
                    'Malware zip inside torrent',
                    '2026-11-15T10:00:00Z',
                    'torrent',
                    'dune-tor-2',
                    'duneparttwo20242160premuxgrp2',
                    @TorIxId,
                    'MockTorrentIndexer',
                    @WorkId
                ),
                (
                    'nzb:abcdefabcdef1234',
                    'Dune.Part.Two.2024.BROKEN.NZB',
                    'Broken par2 repair',
                    '2026-11-15T11:00:00Z',
                    'usenet',
                    'dune-broken-nzb',
                    'duneparttwo2024brokennzb',
                    NULL,
                    'MockUsenetIndexer',
                    @WorkId
                );
                """,
                new { TorIxId = torrentIndexerId, WorkId = workId },
                transaction: tx));
        }

        // 1. Blocklist filtering on both /api/v1/activity/blocklist and /api/v1/downloaders/blocklist
        var usenetBlResp = await client.GetAsync("/api/v1/activity/blocklist?protocol=usenet");
        Assert.Equal(HttpStatusCode.OK, usenetBlResp.StatusCode);
        var usenetBl = (await usenetBlResp.Content.ReadFromJsonAsync<List<RecurringBlockedHashEntry>>())!;
        Assert.Single(usenetBl);
        Assert.Equal("usenet", usenetBl[0].Protocol);

        var qBlResp = await client.GetAsync($"/api/v1/downloaders/blocklist?q=Malware&workId={workId}&limit=1&offset=0");
        Assert.Equal(HttpStatusCode.OK, qBlResp.StatusCode);
        var qBl = (await qBlResp.Content.ReadFromJsonAsync<List<RecurringBlockedHashEntry>>())!;
        Assert.Single(qBl);
        Assert.Equal("1111111111111111111111111111111111111111", qBl[0].InfoHash);

        // 2. POST /api/v1/indexers/search with protocol + hideBlocklisted + sort + limit
        var torSearchResp = await client.PostAsync(
            "/api/v1/indexers/search?protocol=torrent&hideBlocklisted=true&sort=size&order=asc",
            JsonBody(new IndexerSearchQuery(Query: "Dune Part Two", MediaVertical: "movies", WorkId: workId)));
        Assert.Equal(HttpStatusCode.OK, torSearchResp.StatusCode);
        var torRows = JsonNode.Parse(await torSearchResp.Content.ReadAsStringAsync())!.AsArray();
        Assert.Single(torRows);
        Assert.Equal("Dune.Part.Two.2024.1080p.WEB-DL-GRP1", torRows[0]!["release"]!["title"]!.GetValue<string>());

        // 3. POST /api/v1/indexers/search sorted by size desc, limited to 1 returns the larger (REMUX) torrent
        var topSizeResp = await client.PostAsync(
            "/api/v1/indexers/search?protocol=torrent&sort=size&order=desc&limit=1",
            JsonBody(new IndexerSearchQuery(Query: "Dune Part Two", MediaVertical: "movies", WorkId: workId)));
        Assert.Equal(HttpStatusCode.OK, topSizeResp.StatusCode);
        var topSizeRows = JsonNode.Parse(await topSizeResp.Content.ReadAsStringAsync())!.AsArray();
        Assert.Single(topSizeRows);
        Assert.Equal("Dune.Part.Two.2024.2160p.REMUX-GRP2", topSizeRows[0]!["release"]!["title"]!.GetValue<string>());

        // 4. GET /api/v1/catalog/works/{id}/releases?protocol=usenet
        var workRelResp = await client.GetAsync($"/api/v1/catalog/works/{workId}/releases?protocol=usenet&q=German");
        Assert.Equal(HttpStatusCode.OK, workRelResp.StatusCode);
        var workRelRows = JsonNode.Parse(await workRelResp.Content.ReadAsStringAsync())!.AsArray();
        Assert.Single(workRelRows);
        Assert.Equal("usenet", workRelRows[0]!["release"]!["protocol"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpServer_AuthTransportProtocolNegotiationAndBatch_WorkEndToEnd()
    {
        await using var factory = CreateWebFactory();
        var (_, apiKey) = await SeedOwnerAndApiKeyAsync(factory);

        using var anonClient = factory.CreateClient();
        var initBody = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { protocolVersion = "2025-06-18" },
        };

        // Unauthenticated requests fail with 401
        using (var unauth1 = await anonClient.PostAsync("/api/v1/mcp", JsonBody(initBody)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauth1.StatusCode);
        }

        using (var unauth2 = await anonClient.PostAsync("/mcp", JsonBody(initBody)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauth2.StatusCode);
        }

        using var authClient = CreateBearerClient(factory, apiKey);

        // GET -> 405 Method Not Allowed (must not hit SPA fallback!)
        using (var getResp = await authClient.GetAsync("/api/v1/mcp"))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, getResp.StatusCode);
            Assert.Contains("POST", string.Join(",", getResp.Content.Headers.Allow), StringComparison.OrdinalIgnoreCase);
        }

        // DELETE -> 204 No Content
        using (var delResp = await authClient.DeleteAsync("/api/v1/mcp"))
        {
            Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);
        }

        // ?apikey= authentication on /api/v1/mcp and /mcp + protocolVersion negotiation + Mcp-Session-Id echo
        using var reqMsg = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/mcp?apikey={Uri.EscapeDataString(apiKey)}")
        {
            Content = JsonBody(initBody),
        };
        reqMsg.Headers.Add("Mcp-Session-Id", "session-test-123");
        using (var initResp = await anonClient.SendAsync(reqMsg))
        {
            Assert.Equal(HttpStatusCode.OK, initResp.StatusCode);
            Assert.Equal("session-test-123", initResp.Headers.GetValues("Mcp-Session-Id").Single());
            var node = JsonNode.Parse(await initResp.Content.ReadAsStringAsync())!.AsObject();
            Assert.Equal("2025-06-18", node["result"]!["protocolVersion"]!.GetValue<string>());
            Assert.Equal("managerr", node["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        }

        var init202503 = await PostMcpAsync(anonClient, $"/mcp?apikey={Uri.EscapeDataString(apiKey)}", new
        {
            jsonrpc = "2.0",
            id = "init-2",
            method = "initialize",
            @params = new { protocolVersion = "2025-03-26" },
        });
        Assert.Equal("2025-03-26", init202503["result"]!["protocolVersion"]!.GetValue<string>());

        // Notification (no id) -> 202 Accepted with empty body
        using (var notifResp = await authClient.PostAsync("/api/v1/mcp", JsonBody(new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized",
        })))
        {
            Assert.Equal(HttpStatusCode.Accepted, notifResp.StatusCode);
            Assert.Equal(string.Empty, await notifResp.Content.ReadAsStringAsync());
        }

        // Batch request array -> JSON array of 3 responses
        using (var batchResp = await authClient.PostAsync("/api/v1/mcp", JsonBody(new object[]
        {
            new { jsonrpc = "2.0", id = 10, method = "ping" },
            new { jsonrpc = "2.0", id = 11, method = "resources/list" },
            new { jsonrpc = "2.0", id = 12, method = "prompts/list" },
        })))
        {
            Assert.Equal(HttpStatusCode.OK, batchResp.StatusCode);
            var arr = JsonNode.Parse(await batchResp.Content.ReadAsStringAsync())!.AsArray();
            Assert.Equal(3, arr.Count);
            Assert.NotNull(arr[1]!["result"]!["resources"]);
            Assert.NotNull(arr[2]!["result"]!["prompts"]);
        }
    }

    [Fact]
    public async Task McpServer_RbacScopeFilteringAndFullAgentWorkflow_EnforcesZeroTrust()
    {
        await using var factory = CreateWebFactory();
        var (owner, fullKey) = await SeedOwnerAndApiKeyAsync(factory);
        var readOnlyKey = await CreateScopedKeyAsync(factory, owner.Id, PermissionScopes.CatalogRead);

        var moviesRoot = Path.Combine(_testRoot, "mcp-movies");
        Directory.CreateDirectory(moviesRoot);
        using (var scope = factory.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            await catalog.CreateLibraryAsync(new CreateLibraryRequest("Movies", "movies", moviesRoot));
        }

        // 1. Read-only key: tools/list includes read tools only and excludes mutating/admin tools
        using var readClient = CreateBearerClient(factory, readOnlyKey);
        var readToolsObj = await PostMcpAsync(readClient, "/api/v1/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/list",
        });
        var readToolNames = readToolsObj["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("libraries_list", readToolNames);
        Assert.Contains("catalog_list_works", readToolNames);
        Assert.Contains("catalog_get_work", readToolNames);
        Assert.Contains("wanted_list", readToolNames);
        Assert.Contains("profiles_list", readToolNames);

        Assert.DoesNotContain("catalog_add_work", readToolNames);
        Assert.DoesNotContain("catalog_delete_work", readToolNames);
        Assert.DoesNotContain("queue_control", readToolNames);
        Assert.DoesNotContain("system_status_and_tasks", readToolNames);

        // Calling a forbidden tool with read-only key returns isError=true with ERR_RBAC_FORBIDDEN
        var forbiddenCall = await PostMcpAsync(readClient, "/api/v1/mcp", new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new
            {
                name = "catalog_delete_work",
                arguments = new { workId = 1 },
            },
        });
        Assert.True(forbiddenCall["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("ERR_RBAC_FORBIDDEN", forbiddenCall["result"]!["content"]![0]!["text"]!.GetValue<string>());

        // 2. Full-scope key: all 40 tools available + full agent workflow end-to-end
        using var fullClient = CreateBearerClient(factory, fullKey);
        var fullToolsObj = await PostMcpAsync(fullClient, "/api/v1/mcp", new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/list",
        });
        var fullToolNames = fullToolsObj["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(40, fullToolsObj["result"]!["tools"]!.AsArray().Count);
        Assert.Contains("cleanerr_run_workflow", fullToolNames);
        Assert.Contains("cleanerr_rescue_queue_item", fullToolNames);
        Assert.Contains("cleanerr_postpone_queue_item", fullToolNames);
        Assert.Contains("cleanerr_delete_exclusion", fullToolNames);

        static JsonNode ExtractToolPayload(JsonObject rpcResp)
        {
            var result = rpcResp["result"]!.AsObject();
            Assert.False(result["isError"]?.GetValue<bool>() ?? false, $"Unexpected MCP tool error: {result.ToJsonString()}");
            var text = result["content"]![0]!["text"]!.GetValue<string>();
            return JsonNode.Parse(text)!;
        }

        // libraries_list
        var libsPayload = ExtractToolPayload(await PostMcpAsync(fullClient, "/api/v1/mcp", new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "tools/call",
            @params = new { name = "libraries_list", arguments = new { } },
        })).AsArray();
        var libId = libsPayload[0]!["id"]!.GetValue<long>();

        // profiles_list
        var profilesPayload = ExtractToolPayload(await PostMcpAsync(fullClient, "/api/v1/mcp", new
        {
            jsonrpc = "2.0",
            id = 5,
            method = "tools/call",
            @params = new { name = "profiles_list", arguments = new { } },
        })).AsObject();
        var profileId = profilesPayload["qualityProfiles"]!.AsArray()[0]!["id"]!.GetValue<long>();

        // catalog_add_work
        var addPayload = ExtractToolPayload(await PostMcpAsync(fullClient, "/api/v1/mcp", new
        {
            jsonrpc = "2.0",
            id = 6,
            method = "tools/call",
            @params = new
            {
                name = "catalog_add_work",
                arguments = new
                {
                    title = "Blade Runner 2049",
                    libraryId = libId,
                    vertical = "movies",
                    year = 2017,
                    originalLanguage = "en",
                    qualityProfileId = profileId,
                    monitored = true,
                },
            },
        })).AsObject();
        var workId = addPayload["work"]!["id"]!.GetValue<long>();

        // catalog_list_works with q filter
        var listPayload = ExtractToolPayload(await PostMcpAsync(fullClient, "/api/v1/mcp", new
        {
            jsonrpc = "2.0",
            id = 7,
            method = "tools/call",
            @params = new
            {
                name = "catalog_list_works",
                arguments = new { q = "Blade Runner", limit = 10 },
            },
        })).AsObject();
        Assert.Equal(1, listPayload["totalMatched"]!.GetValue<int>());
        Assert.Equal(workId, listPayload["items"]![0]!["id"]!.GetValue<long>());

        // catalog_get_work
        var getPayload = ExtractToolPayload(await PostMcpAsync(fullClient, "/api/v1/mcp", new
        {
            jsonrpc = "2.0",
            id = 8,
            method = "tools/call",
            @params = new
            {
                name = "catalog_get_work",
                arguments = new { workId },
            },
        })).AsObject();
        Assert.Equal("Blade Runner 2049", getPayload["work"]!["title"]!.GetValue<string>());
        Assert.True(getPayload["work"]!["monitored"]!.GetValue<bool>());

        // catalog_set_monitoring -> false
        var monPayload = ExtractToolPayload(await PostMcpAsync(fullClient, "/api/v1/mcp", new
        {
            jsonrpc = "2.0",
            id = 9,
            method = "tools/call",
            @params = new
            {
                name = "catalog_set_monitoring",
                arguments = new { workId, monitored = false },
            },
        })).AsObject();
        Assert.False(monPayload["work"]!["monitored"]!.GetValue<bool>());

        // system_status_and_tasks (action=health)
        var healthPayload = ExtractToolPayload(await PostMcpAsync(fullClient, "/api/v1/mcp", new
        {
            jsonrpc = "2.0",
            id = 10,
            method = "tools/call",
            @params = new
            {
                name = "system_status_and_tasks",
                arguments = new { action = "health" },
            },
        })).AsObject();
        Assert.NotNull(healthPayload["healthChecks"]);

        // 3. system.mcp.enabled = false returns 404 on /api/v1/mcp
        using (var disableScope = factory.Services.CreateScope())
        {
            var settingsSvc = disableScope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            await settingsSvc.SetAsync("system.mcp.enabled", "false", isSecret: false);
        }

        using var disabledResp = await fullClient.PostAsync("/api/v1/mcp", JsonBody(new
        {
            jsonrpc = "2.0",
            id = 11,
            method = "tools/list",
        }));
        Assert.Equal(HttpStatusCode.NotFound, disabledResp.StatusCode);
    }

    private sealed class FakeReleaseSearchHttpGateway : IIndexerHttpGateway
    {
        public Task<string> FetchStringAsync(Uri targetUri, IndexerProxyInternal? matchedProxy, CancellationToken ct = default)
        {
            if (targetUri.Query.Contains("t=caps", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult("""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <caps>
                      <searching>
                        <search available="yes" supportedParams="q"/>
                        <movie-search available="yes" supportedParams="q,imdbid"/>
                      </searching>
                      <categories>
                        <category id="2000" name="Movies"><subcat id="2040" name="HD"/><subcat id="2045" name="UHD"/></category>
                      </categories>
                    </caps>
                    """);
            }

            if (targetUri.Host.Contains("mock-tor", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult("""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
                      <channel>
                        <item>
                          <title>Dune.Part.Two.2024.1080p.WEB-DL-GRP1</title>
                          <guid>dune-tor-1</guid>
                          <link>https://mock-tor.example/dl/1</link>
                          <enclosure url="https://mock-tor.example/dl/1" length="2000000000" type="application/x-bittorrent"/>
                          <torznab:attr name="category" value="2040"/>
                          <torznab:attr name="seeders" value="45"/>
                          <torznab:attr name="peers" value="50"/>
                          <torznab:attr name="infohash" value="2222222222222222222222222222222222222222"/>
                        </item>
                        <item>
                          <title>Dune.Part.Two.2024.2160p.REMUX-GRP2</title>
                          <guid>dune-tor-2</guid>
                          <link>https://mock-tor.example/dl/2</link>
                          <enclosure url="https://mock-tor.example/dl/2" length="8000000000" type="application/x-bittorrent"/>
                          <torznab:attr name="category" value="2045"/>
                          <torznab:attr name="seeders" value="120"/>
                          <torznab:attr name="peers" value="130"/>
                          <torznab:attr name="infohash" value="1111111111111111111111111111111111111111"/>
                        </item>
                      </channel>
                    </rss>
                    """);
            }

            return Task.FromResult("""
                <?xml version="1.0" encoding="UTF-8"?>
                <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
                  <channel>
                    <item>
                      <title>Dune.Part.Two.2024.1080p.German.DL.BluRay-NZB</title>
                      <guid>dune-nzb-1</guid>
                      <link>https://mock-nzb.example/dl/3</link>
                      <enclosure url="https://mock-nzb.example/dl/3" length="4500000000" type="application/x-nzb"/>
                      <newznab:attr name="category" value="2040"/>
                    </item>
                  </channel>
                </rss>
                """);
        }
    }
}
