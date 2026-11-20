import { useCallback, useEffect, useId, useMemo, useState } from 'react';
import {
  Background,
  BackgroundVariant,
  Controls,
  MiniMap,
  ReactFlow,
  addEdge,
  type Connection,
  type Edge,
  type Node,
  type OnEdgesChange,
  type OnNodesChange,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import {
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  LayoutGrid,
  Layers,
  Plus,
  ShieldBan,
  Sliders,
  Trash2,
} from 'lucide-react';
import { ArrAlert, ArrButton, ArrModal } from '../arr/ArrPrimitives';
import type { QualityProfileSummary } from '../catalog/catalogTypesAndHelpers';
import {
  CleanerrWorkflowNode,
  formatReclaimableGb,
  getCleanerrNodeVisuals,
  type CleanerrWorkflowNodeData,
} from './CleanerrWorkflowNode';
import type {
  CleanerrNodeCatalogEntry,
  CleanerrNodeMetric,
  LibraryOption,
} from './cleanerrTypes';

export interface CleanerrDagGraph {
  nodes: Array<Record<string, unknown>>;
  edges: Array<Record<string, unknown>>;
}

export interface CleanerrDagStudioProps {
  graphJson: CleanerrDagGraph;
  onChange: (graph: CleanerrDagGraph) => void;
  nodeCatalog?: CleanerrNodeCatalogEntry[];
  libraries?: LibraryOption[];
  profiles?: QualityProfileSummary[];
  nodeMetrics?: Record<string, CleanerrNodeMetric>;
  onBulkExcludeIds?: (ids: number[]) => void;
  excludingIds?: boolean;
  readOnly?: boolean;
}

export const DEFAULT_CLEANERR_NODE_CATALOG: CleanerrNodeCatalogEntry[] = [
  // Triggers
  {
    nodeType: 'trigger.schedule',
    category: 'trigger',
    label: 'Cron Schedule Trigger',
    description: 'Run workflow automatically on a cron schedule (e.g. 0 3 * * *).',
    color: '#ffc230',
    defaultConfig: { cron: '0 3 * * *' },
  },
  {
    nodeType: 'trigger.webhook',
    category: 'trigger',
    label: 'Webhook Trigger',
    description: 'Triggered by inbound Plex, Radarr, Sonarr, or Seerr events.',
    color: '#ffc230',
    defaultConfig: { source: 'plex', event: 'media.scrobble' },
  },
  {
    nodeType: 'trigger.manual',
    category: 'trigger',
    label: 'Manual / API Trigger',
    description: 'Triggered on-demand from the Cleanerr UI or REST API.',
    color: '#ffc230',
    defaultConfig: { allow_api: true },
  },
  {
    nodeType: 'trigger.storage_pressure',
    category: 'trigger',
    label: 'Storage Pressure Trigger',
    description: 'Trigger cleanup when drive free space drops below threshold.',
    color: '#ffc230',
    defaultConfig: { min_free_gb_trigger: 200, target_free_gb: 500 },
  },

  // Sources
  {
    nodeType: 'source.plex_library',
    category: 'source',
    label: 'Plex Library Source',
    description: 'Load synced items and watch history from Plex libraries.',
    color: '#35c5f4',
    defaultConfig: { media_type: 'movie', library_ids: [] },
  },
  {
    nodeType: 'source.managerr_catalog',
    category: 'source',
    label: 'Managerr Catalog Source',
    description: 'Loads movies and whole TV series directly from the Managerr catalog.',
    color: '#35c5f4',
    defaultConfig: { media_type: 'movie', library_ids: [] },
  },

  // Filters
  {
    nodeType: 'filter.watch_state',
    category: 'filter',
    label: 'Watch State Filter',
    description: 'Filter by days since last watched, view count, or unwatched age.',
    color: '#a855f7',
    defaultConfig: {
      last_watched_older_than_days: 90,
      unwatched_added_older_than_days: 0,
      view_count_gte: 0,
      watched_by_user: '',
      include_unwatched_if_added_older: false,
    },
  },
  {
    nodeType: 'filter.metadata',
    category: 'filter',
    label: 'Size & Metadata Filter',
    description: 'Filter by file size (GB), resolution, rating, year, or genre.',
    color: '#a855f7',
    defaultConfig: {
      size_gt_gb: 0,
      resolution: '',
      rating_lt: 0,
      year_lt: 0,
      genre: '',
    },
  },
  {
    nodeType: 'filter.arr_seerr',
    category: 'filter',
    label: 'Arr / Seerr Filter',
    description: 'Filter by monitored state, tags, Seerr requester, or hardlinks.',
    color: '#a855f7',
    defaultConfig: {
      include_tags: [],
      exclude_tags: ['keep'],
      monitored_only: false,
      seerr_requested_by: '',
      seerr_requested_older_than_days: 0,
      skip_hardlinked_seeding: true,
    },
  },
  {
    nodeType: 'filter.codec_density',
    category: 'filter',
    label: 'Codec & Density Filter',
    description: 'Filter by video codecs, minimum file size, and dual-audio protection.',
    color: '#a855f7',
    defaultConfig: {
      codecs: ['h264'],
      min_size_gb: 15,
      protect_dual_audio: true,
      skip_hardlinked_seeding: true,
    },
  },
  {
    nodeType: 'filter.exclusion_check',
    category: 'filter',
    label: 'Exclusion Rules Guard',
    description: 'Protect excluded items, protected tags, and user quotas.',
    color: '#27c24c',
    defaultConfig: {
      respect_global_exclusions: true,
      respect_user_quotas: true,
      excluded_tags: ['keep', 'protected'],
    },
  },

  // Logic & Limits
  {
    nodeType: 'logic.combinator',
    category: 'logic',
    label: 'Logic Combinator (AND/OR/NOT)',
    description: 'Combine or invert upstream media streams using boolean operations.',
    color: '#5d9cec',
    defaultConfig: { operator: 'AND' },
  },
  {
    nodeType: 'limit.reclaim_budget',
    category: 'limit',
    label: 'Reclaim Budget Limit',
    description: 'Cap reclaimed GB per run or stop at target free space watermark.',
    color: '#ffa726',
    defaultConfig: {
      max_reclaim_gb: 250,
      target_free_space_gb_watermark: 500,
      min_free_space_gb_trigger: 0,
    },
  },

  // Queue
  {
    nodeType: 'queue.grace_period',
    category: 'queue',
    label: 'Grace Period Stage',
    description: 'Stage matched items in Leaving Soon queue with optional auto-rescue.',
    color: '#f97316',
    defaultConfig: {
      grace_days: 7,
      discord_webhook_url: '',
      auto_rescue_on_watch: true,
    },
  },

  // Actions
  {
    nodeType: 'action.managerr',
    category: 'action',
    label: 'Managerr Lifecycle Action',
    description: 'Delete files, unmonitor, downgrade profile, or clean empty folders.',
    color: '#f05050',
    defaultConfig: {
      mode: 'delete_files_and_unmonitor',
      unmonitor: true,
      clean_empty_folders: true,
      reset_requests: false,
      prune_torrent_client: false,
      target_profile_id: null,
    },
  },
  {
    nodeType: 'action.seerr',
    category: 'action',
    label: 'Seerr Request Action',
    description: 'Reset Seerr media status or remove requests for re-requesting.',
    color: '#f05050',
    defaultConfig: { mode: 'reset_media_status' },
  },
  {
    nodeType: 'action.plex',
    category: 'action',
    label: 'Plex Maintenance Action',
    description: 'Refresh library section or empty trash in Plex.',
    color: '#f05050',
    defaultConfig: { mode: 'refresh_library_and_empty_trash' },
  },
  {
    nodeType: 'action.webhook',
    category: 'action',
    label: 'Webhook Notification',
    description: 'Send JSON webhook summary of staged or cleaned items.',
    color: '#f05050',
    defaultConfig: { url: '', include_items: true },
  },
];

const CATEGORY_ORDER: Array<{ key: string; label: string }> = [
  { key: 'all', label: 'All Nodes' },
  { key: 'trigger', label: 'Triggers' },
  { key: 'source', label: 'Sources' },
  { key: 'filter', label: 'Filters' },
  { key: 'logic', label: 'Logic' },
  { key: 'limit', label: 'Limits' },
  { key: 'queue', label: 'Queue' },
  { key: 'action', label: 'Actions' },
];

const FORBIDDEN_NODE_TYPES = new Set([
  'action.compress_av1',
  'queue.plex_collection',
  'source.granularity',
]);

const CATEGORY_KEY_SET = new Set([
  'trigger',
  'source',
  'filter',
  'logic',
  'limit',
  'queue',
  'action',
]);

function normalizeCatalogCategory(entry: CleanerrNodeCatalogEntry): string {
  const nt = entry.nodeType.toLowerCase();
  if (nt.startsWith('trigger.')) return 'trigger';
  if (nt.startsWith('source.')) return 'source';
  if (nt.startsWith('logic.')) return 'logic';
  if (nt.startsWith('limit.') || nt === 'filter.limit') return 'limit';
  if (nt.startsWith('filter.')) return 'filter';
  if (nt.startsWith('queue.')) return 'queue';
  if (nt.startsWith('notify.') || nt.startsWith('action.')) return 'action';

  const cat = entry.category.trim().toLowerCase();
  if (CATEGORY_KEY_SET.has(cat)) return cat;
  if (cat === 'triggers') return 'trigger';
  if (cat === 'sources') return 'source';
  if (cat === 'filters') return 'filter';
  if (cat === 'limits') return 'limit';
  if (cat === 'actions') return 'action';
  if (cat === 'queue & notify') return nt.startsWith('notify.') ? 'action' : 'queue';
  if (cat === 'logic & filters') {
    if (nt.startsWith('logic.')) return 'logic';
    if (nt.startsWith('limit.') || nt === 'filter.limit') return 'limit';
    return 'filter';
  }
  return cat;
}

function computeTopologicalLayout(
  nodes: Array<Record<string, unknown>>,
  edges: Array<Record<string, unknown>>,
): Array<Record<string, unknown>> {
  const nodeIds = nodes.map((n) => String(n.id));
  const idSet = new Set(nodeIds);
  const adj = new Map<string, string[]>();
  const inDegree = new Map<string, number>();
  const level = new Map<string, number>();

  for (const id of nodeIds) {
    adj.set(id, []);
    inDegree.set(id, 0);
    level.set(id, 0);
  }

  for (const edge of edges) {
    const src = String(edge.source ?? '');
    const tgt = String(edge.target ?? '');
    if (!idSet.has(src) || !idSet.has(tgt) || src === tgt) continue;
    adj.get(src)!.push(tgt);
    inDegree.set(tgt, (inDegree.get(tgt) ?? 0) + 1);
  }

  const queue: string[] = [];
  for (const id of nodeIds) {
    if ((inDegree.get(id) ?? 0) === 0) queue.push(id);
  }

  const visited = new Set<string>();
  while (queue.length > 0) {
    const u = queue.shift()!;
    visited.add(u);
    const uLevel = level.get(u) ?? 0;
    for (const v of adj.get(u) ?? []) {
      if ((level.get(v) ?? 0) < uLevel + 1) {
        level.set(v, uLevel + 1);
      }
      const nextDeg = (inDegree.get(v) ?? 1) - 1;
      inDegree.set(v, nextDeg);
      if (nextDeg === 0) queue.push(v);
    }
  }

  // Fallback for any nodes inside cycles
  for (const id of nodeIds) {
    if (!visited.has(id)) {
      const node = nodes.find((n) => String(n.id) === id);
      const dataObj = (node?.data as Record<string, unknown> | undefined) ?? {};
      const nt = String(dataObj.node_type ?? '');
      const fallbackCol = nt.startsWith('trigger.')
        ? 0
        : nt.startsWith('source.')
          ? 1
          : nt.startsWith('filter.') || nt.startsWith('logic.') || nt.startsWith('limit.')
            ? 2
            : nt.startsWith('queue.')
              ? 3
              : 4;
      level.set(id, fallbackCol);
    }
  }

  const columns = new Map<number, Array<Record<string, unknown>>>();
  for (const node of nodes) {
    const col = level.get(String(node.id)) ?? 0;
    if (!columns.has(col)) columns.set(col, []);
    columns.get(col)!.push(node);
  }

  const positioned = new Map<string, { x: number; y: number }>();
  for (const [col, colNodes] of columns.entries()) {
    colNodes.sort((a, b) => {
      const ay = Number((a.position as { y?: number } | undefined)?.y ?? 0);
      const by = Number((b.position as { y?: number } | undefined)?.y ?? 0);
      return ay - by;
    });
    colNodes.forEach((node, rowIdx) => {
      positioned.set(String(node.id), {
        x: 60 + col * 320,
        y: 80 + rowIdx * 185,
      });
    });
  }

  return nodes.map((n) => ({
    ...n,
    position: positioned.get(String(n.id)) ?? n.position ?? { x: 60, y: 80 },
  }));
}

function getRawNodeData(node: Record<string, unknown> | undefined): {
  label: string;
  node_type: string;
  config: Record<string, unknown>;
} {
  const data = (node?.data as Record<string, unknown> | undefined) ?? {};
  return {
    label: String(data.label ?? node?.id ?? 'Step'),
    node_type: String(data.node_type ?? 'filter.watch_state'),
    config:
      data.config && typeof data.config === 'object' && !Array.isArray(data.config)
        ? { ...(data.config as Record<string, unknown>) }
        : {},
  };
}

function parseCommaList(raw: string): string[] {
  return raw
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean);
}

