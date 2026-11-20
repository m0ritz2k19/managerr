using System.Net.Http.Json;
using System.Text.Json;
using Managerr.Catalog;
using Managerr.Metadata;
using Managerr.Profiles;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Managerr.Verification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Managerr.Phase6.Tests;

public sealed class ContainerProbeAndLanguagePolicyTests
{
    [Fact]
    public async Task LargeFileOver2GiB_DoesNotOverflow_AndVerifiesGermanJapaneseDualAudio()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"probe-2gib-{Guid.NewGuid():N}.mkv");
        try
        {
            await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                // Write MKV EBML magic bytes (1A 45 DF A3)
                fs.Write([0x1A, 0x45, 0xDF, 0xA3]);
                // Set length > 2.4 GiB (sparse file on Linux: zero physical disk blocks)
                fs.SetLength(2_546_562_455L);
            }

            var adapter = new DefaultContainerProbeAdapter();

            // ProbeMetadataOnlyAsync must NOT throw OverflowException!
            var metaEx = await Record.ExceptionAsync(() => adapter.ProbeMetadataOnlyAsync(tempFile));
            Assert.Null(metaEx);

            // ProbeAsync must NOT throw OverflowException!
            var probeEx = await Record.ExceptionAsync(() => adapter.ProbeAsync(tempFile));
            Assert.Null(probeEx);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    [Fact]
    public async Task AnimeSeries_GermanFirstVsGermanOnly_EvaluatesDualAudioAndSubbedFallbackAccurately()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"anime-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var animeRoot = Path.Combine(tempDir, "media", "anime");
            var s01Dir = Path.Combine(animeRoot, "AnimeSeries (2016)", "Season 01");
            var s03Dir = Path.Combine(animeRoot, "AnimeSeries (2016)", "Season 03");
            Directory.CreateDirectory(s01Dir);
            Directory.CreateDirectory(s03Dir);

            // Create media files on disk for disk scan
            var fileS01E10 = Path.Combine(s01Dir, "AnimeSeries - S01E10 [WEBDL-1080p].mkv");
            var fileS03E10 = Path.Combine(s03Dir, "AnimeSeries - S03E10 [WEBDL-1080p].mkv");
            await File.WriteAllBytesAsync(fileS01E10, [0x1A, 0x45, 0xDF, 0xA3, 0x00, 0x01, 0x02, 0x03]);
            await File.WriteAllBytesAsync(fileS03E10, [0x1A, 0x45, 0xDF, 0xA3, 0x00, 0x01, 0x02, 0x03]);

            const string pass = "AnimeSeries!2026";
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                    ["Managerr:MasterKey"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                    ["Managerr:SchedulerEnabled"] = "false",
                    ["Managerr:TestbedBootstrap:Enabled"] = "true",
                    ["Managerr:TestbedBootstrap:Username"] = "testadmin",
                    ["Managerr:TestbedBootstrap:Password"] = pass,
                }));
                b.ConfigureServices(s =>
                {
                    s.RemoveAll<IContainerProbeAdapter>();
                    s.AddSingleton<IContainerProbeAdapter, ConfigurableContainerProbeAdapter>();
                });
            });

            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
            var loginResp = await client.PostAsJsonAsync("/api/v1/auth/breakglass/login", new
            {
                usernameOrEmail = "testadmin",
                password = pass,
            });
            loginResp.EnsureSuccessStatusCode();
            var loginBody = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
            if (loginBody.TryGetProperty("csrfToken", out var csrf) && csrf.GetString() is { Length: > 0 } token)
            {
                client.DefaultRequestHeaders.Add("X-Managerr-Csrf", token);
            }

            long workId;
            long s01e10UnitId;
            long s03e10UnitId;
            long germanOnlyPolicyId;
            long origSubPolicyId;

            using (var scope = factory.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var catalog = sp.GetRequiredService<IPolymorphicCatalogService>();
                var profiles = sp.GetRequiredService<IComposableProfileService>();

                // 1. Create Anime library (mediaType = "anime") without explicit default_profile_id:
                // Must automatically default to "german-first-best-quality"!
                var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Anime", "anime", animeRoot));
                Assert.NotNull(lib.DefaultProfileId);

                var profileList = await profiles.ListProfilesAsync();
                var germanFirstProfile = profileList.First(p => p.Slug == "german-first-best-quality");
                Assert.Equal(germanFirstProfile.Id, lib.DefaultProfileId);

                // Fetch seeded policies
                var policyList = await profiles.ListLanguagePoliciesAsync();
                germanOnlyPolicyId = policyList.First(p => p.Slug == "german-only").Id;
                origSubPolicyId = policyList.First(p => p.Slug == "original-plus-subs").Id;

                // 2. Add AnimeSeries work with originalLanguage: "ja"
                var createWorkReq = new CreateMediaWorkRequest(
                    LibraryId: lib.Id,
                    Title: "AnimeSeries",
                    WorkKind: "series",
                    OriginalLanguage: "ja",
                    Year: 2016,
                    Monitored: true,
                    Containers:
                    [
                        new MetadataContainerSeed("season", 1, "Season 1", "2016-01-14",
                        [
                            new MetadataUnitSeed("episode", 10, 10, "God's Blessing on this Wonderful Party!", "2016-03-17"),
                        ]),
                        new MetadataContainerSeed("season", 3, "Season 3", "2024-04-10",
                        [
                            new MetadataUnitSeed("episode", 10, 10, "God's Blessing on These Triumphant Days!", "2024-06-19"),
                        ]),
                    ]);

                var addResp = await client.PostAsJsonAsync("/api/v1/catalog/works", createWorkReq);
                addResp.EnsureSuccessStatusCode();
                var createdTree = (await addResp.Content.ReadFromJsonAsync<MediaWorkDetailTree>())!;
                workId = createdTree.Work.Id;

                var s1 = createdTree.Containers.First(c => c.ContainerNumber == 1);
                var s3 = createdTree.Containers.First(c => c.ContainerNumber == 3);
                s01e10UnitId = s1.Units.First(u => u.UnitNumber == 10).Id;
                s03e10UnitId = s3.Units.First(u => u.UnitNumber == 10).Id;
            }

            // 3. Scan and verify the work on disk
            var scanResp = await client.PostAsync($"/api/v1/catalog/works/{workId}/scan-and-verify", null);
            scanResp.EnsureSuccessStatusCode();

            // 4. Inspect verification under default german-first:
            // S01E10 has audio: ["de", "ja"], sub: ["de"] (PGS bitmap) -> VerifiedPass, LanguageCutoffMet == true
            // Must NOT fail for missing "en" even though profile.RetainAudioLangs contains "en"!
            // S03E10 has audio: ["ja"], sub: ["de"] (PGS bitmap) -> VerifiedPass (fallback tier 3), LanguageCutoffMet == false
            var treeGFirst = await client.GetFromJsonAsync<MediaWorkDetailTree>($"/api/v1/catalog/works/{workId}");
            Assert.NotNull(treeGFirst);

            var s1e10Unit = treeGFirst.Containers.SelectMany(c => c.Units).First(u => u.Id == s01e10UnitId);
            var s1e10File = Assert.Single(s1e10Unit.Files);
            Assert.Equal("VerifiedPass", s1e10File.VerifiedStatus);
            Assert.True(s1e10Unit.LanguageCutoffMet);
            Assert.NotNull(s1e10File.LanguageVerification);
            Assert.True(s1e10File.LanguageVerification.SatisfiesLanguageCutoff);

            var s3e10Unit = treeGFirst.Containers.SelectMany(c => c.Units).First(u => u.Id == s03e10UnitId);
            var s3e10File = Assert.Single(s3e10Unit.Files);
            Assert.Equal("VerifiedPass", s3e10File.VerifiedStatus);
            Assert.False(s3e10Unit.LanguageCutoffMet); // Fallback subbed tier in german-first
            Assert.NotNull(s3e10File.LanguageVerification);
            Assert.False(s3e10File.LanguageVerification.SatisfiesLanguageCutoff);

            // 5. Switch work LanguagePolicyId to german-only via PATCH /api/v1/catalog/works/{id}
            // S03E10 must immediately flip to VerifiedFail (LanguageMismatch) while S01E10 stays VerifiedPass!
            var patchToGermanOnly = await client.PatchAsJsonAsync($"/api/v1/catalog/works/{workId}", new UpdateMediaWorkRequest(
                LanguagePolicyId: germanOnlyPolicyId));
            patchToGermanOnly.EnsureSuccessStatusCode();

            var treeGOnly = await client.GetFromJsonAsync<MediaWorkDetailTree>($"/api/v1/catalog/works/{workId}");
            Assert.NotNull(treeGOnly);

            var s1AfterOnly = treeGOnly.Containers.SelectMany(c => c.Units).First(u => u.Id == s01e10UnitId);
            var s1FileOnly = Assert.Single(s1AfterOnly.Files);
            Assert.Equal("VerifiedPass", s1FileOnly.VerifiedStatus);
            Assert.True(s1AfterOnly.LanguageCutoffMet);

            var s3AfterOnly = treeGOnly.Containers.SelectMany(c => c.Units).First(u => u.Id == s03e10UnitId);
            var s3FileOnly = Assert.Single(s3AfterOnly.Files);
            Assert.Equal("VerifiedFail", s3FileOnly.VerifiedStatus);
            Assert.False(s3AfterOnly.LanguageCutoffMet);
            Assert.NotNull(s3FileOnly.LanguageVerification);
            Assert.Equal("language_mismatch", s3FileOnly.LanguageVerification.FailureCode);

            // 6. Switch work LanguagePolicyId to original-plus-subs via PATCH /api/v1/catalog/works/{id}
            // S03E10 must immediately flip back to VerifiedPass with LanguageCutoffMet == true!
            var patchToOrigSubs = await client.PatchAsJsonAsync($"/api/v1/catalog/works/{workId}", new UpdateMediaWorkRequest(
                LanguagePolicyId: origSubPolicyId));
            patchToOrigSubs.EnsureSuccessStatusCode();

            var treeOrigSubs = await client.GetFromJsonAsync<MediaWorkDetailTree>($"/api/v1/catalog/works/{workId}");
            Assert.NotNull(treeOrigSubs);

            var s3AfterOrig = treeOrigSubs.Containers.SelectMany(c => c.Units).First(u => u.Id == s03e10UnitId);
            var s3FileOrig = Assert.Single(s3AfterOrig.Files);
            Assert.Equal("VerifiedPass", s3FileOrig.VerifiedStatus);
            Assert.True(s3AfterOrig.LanguageCutoffMet);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task LanguagePolicySecurityAndValidation_RejectsInvalidForeignKeys_AndEnforcesAbac()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"anime-sec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var animeRoot = Path.Combine(tempDir, "media", "anime");
        Directory.CreateDirectory(animeRoot);
        var pass = "TestAdminPass123!";

        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                        ["Managerr:MasterKey"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                        ["Managerr:SchedulerEnabled"] = "false",
                        ["Managerr:TestbedBootstrap:Enabled"] = "true",
                        ["Managerr:TestbedBootstrap:Username"] = "testadmin",
                        ["Managerr:TestbedBootstrap:Password"] = pass,
                    });
                });
            });

            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
            var loginResp = await client.PostAsJsonAsync("/api/v1/auth/breakglass/login", new
            {
                usernameOrEmail = "testadmin",
                password = pass,
            });
            loginResp.EnsureSuccessStatusCode();
            var loginBody = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
            if (loginBody.TryGetProperty("csrfToken", out var csrf) && csrf.GetString() is { Length: > 0 } token)
            {
                client.DefaultRequestHeaders.Add("X-Managerr-Csrf", token);
            }

            using var scope = factory.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Anime", "anime", animeRoot));

            // 1. POST /api/v1/catalog/works with invalid LanguagePolicyId -> 400 BadRequest
            var badLpResp = await client.PostAsJsonAsync("/api/v1/catalog/works", new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "Invalid LP Test",
                LanguagePolicyId: 999999));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, badLpResp.StatusCode);

            // 2. POST /api/v1/catalog/works with invalid QualityProfileId -> 400 BadRequest
            var badQpResp = await client.PostAsJsonAsync("/api/v1/catalog/works", new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "Invalid QP Test",
                QualityProfileId: 999999));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, badQpResp.StatusCode);

            // 3. Create a valid work
            var goodWorkResp = await client.PostAsJsonAsync("/api/v1/catalog/works", new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "Valid Work",
                OriginalLanguage: "ja"));
            goodWorkResp.EnsureSuccessStatusCode();
            var goodWork = (await goodWorkResp.Content.ReadFromJsonAsync<MediaWorkDetailTree>())!;

            // 4. PATCH with invalid LanguagePolicyId -> 400 BadRequest
            var patchBadLp = await client.PatchAsJsonAsync($"/api/v1/catalog/works/{goodWork.Work.Id}", new UpdateMediaWorkRequest(
                LanguagePolicyId: 888888));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, patchBadLp.StatusCode);

            // 5. PATCH with invalid QualityProfileId -> 400 BadRequest
            var patchBadQp = await client.PatchAsJsonAsync($"/api/v1/catalog/works/{goodWork.Work.Id}", new UpdateMediaWorkRequest(
                QualityProfileId: 888888));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, patchBadQp.StatusCode);

            // 6. PATCH on non-existent work -> 404 NotFound
            var patchNotFound = await client.PatchAsJsonAsync("/api/v1/catalog/works/777777", new UpdateMediaWorkRequest(
                Title: "Ghost Work"));
            Assert.Equal(System.Net.HttpStatusCode.NotFound, patchNotFound.StatusCode);

            // 7. Refresh on non-existent work -> 404 NotFound
            var refreshNotFound = await client.PostAsync("/api/v1/catalog/works/777777/refresh", null);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, refreshNotFound.StatusCode);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private sealed class ConfigurableContainerProbeAdapter : IContainerProbeAdapter
    {
        public Task<ContainerProbeResult> ProbeAsync(string filePath, double? expectedRuntimeSec = null, CancellationToken ct = default) =>
            ProbeCore(filePath);

        public Task<ContainerProbeResult> ProbeMetadataOnlyAsync(string filePath, CancellationToken ct = default, double? expectedRuntimeSec = null) =>
            ProbeCore(filePath);

        private static Task<ContainerProbeResult> ProbeCore(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            if (fileName.Contains("S01E10", StringComparison.OrdinalIgnoreCase))
            {
                // German + Japanese dual audio + German PGS bitmap subtitle (no text payload)
                return Task.FromResult(new ContainerProbeResult(
                    IntegrityPassed: true,
                    IntegrityFailureReason: null,
                    ContainerFormat: "matroska",
                    DurationSeconds: 1420.0,
                    Streams:
                    [
                        new ProbedRawStream(0, "video", "h264", 0, "und"),
                        new ProbedRawStream(1, "audio", "aac", 2, "de"),
                        new ProbedRawStream(2, "audio", "aac", 2, "ja"),
                        new ProbedRawStream(3, "subtitle", "hdmv_pgs_subtitle", 0, "de"),
                    ]));
            }

            if (fileName.Contains("S03E10", StringComparison.OrdinalIgnoreCase))
            {
                // Japanese audio + German PGS bitmap subtitle (no text payload)
                return Task.FromResult(new ContainerProbeResult(
                    IntegrityPassed: true,
                    IntegrityFailureReason: null,
                    ContainerFormat: "matroska",
                    DurationSeconds: 1420.0,
                    Streams:
                    [
                        new ProbedRawStream(0, "video", "h264", 0, "und"),
                        new ProbedRawStream(1, "audio", "aac", 2, "ja"),
                        new ProbedRawStream(2, "subtitle", "hdmv_pgs_subtitle", 0, "de"),
                    ]));
            }

            return Task.FromResult(new ContainerProbeResult(
                IntegrityPassed: true,
                IntegrityFailureReason: null,
                ContainerFormat: "matroska",
                DurationSeconds: 1400.0,
                Streams:
                [
                    new ProbedRawStream(0, "video", "h264", 0, "und"),
                    new ProbedRawStream(1, "audio", "aac", 2, "de"),
                ]));
        }
    }
}
