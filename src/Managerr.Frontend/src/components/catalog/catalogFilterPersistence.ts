import {
  LIBRARY_VERTICAL_BUBBLES,
  type CatalogAvailabilityBubble,
  type CatalogMonitorBubble,
  type CatalogSortField,
} from './catalogTypesAndHelpers';

export const CATALOG_FILTER_STORAGE_KEY = 'managerr.catalogFilterState';
export const CATALOG_SCROLL_STORAGE_KEY = 'managerr.catalogScrollY';

export type CatalogClassificationFilter =
  | 'all'
  | 'anime-series'
  | 'anime-movie'
  | 'cartoon'
  | 'live-action-series'
  | 'live-action-movie';

export interface CatalogPersistedFilterState {
  libraryIds: number[];
  verticals: string[];
  monitorBubbles: CatalogMonitorBubble[];
  availabilityBubbles: CatalogAvailabilityBubble[];
  filterText: string;
  sortField: CatalogSortField;
  sortAsc: boolean;
  classificationFilter: CatalogClassificationFilter;
}

export const DEFAULT_CATALOG_FILTER_STATE: CatalogPersistedFilterState = {
  libraryIds: [],
  verticals: [],
  monitorBubbles: [],
  availabilityBubbles: [],
  filterText: '',
  sortField: 'title',
  sortAsc: true,
  classificationFilter: 'all',
};

const VALID_MONITOR_BUBBLES = new Set<CatalogMonitorBubble>(['monitored', 'unmonitored']);
const VALID_AVAILABILITY_BUBBLES = new Set<CatalogAvailabilityBubble>(['missing', 'complete', 'continuing']);
const VALID_SORT_FIELDS = new Set<CatalogSortField>(['title', 'year', 'missing', 'added']);

function normalizeCatalogSortField(raw: unknown): CatalogSortField {
  if (typeof raw !== 'string') return 'title';
  const cleaned = raw.trim().toLowerCase();
  const normalized =
    cleaned === 'addedat' ||
    cleaned === 'added_at' ||
    cleaned === 'dateadded' ||
    cleaned === 'date_added'
      ? 'added'
      : cleaned;
  return VALID_SORT_FIELDS.has(normalized as CatalogSortField)
    ? (normalized as CatalogSortField)
    : 'title';
}
const VALID_CLASSIFICATIONS = new Set<CatalogClassificationFilter>([
  'all',
  'anime-series',
  'anime-movie',
  'cartoon',
  'live-action-series',
  'live-action-movie',
]);

const CATALOG_URL_PARAM_KEYS = ['libs', 'types', 'mon', 'avail', 'class', 'q', 'sort', 'dir', 'filter'] as const;

export function isDefaultCatalogFilterState(state: CatalogPersistedFilterState): boolean {
  return (
    state.libraryIds.length === 0 &&
    state.verticals.length === 0 &&
    state.monitorBubbles.length === 0 &&
    state.availabilityBubbles.length === 0 &&
    state.filterText === '' &&
    state.sortField === 'title' &&
    state.sortAsc === true &&
    state.classificationFilter === 'all'
  );
}

export function normalizeUrlSearch(search: string | null | undefined): string {
  if (!search) return '';
  const trimmed = search.startsWith('?') ? search.slice(1) : search;
  if (!trimmed) return '';
  const params = new URLSearchParams(trimmed);
  const str = params.toString();
  return str ? `?${str}` : '';
}

export function hasCatalogFilterSearchParams(search: string | null | undefined): boolean {
  if (!search) return false;
  const params = new URLSearchParams(search.startsWith('?') ? search.slice(1) : search);
  return CATALOG_URL_PARAM_KEYS.some((key) => params.has(key));
}

function parsePositiveIntList(raw: string | null): number[] {
  if (!raw) return [];
  const seen = new Set<number>();
  for (const part of raw.split(',')) {
    const n = Number(part.trim());
    if (Number.isInteger(n) && n > 0) {
      seen.add(n);
    }
  }
  return Array.from(seen).sort((a, b) => a - b);
}

function parseStringList(raw: string | null): string[] {
  if (!raw) return [];
  const seen = new Set<string>();
  for (const part of raw.split(',')) {
    const clean = part.trim().toLowerCase();
    if (clean) {
      seen.add(clean);
    }
  }
  return Array.from(seen);
}

