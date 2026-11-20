import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MigrationAndCompatView } from '../components/MigrationAndCompatView';

describe('MigrationAndCompatView - Multi-Row Path Mappings & Stage as Saved Source', () => {
  let queryClient: QueryClient;

  beforeEach(() => {
    queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false, gcTime: 0 },
        mutations: { retry: false },
      },
    });
  });

  it('renders multi-row path mapping editor, populates homelab defaults, and adds/removes rows', async () => {
    const user = userEvent.setup();

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const url = new URL(rawUrl, 'http://localhost');
      const json = (data: unknown, status = 200) =>
        new Response(JSON.stringify(data), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.pathname === '/api/v1/migration/sources') {
        return json([]);
      }
      return json({});
    });

    vi.stubGlobal('fetch', fetchMock);

    render(
      <QueryClientProvider client={queryClient}>
        <MigrationAndCompatView canExecute={true} />
      </QueryClientProvider>,
    );

    // Initial state: 1 mapping row with no remove button
    expect(screen.getByLabelText('Remote Path Prefix 1')).toHaveValue('');
    expect(screen.getByLabelText('Local Path Prefix 1')).toHaveValue('');
    expect(screen.queryByLabelText('Remove Mapping 1')).not.toBeInTheDocument();

    // Click "+ Add Mapping"
    const addBtn = screen.getByRole('button', { name: /\+ Add Mapping/i });
    await user.click(addBtn);

    // Now 2 rows exist, both have remove buttons
    expect(screen.getByLabelText('Remote Path Prefix 2')).toBeInTheDocument();
    expect(screen.getByLabelText('Remove Mapping 1')).toBeInTheDocument();
    expect(screen.getByLabelText('Remove Mapping 2')).toBeInTheDocument();

    // Click "Fill Homelab Defaults (/tv, /anime, /movies, /animemovs → /media/*)"
    const homelabBtn = screen.getByRole('button', {
      name: /Fill Homelab Defaults \(\/tv, \/anime, \/movies, \/animemovs → \/media\/\*\)/i,
    });
    await user.click(homelabBtn);

    // Verify the 4 homelab defaults are populated
    expect(screen.getByLabelText('Remote Path Prefix 1')).toHaveValue('/tv');
    expect(screen.getByLabelText('Local Path Prefix 1')).toHaveValue('/media/series');

    expect(screen.getByLabelText('Remote Path Prefix 2')).toHaveValue('/anime');
    expect(screen.getByLabelText('Local Path Prefix 2')).toHaveValue('/media/anime');

    expect(screen.getByLabelText('Remote Path Prefix 3')).toHaveValue('/movies');
    expect(screen.getByLabelText('Local Path Prefix 3')).toHaveValue('/media/movies');

    expect(screen.getByLabelText('Remote Path Prefix 4')).toHaveValue('/animemovs');
    expect(screen.getByLabelText('Local Path Prefix 4')).toHaveValue('/media/animefilme');

    // Remove row 4
    const removeBtn4 = screen.getByLabelText('Remove Mapping 4');
    await user.click(removeBtn4);

    expect(screen.queryByLabelText('Remote Path Prefix 4')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Remote Path Prefix 3')).toHaveValue('/movies');
  });

  it('stages Direct *arr instance as a Saved Source for Unified Plan (auto-probing and passing pathMappings)', async () => {
    const user = userEvent.setup();
    const calls: Array<{ method: string; path: string; body?: unknown }> = [];

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const url = new URL(rawUrl, 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      const body = init?.body ? JSON.parse(String(init.body)) : undefined;

      calls.push({ method, path: url.pathname, body });

      const json = (data: unknown, status = 200) =>
        new Response(JSON.stringify(data), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.pathname === '/api/v1/migration/sources' && method === 'GET') {
        return json([]);
      }

      if (url.pathname === '/api/v1/migration/arr-api/probe' && method === 'POST') {
        return json({
          ok: true,
          detectedKind: 'sonarr',
          appName: 'Sonarr',
          version: '4.0.0.748',
          instanceName: 'Sonarr Main',
          normalizedBaseUrl: 'http://sonarr:8989',
          rootFolders: ['/tv', '/anime'],
          catalogItemsCount: 42,
          mediaFilesCount: 520,
          indexersCount: 3,
          downloadClientsCount: 2,
          customFormatsCount: 5,
          blocklistCount: 1,
          message: 'OK',
        });
      }

      if (url.pathname === '/api/v1/migration/sources' && method === 'POST') {
        return json({
          id: 10,
          name: (body as Record<string, unknown>).name,
          sourceKind: (body as Record<string, unknown>).sourceKind,
          transportMode: (body as Record<string, unknown>).transportMode,
          fileOrUrl: (body as Record<string, unknown>).fileOrUrl,
          readarrSlotHint: (body as Record<string, unknown>).readarrSlotHint,
          pathMappings: (body as Record<string, unknown>).pathMappings,
          isEnabled: true,
        });
      }

      return json({});
    });

    vi.stubGlobal('fetch', fetchMock);

    render(
      <QueryClientProvider client={queryClient}>
        <MigrationAndCompatView canExecute={true} />
      </QueryClientProvider>,
    );

    // Populate Homelab Defaults
    const homelabBtn = screen.getByRole('button', {
      name: /Fill Homelab Defaults \(\/tv, \/anime, \/movies, \/animemovs → \/media\/\*\)/i,
    });
    await user.click(homelabBtn);

    // Set *arr API Key
    const keyInput = screen.getByPlaceholderText(/e\.g\. 4d7a8e2b/i);
    await user.type(keyInput, 'test-secret-key-123');

    // Click "Save Source for Unified Plan"
    const saveSourceBtn = screen.getByRole('button', { name: /Save Source for Unified Plan/i });
    await user.click(saveSourceBtn);

    // Verify auto-probe was called first
    await waitFor(() => {
      expect(calls.some((c) => c.path === '/api/v1/migration/arr-api/probe')).toBe(true);
    });

    // Verify POST /api/v1/migration/sources was called with correct payload
    await waitFor(() => {
      const sourcePost = calls.find(
        (c) => c.path === '/api/v1/migration/sources' && c.method === 'POST',
      );
      expect(sourcePost).toBeDefined();
      expect(sourcePost?.body).toEqual({
        name: 'Sonarr',
        sourceKind: 'sonarr',
        transportMode: 'live_api',
        fileOrUrl: 'http://sonarr:8989',
        apiKey: 'test-secret-key-123',
        readarrSlotHint: 'auto',
        pathMappings: {
          '/tv': '/media/series',
          '/anime': '/media/anime',
          '/movies': '/media/movies',
          '/animemovs': '/media/animefilme',
        },
        isEnabled: true,
      });
    });

    // Verify confirmation notice
    await waitFor(() => {
      expect(
        screen.getByText(
          "Saved 'Sonarr' to Migration Sources — add Radarr/Sonarr and click '1. Dry-Run Diff' below to preview the combined cutover!",
        ),
      ).toBeInTheDocument();
    });
  });

  it('supports configuring multi-row path mappings in the Add Migration Source modal', async () => {
    const user = userEvent.setup();
    const calls: Array<{ method: string; path: string; body?: unknown }> = [];

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const url = new URL(rawUrl, 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      const body = init?.body ? JSON.parse(String(init.body)) : undefined;

      calls.push({ method, path: url.pathname, body });

      const json = (data: unknown, status = 200) =>
        new Response(JSON.stringify(data), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.pathname === '/api/v1/migration/sources') {
        if (method === 'GET') return json([]);
        return json({
          id: 11,
          ...body,
        });
      }

      return json({});
    });

    vi.stubGlobal('fetch', fetchMock);

    render(
      <QueryClientProvider client={queryClient}>
        <MigrationAndCompatView canExecute={true} />
      </QueryClientProvider>,
    );

    // Open "Add Source" modal via Toolbar
    const addSourceBtn = screen.getByRole('button', { name: 'Add Source' });
    await user.click(addSourceBtn);

    // Verify modal is displayed
    expect(screen.getByRole('dialog')).toBeInTheDocument();

    // Click "Fill Homelab Defaults" in modal
    const modalHomelabBtn = screen.getByRole('button', { name: 'Fill Homelab Defaults' });
    await user.click(modalHomelabBtn);

    expect(screen.getByLabelText('Source Path Mapping From 1')).toHaveValue('/tv');
    expect(screen.getByLabelText('Source Path Mapping To 1')).toHaveValue('/media/series');

    // Click "Save" in modal footer
    const saveBtn = screen.getByRole('button', { name: 'Save' });
    await user.click(saveBtn);

    // Verify POST /api/v1/migration/sources passed pathMappings
    await waitFor(() => {
      const postCall = calls.find((c) => c.path === '/api/v1/migration/sources' && c.method === 'POST');
      expect(postCall).toBeDefined();
      expect((postCall?.body as Record<string, unknown>).pathMappings).toEqual({
        '/tv': '/media/series',
        '/anime': '/media/anime',
        '/movies': '/media/movies',
        '/animemovs': '/media/animefilme',
      });
    });

    // Re-open "Add Source" modal and verify fields were reset
    await user.click(addSourceBtn);
    const modal = screen.getByRole('dialog');
    const modalScope = within(modal);
    expect(modalScope.getByPlaceholderText(/Optional for offline \.db|e\.g\. 4d7a8e2b/i)).toHaveValue('');
    expect(modalScope.getByLabelText('Source Path Mapping From 1')).toHaveValue('');
  });

  it('stages two sources back-to-back via Save Source for Unified Plan without reusing stale probe cache', async () => {
    const user = userEvent.setup();
    const calls: Array<{ method: string; path: string; body?: Record<string, unknown> }> = [];

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const url = new URL(rawUrl, 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      const body = init?.body ? JSON.parse(String(init.body)) : undefined;

      calls.push({ method, path: url.pathname, body });

      const json = (data: unknown, status = 200) =>
        new Response(JSON.stringify(data), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.pathname === '/api/v1/migration/sources' && method === 'GET') {
        return json([]);
      }

      if (url.pathname === '/api/v1/migration/arr-api/probe' && method === 'POST') {
        if (body.baseUrl.includes('radarr')) {
          return json({
            ok: true,
            detectedKind: 'radarr',
            appName: 'Radarr',
            version: '5.2.6.8710',
            instanceName: 'Radarr 4K',
            normalizedBaseUrl: 'https://radarr.example.com',
            rootFolders: ['/movies', '/animemovs'],
            catalogItemsCount: 88,
            mediaFilesCount: 85,
            indexersCount: 2,
            downloadClientsCount: 1,
            customFormatsCount: 8,
            blocklistCount: 0,
            message: 'OK',
          });
        }
        return json({
          ok: true,
          detectedKind: 'sonarr',
          appName: 'Sonarr',
          version: '4.0.0.748',
          instanceName: 'Sonarr Main',
          normalizedBaseUrl: 'http://sonarr:8989',
          rootFolders: ['/tv', '/anime'],
          catalogItemsCount: 42,
          mediaFilesCount: 520,
          indexersCount: 3,
          downloadClientsCount: 2,
          customFormatsCount: 5,
          blocklistCount: 1,
          message: 'OK',
        });
      }

      if (url.pathname === '/api/v1/migration/sources' && method === 'POST') {
        return json({
          id: calls.filter((c) => c.path === '/api/v1/migration/sources' && c.method === 'POST').length,
          name: body.name,
          sourceKind: body.sourceKind,
          transportMode: body.transportMode,
          fileOrUrl: body.fileOrUrl,
          readarrSlotHint: body.readarrSlotHint,
          pathMappings: body.pathMappings,
          isEnabled: true,
        });
      }

      return json({});
    });

    vi.stubGlobal('fetch', fetchMock);

    render(
      <QueryClientProvider client={queryClient}>
        <MigrationAndCompatView canExecute={true} />
      </QueryClientProvider>,
    );

    // 1. First source: Sonarr
    const urlInput = screen.getByPlaceholderText('http://sonarr:8989');
    const keyInput = screen.getByPlaceholderText(/e\.g\. 4d7a8e2b/i);

    await user.clear(keyInput);
    await user.type(keyInput, 'sonarr-key-111');

    const saveSourceBtn = screen.getByRole('button', { name: /Save Source for Unified Plan/i });
    await user.click(saveSourceBtn);

    // Verify Sonarr probe and source save
    await waitFor(() => {
      const sonarrSourcePost = calls.find(
        (c) => c.path === '/api/v1/migration/sources' && c.method === 'POST' && c.body?.sourceKind === 'sonarr',
      );
      expect(sonarrSourcePost).toBeDefined();
      expect(sonarrSourcePost?.body).toMatchObject({
        sourceKind: 'sonarr',
        fileOrUrl: 'http://sonarr:8989',
        apiKey: 'sonarr-key-111',
      });
    });

    // 2. Second source: change URL to Radarr and key to Radarr's key
    await user.clear(urlInput);
    await user.type(urlInput, 'https://radarr.example.com');
    await user.clear(keyInput);
    await user.type(keyInput, 'radarr-key-999');

    // Click "Save Source for Unified Plan" again
    await user.click(saveSourceBtn);

    // Verify Radarr was re-probed and POSTed correctly (NOT Sonarr's stale probe!)
    await waitFor(() => {
      const probeCalls = calls.filter((c) => c.path === '/api/v1/migration/arr-api/probe');
      expect(probeCalls.length).toBe(2);
      expect(probeCalls[1].body?.baseUrl).toBe('https://radarr.example.com');

      const radarrSourcePost = calls.find(
        (c) => c.path === '/api/v1/migration/sources' && c.method === 'POST' && c.body?.sourceKind === 'radarr',
      );
      expect(radarrSourcePost).toBeDefined();
      expect(radarrSourcePost?.body).toMatchObject({
        sourceKind: 'radarr',
        fileOrUrl: 'https://radarr.example.com',
        apiKey: 'radarr-key-999',
      });
    });
  });
});
