import { useState } from 'react';
import { Link } from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Check, CheckCircle2, Copy, ExternalLink, HardDriveDownload, Key, Play, Plus, RefreshCw, Save, Sparkles, Trash2, Wrench } from 'lucide-react';
import {
  apiFetch,
  formatBytes,
  formatDurationMs,
  formatIntervalSeconds,
  type ApiKeySummary,
  type AuditEventEntry,
  type AuthStatus,
  type BackupFileMetadata,
  type CreatedApiKeyResult,
  type PlexUserSummary,
  type ScheduledTaskSummary,
  type StorageOverview,
  type SystemHealthCheckItem,
  type SystemHealthPingResponse,
  type SystemHealthResolveResponse,
  type SystemRetentionRunResult,
  type SystemRetentionSettings,
} from '../lib/api';
import {
  ArrAlert,
  ArrButton,
  ArrLabel,
  ArrModal,
  FieldSet,
  FormGroup,
  PageContentBody,
  PageToolbar,
  PageToolbarButton,
  PageToolbarSeparator,
} from './arr/ArrPrimitives';
import { PasskeyManagementPanel } from './PasskeyManagementPanel';

export type SystemSubSection = 'status' | 'tasks' | 'backup' | 'security' | 'events';

interface SystemViewProps {
  section?: SystemSubSection;
  availableScopes: string[];
  canSystemAdmin: boolean;
  canReadAudit: boolean;
  user?: PlexUserSummary;
  authStatus?: AuthStatus;
  healthInfo?: SystemHealthPingResponse;
}

