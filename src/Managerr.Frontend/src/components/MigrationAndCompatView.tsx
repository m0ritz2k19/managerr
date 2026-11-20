import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Check,
  CheckCircle2,
  Copy,
  Eye,
  Film,
  FolderSync,
  KeyRound,
  Play,
  Plus,
  RefreshCw,
  Rocket,
  Server,
  Sparkles,
  Trash2,
  Tv,
} from 'lucide-react';
import { apiFetch, type CreatedApiKeyResult } from '../lib/api';
import {
  ArrAlert,
  ArrButton,
  ArrLabel,
  ArrModal,
  FieldSet,
  FormGroup,
  PageContentBody,
  PageToolbar,
  PageToolbarButton,
  PageToolbarSeparator,
} from './arr/ArrPrimitives';
import { PathBrowserInput } from './arr/PathBrowserModal';

interface MigrationSourceSummary {
  id: number;
  name: string;
  sourceKind: string;
  transportMode: string;
  fileOrUrl: string;
  readarrSlotHint: string;
  pathMappings: Record<string, string>;
  isEnabled: boolean;
}

interface MigrationReportSummary {
  runId: number | null;
  mode: string;
  upliftLanguageCustomFormats: boolean;
  startedAt: string;
  finishedAt: string;
  rawIndexerCount: number;
  deduplicatedIndexerCount: number;
  skippedProxyClonesCount: number;
  rawDownloadClientCount: number;
  consolidatedDownloadClientCount: number;
  tvSeriesCount: number;
  tvEpisodeFilesCount: number;
  moviesCount: number;
  movieFilesCount: number;
  musicArtistsCount: number;
  booksUnifiedCount: number;
  dualReadarrMergedBooksCount: number;
  bookFilesCount: number;
  overseerrUsersCorrelatedCount: number;
  overseerrRequestsCorrelatedCount: number;
  verifiedOnDiskFilesCount: number;
  bytesMovedOrModified: number;
  unmappedPaths: string[];
  warnings: string[];
  logs: string[];
  blocklistHashesImportedCount?: number;
  remotePathMappingsCount?: number;
}

interface ArrApiTokenProbeRequest {
  baseUrl: string;
  apiKey: string;
  sourceKind?: string;
  pathMappings?: Record<string, string>;
}

interface ArrApiTokenProbeResult {
  ok: boolean;
  detectedKind: string;
  appName: string;
  version: string;
  instanceName: string;
  normalizedBaseUrl: string;
  rootFolders: string[];
  catalogItemsCount: number;
  mediaFilesCount: number;
  indexersCount: number;
  downloadClientsCount: number;
  customFormatsCount: number;
  blocklistCount: number;
  message: string;
}

interface ArrApiTokenImportRequest {
  baseUrl: string;
  apiKey: string;
  sourceKind?: string;
  name?: string | null;
  readarrSlotHint?: string;
  pathMappings?: Record<string, string>;
  dryRun: boolean;
  saveAsSource: boolean;
  upliftLanguageCustomFormats: boolean;
}

interface ShadowModeDecisionSummary {
  id: number;
  occurredAt: string;
  mediaVertical: string;
  workTitle: string;
  releaseTitle: string;
  managerrDecision: string;
  managerrScore: number;
  legacyApp: string;
  legacyDecision: string;
  wouldUpgradeLanguage: boolean;
  notes: string;
}

interface GeneratedCompatKeyInfo {
  label: string;
  scope: string;
  mountUrl: string;
  rawApiKeyOnce: string;
  maskedHint: string;
}

interface CutoverExecutionResult {
  runId: number;
  executedAt: string;
  generatedCompatKeys: GeneratedCompatKeyInfo[];
  ecosystemInstructions: string[];
}

const HOMELAB_PATH_DEFAULTS = [
  { from: '/tv', to: '/media/series' },
  { from: '/anime', to: '/media/anime' },
  { from: '/movies', to: '/media/movies' },
  { from: '/animemovs', to: '/media/animefilme' },
];

