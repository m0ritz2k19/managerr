using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Managerr.Catalog;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Downloaders;
using Managerr.Profiles;

namespace Managerr.Cleanerr;

public sealed class CleanerrWorkflowApiDisabledException(string message) : InvalidOperationException(message);

public interface ICleanerrLifecycleEngine
{
    IReadOnlyList<CleanerrWorkflowTemplate> ListTemplates();
    CleanerrRuleCatalogResponse GetRuleCatalog();
    IReadOnlyList<CleanerrNodeCatalogEntry> GetNodeCatalog();
    Task<CleanerrOverviewStatsDto> GetOverviewStatsAsync(CancellationToken ct = default);

    Task<CleanerrGeneralSettings> GetGeneralSettingsAsync(CancellationToken ct = default);
    Task<CleanerrGeneralSettings> UpdateGeneralSettingsAsync(UpdateCleanerrGeneralSettingsRequest request, CancellationToken ct = default);

    Task<CleanerrMediaItem> UpsertMediaItemAsync(UpsertCleanerrMediaItemRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<CleanerrMediaItem>> ListMediaItemsAsync(CancellationToken ct = default);
    Task<CleanerrMediaListResponse> QueryMediaItemsAsync(
        string? search = null,
        string? mediaType = null,
        long? libraryId = null,
        int page = 1,
        int pageSize = 50,
        string? watched = null,
        string? sort = null,
        CancellationToken ct = default);
    Task<CleanerrMediaChildrenResponse> GetMediaChildrenAsync(long showMediaItemId, CancellationToken ct = default);
    Task SyncCatalogItemsIntoCleanerrAsync(CancellationToken ct = default);
    Task<CleanerrPlexSyncResult> SyncFromPlexServerAsync(SyncCleanerrPlexRequest? request = null, CancellationToken ct = default);

    Task<IReadOnlyList<CleanerrWorkflowSummary>> ListWorkflowsAsync(CancellationToken ct = default);
    Task<CleanerrWorkflowSummary?> GetWorkflowByIdAsync(long id, CancellationToken ct = default);
    Task<CleanerrWorkflowSummary> UpsertWorkflowAsync(UpsertCleanerrWorkflowRequest request, long? id = null, CancellationToken ct = default);
    Task<CleanerrWorkflowSummary> DuplicateWorkflowAsync(long id, string? newName = null, CancellationToken ct = default);
    Task DeleteWorkflowAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<CleanerrWorkflowRunResult>> ListRunsAsync(long? workflowId = null, int limit = 25, CancellationToken ct = default);
    Task<IReadOnlyList<CleanerrWorkflowRunResult>> EvaluateScheduledWorkflowsAsync(DateTimeOffset? referenceNow = null, CancellationToken ct = default);

    Task<CleanerrWorkflowRunResult> SimulateWorkflowAsync(SimulateCleanerrWorkflowRequest request, CancellationToken ct = default);
    Task<CleanerrLiveImpactPreview> PreviewWorkflowLiveImpactAsync(
        long? workflowId,
        PreviewCleanerrLiveImpactRequest? request = null,
        CancellationToken ct = default);
    Task<CleanerrWorkflowRunResult> ExecuteWorkflowAsync(
        long workflowId,
        bool? dryRunOverride = null,
        string triggerType = "manual",
        double? simulatedFreeSpaceGb = null,
        double? simulatedFreeSpacePercent = null,
        CancellationToken ct = default);

    Task<CleanerrTestMediaResult> TestMediaAsync(TestCleanerrMediaRequest request, CancellationToken ct = default);
    Task<ExportCleanerrRuleYamlResponse> ExportWorkflowYamlAsync(long workflowId, CancellationToken ct = default);
    Task<CleanerrWorkflowSummary> ImportWorkflowYamlAsync(ImportCleanerrRuleYamlRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<CleanerrDeletionQueueItem>> ListDeletionQueueAsync(string? statusFilter = null, CancellationToken ct = default);
    Task<CleanerrQueueProcessResult> ProcessDeletionQueueAsync(DateTimeOffset? referenceNow = null, CancellationToken ct = default);
    Task<IReadOnlyList<CleanerrDeletionQueueItem>> ManualStageItemsAsync(
        ManualStageCleanerrItemsRequest request,
        string actorUsername = "admin",
        CancellationToken ct = default);
    Task<CleanerrDeletionQueueItem> PostponeQueueItemAsync(
        long queueId,
        int? days = null,
        string? reason = null,
        CancellationToken ct = default);
    Task<IReadOnlyList<CleanerrDeletionQueueItem>> PostponeMediaIdsWithFamilyAsync(
        IReadOnlyList<long> mediaIds,
        int days,
        string reason,
        CancellationToken ct = default);
    Task<int> PostponeAllStagedAsync(
        long? workflowId,
        int days,
        string reason,
        CancellationToken ct = default);
    Task<CleanerrDeletionQueueItem> DeleteQueueItemNowAsync(
        long queueId,
        string actorUsername = "admin",
        CancellationToken ct = default);
    Task<CleanerrDeletionQueueItem> RescueQueueItemAsync(long queueId, string reason = "Manually rescued by user", CancellationToken ct = default);
    Task<IReadOnlyList<CleanerrDeletionQueueItem>> RescueMediaIdsWithFamilyAsync(
        IReadOnlyList<long> mediaIds,
        string reason,
        CancellationToken ct = default);
    Task<(CleanerrDeletionQueueItem QueueItem, CleanerrExclusionRule Rule)> ExcludeQueueItemAsync(
        long queueId,
        string createdBy,
        long? createdByUserId,
        int? exclusionQuotaLimit,
        string reason = "Excluded from Deletion Queue",
        string? expiresAt = null,
        CancellationToken ct = default);
    Task<int> BulkExcludeQueueAsync(
        BulkQueueExcludeRequest request,
        string createdBy,
        long? createdByUserId,
        int? exclusionQuotaLimit,
        CancellationToken ct = default);
    Task DeleteQueueItemRecordAsync(long queueId, CancellationToken ct = default);

    Task<IReadOnlyList<CleanerrExclusionRule>> ListExclusionRulesAsync(CancellationToken ct = default);
    Task<CleanerrExclusionRule> CreateExclusionRuleAsync(
        CreateCleanerrExclusionRequest request,
        string createdBy,
        long? createdByUserId,
        int? exclusionQuotaLimit,
        CancellationToken ct = default);
    Task<IReadOnlyList<string>> ExcludeMediaIdsWithFamilyEscalationAsync(
        IReadOnlyList<long> mediaIds,
        string createdBy,
        string reason,
        long? createdByUserId = null,
        int? exclusionQuotaLimit = null,
        string? expiresAt = null,
        CancellationToken ct = default);
    Task<int> ExcludeAllStagedAsync(
        long? workflowId,
        string reason,
        long? createdByUserId = null,
        int? exclusionQuotaLimit = null,
        CancellationToken ct = default);
    Task DeleteExclusionRuleAsync(long ruleId, CancellationToken ct = default);

    Task<JsonObject> HandleDiscordInteractionAsync(JsonObject interactionPayload, CancellationToken ct = default);
    Task<JsonObject> HandleInboundWebhookAsync(string source, JsonObject payload, CancellationToken ct = default);
}

/// <summary>
/// Cleanerr Hybrid Lifecycle Engine (Maintainerr Section Rule Groups + Visual DAG Studio),
/// Grace/Auto-Rescue Worker, 24h Final Warning Reminders, Active-Stream Guard,
/// Whole-Series Family Exclusion Escalator, and Discord Bot Coordinator.
/// </summary>
public sealed partial class CleanerrLifecycleEngine(
    ISqliteDbFactory dbFactory,
    IPolymorphicCatalogService catalogService,
    ICleanerrDiscordService discordService,
    ICleanerrHttpGateway httpGateway,
    IAppSettingsService? appSettings = null,
    IComposableProfileService? profileService = null,
    IUnifiedDownloadOrchestrator? downloadOrchestrator = null) : ICleanerrLifecycleEngine
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _activeStreamSyncGate = new(1, 1);
    private readonly Dictionary<string, HashSet<string>> _activeStreamKeysByMediaRk = new(StringComparer.Ordinal);

    private const string SettingDefaultGraceDays = "cleanerr.default_grace_days";
    private const string SettingDefaultPostponeDays = "cleanerr.default_postpone_days";
    private const string SettingTorrentDownloadsPath = "cleanerr.torrent_downloads_path";
    private const string SettingSeerrUrl = "cleanerr.seerr_url";
    private const string SettingSeerrApiKey = "cleanerr.seerr_api_key";
    private const string SettingFinalWarningHours = "cleanerr.discord_final_warning_hours";

    public CleanerrRuleCatalogResponse GetRuleCatalog() =>
        CleanerrMaintainerrRuleEngine.GetRuleCatalog();

    public IReadOnlyList<CleanerrNodeCatalogEntry> GetNodeCatalog() =>
    [
        // Triggers ("trigger")
        new("trigger.schedule", "trigger", "Cron Schedule Trigger", "Runs workflow automatically on a 5-field UTC cron schedule.", "#ffc230",
            new JsonObject { ["cron"] = "0 3 * * *" }),
        new("trigger.webhook", "trigger", "Webhook Trigger", "Fires when an inbound Plex, Radarr, Sonarr, or Seerr webhook arrives.", "#ffc230",
            new JsonObject { ["source"] = "plex", ["event"] = "media.scrobble" }),
        new("trigger.manual", "trigger", "Manual / API Trigger", "Executes only when invoked manually, via API, or via Discord slash command.", "#ffc230",
            new JsonObject { ["allow_api"] = true }),
        new("trigger.storage_pressure", "trigger", "Storage Pressure Trigger", "Halts or triggers cleanup when drive free space drops below threshold.", "#ffc230",
            new JsonObject { ["min_free_gb_trigger"] = 200, ["target_free_gb"] = 500 }),

        // Sources ("source")
        new("source.plex_library", "source", "Plex Library Source", "Loads synced Plex items filtered by media type and library IDs.", "#35c5f4",
            new JsonObject { ["media_type"] = "movie", ["library_ids"] = new JsonArray() }),
        new("source.managerr_catalog", "source", "Managerr Catalog Source", "Loads movies and whole TV series directly from the Managerr catalog.", "#35c5f4",
            new JsonObject { ["media_type"] = "movie", ["library_ids"] = new JsonArray() }),
        new("source.managerr_library", "source", "Managerr Library", "Loads native Managerr catalog items by media type and library.", "#35c5f4",
            new JsonObject { ["media_type"] = "movie", ["library_ids"] = new JsonArray() }),
        new("source.tautulli", "source", "Tautulli History", "Loads library media enriched with playback history and viewer metrics.", "#35c5f4",
            new JsonObject { ["media_type"] = "movie", ["library_ids"] = new JsonArray() }),
        new("source.arr_library", "source", "Arr Library", "Loads movies or series tracked by Managerr/Radarr/Sonarr.", "#35c5f4",
            new JsonObject { ["media_type"] = "movie", ["library_ids"] = new JsonArray() }),
        new("source.overseerr", "source", "Overseerr / Requests", "Loads media items that originated from a user request.", "#35c5f4",
            new JsonObject { ["media_type"] = "movie", ["requested_by"] = "" }),

        // Filters ("filter")
        new("filter.watch_state", "filter", "Watch State Filter", "Filters by last watched age, view count, viewer, and requester watch status.", "#a855f7",
            new JsonObject { ["last_watched_older_than_days"] = 90, ["unwatched_added_older_than_days"] = 0, ["view_count_gte"] = 0, ["watched_by_user"] = "", ["protect_in_progress"] = true }),
        new("filter.age", "filter", "Added / Release Age", "Filters by days since added to library or release year threshold.", "#a855f7",
            new JsonObject { ["added_older_than_days"] = 180 }),
        new("filter.metadata", "filter", "Size & Metadata Filter", "Filters by file size (GB), resolution, rating, year, or genre.", "#a855f7",
            new JsonObject { ["size_gt_gb"] = 0, ["resolution"] = "", ["rating_lt"] = 0, ["year_lt"] = 0, ["genre"] = "" }),
        new("filter.arr_seerr", "filter", "Arr / Seerr Filter", "Filters by monitored state, tags, Seerr requester, request age, or hardlinks.", "#a855f7",
            new JsonObject { ["include_tags"] = new JsonArray(), ["exclude_tags"] = new JsonArray("keep"), ["monitored_only"] = false, ["seerr_requested_by"] = "", ["seerr_requested_older_than_days"] = 0, ["skip_hardlinked_seeding"] = true }),
        new("filter.codec_density", "filter", "Codec & Density Filter", "Filters by video codecs, minimum file size, resolution, bitrate, and dual-audio protection.", "#a855f7",
            new JsonObject { ["codecs"] = new JsonArray("h264"), ["min_size_gb"] = 15, ["protect_dual_audio"] = true, ["skip_hardlinked_seeding"] = true }),
        new("filter.size_codec", "filter", "Size & Video Codec", "Filters by disk size, resolution, video codec, and seeding hardlinks.", "#a855f7",
            new JsonObject { ["size_gt_gb"] = 10, ["skip_hardlinked_seeding"] = true }),
        new("filter.tags_labels", "filter", "Tags, Labels & Genres", "Filters by catalog tags, Plex labels, genres, or classification taxonomy.", "#a855f7",
            new JsonObject { ["has_tags"] = new JsonArray(), ["exclude_tags"] = new JsonArray("keep") }),
        new("filter.active_stream_guard", "filter", "Active Stream Guard", "Protects items currently streaming or mid-playback (and optionally active series).", "#a855f7",
            new JsonObject { ["protect_in_progress"] = true, ["protect_active_series"] = false }),
        new("filter.exclusion_check", "filter", "Exclusion Rules Guard", "Removes items protected by Cleanerr exclusion rules or excluded tags/users.", "#27c24c",
            new JsonObject { ["respect_global_exclusions"] = true, ["respect_user_quotas"] = true, ["excluded_tags"] = new JsonArray("keep", "protected"), ["excluded_users"] = new JsonArray() }),
        new("filter.disk_space", "filter", "Disk Space Guard", "Halts downstream nodes unless free disk space falls below threshold.", "#a855f7",
            new JsonObject { ["min_free_gb"] = 250, ["target_free_gb"] = 350 }),

        // Logic ("logic")
        new("logic.rule_group", "logic", "Maintainerr Rule Group", "Evaluates boolean sections using Maintainerr-compatible fields & comparators.", "#5d9cec",
            new JsonObject { ["targetMediaType"] = "movie", ["useRules"] = true, ["sections"] = new JsonArray() }),
        new("logic.combinator", "logic", "Logic Combinator (AND/OR/NOT)", "Combine or invert upstream media streams using boolean operations.", "#5d9cec",
            new JsonObject { ["operator"] = "AND" }),
        new("logic.and", "logic", "Logical AND", "Outputs only items present in every upstream branch.", "#5d9cec",
            new JsonObject()),
        new("logic.or", "logic", "Logical OR", "Outputs the union of items from all upstream branches.", "#5d9cec",
            new JsonObject()),
        new("logic.not", "logic", "Logical NOT (Subtract)", "Subtracts subsequent upstream branches from the first branch.", "#5d9cec",
            new JsonObject()),

        // Limits ("limit")
        new("limit.reclaim_budget", "limit", "Reclaim Budget Limit", "Cap reclaimed GB per run or stop at target free space watermark.", "#ffa726",
            new JsonObject { ["max_reclaim_gb"] = 250, ["target_free_space_gb_watermark"] = 500, ["min_free_space_gb_trigger"] = 0 }),
        new("filter.limit", "limit", "Limit / Reclaim Budget", "Caps item count or total reclaimable GB ordered by oldest watched or largest first.", "#ffa726",
            new JsonObject { ["max_items"] = 50, ["max_reclaim_gb"] = 200, ["sort_by"] = "oldest_watched" }),

        // Queue ("queue")
        new("queue.grace_period", "queue", "Grace Period Stage", "Stages matched items in the Deletion Queue for N days before execution.", "#f97316",
            new JsonObject { ["grace_days"] = 7, ["discord_webhook_url"] = "", ["auto_rescue_on_watch"] = true }),
        new("notify.discord", "queue", "Discord Notification", "Sends a Discord summary or interactive staged alert.", "#ec4899",
            new JsonObject { ["grace_days"] = 7 }),
        new("notify.webhook", "queue", "Outbound Webhook", "POSTs JSON summary payload for matched items to a webhook URL.", "#ec4899",
            new JsonObject { ["url"] = "" }),

        // Actions ("action")
        new("action.managerr", "action", "Managerr Lifecycle Action", "Deletes files/unmonitors, removes empty shows, or downgrades quality profile.", "#f05050",
            new JsonObject { ["mode"] = "delete_files_and_unmonitor", ["unmonitor"] = true, ["clean_empty_folders"] = true, ["reset_requests"] = false, ["prune_torrent_client"] = false }),
        new("action.radarr", "action", "Radarr / Movie Action", "Movie-targeted delete/unmonitor or profile downgrade action.", "#f05050",
            new JsonObject { ["mode"] = "delete_files_and_unmonitor", ["clean_empty_folders"] = true }),
        new("action.sonarr", "action", "Sonarr / TV Action", "Whole-series delete, unmonitor, or profile downgrade action.", "#f05050",
            new JsonObject { ["mode"] = "delete_show_if_empty", ["clean_empty_folders"] = true }),
        new("action.plex", "action", "Plex Maintenance Action", "Refreshes library, empties trash, unmatches, or removes media in Plex.", "#f05050",
            new JsonObject { ["mode"] = "refresh_library_and_empty_trash" }),
        new("action.seerr_reset", "action", "Reset Seerr / Request", "Marks native requests cleaned_reset and optionally resets Overseerr/Jellyseerr.", "#f05050",
            new JsonObject { ["reset_external_seerr"] = true }),
        new("action.seerr", "action", "Seerr Request Action", "Reset Seerr media status or remove requests for re-requesting.", "#f05050",
            new JsonObject { ["mode"] = "reset_media_status" }),
        new("action.add_import_exclusion", "action", "Add Import Exclusion", "Adds item to Managerr's import_list_exclusions so import lists won't re-add it.", "#f05050",
            new JsonObject()),
        new("action.delete_files", "action", "Delete Files on Disk", "Deletes media & sidecar files strictly under configured library roots.", "#f05050",
            new JsonObject { ["clean_empty_folders"] = true, ["unmonitor"] = false }),
        new("action.webhook", "action", "Webhook Notification", "Send JSON webhook summary of staged or cleaned items.", "#f05050",
            new JsonObject { ["url"] = "", ["include_items"] = true }),
    ];

    public IReadOnlyList<CleanerrWorkflowTemplate> ListTemplates() =>
    [
        ..CleanerrMaintainerrRuleEngine.GetMaintainerrRulePresets(),
        new CleanerrWorkflowTemplate(
            "stale-watched-movies",
            "Stale Watched Movies (90d + 14d Leaving Soon Grace)",
            "Movies",
            "Stages movies watched > 90 days ago into Managerr's native Leaving Soon queue for 14 days with Discord interactive Exclude & Postpone buttons.",
            JsonNode.Parse("""
                {
                  "nodes": [
                    { "id": "trig", "data": { "label": "Daily Schedule", "node_type": "trigger.schedule", "config": { "cron": "0 3 * * *" } } },
                    { "id": "src", "data": { "label": "Movies Library", "node_type": "source.plex_library", "config": { "media_type": "movie" } } },
                    { "id": "watch", "data": { "label": "Watched > 90d", "node_type": "filter.watch_state", "config": { "last_watched_older_than_days": 90 } } },
                    { "id": "excl", "data": { "label": "Exclusion Check", "node_type": "filter.exclusion_check", "config": {} } },
                    { "id": "grace", "data": { "label": "14d Leaving Soon Grace", "node_type": "queue.grace_period", "config": { "grace_days": 14 } } },
                    { "id": "act", "data": { "label": "Delete & Unmonitor", "node_type": "action.managerr", "config": { "mode": "delete_files_and_unmonitor" } } }
                  ],
                  "edges": [
                    { "source": "trig", "target": "src" },
                    { "source": "src", "target": "watch" },
                    { "source": "watch", "target": "excl" },
                    { "source": "excl", "target": "grace" },
                    { "source": "grace", "target": "act" }
                  ]
                }
                """)!.AsObject()),
        new CleanerrWorkflowTemplate(
            "stale-watched-tv-series",
            "Stale Watched TV Series (180d + 14d Leaving Soon Grace)",
            "TV",
            "Stages whole TV series watched > 180 days ago (protecting in-progress playback) into Managerr's Leaving Soon queue for 14 days before deletion and unmonitoring.",
            JsonNode.Parse("""
                {
                  "nodes": [
                    { "id": "src", "data": { "label": "TV Shows", "node_type": "source.plex_library", "config": { "media_type": "show" } } },
                    { "id": "watch", "data": { "label": "Watched > 180d", "node_type": "filter.watch_state", "config": { "last_watched_older_than_days": 180, "protect_in_progress": true } } },
                    { "id": "excl", "data": { "label": "Exclusion Check", "node_type": "filter.exclusion_check", "config": {} } },
                    { "id": "grace", "data": { "label": "14d Leaving Soon Grace", "node_type": "queue.grace_period", "config": { "grace_days": 14 } } },
                    { "id": "act", "data": { "label": "Delete & Unmonitor Series", "node_type": "action.managerr", "config": { "mode": "delete_files_and_unmonitor" } } }
                  ],
                  "edges": [
                    { "source": "src", "target": "watch" },
                    { "source": "watch", "target": "excl" },
                    { "source": "excl", "target": "grace" },
                    { "source": "grace", "target": "act" }
                  ]
                }
                """)!.AsObject()),
        new CleanerrWorkflowTemplate(
            "ended-tv-series-cleanup",
            "Ended TV Series Inactive > 90 Days",
            "TV",
            "Stages ended TV series inactive > 90 days (protecting in-progress playback) into Leaving Soon for 14 days before removing files and unmonitoring the series.",
            JsonNode.Parse("""
                {
                  "nodes": [
                    { "id": "src", "data": { "label": "TV Shows", "node_type": "source.plex_library", "config": { "media_type": "show" } } },
                    { "id": "meta", "data": { "label": "Ended Series Only", "node_type": "filter.metadata", "config": { "work_status": "Ended" } } },
                    { "id": "watch", "data": { "label": "Watched > 90d", "node_type": "filter.watch_state", "config": { "last_watched_older_than_days": 90, "protect_in_progress": true } } },
                    { "id": "excl", "data": { "label": "Exclusion Check", "node_type": "filter.exclusion_check", "config": {} } },
                    { "id": "grace", "data": { "label": "14d Leaving Soon Grace", "node_type": "queue.grace_period", "config": { "grace_days": 14 } } },
                    { "id": "act", "data": { "label": "Delete & Unmonitor Series", "node_type": "action.managerr", "config": { "mode": "delete_files_and_unmonitor" } } }
                  ],
                  "edges": [
                    { "source": "src", "target": "meta" },
                    { "source": "meta", "target": "watch" },
                    { "source": "watch", "target": "excl" },
                    { "source": "excl", "target": "grace" },
                    { "source": "grace", "target": "act" }
                  ]
                }
                """)!.AsObject()),
        new CleanerrWorkflowTemplate(
            "storage-pressure-emergency",
            "Storage Pressure Guard (< 500 GB Free -> Recover to 650 GB)",
            "Storage",
            "Activates when free disk space drops below 500 GB, skips hardlinked seeding torrents, and stages oldest-watched large movies until 650 GB free watermark is reached.",
            JsonNode.Parse("""
                {
                  "nodes": [
                    { "id": "press", "data": { "label": "Free < 500 GB (Target 650 GB)", "node_type": "trigger.storage_pressure", "config": { "min_free_gb": 500, "target_free_gb": 650, "sort_by": "oldest_watched" } } },
                    { "id": "src", "data": { "label": "Movies", "node_type": "source.plex_library", "config": { "media_type": "movie" } } },
                    { "id": "meta", "data": { "label": "Size > 25 GB (Non-Hardlinked)", "node_type": "filter.metadata", "config": { "size_gt_gb": 25, "skip_hardlinked_seeding": true } } },
                    { "id": "excl", "data": { "label": "Exclusion Check", "node_type": "filter.exclusion_check", "config": {} } },
                    { "id": "grace", "data": { "label": "3d Emergency Grace", "node_type": "queue.grace_period", "config": { "grace_days": 3 } } },
                    { "id": "act", "data": { "label": "Reclaim Space", "node_type": "action.managerr", "config": { "mode": "delete_files_and_unmonitor" } } }
                  ],
                  "edges": [
                    { "source": "press", "target": "src" },
                    { "source": "src", "target": "meta" },
                    { "source": "meta", "target": "excl" },
                    { "source": "excl", "target": "grace" },
                    { "source": "grace", "target": "act" }
                  ]
                }
                """)!.AsObject()),
        new CleanerrWorkflowTemplate(
            "uhd-remux-downgrade",
            "2160p Remux -> 1080p Quality Profile Downgrade",
            "Optimization",
            "Instead of deleting watched favorites, switches their Managerr unit profile to a lower-bitrate quality profile to save storage.",
            JsonNode.Parse("""
                {
                  "nodes": [
                    { "id": "src", "data": { "label": "4K Movies", "node_type": "source.plex_library", "config": { "media_type": "movie" } } },
                    { "id": "meta", "data": { "label": "Resolution 2160p", "node_type": "filter.metadata", "config": { "resolution": "2160p", "size_gt_gb": 35 } } },
                    { "id": "watch", "data": { "label": "Watched > 120d", "node_type": "filter.watch_state", "config": { "last_watched_older_than_days": 120 } } },
                    { "id": "act", "data": { "label": "Downgrade Quality Profile", "node_type": "action.managerr", "config": { "mode": "downgrade_profile", "target_profile_id": null } } }
                  ],
                  "edges": [
                    { "source": "src", "target": "meta" },
                    { "source": "meta", "target": "watch" },
                    { "source": "watch", "target": "act" }
                  ]
                }
                """)!.AsObject()),
        new CleanerrWorkflowTemplate(
            "unwatched-requests-protect-requester",
            "Stale Requests (Protect Until Requester Watched)",
            "Requests",
            "Cleans up requested items older than 60 days ONLY after the requesting PlexUser has watched them.",
            JsonNode.Parse("""
                {
                  "nodes": [
                    { "id": "src", "data": { "label": "Movies", "node_type": "source.plex_library", "config": { "media_type": "movie" } } },
                    { "id": "req", "data": { "label": "Requested > 60d", "node_type": "filter.arr_seerr", "config": { "is_requested": true, "request_older_than_days": 60 } } },
                    { "id": "watch", "data": { "label": "Protect Until Requester Watched", "node_type": "filter.watch_state", "config": { "protect_until_requester_watched": true } } },
                    { "id": "grace", "data": { "label": "7d Grace", "node_type": "queue.grace_period", "config": { "grace_days": 7 } } },
                    { "id": "act", "data": { "label": "Delete & Reset Request", "node_type": "action.managerr", "config": { "mode": "delete_files_and_unmonitor", "reset_requests": true } } }
                  ],
                  "edges": [
                    { "source": "src", "target": "req" },
                    { "source": "req", "target": "watch" },
                    { "source": "watch", "target": "grace" },
                    { "source": "grace", "target": "act" }
                  ]
                }
                """)!.AsObject()),
    ];

    public async Task<CleanerrGeneralSettings> GetGeneralSettingsAsync(CancellationToken ct = default)
    {
        if (appSettings is null)
        {
            return new CleanerrGeneralSettings(7, "", false, "", 24, 7, "/data/downloads");
        }

        var graceStr = await appSettings.GetAsync(SettingDefaultGraceDays, ct);
        var graceDays = int.TryParse(graceStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g)
            ? Math.Clamp(g, 0, 365)
            : 7;

        var postponeStr = await appSettings.GetAsync(SettingDefaultPostponeDays, ct);
        var postponeDays = int.TryParse(postponeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)
            ? Math.Clamp(p, 1, 365)
            : 7;

        var torrentPathRaw = (await appSettings.GetAsync(SettingTorrentDownloadsPath, ct))?.Trim();
        var torrentPath = string.IsNullOrWhiteSpace(torrentPathRaw) ? "/data/downloads" : torrentPathRaw;

        var seerrUrl = (await appSettings.GetAsync(SettingSeerrUrl, ct))?.Trim() ?? "";
        var maskedKey = await appSettings.GetMaskedSecretAsync(SettingSeerrApiKey, ct);
        var warnStr = await appSettings.GetAsync(SettingFinalWarningHours, ct);
        var warnHours = int.TryParse(warnStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wh)
            ? Math.Clamp(wh, 1, 168)
            : 24;

        return new CleanerrGeneralSettings(
            DefaultGraceDays: graceDays,
            SeerrUrl: seerrUrl,
            HasSeerrApiKey: maskedKey.IsSet,
            MaskedSeerrApiKey: maskedKey.MaskedHint ?? "",
            FinalWarningHours: warnHours,
            DefaultPostponeDays: postponeDays,
            TorrentDownloadsPath: torrentPath);
    }

    public async Task<CleanerrGeneralSettings> UpdateGeneralSettingsAsync(
        UpdateCleanerrGeneralSettingsRequest request,
        CancellationToken ct = default)
    {
        if (appSettings is not null)
        {
            if (request.DefaultGraceDays.HasValue)
            {
                await appSettings.SetAsync(
                    SettingDefaultGraceDays,
                    Math.Clamp(request.DefaultGraceDays.Value, 0, 365).ToString(CultureInfo.InvariantCulture),
                    isSecret: false,
                    ct);
            }

            if (request.DefaultPostponeDays.HasValue)
            {
                await appSettings.SetAsync(
                    SettingDefaultPostponeDays,
                    Math.Clamp(request.DefaultPostponeDays.Value, 1, 365).ToString(CultureInfo.InvariantCulture),
                    isSecret: false,
                    ct);
            }

            if (request.TorrentDownloadsPath is not null)
            {
                var cleanedPath = string.IsNullOrWhiteSpace(request.TorrentDownloadsPath)
                    ? "/data/downloads"
                    : request.TorrentDownloadsPath.Trim();
                await appSettings.SetAsync(SettingTorrentDownloadsPath, cleanedPath, isSecret: false, ct);
            }

            if (request.SeerrUrl is not null)
            {
                await appSettings.SetAsync(SettingSeerrUrl, request.SeerrUrl.Trim().TrimEnd('/'), isSecret: false, ct);
            }

            if (request.SeerrApiKey is not null)
            {
                await appSettings.SetAsync(SettingSeerrApiKey, request.SeerrApiKey.Trim(), isSecret: true, ct);
            }

            if (request.FinalWarningHours.HasValue)
            {
                await appSettings.SetAsync(
                    SettingFinalWarningHours,
                    Math.Clamp(request.FinalWarningHours.Value, 1, 168).ToString(CultureInfo.InvariantCulture),
                    isSecret: false,
                    ct);
            }
        }

        return await GetGeneralSettingsAsync(ct);
    }

    public async Task<CleanerrOverviewStatsDto> GetOverviewStatsAsync(CancellationToken ct = default)
    {
        await SyncCatalogItemsIntoCleanerrAsync(ct);
        var allMedia = await ListMediaItemsAsync(ct);
        var byId = allMedia.ToDictionary(m => m.Id);
        var allQueue = await ListDeletionQueueAsync(null, ct);
        var staged = allQueue.Where(q => q.Status == "staged").ToList();
        var exclusions = await ListExclusionRulesAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var activeExcl = exclusions.Count(r => string.IsNullOrWhiteSpace(r.ExpiresAt) || (ParseDto(r.ExpiresAt) is { } exp && exp > now));
        var workflows = await ListWorkflowsAsync(ct);
        var discordCfg = await discordService.GetSettingsAsync(ct);
        var libraries = await catalogService.ListLibrariesAsync(null, ct);
        var (freeGb, freePct) = InspectDiskSpace(null, null, libraries);

        // Whole-series-only totals (never double-count or include legacy season/episode rows)
        var leafOrMovieMedia = allMedia.Where(m => m.MediaType is "movie" or "show" or "music" or "ebook" or "audiobook").ToList();

        var stagedReclaimable = staged
            .Where(q => !byId.TryGetValue(q.MediaItemId, out var m) || !m.IsHardlinked)
            .Sum(q => q.SizeBytes);

        var lastRunAt = workflows
            .Select(w => w.LastRunAt)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .OrderByDescending(s => s, StringComparer.Ordinal)
            .FirstOrDefault();

        var libraryStorage = BuildLibraryStorageStats(libraries, allMedia);
        var reclaimForecast = ComputeReclaimForecast(staged, byId, now);

        return new CleanerrOverviewStatsDto(
            TotalMediaItems: allMedia.Count(m => m.MediaType is "movie" or "show"),
            TotalMediaSizeBytes: leafOrMovieMedia.Sum(m => m.SizeBytes),
            StagedCount: staged.Count,
            StagedSizeBytes: staged.Sum(q => q.SizeBytes),
            StagedReclaimableBytes: stagedReclaimable,
            ActiveExclusionsCount: activeExcl,
            EnabledWorkflowsCount: workflows.Count(w => w.Enabled),
            RescuedTotalCount: allQueue.Count(q => q.Status is "rescued" or "excluded"),
            DeletedTotalCount: allQueue.Count(q => q.Status == "deleted"),
            DiskFreeGb: Math.Round(freeGb, 2),
            DiskFreePercent: Math.Round(freePct, 2),
            LastRunAt: lastRunAt,
            DiscordConfigured: discordCfg.HasBotToken || !string.IsNullOrWhiteSpace(discordCfg.WebhookUrl),
            DiscordGatewayConnected: discordCfg.GatewayConnected,
            DiscordAuthMode: discordCfg.AuthMode,
            LibraryStorage: libraryStorage,
            ReclaimForecast: reclaimForecast);
    }

    public async Task<CleanerrMediaItem> UpsertMediaItemAsync(
        UpsertCleanerrMediaItemRequest req,
        CancellationToken ct = default)
    {
        var mediaType = req.MediaType.Trim().ToLowerInvariant() switch
        {
            "series" or "tv" => "show",
            var t => t,
        };
        if (mediaType is "season" or "seasons" or "episode" or "episodes")
        {
            throw new ArgumentException("TV series are handled in full; use mediaType 'show' instead of 'season' or 'episode'.");
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var addedAt = string.IsNullOrWhiteSpace(req.AddedAt) ? now : req.AddedAt!;
        var resolvedClass = req.ClassificationKind ?? InferClassificationFromTagsAndType(req.Tags, mediaType);

        var id = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                var rk = req.PlexRatingKey.Trim();
                var existingId = await conn.ExecuteScalarAsync<long?>(
                    "SELECT id FROM cleanerr_media_items WHERE plex_rating_key = @Rk;",
                    new { Rk = rk },
                    transaction: tx);

                if (!existingId.HasValue && !rk.StartsWith("mgr-", StringComparison.Ordinal))
                {
                    if (mediaType is "movie" or "show" && req.WorkId.HasValue)
                    {
                        existingId = await conn.ExecuteScalarAsync<long?>(
                            "SELECT id FROM cleanerr_media_items WHERE plex_rating_key LIKE 'mgr-%' AND work_id = @WorkId AND media_type = @MediaType LIMIT 1;",
                            new { WorkId = req.WorkId.Value, MediaType = mediaType },
                            transaction: tx);
                    }
                }

                var param = new
                {
                    Id = existingId,
                    Rk = rk,
                    ParentRk = req.ParentRatingKey,
                    GrandRk = req.GrandparentRatingKey,
                    req.ParentTitle,
                    req.GrandparentTitle,
                    req.LibraryId,
                    req.WorkId,
                    req.UnitId,
                    MediaType = mediaType,
                    Title = req.Title.Trim(),
                    req.Year,
                    req.SeasonNumber,
                    req.EpisodeNumber,
                    req.SizeBytes,
                    req.Resolution,
                    req.Rating,
                    GenresJson = JsonSerializer.Serialize(req.Genres ?? [], JsonOpts),
                    TagsJson = JsonSerializer.Serialize(req.Tags ?? [], JsonOpts),
                    Monitored = req.Monitored ? 1 : 0,
                    AddedAt = addedAt,
                    req.LastViewedAt,
                    req.ViewCount,
                    ViewedByJson = JsonSerializer.Serialize(req.ViewedBy ?? [], JsonOpts),
                    req.ActiveStreamCount,
                    req.SeerrId,
                    req.SeerrRequestedBy,
                    req.SeerrRequestedAt,
                    RequesterWatched = req.RequesterWatched ? 1 : 0,
                    req.FilePath,
                    req.VideoCodec,
                    req.ViewOffsetMs,
                    AudioLanguagesJson = JsonSerializer.Serialize(req.AudioLanguages ?? [], JsonOpts),
                    IsHardlinked = req.IsHardlinked ? 1 : 0,
                    req.CustomFormatScore,
                    ClassificationKind = resolvedClass,
                    IsWatchlisted = req.IsWatchlisted ? 1 : 0,
                    req.WorkStatus,
                    req.PosterUrl,
                    Now = now,
                };

                if (existingId.HasValue)
                {
                    await conn.ExecuteAsync(
                        """
                        UPDATE cleanerr_media_items
                        SET plex_rating_key = @Rk,
                            parent_rating_key = COALESCE(@ParentRk, parent_rating_key),
                            grandparent_rating_key = COALESCE(@GrandRk, grandparent_rating_key),
                            parent_title = COALESCE(@ParentTitle, parent_title),
                            grandparent_title = COALESCE(@GrandparentTitle, grandparent_title),
                            library_id = COALESCE(@LibraryId, library_id),
                            work_id = COALESCE(@WorkId, work_id),
                            unit_id = COALESCE(@UnitId, unit_id),
                            media_type = @MediaType,
                            title = @Title,
                            year = COALESCE(@Year, year),
                            season_number = COALESCE(@SeasonNumber, season_number),
                            episode_number = COALESCE(@EpisodeNumber, episode_number),
                            size_bytes = CASE WHEN @SizeBytes > 0 THEN @SizeBytes ELSE size_bytes END,
                            resolution = @Resolution,
                            rating = @Rating,
                            genres_json = @GenresJson,
                            tags_json = @TagsJson,
                            monitored = @Monitored,
                            added_at = @AddedAt,
                            last_viewed_at = @LastViewedAt,
                            view_count = @ViewCount,
                            viewed_by_json = @ViewedByJson,
                            active_stream_count = @ActiveStreamCount,
                            seerr_id = COALESCE(@SeerrId, seerr_id),
                            seerr_requested_by = COALESCE(@SeerrRequestedBy, seerr_requested_by),
                            seerr_requested_at = COALESCE(@SeerrRequestedAt, seerr_requested_at),
                            requester_watched = CASE WHEN @RequesterWatched = 1 THEN 1 ELSE requester_watched END,
                            file_path = COALESCE(@FilePath, file_path),
                            video_codec = COALESCE(@VideoCodec, video_codec),
                            view_offset_ms = @ViewOffsetMs,
                            audio_languages_json = CASE WHEN @AudioLanguagesJson = '[]' THEN audio_languages_json ELSE @AudioLanguagesJson END,
                            is_hardlinked = CASE WHEN @IsHardlinked = 1 THEN 1 ELSE is_hardlinked END,
                            custom_format_score = CASE WHEN @CustomFormatScore = 0 THEN custom_format_score ELSE @CustomFormatScore END,
                            classification_kind = COALESCE(@ClassificationKind, classification_kind),
                            is_watchlisted = CASE WHEN @IsWatchlisted = 1 THEN 1 ELSE is_watchlisted END,
                            work_status = COALESCE(@WorkStatus, work_status),
                            poster_url = COALESCE(@PosterUrl, poster_url),
                            updated_at = @Now
                        WHERE id = @Id;
                        """,
                        param,
                        transaction: tx);

                    if (mediaType is "movie" or "show" && req.WorkId.HasValue && !rk.StartsWith("mgr-", StringComparison.Ordinal))
                    {
                        await conn.ExecuteAsync(
                            "UPDATE media_works SET plex_rating_key = @Rk WHERE id = @WorkId AND (plex_rating_key IS NULL OR plex_rating_key = '');",
                            new { Rk = rk, WorkId = req.WorkId.Value },
                            transaction: tx);
                    }

                    return existingId.Value;
                }

                var insertedId = await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO cleanerr_media_items (
                        plex_rating_key, parent_rating_key, grandparent_rating_key,
                        parent_title, grandparent_title, library_id, work_id, unit_id,
                        media_type, title, year, season_number, episode_number,
                        size_bytes, resolution, rating, genres_json, tags_json,
                        monitored, added_at, last_viewed_at, view_count, viewed_by_json,
                        active_stream_count, seerr_id, seerr_requested_by, seerr_requested_at,
                        requester_watched, file_path, video_codec, compression_status,
                        view_offset_ms, audio_languages_json, is_hardlinked, custom_format_score,
                        classification_kind, is_watchlisted, work_status, poster_url, updated_at
                    ) VALUES (
                        @Rk, @ParentRk, @GrandRk,
                        @ParentTitle, @GrandparentTitle, @LibraryId, @WorkId, @UnitId,
                        @MediaType, @Title, @Year, @SeasonNumber, @EpisodeNumber,
                        @SizeBytes, @Resolution, @Rating, @GenresJson, @TagsJson,
                        @Monitored, @AddedAt, @LastViewedAt, @ViewCount, @ViewedByJson,
                        @ActiveStreamCount, @SeerrId, @SeerrRequestedBy, @SeerrRequestedAt,
                        @RequesterWatched, @FilePath, @VideoCodec, 'uncompressed',
                        @ViewOffsetMs, @AudioLanguagesJson, @IsHardlinked, @CustomFormatScore,
                        @ClassificationKind, @IsWatchlisted, @WorkStatus, @PosterUrl, @Now
                    );
                    SELECT last_insert_rowid();
                    """,
                    param,
                    transaction: tx);

                if (mediaType is "movie" or "show" && req.WorkId.HasValue && !rk.StartsWith("mgr-", StringComparison.Ordinal))
                {
                    await conn.ExecuteAsync(
                        "UPDATE media_works SET plex_rating_key = @Rk WHERE id = @WorkId AND (plex_rating_key IS NULL OR plex_rating_key = '');",
                        new { Rk = rk, WorkId = req.WorkId.Value },
                        transaction: tx);
                }

                return insertedId;
            },
            ct);

        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await readConn.QuerySingleAsync<MediaItemRow>("SELECT * FROM cleanerr_media_items WHERE id = @Id;", new { Id = id });
        return MapMediaItem(row);
    }

    public async Task<IReadOnlyList<CleanerrMediaItem>> ListMediaItemsAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<MediaItemRow>("SELECT * FROM cleanerr_media_items ORDER BY id ASC;");
        return rows.Select(MapMediaItem).ToList();
    }

    public async Task<CleanerrMediaListResponse> QueryMediaItemsAsync(
        string? search = null,
        string? mediaType = null,
        long? libraryId = null,
        int page = 1,
        int pageSize = 50,
        string? watched = null,
        string? sort = null,
        CancellationToken ct = default)
    {
        var safePage = Math.Max(1, page);
        var safeSize = Math.Clamp(pageSize, 1, 500);
        var offset = (safePage - 1) * safeSize;

        var conditions = new List<string>
        {
            "LOWER(media_type) NOT IN ('season', 'episode')",
        };
        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(search))
        {
            conditions.Add("(LOWER(title) LIKE @Search OR LOWER(COALESCE(parent_title, '')) LIKE @Search OR LOWER(COALESCE(grandparent_title, '')) LIKE @Search)");
            parameters.Add("Search", $"%{search.Trim().ToLowerInvariant()}%");
        }

        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            var normalizedType = mediaType.Trim().ToLowerInvariant() switch
            {
                "series" or "tv" => "show",
                var t => t,
            };
            conditions.Add("LOWER(media_type) = @MediaType");
            parameters.Add("MediaType", normalizedType);
        }

        if (libraryId.HasValue)
        {
            conditions.Add("library_id = @LibraryId");
            parameters.Add("LibraryId", libraryId.Value);
        }

        if (!string.IsNullOrWhiteSpace(watched))
        {
            switch (watched.Trim().ToLowerInvariant())
            {
                case "watched":
                    conditions.Add("COALESCE(view_count, 0) > 0");
                    break;
                case "unwatched":
                    conditions.Add("COALESCE(view_count, 0) = 0");
                    break;
            }
        }

        var orderBySql = sort?.Trim().ToLowerInvariant() switch
        {
            "title" => "ORDER BY LOWER(title) ASC, id ASC",
            "size_desc" => "ORDER BY size_bytes DESC, id ASC",
            "size_asc" => "ORDER BY size_bytes ASC, id ASC",
            "added_desc" => "ORDER BY added_at DESC, id ASC",
            "added_asc" => "ORDER BY added_at ASC, id ASC",
            "last_watched_asc" => "ORDER BY CASE WHEN last_viewed_at IS NULL OR last_viewed_at = '' THEN 0 ELSE 1 END ASC, last_viewed_at ASC, id ASC",
            "last_watched_desc" => "ORDER BY CASE WHEN last_viewed_at IS NULL OR last_viewed_at = '' THEN 1 ELSE 0 END ASC, last_viewed_at DESC, id ASC",
            _ => "ORDER BY id ASC",
        };

        var whereSql = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        parameters.Add("Limit", safeSize);
        parameters.Add("Offset", offset);

        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(1) FROM cleanerr_media_items {whereSql};", parameters);
        var rows = (await conn.QueryAsync<MediaItemRow>(
            $"SELECT * FROM cleanerr_media_items {whereSql} {orderBySql} LIMIT @Limit OFFSET @Offset;",
            parameters)).ToList();

        var mapped = rows.Select(MapMediaItem).ToList();
        var enriched = await EnrichMediaStatusFlagsAsync(mapped, ct);
        return new CleanerrMediaListResponse(enriched, total, safePage, safeSize);
    }

    public async Task<CleanerrMediaChildrenResponse> GetMediaChildrenAsync(
        long showMediaItemId,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var parentRow = await conn.QuerySingleOrDefaultAsync<MediaItemRow>(
            "SELECT * FROM cleanerr_media_items WHERE id = @Id;",
            new { Id = showMediaItemId })
            ?? throw new KeyNotFoundException($"Cleanerr media item #{showMediaItemId} not found.");

        var parent = MapMediaItem(parentRow);
        var enriched = await EnrichMediaStatusFlagsAsync([parent], ct);
        return new CleanerrMediaChildrenResponse(enriched[0], [], []);
    }

    private async Task<IReadOnlyList<CleanerrMediaItem>> EnrichMediaStatusFlagsAsync(
        IReadOnlyList<CleanerrMediaItem> items,
        CancellationToken ct)
    {
        if (items.Count == 0) return items;

        var allMedia = await ListMediaItemsAsync(ct);
        var byRk = allMedia.ToDictionary(m => m.PlexRatingKey, StringComparer.Ordinal);
        var rules = await ListExclusionRulesAsync(ct);
        var stagedIds = (await ListDeletionQueueAsync("staged", ct))
            .Select(q => q.MediaItemId)
            .ToHashSet();
        var now = DateTimeOffset.UtcNow;

        return items.Select(it =>
        {
            var (isExcl, _) = CheckItemExcluded(it, rules, byRk, null, null, null, now);
            return it with
            {
                IsExcluded = isExcl,
                IsStaged = stagedIds.Contains(it.Id),
            };
        }).ToList();
    }

    public async Task<IReadOnlyList<CleanerrWorkflowSummary>> ListWorkflowsAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<WorkflowRow>("SELECT * FROM cleanerr_workflows ORDER BY id ASC;");
        return rows.Select(MapWorkflow).ToList();
    }

    public async Task<CleanerrWorkflowSummary?> GetWorkflowByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await conn.QuerySingleOrDefaultAsync<WorkflowRow>("SELECT * FROM cleanerr_workflows WHERE id = @Id;", new { Id = id });
        return row is null ? null : MapWorkflow(row);
    }

    public async Task<CleanerrWorkflowSummary> UpsertWorkflowAsync(
        UpsertCleanerrWorkflowRequest request,
        long? id = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Workflow name is required.");
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var editorMode = request.RuleGroup is not null || string.Equals(request.EditorMode, "rules", StringComparison.OrdinalIgnoreCase)
            ? "rules"
            : "dag";
        var effectiveGraph = editorMode == "rules" && request.RuleGroup is not null
            ? CleanerrMaintainerrRuleEngine.CompileRuleGroupToSummaryGraph(request.RuleGroup, request.ScheduleCron)
            : (request.GraphJson ?? new JsonObject { ["nodes"] = new JsonArray(), ["edges"] = new JsonArray() });
        var graphStr = effectiveGraph.ToJsonString();
        var ruleGroupStr = request.RuleGroup is not null
            ? JsonSerializer.Serialize(request.RuleGroup, JsonOpts)
            : null;

        var wfId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                if (id.HasValue)
                {
                    await conn.ExecuteAsync(
                        """
                        UPDATE cleanerr_workflows
                        SET name = @Name,
                            description = @Desc,
                            enabled = @Enabled,
                            dry_run = @DryRun,
                            schedule_cron = @Cron,
                            graph_json = @Graph,
                            editor_mode = @EditorMode,
                            rule_group_json = @RuleGroupJson,
                            updated_at = @Now
                        WHERE id = @Id;
                        """,
                        new
                        {
                            Id = id.Value,
                            Name = request.Name.Trim(),
                            Desc = request.Description,
                            Enabled = request.Enabled ? 1 : 0,
                            DryRun = request.DryRun ? 1 : 0,
                            Cron = request.ScheduleCron,
                            Graph = graphStr,
                            EditorMode = editorMode,
                            RuleGroupJson = ruleGroupStr,
                            Now = now,
                        },
                        transaction: tx);
                    return id.Value;
                }

                return await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO cleanerr_workflows (
                        name, description, enabled, dry_run, schedule_cron, graph_json,
                        editor_mode, rule_group_json, created_at, updated_at
                    ) VALUES (
                        @Name, @Desc, @Enabled, @DryRun, @Cron, @Graph,
                        @EditorMode, @RuleGroupJson, @Now, @Now
                    );
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        Name = request.Name.Trim(),
                        Desc = request.Description,
                        Enabled = request.Enabled ? 1 : 0,
                        DryRun = request.DryRun ? 1 : 0,
                        Cron = request.ScheduleCron,
                        Graph = graphStr,
                        EditorMode = editorMode,
                        RuleGroupJson = ruleGroupStr,
                        Now = now,
                    },
                    transaction: tx);
            },
            ct);

        return (await GetWorkflowByIdAsync(wfId, ct))!;
    }

    public async Task<CleanerrWorkflowSummary> DuplicateWorkflowAsync(
        long id,
        string? newName = null,
        CancellationToken ct = default)
    {
        var existing = await GetWorkflowByIdAsync(id, ct)
            ?? throw new KeyNotFoundException($"Cleanerr workflow #{id} not found.");

        var copyName = !string.IsNullOrWhiteSpace(newName)
            ? newName.Trim()
            : $"{existing.Name} (Copy)";

        return await UpsertWorkflowAsync(
            new UpsertCleanerrWorkflowRequest(
                Name: copyName,
                Description: existing.Description,
                Enabled: false,
                DryRun: existing.DryRun,
                ScheduleCron: existing.ScheduleCron,
                GraphJson: existing.GraphJson.DeepClone().AsObject(),
                EditorMode: existing.EditorMode,
                RuleGroup: existing.RuleGroup),
            id: null,
            ct);
    }

    public async Task DeleteWorkflowAsync(long id, CancellationToken ct = default)
    {
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync("DELETE FROM cleanerr_workflows WHERE id = @Id;", new { Id = id }, transaction: tx),
            ct);
    }

    public async Task<IReadOnlyList<CleanerrWorkflowRunResult>> ListRunsAsync(
        long? workflowId = null,
        int limit = 25,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = workflowId.HasValue
            ? await conn.QueryAsync<RunRow>("SELECT * FROM cleanerr_runs WHERE workflow_id = @WfId ORDER BY id DESC LIMIT @Limit;", new { WfId = workflowId.Value, Limit = limit })
            : await conn.QueryAsync<RunRow>("SELECT * FROM cleanerr_runs ORDER BY id DESC LIMIT @Limit;", new { Limit = limit });

        return rows.Select(r => new CleanerrWorkflowRunResult(
            RunId: r.Id,
            WorkflowId: r.WorkflowId,
            TriggerType: r.TriggerType,
            DryRun: r.DryRun != 0,
            Status: r.Status,
            StartedAt: r.StartedAt,
            FinishedAt: r.FinishedAt,
            Summary: JsonSerializer.Deserialize<CleanerrWorkflowRunSummary>(r.SummaryJson, JsonOpts)!)).ToList();
    }

    public async Task<IReadOnlyList<CleanerrWorkflowRunResult>> EvaluateScheduledWorkflowsAsync(
        DateTimeOffset? referenceNow = null,
        CancellationToken ct = default)
    {
        var now = referenceNow ?? DateTimeOffset.UtcNow;
        var enabledWorkflows = (await ListWorkflowsAsync(ct)).Where(w => w.Enabled).ToList();
        var results = new List<CleanerrWorkflowRunResult>();

        foreach (var wf in enabledWorkflows)
        {
            var lastRunAt = ParseDto(wf.LastRunAt);
            var sinceUtc = lastRunAt
                ?? (ParseDto(wf.CreatedAt) is { } createdAt && createdAt <= now.AddMinutes(1)
                    ? createdAt.AddMinutes(-1)
                    : (DateTimeOffset?)null);
            bool isDue;

            if (!string.IsNullOrWhiteSpace(wf.ScheduleCron))
            {
                if (!CleanerrCronEvaluator.IsValidCron(wf.ScheduleCron))
                {
                    continue;
                }
                isDue = CleanerrCronEvaluator.IsDue(wf.ScheduleCron, sinceUtc, now);
            }
            else
            {
                isDue = !lastRunAt.HasValue || (now - lastRunAt.Value) >= TimeSpan.FromHours(24);
            }

            if (!isDue)
            {
                continue;
            }

            try
            {
                var res = await ExecuteWorkflowAsync(wf.Id, dryRunOverride: null, triggerType: "scheduled", ct: ct);
                results.Add(res);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Keep scheduled evaluation loop resilient across independent workflows
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<CleanerrExclusionRule>> ListExclusionRulesAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<ExclusionRow>("SELECT * FROM cleanerr_exclusion_rules ORDER BY id DESC;");
        return rows.Select(MapExclusion).ToList();
    }

    public async Task<CleanerrExclusionRule> CreateExclusionRuleAsync(
        CreateCleanerrExclusionRequest request,
        string createdBy,
        long? createdByUserId,
        int? exclusionQuotaLimit,
        CancellationToken ct = default)
    {
        await EnforceExclusionQuotaAsync(createdByUserId, exclusionQuotaLimit, 1, ct);

        if (request.RuleType == "media_item" && request.MediaItemId.HasValue)
        {
            await ExcludeMediaIdsWithFamilyEscalationAsync(
                [request.MediaItemId.Value],
                createdBy,
                request.Reason ?? "Excluded manually",
                createdByUserId,
                exclusionQuotaLimit: null,
                request.ExpiresAt,
                ct);
            return (await ListExclusionRulesAsync(ct)).First();
        }

        var val = request.Value?.Trim();
        if (string.IsNullOrWhiteSpace(val))
        {
            throw new ArgumentException("Exclusion rule value is required.");
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var id = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO cleanerr_exclusion_rules (
                    media_item_id, external_id, rule_type, value, granularity,
                    workflow_id, reason, created_by, created_by_user_id, expires_at, created_at
                ) VALUES (
                    @Mid, @ExtId, @RuleType, @Value, @Gran,
                    @WfId, @Reason, @CreatedBy, @UserId, @ExpiresAt, @Now
                );
                SELECT last_insert_rowid();
                """,
                new
                {
                    Mid = request.MediaItemId,
                    ExtId = request.ExternalId,
                    RuleType = request.RuleType.Trim().ToLowerInvariant(),
                    Value = val,
                    Gran = request.Granularity ?? "global",
                    WfId = request.WorkflowId,
                    request.Reason,
                    CreatedBy = createdBy,
                    UserId = createdByUserId,
                    request.ExpiresAt,
                    Now = now,
                },
                transaction: tx),
            ct);

        return (await ListExclusionRulesAsync(ct)).First(r => r.Id == id);
    }

