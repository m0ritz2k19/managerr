using Dapper;
using Managerr.Datastore;
using Managerr.Verification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Managerr.Phase4.Tests;

public sealed class QuarantineDeduplicationTests
{
    [Fact]
    public async Task ReverifyingSameDownload_RefreshesSingleOpenQuarantine_AndPrunesSupersededSnippets()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-quarantine-dedupe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Managerr:ConfigDirectory"] = tempDir,
                        ["Managerr:MasterKey"] = "quarantine-dedupe-test-master-key-32",
                        ["Managerr:SchedulerEnabled"] = "false",
                    });
                });
            });

            using var scope = factory.Services.CreateScope();
            var verifier = scope.ServiceProvider.GetRequiredService<ILanguageVerificationService>();
            var dbFactory = scope.ServiceProvider.GetRequiredService<ISqliteDbFactory>();

            var stagedMkv = Path.Combine(tempDir, "Shadow.Protocol.2024.German.1080p.WEB.H264-UNDTAG.mkv");
            await File.WriteAllBytesAsync(stagedMkv, [0x1A, 0x45, 0xDF, 0xA3, 0x01, 0x02]);

            RunVerificationRequest UndTagged(string hash) => new(
                StagedFilePath: stagedMkv,
                MediaVertical: "movies",
                ReleaseTitle: "Shadow.Protocol.2024.German.1080p.WEB.H264-UNDTAG",
                InfoHash: hash,
                ExpectedAudioLanguages: ["de"],
                SyntheticProbeOverride: new ContainerProbeResult(
                    true, null, "mkv", 3600, [new ProbedRawStream(1, "audio", "ac3", 6, "und")]));

            var first = await verifier.VerifyStagedFileAsync(UndTagged("nzo:03b2dc11"));
            var second = await verifier.VerifyStagedFileAsync(UndTagged("nzo:03b2dc11"));
            var third = await verifier.VerifyStagedFileAsync(UndTagged("NZO:03B2DC11"));

            Assert.All([first, second, third], r => Assert.Equal(VerificationVerdicts.QuarantineReview, r.Verdict));
            Assert.NotNull(first.QuarantineId);
            Assert.Equal(first.QuarantineId, second.QuarantineId);
            Assert.Equal(first.QuarantineId, third.QuarantineId);

            var open = await verifier.ListQuarantineAsync("quarantined");
            var entry = Assert.Single(open);
            Assert.Equal(third.JobId, entry.VerificationJobId);

            // Only the latest verification's snippet folder survives
            var quarantineRoot = Path.Combine(tempDir, "quarantine");
            Assert.False(Directory.Exists(Path.Combine(quarantineRoot, $"job-{first.JobId}")));
            Assert.False(Directory.Exists(Path.Combine(quarantineRoot, $"job-{second.JobId}")));
            Assert.True(entry.SnippetPaths.All(File.Exists));

            // A reviewed entry is closed, so the next verification of the same release opens a fresh one
            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                (c, tx) => c.ExecuteAsync("UPDATE quarantined_imports SET status = 'approved' WHERE id = @Id;", new { entry.Id }, tx));
            var afterReview = await verifier.VerifyStagedFileAsync(UndTagged("nzo:03b2dc11"));
            Assert.NotEqual(first.QuarantineId, afterReview.QuarantineId);

            await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core);
            Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM quarantined_imports;"));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task PruneOrphanedQuarantineSnippets_DeletesUnreferencedJobFolders_Only()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-quarantine-prune-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Managerr:ConfigDirectory"] = tempDir,
                        ["Managerr:MasterKey"] = "quarantine-prune-test-master-key-32b",
                        ["Managerr:SchedulerEnabled"] = "false",
                    });
                });
            });

            using var scope = factory.Services.CreateScope();
            var verifier = scope.ServiceProvider.GetRequiredService<ILanguageVerificationService>();

            var stagedMkv = Path.Combine(tempDir, "Ambiguous.Release.mkv");
            await File.WriteAllBytesAsync(stagedMkv, [0x1A, 0x45, 0xDF, 0xA3, 0x01, 0x02]);
            var report = await verifier.VerifyStagedFileAsync(new RunVerificationRequest(
                StagedFilePath: stagedMkv,
                MediaVertical: "movies",
                ReleaseTitle: "Ambiguous.MULTI.1080p.WEB-DL",
                InfoHash: "nzo:keep-me",
                ExpectedAudioLanguages: ["de"],
                SyntheticProbeOverride: new ContainerProbeResult(
                    true, null, "mkv", 3600, [new ProbedRawStream(1, "audio", "ac3", 6, "und")])));
            Assert.Equal(VerificationVerdicts.QuarantineReview, report.Verdict);

            var orphanDir = Path.Combine(tempDir, "quarantine", "job-99999");
            Directory.CreateDirectory(orphanDir);
            await File.WriteAllTextAsync(Path.Combine(orphanDir, "snippet-1.opus"), "stale");

            var pruned = await verifier.PruneOrphanedQuarantineSnippetsAsync();

            Assert.Equal(1, pruned);
            Assert.False(Directory.Exists(orphanDir));
            Assert.True(Directory.Exists(Path.Combine(tempDir, "quarantine", $"job-{report.JobId}")));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }
}
