import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Check, FileAudio, Play, Plus, RefreshCw, Trash2, Upload, X } from 'lucide-react';
import { apiFetch } from '../lib/api';
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
import { ProfilesAdvancedPanels } from './ProfilesAdvancedPanels';

interface AllowedQualityTier {
  name: string;
  rank: number;
  score: number;
  minMbps?: number | null;
  maxMbps?: number | null;
}

interface LanguagePolicyTier {
  tier: number;
  id: string;
  label: string;
  audioContainsAll: string[];
  audioContainsAny: string[];
  subtitlesContainsAny: string[];
  score: number;
  satisfiesLanguageCutoff: boolean;
}

interface LanguagePolicySummary {
  id: number;
  slug: string;
  name: string;
  description: string;
  tiers: LanguagePolicyTier[];
  enableLanguageUpgrades: boolean;
  languageCutoffTier: number;
  tradeoffMode: string;
  maxQualityTierDrop: number;
  minimumAbsoluteQuality: string | null;
}

interface QualityProfileSummary {
  id: number;
  slug: string;
  name: string;
  mediaVertical: string;
  isSystemPreset: boolean;
  allowedQualities: AllowedQualityTier[];
  qualityCutoff: string;
  languagePolicyId: number | null;
  languagePolicy: LanguagePolicySummary | null;
  mixinIds: number[];
  weightQuality: number;
  weightLanguage: number;
  weightTraits: number;
  weightEfficiency: number;
  minTraitScore?: number;
  customFormatScores?: Record<number, number>;
  retainAudioLangs: string[];
  retainSubLangs: string[];
}

interface ProfileMixinSummaryItem {
  id: number;
  slug: string;
  name: string;
  category: string;
}

interface CustomFormatScoreTarget {
  id: number;
  name: string;
  defaultScore: number;
}

interface ReleaseSimulationScorecard {
  approved: boolean;
  decisionKind: string;
  explanation: string;
  canonicalQualityName: string;
  qualityRank: number;
  matchedLanguageTier: number | null;
  matchedLanguageTierLabel: string | null;
  candidateSatisfiesQualityCutoff: boolean;
  candidateSatisfiesLanguageCutoff: boolean;
  existingQualityCutoffMet: boolean;
  existingLanguageCutoffMet: boolean;
  qualityScore: number;
  languageScore: number;
  traitScore: number;
  efficiencyScore: number;
  totalWeightedScore: number;
  matchedTraitRules: string[];
  hardGateFailures: string[];
}

interface MediaFileVerificationReport {
  jobId: number;
  stagedFilePath: string;
  mediaVertical: string;
  verdict: string;
  overallConfidence: number;
  headerAutoPatched: boolean;
  hashBlocklisted: boolean;
  quarantineId: number | null;
  durationMs: number;
  verifiedAudioLanguages: string[];
  verifiedSubtitleLanguages: string[];
  verifiedAt: string;
}

interface QuarantinedImportSummary {
  id: number;
  releaseTitle: string;
  infoHash: string | null;
  stagedFilePath: string;
  expectedLanguages: string[];
  detectedLanguages: string[];
  confidence: number;
  reason: string;
  snippetPaths: string[];
  status: string;
  createdAt: string;
}

interface TrashImportResult {
  importedCount: number;
  updatedCount: number;
  languageRulesDetectedForUplift: string[];
}

interface ProfileEditorState {
  editingId: number | null;
  isSystemPreset: boolean;
  name: string;
  mediaVertical: 'video' | 'music' | 'ebooks';
  selectedQualities: string[];
  qualityCutoff: string;
  languagePolicyId: number | null;
  mixinIds: number[];
  weightQuality: number;
  weightLanguage: number;
  weightTraits: number;
  weightEfficiency: number;
  minTraitScore: number;
  customFormatScores: Record<number, number>;
  retainAudioLangsText: string;
  retainSubLangsText: string;
}

const QUALITY_CATALOG: Record<'video' | 'music' | 'ebooks', Array<{ name: string; score: number }>> = {
  video: [
    { name: 'Remux-2160p', score: 1000 },
    { name: 'BluRay-2160p', score: 920 },
    { name: 'WEBDL-2160p', score: 860 },
    { name: 'WEBRip-2160p', score: 800 },
    { name: 'Remux-1080p', score: 750 },
    { name: 'BluRay-1080p', score: 700 },
    { name: 'WEBDL-1080p', score: 650 },
    { name: 'WEBRip-1080p', score: 580 },
    { name: 'HDTV-1080p', score: 520 },
    { name: 'BluRay-720p', score: 420 },
    { name: 'WEBDL-720p', score: 380 },
    { name: 'HDTV-720p', score: 340 },
  ],
  music: [
    { name: 'FLAC 24bit', score: 1000 },
    { name: 'FLAC', score: 900 },
    { name: 'MP3-320', score: 600 },
    { name: 'MP3-V0', score: 500 },
  ],
  ebooks: [
    { name: 'EPUB', score: 950 },
    { name: 'M4B', score: 950 },
    { name: 'AZW3', score: 800 },
    { name: 'CBZ', score: 900 },
    { name: 'PDF', score: 500 },
  ],
};

