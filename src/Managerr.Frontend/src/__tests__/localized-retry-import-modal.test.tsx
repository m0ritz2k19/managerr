import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ActivityQueueAndHistoryView } from '../components/ActivityQueueAndHistoryView';
import { AcquisitionView } from '../components/AcquisitionView';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('Localized Retry & Manual Override Import Modals', () => {
  it('ActivityQueueAndHistoryView pre-selects work, leaves episodes empty, stays open on ImportBlocked, and closes on VerifiedPass', async () => {
    const user = userEvent.setup();
    const retryCalls: Array<{ path: string; body: Record<string, unknown> }> = [];
    let attempt = 0;

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const url = new URL(rawUrl, 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
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
            externalId: 'nzo-solar-701',
            title: 'Sonnenwanderer.German.2000.DVDRiP.XviD-GRP',
            category: 'movies-managerr',
            mediaVertical: 'movies',
            status: 'Completed',
            totalBytes: 734_003_200,
            remainingBytes: 0,
            outputPath: '/downloads/movies-managerr/Sonnenwanderer.German.2000.DVDRiP.XviD-GRP.1',
            errorMessage: null,
            isStalled: false,
            isMetadataStuck: false,
            infoHash: 'nzo:nzo-solar-701',
          },
        ]);
      }

      if (url.pathname === '/api/v1/downloaders/tracked' && method === 'GET') {
        return json([
          {
            id: 701,
            downloadClientId: 1,
            clientName: 'SABnzbd',
            externalDownloadId: 'nzo-solar-701',
            protocol: 'usenet',
            infoHash: 'nzo:nzo-solar-701',
            title: 'Sonnenwanderer.German.2000.DVDRiP.XviD-GRP',
            mediaVertical: 'movies',
            workId: 846,
            workTitle: 'Solar Drifters',
            unitIds: [33856],
            state: 'ImportBlocked',
            trackedStatus: 'warning',
            statusMessages: ["Download correlated to 'Solar Drifters' via GrabHistory..."],
            outputPath: '/downloads/movies-managerr/Sonnenwanderer.German.2000.DVDRiP.XviD-GRP.1',
            quarantineId: null,
            canMoveFiles: true,
            firstSeenAt: '2026-11-18T12:00:00Z',
            updatedAt: '2026-11-18T12:05:00Z',
          },
        ]);
      }

      if (url.pathname === '/api/v1/catalog/works' && method === 'GET') {
        return json([
          {
            id: 846,
            title: 'Solar Drifters',
            year: 2000,
            workKind: 'movies',
            monitored: true,
          },
        ]);
      }

      if (url.pathname === '/api/v1/downloaders/tracked/701/retry-import' && method === 'POST') {
        const body = init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : {};
        retryCalls.push({ path: url.pathname, body });
        attempt += 1;

        if (attempt === 1) {
          return json({
            verdict: 'ImportBlocked',
            message: 'Policy rejected audio language.',
          });
        }

        return json({
          verdict: 'VerifiedPass',
          destinationPath: '/media/movies/Solar Drifters (2000)/Solar Drifters (2000).avi',
          message: 'Imported',
        });
      }

      return json([]);
    });

    vi.stubGlobal('fetch', fetchMock);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <ActivityQueueAndHistoryView section="queue" canManageQueue={true} canReadQueue={true} />
      </QueryClientProvider>,
    );

    const openRetryBtn = await screen.findByRole('button', { name: /^Retry Import$/i });
    await user.click(openRetryBtn);

    expect(
      await screen.findByRole('heading', {
        level: 2,
        name: /Retry \/ Override Import — Sonnenwanderer\.German\.2000\.DVDRiP\.XviD-GRP/i,
      }),
    ).toBeInTheDocument();

    const workSelect = screen.getByLabelText(/Assign to Work:/i) as HTMLSelectElement;
    const episodesInput = screen.getByLabelText(/Episode Numbers:/i) as HTMLInputElement;
    const bypassCheckbox = screen.getByLabelText(
      /Bypass Quality \/ Language Cutoff Upgrade Rejections/i,
    ) as HTMLInputElement;

    await waitFor(() => {
      expect(workSelect.value).toBe('846');
    });
    expect(episodesInput.value).toBe('');

    expect(bypassCheckbox).toBeChecked();
    await user.click(bypassCheckbox);
    expect(bypassCheckbox).not.toBeChecked();

    // First submit -> ImportBlocked
    const retryButtonsAfterOpen = screen.getAllByRole('button', { name: /^Retry Import$/i });
    await user.click(retryButtonsAfterOpen[retryButtonsAfterOpen.length - 1]);

    await waitFor(() => {
      expect(retryCalls).toHaveLength(1);
      expect(retryCalls[0].body).toMatchObject({
        workId: 846,
        episodeNumbers: null,
        bypassUpgradeCheck: false,
      });
    });

    const statusBanner = await screen.findByRole('status');
    expect(statusBanner).toHaveTextContent('Policy rejected audio language.');
    expect(screen.queryByText('Retried import for tracked download.')).not.toBeInTheDocument();

    // Modal stays open and preserves select & checkbox state
    expect(
      screen.getByRole('heading', {
        level: 2,
        name: /Retry \/ Override Import — Sonnenwanderer\.German\.2000\.DVDRiP\.XviD-GRP/i,
      }),
    ).toBeInTheDocument();
    expect(workSelect.value).toBe('846');
    expect(bypassCheckbox).not.toBeChecked();

    // Second submit -> VerifiedPass
    const retryButtonsSecond = screen.getAllByRole('button', { name: /^Retry Import$/i });
    await user.click(retryButtonsSecond[retryButtonsSecond.length - 1]);

    await waitFor(() => {
      expect(retryCalls).toHaveLength(2);
      expect(
        screen.queryByRole('heading', {
          level: 2,
          name: /Retry \/ Override Import — Sonnenwanderer\.German\.2000\.DVDRiP\.XviD-GRP/i,
        }),
      ).not.toBeInTheDocument();
    });

    expect(await screen.findByText('Imported')).toBeInTheDocument();
  });

  it('AcquisitionView Manual Override modal leaves Episode Numbers empty instead of populating unitIds and submits episodeNumbers: null', async () => {
    const user = userEvent.setup();
    const retryCalls: Array<{ path: string; body: Record<string, unknown> }> = [];

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const url = new URL(rawUrl, 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      const json = (data: unknown, status = 200) =>
        new Response(JSON.stringify(data), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.pathname === '/api/v1/downloaders/drivers' && method === 'GET') {
        return json({ drivers: [], defaultCategoryMappings: {} });
      }
      if (url.pathname === '/api/v1/downloaders' && method === 'GET') return json([]);
      if (url.pathname === '/api/v1/downloaders/queue' && method === 'GET') return json([]);
      if (url.pathname === '/api/v1/downloaders/imports' && method === 'GET') return json([]);
      if (url.pathname === '/api/v1/downloaders/remote-paths' && method === 'GET') return json([]);

      if (url.pathname === '/api/v1/catalog/works' && method === 'GET') {
        return json([
          {
            id: 846,
            title: 'Solar Drifters',
            year: 2000,
            workKind: 'movies',
            monitored: true,
          },
        ]);
      }

      if (url.pathname === '/api/v1/downloaders/tracked' && method === 'GET') {
        return json([
          {
            id: 702,
            downloadClientId: 1,
            clientName: 'SABnzbd',
            externalDownloadId: 'nzo-orbital-702',
            protocol: 'usenet',
            infoHash: 'nzo:nzo-orbital-702',
            title: 'Sonnenwanderer.2000.German.DL.1080p.WEB-GRP',
            mediaVertical: 'movies',
            workId: 846,
            workTitle: 'Solar Drifters',
            unitIds: [33856, 33857],
            state: 'ImportBlocked',
            trackedStatus: 'warning',
            statusMessages: ['Awaiting manual confirmation.'],
            outputPath: '/downloads/movies-managerr/Sonnenwanderer.2000',
            quarantineId: null,
            canMoveFiles: true,
            firstSeenAt: '2026-11-18T12:00:00Z',
            updatedAt: '2026-11-18T12:05:00Z',
          },
        ]);
      }

      if (url.pathname === '/api/v1/downloaders/tracked/702/retry-import' && method === 'POST') {
        const body = init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : {};
        retryCalls.push({ path: url.pathname, body });
        return json({
          verdict: 'VerifiedPass',
          destinationPath: '/media/movies/Solar Drifters (2000)/Solar Drifters (2000).mkv',
          message: 'Imported',
        });
      }

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

    const retryImportBtn = await screen.findByRole('button', { name: /Retry \/ Import/i });
    await user.click(retryImportBtn);

    expect(
      await screen.findByRole('heading', {
        level: 2,
        name: /Manual Override \/ Retry Import — Sonnenwanderer\.2000\.German\.DL\.1080p\.WEB-GRP/i,
      }),
    ).toBeInTheDocument();

    const episodesInput = screen.getByPlaceholderText('e.g. 13 or 1,2') as HTMLInputElement;
    expect(episodesInput.value).toBe('');
    expect(episodesInput.value).not.toBe('33856,33857');

    await user.click(screen.getByRole('button', { name: /Import Now/i }));

    await waitFor(() => {
      expect(retryCalls).toHaveLength(1);
      expect(retryCalls[0].body).toMatchObject({
        workId: 846,
        episodeNumbers: null,
      });
    });
  });
});
