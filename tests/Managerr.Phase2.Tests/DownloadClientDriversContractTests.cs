using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dapper;
using Managerr.Common.Net;
using Managerr.Indexers.Categories;
using Managerr.Downloaders.Clients;
using Managerr.Migration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Managerr.Phase2.Tests;

/// <summary>
/// #38: Recorded JSON-RPC, XML-RPC, and REST contract tests for aria2 queue polling, Deluge, rTorrent, and Flood,
/// plus catalog pruning verification (12 genuine clients, zero fake /api/enqueue placeholders).
/// </summary>
public sealed class DownloadClientDriversContractTests
{
    private const string SampleHash = "fedcba9876543210fedcba9876543210fedcba98";
    private const string SampleMagnet = "magnet:?xt=urn:btih:FEDCBA9876543210FEDCBA9876543210FEDCBA98&dn=Show.S01E01.1080p";

    private static readonly byte[] SampleBencodedTorrent =
        Encoding.ASCII.GetBytes("d8:announce27:https://tracker.invalid/ann4:infod6:lengthi1048576e4:name16:Show.S01E01.mkv12:piece lengthi262144e6:pieces20:01234567890123456789ee");

    [Fact]
    public void Catalog_ExposesOnly12GenuineDrivers_AndPrunesAll9Placeholders()
    {
        var keys = DownloadClientCatalog.All.Select(d => d.Implementation).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(12, DownloadClientCatalog.All.Count);

        string[] expectedSupported =
        [
            "qbittorrent", "deluge", "transmission", "rtorrent", "flood", "vuze", "aria2", "torrent_blackhole",
            "sabnzbd", "nzbget", "pneumatic", "usenet_blackhole",
        ];
        foreach (var k in expectedSupported)
        {
            Assert.Contains(k, keys);
        }

        string[] prunedPlaceholders =
        [
            "utorrent", "hadouken", "rqbit", "tribler", "download_station_torrent",
            "freebox_download", "nzbvortex", "usenet_download_station", "freebox_usenet",
        ];
        foreach (var pruned in prunedPlaceholders)
        {
            Assert.DoesNotContain(pruned, keys);
        }
    }

