import { useMemo, useState } from 'react';
import {
  CheckCircle2,
  ExternalLink,
  FilePlus2,
  Loader2,
  Plus,
  Search,
  X,
} from 'lucide-react';
import clsx from 'clsx';
import { isReadableLatinTitle } from '../../lib/catalogSearch';
import {
  ArrAlert,
  ArrButton,
  ArrLabel,
  ArrModal,
  FieldSet,
  FormGroup,
} from '../arr/ArrPrimitives';
import {
  PosterArtwork,
  QUICK_SAMPLE_SEARCHES,
  COMMON_ISO_LANGUAGES,
  defaultRootPathForVertical,
  pickBestLibraryForCandidate,
  resolveClassificationBadge,
  verticalNoun,
  type LanguagePolicySummary,
  type LibrarySummary,
  type MediaWorkSummary,
  type MetadataProviderDescriptor,
  type MetadataWorkCandidate,
  type QualityProfileSummary,
} from './catalogTypesAndHelpers';

interface CatalogAddMediaSectionProps {
  isAddSection: boolean;
  manualMode: boolean;
  onSetManualMode: (v: boolean) => void;
  addSearchInput: string;
  onSetAddSearchInput: (v: string) => void;
  debouncedAddTerm: string;
  onSetDebouncedAddTerm: (v: string) => void;
  activeAddTab: { id: string; label: string; placeholder: string };
  onSetAddVertical: (v: string) => void;
  isMetadataSearchFetching: boolean;
  isMetadataSearchError: boolean;
  metadataSearchError: Error | null;
  metadataCandidates: MetadataWorkCandidate[];
  existingWorkLookup: (cand: MetadataWorkCandidate) => MediaWorkSummary | undefined;
  onOpenCandidateAddModal: (cand: MetadataWorkCandidate) => void;
  canWriteCatalog: boolean;
  onSetShowAddLibModal: (v: boolean) => void;
  libraries: LibrarySummary[];
  activeLibId: number | null;
  onSetFormLibraryId: (id: number) => void;
  workTitle: string;
  onSetWorkTitle: (v: string) => void;
  workYear: string;
  onSetWorkYear: (v: string) => void;
  workProvider: string;
  onSetWorkProvider: (v: string) => void;
  builtInProviders: MetadataProviderDescriptor[];
  onAddManualWork: () => void;
  isAddManualWorkPending: boolean;
  selectedCandidate: MetadataWorkCandidate | null;
  onCloseCandidateModal: () => void;
  modalSearchOnAdd: boolean;
  onSetModalSearchOnAdd: (v: boolean) => void;
  onAddCandidate: (cand: MetadataWorkCandidate) => void;
  isAddCandidatePending: boolean;
  isAddCandidateError: boolean;
  addCandidateError: Error | null;
  modalLibraryId: string;
  onSetModalLibraryId: (v: string) => void;
  modalMonitorMode: string;
  onSetModalMonitorMode: (v: string) => void;
  modalMinimumAvailability: string;
  onSetModalMinimumAvailability: (v: string) => void;
  modalProfileId: string;
  onSetModalProfileId: (v: string) => void;
  profiles: QualityProfileSummary[];
  languagePolicies?: LanguagePolicySummary[];
  modalLanguagePolicyId?: string;
  onSetModalLanguagePolicyId?: (v: string) => void;
  modalOriginalLanguage?: string;
  onSetModalOriginalLanguage?: (v: string) => void;
  modalAudioPreset: string;
  onSetModalAudioPreset: (v: string) => void;
  modalOrdering: string;
  onSetModalOrdering: (v: string) => void;
  modalTags: string;
  onSetModalTags: (v: string) => void;
}

