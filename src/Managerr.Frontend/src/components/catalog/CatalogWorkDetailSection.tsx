import { Fragment } from 'react';
import { resolveContainerDownloadProgress } from './catalogProgressHelpers';
import {
  AlertTriangle,
  ArrowLeft,
  ArrowUpDown,
  Bookmark,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  Download,
  Loader2,
  RefreshCw,
  Search,
  User,
  X,
} from 'lucide-react';
import clsx from 'clsx';
import { formatBytes } from '../../lib/api';
import { isReadableLatinTitle } from '../../lib/catalogSearch';
import {
  ArrAlert,
  ArrButton,
  ArrLabel,
  ArrModal,
  SmartBreakText,
  compactTraitLabel,
} from '../arr/ArrPrimitives';
import {
  PosterArtwork,
  COMMON_ISO_LANGUAGES,
  formatAgeHours,
  formatLanguagePolicyExplainability,
  resolveClassificationBadge,
  type InteractiveReleaseRow,
  type InteractiveSearchTarget,
  type LanguagePolicySummary,
  type MediaWorkDetailTree,
  type QualityProfileSummary,
} from './catalogTypesAndHelpers';

interface CatalogWorkDetailSectionProps {
  selectedWorkId: number;
  isTreeLoading: boolean;
  isTreeError: boolean;
  treeData?: MediaWorkDetailTree;
  onBackToLibrary: () => void;
  workActionBanner: { kind: 'info' | 'success' | 'warning'; text: string } | null;
  onDismissBanner: () => void;
  canWriteCatalog: boolean;
  canSearchIndexers: boolean;
  onQuickToggleWorkMonitor: (workId: number, monitored: boolean) => void;
  isWorkMonitorPresetPending: boolean;
  onApplyWorkMonitorPreset: (workId: number, monitorPreset: string) => void;
  onUpdateWork: (workId: number, patch: Record<string, unknown>) => void;
  profiles: QualityProfileSummary[];
  languagePolicies?: LanguagePolicySummary[];
  collapsedContainers: Record<number, boolean>;
  onToggleCollapseContainer: (containerId: number) => void;
  isToggleContainerPending: boolean;
  onToggleContainerMonitor: (containerId: number, monitored: boolean) => void;
  searchingContainerId: number | null;
  isAutoSearchContainerPending: boolean;
  onAutoSearchContainer: (workId: number, containerId: number) => void;
  onToggleUnitMonitor: (unitId: number, monitored: boolean) => void;
  searchingUnitId: number | null;
  isAutoSearchUnitPending: boolean;
  onAutoSearchUnit: (workId: number, unitId: number) => void;
  interactiveTarget: InteractiveSearchTarget | null;
  onSetInteractiveTarget: (target: InteractiveSearchTarget | null) => void;
  isInteractiveSearchFetching: boolean;
  interactiveSearchRows: InteractiveReleaseRow[];
  onRefetchInteractiveSearch: () => void;
  interactiveFilterText: string;
  onSetInteractiveFilterText: (v: string) => void;
  interactiveProtocolFilter: 'all' | 'usenet' | 'torrent';
  onSetInteractiveProtocolFilter: (v: 'all' | 'usenet' | 'torrent') => void;
  interactiveHideRejected: boolean;
  onSetInteractiveHideRejected: (v: boolean) => void;
  interactiveSortKey: 'score' | 'age' | 'size' | 'peers';
  onSetInteractiveSortKey: (v: 'score' | 'age' | 'size' | 'peers') => void;
  interactiveSortDir: 'desc' | 'asc';
  onSetInteractiveSortDir: (v: 'desc' | 'asc') => void;
  expandedRejections: Record<string, boolean>;
  onToggleExpandedRejection: (releaseKey: string) => void;
  grabbingGuid: string | null;
  isGrabInteractivePending: boolean;
  onGrabInteractiveRelease: (row: InteractiveReleaseRow, forceGrab: boolean) => void;
}