export function SystemView({
  section = 'status',
  availableScopes,
  canSystemAdmin,
  canReadAudit,
  user,
  authStatus,
  healthInfo,
}: SystemViewProps) {
  const qc = useQueryClient();

  const healthPingQuery = useQuery({
    queryKey: ['system-health-ping'],
    queryFn: async () => {
      try {
        return await apiFetch<SystemHealthPingResponse>('/api/v1/system/health');
      } catch {
        return { status: 'healthy', version: '0.2.0' } as SystemHealthPingResponse;
      }
    },
    enabled: !healthInfo && (section === 'status' || section === 'backup'),
  });

  const effectiveHealthInfo = healthInfo ?? healthPingQuery.data;
  const displayVersion =
    effectiveHealthInfo?.version && effectiveHealthInfo.version !== '0.1.0-phase1'
      ? effectiveHealthInfo.version
      : '0.2.0';
  const displayGitCommit = effectiveHealthInfo?.gitCommit || null;

  const storageQuery = useQuery({
    queryKey: ['system-storage'],
    queryFn: () => apiFetch<StorageOverview>('/api/v1/system/storage'),
    enabled: canSystemAdmin && (section === 'status' || section === 'backup'),
  });

  const backupsQuery = useQuery({
    queryKey: ['system-backups'],
    queryFn: () => apiFetch<BackupFileMetadata[]>('/api/v1/system/backups'),
    enabled: canSystemAdmin && (section === 'backup' || section === 'status'),
  });

  const tokensQuery = useQuery({
    queryKey: ['auth-tokens'],
    queryFn: () => apiFetch<ApiKeySummary[]>('/api/v1/auth/tokens'),
    enabled: section === 'security',
  });

  const auditQuery = useQuery({
    queryKey: ['system-audit'],
    queryFn: () => apiFetch<AuditEventEntry[]>('/api/v1/system/audit?limit=100'),
    enabled: canReadAudit && section === 'events',
  });

  const healthQuery = useQuery({
    queryKey: ['system-health'],
    queryFn: async () => {
      try {
        return await apiFetch<SystemHealthCheckItem[]>('/api/v1/system/health/checks');
      } catch {
        return [] as SystemHealthCheckItem[];
      }
    },
    enabled: section === 'status' || section === 'tasks',
  });

  const tasksQuery = useQuery({
    queryKey: ['system-tasks'],
    queryFn: async () => {
      try {
        return await apiFetch<ScheduledTaskSummary[]>('/api/v1/system/tasks');
      } catch {
        return [] as ScheduledTaskSummary[];
      }
    },
    enabled: section === 'status' || section === 'tasks',
  });

  const [runningTaskKey, setRunningTaskKey] = useState<string | null>(null);
  const [savingTaskKey, setSavingTaskKey] = useState<string | null>(null);
  const [taskScheduleDrafts, setTaskScheduleDrafts] = useState<Record<string, { intervalSeconds: number; isEnabled: boolean }>>({});
  const [taskScheduleMsg, setTaskScheduleMsg] = useState<string | null>(null);
  const [resolvingAction, setResolvingAction] = useState<string | null>(null);
  const [resolveResultMessage, setResolveResultMessage] = useState<string | null>(null);

  const backupSettingsQuery = useQuery({
    queryKey: ['system-backup-settings'],
    queryFn: () => apiFetch<{ maxRetainedBackups: number }>('/api/v1/system/backups/settings'),
    enabled: canSystemAdmin && section === 'backup',
  });
  const [maxRetainedDraft, setMaxRetainedDraft] = useState<number | null>(null);
  const [backupSettingsSaved, setBackupSettingsSaved] = useState(false);

  const saveBackupSettingsMutation = useMutation({
    mutationFn: (maxRetainedBackups: number) =>
      apiFetch<{ maxRetainedBackups: number }>('/api/v1/system/backups/settings', {
        method: 'PUT',
        body: JSON.stringify({ maxRetainedBackups }),
      }),
    onSuccess: (res) => {
      setMaxRetainedDraft(null);
      setBackupSettingsSaved(true);
      qc.setQueryData(['system-backup-settings'], res);
      qc.invalidateQueries({ queryKey: ['system-backup-settings'] });
    },
  });

  const updateTaskMutation = useMutation({
    mutationFn: async ({ taskKey, intervalSeconds, isEnabled }: { taskKey: string; intervalSeconds: number; isEnabled: boolean }) => {
      setSavingTaskKey(taskKey);
      setTaskScheduleMsg(null);
      return apiFetch<ScheduledTaskSummary>(`/api/v1/system/tasks/${encodeURIComponent(taskKey)}`, {
        method: 'PATCH',
        body: JSON.stringify({ intervalSeconds, isEnabled }),
      });
    },
    onSettled: () => {
      setSavingTaskKey(null);
    },
    onSuccess: (updated) => {
      setTaskScheduleDrafts((prev) => {
        const next = { ...prev };
        delete next[updated.taskKey];
        return next;
      });
      setTaskScheduleMsg(`Saved schedule for '${updated.name}'.`);
      qc.invalidateQueries({ queryKey: ['system-tasks'] });
    },
  });

  const resolveHealthMutation = useMutation({
    mutationFn: async (resolveAction: string) => {
      setResolvingAction(resolveAction);
      setResolveResultMessage(null);
      return apiFetch<SystemHealthResolveResponse>(
        `/api/v1/system/health/resolve/${encodeURIComponent(resolveAction)}`,
        { method: 'POST' },
      );
    },
    onSettled: () => {
      setResolvingAction(null);
    },
    onSuccess: (res, resolveAction) => {
      const updated = res?.updatedCount ?? res?.affectedCount;
      const msg =
        res?.message ??
        `Resolved '${res?.actionKey ?? res?.action ?? resolveAction}'${updated !== undefined ? ` (${updated} items updated)` : ''}.`;
      setResolveResultMessage(msg);
      qc.invalidateQueries({ queryKey: ['system-health'] });
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
      qc.invalidateQueries({ queryKey: ['libraries'] });
    },
  });

  const retentionQuery = useQuery({
    queryKey: ['system-retention'],
    queryFn: async () => {
      try {
        return await apiFetch<SystemRetentionSettings>('/api/v1/system/retention');
      } catch {
        return {
          verificationReportsRetentionDays: 90,
          mediaHistoryRetentionDays: 365,
          auditAndJobLogsRetentionDays: 90,
          reviewedQuarantineSnippetsRetentionDays: 30,
          vacuumThresholdDeletedRows: 100,
        } as SystemRetentionSettings;
      }
    },
    enabled:
      canSystemAdmin &&
      (section === 'status' || section === 'backup') &&
      Boolean(effectiveHealthInfo) &&
      effectiveHealthInfo?.version !== '0.1.0-phase1',
  });

  const [retentionDraft, setRetentionDraft] = useState<Partial<SystemRetentionSettings>>({});
  const [retentionSaved, setRetentionSaved] = useState(false);
  const [retentionRunResult, setRetentionRunResult] = useState<SystemRetentionRunResult | null>(null);

  const effectiveRetention: SystemRetentionSettings = {
    verificationReportsRetentionDays:
      retentionDraft.verificationReportsRetentionDays ??
      retentionQuery.data?.verificationReportsRetentionDays ??
      90,
    mediaHistoryRetentionDays:
      retentionDraft.mediaHistoryRetentionDays ??
      retentionQuery.data?.mediaHistoryRetentionDays ??
      365,
    auditAndJobLogsRetentionDays:
      retentionDraft.auditAndJobLogsRetentionDays ??
      retentionQuery.data?.auditAndJobLogsRetentionDays ??
      90,
    reviewedQuarantineSnippetsRetentionDays:
      retentionDraft.reviewedQuarantineSnippetsRetentionDays ??
      retentionQuery.data?.reviewedQuarantineSnippetsRetentionDays ??
      30,
    vacuumThresholdDeletedRows:
      retentionDraft.vacuumThresholdDeletedRows ??
      retentionQuery.data?.vacuumThresholdDeletedRows ??
      100,
  };

  const saveRetentionMutation = useMutation({
    mutationFn: () =>
      apiFetch<SystemRetentionSettings>('/api/v1/system/retention', {
        method: 'PUT',
        body: JSON.stringify(effectiveRetention),
      }),
    onSuccess: () => {
      setRetentionSaved(true);
      setRetentionDraft({});
      qc.invalidateQueries({ queryKey: ['system-retention'] });
    },
  });

  const runRetentionMutation = useMutation({
    mutationFn: () =>
      apiFetch<SystemRetentionRunResult>('/api/v1/system/retention/run', {
        method: 'POST',
      }),
    onSuccess: (result) => {
      setRetentionRunResult(result);
      qc.invalidateQueries({ queryKey: ['system-storage'] });
      qc.invalidateQueries({ queryKey: ['system-audit'] });
    },
  });

  const runTaskMutation = useMutation({
    mutationFn: async (taskKey: string) => {
      setRunningTaskKey(taskKey);
      return apiFetch<ScheduledTaskSummary>(`/api/v1/system/tasks/${encodeURIComponent(taskKey)}/run`, {
        method: 'POST',
      });
    },
    onSettled: () => {
      setRunningTaskKey(null);
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['system-tasks'] });
      qc.invalidateQueries({ queryKey: ['system-health'] });
    },
  });

  const [showKeyModal, setShowKeyModal] = useState(false);
  const [keyName, setKeyName] = useState('');
  const [keyCidrs, setKeyCidrs] = useState('');
  const [keyExpiresOption, setKeyExpiresOption] = useState<'30' | '90' | '365' | 'never' | 'custom'>('90');
  const [keyCustomExpiresDays, setKeyCustomExpiresDays] = useState('90');
  const [keyAllowedLibraryIds, setKeyAllowedLibraryIds] = useState<Set<number>>(new Set());
  const [keyScopes, setKeyScopes] = useState<Set<string>>(
    new Set(availableScopes.includes('metrics:read') ? ['metrics:read'] : availableScopes.slice(0, 1)),
  );
  const [issuedOnce, setIssuedOnce] = useState<CreatedApiKeyResult | null>(null);
  const [copiedKey, setCopiedKey] = useState(false);

  const librariesQuery = useQuery({
    queryKey: ['libraries'],
    queryFn: () => apiFetch<Array<{ id: number; name: string; mediaType: string }>>('/api/v1/libraries'),
    enabled: section === 'security' && showKeyModal,
  });

  const [bgPassword, setBgPassword] = useState('');
  const [bgSaved, setBgSaved] = useState(false);

  const [audioPref, setAudioPref] = useState<string | null>(null);
  const [subPref, setSubPref] = useState<string | null>(null);
  const [discordName, setDiscordName] = useState<string | null>(null);
  const [prefSaved, setPrefSaved] = useState(false);

  const backupMutation = useMutation({
    mutationFn: () =>
      apiFetch<BackupFileMetadata>('/api/v1/system/backups', {
        method: 'POST',
        body: JSON.stringify({ reason: 'ui-manual' }),
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['system-backups'] });
      qc.invalidateQueries({ queryKey: ['system-audit'] });
    },
  });

  const createKeyMutation = useMutation({
    mutationFn: () => {
      const expiresInDays =
        keyExpiresOption === 'never'
          ? null
          : keyExpiresOption === 'custom'
            ? Math.max(1, Number(keyCustomExpiresDays) || 1)
            : Number(keyExpiresOption);
      return apiFetch<CreatedApiKeyResult>('/api/v1/auth/tokens', {
        method: 'POST',
        body: JSON.stringify({
          name: keyName,
          scopes: Array.from(keyScopes),
          allowedLibraryIds: keyAllowedLibraryIds.size > 0 ? Array.from(keyAllowedLibraryIds) : null,
          allowedCidrs: keyCidrs
            .split(',')
            .map((s) => s.trim())
            .filter(Boolean),
          expiresInDays,
        }),
      });
    },
    onSuccess: (created) => {
      setIssuedOnce(created);
      setKeyName('');
      setKeyCidrs('');
      setKeyExpiresOption('90');
      setKeyCustomExpiresDays('90');
      setKeyAllowedLibraryIds(new Set());
      setShowKeyModal(false);
      qc.invalidateQueries({ queryKey: ['auth-tokens'] });
    },
  });

  const generateSeerrKeyMutation = useMutation({
    mutationFn: () =>
      apiFetch<CreatedApiKeyResult>('/api/v1/auth/tokens', {
        method: 'POST',
        body: JSON.stringify({
          name: 'Seerr (Sonarr + Radarr)',
          scopes: ['compat:sonarr', 'compat:radarr'],
        }),
      }),
    onSuccess: (created) => {
      setIssuedOnce(created);
      qc.invalidateQueries({ queryKey: ['auth-tokens'] });
    },
  });

  const revokeKeyMutation = useMutation({
    mutationFn: (id: number) => apiFetch<void>(`/api/v1/auth/tokens/${id}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['auth-tokens'] }),
  });

  const bgMutation = useMutation({
    mutationFn: () =>
      apiFetch<{ configured: boolean }>('/api/v1/auth/breakglass/setup', {
        method: 'POST',
        body: JSON.stringify({ newPassword: bgPassword }),
      }),
    onSuccess: () => {
      setBgPassword('');
      setBgSaved(true);
    },
  });

  const prefMutation = useMutation({
    mutationFn: () =>
      apiFetch('/api/v1/users/me/preferences', {
        method: 'PATCH',
        body: JSON.stringify({
          preferredAudioLang: audioPref ?? user?.preferredAudioLang,
          preferredSubLang: subPref ?? user?.preferredSubLang,
          discordUsername: discordName ?? user?.discordUsername,
        }),
      }),
    onSuccess: () => {
      setPrefSaved(true);
      qc.invalidateQueries({ queryKey: ['me'] });
    },
  });

  return (
    <div className="flex flex-col flex-1">
      <PageToolbar
        left={
          <>
            <PageToolbarButton
              icon={RefreshCw}
              label="Refresh"
              spinning={
                storageQuery.isFetching ||
                backupsQuery.isFetching ||
                tokensQuery.isFetching ||
                auditQuery.isFetching ||
                healthQuery.isFetching ||
                tasksQuery.isFetching
              }
              onClick={() => {
                if (section === 'status' || section === 'tasks') {
                  void storageQuery.refetch();
                  void healthQuery.refetch();
                  void tasksQuery.refetch();
                }
                if (section === 'backup') void backupsQuery.refetch();
                if (section === 'security') void tokensQuery.refetch();
                if (section === 'events') void auditQuery.refetch();
              }}
            />
            {section === 'backup' && canSystemAdmin && (
              <>
                <PageToolbarSeparator />
                <PageToolbarButton
                  icon={HardDriveDownload}
                  label="Backup Now"
                  spinning={backupMutation.isPending}
                  onClick={() => backupMutation.mutate()}
                />
              </>
            )}
            {section === 'security' && (
              <>
                <PageToolbarSeparator />
                <PageToolbarButton icon={Plus} label="Add API Key" onClick={() => setShowKeyModal(true)} />
                {user && (
                  <PageToolbarButton
                    icon={Save}
                    label="Save Prefs"
                    spinning={prefMutation.isPending}
                    onClick={() => prefMutation.mutate()}
                  />
                )}
              </>
            )}
          </>
        }
      />

      <PageContentBody>
        {(section === 'status' || section === 'tasks') && (
          <>
            <FieldSet
              legend="System Health Checks"
              action={
                <ArrButton
                  variant="default"
                  busy={healthQuery.isFetching}
                  onClick={() => void healthQuery.refetch()}
                  title="Refresh System Health Checks"
                >
                  <RefreshCw className={`h-3.5 w-3.5 ${healthQuery.isFetching ? 'animate-spin' : ''}`} /> Refresh Checks
                </ArrButton>
              }
            >
              {resolveResultMessage && (
                <ArrAlert kind="success" className="mb-3">
                  {resolveResultMessage}
                </ArrAlert>
              )}
              {(healthQuery.data ?? []).length === 0 ? (
                <div className="flex items-center gap-2.5 text-[13px] text-[#cccccc]">
                  <CheckCircle2 className="h-4 w-4 text-[#27c24c]" />
                  All system health checks passed — no issues detected.
                </div>
              ) : (
                <div className="space-y-2.5">
                  {healthQuery.data!.map((item, idx) => {
                    const badgeKind =
                      item.severity === 'error'
                        ? 'danger'
                        : item.severity === 'warning'
                          ? 'warning'
                          : 'success';

                    return (
                      <div
                        key={`${item.source}-${idx}`}
                        className="flex flex-col sm:flex-row sm:items-center justify-between p-3 rounded bg-[#25282b] border border-arr-card-border gap-3"
                      >
                        <div className="flex items-start gap-3">
                          <ArrLabel kind={badgeKind}>
                            {item.severity.toUpperCase()}
                          </ArrLabel>
                          <div>
                            <div className="font-semibold text-[13px] text-[#eeeeee] flex items-center gap-2">
                              <span>{item.message}</span>
                              <span className="font-mono text-[11px] text-[#909293] bg-[#1e2022] px-1.5 py-0.5 rounded border border-[#303438]">
                                {item.source}
                              </span>
                            </div>
                            {item.wikiOrActionHint && (
                              <div className="text-[12px] text-arr-accent mt-1 flex items-center gap-1.5">
                                <span>💡 {item.wikiOrActionHint}</span>
                              </div>
                            )}
                          </div>
                        </div>

                        {(item.actionUrl || item.resolveAction) && (
                          <div className="flex items-center gap-2 shrink-0">
                            {item.actionUrl && (
                              <Link
                                to={item.actionUrl}
                                className="inline-flex items-center gap-1.5 px-2.5 py-1 text-[12px] font-medium rounded-[3px] bg-arr-card hover:bg-[#444444] text-arr-accent border border-[#4a5158] transition-colors"
                              >
                                <ExternalLink className="h-3.5 w-3.5" />
                                View in Library
                              </Link>
                            )}
                            {item.resolveAction && (
                              <ArrButton
                                variant="primary"
                                busy={
                                  resolveHealthMutation.isPending &&
                                  resolvingAction === item.resolveAction
                                }
                                onClick={() => resolveHealthMutation.mutate(item.resolveAction!)}
                              >
                                <Wrench className="h-3.5 w-3.5" />
                                Resolve
                              </ArrButton>
                            )}
                          </div>
                        )}
                      </div>
                    );
                  })}
                </div>
              )}
            </FieldSet>

            <FieldSet
              legend="Scheduled Tasks (TaskManager)"
              action={
                <ArrButton
                  variant="default"
                  busy={tasksQuery.isFetching}
                  onClick={() => void tasksQuery.refetch()}
                  title="Refresh Scheduled Background Tasks"
                >
                  <RefreshCw className={`h-3.5 w-3.5 ${tasksQuery.isFetching ? 'animate-spin' : ''}`} /> Refresh Tasks
                </ArrButton>
              }
            >
              {taskScheduleMsg && (
                <ArrAlert kind="success" className="mb-3">
                  {taskScheduleMsg}
                </ArrAlert>
              )}
              {(tasksQuery.data ?? []).length === 0 ? (
                <div className="text-[13px] text-[#909293]">
                  No scheduled background tasks registered or task manager is starting up.
                </div>
              ) : (
                <div className="overflow-x-auto">
                  <table className="arr-table">
                    <thead>
                      <tr>
                        <th>Enabled</th>
                        <th>Name</th>
                        <th>Interval (s)</th>
                        <th>Last Execution</th>
                        <th>Duration</th>
                        <th>Next Execution</th>
                        <th>Last Status</th>
                        <th className="w-40 text-right">Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {tasksQuery.data!.map((task) => {
                        const isTaskRunning = task.isRunning || runningTaskKey === task.taskKey;
                        const isTaskSaving = savingTaskKey === task.taskKey && updateTaskMutation.isPending;
                        const draft = taskScheduleDrafts[task.taskKey];
                        const draftEnabled = draft ? draft.isEnabled : task.isEnabled;
                        const draftInterval = draft ? draft.intervalSeconds : task.intervalSeconds;
                        const hasScheduleChanges =
                          draft !== undefined &&
                          (draft.isEnabled !== task.isEnabled || draft.intervalSeconds !== task.intervalSeconds);

                        const statusKind =
                          isTaskRunning
                            ? 'primary'
                            : task.lastStatus?.toLowerCase() === 'completed'
                              ? 'success'
                              : task.lastStatus?.toLowerCase() === 'failed'
                                ? 'danger'
                                : 'default';

                        return (
                          <tr key={task.taskKey}>
                            <td>
                              <input
                                type="checkbox"
                                aria-label={`Enable ${task.name}`}
                                disabled={!canSystemAdmin}
                                checked={draftEnabled}
                                onChange={(e) => {
                                  setTaskScheduleMsg(null);
                                  setTaskScheduleDrafts((prev) => ({
                                    ...prev,
                                    [task.taskKey]: {
                                      intervalSeconds: draftInterval,
                                      isEnabled: e.target.checked,
                                    },
                                  }));
                                }}
                              />
                            </td>
                            <td>
                              <div className="font-semibold text-[#eeeeee] flex items-center gap-2">
                                <span>{task.name}</span>
                                {!draftEnabled && (
                                  <span className="text-[10px] uppercase font-mono px-1 py-0.2 rounded bg-arr-card text-[#909293]">
                                    Disabled
                                  </span>
                                )}
                              </div>
                              {task.description && (
                                <div className="text-[11px] text-[#909293] mt-0.5">{task.description}</div>
                              )}
                            </td>
                            <td className="font-mono text-[12px] text-[#cccccc]">
                              {canSystemAdmin ? (
                                <div className="flex items-center gap-1.5">
                                  <input
                                    type="number"
                                    min={10}
                                    max={604800}
                                    aria-label={`${task.name} interval seconds`}
                                    value={draftInterval}
                                    onChange={(e) => {
                                      setTaskScheduleMsg(null);
                                      const val = Math.max(10, Math.min(604800, Number(e.target.value) || 10));
                                      setTaskScheduleDrafts((prev) => ({
                                        ...prev,
                                        [task.taskKey]: {
                                          intervalSeconds: val,
                                          isEnabled: draftEnabled,
                                        },
                                      }));
                                    }}
                                    className="arr-input w-24 h-7 px-2 font-mono text-[12px]"
                                  />
                                  <span className="text-[11px] text-[#909293]">
                                    ({formatIntervalSeconds(draftInterval)})
                                  </span>
                                </div>
                              ) : (
                                formatIntervalSeconds(task.intervalSeconds)
                              )}
                            </td>
                            <td className="font-mono text-[12px] text-[#909293] whitespace-nowrap">
                              {task.lastExecutionAt ? new Date(task.lastExecutionAt).toLocaleString() : 'Never'}
                            </td>
                            <td className="font-mono text-[12px] text-[#909293]">
                              {formatDurationMs(task.lastDurationMs)}
                            </td>
                            <td className="font-mono text-[12px] text-[#909293] whitespace-nowrap">
                              {task.nextExecutionAt ? new Date(task.nextExecutionAt).toLocaleString() : '-'}
                            </td>
                            <td>
                              <div className="flex flex-col gap-0.5">
                                <div>
                                  <ArrLabel kind={statusKind}>
                                    {isTaskRunning ? 'Running' : task.lastStatus ?? 'Idle'}
                                  </ArrLabel>
                                </div>
                                {task.lastMessage && (
                                  <div
                                    className="text-[11px] text-[#909293] max-w-xs truncate"
                                    title={task.lastMessage}
                                  >
                                    {task.lastMessage}
                                  </div>
                                )}
                              </div>
                            </td>
                            <td className="text-right">
                              <div className="inline-flex items-center gap-1.5">
                                {canSystemAdmin && (
                                  <ArrButton
                                    variant={hasScheduleChanges ? 'primary' : 'default'}
                                    busy={isTaskSaving}
                                    disabled={!hasScheduleChanges || isTaskSaving}
                                    onClick={() =>
                                      updateTaskMutation.mutate({
                                        taskKey: task.taskKey,
                                        intervalSeconds: draftInterval,
                                        isEnabled: draftEnabled,
                                      })
                                    }
                                    title="Save task schedule"
                                  >
                                    <Save className="h-3 w-3" /> Save
                                  </ArrButton>
                                )}
                                <ArrButton
                                  variant="default"
                                  busy={isTaskRunning}
                                  disabled={isTaskRunning || !task.isEnabled}
                                  onClick={() => runTaskMutation.mutate(task.taskKey)}
                                  title="Execute task immediately"
                                >
                                  <Play className="h-3 w-3" /> Run Now
                                </ArrButton>
                              </div>
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              )}
            </FieldSet>

            {canSystemAdmin && storageQuery.data && (
              <FieldSet legend="Databases & Storage">
                <div className="overflow-x-auto">
                  <table className="arr-table">
                    <thead>
                      <tr>
                        <th>Database</th>
                        <th>Location</th>
                        <th>Journal Mode</th>
                        <th>Schema</th>
                        <th>DB Size</th>
                        <th>WAL Size</th>
                      </tr>
                    </thead>
                    <tbody>
                      {storageQuery.data.databases.map((db) => (
                        <tr key={db.kind}>
                          <td className="font-semibold text-[#eeeeee]">{db.kind}.db</td>
                          <td className="font-mono text-[12px] text-[#909293]">{db.filePath}</td>
                          <td>
                            <ArrLabel kind="success">{db.journalMode.toUpperCase()}</ArrLabel>
                          </td>
                          <td>v{db.schemaVersion}</td>
                          <td>{formatBytes(db.sizeBytes)}</td>
                          <td>{formatBytes(db.walSizeBytes)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </FieldSet>
            )}

            <FieldSet legend="About">
              <div className="max-w-2xl space-y-2 text-[13px]">
                <div className="grid grid-cols-[190px_1fr] gap-4">
                  <span className="font-bold text-[#e1e2e3] text-right">Version</span>
                  <span className="font-mono text-[#cccccc] break-all inline-flex items-center gap-2">
                    <span>{displayVersion}</span>
                    {displayGitCommit && (
                      <span className="text-[11px] px-1.5 py-0.5 rounded bg-[#25282b] text-arr-accent border border-arr-card-border">
                        {displayGitCommit.slice(0, 7)}
                      </span>
                    )}
                  </span>
                </div>
                {[
                  ['Database Engine', 'Embedded SQLite (WAL, Zero External DB)'],
                  ['Bound Plex Server', authStatus?.boundServerName ?? 'Claimed PMS'],
                  ['Authentication', authStatus?.authMode ?? 'Plex SSO + RBAC'],
                  ['AppData Directory', storageQuery.data?.configDirectory ?? '/config'],
                  ['Backup Directory', storageQuery.data?.backupsDirectory ?? '/config/backups'],
                  ['Envelope Master Key', storageQuery.data?.envelopeMasterKeyId ?? 'Restricted'],
                ].map(([k, v]) => (
                  <div key={k} className="grid grid-cols-[190px_1fr] gap-4">
                    <span className="font-bold text-[#e1e2e3] text-right">{k}</span>
                    <span className="font-mono text-[#cccccc] break-all">{v}</span>
                  </div>
                ))}
              </div>
            </FieldSet>
          </>
        )}

        {(section === 'status' || section === 'backup') && canSystemAdmin && (
          <FieldSet legend="Data Retention & SQLite Compaction">
            {retentionSaved && (
              <ArrAlert kind="success" className="mb-4">
                Data retention policy saved.
              </ArrAlert>
            )}
            {retentionRunResult && (
              <ArrAlert kind="info" className="mb-4">
                <div className="font-semibold text-[#eeeeee] mb-1">
                  Cleanup Completed{retentionRunResult.vacuumExecuted ? ' & SQLite VACUUM Executed' : ''}:
                </div>
                <div className="text-[12px] text-[#cccccc]">
                  Deleted {retentionRunResult.deletedVerificationJobs} verification jobs,{' '}
                  {retentionRunResult.deletedHistoryRows} history rows,{' '}
                  {retentionRunResult.deletedAuditEvents} audit events,{' '}
                  {retentionRunResult.deletedJobLogs} job logs,{' '}
                  {retentionRunResult.deletedQuarantineRows} quarantine rows, and{' '}
                  {retentionRunResult.deletedSnippetDirs} snippet directories — reclaimed{' '}
                  <strong className="text-[#27c24c]">{formatBytes(retentionRunResult.bytesReclaimed)}</strong>.
                </div>
              </ArrAlert>
            )}

            <FormGroup
              label="Verification Reports Retention (Days)"
              helpText="Days to retain completed audio/subtitle verification job reports (0 disables pruning)."
            >
              <input
                aria-label="Verification Reports Retention (Days)"
                type="number"
                min={0}
                value={effectiveRetention.verificationReportsRetentionDays}
                onChange={(e) => {
                  setRetentionSaved(false);
                  setRetentionDraft((prev) => ({
                    ...prev,
                    verificationReportsRetentionDays: e.target.value === '' ? 0 : Math.max(0, Number(e.target.value)),
                  }));
                }}
                className="arr-input max-w-[180px]"
              />
            </FormGroup>

            <FormGroup
              label="Media History Retention (Days)"
              helpText="Days to retain grab/import/upgrade media history events (0 disables pruning)."
            >
              <input
                aria-label="Media History Retention (Days)"
                type="number"
                min={0}
                value={effectiveRetention.mediaHistoryRetentionDays}
                onChange={(e) => {
                  setRetentionSaved(false);
                  setRetentionDraft((prev) => ({
                    ...prev,
                    mediaHistoryRetentionDays: e.target.value === '' ? 0 : Math.max(0, Number(e.target.value)),
                  }));
                }}
                className="arr-input max-w-[180px]"
              />
            </FormGroup>

            <FormGroup
              label="Audit & Job Logs Retention (Days)"
              helpText="Days to retain security audit events and background task execution logs (0 disables pruning)."
            >
              <input
                aria-label="Audit & Job Logs Retention (Days)"
                type="number"
                min={0}
                value={effectiveRetention.auditAndJobLogsRetentionDays}
                onChange={(e) => {
                  setRetentionSaved(false);
                  setRetentionDraft((prev) => ({
                    ...prev,
                    auditAndJobLogsRetentionDays: e.target.value === '' ? 0 : Math.max(0, Number(e.target.value)),
                  }));
                }}
                className="arr-input max-w-[180px]"
              />
            </FormGroup>

            <FormGroup
              label="Reviewed Quarantine Snippets Retention (Days)"
              helpText="Days to retain extracted audio snippet directories after quarantine items are reviewed (0 disables pruning)."
            >
              <input
                aria-label="Reviewed Quarantine Snippets Retention (Days)"
                type="number"
                min={0}
                value={effectiveRetention.reviewedQuarantineSnippetsRetentionDays}
                onChange={(e) => {
                  setRetentionSaved(false);
                  setRetentionDraft((prev) => ({
                    ...prev,
                    reviewedQuarantineSnippetsRetentionDays: e.target.value === '' ? 0 : Math.max(0, Number(e.target.value)),
                  }));
                }}
                className="arr-input max-w-[180px]"
              />
            </FormGroup>

            <FormGroup
              label="SQLite VACUUM Threshold (Deleted Rows)"
              helpText="Minimum total rows pruned in a cleanup pass before running SQLite VACUUM compaction."
            >
              <input
                aria-label="SQLite VACUUM Threshold (Deleted Rows)"
                type="number"
                min={0}
                value={effectiveRetention.vacuumThresholdDeletedRows}
                onChange={(e) => {
                  setRetentionSaved(false);
                  setRetentionDraft((prev) => ({
                    ...prev,
                    vacuumThresholdDeletedRows: e.target.value === '' ? 0 : Math.max(0, Number(e.target.value)),
                  }));
                }}
                className="arr-input max-w-[180px]"
              />
            </FormGroup>

            <FormGroup label="">
              <div className="flex flex-wrap items-center gap-2.5">
                <ArrButton
                  variant="primary"
                  busy={saveRetentionMutation.isPending}
                  onClick={() => saveRetentionMutation.mutate()}
                >
                  <Save className="h-3.5 w-3.5" /> Save Retention Policy
                </ArrButton>
                <ArrButton
                  variant="default"
                  busy={runRetentionMutation.isPending}
                  onClick={() => runRetentionMutation.mutate()}
                >
                  <Play className="h-3.5 w-3.5" /> Run Cleanup &amp; VACUUM Now
                </ArrButton>
              </div>
            </FormGroup>
          </FieldSet>
        )}

        {section === 'backup' && (
          <FieldSet legend="Backups">
            {!canSystemAdmin ? (
              <ArrAlert kind="warning">Administrator permissions are required to manage database backups.</ArrAlert>
            ) : (
              <>
                {backupSettingsSaved && (
                  <ArrAlert kind="success" className="mb-4">
                    Backup retention settings saved.
                  </ArrAlert>
                )}

                <FormGroup
                  label="Max Retained Backups"
                  helpText="Maximum number of SQLite hot backup archives retained on disk (1–100)."
                >
                  <div className="flex items-center gap-2">
                    <input
                      aria-label="Max Retained Backups"
                      type="number"
                      min={1}
                      max={100}
                      value={maxRetainedDraft ?? backupSettingsQuery.data?.maxRetainedBackups ?? 10}
                      onChange={(e) => {
                        setBackupSettingsSaved(false);
                        const val = Math.max(1, Math.min(100, Number(e.target.value) || 1));
                        setMaxRetainedDraft(val);
                      }}
                      className="arr-input max-w-[140px]"
                    />
                    <ArrButton
                      variant="primary"
                      busy={saveBackupSettingsMutation.isPending}
                      onClick={() =>
                        saveBackupSettingsMutation.mutate(
                          maxRetainedDraft ?? backupSettingsQuery.data?.maxRetainedBackups ?? 10,
                        )
                      }
                    >
                      <Save className="h-3.5 w-3.5" /> Save
                    </ArrButton>
                  </div>
                </FormGroup>

                {(backupsQuery.data ?? []).length === 0 ? (
                  <div className="text-[13px] text-[#909293] mt-4">
                    No backups are available. Click &ldquo;Backup Now&rdquo; in the toolbar to run an online SQLite{' '}
                    <code>VACUUM INTO</code> archive.
                  </div>
                ) : (
                  <div className="overflow-x-auto mt-4">
                    <table className="arr-table">
                      <thead>
                        <tr>
                          <th>Name</th>
                          <th>Path</th>
                          <th>Size</th>
                          <th>Time</th>
                        </tr>
                      </thead>
                      <tbody>
                        {(backupsQuery.data ?? []).map((b) => (
                          <tr key={b.fileName}>
                            <td className="font-mono text-[#eeeeee]">{b.fileName}</td>
                            <td className="font-mono text-[12px] text-[#909293]">{b.fullPath}</td>
                            <td>{formatBytes(b.sizeBytes)}</td>
                            <td>{new Date(b.createdAtUtc).toLocaleString()}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
              </>
            )}
          </FieldSet>
        )}

        {section === 'security' && (
          <>
            {canSystemAdmin && (
              <ArrAlert kind="info" className="mb-5">
                Looking for Managerr&apos;s Sonarr/Radarr-compatible instance <strong>API Key</strong>?
                It is located in{' '}
                <Link to="/settings/general" className="text-arr-accent font-semibold hover:underline">
                  Settings → General
                </Link>{' '}
                (<strong>Security → API Key</strong>). You can also create and manage scoped, least-privilege tokens below.
              </ArrAlert>
            )}

            {issuedOnce && (
              <ArrAlert kind="success" className="mb-5">
                <div className="font-bold mb-1 flex items-center justify-between gap-1.5">
                  <span className="flex items-center gap-1.5">
                    <Key className="h-4 w-4" /> API Key Created (Shown Once — Copy Now):
                  </span>
                  <button
                    type="button"
                    onClick={() => {
                      void navigator.clipboard.writeText(issuedOnce.rawTokenOnce);
                      setCopiedKey(true);
                      setTimeout(() => setCopiedKey(false), 2000);
                    }}
                    className="inline-flex items-center gap-1 px-2 py-0.5 text-[11px] bg-arr-card hover:bg-[#444444] text-[#eeeeee] rounded border border-[#4a5158] cursor-pointer"
                  >
                    {copiedKey ? <Check className="h-3 w-3 text-[#27c24c]" /> : <Copy className="h-3 w-3" />}
                    {copiedKey ? 'Copied' : 'Copy Key'}
                  </button>
                </div>
                <code className="block bg-[#191919] px-3 py-2 rounded-[3px] font-mono text-[12px] text-arr-accent select-all break-all">
                  {issuedOnce.rawTokenOnce}
                </code>
                {(issuedOnce.scopes.includes('compat:sonarr') || issuedOnce.scopes.includes('compat:radarr')) && (
                  <div className="mt-2 text-[12px] text-[#cccccc] bg-[#222222] p-2 rounded border border-arr-card-border">
                    💡 <strong>Seerr Setup Hint:</strong> Use URL Base <code className="text-arr-accent">/compat/radarr</code> for Radarr and <code className="text-arr-accent">/compat/sonarr</code> for Sonarr in Seerr.
                  </div>
                )}
              </ArrAlert>
            )}

            <FieldSet
              legend="Scoped API Keys"
              action={
                <div className="flex flex-wrap items-center gap-2">
                  <ArrButton
                    variant="default"
                    busy={generateSeerrKeyMutation.isPending}
                    onClick={() => generateSeerrKeyMutation.mutate()}
                    title="Generate a 1-click dual-scoped API key for Overseerr / Jellyseerr / Seerr"
                  >
                    <Sparkles className="h-3.5 w-3.5 text-arr-accent" /> Quick Preset: Seerr (Sonarr + Radarr)
                  </ArrButton>
                  <ArrButton variant="primary" onClick={() => setShowKeyModal(true)}>
                    <Plus className="h-3.5 w-3.5" /> Add API Key
                  </ArrButton>
                </div>
              }
            >
              {(tokensQuery.data ?? []).length === 0 ? (
                <div className="text-[13px] text-[#909293]">No scoped API keys issued yet.</div>
              ) : (
                <div className="overflow-x-auto">
                  <table className="arr-table">
                    <thead>
                      <tr>
                        <th>Name</th>
                        <th>Prefix</th>
                        <th>Scopes</th>
                        <th>CIDR Allowlist</th>
                        <th>Status</th>
                        <th className="w-20 text-right">Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {(tokensQuery.data ?? []).map((t) => {
                        const hasSeerrCompat =
                          t.scopes.includes('compat:sonarr') || t.scopes.includes('compat:radarr');
                        return (
                          <tr key={t.id}>
                            <td className="font-semibold text-[#eeeeee]">
                              <div>{t.name}</div>
                              {hasSeerrCompat && (
                                <div className="text-[11px] font-normal text-arr-accent mt-0.5">
                                  Use URL Base /compat/radarr for Radarr and /compat/sonarr for Sonarr in Seerr
                                </div>
                              )}
                            </td>
                            <td className="font-mono text-[12px] text-[#909293]">{t.maskedHint}</td>
                            <td className="text-[12px]">{t.scopes.join(', ')}</td>
                            <td className="font-mono text-[12px]">
                              {t.allowedCidrs.length > 0 ? t.allowedCidrs.join(', ') : 'Any'}
                            </td>
                            <td>
                              {t.revokedAt ? (
                                <ArrLabel kind="danger">Revoked</ArrLabel>
                              ) : (
                                <ArrLabel kind="success">Active</ArrLabel>
                              )}
                            </td>
                            <td className="text-right">
                              {!t.revokedAt && (
                                <button
                                  type="button"
                                  title="Revoke Key"
                                  onClick={() => revokeKeyMutation.mutate(t.id)}
                                  className="p-1 text-[#f05050] hover:text-white cursor-pointer"
                                >
                                  <Trash2 className="h-4 w-4" />
                                </button>
                              )}
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              )}
            </FieldSet>

            <PasskeyManagementPanel user={user} />

            {user && (
              <FieldSet legend="User Language & Notification Preferences">
                {prefSaved && (
                  <ArrAlert kind="success" className="mb-4">
                    User preferences saved.
                  </ArrAlert>
                )}
                <FormGroup
                  label="Preferred Audio Language"
                  helpText="ISO 639-1 language code used for personal language upgrade targets (e.g. de, en, ja)."
                >
                  <input
                    value={audioPref ?? user.preferredAudioLang ?? ''}
                    onChange={(e) => {
                      setPrefSaved(false);
                      setAudioPref(e.target.value);
                    }}
                    placeholder="de"
                    className="arr-input"
                  />
                </FormGroup>

                <FormGroup label="Preferred Subtitle Language" helpText="ISO 639-1 subtitle code (e.g. de, en).">
                  <input
                    value={subPref ?? user.preferredSubLang ?? ''}
                    onChange={(e) => {
                      setPrefSaved(false);
                      setSubPref(e.target.value);
                    }}
                    placeholder="de"
                    className="arr-input"
                  />
                </FormGroup>

                <FormGroup
                  label="Discord Handle"
                  helpText="Used for Discord mentions in notifications."
                >
                  <input
                    value={discordName ?? user.discordUsername ?? ''}
                    onChange={(e) => {
                      setPrefSaved(false);
                      setDiscordName(e.target.value);
                    }}
                    placeholder="username"
                    className="arr-input"
                  />
                </FormGroup>

                <FormGroup label="">
                  <ArrButton variant="primary" busy={prefMutation.isPending} onClick={() => prefMutation.mutate()}>
                    Save Preferences
                  </ArrButton>
                </FormGroup>
              </FieldSet>
            )}

            {canSystemAdmin && (
              <FieldSet legend="Local Break-Glass Authentication">
                {bgSaved && (
                  <ArrAlert kind="success" className="mb-4">
                    Break-glass password updated.
                  </ArrAlert>
                )}
                <FormGroup
                  label="Emergency Password"
                  helpText="PBKDF2-SHA512 local fallback password (minimum 10 characters) when plex.tv is unreachable."
                >
                  <div className="flex gap-2">
                    <input
                      type="password"
                      value={bgPassword}
                      onChange={(e) => {
                        setBgSaved(false);
                        setBgPassword(e.target.value);
                      }}
                      placeholder="Minimum 10 characters"
                      className="arr-input"
                    />
                    <ArrButton
                      variant="default"
                      busy={bgMutation.isPending}
                      disabled={bgPassword.length < 10}
                      onClick={() => bgMutation.mutate()}
                    >
                      Save
                    </ArrButton>
                  </div>
                </FormGroup>
              </FieldSet>
            )}

            {showKeyModal && (
              <ArrModal
                title="Add Scoped API Key"
                onClose={() => setShowKeyModal(false)}
                maxWidth="max-w-xl"
                footer={
                  <>
                    <ArrButton onClick={() => setShowKeyModal(false)}>Cancel</ArrButton>
                    <ArrButton
                      variant="primary"
                      busy={createKeyMutation.isPending}
                      disabled={!keyName.trim() || keyScopes.size === 0}
                      onClick={() => createKeyMutation.mutate()}
                    >
                      Issue Key
                    </ArrButton>
                  </>
                }
              >
                <FormGroup label="Name">
                  <div className="space-y-1.5">
                    <input
                      value={keyName}
                      onChange={(e) => setKeyName(e.target.value)}
                      placeholder="Recyclarr / Homepage"
                      className="arr-input"
                    />
                    <div className="flex items-center justify-between">
                      <span className="text-[11px] text-[#909293]">Quick Presets:</span>
                      <button
                        type="button"
                        onClick={() => {
                          setKeyName('Seerr (Sonarr + Radarr)');
                          setKeyScopes(new Set(['compat:sonarr', 'compat:radarr']));
                        }}
                        className="inline-flex items-center gap-1 text-[11px] text-arr-accent hover:underline cursor-pointer"
                      >
                        <Sparkles className="h-3 w-3" /> Seerr (Sonarr + Radarr)
                      </button>
                    </div>
                  </div>
                </FormGroup>
                <FormGroup label="CIDR Allowlist" helpText="Comma-separated CIDRs (leave empty for any LAN/client).">
                  <input
                    value={keyCidrs}
                    onChange={(e) => setKeyCidrs(e.target.value)}
                    placeholder="192.168.1.0/24"
                    className="arr-input"
                  />
                </FormGroup>
                <FormGroup label="Expiration" helpText="Optional expiry lifetime for this scoped API key.">
                  <div className="flex flex-wrap items-center gap-2">
                    <select
                      aria-label="API Key Expiration"
                      value={keyExpiresOption}
                      onChange={(e) => setKeyExpiresOption(e.target.value as typeof keyExpiresOption)}
                      className="arr-input max-w-[200px]"
                    >
                      <option value="30">30 days</option>
                      <option value="90">90 days</option>
                      <option value="365">365 days</option>
                      <option value="never">Never expires</option>
                      <option value="custom">Custom days</option>
                    </select>
                    {keyExpiresOption === 'custom' && (
                      <input
                        type="number"
                        min={1}
                        aria-label="Custom expiration days"
                        value={keyCustomExpiresDays}
                        onChange={(e) => setKeyCustomExpiresDays(e.target.value)}
                        className="arr-input max-w-[120px]"
                      />
                    )}
                  </div>
                </FormGroup>
                <FormGroup
                  label="Allowed Libraries"
                  helpText="Optional library restriction (leave all unchecked to allow all accessible libraries)."
                >
                  {(librariesQuery.data ?? []).length === 0 ? (
                    <div className="text-[12px] text-[#909293] pt-1.5">
                      {librariesQuery.isLoading ? 'Loading libraries...' : 'No libraries configured.'}
                    </div>
                  ) : (
                    <div className="flex flex-wrap gap-2 pt-1">
                      {librariesQuery.data!.map((lib) => {
                        const checked = keyAllowedLibraryIds.has(lib.id);
                        return (
                          <label
                            key={lib.id}
                            className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-[3px] border border-arr-card-border bg-arr-card text-[12px] cursor-pointer"
                          >
                            <input
                              type="checkbox"
                              checked={checked}
                              onChange={() =>
                                setKeyAllowedLibraryIds((prev) => {
                                  const next = new Set(prev);
                                  if (next.has(lib.id)) next.delete(lib.id);
                                  else next.add(lib.id);
                                  return next;
                                })
                              }
                            />
                            <span>{lib.name}</span>
                            <span className="text-[10px] text-[#909293] uppercase">{lib.mediaType}</span>
                          </label>
                        );
                      })}
                    </div>
                  )}
                </FormGroup>
                <div className="mt-4 pt-3 border-t border-arr-card-border">
                  <div className="text-[13px] font-bold text-[#e1e2e3] mb-2">Scopes ({keyScopes.size})</div>
                  <div className="flex flex-wrap gap-1.5">
                    {availableScopes.map((sc) => (
                      <button
                        key={sc}
                        type="button"
                        onClick={() =>
                          setKeyScopes((prev) => {
                            const next = new Set(prev);
                            if (next.has(sc)) next.delete(sc);
                            else next.add(sc);
                            return next;
                          })
                        }
                        className={`px-2 py-1 rounded-[3px] border text-[11px] font-mono cursor-pointer ${
                          keyScopes.has(sc)
                            ? 'bg-arr-primary border-arr-primary-border text-white'
                            : 'bg-arr-card border-[#4a5158] text-[#909293]'
                        }`}
                      >
                        {sc}
                      </button>
                    ))}
                  </div>
                </div>
              </ArrModal>
            )}
          </>
        )}

        {section === 'events' && (
          <FieldSet legend="Events">
            {!canReadAudit ? (
              <ArrAlert kind="warning">Audit read permission is required to view system events.</ArrAlert>
            ) : (
              <div className="overflow-x-auto">
                <table className="arr-table">
                  <thead>
                    <tr>
                      <th>Time</th>
                      <th>Component</th>
                      <th>Action</th>
                      <th>Actor</th>
                      <th>Source IP</th>
                    </tr>
                  </thead>
                  <tbody>
                    {(auditQuery.data ?? []).map((ev) => (
                      <tr key={ev.id}>
                        <td className="font-mono text-[12px] text-[#909293] whitespace-nowrap">
                          {new Date(ev.occurredAt).toLocaleString()}
                        </td>
                        <td>
                          <ArrLabel kind="default">
                            {ev.resourceType}
                            {ev.resourceId ? `:${ev.resourceId}` : ''}
                          </ArrLabel>
                        </td>
                        <td className="font-mono text-arr-accent">{ev.action}</td>
                        <td className="text-[#eeeeee]">{ev.actorUsername ?? ev.actorApiKeyPrefix ?? 'system'}</td>
                        <td className="font-mono text-[12px] text-[#909293]">{ev.ipAddress ?? '-'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </FieldSet>
        )}
      </PageContentBody>
    </div>
  );
}