export type ProfilesSubSection = 'profiles' | 'quarantine';

export function ProfilesAndQuarantineView({
  section = 'profiles',
  canReadProfiles,
  canWriteProfiles,
  canReadVerification,
  canReviewQuarantine,
}: {
  section?: ProfilesSubSection;
  canReadProfiles: boolean;
  canWriteProfiles: boolean;
  canReadVerification: boolean;
  canReviewQuarantine: boolean;
}) {
  const qc = useQueryClient();
  const [selectedProfileId, setSelectedProfileId] = useState<number>(0);
  const [editorState, setEditorState] = useState<ProfileEditorState | null>(null);
  const [releaseTitle, setReleaseTitle] = useState(
    'Dune.Part.Two.2024.German.DL.2160p.UHD.BluRay.REMUX.DV.HDR10Plus.TrueHD.Atmos.HEVC-GRP.mkv',
  );
  const [origLang, setOrigLang] = useState('en');
  const [existingQuality, setExistingQuality] = useState('');
  const [existingLangs, setExistingLangs] = useState('en');
  const [showTrashModal, setShowTrashModal] = useState(false);
  const [trashJson, setTrashJson] = useState('');
  const [confirmLang, setConfirmLang] = useState('de');

  const profilesQuery = useQuery({
    queryKey: ['profiles'],
    queryFn: () => apiFetch<QualityProfileSummary[]>('/api/v1/profiles'),
    enabled: canReadProfiles && section === 'profiles',
  });

  const policiesQuery = useQuery({
    queryKey: ['language-policies'],
    queryFn: () => apiFetch<LanguagePolicySummary[]>('/api/v1/profiles/language-policies'),
    enabled: canReadProfiles && section === 'profiles',
  });

  const mixinsQuery = useQuery({
    queryKey: ['profile-mixins'],
    queryFn: () => apiFetch<ProfileMixinSummaryItem[]>('/api/v1/profiles/mixins'),
    enabled: canReadProfiles && section === 'profiles',
  });

  const customFormatsQuery = useQuery({
    queryKey: ['custom-formats'],
    queryFn: () => apiFetch<CustomFormatScoreTarget[]>('/api/v1/profiles/custom-formats'),
    enabled: canReadProfiles && section === 'profiles',
  });

  useEffect(() => {
    if (!profilesQuery.data || profilesQuery.data.length === 0) return;
    if (selectedProfileId === 0 || !profilesQuery.data.some((p) => p.id === selectedProfileId)) {
      const germanDefault = profilesQuery.data.find((p) => p.slug === 'german-first-best-quality');
      setSelectedProfileId((germanDefault ?? profilesQuery.data[0]).id);
    }
  }, [profilesQuery.data, selectedProfileId]);

  const reportsQuery = useQuery({
    queryKey: ['verification-reports'],
    queryFn: () => apiFetch<MediaFileVerificationReport[]>('/api/v1/verification/reports'),
    enabled: canReadVerification && section === 'quarantine',
  });

  const quarantineQuery = useQuery({
    queryKey: ['quarantine'],
    queryFn: () => apiFetch<QuarantinedImportSummary[]>('/api/v1/quarantine'),
    enabled: canReviewQuarantine && section === 'quarantine',
  });

  function openNewProfileEditor() {
    const germanPolicy = (policiesQuery.data ?? []).find((lp) => lp.slug === 'german-first');
    const defaultVideoQuals = QUALITY_CATALOG.video.slice(0, 9).map((q) => q.name);
    setEditorState({
      editingId: null,
      isSystemPreset: false,
      name: '',
      mediaVertical: 'video',
      selectedQualities: defaultVideoQuals,
      qualityCutoff: 'Remux-2160p',
      languagePolicyId: germanPolicy?.id ?? policiesQuery.data?.[0]?.id ?? null,
      mixinIds: [],
      weightQuality: 1.0,
      weightLanguage: 2.0,
      weightTraits: 1.0,
      weightEfficiency: 1.0,
      minTraitScore: 0,
      customFormatScores: {},
      retainAudioLangsText: 'original, en',
      retainSubLangsText: 'en',
    });
  }

  function openEditProfileEditor(p: QualityProfileSummary) {
    setSelectedProfileId(p.id);
    if (!canWriteProfiles) return;
    const vert: 'video' | 'music' | 'ebooks' =
      p.mediaVertical === 'music' ? 'music' : p.mediaVertical === 'ebooks' ? 'ebooks' : 'video';
    const quals = p.allowedQualities?.length
      ? [...p.allowedQualities].sort((a, b) => b.rank - a.rank).map((q) => q.name)
      : QUALITY_CATALOG[vert].map((q) => q.name);
    setEditorState({
      editingId: p.id,
      isSystemPreset: p.isSystemPreset,
      name: p.name,
      mediaVertical: vert,
      selectedQualities: quals,
      qualityCutoff: p.qualityCutoff,
      languagePolicyId: p.languagePolicyId ?? p.languagePolicy?.id ?? null,
      mixinIds: p.mixinIds ? [...p.mixinIds] : [],
      weightQuality: p.weightQuality ?? 1.0,
      weightLanguage: p.weightLanguage ?? 1.0,
      weightTraits: p.weightTraits ?? 1.0,
      weightEfficiency: p.weightEfficiency ?? 1.0,
      minTraitScore: p.minTraitScore ?? 0,
      customFormatScores: p.customFormatScores ? { ...p.customFormatScores } : {},
      retainAudioLangsText: (p.retainAudioLangs?.length ? p.retainAudioLangs : ['original', 'en']).join(', '),
      retainSubLangsText: (p.retainSubLangs?.length ? p.retainSubLangs : ['en']).join(', '),
    });
  }

  const saveProfileMutation = useMutation({
    mutationFn: (state: ProfileEditorState) => {
      const catalog = QUALITY_CATALOG[state.mediaVertical];
      const ordered = catalog.filter((c) => state.selectedQualities.includes(c.name));
      const allowedQualities: AllowedQualityTier[] = ordered.map((item, idx) => ({
        name: item.name,
        rank: ordered.length - idx,
        score: item.score,
      }));
      const cutoff = ordered.some((o) => o.name === state.qualityCutoff)
        ? state.qualityCutoff
        : (ordered[0]?.name ?? 'WEBDL-1080p');

      const retainAudioLangs = state.retainAudioLangsText
        .split(',')
        .map((s) => s.trim().toLowerCase())
        .filter(Boolean);
      const retainSubLangs = state.retainSubLangsText
        .split(',')
        .map((s) => s.trim().toLowerCase())
        .filter(Boolean);

      const body = {
        name: state.name.trim(),
        mediaVertical: state.mediaVertical,
        allowedQualities,
        qualityCutoff: cutoff,
        languagePolicyId: state.languagePolicyId,
        mixinIds: state.mixinIds,
        weightQuality: Number(state.weightQuality) || 1.0,
        weightLanguage: Number(state.weightLanguage) || 1.0,
        weightTraits: Number(state.weightTraits) || 1.0,
        weightEfficiency: Number(state.weightEfficiency) || 1.0,
        minTraitScore: Number(state.minTraitScore) || 0,
        customFormatScores: state.customFormatScores,
        retainAudioLangs: retainAudioLangs.length > 0 ? retainAudioLangs : ['original', 'en'],
        retainSubLangs: retainSubLangs.length > 0 ? retainSubLangs : ['en'],
      };

      if (state.editingId !== null) {
        return apiFetch<QualityProfileSummary>(`/api/v1/profiles/${state.editingId}`, {
          method: 'PUT',
          body: JSON.stringify(body),
        });
      }
      return apiFetch<QualityProfileSummary>('/api/v1/profiles', {
        method: 'POST',
        body: JSON.stringify(body),
      });
    },
    onSuccess: (saved) => {
      setEditorState(null);
      setSelectedProfileId(saved.id);
      qc.invalidateQueries({ queryKey: ['profiles'] });
    },
  });

  const deleteProfileMutation = useMutation({
    mutationFn: (id: number) => apiFetch(`/api/v1/profiles/${id}`, { method: 'DELETE' }),
    onSuccess: () => {
      setEditorState(null);
      qc.invalidateQueries({ queryKey: ['profiles'] });
    },
  });

  const simulateMutation = useMutation({
    mutationFn: () =>
      apiFetch<ReleaseSimulationScorecard>('/api/v1/profiles/simulate', {
        method: 'POST',
        body: JSON.stringify({
          profileId: selectedProfileId || profilesQuery.data?.[0]?.id || 1,
          releaseTitle,
          originalLanguage: origLang,
          existingFileQuality: existingQuality.trim() || null,
          existingFileAudioLanguages: existingLangs
            .split(',')
            .map((s) => s.trim())
            .filter(Boolean),
        }),
      }),
  });

  const trashMutation = useMutation({
    mutationFn: () =>
      apiFetch<TrashImportResult>('/api/v1/profiles/import-trash', {
        method: 'POST',
        body: JSON.stringify({ rawJson: trashJson }),
      }),
    onSuccess: () => {
      setTrashJson('');
      setShowTrashModal(false);
      qc.invalidateQueries({ queryKey: ['profiles'] });
    },
  });

  const approveMutation = useMutation({
    mutationFn: (id: number) =>
      apiFetch(`/api/v1/quarantine/${id}/approve`, {
        method: 'POST',
        body: JSON.stringify({ confirmedLanguage: confirmLang }),
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['quarantine'] }),
  });

  const rejectMutation = useMutation({
    mutationFn: (id: number) =>
      apiFetch(`/api/v1/quarantine/${id}/reject`, {
        method: 'POST',
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['quarantine'] }),
  });

  return (
    <div className="flex flex-col flex-1">
      <PageToolbar
        left={
          <>
            <PageToolbarButton
              icon={RefreshCw}
              label="Refresh"
              spinning={profilesQuery.isFetching || quarantineQuery.isFetching || reportsQuery.isFetching}
              onClick={() => {
                if (section === 'profiles') {
                  void profilesQuery.refetch();
                  void policiesQuery.refetch();
                }
                if (section === 'quarantine') {
                  void quarantineQuery.refetch();
                  void reportsQuery.refetch();
                }
              }}
            />

            {section === 'profiles' && canReadProfiles && (
              <>
                <PageToolbarSeparator />
                {canWriteProfiles && (
                  <PageToolbarButton icon={Plus} label="Add Profile" onClick={openNewProfileEditor} />
                )}
                <PageToolbarButton
                  icon={Play}
                  label="Test Decision"
                  spinning={simulateMutation.isPending}
                  onClick={() => simulateMutation.mutate()}
                />
                {canWriteProfiles && (
                  <PageToolbarButton
                    icon={Upload}
                    label="Import TRaSH"
                    onClick={() => setShowTrashModal(true)}
                  />
                )}
              </>
            )}
          </>
        }
      />

      <PageContentBody>
        {section === 'profiles' && canReadProfiles && (
          <>
            {trashMutation.data && (
              <ArrAlert kind="success" className="mb-4">
                TRaSH Custom Formats imported: {trashMutation.data.importedCount} added,{' '}
                {trashMutation.data.updatedCount} updated.
              </ArrAlert>
            )}

            <FieldSet legend="Quality & Language Profiles">
              <div className="flex flex-wrap gap-4">
                {(profilesQuery.data ?? []).map((p) => {
                  const isGermanDefault = p.slug === 'german-first-best-quality';
                  return (
                    <ArrSettingsCard
                      key={p.id}
                      title={p.name}
                      onClick={() => openEditProfileEditor(p)}
                      topRight={
                        isGermanDefault ? (
                          <ArrLabel kind="primary">Movies &amp; Series</ArrLabel>
                        ) : !p.isSystemPreset ? (
                          <ArrLabel kind="purple">Custom</ArrLabel>
                        ) : undefined
                      }
                      footer={
                        <>
                          <span className="text-[11px] text-[#909293]">Upgrade Until: {p.qualityCutoff}</span>
                          <ArrLabel kind="default">
                            {p.mediaVertical === 'video' ? 'movies + tv' : p.mediaVertical}
                          </ArrLabel>
                        </>
                      }
                    >
                      <div className="flex flex-wrap gap-1 pt-0.5">
                        {p.languagePolicy ? (
                          <ArrLabel kind="success">{p.languagePolicy.name}</ArrLabel>
                        ) : (
                          <ArrLabel kind="default">Quality Only</ArrLabel>
                        )}
                        {p.languagePolicy?.enableLanguageUpgrades && (
                          <ArrLabel kind="warning">Lang Upgrade</ArrLabel>
                        )}
                      </div>
                      <div className="text-[11px] text-[#909293] pt-1">
                        {p.languagePolicy?.minimumAbsoluteQuality ? (
                          <>
                            Floor: <strong className="text-[#cccccc]">{p.languagePolicy.minimumAbsoluteQuality}</strong>{' '}
                            •{' '}
                          </>
                        ) : null}
                        Lang Weight: <strong className="text-[#cccccc]">{p.weightLanguage}x</strong>
                      </div>
                    </ArrSettingsCard>
                  );
                })}

                {canWriteProfiles && <ArrAddCard label="Add Quality Profile" onClick={openNewProfileEditor} />}
              </div>
            </FieldSet>

            <FieldSet legend="Release Decision Simulator">
              <FormGroup label="Profile">
                <select
                  value={selectedProfileId}
                  onChange={(e) => setSelectedProfileId(Number(e.target.value))}
                  className="arr-input"
                >
                  {(profilesQuery.data ?? []).map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.name} ({p.mediaVertical === 'video' ? 'Movies & Series' : p.mediaVertical})
                    </option>
                  ))}
                </select>
              </FormGroup>

              <FormGroup label="Candidate Release">
                <input
                  value={releaseTitle}
                  onChange={(e) => setReleaseTitle(e.target.value)}
                  className="arr-input font-mono text-[13px]"
                />
              </FormGroup>

              <FormGroup label="Original Language" helpText="ISO language code of the source work (e.g. en, ja).">
                <input
                  value={origLang}
                  onChange={(e) => setOrigLang(e.target.value)}
                  className="arr-input"
                />
              </FormGroup>

              <FormGroup
                label="Existing File on Disk"
                helpText="Leave quality blank to simulate a fresh grab, or enter quality + audio languages to test upgrades."
              >
                <div className="grid grid-cols-2 gap-2">
                  <input
                    value={existingQuality}
                    onChange={(e) => setExistingQuality(e.target.value)}
                    placeholder="None (Fresh Grab)"
                    className="arr-input"
                  />
                  <input
                    value={existingLangs}
                    onChange={(e) => setExistingLangs(e.target.value)}
                    placeholder="en"
                    className="arr-input"
                  />
                </div>
              </FormGroup>

              <FormGroup label="">
                <ArrButton
                  variant="primary"
                  busy={simulateMutation.isPending}
                  onClick={() => simulateMutation.mutate()}
                >
                  Evaluate Release
                </ArrButton>
              </FormGroup>

              {simulateMutation.data && (
                <div className="mt-4 overflow-x-auto">
                  <table className="arr-table">
                    <thead>
                      <tr>
                        <th>Verdict</th>
                        <th>Decision Type</th>
                        <th>Quality</th>
                        <th>Language Tier</th>
                        <th>Axis Scores (Q / L / T / E)</th>
                        <th>Weighted Total</th>
                        <th>Explanation</th>
                      </tr>
                    </thead>
                    <tbody>
                      <tr>
                        <td>
                          <ArrLabel kind={simulateMutation.data.approved ? 'success' : 'danger'}>
                            {simulateMutation.data.approved ? 'Approved' : 'Rejected'}
                          </ArrLabel>
                        </td>
                        <td className="font-semibold text-[#eeeeee]">{simulateMutation.data.decisionKind}</td>
                        <td>{simulateMutation.data.canonicalQualityName}</td>
                        <td>{simulateMutation.data.matchedLanguageTierLabel ?? '-'}</td>
                        <td className="font-mono text-[12px]">
                          {simulateMutation.data.qualityScore} / {simulateMutation.data.languageScore} /{' '}
                          {simulateMutation.data.traitScore} / {simulateMutation.data.efficiencyScore}
                        </td>
                        <td className="font-mono font-bold text-arr-accent">
                          {simulateMutation.data.totalWeightedScore.toFixed(1)}
                        </td>
                        <td className="text-[12px] text-[#cccccc]">{simulateMutation.data.explanation}</td>
                      </tr>
                    </tbody>
                  </table>
                </div>
              )}
            </FieldSet>
            <ProfilesAdvancedPanels
              canReadProfiles={canReadProfiles}
              canWriteProfiles={canWriteProfiles}
            />

            {/* ==================== ADD / EDIT QUALITY PROFILE MODAL ==================== */}
            {editorState && (
              <ArrModal
                title={
                  editorState.editingId !== null
                    ? `Edit Quality Profile — ${editorState.name || 'Profile'}`
                    : 'Add Quality Profile'
                }
                onClose={() => setEditorState(null)}
                maxWidth="max-w-2xl"
                footer={
                  <div className="w-full flex items-center justify-between gap-2">
                    <div>
                      {editorState.editingId !== null && !editorState.isSystemPreset && (
                        <ArrButton
                          variant="danger"
                          busy={deleteProfileMutation.isPending}
                          onClick={() => deleteProfileMutation.mutate(editorState.editingId!)}
                        >
                          <Trash2 className="h-3.5 w-3.5" /> Delete
                        </ArrButton>
                      )}
                    </div>
                    <div className="flex items-center gap-2 ml-auto">
                      <ArrButton onClick={() => setEditorState(null)}>Cancel</ArrButton>
                      <ArrButton
                        variant="primary"
                        busy={saveProfileMutation.isPending}
                        disabled={!editorState.name.trim() || editorState.selectedQualities.length === 0}
                        onClick={() => saveProfileMutation.mutate(editorState)}
                      >
                        Save
                      </ArrButton>
                    </div>
                  </div>
                }
              >
                <FormGroup label="Name">
                  <input
                    value={editorState.name}
                    onChange={(e) => setEditorState({ ...editorState, name: e.target.value })}
                    placeholder="e.g. German First — Best Quality"
                    className="arr-input"
                  />
                </FormGroup>

                <FormGroup label="Applies To" helpText="'Movies & TV Series' shares one profile across both Movies and Series.">
                  <select
                    value={editorState.mediaVertical}
                    onChange={(e) => {
                      const nextVert = e.target.value as 'video' | 'music' | 'ebooks';
                      const nextQuals = QUALITY_CATALOG[nextVert].map((q) => q.name);
                      setEditorState({
                        ...editorState,
                        mediaVertical: nextVert,
                        selectedQualities: nextQuals,
                        qualityCutoff: nextQuals[0],
                      });
                    }}
                    className="arr-input"
                  >
                    <option value="video">Movies &amp; TV Series (Video)</option>
                    <option value="music">Music (Lossless / Audio)</option>
                    <option value="ebooks">Books &amp; Audiobooks</option>
                  </select>
                </FormGroup>

                <FormGroup label="Language Policy">
                  <select
                    value={editorState.languagePolicyId ?? ''}
                    onChange={(e) =>
                      setEditorState({
                        ...editorState,
                        languagePolicyId: e.target.value ? Number(e.target.value) : null,
                      })
                    }
                    className="arr-input"
                  >
                    <option value="">Any Language (Quality Only)</option>
                    {(policiesQuery.data ?? []).map((lp) => (
                      <option key={lp.id} value={lp.id}>
                        {lp.name}
                      </option>
                    ))}
                  </select>
                </FormGroup>

                <FormGroup
                  label="Allowed Qualities"
                  helpText="Ordered from highest quality at the top to lowest acceptable quality at the bottom."
                >
                  <div className="space-y-1.5 max-h-[240px] overflow-y-auto border border-arr-card-border rounded-[4px] p-2.5 bg-[#222222]">
                    {QUALITY_CATALOG[editorState.mediaVertical].map((q) => {
                      const checked = editorState.selectedQualities.includes(q.name);
                      return (
                        <label
                          key={q.name}
                          className="flex items-center justify-between px-2 py-1 rounded hover:bg-[#2f2f2f] cursor-pointer text-[13px]"
                        >
                          <span className="inline-flex items-center gap-2">
                            <input
                              type="checkbox"
                              checked={checked}
                              onChange={() => {
                                const next = checked
                                  ? editorState.selectedQualities.filter((x) => x !== q.name)
                                  : [...editorState.selectedQualities, q.name];
                                setEditorState({ ...editorState, selectedQualities: next });
                              }}
                              className="accent-arr-accent"
                            />
                            <span className={checked ? 'text-[#eeeeee] font-medium' : 'text-[#888888]'}>
                              {q.name}
                            </span>
                          </span>
                          <span className="font-mono text-[11px] text-[#909293]">{q.score} pts</span>
                        </label>
                      );
                    })}
                  </div>
                </FormGroup>

                <FormGroup label="Upgrade Until (Cutoff)" helpText="Once this quality is reached (and language cutoff is met), upgrades stop.">
                  <select
                    value={editorState.qualityCutoff}
                    onChange={(e) => setEditorState({ ...editorState, qualityCutoff: e.target.value })}
                    className="arr-input"
                  >
                    {QUALITY_CATALOG[editorState.mediaVertical]
                      .filter((q) => editorState.selectedQualities.includes(q.name))
                      .map((q) => (
                        <option key={q.name} value={q.name}>
                          {q.name}
                        </option>
                      ))}
                  </select>
                </FormGroup>

                <FormGroup
                  label="Axis Weights & Min Trait Score"
                  helpText="2.0x Language Weight guarantees German releases outrank higher-resolution non-German releases."
                >
                  <div className="grid grid-cols-2 sm:grid-cols-5 gap-2">
                    <div>
                      <div className="text-[11px] text-[#909293] mb-1">Language</div>
                      <input
                        type="number"
                        step="0.25"
                        min="0"
                        max="5"
                        value={editorState.weightLanguage}
                        onChange={(e) =>
                          setEditorState({ ...editorState, weightLanguage: Number(e.target.value) || 0 })
                        }
                        className="arr-input font-mono"
                      />
                    </div>
                    <div>
                      <div className="text-[11px] text-[#909293] mb-1">Quality</div>
                      <input
                        type="number"
                        step="0.25"
                        min="0.25"
                        max="5"
                        value={editorState.weightQuality}
                        onChange={(e) =>
                          setEditorState({ ...editorState, weightQuality: Number(e.target.value) || 1.0 })
                        }
                        className="arr-input font-mono"
                      />
                    </div>
                    <div>
                      <div className="text-[11px] text-[#909293] mb-1">Traits</div>
                      <input
                        type="number"
                        step="0.25"
                        min="0"
                        max="5"
                        value={editorState.weightTraits}
                        onChange={(e) =>
                          setEditorState({ ...editorState, weightTraits: Number(e.target.value) || 0 })
                        }
                        className="arr-input font-mono"
                      />
                    </div>
                    <div>
                      <div className="text-[11px] text-[#909293] mb-1">Efficiency</div>
                      <input
                        type="number"
                        step="0.25"
                        min="0"
                        max="5"
                        value={editorState.weightEfficiency}
                        onChange={(e) =>
                          setEditorState({ ...editorState, weightEfficiency: Number(e.target.value) || 0 })
                        }
                        className="arr-input font-mono"
                      />
                    </div>
                    <div>
                      <div className="text-[11px] text-[#909293] mb-1">Min Trait Score</div>
                      <input
                        type="number"
                        step="10"
                        value={editorState.minTraitScore}
                        onChange={(e) =>
                          setEditorState({ ...editorState, minTraitScore: Number(e.target.value) || 0 })
                        }
                        className="arr-input font-mono"
                      />
                    </div>
                  </div>
                </FormGroup>

                {(mixinsQuery.data ?? []).length > 0 && (
                  <FormGroup
                    label="Trait Mixins"
                    helpText="Attach composable codec, HDR, or release-group mixin rule packs to this profile."
                  >
                    <div className="space-y-1.5 max-h-[150px] overflow-y-auto border border-arr-card-border rounded-[4px] p-2.5 bg-[#222222]">
                      {(mixinsQuery.data ?? []).map((mx) => {
                        const checked = editorState.mixinIds.includes(mx.id);
                        return (
                          <label
                            key={mx.id}
                            className="flex items-center justify-between px-2 py-1 rounded hover:bg-[#2f2f2f] cursor-pointer text-[12px]"
                          >
                            <span className="inline-flex items-center gap-2">
                              <input
                                type="checkbox"
                                checked={checked}
                                onChange={() => {
                                  const next = checked
                                    ? editorState.mixinIds.filter((id) => id !== mx.id)
                                    : [...editorState.mixinIds, mx.id];
                                  setEditorState({ ...editorState, mixinIds: next });
                                }}
                                className="accent-arr-accent"
                              />
                              <span className="text-[#eeeeee]">{mx.name}</span>
                            </span>
                            <ArrLabel kind="default">{mx.category}</ArrLabel>
                          </label>
                        );
                      })}
                    </div>
                  </FormGroup>
                )}

                {(customFormatsQuery.data ?? []).length > 0 && (
                  <FormGroup
                    label="Custom Format Scores"
                    helpText="Optional per-profile score overrides (leave blank to use format default score)."
                  >
                    <div className="space-y-1.5 max-h-[150px] overflow-y-auto border border-arr-card-border rounded-[4px] p-2.5 bg-[#222222]">
                      {(customFormatsQuery.data ?? []).map((cf) => {
                        const hasOverride = Object.prototype.hasOwnProperty.call(
                          editorState.customFormatScores,
                          cf.id,
                        );
                        return (
                          <div key={cf.id} className="flex items-center justify-between gap-2 text-[12px]">
                            <span className="text-[#eeeeee] truncate">{cf.name}</span>
                            <input
                              type="number"
                              value={hasOverride ? editorState.customFormatScores[cf.id] : ''}
                              placeholder={`Default (${cf.defaultScore})`}
                              onChange={(e) => {
                                const next = { ...editorState.customFormatScores };
                                if (e.target.value.trim() === '') {
                                  delete next[cf.id];
                                } else {
                                  next[cf.id] = Number(e.target.value) || 0;
                                }
                                setEditorState({ ...editorState, customFormatScores: next });
                              }}
                              className="arr-input !h-[26px] !py-0 !w-28 font-mono text-[11px]"
                            />
                          </div>
                        );
                      })}
                    </div>
                  </FormGroup>
                )}

                <FormGroup
                  label="Retained Languages"
                  helpText="Comma-separated ISO codes retained during muxing/remuxing (e.g. original, en, de)."
                >
                  <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
                    <div>
                      <div className="text-[11px] text-[#909293] mb-1">Retain Audio Langs</div>
                      <input
                        value={editorState.retainAudioLangsText}
                        onChange={(e) =>
                          setEditorState({ ...editorState, retainAudioLangsText: e.target.value })
                        }
                        placeholder="original, en"
                        className="arr-input font-mono text-[12px]"
                      />
                    </div>
                    <div>
                      <div className="text-[11px] text-[#909293] mb-1">Retain Subtitle Langs</div>
                      <input
                        value={editorState.retainSubLangsText}
                        onChange={(e) =>
                          setEditorState({ ...editorState, retainSubLangsText: e.target.value })
                        }
                        placeholder="en"
                        className="arr-input font-mono text-[12px]"
                      />
                    </div>
                  </div>
                </FormGroup>
              </ArrModal>
            )}

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
                      busy={trashMutation.isPending}
                      disabled={!trashJson.trim()}
                      onClick={() => trashMutation.mutate()}
                    >
                      Import JSON
                    </ArrButton>
                  </>
                }
              >
                <p className="text-[13px] text-[#909293] mb-3">
                  Paste a TRaSH-Guides or Recyclarr Custom Format JSON object or array:
                </p>
                <textarea
                  rows={7}
                  value={trashJson}
                  onChange={(e) => setTrashJson(e.target.value)}
                  placeholder='{"trash_id": "...", "name": "...", "specifications": [...]}'
                  className="arr-input font-mono text-[12px]"
                />
              </ArrModal>
            )}
          </>
        )}

        {section === 'quarantine' && (
          <>
            {canReviewQuarantine && (
              <FieldSet
                legend="Language Quarantine"
                action={
                  <div className="flex items-center gap-2 text-[12px]">
                    <span className="text-[#909293]">Confirmed ISO:</span>
                    <input
                      value={confirmLang}
                      onChange={(e) => setConfirmLang(e.target.value)}
                      className="arr-input !w-20 !h-[28px] !py-1 text-center font-mono"
                    />
                  </div>
                }
              >
                {(quarantineQuery.data ?? []).length === 0 ? (
                  <div className="text-[13px] text-[#909293]">No quarantined imports in queue.</div>
                ) : (
                  <div className="overflow-x-auto">
                    <table className="arr-table">
                      <thead>
                        <tr>
                          <th>Release</th>
                          <th>Expected / Detected</th>
                          <th>Confidence</th>
                          <th>Audio Snippets</th>
                          <th>Status</th>
                          <th className="text-right">Actions</th>
                        </tr>
                      </thead>
                      <tbody>
                        {(quarantineQuery.data ?? []).map((q) => (
                          <tr key={q.id}>
                            <td>
                              <div className="font-semibold text-[#eeeeee]">{q.releaseTitle}</div>
                              <div className="text-[11px] text-[#909293]">{q.reason}</div>
                            </td>
                            <td className="font-mono text-[12px]">
                              [{q.expectedLanguages.join(', ')}] &rarr; [{q.detectedLanguages.join(', ')}]
                            </td>
                            <td>{(q.confidence * 100).toFixed(0)}%</td>
                            <td>
                              <div className="flex flex-wrap gap-1">
                                {q.snippetPaths.map((_, idx) => (
                                  <a
                                    key={idx}
                                    href={`/api/v1/quarantine/${q.id}/snippets/${idx}`}
                                    target="_blank"
                                    rel="noreferrer"
                                    className="inline-flex items-center gap-1 px-2 py-0.5 bg-arr-card hover:bg-[#444444] border border-[#4a5158] rounded-[3px] text-[11px] text-arr-accent"
                                  >
                                    <FileAudio className="h-3 w-3" /> #{idx + 1}.opus
                                  </a>
                                ))}
                              </div>
                            </td>
                            <td>
                              <ArrLabel kind={q.status === 'quarantined' ? 'warning' : 'success'}>
                                {q.status}
                              </ArrLabel>
                            </td>
                            <td className="text-right">
                              {q.status === 'quarantined' && (
                                <div className="inline-flex items-center gap-1.5">
                                  <ArrButton
                                    variant="success"
                                    className="h-[26px] px-2 text-[11px]"
                                    onClick={() => approveMutation.mutate(q.id)}
                                  >
                                    <Check className="h-3.5 w-3.5" /> Approve ({confirmLang})
                                  </ArrButton>
                                  <ArrButton
                                    variant="danger"
                                    className="h-[26px] px-2 text-[11px]"
                                    onClick={() => rejectMutation.mutate(q.id)}
                                  >
                                    <X className="h-3.5 w-3.5" /> Blocklist
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
            )}

            {canReadVerification && (
              <FieldSet legend="Verification History">
                {(reportsQuery.data ?? []).length === 0 ? (
                  <div className="text-[13px] text-[#909293]">No verification scans recorded yet.</div>
                ) : (
                  <div className="overflow-x-auto">
                    <table className="arr-table">
                      <thead>
                        <tr>
                          <th>File</th>
                          <th>Vertical</th>
                          <th>Audio Tracks</th>
                          <th>Subtitles</th>
                          <th>Confidence</th>
                          <th>Header Patch</th>
                          <th>Verdict</th>
                        </tr>
                      </thead>
                      <tbody>
                        {(reportsQuery.data ?? []).map((r) => (
                          <tr key={r.jobId}>
                            <td className="font-mono text-[12px] text-[#eeeeee]">{r.stagedFilePath}</td>
                            <td>
                              <ArrLabel kind="default">{r.mediaVertical}</ArrLabel>
                            </td>
                            <td className="font-mono text-[12px]">{r.verifiedAudioLanguages.join(', ') || '-'}</td>
                            <td className="font-mono text-[12px]">{r.verifiedSubtitleLanguages.join(', ') || '-'}</td>
                            <td>{(r.overallConfidence * 100).toFixed(0)}%</td>
                            <td>{r.headerAutoPatched ? <ArrLabel kind="primary">mkvpropedit</ArrLabel> : '-'}</td>
                            <td>
                              <ArrLabel
                                kind={
                                  r.verdict.startsWith('VerifiedPass') || r.verdict === 'MusicPolicyPass'
                                    ? 'success'
                                    : r.verdict === 'VerifiedFail'
                                      ? 'danger'
                                      : 'warning'
                                }
                              >
                                {r.verdict}
                              </ArrLabel>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
              </FieldSet>
            )}
          </>
        )}
      </PageContentBody>
    </div>
  );
}
