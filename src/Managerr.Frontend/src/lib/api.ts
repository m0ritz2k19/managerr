export interface AuthStatus {
  isClaimed: boolean;
  boundServerName: string | null;
  boundMachineId: string | null;
  plexClientIdentifier: string;
  authMode: string;
}

export interface RoleDefinition {
  id: number;
  key: string;
  name: string;
  description: string;
  color: string;
  isSystemPreset: boolean;
  isImmutable: boolean;
  scopes: string[];
  libraryAccessMode: 'inherit_from_plex' | 'explicit_libraries' | 'all_libraries';
  allowedLibraryIds: number[];
  autoApproveRequests: boolean;
  movieRequestQuota: number | null;
  tvSeasonRequestQuota: number | null;
  musicBookRequestQuota: number | null;
  cleanerrExclusionLimit?: number | null;
  assignedUserCount: number;
  createdAt: string;
  updatedAt: string;
}

export interface ScopeDescriptor {
  scope: string;
  category: string;
  label: string;
  description: string;
}

export interface EffectiveUserPermissions {
  hasAllLibrariesAccess: boolean;
  sharedPlexSectionIds: string[];
  explicitAllowedLibraryIds: number[];
  scopes: string[];
  autoApproveRequests: boolean;
  movieRequestQuota: number | null;
  tvSeasonRequestQuota: number | null;
  musicBookRequestQuota: number | null;
  cleanerrExclusionLimit?: number | null;
}

export interface PlexUserSummary {
  id: number;
  plexUserId: number;
  plexUuid: string;
  username: string;
  email: string | null;
  avatarUrl: string | null;
  isServerOwner: boolean;
  isHomeUser: boolean;
  isActive: boolean;
  hasBreakGlassPassword: boolean;
  sharedPlexSectionIds: string[];
  preferredAudioLang: string | null;
  preferredSubLang: string | null;
  discordUserId: string | null;
  discordUsername: string | null;
  seerrUserId: number | null;
  roles: RoleDefinition[];
  effectivePermissions: EffectiveUserPermissions;
  lastLoginAt: string | null;
  createdAt: string;
  authProvider?: 'local' | 'oidc' | 'plex' | 'passkey' | string;
  providers?: string[];
  passkeyUserId?: string | null;
  displayName?: string | null;
  hasPasskeys?: boolean;
  passkeyCount?: number;
}

export interface MeResponse {
  user: PlexUserSummary | null;
  csrfToken: string | null;
  scopes: string[];
  isCookieSession: boolean;
  apiKeyPrefix: string | null;
}

export interface PlexPinInfo {
  pinId: number;
  code: string;
  authUrl: string;
  clientIdentifier: string;
}

export interface PlexOwnedServerOption {
  name: string;
  machineIdentifier: string;
  owned: boolean;
  connections: { uri: string; directUrl: string; local: boolean; protocol: string }[];
}

export interface PlexPollResponse {
  claimed: boolean;
  requiresServerSelection: boolean;
  authenticated: boolean;
  csrfToken?: string;
  sessionToken?: string;
  expiresAt?: string;
  user?: PlexUserSummary;
  availableOwnedServers?: PlexOwnedServerOption[];
}

export interface DatabaseStorageInfo {
  kind: string;
  filePath: string;
  sizeBytes: number;
  walSizeBytes: number;
  journalMode: string;
  schemaVersion: number;
}

export interface StorageOverview {
  configDirectory: string;
  backupsDirectory: string;
  envelopeMasterKeyId: string;
  externalDatabaseRequired: boolean;
  databases: DatabaseStorageInfo[];
}

export interface BackupFileMetadata {
  fileName: string;
  fullPath: string;
  sizeBytes: number;
  createdAtUtc: string;
}

export interface AuditEventEntry {
  id: number;
  occurredAt: string;
  actorUserId: number | null;
  actorPlexUserId: number | null;
  actorUsername: string | null;
  actorApiKeyPrefix: string | null;
  ipAddress: string | null;
  action: string;
  resourceType: string;
  resourceId: string | null;
  detailsJson: string;
  traceId: string | null;
}

export interface ApiKeySummary {
  id: number;
  ownerUserId: number;
  name: string;
  publicPrefix: string;
  maskedHint: string;
  scopes: string[];
  allowedLibraryIds: number[];
  allowedCidrs: string[];
  expiresAt: string | null;
  lastUsedAt: string | null;
  revokedAt: string | null;
  createdAt: string;
}