export function MigrationAndCompatView({ canExecute }: { canExecute: boolean }) {
  const qc = useQueryClient();
  const [subTab, setSubTab] = useState<'wizard' | 'shadow' | 'shims'>('wizard');
  const [showAddSource, setShowAddSource] = useState(false);

  const [srcName, setSrcName] = useState('Sonarr Live');
  const [srcKind, setSrcKind] = useState('sonarr');
  const [srcTransport, setSrcTransport] = useState('live_api');
  const [srcPath, setSrcPath] = useState('http://sonarr:8989');
  const [srcApiKey, setSrcApiKey] = useState('');
  const [srcSlotHint, setSrcSlotHint] = useState('auto');
  const [upliftLangCfs, setUpliftLangCfs] = useState(true);
  const [pathMappingRows, setPathMappingRows] = useState<Array<{ from: string; to: string }>>([
    { from: '', to: '' },
  ]);
  const [sourcePathMappings, setSourcePathMappings] = useState<Array<{ from: string; to: string }>>([
    { from: '', to: '' },
  ]);

  // 1-Click *arr API Token Importer State
  const [arrUrl, setArrUrl] = useState('http://sonarr:8989');
  const [arrApiKey, setArrApiKey] = useState('');
  const [arrAppKind, setArrAppKind] = useState('auto');
  const [arrSaveAsSource, setArrSaveAsSource] = useState(true);
  const [showArrKey, setShowArrKey] = useState(false);
  const [probeResult, setProbeResult] = useState<ArrApiTokenProbeResult | null>(null);
  const [probeError, setProbeError] = useState<string | null>(null);
  const [arrImportFeedback, setArrImportFeedback] = useState<{ kind: 'info' | 'success' | 'danger'; text: string } | null>(null);

  const [latestReport, setLatestReport] = useState<MigrationReportSummary | null>(null);
  const [cutoverResult, setCutoverResult] = useState<CutoverExecutionResult | null>(null);
  const [generatedSeerrKey, setGeneratedSeerrKey] = useState<CreatedApiKeyResult | null>(null);
  const [copiedField, setCopiedField] = useState<string | null>(null);

  const [shadowWork, setShadowWork] = useState('Severance');
  const [shadowRelease, setShadowRelease] = useState('Severance.S01E01.German.DL.1080p.WEB-DL.DDP5.1.H.264-GRP');
  const [shadowExistingQual, setShadowExistingQual] = useState('WEBDL-2160p');

  const copyToClipboard = (text: string, fieldId: string) => {
    void navigator.clipboard.writeText(text);
    setCopiedField(fieldId);
    setTimeout(() => {
      setCopiedField((curr) => (curr === fieldId ? null : curr));
    }, 2000);
  };

  const detectedHost =
    typeof window !== 'undefined' && window.location.hostname ? window.location.hostname : 'localhost';
  const detectedPort =
    typeof window !== 'undefined' && window.location.port
      ? window.location.port
      : typeof window !== 'undefined' && window.location.protocol === 'https:'
        ? '443'
        : '8080';
  const isHttps = typeof window !== 'undefined' && window.location.protocol === 'https:';

  const generateSeerrKeyMutation = useMutation({
    mutationFn: () =>
      apiFetch<CreatedApiKeyResult>('/api/v1/auth/tokens', {
        method: 'POST',
        body: JSON.stringify({
          name: 'Seerr (Sonarr + Radarr)',
          scopes: ['compat:sonarr', 'compat:radarr'],
        }),
      }),
    onSuccess: (data) => {
      setGeneratedSeerrKey(data);
      qc.invalidateQueries({ queryKey: ['auth-tokens'] });
    },
  });

  const sourcesQuery = useQuery({
    queryKey: ['migration-sources'],
    queryFn: () => apiFetch<MigrationSourceSummary[]>('/api/v1/migration/sources'),
  });

  const compatSettingsQuery = useQuery({
    queryKey: ['compat-settings'],
    queryFn: () => apiFetch<{ expandSingleSeasonAnime: boolean }>('/api/v1/compat/settings'),
    enabled: subTab === 'shims',
  });

  const saveCompatSettingsMutation = useMutation({
    mutationFn: (expandSingleSeasonAnime: boolean) =>
      apiFetch<{ expandSingleSeasonAnime: boolean }>('/api/v1/compat/settings', {
        method: 'PUT',
        body: JSON.stringify({ expandSingleSeasonAnime }),
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['compat-settings'] });
    },
  });

  const shadowQuery = useQuery({
    queryKey: ['migration-shadow'],
    queryFn: () => apiFetch<ShadowModeDecisionSummary[]>('/api/v1/migration/shadow-decisions'),
    enabled: subTab === 'shadow',
  });

  const buildPathMappings = (
    rows: Array<{ from: string; to: string }> = pathMappingRows,
  ): Record<string, string> => {
    const map: Record<string, string> = {};
    for (const row of rows) {
      const from = row.from.trim();
      const to = row.to.trim();
      if (from && to) {
        map[from] = to;
      }
    }
    return map;
  };

  const resetAddSourceFields = () => {
    setSrcName('Sonarr Live');
    setSrcKind('sonarr');
    setSrcTransport('live_api');
    setSrcPath('http://sonarr:8989');
    setSrcApiKey('');
    setSrcSlotHint('auto');
    setSourcePathMappings([{ from: '', to: '' }]);
  };

  const addSourceMutation = useMutation({
    mutationFn: () =>
      apiFetch<MigrationSourceSummary>('/api/v1/migration/sources', {
        method: 'POST',
        body: JSON.stringify({
          name: srcName,
          sourceKind: srcKind,
          transportMode: srcTransport,
          fileOrUrl: srcPath,
          apiKey: srcApiKey || null,
          readarrSlotHint: srcSlotHint,
          pathMappings: buildPathMappings(sourcePathMappings),
          isEnabled: true,
        }),
      }),
    onSuccess: () => {
      setShowAddSource(false);
      resetAddSourceFields();
      qc.invalidateQueries({ queryKey: ['migration-sources'] });
    },
  });

  const deleteSourceMutation = useMutation({
    mutationFn: (id: number) => apiFetch<void>(`/api/v1/migration/sources/${id}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['migration-sources'] }),
  });

  const saveSourceMutation = useMutation({
    mutationFn: async () => {
      let currentProbe = probeResult;
      const normalizedCurrentUrl = arrUrl.trim().replace(/\/+$/, '').toLowerCase();
      const probeUrl = currentProbe?.normalizedBaseUrl?.trim().replace(/\/+$/, '').toLowerCase();
      const isProbeValid =
        Boolean(currentProbe && currentProbe.ok && currentProbe.detectedKind) &&
        (arrAppKind === 'auto' || currentProbe?.detectedKind === arrAppKind) &&
        Boolean(
          probeUrl &&
            (probeUrl === normalizedCurrentUrl ||
              normalizedCurrentUrl.includes(probeUrl) ||
              probeUrl.includes(normalizedCurrentUrl)),
        );

      if (!isProbeValid) {
        currentProbe = await apiFetch<ArrApiTokenProbeResult>('/api/v1/migration/arr-api/probe', {
          method: 'POST',
          body: JSON.stringify({
            baseUrl: arrUrl.trim(),
            apiKey: arrApiKey.trim(),
            sourceKind: arrAppKind,
            pathMappings: buildPathMappings(),
          } satisfies ArrApiTokenProbeRequest),
        });
        setProbeResult(currentProbe);
        setProbeError(null);
        if (!currentProbe.ok) {
          throw new Error(currentProbe.message || 'Probe failed before saving source');
        }
      }

      const hostname = (() => {
        try {
          const u = new URL(currentProbe?.normalizedBaseUrl || arrUrl.trim());
          return u.hostname || arrUrl.trim();
        } catch {
          return arrUrl.trim();
        }
      })();

      const resolvedKind = currentProbe?.detectedKind || (arrAppKind !== 'auto' ? arrAppKind : 'sonarr');
      const derivedName = currentProbe?.appName?.trim() || `${resolvedKind} (${hostname})`;
      const targetUrl = currentProbe?.normalizedBaseUrl || arrUrl.trim();

      await apiFetch<MigrationSourceSummary>('/api/v1/migration/sources', {
        method: 'POST',
        body: JSON.stringify({
          name: derivedName,
          sourceKind: resolvedKind,
          transportMode: 'live_api',
          fileOrUrl: targetUrl,
          apiKey: arrApiKey.trim(),
          readarrSlotHint: 'auto',
          pathMappings: buildPathMappings(),
          isEnabled: true,
        }),
      });

      return derivedName;
    },
    onSuccess: (savedName) => {
      qc.invalidateQueries({ queryKey: ['migration-sources'] });
      setArrImportFeedback({
        kind: 'success',
        text: `Saved '${savedName}' to Migration Sources — add Radarr/Sonarr and click '1. Dry-Run Diff' below to preview the combined cutover!`,
      });
    },
    onError: (err: unknown) => {
      setArrImportFeedback({
        kind: 'danger',
        text: err instanceof Error && err.message ? err.message : 'Failed to save source for unified plan.',
      });
    },
  });

  const probeMutation = useMutation({
    mutationFn: () =>
      apiFetch<ArrApiTokenProbeResult>('/api/v1/migration/arr-api/probe', {
        method: 'POST',
        body: JSON.stringify({
          baseUrl: arrUrl.trim(),
          apiKey: arrApiKey.trim(),
          sourceKind: arrAppKind,
          pathMappings: buildPathMappings(),
        } satisfies ArrApiTokenProbeRequest),
      }),
    onSuccess: (data) => {
      setProbeResult(data);
      setProbeError(null);
    },
    onError: (err: unknown) => {
      setProbeError(err instanceof Error && err.message ? err.message : 'Failed to connect to *arr API');
      setProbeResult(null);
    },
  });

  const arrImportMutation = useMutation({
    mutationFn: (dryRun: boolean) =>
      apiFetch<MigrationReportSummary>('/api/v1/migration/arr-api/import', {
        method: 'POST',
        body: JSON.stringify({
          baseUrl: arrUrl.trim(),
          apiKey: arrApiKey.trim(),
          sourceKind: arrAppKind,
          name: null,
          readarrSlotHint: arrAppKind === 'readarr' ? 'auto' : undefined,
          pathMappings: buildPathMappings(),
          dryRun,
          saveAsSource: arrSaveAsSource,
          upliftLanguageCustomFormats: upliftLangCfs,
        } satisfies ArrApiTokenImportRequest),
      }),
    onSuccess: (data, dryRun) => {
      setLatestReport(data);
      if (dryRun) {
        setArrImportFeedback({
          kind: 'info',
          text: 'Dry-run preview generated below. Review detected items and simulated actions before pulling live.',
        });
      } else {
        setArrImportFeedback({
          kind: 'success',
          text: 'Pull complete! All catalog items, media files, indexers, download clients, custom formats, and blocklist entries have been imported into Managerr.',
        });
        qc.invalidateQueries({ queryKey: ['migration-sources'] });
        qc.invalidateQueries({ queryKey: ['catalog-works'] });
        qc.invalidateQueries({ queryKey: ['indexers'] });
        qc.invalidateQueries({ queryKey: ['download-clients'] });
      }
    },
    onError: (err: unknown) => {
      setArrImportFeedback({
        kind: 'danger',
        text: err instanceof Error && err.message ? err.message : 'Failed to import from *arr live API.',
      });
    },
  });

  const rollbackMutation = useMutation({
    mutationFn: () =>
      apiFetch<{ message: string }>('/api/v1/migration/rollback', {
        method: 'POST',
        body: JSON.stringify({ removeSavedSources: true }),
      }),
    onSuccess: (res) => {
      setLatestReport(null);
      setArrImportFeedback({
        kind: 'info',
        text: res.message || 'Removed all migrated items from Managerr database (0 bytes modified on disk).',
      });
      qc.invalidateQueries({ queryKey: ['migration-sources'] });
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
      qc.invalidateQueries({ queryKey: ['libraries'] });
      qc.invalidateQueries({ queryKey: ['indexers'] });
      qc.invalidateQueries({ queryKey: ['download-clients'] });
    },
    onError: (err: unknown) => {
      setArrImportFeedback({
        kind: 'danger',
        text: err instanceof Error && err.message ? err.message : 'Failed to rollback migrated items.',
      });
    },
  });

  const planMutation = useMutation({
    mutationFn: () =>
      apiFetch<MigrationReportSummary>('/api/v1/migration/plan', {
        method: 'POST',
        body: JSON.stringify({
          upliftLanguageCustomFormats: upliftLangCfs,
          globalPathMappings: buildPathMappings(),
        }),
      }),
    onSuccess: (data) => setLatestReport(data),
  });

  const executeMutation = useMutation({
    mutationFn: () =>
      apiFetch<MigrationReportSummary>('/api/v1/migration/execute', {
        method: 'POST',
        body: JSON.stringify({
          upliftLanguageCustomFormats: upliftLangCfs,
          globalPathMappings: buildPathMappings(),
        }),
      }),
    onSuccess: (data) => {
      setLatestReport(data);
      qc.invalidateQueries();
    },
  });

  const shadowMutation = useMutation({
    mutationFn: () =>
      apiFetch<ShadowModeDecisionSummary>('/api/v1/migration/shadow-decisions/evaluate', {
        method: 'POST',
        body: JSON.stringify({
          mediaVertical: 'tv',
          workTitle: shadowWork,
          releaseTitle: shadowRelease,
          profileId: 1,
          legacyApp: 'sonarr',
          legacyDecision: 'Rejected (Quality Cutoff Already Met)',
          originalLanguage: 'en',
          existingFileQuality: shadowExistingQual,
          existingAudioLanguages: ['en'],
        }),
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['migration-shadow'] }),
  });

  const cutoverMutation = useMutation({
    mutationFn: () =>
      apiFetch<CutoverExecutionResult>('/api/v1/migration/cutover', {
        method: 'POST',
        body: JSON.stringify({
          runFinalDeltaSync: true,
          generateScopedCompatKeys: true,
          generateSeparateReadarrAudiobookKey: true,
          globalPathMappings: buildPathMappings(),
        }),
      }),
    onSuccess: (res) => setCutoverResult(res),
  });

  return (
    <div className="flex flex-col flex-1">
      <PageToolbar
        left={
          <>
            <PageToolbarButton
              icon={RefreshCw}
              label="Refresh"
              spinning={sourcesQuery.isFetching || shadowQuery.isFetching}
              onClick={() => {
                void sourcesQuery.refetch();
                if (subTab === 'shadow') void shadowQuery.refetch();
              }}
            />

            <PageToolbarSeparator />

            {subTab === 'wizard' && (
              <>
                {canExecute && (
                  <PageToolbarButton icon={Plus} label="Add Source" onClick={() => setShowAddSource(true)} />
                )}
                <PageToolbarButton
                  icon={Eye}
                  label="Dry-Run Diff"
                  spinning={planMutation.isPending}
                  onClick={() => planMutation.mutate()}
                />
                {canExecute && (
                  <PageToolbarButton
                    icon={Play}
                    label="Execute"
                    spinning={executeMutation.isPending}
                    onClick={() => executeMutation.mutate()}
                  />
                )}
              </>
            )}

            {subTab === 'shims' && canExecute && (
              <>
                <PageToolbarButton
                  icon={Rocket}
                  label="Cutover Keys"
                  spinning={cutoverMutation.isPending}
                  onClick={() => cutoverMutation.mutate()}
                />
                <PageToolbarButton
                  icon={KeyRound}
                  label="Seerr Key"
                  spinning={generateSeerrKeyMutation.isPending}
                  onClick={() => generateSeerrKeyMutation.mutate()}
                />
              </>
            )}
          </>
        }
        right={
          <>
            <PageToolbarButton
              icon={FolderSync}
              label="Import"
              active={subTab === 'wizard'}
              onClick={() => setSubTab('wizard')}
            />
            <PageToolbarButton
              icon={Eye}
              label="Shadow Mode"
              active={subTab === 'shadow'}
              onClick={() => setSubTab('shadow')}
            />
            <PageToolbarButton
              icon={Server}
              label="/compat Shims"
              active={subTab === 'shims'}
              onClick={() => setSubTab('shims')}
            />
          </>
        }
      />

      <PageContentBody>
        {subTab === 'wizard' && (
          <>
            <FieldSet legend="Direct *arr API Token Importer (Pull via API Key)">
              <div className="bg-[#1f1f1f] border border-arr-card-border rounded-[4px] p-5 mb-6">
                <div className="text-[13px] text-[#cccccc] mb-5 leading-relaxed">
                  <Sparkles className="h-4 w-4 text-arr-accent inline mr-1.5 -mt-0.5" />
                  Connect directly to a live running <strong>*arr</strong> instance (Sonarr, Radarr, Prowlarr, Lidarr, Readarr, or Overseerr)
                  using its HTTP base URL and API Key. Probe the instance to inspect discovered root folders, catalog works, media files, indexers,
                  download clients, custom formats, and blocklist, then preview or pull everything into Managerr in one click.
                  Catalog items and file records are imported as-is without starting indexer searches or automatic disk/language scans — use "Scan & Check Languages" inside any series or via Mass Editor multi-select whenever you want to verify files on disk.
                </div>

                <FormGroup
                  label="*arr URL"
                  helpText="Base HTTP URL of your running *arr instance (e.g. http://sonarr:8989, http://radarr:7878, http://prowlarr:9696, or http://192.168.1.10:8989). Only this address is contacted."
                >
                  <input
                    value={arrUrl}
                    onChange={(e) => {
                      setArrUrl(e.target.value);
                      setProbeResult(null);
                      setProbeError(null);
                    }}
                    placeholder="http://sonarr:8989"
                    className="arr-input font-mono text-[13px]"
                  />
                </FormGroup>

                <FormGroup
                  label="*arr API Token / Key"
                  helpText="Found in *arr → Settings → General → Security (X-Api-Key)."
                >
                  <div className="relative flex items-center">
                    <input
                      type={showArrKey ? 'text' : 'password'}
                      value={arrApiKey}
                      onChange={(e) => {
                        setArrApiKey(e.target.value);
                        setProbeResult(null);
                        setProbeError(null);
                      }}
                      placeholder="e.g. 4d7a8e2b1c3f4e5a6b7c8d9e0f1a2b3c"
                      className="arr-input font-mono text-[13px] pr-10"
                    />
                    <button
                      type="button"
                      onClick={() => setShowArrKey((v) => !v)}
                      className="absolute right-2.5 text-[#909293] hover:text-[#e1e2e3] cursor-pointer p-1"
                      title={showArrKey ? 'Hide API Key' : 'Show API Key'}
                    >
                      <Eye className="h-4 w-4" />
                    </button>
                  </div>
                </FormGroup>

                <FormGroup
                  label="Application Type"
                  helpText="Auto-detection uses /system/status to identify the exact application and version."
                >
                  <select
                    value={arrAppKind}
                    onChange={(e) => {
                      setArrAppKind(e.target.value);
                      setProbeResult(null);
                      setProbeError(null);
                    }}
                    className="arr-input"
                  >
                    <option value="auto">Auto-Detect via /system/status (Recommended)</option>
                    <option value="sonarr">Sonarr</option>
                    <option value="radarr">Radarr</option>
                    <option value="prowlarr">Prowlarr</option>
                    <option value="lidarr">Lidarr</option>
                    <option value="readarr">Readarr</option>
                    <option value="overseerr">Overseerr / Jellyseerr / Seerr</option>
                  </select>
                </FormGroup>

                <FormGroup label="Migration Registry">
                  <label className="inline-flex items-center gap-2 pt-1.5 text-[13px] cursor-pointer">
                    <input
                      type="checkbox"
                      checked={arrSaveAsSource}
                      onChange={(e) => setArrSaveAsSource(e.target.checked)}
                    />
                    Save Source in Migration Registry
                  </label>
                </FormGroup>

                <div className="grid grid-cols-1 md:grid-cols-[220px_minmax(0,440px)] items-start gap-x-5 gap-y-1 mb-2">
                  <div />
                  <div className="flex flex-wrap items-center gap-2.5 pt-2">
                    <ArrButton
                      variant="default"
                      busy={probeMutation.isPending}
                      disabled={!arrUrl.trim() || !arrApiKey.trim()}
                      onClick={() => probeMutation.mutate()}
                      title="Test connection and discover instance capabilities"
                    >
                      <Sparkles className="h-3.5 w-3.5 text-arr-accent" /> Test &amp; Probe Token
                    </ArrButton>

                    <ArrButton
                      variant="default"
                      busy={arrImportMutation.isPending && arrImportMutation.variables === true}
                      disabled={!arrUrl.trim() || !arrApiKey.trim()}
                      onClick={() => arrImportMutation.mutate(true)}
                      title="Simulate pull without modifying database or disk"
                    >
                      <Eye className="h-3.5 w-3.5 text-[#ffa500]" /> Preview Pull (Dry Run)
                    </ArrButton>

                    <ArrButton
                      variant="default"
                      busy={saveSourceMutation.isPending}
                      disabled={!canExecute || !arrUrl.trim() || !arrApiKey.trim()}
                      onClick={() => saveSourceMutation.mutate()}
                      title="Save configured instance as a Migration Source for unified multi-source planning and cutover"
                    >
                      <Plus className="h-3.5 w-3.5 text-[#27c24c]" /> Save Source for Unified Plan
                    </ArrButton>

                    <ArrButton
                      variant="primary"
                      busy={arrImportMutation.isPending && arrImportMutation.variables === false}
                      disabled={!canExecute || !arrUrl.trim() || !arrApiKey.trim()}
                      onClick={() => arrImportMutation.mutate(false)}
                      title={
                        !canExecute
                          ? 'Execution disabled (read-only mode)'
                          : 'Pull all series, movies, artists, books, media files, indexers, download clients, custom formats, and blocklist into Managerr'
                      }
                    >
                      <Play className="h-3.5 w-3.5" /> Pull Everything In Now
                    </ArrButton>

                    <ArrButton
                      variant="danger"
                      busy={rollbackMutation.isPending}
                      disabled={!canExecute}
                      onClick={() => rollbackMutation.mutate()}
                      title="Remove migrated *arr catalog items, libraries, indexers, download clients, custom formats, and blocklist entries from Managerr database (keeps all files on disk untouched)"
                    >
                      <Trash2 className="h-3.5 w-3.5" /> Remove Migrated Items
                    </ArrButton>
                  </div>
                </div>

                {probeResult && probeResult.ok && (
                  <div className="bg-[#1b261e] border border-[#27c24c]/40 rounded-[4px] p-4 mt-5 space-y-3">
                    <div className="flex flex-wrap items-center justify-between gap-2">
                      <div className="flex items-center gap-2.5">
                        <CheckCircle2 className="h-5 w-5 text-[#27c24c] shrink-0" />
                        <div>
                          <div className="font-bold text-[14px] text-[#eeeeee]">
                            <span className="text-[#27c24c]">{probeResult.appName || 'Arr Instance'}</span>
                            {probeResult.version ? ` v${probeResult.version}` : ''}
                            {probeResult.instanceName ? ` (${probeResult.instanceName})` : ''}
                            <span className="ml-2 text-[12px] font-normal text-[#27c24c]">— Connection Verified</span>
                          </div>
                          <div className="text-[12px] text-[#909293] font-mono">
                            {probeResult.normalizedBaseUrl || arrUrl}
                            {probeResult.detectedKind && (
                              <span className="ml-2 text-[#cccccc] font-sans">
                                [detected: <strong className="text-arr-accent">{probeResult.detectedKind}</strong>]
                              </span>
                            )}
                          </div>
                        </div>
                      </div>
                      {probeResult.message && (
                        <span className="text-[12px] text-[#cccccc] italic bg-[#111111] px-2.5 py-1 rounded border border-arr-card-border">
                          {probeResult.message}
                        </span>
                      )}
                    </div>

                    {probeResult.rootFolders && probeResult.rootFolders.length > 0 && (
                      <div className="text-[12px] flex flex-wrap items-center gap-1.5 pt-2 border-t border-[#27c24c]/20">
                        <span className="text-[#909293] font-semibold">Discovered Root Folders:</span>
                        {probeResult.rootFolders.map((rf) => (
                          <code key={rf} className="bg-[#111111] text-arr-accent px-2 py-0.5 rounded text-[11px] font-mono border border-arr-card-border">
                            {rf}
                          </code>
                        ))}
                      </div>
                    )}

                    <div className="flex flex-wrap items-center gap-2 pt-2 border-t border-[#27c24c]/20">
                      <ArrLabel kind="primary">
                        Catalog Items: {probeResult.catalogItemsCount.toLocaleString()}
                      </ArrLabel>
                      <ArrLabel kind="usenet">
                        Media Files: {probeResult.mediaFilesCount.toLocaleString()}
                      </ArrLabel>
                      <ArrLabel kind="torrent">
                        Indexers: {probeResult.indexersCount.toLocaleString()}
                      </ArrLabel>
                      <ArrLabel kind="warning">
                        Download Clients: {probeResult.downloadClientsCount.toLocaleString()}
                      </ArrLabel>
                      <ArrLabel kind="purple">
                        Custom Formats: {probeResult.customFormatsCount.toLocaleString()}
                      </ArrLabel>
                      <ArrLabel kind="danger">
                        Blocklist: {probeResult.blocklistCount.toLocaleString()}
                      </ArrLabel>
                    </div>
                  </div>
                )}

                {probeResult && !probeResult.ok && (
                  <ArrAlert kind="danger" className="mt-4">
                    <strong>Probe Failed:</strong> {probeResult.message || 'Target instance returned an error during connection probe.'}
                  </ArrAlert>
                )}

                {probeError && (
                  <ArrAlert kind="danger" className="mt-4">
                    <strong>Connection Error:</strong> {probeError}
                  </ArrAlert>
                )}

                {arrImportFeedback && (
                  <ArrAlert kind={arrImportFeedback.kind} className="mt-4">
                    {arrImportFeedback.text}
                  </ArrAlert>
                )}
              </div>
            </FieldSet>

            <FieldSet legend="Legacy *arr / Overseerr Sources">
              {(sourcesQuery.data ?? []).length === 0 ? (
                <div className="text-[13px] text-[#909293]">
                  No migration sources registered. Click &ldquo;Add Source&rdquo; in the toolbar to connect a backup{' '}
                  <code>.db</code> / <code>.zip</code> or live API.
                </div>
              ) : (
                <div className="overflow-x-auto">
                  <table className="arr-table">
                    <thead>
                      <tr>
                        <th>Name</th>
                        <th>Application</th>
                        <th>Transport</th>
                        <th>Path / URL</th>
                        {canExecute && <th className="w-16 text-right">Remove</th>}
                      </tr>
                    </thead>
                    <tbody>
                      {(sourcesQuery.data ?? []).map((s) => (
                        <tr key={s.id}>
                          <td className="font-semibold text-[#eeeeee]">{s.name}</td>
                          <td>
                            <ArrLabel kind="primary">{s.sourceKind}</ArrLabel>
                          </td>
                          <td>{s.transportMode}</td>
                          <td className="font-mono text-[12px] text-[#909293]">
                            <div>{s.fileOrUrl}</div>
                            {s.pathMappings && Object.keys(s.pathMappings).length > 0 && (
                              <div className="flex flex-wrap gap-1 mt-1 font-sans text-[11px] text-[#cccccc]">
                                {Object.entries(s.pathMappings).map(([from, to]) => (
                                  <span
                                    key={from}
                                    className="bg-[#111111] px-1.5 py-0.5 rounded border border-arr-card-border font-mono text-[10px]"
                                  >
                                    {from} → {to}
                                  </span>
                                ))}
                              </div>
                            )}
                          </td>
                          {canExecute && (
                            <td className="text-right">
                              <button
                                type="button"
                                onClick={() => deleteSourceMutation.mutate(s.id)}
                                className="p-1 text-[#f05050] hover:text-white cursor-pointer"
                              >
                                <Trash2 className="h-4 w-4" />
                              </button>
                            </td>
                          )}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </FieldSet>

            <FieldSet legend="Path Mapping & Profile Uplift">
              <FormGroup
                label="Path Mappings"
                helpText="Map legacy container root folder paths (e.g. /tv, /anime, /movies, /animemovs) to Managerr mounts (/media/*). Note: Managerr also auto-detects /media/* mounts when left empty."
              >
                <div className="space-y-2">
                  <div className="flex flex-wrap items-center gap-2 mb-2">
                    <button
                      type="button"
                      onClick={() => setPathMappingRows(HOMELAB_PATH_DEFAULTS.map((r) => ({ ...r })))}
                      className="text-[12px] px-2.5 py-1 rounded bg-[#2a2a2a] hover:bg-[#333333] text-arr-accent border border-arr-card-border cursor-pointer transition-colors"
                    >
                      Fill Homelab Defaults (/tv, /anime, /movies, /animemovs → /media/*)
                    </button>
                    <button
                      type="button"
                      onClick={() => setPathMappingRows((prev) => [...prev, { from: '', to: '' }])}
                      className="text-[12px] px-2.5 py-1 rounded bg-[#2a2a2a] hover:bg-[#333333] text-[#eeeeee] border border-arr-card-border cursor-pointer inline-flex items-center gap-1 transition-colors"
                    >
                      <Plus className="h-3.5 w-3.5" /> + Add Mapping
                    </button>
                  </div>

                  {pathMappingRows.map((row, idx) => (
                    <div key={idx} className="flex items-center gap-2">
                      <input
                        value={row.from}
                        onChange={(e) => {
                          const val = e.target.value;
                          setPathMappingRows((prev) =>
                            prev.map((r, i) => (i === idx ? { ...r, from: val } : r)),
                          );
                        }}
                        placeholder="/tv"
                        className="arr-input font-mono text-[13px] flex-1"
                        aria-label={`Remote Path Prefix ${idx + 1}`}
                      />
                      <span className="text-[#909293] font-mono text-[13px]">→</span>
                      <PathBrowserInput
                        value={row.to}
                        onChange={(val) => {
                          setPathMappingRows((prev) =>
                            prev.map((r, i) => (i === idx ? { ...r, to: val } : r)),
                          );
                        }}
                        placeholder="/media/series"
                        className="text-[13px]"
                        label={`Local Path Prefix ${idx + 1}`}
                        modalTitle="Select Local Managerr Media Folder"
                        allowCreateFolder
                        disabled={!canExecute}
                      />
                      {pathMappingRows.length > 1 && (
                        <button
                          type="button"
                          onClick={() => setPathMappingRows((prev) => prev.filter((_, i) => i !== idx))}
                          className="p-1.5 text-[#f05050] hover:text-white hover:bg-[#f05050]/20 rounded cursor-pointer shrink-0 transition-colors"
                          title="Remove Mapping"
                          aria-label={`Remove Mapping ${idx + 1}`}
                        >
                          <Trash2 className="h-4 w-4" />
                        </button>
                      )}
                    </div>
                  ))}
                </div>
              </FormGroup>

              <FormGroup label="Language Custom Formats">
                <label className="inline-flex items-center gap-2 pt-1.5 text-[13px] cursor-pointer">
                  <input
                    type="checkbox"
                    checked={upliftLangCfs}
                    onChange={(e) => setUpliftLangCfs(e.target.checked)}
                  />
                  Uplift legacy language Custom Formats into native Language Upgrade Policies
                </label>
              </FormGroup>
            </FieldSet>

            {latestReport && (
              <FieldSet legend={`Migration Summary (${latestReport.mode === 'execute' ? 'Executed' : 'Dry-Run'})`}>
                <ArrAlert kind="success" className="mb-4">
                  Disk Modification: <strong>{latestReport.bytesMovedOrModified} B</strong> (Hardlinks Intact)
                </ArrAlert>
                <div className="overflow-x-auto">
                  <table className="arr-table">
                    <thead>
                      <tr>
                        <th>Indexers (Deduped / Skipped Clones)</th>
                        <th>Download Clients</th>
                        <th>TV Series / Movies</th>
                        <th>Unified Books (Dual-Merged)</th>
                        {latestReport.blocklistHashesImportedCount !== undefined && <th>Blocklist Entries</th>}
                      </tr>
                    </thead>
                    <tbody>
                      <tr>
                        <td>
                          {latestReport.deduplicatedIndexerCount} ({latestReport.skippedProxyClonesCount} skipped)
                        </td>
                        <td>
                          {latestReport.consolidatedDownloadClientCount} (from {latestReport.rawDownloadClientCount})
                        </td>
                        <td>
                          {latestReport.tvSeriesCount} TV / {latestReport.moviesCount} Movies
                        </td>
                        <td>
                          {latestReport.booksUnifiedCount} ({latestReport.dualReadarrMergedBooksCount} merged)
                        </td>
                        {latestReport.blocklistHashesImportedCount !== undefined && (
                          <td>{latestReport.blocklistHashesImportedCount}</td>
                        )}
                      </tr>
                    </tbody>
                  </table>
                </div>
              </FieldSet>
            )}

            {showAddSource && (
              <ArrModal
                title="Add Migration Source"
                onClose={() => {
                  setShowAddSource(false);
                  resetAddSourceFields();
                }}
                maxWidth="max-w-xl"
                footer={
                  <>
                    <ArrButton
                      onClick={() => {
                        setShowAddSource(false);
                        resetAddSourceFields();
                      }}
                    >
                      Cancel
                    </ArrButton>
                    <ArrButton
                      variant="primary"
                      busy={addSourceMutation.isPending}
                      onClick={() => addSourceMutation.mutate()}
                    >
                      Save
                    </ArrButton>
                  </>
                }
              >
                <FormGroup label="Name">
                  <input value={srcName} onChange={(e) => setSrcName(e.target.value)} className="arr-input" />
                </FormGroup>
                <FormGroup label="Application">
                  <select value={srcKind} onChange={(e) => setSrcKind(e.target.value)} className="arr-input">
                    <option value="sonarr">Sonarr</option>
                    <option value="radarr">Radarr</option>
                    <option value="prowlarr">Prowlarr</option>
                    <option value="lidarr">Lidarr</option>
                    <option value="readarr">Readarr</option>
                    <option value="overseerr">Overseerr / Jellyseerr</option>
                    <option value="nzbhydra2">NZBHydra2</option>
                  </select>
                </FormGroup>
                <FormGroup label="Transport">
                  <select value={srcTransport} onChange={(e) => setSrcTransport(e.target.value)} className="arr-input">
                    <option value="live_api">Live *arr API Token (HTTP)</option>
                    <option value="sqlite_file">SQLite / Backup Archive (.db / .zip)</option>
                  </select>
                </FormGroup>
                <FormGroup
                  label={srcTransport === 'live_api' ? '*arr Base URL' : 'File or Database Path'}
                  helpText={
                    srcTransport === 'live_api'
                      ? 'Base HTTP URL of your running *arr instance (e.g. http://sonarr:8989)'
                      : 'File system path to backup archive (.zip) or SQLite database (.db)'
                  }
                >
                  {srcTransport === 'live_api' ? (
                    <input
                      value={srcPath}
                      onChange={(e) => setSrcPath(e.target.value)}
                      placeholder="http://sonarr:8989"
                      className="arr-input font-mono text-[12px]"
                      aria-label="*arr Base URL"
                    />
                  ) : (
                    <PathBrowserInput
                      value={srcPath}
                      onChange={setSrcPath}
                      placeholder="/backups/sonarr.db"
                      label="File or Database Path"
                      modalTitle="Select *arr SQLite DB or Backup .zip"
                      includeFiles
                      allowFileSelection
                      allowCreateFolder={false}
                      className="text-[12px]"
                    />
                  )}
                </FormGroup>
                {srcTransport === 'live_api' ? (
                  <FormGroup
                    label="*arr API Token (X-Api-Key)"
                    helpText="API key from Settings → General → Security in your *arr instance."
                  >
                    <input
                      type="password"
                      value={srcApiKey}
                      onChange={(e) => setSrcApiKey(e.target.value)}
                      placeholder="e.g. 4d7a8e2b1c3f4e5a6b7c8d9e0f1a2b3c"
                      className="arr-input font-mono"
                    />
                  </FormGroup>
                ) : (
                  <FormGroup label="API Key (Optional)">
                    <input
                      type="password"
                      value={srcApiKey}
                      onChange={(e) => setSrcApiKey(e.target.value)}
                      placeholder="Optional for offline .db"
                      className="arr-input font-mono"
                    />
                  </FormGroup>
                )}
                {srcKind === 'readarr' && (
                  <FormGroup label="Readarr Slot">
                    <select value={srcSlotHint} onChange={(e) => setSrcSlotHint(e.target.value)} className="arr-input">
                      <option value="auto">Auto-detect (.epub vs .m4b)</option>
                      <option value="ebook">Ebook Slot</option>
                      <option value="audiobook">Audiobook Slot</option>
                    </select>
                  </FormGroup>
                )}

                <FormGroup
                  label="Path Mappings"
                  helpText="Optional per-source path overrides (e.g. /tv → /media/series). Managerr auto-detects /media/* mounts when left empty."
                >
                  <div className="space-y-2">
                    <div className="flex flex-wrap items-center gap-2 mb-2">
                      <button
                        type="button"
                        onClick={() =>
                          setSourcePathMappings(HOMELAB_PATH_DEFAULTS.map((r) => ({ ...r })))
                        }
                        className="text-[11px] px-2 py-0.5 rounded bg-[#2a2a2a] hover:bg-[#333333] text-arr-accent border border-arr-card-border cursor-pointer transition-colors"
                      >
                        Fill Homelab Defaults
                      </button>
                      <button
                        type="button"
                        onClick={() => setSourcePathMappings((prev) => [...prev, { from: '', to: '' }])}
                        className="text-[11px] px-2 py-0.5 rounded bg-[#2a2a2a] hover:bg-[#333333] text-[#eeeeee] border border-arr-card-border cursor-pointer inline-flex items-center gap-1 transition-colors"
                      >
                        <Plus className="h-3 w-3" /> + Add Mapping
                      </button>
                    </div>
                    {sourcePathMappings.map((row, idx) => (
                      <div key={idx} className="flex items-center gap-2">
                        <input
                          value={row.from}
                          onChange={(e) => {
                            const val = e.target.value;
                            setSourcePathMappings((prev) =>
                              prev.map((r, i) => (i === idx ? { ...r, from: val } : r)),
                            );
                          }}
                          placeholder="/tv"
                          className="arr-input font-mono text-[12px] flex-1"
                          aria-label={`Source Path Mapping From ${idx + 1}`}
                        />
                        <span className="text-[#909293] text-[12px] font-mono">→</span>
                        <PathBrowserInput
                          value={row.to}
                          onChange={(val) => {
                            setSourcePathMappings((prev) =>
                              prev.map((r, i) => (i === idx ? { ...r, to: val } : r)),
                            );
                          }}
                          placeholder="/media/series"
                          className="text-[12px]"
                          label={`Source Path Mapping To ${idx + 1}`}
                          modalTitle="Select Local Managerr Media Folder"
                          allowCreateFolder
                          disabled={!canExecute}
                        />
                        {sourcePathMappings.length > 1 && (
                          <button
                            type="button"
                            onClick={() =>
                              setSourcePathMappings((prev) => prev.filter((_, i) => i !== idx))
                            }
                            className="p-1 text-[#f05050] hover:text-white cursor-pointer shrink-0"
                            title="Remove Mapping"
                            aria-label={`Remove Source Mapping ${idx + 1}`}
                          >
                            <Trash2 className="h-3.5 w-3.5" />
                          </button>
                        )}
                      </div>
                    ))}
                  </div>
                </FormGroup>
              </ArrModal>
            )}
          </>
        )}

        {subTab === 'shadow' && (
          <FieldSet legend="Shadow Mode Release Comparator">
            {canExecute && (
              <>
                <FormGroup label="Series / Movie Title">
                  <input value={shadowWork} onChange={(e) => setShadowWork(e.target.value)} className="arr-input" />
                </FormGroup>
                <FormGroup label="RSS Release Title">
                  <input
                    value={shadowRelease}
                    onChange={(e) => setShadowRelease(e.target.value)}
                    className="arr-input font-mono text-[12px]"
                  />
                </FormGroup>
                <FormGroup label="Existing Disk Quality">
                  <div className="flex gap-2">
                    <input
                      value={shadowExistingQual}
                      onChange={(e) => setShadowExistingQual(e.target.value)}
                      className="arr-input"
                    />
                    <ArrButton
                      variant="primary"
                      busy={shadowMutation.isPending}
                      onClick={() => shadowMutation.mutate()}
                    >
                      Compare
                    </ArrButton>
                  </div>
                </FormGroup>
              </>
            )}

            <div className="mt-4 overflow-x-auto">
              <table className="arr-table">
                <thead>
                  <tr>
                    <th>Work</th>
                    <th>Release</th>
                    <th>Legacy Decision</th>
                    <th>Managerr Decision</th>
                    <th>Notes</th>
                  </tr>
                </thead>
                <tbody>
                  {(shadowQuery.data ?? []).map((d) => (
                    <tr key={d.id}>
                      <td className="font-semibold text-[#eeeeee]">{d.workTitle}</td>
                      <td className="font-mono text-[12px] text-arr-accent">{d.releaseTitle}</td>
                      <td>
                        <ArrLabel kind="default">
                          {d.legacyApp}: {d.legacyDecision}
                        </ArrLabel>
                      </td>
                      <td>
                        <ArrLabel kind={d.wouldUpgradeLanguage ? 'success' : 'primary'}>
                          {d.managerrDecision}
                        </ArrLabel>
                      </td>
                      <td className="text-[12px] text-[#909293]">{d.notes}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </FieldSet>
        )}

        {subTab === 'shims' && (
          <>
            {cutoverResult && (
              <ArrAlert kind="success" className="mb-5">
                <div className="font-bold mb-2 flex items-center gap-1.5">
                  <KeyRound className="h-4 w-4" /> Generated Compatibility API Keys (Shown Once):
                </div>
                <div className="space-y-1.5">
                  {cutoverResult.generatedCompatKeys.map((k) => (
                    <div
                      key={k.rawApiKeyOnce}
                      className="flex flex-wrap items-center justify-between gap-2 bg-[#191919] px-3 py-1.5 rounded-[3px]"
                    >
                      <span className="text-[12px]">
                        <strong>{k.label}</strong> (<code className="text-[#909293]">{k.mountUrl}</code>)
                      </span>
                      <code className="font-mono text-[12px] text-arr-accent select-all">{k.rawApiKeyOnce}</code>
                    </div>
                  ))}
                </div>
              </ArrAlert>
            )}

            <FieldSet
              legend="Seerr / Jellyseerr / Overseerr Integration"
              action={
                <ArrButton
                  variant="primary"
                  busy={generateSeerrKeyMutation.isPending}
                  onClick={() => generateSeerrKeyMutation.mutate()}
                >
                  <KeyRound className="h-3.5 w-3.5" /> Generate Seerr API Key
                </ArrButton>
              }
            >
              <div className="bg-[#1f1f1f] border border-arr-card-border rounded-[4px] p-5 mb-6 space-y-4">
                <div className="flex flex-col md:flex-row md:items-center justify-between gap-3 text-[13px] text-[#cccccc]">
                  <p className="leading-relaxed">
                    <Sparkles className="h-4 w-4 text-arr-accent inline mr-1.5 -mt-0.5" />
                    Connect <strong>Overseerr</strong>, <strong>Jellyseerr</strong>, or <strong>Seerr</strong> directly to Managerr
                    by adding two services: one for <span className="text-arr-accent font-semibold">Radarr (Movies)</span> and one for{' '}
                    <span className="text-arr-accent font-semibold">Sonarr (TV Series)</span>. Managerr handles all search, quality profile,
                    and download workflow orchestration transparently through its compatibility shims.
                  </p>
                </div>

                {generatedSeerrKey && (
                  <ArrAlert kind="success" className="border-[#27c24c]/40 bg-[#17231c]">
                    <div className="flex flex-wrap items-center justify-between gap-2 mb-2">
                      <div className="font-bold flex items-center gap-1.5 text-[#27c24c]">
                        <CheckCircle2 className="h-4 w-4" /> Dual-Scoped API Key Generated (Shown Once):
                      </div>
                      <button
                        type="button"
                        onClick={() => copyToClipboard(generatedSeerrKey.rawTokenOnce, 'seerr-token')}
                        className="inline-flex items-center gap-1.5 px-2.5 py-1 text-[12px] bg-[#27c24c] hover:bg-[#24b145] text-white rounded font-medium cursor-pointer transition-colors"
                      >
                        {copiedField === 'seerr-token' ? <Check className="h-3.5 w-3.5" /> : <Copy className="h-3.5 w-3.5" />}
                        {copiedField === 'seerr-token' ? 'Copied Key!' : 'Copy Key'}
                      </button>
                    </div>
                    <code className="block bg-[#111111] px-3 py-2 rounded-[3px] font-mono text-[13px] text-arr-accent select-all break-all border border-[#26be4a]/20">
                      {generatedSeerrKey.rawTokenOnce}
                    </code>
                    <div className="mt-2 text-[11px] text-[#909293] flex flex-wrap items-center gap-x-3 gap-y-1">
                      <span>Name: <strong className="text-[#cccccc]">{generatedSeerrKey.name}</strong></span>
                      <span>•</span>
                      <span>Scopes: <code className="text-[#cccccc]">{generatedSeerrKey.scopes.join(', ')}</code></span>
                      <span>•</span>
                      <span>Public Prefix: <code className="text-[#cccccc]">{generatedSeerrKey.publicPrefix}</code></span>
                      <span>•</span>
                      <span>Use this same key in both Radarr &amp; Sonarr server configurations below.</span>
                    </div>
                  </ArrAlert>
                )}

                <div className="grid grid-cols-1 lg:grid-cols-2 gap-5 pt-1">
                  {/* Radarr Server in Seerr */}
                  <div className="bg-arr-card border border-arr-card-border rounded-[4px] p-4 flex flex-col justify-between">
                    <div>
                      <div className="flex items-center justify-between pb-2.5 mb-3 border-b border-arr-card-border">
                        <div className="flex items-center gap-2">
                          <Film className="h-4 w-4 text-[#ffc230]" />
                          <h4 className="font-semibold text-[#eeeeee] text-[14px]">Radarr Server in Seerr</h4>
                        </div>
                        <ArrLabel kind="primary">Movies</ArrLabel>
                      </div>
                      <p className="text-[11px] text-[#909293] mb-3">
                        In Seerr: <code>Settings → Services → Radarr → Add Radarr Server</code>
                      </p>

                      <div className="space-y-2 text-[12px]">
                        <div className="flex items-center justify-between py-1 border-b border-[#2d2d2d]">
                          <span className="text-[#909293]">Server Name</span>
                          <span className="font-mono text-[#eeeeee]">Managerr (Radarr Movies)</span>
                        </div>
                        <div className="flex items-center justify-between py-1 border-b border-[#2d2d2d]">
                          <span className="text-[#909293]">Hostname / IP</span>
                          <div className="flex items-center gap-1.5">
                            <span className="font-mono text-[#eeeeee]">{detectedHost}</span>
                            <button
                              type="button"
                              onClick={() => copyToClipboard(detectedHost, 'radarr-host')}
                              className="p-1 hover:text-arr-accent text-[#808080] cursor-pointer"
                              title="Copy Hostname"
                            >
                              {copiedField === 'radarr-host' ? <Check className="h-3 w-3 text-[#27c24c]" /> : <Copy className="h-3 w-3" />}
                            </button>
                          </div>
                        </div>
                        <div className="flex items-center justify-between py-1 border-b border-[#2d2d2d]">
                          <span className="text-[#909293]">Port</span>
                          <div className="flex items-center gap-1.5">
                            <span className="font-mono text-[#eeeeee]">{detectedPort}</span>
                            <button
                              type="button"
                              onClick={() => copyToClipboard(detectedPort, 'radarr-port')}
                              className="p-1 hover:text-arr-accent text-[#808080] cursor-pointer"
                              title="Copy Port"
                            >
                              {copiedField === 'radarr-port' ? <Check className="h-3 w-3 text-[#27c24c]" /> : <Copy className="h-3 w-3" />}
                            </button>
                          </div>
                        </div>
                        <div className="flex items-center justify-between py-1 border-b border-[#2d2d2d]">
                          <span className="text-[#909293]">Use SSL</span>
                          <span className="font-mono text-[#eeeeee]">{isHttps ? 'Enabled (HTTPS)' : 'Disabled (HTTP)'}</span>
                        </div>
                        <div className="flex items-center justify-between py-1 border-b border-[#2d2d2d]">
                          <span className="text-[#909293]">URL Base</span>
                          <div className="flex items-center gap-1.5">
                            <code className="font-mono text-arr-accent bg-[#1a1a1a] px-1.5 py-0.5 rounded">/compat/radarr</code>
                            <button
                              type="button"
                              onClick={() => copyToClipboard('/compat/radarr', 'radarr-urlbase')}
                              className="p-1 hover:text-arr-accent text-[#808080] cursor-pointer"
                              title="Copy URL Base"
                            >
                              {copiedField === 'radarr-urlbase' ? <Check className="h-3 w-3 text-[#27c24c]" /> : <Copy className="h-3 w-3" />}
                            </button>
                          </div>
                        </div>
                        <div className="flex items-center justify-between py-1">
                          <span className="text-[#909293]">API Key</span>
                          <div className="flex items-center gap-1.5 max-w-[220px]">
                            {generatedSeerrKey ? (
                              <>
                                <span className="font-mono text-arr-accent text-[11px] truncate" title={generatedSeerrKey.rawTokenOnce}>
                                  {generatedSeerrKey.rawTokenOnce.slice(0, 12)}…
                                </span>
                                <button
                                  type="button"
                                  onClick={() => copyToClipboard(generatedSeerrKey.rawTokenOnce, 'radarr-key')}
                                  className="p-1 hover:text-arr-accent text-[#808080] cursor-pointer shrink-0"
                                  title="Copy API Key"
                                >
                                  {copiedField === 'radarr-key' ? <Check className="h-3 w-3 text-[#27c24c]" /> : <Copy className="h-3 w-3" />}
                                </button>
                              </>
                            ) : (
                              <button
                                type="button"
                                onClick={() => generateSeerrKeyMutation.mutate()}
                                className="text-[11px] text-arr-accent hover:underline cursor-pointer"
                              >
                                Generate Seerr API Key
                              </button>
                            )}
                          </div>
                        </div>
                      </div>
                    </div>
                    <div className="mt-3 pt-2 text-[11px] text-[#707070] border-t border-[#2d2d2d]">
                      Tip: If running in Docker on the same bridge network, set Hostname to <code>managerr</code> and Port to <code>8080</code>.
                    </div>
                  </div>

                  {/* Sonarr Server in Seerr */}
                  <div className="bg-arr-card border border-arr-card-border rounded-[4px] p-4 flex flex-col justify-between">
                    <div>
                      <div className="flex items-center justify-between pb-2.5 mb-3 border-b border-arr-card-border">
                        <div className="flex items-center gap-2">
                          <Tv className="h-4 w-4 text-arr-accent" />
                          <h4 className="font-semibold text-[#eeeeee] text-[14px]">Sonarr Server in Seerr</h4>
                        </div>
                        <ArrLabel kind="usenet">TV Series</ArrLabel>
                      </div>
                      <p className="text-[11px] text-[#909293] mb-3">
                        In Seerr: <code>Settings → Services → Sonarr → Add Sonarr Server</code>
                      </p>

                      <div className="space-y-2 text-[12px]">
                        <div className="flex items-center justify-between py-1 border-b border-[#2d2d2d]">
                          <span className="text-[#909293]">Server Name</span>
                          <span className="font-mono text-[#eeeeee]">Managerr (Sonarr TV)</span>
                        </div>
                        <div className="flex items-center justify-between py-1 border-b border-[#2d2d2d]">
                          <span className="text-[#909293]">Hostname / IP</span>
                          <div className="flex items-center gap-1.5">
                            <span className="font-mono text-[#eeeeee]">{detectedHost}</span>
                            <button
                              type="button"
                              onClick={() => copyToClipboard(detectedHost, 'sonarr-host')}
                              className="p-1 hover:text-arr-accent text-[#808080] cursor-pointer"
                              title="Copy Hostname"
                            >
                              {copiedField === 'sonarr-host' ? <Check className="h-3 w-3 text-[#27c24c]" /> : <Copy className="h-3 w-3" />}
                            </button>
                          </div>
                        </div>
                        <div className="flex items-center justify-between py-1 border-b border-[#2d2d2d]">
                          <span className="text-[#909293]">Port</span>
                          <div className="flex items-center gap-1.5">
                            <span className="font-mono text-[#eeeeee]">{detectedPort}</span>
                            <button
                              type="button"
                              onClick={() => copyToClipboard(detectedPort, 'sonarr-port')}
                              className="p-1 hover:text-arr-accent text-[#808080] cursor-pointer"
                              title="Copy Port"
                            >
                              {copiedField === 'sonarr-port' ? <Check className="h-3 w-3 text-[#27c24c]" /> : <Copy className="h-3 w-3" />}
                            </button>
                          </div>
                        </div>
                        <div className="flex items-center justify-between py-1 border-b border-[#2d2d2d]">
                          <span className="text-[#909293]">Use SSL</span>
                          <span className="font-mono text-[#eeeeee]">{isHttps ? 'Enabled (HTTPS)' : 'Disabled (HTTP)'}</span>
                        </div>
                        <div className="flex items-center justify-between py-1 border-b border-[#2d2d2d]">
                          <span className="text-[#909293]">URL Base</span>
                          <div className="flex items-center gap-1.5">
                            <code className="font-mono text-arr-accent bg-[#1a1a1a] px-1.5 py-0.5 rounded">/compat/sonarr</code>
                            <button
                              type="button"
                              onClick={() => copyToClipboard('/compat/sonarr', 'sonarr-urlbase')}
                              className="p-1 hover:text-arr-accent text-[#808080] cursor-pointer"
                              title="Copy URL Base"
                            >
                              {copiedField === 'sonarr-urlbase' ? <Check className="h-3 w-3 text-[#27c24c]" /> : <Copy className="h-3 w-3" />}
                            </button>
                          </div>
                        </div>
                        <div className="flex items-center justify-between py-1">
                          <span className="text-[#909293]">API Key</span>
                          <div className="flex items-center gap-1.5 max-w-[220px]">
                            {generatedSeerrKey ? (
                              <>
                                <span className="font-mono text-arr-accent text-[11px] truncate" title={generatedSeerrKey.rawTokenOnce}>
                                  {generatedSeerrKey.rawTokenOnce.slice(0, 12)}…
                                </span>
                                <button
                                  type="button"
                                  onClick={() => copyToClipboard(generatedSeerrKey.rawTokenOnce, 'sonarr-key')}
                                  className="p-1 hover:text-arr-accent text-[#808080] cursor-pointer shrink-0"
                                  title="Copy API Key"
                                >
                                  {copiedField === 'sonarr-key' ? <Check className="h-3 w-3 text-[#27c24c]" /> : <Copy className="h-3 w-3" />}
                                </button>
                              </>
                            ) : (
                              <button
                                type="button"
                                onClick={() => generateSeerrKeyMutation.mutate()}
                                className="text-[11px] text-arr-accent hover:underline cursor-pointer"
                              >
                                Generate Seerr API Key
                              </button>
                            )}
                          </div>
                        </div>
                      </div>
                    </div>
                    <div className="mt-3 pt-2 text-[11px] text-[#707070] border-t border-[#2d2d2d]">
                      Tip: If running in Docker on the same bridge network, set Hostname to <code>managerr</code> and Port to <code>8080</code>.
                    </div>
                  </div>
                </div>

                <div className="mt-4 pt-3 border-t border-arr-card-border flex flex-wrap items-center gap-x-4 gap-y-2 text-[11px] text-[#909293]">
                  <span className="text-[#27c24c] font-semibold flex items-center gap-1">
                    <CheckCircle2 className="h-3.5 w-3.5" /> Out of the Box:
                  </span>
                  <span>✓ <strong>Test Connection</strong> (<code>/api/v3/system/status</code>)</span>
                  <span>✓ <strong>Quality Profiles</strong> &amp; <strong>Root Folders</strong></span>
                  <span>✓ <strong>Season Selection</strong> &amp; <strong>Automatic Search on Request</strong></span>
                  <span>✓ <strong>Tags &amp; Requester Plex-ID</strong> (Automatic language promotion)</span>
                </div>
              </div>
            </FieldSet>

            <FieldSet legend="Compatibility Shim Settings">
              <FormGroup
                label="Sonarr Anime Season Expansion"
                helpText="When enabled, single-season anime series exposed over /compat/sonarr expand episodes into Season 01 for legacy Seerr/Sonarr consumers."
              >
                <label className="inline-flex items-center gap-2 pt-1.5 text-[13px] cursor-pointer">
                  <input
                    type="checkbox"
                    disabled={!canExecute || saveCompatSettingsMutation.isPending || compatSettingsQuery.isLoading}
                    checked={Boolean(compatSettingsQuery.data?.expandSingleSeasonAnime)}
                    onChange={(e) => saveCompatSettingsMutation.mutate(e.target.checked)}
                    className="accent-arr-accent"
                  />
                  <span>Expand single-season anime in Sonarr compatibility responses</span>
                </label>
              </FormGroup>
            </FieldSet>

            <FieldSet legend="Compatibility API Routers">
              <div className="overflow-x-auto">
                <table className="arr-table">
                  <thead>
                    <tr>
                      <th>Mount Path</th>
                      <th>Required Scope</th>
                      <th>Supported Ecosystem Tools</th>
                    </tr>
                  </thead>
                  <tbody>
                    {[
                      ['/compat/sonarr/api/v3/*', 'compat:sonarr', 'Overseerr, Jellyseerr, Bazarr, Recyclarr, nzb360'],
                      ['/compat/radarr/api/v3/*', 'compat:radarr', 'Overseerr, Jellyseerr, Bazarr, Recyclarr, nzb360'],
                      ['/compat/lidarr/api/v1/*', 'compat:lidarr', 'Lidify, Lidarr mobile clients'],
                      ['/compat/readarr/api/v1/* (?slot=ebook|audiobook)', 'compat:readarr', 'Calibre-Web-Automated, Audiobook bots'],
                      ['/compat/prowlarr/api/v1/* & /api/v1/torznab/{id}/api', 'compat:prowlarr', 'Cross-Seed, Autobrr, external Torznab/Newznab'],
                    ].map(([path, scope, apps]) => (
                      <tr key={path}>
                        <td className="font-mono text-arr-accent">{path}</td>
                        <td>
                          <ArrLabel kind="default">{scope}</ArrLabel>
                        </td>
                        <td>{apps}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </FieldSet>
          </>
        )}
      </PageContentBody>
    </div>
  );
}
