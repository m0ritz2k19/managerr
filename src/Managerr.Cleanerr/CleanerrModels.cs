using System.Text.Json.Nodes;

namespace Managerr.Cleanerr;

public sealed record CleanerrMediaItem(
    long Id,
    string PlexRatingKey,
    string? ParentRatingKey,
    string? GrandparentRatingKey,
    string? ParentTitle,
    string? GrandparentTitle,
    long? LibraryId,
    long? WorkId,
    long? UnitId,
    string MediaType, // "movie" | "show" | "season" | "episode" | "music" | "ebook" | "audiobook"
    string Title,
    int? Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    long SizeBytes,
    string? Resolution,
    double? Rating,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Tags,
    bool Monitored,
    string AddedAt,
    string? LastViewedAt,
    int ViewCount,
    IReadOnlyList<string> ViewedBy,
    int ActiveStreamCount,
    long? SeerrId,
    string? SeerrRequestedBy,
    string? SeerrRequestedAt,
    bool RequesterWatched,
    string UpdatedAt,
    string? FilePath = null,
    string? VideoCodec = null,
    string CompressionStatus = "uncompressed",
    long ViewOffsetMs = 0,
    IReadOnlyList<string>? AudioLanguages = null,
    bool IsHardlinked = false,
    int CustomFormatScore = 0,
    string? ClassificationKind = null,
    bool IsWatchlisted = false,
    string? WorkStatus = null,
    string? PosterUrl = null,
    bool IsExcluded = false,
    bool IsStaged = false);

