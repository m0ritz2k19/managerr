using System.Net;
using System.Text;
using Managerr.Catalog;
using Managerr.Common.Net;
using Managerr.Downloaders.Clients;
using Managerr.Indexers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Managerr.Phase5.Tests.Support;

/// <summary>
/// A Managerr host for service-level tests, with optional fakes for outgoing HTTP, the indexers and the download
/// client. Services are resolved from one scope that lives as long as the harness.
/// </summary>
public sealed class HostTestHarness : IAsyncDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"managerr-host-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private IServiceScope _scope = null!;

    public string MediaRoot => Path.Combine(_tempDir, "media");

    public T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    public static async Task<HostTestHarness> StartAsync(
        RecordingHttp? http = null,
        IUnifiedIndexerService? indexer = null,
        IDownloadClientDriverGateway? downloadClient = null)
    {
        var h = new HostTestHarness();
        Directory.CreateDirectory(Path.Combine(h.MediaRoot, "tv"));
        Directory.CreateDirectory(Path.Combine(h.MediaRoot, "movies"));
        h._factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Managerr:ConfigDirectory"] = Path.Combine(h._tempDir, "config"),
                ["Managerr:MasterKey"] = "host-test-harness-master-key-32bytes!",
                ["Managerr:SchedulerEnabled"] = "false",
            }));
            b.ConfigureServices(s =>
            {
                if (http is not null)
                {
                    s.RemoveAll<ISafeHttpClientFactory>();
                    s.AddSingleton<ISafeHttpClientFactory>(http);
                }

                if (indexer is not null)
                {
                    s.RemoveAll<IUnifiedIndexerService>();
                    s.AddSingleton(indexer);
                }

                if (downloadClient is not null)
                {
                    s.RemoveAll<IDownloadClientDriverGateway>();
                    s.AddSingleton(downloadClient);
                }
            });
        });

        h._scope = h._factory.Services.CreateScope();
        await Task.CompletedTask;
        return h;
    }

    public async Task<LibrarySummary> LibraryAsync(string mediaType) =>
        await Get<IPolymorphicCatalogService>().CreateLibraryAsync(
            new CreateLibraryRequest(mediaType == "tv" ? "TV" : "Movies", mediaType, Path.Combine(MediaRoot, mediaType)));

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>
/// Stands in for <see cref="ISafeHttpClientFactory"/>: every outgoing request is recorded and answered by
/// <see cref="Responder"/> (200 with an empty JSON object by default).
/// </summary>
public sealed class RecordingHttp : ISafeHttpClientFactory
{
    private readonly Handler _handler;

    public RecordingHttp() => _handler = new Handler(this);

    public Func<HttpRequestMessage, string, HttpResponseMessage> Responder { get; set; } =
        (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

    public List<RecordedHttpRequest> Requests { get; } = [];

    public HttpClient CreatePublicClient(bool ignoreSslErrors = false) => new(_handler, disposeHandler: false);

    public HttpClient CreateHomelabClient(bool allowPrivateLan = true, bool ignoreSslErrors = false) => new(_handler, disposeHandler: false);

    public void EnsureAddressPermitted(IPAddress address, string? targetHost, bool allowPrivateLan)
    {
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(RecordingHttp owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (owner.Requests)
            {
                owner.Requests.Add(new RecordedHttpRequest(
                    request.Method.Method,
                    request.RequestUri!,
                    request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
                    body));
            }

            return owner.Responder(request, body);
        }
    }
}

public sealed record RecordedHttpRequest(string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body);
