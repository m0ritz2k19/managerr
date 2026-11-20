import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Clock,
  Edit2,
  HardDrive,
  Languages,
  Layers,
  Plus,
  Save,
  ShieldCheck,
  Sliders,
  Sparkles,
  Trash2,
  Upload,
  X,
} from 'lucide-react';
import { apiFetch } from '../lib/api';
import {
  ArrAlert,
  ArrButton,
  ArrLabel,
  ArrModal,
  FieldSet,
  FormGroup,
} from './arr/ArrPrimitives';

// ============================================================================
// Issue #31: Custom Format & Profile DTOs
// ============================================================================

export interface CustomFormatSpecItem {
  name: string;
  implementation: string; // 'ReleaseTitleSpecification' | 'SourceSpecification' | 'ResolutionSpecification' | 'LanguageSpecification' | 'ReleaseGroupSpecification' | 'IndexerFlagSpecification' | 'SizeSpecification'
  negate: boolean;
  required: boolean;
  pattern: string;
}

export interface CustomFormatSummary {
  id: number;
  trashId: string | null;
  name: string;
  includeWhenRenaming: boolean;
  specifications: CustomFormatSpecItem[];
  defaultScore: number;
  containsLanguageRule: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface PreferredTermScore {
  term: string;
  score: number;
}

export interface ReleaseProfileSummary {
  id: number;
  name: string;
  isEnabled: boolean;
  mustContain: string[];
  mustNotContain: string[];
  preferredTerms: PreferredTermScore[];
  indexerId: number | null;
  tags: string[];
  mediaVertical: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface DelayProfileSummary {
  id: number;
  name: string;
  preferredProtocol: 'usenet' | 'torrent' | 'any';
  usenetDelayMinutes: number;
  torrentDelayMinutes: number;
  bypassIfHighestQuality: boolean;
  bypassIfAboveCustomFormatScore: boolean;
  minCustomFormatScoreForBypass: number;
  tags: string[];
  sortOrder: number;
  isDefault: boolean;
}

export interface QualitySizeLimitDto {
  qualityName: string;
  mediaVertical: string;
  minMbPerMin: number;
  preferredMbPerMin: number;
  maxMbPerMin: number;
  updatedAt: string;
}

interface LanguagePolicyTierDto {
  tier: number;
  id: string;
  label: string;
  audioContainsAll: string[];
  audioContainsAny: string[];
  subtitlesContainsAny: string[];
  score: number;
  satisfiesLanguageCutoff: boolean;
}

interface LanguagePolicyItemDto {
  id: number;
  slug: string;
  name: string;
  description: string;
  isSystemPreset?: boolean;
  tiers: LanguagePolicyTierDto[];
  enableLanguageUpgrades: boolean;
  languageCutoffTier: number;
  tradeoffMode: string;
  maxQualityTierDrop: number;
  minimumAbsoluteQuality: string | null;
}

interface TraitMixinRuleDto {
  id: string;
  label: string;
  field: string;
  pattern: string;
  score: number;
  requiredReject: boolean;
}

interface ProfileMixinDto {
  id: number;
  slug: string;
  name: string;
  category: string;
  rules: TraitMixinRuleDto[];
}

interface VerificationSettingsDto {
  autoPassConfidence: number;
  quarantineMinConfidence: number;
  autoPatchHeaderConfidence: number;
  fakeDubCorrelationThreshold: number;
  defaultProfileSlug: string;
}

interface ProfileOptionDto {
  id: number;
  slug: string;
  name: string;
}

interface IndexerDescriptor {
  id: number;
  name: string;
  protocol?: string;
}

export const CF_SPEC_IMPLEMENTATIONS: Array<{
  type: string;
  label: string;
  placeholder: string;
  description: string;
}> = [
  {
    type: 'ReleaseTitleSpecification',
    label: 'Release Title',
    placeholder: '\\b(HDR10(Plus|\\+)?|DV|DoVi)\\b',
    description: 'Regex matching release title string',
  },
  {
    type: 'SourceSpecification',
    label: 'Source',
    placeholder: 'BluRay|WEB-?DL|HDTV',
    description: 'Regex or token matching parsed release source',
  },
  {
    type: 'ResolutionSpecification',
    label: 'Resolution',
    placeholder: '2160p|1080p|720p',
    description: 'Regex matching video resolution',
  },
  {
    type: 'LanguageSpecification',
    label: 'Language',
    placeholder: 'German|Deutsch|ger',
    description: 'Language token or regex match',
  },
  {
    type: 'ReleaseGroupSpecification',
    label: 'Release Group',
    placeholder: '-GRP|-FLUX|-DON|-FraMeSToR',
    description: 'Regex matching release group name',
  },
  {
    type: 'IndexerFlagSpecification',
    label: 'Indexer Flag',
    placeholder: 'G_FREELEECH|VIP',
    description: 'Flag name reported by indexer',
  },
  {
    type: 'SizeSpecification',
    label: 'Size Range (GB)',
    placeholder: 'minGb..maxGb (e.g. 1.5..15.0 or 0..25)',
    description: 'Minimum and maximum size range in GB',
  },
];

const DEFAULT_QUALITY_SIZES: QualitySizeLimitDto[] = [
  { qualityName: 'Remux-2160p', mediaVertical: 'video', minMbPerMin: 150, preferredMbPerMin: 400, maxMbPerMin: 0, updatedAt: '' },
  { qualityName: 'BluRay-2160p', mediaVertical: 'video', minMbPerMin: 80, preferredMbPerMin: 220, maxMbPerMin: 350, updatedAt: '' },
  { qualityName: 'WEBDL-2160p', mediaVertical: 'video', minMbPerMin: 50, preferredMbPerMin: 120, maxMbPerMin: 200, updatedAt: '' },
  { qualityName: 'Remux-1080p', mediaVertical: 'video', minMbPerMin: 60, preferredMbPerMin: 180, maxMbPerMin: 280, updatedAt: '' },
  { qualityName: 'BluRay-1080p', mediaVertical: 'video', minMbPerMin: 30, preferredMbPerMin: 70, maxMbPerMin: 150, updatedAt: '' },
  { qualityName: 'WEBDL-1080p', mediaVertical: 'video', minMbPerMin: 15, preferredMbPerMin: 35, maxMbPerMin: 75, updatedAt: '' },
  { qualityName: 'HDTV-1080p', mediaVertical: 'video', minMbPerMin: 10, preferredMbPerMin: 25, maxMbPerMin: 50, updatedAt: '' },
  { qualityName: 'FLAC 24bit', mediaVertical: 'music', minMbPerMin: 10, preferredMbPerMin: 20, maxMbPerMin: 40, updatedAt: '' },
  { qualityName: 'FLAC', mediaVertical: 'music', minMbPerMin: 5, preferredMbPerMin: 10, maxMbPerMin: 25, updatedAt: '' },
  { qualityName: 'MP3-320', mediaVertical: 'music', minMbPerMin: 2, preferredMbPerMin: 2.5, maxMbPerMin: 3, updatedAt: '' },
  { qualityName: 'EPUB', mediaVertical: 'ebooks', minMbPerMin: 0.05, preferredMbPerMin: 0.2, maxMbPerMin: 1, updatedAt: '' },
  { qualityName: 'M4B', mediaVertical: 'ebooks', minMbPerMin: 0.5, preferredMbPerMin: 1, maxMbPerMin: 3, updatedAt: '' },
];

export interface ProfilesAdvancedPanelsProps {
  canReadProfiles: boolean;
  canWriteProfiles: boolean;
}

export function ProfilesAdvancedPanels({
  canReadProfiles,
  canWriteProfiles,
}: ProfilesAdvancedPanelsProps) {
  const qc = useQueryClient();

  // ============================================================================
  // 1. Native Custom Formats State & Queries
  // ============================================================================
  const customFormatsQuery = useQuery({
    queryKey: ['custom-formats'],
    queryFn: () => apiFetch<CustomFormatSummary[]>('/api/v1/profiles/custom-formats'),
    enabled: canReadProfiles,
  });

  const [cfModalOpen, setCfModalOpen] = useState(false);
  const [editingCfId, setEditingCfId] = useState<number | null>(null);
  const [cfName, setCfName] = useState('');
  const [cfTrashId, setCfTrashId] = useState('');
  const [cfDefaultScore, setCfDefaultScore] = useState<number>(0);
  const [cfIncludeRenaming, setCfIncludeRenaming] = useState(false);
  const [cfSpecs, setCfSpecs] = useState<CustomFormatSpecItem[]>([]);
  const [showTrashModal, setShowTrashModal] = useState(false);
  const [trashJson, setTrashJson] = useState('');
  const [trashBanner, setTrashBanner] = useState<string | null>(null);

  const saveCfMutation = useMutation({
    mutationFn: async () => {
      const payload = {
        name: cfName.trim(),
        trashId: cfTrashId.trim() || null,
        defaultScore: cfDefaultScore,
        includeWhenRenaming: cfIncludeRenaming,
        specifications: cfSpecs,
      };
      if (editingCfId !== null) {
        return apiFetch<CustomFormatSummary>(`/api/v1/profiles/custom-formats/${editingCfId}`, {
          method: 'PUT',
          body: JSON.stringify(payload),
        });
      }
      return apiFetch<CustomFormatSummary>('/api/v1/profiles/custom-formats', {
        method: 'POST',
        body: JSON.stringify(payload),
      });
    },
    onSuccess: () => {
      setCfModalOpen(false);
      qc.invalidateQueries({ queryKey: ['custom-formats'] });
    },
  });

  const deleteCfMutation = useMutation({
    mutationFn: (id: number) => apiFetch(`/api/v1/profiles/custom-formats/${id}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['custom-formats'] }),
  });

  const importTrashMutation = useMutation({
    mutationFn: () =>
      apiFetch<{ importedCount: number; updatedCount: number }>(
        '/api/v1/profiles/import-trash',
        {
          method: 'POST',
          body: JSON.stringify({ rawJson: trashJson }),
        },
      ),
    onSuccess: (data) => {
      setShowTrashModal(false);
      setTrashJson('');
      setTrashBanner(`Imported ${data.importedCount} new and updated ${data.updatedCount} TRaSH Custom Formats!`);
      qc.invalidateQueries({ queryKey: ['custom-formats'] });
    },
  });

  function openNewCf() {
    setEditingCfId(null);
    setCfName('');
    setCfTrashId('');
    setCfDefaultScore(100);
    setCfIncludeRenaming(false);
    setCfSpecs([
      {
        name: 'Release Title Match',
        implementation: 'ReleaseTitleSpecification',
        negate: false,
        required: false,
        pattern: '',
      },
    ]);
    setCfModalOpen(true);
  }

  function openEditCf(cf: CustomFormatSummary) {
    setEditingCfId(cf.id);
    setCfName(cf.name);
    setCfTrashId(cf.trashId ?? '');
    setCfDefaultScore(cf.defaultScore);
    setCfIncludeRenaming(cf.includeWhenRenaming);
    setCfSpecs(
      cf.specifications?.length > 0
        ? cf.specifications
        : [
            {
              name: 'Release Title Match',
              implementation: 'ReleaseTitleSpecification',
              negate: false,
              required: false,
              pattern: '',
            },
          ],
    );
    setCfModalOpen(true);
  }

  function addCfSpecRow() {
    setCfSpecs((prev) => [
      ...prev,
      {
        name: 'Specification Row',
        implementation: 'ReleaseTitleSpecification',
        negate: false,
        required: false,
        pattern: '',
      },
    ]);
  }

  function removeCfSpecRow(index: number) {
    setCfSpecs((prev) => prev.filter((_, i) => i !== index));
  }

  function updateCfSpecRow(index: number, patch: Partial<CustomFormatSpecItem>) {
    setCfSpecs((prev) =>
      prev.map((item, i) => (i === index ? { ...item, ...patch } : item)),
    );
  }

  // ============================================================================
  // 2. Release Profiles / Restrictions State & Queries
  // ============================================================================
  const releaseProfilesQuery = useQuery({
    queryKey: ['release-profiles'],
    queryFn: () => apiFetch<ReleaseProfileSummary[]>('/api/v1/profiles/release-profiles'),
    enabled: canReadProfiles,
  });

  const indexersQuery = useQuery({
    queryKey: ['indexers-list'],
    queryFn: async () => {
      try {
        return await apiFetch<IndexerDescriptor[]>('/api/v1/indexers');
      } catch {
        return [];
      }
    },
    enabled: canReadProfiles,
  });

  const [rpModalOpen, setRpModalOpen] = useState(false);
  const [editingRpId, setEditingRpId] = useState<number | null>(null);
  const [rpName, setRpName] = useState('');
  const [rpEnabled, setRpEnabled] = useState(true);
  const [rpMustContain, setRpMustContain] = useState('');
  const [rpMustNotContain, setRpMustNotContain] = useState('');
  const [rpPreferredTerms, setRpPreferredTerms] = useState<PreferredTermScore[]>([]);
  const [rpIndexerId, setRpIndexerId] = useState<number | null>(null);
  const [rpVertical, setRpVertical] = useState<string>('all');
  const [rpTags, setRpTags] = useState('');

  const saveRpMutation = useMutation({
    mutationFn: async () => {
      const payload = {
        name: rpName.trim(),
        isEnabled: rpEnabled,
        mustContain: rpMustContain
          .split(/[\n,]/)
          .map((s) => s.trim())
          .filter(Boolean),
        mustNotContain: rpMustNotContain
          .split(/[\n,]/)
          .map((s) => s.trim())
          .filter(Boolean),
        preferredTerms: rpPreferredTerms.filter((pt) => pt.term.trim()),
        indexerId: rpIndexerId,
        mediaVertical: rpVertical === 'all' ? null : rpVertical,
        tags: rpTags
          .split(',')
          .map((t) => t.trim())
          .filter(Boolean),
      };
      if (editingRpId !== null) {
        return apiFetch<ReleaseProfileSummary>(`/api/v1/profiles/release-profiles/${editingRpId}`, {
          method: 'PUT',
          body: JSON.stringify(payload),
        });
      }
      return apiFetch<ReleaseProfileSummary>('/api/v1/profiles/release-profiles', {
        method: 'POST',
        body: JSON.stringify(payload),
      });
    },
    onSuccess: () => {
      setRpModalOpen(false);
      qc.invalidateQueries({ queryKey: ['release-profiles'] });
    },
  });

  const deleteRpMutation = useMutation({
    mutationFn: (id: number) =>
      apiFetch(`/api/v1/profiles/release-profiles/${id}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['release-profiles'] }),
  });