export interface CreatedApiKeyResult {
  id: number;
  name: string;
  publicPrefix: string;
  rawTokenOnce: string;
  maskedHint: string;
  scopes: string[];
  allowedLibraryIds: number[];
  allowedCidrs: string[];
  expiresAt: string | null;
}

export interface CatalogFtsDocument {
  id: number;
  mediaType: string;
  title: string;
  altTitles: string;
  year: number | null;
  externalIds: string;
}

let csrfTokenCache: string | null = null;

export function setCsrfToken(token: string | null) {
  csrfTokenCache = token;
}

export class ApiError extends Error {
  status: number;
  code: string;

  constructor(status: number, code: string, message: string) {
    super(message);
    this.status = status;
    this.code = code;
  }
}

export async function apiFetch<T>(path: string, init?: RequestInit): Promise<T> {
  const method = (init?.method ?? 'GET').toUpperCase();
  const headers = new Headers(init?.headers);
  if (init?.body && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json');
  }
  if (csrfTokenCache && ['POST', 'PUT', 'PATCH', 'DELETE'].includes(method)) {
    headers.set('X-Managerr-Csrf', csrfTokenCache);
  }

  const resp = await fetch(path, {
    ...init,
    method,
    headers,
    credentials: 'include',
  });

  if (resp.status === 204) {
    return undefined as T;
  }

  const contentType = resp.headers.get('content-type') ?? '';
  const isJson = contentType.includes('json');
  const body = isJson ? await resp.json() : await resp.text();

  if (!resp.ok) {
    const detail = isJson ? body.detail ?? body.title ?? 'Request failed' : String(body);
    const code = isJson ? body.code ?? 'ERR_HTTP' : 'ERR_HTTP';
    throw new ApiError(resp.status, code, detail);
  }

  return body as T;
}

export function formatBytes(bytes: number): string {
  const sign = bytes < 0 ? '-' : '';
  const abs = Math.abs(bytes);
  if (abs < 1024) return `${sign}${abs} B`;
  if (abs < 1024 * 1024) return `${sign}${(abs / 1024).toFixed(1)} KB`;
  if (abs < 1024 * 1024 * 1024) return `${sign}${(abs / (1024 * 1024)).toFixed(2)} MB`;
  if (abs < 1024 * 1024 * 1024 * 1024) return `${sign}${(abs / (1024 * 1024 * 1024)).toFixed(2)} GB`;
  return `${sign}${(abs / (1024 * 1024 * 1024 * 1024)).toFixed(2)} TB`;
}

export function formatIntervalSeconds(sec: number): string {
  if (sec < 60) return `${sec}s`;
  if (sec < 3600) return `${Math.round(sec / 60)}m`;
  if (sec < 86400) return `${Math.round(sec / 3600)}h`;
  return `${Math.round(sec / 86400)}d`;
}

export function formatDurationMs(ms: number | null): string {
  if (ms === null || ms === undefined) return '-';
  if (ms < 1000) return `${ms}ms`;
  return `${(ms / 1000).toFixed(1)}s`;
}

export interface ScheduledTaskSummary {
  taskKey: string;
  name: string;
  description: string;
  intervalSeconds: number;
  isRunning: boolean;
  isEnabled: boolean;
  lastExecutionAt: string | null;
  lastDurationMs: number | null;
  lastStatus: string | null; // "completed" | "failed" | "running" | "idle"
  lastMessage: string | null;
  nextExecutionAt: string | null;
}

export interface SystemHealthPingResponse {
  status: string;
  service?: string;
  version?: string;
  informationalVersion?: string | null;
  gitCommit?: string | null;
  utcNow?: string;
}

export interface SystemHealthCheckItem {
  source: string;
  severity: 'ok' | 'warning' | 'error';
  message: string;
  wikiOrActionHint: string | null;
  actionUrl?: string | null;
  resolveAction?: string | null;
}

export interface SystemHealthResolveResponse {
  action?: string;
  actionKey?: string;
  affectedCount?: number;
  updatedCount?: number;
  message?: string;
}

export interface SystemRetentionSettings {
  verificationReportsRetentionDays: number;
  mediaHistoryRetentionDays: number;
  auditAndJobLogsRetentionDays: number;
  reviewedQuarantineSnippetsRetentionDays: number;
  vacuumThresholdDeletedRows: number;
}

export interface SystemRetentionRunResult {
  deletedVerificationJobs: number;
  deletedHistoryRows: number;
  deletedAuditEvents: number;
  deletedJobLogs: number;
  deletedQuarantineRows: number;
  deletedSnippetDirs: number;
  vacuumExecuted: boolean;
  bytesReclaimed: number;
}

