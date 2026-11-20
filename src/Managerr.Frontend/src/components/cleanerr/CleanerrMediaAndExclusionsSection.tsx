import { useId, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Clock,
  Database,
  Eye,
  Film,
  Plus,
  RefreshCw,
  Search,
  Shield,
  ShieldAlert,
  ShieldCheck,
  Tag,
  Trash2,
  Tv,
  User,
} from 'lucide-react';
import { apiFetch, formatBytes } from '../../lib/api';
import {
  ArrAlert,
  ArrButton,
  ArrLabel,
  FieldSet,
  FilterBubble,
  FilterBubbleGroup,
  FormGroup,
  PageToolbar,
  PageToolbarButton,
  PageToolbarSeparator,
} from '../arr/ArrPrimitives';
import { CleanerrConfirmModal } from './CleanerrConfirmModal';
import { CleanerrManualStageModal } from './CleanerrManualStageModal';
import type {
  CleanerrExclusionRule,
  CleanerrMediaItemDto,
  CleanerrMediaListResponse,
  CleanerrStatusResponse,
  CleanerrWorkflowSummary,
  LibraryOption,
} from './cleanerrTypes';

interface PlexSyncResult {
  serverUrl?: string;
  sectionsScanned: number;
  moviesSynced: number;
  showsSynced: number;
  activeStreamsDetected?: number;
}

interface Props {
  status?: CleanerrStatusResponse;
  initialSubTab?: 'browser' | 'exclusions';
}

type MediaTypeFilter = 'all' | 'movie' | 'show';
type WatchedFilter = 'all' | 'watched' | 'unwatched';
type SortOption = 'size' | 'last_viewed' | 'added' | 'title';

const MEDIA_TYPE_OPTIONS: Array<{ id: MediaTypeFilter; label: string }> = [
  { id: 'all', label: 'All' },
  { id: 'movie', label: 'Movies' },
  { id: 'show', label: 'Series' },
];

const TTL_OPTIONS = [
  { label: 'Permanent', value: 'permanent' },
  { label: '7 Days', value: '7' },
  { label: '30 Days', value: '30' },
  { label: '90 Days', value: '90' },
  { label: '365 Days', value: '365' },
];

const QUICK_TAG_CHIPS = ['keep', 'protected', '4k-remux', 'favorites'];

function formatShortDate(iso: string | null | undefined): string {
  if (!iso) return 'Never';
  const parsed = new Date(iso);
  if (Number.isNaN(parsed.getTime())) return iso.slice(0, 10);
  return parsed.toLocaleDateString();
}

function calculateExpiresAt(ttlChoice: string): string | null {
  if (ttlChoice === 'permanent') return null;
  const days = Number(ttlChoice);
  if (!Number.isFinite(days) || days <= 0) return null;
  return new Date(Date.now() + days * 86400000).toISOString();
}