    [Fact]
    public async Task Aria2_QueuePolling_BytesEnqueue_AndTestConnection_HonorSecretToken()
    {
        await using var server = LoopbackWireServer.Start(req =>
        {
            var body = JsonNode.Parse(req.Body)!;
            var method = body["method"]!.GetValue<string>();
            return method switch
            {
                "aria2.getVersion" => LoopbackResponse.Json("""{"jsonrpc":"2.0","id":"managerr","result":{"version":"1.37.0"}}"""),
                "aria2.addTorrent" => LoopbackResponse.Json("""{"jsonrpc":"2.0","id":"managerr","result":"2089b05ecca3d829"}"""),
                "aria2.tellActive" => LoopbackResponse.Json($$"""
                    {
                      "jsonrpc": "2.0",
                      "id": "managerr",
                      "result": [
                        {
                          "gid": "2089b05ecca3d829",
                          "status": "active",
                          "totalLength": "2097152000",
                          "completedLength": "524288000",
                          "dir": "/downloads/tv-managerr",
                          "infoHash": "{{SampleHash}}",
                          "bittorrent": { "info": { "name": "Severance.S01E01.1080p.WEB-DL" } },
                          "files": [ { "path": "/downloads/tv-managerr/Severance.S01E01.1080p.WEB-DL.mkv" } ]
                        }
                      ]
                    }
                    """),
                "aria2.tellWaiting" => LoopbackResponse.Json("""
                    {
                      "jsonrpc": "2.0",
                      "id": "managerr",
                      "result": [
                        {
                          "gid": "3111c05ecca3d830",
                          "status": "paused",
                          "totalLength": "1048576000",
                          "completedLength": "0",
                          "dir": "/downloads/anime-managerr",
                          "files": [ { "path": "/downloads/anime-managerr/[SubsPlease] Frieren - 01 (1080p).mkv" } ]
                        }
                      ]
                    }
                    """),
                "aria2.tellStopped" => LoopbackResponse.Json("""
                    {
                      "jsonrpc": "2.0",
                      "id": "managerr",
                      "result": [
                        {
                          "gid": "4222d05ecca3d831",
                          "status": "complete",
                          "totalLength": "734003200",
                          "completedLength": "734003200",
                          "dir": "/downloads/movies-managerr",
                          "infoHash": "1111222233334444555566667777888899990000",
                          "bittorrent": { "info": { "name": "Dune.Part.Two.2024.1080p" } },
                          "files": [ { "path": "/downloads/movies-managerr/Dune.Part.Two.2024.1080p.mkv" } ]
                        }
                      ]
                    }
                    """),
                _ => LoopbackResponse.Json("""{"jsonrpc":"2.0","id":"managerr","result":"OK"}"""),
            };
        });

        var spec = server.Spec("aria2", "torrent", apiKey: "rpc-s3cret");
        var gw = Gateway();

        var testRes = await gw.TestConnectionAsync(spec);
        Assert.True(testRes.Ok);
        Assert.Equal("1.37.0", testRes.Version);

        var gid = await gw.EnqueueReleaseBytesAsync(spec, "Show.S01E01", SampleBencodedTorrent, "tv-managerr");
        Assert.Equal("2089b05ecca3d829", gid);

        var queue = await gw.FetchQueueAsync(spec);
        Assert.Equal(3, queue.Count);

        var active = Assert.Single(queue, q => q.ExternalId == "2089b05ecca3d829");
        Assert.Equal("Severance.S01E01.1080p.WEB-DL", active.Title);
        Assert.Equal(SampleHash, active.InfoHash);
        Assert.Equal("Downloading", active.Status);
        Assert.Equal(2097152000L - 524288000L, active.RemainingBytes);
        Assert.Equal("tv-managerr", active.Category);
        Assert.Equal(MediaVerticals.Tv, active.MediaVertical);

        var paused = Assert.Single(queue, q => q.ExternalId == "3111c05ecca3d830");
        Assert.Equal("Paused", paused.Status);
        Assert.Equal(MediaVerticals.Anime, paused.MediaVertical);

        var completed = Assert.Single(queue, q => q.ExternalId == "4222d05ecca3d831");
        Assert.Equal("Completed", completed.Status);
        Assert.Equal(0L, completed.RemainingBytes);
        Assert.True(completed.CanMoveFiles);

        // Verify all JSON-RPC calls included token:rpc-s3cret as the first parameter
        foreach (var req in server.Requests)
        {
            var p0 = JsonNode.Parse(req.Body)!["params"]![0]!.GetValue<string>();
            Assert.Equal("token:rpc-s3cret", p0);
        }
    }

