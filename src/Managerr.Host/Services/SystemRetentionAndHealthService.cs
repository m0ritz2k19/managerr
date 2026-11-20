using System.Text.Json;
using Dapper;
using Managerr.Catalog;
using Managerr.Common.Media;
using Managerr.Common.Paths;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Downloaders.Parser;
using Microsoft.Data.Sqlite;

namespace Managerr.Host.Services;

public static class HealthResolutionActions
{
    public const string CanonicalizeQualities = "canonicalize_qualities";
    public const string PruneMissingFiles = "prune_missing_files";
    public const string DeduplicateUnitFiles = "deduplicate_unit_files";
    public const string ReclassifyMismatchedWorks = "reclassify_mismatched_works";
}

public sealed record HealthIssueResolutionReportDto(
    string ActionKey,
    int UpdatedCount,
    string Message,
    IReadOnlyList<string>? Details = null);

public sealed record SystemRetentionPolicyDto(
    int VerificationReportsRetentionDays = 90,
    int MediaHistoryRetentionDays = 365,
    int AuditAndJobLogsRetentionDays = 90,
    int ReviewedQuarantineSnippetsRetentionDays = 30,
    int VacuumThresholdDeletedRows = 100);

public sealed record UpdateSystemRetentionPolicyRequest(
    int? VerificationReportsRetentionDays = null,
    int? MediaHistoryRetentionDays = null,
    int? AuditAndJobLogsRetentionDays = null,
    int? ReviewedQuarantineSnippetsRetentionDays = null,
    int? VacuumThresholdDeletedRows = null);

public sealed record RunSystemRetentionRequest(
    bool? ForceVacuum = null);

public sealed record SystemRetentionRunReportDto(
    int DeletedVerificationJobs,
    int DeletedHistoryRows,
    int DeletedAuditEvents,
    int DeletedJobLogs,
    int DeletedQuarantineRows,
    int DeletedSnippetDirs,
    bool VacuumExecuted,
    long BytesReclaimed);

