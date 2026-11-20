import { useId, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  ArrowRight,
  Bot,
  Clock,
  Database,
  Film,
  HardDrive,
  Layers,
  Play,
  RefreshCw,
  Shield,
  Sparkles,
  Trash2,
  Workflow,
} from 'lucide-react';
import { apiFetch, formatBytes } from '../../lib/api';
import {
  ArrAlert,
  ArrButton,
  ArrLabel,
  FieldSet,
  PageToolbar,
  PageToolbarButton,
  PageToolbarSeparator,
  SmartBreakText,
} from '../arr/ArrPrimitives';
import type {
  CleanerrOverviewStatsDto,
  CleanerrStatusResponse,
} from './cleanerrTypes';

export type CleanerrOverviewNavTarget =
  | 'overview'
  | 'studio'
  | 'queue'
  | 'media'
  | 'exclusions'
  | 'discord';

export interface CleanerrOverviewSectionProps {
  status?: CleanerrStatusResponse;
  onNavigateSection?: (section: CleanerrOverviewNavTarget) => void;
}

interface PlexSyncResult {
  serverUrl?: string;
  sectionsScanned: number;
  moviesSynced: number;
  showsSynced: number;
  activeStreamsDetected?: number;
}

interface ProcessQueueResult {
  evaluated?: number;
  evaluatedStagedCount?: number;
  rescuedCount: number;
  deletedCount: number;
  deferredActiveStreamCount: number;
  reclaimedBytes: number;
  finalWarningsSent?: number;
}

function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return 'Never';
  const parsed = new Date(iso);
  if (Number.isNaN(parsed.getTime())) return iso;
  return parsed.toLocaleString();
}

function formatGb(bytes: number): string {
  const gb = bytes / (1024 * 1024 * 1024);
  if (gb >= 100) return `${gb.toFixed(1)} GB`;
  if (gb >= 0.01) return `${gb.toFixed(2)} GB`;
  return formatBytes(bytes);
}