export function parseCatalogFilterStateFromSearch(search: string | null | undefined): CatalogPersistedFilterState {
  const params = new URLSearchParams(search && search.startsWith('?') ? search.slice(1) : (search ?? ''));

  const libraryIds = parsePositiveIntList(params.get('libs'));
  const verticals = parseStringList(params.get('types'));
  const monitorBubbles = parseStringList(params.get('mon')).filter((v): v is CatalogMonitorBubble =>
    VALID_MONITOR_BUBBLES.has(v as CatalogMonitorBubble),
  );
  const availabilityBubbles = parseStringList(params.get('avail')).filter((v): v is CatalogAvailabilityBubble =>
    VALID_AVAILABILITY_BUBBLES.has(v as CatalogAvailabilityBubble),
  );

  const rawClass = (params.get('class') ?? '').trim().toLowerCase() as CatalogClassificationFilter;
  const classificationFilter: CatalogClassificationFilter = VALID_CLASSIFICATIONS.has(rawClass) ? rawClass : 'all';

  let filterText = params.get('q') ?? '';

  const sortField: CatalogSortField = normalizeCatalogSortField(params.get('sort'));

  const rawDir = (params.get('dir') ?? '').trim().toLowerCase();
  const sortAsc = rawDir === 'desc' ? false : rawDir === 'asc' ? true : true;

  // Support legacy / quick-search `?filter=...` parameter
  const legacyFilter = (params.get('filter') ?? '').trim();
  if (legacyFilter) {
    const lower = legacyFilter.toLowerCase();
    if (VALID_MONITOR_BUBBLES.has(lower as CatalogMonitorBubble)) {
      if (monitorBubbles.length === 0) {
        monitorBubbles.push(lower as CatalogMonitorBubble);
      }
    } else if (VALID_AVAILABILITY_BUBBLES.has(lower as CatalogAvailabilityBubble)) {
      if (availabilityBubbles.length === 0) {
        availabilityBubbles.push(lower as CatalogAvailabilityBubble);
      }
    } else if (LIBRARY_VERTICAL_BUBBLES.some((b) => b.id === lower)) {
      if (verticals.length === 0) {
        verticals.push(lower);
      }
    } else if (!filterText) {
      filterText = legacyFilter;
    }
  }

  return {
    libraryIds,
    verticals,
    monitorBubbles,
    availabilityBubbles,
    filterText,
    sortField,
    sortAsc,
    classificationFilter,
  };
}

export function buildCatalogSearchWithFilters(
  currentSearch: string | null | undefined,
  state: CatalogPersistedFilterState,
): string {
  const params = new URLSearchParams(
    currentSearch && currentSearch.startsWith('?') ? currentSearch.slice(1) : (currentSearch ?? ''),
  );

  for (const key of CATALOG_URL_PARAM_KEYS) {
    params.delete(key);
  }

  const sortedLibs = [...state.libraryIds].filter((n) => Number.isInteger(n) && n > 0).sort((a, b) => a - b);
  if (sortedLibs.length > 0) {
    params.set('libs', sortedLibs.join(','));
  }

  const cleanVerticals = [...state.verticals].map((v) => v.trim().toLowerCase()).filter(Boolean);
  if (cleanVerticals.length > 0) {
    params.set('types', cleanVerticals.join(','));
  }

  const cleanMonitor = state.monitorBubbles.filter((v) => VALID_MONITOR_BUBBLES.has(v));
  if (cleanMonitor.length > 0) {
    params.set('mon', cleanMonitor.join(','));
  }

  const cleanAvail = state.availabilityBubbles.filter((v) => VALID_AVAILABILITY_BUBBLES.has(v));
  if (cleanAvail.length > 0) {
    params.set('avail', cleanAvail.join(','));
  }

  if (state.classificationFilter !== 'all' && VALID_CLASSIFICATIONS.has(state.classificationFilter)) {
    params.set('class', state.classificationFilter);
  }

  if (state.filterText.length > 0) {
    params.set('q', state.filterText);
  }

  if (state.sortField !== 'title' && VALID_SORT_FIELDS.has(state.sortField)) {
    params.set('sort', state.sortField);
  }

  if (!state.sortAsc) {
    params.set('dir', 'desc');
  }

  const serialized = params.toString();
  return serialized ? `?${serialized}` : '';
}

export function stripHealthAndLegacyFilterParams(currentSearch: string | URLSearchParams): URLSearchParams {
  const next = new URLSearchParams(currentSearch);
  next.delete('health');
  next.delete('filter');
  return next;
}

