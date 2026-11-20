import { useId, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  AlertCircle,
  ChevronLeft,
  ChevronRight,
  ExternalLink,
  Film,
  Loader2,
  RefreshCw,
  Search,
  X,
  Zap,
} from 'lucide-react';
import {
  apiFetch,
  formatBytes,
  type WantedBulkSearchReport,
  type WantedPagedResponse,
  type WantedUnitItemDto,
} from '../lib/api';
import {
  ArrAlert,
  ArrButton,
  ArrLabel,
  FilterBubble,
  FilterBubbleGroup,
  PageContentBody,
  PageToolbar,
  PageToolbarButton,
  PageToolbarSeparator,
  toggleFilterSetValue,
} from './arr/ArrPrimitives';

interface LibrarySummary {
  id: number;
  name: string;
  mediaType: string;
  rootFolderPath: string;
}

interface AutoSearchResponse {
  targetedUnitsCount: number;
  candidatesEvaluated: number;
  grabbed: boolean;
  selectedRelease?: string;
  message: string;
}

interface WantedViewProps {
  section: 'missing' | 'cutoff';
  onOpenWork?: (workId: number) => void;
  canSearchIndexers?: boolean;
  canWriteCatalog?: boolean;
}

const VERTICAL_BUBBLES = [
  { value: 'movies', label: 'Movies' },
  { value: 'tv', label: 'TV Series' },
  { value: 'anime', label: 'Anime' },
  { value: 'music', label: 'Music' },
  { value: 'ebooks', label: 'Books' },
  { value: 'audiobooks', label: 'Audiobooks' },
  { value: 'comics', label: 'Comics' },
];

