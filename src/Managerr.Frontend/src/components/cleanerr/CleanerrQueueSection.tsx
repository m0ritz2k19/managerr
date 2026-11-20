import { useId, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  BellRing,
  Clock,
  HelpCircle,
  Play,
  Plus,
  RefreshCw,
  RotateCcw,
  Search,
  Shield,
  ShieldCheck,
  Trash2,
  X,
} from 'lucide-react';
import { apiFetch, formatBytes } from '../../lib/api';
import {
  ArrAlert,
  ArrButton,
  ArrLabel,
  FieldSet,
  FilterBubble,
  FilterBubbleGroup,
  PageToolbar,
  PageToolbarButton,
  PageToolbarSeparator,
} from '../arr/ArrPrimitives';
import { CleanerrConfirmModal } from './CleanerrConfirmModal';
import { CleanerrManualStageModal } from './CleanerrManualStageModal';
import { CleanerrTestMediaModal } from './CleanerrTestMediaModal';
import type {
  CleanerrGeneralSettings,
  CleanerrQueueItem,
  CleanerrStatusResponse,
} from './cleanerrTypes';

export interface CleanerrQueueSectionProps {
  status?: CleanerrStatusResponse;
}

type QueueStatusFilter = 'staged' | 'rescued' | 'deleted' | 'excluded' | 'all';

interface ProcessQueueResponse {
  evaluated?: number;
  evaluatedStagedCount?: number;
  rescuedCount: number;
  deletedCount: number;
  deferredActiveStreamCount: number;
  reclaimedBytes: number;
  finalWarningsSent?: number;
}

function formatCountdown(deleteAtIso: string): {
  label: string;
  urgent: boolean;
  expired: boolean;
} {
  const diffMs = new Date(deleteAtIso).getTime() - Date.now();
  if (Number.isNaN(diffMs)) {
    return { label: deleteAtIso, urgent: false, expired: false };
  }
  if (diffMs <= 0) {
    return { label: 'Due Now', urgent: true, expired: true };
  }
  const totalHours = Math.floor(diffMs / (1000 * 60 * 60));
  const days = Math.floor(totalHours / 24);
  const hours = totalHours % 24;
  if (days > 0) {
    return {
      label: `In ${days}d ${hours}h`,
      urgent: days < 1,
      expired: false,
    };
  }
  if (hours > 0) {
    return { label: `In ${hours}h`, urgent: true, expired: false };
  }
  const mins = Math.max(1, Math.floor(diffMs / (1000 * 60)));
  return { label: `In ${mins}m`, urgent: true, expired: false };
}

function formatShortDate(iso: string | null | undefined): string {
  if (!iso) return '—';
  const parsed = new Date(iso);
  if (Number.isNaN(parsed.getTime())) return iso.slice(0, 16);
  return parsed.toLocaleString();
}