public sealed record CleanerrMediaListResponse(
    IReadOnlyList<CleanerrMediaItem> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record CleanerrMediaChildrenResponse(
    CleanerrMediaItem Parent,
    IReadOnlyList<CleanerrMediaItem> Seasons,
    IReadOnlyList<CleanerrMediaItem> Episodes);

public sealed record UpsertCleanerrMediaItemRequest(
    string PlexRatingKey,
    string MediaType,
    string Title,
    string? ParentRatingKey = null,
    string? GrandparentRatingKey = null,
    string? ParentTitle = null,
    string? GrandparentTitle = null,
    long? LibraryId = null,
    long? WorkId = null,
    long? UnitId = null,
    int? Year = null,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    long SizeBytes = 0,
    string? Resolution = "1080p",
    double? Rating = null,
    IReadOnlyList<string>? Genres = null,
    IReadOnlyList<string>? Tags = null,
    bool Monitored = true,
    string? AddedAt = null,
    string? LastViewedAt = null,
    int ViewCount = 0,
    IReadOnlyList<string>? ViewedBy = null,
    int ActiveStreamCount = 0,
    long? SeerrId = null,
    string? SeerrRequestedBy = null,
    string? SeerrRequestedAt = null,
    bool RequesterWatched = false,
    string? FilePath = null,
    string? VideoCodec = null,
    string? CompressionStatus = null,
    long ViewOffsetMs = 0,
    IReadOnlyList<string>? AudioLanguages = null,
    bool IsHardlinked = false,
    int CustomFormatScore = 0,
    string? ClassificationKind = null,
    bool IsWatchlisted = false,
    string? WorkStatus = null,
    string? PosterUrl = null);

public sealed record SyncCleanerrPlexRequest(
    string? PlexServerUrl = null,
    string? PlexToken = null);

public sealed record CleanerrPlexSyncResult(
    string ServerUrl,
    int SectionsScanned,
    int MoviesSynced,
    int ShowsSynced,
    int SeasonsSynced,
    int EpisodesSynced,
    int ActiveStreamsDetected);

public sealed record CleanerrRuleConditionDto(
    string? Operator,
    string Field,
    string Comparator,
    string? Value = null,
    string? SecondField = null);

public sealed record CleanerrRuleSectionDto(
    string SectionOperator,
    IReadOnlyList<CleanerrRuleConditionDto> Rules,
    string? Title = null);

public sealed record CleanerrRuleGroupSpec(
    string TargetMediaType, // "movie" | "show"
    IReadOnlyList<long>? LibraryIds,
    int GraceDays,
    string ActionMode, // "delete_files_and_unmonitor" | "unmonitor_and_delete_existing" | "delete_work_and_files" | "delete_show_if_empty" | "unmonitor_show_if_empty" | "downgrade_profile" | "add_import_exclusion"
    IReadOnlyList<CleanerrRuleSectionDto> Sections,
    bool PruneTorrentClient = false,
    bool CleanEmptyFolders = true,
    bool ResetRequests = true,
    long? DowngradeProfileId = null,
    double? MaxReclaimGb = null,
    double? MinFreeSpaceGbTrigger = null,
    double? TargetFreeSpaceGbWatermark = null,
    bool UseRules = true);

public sealed record CleanerrRuleFieldDescriptor(
    string Key,
    string Category,
    string Label,
    string ValueType, // "number" | "text" | "boolean" | "date" | "list"
    IReadOnlyList<string> SupportedMediaTypes,
    string? Description = null,
    IReadOnlyList<string>? SuggestedValues = null);

public sealed record CleanerrComparatorDescriptor(
    string Key,
    string Label,
    IReadOnlyList<string> SupportedValueTypes,
    bool RequiresValue = true);

public sealed record CleanerrRuleCatalogResponse(
    IReadOnlyList<CleanerrRuleFieldDescriptor> Fields,
    IReadOnlyList<CleanerrComparatorDescriptor> Comparators,
    IReadOnlyList<string> ActionModes);

public sealed record CleanerrNodeCatalogEntry(
    string NodeType,
    string Category,
    string Label,
    string Description,
    string Color,
    JsonObject DefaultConfig);

public sealed record TestCleanerrMediaRequest(
    long MediaItemId,
    CleanerrRuleGroupSpec? RuleGroup = null,
    JsonObject? GraphJson = null,
    long? WorkflowId = null,
    double? SimulatedFreeSpaceGb = null,
    double? SimulatedFreeSpacePercent = null);

public sealed record CleanerrRuleEvaluationTraceItem(
    int SectionIndex,
    int RuleIndex,
    string SectionConnector,
    string? RuleConnector,
    string FieldKey,
    string FieldLabel,
    string? ActualValue,
    string Comparator,
    string? ExpectedTarget,
    bool IsPropertyComparison,
    bool Passed,
    string Explanation);

public sealed record CleanerrSectionEvaluationTrace(
    int SectionIndex,
    string SectionOperator,
    bool SectionPassed,
    IReadOnlyList<CleanerrRuleEvaluationTraceItem> Rules);

public sealed record CleanerrTestMediaResult(
    long MediaItemId,
    string Title,
    string MediaType,
    string? ClassificationKind,
    bool WouldMatch,
    bool IsExcluded,
    string? ExclusionReason,
    bool IsCurrentlyStaged,
    string? StagedDeleteAt,
    IReadOnlyList<CleanerrSectionEvaluationTrace> Sections,
    IReadOnlyList<string> GuardNotes,
    string Summary);

public sealed record CleanerrLibraryStorageStatDto(
    long LibraryId,
    string LibraryTitle,
    string MediaType,
    string RootFolder,
    int ItemCount,
    long LibraryBytes,
    double DriveFreeGb,
    double DriveTotalGb,
    double DriveFreePercent);

public sealed record CleanerrReclaimForecastBucketDto(
    string BucketKey,
    string Label,
    int ItemCount,
    long ReclaimableBytes);

public sealed record CleanerrOverviewStatsDto(
    int TotalMediaItems,
    long TotalMediaSizeBytes,
    int StagedCount,
    long StagedSizeBytes,
    long StagedReclaimableBytes,
    int ActiveExclusionsCount,
    int EnabledWorkflowsCount,
    int RescuedTotalCount,
    int DeletedTotalCount,
    double DiskFreeGb,
    double DiskFreePercent,
    string? LastRunAt,
    bool DiscordConfigured,
    bool DiscordGatewayConnected,
    string DiscordAuthMode,
    IReadOnlyList<CleanerrLibraryStorageStatDto>? LibraryStorage = null,
    IReadOnlyList<CleanerrReclaimForecastBucketDto>? ReclaimForecast = null);

public sealed record CleanerrWorkflowSummary(
    long Id,
    string Name,
    string Description,
    bool Enabled,
    bool DryRun,
    string? ScheduleCron,
    JsonObject GraphJson,
    string? LastRunAt,
    string? LastRunStatus,
    string CreatedAt,
    string UpdatedAt,
    string EditorMode = "dag",
    CleanerrRuleGroupSpec? RuleGroup = null);

public sealed record UpsertCleanerrWorkflowRequest(
    string Name,
    string Description = "",
    bool Enabled = true,
    bool DryRun = true,
    string? ScheduleCron = null,
    JsonObject? GraphJson = null,
    string? EditorMode = null,
    CleanerrRuleGroupSpec? RuleGroup = null,
    string? ConfirmWorkflowName = null);

public sealed record DuplicateCleanerrWorkflowRequest(
    string? Name = null);

public sealed record PreviewCleanerrLiveImpactRequest(
    JsonObject? GraphJson = null,
    CleanerrRuleGroupSpec? RuleGroup = null,
    string? WorkflowName = null,
    string? ActorUsername = null,
    double? SimulatedFreeSpaceGb = null,
    double? SimulatedFreeSpacePercent = null);

public sealed record CleanerrLiveImpactPreview(
    long? WorkflowId,
    string WorkflowName,
    bool CurrentlyDryRun,
    int ItemCount,
    long TotalBytes,
    double TotalGigabytes,
    int AffectedSeriesCount,
    int AffectedMovieCount,
    int AffectedWorkCount,
    int UnwatchedCount,
    int OtherUsersRequestedCount,
    bool RequiresNameConfirmation,
    IReadOnlyList<CleanerrMatchedItemPreview> MatchedItems);

public sealed record CleanerrWorkflowTemplate(
    string Slug,
    string Name,
    string Category,
    string Description,
    JsonObject GraphJson,
    string EditorMode = "dag",
    CleanerrRuleGroupSpec? RuleGroup = null,
    string? DefaultScheduleCron = null);

public sealed record CleanerrMatchedItemPreview(
    long Id,
    string Title,
    string MediaType,
    int? Year,
    long SizeBytes,
    string? Resolution,
    string? LastViewedAt,
    bool IsHardlinked = false,
    IReadOnlyList<string>? AudioLanguages = null,
    long ViewOffsetMs = 0,
    int CustomFormatScore = 0,
    string? ClassificationKind = null,
    string? MatchedReason = null,
    string? ParentTitle = null,
    string? Reason = null);

public sealed record CleanerrNodeExecutionMetric(
    string NodeId,
    string Label,
    string NodeType,
    int InputCount,
    int OutputCount,
    long SizeBytes,
    IReadOnlyList<CleanerrMatchedItemPreview> MatchedItemsPreview,
    IReadOnlyList<long> MatchedItemIds,
    IReadOnlyList<string> Logs);

public sealed record CleanerrWorkflowRunSummary(
    IReadOnlyDictionary<string, CleanerrNodeExecutionMetric> NodeMetrics,
    int StagedCount,
    int DeletedCount,
    long SpaceBytes,
    IReadOnlyList<string> Logs,
    bool DryRun,
    string TriggerType,
    long ReclaimableSpaceBytes = 0,
    int HardlinkedCount = 0,
    int DowngradedCount = 0,
    IReadOnlyList<CleanerrMatchedItemPreview>? MatchedItems = null);

public sealed record CleanerrWorkflowRunResult(
    long? RunId,
    long? WorkflowId,
    string TriggerType,
    bool DryRun,
    string Status,
    string StartedAt,
    string FinishedAt,
    CleanerrWorkflowRunSummary Summary);

public sealed record SimulateCleanerrWorkflowRequest(
    JsonObject? GraphJson = null,
    long? WorkflowId = null,
    double? SimulatedFreeSpaceGb = null,
    double? SimulatedFreeSpacePercent = null,
    CleanerrRuleGroupSpec? RuleGroup = null);

public sealed record CleanerrDeletionQueueItem(
    long Id,
    long MediaItemId,
    string MediaTitle,
    string MediaType,
    long SizeBytes,
    long? WorkflowId,
    string Granularity,
    string StagedAt,
    string DeleteAt,
    int GraceDays,
    string Status,
    JsonObject ActionConfigJson,
    string? RescuedReason,
    string? ExecutedAt,
    JsonArray LogsJson,
    bool IsManual = false,
    int PostponedCount = 0,
    string? MatchedReason = null,
    string? ClassificationKind = null,
    string? PosterUrl = null,
    string? ParentTitle = null,
    string? GrandparentTitle = null,
    int? Year = null,
    string? FinalWarningSentAt = null,
    string? LastViewedAt = null,
    int ViewCount = 0,
    string? SeerrRequestedBy = null);

public sealed record PostponeCleanerrQueueRequest(
    int? Days = null,
    string? Reason = null);

public sealed record ManualStageCleanerrItemsRequest(
    IReadOnlyList<long> MediaItemIds,
    int GraceDays = 7,
    string ActionMode = "delete_files_and_unmonitor",
    string? Reason = null);

public sealed record BulkQueueExcludeRequest(
    IReadOnlyList<long>? QueueIds = null,
    string? Reason = "Bulk excluded from Leaving Soon queue");

public sealed record ImportCleanerrRuleYamlRequest(
    string Yaml,
    bool? DryRun = true,
    bool? Enabled = false,
    string? ConfirmWorkflowName = null);

public sealed record ExportCleanerrRuleYamlResponse(
    long WorkflowId,
    string Name,
    string Yaml);

public sealed record CleanerrQueueProcessResult(
    int Evaluated,
    int RescuedCount,
    int DeletedCount,
    int DeferredActiveStreamCount,
    long ReclaimedBytes,
    IReadOnlyList<object> Details,
    int FinalWarningsSent = 0);

public sealed record CleanerrExclusionRule(
    long Id,
    long? MediaItemId,
    string? ExternalId,
    string RuleType, // "media_item" | "tag" | "plex_label" | "plex_user" | "seerr_user" | "library"
    string Value,
    string Granularity, // "movie" | "series" | "global"
    long? WorkflowId,
    string? Reason,
    string CreatedBy,
    long? CreatedByUserId,
    string? ExpiresAt,
    string CreatedAt);

public sealed record CreateCleanerrExclusionRequest(
    long? MediaItemId = null,
    string? ExternalId = null,
    string RuleType = "media_item",
    string? Value = null,
    string? Granularity = null,
    long? WorkflowId = null,
    string? Reason = null,
    string? ExpiresAt = null);

public sealed record BulkExcludeCleanerrItemsRequest(
    IReadOnlyList<long> MediaItemIds,
    string? Reason = "Bulk excluded from simulation preview",
    string? ExpiresAt = null,
    long? WorkflowId = null);

public sealed record CleanerrGeneralSettings(
    int DefaultGraceDays,
    string SeerrUrl,
    bool HasSeerrApiKey,
    string MaskedSeerrApiKey,
    int FinalWarningHours,
    int DefaultPostponeDays = 7,
    string TorrentDownloadsPath = "/data/downloads");

public sealed record UpdateCleanerrGeneralSettingsRequest(
    int? DefaultGraceDays = null,
    string? SeerrUrl = null,
    string? SeerrApiKey = null,
    int? FinalWarningHours = null,
    int? DefaultPostponeDays = null,
    string? TorrentDownloadsPath = null);

public sealed record CleanerrDiscordGuildOption(
    string Id,
    string Name,
    bool IsOwner);

public sealed record CleanerrDiscordChannelOption(
    string Id,
    string GuildId,
    string Name,
    int Type);

public sealed record CleanerrDiscordDiscoveryResult(
    bool Ok,
    string? BotUserId,
    string? BotUsername,
    IReadOnlyList<CleanerrDiscordGuildOption> Guilds,
    IReadOnlyList<CleanerrDiscordChannelOption> Channels,
    string? Error = null);

public sealed record CleanerrDiscordSettings(
    string WebhookUrl,
    bool HasBotToken,
    string MaskedBotToken,
    string ChannelId,
    string ApplicationId,
    string PublicKeyHex,
    string PublicBaseUrl,
    bool GatewayPassiveShadowMode,
    bool GatewayConnected,
    string GuildId = "",
    string AuthMode = "channel_members", // "channel_members" | "discord_roles" | "linked_plex_rbac"
    IReadOnlyList<string>? AllowedRoleIds = null,
    bool NotifyOnStaged = true,
    bool NotifyOnRescued = false,
    bool NotifyOnDeleted = true,
    bool NotifyOnFinalWarning = true,
    int FinalWarningHours = 24,
    string? GatewayBotUsername = null,
    string? GatewayLastError = null,
    string WebhookBotUsername = "Cleanerr");

public sealed record UpdateCleanerrDiscordSettingsRequest(
    string? WebhookUrl = null,
    string? BotToken = null,
    string? ChannelId = null,
    string? ApplicationId = null,
    string? PublicKeyHex = null,
    string? PublicBaseUrl = null,
    bool? GatewayPassiveShadowMode = null,
    string? GuildId = null,
    string? AuthMode = null,
    IReadOnlyList<string>? AllowedRoleIds = null,
    bool? NotifyOnStaged = null,
    bool? NotifyOnRescued = null,
    bool? NotifyOnDeleted = null,
    bool? NotifyOnFinalWarning = null,
    int? FinalWarningHours = null,
    string? WebhookBotUsername = null);

public sealed record CleanerrDiscordCommandSyncResult(
    bool Ok,
    string Scope,
    int CommandsRegistered,
    string Message);
