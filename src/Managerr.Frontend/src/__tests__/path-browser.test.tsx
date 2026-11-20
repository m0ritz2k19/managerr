import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CatalogModalsSection } from '../components/catalog/CatalogSettingsAndRequestsSection';
import { ActivityQueueAndHistoryView } from '../components/ActivityQueueAndHistoryView';
import { PathBrowserModal, type FilesystemBrowseResponse } from '../components/arr/PathBrowserModal';

describe('PathBrowserModal & PathBrowserInput Integration', () => {
  let queryClient: QueryClient;

  beforeEach(() => {
    queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false, gcTime: 0 },
        mutations: { retry: false },
      },
    });
  });

  it('navigates directories, uses quick roots, creates a folder, and selects folder in Add Root Folder modal', async () => {
    const user = userEvent.setup();
    let currentLibPath = '/media/movies';
    const calls: Array<{ method: string; path: string; query: string; body: unknown }> = [];

    const mockFilesystemResponses: Record<string, FilesystemBrowseResponse> = {
      '/': {
        currentPath: '/',
        parent: null,
        quickRoots: [
          { name: 'Media Root', path: '/media', kind: 'media' },
          { name: 'Downloads', path: '/downloads', kind: 'downloads' },
        ],
        directories: [
          { type: 'folder', name: 'media', path: '/media', lastModified: '2026-01-01T00:00:00Z' },
          { type: 'folder', name: 'downloads', path: '/downloads', lastModified: '2026-01-01T00:00:00Z' },
        ],
        files: [],
        truncated: false,
      },
      '/media': {
        currentPath: '/media',
        parent: '/',
        quickRoots: [
          { name: 'Media Root', path: '/media', kind: 'media' },
          { name: 'Movies', path: '/media/movies', kind: 'media' },
          { name: 'Series', path: '/media/series', kind: 'media' },
        ],
        directories: [
          { type: 'folder', name: 'movies', path: '/media/movies', lastModified: '2026-02-01T00:00:00Z' },
          { type: 'folder', name: 'series', path: '/media/series', lastModified: '2026-02-01T00:00:00Z' },
        ],
        files: [],
        truncated: false,
      },
      '/media/movies': {
        currentPath: '/media/movies',
        parent: '/media',
        quickRoots: [
          { name: 'Media Root', path: '/media', kind: 'media' },
          { name: 'Movies', path: '/media/movies', kind: 'media' },
        ],
        directories: [
          { type: 'folder', name: 'Action', path: '/media/movies/Action', lastModified: '2026-03-01T00:00:00Z' },
        ],
        files: [],
        truncated: false,
      },
      '/media/movies/Sci-Fi': {
        currentPath: '/media/movies/Sci-Fi',
        parent: '/media/movies',
        quickRoots: [],
        directories: [],
        files: [],
        truncated: false,
      },
    };

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const url = new URL(rawUrl, 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      const body = init?.body ? JSON.parse(String(init.body)) : null;

      calls.push({ method, path: url.pathname, query: url.search, body });

      const json = (data: unknown, status = 200) =>
        new Response(JSON.stringify(data), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.pathname === '/api/v1/filesystem' && method === 'GET') {
        const queryPath = url.searchParams.get('path') || '/';
        const res = mockFilesystemResponses[queryPath] ?? {
          currentPath: queryPath,
          parent: queryPath.substring(0, queryPath.lastIndexOf('/')) || '/',
          quickRoots: [],
          directories: [],
          files: [],
          truncated: false,
        };
        return json(res);
      }

      if (url.pathname === '/api/v1/filesystem/directories' && method === 'POST') {
        const targetPath = body?.path ?? '/created';
        mockFilesystemResponses[targetPath] = {
          currentPath: targetPath,
          parent: '/media/movies',
          quickRoots: [],
          directories: [],
          files: [],
          truncated: false,
        };
        return json({ success: true, path: targetPath });
      }

      return json({});
    });

    vi.stubGlobal('fetch', fetchMock);

    render(
      <QueryClientProvider client={queryClient}>
        <CatalogModalsSection
          showAddLibModal={true}
          onSetShowAddLibModal={vi.fn()}
          libName="My Movies"
          onSetLibName={vi.fn()}
          libType="movies"
          onSetLibType={vi.fn()}
          libPath={currentLibPath}
          onSetLibPath={(p) => {
            currentLibPath = p;
          }}
          libPlexSection="1"
          onSetLibPlexSection={vi.fn()}
          isCreateLibPending={false}
          onCreateLib={vi.fn()}
          showRequestModal={false}
          onSetShowRequestModal={vi.fn()}
          reqTitle=""
          onSetReqTitle={vi.fn()}
          reqAudioLang="en"
          onSetReqAudioLang={vi.fn()}
          activeLibId={null}
          onSetFormLibraryId={vi.fn()}
          libraries={[]}
          isSubmitRequestPending={false}
          onSubmitRequest={vi.fn()}
          renameModalConfig={null}
          onCloseRenameModal={vi.fn()}
          showDeleteConfirmModal={false}
          onSetShowDeleteConfirmModal={vi.fn()}
          selectedWorkCount={0}
          deleteFilesOnDisk={false}
          onSetDeleteFilesOnDisk={vi.fn()}
          isDeleteSelectedPending={false}
          onDeleteSelectedWorks={vi.fn()}
        />
      </QueryClientProvider>,
    );

    // Modal is open, browse button is rendered
    const browseBtn = screen.getByLabelText('Browse Root Folder Path');
    expect(browseBtn).toBeInTheDocument();

    // Click Browse button
    await user.click(browseBtn);

    // PathBrowserModal should be visible with title "Select Root Folder"
    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Select Root Folder' })).toBeInTheDocument();
    });

    // Initial path was /media/movies, verify directory entries loaded
    await waitFor(() => {
      expect(screen.getByRole('button', { name: /Action/i })).toBeInTheDocument();
    });

    // Verify breadcrumbs are rendered: / > media > movies
    const breadcrumbNav = screen.getByRole('navigation', { name: 'Directory breadcrumbs' });
    expect(within(breadcrumbNav).getByText('/')).toBeInTheDocument();
    expect(within(breadcrumbNav).getByText('media')).toBeInTheDocument();
    expect(within(breadcrumbNav).getByText('movies')).toBeInTheDocument();

    // Click breadcrumb '/' to navigate to root
    await user.click(within(breadcrumbNav).getByRole('button', { name: '/' }));
    await waitFor(() => {
      const region = screen.getByRole('region', { name: 'Directory contents' });
      expect(within(region).getByRole('button', { name: 'downloads' })).toBeInTheDocument();
    });

    // In '/' folder, select button should be disabled for root folder
    const selectFolderBtn = screen.getByRole('button', { name: 'Select Folder' });
    expect(selectFolderBtn).toBeDisabled();

    // Click quick root "Media Root" (/media)
    const mediaChip = screen.getByRole('button', { name: 'Media Root' });
    await user.click(mediaChip);

    const dirRegion = screen.getByRole('region', { name: 'Directory contents' });
    await waitFor(() => {
      expect(within(dirRegion).getByRole('button', { name: 'movies' })).toBeInTheDocument();
    });

    // Click "movies" folder to navigate into /media/movies
    await user.click(within(dirRegion).getByRole('button', { name: 'movies' }));
    await waitFor(() => {
      expect(within(dirRegion).getByRole('button', { name: 'Action' })).toBeInTheDocument();
    });

    // Create a new folder "+ New Folder"
    const newFolderToggle = screen.getByRole('button', { name: '+ New Folder' });
    await user.click(newFolderToggle);

    const newFolderInput = screen.getByPlaceholderText('New folder name');
    await user.type(newFolderInput, 'Sci-Fi');

    const createBtn = screen.getByRole('button', { name: 'Create' });
    await user.click(createBtn);

    // Verify POST was made
    await waitFor(() => {
      expect(calls).toContainEqual(
        expect.objectContaining({
          method: 'POST',
          path: '/api/v1/filesystem/directories',
          body: { path: '/media/movies/Sci-Fi' },
        }),
      );
    });

    // Modal navigated into /media/movies/Sci-Fi
    await waitFor(() => {
      expect(screen.getByText('/media/movies/Sci-Fi')).toBeInTheDocument();
    });

    // Click "Select Folder" to confirm
    const confirmBtn = screen.getByRole('button', { name: 'Select Folder' });
    expect(confirmBtn).not.toBeDisabled();
    await user.click(confirmBtn);

    // The modal closes and currentLibPath is updated
    expect(currentLibPath).toBe('/media/movies/Sci-Fi');
  });

  it('browses manual import path with includeFiles=true, selects a file, and populates manual import input', async () => {
    const user = userEvent.setup();

    const mockFilesystemResponses: Record<string, FilesystemBrowseResponse> = {
      '/downloads': {
        currentPath: '/downloads',
        parent: '/',
        quickRoots: [
          { name: 'Usenet', path: '/downloads/usenet', kind: 'downloads' },
          { name: 'Torrents', path: '/downloads/torrents', kind: 'downloads' },
        ],
        directories: [
          { type: 'folder', name: 'usenet', path: '/downloads/usenet', lastModified: '2026-02-01T00:00:00Z' },
          { type: 'folder', name: 'torrents', path: '/downloads/torrents', lastModified: '2026-02-01T00:00:00Z' },
        ],
        files: [],
        truncated: false,
      },
      '/downloads/usenet': {
        currentPath: '/downloads/usenet',
        parent: '/downloads',
        quickRoots: [],
        directories: [
          { type: 'folder', name: 'complete', path: '/downloads/usenet/complete', lastModified: '2026-02-01T00:00:00Z' },
        ],
        files: [
          {
            type: 'file',
            name: 'Dune.Part.Two.2024.2160p.Remux.mkv',
            path: '/downloads/usenet/Dune.Part.Two.2024.2160p.Remux.mkv',
            extension: '.mkv',
            size: 45_000_000_000,
            lastModified: '2026-02-01T12:00:00Z',
          },
        ],
        truncated: false,
      },
    };

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const url = new URL(rawUrl, 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();

      const json = (data: unknown, status = 200) =>
        new Response(JSON.stringify(data), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.pathname === '/api/v1/filesystem' && method === 'GET') {
        const queryPath = url.searchParams.get('path') || '/downloads';
        const res = mockFilesystemResponses[queryPath] ?? mockFilesystemResponses['/downloads'];
        return json(res);
      }

      return json([]);
    });

    vi.stubGlobal('fetch', fetchMock);

    render(
      <QueryClientProvider client={queryClient}>
        <ActivityQueueAndHistoryView
          section="manual-import"
          canManageQueue={true}
        />
      </QueryClientProvider>,
    );

    // Find the manual import folder path browse button
    const browseBtn = screen.getByLabelText('Browse Manual Import Folder or File');
    expect(browseBtn).toBeInTheDocument();

    await user.click(browseBtn);

    // Modal should be open with title "Manual Import — Select Folder or Media File"
    await waitFor(() => {
      expect(
        screen.getByRole('heading', { name: 'Manual Import — Select Folder or Media File' }),
      ).toBeInTheDocument();
    });

    // Click quick location "Usenet"
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Usenet' })).toBeInTheDocument();
    });
    await user.click(screen.getByRole('button', { name: 'Usenet' }));

    // File should appear in table
    await waitFor(() => {
      expect(
        screen.getByRole('button', { name: /Dune\.Part\.Two\.2024\.2160p\.Remux\.mkv/i }),
      ).toBeInTheDocument();
    });

    // File size formatted
    expect(screen.getByText('41.91 GB')).toBeInTheDocument();

    // Click the file to select it
    const fileBtn = screen.getByRole('button', { name: /Dune\.Part\.Two\.2024\.2160p\.Remux\.mkv/i });
    await user.click(fileBtn);

    // Select button should now say "Select File"
    const selectFileBtn = screen.getByRole('button', { name: 'Select File' });
    expect(selectFileBtn).toBeInTheDocument();
    expect(selectFileBtn).not.toBeDisabled();

    await user.click(selectFileBtn);

    // Input #manual-folder-path should be updated with selected file path
    const pathInput = screen.getByLabelText('Manual Import Folder or File');
    expect(pathInput).toHaveValue('/downloads/usenet/Dune.Part.Two.2024.2160p.Remux.mkv');
  });

  it('supports client-side filtering and prevents outside-click dismissal during text selection drags', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    const onSelect = vi.fn();

    const mockResponse: FilesystemBrowseResponse = {
      currentPath: '/media',
      parent: '/',
      quickRoots: [],
      directories: [
        { type: 'folder', name: 'Movies', path: '/media/Movies', lastModified: null },
        { type: 'folder', name: 'Series', path: '/media/Series', lastModified: null },
        { type: 'folder', name: 'Anime', path: '/media/Anime', lastModified: null },
      ],
      files: [],
      truncated: false,
    };

    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify(mockResponse), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    );

    render(
      <QueryClientProvider client={queryClient}>
        <PathBrowserModal
          isOpen={true}
          onClose={onClose}
          onSelect={onSelect}
          initialPath="/media"
        />
      </QueryClientProvider>,
    );

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /Movies/i })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /Series/i })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /Anime/i })).toBeInTheDocument();
    });

    // Test instant client-side filter
    const filterInput = screen.getByLabelText('Filter current folder contents');
    await user.type(filterInput, 'anim');

    expect(screen.getByRole('button', { name: /Anime/i })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Movies/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Series/i })).not.toBeInTheDocument();

    // Clear filter
    await user.clear(filterInput);
    expect(screen.getByRole('button', { name: /Movies/i })).toBeInTheDocument();

    // Verify pointerdown inside dialog bounding rect and pointerup outside doesn't close dialog
    const dialog = document.querySelector('dialog');
    expect(dialog).toBeInTheDocument();

    dialog!.getBoundingClientRect = () => ({
      top: 50,
      left: 50,
      right: 600,
      bottom: 600,
      width: 550,
      height: 550,
      x: 50,
      y: 50,
      toJSON: () => ({}),
    });

    const firePointer = (type: 'pointerdown' | 'pointerup', clientX: number, clientY: number) => {
      dialog!.dispatchEvent(new MouseEvent(type, { clientX, clientY, bubbles: true }));
    };

    // 1. Text-selection drag simulation: pointerdown inside (200, 200), pointerup outside (5, 5)
    firePointer('pointerdown', 200, 200);
    firePointer('pointerup', 5, 5);
    expect(onClose).not.toHaveBeenCalled();

    // 2. Intentional outside click: BOTH pointerdown and pointerup outside (5, 5)
    firePointer('pointerdown', 5, 5);
    firePointer('pointerup', 5, 5);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('handles directory error state (e.g. 403 /etc) by disabling select button, hiding new folder, and providing Go to / recovery', async () => {
    const user = userEvent.setup();
    const onSelect = vi.fn();
    const onClose = vi.fn();

    const json = (data: unknown, status = 200) =>
      new Response(JSON.stringify(data), {
        status,
        headers: { 'Content-Type': 'application/json' },
      });

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(
        typeof input === 'string' ? input : input instanceof Request ? input.url : input.toString(),
        'http://localhost',
      );
      const method = init?.method || 'GET';

      if (url.pathname === '/api/v1/filesystem' && method === 'GET') {
        const queryPath = url.searchParams.get('path');
        if (queryPath === '/etc') {
          return new Response(JSON.stringify({ detail: 'Access to /etc is forbidden.' }), {
            status: 403,
            headers: { 'Content-Type': 'application/json' },
          });
        }
        return json({
          currentPath: '/',
          parent: null,
          quickRoots: [{ name: 'Media', path: '/media', kind: 'media' }],
          directories: [{ type: 'folder', name: 'media', path: '/media', lastModified: null }],
          files: [],
          truncated: false,
        });
      }

      return json([]);
    });

    vi.stubGlobal('fetch', fetchMock);

    render(
      <QueryClientProvider client={queryClient}>
        <PathBrowserModal
          isOpen={true}
          initialPath="/etc"
          onClose={onClose}
          onSelect={onSelect}
          allowCreateFolder={true}
        />
      </QueryClientProvider>,
    );

    // Error message should appear
    await waitFor(() => {
      expect(screen.getByText(/Access to \/etc is forbidden/i)).toBeInTheDocument();
    });

    // Select Folder button should be disabled
    const selectBtn = screen.getByRole('button', { name: 'Select Folder' });
    expect(selectBtn).toBeDisabled();

    // + New Folder button should NOT be rendered while in error state
    expect(screen.queryByRole('button', { name: /\+ New Folder/i })).not.toBeInTheDocument();

    // Click "Go to /" recovery button
    const goToRootBtn = screen.getByRole('button', { name: 'Go to /' });
    expect(goToRootBtn).toBeInTheDocument();
    await user.click(goToRootBtn);

    // After clicking "Go to /", it navigates to / and loads directories
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'media' })).toBeInTheDocument();
    });
  });
});
