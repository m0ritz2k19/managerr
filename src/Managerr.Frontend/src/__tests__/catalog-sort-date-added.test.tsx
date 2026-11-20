import { useState } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CatalogView } from '../components/CatalogView';
import {
  parseCatalogFilterStateFromSearch,
  readPersistedCatalogFilterState,
  CATALOG_FILTER_STORAGE_KEY,
} from '../components/catalog/catalogFilterPersistence';
import {
  compareIsoInstants,
  formatCatalogAddedDate,
} from '../components/catalog/catalogTypesAndHelpers';

function createSortMockFetch() {
  return vi.fn(async (input: RequestInfo | URL) => {
    const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
    const url = new URL(rawUrl, 'http://localhost');
    const path = url.pathname;

    const json = (data: unknown, status = 200) =>
      new Response(JSON.stringify(data), {
        status,
        headers: { 'Content-Type': 'application/json' },
      });

    if (path === '/api/v1/libraries') {
      return json([
        {
          id: 1,
          name: 'Movies',
          mediaType: 'movies',
          rootFolderPath: '/data/media/movies',
          plexSectionId: '1',
          defaultProfileId: 1,
          workCount: 3,
        },
      ]);
    }

    if (path === '/api/v1/catalog/works') {
      return json([
        {
          id: 101,
          libraryId: 1,
          workKind: 'movies',
          title: 'Alpha Earliest',
          altTitles: [],
          originalLanguage: 'en',
          year: 2020,
          status: 'released',
          overview: '',
          posterUrl: null,
          monitored: true,
          lockedFields: [],
          primaryProvider: 'tmdb',
          episodeOrderingScheme: 'aired',
          externalIds: {},
          tags: [],
          containerCount: 1,
          unitCount: 1,
          fileCount: 1,
          monitoredUnitCount: 1,
          downloadedUnitCount: 1,
          addedAt: '2026-11-14T12:37:37.1000000+00:00',
          updatedAt: '2026-11-14T12:37:37.1000000+00:00',
        },
        {
          id: 102,
          libraryId: 1,
          workKind: 'movies',
          title: 'Zulu Latest',
          altTitles: [],
          originalLanguage: 'en',
          year: 2024,
          status: 'released',
          overview: '',
          posterUrl: null,
          monitored: true,
          lockedFields: [],
          primaryProvider: 'tmdb',
          episodeOrderingScheme: 'aired',
          externalIds: {},
          tags: [],
          containerCount: 1,
          unitCount: 1,
          fileCount: 1,
          monitoredUnitCount: 1,
          downloadedUnitCount: 1,
          addedAt: '2026-11-14T12:37:37.6074613+00:00',
          updatedAt: '2026-11-14T12:37:37.6074613+00:00',
        },
        {
          id: 103,
          libraryId: 1,
          workKind: 'movies',
          title: 'Middle Offset',
          altTitles: [],
          originalLanguage: 'ja',
          year: 2022,
          status: 'released',
          overview: '',
          posterUrl: null,
          monitored: true,
          lockedFields: [],
          primaryProvider: 'tmdb',
          episodeOrderingScheme: 'aired',
          externalIds: {},
          tags: [],
          containerCount: 1,
          unitCount: 1,
          fileCount: 1,
          monitoredUnitCount: 1,
          downloadedUnitCount: 1,
          addedAt: '2026-11-14T14:37:37.3500000+02:00',
          updatedAt: '2026-11-14T14:37:37.3500000+02:00',
        },
      ]);
    }

    return json([]);
  });
}

function HarnessCatalogView({
  initialSearch = '',
  onSearchChange,
}: {
  initialSearch?: string;
  onSearchChange?: (search: string) => void;
}) {
  const [urlSearch, setUrlSearch] = useState(initialSearch);
  return (
    <CatalogView
      section="catalog"
      urlSearch={urlSearch}
      onSyncUrlSearch={(next) => {
        setUrlSearch(next);
        onSearchChange?.(next);
      }}
      canWriteCatalog={true}
      canRequest={true}
      canApprove={true}
      canSearchIndexers={true}
    />
  );
}