export function CleanerrOverviewSection({
  status,
  onNavigateSection,
}: CleanerrOverviewSectionProps) {
  const qc = useQueryClient();
  const libraryTableCaptionId = useId();
  const forecastHeadingId = useId();
  const [banner, setBanner] = useState<{
    kind: 'info' | 'success' | 'danger';
    message: string;
  } | null>(null);

  const statsQuery = useQuery({
    queryKey: ['cleanerr-stats'],
    queryFn: () =>
      apiFetch<CleanerrOverviewStatsDto>('/api/v1/cleanerr/stats'),
  });

  const syncCatalogMutation = useMutation({
    mutationFn: () =>
      apiFetch<{ synced?: boolean; count?: number; syncedCount?: number }>(
        '/api/v1/cleanerr/media/sync-catalog',
        {
          method: 'POST',
          body: JSON.stringify({}),
        },
      ),
    onSuccess: (res) => {
      const count = res.count ?? res.syncedCount ?? 0;
      setBanner({
        kind: 'success',
        message: `Managerr catalog synced: ${count} media item(s) indexed.`,
      });
      void qc.invalidateQueries({ queryKey: ['cleanerr-stats'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-media'] });
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Failed to sync Managerr catalog.',
      });
    },
  });

  const syncPlexMutation = useMutation({
    mutationFn: () =>
      apiFetch<PlexSyncResult>('/api/v1/cleanerr/sync-plex', {
        method: 'POST',
        body: JSON.stringify({}),
      }),
    onSuccess: (res) => {
      setBanner({
        kind: 'success',
        message: `Plex watch history synced (read-only): ${res.moviesSynced} movies, ${res.showsSynced} series (${res.activeStreamsDetected ?? 0} active stream(s)).`,
      });
      void qc.invalidateQueries({ queryKey: ['cleanerr-stats'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-media'] });
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Failed to sync Plex watch history.',
      });
    },
  });

  const processQueueMutation = useMutation({
    mutationFn: () =>
      apiFetch<ProcessQueueResult>('/api/v1/cleanerr/queue/process', {
        method: 'POST',
        body: JSON.stringify({}),
      }),
    onSuccess: (res) => {
      const evaluated = res.evaluated ?? res.evaluatedStagedCount ?? 0;
      setBanner({
        kind: 'info',
        message: `Scheduled queue check completed: evaluated ${evaluated} staged item(s) — Rescued: ${res.rescuedCount}, Deleted: ${res.deletedCount} (${formatBytes(res.reclaimedBytes)} reclaimed), Deferred active streams: ${res.deferredActiveStreamCount}.`,
      });
      void qc.invalidateQueries({ queryKey: ['cleanerr-stats'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-queue'] });
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Failed to process Leaving Soon queue.',
      });
    },
  });

  const overview = statsQuery.data;
  const libraryStorage = overview?.libraryStorage ?? [];
  const reclaimForecast = overview?.reclaimForecast ?? [];
  const gatewayConnected =
    overview?.discordGatewayConnected ?? status?.gatewayConnected ?? false;
  const botUsername = status?.gatewayBotUsername;
  const diskFreePercent = Math.min(
    100,
    Math.max(0, overview?.diskFreePercent ?? 0),
  );

  return (
    <div className="flex flex-col flex-1">
      <PageToolbar
        left={
          <>
            <PageToolbarButton
              icon={RefreshCw}
              label="Refresh"
              spinning={statsQuery.isFetching}
              onClick={() => {
                void statsQuery.refetch();
              }}
            />
            {status?.canWriteWorkflows && (
              <>
                <PageToolbarSeparator />
                <PageToolbarButton
                  icon={Database}
                  label="Sync Catalog"
                  title="Index local Managerr catalog into Cleanerr"
                  spinning={syncCatalogMutation.isPending}
                  onClick={() => syncCatalogMutation.mutate()}
                />
                <PageToolbarButton
                  icon={Film}
                  label="Sync Plex"
                  title="Read-only Plex watch history and active stream sync"
                  spinning={syncPlexMutation.isPending}
                  onClick={() => syncPlexMutation.mutate()}
                />
              </>
            )}
            {status?.canExecute && (
              <PageToolbarButton
                icon={Play}
                label="Process Queue"
                title="Run scheduled check and process expired Leaving Soon items"
                spinning={processQueueMutation.isPending}
                onClick={() => processQueueMutation.mutate()}
              />
            )}
            {onNavigateSection && (
              <>
                <PageToolbarSeparator />
                <PageToolbarButton
                  icon={Clock}
                  label="Leaving Soon"
                  title="Open Leaving Soon queue"
                  onClick={() => onNavigateSection('queue')}
                />
                <PageToolbarButton
                  icon={Workflow}
                  label="Studio"
                  title="Open Cleanerr Workflow & Rule Studio"
                  onClick={() => onNavigateSection('studio')}
                />
              </>
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

      <div className="p-5 lg:p-6 space-y-6 overflow-x-hidden">
        {banner && <ArrAlert kind={banner.kind}>{banner.message}</ArrAlert>}

        {statsQuery.isError && (
          <ArrAlert kind="danger">
            {(statsQuery.error as Error)?.message ??
              'Failed to load Cleanerr overview statistics.'}
          </ArrAlert>
        )}

        {/* Primary KPI Cards */}
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 xl:grid-cols-7 gap-3.5">
          {/* Media Items + GB */}
          <div className="arr-card p-3.5 flex flex-col justify-between">
            <div className="flex items-center justify-between text-[var(--arr-muted)] mb-1">
              <span className="text-[11px] font-semibold uppercase tracking-wider">
                Media Items
              </span>
              <Layers
                aria-hidden="true"
                className="h-4 w-4 text-[var(--arr-accent)]"
              />
            </div>
            <div className="text-[22px] font-bold text-[var(--arr-text)]">
              {overview?.totalMediaItems ?? 0}
            </div>
            <div className="text-[11px] text-[var(--arr-muted)] truncate">
              {formatGb(overview?.totalMediaSizeBytes ?? 0)} tracked
            </div>
          </div>

          {/* Staged Leaving Soon + Reclaimable GB */}
          <div className="arr-card p-3.5 flex flex-col justify-between border-l-4 border-l-[#ffa726]">
            <div className="flex items-center justify-between text-[var(--arr-muted)] mb-1">
              <span className="text-[11px] font-semibold uppercase tracking-wider text-[#ffa726]">
                Leaving Soon
              </span>
              <Clock aria-hidden="true" className="h-4 w-4 text-[#ffa726]" />
            </div>
            <div className="text-[22px] font-bold text-[#ffa726]">
              {overview?.stagedCount ?? status?.stagedCount ?? 0}
            </div>
            <div className="text-[11px] text-[#27c24c] font-medium truncate">
              +{formatGb(overview?.stagedReclaimableBytes ?? overview?.stagedSizeBytes ?? 0)}{' '}
              reclaimable
            </div>
          </div>

          {/* Disk Free GB / % */}
          <div className="arr-card p-3.5 flex flex-col justify-between">
            <div className="flex items-center justify-between text-[var(--arr-muted)] mb-1">
              <span className="text-[11px] font-semibold uppercase tracking-wider">
                Disk Free
              </span>
              <HardDrive
                aria-hidden="true"
                className="h-4 w-4 text-[#5d9cec]"
              />
            </div>
            <div className="text-[22px] font-bold text-[var(--arr-text)]">
              {(overview?.diskFreeGb ?? 0).toFixed(1)} GB
              <span className="text-[12px] font-normal text-[var(--arr-muted)] ml-1">
                ({Math.round(diskFreePercent)}%)
              </span>
            </div>
            <div
              role="progressbar"
              aria-label="Disk free percentage"
              aria-valuenow={Math.round(diskFreePercent)}
              aria-valuemin={0}
              aria-valuemax={100}
              className="w-full bg-black/30 h-1.5 rounded-full overflow-hidden mt-1.5"
            >
              <div
                className="bg-[#5d9cec] h-full transition-all"
                style={{ width: `${diskFreePercent}%` }}
              />
            </div>
          </div>

          {/* Active Exclusions */}
          <div className="arr-card p-3.5 flex flex-col justify-between">
            <div className="flex items-center justify-between text-[var(--arr-muted)] mb-1">
              <span className="text-[11px] font-semibold uppercase tracking-wider">
                Exclusions
              </span>
              <Shield aria-hidden="true" className="h-4 w-4 text-[#27c24c]" />
            </div>
            <div className="text-[22px] font-bold text-[var(--arr-text)]">
              {overview?.activeExclusionsCount ?? 0}
            </div>
            <div className="text-[11px] text-[var(--arr-muted)] truncate">
              Active protection rules
            </div>
          </div>

          {/* Enabled Workflows */}
          <div className="arr-card p-3.5 flex flex-col justify-between">
            <div className="flex items-center justify-between text-[var(--arr-muted)] mb-1">
              <span className="text-[11px] font-semibold uppercase tracking-wider">
                Workflows
              </span>
              <Workflow
                aria-hidden="true"
                className="h-4 w-4 text-[#9b6ef3]"
              />
            </div>
            <div className="text-[22px] font-bold text-[var(--arr-text)]">
              {overview?.enabledWorkflowsCount ?? 0}
            </div>
            <div
              className="text-[11px] text-[var(--arr-muted)] truncate"
              title={formatDateTime(overview?.lastRunAt)}
            >
              Last run: {overview?.lastRunAt ? formatDateTime(overview.lastRunAt) : 'Never'}
            </div>
          </div>

          {/* Rescued / Deleted Totals */}
          <div className="arr-card p-3.5 flex flex-col justify-between">
            <div className="flex items-center justify-between text-[var(--arr-muted)] mb-1">
              <span className="text-[11px] font-semibold uppercase tracking-wider">
                Rescued / Deleted
              </span>
              <Trash2 aria-hidden="true" className="h-4 w-4 text-[#f05050]" />
            </div>
            <div className="text-[20px] font-bold text-[var(--arr-text)]">
              <span className="text-[#27c24c]">
                {overview?.rescuedTotalCount ?? 0}
              </span>
              <span className="text-[var(--arr-muted)] mx-1">/</span>
              <span className="text-[#f05050]">
                {overview?.deletedTotalCount ?? 0}
              </span>
            </div>
            <div className="text-[11px] text-[var(--arr-muted)] truncate">
              Lifetime queue outcomes
            </div>
          </div>

          {/* Discord Bot / Gateway Status */}
          <div className="arr-card p-3.5 flex flex-col justify-between">
            <div className="flex items-center justify-between text-[var(--arr-muted)] mb-1">
              <span className="text-[11px] font-semibold uppercase tracking-wider">
                Discord Bot
              </span>
              <Bot aria-hidden="true" className="h-4 w-4 text-[#9b6ef3]" />
            </div>
            <div className="pt-0.5">
              <ArrLabel
                kind={
                  gatewayConnected
                    ? 'success'
                    : overview?.discordConfigured
                      ? 'warning'
                      : 'default'
                }
              >
                {gatewayConnected
                  ? 'Gateway Connected'
                  : overview?.discordConfigured
                    ? 'Configured (Standby)'
                    : 'Not Configured'}
              </ArrLabel>
            </div>
            <div
              className="text-[11px] text-[var(--arr-muted)] truncate"
              title={botUsername ?? overview?.discordAuthMode}
            >
              {botUsername
                ? `@${botUsername}`
                : `Auth: ${overview?.discordAuthMode ?? 'channel_members'}`}
            </div>
          </div>
        </div>

        {/* Native Staging Callout */}
        <div className="flex flex-wrap items-center justify-between gap-3 px-3.5 py-2.5 bg-[var(--arr-card-center-bg)] border border-[var(--arr-card-border)] rounded-[4px] text-[12px] text-[var(--arr-text-dim)]">
          <div className="flex items-center gap-2">
            <Sparkles
              aria-hidden="true"
              className="h-4 w-4 text-[var(--arr-accent)] shrink-0"
            />
            <span>
              Cleanerr staging runs natively inside Managerr and Discord with
              read-only Plex watch-state sync.
            </span>
          </div>
          {onNavigateSection && (
            <div className="flex items-center gap-2">
              <ArrButton
                variant="default"
                className="!h-[28px] !px-2.5 !text-[12px]"
                onClick={() => onNavigateSection('queue')}
              >
                Review Leaving Soon
                <ArrowRight aria-hidden="true" className="h-3.5 w-3.5" />
              </ArrButton>
              <ArrButton
                variant="primary"
                className="!h-[28px] !px-2.5 !text-[12px]"
                onClick={() => onNavigateSection('studio')}
              >
                Open Studio
                <ArrowRight aria-hidden="true" className="h-3.5 w-3.5" />
              </ArrButton>
            </div>
          )}
        </div>

        {/* Reclaim Forecast */}
        <FieldSet
          legend={<span id={forecastHeadingId}>Reclaim Forecast</span>}
        >
          {reclaimForecast.length === 0 ? (
            <div className="p-4 rounded-[4px] border border-[var(--arr-card-border)] bg-[var(--arr-card-bg)] text-[13px] text-[var(--arr-muted)]">
              No staged items currently scheduled in the reclaim forecast.
            </div>
          ) : (
            <div
              role="list"
              aria-labelledby={forecastHeadingId}
              className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-5 gap-3"
            >
              {reclaimForecast.map((bucket) => {
                const isDueNow = bucket.bucketKey === 'due_now';
                return (
                  <div
                    key={bucket.bucketKey}
                    role="listitem"
                    className={`arr-card p-3.5 flex flex-col justify-between ${
                      isDueNow && bucket.itemCount > 0
                        ? 'border-l-4 border-l-[#f05050]'
                        : ''
                    }`}
                  >
                    <div className="text-[11px] font-bold uppercase tracking-wider text-[var(--arr-muted)]">
                      {bucket.label}
                    </div>
                    <div className="mt-1.5 flex items-baseline justify-between gap-2">
                      <span
                        className={`text-[20px] font-bold ${
                          isDueNow && bucket.itemCount > 0
                            ? 'text-[#f05050]'
                            : 'text-[var(--arr-text)]'
                        }`}
                      >
                        {bucket.itemCount}
                      </span>
                      <span className="text-[12px] font-mono text-[#27c24c] font-semibold">
                        {formatGb(bucket.reclaimableBytes)}
                      </span>
                    </div>
                    <div className="text-[11px] text-[var(--arr-muted)] mt-0.5">
                      {bucket.itemCount === 1 ? '1 item staged' : `${bucket.itemCount} items staged`}
                    </div>
                  </div>
                );
              })}
            </div>
          )}
        </FieldSet>

        {/* Per-Library Disk Stats Table */}
        <FieldSet legend="Per-Library Disk Stats">
          <div
            tabIndex={0}
            role="region"
            aria-labelledby={libraryTableCaptionId}
            className="cleanerr-table-wrapper cleanerr-responsive-table rounded-[4px] border border-[var(--arr-card-border)] bg-[var(--arr-card-bg)]"
          >
            <table className="arr-table w-full">
              <caption id={libraryTableCaptionId} className="sr-only">
                Per-library disk usage and drive free space statistics
              </caption>
              <thead>
                <tr>
                  <th>Library</th>
                  <th>Type</th>
                  <th>Root Folder</th>
                  <th>Items</th>
                  <th>Library Size</th>
                  <th>Drive Free / Total</th>
                </tr>
              </thead>
              <tbody>
                {statsQuery.isLoading ? (
                  <tr>
                    <td
                      colSpan={6}
                      className="py-8 text-center text-[var(--arr-muted)]"
                    >
                      Loading library storage stats...
                    </td>
                  </tr>
                ) : libraryStorage.length === 0 ? (
                  <tr>
                    <td
                      colSpan={6}
                      className="py-8 text-center text-[var(--arr-muted)]"
                    >
                      No library storage statistics available yet. Configure
                      root folders and run Sync Catalog.
                    </td>
                  </tr>
                ) : (
                  libraryStorage.map((lib) => {
                    const pct = Math.min(
                      100,
                      Math.max(0, lib.driveFreePercent ?? 0),
                    );
                    const meterColor =
                      pct < 10
                        ? 'bg-[#f05050]'
                        : pct < 20
                          ? 'bg-[#ffa726]'
                          : 'bg-[#27c24c]';
                    return (
                      <tr key={lib.libraryId}>
                        <th
                          scope="row"
                          className="text-left font-semibold text-[var(--arr-text)]"
                        >
                          {lib.libraryTitle}
                        </th>
                        <td data-label="Type">
                          <ArrLabel kind="info">{lib.mediaType}</ArrLabel>
                        </td>
                        <td
                          data-label="Root Folder"
                          className="font-mono text-[12px] text-[var(--arr-text-dim)]"
                        >
                          <SmartBreakText text={lib.rootFolder || '—'} />
                        </td>
                        <td data-label="Items" className="font-mono">
                          {lib.itemCount}
                        </td>
                        <td data-label="Library Size" className="font-mono whitespace-nowrap">
                          {formatGb(lib.libraryBytes)}
                        </td>
                        <td data-label="Drive Free / Total" className="min-w-[200px]">
                          <div className="flex items-center justify-between gap-2 text-[12px] font-mono mb-1">
                            <span>
                              {lib.driveFreeGb.toFixed(1)} GB /{' '}
                              {lib.driveTotalGb.toFixed(1)} GB
                            </span>
                            <span className="text-[var(--arr-muted)]">
                              {pct.toFixed(0)}% free
                            </span>
                          </div>
                          <div
                            role="progressbar"
                            aria-label={`${lib.libraryTitle} drive free space`}
                            aria-valuenow={Math.round(pct)}
                            aria-valuemin={0}
                            aria-valuemax={100}
                            className="w-full bg-black/30 h-1.5 rounded-full overflow-hidden"
                          >
                            <div
                              className={`${meterColor} h-full transition-all`}
                              style={{ width: `${pct}%` }}
                            />
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
    </div>
  );
}
