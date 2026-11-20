import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CatalogView } from '../components/CatalogView';

function createDownloadRateMockFetch() {
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
          name: 'Serien',
          mediaType: 'tv',
          rootFolderPath: '/data/media/tv',
          plexSectionId: '2',
          defaultProfileId: 1,
          workCount: 3,
        },
      ]);
    }

    if (path === '/api/v1/catalog/works') {
      return json([
        {
          id: 397,
          libraryId: 1,
          workKind: 'tv',
          title: 'Fallout',
          altTitles: [],
          originalLanguage: 'en',
          year: 2024,
          status: 'continuing',
          overview: 'Post-apocalyptic drama.',
          posterUrl: null,
          monitored: true,
          lockedFields: [],
          primaryProvider: 'tvdb',
          episodeOrderingScheme: 'aired',
          externalIds: { tvdb: '416744' },
          tags: ['live-action-series'],
          containerCount: 3,
          unitCount: 19,
          fileCount: 12,
          monitoredUnitCount: 12,
          downloadedUnitCount: 12,
        },
        {
          id: 501,
          libraryId: 1,
          workKind: 'tv',
          title: 'Severance',
          altTitles: [],
          originalLanguage: 'en',
          year: 2022,
          status: 'continuing',
          overview: 'Lumon Industries.',
          posterUrl: null,
          monitored: true,
          lockedFields: [],
          primaryProvider: 'tvdb',
          episodeOrderingScheme: 'aired',
          externalIds: { tvdb: '371980' },
          tags: ['live-action-series'],
          containerCount: 2,
          unitCount: 22,
          fileCount: 6,
          monitoredUnitCount: 9,
          downloadedUnitCount: 6,
        },
        {
          id: 502,
          libraryId: 1,
          workKind: 'tv',
          title: 'Archived Show',
          altTitles: [],
          originalLanguage: 'en',
          year: 2010,
          status: 'ended',
          overview: 'Unmonitored archive.',
          posterUrl: null,
          monitored: false,
          lockedFields: [],
          primaryProvider: 'tvdb',
          episodeOrderingScheme: 'aired',
          externalIds: { tvdb: '100001' },
          tags: ['live-action-series'],
          containerCount: 2,
          unitCount: 40,
          fileCount: 0,
          monitoredUnitCount: 0,
          downloadedUnitCount: 0,
        },
      ]);
    }

    if (path === '/api/v1/catalog/works/501') {
      return json({
        work: {
          id: 501,
          libraryId: 1,
          workKind: 'tv',
          title: 'Severance',
          altTitles: [],
          originalLanguage: 'en',
          year: 2022,
          status: 'continuing',
          overview: 'Lumon Industries.',
          posterUrl: null,
          monitored: true,
          lockedFields: [],
          primaryProvider: 'tvdb',
          episodeOrderingScheme: 'aired',
          externalIds: { tvdb: '371980' },
          tags: ['live-action-series'],
          containerCount: 2,
          unitCount: 22,
          fileCount: 6,
          monitoredUnitCount: 9,
          downloadedUnitCount: 6,
        },
        containers: [
          {
            id: 10,
            containerKind: 'season',
            containerNumber: 0,
            title: 'Specials',
            releaseDate: '2022-01-01',
            monitored: false,
            units: [
              {
                id: 1001,
                unitKind: 'episode',
                unitNumber: 1,
                absoluteNumber: null,
                title: 'Behind the Scenes',
                airOrReleaseDate: '2022-02-01',
                monitored: false,
                requiredAudioLangs: ['de'],
                requiredSubLangs: [],
                qualityCutoffMet: false,
                languageCutoffMet: false,
                files: [],
              },
            ],
          },
          {
            id: 11,
            containerKind: 'season',
            containerNumber: 1,
            title: 'Season 1',
            releaseDate: '2022-02-18',
            monitored: true,
            units: [
              {
                id: 1002,
                unitKind: 'episode',
                unitNumber: 1,
                absoluteNumber: 1,
                title: 'Good News About Hell',
                airOrReleaseDate: '2022-02-18',
                monitored: true,
                requiredAudioLangs: ['de'],
                requiredSubLangs: [],
                qualityCutoffMet: true,
                languageCutoffMet: true,
                files: [
                  {
                    id: 9001,
                    relativePath: 'Severance/S01E01.mkv',
                    sizeBytes: 1500000000,
                    qualityName: 'WEBDL-1080p',
                    detectedAudioLangs: ['de'],
                    verifiedStatus: 'VerifiedPass',
                    inodeHardlinked: false,
                  },
                ],
              },
              {
                id: 1003,
                unitKind: 'episode',
                unitNumber: 2,
                absoluteNumber: 2,
                title: 'Half Loop',
                airOrReleaseDate: '2022-02-25',
                monitored: true,
                requiredAudioLangs: ['de'],
                requiredSubLangs: [],
                qualityCutoffMet: false,
                languageCutoffMet: false,
                files: [],
              },
              {
                id: 1004,
                unitKind: 'episode',
                unitNumber: 3,
                absoluteNumber: 3,
                title: 'Bonus Unmonitored Recap',
                airOrReleaseDate: '2022-03-01',
                monitored: false,
                requiredAudioLangs: ['de'],
                requiredSubLangs: [],
                qualityCutoffMet: false,
                languageCutoffMet: false,
                files: [],
              },
            ],
          },
        ],
      });
    }

    return json([]);
  });
}

