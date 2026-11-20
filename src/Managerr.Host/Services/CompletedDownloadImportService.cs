using System.Text.Json;
using Dapper;
using Managerr.Catalog;
using Managerr.Common.Net;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
using Managerr.Downloaders.Organizer;
using Managerr.Downloaders.Parser;
using Managerr.Host.Hubs;
using Managerr.Indexers;
using Managerr.Indexers.Torznab;
using Managerr.Migration;
using Managerr.Profiles;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace Managerr.Host.Services;

public static class TrackedDownloadStates
{
    public const string Downloading = "Downloading";
    public const string ImportPending = "ImportPending";
    public const string Importing = "Importing";
    public const string Imported = "Imported";
    public const string ImportBlocked = "ImportBlocked";
    public const string FailedPending = "FailedPending";
    public const string Failed = "Failed";
    public const string Ignored = "Ignored";
    public const string Removed = "Removed";
}

public sealed record TrackedDownloadDto(
    long Id,
    long DownloadClientId,
    string ClientName,
    string ExternalDownloadId,
    string Protocol,
    string Title,
    string MediaVertical,
    long? WorkId,
    string? WorkTitle,
    IReadOnlyList<long> UnitIds,
    string State,
    string TrackedStatus,
    IReadOnlyList<string> StatusMessages,
    string? OutputPath,
    long? QuarantineId,
    bool CanMoveFiles,
    string FirstSeenAt,
    string UpdatedAt);

public sealed record ManualOverrideImportRequest(
    long? WorkId = null,
    int? SeasonNumber = null,
    IReadOnlyList<int>? EpisodeNumbers = null,
    bool BypassUpgradeCheck = false,
    IReadOnlyList<string>? ConfirmedAudioLanguages = null);

public sealed record ProcessedDownloadItemOutcome(
    long ClientId,
    string ClientName,
    string ExternalDownloadId,
    string Title,
    string MediaVertical,
    string Verdict,
    string? SourceFilePath,
    string? DestinationFilePath,
    bool Hardlinked,
    long? QuarantineId,
    string Message);

public sealed record ProcessCompletedDownloadsReport(
    int EvaluatedCount,
    int ImportedCount,
    int QuarantinedCount,
    int RejectedCount,
    IReadOnlyList<ProcessedDownloadItemOutcome> Items,
    int RemovedCount = 0);

public interface ICompletedDownloadImportService
{
    Task<ProcessCompletedDownloadsReport> ProcessCompletedDownloadsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ProcessedDownloadItemOutcome>> ListRecentImportsAsync(int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<TrackedDownloadDto>> ListTrackedDownloadsAsync(int limit = 100, CancellationToken ct = default);
    Task<ProcessedDownloadItemOutcome> RetryTrackedDownloadImportAsync(long trackedId, ManualOverrideImportRequest? overrideRequest = null, CancellationToken ct = default);
    Task<TrackedDownloadDto?> IgnoreTrackedDownloadAsync(long trackedId, CancellationToken ct = default);
}

/// <summary>
/// Sonarr-adapted Completed Download Handling (CDH), TrackedDownload state machine, FailedDownloadService,
/// AggregationService, and ImportDecisionMaker specification pipeline across all Managerr media verticals.
/// </summary>
public sealed partial class CompletedDownloadImportService(
    ISqliteDbFactory dbFactory,
    IUnifiedDownloadOrchestrator downloadOrchestrator,
    IReleaseParserRouter parserRouter,
    IAtomicFileOrganizer organizer,
    ILanguageVerificationService verifier,
    IPolymorphicCatalogService catalogService,
    IAppSettingsService appSettings,
    ISafeHttpClientFactory httpFactory,
    IComposableProfileService? profileService = null,
    IUnifiedIndexerService? indexerService = null,
    IRealtimeBroadcaster? realtime = null,
    Managerr.Common.Notifications.IMediaLifecycleNotificationSink? notificationSink = null,
    IWantedAndActivityService? wantedService = null,
    Microsoft.Extensions.Configuration.IConfiguration? configuration = null,
    Microsoft.Extensions.Logging.ILogger<CompletedDownloadImportService>? logger = null,
    IServiceProvider? serviceProvider = null) : ICompletedDownloadImportService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // States that only make sense while the client still lists the item (queue or history)
    private static readonly HashSet<string> ClientBoundStates = new(StringComparer.Ordinal)
    {
        TrackedDownloadStates.Downloading,
        TrackedDownloadStates.ImportPending,
    };

    private TimeSpan VanishedGracePeriod => TimeSpan.FromSeconds(Math.Max(0,
        configuration?.GetValue("Managerr:TrackedDownloadVanishedGraceSeconds", 300) ?? 300));
    private readonly SemaphoreSlim _importGate = new(1, 1);