export interface TrackedDownloadDto {
  id: number;
  downloadClientId: number;
  clientName: string;
  externalDownloadId: string;
  protocol: string;
  title: string;
  mediaVertical: string;
  workId: number | null;
  workTitle: string | null;
  unitIds: number[];
  state: string; // "Downloading" | "ImportPending" | "Importing" | "Imported" | "ImportBlocked" | "FailedPending" | "Failed" | "Ignored" | "Removed"
  trackedStatus: string; // "ok" | "warning" | "error"
  statusMessages: string[];
  outputPath: string | null;
  quarantineId: number | null;
  canMoveFiles: boolean;
  firstSeenAt: string;
  updatedAt: string;
}

export interface ManualOverrideImportRequest {
  workId?: number | null;
  seasonNumber?: number | null;
  episodeNumbers?: number[] | null;
  bypassUpgradeCheck?: boolean;
  confirmedAudioLanguages?: string[] | null;
}

export interface ProcessedDownloadItemOutcome {
  verdict?: string | null;
  destinationFilePath?: string | null;
  message?: string | null;
  action?: string;
  destinationPath?: string | null;
  errorMessage?: string | null;
  warning?: string | null;
}

export interface RssSyncDecisionItem {
  releaseTitle: string;
  matchedWorkTitle?: string | null;
  approved: boolean;
  grabbed: boolean;
  decisionKind?: string | null;
  explanation?: string | null;
}

export interface MultiCategoryRssSyncReport {
  polledIndexers: number;
  totalReleases: number;
  matchedCandidatesCount?: number;
  approvedCount?: number;
  grabbedCount?: number;
  decisions?: RssSyncDecisionItem[];
  releasesByVertical?: Record<string, unknown[]>;
}

export interface WantedPagedResponse<T> {
  page: number;
  pageSize: number;
  totalCount: number;
  items: T[];
}

export interface WantedUnitItemDto {
  unitId: number;
  workId: number;
  libraryId: number;
  libraryName: string;
  mediaVertical: string;
  workTitle: string;
  workPosterUrl: string | null;
  containerId: number;
  containerNumber: number;
  containerTitle: string;
  unitKind: string;
  unitNumber: number;
  absoluteNumber: number | null;
  unitTitle: string;
  episodeCode: string;
  airOrReleaseDate: string | null;
  isReleased: boolean;
  monitored: boolean;
  effectiveProfileId: number | null;
  effectiveProfileName: string | null;
  requiredAudioLangs: string[];
  requiredSubLangs: string[];
  qualityCutoffMet: boolean;
  languageCutoffMet: boolean;
  existingQualityName: string | null;
  existingAudioLangs: string[];
  existingSizeBytes: number | null;
  cutoffUnmetReason: string | null;
}

export interface WantedSearchItemOutcome {
  workId: number;
  workTitle: string;
  unitIds: number[];
  label: string;
  candidatesFound: number;
  grabbed: number | boolean;
  selectedRelease: string | null;
  message: string;
}

export interface WantedBulkSearchReport {
  mode: string;
  targetedUnitsCount: number;
  searchedGroupsCount: number;
  candidatesEvaluated: number;
  grabbedCount: number;
  outcomes: WantedSearchItemOutcome[];
}

export interface ActiveDownloadQueueItem {
  clientId: number;
  clientName: string;
  protocol: string;
  externalId: string;
  infoHash: string | null;
  title: string;
  category: string;
  mediaVertical: string;
  status: string;
  totalBytes: number;
  remainingBytes: number;
  outputPath: string;
  containedFiles: string[];
  isStalled: boolean;
  isMetadataStuck: boolean;
  errorMessage: string | null;
  canMoveFiles: boolean;
}

export interface MediaHistoryItem {
  id: number;
  eventType: string;
  occurredAt: string;
  workId?: number | null;
  workTitle?: string | null;
  episodeCode?: string | null;
  sourceTitle?: string | null;
  qualityName?: string | null;
  languages?: string[] | string | null;
  indexerName?: string | null;
  downloadClientName?: string | null;
  mediaVertical?: string | null;
  data?: Record<string, unknown> | null;
}

export interface BlocklistEntry {
  infoHash: string;
  title: string;
  reason: string;
  blockedAt: string;
}

export interface ScannedManualImportItem {
  id?: string;
  filePath: string;
  relativePath?: string;
  folderName?: string;
  fileName: string;
  sizeBytes: number;
  mediaVertical: string;
  parsedTitle?: string;
  workId?: number | null;
  workTitle?: string | null;
  seasonNumber?: number | null;
  episodeNumbers?: number[];
  qualityName?: string | null;
  languages?: string[];
  rejections?: string[];
}

