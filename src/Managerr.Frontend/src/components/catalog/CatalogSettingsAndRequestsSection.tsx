import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  ArrowDown,
  ArrowLeft,
  ArrowUp,
  ArrowUpDown,
  Calendar as CalendarIcon,
  CalendarRange,
  Check,
  ChevronLeft,
  ChevronRight,
  Database,
  Edit2,
  FileCheck,
  FilePlus2,
  FileText,
  FolderPlus,
  Globe,
  Languages,
  LayoutGrid,
  List,
  Plus,
  Radar,
  RefreshCw,
  Search,
  Send,
  Settings,
  SlidersHorizontal,
  Tag,
  Trash2,
  User,
  X,
} from 'lucide-react';
import clsx from 'clsx';
import {
  BulkFileRenameModal,
  DiskScanReportBanner,
  MediaManagementToolsPanel,
  type DiskScanReportDto,
} from '../MediaManagementToolsPanel';
import {
  ArrAlert,
  ArrButton,
  ArrLabel,
  ArrModal,
  ArrSettingsCard,
  FieldSet,
  FilterBubble,
  FormGroup,
  PageToolbar,
  PageToolbarButton,
  PageToolbarSeparator,
  toggleFilterSetValue,
} from '../arr/ArrPrimitives';
import { PathBrowserInput } from '../arr/PathBrowserModal';
import {
  ADD_VERTICAL_TABS,
  type CatalogSortField,
  type CatalogSubSection,
  type CatalogViewMode,
  type InteractiveSearchTarget,
  type LibrarySummary,
  type MediaRequestSummary,
  type MediaWorkDetailTree,
  type MediaWorkSummary,
  type ProvidersCatalogResponse,
  type QualityProfileSummary,
} from './catalogTypesAndHelpers';
import { apiFetch, formatBytes } from '../../lib/api';

interface MetadataProviderSettingsItem {
  key: string;
  displayName: string;
  requiresApiKey: boolean;
  configurationStatus: string;
  defaultBaseUrl: string | null;
  customBaseUrl: string | null;
  hasApiKey: boolean;
  maskedApiKey: string | null;
  anidbClientVer?: string | null;
  anidbTitleSearchUrl?: string | null;
}

interface CustomMetadataWebhookDto {
  id: number;
  name: string;
  endpointUrl?: string;
  baseUrl?: string;
  url?: string;
  vertical?: string;
  supportedVerticals?: string[];
  isEnabled?: boolean;
}

interface CatalogSettingsAndRequestsSectionProps {
  section: Extract<CatalogSubSection, 'requests' | 'media-management' | 'metadata'>;
  requests: MediaRequestSummary[];
  canApprove: boolean;
  onDecideRequest: (id: number, approve: boolean) => void;
  scanReportBanner: { libName: string; report: DiskScanReportDto } | null;
  onDismissScanReport: () => void;
  libraries: LibrarySummary[];
  works: MediaWorkSummary[];
  profiles: QualityProfileSummary[];
  canWriteCatalog: boolean;
  canAdmin?: boolean;
  isLibraryScanPending: boolean;
  scanningLibraryId?: number;
  onScanLibrary: (libId: number) => void;
  onOpenRenamePreview: (config: {
    isOpen: boolean;
    title: string;
    previewUrl: string;
    executeUrl: string;
  }) => void;
  providersData?: ProvidersCatalogResponse;
  cwProvider: string;
  onChangeCwProvider: (v: string) => void;
  cwId: string;
  onChangeCwId: (v: string) => void;
  crosswalkData?: Record<string, string>;
}