export function CleanerrMediaAndExclusionsSection({
  status,
  initialSubTab = 'browser',
}: Props) {
  const qc = useQueryClient();
  const [subTab, setSubTab] = useState<'browser' | 'exclusions'>(initialSubTab);
  const [banner, setBanner] = useState<{
    kind: 'info' | 'success' | 'danger';
    message: string;
  } | null>(null);

  // Library Browser state
  const [searchQuery, setSearchQuery] = useState('');
  const [mediaType, setMediaType] = useState<MediaTypeFilter>('all');
  const [libraryId, setLibraryId] = useState<string>('');
  const [watchedFilter, setWatchedFilter] = useState<WatchedFilter>('all');
  const [sortBy, setSortBy] = useState<SortOption>('size');
  const [page, setPage] = useState(1);
  const pageSize = 50;
  const [selectedIds, setSelectedIds] = useState<number[]>([]);
  const [manualStageOpen, setManualStageOpen] = useState(false);

  // Exclusions Manager state
  const [ruleType, setRuleType] = useState<
    'tag' | 'plex_label' | 'plex_user' | 'seerr_user' | 'library'
  >('tag');
  const [ruleScope, setRuleScope] = useState<string>('global');
  const [ruleValue, setRuleValue] = useState('');
  const [ruleTtl, setRuleTtl] = useState('permanent');
  const [ruleReason, setRuleReason] = useState('Protected by Cleanerr rule');
  const [ruleSearch, setRuleSearch] = useState('');
  const [revokeTarget, setRevokeTarget] =
    useState<CleanerrExclusionRule | null>(null);

  // Accessible IDs
  const mediaSearchId = useId();
  const librarySelectId = useId();
  const watchedSelectId = useId();
  const sortSelectId = useId();
  const mediaTypeGroupId = useId();
  const mediaTableCaptionId = useId();
  const rulesTableCaptionId = useId();
  const ruleTypeId = useId();
  const ruleScopeId = useId();
  const ruleValueId = useId();
  const ruleTtlId = useId();
  const ruleReasonId = useId();
  const ruleSearchId = useId();

  // Queries
  const mediaQuery = useQuery({
    queryKey: [
      'cleanerr-media',
      mediaType,
      libraryId,
      watchedFilter,
      searchQuery,
      sortBy,
      page,
      pageSize,
    ],
    queryFn: () => {
      const params = new URLSearchParams();
      if (mediaType !== 'all') params.set('mediaType', mediaType);
      if (libraryId) params.set('libraryId', libraryId);
      if (watchedFilter !== 'all') params.set('watched', watchedFilter);
      if (searchQuery.trim()) params.set('search', searchQuery.trim());
      params.set('sort', sortBy);
      params.set('page', String(page));
      params.set('pageSize', String(pageSize));
      const qs = params.toString();
      return apiFetch<CleanerrMediaListResponse | CleanerrMediaItemDto[]>(
        `/api/v1/cleanerr/media${qs ? `?${qs}` : ''}`,
      );
    },
  });

  const librariesQuery = useQuery({
    queryKey: ['libraries'],
    queryFn: () => apiFetch<LibraryOption[]>('/api/v1/libraries'),
  });

  const exclusionsQuery = useQuery({
    queryKey: ['cleanerr-exclusions'],
    queryFn: () =>
      apiFetch<CleanerrExclusionRule[]>('/api/v1/cleanerr/exclusions'),
  });

  const workflowsQuery = useQuery({
    queryKey: ['cleanerr-workflows'],
    queryFn: () =>
      apiFetch<CleanerrWorkflowSummary[]>('/api/v1/cleanerr/workflows'),
  });

  // Normalize & safeguard-filter media response
  const { visibleMedia, totalMediaCount } = useMemo(() => {
    const raw = mediaQuery.data;
    if (!raw) {
      return { visibleMedia: [] as CleanerrMediaItemDto[], totalMediaCount: 0 };
    }

    let items: CleanerrMediaItemDto[];
    let total: number;

    if (Array.isArray(raw)) {
      items = [...raw];
      if (mediaType !== 'all') {
        items = items.filter((m) => m.mediaType === mediaType);
      }
      if (libraryId) {
        const libNum = Number(libraryId);
        items = items.filter((m) => m.libraryId === libNum);
      }
      if (watchedFilter === 'watched') {
        items = items.filter((m) => m.viewCount > 0);
      } else if (watchedFilter === 'unwatched') {
        items = items.filter((m) => m.viewCount === 0);
      }
      if (searchQuery.trim()) {
        const q = searchQuery.trim().toLowerCase();
        items = items.filter(
          (m) =>
            m.title.toLowerCase().includes(q) ||
            m.parentTitle?.toLowerCase().includes(q) ||
            m.grandparentTitle?.toLowerCase().includes(q) ||
            m.seerrRequestedBy?.toLowerCase().includes(q) ||
            (m.tags ?? []).some((t) => t.toLowerCase().includes(q)),
        );
      }
      items.sort((a, b) => {
        if (sortBy === 'title') return a.title.localeCompare(b.title);
        if (sortBy === 'last_viewed') {
          return (a.lastViewedAt ?? '').localeCompare(b.lastViewedAt ?? '');
        }
        if (sortBy === 'added') {
          return (a.addedAt ?? '').localeCompare(b.addedAt ?? '');
        }
        return b.sizeBytes - a.sizeBytes;
      });
      total = items.length;
      const offset = (page - 1) * pageSize;
      items = items.slice(offset, offset + pageSize);
    } else {
      items = raw.items ?? [];
      total = raw.total ?? items.length;
    }

    return { visibleMedia: items, totalMediaCount: total };
  }, [
    mediaQuery.data,
    mediaType,
    libraryId,
    watchedFilter,
    searchQuery,
    sortBy,
    page,
    pageSize,
  ]);

  const workflowNameById = useMemo(() => {
    const map = new Map<number, string>();
    for (const wf of workflowsQuery.data ?? []) {
      map.set(wf.id, wf.name);
    }
    return map;
  }, [workflowsQuery.data]);

  const filteredExclusions = useMemo(() => {
    const list = exclusionsQuery.data ?? [];
    if (!ruleSearch.trim()) return list;
    const q = ruleSearch.trim().toLowerCase();
    return list.filter(
      (r) =>
        r.value.toLowerCase().includes(q) ||
        r.ruleType.toLowerCase().includes(q) ||
        r.reason?.toLowerCase().includes(q) ||
        r.createdBy.toLowerCase().includes(q),
    );
  }, [exclusionsQuery.data, ruleSearch]);

  // Mutations
  const syncCatalogMutation = useMutation({
    mutationFn: () =>
      apiFetch<{ synced: boolean; count: number }>(
        '/api/v1/cleanerr/media/sync-catalog',
        { method: 'POST', body: JSON.stringify({}) },
      ),
    onSuccess: (res) => {
      setBanner({
        kind: 'success',
        message: `Catalog synced into Cleanerr (${res.count} items indexed).`,
      });
      void qc.invalidateQueries({ queryKey: ['cleanerr-media'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-stats'] });
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Catalog sync failed.',
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
        message: `Synced Plex (${res.sectionsScanned} sections: ${res.moviesSynced} movies, ${res.showsSynced} series).`,
      });
      void qc.invalidateQueries({ queryKey: ['cleanerr-media'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-stats'] });
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Plex sync failed.',
      });
    },
  });

  const bulkExcludeMutation = useMutation({
    mutationFn: (ids: number[]) =>
      apiFetch<{ excludedCount: number; excludedTitles?: string[] }>(
        '/api/v1/cleanerr/exclusions/bulk',
        {
          method: 'POST',
          body: JSON.stringify({
            mediaItemIds: ids,
            reason: 'Bulk excluded via Library Browser',
          }),
        },
      ),
    onSuccess: (res) => {
      setBanner({
        kind: 'success',
        message: `Excluded ${res.excludedCount} item(s) with family protection.`,
      });
      setSelectedIds([]);
      void qc.invalidateQueries({ queryKey: ['cleanerr-media'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-exclusions'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-queue'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-stats'] });
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Bulk exclusion failed.',
      });
    },
  });

  const excludeSingleMutation = useMutation({
    mutationFn: (item: CleanerrMediaItemDto) => {
      const granularity =
        item.mediaType === 'show'
          ? 'series'
          : item.mediaType === 'movie'
            ? 'movie'
            : 'global';
      return apiFetch('/api/v1/cleanerr/exclusions', {
        method: 'POST',
        body: JSON.stringify({
          mediaItemId: item.id,
          ruleType: 'media_item',
          value: item.parentTitle ? `${item.parentTitle} - ${item.title}` : item.title,
          granularity,
          reason: `Excluded ${item.mediaType} via Library Browser`,
        }),
      });
    },
    onSuccess: (_res, item) => {
      setBanner({
        kind: 'success',
        message: `Protected "${item.title}" in Cleanerr Exclusions.`,
      });
      void qc.invalidateQueries({ queryKey: ['cleanerr-media'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-exclusions'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-queue'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-stats'] });
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Failed to exclude item.',
      });
    },
  });

  const createRuleMutation = useMutation({
    mutationFn: () => {
      const workflowId =
        ruleScope !== 'global' && Number.isFinite(Number(ruleScope))
          ? Number(ruleScope)
          : null;
      return apiFetch<CleanerrExclusionRule>('/api/v1/cleanerr/exclusions', {
        method: 'POST',
        body: JSON.stringify({
          ruleType,
          value: ruleValue.trim(),
          granularity: workflowId ? 'workflow' : 'global',
          workflowId,
          reason: ruleReason.trim() || 'Cleanerr exclusion rule',
          expiresAt: calculateExpiresAt(ruleTtl),
        }),
      });
    },
    onSuccess: () => {
      setBanner({
        kind: 'success',
        message: `Added exclusion rule [${ruleType}] "${ruleValue.trim()}".`,
      });
      setRuleValue('');
      void qc.invalidateQueries({ queryKey: ['cleanerr-exclusions'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-stats'] });
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Failed to create exclusion rule.',
      });
    },
  });

  const deleteRuleMutation = useMutation({
    mutationFn: (id: number) =>
      apiFetch(`/api/v1/cleanerr/exclusions/${id}`, { method: 'DELETE' }),
    onSuccess: () => {
      setBanner({
        kind: 'info',
        message: 'Exclusion rule revoked.',
      });
      setRevokeTarget(null);
      void qc.invalidateQueries({ queryKey: ['cleanerr-exclusions'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-media'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-stats'] });
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Failed to revoke exclusion rule.',
      });
    },
  });

  const toggleSelectAllVisible = () => {
    const visibleIds = visibleMedia.map((m) => m.id);
    const allSelected =
      visibleIds.length > 0 &&
      visibleIds.every((id) => selectedIds.includes(id));
    if (allSelected) {
      setSelectedIds((prev) => prev.filter((id) => !visibleIds.includes(id)));
    } else {
      setSelectedIds((prev) => Array.from(new Set([...prev, ...visibleIds])));
    }
  };

  const toggleSelectOne = (id: number) => {
    setSelectedIds((prev) =>
      prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id],
    );
  };

  const allVisibleSelected =
    visibleMedia.length > 0 &&
    visibleMedia.every((m) => selectedIds.includes(m.id));

  const exclusionCount = exclusionsQuery.data?.length ?? 0;
  const quotaLimit = status?.cleanerrExclusionLimit ?? null;
  const canWrite = Boolean(status?.canWriteWorkflows);
  const canExclude = Boolean(status?.canExclude);
  const totalPages = Math.max(1, Math.ceil(totalMediaCount / pageSize));
  const currentPage = Math.min(page, totalPages);

  return (
    <div className="flex flex-col flex-1">
      <PageToolbar
        left={
          <>
            <PageToolbarButton
              icon={Film}
              label={`Library Browser (${totalMediaCount})`}
              active={subTab === 'browser'}
              onClick={() => setSubTab('browser')}
            />
            <PageToolbarButton
              icon={ShieldCheck}
              label={`Exclusions Manager (${exclusionCount})`}
              active={subTab === 'exclusions'}
              onClick={() => setSubTab('exclusions')}
            />
            <PageToolbarSeparator />
            <PageToolbarButton
              icon={RefreshCw}
              label="Refresh"
              spinning={
                subTab === 'browser'
                  ? mediaQuery.isFetching
                  : exclusionsQuery.isFetching
              }
              onClick={() => {
                if (subTab === 'browser') {
                  void mediaQuery.refetch();
                } else {
                  void exclusionsQuery.refetch();
                }
              }}
            />
            {subTab === 'browser' && canWrite && (
              <>
                <PageToolbarButton
                  icon={Database}
                  label="Sync Catalog"
                  spinning={syncCatalogMutation.isPending}
                  onClick={() => syncCatalogMutation.mutate()}
                />
                <PageToolbarButton
                  icon={RefreshCw}
                  label="Sync Plex"
                  spinning={syncPlexMutation.isPending}
                  onClick={() => syncPlexMutation.mutate()}
                />
              </>
            )}
          </>
        }
        right={
          status && (
            <div className="flex items-center gap-2">
              {quotaLimit !== null && (
                <ArrLabel
                  kind={exclusionCount >= quotaLimit ? 'warning' : 'info'}
                >
                  Exclusion Quota: {exclusionCount} / {quotaLimit}
                </ArrLabel>
              )}
              <ArrLabel kind={status.canWriteWorkflows ? 'success' : 'default'}>
                {status.canWriteWorkflows
                  ? 'Cleanerr Admin'
                  : status.canExclude
                    ? quotaLimit === null
                      ? 'Unlimited Exclusions'
                      : `Quota: ${quotaLimit}`
                    : 'Read Only'}
              </ArrLabel>
            </div>
          )
        }
      />

      <div className="p-5 lg:p-6 space-y-5 overflow-x-hidden">
        <div role="status" aria-live="polite">
          {banner && <ArrAlert kind={banner.kind}>{banner.message}</ArrAlert>}
        </div>

        {subTab === 'browser' ? (
          <div className="space-y-4">
            <search>
              <div className="p-3.5 rounded-[4px] border border-[var(--arr-card-border)] bg-[var(--arr-card-bg)] space-y-3">
                <div className="flex flex-wrap items-center gap-3">
                  <div className="relative flex-1 min-w-[220px]">
                    <label htmlFor={mediaSearchId} className="sr-only">
                      Search media library
                    </label>
                    <Search
                      aria-hidden="true"
                      className="h-4 w-4 text-[var(--arr-muted)] absolute left-2.5 top-1/2 -translate-y-1/2"
                    />
                    <input
                      id={mediaSearchId}
                      type="search"
                      autoCorrect="off"
                      autoCapitalize="none"
                      spellCheck={false}
                      placeholder="Search title, series, tag, or Seerr requester..."
                      value={searchQuery}
                      onChange={(e) => {
                        setSearchQuery(e.target.value);
                        setPage(1);
                      }}
                      className="arr-input pl-9"
                    />
                  </div>

                  <div className="flex flex-wrap items-center gap-2">
                    <label htmlFor={librarySelectId} className="sr-only">
                      Filter by library
                    </label>
                    <select
                      id={librarySelectId}
                      value={libraryId}
                      onChange={(e) => {
                        setLibraryId(e.target.value);
                        setPage(1);
                      }}
                      className="arr-input !w-auto min-w-[160px]"
                    >
                      <option value="">All Libraries</option>
                      {(librariesQuery.data ?? []).map((lib) => (
                        <option key={lib.id} value={String(lib.id)}>
                          {lib.name}
                        </option>
                      ))}
                    </select>

                    <label htmlFor={watchedSelectId} className="sr-only">
                      Filter by watch state
                    </label>
                    <select
                      id={watchedSelectId}
                      value={watchedFilter}
                      onChange={(e) => {
                        setWatchedFilter(e.target.value as WatchedFilter);
                        setPage(1);
                      }}
                      className="arr-input !w-auto min-w-[130px]"
                    >
                      <option value="all">All Watch States</option>
                      <option value="watched">Watched Only</option>
                      <option value="unwatched">Unwatched Only</option>
                    </select>

                    <label htmlFor={sortSelectId} className="sr-only">
                      Sort media items
                    </label>
                    <select
                      id={sortSelectId}
                      value={sortBy}
                      onChange={(e) => {
                        setSortBy(e.target.value as SortOption);
                        setPage(1);
                      }}
                      className="arr-input !w-auto min-w-[165px]"
                    >
                      <option value="size">Sort: Size (Largest)</option>
                      <option value="last_viewed">
                        Sort: Last Watched (Oldest)
                      </option>
                      <option value="added">Sort: Added (Oldest)</option>
                      <option value="title">Sort: Title (A–Z)</option>
                    </select>
                  </div>
                </div>

                <div className="flex flex-wrap items-center justify-between gap-3">
                  <FilterBubbleGroup
                    label="Media Type"
                    labelId={mediaTypeGroupId}
                  >
                    {MEDIA_TYPE_OPTIONS.map((opt) => (
                      <FilterBubble
                        key={opt.id}
                        label={opt.label}
                        active={mediaType === opt.id}
                        groupLabelId={mediaTypeGroupId}
                        onClick={() => {
                          setMediaType(opt.id);
                          setPage(1);
                        }}
                      />
                    ))}
                  </FilterBubbleGroup>

                  <div className="flex flex-wrap items-center gap-2">
                    {selectedIds.length > 0 && (
                      <>
                        <span className="text-[12px] text-[var(--arr-muted)] font-medium">
                          {selectedIds.length} selected
                        </span>
                        {canExclude && (
                          <ArrButton
                            variant="primary"
                            busy={bulkExcludeMutation.isPending}
                            onClick={() =>
                              bulkExcludeMutation.mutate(selectedIds)
                            }
                          >
                            <ShieldCheck
                              aria-hidden="true"
                              className="h-3.5 w-3.5"
                            />
                            Bulk Exclude ({selectedIds.length})
                          </ArrButton>
                        )}
                        {canWrite && (
                          <ArrButton
                            variant="warning"
                            onClick={() => setManualStageOpen(true)}
                          >
                            <Clock aria-hidden="true" className="h-3.5 w-3.5" />
                            Manual Stage ({selectedIds.length})
                          </ArrButton>
                        )}
                        <ArrButton
                          variant="default"
                          onClick={() => setSelectedIds([])}
                        >
                          Clear
                        </ArrButton>
                      </>
                    )}
                    {selectedIds.length === 0 && canWrite && (
                      <ArrButton
                        variant="default"
                        onClick={() => setManualStageOpen(true)}
                      >
                        <Clock aria-hidden="true" className="h-3.5 w-3.5" />
                        Manual Stage...
                      </ArrButton>
                    )}
                  </div>
                </div>
              </div>
            </search>

            <div
              tabIndex={0}
              role="region"
              aria-labelledby={mediaTableCaptionId}
              className="cleanerr-table-wrapper cleanerr-responsive-table rounded-[4px] border border-[var(--arr-card-border)] bg-[var(--arr-card-bg)]"
            >
              <table className="arr-table w-full">
                <caption id={mediaTableCaptionId} className="sr-only">
                  Cleanerr synced media catalog items
                </caption>
                <thead>
                  <tr>
                    <th className="w-10">
                      <input
                        type="checkbox"
                        aria-label="Select all visible media items"
                        checked={allVisibleSelected}
                        onChange={toggleSelectAllVisible}
                      />
                    </th>
                    <th>Title</th>
                    <th>Type / Quality</th>
                    <th>Size</th>
                    <th>Watch State</th>
                    <th>Tags &amp; Requester</th>
                    <th className="text-right">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {mediaQuery.isLoading ? (
                    <tr>
                      <td
                        colSpan={7}
                        className="py-10 text-center text-[var(--arr-muted)]"
                      >
                        Loading synced media library...
                      </td>
                    </tr>
                  ) : mediaQuery.isError ? (
                    <tr>
                      <td
                        colSpan={7}
                        className="py-8 text-center text-[var(--arr-danger)]"
                      >
                        {(mediaQuery.error as Error)?.message ??
                          'Failed to load media catalog.'}
                      </td>
                    </tr>
                  ) : visibleMedia.length === 0 ? (
                    <tr>
                      <td
                        colSpan={7}
                        className="py-10 text-center text-[var(--arr-muted)]"
                      >
                        No media items match the current filters. Use Sync
                        Catalog or Sync Plex to populate.
                      </td>
                    </tr>
                  ) : (
                    visibleMedia.map((item) => {
                      const isSelected = selectedIds.includes(item.id);
                      const displayTitle = item.grandparentTitle
                        ? `${item.grandparentTitle} - S${String(item.seasonNumber ?? 0).padStart(2, '0')}E${String(item.episodeNumber ?? 0).padStart(2, '0')} - ${item.title}`
                        : item.parentTitle
                          ? `${item.parentTitle} - ${item.title}`
                          : item.title;

                      return (
                        <tr
                          key={item.id}
                          className={
                            isSelected ? 'bg-[var(--arr-primary)]/15' : undefined
                          }
                        >
                          <td data-label="Select">
                            <input
                              type="checkbox"
                              aria-label={`Select ${displayTitle}`}
                              checked={isSelected}
                              onChange={() => toggleSelectOne(item.id)}
                            />
                          </td>
                          <th
                            scope="row"
                            className="text-left font-medium text-[var(--arr-text)]"
                          >
                            <div className="flex items-center gap-2 min-w-0">
                              {item.mediaType === 'movie' ? (
                                <Film
                                  aria-hidden="true"
                                  className="h-4 w-4 text-amber-400 shrink-0"
                                />
                              ) : (
                                <Tv
                                  aria-hidden="true"
                                  className="h-4 w-4 text-[var(--arr-accent)] shrink-0"
                                />
                              )}
                              <div className="min-w-0">
                                <span
                                  className="font-semibold truncate max-w-[280px] block"
                                  title={displayTitle}
                                >
                                  {displayTitle}
                                </span>
                                <div className="text-[11px] text-[var(--arr-muted)] font-normal">
                                  {item.year ? `${item.year} • ` : ''}
                                  Added {formatShortDate(item.addedAt)}
                                </div>
                              </div>
                            </div>
                          </th>
                          <td data-label="Type / Quality">
                            <div className="flex flex-wrap items-center gap-1">
                              <ArrLabel kind="default">
                                {item.mediaType}
                              </ArrLabel>
                              {item.resolution && (
                                <ArrLabel kind="info">
                                  {item.resolution}
                                </ArrLabel>
                              )}
                              {item.isStaged && (
                                <ArrLabel kind="warning">Staged</ArrLabel>
                              )}
                            </div>
                          </td>
                          <td
                            data-label="Size"
                            className="font-mono whitespace-nowrap"
                          >
                            {formatBytes(item.sizeBytes)}
                          </td>
                          <td data-label="Watch State">
                            <div className="inline-flex items-center gap-1 text-[12px]">
                              <Eye
                                aria-hidden="true"
                                className="h-3.5 w-3.5 text-amber-400"
                              />
                              <span>
                                {item.viewCount > 0
                                  ? `${item.viewCount} views`
                                  : 'Unwatched'}
                              </span>
                            </div>
                            <div className="text-[11px] text-[var(--arr-muted)]">
                              Last: {formatShortDate(item.lastViewedAt)}
                            </div>
                          </td>
                          <td data-label="Tags & Requester">
                            <div className="flex flex-wrap items-center gap-1">
                              {item.seerrRequestedBy && (
                                <ArrLabel kind="purple">
                                  <User aria-hidden="true" className="h-3 w-3" />{' '}
                                  {item.seerrRequestedBy}
                                </ArrLabel>
                              )}
                              {(item.tags ?? []).map((t) => (
                                <ArrLabel key={t} kind="default">
                                  <Tag aria-hidden="true" className="h-2.5 w-2.5" />{' '}
                                  {t}
                                </ArrLabel>
                              ))}
                              {!item.seerrRequestedBy &&
                                (!item.tags || item.tags.length === 0) && (
                                  <span className="text-[11px] text-[var(--arr-muted)]">
                                    —
                                  </span>
                                )}
                            </div>
                          </td>
                          <td
                            data-label="Actions"
                            className="text-right whitespace-nowrap"
                          >
                            <div className="inline-flex items-center gap-1.5">
                              {item.isExcluded ? (
                                <ArrLabel kind="success">
                                  <ShieldCheck
                                    aria-hidden="true"
                                    className="h-3 w-3"
                                  />{' '}
                                  Protected
                                </ArrLabel>
                              ) : (
                                canExclude && (
                                  <ArrButton
                                    variant="default"
                                    className="!h-[26px] !px-2 !text-[11px]"
                                    busy={
                                      excludeSingleMutation.isPending &&
                                      excludeSingleMutation.variables?.id ===
                                        item.id
                                    }
                                    onClick={() =>
                                      excludeSingleMutation.mutate(item)
                                    }
                                  >
                                    <ShieldAlert
                                      aria-hidden="true"
                                      className="h-3 w-3 text-amber-400"
                                    />{' '}
                                    Exclude
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

            {totalMediaCount > 0 && (
              <nav
                aria-label="Media catalog pagination"
                className="flex flex-wrap items-center justify-between gap-3 px-3 py-2.5 rounded-[4px] border border-[var(--arr-card-border)] bg-[var(--arr-card-bg)] text-[12px]"
              >
                <span className="text-[var(--arr-muted)]">
                  Page{' '}
                  <strong className="text-[var(--arr-text)]">
                    {currentPage}
                  </strong>{' '}
                  of{' '}
                  <strong className="text-[var(--arr-text)]">
                    {totalPages}
                  </strong>{' '}
                  ({totalMediaCount} items)
                </span>
                <div className="flex items-center gap-2">
                  <ArrButton
                    variant="default"
                    className="!h-[28px] !px-3 !text-[12px]"
                    disabled={currentPage <= 1 || mediaQuery.isFetching}
                    onClick={() => setPage((p) => Math.max(1, p - 1))}
                  >
                    Previous
                  </ArrButton>
                  <ArrButton
                    variant="default"
                    className="!h-[28px] !px-3 !text-[12px]"
                    disabled={currentPage >= totalPages || mediaQuery.isFetching}
                    onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
                  >
                    Next
                  </ArrButton>
                </div>
              </nav>
            )}
          </div>
        ) : (
          <div className="space-y-6">
            <ArrAlert kind="info">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <div className="flex items-center gap-2 text-[12px]">
                  <Shield aria-hidden="true" className="h-4 w-4 shrink-0" />
                  <span>
                    TV series are always handled and protected in full. Rules can
                    target all workflows globally or a specific workflow.
                  </span>
                </div>
                {quotaLimit !== null && (
                  <ArrLabel
                    kind={exclusionCount >= quotaLimit ? 'warning' : 'success'}
                  >
                    Role Quota: {exclusionCount} of {quotaLimit} used
                  </ArrLabel>
                )}
              </div>
            </ArrAlert>

            {canExclude && (
              <form
                onSubmit={(e) => {
                  e.preventDefault();
                  if (!ruleValue.trim()) return;
                  createRuleMutation.mutate();
                }}
                className="p-4 rounded-[4px] border border-[var(--arr-card-border)] bg-[var(--arr-card-bg)]"
              >
                <FieldSet
                  legend="Create Exclusion Rule"
                  className="!mb-0"
                  action={
                    <ArrButton
                      type="submit"
                      variant="primary"
                      disabled={!ruleValue.trim()}
                      busy={createRuleMutation.isPending}
                    >
                      <Plus aria-hidden="true" className="h-3.5 w-3.5" /> Add
                      Exclusion Rule
                    </ArrButton>
                  }
                >
                  <FormGroup
                    label="Rule Type"
                    htmlFor={ruleTypeId}
                    helpText="Match media by Arr tag, Plex label, Plex user, Seerr requester, or library."
                  >
                    <select
                      id={ruleTypeId}
                      name="ruleType"
                      value={ruleType}
                      onChange={(e) =>
                        setRuleType(e.target.value as typeof ruleType)
                      }
                      className="arr-input"
                    >
                      <option value="tag">Arr Tag (tag)</option>
                      <option value="plex_label">
                        Plex Label (plex_label)
                      </option>
                      <option value="plex_user">Plex User (plex_user)</option>
                      <option value="seerr_user">
                        Seerr Requester (seerr_user)
                      </option>
                      <option value="library">Library (library)</option>
                    </select>
                  </FormGroup>

                  <FormGroup
                    label="Scope"
                    htmlFor={ruleScopeId}
                    helpText="Apply globally to all workflows or scope to a single workflow."
                  >
                    <select
                      id={ruleScopeId}
                      name="scope"
                      value={ruleScope}
                      onChange={(e) => setRuleScope(e.target.value)}
                      className="arr-input"
                    >
                      <option value="global">Global (All Workflows)</option>
                      {(workflowsQuery.data ?? []).map((wf) => (
                        <option key={wf.id} value={String(wf.id)}>
                          Workflow: {wf.name}
                        </option>
                      ))}
                    </select>
                  </FormGroup>

                  <FormGroup
                    label="Match Value"
                    htmlFor={ruleValueId}
                    required
                    helpText="Exact tag, label, username, or library name."
                  >
                    <div className="space-y-2">
                      <input
                        id={ruleValueId}
                        name="value"
                        type="text"
                        required
                        autoCorrect="off"
                        autoCapitalize="none"
                        spellCheck={false}
                        value={ruleValue}
                        onChange={(e) => setRuleValue(e.target.value)}
                        placeholder="e.g. keep, protected, 4k-remux..."
                        className="arr-input"
                      />
                      <div className="flex flex-wrap items-center gap-1.5">
                        <span className="text-[11px] text-[var(--arr-muted)]">
                          Quick chips:
                        </span>
                        {QUICK_TAG_CHIPS.map((chip) => (
                          <button
                            key={chip}
                            type="button"
                            onClick={() => setRuleValue(chip)}
                            className={`px-2 py-0.5 rounded text-[11px] font-medium border transition-colors cursor-pointer ${
                              ruleValue === chip
                                ? 'bg-[var(--arr-accent)] text-[var(--arr-accent-fg)] border-[var(--arr-accent)] font-bold'
                                : 'bg-[var(--arr-card-center-bg)] text-[var(--arr-muted)] border-[var(--arr-card-border)] hover:text-[var(--arr-text)]'
                            }`}
                          >
                            {chip}
                          </button>
                        ))}
                      </div>
                    </div>
                  </FormGroup>

                  <FormGroup
                    label="Duration (TTL)"
                    htmlFor={ruleTtlId}
                    helpText="Automatic expiration window or permanent protection."
                  >
                    <select
                      id={ruleTtlId}
                      name="ttl"
                      value={ruleTtl}
                      onChange={(e) => setRuleTtl(e.target.value)}
                      className="arr-input"
                    >
                      {TTL_OPTIONS.map((opt) => (
                        <option key={opt.value} value={opt.value}>
                          {opt.label}
                        </option>
                      ))}
                    </select>
                  </FormGroup>

                  <FormGroup label="Audit Reason" htmlFor={ruleReasonId}>
                    <input
                      id={ruleReasonId}
                      name="reason"
                      type="text"
                      value={ruleReason}
                      onChange={(e) => setRuleReason(e.target.value)}
                      placeholder="Reason for exclusion..."
                      className="arr-input"
                    />
                  </FormGroup>
                </FieldSet>
              </form>
            )}

            <FieldSet
              legend={`Active Exclusions (${filteredExclusions.length})`}
              action={
                <search>
                  <div className="relative">
                    <label htmlFor={ruleSearchId} className="sr-only">
                      Filter exclusion rules
                    </label>
                    <Search
                      aria-hidden="true"
                      className="h-3.5 w-3.5 text-[var(--arr-muted)] absolute left-2.5 top-1/2 -translate-y-1/2"
                    />
                    <input
                      id={ruleSearchId}
                      type="search"
                      autoCorrect="off"
                      autoCapitalize="none"
                      spellCheck={false}
                      placeholder="Filter exclusions..."
                      value={ruleSearch}
                      onChange={(e) => setRuleSearch(e.target.value)}
                      className="arr-input !h-[30px] pl-8 !text-[12px] !w-56"
                    />
                  </div>
                </search>
              }
            >
              <div
                tabIndex={0}
                role="region"
                aria-labelledby={rulesTableCaptionId}
                className="cleanerr-table-wrapper cleanerr-responsive-table rounded-[4px] border border-[var(--arr-card-border)] bg-[var(--arr-card-bg)]"
              >
                <table className="arr-table w-full">
                  <caption id={rulesTableCaptionId} className="sr-only">
                    Active Cleanerr exclusion rules
                  </caption>
                  <thead>
                    <tr>
                      <th>Protected Target</th>
                      <th>Rule Type</th>
                      <th>Scope</th>
                      <th>Reason</th>
                      <th>Added By</th>
                      <th>Expires</th>
                      <th className="text-right">Actions</th>
                    </tr>
                  </thead>
                  <tbody>
                    {exclusionsQuery.isLoading ? (
                      <tr>
                        <td
                          colSpan={7}
                          className="py-8 text-center text-[var(--arr-muted)]"
                        >
                          Loading exclusion rules...
                        </td>
                      </tr>
                    ) : filteredExclusions.length === 0 ? (
                      <tr>
                        <td
                          colSpan={7}
                          className="py-8 text-center text-[var(--arr-muted)]"
                        >
                          No active exclusion rules found.
                        </td>
                      </tr>
                    ) : (
                      filteredExclusions.map((rule) => {
                        const scopeLabel = rule.workflowId
                          ? `Workflow: ${workflowNameById.get(rule.workflowId) ?? `#${rule.workflowId}`}`
                          : rule.granularity === 'global'
                            ? 'Global'
                            : rule.granularity;

                        return (
                          <tr key={rule.id}>
                            <th
                              scope="row"
                              className="text-left font-semibold text-[var(--arr-text)]"
                            >
                              <span
                                className="truncate max-w-[260px] inline-block"
                                title={rule.value}
                              >
                                {rule.value}
                              </span>
                            </th>
                            <td data-label="Rule Type">
                              <ArrLabel
                                kind={
                                  rule.ruleType === 'tag'
                                    ? 'primary'
                                    : rule.ruleType === 'plex_label'
                                      ? 'purple'
                                      : rule.ruleType === 'seerr_user' ||
                                          rule.ruleType === 'plex_user'
                                        ? 'warning'
                                        : 'default'
                                }
                              >
                                {rule.ruleType}
                              </ArrLabel>
                            </td>
                            <td data-label="Scope">
                              <span className="text-[12px] text-[var(--arr-text-dim)]">
                                {scopeLabel}
                              </span>
                            </td>
                            <td data-label="Reason">
                              <span
                                className="text-[var(--arr-muted)] truncate max-w-[200px] inline-block"
                                title={rule.reason ?? '—'}
                              >
                                {rule.reason ?? '—'}
                              </span>
                            </td>
                            <td data-label="Added By">
                              <span className="text-[12px] text-[var(--arr-text-dim)]">
                                {rule.createdBy}
                              </span>
                            </td>
                            <td data-label="Expires">
                              {rule.expiresAt ? (
                                <span className="font-mono text-[11px] text-amber-400">
                                  {rule.expiresAt.slice(0, 10)}
                                </span>
                              ) : (
                                <ArrLabel kind="success">Permanent</ArrLabel>
                              )}
                            </td>
                            <td
                              data-label="Actions"
                              className="text-right whitespace-nowrap"
                            >
                              {status?.canWriteWorkflows && (
                                <ArrButton
                                  variant="default"
                                  className="!h-[26px] !px-2 !text-[11px] hover:!text-[var(--arr-danger)]"
                                  aria-label={`Revoke exclusion rule ${rule.value}`}
                                  onClick={() => setRevokeTarget(rule)}
                                >
                                  <Trash2
                                    aria-hidden="true"
                                    className="h-3.5 w-3.5"
                                  />{' '}
                                  Revoke
                                </ArrButton>
                              )}
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
        )}
      </div>

      {manualStageOpen && (
        <CleanerrManualStageModal
          initialSelectedIds={selectedIds}
          onClose={() => setManualStageOpen(false)}
          onSuccess={(count) => {
            setSelectedIds([]);
            setBanner({
              kind: 'success',
              message: `Staged ${count} item(s) in Leaving Soon queue.`,
            });
          }}
        />
      )}

      {revokeTarget && (
        <CleanerrConfirmModal
          title="Revoke Exclusion Rule"
          description={
            <>
              Are you sure you want to revoke the{' '}
              <strong>{revokeTarget.ruleType}</strong> exclusion for{' '}
              <strong>&ldquo;{revokeTarget.value}&rdquo;</strong>? Matching media
              may become eligible for scheduled cleanup workflows.
            </>
          }
          confirmLabel="Revoke Rule"
          confirmVariant="danger"
          busy={deleteRuleMutation.isPending}
          onConfirm={() => deleteRuleMutation.mutate(revokeTarget.id)}
          onClose={() => setRevokeTarget(null)}
        />
      )}
    </div>
  );
}