    public async Task<IReadOnlyList<string>> ExcludeMediaIdsWithFamilyEscalationAsync(
        IReadOnlyList<long> mediaIds,
        string createdBy,
        string reason,
        long? createdByUserId = null,
        int? exclusionQuotaLimit = null,
        string? expiresAt = null,
        CancellationToken ct = default)
    {
        if (mediaIds.Count == 0) return [];
        await EnforceExclusionQuotaAsync(createdByUserId, exclusionQuotaLimit, mediaIds.Count, ct);

        var allMedia = await ListMediaItemsAsync(ct);
        var byId = allMedia.ToDictionary(m => m.Id);
        var excludedTitles = new List<string>();
        var seenTargets = new HashSet<long>();
        var now = DateTimeOffset.UtcNow.ToString("O");

        foreach (var mid in mediaIds)
        {
            if (!byId.TryGetValue(mid, out var media)) continue;

            // Whole-series family escalation: excluding any TV episode or season protects the parent Show and rescues all sibling episodes/seasons
            var (targetMedia, familyIds) = ResolveMediaToSeriesFamily(allMedia, media);
            if (!seenTargets.Add(targetMedia.Id)) continue;

            var isTv = media.MediaType is "show" or "season" or "episode" || targetMedia.MediaType == "show";
            var gran = isTv ? "series" : "movie";
            var displayTitle = CleanerrDiscordService.FormatSeriesOrMovieTitle(media);

            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                async (conn, tx) =>
                {
                    var existing = await conn.ExecuteScalarAsync<long?>(
                        "SELECT id FROM cleanerr_exclusion_rules WHERE media_item_id = @Mid AND rule_type = 'media_item';",
                        new { Mid = targetMedia.Id },
                        transaction: tx);

                    if (!existing.HasValue)
                    {
                        await conn.ExecuteAsync(
                            """
                            INSERT INTO cleanerr_exclusion_rules (
                                media_item_id, external_id, rule_type, value, granularity,
                                reason, created_by, created_by_user_id, expires_at, created_at
                            ) VALUES (
                                @Mid, @ExtId, 'media_item', @Val, @Gran,
                                @Reason, @CreatedBy, @UserId, @ExpiresAt, @Now
                            );
                            """,
                            new
                            {
                                Mid = targetMedia.Id,
                                ExtId = targetMedia.PlexRatingKey,
                                Val = displayTitle,
                                Gran = gran,
                                Reason = reason,
                                CreatedBy = createdBy,
                                UserId = createdByUserId,
                                ExpiresAt = expiresAt,
                                Now = now,
                            },
                            transaction: tx);
                    }

                    await conn.ExecuteAsync(
                        """
                        UPDATE cleanerr_deletion_queue
                        SET status = 'excluded',
                            rescued_reason = @Reason,
                            executed_at = @Now
                        WHERE media_item_id IN @FamilyIds AND status = 'staged';
                        """,
                        new { FamilyIds = familyIds, Reason = reason, Now = now },
                        transaction: tx);
                },
                ct);

            excludedTitles.Add(displayTitle);
        }

