using System.Net;
using System.Net.Http.Json;
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

public sealed class Wave3MediaManagementAndMassEditorTests
{
    [Fact]
    public async Task DiskScan_AutoAdoptsMatchingFiles_PrunesDeleted_MapsUnmapped_AndExecutesBulkRename()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-wave3-disk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var tvRoot = Path.Combine(tempDir, "storage", "media", "tv");
            Directory.CreateDirectory(tvRoot);

            await using var factory = CreateFactory(tempDir, "Wave3DiskPass!2026");
            using var scope = factory.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var profiles = scope.ServiceProvider.GetRequiredService<IComposableProfileService>();

            var tvProfile = (await profiles.ListProfilesAsync()).First(p => p.Slug == "german-first-best-quality");
            var tvLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest(
                Name: "TV Root",
                MediaType: "tv",
                RootFolderPath: tvRoot,
                DefaultProfileId: tvProfile.Id));

            var workTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: "Avatar: Seven Havens",
                WorkKind: "series",
                OriginalLanguage: "en",
                Year: 2026,
                Monitored: true,
                ExternalIds: new Dictionary<string, string> { ["tvdb"] = "450001" },
                Containers:
                [
                    new MetadataContainerSeed(
                        ContainerKind: "season",
                        ContainerNumber: 1,
                        Title: "Season 1",
                        ReleaseDate: "2026-02-01",
                        Units:
                        [
                            new MetadataUnitSeed("episode", 1, 1, "Deleted Episode", "2026-02-01"),
                            new MetadataUnitSeed("episode", 2, 2, "The Convergence", "2026-02-08"),
                            new MetadataUnitSeed("episode", 3, 3, "Manual Target", "2026-02-15"),
                        ]),
                ]));

            var units = workTree.Containers[0].Units;
            var ep1 = units.Single(u => u.UnitNumber == 1);
            var ep2 = units.Single(u => u.UnitNumber == 2);
            var ep3 = units.Single(u => u.UnitNumber == 3);

            // 1. Attach a DB file record for S01E01 whose file does NOT exist on disk (to verify missing-file pruning)
            await catalog.AttachFileAsync(new AttachMediaFileRequest(
                UnitId: ep1.Id,
                RelativePath: "Avatar Seven Havens (2026)/Season 01/missing-on-disk.mkv",
                SizeBytes: 400_000_000,
                QualityName: "WEBDL-1080p",
                DetectedAudioLangs: ["de"]));

            // 2. Create a real unlinked S01E02 file + subtitle sidecar on disk (to verify auto-adoption)
            var s01Dir = Path.Combine(tvRoot, "Avatar Seven Havens (2026)", "Season 01");
            Directory.CreateDirectory(s01Dir);
            var ep2DiskFile = Path.Combine(s01Dir, "Avatar.Seven.Havens.S01E02.German.DL.1080p.WEB-DL.H264-GRP.mkv");
            var ep2Sidecar = Path.Combine(s01Dir, "Avatar.Seven.Havens.S01E02.German.DL.1080p.WEB-DL.H264-GRP.de.srt");
            await File.WriteAllBytesAsync(ep2DiskFile, new byte[4096]);
            await File.WriteAllTextAsync(ep2Sidecar, "1\n00:00:01,000 --> 00:00:03,000\nHallo Welt\n");

            // 3. Create an unmapped mystery file on disk
            var mysteryFile = Path.Combine(tvRoot, "Mystery.Show.S09E99.1080p.WEB-DL.mkv");
            await File.WriteAllBytesAsync(mysteryFile, new byte[2048]);

            using var client = await CreateAuthenticatedClientAsync(factory, "Wave3DiskPass!2026");

            // Trigger Refresh & Scan on library
            var scanResp = await client.PostAsync($"/api/v1/libraries/{tvLib.Id}/scan", content: null);
            scanResp.EnsureSuccessStatusCode();
            var scanReport = await scanResp.Content.ReadFromJsonAsync<DiskScanReportDto>();
            Assert.NotNull(scanReport);
            Assert.Equal(1, scanReport.MissingRemovedCount);
            Assert.Equal(1, scanReport.AutoAdoptedCount);
            Assert.Equal(1, scanReport.UnmappedCount);

            // Verify unmapped file is listed and can be interactively mapped to S01E03
            var unmapped = await client.GetFromJsonAsync<List<UnmappedMediaFileDto>>($"/api/v1/libraries/unmapped-files?libraryId={tvLib.Id}");
            Assert.NotNull(unmapped);
            Assert.Single(unmapped);
            Assert.Equal("Mystery.Show.S09E99.1080p.WEB-DL.mkv", unmapped[0].FileName);

            var mapResp = await client.PostAsJsonAsync("/api/v1/libraries/unmapped-files/map", new MapUnmappedFilesRequest(
            [
                new MapUnmappedFileItemRequest(
                    UnmappedFileId: unmapped[0].Id,
                    WorkId: workTree.Work.Id,
                    UnitIds: [ep3.Id],
                    QualityName: "WEBDL-1080p",
                    Languages: ["de", "en"],
                    RenameToTemplate: true),
            ]));
            mapResp.EnsureSuccessStatusCode();
            var mapReport = await mapResp.Content.ReadFromJsonAsync<MapUnmappedFilesReportDto>();
            Assert.NotNull(mapReport);
            Assert.Equal(1, mapReport.MappedCount);

            // Update Naming Template with {tvdb-{tvdbId}} + {Episode Title} + dash colon replacement
            var customTvTpl = "{Series TitleYear} {tvdb-{tvdbId}}/Season {Season:00}/{Series Title} - S{Season:00}E{Episode:00} - {Episode Title} [{Quality Full}]";
            var putNaming = await client.PutAsJsonAsync("/api/v1/settings/naming", new UpdateNamingConfigRequest(
                RenameMediaFiles: true,
                ColonReplacement: "dash",
                Templates: new Dictionary<string, string> { ["tv"] = customTvTpl }));
            putNaming.EnsureSuccessStatusCode();

            var previewSampleResp = await client.PostAsJsonAsync("/api/v1/settings/naming/preview", new NamingPreviewRequest("tv"));
            previewSampleResp.EnsureSuccessStatusCode();
            var sampleDto = await previewSampleResp.Content.ReadFromJsonAsync<NamingPreviewResultDto>();
            Assert.NotNull(sampleDto);
            Assert.Contains("{tvdb-421099}", sampleDto.SampleRelativePath);
            Assert.Contains("Avatar- Seven Havens", sampleDto.SampleRelativePath);

            // Preview bulk rename on work
            var renamePreviews = await client.GetFromJsonAsync<List<RenameFilePreviewItemDto>>(
                $"/api/v1/catalog/works/{workTree.Work.Id}/rename-preview");
            Assert.NotNull(renamePreviews);
            Assert.Equal(2, renamePreviews.Count);
            Assert.All(renamePreviews, p =>
            {
                Assert.True(p.NeedsRename);
                Assert.Contains("{tvdb-450001}", p.NewRelativePath);
            });

            // Execute bulk rename on work
            var execResp = await client.PostAsJsonAsync(
                $"/api/v1/catalog/works/{workTree.Work.Id}/rename-execute",
                new RenameExecuteRequest(null));
            execResp.EnsureSuccessStatusCode();
            var execReport = await execResp.Content.ReadFromJsonAsync<RenameExecuteReportDto>();
            Assert.NotNull(execReport);
            Assert.Equal(2, execReport.RenamedCount);
            Assert.Equal(0, execReport.FailedCount);

            // Verify renamed media file AND renamed .de.srt sidecar exist on disk at the new path
            var ep2Preview = renamePreviews.Single(p => p.EpisodeCode == "S01E02");
            var expectedNewEp2Full = Path.Combine(tvRoot, ep2Preview.NewRelativePath);
            Assert.True(File.Exists(expectedNewEp2Full));
            var expectedNewSidecar = Path.ChangeExtension(expectedNewEp2Full, null) + ".de.srt";
            Assert.True(File.Exists(expectedNewSidecar));

            // Verify MediaFileRenamed recorded in media_history
            var hist = await client.GetFromJsonAsync<WantedPagedResponse<MediaHistoryItemDto>>(
                "/api/v1/activity/history?eventType=MediaFileRenamed");
            Assert.NotNull(hist);
            Assert.Equal(2, hist.TotalCount);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task MonitorPresets_MinimumAvailabilityGating_AndMassEditorMoveAndDelete()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-wave3-mass-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var tvRootA = Path.Combine(tempDir, "storage", "tv-a");
            var tvRootB = Path.Combine(tempDir, "storage", "tv-b");
            var moviesRoot = Path.Combine(tempDir, "storage", "movies");
            Directory.CreateDirectory(tvRootA);
            Directory.CreateDirectory(tvRootB);
            Directory.CreateDirectory(moviesRoot);

            await using var factory = CreateFactory(tempDir, "Wave3MassPass!2026");
            using var scope = factory.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var profiles = scope.ServiceProvider.GetRequiredService<IComposableProfileService>();
            var allProfiles = await profiles.ListProfilesAsync();
            var prof1 = allProfiles[0];
            var prof2 = allProfiles.Count > 1 ? allProfiles[1] : allProfiles[0];

            var libA = await catalog.CreateLibraryAsync(new CreateLibraryRequest("TV A", "tv", tvRootA, DefaultProfileId: prof1.Id));
            var libB = await catalog.CreateLibraryAsync(new CreateLibraryRequest("TV B", "tv", tvRootB, DefaultProfileId: prof1.Id));
            var movieLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Movies", "movies", moviesRoot, DefaultProfileId: prof1.Id));

            var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)).ToString("yyyy-MM-dd");
            var show = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: libA.Id,
                Title: "Preset Odyssey",
                WorkKind: "series",
                Year: 2025,
                Monitored: true,
                Containers:
                [
                    new MetadataContainerSeed("season", 0, "Specials", "2025-01-01",
                    [
                        new MetadataUnitSeed("episode", 1, null, "Behind the Scenes", "2025-01-01"),
                    ]),
                    new MetadataContainerSeed("season", 1, "Season 1", "2025-02-01",
                    [
                        new MetadataUnitSeed("episode", 1, 1, "Pilot", "2025-02-01"),
                        new MetadataUnitSeed("episode", 2, 2, "Second", "2025-02-08"),
                    ]),
                    new MetadataContainerSeed("season", 2, "Season 2", futureDate,
                    [
                        new MetadataUnitSeed("episode", 1, 3, "Future Premiere", futureDate),
                    ]),
                ]));

            // Attach an existing file on disk to S01E02
            var s01e02 = show.Containers.Single(c => c.ContainerNumber == 1).Units.Single(u => u.UnitNumber == 2);
            var relFile = Path.Combine("Preset Odyssey (2025)", "Season 01", "S01E02.mkv");
            var fullFileA = Path.Combine(tvRootA, relFile);
            Directory.CreateDirectory(Path.GetDirectoryName(fullFileA)!);
            await File.WriteAllBytesAsync(fullFileA, new byte[2048]);
            await catalog.AttachFileAsync(new AttachMediaFileRequest(s01e02.Id, relFile, 2048, "WEBDL-1080p"));

            using var client = await CreateAuthenticatedClientAsync(factory, "Wave3MassPass!2026");

            // 1. Test Pilot preset -> only S01E01 monitored
            var pilotResp = await client.PostAsJsonAsync($"/api/v1/catalog/works/{show.Work.Id}/monitor-preset", new { monitorPreset = "pilot" });
            pilotResp.EnsureSuccessStatusCode();
            var pilotTree = (await pilotResp.Content.ReadFromJsonAsync<MediaWorkDetailTree>())!;
            var pilotMonitoredUnits = pilotTree.Containers.SelectMany(c => c.Units).Where(u => u.Monitored).ToList();
            Assert.Single(pilotMonitoredUnits);
            Assert.Equal("Pilot", pilotMonitoredUnits[0].Title);

            // 2. Test Existing preset -> only S01E02 (which has a file) monitored
            var existingResp = await client.PostAsJsonAsync($"/api/v1/catalog/works/{show.Work.Id}/monitor-preset", new { monitorPreset = "existing" });
            var existingTree = (await existingResp.Content.ReadFromJsonAsync<MediaWorkDetailTree>())!;
            var existingMonitoredUnits = existingTree.Containers.SelectMany(c => c.Units).Where(u => u.Monitored).ToList();
            Assert.Single(existingMonitoredUnits);
            Assert.Equal("Second", existingMonitoredUnits[0].Title);

            // 3. Test Missing preset -> S01E01 & S02E01 monitored (Specials excluded by default, S01E02 has file)
            var missingPresetResp = await client.PostAsJsonAsync($"/api/v1/catalog/works/{show.Work.Id}/monitor-preset", new { monitorPreset = "missing" });
            var missingPresetTree = (await missingPresetResp.Content.ReadFromJsonAsync<MediaWorkDetailTree>())!;
            var missingUnits = missingPresetTree.Containers.SelectMany(c => c.Units).Where(u => u.Monitored).Select(u => u.Title).ToArray();
            Assert.Equal(2, missingUnits.Length);
            Assert.Contains("Pilot", missingUnits);
            Assert.Contains("Future Premiere", missingUnits);

            // 4. Test MonitorSpecials -> Season 0 becomes monitored without unmonitoring Pilot/Future Premiere
            var specResp = await client.PostAsJsonAsync($"/api/v1/catalog/works/{show.Work.Id}/monitor-preset", new { monitorPreset = "monitorSpecials" });
            var specTree = (await specResp.Content.ReadFromJsonAsync<MediaWorkDetailTree>())!;
            Assert.True(specTree.Containers.Single(c => c.ContainerNumber == 0).Monitored);
            Assert.Equal(3, specTree.Containers.SelectMany(c => c.Units).Count(u => u.Monitored));

            // 5. Minimum Availability movie gating
            var movie = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: movieLib.Id,
                Title: "Dune Messiah",
                WorkKind: "movies",
                Year: DateTime.UtcNow.Year + 1,
                Status: "announced",
                Monitored: true,
                MinimumAvailability: "released",
                Containers:
                [
                    new MetadataContainerSeed("feature_collection", 1, "Dune Messiah", futureDate,
                    [
                        new MetadataUnitSeed("feature", 1, null, "Dune Messiah", futureDate),
                    ]),
                ]));

            var wantedBefore = await client.GetFromJsonAsync<WantedPagedResponse<WantedUnitItemDto>>(
                "/api/v1/wanted/missing?vertical=movies&releasedOnly=true");
            Assert.NotNull(wantedBefore);
            Assert.Empty(wantedBefore.Items);

            // Bulk-edit movie MinimumAvailability -> 'announced' and verify it becomes eligible immediately!
            var movieBulkResp = await client.PostAsJsonAsync("/api/v1/catalog/works/bulk-edit", new BulkEditWorksRequest(
                WorkIds: [movie.Work.Id],
                MinimumAvailability: "announced",
                Tags: ["sci-fi", "imax"],
                TagMode: "add"));
            movieBulkResp.EnsureSuccessStatusCode();

            var wantedAfter = await client.GetFromJsonAsync<WantedPagedResponse<WantedUnitItemDto>>(
                "/api/v1/wanted/missing?vertical=movies&releasedOnly=true");
            Assert.NotNull(wantedAfter);
            Assert.Single(wantedAfter.Items);
            Assert.Equal("Dune Messiah", wantedAfter.Items[0].WorkTitle);

            // 6. Mass Editor: move 'Preset Odyssey' from libA -> libB with MoveFilesOnLibraryChange=true + QualityProfile update
            var moveBulkResp = await client.PostAsJsonAsync("/api/v1/catalog/works/bulk-edit", new BulkEditWorksRequest(
                WorkIds: [show.Work.Id],
                LibraryId: libB.Id,
                MoveFilesOnLibraryChange: true,
                QualityProfileId: prof2.Id));
            moveBulkResp.EnsureSuccessStatusCode();
            var moveReport = await moveBulkResp.Content.ReadFromJsonAsync<BulkEditWorksReportDto>();
            Assert.NotNull(moveReport);
            Assert.Equal(1, moveReport.UpdatedCount);
            Assert.Equal(1, moveReport.MovedFilesCount);
            Assert.False(File.Exists(fullFileA));
            var fullFileB = Path.Combine(tvRootB, relFile);
            Assert.True(File.Exists(fullFileB));

            // 7. Mass Editor: bulk delete with deleteFilesOnDisk=true
            var delBulkResp = await client.PostAsJsonAsync("/api/v1/catalog/works/bulk-edit", new BulkEditWorksRequest(
                WorkIds: [show.Work.Id],
                DeleteWorks: true,
                DeleteFilesOnDisk: true));
            delBulkResp.EnsureSuccessStatusCode();
            Assert.False(File.Exists(fullFileB));
            var checkGone = await client.GetAsync($"/api/v1/catalog/works/{show.Work.Id}");
            Assert.Equal(HttpStatusCode.NotFound, checkGone.StatusCode);
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
                    ["Managerr:MasterKey"] = "wave3-media-mgmt-master-key-32b!",
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