    private static readonly HashSet<string> PrimaryMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".ts", ".webm",
        ".flac", ".mp3", ".m4b", ".m4a", ".opus", ".ogg",
        ".epub", ".pdf", ".cbz", ".cbr",
    };

    private static readonly string[] IncompleteStagingSuffixes = [".part", ".partial", ".!qb", ".tmp", ".crdownload"];

    public async Task<ProcessCompletedDownloadsReport> ProcessCompletedDownloadsAsync(CancellationToken ct = default)
    {
        await _importGate.WaitAsync(ct);
        try
        {
            return await ProcessCompletedDownloadsCoreAsync(ct);
        }
        finally
        {
            _importGate.Release();
        }
    }

    private async Task<ProcessCompletedDownloadsReport> ProcessCompletedDownloadsCoreAsync(CancellationToken ct)
    {
        var clients = (await downloadOrchestrator.ListClientsAsync(ct)).ToDictionary(c => c.Id);
        var snapshot = await downloadOrchestrator.GetUnifiedQueueSnapshotAsync(ct);
        var queue = snapshot.Items;
        var libraries = await catalogService.ListLibrariesAsync(null, ct);
        var works = await catalogService.ListWorksAsync(null, null, null, ct);

        Dictionary<(long ClientId, string ExtId), TrackedRow> existingTracked;
        Dictionary<(long ClientId, string ExtId), string> terminalLedgerVerdicts;
        Dictionary<long, (string Status, string[] DetectedLangs)> quarantineStates;

        await using (var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            existingTracked = (await readConn.QueryAsync<TrackedRow>("SELECT * FROM tracked_downloads;"))
                .ToDictionary(r => (r.DownloadClientId, r.ExternalDownloadId));

            // Treat final Imported / Failed / FailedUnpack / Malware / VerifiedFail ledger entries as terminal; QuarantineReview can resume once approved!
            terminalLedgerVerdicts = (await readConn.QueryAsync<(long ClientId, string ExtId, string Verdict)>(
                    "SELECT download_client_id AS ClientId, external_download_id AS ExtId, verdict AS Verdict FROM processed_download_imports;"))
                .Where(x => x.Verdict is VerificationVerdicts.VerifiedPass
                    or VerificationVerdicts.VerifiedPassAutoTagged
                    or VerificationVerdicts.MusicPolicyPass
                    or "ClientFailed"
                    or "FailedUnpack"
                    or "BlockedMalware"
                    or VerificationVerdicts.VerifiedFail)
                .GroupBy(x => (x.ClientId, x.ExtId))
                .ToDictionary(g => g.Key, g => g.Last().Verdict);

            var qRows = await readConn.QueryAsync<(long Id, string Status, string DetJson)>(
                "SELECT id AS Id, status AS Status, detected_languages_json AS DetJson FROM quarantined_imports;");
            quarantineStates = qRows.ToDictionary(
                q => q.Id,
                q => (q.Status, JsonSerializer.Deserialize<string[]>(q.DetJson, JsonOpts) ?? []));
        }

        var outcomes = new List<ProcessedDownloadItemOutcome>();
        var imported = 0;
        var quarantined = 0;
        var rejected = 0;
        var evaluatedCompleted = 0;

        foreach (var item in queue)
        {
            existingTracked.TryGetValue((item.ClientId, item.ExternalId), out var prevTracked);
            if (prevTracked?.State is TrackedDownloadStates.Ignored or TrackedDownloadStates.Failed)
            {
                continue;
            }

            if (prevTracked?.State == TrackedDownloadStates.Imported ||
                terminalLedgerVerdicts.TryGetValue((item.ClientId, item.ExternalId), out var prevLedgerVerdict))
            {
                if (prevTracked is null)
                {
                    var wasFailedLedger = terminalLedgerVerdicts.TryGetValue((item.ClientId, item.ExternalId), out prevLedgerVerdict) &&
                                          prevLedgerVerdict is "ClientFailed" or "FailedUnpack" or "BlockedMalware" or VerificationVerdicts.VerifiedFail;
                    await UpsertTrackedDownloadAsync(
                        item,
                        workId: null,
                        workTitle: null,
                        unitIds: [],
                        state: wasFailedLedger ? TrackedDownloadStates.Failed : TrackedDownloadStates.Imported,
                        trackedStatus: wasFailedLedger ? "error" : "ok",
                        statusMessages: [],
                        quarantineId: null,
                        ct);
                }

                continue;
            }

            var grabHistory = await downloadOrchestrator.FindGrabHistoryByItemAsync(item.ClientId, item.ExternalId, item.InfoHash, item.Title, ct);
            var clientCfg = clients.GetValueOrDefault(item.ClientId);

            if (grabHistory is null && IsForeignClientCategoryItem(item, clientCfg))
            {
                if (prevTracked is not null &&
                    prevTracked.State != TrackedDownloadStates.Imported &&
                    prevTracked.WorkId is null)
                {
                    await dbFactory.ExecuteWriteAsync(
                        DatabaseKind.Core,
                        (conn, tx) => conn.ExecuteAsync(
                            "DELETE FROM tracked_downloads WHERE id = @Id AND work_id IS NULL;",
                            new { Id = prevTracked.Id },
                            transaction: tx),
                        ct);
                    existingTracked.Remove((item.ClientId, item.ExternalId));
                }

                continue;
            }

            // 1. FailedDownloadService handling for failed Usenet/Torrent client jobs
            if (IsFailedClientStatus(item))
            {
                var failMsg = item.ErrorMessage ?? $"Download client reported failed status '{item.Status}'.";
                if (WantedAndActivityService.IsTransientDownloaderClientFault(failMsg))
                {
                    await UpsertTrackedDownloadAsync(
                        item,
                        grabHistory?.WorkId,
                        null,
                        grabHistory?.UnitIds ?? [],
                        TrackedDownloadStates.Downloading,
                        "warning",
                        [$"Transient client fault: {failMsg}"],
                        null,
                        ct);
                    continue;
                }

                evaluatedCompleted++;
                rejected++;
                ProcessedDownloadItemOutcome failedOutcome;
                try
                {
                    failedOutcome = await HandleTerminalDownloadFailureAsync(
                        item,
                        clientCfg,
                        grabHistory,
                        works,
                        libraries,
                        failMsg,
                        ledgerVerdict: "ClientFailed",
                        forceRemoveFromClient: false,
                        explicitWorkId: null,
                        explicitWorkTitle: null,
                        explicitUnitIds: null,
                        sourceFilePath: item.OutputPath,
                        ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One broken failure path must not stop the others; the item stays FailedPending and is retried next poll
                    var errorMsg = $"Failed-download handling failed unexpectedly: {ex.GetType().Name}: {ex.Message}";
                    await UpsertTrackedDownloadAsync(
                        item,
                        grabHistory?.WorkId,
                        null,
                        grabHistory?.UnitIds ?? [],
                        TrackedDownloadStates.FailedPending,
                        "error",
                        [failMsg, errorMsg],
                        null,
                        ct);
                    failedOutcome = new ProcessedDownloadItemOutcome(
                        item.ClientId, item.ClientName, item.ExternalId, item.Title, item.MediaVertical,
                        TrackedDownloadStates.FailedPending, item.OutputPath, null, false, null, errorMsg);
                }

                outcomes.Add(failedOutcome);
                continue;
            }

            // 2. Active in-progress downloads: maintain TrackedDownload in Downloading / ImportPending state
            if (!IsCompletedStatus(item.Status))
            {
                var prelimParsed = parserRouter.Parse(item.Title, item.MediaVertical);
                var prelimLib = ResolveTargetLibrary(libraries, prelimParsed.MediaVertical);
                var prelimWork = ResolveCorrelatedOrTitleMatchedWork(works, prelimLib, prelimParsed, grabHistory, out _, item.Title, item.OutputPath, parserRouter);
                var activeState = IsUnpackingClientStatus(item.Status)
                    ? TrackedDownloadStates.ImportPending
                    : TrackedDownloadStates.Downloading;
                var activeMsgs = !string.IsNullOrWhiteSpace(item.ErrorMessage)
                    ? new[] { item.ErrorMessage! }
                    : IsUnpackingClientStatus(item.Status)
                        ? new[] { $"Client is {item.Status.ToLowerInvariant()}; awaiting completion before import." }
                        : Array.Empty<string>();

                await UpsertTrackedDownloadAsync(
                    item,
                    prelimWork?.Id ?? grabHistory?.WorkId,
                    prelimWork?.Title,
                    grabHistory?.UnitIds ?? [],
                    activeState,
                    activeMsgs.Length > 0 ? "warning" : "ok",
                    activeMsgs,
                    null,
                    ct);
                continue;
            }

            // If previously quarantined and STILL awaiting human review, skip re-probing until approved or manually retried
            ManualOverrideImportRequest? autoResumeOverride = null;
            if (prevTracked?.State == TrackedDownloadStates.ImportBlocked &&
                prevTracked.QuarantineId.HasValue &&
                quarantineStates.TryGetValue(prevTracked.QuarantineId.Value, out var qInfo))
            {
                if (string.Equals(qInfo.Status, "quarantined", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(qInfo.Status, "approved", StringComparison.OrdinalIgnoreCase))
                {
                    autoResumeOverride = new ManualOverrideImportRequest(
                        WorkId: prevTracked.WorkId,
                        ConfirmedAudioLanguages: qInfo.DetectedLangs.Length > 0 ? qInfo.DetectedLangs : ["en"],
                        BypassUpgradeCheck: true);
                }
                else if (qInfo.Status.StartsWith("rejected", StringComparison.OrdinalIgnoreCase))
                {
                    await UpsertTrackedDownloadAsync(
                        item,
                        prevTracked.WorkId,
                        prevTracked.WorkTitle,
                        JsonSerializer.Deserialize<long[]>(prevTracked.UnitIdsJson, JsonOpts) ?? [],
                        TrackedDownloadStates.Failed,
                        "error",
                        ["Quarantined release was rejected and blocklisted."],
                        prevTracked.QuarantineId,
                        ct);
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(item.OutputPath))
            {
                await UpsertTrackedDownloadAsync(
                    item,
                    grabHistory?.WorkId,
                    null,
                    grabHistory?.UnitIds ?? [],
                    TrackedDownloadStates.ImportBlocked,
                    "warning",
                    ["Download completed, but client returned an empty output storage path."],
                    null,
                    ct);
                continue;
            }

            evaluatedCompleted++;
            ProcessedDownloadItemOutcome outcome;
            try
            {
                outcome = await EvaluateAndImportSingleItemAsync(
                    item,
                    clientCfg,
                    grabHistory,
                    libraries,
                    works,
                    manualOverride: autoResumeOverride,
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Unexpected error importing completed download {ExternalId} ({Title}) from {Client}.", item.ExternalId, item.Title, item.ClientName);
                // One broken download must not stop the others; it waits for a retry or manual import
                var errorMsg = $"Import failed unexpectedly: {ex.GetType().Name}: {ex.Message}";
                await UpsertTrackedDownloadAsync(
                    item,
                    grabHistory?.WorkId,
                    null,
                    grabHistory?.UnitIds ?? [],
                    TrackedDownloadStates.ImportBlocked,
                    "error",
                    [errorMsg],
                    null,
                    ct);
                outcome = new ProcessedDownloadItemOutcome(
                    item.ClientId, item.ClientName, item.ExternalId, item.Title, item.MediaVertical,
                    "ImportBlocked", item.OutputPath, null, false, null, errorMsg);
            }

            // Refresh works list if a standalone movie work was auto-registered
            if (outcome.Verdict is VerificationVerdicts.VerifiedPass or VerificationVerdicts.VerifiedPassAutoTagged or VerificationVerdicts.MusicPolicyPass)
            {
                imported++;
                works = await catalogService.ListWorksAsync(null, null, null, ct);
            }
            else if (outcome.Verdict == VerificationVerdicts.QuarantineReview)
            {
                quarantined++;
            }
            else if (outcome.Verdict is "ClientFailed" or "BlockedMalware" or VerificationVerdicts.VerifiedFail or "ImportBlocked")
            {
                rejected++;
            }

            outcomes.Add(outcome);
        }

        var removedForeignTracked = await SweepOrphanedForeignTrackedDownloadsAsync(existingTracked, snapshot, clients, ct);

        var removed = await MarkVanishedTrackedDownloadsAsync(existingTracked.Values, snapshot, clients, ct);

        if (realtime is not null && (evaluatedCompleted > 0 || outcomes.Count > 0 || removed > 0 || removedForeignTracked > 0))
        {
            await TryBroadcastAsync(PermissionScopes.QueueRead, "TrackedDownloadRefreshed", new
            {
                evaluatedCount = evaluatedCompleted,
                importedCount = imported,
                quarantinedCount = quarantined,
                rejectedCount = rejected,
                removedCount = removed,
                timestampUtc = DateTimeOffset.UtcNow,
            }, ct);
        }

        return new ProcessCompletedDownloadsReport(
            EvaluatedCount: evaluatedCompleted,
            ImportedCount: imported,
            QuarantinedCount: quarantined,
            RejectedCount: rejected,
            Items: outcomes,
            RemovedCount: removed);
    }

    /// <summary>
    /// Sonarr-style reconciliation: a tracked download that a reachable client no longer lists in its
    /// queue or history (deleted manually, pruned from history, client reset) is marked Removed instead of
    /// staying Downloading forever. Unreachable clients never cause a state change.
    /// </summary>
    private async Task<int> MarkVanishedTrackedDownloadsAsync(
        IEnumerable<TrackedRow> trackedRows,
        UnifiedQueueSnapshot snapshot,
        IReadOnlyDictionary<long, DownloadClientSummary> clients,
        CancellationToken ct)
    {
        var present = snapshot.Items.Select(i => (i.ClientId, i.ExternalId)).ToHashSet();
        var cutoff = DateTimeOffset.UtcNow - VanishedGracePeriod;

        var vanished = trackedRows
            .Where(r => ClientBoundStates.Contains(r.State))
            .Where(r => snapshot.ReachableClientIds.Contains(r.DownloadClientId))
            .Where(r => !present.Contains((r.DownloadClientId, r.ExternalDownloadId)))
            // updated_at is refreshed on every poll that still sees the item, so it doubles as "last seen"
            .Where(r => DateTimeOffset.TryParse(r.UpdatedAt, out var lastSeen) && lastSeen <= cutoff)
            .ToList();

        if (vanished.Count == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                UPDATE tracked_downloads
                SET state = @State,
                    tracked_status = 'warning',
                    status_messages_json = @MsgsJson,
                    updated_at = @Now
                WHERE id = @Id;
                """,
                vanished.Select(r => new
                {
                    State = TrackedDownloadStates.Removed,
                    MsgsJson = JsonSerializer.Serialize(new[]
                    {
                        $"No longer present in {clients.GetValueOrDefault(r.DownloadClientId)?.Name ?? r.ClientName} queue or history (last seen {r.UpdatedAt}); marked as removed.",
                    }, JsonOpts),
                    Now = now,
                    r.Id,
                }),
                transaction: tx),
            ct);

        return vanished.Count;
    }

    private async Task<int> SweepOrphanedForeignTrackedDownloadsAsync(
        Dictionary<(long ClientId, string ExtId), TrackedRow> existingTracked,
        UnifiedQueueSnapshot snapshot,
        IReadOnlyDictionary<long, DownloadClientSummary> clients,
        CancellationToken ct)
    {
        var candidates = existingTracked.Values
            .Where(r => r.WorkId is null && r.State != TrackedDownloadStates.Imported)
            .Where(r =>
                (snapshot.ForeignItemKeys is not null && snapshot.ForeignItemKeys.Contains((r.DownloadClientId, r.ExternalDownloadId))) ||
                (r.State == TrackedDownloadStates.ImportBlocked && IsOutputPathUnderForeignCategory(r.OutputPath, clients.GetValueOrDefault(r.DownloadClientId))))
            .ToList();

        if (candidates.Count == 0)
        {
            return 0;
        }

        var toDelete = new List<TrackedRow>();
        foreach (var row in candidates)
        {
            var grabHistory = await downloadOrchestrator.FindGrabHistoryByItemAsync(
                row.DownloadClientId,
                row.ExternalDownloadId,
                row.InfoHash,
                row.Title,
                ct);
            if (grabHistory is null)
            {
                toDelete.Add(row);
            }
        }

        if (toDelete.Count == 0)
        {
            return 0;
        }

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                "DELETE FROM tracked_downloads WHERE id = @Id AND work_id IS NULL;",
                toDelete.Select(r => new { r.Id }),
                transaction: tx),
            ct);

        foreach (var row in toDelete)
        {
            existingTracked.Remove((row.DownloadClientId, row.ExternalDownloadId));
        }

        return toDelete.Count;
    }

    public async Task<ProcessedDownloadItemOutcome> RetryTrackedDownloadImportAsync(
        long trackedId,
        ManualOverrideImportRequest? overrideRequest = null,
        CancellationToken ct = default)
    {
        await _importGate.WaitAsync(ct);
        try
        {
            TrackedRow? row;
            await using (var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
            {
                row = await conn.QuerySingleOrDefaultAsync<TrackedRow>(
                    "SELECT * FROM tracked_downloads WHERE id = @Id;",
                    new { Id = trackedId });
            }

            if (row is null)
            {
                throw new KeyNotFoundException($"TrackedDownload #{trackedId} not found.");
            }

            var clients = (await downloadOrchestrator.ListClientsAsync(ct)).ToDictionary(c => c.Id);
            var queue = await downloadOrchestrator.GetUnifiedQueueAsync(ct);
            var liveItem = queue.FirstOrDefault(q => q.ClientId == row.DownloadClientId && q.ExternalId == row.ExternalDownloadId);

            if (liveItem is null)
            {
                var fallbackFiles = !string.IsNullOrWhiteSpace(row.OutputPath) && File.Exists(row.OutputPath)
                    ? new[] { row.OutputPath }
                    : !string.IsNullOrWhiteSpace(row.OutputPath) && Directory.Exists(row.OutputPath)
                        ? Directory.EnumerateFiles(row.OutputPath, "*", SearchOption.AllDirectories).Take(250).ToArray()
                        : Array.Empty<string>();

                liveItem = new ActiveDownloadQueueItem(
                    ClientId: row.DownloadClientId,
                    ClientName: row.ClientName,
                    Protocol: row.Protocol,
                    ExternalId: row.ExternalDownloadId,
                    InfoHash: row.InfoHash,
                    Title: row.Title,
                    Category: row.MediaVertical,
                    MediaVertical: row.MediaVertical,
                    Status: "Completed",
                    TotalBytes: 0,
                    RemainingBytes: 0,
                    OutputPath: row.OutputPath ?? "",
                    ContainedFiles: fallbackFiles,
                    IsStalled: false,
                    IsMetadataStuck: false,
                    ErrorMessage: null,
                    CanMoveFiles: row.CanMoveFiles != 0);
            }

            var grabHistory = await downloadOrchestrator.FindGrabHistoryAsync(liveItem.ClientId, liveItem.ExternalId, liveItem.InfoHash, ct);
            var libraries = await catalogService.ListLibrariesAsync(null, ct);
            var works = await catalogService.ListWorksAsync(null, null, null, ct);

            var effectiveOverride = (overrideRequest ?? new ManualOverrideImportRequest()) with
            {
                WorkId = overrideRequest?.WorkId ?? row.WorkId ?? grabHistory?.WorkId,
            };

            var outcome = await EvaluateAndImportSingleItemAsync(
                liveItem,
                clients.GetValueOrDefault(liveItem.ClientId),
                grabHistory,
                libraries,
                works,
                effectiveOverride,
                ct);

            if (realtime is not null)
            {
                await TryBroadcastAsync(PermissionScopes.QueueRead, "TrackedDownloadRefreshed", new { trackedId, outcome.Verdict }, ct);
            }

            return outcome;
        }
        finally
        {
            _importGate.Release();
        }
    }

    public async Task<TrackedDownloadDto?> IgnoreTrackedDownloadAsync(long trackedId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                UPDATE tracked_downloads
                SET state = @State, tracked_status = 'ok', updated_at = @Now
                WHERE id = @Id;
                """,
                new { Id = trackedId, State = TrackedDownloadStates.Ignored, Now = now },
                transaction: tx),
            ct);

        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var updated = await readConn.QuerySingleOrDefaultAsync<TrackedRow>("SELECT * FROM tracked_downloads WHERE id = @Id;", new { Id = trackedId });
        return updated is null ? null : MapTrackedDto(updated);
    }

    public async Task<IReadOnlyList<TrackedDownloadDto>> ListTrackedDownloadsAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<TrackedRow>(
            "SELECT * FROM tracked_downloads ORDER BY updated_at DESC, id DESC LIMIT @Lim;",
            new { Lim = Math.Clamp(limit, 1, 500) });
        return rows.Select(MapTrackedDto).ToList();
    }

    private async Task<ProcessedDownloadItemOutcome> EvaluateAndImportSingleItemAsync(
        ActiveDownloadQueueItem item,
        DownloadClientSummary? clientCfg,
        DownloadGrabHistoryEntry? grabHistory,
        IReadOnlyList<LibrarySummary> libraries,
        IReadOnlyList<MediaWorkSummary> works,
        ManualOverrideImportRequest? manualOverride,
        CancellationToken ct)
    {
        // Spec 1a: Terminal Unpack Failure (_FAILED_ marker from SABnzbd/NZBGet) -> fail, blocklist & auto-repull replacement immediately
        if (IsFailedUnpackPath(item.OutputPath, out var failedUnpackReason))
        {
            return await HandleTerminalDownloadFailureAsync(
                item,
                clientCfg,
                grabHistory,
                works,
                libraries,
                failedUnpackReason,
                ledgerVerdict: "ClientFailed",
                forceRemoveFromClient: false,
                explicitWorkId: null,
                explicitWorkTitle: null,
                explicitUnitIds: null,
                sourceFilePath: item.OutputPath,
                ct);
        }

        // Spec 1b: NotUnpackingSpecification (_UNPACK_, .part, .partial, !qB)
        if (IsUnpackingPathOrFiles(item.OutputPath, item.ContainedFiles, out var unpackReason))
        {
            await UpsertTrackedDownloadAsync(
                item,
                grabHistory?.WorkId,
                null,
                grabHistory?.UnitIds ?? [],
                TrackedDownloadStates.ImportPending,
                "warning",
                [unpackReason],
                null,
                ct);

            return new ProcessedDownloadItemOutcome(
                item.ClientId, item.ClientName, item.ExternalId, item.Title, item.MediaVertical,
                "DeferredUnpacking", item.OutputPath, null, false, null, unpackReason);
        }

        // Spec 2: Pre-Import Malware & Dangerous Extension Gate (.exe, .scr, .lnk, .bat, .iso, etc.)
        var safety = downloadOrchestrator.InspectFilesBeforeImport(item.ContainedFiles, item.MediaVertical);
        if (!safety.IsSafe)
        {
            var msg = safety.RejectionReason ?? "Blocked dangerous/executable file before import.";
            return await HandleTerminalDownloadFailureAsync(
                item,
                clientCfg,
                grabHistory,
                works,
                libraries,
                msg,
                ledgerVerdict: "BlockedMalware",
                forceRemoveFromClient: true,
                explicitWorkId: null,
                explicitWorkTitle: null,
                explicitUnitIds: null,
                sourceFilePath: item.OutputPath,
                ct);
        }

        // Spec 3: AggregationService (DownloadClientItem Title + Folder Name + GrabHistory ReleaseTitle)
        var parsed = AggregateJobReleaseInfo(item, grabHistory);

        // Spec 4: Media File Discovery & NotSampleSpecification
        var mediaFiles = parsed.MediaVertical is "tv" or "anime"
            ? SelectAllImportableMediaFiles(item.OutputPath, item.ContainedFiles)
            : SelectPrimaryMediaFile(item.OutputPath, item.ContainedFiles) is { } singleFile
                ? [singleFile]
                : [];

        if (mediaFiles.Count == 0)
        {
            if (grabHistory?.WorkId is { } grabbedWorkId &&
                await TryReconcileExternallyMovedDownloadAsync(grabbedWorkId, grabHistory.UnitIds, grabHistory.GrabbedAt, ct))
            {
                var tree = await catalogService.GetWorkTreeAsync(grabbedWorkId, null, ct);
                var reconciledUnits = grabHistory.UnitIds.Count > 0
                    ? tree?.Containers.SelectMany(c => c.Units).Where(u => grabHistory.UnitIds.Contains(u.Id)).ToList() ?? []
                    : tree?.Containers.SelectMany(c => c.Units).ToList() ?? [];
                var unitIds = reconciledUnits.Select(u => u.Id).ToArray();
                var paths = reconciledUnits.SelectMany(u => u.Files).Select(f => f.RelativePath).Distinct().ToArray();
                var reconciledLibRoot = tree is not null
                    ? libraries.FirstOrDefault(l => l.Id == tree.Work.LibraryId)?.RootFolderPath
                    : null;
                var reconciledLedgerDest = paths.Length > 0 && !string.IsNullOrWhiteSpace(reconciledLibRoot)
                    ? Path.Combine(reconciledLibRoot, paths[0])
                    : (paths.FirstOrDefault() ?? item.OutputPath ?? "");
                var reconciledLedgerMsg = $"Reconciled externally moved download for '{tree?.Work.Title ?? item.Title}' ({VerificationVerdicts.VerifiedPass}) -> {string.Join(", ", paths)}";

                if (clientCfg?.RemoveCompleted == true && item.CanMoveFiles)
                {
                    try
                    {
                        await downloadOrchestrator.RemoveDownloadFromClientAsync(item.ClientId, item.ExternalId, deleteData: true, ct);
                        TryDeleteImportedUsenetJobFolder(item, libraries);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger?.LogWarning(ex, "Reconciled {ExternalId} ({Title}), but removing it from {Client} failed; leaving it in the client.",
                            item.ExternalId, item.Title, item.ClientName);
                    }
                }

                await RecordImportLedgerAsync(item.ClientId, item.ExternalId, item.Title, VerificationVerdicts.VerifiedPass, reconciledLedgerDest, false, reconciledLedgerMsg, ct);
                await UpsertTrackedDownloadAsync(
                    item,
                    grabbedWorkId,
                    tree?.Work.Title,
                    unitIds,
                    TrackedDownloadStates.Imported,
                    "ok",
                    [],
                    null,
                    ct);

                if (realtime is not null && tree is not null)
                {
                    await TryBroadcastAsync(PermissionScopes.CatalogRead, "MediaImported", new
                    {
                        workId = tree.Work.Id,
                        workTitle = tree.Work.Title,
                        unitIds,
                        paths,
                        verdict = VerificationVerdicts.VerifiedPass,
                    }, ct);
                }

                return new ProcessedDownloadItemOutcome(
                    item.ClientId, item.ClientName, item.ExternalId, item.Title, parsed.MediaVertical,
                    VerificationVerdicts.VerifiedPass, item.OutputPath, reconciledLedgerDest, false, null, reconciledLedgerMsg);
            }

            var msg = $"No importable non-sample media file found in '{item.OutputPath}'.";
            await UpsertTrackedDownloadAsync(
                item,
                grabHistory?.WorkId,
                null,
                grabHistory?.UnitIds ?? [],
                TrackedDownloadStates.ImportBlocked,
                "warning",
                [msg],
                null,
                ct);

            return new ProcessedDownloadItemOutcome(
                item.ClientId, item.ClientName, item.ExternalId, item.Title, parsed.MediaVertical,
                "ImportBlocked", item.OutputPath, null, false, null, msg);
        }

        // Resolve target Work (Manual Override -> GrabHistory Correlation -> CleanSeriesTitle / SortTitle / AltTitles)
        MediaWorkSummary? matchedWork;
        string? grabMismatchReason = null;
        if (manualOverride?.WorkId is { } explicitWorkId)
        {
            matchedWork = works.FirstOrDefault(w => w.Id == explicitWorkId);
        }
        else
        {
            var preferredLib = ResolveTargetLibrary(libraries, parsed.MediaVertical);
            matchedWork = ResolveCorrelatedOrTitleMatchedWork(works, preferredLib, parsed, grabHistory, out grabMismatchReason, item.Title, item.OutputPath, parserRouter);
        }

        if (grabMismatchReason is not null && manualOverride?.WorkId is null)
        {
            await UpsertTrackedDownloadAsync(
                item,
                grabHistory?.WorkId,
                matchedWork?.Title,
                grabHistory?.UnitIds ?? [],
                TrackedDownloadStates.ImportBlocked,
                "warning",
                [grabMismatchReason],
                null,
                ct);

            return new ProcessedDownloadItemOutcome(
                item.ClientId, item.ClientName, item.ExternalId, item.Title, parsed.MediaVertical,
                "ImportBlocked", mediaFiles[0], null, false, null, grabMismatchReason);
        }

        var targetLibrary = matchedWork is not null
            ? libraries.FirstOrDefault(l => l.Id == matchedWork.LibraryId) ?? ResolveTargetLibrary(libraries, parsed.MediaVertical)
            : ResolveTargetLibrary(libraries, parsed.MediaVertical);

        if (targetLibrary is null)
        {
            var msg = $"No configured library found for vertical '{parsed.MediaVertical}'.";
            await UpsertTrackedDownloadAsync(
                item,
                matchedWork?.Id,
                matchedWork?.Title,
                [],
                TrackedDownloadStates.ImportBlocked,
                "warning",
                [msg],
                null,
                ct);

            return new ProcessedDownloadItemOutcome(
                item.ClientId, item.ClientName, item.ExternalId, item.Title, parsed.MediaVertical,
                "ImportBlocked", mediaFiles[0], null, false, null, msg);
        }

        MediaWorkDetailTree workTree;
        if (matchedWork is not null)
        {
            workTree = (await catalogService.GetWorkTreeAsync(matchedWork.Id, null, ct))!;
        }
        else if (parsed.MediaVertical is "tv" or "anime")
        {
            // Sonarr ParsingService rule: NEVER create an orphan 1-episode stub series when an episodic show isn't in the catalog!
            var unknownSeriesMsg = $"Unknown series '{parsed.CleanTitle}' — add series to catalog first or map it via Retry / Manual Import.";
            await UpsertTrackedDownloadAsync(
                item,
                null,
                null,
                [],
                TrackedDownloadStates.ImportBlocked,
                "warning",
                [unknownSeriesMsg],
                null,
                ct);

            return new ProcessedDownloadItemOutcome(
                item.ClientId, item.ClientName, item.ExternalId, item.Title, parsed.MediaVertical,
                "ImportBlocked", mediaFiles[0], null, false, null, unknownSeriesMsg);
        }
        else
        {
            var newWorkKind = parsed.MediaVertical is "music" or "ebooks" or "audiobooks" or "comics" ? parsed.MediaVertical : "movies";

            // ResolveTargetLibrary falls back to the first library; a new work must go into one of its own kind
            var kindLibrary = PolymorphicCatalogService.LibraryAcceptsWorkKind(targetLibrary.MediaType, newWorkKind)
                ? targetLibrary
                : libraries.FirstOrDefault(l => PolymorphicCatalogService.LibraryAcceptsWorkKind(l.MediaType, newWorkKind));
            if (kindLibrary is null)
            {
                var noLibraryMsg = $"No library for '{newWorkKind}' is configured; '{parsed.CleanTitle}' was not added to '{targetLibrary.Name}' ({targetLibrary.MediaType}).";
                await UpsertTrackedDownloadAsync(
                    item,
                    null,
                    null,
                    [],
                    TrackedDownloadStates.ImportBlocked,
                    "warning",
                    [noLibraryMsg],
                    null,
                    ct);

                return new ProcessedDownloadItemOutcome(
                    item.ClientId, item.ClientName, item.ExternalId, item.Title, parsed.MediaVertical,
                    "ImportBlocked", mediaFiles[0], null, false, null, noLibraryMsg);
            }

            targetLibrary = kindLibrary;
            workTree = await catalogService.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: targetLibrary.Id,
                Title: parsed.CleanTitle,
                WorkKind: newWorkKind,
                Year: parsed.Year,
                InitialRequiredAudioLangs: parsed.DetectedLanguages.Where(l => !string.Equals(l, "multi", StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } expLangs ? expLangs : ["en"]), null, ct);
        }

        // Spec 5: FreeSpaceSpecification
        if (!CheckLibraryFreeSpace(targetLibrary.RootFolderPath, mediaFiles, out var freeSpaceError))
        {
            await UpsertTrackedDownloadAsync(
                item,
                workTree.Work.Id,
                workTree.Work.Title,
                [],
                TrackedDownloadStates.ImportBlocked,
                "warning",
                [freeSpaceError!],
                null,
                ct);

            return new ProcessedDownloadItemOutcome(
                item.ClientId, item.ClientName, item.ExternalId, item.Title, parsed.MediaVertical,
                "ImportBlocked", mediaFiles[0], null, false, null, freeSpaceError!);
        }

        // Transition TrackedDownload -> Importing
        await UpsertTrackedDownloadAsync(
            item,
            workTree.Work.Id,
            workTree.Work.Title,
            grabHistory?.UnitIds ?? [],
            TrackedDownloadStates.Importing,
            "ok",
            [],
            null,
            ct);

        var useHardlinksRaw = await appSettings.GetAsync("naming.use_hardlinks", ct);
        var useHardlinks = bool.TryParse(useHardlinksRaw?.Trim('"'), out var parsedHl) && parsedHl;

        var jobAllHardlinked = true;
        var jobImportedPaths = new List<string>();
        var jobImportedUnitIds = new List<long>();
        var jobLastVerdict = VerificationVerdicts.VerifiedPass;
        ProcessedDownloadItemOutcome? lastFileOutcome = null;
        var missingStagingFileEncountered = false;
        var missingStagingMessages = new List<string>();

        foreach (var mediaFile in mediaFiles)
        {
            if (!File.Exists(mediaFile))
            {
                var missingMsg = $"Staged media file '{mediaFile}' not found on disk.";
                logger?.LogWarning("Staged media file {Path} not found for download {Title}.", mediaFile, item.Title);
                missingStagingFileEncountered = true;
                missingStagingMessages.Add(missingMsg);
                continue;
            }

            var fileParsed = AggregateSingleFileReleaseInfo(parsed, mediaFile, workTree, manualOverride);

            if (workTree.Work.WorkKind is "tv" or "anime" &&
                fileParsed.SeasonNumber == 0 &&
                fileParsed.EpisodeNumbers.Count > 0)
            {
                workTree = await EnsureSpecialsContainerAndUnitsAsync(workTree, fileParsed.EpisodeNumbers, ct);
            }

            // Spec 6: MatchesGrabAndUnitSpecification (Strict episodic resolution — NEVER overwrite S01E01 on missing SxxEyy!)
            var (targetUnits, unitMatchError) = ResolveTargetUnitsStrict(
                workTree,
                fileParsed,
                grabHistory,
                manualOverride,
                isSingleFileJob: mediaFiles.Count == 1);

            if (unitMatchError is not null || targetUnits.Count == 0)
            {
                var blockReason = unitMatchError ?? $"Could not match '{Path.GetFileName(mediaFile)}' to a unit in '{workTree.Work.Title}'.";
                await UpsertTrackedDownloadAsync(
                    item,
                    workTree.Work.Id,
                    workTree.Work.Title,
                    [],
                    TrackedDownloadStates.ImportBlocked,
                    "warning",
                    [blockReason],
                    null,
                    ct);

                return new ProcessedDownloadItemOutcome(
                    item.ClientId, item.ClientName, item.ExternalId, item.Title, fileParsed.MediaVertical,
                    "ImportBlocked", mediaFile, null, false, null, blockReason);
            }

            var primaryUnit = targetUnits[0];
            var primaryContainer = workTree.Containers.FirstOrDefault(c => c.Id == primaryUnit.ContainerId);
            fileParsed = fileParsed with
            {
                SeasonNumber = primaryContainer?.ContainerNumber ?? fileParsed.SeasonNumber,
                EpisodeNumbers = targetUnits.Select(u => u.UnitNumber).Distinct().OrderBy(x => x).ToArray(),
                AbsoluteEpisodeNumbers = targetUnits.Where(u => u.AbsoluteNumber.HasValue).Select(u => u.AbsoluteNumber!.Value).Distinct().OrderBy(x => x).ToArray()
            };
            var effectiveProfileId = primaryUnit.ProfileOverrideId ?? workTree.Work.QualityProfileId ?? targetLibrary.DefaultProfileId;

            // Spec 7: UpgradeSpecification (evaluate against existing attached MediaFile via ComposableProfileService)
            if (primaryUnit.Files.Count > 0 &&
                !(manualOverride?.BypassUpgradeCheck ?? false) &&
                profileService is not null &&
                effectiveProfileId.HasValue)
            {
                var fileStem = Path.GetFileNameWithoutExtension(mediaFile);
                var simTitle = item.Title;
                if (mediaFiles.Count > 1)
                {
                    var innerParsed = parserRouter.Parse(fileStem, fileParsed.MediaVertical);
                    var innerLacksQuality = innerParsed.Resolution is null || innerParsed.Source is null;
                    var innerLacksLang = innerParsed.DetectedLanguages.Count == 0 && !innerParsed.IsDualAudio;
                    var jobHasQuality = parsed.Resolution is not null || parsed.Source is not null;
                    var jobHasLang = parsed.DetectedLanguages.Count > 0 || parsed.IsDualAudio;
                    if ((innerLacksQuality && jobHasQuality) || (innerLacksLang && jobHasLang))
                    {
                        var jobSuffix = !string.IsNullOrWhiteSpace(grabHistory?.ReleaseTitle) &&
                                        !string.Equals(grabHistory.ReleaseTitle, item.Title, StringComparison.OrdinalIgnoreCase)
                            ? $"{item.Title} {grabHistory.ReleaseTitle}"
                            : item.Title;
                        simTitle = string.IsNullOrWhiteSpace(jobSuffix) ? fileStem : $"{fileStem} {jobSuffix}";
                    }
                    else
                    {
                        simTitle = fileStem;
                    }
                }

                var simScorecard = await profileService.SimulateAsync(
                    new SimulateReleaseRequest(
                        ProfileId: effectiveProfileId.Value,
                        ReleaseTitle: simTitle,
                        UnitId: primaryUnit.Id,
                        OriginalLanguage: workTree.Work.OriginalLanguage,
                        SizeBytes: new FileInfo(mediaFile).Length),
                    ct);

                if (!simScorecard.Approved)
                {
                    var upgradeRejectMsg = $"Upgrade rejected ({simScorecard.DecisionKind}): {simScorecard.Explanation}";
                    await UpsertTrackedDownloadAsync(
                        item,
                        workTree.Work.Id,
                        workTree.Work.Title,
                        targetUnits.Select(u => u.Id).ToArray(),
                        TrackedDownloadStates.ImportBlocked,
                        "warning",
                        [upgradeRejectMsg],
                        null,
                        ct);

                    return new ProcessedDownloadItemOutcome(
                        item.ClientId, item.ClientName, item.ExternalId, item.Title, fileParsed.MediaVertical,
                        "ImportBlocked", mediaFile, null, false, null, upgradeRejectMsg);
                }
            }

            LanguagePolicySummary? effectiveLanguagePolicy = null;
            if (profileService is not null)
            {
                if (workTree.Work.LanguagePolicyId.HasValue)
                {
                    var allPolicies = await profileService.ListLanguagePoliciesAsync(ct);
                    effectiveLanguagePolicy = allPolicies.FirstOrDefault(p => p.Id == workTree.Work.LanguagePolicyId.Value);
                }

                if (effectiveLanguagePolicy is null && effectiveProfileId.HasValue)
                {
                    var prof = await profileService.GetProfileByIdAsync(effectiveProfileId.Value, ct);
                    effectiveLanguagePolicy = prof?.LanguagePolicy;
                }
            }

            // Spec 8: 3-Tier Post-Download Language & Fake-Dub Verification (with Manual/Quarantine-Approved override support)
            var stagedSidecarSubs = MediaManagementAndDiskScanService.DetectSidecarSubtitleLanguages(mediaFile);
            MediaFileVerificationReport report;
            IReadOnlyList<string> expectedAudio = [];
            if (manualOverride?.ConfirmedAudioLanguages is { Count: > 0 } confirmedLangs)
            {
                var normConfirmed = confirmedLangs
                    .Select(l => LanguageCodeNormalizer.NormalizeToIso2(l))
                    .Where(l => l != "und")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (normConfirmed.Count == 0) normConfirmed.Add("en");

                report = new MediaFileVerificationReport(
                    JobId: 0,
                    UnitId: primaryUnit.Id,
                    StagedFilePath: mediaFile,
                    MediaVertical: fileParsed.MediaVertical,
                    Verdict: VerificationVerdicts.VerifiedPass,
                    OverallConfidence: 1.0,
                    HeaderAutoPatched: false,
                    ExistingFilePreservedOnFailure: false,
                    HashBlocklisted: false,
                    QuarantineId: null,
                    DurationMs: 1,
                    VerifiedAudioLanguages: normConfirmed,
                    VerifiedSubtitleLanguages: [],
                    Streams: [],
                    VerifiedAt: DateTimeOffset.UtcNow.ToString("O"));
            }
            else
            {
                expectedAudio = BuildExpectedAudioLanguages(fileParsed, targetUnits, workTree.Work.OriginalLanguage, effectiveLanguagePolicy);
                try
                {
                    report = await verifier.VerifyStagedFileAsync(new RunVerificationRequest(
                        StagedFilePath: mediaFile,
                        MediaVertical: fileParsed.MediaVertical,
                        ReleaseTitle: mediaFiles.Count > 1 ? Path.GetFileName(mediaFile) : item.Title,
                        InfoHash: item.InfoHash,
                        UnitId: primaryUnit.Id,
                        ProfileId: effectiveProfileId,
                        OriginalLanguage: workTree.Work.OriginalLanguage,
                        ExpectedAudioLanguages: expectedAudio,
                        VerificationMode: "verify_content_on_upgrades_and_ambiguous"), ct);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    var msg = $"Staging file '{mediaFile}' vanished during verification.";
                    logger?.LogWarning(ex, "Staging file {Path} vanished during verification for {Title}.", mediaFile, item.Title);
                    missingStagingFileEncountered = true;
                    missingStagingMessages.Add(msg);
                    continue;
                }

                if (expectedAudio.Count > 0 &&
                    (report.Verdict == VerificationVerdicts.QuarantineReview || report.Verdict == VerificationVerdicts.VerifiedFail))
                {
                    var allExpectedPresent = expectedAudio.All(reqLang =>
                        report.Streams.Any(s =>
                            string.Equals(s.StreamType, "audio", StringComparison.OrdinalIgnoreCase) &&
                            !s.IsDuplicateFakeDub &&
                            string.Equals(s.DetectedLanguage, reqLang, StringComparison.OrdinalIgnoreCase) &&
                            s.Confidence >= 0.80));

                    if (allExpectedPresent)
                    {
                        var origQuarantineId = report.QuarantineId;
                        var origHashBlocklisted = report.HashBlocklisted;

                        report = report with { Verdict = VerificationVerdicts.VerifiedPass, QuarantineId = null };

                        if (origQuarantineId.HasValue)
                        {
                            try
                            {
                                await dbFactory.ExecuteWriteAsync(DatabaseKind.Core, async (conn, tx) =>
                                {
                                    await conn.ExecuteAsync("DELETE FROM quarantined_imports WHERE id = @Qid;", new { Qid = origQuarantineId.Value }, tx);
                                }, ct);
                            }
                            catch (Exception ex)
                            {
                                logger?.LogWarning(ex, "Failed to clean up quarantined_imports row {Qid} after override.", origQuarantineId.Value);
                            }
                        }

                        if (origHashBlocklisted && !string.IsNullOrWhiteSpace(item.InfoHash))
                        {
                            try
                            {
                                await dbFactory.ExecuteWriteAsync(DatabaseKind.Core, async (conn, tx) =>
                                {
                                    await conn.ExecuteAsync("DELETE FROM recurring_blocklist_hashes WHERE info_hash = @Hash;", new { Hash = item.InfoHash }, tx);
                                }, ct);
                            }
                            catch (Exception ex)
                            {
                                logger?.LogWarning(ex, "Failed to clean up recurring_blocklist_hashes row for {Hash} after override.", item.InfoHash);
                            }
                        }
                    }
                }
            }

            var mergedAudioLangs = report.VerifiedAudioLanguages
                .Where(l => !string.IsNullOrWhiteSpace(l) && !string.Equals(l, "und", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var mergedSubtitleLangs = report.VerifiedSubtitleLanguages
                .Concat(stagedSidecarSubs)
                .Where(l => !string.IsNullOrWhiteSpace(l) && !string.Equals(l, "und", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            LanguagePolicyOnDiskEvaluation? diskPolicyCheck = null;
            if (effectiveLanguagePolicy is not null && profileService is not null)
            {
                diskPolicyCheck = profileService.EvaluateOnDiskLanguagesAgainstPolicy(
                    effectiveLanguagePolicy,
                    workTree.Work.OriginalLanguage,
                    mergedAudioLangs,
                    mergedSubtitleLangs);
            }

            if (manualOverride?.ConfirmedAudioLanguages is not { Count: > 0 } &&
                (report.Verdict is VerificationVerdicts.VerifiedPass or VerificationVerdicts.VerifiedPassAutoTagged or VerificationVerdicts.MusicPolicyPass) &&
                diskPolicyCheck is { PolicyAllowed: false })
            {
                var origIso = LanguageCodeNormalizer.NormalizeToIso2(workTree.Work.OriginalLanguage);
                var unitAudioReqs = targetUnits.SelectMany(u => u.RequiredAudioLangs)
                    .Select(r => string.Equals(r, "original", StringComparison.OrdinalIgnoreCase) ? origIso : LanguageCodeNormalizer.NormalizeToIso2(r))
                    .Where(r => r != "und").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var unitSubReqs = targetUnits.SelectMany(u => u.RequiredSubLangs)
                    .Select(r => string.Equals(r, "original", StringComparison.OrdinalIgnoreCase) ? origIso : LanguageCodeNormalizer.NormalizeToIso2(r))
                    .Where(r => r != "und").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var unitPromotedAllowed = unitAudioReqs.Length > 0 &&
                    unitAudioReqs.All(r => mergedAudioLangs.Contains(r, StringComparer.OrdinalIgnoreCase)) &&
                    unitSubReqs.All(r => mergedSubtitleLangs.Contains(r, StringComparer.OrdinalIgnoreCase));

                if (!unitPromotedAllowed)
                {
                    await catalogService.UpdateUnitAsync(primaryUnit.Id, new UpdateMediaUnitRequest(LanguageCutoffMet: primaryUnit.LanguageCutoffMet), ct);
                    report = report with
                    {
                        Verdict = VerificationVerdicts.VerifiedFail,
                        FailureReason = diskPolicyCheck.FailureReason,
                        FailureCode = "language_mismatch",
                        SatisfiesLanguageCutoff = false
                    };

                    if (report.JobId > 0)
                    {
                        var serializedReport = JsonSerializer.Serialize(report, JsonOpts);
                        await dbFactory.ExecuteWriteAsync(DatabaseKind.Core, (conn, tx) => conn.ExecuteAsync(
                            "UPDATE verification_jobs SET verdict = 'VerifiedFail', report_json = @ReportJson WHERE id = @Jid;",
                            new { ReportJson = serializedReport, Jid = report.JobId }, tx), ct);
                    }
                }
            }

            if (report.Verdict == VerificationVerdicts.QuarantineReview)
            {
                var qMsg = $"Held in Quarantine (#{report.QuarantineId}) — ambiguous language confidence ({report.OverallConfidence:P0}); existing library file untouched.";
                await RecordImportLedgerAsync(item.ClientId, item.ExternalId, item.Title, report.Verdict, null, false, qMsg, ct);
                await UpsertTrackedDownloadAsync(
                    item,
                    workTree.Work.Id,
                    workTree.Work.Title,
                    targetUnits.Select(u => u.Id).ToArray(),
                    TrackedDownloadStates.ImportBlocked,
                    "warning",
                    [qMsg],
                    report.QuarantineId,
                    ct);

                return new ProcessedDownloadItemOutcome(
                    item.ClientId, item.ClientName, item.ExternalId, item.Title, fileParsed.MediaVertical,
                    report.Verdict, mediaFile, null, false, report.QuarantineId, qMsg);
            }

            if (report.Verdict == VerificationVerdicts.VerifiedFail)
            {
                var failMsg = $"Blocked ({report.Verdict}) — fake duplicate dub or missing expected language.";
                return await HandleTerminalDownloadFailureAsync(
                    item,
                    clientCfg,
                    grabHistory,
                    works,
                    libraries,
                    failMsg,
                    ledgerVerdict: report.Verdict,
                    forceRemoveFromClient: false,
                    explicitWorkId: workTree.Work.Id,
                    explicitWorkTitle: workTree.Work.Title,
                    explicitUnitIds: targetUnits.Select(u => u.Id).ToArray(),
                    sourceFilePath: mediaFile,
                    ct);
            }

            fileParsed = fileParsed with
            {
                DetectedLanguages = mergedAudioLangs.Length > 0
                    ? mergedAudioLangs
                    : fileParsed.DetectedLanguages
            };

            // Spec 9: Atomic Hardlink / Move / Copy Import into Library Root (+ companion subtitle sidecars!)
            OrganizeFileResult organized;
            try
            {
                organized = await organizer.ImportFileAtomicallyAsync(new OrganizeFileRequest(
                    SourceFilePath: mediaFile,
                    LibraryRootPath: targetLibrary.RootFolderPath,
                    ParsedInfo: fileParsed,
                    PreferHardlink: useHardlinks || !item.CanMoveFiles,
                    CanMoveSource: item.CanMoveFiles,
                    ImportCompanionSubtitles: true,
                    NamingContext: BuildExistingWorkNamingContext(workTree, targetLibrary.RootFolderPath, fileParsed.SeasonNumber, primaryUnit.Title)), ct);
            }
            catch (Exception ex)
            {
                try
                {
                    await catalogService.UpdateUnitAsync(primaryUnit.Id, new UpdateMediaUnitRequest(LanguageCutoffMet: primaryUnit.LanguageCutoffMet), CancellationToken.None);
                }
                catch
                {
                    // Best-effort rollback of premature verifier cutoff flag
                }

                if (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    logger?.LogWarning(ex, "Staging media file {Path} vanished before atomic import.", mediaFile);
                    missingStagingFileEncountered = true;
                    missingStagingMessages.Add($"Staging media file '{mediaFile}' vanished before atomic import.");
                    continue;
                }

                throw;
            }

            var relPath = Path.GetRelativePath(targetLibrary.RootFolderPath, organized.DestinationFilePath);
            var fileSize = new FileInfo(organized.DestinationFilePath).Length;
            var qualityName = fileParsed.ToQualityName();

            var videoCodec = !string.IsNullOrWhiteSpace(fileParsed.VideoCodec)
                ? fileParsed.VideoCodec
                : report.Streams.FirstOrDefault(s => string.Equals(s.StreamType, "video", StringComparison.OrdinalIgnoreCase))?.Codec;
            var audioCodec = !string.IsNullOrWhiteSpace(fileParsed.AudioCodec)
                ? fileParsed.AudioCodec
                : report.Streams.FirstOrDefault(s => string.Equals(s.StreamType, "audio", StringComparison.OrdinalIgnoreCase))?.Codec;

            foreach (var (unit, unitIdx) in targetUnits.Select((u, i) => (u, i)))
            {
                await dbFactory.ExecuteWriteAsync(
                    DatabaseKind.Core,
                    (conn, tx) => conn.ExecuteAsync(
                        "DELETE FROM media_files WHERE unit_id = @Uid;",
                        new { Uid = unit.Id },
                        transaction: tx),
                    ct);

                var attachedFile = await catalogService.AttachFileAsync(new AttachMediaFileRequest(
                    UnitId: unit.Id,
                    RelativePath: relPath,
                    SizeBytes: fileSize,
                    QualityName: qualityName,
                    EditionName: fileParsed.MovieEdition,
                    Resolution: fileParsed.Resolution,
                    VideoCodec: videoCodec,
                    HdrFormat: fileParsed.HdrFormat,
                    AudioCodec: audioCodec,
                    DetectedAudioLangs: mergedAudioLangs,
                    DetectedSubLangs: mergedSubtitleLangs,
                    VerifiedStatus: report.Verdict,
                    InodeHardlinked: organized.Hardlinked), ct);

                var (qualMet, langMet) = await EvaluateImportedCutoffsAsync(
                    unit,
                    effectiveProfileId,
                    workTree.Work.OriginalLanguage,
                    item.Title,
                    mergedAudioLangs,
                    mergedSubtitleLangs,
                    workTree.Work.LanguagePolicyId,
                    effectiveLanguagePolicy,
                    ct);

                await catalogService.UpdateUnitAsync(
                    unit.Id,
                    new UpdateMediaUnitRequest(QualityCutoffMet: qualMet, LanguageCutoffMet: langMet),
                    ct);

                var persistedReport = report with
                {
                    UnitId = unit.Id,
                    StagedFilePath = organized.DestinationFilePath,
                    Verdict = report.Verdict,
                    QuarantineId = null,
                    ExistingFilePreservedOnFailure = false,
                    VerifiedAudioLanguages = mergedAudioLangs,
                    VerifiedSubtitleLanguages = mergedSubtitleLangs,
                    FailureReason = null,
                    FailureCode = null,
                    MatchedPolicyTier = diskPolicyCheck?.MatchedTier,
                    MatchedPolicyTierLabel = diskPolicyCheck?.MatchedTierLabel,
                    SatisfiesLanguageCutoff = langMet,
                };

                var rowsAffected = 0;
                if (unitIdx == 0 && report.JobId > 0)
                {
                    var rep = persistedReport with { JobId = report.JobId };
                    var reportJson = JsonSerializer.Serialize(rep, JsonOpts);
                    rowsAffected = await dbFactory.ExecuteWriteAsync(DatabaseKind.Core, (conn, tx) => conn.ExecuteAsync(
                        """
                        UPDATE verification_jobs
                        SET unit_id = @Uid,
                            media_file_id = @Fid,
                            staged_file_path = @DestPath,
                            verdict = @Verdict,
                            overall_confidence = @Conf,
                            detected_audio_langs_json = @AudioJson,
                            detected_sub_langs_json = @SubJson,
                            report_json = @ReportJson
                        WHERE id = @Jid;
                        """,
                        new
                        {
                            Uid = unit.Id,
                            Fid = attachedFile.Id,
                            DestPath = organized.DestinationFilePath,
                            Verdict = rep.Verdict,
                            Conf = rep.OverallConfidence,
                            AudioJson = JsonSerializer.Serialize(mergedAudioLangs, JsonOpts),
                            SubJson = JsonSerializer.Serialize(mergedSubtitleLangs, JsonOpts),
                            ReportJson = reportJson,
                            Jid = report.JobId
                        }, transaction: tx), ct);
                }

                if (rowsAffected == 0)
                {
                    var audioJson = JsonSerializer.Serialize(mergedAudioLangs, JsonOpts);
                    var subJson = JsonSerializer.Serialize(mergedSubtitleLangs, JsonOpts);
                    var initialReportJson = JsonSerializer.Serialize(persistedReport, JsonOpts);
                    var nowIso = DateTimeOffset.UtcNow.ToString("O");

                    await dbFactory.ExecuteWriteAsync(DatabaseKind.Core, async (conn, tx) =>
                    {
                        var id = await conn.ExecuteScalarAsync<long>(
                            """
                            INSERT INTO verification_jobs (
                                unit_id, media_file_id, staged_file_path, media_vertical,
                                verdict, overall_confidence, header_auto_patched,
                                detected_audio_langs_json, detected_sub_langs_json,
                                report_json, duration_ms, created_at)
                            VALUES (
                                @Uid, @Fid, @DestPath, @Vertical,
                                @Verdict, @Conf, @Patched,
                                @AudioJson, @SubJson,
                                @ReportJson, @DurationMs, @CreatedAt);
                            SELECT last_insert_rowid();
                            """,
                            new
                            {
                                Uid = unit.Id,
                                Fid = attachedFile.Id,
                                DestPath = organized.DestinationFilePath,
                                Vertical = fileParsed.MediaVertical,
                                Verdict = persistedReport.Verdict,
                                Conf = persistedReport.OverallConfidence,
                                Patched = persistedReport.HeaderAutoPatched ? 1 : 0,
                                AudioJson = audioJson,
                                SubJson = subJson,
                                ReportJson = initialReportJson,
                                DurationMs = Math.Max(1, persistedReport.DurationMs),
                                CreatedAt = nowIso
                            }, transaction: tx);

                        var finalReportJson = JsonSerializer.Serialize(persistedReport with { JobId = id }, JsonOpts);
                        await conn.ExecuteAsync(
                            "UPDATE verification_jobs SET report_json = @ReportJson WHERE id = @Id;",
                            new { ReportJson = finalReportJson, Id = id },
                            transaction: tx);
                    }, ct);
                }

                jobImportedUnitIds.Add(unit.Id);
            }

            if (mediaFiles.Count > 1)
            {
                await TryBroadcastAsync(PermissionScopes.CatalogRead, "catalog:updated", new { workId = workTree.Work.Id }, ct);
            }

            jobAllHardlinked &= organized.Hardlinked;
            jobImportedPaths.Add(relPath);
            jobLastVerdict = report.Verdict;

            var okMsg = $"Imported via {organized.TransferMode} ({report.Verdict}, audio=[{string.Join(',', mergedAudioLangs)}]) -> {relPath}";
            lastFileOutcome = new ProcessedDownloadItemOutcome(
                item.ClientId, item.ClientName, item.ExternalId, item.Title, fileParsed.MediaVertical,
                report.Verdict, mediaFile, organized.DestinationFilePath, organized.Hardlinked, null, okMsg);
        }

        if (missingStagingFileEncountered)
        {
            var reconciled = await TryReconcileExternallyMovedDownloadAsync(workTree.Work.Id, grabHistory?.UnitIds, grabHistory?.GrabbedAt, ct);
            if (reconciled)
            {
                var refreshedTree = await catalogService.GetWorkTreeAsync(workTree.Work.Id, null, ct);
                var expectedUnitIds = grabHistory?.UnitIds is { Count: > 0 } gUnitIds
                    ? gUnitIds
                    : (jobImportedUnitIds.Count > 0 ? jobImportedUnitIds : refreshedTree?.Containers.SelectMany(c => c.Units).Select(u => u.Id).ToList() ?? []);
                var allReconciledUnits = refreshedTree?.Containers.SelectMany(c => c.Units)
                    .Where(u => expectedUnitIds.Contains(u.Id)).ToList() ?? [];
                var unitIds = allReconciledUnits.Select(u => u.Id).ToArray();
                var paths = allReconciledUnits.SelectMany(u => u.Files).Select(f => f.RelativePath).Distinct().ToArray();
                var reconciledLedgerDest = paths.Length > 0
                    ? Path.Combine(targetLibrary.RootFolderPath, paths[0])
                    : (item.OutputPath ?? "");
                var reconciledLedgerMsg = $"Reconciled externally moved download for '{workTree.Work.Title}' ({VerificationVerdicts.VerifiedPass}) -> {string.Join(", ", paths)}";

                if (clientCfg?.RemoveCompleted == true && item.CanMoveFiles)
                {
                    try
                    {
                        await downloadOrchestrator.RemoveDownloadFromClientAsync(item.ClientId, item.ExternalId, deleteData: true, ct);
                        TryDeleteImportedUsenetJobFolder(item, libraries);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger?.LogWarning(ex, "Reconciled {ExternalId} ({Title}), but removing it from {Client} failed; leaving it in the client.",
                            item.ExternalId, item.Title, item.ClientName);
                    }
                }

                await RecordImportLedgerAsync(item.ClientId, item.ExternalId, item.Title, VerificationVerdicts.VerifiedPass, reconciledLedgerDest, false, reconciledLedgerMsg, ct);
                await UpsertTrackedDownloadAsync(
                    item,
                    workTree.Work.Id,
                    workTree.Work.Title,
                    unitIds,
                    TrackedDownloadStates.Imported,
                    "ok",
                    [],
                    null,
                    ct);

                if (realtime is not null)
                {
                    await TryBroadcastAsync(PermissionScopes.CatalogRead, "MediaImported", new
                    {
                        workId = workTree.Work.Id,
                        workTitle = workTree.Work.Title,
                        unitIds,
                        paths,
                        verdict = VerificationVerdicts.VerifiedPass,
                    }, ct);
                }

                return new ProcessedDownloadItemOutcome(
                    item.ClientId, item.ClientName, item.ExternalId, item.Title, parsed.MediaVertical,
                    VerificationVerdicts.VerifiedPass, item.OutputPath, reconciledLedgerDest, false, null, reconciledLedgerMsg);
            }
            else
            {
                var blockReason = missingStagingMessages.Count > 0
                    ? string.Join("; ", missingStagingMessages)
                    : $"One or more staging media files vanished before import for '{workTree.Work.Title}'.";

                await UpsertTrackedDownloadAsync(
                    item,
                    workTree.Work.Id,
                    workTree.Work.Title,
                    jobImportedUnitIds.Count > 0 ? jobImportedUnitIds.Distinct().ToArray() : (grabHistory?.UnitIds ?? []),
                    TrackedDownloadStates.ImportBlocked,
                    "warning",
                    [blockReason],
                    null,
                    ct);

                return new ProcessedDownloadItemOutcome(
                    item.ClientId, item.ClientName, item.ExternalId, item.Title, parsed.MediaVertical,
                    "ImportBlocked", item.OutputPath, null, false, null, blockReason);
            }
        }

        // Only remove from download client when RemoveCompleted is enabled AND files are movable (never delete active seeding torrents!).
        // Every media file of the job was imported by now (any failure returns above), so the source data is no longer needed.
        // The files are already imported at this point: a client that refuses the removal (403, timeout, unsupported
        // driver) must not turn a successful import into ImportBlocked and a retry on every poll (#68)
        if (clientCfg?.RemoveCompleted == true && item.CanMoveFiles)
        {
            try
            {
                await downloadOrchestrator.RemoveDownloadFromClientAsync(item.ClientId, item.ExternalId, deleteData: true, ct);
                TryDeleteImportedUsenetJobFolder(item, libraries);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Imported {ExternalId} ({Title}), but removing it from {Client} failed; leaving it in the client.",
                    item.ExternalId, item.Title, item.ClientName);
            }
        }

        await TryTriggerPlexSectionRefreshAsync(targetLibrary.PlexSectionId, targetLibrary.RootFolderPath, jobImportedPaths.FirstOrDefault(), ct);

        var ledgerDest = Path.Combine(targetLibrary.RootFolderPath, jobImportedPaths[0]);
        var ledgerMsg = jobImportedPaths.Count == 1
            ? lastFileOutcome!.Message
            : $"Imported {jobImportedPaths.Count} episode files ({jobLastVerdict}) -> {string.Join(", ", jobImportedPaths)}";

        await RecordImportLedgerAsync(item.ClientId, item.ExternalId, item.Title, jobLastVerdict, ledgerDest, jobAllHardlinked, ledgerMsg, ct);
        await UpsertTrackedDownloadAsync(
            item,
            workTree.Work.Id,
            workTree.Work.Title,
            jobImportedUnitIds.Distinct().ToArray(),
            TrackedDownloadStates.Imported,
            "ok",
            [],
            null,
            ct);

        if (realtime is not null)
        {
            await TryBroadcastAsync(PermissionScopes.CatalogRead, "MediaImported", new
            {
                workId = workTree.Work.Id,
                workTitle = workTree.Work.Title,
                unitIds = jobImportedUnitIds.Distinct().ToArray(),
                paths = jobImportedPaths,
                verdict = jobLastVerdict,
            }, ct);
        }

        return lastFileOutcome! with { Message = ledgerMsg };
    }

    public async Task<IReadOnlyList<ProcessedDownloadItemOutcome>> ListRecentImportsAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<LedgerRow>(
            """
            SELECT p.*, COALESCE(c.name, 'Download Client') AS client_name
            FROM processed_download_imports p
            LEFT JOIN download_clients c ON c.id = p.download_client_id
            ORDER BY p.id DESC
            LIMIT @Lim;
            """,
            new { Lim = Math.Clamp(limit, 1, 200) });

        return rows.Select(r => new ProcessedDownloadItemOutcome(
            r.DownloadClientId,
            r.ClientName,
            r.ExternalDownloadId,
            r.Title,
            "auto",
            r.Verdict,
            null,
            r.DestinationPath,
            r.Hardlinked != 0,
            null,
            r.Message)).ToList();
    }

    private ParsedReleaseInfo AggregateJobReleaseInfo(ActiveDownloadQueueItem item, DownloadGrabHistoryEntry? grabHistory)
    {
        var effectiveHint = grabHistory is not null && !string.IsNullOrWhiteSpace(grabHistory.MediaVertical) ? grabHistory.MediaVertical : item.MediaVertical;
        var fromClient = parserRouter.Parse(item.Title, effectiveHint);
        if (string.Equals(grabHistory?.MediaVertical, "anime", StringComparison.OrdinalIgnoreCase) && fromClient.MediaVertical == "tv")
        {
            fromClient = fromClient with { MediaVertical = "anime" };
        }

        ParsedReleaseInfo? fromFolder = null;
        if (!string.IsNullOrWhiteSpace(item.OutputPath) && Directory.Exists(item.OutputPath))
        {
            var folderName = Path.GetFileName(item.OutputPath.TrimEnd('/', '\\'));
            if (!string.IsNullOrWhiteSpace(folderName) &&
                !folderName.Equals("tv", StringComparison.OrdinalIgnoreCase) &&
                !folderName.Equals("movies", StringComparison.OrdinalIgnoreCase) &&
                !folderName.Equals("complete", StringComparison.OrdinalIgnoreCase))
            {
                fromFolder = parserRouter.Parse(folderName, fromClient.MediaVertical);
            }
        }

        ParsedReleaseInfo? fromGrab = grabHistory is not null
            ? parserRouter.Parse(grabHistory.ReleaseTitle, grabHistory.MediaVertical)
            : null;

        return fromClient with
        {
            SeasonNumber = fromClient.SeasonNumber ?? fromFolder?.SeasonNumber ?? fromGrab?.SeasonNumber,
            EpisodeNumbers = fromClient.EpisodeNumbers.Count > 0
                ? fromClient.EpisodeNumbers
                : fromFolder?.EpisodeNumbers is { Count: > 0 } fEps
                    ? fEps
                    : fromGrab?.EpisodeNumbers ?? [],
            AbsoluteEpisodeNumbers = fromClient.AbsoluteEpisodeNumbers.Count > 0
                ? fromClient.AbsoluteEpisodeNumbers
                : fromFolder?.AbsoluteEpisodeNumbers is { Count: > 0 } fAbs
                    ? fAbs
                    : fromGrab?.AbsoluteEpisodeNumbers ?? [],
            Source = fromClient.Source ?? fromFolder?.Source ?? fromGrab?.Source,
            Resolution = fromClient.Resolution ?? fromFolder?.Resolution ?? fromGrab?.Resolution,
            VideoCodec = fromClient.VideoCodec ?? fromFolder?.VideoCodec ?? fromGrab?.VideoCodec,
            HdrFormat = fromClient.HdrFormat ?? fromFolder?.HdrFormat ?? fromGrab?.HdrFormat,
            AudioCodec = fromClient.AudioCodec ?? fromFolder?.AudioCodec ?? fromGrab?.AudioCodec,
            DetectedLanguages = fromClient.DetectedLanguages.Count > 0
                ? fromClient.DetectedLanguages
                : fromFolder?.DetectedLanguages is { Count: > 0 } fLangs
                    ? fLangs
                    : fromGrab?.DetectedLanguages ?? [],
            IsDualAudio = fromClient.IsDualAudio || (fromFolder?.IsDualAudio ?? false) || (fromGrab?.IsDualAudio ?? false),
            IsMultiSubs = fromClient.IsMultiSubs || (fromFolder?.IsMultiSubs ?? false) || (fromGrab?.IsMultiSubs ?? false),
        };
    }

    private ParsedReleaseInfo AggregateSingleFileReleaseInfo(
        ParsedReleaseInfo jobParsed,
        string mediaFilePath,
        MediaWorkDetailTree workTree,
        ManualOverrideImportRequest? manualOverride)
    {
        var fileParsed = jobParsed with
        {
            CleanTitle = workTree.Work.Title,
            Year = workTree.Work.Year ?? jobParsed.Year,
            MediaVertical = workTree.Work.WorkKind,
        };

        if (workTree.Work.WorkKind is "tv" or "anime" || jobParsed.MediaVertical is "tv" or "anime")
        {
            var fileStem = Path.GetFileNameWithoutExtension(mediaFilePath);
            var fromFileName = parserRouter.Parse(fileStem, workTree.Work.WorkKind);
            int? resolvedSeason;
            IReadOnlyList<int> resolvedEps;
            if (manualOverride?.EpisodeNumbers is { Count: > 0 } ovEps)
            {
                resolvedSeason = manualOverride.SeasonNumber ?? fromFileName.SeasonNumber ?? jobParsed.SeasonNumber;
                resolvedEps = ovEps;
            }
            else if (fromFileName.SeasonNumber.HasValue && fromFileName.EpisodeNumbers.Count > 0)
            {
                resolvedSeason = manualOverride?.SeasonNumber ?? fromFileName.SeasonNumber;
                resolvedEps = fromFileName.EpisodeNumbers;
            }
            else if (jobParsed.EpisodeNumbers.Count > 0)
            {
                resolvedSeason = manualOverride?.SeasonNumber ?? jobParsed.SeasonNumber;
                resolvedEps = jobParsed.EpisodeNumbers;
            }
            else
            {
                resolvedSeason = manualOverride?.SeasonNumber ?? fromFileName.SeasonNumber ?? jobParsed.SeasonNumber;
                resolvedEps = fromFileName.EpisodeNumbers;
            }
            var resolvedAbs = fromFileName.AbsoluteEpisodeNumbers.Count > 0
                ? fromFileName.AbsoluteEpisodeNumbers
                : jobParsed.AbsoluteEpisodeNumbers;

            if (!resolvedSeason.HasValue && TryExtractSpecialEpisodeNumber(fileStem, jobParsed.RawTitle, resolvedAbs, out var specialEp))
            {
                resolvedSeason = 0;
                resolvedEps = [specialEp];
                resolvedAbs = [];
            }

            if (workTree.Work.WorkKind is "tv" or "anime" && !resolvedSeason.HasValue)
            {
                var knownTitles = GetKnownWorkTitles(workTree.Work.Title, workTree.Work.SortTitle, workTree.Work.AltTitles);

                if ((ReleaseSearchOrchestrator.TryInferSequelSeason(fromFileName, knownTitles, out var sequelSeason) ||
                     ReleaseSearchOrchestrator.TryInferSequelSeason(jobParsed, knownTitles, out sequelSeason)) &&
                    workTree.Containers.Any(c => c.ContainerNumber == sequelSeason))
                {
                    resolvedSeason = sequelSeason;
                    if (resolvedEps.Count == 0 && resolvedAbs.Count > 0)
                    {
                        resolvedEps = resolvedAbs;
                        resolvedAbs = [];
                    }
                }
            }

            fileParsed = fileParsed with
            {
                SeasonNumber = resolvedSeason,
                EpisodeNumbers = resolvedEps,
                AbsoluteEpisodeNumbers = resolvedAbs,
                IsFullSeasonPack = false,
                Source = fromFileName.Source ?? jobParsed.Source,
                Resolution = fromFileName.Resolution ?? jobParsed.Resolution,
                VideoCodec = fromFileName.VideoCodec ?? jobParsed.VideoCodec,
                HdrFormat = fromFileName.HdrFormat ?? jobParsed.HdrFormat,
                AudioCodec = fromFileName.AudioCodec ?? jobParsed.AudioCodec,
                DetectedLanguages = fromFileName.DetectedLanguages.Count > 0 ? fromFileName.DetectedLanguages : jobParsed.DetectedLanguages,
                IsDualAudio = fromFileName.IsDualAudio || jobParsed.IsDualAudio,
                IsMultiSubs = fromFileName.IsMultiSubs || jobParsed.IsMultiSubs,
            };
        }

        return fileParsed;
    }

}
