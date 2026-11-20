import { useId } from 'react';
import { resolveWorkDownloadProgress } from './catalogProgressHelpers';
import {
  AlertTriangle,
  ArrowDown,
  ArrowUp,
  Bookmark,
  Check,
  FolderPlus,
  Languages,
  Loader2,
  Plus,
  SlidersHorizontal,
  Sparkles,
  Trash2,
  Wrench,
  X,
} from 'lucide-react';
import clsx from 'clsx';
import {
  ArrAlert,
  ArrButton,
  ArrLabel,
  FilterBubble,
  FilterBubbleGroup,
  toggleFilterSetValue,
} from '../arr/ArrPrimitives';
import {
  PosterArtwork,
  COMMON_ISO_LANGUAGES,
  formatCatalogAddedDate,
  resolveClassificationBadge,
  verticalNoun,
  type CatalogAutoClassifyReportDto,
  type CatalogAvailabilityBubble,
  type CatalogMonitorBubble,
  type CatalogSortField,
  type CatalogSubSection,
  type CatalogViewMode,
  type HealthFilterMeta,
  type LanguagePolicySummary,
  type LibrarySummary,
  type MediaWorkSummary,
  type QualityProfileSummary,
} from './catalogTypesAndHelpers';

export interface CatalogFacetCounts {
  verticalCounts: Record<string, number>;
  libraryCounts: Record<number, number>;
  monitored: number;
  unmonitored: number;
  missing: number;
  complete: number;
  continuing: number;
}

export interface CatalogFilterBarProps {
  libraries: LibrarySummary[];
  visibleVerticalBubbles: ReadonlyArray<{ id: string; label: string }>;
  catalogFacetCounts: CatalogFacetCounts;
  selectedVerticals: Set<string>;
  onToggleVertical: (id: string) => void;
  selectedLibraryIds: Set<number>;
  onToggleLibraryId: (id: number) => void;
  selectedMonitorBubbles: Set<CatalogMonitorBubble>;
  onToggleMonitorBubble: (id: CatalogMonitorBubble) => void;
  selectedAvailabilityBubbles: Set<CatalogAvailabilityBubble>;
  onToggleAvailabilityBubble: (id: CatalogAvailabilityBubble) => void;
  normalizedUrlHealth: string;
  onClearUrlParams?: () => void;
  classificationFilter:
    | 'all'
    | 'anime-series'
    | 'anime-movie'
    | 'cartoon'
    | 'live-action-series'
    | 'live-action-movie';
  onSetClassificationFilter: (
    val: 'all' | 'anime-series' | 'anime-movie' | 'cartoon' | 'live-action-series' | 'live-action-movie',
  ) => void;
  filteredWorksCount: number;
  allWorksCount: number;
  activeCatalogFilterCount: number;
  onClearAllCatalogFilters: () => void;
}

export function CatalogFilterBubbleBar({
  libraries,
  visibleVerticalBubbles,
  catalogFacetCounts,
  selectedVerticals,
  onToggleVertical,
  selectedLibraryIds,
  onToggleLibraryId,
  selectedMonitorBubbles,
  onToggleMonitorBubble,
  selectedAvailabilityBubbles,
  onToggleAvailabilityBubble,
  normalizedUrlHealth,
  onClearUrlParams,
  classificationFilter,
  onSetClassificationFilter,
  filteredWorksCount,
  allWorksCount,
  activeCatalogFilterCount,
  onClearAllCatalogFilters,
}: CatalogFilterBarProps) {
  const typeGroupLabelId = useId();
  const libraryGroupLabelId = useId();
  const statusGroupLabelId = useId();
  const classificationSelectId = useId();

  if (libraries.length === 0) return null;

  return (
    <div className="bg-arr-filterbar border-b border-arr-card-border px-4 lg:px-6 py-2.5 flex flex-wrap items-center gap-x-2 gap-y-2 select-none">
      <FilterBubbleGroup label="Type" labelId={typeGroupLabelId}>
        {visibleVerticalBubbles.map((vb) => (
          <FilterBubble
            key={vb.id}
            label={vb.label}
            count={catalogFacetCounts.verticalCounts[vb.id] ?? 0}
            active={selectedVerticals.has(vb.id)}
            onClick={() => onToggleVertical(vb.id)}
            tone="cyan"
            groupLabelId={typeGroupLabelId}
            title={`Toggle ${vb.label} filter (select multiple)`}
          />
        ))}
      </FilterBubbleGroup>

      {libraries.length > 0 && (
        <FilterBubbleGroup label="Library" labelId={libraryGroupLabelId}>
          {libraries.map((lib) => (
            <FilterBubble
              key={lib.id}
              label={lib.name}
              count={catalogFacetCounts.libraryCounts[lib.id] ?? lib.workCount}
              active={selectedLibraryIds.has(lib.id)}
              onClick={() => onToggleLibraryId(lib.id)}
              tone="purple"
              groupLabelId={libraryGroupLabelId}
              title={`Toggle library '${lib.name}' (${lib.rootFolderPath})`}
            />
          ))}
        </FilterBubbleGroup>
      )}

      <FilterBubbleGroup label="Status" labelId={statusGroupLabelId}>
        <FilterBubble
          label="Monitored"
          count={catalogFacetCounts.monitored}
          active={selectedMonitorBubbles.has('monitored')}
          onClick={() => onToggleMonitorBubble('monitored')}
          tone="blue"
        />
        <FilterBubble
          label="Unmonitored"
          count={catalogFacetCounts.unmonitored}
          active={selectedMonitorBubbles.has('unmonitored')}
          onClick={() => onToggleMonitorBubble('unmonitored')}
          tone="amber"
        />
        <FilterBubble
          label="Missing"
          count={catalogFacetCounts.missing}
          active={selectedAvailabilityBubbles.has('missing')}
          onClick={() => onToggleAvailabilityBubble('missing')}
          tone="rose"
        />
        <FilterBubble
          label="Downloaded"
          count={catalogFacetCounts.complete}
          active={selectedAvailabilityBubbles.has('complete')}
          onClick={() => onToggleAvailabilityBubble('complete')}
          tone="green"
        />
        {catalogFacetCounts.continuing > 0 && (
          <FilterBubble
            label="Continuing"
            count={catalogFacetCounts.continuing}
            active={selectedAvailabilityBubbles.has('continuing')}
            onClick={() => onToggleAvailabilityBubble('continuing')}
            tone="cyan"
          />
        )}
        {normalizedUrlHealth && (
          <FilterBubble
            label={`Health: ${normalizedUrlHealth}`}
            active={true}
            onClick={() => onClearUrlParams?.()}
            tone="rose"
          />
        )}
      </FilterBubbleGroup>

      <div className="inline-flex items-center gap-1.5 rounded-[6px] bg-black/20 border border-[#323840] px-2.5 py-1">
        <label
          htmlFor={classificationSelectId}
          className="text-[10px] font-bold uppercase tracking-wider text-[#8d959e] border-r border-[#323840] pr-2 mr-0.5 select-none cursor-pointer"
        >
          Classification
        </label>
        <select
          id={classificationSelectId}
          value={classificationFilter}
          onChange={(e) =>
            onSetClassificationFilter(
              e.target.value as
                | 'all'
                | 'anime-series'
                | 'anime-movie'
                | 'cartoon'
                | 'live-action-series'
                | 'live-action-movie',
            )
          }
          className={clsx(
            'h-[28px] px-2.5 rounded-full border text-[11px] font-medium outline-none transition-colors cursor-pointer',
            classificationFilter !== 'all'
              ? 'bg-arr-accent/15 border-arr-accent text-arr-accent font-semibold'
              : 'bg-[#25282c] border-[#3a4046] text-[#c0c4c9] hover:border-[#59626b]',
          )}
          title="Filter library by media classification"
        >
          <option value="all">All Classifications</option>
          <option value="anime-series">Anime Series</option>
          <option value="anime-movie">Anime Movie</option>
          <option value="cartoon">Cartoon (Series &amp; Movie)</option>
          <option value="live-action-series">Live-Action TV</option>
          <option value="live-action-movie">Live-Action Movie</option>
        </select>
      </div>

      <div className="ml-auto flex items-center gap-2 text-[11px] text-[#90969e]">
        <span>
          Showing <strong className="text-[#eeeeee]">{filteredWorksCount}</strong> of{' '}
          <strong className="text-[#cccccc]">{allWorksCount}</strong>
        </span>
        {activeCatalogFilterCount > 0 && (
          <button
            type="button"
            onClick={onClearAllCatalogFilters}
            className="inline-flex items-center gap-1 h-[26px] px-2.5 rounded-full border border-[#4a5158] bg-[#2a2e33] text-[#e1e4e8] hover:text-white hover:border-[#f05050] text-[11px] font-medium transition-colors cursor-pointer"
          >
            <X className="h-3 w-3" /> Clear ({activeCatalogFilterCount})
          </button>
        )}
      </div>
    </div>
  );
}