    [Fact]
    public async Task Deluge_SupportsGzipJsonRpc_SessionCookie_EnqueueUrlAndBytes_Label_Queue_Control_AndRemove()
    {
        await using var server = LoopbackWireServer.Start(req =>
        {
            var body = JsonNode.Parse(req.Body)!;
            var method = body["method"]!.GetValue<string>();
            if (method == "auth.login")
            {
                var pw = body["params"]![0]!.GetValue<string>();
                if (pw != "deluge-pass")
                {
                    return LoopbackResponse.Json("""{"id":1,"result":false,"error":null}""");
                }

                return LoopbackResponse.GzipJson(
                    """{"id":1,"result":true,"error":null}""",
                    new Dictionary<string, string> { ["Set-Cookie"] = "_session_id=deluge_sid_99; Path=/json; HttpOnly" });
            }

            // Require session cookie on every authenticated Deluge call
            if (!(req.Headers.Get("Cookie")?.Contains("_session_id=deluge_sid_99", StringComparison.Ordinal) ?? false))
            {
                return LoopbackResponse.Json("""{"id":1,"result":null,"error":{"message":"Not authenticated","code":1}}""");
            }

            return method switch
            {
                "daemon.info" => LoopbackResponse.GzipJson("""{"id":1,"result":"2.1.1","error":null}"""),
                "core.add_torrent_url" => LoopbackResponse.GzipJson($$"""{"id":1,"result":"{{SampleHash}}","error":null}"""),
                "core.add_torrent_file" => LoopbackResponse.GzipJson($$"""{"id":1,"result":"{{SampleHash}}","error":null}"""),
                "label.set_torrent" => LoopbackResponse.GzipJson("""{"id":1,"result":null,"error":null}"""),
                "web.update_ui" => LoopbackResponse.GzipJson($$"""
                    {
                      "id": 1,
                      "result": {
                        "connected": true,
                        "torrents": {
                          "{{SampleHash}}": {
                            "hash": "{{SampleHash}}",
                            "name": "Andor.S01E01.1080p.WEB-DL",
                            "state": "Seeding",
                            "is_finished": true,
                            "save_path": "/data/torrents",
                            "total_size": 1500000000,
                            "total_done": 1500000000,
                            "total_remaining": 0,
                            "label": "tv-managerr",
                            "message": "OK",
                            "files": [ { "path": "Andor.S01E01.1080p.WEB-DL/Andor.S01E01.mkv", "size": 1500000000 } ]
                          }
                        }
                      },
                      "error": null
                    }
                    """),
                "core.pause_torrent" or "core.resume_torrent" or "core.queue_top" or "core.remove_torrent" =>
                    LoopbackResponse.GzipJson("""{"id":1,"result":true,"error":null}"""),
                _ => LoopbackResponse.Json("""{"id":1,"result":null,"error":{"message":"Unexpected method"}}"""),
            };
        });

        var spec = server.Spec("deluge", "torrent", password: "deluge-pass");
        var gw = Gateway();

        var testRes = await gw.TestConnectionAsync(spec);
        Assert.True(testRes.Ok);
        Assert.Equal("2.1.1", testRes.Version);

        var urlHash = await gw.EnqueueReleaseAsync(spec, "Andor.S01E01", "https://indexer.invalid/andor.torrent", "tv-managerr");
        Assert.Equal(SampleHash, urlHash);

        var bytesHash = await gw.EnqueueReleaseBytesAsync(spec, "Andor.S01E01", SampleBencodedTorrent, "tv-managerr");
        Assert.Equal(SampleHash, bytesHash);

        var queue = await gw.FetchQueueAsync(spec);
        var item = Assert.Single(queue);
        Assert.Equal(SampleHash, item.ExternalId);
        Assert.Equal("Completed", item.Status);
        Assert.Equal("tv-managerr", item.Category);
        Assert.Equal(MediaVerticals.Tv, item.MediaVertical);
        Assert.Equal("/data/torrents/Andor.S01E01.1080p.WEB-DL", item.OutputPath);
        Assert.Single(item.ContainedFiles);

        await gw.ControlDownloadAsync(spec, SampleHash, "pause");
        await gw.ControlDownloadAsync(spec, SampleHash, "resume");
        await gw.RemoveDownloadAsync(spec, SampleHash, deleteData: true);

        var calledMethods = server.Requests.Select(r => JsonNode.Parse(r.Body)!["method"]!.GetValue<string>()).ToList();
        Assert.Contains("core.add_torrent_url", calledMethods);
        Assert.Contains("core.add_torrent_file", calledMethods);
        Assert.Contains("label.set_torrent", calledMethods);
        Assert.Contains("web.update_ui", calledMethods);
        Assert.Contains("core.pause_torrent", calledMethods);
        Assert.Contains("core.resume_torrent", calledMethods);
        Assert.Contains("core.remove_torrent", calledMethods);

        var removeReq = server.Requests.Last(r => JsonNode.Parse(r.Body)!["method"]!.GetValue<string>() == "core.remove_torrent");
        Assert.Equal($$"""["{{SampleHash}}",true]""", JsonNode.Parse(removeReq.Body)!["params"]!.ToJsonString());
    }

