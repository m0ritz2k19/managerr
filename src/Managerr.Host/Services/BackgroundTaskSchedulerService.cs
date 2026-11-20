using System.Collections.Concurrent;
using System.Diagnostics;
using Dapper;
using Managerr.Catalog;
using Managerr.Cleanerr;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Downloaders;
using Managerr.Host.Hubs;
using Managerr.Indexers;
using Managerr.Security.Rbac;
using Managerr.Verification;

namespace Managerr.Host.Services;

public static class ScheduledTaskKeys
{
    public const string RefreshMonitoredDownloads = "RefreshMonitoredDownloads";
    public const string RssSync = "RssSync";
    public const string QueueHealthCheck = "QueueHealthCheck";
    public const string HealthCheck = "HealthCheck";
    public const string RefreshCatalogMetadata = "RefreshCatalogMetadata";
    public const string DiskScanLibraries = "DiskScanLibraries";
    public const string ImportListSync = "ImportListSync";
    public const string SqliteBackup = "SqliteBackup";
    public const string RetentionAndVacuum = "RetentionAndVacuum";
    public const string CleanerrEvaluation = "CleanerrEvaluation";
}

public sealed record ScheduledTaskSummary(
    string TaskKey,
    string Name,
    string Description,
    int IntervalSeconds,
    bool IsRunning,
    bool IsEnabled,
    string? LastExecutionAt,
    long? LastDurationMs,
    string? LastStatus,
    string? LastMessage,
    string? NextExecutionAt);

public sealed record SystemHealthCheckItem(
    string Source,
    string Severity, // "ok" | "warning" | "error"
    string Message,
    string? WikiOrActionHint = null,
    string? ActionUrl = null,
    string? ResolveAction = null);

public interface ITaskManagerService
{
    Task EnsureTasksSeededAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledTaskSummary>> ListTasksAsync(CancellationToken ct = default);
    Task<ScheduledTaskSummary> RunTaskNowAsync(string taskKey, CancellationToken ct = default);
    Task<ScheduledTaskSummary> UpdateTaskScheduleAsync(string taskKey, int? intervalSeconds, bool? isEnabled, CancellationToken ct = default) =>
        throw new NotSupportedException("Updating task schedules is not supported by this implementation.");
    Task<IReadOnlyList<SystemHealthCheckItem>> GetSystemHealthChecksAsync(CancellationToken ct = default);
    Task<HealthIssueResolutionReportDto> ResolveHealthIssueAsync(string actionKey, CancellationToken ct = default);
    Task<SystemRetentionPolicyDto> GetRetentionPolicyAsync(CancellationToken ct = default);
    Task<SystemRetentionPolicyDto> UpdateRetentionPolicyAsync(UpdateSystemRetentionPolicyRequest request, CancellationToken ct = default);
    Task<SystemRetentionRunReportDto> RunRetentionCleanupAsync(bool forceVacuum = false, CancellationToken ct = default);
}

/// <summary>
/// Persistent Sonarr TaskManager equivalent + BackgroundService loop coordinating automated CDH download monitoring,
/// reactive post-grab checks, RSS Sync -> Grab, Queue Health Sweep, Metadata Refresh, Disk Scan, Import Lists, and SQLite Backups.
/// </summary>
public sealed class BackgroundTaskSchedulerService : BackgroundService, ITaskManagerService
{
    private readonly ISqliteDbFactory _dbFactory;
    private readonly ICompletedDownloadImportService _importService;
    private readonly IAutomatedRssSyncService _rssSyncService;
    private readonly IUnifiedDownloadOrchestrator _downloadOrchestrator;
    private readonly IUnifiedIndexerService _indexerService;
    private readonly IPolymorphicCatalogService _catalogService;
    private readonly ILanguageVerificationService _verificationService;
    private readonly ISqliteBackupService _backupService;
    private readonly IRealtimeBroadcaster _realtime;
    private readonly ILogger<BackgroundTaskSchedulerService> _logger;
    private readonly IMediaManagementAndDiskScanService? _diskScanService;
    private readonly IImportListSyncService? _importListSyncService;
    private readonly Managerr.Common.Notifications.IMediaLifecycleNotificationSink? _notificationSink;
    private readonly Managerr.Datastore.Services.IHybridSqliteCache? _cache;
    private readonly IWantedAndActivityService? _wantedService;
    private readonly ICleanerrLifecycleEngine? _cleanerrEngine;
    private readonly ICleanerrDiscordService? _cleanerrDiscord;
    private readonly IAppSettingsService? _appSettings;
    private readonly SystemRetentionAndHealthService _retentionAndHealth;

    private readonly int _initialDelayMs;
    private readonly int _tickIntervalMs;
    private readonly int _postGrabDebounceMs;
    private readonly bool _schedulerEnabled;
    private readonly IReadOnlyList<TaskDefinitionSeed> _taskSeeds;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _taskGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _runningFlags = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _debounceLock = new();
    private CancellationTokenSource? _postGrabDebounceCts;
    private CancellationToken _stoppingToken;
    private bool _disposed;

