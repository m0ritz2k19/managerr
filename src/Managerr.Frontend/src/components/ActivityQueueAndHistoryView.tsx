import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  ArrowUpCircle,
  Ban,
  CheckCircle2,
  ChevronLeft,
  ChevronRight,
  ExternalLink,
  FolderOpen,
  ListFilter,
  Loader2,
  Pause,
  Play,
  RefreshCw,
  Search,
  ShieldAlert,
  Trash2,
  X,
} from 'lucide-react';
import {
  apiFetch,
  formatBytes,
  type ActiveDownloadQueueItem,
  type BlocklistEntry as ApiBlocklistEntry,
  type ManualImportExecuteRequest,
  type MediaHistoryItem,
  type ScannedManualImportItem,
  type TrackedDownloadDto,
} from '../lib/api';

interface BlocklistEntry extends ApiBlocklistEntry {
  protocol?: string | null;
  releaseGuid?: string | null;
  downloadUrl?: string | null;
  normalizedTitle?: string | null;
  indexerId?: number | null;
  indexerName?: string | null;
  sizeBytes?: number | null;
  publishedAt?: string | null;
  workId?: number | null;
}
import {
  ArrAlert,
  ArrButton,
  ArrLabel,
  ArrModal,
  FilterBubble,
  PageContentBody,
  PageToolbar,
  PageToolbarButton,
  PageToolbarSeparator,
  SmartBreakText,
  toggleFilterSetValue,
} from './arr/ArrPrimitives';
import { PathBrowserInput } from './arr/PathBrowserModal';

interface CatalogWorkSummary {
  id: number;
  title: string;
  year?: number | null;
  workKind: string;
}

interface ActivityQueueAndHistoryViewProps {
  section: 'queue' | 'history' | 'blocklist' | 'manual-import';
  canManageQueue?: boolean;
  canReadQueue?: boolean;
  canWriteCatalog?: boolean;
  onOpenWork?: (workId: number) => void;
}

const EVENT_TYPE_OPTIONS = [
  { value: '', label: 'All Event Types' },
  { value: 'Grabbed', label: 'Grabbed' },
  { value: 'DownloadFolderImported', label: 'Imported' },
  { value: 'DownloadFailed', label: 'Download Failed' },
  { value: 'MediaFileDeleted', label: 'File Deleted' },
  { value: 'MediaFileRenamed', label: 'File Renamed' },
];

const VERTICAL_OPTIONS = [
  { value: '', label: 'All Verticals' },
  { value: 'tv', label: 'TV Shows' },
  { value: 'movies', label: 'Movies' },
  { value: 'anime', label: 'Anime' },
  { value: 'music', label: 'Music' },
  { value: 'ebooks', label: 'E-Books' },
  { value: 'audiobooks', label: 'Audiobooks' },
  { value: 'comics', label: 'Comics' },
];