export function readPersistedCatalogFilterState(): CatalogPersistedFilterState | null {
  if (typeof window === 'undefined') return null;
  try {
    const raw = window.sessionStorage?.getItem(CATALOG_FILTER_STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<CatalogPersistedFilterState> | null;
    if (!parsed || typeof parsed !== 'object') return null;

    const libraryIds = Array.isArray(parsed.libraryIds)
      ? Array.from(new Set(parsed.libraryIds.map(Number).filter((n) => Number.isInteger(n) && n > 0))).sort(
          (a, b) => a - b,
        )
      : [];
    const verticals = Array.isArray(parsed.verticals)
      ? Array.from(
          new Set(
            parsed.verticals
              .filter((v): v is string => typeof v === 'string')
              .map((v) => v.trim().toLowerCase())
              .filter(Boolean),
          ),
        )
      : [];
    const monitorBubbles = Array.isArray(parsed.monitorBubbles)
      ? Array.from(
          new Set(
            parsed.monitorBubbles.filter((v): v is CatalogMonitorBubble =>
              VALID_MONITOR_BUBBLES.has(v as CatalogMonitorBubble),
            ),
          ),
        )
      : [];
    const availabilityBubbles = Array.isArray(parsed.availabilityBubbles)
      ? Array.from(
          new Set(
            parsed.availabilityBubbles.filter((v): v is CatalogAvailabilityBubble =>
              VALID_AVAILABILITY_BUBBLES.has(v as CatalogAvailabilityBubble),
            ),
          ),
        )
      : [];
    const filterText = typeof parsed.filterText === 'string' ? parsed.filterText : '';
    const sortField: CatalogSortField = normalizeCatalogSortField(parsed.sortField);
    const sortAsc = typeof parsed.sortAsc === 'boolean' ? parsed.sortAsc : true;
    const classificationFilter: CatalogClassificationFilter =
      typeof parsed.classificationFilter === 'string' &&
      VALID_CLASSIFICATIONS.has(parsed.classificationFilter as CatalogClassificationFilter)
        ? (parsed.classificationFilter as CatalogClassificationFilter)
        : 'all';

    return {
      libraryIds,
      verticals,
      monitorBubbles,
      availabilityBubbles,
      filterText,
      sortField,
      sortAsc,
      classificationFilter,
    };
  } catch {
    return null;
  }
}

export function writePersistedCatalogFilterState(state: CatalogPersistedFilterState): void {
  if (typeof window === 'undefined') return;
  try {
    if (isDefaultCatalogFilterState(state)) {
      window.sessionStorage?.removeItem(CATALOG_FILTER_STORAGE_KEY);
      return;
    }
    window.sessionStorage?.setItem(CATALOG_FILTER_STORAGE_KEY, JSON.stringify(state));
  } catch {
    // Ignore storage quota or security errors
  }
}

export function clearPersistedCatalogFilterState(): void {
  if (typeof window === 'undefined') return;
  try {
    window.sessionStorage?.removeItem(CATALOG_FILTER_STORAGE_KEY);
  } catch {
    // Ignore storage errors
  }
}

export function resolveInitialCatalogFilterState(
  urlSearch?: string | null,
  urlFilter?: string | null,
): CatalogPersistedFilterState {
  if (urlSearch && hasCatalogFilterSearchParams(urlSearch)) {
    return parseCatalogFilterStateFromSearch(urlSearch);
  }
  const cleanUrlFilter = (urlFilter ?? '').trim();
  if (cleanUrlFilter) {
    return parseCatalogFilterStateFromSearch(`?filter=${encodeURIComponent(cleanUrlFilter)}`);
  }
  return readPersistedCatalogFilterState() ?? DEFAULT_CATALOG_FILTER_STATE;
}

export function buildSavedCatalogLibraryPath(currentSearch = ''): string {
  const saved = readPersistedCatalogFilterState();
  if (!saved || isDefaultCatalogFilterState(saved)) {
    return '/library';
  }
  return `/library${buildCatalogSearchWithFilters(currentSearch, saved)}`;
}

export function saveCatalogScrollY(scrollY: number): void {
  if (typeof window === 'undefined') return;
  try {
    if (Number.isFinite(scrollY) && scrollY > 0) {
      window.sessionStorage?.setItem(CATALOG_SCROLL_STORAGE_KEY, String(Math.round(scrollY)));
    } else {
      window.sessionStorage?.removeItem(CATALOG_SCROLL_STORAGE_KEY);
    }
  } catch {
    // Ignore storage errors
  }
}

export function consumeSavedCatalogScrollY(): number | null {
  if (typeof window === 'undefined') return null;
  try {
    const raw = window.sessionStorage?.getItem(CATALOG_SCROLL_STORAGE_KEY);
    if (!raw) return null;
    window.sessionStorage?.removeItem(CATALOG_SCROLL_STORAGE_KEY);
    const parsed = Number(raw);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
  } catch {
    return null;
  }
}