export function CatalogWorkDetailSection({
  selectedWorkId,
  isTreeLoading,
  isTreeError,
  treeData,
  onBackToLibrary,
  workActionBanner,
  onDismissBanner,
  canWriteCatalog,
  canSearchIndexers,
  onQuickToggleWorkMonitor,
  isWorkMonitorPresetPending,
  onApplyWorkMonitorPreset,
  onUpdateWork,
  profiles,
  languagePolicies = [],
  collapsedContainers,
  onToggleCollapseContainer,
  isToggleContainerPending,
  onToggleContainerMonitor,
  searchingContainerId,
  isAutoSearchContainerPending,
  onAutoSearchContainer,
  onToggleUnitMonitor,
  searchingUnitId,
  isAutoSearchUnitPending,
  onAutoSearchUnit,
  interactiveTarget,
  onSetInteractiveTarget,
  isInteractiveSearchFetching,
  interactiveSearchRows,
  onRefetchInteractiveSearch,
  interactiveFilterText,
  onSetInteractiveFilterText,
  interactiveProtocolFilter,
  onSetInteractiveProtocolFilter,
  interactiveHideRejected,
  onSetInteractiveHideRejected,
  interactiveSortKey,
  onSetInteractiveSortKey,
  interactiveSortDir,
  onSetInteractiveSortDir,
  expandedRejections,
  onToggleExpandedRejection,
  grabbingGuid,
  isGrabInteractivePending,
  onGrabInteractiveRelease,
}: CatalogWorkDetailSectionProps) {
  if (isTreeLoading) {
    return (
      <div className="py-20 flex flex-col items-center justify-center gap-3 text-[#909293] text-[14px]">
        <h1 className="text-[20px] font-normal text-[#eeeeee]">Media Details</h1>
        <Loader2 className="h-8 w-8 animate-spin text-arr-accent" />
        <span>Loading media details...</span>
      </div>
    );
  }

  if (isTreeError || !treeData) {
    return (
      <div className="space-y-3">
        <h1 className="text-[20px] font-normal text-[#eeeeee]">Media Details</h1>
        <ArrAlert kind="danger">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <span>Could not load media item #{selectedWorkId}. It may have been removed.</span>
            <ArrButton variant="default" onClick={onBackToLibrary}>
              <ArrowLeft className="h-4 w-4" /> Back to Library
            </ArrButton>
          </div>
        </ArrAlert>
      </div>
    );
  }

  return (
    <>
      <div className="space-y-5">
        {workActionBanner && (
          <ArrAlert kind={workActionBanner.kind}>
            <div className="flex items-center justify-between gap-3">
              <span>{workActionBanner.text}</span>
              <button
                type="button"
                onClick={onDismissBanner}
                className="text-current opacity-75 hover:opacity-100 cursor-pointer"
              >
                <X className="h-4 w-4" />
              </button>
            </div>
          </ArrAlert>
        )}

        <div className="bg-arr-card border border-arr-card-border rounded-[4px] p-5 flex flex-col sm:flex-row items-start gap-5">
          <PosterArtwork
            posterUrl={treeData.work.posterUrl}
            title={treeData.work.title}
            vertical={treeData.work.workKind}
            year={treeData.work.year}
            className="w-[128px] h-[192px] rounded-[3px] shrink-0 shadow-md"
          />

          <div className="space-y-2 flex-1 min-w-0">
            <div className="flex flex-wrap items-center gap-2.5">
              <button
                type="button"
                disabled={!canWriteCatalog}
                title={treeData.work.monitored ? 'Monitored (click to unmonitor)' : 'Unmonitored (click to monitor)'}
                onClick={() => onQuickToggleWorkMonitor(treeData.work.id, !treeData.work.monitored)}
                className="cursor-pointer disabled:pointer-events-none"
              >
                <Bookmark
                  className={`h-5 w-5 shrink-0 ${
                    treeData.work.monitored ? 'fill-arr-accent text-arr-accent' : 'text-[#666666]'
                  }`}
                />
              </button>
              <h1 className="text-[26px] font-light text-[#eeeeee] leading-tight">
                {treeData.work.title}{' '}
                {treeData.work.year ? (
                  <span className="text-[#909293]">({treeData.work.year})</span>
                ) : null}
              </h1>
            </div>

            {(() => {
              const detailBadge = resolveClassificationBadge(treeData.work);
              return (
                <div className="flex flex-wrap items-center gap-1.5">
                  <ArrLabel kind="primary">{treeData.work.workKind}</ArrLabel>
                  {detailBadge && (
                    <span
                      className={clsx(
                        'inline-flex items-center px-2 py-0.5 rounded-[3px] text-[11px] font-medium tracking-wide',
                        detailBadge.badgeClass,
                      )}
                    >
                      {detailBadge.label}
                    </span>
                  )}
                  <ArrLabel kind="success">Provider: {treeData.work.primaryProvider}</ArrLabel>
                  <ArrLabel kind="default">Lang: {treeData.work.originalLanguage.toUpperCase()}</ArrLabel>
                  <ArrLabel kind="default">Order: {treeData.work.episodeOrderingScheme}</ArrLabel>
                  <ArrLabel kind={treeData.work.status === 'continuing' ? 'primary' : 'default'}>
                    {treeData.work.status}
                  </ArrLabel>
                  {treeData.work.tags?.map((t) => (
                    <ArrLabel key={t} kind="purple">
                      #{t}
                    </ArrLabel>
                  ))}
                  {Object.entries(treeData.work.externalIds).map(([k, v]) => (
                    <ArrLabel key={k} kind="inverse">
                      {k}: {v}
                    </ArrLabel>
                  ))}
                </div>
              );
            })()}

            {treeData.work.altTitles.filter(isReadableLatinTitle).length > 0 && (
              <div className="text-[12px] text-[#888888] truncate">
                Also known as: {treeData.work.altTitles.filter(isReadableLatinTitle).slice(0, 4).join(' • ')}
              </div>
            )}

            {treeData.work.overview && (
              <p className="text-[13px] text-[#c2c4c6] pt-1 max-w-4xl leading-relaxed">
                {treeData.work.overview}
              </p>
            )}

            {canWriteCatalog && (() => {
              const selectedPolicyId = treeData.work.languagePolicyId;
              const activeProfile = profiles.find((p) => p.id === treeData.work.qualityProfileId);
              const resolvedProfilePolicyName =
                activeProfile?.languagePolicy?.name ??
                (languagePolicies.find((lp) => lp.id === activeProfile?.languagePolicyId)?.name ?? 'German First');

              const activePolicy =
                (selectedPolicyId ? languagePolicies.find((lp) => lp.id === selectedPolicyId) : null) ??
                activeProfile?.languagePolicy ??
                languagePolicies.find((lp) => lp.id === activeProfile?.languagePolicyId) ??
                languagePolicies.find((lp) => lp.slug === 'german-first') ??
                languagePolicies[0];

              const explainability = formatLanguagePolicyExplainability(
                activePolicy,
                treeData.work.originalLanguage,
              );

              return (
                <div className="flex flex-wrap items-center gap-3 pt-2 border-t border-arr-card-border">
                  <div className="flex items-center gap-1.5 text-[12px]">
                    <span className="text-[#909293] font-medium">Monitor Preset:</span>
                    <select
                      value=""
                      disabled={isWorkMonitorPresetPending}
                      onChange={(e) => {
                        if (e.target.value) {
                          onApplyWorkMonitorPreset(treeData.work.id, e.target.value);
                        }
                      }}
                      className="arr-input !h-[28px] !py-0.5 !text-[12px] !w-48 cursor-pointer"
                    >
                      <option value="" disabled>
                        {isWorkMonitorPresetPending ? 'Applying...' : '⚡ Apply Monitor Preset...'}
                      </option>
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

                  {treeData.work.workKind === 'movies' && (
                    <div className="flex items-center gap-1.5 text-[12px]">
                      <span className="text-[#909293] font-medium">Min Availability:</span>
                      <select
                        aria-label="Min Availability"
                        value={treeData.work.minimumAvailability ?? 'released'}
                        onChange={(e) =>
                          onUpdateWork(treeData.work.id, { minimumAvailability: e.target.value })
                        }
                        className="arr-input !h-[28px] !py-0.5 !text-[12px] !w-36 cursor-pointer"
                      >
                        <option value="released">Released</option>
                        <option value="inCinemas">In Cinemas</option>
                        <option value="announced">Announced</option>
                      </select>
                    </div>
                  )}

                  <div className="flex items-center gap-1.5 text-[12px]">
                    <span className="text-[#909293] font-medium">Quality Profile:</span>
                    <select
                      aria-label="Quality Profile"
                      value={treeData.work.qualityProfileId ? String(treeData.work.qualityProfileId) : ''}
                      onChange={(e) => {
                        const val = e.target.value;
                        onUpdateWork(treeData.work.id, {
                          qualityProfileId: val ? Number(val) : null,
                          clearQualityProfile: !val,
                        });
                      }}
                      className="arr-input !h-[28px] !py-0.5 !text-[12px] !w-48 cursor-pointer"
                    >
                      <option value="">Library Default</option>
                      {profiles.map((p) => (
                        <option key={p.id} value={String(p.id)}>
                          {p.name}
                        </option>
                      ))}
                    </select>
                  </div>

                  <div className="flex items-center gap-1.5 text-[12px]">
                    <span className="text-[#909293] font-medium">Wanted Language:</span>
                    <select
                      aria-label="Wanted Language"
                      value={treeData.work.languagePolicyId ? String(treeData.work.languagePolicyId) : ''}
                      onChange={(e) => {
                        const val = e.target.value;
                        onUpdateWork(treeData.work.id, {
                          languagePolicyId: val ? Number(val) : null,
                          clearLanguagePolicy: !val,
                        });
                      }}
                      className="arr-input !h-[28px] !py-0.5 !text-[12px] !w-48 cursor-pointer"
                    >
                      <option value="">Profile Default ({resolvedProfilePolicyName})</option>
                      {languagePolicies.map((lp) => (
                        <option key={lp.id} value={String(lp.id)}>
                          {lp.name}
                        </option>
                      ))}
                    </select>
                  </div>

                  <div className="flex items-center gap-1.5 text-[12px]">
                    <span className="text-[#909293] font-medium">Original Language:</span>
                    <select
                      aria-label="Original Language"
                      value={treeData.work.originalLanguage ?? 'en'}
                      onChange={(e) => onUpdateWork(treeData.work.id, { originalLanguage: e.target.value })}
                      className="arr-input !h-[28px] !py-0.5 !text-[12px] !w-44 cursor-pointer"
                    >
                      {COMMON_ISO_LANGUAGES.map((l) => (
                        <option key={l.code} value={l.code}>
                          {l.label}
                        </option>
                      ))}
                      {!COMMON_ISO_LANGUAGES.some(
                        (l) => l.code === (treeData.work.originalLanguage ?? '').toLowerCase(),
                      ) && treeData.work.originalLanguage && (
                        <option value={treeData.work.originalLanguage}>
                          {treeData.work.originalLanguage} (custom)
                        </option>
                      )}
                    </select>
                  </div>

                  <div
                    data-testid="language-policy-explainability"
                    className="w-full text-[11px] text-[#909293] bg-[#1a1c1e] border border-[#2a2d30] rounded-[3px] px-2.5 py-1 flex items-center gap-2 flex-wrap"
                  >
                    <span className="text-arr-accent font-medium">Policy: {activePolicy?.name ?? 'German First'}</span>
                    <span>•</span>
                    <span>
                      <strong className="text-[#c2c4c6] font-medium">Cutoff:</strong> {explainability.cutoffSummary}
                    </span>
                    {explainability.fallbackSummary && (
                      <>
                        <span>•</span>
                        <span>
                          <strong className="text-[#888888] font-medium">Fallback (Upgradeable):</strong> {explainability.fallbackSummary}
                        </span>
                      </>
                    )}
                  </div>
                </div>
              );
            })()}
          </div>
        </div>

        {treeData.containers.map((c) => {
          const work = treeData.work;
          const isSeriesWork =
            work.workKind === 'tv' || work.workKind === 'anime' || c.containerKind.toLowerCase() === 'season';
          const isCollapsed = Boolean(collapsedContainers?.[c.id]);
          const todayIso = new Date().toISOString().slice(0, 10);
          const {
            downloadedCount,
            targetUnitCount,
            totalUnitCount,
            badgeKind,
          } = resolveContainerDownloadProgress(c, treeData.containers, todayIso);

          const seasonSearching = searchingContainerId === c.id && isAutoSearchContainerPending;

          return (
            <div key={c.id} className="bg-arr-card border border-arr-card-border rounded-[4px] overflow-hidden">
              <div className="px-4 py-2.5 bg-arr-heading border-b border-arr-card-border flex flex-wrap items-center justify-between gap-3">
                <div className="flex items-center gap-2.5 min-w-0">
                  <button
                    type="button"
                    disabled={!canWriteCatalog || isToggleContainerPending}
                    onClick={() => onToggleContainerMonitor(c.id, !c.monitored)}
                    title={
                      c.monitored
                        ? `Unmonitor ${c.title} and all its episodes`
                        : `Monitor ${c.title} and all its episodes`
                    }
                    className={clsx(
                      'inline-flex items-center gap-1.5 px-2.5 py-1 text-[11px] font-semibold rounded-[3px] border transition-colors cursor-pointer disabled:opacity-50',
                      c.monitored
                        ? 'bg-[#27c24c]/20 text-[#27c24c] border-[#27c24c]/40 hover:bg-[#27c24c]/30'
                        : 'bg-[#494949]/40 text-[#999999] border-[#555555] hover:bg-[#494949]',
                    )}
                  >
                    <Bookmark
                      className={clsx('h-3.5 w-3.5', c.monitored ? 'fill-current text-[#27c24c]' : 'text-[#777777]')}
                    />
                    <span>{c.monitored ? 'Monitored' : 'Unmonitored'}</span>
                  </button>

                  <button
                    type="button"
                    onClick={() => onToggleCollapseContainer(c.id)}
                    className="inline-flex items-center gap-1.5 text-left group cursor-pointer"
                  >
                    {isCollapsed ? (
                      <ChevronRight className="h-4 w-4 text-[#909293] group-hover:text-arr-accent" />
                    ) : (
                      <ChevronDown className="h-4 w-4 text-[#909293] group-hover:text-arr-accent" />
                    )}
                    <span className="text-[16px] font-normal text-[#eeeeee] group-hover:text-arr-accent transition-colors">
                      {c.title}
                    </span>
                    {!isSeriesWork && (
                      <span className="text-[12px] text-[#909293]">
                        ({c.containerKind} #{c.containerNumber})
                      </span>
                    )}
                  </button>

                  <span
                    title={
                      targetUnitCount !== totalUnitCount
                        ? `${downloadedCount} / ${targetUnitCount} monitored (${totalUnitCount} total)`
                        : undefined
                    }
                  >
                    <ArrLabel kind={badgeKind}>
                      {downloadedCount} / {targetUnitCount}
                    </ArrLabel>
                  </span>
                </div>

                {canSearchIndexers && (isSeriesWork || c.units.length > 1) && (
                  <div className="flex items-center gap-1.5">
                    <ArrButton
                      className="!h-[28px] !px-2.5 !text-[12px]"
                      busy={seasonSearching}
                      onClick={() => onAutoSearchContainer(work.id, c.id)}
                    >
                      <Search className="h-3.5 w-3.5" />
                      <span>{isSeriesWork ? 'Season Search' : 'Search All'}</span>
                    </ArrButton>
                    <ArrButton
                      className="!h-[28px] !px-2.5 !text-[12px]"
                      onClick={() =>
                        onSetInteractiveTarget({
                          label: isSeriesWork
                            ? `${work.title} — ${c.title} (S${String(c.containerNumber).padStart(2, '0')})`
                            : `${work.title} — ${c.title}`,
                          season: isSeriesWork ? c.containerNumber : null,
                          episode: null,
                        })
                      }
                    >
                      <User className="h-3.5 w-3.5" />
                      <span>Interactive</span>
                    </ArrButton>
                  </div>
                )}
              </div>

              {!isCollapsed && (
                <div className="overflow-x-auto">
                  <table className="arr-table">
                    <thead>
                      <tr>
                        <th className="w-8"></th>
                        <th className="w-20">#</th>
                        <th>Title</th>
                        {!isSeriesWork && <th>Kind / Slot</th>}
                        <th>{isSeriesWork ? 'Air Date' : 'Release Date'}</th>
                        <th>Required Audio</th>
                        <th className="text-right">Status</th>
                        {canSearchIndexers && (
                          <th className="w-28 text-right">Actions</th>
                        )}
                      </tr>
                    </thead>
                    <tbody>
                      {c.units.map((u) => {
                        const isEpisodicUnit = isSeriesWork || u.unitKind.toLowerCase() === 'episode';
                        const unitSearching = searchingUnitId === u.id && isAutoSearchUnitPending;
                        const isUnaired =
                          Boolean(u.airOrReleaseDate) && (u.airOrReleaseDate ?? '') > todayIso;
                        const slotVerticalOverride =
                          u.unitKind === 'audiobook_slot'
                            ? 'audiobooks'
                            : u.unitKind === 'ebook_slot'
                              ? 'ebooks'
                              : undefined;
                        const epCode = isEpisodicUnit
                          ? `S${String(c.containerNumber).padStart(2, '0')}E${String(u.unitNumber).padStart(2, '0')}`
                          : u.title;

                        return (
                          <tr key={u.id}>
                            <td>
                              <button
                                type="button"
                                disabled={!canWriteCatalog}
                                onClick={() => onToggleUnitMonitor(u.id, !u.monitored)}
                                title={
                                  u.monitored
                                    ? 'Monitored (click to unmonitor)'
                                    : 'Unmonitored (click to monitor)'
                                }
                                className="cursor-pointer disabled:pointer-events-none"
                              >
                                <Bookmark
                                  className={`h-4 w-4 ${
                                    u.monitored ? 'fill-[#e1e2e3] text-[#e1e2e3]' : 'text-[#666666]'
                                  }`}
                                />
                              </button>
                            </td>
                            <td className="font-mono text-[12px] text-[#cccccc]">
                              {isEpisodicUnit ? String(u.unitNumber).padStart(2, '0') : u.unitNumber}
                              {u.absoluteNumber !== null && u.absoluteNumber !== u.unitNumber && (
                                <span className="ml-1 text-[11px] text-[#7e848a]">({u.absoluteNumber})</span>
                              )}
                            </td>
                            <td className="font-medium text-[#eeeeee]">{u.title}</td>
                            {!isSeriesWork && (
                              <td>
                                <ArrLabel kind="default">{u.unitKind}</ArrLabel>
                              </td>
                            )}
                            <td className="text-[12px] text-[#909293]">{u.airOrReleaseDate ?? '-'}</td>
                            <td>
                              {u.requiredAudioLangs.length > 0 ? (
                                <div className="flex flex-wrap gap-1">
                                  {u.requiredAudioLangs.map((l) => (
                                    <ArrLabel key={l} kind="purple">
                                      {l}
                                    </ArrLabel>
                                  ))}
                                </div>
                              ) : (
                                <span className="text-[#777777]">-</span>
                              )}
                            </td>
                            <td className="text-right">
                              {u.files.length > 0 ? (
                                <div className="inline-flex flex-wrap items-center justify-end gap-1">
                                  <ArrLabel kind="success">
                                    {u.files[0].sourceQualityName ?? u.files[0].qualityName}
                                  </ArrLabel>
                                  {u.files[0].videoCodec?.toUpperCase() === 'AV1' ? (
                                    <ArrLabel kind="purple">AV1</ArrLabel>
                                  ) : u.files[0].videoCodec ? (
                                    <ArrLabel kind="default">{u.files[0].videoCodec}</ArrLabel>
                                  ) : null}
                                  {(() => {
                                    const f = u.files[0];
                                    const lv = f.latestVerification ?? f.languageVerification;
                                    const langs =
                                      lv && lv.detectedAudioLanguages && lv.detectedAudioLanguages.length > 0
                                        ? lv.detectedAudioLanguages
                                        : (f.detectedAudioLangs ?? []);
                                    const subLangs =
                                      lv && lv.detectedSubLanguages && lv.detectedSubLanguages.length > 0
                                        ? lv.detectedSubLanguages
                                        : (f.detectedSubLangs ?? []);
                                    const confPct =
                                      lv && Number.isFinite(lv.overallConfidence)
                                        ? Math.round(lv.overallConfidence * 100)
                                        : null;
                                    const tierHint = lv?.audioMethod
                                      ? ` | Method: ${lv.audioMethod}${confPct !== null ? ` (${confPct}%)` : ''}`
                                      : confPct !== null
                                        ? ` (${confPct}%)`
                                        : '';
                                    const langHint = langs.length > 0 ? ` [Audio: ${langs.join(', ')}]` : '';
                                    const subHint = subLangs.length > 0 ? ` [Sub: ${subLangs.join(', ')}]` : '';

                                    const statusNorm = (f.verifiedStatus ?? '').toLowerCase().trim();

                                    const isProbeError =
                                      statusNorm === 'probeerror' ||
                                      statusNorm === 'corrupt_container' ||
                                      Boolean(lv?.failureCode && lv.failureCode.toLowerCase() !== 'language_mismatch');

                                    const isMismatch =
                                      !isProbeError &&
                                      ['mismatch', 'verifiedfail'].includes(statusNorm);

                                    const isVerifiedPass =
                                      !isProbeError &&
                                      !isMismatch &&
                                      [
                                        'verified',
                                        'verifiedpass',
                                        'verifiedpassautotagged',
                                        'musicpolicypass',
                                      ].includes(statusNorm);

                                    const isQuarantine = statusNorm === 'quarantinereview';

                                    const isUnverified =
                                      !isProbeError &&
                                      !isMismatch &&
                                      !isVerifiedPass &&
                                      !isQuarantine &&
                                      (
                                        [
                                          'unverified',
                                          'importedunverified',
                                          'disk_scan_adopted',
                                          'importedverifiedondisk',
                                        ].includes(statusNorm) ||
                                        Boolean(statusNorm)
                                      );

                                    const cutoffMet =
                                      lv?.satisfiesLanguageCutoff !== undefined && lv?.satisfiesLanguageCutoff !== null
                                        ? lv.satisfiesLanguageCutoff
                                        : u.languageCutoffMet;

                                    const isFallbackUpgradeable = isVerifiedPass && cutoffMet === false;

                                    return (
                                      <>
                                        {langs.length > 0 && (
                                          <ArrLabel
                                            kind="default"
                                            title={`Detected audio languages: ${langs.join(', ')}`}
                                          >
                                            Audio: {langs.join(', ')}
                                          </ArrLabel>
                                        )}
                                        {subLangs.length > 0 && (
                                          <ArrLabel
                                            kind="default"
                                            title={`Detected subtitle languages: ${subLangs.join(', ')}`}
                                          >
                                            Sub: {subLangs.join(', ')}
                                          </ArrLabel>
                                        )}
                                        {isProbeError && (
                                          <ArrLabel
                                            kind="warning"
                                            title={lv?.failureReason || 'Container could not be probed'}
                                          >
                                            Probe Error
                                          </ArrLabel>
                                        )}
                                        {isMismatch && (
                                          <ArrLabel
                                            kind="danger"
                                            title={lv?.failureReason || `Audio mismatch — required audio language missing${langHint}${subHint}${tierHint}`}
                                          >
                                            Mismatch{confPct !== null ? ` ${confPct}%` : ''}
                                          </ArrLabel>
                                        )}
                                        {isFallbackUpgradeable && (
                                          <ArrLabel
                                            kind="info"
                                            title={
                                              lv?.matchedPolicyTierLabel
                                                ? `Matched Tier: ${lv.matchedPolicyTierLabel} (Upgradeable)`
                                                : `Acceptable language fallback, but does not meet wanted cutoff${langHint}${subHint}`
                                            }
                                          >
                                            Fallback (Upgradeable){confPct !== null ? ` ${confPct}%` : ''}
                                          </ArrLabel>
                                        )}
                                        {isVerifiedPass && !isFallbackUpgradeable && (
                                          <ArrLabel
                                            kind="success"
                                            title={`3-Tier LID Verified${langHint}${subHint}${tierHint}`}
                                          >
                                            Verified{confPct !== null ? ` ${confPct}%` : ''}
                                          </ArrLabel>
                                        )}
                                        {isQuarantine && (
                                          <ArrLabel
                                            kind="warning"
                                            title={`Low LID confidence — manual or quarantine review required${langHint}${subHint}${tierHint}`}
                                          >
                                            Low Confidence{confPct !== null ? ` ${confPct}%` : ''}
                                          </ArrLabel>
                                        )}
                                        {isUnverified && (
                                          <ArrLabel
                                            kind="warning"
                                            title="Files not language-checked yet — click 'Scan & Check Languages' to verify"
                                          >
                                            Unverified
                                          </ArrLabel>
                                        )}
                                      </>
                                    );
                                  })()}
                                </div>
                              ) : !u.monitored ? (
                                <ArrLabel kind="default">Unmonitored</ArrLabel>
                              ) : isUnaired ? (
                                <ArrLabel kind="primary">Unaired</ArrLabel>
                              ) : (
                                <ArrLabel kind="danger">Missing</ArrLabel>
                              )}
                            </td>
                            {canSearchIndexers && (
                              <td className="text-right">
                                <div className="inline-flex items-center justify-end gap-1">
                                  <button
                                    type="button"
                                    disabled={unitSearching}
                                    onClick={() => onAutoSearchUnit(work.id, u.id)}
                                    title={`Automatic Search — ${epCode}`}
                                    aria-label={`Automatic Search ${epCode}`}
                                    className="h-[26px] w-[26px] inline-flex items-center justify-center rounded-[3px] border border-transparent hover:border-[#444b52] hover:bg-[#33383e] text-[#c8cacd] hover:text-arr-accent transition-colors cursor-pointer disabled:opacity-50"
                                  >
                                    {unitSearching ? (
                                      <Loader2 className="h-3.5 w-3.5 animate-spin text-arr-accent" />
                                    ) : (
                                      <Search className="h-3.5 w-3.5" />
                                    )}
                                  </button>
                                  <button
                                    type="button"
                                    onClick={() =>
                                      onSetInteractiveTarget({
                                        label: isEpisodicUnit
                                          ? `${work.title} — ${epCode} — ${u.title}`
                                          : `${work.title} — ${u.title}`,
                                        verticalOverride: slotVerticalOverride,
                                        season: isEpisodicUnit ? c.containerNumber : null,
                                        episode: isEpisodicUnit ? u.unitNumber : null,
                                        unitId: u.id,
                                      })
                                    }
                                    title={`Interactive Search — ${epCode}`}
                                    aria-label={`Interactive Search ${epCode}`}
                                    className="h-[26px] w-[26px] inline-flex items-center justify-center rounded-[3px] border border-transparent hover:border-[#444b52] hover:bg-[#33383e] text-[#c8cacd] hover:text-arr-accent transition-colors cursor-pointer"
                                  >
                                    <User className="h-3.5 w-3.5" />
                                  </button>
                                </div>
                              </td>
                            )}
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          );
        })}
      </div>

      {/* ==================== INTERACTIVE RELEASE SEARCH MODAL ==================== */}
      {interactiveTarget && (
        <ArrModal
          title={`Interactive Search — ${interactiveTarget.label}`}
          onClose={() => onSetInteractiveTarget(null)}
          maxWidth="max-w-[96vw] 2xl:max-w-[1580px]"
          bodyClassName="p-0 flex flex-col overflow-hidden min-h-[380px]"
          footer={
            <div className="w-full flex flex-wrap items-center justify-between gap-2">
              <div className="flex flex-wrap items-center gap-2 text-[12px] text-[#909293]">
                {interactiveTarget.season !== null && (
                  <>
                    <span>
                      Scoped to{' '}
                      <strong className="text-[#e1e2e3]">
                        Season {String(interactiveTarget.season).padStart(2, '0')}
                        {interactiveTarget.episode !== null
                          ? ` • Episode ${String(interactiveTarget.episode).padStart(2, '0')}`
                          : ' (Season Packs & Episodes)'}
                      </strong>
                    </span>
                    {interactiveTarget.episode !== null && (
                      <ArrButton
                        className="!h-[26px] !px-2.5 !text-[11px]"
                        onClick={() =>
                          onSetInteractiveTarget({
                            label: `${treeData.work.title} — Season ${interactiveTarget.season}`,
                            season: interactiveTarget.season,
                            episode: null,
                          })
                        }
                      >
                        Broaden to Season {String(interactiveTarget.season).padStart(2, '0')}
                      </ArrButton>
                    )}
                    <ArrButton
                      className="!h-[26px] !px-2.5 !text-[11px]"
                      onClick={() =>
                        onSetInteractiveTarget({
                          label: treeData.work.title,
                          season: null,
                          episode: null,
                        })
                      }
                    >
                      Show All Series Releases
                    </ArrButton>
                  </>
                )}
              </div>
              <ArrButton onClick={() => onSetInteractiveTarget(null)}>Close</ArrButton>
            </div>
          }
        >
          {isInteractiveSearchFetching ? (
            <div className="py-16 px-6 flex items-center justify-center gap-2.5 text-[#cccccc] text-[14px]">
              <Loader2 className="h-5 w-5 animate-spin text-arr-accent" />
              Querying configured Newznab and Torznab indexers for {interactiveTarget.label}...
            </div>
          ) : interactiveSearchRows.length === 0 ? (
            <div className="p-6">
              <ArrAlert kind="warning">
                No releases found across configured indexers for &ldquo;{interactiveTarget.label}&rdquo;. Configure an
                indexer under <strong>Settings → Indexers</strong> or broaden the search scope below.
              </ArrAlert>
            </div>
          ) : (
            (() => {
              const rawRows = interactiveSearchRows;
              const needle = interactiveFilterText.trim().toLowerCase();
              const filteredRows = rawRows
                .filter((r) => {
                  if (interactiveProtocolFilter !== 'all' && r.release.protocol !== interactiveProtocolFilter) {
                    return false;
                  }
                  const rowRej = r.rejections ?? [];
                  const rejected = Boolean(r.isBlocklisted) || r.approved === false || rowRej.length > 0;
                  if (interactiveHideRejected && rejected) {
                    return false;
                  }
                  if (!needle) return true;
                  return (
                    r.release.title.toLowerCase().includes(needle) ||
                    (r.release.indexerName ?? '').toLowerCase().includes(needle) ||
                    (r.qualityName ?? '').toLowerCase().includes(needle) ||
                    r.parsed.detectedLanguages.some((l) => l.toLowerCase().includes(needle)) ||
                    (r.matchedCustomFormats ?? []).some((cf) => cf.toLowerCase().includes(needle))
                  );
                })
                .slice()
                .sort((a, b) => {
                  const mult = interactiveSortDir === 'desc' ? -1 : 1;
                  if (interactiveSortKey === 'age') {
                    return ((a.ageHours ?? 999999) - (b.ageHours ?? 999999)) * mult;
                  }
                  if (interactiveSortKey === 'size') {
                    return ((a.release.sizeBytes ?? 0) - (b.release.sizeBytes ?? 0)) * mult;
                  }
                  if (interactiveSortKey === 'peers') {
                    return ((a.release.seeders ?? -1) - (b.release.seeders ?? -1)) * mult;
                  }
                  return ((a.totalWeightedScore ?? 0) - (b.totalWeightedScore ?? 0)) * mult;
                });

              const approvedTotal = rawRows.filter(
                (r) => !r.isBlocklisted && r.approved !== false && (r.rejections ?? []).length === 0,
              ).length;

              const toggleSort = (key: 'score' | 'age' | 'size' | 'peers') => {
                if (interactiveSortKey === key) {
                  onSetInteractiveSortDir(interactiveSortDir === 'desc' ? 'asc' : 'desc');
                } else {
                  onSetInteractiveSortKey(key);
                  onSetInteractiveSortDir(key === 'age' ? 'asc' : 'desc');
                }
              };

              return (
                <>
                  {/* Filter & Sort Toolbar */}
                  <div className="bg-[#222222] border-b border-arr-card-border px-4 py-2.5 flex flex-wrap items-center justify-between gap-2.5 shrink-0">
                    <div className="flex flex-wrap items-center gap-2">
                      <div className="relative">
                        <Search className="h-3.5 w-3.5 text-[#888888] absolute left-2.5 top-1/2 -translate-y-1/2 pointer-events-none" />
                        <input
                          value={interactiveFilterText}
                          onChange={(e) => onSetInteractiveFilterText(e.target.value)}
                          placeholder="Filter title, codec, group, lang..."
                          className="arr-input !h-[30px] !pl-8 !pr-7 !py-0.5 !text-[12px] w-56 sm:w-64"
                        />
                        {interactiveFilterText && (
                          <button
                            type="button"
                            onClick={() => onSetInteractiveFilterText('')}
                            className="absolute right-2 top-1/2 -translate-y-1/2 text-[#888888] hover:text-[#eeeeee] cursor-pointer"
                          >
                            <X className="h-3.5 w-3.5" />
                          </button>
                        )}
                      </div>

                      <div className="inline-flex rounded-[3px] border border-[#444444] overflow-hidden text-[11px]">
                        {(['all', 'usenet', 'torrent'] as const).map((proto) => (
                          <button
                            key={proto}
                            type="button"
                            onClick={() => onSetInteractiveProtocolFilter(proto)}
                            className={clsx(
                              'px-2.5 py-1 font-medium capitalize transition-colors cursor-pointer',
                              interactiveProtocolFilter === proto
                                ? 'bg-arr-accent text-arr-accent-fg font-semibold'
                                : 'bg-[#2c2c2c] text-[#cccccc] hover:bg-[#383838]',
                            )}
                          >
                            {proto === 'all' ? 'All' : proto === 'usenet' ? 'Usenet' : 'Torrents'}
                          </button>
                        ))}
                      </div>

                      <button
                        type="button"
                        onClick={() => onSetInteractiveHideRejected(!interactiveHideRejected)}
                        className={clsx(
                          'px-2.5 py-1 rounded-[3px] border text-[11px] font-medium transition-colors cursor-pointer',
                          interactiveHideRejected
                            ? 'bg-[#27c24c]/20 border-[#27c24c]/60 text-[#27c24c]'
                            : 'bg-[#2c2c2c] border-[#444444] text-[#cccccc] hover:bg-[#383838]',
                        )}
                      >
                        Approved Only ({approvedTotal})
                      </button>
                    </div>

                    <div className="flex items-center gap-2.5 text-[12px] text-[#909293]">
                      <span>
                        Showing <strong className="text-[#eeeeee]">{filteredRows.length}</strong> of {rawRows.length}{' '}
                        releases
                      </span>
                      <ArrButton
                        className="!h-[28px] !px-2.5 !text-[11px]"
                        onClick={onRefetchInteractiveSearch}
                        title="Re-query indexers"
                      >
                        <RefreshCw className="h-3.5 w-3.5" /> Refresh
                      </ArrButton>
                    </div>
                  </div>

                  {/* Dense Wide Sonarr Release Table */}
                  <div className="flex-1 overflow-auto">
                    <table className="arr-table min-w-[980px] border-separate border-spacing-0">
                      <thead className="sticky top-0 z-20 bg-arr-toolbar shadow-[0_1px_0_#444444]">
                        <tr>
                          <th className="w-[58px] !py-2.5 !px-3">Source</th>
                          <th
                            onClick={() => toggleSort('age')}
                            className="w-[74px] !py-2.5 !px-2.5 cursor-pointer hover:text-arr-accent select-none"
                          >
                            <span className="inline-flex items-center gap-1">
                              Age <ArrowUpDown className="h-3 w-3 opacity-60" />
                            </span>
                          </th>
                          <th className="min-w-[380px] !py-2.5 !px-3">Release Title</th>
                          <th className="w-[130px] !py-2.5 !px-2.5">Indexer</th>
                          <th
                            onClick={() => toggleSort('size')}
                            className="w-[92px] !py-2.5 !px-2.5 text-right cursor-pointer hover:text-arr-accent select-none"
                          >
                            <span className="inline-flex items-center justify-end gap-1">
                              Size <ArrowUpDown className="h-3 w-3 opacity-60" />
                            </span>
                          </th>
                          <th className="w-[108px] !py-2.5 !px-2.5">Languages</th>
                          <th className="w-[120px] !py-2.5 !px-2.5">Quality</th>
                          <th
                            onClick={() => toggleSort('score')}
                            className="w-[210px] !py-2.5 !px-2.5 cursor-pointer hover:text-arr-accent select-none"
                          >
                            <span className="inline-flex items-center gap-1">
                              Score / Formats <ArrowUpDown className="h-3 w-3 opacity-60" />
                            </span>
                          </th>
                          <th className="w-[132px] !py-2.5 !px-3 text-right sticky right-0 z-20 bg-arr-toolbar border-l border-arr-card-border">
                            Actions
                          </th>
                        </tr>
                      </thead>
                      <tbody>
                        {filteredRows.length === 0 ? (
                          <tr>
                            <td colSpan={9} className="!py-10 text-center text-[#909293] text-[13px]">
                              No releases match the active filter criteria.
                            </td>
                          </tr>
                        ) : (
                          filteredRows.map((row) => {
                            const sNum = row.parsed.seasonNumber;
                            const eps = row.parsed.episodeNumbers ?? [];
                            const isPack = Boolean(row.parsed.isFullSeasonPack);
                            const matchBadge =
                              sNum !== null && sNum !== undefined
                                ? isPack
                                  ? `S${String(sNum).padStart(2, '0')} Pack`
                                  : eps.length > 0
                                    ? `S${String(sNum).padStart(2, '0')}E${eps
                                        .slice(0, 2)
                                        .map((n) => String(n).padStart(2, '0'))
                                        .join('-E')}${eps.length > 2 ? '+' : ''}`
                                    : `S${String(sNum).padStart(2, '0')}`
                                : (row.parsed.absoluteEpisodeNumbers ?? []).length > 0
                                  ? `Ep ${(row.parsed.absoluteEpisodeNumbers ?? [])[0]}`
                                  : null;

                            const isBlocklisted = Boolean(row.isBlocklisted);
                            const rejections = [
                              ...(isBlocklisted ? ['Release is on Blocklist'] : []),
                              ...(row.rejections ?? []),
                            ];
                            const isRejected = isBlocklisted || row.approved === false || rejections.length > 0;
                            const releaseKey = row.release.guid ?? row.release.downloadUrl;
                            const isRejectionsExpanded = Boolean(expandedRejections[releaseKey]);
                            const cfs = row.matchedCustomFormats ?? [];
                            const visibleCfs = cfs.slice(0, 2);
                            const extraCfCount = Math.max(0, cfs.length - visibleCfs.length);

                            return (
                              <Fragment key={releaseKey}>
                                <tr className="group/relrow hover:bg-[#323232] transition-colors">
                                  {/* Source */}
                                  <td className="!py-2 !px-3 border-b border-[#303030] whitespace-nowrap">
                                    <ArrLabel kind={row.release.protocol === 'usenet' ? 'usenet' : 'torrent'}>
                                      {row.release.protocol === 'usenet' ? 'nzb' : 'torrent'}
                                    </ArrLabel>
                                  </td>

                                  {/* Age (+ torrent peers pill) */}
                                  <td className="!py-2 !px-2.5 border-b border-[#303030] whitespace-nowrap text-[12px]">
                                    <div className="flex items-center gap-1.5">
                                      <span className="text-[#c2c4c6]">{formatAgeHours(row.ageHours)}</span>
                                      {row.release.protocol === 'torrent' &&
                                        row.release.seeders !== null &&
                                        row.release.seeders !== undefined && (
                                          <span
                                            className="px-1 py-0.5 rounded bg-[#00853d]/25 border border-[#00853d]/50 text-[#3ce37e] font-mono text-[10px]"
                                            title={`${row.release.seeders} seeders / ${row.release.peers ?? 0} peers`}
                                          >
                                            {row.release.seeders}/{row.release.peers ?? 0}
                                          </span>
                                        )}
                                    </div>
                                  </td>

                                  {/* Release Title (Smart Delimiter Wrap — never breaks mid-word!) */}
                                  <td className="!py-2 !px-3 border-b border-[#303030]">
                                    <div className="flex flex-wrap items-center gap-1.5">
                                      <SmartBreakText
                                        text={row.release.title}
                                        className="text-[12.5px] font-medium text-[#eeeeee] leading-snug"
                                      />
                                      {(isPack || eps.length > 1 || interactiveTarget.episode === null) && matchBadge && (
                                        <ArrLabel kind={isPack ? 'purple' : 'inverse'} className="!text-[10px] !py-0">
                                          {matchBadge}
                                        </ArrLabel>
                                      )}
                                      {row.indexerFlags?.map((flag) => (
                                        <ArrLabel key={flag} kind="purple" className="!text-[10px] !py-0">
                                          {flag}
                                        </ArrLabel>
                                      ))}
                                    </div>
                                  </td>

                                  {/* Indexer */}
                                  <td
                                    className="!py-2 !px-2.5 border-b border-[#303030] text-[12px] text-[#b8babc] max-w-[130px] truncate whitespace-nowrap"
                                    title={row.release.indexerName ?? undefined}
                                  >
                                    {row.release.indexerName ?? (row.release.indexerId ? `#${row.release.indexerId}` : '-')}
                                  </td>

                                  {/* Size */}
                                  <td className="!py-2 !px-2.5 border-b border-[#303030] text-right font-mono text-[12px] text-[#d0d2d4] whitespace-nowrap">
                                    {formatBytes(row.release.sizeBytes ?? 0)}
                                  </td>

                                  {/* Languages */}
                                  <td className="!py-2 !px-2.5 border-b border-[#303030] whitespace-nowrap">
                                    <div className="inline-flex items-center gap-1">
                                      {row.parsed.isDualAudio && <ArrLabel kind="purple">DL</ArrLabel>}
                                      {row.parsed.detectedLanguages.length > 0 ? (
                                        row.parsed.detectedLanguages.slice(0, 3).map((l) => (
                                          <ArrLabel key={l} kind="default">
                                            {l.toUpperCase()}
                                          </ArrLabel>
                                        ))
                                      ) : (
                                        <span className="text-[11px] text-[#777777]">—</span>
                                      )}
                                    </div>
                                  </td>

                                  {/* Quality */}
                                  <td className="!py-2 !px-2.5 border-b border-[#303030] whitespace-nowrap">
                                    <ArrLabel kind="primary">
                                      {row.qualityName || `${row.parsed.source ?? 'WEB'} ${row.parsed.resolution ?? ''}`}
                                    </ArrLabel>
                                  </td>

                                  {/* Score & Compact Custom Formats */}
                                  <td
                                    className="!py-2 !px-2.5 border-b border-[#303030]"
                                    title={cfs.length > 0 ? cfs.join('\n') : undefined}
                                  >
                                    <div className="flex flex-wrap items-center gap-1">
                                      {row.totalWeightedScore !== undefined && (
                                        <ArrLabel
                                          kind={
                                            row.totalWeightedScore > 0
                                              ? 'success'
                                              : row.totalWeightedScore < 0
                                                ? 'danger'
                                                : 'default'
                                          }
                                          className="font-mono !text-[10.5px]"
                                        >
                                          {row.totalWeightedScore > 0
                                            ? `+${row.totalWeightedScore}`
                                            : row.totalWeightedScore}
                                        </ArrLabel>
                                      )}
                                      {visibleCfs.map((cf) => (
                                        <span
                                          key={cf}
                                          title={cf}
                                          className="inline-flex items-center px-1.5 py-[1px] rounded-[3px] bg-[#383d42] border border-[#4c535a] text-[#e1e2e3] text-[10px] font-medium whitespace-nowrap"
                                        >
                                          {compactTraitLabel(cf)}
                                        </span>
                                      ))}
                                      {extraCfCount > 0 && (
                                        <span
                                          title={cfs.join('\n')}
                                          className="inline-flex items-center px-1.5 py-[1px] rounded-[3px] bg-arr-card border border-[#4c535a] text-arr-accent text-[10px] font-semibold whitespace-nowrap cursor-help"
                                        >
                                          +{extraCfCount}
                                        </span>
                                      )}
                                      {row.totalWeightedScore === undefined && cfs.length === 0 && (
                                        <span className="text-[11px] text-[#777777]">—</span>
                                      )}
                                    </div>
                                  </td>

                                  {/* Sticky Right Actions (Status Icon + Grab / Force Grab) */}
                                  <td className="!py-2 !px-3 border-b border-[#303030] text-right whitespace-nowrap sticky right-0 z-10 bg-arr-card group-hover/relrow:bg-[#323232] border-l border-arr-card-border transition-colors">
                                    <div className="inline-flex items-center justify-end gap-1.5">
                                      {isRejected ? (
                                        <>
                                          <button
                                            type="button"
                                            onClick={() => onToggleExpandedRejection(releaseKey)}
                                            title={
                                              rejections.length > 0
                                                ? `${rejections.join('\n')}\n(Click to ${isRejectionsExpanded ? 'hide' : 'view'} details)`
                                                : 'Rejected by profile rules'
                                            }
                                            className="inline-flex items-center gap-0.5 h-[26px] px-1.5 rounded-[3px] bg-[#f05050]/20 hover:bg-[#f05050]/35 border border-[#f05050]/50 text-[#ff8080] text-[11px] font-semibold cursor-pointer"
                                          >
                                            <AlertTriangle className="h-3.5 w-3.5" />
                                            {rejections.length > 0 && <span>{rejections.length}</span>}
                                          </button>
                                          <ArrButton
                                            variant="warning"
                                            className="!h-[26px] !px-2 !text-[11px]"
                                            busy={isGrabInteractivePending && grabbingGuid === releaseKey}
                                            disabled={isGrabInteractivePending}
                                            onClick={() => onGrabInteractiveRelease(row, true)}
                                            title="Bypass rejection rules and force grab release"
                                          >
                                            <Download className="h-3 w-3" /> Force
                                          </ArrButton>
                                        </>
                                      ) : (
                                        <>
                                          <span
                                            title={row.decisionKind || 'Approved for grab'}
                                            className="inline-flex items-center justify-center h-[26px] w-[26px] rounded-[3px] text-[#27c24c]"
                                          >
                                            <CheckCircle2 className="h-4 w-4" />
                                          </span>
                                          <ArrButton
                                            variant="primary"
                                            className="!h-[26px] !px-2.5 !text-[11px]"
                                            busy={isGrabInteractivePending && grabbingGuid === releaseKey}
                                            disabled={isGrabInteractivePending}
                                            onClick={() => onGrabInteractiveRelease(row, false)}
                                            title={`Grab release (${row.decisionKind || 'Approved'})`}
                                          >
                                            <Download className="h-3 w-3" /> Grab
                                          </ArrButton>
                                        </>
                                      )}
                                    </div>
                                  </td>
                                </tr>

                                {/* Expandable Full-Width Rejection Breakdown Strip */}
                                {isRejected && isRejectionsExpanded && rejections.length > 0 && (
                                  <tr className="bg-[#231919]">
                                    <td colSpan={9} className="!py-2 !px-4 border-b border-[#3a2424]">
                                      <div className="flex flex-wrap items-center gap-2 text-[11.5px] text-[#f5c6cb]">
                                        <span className="font-semibold uppercase tracking-wide text-[10px] text-[#ff8080]">
                                          Rejection Reasons:
                                        </span>
                                        {rejections.map((rej, rIdx) => (
                                          <span
                                            key={rIdx}
                                            className="px-2 py-0.5 rounded bg-[#a94442]/25 border border-[#a94442]/50 font-mono text-[11px]"
                                          >
                                            {rej}
                                          </span>
                                        ))}
                                      </div>
                                    </td>
                                  </tr>
                                )}
                              </Fragment>
                            );
                          })
                        )}
                      </tbody>
                    </table>
                  </div>
                </>
              );
            })()
          )}
        </ArrModal>
      )}
    </>
  );
}
