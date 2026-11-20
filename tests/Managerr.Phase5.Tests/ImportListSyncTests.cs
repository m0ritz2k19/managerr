using Managerr.Catalog;
using Managerr.Host.Services;
using Managerr.Phase5.Tests.Support;
using Xunit;

namespace Managerr.Phase5.Tests;

/// <summary>
/// #67: import lists. A sync adds what is new with the list's settings, skips what the catalog already has and what is
/// excluded, and a second sync adds nothing.
/// </summary>
public sealed class ImportListSyncTests
{
    private const string ListJson = """
        [
          {"title": "Dune: Part Two", "imdb_id": "tt15239678", "year": 2024, "poster_url": "https://img.test/dune2.jpg"},
          {"title": "Arrival", "imdb_id": "tt2543164", "year": 2016},
          {"title": "Morbius", "imdb_id": "tt5108870", "year": 2022},
          {"title": "", "imdb_id": "tt0000000"}
        ]
        """;

    [Fact]
    public async Task Sync_AddsNewTitlesWithTheListsSettings_SkipsExistingAndExcluded_AndIsIdempotent()
    {
        var http = new RecordingHttp { Responder = (_, _) => RecordingHttp.Json(ListJson) };
        await using var h = await HostTestHarness.StartAsync(http);
        var catalog = h.Get<IPolymorphicCatalogService>();
        var lists = h.Get<IImportListSyncService>();
        var movies = await h.LibraryAsync("movies");

        // Already in the catalog (matched by IMDb id, not title) and excluded by the user
        await catalog.AddWorkAsync(new CreateMediaWorkRequest(
            LibraryId: movies.Id, Title: "Arrival (2016)", WorkKind: "movies", OriginalLanguage: "en", Year: 2016,
            Monitored: true, ExternalIds: new Dictionary<string, string> { ["imdb"] = "tt2543164" }));
        await lists.AddExclusionAsync(new CreateImportListExclusionRequest("imdb:tt5108870", "Morbius", "movies", 2022));

        var list = await lists.UpsertImportListAsync(new UpsertImportListRequest(
            Name: "Popular movies", ListType: "stevenlu", MediaVertical: "movies", LibraryId: movies.Id,
            MonitorMode: "none", Settings: new Dictionary<string, string> { ["url"] = "https://lists.test/movies.json" },
            Tags: ["from-list"]));

        var first = await lists.SyncImportListAsync(list.Id);

        Assert.Equal("https://lists.test/movies.json", Assert.Single(http.Requests).Uri.ToString());
        Assert.Equal(1, first.AddedCount);
        Assert.Equal(1, first.SkippedExistingCount);
        Assert.Equal(1, first.SkippedExcludedCount);

        var works = await catalog.ListWorksAsync();
        Assert.Equal(2, works.Count);
        var dune = works.Single(w => w.Title == "Dune: Part Two");
        Assert.Equal("tt15239678", dune.ExternalIds["imdb"]);
        Assert.Equal(2024, dune.Year);
        Assert.False(dune.Monitored);
        Assert.Contains("from-list", dune.Tags);
        Assert.Equal(movies.Id, dune.LibraryId);

        var second = await lists.SyncImportListAsync(list.Id);
        Assert.Equal(0, second.AddedCount);
        Assert.Equal(2, second.SkippedExistingCount);
        Assert.Equal(2, (await catalog.ListWorksAsync()).Count);
    }

    [Fact]
    public async Task AnUnreachableList_IsReportedInTheSyncAll_AndTheOtherListsStillSync()
    {
        var http = new RecordingHttp
        {
            Responder = (req, _) => req.RequestUri!.AbsolutePath == "/down.json"
                ? RecordingHttp.Json("""{"error":"down"}""", System.Net.HttpStatusCode.BadGateway)
                : RecordingHttp.Json("""[{"title": "Arrival", "imdb_id": "tt2543164", "year": 2016}]"""),
        };
        await using var h = await HostTestHarness.StartAsync(http);
        var lists = h.Get<IImportListSyncService>();
        var movies = await h.LibraryAsync("movies");
        await lists.UpsertImportListAsync(new UpsertImportListRequest(
            "Broken list", "stevenlu", "movies", movies.Id, Settings: new Dictionary<string, string> { ["url"] = "https://lists.test/down.json" }));
        await lists.UpsertImportListAsync(new UpsertImportListRequest(
            "Working list", "stevenlu", "movies", movies.Id, Settings: new Dictionary<string, string> { ["url"] = "https://lists.test/ok.json" }));

        var reports = await lists.SyncAllEnabledListsAsync();

        Assert.Equal(2, reports.Count);
        Assert.StartsWith("Sync error:", reports[0].Message);
        Assert.Equal(0, reports[0].AddedCount);
        Assert.Equal(1, reports[1].AddedCount);
        Assert.Equal(["Arrival"], (await h.Get<IPolymorphicCatalogService>().ListWorksAsync()).Select(w => w.Title));
    }
}
