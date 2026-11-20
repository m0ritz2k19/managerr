import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AcquisitionView } from '../components/AcquisitionView';

describe('AcquisitionView Indexer & Download Client Forms', () => {
  it('validates, tests, and saves a Torznab indexer via the Indexer modal', async () => {
    const user = userEvent.setup();
    const mutations: Array<{ method: string; path: string; body: Record<string, unknown> }> = [];

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const url = new URL(rawUrl, 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      const json = (data: unknown, status = 200) =>
        new Response(JSON.stringify(data), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (method !== 'GET' && init?.body) {
        mutations.push({
          method,
          path: url.pathname,
          body: JSON.parse(String(init.body)) as Record<string, unknown>,
        });
      }

      if (url.pathname === '/api/v1/indexers' && method === 'GET') return json([]);
      if (url.pathname === '/api/v1/indexers/definitions') return json({ totalCount: 12, items: [] });
      if (url.pathname === '/api/v1/indexers/test' && method === 'POST') {
        return json({ ok: true, message: 'Connected cleanly to Torznab feed.' });
      }
      if (url.pathname === '/api/v1/indexers' && method === 'POST') {
        return json({ id: 7 }, 201);
      }

      return json([]);
    });

    vi.stubGlobal('fetch', fetchMock);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <AcquisitionView
          section="indexers"
          canReadIndexers={true}
          canWriteIndexers={true}
          canReadDownloaders={true}
          canWriteDownloaders={true}
        />
      </QueryClientProvider>,
    );

    const addIndexerBtns = await screen.findAllByRole('button', { name: /^Add Indexer$/i });
    await user.click(addIndexerBtns[0]);

    expect(screen.getByRole('button', { name: /Newznab/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Torznab/i })).toBeInTheDocument();
    expect(screen.queryByText(/Cardigann/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/Bundled Definitions/i)).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Newznab/i }));

    expect(screen.getByRole('heading', { level: 2, name: /Add Newznab Indexer/i })).toBeInTheDocument();

    const saveBtn = screen.getByRole('button', { name: /^Save$/i });
    expect(saveBtn).toBeDisabled();

    await user.type(screen.getByLabelText(/^Name$/i), 'Prowlarr Torznab');

    const torznabRadio = screen.getByRole('radio', { name: /Torznab \(Torrent \/ Magnet\)/i });
    await user.click(torznabRadio);
    expect(torznabRadio).toBeChecked();
    expect(screen.getByRole('heading', { level: 2, name: /Add Torznab Indexer/i })).toBeInTheDocument();

    await user.type(screen.getByLabelText(/^URL$/i), 'http://prowlarr.local:9696/1/api');
    await user.type(screen.getByLabelText(/^API Key$/i), 'secret-torznab-key');

    await user.click(screen.getByRole('button', { name: /^Test$/i }));
    expect(await screen.findByText('Connected cleanly to Torznab feed.')).toBeInTheDocument();

    expect(saveBtn).toBeEnabled();
    await user.click(saveBtn);

    await waitFor(() => {
      const postCall = mutations.find((m) => m.method === 'POST' && m.path === '/api/v1/indexers');
      expect(postCall).toBeDefined();
      expect(postCall!.body).toMatchObject({
        name: 'Prowlarr Torznab',
        implementation: 'torznab',
        protocol: 'torrent',
        baseUrl: 'http://prowlarr.local:9696/1/api',
        apiKey: 'secret-torznab-key',
      });
    });
  });

  it('selects a driver and submits Download Client configuration', async () => {
    const user = userEvent.setup();
    const mutations: Array<{ method: string; path: string; body: Record<string, unknown> }> = [];

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const url = new URL(rawUrl, 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      const json = (data: unknown, status = 200) =>
        new Response(JSON.stringify(data), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (method !== 'GET' && init?.body) {
        mutations.push({
          method,
          path: url.pathname,
          body: JSON.parse(String(init.body)) as Record<string, unknown>,
        });
      }

      if (url.pathname === '/api/v1/downloaders/drivers') {
        return json({
          drivers: [
            {
              implementation: 'qbittorrent',
              displayName: 'qBittorrent',
              protocol: 'torrent',
              defaultPort: 8080,
              authMode: 'credentials',
              description: 'qBittorrent WebUI v2',
            },
          ],
          defaultCategoryMappings: { movies: 'movies-managerr' },
        });
      }
      if (url.pathname === '/api/v1/downloaders' && method === 'GET') return json([]);
      if (url.pathname === '/api/v1/downloaders/queue') return json([]);
      if (url.pathname === '/api/v1/downloaders/imports') return json([]);
      if (url.pathname === '/api/v1/downloaders/tracked') return json([]);
      if (url.pathname === '/api/v1/downloaders' && method === 'POST') return json({ id: 3 }, 201);

      return json([]);
    });

    vi.stubGlobal('fetch', fetchMock);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <AcquisitionView
          section="downloaders"
          canReadIndexers={true}
          canWriteIndexers={true}
          canReadDownloaders={true}
          canWriteDownloaders={true}
        />
      </QueryClientProvider>,
    );

    await user.click(await screen.findByRole('button', { name: /^Add Client$/i }));
    await user.click(await screen.findByRole('button', { name: /qBittorrent/i }));

    expect(
      screen.getByRole('heading', { level: 2, name: /Add Download Client — qBittorrent/i }),
    ).toBeInTheDocument();

    const hostInput = screen.getByPlaceholderText('localhost');
    await user.clear(hostInput);
    await user.type(hostInput, '10.0.0.45');

    await user.type(screen.getByPlaceholderText(/Optional username/i), 'qbadmin');
    await user.type(screen.getByPlaceholderText('Optional password'), 'qbpass123');
    await user.click(screen.getByLabelText(/Connect via HTTPS/i));

    await user.click(screen.getByRole('button', { name: /^Save$/i }));

    await waitFor(() => {
      const postCall = mutations.find((m) => m.method === 'POST' && m.path === '/api/v1/downloaders');
      expect(postCall).toBeDefined();
      expect(postCall!.body).toMatchObject({
        name: 'qBittorrent',
        implementation: 'qbittorrent',
        host: '10.0.0.45',
        port: 8080,
        useSsl: true,
        username: 'qbadmin',
        password: 'qbpass123',
      });
    });
  });

  it('displays regional Newznab categories for SceneNZBs and allows adding custom numeric category IDs', async () => {
    const user = userEvent.setup();
    const mutations: Array<{ method: string; path: string; body: Record<string, unknown> }> = [];

    const mockIndexers = [
      {
        id: 42,
        name: 'SceneNZBs',
        implementation: 'newznab',
        definitionId: null,
        protocol: 'usenet',
        baseUrl: 'https://scenenzbs.com/api',
        apiKeySecret: { isSet: true, maskedHint: '****' },
        categories: [2000, 2100, 5000, 5030, 5100, 5140, 7100, 8000],
        tags: [],
        priority: 25,
        rssEnabled: true,
        interactiveSearchEnabled: true,
        autoSearchEnabled: true,
        requestDelayMs: 0,
        isHealthy: true,
        consecutiveFailures: 0,
        lastError: null,
      },
    ];

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const url = new URL(rawUrl, 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      const json = (data: unknown, status = 200) =>
        new Response(JSON.stringify(data), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (method !== 'GET' && init?.body) {
        mutations.push({
          method,
          path: url.pathname,
          body: JSON.parse(String(init.body)) as Record<string, unknown>,
        });
      }

      if (url.pathname === '/api/v1/indexers' && method === 'GET') return json(mockIndexers);
      if (url.pathname === '/api/v1/indexers/definitions') return json({ totalCount: 0, items: [] });
      if (url.pathname === '/api/v1/indexers/42' && method === 'PUT') return json({ id: 42 });

      return json([]);
    });

    vi.stubGlobal('fetch', fetchMock);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <AcquisitionView
          section="indexers"
          canReadIndexers={true}
          canWriteIndexers={true}
          canReadDownloaders={true}
          canWriteDownloaders={true}
        />
      </QueryClientProvider>,
    );

    // Click SceneNZBs settings card to open edit modal
    const sceneCard = await screen.findByText('SceneNZBs');
    await user.click(sceneCard);

    expect(await screen.findByRole('heading', { level: 2, name: /Edit Indexer — SceneNZBs/i })).toBeInTheDocument();

    // Verify regional category chips are visible
    expect(screen.getByRole('button', { name: 'Movies DE (2100)' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'TV SD (5030)' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'TV DE (5100)' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'TV HD DE (5140)' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Books DE (7100)' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Other (8000)' })).toBeInTheDocument();

    // Add a custom category ID: 5170 (Anime DE)
    const customInput = screen.getByLabelText(/Custom Category ID/i);
    await user.type(customInput, '5170');
    await user.click(screen.getByRole('button', { name: /\+ Add Category/i }));

    // Now Anime DE (5170) chip should appear
    expect(await screen.findByRole('button', { name: 'Anime DE (5170)' })).toBeInTheDocument();

    // Toggle off TV SD (5030)
    await user.click(screen.getByRole('button', { name: 'TV SD (5030)' }));

    // Save indexer
    const saveBtn = screen.getByRole('button', { name: /^Save$/i });
    await user.click(saveBtn);

    await waitFor(() => {
      const putCall = mutations.find((m) => m.method === 'PUT' && m.path === '/api/v1/indexers/42');
      expect(putCall).toBeDefined();
      const savedCategories = putCall!.body.categories as number[];
      expect(savedCategories).toContain(2100);
      expect(savedCategories).toContain(5100);
      expect(savedCategories).toContain(5140);
      expect(savedCategories).toContain(5170); // added custom
      expect(savedCategories).toContain(7100);
      expect(savedCategories).toContain(8000);
      expect(savedCategories).not.toContain(5030); // toggled off
    });
  });
});