export interface ManualImportExecuteItem {
  sourceFilePath?: string;
  filePath?: string;
  workId: number;
  seasonNumber?: number | null;
  episodeNumbers?: number[];
  qualityName?: string | null;
  languages?: string[];
}

export interface ManualImportExecuteRequest {
  importMode?: 'hardlink_or_copy' | 'move';
  mode?: 'hardlink_or_copy' | 'move';
  items: ManualImportExecuteItem[];
}

import type { ColorSchemeId, ManagerrThemeExportV1 } from './theme';

export interface UiThemeSettingsDto {
  defaultTheme: 'system' | 'dark' | 'oled' | 'light';
  accentColor: string;
  colorScheme?: ColorSchemeId;
  customTheme?: ManagerrThemeExportV1 | null;
}

export interface GeneralSettingsResponse {
  instanceName: string;
  authenticationMethod: string;
  authenticationRequired: string;
  apiKey: string;
  boundServerName: string | null;
  ui?: UiThemeSettingsDto;
  compatUrlBases: {
    sonarr: string;
    radarr: string;
    lidarr: string;
    readarr: string;
    prowlarr: string;
  };
}

export interface ResetGeneralApiKeyResponse {
  apiKey: string;
}

export interface AuthProvidersResponse {
  isClaimed: boolean;
  instanceName: string;
  ui: UiThemeSettingsDto;
  local: {
    enabled: boolean;
  };
  passkeys: {
    enabled: boolean;
    rpId: string;
  };
  plex: {
    enabled: boolean;
    isBound: boolean;
    boundServerName: string | null;
  };
  oidc: {
    enabled: boolean;
    displayName: string;
    autoRedirect: boolean;
  };
}

export interface LocalClaimRequest {
  username: string;
  email?: string;
  displayName?: string;
  password: string;
}

export interface LocalLoginRequest {
  usernameOrEmail: string;
  password: string;
}

export interface LocalPasswordChangeRequest {
  currentPassword?: string;
  newPassword: string;
}

export interface ProfileUpdateRequest {
  username: string;
  displayName?: string;
  email?: string;
}

export interface ProfileUpdateResponse {
  user: PlexUserSummary;
  rpId?: string;
  userId?: string;
}

export interface PasskeyDto {
  id: string;
  name: string;
  aaguid: string;
  credentialDeviceType?: string;
  credentialBackedUp?: boolean;
  transports?: string[];
  registeredAt: number | string;
  lastUsedAt?: number | string | null;
}

export interface PasskeySummaryResponse {
  rpId: string;
  userId: string;
  credentials: PasskeyDto[];
}

export interface AuthSettingsDto {
  instanceName?: string;
  themeMode?: 'system' | 'dark' | 'oled' | 'light';
  accentColor?: string;
  localAuthEnabled?: boolean;
  passkeysEnabled?: boolean;
  oidcEnabled?: boolean;
  oidcDisplayName?: string;
  oidcAuthority?: string;
  oidcClientId?: string;
  oidcClientSecret?: string;
  hasOidcClientSecret?: boolean;
  oidcScope?: string;
  oidcAutoRedirect?: boolean;
  isOidcManagedByEnv?: boolean;
}

export interface TestOidcResult {
  success: boolean;
  issuer?: string;
  authorizationEndpoint?: string;
  tokenEndpoint?: string;
  userinfoEndpoint?: string;
  error?: string;
}

export interface LocalUserCreateRequest {
  username: string;
  password: string;
  displayName?: string;
  email?: string;
  roleIds?: number[];
}

export interface LanguageVerificationDto {
  status: 'verified' | 'unverified' | 'mismatch' | 'unknown' | string;
  detectedAudioLanguages: string[];
  detectedSubtitleLanguages: string[];
  audioMatches: boolean;
  subtitleMatches: boolean;
  notes?: string | null;
}

export interface LibraryOption {
  id: number;
  name: string;
  mediaType: string;
  rootFolderPath?: string;
  freeSpaceBytes?: number | null;
  totalSpaceBytes?: number | null;
  rootAccessible?: boolean;
}

export interface LibrarySummary {
  id: number;
  slug: string;
  name: string;
  mediaType: string;
  rootFolderPath: string;
  plexSectionId: string | null;
  metadataWaterfall: string[];
  defaultProfileId: number | null;
  workCount: number;
  freeSpaceBytes?: number | null;
  totalSpaceBytes?: number | null;
  rootAccessible?: boolean;
}