public sealed class SystemRetentionAndHealthService(
    ISqliteDbFactory dbFactory,
    IAppSettingsService? appSettings = null,
    IAppPaths? appPaths = null,
    IReleaseParserRouter? parserRouter = null,
    ILogger? logger = null)
{
    public const string RetentionPolicySettingKey = "system.retention_policy";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly IReleaseParserRouter _parser = parserRouter ?? new ReleaseParserRouter();

    public async Task<IReadOnlyList<SystemHealthCheckItem>> EvaluateLibraryHealthChecksAsync(CancellationToken ct = default)
    {
        var checks = new List<SystemHealthCheckItem>();

        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);

        // 1. Duplicate files per unit (Library / DuplicateUnitFiles)
        var duplicates = (await conn.QueryAsync<string>(
            """
            SELECT w.title || COALESCE(' - ' || u.title, '') || ' (unit ' || f.unit_id || ')'
            FROM media_files f
            JOIN media_works w ON w.id = f.work_id
            LEFT JOIN media_units u ON u.id = f.unit_id
            GROUP BY f.unit_id, COALESCE(f.edition_id, 0)
            HAVING COUNT(*) > 1
            ORDER BY w.title;
            """)).ToList();

        if (duplicates.Count > 0)
        {
            checks.Add(new SystemHealthCheckItem(
                Source: "Library",
                Severity: "warning",
                Message: $"{duplicates.Count} episode(s)/item(s) have more than one media file ({string.Join(", ", duplicates.Take(3))}).",
                WikiOrActionHint: "Keep one file per item: delete the extra file or re-import; extra disk files appear under Unmapped Files.",
                ActionUrl: "/library?health=duplicates",
                ResolveAction: HealthResolutionActions.DeduplicateUnitFiles));
        }

        // 2. Non-canonical or unknown quality strings (Library / NonCanonicalQuality)
        var fileRows = (await conn.QueryAsync<MediaFileHealthRow>(
            """
            SELECT f.id AS Id,
                   f.work_id AS WorkId,
                   f.unit_id AS UnitId,
                   f.edition_id AS EditionId,
                   f.relative_path AS RelativePath,
                   f.size_bytes AS SizeBytes,
                   f.quality_name AS QualityName,
                   f.source_quality_name AS SourceQualityName,
                   f.resolution AS Resolution,
                   f.verified_status AS VerifiedStatus,
                   w.title AS WorkTitle,
                   w.work_kind AS WorkKind,
                   l.root_folder_path AS RootFolderPath,
                   l.name AS LibraryName
            FROM media_files f
            JOIN media_works w ON w.id = f.work_id
            JOIN libraries l ON l.id = w.library_id
            ORDER BY f.id ASC;
            """)).ToList();

        var nonCanonicalFiles = fileRows
            .Where(IsNonCanonicalOrRepairableQuality)
            .ToList();

        if (nonCanonicalFiles.Count > 0)
        {
            var samples = nonCanonicalFiles
                .Select(f => $"{f.WorkTitle} ('{f.QualityName}')")
                .Distinct(StringComparer.Ordinal)
                .Take(3);
            checks.Add(new SystemHealthCheckItem(
                Source: "Library",
                Severity: "warning",
                Message: $"{nonCanonicalFiles.Count} media file(s) have non-canonical or unknown quality labels ({string.Join(", ", samples)}).",
                WikiOrActionHint: "Normalize quality labels so profile cutoffs and upgrade comparisons match canonically.",
                ActionUrl: "/library?health=non-canonical-quality",
                ResolveAction: HealthResolutionActions.CanonicalizeQualities));
        }

        // 3. Work kind vs Library vertical mismatch (Library / MismatchedLibraryKind)
        var workRows = (await conn.QueryAsync<WorkLibraryHealthRow>(
            """
            SELECT w.id AS WorkId,
                   w.title AS Title,
                   w.work_kind AS WorkKind,
                   l.id AS LibraryId,
                   l.name AS LibraryName,
                   l.media_type AS LibraryMediaType
            FROM media_works w
            JOIN libraries l ON l.id = w.library_id
            ORDER BY w.title ASC;
            """)).ToList();

        var mismatchedWorks = workRows
            .Where(w => !IsWorkKindCompatibleWithLibrary(w.LibraryMediaType, w.WorkKind))
            .ToList();

        if (mismatchedWorks.Count > 0)
        {
            var samples = mismatchedWorks
                .Take(3)
                .Select(w => $"{w.Title} ({w.WorkKind} in {w.LibraryName})");
            checks.Add(new SystemHealthCheckItem(
                Source: "Library",
                Severity: "warning",
                Message: $"{mismatchedWorks.Count} work(s) have a work kind that does not match their library vertical ({string.Join(", ", samples)}).",
                WikiOrActionHint: "Move or reclassify works into a library with a matching media vertical.",
                ActionUrl: "/library?health=wrong-kind",
                ResolveAction: HealthResolutionActions.ReclassifyMismatchedWorks));
        }

        // 4. Missing physical files on disk (Library / MissingDiskFiles)
        var missingFiles = new List<MediaFileHealthRow>();
        foreach (var row in fileRows)
        {
            if (string.IsNullOrWhiteSpace(row.RootFolderPath) || !Directory.Exists(row.RootFolderPath))
            {
                continue;
            }

            var fullPath = ResolveFullFilePath(row.RootFolderPath, row.RelativePath);
            if (!File.Exists(fullPath))
            {
                missingFiles.Add(row);
            }
        }

        if (missingFiles.Count > 0)
        {
            var samples = missingFiles
                .Take(3)
                .Select(f => $"{f.WorkTitle} ({Path.GetFileName(f.RelativePath)})");
            checks.Add(new SystemHealthCheckItem(
                Source: "Library",
                Severity: "warning",
                Message: $"{missingFiles.Count} registered media file(s) are missing from disk ({string.Join(", ", samples)}).",
                WikiOrActionHint: "Prune stale database file records or restore the missing files on disk.",
                ActionUrl: "/library?health=missing-files",
                ResolveAction: HealthResolutionActions.PruneMissingFiles));
        }

        return checks;
    }

    public async Task<HealthIssueResolutionReportDto> ResolveHealthIssueAsync(string actionKey, CancellationToken ct = default)
    {
        var normalized = actionKey.Trim().ToLowerInvariant();
        return normalized switch
        {
            HealthResolutionActions.CanonicalizeQualities => await CanonicalizeQualitiesAsync(ct),
            HealthResolutionActions.PruneMissingFiles => await PruneMissingFilesAsync(ct),
            HealthResolutionActions.DeduplicateUnitFiles => await DeduplicateUnitFilesAsync(ct),
            HealthResolutionActions.ReclassifyMismatchedWorks => await ReclassifyMismatchedWorksAsync(ct),
            _ => throw new KeyNotFoundException($"Unknown health resolution action '{actionKey}'."),
        };
    }

    public async Task<SystemRetentionPolicyDto> GetRetentionPolicyAsync(CancellationToken ct = default)
    {
        string? raw = null;
        if (appSettings is not null)
        {
            raw = await appSettings.GetAsync(RetentionPolicySettingKey, ct);
        }
        else
        {
            await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
            raw = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT value FROM app_settings WHERE key = @Key;",
                new { Key = RetentionPolicySettingKey });
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new SystemRetentionPolicyDto();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<SystemRetentionPolicyDto>(raw, JsonOpts);
            return SanitizePolicy(parsed ?? new SystemRetentionPolicyDto());
        }
        catch
        {
            return new SystemRetentionPolicyDto();
        }
    }

    public async Task<SystemRetentionPolicyDto> UpdateRetentionPolicyAsync(
        UpdateSystemRetentionPolicyRequest request,
        CancellationToken ct = default)
    {
        var current = await GetRetentionPolicyAsync(ct);
        var updated = SanitizePolicy(new SystemRetentionPolicyDto(
            VerificationReportsRetentionDays: request.VerificationReportsRetentionDays ?? current.VerificationReportsRetentionDays,
            MediaHistoryRetentionDays: request.MediaHistoryRetentionDays ?? current.MediaHistoryRetentionDays,
            AuditAndJobLogsRetentionDays: request.AuditAndJobLogsRetentionDays ?? current.AuditAndJobLogsRetentionDays,
            ReviewedQuarantineSnippetsRetentionDays: request.ReviewedQuarantineSnippetsRetentionDays ?? current.ReviewedQuarantineSnippetsRetentionDays,
            VacuumThresholdDeletedRows: request.VacuumThresholdDeletedRows ?? current.VacuumThresholdDeletedRows));

        var json = JsonSerializer.Serialize(updated, JsonOpts);
        if (appSettings is not null)
        {
            await appSettings.SetAsync(RetentionPolicySettingKey, json, isSecret: false, ct);
        }
        else
        {
            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                (conn, tx) => conn.ExecuteAsync(
                    """
                    INSERT INTO app_settings (key, value, is_secret, updated_at)
                    VALUES (@Key, @Value, 0, @UpdatedAt)
                    ON CONFLICT(key) DO UPDATE SET
                        value = excluded.value,
                        is_secret = 0,
                        updated_at = excluded.updated_at;
                    """,
                    new
                    {
                        Key = RetentionPolicySettingKey,
                        Value = json,
                        UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    },
                    tx),
                ct);
        }

        return updated;
    }

    public async Task<SystemRetentionRunReportDto> ExecuteRetentionCleanupAsync(
        bool forceVacuum = false,
        CancellationToken ct = default)
    {
        var policy = await GetRetentionPolicyAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var verificationCutoffIso = ComputeCutoffIso(now, policy.VerificationReportsRetentionDays);
        var historyCutoffIso = ComputeCutoffIso(now, policy.MediaHistoryRetentionDays);
        var auditCutoffIso = ComputeCutoffIso(now, policy.AuditAndJobLogsRetentionDays);
        var quarantineCutoffIso = ComputeCutoffIso(now, policy.ReviewedQuarantineSnippetsRetentionDays);

        var coreSizeBefore = GetDatabaseAndWalBytes(DatabaseKind.Core);
        var telemetrySizeBefore = GetDatabaseAndWalBytes(DatabaseKind.Telemetry);

        var quarantineRoot = appPaths is not null
            ? Path.Combine(appPaths.ConfigDirectory, "quarantine")
            : Path.Combine(Path.GetDirectoryName(dbFactory.GetDatabasePath(DatabaseKind.Core)) ?? ".", "quarantine");

        var deletedSnippetDirsSet = new HashSet<string>(StringComparer.Ordinal);

        // 1. Inspect resolved quarantine rows & delete their expired snippet directories + rows
        List<QuarantineRetentionRow> expiredQuarantineRows;
        await using (var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            expiredQuarantineRows = (await readConn.QueryAsync<QuarantineRetentionRow>(
                """
                SELECT id AS Id,
                       verification_job_id AS VerificationJobId,
                       snippet_paths_json AS SnippetPathsJson,
                       status AS Status,
                       reviewed_at AS ReviewedAt,
                       created_at AS CreatedAt
                FROM quarantined_imports
                WHERE status NOT IN ('quarantined', 'pending')
                  AND COALESCE(reviewed_at, created_at) <= @CutoffIso;
                """,
                new { CutoffIso = quarantineCutoffIso })).ToList();
        }

        foreach (var qRow in expiredQuarantineRows)
        {
            foreach (var snippetPath in DeserializePaths(qRow.SnippetPathsJson))
            {
                TryDeleteSnippetAndParentDir(snippetPath, quarantineRoot, deletedSnippetDirsSet);
            }

            if (Directory.Exists(quarantineRoot))
            {
                var jobDir = Path.GetFullPath(Path.Combine(quarantineRoot, $"job-{qRow.VerificationJobId}"));
                if (Directory.Exists(jobDir))
                {
                    try
                    {
                        Directory.Delete(jobDir, recursive: true);
                        deletedSnippetDirsSet.Add(jobDir);
                    }
                    catch
                    {
                        // Best-effort filesystem cleanup
                    }
                }
            }
        }

        var (deletedQuarantineRows, deletedVerificationJobs, deletedHistoryRows) = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                var delQuarantine = 0;
                if (expiredQuarantineRows.Count > 0)
                {
                    var ids = expiredQuarantineRows.Select(r => r.Id).ToArray();
                    await conn.ExecuteAsync(
                        "UPDATE quarantined_imports SET snippet_paths_json = '[]' WHERE id IN @Ids;",
                        new { Ids = ids },
                        tx);

                    delQuarantine = await conn.ExecuteAsync(
                        "DELETE FROM quarantined_imports WHERE id IN @Ids;",
                        new { Ids = ids },
                        tx);
                }

                var delVerif = await conn.ExecuteAsync(
                    """
                    DELETE FROM verification_jobs
                    WHERE created_at <= @CutoffIso
                      AND id NOT IN (
                          SELECT verification_job_id
                          FROM quarantined_imports
                          WHERE status IN ('quarantined', 'pending')
                      );
                    """,
                    new { CutoffIso = verificationCutoffIso },
                    tx);

                var delHistory = await conn.ExecuteAsync(
                    """
                    DELETE FROM media_history
                    WHERE occurred_at <= @CutoffIso;
                    """,
                    new { CutoffIso = historyCutoffIso },
                    tx);

                return (delQuarantine, delVerif, delHistory);
            },
            ct);

        // Also sweep orphaned /config/quarantine/* directories not referenced by any active/remaining row
        if (Directory.Exists(quarantineRoot))
        {
            HashSet<string> keptDirs;
            HashSet<long> activeJobIds;
            await using (var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
            {
                var remainingRows = (await readConn.QueryAsync<QuarantineRetentionRow>(
                    """
                    SELECT id AS Id,
                           verification_job_id AS VerificationJobId,
                           snippet_paths_json AS SnippetPathsJson,
                           status AS Status,
                           reviewed_at AS ReviewedAt,
                           created_at AS CreatedAt
                    FROM quarantined_imports;
                    """)).ToList();

                var rowsToKeepSnippets = policy.ReviewedQuarantineSnippetsRetentionDays <= 0
                    ? remainingRows.Where(r => r.Status is "quarantined" or "pending").ToList()
                    : remainingRows;

                keptDirs = rowsToKeepSnippets
                    .SelectMany(r => DeserializePaths(r.SnippetPathsJson))
                    .Select(p => Path.GetDirectoryName(p))
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Select(d => Path.GetFullPath(d!))
                    .ToHashSet(StringComparer.Ordinal);

                activeJobIds = rowsToKeepSnippets
                    .Select(r => r.VerificationJobId)
                    .ToHashSet();
            }

            foreach (var dir in Directory.EnumerateDirectories(quarantineRoot))
            {
                var fullDir = Path.GetFullPath(dir);
                var dirName = Path.GetFileName(fullDir);
                if (keptDirs.Contains(fullDir))
                {
                    continue;
                }

                if (dirName.StartsWith("job-", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(dirName["job-".Length..], out var dirJobId) &&
                    activeJobIds.Contains(dirJobId))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(fullDir, recursive: true);
                    deletedSnippetDirsSet.Add(fullDir);
                }
                catch
                {
                    // Best-effort filesystem cleanup
                }
            }
        }

        // 2. Prune Telemetry database (audit_events & job_logs)
        var (deletedAuditEvents, deletedJobLogs) = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Telemetry,
            async (conn, tx) =>
            {
                var delAudit = await conn.ExecuteAsync(
                    "DELETE FROM audit_events WHERE occurred_at <= @CutoffIso;",
                    new { CutoffIso = auditCutoffIso },
                    tx);

                var delLogs = await conn.ExecuteAsync(
                    "DELETE FROM job_logs WHERE occurred_at <= @CutoffIso;",
                    new { CutoffIso = auditCutoffIso },
                    tx);

                return (delAudit, delLogs);
            },
            ct);

        // 3. Conditional VACUUM + PRAGMA wal_checkpoint(TRUNCATE)
        var coreDeletedTotal = deletedVerificationJobs + deletedHistoryRows + deletedQuarantineRows;
        var telemetryDeletedTotal = deletedAuditEvents + deletedJobLogs;
        var totalDeleted = coreDeletedTotal + telemetryDeletedTotal;
        var threshold = Math.Max(1, policy.VacuumThresholdDeletedRows);

        var vacuumCore = forceVacuum || policy.VacuumThresholdDeletedRows <= 0 || coreDeletedTotal >= threshold || totalDeleted >= threshold;
        var vacuumTelemetry = forceVacuum || policy.VacuumThresholdDeletedRows <= 0 || telemetryDeletedTotal >= threshold || totalDeleted >= threshold;
        var vacuumExecuted = false;

        if (vacuumCore)
        {
            await VacuumAndTruncateWalAsync(DatabaseKind.Core, ct);
            vacuumExecuted = true;
        }

        if (vacuumTelemetry)
        {
            await VacuumAndTruncateWalAsync(DatabaseKind.Telemetry, ct);
            vacuumExecuted = true;
        }

        var coreSizeAfter = GetDatabaseAndWalBytes(DatabaseKind.Core);
        var telemetrySizeAfter = GetDatabaseAndWalBytes(DatabaseKind.Telemetry);
        var bytesReclaimed = Math.Max(0L, coreSizeBefore - coreSizeAfter) + Math.Max(0L, telemetrySizeBefore - telemetrySizeAfter);

        return new SystemRetentionRunReportDto(
            DeletedVerificationJobs: deletedVerificationJobs,
            DeletedHistoryRows: deletedHistoryRows,
            DeletedAuditEvents: deletedAuditEvents,
            DeletedJobLogs: deletedJobLogs,
            DeletedQuarantineRows: deletedQuarantineRows,
            DeletedSnippetDirs: deletedSnippetDirsSet.Count,
            VacuumExecuted: vacuumExecuted,
            BytesReclaimed: bytesReclaimed);
    }

    private async Task<HealthIssueResolutionReportDto> CanonicalizeQualitiesAsync(CancellationToken ct)
    {
        List<MediaFileHealthRow> rows;
        Dictionary<long, string> latestHistorySourceByUnit;

        await using (var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            rows = (await conn.QueryAsync<MediaFileHealthRow>(
                """
                SELECT f.id AS Id,
                       f.work_id AS WorkId,
                       f.unit_id AS UnitId,
                       f.edition_id AS EditionId,
                       f.relative_path AS RelativePath,
                       f.size_bytes AS SizeBytes,
                       f.quality_name AS QualityName,
                       f.source_quality_name AS SourceQualityName,
                       f.resolution AS Resolution,
                       f.verified_status AS VerifiedStatus,
                       w.title AS WorkTitle,
                       w.work_kind AS WorkKind,
                       l.root_folder_path AS RootFolderPath,
                       l.name AS LibraryName
                FROM media_files f
                JOIN media_works w ON w.id = f.work_id
                JOIN libraries l ON l.id = w.library_id
                ORDER BY f.id ASC;
                """)).ToList();

            var historyRows = (await conn.QueryAsync<(long UnitId, string SourceTitle)>(
                """
                SELECT unit_id AS UnitId, source_title AS SourceTitle
                FROM media_history
                WHERE unit_id IS NOT NULL AND source_title IS NOT NULL AND source_title <> ''
                ORDER BY id DESC;
                """)).ToList();

            latestHistorySourceByUnit = new Dictionary<long, string>();
            foreach (var h in historyRows)
            {
                latestHistorySourceByUnit.TryAdd(h.UnitId, h.SourceTitle);
            }
        }

        var updates = new List<(long Id, string OldQuality, string NewQuality, string? NewSourceQuality, string WorkTitle)>();
        foreach (var row in rows)
        {
            latestHistorySourceByUnit.TryGetValue(row.UnitId, out var historySourceTitle);
            var targetQuality = ResolveCanonicalQualityForFile(row, historySourceTitle);
            var targetSourceQuality = string.IsNullOrWhiteSpace(row.SourceQualityName)
                ? row.SourceQualityName
                : QualityNames.Canonicalize(row.SourceQualityName);
            if (string.Equals(targetSourceQuality, QualityNames.Unknown, StringComparison.OrdinalIgnoreCase))
            {
                targetSourceQuality = targetQuality;
            }

            if (!string.Equals(row.QualityName, targetQuality, StringComparison.Ordinal) ||
                !string.Equals(row.SourceQualityName, targetSourceQuality, StringComparison.Ordinal))
            {
                updates.Add((row.Id, row.QualityName, targetQuality, targetSourceQuality, row.WorkTitle));
            }
        }

        if (updates.Count > 0)
        {
            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                async (conn, tx) =>
                {
                    foreach (var u in updates)
                    {
                        await conn.ExecuteAsync(
                            """
                            UPDATE media_files
                            SET quality_name = @NewQuality,
                                source_quality_name = @NewSourceQuality
                            WHERE id = @Id;
                            """,
                            new
                            {
                                u.Id,
                                u.NewQuality,
                                u.NewSourceQuality,
                            },
                            tx);
                    }
                },
                ct);
        }

        var details = updates
            .Take(20)
            .Select(u => $"{u.WorkTitle}: '{u.OldQuality}' -> '{u.NewQuality}'")
            .ToList();

        return new HealthIssueResolutionReportDto(
            ActionKey: HealthResolutionActions.CanonicalizeQualities,
            UpdatedCount: updates.Count,
            Message: $"Normalized quality labels on {updates.Count} media file(s).",
            Details: details);
    }

    private async Task<HealthIssueResolutionReportDto> PruneMissingFilesAsync(CancellationToken ct)
    {
        List<MediaFileHealthRow> rows;
        await using (var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            rows = (await conn.QueryAsync<MediaFileHealthRow>(
                """
                SELECT f.id AS Id,
                       f.work_id AS WorkId,
                       f.unit_id AS UnitId,
                       f.edition_id AS EditionId,
                       f.relative_path AS RelativePath,
                       f.size_bytes AS SizeBytes,
                       f.quality_name AS QualityName,
                       f.source_quality_name AS SourceQualityName,
                       f.resolution AS Resolution,
                       f.verified_status AS VerifiedStatus,
                       w.title AS WorkTitle,
                       w.work_kind AS WorkKind,
                       l.root_folder_path AS RootFolderPath,
                       l.name AS LibraryName
                FROM media_files f
                JOIN media_works w ON w.id = f.work_id
                JOIN libraries l ON l.id = w.library_id
                ORDER BY f.id ASC;
                """)).ToList();
        }

        var toPrune = new List<MediaFileHealthRow>();
        foreach (var row in rows)
        {
            // Guard: Only prune missing files when the library's root folder itself exists and is mounted
            if (string.IsNullOrWhiteSpace(row.RootFolderPath) || !Directory.Exists(row.RootFolderPath))
            {
                continue;
            }

            var fullPath = ResolveFullFilePath(row.RootFolderPath, row.RelativePath);
            if (!File.Exists(fullPath))
            {
                toPrune.Add(row);
            }
        }

        if (toPrune.Count > 0)
        {
            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                async (conn, tx) =>
                {
                    var ids = toPrune.Select(r => r.Id).ToArray();
                    var affectedUnits = toPrune.Select(r => r.UnitId).Distinct().ToArray();

                    await conn.ExecuteAsync(
                        "DELETE FROM media_files WHERE id IN @Ids;",
                        new { Ids = ids },
                        tx);

                    await conn.ExecuteAsync(
                        """
                        UPDATE media_units
                        SET quality_cutoff_met = 0,
                            language_cutoff_met = 0
                        WHERE id IN @UnitIds
                          AND NOT EXISTS (SELECT 1 FROM media_files mf WHERE mf.unit_id = media_units.id);
                        """,
                        new { UnitIds = affectedUnits },
                        tx);
                },
                ct);
        }

        var details = toPrune
            .Take(20)
            .Select(r => $"{r.WorkTitle}: {r.RelativePath}")
            .ToList();

        return new HealthIssueResolutionReportDto(
            ActionKey: HealthResolutionActions.PruneMissingFiles,
            UpdatedCount: toPrune.Count,
            Message: $"Pruned {toPrune.Count} missing media file record(s).",
            Details: details);
    }

    private async Task<HealthIssueResolutionReportDto> DeduplicateUnitFilesAsync(CancellationToken ct)
    {
        List<MediaFileHealthRow> rows;
        await using (var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            rows = (await conn.QueryAsync<MediaFileHealthRow>(
                """
                SELECT f.id AS Id,
                       f.work_id AS WorkId,
                       f.unit_id AS UnitId,
                       f.edition_id AS EditionId,
                       f.relative_path AS RelativePath,
                       f.size_bytes AS SizeBytes,
                       f.quality_name AS QualityName,
                       f.source_quality_name AS SourceQualityName,
                       f.resolution AS Resolution,
                       f.verified_status AS VerifiedStatus,
                       w.title AS WorkTitle,
                       w.work_kind AS WorkKind,
                       l.root_folder_path AS RootFolderPath,
                       l.name AS LibraryName
                FROM media_files f
                JOIN media_works w ON w.id = f.work_id
                JOIN libraries l ON l.id = w.library_id
                ORDER BY f.id ASC;
                """)).ToList();
        }

        var duplicateGroups = rows
            .GroupBy(r => (r.UnitId, EditionSlot: r.EditionId ?? 0L))
            .Where(g => g.Count() > 1)
            .ToList();

        var idsToDelete = new List<long>();
        var details = new List<string>();

        foreach (var group in duplicateGroups)
        {
            var ordered = group
                .OrderByDescending(r => !string.Equals(r.VerifiedStatus, "disk_scan_adopted", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(r => r.SizeBytes)
                .ThenByDescending(r => r.Id)
                .ToList();

            var winner = ordered[0];
            foreach (var extra in ordered.Skip(1))
            {
                idsToDelete.Add(extra.Id);
                if (details.Count < 20)
                {
                    details.Add($"{extra.WorkTitle} (unit {extra.UnitId}): kept '{winner.RelativePath}', unlinked '{extra.RelativePath}'");
                }
            }
        }

        if (idsToDelete.Count > 0)
        {
            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                (conn, tx) => conn.ExecuteAsync(
                    "DELETE FROM media_files WHERE id IN @Ids;",
                    new { Ids = idsToDelete.ToArray() },
                    tx),
                ct);
        }

        return new HealthIssueResolutionReportDto(
            ActionKey: HealthResolutionActions.DeduplicateUnitFiles,
            UpdatedCount: idsToDelete.Count,
            Message: $"Unlinked {idsToDelete.Count} duplicate media file record(s).",
            Details: details);
    }

    private async Task<HealthIssueResolutionReportDto> ReclassifyMismatchedWorksAsync(CancellationToken ct)
    {
        List<WorkLibraryHealthRow> works;
        List<(long Id, string Name, string MediaType)> libraries;

        await using (var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            works = (await conn.QueryAsync<WorkLibraryHealthRow>(
                """
                SELECT w.id AS WorkId,
                       w.title AS Title,
                       w.work_kind AS WorkKind,
                       l.id AS LibraryId,
                       l.name AS LibraryName,
                       l.media_type AS LibraryMediaType
                FROM media_works w
                JOIN libraries l ON l.id = w.library_id
                ORDER BY w.id ASC;
                """)).ToList();

            libraries = (await conn.QueryAsync<(long Id, string Name, string MediaType)>(
                "SELECT id AS Id, name AS Name, media_type AS MediaType FROM libraries ORDER BY id ASC;")).ToList();
        }

        var mismatched = works
            .Where(w => !IsWorkKindCompatibleWithLibrary(w.LibraryMediaType, w.WorkKind))
            .ToList();

        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        var updatedCount = 0;
        var details = new List<string>();

        if (mismatched.Count > 0)
        {
            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                async (conn, tx) =>
                {
                    foreach (var work in mismatched)
                    {
                        var compatibleLib = libraries.FirstOrDefault(l => IsWorkKindCompatibleWithLibrary(l.MediaType, work.WorkKind));
                        if (compatibleLib.Id > 0)
                        {
                            await conn.ExecuteAsync(
                                "UPDATE media_works SET library_id = @LibId, updated_at = @Now WHERE id = @WorkId;",
                                new { LibId = compatibleLib.Id, Now = nowIso, work.WorkId },
                                tx);
                            details.Add($"Moved '{work.Title}' ({work.WorkKind}) to library '{compatibleLib.Name}'");
                        }
                        else
                        {
                            await conn.ExecuteAsync(
                                "UPDATE media_works SET work_kind = @WorkKind, updated_at = @Now WHERE id = @WorkId;",
                                new { WorkKind = work.LibraryMediaType, Now = nowIso, work.WorkId },
                                tx);
                            details.Add($"Reclassified '{work.Title}' from '{work.WorkKind}' to '{work.LibraryMediaType}'");
                        }

                        updatedCount++;
                    }
                },
                ct);
        }

        return new HealthIssueResolutionReportDto(
            ActionKey: HealthResolutionActions.ReclassifyMismatchedWorks,
            UpdatedCount: updatedCount,
            Message: $"Resolved {updatedCount} mismatched work(s).",
            Details: details);
    }

    private bool IsNonCanonicalOrRepairableQuality(MediaFileHealthRow row)
    {
        var canonical = QualityNames.Canonicalize(row.QualityName);
        if (!string.Equals(canonical, row.QualityName, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(canonical, QualityNames.Unknown, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(row.VerifiedStatus, "disk_scan_adopted", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return HasParseableQualityTokens(row.RelativePath, row.SourceQualityName, row.WorkKind);
        }

        return false;
    }

    private bool HasParseableQualityTokens(string relativePath, string? sourceQualityName, string workKind)
    {
        if (!string.IsNullOrWhiteSpace(sourceQualityName) &&
            !string.Equals(QualityNames.Canonicalize(sourceQualityName), QualityNames.Unknown, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        var parsed = _parser.Parse(fileName, workKind);
        if (!string.IsNullOrWhiteSpace(parsed.Source) ||
            !string.IsNullOrWhiteSpace(parsed.Resolution) ||
            !string.IsNullOrWhiteSpace(parsed.MusicFormat) ||
            !string.IsNullOrWhiteSpace(parsed.BookFormat))
        {
            return true;
        }

        var parsedFull = _parser.Parse(relativePath.Replace('/', ' ').Replace('\\', ' '), workKind);
        return !string.IsNullOrWhiteSpace(parsedFull.Source) ||
               !string.IsNullOrWhiteSpace(parsedFull.Resolution) ||
               !string.IsNullOrWhiteSpace(parsedFull.MusicFormat) ||
               !string.IsNullOrWhiteSpace(parsedFull.BookFormat);
    }

    private string ResolveCanonicalQualityForFile(MediaFileHealthRow row, string? historySourceTitle)
    {
        var canonical = QualityNames.Canonicalize(row.QualityName);
        if (!string.Equals(canonical, QualityNames.Unknown, StringComparison.OrdinalIgnoreCase))
        {
            return canonical;
        }

        if (!string.IsNullOrWhiteSpace(row.SourceQualityName))
        {
            var fromSourceCol = QualityNames.Canonicalize(row.SourceQualityName);
            if (!string.Equals(fromSourceCol, QualityNames.Unknown, StringComparison.OrdinalIgnoreCase))
            {
                return fromSourceCol;
            }
        }

        foreach (var candidate in new[]
                 {
                     historySourceTitle,
                     Path.GetFileNameWithoutExtension(row.RelativePath),
                     row.RelativePath.Replace('/', ' ').Replace('\\', ' '),
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var parsed = _parser.Parse(candidate, row.WorkKind);
            if (!string.IsNullOrWhiteSpace(parsed.MusicFormat))
            {
                return parsed.MusicFormat;
            }

            if (!string.IsNullOrWhiteSpace(parsed.BookFormat))
            {
                return parsed.BookFormat;
            }

            if (!string.IsNullOrWhiteSpace(parsed.Source) || !string.IsNullOrWhiteSpace(parsed.Resolution))
            {
                return QualityNames.Canonicalize($"{parsed.Source ?? "WEBDL"}-{parsed.Resolution ?? row.Resolution ?? "1080p"}");
            }
        }

        var ext = Path.GetExtension(row.RelativePath).ToLowerInvariant();
        return ext switch
        {
            ".epub" => "EPUB",
            ".azw3" => "AZW3",
            ".pdf" => "PDF",
            ".m4b" => "M4B",
            ".cbz" => "CBZ",
            ".cbr" => "CBR",
            ".flac" => "FLAC",
            ".mp3" => "MP3-320",
            _ => QualityNames.Canonicalize($"WEBDL-{row.Resolution ?? "1080p"}"),
        };
    }

    private static bool IsWorkKindCompatibleWithLibrary(string libraryMediaType, string workKind)
    {
        try
        {
            return PolymorphicCatalogService.LibraryAcceptsWorkKind(libraryMediaType, workKind);
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveFullFilePath(string rootFolderPath, string relativePath) =>
        Path.IsPathRooted(relativePath)
            ? relativePath
            : Path.GetFullPath(Path.Combine(rootFolderPath, relativePath));

    private static SystemRetentionPolicyDto SanitizePolicy(SystemRetentionPolicyDto policy) =>
        new(
            VerificationReportsRetentionDays: Math.Max(0, policy.VerificationReportsRetentionDays),
            MediaHistoryRetentionDays: Math.Max(0, policy.MediaHistoryRetentionDays),
            AuditAndJobLogsRetentionDays: Math.Max(0, policy.AuditAndJobLogsRetentionDays),
            ReviewedQuarantineSnippetsRetentionDays: Math.Max(0, policy.ReviewedQuarantineSnippetsRetentionDays),
            VacuumThresholdDeletedRows: Math.Max(0, policy.VacuumThresholdDeletedRows));

    private static string ComputeCutoffIso(DateTimeOffset now, int retentionDays) =>
        retentionDays <= 0
            ? now.AddMinutes(1).ToString("O")
            : now.AddDays(-retentionDays).ToString("O");

    private static string[] DeserializePaths(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOpts) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void TryDeleteSnippetAndParentDir(
        string snippetPath,
        string quarantineRoot,
        HashSet<string> deletedSnippetDirsSet)
    {
        if (string.IsNullOrWhiteSpace(snippetPath) || string.IsNullOrWhiteSpace(quarantineRoot))
        {
            return;
        }

        try
        {
            var fullRoot = Path.GetFullPath(quarantineRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullSnippetPath = Path.GetFullPath(snippetPath);

            if (!fullSnippetPath.StartsWith(fullRoot, StringComparison.Ordinal) ||
                !fullSnippetPath.EndsWith(".opus", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (File.Exists(fullSnippetPath))
            {
                File.Delete(fullSnippetPath);
            }

            var dir = Path.GetDirectoryName(fullSnippetPath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                var fullDir = Path.GetFullPath(dir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (fullDir.StartsWith(fullRoot, StringComparison.Ordinal))
                {
                    Directory.Delete(fullDir, recursive: true);
                    deletedSnippetDirsSet.Add(fullDir);
                }
            }
        }
        catch
        {
            // Best-effort snippet cleanup
        }
    }

    private long GetDatabaseAndWalBytes(DatabaseKind kind)
    {
        var dbPath = dbFactory.GetDatabasePath(kind);
        var walPath = dbPath + "-wal";
        var size = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0L;
        var walSize = File.Exists(walPath) ? new FileInfo(walPath).Length : 0L;
        return size + walSize;
    }

    private async Task VacuumAndTruncateWalAsync(DatabaseKind kind, CancellationToken ct)
    {
        try
        {
            await using var conn = await dbFactory.OpenReadConnectionAsync(kind, ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
            await cmd.ExecuteNonQueryAsync(ct);
            SqliteConnection.ClearPool(conn);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            logger?.LogWarning(
                ex,
                "Skipped SQLite VACUUM / WAL truncate on {DatabaseKind} because another connection held a lock (SQLite error {SqliteErrorCode}).",
                kind,
                ex.SqliteErrorCode);
        }
    }

    private sealed class MediaFileHealthRow
    {
        public long Id { get; init; }
        public long WorkId { get; init; }
        public long UnitId { get; init; }
        public long? EditionId { get; init; }
        public string RelativePath { get; init; } = "";
        public long SizeBytes { get; init; }
        public string QualityName { get; init; } = "";
        public string? SourceQualityName { get; init; }
        public string? Resolution { get; init; }
        public string VerifiedStatus { get; init; } = "";
        public string WorkTitle { get; init; } = "";
        public string WorkKind { get; init; } = "";
        public string RootFolderPath { get; init; } = "";
        public string LibraryName { get; init; } = "";
    }

    private sealed class WorkLibraryHealthRow
    {
        public long WorkId { get; init; }
        public string Title { get; init; } = "";
        public string WorkKind { get; init; } = "";
        public long LibraryId { get; init; }
        public string LibraryName { get; init; } = "";
        public string LibraryMediaType { get; init; } = "";
    }

    private sealed class QuarantineRetentionRow
    {
        public long Id { get; init; }
        public long VerificationJobId { get; init; }
        public string SnippetPathsJson { get; init; } = "[]";
        public string Status { get; init; } = "";
        public string? ReviewedAt { get; init; }
        public string CreatedAt { get; init; } = "";
    }
}
