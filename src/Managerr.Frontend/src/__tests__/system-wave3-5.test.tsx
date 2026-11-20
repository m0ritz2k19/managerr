import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, useLocation } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SystemView } from '../components/SystemView';
import { CatalogView } from '../components/CatalogView';
import { NamingTemplatesEditorPanel } from '../components/MediaManagementToolsPanel';
import { CatalogAddMediaSection } from '../components/catalog/CatalogAddMediaSection';

function LocationProbe() {
  const loc = useLocation();
  return <div data-testid="location-probe">{loc.pathname + loc.search}</div>;
}

describe('Wave 3–5 Frontend Features (#44, #57, #60, #40)', () => {
  it('renders dynamic version + gitCommit, health check actionUrl & resolveAction, and Retention Policy + VACUUM UI in SystemView', async () => {
    const user = userEvent.setup();
    const resolveCalls: string[] = [];
    const retentionUpdates: unknown[] = [];
    let retentionRunCalled = false;

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const url = new URL(rawUrl, 'http://localhost');
      const path = url.pathname;
      const method = (init?.method ?? 'GET').toUpperCase();

      const json = (data: unknown, status = 200) =>
        new Response(JSON.stringify(data), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (path === '/api/v1/system/health' && method === 'GET') {
        return json({
          status: 'healthy',
          service: 'Managerr.Host',
          version: '0.2.4',
          informationalVersion: '0.2.4+abc1234',
          gitCommit: 'abc1234def5678',
          utcNow: '2026-11-07T18:00:00Z',
        });
      }

      if (path === '/api/v1/system/health/checks' && method === 'GET') {
        return json([
          {
            key: 'duplicate-unit-files',
            category: 'MediaFiles',
            severity: 'warning',
            title: 'Duplicate Unit Files',
            message: '3 duplicate unit files detected across works.',
            actionUrl: '/library?health=duplicates',
            resolveAction: 'deduplicate_unit_files',
          },
        ]);
      }

      if (path.startsWith('/api/v1/system/health/resolve/') && method === 'POST') {
        const action = decodeURIComponent(path.replace('/api/v1/system/health/resolve/', ''));
        resolveCalls.push(action);
        return json({
          action,
          resolved: true,
          affectedCount: 3,
          message: 'Deduplicated 3 unit files.',
        });
      }

      if (path === '/api/v1/system/retention' && method === 'GET') {
        return json({
          verificationReportsRetentionDays: 30,
          mediaHistoryRetentionDays: 365,
          auditAndJobLogsRetentionDays: 90,
          reviewedQuarantineSnippetsRetentionDays: 14,
          vacuumThresholdDeletedRows: 1000,
        });
      }

      if (path === '/api/v1/system/retention' && method === 'PUT') {
        const parsed = init?.body ? JSON.parse(String(init.body)) : {};
        retentionUpdates.push(parsed);
        return json(parsed);
      }

      if (path === '/api/v1/system/retention/run' && method === 'POST') {
        retentionRunCalled = true;
        return json({
          deletedVerificationJobs: 12,
          deletedHistoryRows: 45,
          deletedAuditEvents: 80,
          deletedJobLogs: 23,
          deletedQuarantineRows: 4,
          deletedSnippetDirs: 2,
          vacuumExecuted: true,
          bytesReclaimed: 10485760,
        });
      }

      if (path === '/api/v1/system/storage') {
        return json({
          configDirectory: '/config',
          backupsDirectory: '/config/backups',
          snippetsDirectory: '/config/snippets',
          walMode: 'wal',
          synchronousMode: 'normal',
          databases: [],
          diskSpaces: [],
          generatedAtUtc: '2026-11-07T18:00:00Z',
        });
      }

      if (path === '/api/v1/system/backups') return json([]);
      if (path === '/api/v1/system/tasks') return json([]);

      return json([]);
    });

    vi.stubGlobal('fetch', fetchMock);

    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/system/status']}>
          <LocationProbe />
          <SystemView
            section="status"
            availableScopes={['system:admin', 'audit:read']}
            canSystemAdmin={true}
            canReadAudit={true}
          />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    // #44: Dynamic version & short git commit SHA in About section
    expect(await screen.findByText('0.2.4')).toBeInTheDocument();
    expect(screen.getByText('abc1234')).toBeInTheDocument();

    // #57: Health check item renders View in Library link and 1-click Resolve button
    const viewInLibraryLink = await screen.findByRole('link', { name: /View in Library/i });
    expect(viewInLibraryLink).toHaveAttribute('href', '/library?health=duplicates');

    const resolveBtn = screen.getByRole('button', { name: /^Resolve$/i });
    await user.click(resolveBtn);

    await waitFor(() => {
      expect(resolveCalls).toEqual(['deduplicate_unit_files']);
    });
    expect(await screen.findByText(/Deduplicated 3 unit files/i)).toBeInTheDocument();

    // #60: Data Retention & SQLite Compaction card
    expect(screen.getByText('Data Retention & SQLite Compaction')).toBeInTheDocument();
    const saveRetentionBtn = screen.getByRole('button', { name: /Save Retention Policy/i });
    await user.click(saveRetentionBtn);

    await waitFor(() => {
      expect(retentionUpdates.length).toBe(1);
    });
    expect(retentionUpdates[0]).toEqual({
      verificationReportsRetentionDays: 30,
      mediaHistoryRetentionDays: 365,
      auditAndJobLogsRetentionDays: 90,
      reviewedQuarantineSnippetsRetentionDays: 14,
      vacuumThresholdDeletedRows: 1000,
    });

    const runCleanupBtn = screen.getByRole('button', { name: /Run Cleanup & VACUUM Now/i });
    await user.click(runCleanupBtn);

    await waitFor(() => {
      expect(retentionRunCalled).toBe(true);
    });
    expect(await screen.findByText(/Cleanup Completed/i)).toBeInTheDocument();
    expect(screen.getByText(/10\.00 MB/i)).toBeInTheDocument();
  });

  it('renders /library?health=duplicates banner with Clear Filter & Resolve All in CatalogView, plus #40 Not configured badge in Metadata settings', async () => {
    const user = userEvent.setup();
    const resolveCalls: string[] = [];
    const onClearUrlHealth = vi.fn();

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const url = new URL(rawUrl, 'http://localhost');
      const path = url.pathname;
      const method = (init?.method ?? 'GET').toUpperCase();

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
            title: 'Arrival',
            altTitles: [],
            originalLanguage: 'en',
            year: 2016,
            status: 'released',
            monitored: true,
            posterUrl: null,
            primaryProvider: 'tmdb',
            episodeOrderingScheme: 'aired',
            externalIds: { tmdb: '329865' },
            unitCount: 1,
            fileCount: 2,
          },
        ]);
      }

      if (path === '/api/v1/profiles') return json([]);

      if (path.startsWith('/api/v1/system/health/resolve/') && method === 'POST') {
        const action = decodeURIComponent(path.replace('/api/v1/system/health/resolve/', ''));
        resolveCalls.push(action);
        return json({
          action,
          resolved: true,
          affectedCount: 2,
          message: 'Deduplicated 2 files across catalog.',
        });
      }

      if (path === '/api/v1/metadata/providers') {
        return json({
          builtInProviders: [
            {
              key: 'tmdb',
              displayName: 'TMDB (The Movie Database)',
              description: 'Requires TMDB API key',
              supportedVerticals: ['movies', 'tv'],
              isBuiltInArrDefault: true,
              requiresApiKey: true,
              hasApiKey: false,
              configurationStatus: 'not_configured',
            },
          ],
          crosswalkEnabled: true,
        });
      }

      if (path === '/api/v1/metadata/settings') {
        return json({
          hasTmdbApiKey: false,
          tmdbApiKeyMasked: '',
          hasTvdbApiKey: false,
          tvdbApiKeyMasked: '',
          hasMalClientId: false,
          malClientIdMasked: '',
          hasAnidbClient: false,
          anidbClientName: '',
          anidbClientVersion: 1,
          contactEmail: '',
        });
      }

      return json([]);
    });

    vi.stubGlobal('fetch', fetchMock);

    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });

    const { rerender } = render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/library?health=duplicates']}>
          <CatalogView
            section="catalog"
            urlHealth="duplicates"
            onClearUrlParams={onClearUrlHealth}
            canWriteCatalog={true}
            canRequest={true}
            canApprove={true}
            canSearchIndexers={true}
          />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    // #57: Active health filter banner with Clear Filter and Resolve All button
    expect(await screen.findByText(/Duplicate Unit Files/i)).toBeInTheDocument();
    const resolveAllBtn = screen.getByRole('button', { name: /Resolve All \(Deduplicate Files\)/i });
    await user.click(resolveAllBtn);

    await waitFor(() => {
      expect(resolveCalls).toEqual(['deduplicate_unit_files']);
    });
    expect(await screen.findByText(/Deduplicated 2 files across catalog/i)).toBeInTheDocument();

    const clearFilterBtn = screen.getByRole('button', { name: /Clear Filter/i });
    await user.click(clearFilterBtn);
    expect(onClearUrlHealth).toHaveBeenCalledTimes(1);

    // #40: Render metadata section and verify "Not configured (API key required)" badge
    rerender(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/settings/metadata']}>
          <CatalogView
            section="metadata"
            canWriteCatalog={true}
            canRequest={true}
            canApprove={true}
            canSearchIndexers={true}
          />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    expect(await screen.findByText('Not configured (API key required)')).toBeInTheDocument();
  });

  it('defaults Import Transfer Mode to Move on Import in NamingTemplatesEditorPanel, sorts Add Media by Popularity by default, and displays Specials on series cards', async () => {
    const user = userEvent.setup();
    const namingSaves: Array<{ useHardlinks?: boolean }> = [];

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const url = new URL(rawUrl, 'http://localhost');
      const path = url.pathname;
      const method = (init?.method ?? 'GET').toUpperCase();

      const json = (data: unknown, status = 200) =>
        new Response(JSON.stringify(data), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (path === '/api/v1/settings/naming' && method === 'GET') {
        return json({
          renameMediaFiles: true,
          useHardlinks: false,
          colonReplacement: 'space_dash',
          templates: {},
          availableTokens: [],
        });
      }

      if (path === '/api/v1/settings/naming' && method === 'PUT') {
        const parsed = init?.body ? JSON.parse(String(init.body)) : {};
        namingSaves.push(parsed);
        return json(parsed);
      }

      if (path === '/api/v1/settings/naming/preview') {
        return json({ previewPath: 'Sample/Preview.mkv' });
      }

      return json([]);
    });

    vi.stubGlobal('fetch', fetchMock);

    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <NamingTemplatesEditorPanel canWrite={true} />
        <CatalogAddMediaSection
          isAddSection={true}
          manualMode={false}
          onSetManualMode={vi.fn()}
          addSearchInput="Dark"
          onSetAddSearchInput={vi.fn()}
          debouncedAddTerm="Dark"
          onSetDebouncedAddTerm={vi.fn()}
          activeAddTab={{ id: 'all', label: 'All Media', placeholder: 'Search' }}
          onSetAddVertical={vi.fn()}
          isMetadataSearchFetching={false}
          isMetadataSearchError={false}
          metadataSearchError={null}
          metadataCandidates={[
            {
              providerKey: 'tmdb',
              providerItemId: '1',
              vertical: 'tv',
              title: 'Obscure Show',
              altTitles: [],
              originalLanguage: 'en',
              year: 2023,
              status: 'ended',
              overview: 'Low popularity candidate returned first by raw index',
              posterUrl: null,
              externalIds: { tmdb: '1' },
              popularity: 12.5,
              containers: [
                { containerKind: 'season', containerNumber: 1, title: 'Season 1', releaseDate: null, units: [] },
              ],
            },
            {
              providerKey: 'tvdb',
              providerItemId: '2',
              vertical: 'tv',
              title: 'Blockbuster Series',
              altTitles: [],
              originalLanguage: 'de',
              year: 2017,
              status: 'ended',
              overview: 'High popularity candidate with specials',
              posterUrl: null,
              externalIds: { tvdb: '2' },
              popularity: 450.0,
              containers: [
                { containerKind: 'season', containerNumber: 0, title: 'Specials', releaseDate: null, units: [] },
                { containerKind: 'season', containerNumber: 1, title: 'Season 1', releaseDate: null, units: [] },
                { containerKind: 'season', containerNumber: 2, title: 'Season 2', releaseDate: null, units: [] },
              ],
            },
          ]}
          existingWorkLookup={() => undefined}
          onOpenCandidateAddModal={vi.fn()}
          canWriteCatalog={true}
          onSetShowAddLibModal={vi.fn()}
          libraries={[]}
          activeLibId={null}
          onSetFormLibraryId={vi.fn()}
          workTitle=""
          onSetWorkTitle={vi.fn()}
          workYear=""
          onSetWorkYear={vi.fn()}
          workProvider="auto"
          onSetWorkProvider={vi.fn()}
          builtInProviders={[]}
          onAddManualWork={vi.fn()}
          isAddManualWorkPending={false}
          selectedCandidate={null}
          onCloseCandidateModal={vi.fn()}
          modalSearchOnAdd={true}
          onSetModalSearchOnAdd={vi.fn()}
          onAddCandidate={vi.fn()}
          isAddCandidatePending={false}
          isAddCandidateError={false}
          addCandidateError={null}
          modalLibraryId=""
          onSetModalLibraryId={vi.fn()}
          modalMonitorMode="all"
          onSetModalMonitorMode={vi.fn()}
          modalMinimumAvailability="released"
          onSetModalMinimumAvailability={vi.fn()}
          modalProfileId=""
          onSetModalProfileId={vi.fn()}
          profiles={[]}
          modalAudioPreset=""
          onSetModalAudioPreset={vi.fn()}
          modalOrdering="aired"
          onSetModalOrdering={vi.fn()}
          modalTags=""
          onSetModalTags={vi.fn()}
        />
      </QueryClientProvider>,
    );

    // 1. Verify Import Transfer Mode defaults to Move ("move" / useHardlinks: false)
    const transferSelect = (await screen.findByLabelText('Import Transfer Mode')) as HTMLSelectElement;
    expect(transferSelect.value).toBe('move');

    const saveNamingBtn = screen.getByRole('button', { name: /Save Naming Settings/i });
    await user.click(saveNamingBtn);
    await waitFor(() => {
      expect(namingSaves.length).toBe(1);
    });
    expect(namingSaves[0].useHardlinks).toBe(false);

    // Switch to hardlink and save again
    await user.selectOptions(transferSelect, 'hardlink');
    await user.click(saveNamingBtn);
    await waitFor(() => {
      expect(namingSaves.length).toBe(2);
    });
    expect(namingSaves[1].useHardlinks).toBe(true);

    // 2. Verify Order by defaults to Popularity and places Blockbuster Series before Obscure Show
    const orderBySelect = screen.getByLabelText('Order by') as HTMLSelectElement;
    expect(orderBySelect.value).toBe('popularity');

    const bodyTextPopularity = document.body.textContent ?? '';
    expect(bodyTextPopularity.indexOf('Blockbuster Series')).toBeLessThan(
      bodyTextPopularity.indexOf('Obscure Show'),
    );

    // Switch to Year (Newest) and verify Obscure Show (2023) comes before Blockbuster Series (2017)
    await user.selectOptions(orderBySelect, 'year');
    const bodyTextYear = document.body.textContent ?? '';
    expect(bodyTextYear.indexOf('Obscure Show')).toBeLessThan(bodyTextYear.indexOf('Blockbuster Series'));

    // 3. Verify Specials badge on series card with containerNumber 0 + 2 regular seasons
    expect(screen.getByText('2 Seasons + Specials')).toBeInTheDocument();
  });
});