    [Fact]
    public async Task Rtorrent_SupportsXmlRpc_BasicAuth_LoadStartAndRawStart_Multicall2_PauseResume_AndErase()
    {
        var expectedAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("rtuser:rtpass"));

        await using var server = LoopbackWireServer.Start(req =>
        {
            if (req.Headers.Get("Authorization") != expectedAuth)
            {
                return new LoopbackResponse(401, "Unauthorized", "text/plain", null);
            }

            var doc = XDocument.Parse(req.Body);
            var methodName = doc.Descendants("methodName").Single().Value;

            return methodName switch
            {
                "system.client_version" => LoopbackResponse.Xml("""
                    <?xml version="1.0"?>
                    <methodResponse><params><param><value><string>0.9.8</string></value></param></params></methodResponse>
                    """),
                "load.start" or "load.raw_start" or "d.pause" or "d.resume" or "d.erase" => LoopbackResponse.Xml("""
                    <?xml version="1.0"?>
                    <methodResponse><params><param><value><i4>0</i4></value></param></params></methodResponse>
                    """),
                "d.multicall2" => LoopbackResponse.Xml($$"""
                    <?xml version="1.0"?>
                    <methodResponse>
                      <params>
                        <param>
                          <value>
                            <array>
                              <data>
                                <value>
                                  <array>
                                    <data>
                                      <value><string>{{SampleHash.ToUpperInvariant()}}</string></value>
                                      <value><string>Shogun.S01E01.2160p.WEB-DL</string></value>
                                      <value><i8>4294967296</i8></value>
                                      <value><i8>4294967296</i8></value>
                                      <value><i4>0</i4></value>
                                      <value><string>/downloads/complete</string></value>
                                      <value><string>tv-managerr</string></value>
                                      <value><string></string></value>
                                    </data>
                                  </array>
                                </value>
                              </data>
                            </array>
                          </value>
                        </param>
                      </params>
                    </methodResponse>
                    """),
                _ => LoopbackResponse.Xml("""
                    <?xml version="1.0"?>
                    <methodResponse><fault><value><struct>
                      <member><name>faultCode</name><value><int>-506</int></value></member>
                      <member><name>faultString</name><value><string>Method not found</string></value></member>
                    </struct></value></fault></methodResponse>
                    """),
            };
        });

        var spec = server.Spec("rtorrent", "torrent", username: "rtuser", password: "rtpass");
        var gw = Gateway();

        var testRes = await gw.TestConnectionAsync(spec);
        Assert.True(testRes.Ok);
        Assert.Equal("0.9.8", testRes.Version);

        var addedMagnetId = await gw.EnqueueReleaseAsync(spec, "Shogun.S01E01", SampleMagnet, "tv-managerr");
        Assert.Equal(SampleHash, addedMagnetId);

        var addedBytesId = await gw.EnqueueReleaseBytesAsync(spec, "Shogun.S01E01", SampleBencodedTorrent, "tv-managerr");
        Assert.False(string.IsNullOrWhiteSpace(addedBytesId));

        var queue = await gw.FetchQueueAsync(spec);
        var item = Assert.Single(queue);
        Assert.Equal(SampleHash.ToUpperInvariant(), item.ExternalId);
        Assert.Equal(SampleHash, item.InfoHash);
        Assert.Equal("Shogun.S01E01.2160p.WEB-DL", item.Title);
        Assert.Equal("Completed", item.Status);
        Assert.True(item.CanMoveFiles);
        Assert.Equal("tv-managerr", item.Category);
        Assert.Equal(MediaVerticals.Tv, item.MediaVertical);
        Assert.Equal("/downloads/complete/Shogun.S01E01.2160p.WEB-DL", item.OutputPath);

