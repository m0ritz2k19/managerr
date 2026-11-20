import { useId, useMemo, useState, type KeyboardEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  AlertTriangle,
  CheckCircle2,
  Clock,
  Copy,
  Eye,
  FileCode,
  FileSearch,
  History,
  Play,
  Plus,
  RefreshCw,
  Save,
  Shield,
  Sparkles,
  Trash2,
} from 'lucide-react';
import { apiFetch, formatBytes } from '../../lib/api';
import {
  ArrAlert,
  ArrButton,
  ArrLabel,
  ArrModal,
  FieldSet,
  PageToolbar,
  PageToolbarButton,
  PageToolbarSeparator,
} from '../arr/ArrPrimitives';
import type { QualityProfileSummary } from '../catalog/catalogTypesAndHelpers';
import { CleanerrConfirmModal } from './CleanerrConfirmModal';
import { CleanerrDagStudio, type CleanerrDagGraph } from './CleanerrDagStudio';
import { CleanerrManualStageModal } from './CleanerrManualStageModal';
import { CleanerrRuleBuilder } from './CleanerrRuleBuilder';
import { CleanerrTestMediaModal } from './CleanerrTestMediaModal';
import { CleanerrYamlModal } from './CleanerrYamlModal';
import type {
  CleanerrLiveImpactPreview,
  CleanerrNodeCatalogEntry,
  CleanerrRuleCatalogResponse,
  CleanerrRuleGroupSpec,
  CleanerrRunResult,
  CleanerrStatusResponse,
  CleanerrTemplate,
  CleanerrWorkflowSummary,
  LibraryOption,
} from './cleanerrTypes';

export interface CleanerrStudioSectionProps {
  status?: CleanerrStatusResponse;
}

const CRON_PRESETS: Array<{ value: string; label: string }> = [
  { value: '0 3 * * *', label: 'Daily at 03:00 UTC (0 3 * * *)' },
  { value: '0 */6 * * *', label: 'Every 6 Hours UTC (0 */6 * * *)' },
  { value: '0 2 * * 1', label: 'Weekly Monday 02:00 UTC (0 2 * * 1)' },
  { value: '', label: 'Manual Only (No Cron)' },
];

const DEFAULT_RULE_GROUP: CleanerrRuleGroupSpec = {
  targetMediaType: 'movie',
  graceDays: 7,
  actionMode: 'delete_files_and_unmonitor',
  cleanEmptyFolders: true,
  resetRequests: true,
  pruneTorrentClient: false,
  sections: [
    {
      sectionOperator: 'AND',
      title: 'Section 1',
      rules: [
        {
          operator: null,
          field: 'plex.daysSinceLastActivity',
          comparator: 'BIGGER',
          value: '90',
        },
        {
          operator: 'AND',
          field: 'plex.isInProgress',
          comparator: 'EQUALS',
          value: 'false',
        },
      ],
    },
  ],
};

function buildGraphFromRuleGroup(
  spec: CleanerrRuleGroupSpec,
  scheduleCron: string,
): CleanerrDagGraph {
  const nodes: Array<Record<string, unknown>> = [];
  const edges: Array<Record<string, unknown>> = [];

  if (scheduleCron.trim()) {
    nodes.push({
      id: 'n_trigger',
      type: 'cleanerrNode',
      position: { x: 40, y: 120 },
      data: {
        label: `Cron (${scheduleCron.trim()})`,
        node_type: 'trigger.schedule',
        config: { cron: scheduleCron.trim() },
      },
    });
  } else {
    nodes.push({
      id: 'n_trigger',
      type: 'cleanerrNode',
      position: { x: 40, y: 120 },
      data: {
        label: 'Manual Trigger',
        node_type: 'trigger.manual',
        config: { allow_api: true },
      },
    });
  }

  nodes.push({
    id: 'n_source',
    type: 'cleanerrNode',
    position: { x: 280, y: 120 },
    data: {
      label: `Catalog: ${spec.targetMediaType}`,
      node_type: 'source.managerr_catalog',
      config: {
        media_type: spec.targetMediaType,
        library_ids: spec.libraryIds ?? [],
      },
    },
  });
  edges.push({ id: 'e_trig_src', source: 'n_trigger', target: 'n_source' });

  nodes.push({
    id: 'n_rules',
    type: 'cleanerrNode',
    position: { x: 540, y: 120 },
    data: {
      label: `Maintainerr Rules (${spec.sections.length} section(s))`,
      node_type: 'filter.maintainerr_rules',
      config: { ...spec },
    },
  });
  edges.push({ id: 'e_src_rules', source: 'n_source', target: 'n_rules' });

  nodes.push({
    id: 'n_excl',
    type: 'cleanerrNode',
    position: { x: 800, y: 120 },
    data: {
      label: 'Exclusion Rules Guard',
      node_type: 'filter.exclusion_check',
      config: {
        respect_global_exclusions: true,
        respect_user_quotas: true,
        excluded_tags: ['keep', 'protected'],
      },
    },
  });
  edges.push({ id: 'e_rules_excl', source: 'n_rules', target: 'n_excl' });

  let prevId = 'n_excl';
  if (spec.graceDays > 0) {
    nodes.push({
      id: 'n_grace',
      type: 'cleanerrNode',
      position: { x: 1060, y: 120 },
      data: {
        label: `${spec.graceDays}d Leaving Soon Grace`,
        node_type: 'queue.grace_period',
        config: {
          grace_days: spec.graceDays,
          auto_rescue_on_watch: true,
        },
      },
    });
    edges.push({ id: 'e_excl_grace', source: prevId, target: 'n_grace' });
    prevId = 'n_grace';
  }

  nodes.push({
    id: 'n_action',
    type: 'cleanerrNode',
    position: { x: spec.graceDays > 0 ? 1320 : 1060, y: 120 },
    data: {
      label: `Action: ${spec.actionMode}`,
      node_type: 'action.managerr',
      config: {
        mode: spec.actionMode,
        clean_empty_folders: spec.cleanEmptyFolders ?? true,
        reset_requests: spec.resetRequests ?? true,
        prune_torrent_client: spec.pruneTorrentClient ?? false,
        target_profile_id: spec.downgradeProfileId ?? null,
      },
    },
  });
  edges.push({ id: 'e_prev_act', source: prevId, target: 'n_action' });

  return { nodes, edges };
}

function normalizeTargetMediaType(mt: unknown): 'movie' | 'show' {
  return mt === 'movie' ? 'movie' : 'show';
}

function normalizeRuleGroup(spec: CleanerrRuleGroupSpec): CleanerrRuleGroupSpec {
  return {
    ...spec,
    targetMediaType: normalizeTargetMediaType(spec.targetMediaType),
  };
}

function normalizeGraphMediaTypes(graph: CleanerrDagGraph): CleanerrDagGraph {
  return {
    ...graph,
    nodes: (graph.nodes ?? []).map((node) => {
      const data = (node.data ?? {}) as Record<string, unknown>;
      const nodeType = String(data.node_type ?? data.nodeType ?? '');
      const cfg = (data.config ?? {}) as Record<string, unknown>;
      if (
        (nodeType === 'source.managerr_catalog' || nodeType === 'source.plex_library') &&
        (cfg.media_type === 'season' || cfg.media_type === 'episode')
      ) {
        return {
          ...node,
          data: {
            ...data,
            config: {
              ...cfg,
              media_type: 'show',
            },
          },
        };
      }
      if (
        nodeType === 'filter.maintainerr_rules' &&
        (cfg.targetMediaType === 'season' || cfg.targetMediaType === 'episode')
      ) {
        return {
          ...node,
          data: {
            ...data,
            config: {
              ...cfg,
              targetMediaType: 'show',
            },
          },
        };
      }
      return node;
    }),
  };
}

