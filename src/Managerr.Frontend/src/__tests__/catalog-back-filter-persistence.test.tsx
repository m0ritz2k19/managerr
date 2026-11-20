import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { BrowserRouter, MemoryRouter, useLocation } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { App } from '../App';
import {
  CATALOG_FILTER_STORAGE_KEY,
  writePersistedCatalogFilterState,
} from '../components/catalog/catalogFilterPersistence';

const FULL_ADMIN_SCOPES = [
  'audit:read',
  'catalog:auto_approve',
  'catalog:read',
  'catalog:request',
  'catalog:write',
  'downloaders:read',
  'downloaders:write',
  'indexers:read',
  'indexers:search',
  'indexers:write',
  'langverify:read',
  'migration:execute',
  'migration:read',
  'profiles:read',
  'profiles:write',
  'quarantine:review',
  'queue:manage',
  'queue:read',
  'roles:manage',
  'system:admin',
  'users:manage',
  'users:read',
];

function LocationProbe() {
  const loc = useLocation();
  return <div data-testid="location-probe">{loc.pathname + loc.search}</div>;
}

function createCatalogMockFetch() {
  return vi.fn(async (input: RequestInfo | URL) => {
    const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
    const url = new URL(rawUrl, 'http://localhost');
    const path = url.pathname;

    const json = (data: unknown, status = 200) =>
      new Response(JSON.stringify(data), {
        status,
        headers: { 'Content-Type': 'application/json' },
      });

    if (path === '/api/v1/auth/status') {
      return json({
        isClaimed: true,
        boundServerName: 'HomePlex',
        boundMachineId: 'machine-123',
        plexClientIdentifier: 'client-123',
        authMode: 'plex_primary_with_lan_breakglass',
      });
    }

    if (path === '/api/v1/auth/me') {
      return json({
        user: {
          id: 1,
          plexUserId: '1001',
          plexUuid: 'uuid-1001',
          username: 'admin',
          email: 'admin@example.com',
          thumbUrl: null,
          accountType: 'owner',
          isHomeUser: false,
          isAdmin: true,
          isActive: true,
          roles: ['Owner'],
        },
        csrfToken: 'csrf-test-token',
        scopes: FULL_ADMIN_SCOPES,
        isCookieSession: true,
        apiKeyPrefix: null,
      });
    }

    if (path === '/api/v1/libraries') {
      return json([
        {
          id: 1,
          name: 'Movies Lib',
          mediaType: 'movies',
          rootFolderPath: '/data/media/movies',
          plexSectionId: '1',
          defaultProfileId: 1,
          workCount: 1,
        },
        {
          id: 2,
          name: 'Anime Lib',
          mediaType: 'anime',
          rootFolderPath: '/data/media/anime',
          plexSectionId: '2',
          defaultProfileId: 1,
          workCount: 2,
        },
      ]);
    }

    if (path === '/api/v1/catalog/works') {
      return json([
        {
          id: 42,
          libraryId: 1,
          workKind: 'movies',
          title: 'Dune: Part Two',
          altTitles: [],
          originalLanguage: 'en',
          year: 2024,
          status: 'released',
          overview: 'Paul Atreides unites with Chani.',
          monitored: true,
          posterUrl: null,
          primaryProvider: 'tmdb',
          episodeOrderingScheme: 'aired',
          lockedFields: [],
          externalIds: { tmdb: '693134' },
          tags: ['live-action-movie'],
          containerCount: 1,
          unitCount: 1,
          fileCount: 1,
        },
        {
          id: 101,
          libraryId: 2,
          workKind: 'anime',
          title: 'KonoSuba',
          altTitles: ['Konosuba: Gods Blessing on This Wonderful World'],
          originalLanguage: 'ja',
          year: 2016,
          status: 'continuing',
          overview: 'Kazuma Sato is sent to a fantasy world.',
          monitored: true,
          posterUrl: null,
          primaryProvider: 'tvdb',
          episodeOrderingScheme: 'absolute',
          lockedFields: [],
          externalIds: { tvdb: '303157' },
          tags: ['anime-series'],
          containerCount: 2,
          unitCount: 20,
          fileCount: 10,
        },
        {
          id: 102,
          libraryId: 2,
          workKind: 'anime',
          title: 'Frieren: Beyond Journeys End',
          altTitles: [],
          originalLanguage: 'ja',
          year: 2023,
          status: 'ended',
          overview: 'Elf mage Frieren reflects on a long life.',
          monitored: false,
          posterUrl: null,
          primaryProvider: 'tvdb',
          episodeOrderingScheme: 'absolute',
          lockedFields: [],
          externalIds: { tvdb: '424536' },
          tags: ['anime-series'],
          containerCount: 1,
          unitCount: 28,
          fileCount: 28,
        },
      ]);
    }

    if (path === '/api/v1/catalog/works/101') {
      return json({
        work: {
          id: 101,
          libraryId: 2,
          workKind: 'anime',
          title: 'KonoSuba',
          altTitles: [],
          originalLanguage: 'ja',
          year: 2016,
          status: 'continuing',
          overview: 'Kazuma Sato is sent to a fantasy world.',
          posterUrl: null,
          monitored: true,
          lockedFields: [],
          primaryProvider: 'tvdb',
          episodeOrderingScheme: 'absolute',
          externalIds: { tvdb: '303157' },
          tags: ['anime-series'],
          containerCount: 1,
          unitCount: 20,
          fileCount: 10,
        },
        containers: [],
      });
    }

    if (path === '/api/v1/profiles') return json([]);
    if (path === '/api/v1/profiles/language-policies') return json([]);
    if (path === '/api/v1/system/info') {
      return json({
        version: '0.2.0',
        framework: '.NET 10.0',
        os: 'Linux',
        sqliteMode: 'WAL',
        dataDir: '/config',
        startedAtUtc: new Date().toISOString(),
        uptimeSeconds: 120,
      });
    }
    return json([]);
  });
}