export interface CatalogLibraryGridSectionProps {
  healthFilterMeta: HealthFilterMeta | null;
  isResolveHealthFilterPending: boolean;
  onResolveHealthFilter: (resolveAction: string) => void;
  onClearUrlParams?: () => void;
  autoClassifyReport: CatalogAutoClassifyReportDto | null;
  onDismissAutoClassifyReport: () => void;
  workActionBanner: { kind: 'info' | 'success' | 'warning'; text: string } | null;
  onDismissWorkActionBanner: () => void;
  libraries: LibrarySummary[];
  librariesById: Map<number, LibrarySummary>;
  canWriteCatalog: boolean;
  onNavigate?: (target: CatalogSubSection) => void;
  onSetShowAddLibModal: (v: boolean) => void;
  isWorksLoading: boolean;
  filteredWorks: MediaWorkSummary[];
  allWorks: MediaWorkSummary[];
  activeCatalogFilterCount: number;
  onClearAllCatalogFilters: () => void;
  catalogFilterText: string;
  onSearchInAddNew?: (query: string) => void;
  massEditMode: boolean;
  selectedWorkIds: Set<number>;
  onSetSelectedWorkIds: React.Dispatch<React.SetStateAction<Set<number>>>;
  viewMode: CatalogViewMode;
  catalogSortField?: CatalogSortField;
  catalogSortAsc?: boolean;
  onSelectCatalogSort?: (field: CatalogSortField) => void;
  onOpenWork: (workId: number) => void;
  onQuickToggleWorkMonitor: (workId: number, monitored: boolean) => void;
  onSetShowDeleteConfirmModal: (v: boolean) => void;
  isBulkEditPending: boolean;
  onBulkEditWorks: () => void;
  isBulkScanAndVerifyPending?: boolean;
  onBulkScanAndVerify?: () => void;
  bulkMonitored: 'keep' | 'monitored' | 'unmonitored';
  onSetBulkMonitored: (v: 'keep' | 'monitored' | 'unmonitored') => void;
  bulkMonitorPreset: string;
  onSetBulkMonitorPreset: (v: string) => void;
  bulkQualityProfileId: string;
  onSetBulkQualityProfileId: (v: string) => void;
  profiles: QualityProfileSummary[];
  languagePolicies?: LanguagePolicySummary[];
  bulkLanguagePolicyId?: string;
  onSetBulkLanguagePolicyId?: (v: string) => void;
  bulkOriginalLanguage?: string;
  onSetBulkOriginalLanguage?: (v: string) => void;
  bulkMinAvailability: string;
  onSetBulkMinAvailability: (v: string) => void;
  bulkLibraryId: string;
  onSetBulkLibraryId: (v: string) => void;
  bulkMoveFiles: boolean;
  onSetBulkMoveFiles: (v: boolean) => void;
  bulkTags: string;
  onSetBulkTags: (v: string) => void;
  bulkTagMode: 'add' | 'remove' | 'replace';
  onSetBulkTagMode: (v: 'add' | 'remove' | 'replace') => void;
}

