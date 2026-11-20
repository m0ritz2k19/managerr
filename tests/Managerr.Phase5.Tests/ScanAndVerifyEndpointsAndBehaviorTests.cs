using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Managerr.Catalog;
using Managerr.Host.Services;
using Managerr.Metadata;
using Managerr.Profiles;
using Managerr.Verification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Managerr.Phase5.Tests;

public sealed class ScanAndVerifyEndpointsAndBehaviorTests
{
    [Fact]
    public async Task AddWork_DoesNotAutomaticallyScan_AndScanAndVerifyEndpointsWorkForSingleAndBulk()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-scan-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var tvRoot = Path.Combine(tempDir, "storage", "media", "tv");
            var showDir1 = Path.Combine(tvRoot, "TestShowOne (2026)", "Season 01");
            var showDir2 = Path.Combine(tvRoot, "TestShowTwo (2026)", "Season 01");
            Directory.CreateDirectory(showDir1);
            Directory.CreateDirectory(showDir2);

            // Put a file on disk before creating the work
            var file1 = Path.Combine(showDir1, "TestShowOne.S01E01.1080p.WEB-DL.mkv");
            var file2 = Path.Combine(showDir2, "TestShowTwo.S01E01.1080p.WEB-DL.mkv");
            await File.WriteAllBytesAsync(file1, new byte[8192]);
            await File.WriteAllBytesAsync(file2, new byte[4096]);

            // Companion sidecar subtitle
            var sidecarSub1 = Path.Combine(showDir1, "TestShowOne.S01E01.1080p.WEB-DL.de.srt");
            await File.WriteAllTextAsync(sidecarSub1, "1\n00:00:01,000 --> 00:00:03,000\nHallo Welt\n");

            const string pass = "ScanVerifyPass!2026";
            await using var factory = CreateFactory(tempDir, pass);
            var client = await CreateAuthenticatedClientAsync(factory, pass);

            using (var scope = factory.Services.CreateScope())
            {
                var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
                var profiles = scope.ServiceProvider.GetRequiredService<IComposableProfileService>();

                var profile = (await profiles.ListProfilesAsync()).First();
                var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest(
                    Name: "TV Lib",
                    MediaType: "tv",
                    RootFolderPath: tvRoot,
                    DefaultProfileId: profile.Id));

                // 1. Add work via POST /api/v1/catalog/works - MUST NOT automatically scan disk!
                var addWorkReq = new CreateMediaWorkRequest(
                    LibraryId: lib.Id,
                    Title: "TestShowOne",
                    WorkKind: "series",
                    OriginalLanguage: "en",
                    Year: 2026,
                    Monitored: true,
                    ExternalIds: new Dictionary<string, string> { ["tvdb"] = "500001" },
                    Containers:
                    [
                        new MetadataContainerSeed("season", 1, "Season 1", "2026-01-01",
                        [
                            new MetadataUnitSeed("episode", 1, 1, "Episode 1", "2026-01-01"),
                            new MetadataUnitSeed("episode", 2, 2, "Episode 2 (Ghost)", "2026-01-08"),
                        ]),
                    ]);

                var addResp = await client.PostAsJsonAsync("/api/v1/catalog/works", addWorkReq);
                addResp.EnsureSuccessStatusCode();
                var createdWork = (await addResp.Content.ReadFromJsonAsync<MediaWorkDetailTree>())!;

                // Because no automatic scan was run, files count is 0
                var treeAfterAdd = (await catalog.GetWorkTreeAsync(createdWork.Work.Id))!;
                var s1AfterAdd = treeAfterAdd.Containers.Single();
                Assert.Empty(s1AfterAdd.Units.SelectMany(u => u.Files));

                // Attach a ghost file to Episode 2 (points to non-existent disk path) to verify ghost pruning
                var e2 = s1AfterAdd.Units.Single(u => u.UnitNumber == 2);
                await catalog.AttachFileAsync(new AttachMediaFileRequest(
                    UnitId: e2.Id,
                    RelativePath: "TestShowOne (2026)/Season 01/GhostFile.mkv",
                    SizeBytes: 1000,
                    QualityName: "WEBDL-1080p",
                    VerifiedStatus: "ImportedUnverified"));
                await catalog.UpdateUnitAsync(e2.Id, new UpdateMediaUnitRequest(QualityCutoffMet: true, LanguageCutoffMet: true));