        return excludedTitles;
    }

    public async Task<int> ExcludeAllStagedAsync(
        long? workflowId,
        string reason,
        long? createdByUserId = null,
        int? exclusionQuotaLimit = null,
        CancellationToken ct = default)
    {
        var staged = await ListDeletionQueueAsync("staged", ct);
        if (workflowId is > 0)
        {
            staged = staged.Where(q => q.WorkflowId == workflowId.Value).ToList();
        }

        var ids = staged.Select(q => q.MediaItemId).Distinct().ToList();
        await ExcludeMediaIdsWithFamilyEscalationAsync(ids, "discord_bulk", reason, createdByUserId, exclusionQuotaLimit, null, ct);
        return staged.Count;
    }

    public async Task DeleteExclusionRuleAsync(long ruleId, CancellationToken ct = default)
    {
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync("DELETE FROM cleanerr_exclusion_rules WHERE id = @Id;", new { Id = ruleId }, transaction: tx),
            ct);
    }

    private async Task EnforceExclusionQuotaAsync(
        long? userId,
        int? exclusionQuotaLimit,
        int requestedIncrement,
        CancellationToken ct)
    {
        if (!userId.HasValue || !exclusionQuotaLimit.HasValue)
        {
            return;
        }

        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var current = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(1) FROM cleanerr_exclusion_rules
            WHERE created_by_user_id = @Uid
              AND (expires_at IS NULL OR expires_at > @Now);
            """,
            new { Uid = userId.Value, Now = DateTimeOffset.UtcNow.ToString("O") });

        if (current + requestedIncrement > exclusionQuotaLimit.Value)
        {
            throw new InvalidOperationException(
                $"Role exclusion quota exceeded ({current}/{exclusionQuotaLimit.Value} active Cleanerr exclusions used).");
        }
    }

    private static (bool Excluded, string? Reason) CheckItemExcluded(
        CleanerrMediaItem item,
        IReadOnlyList<CleanerrExclusionRule> rules,
        IReadOnlyDictionary<string, CleanerrMediaItem> itemsByRk,
        long? workflowId,
        IReadOnlyList<string>? extraExcludedTags,
        IReadOnlyList<string>? extraExcludedUsers,
        DateTimeOffset now,
        bool respectGlobalExclusions = true)
    {
        var ancestors = new List<CleanerrMediaItem> { item };
        if (!string.IsNullOrWhiteSpace(item.ParentRatingKey) && itemsByRk.TryGetValue(item.ParentRatingKey, out var parent))
            ancestors.Add(parent);
        if (!string.IsNullOrWhiteSpace(item.GrandparentRatingKey) && itemsByRk.TryGetValue(item.GrandparentRatingKey, out var gp))
            ancestors.Add(gp);

        var allTags = ancestors.SelectMany(a => a.Tags).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (extraExcludedTags is { Count: > 0 })
        {
            var hitTag = extraExcludedTags.FirstOrDefault(allTags.Contains);
            if (hitTag is not null) return (true, $"Excluded by tag '{hitTag}'");
        }

        if (extraExcludedUsers is { Count: > 0 } && !string.IsNullOrWhiteSpace(item.SeerrRequestedBy) &&
            extraExcludedUsers.Contains(item.SeerrRequestedBy, StringComparer.OrdinalIgnoreCase))
        {
            return (true, $"Excluded by requester '{item.SeerrRequestedBy}'");
        }

        var ancestorIds = ancestors.Select(a => a.Id).ToHashSet();
        var ancestorRks = ancestors.Select(a => a.PlexRatingKey).ToHashSet(StringComparer.Ordinal);
        var ancestorTitles = ancestors.Select(a => a.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            if (!respectGlobalExclusions && !rule.WorkflowId.HasValue)
                continue;
            if (!string.IsNullOrWhiteSpace(rule.ExpiresAt) && ParseDto(rule.ExpiresAt) is { } exp && exp <= now)
                continue;
            if (rule.WorkflowId.HasValue && workflowId.HasValue && rule.WorkflowId.Value != workflowId.Value)
                continue;

            var val = rule.Value.Trim();
            if (rule.RuleType == "media_item")
            {
                if (rule.MediaItemId.HasValue && ancestorIds.Contains(rule.MediaItemId.Value))
                    return (true, $"Excluded by Cleanerr rule #{rule.Id} ({rule.Reason ?? rule.Value})");
                if (!string.IsNullOrWhiteSpace(rule.ExternalId) && ancestorRks.Contains(rule.ExternalId))
                    return (true, $"Excluded by rating key {rule.ExternalId}");
                if (!rule.MediaItemId.HasValue && string.IsNullOrWhiteSpace(rule.ExternalId) && ancestorTitles.Contains(val))
                    return (true, $"Excluded by title rule '{rule.Value}'");
            }
            else if (rule.RuleType is "tag" or "plex_label")
            {
                if (allTags.Contains(val)) return (true, $"Excluded by {rule.RuleType} '{val}'");
            }
            else if (rule.RuleType is "seerr_user" or "plex_user")
            {
                if (ancestors.Any(a =>
                    string.Equals(a.SeerrRequestedBy, val, StringComparison.OrdinalIgnoreCase) ||
                    a.ViewedBy.Contains(val, StringComparer.OrdinalIgnoreCase)))
                {
                    return (true, $"Excluded by user '{val}'");
                }
            }
            else if (rule.RuleType == "library")
            {
                if (item.LibraryId.HasValue && string.Equals(item.LibraryId.Value.ToString(CultureInfo.InvariantCulture), val, StringComparison.Ordinal))
                    return (true, $"Excluded by library rule '{val}'");
            }
        }

        return (false, null);
    }

    private static (CleanerrMediaItem TargetShowOrMovie, IReadOnlyList<long> FamilyIds) ResolveMediaToSeriesFamily(
        IReadOnlyList<CleanerrMediaItem> allMedia,
        CleanerrMediaItem media)
    {
        if (media.MediaType is not ("show" or "season" or "episode"))
        {
            return (media, [media.Id]);
        }

        var showRk = media.MediaType switch
        {
            "show" => media.PlexRatingKey,
            "season" => media.ParentRatingKey ?? media.GrandparentRatingKey,
            _ => media.GrandparentRatingKey,
        };

        if (string.IsNullOrWhiteSpace(showRk) && media.MediaType == "episode" && !string.IsNullOrWhiteSpace(media.ParentRatingKey))
        {
            var parentSeason = allMedia.FirstOrDefault(m => string.Equals(m.PlexRatingKey, media.ParentRatingKey, StringComparison.Ordinal));
            if (parentSeason is not null)
            {
                showRk = parentSeason.MediaType == "show"
                    ? parentSeason.PlexRatingKey
                    : parentSeason.ParentRatingKey ?? parentSeason.GrandparentRatingKey;
            }
        }

        var targetShow = (!string.IsNullOrWhiteSpace(showRk)
                             ? allMedia.FirstOrDefault(m => string.Equals(m.PlexRatingKey, showRk, StringComparison.Ordinal))
                             : null)
                         ?? (media.WorkId.HasValue
                             ? allMedia.FirstOrDefault(m => m.MediaType == "show" && m.WorkId == media.WorkId)
                             : null);

        if (targetShow is null)
        {
            var showTitleFallback = media.MediaType == "show"
                ? media.Title
                : media.GrandparentTitle ?? (media.MediaType == "season" ? media.ParentTitle : null);
            if (!string.IsNullOrWhiteSpace(showTitleFallback))
            {
                targetShow = allMedia.FirstOrDefault(m =>
                    m.MediaType == "show" &&
                    string.Equals(m.Title, showTitleFallback, StringComparison.OrdinalIgnoreCase));
            }
        }

        targetShow ??= media;
        if (string.IsNullOrWhiteSpace(showRk) && targetShow.MediaType == "show")
        {
            showRk = targetShow.PlexRatingKey;
        }

        var seasonRks = !string.IsNullOrWhiteSpace(showRk)
            ? allMedia
                .Where(m => m.MediaType == "season" &&
                            (string.Equals(m.ParentRatingKey, showRk, StringComparison.Ordinal) ||
                             string.Equals(m.GrandparentRatingKey, showRk, StringComparison.Ordinal)))
                .Select(m => m.PlexRatingKey)
                .ToHashSet(StringComparer.Ordinal)
            : [];

        var family = allMedia.Where(m =>
            m.Id == media.Id ||
            m.Id == targetShow.Id ||
            (!string.IsNullOrWhiteSpace(showRk) && (
                string.Equals(m.PlexRatingKey, showRk, StringComparison.Ordinal) ||
                string.Equals(m.ParentRatingKey, showRk, StringComparison.Ordinal) ||
                string.Equals(m.GrandparentRatingKey, showRk, StringComparison.Ordinal))) ||
            (!string.IsNullOrWhiteSpace(m.ParentRatingKey) && seasonRks.Contains(m.ParentRatingKey)) ||
            (media.WorkId.HasValue && m.WorkId == media.WorkId))
            .Select(m => m.Id)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        return (targetShow, family);
    }

    private static string? ResolveExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current)) return current;
                current = Path.GetDirectoryName(current);
            }
        }
        catch
        {
            // Ignore invalid paths
        }
        return null;
    }

    private static DriveInfo? TryGetDriveInfo(string? path)
    {
        var existing = ResolveExistingDirectory(path);
        if (existing is null) return null;
        try
        {
            var di = new DriveInfo(existing);
            _ = di.TotalSize;
            return di;
        }
        catch
        {
            return null;
        }
    }

    private (double FreeGb, double FreePct) InspectDiskSpace(
        double? simFreeGb,
        double? simFreePct,
        IReadOnlyList<LibrarySummary>? libraries = null)
    {
        if (simFreeGb.HasValue || simFreePct.HasValue)
        {
            return (simFreeGb ?? 2000.0, simFreePct ?? 50.0);
        }

        try
        {
            var drivesByMount = new Dictionary<string, DriveInfo>(StringComparer.Ordinal);
            if (libraries is { Count: > 0 })
            {
                foreach (var lib in libraries)
                {
                    var di = TryGetDriveInfo(lib.RootFolderPath);
                    if (di is not null)
                    {
                        drivesByMount.TryAdd(di.Name, di);
                    }
                }
            }

            if (drivesByMount.Count == 0)
            {
                var fallback = TryGetDriveInfo(AppContext.BaseDirectory);
                if (fallback is not null)
                {
                    drivesByMount.TryAdd(fallback.Name, fallback);
                }
            }

            if (drivesByMount.Count > 0)
            {
                long availBytes = 0;
                long totalBytes = 0;
                foreach (var di in drivesByMount.Values)
                {
                    availBytes += di.AvailableFreeSpace;
                    totalBytes += di.TotalSize;
                }

                var freeGb = availBytes / (1024.0 * 1024.0 * 1024.0);
                var pct = totalBytes > 0 ? (double)availBytes / totalBytes * 100.0 : 50.0;
                return (freeGb, pct);
            }
        }
        catch
        {
            // Fallback when DriveInfo cannot inspect filesystem
        }

        return (1000.0, 50.0);
    }

    private static IReadOnlyList<CleanerrLibraryStorageStatDto> BuildLibraryStorageStats(
        IReadOnlyList<LibrarySummary> libraries,
        IReadOnlyList<CleanerrMediaItem> allMedia)
    {
        var result = new List<CleanerrLibraryStorageStatDto>(libraries.Count);
        foreach (var lib in libraries)
        {
            var libItems = allMedia.Where(m => m.LibraryId == lib.Id).ToList();
            var topLevelItems = libItems.Where(m => m.MediaType is "movie" or "show" or "music" or "ebook" or "audiobook").ToList();
            var topLevelCount = topLevelItems.Count;
            var libBytes = topLevelItems.Sum(m => m.SizeBytes);

            double freeGb = 0;
            double totalGb = 0;
            double freePct = 0;
            var di = TryGetDriveInfo(lib.RootFolderPath);
            if (di is not null)
            {
                freeGb = Math.Round(di.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0), 2);
                totalGb = Math.Round(di.TotalSize / (1024.0 * 1024.0 * 1024.0), 2);
                freePct = di.TotalSize > 0 ? Math.Round((double)di.AvailableFreeSpace / di.TotalSize * 100.0, 2) : 0;
            }

            result.Add(new CleanerrLibraryStorageStatDto(
                LibraryId: lib.Id,
                LibraryTitle: lib.Name,
                MediaType: lib.MediaType,
                RootFolder: lib.RootFolderPath,
                ItemCount: topLevelCount,
                LibraryBytes: libBytes,
                DriveFreeGb: freeGb,
                DriveTotalGb: totalGb,
                DriveFreePercent: freePct));
        }

        return result;
    }

    private static IReadOnlyList<CleanerrReclaimForecastBucketDto> ComputeReclaimForecast(
        IReadOnlyList<CleanerrDeletionQueueItem> staged,
        IReadOnlyDictionary<long, CleanerrMediaItem> mediaById,
        DateTimeOffset now)
    {
        var dueNowCount = 0;
        long dueNowBytes = 0;
        var d7Count = 0;
        long d7Bytes = 0;
        var d14Count = 0;
        long d14Bytes = 0;
        var d30Count = 0;
        long d30Bytes = 0;
        var beyondCount = 0;
        long beyondBytes = 0;

        var t7 = now.AddDays(7);
        var t14 = now.AddDays(14);
        var t30 = now.AddDays(30);

        foreach (var q in staged)
        {
            var deleteAt = ParseDto(q.DeleteAt) ?? now;
            var reclaimBytes = (!mediaById.TryGetValue(q.MediaItemId, out var m) || !m.IsHardlinked) ? q.SizeBytes : 0L;

            if (deleteAt <= now)
            {
                dueNowCount++;
                dueNowBytes += reclaimBytes;
            }
            else if (deleteAt <= t7)
            {
                d7Count++;
                d7Bytes += reclaimBytes;
            }
            else if (deleteAt <= t14)
            {
                d14Count++;
                d14Bytes += reclaimBytes;
            }
            else if (deleteAt <= t30)
            {
                d30Count++;
                d30Bytes += reclaimBytes;
            }
            else
            {
                beyondCount++;
                beyondBytes += reclaimBytes;
            }
        }

        return
        [
            new("due_now", "Due Now", dueNowCount, dueNowBytes),
            new("7d", "Within 7 Days", d7Count, d7Bytes),
            new("14d", "8–14 Days", d14Count, d14Bytes),
            new("30d", "15–30 Days", d30Count, d30Bytes),
            new("beyond_30d", "Beyond 30 Days", beyondCount, beyondBytes),
        ];
    }

    private static string? GetString(JsonObject obj, string key) =>
        obj[key]?.GetValueKind() == JsonValueKind.String ? obj[key]!.GetValue<string>() : obj[key]?.ToString();

    private static int? GetInt(JsonObject obj, string key) =>
        obj[key] is { } n && int.TryParse(n.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static long? GetLong(JsonObject obj, string key) =>
        obj[key] is { } n && long.TryParse(n.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? GetDouble(JsonObject obj, string key) =>
        obj[key] is { } n && double.TryParse(n.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static bool? GetBool(JsonObject obj, string key) =>
        obj[key] is { } n && bool.TryParse(n.ToString(), out var v) ? v : null;

    private static IReadOnlyList<string> GetStringList(JsonObject obj, string key) =>
        obj[key] is JsonArray arr ? arr.Select(x => x?.ToString()?.Trim() ?? "").Where(s => s.Length > 0).ToList() : [];

    private static IReadOnlyList<long> GetLongList(JsonObject obj, string key) =>
        obj[key] is JsonArray arr ? arr.Select(x => long.TryParse(x?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1).Where(v => v > 0).ToList() : [];

    private static DateTimeOffset? ParseDto(string? iso) =>
        !string.IsNullOrWhiteSpace(iso) && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto
            : null;

    private static string EnsureTrailingSlash(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static List<string> ResolveTorrentSearchDirectories(string? configuredTorrentPath)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredTorrentPath))
        {
            var trimmed = configuredTorrentPath.Trim();
            if (Directory.Exists(trimmed))
            {
                dirs.Add(trimmed);
            }
        }

        const string legacyTorrentDir = "/downloads/torrents";
        if (Directory.Exists(legacyTorrentDir) &&
            !dirs.Contains(legacyTorrentDir, StringComparer.Ordinal))
        {
            dirs.Add(legacyTorrentDir);
        }

        return dirs;
    }

    private static Dictionary<string, List<(string FullPath, long Length)>> BuildTorrentDownloadFileCache(
        string? configuredTorrentPath = null)
    {
        var cache = new Dictionary<string, List<(string FullPath, long Length)>>(StringComparer.Ordinal);
        var torrentDirs = ResolveTorrentSearchDirectories(configuredTorrentPath);
        if (torrentDirs.Count == 0)
        {
            return cache;
        }

        foreach (var torrentDir in torrentDirs)
        {
            try
            {
                foreach (var p in Directory.EnumerateFiles(torrentDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var fi = new FileInfo(p);
                        if (!fi.Exists) continue;
                        if (!cache.TryGetValue(fi.Name, out var list))
                        {
                            list = [];
                            cache[fi.Name] = list;
                        }
                        list.Add((fi.FullName, fi.Length));
                    }
                    catch
                    {
                        // Ignore unreadable entries
                    }
                }
            }
            catch
            {
                // Ignore filesystem probe errors
            }
        }

        return cache;
    }

    private static bool IsFileHardlinkedOnDisk(
        string? filePath,
        IReadOnlyDictionary<string, List<(string FullPath, long Length)>>? torrentFileCache = null,
        string? configuredTorrentPath = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists) return false;

            if (torrentFileCache is not null)
            {
                return torrentFileCache.TryGetValue(fi.Name, out var cachedMatches) &&
                       cachedMatches.Any(m => !string.Equals(m.FullPath, fi.FullName, StringComparison.Ordinal) && m.Length == fi.Length);
            }

            foreach (var torrentDir in ResolveTorrentSearchDirectories(configuredTorrentPath))
            {
                var matches = Directory.EnumerateFiles(torrentDir, fi.Name, SearchOption.AllDirectories);
                if (matches.Any(p => !string.Equals(Path.GetFullPath(p), fi.FullName, StringComparison.Ordinal) && new FileInfo(p).Length == fi.Length))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore filesystem probe errors
        }

        return false;
    }

    private static string InferClassificationFromTagsAndType(IReadOnlyList<string>? tags, string mediaType)
    {
        var isMovie = string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(mediaType, "movies", StringComparison.OrdinalIgnoreCase);
        var set = new HashSet<string>(tags ?? [], StringComparer.OrdinalIgnoreCase);

        if (set.Contains("anime-movie")) return "anime-movie";
        if (set.Contains("anime-series")) return "anime-series";
        if (set.Contains("cartoon-movie")) return "cartoon-movie";
        if (set.Contains("cartoon-series")) return "cartoon-series";
        if (set.Contains("live-action-movie")) return "live-action-movie";
        if (set.Contains("live-action-series")) return "live-action-series";

        if (set.Contains("anime")) return isMovie ? "anime-movie" : "anime-series";
        if (set.Contains("cartoon")) return isMovie ? "cartoon-movie" : "cartoon-series";
        return isMovie ? "live-action-movie" : "live-action-series";
    }

    private static CleanerrMediaItem MapMediaItem(MediaItemRow r)
    {
        var tags = JsonSerializer.Deserialize<string[]>(r.TagsJson, JsonOpts) ?? [];
        var classification = !string.IsNullOrWhiteSpace(r.ClassificationKind)
            ? r.ClassificationKind
            : InferClassificationFromTagsAndType(tags, r.MediaType);

        return new(
            r.Id, r.PlexRatingKey, r.ParentRatingKey, r.GrandparentRatingKey,
            r.ParentTitle, r.GrandparentTitle, r.LibraryId, r.WorkId, r.UnitId,
            r.MediaType, r.Title, r.Year, r.SeasonNumber, r.EpisodeNumber,
            r.SizeBytes, r.Resolution, r.Rating,
            JsonSerializer.Deserialize<string[]>(r.GenresJson, JsonOpts) ?? [],
            tags,
            r.Monitored != 0, r.AddedAt, r.LastViewedAt, r.ViewCount,
            JsonSerializer.Deserialize<string[]>(r.ViewedByJson, JsonOpts) ?? [],
            r.ActiveStreamCount, r.SeerrId, r.SeerrRequestedBy, r.SeerrRequestedAt,
            r.RequesterWatched != 0, r.UpdatedAt, r.FilePath,
            r.VideoCodec,
            string.IsNullOrWhiteSpace(r.CompressionStatus) ? "uncompressed" : r.CompressionStatus,
            r.ViewOffsetMs,
            JsonSerializer.Deserialize<string[]>(r.AudioLanguagesJson, JsonOpts) ?? [],
            r.IsHardlinked != 0,
            r.CustomFormatScore,
            classification,
            r.IsWatchlisted != 0,
            r.WorkStatus,
            r.PosterUrl);
    }

    private static CleanerrWorkflowSummary MapWorkflow(WorkflowRow r)
    {
        CleanerrRuleGroupSpec? ruleGroup = null;
        if (!string.IsNullOrWhiteSpace(r.RuleGroupJson))
        {
            try
            {
                ruleGroup = JsonSerializer.Deserialize<CleanerrRuleGroupSpec>(r.RuleGroupJson, JsonOpts);
            }
            catch
            {
                ruleGroup = null;
            }
        }

        var mode = !string.IsNullOrWhiteSpace(r.EditorMode) ? r.EditorMode : (ruleGroup is not null ? "rules" : "dag");
        return new(
            r.Id, r.Name, r.Description, r.Enabled != 0, r.DryRun != 0, r.ScheduleCron,
            JsonNode.Parse(r.GraphJson)?.AsObject() ?? new JsonObject(),
            r.LastRunAt, r.LastRunStatus, r.CreatedAt, r.UpdatedAt,
            mode, ruleGroup);
    }

    private static CleanerrDeletionQueueItem MapQueue(QueueRow r) =>
        new(
            r.Id, r.MediaItemId, r.MediaTitle, r.MediaType, r.SizeBytes,
            r.WorkflowId, r.Granularity, r.StagedAt, r.DeleteAt, r.GraceDays,
            r.Status,
            JsonNode.Parse(r.ActionConfigJson)?.AsObject() ?? new JsonObject(),
            r.RescuedReason, r.ExecutedAt,
            JsonNode.Parse(r.LogsJson)?.AsArray() ?? [],
            r.IsManual != 0,
            r.PostponedCount,
            r.MatchedReason,
            r.ClassificationKind,
            r.PosterUrl,
            r.ParentTitle,
            r.GrandparentTitle,
            r.Year,
            r.FinalWarningSentAt,
            r.LastViewedAt,
            r.ViewCount,
            r.SeerrRequestedBy);

    private static CleanerrExclusionRule MapExclusion(ExclusionRow r) =>
        new(
            r.Id, r.MediaItemId, r.ExternalId, r.RuleType, r.Value, r.Granularity,
            r.WorkflowId, r.Reason, r.CreatedBy, r.CreatedByUserId, r.ExpiresAt, r.CreatedAt);

    private sealed class NativeRequestJoinRow
    {
        public long Id { get; init; }
        public long? WorkId { get; init; }
        public string CreatedAt { get; init; } = "";
        public string? Username { get; init; }
    }

    private sealed class MediaItemRow
    {
        public long Id { get; init; }
        public string PlexRatingKey { get; init; } = "";
        public string? ParentRatingKey { get; init; }
        public string? GrandparentRatingKey { get; init; }
        public string? ParentTitle { get; init; }
        public string? GrandparentTitle { get; init; }
        public long? LibraryId { get; init; }
        public long? WorkId { get; init; }
        public long? UnitId { get; init; }
        public string MediaType { get; init; } = "";
        public string Title { get; init; } = "";
        public int? Year { get; init; }
        public int? SeasonNumber { get; init; }
        public int? EpisodeNumber { get; init; }
        public long SizeBytes { get; init; }
        public string? Resolution { get; init; }
        public double? Rating { get; init; }
        public string GenresJson { get; init; } = "[]";
        public string TagsJson { get; init; } = "[]";
        public int Monitored { get; init; }
        public string AddedAt { get; init; } = "";
        public string? LastViewedAt { get; init; }
        public int ViewCount { get; init; }
        public string ViewedByJson { get; init; } = "[]";
        public int ActiveStreamCount { get; init; }
        public long? SeerrId { get; init; }
        public string? SeerrRequestedBy { get; init; }
        public string? SeerrRequestedAt { get; init; }
        public int RequesterWatched { get; init; }
        public string UpdatedAt { get; init; } = "";
        public string? FilePath { get; init; }
        public string? VideoCodec { get; init; }
        public string CompressionStatus { get; init; } = "uncompressed";
        public long ViewOffsetMs { get; init; }
        public string AudioLanguagesJson { get; init; } = "[]";
        public int IsHardlinked { get; init; }
        public int CustomFormatScore { get; init; }
        public string? ClassificationKind { get; init; }
        public int IsWatchlisted { get; init; }
        public string? WorkStatus { get; init; }
        public string? PosterUrl { get; init; }
    }

    private sealed class WorkflowRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public int Enabled { get; init; }
        public int DryRun { get; init; }
        public string? ScheduleCron { get; init; }
        public string GraphJson { get; init; } = "{}";
        public string? EditorMode { get; init; }
        public string? RuleGroupJson { get; init; }
        public string? LastRunAt { get; init; }
        public string? LastRunStatus { get; init; }
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private sealed class RunRow
    {
        public long Id { get; init; }
        public long? WorkflowId { get; init; }
        public string TriggerType { get; init; } = "";
        public int DryRun { get; init; }
        public string Status { get; init; } = "";
        public string StartedAt { get; init; } = "";
        public string FinishedAt { get; init; } = "";
        public string SummaryJson { get; init; } = "{}";
    }

    private sealed class QueueRow
    {
        public long Id { get; init; }
        public long MediaItemId { get; init; }
        public string MediaTitle { get; init; } = "";
        public string MediaType { get; init; } = "";
        public long SizeBytes { get; init; }
        public long? WorkflowId { get; init; }
        public string Granularity { get; init; } = "";
        public string StagedAt { get; init; } = "";
        public string DeleteAt { get; init; } = "";
        public int GraceDays { get; init; }
        public string Status { get; init; } = "";
        public string ActionConfigJson { get; init; } = "{}";
        public string? RescuedReason { get; init; }
        public string? ExecutedAt { get; init; }
        public string LogsJson { get; init; } = "[]";
        public int IsManual { get; init; }
        public int PostponedCount { get; init; }
        public string? MatchedReason { get; init; }
        public string? ClassificationKind { get; init; }
        public string? PosterUrl { get; init; }
        public string? ParentTitle { get; init; }
        public string? GrandparentTitle { get; init; }
        public int? Year { get; init; }
        public string? FinalWarningSentAt { get; init; }
        public string? LastViewedAt { get; init; }
        public int ViewCount { get; init; }
        public string? SeerrRequestedBy { get; init; }
    }

    private sealed class ExclusionRow
    {
        public long Id { get; init; }
        public long? MediaItemId { get; init; }
        public string? ExternalId { get; init; }
        public string RuleType { get; init; } = "";
        public string Value { get; init; } = "";
        public string Granularity { get; init; } = "";
        public long? WorkflowId { get; init; }
        public string? Reason { get; init; }
        public string CreatedBy { get; init; } = "";
        public long? CreatedByUserId { get; init; }
        public string? ExpiresAt { get; init; }
        public string CreatedAt { get; init; } = "";
    }
}
