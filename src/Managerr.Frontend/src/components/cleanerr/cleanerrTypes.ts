export interface CleanerrStatusResponse {
  module: string;
  defaultAccessPolicy: string;
  canRead: boolean;
  canRescue: boolean;
  canExclude: boolean;
  canWriteWorkflows: boolean;
  canExecute: boolean;
  cleanerrExclusionLimit: number | null;
  gatewayConnected?: boolean;
  gatewayBotUsername?: string | null;
  stagedCount?: number;
}

export interface CleanerrLibraryStorageStatDto {
  libraryId: number;
  libraryTitle: string;
  mediaType: string;
  rootFolder: string;
  itemCount: number;
  libraryBytes: number;
  driveFreeGb: number;
  driveTotalGb: number;
  driveFreePercent: number;
}

export interface CleanerrReclaimForecastBucketDto {
  bucketKey: string;
  label: string;
  itemCount: number;
  reclaimableBytes: number;
}

export interface CleanerrOverviewStatsDto {
  totalMediaItems: number;
  totalMediaSizeBytes: number;
  stagedCount: number;
  stagedSizeBytes: number;
  stagedReclaimableBytes: number;
  activeExclusionsCount: number;
  enabledWorkflowsCount: number;
  rescuedTotalCount: number;
  deletedTotalCount: number;
  diskFreeGb: number;
  diskFreePercent: number;
  lastRunAt: string | null;
  discordConfigured: boolean;
  discordGatewayConnected: boolean;
  discordAuthMode: string;
  libraryStorage?: CleanerrLibraryStorageStatDto[] | null;
  reclaimForecast?: CleanerrReclaimForecastBucketDto[] | null;
}

export interface CleanerrRuleFieldDescriptor {
  key: string;
  category: string;
  label: string;
  valueType: 'number' | 'text' | 'boolean' | 'date' | 'list';
  supportedMediaTypes: string[];
  description?: string | null;
  suggestedValues?: string[] | null;
}

export interface CleanerrComparatorDescriptor {
  key: string;
  label: string;
  supportedValueTypes: string[];
  requiresValue: boolean;
}

export interface CleanerrRuleCatalogResponse {
  fields: CleanerrRuleFieldDescriptor[];
  comparators: CleanerrComparatorDescriptor[];
  actionModes: string[];
}

export interface CleanerrNodeCatalogEntry {
  nodeType: string;
  category: 'trigger' | 'source' | 'filter' | 'logic' | 'limit' | 'queue' | 'action' | string;
  label: string;
  description: string;
  color: string;
  defaultConfig: Record<string, unknown>;
}

export interface CleanerrRuleConditionDto {
  operator: 'AND' | 'OR' | null;
  field: string;
  comparator: string;
  value?: string | null;
  secondField?: string | null;
}

export interface CleanerrRuleSectionDto {
  sectionOperator: 'AND' | 'OR';
  rules: CleanerrRuleConditionDto[];
  title?: string | null;
}

export interface CleanerrRuleGroupSpec {
  targetMediaType: 'movie' | 'show';
  libraryIds?: number[] | null;
  graceDays: number;
  actionMode: string;
  sections: CleanerrRuleSectionDto[];
  pruneTorrentClient?: boolean;
  cleanEmptyFolders?: boolean;
  resetRequests?: boolean;
  downgradeProfileId?: number | null;
  maxReclaimGb?: number | null;
  minFreeSpaceGbTrigger?: number | null;
  targetFreeSpaceGbWatermark?: number | null;
  useRules?: boolean;
}

export interface CleanerrWorkflowSummary {
  id: number;
  name: string;
  description: string;
  enabled: boolean;
  dryRun: boolean;
  scheduleCron: string | null;
  graphJson: { nodes: Array<Record<string, unknown>>; edges: Array<Record<string, unknown>> };
  lastRunAt: string | null;
  lastRunStatus: string | null;
  createdAt: string;
  updatedAt: string;
  editorMode: 'rules' | 'dag';
  ruleGroup?: CleanerrRuleGroupSpec | null;
}

