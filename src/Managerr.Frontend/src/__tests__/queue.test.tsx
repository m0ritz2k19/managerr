import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ActivityQueueAndHistoryView } from '../components/ActivityQueueAndHistoryView';

describe('ActivityQueueAndHistoryView Queue Actions', () => {
  it('triggers pause, resume, and remove modal DELETE calls with expected query options', async () => {
    const user = userEvent.setup();
    const calls: Array<{ method: string; path: string; search: string; body: unknown }> = [];

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const url = new URL(rawUrl, 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      const parsedBody = init?.body ? JSON.parse(String(init.body)) : null;

      calls.push({
        method,
        path: url.pathname,
        search: url.search,
        body: parsedBody,
      });

      const json = (data: unknown, status = 200) =>
        new Response(JSON.stringify(data), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.pathname === '/api/v1/downloaders/queue' && method === 'GET') {
        return json([
          {
            clientId: 1,
            clientName: 'SABnzbd',
            protocol: 'usenet',
            externalId: 'nzo_alpha',
            title: 'Dune.Part.Two.2024.German.DL.2160p.Remux.mkv',
            category: 'movies',
            mediaVertical: 'movies',
            status: 'Downloading',
            totalBytes: 40_000_000_000,
            remainingBytes: 10_000_000_000,
            outputPath: null,
            errorMessage: null,
            isStalled: false,
            isMetadataStuck: false,
            infoHash: 'abc123hash',
          },
          {
            clientId: 2,
            clientName: 'qBittorrent',
            protocol: 'torrent',
            externalId: 'hash_beta',
            title: 'Severance.S02E01.1080p.WEB-DL.mkv',
            category: 'tv',
            mediaVertical: 'tv',
            status: 'PausedDL',
            totalBytes: 4_000_000_000,
            remainingBytes: 2_000_000_000,
            outputPath: null,
            errorMessage: null,
            isStalled: false,
            isMetadataStuck: false,
            infoHash: 'hash_beta',
          },
        ]);
      }

      if (url.pathname === '/api/v1/downloaders/tracked' && method === 'GET') {
        return json([]);
      }

      if (url.pathname.endsWith('/control') && method === 'POST') {
        return new Response(null, { status: 204 });
      }

      if (url.pathname.startsWith('/api/v1/downloaders/queue/') && method === 'DELETE') {
        return new Response(null, { status: 204 });
      }

      return json([]);
    });

    vi.stubGlobal('fetch', fetchMock);

    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <ActivityQueueAndHistoryView section="queue" canManageQueue={true} canReadQueue={true} />
      </QueryClientProvider>,
    );

    expect(await screen.findByText('SABnzbd')).toBeInTheDocument();
    expect(screen.getByText('qBittorrent')).toBeInTheDocument();

    // 1. Pause active item (clientId=1, externalId=nzo_alpha)
    await user.click(screen.getByRole('button', { name: 'Pause download' }));
    await waitFor(() => {
      expect(calls).toContainEqual({
        method: 'POST',
        path: '/api/v1/downloaders/queue/1/nzo_alpha/control',
        search: '',
        body: { action: 'pause' },
      });
    });

    // 2. Resume paused item (clientId=2, externalId=hash_beta)
    await user.click(screen.getByRole('button', { name: 'Resume download' }));
    await waitFor(() => {
      expect(calls).toContainEqual({
        method: 'POST',
        path: '/api/v1/downloaders/queue/2/hash_beta/control',
        search: '',
        body: { action: 'resume' },
      });
    });

    // 3. Remove active item with blocklist + replacement search options
    const removeButtons = screen.getAllByRole('button', { name: 'Remove from queue' });
    await user.click(removeButtons[0]);

    expect(await screen.findByRole('heading', { level: 2, name: 'Remove from Queue' })).toBeInTheDocument();
    await user.click(screen.getByLabelText(/Add Release Hash to Recurring Blocklist/i));
    await user.click(screen.getByLabelText(/Automatically Search Replacement Release/i));
    await user.click(screen.getByRole('button', { name: 'Confirm Remove' }));

    await waitFor(() => {
      const deleteCall = calls.find(
        (c) => c.method === 'DELETE' && c.path === '/api/v1/downloaders/queue/1/nzo_alpha',
      );
      expect(deleteCall).toBeDefined();
      const params = new URLSearchParams(deleteCall!.search);
      expect(params.get('removeFromClient')).toBe('true');
      expect(params.get('deleteData')).toBe('false');
      expect(params.get('blocklist')).toBe('true');
      expect(params.get('searchReplacement')).toBe('true');
      expect(params.get('infoHash')).toBe('abc123hash');
    });
  });
});