function formatCommaList(val: unknown): string {
  return Array.isArray(val) ? val.map(String).join(', ') : '';
}

function InspectorTextField({
  id,
  label,
  value,
  onChange,
  placeholder,
  mono,
  disabled,
}: {
  id: string;
  label: string;
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  mono?: boolean;
  disabled?: boolean;
}) {
  return (
    <div>
      <label htmlFor={id} className="block text-[11px] font-medium text-[#ccc] mb-1">
        {label}
      </label>
      <input
        id={id}
        type="text"
        value={value}
        disabled={disabled}
        placeholder={placeholder}
        onChange={(e) => onChange(e.target.value)}
        className={`arr-input !h-[30px] !text-[12px] ${mono ? 'font-mono' : ''}`}
      />
    </div>
  );
}

function InspectorNumberField({
  id,
  label,
  value,
  onChange,
  min = 0,
  max,
  step = 1,
  disabled,
}: {
  id: string;
  label: string;
  value: number;
  onChange: (value: number) => void;
  min?: number;
  max?: number;
  step?: number;
  disabled?: boolean;
}) {
  return (
    <div>
      <label htmlFor={id} className="block text-[11px] font-medium text-[#ccc] mb-1">
        {label}
      </label>
      <input
        id={id}
        type="number"
        min={min}
        max={max}
        step={step}
        value={Number.isFinite(value) ? value : 0}
        disabled={disabled}
        onChange={(e) => onChange(Number(e.target.value) || 0)}
        className="arr-input !h-[30px] !text-[12px] font-mono"
      />
    </div>
  );
}

function InspectorCheckboxField({
  id,
  label,
  checked,
  onChange,
  disabled,
}: {
  id: string;
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
}) {
  return (
    <div className="flex items-start gap-2 pt-1">
      <input
        id={id}
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
        className="mt-0.5 rounded-[2px] cursor-pointer"
      />
      <label htmlFor={id} className="text-[11px] text-[#ccc] cursor-pointer leading-snug">
        {label}
      </label>
    </div>
  );
}