export function CatalogAddMediaSection({
  isAddSection,
  manualMode,
  onSetManualMode,
  addSearchInput,
  onSetAddSearchInput,
  debouncedAddTerm,
  onSetDebouncedAddTerm,
  activeAddTab,
  onSetAddVertical,
  isMetadataSearchFetching,
  isMetadataSearchError,
  metadataSearchError,
  metadataCandidates,
  existingWorkLookup,
  onOpenCandidateAddModal,
  canWriteCatalog,
  onSetShowAddLibModal,
  libraries,
  activeLibId,
  onSetFormLibraryId,
  workTitle,
  onSetWorkTitle,
  workYear,
  onSetWorkYear,
  workProvider,
  onSetWorkProvider,
  builtInProviders,
  onAddManualWork,
  isAddManualWorkPending,
  selectedCandidate,
  onCloseCandidateModal,
  modalSearchOnAdd,
  onSetModalSearchOnAdd,
  onAddCandidate,
  isAddCandidatePending,
  isAddCandidateError,
  addCandidateError,
  modalLibraryId,
  onSetModalLibraryId,
  modalMonitorMode,
  onSetModalMonitorMode,
  modalMinimumAvailability,
  onSetModalMinimumAvailability,
  modalProfileId,
  onSetModalProfileId,
  profiles,
  languagePolicies = [],
  modalLanguagePolicyId = '',
  onSetModalLanguagePolicyId,
  modalOriginalLanguage = '',
  onSetModalOriginalLanguage,
  modalAudioPreset,
  onSetModalAudioPreset,
  modalOrdering,
  onSetModalOrdering,
  modalTags,
  onSetModalTags,
}: CatalogAddMediaSectionProps) {
  const [sortOrder, setSortOrder] = useState<'popularity' | 'relevance' | 'year' | 'title'>('popularity');

  const sortedCandidates = useMemo(() => {
    if (sortOrder === 'relevance') {
      return metadataCandidates;
    }
    const indexed = metadataCandidates.map((cand, index) => ({ cand, index }));
    indexed.sort((a, b) => {
      if (sortOrder === 'popularity') {
        const diff = (b.cand.popularity ?? 0) - (a.cand.popularity ?? 0);
        return diff !== 0 ? diff : a.index - b.index;
      }
      if (sortOrder === 'year') {
        const diff = (b.cand.year ?? 0) - (a.cand.year ?? 0);
        return diff !== 0 ? diff : a.index - b.index;
      }
      if (sortOrder === 'title') {
        const cmp = a.cand.title.localeCompare(b.cand.title);
        return cmp !== 0 ? cmp : a.index - b.index;
      }
      return a.index - b.index;
    });
    return indexed.map((item) => item.cand);
  }, [metadataCandidates, sortOrder]);

  return (
    <>
      {/* ==================== *ARR ADD NEW SEARCH VIEW ==================== */}
      {isAddSection && !manualMode && (
        <div className="max-w-5xl mx-auto space-y-5">
          {/* Classic 46px Servarr Search Input Group */}
          <div className="flex items-stretch h-[46px] rounded-[4px] overflow-hidden shadow-[0_2px_8px_rgba(0,0,0,0.35)] border border-[#4a5158] focus-within:border-arr-accent transition-colors">
            <div className="w-[46px] bg-arr-card border-r border-[#4a5158] flex items-center justify-center text-[#cccccc] shrink-0">
              {isMetadataSearchFetching ? (
                <Loader2 className="h-5 w-5 animate-spin text-arr-accent" />
              ) : (
                <Search className="h-5 w-5" />
              )}
            </div>
            <input
              autoFocus
              value={addSearchInput}
              onChange={(e) => onSetAddSearchInput(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault();
                  onSetDebouncedAddTerm(addSearchInput.trim());
                }
              }}
              placeholder={activeAddTab.placeholder}
              className="flex-1 bg-arr-input px-4 text-[16px] text-[#eeeeee] placeholder-[#777777] focus:outline-none"
            />
            {addSearchInput && (
              <button
                type="button"
                title="Clear search"
                onClick={() => {
                  onSetAddSearchInput('');
                  onSetDebouncedAddTerm('');
                }}
                className="px-3.5 bg-arr-input text-[#888888] hover:text-[#eeeeee] transition-colors cursor-pointer"
              >
                <X className="h-5 w-5" />
              </button>
            )}
          </div>

          {debouncedAddTerm.length < 2 ? (
            <div className="py-10 text-center space-y-4">
              <div className="text-[16px] font-light text-[#d0d2d4]">
                It&apos;s easy to add a new movie, series, anime, album, or book — just start typing the title above.
              </div>
              <div className="text-[12px] text-[#8d9093]">
                Supports direct external ID lookups:{' '}
                <code className="text-arr-accent">imdb:tt15239678</code>,{' '}
                <code className="text-arr-accent">tmdb:693134</code>,{' '}
                <code className="text-arr-accent">tvdb:371980</code>,{' '}
                <code className="text-arr-accent">anilist:154587</code>,{' '}
                <code className="text-arr-accent">isbn:9780593135204</code>
              </div>
              <div className="flex flex-wrap items-center justify-center gap-2 pt-2">
                {QUICK_SAMPLE_SEARCHES.map((s) => (
                  <button
                    key={s.label}
                    type="button"
                    onClick={() => {
                      onSetAddVertical(s.vertical);
                      onSetAddSearchInput(s.label);
                      onSetDebouncedAddTerm(s.label);
                    }}
                    className="px-2.5 py-1 text-[12px] rounded-[3px] bg-arr-card border border-arr-card-border text-[#cccccc] hover:border-arr-accent hover:text-arr-accent transition-colors cursor-pointer"
                  >
                    {s.label}
                  </button>
                ))}
              </div>
            </div>
          ) : isMetadataSearchError ? (
            <ArrAlert kind="danger">
              Metadata lookup failed: {metadataSearchError?.message ?? 'Request error'}
            </ArrAlert>
          ) : !isMetadataSearchFetching && metadataCandidates.length === 0 ? (
            <div className="py-10 text-center space-y-3 bg-arr-card border border-arr-card-border rounded-[4px] px-4">
              <div className="text-[15px] text-[#cccccc]">
                Could not find any results for &ldquo;{debouncedAddTerm}&rdquo; in{' '}
                <strong>{activeAddTab.label}</strong>.
              </div>
              <div className="text-[12px] text-[#909293]">
                Try switching the vertical filter above to <strong>All Media</strong>, searching by{' '}
                <code>imdb:</code> / <code>tvdb:</code> / <code>tmdb:</code> ID, or adding an unlisted item manually.
              </div>
              {canWriteCatalog && (
                <div className="pt-1">
                  <ArrButton
                    onClick={() => {
                      onSetWorkTitle(debouncedAddTerm);
                      onSetManualMode(true);
                    }}
                  >
                    <FilePlus2 className="h-4 w-4" /> Add &ldquo;{debouncedAddTerm}&rdquo; via Manual Entry
                  </ArrButton>
                </div>
              )}
            </div>
          ) : (
            <div className="space-y-3">
              {sortedCandidates.length > 0 && (
                <div className="flex items-center justify-between text-[12px] text-[#909293] bg-arr-heading border border-arr-card-border rounded-[4px] px-3.5 py-2">
                  <span>
                    <strong className="text-[#eeeeee]">{sortedCandidates.length}</strong> results
                  </span>
                  <label className="inline-flex items-center gap-2">
                    <span className="text-[#cccccc] font-medium">Order by</span>
                    <select
                      aria-label="Order by"
                      value={sortOrder}
                      onChange={(e) =>
                        setSortOrder(e.target.value as 'popularity' | 'relevance' | 'year' | 'title')
                      }
                      className="arr-input !h-[28px] !py-0.5 !px-2 !text-[12px] !w-auto"
                    >
                      <option value="popularity">Popularity</option>
                      <option value="relevance">Relevance</option>
                      <option value="year">Year (Newest)</option>
                      <option value="title">Title (A–Z)</option>
                    </select>
                  </label>
                </div>
              )}

              {sortedCandidates.map((cand) => {
                const inLib = existingWorkLookup(cand);
                const imdb = cand.externalIds.imdb;
                const tmdb = cand.externalIds.tmdb;
                const tvdb = cand.externalIds.tvdb;
                const anilist = cand.externalIds.anilist;
                const openlib = cand.externalIds.openlibrary;
                const candBadge = resolveClassificationBadge(cand);
                const regularSeasonCount = cand.containers.filter((c) => c.containerNumber > 0).length;
                const hasSpecials = cand.containers.some((c) => c.containerNumber === 0);

                return (
                  <div
                    key={`${cand.vertical}-${cand.providerKey}-${cand.providerItemId}`}
                    onClick={() => onOpenCandidateAddModal(cand)}
                    className="bg-arr-card hover:bg-arr-sidebar-active border border-arr-card-border hover:border-arr-accent/60 rounded-[4px] p-4 flex flex-col sm:flex-row gap-4 sm:gap-5 transition-colors cursor-pointer group"
                  >
                    <PosterArtwork
                      posterUrl={cand.posterUrl}
                      title={cand.title}
                      vertical={cand.vertical}
                      className="w-[110px] h-[165px] sm:w-[132px] sm:h-[198px] rounded-[3px] shrink-0 shadow"
                    />

                    <div className="flex-1 min-w-0 flex flex-col justify-between">
                      <div>
                        <div className="flex flex-wrap items-baseline justify-between gap-2">
                          <div className="flex flex-wrap items-baseline gap-2 min-w-0">
                            <span className="text-[22px] sm:text-[24px] font-light text-[#eeeeee] group-hover:text-arr-accent transition-colors leading-snug">
                              {cand.title}
                            </span>
                            {cand.year && (
                              <span className="text-[18px] font-light text-[#909293]">({cand.year})</span>
                            )}
                            {inLib && (
                              <span className="inline-flex items-center gap-1 text-[12px] font-semibold text-[#27c24c] ml-1">
                                <CheckCircle2 className="h-4 w-4" /> Already in Library
                              </span>
                            )}
                          </div>

                          {/* External Catalog Links */}
                          <div
                            className="flex items-center gap-1.5 text-[11px]"
                            onClick={(e) => e.stopPropagation()}
                          >
                            {imdb && (
                              <a
                                href={`https://www.imdb.com/title/${imdb}`}
                                target="_blank"
                                rel="noreferrer"
                                className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-arr-card hover:bg-[#444444] text-[#f5c518] font-semibold"
                              >
                                IMDb <ExternalLink className="h-3 w-3" />
                              </a>
                            )}
                            {tmdb && (
                              <a
                                href={`https://www.themoviedb.org/${cand.vertical === 'movies' ? 'movie' : 'tv'}/${tmdb}`}
                                target="_blank"
                                rel="noreferrer"
                                className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-arr-card hover:bg-[#444444] text-arr-accent font-semibold"
                              >
                                TMDB <ExternalLink className="h-3 w-3" />
                              </a>
                            )}
                            {tvdb && (
                              <a
                                href={`https://thetvdb.com/?tab=series&id=${tvdb}`}
                                target="_blank"
                                rel="noreferrer"
                                className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-arr-card hover:bg-[#444444] text-[#27c24c] font-semibold"
                              >
                                TVDB <ExternalLink className="h-3 w-3" />
                              </a>
                            )}
                            {anilist && (
                              <a
                                href={`https://anilist.co/anime/${anilist}`}
                                target="_blank"
                                rel="noreferrer"
                                className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-arr-card hover:bg-[#444444] text-[#3db4f2] font-semibold"
                              >
                                AniList <ExternalLink className="h-3 w-3" />
                              </a>
                            )}
                            {openlib && (
                              <a
                                href={`https://openlibrary.org/works/${openlib}`}
                                target="_blank"
                                rel="noreferrer"
                                className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-arr-card hover:bg-[#444444] text-[#e1e2e3] font-semibold"
                              >
                                OpenLib <ExternalLink className="h-3 w-3" />
                              </a>
                            )}
                          </div>
                        </div>

                        {/* Servarr Badges Row */}
                        <div className="flex flex-wrap items-center gap-1.5 mt-1.5">
                          <ArrLabel kind="primary">{verticalNoun(cand.vertical)}</ArrLabel>
                          {candBadge && (
                            <span
                              className={clsx(
                                'inline-flex items-center px-2 py-0.5 rounded-[3px] text-[11px] font-medium tracking-wide',
                                candBadge.badgeClass,
                              )}
                            >
                              {candBadge.label}
                            </span>
                          )}
                          <ArrLabel kind="default">{cand.providerKey}</ArrLabel>
                          {cand.originalLanguage && (
                            <ArrLabel kind="inverse">{cand.originalLanguage.toUpperCase()}</ArrLabel>
                          )}
                          <ArrLabel kind={cand.status === 'continuing' ? 'primary' : 'success'}>
                            {cand.status}
                          </ArrLabel>
                          {(cand.vertical === 'tv' || cand.vertical === 'anime') && cand.containers.length > 0 && (
                            <ArrLabel kind="default">
                              {regularSeasonCount > 0
                                ? `${regularSeasonCount} Season${regularSeasonCount === 1 ? '' : 's'}${hasSpecials ? ' + Specials' : ''}`
                                : hasSpecials
                                  ? 'Specials'
                                  : `${cand.containers.length} Season${cand.containers.length === 1 ? '' : 's'}`}
                            </ArrLabel>
                          )}
                          {cand.vertical === 'ebooks' && (
                            <ArrLabel kind="purple">Dual-Slot: Ebook + Audiobook</ArrLabel>
                          )}
                          {cand.externalIds.isbn13 && (
                            <ArrLabel kind="default">ISBN {cand.externalIds.isbn13}</ArrLabel>
                          )}
                        </div>

                        {/* Classification Auto-Tags & Genres */}
                        {((cand.suggestedTags && cand.suggestedTags.length > 0) ||
                          (cand.genres && cand.genres.length > 0)) && (
                          <div className="flex flex-wrap items-center gap-x-3 gap-y-1.5 mt-2 pt-1.5 border-t border-[#343940]/60 text-[11px]">
                            {cand.suggestedTags && cand.suggestedTags.length > 0 && (
                              <div className="flex flex-wrap items-center gap-1">
                                <span className="text-[#888f98] font-medium">Auto-tags:</span>
                                {cand.suggestedTags.map((tag) => (
                                  <span
                                    key={tag}
                                    className="px-1.5 py-[0.5px] rounded bg-[#0f2438] border border-[#0ea5e9]/30 text-[#7dd3fc] text-[10.5px] font-mono"
                                  >
                                    {tag}
                                  </span>
                                ))}
                              </div>
                            )}
                            {cand.genres && cand.genres.length > 0 && (
                              <div className="flex flex-wrap items-center gap-1">
                                <span className="text-[#888f98] font-medium">Genres:</span>
                                {cand.genres.map((g) => (
                                  <span
                                    key={g}
                                    className="px-1.5 py-[0.5px] rounded bg-[#212428] border border-[#353b43] text-[#cfd3d8] text-[10.5px]"
                                  >
                                    {g}
                                  </span>
                                ))}
                              </div>
                            )}
                          </div>
                        )}

                        {cand.altTitles.filter(isReadableLatinTitle).length > 0 && (
                          <div className="text-[12px] text-[#888888] mt-1.5 truncate">
                            Alt: {cand.altTitles.filter(isReadableLatinTitle).slice(0, 3).join(' • ')}
                          </div>
                        )}

                        <p className="text-[13px] text-[#c2c4c6] leading-relaxed line-clamp-3 mt-2">
                          {cand.overview || 'No synopsis provided by upstream catalog.'}
                        </p>
                      </div>

                      <div className="pt-3 mt-2 border-t border-[#363b40] flex items-center justify-between text-[12px] text-[#909293]">
                        <span className="font-mono text-[11px] truncate">
                          {Object.entries(cand.externalIds)
                            .slice(0, 4)
                            .map(([k, v]) => `${k}:${v}`)
                            .join('  ')}
                        </span>
                        <span className="text-arr-accent font-medium shrink-0 ml-3">
                          {inLib ? 'Open in Library →' : `+ Add ${verticalNoun(cand.vertical)} →`}
                        </span>
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
          )}
        </div>
      )}

      {/* ==================== MANUAL / OFFLINE ENTRY FALLBACK ==================== */}
      {isAddSection && manualMode && (
        <FieldSet
          legend="Manual / Unlisted Media Entry"
          action={
            <ArrButton onClick={() => onSetManualMode(false)}>
              <Search className="h-3.5 w-3.5" /> Back to *arr Search
            </ArrButton>
          }
        >
          {libraries.length === 0 ? (
            <ArrAlert kind="warning">
              Create a Root Folder / PMS Library first before adding unlisted manual works.{' '}
              {canWriteCatalog && (
                <button
                  type="button"
                  onClick={() => onSetShowAddLibModal(true)}
                  className="underline font-semibold ml-1 cursor-pointer"
                >
                  Add Root Folder
                </button>
              )}
            </ArrAlert>
          ) : (
            <>
              <FormGroup label="Root Folder / Library">
                <select
                  value={activeLibId ?? ''}
                  onChange={(e) => onSetFormLibraryId(Number(e.target.value))}
                  className="arr-input"
                >
                  {libraries.map((lib) => (
                    <option key={lib.id} value={lib.id}>
                      {lib.name} — {lib.rootFolderPath} ({lib.mediaType})
                    </option>
                  ))}
                </select>
              </FormGroup>

              <FormGroup label="Title" helpText="Custom movie, series, indie album, or local sidecar title.">
                <input
                  value={workTitle}
                  onChange={(e) => onSetWorkTitle(e.target.value)}
                  placeholder="e.g. Home Recording 2026"
                  className="arr-input"
                />
              </FormGroup>

              <FormGroup label="Year">
                <input
                  type="number"
                  value={workYear}
                  onChange={(e) => onSetWorkYear(e.target.value)}
                  className="arr-input"
                />
              </FormGroup>

              <FormGroup label="Metadata Provider">
                <select
                  value={workProvider}
                  onChange={(e) => onSetWorkProvider(e.target.value)}
                  className="arr-input"
                >
                  <option value="auto">Library Waterfall Default</option>
                  {builtInProviders.map((p) => (
                    <option key={p.key} value={p.key}>
                      {p.displayName}
                    </option>
                  ))}
                </select>
              </FormGroup>

              <FormGroup label="">
                <ArrButton
                  variant="success"
                  disabled={!workTitle.trim() || !canWriteCatalog}
                  busy={isAddManualWorkPending}
                  onClick={onAddManualWork}
                >
                  <Plus className="h-4 w-4" /> Add Unlisted Item
                </ArrButton>
              </FormGroup>
            </>
          )}
        </FieldSet>
      )}

      {/* ==================== *ARR ADD WORK MODAL ==================== */}
      {selectedCandidate && (
        <ArrModal
          title={
            <span>
              {selectedCandidate.title}{' '}
              {selectedCandidate.year ? (
                <span className="text-[#909293]">({selectedCandidate.year})</span>
              ) : null}
            </span>
          }
          onClose={onCloseCandidateModal}
          maxWidth="max-w-2xl"
          footer={
            <div className="w-full flex flex-wrap items-center justify-between gap-3">
              <label className="inline-flex items-center gap-2 text-[13px] text-[#e1e2e3] cursor-pointer select-none">
                <input
                  type="checkbox"
                  checked={modalSearchOnAdd}
                  onChange={(e) => onSetModalSearchOnAdd(e.target.checked)}
                  className="h-4 w-4 accent-arr-accent"
                />
                <span>
                  Start search for missing {verticalNoun(selectedCandidate.vertical).toLowerCase()}
                </span>
              </label>

              <div className="flex items-center gap-2 ml-auto">
                <ArrButton onClick={onCloseCandidateModal}>Cancel</ArrButton>
                <ArrButton
                  variant="success"
                  disabled={!canWriteCatalog}
                  busy={isAddCandidatePending}
                  onClick={() => onAddCandidate(selectedCandidate)}
                >
                  <Plus className="h-4 w-4" /> Add {verticalNoun(selectedCandidate.vertical)}
                </ArrButton>
              </div>
            </div>
          }
        >
          <div className="flex items-start gap-4 pb-4 mb-5 border-b border-arr-card-border">
            <PosterArtwork
              posterUrl={selectedCandidate.posterUrl}
              title={selectedCandidate.title}
              vertical={selectedCandidate.vertical}
              className="w-[78px] h-[116px] rounded-[3px] shrink-0"
            />
            <div className="min-w-0 space-y-1.5">
              {(() => {
                const modalCandBadge = resolveClassificationBadge(selectedCandidate);
                return (
                  <div className="flex flex-wrap items-center gap-1.5">
                    <ArrLabel kind="primary">{verticalNoun(selectedCandidate.vertical)}</ArrLabel>
                    {modalCandBadge && (
                      <span
                        className={clsx(
                          'inline-flex items-center px-2 py-0.5 rounded-[3px] text-[11px] font-medium tracking-wide',
                          modalCandBadge.badgeClass,
                        )}
                      >
                        {modalCandBadge.label}
                      </span>
                    )}
                    <ArrLabel kind="default">{selectedCandidate.providerKey}</ArrLabel>
                    <ArrLabel kind="inverse">{selectedCandidate.originalLanguage.toUpperCase()}</ArrLabel>
                    {Object.entries(selectedCandidate.externalIds)
                      .slice(0, 4)
                      .map(([k, v]) => (
                        <ArrLabel key={k} kind="default">
                          {k}:{v}
                        </ArrLabel>
                      ))}
                  </div>
                );
              })()}
              <p className="text-[12.5px] text-[#b8babc] line-clamp-3 leading-relaxed">
                {selectedCandidate.overview || 'No overview available.'}
              </p>
            </div>
          </div>

          {isAddCandidateError && (
            <ArrAlert kind="danger" className="mb-4">
              {addCandidateError?.message ?? 'Failed to add item.'}
            </ArrAlert>
          )}

          <FormGroup
            label="Root Folder"
            helpText="Destination library root where organized hardlinks will be placed."
          >
            {(() => {
              const bestLib = pickBestLibraryForCandidate(selectedCandidate, libraries);
              return (
                <div className="flex items-center gap-2">
                  <select
                    value={modalLibraryId}
                    onChange={(e) => onSetModalLibraryId(e.target.value)}
                    className="arr-input flex-1 min-w-0"
                  >
                    {bestLib && (
                      <option value={String(bestLib.id)}>
                        ★ {bestLib.rootFolderPath} ({bestLib.name} — Recommended)
                      </option>
                    )}
                    {libraries
                      .filter((l) => l.id !== bestLib?.id)
                      .map((lib) => (
                        <option key={lib.id} value={String(lib.id)}>
                          {lib.rootFolderPath} ({lib.name} — {lib.mediaType})
                        </option>
                      ))}
                    <option value="auto-create">
                      + Auto-create {defaultRootPathForVertical(selectedCandidate.vertical, libraries)} (
                      {verticalNoun(selectedCandidate.vertical)} Library)
                    </option>
                  </select>
                  {canWriteCatalog && (
                    <ArrButton
                      type="button"
                      onClick={() => onSetShowAddLibModal(true)}
                      title="Add Root Folder"
                      aria-label="Add Root Folder"
                      className="px-2.5 shrink-0 whitespace-nowrap text-xs h-[34px]"
                    >
                      <Plus className="h-3.5 w-3.5" aria-hidden="true" />
                      <span className="hidden sm:inline">Add Root Folder</span>
                    </ArrButton>
                  )}
                </div>
              );
            })()}
          </FormGroup>

          <FormGroup label="Monitor" helpText="Control which seasons, episodes, or format slots are monitored.">
            <select
              value={modalMonitorMode}
              onChange={(e) => onSetModalMonitorMode(e.target.value)}
              className="arr-input"
            >
              {selectedCandidate.vertical === 'tv' || selectedCandidate.vertical === 'anime' ? (
                <>
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
                </>
              ) : selectedCandidate.vertical === 'ebooks' || selectedCandidate.vertical === 'audiobooks' ? (
                <>
                  <option value="all">Ebook &amp; Audiobook (Dual-Slot)</option>
                  <option value="ebookOnly">eBook Slot Only</option>
                  <option value="audiobookOnly">Audiobook Slot Only</option>
                  <option value="none">None (Unmonitored)</option>
                </>
              ) : (
                <>
                  <option value="all">{verticalNoun(selectedCandidate.vertical)} Only (Monitored)</option>
                  <option value="none">None (Unmonitored)</option>
                </>
              )}
            </select>
          </FormGroup>

          {selectedCandidate.vertical === 'movies' && (
            <FormGroup
              label="Minimum Availability"
              helpText="Determines when a movie is eligible for automatic monitoring and download."
            >
              <select
                value={modalMinimumAvailability}
                onChange={(e) => onSetModalMinimumAvailability(e.target.value)}
                className="arr-input"
              >
                <option value="released">Released</option>
                <option value="inCinemas">In Cinemas</option>
                <option value="announced">Announced</option>
              </select>
            </FormGroup>
          )}

          <FormGroup
            label="Quality Profile"
            helpText="Movies and Series share the 'German First — Best Quality' profile by default."
          >
            {(() => {
              const selectedLib = libraries.find((l) => String(l.id) === modalLibraryId);
              const libDefaultProf = selectedLib?.defaultProfileId
                ? profiles.find((p) => p.id === selectedLib.defaultProfileId)
                : null;
              const candVert = selectedCandidate.vertical.toLowerCase();
              const expectedProfVert = ['movies', 'tv', 'anime'].includes(candVert)
                ? 'video'
                : ['music', 'audiobooks'].includes(candVert)
                  ? 'audio'
                  : ['ebooks', 'comics'].includes(candVert)
                    ? 'books'
                    : candVert;
              const compatibleProfiles = profiles.filter(
                (p) => p.mediaVertical.toLowerCase() === expectedProfVert || p.mediaVertical.toLowerCase() === candVert,
              );
              const otherProfiles = profiles.filter(
                (p) => !compatibleProfiles.some((cp) => cp.id === p.id),
              );
              const defaultLabel = libDefaultProf
                ? `Library Default (${libDefaultProf.name})`
                : candVert === 'movies' || candVert === 'tv'
                  ? 'Library Default (German First — Best Quality)'
                  : 'Library Default Profile';

              return (
                <select
                  value={modalProfileId}
                  onChange={(e) => onSetModalProfileId(e.target.value)}
                  className="arr-input"
                >
                  <option value="">{defaultLabel}</option>
                  {compatibleProfiles.map((p) => (
                    <option key={p.id} value={String(p.id)}>
                      {p.name} (cutoff: {p.qualityCutoff})
                    </option>
                  ))}
                  {otherProfiles.map((p) => (
                    <option key={p.id} value={String(p.id)}>
                      {p.name} ({p.mediaVertical} • cutoff: {p.qualityCutoff})
                    </option>
                  ))}
                </select>
              );
            })()}
          </FormGroup>

          <FormGroup
            label="Wanted Language"
            helpText="Language policy defining required audio, subtitles, and upgrade cutoff."
          >
            {(() => {
              const selectedProf = profiles.find((p) => String(p.id) === modalProfileId);
              const resolvedDefaultName =
                selectedProf?.languagePolicy?.name ??
                (languagePolicies?.find((lp) => lp.id === selectedProf?.languagePolicyId)?.name ?? 'German First');
              return (
                <select
                  aria-label="Wanted Language"
                  value={modalLanguagePolicyId}
                  onChange={(e) => onSetModalLanguagePolicyId?.(e.target.value)}
                  className="arr-input"
                >
                  <option value="">Profile Default ({resolvedDefaultName})</option>
                  {languagePolicies.map((lp) => (
                    <option key={lp.id} value={String(lp.id)}>
                      {lp.name}
                    </option>
                  ))}
                </select>
              );
            })()}
          </FormGroup>

          <FormGroup
            label="Original Language"
            helpText="Original production audio language (e.g. Japanese for anime, German for Tatort)."
          >
            <select
              aria-label="Original Language"
              value={modalOriginalLanguage || selectedCandidate.originalLanguage || 'en'}
              onChange={(e) => onSetModalOriginalLanguage?.(e.target.value)}
              className="arr-input"
            >
              {COMMON_ISO_LANGUAGES.map((l) => (
                <option key={l.code} value={l.code}>
                  {l.label}
                </option>
              ))}
              {!COMMON_ISO_LANGUAGES.some(
                (l) => l.code === (modalOriginalLanguage || selectedCandidate.originalLanguage || '').toLowerCase(),
              ) && (
                <option value={modalOriginalLanguage || selectedCandidate.originalLanguage}>
                  {modalOriginalLanguage || selectedCandidate.originalLanguage} (custom)
                </option>
              )}
            </select>
          </FormGroup>

          <FormGroup
            label="Required Audio"
            helpText="Verified post-download via ffprobe & acoustic duplicate check."
          >
            <select
              value={modalAudioPreset}
              onChange={(e) => onSetModalAudioPreset(e.target.value)}
              className="arr-input"
            >
              <option value="">Original / Any Audio</option>
              <option value="de,en">German + English (Dual Audio / DL)</option>
              <option value="de">German (Deutsch)</option>
              <option value="en">English</option>
              <option value="ja">Japanese (Original)</option>
            </select>
          </FormGroup>

          {(selectedCandidate.vertical === 'tv' || selectedCandidate.vertical === 'anime') && (
            <FormGroup label="Series Type / Ordering">
              <select
                value={modalOrdering}
                onChange={(e) => onSetModalOrdering(e.target.value)}
                className="arr-input"
              >
                <option value="aired">Standard (Aired Season / Episode)</option>
                <option value="absolute">Anime / Absolute Episode Numbering</option>
                <option value="dvd">DVD Ordering</option>
              </select>
            </FormGroup>
          )}

          <FormGroup label="Tags" helpText="Optional comma-separated tags (e.g. keep, 4k, kids).">
            <input
              value={modalTags}
              onChange={(e) => onSetModalTags(e.target.value)}
              placeholder="e.g. keep, german-dl"
              className="arr-input"
            />
            {selectedCandidate.suggestedTags && selectedCandidate.suggestedTags.length > 0 && (
              <div className="mt-1.5 flex flex-wrap items-center gap-1.5 text-[11px]">
                <span className="text-[#888f98] font-medium">Auto-tags:</span>
                {selectedCandidate.suggestedTags.map((tag) => (
                  <button
                    key={tag}
                    type="button"
                    onClick={() => {
                      const current = modalTags
                        .split(',')
                        .map((s) => s.trim())
                        .filter(Boolean);
                      if (!current.includes(tag)) {
                        onSetModalTags([...current, tag].join(', '));
                      }
                    }}
                    className="px-1.5 py-[1px] rounded bg-[#0f2438] border border-[#0ea5e9]/30 text-[#7dd3fc] hover:bg-[#0ea5e9]/20 text-[10.5px] font-mono cursor-pointer transition-colors"
                    title={`Click to add '${tag}' to tags`}
                  >
                    +{tag}
                  </button>
                ))}
              </div>
            )}
          </FormGroup>
        </ArrModal>
      )}
    </>
  );
}