    public BackgroundTaskSchedulerService(
        ISqliteDbFactory dbFactory,
        ICompletedDownloadImportService importService,
        IAutomatedRssSyncService rssSyncService,
        IUnifiedDownloadOrchestrator downloadOrchestrator,
        IUnifiedIndexerService indexerService,
        IPolymorphicCatalogService catalogService,
        ILanguageVerificationService verificationService,
        ISqliteBackupService backupService,
        IRealtimeBroadcaster realtime,
        IConfiguration configuration,
        ILogger<BackgroundTaskSchedulerService> logger,
        IMediaManagementAndDiskScanService? diskScanService = null,
        IImportListSyncService? importListSyncService = null,
        Managerr.Common.Notifications.IMediaLifecycleNotificationSink? notificationSink = null,
        Managerr.Datastore.Services.IHybridSqliteCache? cache = null,
        IWantedAndActivityService? wantedService = null,
        IAppSettingsService? appSettings = null,
        Managerr.Common.Paths.IAppPaths? appPaths = null,
        Managerr.Downloaders.Parser.IReleaseParserRouter? parserRouter = null,
        ICleanerrLifecycleEngine? cleanerrEngine = null,
        ICleanerrDiscordService? cleanerrDiscord = null)
    {
        _dbFactory = dbFactory;
        _importService = importService;
        _rssSyncService = rssSyncService;
        _downloadOrchestrator = downloadOrchestrator;
        _indexerService = indexerService;
        _catalogService = catalogService;
        _verificationService = verificationService;
        _backupService = backupService;
        _realtime = realtime;
        _logger = logger;
        _diskScanService = diskScanService;
        _importListSyncService = importListSyncService;
        _notificationSink = notificationSink;
        _cache = cache;
        _wantedService = wantedService;
        _cleanerrEngine = cleanerrEngine;
        _cleanerrDiscord = cleanerrDiscord;
        _appSettings = appSettings;
        _retentionAndHealth = new SystemRetentionAndHealthService(dbFactory, appSettings, appPaths, parserRouter, logger);

        _schedulerEnabled = configuration.GetValue("Managerr:SchedulerEnabled", true);
        _initialDelayMs = Math.Max(0, configuration.GetValue("Managerr:SchedulerInitialDelayMs", 12_000));
        _tickIntervalMs = Math.Max(50, configuration.GetValue("Managerr:SchedulerTickMs", 2_000));
        _postGrabDebounceMs = Math.Max(50, configuration.GetValue("Managerr:PostGrabDebounceMs", 5_000));

        var hasDownloadMonitorOverride = !string.IsNullOrWhiteSpace(configuration["Managerr:DownloadMonitorIntervalSeconds"]);
        var hasRssSyncOverride = !string.IsNullOrWhiteSpace(configuration["Managerr:RssSyncIntervalSeconds"]);
        var hasQueueSweepOverride = !string.IsNullOrWhiteSpace(configuration["Managerr:QueueSweepIntervalSeconds"]);
        var hasMetadataRefreshOverride = !string.IsNullOrWhiteSpace(configuration["Managerr:MetadataRefreshIntervalSeconds"]);

        var downloadMonitorSec = Math.Max(5, configuration.GetValue("Managerr:DownloadMonitorIntervalSeconds", 60));
        var rssSyncSec = Math.Max(30, configuration.GetValue("Managerr:RssSyncIntervalSeconds", 900));
        var queueSweepSec = Math.Max(30, configuration.GetValue("Managerr:QueueSweepIntervalSeconds", 300));
        var metadataRefreshSec = Math.Max(3600, configuration.GetValue("Managerr:MetadataRefreshIntervalSeconds", 86400));

        _taskSeeds =
        [
            new(
                ScheduledTaskKeys.RefreshMonitoredDownloads,
                "Refresh Monitored Downloads",
                "Polls active download clients, updates TrackedDownload CDH state, imports completed downloads, and auto-repulls failed releases.",
                downloadMonitorSec,
                hasDownloadMonitorOverride),
            new(
                ScheduledTaskKeys.RssSync,
                "RSS Sync & Auto-Grab",
                "Polls RSS feeds across enabled indexers, evaluates Composable Profiles on wanted items, and grabs approved releases.",
                rssSyncSec,
                hasRssSyncOverride),
            new(
                ScheduledTaskKeys.QueueHealthCheck,
                "Queue Health & Strike Sweep",
                "Evaluates stalled, failed, or unsafe downloads, applies strikes, blocklists recurring bad releases, and auto-repulls replacements.",
                queueSweepSec,
                hasQueueSweepOverride),
            new(
                ScheduledTaskKeys.HealthCheck,
                "System Health Check",
                "Audits indexers, download clients, library root folders, disk space, and quarantine status.",
                600),
            new(
                ScheduledTaskKeys.DiskScanLibraries,
                "Disk Scan & Unmapped Files",
                "Scans library root folders on disk to auto-adopt existing files, prune deleted files, and index unmapped files.",
                21600),
            new(
                ScheduledTaskKeys.ImportListSync,
                "Import List Sync",
                "Polls enabled Import Lists (TMDB, StevenLu, AniList, Trakt, OpenLibrary, Custom JSON) and adds non-excluded works.",
                21600),
            new(
                ScheduledTaskKeys.RefreshCatalogMetadata,
                "Nightly Metadata Refresh & Cache Sync",
                "Polls upstream metadata once every 24h for continuing monitored works with polite pacing, refreshes local SQLite cache.db, and prunes expired entries.",
                metadataRefreshSec,
                hasMetadataRefreshOverride),
            new(
                ScheduledTaskKeys.SqliteBackup,
                "SQLite WAL Hot Backup",
                "Creates a consistent online backup archive of Core, Cache, and Telemetry SQLite databases.",
                86400),
            new(
                ScheduledTaskKeys.RetentionAndVacuum,
                "Database Retention & SQLite VACUUM",
                "Prunes aged verification reports, media history, audit/job logs, and reviewed quarantine audio snippets, then runs SQLite VACUUM + WAL truncate checkpoint when threshold is met.",
                86400),
            new(
                ScheduledTaskKeys.CleanerrEvaluation,
                "Cleanerr Lifecycle Evaluation",
                "Syncs catalog media into Cleanerr, evaluates scheduled lifecycle workflows, processes grace expirations, auto-rescues, and sends final warnings.",
                900),
        ];

        _downloadOrchestrator.DownloadMonitorTriggerRequested += OnDownloadMonitorTriggerRequested;
    }

