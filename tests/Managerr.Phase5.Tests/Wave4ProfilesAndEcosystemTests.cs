using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Managerr.Catalog;
using Managerr.Host.Services;
using Managerr.Metadata;
using Managerr.Profiles;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Managerr.Phase5.Tests;

public sealed class Wave4ProfilesAndEcosystemTests
{
    [Fact]
    public async Task CustomFormats_ReleaseProfiles_DelayProfiles_AndQualitySizeLimits_EnforcedEndToEnd()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-wave4-profiles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await using var factory = CreateFactory(tempDir, "Wave4ProfilesPass!2026");
            using var client = await CreateAuthenticatedClientAsync(factory, "Wave4ProfilesPass!2026");

            var profiles = await client.GetFromJsonAsync<List<QualityProfileSummary>>("/api/v1/profiles/quality");
            Assert.NotNull(profiles);
            var tvProfile = profiles.First(p => p.Slug == "german-first-best-quality");

            // 1. Native Custom Format CRUD (Source + Size + IndexerFlag specifications)
            var createCfResp = await client.PostAsJsonAsync("/api/v1/profiles/custom-formats", new UpsertCustomFormatRequest(
                Name: "Freeleech Golden WEB-DL",
                IncludeWhenRenaming: true,
                DefaultScore: 150,
                Specifications:
                [
                    new CustomFormatSpecItem("WEB-DL Source", "SourceSpecification", Negate: false, Required: true, Pattern: "WEB-DL"),
                    new CustomFormatSpecItem("Size 1..20 GiB", "SizeSpecification", Negate: false, Required: true, Pattern: "1..20"),
                    new CustomFormatSpecItem("Freeleech Flag", "IndexerFlagSpecification", Negate: false, Required: true, Pattern: "freeleech"),
                ]));
            createCfResp.EnsureSuccessStatusCode();
            var createdCf = await createCfResp.Content.ReadFromJsonAsync<CustomFormatSummary>();
            Assert.NotNull(createdCf);
            Assert.Equal(3, createdCf.Specifications.Count);

            // Simulate WITH freeleech + 4 GiB size -> awards +150 CF score
            var simMatchResp = await client.PostAsJsonAsync("/api/v1/profiles/simulate", new SimulateReleaseRequest(
                ProfileId: tvProfile.Id,
                ReleaseTitle: "Severance.S02E01.German.DL.1080p.WEB-DL.H264-GRP",
                OriginalLanguage: "en",
                SizeBytes: 4L * 1024 * 1024 * 1024,
                RuntimeMinutes: 55,
                IndexerFlags: ["freeleech"],
                MediaVertical: "tv"));
            simMatchResp.EnsureSuccessStatusCode();
            var simMatch = await simMatchResp.Content.ReadFromJsonAsync<ReleaseSimulationScorecard>();
            Assert.NotNull(simMatch);
            Assert.True(simMatch.Approved);
            Assert.Contains(simMatch.MatchedTraitRules, m => m.Contains("Freeleech Golden WEB-DL", StringComparison.Ordinal));

            // Simulate WITHOUT freeleech flag -> CF does not match
            var simNoFlagResp = await client.PostAsJsonAsync("/api/v1/profiles/simulate", new SimulateReleaseRequest(
                ProfileId: tvProfile.Id,
                ReleaseTitle: "Severance.S02E01.German.DL.1080p.WEB-DL.H264-GRP",
                OriginalLanguage: "en",
                SizeBytes: 4L * 1024 * 1024 * 1024,
                RuntimeMinutes: 55,
                IndexerFlags: [],
                MediaVertical: "tv"));
            simNoFlagResp.EnsureSuccessStatusCode();
            var simNoFlag = await simNoFlagResp.Content.ReadFromJsonAsync<ReleaseSimulationScorecard>();
            Assert.NotNull(simNoFlag);
            Assert.DoesNotContain(simNoFlag.MatchedTraitRules, m => m.Contains("Freeleech Golden WEB-DL", StringComparison.Ordinal));
            Assert.True(simMatch.TraitScore >= simNoFlag.TraitScore + 150);

            // Create and delete a temporary Custom Format to verify PUT + DELETE CRUD
            var tempCfResp = await client.PostAsJsonAsync("/api/v1/profiles/custom-formats", new UpsertCustomFormatRequest(
                Name: "Temp Group Rule",
                DefaultScore: 25,
                Specifications: [new CustomFormatSpecItem("Group FLUX", "ReleaseGroupSpecification", false, true, "FLUX")]));
            tempCfResp.EnsureSuccessStatusCode();
            var tempCf = await tempCfResp.Content.ReadFromJsonAsync<CustomFormatSummary>();
            Assert.NotNull(tempCf);

