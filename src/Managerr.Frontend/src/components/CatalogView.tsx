import { useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { matchesCatalogWorkQuery } from '../lib/catalogSearch';
import type { DiskScanReportDto } from './MediaManagementToolsPanel';
import {
  apiFetch,
  type CreatedApiKeyResult,
  type SystemHealthResolveResponse,
} from '../lib/api';
import {
  PageContentBody,
  toggleFilterSetValue,
} from './arr/ArrPrimitives';
import {
  ADD_VERTICAL_TABS,
  compareIsoInstants,
  LIBRARY_VERTICAL_BUBBLES,
  VIEW_MODE_STORAGE_KEY,
  defaultRootPathForVertical,
  pickBestLibraryForCandidate,
  resolveClassificationBadge,
  resolveHealthFilterMeta,
  verticalNoun,
  type AutoSearchResponse,
  type BulkWorkLanguageScanReportDto,
  type CalendarEntry,
  type CatalogAutoClassifyReportDto,
  type CatalogAvailabilityBubble,
  type CatalogMonitorBubble,
  type CatalogSortField,
  type CatalogSubSection,
  type CatalogViewMode,
  type ClassificationBadgeInfo,
  type InteractiveReleaseRow,
  type InteractiveSearchTarget,
  type LanguagePolicySummary,
  type LibrarySummary,
  type MediaRequestSummary,
  type MediaWorkDetailTree,
  type MediaWorkSummary,
  type MetadataWorkCandidate,
  type ProvidersCatalogResponse,
  type QualityProfileSummary,
  type WorkLanguageScanReportDto,
} from './catalog/catalogTypesAndHelpers';
import { CatalogAddMediaSection } from './catalog/CatalogAddMediaSection';
import { CatalogCalendarSection, computeCalendarDateModel } from './catalog/CatalogCalendarSection';
import { CatalogFilterBubbleBar, CatalogLibraryGridSection } from './catalog/CatalogLibraryGridSection';
import {
  CatalogModalsSection,
  CatalogSettingsAndRequestsSection,
  CatalogToolbarSection,
} from './catalog/CatalogSettingsAndRequestsSection';
import { CatalogWorkDetailSection } from './catalog/CatalogWorkDetailSection';
import { resolveWorkDownloadProgress } from './catalog/catalogProgressHelpers';
import {
  DEFAULT_CATALOG_FILTER_STATE,
  buildCatalogSearchWithFilters,
  clearPersistedCatalogFilterState,
  consumeSavedCatalogScrollY,
  normalizeUrlSearch,
  parseCatalogFilterStateFromSearch,
  resolveInitialCatalogFilterState,
  saveCatalogScrollY,
  writePersistedCatalogFilterState,
  type CatalogClassificationFilter,
  type CatalogPersistedFilterState,
} from './catalog/catalogFilterPersistence';

export {
  pickBestLibraryForCandidate,
  resolveClassificationBadge,
  type CatalogSubSection,
  type ClassificationBadgeInfo,
};

interface Props {
  section?: CatalogSubSection;
  resetNonce?: number;
  focusedWorkId?: number | null;
  onClearFocusedWork?: () => void;
  onSelectWorkAndNavigate?: (workId: number) => void;
  initialAddSearchQuery?: string;
  onConsumeInitialAddSearch?: () => void;
  onNavigate?: (target: CatalogSubSection) => void;
  externalFilterRequest?: { text: string; openWorkId?: number | null; nonce: number };
  urlSearch?: string;
  onSyncUrlSearch?: (nextSearch: string) => void;
  urlFilter?: string | null;
  urlHealth?: string | null;
  onClearUrlParams?: () => void;
  onSearchInAddNew?: (query: string) => void;
  canWriteCatalog: boolean;
  canRequest: boolean;
  canApprove: boolean;
  canSearchIndexers?: boolean;
  canReadProfiles?: boolean;
  canSystemAdmin?: boolean;
}

const catalogTitleCollator = new Intl.Collator(undefined, {
  numeric: true,
  sensitivity: 'base',
});

export function CatalogView({
  section = 'catalog',
  resetNonce = 0,
  focusedWorkId = null,
  onClearFocusedWork,
  onSelectWorkAndNavigate,
  initialAddSearchQuery = '',
  onConsumeInitialAddSearch,
  onNavigate,
  externalFilterRequest,
  urlSearch,
  onSyncUrlSearch,
  urlFilter = null,
  urlHealth = null,
  onClearUrlParams,
  onSearchInAddNew,
  canWriteCatalog,
  canRequest,
  canApprove,
  canSearchIndexers = true,
  canReadProfiles = true,
  canSystemAdmin = false,
}: Props) {
  const qc = useQueryClient();
  const isCatalogListRoute = section === 'catalog' && focusedWorkId === null;
  const [initialCatalogFilters] = useState<CatalogPersistedFilterState>(() =>
    isCatalogListRoute
      ? resolveInitialCatalogFilterState(urlSearch, urlFilter)
      : DEFAULT_CATALOG_FILTER_STATE,
  );
  const [selectedWorkId, setSelectedWorkId] = useState<number | null>(focusedWorkId);
  const [viewMode, setViewMode] = useState<CatalogViewMode>(() => {
    if (typeof window !== 'undefined') {
      const saved = window.localStorage?.getItem?.(VIEW_MODE_STORAGE_KEY);
      if (saved === 'posters' || saved === 'overview' || saved === 'table') {
        return saved;
      }
    }
    return 'posters';
  });

  // Multi-select bubble filters for Library Catalog
  const [selectedLibraryIds, setSelectedLibraryIds] = useState<Set<number>>(
    () => new Set(initialCatalogFilters.libraryIds),
  );
  const [selectedVerticals, setSelectedVerticals] = useState<Set<string>>(
    () => new Set(initialCatalogFilters.verticals),
  );
  const [selectedMonitorBubbles, setSelectedMonitorBubbles] = useState<Set<CatalogMonitorBubble>>(
    () => new Set(initialCatalogFilters.monitorBubbles),
  );
  const [selectedAvailabilityBubbles, setSelectedAvailabilityBubbles] = useState<Set<CatalogAvailabilityBubble>>(
    () => new Set(initialCatalogFilters.availabilityBubbles),
  );
  const [catalogFilterText, setCatalogFilterText] = useState<string>(initialCatalogFilters.filterText);
  const [catalogSortField, setCatalogSortField] = useState<CatalogSortField>(initialCatalogFilters.sortField);
  const [catalogSortAsc, setCatalogSortAsc] = useState<boolean>(initialCatalogFilters.sortAsc);
  const [sortAnnouncement, setSortAnnouncement] = useState('');
  const [classificationFilter, setClassificationFilter] = useState<CatalogClassificationFilter>(
    initialCatalogFilters.classificationFilter,
  );
  const [autoClassifyReport, setAutoClassifyReport] = useState<CatalogAutoClassifyReportDto | null>(null);

  const [workActionBanner, setWorkActionBanner] = useState<{ kind: 'info' | 'success' | 'warning'; text: string } | null>(
    null,
  );

  function updateViewMode(next: CatalogViewMode) {
    setViewMode(next);
    if (typeof window !== 'undefined') {
      window.localStorage?.setItem?.(VIEW_MODE_STORAGE_KEY, next);
    }
  }

  // Root folder modal
  const [showAddLibModal, setShowAddLibModal] = useState(false);
  const [libName, setLibName] = useState('');
  const [libType, setLibType] = useState('movies');
  const [libPath, setLibPath] = useState('/media/movies');
  const [libPlexSection, setLibPlexSection] = useState('');

  // *arr Add New live search state
  const [addSearchInput, setAddSearchInput] = useState(initialAddSearchQuery);
  const [debouncedAddTerm, setDebouncedAddTerm] = useState(initialAddSearchQuery);
  const [addVertical, setAddVertical] = useState<string>('all');
  const [manualMode, setManualMode] = useState(false);

  // Add Candidate Modal state
  const [selectedCandidate, setSelectedCandidate] = useState<MetadataWorkCandidate | null>(null);
  const [modalLibraryId, setModalLibraryId] = useState<string>('auto');
  const [modalMonitorMode, setModalMonitorMode] = useState<string>('all');
  const [modalProfileId, setModalProfileId] = useState<string>('');
  const [modalAudioPreset, setModalAudioPreset] = useState<string>('');
  const [modalOrdering, setModalOrdering] = useState<string>('aired');
  const [modalTags, setModalTags] = useState<string>('');
  const [modalSearchOnAdd, setModalSearchOnAdd] = useState<boolean>(true);
  const [modalMinimumAvailability, setModalMinimumAvailability] = useState<string>('released');
  const [modalLanguagePolicyId, setModalLanguagePolicyId] = useState<string>('');
  const [modalOriginalLanguage, setModalOriginalLanguage] = useState<string>('');

  // Issue #28 & #30: Media Management & Mass Edit states
  const [scanReportBanner, setScanReportBanner] = useState<{ libName: string; report: DiskScanReportDto } | null>(null);
  const [renameModalConfig, setRenameModalConfig] = useState<{
    isOpen: boolean;
    title: string;
    previewUrl: string;
    executeUrl: string;
  } | null>(null);
  const [massEditMode, setMassEditMode] = useState<boolean>(false);
  const [selectedWorkIds, setSelectedWorkIds] = useState<Set<number>>(new Set());
  const [bulkMonitored, setBulkMonitored] = useState<'keep' | 'monitored' | 'unmonitored'>('keep');
  const [bulkMonitorPreset, setBulkMonitorPreset] = useState<string>('keep');
  const [bulkQualityProfileId, setBulkQualityProfileId] = useState<string>('keep');
  const [bulkLanguagePolicyId, setBulkLanguagePolicyId] = useState<string>('keep');
  const [bulkOriginalLanguage, setBulkOriginalLanguage] = useState<string>('keep');
  const [bulkMinAvailability, setBulkMinAvailability] = useState<string>('keep');
  const [bulkLibraryId, setBulkLibraryId] = useState<string>('keep');
  const [bulkMoveFiles, setBulkMoveFiles] = useState<boolean>(false);
  const [bulkTags, setBulkTags] = useState<string>('');
  const [bulkTagMode, setBulkTagMode] = useState<'add' | 'remove' | 'replace'>('add');
  const [showDeleteConfirmModal, setShowDeleteConfirmModal] = useState<boolean>(false);
  const [deleteFilesOnDisk, setDeleteFilesOnDisk] = useState<boolean>(false);

  // Manual fallback form state
  const [workTitle, setWorkTitle] = useState('');
  const [workYear, setWorkYear] = useState('2025');
  const [workProvider, setWorkProvider] = useState('auto');

  // Interactive Search modal state
  const [interactiveTarget, setInteractiveTarget] = useState<InteractiveSearchTarget | null>(null);
  const [collapsedContainers, setCollapsedContainers] = useState<Record<number, boolean>>({});
  const [searchingContainerId, setSearchingContainerId] = useState<number | null>(null);
  const [searchingUnitId, setSearchingUnitId] = useState<number | null>(null);
  const [grabbingGuid, setGrabbingGuid] = useState<string | null>(null);
  const [expandedRejections, setExpandedRejections] = useState<Record<string, boolean>>({});
  const [interactiveFilterText, setInteractiveFilterText] = useState<string>('');
  const [interactiveProtocolFilter, setInteractiveProtocolFilter] = useState<'all' | 'usenet' | 'torrent'>('all');
  const [interactiveHideRejected, setInteractiveHideRejected] = useState<boolean>(false);
  const [interactiveSortKey, setInteractiveSortKey] = useState<'score' | 'age' | 'size' | 'peers'>('score');
  const [interactiveSortDir, setInteractiveSortDir] = useState<'desc' | 'asc'>('desc');

  // Requests & Crosswalk state
  const [showRequestModal, setShowRequestModal] = useState(false);
  const [reqTitle, setReqTitle] = useState('');
  const [reqAudioLang, setReqAudioLang] = useState('');
  const [cwProvider, setCwProvider] = useState('anilist');
  const [cwId, setCwId] = useState('154587');

  // Issue #32: Calendar view states
  const [calendarViewMode, setCalendarViewMode] = useState<'month' | 'week' | 'agenda'>('month');
  const [calendarAnchorDate, setCalendarAnchorDate] = useState<Date>(() => new Date());
  const [calSelectedVerticals, setCalSelectedVerticals] = useState<Set<string>>(new Set());
  const [calMonitoredOnly, setCalMonitoredOnly] = useState<boolean>(false);
  const [showIcalModal, setShowIcalModal] = useState<boolean>(false);
  const [icalApiKey, setIcalApiKey] = useState<string>('');
  const [icalIncludeUnmonitored, setIcalIncludeUnmonitored] = useState<boolean>(true);
  const [icalVertical, setIcalVertical] = useState<string>('all');
  const [icalPastDays, setIcalPastDays] = useState<number>(7);
  const [icalFutureDays, setIcalFutureDays] = useState<number>(30);
  const [copiedIcalUrl, setCopiedIcalUrl] = useState<boolean>(false);

  const lastSyncedUrlSearchRef = useRef<string | null>(urlSearch ?? null);
  const prevResetNonceRef = useRef<number>(resetNonce);
  const pendingScrollRestoreRef = useRef<number | null | undefined>(undefined);
  if (pendingScrollRestoreRef.current === undefined) {
    pendingScrollRestoreRef.current = isCatalogListRoute ? consumeSavedCatalogScrollY() : null;
  }

  function openWork(workId: number) {
    if (isCatalogListRoute && selectedWorkId === null && typeof window !== 'undefined') {
      saveCatalogScrollY(window.scrollY);
    }
    setSelectedWorkId(workId);
    onSelectWorkAndNavigate?.(workId);
  }

  useEffect(() => {
    setSelectedWorkId(focusedWorkId ?? null);
    if (focusedWorkId === null) {
      setWorkActionBanner(null);
      setInteractiveTarget(null);
    }
  }, [focusedWorkId]);

  useEffect(() => {
    if (!isCatalogListRoute || urlSearch === undefined) return;
    if (urlSearch === lastSyncedUrlSearchRef.current) return;
    lastSyncedUrlSearchRef.current = urlSearch;
    const parsed = parseCatalogFilterStateFromSearch(urlSearch);
    setSelectedLibraryIds(new Set(parsed.libraryIds));
    setSelectedVerticals(new Set(parsed.verticals));
    setSelectedMonitorBubbles(new Set(parsed.monitorBubbles));
    setSelectedAvailabilityBubbles(new Set(parsed.availabilityBubbles));
    setCatalogFilterText(parsed.filterText);
    setCatalogSortField(parsed.sortField);
    setCatalogSortAsc(parsed.sortAsc);
    setClassificationFilter(parsed.classificationFilter);
    writePersistedCatalogFilterState(parsed);
  }, [isCatalogListRoute, urlSearch]);

  useEffect(() => {
    if (section !== 'catalog' || urlSearch !== undefined) return;
    const f = (urlFilter ?? '').trim();
    const lower = f.toLowerCase();
    if (!f) {
      return;
    }
    if (lower === 'monitored' || lower === 'unmonitored') {
      setSelectedMonitorBubbles(new Set([lower]));
      setCatalogFilterText('');
    } else if (lower === 'missing' || lower === 'complete' || lower === 'continuing') {
      setSelectedAvailabilityBubbles(new Set([lower]));
      setCatalogFilterText('');
    } else if (LIBRARY_VERTICAL_BUBBLES.some((b) => b.id === lower)) {
      setSelectedVerticals(new Set([lower]));
      setCatalogFilterText('');
    } else {
      setCatalogFilterText(f);
    }
  }, [section, urlFilter, urlSearch]);

  useEffect(() => {
    if (!isCatalogListRoute) return;
    const currentState: CatalogPersistedFilterState = {
      libraryIds: Array.from(selectedLibraryIds),
      verticals: Array.from(selectedVerticals),
      monitorBubbles: Array.from(selectedMonitorBubbles),
      availabilityBubbles: Array.from(selectedAvailabilityBubbles),
      filterText: catalogFilterText,
      sortField: catalogSortField,
      sortAsc: catalogSortAsc,
      classificationFilter,
    };
    writePersistedCatalogFilterState(currentState);
    if (urlSearch !== undefined && onSyncUrlSearch) {
      const nextSearch = buildCatalogSearchWithFilters(urlSearch, currentState);
      if (normalizeUrlSearch(nextSearch) !== normalizeUrlSearch(urlSearch)) {
        lastSyncedUrlSearchRef.current = nextSearch;
        onSyncUrlSearch(nextSearch);
      }
    }
  }, [
    isCatalogListRoute,
    selectedLibraryIds,
    selectedVerticals,
    selectedMonitorBubbles,
    selectedAvailabilityBubbles,
    catalogFilterText,
    catalogSortField,
    catalogSortAsc,
    classificationFilter,
    urlSearch,
    onSyncUrlSearch,
  ]);

  useEffect(() => {
    if (externalFilterRequest && externalFilterRequest.nonce > 0) {
      setCatalogFilterText(externalFilterRequest.text);
      setSelectedWorkId(externalFilterRequest.openWorkId ?? null);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [externalFilterRequest?.nonce]);

  useEffect(() => {
    if (resetNonce !== prevResetNonceRef.current) {
      prevResetNonceRef.current = resetNonce;
      setSelectedWorkId(null);
      setWorkActionBanner(null);
      setInteractiveTarget(null);
    }
  }, [resetNonce]);

  useEffect(() => {
    if (section !== 'catalog') {
      setSelectedWorkId(null);
      setInteractiveTarget(null);
    }
  }, [section]);

  useEffect(() => {
    if (initialAddSearchQuery && initialAddSearchQuery.trim().length > 0) {
      setAddSearchInput(initialAddSearchQuery);
      setDebouncedAddTerm(initialAddSearchQuery);
      setManualMode(false);
      onConsumeInitialAddSearch?.();
    }
  }, [initialAddSearchQuery, onConsumeInitialAddSearch]);

  useEffect(() => {
    const timer = window.setTimeout(() => {
      setDebouncedAddTerm(addSearchInput.trim());
    }, 450);
    return () => window.clearTimeout(timer);
  }, [addSearchInput]);

  const librariesQuery = useQuery({
    queryKey: ['libraries'],
    queryFn: () => apiFetch<LibrarySummary[]>('/api/v1/libraries'),
  });

  const [formLibraryId, setFormLibraryId] = useState<number | null>(null);
  const activeLibId = formLibraryId ?? Array.from(selectedLibraryIds)[0] ?? librariesQuery.data?.[0]?.id ?? null;

  const worksQuery = useQuery({
    queryKey: ['catalog-works'],
    queryFn: () => apiFetch<MediaWorkSummary[]>('/api/v1/catalog/works'),
    enabled: section === 'catalog' || section === 'add' || section === 'media-management',
  });

  useEffect(() => {
    if (!isCatalogListRoute || worksQuery.isLoading) return;
    const savedY = pendingScrollRestoreRef.current;
    if (savedY === null || savedY === undefined) return;
    pendingScrollRestoreRef.current = null;
    if (typeof window !== 'undefined' && typeof window.scrollTo === 'function' && savedY > 0) {
      try {
        window.scrollTo({ top: savedY, left: 0, behavior: 'instant' as ScrollBehavior });
      } catch {
        // Ignore jsdom or unsupported scrollTo environments
      }
    }
  }, [isCatalogListRoute, worksQuery.isLoading]);

  const treeQuery = useQuery({
    queryKey: ['catalog-tree', selectedWorkId],
    queryFn: () => apiFetch<MediaWorkDetailTree>(`/api/v1/catalog/works/${selectedWorkId}`),
    enabled: section === 'catalog' && selectedWorkId !== null,
  });

  const metadataSearchQuery = useQuery({
    queryKey: ['metadata-live-search', addVertical, debouncedAddTerm],
    queryFn: () =>
      apiFetch<MetadataWorkCandidate[]>(
        `/api/v1/metadata/search?q=${encodeURIComponent(debouncedAddTerm)}&vertical=${encodeURIComponent(addVertical)}`,
      ),
    enabled: section === 'add' && !manualMode && debouncedAddTerm.length >= 2,
    staleTime: 60_000,
  });

  const profilesQuery = useQuery({
    queryKey: ['profiles'],
    queryFn: () => apiFetch<QualityProfileSummary[]>('/api/v1/profiles'),
    enabled: Boolean(
      canReadProfiles &&
        (section === 'add' || section === 'media-management' || section === 'catalog' || selectedCandidate !== null),
    ),
  });

  const languagePoliciesQuery = useQuery({
    queryKey: ['language-policies'],
    queryFn: () => apiFetch<LanguagePolicySummary[]>('/api/v1/profiles/language-policies'),
    enabled: Boolean(
      (canReadProfiles || canWriteCatalog) &&
        (section === 'add' || section === 'media-management' || section === 'catalog' || selectedCandidate !== null),
    ),
  });

  const interactiveSearchQuery = useQuery({
    queryKey: [
      'work-interactive-search',
      selectedWorkId,
      interactiveTarget?.verticalOverride ?? '',
      interactiveTarget?.season ?? null,
      interactiveTarget?.episode ?? null,
      interactiveTarget?.unitId ?? null,
    ],
    queryFn: () => {
      const w = treeQuery.data!.work;
      const tmdbNum = w.externalIds.tmdb ? Number(w.externalIds.tmdb) : null;
      const tvdbNum = w.externalIds.tvdb ? Number(w.externalIds.tvdb) : null;
      const tvmazeNum = w.externalIds.tvmaze ? Number(w.externalIds.tvmaze) : null;
      return apiFetch<InteractiveReleaseRow[]>('/api/v1/indexers/search', {
        method: 'POST',
        body: JSON.stringify({
          query: w.title,
          mediaVertical: interactiveTarget?.verticalOverride ?? w.workKind,
          imdbId: w.externalIds.imdb ?? null,
          tmdbId: Number.isFinite(tmdbNum) ? tmdbNum : null,
          tvdbId: Number.isFinite(tvdbNum) ? tvdbNum : null,
          tvMazeId: Number.isFinite(tvmazeNum) ? tvmazeNum : null,
          year: w.year ?? null,
          altTitles: w.altTitles ?? [],
          season: interactiveTarget?.season ?? null,
          episode: interactiveTarget?.episode ?? null,
          workId: w.id,
          unitId: interactiveTarget?.unitId ?? null,
          interactive: true,
        }),
      });
    },
    enabled: interactiveTarget !== null && Boolean(treeQuery.data?.work) && canSearchIndexers,
  });

  const { calStartDateIso, calEndDateIso, calRangeLabel, calMonthGridDays, calWeekDays } = useMemo(
    () => computeCalendarDateModel(calendarViewMode, calendarAnchorDate),
    [calendarViewMode, calendarAnchorDate],
  );

  const calendarQuery = useQuery({
    queryKey: ['calendar', calStartDateIso, calEndDateIso],
    queryFn: () =>
      apiFetch<CalendarEntry[]>(`/api/v1/calendar?start=${calStartDateIso}&end=${calEndDateIso}`),
    enabled: section === 'calendar',
  });

  function handleCalendarPrev() {
    setCalendarAnchorDate((prev) => {
      if (calendarViewMode === 'month') {
        return new Date(prev.getFullYear(), prev.getMonth() - 1, 1);
      }
      if (calendarViewMode === 'week') {
        return new Date(prev.getFullYear(), prev.getMonth(), prev.getDate() - 7);
      }
      return new Date(prev.getFullYear(), prev.getMonth(), prev.getDate() - 14);
    });
  }

  function handleCalendarToday() {
    setCalendarAnchorDate(new Date());
  }

  function handleCalendarNext() {
    setCalendarAnchorDate((prev) => {
      if (calendarViewMode === 'month') {
        return new Date(prev.getFullYear(), prev.getMonth() + 1, 1);
      }
      if (calendarViewMode === 'week') {
        return new Date(prev.getFullYear(), prev.getMonth(), prev.getDate() + 7);
      }
      return new Date(prev.getFullYear(), prev.getMonth(), prev.getDate() + 14);
    });
  }

  const createCalendarTokenMutation = useMutation({
    mutationFn: () =>
      apiFetch<CreatedApiKeyResult>('/api/v1/auth/tokens', {
        method: 'POST',
        body: JSON.stringify({
          name: 'Calendar iCal Feed',
          scopes: ['catalog:read'],
        }),
      }),
    onSuccess: (data) => {
      setIcalApiKey(data.rawTokenOnce);
    },
  });

  const filteredCalendarItems = useMemo(() => {
    return (calendarQuery.data ?? []).filter((item) => {
      if (calSelectedVerticals.size > 0) {
        const raw = item.workKind?.toLowerCase() ?? '';
        const norm =
          raw === 'movie'
            ? 'movies'
            : raw === 'series'
              ? 'tv'
              : raw === 'book'
                ? 'ebooks'
                : raw;
        if (!calSelectedVerticals.has(norm)) return false;
      }
      if (calMonitoredOnly && !item.monitored) return false;
      return true;
    });
  }, [calendarQuery.data, calSelectedVerticals, calMonitoredOnly]);

  const calendarItemsByDate = useMemo(() => {
    const map: Record<string, CalendarEntry[]> = {};
    for (const item of filteredCalendarItems) {
      const dateKey = (item.airOrReleaseDate ?? '').slice(0, 10);
      if (!map[dateKey]) map[dateKey] = [];
      map[dateKey].push(item);
    }
    return map;
  }, [filteredCalendarItems]);

  const icalFeedUrl = useMemo(() => {
    const origin = typeof window !== 'undefined' ? window.location.origin : '';
    const keyPart = icalApiKey ? encodeURIComponent(icalApiKey) : '<API_KEY>';
    const unmonitoredPart = icalIncludeUnmonitored ? '&unmonitored=true' : '&unmonitored=false';
    const vertPart = icalVertical !== 'all' ? `&vertical=${encodeURIComponent(icalVertical)}` : '';
    return `${origin}/feed/v1/calendar/managerr.ics?apikey=${keyPart}${unmonitoredPart}${vertPart}&pastDays=${icalPastDays}&futureDays=${icalFutureDays}`;
  }, [icalApiKey, icalIncludeUnmonitored, icalVertical, icalPastDays, icalFutureDays]);

  const webcalFeedUrl = useMemo(() => {
    return icalFeedUrl.replace(/^https?:\/\//i, 'webcal://');
  }, [icalFeedUrl]);

  const requestsQuery = useQuery({
    queryKey: ['media-requests'],
    queryFn: () => apiFetch<MediaRequestSummary[]>('/api/v1/requests'),
    enabled: section === 'requests',
  });

  const providersQuery = useQuery({
    queryKey: ['metadata-providers'],
    queryFn: () => apiFetch<ProvidersCatalogResponse>('/api/v1/metadata/providers'),
    enabled: section === 'metadata' || section === 'add' || section === 'catalog',
  });

  const crosswalkQuery = useQuery({
    queryKey: ['metadata-crosswalk', cwProvider, cwId],
    queryFn: () =>
      apiFetch<Record<string, string>>(
        `/api/v1/metadata/crosswalk?provider=${encodeURIComponent(cwProvider)}&externalId=${encodeURIComponent(cwId)}`,
      ),
    enabled: section === 'metadata' && cwId.trim().length > 0,
  });

  const existingWorkLookup = useMemo(() => {
    const works = worksQuery.data ?? [];
    return (cand: MetadataWorkCandidate): MediaWorkSummary | undefined => {
      return works.find((w) => {
        for (const [k, v] of Object.entries(cand.externalIds)) {
          if (v && w.externalIds[k] && w.externalIds[k].toLowerCase() === v.toLowerCase()) {
            return true;
          }
        }
        return (
          w.workKind.toLowerCase() === cand.vertical.toLowerCase() &&
          w.title.toLowerCase() === cand.title.toLowerCase() &&
          (!cand.year || !w.year || w.year === cand.year)
        );
      });
    };
  }, [worksQuery.data]);

  function openCandidateAddModal(cand: MetadataWorkCandidate) {
    const existing = existingWorkLookup(cand);
    if (existing) {
      setSelectedWorkId(existing.id);
      if (onSelectWorkAndNavigate) {
        onSelectWorkAndNavigate(existing.id);
      } else {
        onNavigate?.('catalog');
      }
      return;
    }

    const libs = librariesQuery.data ?? [];
    const bestLib = pickBestLibraryForCandidate(cand, libs);
    setModalLibraryId(bestLib ? String(bestLib.id) : 'auto-create');
    setModalMonitorMode('all');
    setModalOrdering(
      cand.vertical === 'anime' ||
        cand.classificationKind === 'anime-series' ||
        cand.suggestedTags?.includes('anime-series')
        ? 'absolute'
        : 'aired',
    );
    setModalAudioPreset('');
    setModalLanguagePolicyId('');
    setModalOriginalLanguage(cand.originalLanguage || 'en');
    setModalTags(cand.suggestedTags && cand.suggestedTags.length > 0 ? cand.suggestedTags.join(', ') : '');
    setModalSearchOnAdd(true);
    setModalMinimumAvailability('released');

    const isVideo = ['movies', 'tv', 'anime'].includes(cand.vertical.toLowerCase());
    if (isVideo || bestLib?.defaultProfileId) {
      setModalProfileId('');
    } else {
      const matchingProf = (profilesQuery.data ?? []).find(
        (p) => p.mediaVertical.toLowerCase() === cand.vertical.toLowerCase(),
      );
      setModalProfileId(matchingProf ? String(matchingProf.id) : '');
    }
    setSelectedCandidate(cand);
  }

  const createLibMutation = useMutation({
    mutationFn: () =>
      apiFetch<LibrarySummary>('/api/v1/libraries', {
        method: 'POST',
        body: JSON.stringify({
          name: libName,
          mediaType: libType,
          rootFolderPath: libPath,
          plexSectionId: libPlexSection || null,
        }),
      }),
    onSuccess: () => {
      setLibName('');
      setShowAddLibModal(false);
      qc.invalidateQueries({ queryKey: ['libraries'] });
    },
  });

  const addCandidateMutation = useMutation({
    mutationFn: async (cand: MetadataWorkCandidate) => {
      let targetLibId = Number(modalLibraryId);
      if (modalLibraryId === 'auto-create' || !Number.isFinite(targetLibId) || targetLibId <= 0) {
        const libs = librariesQuery.data ?? [];
        const bestLib = pickBestLibraryForCandidate(cand, libs);
        if (bestLib) {
          targetLibId = bestLib.id;
        } else {
          const autoLib = await apiFetch<LibrarySummary>('/api/v1/libraries', {
            method: 'POST',
            body: JSON.stringify({
              name: `${verticalNoun(cand.vertical)} Library`,
              mediaType: cand.vertical,
              rootFolderPath: defaultRootPathForVertical(cand.vertical, libs),
              plexSectionId: null,
            }),
          });
          targetLibId = autoLib.id;
        }
      }

      const audioLangs = modalAudioPreset
        .split(',')
        .map((s) => s.trim())
        .filter(Boolean);
      const parsedTags = modalTags
        .split(',')
        .map((s) => s.trim())
        .filter(Boolean);
      const candidateTags = parsedTags.length > 0 ? parsedTags : (cand.suggestedTags ?? undefined);

      const created = await apiFetch<MediaWorkDetailTree>('/api/v1/catalog/works', {
        method: 'POST',
        body: JSON.stringify({
          libraryId: targetLibId,
          title: cand.title,
          workKind: cand.vertical,
          primaryProvider: cand.providerKey,
          episodeOrderingScheme: modalOrdering,
          altTitles: cand.altTitles,
          originalLanguage: modalOriginalLanguage || cand.originalLanguage,
          languagePolicyId: modalLanguagePolicyId ? Number(modalLanguagePolicyId) : null,
          year: cand.year,
          status: cand.status,
          overview: cand.overview,
          posterUrl: cand.posterUrl,
          monitored: modalMonitorMode !== 'none',
          monitorMode: modalMonitorMode,
          minimumAvailability: cand.vertical === 'movies' ? modalMinimumAvailability : undefined,
          externalIds: cand.externalIds,
          tags: candidateTags,
          containers: cand.containers,
          dualFormatBookSlots: modalMonitorMode !== 'ebookOnly' && modalMonitorMode !== 'audiobookOnly' ? true : true,
          initialRequiredAudioLangs: audioLangs,
          initialProfileOverrideId: modalProfileId ? Number(modalProfileId) : null,
        }),
      });

      let searchOutcome: AutoSearchResponse | null = null;
      if (modalSearchOnAdd && modalMonitorMode !== 'none' && canSearchIndexers) {
        try {
          searchOutcome = await apiFetch<AutoSearchResponse>(`/api/v1/catalog/works/${created.work.id}/auto-search`, {
            method: 'POST',
          });
        } catch {
          searchOutcome = null;
        }
      }

      return { created, searchOutcome };
    },
    onSuccess: ({ created, searchOutcome }) => {
      setSelectedCandidate(null);
      setSelectedWorkId(created.work.id);
      if (searchOutcome) {
        setWorkActionBanner({
          kind: searchOutcome.grabbed ? 'success' : 'info',
          text: searchOutcome.message,
        });
      } else {
        setWorkActionBanner({
          kind: 'success',
          text: `Added '${created.work.title}' to library.`,
        });
      }
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
      qc.invalidateQueries({ queryKey: ['libraries'] });
      if (onSelectWorkAndNavigate) {
        onSelectWorkAndNavigate(created.work.id);
      } else {
        onNavigate?.('catalog');
      }
    },
  });

  const addManualWorkMutation = useMutation({
    mutationFn: () =>
      apiFetch<MediaWorkDetailTree>('/api/v1/catalog/works', {
        method: 'POST',
        body: JSON.stringify({
          libraryId: activeLibId,
          title: workTitle,
          primaryProvider: workProvider,
          year: Number(workYear) || null,
          dualFormatBookSlots: true,
        }),
      }),
    onSuccess: (created) => {
      setWorkTitle('');
      setSelectedWorkId(created.work.id);
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
      qc.invalidateQueries({ queryKey: ['libraries'] });
      if (onSelectWorkAndNavigate) {
        onSelectWorkAndNavigate(created.work.id);
      } else {
        onNavigate?.('catalog');
      }
    },
  });

  const autoSearchWorkMutation = useMutation({
    mutationFn: (workId: number) =>
      apiFetch<AutoSearchResponse>(`/api/v1/catalog/works/${workId}/auto-search`, {
        method: 'POST',
      }),
    onSuccess: (res) => {
      setWorkActionBanner({
        kind: res.grabbed ? 'success' : 'warning',
        text: res.message,
      });
    },
    onError: (err: Error) => {
      setWorkActionBanner({
        kind: 'warning',
        text: `Automatic search failed: ${err.message}`,
      });
    },
  });

  const autoSearchContainerMutation = useMutation({
    mutationFn: ({ workId, containerId }: { workId: number; containerId: number }) => {
      setSearchingContainerId(containerId);
      return apiFetch<AutoSearchResponse>(`/api/v1/catalog/works/${workId}/containers/${containerId}/auto-search`, {
        method: 'POST',
      });
    },
    onSuccess: (res) => {
      setSearchingContainerId(null);
      setWorkActionBanner({
        kind: res.grabbed ? 'success' : 'warning',
        text: res.message,
      });
    },
    onError: (err: Error) => {
      setSearchingContainerId(null);
      setWorkActionBanner({
        kind: 'warning',
        text: `Season search failed: ${err.message}`,
      });
    },
  });

  const autoSearchUnitMutation = useMutation({
    mutationFn: ({ workId, unitId }: { workId: number; unitId: number }) => {
      setSearchingUnitId(unitId);
      return apiFetch<AutoSearchResponse>(`/api/v1/catalog/works/${workId}/units/${unitId}/auto-search`, {
        method: 'POST',
      });
    },
    onSuccess: (res) => {
      setSearchingUnitId(null);
      setWorkActionBanner({
        kind: res.grabbed ? 'success' : 'warning',
        text: res.message,
      });
    },
    onError: (err: Error) => {
      setSearchingUnitId(null);
      setWorkActionBanner({
        kind: 'warning',
        text: `Episode search failed: ${err.message}`,
      });
    },
  });

  const grabInteractiveMutation = useMutation({
    mutationFn: ({ row, forceGrab = false }: { row: InteractiveReleaseRow; forceGrab?: boolean }) => {
      setGrabbingGuid(row.release.guid ?? row.release.downloadUrl ?? null);
      return apiFetch<{ clientName: string; resolvedCategory: string }>('/api/v1/downloaders/grab', {
        method: 'POST',
        body: JSON.stringify({
          title: row.release.title,
          protocol: row.release.protocol,
          mediaVertical:
            interactiveTarget?.verticalOverride ?? treeQuery.data?.work.workKind ?? row.release.mediaVertical,
          downloadUrlOrMagnet: row.release.magnetUrl ?? row.release.downloadUrl,
          infoHash: row.release.infoHash,
          workId: treeQuery.data?.work.id,
          unitIds: interactiveTarget?.unitId ? [interactiveTarget.unitId] : undefined,
          releaseGuid: row.release.guid ?? null,
          indexerId: row.release.indexerId ?? null,
          indexerName: row.release.indexerName ?? null,
          sizeBytes: row.release.sizeBytes ?? null,
          publishedAt: row.release.publishedAt ?? null,
          forceGrab,
        }),
      });
    },
    onSuccess: (res, vars) => {
      setGrabbingGuid(null);
      setInteractiveTarget(null);
      setWorkActionBanner({
        kind: 'success',
        text: `${vars.forceGrab ? 'Force-grabbed' : 'Grabbed'} '${vars.row.release.title}' → ${res.clientName} (${res.resolvedCategory})`,
      });
    },
    onError: (err: Error) => {
      setGrabbingGuid(null);
      setWorkActionBanner({
        kind: 'warning',
        text: `Grab failed: ${err.message}`,
      });
    },
  });

  const toggleContainerMutation = useMutation({
    mutationFn: ({ containerId, monitored }: { containerId: number; monitored: boolean }) =>
      apiFetch<MediaWorkDetailTree>(`/api/v1/catalog/containers/${containerId}`, {
        method: 'PATCH',
        body: JSON.stringify({ monitored, cascadeUnits: true }),
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['catalog-tree', selectedWorkId] });
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
    },
  });

  const toggleUnitMutation = useMutation({
    mutationFn: ({ unitId, monitored }: { unitId: number; monitored: boolean }) =>
      apiFetch(`/api/v1/catalog/units/${unitId}`, {
        method: 'PATCH',
        body: JSON.stringify({ monitored }),
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['catalog-tree', selectedWorkId] }),
  });

  const refreshMetadataMutation = useMutation({
    mutationFn: ({ workId, provider }: { workId: number; provider?: string | null }) =>
      apiFetch(`/api/v1/catalog/works/${workId}/refresh`, {
        method: 'POST',
        body: JSON.stringify({ switchProvider: provider ?? null }),
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['catalog-tree', selectedWorkId] });
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
      setWorkActionBanner({ kind: 'info', text: 'Refreshed metadata from provider waterfall.' });
    },
  });

  const submitRequestMutation = useMutation({
    mutationFn: () =>
      apiFetch<MediaRequestSummary>('/api/v1/requests', {
        method: 'POST',
        body: JSON.stringify({
          libraryId: activeLibId,
          title: reqTitle,
          requestedAudioLang: reqAudioLang || null,
        }),
      }),
    onSuccess: () => {
      setReqTitle('');
      setShowRequestModal(false);
      qc.invalidateQueries({ queryKey: ['media-requests'] });
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
    },
  });

  const decideRequestMutation = useMutation({
    mutationFn: ({ id, approve }: { id: number; approve: boolean }) =>
      apiFetch(`/api/v1/requests/${id}/decide`, {
        method: 'POST',
        body: JSON.stringify({ approve }),
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['media-requests'] });
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
    },
  });

  const libraryScanMutation = useMutation({
    mutationFn: (libId: number) =>
      apiFetch<DiskScanReportDto>(`/api/v1/libraries/${libId}/scan`, {
        method: 'POST',
      }),
    onSuccess: (report, libId) => {
      const lib = (librariesQuery.data ?? []).find((l) => l.id === libId);
      const libName = lib?.name ?? `Library #${libId}`;
      setScanReportBanner({ libName, report });
      qc.invalidateQueries({ queryKey: ['libraries'] });
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
      qc.invalidateQueries({ queryKey: ['unmapped-files'] });
    },
    onError: (err: Error) => {
      setWorkActionBanner({
        kind: 'warning',
        text: `Library disk scan failed: ${err.message}`,
      });
    },
  });

  const workScanMutation = useMutation({
    mutationFn: (workId: number) =>
      apiFetch<DiskScanReportDto>(`/api/v1/catalog/works/${workId}/scan`, {
        method: 'POST',
      }),
    onSuccess: (report) => {
      const workTitleStr = treeQuery.data?.work?.title ?? 'Media';
      setWorkActionBanner({
        kind: 'success',
        text: `Scan finished for '${workTitleStr}': ${report.scannedFilesCount} files scanned, ${report.autoAdoptedCount} adopted, ${report.missingRemovedCount} missing removed.`,
      });
      qc.invalidateQueries({ queryKey: ['catalog-tree', selectedWorkId] });
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
    },
    onError: (err: Error) => {
      setWorkActionBanner({
        kind: 'warning',
        text: `Work disk scan failed: ${err.message}`,
      });
    },
  });

  const workScanAndVerifyMutation = useMutation({
    mutationFn: (workId: number) =>
      apiFetch<WorkLanguageScanReportDto>(`/api/v1/catalog/works/${workId}/scan-and-verify`, {
        method: 'POST',
      }),
    onSuccess: (res, workId) => {
      setWorkActionBanner({
        kind: 'success',
        text: `Scanned '${res.workTitle}': ${res.filesLanguageCheckedCount} file(s) language-checked (${res.verifiedPassCount} verified, ${res.languageMismatchCount} mismatch), ${res.autoAdoptedCount} adopted, ${res.missingRemovedCount} missing removed.`,
      });
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
      qc.invalidateQueries({ queryKey: ['catalog-tree', workId] });
    },
    onError: (err: Error) => {
      setWorkActionBanner({
        kind: 'warning',
        text: `Work scan and language check failed: ${err.message}`,
      });
    },
  });

  const autoClassifyMutation = useMutation({
    mutationFn: () =>
      apiFetch<CatalogAutoClassifyReportDto>('/api/v1/catalog/works/auto-classify', {
        method: 'POST',
        body: JSON.stringify({}),
      }),
    onSuccess: (report) => {
      setAutoClassifyReport(report);
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
      qc.invalidateQueries({ queryKey: ['work-tree'] });
      qc.invalidateQueries({ queryKey: ['catalog-tree'] });
      const countsSummary = Object.entries(report.countsByClassification ?? {})
        .map(([k, count]) => `${k}: ${count}`)
        .join(', ');
      setWorkActionBanner({
        kind: 'success',
        text: `Library auto-classification complete: ${report.evaluatedCount} evaluated, ${report.updatedCount} updated${
          countsSummary ? ` (${countsSummary})` : ''
        }.`,
      });
    },
    onError: (err: Error) => {
      setWorkActionBanner({
        kind: 'warning',
        text: `Auto-classify library failed: ${err.message}`,
      });
    },
  });

  const resolveHealthFilterMutation = useMutation({
    mutationFn: (resolveAction: string) =>
      apiFetch<SystemHealthResolveResponse>(
        `/api/v1/system/health/resolve/${encodeURIComponent(resolveAction)}`,
        { method: 'POST' },
      ),
    onSuccess: (res, resolveAction) => {
      const msg =
        res?.message ??
        `Resolved '${res?.action ?? resolveAction}'${res?.affectedCount !== undefined ? ` (${res.affectedCount} items updated)` : ''}.`;
      setWorkActionBanner({
        kind: 'success',
        text: msg,
      });
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
      qc.invalidateQueries({ queryKey: ['libraries'] });
      qc.invalidateQueries({ queryKey: ['system-health'] });
    },
    onError: (err: Error) => {
      setWorkActionBanner({
        kind: 'warning',
        text: `Resolve health issue failed: ${err.message}`,
      });
    },
  });

  const workMonitorPresetMutation = useMutation({
    mutationFn: ({ workId, monitorPreset }: { workId: number; monitorPreset: string }) =>
      apiFetch<{ message?: string }>(`/api/v1/catalog/works/${workId}/monitor-preset`, {
        method: 'POST',
        body: JSON.stringify({ monitorPreset }),
      }),
    onSuccess: (_, vars) => {
      setWorkActionBanner({
        kind: 'success',
        text: `Applied monitor preset '${vars.monitorPreset}' to '${treeQuery.data?.work?.title}'.`,
      });
      qc.invalidateQueries({ queryKey: ['catalog-tree', selectedWorkId] });
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
    },
    onError: (err: Error) => {
      setWorkActionBanner({
        kind: 'warning',
        text: `Failed to apply monitor preset: ${err.message}`,
      });
    },
  });

  const updateWorkMutation = useMutation({
    mutationFn: ({ workId, patch }: { workId: number; patch: Record<string, unknown> }) =>
      apiFetch<MediaWorkDetailTree>(`/api/v1/catalog/works/${workId}`, {
        method: 'PATCH',
        body: JSON.stringify(patch),
      }),
    onSuccess: (_data, vars) => {
      qc.invalidateQueries({ queryKey: ['catalog-tree', vars.workId] });
      qc.invalidateQueries({ queryKey: ['catalog-work-tree', vars.workId] });
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
      setWorkActionBanner({
        kind: 'success',
        text: 'Work settings updated.',
      });
    },
    onError: (err: Error) => {
      setWorkActionBanner({
        kind: 'warning',
        text: `Update failed: ${err.message}`,
      });
    },
  });

  const bulkEditWorksMutation = useMutation({
    mutationFn: async () => {
      const workIds = Array.from(selectedWorkIds);
      const monitored =
        bulkMonitored === 'monitored' ? true : bulkMonitored === 'unmonitored' ? false : undefined;
      const monitorPreset = bulkMonitorPreset === 'keep' ? undefined : bulkMonitorPreset;
      const clearQualityProfile = bulkQualityProfileId === 'default' ? true : undefined;
      const qualityProfileId =
        bulkQualityProfileId === 'keep'
          ? undefined
          : bulkQualityProfileId === 'default'
            ? null
            : Number(bulkQualityProfileId);
      const clearLanguagePolicy = bulkLanguagePolicyId === 'default' ? true : undefined;
      const languagePolicyId =
        bulkLanguagePolicyId === 'keep'
          ? undefined
          : bulkLanguagePolicyId === 'default'
            ? null
            : Number(bulkLanguagePolicyId);
      const originalLanguage = bulkOriginalLanguage === 'keep' ? undefined : bulkOriginalLanguage;
      const minimumAvailability = bulkMinAvailability === 'keep' ? undefined : bulkMinAvailability;
      const libraryId = bulkLibraryId === 'keep' ? undefined : Number(bulkLibraryId);
      const parsedTags = bulkTags.trim()
        ? bulkTags
            .split(',')
            .map((s) => s.trim())
            .filter(Boolean)
        : undefined;

      return apiFetch<{ updatedCount?: number }>('/api/v1/catalog/works/bulk-edit', {
        method: 'POST',
        body: JSON.stringify({
          workIds,
          monitored,
          monitorPreset,
          qualityProfileId,
          clearQualityProfile,
          languagePolicyId,
          clearLanguagePolicy,
          originalLanguage,
          minimumAvailability,
          libraryId,
          moveFilesOnLibraryChange: bulkMoveFiles,
          tags: parsedTags,
          tagMode: bulkTagMode,
        }),
      });
    },
    onSuccess: (res) => {
      const count = res?.updatedCount ?? selectedWorkIds.size;
      setWorkActionBanner({
        kind: 'success',
        text: `Bulk updated ${count} work${count === 1 ? '' : 's'}.`,
      });
      setSelectedWorkIds(new Set());
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
      qc.invalidateQueries({ queryKey: ['libraries'] });
    },
    onError: (err: Error) => {
      setWorkActionBanner({
        kind: 'warning',
        text: `Bulk edit failed: ${err.message}`,
      });
    },
  });

  const bulkScanAndVerifyMutation = useMutation({
    mutationFn: async () => {
      const allIds = Array.from(selectedWorkIds);
      const chunkSize = 50;
      const agg: BulkWorkLanguageScanReportDto = {
        targetedWorksCount: 0,
        totalFilesScannedCount: 0,
        totalAutoAdoptedCount: 0,
        totalMissingRemovedCount: 0,
        totalFilesLanguageCheckedCount: 0,
        totalVerifiedPassCount: 0,
        totalLanguageMismatchCount: 0,
        durationMs: 0,
        reports: [],
      };
      for (let i = 0; i < allIds.length; i += chunkSize) {
        const workIds = allIds.slice(i, i + chunkSize);
        const part = await apiFetch<BulkWorkLanguageScanReportDto>('/api/v1/catalog/works/bulk-scan-and-verify', {
          method: 'POST',
          body: JSON.stringify({ workIds }),
        });
        agg.targetedWorksCount += part.targetedWorksCount;
        agg.totalFilesScannedCount += part.totalFilesScannedCount;
        agg.totalAutoAdoptedCount += part.totalAutoAdoptedCount;
        agg.totalMissingRemovedCount += part.totalMissingRemovedCount;
        agg.totalFilesLanguageCheckedCount += part.totalFilesLanguageCheckedCount;
        agg.totalVerifiedPassCount += part.totalVerifiedPassCount;
        agg.totalLanguageMismatchCount += part.totalLanguageMismatchCount;
        agg.durationMs += part.durationMs;
        agg.reports.push(...part.reports);
      }
      return agg;
    },
    onSuccess: (res) => {
      setWorkActionBanner({
        kind: 'success',
        text: `Scanned ${res.targetedWorksCount} selected item(s): ${res.totalFilesLanguageCheckedCount} file(s) language-checked (${res.totalVerifiedPassCount} verified, ${res.totalLanguageMismatchCount} mismatch), ${res.totalAutoAdoptedCount} adopted, ${res.totalMissingRemovedCount} missing removed.`,
      });
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
      qc.invalidateQueries({ queryKey: ['catalog-tree'] });
    },
    onError: (err: Error) => {
      setWorkActionBanner({
        kind: 'warning',
        text: `Cancel scan job failed: ${err.message}`,
      });
    },
  });

  const deleteSelectedWorksMutation = useMutation({
    mutationFn: async () => {
      const ids = Array.from(selectedWorkIds);
      try {
        await apiFetch('/api/v1/catalog/works/bulk-edit', {
          method: 'POST',
          body: JSON.stringify({
            workIds: ids,
            deleteWorks: true,
            deleteFilesOnDisk: deleteFilesOnDisk,
          }),
        });
      } catch {
        try {
          await apiFetch('/api/v1/catalog/works/bulk-delete', {
            method: 'POST',
            body: JSON.stringify({
              workIds: ids,
              deleteFiles: deleteFilesOnDisk,
            }),
          });
        } catch {
          await Promise.all(
            ids.map((id) =>
              apiFetch(`/api/v1/catalog/works/${id}${deleteFilesOnDisk ? '?deleteFiles=true' : ''}`, {
                method: 'DELETE',
              }),
            ),
          );
        }
      }
    },
    onSuccess: () => {
      const count = selectedWorkIds.size;
      setShowDeleteConfirmModal(false);
      setSelectedWorkIds(new Set());
      setWorkActionBanner({
        kind: 'success',
        text: `Deleted ${count} work${count === 1 ? '' : 's'}${deleteFilesOnDisk ? ' and files on disk' : ''}.`,
      });
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
      qc.invalidateQueries({ queryKey: ['libraries'] });
    },
    onError: (err: Error) => {
      setWorkActionBanner({
        kind: 'warning',
        text: `Deletion failed: ${err.message}`,
      });
    },
  });

  const quickToggleWorkMonitorMutation = useMutation({
    mutationFn: ({ workId, monitored }: { workId: number; monitored: boolean }) =>
      apiFetch<MediaWorkDetailTree>(`/api/v1/catalog/works/${workId}`, {
        method: 'PATCH',
        body: JSON.stringify({ monitored }),
      }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['catalog-works'] });
      if (selectedWorkId !== null) {
        void qc.invalidateQueries({ queryKey: ['catalog-tree', selectedWorkId] });
      }
    },
  });

  const librariesById = useMemo(() => {
    const map = new Map<number, LibrarySummary>();
    for (const lib of librariesQuery.data ?? []) {
      map.set(lib.id, lib);
    }
    return map;
  }, [librariesQuery.data]);

  const normalizeWorkVertical = (wk: string): string => {
    const v = wk.toLowerCase();
    if (v === 'movie') return 'movies';
    if (v === 'series') return 'tv';
    if (v === 'book') return 'ebooks';
    return v;
  };

  const allWorks = useMemo(() => worksQuery.data ?? [], [worksQuery.data]);

  const catalogFacetCounts = useMemo(() => {
    const verticalCounts: Record<string, number> = {};
    const libraryCounts: Record<number, number> = {};
    let monitored = 0;
    let unmonitored = 0;
    let missing = 0;
    let complete = 0;
    let continuing = 0;

    for (const w of allWorks) {
      const nv = normalizeWorkVertical(w.workKind);
      verticalCounts[nv] = (verticalCounts[nv] ?? 0) + 1;
      libraryCounts[w.libraryId] = (libraryCounts[w.libraryId] ?? 0) + 1;
      if (w.monitored) monitored++;
      else unmonitored++;

      const progress = resolveWorkDownloadProgress(w);
      if (progress.isComplete) complete++;
      else if (progress.hasMissingMonitored || (w.monitored && w.unitCount === 0)) missing++;

      if (w.status === 'continuing') continuing++;
    }

    return {
      verticalCounts,
      libraryCounts,
      monitored,
      unmonitored,
      missing,
      complete,
      continuing,
    };
  }, [allWorks]);

  const visibleVerticalBubbles = useMemo(() => {
    const libTypes = new Set((librariesQuery.data ?? []).map((l) => normalizeWorkVertical(l.mediaType)));
    const coreIds = new Set(['movies', 'tv', 'anime', 'music', 'ebooks']);
    return LIBRARY_VERTICAL_BUBBLES.filter(
      (b) =>
        coreIds.has(b.id) ||
        libTypes.has(b.id) ||
        (catalogFacetCounts.verticalCounts[b.id] ?? 0) > 0 ||
        selectedVerticals.has(b.id),
    );
  }, [librariesQuery.data, catalogFacetCounts.verticalCounts, selectedVerticals]);

  const normalizedUrlHealth = (urlHealth ?? '').trim().toLowerCase();
  const healthFilterMeta = useMemo(
    () => (normalizedUrlHealth ? resolveHealthFilterMeta(normalizedUrlHealth) : null),
    [normalizedUrlHealth],
  );

  const activeCatalogFilterCount =
    selectedLibraryIds.size +
    selectedVerticals.size +
    selectedMonitorBubbles.size +
    selectedAvailabilityBubbles.size +
    (classificationFilter !== 'all' ? 1 : 0) +
    (normalizedUrlHealth ? 1 : 0) +
    (catalogFilterText.trim() ? 1 : 0);

  function clearAllCatalogFilters() {
    setSelectedLibraryIds(new Set());
    setSelectedVerticals(new Set());
    setSelectedMonitorBubbles(new Set());
    setSelectedAvailabilityBubbles(new Set());
    setClassificationFilter('all');
    setCatalogFilterText('');
    clearPersistedCatalogFilterState();
    if (onSyncUrlSearch) {
      const nextSearch = buildCatalogSearchWithFilters('', {
        ...DEFAULT_CATALOG_FILTER_STATE,
        sortField: catalogSortField,
        sortAsc: catalogSortAsc,
      });
      lastSyncedUrlSearchRef.current = nextSearch;
      onSyncUrlSearch(nextSearch);
    } else {
      onClearUrlParams?.();
    }
  }

  const filteredWorks = useMemo(() => {
    const q = catalogFilterText.trim().toLowerCase();
    const list = allWorks.filter((w) => {
      if (selectedLibraryIds.size > 0 && !selectedLibraryIds.has(w.libraryId)) {
        return false;
      }

      if (selectedVerticals.size > 0) {
        const nv = normalizeWorkVertical(w.workKind);
        if (!selectedVerticals.has(nv)) {
          return false;
        }
      }

      if (selectedMonitorBubbles.size > 0) {
        const matchMon =
          (selectedMonitorBubbles.has('monitored') && w.monitored) ||
          (selectedMonitorBubbles.has('unmonitored') && !w.monitored);
        if (!matchMon) return false;
      }

      if (selectedAvailabilityBubbles.size > 0) {
        const progress = resolveWorkDownloadProgress(w);
        const isMissing = !progress.isComplete && (progress.hasMissingMonitored || (w.monitored && w.unitCount === 0));
        const matchAvail =
          (selectedAvailabilityBubbles.has('missing') && isMissing) ||
          (selectedAvailabilityBubbles.has('complete') && progress.isComplete) ||
          (selectedAvailabilityBubbles.has('continuing') && w.status === 'continuing');
        if (!matchAvail) return false;
      }

      if (normalizedUrlHealth) {
        const progress = resolveWorkDownloadProgress(w);
        const isComplete = progress.isComplete;
        if (normalizedUrlHealth === 'duplicates') {
          if (w.fileCount <= w.unitCount && allWorks.some((item) => item.fileCount > item.unitCount)) {
            return false;
          }
        } else if (normalizedUrlHealth === 'wrong-kind') {
          const lib = librariesById.get(w.libraryId);
          if (
            lib &&
            normalizeWorkVertical(lib.mediaType) === normalizeWorkVertical(w.workKind) &&
            allWorks.some((item) => {
              const itemLib = librariesById.get(item.libraryId);
              return itemLib && normalizeWorkVertical(itemLib.mediaType) !== normalizeWorkVertical(item.workKind);
            })
          ) {
            return false;
          }
        } else if (normalizedUrlHealth === 'non-canonical-quality') {
          if (w.fileCount === 0 && allWorks.some((item) => item.fileCount > 0)) {
            return false;
          }
        } else if (
          normalizedUrlHealth === 'missing' ||
          normalizedUrlHealth === 'missing-files' ||
          normalizedUrlHealth === 'warning' ||
          normalizedUrlHealth === 'error' ||
          normalizedUrlHealth === 'unhealthy' ||
          normalizedUrlHealth === 'issues'
        ) {
          if (isComplete && allWorks.some((item) => !resolveWorkDownloadProgress(item).isComplete)) {
            return false;
          }
        } else if (
          normalizedUrlHealth === 'ok' ||
          normalizedUrlHealth === 'healthy' ||
          normalizedUrlHealth === 'complete'
        ) {
          if (!isComplete) return false;
        } else if (normalizedUrlHealth === 'unmonitored') {
          if (w.monitored) return false;
        }
      }

      if (classificationFilter !== 'all') {
        const badge = resolveClassificationBadge(w);
        if (classificationFilter === 'cartoon') {
          const matchCartoon =
            badge?.key === 'cartoon' ||
            badge?.key === 'cartoon-series' ||
            badge?.key === 'cartoon-movie' ||
            w.tags?.some((t) => t.toLowerCase().includes('cartoon'));
          if (!matchCartoon) return false;
        } else if (classificationFilter === 'anime-series') {
          const matchAnimeSeries =
            badge?.key === 'anime-series' ||
            w.tags?.some((t) => t.toLowerCase() === 'anime-series' || t.toLowerCase() === 'anime');
          if (!matchAnimeSeries) return false;
        } else if (classificationFilter === 'anime-movie') {
          const matchAnimeMovie =
            badge?.key === 'anime-movie' ||
            w.tags?.some((t) => t.toLowerCase() === 'anime-movie');
          if (!matchAnimeMovie) return false;
        } else if (classificationFilter === 'live-action-series') {
          const matchLiveActionSeries =
            badge?.key === 'live-action-series' ||
            w.tags?.some((t) => t.toLowerCase() === 'live-action-series' || t.toLowerCase() === 'live-action');
          if (!matchLiveActionSeries) return false;
        } else if (classificationFilter === 'live-action-movie') {
          const matchLiveActionMovie =
            badge?.key === 'live-action-movie' ||
            w.tags?.some((t) => t.toLowerCase() === 'live-action-movie');
          if (!matchLiveActionMovie) return false;
        }
      }

      if (q && !matchesCatalogWorkQuery(w, q)) {
        return false;
      }

      return true;
    });

    const dir = catalogSortAsc ? 1 : -1;
    return [...list].sort((a, b) => {
      if (catalogSortField === 'year') {
        const ya = a.year ?? 0;
        const yb = b.year ?? 0;
        if (ya !== yb) return (ya - yb) * dir;
      } else if (catalogSortField === 'missing') {
        const ma = resolveWorkDownloadProgress(a).missingMonitoredUnits;
        const mb = resolveWorkDownloadProgress(b).missingMonitoredUnits;
        if (ma !== mb) return (ma - mb) * dir;
      } else if (catalogSortField === 'added') {
        const cmp = compareIsoInstants(a.addedAt, b.addedAt);
        if (cmp !== 0) return cmp * dir;
        if (a.id !== b.id) return (a.id - b.id) * dir;
      }
      return catalogTitleCollator.compare(a.title, b.title) * dir;
    });
  }, [
    allWorks,
    selectedLibraryIds,
    selectedVerticals,
    selectedMonitorBubbles,
    selectedAvailabilityBubbles,
    normalizedUrlHealth,
    librariesById,
    classificationFilter,
    catalogFilterText,
    catalogSortField,
    catalogSortAsc,
  ]);

  const activeAddTab = ADD_VERTICAL_TABS.find((t) => t.id === addVertical) ?? ADD_VERTICAL_TABS[0];

  function handleSelectCatalogSort(field: CatalogSortField) {
    const isSame = field === catalogSortField;
    const nextAsc = isSame ? !catalogSortAsc : field === 'title';
    if (isSame) {
      setCatalogSortAsc(nextAsc);
    } else {
      setCatalogSortField(field);
      setCatalogSortAsc(nextAsc);
    }
    const label =
      field === 'added'
        ? 'Date Added'
        : field === 'year'
          ? 'Year'
          : field === 'missing'
            ? 'Missing'
            : 'Title';
    setSortAnnouncement(
      `Sorted by ${label}, ${nextAsc ? 'ascending' : 'descending'}, ${filteredWorks.length} items`,
    );
  }

  return (
    <div className="flex flex-col flex-1">
      <CatalogToolbarSection
        section={section}
        selectedWorkId={selectedWorkId}
        onReturnToLibrary={() => {
          setSelectedWorkId(null);
          setWorkActionBanner(null);
          setInteractiveTarget(null);
          onClearFocusedWork?.();
        }}
        treeData={treeQuery.data}
        canWriteCatalog={canWriteCatalog}
        isRefreshMetadataPending={refreshMetadataMutation.isPending}
        onRefreshMetadata={(workId) => refreshMetadataMutation.mutate({ workId })}
        isWorkScanPending={workScanMutation.isPending}
        onWorkScan={(workId) => workScanMutation.mutate(workId)}
        isWorkScanAndVerifyPending={workScanAndVerifyMutation.isPending}
        onWorkScanAndVerify={(workId) => workScanAndVerifyMutation.mutate(workId)}
        onOpenRenamePreview={setRenameModalConfig}
        canSearchIndexers={canSearchIndexers}
        isAutoSearchWorkPending={autoSearchWorkMutation.isPending}
        onAutoSearchWork={(workId) => autoSearchWorkMutation.mutate(workId)}
        onSetInteractiveTarget={setInteractiveTarget}
        onNavigate={onNavigate}
        onSetShowAddLibModal={setShowAddLibModal}
        manualMode={manualMode}
        onToggleManualMode={() => setManualMode((v) => !v)}
        isAnyCatalogQueryFetching={
          worksQuery.isFetching ||
          librariesQuery.isFetching ||
          calendarQuery.isFetching ||
          requestsQuery.isFetching
        }
        onRefreshActiveSection={() => {
          void librariesQuery.refetch();
          if (section === 'catalog') void worksQuery.refetch();
          if (section === 'calendar') void calendarQuery.refetch();
          if (section === 'requests') void requestsQuery.refetch();
          if (section === 'metadata') void providersQuery.refetch();
        }}
        isAutoClassifyPending={autoClassifyMutation.isPending}
        onAutoClassify={() => autoClassifyMutation.mutate()}
        massEditMode={massEditMode}
        onToggleMassEditMode={() => {
          setMassEditMode((v) => !v);
          if (massEditMode) setSelectedWorkIds(new Set());
        }}
        canRequest={canRequest}
        onSetShowRequestModal={setShowRequestModal}
        onCalendarPrev={handleCalendarPrev}
        onCalendarToday={handleCalendarToday}
        onCalendarNext={handleCalendarNext}
        calRangeLabel={calRangeLabel}
        onSetShowIcalModal={setShowIcalModal}
        catalogFilterText={catalogFilterText}
        onSetCatalogFilterText={setCatalogFilterText}
        filteredWorks={filteredWorks}
        onSearchInAddNew={onSearchInAddNew}
        onOpenWork={openWork}
        catalogSortField={catalogSortField}
        catalogSortAsc={catalogSortAsc}
        sortStatusAnnouncement={sortAnnouncement}
        onSelectCatalogSort={handleSelectCatalogSort}
        onSetCatalogSortField={setCatalogSortField}
        onToggleCatalogSortAsc={() => setCatalogSortAsc((v) => !v)}
        onSetCatalogSortAsc={setCatalogSortAsc}
        viewMode={viewMode}
        onUpdateViewMode={updateViewMode}
        addVertical={addVertical}
        onSetAddVertical={setAddVertical}
        calendarViewMode={calendarViewMode}
        onSetCalendarViewMode={setCalendarViewMode}
        calSelectedVerticals={calSelectedVerticals}
        onSetCalSelectedVerticals={setCalSelectedVerticals}
        calMonitoredOnly={calMonitoredOnly}
        onSetCalMonitoredOnly={setCalMonitoredOnly}
      />

      {/* ==================== MULTI-SELECT TOP FILTER BUBBLE BAR ==================== */}
      {section === 'catalog' && selectedWorkId === null && (
        <CatalogFilterBubbleBar
          libraries={librariesQuery.data ?? []}
          visibleVerticalBubbles={visibleVerticalBubbles}
          catalogFacetCounts={catalogFacetCounts}
          selectedVerticals={selectedVerticals}
          onToggleVertical={(id) => setSelectedVerticals((prev) => toggleFilterSetValue(prev, id))}
          selectedLibraryIds={selectedLibraryIds}
          onToggleLibraryId={(id) => setSelectedLibraryIds((prev) => toggleFilterSetValue(prev, id))}
          selectedMonitorBubbles={selectedMonitorBubbles}
          onToggleMonitorBubble={(id) => setSelectedMonitorBubbles((prev) => toggleFilterSetValue(prev, id))}
          selectedAvailabilityBubbles={selectedAvailabilityBubbles}
          onToggleAvailabilityBubble={(id) => setSelectedAvailabilityBubbles((prev) => toggleFilterSetValue(prev, id))}
          normalizedUrlHealth={normalizedUrlHealth}
          onClearUrlParams={onClearUrlParams}
          classificationFilter={classificationFilter}
          onSetClassificationFilter={setClassificationFilter}
          filteredWorksCount={filteredWorks.length}
          allWorksCount={allWorks.length}
          activeCatalogFilterCount={activeCatalogFilterCount}
          onClearAllCatalogFilters={clearAllCatalogFilters}
        />
      )}

      <PageContentBody>
        {/* ==================== LIBRARY CATALOG LIST ==================== */}
        {section === 'catalog' && selectedWorkId === null && (
          <CatalogLibraryGridSection
            healthFilterMeta={healthFilterMeta}
            isResolveHealthFilterPending={resolveHealthFilterMutation.isPending}
            onResolveHealthFilter={(resolveAction) => resolveHealthFilterMutation.mutate(resolveAction)}
            onClearUrlParams={onClearUrlParams}
            autoClassifyReport={autoClassifyReport}
            onDismissAutoClassifyReport={() => setAutoClassifyReport(null)}
            workActionBanner={workActionBanner}
            onDismissWorkActionBanner={() => setWorkActionBanner(null)}
            libraries={librariesQuery.data ?? []}
            librariesById={librariesById}
            canWriteCatalog={canWriteCatalog}
            onNavigate={onNavigate}
            onSetShowAddLibModal={setShowAddLibModal}
            isWorksLoading={worksQuery.isLoading}
            filteredWorks={filteredWorks}
            allWorks={allWorks}
            activeCatalogFilterCount={activeCatalogFilterCount}
            onClearAllCatalogFilters={clearAllCatalogFilters}
            catalogFilterText={catalogFilterText}
            onSearchInAddNew={onSearchInAddNew}
            massEditMode={massEditMode}
            selectedWorkIds={selectedWorkIds}
            onSetSelectedWorkIds={setSelectedWorkIds}
            viewMode={viewMode}
            catalogSortField={catalogSortField}
            catalogSortAsc={catalogSortAsc}
            onSelectCatalogSort={handleSelectCatalogSort}
            onOpenWork={openWork}
            onQuickToggleWorkMonitor={(workId, monitored) =>
              quickToggleWorkMonitorMutation.mutate({ workId, monitored })
            }
            onSetShowDeleteConfirmModal={setShowDeleteConfirmModal}
            isBulkEditPending={bulkEditWorksMutation.isPending}
            onBulkEditWorks={() => bulkEditWorksMutation.mutate()}
            isBulkScanAndVerifyPending={bulkScanAndVerifyMutation.isPending}
            onBulkScanAndVerify={() => bulkScanAndVerifyMutation.mutate()}
            bulkMonitored={bulkMonitored}
            onSetBulkMonitored={setBulkMonitored}
            bulkMonitorPreset={bulkMonitorPreset}
            onSetBulkMonitorPreset={setBulkMonitorPreset}
            bulkQualityProfileId={bulkQualityProfileId}
            onSetBulkQualityProfileId={setBulkQualityProfileId}
            profiles={profilesQuery.data ?? []}
            languagePolicies={languagePoliciesQuery.data ?? []}
            bulkLanguagePolicyId={bulkLanguagePolicyId}
            onSetBulkLanguagePolicyId={setBulkLanguagePolicyId}
            bulkOriginalLanguage={bulkOriginalLanguage}
            onSetBulkOriginalLanguage={setBulkOriginalLanguage}
            bulkMinAvailability={bulkMinAvailability}
            onSetBulkMinAvailability={setBulkMinAvailability}
            bulkLibraryId={bulkLibraryId}
            onSetBulkLibraryId={setBulkLibraryId}
            bulkMoveFiles={bulkMoveFiles}
            onSetBulkMoveFiles={setBulkMoveFiles}
            bulkTags={bulkTags}
            onSetBulkTags={setBulkTags}
            bulkTagMode={bulkTagMode}
            onSetBulkTagMode={setBulkTagMode}
          />
        )}

        {/* ==================== WORK DETAIL VIEW ==================== */}
        {section === 'catalog' && selectedWorkId !== null && (
          <CatalogWorkDetailSection
            selectedWorkId={selectedWorkId}
            isTreeLoading={treeQuery.isLoading}
            isTreeError={treeQuery.isError}
            treeData={treeQuery.data}
            onBackToLibrary={() => {
              setSelectedWorkId(null);
              onClearFocusedWork?.();
            }}
            workActionBanner={workActionBanner}
            onDismissBanner={() => setWorkActionBanner(null)}
            canWriteCatalog={canWriteCatalog}
            canSearchIndexers={canSearchIndexers}
            onQuickToggleWorkMonitor={(workId, monitored) =>
              quickToggleWorkMonitorMutation.mutate({ workId, monitored })
            }
            isWorkMonitorPresetPending={workMonitorPresetMutation.isPending}
            onApplyWorkMonitorPreset={(workId, monitorPreset) =>
              workMonitorPresetMutation.mutate({ workId, monitorPreset })
            }
            onUpdateWork={(workId, patch) => updateWorkMutation.mutate({ workId, patch })}
            profiles={profilesQuery.data ?? []}
            languagePolicies={languagePoliciesQuery.data ?? []}
            collapsedContainers={collapsedContainers}
            onToggleCollapseContainer={(containerId) =>
              setCollapsedContainers((prev) => ({
                ...prev,
                [containerId]: !prev[containerId],
              }))
            }
            isToggleContainerPending={toggleContainerMutation.isPending}
            onToggleContainerMonitor={(containerId, monitored) =>
              toggleContainerMutation.mutate({ containerId, monitored })
            }
            searchingContainerId={searchingContainerId}
            isAutoSearchContainerPending={autoSearchContainerMutation.isPending}
            onAutoSearchContainer={(workId, containerId) =>
              autoSearchContainerMutation.mutate({ workId, containerId })
            }
            onToggleUnitMonitor={(unitId, monitored) =>
              toggleUnitMutation.mutate({ unitId, monitored })
            }
            searchingUnitId={searchingUnitId}
            isAutoSearchUnitPending={autoSearchUnitMutation.isPending}
            onAutoSearchUnit={(workId, unitId) => autoSearchUnitMutation.mutate({ workId, unitId })}
            interactiveTarget={interactiveTarget}
            onSetInteractiveTarget={setInteractiveTarget}
            isInteractiveSearchFetching={interactiveSearchQuery.isFetching}
            interactiveSearchRows={interactiveSearchQuery.data ?? []}
            onRefetchInteractiveSearch={() => void interactiveSearchQuery.refetch()}
            interactiveFilterText={interactiveFilterText}
            onSetInteractiveFilterText={setInteractiveFilterText}
            interactiveProtocolFilter={interactiveProtocolFilter}
            onSetInteractiveProtocolFilter={setInteractiveProtocolFilter}
            interactiveHideRejected={interactiveHideRejected}
            onSetInteractiveHideRejected={setInteractiveHideRejected}
            interactiveSortKey={interactiveSortKey}
            onSetInteractiveSortKey={setInteractiveSortKey}
            interactiveSortDir={interactiveSortDir}
            onSetInteractiveSortDir={setInteractiveSortDir}
            expandedRejections={expandedRejections}
            onToggleExpandedRejection={(releaseKey) =>
              setExpandedRejections((prev) => ({
                ...prev,
                [releaseKey]: !prev[releaseKey],
              }))
            }
            grabbingGuid={grabbingGuid}
            isGrabInteractivePending={grabInteractiveMutation.isPending}
            onGrabInteractiveRelease={(row, forceGrab) => grabInteractiveMutation.mutate({ row, forceGrab })}
          />
        )}

        {/* ==================== ADD NEW / MANUAL ENTRY / CANDIDATE MODAL ==================== */}
        {(section === 'add' || selectedCandidate !== null) && (
          <CatalogAddMediaSection
            isAddSection={section === 'add'}
            manualMode={manualMode}
            onSetManualMode={setManualMode}
            addSearchInput={addSearchInput}
            onSetAddSearchInput={setAddSearchInput}
            debouncedAddTerm={debouncedAddTerm}
            onSetDebouncedAddTerm={setDebouncedAddTerm}
            activeAddTab={activeAddTab}
            onSetAddVertical={setAddVertical}
            isMetadataSearchFetching={metadataSearchQuery.isFetching}
            isMetadataSearchError={metadataSearchQuery.isError}
            metadataSearchError={(metadataSearchQuery.error as Error | null) ?? null}
            metadataCandidates={metadataSearchQuery.data ?? []}
            existingWorkLookup={existingWorkLookup}
            onOpenCandidateAddModal={openCandidateAddModal}
            canWriteCatalog={canWriteCatalog}
            onSetShowAddLibModal={setShowAddLibModal}
            libraries={librariesQuery.data ?? []}
            activeLibId={activeLibId}
            onSetFormLibraryId={setFormLibraryId}
            workTitle={workTitle}
            onSetWorkTitle={setWorkTitle}
            workYear={workYear}
            onSetWorkYear={setWorkYear}
            workProvider={workProvider}
            onSetWorkProvider={setWorkProvider}
            builtInProviders={providersQuery.data?.builtInProviders ?? []}
            onAddManualWork={() => addManualWorkMutation.mutate()}
            isAddManualWorkPending={addManualWorkMutation.isPending}
            selectedCandidate={selectedCandidate}
            onCloseCandidateModal={() => setSelectedCandidate(null)}
            modalSearchOnAdd={modalSearchOnAdd}
            onSetModalSearchOnAdd={setModalSearchOnAdd}
            onAddCandidate={(cand) => addCandidateMutation.mutate(cand)}
            isAddCandidatePending={addCandidateMutation.isPending}
            isAddCandidateError={addCandidateMutation.isError}
            addCandidateError={(addCandidateMutation.error as Error | null) ?? null}
            modalLibraryId={modalLibraryId}
            onSetModalLibraryId={setModalLibraryId}
            modalMonitorMode={modalMonitorMode}
            onSetModalMonitorMode={setModalMonitorMode}
            modalMinimumAvailability={modalMinimumAvailability}
            onSetModalMinimumAvailability={setModalMinimumAvailability}
            modalProfileId={modalProfileId}
            onSetModalProfileId={setModalProfileId}
            profiles={profilesQuery.data ?? []}
            languagePolicies={languagePoliciesQuery.data ?? []}
            modalLanguagePolicyId={modalLanguagePolicyId}
            onSetModalLanguagePolicyId={setModalLanguagePolicyId}
            modalOriginalLanguage={modalOriginalLanguage}
            onSetModalOriginalLanguage={setModalOriginalLanguage}
            modalAudioPreset={modalAudioPreset}
            onSetModalAudioPreset={setModalAudioPreset}
            modalOrdering={modalOrdering}
            onSetModalOrdering={setModalOrdering}
            modalTags={modalTags}
            onSetModalTags={setModalTags}
          />
        )}

        {/* ==================== CALENDAR ==================== */}
        {section === 'calendar' && (
          <CatalogCalendarSection
            calRangeLabel={calRangeLabel}
            filteredCalendarItems={filteredCalendarItems}
            calendarViewMode={calendarViewMode}
            calMonthGridDays={calMonthGridDays}
            calWeekDays={calWeekDays}
            calendarItemsByDate={calendarItemsByDate}
            onOpenWork={(workId) => {
              if (onSelectWorkAndNavigate) {
                onSelectWorkAndNavigate(workId);
              } else {
                setSelectedWorkId(workId);
                onNavigate?.('catalog');
              }
            }}
            showIcalModal={showIcalModal}
            onSetShowIcalModal={setShowIcalModal}
            icalVertical={icalVertical}
            onSetIcalVertical={setIcalVertical}
            icalIncludeUnmonitored={icalIncludeUnmonitored}
            onSetIcalIncludeUnmonitored={setIcalIncludeUnmonitored}
            icalPastDays={icalPastDays}
            onSetIcalPastDays={setIcalPastDays}
            icalFutureDays={icalFutureDays}
            onSetIcalFutureDays={setIcalFutureDays}
            icalApiKey={icalApiKey}
            onSetIcalApiKey={setIcalApiKey}
            onGenerateCalendarToken={() => createCalendarTokenMutation.mutate()}
            isGeneratingCalendarToken={createCalendarTokenMutation.isPending}
            icalFeedUrl={icalFeedUrl}
            webcalFeedUrl={webcalFeedUrl}
            copiedIcalUrl={copiedIcalUrl}
            onSetCopiedIcalUrl={setCopiedIcalUrl}
          />
        )}

        {/* ==================== REQUESTS / MEDIA MANAGEMENT / METADATA ==================== */}
        {(section === 'requests' || section === 'media-management' || section === 'metadata') && (
          <CatalogSettingsAndRequestsSection
            section={section}
            requests={requestsQuery.data ?? []}
            canApprove={canApprove}
            onDecideRequest={(id, approve) => decideRequestMutation.mutate({ id, approve })}
            scanReportBanner={scanReportBanner}
            onDismissScanReport={() => setScanReportBanner(null)}
            libraries={librariesQuery.data ?? []}
            works={worksQuery.data ?? []}
            profiles={profilesQuery.data ?? []}
            canWriteCatalog={canWriteCatalog}
            canAdmin={canSystemAdmin}
            isLibraryScanPending={libraryScanMutation.isPending}
            scanningLibraryId={libraryScanMutation.variables}
            onScanLibrary={(libId) => libraryScanMutation.mutate(libId)}
            onOpenRenamePreview={setRenameModalConfig}
            providersData={providersQuery.data}
            cwProvider={cwProvider}
            onChangeCwProvider={setCwProvider}
            cwId={cwId}
            onChangeCwId={setCwId}
            crosswalkData={crosswalkQuery.data}
          />
        )}

        {/* ==================== MODALS ==================== */}
        <CatalogModalsSection
          showAddLibModal={showAddLibModal}
          onSetShowAddLibModal={setShowAddLibModal}
          libName={libName}
          onSetLibName={setLibName}
          libType={libType}
          onSetLibType={setLibType}
          libPath={libPath}
          onSetLibPath={setLibPath}
          libPlexSection={libPlexSection}
          onSetLibPlexSection={setLibPlexSection}
          isCreateLibPending={createLibMutation.isPending}
          onCreateLib={() => createLibMutation.mutate()}
          showRequestModal={showRequestModal}
          onSetShowRequestModal={setShowRequestModal}
          reqTitle={reqTitle}
          onSetReqTitle={setReqTitle}
          reqAudioLang={reqAudioLang}
          onSetReqAudioLang={setReqAudioLang}
          activeLibId={activeLibId}
          onSetFormLibraryId={setFormLibraryId}
          libraries={librariesQuery.data ?? []}
          isSubmitRequestPending={submitRequestMutation.isPending}
          onSubmitRequest={() => submitRequestMutation.mutate()}
          renameModalConfig={renameModalConfig}
          onCloseRenameModal={() => setRenameModalConfig(null)}
          showDeleteConfirmModal={showDeleteConfirmModal}
          onSetShowDeleteConfirmModal={setShowDeleteConfirmModal}
          selectedWorkCount={selectedWorkIds.size}
          deleteFilesOnDisk={deleteFilesOnDisk}
          onSetDeleteFilesOnDisk={setDeleteFilesOnDisk}
          isDeleteSelectedPending={deleteSelectedWorksMutation.isPending}
          onDeleteSelectedWorks={() => deleteSelectedWorksMutation.mutate()}
        />
      </PageContentBody>
    </div>
  );
}
