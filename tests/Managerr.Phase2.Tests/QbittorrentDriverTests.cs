using System.Net;
using System.Net.Sockets;
using System.Text;
using Managerr.Common.Net;
using Managerr.Downloaders.Clients;
using Microsoft.Extensions.Options;
using Xunit;

namespace Managerr.Phase2.Tests;

/// <summary>
/// The qBittorrent driver against a fake qBittorrent on loopback that behaves like the real WebUI API: /auth/login
/// answers 200 "Ok." with an SID cookie (or 200 "Fails." for bad credentials), and every other endpoint is 403 without
/// that cookie. Uses the real <see cref="SafeHttpClientFactory"/>, so cookie handling is the production one (#63).
/// </summary>
public sealed class QbittorrentDriverTests
{
    public static TheoryData<string> Operations() => new(["add", "queue", "remove", "pause"]);

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task EveryCall_LogsInFirst_WhenTheWebUiRequiresAuthentication(string operation)
    {
        await using var qbit = FakeQbittorrent.Start(requireAuth: true);
        var gateway = new DownloadClientDriverGateway(Factory());
        var client = qbit.Spec(username: "admin", password: "adminadmin");

        await RunAsync(gateway, client, operation);

        Assert.True(qbit.Forbidden.Count == 0, $"calls without a session: {string.Join(", ", qbit.Forbidden)}");
        Assert.Contains(qbit.Authorized, p => p == ExpectedPath(operation));
    }