            var updateTempResp = await client.PutAsJsonAsync($"/api/v1/profiles/custom-formats/{tempCf.Id}", new UpsertCustomFormatRequest(
                Name: "Temp Group Rule v2",
                DefaultScore: 40,
                Specifications: tempCf.Specifications));
            updateTempResp.EnsureSuccessStatusCode();

            var deleteTempResp = await client.DeleteAsync($"/api/v1/profiles/custom-formats/{tempCf.Id}");
            Assert.Equal(HttpStatusCode.NoContent, deleteTempResp.StatusCode);

            // 2. Release Restrictions (MustContain, MustNotContain, PreferredTerms scoped by WorkTags)
            var createRpResp = await client.PostAsJsonAsync("/api/v1/profiles/release-profiles", new UpsertReleaseProfileRequest(
                Name: "Anime VIP Restrictions",
                IsEnabled: true,
                MustContain: ["German", "Dual"],
                MustNotContain: ["CAM", "HDTS"],
                PreferredTerms: [new PreferredTermScore("Hybrid", 75)],
                Tags: ["anime-vip"],
                MediaVertical: "tv"));
            createRpResp.EnsureSuccessStatusCode();
            var createdRp = await createRpResp.Content.ReadFromJsonAsync<ReleaseProfileSummary>();
            Assert.NotNull(createdRp);

            // Missing MustContain ("German" / "Dual") when tagged "anime-vip" -> rejected
            var rpMissingMustResp = await client.PostAsJsonAsync("/api/v1/profiles/simulate", new SimulateReleaseRequest(
                ProfileId: tvProfile.Id,
                ReleaseTitle: "Show.S01E01.1080p.WEB-DL.H264-GRP",
                OriginalLanguage: "en",
                ExplicitAudioLanguages: ["de", "en"],
                WorkTags: ["anime-vip"],
                MediaVertical: "tv"));
            rpMissingMustResp.EnsureSuccessStatusCode();
            var rpMissingMust = await rpMissingMustResp.Content.ReadFromJsonAsync<ReleaseSimulationScorecard>();
            Assert.NotNull(rpMissingMust);
            Assert.False(rpMissingMust.Approved);
            Assert.Contains(rpMissingMust.HardGateFailures, f => f.Contains("MustContain", StringComparison.OrdinalIgnoreCase));

            // Matching MustNotContain ("CAM") -> rejected
            var rpForbiddenResp = await client.PostAsJsonAsync("/api/v1/profiles/simulate", new SimulateReleaseRequest(
                ProfileId: tvProfile.Id,
                ReleaseTitle: "Show.S01E01.German.CAM.1080p.WEB-DL.H264-GRP",
                OriginalLanguage: "en",
                WorkTags: ["anime-vip"],
                MediaVertical: "tv"));
            rpForbiddenResp.EnsureSuccessStatusCode();
            var rpForbidden = await rpForbiddenResp.Content.ReadFromJsonAsync<ReleaseSimulationScorecard>();
            Assert.NotNull(rpForbidden);
            Assert.False(rpForbidden.Approved);
            Assert.Contains(rpForbidden.HardGateFailures, f => f.Contains("MustNotContain", StringComparison.OrdinalIgnoreCase));

            // Matching MustContain ("German") + Preferred ("Hybrid") -> approved with +75 score
            var rpApprovedResp = await client.PostAsJsonAsync("/api/v1/profiles/simulate", new SimulateReleaseRequest(
                ProfileId: tvProfile.Id,
                ReleaseTitle: "Show.S01E01.German.DL.Hybrid.1080p.WEB-DL.H264-GRP",
                OriginalLanguage: "en",
                WorkTags: ["anime-vip"],
                MediaVertical: "tv"));
            rpApprovedResp.EnsureSuccessStatusCode();
            var rpApproved = await rpApprovedResp.Content.ReadFromJsonAsync<ReleaseSimulationScorecard>();
            Assert.NotNull(rpApproved);
            Assert.True(rpApproved.Approved);
            Assert.Contains(rpApproved.MatchedTraitRules, m => m.Contains("Hybrid", StringComparison.Ordinal) && m.Contains("+75", StringComparison.Ordinal));

