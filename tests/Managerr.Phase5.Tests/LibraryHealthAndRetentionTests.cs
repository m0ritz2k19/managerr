using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Managerr.Catalog;
using Managerr.Datastore;
using Managerr.Host.Services;
using Managerr.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Managerr.Phase5.Tests;

public sealed class LibraryHealthAndRetentionTests
{
    [Fact]
    public async Task SystemHealth_ExposesSingleSourceVersion020_AndGitRevisionInfo()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateFactory(tempDir);
            using var client = factory.CreateClient();

            var health = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/health");
            Assert.Equal("healthy", health.GetProperty("status").GetString());
            Assert.Equal("0.2.0", health.GetProperty("version").GetString());

            var infoVer = health.GetProperty("informationalVersion").GetString();
            Assert.NotNull(infoVer);
            Assert.StartsWith("0.2.0", infoVer, StringComparison.Ordinal);

            var prevSha = Environment.GetEnvironmentVariable("MANAGERR_GIT_SHA");
            try
            {
                Environment.SetEnvironmentVariable("MANAGERR_GIT_SHA", "deadbeef1234");
                var healthWithEnvSha = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/health");
                Assert.Equal("0.2.0", healthWithEnvSha.GetProperty("version").GetString());
                Assert.Equal("deadbeef1234", healthWithEnvSha.GetProperty("gitCommit").GetString());
                Assert.Equal("0.2.0+deadbeef1234", healthWithEnvSha.GetProperty("informationalVersion").GetString());
            }
            finally
            {
                Environment.SetEnvironmentVariable("MANAGERR_GIT_SHA", prevSha);
            }
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task LibraryHealthChecks_DetectAndResolve_Duplicates_NonCanonicalQualities_WrongKind_AndMissingDiskFiles()
    {
        var tempDir = CreateTempDir();
        var moviesRoot = Path.Combine(tempDir, "media", "movies");
        var tvRoot = Path.Combine(tempDir, "media", "tv");
        var booksRoot = Path.Combine(tempDir, "media", "books");
        Directory.CreateDirectory(moviesRoot);
        Directory.CreateDirectory(tvRoot);
        Directory.CreateDirectory(booksRoot);

        try
        {
            await using var factory = CreateFactory(tempDir);
            using var scope = factory.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var dbFactory = scope.ServiceProvider.GetRequiredService<ISqliteDbFactory>();

            var moviesLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Movies", "movies", moviesRoot));
            var tvLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("TV", "tv", tvRoot));
            var booksLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Books", "ebooks", booksRoot));

            // 1. Create a movie work with 2 physical files on the same unit (duplicate) + non-canonical qualities ("MKV", "WEB-DL-1080p")
            var movieTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: moviesLib.Id,
                Title: "Dune Part Two",
                WorkKind: "movies",
                Year: 2024));
            var movieUnit = movieTree.Containers.Single().Units.Single();

            var file1Rel = "Dune Part Two (2024)/Dune.Part.Two.2024.2160p.BluRay.REMUX.HEVC.mkv";
            var file2Rel = "Dune Part Two (2024)/Dune.Part.Two.2024.1080p.WEB-DL.H264.mkv";
            Directory.CreateDirectory(Path.Combine(moviesRoot, "Dune Part Two (2024)"));
            await File.WriteAllBytesAsync(Path.Combine(moviesRoot, file1Rel), new byte[8192]);
            await File.WriteAllBytesAsync(Path.Combine(moviesRoot, file2Rel), new byte[4096]);

            var attached1 = await catalog.AttachFileAsync(new AttachMediaFileRequest(
                UnitId: movieUnit.Id,
                RelativePath: file1Rel,
                SizeBytes: 8192,
                QualityName: "MKV",
                VerifiedStatus: "VerifiedPass"));
            var attached2 = await catalog.AttachFileAsync(new AttachMediaFileRequest(
                UnitId: movieUnit.Id,
                RelativePath: file2Rel,
                SizeBytes: 4096,
                QualityName: "WEB-DL-1080p",
                VerifiedStatus: "VerifiedPass"));

            // Seed uncanonicalized legacy quality strings directly in SQLite (since AttachFileAsync canonicalizes new writes)
            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                async (conn, tx) =>
                {
                    await conn.ExecuteAsync(
                        "UPDATE media_files SET quality_name = 'MKV' WHERE id = @Id;",
                        new { attached1.Id },
                        tx);
                    await conn.ExecuteAsync(
                        "UPDATE media_files SET quality_name = 'WEB-DL-1080p' WHERE id = @Id;",
                        new { attached2.Id },
                        tx);
                });

            // 2. Create a TV work with a missing physical file on disk
            var tvTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: "Pioneer One",
                WorkKind: "tv",
                Year: 2010,
                Containers:
                [
                    new MetadataContainerSeed("season", 1, "Season 1", "2010-06-16",
                    [
                        new MetadataUnitSeed("episode", 1, 1, "Earthbound", "2010-06-16"),
                    ]),
                ]));
            var tvUnit = tvTree.Containers.Single().Units.Single();
            await catalog.AttachFileAsync(new AttachMediaFileRequest(
                UnitId: tvUnit.Id,
                RelativePath: "Pioneer One (2010)/Season 01/Pioneer.One.S01E01.1080p.WEBDL.mkv",
                SizeBytes: 2048,
                QualityName: "WEBDL-1080p",
                VerifiedStatus: "VerifiedPass"));

            // 3. Seed a legacy mismatched work kind ('ebooks' work inside 'movies' library)
            var mismatchedWorkTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: booksLib.Id,
                Title: "Wind and Truth",
                WorkKind: "ebooks",
                Year: 2024));
            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                (conn, tx) => conn.ExecuteAsync(
                    "UPDATE media_works SET library_id = @MoviesLibId WHERE id = @WorkId;",
                    new { MoviesLibId = moviesLib.Id, WorkId = mismatchedWorkTree.Work.Id },
                    tx));

            using var client = await CreateAuthenticatedAdminClientAsync(factory);

            // Inspect GET /api/v1/system/health/checks
            var checks = await client.GetFromJsonAsync<List<SystemHealthCheckItem>>("/api/v1/system/health/checks");
            Assert.NotNull(checks);

            var dupCheck = Assert.Single(checks!, c => c.ActionUrl == "/library?health=duplicates");
            Assert.Equal("Library", dupCheck.Source);
            Assert.Equal("warning", dupCheck.Severity);
            Assert.Equal("deduplicate_unit_files", dupCheck.ResolveAction);
            Assert.Contains("Dune Part Two", dupCheck.Message);

            var qualityCheck = Assert.Single(checks!, c => c.ActionUrl == "/library?health=non-canonical-quality");
            Assert.Equal("Library", qualityCheck.Source);
            Assert.Equal("warning", qualityCheck.Severity);
            Assert.Equal("canonicalize_qualities", qualityCheck.ResolveAction);
            Assert.Contains("MKV", qualityCheck.Message);
            Assert.Contains("WEB-DL-1080p", qualityCheck.Message);

            var kindCheck = Assert.Single(checks!, c => c.ActionUrl == "/library?health=wrong-kind");
            Assert.Equal("Library", kindCheck.Source);
            Assert.Equal("warning", kindCheck.Severity);
            Assert.Equal("reclassify_mismatched_works", kindCheck.ResolveAction);
            Assert.Contains("Wind and Truth", kindCheck.Message);

            var missingCheck = Assert.Single(checks!, c => c.ActionUrl == "/library?health=missing-files");
            Assert.Equal("Library", missingCheck.Source);
            Assert.Equal("warning", missingCheck.Severity);
            Assert.Equal("prune_missing_files", missingCheck.ResolveAction);
            Assert.Contains("Pioneer One", missingCheck.Message);

            // Resolve 1: canonicalize_qualities -> normalizes "MKV" (inferring Remux-2160p from filename) and "WEB-DL-1080p" (-> "WEBDL-1080p")
            var canonResp = await client.PostAsync("/api/v1/system/health/resolve/canonicalize_qualities", null);
            Assert.Equal(HttpStatusCode.OK, canonResp.StatusCode);
            var canonReport = await canonResp.Content.ReadFromJsonAsync<HealthIssueResolutionReportDto>();
            Assert.NotNull(canonReport);
            Assert.Equal(2, canonReport!.UpdatedCount);

            var updatedMovieTree = await catalog.GetWorkTreeAsync(movieTree.Work.Id);
            var qualities = updatedMovieTree!.Containers.Single().Units.Single().Files.Select(f => f.QualityName).OrderBy(q => q).ToList();
            Assert.Equal(["Remux-2160p", "WEBDL-1080p"], qualities);

            // Resolve 2: prune_missing_files -> removes the missing Pioneer One file row, keeps Dune Part Two's existing files
            var pruneResp = await client.PostAsync("/api/v1/system/health/resolve/prune_missing_files", null);
            Assert.Equal(HttpStatusCode.OK, pruneResp.StatusCode);
            var pruneReport = await pruneResp.Content.ReadFromJsonAsync<HealthIssueResolutionReportDto>();
            Assert.NotNull(pruneReport);
            Assert.Equal(1, pruneReport!.UpdatedCount);

            var updatedTvTree = await catalog.GetWorkTreeAsync(tvTree.Work.Id);
            Assert.Empty(updatedTvTree!.Containers.Single().Units.Single().Files);

            // Resolve 3: deduplicate_unit_files -> keeps the larger 8192-byte Remux-2160p file on Dune Part Two and unlinks the extra row
            var dedupResp = await client.PostAsync("/api/v1/system/health/resolve/deduplicate_unit_files", null);
            Assert.Equal(HttpStatusCode.OK, dedupResp.StatusCode);
            var dedupReport = await dedupResp.Content.ReadFromJsonAsync<HealthIssueResolutionReportDto>();
            Assert.NotNull(dedupReport);
            Assert.Equal(1, dedupReport!.UpdatedCount);

            var dedupedMovieTree = await catalog.GetWorkTreeAsync(movieTree.Work.Id);
            var keptFile = Assert.Single(dedupedMovieTree!.Containers.Single().Units.Single().Files);
            Assert.Equal(8192, keptFile.SizeBytes);
            Assert.Equal("Remux-2160p", keptFile.QualityName);
            // Both physical files remain untouched on disk
            Assert.True(File.Exists(Path.Combine(moviesRoot, file1Rel)));
            Assert.True(File.Exists(Path.Combine(moviesRoot, file2Rel)));

            // Resolve 4: reclassify_mismatched_works -> moves 'Wind and Truth' (ebooks) into the Books library
            var reclassResp = await client.PostAsync("/api/v1/system/health/resolve/reclassify_mismatched_works", null);
            Assert.Equal(HttpStatusCode.OK, reclassResp.StatusCode);
            var reclassReport = await reclassResp.Content.ReadFromJsonAsync<HealthIssueResolutionReportDto>();
            Assert.NotNull(reclassReport);
            Assert.Equal(1, reclassReport!.UpdatedCount);

            var movedBook = await catalog.GetWorkTreeAsync(mismatchedWorkTree.Work.Id);
            Assert.Equal(booksLib.Id, movedBook!.Work.LibraryId);

            // Re-run health checks -> all 4 Library warnings are now gone!
            var checksAfter = await client.GetFromJsonAsync<List<SystemHealthCheckItem>>("/api/v1/system/health/checks");
            Assert.DoesNotContain(checksAfter!, c => c.Source == "Library");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task RetentionApi_And_RetentionAndVacuumScheduledTask_PruneAgedRows_CleanResolvedQuarantineSnippets_AndVacuum()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateFactory(tempDir);
            using var scope = factory.Services.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<ISqliteDbFactory>();
            using var client = await CreateAuthenticatedAdminClientAsync(factory);

            // 1. Verify default GET /api/v1/system/retention
            var defaultPolicy = await client.GetFromJsonAsync<SystemRetentionPolicyDto>("/api/v1/system/retention");
            Assert.NotNull(defaultPolicy);
            Assert.Equal(90, defaultPolicy!.VerificationReportsRetentionDays);
            Assert.Equal(365, defaultPolicy.MediaHistoryRetentionDays);
            Assert.Equal(90, defaultPolicy.AuditAndJobLogsRetentionDays);
            Assert.Equal(30, defaultPolicy.ReviewedQuarantineSnippetsRetentionDays);
            Assert.Equal(100, defaultPolicy.VacuumThresholdDeletedRows);

            // 2. Update retention policy via PUT /api/v1/system/retention (low VacuumThresholdDeletedRows so VACUUM triggers)
            var putResp = await client.PutAsJsonAsync("/api/v1/system/retention", new UpdateSystemRetentionPolicyRequest(
                VerificationReportsRetentionDays: 30,
                MediaHistoryRetentionDays: 60,
                AuditAndJobLogsRetentionDays: 14,
                ReviewedQuarantineSnippetsRetentionDays: 7,
                VacuumThresholdDeletedRows: 2));
            Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
            var updatedPolicy = await putResp.Content.ReadFromJsonAsync<SystemRetentionPolicyDto>();
            Assert.NotNull(updatedPolicy);
            Assert.Equal(30, updatedPolicy!.VerificationReportsRetentionDays);
            Assert.Equal(60, updatedPolicy.MediaHistoryRetentionDays);
            Assert.Equal(14, updatedPolicy.AuditAndJobLogsRetentionDays);
            Assert.Equal(7, updatedPolicy.ReviewedQuarantineSnippetsRetentionDays);
            Assert.Equal(2, updatedPolicy.VacuumThresholdDeletedRows);

            // 3. Seed aged and fresh rows across Core and Telemetry databases + quarantine .opus snippet folders on disk
            var now = DateTimeOffset.UtcNow;
            var oldIso = now.AddDays(-120).ToString("O");
            var recentIso = now.AddDays(-1).ToString("O");

            var quarantineRoot = Path.Combine(tempDir, "config", "quarantine");
            var resolvedSnippetDir = Path.Combine(quarantineRoot, "job-101");
            var pendingSnippetDir = Path.Combine(quarantineRoot, "job-102");
            var resolvedSnippetFile = Path.Combine(resolvedSnippetDir, "window-0.opus");
            var pendingSnippetFile = Path.Combine(pendingSnippetDir, "window-0.opus");
            Directory.CreateDirectory(resolvedSnippetDir);
            Directory.CreateDirectory(pendingSnippetDir);
            await File.WriteAllBytesAsync(resolvedSnippetFile, new byte[2048]);
            await File.WriteAllBytesAsync(pendingSnippetFile, new byte[2048]);

            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                async (conn, tx) =>
                {
                    // Aged verification job (unreferenced -> should be deleted)
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO verification_jobs (
                            id, staged_file_path, media_vertical, verdict, overall_confidence,
                            header_auto_patched, report_json, duration_ms, created_at
                        ) VALUES (
                            100, '/tmp/old.mkv', 'movies', 'VerifiedPass', 0.99, 0, @BigJson, 120, @OldIso
                        );
                        """,
                        new { BigJson = new string('x', 32_768), OldIso = oldIso },
                        tx);

                    // Aged verification job for resolved quarantine import (should be deleted after resolved quarantine row is pruned)
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO verification_jobs (
                            id, staged_file_path, media_vertical, verdict, overall_confidence,
                            header_auto_patched, report_json, duration_ms, created_at
                        ) VALUES (
                            101, '/tmp/resolved.mkv', 'movies', 'QuarantineReview', 0.55, 0, '{}', 120, @OldIso
                        );
                        """,
                        new { OldIso = oldIso },
                        tx);

                    // Aged verification job still referenced by a PENDING quarantine import (MUST NOT be deleted!)
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO verification_jobs (
                            id, staged_file_path, media_vertical, verdict, overall_confidence,
                            header_auto_patched, report_json, duration_ms, created_at
                        ) VALUES (
                            102, '/tmp/pending.mkv', 'movies', 'QuarantineReview', 0.52, 0, '{}', 120, @OldIso
                        );
                        """,
                        new { OldIso = oldIso },
                        tx);

                    // Fresh verification job (MUST NOT be deleted!)
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO verification_jobs (
                            id, staged_file_path, media_vertical, verdict, overall_confidence,
                            header_auto_patched, report_json, duration_ms, created_at
                        ) VALUES (
                            103, '/tmp/fresh.mkv', 'movies', 'VerifiedPass', 0.99, 0, '{}', 100, @RecentIso
                        );
                        """,
                        new { RecentIso = recentIso },
                        tx);

                    // Resolved quarantine import older than 7 days -> snippets & row should be deleted
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO quarantined_imports (
                            id, verification_job_id, release_title, staged_file_path,
                            confidence, reason, snippet_paths_json, status, reviewed_at, created_at
                        ) VALUES (
                            201, 101, 'Old.Resolved.Release', '/tmp/resolved.mkv',
                            0.55, 'Low confidence', @SnippetsJson, 'approved', @OldIso, @OldIso
                        );
                        """,
                        new
                        {
                            SnippetsJson = JsonSerializer.Serialize(new[] { resolvedSnippetFile }),
                            OldIso = oldIso,
                        },
                        tx);

                    // Pending quarantine import -> snippets & row MUST be preserved
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO quarantined_imports (
                            id, verification_job_id, release_title, staged_file_path,
                            confidence, reason, snippet_paths_json, status, created_at
                        ) VALUES (
                            202, 102, 'Active.Pending.Release', '/tmp/pending.mkv',
                            0.52, 'Low confidence', @SnippetsJson, 'quarantined', @OldIso
                        );
                        """,
                        new
                        {
                            SnippetsJson = JsonSerializer.Serialize(new[] { pendingSnippetFile }),
                            OldIso = oldIso,
                        },
                        tx);

                    // Aged + fresh media_history rows
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO media_history (occurred_at, event_type, media_vertical, source_title)
                        VALUES (@OldIso, 'Grabbed', 'movies', 'Old.Movie.2020'),
                               (@RecentIso, 'Grabbed', 'movies', 'Fresh.Movie.2026');
                        """,
                        new { OldIso = oldIso, RecentIso = recentIso },
                        tx);
                });

            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Telemetry,
                async (conn, tx) =>
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO audit_events (occurred_at, action, resource_type, details_json)
                        VALUES (@OldIso, 'old.audit.action', 'test', '{}'),
                               (@RecentIso, 'fresh.audit.action', 'test', '{}');

                        INSERT INTO job_logs (occurred_at, subsystem, level, message, metadata_json)
                        VALUES (@OldIso, 'scheduler', 'INFO', 'Old log entry', '{}'),
                               (@RecentIso, 'scheduler', 'INFO', 'Fresh log entry', '{}');
                        """,
                        new { OldIso = oldIso, RecentIso = recentIso },
                        tx);
                });

            // 4. Trigger POST /api/v1/system/retention/run
            var runResp = await client.PostAsync("/api/v1/system/retention/run", null);
            Assert.Equal(HttpStatusCode.OK, runResp.StatusCode);
            var runReport = await runResp.Content.ReadFromJsonAsync<SystemRetentionRunReportDto>();
            Assert.NotNull(runReport);
            Assert.Equal(2, runReport!.DeletedVerificationJobs); // jobs 100 and 101 deleted; 102 (pending quarantine) and 103 (fresh) kept
            Assert.Equal(1, runReport.DeletedHistoryRows);
            Assert.Equal(1, runReport.DeletedAuditEvents);
            Assert.Equal(1, runReport.DeletedJobLogs);
            Assert.Equal(1, runReport.DeletedQuarantineRows);
            Assert.Equal(1, runReport.DeletedSnippetDirs);
            Assert.True(runReport.VacuumExecuted);
            Assert.True(runReport.BytesReclaimed >= 0);

            // Resolved snippet folder was deleted, pending snippet folder still exists
            Assert.False(Directory.Exists(resolvedSnippetDir));
            Assert.True(Directory.Exists(pendingSnippetDir));
            Assert.True(File.Exists(pendingSnippetFile));

            await using (var coreConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core))
            {
                var remainingJobIds = (await coreConn.QueryAsync<long>("SELECT id FROM verification_jobs ORDER BY id;")).ToList();
                Assert.Equal([102L, 103L], remainingJobIds);

                var remainingQuarantineIds = (await coreConn.QueryAsync<long>("SELECT id FROM quarantined_imports ORDER BY id;")).ToList();
                Assert.Equal([202L], remainingQuarantineIds);

                var remainingHistory = (await coreConn.QueryAsync<string>("SELECT source_title FROM media_history;")).ToList();
                Assert.Equal(["Fresh.Movie.2026"], remainingHistory);
            }

            // 5. Verify scheduled task "RetentionAndVacuum" is registered and runs via POST /api/v1/system/tasks/RetentionAndVacuum/run
            var tasks = await client.GetFromJsonAsync<List<ScheduledTaskSummary>>("/api/v1/system/tasks");
            Assert.NotNull(tasks);
            Assert.Contains(tasks!, t => t.TaskKey == ScheduledTaskKeys.RetentionAndVacuum && t.IntervalSeconds == 86400);

            var taskRunResp = await client.PostAsync($"/api/v1/system/tasks/{ScheduledTaskKeys.RetentionAndVacuum}/run", null);
            Assert.Equal(HttpStatusCode.OK, taskRunResp.StatusCode);
            var taskSummary = await taskRunResp.Content.ReadFromJsonAsync<ScheduledTaskSummary>();
            Assert.NotNull(taskSummary);
            Assert.Equal("completed", taskSummary!.LastStatus);
            Assert.Contains("Pruned", taskSummary.LastMessage);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task RetentionCleanup_And_SnippetEndpoint_EnforceQuarantinePathConfinement()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateFactory(tempDir);
            using var scope = factory.Services.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<ISqliteDbFactory>();
            var taskManager = scope.ServiceProvider.GetRequiredService<ITaskManagerService>();
            using var client = await CreateAuthenticatedAdminClientAsync(factory);

            var configDir = Path.Combine(tempDir, "config");
            var quarantineRoot = Path.Combine(configDir, "quarantine");
            var siblingPrefixDir = Path.Combine(configDir, "quarantine-evil");
            Directory.CreateDirectory(quarantineRoot);
            Directory.CreateDirectory(siblingPrefixDir);

            // Files outside /config/quarantine (sibling prefix dir and parent traversal)
            var outsideSiblingOpus = Path.Combine(siblingPrefixDir, "outside.opus");
            var outsideParentOpus = Path.Combine(quarantineRoot, "..", "outside-parent.opus");
            var nonOpusInsideQuarantine = Path.Combine(quarantineRoot, "job-301", "notes.txt");
            Directory.CreateDirectory(Path.Combine(quarantineRoot, "job-301"));

            await File.WriteAllBytesAsync(outsideSiblingOpus, new byte[512]);
            await File.WriteAllBytesAsync(Path.GetFullPath(outsideParentOpus), new byte[512]);
            await File.WriteAllBytesAsync(nonOpusInsideQuarantine, new byte[512]);

            var oldIso = DateTimeOffset.UtcNow.AddDays(-90).ToString("O");
            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                async (conn, tx) =>
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO verification_jobs (
                            id, staged_file_path, media_vertical, verdict, overall_confidence,
                            header_auto_patched, report_json, duration_ms, created_at
                        ) VALUES (
                            301, '/tmp/traversal.mkv', 'movies', 'QuarantineReview', 0.50, 0, '{}', 100, @OldIso
                        );
                        """,
                        new { OldIso = oldIso },
                        tx);

                    await conn.ExecuteAsync(
                        """
                        INSERT INTO quarantined_imports (
                            id, verification_job_id, release_title, staged_file_path,
                            confidence, reason, snippet_paths_json, status, reviewed_at, created_at
                        ) VALUES (
                            401, 301, 'Traversal.Test', '/tmp/traversal.mkv',
                            0.50, 'Traversal check', @SnippetsJson, 'approved', @OldIso, @OldIso
                        );
                        """,
                        new
                        {
                            SnippetsJson = JsonSerializer.Serialize(new[]
                            {
                                outsideSiblingOpus,
                                outsideParentOpus,
                                nonOpusInsideQuarantine,
                            }),
                            OldIso = oldIso,
                        },
                        tx);
                });

            // Streaming an out-of-root snippet via GET /api/v1/quarantine/{id}/snippets/{index} returns 404 NotFound
            var streamSiblingResp = await client.GetAsync("/api/v1/quarantine/401/snippets/0");
            Assert.Equal(HttpStatusCode.NotFound, streamSiblingResp.StatusCode);

            var streamParentResp = await client.GetAsync("/api/v1/quarantine/401/snippets/1");
            Assert.Equal(HttpStatusCode.NotFound, streamParentResp.StatusCode);

            var streamNonOpusResp = await client.GetAsync("/api/v1/quarantine/401/snippets/2");
            Assert.Equal(HttpStatusCode.NotFound, streamNonOpusResp.StatusCode);

            // Running retention cleanup deletes the expired quarantine row & inside job-301 dir,
            // but NEVER deletes files outside /config/quarantine/!
            var report = await taskManager.RunRetentionCleanupAsync(forceVacuum: false);
            Assert.Equal(1, report.DeletedQuarantineRows);
            Assert.True(File.Exists(outsideSiblingOpus));
            Assert.True(File.Exists(Path.GetFullPath(outsideParentOpus)));
            Assert.True(Directory.Exists(siblingPrefixDir));
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
                    ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                    ["Managerr:MasterKey"] = "wave3-health-retention-master-key-32b!",
                    ["Managerr:SchedulerEnabled"] = "false",
                    ["Managerr:TestbedBootstrap:Enabled"] = "true",
                    ["Managerr:TestbedBootstrap:Username"] = "testadmin",
                    ["Managerr:TestbedBootstrap:Password"] = "Wave3AdminPass!2026",
                });
            });
        });

    private static async Task<HttpClient> CreateAuthenticatedAdminClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/breakglass/login", new
        {
            usernameOrEmail = "testadmin",
            password = "Wave3AdminPass!2026",
        });
        loginResp.EnsureSuccessStatusCode();

        var loginJson = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
        var csrfToken = loginJson.GetProperty("csrfToken").GetString();
        if (loginResp.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", setCookies.Select(c => c.Split(';')[0])));
        }

        client.DefaultRequestHeaders.Add("X-Managerr-Csrf", csrfToken);
        return client;
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"managerr-wave3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
    }
}