describe('Catalog Sort by Date Added', () => {
  beforeEach(() => {
    window.sessionStorage.clear();
    window.localStorage.clear();
  });

  it('compares ISO instants accurately across sub-millisecond .NET "O" timestamps, offsets, and missing values', () => {
    expect(
      compareIsoInstants('2026-11-14T12:37:37.6074613+00:00', '2026-11-14T12:37:37.1000000+00:00'),
    ).toBeGreaterThan(0);
    expect(
      compareIsoInstants('2026-11-14T12:37:37.0000001Z', '2026-11-14T12:37:37.0000002+00:00'),
    ).toBeLessThan(0);
    expect(
      compareIsoInstants('2026-11-14T14:37:37.3500000+02:00', '2026-11-14T12:37:37.3500000Z'),
    ).toBe(0);
    expect(compareIsoInstants(undefined, '2026-11-14T12:37:37Z')).toBeLessThan(0);
    expect(compareIsoInstants(undefined, null)).toBe(0);

    expect(formatCatalogAddedDate('2026-11-14T12:37:37.6074613+00:00')).toBe('2026-11-14');
    expect(formatCatalogAddedDate(null)).toBe('—');
    expect(formatCatalogAddedDate('invalid')).toBe('—');
  });

  it('sorts newest-first then toggles oldest-first via toolbar Date Added button, syncing URL and sessionStorage', async () => {
    const user = userEvent.setup();
    vi.stubGlobal('fetch', createSortMockFetch());
    window.localStorage.setItem('managerr.catalog.viewMode.v2', 'table');

    const observedSearches: string[] = [];
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/library']}>
          <HarnessCatalogView onSearchChange={(s) => observedSearches.push(s)} />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    const table = await screen.findByRole('table');
    const getRowTitles = () => {
      const tbody = within(table).getAllByRole('row').slice(1);
      return tbody.map((row) => within(row).getAllByRole('cell')[2].textContent?.trim());
    };

    // Default sort is Title ascending
    expect(getRowTitles()).toEqual(['Alpha Earliest', 'Middle Offset', 'Zulu Latest']);

    const dateAddedToolbarBtn = screen
      .getAllByRole('button', { name: /^Date Added$/i })
      .find((b) => b.hasAttribute('aria-pressed'));
    expect(dateAddedToolbarBtn).toBeDefined();
    await user.click(dateAddedToolbarBtn!);

    // First click on Date Added defaults to newest-first (desc)
    await waitFor(() => {
      expect(getRowTitles()).toEqual(['Zulu Latest', 'Middle Offset', 'Alpha Earliest']);
    });
    expect(dateAddedToolbarBtn).toHaveAttribute('aria-pressed', 'true');
    expect(observedSearches.at(-1)).toBe('?sort=added&dir=desc');
    expect(readPersistedCatalogFilterState()?.sortField).toBe('added');
    expect(readPersistedCatalogFilterState()?.sortAsc).toBe(false);

    // Second click toggles to oldest-first (asc)
    await user.click(dateAddedToolbarBtn!);

    await waitFor(() => {
      expect(getRowTitles()).toEqual(['Alpha Earliest', 'Middle Offset', 'Zulu Latest']);
    });
    expect(observedSearches.at(-1)).toBe('?sort=added');
    expect(readPersistedCatalogFilterState()?.sortField).toBe('added');
    expect(readPersistedCatalogFilterState()?.sortAsc).toBe(true);
  });

  it('persists sort aliases across URL search and sessionStorage reload', () => {
    const fromAlias = parseCatalogFilterStateFromSearch('?sort=date_added&dir=desc');
    expect(fromAlias.sortField).toBe('added');
    expect(fromAlias.sortAsc).toBe(false);

    const fromAsc = parseCatalogFilterStateFromSearch('?sort=added');
    expect(fromAsc.sortField).toBe('added');
    expect(fromAsc.sortAsc).toBe(true);

    window.sessionStorage.setItem(
      CATALOG_FILTER_STORAGE_KEY,
      JSON.stringify({ sortField: 'addedAt', sortAsc: false }),
    );
    const persisted = readPersistedCatalogFilterState();
    expect(persisted?.sortField).toBe('added');
    expect(persisted?.sortAsc).toBe(false);
  });

  it('renders <time datetime> cells and sets aria-sort only on active <th scope="col"> in Table view', async () => {
    const user = userEvent.setup();
    vi.stubGlobal('fetch', createSortMockFetch());
    window.localStorage.setItem('managerr.catalog.viewMode.v2', 'table');

    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/library']}>
          <HarnessCatalogView initialSearch="?sort=date_added&dir=desc" />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    const table = await screen.findByRole('table');
    const headers = within(table).getAllByRole('columnheader');

    const titleHeader = headers.find((h) => h.textContent?.includes('Title'))!;
    const yearHeader = headers.find((h) => h.textContent?.includes('Year'))!;
    const addedHeader = headers.find((h) => h.textContent?.includes('Date Added'))!;

    expect(titleHeader).toHaveAttribute('scope', 'col');
    expect(titleHeader).not.toHaveAttribute('aria-sort');
    expect(yearHeader).toHaveAttribute('scope', 'col');
    expect(yearHeader).not.toHaveAttribute('aria-sort');
    expect(addedHeader).toHaveAttribute('scope', 'col');
    expect(addedHeader).toHaveAttribute('aria-sort', 'descending');

    const timeEls = table.querySelectorAll('time[datetime]');
    expect(timeEls.length).toBe(3);
    expect(timeEls[0]).toHaveAttribute('datetime', '2026-11-14T12:37:37.6074613+00:00');
    expect(timeEls[0].textContent).toBe('2026-11-14');

    // Clicking the Date Added table header button toggles to ascending
    await user.click(within(addedHeader).getByRole('button', { name: /Date Added/i }));
    await waitFor(() => {
      expect(addedHeader).toHaveAttribute('aria-sort', 'ascending');
    });
    expect(titleHeader).not.toHaveAttribute('aria-sort');
  });
});