            // 3. Quality Size Limits (MB/min)
            var putSizesResp = await client.PutAsJsonAsync("/api/v1/profiles/quality-sizes", new BulkUpsertQualitySizeLimitsRequest(
            [
                new UpsertQualitySizeLimitItem(
                    QualityName: "WEBDL-1080p",
                    MediaVertical: "tv",
                    MinMbPerMin: 10,
                    PreferredMbPerMin: 40,
                    MaxMbPerMin: 80),
            ]));
            putSizesResp.EnsureSuccessStatusCode();

            // Oversized release: 7,500 MB / 60 min = ~125 MB/min (> 80 max) -> rejected
            var oversizedResp = await client.PostAsJsonAsync("/api/v1/profiles/simulate", new SimulateReleaseRequest(
                ProfileId: tvProfile.Id,
                ReleaseTitle: "Show.S01E02.German.DL.1080p.WEB-DL.H264-GRP",
                OriginalLanguage: "en",
                SizeBytes: 7_500L * 1024 * 1024,
                RuntimeMinutes: 60,
                MediaVertical: "tv"));
            oversizedResp.EnsureSuccessStatusCode();
            var oversized = await oversizedResp.Content.ReadFromJsonAsync<ReleaseSimulationScorecard>();
            Assert.NotNull(oversized);
            Assert.False(oversized.Approved);

            // In-range release: 2,400 MB / 60 min = 40 MB/min -> approved
            var validSizeResp = await client.PostAsJsonAsync("/api/v1/profiles/simulate", new SimulateReleaseRequest(
                ProfileId: tvProfile.Id,
                ReleaseTitle: "Show.S01E02.German.DL.1080p.WEB-DL.H264-GRP",
                OriginalLanguage: "en",
                SizeBytes: 2_400L * 1024 * 1024,
                RuntimeMinutes: 60,
                MediaVertical: "tv"));
            validSizeResp.EnsureSuccessStatusCode();
            var validSize = await validSizeResp.Content.ReadFromJsonAsync<ReleaseSimulationScorecard>();
            Assert.NotNull(validSize);
            Assert.True(validSize.Approved);

            // 4. Protocol Delay Profiles & Bypass Evaluation
            var delayList = await client.GetFromJsonAsync<List<DelayProfileSummary>>("/api/v1/profiles/delay-profiles");
            Assert.NotNull(delayList);
            var defaultDelay = Assert.Single(delayList, d => d.IsDefault);

            // Deleting the default delay profile must be blocked
            var deleteDefaultDelayResp = await client.DeleteAsync($"/api/v1/profiles/delay-profiles/{defaultDelay.Id}");
            Assert.False(deleteDefaultDelayResp.IsSuccessStatusCode);

            var createDelayResp = await client.PostAsJsonAsync("/api/v1/profiles/delay-profiles", new UpsertDelayProfileRequest(
                Name: "Usenet Priority 2h Delay",
                PreferredProtocol: "usenet",
                UsenetDelayMinutes: 0,
                TorrentDelayMinutes: 120,
                BypassIfHighestQuality: true,
                BypassIfAboveCustomFormatScore: false,
                Tags: ["delayed-show"],
                SortOrder: 10));
            createDelayResp.EnsureSuccessStatusCode();

            using var scope = factory.Services.CreateScope();
            var profileSvc = scope.ServiceProvider.GetRequiredService<IComposableProfileService>();

            var nonCutoffScorecard = await profileSvc.SimulateAsync(new SimulateReleaseRequest(
                ProfileId: tvProfile.Id,
                ReleaseTitle: "Show.S01E03.German.DL.1080p.WEB-DL.H264-GRP",
                OriginalLanguage: "en",
                MediaVertical: "tv"));
            Assert.True(nonCutoffScorecard.Approved);
            Assert.False(nonCutoffScorecard.CandidateSatisfiesQualityCutoff);

            // 15-minute-old torrent below cutoff -> delayed (requires 120m)
            var delayedDecision = await profileSvc.EvaluateDelayAsync(
                protocol: "torrent",
                publishedAt: DateTimeOffset.UtcNow.AddMinutes(-15),
                workTags: ["delayed-show"],
                scorecard: nonCutoffScorecard);
            Assert.True(delayedDecision.IsDelayed);
            Assert.Equal(120, delayedDecision.RequiredDelayMinutes);