  function openNewRp() {
    setEditingRpId(null);
    setRpName('');
    setRpEnabled(true);
    setRpMustContain('');
    setRpMustNotContain('');
    setRpPreferredTerms([{ term: 'x265', score: 50 }]);
    setRpIndexerId(null);
    setRpVertical('all');
    setRpTags('');
    setRpModalOpen(true);
  }

  function openEditRp(rp: ReleaseProfileSummary) {
    setEditingRpId(rp.id);
    setRpName(rp.name);
    setRpEnabled(rp.isEnabled);
    setRpMustContain(rp.mustContain?.join('\n') ?? '');
    setRpMustNotContain(rp.mustNotContain?.join('\n') ?? '');
    setRpPreferredTerms(
      rp.preferredTerms?.length > 0 ? rp.preferredTerms : [{ term: '', score: 10 }],
    );
    setRpIndexerId(rp.indexerId);
    setRpVertical(rp.mediaVertical ?? 'all');
    setRpTags(rp.tags?.join(', ') ?? '');
    setRpModalOpen(true);
  }

  // ============================================================================
  // 3. Protocol Delay Profiles State & Queries
  // ============================================================================
  const delayProfilesQuery = useQuery({
    queryKey: ['delay-profiles'],
    queryFn: () => apiFetch<DelayProfileSummary[]>('/api/v1/profiles/delay-profiles'),
    enabled: canReadProfiles,
  });

  const [dpModalOpen, setDpModalOpen] = useState(false);
  const [editingDpId, setEditingDpId] = useState<number | null>(null);
  const [dpName, setDpName] = useState('');
  const [dpProtocol, setDpProtocol] = useState<'usenet' | 'torrent' | 'any'>('any');
  const [dpUsenetDelay, setDpUsenetDelay] = useState<number>(0);
  const [dpTorrentDelay, setDpTorrentDelay] = useState<number>(120);
  const [dpBypassQuality, setDpBypassQuality] = useState(true);
  const [dpBypassCf, setDpBypassCf] = useState(false);
  const [dpMinCfScore, setDpMinCfScore] = useState<number>(100);
  const [dpTags, setDpTags] = useState('');
  const [dpSortOrder, setDpSortOrder] = useState<number>(1);
  const [dpIsDefault, setDpIsDefault] = useState(false);

  const saveDpMutation = useMutation({
    mutationFn: async () => {
      const payload = {
        name: dpName.trim(),
        preferredProtocol: dpProtocol,
        usenetDelayMinutes: Number(dpUsenetDelay) || 0,
        torrentDelayMinutes: Number(dpTorrentDelay) || 0,
        bypassIfHighestQuality: dpBypassQuality,
        bypassIfAboveCustomFormatScore: dpBypassCf,
        minCustomFormatScoreForBypass: Number(dpMinCfScore) || 0,
        tags: dpTags
          .split(',')
          .map((t) => t.trim())
          .filter(Boolean),
        sortOrder: dpSortOrder,
      };
      if (editingDpId !== null) {
        return apiFetch<DelayProfileSummary>(`/api/v1/profiles/delay-profiles/${editingDpId}`, {
          method: 'PUT',
          body: JSON.stringify(payload),
        });
      }
      return apiFetch<DelayProfileSummary>('/api/v1/profiles/delay-profiles', {
        method: 'POST',
        body: JSON.stringify(payload),
      });
    },
    onSuccess: () => {
      setDpModalOpen(false);
      qc.invalidateQueries({ queryKey: ['delay-profiles'] });
    },
  });