export function WantedView({
  section,
  onOpenWork,
  canSearchIndexers = false,
}: WantedViewProps) {
  const qc = useQueryClient();
  const wantedTypeGroupId = useId();
  const wantedLibraryGroupId = useId();
  const wantedStatusGroupId = useId();

  // Multi-select bubble filters & Pagination State
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [selectedVerticals, setSelectedVerticals] = useState<Set<string>>(new Set());
  const [selectedLibraryIds, setSelectedLibraryIds] = useState<Set<number>>(new Set());
  const [monitoredOnly, setMonitoredOnly] = useState(true);
  const [releasedOnly, setReleasedOnly] = useState(true);
  const [language, setLanguage] = useState('');

  const verticalCsv = useMemo(
    () => Array.from(selectedVerticals).sort().join(','),
    [selectedVerticals],
  );
  const libraryIdsCsv = useMemo(
    () => Array.from(selectedLibraryIds).sort((a, b) => a - b).join(','),
    [selectedLibraryIds],
  );

  // Selected units for bulk operations
  const [selectedUnitIds, setSelectedUnitIds] = useState<Set<number>>(new Set());

  // Per-unit auto search state
  const [searchingUnitId, setSearchingUnitId] = useState<number | null>(null);

  // Bulk search report banner state
  const [lastBulkReport, setLastBulkReport] = useState<WantedBulkSearchReport | null>(null);
  const [showReportDetails, setShowReportDetails] = useState(false);
  const [actionAlert, setActionAlert] = useState<{ kind: 'info' | 'success' | 'warning' | 'danger'; text: string } | null>(null);

  // Fetch libraries for filter bubbles
  const librariesQuery = useQuery({
    queryKey: ['libraries'],
    queryFn: () => apiFetch<LibrarySummary[]>('/api/v1/libraries'),
  });

  // Query wanted items
  const queryEndpoint = section === 'missing' ? '/api/v1/wanted/missing' : '/api/v1/wanted/cutoff-unmet';

  const wantedQuery = useQuery({
    queryKey: [
      'wanted',
      section,
      page,
      pageSize,
      verticalCsv,
      libraryIdsCsv,
      monitoredOnly,
      releasedOnly,
      language,
    ],
    queryFn: () => {
      const params = new URLSearchParams({
        page: String(page),
        pageSize: String(pageSize),
        monitoredOnly: String(monitoredOnly),
        releasedOnly: String(releasedOnly),
      });
      if (verticalCsv) params.set('vertical', verticalCsv);
      if (libraryIdsCsv) params.set('libraryIds', libraryIdsCsv);
      if (language.trim()) params.set('language', language.trim());

      return apiFetch<WantedPagedResponse<WantedUnitItemDto>>(`${queryEndpoint}?${params.toString()}`);
    },
  });

  const totalPages = useMemo(() => {
    if (!wantedQuery.data) return 1;
    return Math.max(1, Math.ceil(wantedQuery.data.totalCount / pageSize));
  }, [wantedQuery.data, pageSize]);

  // Bulk search mutation
  const bulkSearchMutation = useMutation({
    mutationFn: (body: {
      mode: 'selected' | 'all_missing' | 'all_cutoff_unmet';
      unitIds?: number[];
      vertical?: string | null;
      libraryIds?: string | null;
      monitoredOnly?: boolean;
      maxItems?: number;
    }) =>
      apiFetch<WantedBulkSearchReport>('/api/v1/wanted/search', {
        method: 'POST',
        body: JSON.stringify(body),
      }),
    onSuccess: (report) => {
      setLastBulkReport(report);
      setShowReportDetails(false);
      setSelectedUnitIds(new Set());
      void qc.invalidateQueries({ queryKey: ['wanted'] });
      setActionAlert({
        kind: report.grabbedCount > 0 ? 'success' : 'info',
        text: `Bulk Search [${report.mode}]: Grabbed ${report.grabbedCount} of ${report.targetedUnitsCount} targeted items (${report.candidatesEvaluated} candidates evaluated across ${report.searchedGroupsCount} groups).`,
      });
    },
    onError: (err: unknown) => {
      const message = err instanceof Error ? err.message : String(err);
      setActionAlert({
        kind: 'danger',
        text: `Bulk search failed: ${message}`,
      });
    },
  });

  // Single unit auto-search mutation
  const unitAutoSearchMutation = useMutation({
    mutationFn: ({ workId, unitId }: { workId: number; unitId: number }) => {
      setSearchingUnitId(unitId);
      return apiFetch<AutoSearchResponse>(`/api/v1/catalog/works/${workId}/units/${unitId}/auto-search`, {
        method: 'POST',
      });
    },
    onSuccess: (res, vars) => {
      setSearchingUnitId(null);
      void qc.invalidateQueries({ queryKey: ['wanted'] });
      setActionAlert({
        kind: res.grabbed ? 'success' : 'warning',
        text: `Auto Search (Unit #${vars.unitId}): ${res.message}${res.selectedRelease ? ` → ${res.selectedRelease}` : ''}`,
      });
    },
    onError: (err: unknown, vars) => {
      setSearchingUnitId(null);
      const message = err instanceof Error ? err.message : String(err);
      setActionAlert({
        kind: 'danger',
        text: `Auto search for Unit #${vars.unitId} failed: ${message}`,
      });
    },
  });

  // Selection handlers
  const currentItems = wantedQuery.data?.items ?? [];
  const allCurrentSelected =
    currentItems.length > 0 && currentItems.every((item) => selectedUnitIds.has(item.unitId));

  function toggleSelectAllCurrent() {
    if (allCurrentSelected) {
      setSelectedUnitIds((prev) => {
        const next = new Set(prev);
        for (const item of currentItems) {
          next.delete(item.unitId);
        }
        return next;
      });
    } else {
      setSelectedUnitIds((prev) => {
        const next = new Set(prev);
        for (const item of currentItems) {
          next.add(item.unitId);
        }
        return next;
      });
    }
  }

  function toggleSelectUnit(unitId: number) {
    setSelectedUnitIds((prev) => {
      const next = new Set(prev);
      if (next.has(unitId)) {
        next.delete(unitId);
      } else {
        next.add(unitId);
      }
      return next;
    });
  }

  const isCutoff = section === 'cutoff';
  const activeFilterCount =
    selectedVerticals.size +
    selectedLibraryIds.size +
    (monitoredOnly ? 0 : 1) +
    (releasedOnly ? 0 : 1) +
    (language.trim() ? 1 : 0);

  return (
    <div className="flex flex-col flex-1 min-w-0">
      {/* ==================== PAGE TOOLBAR ==================== */}
      <PageToolbar
        left={
          <>
            <PageToolbarButton
              icon={Search}
              label={`Search Selected (${selectedUnitIds.size})`}
              disabled={selectedUnitIds.size === 0 || !canSearchIndexers || bulkSearchMutation.isPending}
              spinning={bulkSearchMutation.isPending && bulkSearchMutation.variables?.mode === 'selected'}
              onClick={() => {
                if (selectedUnitIds.size === 0) return;
                bulkSearchMutation.mutate({
                  mode: 'selected',
                  unitIds: Array.from(selectedUnitIds),
                });
              }}
              title="Search indexers for all selected units"
            />
            <PageToolbarButton
              icon={Zap}
              label={isCutoff ? 'Search All Cutoff' : 'Search All Missing'}
              disabled={!canSearchIndexers || bulkSearchMutation.isPending}
              spinning={
                bulkSearchMutation.isPending &&
                bulkSearchMutation.variables?.mode === (isCutoff ? 'all_cutoff_unmet' : 'all_missing')
              }
              onClick={() => {
                bulkSearchMutation.mutate({
                  mode: isCutoff ? 'all_cutoff_unmet' : 'all_missing',
                  vertical: verticalCsv || null,
                  libraryIds: libraryIdsCsv || null,
                  monitoredOnly,
                });
              }}
              title={
                isCutoff
                  ? 'Batch search indexers for all items not meeting quality or language cutoff'
                  : 'Batch search indexers for all missing episodes and movies'
              }
            />
            <PageToolbarSeparator />
            <PageToolbarButton
              icon={RefreshCw}
              label="Refresh"
              spinning={wantedQuery.isFetching}
              onClick={() => void wantedQuery.refetch()}
              title="Refresh wanted list"
            />
          </>
        }
        right={
          <div className="flex items-center gap-2 text-[12px] text-[#cccccc]">
            <span>
              Page <strong className="text-[#eeeeee]">{page}</strong> of{' '}
              <strong className="text-[#eeeeee]">{totalPages}</strong>
              {wantedQuery.data && (
                <span className="text-[#888888] ml-1">({wantedQuery.data.totalCount} items)</span>
              )}
            </span>
            <div className="inline-flex items-center gap-0.5 ml-2">
              <button
                type="button"
                disabled={page <= 1 || wantedQuery.isFetching}
                onClick={() => setPage((p) => Math.max(1, p - 1))}
                aria-label="Previous Page"
                className="p-1 rounded bg-arr-card hover:bg-[#444444] text-[#cccccc] disabled:opacity-40 disabled:pointer-events-none transition-colors"
              >
                <ChevronLeft className="h-4 w-4" />
              </button>
              <button
                type="button"
                disabled={page >= totalPages || wantedQuery.isFetching}
                onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
                aria-label="Next Page"
                className="p-1 rounded bg-arr-card hover:bg-[#444444] text-[#cccccc] disabled:opacity-40 disabled:pointer-events-none transition-colors"
              >
                <ChevronRight className="h-4 w-4" />
              </button>
            </div>
          </div>
        }
      />

      {/* ==================== MULTI-SELECT BUBBLE FILTER BAR ==================== */}
      <div className="bg-arr-page border-b border-arr-card-border px-4 lg:px-5 py-2.5 flex flex-wrap items-center gap-2 text-[12px]">
        <FilterBubbleGroup label="Type" labelId={wantedTypeGroupId}>
          {VERTICAL_BUBBLES.map((v) => (
            <FilterBubble
              key={v.value}
              label={v.label}
              active={selectedVerticals.has(v.value)}
              onClick={() => {
                setSelectedVerticals((prev) => toggleFilterSetValue(prev, v.value));
                setPage(1);
              }}
              tone="cyan"
              groupLabelId={wantedTypeGroupId}
            />
          ))}
        </FilterBubbleGroup>

        {(librariesQuery.data ?? []).length > 0 && (
          <FilterBubbleGroup label="Library" labelId={wantedLibraryGroupId}>
            {(librariesQuery.data ?? []).map((lib) => (
              <FilterBubble
                key={lib.id}
                label={lib.name}
                active={selectedLibraryIds.has(lib.id)}
                onClick={() => {
                  setSelectedLibraryIds((prev) => toggleFilterSetValue(prev, lib.id));
                  setPage(1);
                }}
                tone="purple"
                groupLabelId={wantedLibraryGroupId}
              />
            ))}
          </FilterBubbleGroup>
        )}

        <FilterBubbleGroup label="Status" labelId={wantedStatusGroupId}>
          <FilterBubble
            label="Monitored Only"
            active={monitoredOnly}
            onClick={() => {
              setMonitoredOnly((v) => !v);
              setPage(1);
            }}
            tone="green"
          />
          <FilterBubble
            label="Released Only"
            active={releasedOnly}
            onClick={() => {
              setReleasedOnly((v) => !v);
              setPage(1);
            }}
            tone="blue"
          />
        </FilterBubbleGroup>

        <div className="flex items-center gap-2 ml-auto">
          <input
            id="wanted-lang"
            type="text"
            value={language}
            onChange={(e) => {
              setLanguage(e.target.value);
              setPage(1);
            }}
            placeholder="Lang (de, en)"
            className="w-28 bg-[#25282c] border border-[#3a4046] rounded-full px-3 h-[28px] text-[12px] text-[#eeeeee] placeholder-[#777d85] focus:border-arr-accent outline-none font-mono"
          />

          <div className="inline-flex items-center rounded-full border border-[#3a4046] bg-[#25282c] p-0.5">
            {[25, 50, 100].map((sz) => (
              <button
                key={sz}
                type="button"
                onClick={() => {
                  setPageSize(sz);
                  setPage(1);
                }}
                className={
                  pageSize === sz
                    ? 'px-2 h-[22px] rounded-full bg-arr-accent text-arr-accent-fg text-[11px] font-semibold cursor-pointer'
                    : 'px-2 h-[22px] rounded-full text-[#909293] hover:text-[#eeeeee] text-[11px] cursor-pointer'
                }
              >
                {sz}
              </button>
            ))}
          </div>

          {activeFilterCount > 0 && (
            <button
              type="button"
              onClick={() => {
                setSelectedVerticals(new Set());
                setSelectedLibraryIds(new Set());
                setMonitoredOnly(true);
                setReleasedOnly(true);
                setLanguage('');
                setPage(1);
              }}
              className="inline-flex items-center gap-1 h-[28px] px-2.5 rounded-full border border-[#4a5158] bg-[#2a2e33] text-[#d0d4d9] hover:text-white hover:border-[#f05050] text-[11px] cursor-pointer"
            >
              <X className="h-3 w-3" /> Reset
            </button>
          )}
        </div>
      </div>

      {/* ==================== CONTENT BODY ==================== */}
      <PageContentBody className="space-y-4">
        {/* Action Alert Banner */}
        {actionAlert && (
          <ArrAlert
            kind={actionAlert.kind}
            className="flex items-center justify-between gap-2"
          >
            <span>{actionAlert.text}</span>
            <button
              type="button"
              onClick={() => setActionAlert(null)}
              className="text-inherit opacity-70 hover:opacity-100 p-0.5 cursor-pointer"
              aria-label="Dismiss alert"
            >
              <X className="h-4 w-4" />
            </button>
          </ArrAlert>
        )}

        {/* Bulk Search Report Box */}
        {lastBulkReport && (
          <div className="bg-[#25282c] border border-[#3b434c] rounded-[4px] p-4 text-[13px] text-[#dddddd]">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div className="flex items-center gap-2">
                <ArrLabel kind={lastBulkReport.grabbedCount > 0 ? 'success' : 'warning'}>
                  {lastBulkReport.mode.toUpperCase()}
                </ArrLabel>
                <span>
                  Targeted <strong className="text-[#ffffff]">{lastBulkReport.targetedUnitsCount}</strong> items in{' '}
                  <strong className="text-[#ffffff]">{lastBulkReport.searchedGroupsCount}</strong> search groups • Evaluated{' '}
                  <strong className="text-[#ffffff]">{lastBulkReport.candidatesEvaluated}</strong> candidates • Grabbed{' '}
                  <strong className={lastBulkReport.grabbedCount > 0 ? 'text-[#27c24c]' : 'text-[#ffffff]'}>
                    {lastBulkReport.grabbedCount}
                  </strong>{' '}
                  releases
                </span>
              </div>
              <div className="flex items-center gap-2">
                {lastBulkReport.outcomes.length > 0 && (
                  <button
                    type="button"
                    onClick={() => setShowReportDetails((v) => !v)}
                    className="text-arr-accent hover:underline text-[12px] cursor-pointer"
                  >
                    {showReportDetails ? 'Hide Details' : `View Details (${lastBulkReport.outcomes.length})`}
                  </button>
                )}
                <button
                  type="button"
                  onClick={() => setLastBulkReport(null)}
                  className="text-[#888888] hover:text-[#eeeeee] p-1 cursor-pointer"
                  aria-label="Dismiss bulk report"
                >
                  <X className="h-4 w-4" />
                </button>
              </div>
            </div>

            {showReportDetails && lastBulkReport.outcomes.length > 0 && (
              <div className="mt-3 pt-3 border-t border-[#3b434c] overflow-x-auto max-h-72 overflow-y-auto">
                <table className="arr-table text-[12px]">
                  <thead>
                    <tr>
                      <th>Work / Scope</th>
                      <th>Candidates</th>
                      <th>Status</th>
                      <th>Grabbed Release</th>
                      <th>Message</th>
                    </tr>
                  </thead>
                  <tbody>
                    {lastBulkReport.outcomes.map((o, idx) => (
                      <tr key={`${o.workId}-${idx}`}>
                        <td className="font-medium text-[#eeeeee]">
                          {o.workTitle}
                          <span className="text-[#888888] ml-1.5 font-normal">({o.label})</span>
                        </td>
                        <td>{o.candidatesFound}</td>
                        <td>
                          <ArrLabel kind={o.grabbed ? 'success' : 'default'}>
                            {o.grabbed ? 'Grabbed' : 'None Grabbed'}
                          </ArrLabel>
                        </td>
                        <td className="font-mono text-[11px] text-[#cccccc] max-w-xs truncate">
                          {o.selectedRelease ?? '-'}
                        </td>
                        <td className="text-[#999999]">{o.message}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        )}

        {/* Table / Loading / Empty */}
        {wantedQuery.isLoading ? (
          <div className="py-20 flex flex-col items-center justify-center gap-3 text-[#909293] text-[14px]">
            <Loader2 className="h-8 w-8 animate-spin text-arr-accent" />
            <span>Loading {isCutoff ? 'Cutoff Unmet' : 'Missing'} items...</span>
          </div>
        ) : currentItems.length === 0 ? (
          <ArrAlert kind="info" className="flex items-center gap-2.5">
            <AlertCircle className="h-5 w-5 text-arr-accent" />
            <div>
              <p className="font-medium text-[#ffffff]">
                No {isCutoff ? 'Cutoff Unmet' : 'Missing'} items found.
              </p>
              <p className="text-[12px] text-[#cccccc] mt-0.5">
                All media items matching your current filters have satisfied their profiles and language policies.
              </p>
            </div>
          </ArrAlert>
        ) : (
          <div className="overflow-x-auto border border-arr-card-border rounded-[4px] bg-arr-heading">
            <table className="arr-table">
              <thead>
                <tr>
                  <th className="w-10 text-center">
                    <input
                      type="checkbox"
                      checked={allCurrentSelected}
                      onChange={toggleSelectAllCurrent}
                      aria-label="Select all on this page"
                      className="accent-arr-accent rounded"
                    />
                  </th>
                  <th>Work</th>
                  <th>Code</th>
                  <th>Title</th>
                  <th>Air / Release Date</th>
                  <th>Profile</th>
                  <th>Languages (Req / Exist)</th>
                  <th>{isCutoff ? 'Cutoff Reason' : 'Status'}</th>
                  <th className="text-right">Action</th>
                </tr>
              </thead>
              <tbody>
                {currentItems.map((item) => {
                  const isSelected = selectedUnitIds.has(item.unitId);
                  const isSearchingThis = searchingUnitId === item.unitId;

                  return (
                    <tr
                      key={item.unitId}
                      className={isSelected ? '!bg-[#2d3748]' : undefined}
                    >
                      <td className="text-center">
                        <input
                          type="checkbox"
                          checked={isSelected}
                          onChange={() => toggleSelectUnit(item.unitId)}
                          aria-label={`Select ${item.workTitle} ${item.episodeCode}`}
                          className="accent-arr-accent rounded"
                        />
                      </td>

                      {/* Work Title & Poster */}
                      <td>
                        <div className="flex items-center gap-2.5">
                          {item.workPosterUrl ? (
                            <img
                              src={item.workPosterUrl}
                              alt=""
                              className="w-7 h-10 object-cover rounded-[2px] bg-[#111111] shrink-0"
                              onError={(e) => {
                                (e.currentTarget as HTMLElement).style.display = 'none';
                              }}
                            />
                          ) : (
                            <div className="w-7 h-10 rounded-[2px] bg-arr-card flex items-center justify-center shrink-0 text-[#666666]">
                              <Film className="h-4 w-4" />
                            </div>
                          )}
                          <div className="min-w-0">
                            {onOpenWork ? (
                              <button
                                type="button"
                                onClick={() => onOpenWork(item.workId)}
                                className="text-[13px] font-medium text-[#eeeeee] hover:text-arr-accent text-left truncate flex items-center gap-1 cursor-pointer transition-colors"
                                title={`Open ${item.workTitle} in Catalog`}
                              >
                                <span className="truncate">{item.workTitle}</span>
                                <ExternalLink className="h-3 w-3 opacity-60 shrink-0" />
                              </button>
                            ) : (
                              <span className="text-[13px] font-medium text-[#eeeeee] truncate">
                                {item.workTitle}
                              </span>
                            )}
                            <div className="flex items-center gap-1.5 mt-0.5 text-[11px] text-[#888888]">
                              <ArrLabel kind="inverse" className="!text-[10px] !py-0">
                                {item.mediaVertical}
                              </ArrLabel>
                              <span>{item.libraryName}</span>
                            </div>
                          </div>
                        </div>
                      </td>

                      {/* Episode Code */}
                      <td>
                        <span className="font-mono text-[12px] font-semibold text-arr-accent">
                          {item.episodeCode || '-'}
                        </span>
                      </td>

                      {/* Unit Title */}
                      <td className="text-[13px] text-[#cccccc] max-w-xs truncate" title={item.unitTitle}>
                        {item.unitTitle || item.containerTitle || '-'}
                      </td>

                      {/* Air / Release Date */}
                      <td className="text-[12px] text-[#aaaaaa]">
                        {item.airOrReleaseDate ? item.airOrReleaseDate.slice(0, 10) : '-'}
                      </td>

                      {/* Effective Profile */}
                      <td>
                        <span className="text-[12px] text-[#bbbbbb]">
                          {item.effectiveProfileName || '-'}
                        </span>
                      </td>

                      {/* Languages */}
                      <td>
                        <div className="flex flex-col gap-1">
                          {item.requiredAudioLangs && item.requiredAudioLangs.length > 0 && (
                            <div className="flex items-center gap-1">
                              <span className="text-[10px] uppercase text-[#888888]">Req:</span>
                              <div className="flex flex-wrap gap-0.5">
                                {item.requiredAudioLangs.map((lang) => (
                                  <ArrLabel key={`req-${lang}`} kind="purple" className="!text-[10px] !py-0">
                                    {lang}
                                  </ArrLabel>
                                ))}
                              </div>
                            </div>
                          )}
                          {item.existingAudioLangs && item.existingAudioLangs.length > 0 && (
                            <div className="flex items-center gap-1">
                              <span className="text-[10px] uppercase text-[#888888]">Exist:</span>
                              <div className="flex flex-wrap gap-0.5">
                                {item.existingAudioLangs.map((lang) => (
                                  <ArrLabel key={`ex-${lang}`} kind="success" className="!text-[10px] !py-0">
                                    {lang}
                                  </ArrLabel>
                                ))}
                              </div>
                            </div>
                          )}
                          {(!item.requiredAudioLangs || item.requiredAudioLangs.length === 0) &&
                            (!item.existingAudioLangs || item.existingAudioLangs.length === 0) && (
                              <span className="text-[11px] text-[#666666]">-</span>
                            )}
                        </div>
                      </td>

                      {/* Status / Cutoff Unmet Reason */}
                      <td>
                        {isCutoff ? (
                          <div className="flex flex-col gap-1 items-start">
                            {item.existingQualityName && (
                              <ArrLabel kind="warning">
                                {item.existingQualityName}
                                {item.existingSizeBytes ? ` (${formatBytes(item.existingSizeBytes)})` : ''}
                              </ArrLabel>
                            )}
                            <ArrLabel kind="danger">
                              {item.cutoffUnmetReason || 'Cutoff Unmet'}
                            </ArrLabel>
                          </div>
                        ) : (
                          <ArrLabel kind="danger">Missing</ArrLabel>
                        )}
                      </td>

                      {/* Per-row Auto Search Action */}
                      <td className="text-right">
                        <ArrButton
                          variant="default"
                          className="!h-[28px] !px-2.5 !text-[12px]"
                          disabled={!canSearchIndexers || unitAutoSearchMutation.isPending}
                          busy={isSearchingThis}
                          onClick={() =>
                            unitAutoSearchMutation.mutate({
                              workId: item.workId,
                              unitId: item.unitId,
                            })
                          }
                          title="Trigger automated search for this specific unit"
                        >
                          <Search className="h-3.5 w-3.5" />
                          <span>Search</span>
                        </ArrButton>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </PageContentBody>
    </div>
  );
}