export function CleanerrDagStudio({
  graphJson,
  onChange,
  nodeCatalog,
  libraries = [],
  profiles = [],
  nodeMetrics,
  onBulkExcludeIds,
  excludingIds = false,
  readOnly = false,
}: CleanerrDagStudioProps) {
  const baseId = useId();
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(null);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [paletteCategory, setPaletteCategory] = useState<string>('all');
  const [advancedJsonOpen, setAdvancedJsonOpen] = useState(false);
  const [inspectorJson, setInspectorJson] = useState('');
  const [inspectorError, setInspectorError] = useState<string | null>(null);

  const catalog = useMemo(() => {
    const source = nodeCatalog && nodeCatalog.length > 0 ? nodeCatalog : DEFAULT_CLEANERR_NODE_CATALOG;
    return source.filter((entry) => !FORBIDDEN_NODE_TYPES.has(entry.nodeType));
  }, [nodeCatalog]);

  const filteredPalette = useMemo(() => {
    if (paletteCategory === 'all') return catalog;
    return catalog.filter(
      (entry) => normalizeCatalogCategory(entry) === paletteCategory,
    );
  }, [catalog, paletteCategory]);

  const handleNodeLabelChange = useCallback(
    (nodeId: string, nextLabel: string) => {
      if (readOnly) return;
      const nextNodes = (graphJson.nodes ?? []).map((n) => {
        if (String(n.id) !== nodeId) return n;
        const current = getRawNodeData(n);
        return {
          ...n,
          data: {
            ...current,
            label: nextLabel,
          },
        };
      });
      onChange({ nodes: nextNodes, edges: graphJson.edges ?? [] });
    },
    [graphJson.edges, graphJson.nodes, onChange, readOnly],
  );

  const rfNodes: Node[] = useMemo(() => {
    return (graphJson.nodes ?? []).map((n, idx) => {
      const id = String(n.id);
      const raw = getRawNodeData(n);
      const pos = n.position as { x: number; y: number } | undefined;
      const metric = nodeMetrics?.[id];
      const nodeData: CleanerrWorkflowNodeData = {
        label: raw.label,
        node_type: raw.node_type,
        config: raw.config,
        metric,
        onLabelChange: readOnly ? undefined : handleNodeLabelChange,
      };
      return {
        id,
        type: 'workflowNode',
        position: pos ?? { x: 60 + idx * 290, y: 120 },
        selected: id === selectedNodeId,
        data: nodeData,
      };
    });
  }, [graphJson.nodes, handleNodeLabelChange, nodeMetrics, readOnly, selectedNodeId]);

  const rfEdges: Edge[] = useMemo(() => {
    return (graphJson.edges ?? []).map((e, idx) => ({
      id: String(e.id ?? `e-${e.source}-${e.target}-${idx}`),
      source: String(e.source),
      target: String(e.target),
      animated: true,
      style: { stroke: 'var(--arr-accent)', strokeWidth: 1.75 },
    }));
  }, [graphJson.edges]);

  const nodeTypes = useMemo(() => ({ workflowNode: CleanerrWorkflowNode }), []);

  const selectedGraphNode = useMemo(
    () => (graphJson.nodes ?? []).find((n) => String(n.id) === selectedNodeId) ?? null,
    [graphJson.nodes, selectedNodeId],
  );

  const selectedRawData = useMemo(
    () => (selectedGraphNode ? getRawNodeData(selectedGraphNode) : null),
    [selectedGraphNode],
  );

  const selectedMetric = selectedNodeId ? nodeMetrics?.[selectedNodeId] : undefined;

  useEffect(() => {
    if (selectedRawData) {
      setInspectorJson(JSON.stringify(selectedRawData, null, 2));
      setInspectorError(null);
    } else {
      setInspectorJson('');
      setInspectorError(null);
    }
  }, [selectedNodeId, selectedRawData]);

  const onNodesChange: OnNodesChange<Node> = useCallback(
    (changes) => {
      if (readOnly) return;
      const updated = changes.applyTo(rfNodes);
      const nextNodes = updated.map((rfNode) => {
        const orig = (graphJson.nodes ?? []).find((n) => String(n.id) === rfNode.id);
        const raw = getRawNodeData(orig);
        return {
          ...(orig ?? {}),
          id: rfNode.id,
          type: 'workflowNode',
          position: rfNode.position,
          data: {
            label: raw.label,
            node_type: raw.node_type,
            config: raw.config,
          },
        };
      });
      onChange({ nodes: nextNodes, edges: graphJson.edges ?? [] });
    },
    [graphJson.edges, graphJson.nodes, onChange, readOnly, rfNodes],
  );

  const onEdgesChange: OnEdgesChange<Edge> = useCallback(
    (changes) => {
      if (readOnly) return;
      const updated = changes.applyTo(rfEdges);
      const nextEdges = updated.map((rfEdge) => ({
        id: rfEdge.id,
        source: rfEdge.source,
        target: rfEdge.target,
      }));
      onChange({ nodes: graphJson.nodes ?? [], edges: nextEdges });
    },
    [graphJson.nodes, onChange, readOnly, rfEdges],
  );

  const onConnect = useCallback(
    (connection: Connection) => {
      if (readOnly) return;
      const updated = addEdge(connection, rfEdges);
      const nextEdges = updated.map((rfEdge) => ({
        id: rfEdge.id,
        source: rfEdge.source,
        target: rfEdge.target,
      }));
      onChange({ nodes: graphJson.nodes ?? [], edges: nextEdges });
    },
    [graphJson.nodes, onChange, readOnly, rfEdges],
  );

  const updateSelectedNode = useCallback(
    (updater: (current: { label: string; node_type: string; config: Record<string, unknown> }) => {
      label: string;
      node_type: string;
      config: Record<string, unknown>;
    }) => {
      if (!selectedNodeId || readOnly) return;
      const nextNodes = (graphJson.nodes ?? []).map((n) => {
        if (String(n.id) !== selectedNodeId) return n;
        const current = getRawNodeData(n);
        return {
          ...n,
          type: 'workflowNode',
          data: updater(current),
        };
      });
      onChange({ nodes: nextNodes, edges: graphJson.edges ?? [] });
    },
    [graphJson.edges, graphJson.nodes, onChange, readOnly, selectedNodeId],
  );

  const updateConfigField = useCallback(
    (key: string, value: unknown) => {
      updateSelectedNode((curr) => ({
        ...curr,
        config: {
          ...curr.config,
          [key]: value,
        },
      }));
    },
    [updateSelectedNode],
  );

  const addNodeFromCatalog = (entry: CleanerrNodeCatalogEntry) => {
    if (readOnly) return;
    const newId = `node_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 5)}`;
    const maxX = (graphJson.nodes ?? []).reduce((acc, n) => {
      const x = Number((n.position as { x?: number } | undefined)?.x ?? 0);
      return Math.max(acc, x);
    }, -220);

    const newNode: Record<string, unknown> = {
      id: newId,
      type: 'workflowNode',
      position: {
        x: (graphJson.nodes ?? []).length > 0 ? maxX + 300 : 60,
        y: 120,
      },
      data: {
        label: entry.label,
        node_type: entry.nodeType,
        config: { ...entry.defaultConfig },
      },
    };
    onChange({
      nodes: [...(graphJson.nodes ?? []), newNode],
      edges: graphJson.edges ?? [],
    });
    setSelectedNodeId(newId);
    setPaletteOpen(false);
  };

  const handleAutoLayout = () => {
    const layouted = computeTopologicalLayout(graphJson.nodes ?? [], graphJson.edges ?? []);
    onChange({ nodes: layouted, edges: graphJson.edges ?? [] });
  };

  const deleteSelectedNode = () => {
    if (!selectedNodeId || readOnly) return;
    const nextNodes = (graphJson.nodes ?? []).filter((n) => String(n.id) !== selectedNodeId);
    const nextEdges = (graphJson.edges ?? []).filter(
      (e) => String(e.source) !== selectedNodeId && String(e.target) !== selectedNodeId,
    );
    onChange({ nodes: nextNodes, edges: nextEdges });
    setSelectedNodeId(null);
  };

  const applyRawJsonConfig = () => {
    if (!selectedNodeId || readOnly) return;
    try {
      const parsed = JSON.parse(inspectorJson) as Record<string, unknown>;
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
        setInspectorError('Node data must be a JSON object.');
        return;
      }
      const nextNodes = (graphJson.nodes ?? []).map((n) => {
        if (String(n.id) !== selectedNodeId) return n;
        return { ...n, type: 'workflowNode', data: parsed };
      });
      onChange({ nodes: nextNodes, edges: graphJson.edges ?? [] });
      setInspectorError(null);
    } catch (err: unknown) {
      setInspectorError(err instanceof Error ? err.message : 'Invalid JSON syntax');
    }
  };

  const renderLibraryCheckboxes = (selectedIdsRaw: unknown) => {
    const selectedIds = Array.isArray(selectedIdsRaw) ? selectedIdsRaw.map(Number) : [];
    if (libraries.length === 0) {
      return (
        <p className="text-[11px] text-[#888888] italic">
          No Plex libraries loaded; evaluates all libraries by default.
        </p>
      );
    }
    return (
      <fieldset className="m-0 p-0 border-0">
        <legend className="text-[11px] font-medium text-[#ccc] mb-1">
          Filter Specific Libraries (Optional)
        </legend>
        <div
          className="space-y-1 max-h-32 overflow-y-auto p-2 rounded-[3px] border"
          style={{ backgroundColor: 'var(--arr-input-bg)', borderColor: 'var(--arr-card-border)' }}
        >
          {libraries.map((lib) => {
            const libId = `${baseId}-lib-${lib.id}`;
            const checked = selectedIds.includes(lib.id);
            const libKind = lib.libraryType ?? lib.mediaType ?? lib.mediaVertical ?? 'media';
            return (
              <div key={lib.id} className="flex items-center gap-2">
                <input
                  id={libId}
                  type="checkbox"
                  checked={checked}
                  disabled={readOnly}
                  onChange={(e) => {
                    const next = e.target.checked
                      ? [...selectedIds, lib.id]
                      : selectedIds.filter((id) => id !== lib.id);
                    updateConfigField('library_ids', next);
                  }}
                />
                <label htmlFor={libId} className="text-[11px] text-[#ccc] cursor-pointer">
                  {lib.name} ({libKind})
                </label>
              </div>
            );
          })}
        </div>
      </fieldset>
    );
  };

  const renderStructuredConfigFields = (
    nodeType: string,
    cfg: Record<string, unknown>,
  ) => {
    const fId = (name: string) => `${baseId}-cfg-${name}`;

    switch (nodeType) {
      case 'trigger.schedule':
        return (
          <InspectorTextField
            id={fId('cron')}
            label="Cron Expression"
            value={String(cfg.cron ?? '0 3 * * *')}
            onChange={(v) => updateConfigField('cron', v)}
            placeholder="0 3 * * *"
            mono
            disabled={readOnly}
          />
        );

      case 'trigger.webhook':
        return (
          <>
            <div>
              <label htmlFor={fId('wh-source')} className="block text-[11px] font-medium text-[#ccc] mb-1">
                Webhook Source
              </label>
              <select
                id={fId('wh-source')}
                value={String(cfg.source ?? 'plex')}
                disabled={readOnly}
                onChange={(e) => updateConfigField('source', e.target.value)}
                className="arr-input !h-[30px] !py-0 !text-[12px]"
              >
                <option value="plex">Plex</option>
                <option value="managerr">Managerr</option>
                <option value="seerr">Seerr / Overseerr</option>
                <option value="radarr">Radarr</option>
                <option value="sonarr">Sonarr</option>
              </select>
            </div>
            <InspectorTextField
              id={fId('wh-event')}
              label="Event Name"
              value={String(cfg.event ?? 'media.scrobble')}
              onChange={(v) => updateConfigField('event', v)}
              mono
              disabled={readOnly}
            />
          </>
        );

      case 'trigger.manual':
        return (
          <InspectorCheckboxField
            id={fId('allow-api')}
            label="Allow execution via REST API trigger"
            checked={cfg.allow_api !== false}
            onChange={(v) => updateConfigField('allow_api', v)}
            disabled={readOnly}
          />
        );

      case 'trigger.storage_pressure':
        return (
          <>
            <InspectorNumberField
              id={fId('min-free-gb')}
              label="Trigger When Free Space Below (GB)"
              value={Number(cfg.min_free_gb_trigger ?? 200)}
              onChange={(v) => updateConfigField('min_free_gb_trigger', v)}
              disabled={readOnly}
            />
            <InspectorNumberField
              id={fId('min-free-pct')}
              label="Trigger When Free Space Below (%)"
              value={Number(cfg.min_free_percent_trigger ?? 0)}
              min={0}
              max={100}
              onChange={(v) => updateConfigField('min_free_percent_trigger', v)}
              disabled={readOnly}
            />
            <InspectorNumberField
              id={fId('target-free-gb')}
              label="Target Free Space Watermark (GB)"
              value={Number(cfg.target_free_gb ?? 500)}
              onChange={(v) => updateConfigField('target_free_gb', v)}
              disabled={readOnly}
            />
            <div>
              <label htmlFor={fId('press-sort-by')} className="block text-[11px] font-medium text-[#ccc] mb-1">
                Reclaim Priority Order
              </label>
              <select
                id={fId('press-sort-by')}
                value={String(cfg.sort_by ?? 'oldest_watched')}
                disabled={readOnly}
                onChange={(e) => updateConfigField('sort_by', e.target.value)}
                className="arr-input !h-[30px] !py-0 !text-[12px]"
              >
                <option value="oldest_watched">Oldest Watched First</option>
                <option value="largest_first">Largest File Size First</option>
                <option value="lowest_rating">Lowest Rating First</option>
                <option value="largest_gb_per_play">Largest GB per Play</option>
              </select>
            </div>
          </>
        );

      case 'source.plex_library':
      case 'source.managerr_catalog':
      case 'source.managerr_library':
      case 'source.tautulli':
      case 'source.arr_library':
      case 'source.overseerr': {
        const rawMediaType = String(cfg.media_type ?? 'movie');
        const normalizedMediaType =
          rawMediaType === 'season' || rawMediaType === 'episode' ? 'show' : rawMediaType;
        return (
          <>
            <div>
              <label htmlFor={fId('media-type')} className="block text-[11px] font-medium text-[#ccc] mb-1">
                Media Type
              </label>
              <select
                id={fId('media-type')}
                value={normalizedMediaType}
                disabled={readOnly}
                onChange={(e) => updateConfigField('media_type', e.target.value)}
                className="arr-input !h-[30px] !py-0 !text-[12px]"
              >
                <option value="movie">Movies</option>
                <option value="show">TV Series (Full)</option>
              </select>
            </div>
            {nodeType === 'source.overseerr' && (
              <InspectorTextField
                id={fId('src-req-by')}
                label="Requested By User (Optional)"
                value={String(cfg.requested_by ?? '')}
                onChange={(v) => updateConfigField('requested_by', v)}
                placeholder="Any requester"
                disabled={readOnly}
              />
            )}
            {nodeType === 'source.tautulli' && (
              <InspectorCheckboxField
                id={fId('src-watched-only')}
                label="Only include items with playback history"
                checked={Boolean(cfg.watched_only)}
                onChange={(v) => updateConfigField('watched_only', v)}
                disabled={readOnly}
              />
            )}
            {renderLibraryCheckboxes(cfg.library_ids)}
          </>
        );
      }

      case 'filter.watch_state':
        return (
          <>
            <InspectorNumberField
              id={fId('last-watched-days')}
              label="Last Watched Older Than (Days)"
              value={Number(cfg.last_watched_older_than_days ?? 0)}
              onChange={(v) => updateConfigField('last_watched_older_than_days', v)}
              disabled={readOnly}
            />
            <InspectorNumberField
              id={fId('unwatched-days')}
              label="Unwatched & Added Older Than (Days)"
              value={Number(cfg.unwatched_added_older_than_days ?? 0)}
              onChange={(v) => updateConfigField('unwatched_added_older_than_days', v)}
              disabled={readOnly}
            />
            <InspectorNumberField
              id={fId('view-count')}
              label="Minimum View Count (>=)"
              value={Number(cfg.view_count_gte ?? 0)}
              onChange={(v) => updateConfigField('view_count_gte', v)}
              disabled={readOnly}
            />
            <InspectorTextField
              id={fId('watched-user')}
              label="Watched By User (Optional)"
              value={String(cfg.watched_by_user ?? '')}
              onChange={(v) => updateConfigField('watched_by_user', v)}
              placeholder="All users"
              disabled={readOnly}
            />
            <InspectorNumberField
              id={fId('protect-series-days')}
              label="Protect Series Watched Within (Days)"
              value={Number(cfg.protect_active_series_days ?? 0)}
              onChange={(v) => updateConfigField('protect_active_series_days', v)}
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('include-unwatched')}
              label="Also match never-watched items added older than threshold"
              checked={Boolean(cfg.include_unwatched_if_added_older)}
              onChange={(v) => updateConfigField('include_unwatched_if_added_older', v)}
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('ws-protect-in-progress')}
              label="Protect items currently in-progress or streaming"
              checked={cfg.protect_in_progress !== false}
              onChange={(v) => updateConfigField('protect_in_progress', v)}
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('ws-protect-requester')}
              label="Protect requested items until requester has watched"
              checked={Boolean(cfg.protect_until_requester_watched)}
              onChange={(v) => updateConfigField('protect_until_requester_watched', v)}
              disabled={readOnly}
            />
          </>
        );

      case 'filter.age':
        return (
          <>
            <InspectorNumberField
              id={fId('age-added-days')}
              label="Added Older Than (Days)"
              value={Number(cfg.added_older_than_days ?? 0)}
              onChange={(v) => updateConfigField('added_older_than_days', v)}
              disabled={readOnly}
            />
            <InspectorNumberField
              id={fId('age-year-lt')}
              label="Release Year Before (<)"
              value={Number(cfg.release_year_lt ?? cfg.year_lt ?? 0)}
              onChange={(v) => updateConfigField('release_year_lt', v)}
              disabled={readOnly}
            />
            <InspectorNumberField
              id={fId('age-year-gt')}
              label="Release Year After (>)"
              value={Number(cfg.release_year_gt ?? cfg.year_gt ?? 0)}
              onChange={(v) => updateConfigField('release_year_gt', v)}
              disabled={readOnly}
            />
          </>
        );

      case 'filter.metadata':
        return (
          <>
            <InspectorNumberField
              id={fId('size-gt')}
              label="File Size Greater Than (GB)"
              value={Number(cfg.size_gt_gb ?? 0)}
              step={0.5}
              onChange={(v) => updateConfigField('size_gt_gb', v)}
              disabled={readOnly}
            />
            <div>
              <label htmlFor={fId('resolution')} className="block text-[11px] font-medium text-[#ccc] mb-1">
                Video Resolution
              </label>
              <select
                id={fId('resolution')}
                value={String(cfg.resolution ?? '')}
                disabled={readOnly}
                onChange={(e) => updateConfigField('resolution', e.target.value)}
                className="arr-input !h-[30px] !py-0 !text-[12px]"
              >
                <option value="">Any Resolution</option>
                <option value="4k">4K / 2160p</option>
                <option value="1080p">1080p Full HD</option>
                <option value="720p">720p HD</option>
                <option value="sd">SD</option>
              </select>
            </div>
            <InspectorNumberField
              id={fId('rating-lt')}
              label="Rating Below (<)"
              value={Number(cfg.rating_lt ?? 0)}
              step={0.1}
              max={10}
              onChange={(v) => updateConfigField('rating_lt', v)}
              disabled={readOnly}
            />
            <InspectorNumberField
              id={fId('year-lt')}
              label="Release Year Before (<)"
              value={Number(cfg.year_lt ?? 0)}
              onChange={(v) => updateConfigField('year_lt', v)}
              disabled={readOnly}
            />
            <InspectorTextField
              id={fId('genre')}
              label="Genre Contains (Optional)"
              value={String(cfg.genre ?? '')}
              onChange={(v) => updateConfigField('genre', v)}
              placeholder="e.g. Documentary"
              disabled={readOnly}
            />
          </>
        );

      case 'filter.arr_seerr':
        return (
          <>
            <InspectorTextField
              id={fId('seerr-user')}
              label="Seerr Requested By User"
              value={String(cfg.seerr_requested_by ?? '')}
              onChange={(v) => updateConfigField('seerr_requested_by', v)}
              placeholder="Leave empty for any requester"
              disabled={readOnly}
            />
            <InspectorNumberField
              id={fId('seerr-days')}
              label="Requested Older Than (Days)"
              value={Number(cfg.seerr_requested_older_than_days ?? 0)}
              onChange={(v) => updateConfigField('seerr_requested_older_than_days', v)}
              disabled={readOnly}
            />
            <InspectorTextField
              id={fId('include-tags')}
              label="Include Tags (comma-separated)"
              value={formatCommaList(cfg.include_tags)}
              onChange={(v) => updateConfigField('include_tags', parseCommaList(v))}
              disabled={readOnly}
            />
            <InspectorTextField
              id={fId('exclude-tags')}
              label="Exclude Tags (comma-separated)"
              value={formatCommaList(cfg.exclude_tags)}
              onChange={(v) => updateConfigField('exclude_tags', parseCommaList(v))}
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('monitored-only')}
              label="Only match monitored items"
              checked={Boolean(cfg.monitored_only)}
              onChange={(v) => updateConfigField('monitored_only', v)}
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('skip-hardlinked')}
              label="Skip hardlinked seeding files (st_nlink > 1)"
              checked={cfg.skip_hardlinked_seeding !== false}
              onChange={(v) => updateConfigField('skip_hardlinked_seeding', v)}
              disabled={readOnly}
            />
          </>
        );

      case 'filter.codec_density':
        return (
          <>
            <InspectorTextField
              id={fId('codecs')}
              label="Target Video Codecs (comma-separated)"
              value={formatCommaList(cfg.codecs)}
              onChange={(v) => updateConfigField('codecs', parseCommaList(v))}
              placeholder="h264, mpeg4"
              mono
              disabled={readOnly}
            />
            <InspectorNumberField
              id={fId('min-size-gb')}
              label="Minimum File Size (GB)"
              value={Number(cfg.min_size_gb ?? 0)}
              step={0.5}
              onChange={(v) => updateConfigField('min_size_gb', v)}
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('protect-dual-audio')}
              label="Protect dual-audio / multi-language releases"
              checked={Boolean(cfg.protect_dual_audio)}
              onChange={(v) => updateConfigField('protect_dual_audio', v)}
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('codec-skip-hardlinked')}
              label="Skip hardlinked seeding files (st_nlink > 1)"
              checked={cfg.skip_hardlinked_seeding !== false}
              onChange={(v) => updateConfigField('skip_hardlinked_seeding', v)}
              disabled={readOnly}
            />
          </>
        );

      case 'filter.size_codec':
        return (
          <>
            <InspectorNumberField
              id={fId('sc-size-gt')}
              label="File Size Greater Than (GB)"
              value={Number(cfg.size_gt_gb ?? cfg.min_size_gb ?? 0)}
              step={0.5}
              onChange={(v) => updateConfigField('size_gt_gb', v)}
              disabled={readOnly}
            />
            <InspectorNumberField
              id={fId('sc-size-lt')}
              label="File Size Less Than (GB)"
              value={Number(cfg.size_lt_gb ?? cfg.max_size_gb ?? 0)}
              step={0.5}
              onChange={(v) => updateConfigField('size_lt_gb', v)}
              disabled={readOnly}
            />
            <div>
              <label htmlFor={fId('sc-resolution')} className="block text-[11px] font-medium text-[#ccc] mb-1">
                Video Resolution
              </label>
              <select
                id={fId('sc-resolution')}
                value={String(cfg.resolution ?? '')}
                disabled={readOnly}
                onChange={(e) => updateConfigField('resolution', e.target.value)}
                className="arr-input !h-[30px] !py-0 !text-[12px]"
              >
                <option value="">Any Resolution</option>
                <option value="2160p">4K / 2160p</option>
                <option value="1080p">1080p Full HD</option>
                <option value="720p">720p HD</option>
                <option value="480p">480p / SD</option>
              </select>
            </div>
            <InspectorTextField
              id={fId('sc-codecs')}
              label="Target Video Codecs (comma-separated)"
              value={formatCommaList(cfg.codecs ?? cfg.codec_in)}
              onChange={(v) => updateConfigField('codecs', parseCommaList(v))}
              placeholder="h264, hevc, av1"
              mono
              disabled={readOnly}
            />
            <InspectorNumberField
              id={fId('sc-max-cf')}
              label="Max Custom Format Score (Protect Above)"
              value={Number(cfg.max_custom_format_score ?? 0)}
              onChange={(v) => updateConfigField('max_custom_format_score', v || null)}
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('sc-exclude-dv5')}
              label="Match only non-Dolby Vision Profile 5 (protect DV5)"
              checked={Boolean(cfg.exclude_dv5)}
              onChange={(v) => updateConfigField('exclude_dv5', v)}
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('sc-protect-dual')}
              label="Protect dual-audio / multi-language releases"
              checked={Boolean(cfg.protect_dual_audio)}
              onChange={(v) => updateConfigField('protect_dual_audio', v)}
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('sc-skip-hardlinked')}
              label="Skip hardlinked seeding files (st_nlink > 1)"
              checked={cfg.skip_hardlinked_seeding !== false}
              onChange={(v) => updateConfigField('skip_hardlinked_seeding', v)}
              disabled={readOnly}
            />
          </>
        );

      case 'filter.tags_labels':
        return (
          <>
            <InspectorTextField
              id={fId('tl-has-tags')}
              label="Require Any Tag / Label (comma-separated)"
              value={formatCommaList(cfg.has_tags ?? cfg.include_tags)}
              onChange={(v) => updateConfigField('has_tags', parseCommaList(v))}
              disabled={readOnly}
            />
            <InspectorTextField
              id={fId('tl-exclude-tags')}
              label="Exclude Tags / Labels (comma-separated)"
              value={formatCommaList(cfg.exclude_tags)}
              onChange={(v) => updateConfigField('exclude_tags', parseCommaList(v))}
              disabled={readOnly}
            />
            <InspectorTextField
              id={fId('tl-genres')}
              label="Match Any Genre (comma-separated)"
              value={formatCommaList(cfg.genre_any ?? cfg.genres_any)}
              onChange={(v) => updateConfigField('genre_any', parseCommaList(v))}
              disabled={readOnly}
            />
            <div>
              <label htmlFor={fId('tl-class')} className="block text-[11px] font-medium text-[#ccc] mb-1">
                Taxonomy Classification
              </label>
              <select
                id={fId('tl-class')}
                value={String(cfg.classification ?? '')}
                disabled={readOnly}
                onChange={(e) => updateConfigField('classification', e.target.value)}
                className="arr-input !h-[30px] !py-0 !text-[12px]"
              >
                <option value="">Any Classification</option>
                <option value="live-action-movie">Live-Action Movie</option>
                <option value="live-action-series">Live-Action Series</option>
                <option value="anime-movie">Anime Movie</option>
                <option value="anime-series">Anime Series</option>
                <option value="cartoon-movie">Cartoon Movie</option>
                <option value="cartoon-series">Cartoon Series</option>
              </select>
            </div>
          </>
        );

      case 'filter.active_stream_guard':
        return (
          <>
            <InspectorCheckboxField
              id={fId('asg-in-progress')}
              label="Protect items currently streaming or mid-playback"
              checked={cfg.protect_in_progress !== false}
              onChange={(v) => updateConfigField('protect_in_progress', v)}
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('asg-active-series')}
              label="Protect whole TV series if any episode is streaming"
              checked={Boolean(cfg.protect_active_series ?? cfg.protect_series)}
              onChange={(v) => updateConfigField('protect_active_series', v)}
              disabled={readOnly}
            />
          </>
        );

      case 'filter.disk_space':
        return (
          <>
            <InspectorNumberField
              id={fId('ds-min-gb')}
              label="Trigger Below Free Space (GB)"
              value={Number(cfg.min_free_gb ?? 250)}
              onChange={(v) => updateConfigField('min_free_gb', v)}
              disabled={readOnly}
            />
            <InspectorNumberField
              id={fId('ds-min-pct')}
              label="Trigger Below Free Space (%)"
              value={Number(cfg.min_free_percent ?? 0)}
              min={0}
              max={100}
              onChange={(v) => updateConfigField('min_free_percent', v)}
              disabled={readOnly}
            />
            <InspectorNumberField
              id={fId('ds-target-gb')}
              label="Target Free Watermark (GB)"
              value={Number(cfg.target_free_gb ?? 350)}
              onChange={(v) => updateConfigField('target_free_gb', v)}
              disabled={readOnly}
            />
            <div>
              <label htmlFor={fId('ds-sort-by')} className="block text-[11px] font-medium text-[#ccc] mb-1">
                Reclaim Priority Order
              </label>
              <select
                id={fId('ds-sort-by')}
                value={String(cfg.sort_by ?? 'oldest_watched')}
                disabled={readOnly}
                onChange={(e) => updateConfigField('sort_by', e.target.value)}
                className="arr-input !h-[30px] !py-0 !text-[12px]"
              >
                <option value="oldest_watched">Oldest Watched First</option>
                <option value="largest_first">Largest File Size First</option>
                <option value="lowest_rating">Lowest Rating First</option>
                <option value="largest_gb_per_play">Largest GB per Play</option>
              </select>
            </div>
          </>
        );

      case 'filter.exclusion_check':
        return (
          <>
            <InspectorCheckboxField
              id={fId('global-excl')}
              label="Respect global Cleanerr exclusion rules"
              checked={cfg.respect_global_exclusions !== false}
              onChange={(v) => updateConfigField('respect_global_exclusions', v)}
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('user-quotas')}
              label="Respect per-user exclusion quotas"
              checked={Boolean(cfg.respect_user_quotas)}
              onChange={(v) => updateConfigField('respect_user_quotas', v)}
              disabled={readOnly}
            />
            <InspectorTextField
              id={fId('excl-tags')}
              label="Protected Tags (comma-separated)"
              value={formatCommaList(cfg.excluded_tags)}
              onChange={(v) => updateConfigField('excluded_tags', parseCommaList(v))}
              disabled={readOnly}
            />
            <InspectorTextField
              id={fId('excl-users')}
              label="Protected Requesters / Users (comma-separated)"
              value={formatCommaList(cfg.excluded_users)}
              onChange={(v) => updateConfigField('excluded_users', parseCommaList(v))}
              placeholder="alice, bob"
              disabled={readOnly}
            />
          </>
        );

      case 'logic.rule_group':
      case 'filter.maintainerr_rules':
        return (
          <>
            <div>
              <label htmlFor={fId('rg-media-type')} className="block text-[11px] font-medium text-[#ccc] mb-1">
                Target Media Type
              </label>
              <select
                id={fId('rg-media-type')}
                value={String(cfg.targetMediaType ?? 'movie')}
                disabled={readOnly}
                onChange={(e) => updateConfigField('targetMediaType', e.target.value)}
                className="arr-input !h-[30px] !py-0 !text-[12px]"
              >
                <option value="movie">Movies</option>
                <option value="show">TV Series (Full)</option>
              </select>
            </div>
            <InspectorCheckboxField
              id={fId('rg-use-rules')}
              label="Evaluate Boolean Rule Sections"
              checked={cfg.useRules !== false}
              onChange={(v) => updateConfigField('useRules', v)}
              disabled={readOnly}
            />
            <InspectorNumberField
              id={fId('rg-max-reclaim')}
              label="Max Reclaim Cap (GB, Optional)"
              value={Number(cfg.maxReclaimGb ?? 0)}
              onChange={(v) => updateConfigField('maxReclaimGb', v || null)}
              disabled={readOnly}
            />
            <p className="text-[11px] text-[#909293]">
              Sections configured: {Array.isArray(cfg.sections) ? cfg.sections.length : 0}. Edit rule conditions in the Maintainerr Rule Builder tab or Advanced JSON Config below.
            </p>
          </>
        );

      case 'logic.and':
      case 'logic.or':
      case 'logic.not':
        return (
          <p className="text-[11px] text-[#909293]">
            Boolean set combinator ({nodeType.replace('logic.', '').toUpperCase()}). Connect two or more upstream nodes into this node.
          </p>
        );

      case 'logic.combinator':
        return (
          <div>
            <label htmlFor={fId('logic-op')} className="block text-[11px] font-medium text-[#ccc] mb-1">
              Boolean Operator
            </label>
            <select
              id={fId('logic-op')}
              value={String(cfg.operator ?? 'AND')}
              disabled={readOnly}
              onChange={(e) => updateConfigField('operator', e.target.value)}
              className="arr-input !h-[30px] !py-0 !text-[12px] font-mono"
            >
              <option value="AND">AND (Intersection)</option>
              <option value="OR">OR (Union)</option>
              <option value="NOT">NOT (Invert / Subtract)</option>
            </select>
          </div>
        );

      case 'limit.reclaim_budget':
      case 'filter.limit':
        return (
          <>
            {nodeType === 'filter.limit' && (
              <InspectorNumberField
                id={fId('lim-max-items')}
                label="Max Items Per Run"
                value={Number(cfg.max_items ?? 50)}
                onChange={(v) => updateConfigField('max_items', v)}
                disabled={readOnly}
              />
            )}
            <InspectorNumberField
              id={fId('max-reclaim')}
              label="Max Reclaim Per Run (GB)"
              value={Number(cfg.max_reclaim_gb ?? 250)}
              onChange={(v) => updateConfigField('max_reclaim_gb', v)}
              disabled={readOnly}
            />
            <InspectorNumberField
              id={fId('target-watermark')}
              label="Target Free Space Watermark (GB)"
              value={Number(cfg.target_free_space_gb_watermark ?? 0)}
              onChange={(v) => updateConfigField('target_free_space_gb_watermark', v)}
              disabled={readOnly}
            />
            <InspectorNumberField
              id={fId('min-trigger')}
              label="Only Run Below Free Space (GB)"
              value={Number(cfg.min_free_space_gb_trigger ?? 0)}
              onChange={(v) => updateConfigField('min_free_space_gb_trigger', v)}
              disabled={readOnly}
            />
            <div>
              <label htmlFor={fId('lim-sort-by')} className="block text-[11px] font-medium text-[#ccc] mb-1">
                Priority Sort Order
              </label>
              <select
                id={fId('lim-sort-by')}
                value={String(cfg.sort_by ?? 'oldest_watched')}
                disabled={readOnly}
                onChange={(e) => updateConfigField('sort_by', e.target.value)}
                className="arr-input !h-[30px] !py-0 !text-[12px]"
              >
                <option value="oldest_watched">Oldest Watched First</option>
                <option value="largest_first">Largest File Size First</option>
                <option value="lowest_rating">Lowest Rating First</option>
                <option value="largest_gb_per_play">Largest GB per Play</option>
              </select>
            </div>
          </>
        );

      case 'notify.discord':
        return (
          <>
            <InspectorNumberField
              id={fId('nd-grace-days')}
              label="Displayed Grace Period (Days)"
              value={Number(cfg.grace_days ?? 7)}
              max={365}
              onChange={(v) => updateConfigField('grace_days', Math.max(0, v))}
              disabled={readOnly}
            />
            <InspectorTextField
              id={fId('nd-webhook')}
              label="Discord Webhook Override (Optional)"
              value={String(cfg.discord_webhook_url ?? cfg.webhook_url ?? '')}
              onChange={(v) => updateConfigField('discord_webhook_url', v)}
              placeholder="Uses global Settings webhook if blank"
              mono
              disabled={readOnly}
            />
          </>
        );

      case 'notify.webhook':
        return (
          <InspectorTextField
            id={fId('nw-url')}
            label="Outbound Webhook URL"
            value={String(cfg.url ?? '')}
            onChange={(v) => updateConfigField('url', v)}
            placeholder="https://example.com/webhook"
            mono
            disabled={readOnly}
          />
        );

      case 'queue.grace_period':
        return (
          <>
            <InspectorNumberField
              id={fId('grace-days')}
              label="Grace Period Duration (Days)"
              value={Number(cfg.grace_days ?? 7)}
              max={365}
              onChange={(v) => updateConfigField('grace_days', Math.max(0, v))}
              disabled={readOnly}
            />
            <InspectorTextField
              id={fId('grace-discord')}
              label="Discord Webhook Override (Optional)"
              value={String(cfg.discord_webhook_url ?? '')}
              onChange={(v) => updateConfigField('discord_webhook_url', v)}
              placeholder="Uses global Settings webhook if blank"
              mono
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('auto-rescue')}
              label="Auto-Rescue if watched in Plex during Grace Period"
              checked={cfg.auto_rescue_on_watch !== false}
              onChange={(v) => updateConfigField('auto_rescue_on_watch', v)}
              disabled={readOnly}
            />
          </>
        );

      case 'action.managerr':
      case 'action.radarr':
      case 'action.sonarr':
        return (
          <>
            <div>
              <label htmlFor={fId('action-mode')} className="block text-[11px] font-medium text-[#ccc] mb-1">
                Managerr Cleanup Mode
              </label>
              <select
                id={fId('action-mode')}
                value={String(cfg.mode ?? 'delete_files_and_unmonitor')}
                disabled={readOnly}
                onChange={(e) => updateConfigField('mode', e.target.value)}
                className="arr-input !h-[30px] !py-0 !text-[12px]"
              >
                <option value="delete_files_and_unmonitor">Delete Files &amp; Unmonitor</option>
                <option value="unmonitor_and_delete_existing">Unmonitor &amp; Delete Existing</option>
                <option value="delete_files_only">Delete Files Only (Keep Monitored)</option>
                <option value="delete_work_and_files">Delete Work &amp; Files</option>
                <option value="delete_show_if_empty">Delete Show If Empty</option>
                <option value="unmonitor_show_if_empty">Unmonitor Show If Empty</option>
                <option value="downgrade_profile">Downgrade Quality Profile</option>
                <option value="unmonitor_only">Unmonitor Only</option>
                <option value="add_import_exclusion">Add Import Exclusion</option>
              </select>
            </div>
            {(String(cfg.mode ?? '') === 'downgrade_profile' || profiles.length > 0) && (
              <div>
                <label htmlFor={fId('target-profile')} className="block text-[11px] font-medium text-[#ccc] mb-1">
                  Target Downgrade Profile
                </label>
                {profiles.length > 0 ? (
                  <select
                    id={fId('target-profile')}
                    value={cfg.target_profile_id ? String(cfg.target_profile_id) : ''}
                    disabled={readOnly}
                    onChange={(e) =>
                      updateConfigField(
                        'target_profile_id',
                        e.target.value ? Number(e.target.value) : null,
                      )
                    }
                    className="arr-input !h-[30px] !py-0 !text-[12px]"
                  >
                    <option value="">None / Keep Current</option>
                    {profiles.map((p) => (
                      <option key={p.id} value={p.id}>
                        {p.name} ({p.qualityCutoff})
                      </option>
                    ))}
                  </select>
                ) : (
                  <input
                    id={fId('target-profile')}
                    type="number"
                    min={1}
                    value={cfg.target_profile_id ? String(cfg.target_profile_id) : ''}
                    disabled={readOnly}
                    placeholder="Profile ID"
                    onChange={(e) =>
                      updateConfigField(
                        'target_profile_id',
                        e.target.value ? Number(e.target.value) : null,
                      )
                    }
                    className="arr-input !h-[30px] !text-[12px] font-mono"
                  />
                )}
              </div>
            )}
            <InspectorCheckboxField
              id={fId('act-unmonitor')}
              label="Unmonitor item in Managerr"
              checked={cfg.unmonitor !== false}
              onChange={(v) => updateConfigField('unmonitor', v)}
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('act-clean-dirs')}
              label="Clean empty folders on disk"
              checked={Boolean(cfg.clean_empty_folders)}
              onChange={(v) => updateConfigField('clean_empty_folders', v)}
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('act-reset-req')}
              label="Reset Seerr / Overseerr requests"
              checked={Boolean(cfg.reset_requests)}
              onChange={(v) => updateConfigField('reset_requests', v)}
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('act-prune-torrent')}
              label="Prune download client torrents &amp; files"
              checked={Boolean(cfg.prune_torrent_client)}
              onChange={(v) => updateConfigField('prune_torrent_client', v)}
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('act-add-import-excl')}
              label="Also add to import list exclusions"
              checked={Boolean(cfg.add_import_exclusion)}
              onChange={(v) => updateConfigField('add_import_exclusion', v)}
              disabled={readOnly}
            />
          </>
        );

      case 'action.seerr':
      case 'action.seerr_reset':
        return (
          <>
            <div>
              <label htmlFor={fId('seerr-mode')} className="block text-[11px] font-medium text-[#ccc] mb-1">
                Seerr Action Mode
              </label>
              <select
                id={fId('seerr-mode')}
                value={String(cfg.mode ?? 'reset_media_status')}
                disabled={readOnly}
                onChange={(e) => updateConfigField('mode', e.target.value)}
                className="arr-input !h-[30px] !py-0 !text-[12px]"
              >
                <option value="reset_media_status">Reset Media Status (Allow Re-Request)</option>
                <option value="delete_request">Delete Associated Seerr Request</option>
              </select>
            </div>
            <InspectorCheckboxField
              id={fId('seerr-reset-ext')}
              label="Also reset external Overseerr / Jellyseerr request"
              checked={cfg.reset_external_seerr !== false}
              onChange={(v) => updateConfigField('reset_external_seerr', v)}
              disabled={readOnly}
            />
          </>
        );

      case 'action.add_import_exclusion':
        return (
          <p className="text-[11px] text-[#909293]">
            Adds matched movies or series to Managerr&apos;s import list exclusions so automated import lists will not re-add them.
          </p>
        );

      case 'action.delete_files':
        return (
          <>
            <InspectorCheckboxField
              id={fId('df-clean-dirs')}
              label="Clean empty folders &amp; sidecars on disk"
              checked={cfg.clean_empty_folders !== false}
              onChange={(v) => updateConfigField('clean_empty_folders', v)}
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('df-unmonitor')}
              label="Unmonitor item after deleting files"
              checked={Boolean(cfg.unmonitor)}
              onChange={(v) => updateConfigField('unmonitor', v)}
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('df-skip-hl')}
              label="Skip hardlinked seeding files"
              checked={Boolean(cfg.skip_hardlinked_seeding)}
              onChange={(v) => updateConfigField('skip_hardlinked_seeding', v)}
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('df-prune-torrent')}
              label="Prune download client torrents &amp; files"
              checked={Boolean(cfg.prune_torrent_client)}
              onChange={(v) => updateConfigField('prune_torrent_client', v)}
              disabled={readOnly}
            />
          </>
        );

      case 'action.plex':
        return (
          <div>
            <label htmlFor={fId('plex-mode')} className="block text-[11px] font-medium text-[#ccc] mb-1">
              Plex Maintenance Action
            </label>
            <select
              id={fId('plex-mode')}
              value={String(cfg.mode ?? 'refresh_library_and_empty_trash')}
              disabled={readOnly}
              onChange={(e) => updateConfigField('mode', e.target.value)}
              className="arr-input !h-[30px] !py-0 !text-[12px]"
            >
              <option value="refresh_library_and_empty_trash">Refresh Library &amp; Empty Trash</option>
              <option value="refresh_library">Refresh Library Only</option>
              <option value="empty_trash">Empty Trash Only</option>
              <option value="remove_media">Remove Media Directly</option>
            </select>
          </div>
        );

      case 'action.webhook':
        return (
          <>
            <InspectorTextField
              id={fId('wh-url')}
              label="Webhook URL"
              value={String(cfg.url ?? '')}
              onChange={(v) => updateConfigField('url', v)}
              placeholder="https://discord.com/api/webhooks/..."
              mono
              disabled={readOnly}
            />
            <InspectorCheckboxField
              id={fId('wh-items')}
              label="Include matched items payload"
              checked={cfg.include_items !== false}
              onChange={(v) => updateConfigField('include_items', v)}
              disabled={readOnly}
            />
          </>
        );

      default:
        return (
          <p className="text-[11px] text-[#888888]">
            Use the Advanced JSON Config section below to edit parameters for <code>{nodeType}</code>.
          </p>
        );
    }
  };

  return (
    <div className="space-y-3">
      {/* Categorized Studio Toolbar */}
      <div
        className="flex flex-wrap items-center justify-between gap-2 px-3 py-2 rounded-[4px] border"
        style={{ backgroundColor: 'var(--arr-toolbar-bg)', borderColor: 'var(--arr-card-border)' }}
      >
        <div className="flex flex-wrap items-center gap-2">
          {!readOnly && (
            <ArrButton
              variant="primary"
              className="!h-[30px] !px-3 !text-[12px]"
              onClick={() => setPaletteOpen(true)}
            >
              <Plus className="h-3.5 w-3.5" aria-hidden="true" />
              Add Node
            </ArrButton>
          )}
          <ArrButton
            variant="default"
            className="!h-[30px] !px-3 !text-[12px]"
            onClick={handleAutoLayout}
            disabled={readOnly || (graphJson.nodes ?? []).length === 0}
          >
            <LayoutGrid className="h-3.5 w-3.5" aria-hidden="true" />
            Auto-Layout
          </ArrButton>
        </div>

        <div className="flex items-center gap-3 text-[11px] font-mono text-[#909293]">
          <span>
            Nodes: <strong className="text-[#e1e2e3]">{rfNodes.length}</strong>
          </span>
          <span>
            Edges: <strong className="text-[#e1e2e3]">{rfEdges.length}</strong>
          </span>
        </div>
      </div>

      {/* Main Canvas & Structured Inspector */}
      <div className="grid grid-cols-1 lg:grid-cols-12 gap-4">
        <div
          className="lg:col-span-8 xl:col-span-9 h-[520px] rounded-[4px] border overflow-hidden relative"
          style={{ backgroundColor: 'var(--arr-page-bg)', borderColor: 'var(--arr-card-border)' }}
        >
          <ReactFlow
            nodes={rfNodes}
            edges={rfEdges}
            nodeTypes={nodeTypes}
            onNodesChange={onNodesChange}
            onEdgesChange={onEdgesChange}
            onConnect={onConnect}
            onNodeClick={(_, node) => setSelectedNodeId(node.id)}
            onPaneClick={() => setSelectedNodeId(null)}
            nodesDraggable={!readOnly}
            nodesConnectable={!readOnly}
            elementsSelectable
            fitView
            fitViewOptions={{ padding: 0.2 }}
            proOptions={{ hideAttribution: true }}
          >
            <Background variant={BackgroundVariant.Dots} gap={20} size={1.2} color="#334242" />
            <Controls />
            <MiniMap
              nodeColor={(n) => {
                const nt = String((n.data as Record<string, unknown> | undefined)?.node_type ?? '');
                return getCleanerrNodeVisuals(nt).accentHex;
              }}
              maskColor="rgba(16, 21, 21, 0.72)"
            />
          </ReactFlow>
        </div>

        {/* Structured Dynamic Node Inspector */}
        <aside
          aria-label="Node Inspector"
          className="lg:col-span-4 xl:col-span-3 rounded-[4px] border p-3.5 flex flex-col justify-between max-h-[520px] overflow-y-auto"
          style={{ backgroundColor: 'var(--arr-card-bg)', borderColor: 'var(--arr-card-border)' }}
        >
          <div>
            <div
              className="flex items-center justify-between pb-2 mb-3 border-b"
              style={{ borderBottomColor: 'var(--arr-card-border)' }}
            >
              <span className="text-[13px] font-bold text-[#e1e2e3] flex items-center gap-1.5">
                <Sliders className="h-4 w-4 text-arr-accent" aria-hidden="true" />
                Node Inspector
              </span>
              {selectedNodeId && !readOnly && (
                <button
                  type="button"
                  onClick={deleteSelectedNode}
                  aria-label="Delete selected node"
                  className="inline-flex items-center gap-1 px-2 py-0.5 rounded-[3px] text-[11px] bg-[#f05050]/15 text-[#f05050] border border-[#f05050]/35 hover:bg-[#f05050]/25 cursor-pointer"
                >
                  <Trash2 className="h-3 w-3" aria-hidden="true" />
                  Remove
                </button>
              )}
            </div>

            {!selectedRawData || !selectedNodeId ? (
              <div className="py-10 px-3 text-center space-y-2">
                <Layers className="h-8 w-8 mx-auto text-[#666e75]" aria-hidden="true" />
                <p className="text-[12px] font-medium text-[#cccccc]">
                  Select a node on the canvas to inspect and edit its parameters.
                </p>
                <p className="text-[11px] text-[#909293]">
                  Connect nodes left-to-right to define your trigger, source, filters, grace queue, and cleanup actions.
                </p>
              </div>
            ) : (
              <div className="space-y-3 text-xs">
                <div className="text-[11px] font-mono text-[#909293]">
                  ID: <span className="text-[#e1e2e3]">{selectedNodeId}</span>
                </div>

                <InspectorTextField
                  id={`${baseId}-node-label`}
                  label="Step Label"
                  value={selectedRawData.label}
                  onChange={(v) =>
                    updateSelectedNode((curr) => ({ ...curr, label: v }))
                  }
                  disabled={readOnly}
                />

                <div>
                  <label
                    htmlFor={`${baseId}-node-type`}
                    className="block text-[11px] font-medium text-[#ccc] mb-1"
                  >
                    Node Type
                  </label>
                  <select
                    id={`${baseId}-node-type`}
                    value={selectedRawData.node_type}
                    disabled={readOnly}
                    onChange={(e) => {
                      const nextType = e.target.value;
                      const match = catalog.find((c) => c.nodeType === nextType);
                      updateSelectedNode((curr) => ({
                        label: match?.label ?? curr.label,
                        node_type: nextType,
                        config: { ...(match?.defaultConfig ?? {}) },
                      }));
                    }}
                    className="arr-input !h-[30px] !py-0 !text-[12px] font-mono"
                  >
                    {!catalog.some((c) => c.nodeType === selectedRawData.node_type) && (
                      <option value={selectedRawData.node_type}>
                        {selectedRawData.node_type}
                      </option>
                    )}
                    {catalog.map((entry) => (
                      <option key={entry.nodeType} value={entry.nodeType}>
                        {entry.nodeType} — {entry.label}
                      </option>
                    ))}
                  </select>
                </div>

                <div
                  className="pt-2.5 border-t space-y-2.5"
                  style={{ borderTopColor: 'var(--arr-card-border)' }}
                >
                  {renderStructuredConfigFields(selectedRawData.node_type, selectedRawData.config)}
                </div>

                {/* Simulation Preview for Selected Node */}
                {selectedMetric && (
                  <div
                    className="pt-3 border-t space-y-2"
                    style={{ borderTopColor: 'var(--arr-card-border)' }}
                  >
                    <div className="flex items-center justify-between">
                      <span className="text-[11px] font-semibold text-[#27c24c] flex items-center gap-1">
                        <CheckCircle2 className="h-3.5 w-3.5" aria-hidden="true" />
                        Matched: {selectedMetric.outputCount} / {selectedMetric.inputCount}
                      </span>
                      <span className="text-[11px] font-mono text-[#ffc230]">
                        {formatReclaimableGb(selectedMetric.sizeBytes)}
                      </span>
                    </div>

                    {onBulkExcludeIds &&
                      ((selectedMetric.matchedItemIds?.length ?? 0) > 0 ||
                        (selectedMetric.matchedItemsPreview?.length ?? 0) > 0) && (
                        <ArrButton
                          variant="warning"
                          busy={excludingIds}
                          className="!h-[28px] !w-full !text-[11px]"
                          onClick={() => {
                            const ids =
                              selectedMetric.matchedItemIds && selectedMetric.matchedItemIds.length > 0
                                ? selectedMetric.matchedItemIds
                                : (selectedMetric.matchedItemsPreview ?? []).map((item) => item.id);
                            onBulkExcludeIds(ids);
                          }}
                        >
                          <ShieldBan className="h-3.5 w-3.5" aria-hidden="true" />
                          Exclude Matched Items ({selectedMetric.outputCount})
                        </ArrButton>
                      )}

                    {selectedMetric.matchedItemsPreview && selectedMetric.matchedItemsPreview.length > 0 && (
                      <div className="space-y-1 max-h-40 overflow-y-auto pr-0.5">
                        {selectedMetric.matchedItemsPreview.map((item) => (
                          <div
                            key={item.id}
                            className="p-1.5 rounded-[3px] border flex items-center justify-between gap-2 text-[11px]"
                            style={{
                              backgroundColor: 'var(--arr-input-bg)',
                              borderColor: 'var(--arr-card-border)',
                            }}
                          >
                            <div className="truncate">
                              <span className="font-medium text-[#e1e2e3]">{item.title}</span>
                              <span className="ml-1.5 text-[10px] font-mono uppercase text-[#909293]">
                                [{item.mediaType}]
                              </span>
                            </div>
                            <span className="font-mono text-[10px] text-[#ccc] shrink-0">
                              {formatReclaimableGb(item.sizeBytes)}
                            </span>
                          </div>
                        ))}
                      </div>
                    )}
                  </div>
                )}

                {/* Collapsible Advanced JSON Config */}
                <div
                  className="pt-2.5 border-t"
                  style={{ borderTopColor: 'var(--arr-card-border)' }}
                >
                  <button
                    type="button"
                    aria-expanded={advancedJsonOpen}
                    aria-controls={`${baseId}-raw-json-panel`}
                    onClick={() => setAdvancedJsonOpen((prev) => !prev)}
                    className="w-full flex items-center justify-between text-[11px] font-semibold text-[#909293] hover:text-[#e1e2e3] py-1 cursor-pointer"
                  >
                    <span>Advanced JSON Config</span>
                    {advancedJsonOpen ? (
                      <ChevronDown className="h-3.5 w-3.5" aria-hidden="true" />
                    ) : (
                      <ChevronRight className="h-3.5 w-3.5" aria-hidden="true" />
                    )}
                  </button>

                  {advancedJsonOpen && (
                    <div id={`${baseId}-raw-json-panel`} className="mt-2 space-y-2">
                      {inspectorError && (
                        <ArrAlert kind="danger" className="!py-1.5 !px-2 !text-[11px]">
                          {inspectorError}
                        </ArrAlert>
                      )}
                      <label htmlFor={`${baseId}-raw-json-textarea`} className="visually-hidden">
                        Raw Node Data JSON
                      </label>
                      <textarea
                        id={`${baseId}-raw-json-textarea`}
                        rows={8}
                        value={inspectorJson}
                        disabled={readOnly}
                        onChange={(e) => setInspectorJson(e.target.value)}
                        className="arr-input !font-mono !text-[11px] !p-2"
                      />
                      {!readOnly && (
                        <ArrButton
                          variant="default"
                          className="!h-[28px] !w-full !text-[11px]"
                          onClick={applyRawJsonConfig}
                        >
                          Apply JSON Config
                        </ArrButton>
                      )}
                    </div>
                  )}
                </div>
              </div>
            )}
          </div>
        </aside>
      </div>

      {/* Categorized Add Node Palette Modal */}
      {paletteOpen && (
        <ArrModal
          title="Add Cleanerr Workflow Node"
          onClose={() => setPaletteOpen(false)}
          maxWidth="max-w-3xl"
        >
          <div className="space-y-4">
            <div className="flex flex-wrap gap-1.5" role="group" aria-label="Node category filter">
              {CATEGORY_ORDER.map((cat) => {
                const active = paletteCategory === cat.key;
                return (
                  <button
                    key={cat.key}
                    type="button"
                    aria-pressed={active}
                    onClick={() => setPaletteCategory(cat.key)}
                    className={`px-2.5 py-1 rounded-[3px] text-[11px] font-semibold border cursor-pointer transition-colors ${
                      active
                        ? 'bg-arr-primary border-arr-primary-border text-white'
                        : 'bg-arr-input border-arr-card-border text-[#ccc] hover:text-white'
                    }`}
                  >
                    {cat.label}
                  </button>
                );
              })}
            </div>

            <div className="grid grid-cols-1 sm:grid-cols-2 gap-2.5">
              {filteredPalette.map((entry) => {
                const visuals = getCleanerrNodeVisuals(entry.nodeType);
                const Icon = visuals.icon;
                return (
                  <button
                    key={entry.nodeType}
                    type="button"
                    onClick={() => addNodeFromCatalog(entry)}
                    className="flex flex-col text-left p-3 rounded-[4px] border transition-colors cursor-pointer hover:border-arr-accent"
                    style={{
                      backgroundColor: 'var(--arr-card-bg)',
                      borderColor: 'var(--arr-card-border)',
                      borderLeftWidth: '3px',
                      borderLeftColor: entry.color || visuals.accentHex,
                    }}
                  >
                    <div className="flex items-center justify-between gap-2 w-full">
                      <span className="inline-flex items-center gap-1.5 text-xs font-semibold text-[#e1e2e3]">
                        <Icon
                          className="h-3.5 w-3.5 shrink-0"
                          style={{ color: entry.color || visuals.accentHex }}
                          aria-hidden="true"
                        />
                        {entry.label}
                      </span>
                      <span className="px-1.5 py-0.5 rounded-[2px] text-[10px] font-mono uppercase bg-arr-input border border-arr-card-border text-[#ccc]">
                        {entry.category}
                      </span>
                    </div>
                    <p className="text-[11px] text-[#909293] mt-1">{entry.description}</p>
                    <span className="text-[10px] font-mono text-arr-accent mt-1.5">
                      {entry.nodeType}
                    </span>
                  </button>
                );
              })}
            </div>
          </div>
        </ArrModal>
      )}
    </div>
  );
}
