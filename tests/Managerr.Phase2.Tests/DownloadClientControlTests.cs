using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Managerr.Common.Net;
using Managerr.Downloaders.Clients;
using Microsoft.Extensions.Options;
using Xunit;

namespace Managerr.Phase2.Tests;

/// <summary>
/// #68: removing and pausing/resuming downloads on NZBGet, Transmission and aria2, against fake JSON-RPC servers that
/// answer like the real ones. Before, these calls returned without doing anything.
/// </summary>
public sealed class DownloadClientControlTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    // ---------------------------------------------------------------- NZBGet

    [Fact]
    public async Task Nzbget_RemovesAQueuedJob_WithGroupFinalDelete()
    {
        await using var nzbget = FakeRpcServer.Start((_, _) => Rpc(true));

        await Gateway().RemoveDownloadAsync(nzbget.Spec("nzbget", "usenet"), "42", deleteData: true);

        var call = Assert.Single(nzbget.Requests);
        Assert.Equal("editqueue", call.Body?["method"]?.GetValue<string>());
        Assert.Equal("""["GroupFinalDelete","",[42]]""", call.Body?["params"]?.ToJsonString());
    }

    [Theory]
    [InlineData(true, "HistoryFinalDelete")]
    [InlineData(false, "HistoryDelete")]
    public async Task Nzbget_RemovesAFinishedJob_FromTheHistory(bool deleteData, string historyCommand)
    {
        // A finished job is no longer in the queue, so GroupFinalDelete answers false
        await using var nzbget = FakeRpcServer.Start((_, body) =>
            Rpc(body?["params"]?[0]?.GetValue<string>() != "GroupFinalDelete"));

        await Gateway().RemoveDownloadAsync(nzbget.Spec("nzbget", "usenet"), "nzbget:42", deleteData);

        Assert.Equal(["GroupFinalDelete", historyCommand], nzbget.Requests.Select(r => r.Body?["params"]?[0]?.GetValue<string>()));
    }

    [Theory]
    [InlineData("pause", "GroupPause", "")]
    [InlineData("resume", "GroupResume", "")]
    [InlineData("force_start", "GroupSetPriority", "900")]
    [InlineData("top_priority", "GroupMoveTop", "")]
    public async Task Nzbget_QueueActions(string action, string command, string param)
    {
        await using var nzbget = FakeRpcServer.Start((_, _) => Rpc(true));

        await Gateway().ControlDownloadAsync(nzbget.Spec("nzbget", "usenet"), "42", action);

        var call = Assert.Single(nzbget.Requests);
        Assert.Equal($$"""["{{command}}","{{param}}",[42]]""", call.Body?["params"]?.ToJsonString());
    }

    [Fact]
    public async Task Nzbget_RejectedAction_Throws()
    {
        await using var nzbget = FakeRpcServer.Start((_, _) => Rpc(false));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Gateway().ControlDownloadAsync(nzbget.Spec("nzbget", "usenet"), "42", "pause"));
        Assert.Contains("GroupPause", ex.Message);
    }

    // ---------------------------------------------------------- Transmission

    [Fact]
    public async Task Transmission_RemovesByHash_AfterTheSessionIdHandshake()
    {
        await using var tr = FakeRpcServer.Start(TransmissionHandler("success"));

        await Gateway().RemoveDownloadAsync(tr.Spec("transmission", "torrent"), Hash, deleteData: true);

        var call = tr.Requests.Last();
        Assert.Equal("torrent-remove", call.Body?["method"]?.GetValue<string>());
        Assert.Equal($$"""{"ids":["{{Hash}}"],"delete-local-data":true}""", call.Body?["arguments"]?.ToJsonString());
        Assert.Equal("sid-1", call.SessionId);
    }

    [Theory]
    [InlineData("pause", "torrent-stop")]
    [InlineData("resume", "torrent-start")]
    [InlineData("force_start", "torrent-start-now")]
    [InlineData("top_priority", "queue-move-top")]
    public async Task Transmission_QueueActions(string action, string method)
    {
        await using var tr = FakeRpcServer.Start(TransmissionHandler("success"));

        await Gateway().ControlDownloadAsync(tr.Spec("transmission", "torrent"), Hash, action);

        Assert.Equal(method, tr.Requests.Last().Body?["method"]?.GetValue<string>());
    }

    [Fact]
    public async Task Transmission_ErrorResult_Throws()
    {
        // Transmission reports failures as HTTP 200 with a result other than "success"
        await using var tr = FakeRpcServer.Start(TransmissionHandler("invalid argument"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Gateway().RemoveDownloadAsync(tr.Spec("transmission", "torrent"), Hash, deleteData: false));
        Assert.Contains("invalid argument", ex.Message);
    }

    [Theory]
    [InlineData("torrent-added")]
    [InlineData("torrent-duplicate")]
    public async Task Transmission_Add_ReturnsTheTorrentHash_AsTheDownloadId(string key)
    {
        await using var tr = FakeRpcServer.Start((headers, _) => headers.Get("X-Transmission-Session-Id") is null
            ? (409, "", new Dictionary<string, string> { ["X-Transmission-Session-Id"] = "sid-1" })
            : (200, "{\"result\":\"success\",\"arguments\":{\"" + key + "\":{\"id\":7,\"name\":\"x\",\"hashString\":\"" + Hash.ToUpperInvariant() + "\"}}}", null));

        var id = await Gateway().EnqueueReleaseAsync(tr.Spec("transmission", "torrent"), "Some.Release", "https://indexer.invalid/a.torrent", "tv");

        Assert.Equal(Hash, id);
    }

    // ----------------------------------------------------------------- aria2

    [Fact]
    public async Task Aria2_RemovesAFinishedDownload_WithRemoveDownloadResult_AndSendsTheSecret()
    {
        // forceRemove fails for a download that already finished
        await using var aria2 = FakeRpcServer.Start((_, body) => body?["method"]?.GetValue<string>() == "aria2.forceRemove"
            ? (200, """{"jsonrpc":"2.0","id":"managerr","error":{"code":1,"message":"GID is not found"}}""", null)
            : (200, """{"jsonrpc":"2.0","id":"managerr","result":"OK"}""", null));

        await Gateway().RemoveDownloadAsync(aria2.Spec("aria2", "torrent", apiKey: "s3cret"), "2089b05ecca3d829", deleteData: false);

        Assert.Equal(["aria2.forceRemove", "aria2.removeDownloadResult"], aria2.Requests.Select(r => r.Body?["method"]?.GetValue<string>()));
        Assert.All(aria2.Requests, r => Assert.Equal("""["token:s3cret","2089b05ecca3d829"]""", r.Body?["params"]?.ToJsonString()));
    }

    [Theory]
    [InlineData("pause", "aria2.pause")]
    [InlineData("resume", "aria2.unpause")]
    [InlineData("top_priority", "aria2.changePosition")]
    public async Task Aria2_QueueActions(string action, string method)
    {
        await using var aria2 = FakeRpcServer.Start((_, _) => (200, """{"jsonrpc":"2.0","id":"managerr","result":"OK"}""", null));

        await Gateway().ControlDownloadAsync(aria2.Spec("aria2", "torrent"), "2089b05ecca3d829", action);

        Assert.Equal(method, Assert.Single(aria2.Requests).Body?["method"]?.GetValue<string>());
    }

    // ------------------------------------------------------- unsupported drivers

    [Theory]
    [InlineData("utorrent")]
    [InlineData("rqbit")]
    [InlineData("nzbvortex")]
    public async Task DriversWithoutAnImplementation_SayIt_InsteadOfDoingNothing(string implementation)
    {
        await using var server = FakeRpcServer.Start((_, _) => (200, "{}", null));
        var spec = server.Spec(implementation, "torrent");

        var enqueue = await Assert.ThrowsAsync<DownloadClientActionNotSupportedException>(() =>
            Gateway().EnqueueReleaseAsync(spec, "Release", "magnet:?xt=urn:btih:0123456789abcdef0123", "tv"));
        var poll = await Assert.ThrowsAsync<DownloadClientActionNotSupportedException>(() =>
            Gateway().FetchQueueAsync(spec));
        var remove = await Assert.ThrowsAsync<DownloadClientActionNotSupportedException>(() =>
            Gateway().RemoveDownloadAsync(spec, "x", deleteData: true));
        var control = await Assert.ThrowsAsync<DownloadClientActionNotSupportedException>(() =>
            Gateway().ControlDownloadAsync(spec, "x", "pause"));

        Assert.Contains("does not support adding downloads", enqueue.Message);
        Assert.Contains("does not support polling the download queue", poll.Message);
        Assert.Contains("does not support removing downloads", remove.Message);
        Assert.Contains("'pause'", control.Message);
        Assert.Empty(server.Requests);
    }

    private static DownloadClientDriverGateway Gateway() => new(new SafeHttpClientFactory(Options.Create(new SafeHttpEgressOptions())));

    private static (int, string, Dictionary<string, string>?) Rpc(bool result) =>
        (200, "{\"version\":\"1.1\",\"result\":" + (result ? "true" : "false") + "}", null);

    private static Func<System.Collections.Specialized.NameValueCollection, JsonNode?, (int, string, Dictionary<string, string>?)> TransmissionHandler(string result) =>
        (headers, _) => headers.Get("X-Transmission-Session-Id") != "sid-1"
            ? (409, "", new Dictionary<string, string> { ["X-Transmission-Session-Id"] = "sid-1" })
            : (200, "{\"result\":\"" + result + "\",\"arguments\":{}}", null);

    private sealed record RecordedRequest(string Path, JsonNode? Body, string? SessionId);

    /// <summary>A loopback JSON-RPC server whose answers the test decides; records every request after the handshake.</summary>
    private sealed class FakeRpcServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<System.Collections.Specialized.NameValueCollection, JsonNode?, (int Status, string Body, Dictionary<string, string>? Headers)> _handler;
        private readonly Task _loop;

        private FakeRpcServer(int port, Func<System.Collections.Specialized.NameValueCollection, JsonNode?, (int, string, Dictionary<string, string>?)> handler)
        {
            Port = port;
            _handler = handler;
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                _listener.Start();
            }
            catch (HttpListenerException)
            {
                _listener.Close();
                throw;
            }

            _loop = Task.Run(LoopAsync);
        }

        public int Port { get; }

        public List<RecordedRequest> Requests { get; } = [];

        public static FakeRpcServer Start(Func<System.Collections.Specialized.NameValueCollection, JsonNode?, (int, string, Dictionary<string, string>?)> handler)
        {
            for (var attempt = 1; ; attempt++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                try
                {
                    return new FakeRpcServer(port, handler);
                }
                catch (HttpListenerException) when (attempt < 5)
                {
                }
            }
        }

        public DownloadClientConnectionSpec Spec(string implementation, string protocol, string? apiKey = null) =>
            new(1, $"Fake {implementation}", implementation, protocol, "127.0.0.1", Port, false, null, "user", "pass", apiKey,
                new Dictionary<string, string> { ["tv"] = "tv" }, new Dictionary<string, string>());

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception) when (!_listener.IsListening)
                {
                    return;
                }

                try
                {
                    using var reader = new StreamReader(ctx.Request.InputStream);
                    var raw = await reader.ReadToEndAsync();
                    var body = string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw);
                    var (status, responseBody, headers) = _handler(ctx.Request.Headers, body);
                    if (status != 409)
                    {
                        lock (Requests)
                        {
                            Requests.Add(new RecordedRequest(ctx.Request.Url!.AbsolutePath, body, ctx.Request.Headers.Get("X-Transmission-Session-Id")));
                        }
                    }

                    foreach (var (k, v) in headers ?? [])
                    {
                        ctx.Response.Headers.Add(k, v);
                    }

                    var bytes = Encoding.UTF8.GetBytes(responseBody);
                    ctx.Response.StatusCode = status;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                }
                finally
                {
                    ctx.Response.Close();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Close only: on Linux, Close after Stop re-binds the port (see QbittorrentDriverTests)
            _listener.Close();
            try { await _loop; } catch { /* listener shut down */ }
        }
    }
}
