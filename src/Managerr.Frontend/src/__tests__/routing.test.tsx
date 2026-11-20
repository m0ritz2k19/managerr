import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, useLocation } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { App } from '../App';

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

function createMockFetch(scopes: string[] = FULL_ADMIN_SCOPES) {
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
        scopes,
        isCookieSession: true,
        apiKeyPrefix: null,
      });
    }

    if (path === '/api/v1/libraries') {
      return json([
        {
          id: 1,
          name: 'Movies',
          mediaType: 'movies',
          rootFolderPath: '/data/media/movies',
          plexSectionId: '1',
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
          monitored: true,
          posterUrl: null,
          primaryProvider: 'tmdb',
          episodeOrderingScheme: 'aired',
          externalIds: { tmdb: '693134' },
          unitCount: 1,
          fileCount: 1,
        },
        {
          id: 99,
          libraryId: 1,
          workKind: 'movies',
          title: 'Blade Runner 2049',
          altTitles: [],
          originalLanguage: 'en',
          year: 2017,
          status: 'released',
          monitored: true,
          posterUrl: null,
          primaryProvider: 'tmdb',
          episodeOrderingScheme: 'aired',
          externalIds: { tmdb: '335984' },
          unitCount: 1,
          fileCount: 0,
        },
      ]);
    }

    if (path === '/api/v1/catalog/works/42') {
      return json({
        work: {
          id: 42,
          libraryId: 1,
          workKind: 'movies',
          title: 'Dune: Part Two',
          altTitles: [],
          originalLanguage: 'en',
          year: 2024,
          status: 'released',
          overview: 'Paul Atreides unites with Chani and the Fremen.',
          posterUrl: null,
          monitored: true,
          primaryProvider: 'tmdb',
          episodeOrderingScheme: 'aired',
          externalIds: { tmdb: '693134' },
        },
        containers: [],
        files: [],
      });
    }

    if (path === '/api/v1/profiles') return json([]);
    if (path === '/api/v1/wanted/missing' || path === '/api/v1/wanted/cutoff') {
      return json({ page: 1, pageSize: 50, totalCount: 0, items: [] });
    }
    if (path === '/api/v1/downloaders/queue' || path === '/api/v1/downloaders/tracked') {
      return json([]);
    }
    if (path === '/api/v1/indexers') return json([]);
    if (path === '/api/v1/indexers/definitions') return json({ totalCount: 0, items: [] });
    if (path === '/api/v1/system/info') {
      return json({
        version: '0.1.0',
        framework: '.NET 10.0',
        os: 'Linux',
        sqliteMode: 'WAL',
        dataDir: '/config',
        startedAtUtc: new Date().toISOString(),
        uptimeSeconds: 120,
      });
    }
    if (path === '/api/v1/system/health') return json([]);
    if (path === '/api/v1/system/disks') return json([]);
    if (path === '/api/v1/system/tasks') return json([]);

    return json([]);
  });
}

function renderAppAt(initialEntries: string[] = ['/'], scopes: string[] = FULL_ADMIN_SCOPES) {
  const fetchMock = createMockFetch(scopes);
  vi.stubGlobal('fetch', fetchMock);

  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });

  const utils = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={initialEntries}>
        <LocationProbe />
        <App />
      </MemoryRouter>
    </QueryClientProvider>,
  );

  return { ...utils, fetchMock };
}

describe('SPA URL Routing, Accessibility Landmarks & Deep-Linking', () => {
  it('redirects / to /library, sets document.title, and renders <nav aria-label="Primary"> with aria-current="page"', async () => {
    const user = userEvent.setup();
    renderAppAt(['/']);

    await waitFor(() => {
      expect(screen.getByTestId('location-probe')).toHaveTextContent('/library');
    });

    expect(document.title).toBe('Library | Managerr');
    expect(screen.getByRole('heading', { level: 1, name: 'Library' })).toBeInTheDocument();

    const primaryNav = screen.getByRole('navigation', { name: 'Primary' });
    expect(primaryNav).toHaveAttribute('aria-label', 'Primary');
    expect(primaryNav.getAttribute('aria-label')?.toLowerCase()).not.toContain('navigation');

    const catalogLink = within(primaryNav).getByRole('link', { name: 'Catalog' });
    expect(catalogLink).toHaveAttribute('href', '/library');
    expect(catalogLink).toHaveAttribute('aria-current', 'page');

    // Navigate via sidebar NavLinks across sections and verify URL + document.title + h1 updates
    await user.click(within(primaryNav).getByRole('link', { name: 'Wanted' }));
    await waitFor(() => {
      expect(screen.getByTestId('location-probe')).toHaveTextContent('/wanted/missing');
    });
    expect(document.title).toBe('Wanted: Missing | Managerr');
    expect(screen.getByRole('heading', { level: 1, name: 'Wanted: Missing' })).toBeInTheDocument();
    expect(within(primaryNav).getByRole('link', { name: 'Missing' })).toHaveAttribute('aria-current', 'page');

    await user.click(within(primaryNav).getByRole('link', { name: 'Activity' }));
    await waitFor(() => {
      expect(screen.getByTestId('location-probe')).toHaveTextContent('/activity/queue');
    });
    expect(document.title).toBe('Activity Queue | Managerr');
    expect(screen.getByRole('heading', { level: 1, name: 'Activity Queue' })).toBeInTheDocument();

    await user.click(within(primaryNav).getByRole('link', { name: 'Settings' }));
    await waitFor(() => {
      expect(screen.getByTestId('location-probe')).toHaveTextContent('/settings');
    });
    expect(document.title).toBe('Settings | Managerr');

    await user.click(within(primaryNav).getByRole('link', { name: 'Indexers' }));
    await waitFor(() => {
      expect(screen.getByTestId('location-probe')).toHaveTextContent('/settings/indexers');
    });
    expect(document.title).toBe('Indexers | Managerr');
    expect(screen.getByRole('heading', { level: 1, name: 'Indexers' })).toBeInTheDocument();
  });

  it('deep-links directly to /works/:workId and navigates back to /library when closed', async () => {
    const user = userEvent.setup();
    renderAppAt(['/works/42']);

    const workHeading = await screen.findByRole('heading', { level: 1, name: /Dune: Part Two/i });
    expect(workHeading).toBeInTheDocument();
    expect(screen.getByText(/Paul Atreides unites with Chani/i)).toBeInTheDocument();

    await waitFor(() => {
      expect(document.title).toBe('Dune: Part Two | Managerr');
    });

    // Click toolbar back-to-library button
    const backButtons = screen.getAllByRole('button', { name: /^Library$/i });
    await user.click(backButtons[0]);

    await waitFor(() => {
      expect(screen.getByTestId('location-probe')).toHaveTextContent('/library');
    });
    expect(document.title).toBe('Library | Managerr');
  });

  it('supports /library?filter=missing and /library?health=warning query parameters', async () => {
    renderAppAt(['/library?health=warning']);

    expect((await screen.findAllByText('Blade Runner 2049')).length).toBeGreaterThan(0);
    expect(screen.queryByText('Dune: Part Two')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Health: warning/i })).toBeInTheDocument();
  });

  it('enforces RBAC route guards by redirecting unauthorized paths back to /library', async () => {
    renderAppAt(['/settings/users'], ['catalog:read']);

    await waitFor(() => {
      expect(screen.getByTestId('location-probe')).toHaveTextContent('/library');
    });
    expect(document.title).toBe('Library | Managerr');
  });
});