    public override void Dispose()
    {
        _downloadOrchestrator.DownloadMonitorTriggerRequested -= OnDownloadMonitorTriggerRequested;
        lock (_debounceLock)
        {
            _disposed = true;
            try { _postGrabDebounceCts?.Cancel(); } catch (ObjectDisposedException) { }
            try { _postGrabDebounceCts?.Dispose(); } catch { }
            _postGrabDebounceCts = null;
        }

        base.Dispose();
    }

    public async Task EnsureTasksSeededAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var nowIso = now.ToString("O");

        await _dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                // Drop rows of tasks this build no longer ships (e.g. the split-out Cleanerr lifecycle task),
                // so they neither show up in the task list nor tick as no-ops.
                await conn.ExecuteAsync(
                    "DELETE FROM scheduled_tasks WHERE task_key NOT IN @Keys;",
                    new { Keys = _taskSeeds.Select(t => t.TaskKey).ToArray() },
                    tx);

                foreach (var seed in _taskSeeds)
                {
                    // Stagger initial scheduled runs: RefreshMonitoredDownloads runs immediately on first scheduler tick;
                    // heavier tasks schedule one interval ahead unless manually triggered.
                    var initialNext = seed.TaskKey == ScheduledTaskKeys.RefreshMonitoredDownloads
                        ? nowIso
                        : now.AddSeconds(seed.IntervalSeconds).ToString("O");

                    var sql = seed.HasExplicitIntervalOverride
                        ? """
                          INSERT INTO scheduled_tasks (
                              task_key, name, description, interval_seconds, is_enabled,
                              last_status, next_execution_at, updated_at
                          )
                          VALUES (
                              @TaskKey, @Name, @Description, @IntervalSeconds, 1,
                              'idle', @NextExecutionAt, @UpdatedAt
                          )
                          ON CONFLICT(task_key) DO UPDATE SET
                              name = excluded.name,
                              description = excluded.description,
                              interval_seconds = excluded.interval_seconds,
                              next_execution_at = COALESCE(scheduled_tasks.next_execution_at, excluded.next_execution_at);
                          """
                        : """
                          INSERT INTO scheduled_tasks (
                              task_key, name, description, interval_seconds, is_enabled,
                              last_status, next_execution_at, updated_at
                          )
                          VALUES (
                              @TaskKey, @Name, @Description, @IntervalSeconds, 1,
                              'idle', @NextExecutionAt, @UpdatedAt
                          )
                          ON CONFLICT(task_key) DO UPDATE SET
                              name = excluded.name,
                              description = excluded.description,
                              next_execution_at = COALESCE(scheduled_tasks.next_execution_at, excluded.next_execution_at);
                          """;

                    await conn.ExecuteAsync(
                        sql,
                        new
                        {
                            seed.TaskKey,
                            seed.Name,
                            seed.Description,
                            seed.IntervalSeconds,
                            NextExecutionAt = initialNext,
                            UpdatedAt = nowIso,
                        },
                        tx);
                }
            },
            ct);
    }

    public async Task<IReadOnlyList<ScheduledTaskSummary>> ListTasksAsync(CancellationToken ct = default)
    {
        await EnsureTasksSeededAsync(ct);

        await using var conn = await _dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = (await conn.QueryAsync<ScheduledTaskRow>(
            """
            SELECT task_key AS TaskKey,
                   name AS Name,
                   description AS Description,
                   interval_seconds AS IntervalSeconds,
                   is_enabled AS IsEnabled,
                   last_execution_at AS LastExecutionAt,
                   last_duration_ms AS LastDurationMs,
                   last_status AS LastStatus,
                   last_message AS LastMessage,
                   next_execution_at AS NextExecutionAt
            FROM scheduled_tasks
            ORDER BY interval_seconds ASC, task_key ASC;
            """)).ToList();

        return rows.Select(MapSummary).ToList();
    }

    public async Task<ScheduledTaskSummary> RunTaskNowAsync(string taskKey, CancellationToken ct = default)
    {
        await EnsureTasksSeededAsync(ct);

        var canonical = _taskSeeds
            .FirstOrDefault(s => s.TaskKey.Equals(taskKey, StringComparison.OrdinalIgnoreCase))
            ?.TaskKey;

        if (canonical is null)
        {
            throw new KeyNotFoundException($"Unknown scheduled task key '{taskKey}'.");
        }

        await ExecuteTaskInternalAsync(canonical, isManualTrigger: true, ct);

        var tasks = await ListTasksAsync(ct);
        return tasks.First(t => t.TaskKey.Equals(canonical, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ScheduledTaskSummary> UpdateTaskScheduleAsync(
        string taskKey,
        int? intervalSeconds,
        bool? isEnabled,
        CancellationToken ct = default)
    {
        await EnsureTasksSeededAsync(ct);

        var canonical = _taskSeeds
            .FirstOrDefault(s => s.TaskKey.Equals(taskKey, StringComparison.OrdinalIgnoreCase))
            ?.TaskKey;

        if (canonical is null)
        {
            throw new KeyNotFoundException($"Unknown scheduled task key '{taskKey}'.");
        }

        var now = DateTimeOffset.UtcNow;
        var nowIso = now.ToString("O");

        await _dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                var existing = await conn.QuerySingleOrDefaultAsync<ScheduledTaskRow>(
                    """
                    SELECT task_key AS TaskKey,
                           name AS Name,
                           description AS Description,
                           interval_seconds AS IntervalSeconds,
                           is_enabled AS IsEnabled,
                           last_execution_at AS LastExecutionAt,
                           last_duration_ms AS LastDurationMs,
                           last_status AS LastStatus,
                           last_message AS LastMessage,
                           next_execution_at AS NextExecutionAt
                    FROM scheduled_tasks
                    WHERE task_key = @TaskKey;
                    """,
                    new { TaskKey = canonical },
                    tx) ?? throw new KeyNotFoundException($"Scheduled task '{canonical}' not found.");

                var nextInterval = intervalSeconds.HasValue
                    ? Math.Clamp(intervalSeconds.Value, 10, 604_800)
                    : existing.IntervalSeconds;
                var wasEnabled = existing.IsEnabled != 0;
                var nextEnabled = isEnabled ?? wasEnabled;

                var intervalChanged = intervalSeconds.HasValue && nextInterval != existing.IntervalSeconds;
                var reEnabled = !wasEnabled && nextEnabled;
                var nextExecutionAt = (intervalChanged || reEnabled)
                    ? now.AddSeconds(nextInterval).ToString("O")
                    : existing.NextExecutionAt;

                await conn.ExecuteAsync(
                    """
                    UPDATE scheduled_tasks
                    SET interval_seconds = @IntervalSeconds,
                        is_enabled = @IsEnabled,
                        next_execution_at = @NextExecutionAt,
                        updated_at = @UpdatedAt
                    WHERE task_key = @TaskKey;
                    """,
                    new
                    {
                        TaskKey = canonical,
                        IntervalSeconds = nextInterval,
                        IsEnabled = nextEnabled ? 1 : 0,
                        NextExecutionAt = nextExecutionAt,
                        UpdatedAt = nowIso,
                    },
                    tx);
            },
            ct);

        var tasks = await ListTasksAsync(ct);
        return tasks.First(t => t.TaskKey.Equals(canonical, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<SystemHealthCheckItem>> GetSystemHealthChecksAsync(CancellationToken ct = default)
    {
        var items = new List<SystemHealthCheckItem>();

        var libraries = await _catalogService.ListLibrariesAsync(null, ct);
        if (libraries.Count == 0)
        {
            items.Add(new SystemHealthCheckItem(
                Source: "Catalog",
                Severity: "warning",
                Message: "No media libraries (root folders) are configured yet.",
                WikiOrActionHint: "Open Catalog -> Add Library to configure at least one root folder."));
        }
        else
        {
            foreach (var lib in libraries)
            {
                if (!Directory.Exists(lib.RootFolderPath))
                {
                    items.Add(new SystemHealthCheckItem(
                        Source: $"RootFolder ({lib.Name})",
                        Severity: "error",
                        Message: $"Root folder '{lib.RootFolderPath}' does not exist or is not mounted.",
                        WikiOrActionHint: "Verify container volume mounts and folder permissions."));
                    continue;
                }

                try
                {
                    var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(lib.RootFolderPath)) ?? "/");
                    if (drive.IsReady && drive.AvailableFreeSpace < 1_073_741_824L) // < 1 GiB
                    {
                        var freeMb = drive.AvailableFreeSpace / (1024 * 1024);
                        items.Add(new SystemHealthCheckItem(
                            Source: $"DiskSpace ({lib.Name})",
                            Severity: "warning",
                            Message: $"Low free disk space on '{lib.RootFolderPath}' ({freeMb} MiB remaining).",
                            WikiOrActionHint: "Free up space on the library volume or expand storage capacity."));
                    }
                }
                catch
                {
                    // Ignore virtual / non-standard mount points where DriveInfo is unavailable
                }
            }
        }

        var indexers = await _indexerService.ListIndexersAsync(ct);
        if (indexers.Count == 0)
        {
            items.Add(new SystemHealthCheckItem(
                Source: "Indexers",
                Severity: "warning",
                Message: "No indexers are configured.",
                WikiOrActionHint: "Add a Torznab, Newznab, or Cardigann indexer in Acquisition -> Indexers."));
        }
        else
        {
            var unhealthy = indexers.Where(i => !i.IsHealthy).ToList();
            if (unhealthy.Count == indexers.Count)
            {
                items.Add(new SystemHealthCheckItem(
                    Source: "Indexers",
                    Severity: "error",
                    Message: $"All {indexers.Count} configured indexer(s) are currently in failure backoff ({string.Join(", ", unhealthy.Select(u => u.Name))}).",
                    WikiOrActionHint: "Test indexer connectivity and API keys in Acquisition -> Indexers."));
            }
            else if (unhealthy.Count > 0)
            {
                items.Add(new SystemHealthCheckItem(
                    Source: "Indexers",
                    Severity: "warning",
                    Message: $"{unhealthy.Count} indexer(s) degraded: {string.Join(", ", unhealthy.Select(u => $"{u.Name} ({u.LastError ?? "backoff"})"))}.",
                    WikiOrActionHint: "Inspect degraded indexers in Acquisition -> Indexers."));
            }
        }

        var clients = await _downloadOrchestrator.ListClientsAsync(ct);
        var enabledClients = clients.Where(c => c.IsEnabled).ToList();
        if (enabledClients.Count == 0)
        {
            items.Add(new SystemHealthCheckItem(
                Source: "DownloadClients",
                Severity: "warning",
                Message: "No enabled download clients are configured.",
                WikiOrActionHint: "Configure SABnzbd, qBittorrent, or another client in Acquisition -> Downloaders."));
        }

        var tracked = await _importService.ListTrackedDownloadsAsync(100, ct);
        var blocked = tracked.Where(t => t.State == TrackedDownloadStates.ImportBlocked).ToList();
        if (blocked.Count > 0)
        {
            items.Add(new SystemHealthCheckItem(
                Source: "CompletedDownloadHandling",
                Severity: "warning",
                Message: $"{blocked.Count} download(s) are in ImportBlocked state ({string.Join(", ", blocked.Take(3).Select(b => b.Title))}).",
                WikiOrActionHint: "Review status warnings or use Retry / Manual Import in Acquisition -> Downloaders."));
        }

        // Active downloads refresh updated_at on every poll; a long gap means the client has not reported them
        var staleCutoff = DateTimeOffset.UtcNow.AddHours(-6);
        var stale = tracked
            .Where(t => t.State is TrackedDownloadStates.Downloading or TrackedDownloadStates.ImportPending)
            .Where(t => DateTimeOffset.TryParse(t.UpdatedAt, out var lastSeen) && lastSeen < staleCutoff)
            .ToList();
        if (stale.Count > 0)
        {
            items.Add(new SystemHealthCheckItem(
                Source: "CompletedDownloadHandling",
                Severity: "warning",
                Message: $"{stale.Count} active download(s) have not been reported by their download client for over 6 hours ({string.Join(", ", stale.Take(3).Select(s => s.Title))}).",
                WikiOrActionHint: "Check that the download client is reachable and enabled in Acquisition -> Downloaders."));
        }

        var libraryChecks = await _retentionAndHealth.EvaluateLibraryHealthChecksAsync(ct);
        items.AddRange(libraryChecks);

        var activeQuarantine = await _verificationService.ListQuarantineAsync("quarantined", ct);
        if (activeQuarantine.Count > 0)
        {
            items.Add(new SystemHealthCheckItem(
                Source: "LanguageVerification",
                Severity: "warning",
                Message: $"{activeQuarantine.Count} media file(s) are awaiting review in Language Verification Quarantine.",
                WikiOrActionHint: "Approve or reject quarantined files in Profiles & Verification -> Quarantine."));
        }

        if (_cleanerrDiscord is not null)
        {
            var discordSettings = await _cleanerrDiscord.GetSettingsAsync(ct);
            if (discordSettings.HasBotToken &&
                !discordSettings.GatewayPassiveShadowMode &&
                !_cleanerrDiscord.IsGatewayConnected)
            {
                var errSuffix = string.IsNullOrWhiteSpace(_cleanerrDiscord.GatewayLastError)
                    ? string.Empty
                    : $" ({_cleanerrDiscord.GatewayLastError})";
                items.Add(new SystemHealthCheckItem(
                    Source: "CleanerrDiscord",
                    Severity: "warning",
                    Message: $"Cleanerr Discord Bot token is configured but the Gateway is disconnected{errSuffix}.",
                    WikiOrActionHint: "Verify the Discord Bot token and Gateway intents in Cleanerr -> Discord & Settings."));
            }
        }

        if (_cleanerrEngine is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var staged = await _cleanerrEngine.ListDeletionQueueAsync("staged", ct);
            var overdueCount = staged.Count(q =>
                DateTimeOffset.TryParse(q.DeleteAt, out var deleteAt) && deleteAt < now);
            if (overdueCount > 0)
            {
                items.Add(new SystemHealthCheckItem(
                    Source: "CleanerrQueue",
                    Severity: "warning",
                    Message: $"{overdueCount} staged Cleanerr item(s) have passed their scheduled delete_at timestamp and await queue processing.",
                    WikiOrActionHint: "Run Cleanerr Evaluation or process the Leaving Soon queue in Cleanerr -> Leaving Soon."));
            }
        }

        if (items.Count == 0)
        {
            items.Add(new SystemHealthCheckItem(
                Source: "System",
                Severity: "ok",
                Message: "All indexers, download clients, root folders, and background pipelines are healthy.",
                WikiOrActionHint: null));
        }

        return items;
    }

    public Task<HealthIssueResolutionReportDto> ResolveHealthIssueAsync(string actionKey, CancellationToken ct = default) =>
        _retentionAndHealth.ResolveHealthIssueAsync(actionKey, ct);

    public Task<SystemRetentionPolicyDto> GetRetentionPolicyAsync(CancellationToken ct = default) =>
        _retentionAndHealth.GetRetentionPolicyAsync(ct);

    public Task<SystemRetentionPolicyDto> UpdateRetentionPolicyAsync(UpdateSystemRetentionPolicyRequest request, CancellationToken ct = default) =>
        _retentionAndHealth.UpdateRetentionPolicyAsync(request, ct);

    public Task<SystemRetentionRunReportDto> RunRetentionCleanupAsync(bool forceVacuum = false, CancellationToken ct = default) =>
        _retentionAndHealth.ExecuteRetentionCleanupAsync(forceVacuum, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        try
        {
            await EnsureTasksSeededAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Initial scheduled_tasks seed failed on startup; will retry on tick.");
        }

        if (!_schedulerEnabled)
        {
            return;
        }

        if (_initialDelayMs > 0)
        {
            try
            {
                await Task.Delay(_initialDelayMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dueTasks = await GetDueTaskKeysAsync(stoppingToken);
                foreach (var taskKey in dueTasks)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await ExecuteTaskInternalAsync(taskKey, isManualTrigger: false, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in BackgroundTaskSchedulerService loop.");
            }

            try
            {
                await Task.Delay(_tickIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void OnDownloadMonitorTriggerRequested()
    {
        if (!_schedulerEnabled || _stoppingToken.IsCancellationRequested)
        {
            return;
        }

        CancellationTokenSource cts;
        CancellationToken debounceToken;
        lock (_debounceLock)
        {
            if (_disposed || _stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try { _postGrabDebounceCts?.Cancel(); } catch (ObjectDisposedException) { }
            try
            {
                cts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _postGrabDebounceCts = cts;
            debounceToken = cts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_postGrabDebounceMs, debounceToken);
                await ExecuteTaskInternalAsync(ScheduledTaskKeys.RefreshMonitoredDownloads, isManualTrigger: false, _stoppingToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // Debounced or shutting down
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reactive post-grab RefreshMonitoredDownloads trigger failed.");
            }
            finally
            {
                lock (_debounceLock)
                {
                    if (ReferenceEquals(_postGrabDebounceCts, cts))
                    {
                        _postGrabDebounceCts = null;
                    }
                }
                try { cts.Dispose(); } catch { }
            }
        });
    }

    private async Task<IReadOnlyList<string>> GetDueTaskKeysAsync(CancellationToken ct)
    {
        await EnsureTasksSeededAsync(ct);
        var nowIso = DateTimeOffset.UtcNow.ToString("O");

        await using var conn = await _dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        return (await conn.QueryAsync<string>(
            """
            SELECT task_key
            FROM scheduled_tasks
            WHERE is_enabled = 1
              AND (next_execution_at IS NULL OR next_execution_at <= @NowIso)
            ORDER BY interval_seconds ASC;
            """,
            new { NowIso = nowIso })).ToList();
    }

    private async Task ExecuteTaskInternalAsync(string taskKey, bool isManualTrigger, CancellationToken ct)
    {
        var gate = _taskGates.GetOrAdd(taskKey, _ => new SemaphoreSlim(1, 1));
        if (!isManualTrigger)
        {
            if (!await gate.WaitAsync(0, ct))
            {
                return;
            }
        }
        else
        {
            await gate.WaitAsync(ct);
        }

        var sw = Stopwatch.StartNew();
        var startedAtIso = DateTimeOffset.UtcNow.ToString("O");
        _runningFlags[taskKey] = true;

        try
        {
            await UpdateTaskStateAsync(taskKey, startedAtIso, null, "running", "Executing...", nextExecutionIso: null, ct);
            await BroadcastTaskSafeAsync(taskKey, "running", "Executing...", ct);

            var summaryMessage = await DispatchTaskWorkloadAsync(taskKey, ct);
            sw.Stop();

            var intervalSec = await GetTaskIntervalSecondsAsync(taskKey, ct);
            if (string.Equals(taskKey, ScheduledTaskKeys.RefreshMonitoredDownloads, StringComparison.OrdinalIgnoreCase))
            {
                await using var activeConn = await _dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
                var hasActiveDownloads = await activeConn.ExecuteScalarAsync<int>(
                    "SELECT EXISTS(SELECT 1 FROM tracked_downloads WHERE state IN ('Downloading', 'ImportPending'));") != 0;
                if (hasActiveDownloads)
                {
                    intervalSec = Math.Min(intervalSec, 5);
                }
            }

            var nextIso = DateTimeOffset.UtcNow.AddSeconds(intervalSec).ToString("O");

            _runningFlags[taskKey] = false;
            await UpdateTaskStateAsync(taskKey, startedAtIso, sw.ElapsedMilliseconds, "completed", summaryMessage, nextIso, ct);
            await BroadcastTaskSafeAsync(taskKey, "completed", summaryMessage, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _runningFlags[taskKey] = false;
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _runningFlags[taskKey] = false;
            _logger.LogError(ex, "Scheduled task {TaskKey} failed after {ElapsedMs}ms.", taskKey, sw.ElapsedMilliseconds);

            var intervalSec = await GetTaskIntervalSecondsAsync(taskKey, CancellationToken.None);
            var nextIso = DateTimeOffset.UtcNow.AddSeconds(intervalSec).ToString("O");
            var errMsg = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;

            await UpdateTaskStateAsync(taskKey, startedAtIso, sw.ElapsedMilliseconds, "failed", errMsg, nextIso, CancellationToken.None);
            await BroadcastTaskSafeAsync(taskKey, "failed", errMsg, CancellationToken.None);

            if (isManualTrigger)
            {
                throw;
            }
        }
        finally
        {
            _runningFlags[taskKey] = false;
            gate.Release();
        }
    }

    private async Task<string> DispatchTaskWorkloadAsync(string taskKey, CancellationToken ct)
    {
        switch (taskKey)
        {
            case ScheduledTaskKeys.RefreshMonitoredDownloads:
            {
                var rep = await _importService.ProcessCompletedDownloadsAsync(ct);
                return $"Evaluated {rep.EvaluatedCount} download(s): {rep.ImportedCount} imported, {rep.QuarantinedCount} quarantined, {rep.RejectedCount} blocked/rejected, {rep.RemovedCount} removed from client.";
            }

            case ScheduledTaskKeys.RssSync:
            {
                var rep = await _rssSyncService.ExecuteRssSyncAndGrabAsync(ct);
                return $"Polled {rep.PolledIndexers} indexer(s), {rep.TotalReleases} release(s): {rep.MatchedCandidatesCount} matched, {rep.ApprovedCount} approved, {rep.GrabbedCount} grabbed.";
            }

            case ScheduledTaskKeys.QueueHealthCheck:
            {
                var actions = await _downloadOrchestrator.EvaluateQueueHealthAndStrikesAsync(3, ct);
                if (actions.Count == 0)
                {
                    return "Queue healthy (0 strikes applied).";
                }

                var repulled = 0;
                if (_wantedService is not null)
                {
                    foreach (var action in actions.Where(a => a.AddedToRecurringBlocklist))
                    {
                        var repNote = await _wantedService.RedownloadFailedWorkOrUnitsAsync(
                            action.WorkId,
                            action.UnitIds,
                            action.Title,
                            action.MediaVertical ?? "tv",
                            ct);
                        if (repNote is not null && repNote.StartsWith("Auto-grabbed replacement", StringComparison.OrdinalIgnoreCase))
                        {
                            repulled++;
                        }
                    }
                }

                return repulled > 0
                    ? $"Applied queue actions to {actions.Count} item(s); auto-grabbed {repulled} replacement release(s)."
                    : $"Applied queue actions to {actions.Count} item(s).";
            }

            case ScheduledTaskKeys.HealthCheck:
            {
                await _verificationService.PruneOrphanedQuarantineSnippetsAsync(ct);
                var checks = await GetSystemHealthChecksAsync(ct);
                var errors = checks.Count(c => c.Severity == "error");
                var warnings = checks.Count(c => c.Severity == "warning");
                if ((errors > 0 || warnings > 0) && _notificationSink is not null)
                {
                    var topIssue = checks.First(c => c.Severity is "error" or "warning");
                    await _notificationSink.PublishAsync(
                        new Managerr.Common.Notifications.MediaLifecycleNotificationEvent(
                            EventType: Managerr.Common.Notifications.MediaNotificationEventTypes.OnHealthIssue,
                            Title: $"Managerr Health Issue ({errors} error, {warnings} warning)",
                            Message: $"[{topIssue.Source}] {topIssue.Message}"),
                        ct);
                }

                return errors == 0 && warnings == 0
                    ? "All system health checks passed."
                    : $"{errors} error(s), {warnings} warning(s) detected.";
            }

            case ScheduledTaskKeys.RefreshCatalogMetadata:
            {
                var works = await _catalogService.ListWorksAsync(null, null, null, ct);
                var continuing = works
                    .Where(w => w.ExternalIds.Count > 0 &&
                                ((w.Monitored && !w.Status.Equals("ended", StringComparison.OrdinalIgnoreCase)) ||
                                 w.AltTitles.Count == 0))
                    .Take(100)
                    .ToList();

                var refreshed = 0;
                for (var i = 0; i < continuing.Count; i++)
                {
                    var w = continuing[i];
                    try
                    {
                        await _catalogService.RefreshWorkMetadataAsync(w.Id, null, null, ct);
                        refreshed++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Skipped metadata refresh for work {WorkId} ({Title}).", w.Id, w.Title);
                    }

                    if (i + 1 < continuing.Count)
                    {
                        await Task.Delay(350, ct);
                    }
                }

                var pruned = _cache is not null ? await _cache.PurgeExpiredAsync(ct) : 0;
                return $"Refreshed metadata for {refreshed}/{continuing.Count} continuing monitored work(s); pruned {pruned} expired cache entry(ies).";
            }

            case ScheduledTaskKeys.DiskScanLibraries:
            {
                if (_diskScanService is null)
                {
                    return "Disk scan service not attached.";
                }

                var reports = await _diskScanService.ScanAllLibrariesAsync(ct);
                var adopted = reports.Sum(r => r.AutoAdoptedCount);
                var missing = reports.Sum(r => r.MissingRemovedCount);
                var unmapped = reports.Sum(r => r.UnmappedCount);
                return $"Scanned {reports.Count} library(ies): {adopted} adopted, {missing} missing pruned, {unmapped} unmapped.";
            }

            case ScheduledTaskKeys.ImportListSync:
            {
                if (_importListSyncService is null)
                {
                    return "Import list sync service not attached.";
                }

                var reports = await _importListSyncService.SyncAllEnabledListsAsync(ct);
                var added = reports.Sum(r => r.AddedCount);
                var fetched = reports.Sum(r => r.FetchedCount);
                return $"Synced {reports.Count} enabled import list(s): {fetched} fetched, {added} added.";
            }

            case ScheduledTaskKeys.SqliteBackup:
            {
                var maxRetained = 10;
                if (_appSettings is not null)
                {
                    var rawMax = await _appSettings.GetAsync("system.backups.max_retained", ct);
                    if (int.TryParse(rawMax, out var parsedMax))
                    {
                        maxRetained = Math.Clamp(parsedMax, 1, 100);
                    }
                }
                var backup = await _backupService.CreateHotBackupAsync("scheduled", maxRetainedBackups: maxRetained, ct);
                return $"Created hot backup '{backup.FileName}' ({backup.SizeBytes / 1024} KiB).";
            }

            case ScheduledTaskKeys.RetentionAndVacuum:
            {
                var rep = await _retentionAndHealth.ExecuteRetentionCleanupAsync(forceVacuum: false, ct);
                return $"Pruned {rep.DeletedVerificationJobs} verification job(s), {rep.DeletedHistoryRows} history row(s), {rep.DeletedAuditEvents} audit event(s), {rep.DeletedJobLogs} job log(s), {rep.DeletedQuarantineRows} quarantine row(s), {rep.DeletedSnippetDirs} snippet folder(s); vacuum={rep.VacuumExecuted} ({rep.BytesReclaimed} B reclaimed).";
            }

            case ScheduledTaskKeys.CleanerrEvaluation:
            {
                if (_cleanerrEngine is null)
                {
                    return "Cleanerr lifecycle engine not attached.";
                }

                await _cleanerrEngine.SyncCatalogItemsIntoCleanerrAsync(ct);
                var workflowRuns = await _cleanerrEngine.EvaluateScheduledWorkflowsAsync(ct: ct);
                var queueResult = await _cleanerrEngine.ProcessDeletionQueueAsync(ct: ct);

                try
                {
                    await _realtime.PublishScopedEventAsync(
                        PermissionScopes.CleanerrRead,
                        "cleanerr.updated",
                        new
                        {
                            action = "scheduler.cleanerr_evaluation",
                            evaluatedWorkflows = workflowRuns.Count,
                            queueResult.DeletedCount,
                            queueResult.RescuedCount,
                            timestampUtc = DateTimeOffset.UtcNow,
                        },
                        ct);
                }
                catch
                {
                    // Never fail scheduled task if SignalR broadcast fails
                }

                return $"Evaluated {workflowRuns.Count} workflow(s); queue: {queueResult.Evaluated} checked, {queueResult.RescuedCount} rescued, {queueResult.DeletedCount} deleted, {queueResult.FinalWarningsSent} final warning(s) sent.";
            }

            default:
                return "Completed.";
        }
    }

    private async Task<int> GetTaskIntervalSecondsAsync(string taskKey, CancellationToken ct)
    {
        await using var conn = await _dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var interval = await conn.QueryFirstOrDefaultAsync<int?>(
            "SELECT interval_seconds FROM scheduled_tasks WHERE task_key = @TaskKey;",
            new { TaskKey = taskKey });
        return interval is > 0 ? interval.Value : 300;
    }

    private async Task UpdateTaskStateAsync(
        string taskKey,
        string lastExecutionIso,
        long? durationMs,
        string status,
        string message,
        string? nextExecutionIso,
        CancellationToken ct)
    {
        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        await _dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE scheduled_tasks
                    SET last_execution_at = @LastExecutionAt,
                        last_duration_ms = COALESCE(@LastDurationMs, last_duration_ms),
                        last_status = @LastStatus,
                        last_message = @LastMessage,
                        next_execution_at = COALESCE(@NextExecutionAt, next_execution_at),
                        updated_at = @UpdatedAt
                    WHERE task_key = @TaskKey;
                    """,
                    new
                    {
                        TaskKey = taskKey,
                        LastExecutionAt = lastExecutionIso,
                        LastDurationMs = durationMs,
                        LastStatus = status,
                        LastMessage = message,
                        NextExecutionAt = nextExecutionIso,
                        UpdatedAt = nowIso,
                    },
                    tx);
            },
            ct);
    }

    private async Task BroadcastTaskSafeAsync(string taskKey, string status, string message, CancellationToken ct)
    {
        try
        {
            await _realtime.PublishScopedEventAsync(
                PermissionScopes.QueueRead,
                "TaskUpdated",
                new
                {
                    taskKey,
                    status,
                    isRunning = status == "running",
                    message,
                    timestampUtc = DateTimeOffset.UtcNow,
                },
                ct);
        }
        catch
        {
            // Never fail a scheduled task if SignalR broadcast fails
        }
    }

    private ScheduledTaskSummary MapSummary(ScheduledTaskRow row)
    {
        var isRunning = _runningFlags.TryGetValue(row.TaskKey, out var running) && running;
        return new ScheduledTaskSummary(
            TaskKey: row.TaskKey,
            Name: row.Name,
            Description: row.Description,
            IntervalSeconds: row.IntervalSeconds,
            IsRunning: isRunning,
            IsEnabled: row.IsEnabled != 0,
            LastExecutionAt: row.LastExecutionAt,
            LastDurationMs: row.LastDurationMs,
            LastStatus: isRunning ? "running" : row.LastStatus,
            LastMessage: row.LastMessage,
            NextExecutionAt: row.NextExecutionAt);
    }

    private sealed record TaskDefinitionSeed(
        string TaskKey,
        string Name,
        string Description,
        int IntervalSeconds,
        bool HasExplicitIntervalOverride = false);

    private sealed class ScheduledTaskRow
    {
        public string TaskKey { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public int IntervalSeconds { get; set; }
        public int IsEnabled { get; set; }
        public string? LastExecutionAt { get; set; }
        public long? LastDurationMs { get; set; }
        public string? LastStatus { get; set; }
        public string? LastMessage { get; set; }
        public string? NextExecutionAt { get; set; }
    }
}