function syncRuleGroupFromGraph(
  graph: CleanerrDagGraph,
  current: CleanerrRuleGroupSpec,
): CleanerrRuleGroupSpec {
  const nodes = graph.nodes ?? [];
  for (const node of nodes) {
    const data = (node.data ?? {}) as Record<string, unknown>;
    const nodeType = String(data.node_type ?? data.nodeType ?? '');
    const cfg = (data.config ?? {}) as Record<string, unknown>;
    if (nodeType === 'filter.maintainerr_rules' && Array.isArray(cfg.sections)) {
      return normalizeRuleGroup({
        ...current,
        ...(cfg as Partial<CleanerrRuleGroupSpec>),
      });
    }
  }

  let next = { ...current };
  for (const node of nodes) {
    const data = (node.data ?? {}) as Record<string, unknown>;
    const nodeType = String(data.node_type ?? data.nodeType ?? '');
    const cfg = (data.config ?? {}) as Record<string, unknown>;
    if (
      (nodeType === 'source.managerr_catalog' ||
        nodeType === 'source.plex_library') &&
      typeof cfg.media_type === 'string'
    ) {
      const mt = cfg.media_type;
      if (mt === 'movie' || mt === 'show' || mt === 'season' || mt === 'episode') {
        next = {
          ...next,
          targetMediaType: normalizeTargetMediaType(mt),
          libraryIds: Array.isArray(cfg.library_ids)
            ? cfg.library_ids.map(Number).filter(Number.isFinite)
            : next.libraryIds,
        };
      }
    } else if (
      nodeType === 'queue.grace_period' &&
      typeof cfg.grace_days === 'number'
    ) {
      next = {
        ...next,
        graceDays: cfg.grace_days,
      };
    } else if (nodeType === 'action.managerr' && typeof cfg.mode === 'string') {
      next = {
        ...next,
        actionMode: cfg.mode,
        cleanEmptyFolders:
          typeof cfg.clean_empty_folders === 'boolean'
            ? cfg.clean_empty_folders
            : next.cleanEmptyFolders,
        resetRequests:
          typeof cfg.reset_requests === 'boolean'
            ? cfg.reset_requests
            : next.resetRequests,
        pruneTorrentClient:
          typeof cfg.prune_torrent_client === 'boolean'
            ? cfg.prune_torrent_client
            : next.pruneTorrentClient,
      };
    }
  }
  return next;
}

const FALLBACK_TEMPLATES: CleanerrTemplate[] = [
  {
    slug: 'monday-movies-365d',
    name: 'Monday Movies (365d Inactive + 7d Grace)',
    category: 'Maintainerr Presets',
    description:
      'Stages movies inactive for >365 days into Leaving Soon with a 7-day grace window and hardlink/in-progress guards.',
    editorMode: 'rules',
    defaultScheduleCron: '0 2 * * 1',
    ruleGroup: {
      targetMediaType: 'movie',
      graceDays: 7,
      actionMode: 'delete_files_and_unmonitor',
      cleanEmptyFolders: true,
      resetRequests: true,
      pruneTorrentClient: false,
      sections: [
        {
          sectionOperator: 'AND',
          title: 'Inactive Movies (>365d)',
          rules: [
            {
              operator: null,
              field: 'plex.daysSinceLastActivity',
              comparator: 'BIGGER',
              value: '365',
            },
            {
              operator: 'AND',
              field: 'plex.isInProgress',
              comparator: 'EQUALS',
              value: 'false',
            },
            {
              operator: 'AND',
              field: 'file.isHardlinked',
              comparator: 'EQUALS',
              value: 'false',
            },
          ],
        },
      ],
    },
    graphJson: { nodes: [], edges: [] },
  },
  {
    slug: 'stale-tv-series-365d',
    name: 'Stale Watched TV Series (365d)',
    category: 'Maintainerr Presets',
    description:
      'Stages whole TV series inactive for >365 days into Leaving Soon with a 7-day grace window and hardlink/in-progress guards.',
    editorMode: 'rules',
    defaultScheduleCron: '0 2 * * 1',
    ruleGroup: {
      targetMediaType: 'show',
      graceDays: 7,
      actionMode: 'delete_files_and_unmonitor',
      cleanEmptyFolders: true,
      resetRequests: true,
      pruneTorrentClient: false,
      sections: [
        {
          sectionOperator: 'AND',
          title: 'Stale Inactive TV Series (>365d)',
          rules: [
            {
              operator: null,
              field: 'plex.daysSinceLastActivity',
              comparator: 'BIGGER',
              value: '365',
            },
            {
              operator: 'AND',
              field: 'plex.isInProgress',
              comparator: 'EQUALS',
              value: 'false',
            },
            {
              operator: 'AND',
              field: 'file.isHardlinked',
              comparator: 'EQUALS',
              value: 'false',
            },
          ],
        },
      ],
    },
    graphJson: { nodes: [], edges: [] },
  },
  {
    slug: 'dag-storage-pressure-relief',
    name: 'DAG Storage Pressure Pipeline (<250 GB Free)',
    category: 'DAG Templates',
    description:
      'Visual DAG pipeline triggered on storage pressure (<250 GB free) to stage large watched movies with a 200 GB reclaim cap.',
    editorMode: 'dag',
    defaultScheduleCron: '0 */6 * * *',
    ruleGroup: null,
    graphJson: {
      nodes: [
        {
          id: 'n1',
          type: 'cleanerrNode',
          position: { x: 40, y: 120 },
          data: {
            label: 'Storage Pressure (<250 GB)',
            node_type: 'trigger.storage_pressure',
            config: { min_free_gb_trigger: 250, target_free_gb: 500 },
          },
        },
        {
          id: 'n2',
          type: 'cleanerrNode',
          position: { x: 280, y: 120 },
          data: {
            label: 'Movies Catalog',
            node_type: 'source.managerr_catalog',
            config: { media_type: 'movie', library_ids: [] },
          },
        },
        {
          id: 'n3',
          type: 'cleanerrNode',
          position: { x: 540, y: 120 },
          data: {
            label: 'Watched > 30d',
            node_type: 'filter.watch_state',
            config: {
              last_watched_older_than_days: 30,
              unwatched_added_older_than_days: 0,
              view_count_gte: 1,
            },
          },
        },
        {
          id: 'n4',
          type: 'cleanerrNode',
          position: { x: 800, y: 120 },
          data: {
            label: 'Exclusions Guard',
            node_type: 'filter.exclusion_check',
            config: {
              respect_global_exclusions: true,
              respect_user_quotas: true,
              excluded_tags: ['keep', 'protected'],
            },
          },
        },
        {
          id: 'n5',
          type: 'cleanerrNode',
          position: { x: 1060, y: 120 },
          data: {
            label: 'Cap 200 GB / Run',
            node_type: 'limit.reclaim_budget',
            config: {
              max_reclaim_gb: 200,
              target_free_space_gb_watermark: 500,
              min_free_space_gb_trigger: 250,
            },
          },
        },
        {
          id: 'n6',
          type: 'cleanerrNode',
          position: { x: 1320, y: 120 },
          data: {
            label: '3d Leaving Soon Grace',
            node_type: 'queue.grace_period',
            config: {
              grace_days: 3,
              auto_rescue_on_watch: true,
            },
          },
        },
        {
          id: 'n7',
          type: 'cleanerrNode',
          position: { x: 1580, y: 120 },
          data: {
            label: 'Delete & Unmonitor',
            node_type: 'action.managerr',
            config: {
              mode: 'delete_files_and_unmonitor',
              unmonitor: true,
              clean_empty_folders: true,
            },
          },
        },
      ],
      edges: [
        { id: 'e1', source: 'n1', target: 'n2' },
        { id: 'e2', source: 'n2', target: 'n3' },
        { id: 'e3', source: 'n3', target: 'n4' },
        { id: 'e4', source: 'n4', target: 'n5' },
        { id: 'e5', source: 'n5', target: 'n6' },
        { id: 'e6', source: 'n6', target: 'n7' },
      ],
    },
  },
];

function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return 'Never';
  const parsed = new Date(iso);
  if (Number.isNaN(parsed.getTime())) return iso;
  return parsed.toLocaleString();
}