export function CatalogSettingsAndRequestsSection({
  section,
  requests,
  canApprove,
  onDecideRequest,
  scanReportBanner,
  onDismissScanReport,
  libraries,
  works,
  profiles,
  canWriteCatalog,
  canAdmin = false,
  isLibraryScanPending,
  scanningLibraryId,
  onScanLibrary,
  onOpenRenamePreview,
  providersData,
  cwProvider,
  onChangeCwProvider,
  cwId,
  onChangeCwId,
  crosswalkData,
}: CatalogSettingsAndRequestsSectionProps) {
  const qc = useQueryClient();

  // Library Edit Modal State
  const [editingLib, setEditingLib] = useState<LibrarySummary | null>(null);
  const [editLibName, setEditLibName] = useState('');
  const [editLibPath, setEditLibPath] = useState('');
  const [editLibPlexSection, setEditLibPlexSection] = useState('');
  const [editLibProfileId, setEditLibProfileId] = useState<number | null>(null);
  const [editLibWaterfall, setEditLibWaterfall] = useState('');
  const [libEditError, setLibEditError] = useState<string | null>(null);

  // Metadata Provider Settings Modal State
  const [editingProvider, setEditingProvider] = useState<MetadataProviderSettingsItem | null>(null);
  const [provApiKey, setProvApiKey] = useState('');
  const [provClearApiKey, setProvClearApiKey] = useState(false);
  const [provCustomBaseUrl, setProvCustomBaseUrl] = useState('');
  const [provAnidbClientVer, setProvAnidbClientVer] = useState('');
  const [provAnidbTitleSearchUrl, setProvAnidbTitleSearchUrl] = useState('');
  const [provBanner, setProvBanner] = useState<{ kind: 'success' | 'danger'; text: string } | null>(null);

  // Custom Metadata Webhooks Modal State
  const [showWebhookModal, setShowWebhookModal] = useState(false);
  const [whName, setWhName] = useState('');
  const [whUrl, setWhUrl] = useState('');
  const [whVertical, setWhVertical] = useState('movies');
  const [whAuthHeaderName, setWhAuthHeaderName] = useState('');
  const [whAuthHeaderValue, setWhAuthHeaderValue] = useState('');
  const [whError, setWhError] = useState<string | null>(null);

  const providerSettingsQuery = useQuery({
    queryKey: ['metadata-provider-settings'],
    queryFn: async () => {
      try {
        const res = await apiFetch<MetadataProviderSettingsItem[]>('/api/v1/metadata/providers/settings');
        return Array.isArray(res) ? res : [];
      } catch {
        return [] as MetadataProviderSettingsItem[];
      }
    },
    enabled: section === 'metadata' && canAdmin,
  });

  const webhooksQuery = useQuery({
    queryKey: ['metadata-webhooks'],
    queryFn: async () => {
      try {
        const res = await apiFetch<CustomMetadataWebhookDto[]>('/api/v1/metadata/webhooks');
        return Array.isArray(res) ? res : [];
      } catch {
        return [] as CustomMetadataWebhookDto[];
      }
    },
    enabled: section === 'metadata',
  });

  const saveProviderSettingsMutation = useMutation({
    mutationFn: async () => {
      if (!editingProvider) return;
      const body: Record<string, unknown> = {
        clearApiKey: provClearApiKey,
        customBaseUrl: provCustomBaseUrl.trim(),
      };
      if (!provClearApiKey && provApiKey.trim()) {
        body.apiKey = provApiKey.trim();
      }
      if (editingProvider.key.toLowerCase() === 'anidb') {
        body.anidbClientVer = provAnidbClientVer.trim();
        body.anidbTitleSearchUrl = provAnidbTitleSearchUrl.trim();
      }
      return apiFetch<MetadataProviderSettingsItem>(
        `/api/v1/metadata/providers/${encodeURIComponent(editingProvider.key)}/settings`,
        {
          method: 'PUT',
          body: JSON.stringify(body),
        },
      );
    },
    onSuccess: (updated) => {
      setProvBanner({
        kind: 'success',
        text: `Updated settings for ${updated?.displayName ?? editingProvider?.displayName ?? 'provider'}.`,
      });
      setEditingProvider(null);
      qc.invalidateQueries({ queryKey: ['metadata-provider-settings'] });
      qc.invalidateQueries({ queryKey: ['metadata-providers'] });
      qc.invalidateQueries({ queryKey: ['providers'] });
    },
    onError: (err: Error) => {
      setProvBanner({ kind: 'danger', text: err.message });
    },
  });

  const createWebhookMutation = useMutation({
    mutationFn: () =>
      apiFetch<CustomMetadataWebhookDto>('/api/v1/metadata/webhooks', {
        method: 'POST',
        body: JSON.stringify({
          name: whName.trim(),
          endpointUrl: whUrl.trim(),
          baseUrl: whUrl.trim(),
          url: whUrl.trim(),
          vertical: whVertical,
          supportedVerticals: [whVertical],
          authHeaderName: whAuthHeaderName.trim() || null,
          authHeaderValue: whAuthHeaderValue.trim() || null,
          isEnabled: true,
        }),
      }),
    onSuccess: () => {
      setShowWebhookModal(false);
      setWhName('');
      setWhUrl('');
      setWhAuthHeaderName('');
      setWhAuthHeaderValue('');
      setWhError(null);
      qc.invalidateQueries({ queryKey: ['metadata-webhooks'] });
      qc.invalidateQueries({ queryKey: ['metadata-providers'] });
    },
    onError: (err: Error) => {
      setWhError(err.message);
    },
  });

  const deleteWebhookMutation = useMutation({
    mutationFn: (id: number) => apiFetch<void>(`/api/v1/metadata/webhooks/${id}`, { method: 'DELETE' }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['metadata-webhooks'] });
      qc.invalidateQueries({ queryKey: ['metadata-providers'] });
    },
  });

  const updateLibraryMutation = useMutation({
    mutationFn: async () => {
      if (!editingLib) return;
      const waterfall = editLibWaterfall
        .split(',')
        .map((s) => s.trim())
        .filter(Boolean);
      return apiFetch<LibrarySummary>(`/api/v1/libraries/${editingLib.id}`, {
        method: 'PATCH',
        body: JSON.stringify({
          name: editLibName.trim(),
          rootFolderPath: editLibPath.trim(),
          plexSectionId: editLibPlexSection.trim() ? Number(editLibPlexSection.trim()) : null,
          defaultProfileId: editLibProfileId,
          clearDefaultProfile: editLibProfileId === null,
          metadataWaterfall: waterfall.length > 0 ? waterfall : undefined,
        }),
      });
    },
    onSuccess: () => {
      setEditingLib(null);
      setLibEditError(null);
      qc.invalidateQueries({ queryKey: ['libraries'] });
      qc.invalidateQueries({ queryKey: ['works'] });
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
    },
    onError: (err: Error) => {
      setLibEditError(err.message);
    },
  });

  function openEditLibraryModal(lib: LibrarySummary) {
    setEditingLib(lib);
    setEditLibName(lib.name);
    setEditLibPath(lib.rootFolderPath);
    setEditLibPlexSection(lib.plexSectionId !== null && lib.plexSectionId !== undefined ? String(lib.plexSectionId) : '');
    setEditLibProfileId(lib.defaultProfileId ?? null);
    setEditLibWaterfall((lib.metadataWaterfall ?? []).join(', '));
    setLibEditError(null);
  }

  function openProviderConfigModal(
    p: {
      key: string;
      displayName: string;
      requiresApiKey?: boolean;
      configurationStatus?: string | null;
      hasApiKey?: boolean;
    },
    settingsItem?: MetadataProviderSettingsItem,
  ) {
    setEditingProvider(
      settingsItem ?? {
        key: p.key,
        displayName: p.displayName,
        requiresApiKey: Boolean(p.requiresApiKey),
        configurationStatus: p.configurationStatus ?? 'configured',
        defaultBaseUrl: null,
        customBaseUrl: null,
        hasApiKey: Boolean(p.hasApiKey),
        maskedApiKey: null,
        anidbClientVer: null,
        anidbTitleSearchUrl: null,
      },
    );
    setProvApiKey('');
    setProvClearApiKey(false);
    setProvCustomBaseUrl(settingsItem?.customBaseUrl ?? '');
    setProvAnidbClientVer(settingsItem?.anidbClientVer ?? '');
    setProvAnidbTitleSearchUrl(settingsItem?.anidbTitleSearchUrl ?? '');
    setProvBanner(null);
  }

  if (section === 'requests') {
    return (
      <FieldSet legend="Requests">
        {requests.length === 0 ? (
          <div className="text-[13px] text-[#909293]">No media requests in queue.</div>
        ) : (
          <div className="overflow-x-auto">
            <table className="arr-table">
              <thead>
                <tr>
                  <th>Title</th>
                  <th>Vertical</th>
                  <th>Requested By</th>
                  <th>Audio Override</th>
                  <th>Created</th>
                  <th>Status</th>
                  {canApprove && <th className="text-right">Actions</th>}
                </tr>
              </thead>
              <tbody>
                {requests.map((r) => (
                  <tr key={r.id}>
                    <td className="font-semibold text-[#eeeeee]">{r.title}</td>
                    <td>
                      <ArrLabel kind="default">{r.mediaVertical}</ArrLabel>
                    </td>
                    <td>{r.requesterUsername}</td>
                    <td className="font-mono text-[12px]">{r.requestedAudioLang ?? '-'}</td>
                    <td className="text-[12px] text-[#909293]">{new Date(r.createdAt).toLocaleDateString()}</td>
                    <td>
                      <ArrLabel
                        kind={
                          r.status === 'approved'
                            ? 'success'
                            : r.status === 'declined'
                              ? 'danger'
                              : 'warning'
                        }
                      >
                        {r.status}
                      </ArrLabel>
                    </td>
                    {canApprove && (
                      <td className="text-right">
                        {r.status === 'pending' && (
                          <div className="inline-flex items-center gap-1.5">
                            <ArrButton
                              variant="success"
                              className="h-[26px] px-2 text-[11px]"
                              onClick={() => onDecideRequest(r.id, true)}
                            >
                              <Check className="h-3.5 w-3.5" /> Approve
                            </ArrButton>
                            <ArrButton
                              variant="danger"
                              className="h-[26px] px-2 text-[11px]"
                              onClick={() => onDecideRequest(r.id, false)}
                            >
                              <X className="h-3.5 w-3.5" /> Decline
                            </ArrButton>
                          </div>
                        )}
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </FieldSet>
    );
  }

  if (section === 'media-management') {
    return (
      <>
        {scanReportBanner && (
          <div className="mb-5">
            <DiskScanReportBanner
              libraryName={scanReportBanner.libName}
              report={scanReportBanner.report}
              onDismiss={onDismissScanReport}
            />
          </div>
        )}
        <FieldSet legend="Root Folders & PMS Library Bindings">
          {libraries.length === 0 ? (
            <div className="text-[13px] text-[#909293]">
              No root folders configured. Click &ldquo;Add Root Folder&rdquo; in the toolbar.
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table className="arr-table">
                <thead>
                  <tr>
                    <th>Name</th>
                    <th>Path</th>
                    <th>Media Vertical</th>
                    <th>Default Profile</th>
                    <th>PMS Section ID</th>
                    <th>Metadata Waterfall</th>
                    <th className="text-right">Works</th>
                    <th className="text-right">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {libraries.map((lib) => {
                    const boundProfile = lib.defaultProfileId
                      ? profiles.find((p) => p.id === lib.defaultProfileId)
                      : null;
                    const scanningThis = isLibraryScanPending && scanningLibraryId === lib.id;
                    return (
                      <tr key={lib.id}>
                        <td className="font-semibold text-[#eeeeee]">{lib.name}</td>
                        <td className="font-mono text-arr-accent">
                          <div>{lib.rootFolderPath}</div>
                          {typeof lib.freeSpaceBytes === 'number' && typeof lib.totalSpaceBytes === 'number' && lib.totalSpaceBytes > 0 && (
                            <div className="mt-1 max-w-[200px]">
                              <div className="flex items-center justify-between text-[11px] text-[#909293] mb-0.5">
                                <span>{formatBytes(lib.freeSpaceBytes)} free</span>
                                <span>{formatBytes(lib.totalSpaceBytes)}</span>
                              </div>
                              <div className="h-1.5 w-full bg-[#1e1e1e] rounded-full overflow-hidden border border-arr-card-border">
                                <div
                                  className="h-full bg-arr-accent rounded-full"
                                  style={{ width: `${Math.min(100, Math.max(0, ((lib.totalSpaceBytes - lib.freeSpaceBytes) / lib.totalSpaceBytes) * 100))}%` }}
                                />
                              </div>
                            </div>
                          )}
                        </td>
                        <td>
                          <ArrLabel kind="primary">{lib.mediaType}</ArrLabel>
                        </td>
                        <td>
                          {boundProfile ? (
                            <ArrLabel kind="success">{boundProfile.name}</ArrLabel>
                          ) : lib.mediaType === 'movies' || lib.mediaType === 'tv' ? (
                            <ArrLabel kind="success">German First — Best Quality</ArrLabel>
                          ) : (
                            <span className="text-[12px] text-[#777777]">Vertical Default</span>
                          )}
                        </td>
                        <td className="font-mono">{lib.plexSectionId ?? '-'}</td>
                        <td className="font-mono text-[12px] text-[#909293]">{lib.metadataWaterfall.join(' → ')}</td>
                        <td className="text-right font-semibold">{lib.workCount}</td>
                        <td className="text-right whitespace-nowrap">
                          <div className="inline-flex items-center gap-1.5 justify-end">
                            <ArrButton
                              variant="default"
                              className="!h-[28px] !px-2.5 !text-[11px]"
                              disabled={!canWriteCatalog}
                              onClick={() => openEditLibraryModal(lib)}
                            >
                              <Edit2 className="h-3 w-3" /> Edit
                            </ArrButton>
                            <ArrButton
                              variant="default"
                              className="!h-[28px] !px-2.5 !text-[11px]"
                              disabled={!canWriteCatalog || scanningThis}
                              busy={scanningThis}
                              onClick={() => onScanLibrary(lib.id)}
                            >
                              <RefreshCw className="h-3 w-3" /> Refresh &amp; Scan
                            </ArrButton>
                            <ArrButton
                              variant="default"
                              className="!h-[28px] !px-2.5 !text-[11px]"
                              disabled={!canWriteCatalog}
                              onClick={() =>
                                onOpenRenamePreview({
                                  isOpen: true,
                                  title: `Bulk File Rename Preview — ${lib.name}`,
                                  previewUrl: `/api/v1/libraries/${lib.id}/rename-preview`,
                                  executeUrl: `/api/v1/libraries/${lib.id}/rename-execute`,
                                })
                              }
                            >
                              <FileText className="h-3 w-3" /> Preview Rename
                            </ArrButton>
                          </div>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
        </FieldSet>

        <MediaManagementToolsPanel
          libraries={libraries}
          works={works}
          canWrite={canWriteCatalog}
        />

        {editingLib && (
          <ArrModal
            title={`Edit Root Folder / Library — ${editingLib.name}`}
            onClose={() => setEditingLib(null)}
            maxWidth="max-w-xl"
            footer={
              <>
                <ArrButton onClick={() => setEditingLib(null)}>Cancel</ArrButton>
                <ArrButton
                  variant="primary"
                  disabled={!editLibName.trim() || !editLibPath.trim()}
                  busy={updateLibraryMutation.isPending}
                  onClick={() => updateLibraryMutation.mutate()}
                >
                  Save Changes
                </ArrButton>
              </>
            }
          >
            {libEditError && (
              <ArrAlert kind="danger" className="mb-4">
                {libEditError}
              </ArrAlert>
            )}
            <FormGroup label="Name">
              <input
                value={editLibName}
                onChange={(e) => setEditLibName(e.target.value)}
                className="arr-input"
              />
            </FormGroup>
            <FormGroup label="Root Folder Path">
              <PathBrowserInput
                value={editLibPath}
                onChange={setEditLibPath}
                placeholder="/media/movies"
                label="Root Folder Path"
                modalTitle="Select Root Folder"
                allowCreateFolder
              />
            </FormGroup>
            <FormGroup label="Default Quality Profile" helpText="Profile automatically assigned to works added to this library.">
              <select
                value={editLibProfileId ?? ''}
                onChange={(e) => setEditLibProfileId(e.target.value ? Number(e.target.value) : null)}
                className="arr-input"
              >
                <option value="">Vertical Default</option>
                {profiles.map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name} ({p.mediaVertical})
                  </option>
                ))}
              </select>
            </FormGroup>
            <FormGroup
              label="Metadata Waterfall"
              helpText="Comma-separated ordered provider keys (e.g. tmdb, skyhook, anilist, anidb)."
            >
              <input
                value={editLibWaterfall}
                onChange={(e) => setEditLibWaterfall(e.target.value)}
                placeholder="tmdb, skyhook"
                className="arr-input font-mono text-[12px]"
              />
            </FormGroup>
            <FormGroup label="Plex Section ID" helpText="PMS section number used for inherited library RBAC.">
              <input
                value={editLibPlexSection}
                onChange={(e) => setEditLibPlexSection(e.target.value)}
                placeholder="1"
                className="arr-input font-mono"
              />
            </FormGroup>
          </ArrModal>
        )}
      </>
    );
  }

  return (
    <>
      {providersData?.cacheStats && (
        <div className="mb-6 p-4 bg-arr-card border border-arr-card-border rounded-[4px]">
          <div className="flex items-center justify-between pb-3 mb-3 border-b border-arr-card-border">
            <div className="flex items-center gap-2">
              <Database className="h-5 w-5 text-arr-accent" />
              <span className="text-[15px] font-semibold text-[#eeeeee]">
                Local SQLite Metadata Cache &amp; Anti-Spam Policy
              </span>
            </div>
            <ArrLabel kind="success">
              {providersData.cacheStats.activeEntries} Active Cached Queries
            </ArrLabel>
          </div>
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-4 text-[12px]">
            <div className="bg-[#1f1f1f] p-2.5 rounded border border-arr-card-border">
              <div className="text-[#909293] uppercase text-[10px] font-semibold tracking-wider">Total Entries</div>
              <div className="text-[16px] font-semibold text-[#eeeeee] mt-0.5">
                {providersData.cacheStats.totalEntries}
              </div>
            </div>
            <div className="bg-[#1f1f1f] p-2.5 rounded border border-arr-card-border">
              <div className="text-[#909293] uppercase text-[10px] font-semibold tracking-wider">Search TTL</div>
              <div className="text-[16px] font-semibold text-arr-accent mt-0.5">
                {providersData.cacheStats.searchTtlHours}h
              </div>
            </div>
            <div className="bg-[#1f1f1f] p-2.5 rounded border border-arr-card-border">
              <div className="text-[#909293] uppercase text-[10px] font-semibold tracking-wider">Lookup TTL</div>
              <div className="text-[16px] font-semibold text-[#27c24c] mt-0.5">
                {providersData.cacheStats.lookupTtlHours}h
              </div>
            </div>
            <div className="bg-[#1f1f1f] p-2.5 rounded border border-arr-card-border">
              <div className="text-[#909293] uppercase text-[10px] font-semibold tracking-wider">Nightly Refresh</div>
              <div className="text-[16px] font-semibold text-[#9b6ef3] mt-0.5">
                Every {providersData.cacheStats.nightlyRefreshHours}h
              </div>
            </div>
          </div>
          <p className="text-[11.5px] text-[#888888] mt-3">
            Protects upstream rate limits for TMDB, TVDB, AniList, MusicBrainz, and OpenLibrary with persistent disk-backed caching and automated background expiry.
          </p>
        </div>
      )}

      {provBanner && (
        <ArrAlert kind={provBanner.kind} className="mb-4">
          {provBanner.text}
        </ArrAlert>
      )}

      <FieldSet legend="Metadata Sources">
        <div className="flex flex-wrap gap-4">
          {(providersData?.builtInProviders ?? []).map((p) => {
            const settingsItem = providerSettingsQuery.data?.find(
              (s) => s.key.toLowerCase() === p.key.toLowerCase(),
            );
            const effectiveHasApiKey = settingsItem ? settingsItem.hasApiKey : p.hasApiKey;
            const effectiveStatus = settingsItem ? settingsItem.configurationStatus : p.configurationStatus;
            const isNotConfigured =
              effectiveStatus === 'not_configured' || (p.requiresApiKey && !effectiveHasApiKey);
            return (
              <ArrSettingsCard
                key={p.key}
                title={p.displayName}
                footer={
                  <>
                    <span className="font-mono text-[11px] text-[#909293]">{p.key}</span>
                    <div className="inline-flex flex-wrap items-center gap-1.5">
                      {isNotConfigured && (
                        <ArrLabel kind="warning">Not configured (API key required)</ArrLabel>
                      )}
                      {!isNotConfigured && p.requiresApiKey && (
                        <ArrLabel kind="success">Key Set</ArrLabel>
                      )}
                      <ArrLabel kind={p.isBuiltInArrDefault ? 'primary' : 'success'}>
                        {p.isBuiltInArrDefault ? '*arr Default' : 'Alternative'}
                      </ArrLabel>
                      {canAdmin && (
                        <ArrButton
                          variant="default"
                          className="!h-[24px] !px-2 !text-[11px]"
                          onClick={() => openProviderConfigModal(p, settingsItem)}
                        >
                          <Settings className="h-3 w-3" /> Configure
                        </ArrButton>
                      )}
                    </div>
                  </>
                }
              >
                <p className="line-clamp-2">{p.description}</p>
                {settingsItem?.customBaseUrl && (
                  <div className="text-[11px] font-mono text-arr-accent truncate pt-1">
                    {settingsItem.customBaseUrl}
                  </div>
                )}
                <div className="flex flex-wrap gap-1 pt-1">
                  {p.supportedVerticals.map((v) => (
                    <ArrLabel key={v} kind="default">
                      {v}
                    </ArrLabel>
                  ))}
                </div>
              </ArrSettingsCard>
            );
          })}
        </div>
      </FieldSet>

      <FieldSet
        legend={
          <div className="flex items-center gap-2">
            <Globe className="h-4 w-4 text-arr-accent" />
            <span>Custom Metadata Webhooks</span>
          </div>
        }
        action={
          canAdmin && (
            <ArrButton
              variant="success"
              onClick={() => {
                setWhError(null);
                setShowWebhookModal(true);
              }}
            >
              <Plus className="h-3.5 w-3.5" /> Add Metadata Webhook
            </ArrButton>
          )
        }
      >
        {(webhooksQuery.data ?? []).length === 0 ? (
          <div className="text-[13px] text-[#909293]">
            No custom HTTP metadata webhooks registered.
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="arr-table">
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Endpoint URL</th>
                  <th>Vertical</th>
                  {canAdmin && <th className="text-right">Actions</th>}
                </tr>
              </thead>
              <tbody>
                {(webhooksQuery.data ?? []).map((wh) => (
                  <tr key={wh.id}>
                    <td className="font-semibold text-[#eeeeee]">{wh.name}</td>
                    <td className="font-mono text-[12px] text-arr-accent">
                      {wh.endpointUrl || wh.baseUrl || wh.url || '-'}
                    </td>
                    <td>
                      <ArrLabel kind="default">
                        {wh.vertical || (wh.supportedVerticals ?? []).join(', ') || 'all'}
                      </ArrLabel>
                    </td>
                    {canAdmin && (
                      <td className="text-right">
                        <ArrButton
                          variant="danger"
                          className="!h-[26px] !px-2 !text-[11px]"
                          busy={deleteWebhookMutation.isPending && deleteWebhookMutation.variables === wh.id}
                          onClick={() => deleteWebhookMutation.mutate(wh.id)}
                        >
                          <Trash2 className="h-3 w-3" /> Delete
                        </ArrButton>
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </FieldSet>

      {editingProvider && (
        <ArrModal
          title={`Configure Provider — ${editingProvider.displayName}`}
          onClose={() => setEditingProvider(null)}
          maxWidth="max-w-lg"
          footer={
            <>
              <ArrButton onClick={() => setEditingProvider(null)}>Cancel</ArrButton>
              <ArrButton
                variant="primary"
                busy={saveProviderSettingsMutation.isPending}
                onClick={() => saveProviderSettingsMutation.mutate()}
              >
                Save Settings
              </ArrButton>
            </>
          }
        >
          <FormGroup
            label="API Key"
            helpText={
              editingProvider.hasApiKey
                ? `Current key: ${editingProvider.maskedApiKey ?? '********'}. Leave blank to preserve.`
                : editingProvider.requiresApiKey
                  ? 'API key is required for this metadata provider.'
                  : 'Optional API key override.'
            }
          >
            <div className="space-y-2">
              <input
                type="password"
                value={provApiKey}
                disabled={provClearApiKey}
                onChange={(e) => setProvApiKey(e.target.value)}
                placeholder={
                  editingProvider.hasApiKey ? '•••••••• (saved — leave blank to keep)' : 'Enter API Key'
                }
                className="arr-input font-mono"
              />
              {editingProvider.hasApiKey && (
                <label className="inline-flex items-center gap-2 text-[12px] text-[#f05050] cursor-pointer">
                  <input
                    type="checkbox"
                    checked={provClearApiKey}
                    onChange={(e) => setProvClearApiKey(e.target.checked)}
                    className="accent-[#f05050]"
                  />
                  Clear saved API key
                </label>
              )}
            </div>
          </FormGroup>

          <FormGroup
            label="Custom Base URL"
            helpText={
              editingProvider.defaultBaseUrl
                ? `Default: ${editingProvider.defaultBaseUrl}. Leave blank to use default.`
                : 'Optional custom endpoint or proxy URL.'
            }
          >
            <input
              value={provCustomBaseUrl}
              onChange={(e) => setProvCustomBaseUrl(e.target.value)}
              placeholder={editingProvider.defaultBaseUrl ?? 'https://...'}
              className="arr-input font-mono text-[12px]"
            />
          </FormGroup>

          {editingProvider.key.toLowerCase() === 'anidb' && (
            <>
              <FormGroup label="AniDB Client Version" helpText="Registered AniDB HTTP clientver parameter.">
                <input
                  value={provAnidbClientVer}
                  onChange={(e) => setProvAnidbClientVer(e.target.value)}
                  placeholder="1"
                  className="arr-input font-mono"
                />
              </FormGroup>
              <FormGroup label="AniDB Title Search URL" helpText="Optional custom AniDB title dump / search endpoint.">
                <input
                  value={provAnidbTitleSearchUrl}
                  onChange={(e) => setProvAnidbTitleSearchUrl(e.target.value)}
                  placeholder="https://anidb.net/api/anime-titles.xml.gz"
                  className="arr-input font-mono text-[12px]"
                />
              </FormGroup>
            </>
          )}
        </ArrModal>
      )}

      {showWebhookModal && (
        <ArrModal
          title="Add Custom Metadata Webhook"
          onClose={() => setShowWebhookModal(false)}
          maxWidth="max-w-lg"
          footer={
            <>
              <ArrButton onClick={() => setShowWebhookModal(false)}>Cancel</ArrButton>
              <ArrButton
                variant="primary"
                disabled={!whName.trim() || !whUrl.trim()}
                busy={createWebhookMutation.isPending}
                onClick={() => createWebhookMutation.mutate()}
              >
                Save Webhook
              </ArrButton>
            </>
          }
        >
          {whError && (
            <ArrAlert kind="danger" className="mb-4">
              {whError}
            </ArrAlert>
          )}
          <FormGroup label="Name">
            <input
              value={whName}
              onChange={(e) => setWhName(e.target.value)}
              placeholder="e.g. Internal Metadata Proxy"
              className="arr-input"
            />
          </FormGroup>
          <FormGroup label="Endpoint URL">
            <input
              value={whUrl}
              onChange={(e) => setWhUrl(e.target.value)}
              placeholder="https://metadata.local/api"
              className="arr-input font-mono text-[12px]"
            />
          </FormGroup>
          <FormGroup label="Media Vertical">
            <select value={whVertical} onChange={(e) => setWhVertical(e.target.value)} className="arr-input">
              <option value="movies">Movies</option>
              <option value="tv">TV Series</option>
              <option value="anime">Anime</option>
              <option value="music">Music</option>
              <option value="ebooks">Ebooks</option>
              <option value="audiobooks">Audiobooks</option>
              <option value="comics">Comics</option>
            </select>
          </FormGroup>
          <FormGroup label="Auth Header Name" helpText="Optional HTTP header name (e.g. Authorization or X-Api-Key).">
            <input
              value={whAuthHeaderName}
              onChange={(e) => setWhAuthHeaderName(e.target.value)}
              placeholder="X-Api-Key"
              className="arr-input font-mono text-[12px]"
            />
          </FormGroup>
          <FormGroup label="Auth Header Value" helpText="Optional header value / bearer token.">
            <input
              type="password"
              value={whAuthHeaderValue}
              onChange={(e) => setWhAuthHeaderValue(e.target.value)}
              placeholder="Secret token"
              className="arr-input font-mono text-[12px]"
            />
          </FormGroup>
        </ArrModal>
      )}

      <FieldSet legend="External ID Crosswalk Lookup">
        <FormGroup label="Source Provider">
          <input
            value={cwProvider}
            onChange={(e) => onChangeCwProvider(e.target.value)}
            placeholder="anilist, tvdb, openlibrary..."
            className="arr-input"
          />
        </FormGroup>
        <FormGroup label="External ID">
          <input
            value={cwId}
            onChange={(e) => onChangeCwId(e.target.value)}
            placeholder="154587"
            className="arr-input font-mono"
          />
        </FormGroup>
        <FormGroup label="Resolved IDs">
          <div className="flex flex-wrap gap-1.5 pt-1.5">
            {Object.entries(crosswalkData ?? {}).map(([k, v]) => (
              <ArrLabel key={k} kind="inverse">
                {k}: {v}
              </ArrLabel>
            ))}
          </div>
        </FormGroup>
      </FieldSet>
    </>
  );
}

export interface CatalogModalsSectionProps {
  showAddLibModal: boolean;
  onSetShowAddLibModal: (v: boolean) => void;
  libName: string;
  onSetLibName: (v: string) => void;
  libType: string;
  onSetLibType: (v: string) => void;
  libPath: string;
  onSetLibPath: (v: string) => void;
  libPlexSection: string;
  onSetLibPlexSection: (v: string) => void;
  isCreateLibPending: boolean;
  onCreateLib: () => void;
  showRequestModal: boolean;
  onSetShowRequestModal: (v: boolean) => void;
  reqTitle: string;
  onSetReqTitle: (v: string) => void;
  reqAudioLang: string;
  onSetReqAudioLang: (v: string) => void;
  activeLibId: number | null;
  onSetFormLibraryId: (v: number) => void;
  libraries: LibrarySummary[];
  isSubmitRequestPending: boolean;
  onSubmitRequest: () => void;
  renameModalConfig: {
    isOpen: boolean;
    title: string;
    previewUrl: string;
    executeUrl: string;
  } | null;
  onCloseRenameModal: () => void;
  showDeleteConfirmModal: boolean;
  onSetShowDeleteConfirmModal: (v: boolean) => void;
  selectedWorkCount: number;
  deleteFilesOnDisk: boolean;
  onSetDeleteFilesOnDisk: (v: boolean) => void;
  isDeleteSelectedPending: boolean;
  onDeleteSelectedWorks: () => void;
}

export function CatalogModalsSection({
  showAddLibModal,
  onSetShowAddLibModal,
  libName,
  onSetLibName,
  libType,
  onSetLibType,
  libPath,
  onSetLibPath,
  libPlexSection,
  onSetLibPlexSection,
  isCreateLibPending,
  onCreateLib,
  showRequestModal,
  onSetShowRequestModal,
  reqTitle,
  onSetReqTitle,
  reqAudioLang,
  onSetReqAudioLang,
  activeLibId,
  onSetFormLibraryId,
  libraries,
  isSubmitRequestPending,
  onSubmitRequest,
  renameModalConfig,
  onCloseRenameModal,
  showDeleteConfirmModal,
  onSetShowDeleteConfirmModal,
  selectedWorkCount,
  deleteFilesOnDisk,
  onSetDeleteFilesOnDisk,
  isDeleteSelectedPending,
  onDeleteSelectedWorks,
}: CatalogModalsSectionProps) {
  const qc = useQueryClient();
  const [addLibProfileId, setAddLibProfileId] = useState<number | null>(null);
  const [addLibWaterfall, setAddLibWaterfall] = useState('');
  const [addLibError, setAddLibError] = useState<string | null>(null);

  const modalProfilesQuery = useQuery({
    queryKey: ['profiles'],
    queryFn: async () => {
      try {
        const res = await apiFetch<QualityProfileSummary[]>('/api/v1/profiles');
        return Array.isArray(res) ? res : [];
      } catch {
        return [] as QualityProfileSummary[];
      }
    },
    enabled: showAddLibModal,
  });

  const createCustomLibMutation = useMutation({
    mutationFn: () => {
      const waterfall = addLibWaterfall
        .split(',')
        .map((s) => s.trim())
        .filter(Boolean);
      return apiFetch<LibrarySummary>('/api/v1/libraries', {
        method: 'POST',
        body: JSON.stringify({
          name: libName.trim(),
          mediaType: libType,
          rootFolderPath: libPath.trim(),
          plexSectionId: libPlexSection.trim() ? Number(libPlexSection.trim()) : null,
          defaultProfileId: addLibProfileId,
          metadataWaterfall: waterfall.length > 0 ? waterfall : undefined,
        }),
      });
    },
    onSuccess: () => {
      setAddLibProfileId(null);
      setAddLibWaterfall('');
      setAddLibError(null);
      onSetShowAddLibModal(false);
      qc.invalidateQueries({ queryKey: ['libraries'] });
    },
    onError: (err: Error) => {
      setAddLibError(err.message);
    },
  });

  return (
    <>
      {showAddLibModal && (
        <ArrModal
          title="Add Root Folder / PMS Library"
          onClose={() => onSetShowAddLibModal(false)}
          maxWidth="max-w-xl"
          footer={
            <>
              <ArrButton onClick={() => onSetShowAddLibModal(false)}>Cancel</ArrButton>
              <ArrButton
                variant="primary"
                disabled={!libName.trim()}
                busy={isCreateLibPending || createCustomLibMutation.isPending}
                onClick={() => {
                  if (addLibProfileId !== null || addLibWaterfall.trim().length > 0) {
                    createCustomLibMutation.mutate();
                  } else {
                    onCreateLib();
                  }
                }}
              >
                Save
              </ArrButton>
            </>
          }
        >
          {addLibError && (
            <ArrAlert kind="danger" className="mb-4">
              {addLibError}
            </ArrAlert>
          )}
          <FormGroup label="Name">
            <input
              value={libName}
              onChange={(e) => onSetLibName(e.target.value)}
              placeholder="Movies (4K & 1080p)"
              className="arr-input"
            />
          </FormGroup>
          <FormGroup label="Media Type">
            <select value={libType} onChange={(e) => onSetLibType(e.target.value)} className="arr-input">
              <option value="movies">Movies (TMDB / SkyHook + alternatives)</option>
              <option value="tv">TV Series (TVDB / SkyHook + TMDB/TVMaze)</option>
              <option value="anime">Anime (TVDB + AniList/AniDB)</option>
              <option value="music">Music (MusicBrainz + Deezer/Discogs)</option>
              <option value="ebooks">Books Dual-Slot (OpenLibrary + GoogleBooks/Audnexus)</option>
              <option value="audiobooks">Audiobooks (OpenLibrary + Audnexus/Apple Books)</option>
              <option value="comics">Comics &amp; Manga (ComicVine + MangaDex)</option>
            </select>
          </FormGroup>
          <FormGroup label="Root Folder Path">
            <PathBrowserInput
              value={libPath}
              onChange={onSetLibPath}
              placeholder="/media/movies"
              label="Root Folder Path"
              modalTitle="Select Root Folder"
              allowCreateFolder
            />
          </FormGroup>
          <FormGroup label="Default Quality Profile" helpText="Optional default quality profile for newly added works.">
            <select
              value={addLibProfileId ?? ''}
              onChange={(e) => setAddLibProfileId(e.target.value ? Number(e.target.value) : null)}
              className="arr-input"
            >
              <option value="">Vertical Default</option>
              {(modalProfilesQuery.data ?? []).map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name} ({p.mediaVertical})
                </option>
              ))}
            </select>
          </FormGroup>
          <FormGroup
            label="Metadata Waterfall"
            helpText="Optional comma-separated provider order (e.g. tmdb, skyhook, anilist)."
          >
            <input
              value={addLibWaterfall}
              onChange={(e) => setAddLibWaterfall(e.target.value)}
              placeholder="Leave blank for vertical default"
              className="arr-input font-mono text-[12px]"
            />
          </FormGroup>
          <FormGroup label="Plex Section ID" helpText="PMS section number used for inherited library RBAC.">
            <input
              value={libPlexSection}
              onChange={(e) => onSetLibPlexSection(e.target.value)}
              placeholder="1"
              className="arr-input font-mono"
            />
          </FormGroup>
        </ArrModal>
      )}

      {showRequestModal && (
        <ArrModal
          title="Submit Media Request"
          onClose={() => onSetShowRequestModal(false)}
          maxWidth="max-w-lg"
          footer={
            <>
              <ArrButton onClick={() => onSetShowRequestModal(false)}>Cancel</ArrButton>
              <ArrButton
                variant="primary"
                disabled={!reqTitle.trim()}
                busy={isSubmitRequestPending}
                onClick={onSubmitRequest}
              >
                Submit Request
              </ArrButton>
            </>
          }
        >
          <FormGroup label="Library">
            <select
              value={activeLibId ?? ''}
              onChange={(e) => onSetFormLibraryId(Number(e.target.value))}
              className="arr-input"
            >
              {libraries.map((lib) => (
                <option key={lib.id} value={lib.id}>
                  {lib.name} ({lib.mediaType})
                </option>
              ))}
            </select>
          </FormGroup>
          <FormGroup label="Title">
            <input
              value={reqTitle}
              onChange={(e) => onSetReqTitle(e.target.value)}
              placeholder="Title to request"
              className="arr-input"
            />
          </FormGroup>
          <FormGroup label="Audio Language" helpText="Optional ISO override (e.g. de, en, ja).">
            <input
              value={reqAudioLang}
              onChange={(e) => onSetReqAudioLang(e.target.value)}
              placeholder="de"
              className="arr-input"
            />
          </FormGroup>
        </ArrModal>
      )}

      {renameModalConfig && (
        <BulkFileRenameModal
          isOpen={renameModalConfig.isOpen}
          title={renameModalConfig.title}
          previewUrl={renameModalConfig.previewUrl}
          executeUrl={renameModalConfig.executeUrl}
          onClose={onCloseRenameModal}
        />
      )}

      {showDeleteConfirmModal && (
        <ArrModal
          title={`Delete ${selectedWorkCount} Selected Works`}
          onClose={() => onSetShowDeleteConfirmModal(false)}
          maxWidth="max-w-md"
          footer={
            <div className="flex items-center justify-end gap-2 w-full">
              <ArrButton onClick={() => onSetShowDeleteConfirmModal(false)}>Cancel</ArrButton>
              <ArrButton
                variant="danger"
                busy={isDeleteSelectedPending}
                disabled={isDeleteSelectedPending}
                onClick={onDeleteSelectedWorks}
              >
                <Trash2 className="h-4 w-4" /> Confirm Delete
              </ArrButton>
            </div>
          }
        >
          <div className="space-y-4">
            <p className="text-[13px] text-[#dddddd]">
              Are you sure you want to delete <strong>{selectedWorkCount}</strong> selected media work
              {selectedWorkCount === 1 ? '' : 's'} from the catalog?
            </p>
            <div className="p-3 bg-[#1e1e1e] border border-arr-card-border rounded-[4px]">
              <label className="inline-flex items-center gap-2 cursor-pointer select-none">
                <input
                  type="checkbox"
                  checked={deleteFilesOnDisk}
                  onChange={(e) => onSetDeleteFilesOnDisk(e.target.checked)}
                  className="rounded border-[#4a5158] bg-[#141414] text-[#f05050] focus:ring-0 cursor-pointer"
                />
                <span className="text-[13px] text-[#f05050] font-medium">Delete Files on Disk</span>
              </label>
                <p className="text-[11px] text-[#888888] mt-1">
                  If checked, media files associated with these works will be permanently removed from disk.
                </p>
              </div>
            </div>
          </ArrModal>
        )}
    </>
  );
}

export interface CatalogToolbarSectionProps {
  section: CatalogSubSection;
  selectedWorkId: number | null;
  onReturnToLibrary: () => void;
  treeData: MediaWorkDetailTree | undefined;
  canWriteCatalog: boolean;
  isRefreshMetadataPending: boolean;
  onRefreshMetadata: (workId: number) => void;
  isWorkScanPending: boolean;
  onWorkScan: (workId: number) => void;
  isWorkScanAndVerifyPending?: boolean;
  onWorkScanAndVerify?: (workId: number) => void;
  onOpenRenamePreview: (cfg: {
    isOpen: boolean;
    title: string;
    previewUrl: string;
    executeUrl: string;
  }) => void;
  canSearchIndexers: boolean;
  isAutoSearchWorkPending: boolean;
  onAutoSearchWork: (workId: number) => void;
  onSetInteractiveTarget: (target: InteractiveSearchTarget | null) => void;
  onNavigate?: (target: CatalogSubSection) => void;
  onSetShowAddLibModal: (v: boolean) => void;
  manualMode: boolean;
  onToggleManualMode: () => void;
  isAnyCatalogQueryFetching: boolean;
  onRefreshActiveSection: () => void;
  isAutoClassifyPending: boolean;
  onAutoClassify: () => void;
  massEditMode: boolean;
  onToggleMassEditMode: () => void;
  canRequest: boolean;
  onSetShowRequestModal: (v: boolean) => void;
  onCalendarPrev: () => void;
  onCalendarToday: () => void;
  onCalendarNext: () => void;
  calRangeLabel: string;
  onSetShowIcalModal: (v: boolean) => void;
  catalogFilterText: string;
  onSetCatalogFilterText: (v: string) => void;
  filteredWorks: MediaWorkSummary[];
  onSearchInAddNew?: (query: string) => void;
  onOpenWork: (workId: number) => void;
  catalogSortField: CatalogSortField;
  catalogSortAsc?: boolean;
  sortStatusAnnouncement?: string;
  onSelectCatalogSort?: (field: CatalogSortField) => void;
  onSetCatalogSortField: (v: CatalogSortField) => void;
  onToggleCatalogSortAsc: () => void;
  onSetCatalogSortAsc: (v: boolean) => void;
  viewMode: CatalogViewMode;
  onUpdateViewMode: (next: CatalogViewMode) => void;
  addVertical: string;
  onSetAddVertical: (v: string) => void;
  calendarViewMode: 'month' | 'week' | 'agenda';
  onSetCalendarViewMode: (v: 'month' | 'week' | 'agenda') => void;
  calSelectedVerticals: Set<string>;
  onSetCalSelectedVerticals: React.Dispatch<React.SetStateAction<Set<string>>>;
  calMonitoredOnly: boolean;
  onSetCalMonitoredOnly: React.Dispatch<React.SetStateAction<boolean>>;
}

export function CatalogToolbarSection({
  section,
  selectedWorkId,
  onReturnToLibrary,
  treeData,
  canWriteCatalog,
  isRefreshMetadataPending,
  onRefreshMetadata,
  isWorkScanPending,
  onWorkScan,
  isWorkScanAndVerifyPending,
  onWorkScanAndVerify,
  onOpenRenamePreview,
  canSearchIndexers,
  isAutoSearchWorkPending,
  onAutoSearchWork,
  onSetInteractiveTarget,
  onNavigate,
  onSetShowAddLibModal,
  manualMode,
  onToggleManualMode,
  isAnyCatalogQueryFetching,
  onRefreshActiveSection,
  isAutoClassifyPending,
  onAutoClassify,
  massEditMode,
  onToggleMassEditMode,
  canRequest,
  onSetShowRequestModal,
  onCalendarPrev,
  onCalendarToday,
  onCalendarNext,
  calRangeLabel,
  onSetShowIcalModal,
  catalogFilterText,
  onSetCatalogFilterText,
  filteredWorks,
  onSearchInAddNew,
  onOpenWork,
  catalogSortField,
  catalogSortAsc,
  sortStatusAnnouncement,
  onSelectCatalogSort,
  onSetCatalogSortField,
  onToggleCatalogSortAsc,
  onSetCatalogSortAsc,
  viewMode,
  onUpdateViewMode,
  addVertical,
  onSetAddVertical,
  calendarViewMode,
  onSetCalendarViewMode,
  calSelectedVerticals,
  onSetCalSelectedVerticals,
  calMonitoredOnly,
  onSetCalMonitoredOnly,
}: CatalogToolbarSectionProps) {
  return (
    <PageToolbar
      left={
        <>
          {section === 'catalog' && selectedWorkId !== null ? (
            <>
              <PageToolbarButton
                icon={ArrowLeft}
                label="Library"
                title="Return to Library Poster Wall"
                onClick={onReturnToLibrary}
              />
              {treeData && (
                <>
                  <PageToolbarSeparator />
                  {canWriteCatalog && (
                    <>
                      <PageToolbarButton
                        icon={RefreshCw}
                        label="Refresh Metadata"
                        spinning={isRefreshMetadataPending}
                        onClick={() => onRefreshMetadata(treeData.work.id)}
                      />
                      <PageToolbarButton
                        icon={RefreshCw}
                        label="Refresh & Scan"
                        spinning={isWorkScanPending}
                        onClick={() => onWorkScan(treeData.work.id)}
                      />
                      {onWorkScanAndVerify && (
                        <PageToolbarButton
                          icon={Languages}
                          label="Scan & Check Languages"
                          spinning={isWorkScanAndVerifyPending}
                          onClick={() => onWorkScanAndVerify(treeData.work.id)}
                        />
                      )}
                      <PageToolbarButton
                        icon={FileCheck}
                        label="Preview Rename"
                        onClick={() =>
                          onOpenRenamePreview({
                            isOpen: true,
                            title: `Rename Preview — ${treeData.work.title}`,
                            previewUrl: `/api/v1/catalog/works/${treeData.work.id}/rename-preview`,
                            executeUrl: `/api/v1/catalog/works/${treeData.work.id}/rename-execute`,
                          })
                        }
                      />
                    </>
                  )}
                  {canSearchIndexers && (
                    <>
                      <PageToolbarButton
                        icon={Radar}
                        label="Search Monitored"
                        spinning={isAutoSearchWorkPending}
                        onClick={() => onAutoSearchWork(treeData.work.id)}
                      />
                      <PageToolbarButton
                        icon={User}
                        label="Interactive Search"
                        onClick={() =>
                          onSetInteractiveTarget({
                            label: treeData.work.title,
                            season: null,
                            episode: null,
                          })
                        }
                      />
                    </>
                  )}
                </>
              )}
            </>
          ) : section === 'add' ? (
            <>
              <PageToolbarButton icon={ArrowLeft} label="Library" onClick={() => onNavigate?.('catalog')} />
              {canWriteCatalog && (
                <>
                  <PageToolbarSeparator />
                  <PageToolbarButton
                    icon={FolderPlus}
                    label="Add Root Folder"
                    onClick={() => onSetShowAddLibModal(true)}
                  />
                  <PageToolbarButton
                    icon={FilePlus2}
                    label={manualMode ? 'Live Lookup' : 'Manual Entry'}
                    active={manualMode}
                    onClick={onToggleManualMode}
                  />
                </>
              )}
            </>
          ) : (
            <>
              <PageToolbarButton
                icon={RefreshCw}
                label="Refresh & Scan"
                spinning={isAnyCatalogQueryFetching}
                onClick={onRefreshActiveSection}
              />

              {section === 'catalog' && canWriteCatalog && (
                <>
                  <PageToolbarSeparator />
                  <PageToolbarButton icon={Plus} label="Add New" onClick={() => onNavigate?.('add')} />
                  <PageToolbarButton
                    icon={FolderPlus}
                    label="Add Root Folder"
                    onClick={() => onSetShowAddLibModal(true)}
                  />
                  <PageToolbarButton
                    icon={Tag}
                    label="Auto-Tag Library"
                    title="Automatically classify library works into Anime, Cartoon, Live-Action and assign canonical tags"
                    spinning={isAutoClassifyPending}
                    onClick={onAutoClassify}
                  />
                  <PageToolbarButton
                    icon={SlidersHorizontal}
                    label="Mass Editor"
                    active={massEditMode}
                    onClick={onToggleMassEditMode}
                  />
                </>
              )}

              {section === 'media-management' && canWriteCatalog && (
                <>
                  <PageToolbarSeparator />
                  <PageToolbarButton
                    icon={FolderPlus}
                    label="Add Root Folder"
                    onClick={() => onSetShowAddLibModal(true)}
                  />
                </>
              )}

              {section === 'requests' && canRequest && (
                <>
                  <PageToolbarSeparator />
                  <PageToolbarButton
                    icon={Send}
                    label="New Request"
                    onClick={() => onSetShowRequestModal(true)}
                  />
                </>
              )}

              {section === 'calendar' && (
                <>
                  <PageToolbarSeparator />
                  <PageToolbarButton
                    icon={ChevronLeft}
                    label="Prev"
                    title="Previous date range"
                    onClick={onCalendarPrev}
                  />
                  <PageToolbarButton
                    icon={CalendarIcon}
                    label="Today"
                    title="Jump to today"
                    onClick={onCalendarToday}
                  />
                  <PageToolbarButton
                    icon={ChevronRight}
                    label="Next"
                    title="Next date range"
                    onClick={onCalendarNext}
                  />
                  <div className="hidden sm:flex items-center px-2 text-[13px] font-semibold text-[#e1e2e3]">
                    {calRangeLabel}
                  </div>
                  <PageToolbarSeparator />
                  <PageToolbarButton
                    icon={CalendarRange}
                    label="iCal Feed"
                    title="Subscribe to iCal (.ics) Calendar Feed"
                    onClick={() => onSetShowIcalModal(true)}
                  />
                </>
              )}
            </>
          )}
        </>
      }
      right={
        section === 'catalog' && selectedWorkId === null ? (
          <div className="flex flex-wrap items-center gap-2">
            <div className="relative flex items-center">
              <Search className="h-3.5 w-3.5 text-[#888f98] absolute left-2.5 pointer-events-none" />
              <input
                type="text"
                value={catalogFilterText}
                onChange={(e) => onSetCatalogFilterText(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    const q = catalogFilterText.trim();
                    if (q.length > 0) {
                      e.preventDefault();
                      if (filteredWorks.length === 0 && canWriteCatalog) {
                        onSearchInAddNew?.(q);
                      } else if (filteredWorks.length === 1) {
                        onOpenWork(filteredWorks[0].id);
                      }
                    }
                  }
                }}
                placeholder="Filter library..."
                className="bg-[#1e2124] border border-[#3a4046] focus:border-arr-accent rounded-full pl-8 pr-7 h-[30px] text-[12px] text-[#eeeeee] placeholder-[#7f868f] outline-none w-36 sm:w-44 transition-colors"
              />
              {catalogFilterText && (
                <button
                  type="button"
                  onClick={() => onSetCatalogFilterText('')}
                  className="absolute right-2 text-[#888f98] hover:text-white cursor-pointer"
                  aria-label="Clear filter text"
                >
                  <X className="h-3.5 w-3.5" />
                </button>
              )}
            </div>

            <div className="inline-flex items-center rounded-full border border-[#3a4046] bg-[#1e2124] p-0.5 text-[11px]">
              {(
                [
                  { id: 'title', label: 'Title' },
                  { id: 'year', label: 'Year' },
                  { id: 'missing', label: 'Missing' },
                  { id: 'added', label: 'Date Added' },
                ] as const
              ).map((s) => {
                const active = catalogSortField === s.id;
                const directionLabel =
                  catalogSortAsc === true ? 'ascending' : catalogSortAsc === false ? 'descending' : 'active';
                return (
                  <button
                    key={s.id}
                    type="button"
                    aria-pressed={active}
                    title={
                      active
                        ? `Sorted by ${s.label} (${directionLabel}) — click to reverse`
                        : `Sort by ${s.label}`
                    }
                    onClick={() => {
                      if (onSelectCatalogSort) {
                        onSelectCatalogSort(s.id);
                      } else if (catalogSortField === s.id) {
                        onToggleCatalogSortAsc();
                      } else {
                        onSetCatalogSortField(s.id);
                        onSetCatalogSortAsc(s.id === 'title');
                      }
                    }}
                    className={clsx(
                      'inline-flex items-center gap-1 px-2.5 h-[22px] rounded-full transition-colors cursor-pointer',
                      active
                        ? 'bg-arr-accent text-arr-accent-fg font-semibold'
                        : 'text-[#9aa0a6] hover:text-[#eeeeee]',
                    )}
                  >
                    <span>{s.label}</span>
                    {active &&
                      (catalogSortAsc === true ? (
                        <ArrowUp className="h-3 w-3" aria-hidden="true" />
                      ) : catalogSortAsc === false ? (
                        <ArrowDown className="h-3 w-3" aria-hidden="true" />
                      ) : (
                        <ArrowUpDown className="h-3 w-3" aria-hidden="true" />
                      ))}
                  </button>
                );
              })}
            </div>
            <span role="status" className="sr-only">
              {sortStatusAnnouncement ?? ''}
            </span>

            <PageToolbarSeparator />
            <PageToolbarButton
              icon={LayoutGrid}
              label="Posters"
              active={viewMode === 'posters'}
              onClick={() => onUpdateViewMode('posters')}
            />
            <PageToolbarButton
              icon={FileText}
              label="Overview"
              active={viewMode === 'overview'}
              onClick={() => onUpdateViewMode('overview')}
            />
            <PageToolbarButton
              icon={List}
              label="Table"
              active={viewMode === 'table'}
              onClick={() => onUpdateViewMode('table')}
            />
          </div>
        ) : section === 'add' && !manualMode ? (
          <div className="flex flex-wrap items-center gap-1.5">
            {ADD_VERTICAL_TABS.map((tab) => {
              const active = addVertical === tab.id;
              return (
                <FilterBubble
                  key={tab.id}
                  label={tab.label}
                  active={active}
                  onClick={() => onSetAddVertical(tab.id)}
                  tone="cyan"
                />
              );
            })}
          </div>
        ) : section === 'calendar' ? (
          <div className="flex flex-wrap items-center gap-2">
            <div className="inline-flex rounded-full border border-[#3a4046] bg-[#1e2124] p-0.5">
              {(['month', 'week', 'agenda'] as const).map((mode) => (
                <button
                  key={mode}
                  type="button"
                  onClick={() => onSetCalendarViewMode(mode)}
                  className={clsx(
                    'px-2.5 py-1 text-[11px] font-medium rounded-full transition-colors uppercase tracking-wider cursor-pointer',
                    calendarViewMode === mode
                      ? 'bg-arr-accent text-arr-accent-fg font-semibold'
                      : 'text-[#888888] hover:text-[#cccccc]',
                  )}
                >
                  {mode}
                </button>
              ))}
            </div>

            <div className="flex flex-wrap items-center gap-1.5">
              {[
                { id: 'movies', label: 'Movies' },
                { id: 'tv', label: 'TV' },
                { id: 'anime', label: 'Anime' },
                { id: 'music', label: 'Music' },
                { id: 'ebooks', label: 'Books' },
              ].map((b) => (
                <FilterBubble
                  key={b.id}
                  label={b.label}
                  active={calSelectedVerticals.has(b.id)}
                  onClick={() => onSetCalSelectedVerticals((prev) => toggleFilterSetValue(prev, b.id))}
                  tone="cyan"
                />
              ))}
              <FilterBubble
                label="Monitored"
                active={calMonitoredOnly}
                onClick={() => onSetCalMonitoredOnly((v) => !v)}
                tone="green"
              />
              {(calSelectedVerticals.size > 0 || calMonitoredOnly) && (
                <button
                  type="button"
                  onClick={() => {
                    onSetCalSelectedVerticals(new Set());
                    onSetCalMonitoredOnly(false);
                  }}
                  className="inline-flex items-center gap-1 h-[28px] px-2.5 rounded-full border border-[#4a5158] bg-[#2a2e33] text-[#d0d4d9] hover:text-white hover:border-[#f05050] text-[11px] cursor-pointer"
                >
                  <X className="h-3 w-3" /> Reset
                </button>
              )}
            </div>
          </div>
        ) : undefined
      }
    />
  );
}
