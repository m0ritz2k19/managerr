using System.Reflection;
using Dapper;
using Managerr.Datastore;
using Managerr.Datastore.Migrations;
using Managerr.Verification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Managerr.Phase4.Tests;

public sealed class VerificationReportStorageTests
{
    [Fact]
    public async Task Reports_DoNotPersistPcmSamples_ButFakeDubDetectionStillUsesThem()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateFactory(tempDir);
            using var scope = factory.Services.CreateScope();
            var verifier = scope.ServiceProvider.GetRequiredService<ILanguageVerificationService>();
            var dbFactory = scope.ServiceProvider.GetRequiredService<ISqliteDbFactory>();

            var mkv = Path.Combine(tempDir, "Movie.2025.MULTI.1080p.WEB-DL-FAKEGRP.mkv");
            await File.WriteAllBytesAsync(mkv, [0x1A, 0x45, 0xDF, 0xA3, 0x01, 0x02]);

            // 48k samples per window, like a real stratified window preview
            var pcm = Enumerable.Range(0, 48_000).Select(i => (float)Math.Sin(i / 7.0)).ToArray();
            var report = await verifier.VerifyStagedFileAsync(new RunVerificationRequest(
                StagedFilePath: mkv,
                MediaVertical: "movies",
                ReleaseTitle: "Movie.2025.MULTI.1080p.WEB-DL-FAKEGRP",
                ExpectedAudioLanguages: ["de", "en"],
                SyntheticProbeOverride: new ContainerProbeResult(
                    true, null, "mkv", 5400,
                    [
                        new ProbedRawStream(1, "audio", "aac", 2, "en", AudioWaveformPreview: pcm),
                        new ProbedRawStream(2, "audio", "aac", 2, "de", AudioWaveformPreview: pcm),
                    ])));

            // Identical waveforms on both tracks are still caught in memory
            Assert.Equal(VerificationVerdicts.VerifiedFail, report.Verdict);
            Assert.Contains(report.Streams, s => s.IsDuplicateFakeDub);

            await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core);
            var stored = await conn.ExecuteScalarAsync<string>(
                "SELECT report_json FROM verification_jobs WHERE id = @Id;", new { Id = report.JobId });
            Assert.DoesNotContain("pcmFingerprint", stored!, StringComparison.OrdinalIgnoreCase);
            Assert.True(stored!.Length < 20_000, $"Stored report is {stored.Length} bytes");

            var listed = Assert.Single(await verifier.ListRecentReportsAsync());
            Assert.All(listed.Streams.SelectMany(s => s.Windows ?? []), w => Assert.Null(w.PcmFingerprint));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Migration018_StripsStoredPcmSamples_AndKeepsTheRestOfTheReport()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateFactory(tempDir);
            using var scope = factory.Services.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<ISqliteDbFactory>();

            const string legacyReport = """
                {"jobId":1,"verdict":"QuarantineReview","overallConfidence":0.35,"streams":[
                  {"streamIndex":1,"method":"stage3","windows":[
                    {"offsetSec":648,"speechSec":9.5,"detectedLanguage":"und","probability":0.25,"pcmFingerprint":[0.1,-0.2,0.3]},
                    {"offsetSec":1224,"speechSec":9.5,"detectedLanguage":"und","probability":0.25,"pcmFingerprint":[0.4]}]},
                  {"streamIndex":2,"method":"stage1","windows":null}]}
                """;

            var upSql = ((IReadOnlyList<SqlMigration>)typeof(SchemaMigrator)
                    .GetField("CoreMigrations", BindingFlags.NonPublic | BindingFlags.Static)!
                    .GetValue(null)!)
                .Single(m => m.Version == 18)
                .UpSql;

            var migrated = await dbFactory.ExecuteWriteAsync(DatabaseKind.Core, async (conn, tx) =>
            {
                var id = await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO verification_jobs (staged_file_path, media_vertical, verdict, overall_confidence, report_json, duration_ms, created_at)
                    VALUES ('/x.mkv', 'movies', 'QuarantineReview', 0.35, @Json, 1, '2026-11-02T20:24:09+00:00');
                    SELECT last_insert_rowid();
                    """,
                    new { Json = legacyReport }, tx);
                await conn.ExecuteAsync(upSql, transaction: tx);
                return await conn.ExecuteScalarAsync<string>("SELECT report_json FROM verification_jobs WHERE id = @Id;", new { Id = id }, tx);
            });

            Assert.DoesNotContain("pcmFingerprint", migrated!);
            Assert.Contains("\"offsetSec\":1224", migrated);
            Assert.Contains("\"detectedLanguage\":\"und\"", migrated);
            Assert.Contains("\"windows\":null", migrated);
            Assert.Contains("\"verdict\":\"QuarantineReview\"", migrated);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task VacuumAfterMigration_ShrinksTheDatabaseFileInWalMode()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateFactory(tempDir);
            using var scope = factory.Services.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<ISqliteDbFactory>();
            var migrator = scope.ServiceProvider.GetRequiredService<ISchemaMigrator>();
            var dbPath = dbFactory.GetDatabasePath(DatabaseKind.Core);

            // ~20 MB of pages that become free, like the stripped PCM samples
            await dbFactory.ExecuteWriteAsync(DatabaseKind.Core, async (conn, tx) =>
            {
                await conn.ExecuteAsync("CREATE TABLE bloat (v BLOB);", transaction: tx);
                await conn.ExecuteAsync(
                    "WITH RECURSIVE c(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM c WHERE i < 2000) INSERT INTO bloat SELECT randomblob(10000) FROM c;",
                    transaction: tx);
            });
            await dbFactory.ExecuteWriteAsync(DatabaseKind.Core, (conn, tx) => conn.ExecuteAsync("DROP TABLE bloat;", transaction: tx));
            await using (var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core))
            {
                await conn.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE);");
            }

            var bloated = new FileInfo(dbPath).Length;
            Assert.True(bloated > 15_000_000, $"Expected a bloated file, got {bloated} bytes");

            // Re-run the no-op 019 migration, which only exists to vacuum
            await dbFactory.ExecuteWriteAsync(DatabaseKind.Core, (conn, tx) =>
                conn.ExecuteAsync("DELETE FROM schema_migrations WHERE version = 19;", transaction: tx));
            await migrator.MigrateAllAsync();

            var compacted = new FileInfo(dbPath).Length;
            Assert.True(compacted < 5_000_000, $"Expected the file to shrink after VACUUM, still {compacted} bytes (was {bloated})");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(string tempDir) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = tempDir,
                    ["Managerr:MasterKey"] = "report-storage-test-master-key-32byt",
                    ["Managerr:SchedulerEnabled"] = "false",
                });
            });
        });

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"managerr-report-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
    }
}