function normalizeWorkVertical(wk: string): string {
  const v = wk.toLowerCase();
  if (v === 'movie') return 'movies';
  if (v === 'series') return 'tv';
  if (v === 'book') return 'ebooks';
  return v;
}

function renderSortableTableHeader(
  field: CatalogSortField,
  label: string,
  catalogSortField?: CatalogSortField,
  catalogSortAsc?: boolean,
  onSelectCatalogSort?: (field: CatalogSortField) => void,
) {
  const isSorted = catalogSortField === field;
  const ariaSort =
    onSelectCatalogSort && isSorted
      ? catalogSortAsc
        ? 'ascending'
        : 'descending'
      : undefined;
  return (
    <th scope="col" aria-sort={ariaSort}>
      {onSelectCatalogSort ? (
        <button
          type="button"
          onClick={() => onSelectCatalogSort(field)}
          className="inline-flex items-center gap-1 min-h-[24px] w-full text-left font-inherit text-inherit hover:text-white focus-visible:outline focus-visible:outline-2 focus-visible:outline-arr-accent rounded-sm cursor-pointer"
        >
          <span>{label}</span>
          {isSorted &&
            (catalogSortAsc ? (
              <ArrowUp className="h-3 w-3" aria-hidden="true" />
            ) : (
              <ArrowDown className="h-3 w-3" aria-hidden="true" />
            ))}
        </button>
      ) : (
        label
      )}
    </th>
  );
}

function CatalogWorksTableRegion({ children }: { children: React.ReactNode }) {
  const captionId = useId();
  return (
    <div className="overflow-x-auto" role="region" aria-labelledby={captionId} tabIndex={0}>
      <table className="arr-table">
        <caption id={captionId} className="sr-only">
          Library catalog
        </caption>
        {children}
      </table>
    </div>
  );
}

