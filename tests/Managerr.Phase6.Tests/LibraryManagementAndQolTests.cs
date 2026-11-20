using System.Net;
using System.Text.Json;
using Dapper;
using Managerr.Catalog;
using Managerr.Common.Crypto;
using Managerr.Common.Net;
using Managerr.Common.Notifications;
using Managerr.Datastore;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
using Managerr.Host.Hubs;
using Managerr.Host.Services;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Managerr.Phase6.Tests;

public sealed class LibraryManagementAndQolTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"managerr-p6-qol-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _http = null!;
    private string _apiKey = null!;
    private readonly FakeGateway _gateway = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Managerr:ConfigDirectory"] = configDir,
                ["Managerr:MasterKey"] = "test-master-key-thirty-two-bytes-1234",
                ["Managerr:SchedulerEnabled"] = "false",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IDownloadClientDriverGateway>();
                s.AddSingleton<IDownloadClientDriverGateway>(_gateway);
            });
        });

        using (var scope = _factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
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

    [Fact]
    public void IsTransientDownloaderClientFault_DistinguishesClientVsReleaseErrors()
    {
        // 1. Genuine release failures should NOT match
        Assert.False(WantedAndActivityService.IsTransientDownloaderClientFault("Aborted, cannot be completed"));
        Assert.False(WantedAndActivityService.IsTransientDownloaderClientFault("Aborted, cannot be completed - missing articles"));
        Assert.False(WantedAndActivityService.IsTransientDownloaderClientFault("Repair failed, not enough repair blocks"));
        Assert.False(WantedAndActivityService.IsTransientDownloaderClientFault(null));
        Assert.False(WantedAndActivityService.IsTransientDownloaderClientFault(""));

        // 2. Transient downloader infrastructure/config errors SHOULD match
        Assert.True(WantedAndActivityService.IsTransientDownloaderClientFault("No active servers"));
        Assert.True(WantedAndActivityService.IsTransientDownloaderClientFault("Missing server in config"));
        Assert.True(WantedAndActivityService.IsTransientDownloaderClientFault("Disk full: cannot allocate 500MB"));
        Assert.True(WantedAndActivityService.IsTransientDownloaderClientFault("No space left on device"));
        Assert.True(WantedAndActivityService.IsTransientDownloaderClientFault("Permission denied writing to destination"));
        Assert.True(WantedAndActivityService.IsTransientDownloaderClientFault("Read-only file system"));
        Assert.True(WantedAndActivityService.IsTransientDownloaderClientFault("Login failed for news.usenetserver.com"));
        Assert.True(WantedAndActivityService.IsTransientDownloaderClientFault("Authentication failed"));
        Assert.True(WantedAndActivityService.IsTransientDownloaderClientFault("Connection refused by downloader"));
        Assert.True(WantedAndActivityService.IsTransientDownloaderClientFault("Network is unreachable"));
    }

    [Fact]
    public async Task RemoveQueueItemWithOptions_WhenTransientClientFault_DoesNotPoisonBlocklist()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var orchestrator = sp.GetRequiredService<IUnifiedDownloadOrchestrator>();
        var wantedService = sp.GetRequiredService<IWantedAndActivityService>();
        var db = sp.GetRequiredService<ISqliteDbFactory>();

        var client = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
            Name: "SabNzbd",
            Implementation: "sabnzbd",
            Host: "sabnzbd",
            Port: 8080));

        // Add transient fault item to gateway queue
        var extIdTransient = "sab-transient-1";
        var transientHash = "aabbccddeeff00112233445566778899aabbccdd";
        _gateway.QueueItems.Add(new ActiveDownloadQueueItem(
            client.Id,
            client.Name,
            "usenet",
            extIdTransient,
            transientHash,
            "Naruto.Shippuden.S01E01.1080p.WEB-DL",
            "tv-managerr",
            "tv",
            "Failed",
            100_000,
            0,
            "",
            [],
            false,
            false,
            ErrorMessage: "Disk full: cannot allocate space"));

        // Remove with Blocklist = true
        var outcome = await wantedService.RemoveQueueItemWithOptionsAsync(
            client.Id,
            extIdTransient,
            new RemoveQueueItemOptions(RemoveFromClient: true, Blocklist: true));

        // Blocklisted must be false because it was a transient downloader client fault!
        Assert.False(outcome.Blocklisted);
        Assert.True(outcome.RemovedFromClient);

        // Verify recurring_blocklist_hashes does NOT have this hash
        await using (var conn = await db.OpenReadConnectionAsync(DatabaseKind.Core))
        {
            var count = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM recurring_blocklist_hashes WHERE info_hash = @Hash;",
                new { Hash = transientHash });
            Assert.Equal(0, count);

            // Tracked download should be in Failed state with transient message
            var tracked = await conn.QuerySingleOrDefaultAsync<(string State, string TrackedStatus, string Msgs)>(
                "SELECT state, tracked_status, status_messages_json FROM tracked_downloads WHERE external_download_id = @Id;",
                new { Id = extIdTransient });
            Assert.Equal(TrackedDownloadStates.Failed, tracked.State);
            Assert.Contains("Transient downloader fault", tracked.Msgs);
        }

        // Now test a genuine release failure (e.g. missing articles)
        var extIdGenuine = "sab-genuine-2";
        var genuineHash = "11223344556677889900aabbccddeeff00112233";
        _gateway.QueueItems.Add(new ActiveDownloadQueueItem(
            client.Id,
            client.Name,
            "usenet",
            extIdGenuine,
            genuineHash,
            "Naruto.Shippuden.S01E02.1080p.WEB-DL",
            "tv-managerr",
            "tv",
            "Failed",
            100_000,
            0,
            "",
            [],
            false,
            false,
            ErrorMessage: "Aborted, cannot be completed - missing articles"));

        var genuineOutcome = await wantedService.RemoveQueueItemWithOptionsAsync(
            client.Id,
            extIdGenuine,
            new RemoveQueueItemOptions(RemoveFromClient: true, Blocklist: true));

        // Genuine release failure DOES blocklist
        Assert.True(genuineOutcome.Blocklisted);

        await using (var conn = await db.OpenReadConnectionAsync(DatabaseKind.Core))
        {
            var count = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM recurring_blocklist_hashes WHERE info_hash = @Hash;",
                new { Hash = genuineHash });
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public async Task LibrarySummary_ExposesFreeAndTotalDiskSpaceTelemetry()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var catalog = sp.GetRequiredService<IPolymorphicCatalogService>();

        var testLibPath = Path.Combine(_tempDir, "media", "movies");
        Directory.CreateDirectory(testLibPath);

        var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest(
            Name: "Test Movies",
            MediaType: "movies",
            RootFolderPath: testLibPath));

        Assert.NotNull(lib);
        Assert.True(lib.FreeSpaceBytes.HasValue, "FreeSpaceBytes should be populated");
        Assert.True(lib.FreeSpaceBytes.Value > 0, "FreeSpaceBytes should be > 0");
        Assert.True(lib.TotalSpaceBytes.HasValue, "TotalSpaceBytes should be populated");
        Assert.True(lib.TotalSpaceBytes.Value > 0, "TotalSpaceBytes should be > 0");
        Assert.True(lib.RootAccessible, "RootAccessible should be true for existing folder");

        // Verify retrieval via GetLibraryByIdAsync and ListLibrariesAsync
        var fetched = await catalog.GetLibraryByIdAsync(lib.Id);
        Assert.NotNull(fetched);
        Assert.Equal(lib.FreeSpaceBytes, fetched.FreeSpaceBytes);
        Assert.Equal(lib.TotalSpaceBytes, fetched.TotalSpaceBytes);
        Assert.True(fetched.RootAccessible);

        var list = await catalog.ListLibrariesAsync();
        var inList = Assert.Single(list, l => l.Id == lib.Id);
        Assert.True(inList.FreeSpaceBytes.HasValue && inList.FreeSpaceBytes.Value > 0);
        Assert.True(inList.TotalSpaceBytes.HasValue && inList.TotalSpaceBytes.Value > 0);
        Assert.True(inList.RootAccessible);
    }

    [Fact]
    public async Task GetWorkTreeAsync_AttachesLanguageVerificationBadges()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var catalog = sp.GetRequiredService<IPolymorphicCatalogService>();
        var db = sp.GetRequiredService<ISqliteDbFactory>();

        var libPath = Path.Combine(_tempDir, "media", "shows");
        Directory.CreateDirectory(libPath);
        var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Anime TV", "tv", libPath));

        var work = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
            LibraryId: lib.Id,
            Title: "Sousou no Frieren",
            Year: 2023,
            Monitored: true));

        var treeBefore = await catalog.GetWorkTreeAsync(work.Work.Id);
        Assert.NotNull(treeBefore);
        var container = Assert.Single(treeBefore.Containers);
        var unit = Assert.Single(container.Units);

        var mediaFile = await catalog.AttachFileAsync(new AttachMediaFileRequest(
            UnitId: unit.Id,
            RelativePath: "Season 01/Frieren - S01E01 - The Journey's End.mkv",
            SizeBytes: 1_234_567_890,
            QualityName: "WEBDL-1080p",
            Resolution: "1080p",
            VideoCodec: "h265",
            HdrFormat: null,
            AudioCodec: "aac",
            DetectedAudioLangs: ["jpn", "deu"],
            DetectedSubLangs: ["eng", "deu"],
            VerifiedStatus: "VerifiedPass",
            InodeHardlinked: false));

        // Insert verification_jobs row
        var reportJson = JsonSerializer.Serialize(new
        {
            jobId = 999,
            unitId = unit.Id,
            mediaFileId = mediaFile.Id,
            stagedFilePath = "/downloads/frieren.mkv",
            mediaVertical = "tv",
            verdict = "VerifiedPass",
            overallConfidence = 0.985,
            streams = new object[]
            {
                new { streamIndex = 0, streamType = "audio", codec = "aac", declaredLanguage = "jpn", detectedLanguage = "jpn", confidence = 0.99, method = "Whisper" },
                new { streamIndex = 1, streamType = "subtitle", codec = "subrip", declaredLanguage = "eng", detectedLanguage = "eng", confidence = 0.98, method = "Lingua" }
            },
            verifiedAt = "2026-11-13T00:00:00Z"
        });

        await db.ExecuteWriteAsync(DatabaseKind.Core, (conn, tx) => conn.ExecuteAsync(
            """
            INSERT INTO verification_jobs (
                unit_id, media_file_id, staged_file_path, media_vertical, verdict,
                overall_confidence, header_auto_patched, detected_audio_langs_json,
                detected_sub_langs_json, report_json, duration_ms, created_at
            ) VALUES (
                @UnitId, @MediaFileId, @Path, 'tv', 'VerifiedPass',
                0.985, 0, '["jpn","deu"]', '["eng","deu"]', @ReportJson, 1450, '2026-11-13T00:00:00Z'
            );
            """,
            new
            {
                UnitId = unit.Id,
                MediaFileId = mediaFile.Id,
                Path = "/downloads/frieren.mkv",
                ReportJson = reportJson
            },
            transaction: tx));

        // Get work tree and inspect file summary
        var tree = await catalog.GetWorkTreeAsync(work.Work.Id);
        Assert.NotNull(tree);
        var unitDetail = Assert.Single(tree.Containers.Single().Units);
        var fileSummary = Assert.Single(unitDetail.Files);

        Assert.NotNull(fileSummary.LanguageVerification);
        Assert.Equal("VerifiedPass", fileSummary.LanguageVerification.Verdict);
        Assert.Equal(0.985, fileSummary.LanguageVerification.OverallConfidence, precision: 3);
        Assert.Equal("Whisper", fileSummary.LanguageVerification.AudioMethod);
        Assert.Equal("Lingua", fileSummary.LanguageVerification.SubMethod);
        Assert.Contains("jpn", fileSummary.LanguageVerification.DetectedAudioLanguages);
        Assert.Contains("deu", fileSummary.LanguageVerification.DetectedAudioLanguages);
        Assert.Contains("eng", fileSummary.LanguageVerification.DetectedSubLanguages);
        Assert.Equal(1450, fileSummary.LanguageVerification.DurationMs);
        Assert.Equal("2026-11-13T00:00:00Z", fileSummary.LanguageVerification.VerifiedAt);
    }

    [Fact]
    public async Task RealtimeSseStream_DeliversConnectedAndLiveFrames()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/realtime/stream");
        req.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());

        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // Read initial connection frame: event: connected\ndata: {"ok":true}\n\n
        var line1 = await reader.ReadLineAsync(cts.Token);
        var line2 = await reader.ReadLineAsync(cts.Token);
        var line3 = await reader.ReadLineAsync(cts.Token);

        Assert.Equal("event: connected", line1);
        Assert.Equal("data: {\"ok\":true}", line2);
        Assert.Equal("", line3);

        // Publish a scoped event via IRealtimeBroadcaster
        var broadcaster = _factory.Services.GetRequiredService<IRealtimeBroadcaster>();
        await broadcaster.PublishScopedEventAsync(
            PermissionScopes.CatalogRead,
            "media_library_updated",
            new { action = "scan_completed", libraryId = 42 });

        // Read broadcasted event frame
        var evLine = await reader.ReadLineAsync(cts.Token);
        var dataLine = await reader.ReadLineAsync(cts.Token);
        var blankLine = await reader.ReadLineAsync(cts.Token);

        Assert.Equal("event: media_library_updated", evLine);
        Assert.Contains("\"action\":\"scan_completed\"", dataLine);
        Assert.Contains("\"libraryId\":42", dataLine);
        Assert.Equal("", blankLine);
    }

    [Fact]
    public async Task PlexNotification_AppendsTargetedPathWhenProvided()
    {
        HttpRequestMessage? capturedRequest = null;
        var fakeHandler = new CapturingHttpMessageHandler(req => capturedRequest = req);

        var service = new NotificationConnectionService(
            _factory.Services.GetRequiredService<ISqliteDbFactory>(),
            _factory.Services.GetRequiredService<ISecretProtector>(),
            _factory.Services.GetRequiredService<ISafeHttpClientFactory>(),
            NullLogger<NotificationConnectionService>.Instance,
            () => new HttpClient(fakeHandler));

        var targetFile = Path.Combine(_tempDir, "media", "tv", "Frieren", "Season 01", "Frieren.S01E01.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        await File.WriteAllTextAsync(targetFile, "dummy");

        var testEvent = new MediaLifecycleNotificationEvent(
            EventType: MediaNotificationEventTypes.OnImport,
            Title: "Import Completed",
            Message: "Imported Frieren S01E01",
            DestinationPath: targetFile);

        var settings = new Dictionary<string, string>
        {
            ["serverUrl"] = "http://plex.local:32400",
            ["token"] = "plex-secret-token",
            ["sectionId"] = "2"
        };

        var conn = await service.UpsertConnectionAsync(new UpsertNotificationConnectionRequest(
            Name: "Plex Local",
            ProviderType: "plex",
            Settings: settings,
            OnImport: true));

        await service.PublishAsync(testEvent);

        Assert.NotNull(capturedRequest);

        var uri = capturedRequest.RequestUri?.ToString() ?? "";
        Assert.StartsWith("http://plex.local:32400/library/sections/2/refresh", uri);

        var expectedTargetDir = Path.GetDirectoryName(targetFile)!;
        Assert.Contains("path=", uri);
        var unescapedQuery = Uri.UnescapeDataString(capturedRequest.RequestUri!.Query);
        Assert.Contains($"path={expectedTargetDir}", unescapedQuery);
        Assert.True(capturedRequest.Headers.TryGetValues("X-Plex-Token", out var tokens));
        Assert.Equal("plex-secret-token", Assert.Single(tokens));
    }

    private sealed class CapturingHttpMessageHandler(Action<HttpRequestMessage> capture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class FakeGateway : IDownloadClientDriverGateway
    {
        public List<ActiveDownloadQueueItem> QueueItems { get; } = [];

        public IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers() => DownloadClientCatalog.All;

        public Task<string> EnqueueReleaseAsync(DownloadClientConnectionSpec client, string releaseTitle, string downloadUrlOrMagnet, string resolvedCategory, CancellationToken ct = default)
            => Task.FromResult($"q-{Guid.NewGuid():N}");

        public Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(DownloadClientConnectionSpec client, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ActiveDownloadQueueItem>>(QueueItems.Where(q => q.ClientId == client.Id).ToList());

        public Task RemoveDownloadAsync(DownloadClientConnectionSpec client, string externalId, bool deleteData, CancellationToken ct = default)
        {
            QueueItems.RemoveAll(q => q.ExternalId == externalId);
            return Task.CompletedTask;
        }
    }
}
