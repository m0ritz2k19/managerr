using Managerr.Catalog;
using Managerr.Host.Services;
using Managerr.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Managerr.Phase5.Tests;

public sealed class DiskScanDuplicateFileTests
{
    [Fact]
    public async Task DiskScan_KeepsOneFilePerEpisode_AndListsExtraFilesAsUnmappedDuplicates()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-scan-dupes-{Guid.NewGuid():N}");
        var tvRoot = Path.Combine(tempDir, "storage", "media", "tv");
        var seasonDir = Path.Combine(tvRoot, "Pioneer One (2010)", "Season 01");
        Directory.CreateDirectory(seasonDir);

        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                        ["Managerr:MasterKey"] = "scan-dupes-test-master-key-32bytes!!",
                        ["Managerr:SchedulerEnabled"] = "false",
                    });
                });
            });

            using var scope = factory.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var diskScan = scope.ServiceProvider.GetRequiredService<IMediaManagementAndDiskScanService>();
            var taskManager = scope.ServiceProvider.GetRequiredService<ITaskManagerService>();

            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest(Name: "TV", MediaType: "tv", RootFolderPath: tvRoot));
            var tree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "Pioneer One",
                WorkKind: "series",
                AltTitles: [],
                OriginalLanguage: "en",
                Year: 2010,
                Status: "ended",
                Monitored: true,
                ExternalIds: new Dictionary<string, string> { ["tvdb"] = "170551" },
                Containers:
                [
                    new MetadataContainerSeed("season", 1, "Season 1", "2010-06-16",
                    [
                        new MetadataUnitSeed("episode", 1, 1, "Earthbound", "2010-06-16"),
                        new MetadataUnitSeed("episode", 2, 2, "The Man From Mars", "2010-07-01"),
                        new MetadataUnitSeed("episode", 3, 3, "Alone in the Night", "2010-07-15"),
                    ]),
                ]));
            var units = tree.Containers.Single().Units;

            // E1: two files on disk (the testlab case); E2: one file; E3 already has an imported file elsewhere
            await File.WriteAllBytesAsync(Path.Combine(seasonDir, "Pioneer One (2010) - S01E01 [WEB-DL 1080p AVC].mkv"), new byte[4096]);
            await File.WriteAllBytesAsync(Path.Combine(seasonDir, "Pioneer One (2010) - S01E01 - Earthbound.mkv"), new byte[2048]);
            await File.WriteAllBytesAsync(Path.Combine(seasonDir, "Pioneer One (2010) - S01E02 - The Man From Mars.mkv"), new byte[2048]);
            await File.WriteAllBytesAsync(Path.Combine(seasonDir, "Pioneer One (2010) - S01E03 - Alone in the Night.mkv"), new byte[2048]);
            await File.WriteAllBytesAsync(Path.Combine(seasonDir, "Pioneer One (2010) - S01E03 [WEB-DL 1080p AVC].mkv"), new byte[4096]);
            var e3 = units.Single(u => u.UnitNumber == 3);
            await catalog.AttachFileAsync(new AttachMediaFileRequest(
                e3.Id, "Pioneer One (2010)/Season 01/Pioneer One (2010) - S01E03 [WEB-DL 1080p AVC].mkv", 4096, "WEBDL-1080p",
                VerifiedStatus: "VerifiedPass"));

            for (var pass = 1; pass <= 2; pass++)
            {
                await diskScan.ScanLibraryAsync(lib.Id);

                var after = await catalog.GetWorkTreeAsync(tree.Work.Id);
                Assert.All(after!.Containers.Single().Units, u => Assert.Single(u.Files));
                Assert.Contains("S01E03 [WEB-DL 1080p AVC]", after.Containers.Single().Units.Single(u => u.UnitNumber == 3).Files[0].RelativePath);

                var duplicates = (await diskScan.ListUnmappedFilesAsync(lib.Id))
                    .Where(u => u.Reason.StartsWith("Duplicate:", StringComparison.Ordinal))
                    .Select(u => u.FileName)
                    .OrderBy(n => n)
                    .ToList();
                Assert.Equal(2, duplicates.Count);
                Assert.Contains(duplicates, n => n.Contains("S01E01", StringComparison.Ordinal));
                Assert.Contains("Pioneer One (2010) - S01E03 - Alone in the Night.mkv", duplicates);
            }

            Assert.DoesNotContain(await taskManager.GetSystemHealthChecksAsync(), c => c.Source == "Library");

            // A second file attached by other means is reported
            await File.WriteAllBytesAsync(Path.Combine(seasonDir, "extra.mkv"), new byte[1024]);
            await catalog.AttachFileAsync(new AttachMediaFileRequest(
                e3.Id, "Pioneer One (2010)/Season 01/extra.mkv", 1024, "WEBDL-1080p", VerifiedStatus: "VerifiedPass"));
            var check = Assert.Single(await taskManager.GetSystemHealthChecksAsync(), c => c.Source == "Library");
            Assert.Contains("1 episode(s)/item(s) have more than one media file", check.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }
}