export function CleanerrStudioSection({ status }: CleanerrStudioSectionProps) {
  const qc = useQueryClient();
  const baseId = useId();
  const matchedTableCaptionId = `${baseId}-matched-caption`;
  const nodeMetricsCaptionId = `${baseId}-metrics-caption`;
  const runsTableCaptionId = `${baseId}-runs-caption`;

  // Editor state
  const [selectedWorkflowId, setSelectedWorkflowId] = useState<number | null>(
    null,
  );
  const [activeTemplateSlug, setActiveTemplateSlug] = useState('');
  const [name, setName] = useState('New Maintainerr Rule Group');
  const [description, setDescription] = useState(
    'Automated Cleanerr retention policy',
  );
  const [scheduleCron, setScheduleCron] = useState('0 3 * * *');
  const [enabled, setEnabled] = useState(false);
  const [dryRun, setDryRun] = useState(true);
  const [originallyDryRun, setOriginallyDryRun] = useState(true);
  const [editorMode, setEditorMode] = useState<'rules' | 'dag'>('rules');
  const [ruleGroup, setRuleGroup] = useState<CleanerrRuleGroupSpec>(() =>
    structuredClone(DEFAULT_RULE_GROUP),
  );
  const [graphJson, setGraphJson] = useState<CleanerrDagGraph>(() =>
    buildGraphFromRuleGroup(DEFAULT_RULE_GROUP, '0 3 * * *'),
  );
  const [simulatedFreeGb, setSimulatedFreeGb] = useState('');
  const [simulatedFreePct, setSimulatedFreePct] = useState('');

  // Diagnostics / selection / modals
  const [lastRunResult, setLastRunResult] = useState<CleanerrRunResult | null>(
    null,
  );
  const [selectedMatchedIds, setSelectedMatchedIds] = useState<number[]>([]);
  const [manualStageIds, setManualStageIds] = useState<number[] | null>(null);
  const [showTestMediaModal, setShowTestMediaModal] = useState(false);
  const [showYamlModal, setShowYamlModal] = useState(false);
  const [deleteTarget, setDeleteTarget] =
    useState<CleanerrWorkflowSummary | null>(null);

  // Live-Enable Impact Confirmation state
  const [liveImpactModalOpen, setLiveImpactModalOpen] = useState(false);
  const [liveImpactPreview, setLiveImpactPreview] =
    useState<CleanerrLiveImpactPreview | null>(null);
  const [confirmNameInput, setConfirmNameInput] = useState('');

  const [banner, setBanner] = useState<{
    kind: 'info' | 'success' | 'danger';
    message: string;
  } | null>(null);

  // Queries
  const workflowsQuery = useQuery({
    queryKey: ['cleanerr-workflows'],
    queryFn: () =>
      apiFetch<CleanerrWorkflowSummary[]>('/api/v1/cleanerr/workflows'),
  });

  const templatesQuery = useQuery({
    queryKey: ['cleanerr-templates'],
    queryFn: () => apiFetch<CleanerrTemplate[]>('/api/v1/cleanerr/templates'),
  });

  const catalogQuery = useQuery({
    queryKey: ['cleanerr-rules-catalog'],
    queryFn: () =>
      apiFetch<CleanerrRuleCatalogResponse>('/api/v1/cleanerr/rules/fields'),
  });

  const nodeCatalogQuery = useQuery({
    queryKey: ['cleanerr-node-catalog'],
    queryFn: async () => {
      try {
        return await apiFetch<CleanerrNodeCatalogEntry[]>(
          '/api/v1/cleanerr/node-catalog',
        );
      } catch {
        return [];
      }
    },
  });

  const librariesQuery = useQuery({
    queryKey: ['libraries'],
    queryFn: () => apiFetch<LibraryOption[]>('/api/v1/libraries'),
  });

  const profilesQuery = useQuery({
    queryKey: ['profiles'],
    queryFn: () => apiFetch<QualityProfileSummary[]>('/api/v1/profiles'),
  });

  const runsQuery = useQuery({
    queryKey: ['cleanerr-runs'],
    queryFn: () =>
      apiFetch<CleanerrRunResult[]>('/api/v1/cleanerr/runs?limit=20'),
  });

  const allTemplates = useMemo(() => {
    const apiTemplates = templatesQuery.data ?? [];
    if (apiTemplates.length > 0) return apiTemplates;
    return FALLBACK_TEMPLATES;
  }, [templatesQuery.data]);

  const workflowNameById = useMemo(() => {
    const map = new Map<number, string>();
    for (const wf of workflowsQuery.data ?? []) {
      map.set(wf.id, wf.name);
    }
    return map;
  }, [workflowsQuery.data]);

  const loadWorkflow = (wf: CleanerrWorkflowSummary) => {
    setSelectedWorkflowId(wf.id);
    setActiveTemplateSlug('');
    setName(wf.name);
    setDescription(wf.description ?? '');
    setEnabled(wf.enabled);
    setDryRun(wf.dryRun);
    setOriginallyDryRun(wf.dryRun);
    setScheduleCron(wf.scheduleCron ?? '');
    const mode = wf.editorMode ?? (wf.ruleGroup ? 'rules' : 'dag');
    setEditorMode(mode);
    const nextRuleGroup = normalizeRuleGroup(
      wf.ruleGroup ?? structuredClone(DEFAULT_RULE_GROUP),
    );
    setRuleGroup(nextRuleGroup);
    if (wf.graphJson && (wf.graphJson.nodes?.length ?? 0) > 0) {
      setGraphJson(normalizeGraphMediaTypes(wf.graphJson));
    } else {
      setGraphJson(buildGraphFromRuleGroup(nextRuleGroup, wf.scheduleCron ?? ''));
    }
    setLastRunResult(null);
    setSelectedMatchedIds([]);
    setConfirmNameInput('');
  };

  const loadTemplate = (slug: string) => {
    const t = allTemplates.find((item) => item.slug === slug);
    if (!t) return;
    setActiveTemplateSlug(slug);
    setSelectedWorkflowId(null);
    setName(t.name);
    setDescription(t.description);
    setEnabled(false);
    setDryRun(true);
    setOriginallyDryRun(true);
    const cron = t.defaultScheduleCron ?? '0 3 * * *';
    setScheduleCron(cron);
    const mode = t.editorMode ?? (t.ruleGroup ? 'rules' : 'dag');
    setEditorMode(mode);
    const nextRuleGroup = normalizeRuleGroup(
      t.ruleGroup
        ? structuredClone(t.ruleGroup)
        : structuredClone(DEFAULT_RULE_GROUP),
    );
    setRuleGroup(nextRuleGroup);
    if (t.graphJson && (t.graphJson.nodes?.length ?? 0) > 0) {
      setGraphJson(normalizeGraphMediaTypes(t.graphJson));
    } else {
      setGraphJson(buildGraphFromRuleGroup(nextRuleGroup, cron));
    }
    setLastRunResult(null);
    setSelectedMatchedIds([]);
    setConfirmNameInput('');
    setBanner({
      kind: 'info',
      message: `Loaded template "${t.name}" in Dry-Run safety mode.`,
    });
  };

  const createBlankWorkflow = () => {
    setSelectedWorkflowId(null);
    setActiveTemplateSlug('');
    setName('New Cleanerr Workflow');
    setDescription('');
    setEnabled(false);
    setDryRun(true);
    setOriginallyDryRun(true);
    setScheduleCron('0 3 * * *');
    setEditorMode('rules');
    const freshSpec = structuredClone(DEFAULT_RULE_GROUP);
    setRuleGroup(freshSpec);
    setGraphJson(buildGraphFromRuleGroup(freshSpec, '0 3 * * *'));
    setLastRunResult(null);
    setSelectedMatchedIds([]);
    setConfirmNameInput('');
  };

  // Bidirectional Editor Mode sync on switch
  const switchEditorMode = (nextMode: 'rules' | 'dag') => {
    if (nextMode === editorMode) return;
    if (nextMode === 'dag') {
      setGraphJson(buildGraphFromRuleGroup(ruleGroup, scheduleCron));
    } else {
      setRuleGroup((prev) => syncRuleGroupFromGraph(graphJson, prev));
    }
    setEditorMode(nextMode);
  };

  const handleRuleGroupChange = (nextSpec: CleanerrRuleGroupSpec) => {
    setRuleGroup(nextSpec);
    setGraphJson(buildGraphFromRuleGroup(nextSpec, scheduleCron));
  };

  const handleGraphChange = (nextGraph: CleanerrDagGraph) => {
    setGraphJson(normalizeGraphMediaTypes(nextGraph));
  };

  // Mutations
  const previewImpactMutation = useMutation({
    mutationFn: () => {
      const targetId = selectedWorkflowId ?? 'preview';
      const effectiveGraph =
        editorMode === 'rules'
          ? buildGraphFromRuleGroup(ruleGroup, scheduleCron)
          : graphJson;
      return apiFetch<CleanerrLiveImpactPreview>(
        `/api/v1/cleanerr/workflows/${targetId}/preview-impact`,
        {
          method: 'POST',
          body: JSON.stringify({
            graphJson: effectiveGraph,
            ruleGroup: editorMode === 'rules' ? ruleGroup : null,
            workflowName: name.trim(),
          }),
        },
      );
    },
    onSuccess: (impact) => {
      setLiveImpactPreview(impact);
      setConfirmNameInput('');
      setLiveImpactModalOpen(true);
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Failed to preview live workflow impact.',
      });
    },
  });

  const saveWorkflowMutation = useMutation({
    mutationFn: (confirmWorkflowName?: string) => {
      const effectiveGraph =
        editorMode === 'rules'
          ? buildGraphFromRuleGroup(ruleGroup, scheduleCron)
          : graphJson;
      const payload = {
        name: name.trim(),
        description: description.trim(),
        enabled,
        dryRun,
        scheduleCron: scheduleCron.trim() || null,
        editorMode,
        ruleGroup: editorMode === 'rules' ? ruleGroup : null,
        graphJson: effectiveGraph,
        confirmWorkflowName: confirmWorkflowName || undefined,
      };
      if (selectedWorkflowId) {
        return apiFetch<CleanerrWorkflowSummary>(
          `/api/v1/cleanerr/workflows/${selectedWorkflowId}`,
          {
            method: 'PUT',
            body: JSON.stringify(payload),
          },
        );
      }
      return apiFetch<CleanerrWorkflowSummary>('/api/v1/cleanerr/workflows', {
        method: 'POST',
        body: JSON.stringify(payload),
      });
    },
    onSuccess: (saved) => {
      setLiveImpactModalOpen(false);
      setConfirmNameInput('');
      loadWorkflow(saved);
      setBanner({
        kind: 'success',
        message: `Saved workflow "${saved.name}" (#${saved.id}).`,
      });
      void qc.invalidateQueries({ queryKey: ['cleanerr-workflows'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-stats'] });
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Failed to save workflow.',
      });
    },
  });

  const handleSaveClick = () => {
    if (!name.trim()) {
      setBanner({ kind: 'danger', message: 'Workflow name is required.' });
      return;
    }
    const selectedWorkflow = (workflowsQuery.data ?? []).find(
      (wf) => wf.id === selectedWorkflowId,
    );
    const isNew = selectedWorkflowId === null;
    const requiresLiveConfirm =
      !dryRun &&
      (isNew ||
        (selectedWorkflow?.dryRun ?? originallyDryRun) !== false ||
        enabled);

    if (requiresLiveConfirm) {
      previewImpactMutation.mutate();
      return;
    }
    saveWorkflowMutation.mutate(undefined);
  };

  const duplicateWorkflowMutation = useMutation({
    mutationFn: (wf: CleanerrWorkflowSummary) =>
      apiFetch<CleanerrWorkflowSummary>(
        `/api/v1/cleanerr/workflows/${wf.id}/duplicate`,
        {
          method: 'POST',
          body: JSON.stringify({ name: `${wf.name} (Copy)` }),
        },
      ),
    onSuccess: (cloned) => {
      loadWorkflow(cloned);
      setBanner({
        kind: 'success',
        message: `Cloned workflow as "${cloned.name}".`,
      });
      void qc.invalidateQueries({ queryKey: ['cleanerr-workflows'] });
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Failed to duplicate workflow.',
      });
    },
  });

  const deleteWorkflowMutation = useMutation({
    mutationFn: (id: number) =>
      apiFetch(`/api/v1/cleanerr/workflows/${id}`, { method: 'DELETE' }),
    onSuccess: (_res, deletedId) => {
      setDeleteTarget(null);
      if (selectedWorkflowId === deletedId) {
        createBlankWorkflow();
      }
      setBanner({
        kind: 'info',
        message: 'Workflow deleted.',
      });
      void qc.invalidateQueries({ queryKey: ['cleanerr-workflows'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-stats'] });
    },
    onError: (err: Error) => {
      setDeleteTarget(null);
      setBanner({
        kind: 'danger',
        message: err.message || 'Failed to delete workflow.',
      });
    },
  });

  const simulateMutation = useMutation({
    mutationFn: () => {
      const freeGb = simulatedFreeGb.trim() ? Number(simulatedFreeGb) : null;
      const freePct = simulatedFreePct.trim() ? Number(simulatedFreePct) : null;
      const effectiveGraph =
        editorMode === 'rules'
          ? buildGraphFromRuleGroup(ruleGroup, scheduleCron)
          : graphJson;
      const endpoint = selectedWorkflowId
        ? `/api/v1/cleanerr/workflows/${selectedWorkflowId}/simulate`
        : '/api/v1/cleanerr/workflows/simulate';
      return apiFetch<CleanerrRunResult>(endpoint, {
        method: 'POST',
        body: JSON.stringify({
          graphJson: effectiveGraph,
          ruleGroup: editorMode === 'rules' ? ruleGroup : null,
          workflowId: selectedWorkflowId,
          simulatedFreeSpaceGb:
            freeGb !== null && Number.isFinite(freeGb) ? freeGb : null,
          simulatedFreeSpacePercent:
            freePct !== null && Number.isFinite(freePct) ? freePct : null,
        }),
      });
    },
    onSuccess: (res) => {
      setLastRunResult(res);
      setSelectedMatchedIds([]);
      setBanner({
        kind: 'info',
        message: `Simulation completed: ${res.summary.stagedCount} item(s) matched (${formatBytes(res.summary.reclaimableSpaceBytes ?? res.summary.spaceBytes)} reclaimable).`,
      });
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Simulation failed.',
      });
    },
  });

  const runWorkflowMutation = useMutation({
    mutationFn: (id: number) =>
      apiFetch<CleanerrRunResult>(`/api/v1/cleanerr/workflows/${id}/run`, {
        method: 'POST',
        body: JSON.stringify({ dryRun }),
      }),
    onSuccess: (res) => {
      setLastRunResult(res);
      setSelectedMatchedIds([]);
      setBanner({
        kind: res.dryRun ? 'info' : 'success',
        message: `${res.dryRun ? '[Dry-Run]' : '[Live Run]'} Finished — Staged: ${res.summary.stagedCount}, Deleted: ${res.summary.deletedCount}, Reclaimable: ${formatBytes(res.summary.reclaimableSpaceBytes ?? res.summary.spaceBytes)}.`,
      });
      void qc.invalidateQueries({ queryKey: ['cleanerr-workflows'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-runs'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-queue'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-stats'] });
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Workflow execution failed.',
      });
    },
  });

  const bulkExcludeMutation = useMutation({
    mutationFn: (ids: number[]) =>
      apiFetch('/api/v1/cleanerr/exclusions/bulk', {
        method: 'POST',
        body: JSON.stringify({
          mediaItemIds: ids,
          reason: 'Excluded from Cleanerr Studio simulation preview',
          workflowId: selectedWorkflowId,
        }),
      }),
    onSuccess: (_res, ids) => {
      setSelectedMatchedIds([]);
      setBanner({
        kind: 'success',
        message: `Created exclusion rules for ${ids.length} selected item(s).`,
      });
      void qc.invalidateQueries({ queryKey: ['cleanerr-exclusions'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-stats'] });
    },
    onError: (err: Error) => {
      setBanner({
        kind: 'danger',
        message: err.message || 'Failed to exclude selected items.',
      });
    },
  });

  const matchedItems = lastRunResult?.summary.matchedItems ?? [];
  const nodeMetricsList = Object.values(
    lastRunResult?.summary.nodeMetrics ?? {},
  );
  const allMatchedSelected =
    matchedItems.length > 0 &&
    matchedItems.every((m) => selectedMatchedIds.includes(m.id));

  const toggleMatchedOne = (id: number) => {
    setSelectedMatchedIds((prev) =>
      prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id],
    );
  };

  const toggleMatchedAll = () => {
    if (allMatchedSelected) {
      setSelectedMatchedIds([]);
    } else {
      setSelectedMatchedIds(matchedItems.map((m) => m.id));
    }
  };

  const handleTabKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
      e.preventDefault();
      switchEditorMode(editorMode === 'rules' ? 'dag' : 'rules');
    } else if (e.key === 'Home') {
      e.preventDefault();
      switchEditorMode('rules');
    } else if (e.key === 'End') {
      e.preventDefault();
      switchEditorMode('dag');
    }
  };

  const canWrite = Boolean(status?.canWriteWorkflows);
  const canExecute = Boolean(status?.canExecute);
  const canExclude = Boolean(status?.canExclude);
  const matchedCronPreset = CRON_PRESETS.some(
    (p) => p.value === scheduleCron,
  )
    ? scheduleCron
    : '__custom__';

  return (
    <div className="flex flex-col flex-1">
      <PageToolbar
        left={
          <>
            <PageToolbarButton
              icon={Plus}
              label="New Workflow"
              onClick={createBlankWorkflow}
            />
            {canWrite && (
              <PageToolbarButton
                icon={Save}
                label={selectedWorkflowId ? 'Save Workflow' : 'Create Workflow'}
                spinning={
                  saveWorkflowMutation.isPending ||
                  previewImpactMutation.isPending
                }
                onClick={handleSaveClick}
              />
            )}
            <PageToolbarSeparator />
            <PageToolbarButton
              icon={Eye}
              label="Simulate"
              title="Run a dry-run simulation against the synced catalog"
              spinning={simulateMutation.isPending}
              onClick={() => simulateMutation.mutate()}
            />
            <PageToolbarButton
              icon={FileSearch}
              label="Test Media"
              title="Trace a single media item through the active rules"
              onClick={() => setShowTestMediaModal(true)}
            />
            {(canWrite || selectedWorkflowId !== null) && (
              <PageToolbarButton
                icon={FileCode}
                label="YAML / JSON"
                title="Import or export workflow YAML/JSON"
                onClick={() => setShowYamlModal(true)}
              />
            )}
            {selectedWorkflowId && canExecute && (
              <>
                <PageToolbarSeparator />
                <PageToolbarButton
                  icon={Play}
                  label={dryRun ? 'Run Dry-Run' : 'Run Live'}
                  title="Execute saved workflow immediately"
                  spinning={runWorkflowMutation.isPending}
                  onClick={() => runWorkflowMutation.mutate(selectedWorkflowId)}
                />
              </>
            )}
          </>
        }
        right={
          <div className="flex items-center gap-2.5 pr-1">
            <label
              htmlFor={`${baseId}-sim-free-gb`}
              className="text-[11px] text-[var(--arr-muted)]"
            >
              Sim Free GB:
            </label>
            <input
              id={`${baseId}-sim-free-gb`}
              type="number"
              placeholder="Live"
              value={simulatedFreeGb}
              onChange={(e) => setSimulatedFreeGb(e.target.value)}
              className="arr-input !h-[28px] !w-20 !py-0 !px-2 !text-[11px]"
            />
            <label
              htmlFor={`${baseId}-sim-free-pct`}
              className="text-[11px] text-[var(--arr-muted)]"
            >
              Sim Free %:
            </label>
            <input
              id={`${baseId}-sim-free-pct`}
              type="number"
              min={0}
              max={100}
              placeholder="Live"
              value={simulatedFreePct}
              onChange={(e) => setSimulatedFreePct(e.target.value)}
              className="arr-input !h-[28px] !w-16 !py-0 !px-2 !text-[11px]"
            />
          </div>
        }
      />

      <div className="p-5 lg:p-6 space-y-6 overflow-x-hidden">
        {banner && <ArrAlert kind={banner.kind}>{banner.message}</ArrAlert>}

        {/* Saved Workflows & Template Picker */}
        <div className="grid grid-cols-1 xl:grid-cols-[minmax(0,2fr)_minmax(0,1fr)] gap-4">
          <div className="p-4 rounded-[4px] border border-[var(--arr-card-border)] bg-[var(--arr-card-bg)] space-y-3">
            <div className="flex items-center justify-between gap-2">
              <h3 className="text-[13px] font-bold uppercase tracking-wider text-[var(--arr-text)]">
                Saved Workflows ({workflowsQuery.data?.length ?? 0})
              </h3>
              <button
                type="button"
                onClick={() => void workflowsQuery.refetch()}
                className="inline-flex items-center gap-1 text-[11px] text-[var(--arr-muted)] hover:text-[var(--arr-text)] cursor-pointer"
              >
                <RefreshCw
                  aria-hidden="true"
                  className={`h-3.5 w-3.5 ${workflowsQuery.isFetching ? 'animate-spin' : ''}`}
                />
                Refresh
              </button>
            </div>

            {workflowsQuery.isLoading ? (
              <div className="text-[12px] text-[var(--arr-muted)] py-4">
                Loading saved workflows...
              </div>
            ) : (workflowsQuery.data ?? []).length === 0 ? (
              <div className="text-[12px] text-[var(--arr-muted)] py-4">
                No saved workflows yet. Start from a preset template or build a
                custom workflow below.
              </div>
            ) : (
              <div className="grid grid-cols-1 md:grid-cols-2 gap-2.5 max-h-56 overflow-y-auto pr-1">
                {(workflowsQuery.data ?? []).map((wf) => {
                  const isActive = wf.id === selectedWorkflowId;
                  return (
                    <div
                      key={wf.id}
                      className={`p-3 rounded-[4px] border transition-colors flex flex-col justify-between gap-2 ${
                        isActive
                          ? 'border-[var(--arr-accent)] bg-[var(--arr-primary)]/10'
                          : 'border-[var(--arr-card-border)] bg-[var(--arr-card-center-bg)]'
                      }`}
                    >
                      <div className="flex items-start justify-between gap-2">
                        <button
                          type="button"
                          onClick={() => loadWorkflow(wf)}
                          className="text-left min-w-0 flex-1 cursor-pointer"
                        >
                          <div className="text-[13px] font-semibold text-[var(--arr-text)] truncate">
                            {wf.name}
                          </div>
                          <div className="text-[11px] text-[var(--arr-muted)] truncate mt-0.5">
                            {wf.scheduleCron
                              ? `Cron: ${wf.scheduleCron} UTC`
                              : 'Manual schedule'}
                            {wf.lastRunAt
                              ? ` • Last: ${formatDateTime(wf.lastRunAt)}`
                              : ''}
                          </div>
                        </button>
                        <div className="flex items-center gap-1 shrink-0">
                          <ArrLabel kind={wf.dryRun ? 'warning' : 'danger'}>
                            {wf.dryRun ? 'Dry-Run' : 'Live'}
                          </ArrLabel>
                          <ArrLabel kind={wf.enabled ? 'success' : 'default'}>
                            {wf.enabled ? 'On' : 'Off'}
                          </ArrLabel>
                        </div>
                      </div>

                      <div className="flex items-center justify-between gap-2 pt-1 border-t border-[var(--arr-divider)]">
                        <span className="text-[10px] font-mono uppercase text-[var(--arr-muted)]">
                          {wf.editorMode === 'dag' ? 'Visual DAG' : 'Rule Builder'}
                        </span>
                        {canWrite && (
                          <div className="flex items-center gap-1">
                            <ArrButton
                              variant="default"
                              className="!h-[22px] !px-2 !text-[10px]"
                              title={`Clone "${wf.name}"`}
                              busy={duplicateWorkflowMutation.isPending}
                              onClick={() => duplicateWorkflowMutation.mutate(wf)}
                            >
                              <Copy aria-hidden="true" className="h-3 w-3" />
                              Clone
                            </ArrButton>
                            <ArrButton
                              variant="default"
                              className="!h-[22px] !px-2 !text-[10px] text-[#f05050]"
                              title={`Delete "${wf.name}"`}
                              onClick={() => setDeleteTarget(wf)}
                            >
                              <Trash2 aria-hidden="true" className="h-3 w-3" />
                            </ArrButton>
                          </div>
                        )}
                      </div>
                    </div>
                  );
                })}
              </div>
            )}
          </div>

          {/* Template Picker */}
          <div className="p-4 rounded-[4px] border border-[var(--arr-card-border)] bg-[var(--arr-card-bg)] space-y-3">
            <div className="flex items-center gap-2">
              <Sparkles
                aria-hidden="true"
                className="h-4 w-4 text-[var(--arr-accent)]"
              />
              <h3 className="text-[13px] font-bold uppercase tracking-wider text-[var(--arr-text)]">
                Template Picker
              </h3>
            </div>
            <div>
              <label
                htmlFor={`${baseId}-template-select`}
                className="block text-[11px] font-semibold text-[var(--arr-muted)] mb-1"
              >
                Load a Maintainerr Preset or DAG Template
              </label>
              <select
                id={`${baseId}-template-select`}
                value={activeTemplateSlug}
                onChange={(e) => {
                  if (e.target.value) loadTemplate(e.target.value);
                }}
                className="arr-input !h-[32px] !text-[12px]"
              >
                <option value="">-- Choose Template --</option>
                {allTemplates.map((t) => (
                  <option key={t.slug} value={t.slug}>
                    [{t.category}] {t.name}
                  </option>
                ))}
              </select>
            </div>
            <div className="space-y-1.5 max-h-36 overflow-y-auto pr-1">
              {allTemplates.map((t) => (
                <button
                  key={t.slug}
                  type="button"
                  onClick={() => loadTemplate(t.slug)}
                  className={`w-full text-left p-2 rounded-[4px] border text-[11px] transition-colors cursor-pointer ${
                    activeTemplateSlug === t.slug
                      ? 'border-[var(--arr-accent)] bg-[var(--arr-primary)]/10'
                      : 'border-[var(--arr-card-border)] bg-[var(--arr-card-center-bg)] hover:border-[var(--arr-accent)]/60'
                  }`}
                >
                  <div className="flex items-center justify-between gap-2 font-semibold text-[var(--arr-text)]">
                    <span className="truncate">{t.name}</span>
                    <ArrLabel kind={t.editorMode === 'dag' ? 'purple' : 'info'}>
                      {t.editorMode === 'dag' ? 'DAG' : 'Rules'}
                    </ArrLabel>
                  </div>
                  <p className="text-[var(--arr-muted)] mt-0.5 line-clamp-2">
                    {t.description}
                  </p>
                </button>
              ))}
            </div>
          </div>
        </div>

        {/* Workflow Metadata & Customizable Cron Schedule Bar */}
        <div className="p-4 rounded-[4px] border border-[var(--arr-card-border)] bg-[var(--arr-card-bg)] space-y-4">
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
            <div className="lg:col-span-2">
              <label
                htmlFor={`${baseId}-wf-name`}
                className="block text-[12px] font-bold text-[var(--arr-text)] mb-1"
              >
                Workflow Name
              </label>
              <input
                id={`${baseId}-wf-name`}
                type="text"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="e.g. Monday Movies Cleanup"
                className="arr-input"
              />
            </div>

            <div className="lg:col-span-2">
              <label
                htmlFor={`${baseId}-wf-desc`}
                className="block text-[12px] font-bold text-[var(--arr-text)] mb-1"
              >
                Description
              </label>
              <input
                id={`${baseId}-wf-desc`}
                type="text"
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                placeholder="Brief summary of cleanup scope and guards..."
                className="arr-input"
              />
            </div>

            <div>
              <label
                htmlFor={`${baseId}-cron-preset`}
                className="block text-[12px] font-bold text-[var(--arr-text)] mb-1"
              >
                Schedule Preset
              </label>
              <select
                id={`${baseId}-cron-preset`}
                value={matchedCronPreset}
                onChange={(e) => {
                  if (e.target.value !== '__custom__') {
                    setScheduleCron(e.target.value);
                  }
                }}
                className="arr-input text-[12px]"
              >
                {CRON_PRESETS.map((p) => (
                  <option key={p.label} value={p.value}>
                    {p.label}
                  </option>
                ))}
                <option value="__custom__">Custom Cron Expression</option>
              </select>
            </div>

            <div>
              <label
                htmlFor={`${baseId}-cron-input`}
                className="block text-[12px] font-bold text-[var(--arr-text)] mb-1"
              >
                Schedule Cron (UTC)
              </label>
              <input
                id={`${baseId}-cron-input`}
                type="text"
                value={scheduleCron}
                onChange={(e) => setScheduleCron(e.target.value)}
                placeholder="0 3 * * *"
                className="arr-input font-mono text-[12px]"
              />
              <p className="mt-1 text-[11px] text-[var(--arr-muted)]">
                Evaluated in UTC (e.g. <code>0 3 * * *</code> daily at 03:00 UTC).
              </p>
            </div>

            <div className="lg:col-span-2 flex flex-wrap items-center gap-6 pt-2 md:pt-5">
              <label className="inline-flex items-center gap-2 cursor-pointer text-[13px] font-semibold text-[var(--arr-text)]">
                <input
                  type="checkbox"
                  checked={enabled}
                  onChange={(e) => setEnabled(e.target.checked)}
                />
                <span>Enabled</span>
              </label>

              <label className="inline-flex items-center gap-2 cursor-pointer text-[13px] font-semibold text-[#ffa726]">
                <input
                  type="checkbox"
                  checked={dryRun}
                  onChange={(e) => setDryRun(e.target.checked)}
                />
                <span>Dry-Run Safety Mode (Preview Without Staging)</span>
              </label>

              {!dryRun && (
                <ArrLabel kind="danger">
                  {enabled
                    ? 'Live Execution Active — Requires Name Confirmation on Save'
                    : 'Live Mode Selected — Requires Name Confirmation on Save'}
                </ArrLabel>
              )}
            </div>
          </div>

          {/* Accessible Mode Tabs (underline indicator, not color alone) */}
          <div className="flex flex-wrap items-center justify-between gap-3 border-t border-[var(--arr-divider)] pt-3">
            <div
              role="tablist"
              aria-label="Workflow Editor Mode"
              onKeyDown={handleTabKeyDown}
              className="flex items-center gap-6 border-b border-[var(--arr-card-border)]"
            >
              <button
                type="button"
                role="tab"
                id={`${baseId}-tab-rules`}
                aria-selected={editorMode === 'rules'}
                tabIndex={editorMode === 'rules' ? 0 : -1}
                onClick={() => switchEditorMode('rules')}
                className={`relative pb-2.5 text-[13px] font-semibold transition-colors cursor-pointer whitespace-nowrap ${
                  editorMode === 'rules'
                    ? 'text-[var(--arr-accent)] border-b-[3px] border-[var(--arr-accent)] -mb-px'
                    : 'text-[var(--arr-muted)] hover:text-[var(--arr-text)] border-b-[3px] border-transparent -mb-px'
                }`}
              >
                Maintainerr Rule Builder
              </button>
              <button
                type="button"
                role="tab"
                id={`${baseId}-tab-dag`}
                aria-selected={editorMode === 'dag'}
                tabIndex={editorMode === 'dag' ? 0 : -1}
                onClick={() => switchEditorMode('dag')}
                className={`relative pb-2.5 text-[13px] font-semibold transition-colors cursor-pointer whitespace-nowrap ${
                  editorMode === 'dag'
                    ? 'text-[var(--arr-accent)] border-b-[3px] border-[var(--arr-accent)] -mb-px'
                    : 'text-[var(--arr-muted)] hover:text-[var(--arr-text)] border-b-[3px] border-transparent -mb-px'
                }`}
              >
                Visual DAG Studio
              </button>
            </div>

            <span className="text-[11px] text-[var(--arr-muted)]">
              {editorMode === 'rules'
                ? 'Edits sync automatically to the Visual DAG representation.'
                : 'Build multi-branch node pipelines with real-time simulation metrics.'}
            </span>
          </div>
        </div>

        {/* Mode Tabpanels */}
        <div
          role="tabpanel"
          aria-labelledby={`${baseId}-tab-rules`}
          hidden={editorMode !== 'rules'}
        >
          {editorMode === 'rules' && (
            <CleanerrRuleBuilder
              ruleGroup={ruleGroup}
              onChange={handleRuleGroupChange}
              catalog={catalogQuery.data}
              libraries={librariesQuery.data}
              profiles={profilesQuery.data}
            />
          )}
        </div>

        <div
          role="tabpanel"
          aria-labelledby={`${baseId}-tab-dag`}
          hidden={editorMode !== 'dag'}
        >
          {editorMode === 'dag' && (
            <CleanerrDagStudio
              graphJson={graphJson}
              onChange={handleGraphChange}
              nodeCatalog={
                nodeCatalogQuery.data && nodeCatalogQuery.data.length > 0
                  ? nodeCatalogQuery.data
                  : undefined
              }
              libraries={librariesQuery.data}
              profiles={profilesQuery.data}
              nodeMetrics={lastRunResult?.summary.nodeMetrics}
              onBulkExcludeIds={(ids) => bulkExcludeMutation.mutate(ids)}
              excludingIds={bulkExcludeMutation.isPending}
              readOnly={!canWrite}
            />
          )}
        </div>

        {/* Simulation / Execution Results */}
        {lastRunResult && (
          <FieldSet
            legend={
              <span className="inline-flex items-center gap-2">
                <CheckCircle2
                  aria-hidden="true"
                  className="h-5 w-5 text-[#27c24c]"
                />
                <span>Simulation &amp; Execution Diagnostics</span>
                <ArrLabel kind={lastRunResult.dryRun ? 'warning' : 'danger'}>
                  {lastRunResult.dryRun ? 'Dry-Run' : 'Live'}
                </ArrLabel>
              </span>
            }
          >
            <div className="space-y-4">
              <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
                <div className="arr-card p-3">
                  <div className="text-[11px] uppercase text-[var(--arr-muted)] font-semibold">
                    Staged / Matched
                  </div>
                  <div className="text-[20px] font-bold text-[#ffa726]">
                    {lastRunResult.summary.stagedCount}
                  </div>
                </div>
                <div className="arr-card p-3">
                  <div className="text-[11px] uppercase text-[var(--arr-muted)] font-semibold">
                    Deleted
                  </div>
                  <div className="text-[20px] font-bold text-[#f05050]">
                    {lastRunResult.summary.deletedCount}
                  </div>
                </div>
                <div className="arr-card p-3">
                  <div className="text-[11px] uppercase text-[var(--arr-muted)] font-semibold">
                    Reclaimable Space
                  </div>
                  <div className="text-[20px] font-bold text-[#27c24c]">
                    {formatBytes(
                      lastRunResult.summary.reclaimableSpaceBytes ??
                        lastRunResult.summary.spaceBytes,
                    )}
                  </div>
                </div>
                <div className="arr-card p-3">
                  <div className="text-[11px] uppercase text-[var(--arr-muted)] font-semibold">
                    Hardlinked Protected
                  </div>
                  <div className="text-[20px] font-bold text-[var(--arr-accent)]">
                    {lastRunResult.summary.hardlinkedCount ?? 0}
                  </div>
                </div>
              </div>

              {/* Matched Items Table with Bulk Exclude & Stage Selected */}
              {matchedItems.length > 0 && (
                <div className="space-y-2.5">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <span className="text-[13px] font-semibold text-[var(--arr-text)]">
                      Matched Media Items ({matchedItems.length})
                    </span>
                    <div className="flex flex-wrap items-center gap-2">
                      {canExclude && selectedMatchedIds.length > 0 && (
                        <ArrButton
                          variant="primary"
                          className="!h-[28px] !px-2.5 !text-[12px]"
                          busy={bulkExcludeMutation.isPending}
                          onClick={() =>
                            bulkExcludeMutation.mutate(selectedMatchedIds)
                          }
                        >
                          <Shield aria-hidden="true" className="h-3.5 w-3.5" />
                          Exclude Selected ({selectedMatchedIds.length})
                        </ArrButton>
                      )}
                      {canWrite && selectedMatchedIds.length > 0 && (
                        <ArrButton
                          variant="warning"
                          className="!h-[28px] !px-2.5 !text-[12px]"
                          onClick={() => setManualStageIds(selectedMatchedIds)}
                        >
                          <Clock aria-hidden="true" className="h-3.5 w-3.5" />
                          Stage Selected ({selectedMatchedIds.length})
                        </ArrButton>
                      )}
                    </div>
                  </div>

                  <div
                    tabIndex={0}
                    role="region"
                    aria-labelledby={matchedTableCaptionId}
                    className="cleanerr-table-wrapper cleanerr-responsive-table max-h-72 rounded-[4px] border border-[var(--arr-card-border)] bg-[var(--arr-card-bg)]"
                  >
                    <table className="arr-table w-full text-[12px]">
                      <caption id={matchedTableCaptionId} className="sr-only">
                        Matched media items from workflow simulation
                      </caption>
                      <thead>
                        <tr>
                          <th className="w-10">
                            <input
                              type="checkbox"
                              aria-label="Select all matched items"
                              checked={allMatchedSelected}
                              onChange={toggleMatchedAll}
                            />
                          </th>
                          <th>Title</th>
                          <th>Type</th>
                          <th>Size</th>
                          <th>Reason</th>
                        </tr>
                      </thead>
                      <tbody>
                        {matchedItems.map((m) => {
                          const checked = selectedMatchedIds.includes(m.id);
                          const label = m.parentTitle
                            ? `${m.parentTitle} - ${m.title}`
                            : m.title;
                          return (
                            <tr key={m.id}>
                              <td data-label="Select">
                                <input
                                  type="checkbox"
                                  aria-label={`Select ${label}`}
                                  checked={checked}
                                  onChange={() => toggleMatchedOne(m.id)}
                                />
                              </td>
                              <th
                                scope="row"
                                className="text-left font-semibold text-[var(--arr-text)]"
                              >
                                {label} {m.year ? `(${m.year})` : ''}
                              </th>
                              <td data-label="Type">
                                <ArrLabel kind="default">{m.mediaType}</ArrLabel>
                              </td>
                              <td
                                data-label="Size"
                                className="font-mono whitespace-nowrap"
                              >
                                {formatBytes(m.sizeBytes)}
                              </td>
                              <td
                                data-label="Reason"
                                className="text-[11px] text-[var(--arr-muted)]"
                              >
                                {m.matchedReason ?? m.reason ?? '—'}
                              </td>
                            </tr>
                          );
                        })}
                      </tbody>
                    </table>
                  </div>
                </div>
              )}

              {/* Per-Node Execution Metrics */}
              {nodeMetricsList.length > 0 && (
                <div
                  tabIndex={0}
                  role="region"
                  aria-labelledby={nodeMetricsCaptionId}
                  className="cleanerr-table-wrapper cleanerr-responsive-table rounded-[4px] border border-[var(--arr-card-border)] bg-[var(--arr-card-bg)]"
                >
                  <table className="arr-table w-full text-[12px]">
                    <caption id={nodeMetricsCaptionId} className="sr-only">
                      Per-node workflow execution metrics
                    </caption>
                    <thead>
                      <tr>
                        <th>Node</th>
                        <th>Type</th>
                        <th>Input</th>
                        <th>Output</th>
                        <th>Volume</th>
                        <th>Log</th>
                      </tr>
                    </thead>
                    <tbody>
                      {nodeMetricsList.map((nm) => (
                        <tr key={nm.nodeId}>
                          <th
                            scope="row"
                            className="text-left font-semibold text-[var(--arr-text)]"
                          >
                            {nm.label}
                          </th>
                          <td
                            data-label="Type"
                            className="font-mono text-[11px] text-[var(--arr-accent)]"
                          >
                            {nm.nodeType}
                          </td>
                          <td data-label="Input" className="font-mono">
                            {nm.inputCount}
                          </td>
                          <td
                            data-label="Output"
                            className="font-mono font-semibold text-[#ffa726]"
                          >
                            {nm.outputCount}
                          </td>
                          <td data-label="Volume" className="font-mono whitespace-nowrap">
                            {formatBytes(nm.sizeBytes)}
                          </td>
                          <td
                            data-label="Log"
                            className="font-mono text-[11px] text-[var(--arr-muted)]"
                          >
                            {nm.logs?.[0] ?? '—'}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          </FieldSet>
        )}

        {/* Recent Run History */}
        <FieldSet
          legend={
            <span className="inline-flex items-center gap-2">
              <History aria-hidden="true" className="h-5 w-5 text-[var(--arr-accent)]" />
              <span>Recent Run History</span>
            </span>
          }
        >
          <div
            tabIndex={0}
            role="region"
            aria-labelledby={runsTableCaptionId}
            className="cleanerr-table-wrapper cleanerr-responsive-table rounded-[4px] border border-[var(--arr-card-border)] bg-[var(--arr-card-bg)]"
          >
            <table className="arr-table w-full text-[12px]">
              <caption id={runsTableCaptionId} className="sr-only">
                Recent Cleanerr workflow execution history
              </caption>
              <thead>
                <tr>
                  <th>Run</th>
                  <th>Workflow</th>
                  <th>Trigger</th>
                  <th>Mode</th>
                  <th>Status</th>
                  <th>Staged / Deleted</th>
                  <th>Space</th>
                  <th>Finished</th>
                </tr>
              </thead>
              <tbody>
                {runsQuery.isLoading ? (
                  <tr>
                    <td
                      colSpan={8}
                      className="py-8 text-center text-[var(--arr-muted)]"
                    >
                      Loading recent run history...
                    </td>
                  </tr>
                ) : (runsQuery.data ?? []).length === 0 ? (
                  <tr>
                    <td
                      colSpan={8}
                      className="py-8 text-center text-[var(--arr-muted)]"
                    >
                      No workflow runs recorded yet.
                    </td>
                  </tr>
                ) : (
                  (runsQuery.data ?? []).map((run, idx) => (
                    <tr key={run.runId ?? `run-${idx}`}>
                      <th
                        scope="row"
                        className="text-left font-mono text-[var(--arr-text)]"
                      >
                        #{run.runId ?? idx + 1}
                      </th>
                      <td data-label="Workflow">
                        {run.workflowId
                          ? workflowNameById.get(run.workflowId) ??
                            `Workflow #${run.workflowId}`
                          : 'Ad-hoc'}
                      </td>
                      <td data-label="Trigger">
                        <ArrLabel kind="default">
                          {run.triggerType ?? 'manual'}
                        </ArrLabel>
                      </td>
                      <td data-label="Mode">
                        <ArrLabel kind={run.dryRun ? 'warning' : 'danger'}>
                          {run.dryRun ? 'Dry-Run' : 'Live'}
                        </ArrLabel>
                      </td>
                      <td data-label="Status">
                        <ArrLabel
                          kind={
                            run.status === 'failed'
                              ? 'danger'
                              : run.status === 'completed'
                                ? 'success'
                                : 'info'
                          }
                        >
                          {run.status ?? 'completed'}
                        </ArrLabel>
                      </td>
                      <td data-label="Staged / Deleted" className="font-mono">
                        {run.summary?.stagedCount ?? 0} /{' '}
                        {run.summary?.deletedCount ?? 0}
                      </td>
                      <td
                        data-label="Space"
                        className="font-mono whitespace-nowrap"
                      >
                        {formatBytes(
                          run.summary?.reclaimableSpaceBytes ??
                            run.summary?.spaceBytes ??
                            0,
                        )}
                      </td>
                      <td
                        data-label="Finished"
                        className="font-mono text-[11px] text-[var(--arr-muted)]"
                      >
                        {formatDateTime(run.finishedAt ?? run.startedAt)}
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
        </FieldSet>
      </div>

      {/* Test Media Modal */}
      {showTestMediaModal && (
        <CleanerrTestMediaModal
          ruleGroup={editorMode === 'rules' ? ruleGroup : null}
          graphJson={
            editorMode === 'dag'
              ? { nodes: graphJson.nodes, edges: graphJson.edges }
              : null
          }
          workflowId={selectedWorkflowId}
          onClose={() => setShowTestMediaModal(false)}
        />
      )}

      {/* YAML / JSON Modal */}
      {showYamlModal && (
        <CleanerrYamlModal
          workflowId={selectedWorkflowId}
          canImport={canWrite}
          onClose={() => setShowYamlModal(false)}
          onImportSuccess={(imported) => {
            loadWorkflow(imported);
            setBanner({
              kind: 'success',
              message: `Imported workflow "${imported.name}".`,
            });
          }}
        />
      )}

      {/* Manual Stage Selected Items Modal */}
      {manualStageIds && (
        <CleanerrManualStageModal
          initialSelectedIds={manualStageIds}
          onClose={() => setManualStageIds(null)}
          onSuccess={(count) => {
            setSelectedMatchedIds([]);
            setBanner({
              kind: 'success',
              message: `Staged ${count} selected item(s) to Leaving Soon.`,
            });
          }}
        />
      )}

      {/* Delete Workflow Confirm Modal */}
      {deleteTarget && (
        <CleanerrConfirmModal
          title="Delete Cleanerr Workflow"
          confirmLabel="Delete Workflow"
          confirmVariant="danger"
          busy={deleteWorkflowMutation.isPending}
          onClose={() => setDeleteTarget(null)}
          onConfirm={() => deleteWorkflowMutation.mutate(deleteTarget.id)}
          description={
            <p>
              Are you sure you want to delete workflow{' '}
              <strong className="text-[var(--arr-text)]">
                {deleteTarget.name}
              </strong>
              ? This does not remove already staged queue items or exclusions.
            </p>
          }
        />
      )}

      {/* Live-Enable Impact Preview & Name Confirmation Modal */}
      {liveImpactModalOpen && liveImpactPreview && (
        <ArrModal
          title={
            <span className="inline-flex items-center gap-2">
              <AlertTriangle
                aria-hidden="true"
                className="h-5 w-5 text-[#ffa726]"
              />
              <span>Confirm Live Mode Impact</span>
            </span>
          }
          onClose={() => setLiveImpactModalOpen(false)}
          maxWidth="max-w-2xl"
          footer={
            <>
              <ArrButton
                variant="default"
                autoFocus
                disabled={saveWorkflowMutation.isPending}
                onClick={() => setLiveImpactModalOpen(false)}
              >
                Cancel
              </ArrButton>
              <ArrButton
                variant="danger"
                busy={saveWorkflowMutation.isPending}
                disabled={
                  confirmNameInput.trim() !== name.trim() || !name.trim()
                }
                onClick={() =>
                  saveWorkflowMutation.mutate(confirmNameInput.trim())
                }
              >
                Confirm &amp; Save Live Workflow
              </ArrButton>
            </>
          }
        >
          <div className="space-y-4 text-[13px] text-[var(--arr-text-dim)]">
            <p>
              Saving <strong className="text-[var(--arr-text)]">{name}</strong>{' '}
              with <strong>Dry-Run disabled</strong>
              {enabled ? (
                <>
                  {' '}
                  and <strong>Enabled</strong>
                </>
              ) : null}{' '}
              allows Cleanerr to stage and execute real cleanup actions. Review
              the live impact below before confirming:
            </p>

            <div className="grid grid-cols-2 sm:grid-cols-3 gap-2.5">
              <div className="p-3 rounded bg-[var(--arr-card-center-bg)] border border-[var(--arr-card-border)]">
                <div className="text-[11px] uppercase text-[var(--arr-muted)] font-semibold">
                  Matched Items
                </div>
                <div className="text-[18px] font-bold text-[var(--arr-text)] mt-0.5">
                  {liveImpactPreview.itemCount}
                </div>
              </div>
              <div className="p-3 rounded bg-[var(--arr-card-center-bg)] border border-[var(--arr-card-border)]">
                <div className="text-[11px] uppercase text-[var(--arr-muted)] font-semibold">
                  Impact Volume
                </div>
                <div className="text-[18px] font-bold text-[#ffa726] mt-0.5">
                  {liveImpactPreview.totalGigabytes.toFixed(2)} GB
                </div>
                <div className="text-[10px] text-[var(--arr-muted)]">
                  {formatBytes(liveImpactPreview.totalBytes)}
                </div>
              </div>
              <div className="p-3 rounded bg-[var(--arr-card-center-bg)] border border-[var(--arr-card-border)]">
                <div className="text-[11px] uppercase text-[var(--arr-muted)] font-semibold">
                  Series / Movies
                </div>
                <div className="text-[15px] font-bold text-[var(--arr-accent)] mt-0.5">
                  {liveImpactPreview.affectedSeriesCount} Series •{' '}
                  {liveImpactPreview.affectedMovieCount} Movies
                </div>
              </div>
            </div>

            {liveImpactPreview.matchedItems.length > 0 && (
              <div className="space-y-1.5">
                <div className="text-[12px] font-semibold text-[var(--arr-text)]">
                  Sample Matched Items ({liveImpactPreview.matchedItems.length}
                  ):
                </div>
                <div className="max-h-40 overflow-y-auto rounded border border-[var(--arr-card-border)] bg-[var(--arr-card-center-bg)] divide-y divide-[var(--arr-divider)]">
                  {liveImpactPreview.matchedItems.slice(0, 10).map((item) => (
                    <div
                      key={item.id}
                      className="px-3 py-1.5 flex items-center justify-between gap-2 text-[12px]"
                    >
                      <span className="truncate text-[var(--arr-text)]">
                        {item.parentTitle
                          ? `${item.parentTitle} - ${item.title}`
                          : item.title}
                        {item.year ? ` (${item.year})` : ''}
                      </span>
                      <span className="font-mono text-[11px] text-[var(--arr-muted)] shrink-0">
                        {formatBytes(item.sizeBytes)}
                      </span>
                    </div>
                  ))}
                </div>
              </div>
            )}

            <div className="p-3.5 rounded border border-[#f05050]/50 bg-[#f05050]/10 space-y-2">
              <label
                htmlFor={`${baseId}-confirm-wf-name`}
                className="block text-[12px] font-semibold text-[var(--arr-text)]"
              >
                Type exact workflow name{' '}
                <code className="px-1 py-0.5 rounded bg-black/30 font-mono text-[var(--arr-accent)]">
                  {name.trim()}
                </code>{' '}
                to confirm live activation:
              </label>
              <input
                id={`${baseId}-confirm-wf-name`}
                type="text"
                value={confirmNameInput}
                onChange={(e) => setConfirmNameInput(e.target.value)}
                placeholder={name.trim()}
                className="arr-input"
              />
            </div>
          </div>
        </ArrModal>
      )}
    </div>
  );
}
