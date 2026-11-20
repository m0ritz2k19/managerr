import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Download,
  Edit2,
  FileSearch,
  FlaskConical,
  FolderSync,
  Plus,
  RefreshCw,
  Search,
  ShieldAlert,
  ShieldCheck,
  Trash2,
  X,
} from 'lucide-react';
import {
  apiFetch,
  formatBytes,
  type ManualOverrideImportRequest,
  type MultiCategoryRssSyncReport,
  type ProcessedDownloadItemOutcome,
  type TrackedDownloadDto,
} from '../lib/api';
import {
  ArrAddCard,
  ArrAlert,
  ArrButton,
  ArrLabel,
  ArrModal,
  ArrSettingsCard,
  FieldSet,
  FormGroup,
  PageContentBody,
  PageToolbar,
  PageToolbarButton,
  PageToolbarSeparator,
} from './arr/ArrPrimitives';
import {
  DEFAULT_CATEGORIES,
  STANDARD_CATEGORIES,
  getCategoryLabel,
  type ActiveQueueItemDto,
  type DownloadClientDto,
  type DriverDto,
  type IndexerFormState,
  type IndexerSummaryDto,
  type ProcessCompletedReport,
  type ProcessedImportRow,
  type RemotePathMappingDto,
  type SearchResultRow,
} from './acquisition/acquisitionTypes';
import { DownloadersSection } from './acquisition/DownloadersSection';

export type AcquisitionSubSection = 'indexers' | 'downloaders' | 'parser';

interface CardigannSettingField {
  name: string;
  type?: string;
  label?: string;
  default?: string | boolean | number;
  options?: Record<string, string> | Array<{ value: string; label: string }>;
}

interface CardigannDefinitionDto {
  id: string;
  name?: string;
  displayName?: string;
  description?: string;
  type?: string;
  protocol?: string;
  links?: string[];
  settings?: CardigannSettingField[];
  categories?: number[];
}

interface IndexerProxyDto {
  id: number;
  name: string;
  proxyType: string;
  host?: string;
  port?: number;
  url?: string;
  baseUrl?: string;
  username?: string | null;
  requestTimeoutSec?: number;
  tags?: string[];
  isEnabled?: boolean;
}

type ExtendedIndexerDto = IndexerSummaryDto & {
  extraSettings?: Record<string, string>;
  requestDelayMs?: number;
  tags?: string[];
};

type ExtendedDownloadClientDto = DownloadClientDto & {
  isEnabled?: boolean;
  urlBase?: string | null;
  priority?: number;
  removeCompleted?: boolean;
  removeFailed?: boolean;
  extraSettings?: Record<string, string>;
};

interface AcquisitionViewProps {
  section?: AcquisitionSubSection;
  canReadIndexers: boolean;
  canWriteIndexers: boolean;
  canReadDownloaders: boolean;
  canWriteDownloaders: boolean;
}