export function CatalogLibraryGridSection({
  healthFilterMeta,
  isResolveHealthFilterPending,
  onResolveHealthFilter,
  onClearUrlParams,
  autoClassifyReport,
  onDismissAutoClassifyReport,
  workActionBanner,
  onDismissWorkActionBanner,
  libraries,
  librariesById,
  canWriteCatalog,
  onNavigate,
  onSetShowAddLibModal,
  isWorksLoading,
  filteredWorks,
  allWorks,
  activeCatalogFilterCount,
  onClearAllCatalogFilters,
  catalogFilterText,
  onSearchInAddNew,
  massEditMode,
  selectedWorkIds,
  onSetSelectedWorkIds,
  viewMode,
  catalogSortField,
  catalogSortAsc,
  onSelectCatalogSort,
  onOpenWork,
  onQuickToggleWorkMonitor,
  onSetShowDeleteConfirmModal,
  isBulkEditPending,
  onBulkEditWorks,
  isBulkScanAndVerifyPending,
  onBulkScanAndVerify,
  bulkMonitored,
  onSetBulkMonitored,
  bulkMonitorPreset,
  onSetBulkMonitorPreset,
  bulkQualityProfileId,
  onSetBulkQualityProfileId,
  profiles,
  languagePolicies = [],
  bulkLanguagePolicyId = 'keep',
  onSetBulkLanguagePolicyId,
  bulkOriginalLanguage = 'keep',
  onSetBulkOriginalLanguage,
  bulkMinAvailability,
  onSetBulkMinAvailability,
  bulkLibraryId,
  onSetBulkLibraryId,
  bulkMoveFiles,
  onSetBulkMoveFiles,
  bulkTags,
  onSetBulkTags,
  bulkTagMode,
  onSetBulkTagMode,
}: CatalogLibraryGridSectionProps) {
  return (
    <>
      {healthFilterMeta && (
        <div className="mb-4 rounded-[4px] border border-[#f8ac59]/50 bg-[#2d2518] p-3.5 text-[#eeeeee] flex flex-col sm:flex-row sm:items-center justify-between gap-3">
          <div className="flex items-start gap-2.5">
            <AlertTriangle className="h-4 w-4 text-[#f8ac59] shrink-0 mt-0.5" />
            <div>
              <div className="text-[13px] font-semibold text-[#fcd34d]">
                Active Health Check Filter: {healthFilterMeta.title}
              </div>
              <div className="text-[12px] text-[#d6d3d1] mt-0.5">{healthFilterMeta.description}</div>
            </div>
          </div>
          <div className="flex flex-wrap items-center gap-2 shrink-0">
            {healthFilterMeta.resolveAction && (
              <ArrButton
                variant="primary"
                busy={isResolveHealthFilterPending}
                onClick={() => onResolveHealthFilter(healthFilterMeta.resolveAction!)}
              >
                <Wrench className="h-3.5 w-3.5" /> {healthFilterMeta.resolveButtonLabel}
              </ArrButton>
            )}
            <ArrButton variant="default" onClick={() => onClearUrlParams?.()}>
              <X className="h-3.5 w-3.5" /> Clear Filter
            </ArrButton>
          </div>
        </div>
      )}

      {autoClassifyReport && (
        <div className="mb-4 bg-[#1e2430] border border-[#3b82f6]/40 rounded-[4px] p-3.5 text-[12.5px] text-[#e2e8f0] flex flex-col sm:flex-row sm:items-center justify-between gap-3 shadow-md">
          <div className="space-y-1">
            <div className="flex items-center gap-2 font-semibold text-[#60a5fa]">
              <Sparkles className="h-4 w-4 text-[#38bdf8]" />
              <span>Auto-Classification &amp; Tagging Report</span>
            </div>
            <div className="flex flex-wrap items-center gap-2 text-[12px] text-[#cbd5e1]">
              <span>
                Evaluated: <strong className="text-white">{autoClassifyReport.evaluatedCount}</strong>
              </span>
              <span className="text-[#475569]">•</span>
              <span>
                Updated: <strong className="text-[#4ade80]">{autoClassifyReport.updatedCount}</strong>
              </span>
              {Object.keys(autoClassifyReport.countsByClassification ?? {}).length > 0 && (
                <>
                  <span className="text-[#475569]">•</span>
                  <div className="flex flex-wrap items-center gap-1.5">
                    {Object.entries(autoClassifyReport.countsByClassification).map(([clsKey, count]) => {
                      const sampleBadge = resolveClassificationBadge({ classificationKind: clsKey });
                      return (
                        <span
                          key={clsKey}
                          className={clsx(
                            'px-1.5 py-0.5 rounded text-[10.5px] font-medium tracking-wide',
                            sampleBadge?.badgeClass ?? 'bg-[#334155] text-[#e2e8f0]',
                          )}
                        >
                          {sampleBadge?.label ?? clsKey}: {count}
                        </span>
                      );
                    })}
                  </div>
                </>
              )}
            </div>
          </div>
          <button
            type="button"
            onClick={onDismissAutoClassifyReport}
            className="text-[#94a3b8] hover:text-white transition-colors self-start sm:self-center p-1 cursor-pointer"
            aria-label="Dismiss auto-classification report"
          >
            <X className="h-4 w-4" />
          </button>
        </div>
      )}

      {workActionBanner && (
        <ArrAlert kind={workActionBanner.kind} className="mb-4">
          <div className="flex items-center justify-between gap-3">
            <span>{workActionBanner.text}</span>
            <button
              type="button"
              onClick={onDismissWorkActionBanner}
              className="text-current opacity-75 hover:opacity-100 cursor-pointer"
            >
              <X className="h-4 w-4" />
            </button>
          </div>
        </ArrAlert>
      )}

      {libraries.length === 0 ? (
        <ArrAlert kind="info">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <span>
              No Root Folders / PMS Libraries configured yet. Add a root folder or jump straight to{' '}
              <strong>Add New</strong> to search Movies, Series, Anime, Music, and Books.
            </span>
            {canWriteCatalog && (
              <div className="flex items-center gap-2">
                <ArrButton variant="success" onClick={() => onNavigate?.('add')}>
                  <Plus className="h-3.5 w-3.5" /> Add New Media
                </ArrButton>
                <ArrButton variant="primary" onClick={() => onSetShowAddLibModal(true)}>
                  <FolderPlus className="h-3.5 w-3.5" /> Add Root Folder
                </ArrButton>
              </div>
            )}
          </div>
        </ArrAlert>
      ) : isWorksLoading ? (
        <div className="py-16 flex flex-col items-center justify-center gap-3 text-[#909293] text-[14px]">
          <Loader2 className="h-7 w-7 animate-spin text-arr-accent" />
          <span>Loading library cover arts...</span>
        </div>
      ) : filteredWorks.length === 0 ? (
        <div className="py-14 text-center space-y-3">
          <div className="text-[15px] text-[#cccccc]">
            {activeCatalogFilterCount > 0
              ? 'No library items match your active filter bubbles.'
              : 'Your library is currently empty.'}
          </div>
          <div className="flex items-center justify-center gap-2">
            {activeCatalogFilterCount > 0 && (
              <ArrButton variant="default" onClick={onClearAllCatalogFilters}>
                <X className="h-4 w-4" /> Clear Active Filters
              </ArrButton>
            )}
            {allWorks.length > 0 && catalogFilterText.trim().length > 0 && canWriteCatalog && (
              <ArrButton variant="primary" onClick={() => onSearchInAddNew?.(catalogFilterText.trim())}>
                <Plus className="h-4 w-4" /> Search &ldquo;{catalogFilterText.trim()}&rdquo; in Add New
              </ArrButton>
            )}
            {canWriteCatalog && !(allWorks.length > 0 && catalogFilterText.trim().length > 0) && (
              <ArrButton variant="success" onClick={() => onNavigate?.('add')}>
                <Plus className="h-4 w-4" /> Add New Media
              </ArrButton>
            )}
          </div>
        </div>
      ) : (
        <>
          {massEditMode && (
            <div className="flex flex-wrap items-center justify-between gap-3 p-3 mb-4 bg-[#212529] border border-arr-card-border rounded-[4px] text-[13px]">
              <div className="flex items-center gap-2">
                <SlidersHorizontal className="h-4 w-4 text-arr-accent" />
                <span className="font-semibold text-[#eeeeee]">Mass Editor Active:</span>
                <ArrLabel kind={selectedWorkIds.size > 0 ? 'primary' : 'default'}>
                  {selectedWorkIds.size} of {filteredWorks.length} Selected
                </ArrLabel>
              </div>
              <div className="flex items-center gap-2">
                <ArrButton
                  variant="default"
                  className="!h-[28px] !px-2.5 !text-[12px]"
                  onClick={() => onSetSelectedWorkIds(new Set(filteredWorks.map((w) => w.id)))}
                >
                  Select All Visible ({filteredWorks.length})
                </ArrButton>
                <ArrButton
                  variant="default"
                  className="!h-[28px] !px-2.5 !text-[12px]"
                  disabled={selectedWorkIds.size === 0}
                  onClick={() => onSetSelectedWorkIds(new Set())}
                >
                  Clear Selection
                </ArrButton>
              </div>
            </div>
          )}

          {viewMode === 'posters' ? (
            <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6 2xl:grid-cols-8 gap-4">
              {filteredWorks.map((w) => {
                const progress = resolveWorkDownloadProgress(w);
                const complete = progress.isComplete;
                const partial = progress.isPartial;
                const isSelected = selectedWorkIds.has(w.id);
                const isMovie = normalizeWorkVertical(w.workKind) === 'movies';
                const libNameLabel = librariesById.get(w.libraryId)?.name;
                const classBadge = resolveClassificationBadge(w);

                const statusTrackClass = complete
                  ? w.status === 'continuing'
                    ? 'bg-[#357ebd]'
                    : 'bg-[#27c24c]'
                  : !w.monitored
                    ? 'bg-[#4b5158]'
                    : progress.targetUnits === 0
                      ? w.status === 'continuing'
                        ? 'bg-[#357ebd]'
                        : 'bg-[#4b5158]'
                      : partial
                        ? 'bg-[#993333]'
                        : 'bg-[#d94343]';

                const statusLabelText = isMovie
                  ? complete
                    ? 'Downloaded'
                    : w.monitored
                      ? 'Missing'
                      : 'Unmonitored'
                  : `${progress.downloadedUnits} / ${progress.targetUnits}`;

                const statusTooltip =
                  !isMovie && progress.targetUnits !== progress.totalUnits
                    ? `${progress.downloadedUnits} / ${progress.targetUnits} monitored (${progress.totalUnits} total)`
                    : undefined;

                return (
                  <div
                    key={w.id}
                    role="button"
                    tabIndex={0}
                    onClick={() => {
                      if (massEditMode) {
                        onSetSelectedWorkIds((prev) => toggleFilterSetValue(prev, w.id));
                      } else {
                        onOpenWork(w.id);
                      }
                    }}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault();
                        if (massEditMode) {
                          onSetSelectedWorkIds((prev) => toggleFilterSetValue(prev, w.id));
                        } else {
                          onOpenWork(w.id);
                        }
                      }
                    }}
                    className={clsx(
                      'bg-arr-card border rounded-[4px] overflow-hidden flex flex-col group relative shadow-[0_4px_14px_rgba(0,0,0,0.45)] transition-all duration-150 cursor-pointer select-none',
                      massEditMode && isSelected
                        ? 'border-arr-accent ring-2 ring-arr-accent'
                        : 'border-arr-card-border hover:border-arr-accent hover:-translate-y-0.5',
                    )}
                  >
                    <div className="relative aspect-[2/3] w-full overflow-hidden bg-[#16181b]">
                      <PosterArtwork
                        posterUrl={w.posterUrl}
                        title={w.title}
                        vertical={w.workKind}
                        year={w.year}
                        className="w-full h-full group-hover:scale-[1.03] transition-transform duration-200"
                      />

                      <div className="absolute inset-x-0 top-0 h-12 bg-gradient-to-b from-black/70 to-transparent pointer-events-none" />

                      {massEditMode ? (
                        <div
                          className="absolute top-2 left-2 z-20 p-1 rounded bg-black/80"
                          onClick={(e) => {
                            e.stopPropagation();
                            onSetSelectedWorkIds((prev) => toggleFilterSetValue(prev, w.id));
                          }}
                        >
                          <input
                            type="checkbox"
                            checked={isSelected}
                            onChange={() => {}}
                            className="rounded border-[#4a5158] bg-[#1e1e1e] text-arr-accent focus:ring-0 cursor-pointer pointer-events-none"
                          />
                        </div>
                      ) : (
                        <div className="absolute top-2 left-2 z-20 flex flex-col items-start gap-1 pointer-events-none">
                          <span className="px-1.5 py-[1px] rounded-[3px] bg-black/70 border border-white/15 text-[10px] font-semibold uppercase tracking-wide text-[#e1e4e8]">
                            {verticalNoun(w.workKind)}
                          </span>
                          {classBadge && (
                            <span
                              className={clsx(
                                'px-1.5 py-[1px] rounded-[3px] text-[10px] font-semibold tracking-wide shadow-sm',
                                classBadge.badgeClass,
                              )}
                            >
                              {classBadge.label}
                            </span>
                          )}
                        </div>
                      )}

                      <button
                        type="button"
                        title={w.monitored ? 'Monitored (click to unmonitor)' : 'Unmonitored (click to monitor)'}
                        disabled={!canWriteCatalog}
                        onClick={(e) => {
                          e.stopPropagation();
                          if (!canWriteCatalog) return;
                          onQuickToggleWorkMonitor(w.id, !w.monitored);
                        }}
                        className={clsx(
                          'absolute top-1.5 right-1.5 z-20 p-1.5 rounded-[3px] bg-black/70 border border-white/10 transition-colors',
                          canWriteCatalog ? 'hover:bg-black/90 cursor-pointer' : 'pointer-events-none',
                        )}
                      >
                        <Bookmark
                          className={clsx(
                            'h-3.5 w-3.5 transition-colors',
                            w.monitored ? 'fill-arr-accent text-arr-accent' : 'text-[#888f96]',
                          )}
                        />
                      </button>
                    </div>

                    <div
                      className={clsx('relative h-[20px] w-full overflow-hidden', statusTrackClass)}
                      title={statusTooltip}
                    >
                      {partial && w.monitored && (
                        <div
                          className="absolute inset-y-0 left-0 bg-[#e69500] transition-all"
                          style={{ width: `${progress.percent}%` }}
                        />
                      )}
                      <div className="relative z-10 h-full px-2 flex items-center justify-center text-[11px] font-bold tracking-tight text-white drop-shadow-[0_1px_1px_rgba(0,0,0,0.65)]">
                        {statusLabelText}
                      </div>
                    </div>

                    <div className="px-2.5 py-2 bg-arr-card flex-1 flex flex-col justify-between">
                      <div
                        className="text-[13px] font-semibold text-[#eeeeee] truncate group-hover:text-arr-accent transition-colors"
                        title={w.title}
                      >
                        {w.title}
                      </div>
                      <div className="flex items-center justify-between gap-1.5 mt-0.5 text-[11px] text-[#8d939a]">
                        <span>{w.year ?? '—'}</span>
                        {libNameLabel && (
                          <span className="truncate max-w-[105px]" title={libNameLabel}>
                            {libNameLabel}
                          </span>
                        )}
                      </div>
                      {classBadge && (
                        <div className="mt-1 pt-1 border-t border-[#343a42]/60">
                          <span
                            className={clsx(
                              'inline-block px-1.5 py-[0.5px] rounded-[3px] text-[10px] font-medium tracking-wide',
                              classBadge.badgeClass,
                            )}
                          >
                            {classBadge.label}
                          </span>
                        </div>
                      )}
                    </div>
                  </div>
                );
              })}
            </div>
          ) : viewMode === 'overview' ? (
            <div className="space-y-3">
              {filteredWorks.map((w) => {
                const progress = resolveWorkDownloadProgress(w);
                const complete = progress.isComplete;
                const isSelected = selectedWorkIds.has(w.id);
                const libNameLabel = librariesById.get(w.libraryId)?.name;
                const classBadge = resolveClassificationBadge(w);
                return (
                  <div
                    key={w.id}
                    onClick={() => {
                      if (massEditMode) {
                        onSetSelectedWorkIds((prev) => toggleFilterSetValue(prev, w.id));
                      } else {
                        onOpenWork(w.id);
                      }
                    }}
                    className={clsx(
                      'bg-[#25282c] border rounded-[4px] p-3.5 flex items-start gap-4 transition-colors cursor-pointer group',
                      massEditMode && isSelected
                        ? 'border-arr-accent bg-arr-accent/10'
                        : 'border-[#363b42] hover:border-arr-accent',
                    )}
                  >
                    {massEditMode && (
                      <div
                        className="pt-1"
                        onClick={(e) => {
                          e.stopPropagation();
                          onSetSelectedWorkIds((prev) => toggleFilterSetValue(prev, w.id));
                        }}
                      >
                        <input
                          type="checkbox"
                          checked={isSelected}
                          onChange={() => {}}
                          className="rounded border-[#4a5158] bg-[#1e1e1e] text-arr-accent focus:ring-0 cursor-pointer pointer-events-none"
                        />
                      </div>
                    )}

                    <PosterArtwork
                      posterUrl={w.posterUrl}
                      title={w.title}
                      vertical={w.workKind}
                      year={w.year}
                      className="w-[84px] sm:w-[96px] aspect-[2/3] rounded-[3px] shrink-0 shadow"
                    />

                    <div className="flex-1 min-w-0 space-y-1.5">
                      <div className="flex flex-wrap items-center justify-between gap-2">
                        <div className="flex items-center gap-2 min-w-0">
                          <button
                            type="button"
                            disabled={!canWriteCatalog}
                            onClick={(e) => {
                              e.stopPropagation();
                              if (!canWriteCatalog) return;
                              onQuickToggleWorkMonitor(w.id, !w.monitored);
                            }}
                            className="cursor-pointer disabled:pointer-events-none"
                            title={w.monitored ? 'Monitored' : 'Unmonitored'}
                          >
                            <Bookmark
                              className={clsx(
                                'h-4 w-4 shrink-0',
                                w.monitored ? 'fill-arr-accent text-arr-accent' : 'text-[#666666]',
                              )}
                            />
                          </button>
                          <span className="text-[17px] font-semibold text-[#eeeeee] group-hover:text-arr-accent truncate">
                            {w.title}
                          </span>
                          {w.year && <span className="text-[14px] text-[#8d939a]">({w.year})</span>}
                        </div>

                        <ArrLabel
                          kind={
                            complete
                              ? 'success'
                              : w.monitored && progress.hasMissingMonitored
                                ? 'danger'
                                : 'default'
                          }
                        >
                          {normalizeWorkVertical(w.workKind) === 'movies'
                            ? complete
                              ? 'Downloaded'
                              : w.monitored
                                ? 'Missing'
                                : 'Unmonitored'
                            : `${progress.downloadedUnits} / ${progress.targetUnits} Files`}
                        </ArrLabel>
                      </div>

                      <div className="flex flex-wrap items-center gap-1.5">
                        <ArrLabel kind="primary">{verticalNoun(w.workKind)}</ArrLabel>
                        {classBadge && (
                          <span
                            className={clsx(
                              'inline-flex items-center px-2 py-0.5 rounded-[3px] text-[11px] font-medium tracking-wide',
                              classBadge.badgeClass,
                            )}
                          >
                            {classBadge.label}
                          </span>
                        )}
                        {libNameLabel && <ArrLabel kind="purple">{libNameLabel}</ArrLabel>}
                        <ArrLabel kind="default">{w.primaryProvider}</ArrLabel>
                        <ArrLabel kind={w.status === 'continuing' ? 'primary' : 'default'}>{w.status}</ArrLabel>
                        <ArrLabel kind="inverse">{w.originalLanguage.toUpperCase()}</ArrLabel>
                        {w.tags?.map((t) => (
                          <ArrLabel key={t} kind="purple">
                            #{t}
                          </ArrLabel>
                        ))}
                      </div>

                      {w.overview ? (
                        <p className="text-[12.5px] text-[#b4bac2] line-clamp-2 leading-relaxed pt-0.5">
                          {w.overview}
                        </p>
                      ) : (
                        <p className="text-[12px] text-[#6d747d] italic pt-0.5">No overview available.</p>
                      )}
                    </div>
                  </div>
                );
              })}
            </div>
          ) : (
            <CatalogWorksTableRegion>
                <thead>
                  <tr>
                    {massEditMode && (
                      <th scope="col" className="w-8">
                        <input
                          type="checkbox"
                          checked={filteredWorks.length > 0 && selectedWorkIds.size === filteredWorks.length}
                          onChange={() => {
                            if (selectedWorkIds.size === filteredWorks.length) {
                              onSetSelectedWorkIds(new Set());
                            } else {
                              onSetSelectedWorkIds(new Set(filteredWorks.map((w) => w.id)));
                            }
                          }}
                          className="rounded border-[#4a5158] bg-[#1e1e1e] text-arr-accent focus:ring-0 cursor-pointer"
                        />
                      </th>
                    )}
                    <th scope="col" className="w-8"></th>
                    <th scope="col" className="w-12">Poster</th>
                    {renderSortableTableHeader('title', 'Title', catalogSortField, catalogSortAsc, onSelectCatalogSort)}
                    <th scope="col">Type</th>
                    <th scope="col">Library</th>
                    {renderSortableTableHeader('year', 'Year', catalogSortField, catalogSortAsc, onSelectCatalogSort)}
                    {renderSortableTableHeader('added', 'Date Added', catalogSortField, catalogSortAsc, onSelectCatalogSort)}
                    <th scope="col">Metadata</th>
                    <th scope="col">Status</th>
                    <th scope="col" className="text-right">Files / Units</th>
                  </tr>
                </thead>
                <tbody>
                  {filteredWorks.map((w) => {
                    const progress = resolveWorkDownloadProgress(w);
                    const complete = progress.isComplete;
                    const isSelected = selectedWorkIds.has(w.id);
                    const libNameLabel = librariesById.get(w.libraryId)?.name ?? `#${w.libraryId}`;
                    const classBadge = resolveClassificationBadge(w);
                    return (
                      <tr key={w.id} className={massEditMode && isSelected ? 'bg-arr-accent/10' : undefined}>
                        {massEditMode && (
                          <td onClick={(e) => e.stopPropagation()}>
                            <input
                              type="checkbox"
                              checked={isSelected}
                              onChange={() => {
                                onSetSelectedWorkIds((prev) => toggleFilterSetValue(prev, w.id));
                              }}
                              className="rounded border-[#4a5158] bg-[#1e1e1e] text-arr-accent focus:ring-0 cursor-pointer"
                            />
                          </td>
                        )}
                        <td>
                          <button
                            type="button"
                            disabled={!canWriteCatalog}
                            onClick={(e) => {
                              e.stopPropagation();
                              if (!canWriteCatalog) return;
                              onQuickToggleWorkMonitor(w.id, !w.monitored);
                            }}
                            className="cursor-pointer disabled:pointer-events-none"
                            title={w.monitored ? 'Monitored' : 'Unmonitored'}
                          >
                            <Bookmark
                              className={clsx(
                                'h-4 w-4',
                                w.monitored ? 'fill-arr-accent text-arr-accent' : 'text-[#666666]',
                              )}
                            />
                          </button>
                        </td>
                        <td>
                          <button
                            type="button"
                            onClick={() => {
                              if (massEditMode) {
                                onSetSelectedWorkIds((prev) => toggleFilterSetValue(prev, w.id));
                              } else {
                                onOpenWork(w.id);
                              }
                            }}
                            className="block cursor-pointer"
                          >
                            <PosterArtwork
                              posterUrl={w.posterUrl}
                              title={w.title}
                              vertical={w.workKind}
                              year={w.year}
                              compact
                              className="w-9 h-[54px] rounded-[2px] shadow-sm"
                            />
                          </button>
                        </td>
                        <td>
                          <button
                            type="button"
                            onClick={() => {
                              if (massEditMode) {
                                onSetSelectedWorkIds((prev) => toggleFilterSetValue(prev, w.id));
                              } else {
                                onOpenWork(w.id);
                              }
                            }}
                            className="font-semibold text-arr-accent hover:underline text-left cursor-pointer"
                          >
                            {w.title}
                          </button>
                        </td>
                        <td>
                          <div className="flex flex-wrap items-center gap-1">
                            <ArrLabel kind="default">{verticalNoun(w.workKind)}</ArrLabel>
                            {classBadge && (
                              <span
                                className={clsx(
                                  'inline-flex items-center px-1.5 py-[0.5px] rounded-[3px] text-[10px] font-medium tracking-wide',
                                  classBadge.badgeClass,
                                )}
                              >
                                {classBadge.label}
                              </span>
                            )}
                          </div>
                        </td>
                        <td className="text-[12px] text-[#b8bec5]">{libNameLabel}</td>
                        <td>{w.year ?? '-'}</td>
                        <td className="text-[12px] text-[#b8bec5] whitespace-nowrap">
                          {w.addedAt ? <time dateTime={w.addedAt}>{formatCatalogAddedDate(w.addedAt)}</time> : '—'}
                        </td>
                        <td>
                          <ArrLabel kind="primary">{w.primaryProvider}</ArrLabel>
                        </td>
                        <td>
                          <ArrLabel kind={w.status === 'continuing' ? 'primary' : 'success'}>{w.status}</ArrLabel>
                        </td>
                        <td className="text-right">
                          <ArrLabel
                            kind={
                              complete
                                ? 'success'
                                : w.monitored && progress.hasMissingMonitored
                                  ? 'danger'
                                  : 'default'
                            }
                          >
                            {progress.downloadedUnits} / {progress.targetUnits}
                          </ArrLabel>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
            </CatalogWorksTableRegion>
          )}

          {massEditMode && selectedWorkIds.size > 0 && (
            <div className="sticky bottom-0 z-40 bg-[#1c1f23] border-t-2 border-arr-accent shadow-[0_-6px_28px_rgba(0,0,0,0.85)] p-4 -mx-5 -mb-5 lg:-mx-6 lg:-mb-6 mt-6 space-y-3">
              <div className="flex flex-wrap items-center justify-between gap-3 pb-2.5 border-b border-[#2d3238]">
                <div className="flex items-center gap-2">
                  <SlidersHorizontal className="h-4 w-4 text-arr-accent" />
                  <span className="text-[14px] font-semibold text-[#eeeeee]">
                    Bulk Edit {selectedWorkIds.size} Selected Work{selectedWorkIds.size === 1 ? '' : 's'}
                  </span>
                </div>
                <div className="flex items-center gap-2">
                  {onBulkScanAndVerify && (
                    <ArrButton
                      variant="primary"
                      className="!h-[32px] !px-3"
                      disabled={isBulkScanAndVerifyPending}
                      busy={isBulkScanAndVerifyPending}
                      onClick={onBulkScanAndVerify}
                    >
                      <Languages className="h-3.5 w-3.5" /> Scan & Check Languages ({selectedWorkIds.size})
                    </ArrButton>
                  )}
                  <ArrButton
                    variant="danger"
                    className="!h-[32px] !px-3"
                    onClick={() => onSetShowDeleteConfirmModal(true)}
                  >
                    <Trash2 className="h-3.5 w-3.5" /> Delete Selected ({selectedWorkIds.size})
                  </ArrButton>
                  <ArrButton
                    variant="success"
                    className="!h-[32px] !px-3.5"
                    disabled={isBulkEditPending}
                    busy={isBulkEditPending}
                    onClick={onBulkEditWorks}
                  >
                    <Check className="h-4 w-4" /> Apply Bulk Changes
                  </ArrButton>
                </div>
              </div>

              <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-3 text-[12px]">
                <div>
                  <label className="block text-[#909293] mb-1 font-medium">Monitored</label>
                  <select
                    value={bulkMonitored}
                    onChange={(e) => onSetBulkMonitored(e.target.value as 'keep' | 'monitored' | 'unmonitored')}
                    className="arr-input !h-[30px] !py-0.5 !text-[12px]"
                  >
                    <option value="keep">Keep</option>
                    <option value="monitored">Monitored</option>
                    <option value="unmonitored">Unmonitored</option>
                  </select>
                </div>

                <div>
                  <label className="block text-[#909293] mb-1 font-medium">Monitor Preset</label>
                  <select
                    value={bulkMonitorPreset}
                    onChange={(e) => onSetBulkMonitorPreset(e.target.value)}
                    className="arr-input !h-[30px] !py-0.5 !text-[12px]"
                  >
                    <option value="keep">Keep</option>
                    <option value="all">All Episodes / Units</option>
                    <option value="future">Future</option>
                    <option value="missing">Missing</option>
                    <option value="existing">Existing</option>
                    <option value="recent">Recent (90d + Future)</option>
                    <option value="pilot">Pilot Episode (S01E01)</option>
                    <option value="firstSeason">First Season</option>
                    <option value="lastSeason">Latest Season</option>
                    <option value="monitorSpecials">Monitor Specials</option>
                    <option value="unmonitorSpecials">Unmonitor Specials</option>
                    <option value="none">None (Unmonitored)</option>
                  </select>
                </div>

                <div>
                  <label className="block text-[#909293] mb-1 font-medium">Quality Profile</label>
                  <select
                    value={bulkQualityProfileId}
                    onChange={(e) => onSetBulkQualityProfileId(e.target.value)}
                    className="arr-input !h-[30px] !py-0.5 !text-[12px]"
                  >
                    <option value="keep">Keep</option>
                    <option value="default">Use Library Default</option>
                    {profiles.map((p) => (
                      <option key={p.id} value={String(p.id)}>
                        {p.name}
                      </option>
                    ))}
                  </select>
                </div>

                <div>
                  <label className="block text-[#909293] mb-1 font-medium">Wanted Language</label>
                  <select
                    aria-label="Bulk Wanted Language"
                    value={bulkLanguagePolicyId}
                    onChange={(e) => onSetBulkLanguagePolicyId?.(e.target.value)}
                    className="arr-input !h-[30px] !py-0.5 !text-[12px]"
                  >
                    <option value="keep">Keep</option>
                    <option value="default">Use Profile Default</option>
                    {languagePolicies.map((p) => (
                      <option key={p.id} value={String(p.id)}>
                        {p.name}
                      </option>
                    ))}
                  </select>
                </div>

                <div>
                  <label className="block text-[#909293] mb-1 font-medium">Original Language</label>
                  <select
                    aria-label="Bulk Original Language"
                    value={bulkOriginalLanguage}
                    onChange={(e) => onSetBulkOriginalLanguage?.(e.target.value)}
                    className="arr-input !h-[30px] !py-0.5 !text-[12px]"
                  >
                    <option value="keep">Keep</option>
                    {COMMON_ISO_LANGUAGES.map((l) => (
                      <option key={l.code} value={l.code}>
                        {l.label}
                      </option>
                    ))}
                  </select>
                </div>

                <div>
                  <label className="block text-[#909293] mb-1 font-medium">Minimum Availability</label>
                  <select
                    value={bulkMinAvailability}
                    onChange={(e) => onSetBulkMinAvailability(e.target.value)}
                    className="arr-input !h-[30px] !py-0.5 !text-[12px]"
                  >
                    <option value="keep">Keep</option>
                    <option value="released">Released</option>
                    <option value="inCinemas">In Cinemas</option>
                    <option value="announced">Announced</option>
                  </select>
                </div>

                <div>
                  <label className="block text-[#909293] mb-1 font-medium">Root Folder / Library</label>
                  <select
                    value={bulkLibraryId}
                    onChange={(e) => onSetBulkLibraryId(e.target.value)}
                    className="arr-input !h-[30px] !py-0.5 !text-[12px]"
                  >
                    <option value="keep">Keep</option>
                    {libraries.map((lib) => (
                      <option key={lib.id} value={String(lib.id)}>
                        {lib.name}
                      </option>
                    ))}
                  </select>
                  {bulkLibraryId !== 'keep' && (
                    <label className="inline-flex items-center gap-1.5 mt-1 text-[11px] text-[#cccccc] cursor-pointer">
                      <input
                        type="checkbox"
                        checked={bulkMoveFiles}
                        onChange={(e) => onSetBulkMoveFiles(e.target.checked)}
                        className="rounded border-[#4a5158] bg-[#1e1e1e] text-arr-accent focus:ring-0 cursor-pointer"
                      />
                      <span>Move Files on Disk</span>
                    </label>
                  )}
                </div>

                <div>
                  <label className="block text-[#909293] mb-1 font-medium">Tags &amp; Mode</label>
                  <div className="flex items-center gap-1">
                    <input
                      value={bulkTags}
                      onChange={(e) => onSetBulkTags(e.target.value)}
                      placeholder="tag1, tag2"
                      className="arr-input !h-[30px] !py-0.5 !text-[12px] flex-1 min-w-0"
                    />
                    <select
                      value={bulkTagMode}
                      onChange={(e) => onSetBulkTagMode(e.target.value as 'add' | 'remove' | 'replace')}
                      className="arr-input !h-[30px] !py-0.5 !text-[11px] !w-20 shrink-0"
                    >
                      <option value="add">Add</option>
                      <option value="remove">Remove</option>
                      <option value="replace">Replace</option>
                    </select>
                  </div>
                </div>
              </div>
            </div>
          )}
        </>
      )}
    </>
  );
}