    [Fact]
    public async Task Queue_IsRead_WhenTheWebUiRequiresAuthentication()
    {
        await using var qbit = FakeQbittorrent.Start(requireAuth: true);
        var gateway = new DownloadClientDriverGateway(Factory());

        var queue = await gateway.FetchQueueAsync(qbit.Spec(username: "admin", password: "adminadmin"));

        var item = Assert.Single(queue);
        Assert.Equal(FakeQbittorrent.Hash, item.ExternalId);
        Assert.Equal("Severance.S01E02.1080p.WEB-DL-GRP", item.Title);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task WrongPassword_FailsWithAClearMessage(string operation)
    {
        await using var qbit = FakeQbittorrent.Start(requireAuth: true);
        var gateway = new DownloadClientDriverGateway(Factory());

        // qBittorrent answers a bad login with 200 "Fails.", not an error status
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(gateway, qbit.Spec(username: "admin", password: "wrong"), operation));

        Assert.Contains("rejected the username or password", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wrong", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task NoCredentials_SkipLogin_ForWebUisThatBypassAuthentication(string operation)
    {
        // "Bypass authentication for clients in whitelisted IP subnets": no username configured, no login sent
        await using var qbit = FakeQbittorrent.Start(requireAuth: false);
        var gateway = new DownloadClientDriverGateway(Factory());

        await RunAsync(gateway, qbit.Spec(username: null, password: null), operation);

        Assert.Equal(0, qbit.LoginAttempts);
        Assert.Contains(qbit.Authorized, p => p == ExpectedPath(operation));
    }

    [Theory]
    [InlineData("2.11.2", "/api/v2/torrents/stop", "/api/v2/torrents/start")]
    [InlineData("2.8.3", "/api/v2/torrents/pause", "/api/v2/torrents/resume")]
    public async Task PauseAndResume_UseTheEndpointsOfTheWebUiVersion(string webApiVersion, string pausePath, string resumePath)
    {
        // #69: qBittorrent 5.x only knows stop/start, 4.x only pause/resume
        await using var qbit = FakeQbittorrent.Start(requireAuth: true, webApiVersion);
        var gateway = new DownloadClientDriverGateway(Factory());
        var client = qbit.Spec(username: "admin", password: "adminadmin");

        await gateway.ControlDownloadAsync(client, FakeQbittorrent.Hash, "pause");
        await gateway.ControlDownloadAsync(client, FakeQbittorrent.Hash, "resume");

        Assert.Contains(pausePath, qbit.Authorized);
        Assert.Contains(resumePath, qbit.Authorized);
    }

    private static async Task RunAsync(DownloadClientDriverGateway gateway, DownloadClientConnectionSpec client, string operation)
    {
        switch (operation)
        {
            case "add":
                await gateway.EnqueueReleaseAsync(client, "Severance.S01E02.1080p.WEB-DL-GRP", $"magnet:?xt=urn:btih:{FakeQbittorrent.Hash}", "tv-managerr");
                break;
            case "queue":
                await gateway.FetchQueueAsync(client);
                break;
            case "remove":
                await gateway.RemoveDownloadAsync(client, FakeQbittorrent.Hash, deleteData: true);
                break;
            case "pause":
                await gateway.ControlDownloadAsync(client, FakeQbittorrent.Hash, "pause");
                break;
        }
    }

    private static string ExpectedPath(string operation) => operation switch
    {
        "add" => "/api/v2/torrents/add",
        "queue" => "/api/v2/torrents/info",
        "remove" => "/api/v2/torrents/delete",
        _ => "/api/v2/torrents/pause",
    };

    private static SafeHttpClientFactory Factory() => new(Options.Create(new SafeHttpEgressOptions()));

    private sealed class FakeQbittorrent : IAsyncDisposable
    {
        public const string Hash = "0123456789abcdef0123456789abcdef01234567";
        private const string Sid = "fake-session-7f3a";

        private readonly HttpListener _listener = new();
        private readonly bool _requireAuth;
        private readonly Version _webApiVersion;
        private readonly Task _loop;

        private FakeQbittorrent(int port, bool requireAuth, string webApiVersion)
        {
            Port = port;
            _requireAuth = requireAuth;
            _webApiVersion = Version.Parse(webApiVersion);
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

        public int LoginAttempts { get; private set; }

        public List<string> Authorized { get; } = [];

        public List<string> Forbidden { get; } = [];

        public static FakeQbittorrent Start(bool requireAuth, string webApiVersion = "2.8.3")
        {
            // The free port is released before HttpListener binds it, so another test can take it in between; retry
            for (var attempt = 1; ; attempt++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                try
                {
                    return new FakeQbittorrent(port, requireAuth, webApiVersion);
                }
                catch (HttpListenerException) when (attempt < 5)
                {
                }
            }
        }

        public DownloadClientConnectionSpec Spec(string? username, string? password) =>
            new(1, "qBittorrent", "qbittorrent", "torrent", "127.0.0.1", Port, false, null, username, password, null,
                new Dictionary<string, string> { ["tv"] = "tv-managerr" }, new Dictionary<string, string>());

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
                    Handle(ctx);
                }
                finally
                {
                    ctx.Response.Close();
                }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url!.AbsolutePath;
            if (path == "/api/v2/auth/login")
            {
                LoginAttempts++;
                using var reader = new StreamReader(ctx.Request.InputStream);
                var form = System.Web.HttpUtility.ParseQueryString(reader.ReadToEnd());
                if (form["username"] == "admin" && form["password"] == "adminadmin")
                {
                    ctx.Response.Headers.Add("Set-Cookie", $"SID={Sid}; HttpOnly; SameSite=Strict; path=/");
                    Write(ctx, 200, "Ok.");
                }
                else
                {
                    Write(ctx, 200, "Fails.");
                }

                return;
            }

            if (_requireAuth && ctx.Request.Cookies["SID"]?.Value != Sid)
            {
                lock (Forbidden)
                {
                    Forbidden.Add(path);
                }

                Write(ctx, 403, "Forbidden");
                return;
            }

            lock (Authorized)
            {
                Authorized.Add(path);
            }

            if (path == "/api/v2/app/webapiVersion")
            {
                Write(ctx, 200, _webApiVersion.ToString());
                return;
            }

            // qBittorrent 5 (WebAPI 2.11) renamed pause/resume to stop/start and dropped the old endpoints
            var renamed = _webApiVersion >= new Version(2, 11);
            if ((renamed && path is "/api/v2/torrents/pause" or "/api/v2/torrents/resume") ||
                (!renamed && path is "/api/v2/torrents/stop" or "/api/v2/torrents/start"))
            {
                Write(ctx, 404, "Not Found");
                return;
            }

            if (path == "/api/v2/torrents/info")
            {
                Write(ctx, 200, $$"""
                    [{"hash":"{{Hash}}","name":"Severance.S01E02.1080p.WEB-DL-GRP","category":"tv-managerr","state":"downloading",
                      "size":1000,"amount_left":400,"content_path":"/downloads/Severance.S01E02.1080p.WEB-DL-GRP.mkv"}]
                    """, "application/json");
                return;
            }

            Write(ctx, 200, "Ok.");
        }

        private static void Write(HttpListenerContext ctx, int status, string body, string contentType = "text/plain")
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            ctx.Response.OutputStream.Write(bytes);
        }

        public async ValueTask DisposeAsync()
        {
            // Close only: on Linux the managed HttpListener re-binds the port when Close follows Stop, which fails
            // with "Address already in use" once another test has taken that port (seen in CI)
            _listener.Close();
            try { await _loop; } catch { /* listener shut down */ }
        }
    }
}