export function AcquisitionView({
  section = 'indexers',
  canReadIndexers,
  canWriteIndexers,
  canReadDownloaders,
  canWriteDownloaders,
}: AcquisitionViewProps) {
  const qc = useQueryClient();

  // Indexer picker + Add/Edit modal state
  const [showIndexerCatalogModal, setShowIndexerCatalogModal] = useState(false);
  const [indexerForm, setIndexerForm] = useState<IndexerFormState | null>(null);
  const [indexerExtraSettings, setIndexerExtraSettings] = useState<Record<string, string>>({});
  const [indexerRequestDelayMs, setIndexerRequestDelayMs] = useState<string>('0');
  const [indexerTags, setIndexerTags] = useState<string>('');
  const [indexerTestStatus, setIndexerTestStatus] = useState<{ ok: boolean; message: string } | null>(null);
  const [customCategoryInput, setCustomCategoryInput] = useState('');
  const [extraCategoryIds, setExtraCategoryIds] = useState<number[]>([]);

  // Indexer Proxies state
  const [proxyModalOpen, setProxyModalOpen] = useState(false);
  const [editingProxyId, setEditingProxyId] = useState<number | null>(null);
  const [proxyName, setProxyName] = useState('');
  const [proxyType, setProxyType] = useState<'flaresolverr' | 'byparr' | 'http' | 'socks4' | 'socks5'>('flaresolverr');
  const [proxyHostOrUrl, setProxyHostOrUrl] = useState('');
  const [proxyPort, setProxyPort] = useState('8191');
  const [proxyUsername, setProxyUsername] = useState('');
  const [proxyPassword, setProxyPassword] = useState('');
  const [proxyTimeoutSec, setProxyTimeoutSec] = useState('60');
  const [proxyTags, setProxyTags] = useState('');
  const [proxyEnabled, setProxyEnabled] = useState(true);
  const [proxyError, setProxyError] = useState<string | null>(null);

  // Interactive search state
  const [searchQuery, setSearchQuery] = useState('');
  const [searchVertical, setSearchVertical] = useState('movies');
  const [grabBanner, setGrabBanner] = useState<string | null>(null);

  // Download client Add/Edit modal state
  const [showAddClientPicker, setShowAddClientPicker] = useState(false);
  const [editingClientId, setEditingClientId] = useState<number | null>(null);
  const [selectedDriver, setSelectedDriver] = useState<DriverDto | null>(null);
  const [clientHasSavedKey, setClientHasSavedKey] = useState(false);
  const [clientHasSavedPass, setClientHasSavedPass] = useState(false);
  const [clientName, setClientName] = useState('');
  const [clientHost, setClientHost] = useState('localhost');
  const [clientPort, setClientPort] = useState('8080');
  const [clientApiKey, setClientApiKey] = useState('');
  const [clientUsername, setClientUsername] = useState('');
  const [clientPassword, setClientPassword] = useState('');
  const [clientUseSsl, setClientUseSsl] = useState(false);
  const [clientIsEnabled, setClientIsEnabled] = useState(true);
  const [clientUrlBase, setClientUrlBase] = useState('');
  const [clientPriority, setClientPriority] = useState('1');
  const [clientRemoveCompleted, setClientRemoveCompleted] = useState(true);
  const [clientRemoveFailed, setClientRemoveFailed] = useState(true);
  const [clientCategoryMappings, setClientCategoryMappings] = useState<Record<string, string>>({});
  const [clientWatchFolder, setClientWatchFolder] = useState('');
  const [clientTestStatus, setClientTestStatus] = useState<{ ok: boolean; message: string } | null>(null);
  const [downloaderBanner, setDownloaderBanner] = useState<string | null>(null);

  // Tracked downloads & Manual Override Import state
  const [selectedTrackedForImport, setSelectedTrackedForImport] = useState<TrackedDownloadDto | null>(null);
  const [overrideWorkId, setOverrideWorkId] = useState<number | null>(null);
  const [overrideSeason, setOverrideSeason] = useState<string>('');
  const [overrideEpisodes, setOverrideEpisodes] = useState<string>('');
  const [bypassUpgrade, setBypassUpgrade] = useState<boolean>(false);
  const [confirmedAudioLangs, setConfirmedAudioLangs] = useState<string>('');
  const [manualImportError, setManualImportError] = useState<string | null>(null);
  const [ignoringTrackedId, setIgnoringTrackedId] = useState<number | null>(null);

  // RSS Sync state
  const [rssSyncResult, setRssSyncResult] = useState<MultiCategoryRssSyncReport | null>(null);

  const [parseTitle, setParseTitle] = useState(
    'Dune.Part.Two.2024.IMAX.GERMAN.DL.2160p.UHD.BluRay.REMUX.DV.HDR10.TrueHD.Atmos.HEVC-GROUP',
  );

  const indexersQuery = useQuery({
    queryKey: ['indexers'],
    queryFn: () => apiFetch<IndexerSummaryDto[]>('/api/v1/indexers'),
    enabled: canReadIndexers && section === 'indexers',
  });

  const definitionsQuery = useQuery({
    queryKey: ['indexer-definitions'],
    queryFn: async () => {
      try {
        const res = await apiFetch<CardigannDefinitionDto[] | { items?: CardigannDefinitionDto[] }>(
          '/api/v1/indexers/definitions',
        );
        if (Array.isArray(res)) return res;
        if (res && Array.isArray(res.items)) return res.items;
        return [] as CardigannDefinitionDto[];
      } catch {
        return [] as CardigannDefinitionDto[];
      }
    },
    enabled: canReadIndexers && section === 'indexers',
  });

  const proxiesQuery = useQuery({
    queryKey: ['indexer-proxies'],
    queryFn: async () => {
      try {
        const res = await apiFetch<IndexerProxyDto[]>('/api/v1/indexers/proxies');
        return Array.isArray(res) ? res : ([] as IndexerProxyDto[]);
      } catch {
        return [] as IndexerProxyDto[];
      }
    },
    enabled: canReadIndexers && section === 'indexers',
  });

  const driversQuery = useQuery({
    queryKey: ['downloader-drivers'],
    queryFn: () =>
      apiFetch<{ drivers: DriverDto[]; defaultCategoryMappings: Record<string, string> }>(
        '/api/v1/downloaders/drivers',
      ),
    enabled: canReadDownloaders && (section === 'downloaders' || showAddClientPicker || editingClientId !== null),
  });

  const clientsQuery = useQuery({
    queryKey: ['downloaders'],
    queryFn: () => apiFetch<DownloadClientDto[]>('/api/v1/downloaders'),
    enabled: canReadDownloaders && section === 'downloaders',
  });

  const queueQuery = useQuery({
    queryKey: ['downloaders-queue'],
    queryFn: () => apiFetch<ActiveQueueItemDto[]>('/api/v1/downloaders/queue'),
    enabled: canReadDownloaders && section === 'downloaders',
  });

  const importsQuery = useQuery({
    queryKey: ['downloaders-imports'],
    queryFn: () => apiFetch<ProcessedImportRow[]>('/api/v1/downloaders/imports?limit=30'),
    enabled: canReadDownloaders && section === 'downloaders',
  });

  const trackedQuery = useQuery({
    queryKey: ['downloaders-tracked'],
    queryFn: async () => {
      try {
        return await apiFetch<TrackedDownloadDto[]>('/api/v1/downloaders/tracked');
      } catch {
        return [] as TrackedDownloadDto[];
      }
    },
    enabled: canReadDownloaders && section === 'downloaders',
  });

  const remotePathsQuery = useQuery({
    queryKey: ['downloaders-remote-paths'],
    queryFn: () => apiFetch<RemotePathMappingDto[]>('/api/v1/downloaders/remote-paths'),
    enabled: canReadDownloaders && section === 'downloaders',
  });

  const catalogWorksQuery = useQuery({
    queryKey: ['catalog-works-lookup'],
    queryFn: async () => {
      try {
        return await apiFetch<Array<{ id: number; title: string; year: number | null; workKind: string }>>(
          '/api/v1/catalog/works',
        );
      } catch {
        return [];
      }
    },
    enabled: selectedTrackedForImport !== null,
  });

  const parsePreview = useQuery({
    queryKey: ['parse-preview', parseTitle],
    queryFn: () =>
      apiFetch<{
        parsed: {
          mediaVertical: string;
          cleanTitle: string;
          year: number | null;
          movieEdition: string | null;
          resolution: string | null;
          source: string | null;
          hdrFormat: string | null;
          audioCodec: string | null;
          detectedLanguages: string[];
          isDualAudio: boolean;
        };
        previewRelativePath: string;
      }>('/api/v1/parser/parse', {
        method: 'POST',
        body: JSON.stringify({ title: parseTitle, extension: '.mkv' }),
      }),
    enabled: section === 'parser' && parseTitle.trim().length > 3,
  });

  function applyCardigannDefinitionDefaults(defId: string, overrideEmptyOnly = false) {
    const def = (definitionsQuery.data ?? []).find((d) => d.id === defId);
    if (!def) return;
    const defaults: Record<string, string> = {};
    for (const field of def.settings ?? []) {
      if (field.default !== undefined && field.default !== null) {
        defaults[field.name] = String(field.default);
      }
    }
    setIndexerExtraSettings((prev) => ({ ...defaults, ...(overrideEmptyOnly ? prev : {}) }));
    setIndexerForm((prev) => {
      if (!prev) return prev;
      return {
        ...prev,
        definitionId: def.id,
        name: prev.name.trim() ? prev.name : (def.displayName || def.name || def.id),
        baseUrl: prev.baseUrl.trim() ? prev.baseUrl : (def.links?.[0] ?? ''),
        categories:
          def.categories && def.categories.length > 0 && !overrideEmptyOnly
            ? [...def.categories]
            : prev.categories,
      };
    });
  }

  function openNewNabIndexer(impl: 'newznab' | 'torznab' | 'cardigann') {
    setShowIndexerCatalogModal(false);
    setIndexerTestStatus(null);
    setCustomCategoryInput('');
    setExtraCategoryIds([]);
    const firstDef = impl === 'cardigann' ? (definitionsQuery.data ?? [])[0] : undefined;
    const defaultExtra: Record<string, string> = {};
    if (firstDef?.settings) {
      for (const f of firstDef.settings) {
        if (f.default !== undefined && f.default !== null) {
          defaultExtra[f.name] = String(f.default);
        }
      }
    }
    setIndexerExtraSettings(defaultExtra);
    setIndexerRequestDelayMs('0');
    setIndexerTags('');
    setIndexerForm({
      editingId: null,
      hasSavedApiKey: false,
      name: firstDef ? (firstDef.displayName || firstDef.name || firstDef.id) : '',
      implementation: impl,
      definitionId: firstDef?.id ?? null,
      protocol: impl === 'newznab' ? 'usenet' : 'torrent',
      baseUrl: firstDef?.links?.[0] ?? '',
      apiKey: '',
      categories:
        firstDef?.categories && firstDef.categories.length > 0
          ? [...firstDef.categories]
          : [...DEFAULT_CATEGORIES],
      priority: 25,
      rssEnabled: true,
      autoSearchEnabled: true,
      interactiveSearchEnabled: true,
    });
  }

  function openEditIndexer(ixRaw: IndexerSummaryDto) {
    if (!canWriteIndexers) return;
    const ix = ixRaw as ExtendedIndexerDto;
    setIndexerTestStatus(null);
    setCustomCategoryInput('');
    const nonStandard = (ix.categories ?? []).filter(
      (c) => !STANDARD_CATEGORIES.some((sc) => sc.id === c),
    );
    setExtraCategoryIds(nonStandard);
    const implLower = ix.implementation.toLowerCase();
    const impl: 'newznab' | 'torznab' | 'cardigann' =
      implLower === 'cardigann' ? 'cardigann' : implLower === 'torznab' ? 'torznab' : 'newznab';
    setIndexerExtraSettings(ix.extraSettings ? { ...ix.extraSettings } : {});
    setIndexerRequestDelayMs(String(ix.requestDelayMs ?? 0));
    setIndexerTags((ix.tags ?? []).join(', '));
    setIndexerForm({
      editingId: ix.id,
      hasSavedApiKey: Boolean(ix.apiKeySecret?.isSet),
      name: ix.name,
      implementation: impl,
      definitionId: ix.definitionId ?? null,
      protocol: ix.protocol.toLowerCase() === 'usenet' ? 'usenet' : 'torrent',
      baseUrl: ix.baseUrl,
      apiKey: '',
      categories: ix.categories?.length > 0 ? [...ix.categories] : [...DEFAULT_CATEGORIES],
      priority: ix.priority || 25,
      rssEnabled: ix.rssEnabled ?? true,
      autoSearchEnabled: ix.autoSearchEnabled ?? true,
      interactiveSearchEnabled: ix.interactiveSearchEnabled ?? true,
    });
  }

  function buildIndexerRequestPayload(form: IndexerFormState) {
    const parsedDelay = Math.max(0, Number(indexerRequestDelayMs) || 0);
    const parsedTags = indexerTags
      .split(',')
      .map((t) => t.trim())
      .filter(Boolean);
    return {
      name: form.name.trim(),
      implementation: form.implementation,
      definitionId: form.implementation === 'cardigann' ? form.definitionId : null,
      protocol: form.implementation === 'newznab' ? 'usenet' : form.protocol,
      baseUrl: form.baseUrl.trim(),
      apiKey: form.apiKey.trim() || undefined,
      categories: form.categories.length > 0 ? form.categories : DEFAULT_CATEGORIES,
      priority: Number(form.priority) || 25,
      rssEnabled: form.rssEnabled,
      autoSearchEnabled: form.autoSearchEnabled,
      interactiveSearchEnabled: form.interactiveSearchEnabled,
      requestDelayMs: parsedDelay,
      tags: parsedTags,
      extraSettings: form.implementation === 'cardigann' ? indexerExtraSettings : undefined,
    };
  }

  function openNewProxyModal() {
    setEditingProxyId(null);
    setProxyName('');
    setProxyType('flaresolverr');
    setProxyHostOrUrl('http://flaresolverr:8191');
    setProxyPort('8191');
    setProxyUsername('');
    setProxyPassword('');
    setProxyTimeoutSec('60');
    setProxyTags('');
    setProxyEnabled(true);
    setProxyError(null);
    setProxyModalOpen(true);
  }

  function openEditProxyModal(p: IndexerProxyDto) {
    setEditingProxyId(p.id);
    setProxyName(p.name);
    const pt = (p.proxyType || 'http').toLowerCase();
    setProxyType(
      pt === 'flaresolverr' || pt === 'byparr' || pt === 'socks4' || pt === 'socks5' ? pt : 'http',
    );
    setProxyHostOrUrl(p.url || p.baseUrl || p.host || '');
    setProxyPort(p.port ? String(p.port) : pt === 'flaresolverr' || pt === 'byparr' ? '8191' : '8080');
    setProxyUsername(p.username ?? '');
    setProxyPassword('');
    setProxyTimeoutSec(String(p.requestTimeoutSec ?? 60));
    setProxyTags((p.tags ?? []).join(', '));
    setProxyEnabled(p.isEnabled ?? true);
    setProxyError(null);
    setProxyModalOpen(true);
  }

  const saveProxyMutation = useMutation({
    mutationFn: () => {
      const rawHost = proxyHostOrUrl.trim();
      let parsedHost = rawHost;
      let parsedPort = Number(proxyPort) || 8191;
      let fullUrl = rawHost;
      try {
        if (rawHost.startsWith('http://') || rawHost.startsWith('https://')) {
          const u = new URL(rawHost);
          parsedHost = u.hostname;
          if (u.port) parsedPort = Number(u.port);
        } else {
          fullUrl =
            proxyType === 'flaresolverr' || proxyType === 'byparr'
              ? `http://${rawHost}:${parsedPort}`
              : `${proxyType}://${rawHost}:${parsedPort}`;
        }
      } catch {
        // Keep raw host/url values as entered
      }
      const payload = {
        name: proxyName.trim(),
        proxyType,
        host: parsedHost,
        port: parsedPort,
        url: fullUrl,
        baseUrl: fullUrl,
        username: proxyUsername.trim() || null,
        password: proxyPassword || undefined,
        requestTimeoutSec: Math.max(5, Number(proxyTimeoutSec) || 60),
        tags: proxyTags
          .split(',')
          .map((t) => t.trim())
          .filter(Boolean),
        isEnabled: proxyEnabled,
      };
      if (editingProxyId !== null) {
        return apiFetch<IndexerProxyDto>(`/api/v1/indexers/proxies/${editingProxyId}`, {
          method: 'PUT',
          body: JSON.stringify(payload),
        });
      }
      return apiFetch<IndexerProxyDto>('/api/v1/indexers/proxies', {
        method: 'POST',
        body: JSON.stringify(payload),
      });
    },
    onSuccess: () => {
      setProxyModalOpen(false);
      setProxyError(null);
      qc.invalidateQueries({ queryKey: ['indexer-proxies'] });
    },
    onError: (err: Error) => {
      setProxyError(err.message);
    },
  });

  const deleteProxyMutation = useMutation({
    mutationFn: (id: number) => apiFetch(`/api/v1/indexers/proxies/${id}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['indexer-proxies'] }),
  });

  const saveIndexerMutation = useMutation({
    mutationFn: (form: IndexerFormState) => {
      const payload = buildIndexerRequestPayload(form);
      if (form.editingId !== null) {
        return apiFetch(`/api/v1/indexers/${form.editingId}`, {
          method: 'PUT',
          body: JSON.stringify(payload),
        });
      }
      return apiFetch('/api/v1/indexers', {
        method: 'POST',
        body: JSON.stringify(payload),
      });
    },
    onSuccess: () => {
      setIndexerForm(null);
      setIndexerTestStatus(null);
      qc.invalidateQueries({ queryKey: ['indexers'] });
    },
  });

  const testIndexerMutation = useMutation({
    mutationFn: (form: IndexerFormState) =>
      apiFetch<{ ok: boolean; message: string }>('/api/v1/indexers/test', {
        method: 'POST',
        body: JSON.stringify({
          indexer: buildIndexerRequestPayload({
            ...form,
            name: form.name.trim() || 'Indexer Test',
          }),
          existingId: form.editingId,
        }),
      }),
    onSuccess: (res) => {
      setIndexerTestStatus(res);
      qc.invalidateQueries({ queryKey: ['indexers'] });
    },
    onError: (err: Error) => {
      setIndexerTestStatus({ ok: false, message: err.message });
    },
  });

  const deleteIndexerMutation = useMutation({
    mutationFn: (id: number) => apiFetch(`/api/v1/indexers/${id}`, { method: 'DELETE' }),
    onSuccess: () => {
      setIndexerForm(null);
      qc.invalidateQueries({ queryKey: ['indexers'] });
    },
  });

  const searchReleasesMutation = useMutation({
    mutationFn: () =>
      apiFetch<SearchResultRow[]>('/api/v1/indexers/search', {
        method: 'POST',
        body: JSON.stringify({
          query: searchQuery.trim() || null,
          mediaVertical: searchVertical || null,
          interactive: true,
        }),
      }),
  });

  const [grabbingReleaseKey, setGrabbingReleaseKey] = useState<string | null>(null);

  const grabReleaseMutation = useMutation({
    mutationFn: ({ row, forceGrab = false }: { row: SearchResultRow; forceGrab?: boolean }) => {
      setGrabbingReleaseKey(row.release.guid ?? row.release.downloadUrl);
      return apiFetch<{ clientName: string; externalDownloadId: string; resolvedCategory: string }>(
        '/api/v1/downloaders/grab',
        {
          method: 'POST',
          body: JSON.stringify({
            title: row.release.title,
            protocol: row.release.protocol,
            mediaVertical: row.release.mediaVertical || searchVertical || 'movies',
            downloadUrlOrMagnet: row.release.magnetUrl || row.release.downloadUrl,
            infoHash: row.release.infoHash,
            releaseGuid: row.release.guid ?? null,
            indexerId: row.release.indexerId ?? null,
            indexerName: row.release.indexerName ?? null,
            sizeBytes: row.release.sizeBytes ?? null,
            publishedAt: row.release.publishedAt ?? null,
            forceGrab: Boolean(forceGrab),
          }),
        },
      );
    },
    onSuccess: (res, vars) => {
      setGrabbingReleaseKey(null);
      setGrabBanner(
        `${vars.forceGrab ? 'Force-grabbed' : 'Sent'} "${vars.row.release.title}" to ${res.clientName} (Category: ${res.resolvedCategory}, Job: ${res.externalDownloadId}).`,
      );
      qc.invalidateQueries({ queryKey: ['downloaders-queue'] });
    },
    onError: () => {
      setGrabbingReleaseKey(null);
    },
  });

  function openEditClient(cRaw: DownloadClientDto) {
    if (!canWriteDownloaders) return;
    const c = cRaw as ExtendedDownloadClientDto;
    const matchedDriver = (driversQuery.data?.drivers ?? []).find(
      (d) => d.implementation.toLowerCase() === c.implementation.toLowerCase(),
    ) ?? {
      implementation: c.implementation,
      displayName: c.implementation,
      protocol: c.protocol,
      defaultPort: c.port,
      authMode: 'api_key',
      description: '',
    };
    setShowAddClientPicker(false);
    setEditingClientId(c.id);
    setSelectedDriver(matchedDriver);
    setClientName(c.name);
    setClientHost(c.host);
    setClientPort(String(c.port));
    setClientUseSsl(Boolean(c.useSsl));
    setClientIsEnabled(c.isEnabled ?? true);
    setClientUrlBase(c.urlBase ?? '');
    setClientPriority(String(c.priority ?? 1));
    setClientRemoveCompleted(c.removeCompleted ?? true);
    setClientRemoveFailed(c.removeFailed ?? true);
    setClientCategoryMappings({
      ...(driversQuery.data?.defaultCategoryMappings ?? {}),
      ...(c.categoryMappings ?? {}),
    });
    setClientWatchFolder(c.extraSettings?.watchFolder ?? '');
    setClientTestStatus(null);
    setClientUsername(c.username ?? '');
    setClientPassword('');
    setClientApiKey('');
    setClientHasSavedKey(Boolean(c.apiKeySecret?.isSet));
    setClientHasSavedPass(Boolean(c.passwordSecret?.isSet));
  }

  function closeClientModal() {
    setShowAddClientPicker(false);
    setEditingClientId(null);
    setSelectedDriver(null);
    setClientApiKey('');
    setClientUsername('');
    setClientPassword('');
    setClientIsEnabled(true);
    setClientUrlBase('');
    setClientPriority('1');
    setClientRemoveCompleted(true);
    setClientRemoveFailed(true);
    setClientCategoryMappings({});
    setClientWatchFolder('');
    setClientTestStatus(null);
    setClientHasSavedKey(false);
    setClientHasSavedPass(false);
  }

  const saveClientMutation = useMutation({
    mutationFn: (payload: {
      id: number | null;
      name: string;
      implementation: string;
      host: string;
      port: number;
      useSsl?: boolean;
      urlBase?: string;
      apiKey?: string;
      username?: string;
      password?: string;
      priority?: number;
      isEnabled?: boolean;
      removeCompleted?: boolean;
      removeFailed?: boolean;
      categoryMappings?: Record<string, string>;
      extraSettings?: Record<string, string>;
    }) => {
      const body = {
        name: payload.name,
        implementation: payload.implementation,
        host: payload.host,
        port: payload.port,
        useSsl: payload.useSsl,
        urlBase: payload.urlBase || null,
        apiKey: payload.apiKey,
        username: payload.username,
        password: payload.password,
        priority: payload.priority ?? 1,
        isEnabled: payload.isEnabled ?? true,
        removeCompleted: payload.removeCompleted ?? true,
        removeFailed: payload.removeFailed ?? true,
        categoryMappings: payload.categoryMappings,
        extraSettings: payload.extraSettings,
      };
      if (payload.id !== null) {
        return apiFetch(`/api/v1/downloaders/${payload.id}`, {
          method: 'PUT',
          body: JSON.stringify(body),
        });
      }
      return apiFetch('/api/v1/downloaders', {
        method: 'POST',
        body: JSON.stringify(body),
      });
    },
    onSuccess: () => {
      closeClientModal();
      qc.invalidateQueries({ queryKey: ['downloaders'] });
    },
  });

  const testClientMutation = useMutation({
    mutationFn: async () => {
      if (editingClientId !== null) {
        try {
          return await apiFetch<{ ok?: boolean; success?: boolean; message: string }>(
            `/api/v1/downloadclients/${editingClientId}/test`,
            { method: 'POST' },
          );
        } catch {
          return await apiFetch<{ ok?: boolean; success?: boolean; message: string }>(
            `/api/v1/downloaders/${editingClientId}/test`,
            { method: 'POST' },
          );
        }
      }
      if (!selectedDriver) {
        throw new Error('Select a download client driver first.');
      }
      const draftBody = {
        name: clientName.trim() || selectedDriver.displayName,
        implementation: selectedDriver.implementation,
        host: clientHost.trim() || 'localhost',
        port: Number(clientPort) || selectedDriver.defaultPort,
        useSsl: clientUseSsl,
        urlBase: clientUrlBase.trim() || null,
        apiKey: clientApiKey.trim() || undefined,
        username: clientUsername.trim() || undefined,
        password: clientPassword || undefined,
        extraSettings: clientWatchFolder.trim() ? { watchFolder: clientWatchFolder.trim() } : undefined,
      };
      try {
        return await apiFetch<{ ok?: boolean; success?: boolean; message: string }>(
          '/api/v1/downloaders/test',
          { method: 'POST', body: JSON.stringify(draftBody) },
        );
      } catch {
        const created = await apiFetch<DownloadClientDto>('/api/v1/downloaders', {
          method: 'POST',
          body: JSON.stringify({
            ...draftBody,
            priority: Number(clientPriority) || 1,
            isEnabled: clientIsEnabled,
            removeCompleted: clientRemoveCompleted,
            removeFailed: clientRemoveFailed,
            categoryMappings: clientCategoryMappings,
          }),
        });
        setEditingClientId(created.id);
        qc.invalidateQueries({ queryKey: ['downloaders'] });
        try {
          return await apiFetch<{ ok?: boolean; success?: boolean; message: string }>(
            `/api/v1/downloadclients/${created.id}/test`,
            { method: 'POST' },
          );
        } catch {
          return await apiFetch<{ ok?: boolean; success?: boolean; message: string }>(
            `/api/v1/downloaders/${created.id}/test`,
            { method: 'POST' },
          );
        }
      }
    },
    onSuccess: (res) => {
      const ok = res.ok ?? res.success ?? true;
      setClientTestStatus({ ok, message: res.message || (ok ? 'Connection succeeded.' : 'Connection failed.') });
    },
    onError: (err: Error) => {
      setClientTestStatus({ ok: false, message: err.message });
    },
  });

  const deleteClientMutation = useMutation({
    mutationFn: (id: number) => apiFetch(`/api/v1/downloaders/${id}`, { method: 'DELETE' }),
    onSuccess: () => {
      closeClientModal();
      qc.invalidateQueries({ queryKey: ['downloaders'] });
    },
  });

  const sweepQueueMutation = useMutation({
    mutationFn: () =>
      apiFetch<Array<{ title: string; actionTaken: string; strikeCount: number }>>(
        '/api/v1/downloaders/queue/sweep',
        {
          method: 'POST',
          body: JSON.stringify({ strikeThreshold: 3 }),
        },
      ),
    onSuccess: (actions) => {
      setDownloaderBanner(
        actions.length === 0
          ? 'Queue Sweep completed — all active jobs are healthy.'
          : `Queue Sweep evaluated ${actions.length} item(s): ${actions.map((a) => `${a.title} [${a.actionTaken} ${a.strikeCount}/3]`).join('; ')}`,
      );
      qc.invalidateQueries({ queryKey: ['downloaders-queue'] });
      qc.invalidateQueries({ queryKey: ['downloaders-tracked'] });
    },
  });

  const importCompletedMutation = useMutation({
    mutationFn: () =>
      apiFetch<ProcessCompletedReport>('/api/v1/downloaders/process-completed', {
        method: 'POST',
        body: JSON.stringify({}),
      }),
    onSuccess: (rep) => {
      setDownloaderBanner(
        `Completed-Import Pipeline evaluated ${rep.evaluatedCount} item(s): ${rep.importedCount} imported (hardlinked), ${rep.quarantinedCount} quarantined, ${rep.rejectedCount} rejected.`,
      );
      qc.invalidateQueries({ queryKey: ['downloaders-queue'] });
      qc.invalidateQueries({ queryKey: ['downloaders-imports'] });
      qc.invalidateQueries({ queryKey: ['downloaders-tracked'] });
      qc.invalidateQueries({ queryKey: ['quarantine'] });
      qc.invalidateQueries({ queryKey: ['works'] });
    },
  });

  const rssSyncMutation = useMutation({
    mutationFn: () =>
      apiFetch<MultiCategoryRssSyncReport>('/api/v1/indexers/rss-sync', {
        method: 'POST',
      }),
    onSuccess: (report) => {
      setRssSyncResult(report);
      qc.invalidateQueries({ queryKey: ['indexers'] });
      qc.invalidateQueries({ queryKey: ['downloaders-tracked'] });
      qc.invalidateQueries({ queryKey: ['downloaders-queue'] });
    },
  });

  const retryImportMutation = useMutation({
    mutationFn: ({ id, req }: { id: number; req: ManualOverrideImportRequest }) =>
      apiFetch<ProcessedDownloadItemOutcome>(`/api/v1/downloaders/tracked/${id}/retry-import`, {
        method: 'POST',
        body: JSON.stringify(req),
      }),
    onSuccess: (outcome) => {
      const dest = outcome?.destinationFilePath ?? outcome?.destinationPath;
      const detail = outcome?.message ?? outcome?.warning;
      setDownloaderBanner(
        `Manual Import Override [${outcome?.verdict ?? outcome?.action ?? 'Processed'}]${dest ? ` -> ${dest}` : ''}${detail ? ` — ${detail}` : ''}`,
      );
      setSelectedTrackedForImport(null);
      qc.invalidateQueries({ queryKey: ['downloaders-tracked'] });
      qc.invalidateQueries({ queryKey: ['downloaders-queue'] });
      qc.invalidateQueries({ queryKey: ['downloaders-imports'] });
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
    },
    onError: (err: unknown) => {
      setManualImportError(err instanceof Error ? err.message : 'Manual import override failed.');
    },
  });

  const ignoreTrackedMutation = useMutation({
    mutationFn: (id: number) => {
      setIgnoringTrackedId(id);
      return apiFetch(`/api/v1/downloaders/tracked/${id}/ignore`, {
        method: 'POST',
      });
    },
    onSettled: () => {
      setIgnoringTrackedId(null);
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['downloaders-tracked'] });
    },
  });

  const createRemotePathMutation = useMutation({
    mutationFn: (body: {
      downloadClientId: number | null;
      host: string;
      remotePath: string;
      localPath: string;
    }) =>
      apiFetch<RemotePathMappingDto>('/api/v1/downloaders/remote-paths', {
        method: 'POST',
        body: JSON.stringify(body),
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['downloaders-remote-paths'] });
    },
  });

  const deleteRemotePathMutation = useMutation({
    mutationFn: (id: number) =>
      apiFetch(`/api/v1/downloaders/remote-paths/${id}`, {
        method: 'DELETE',
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['downloaders-remote-paths'] });
    },
  });

  function openManualImportModal(item: TrackedDownloadDto) {
    setSelectedTrackedForImport(item);
    setOverrideWorkId(item.workId ?? null);
    setOverrideSeason('');
    setOverrideEpisodes('');
    setBypassUpgrade(false);
    setConfirmedAudioLangs('');
    setManualImportError(null);
  }

  function handleExecuteManualImport() {
    if (!selectedTrackedForImport) return;
    const epNums = overrideEpisodes
      .split(',')
      .map((s) => parseInt(s.trim(), 10))
      .filter((n) => !isNaN(n));

    const audioLangs = confirmedAudioLangs
      .split(',')
      .map((s) => s.trim().toLowerCase())
      .filter(Boolean);

    const payload: ManualOverrideImportRequest = {
      workId: overrideWorkId,
      seasonNumber: overrideSeason.trim() ? parseInt(overrideSeason.trim(), 10) : null,
      episodeNumbers: epNums.length > 0 ? epNums : null,
      bypassUpgradeCheck: bypassUpgrade,
      confirmedAudioLanguages: audioLangs.length > 0 ? audioLangs : null,
    };

    retryImportMutation.mutate({ id: selectedTrackedForImport.id, req: payload });
  }

  function toggleCategory(catId: number) {
    setIndexerForm((prev) => {
      if (!prev) return prev;
      const exists = prev.categories.includes(catId);
      const next = exists
        ? prev.categories.filter((c) => c !== catId)
        : [...prev.categories, catId].sort((a, b) => a - b);
      return { ...prev, categories: next };
    });
  }

  function handleAddCustomCategory() {
    const trimmed = customCategoryInput.trim();
    if (!trimmed) return;
    const parsed = parseInt(trimmed, 10);
    if (!Number.isFinite(parsed) || parsed <= 0) return;

    setExtraCategoryIds((prev) => (prev.includes(parsed) ? prev : [...prev, parsed]));
    setIndexerForm((prev) => {
      if (!prev) return prev;
      if (prev.categories.includes(parsed)) return prev;
      return {
        ...prev,
        categories: [...prev.categories, parsed].sort((a, b) => a - b),
      };
    });
    setCustomCategoryInput('');
  }

  const indexerFormCategories = indexerForm?.categories;
  const visibleCategories = useMemo(() => {
    const ids = new Set<number>();
    const list: Array<{ id: number; label: string }> = [];

    for (const cat of STANDARD_CATEGORIES) {
      ids.add(cat.id);
      list.push(cat);
    }

    for (const id of extraCategoryIds) {
      if (!ids.has(id)) {
        ids.add(id);
        list.push({ id, label: getCategoryLabel(id) });
      }
    }

    if (indexerFormCategories) {
      for (const id of indexerFormCategories) {
        if (!ids.has(id)) {
          ids.add(id);
          list.push({ id, label: getCategoryLabel(id) });
        }
      }
    }

    return list.sort((a, b) => a.id - b.id);
  }, [extraCategoryIds, indexerFormCategories]);

  return (
    <div className="flex flex-col flex-1">
      <PageToolbar
        left={
          <>
            <PageToolbarButton
              icon={RefreshCw}
              label="Refresh"
              spinning={
                indexersQuery.isFetching ||
                clientsQuery.isFetching ||
                queueQuery.isFetching ||
                importsQuery.isFetching ||
                trackedQuery.isFetching ||
                parsePreview.isFetching ||
                rssSyncMutation.isPending
              }
              onClick={() => {
                if (section === 'indexers') {
                  void indexersQuery.refetch();
                }
                if (section === 'downloaders') {
                  void clientsQuery.refetch();
                  void queueQuery.refetch();
                  void importsQuery.refetch();
                  void trackedQuery.refetch();
                }
                if (section === 'parser') void parsePreview.refetch();
              }}
            />

            {section === 'indexers' && canWriteIndexers && (
              <>
                <PageToolbarSeparator />
                <PageToolbarButton
                  icon={Plus}
                  label="Add Indexer"
                  onClick={() => {
                    setShowIndexerCatalogModal(true);
                  }}
                />
                <PageToolbarButton
                  icon={FolderSync}
                  label="RSS Sync"
                  spinning={rssSyncMutation.isPending}
                  onClick={() => rssSyncMutation.mutate()}
                  title="Run automated multi-category RSS sync across all active indexers"
                />
              </>
            )}

            {section === 'downloaders' && canWriteDownloaders && (
              <>
                <PageToolbarSeparator />
                <PageToolbarButton
                  icon={Plus}
                  label="Add Client"
                  onClick={() => {
                    setEditingClientId(null);
                    setSelectedDriver(null);
                    setClientIsEnabled(true);
                    setClientUrlBase('');
                    setClientPriority('1');
                    setClientRemoveCompleted(true);
                    setClientRemoveFailed(true);
                    setClientCategoryMappings({ ...(driversQuery.data?.defaultCategoryMappings ?? {}) });
                    setClientWatchFolder('');
                    setClientTestStatus(null);
                    setShowAddClientPicker(true);
                  }}
                />
                <PageToolbarButton
                  icon={ShieldAlert}
                  label="Sweep Queue"
                  spinning={sweepQueueMutation.isPending}
                  onClick={() => sweepQueueMutation.mutate()}
                />
                <PageToolbarButton
                  icon={FolderSync}
                  label="Import Completed"
                  spinning={importCompletedMutation.isPending}
                  onClick={() => importCompletedMutation.mutate()}
                />
              </>
            )}

            {section === 'parser' && (
              <>
                <PageToolbarSeparator />
                <PageToolbarButton
                  icon={FileSearch}
                  label="Parse Sample"
                  onClick={() =>
                    setParseTitle(
                      'Dune.Part.Two.2024.IMAX.GERMAN.DL.2160p.UHD.BluRay.REMUX.DV.HDR10.TrueHD.Atmos.HEVC-GROUP',
                    )
                  }
                />
              </>
            )}
          </>
        }
      />

      <PageContentBody>
        {section === 'indexers' && canReadIndexers && (
          <>
            {grabBanner && (
              <ArrAlert kind="success" className="mb-4">
                {grabBanner}
              </ArrAlert>
            )}

            {rssSyncResult && (
              <div className="mb-5 rounded-[4px] border border-arr-card-border bg-[#222528] p-4 text-[#eeeeee]">
                <div className="flex items-center justify-between pb-3 border-b border-[#303438] mb-3">
                  <div className="flex items-center gap-2">
                    <FolderSync className="h-4 w-4 text-arr-accent" />
                    <span className="font-bold text-[14px]">Multi-Category RSS Sync Report</span>
                  </div>
                  <button
                    type="button"
                    onClick={() => setRssSyncResult(null)}
                    className="text-[#909293] hover:text-white p-1 cursor-pointer rounded hover:bg-[#2f3338]"
                    title="Dismiss Sync Report"
                  >
                    <X className="h-4 w-4" />
                  </button>
                </div>

                <div className="grid grid-cols-2 sm:grid-cols-5 gap-3 mb-4">
                  <div className="bg-[#1b1d1f] p-2.5 rounded border border-[#2e3236]">
                    <div className="text-[11px] text-[#909293]">Polled Indexers</div>
                    <div className="text-[16px] font-bold text-[#eeeeee]">{rssSyncResult.polledIndexers}</div>
                  </div>
                  <div className="bg-[#1b1d1f] p-2.5 rounded border border-[#2e3236]">
                    <div className="text-[11px] text-[#909293]">Total Releases</div>
                    <div className="text-[16px] font-bold text-[#eeeeee]">{rssSyncResult.totalReleases}</div>
                  </div>
                  <div className="bg-[#1b1d1f] p-2.5 rounded border border-[#2e3236]">
                    <div className="text-[11px] text-[#909293]">Matched Candidates</div>
                    <div className="text-[16px] font-bold text-arr-accent">{rssSyncResult.matchedCandidatesCount ?? 0}</div>
                  </div>
                  <div className="bg-[#1b1d1f] p-2.5 rounded border border-[#2e3236]">
                    <div className="text-[11px] text-[#909293]">Approved</div>
                    <div className="text-[16px] font-bold text-[#27c24c]">{rssSyncResult.approvedCount ?? 0}</div>
                  </div>
                  <div className="bg-[#1b1d1f] p-2.5 rounded border border-[#2e3236]">
                    <div className="text-[11px] text-[#909293]">Grabbed</div>
                    <div className="text-[16px] font-bold text-arr-accent">{rssSyncResult.grabbedCount ?? 0}</div>
                  </div>
                </div>

                {rssSyncResult.decisions && rssSyncResult.decisions.length > 0 && (
                  <div>
                    <div className="text-[12px] font-semibold text-[#cccccc] mb-2 flex items-center justify-between">
                      <span>Sync Evaluation Decisions ({rssSyncResult.decisions.length})</span>
                      <span className="text-[11px] text-[#909293]">Automated Release Ingestion Pipeline</span>
                    </div>
                    <div className="max-h-64 overflow-y-auto rounded border border-[#2e3236] bg-[#191b1d]">
                      <table className="arr-table text-[12px]">
                        <thead>
                          <tr>
                            <th>Release</th>
                            <th>Matched Work</th>
                            <th>Decision Kind</th>
                            <th>Outcome</th>
                            <th>Explanation</th>
                          </tr>
                        </thead>
                        <tbody>
                          {rssSyncResult.decisions.map((dec, i) => (
                            <tr key={i}>
                              <td className="font-mono text-[#eeeeee] max-w-xs truncate" title={dec.releaseTitle}>
                                {dec.releaseTitle}
                              </td>
                              <td className="text-arr-accent font-medium">{dec.matchedWorkTitle ?? '-'}</td>
                              <td>
                                <ArrLabel
                                  kind={
                                    dec.decisionKind === 'Upgrade'
                                      ? 'primary'
                                      : dec.decisionKind === 'Grab'
                                        ? 'success'
                                        : 'default'
                                  }
                                >
                                  {dec.decisionKind ?? 'Evaluated'}
                                </ArrLabel>
                              </td>
                              <td>
                                {dec.grabbed ? (
                                  <ArrLabel kind="success">Grabbed</ArrLabel>
                                ) : dec.approved ? (
                                  <ArrLabel kind="primary">Approved</ArrLabel>
                                ) : (
                                  <ArrLabel kind="warning">Rejected</ArrLabel>
                                )}
                              </td>
                              <td className="text-[#909293] font-mono text-[11px] max-w-md truncate" title={dec.explanation ?? ''}>
                                {dec.explanation ?? '-'}
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  </div>
                )}
              </div>
            )}

            <FieldSet legend="Indexers">
              <div className="flex flex-wrap gap-4">
                {(indexersQuery.data ?? []).map((ix) => (
                  <ArrSettingsCard
                    key={ix.id}
                    title={ix.name}
                    onClick={canWriteIndexers ? () => openEditIndexer(ix) : undefined}
                    footer={
                      <>
                        <span className="font-mono text-[11px] text-[#909293] truncate max-w-[165px]" title={ix.baseUrl}>
                          {ix.baseUrl}
                        </span>
                        <div className="inline-flex items-center gap-1.5">
                          <ArrLabel kind={ix.isHealthy ? 'success' : 'danger'}>
                            {ix.isHealthy ? 'Enabled' : `Backoff (${ix.consecutiveFailures})`}
                          </ArrLabel>
                          {canWriteIndexers && (
                            <button
                              type="button"
                              title="Remove indexer"
                              onClick={(e) => {
                                e.stopPropagation();
                                deleteIndexerMutation.mutate(ix.id);
                              }}
                              className="text-[#909293] hover:text-[#f05050] cursor-pointer p-0.5"
                            >
                              <Trash2 className="h-3.5 w-3.5" />
                            </button>
                          )}
                        </div>
                      </>
                    }
                  >
                    <div className="flex flex-wrap gap-1 pt-1">
                      <ArrLabel kind={ix.protocol.toLowerCase() === 'usenet' ? 'usenet' : 'torrent'}>
                        {ix.protocol.toLowerCase() === 'usenet' ? 'Usenet' : 'Torrent'}
                      </ArrLabel>
                      {ix.rssEnabled && <ArrLabel kind="success">RSS</ArrLabel>}
                      {ix.autoSearchEnabled && <ArrLabel kind="primary">Automatic Search</ArrLabel>}
                      {ix.interactiveSearchEnabled && <ArrLabel kind="success">Interactive Search</ArrLabel>}
                    </div>
                    <div className="text-[11px] text-[#909293] pt-1">
                      Implementation: <code className="text-[#cccccc]">{ix.implementation}</code>
                      {ix.definitionId ? ` (${ix.definitionId})` : ''} • Priority {ix.priority ?? 25}
                    </div>
                    {ix.lastError && !ix.isHealthy && (
                      <div className="text-[11px] text-[#f05050] truncate" title={ix.lastError}>
                        {ix.lastError}
                      </div>
                    )}
                  </ArrSettingsCard>
                ))}

                {canWriteIndexers && (
                  <ArrAddCard
                    label="Add Indexer"
                    onClick={() => {
                      setShowIndexerCatalogModal(true);
                    }}
                  />
                )}
              </div>
            </FieldSet>

            <FieldSet legend="Interactive Search & Grab">
              <div className="flex flex-wrap items-center gap-2 mb-4">
                <select
                  value={searchVertical}
                  onChange={(e) => setSearchVertical(e.target.value)}
                  className="arr-input !w-40"
                >
                  <option value="movies">Movies</option>
                  <option value="tv">TV</option>
                  <option value="anime">Anime</option>
                  <option value="music">Music</option>
                  <option value="ebooks">E-Books</option>
                  <option value="audiobooks">Audiobooks</option>
                </select>
                <input
                  value={searchQuery}
                  onChange={(e) => setSearchQuery(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') searchReleasesMutation.mutate();
                  }}
                  placeholder="Query title (or leave blank to list latest feed items)..."
                  className="arr-input flex-1 min-w-[240px]"
                />
                <ArrButton
                  variant="primary"
                  busy={searchReleasesMutation.isPending}
                  onClick={() => searchReleasesMutation.mutate()}
                >
                  <Search className="h-3.5 w-3.5" /> Search Indexers
                </ArrButton>
              </div>

              {searchReleasesMutation.data && (
                <div className="overflow-x-auto">
                  {searchReleasesMutation.data.length === 0 ? (
                    <div className="text-[13px] text-[#909293]">No releases returned from enabled indexers.</div>
                  ) : (
                    <table className="arr-table">
                      <thead>
                        <tr>
                          <th>Protocol</th>
                          <th>Indexer</th>
                          <th>Release Title</th>
                          <th>Languages</th>
                          <th>Quality</th>
                          <th>Size</th>
                          <th className="text-right">Grab</th>
                        </tr>
                      </thead>
                      <tbody>
                        {searchReleasesMutation.data.map((row) => {
                          const releaseKey =
                            row.release.guid ||
                            `${row.release.indexerId}-${row.release.title}-${row.release.downloadUrl}`;
                          const isBusy =
                            grabReleaseMutation.isPending &&
                            grabbingReleaseKey === (row.release.guid ?? row.release.downloadUrl);

                          return (
                            <tr key={releaseKey}>
                              <td>
                                <ArrLabel
                                  kind={row.release.protocol.toLowerCase() === 'usenet' ? 'usenet' : 'torrent'}
                                >
                                  {row.release.protocol.toUpperCase()}
                                </ArrLabel>
                              </td>
                              <td className="text-[#cccccc]">
                                {row.release.indexerName ?? (row.release.indexerId ? `#${row.release.indexerId}` : '-')}
                              </td>
                              <td className="font-mono text-[12px] text-[#eeeeee]">
                                <div className="flex flex-wrap items-center gap-1.5">
                                  <span>{row.release.title}</span>
                                  {row.isBlocklisted && (
                                    <span
                                      title={
                                        row.rejections?.join(' • ') ||
                                        'Previously failed release — skipped automatically'
                                      }
                                    >
                                      <ArrLabel kind="danger">
                                        {row.release.protocol?.toLowerCase() === 'usenet'
                                          ? 'Failed NZB (Blocklisted)'
                                          : 'Blocklisted'}
                                      </ArrLabel>
                                    </span>
                                  )}
                                </div>
                              </td>
                              <td>
                                <div className="flex flex-wrap gap-1">
                                  {(row.parsed.detectedLanguages ?? []).map((lang) => (
                                    <ArrLabel key={lang} kind="primary">
                                      {lang}
                                    </ArrLabel>
                                  ))}
                                  {row.parsed.isDualAudio && <ArrLabel kind="warning">DL</ArrLabel>}
                                </div>
                              </td>
                              <td>
                                <ArrLabel kind="inverse">
                                  {[row.parsed.source, row.parsed.resolution].filter(Boolean).join(' ') || 'Auto'}
                                </ArrLabel>
                              </td>
                              <td>{formatBytes(row.release.sizeBytes ?? 0)}</td>
                              <td className="text-right">
                                {row.isBlocklisted ? (
                                  <ArrButton
                                    variant="warning"
                                    className="h-[26px] px-2.5 text-[11px]"
                                    busy={isBusy}
                                    disabled={grabReleaseMutation.isPending}
                                    onClick={() => grabReleaseMutation.mutate({ row, forceGrab: true })}
                                    title="Force grab release despite failed NZB blocklist"
                                  >
                                    <Download className="h-3 w-3" /> Force Grab
                                  </ArrButton>
                                ) : (
                                  <ArrButton
                                    variant="primary"
                                    className="h-[26px] px-2.5 text-[11px]"
                                    busy={isBusy}
                                    disabled={grabReleaseMutation.isPending}
                                    onClick={() => grabReleaseMutation.mutate({ row, forceGrab: false })}
                                  >
                                    <Download className="h-3 w-3" /> Grab
                                  </ArrButton>
                                )}
                              </td>
                            </tr>
                          );
                        })}
                      </tbody>
                    </table>
                  )}
                </div>
              )}
            </FieldSet>

            {/* ==================== INDEXER PROXIES MANAGEMENT PANEL ==================== */}
            <FieldSet
              legend={
                <div className="flex items-center gap-2">
                  <ShieldCheck className="h-4 w-4 text-arr-accent" />
                  <span>Indexer Proxies ({(proxiesQuery.data ?? []).length})</span>
                </div>
              }
              action={
                canWriteIndexers && (
                  <ArrButton variant="success" onClick={openNewProxyModal}>
                    <Plus className="h-3.5 w-3.5" /> Add Proxy
                  </ArrButton>
                )
              }
            >
              <p className="text-[12px] text-[#909293] mb-3">
                Route indexer traffic through FlareSolverr, Byparr, HTTP, or SOCKS4/SOCKS5 proxies matched by tags.
              </p>
              {(proxiesQuery.data ?? []).length === 0 ? (
                <div className="text-[13px] text-[#888888]">
                  No indexer proxies configured.
                </div>
              ) : (
                <div className="overflow-x-auto border border-arr-card-border rounded-[4px]">
                  <table className="arr-table">
                    <thead>
                      <tr>
                        <th>Name</th>
                        <th>Type</th>
                        <th>Endpoint</th>
                        <th>Timeout</th>
                        <th>Tags</th>
                        <th>Status</th>
                        {canWriteIndexers && <th className="text-right">Actions</th>}
                      </tr>
                    </thead>
                    <tbody>
                      {(proxiesQuery.data ?? []).map((pr) => (
                        <tr key={pr.id}>
                          <td className="font-semibold text-[#eeeeee]">{pr.name}</td>
                          <td>
                            <ArrLabel kind="primary">{pr.proxyType}</ArrLabel>
                          </td>
                          <td className="font-mono text-[12px] text-arr-accent">
                            {pr.url || pr.baseUrl || (pr.host ? `${pr.host}${pr.port ? `:${pr.port}` : ''}` : '-')}
                          </td>
                          <td className="font-mono text-[12px]">{pr.requestTimeoutSec ?? 60}s</td>
                          <td>
                            <div className="flex flex-wrap gap-1">
                              {(pr.tags ?? []).length > 0
                                ? (pr.tags ?? []).map((t) => (
                                    <ArrLabel key={t} kind="default">
                                      {t}
                                    </ArrLabel>
                                  ))
                                : '-'}
                            </div>
                          </td>
                          <td>
                            <ArrLabel kind={pr.isEnabled !== false ? 'success' : 'default'}>
                              {pr.isEnabled !== false ? 'Enabled' : 'Disabled'}
                            </ArrLabel>
                          </td>
                          {canWriteIndexers && (
                            <td className="text-right">
                              <div className="inline-flex items-center gap-1.5">
                                <ArrButton
                                  className="!h-[26px] !px-2 !text-[11px]"
                                  onClick={() => openEditProxyModal(pr)}
                                >
                                  <Edit2 className="h-3 w-3" /> Edit
                                </ArrButton>
                                <ArrButton
                                  variant="danger"
                                  className="!h-[26px] !px-2 !text-[11px]"
                                  busy={deleteProxyMutation.isPending && deleteProxyMutation.variables === pr.id}
                                  onClick={() => deleteProxyMutation.mutate(pr.id)}
                                >
                                  <Trash2 className="h-3 w-3" />
                                </ArrButton>
                              </div>
                            </td>
                          )}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </FieldSet>

            {/* ==================== UNIFIED ADD INDEXER PICKER MODAL ==================== */}
            {showIndexerCatalogModal && (
              <ArrModal
                title="Add Indexer"
                onClose={() => setShowIndexerCatalogModal(false)}
                maxWidth="max-w-2xl"
                footer={<ArrButton onClick={() => setShowIndexerCatalogModal(false)}>Close</ArrButton>}
              >
                <div>
                  <div className="text-[12px] font-semibold uppercase tracking-wider text-[#909293] mb-2.5">
                    Select Indexer Protocol
                  </div>
                  <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                    <button
                      type="button"
                      onClick={() => openNewNabIndexer('newznab')}
                      className="text-left p-3.5 bg-arr-card hover:bg-[#3d3d3d] border border-[#444444] rounded-[4px] transition-colors cursor-pointer"
                    >
                      <div className="flex items-center justify-between gap-2 mb-1">
                        <span className="text-[15px] font-medium text-[#eeeeee]">Newznab</span>
                        <ArrLabel kind="usenet">Usenet</ArrLabel>
                      </div>
                      <div className="text-[12px] text-[#909293]">
                        Any Usenet indexer or aggregator that speaks the Newznab API
                      </div>
                    </button>

                    <button
                      type="button"
                      onClick={() => openNewNabIndexer('torznab')}
                      className="text-left p-3.5 bg-arr-card hover:bg-[#3d3d3d] border border-[#444444] rounded-[4px] transition-colors cursor-pointer"
                    >
                      <div className="flex items-center justify-between gap-2 mb-1">
                        <span className="text-[15px] font-medium text-[#eeeeee]">Torznab</span>
                        <ArrLabel kind="torrent">Torrent</ArrLabel>
                      </div>
                      <div className="text-[12px] text-[#909293]">
                        Any torrent indexer or proxy that speaks the Torznab API
                      </div>
                    </button>

                    {(definitionsQuery.data ?? []).length > 0 && (
                      <button
                        type="button"
                        onClick={() => openNewNabIndexer('cardigann')}
                        className="text-left p-3.5 bg-arr-card hover:bg-[#3d3d3d] border border-[#444444] rounded-[4px] transition-colors cursor-pointer"
                      >
                        <div className="flex items-center justify-between gap-2 mb-1">
                          <span className="text-[15px] font-medium text-[#eeeeee]">Cardigann</span>
                          <ArrLabel kind="primary">Definition</ArrLabel>
                        </div>
                        <div className="text-[12px] text-[#909293]">
                          Custom YAML/JSON scraper definition ({(definitionsQuery.data ?? []).length} available)
                        </div>
                      </button>
                    )}
                  </div>
                </div>
              </ArrModal>
            )}

            {/* ==================== ADD / EDIT INDEXER PROXY MODAL ==================== */}
            {proxyModalOpen && (
              <ArrModal
                title={editingProxyId !== null ? `Edit Indexer Proxy — ${proxyName}` : 'Add Indexer Proxy'}
                onClose={() => setProxyModalOpen(false)}
                maxWidth="max-w-lg"
                footer={
                  <>
                    <ArrButton onClick={() => setProxyModalOpen(false)}>Cancel</ArrButton>
                    <ArrButton
                      variant="primary"
                      disabled={!proxyName.trim() || !proxyHostOrUrl.trim()}
                      busy={saveProxyMutation.isPending}
                      onClick={() => saveProxyMutation.mutate()}
                    >
                      Save Proxy
                    </ArrButton>
                  </>
                }
              >
                {proxyError && (
                  <ArrAlert kind="danger" className="mb-4">
                    {proxyError}
                  </ArrAlert>
                )}
                <FormGroup label="Name">
                  <input
                    value={proxyName}
                    onChange={(e) => setProxyName(e.target.value)}
                    placeholder="e.g. FlareSolverr Homelab"
                    className="arr-input"
                  />
                </FormGroup>
                <FormGroup label="Proxy Type">
                  <select
                    value={proxyType}
                    onChange={(e) => {
                      const next = e.target.value as 'flaresolverr' | 'byparr' | 'http' | 'socks4' | 'socks5';
                      setProxyType(next);
                      if (next === 'flaresolverr' || next === 'byparr') setProxyPort('8191');
                      else if (next === 'http') setProxyPort('8080');
                      else setProxyPort('1080');
                    }}
                    className="arr-input"
                  >
                    <option value="flaresolverr">FlareSolverr</option>
                    <option value="byparr">Byparr</option>
                    <option value="http">HTTP Proxy</option>
                    <option value="socks4">SOCKS4 Proxy</option>
                    <option value="socks5">SOCKS5 Proxy</option>
                  </select>
                </FormGroup>
                <FormGroup
                  label={proxyType === 'flaresolverr' || proxyType === 'byparr' ? 'URL or Host' : 'Host'}
                  helpText="Full URL (http://flaresolverr:8191) or hostname/IP."
                >
                  <input
                    value={proxyHostOrUrl}
                    onChange={(e) => setProxyHostOrUrl(e.target.value)}
                    placeholder="http://flaresolverr:8191"
                    className="arr-input font-mono text-[12px]"
                  />
                </FormGroup>
                <FormGroup label="Port">
                  <input
                    type="number"
                    value={proxyPort}
                    onChange={(e) => setProxyPort(e.target.value)}
                    className="arr-input font-mono !w-28"
                  />
                </FormGroup>
                <FormGroup label="Request Timeout (sec)">
                  <input
                    type="number"
                    min="5"
                    max="300"
                    value={proxyTimeoutSec}
                    onChange={(e) => setProxyTimeoutSec(e.target.value)}
                    className="arr-input font-mono !w-28"
                  />
                </FormGroup>
                {(proxyType === 'http' || proxyType === 'socks4' || proxyType === 'socks5') && (
                  <>
                    <FormGroup label="Username">
                      <input
                        value={proxyUsername}
                        onChange={(e) => setProxyUsername(e.target.value)}
                        placeholder="Optional proxy username"
                        className="arr-input"
                      />
                    </FormGroup>
                    <FormGroup label="Password">
                      <input
                        type="password"
                        value={proxyPassword}
                        onChange={(e) => setProxyPassword(e.target.value)}
                        placeholder="Optional proxy password"
                        className="arr-input"
                      />
                    </FormGroup>
                  </>
                )}
                <FormGroup label="Tags" helpText="Comma-separated tags matching indexers that should use this proxy.">
                  <input
                    value={proxyTags}
                    onChange={(e) => setProxyTags(e.target.value)}
                    placeholder="e.g. cloudflare, public-trackers"
                    className="arr-input font-mono text-[12px]"
                  />
                </FormGroup>
                <FormGroup label="Enabled">
                  <label className="inline-flex items-center gap-2 pt-1.5 text-[13px] cursor-pointer">
                    <input
                      type="checkbox"
                      checked={proxyEnabled}
                      onChange={(e) => setProxyEnabled(e.target.checked)}
                    />
                    Enable this proxy
                  </label>
                </FormGroup>
              </ArrModal>
            )}

            {/* ==================== ADD / EDIT INDEXER MODAL ==================== */}
            {indexerForm && (
              <ArrModal
                title={
                  indexerForm.editingId !== null
                    ? `Edit Indexer — ${indexerForm.name || 'Indexer'}`
                    : `Add ${indexerForm.implementation === 'torznab' ? 'Torznab' : indexerForm.implementation === 'cardigann' ? 'Cardigann' : 'Newznab'} Indexer`
                }
                onClose={() => {
                  setIndexerForm(null);
                  setIndexerTestStatus(null);
                }}
                maxWidth="max-w-2xl"
                footer={
                  <div className="w-full flex flex-wrap items-center justify-between gap-2">
                    <div>
                      {indexerForm.editingId !== null && (
                        <ArrButton
                          variant="danger"
                          busy={deleteIndexerMutation.isPending}
                          onClick={() => deleteIndexerMutation.mutate(indexerForm.editingId!)}
                        >
                          <Trash2 className="h-3.5 w-3.5" /> Delete
                        </ArrButton>
                      )}
                    </div>
                    <div className="flex items-center gap-2 ml-auto">
                      <ArrButton
                        busy={testIndexerMutation.isPending}
                        disabled={!indexerForm.baseUrl.trim()}
                        onClick={() => testIndexerMutation.mutate(indexerForm)}
                      >
                        <FlaskConical className="h-3.5 w-3.5" /> Test
                      </ArrButton>
                      <ArrButton
                        onClick={() => {
                          setIndexerForm(null);
                          setIndexerTestStatus(null);
                        }}
                      >
                        Cancel
                      </ArrButton>
                      <ArrButton
                        variant="primary"
                        type="submit"
                        form="indexer-config-form"
                        busy={saveIndexerMutation.isPending}
                        disabled={!indexerForm.name.trim() || !indexerForm.baseUrl.trim()}
                      >
                        Save
                      </ArrButton>
                    </div>
                  </div>
                }
              >
                {indexerTestStatus && (
                  <ArrAlert kind={indexerTestStatus.ok ? 'success' : 'danger'} className="mb-4">
                    {indexerTestStatus.message}
                  </ArrAlert>
                )}

                {saveIndexerMutation.isError && (
                  <ArrAlert kind="danger" className="mb-4">
                    {(saveIndexerMutation.error as Error)?.message ?? 'Failed to save indexer.'}
                  </ArrAlert>
                )}

                <form
                  id="indexer-config-form"
                  onSubmit={(e) => {
                    e.preventDefault();
                    if (indexerForm.name.trim() && indexerForm.baseUrl.trim()) {
                      saveIndexerMutation.mutate(indexerForm);
                    }
                  }}
                >
                  <FormGroup label="Name" htmlFor="indexer-name">
                    <input
                      id="indexer-name"
                      name="name"
                      value={indexerForm.name}
                      onChange={(e) => setIndexerForm({ ...indexerForm, name: e.target.value })}
                      placeholder="e.g. My Usenet indexer"
                      className="arr-input"
                      required
                    />
                  </FormGroup>

                  <FormGroup label="Implementation">
                    <div className="flex flex-wrap items-center gap-5 pt-1.5">
                      <div className="flex items-center gap-2">
                        <input
                          type="radio"
                          id="indexer-impl-newznab"
                          name="implementation"
                          value="newznab"
                          checked={indexerForm.implementation === 'newznab'}
                          onChange={() =>
                            setIndexerForm({
                              ...indexerForm,
                              implementation: 'newznab',
                              protocol: 'usenet',
                            })
                          }
                          className="accent-arr-accent cursor-pointer"
                        />
                        <label
                          htmlFor="indexer-impl-newznab"
                          className="text-[13px] text-[#eeeeee] cursor-pointer"
                        >
                          Newznab (Usenet NZB)
                        </label>
                      </div>

                      <div className="flex items-center gap-2">
                        <input
                          type="radio"
                          id="indexer-impl-torznab"
                          name="implementation"
                          value="torznab"
                          checked={indexerForm.implementation === 'torznab'}
                          onChange={() =>
                            setIndexerForm({
                              ...indexerForm,
                              implementation: 'torznab',
                              protocol: 'torrent',
                            })
                          }
                          className="accent-arr-accent cursor-pointer"
                        />
                        <label
                          htmlFor="indexer-impl-torznab"
                          className="text-[13px] text-[#eeeeee] cursor-pointer"
                        >
                          Torznab (Torrent / Magnet)
                        </label>
                      </div>

                      <div className="flex items-center gap-2">
                        <input
                          type="radio"
                          id="indexer-impl-cardigann"
                          name="implementation"
                          value="cardigann"
                          checked={indexerForm.implementation === 'cardigann'}
                          onChange={() => {
                            const firstDef = (definitionsQuery.data ?? [])[0];
                            setIndexerForm({
                              ...indexerForm,
                              implementation: 'cardigann',
                              protocol: 'torrent',
                              definitionId: indexerForm.definitionId || firstDef?.id || '',
                            });
                            if (!indexerForm.definitionId && firstDef) {
                              applyCardigannDefinitionDefaults(firstDef.id, true);
                            }
                          }}
                          className="accent-arr-accent cursor-pointer"
                        />
                        <label
                          htmlFor="indexer-impl-cardigann"
                          className="text-[13px] text-[#eeeeee] cursor-pointer"
                        >
                          Cardigann Definition
                        </label>
                      </div>
                    </div>
                  </FormGroup>

                  {indexerForm.implementation === 'cardigann' && (() => {
                    const defs = definitionsQuery.data ?? [];
                    const selectedDef = defs.find((d) => d.id === indexerForm.definitionId);
                    const defSettings = selectedDef?.settings ?? [];
                    return (
                      <>
                        <FormGroup
                          label="Cardigann Definition"
                          helpText={selectedDef?.description || 'Select a native Cardigann scraper definition.'}
                        >
                          {defs.length > 0 ? (
                            <select
                              value={indexerForm.definitionId ?? ''}
                              onChange={(e) => applyCardigannDefinitionDefaults(e.target.value, false)}
                              className="arr-input"
                            >
                              <option value="">-- Select Definition --</option>
                              {defs.map((d) => (
                                <option key={d.id} value={d.id}>
                                  {d.displayName || d.name || d.id} ({d.id})
                                </option>
                              ))}
                            </select>
                          ) : (
                            <input
                              value={indexerForm.definitionId ?? ''}
                              onChange={(e) =>
                                setIndexerForm({ ...indexerForm, definitionId: e.target.value })
                              }
                              placeholder="e.g. 1337x, nyaa"
                              className="arr-input font-mono"
                            />
                          )}
                        </FormGroup>

                        {defSettings.map((field) => {
                          const fieldType = (field.type ?? 'text').toLowerCase();
                          const val = indexerExtraSettings[field.name] ?? (field.default !== undefined ? String(field.default) : '');
                          if (fieldType === 'checkbox' || fieldType === 'bool' || fieldType === 'boolean') {
                            return (
                              <FormGroup key={field.name} label={field.label || field.name}>
                                <label className="inline-flex items-center gap-2 pt-1.5 text-[13px] cursor-pointer">
                                  <input
                                    type="checkbox"
                                    checked={val === 'true'}
                                    onChange={(e) =>
                                      setIndexerExtraSettings((prev) => ({
                                        ...prev,
                                        [field.name]: e.target.checked ? 'true' : 'false',
                                      }))
                                    }
                                    className="accent-arr-accent"
                                  />
                                  <span>{field.label || field.name}</span>
                                </label>
                              </FormGroup>
                            );
                          }
                          if (fieldType === 'select' && field.options) {
                            const opts = Array.isArray(field.options)
                              ? field.options
                              : Object.entries(field.options).map(([value, label]) => ({ value, label }));
                            return (
                              <FormGroup key={field.name} label={field.label || field.name}>
                                <select
                                  value={val}
                                  onChange={(e) =>
                                    setIndexerExtraSettings((prev) => ({
                                      ...prev,
                                      [field.name]: e.target.value,
                                    }))
                                  }
                                  className="arr-input"
                                >
                                  {opts.map((o) => (
                                    <option key={o.value} value={o.value}>
                                      {o.label}
                                    </option>
                                  ))}
                                </select>
                              </FormGroup>
                            );
                          }
                          return (
                            <FormGroup key={field.name} label={field.label || field.name}>
                              <input
                                type={fieldType === 'password' ? 'password' : 'text'}
                                value={val}
                                onChange={(e) =>
                                  setIndexerExtraSettings((prev) => ({
                                    ...prev,
                                    [field.name]: e.target.value,
                                  }))
                                }
                                className="arr-input font-mono text-[12px]"
                              />
                            </FormGroup>
                          );
                        })}
                      </>
                    );
                  })()}

                  <FormGroup
                    label="URL"
                    htmlFor="indexer-url"
                    helpText="Indexer base URL or /api endpoint (e.g. https://api.indexer.com)."
                  >
                    <input
                      id="indexer-url"
                      name="baseUrl"
                      value={indexerForm.baseUrl}
                      onChange={(e) => setIndexerForm({ ...indexerForm, baseUrl: e.target.value })}
                      placeholder="https://api.indexer.com"
                      className="arr-input font-mono"
                      autoCorrect="off"
                      autoCapitalize="none"
                      spellCheck={false}
                      required
                    />
                  </FormGroup>

                  <FormGroup
                    label="API Key"
                    htmlFor="indexer-api-key"
                    helpText={
                      indexerForm.editingId !== null && indexerForm.hasSavedApiKey
                        ? 'An encrypted API key is already saved. Leave blank to keep the existing key.'
                        : undefined
                    }
                  >
                    <input
                      id="indexer-api-key"
                      name="apiKey"
                      type="password"
                      value={indexerForm.apiKey}
                      onChange={(e) => setIndexerForm({ ...indexerForm, apiKey: e.target.value })}
                      placeholder={
                        indexerForm.editingId !== null && indexerForm.hasSavedApiKey
                          ? '•••••••• (saved — leave blank to keep)'
                          : 'Indexer API key'
                      }
                      className="arr-input font-mono"
                      autoCorrect="off"
                      autoCapitalize="none"
                      spellCheck={false}
                    />
                  </FormGroup>

                  <FormGroup label="Search Capabilities">
                    <div className="space-y-2 pt-1 text-[13px]">
                      <label className="flex items-center gap-2 cursor-pointer">
                        <input
                          type="checkbox"
                          checked={indexerForm.rssEnabled}
                          onChange={(e) => setIndexerForm({ ...indexerForm, rssEnabled: e.target.checked })}
                          className="accent-arr-accent"
                        />
                        <span>Enable RSS</span>
                      </label>
                      <label className="flex items-center gap-2 cursor-pointer">
                        <input
                          type="checkbox"
                          checked={indexerForm.autoSearchEnabled}
                          onChange={(e) => setIndexerForm({ ...indexerForm, autoSearchEnabled: e.target.checked })}
                          className="accent-arr-accent"
                        />
                        <span>Enable Automatic Search</span>
                      </label>
                      <label className="flex items-center gap-2 cursor-pointer">
                        <input
                          type="checkbox"
                          checked={indexerForm.interactiveSearchEnabled}
                          onChange={(e) =>
                            setIndexerForm({ ...indexerForm, interactiveSearchEnabled: e.target.checked })
                          }
                          className="accent-arr-accent"
                        />
                        <span>Enable Interactive Search</span>
                      </label>
                    </div>
                  </FormGroup>

                  <FormGroup
                    label="Categories"
                    helpText="Select Newznab categories queried during Movie, Series, Anime, Music, and Book searches."
                  >
                    <div className="flex flex-wrap gap-1.5 pt-0.5">
                      {visibleCategories.map((cat) => {
                        const active = indexerForm.categories.includes(cat.id);
                        return (
                          <button
                            key={cat.id}
                            type="button"
                            aria-pressed={active}
                            onClick={() => toggleCategory(cat.id)}
                            className={`px-2 py-1 text-[11px] rounded border transition-colors cursor-pointer ${
                              active
                                ? 'bg-arr-primary border-arr-primary-border text-white font-semibold'
                                : 'bg-arr-card border-[#4a5158] text-[#b8b8b8] hover:text-white'
                            }`}
                          >
                            {cat.label}
                          </button>
                        );
                      })}
                    </div>
                    <div className="flex flex-wrap items-center gap-2 mt-2 pt-2 border-t border-[#333a42]">
                      <label htmlFor="custom-category-id" className="text-[11px] text-[#909293]">
                        Custom Category ID
                      </label>
                      <input
                        id="custom-category-id"
                        name="customCategoryId"
                        type="text"
                        inputMode="numeric"
                        pattern="[0-9]*"
                        autoComplete="off"
                        autoCorrect="off"
                        autoCapitalize="none"
                        spellCheck={false}
                        placeholder="e.g. 5170"
                        value={customCategoryInput}
                        onChange={(e) => setCustomCategoryInput(e.target.value)}
                        onKeyDown={(e) => {
                          if (e.key === 'Enter' && !e.nativeEvent.isComposing) {
                            e.preventDefault();
                            handleAddCustomCategory();
                          }
                        }}
                        className="arr-input !w-28 text-xs font-mono"
                      />
                      <button
                        type="button"
                        onClick={handleAddCustomCategory}
                        className="px-2.5 py-1 text-xs rounded border border-[#4a5158] bg-arr-card text-[#d1d5db] hover:text-white hover:border-[#6b7280] transition-colors cursor-pointer"
                      >
                        + Add Category
                      </button>
                    </div>
                  </FormGroup>

                  <FormGroup
                    label="Indexer Priority"
                    htmlFor="indexer-priority"
                    helpText="1 (highest priority) to 50 (lowest priority). Default is 25."
                  >
                    <input
                      id="indexer-priority"
                      name="priority"
                      type="number"
                      min={1}
                      max={50}
                      value={indexerForm.priority}
                      onChange={(e) => setIndexerForm({ ...indexerForm, priority: Number(e.target.value) || 25 })}
                      className="arr-input !w-28 font-mono"
                    />
                  </FormGroup>

                  <FormGroup
                    label="Request Delay (ms)"
                    htmlFor="indexer-request-delay"
                    helpText="Minimum delay between consecutive HTTP requests to this indexer (0 = no extra delay)."
                  >
                    <input
                      id="indexer-request-delay"
                      name="requestDelayMs"
                      type="number"
                      min={0}
                      max={60000}
                      value={indexerRequestDelayMs}
                      onChange={(e) => setIndexerRequestDelayMs(e.target.value)}
                      className="arr-input !w-28 font-mono"
                    />
                  </FormGroup>

                  <FormGroup
                    label="Tags"
                    htmlFor="indexer-tags"
                    helpText="Comma-separated tags (used to bind Indexer Proxies or Release Profiles)."
                  >
                    <input
                      id="indexer-tags"
                      name="tags"
                      value={indexerTags}
                      onChange={(e) => setIndexerTags(e.target.value)}
                      placeholder="e.g. cloudflare, usenet-primary"
                      className="arr-input font-mono text-[12px]"
                    />
                  </FormGroup>
                </form>
              </ArrModal>
            )}
          </>
        )}

        {section === 'downloaders' && canReadDownloaders && (
          <DownloadersSection
            canWriteDownloaders={canWriteDownloaders}
            downloaderBanner={downloaderBanner}
            clients={clientsQuery.data ?? []}
            drivers={driversQuery.data?.drivers ?? []}
            trackedDownloads={trackedQuery.data ?? []}
            isTrackedFetching={trackedQuery.isFetching}
            onRefetchTracked={() => void trackedQuery.refetch()}
            queueItems={queueQuery.data ?? []}
            imports={importsQuery.data ?? []}
            catalogWorks={catalogWorksQuery.data ?? []}
            onOpenEditClient={openEditClient}
            onOpenAddClient={() => {
              setEditingClientId(null);
              setSelectedDriver(null);
              setClientIsEnabled(true);
              setClientUrlBase('');
              setClientPriority('1');
              setClientRemoveCompleted(true);
              setClientRemoveFailed(true);
              setClientCategoryMappings({ ...(driversQuery.data?.defaultCategoryMappings ?? {}) });
              setClientWatchFolder('');
              setClientTestStatus(null);
              setShowAddClientPicker(true);
            }}
            onDeleteClient={(id) => deleteClientMutation.mutate(id)}
            isDeleteClientPending={deleteClientMutation.isPending}
            showAddClientPicker={showAddClientPicker}
            editingClientId={editingClientId}
            selectedDriver={selectedDriver}
            onSelectDriver={setSelectedDriver}
            onCloseClientModal={closeClientModal}
            clientName={clientName}
            onChangeClientName={setClientName}
            clientHost={clientHost}
            onChangeClientHost={setClientHost}
            clientPort={clientPort}
            onChangeClientPort={setClientPort}
            clientApiKey={clientApiKey}
            onChangeClientApiKey={setClientApiKey}
            clientUsername={clientUsername}
            onChangeClientUsername={setClientUsername}
            clientPassword={clientPassword}
            onChangeClientPassword={setClientPassword}
            clientUseSsl={clientUseSsl}
            onChangeClientUseSsl={setClientUseSsl}
            clientIsEnabled={clientIsEnabled}
            onChangeClientIsEnabled={setClientIsEnabled}
            clientUrlBase={clientUrlBase}
            onChangeClientUrlBase={setClientUrlBase}
            clientPriority={clientPriority}
            onChangeClientPriority={setClientPriority}
            clientRemoveCompleted={clientRemoveCompleted}
            onChangeClientRemoveCompleted={setClientRemoveCompleted}
            clientRemoveFailed={clientRemoveFailed}
            onChangeClientRemoveFailed={setClientRemoveFailed}
            clientCategoryMappings={clientCategoryMappings}
            onChangeClientCategoryMappings={setClientCategoryMappings}
            clientWatchFolder={clientWatchFolder}
            onChangeClientWatchFolder={setClientWatchFolder}
            onTestClient={() => testClientMutation.mutate()}
            isTestClientPending={testClientMutation.isPending}
            clientTestStatus={clientTestStatus}
            clientHasSavedKey={clientHasSavedKey}
            clientHasSavedPass={clientHasSavedPass}
            onSaveClient={() => {
              if (!selectedDriver) return;
              saveClientMutation.mutate({
                id: editingClientId,
                name: clientName.trim() || selectedDriver.displayName,
                implementation: selectedDriver.implementation,
                host: clientHost.trim() || 'localhost',
                port: Number(clientPort) || selectedDriver.defaultPort,
                useSsl: clientUseSsl,
                urlBase: clientUrlBase.trim() || undefined,
                apiKey: clientApiKey.trim() || undefined,
                username: clientUsername.trim() || undefined,
                password: clientPassword || undefined,
                priority: Math.max(1, Number(clientPriority) || 1),
                isEnabled: clientIsEnabled,
                removeCompleted: clientRemoveCompleted,
                removeFailed: clientRemoveFailed,
                categoryMappings: clientCategoryMappings,
                extraSettings: clientWatchFolder.trim()
                  ? { watchFolder: clientWatchFolder.trim() }
                  : undefined,
              });
            }}
            isSaveClientPending={saveClientMutation.isPending}
            selectedTrackedForImport={selectedTrackedForImport}
            onOpenManualImportModal={openManualImportModal}
            onCloseManualImportModal={() => setSelectedTrackedForImport(null)}
            onIgnoreTracked={(id) => ignoreTrackedMutation.mutate(id)}
            isIgnoreTrackedPending={ignoreTrackedMutation.isPending}
            ignoringTrackedId={ignoringTrackedId}
            overrideWorkId={overrideWorkId}
            onChangeOverrideWorkId={setOverrideWorkId}
            overrideSeason={overrideSeason}
            onChangeOverrideSeason={setOverrideSeason}
            overrideEpisodes={overrideEpisodes}
            onChangeOverrideEpisodes={setOverrideEpisodes}
            bypassUpgrade={bypassUpgrade}
            onChangeBypassUpgrade={setBypassUpgrade}
            confirmedAudioLangs={confirmedAudioLangs}
            onChangeConfirmedAudioLangs={setConfirmedAudioLangs}
            manualImportError={manualImportError}
            onExecuteManualImport={handleExecuteManualImport}
            isRetryImportPending={retryImportMutation.isPending}
            remotePaths={remotePathsQuery.data ?? []}
            onAddRemotePath={async (data) => {
              await createRemotePathMutation.mutateAsync(data);
            }}
            isAddRemotePathPending={createRemotePathMutation.isPending}
            onDeleteRemotePath={(id) => deleteRemotePathMutation.mutate(id)}
            isDeleteRemotePathPending={deleteRemotePathMutation.isPending}
          />
        )}

        {section === 'parser' && (
          <FieldSet legend="Release Parser & Naming Tester">
            <FormGroup
              label="Release Title"
              helpText="Enter any scene or P2P release name to inspect parsed quality, language, edition, and target hardlink path."
            >
              <input
                value={parseTitle}
                onChange={(e) => setParseTitle(e.target.value)}
                className="arr-input font-mono text-[13px]"
              />
            </FormGroup>

            {parsePreview.data && (
              <div className="mt-5 overflow-x-auto">
                <table className="arr-table">
                  <thead>
                    <tr>
                      <th>Vertical</th>
                      <th>Clean Title</th>
                      <th>Quality / HDR</th>
                      <th>Edition</th>
                      <th>Languages</th>
                      <th>Target Relative Path</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr>
                      <td>
                        <ArrLabel kind="primary">{parsePreview.data.parsed.mediaVertical.toUpperCase()}</ArrLabel>
                      </td>
                      <td className="font-semibold text-[#eeeeee]">
                        {parsePreview.data.parsed.cleanTitle}{' '}
                        {parsePreview.data.parsed.year ? `(${parsePreview.data.parsed.year})` : ''}
                      </td>
                      <td>
                        <ArrLabel kind="inverse">
                          {[
                            parsePreview.data.parsed.source,
                            parsePreview.data.parsed.resolution,
                            parsePreview.data.parsed.hdrFormat,
                          ]
                            .filter(Boolean)
                            .join(' ') || 'Standard'}
                        </ArrLabel>
                      </td>
                      <td>
                        {parsePreview.data.parsed.movieEdition ? (
                          <ArrLabel kind="purple">{parsePreview.data.parsed.movieEdition}</ArrLabel>
                        ) : (
                          '-'
                        )}
                      </td>
                      <td>
                        <div className="flex flex-wrap gap-1">
                          {parsePreview.data.parsed.detectedLanguages.map((l) => (
                            <ArrLabel key={l} kind="success">
                              {l}
                            </ArrLabel>
                          ))}
                          {parsePreview.data.parsed.isDualAudio && <ArrLabel kind="warning">DL</ArrLabel>}
                        </div>
                      </td>
                      <td className="font-mono text-[12px] text-arr-accent">{parsePreview.data.previewRelativePath}</td>
                    </tr>
                  </tbody>
                </table>
              </div>
            )}
          </FieldSet>
        )}
      </PageContentBody>
    </div>
  );
}