export function ActivityQueueAndHistoryView({
  section,
  canManageQueue = false,
  canReadQueue = true,
  onOpenWork,
}: ActivityQueueAndHistoryViewProps) {
  const qc = useQueryClient();
  const [alert, setAlert] = useState<{ kind: 'info' | 'success' | 'warning' | 'danger'; text: string } | null>(null);

  // ==================== QUEUE STATE ====================
  const [itemToRemove, setItemToRemove] = useState<ActiveDownloadQueueItem | null>(null);
  const [removeFormClient, setRemoveFromClient] = useState(true);
  const [removeDeleteData, setRemoveDeleteData] = useState(false);
  const [removeBlocklist, setRemoveBlocklist] = useState(false);
  const [removeSearchReplacement, setRemoveSearchReplacement] = useState(false);

  // Tracked download override import modal
  const [trackedToOverride, setTrackedToOverride] = useState<TrackedDownloadDto | null>(null);
  const [overrideWorkId, setOverrideWorkId] = useState<string>('');
  const [overrideSeason, setOverrideSeason] = useState<string>('');
  const [overrideEpisodes, setOverrideEpisodes] = useState<string>('');
  const [overrideBypass, setOverrideBypass] = useState(true);
  const [overrideStatusMessage, setOverrideStatusMessage] = useState<string | null>(null);

  // ==================== HISTORY STATE ====================
  const [historyPage, setHistoryPage] = useState(1);
  const [historyEventTypes, setHistoryEventTypes] = useState<Set<string>>(() => new Set());
  const [historyVerticals, setHistoryVerticals] = useState<Set<string>>(() => new Set());
  const historyEventTypesParam = useMemo(
    () => Array.from(historyEventTypes).sort().join(','),
    [historyEventTypes],
  );
  const historyVerticalsParam = useMemo(
    () => Array.from(historyVerticals).sort().join(','),
    [historyVerticals],
  );

  // ==================== BLOCKLIST STATE ====================
  const [selectedBlocklistHashes, setSelectedBlocklistHashes] = useState<Set<string>>(new Set());

  // ==================== MANUAL IMPORT STATE ====================
  const [manualFolder, setManualFolder] = useState('/downloads');
  const [manualVerticalHint, setManualVerticalHint] = useState('');
  const [manualImportMode, setManualImportMode] = useState<'hardlink_or_copy' | 'move'>('hardlink_or_copy');
  const [scannedFiles, setScannedFiles] = useState<ScannedManualImportItem[]>([]);
  const [selectedManualFilePaths, setSelectedManualFilePaths] = useState<Set<string>>(new Set());

  // Editable row overrides for manual import
  const [rowOverrides, setRowOverrides] = useState<
    Record<
      string,
      {
        workId?: number;
        seasonNumber?: number;
        episodeNumbers?: string;
        qualityName?: string;
        languages?: string;
      }
    >
  >({});

  // ==================== QUERIES ====================

  // Catalog works for manual import dropdowns
  const worksQuery = useQuery({
    queryKey: ['catalog-works-summary'],
    queryFn: () => apiFetch<CatalogWorkSummary[]>('/api/v1/catalog/works'),
    enabled: section === 'manual-import' || Boolean(trackedToOverride),
  });

  // Unified download queue (auto-refreshes every 8s)
  const queueQuery = useQuery({
    queryKey: ['download-queue'],
    queryFn: () => apiFetch<ActiveDownloadQueueItem[]>('/api/v1/downloaders/queue'),
    enabled: section === 'queue' && canReadQueue,
    refetchInterval: 8000,
  });

  // Tracked downloads
  const trackedQuery = useQuery({
    queryKey: ['download-tracked'],
    queryFn: () => apiFetch<TrackedDownloadDto[]>('/api/v1/downloaders/tracked'),
    enabled: section === 'queue' && canReadQueue,
    refetchInterval: 8000,
  });

  // Media history query
  const historyQuery = useQuery({
    queryKey: ['activity-history', historyPage, historyEventTypesParam, historyVerticalsParam],
    queryFn: async () => {
      const params = new URLSearchParams({
        page: String(historyPage),
        pageSize: '50',
      });
      if (historyEventTypesParam) params.set('eventType', historyEventTypesParam);
      if (historyVerticalsParam) params.set('vertical', historyVerticalsParam);

      const res = await apiFetch<
        { page: number; pageSize: number; totalCount: number; items: MediaHistoryItem[] } | MediaHistoryItem[]
      >(`/api/v1/activity/history?${params.toString()}`);

      if (Array.isArray(res)) {
        return { page: historyPage, pageSize: 50, totalCount: res.length, items: res };
      }
      return res;
    },
    enabled: section === 'history',
  });

  // Blocklist query
  const blocklistQuery = useQuery({
    queryKey: ['activity-blocklist'],
    queryFn: async () => {
      const res = await apiFetch<BlocklistEntry[] | { items: BlocklistEntry[] }>('/api/v1/activity/blocklist');
      if (Array.isArray(res)) return res;
      return res.items ?? [];
    },
    enabled: section === 'blocklist',
  });

  // ==================== MUTATIONS ====================

  // Process completed downloads sweep
  const processCompletedMutation = useMutation({
    mutationFn: () => apiFetch<unknown>('/api/v1/downloaders/process-completed', { method: 'POST' }),
    onSuccess: () => {
      void queueQuery.refetch();
      void trackedQuery.refetch();
      setAlert({ kind: 'success', text: 'Triggered CDH sweep for completed downloads.' });
    },
    onError: (err: unknown) => {
      setAlert({ kind: 'danger', text: `Failed to process completed downloads: ${String(err)}` });
    },
  });

  // Sweep stalled/malware queue items
  const sweepStalledMutation = useMutation({
    mutationFn: () =>
      apiFetch<unknown>('/api/v1/downloaders/queue/sweep', {
        method: 'POST',
        body: JSON.stringify({ strikeThreshold: 3 }),
      }),
    onSuccess: () => {
      void queueQuery.refetch();
      void trackedQuery.refetch();
      setAlert({ kind: 'success', text: 'Queue sweep evaluated stalled and malware releases.' });
    },
    onError: (err: unknown) => {
      setAlert({ kind: 'danger', text: `Sweep failed: ${String(err)}` });
    },
  });

  // Queue item control (pause / resume / top_priority)
  const controlQueueMutation = useMutation({
    mutationFn: ({
      clientId,
      externalId,
      action,
    }: {
      clientId: number;
      externalId: string;
      action: 'pause' | 'resume' | 'top_priority';
    }) =>
      apiFetch<void>(
        `/api/v1/downloaders/queue/${clientId}/${encodeURIComponent(externalId)}/control`,
        {
          method: 'POST',
          body: JSON.stringify({ action }),
        },
      ),
    onSuccess: (_, vars) => {
      void queueQuery.refetch();
      setAlert({ kind: 'success', text: `Applied '${vars.action}' action to queue item.` });
    },
    onError: (err: unknown) => {
      setAlert({ kind: 'danger', text: `Queue control action failed: ${String(err)}` });
    },
  });

  // Delete from queue mutation
  const deleteQueueItemMutation = useMutation({
    mutationFn: () => {
      if (!itemToRemove) throw new Error('No item to remove');
      const params = new URLSearchParams({
        removeFromClient: String(removeFormClient),
        deleteData: String(removeDeleteData),
        blocklist: String(removeBlocklist),
        searchReplacement: String(removeSearchReplacement),
        title: itemToRemove.title,
        infoHash: itemToRemove.infoHash ?? '',
      });
      return apiFetch<void>(
        `/api/v1/downloaders/queue/${itemToRemove.clientId}/${encodeURIComponent(itemToRemove.externalId)}?${params.toString()}`,
        { method: 'DELETE' },
      );
    },
    onSuccess: () => {
      setItemToRemove(null);
      void queueQuery.refetch();
      void trackedQuery.refetch();
      setAlert({ kind: 'success', text: 'Removed download item from queue.' });
    },
    onError: (err: unknown) => {
      setAlert({ kind: 'danger', text: `Failed to remove queue item: ${String(err)}` });
    },
  });

  // Retry / Override Tracked Download Import
  const retryTrackedImportMutation = useMutation({
    mutationFn: ({
      id,
      overrideRequest,
    }: {
      id: number;
      overrideRequest?: {
        workId?: number | null;
        seasonNumber?: number | null;
        episodeNumbers?: number[] | null;
        bypassUpgradeCheck?: boolean;
      };
    }) =>
      apiFetch<{ verdict?: string; message?: string; destinationPath?: string; destinationFilePath?: string }>(
        `/api/v1/downloaders/tracked/${id}/retry-import`,
        {
          method: 'POST',
          body: JSON.stringify(overrideRequest ?? {}),
        },
      ),
    onSuccess: (outcome) => {
      void trackedQuery.refetch();
      void queueQuery.refetch();
      const verdict = outcome?.verdict ?? '';
      const isSuccess = verdict.startsWith('VerifiedPass') || verdict === 'MusicPolicyPass';
      if (isSuccess) {
        setTrackedToOverride(null);
        setOverrideStatusMessage(null);
        setAlert({ kind: 'success', text: outcome?.message || 'Retried import for tracked download.' });
      } else {
        setOverrideStatusMessage(outcome?.message || 'Import remained blocked.');
      }
    },
    onError: (err: unknown) => {
      setOverrideStatusMessage(`Retry import failed: ${String(err)}`);
    },
  });

  // Mark History item as failed
  const markHistoryFailedMutation = useMutation({
    mutationFn: (historyId: number) =>
      apiFetch<unknown>(`/api/v1/activity/history/${historyId}/mark-failed`, {
        method: 'POST',
        body: JSON.stringify({ searchReplacement: true }),
      }),
    onSuccess: () => {
      void historyQuery.refetch();
      setAlert({ kind: 'success', text: 'Marked history entry as failed and triggered replacement search.' });
    },
    onError: (err: unknown) => {
      setAlert({ kind: 'danger', text: `Failed to mark history entry: ${String(err)}` });
    },
  });

  // Delete single blocklist hash
  const deleteBlocklistHashMutation = useMutation({
    mutationFn: (infoHash: string) =>
      apiFetch<void>(`/api/v1/activity/blocklist/${encodeURIComponent(infoHash)}`, {
        method: 'DELETE',
      }),
    onSuccess: () => {
      void blocklistQuery.refetch();
      setAlert({ kind: 'success', text: 'Removed hash from recurring blocklist.' });
    },
    onError: (err: unknown) => {
      setAlert({ kind: 'danger', text: `Delete failed: ${String(err)}` });
    },
  });

  // Bulk delete blocklist hashes
  const bulkDeleteBlocklistMutation = useMutation({
    mutationFn: (body: { infoHashes?: string[]; clearAll?: boolean }) =>
      apiFetch<unknown>('/api/v1/activity/blocklist/bulk-delete', {
        method: 'POST',
        body: JSON.stringify(body),
      }),
    onSuccess: () => {
      setSelectedBlocklistHashes(new Set());
      void blocklistQuery.refetch();
      setAlert({ kind: 'success', text: 'Updated recurring blocklist.' });
    },
    onError: (err: unknown) => {
      setAlert({ kind: 'danger', text: `Bulk delete failed: ${String(err)}` });
    },
  });

  // Scan folder mutation
  const scanFolderMutation = useMutation({
    mutationFn: () => {
      const params = new URLSearchParams({ folder: manualFolder });
      if (manualVerticalHint) params.set('vertical', manualVerticalHint);
      return apiFetch<ScannedManualImportItem[] | { items: ScannedManualImportItem[] }>(
        `/api/v1/downloaders/manual-import?${params.toString()}`,
      );
    },
    onSuccess: (res) => {
      const items = Array.isArray(res) ? res : res.items ?? [];
      setScannedFiles(items);
      const preselected = new Set<string>();
      const initialOverrides: typeof rowOverrides = {};
      for (const item of items) {
        if (!item.rejections || item.rejections.length === 0) {
          preselected.add(item.filePath);
        }
        initialOverrides[item.filePath] = {
          workId: item.workId ?? undefined,
          seasonNumber: item.seasonNumber ?? undefined,
          episodeNumbers: item.episodeNumbers ? item.episodeNumbers.join(', ') : '',
          qualityName: item.qualityName ?? '',
          languages: item.languages ? item.languages.join(', ') : '',
        };
      }
      setSelectedManualFilePaths(preselected);
      setRowOverrides(initialOverrides);
      setAlert({ kind: 'info', text: `Scanned ${items.length} files from folder.` });
    },
    onError: (err: unknown) => {
      setAlert({ kind: 'danger', text: `Scan failed: ${String(err)}` });
    },
  });

  // Execute manual import mutation
  const executeManualImportMutation = useMutation({
    mutationFn: (body: ManualImportExecuteRequest) =>
      apiFetch<unknown>('/api/v1/downloaders/manual-import', {
        method: 'POST',
        body: JSON.stringify(body),
      }),
    onSuccess: () => {
      setScannedFiles([]);
      setSelectedManualFilePaths(new Set());
      setRowOverrides({});
      void qc.invalidateQueries({ queryKey: ['download-queue'] });
      setAlert({ kind: 'success', text: 'Successfully imported selected files.' });
    },
    onError: (err: unknown) => {
      setAlert({ kind: 'danger', text: `Manual import failed: ${String(err)}` });
    },
  });

  // Tracked downloads dictionary for quick lookup by externalId or hash
  const trackedMap = useMemo(() => {
    const map = new Map<string, TrackedDownloadDto>();
    for (const td of trackedQuery.data ?? []) {
      map.set(td.externalDownloadId.toLowerCase(), td);
      if (td.title) map.set(td.title.toLowerCase(), td);
    }
    return map;
  }, [trackedQuery.data]);

  return (
    <div className="flex flex-col flex-1 min-w-0">
      {/* ==================== PAGE TOOLBAR ==================== */}
      <PageToolbar
        left={
          <>
            {section === 'queue' && (
              <>
                <PageToolbarButton
                  icon={Play}
                  label="Refresh Downloads"
                  disabled={!canManageQueue || processCompletedMutation.isPending}
                  spinning={processCompletedMutation.isPending}
                  onClick={() => processCompletedMutation.mutate()}
                  title="Run Completed Download Handling (CDH) scan"
                />
                <PageToolbarButton
                  icon={ShieldAlert}
                  label="Sweep Stalled"
                  disabled={!canManageQueue || sweepStalledMutation.isPending}
                  spinning={sweepStalledMutation.isPending}
                  onClick={() => sweepStalledMutation.mutate()}
                  title="Sweep stalled and malware items with automatic strike counts"
                />
                <PageToolbarSeparator />
                <PageToolbarButton
                  icon={RefreshCw}
                  label="Refresh"
                  spinning={queueQuery.isFetching || trackedQuery.isFetching}
                  onClick={() => {
                    void queueQuery.refetch();
                    void trackedQuery.refetch();
                  }}
                  title="Refresh active queue"
                />
              </>
            )}

            {section === 'history' && (
              <>
                <PageToolbarButton
                  icon={RefreshCw}
                  label="Refresh"
                  spinning={historyQuery.isFetching}
                  onClick={() => void historyQuery.refetch()}
                  title="Refresh activity history"
                />
              </>
            )}

            {section === 'blocklist' && (
              <>
                <PageToolbarButton
                  icon={Trash2}
                  label={`Remove Selected (${selectedBlocklistHashes.size})`}
                  disabled={selectedBlocklistHashes.size === 0 || bulkDeleteBlocklistMutation.isPending}
                  spinning={bulkDeleteBlocklistMutation.isPending}
                  onClick={() =>
                    bulkDeleteBlocklistMutation.mutate({
                      infoHashes: Array.from(selectedBlocklistHashes),
                    })
                  }
                  title="Remove selected hashes from blocklist"
                />
                <PageToolbarButton
                  icon={Ban}
                  label="Clear All Blocklist"
                  disabled={
                    (blocklistQuery.data ?? []).length === 0 || bulkDeleteBlocklistMutation.isPending
                  }
                  onClick={() => {
                    if (window.confirm('Are you sure you want to clear the entire recurring blocklist?')) {
                      bulkDeleteBlocklistMutation.mutate({ clearAll: true });
                    }
                  }}
                  title="Clear all recurring blocklist entries"
                />
                <PageToolbarSeparator />
                <PageToolbarButton
                  icon={RefreshCw}
                  label="Refresh"
                  spinning={blocklistQuery.isFetching}
                  onClick={() => void blocklistQuery.refetch()}
                  title="Refresh blocklist"
                />
              </>
            )}

            {section === 'manual-import' && (
              <>
                <PageToolbarButton
                  icon={FolderOpen}
                  label="Scan Folder"
                  disabled={!manualFolder.trim() || scanFolderMutation.isPending}
                  spinning={scanFolderMutation.isPending}
                  onClick={() => scanFolderMutation.mutate()}
                  title="Scan target directory for media files"
                />
                <PageToolbarSeparator />
                <PageToolbarButton
                  icon={CheckCircle2}
                  label={`Import Selected (${selectedManualFilePaths.size})`}
                  disabled={
                    selectedManualFilePaths.size === 0 ||
                    executeManualImportMutation.isPending ||
                    !canManageQueue
                  }
                  spinning={executeManualImportMutation.isPending}
                  onClick={() => {
                    const itemsToImport = scannedFiles
                      .filter((f) => selectedManualFilePaths.has(f.filePath))
                      .map((f) => {
                        const o = rowOverrides[f.filePath] || {};
                        const parsedEps = o.episodeNumbers
                          ? o.episodeNumbers
                              .split(',')
                              .map((n) => parseInt(n.trim(), 10))
                              .filter((n) => Number.isFinite(n))
                          : f.episodeNumbers;
                        const parsedLangs = o.languages
                          ? o.languages
                              .split(',')
                              .map((s) => s.trim())
                              .filter(Boolean)
                          : f.languages;

                        return {
                          sourceFilePath: f.filePath,
                          filePath: f.filePath,
                          workId: o.workId ?? f.workId ?? 0,
                          seasonNumber: o.seasonNumber ?? f.seasonNumber,
                          episodeNumbers: parsedEps,
                          qualityName: o.qualityName ?? f.qualityName,
                          languages: parsedLangs,
                        };
                      });

                    const invalidItem = itemsToImport.find((i) => !i.workId);
                    if (invalidItem) {
                      setAlert({
                        kind: 'warning',
                        text: `File '${invalidItem.filePath}' must be linked to a Work before importing.`,
                      });
                      return;
                    }

                    executeManualImportMutation.mutate({
                      importMode: manualImportMode,
                      mode: manualImportMode,
                      items: itemsToImport,
                    });
                  }}
                  title="Execute manual import on selected files"
                />
              </>
            )}
          </>
        }
        right={
          section === 'history' && (
            <div className="flex items-center gap-2 text-[12px] text-[#cccccc]">
              <span>
                Page <strong className="text-[#eeeeee]">{historyPage}</strong>
                {historyQuery.data && (
                  <span className="text-[#888888] ml-1">
                    ({historyQuery.data.totalCount} items)
                  </span>
                )}
              </span>
              <div className="inline-flex items-center gap-0.5 ml-2">
                <button
                  type="button"
                  disabled={historyPage <= 1 || historyQuery.isFetching}
                  onClick={() => setHistoryPage((p) => Math.max(1, p - 1))}
                  aria-label="Previous Page"
                  className="p-1 rounded bg-arr-card hover:bg-[#444444] text-[#cccccc] disabled:opacity-40 disabled:pointer-events-none transition-colors"
                >
                  <ChevronLeft className="h-4 w-4" />
                </button>
                <button
                  type="button"
                  disabled={
                    !historyQuery.data ||
                    historyQuery.data.items.length < 50 ||
                    historyQuery.isFetching
                  }
                  onClick={() => setHistoryPage((p) => p + 1)}
                  aria-label="Next Page"
                  className="p-1 rounded bg-arr-card hover:bg-[#444444] text-[#cccccc] disabled:opacity-40 disabled:pointer-events-none transition-colors"
                >
                  <ChevronRight className="h-4 w-4" />
                </button>
              </div>
            </div>
          )
        }
      />

      {/* ==================== SUB-TOOLBARS / FILTERS ==================== */}
      {section === 'history' && (
        <div className="bg-arr-page border-b border-arr-card-border px-4 lg:px-5 py-2.5 flex flex-col gap-2 text-[13px]">
          <div className="flex flex-wrap items-center gap-1.5">
            <span className="inline-flex items-center gap-1 text-[11px] font-semibold uppercase tracking-wider text-[#909293] mr-1">
              <ListFilter className="h-3.5 w-3.5" />
              Events
            </span>
            <FilterBubble
              label="All Events"
              active={historyEventTypes.size === 0}
              onClick={() => {
                setHistoryEventTypes(new Set());
                setHistoryPage(1);
              }}
            />
            {EVENT_TYPE_OPTIONS.filter((opt) => opt.value !== '').map((opt) => {
              const tone =
                opt.value === 'DownloadFolderImported'
                  ? 'green'
                  : opt.value === 'DownloadFailed'
                    ? 'rose'
                    : opt.value === 'MediaFileDeleted'
                      ? 'amber'
                      : opt.value === 'Grabbed'
                        ? 'blue'
                        : 'cyan';
              return (
                <FilterBubble
                  key={opt.value}
                  label={opt.label}
                  tone={tone}
                  active={historyEventTypes.has(opt.value)}
                  onClick={() => {
                    setHistoryEventTypes((prev) => toggleFilterSetValue(prev, opt.value));
                    setHistoryPage(1);
                  }}
                />
              );
            })}
          </div>

          <div className="flex flex-wrap items-center gap-1.5">
            <span className="text-[11px] font-semibold uppercase tracking-wider text-[#909293] mr-1">
              Media
            </span>
            <FilterBubble
              label="All Media"
              active={historyVerticals.size === 0}
              onClick={() => {
                setHistoryVerticals(new Set());
                setHistoryPage(1);
              }}
            />
            {VERTICAL_OPTIONS.filter((v) => v.value !== '').map((v) => (
              <FilterBubble
                key={v.value}
                label={v.label}
                active={historyVerticals.has(v.value)}
                onClick={() => {
                  setHistoryVerticals((prev) => toggleFilterSetValue(prev, v.value));
                  setHistoryPage(1);
                }}
              />
            ))}

            {(historyEventTypes.size > 0 || historyVerticals.size > 0) && (
              <button
                type="button"
                onClick={() => {
                  setHistoryEventTypes(new Set());
                  setHistoryVerticals(new Set());
                  setHistoryPage(1);
                }}
                className="ml-auto inline-flex items-center gap-1 rounded-full border border-[#5a3538] bg-[#362022] px-2.5 py-1 text-[11px] font-medium text-[#ff9aa2] hover:bg-[#47282b] transition-colors"
              >
                <X className="h-3 w-3" />
                Reset
              </button>
            )}
          </div>
        </div>
      )}

      {section === 'manual-import' && (
        <div className="bg-arr-page border-b border-arr-card-border px-4 lg:px-5 py-3 flex flex-wrap items-center gap-4 text-[13px]">
          <div className="flex items-center gap-2 flex-1 min-w-[280px]">
            <label htmlFor="manual-folder-path" className="text-[#909293] text-[12px] font-medium shrink-0">Folder Path:</label>
            <PathBrowserInput
              id="manual-folder-path"
              value={manualFolder}
              onChange={(p) => setManualFolder(p)}
              placeholder="/downloads/torrents or /downloads/usenet"
              label="Manual Import Folder or File"
              modalTitle="Manual Import — Select Folder or Media File"
              includeFiles
              allowFileSelection
              allowCreateFolder={false}
              disabled={!canManageQueue}
            />
          </div>

          <div className="flex items-center gap-1.5">
            <label htmlFor="manual-vertical-hint" className="text-[#909293] text-[12px] font-medium">Vertical Hint:</label>
            <select
              id="manual-vertical-hint"
              value={manualVerticalHint}
              onChange={(e) => setManualVerticalHint(e.target.value)}
              className="bg-arr-input border border-[#444444] rounded-[3px] px-2 py-1 text-[12px] text-[#eeeeee] focus:border-arr-accent outline-none"
            >
              {VERTICAL_OPTIONS.map((v) => (
                <option key={v.value} value={v.value}>
                  {v.label}
                </option>
              ))}
            </select>
          </div>

          <div className="flex items-center gap-1.5">
            <label htmlFor="manual-import-mode" className="text-[#909293] text-[12px] font-medium">Mode:</label>
            <select
              id="manual-import-mode"
              value={manualImportMode}
              onChange={(e) => setManualImportMode(e.target.value as 'hardlink_or_copy' | 'move')}
              className="bg-arr-input border border-[#444444] rounded-[3px] px-2 py-1 text-[12px] text-[#eeeeee] focus:border-arr-accent outline-none"
            >
              <option value="hardlink_or_copy">Hardlink (or Copy if across drives)</option>
              <option value="move">Move</option>
            </select>
          </div>
        </div>
      )}

      {/* ==================== CONTENT BODY ==================== */}
      <PageContentBody className="space-y-4">
        {/* Banner Alert */}
        {alert && (
          <ArrAlert kind={alert.kind} className="flex items-center justify-between gap-2">
            <span>{alert.text}</span>
            <button
              type="button"
              onClick={() => setAlert(null)}
              className="text-inherit opacity-75 hover:opacity-100 p-0.5 cursor-pointer"
              aria-label="Dismiss alert"
            >
              <X className="h-4 w-4" />
            </button>
          </ArrAlert>
        )}

        {/* -------------------- SECTION: QUEUE -------------------- */}
        {section === 'queue' && (
          <div>
            {queueQuery.isLoading ? (
              <div className="py-20 flex flex-col items-center justify-center gap-3 text-[#909293] text-[14px]">
                <Loader2 className="h-8 w-8 animate-spin text-arr-accent" />
                <span>Loading active download queue...</span>
              </div>
            ) : (queueQuery.data ?? []).length === 0 ? (
              <ArrAlert kind="info">
                No active downloads currently in download client queues.
              </ArrAlert>
            ) : (
              <div className="overflow-x-auto border border-arr-card-border rounded-[4px] bg-arr-heading">
                <table className="arr-table">
                  <thead>
                    <tr>
                      <th>Client</th>
                      <th>Protocol</th>
                      <th>Title</th>
                      <th>Vertical</th>
                      <th>Status / Progress</th>
                      <th>Size / Remaining</th>
                      <th className="text-right">Actions</th>
                    </tr>
                  </thead>
                  <tbody>
                    {(queueQuery.data ?? []).map((item) => {
                      const total = item.totalBytes;
                      const remaining = item.remainingBytes;
                      const downloaded = Math.max(0, total - remaining);
                      const percent = total > 0 ? Math.min(100, Math.round((downloaded / total) * 100)) : 0;

                      // Check for matching tracked download
                      const tracked =
                        trackedMap.get(item.externalId.toLowerCase()) ??
                        trackedMap.get(item.title.toLowerCase());

                      const isImportBlocked = tracked?.state === 'ImportBlocked';
                      const isPaused = item.status.toLowerCase().includes('pause') || item.status.toLowerCase().includes('stop');

                      return (
                        <tr key={`${item.clientId}-${item.externalId}`}>
                          {/* Client */}
                          <td className="text-[12px] font-medium text-[#cccccc]">
                            {item.clientName}
                          </td>

                          {/* Protocol */}
                          <td>
                            <ArrLabel kind={item.protocol === 'usenet' ? 'usenet' : 'torrent'}>
                              {item.protocol}
                            </ArrLabel>
                          </td>

                          {/* Title */}
                          <td className="text-[12.5px] font-medium text-[#eeeeee] min-w-[260px]">
                            <div>
                              <SmartBreakText text={item.title} />
                            </div>
                            {item.outputPath && (
                              <div className="text-[10px] text-[#888888] truncate mt-0.5" title={item.outputPath}>
                                {item.outputPath}
                              </div>
                            )}

                            {/* Tracked warnings or import blocked notes */}
                            {tracked && tracked.statusMessages && tracked.statusMessages.length > 0 && (
                              <div className="mt-1 flex flex-wrap gap-1">
                                {tracked.statusMessages.map((msg, i) => (
                                  <ArrLabel key={i} kind="warning" className="!text-[10px] !py-0">
                                    {msg}
                                  </ArrLabel>
                                ))}
                              </div>
                            )}
                          </td>

                          {/* Vertical */}
                          <td>
                            <ArrLabel kind="inverse">{item.mediaVertical || item.category || 'media'}</ArrLabel>
                          </td>

                          {/* Status / Progress */}
                          <td className="w-48">
                            <div className="space-y-1">
                              <div className="flex items-center justify-between text-[11px]">
                                <span className="font-medium text-[#dddddd]">{item.status}</span>
                                <span className="text-[#aaaaaa]">{percent}%</span>
                              </div>
                              <div className="w-full h-1.5 bg-arr-card rounded-full overflow-hidden">
                                <div
                                  className="h-full bg-arr-accent transition-all duration-300"
                                  style={{ width: `${percent}%` }}
                                />
                              </div>
                              <div className="flex items-center gap-1">
                                {item.isStalled && <ArrLabel kind="danger" className="!text-[9px]">Stalled</ArrLabel>}
                                {item.isMetadataStuck && <ArrLabel kind="warning" className="!text-[9px]">Metadata Stuck</ArrLabel>}
                                {isImportBlocked && <ArrLabel kind="danger" className="!text-[9px]">Import Blocked</ArrLabel>}
                              </div>
                            </div>
                          </td>

                          {/* Size / Remaining */}
                          <td className="text-[12px] font-mono text-[#cccccc] whitespace-nowrap">
                            <div>{formatBytes(total)}</div>
                            <div className="text-[10px] text-[#888888]">
                              {remaining > 0 ? `${formatBytes(remaining)} left` : 'Completed'}
                            </div>
                          </td>

                          {/* Actions */}
                          <td className="text-right whitespace-nowrap">
                            <div className="inline-flex items-center gap-1">
                              {/* Override/retry import button if tracked download import is blocked */}
                              {isImportBlocked && tracked && canManageQueue && (
                                <ArrButton
                                  variant="warning"
                                  className="!h-[26px] !px-2 !text-[11px]"
                                  onClick={() => {
                                    setTrackedToOverride(tracked);
                                    setOverrideWorkId(tracked.workId ? String(tracked.workId) : '');
                                    setOverrideSeason('');
                                    setOverrideEpisodes('');
                                    setOverrideStatusMessage(null);
                                  }}
                                  title="Retry or manually override import"
                                >
                                  Retry Import
                                </ArrButton>
                              )}

                              {canManageQueue && (
                                <>
                                  {isPaused ? (
                                    <button
                                      type="button"
                                      onClick={() =>
                                        controlQueueMutation.mutate({
                                          clientId: item.clientId,
                                          externalId: item.externalId,
                                          action: 'resume',
                                        })
                                      }
                                      className="p-1 rounded bg-arr-card hover:bg-[#444444] text-[#27c24c] transition-colors"
                                      title="Resume download"
                                      aria-label="Resume download"
                                    >
                                      <Play className="h-3.5 w-3.5" />
                                    </button>
                                  ) : (
                                    <button
                                      type="button"
                                      onClick={() =>
                                        controlQueueMutation.mutate({
                                          clientId: item.clientId,
                                          externalId: item.externalId,
                                          action: 'pause',
                                        })
                                      }
                                      className="p-1 rounded bg-arr-card hover:bg-[#444444] text-[#ffa500] transition-colors"
                                      title="Pause download"
                                      aria-label="Pause download"
                                    >
                                      <Pause className="h-3.5 w-3.5" />
                                    </button>
                                  )}

                                  <button
                                    type="button"
                                    onClick={() =>
                                      controlQueueMutation.mutate({
                                        clientId: item.clientId,
                                        externalId: item.externalId,
                                        action: 'top_priority',
                                      })
                                    }
                                    className="p-1 rounded bg-arr-card hover:bg-[#444444] text-arr-accent transition-colors"
                                    title="Move to top priority"
                                    aria-label="Move to top priority"
                                  >
                                    <ArrowUpCircle className="h-3.5 w-3.5" />
                                  </button>

                                  <button
                                    type="button"
                                    onClick={() => {
                                      setItemToRemove(item);
                                      setRemoveFromClient(true);
                                      setRemoveDeleteData(false);
                                      setRemoveBlocklist(false);
                                      setRemoveSearchReplacement(false);
                                    }}
                                    className="p-1 rounded bg-arr-card hover:bg-[#444444] text-[#f05050] transition-colors"
                                    title="Remove from queue..."
                                    aria-label="Remove from queue"
                                  >
                                    <Trash2 className="h-3.5 w-3.5" />
                                  </button>
                                </>
                              )}
                            </div>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        )}

        {/* -------------------- SECTION: HISTORY -------------------- */}
        {section === 'history' && (
          <div>
            {historyQuery.isLoading ? (
              <div className="py-20 flex flex-col items-center justify-center gap-3 text-[#909293] text-[14px]">
                <Loader2 className="h-8 w-8 animate-spin text-arr-accent" />
                <span>Loading media event history...</span>
              </div>
            ) : (historyQuery.data?.items ?? []).length === 0 ? (
              <ArrAlert kind="info">
                No activity history records found matching current filters.
              </ArrAlert>
            ) : (
              <div className="overflow-x-auto border border-arr-card-border rounded-[4px] bg-arr-heading">
                <table className="arr-table">
                  <thead>
                    <tr>
                      <th>Event</th>
                      <th>Time</th>
                      <th>Work / Episode</th>
                      <th>Source Title</th>
                      <th>Quality</th>
                      <th>Indexer / Client</th>
                      <th className="text-right">Action</th>
                    </tr>
                  </thead>
                  <tbody>
                    {(historyQuery.data?.items ?? []).map((row) => {
                      const eventLabelKind =
                        row.eventType === 'Grabbed'
                          ? 'primary'
                          : row.eventType === 'DownloadFolderImported'
                            ? 'success'
                            : row.eventType === 'DownloadFailed'
                              ? 'danger'
                              : 'default';

                      const canMarkFailed =
                        canManageQueue &&
                        (row.eventType === 'Grabbed' || row.eventType === 'DownloadFolderImported');

                      return (
                        <tr key={row.id}>
                          {/* Event */}
                          <td>
                            <ArrLabel kind={eventLabelKind}>{row.eventType}</ArrLabel>
                          </td>

                          {/* Time */}
                          <td className="text-[12px] text-[#aaaaaa] whitespace-nowrap">
                            {row.occurredAt ? new Date(row.occurredAt).toLocaleString() : '-'}
                          </td>

                          {/* Work & Episode */}
                          <td>
                            <div className="flex items-center gap-1.5">
                              {row.workId && onOpenWork ? (
                                <button
                                  type="button"
                                  onClick={() => onOpenWork(row.workId!)}
                                  className="text-[13px] font-medium text-[#eeeeee] hover:text-arr-accent flex items-center gap-1 cursor-pointer transition-colors"
                                >
                                  <span>{row.workTitle ?? `Work #${row.workId}`}</span>
                                  <ExternalLink className="h-3 w-3 opacity-60" />
                                </button>
                              ) : (
                                <span className="text-[13px] font-medium text-[#eeeeee]">
                                  {row.workTitle ?? (row.workId ? `Work #${row.workId}` : '-')}
                                </span>
                              )}
                              {row.episodeCode && (
                                <span className="font-mono text-[11px] font-semibold text-arr-accent">
                                  {row.episodeCode}
                                </span>
                              )}
                            </div>
                          </td>

                          {/* Source Title */}
                          <td className="text-[12px] text-[#cccccc] min-w-[260px]">
                            <SmartBreakText text={row.sourceTitle ?? '-'} />
                          </td>

                          {/* Quality & Languages */}
                          <td>
                            <div className="flex flex-wrap gap-1">
                              {row.qualityName && <ArrLabel kind="inverse">{row.qualityName}</ArrLabel>}
                              {Array.isArray(row.languages) ? (
                                row.languages.map((l) => (
                                  <ArrLabel key={l} kind="default">
                                    {l}
                                  </ArrLabel>
                                ))
                              ) : row.languages ? (
                                <ArrLabel kind="default">{String(row.languages)}</ArrLabel>
                              ) : null}
                            </div>
                          </td>

                          {/* Indexer / Client */}
                          <td className="text-[12px] text-[#909293]">
                            <div>{row.indexerName || '-'}</div>
                            <div className="text-[11px] text-[#666666]">{row.downloadClientName}</div>
                          </td>

                          {/* Action: Mark as Failed */}
                          <td className="text-right">
                            {canMarkFailed && (
                              <ArrButton
                                variant="danger"
                                className="!h-[26px] !px-2.5 !text-[11px]"
                                busy={markHistoryFailedMutation.isPending}
                                onClick={() => {
                                  if (
                                    window.confirm(
                                      `Mark '${row.sourceTitle ?? row.workTitle}' as failed and trigger replacement search?`,
                                    )
                                  ) {
                                    markHistoryFailedMutation.mutate(row.id);
                                  }
                                }}
                                title="Mark release as failed and automatically search replacement release"
                              >
                                Mark as Failed
                              </ArrButton>
                            )}
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        )}

        {/* -------------------- SECTION: BLOCKLIST -------------------- */}
        {section === 'blocklist' && (
          <div className="space-y-3">
            <div className="text-[12px] text-[#909293] bg-arr-page border border-arr-card-border px-3.5 py-2.5 rounded-[4px] leading-relaxed">
              Recurring blocklist prevents known-bad releases from being re-grabbed. Both <strong className="text-[#eeeeee]">Failed Usenet NZBs</strong> (tracked across NZB GUID, download URL, indexer+size+date fingerprint, and normalized title so broken NZBs are never retried) and <strong className="text-[#eeeeee]">Blocklisted Torrents</strong> (<span className="font-mono text-[#bbbbbb]">infoHash</span>) are stored here.
            </div>

            {blocklistQuery.isLoading ? (
              <div className="py-20 flex flex-col items-center justify-center gap-3 text-[#909293] text-[14px]">
                <Loader2 className="h-8 w-8 animate-spin text-arr-accent" />
                <span>Loading recurring blocklist...</span>
              </div>
            ) : (blocklistQuery.data ?? []).length === 0 ? (
              <ArrAlert kind="info">
                No releases are currently in the recurring blocklist.
              </ArrAlert>
            ) : (
              <div className="overflow-x-auto border border-arr-card-border rounded-[4px] bg-arr-heading">
                <table className="arr-table">
                  <thead>
                    <tr>
                      <th className="w-10 text-center">
                        <input
                          type="checkbox"
                          checked={
                            (blocklistQuery.data ?? []).length > 0 &&
                            (blocklistQuery.data ?? []).every((e) =>
                              selectedBlocklistHashes.has(e.infoHash),
                            )
                          }
                          onChange={(e) => {
                            if (e.target.checked) {
                              setSelectedBlocklistHashes(
                                new Set((blocklistQuery.data ?? []).map((x) => x.infoHash)),
                              );
                            } else {
                              setSelectedBlocklistHashes(new Set());
                            }
                          }}
                          className="accent-arr-accent rounded"
                          aria-label="Select all blocklist items"
                        />
                      </th>
                      <th className="w-20">Protocol</th>
                      <th>Release Title</th>
                      <th>Indexer</th>
                      <th>Tracking Fingerprint</th>
                      <th>Failure Reason</th>
                      <th>Blocked At</th>
                      <th className="text-right w-16">Action</th>
                    </tr>
                  </thead>
                  <tbody>
                    {(blocklistQuery.data ?? []).map((item) => {
                      const isSelected = selectedBlocklistHashes.has(item.infoHash);
                      const isUsenet =
                        item.protocol?.toLowerCase() === 'usenet' ||
                        item.infoHash.startsWith('nzb:') ||
                        item.infoHash.startsWith('nzo:') ||
                        item.infoHash.startsWith('guid:');

                      return (
                        <tr key={item.infoHash} className={isSelected ? '!bg-[#2d3748]' : undefined}>
                          <td className="text-center">
                            <input
                              type="checkbox"
                              checked={isSelected}
                              onChange={() => {
                                setSelectedBlocklistHashes((prev) => {
                                  const next = new Set(prev);
                                  if (next.has(item.infoHash)) {
                                    next.delete(item.infoHash);
                                  } else {
                                    next.add(item.infoHash);
                                  }
                                  return next;
                                });
                              }}
                              aria-label={`Select ${item.title}`}
                              className="accent-arr-accent rounded"
                            />
                          </td>

                          {/* Protocol */}
                          <td className="whitespace-nowrap">
                            <ArrLabel kind={isUsenet ? 'usenet' : 'torrent'}>
                              {isUsenet ? 'NZB' : 'TORRENT'}
                            </ArrLabel>
                          </td>

                          {/* Release Title + Size */}
                          <td className="font-medium text-[#eeeeee] text-[12.5px] min-w-[260px]">
                            <div className="flex flex-wrap items-baseline gap-1.5">
                              <SmartBreakText text={item.title} />
                              {Boolean(item.sizeBytes && item.sizeBytes > 0) && (
                                <span className="font-mono text-[11px] text-[#909293] whitespace-nowrap">
                                  ({formatBytes(item.sizeBytes!)})
                                </span>
                              )}
                            </div>
                          </td>

                          {/* Indexer */}
                          <td className="text-[12px] text-[#b8babc] whitespace-nowrap">
                            {item.indexerName || (item.indexerId ? `#${item.indexerId}` : '-')}
                          </td>

                          {/* Tracking Fingerprint */}
                          <td className="text-[11px] whitespace-nowrap">
                            <div
                              className="font-mono text-[#aaaaaa] truncate max-w-[240px]"
                              title={item.infoHash}
                            >
                              {item.infoHash}
                            </div>
                            {item.releaseGuid && (
                              <div
                                className="text-[10px] text-[#777777] font-mono truncate max-w-[240px]"
                                title={item.releaseGuid}
                              >
                                {item.releaseGuid}
                              </div>
                            )}
                          </td>

                          {/* Failure Reason */}
                          <td>
                            <ArrLabel kind="danger">{item.reason}</ArrLabel>
                          </td>

                          {/* Blocked At */}
                          <td className="text-[12px] text-[#888888] whitespace-nowrap">
                            {item.blockedAt ? new Date(item.blockedAt).toLocaleString() : '-'}
                          </td>

                          {/* Action */}
                          <td className="text-right">
                            <ArrButton
                              variant="default"
                              className="!h-[26px] !px-2 !text-[11px] text-[#f05050] hover:text-[#ffffff] hover:!bg-[#ee3d3d]"
                              disabled={deleteBlocklistHashMutation.isPending}
                              onClick={() => deleteBlocklistHashMutation.mutate(item.infoHash)}
                              title="Delete this blocklist entry"
                            >
                              <Trash2 className="h-3.5 w-3.5" />
                            </ArrButton>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        )}

        {/* -------------------- SECTION: MANUAL IMPORT -------------------- */}
        {section === 'manual-import' && (
          <div>
            {scannedFiles.length === 0 ? (
              <div className="py-16 text-center text-[#909293] border border-dashed border-[#444444] rounded-[4px]">
                <FolderOpen className="h-10 w-10 mx-auto text-[#666666] mb-3" />
                <h4 className="text-[16px] font-normal text-[#dddddd] mb-1">
                  Ready to scan folder
                </h4>
                <p className="text-[13px] text-[#888888] max-w-md mx-auto mb-4">
                  Enter a target downloads directory above and click <strong>Scan Folder</strong> to identify media files and assign them to catalog series and movies.
                </p>
                <ArrButton
                  variant="primary"
                  busy={scanFolderMutation.isPending}
                  onClick={() => scanFolderMutation.mutate()}
                >
                  <Search className="h-4 w-4" /> Scan &lsquo;{manualFolder}&rsquo;
                </ArrButton>
              </div>
            ) : (
              <div className="space-y-3">
                <div className="flex items-center justify-between text-[13px] text-[#cccccc]">
                  <span>
                    Scanned <strong>{scannedFiles.length}</strong> files. <strong>{selectedManualFilePaths.size}</strong> selected for import.
                  </span>
                  <div className="flex items-center gap-2">
                    <button
                      type="button"
                      onClick={() =>
                        setSelectedManualFilePaths(new Set(scannedFiles.map((f) => f.filePath)))
                      }
                      className="text-arr-accent hover:underline text-[12px] cursor-pointer"
                    >
                      Select All
                    </button>
                    <span className="text-[#555555]">|</span>
                    <button
                      type="button"
                      onClick={() => setSelectedManualFilePaths(new Set())}
                      className="text-arr-accent hover:underline text-[12px] cursor-pointer"
                    >
                      Deselect All
                    </button>
                  </div>
                </div>

                <div className="overflow-x-auto border border-arr-card-border rounded-[4px] bg-arr-heading">
                  <table className="arr-table">
                    <thead>
                      <tr>
                        <th className="w-10 text-center">
                          <input
                            type="checkbox"
                            checked={
                              scannedFiles.length > 0 &&
                              scannedFiles.every((f) => selectedManualFilePaths.has(f.filePath))
                            }
                            onChange={(e) => {
                              if (e.target.checked) {
                                setSelectedManualFilePaths(
                                  new Set(scannedFiles.map((f) => f.filePath)),
                                );
                              } else {
                                setSelectedManualFilePaths(new Set());
                              }
                            }}
                            className="accent-arr-accent rounded"
                            aria-label="Select all scanned files"
                          />
                        </th>
                        <th>File / Relative Path</th>
                        <th>Work Target</th>
                        <th>Season</th>
                        <th>Episodes</th>
                        <th>Quality</th>
                        <th>Languages</th>
                        <th>Warnings / Status</th>
                      </tr>
                    </thead>
                    <tbody>
                      {scannedFiles.map((file) => {
                        const isSelected = selectedManualFilePaths.has(file.filePath);
                        const ov = rowOverrides[file.filePath] || {};

                        return (
                          <tr key={file.filePath} className={isSelected ? '!bg-[#2d3748]' : undefined}>
                            {/* Checkbox */}
                            <td className="text-center">
                              <input
                                type="checkbox"
                                checked={isSelected}
                                onChange={() => {
                                  setSelectedManualFilePaths((prev) => {
                                    const next = new Set(prev);
                                    if (next.has(file.filePath)) {
                                      next.delete(file.filePath);
                                    } else {
                                      next.add(file.filePath);
                                    }
                                    return next;
                                  });
                                }}
                                aria-label={`Select ${file.fileName}`}
                                className="accent-arr-accent rounded"
                              />
                            </td>

                            {/* File / Relative Path */}
                            <td className="min-w-[260px]">
                              <div className="text-[12.5px] font-medium text-[#eeeeee]">
                                <SmartBreakText text={file.fileName} />
                              </div>
                              <div className="text-[10.5px] text-[#888888] truncate max-w-[360px] mt-0.5" title={file.relativePath || file.filePath}>
                                {file.relativePath || file.filePath}
                              </div>
                              <div className="text-[10.5px] text-[#aaaaaa] mt-0.5">
                                {formatBytes(file.sizeBytes)}
                              </div>
                            </td>

                            {/* Work Target Dropdown */}
                            <td>
                              <select
                                value={ov.workId ?? file.workId ?? ''}
                                onChange={(e) => {
                                  const val = e.target.value ? Number(e.target.value) : undefined;
                                  setRowOverrides((prev) => ({
                                    ...prev,
                                    [file.filePath]: {
                                      ...prev[file.filePath],
                                      workId: val,
                                    },
                                  }));
                                }}
                                className="bg-arr-input border border-[#444444] rounded-[3px] px-2 py-1 text-[12px] text-[#eeeeee] w-48 focus:border-arr-accent outline-none"
                              >
                                <option value="">Select Work...</option>
                                {(worksQuery.data ?? []).map((w) => (
                                  <option key={w.id} value={w.id}>
                                    {w.title} {w.year ? `(${w.year})` : ''} [{w.workKind}]
                                  </option>
                                ))}
                              </select>
                            </td>

                            {/* Season */}
                            <td className="w-20">
                              <input
                                type="number"
                                value={ov.seasonNumber !== undefined ? ov.seasonNumber : file.seasonNumber ?? ''}
                                onChange={(e) => {
                                  const val = e.target.value ? Number(e.target.value) : undefined;
                                  setRowOverrides((prev) => ({
                                    ...prev,
                                    [file.filePath]: {
                                      ...prev[file.filePath],
                                      seasonNumber: val,
                                    },
                                  }));
                                }}
                                placeholder="1"
                                className="bg-arr-input border border-[#444444] rounded-[3px] px-2 py-1 text-[12px] text-[#eeeeee] w-16 focus:border-arr-accent outline-none font-mono"
                              />
                            </td>

                            {/* Episode numbers */}
                            <td className="w-24">
                              <input
                                type="text"
                                value={
                                  ov.episodeNumbers !== undefined
                                    ? ov.episodeNumbers
                                    : (file.episodeNumbers ?? []).join(', ')
                                }
                                onChange={(e) => {
                                  const val = e.target.value;
                                  setRowOverrides((prev) => ({
                                    ...prev,
                                    [file.filePath]: {
                                      ...prev[file.filePath],
                                      episodeNumbers: val,
                                    },
                                  }));
                                }}
                                placeholder="1 or 1,2"
                                className="bg-arr-input border border-[#444444] rounded-[3px] px-2 py-1 text-[12px] text-[#eeeeee] w-20 focus:border-arr-accent outline-none font-mono"
                              />
                            </td>

                            {/* Quality */}
                            <td className="w-32">
                              <input
                                type="text"
                                value={ov.qualityName !== undefined ? ov.qualityName : file.qualityName ?? ''}
                                onChange={(e) => {
                                  const val = e.target.value;
                                  setRowOverrides((prev) => ({
                                    ...prev,
                                    [file.filePath]: {
                                      ...prev[file.filePath],
                                      qualityName: val,
                                    },
                                  }));
                                }}
                                placeholder="WEBDL-1080p"
                                className="bg-arr-input border border-[#444444] rounded-[3px] px-2 py-1 text-[12px] text-[#eeeeee] w-28 focus:border-arr-accent outline-none font-mono"
                              />
                            </td>

                            {/* Languages */}
                            <td className="w-28">
                              <input
                                type="text"
                                value={
                                  ov.languages !== undefined
                                    ? ov.languages
                                    : (file.languages ?? []).join(', ')
                                }
                                onChange={(e) => {
                                  const val = e.target.value;
                                  setRowOverrides((prev) => ({
                                    ...prev,
                                    [file.filePath]: {
                                      ...prev[file.filePath],
                                      languages: val,
                                    },
                                  }));
                                }}
                                placeholder="en, de"
                                className="bg-arr-input border border-[#444444] rounded-[3px] px-2 py-1 text-[12px] text-[#eeeeee] w-24 focus:border-arr-accent outline-none font-mono"
                              />
                            </td>

                            {/* Rejections / Status */}
                            <td>
                              {file.rejections && file.rejections.length > 0 ? (
                                <div className="flex flex-wrap gap-1">
                                  {file.rejections.map((rej, idx) => (
                                    <ArrLabel key={idx} kind="danger" className="!text-[10px]">
                                      {rej}
                                    </ArrLabel>
                                  ))}
                                </div>
                              ) : (
                                <ArrLabel kind="success" className="!text-[10px]">
                                  Ready
                                </ArrLabel>
                              )}
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              </div>
            )}
          </div>
        )}

        {/* ==================== REMOVE QUEUE ITEM MODAL ==================== */}
        {itemToRemove && (
          <ArrModal
            title="Remove from Queue"
            onClose={() => setItemToRemove(null)}
            maxWidth="max-w-md"
            footer={
              <>
                <ArrButton onClick={() => setItemToRemove(null)}>Cancel</ArrButton>
                <ArrButton
                  variant="danger"
                  busy={deleteQueueItemMutation.isPending}
                  onClick={() => deleteQueueItemMutation.mutate()}
                >
                  Confirm Remove
                </ArrButton>
              </>
            }
          >
            <div className="space-y-4 text-[13px] text-[#cccccc]">
              <p>
                Are you sure you want to remove &lsquo;<strong>{itemToRemove.title}</strong>&rsquo; from the queue?
              </p>

              <div className="space-y-2.5 pt-2 border-t border-arr-card-border">
                <label className="flex items-center gap-2 cursor-pointer select-none">
                  <input
                    type="checkbox"
                    checked={removeFormClient}
                    onChange={(e) => setRemoveFromClient(e.target.checked)}
                    className="accent-arr-accent rounded"
                  />
                  <span>Remove from Download Client ({itemToRemove.clientName})</span>
                </label>

                <label className="flex items-center gap-2 cursor-pointer select-none">
                  <input
                    type="checkbox"
                    checked={removeDeleteData}
                    onChange={(e) => setRemoveDeleteData(e.target.checked)}
                    className="accent-[#f05050] rounded"
                  />
                  <span className="text-[#f05050]">Delete Downloaded Files on Disk</span>
                </label>

                <label className="flex items-center gap-2 cursor-pointer select-none">
                  <input
                    type="checkbox"
                    checked={removeBlocklist}
                    onChange={(e) => setRemoveBlocklist(e.target.checked)}
                    className="accent-[#ffa500] rounded"
                  />
                  <span>Add Release Hash to Recurring Blocklist</span>
                </label>

                <label className="flex items-center gap-2 cursor-pointer select-none">
                  <input
                    type="checkbox"
                    checked={removeSearchReplacement}
                    onChange={(e) => setRemoveSearchReplacement(e.target.checked)}
                    className="accent-arr-accent rounded"
                  />
                  <span>Automatically Search Replacement Release</span>
                </label>
              </div>
            </div>
          </ArrModal>
        )}

        {/* ==================== OVERRIDE TRACKED IMPORT MODAL ==================== */}
        {trackedToOverride && (
          <ArrModal
            title={`Retry / Override Import — ${trackedToOverride.title}`}
            onClose={() => {
              setTrackedToOverride(null);
              setOverrideStatusMessage(null);
            }}
            maxWidth="max-w-lg"
            footer={
              <>
                <ArrButton
                  onClick={() => {
                    setTrackedToOverride(null);
                    setOverrideStatusMessage(null);
                  }}
                >
                  Cancel
                </ArrButton>
                <ArrButton
                  variant="primary"
                  busy={retryTrackedImportMutation.isPending}
                  onClick={() => {
                    setOverrideStatusMessage(null);
                    const parsedEps = overrideEpisodes
                      ? overrideEpisodes
                          .split(',')
                          .map((n) => parseInt(n.trim(), 10))
                          .filter((n) => Number.isFinite(n))
                      : null;

                    retryTrackedImportMutation.mutate({
                      id: trackedToOverride.id,
                      overrideRequest: {
                        workId: overrideWorkId ? Number(overrideWorkId) : null,
                        seasonNumber: overrideSeason ? Number(overrideSeason) : null,
                        episodeNumbers: parsedEps,
                        bypassUpgradeCheck: overrideBypass,
                      },
                    });
                  }}
                >
                  Retry Import
                </ArrButton>
              </>
            }
          >
            <div className="space-y-4 text-[13px] text-[#cccccc]">
              {overrideStatusMessage && (
                <div role="status">
                  <ArrAlert kind="warning">{overrideStatusMessage}</ArrAlert>
                </div>
              )}

              {trackedToOverride.statusMessages.length > 0 && (
                <div className="space-y-1">
                  <span className="text-[12px] font-bold text-[#f05050]">Import Blocked Messages:</span>
                  <ul className="list-disc list-inside text-[12px] text-[#f2dede] bg-[#a94442]/20 p-2.5 rounded-[4px] border border-[#a94442]">
                    {trackedToOverride.statusMessages.map((msg, i) => (
                      <li key={i}>{msg}</li>
                    ))}
                  </ul>
                </div>
              )}

              <div className="space-y-3">
                <div>
                  <label htmlFor="override-work" className="block text-[12px] font-bold text-[#dddddd] mb-1">
                    Assign to Work:
                  </label>
                  <select
                    id="override-work"
                    value={overrideWorkId}
                    onChange={(e) => setOverrideWorkId(e.target.value)}
                    className="w-full bg-arr-input border border-[#444444] rounded-[3px] px-2.5 py-1.5 text-[13px] text-[#eeeeee] focus:border-arr-accent outline-none"
                  >
                    <option value="">Select Work...</option>
                    {(worksQuery.data ?? []).map((w) => (
                      <option key={w.id} value={w.id}>
                        {w.title} {w.year ? `(${w.year})` : ''} [{w.workKind}]
                      </option>
                    ))}
                  </select>
                </div>

                <div className="grid grid-cols-2 gap-3">
                  <div>
                    <label htmlFor="override-season" className="block text-[12px] font-bold text-[#dddddd] mb-1">
                      Season Number:
                    </label>
                    <input
                      id="override-season"
                      type="number"
                      value={overrideSeason}
                      onChange={(e) => setOverrideSeason(e.target.value)}
                      placeholder="e.g. 1"
                      className="w-full bg-arr-input border border-[#444444] rounded-[3px] px-2.5 py-1.5 text-[13px] text-[#eeeeee] focus:border-arr-accent outline-none font-mono"
                    />
                  </div>
                  <div>
                    <label htmlFor="override-episodes" className="block text-[12px] font-bold text-[#dddddd] mb-1">
                      Episode Numbers:
                    </label>
                    <input
                      id="override-episodes"
                      type="text"
                      value={overrideEpisodes}
                      onChange={(e) => setOverrideEpisodes(e.target.value)}
                      placeholder="e.g. 1 or 1,2"
                      className="w-full bg-arr-input border border-[#444444] rounded-[3px] px-2.5 py-1.5 text-[13px] text-[#eeeeee] focus:border-arr-accent outline-none font-mono"
                    />
                  </div>
                </div>

                <div className="flex items-center gap-2 pt-1">
                  <input
                    id="override-bypass"
                    type="checkbox"
                    checked={overrideBypass}
                    onChange={(e) => setOverrideBypass(e.target.checked)}
                    className="accent-arr-accent rounded cursor-pointer"
                  />
                  <label htmlFor="override-bypass" className="cursor-pointer select-none">
                    Bypass Quality / Language Cutoff Upgrade Rejections
                  </label>
                </div>
              </div>
            </div>
          </ArrModal>
        )}
      </PageContentBody>
    </div>
  );
}