                // Also create Work 2
                var work2 = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                    LibraryId: lib.Id,
                    Title: "TestShowTwo",
                    WorkKind: "series",
                    OriginalLanguage: "en",
                    Year: 2026,
                    Monitored: true,
                    ExternalIds: new Dictionary<string, string> { ["tvdb"] = "500002" },
                    Containers:
                    [
                        new MetadataContainerSeed("season", 1, "Season 1", "2026-02-01",
                        [
                            new MetadataUnitSeed("episode", 1, 1, "Episode 1", "2026-02-01"),
                        ]),
                    ]));

                // 2. Call Single Work scan-and-verify endpoint: POST /api/v1/catalog/works/{id}/scan-and-verify
                var scanResp = await client.PostAsync($"/api/v1/catalog/works/{createdWork.Work.Id}/scan-and-verify", null);
                scanResp.EnsureSuccessStatusCode();
                var scanReport = (await scanResp.Content.ReadFromJsonAsync<WorkLanguageScanReportDto>())!;

                Assert.Equal(createdWork.Work.Id, scanReport.WorkId);
                Assert.True(scanReport.AutoAdoptedCount >= 1);
                Assert.True(scanReport.MissingRemovedCount >= 1); // GhostFile.mkv removed!

                // Verify that ghost file was pruned and its unit cutoffs reset
                var treeAfterScan = (await catalog.GetWorkTreeAsync(createdWork.Work.Id))!;
                var e2After = treeAfterScan.Containers.Single().Units.Single(u => u.UnitNumber == 2);
                Assert.Empty(e2After.Files);
                Assert.False(e2After.QualityCutoffMet);
                Assert.False(e2After.LanguageCutoffMet);

                // Verify that real file was adopted onto Episode 1
                var e1After = treeAfterScan.Containers.Single().Units.Single(u => u.UnitNumber == 1);
                Assert.Single(e1After.Files);
                var mf1 = e1After.Files.Single();
                Assert.Equal(8192, mf1.SizeBytes);
                Assert.Contains("de", mf1.DetectedSubLangs); // Companion sidecar detected!

                // 3. Call Bulk scan-and-verify endpoint: POST /api/v1/catalog/works/bulk-scan-and-verify
                var bulkReq = new BulkScanAndVerifyWorksRequest([createdWork.Work.Id, work2.Work.Id, 99999999L]);
                var bulkResp = await client.PostAsJsonAsync("/api/v1/catalog/works/bulk-scan-and-verify", bulkReq);
                bulkResp.EnsureSuccessStatusCode();
                var bulkReport = (await bulkResp.Content.ReadFromJsonAsync<BulkWorkLanguageScanReportDto>())!;

                Assert.Equal(2, bulkReport.TargetedWorksCount); // 99999999 was filtered out by accessible check
                Assert.Equal(2, bulkReport.Reports.Count);

                // Verify Work 2 adopted its file
                var tree2After = (await catalog.GetWorkTreeAsync(work2.Work.Id))!;
                var w2Unit = tree2After.Containers.Single().Units.Single();
                Assert.Single(w2Unit.Files);
                Assert.Equal(4096, w2Unit.Files[0].SizeBytes);

                // 4. Test 404 for unknown work on single endpoint
                var notFoundResp = await client.PostAsync("/api/v1/catalog/works/8888888/scan-and-verify", null);
                Assert.Equal(HttpStatusCode.NotFound, notFoundResp.StatusCode);

                // 5. Test 404 for unknown work on regular scan endpoint
                var notFoundScanResp = await client.PostAsync("/api/v1/catalog/works/8888888/scan", null);
                Assert.Equal(HttpStatusCode.NotFound, notFoundScanResp.StatusCode);
            }
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
                    ["Managerr:MasterKey"] = "scan-verify-test-master-key-32b!",
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
}