describe('Catalog Download Rate Uses Monitored Episodes as Y', () => {
  beforeEach(() => {
    window.sessionStorage.clear();
    window.localStorage.clear();
  });

  it('renders X / Y using monitored episodes across Posters, Overview, Table, availability bubbles, and Season headers', async () => {
    const user = userEvent.setup();
    vi.stubGlobal('fetch', createDownloadRateMockFetch());

    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/library']}>
          <CatalogView
            section="catalog"
            canWriteCatalog={true}
            canRequest={true}
            canApprove={true}
            canSearchIndexers={true}
          />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    // Fallout: 12 downloaded, 12 monitored target (out of 19 total) -> 12 / 12 (not 12 / 19!)
    expect(await screen.findByText('12 / 12')).toBeInTheDocument();
    expect(screen.queryByText('12 / 19')).not.toBeInTheDocument();
    const falloutStrip = screen.getByTitle('12 / 12 monitored (19 total)');
    expect(falloutStrip).toHaveClass('bg-[#357ebd]');

    // Severance: 6 downloaded, 9 monitored (out of 22 total) -> 6 / 9
    expect(screen.getByText('6 / 9')).toBeInTheDocument();
    expect(screen.queryByText('6 / 22')).not.toBeInTheDocument();
    const sevStrip = screen.getByTitle('6 / 9 monitored (22 total)');
    expect(sevStrip).toHaveClass('bg-[#993333]');
    const sevFill = sevStrip.querySelector('.bg-\\[\\#e69500\\]') as HTMLElement | null;
    expect(sevFill).not.toBeNull();
    expect(sevFill?.style.width).toBe('67%');

    // Archived Show: 0 downloaded, 0 monitored (out of 40 total) -> 0 / 0
    expect(screen.getByText('0 / 0')).toBeInTheDocument();
    expect(screen.queryByText('0 / 40')).not.toBeInTheDocument();

    // Filter bubbles reflect monitored completion: Fallout is Downloaded (1), only Severance is Missing (1)
    expect(screen.getByRole('button', { name: /^Downloaded 1$/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Missing 1$/i })).toBeInTheDocument();

    // Switch to Overview mode and verify X / Y Files uses monitored episodes
    await user.click(screen.getByRole('button', { name: /^Overview$/i }));
    expect(await screen.findByText('12 / 12 Files')).toBeInTheDocument();
    expect(screen.getByText('6 / 9 Files')).toBeInTheDocument();

    // Switch to Table mode and verify X / Y uses monitored episodes
    await user.click(screen.getByRole('button', { name: /^Table$/i }));
    expect(await screen.findByText('12 / 12')).toBeInTheDocument();
    expect(screen.getByText('6 / 9')).toBeInTheDocument();

    // Open Severance detail view and verify Season headers use monitored episodes as Y
    await user.click(screen.getByRole('button', { name: 'Severance' }));
    await waitFor(() => {
      expect(screen.getByText('Specials')).toBeInTheDocument();
      expect(screen.getByText('Season 1')).toBeInTheDocument();
    });

    // Specials has 1 unmonitored episode (0 files) -> 0 / 0
    // Season 1 has 2 monitored episodes (1 file) + 1 unmonitored episode (0 files) -> 1 / 2 (not 1 / 3)
    expect(screen.getByText('0 / 0')).toBeInTheDocument();
    expect(screen.getByText('1 / 2')).toBeInTheDocument();
    expect(screen.queryByText('1 / 3')).not.toBeInTheDocument();
  });
});