export function CleanerrQueueSection({ status }: CleanerrQueueSectionProps) {
  const qc = useQueryClient();
  const statusFilterGroupId = useId();
  const queueTableCaptionId = useId();
  const searchInputId = useId();

  const [statusFilter, setStatusFilter] = useState<QueueStatusFilter>('staged');
  const [searchQuery, setSearchQuery] = useState('');
  const [manualStageOpen, setManualStageOpen] = useState(false);
  const [deleteConfirmItem, setDeleteConfirmItem] =
    useState<CleanerrQueueItem | null>(null);
  const [excludeAllConfirmOpen, setExcludeAllConfirmOpen] = useState(false);
  const [whyItem, setWhyItem] = useState<CleanerrQueueItem | null>(null);
  const [banner, setBanner] = useState<{
    kind: 'info' | 'success' | 'danger';
    message: string;
  } | null>(null);

  const queueQuery = useQuery({
    queryKey: ['cleanerr-queue'],
    queryFn: () => apiFetch<CleanerrQueueItem[]>('/api/v1/cleanerr/queue'),
  });

  const settingsQuery = useQuery({
    queryKey: ['cleanerr-settings'],
    queryFn: () =>
      apiFetch<CleanerrGeneralSettings>('/api/v1/cleanerr/settings'),
  });

  const defaultPostponeDays = Math.max(
    1,
    settingsQuery.data?.defaultPostponeDays ?? 7,
  );

  const invalidateQueue = () => {
    void qc.invalidateQueries({ queryKey: ['cleanerr-queue'] });
    void qc.invalidateQueries({ queryKey: ['cleanerr-stats'] });
    void qc.invalidateQueries({ queryKey: ['cleanerr-media'] });
  };

  // Bulk / Toolbar Mutations
  const processQueueMutation = useMutation({
    mutationFn: () =>
      apiFetch<ProcessQueueResponse>('/api/v1/cleanerr/queue/process', {
        method: 'POST',
        body: JSON.stringify({}),
      }),
    onSuccess: (res) => {
      const evaluated = res.evaluated ?? res.evaluatedStagedCount ?? 0;
      setBanner({
        kind: 'info',
        message: `Queue processed (${evaluated} evaluated): Rescued ${res.rescuedCount}, Deleted ${res.deletedCount} (${formatBytes(res.reclaimedBytes)} reclaimed), Deferred active streams: ${res.deferredActiveStreamCount}.`,
      });
      invalidateQueue();
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Failed to process Leaving Soon queue.',
      });
    },
  });

  const excludeAllMutation = useMutation({
    mutationFn: (stagedIds: number[]) =>
      apiFetch<{ excludedCount?: number; count?: number }>(
        '/api/v1/cleanerr/queue/exclude-all',
        {
          method: 'POST',
          body: JSON.stringify({
            queueIds: stagedIds,
            reason: 'Bulk excluded all staged items via Leaving Soon Queue',
          }),
        },
      ),
    onSuccess: (res) => {
      setExcludeAllConfirmOpen(false);
      const cnt = res.excludedCount ?? res.count ?? 0;
      setBanner({
        kind: 'success',
        message: `Excluded ${cnt || 'all'} staged item(s) and protected them from deletion.`,
      });
      void qc.invalidateQueries({ queryKey: ['cleanerr-exclusions'] });
      invalidateQueue();
    },
    onError: (err: Error) => {
      setExcludeAllConfirmOpen(false);
      setBanner({
        kind: 'danger',
        message: err.message || 'Failed to exclude staged items.',
      });
    },
  });

  const postponeAllMutation = useMutation({
    mutationFn: (days: number) =>
      apiFetch<{ postponedCount?: number; count?: number }>(
        '/api/v1/cleanerr/queue/postpone-all',
        {
          method: 'POST',
          body: JSON.stringify({
            days,
            reason: `Postponed all staged items +${days}d via Web UI`,
          }),
        },
      ),
    onSuccess: (res, days) => {
      const cnt = res.postponedCount ?? res.count ?? 0;
      setBanner({
        kind: 'success',
        message: `Postponed ${cnt || 'all'} staged item(s) by +${days} days.`,
      });
      invalidateQueue();
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Failed to postpone all staged items.',
      });
    },
  });

  // Per-row Mutations
  const rescueMutation = useMutation({
    mutationFn: (id: number) =>
      apiFetch(`/api/v1/cleanerr/queue/${id}/rescue`, {
        method: 'POST',
        body: JSON.stringify({ reason: 'Rescued via Leaving Soon Queue' }),
      }),
    onSuccess: () => {
      setBanner({
        kind: 'success',
        message: 'Media item rescued from Leaving Soon queue.',
      });
      invalidateQueue();
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Failed to rescue item.',
      });
    },
  });

  const excludeMutation = useMutation({
    mutationFn: (id: number) =>
      apiFetch(`/api/v1/cleanerr/queue/${id}/exclude`, {
        method: 'POST',
        body: JSON.stringify({
          reason: 'Excluded permanently via Leaving Soon Queue',
        }),
      }),
    onSuccess: () => {
      setBanner({
        kind: 'success',
        message: 'Media item excluded from future Cleanerr runs.',
      });
      void qc.invalidateQueries({ queryKey: ['cleanerr-exclusions'] });
      invalidateQueue();
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Failed to exclude item.',
      });
    },
  });

  const postponeMutation = useMutation({
    mutationFn: ({ id, days }: { id: number; days: number }) =>
      apiFetch(`/api/v1/cleanerr/queue/${id}/postpone`, {
        method: 'POST',
        body: JSON.stringify({
          days,
          reason: `Postponed +${days}d via Leaving Soon Queue`,
        }),
      }),
    onSuccess: (_res, vars) => {
      setBanner({
        kind: 'info',
        message: `Postponed item deletion by +${vars.days} days.`,
      });
      invalidateQueue();
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Failed to postpone item.',
      });
    },
  });

  const deleteNowMutation = useMutation({
    mutationFn: (id: number) =>
      apiFetch(`/api/v1/cleanerr/queue/${id}/delete-now`, {
        method: 'POST',
        body: JSON.stringify({}),
      }),
    onSuccess: () => {
      setDeleteConfirmItem(null);
      setBanner({
        kind: 'info',
        message: 'Immediate deletion executed for queued item.',
      });
      invalidateQueue();
    },
    onError: (err: Error) => {
      setDeleteConfirmItem(null);
      setBanner({
        kind: 'danger',
        message: err.message || 'Failed to execute immediate deletion.',
      });
    },
  });

  const removeHistoryMutation = useMutation({
    mutationFn: (id: number) =>
      apiFetch(`/api/v1/cleanerr/queue/${id}`, {
        method: 'DELETE',
      }),
    onSuccess: () => {
      setBanner({
        kind: 'info',
        message: 'Removed history entry from queue.',
      });
      invalidateQueue();
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Failed to remove queue entry.',
      });
    },
  });

  const counts = useMemo(() => {
    const items = queueQuery.data ?? [];
    let staged = 0;
    let rescued = 0;
    let deleted = 0;
    let excluded = 0;
    for (const item of items) {
      if (item.status === 'staged') staged += 1;
      else if (item.status === 'rescued') rescued += 1;
      else if (item.status === 'deleted') deleted += 1;
      else if (item.status === 'excluded') excluded += 1;
    }
    return {
      staged,
      rescued,
      deleted,
      excluded,
      all: items.length,
    };
  }, [queueQuery.data]);

  const stagedItems = useMemo(
    () => (queueQuery.data ?? []).filter((q) => q.status === 'staged'),
    [queueQuery.data],
  );

  const filteredQueue = useMemo(() => {
    let items = queueQuery.data ?? [];
    if (statusFilter !== 'all') {
      items = items.filter((q) => q.status === statusFilter);
    }
    if (searchQuery.trim()) {
      const q = searchQuery.trim().toLowerCase();
      items = items.filter(
        (i) =>
          i.mediaTitle.toLowerCase().includes(q) ||
          i.parentTitle?.toLowerCase().includes(q) ||
          i.grandparentTitle?.toLowerCase().includes(q) ||
          i.matchedReason?.toLowerCase().includes(q) ||
          i.rescuedReason?.toLowerCase().includes(q) ||
          i.classificationKind?.toLowerCase().includes(q) ||
          i.seerrRequestedBy?.toLowerCase().includes(q),
      );
    }
    return items;
  }, [queueQuery.data, statusFilter, searchQuery]);

  const canWrite = Boolean(status?.canWriteWorkflows);
  const canExecute = Boolean(status?.canExecute);
  const canRescue = Boolean(status?.canRescue);
  const canExclude = Boolean(status?.canExclude);
  const canManualStage = canWrite;
  const canRemoveHistory = canWrite;

  return (
    <div className="flex flex-col flex-1">
      <PageToolbar
        left={
          <>
            <PageToolbarButton
              icon={RefreshCw}
              label="Refresh"
              spinning={queueQuery.isFetching}
              onClick={() => {
                void queueQuery.refetch();
              }}
            />
            {canManualStage && (
              <>
                <PageToolbarSeparator />
                <PageToolbarButton
                  icon={Plus}
                  label="Manual Stage"
                  title="Manually stage media items into Leaving Soon queue"
                  onClick={() => setManualStageOpen(true)}
                />
              </>
            )}
            {canExecute && (
              <PageToolbarButton
                icon={Play}
                label="Process Queue Now"
                title="Evaluate grace expirations and execute due deletions"
                spinning={processQueueMutation.isPending}
                onClick={() => processQueueMutation.mutate()}
              />
            )}
          </>
        }
        right={
          status && (
            <ArrLabel kind={status.canWriteWorkflows ? 'success' : 'default'}>
              {status.canWriteWorkflows
                ? 'Cleanerr Admin'
                : status.canExclude
                  ? `Exclusion Limit: ${status.cleanerrExclusionLimit ?? 'Unlimited'}`
                  : 'Read Only'}
            </ArrLabel>
          )
        }
      />

      <div className="p-5 lg:p-6 space-y-5 overflow-x-hidden">
        {banner && <ArrAlert kind={banner.kind}>{banner.message}</ArrAlert>}

        <FieldSet
          legend="Leaving Soon & Deletion Queue"
          action={
            <div className="flex flex-wrap items-center gap-2">
              {canRescue && stagedItems.length > 0 && (
                <ArrButton
                  variant="default"
                  className="!h-[30px] !px-2.5 !text-[12px]"
                  busy={postponeAllMutation.isPending}
                  onClick={() => postponeAllMutation.mutate(defaultPostponeDays)}
                >
                  <Clock aria-hidden="true" className="h-3.5 w-3.5" />
                  Postpone All +{defaultPostponeDays}d
                </ArrButton>
              )}
              {canExclude && stagedItems.length > 0 && (
                <ArrButton
                  variant="primary"
                  className="!h-[30px] !px-2.5 !text-[12px]"
                  busy={excludeAllMutation.isPending}
                  onClick={() => setExcludeAllConfirmOpen(true)}
                >
                  <ShieldCheck aria-hidden="true" className="h-3.5 w-3.5" />
                  Exclude All Staged ({stagedItems.length})
                </ArrButton>
              )}
            </div>
          }
        >
          {/* Search and Filter Bubbles */}
          <div className="flex flex-wrap items-center justify-between gap-3 mb-4">
            <search className="w-full sm:w-auto">
              <div className="relative">
                <label htmlFor={searchInputId} className="sr-only">
                  Filter queue items
                </label>
                <Search
                  aria-hidden="true"
                  className="absolute left-2.5 top-2 h-4 w-4 text-[var(--arr-muted)]"
                />
                <input
                  id={searchInputId}
                  type="search"
                  autoCorrect="off"
                  autoCapitalize="none"
                  spellCheck={false}
                  placeholder="Filter by title, requester, or reason..."
                  value={searchQuery}
                  onChange={(e) => setSearchQuery(e.target.value)}
                  className="arr-input !h-[32px] pl-9 !text-[12px] sm:!w-72"
                />
              </div>
            </search>

            <FilterBubbleGroup label="Status" labelId={statusFilterGroupId}>
              <FilterBubble
                label="Staged"
                active={statusFilter === 'staged'}
                onClick={() => setStatusFilter('staged')}
                count={counts.staged}
                tone="amber"
                groupLabelId={statusFilterGroupId}
              />
              <FilterBubble
                label="Rescued"
                active={statusFilter === 'rescued'}
                onClick={() => setStatusFilter('rescued')}
                count={counts.rescued}
                tone="blue"
                groupLabelId={statusFilterGroupId}
              />
              <FilterBubble
                label="Deleted"
                active={statusFilter === 'deleted'}
                onClick={() => setStatusFilter('deleted')}
                count={counts.deleted}
                tone="rose"
                groupLabelId={statusFilterGroupId}
              />
              <FilterBubble
                label="Excluded"
                active={statusFilter === 'excluded'}
                onClick={() => setStatusFilter('excluded')}
                count={counts.excluded}
                tone="green"
                groupLabelId={statusFilterGroupId}
              />
              <FilterBubble
                label="All"
                active={statusFilter === 'all'}
                onClick={() => setStatusFilter('all')}
                count={counts.all}
                tone="cyan"
                groupLabelId={statusFilterGroupId}
              />
            </FilterBubbleGroup>
          </div>

          <div
            tabIndex={0}
            role="region"
            aria-labelledby={queueTableCaptionId}
            className="cleanerr-table-wrapper cleanerr-responsive-table rounded-[4px] border border-[var(--arr-card-border)] bg-[var(--arr-card-bg)]"
          >
            <table className="arr-table w-full text-[12px]">
              <caption id={queueTableCaptionId} className="sr-only">
                Cleanerr Leaving Soon staged items and queue execution history
              </caption>
              <thead>
                <tr>
                  <th>Title &amp; Context</th>
                  <th>Type</th>
                  <th>Size</th>
                  <th>Matched / Outcome Reason</th>
                  <th>Countdown / Timestamp</th>
                  <th>Status</th>
                  <th className="text-right">Actions</th>
                </tr>
              </thead>
              <tbody>
                {queueQuery.isLoading ? (
                  <tr>
                    <td
                      colSpan={7}
                      className="py-10 text-center text-[var(--arr-muted)]"
                    >
                      Loading Leaving Soon queue...
                    </td>
                  </tr>
                ) : queueQuery.isError ? (
                  <tr>
                    <td
                      colSpan={7}
                      className="py-8 text-center text-[var(--arr-danger)]"
                    >
                      {(queueQuery.error as Error)?.message ??
                        'Failed to load Cleanerr queue.'}
                    </td>
                  </tr>
                ) : filteredQueue.length === 0 ? (
                  <tr>
                    <td
                      colSpan={7}
                      className="py-10 text-center text-[var(--arr-muted)]"
                    >
                      {statusFilter === 'staged'
                        ? 'No media items are currently staged in Leaving Soon.'
                        : 'No queue records match the active filter.'}
                    </td>
                  </tr>
                ) : (
                  filteredQueue.map((item) => {
                    const isStaged = item.status === 'staged';
                    const countdown = formatCountdown(item.deleteAt);
                    const fullTitle = item.parentTitle
                      ? `${item.parentTitle} - ${item.mediaTitle}`
                      : item.mediaTitle;

                    return (
                      <tr key={item.id}>
                        <th
                          scope="row"
                          className="text-left font-medium text-[var(--arr-text)]"
                        >
                          <div className="flex items-center gap-2.5">
                            {item.posterUrl && (
                              <img
                                src={item.posterUrl}
                                alt=""
                                className="h-9 w-6 object-cover rounded bg-black/40 shrink-0"
                              />
                            )}
                            <div className="min-w-0">
                              <div className="font-semibold text-[var(--arr-text)] flex items-center gap-1.5 flex-wrap">
                                <span
                                  className="truncate max-w-[260px]"
                                  title={fullTitle}
                                >
                                  {fullTitle}
                                </span>
                                {item.year ? (
                                  <span className="text-[var(--arr-muted)] font-normal text-[11px]">
                                    ({item.year})
                                  </span>
                                ) : null}
                                {item.classificationKind && (
                                  <ArrLabel kind="primary">
                                    {item.classificationKind}
                                  </ArrLabel>
                                )}
                                {item.isManual && (
                                  <ArrLabel kind="warning">Manual</ArrLabel>
                                )}
                                {(item.postponedCount ?? 0) > 0 && (
                                  <ArrLabel kind="purple">
                                    Postponed x{item.postponedCount}
                                  </ArrLabel>
                                )}
                              </div>
                              {(item.grandparentTitle || item.seerrRequestedBy) && (
                                <div className="text-[11px] text-[var(--arr-muted)] truncate">
                                  {item.grandparentTitle
                                    ? `Series: ${item.grandparentTitle}`
                                    : ''}
                                  {item.grandparentTitle && item.seerrRequestedBy
                                    ? ' • '
                                    : ''}
                                  {item.seerrRequestedBy
                                    ? `Requested by ${item.seerrRequestedBy}`
                                    : ''}
                                </div>
                              )}
                            </div>
                          </div>
                        </th>

                        <td data-label="Type">
                          <ArrLabel kind="default">
                            {item.mediaType === 'movie' ? 'Movie' : 'Series'}
                          </ArrLabel>
                        </td>

                        <td
                          data-label="Size"
                          className="whitespace-nowrap font-mono"
                        >
                          {formatBytes(item.sizeBytes)}
                        </td>

                        <td data-label="Reason">
                          <div
                            className="text-[11px] text-[var(--arr-text-dim)] truncate max-w-[230px]"
                            title={
                              item.matchedReason ?? item.rescuedReason ?? '—'
                            }
                          >
                            {item.matchedReason ?? item.rescuedReason ?? '—'}
                          </div>
                        </td>

                        <td data-label="Countdown">
                          {isStaged ? (
                            <div className="space-y-1 whitespace-nowrap">
                              <div className="flex flex-wrap items-center gap-1.5">
                                <ArrLabel
                                  kind={countdown.urgent ? 'danger' : 'warning'}
                                  title={`Scheduled deletion: ${formatShortDate(item.deleteAt)}`}
                                >
                                  <Clock
                                    aria-hidden="true"
                                    className="h-3 w-3"
                                  />
                                  {countdown.label}
                                </ArrLabel>
                                {item.finalWarningSentAt && (
                                  <ArrLabel
                                    kind="info"
                                    title={`Final warning sent at ${formatShortDate(item.finalWarningSentAt)}`}
                                  >
                                    <BellRing
                                      aria-hidden="true"
                                      className="h-3 w-3"
                                    />
                                    Final Warning Sent
                                  </ArrLabel>
                                )}
                              </div>
                              <div className="text-[10px] text-[var(--arr-muted)] font-mono">
                                {formatShortDate(item.deleteAt)}
                              </div>
                            </div>
                          ) : (
                            <span className="text-[11px] text-[var(--arr-muted)] font-mono">
                              {formatShortDate(
                                item.executedAt ?? item.stagedAt,
                              )}
                            </span>
                          )}
                        </td>

                        <td data-label="Status">
                          <ArrLabel
                            kind={
                              item.status === 'staged'
                                ? 'warning'
                                : item.status === 'deleted'
                                  ? 'danger'
                                  : item.status === 'rescued'
                                    ? 'primary'
                                    : 'success'
                            }
                          >
                            {item.status}
                          </ArrLabel>
                        </td>

                        <td
                          data-label="Actions"
                          className="text-right whitespace-nowrap"
                        >
                          <div className="inline-flex flex-wrap items-center justify-end gap-1">
                            <ArrButton
                              variant="default"
                              className="!h-[26px] !px-2 !text-[11px]"
                              title="Explain why this media matched or evaluate against rules"
                              onClick={() => setWhyItem(item)}
                            >
                              <HelpCircle
                                aria-hidden="true"
                                className="h-3 w-3"
                              />
                              Why?
                            </ArrButton>

                            {isStaged ? (
                              <>
                                {canRescue && (
                                  <ArrButton
                                    variant="primary"
                                    className="!h-[26px] !px-2 !text-[11px]"
                                    title="Rescue item from Leaving Soon queue"
                                    busy={rescueMutation.isPending}
                                    onClick={() => rescueMutation.mutate(item.id)}
                                  >
                                    <RotateCcw
                                      aria-hidden="true"
                                      className="h-3 w-3"
                                    />
                                    Rescue
                                  </ArrButton>
                                )}
                                {canExclude && (
                                  <ArrButton
                                    variant="success"
                                    className="!h-[26px] !px-2 !text-[11px]"
                                    title="Exclude item from future deletions"
                                    busy={excludeMutation.isPending}
                                    onClick={() =>
                                      excludeMutation.mutate(item.id)
                                    }
                                  >
                                    <Shield
                                      aria-hidden="true"
                                      className="h-3 w-3"
                                    />
                                    Exclude
                                  </ArrButton>
                                )}
                                {canRescue && (
                                  <ArrButton
                                    variant="default"
                                    className="!h-[26px] !px-2 !text-[11px]"
                                    title={`Postpone deletion by +${defaultPostponeDays} days`}
                                    busy={postponeMutation.isPending}
                                    onClick={() =>
                                      postponeMutation.mutate({
                                        id: item.id,
                                        days: defaultPostponeDays,
                                      })
                                    }
                                  >
                                    +{defaultPostponeDays}d
                                  </ArrButton>
                                )}
                                {canExecute && (
                                  <ArrButton
                                    variant="danger"
                                    className="!h-[26px] !px-2 !text-[11px]"
                                    title="Delete media immediately"
                                    onClick={() => setDeleteConfirmItem(item)}
                                  >
                                    <Trash2
                                      aria-hidden="true"
                                      className="h-3 w-3"
                                    />
                                    Delete Now
                                  </ArrButton>
                                )}
                              </>
                            ) : (
                              canRemoveHistory && (
                                <ArrButton
                                  variant="default"
                                  className="!h-[26px] !px-2 !text-[11px]"
                                  title="Remove historical queue record"
                                  busy={removeHistoryMutation.isPending}
                                  onClick={() =>
                                    removeHistoryMutation.mutate(item.id)
                                  }
                                >
                                  <X
                                    aria-hidden="true"
                                    className="h-3 w-3"
                                  />
                                  Remove
                                </ArrButton>
                              )
                            )}
                          </div>
                        </td>
                      </tr>
                    );
                  })
                )}
              </tbody>
            </table>
          </div>
        </FieldSet>
      </div>

      {/* Manual Stage Modal */}
      {manualStageOpen && (
        <CleanerrManualStageModal
          onClose={() => setManualStageOpen(false)}
          onSuccess={(count) => {
            setBanner({
              kind: 'success',
              message: `Manually staged ${count} media item(s) to Leaving Soon.`,
            });
          }}
        />
      )}

      {/* Why? Test Media Modal */}
      {whyItem && (
        <CleanerrTestMediaModal
          workflowId={whyItem.workflowId}
          initialMediaItemId={whyItem.mediaItemId}
          onClose={() => setWhyItem(null)}
        />
      )}

      {/* Destructive Delete Now Confirm Modal (Cancel auto-focused via CleanerrConfirmModal) */}
      {deleteConfirmItem && (
        <CleanerrConfirmModal
          title="Confirm Immediate Deletion"
          confirmLabel="Delete Now"
          confirmVariant="danger"
          busy={deleteNowMutation.isPending}
          onClose={() => setDeleteConfirmItem(null)}
          onConfirm={() => deleteNowMutation.mutate(deleteConfirmItem.id)}
          description={
            <div className="space-y-2">
              <p>
                Are you sure you want to immediately delete{' '}
                <strong className="text-[var(--arr-text)]">
                  {deleteConfirmItem.mediaTitle}
                </strong>{' '}
                ({formatBytes(deleteConfirmItem.sizeBytes)})?
              </p>
              <p className="text-[12px] text-[var(--arr-muted)]">
                This bypasses the remaining grace countdown and cannot be
                undone.
              </p>
            </div>
          }
        />
      )}

      {/* Exclude All Staged Confirm Modal */}
      {excludeAllConfirmOpen && (
        <CleanerrConfirmModal
          title="Exclude All Staged Items"
          confirmLabel={`Exclude All (${stagedItems.length})`}
          confirmVariant="warning"
          busy={excludeAllMutation.isPending}
          onClose={() => setExcludeAllConfirmOpen(false)}
          onConfirm={() =>
            excludeAllMutation.mutate(stagedItems.map((i) => i.id))
          }
          description={
            <p>
              This will rescue all{' '}
              <strong className="text-[var(--arr-text)]">
                {stagedItems.length}
              </strong>{' '}
              currently staged Leaving Soon item(s) and create permanent
              exclusion rules so they are not re-staged.
            </p>
          }
        />
      )}
    </div>
  );
}