            // 15-minute-old torrent that satisfies BOTH quality & language cutoffs -> bypasses delay!
            var cutoffScorecard = await profileSvc.SimulateAsync(new SimulateReleaseRequest(
                ProfileId: tvProfile.Id,
                ReleaseTitle: "Show.S01E03.German.DL.2160p.UHD.BluRay.REMUX.HEVC-GRP",
                OriginalLanguage: "en",
                MediaVertical: "tv"));
            Assert.True(cutoffScorecard.Approved);
            Assert.True(cutoffScorecard.CandidateSatisfiesQualityCutoff);
            Assert.True(cutoffScorecard.CandidateSatisfiesLanguageCutoff);

            var bypassedDecision = await profileSvc.EvaluateDelayAsync(
                protocol: "torrent",
                publishedAt: DateTimeOffset.UtcNow.AddMinutes(-15),
                workTags: ["delayed-show"],
                scorecard: cutoffScorecard);
            Assert.False(bypassedDecision.IsDelayed);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ConnectNotifications_CalendarIcsFeed_AndImportListsWithExclusions_WorkEndToEnd()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-wave4-ecosystem-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        await using var webhookServer = new LoopbackWebhookCaptureServer();
        webhookServer.Start();

        try
        {
            var tvRoot = Path.Combine(tempDir, "storage", "media", "tv");
            var moviesRoot = Path.Combine(tempDir, "storage", "media", "movies");
            Directory.CreateDirectory(tvRoot);
            Directory.CreateDirectory(moviesRoot);

            await using var factory = CreateFactory(tempDir, "Wave4EcoPass!2026");
            using var scope = factory.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var profiles = scope.ServiceProvider.GetRequiredService<IComposableProfileService>();
            var defaultProfile = (await profiles.ListProfilesAsync()).First(p => p.Slug == "german-first-best-quality");

            var tvLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest(
                Name: "TV Root",
                MediaType: "tv",
                RootFolderPath: tvRoot,
                DefaultProfileId: defaultProfile.Id));
            var movieLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest(
                Name: "Movies Root",
                MediaType: "movies",
                RootFolderPath: moviesRoot,
                DefaultProfileId: defaultProfile.Id));

            var upcomingAirDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)).ToString("yyyy-MM-dd");
            await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: "Avatar: Seven Havens",
                WorkKind: "tv",
                OriginalLanguage: "en",
                Year: 2026,
                Monitored: true,
                Containers:
                [
                    new MetadataContainerSeed(
                        ContainerKind: "season",
                        ContainerNumber: 1,
                        Title: "Season 1",
                        ReleaseDate: upcomingAirDate,
                        Units:
                        [
                            new MetadataUnitSeed("episode", 1, 1, "The Convergence", upcomingAirDate),
                        ]),
                ]));

            using var client = await CreateAuthenticatedClientAsync(factory, "Wave4EcoPass!2026");

            // 1. Connect Notification Provider (Signed Generic Webhook + Test + Secret Masking)
            var createNotifResp = await client.PostAsJsonAsync("/api/v1/notifications", new UpsertNotificationConnectionRequest(
                Name: "Homelab Signed Webhook",
                ProviderType: "webhook",
                Settings: new Dictionary<string, string>
                {
                    ["url"] = $"http://127.0.0.1:{webhookServer.Port}/hook",
                    ["method"] = "POST",
                    ["hmacSecret"] = "wave4-super-secret-hmac",
                },
                OnGrab: true,
                OnImport: true,
                OnUpgrade: true,
                OnDelete: true,
                IsEnabled: true));
            createNotifResp.EnsureSuccessStatusCode();
            var createdNotif = await createNotifResp.Content.ReadFromJsonAsync<NotificationConnectionDto>();
            Assert.NotNull(createdNotif);
            Assert.Equal("********", createdNotif.Settings["hmacSecret"]);

            var testNotifResp = await client.PostAsync($"/api/v1/notifications/{createdNotif.Id}/test", content: null);
            testNotifResp.EnsureSuccessStatusCode();
            var testDispatch = await testNotifResp.Content.ReadFromJsonAsync<NotificationDispatchResultDto>();
            Assert.NotNull(testDispatch);
            Assert.True(testDispatch.Success);

            Assert.True(webhookServer.Requests.TryDequeue(out var capturedTestHook));
            Assert.StartsWith("sha256=", capturedTestHook.SignatureHeader, StringComparison.Ordinal);
            Assert.Contains("\"eventType\":\"Test\"", capturedTestHook.Body, StringComparison.Ordinal);

            // 2. Calendar iCal (.ics) Feed authenticated via ?apikey= without cookies
            var createTokenResp = await client.PostAsJsonAsync("/api/v1/auth/tokens", new
            {
                name = "Calendar iCal Key",
                scopes = new[] { "catalog:read" },
            });
            createTokenResp.EnsureSuccessStatusCode();
            var tokenJson = await createTokenResp.Content.ReadFromJsonAsync<JsonElement>();
            var rawApiKey = tokenJson.GetProperty("rawTokenOnce").GetString();
            Assert.False(string.IsNullOrWhiteSpace(rawApiKey));

            using var unauthClient = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            var noKeyResp = await unauthClient.GetAsync("/feed/v1/calendar/managerr.ics");
            Assert.Equal(HttpStatusCode.Unauthorized, noKeyResp.StatusCode);

            var icsResp = await unauthClient.GetAsync($"/feed/v1/calendar/managerr.ics?apikey={Uri.EscapeDataString(rawApiKey!)}&futureDays=30");
            icsResp.EnsureSuccessStatusCode();
            Assert.Contains("text/calendar", icsResp.Content.Headers.ContentType?.MediaType ?? "", StringComparison.OrdinalIgnoreCase);
            var icsBody = await icsResp.Content.ReadAsStringAsync();
            Assert.Contains("BEGIN:VCALENDAR\r\n", icsBody, StringComparison.Ordinal);
            Assert.Contains("BEGIN:VEVENT\r\n", icsBody, StringComparison.Ordinal);
            Assert.Contains("SUMMARY:Avatar: Seven Havens - S01E01 - The Convergence", icsBody, StringComparison.Ordinal);
            Assert.Contains("END:VCALENDAR\r\n", icsBody, StringComparison.Ordinal);

            // 3. Automated Import Lists + Exclusions + Scheduled Task Integration
            var addExclResp = await client.PostAsJsonAsync("/api/v1/import-lists/exclusions", new CreateImportListExclusionRequest(
                ExternalKey: "imdb:tt0000999",
                Title: "Excluded Movie",
                MediaVertical: "movies",
                Year: 2025));
            addExclResp.EnsureSuccessStatusCode();

            var inlineMoviesJson = JsonSerializer.Serialize(new object[]
            {
                new { title = "Excluded Movie", year = 2025, imdb_id = "tt0000999" },
                new { title = "Dune Messiah", year = 2026, imdb_id = "tt15239678" },
            });

            var createListResp = await client.PostAsJsonAsync("/api/v1/import-lists", new UpsertImportListRequest(
                Name: "Curated Sci-Fi Watchlist",
                ListType: "custom_json",
                MediaVertical: "movies",
                LibraryId: movieLib.Id,
                QualityProfileId: defaultProfile.Id,
                MonitorMode: "all",
                MinimumAvailability: "released",
                SearchOnAdd: false,
                Settings: new Dictionary<string, string> { ["itemsJson"] = inlineMoviesJson },
                Tags: ["watchlist-auto"],
                SyncIntervalHours: 12,
                IsEnabled: true));
            createListResp.EnsureSuccessStatusCode();
            var createdList = await createListResp.Content.ReadFromJsonAsync<ImportListSummaryDto>();
            Assert.NotNull(createdList);

            var syncResp = await client.PostAsync($"/api/v1/import-lists/{createdList.Id}/sync", content: null);
            syncResp.EnsureSuccessStatusCode();
            var syncReport = await syncResp.Content.ReadFromJsonAsync<ImportListSyncReportDto>();
            Assert.NotNull(syncReport);
            Assert.Equal(2, syncReport.FetchedCount);
            Assert.Equal(1, syncReport.AddedCount);
            Assert.Equal(1, syncReport.SkippedExcludedCount);

            // Running ImportListSync via BackgroundTaskSchedulerService skips now-existing "Dune Messiah"
            var runTaskResp = await client.PostAsync("/api/v1/system/tasks/ImportListSync/run", content: null);
            runTaskResp.EnsureSuccessStatusCode();

            var movieWorks = await client.GetFromJsonAsync<List<MediaWorkSummary>>("/api/v1/catalog/works?vertical=movies");
            Assert.NotNull(movieWorks);
            var duneWork = Assert.Single(movieWorks, w => w.Title == "Dune Messiah");
            Assert.DoesNotContain(movieWorks, w => w.Title == "Excluded Movie");

            // 4. Deleting "Dune Messiah" triggers the OnDelete lifecycle notification to our signed webhook!
            var deleteWorkResp = await client.PostAsJsonAsync("/api/v1/catalog/works/bulk-edit", new BulkEditWorksRequest(
                WorkIds: [duneWork.Id],
                DeleteWorks: true,
                DeleteFilesOnDisk: false));
            deleteWorkResp.EnsureSuccessStatusCode();

            Assert.True(webhookServer.Requests.TryDequeue(out var capturedDeleteHook));
            Assert.Contains("\"eventType\":\"OnDelete\"", capturedDeleteHook.Body, StringComparison.Ordinal);
            Assert.Contains("Dune Messiah", capturedDeleteHook.Body, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(string tempDir, string password) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                    ["Managerr:MasterKey"] = "wave4-profiles-eco-master-key32!",
                    ["Managerr:SchedulerEnabled"] = "false",
                    ["Managerr:TestbedBootstrap:Enabled"] = "true",
                    ["Managerr:TestbedBootstrap:Username"] = "testadmin",
                    ["Managerr:TestbedBootstrap:Password"] = password,
                });
            });
        });

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(WebApplicationFactory<Program> factory, string password)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/breakglass/login", new
        {
            usernameOrEmail = "testadmin",
            password,
        });
        loginResp.EnsureSuccessStatusCode();
        var body = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
        if (body.TryGetProperty("csrfToken", out var csrf) && csrf.GetString() is { Length: > 0 } token)
        {
            client.DefaultRequestHeaders.Add("X-Managerr-Csrf", token);
        }

        return client;
    }

    private sealed record CapturedWebhookRequest(string Path, string SignatureHeader, string Body);

    private sealed class LoopbackWebhookCaptureServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private Task? _acceptLoop;

        public int Port { get; private set; }
        public ConcurrentQueue<CapturedWebhookRequest> Requests { get; } = new();

        public void Start()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await _listener.AcceptTcpClientAsync(ct);
                }
                catch
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    using (tcp)
                    await using (var stream = tcp.GetStream())
                    {
                        var buffer = new byte[65536];
                        var totalRead = 0;
                        var headerEndIndex = -1;
                        var contentLength = 0;

                        while (totalRead < buffer.Length)
                        {
                            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
                            if (read <= 0) break;
                            totalRead += read;

                            if (headerEndIndex < 0)
                            {
                                var currentText = Encoding.ASCII.GetString(buffer, 0, totalRead);
                                var idx = currentText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                                if (idx >= 0)
                                {
                                    headerEndIndex = Encoding.ASCII.GetByteCount(currentText[..(idx + 4)]);
                                    foreach (var headerLine in currentText[..idx].Split("\r\n"))
                                    {
                                        if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                                            int.TryParse(headerLine["Content-Length:".Length..].Trim(), out var cl))
                                        {
                                            contentLength = cl;
                                        }
                                    }
                                }
                            }

                            if (headerEndIndex >= 0 && totalRead >= headerEndIndex + contentLength)
                            {
                                break;
                            }
                        }

                        if (headerEndIndex > 0)
                        {
                            var headerText = Encoding.UTF8.GetString(buffer, 0, headerEndIndex);
                            var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                            var requestLineParts = lines.Length > 0 ? lines[0].Split(' ') : [];
                            var path = requestLineParts.Length > 1 ? requestLineParts[1] : "/";
                            var sig = "";
                            foreach (var line in lines.Skip(1))
                            {
                                if (line.StartsWith("X-Managerr-Signature:", StringComparison.OrdinalIgnoreCase))
                                {
                                    sig = line["X-Managerr-Signature:".Length..].Trim();
                                }
                            }

                            var bodyBytesCount = Math.Max(0, Math.Min(contentLength, totalRead - headerEndIndex));
                            var body = Encoding.UTF8.GetString(buffer, headerEndIndex, bodyBytesCount);
                            Requests.Enqueue(new CapturedWebhookRequest(path, sig, body));
                        }

                        var respBytes = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 11\r\nConnection: close\r\n\r\n{\"ok\":true}");
                        await stream.WriteAsync(respBytes, ct);
                        await stream.FlushAsync(ct);
                    }
                }, ct);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { _listener.Stop(); } catch { }
            if (_acceptLoop is not null)
            {
                try { await _acceptLoop; } catch { }
            }
            _cts.Dispose();
        }
    }
}
