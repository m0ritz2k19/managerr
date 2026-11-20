import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { CatalogWorkDetailSection } from '../components/catalog/CatalogWorkDetailSection';
import { CatalogToolbarSection } from '../components/catalog/CatalogSettingsAndRequestsSection';
import { CatalogLibraryGridSection } from '../components/catalog/CatalogLibraryGridSection';
import type {
  LanguagePolicySummary,
  LibrarySummary,
  MediaWorkDetailTree,
  MediaWorkSummary,
  QualityProfileSummary,
} from '../components/catalog/catalogTypesAndHelpers';

describe('Catalog Language Scan & Verification Frontend', () => {
  const dummyWork: MediaWorkSummary = {
    id: 42,
    libraryId: 1,
    workKind: 'series',
    primaryProvider: 'tvdb',
    episodeOrderingScheme: 'standard',
    title: 'Dark',
    altTitles: [],
    originalLanguage: 'de',
    year: 2017,
    status: 'ended',
    overview: 'A missing child sets four families on a frantic hunt for answers.',
    posterUrl: null,
    monitored: true,
    lockedFields: [],
    externalIds: { tvdb: '334824' },
    containerCount: 1,
    unitCount: 1,
    fileCount: 1,
  };

  const dummyLibrary: LibrarySummary = {
    id: 1,
    slug: 'tv-shows',
    name: 'TV Shows',
    mediaType: 'series',
    rootFolderPath: '/data/media/tv',
    plexSectionId: null,
    metadataWaterfall: ['tvdb'],
    defaultProfileId: 1,
    workCount: 1,
  };

  it('renders Unverified badge for ImportedUnverified and disk_scan_adopted statuses', () => {
    const treeData: MediaWorkDetailTree = {
      work: dummyWork,
      containers: [
        {
          id: 101,
          containerKind: 'season',
          containerNumber: 1,
          title: 'Season 1',
          releaseDate: '2017-12-01',
          monitored: true,
          units: [
            {
              id: 1001,
              unitKind: 'episode',
              unitNumber: 1,
              absoluteNumber: 1,
              title: 'Secrets',
              airOrReleaseDate: '2017-12-01',
              monitored: true,
              requiredAudioLangs: ['de'],
              requiredSubLangs: [],
              qualityCutoffMet: true,
              languageCutoffMet: false,
              files: [
                {
                  id: 501,
                  relativePath: 'Season 01/Dark.S01E01.mkv',
                  sizeBytes: 1500000000,
                  qualityName: 'WEBDL-1080p',
                  detectedAudioLangs: [],
                  detectedSubLangs: [],
                  verifiedStatus: 'ImportedUnverified',
                  inodeHardlinked: false,
                },
              ],
            },
          ],
        },
      ],
    };

    render(
      <CatalogWorkDetailSection
        selectedWorkId={42}
        isTreeLoading={false}
        isTreeError={false}
        treeData={treeData}
        onBackToLibrary={vi.fn()}
        workActionBanner={null}
        onDismissBanner={vi.fn()}
        canWriteCatalog={true}
        canSearchIndexers={true}
        onQuickToggleWorkMonitor={vi.fn()}
        isWorkMonitorPresetPending={false}
        onApplyWorkMonitorPreset={vi.fn()}
        onUpdateWork={vi.fn()}
        profiles={[]}
        collapsedContainers={{}}
        onToggleCollapseContainer={vi.fn()}
        isToggleContainerPending={false}
        onToggleContainerMonitor={vi.fn()}
        searchingContainerId={null}
        isAutoSearchContainerPending={false}
        onAutoSearchContainer={vi.fn()}
        onToggleUnitMonitor={vi.fn()}
        searchingUnitId={null}
        isAutoSearchUnitPending={false}
        onAutoSearchUnit={vi.fn()}
        interactiveTarget={null}
        onSetInteractiveTarget={vi.fn()}
        isInteractiveSearchFetching={false}
        interactiveSearchRows={[]}
        onRefetchInteractiveSearch={vi.fn()}
        interactiveFilterText=""
        onSetInteractiveFilterText={vi.fn()}
        interactiveProtocolFilter="all"
        onSetInteractiveProtocolFilter={vi.fn()}
        interactiveHideRejected={false}
        onSetInteractiveHideRejected={vi.fn()}
        interactiveSortKey="score"
        onSetInteractiveSortKey={vi.fn()}
        interactiveSortDir="desc"
        onSetInteractiveSortDir={vi.fn()}
        expandedRejections={{}}
        onToggleExpandedRejection={vi.fn()}
        grabbingGuid={null}
        isGrabInteractivePending={false}
        onGrabInteractiveRelease={vi.fn()}
      />
    );

    expect(screen.getByText('Unverified')).toBeInTheDocument();
  });

  it('renders Verified badge with audio and subtitle languages for VerifiedPass status', () => {
    const treeData: MediaWorkDetailTree = {
      work: dummyWork,
      containers: [
        {
          id: 101,
          containerKind: 'season',
          containerNumber: 1,
          title: 'Season 1',
          releaseDate: '2017-12-01',
          monitored: true,
          units: [
            {
              id: 1001,
              unitKind: 'episode',
              unitNumber: 1,
              absoluteNumber: 1,
              title: 'Secrets',
              airOrReleaseDate: '2017-12-01',
              monitored: true,
              requiredAudioLangs: ['de'],
              requiredSubLangs: ['en'],
              qualityCutoffMet: true,
              languageCutoffMet: true,
              files: [
                {
                  id: 501,
                  relativePath: 'Season 01/Dark.S01E01.mkv',
                  sizeBytes: 1500000000,
                  qualityName: 'WEBDL-1080p',
                  detectedAudioLangs: ['de', 'en'],
                  detectedSubLangs: ['en', 'fr'],
                  verifiedStatus: 'VerifiedPass',
                  inodeHardlinked: false,
                  languageVerification: {
                    verdict: 'VerifiedPass',
                    overallConfidence: 0.98,
                    audioMethod: 'whisper',
                    detectedAudioLanguages: ['de', 'en'],
                    detectedSubLanguages: ['en', 'fr'],
                    durationMs: 120,
                    verifiedAt: '2026-11-13T20:00:00Z',
                  },
                },
              ],
            },
          ],
        },
      ],
    };

    render(
      <CatalogWorkDetailSection
        selectedWorkId={42}
        isTreeLoading={false}
        isTreeError={false}
        treeData={treeData}
        onBackToLibrary={vi.fn()}
        workActionBanner={null}
        onDismissBanner={vi.fn()}
        canWriteCatalog={true}
        canSearchIndexers={true}
        onQuickToggleWorkMonitor={vi.fn()}
        isWorkMonitorPresetPending={false}
        onApplyWorkMonitorPreset={vi.fn()}
        onUpdateWork={vi.fn()}
        profiles={[]}
        collapsedContainers={{}}
        onToggleCollapseContainer={vi.fn()}
        isToggleContainerPending={false}
        onToggleContainerMonitor={vi.fn()}
        searchingContainerId={null}
        isAutoSearchContainerPending={false}
        onAutoSearchContainer={vi.fn()}
        onToggleUnitMonitor={vi.fn()}
        searchingUnitId={null}
        isAutoSearchUnitPending={false}
        onAutoSearchUnit={vi.fn()}
        interactiveTarget={null}
        onSetInteractiveTarget={vi.fn()}
        isInteractiveSearchFetching={false}
        interactiveSearchRows={[]}
        onRefetchInteractiveSearch={vi.fn()}
        interactiveFilterText=""
        onSetInteractiveFilterText={vi.fn()}
        interactiveProtocolFilter="all"
        onSetInteractiveProtocolFilter={vi.fn()}
        interactiveHideRejected={false}
        onSetInteractiveHideRejected={vi.fn()}
        interactiveSortKey="score"
        onSetInteractiveSortKey={vi.fn()}
        interactiveSortDir="desc"
        onSetInteractiveSortDir={vi.fn()}
        expandedRejections={{}}
        onToggleExpandedRejection={vi.fn()}
        grabbingGuid={null}
        isGrabInteractivePending={false}
        onGrabInteractiveRelease={vi.fn()}
      />
    );

    expect(screen.getByText('Audio: de, en')).toBeInTheDocument();
    expect(screen.getByText('Sub: en, fr')).toBeInTheDocument();
    expect(screen.getByText('Verified 98%')).toBeInTheDocument();
  });

  it('renders Scan & Check Languages button in Series toolbar and fires onWorkScanAndVerify', async () => {
    const user = userEvent.setup();
    const handleScanAndVerify = vi.fn();

    const treeData: MediaWorkDetailTree = {
      work: dummyWork,
      containers: [],
    };

    render(
      <CatalogToolbarSection
        section="catalog"
        selectedWorkId={42}
        onReturnToLibrary={vi.fn()}
        treeData={treeData}
        canWriteCatalog={true}
        isRefreshMetadataPending={false}
        onRefreshMetadata={vi.fn()}
        isWorkScanPending={false}
        onWorkScan={vi.fn()}
        isWorkScanAndVerifyPending={false}
        onWorkScanAndVerify={handleScanAndVerify}
        onOpenRenamePreview={vi.fn()}
        canSearchIndexers={false}
        isAutoSearchWorkPending={false}
        onAutoSearchWork={vi.fn()}
        onSetInteractiveTarget={vi.fn()}
        onSetShowAddLibModal={vi.fn()}
        manualMode={false}
        onToggleManualMode={vi.fn()}
        isAnyCatalogQueryFetching={false}
        onRefreshActiveSection={vi.fn()}
        isAutoClassifyPending={false}
        onAutoClassify={vi.fn()}
        massEditMode={false}
        onToggleMassEditMode={vi.fn()}
        canRequest={false}
        onSetShowRequestModal={vi.fn()}
        onCalendarPrev={vi.fn()}
        onCalendarToday={vi.fn()}
        onCalendarNext={vi.fn()}
        calRangeLabel=""
        onSetShowIcalModal={vi.fn()}
        catalogFilterText=""
        onSetCatalogFilterText={vi.fn()}
        filteredWorks={[]}
        onOpenWork={vi.fn()}
        catalogSortField="title"
        onSetCatalogSortField={vi.fn()}
        onToggleCatalogSortAsc={vi.fn()}
        onSetCatalogSortAsc={vi.fn()}
        viewMode="posters"
        onUpdateViewMode={vi.fn()}
        addVertical="video"
        onSetAddVertical={vi.fn()}
        calendarViewMode="month"
        onSetCalendarViewMode={vi.fn()}
        calSelectedVerticals={new Set()}
        onSetCalSelectedVerticals={vi.fn()}
        calMonitoredOnly={false}
        onSetCalMonitoredOnly={vi.fn()}
      />
    );

    const scanBtn = screen.getByRole('button', { name: /Scan & Check Languages/i });
    expect(scanBtn).toBeInTheDocument();
    await user.click(scanBtn);
    expect(handleScanAndVerify).toHaveBeenCalledWith(42);
  });

  it('renders Scan & Check Languages ({count}) button in Mass Editor and fires onBulkScanAndVerify', async () => {
    const user = userEvent.setup();
    const handleBulkScanAndVerify = vi.fn();

    render(
      <CatalogLibraryGridSection
        healthFilterMeta={null}
        isResolveHealthFilterPending={false}
        onResolveHealthFilter={vi.fn()}
        onClearUrlParams={vi.fn()}
        autoClassifyReport={null}
        onDismissAutoClassifyReport={vi.fn()}
        workActionBanner={null}
        onDismissWorkActionBanner={vi.fn()}
        libraries={[dummyLibrary]}
        librariesById={new Map([[1, dummyLibrary]])}
        canWriteCatalog={true}
        onSetShowAddLibModal={vi.fn()}
        isWorksLoading={false}
        filteredWorks={[dummyWork]}
        allWorks={[dummyWork]}
        activeCatalogFilterCount={0}
        onClearAllCatalogFilters={vi.fn()}
        catalogFilterText=""
        massEditMode={true}
        selectedWorkIds={new Set([42, 43])}
        onSetSelectedWorkIds={vi.fn()}
        viewMode="posters"
        onOpenWork={vi.fn()}
        onQuickToggleWorkMonitor={vi.fn()}
        onSetShowDeleteConfirmModal={vi.fn()}
        isBulkEditPending={false}
        onBulkEditWorks={vi.fn()}
        isBulkScanAndVerifyPending={false}
        onBulkScanAndVerify={handleBulkScanAndVerify}
        bulkMonitored="keep"
        onSetBulkMonitored={vi.fn()}
        bulkMonitorPreset="keep"
        onSetBulkMonitorPreset={vi.fn()}
        bulkQualityProfileId="keep"
        onSetBulkQualityProfileId={vi.fn()}
        profiles={[]}
        bulkMinAvailability="keep"
        onSetBulkMinAvailability={vi.fn()}
        bulkLibraryId="keep"
        onSetBulkLibraryId={vi.fn()}
        bulkMoveFiles={false}
        onSetBulkMoveFiles={vi.fn()}
        bulkTags=""
        onSetBulkTags={vi.fn()}
        bulkTagMode="replace"
        onSetBulkTagMode={vi.fn()}
      />
    );

    const bulkScanBtn = screen.getByRole('button', { name: /Scan & Check Languages \(2\)/i });
    expect(bulkScanBtn).toBeInTheDocument();
    await user.click(bulkScanBtn);
    expect(handleBulkScanAndVerify).toHaveBeenCalledTimes(1);
  });

  it('renders Wanted Language, Original Language, Quality Profile controls and explainability pill for anime and fires onUpdateWork', async () => {
    const user = userEvent.setup();
    const handleUpdateWork = vi.fn();

    const animeWork: MediaWorkSummary = {
      ...dummyWork,
      id: 88,
      workKind: 'anime',
      title: 'Frieren: Beyond Journey\'s End',
      originalLanguage: 'ja',
      languagePolicyId: 2,
      qualityProfileId: 1,
    };

    const dummyPolicies: LanguagePolicySummary[] = [
      {
        id: 1,
        slug: 'german-first',
        name: 'German First',
        verificationMode: '3tier',
        tiers: [
          {
            tier: 1,
            label: 'German Audio',
            audioContainsAll: ['de'],
            audioContainsAny: [],
            subtitlesContainsAny: [],
            satisfiesLanguageCutoff: true,
            scoreBonus: 100,
          },
          {
            tier: 2,
            label: 'Original + German Sub',
            audioContainsAll: ['original'],
            audioContainsAny: [],
            subtitlesContainsAny: ['de'],
            satisfiesLanguageCutoff: false,
            scoreBonus: 50,
          },
        ],
      },
      {
        id: 2,
        slug: 'original-plus-subs',
        name: 'Original + Subtitles',
        verificationMode: '3tier',
        tiers: [
          {
            tier: 1,
            label: 'Original + German Sub',
            audioContainsAll: ['original'],
            audioContainsAny: [],
            subtitlesContainsAny: ['de'],
            satisfiesLanguageCutoff: true,
            scoreBonus: 100,
          },
        ],
      },
    ];

    const dummyProfiles: QualityProfileSummary[] = [
      {
        id: 1,
        slug: 'hd-1080p',
        name: 'HD-1080p',
        mediaVertical: 'video',
        qualityCutoff: 'WEBDL-1080p',
        languagePolicyId: 1,
        languagePolicy: dummyPolicies[0],
      },
    ];

    const treeData: MediaWorkDetailTree = {
      work: animeWork,
      containers: [],
    };

    render(
      <CatalogWorkDetailSection
        selectedWorkId={88}
        isTreeLoading={false}
        isTreeError={false}
        treeData={treeData}
        onBackToLibrary={vi.fn()}
        workActionBanner={null}
        onDismissBanner={vi.fn()}
        canWriteCatalog={true}
        canSearchIndexers={true}
        onQuickToggleWorkMonitor={vi.fn()}
        isWorkMonitorPresetPending={false}
        onApplyWorkMonitorPreset={vi.fn()}
        onUpdateWork={handleUpdateWork}
        profiles={dummyProfiles}
        languagePolicies={dummyPolicies}
        collapsedContainers={{}}
        onToggleCollapseContainer={vi.fn()}
        isToggleContainerPending={false}
        onToggleContainerMonitor={vi.fn()}
        searchingContainerId={null}
        isAutoSearchContainerPending={false}
        onAutoSearchContainer={vi.fn()}
        onToggleUnitMonitor={vi.fn()}
        searchingUnitId={null}
        isAutoSearchUnitPending={false}
        onAutoSearchUnit={vi.fn()}
        interactiveTarget={null}
        onSetInteractiveTarget={vi.fn()}
        isInteractiveSearchFetching={false}
        interactiveSearchRows={[]}
        onRefetchInteractiveSearch={vi.fn()}
        interactiveFilterText=""
        onSetInteractiveFilterText={vi.fn()}
        interactiveProtocolFilter="all"
        onSetInteractiveProtocolFilter={vi.fn()}
        interactiveHideRejected={false}
        onSetInteractiveHideRejected={vi.fn()}
        interactiveSortKey="score"
        onSetInteractiveSortKey={vi.fn()}
        interactiveSortDir="desc"
        onSetInteractiveSortDir={vi.fn()}
        expandedRejections={{}}
        onToggleExpandedRejection={vi.fn()}
        grabbingGuid={null}
        isGrabInteractivePending={false}
        onGrabInteractiveRelease={vi.fn()}
      />
    );

    // Verify Explainability pill is present with policy and substituted original language (JA)
    const explainability = screen.getByTestId('language-policy-explainability');
    expect(explainability).toBeInTheDocument();
    expect(explainability).toHaveTextContent('Policy: Original + Subtitles');
    expect(explainability).toHaveTextContent('Cutoff: JA + DE Sub');

    // Verify Wanted Language select
    const wantedLangSelect = screen.getByRole('combobox', { name: 'Wanted Language' });
    expect(wantedLangSelect).toBeInTheDocument();
    expect(wantedLangSelect).toHaveValue('2');

    // Switch Wanted Language to Profile Default ("")
    await user.selectOptions(wantedLangSelect, '');
    expect(handleUpdateWork).toHaveBeenCalledWith(88, {
      languagePolicyId: null,
      clearLanguagePolicy: true,
    });

    // Verify Original Language select
    const origLangSelect = screen.getByRole('combobox', { name: 'Original Language' });
    expect(origLangSelect).toBeInTheDocument();
    expect(origLangSelect).toHaveValue('ja');

    // Switch Original Language to "de"
    await user.selectOptions(origLangSelect, 'de');
    expect(handleUpdateWork).toHaveBeenCalledWith(88, {
      originalLanguage: 'de',
    });

    // Verify Quality Profile select is rendered for anime (not hidden behind movies)
    const qualityProfileSelect = screen.getByRole('combobox', { name: 'Quality Profile' });
    expect(qualityProfileSelect).toBeInTheDocument();
    expect(qualityProfileSelect).toHaveValue('1');

    // Switch Quality Profile to Library Default ("")
    await user.selectOptions(qualityProfileSelect, '');
    expect(handleUpdateWork).toHaveBeenCalledWith(88, {
      qualityProfileId: null,
      clearQualityProfile: true,
    });
  });

  it('renders Probe Error, Fallback (Upgradeable), and Mismatch badges on episode files', () => {
    const treeData: MediaWorkDetailTree = {
      work: dummyWork,
      containers: [
        {
          id: 101,
          containerKind: 'season',
          containerNumber: 1,
          title: 'Season 1',
          releaseDate: '2017-12-01',
          monitored: true,
          units: [
            {
              id: 1001,
              unitKind: 'episode',
              unitNumber: 1,
              absoluteNumber: 1,
              title: 'Secrets',
              airOrReleaseDate: '2017-12-01',
              monitored: true,
              requiredAudioLangs: ['de'],
              requiredSubLangs: [],
              qualityCutoffMet: true,
              languageCutoffMet: false,
              files: [
                {
                  id: 501,
                  relativePath: 'Season 01/Dark.S01E01.mkv',
                  sizeBytes: 1500000000,
                  qualityName: 'WEBDL-1080p',
                  detectedAudioLangs: [],
                  detectedSubLangs: [],
                  verifiedStatus: 'ProbeError',
                  inodeHardlinked: false,
                  latestVerification: {
                    verdict: 'ProbeError',
                    overallConfidence: 0,
                    detectedAudioLanguages: [],
                    detectedSubLanguages: [],
                    durationMs: 0,
                    verifiedAt: '2026-11-14T10:00:00Z',
                    failureReason: 'Corrupt ebml header',
                    failureCode: 'corrupt_container',
                  },
                },
              ],
            },
            {
              id: 1002,
              unitKind: 'episode',
              unitNumber: 2,
              absoluteNumber: 2,
              title: 'Lies',
              airOrReleaseDate: '2017-12-01',
              monitored: true,
              requiredAudioLangs: ['de'],
              requiredSubLangs: [],
              qualityCutoffMet: true,
              languageCutoffMet: false,
              files: [
                {
                  id: 502,
                  relativePath: 'Season 01/Dark.S01E02.mkv',
                  sizeBytes: 1600000000,
                  qualityName: 'WEBDL-1080p',
                  detectedAudioLangs: ['ja'],
                  detectedSubLangs: ['de'],
                  verifiedStatus: 'VerifiedPass',
                  inodeHardlinked: false,
                  latestVerification: {
                    verdict: 'VerifiedPass',
                    overallConfidence: 0.95,
                    audioMethod: 'whisper',
                    detectedAudioLanguages: ['ja'],
                    detectedSubLanguages: ['de'],
                    durationMs: 150,
                    verifiedAt: '2026-11-14T10:00:00Z',
                    satisfiesLanguageCutoff: false,
                    matchedPolicyTierLabel: 'Original + DE Sub',
                  },
                },
              ],
            },
            {
              id: 1003,
              unitKind: 'episode',
              unitNumber: 3,
              absoluteNumber: 3,
              title: 'Past and Present',
              airOrReleaseDate: '2017-12-01',
              monitored: true,
              requiredAudioLangs: ['de'],
              requiredSubLangs: [],
              qualityCutoffMet: true,
              languageCutoffMet: false,
              files: [
                {
                  id: 503,
                  relativePath: 'Season 01/Dark.S01E03.mkv',
                  sizeBytes: 1700000000,
                  qualityName: 'WEBDL-1080p',
                  detectedAudioLangs: ['es'],
                  detectedSubLangs: [],
                  verifiedStatus: 'VerifiedFail',
                  inodeHardlinked: false,
                  latestVerification: {
                    verdict: 'VerifiedFail',
                    overallConfidence: 0.91,
                    audioMethod: 'whisper',
                    detectedAudioLanguages: ['es'],
                    detectedSubLanguages: [],
                    durationMs: 140,
                    verifiedAt: '2026-11-14T10:00:00Z',
                    failureReason: 'File audio (es) does not match wanted policy',
                    failureCode: 'language_mismatch',
                  },
                },
              ],
            },
          ],
        },
      ],
    };

    render(
      <CatalogWorkDetailSection
        selectedWorkId={42}
        isTreeLoading={false}
        isTreeError={false}
        treeData={treeData}
        onBackToLibrary={vi.fn()}
        workActionBanner={null}
        onDismissBanner={vi.fn()}
        canWriteCatalog={true}
        canSearchIndexers={true}
        onQuickToggleWorkMonitor={vi.fn()}
        isWorkMonitorPresetPending={false}
        onApplyWorkMonitorPreset={vi.fn()}
        onUpdateWork={vi.fn()}
        profiles={[]}
        collapsedContainers={{}}
        onToggleCollapseContainer={vi.fn()}
        isToggleContainerPending={false}
        onToggleContainerMonitor={vi.fn()}
        searchingContainerId={null}
        isAutoSearchContainerPending={false}
        onAutoSearchContainer={vi.fn()}
        onToggleUnitMonitor={vi.fn()}
        searchingUnitId={null}
        isAutoSearchUnitPending={false}
        onAutoSearchUnit={vi.fn()}
        interactiveTarget={null}
        onSetInteractiveTarget={vi.fn()}
        isInteractiveSearchFetching={false}
        interactiveSearchRows={[]}
        onRefetchInteractiveSearch={vi.fn()}
        interactiveFilterText=""
        onSetInteractiveFilterText={vi.fn()}
        interactiveProtocolFilter="all"
        onSetInteractiveProtocolFilter={vi.fn()}
        interactiveHideRejected={false}
        onSetInteractiveHideRejected={vi.fn()}
        interactiveSortKey="score"
        onSetInteractiveSortKey={vi.fn()}
        interactiveSortDir="desc"
        onSetInteractiveSortDir={vi.fn()}
        expandedRejections={{}}
        onToggleExpandedRejection={vi.fn()}
        grabbingGuid={null}
        isGrabInteractivePending={false}
        onGrabInteractiveRelease={vi.fn()}
      />
    );

    // Episode 1 Probe Error
    expect(screen.getByText('Probe Error')).toBeInTheDocument();

    // Episode 2 Fallback (Upgradeable)
    expect(screen.getByText('Fallback (Upgradeable) 95%')).toBeInTheDocument();

    // Episode 3 Mismatch
    expect(screen.getByText('Mismatch 91%')).toBeInTheDocument();
  });

  it('renders Wanted Language and Original Language in Mass Editor and fires handlers', async () => {
    const user = userEvent.setup();
    const handleSetBulkLanguagePolicy = vi.fn();
    const handleSetBulkOriginalLanguage = vi.fn();

    const dummyPolicies: LanguagePolicySummary[] = [
      {
        id: 1,
        slug: 'german-first',
        name: 'German First',
        verificationMode: '3tier',
        tiers: [],
      },
    ];

    render(
      <CatalogLibraryGridSection
        healthFilterMeta={null}
        isResolveHealthFilterPending={false}
        onResolveHealthFilter={vi.fn()}
        onClearUrlParams={vi.fn()}
        autoClassifyReport={null}
        onDismissAutoClassifyReport={vi.fn()}
        workActionBanner={null}
        onDismissWorkActionBanner={vi.fn()}
        libraries={[dummyLibrary]}
        librariesById={new Map([[1, dummyLibrary]])}
        canWriteCatalog={true}
        onSetShowAddLibModal={vi.fn()}
        isWorksLoading={false}
        filteredWorks={[dummyWork]}
        allWorks={[dummyWork]}
        activeCatalogFilterCount={0}
        onClearAllCatalogFilters={vi.fn()}
        catalogFilterText=""
        massEditMode={true}
        selectedWorkIds={new Set([42])}
        onSetSelectedWorkIds={vi.fn()}
        viewMode="posters"
        onOpenWork={vi.fn()}
        onQuickToggleWorkMonitor={vi.fn()}
        onSetShowDeleteConfirmModal={vi.fn()}
        isBulkEditPending={false}
        onBulkEditWorks={vi.fn()}
        bulkMonitored="keep"
        onSetBulkMonitored={vi.fn()}
        bulkMonitorPreset="keep"
        onSetBulkMonitorPreset={vi.fn()}
        bulkQualityProfileId="keep"
        onSetBulkQualityProfileId={vi.fn()}
        profiles={[]}
        languagePolicies={dummyPolicies}
        bulkLanguagePolicyId="keep"
        onSetBulkLanguagePolicyId={handleSetBulkLanguagePolicy}
        bulkOriginalLanguage="keep"
        onSetBulkOriginalLanguage={handleSetBulkOriginalLanguage}
        bulkMinAvailability="keep"
        onSetBulkMinAvailability={vi.fn()}
        bulkLibraryId="keep"
        onSetBulkLibraryId={vi.fn()}
        bulkMoveFiles={false}
        onSetBulkMoveFiles={vi.fn()}
        bulkTags=""
        onSetBulkTags={vi.fn()}
        bulkTagMode="replace"
        onSetBulkTagMode={vi.fn()}
      />
    );

    const bulkLangSelect = screen.getByRole('combobox', { name: 'Bulk Wanted Language' });
    expect(bulkLangSelect).toBeInTheDocument();
    await user.selectOptions(bulkLangSelect, '1');
    expect(handleSetBulkLanguagePolicy).toHaveBeenCalledWith('1');

    const bulkOrigLangSelect = screen.getByRole('combobox', { name: 'Bulk Original Language' });
    expect(bulkOrigLangSelect).toBeInTheDocument();
    await user.selectOptions(bulkOrigLangSelect, 'ja');
    expect(handleSetBulkOriginalLanguage).toHaveBeenCalledWith('ja');
  });
});