describe('Catalog Filter Persistence Across Back Navigation', () => {
  beforeEach(() => {
    window.sessionStorage.clear();
    window.localStorage.clear();
    window.history.replaceState(null, '', '/library');
  });

  it('preserves all catalog filters, search text, classification, and sort order when clicking in-app Back (even after prior sidebar navigation)', async () => {
    const user = userEvent.setup();
    vi.stubGlobal('fetch', createCatalogMockFetch());

    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/library']}>
          <LocationProbe />
          <App />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    expect((await screen.findAllByText('Dune: Part Two')).length).toBeGreaterThan(0);
    expect(screen.getAllByText('KonoSuba').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Frieren: Beyond Journeys End').length).toBeGreaterThan(0);

    // Click sidebar "Catalog" once first so App's catalogResetNonce > 0 before filters are set
    const primaryNav = screen.getByRole('navigation', { name: 'Primary' });
    await user.click(within(primaryNav).getByRole('link', { name: 'Catalog' }));

    // Apply multiple filters: Anime vertical, Anime Lib, Monitored, Missing, Anime Series classification, text "Kono", and Year sort
    await user.click(screen.getByTitle('Toggle Anime filter (select multiple)'));
    await user.click(screen.getByTitle(/Toggle library 'Anime Lib'/i));
    await user.click(screen.getByRole('button', { name: /^Monitored \d+$/i }));
    await user.click(screen.getByRole('button', { name: /^Missing \d+$/i }));
    await user.selectOptions(screen.getByTitle('Filter library by media classification'), 'anime-series');

    const filterInput = screen.getByPlaceholderText('Filter library...');
    await user.type(filterInput, 'Kono');
    await user.click(screen.getByRole('button', { name: /^Year$/i }));

    await waitFor(() => {
      expect(screen.queryByText('Dune: Part Two')).not.toBeInTheDocument();
      expect(screen.queryByText('Frieren: Beyond Journeys End')).not.toBeInTheDocument();
      expect(screen.getAllByText('KonoSuba').length).toBeGreaterThan(0);
    });

    const filteredProbeText = screen.getByTestId('location-probe').textContent ?? '';
    expect(filteredProbeText).toContain('/library?');
    expect(filteredProbeText).toContain('libs=2');
    expect(filteredProbeText).toContain('types=anime');
    expect(filteredProbeText).toContain('mon=monitored');
    expect(filteredProbeText).toContain('avail=missing');
    expect(filteredProbeText).toContain('class=anime-series');
    expect(filteredProbeText).toContain('q=Kono');
    expect(filteredProbeText).toContain('sort=year');

    // Open KonoSuba work detail page (/works/101)
    await user.click(screen.getAllByText('KonoSuba')[0]);

    expect(await screen.findByRole('heading', { level: 1, name: /KonoSuba/i })).toBeInTheDocument();
    expect(screen.getByTestId('location-probe')).toHaveTextContent('/works/101');

    // Click in-app "Library" Back button
    const backButtons = screen.getAllByRole('button', { name: /^Library$/i });
    await user.click(backButtons[0]);

    await waitFor(() => {
      expect(screen.getByTestId('location-probe').textContent).toBe(filteredProbeText);
    });

    // Verify all filters, search text, classification, and filtered results remain intact
    expect(await screen.findByPlaceholderText('Filter library...')).toHaveValue('Kono');
    expect(screen.getByTitle('Filter library by media classification')).toHaveValue('anime-series');
    expect(screen.queryByText('Dune: Part Two')).not.toBeInTheDocument();
    expect(screen.queryByText('Frieren: Beyond Journeys End')).not.toBeInTheDocument();
    expect(screen.getAllByText('KonoSuba').length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: /Clear \(6\)/i })).toBeInTheDocument();

    // Explicitly clicking Clear resets all filters and strips query params (while keeping non-filter sort)
    await user.click(screen.getByRole('button', { name: /Clear \(6\)/i }));
    await waitFor(() => {
      expect(screen.getAllByText('Dune: Part Two').length).toBeGreaterThan(0);
      expect(screen.getAllByText('Frieren: Beyond Journeys End').length).toBeGreaterThan(0);
    });
    expect(screen.getByPlaceholderText('Filter library...')).toHaveValue('');
    expect(window.sessionStorage.getItem(CATALOG_FILTER_STORAGE_KEY)).toContain('"filterText":""');
  });

  it('preserves filters and restores scroll position on browser Back (popstate) from /works/:workId', async () => {
    const user = userEvent.setup();
    vi.stubGlobal('fetch', createCatalogMockFetch());
    const scrollToSpy = vi.fn();
    vi.stubGlobal('scrollTo', scrollToSpy);
    Object.defineProperty(window, 'scrollY', { value: 480, writable: true, configurable: true });

    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <BrowserRouter>
          <LocationProbe />
          <App />
        </BrowserRouter>
      </QueryClientProvider>,
    );

    expect((await screen.findAllByText('Dune: Part Two')).length).toBeGreaterThan(0);

    // Filter down to Anime + "Kono"
    await user.click(screen.getByTitle('Toggle Anime filter (select multiple)'));
    await user.type(screen.getByPlaceholderText('Filter library...'), 'Kono');

    await waitFor(() => {
      expect(window.location.search).toContain('types=anime');
      expect(window.location.search).toContain('q=Kono');
    });

    // Click into KonoSuba detail (/works/101)
    await user.click(screen.getAllByText('KonoSuba')[0]);
    expect(await screen.findByRole('heading', { level: 1, name: /KonoSuba/i })).toBeInTheDocument();
    expect(window.location.pathname).toBe('/works/101');

    // Simulate browser Back button
    await act(async () => {
      window.history.back();
      await new Promise((resolve) => setTimeout(resolve, 25));
    });

    await waitFor(() => {
      expect(window.location.pathname).toBe('/library');
      expect(window.location.search).toContain('types=anime');
      expect(window.location.search).toContain('q=Kono');
    });

    expect(await screen.findByPlaceholderText('Filter library...')).toHaveValue('Kono');
    expect(screen.queryByText('Dune: Part Two')).not.toBeInTheDocument();
    expect(screen.getAllByText('KonoSuba').length).toBeGreaterThan(0);
    expect(scrollToSpy).toHaveBeenCalledWith(expect.objectContaining({ top: 480, left: 0 }));
  });

  it('synthesizes /library?... from persisted session filters when clicking Back from a deep-linked /works/:workId URL', async () => {
    const user = userEvent.setup();
    vi.stubGlobal('fetch', createCatalogMockFetch());

    writePersistedCatalogFilterState({
      libraryIds: [2],
      verticals: ['anime'],
      monitorBubbles: [],
      availabilityBubbles: ['missing'],
      filterText: 'Kono',
      sortField: 'title',
      sortAsc: true,
      classificationFilter: 'all',
    });

    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/works/101']}>
          <LocationProbe />
          <App />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    expect(await screen.findByRole('heading', { level: 1, name: /KonoSuba/i })).toBeInTheDocument();

    const backButtons = screen.getAllByRole('button', { name: /^Library$/i });
    await user.click(backButtons[0]);

    await waitFor(() => {
      expect(screen.getByTestId('location-probe').textContent).toBe('/library?libs=2&types=anime&avail=missing&q=Kono');
    });
    expect(await screen.findByPlaceholderText('Filter library...')).toHaveValue('Kono');
    expect(screen.queryByText('Dune: Part Two')).not.toBeInTheDocument();
  });
});