export interface CleanerrTemplate {
  slug: string;
  name: string;
  category: string;
  description: string;
  graphJson: { nodes: Array<Record<string, unknown>>; edges: Array<Record<string, unknown>> };
  editorMode?: 'rules' | 'dag';
  ruleGroup?: CleanerrRuleGroupSpec | null;
  defaultScheduleCron?: string | null;
}

export interface CleanerrNodeMetric {
  nodeId: string;
  label: string;
  nodeType: string;
  inputCount: number;
  outputCount: number;
  sizeBytes: number;
  matchedItemIds: number[];
  matchedItemsPreview?: CleanerrMatchedItemPreview[];
  logs: string[];
}

export interface CleanerrMatchedItemPreview {
  id: number;
  title: string;
  mediaType: string;
  sizeBytes: number;
  year?: number | null;
  parentTitle?: string | null;
  resolution?: string | null;
  lastViewedAt?: string | null;
  isHardlinked?: boolean;
  classificationKind?: string | null;
  matchedReason?: string | null;
  reason?: string | null;
}

export interface CleanerrRunResult {
  runId: number | null;
  workflowId?: number | null;
  triggerType?: string;
  dryRun: boolean;
  status?: string;
  startedAt?: string;
  finishedAt?: string;
  summary: {
    nodeMetrics: Record<string, CleanerrNodeMetric>;
    stagedCount: number;
    deletedCount: number;
    spaceBytes: number;
    reclaimableSpaceBytes?: number;
    hardlinkedCount?: number;
    downgradedCount?: number;
    logs: string[];
    matchedItems?: CleanerrMatchedItemPreview[];
  };
}

export interface CleanerrRuleEvaluationTraceItem {
  sectionIndex: number;
  ruleIndex: number;
  sectionConnector: string;
  ruleConnector: string | null;
  fieldKey: string;
  fieldLabel: string;
  actualValue: string | null;
  comparator: string;
  expectedTarget: string | null;
  isPropertyComparison: boolean;
  passed: boolean;
  explanation: string;
}

export interface CleanerrSectionEvaluationTrace {
  sectionIndex: number;
  sectionOperator: string;
  sectionPassed: boolean;
  rules: CleanerrRuleEvaluationTraceItem[];
}

export interface CleanerrTestMediaResult {
  mediaItemId: number;
  title: string;
  mediaType: string;
  classificationKind: string | null;
  wouldMatch: boolean;
  isExcluded: boolean;
  exclusionReason: string | null;
  isCurrentlyStaged: boolean;
  stagedDeleteAt: string | null;
  sections: CleanerrSectionEvaluationTrace[];
  guardNotes: string[];
  summary: string;
}

export interface CleanerrMediaItemDto {
  id: number;
  plexRatingKey: string;
  parentRatingKey?: string | null;
  grandparentRatingKey?: string | null;
  libraryId?: number | null;
  mediaType: string;
  title: string;
  year: number | null;
  seasonNumber?: number | null;
  episodeNumber?: number | null;
  sizeBytes: number;
  resolution: string | null;
  rating?: number | null;
  genres?: string[];
  tags: string[];
  monitored?: boolean;
  addedAt?: string;
  lastViewedAt: string | null;
  viewCount: number;
  viewedBy?: string[];
  activeStreamCount: number;
  seerrRequestedBy?: string | null;
  seerrRequestedAt?: string | null;
  filePath: string | null;
  videoCodec?: string | null;
  viewOffsetMs?: number;
  audioLanguages?: string[];
  isHardlinked?: boolean;
  customFormatScore?: number;
  parentTitle?: string | null;
  grandparentTitle?: string | null;
  classificationKind?: string | null;
  isWatchlisted?: boolean;
  workStatus?: string | null;
  posterUrl?: string | null;
  isExcluded?: boolean;
  isStaged?: boolean;
}

