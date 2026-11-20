using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Managerr.Catalog;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
using Managerr.Metadata;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Managerr.Phase6.Tests.ArrCompat;

/// <summary>
/// A Managerr instance with fixed metadata and a fake download client, and an HTTP client that talks to the compat
/// shims the way Seerr's ExternalAPI does: base URL + "/api/v3", the key in the ?apikey= query string, JSON bodies.
/// Every response is recorded against the Seerr contract call it answers.
/// </summary>
public sealed class SeerrCompatHarness : IAsyncDisposable
{
    public const int SeveranceTvdbId = 371980;
    public const int DuneTmdbId = 693134;

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"managerr-seerr-compat-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _http = null!;
    private string _apiKey = null!;

    public List<SeerrObservation> Observations { get; } = [];

    public FakeQueueGateway Queue { get; } = new();

    public string TvRoot => Path.Combine(_tempDir, "media", "tv");

    public string MoviesRoot => Path.Combine(_tempDir, "media", "movies");

    public IServiceProvider Services => _factory.Services;

    public static async Task<SeerrCompatHarness> StartAsync()
    {
        var h = new SeerrCompatHarness();
        Directory.CreateDirectory(h.TvRoot);
        Directory.CreateDirectory(h.MoviesRoot);

        h._factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Managerr:ConfigDirectory"] = Path.Combine(h._tempDir, "config"),
                ["Managerr:MasterKey"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                ["Managerr:SchedulerEnabled"] = "false",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IMetadataFederationService>();
                s.AddSingleton<IMetadataFederationService, FixtureMetadataFederation>();
                s.RemoveAll<IDownloadClientDriverGateway>();
                s.AddSingleton<IDownloadClientDriverGateway>(h.Queue);
            });
        });

        using (var scope = h._factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var catalog = sp.GetRequiredService<IPolymorphicCatalogService>();
            await catalog.CreateLibraryAsync(new CreateLibraryRequest("TV Shows", "tv", h.TvRoot));
            await catalog.CreateLibraryAsync(new CreateLibraryRequest("Movies", "movies", h.MoviesRoot));

            var client = await sp.GetRequiredService<IUnifiedDownloadOrchestrator>().CreateClientAsync(
                new UpsertDownloadClientRequest(Name: "SABnzbd", Implementation: "sabnzbd", Host: "sab", Port: 8080, ApiKey: "k"));
            h.Queue.ClientId = client.Id;
            h.Queue.ClientName = client.Name;

            // Seerr is set up with one key that holds both compat scopes, minted by an admin
            var users = sp.GetRequiredService<IPlexUserService>();
            var auth = sp.GetRequiredService<IPlexAuthAndSessionService>();
            var owner = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
                1, "owner-uuid", "plexowner", "owner@plex.local", null, true, true, true, ["1", "2", "3", "4"]));
            var key = await auth.CreateApiKeyAsync(owner.Id, "Seerr", [PermissionScopes.CompatSonarr, PermissionScopes.CompatRadarr]);
            h._apiKey = key.RawTokenOnce;
        }

        h._http = h._factory.CreateClient();
        return h;
    }

    /// <summary>Sends one Seerr call. <paramref name="path"/> is relative to /api/v3 and may carry concrete ids.</summary>
    public async Task<SeerrObservation> SendAsync(
        string app,
        string method,
        string path,
        IReadOnlyDictionary<string, object?>? query = null,
        object? body = null,
        string? command = null)
    {
        var call = MatchCall(app, method, path, command);
        var qs = new StringBuilder($"apikey={Uri.EscapeDataString(_apiKey)}");
        foreach (var (k, v) in query ?? new Dictionary<string, object?>())
        {
            qs.Append('&').Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(Format(v)));
        }

        var url = $"/compat/{app}/api/v3{path}?{qs}";
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null)
        {
            request.Content = body is JsonNode node
                ? new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json")
                : JsonContent.Create(body);
        }

        using var response = await _http.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        var observation = new SeerrObservation(call, url.Replace(_apiKey, "KEY"), (int)response.StatusCode, SeerrObservation.ParseBody(raw));
        Observations.Add(observation);
        return observation;
    }

    /// <summary>A grab Managerr made itself (so it has grab history), as a user would from interactive search.</summary>
    public async Task<string> GrabAsync(string title, string vertical, long workId, IReadOnlyList<long> unitIds)
    {
        using var scope = _factory.Services.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>().GrabReleaseAsync(new GrabReleaseRequest(
            title, "usenet", vertical, $"https://indexer.invalid/get/{Uri.EscapeDataString(title)}",
            WorkId: workId, UnitIds: unitIds, SourceType: "interactive"));
        return outcome.ExternalDownloadId;
    }

    public Task<SeerrObservation> CommandAsync(string app, string name, object options) =>
        SendAsync(app, "POST", "/command", body: MergeCommand(name, options), command: name);

    private static JsonObject MergeCommand(string name, object options)
    {
        var body = new JsonObject { ["name"] = name };
        foreach (var (k, v) in JsonSerializerToObject(options))
        {
            body[k] = v?.DeepClone();
        }

        return body;
    }

    private static JsonObject JsonSerializerToObject(object o) =>
        System.Text.Json.JsonSerializer.SerializeToNode(o, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.AsObject();

    private static SeerrCall MatchCall(string app, string method, string path, string? command)
    {
        var normalized = OpenApiSpec.NormalizePath(path);
        var call = SeerrContract.For(app).SingleOrDefault(c =>
            c.Method == method && c.Command == command &&
            OpenApiSpec.NormalizePath(c.Path) == Generalize(normalized));
        return call ?? throw new InvalidOperationException($"{app} {method} {path} {command} is not a call in seerr-contract.json");
    }

    // "/series/7" -> "/series/{}" so concrete ids match the contract's "/series/{id}"
    private static string Generalize(string normalizedPath) =>
        string.Join('/', normalizedPath.Split('/').Select(s => s.Length > 0 && s.All(char.IsDigit) ? "{}" : s));

    private static string Format(object? v) => v switch
    {
        null => "",
        bool b => b ? "true" : "false",
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A download client whose queue the test controls.</summary>
    public sealed class FakeQueueGateway : IDownloadClientDriverGateway
    {
        public long ClientId { get; set; }

        public string ClientName { get; set; } = "";

        public List<ActiveDownloadQueueItem> Items { get; } = [];

        public void Add(string externalId, string title, string vertical, long total, long remaining) =>
            Items.Add(new ActiveDownloadQueueItem(
                ClientId, ClientName, "usenet", externalId, $"nzo:{externalId}", title, vertical, vertical,
                "Downloading", total, remaining, "", [], false, false, null));

        public IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers() => DownloadClientCatalog.All;

        public Task<string> EnqueueReleaseAsync(DownloadClientConnectionSpec client, string releaseTitle, string downloadUrlOrMagnet, string resolvedCategory, CancellationToken ct = default) =>
            Task.FromResult($"nzo-{Guid.NewGuid():N}");

        public Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(DownloadClientConnectionSpec client, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActiveDownloadQueueItem>>(Items.ToList());

        public Task RemoveDownloadAsync(DownloadClientConnectionSpec client, string externalId, bool deleteData, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>Offline metadata: one series and one movie, found by id prefix ("tvdb:371980") or title.</summary>
    private sealed class FixtureMetadataFederation : IMetadataFederationService
    {
        private static readonly MetadataWorkCandidate Severance = new(
            "tvdb", SeveranceTvdbId.ToString(), "tv", "Severance", [], "en", 2022, "continuing",
            "Mark leads a team of office workers whose memories have been surgically divided.", null,
            new Dictionary<string, string> { ["tvdb"] = SeveranceTvdbId.ToString(), ["tmdb"] = "95396", ["imdb"] = "tt11280740" },
            [Season(1, 9, "2022-02-18"), Season(2, 10, "2025-01-17")],
            Genres: ["Drama"]);

        private static readonly MetadataWorkCandidate Dune = new(
            "tmdb", DuneTmdbId.ToString(), "movies", "Dune: Part Two", [], "en", 2024, "released",
            "Paul Atreides unites with Chani and the Fremen.", null,
            new Dictionary<string, string> { ["tmdb"] = DuneTmdbId.ToString(), ["imdb"] = "tt15239678" },
            [new MetadataContainerSeed("movie", 1, "Dune: Part Two", "2024-03-01", [new MetadataUnitSeed("movie", 1, null, "Dune: Part Two", "2024-03-01")])],
            Genres: ["Science Fiction"]);

        private static MetadataContainerSeed Season(int number, int episodes, string firstAired) =>
            new("season", number, $"Season {number}", firstAired,
                Enumerable.Range(1, episodes)
                    .Select(e => new MetadataUnitSeed("episode", e, null, $"Episode {e}", DateTime.Parse(firstAired).AddDays(7 * (e - 1)).ToString("yyyy-MM-dd")))
                    .ToList());

        public IReadOnlyList<MetadataProviderDescriptor> ListBuiltInProviders(string? vertical = null) => [];

        public IReadOnlyList<string> GetDefaultWaterfall(string vertical) => [];

        public Task<IReadOnlyList<MetadataWorkCandidate>> SearchWaterfallAsync(string query, string vertical, IReadOnlyList<string>? customWaterfall = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataWorkCandidate>>(Find(query, vertical) is { } hit ? [hit] : []);

        public Task<MetadataWorkCandidate?> ResolveWorkAsync(string vertical, string? preferredProvider, IReadOnlyDictionary<string, string> knownExternalIds, IReadOnlyList<string>? customWaterfall = null, CancellationToken ct = default) =>
            ResolveWorkAsync(vertical, preferredProvider, knownExternalIds, customWaterfall, false, ct);

        public Task<MetadataWorkCandidate?> ResolveWorkAsync(string vertical, string? preferredProvider, IReadOnlyDictionary<string, string> knownExternalIds, IReadOnlyList<string>? customWaterfall, bool forceRefresh, CancellationToken ct = default)
        {
            var hit = new[] { Severance, Dune }.FirstOrDefault(c => knownExternalIds.Any(k => c.ExternalIds.TryGetValue(k.Key, out var v) && v == k.Value));
            return Task.FromResult(hit);
        }

        public MergedWorkMetadataFields ApplyFieldMergeWithLocks(MergedWorkMetadataFields current, MetadataWorkCandidate incoming, IReadOnlySet<string> lockedFields) =>
            new(
                lockedFields.Contains("title") ? current.Title : incoming.Title,
                incoming.AltTitles,
                incoming.OriginalLanguage,
                incoming.Year,
                incoming.Status,
                incoming.Overview,
                incoming.PosterUrl,
                incoming.ExternalIds);

        private static MetadataWorkCandidate? Find(string query, string vertical)
        {
            var q = query.Trim();
            var tv = vertical is "tv" or "anime";
            if (q.Contains(':'))
            {
                var parts = q.Split(':', 2);
                var hit = tv ? Severance : Dune;
                return hit.ExternalIds.TryGetValue(parts[0].ToLowerInvariant(), out var id) && id == parts[1] ? hit : null;
            }

            var byTitle = tv ? Severance : Dune;
            return byTitle.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ? byTitle : null;
        }
    }
}