  const deleteDpMutation = useMutation({
    mutationFn: (id: number) =>
      apiFetch(`/api/v1/profiles/delay-profiles/${id}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['delay-profiles'] }),
  });

  function openNewDp() {
    setEditingDpId(null);
    setDpName('');
    setDpProtocol('any');
    setDpUsenetDelay(0);
    setDpTorrentDelay(120);
    setDpBypassQuality(true);
    setDpBypassCf(false);
    setDpMinCfScore(100);
    setDpTags('');
    setDpSortOrder(10);
    setDpIsDefault(false);
    setDpModalOpen(true);
  }

  function openEditDp(dp: DelayProfileSummary) {
    setEditingDpId(dp.id);
    setDpName(dp.name);
    setDpProtocol(dp.preferredProtocol);
    setDpUsenetDelay(dp.usenetDelayMinutes);
    setDpTorrentDelay(dp.torrentDelayMinutes);
    setDpBypassQuality(dp.bypassIfHighestQuality);
    setDpBypassCf(dp.bypassIfAboveCustomFormatScore);
    setDpMinCfScore(dp.minCustomFormatScoreForBypass);
    setDpTags(dp.tags?.join(', ') ?? '');
    setDpSortOrder(dp.sortOrder);
    setDpIsDefault(dp.isDefault);
    setDpModalOpen(true);
  }

  // ============================================================================
  // 4. Quality Size Limits (MB/min) State & Queries
  // ============================================================================
  const [sizeLimitsVertical, setSizeLimitsVertical] = useState<string>('video');
  const [customSizeLimits, setCustomSizeLimits] = useState<QualitySizeLimitDto[] | null>(null);
  const [sizeSaveSuccess, setSizeSaveSuccess] = useState(false);

  const qualitySizesQuery = useQuery({
    queryKey: ['quality-sizes'],
    queryFn: async () => {
      try {
        const data = await apiFetch<QualitySizeLimitDto[]>('/api/v1/profiles/quality-sizes');
        return data && data.length > 0 ? data : DEFAULT_QUALITY_SIZES;
      } catch {
        return DEFAULT_QUALITY_SIZES;
      }
    },
    enabled: canReadProfiles,
  });

  const currentSizeLimits = customSizeLimits ?? qualitySizesQuery.data ?? DEFAULT_QUALITY_SIZES;

  const saveSizeLimitsMutation = useMutation({
    mutationFn: async () => {
      return apiFetch('/api/v1/profiles/quality-sizes', {
        method: 'PUT',
        body: JSON.stringify({ items: currentSizeLimits }),
      });
    },
    onSuccess: () => {
      setSizeSaveSuccess(true);
      setTimeout(() => setSizeSaveSuccess(false), 3000);
      qc.invalidateQueries({ queryKey: ['quality-sizes'] });
    },
  });

  function updateSizeLimit(
    qualityName: string,
    vertical: string,
    field: 'minMbPerMin' | 'preferredMbPerMin' | 'maxMbPerMin',
    value: number,
  ) {
    const next = currentSizeLimits.map((item) => {
      if (item.qualityName === qualityName && item.mediaVertical === vertical) {
        return { ...item, [field]: value };
      }
      return item;
    });
    setCustomSizeLimits(next);
  }

  // Filtered size limits for display
  const displayedSizeLimits = currentSizeLimits.filter((s) =>
    sizeLimitsVertical === 'all' ? true : s.mediaVertical === sizeLimitsVertical,
  );

  // ============================================================================
  // 5. Language Policies CRUD State & Queries
  // ============================================================================
  const languagePoliciesQuery = useQuery({
    queryKey: ['language-policies'],
    queryFn: () => apiFetch<LanguagePolicyItemDto[]>('/api/v1/profiles/language-policies'),
    enabled: canReadProfiles,
  });

  const [lpModalOpen, setLpModalOpen] = useState(false);
  const [editingLpId, setEditingLpId] = useState<number | null>(null);
  const [lpName, setLpName] = useState('');
  const [lpSlug, setLpSlug] = useState('');
  const [lpDesc, setLpDesc] = useState('');
  const [lpEnableUpgrades, setLpEnableUpgrades] = useState(true);
  const [lpCutoffTier, setLpCutoffTier] = useState(1);
  const [lpTradeoffMode, setLpTradeoffMode] = useState('bounded_stepdown');
  const [lpMaxDrop, setLpMaxDrop] = useState(2);
  const [lpMinQuality, setLpMinQuality] = useState('WEBDL-1080p');
  const [lpTiers, setLpTiers] = useState<
    Array<{
      tier: number;
      id: string;
      label: string;
      audioAll: string;
      audioAny: string;
      subsAny: string;
      score: number;
      satisfiesCutoff: boolean;
    }>
  >([]);
  const [lpBanner, setLpBanner] = useState<{ kind: 'success' | 'danger'; text: string } | null>(null);

  function openNewLp() {
    setEditingLpId(null);
    setLpName('');
    setLpSlug('');
    setLpDesc('');
    setLpEnableUpgrades(true);
    setLpCutoffTier(1);
    setLpTradeoffMode('bounded_stepdown');
    setLpMaxDrop(2);
    setLpMinQuality('WEBDL-1080p');
    setLpTiers([
      {
        tier: 1,
        id: 'tier-1',
        label: 'Primary Audio',
        audioAll: 'de, original',
        audioAny: '',
        subsAny: '',
        score: 1100,
        satisfiesCutoff: true,
      },
    ]);
    setLpBanner(null);
    setLpModalOpen(true);
  }

  function openEditLp(lp: LanguagePolicyItemDto) {
    setEditingLpId(lp.id);
    setLpName(lp.name);
    setLpSlug(lp.slug);
    setLpDesc(lp.description ?? '');
    setLpEnableUpgrades(lp.enableLanguageUpgrades);
    setLpCutoffTier(lp.languageCutoffTier);
    setLpTradeoffMode(lp.tradeoffMode || 'bounded_stepdown');
    setLpMaxDrop(lp.maxQualityTierDrop ?? 2);
    setLpMinQuality(lp.minimumAbsoluteQuality ?? '');
    setLpTiers(
      (lp.tiers ?? []).map((t) => ({
        tier: t.tier,
        id: t.id,
        label: t.label,
        audioAll: (t.audioContainsAll ?? []).join(', '),
        audioAny: (t.audioContainsAny ?? []).join(', '),
        subsAny: (t.subtitlesContainsAny ?? []).join(', '),
        score: t.score,
        satisfiesCutoff: t.satisfiesLanguageCutoff,
      })),
    );
    setLpBanner(null);
    setLpModalOpen(true);
  }

  const saveLpMutation = useMutation({
    mutationFn: () => {
      const splitCsv = (raw: string) =>
        raw
          .split(',')
          .map((s) => s.trim().toLowerCase())
          .filter(Boolean);
      const payload = {
        name: lpName.trim(),
        slug: lpSlug.trim() || undefined,
        description: lpDesc.trim(),
        tiers: lpTiers.map((t, idx) => ({
          tier: Number(t.tier) || idx + 1,
          id: t.id.trim() || `tier-${idx + 1}`,
          label: t.label.trim() || `Tier ${idx + 1}`,
          audioContainsAll: splitCsv(t.audioAll),
          audioContainsAny: splitCsv(t.audioAny),
          subtitlesContainsAny: splitCsv(t.subsAny),
          score: Number(t.score) || 0,
          satisfiesLanguageCutoff: t.satisfiesCutoff,
        })),
        enableLanguageUpgrades: lpEnableUpgrades,
        languageCutoffTier: Math.max(1, Number(lpCutoffTier) || 1),
        tradeoffMode: lpTradeoffMode,
        maxQualityTierDrop: Math.max(0, Number(lpMaxDrop) || 0),
        minimumAbsoluteQuality: lpMinQuality.trim() || null,
      };
      if (editingLpId !== null) {
        return apiFetch<LanguagePolicyItemDto>(`/api/v1/profiles/language-policies/${editingLpId}`, {
          method: 'PUT',
          body: JSON.stringify(payload),
        });
      }
      return apiFetch<LanguagePolicyItemDto>('/api/v1/profiles/language-policies', {
        method: 'POST',
        body: JSON.stringify(payload),
      });
    },
    onSuccess: () => {
      setLpModalOpen(false);
      qc.invalidateQueries({ queryKey: ['language-policies'] });
      qc.invalidateQueries({ queryKey: ['profiles'] });
    },
    onError: (err: Error) => {
      setLpBanner({ kind: 'danger', text: err.message });
    },
  });

  const deleteLpMutation = useMutation({
    mutationFn: (id: number) =>
      apiFetch<void>(`/api/v1/profiles/language-policies/${id}`, { method: 'DELETE' }),
    onSuccess: () => {
      setLpBanner({ kind: 'success', text: 'Language policy deleted.' });
      qc.invalidateQueries({ queryKey: ['language-policies'] });
      qc.invalidateQueries({ queryKey: ['profiles'] });
    },
    onError: (err: Error) => {
      setLpBanner({ kind: 'danger', text: err.message });
    },
  });

  // ============================================================================
  // 6. Trait Mixins CRUD State & Queries
  // ============================================================================
  const mixinsQuery = useQuery({
    queryKey: ['profile-mixins'],
    queryFn: () => apiFetch<ProfileMixinDto[]>('/api/v1/profiles/mixins'),
    enabled: canReadProfiles,
  });

  const [mxModalOpen, setMxModalOpen] = useState(false);
  const [editingMxId, setEditingMxId] = useState<number | null>(null);
  const [mxName, setMxName] = useState('');
  const [mxSlug, setMxSlug] = useState('');
  const [mxCategory, setMxCategory] = useState('codec');
  const [mxRules, setMxRules] = useState<TraitMixinRuleDto[]>([]);
  const [mxBanner, setMxBanner] = useState<{ kind: 'success' | 'danger'; text: string } | null>(null);

  function openNewMx() {
    setEditingMxId(null);
    setMxName('');
    setMxSlug('');
    setMxCategory('codec');
    setMxRules([
      {
        id: 'rule-1',
        label: 'HEVC Video Codec',
        field: 'VideoCodec',
        pattern: '^HEVC$',
        score: 100,
        requiredReject: false,
      },
    ]);
    setMxBanner(null);
    setMxModalOpen(true);
  }

  function openEditMx(mx: ProfileMixinDto) {
    setEditingMxId(mx.id);
    setMxName(mx.name);
    setMxSlug(mx.slug);
    setMxCategory(mx.category || 'codec');
    setMxRules(mx.rules?.length ? mx.rules.map((r) => ({ ...r })) : []);
    setMxBanner(null);
    setMxModalOpen(true);
  }

  const saveMxMutation = useMutation({
    mutationFn: () => {
      const payload = {
        name: mxName.trim(),
        slug: mxSlug.trim() || undefined,
        category: mxCategory.trim() || 'custom',
        rules: mxRules.map((r, idx) => ({
          id: r.id.trim() || `rule-${idx + 1}`,
          label: r.label.trim() || `Rule ${idx + 1}`,
          field: r.field,
          pattern: r.pattern,
          score: Number(r.score) || 0,
          requiredReject: Boolean(r.requiredReject),
        })),
      };
      if (editingMxId !== null) {
        return apiFetch<ProfileMixinDto>(`/api/v1/profiles/mixins/${editingMxId}`, {
          method: 'PUT',
          body: JSON.stringify(payload),
        });
      }
      return apiFetch<ProfileMixinDto>('/api/v1/profiles/mixins', {
        method: 'POST',
        body: JSON.stringify(payload),
      });
    },
    onSuccess: () => {
      setMxModalOpen(false);
      qc.invalidateQueries({ queryKey: ['profile-mixins'] });
      qc.invalidateQueries({ queryKey: ['profiles'] });
    },
    onError: (err: Error) => {
      setMxBanner({ kind: 'danger', text: err.message });
    },
  });

  const deleteMxMutation = useMutation({
    mutationFn: (id: number) =>
      apiFetch<void>(`/api/v1/profiles/mixins/${id}`, { method: 'DELETE' }),
    onSuccess: () => {
      setMxBanner({ kind: 'success', text: 'Trait mixin deleted.' });
      qc.invalidateQueries({ queryKey: ['profile-mixins'] });
      qc.invalidateQueries({ queryKey: ['profiles'] });
    },
    onError: (err: Error) => {
      setMxBanner({ kind: 'danger', text: err.message });
    },
  });

  // ============================================================================
  // 7. Verification Thresholds & Default Fallback Profile Settings
  // ============================================================================
  const verificationSettingsQuery = useQuery({
    queryKey: ['verification-settings'],
    queryFn: () => apiFetch<VerificationSettingsDto>('/api/v1/verification/settings'),
    enabled: canReadProfiles,
  });

  const profileOptionsQuery = useQuery({
    queryKey: ['profiles'],
    queryFn: () => apiFetch<ProfileOptionDto[]>('/api/v1/profiles'),
    enabled: canReadProfiles,
  });

  const [vsAutoPass, setVsAutoPass] = useState('0.80');
  const [vsQuarantineMin, setVsQuarantineMin] = useState('0.45');
  const [vsAutoPatch, setVsAutoPatch] = useState('0.85');
  const [vsFakeDub, setVsFakeDub] = useState('0.97');
  const [vsDefaultSlug, setVsDefaultSlug] = useState('german-first-best-quality');
  const [vsBanner, setVsBanner] = useState<{ kind: 'success' | 'danger'; text: string } | null>(null);

  useEffect(() => {
    if (verificationSettingsQuery.data) {
      setVsAutoPass(String(verificationSettingsQuery.data.autoPassConfidence));
      setVsQuarantineMin(String(verificationSettingsQuery.data.quarantineMinConfidence));
      setVsAutoPatch(String(verificationSettingsQuery.data.autoPatchHeaderConfidence));
      setVsFakeDub(String(verificationSettingsQuery.data.fakeDubCorrelationThreshold));
      setVsDefaultSlug(verificationSettingsQuery.data.defaultProfileSlug || 'german-first-best-quality');
    }
  }, [verificationSettingsQuery.data]);

  const saveVerificationSettingsMutation = useMutation({
    mutationFn: () =>
      apiFetch<VerificationSettingsDto>('/api/v1/verification/settings', {
        method: 'PUT',
        body: JSON.stringify({
          autoPassConfidence: Number(vsAutoPass) || 0.8,
          quarantineMinConfidence: Number(vsQuarantineMin) || 0.45,
          autoPatchHeaderConfidence: Number(vsAutoPatch) || 0.85,
          fakeDubCorrelationThreshold: Number(vsFakeDub) || 0.97,
          defaultProfileSlug: vsDefaultSlug || 'german-first-best-quality',
        }),
      }),
    onSuccess: (saved) => {
      setVsBanner({
        kind: 'success',
        text: `Verification thresholds & default profile (${saved.defaultProfileSlug}) saved.`,
      });
      qc.invalidateQueries({ queryKey: ['verification-settings'] });
    },
    onError: (err: Error) => {
      setVsBanner({ kind: 'danger', text: err.message });
    },
  });

  return (
    <div className="space-y-8 mt-6">
      {trashBanner && (
        <ArrAlert kind="success" className="mb-4">
          <div className="flex items-center justify-between">
            <span>{trashBanner}</span>
            <button
              type="button"
              onClick={() => setTrashBanner(null)}
              className="text-[#909293] hover:text-white"
            >
              <X className="h-4 w-4" />
            </button>
          </div>
        </ArrAlert>
      )}

      {/* ====================================================================== */}
      {/* 1. NATIVE CUSTOM FORMAT VISUAL BUILDER & TRASH JSON CRUD                */}
      {/* ====================================================================== */}
      <FieldSet
        legend={
          <div className="flex items-center gap-2">
            <Layers className="h-5 w-5 text-arr-accent" />
            <span>Custom Formats (TRaSH Specifications Builder)</span>
          </div>
        }
        action={
          canWriteProfiles && (
            <div className="flex items-center gap-2">
              <ArrButton variant="default" onClick={() => setShowTrashModal(true)}>
                <Upload className="h-3.5 w-3.5" /> Import TRaSH JSON
              </ArrButton>
              <ArrButton variant="success" onClick={openNewCf}>
                <Plus className="h-3.5 w-3.5" /> Add Custom Format
              </ArrButton>
            </div>
          )
        }
      >
        <p className="text-[13px] text-[#909293] mb-4">
          Score releases dynamically based on Sonarr/Radarr release specifications. Scores are added to candidate releases during automated or interactive search.
        </p>

        {(customFormatsQuery.data ?? []).length === 0 ? (
          <div className="bg-arr-card border border-arr-card-border rounded-[4px] p-6 text-center text-[13px] text-[#909293]">
            No Custom Formats configured yet. Click <strong>Import TRaSH JSON</strong> to pull standard community rules or <strong>Add Custom Format</strong> to create native regex/size specifications.
          </div>
        ) : (
          <div className="overflow-x-auto border border-arr-card-border rounded-[4px]">
            <table className="arr-table w-full">
              <thead>
                <tr>
                  <th>Format Name</th>
                  <th>Default Score</th>
                  <th>Specifications</th>
                  <th>TRaSH ID</th>
                  <th>Renaming</th>
                  <th className="text-right">Actions</th>
                </tr>
              </thead>
              <tbody>
                {(customFormatsQuery.data ?? []).map((cf) => {
                  const scoreKind =
                    cf.defaultScore > 0 ? 'success' : cf.defaultScore < 0 ? 'danger' : 'default';
                  const scorePrefix = cf.defaultScore > 0 ? '+' : '';

                  return (
                    <tr key={cf.id} className="hover:bg-[#2b2b2b]">
                      <td>
                        <div className="font-semibold text-[#eeeeee] flex items-center gap-2">
                          <span>{cf.name}</span>
                          {cf.containsLanguageRule && (
                            <ArrLabel kind="purple" className="text-[10px]">
                              Language Rule
                            </ArrLabel>
                          )}
                        </div>
                      </td>
                      <td>
                        <ArrLabel kind={scoreKind} className="font-mono">
                          {scorePrefix}
                          {cf.defaultScore}
                        </ArrLabel>
                      </td>
                      <td>
                        <div className="text-[12px] text-[#b8b8b8]">
                          {cf.specifications?.length ? (
                            <span className="inline-flex flex-wrap gap-1 items-center">
                              <span className="font-medium text-[#e1e2e3]">
                                {cf.specifications.length} {cf.specifications.length === 1 ? 'spec' : 'specs'}:
                              </span>
                              {cf.specifications.map((s, idx) => (
                                <span
                                  key={idx}
                                  className="px-1.5 py-0.5 rounded bg-arr-card text-[11px] font-mono text-[#90c0ef]"
                                  title={`${s.implementation}: ${s.pattern || '(no pattern)'}${s.negate ? ' [NOT]' : ''}`}
                                >
                                  {s.negate ? '!' : ''}
                                  {s.implementation.replace('Specification', '')}
                                </span>
                              ))}
                            </span>
                          ) : (
                            <span className="text-[#666666]">None</span>
                          )}
                        </div>
                      </td>
                      <td className="font-mono text-[11px] text-[#909293]">
                        {cf.trashId ? (
                          <span className="px-1.5 py-0.5 rounded bg-arr-heading border border-[#444444]">
                            {cf.trashId}
                          </span>
                        ) : (
                          <span className="text-[#666666]">—</span>
                        )}
                      </td>
                      <td>
                        {cf.includeWhenRenaming ? (
                          <ArrLabel kind="primary">In Title</ArrLabel>
                        ) : (
                          <span className="text-[11px] text-[#666666]">No</span>
                        )}
                      </td>
                      <td className="text-right">
                        {canWriteProfiles && (
                          <div className="inline-flex items-center gap-1.5">
                            <ArrButton
                              className="h-[28px] px-2 text-[11px]"
                              onClick={() => openEditCf(cf)}
                            >
                              <Edit2 className="h-3 w-3" /> Edit
                            </ArrButton>
                            <ArrButton
                              variant="danger"
                              className="h-[28px] px-2 text-[11px]"
                              busy={deleteCfMutation.isPending && deleteCfMutation.variables === cf.id}
                              onClick={() => {
                                if (window.confirm(`Delete Custom Format "${cf.name}"?`)) {
                                  deleteCfMutation.mutate(cf.id);
                                }
                              }}
                            >
                              <Trash2 className="h-3 w-3" />
                            </ArrButton>
                          </div>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </FieldSet>

      {/* ====================================================================== */}
      {/* 2. RELEASE RESTRICTIONS (RELEASE PROFILES) CRUD                         */}
      {/* ====================================================================== */}
      <FieldSet
        legend={
          <div className="flex items-center gap-2">
            <Sliders className="h-5 w-5 text-[#ffc230]" />
            <span>Release Profiles &amp; Restrictions</span>
          </div>
        }
        action={
          canWriteProfiles && (
            <ArrButton variant="success" onClick={openNewRp}>
              <Plus className="h-3.5 w-3.5" /> Add Release Profile
            </ArrButton>
          )
        }
      >
        <p className="text-[13px] text-[#909293] mb-4">
          Strict filtering rules (Must Contain, Must Not Contain) and legacy Sonarr/Radarr Preferred Terms scoring applied across indexers or verticals.
        </p>

        {(releaseProfilesQuery.data ?? []).length === 0 ? (
          <div className="bg-arr-card border border-arr-card-border rounded-[4px] p-6 text-center text-[13px] text-[#909293]">
            No Release Profiles configured. Click <strong>Add Release Profile</strong> to define hard exclusion terms or indexer-specific restrictions.
          </div>
        ) : (
          <div className="overflow-x-auto border border-arr-card-border rounded-[4px]">
            <table className="arr-table w-full">
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Vertical / Indexer</th>
                  <th>Must Contain</th>
                  <th>Must Not Contain</th>
                  <th>Preferred Terms</th>
                  <th>Tags</th>
                  <th className="text-right">Actions</th>
                </tr>
              </thead>
              <tbody>
                {(releaseProfilesQuery.data ?? []).map((rp) => (
                  <tr key={rp.id} className="hover:bg-[#2b2b2b]">
                    <td>
                      <div className="font-semibold text-[#eeeeee] flex items-center gap-2">
                        <span>{rp.name}</span>
                        {!rp.isEnabled && <ArrLabel kind="default">Disabled</ArrLabel>}
                      </div>
                    </td>
                    <td>
                      <div className="flex flex-wrap gap-1">
                        <ArrLabel kind="default">{rp.mediaVertical ?? 'All Media'}</ArrLabel>
                        {rp.indexerId !== null && (
                          <ArrLabel kind="primary">
                            Indexer #
                            {indexersQuery.data?.find((i) => i.id === rp.indexerId)?.name ?? rp.indexerId}
                          </ArrLabel>
                        )}
                      </div>
                    </td>
                    <td>
                      {rp.mustContain?.length ? (
                        <div className="flex flex-wrap gap-1 max-w-[200px]">
                          {rp.mustContain.map((t, idx) => (
                            <span
                              key={idx}
                              className="px-1.5 py-0.5 rounded bg-[#1f3824] border border-[#27c24c]/40 text-[#a3e9b2] text-[11px] font-mono"
                            >
                              +{t}
                            </span>
                          ))}
                        </div>
                      ) : (
                        <span className="text-[#666666] text-[12px]">—</span>
                      )}
                    </td>
                    <td>
                      {rp.mustNotContain?.length ? (
                        <div className="flex flex-wrap gap-1 max-w-[200px]">
                          {rp.mustNotContain.map((t, idx) => (
                            <span
                              key={idx}
                              className="px-1.5 py-0.5 rounded bg-[#3b1e1e] border border-[#f05050]/40 text-[#f5a3a3] text-[11px] font-mono"
                            >
                              -{t}
                            </span>
                          ))}
                        </div>
                      ) : (
                        <span className="text-[#666666] text-[12px]">—</span>
                      )}
                    </td>
                    <td>
                      {rp.preferredTerms?.length ? (
                        <div className="flex flex-wrap gap-1 max-w-[240px]">
                          {rp.preferredTerms.map((pt, idx) => (
                            <span
                              key={idx}
                              className="px-1.5 py-0.5 rounded bg-[#2b2f38] text-[11px] font-mono text-[#89b4fa]"
                            >
                              {pt.term} ({pt.score > 0 ? `+${pt.score}` : pt.score})
                            </span>
                          ))}
                        </div>
                      ) : (
                        <span className="text-[#666666] text-[12px]">—</span>
                      )}
                    </td>
                    <td>
                      {rp.tags?.length ? (
                        <div className="flex flex-wrap gap-1">
                          {rp.tags.map((t, idx) => (
                            <ArrLabel key={idx} kind="default" className="text-[10px]">
                              {t}
                            </ArrLabel>
                          ))}
                        </div>
                      ) : (
                        <span className="text-[#666666] text-[12px]">—</span>
                      )}
                    </td>
                    <td className="text-right">
                      {canWriteProfiles && (
                        <div className="inline-flex items-center gap-1.5">
                          <ArrButton
                            className="h-[28px] px-2 text-[11px]"
                            onClick={() => openEditRp(rp)}
                          >
                            <Edit2 className="h-3 w-3" /> Edit
                          </ArrButton>
                          <ArrButton
                            variant="danger"
                            className="h-[28px] px-2 text-[11px]"
                            busy={deleteRpMutation.isPending && deleteRpMutation.variables === rp.id}
                            onClick={() => {
                              if (window.confirm(`Delete Release Profile "${rp.name}"?`)) {
                                deleteRpMutation.mutate(rp.id);
                              }
                            }}
                          >
                            <Trash2 className="h-3 w-3" />
                          </ArrButton>
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </FieldSet>

      {/* ====================================================================== */}
      {/* 3. PROTOCOL DELAY PROFILES CRUD                                        */}
      {/* ====================================================================== */}
      <FieldSet
        legend={
          <div className="flex items-center gap-2">
            <Clock className="h-5 w-5 text-[#27c24c]" />
            <span>Protocol Delay Profiles</span>
          </div>
        }
        action={
          canWriteProfiles && (
            <ArrButton variant="success" onClick={openNewDp}>
              <Plus className="h-3.5 w-3.5" /> Add Delay Profile
            </ArrButton>
          )
        }
      >
        <p className="text-[13px] text-[#909293] mb-4">
          Control grace periods before grabbing Usenet or Torrent releases to allow higher-quality releases (or custom format upgrades) to propagate.
        </p>

        {(delayProfilesQuery.data ?? []).length === 0 ? (
          <div className="bg-arr-card border border-arr-card-border rounded-[4px] p-6 text-center text-[13px] text-[#909293]">
            No Delay Profiles found.
          </div>
        ) : (
          <div className="overflow-x-auto border border-arr-card-border rounded-[4px]">
            <table className="arr-table w-full">
              <thead>
                <tr>
                  <th>Order / Name</th>
                  <th>Preferred Protocol</th>
                  <th>Usenet Delay</th>
                  <th>Torrent Delay</th>
                  <th>Bypass Rules</th>
                  <th>Tags</th>
                  <th className="text-right">Actions</th>
                </tr>
              </thead>
              <tbody>
                {(delayProfilesQuery.data ?? []).map((dp) => (
                  <tr key={dp.id} className="hover:bg-[#2b2b2b]">
                    <td>
                      <div className="flex items-center gap-2">
                        <span className="font-mono text-[12px] text-[#888888]">#{dp.sortOrder}</span>
                        <span className="font-semibold text-[#eeeeee]">{dp.name}</span>
                        {dp.isDefault && <ArrLabel kind="primary">Default</ArrLabel>}
                      </div>
                    </td>
                    <td>
                      <ArrLabel
                        kind={
                          dp.preferredProtocol === 'usenet'
                            ? 'usenet'
                            : dp.preferredProtocol === 'torrent'
                              ? 'torrent'
                              : 'default'
                        }
                      >
                        {dp.preferredProtocol.toUpperCase()}
                      </ArrLabel>
                    </td>
                    <td className="font-mono text-[12px]">
                      {dp.usenetDelayMinutes === 0 ? '0 min' : `${dp.usenetDelayMinutes} min`}
                    </td>
                    <td className="font-mono text-[12px]">
                      {dp.torrentDelayMinutes === 0 ? '0 min' : `${dp.torrentDelayMinutes} min`}
                    </td>
                    <td>
                      <div className="space-y-0.5 text-[11px] text-[#b8b8b8]">
                        {dp.bypassIfHighestQuality && <div>&bull; Bypass on highest quality</div>}
                        {dp.bypassIfAboveCustomFormatScore && (
                          <div>&bull; Bypass if CF score &ge; {dp.minCustomFormatScoreForBypass}</div>
                        )}
                        {!dp.bypassIfHighestQuality && !dp.bypassIfAboveCustomFormatScore && (
                          <span className="text-[#666666]">None</span>
                        )}
                      </div>
                    </td>
                    <td>
                      {dp.tags?.length ? (
                        <div className="flex flex-wrap gap-1">
                          {dp.tags.map((t, idx) => (
                            <ArrLabel key={idx} kind="default" className="text-[10px]">
                              {t}
                            </ArrLabel>
                          ))}
                        </div>
                      ) : (
                        <span className="text-[#666666] text-[12px]">—</span>
                      )}
                    </td>
                    <td className="text-right">
                      {canWriteProfiles && (
                        <div className="inline-flex items-center gap-1.5">
                          <ArrButton
                            className="h-[28px] px-2 text-[11px]"
                            onClick={() => openEditDp(dp)}
                          >
                            <Edit2 className="h-3 w-3" /> Edit
                          </ArrButton>
                          <ArrButton
                            variant="danger"
                            className="h-[28px] px-2 text-[11px]"
                            disabled={dp.isDefault}
                            title={dp.isDefault ? 'Cannot delete the default delay profile' : 'Delete'}
                            busy={deleteDpMutation.isPending && deleteDpMutation.variables === dp.id}
                            onClick={() => {
                              if (window.confirm(`Delete Delay Profile "${dp.name}"?`)) {
                                deleteDpMutation.mutate(dp.id);
                              }
                            }}
                          >
                            <Trash2 className="h-3 w-3" />
                          </ArrButton>
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </FieldSet>

      {/* ====================================================================== */}
      {/* 4. QUALITY SIZE LIMITS (MB/min)                                        */}
      {/* ====================================================================== */}
      <FieldSet
        legend={
          <div className="flex items-center gap-2">
            <HardDrive className="h-5 w-5 text-arr-accent" />
            <span>Quality Size Limits (MB/min)</span>
          </div>
        }
        action={
          canWriteProfiles && (
            <div className="flex items-center gap-2">
              <ArrButton
                variant="primary"
                busy={saveSizeLimitsMutation.isPending}
                onClick={() => saveSizeLimitsMutation.mutate()}
              >
                <Save className="h-3.5 w-3.5" /> Save Size Limits
              </ArrButton>
            </div>
          )
        }
      >
        <div className="flex flex-wrap items-center justify-between gap-3 mb-4">
          <p className="text-[13px] text-[#909293]">
            Target bitrates per quality tier in Megabytes per minute (MB/min). Releases exceeding Maximum or falling below Minimum are rejected. <span className="font-semibold text-[#eeeeee]">0 MB/min = Unlimited</span>.
          </p>

          <div className="inline-flex rounded-[3px] border border-[#444444] bg-[#222222] p-0.5">
            {(['video', 'music', 'ebooks', 'all'] as const).map((vert) => (
              <button
                key={vert}
                type="button"
                onClick={() => setSizeLimitsVertical(vert)}
                className={`px-3 py-1 text-[12px] font-medium rounded-[2px] transition-colors ${
                  sizeLimitsVertical === vert
                    ? 'bg-arr-accent text-arr-accent-fg font-semibold'
                    : 'text-[#888888] hover:text-[#cccccc]'
                }`}
              >
                {vert === 'all' ? 'All Verticals' : vert.toUpperCase()}
              </button>
            ))}
          </div>
        </div>

        {sizeSaveSuccess && (
          <ArrAlert kind="success" className="mb-4">
            Quality size limits successfully updated!
          </ArrAlert>
        )}

        <div className="overflow-x-auto border border-arr-card-border rounded-[4px]">
          <table className="arr-table w-full">
            <thead>
              <tr>
                <th>Quality Tier</th>
                <th>Vertical</th>
                <th className="w-[180px]">Minimum (MB/min)</th>
                <th className="w-[180px]">Preferred (MB/min)</th>
                <th className="w-[180px]">Maximum (MB/min)</th>
                <th className="text-right">Est. 60m / 120m Size</th>
              </tr>
            </thead>
            <tbody>
              {displayedSizeLimits.map((item) => {
                const est60mGb = ((item.preferredMbPerMin * 60) / 1024).toFixed(1);
                const est120mGb = ((item.preferredMbPerMin * 120) / 1024).toFixed(1);

                return (
                  <tr key={`${item.mediaVertical}-${item.qualityName}`} className="hover:bg-[#2b2b2b]">
                    <td>
                      <div className="font-semibold text-[#eeeeee]">{item.qualityName}</div>
                    </td>
                    <td>
                      <ArrLabel kind="default">{item.mediaVertical}</ArrLabel>
                    </td>
                    <td>
                      <div className="flex items-center gap-1.5">
                        <input
                          type="number"
                          min="0"
                          step="1"
                          value={item.minMbPerMin}
                          disabled={!canWriteProfiles}
                          onChange={(e) =>
                            updateSizeLimit(
                              item.qualityName,
                              item.mediaVertical,
                              'minMbPerMin',
                              Math.max(0, Number(e.target.value) || 0),
                            )
                          }
                          className="arr-input !w-[90px] !h-[28px] !py-1 text-center font-mono text-[12px]"
                        />
                        <span className="text-[11px] text-[#888888]">MB/m</span>
                      </div>
                    </td>
                    <td>
                      <div className="flex items-center gap-1.5">
                        <input
                          type="number"
                          min="0"
                          step="1"
                          value={item.preferredMbPerMin}
                          disabled={!canWriteProfiles}
                          onChange={(e) =>
                            updateSizeLimit(
                              item.qualityName,
                              item.mediaVertical,
                              'preferredMbPerMin',
                              Math.max(0, Number(e.target.value) || 0),
                            )
                          }
                          className="arr-input !w-[90px] !h-[28px] !py-1 text-center font-mono text-[12px] text-arr-accent"
                        />
                        <span className="text-[11px] text-[#888888]">MB/m</span>
                      </div>
                    </td>
                    <td>
                      <div className="flex items-center gap-1.5">
                        <input
                          type="number"
                          min="0"
                          step="1"
                          value={item.maxMbPerMin}
                          disabled={!canWriteProfiles}
                          onChange={(e) =>
                            updateSizeLimit(
                              item.qualityName,
                              item.mediaVertical,
                              'maxMbPerMin',
                              Math.max(0, Number(e.target.value) || 0),
                            )
                          }
                          className="arr-input !w-[90px] !h-[28px] !py-1 text-center font-mono text-[12px]"
                        />
                        <span className="text-[11px] text-[#888888]">
                          {item.maxMbPerMin === 0 ? '(unlimited)' : 'MB/m'}
                        </span>
                      </div>
                    </td>
                    <td className="text-right font-mono text-[12px] text-[#909293]">
                      {item.mediaVertical === 'video'
                        ? `${est60mGb} GB / ${est120mGb} GB`
                        : `${(item.preferredMbPerMin * 5).toFixed(1)} MB / track`}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </FieldSet>

      {/* ====================================================================== */}
      {/* 5. LANGUAGE POLICIES CRUD                                              */}
      {/* ====================================================================== */}
      <FieldSet
        legend={
          <div className="flex items-center gap-2">
            <Languages className="h-5 w-5 text-arr-accent" />
            <span>Language Policies</span>
          </div>
        }
        action={
          canWriteProfiles && (
            <ArrButton variant="success" onClick={openNewLp}>
              <Plus className="h-3.5 w-3.5" /> Add Language Policy
            </ArrButton>
          )
        }
      >
        <p className="text-[13px] text-[#909293] mb-4">
          Define ordered audio &amp; subtitle preference tiers, automatic language upgrade cutoffs, and quality step-down guardrails.
        </p>

        {lpBanner && (
          <ArrAlert kind={lpBanner.kind} className="mb-4">
            {lpBanner.text}
          </ArrAlert>
        )}

        <div className="overflow-x-auto border border-arr-card-border rounded-[4px]">
          <table className="arr-table w-full">
            <thead>
              <tr>
                <th>Policy Name</th>
                <th>Tiers</th>
                <th>Cutoff Tier</th>
                <th>Tradeoff Mode</th>
                <th>Min Quality Floor</th>
                <th className="text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {(languagePoliciesQuery.data ?? []).map((lp) => (
                <tr key={lp.id} className="hover:bg-[#2b2b2b]">
                  <td>
                    <div className="font-semibold text-[#eeeeee] flex items-center gap-2">
                      <span>{lp.name}</span>
                      {lp.isSystemPreset && <ArrLabel kind="primary">Preset</ArrLabel>}
                    </div>
                    <div className="text-[11px] font-mono text-[#909293]">{lp.slug}</div>
                  </td>
                  <td className="font-mono text-[12px]">{lp.tiers?.length ?? 0} tier(s)</td>
                  <td className="font-mono text-[12px]">Tier {lp.languageCutoffTier}</td>
                  <td>
                    <ArrLabel kind="default">{lp.tradeoffMode}</ArrLabel>
                  </td>
                  <td className="font-mono text-[12px]">{lp.minimumAbsoluteQuality || 'None'}</td>
                  <td className="text-right">
                    {canWriteProfiles && (
                      <div className="inline-flex items-center gap-1.5">
                        <ArrButton
                          className="h-[28px] px-2 text-[11px]"
                          onClick={() => openEditLp(lp)}
                        >
                          <Edit2 className="h-3 w-3" /> Edit
                        </ArrButton>
                        <ArrButton
                          variant="danger"
                          className="h-[28px] px-2 text-[11px]"
                          disabled={Boolean(lp.isSystemPreset)}
                          title={lp.isSystemPreset ? 'System preset policies cannot be deleted' : 'Delete policy'}
                          busy={deleteLpMutation.isPending && deleteLpMutation.variables === lp.id}
                          onClick={() => {
                            if (window.confirm(`Delete Language Policy "${lp.name}"?`)) {
                              deleteLpMutation.mutate(lp.id);
                            }
                          }}
                        >
                          <Trash2 className="h-3 w-3" />
                        </ArrButton>
                      </div>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </FieldSet>

      {/* ====================================================================== */}
      {/* 6. TRAIT MIXINS CRUD                                                   */}
      {/* ====================================================================== */}
      <FieldSet
        legend={
          <div className="flex items-center gap-2">
            <Sparkles className="h-5 w-5 text-[#9b6ef3]" />
            <span>Trait Mixins (Codec / HDR / Group Rule Packs)</span>
          </div>
        }
        action={
          canWriteProfiles && (
            <ArrButton variant="success" onClick={openNewMx}>
              <Plus className="h-3.5 w-3.5" /> Add Trait Mixin
            </ArrButton>
          )
        }
      >
        <p className="text-[13px] text-[#909293] mb-4">
          Composable regex scoring &amp; rejection rule packs attachable to multiple Quality Profiles.
        </p>

        {mxBanner && (
          <ArrAlert kind={mxBanner.kind} className="mb-4">
            {mxBanner.text}
          </ArrAlert>
        )}

        <div className="overflow-x-auto border border-arr-card-border rounded-[4px]">
          <table className="arr-table w-full">
            <thead>
              <tr>
                <th>Name</th>
                <th>Category</th>
                <th>Rules</th>
                <th className="text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {(mixinsQuery.data ?? []).map((mx) => (
                <tr key={mx.id} className="hover:bg-[#2b2b2b]">
                  <td>
                    <div className="font-semibold text-[#eeeeee]">{mx.name}</div>
                    <div className="text-[11px] font-mono text-[#909293]">{mx.slug}</div>
                  </td>
                  <td>
                    <ArrLabel kind="purple">{mx.category}</ArrLabel>
                  </td>
                  <td>
                    <div className="flex flex-wrap gap-1">
                      {(mx.rules ?? []).map((r, i) => (
                        <span
                          key={i}
                          className="px-1.5 py-0.5 rounded bg-arr-card text-[11px] font-mono text-[#cccccc]"
                        >
                          {r.label} ({r.score > 0 ? `+${r.score}` : r.score})
                        </span>
                      ))}
                    </div>
                  </td>
                  <td className="text-right">
                    {canWriteProfiles && (
                      <div className="inline-flex items-center gap-1.5">
                        <ArrButton
                          className="h-[28px] px-2 text-[11px]"
                          onClick={() => openEditMx(mx)}
                        >
                          <Edit2 className="h-3 w-3" /> Edit
                        </ArrButton>
                        <ArrButton
                          variant="danger"
                          className="h-[28px] px-2 text-[11px]"
                          busy={deleteMxMutation.isPending && deleteMxMutation.variables === mx.id}
                          onClick={() => {
                            if (window.confirm(`Delete Trait Mixin "${mx.name}"?`)) {
                              deleteMxMutation.mutate(mx.id);
                            }
                          }}
                        >
                          <Trash2 className="h-3 w-3" />
                        </ArrButton>
                      </div>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </FieldSet>

      {/* ====================================================================== */}
      {/* 7. VERIFICATION THRESHOLDS & DEFAULT FALLBACK PROFILE                  */}
      {/* ====================================================================== */}
      <FieldSet
        legend={
          <div className="flex items-center gap-2">
            <ShieldCheck className="h-5 w-5 text-[#27c24c]" />
            <span>Verification Thresholds &amp; Default Fallback Profile</span>
          </div>
        }
        action={
          canWriteProfiles && (
            <ArrButton
              variant="primary"
              busy={saveVerificationSettingsMutation.isPending}
              onClick={() => saveVerificationSettingsMutation.mutate()}
            >
              <Save className="h-3.5 w-3.5" /> Save Verification Settings
            </ArrButton>
          )
        }
      >
        {vsBanner && (
          <ArrAlert kind={vsBanner.kind} className="mb-4">
            {vsBanner.text}
          </ArrAlert>
        )}

        <div className="bg-arr-card border border-arr-card-border rounded-[4px] p-4 space-y-2">
          <FormGroup
            label="Auto-Pass Confidence"
            helpText="Minimum confidence (0.50–0.99) required for automatic VerifiedPass (default 0.80)."
          >
            <input
              type="number"
              step="0.01"
              min="0.50"
              max="0.99"
              disabled={!canWriteProfiles}
              value={vsAutoPass}
              onChange={(e) => setVsAutoPass(e.target.value)}
              className="arr-input font-mono !w-32"
            />
          </FormGroup>
          <FormGroup
            label="Quarantine Min Confidence"
            helpText="Borderline confidence lower bound (0.10–0.95) sent to QuarantineReview instead of hard-failing (default 0.45)."
          >
            <input
              type="number"
              step="0.01"
              min="0.10"
              max="0.95"
              disabled={!canWriteProfiles}
              value={vsQuarantineMin}
              onChange={(e) => setVsQuarantineMin(e.target.value)}
              className="arr-input font-mono !w-32"
            />
          </FormGroup>
          <FormGroup
            label="Auto-Patch MKV Header Confidence"
            helpText="Minimum Whisper speech confidence (0.50–0.99) to auto-patch 'und' MKV headers via mkvpropedit (default 0.85)."
          >
            <input
              type="number"
              step="0.01"
              min="0.50"
              max="0.99"
              disabled={!canWriteProfiles}
              value={vsAutoPatch}
              onChange={(e) => setVsAutoPatch(e.target.value)}
              className="arr-input font-mono !w-32"
            />
          </FormGroup>
          <FormGroup
            label="Fake Dub Correlation Threshold"
            helpText="Pearson waveform correlation (0.80–0.999) above which distinct language tracks are flagged as duplicate fake dubs (default 0.97)."
          >
            <input
              type="number"
              step="0.005"
              min="0.80"
              max="0.999"
              disabled={!canWriteProfiles}
              value={vsFakeDub}
              onChange={(e) => setVsFakeDub(e.target.value)}
              className="arr-input font-mono !w-32"
            />
          </FormGroup>
          <FormGroup
            label="Default Fallback Profile"
            helpText="Used during disk scans and policy checks when neither the work nor its library sets an explicit profile."
          >
            <select
              disabled={!canWriteProfiles}
              value={vsDefaultSlug}
              onChange={(e) => setVsDefaultSlug(e.target.value)}
              className="arr-input"
            >
              {(profileOptionsQuery.data ?? []).map((p) => (
                <option key={p.id} value={p.slug}>
                  {p.name} ({p.slug})
                </option>
              ))}
              {!(profileOptionsQuery.data ?? []).some((p) => p.slug === vsDefaultSlug) && (
                <option value={vsDefaultSlug}>{vsDefaultSlug}</option>
              )}
            </select>
          </FormGroup>
        </div>
      </FieldSet>

      {/* ====================================================================== */}
      {/* MODAL: ADD / EDIT CUSTOM FORMAT                                         */}
      {/* ====================================================================== */}
      {cfModalOpen && (
        <ArrModal
          title={editingCfId !== null ? `Edit Custom Format — ${cfName}` : 'Add Custom Format'}
          onClose={() => setCfModalOpen(false)}
          maxWidth="max-w-3xl"
          footer={
            <div className="w-full flex items-center justify-between">
              <div>
                {editingCfId !== null && (
                  <ArrButton
                    variant="danger"
                    busy={deleteCfMutation.isPending}
                    onClick={() => {
                      if (window.confirm(`Delete Custom Format "${cfName}"?`)) {
                        deleteCfMutation.mutate(editingCfId);
                        setCfModalOpen(false);
                      }
                    }}
                  >
                    <Trash2 className="h-3.5 w-3.5" /> Delete
                  </ArrButton>
                )}
              </div>
              <div className="flex items-center gap-2">
                <ArrButton onClick={() => setCfModalOpen(false)}>Cancel</ArrButton>
                <ArrButton
                  variant="primary"
                  busy={saveCfMutation.isPending}
                  disabled={!cfName.trim() || cfSpecs.length === 0}
                  onClick={() => saveCfMutation.mutate()}
                >
                  Save Custom Format
                </ArrButton>
              </div>
            </div>
          }
        >
          <div className="space-y-4">
            <FormGroup label="Format Name" helpText="e.g. HDR10+, Remux Tier 1, or German Atmos">
              <input
                value={cfName}
                onChange={(e) => setCfName(e.target.value)}
                placeholder="e.g. HDR10+"
                className="arr-input"
              />
            </FormGroup>

            <FormGroup label="TRaSH ID" helpText="Optional UUID or slug from TRaSH Guides (e.g. 5f20658f8b898a9b6c0e39300305b821)">
              <input
                value={cfTrashId}
                onChange={(e) => setCfTrashId(e.target.value)}
                placeholder="TRaSH UUID or leave blank"
                className="arr-input font-mono text-[12px]"
              />
            </FormGroup>

            <FormGroup label="Default Score" helpText="Positive value awards bonus points; negative value penalizes unwanted releases.">
              <input
                type="number"
                value={cfDefaultScore}
                onChange={(e) => setCfDefaultScore(Number(e.target.value) || 0)}
                className="arr-input font-mono !w-[160px]"
              />
            </FormGroup>

            <FormGroup label="Include in Renaming" helpText="Append custom format name token when renaming imported files on disk.">
              <label className="flex items-center gap-2 cursor-pointer mt-1">
                <input
                  type="checkbox"
                  checked={cfIncludeRenaming}
                  onChange={(e) => setCfIncludeRenaming(e.target.checked)}
                  className="accent-arr-accent h-4 w-4"
                />
                <span className="text-[13px] text-[#eeeeee]">Include when renaming files</span>
              </label>
            </FormGroup>

            <div className="pt-3 border-t border-arr-card-border">
              <div className="flex items-center justify-between mb-3">
                <h4 className="text-[14px] font-semibold text-[#e1e2e3]">Specifications</h4>
                <ArrButton variant="default" className="h-[28px] px-2.5 text-[12px]" onClick={addCfSpecRow}>
                  <Plus className="h-3 w-3" /> Add Specification
                </ArrButton>
              </div>

              <div className="space-y-3">
                {cfSpecs.map((spec, idx) => {
                  const implInfo = CF_SPEC_IMPLEMENTATIONS.find((i) => i.type === spec.implementation);

                  return (
                    <div
                      key={idx}
                      className="p-3 bg-arr-heading border border-[#3c3c3c] rounded-[4px] space-y-2.5"
                    >
                      <div className="flex flex-wrap items-center justify-between gap-2">
                        <div className="flex items-center gap-2">
                          <span className="text-[11px] font-mono text-[#888888]">#{idx + 1}</span>
                          <input
                            value={spec.name}
                            onChange={(e) => updateCfSpecRow(idx, { name: e.target.value })}
                            placeholder="Specification Label"
                            className="arr-input !h-[28px] !py-0 text-[12px] !w-[200px]"
                          />
                        </div>

                        <div className="flex items-center gap-3">
                          <label className="flex items-center gap-1.5 text-[12px] text-[#cccccc] cursor-pointer">
                            <input
                              type="checkbox"
                              checked={spec.negate}
                              onChange={(e) => updateCfSpecRow(idx, { negate: e.target.checked })}
                              className="accent-[#f05050]"
                            />
                            <span>Negate (NOT)</span>
                          </label>

                          <label className="flex items-center gap-1.5 text-[12px] text-[#cccccc] cursor-pointer">
                            <input
                              type="checkbox"
                              checked={spec.required}
                              onChange={(e) => updateCfSpecRow(idx, { required: e.target.checked })}
                              className="accent-arr-accent"
                            />
                            <span>Required</span>
                          </label>

                          {cfSpecs.length > 1 && (
                            <button
                              type="button"
                              onClick={() => removeCfSpecRow(idx)}
                              className="text-[#888888] hover:text-[#f05050] p-1 cursor-pointer"
                              title="Remove specification"
                            >
                              <Trash2 className="h-3.5 w-3.5" />
                            </button>
                          )}
                        </div>
                      </div>

                      <div className="grid grid-cols-1 md:grid-cols-[200px_1fr] gap-2 items-center">
                        <select
                          value={spec.implementation}
                          onChange={(e) =>
                            updateCfSpecRow(idx, {
                              implementation: e.target.value,
                              name: e.target.value.replace('Specification', ''),
                            })
                          }
                          className="arr-input !h-[30px] !py-0 text-[12px]"
                        >
                          {CF_SPEC_IMPLEMENTATIONS.map((imp) => (
                            <option key={imp.type} value={imp.type}>
                              {imp.label}
                            </option>
                          ))}
                        </select>

                        <input
                          value={spec.pattern}
                          onChange={(e) => updateCfSpecRow(idx, { pattern: e.target.value })}
                          placeholder={implInfo?.placeholder ?? 'Regex pattern...'}
                          className="arr-input !h-[30px] !py-0 font-mono text-[12px]"
                        />
                      </div>
                      {implInfo && (
                        <div className="text-[11px] text-[#777777] italic pl-1">
                          {implInfo.description}
                        </div>
                      )}
                    </div>
                  );
                })}
              </div>
            </div>
          </div>
        </ArrModal>
      )}

      {/* ====================================================================== */}
      {/* MODAL: ADD / EDIT RELEASE PROFILE                                       */}
      {/* ====================================================================== */}
      {rpModalOpen && (
        <ArrModal
          title={editingRpId !== null ? `Edit Release Profile — ${rpName}` : 'Add Release Profile'}
          onClose={() => setRpModalOpen(false)}
          maxWidth="max-w-2xl"
          footer={
            <div className="w-full flex items-center justify-between">
              <div>
                {editingRpId !== null && (
                  <ArrButton
                    variant="danger"
                    busy={deleteRpMutation.isPending}
                    onClick={() => {
                      if (window.confirm(`Delete Release Profile "${rpName}"?`)) {
                        deleteRpMutation.mutate(editingRpId);
                        setRpModalOpen(false);
                      }
                    }}
                  >
                    <Trash2 className="h-3.5 w-3.5" /> Delete
                  </ArrButton>
                )}
              </div>
              <div className="flex items-center gap-2">
                <ArrButton onClick={() => setRpModalOpen(false)}>Cancel</ArrButton>
                <ArrButton
                  variant="primary"
                  busy={saveRpMutation.isPending}
                  disabled={!rpName.trim()}
                  onClick={() => saveRpMutation.mutate()}
                >
                  Save Release Profile
                </ArrButton>
              </div>
            </div>
          }
        >
          <div className="space-y-3">
            <FormGroup label="Name" helpText="e.g. Anime Subtitle Rules, HDR Preference, or Anti-CAM">
              <input
                value={rpName}
                onChange={(e) => setRpName(e.target.value)}
                placeholder="e.g. UHD Preferences"
                className="arr-input"
              />
            </FormGroup>

            <FormGroup label="Status">
              <label className="flex items-center gap-2 cursor-pointer mt-1">
                <input
                  type="checkbox"
                  checked={rpEnabled}
                  onChange={(e) => setRpEnabled(e.target.checked)}
                  className="accent-arr-accent h-4 w-4"
                />
                <span className="text-[13px] text-[#eeeeee]">Enable this release profile</span>
              </label>
            </FormGroup>

            <FormGroup label="Media Vertical">
              <select
                value={rpVertical}
                onChange={(e) => setRpVertical(e.target.value)}
                className="arr-input"
              >
                <option value="all">All Media Verticals</option>
                <option value="movies">Movies</option>
                <option value="tv">TV Series</option>
                <option value="anime">Anime</option>
                <option value="music">Music</option>
                <option value="ebooks">Books &amp; Audiobooks</option>
              </select>
            </FormGroup>

            <FormGroup label="Indexer" helpText="Apply this profile to all indexers or restrict to a specific one.">
              <select
                value={rpIndexerId ?? ''}
                onChange={(e) => setRpIndexerId(e.target.value ? Number(e.target.value) : null)}
                className="arr-input"
              >
                <option value="">Any Indexer</option>
                {(indexersQuery.data ?? []).map((idx) => (
                  <option key={idx.id} value={idx.id}>
                    {idx.name}
                  </option>
                ))}
              </select>
            </FormGroup>

            <FormGroup label="Must Contain" helpText="Releases lacking any of these terms are immediately rejected. One term per line or comma-separated.">
              <textarea
                rows={3}
                value={rpMustContain}
                onChange={(e) => setRpMustContain(e.target.value)}
                placeholder="e.g.&#10;German&#10;DL"
                className="arr-input font-mono text-[12px]"
              />
            </FormGroup>

            <FormGroup label="Must Not Contain" helpText="Releases containing any of these terms are immediately rejected. One term per line or comma-separated.">
              <textarea
                rows={3}
                value={rpMustNotContain}
                onChange={(e) => setRpMustNotContain(e.target.value)}
                placeholder="e.g.&#10;CAM&#10;TeleSync&#10;HDCAM"
                className="arr-input font-mono text-[12px]"
              />
            </FormGroup>

            <div className="pt-3 border-t border-arr-card-border">
              <div className="flex items-center justify-between mb-2">
                <label className="text-[13px] font-bold text-[#e1e2e3]">Preferred Terms (Score)</label>
                <ArrButton
                  variant="default"
                  className="h-[26px] px-2 text-[11px]"
                  onClick={() =>
                    setRpPreferredTerms((prev) => [...prev, { term: '', score: 10 }])
                  }
                >
                  <Plus className="h-3 w-3" /> Add Term
                </ArrButton>
              </div>

              <div className="space-y-2">
                {rpPreferredTerms.map((pt, idx) => (
                  <div key={idx} className="flex items-center gap-2">
                    <input
                      value={pt.term}
                      onChange={(e) => {
                        const val = e.target.value;
                        setRpPreferredTerms((prev) =>
                          prev.map((item, i) => (i === idx ? { ...item, term: val } : item)),
                        );
                      }}
                      placeholder="e.g. Remux or x265"
                      className="arr-input !h-[30px] font-mono text-[12px] flex-1"
                    />
                    <input
                      type="number"
                      value={pt.score}
                      onChange={(e) => {
                        const val = Number(e.target.value) || 0;
                        setRpPreferredTerms((prev) =>
                          prev.map((item, i) => (i === idx ? { ...item, score: val } : item)),
                        );
                      }}
                      className="arr-input !h-[30px] !w-[90px] font-mono text-center text-[12px]"
                    />
                    <button
                      type="button"
                      onClick={() =>
                        setRpPreferredTerms((prev) => prev.filter((_, i) => i !== idx))
                      }
                      className="text-[#888888] hover:text-[#f05050] p-1 cursor-pointer"
                    >
                      <Trash2 className="h-3.5 w-3.5" />
                    </button>
                  </div>
                ))}
              </div>
            </div>

            <FormGroup label="Tags" helpText="Comma-separated tags (e.g. 4k, anime).">
              <input
                value={rpTags}
                onChange={(e) => setRpTags(e.target.value)}
                placeholder="e.g. 4k, uhd"
                className="arr-input text-[12px]"
              />
            </FormGroup>
          </div>
        </ArrModal>
      )}

      {/* ====================================================================== */}
      {/* MODAL: ADD / EDIT DELAY PROFILE                                         */}
      {/* ====================================================================== */}
      {dpModalOpen && (
        <ArrModal
          title={editingDpId !== null ? `Edit Delay Profile — ${dpName}` : 'Add Delay Profile'}
          onClose={() => setDpModalOpen(false)}
          maxWidth="max-w-xl"
          footer={
            <div className="w-full flex items-center justify-between">
              <div>
                {editingDpId !== null && !dpIsDefault && (
                  <ArrButton
                    variant="danger"
                    busy={deleteDpMutation.isPending}
                    onClick={() => {
                      if (window.confirm(`Delete Delay Profile "${dpName}"?`)) {
                        deleteDpMutation.mutate(editingDpId);
                        setDpModalOpen(false);
                      }
                    }}
                  >
                    <Trash2 className="h-3.5 w-3.5" /> Delete
                  </ArrButton>
                )}
              </div>
              <div className="flex items-center gap-2">
                <ArrButton onClick={() => setDpModalOpen(false)}>Cancel</ArrButton>
                <ArrButton
                  variant="primary"
                  busy={saveDpMutation.isPending}
                  disabled={!dpName.trim()}
                  onClick={() => saveDpMutation.mutate()}
                >
                  Save Delay Profile
                </ArrButton>
              </div>
            </div>
          }
        >
          <div className="space-y-3">
            <FormGroup label="Name">
              <input
                value={dpName}
                onChange={(e) => setDpName(e.target.value)}
                placeholder="e.g. Prefer Usenet over Torrent"
                className="arr-input"
              />
            </FormGroup>

            <FormGroup label="Preferred Protocol">
              <select
                value={dpProtocol}
                onChange={(e) => setDpProtocol(e.target.value as 'usenet' | 'torrent' | 'any')}
                className="arr-input"
              >
                <option value="any">Any (No protocol preference)</option>
                <option value="usenet">Usenet (Prioritize NZB)</option>
                <option value="torrent">Torrent (Prioritize Torrent)</option>
              </select>
            </FormGroup>

            <FormGroup label="Usenet Delay" helpText="Minutes to wait before grabbing Usenet releases (0 = immediate)">
              <div className="flex items-center gap-2">
                <input
                  type="number"
                  min="0"
                  value={dpUsenetDelay}
                  onChange={(e) => setDpUsenetDelay(Number(e.target.value) || 0)}
                  className="arr-input font-mono !w-[110px]"
                />
                <span className="text-[12px] text-[#888888]">minutes</span>
              </div>
            </FormGroup>

            <FormGroup label="Torrent Delay" helpText="Minutes to wait before grabbing Torrent releases (e.g. 120 = wait 2 hours)">
              <div className="flex items-center gap-2">
                <input
                  type="number"
                  min="0"
                  value={dpTorrentDelay}
                  onChange={(e) => setDpTorrentDelay(Number(e.target.value) || 0)}
                  className="arr-input font-mono !w-[110px]"
                />
                <span className="text-[12px] text-[#888888]">minutes</span>
              </div>
            </FormGroup>

            <FormGroup label="Bypass If Highest Quality" helpText="If release matches the cutoff quality tier in the quality profile, bypass delay immediately.">
              <label className="flex items-center gap-2 cursor-pointer mt-1">
                <input
                  type="checkbox"
                  checked={dpBypassQuality}
                  onChange={(e) => setDpBypassQuality(e.target.checked)}
                  className="accent-arr-accent h-4 w-4"
                />
                <span className="text-[13px] text-[#eeeeee]">Bypass delay for top-tier quality</span>
              </label>
            </FormGroup>

            <FormGroup label="Bypass On Score">
              <label className="flex items-center gap-2 cursor-pointer mt-1 mb-2">
                <input
                  type="checkbox"
                  checked={dpBypassCf}
                  onChange={(e) => setDpBypassCf(e.target.checked)}
                  className="accent-arr-accent h-4 w-4"
                />
                <span className="text-[13px] text-[#eeeeee]">Bypass if Custom Format score threshold met</span>
              </label>
              {dpBypassCf && (
                <div className="flex items-center gap-2 mt-1">
                  <span className="text-[12px] text-[#909293]">Min Score:</span>
                  <input
                    type="number"
                    value={dpMinCfScore}
                    onChange={(e) => setDpMinCfScore(Number(e.target.value) || 0)}
                    className="arr-input font-mono !w-[100px] !h-[28px] !py-0"
                  />
                </div>
              )}
            </FormGroup>

            <FormGroup label="Tags" helpText="Apply delay profile only to works with matching tags.">
              <input
                value={dpTags}
                onChange={(e) => setDpTags(e.target.value)}
                placeholder="e.g. torrent-only, delay-4k"
                className="arr-input text-[12px]"
              />
            </FormGroup>

            <FormGroup label="Sort Order" helpText="Lower number profiles are evaluated first.">
              <input
                type="number"
                value={dpSortOrder}
                onChange={(e) => setDpSortOrder(Number(e.target.value) || 1)}
                className="arr-input font-mono !w-[90px]"
              />
            </FormGroup>
          </div>
        </ArrModal>
      )}

      {/* ====================================================================== */}
      {/* MODAL: IMPORT TRASH JSON                                                */}
      {/* ====================================================================== */}
      {showTrashModal && (
        <ArrModal
          title="Import TRaSH-Guides Custom Format JSON"
          onClose={() => setShowTrashModal(false)}
          maxWidth="max-w-xl"
          footer={
            <>
              <ArrButton onClick={() => setShowTrashModal(false)}>Cancel</ArrButton>
              <ArrButton
                variant="primary"
                busy={importTrashMutation.isPending}
                disabled={!trashJson.trim()}
                onClick={() => importTrashMutation.mutate()}
              >
                Import JSON
              </ArrButton>
            </>
          }
        >
          <p className="text-[13px] text-[#909293] mb-3">
            Paste a TRaSH-Guides or Recyclarr Custom Format JSON object or array. Specifications and scores will be parsed and loaded into Managerr:
          </p>
          <textarea
            rows={8}
            value={trashJson}
            onChange={(e) => setTrashJson(e.target.value)}
            placeholder='{"trash_id": "...", "name": "HDR10+", "specifications": [...]}'
            className="arr-input font-mono text-[12px]"
          />
        </ArrModal>
      )}

      {/* ====================================================================== */}
      {/* MODAL: ADD / EDIT LANGUAGE POLICY                                      */}
      {/* ====================================================================== */}
      {lpModalOpen && (
        <ArrModal
          title={editingLpId !== null ? `Edit Language Policy — ${lpName}` : 'Add Language Policy'}
          onClose={() => setLpModalOpen(false)}
          maxWidth="max-w-3xl"
          footer={
            <>
              <ArrButton onClick={() => setLpModalOpen(false)}>Cancel</ArrButton>
              <ArrButton
                variant="primary"
                disabled={!lpName.trim() || lpTiers.length === 0}
                busy={saveLpMutation.isPending}
                onClick={() => saveLpMutation.mutate()}
              >
                Save Language Policy
              </ArrButton>
            </>
          }
        >
          <div className="space-y-3">
            <FormGroup label="Policy Name">
              <input
                value={lpName}
                onChange={(e) => setLpName(e.target.value)}
                placeholder="e.g. German + Original Dual-Audio"
                className="arr-input"
              />
            </FormGroup>
            <FormGroup label="Slug" helpText="Optional unique identifier slug.">
              <input
                value={lpSlug}
                onChange={(e) => setLpSlug(e.target.value)}
                placeholder="auto-generated if blank"
                className="arr-input font-mono text-[12px]"
              />
            </FormGroup>
            <FormGroup label="Description">
              <input
                value={lpDesc}
                onChange={(e) => setLpDesc(e.target.value)}
                className="arr-input"
              />
            </FormGroup>
            <FormGroup label="Language Upgrades">
              <label className="inline-flex items-center gap-2 pt-1.5 text-[13px] cursor-pointer">
                <input
                  type="checkbox"
                  checked={lpEnableUpgrades}
                  onChange={(e) => setLpEnableUpgrades(e.target.checked)}
                />
                Enable automatic language upgrades until Cutoff Tier
              </label>
            </FormGroup>
            <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
              <FormGroup label="Cutoff Tier">
                <input
                  type="number"
                  min="1"
                  value={lpCutoffTier}
                  onChange={(e) => setLpCutoffTier(Number(e.target.value) || 1)}
                  className="arr-input font-mono"
                />
              </FormGroup>
              <FormGroup label="Tradeoff Mode">
                <select
                  value={lpTradeoffMode}
                  onChange={(e) => setLpTradeoffMode(e.target.value)}
                  className="arr-input"
                >
                  <option value="bounded_stepdown">Bounded Stepdown</option>
                  <option value="never_downgrade_quality">Never Downgrade Quality</option>
                  <option value="prefer_language_over_quality">Prefer Language Over Quality</option>
                </select>
              </FormGroup>
              <FormGroup label="Max Quality Drop">
                <input
                  type="number"
                  min="0"
                  value={lpMaxDrop}
                  onChange={(e) => setLpMaxDrop(Number(e.target.value) || 0)}
                  className="arr-input font-mono"
                />
              </FormGroup>
            </div>
            <FormGroup label="Minimum Quality Floor" helpText="e.g. WEBDL-1080p or leave blank.">
              <input
                value={lpMinQuality}
                onChange={(e) => setLpMinQuality(e.target.value)}
                placeholder="WEBDL-1080p"
                className="arr-input font-mono"
              />
            </FormGroup>

            <div className="pt-3 border-t border-arr-card-border">
              <div className="flex items-center justify-between mb-2">
                <h4 className="text-[13px] font-semibold text-[#eeeeee]">Priority Tiers</h4>
                <ArrButton
                  className="h-[26px] px-2 text-[11px]"
                  onClick={() =>
                    setLpTiers((prev) => [
                      ...prev,
                      {
                        tier: prev.length + 1,
                        id: `tier-${prev.length + 1}`,
                        label: `Tier ${prev.length + 1}`,
                        audioAll: '',
                        audioAny: 'original, en',
                        subsAny: '',
                        score: 300,
                        satisfiesCutoff: false,
                      },
                    ])
                  }
                >
                  <Plus className="h-3 w-3" /> Add Tier
                </ArrButton>
              </div>
              <div className="space-y-2.5">
                {lpTiers.map((t, idx) => (
                  <div key={idx} className="p-2.5 bg-arr-heading border border-[#3c3c3c] rounded space-y-2">
                    <div className="flex flex-wrap items-center gap-2">
                      <input
                        type="number"
                        min="1"
                        value={t.tier}
                        onChange={(e) =>
                          setLpTiers((prev) =>
                            prev.map((x, i) => (i === idx ? { ...x, tier: Number(e.target.value) || 1 } : x)),
                          )
                        }
                        className="arr-input !w-16 !h-[28px] font-mono text-[12px]"
                        title="Tier priority"
                      />
                      <input
                        value={t.label}
                        onChange={(e) =>
                          setLpTiers((prev) =>
                            prev.map((x, i) => (i === idx ? { ...x, label: e.target.value } : x)),
                          )
                        }
                        placeholder="Tier Label"
                        className="arr-input !h-[28px] text-[12px] flex-1"
                      />
                      <input
                        type="number"
                        value={t.score}
                        onChange={(e) =>
                          setLpTiers((prev) =>
                            prev.map((x, i) => (i === idx ? { ...x, score: Number(e.target.value) || 0 } : x)),
                          )
                        }
                        className="arr-input !w-24 !h-[28px] font-mono text-[12px]"
                        title="Score"
                      />
                      <label className="inline-flex items-center gap-1 text-[11px] text-[#cccccc]">
                        <input
                          type="checkbox"
                          checked={t.satisfiesCutoff}
                          onChange={(e) =>
                            setLpTiers((prev) =>
                              prev.map((x, i) => (i === idx ? { ...x, satisfiesCutoff: e.target.checked } : x)),
                            )
                          }
                        />
                        Cutoff
                      </label>
                      {lpTiers.length > 1 && (
                        <button
                          type="button"
                          onClick={() => setLpTiers((prev) => prev.filter((_, i) => i !== idx))}
                          className="text-[#888888] hover:text-[#f05050] p-1"
                        >
                          <Trash2 className="h-3.5 w-3.5" />
                        </button>
                      )}
                    </div>
                    <div className="grid grid-cols-1 sm:grid-cols-3 gap-2">
                      <input
                        value={t.audioAll}
                        onChange={(e) =>
                          setLpTiers((prev) =>
                            prev.map((x, i) => (i === idx ? { ...x, audioAll: e.target.value } : x)),
                          )
                        }
                        placeholder="Audio ALL (de, original)"
                        className="arr-input !h-[28px] font-mono text-[11px]"
                      />
                      <input
                        value={t.audioAny}
                        onChange={(e) =>
                          setLpTiers((prev) =>
                            prev.map((x, i) => (i === idx ? { ...x, audioAny: e.target.value } : x)),
                          )
                        }
                        placeholder="Audio ANY (en, original)"
                        className="arr-input !h-[28px] font-mono text-[11px]"
                      />
                      <input
                        value={t.subsAny}
                        onChange={(e) =>
                          setLpTiers((prev) =>
                            prev.map((x, i) => (i === idx ? { ...x, subsAny: e.target.value } : x)),
                          )
                        }
                        placeholder="Subs ANY (de, en)"
                        className="arr-input !h-[28px] font-mono text-[11px]"
                      />
                    </div>
                  </div>
                ))}
              </div>
            </div>
          </div>
        </ArrModal>
      )}

      {/* ====================================================================== */}
      {/* MODAL: ADD / EDIT TRAIT MIXIN                                          */}
      {/* ====================================================================== */}
      {mxModalOpen && (
        <ArrModal
          title={editingMxId !== null ? `Edit Trait Mixin — ${mxName}` : 'Add Trait Mixin'}
          onClose={() => setMxModalOpen(false)}
          maxWidth="max-w-3xl"
          footer={
            <>
              <ArrButton onClick={() => setMxModalOpen(false)}>Cancel</ArrButton>
              <ArrButton
                variant="primary"
                disabled={!mxName.trim()}
                busy={saveMxMutation.isPending}
                onClick={() => saveMxMutation.mutate()}
              >
                Save Trait Mixin
              </ArrButton>
            </>
          }
        >
          <div className="space-y-3">
            <FormGroup label="Mixin Name">
              <input
                value={mxName}
                onChange={(e) => setMxName(e.target.value)}
                placeholder="e.g. Modern Codec Efficiency"
                className="arr-input"
              />
            </FormGroup>
            <FormGroup label="Category">
              <select
                value={mxCategory}
                onChange={(e) => setMxCategory(e.target.value)}
                className="arr-input"
              >
                <option value="codec">Codec</option>
                <option value="hdr_audio">HDR &amp; Audio</option>
                <option value="release_group">Release Group</option>
                <option value="custom">Custom</option>
              </select>
            </FormGroup>
            <div className="pt-3 border-t border-arr-card-border">
              <div className="flex items-center justify-between mb-2">
                <h4 className="text-[13px] font-semibold text-[#eeeeee]">Rules</h4>
                <ArrButton
                  className="h-[26px] px-2 text-[11px]"
                  onClick={() =>
                    setMxRules((prev) => [
                      ...prev,
                      {
                        id: `rule-${prev.length + 1}`,
                        label: '',
                        field: 'RawTitle',
                        pattern: '',
                        score: 100,
                        requiredReject: false,
                      },
                    ])
                  }
                >
                  <Plus className="h-3 w-3" /> Add Rule
                </ArrButton>
              </div>
              <div className="space-y-2">
                {mxRules.map((r, idx) => (
                  <div key={idx} className="p-2.5 bg-arr-heading border border-[#3c3c3c] rounded space-y-2">
                    <div className="flex flex-wrap items-center gap-2">
                      <input
                        value={r.label}
                        onChange={(e) =>
                          setMxRules((prev) =>
                            prev.map((x, i) => (i === idx ? { ...x, label: e.target.value } : x)),
                          )
                        }
                        placeholder="Rule Label"
                        className="arr-input !h-[28px] text-[12px] flex-1"
                      />
                      <select
                        value={r.field}
                        onChange={(e) =>
                          setMxRules((prev) =>
                            prev.map((x, i) => (i === idx ? { ...x, field: e.target.value } : x)),
                          )
                        }
                        className="arr-input !w-36 !h-[28px] text-[12px]"
                      >
                        <option value="VideoCodec">VideoCodec</option>
                        <option value="HdrFormat">HdrFormat</option>
                        <option value="AudioCodec">AudioCodec</option>
                        <option value="ReleaseGroup">ReleaseGroup</option>
                        <option value="MovieEdition">MovieEdition</option>
                        <option value="RawTitle">RawTitle</option>
                      </select>
                      <input
                        type="number"
                        value={r.score}
                        onChange={(e) =>
                          setMxRules((prev) =>
                            prev.map((x, i) => (i === idx ? { ...x, score: Number(e.target.value) || 0 } : x)),
                          )
                        }
                        className="arr-input !w-24 !h-[28px] font-mono text-[12px]"
                      />
                      <label className="inline-flex items-center gap-1 text-[11px] text-[#f05050]">
                        <input
                          type="checkbox"
                          checked={r.requiredReject}
                          onChange={(e) =>
                            setMxRules((prev) =>
                              prev.map((x, i) => (i === idx ? { ...x, requiredReject: e.target.checked } : x)),
                            )
                          }
                        />
                        Reject
                      </label>
                      <button
                        type="button"
                        onClick={() => setMxRules((prev) => prev.filter((_, i) => i !== idx))}
                        className="text-[#888888] hover:text-[#f05050] p-1"
                      >
                        <Trash2 className="h-3.5 w-3.5" />
                      </button>
                    </div>
                    <input
                      value={r.pattern}
                      onChange={(e) =>
                        setMxRules((prev) =>
                          prev.map((x, i) => (i === idx ? { ...x, pattern: e.target.value } : x)),
                        )
                      }
                      placeholder="Regex pattern (e.g. ^AV1$)"
                      className="arr-input !h-[28px] font-mono text-[11px]"
                    />
                  </div>
                ))}
              </div>
            </div>
          </div>
        </ArrModal>
      )}
    </div>
  );
}