export interface CleanerrMediaListResponse {
  items: CleanerrMediaItemDto[];
  total: number;
  page: number;
  pageSize: number;
}

export interface CleanerrMediaChildrenResponse {
  parent: CleanerrMediaItemDto;
  seasons: CleanerrMediaItemDto[];
  episodes: CleanerrMediaItemDto[];
}

export interface CleanerrQueueItem {
  id: number;
  mediaItemId: number;
  mediaTitle: string;
  mediaType: string;
  sizeBytes: number;
  workflowId: number | null;
  granularity: string;
  stagedAt: string;
  deleteAt: string;
  graceDays: number;
  status: string;
  rescuedReason: string | null;
  executedAt: string | null;
  isManual?: boolean;
  postponedCount?: number;
  matchedReason?: string | null;
  classificationKind?: string | null;
  posterUrl?: string | null;
  parentTitle?: string | null;
  grandparentTitle?: string | null;
  year?: number | null;
  finalWarningSentAt?: string | null;
  lastViewedAt?: string | null;
  viewCount?: number;
  seerrRequestedBy?: string | null;
}

export interface CleanerrExclusionRule {
  id: number;
  mediaItemId?: number | null;
  externalId?: string | null;
  ruleType: string;
  value: string;
  granularity: string;
  reason: string | null;
  createdBy: string;
  expiresAt: string | null;
  workflowId?: number | null;
  createdAt?: string;
}

export interface CleanerrDiscordGuildOption {
  id: string;
  name: string;
  isOwner: boolean;
}

export interface CleanerrDiscordChannelOption {
  id: string;
  guildId: string;
  name: string;
  type: number;
}

export interface CleanerrDiscordDiscoveryResult {
  ok: boolean;
  botUserId?: string | null;
  botUsername?: string | null;
  guilds: CleanerrDiscordGuildOption[];
  channels: CleanerrDiscordChannelOption[];
  error?: string | null;
}

export interface CleanerrDiscordSettings {
  webhookUrl: string;
  hasBotToken: boolean;
  maskedBotToken: string;
  channelId: string;
  applicationId: string;
  publicKeyHex: string;
  publicBaseUrl: string;
  gatewayPassiveShadowMode: boolean;
  gatewayConnected: boolean;
  guildId?: string;
  authMode?: 'channel_members' | 'discord_roles' | 'linked_plex_rbac';
  allowedRoleIds?: string[];
  notifyOnStaged?: boolean;
  notifyOnRescued?: boolean;
  notifyOnDeleted?: boolean;
  notifyOnFinalWarning?: boolean;
  finalWarningHours?: number;
  gatewayBotUsername?: string | null;
  gatewayLastError?: string | null;
  webhookBotUsername?: string;
}

export interface CleanerrGeneralSettings {
  defaultGraceDays: number;
  seerrUrl: string;
  hasSeerrApiKey: boolean;
  maskedSeerrApiKey: string;
  finalWarningHours: number;
  defaultPostponeDays?: number;
  torrentDownloadsPath?: string;
}

export interface LibraryOption {
  id: number;
  name: string;
  mediaType?: string;
  mediaVertical?: string;
  libraryType?: string;
}

export interface CleanerrLiveImpactPreview {
  workflowId: number | null;
  workflowName: string;
  currentlyDryRun: boolean;
  itemCount: number;
  totalBytes: number;
  totalGigabytes: number;
  affectedSeriesCount: number;
  affectedMovieCount: number;
  affectedWorkCount: number;
  unwatchedCount: number;
  otherUsersRequestedCount: number;
  requiresNameConfirmation: boolean;
  matchedItems: Array<{
    id: number;
    title: string;
    mediaType: string;
    sizeBytes: number;
    year?: number | null;
    parentTitle?: string | null;
    reason?: string | null;
  }>;
}