        await gw.ControlDownloadAsync(spec, item.ExternalId, "pause");
        await gw.ControlDownloadAsync(spec, item.ExternalId, "resume");
        await gw.RemoveDownloadAsync(spec, item.ExternalId, deleteData: true);

        Assert.All(server.Requests, r => Assert.Equal("/RPC2", r.Path));
        var loadStartXml = server.Requests.Single(r => r.Body.Contains("<methodName>load.start</methodName>", StringComparison.Ordinal)).Body;
        Assert.Contains("d.custom1.set=tv-managerr", loadStartXml);

        var rawStartXml = server.Requests.Single(r => r.Body.Contains("<methodName>load.raw_start</methodName>", StringComparison.Ordinal)).Body;
        Assert.Contains("<base64>", rawStartXml);
        Assert.Contains("d.custom1.set=tv-managerr", rawStartXml);

        Assert.Contains(server.Requests, r => r.Body.Contains("<methodName>d.pause</methodName>", StringComparison.Ordinal));
        Assert.Contains(server.Requests, r => r.Body.Contains("<methodName>d.resume</methodName>", StringComparison.Ordinal));
        Assert.Contains(server.Requests, r => r.Body.Contains("<methodName>d.erase</methodName>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flood_SupportsJwtCookie_AddUrlsAndFiles_Queue_StopStart_DeleteWithData_AndTest()
    {
        await using var server = LoopbackWireServer.Start(req =>
        {
            if (req.Path == "/api/auth/authenticate" && req.Method == "POST")
            {
                var creds = JsonNode.Parse(req.Body)!;
                if (creds["username"]?.GetValue<string>() != "floodadmin" || creds["password"]?.GetValue<string>() != "floodsecret")
                {
                    return LoopbackResponse.Json("""{"message":"Invalid credentials"}""", status: 401);
                }

                return LoopbackResponse.Json(
                    """{"success":true,"username":"floodadmin","level":10}""",
                    headers: new Dictionary<string, string> { ["Set-Cookie"] = "jwt=eyJhbGciOiJIUzI1NiJ9.flood; Path=/; HttpOnly" });
            }

            if (!(req.Headers.Get("Cookie")?.Contains("jwt=eyJhbGciOiJIUzI1NiJ9.flood", StringComparison.Ordinal) ?? false))
            {
                return LoopbackResponse.Json("""{"message":"Authentication required"}""", status: 401);
            }

            return (req.Method, req.Path) switch
            {
                ("GET", "/api/client/connection-test") => LoopbackResponse.Json("""{"isConnected":true}"""),
                ("POST", "/api/torrents/add-urls") => LoopbackResponse.Json("""[{}]"""),
                ("POST", "/api/torrents/add-files") => LoopbackResponse.Json("""[{}]"""),
                ("GET", "/api/torrents") => LoopbackResponse.Json($$"""
                    {
                      "id": 1001,
                      "torrents": {
                        "{{SampleHash}}": {
                          "hash": "{{SampleHash}}",
                          "name": "Cyberpunk.Edgerunners.S01.1080p",
                          "sizeBytes": 8000000000,
                          "bytesDone": 2000000000,
                          "status": ["downloading", "active"],
                          "tags": ["anime-managerr"],
                          "directory": "/downloads/flood",
                          "message": ""
                        }
                      }
                    }
                    """),
                ("POST", "/api/torrents/stop") => LoopbackResponse.Json("""{}"""),
                ("POST", "/api/torrents/start") => LoopbackResponse.Json("""{}"""),
                ("POST", "/api/torrents/delete") => LoopbackResponse.Json("""{}"""),
                _ => LoopbackResponse.Json("""{"message":"Not found"}""", status: 404),
            };
        });

        var spec = server.Spec("flood", "torrent", username: "floodadmin", password: "floodsecret");
        var gw = Gateway();

        var testRes = await gw.TestConnectionAsync(spec);
        Assert.True(testRes.Ok);

        var magnetHash = await gw.EnqueueReleaseAsync(spec, "Cyberpunk.Edgerunners", SampleMagnet, "anime-managerr");
        Assert.Equal(SampleHash, magnetHash);

        var fileHash = await gw.EnqueueReleaseBytesAsync(spec, "Cyberpunk.Edgerunners", SampleBencodedTorrent, "anime-managerr");
        Assert.False(string.IsNullOrWhiteSpace(fileHash));

        var queue = await gw.FetchQueueAsync(spec);
        var item = Assert.Single(queue);
        Assert.Equal(SampleHash, item.ExternalId);
        Assert.Equal("Downloading", item.Status);
        Assert.Equal(6000000000L, item.RemainingBytes);
        Assert.Equal("anime-managerr", item.Category);
        Assert.Equal(MediaVerticals.Anime, item.MediaVertical);

        await gw.ControlDownloadAsync(spec, SampleHash, "pause");
        await gw.ControlDownloadAsync(spec, SampleHash, "resume");
        await gw.RemoveDownloadAsync(spec, SampleHash, deleteData: true);

        var addUrlsBody = JsonNode.Parse(server.Requests.Single(r => r.Path == "/api/torrents/add-urls").Body)!;
        Assert.Equal("anime-managerr", addUrlsBody["tags"]![0]!.GetValue<string>());

        var addFilesBody = JsonNode.Parse(server.Requests.Single(r => r.Path == "/api/torrents/add-files").Body)!;
        Assert.Equal(Convert.ToBase64String(SampleBencodedTorrent), addFilesBody["files"]![0]!.GetValue<string>());
        Assert.Equal("anime-managerr", addFilesBody["tags"]![0]!.GetValue<string>());

        var deleteBody = JsonNode.Parse(server.Requests.Single(r => r.Path == "/api/torrents/delete").Body)!;
        Assert.Equal(SampleHash, deleteBody["hashes"]![0]!.GetValue<string>());
        Assert.True(deleteBody["deleteData"]!.GetValue<bool>());
    }

    [Fact]
    public async Task LegacyMigrator_SkipsPrunedClientsWithCleanWarning_InsteadOfFailing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-mig-dc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var legacyDb = Path.Combine(tempDir, "sonarr.db");
            await using (var conn = new SqliteConnection($"Data Source={legacyDb}"))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync("""
                    CREATE TABLE VersionInfo (Version INTEGER NOT NULL);
                    INSERT INTO VersionInfo VALUES (200);
                    CREATE TABLE DownloadClients (
                        Id INTEGER PRIMARY KEY,
                        Name TEXT NOT NULL,
                        Implementation TEXT NOT NULL,
                        Settings TEXT NOT NULL,
                        Enable INTEGER NOT NULL
                    );
                    INSERT INTO DownloadClients VALUES (1, 'Legacy uTorrent', 'UTorrent', '{"host":"127.0.0.1","port":8080}', 1);
                    INSERT INTO DownloadClients VALUES (2, 'Homelab Deluge', 'Deluge', '{"host":"127.0.0.1","port":8112,"password":"secret"}', 1);
                    """);
            }

            await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("Managerr:DataDirectory", Path.Combine(tempDir, "data"));
            });

            var migrator = app.Services.GetRequiredService<ILegacyMigrationService>();
            var report = await migrator.ExecuteMigrationAsync(new MigrationPlanAndExecuteRequest(
                Sources:
                [
                    new AdHocMigrationSourceSpec(
                        Name: "Sonarr Test",
                        SourceKind: "sonarr",
                        FileOrUrl: legacyDb),
                ],
                GlobalPathMappings: null,
                UpliftLanguageCustomFormats: false));

            Assert.Contains(report.Warnings, w => w.Contains("Legacy uTorrent", StringComparison.Ordinal) && w.Contains("UTorrent", StringComparison.Ordinal));
            Assert.Contains(report.DownloadClients, c => c.Name == "Homelab Deluge");
            Assert.DoesNotContain(report.DownloadClients, c => c.Name == "Legacy uTorrent");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private static DownloadClientDriverGateway Gateway() =>
        new(new SafeHttpClientFactory(Options.Create(new SafeHttpEgressOptions())));

    private sealed record RecordedWireRequest(
        string Method,
        string Path,
        System.Collections.Specialized.NameValueCollection Headers,
        string Body);

    private sealed record LoopbackResponse(
        int Status,
        byte[] BodyBytes,
        string ContentType,
        IReadOnlyDictionary<string, string>? Headers)
    {
        public LoopbackResponse(int status, string body, string contentType, IReadOnlyDictionary<string, string>? headers)
            : this(status, Encoding.UTF8.GetBytes(body), contentType, headers)
        {
        }

        public static LoopbackResponse Json(string json, int status = 200, IReadOnlyDictionary<string, string>? headers = null) =>
            new(status, json, "application/json", headers);

        public static LoopbackResponse Xml(string xml, int status = 200, IReadOnlyDictionary<string, string>? headers = null) =>
            new(status, xml, "text/xml; charset=utf-8", headers);

        public static LoopbackResponse GzipJson(string json, IReadOnlyDictionary<string, string>? headers = null)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
            {
                var raw = Encoding.UTF8.GetBytes(json);
                gz.Write(raw, 0, raw.Length);
            }

            return new LoopbackResponse(200, ms.ToArray(), "application/json", headers);
        }
    }

    private sealed class LoopbackWireServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<RecordedWireRequest, LoopbackResponse> _handler;
        private readonly Task _loop;

        private LoopbackWireServer(int port, Func<RecordedWireRequest, LoopbackResponse> handler)
        {
            Port = port;
            _handler = handler;
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public int Port { get; }
        public List<RecordedWireRequest> Requests { get; } = [];

        public static LoopbackWireServer Start(Func<RecordedWireRequest, LoopbackResponse> handler)
        {
            for (var attempt = 1; ; attempt++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                try
                {
                    return new LoopbackWireServer(port, handler);
                }
                catch (HttpListenerException) when (attempt < 5)
                {
                }
            }
        }

        public DownloadClientConnectionSpec Spec(
            string implementation,
            string protocol,
            string? username = null,
            string? password = null,
            string? apiKey = null) =>
            new(
                Id: 1,
                Name: $"Test {implementation}",
                Implementation: implementation,
                Protocol: protocol,
                Host: "127.0.0.1",
                Port: Port,
                UseSsl: false,
                UrlBase: null,
                Username: username,
                PlainPassword: password,
                PlainApiKey: apiKey,
                CategoryMappings: DownloadClientCatalog.DefaultCategoryMappings(),
                ExtraSettings: new Dictionary<string, string>());

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
                    using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = await sr.ReadToEndAsync();
                    var recorded = new RecordedWireRequest(
                        ctx.Request.HttpMethod,
                        ctx.Request.Url!.AbsolutePath,
                        ctx.Request.Headers,
                        body);
                    lock (Requests)
                    {
                        Requests.Add(recorded);
                    }

                    var resp = _handler(recorded);
                    if (resp.Headers is not null)
                    {
                        foreach (var (k, v) in resp.Headers)
                        {
                            ctx.Response.Headers.Add(k, v);
                        }
                    }

                    ctx.Response.StatusCode = resp.Status;
                    ctx.Response.ContentType = resp.ContentType;
                    await ctx.Response.OutputStream.WriteAsync(resp.BodyBytes);
                }
                finally
                {
                    ctx.Response.Close();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Close();
            try { await _loop; } catch { /* shutdown */ }
        }
    }
}
