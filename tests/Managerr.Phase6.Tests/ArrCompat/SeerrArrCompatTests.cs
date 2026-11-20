using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Managerr.Phase6.Tests.ArrCompat;

/// <summary>
/// Managerr as a Sonarr/Radarr substitute for Seerr. Two pinned references keep this honest when either side changes:
/// <list type="bullet">
/// <item>upstream/*.openapi.json, the official Sonarr/Radarr v3 API at the upstream/ submodule commits: every compat
/// route must exist there and responses are validated against its schemas;</item>
/// <item>seerr-contract.json, the calls Seerr's servarr client makes and the fields it reads, extracted from Seerr's
/// source. The workflow tests replay those calls in Seerr's order with Seerr's request bodies.</item>
/// </list>
/// Refresh both with scripts/update-arr-compat-contract.py.
/// </summary>
public sealed class SeerrArrCompatTests
{
    public static TheoryData<string> Apps() => new(["sonarr", "radarr"]);

    // Managerr extras beyond the upstream API, kept deliberately
    private static readonly HashSet<string> ManagerrOnlyRoutes = new(StringComparer.Ordinal)
    {
    };

    [Fact]
    public void SeerrContract_IsReviewed_AndEveryCallExistsUpstream()
    {
        var unreviewed = SeerrContract.Calls.Where(c => c.Reads is null).Select(c => c.Key).ToList();
        Assert.True(unreviewed.Count == 0,
            $"seerr-contract.json has calls nobody has reviewed (\"reads\": null). Look at what Seerr does with the response and fill in 'reads':\n  {string.Join("\n  ", unreviewed)}");

        var specs = new Dictionary<string, OpenApiSpec> { ["sonarr"] = OpenApiSpec.Load("sonarr"), ["radarr"] = OpenApiSpec.Load("radarr") };
        var unknown = SeerrContract.Calls.Where(c => !specs[c.App].HasOperation(c.Method, c.Path)).Select(c => c.Key).ToList();
        Assert.True(unknown.Count == 0,
            $"Seerr calls endpoints the pinned upstream API does not have; bump upstream/ or check the contract:\n  {string.Join("\n  ", unknown)}");
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task CompatRoutes_ExistInTheUpstreamApi(string app)
    {
        await using var h = await SeerrCompatHarness.StartAsync();
        var spec = OpenApiSpec.Load(app);
        var extras = CompatRoutes(h.Services, app)
            .Where(r => !spec.HasOperation(r.Method, r.Path) && !ManagerrOnlyRoutes.Contains($"{app} {r.Method} {r.Path}"))
            .Select(r => $"{r.Method} {r.Path}")
            .ToList();
        Assert.True(extras.Count == 0,
            $"/compat/{app} serves routes the real {app} (at {spec.Commit[..10]}) does not have. Fix the route, or list it in ManagerrOnlyRoutes:\n  {string.Join("\n  ", extras)}");
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task EverySeerrCall_IsRoutedByManagerr(string app)
    {
        await using var h = await SeerrCompatHarness.StartAsync();
        var routes = CompatRoutes(h.Services, app).Select(r => OpenApiSpec.Key(r.Method, r.Path)).ToHashSet();
        var missing = SeerrContract.For(app)
            .Where(c => !routes.Contains(OpenApiSpec.Key(c.Method, c.Path)))
            .Select(c => $"{c.Key}  (Seerr: {string.Join(", ", c.UsedBy)})")
            .Distinct()
            .ToList();
        Assert.True(missing.Count == 0, $"Seerr calls these, Managerr has no route for them:\n  {string.Join("\n  ", missing)}");
    }

    [Fact]
    public async Task SeerrSonarrWorkflow_SettingsRequestScanTrackAndRemove()
    {
        await using var h = await SeerrCompatHarness.StartAsync();
        const string app = "sonarr";

        // Settings > Services > Sonarr > Test (routes/settings/sonarr.ts)
        var status = await h.SendAsync(app, "GET", "/system/status");
        await h.SendAsync(app, "GET", "/qualityProfile");
        var folders = await h.SendAsync(app, "GET", "/rootfolder");
        await h.SendAsync(app, "GET", "/languageprofile"); // only for Sonarr <= 3, see the status version
        await h.SendAsync(app, "GET", "/tag");
        var profileId = FirstId(h, app, "GET", "/qualityProfile");
        var rootFolder = RootFolderPath(folders, h.TvRoot);

        // User tags (settings migration 0007 and per-user tagging in MediaRequestSubscriber)
        var tag = await h.SendAsync(app, "POST", "/tag", body: new { label = "1 - plexowner" });
        var tagId = IdOf(tag);
        await h.SendAsync(app, "PUT", $"/tag/{tagId}", body: new { id = tagId, label = "1 - plexowner renamed" });
        var tags = await h.SendAsync(app, "GET", "/tag");
        Assert.Contains(tags.Body as JsonArray ?? [], t => t?["id"]?.GetValue<int>() == tagId && t["label"]?.GetValue<string>() == "1 - plexowner renamed");

        // Request season 1 of a new series: SonarrAPI.addSeries -> getSeriesByTvdbId, then POST
        var lookup = await h.SendAsync(app, "GET", "/series/lookup", Q("term", $"tvdb:{SeerrCompatHarness.SeveranceTvdbId}"));
        var candidate = FirstOf(lookup);
        Assert.True(candidate?["id"] is null || candidate["id"]!.GetValue<long>() == 0, $"lookup of a new series must not carry an id: {candidate}");
        var created = await h.SendAsync(app, "POST", "/series", body: new JsonObject
        {
            ["tvdbId"] = SeerrCompatHarness.SeveranceTvdbId,
            ["title"] = "Severance",
            ["qualityProfileId"] = profileId,
            ["languageProfileId"] = null,
            ["seasons"] = new JsonArray(
                (candidate?["seasons"] as JsonArray ?? [])
                .Select(s => (JsonNode)new JsonObject
                {
                    ["seasonNumber"] = s!["seasonNumber"]!.GetValue<int>(),
                    ["monitored"] = s["seasonNumber"]!.GetValue<int>() == 1,
                }).ToArray()),
            ["tags"] = new JsonArray(tagId),
            ["seasonFolder"] = true,
            ["monitored"] = true,
            ["monitorNewItems"] = "none",
            ["rootFolderPath"] = rootFolder,
            ["seriesType"] = "standard",
            ["addOptions"] = new JsonObject { ["ignoreEpisodesWithFiles"] = true, ["searchForMissingEpisodes"] = false },
        });
        var seriesId = IdOf(created);

        // Scanner and availability sync
        await h.SendAsync(app, "GET", "/series");
        await h.SendAsync(app, "GET", "/series", Q("tvdbId", SeerrCompatHarness.SeveranceTvdbId));
        await h.SendAsync(app, "GET", $"/series/{seriesId}");

        // Request season 2 of the existing series: the looked-up object goes back as a whole (round trip)
        var existing = FirstOf(await h.SendAsync(app, "GET", "/series/lookup", Q("term", $"tvdb:{SeerrCompatHarness.SeveranceTvdbId}")))!.AsObject();
        Assert.Equal(seriesId, existing["id"]?.GetValue<long>());
        existing["monitored"] = true;
        foreach (var season in existing["seasons"]!.AsArray())
        {
            if (season!["seasonNumber"]!.GetValue<int>() == 2)
            {
                season["monitored"] = true;
            }
        }

        var updated = await h.SendAsync(app, "PUT", "/series", body: existing);
        Assert.Equal(seriesId, IdOf(updated));
        var episodes = await h.SendAsync(app, "GET", "/episode", Q("seriesId", seriesId));
        var toMonitor = (episodes.Body as JsonArray ?? [])
            .Where(e => e!["seasonNumber"]?.GetValue<int>() == 2 && e["monitored"]?.GetValue<bool>() != true)
            .Select(e => e!["id"]!.GetValue<long>())
            .ToArray();
        await h.SendAsync(app, "PUT", "/episode/monitor", body: new { episodeIds = toMonitor.Length > 0 ? toMonitor : [1L], monitored = true });
        await h.CommandAsync(app, "MissingEpisodeSearch", new { seriesId });

        // Search by title (getSeriesByTitle)
        await h.SendAsync(app, "GET", "/series/lookup", Q("term", "Severance"));

        // Download tracker
        h.Queue.Add("nzo-sev-0102", "Severance.S01E02.Half.Loop.1080p.ATVP.WEB-DL.DDP5.1.H.264-NTb", "tv", 2_000_000_000, 500_000_000);

        // A grab under the German title covering two episodes: only grab history ties it to the series (#66)
        var episodeIds = (episodes.Body as JsonArray ?? [])
            .Where(e => e!["seasonNumber"]?.GetValue<int>() == 1 && e["episodeNumber"]?.GetValue<int>() is 3 or 4)
            .ToDictionary(e => e!["episodeNumber"]!.GetValue<int>(), e => e!["id"]!.GetValue<long>());
        var germanGrab = await h.GrabAsync("Abgetrennt.S01E03E04.German.DL.1080p.WEB.h264-GERMANS", "tv", seriesId, [episodeIds[3], episodeIds[4]]);
        h.Queue.Add(germanGrab, "Abgetrennt.S01E03E04.German.DL.1080p.WEB.h264-GERMANS", "tv", 4_000_000_000, 1_000_000_000);

        await h.CommandAsync(app, "RefreshMonitoredDownloads", new { });
        var queue = await h.SendAsync(app, "GET", "/queue", Q("includeEpisode", true));
        var germanRecords = (queue.Body?["records"] as JsonArray ?? []).Where(r => r?["downloadId"]?.GetValue<string>() == germanGrab).ToList();
        Assert.Equal(2, germanRecords.Count);
        Assert.All(germanRecords, r => Assert.Equal(seriesId, r!["seriesId"]!.GetValue<long>()));
        Assert.Equal([3, 4], germanRecords.Select(r => r!["episode"]!["episodeNumber"]!.GetValue<int>()).Order());

        // Remove media (routes/media.ts): lookup, then DELETE; afterwards the id must be gone
        var toRemove = FirstOf(await h.SendAsync(app, "GET", "/series/lookup", Q("term", $"tvdb:{SeerrCompatHarness.SeveranceTvdbId}")));
        await h.SendAsync(app, "DELETE", $"/series/{toRemove!["id"]!.GetValue<long>()}", new Dictionary<string, object?> { ["deleteFiles"] = true, ["addImportExclusion"] = false });
        var gone = await h.SendAsync(app, "GET", $"/series/{seriesId}");
        Assert.Equal(404, gone.Status);

        Verify(h, app, status, expectedAfterDelete: gone);
    }

    [Fact]
    public async Task SeerrRadarrWorkflow_SettingsRequestScanTrackAndRemove()
    {
        await using var h = await SeerrCompatHarness.StartAsync();
        const string app = "radarr";

        var status = await h.SendAsync(app, "GET", "/system/status");
        await h.SendAsync(app, "GET", "/qualityProfile");
        var folders = await h.SendAsync(app, "GET", "/rootfolder");
        await h.SendAsync(app, "GET", "/tag");
        var profileId = FirstId(h, app, "GET", "/qualityProfile");
        var rootFolder = RootFolderPath(folders, h.MoviesRoot);

        var tag = await h.SendAsync(app, "POST", "/tag", body: new { label = "1 - plexowner" });
        var tagId = IdOf(tag);
        await h.SendAsync(app, "PUT", $"/tag/{tagId}", body: new { id = tagId, label = "1 - plexowner renamed" });
        var tags = await h.SendAsync(app, "GET", "/tag");
        Assert.Contains(tags.Body as JsonArray ?? [], t => t?["id"]?.GetValue<int>() == tagId && t["label"]?.GetValue<string>() == "1 - plexowner renamed");

        // RadarrAPI.addMovie for a new movie (request approved without auto-search, so monitored=false here)
        var lookup = await h.SendAsync(app, "GET", "/movie/lookup", Q("term", $"tmdb:{SeerrCompatHarness.DuneTmdbId}"));
        var candidate = FirstOf(lookup);
        Assert.True(candidate?["id"] is null || candidate["id"]!.GetValue<long>() == 0, $"lookup of a new movie must not carry an id: {candidate}");
        var created = await h.SendAsync(app, "POST", "/movie", body: new
        {
            title = "Dune: Part Two",
            qualityProfileId = profileId,
            profileId,
            titleSlug = SeerrCompatHarness.DuneTmdbId.ToString(),
            minimumAvailability = "released",
            tmdbId = SeerrCompatHarness.DuneTmdbId,
            year = 2024,
            rootFolderPath = rootFolder,
            monitored = false,
            tags = new[] { tagId },
            addOptions = new { searchForMovie = false },
        });
        var movieId = IdOf(created);

        // Re-request: the movie exists but is unmonitored, so Seerr spreads the lookup result into PUT /movie
        var existing = FirstOf(await h.SendAsync(app, "GET", "/movie/lookup", Q("term", $"tmdb:{SeerrCompatHarness.DuneTmdbId}")))!.AsObject();
        Assert.Equal(movieId, existing["id"]?.GetValue<long>());
        Assert.False(existing["monitored"]?.GetValue<bool>() ?? true, "the movie was added unmonitored");
        var put = (JsonObject)existing.DeepClone();
        put["qualityProfileId"] = profileId;
        put["profileId"] = profileId;
        put["titleSlug"] = SeerrCompatHarness.DuneTmdbId.ToString();
        put["minimumAvailability"] = "released";
        put["tags"] = new JsonArray(tagId);
        put["rootFolderPath"] = rootFolder;
        put["monitored"] = true;
        put["addOptions"] = new JsonObject { ["searchForMovie"] = true };
        var updated = await h.SendAsync(app, "PUT", "/movie", body: put);
        Assert.True(updated.Body?["monitored"]?.GetValue<bool>() == true, $"PUT /movie must return the movie monitored: {updated.Body}");
        await h.CommandAsync(app, "MoviesSearch", new { movieIds = new[] { movieId } });

        await h.SendAsync(app, "GET", "/movie");
        await h.SendAsync(app, "GET", "/movie", Q("tmdbId", SeerrCompatHarness.DuneTmdbId));
        await h.SendAsync(app, "GET", $"/movie/{movieId}");

        h.Queue.Add("nzo-dune-2", "Dune.Part.Two.2024.2160p.WEB-DL.DDP5.1.Atmos.DV.HDR.H.265-FLUX", "movies", 20_000_000_000, 8_000_000_000);

        // The German release title shares no words with the work title; grab history still ties it to the movie (#66)
        var germanGrab = await h.GrabAsync("Dune.Teil.Zwei.2024.German.DL.1080p.BluRay.x264-GERMANS", "movies", movieId, []);
        h.Queue.Add(germanGrab, "Dune.Teil.Zwei.2024.German.DL.1080p.BluRay.x264-GERMANS", "movies", 12_000_000_000, 3_000_000_000);

        await h.CommandAsync(app, "RefreshMonitoredDownloads", new { });
        var queue = await h.SendAsync(app, "GET", "/queue", Q("includeEpisode", true));
        var germanRecord = Assert.Single(queue.Body?["records"] as JsonArray ?? [], r => r?["downloadId"]?.GetValue<string>() == germanGrab);
        Assert.Equal(movieId, germanRecord!["movieId"]!.GetValue<long>());

        var toRemove = FirstOf(await h.SendAsync(app, "GET", "/movie/lookup", Q("term", $"tmdb:{SeerrCompatHarness.DuneTmdbId}")));
        await h.SendAsync(app, "DELETE", $"/movie/{toRemove!["id"]!.GetValue<long>()}", new Dictionary<string, object?> { ["deleteFiles"] = true, ["addImportExclusion"] = false });
        var gone = await h.SendAsync(app, "GET", $"/movie/{movieId}");
        Assert.Equal(404, gone.Status);

        Verify(h, app, status, expectedAfterDelete: gone);
    }

    /// <summary>
    /// Checks every recorded response: 2xx, every field Seerr reads is present with the right casing, every call of
    /// the contract was exercised with real data, and the schema differences match known-compat-gaps.json.
    /// </summary>
    private static void Verify(SeerrCompatHarness h, string app, SeerrObservation status, SeerrObservation expectedAfterDelete)
    {
        var spec = OpenApiSpec.Load(app);
        var failures = new List<string>();
        var exercised = new HashSet<string>();
        var observed = new Dictionary<string, SortedSet<string>>();
        var details = new Dictionary<string, string>();

        foreach (var o in h.Observations.Where(o => !ReferenceEquals(o, expectedAfterDelete)))
        {
            if (o.Status is < 200 or > 299)
            {
                failures.Add($"{o}: Seerr treats non-2xx as failure. Body: {Truncate(o.Body?.ToJsonString())}");
                continue;
            }

            var readsOk = true;
            foreach (var read in o.Call.Reads ?? [])
            {
                var (problems, wasReached) = SeerrCall.CheckRead(o.Body, read);
                if (problems.Count > 0)
                {
                    readsOk = false;
                    failures.Add($"{o}: Seerr reads '{read}': {string.Join("; ", problems)}");
                }
                else if (!wasReached)
                {
                    readsOk = false;
                }
            }

            if (readsOk)
            {
                exercised.Add(o.Call.Key);
            }

            var schema = spec.SuccessSchema(o.Call.Method, o.Call.Path);
            if (schema is null || o.Body is null)
            {
                continue;
            }

            var set = observed.TryGetValue(o.Call.Key, out var s) ? s : observed[o.Call.Key] = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var issue in spec.Validate(o.Body, schema))
            {
                var key = $"{issue.Kind} {issue.Path}";
                set.Add(key);
                details.TryAdd($"{o.Call.Key}|{key}", issue.ToString());
                if (issue.IsWrongValue && (o.Call.Reads ?? []).Any(r => SameField(issue.Path, r)))
                {
                    failures.Add($"{o}: a field Seerr reads has the wrong type: {issue}");
                }
            }
        }

        var notExercised = SeerrContract.For(app).Where(c => !exercised.Contains(c.Key)).Select(c => c.Key).ToList();
        if (notExercised.Count > 0)
        {
            failures.Add("Calls the workflow did not exercise with data (every field Seerr reads must be reached at least once):\n    " +
                         string.Join("\n    ", notExercised));
        }

        var version = status.Body?["version"]?.GetValue<string>() ?? "";
        if (!int.TryParse(version.Split('.')[0], out var major) || major < (app == "sonarr" ? 4 : 5))
        {
            failures.Add($"system/status reports {app} '{version}'; Seerr changes behaviour by major version (Sonarr <= 3 needs language profiles).");
        }

        if (CompatGaps.Updating)
        {
            CompatGaps.Write(app, observed.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
        }
        else
        {
            var baseline = CompatGaps.Load().Where(kv => kv.Key.StartsWith(app + " ", StringComparison.Ordinal)).ToDictionary();
            foreach (var key in observed.Keys.Union(baseline.Keys).Order(StringComparer.Ordinal))
            {
                var now = observed.GetValueOrDefault(key) ?? [];
                var known = new SortedSet<string>(baseline.GetValueOrDefault(key) ?? [], StringComparer.Ordinal);
                var added = now.Except(known).ToList();
                var fixedOnes = known.Except(now).ToList();
                if (added.Count > 0)
                {
                    failures.Add($"{key}: new differences from the upstream schema:\n    " +
                                 string.Join("\n    ", added.Select(a => details.GetValueOrDefault($"{key}|{a}", a))));
                }

                if (fixedOnes.Count > 0)
                {
                    failures.Add($"{key}: no longer differs; remove from {CompatGaps.FileName}:\n    {string.Join("\n    ", fixedOnes)}");
                }
            }
        }

        Assert.True(failures.Count == 0,
            $"{app} compat vs Seerr {SeerrContract.Commit[..10]} / upstream {spec.Commit[..10]}: {failures.Count} problem(s)\n\n" +
            string.Join("\n\n", failures) +
            $"\n\nCalls made:\n  {string.Join("\n  ", h.Observations)}" +
            (CompatGaps.Updating ? $"\n\n{CompatGaps.FileName} was rewritten for {app}." : ""));
    }

    // "$[].seasons[].statistics.episodeFileCount" vs "[].seasons[].statistics.episodeFileCount"
    private static bool SameField(string issuePath, string read) =>
        string.Equals(issuePath.TrimStart('$').TrimStart('.'), read.TrimStart('.'), StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(string Method, string Path)> CompatRoutes(IServiceProvider services, string app)
    {
        var prefix = $"/compat/{app}/api/v3";
        return services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(m => (m, e.RoutePattern.RawText![prefix.Length..] is { Length: > 0 } p ? p : "/")))
            .Distinct();
    }

    private static Dictionary<string, object?> Q(string key, object? value) => new() { [key] = value };

    private static JsonNode? FirstOf(SeerrObservation o) => (o.Body as JsonArray)?.FirstOrDefault();

    private static long IdOf(SeerrObservation o) =>
        o.Body?["id"]?.GetValue<long>() ?? throw new Xunit.Sdk.XunitException($"{o} returned no id: {Truncate(o.Body?.ToJsonString())}");

    private static int FirstId(SeerrCompatHarness h, string app, string method, string path)
    {
        var o = h.Observations.Last(x => x.Call.App == app && x.Call.Method == method && x.Call.Path == path);
        return FirstOf(o)?["id"]?.GetValue<int>() ?? throw new Xunit.Sdk.XunitException($"{o} returned nothing to choose from");
    }

    private static string RootFolderPath(SeerrObservation folders, string expected)
    {
        var paths = (folders.Body as JsonArray ?? []).Select(f => f?["path"]?.GetValue<string>()).ToList();
        Assert.Contains(paths, p => p is not null && Path.TrimEndingDirectorySeparator(p) == Path.TrimEndingDirectorySeparator(expected));
        return expected;
    }

    private static string Truncate(string? s) => s is null ? "(none)" : s.Length > 600 ? s[..600] + "..." : s;
}
