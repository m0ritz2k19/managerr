import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CatalogView } from '../components/CatalogView';
import { WantedView } from '../components/WantedView';

function createFilterGroupMockFetch() {
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
          name: 'Filme',
          mediaType: 'movies',
          rootFolderPath: '/data/media/movies',
          plexSectionId: '1',
          defaultProfileId: 1,
          workCount: 1,
        },
        {
          id: 2,
          name: 'Anime',
          mediaType: 'anime',
          rootFolderPath: '/data/media/anime',
          plexSectionId: '3',
          defaultProfileId: 2,
          workCount: 1,
        },
        {
          id: 3,
          name: 'Animefilme',
          mediaType: 'movies',
          rootFolderPath: '/data/media/anime-movies',
          plexSectionId: '4',
          defaultProfileId: 1,
          workCount: 1,
        },
      ]);
    }

    if (path === '/api/v1/catalog/works') {
      return json([
        {
          id: 10,
          libraryId: 1,
          workKind: 'movies',
          title: 'Dune: Part Two',
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
          externalIds: { tmdb: '693134' },
          tags: ['live-action-movie'],
          containerCount: 1,
          unitCount: 1,
          fileCount: 1,
          monitoredUnitCount: 1,
          downloadedUnitCount: 1,
        },
        {
          id: 20,
          libraryId: 2,
          workKind: 'anime',
          title: 'KonoSuba',
          altTitles: [],
          originalLanguage: 'ja',
          year: 2016,
          status: 'ended',
          overview: '',
          posterUrl: null,
          monitored: true,
          lockedFields: [],
          primaryProvider: 'tvdb',
          episodeOrderingScheme: 'aired',
          externalIds: { tvdb: '299812' },
          tags: ['anime-series'],
          containerCount: 3,
          unitCount: 32,
          fileCount: 32,
          monitoredUnitCount: 32,
          downloadedUnitCount: 32,
        },
        {
          id: 30,
          libraryId: 3,
          workKind: 'movies',
          title: 'Your Name.',
          altTitles: [],
          originalLanguage: 'ja',
          year: 2016,
          status: 'released',
          overview: '',
          posterUrl: null,
          monitored: true,
          lockedFields: [],
          primaryProvider: 'tmdb',
          episodeOrderingScheme: 'aired',
          externalIds: { tmdb: '372058' },
          tags: ['anime-movie'],
          containerCount: 1,
          unitCount: 1,
          fileCount: 1,
          monitoredUnitCount: 1,
          downloadedUnitCount: 1,
        },
      ]);
    }

    if (path === '/api/v1/wanted/missing' || path === '/api/v1/wanted/cutoff') {
      return json({
        items: [],
        totalCount: 0,
        page: 1,
        pageSize: 25,
      });
    }

    return json([]);
  });
}

describe('Catalog & Wanted Labeled Filter Groups', () => {
  beforeEach(() => {
    window.sessionStorage.clear();
    window.localStorage.clear();
  });

  it('separates and labels Type, Library, Status, and Classification groups in CatalogView so Anime type vs Anime library are unambiguous', async () => {
    const user = userEvent.setup();
    vi.stubGlobal('fetch', createFilterGroupMockFetch());

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

    const typeGroup = await screen.findByRole('group', { name: 'Type' });
    const libraryGroup = screen.getByRole('group', { name: 'Library' });
    const statusGroup = screen.getByRole('group', { name: 'Status' });
    const classificationSelect = screen.getByRole('combobox', { name: 'Classification' });

    expect(typeGroup).toBeInTheDocument();
    expect(libraryGroup).toBeInTheDocument();
    expect(statusGroup).toBeInTheDocument();
    expect(classificationSelect).toHaveValue('all');

    // Accessible names unambiguously distinguish Type "Anime" vs Library "Anime"
    const animeTypeBtn = screen.getByRole('button', { name: /^Anime 1 Type$/i });
    const animeLibraryBtn = screen.getByRole('button', { name: /^Anime 1 Library$/i });
    const animeMoviesLibraryBtn = screen.getByRole('button', { name: /^Animefilme 1 Library$/i });

    expect(within(typeGroup).getByRole('button', { name: /^Anime 1 Type$/i })).toBe(animeTypeBtn);
    expect(within(libraryGroup).getByRole('button', { name: /^Anime 1 Library$/i })).toBe(animeLibraryBtn);
    expect(within(libraryGroup).getByRole('button', { name: /^Animefilme 1 Library$/i })).toBe(animeMoviesLibraryBtn);

    // Selecting Anime + Animefilme within the Library group ORs them together (shows KonoSuba + Your Name.)
    await user.click(animeLibraryBtn);
    await user.click(animeMoviesLibraryBtn);

    await waitFor(() => {
      expect(screen.queryByText('Dune: Part Two')).not.toBeInTheDocument();
      expect(screen.getAllByText('KonoSuba').length).toBeGreaterThan(0);
      expect(screen.getAllByText('Your Name.').length).toBeGreaterThan(0);
    });

    // Clear Library selection, then filter by Type -> Anime (only workKind === 'anime', excluding Animefilme movies)
    await user.click(animeLibraryBtn);
    await user.click(animeMoviesLibraryBtn);
    await user.click(animeTypeBtn);

    await waitFor(() => {
      expect(screen.queryByText('Dune: Part Two')).not.toBeInTheDocument();
      expect(screen.queryByText('Your Name.')).not.toBeInTheDocument();
      expect(screen.getAllByText('KonoSuba').length).toBeGreaterThan(0);
    });
  });

  it('separates and labels Type, Library, and Status groups in WantedView so Anime type vs Anime library are unambiguous', async () => {
    vi.stubGlobal('fetch', createFilterGroupMockFetch());

    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/wanted/missing']}>
          <WantedView section="missing" canSearchIndexers={true} canWriteCatalog={true} />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    const typeGroup = await screen.findByRole('group', { name: 'Type' });
    const libraryGroup = await screen.findByRole('group', { name: 'Library' });
    const statusGroup = screen.getByRole('group', { name: 'Status' });

    const animeTypeBtn = within(typeGroup).getByRole('button', { name: /^Anime Type$/i });
    const animeLibraryBtn = within(libraryGroup).getByRole('button', { name: /^Anime Library$/i });
    expect(animeTypeBtn).toBeInTheDocument();
    expect(animeLibraryBtn).toBeInTheDocument();
    expect(within(statusGroup).getByRole('button', { name: /^Monitored Only$/i })).toBeInTheDocument();
    expect(within(statusGroup).getByRole('button', { name: /^Released Only$/i })).toBeInTheDocument();
  });
});
