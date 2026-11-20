import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import { Handle, Position, type NodeProps } from '@xyflow/react';
import {
  Clock,
  Database,
  Filter,
  Film,
  Gauge,
  GitBranch,
  HardDrive,
  Pencil,
  ShieldCheck,
  Sparkles,
  Trash2,
  Tv,
  Webhook,
  Zap,
  type LucideIcon,
} from 'lucide-react';
import type { CleanerrNodeMetric } from './cleanerrTypes';

export interface CleanerrWorkflowNodeData extends Record<string, unknown> {
  label: string;
  node_type: string;
  config?: Record<string, unknown>;
  metric?: CleanerrNodeMetric;
  onLabelChange?: (nodeId: string, nextLabel: string) => void;
}

export interface CleanerrNodeVisuals {
  categoryLabel: string;
  accentHex: string;
  icon: LucideIcon;
  hasTarget: boolean;
  hasSource: boolean;
}

export function formatReclaimableGb(bytes: number): string {
  const gb = bytes / (1024 * 1024 * 1024);
  if (gb >= 100) return `${gb.toFixed(1)} GB`;
  if (gb >= 0.01) return `${gb.toFixed(2)} GB`;
  if (bytes > 0) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return '0.00 GB';
}

export function getCleanerrNodeVisuals(nodeType: string): CleanerrNodeVisuals {
  if (nodeType.startsWith('trigger.')) {
    const icon =
      nodeType === 'trigger.webhook'
        ? Webhook
        : nodeType === 'trigger.storage_pressure'
          ? HardDrive
          : Zap;
    return {
      categoryLabel: 'TRIGGER',
      accentHex: '#ffc230',
      icon,
      hasTarget: false,
      hasSource: true,
    };
  }
  if (nodeType.startsWith('source.')) {
    return {
      categoryLabel: 'SOURCE',
      accentHex: '#35c5f4',
      icon: Database,
      hasTarget: true,
      hasSource: true,
    };
  }
  if (nodeType === 'filter.exclusion_check') {
    return {
      categoryLabel: 'PROTECTION',
      accentHex: '#27c24c',
      icon: ShieldCheck,
      hasTarget: true,
      hasSource: true,
    };
  }
  if (nodeType.startsWith('filter.')) {
    return {
      categoryLabel: 'FILTER',
      accentHex: '#a855f7',
      icon: Filter,
      hasTarget: true,
      hasSource: true,
    };
  }
  if (nodeType.startsWith('logic.')) {
    return {
      categoryLabel: 'LOGIC',
      accentHex: '#5d9cec',
      icon: GitBranch,
      hasTarget: true,
      hasSource: true,
    };
  }
  if (nodeType.startsWith('limit.')) {
    return {
      categoryLabel: 'LIMIT',
      accentHex: '#ffa726',
      icon: Gauge,
      hasTarget: true,
      hasSource: true,
    };
  }
  if (nodeType.startsWith('queue.')) {
    return {
      categoryLabel: 'QUEUE',
      accentHex: '#f97316',
      icon: Clock,
      hasTarget: true,
      hasSource: true,
    };
  }

  let actionIcon: LucideIcon = Trash2;
  if (nodeType === 'action.radarr') actionIcon = Film;
  else if (nodeType === 'action.sonarr') actionIcon = Tv;
  else if (nodeType === 'action.seerr') actionIcon = Sparkles;
  else if (nodeType === 'action.webhook') actionIcon = Webhook;

  return {
    categoryLabel: 'ACTION',
    accentHex: '#f05050',
    icon: actionIcon,
    hasTarget: true,
    hasSource: true,
  };
}

function asString(value: unknown): string {
  return typeof value === 'string' ? value.trim() : '';
}

function asNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function summarizeConfig(nodeType: string, cfg: Record<string, unknown> = {}): string[] {
  const pills: string[] = [];

  if (nodeType === 'trigger.schedule') {
    pills.push(`cron: ${asString(cfg.cron) || '0 3 * * *'}`);
  } else if (nodeType === 'trigger.webhook') {
    pills.push(`src: ${asString(cfg.source) || 'plex'}`);
    const event = asString(cfg.event);
    if (event) pills.push(event);
  } else if (nodeType === 'trigger.manual') {
    pills.push(cfg.allow_api === false ? 'ui-only' : 'ui + api');
  } else if (nodeType === 'trigger.storage_pressure') {
    const minFree = asNumber(cfg.min_free_gb_trigger);
    const targetFree = asNumber(cfg.target_free_gb);
    if (minFree !== null) pills.push(`free < ${minFree} GB`);
    if (targetFree !== null) pills.push(`target: ${targetFree} GB`);
  } else if (nodeType === 'source.plex_library' || nodeType === 'source.managerr_catalog') {
    const rawMediaType = asString(cfg.media_type) || 'movie';
    const displayMediaType =
      rawMediaType === 'season' || rawMediaType === 'episode' ? 'show' : rawMediaType;
    pills.push(`type: ${displayMediaType}`);
    if (Array.isArray(cfg.library_ids) && cfg.library_ids.length > 0) {
      pills.push(`libs: ${cfg.library_ids.length}`);
    }
  } else if (nodeType === 'filter.watch_state') {
    const watchedDays = asNumber(cfg.last_watched_older_than_days);
    const unwatchedDays = asNumber(cfg.unwatched_added_older_than_days);
    const minViews = asNumber(cfg.view_count_gte);
    if (watchedDays !== null && watchedDays > 0) pills.push(`older > ${watchedDays}d`);
    if (unwatchedDays !== null && unwatchedDays > 0) pills.push(`unwatched > ${unwatchedDays}d`);
    if (minViews !== null && minViews > 0) pills.push(`views >= ${minViews}`);
    if (cfg.include_unwatched_if_added_older === true) pills.push('+unwatched');
  } else if (nodeType === 'filter.metadata') {
    const res = asString(cfg.resolution);
    const sizeGt = asNumber(cfg.size_gt_gb);
    const ratingLt = asNumber(cfg.rating_lt);
    if (res) pills.push(`res: ${res.toUpperCase()}`);
    if (sizeGt !== null && sizeGt > 0) pills.push(`> ${sizeGt} GB`);
    if (ratingLt !== null && ratingLt > 0) pills.push(`rating < ${ratingLt}`);
  } else if (nodeType === 'filter.arr_seerr') {
    const requester = asString(cfg.seerr_requested_by);
    const reqDays = asNumber(cfg.seerr_requested_older_than_days);
    if (requester) pills.push(`user: ${requester}`);
    if (reqDays !== null && reqDays > 0) pills.push(`req > ${reqDays}d`);
    if (Array.isArray(cfg.exclude_tags) && cfg.exclude_tags.length > 0) {
      pills.push(`!tag: ${cfg.exclude_tags.join(',')}`);
    }
    if (cfg.skip_hardlinked_seeding === true) pills.push('seed-safe');
  } else if (nodeType === 'filter.codec_density') {
    if (Array.isArray(cfg.codecs) && cfg.codecs.length > 0) {
      pills.push(`codecs: ${cfg.codecs.join(',')}`);
    }
    const minSize = asNumber(cfg.min_size_gb);
    if (minSize !== null && minSize > 0) pills.push(`> ${minSize} GB`);
    if (cfg.protect_dual_audio === true) pills.push('dual-audio safe');
    if (cfg.skip_hardlinked_seeding === true) pills.push('seed-safe');
  } else if (nodeType === 'filter.exclusion_check') {
    if (cfg.respect_global_exclusions !== false) pills.push('global exclusions');
    if (cfg.respect_user_quotas === true) pills.push('user quotas');
  } else if (nodeType === 'logic.combinator') {
    pills.push(`op: ${asString(cfg.operator) || 'AND'}`);
  } else if (nodeType === 'limit.reclaim_budget') {
    const maxReclaim = asNumber(cfg.max_reclaim_gb);
    const watermark = asNumber(cfg.target_free_space_gb_watermark);
    if (maxReclaim !== null && maxReclaim > 0) pills.push(`max: ${maxReclaim} GB`);
    if (watermark !== null && watermark > 0) pills.push(`free >= ${watermark} GB`);
  } else if (nodeType === 'queue.grace_period') {
    pills.push(`grace: ${asNumber(cfg.grace_days) ?? 7}d`);
    pills.push('Leaving Soon');
    if (cfg.auto_rescue_on_watch !== false) pills.push('auto-rescue');
  } else if (nodeType.startsWith('action.')) {
    const mode = asString(cfg.mode);
    if (mode) pills.push(`mode: ${mode.replace(/_/g, ' ')}`);
    const profileId = asNumber(cfg.target_profile_id);
    if (profileId !== null && profileId > 0) pills.push(`profile #${profileId}`);
    if (cfg.clean_empty_folders === true) pills.push('clean dirs');
    if (cfg.reset_requests === true) pills.push('reset req');
  }

  return pills.slice(0, 4);
}

export function CleanerrWorkflowNode({ id, data, selected }: NodeProps) {
  const nodeData = data as CleanerrWorkflowNodeData;
  const nodeType = asString(nodeData.node_type) || 'filter.watch_state';
  const label = asString(nodeData.label) || nodeType;
  const visuals = getCleanerrNodeVisuals(nodeType);
  const Icon = visuals.icon;
  const pills = summarizeConfig(nodeType, nodeData.config ?? {});
  const metric = nodeData.metric;
  const onLabelChange = nodeData.onLabelChange;

  const [editing, setEditing] = useState(false);
  const [draftLabel, setDraftLabel] = useState(label);
  const inputRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    setDraftLabel(label);
  }, [label]);

  useEffect(() => {
    if (editing) {
      inputRef.current?.focus();
      inputRef.current?.select();
    }
  }, [editing]);

  const commitLabel = () => {
    setEditing(false);
    const trimmed = draftLabel.trim();
    if (trimmed && trimmed !== label && onLabelChange) {
      onLabelChange(id, trimmed);
    } else {
      setDraftLabel(label);
    }
  };

  const handleKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      commitLabel();
    } else if (e.key === 'Escape') {
      setDraftLabel(label);
      setEditing(false);
    }
  };

  return (
    <div
      className="min-w-[235px] max-w-[280px] rounded-[4px] border shadow-lg transition-all"
      style={{
        backgroundColor: 'var(--arr-card-bg)',
        borderColor: selected ? 'var(--arr-accent)' : 'var(--arr-card-border)',
        borderTopWidth: '3px',
        borderTopColor: visuals.accentHex,
        color: 'var(--arr-text)',
        boxShadow: selected
          ? '0 0 0 2px color-mix(in oklab, var(--arr-accent) 45%, transparent), 0 8px 20px rgba(0,0,0,0.45)'
          : '0 6px 16px rgba(0,0,0,0.35)',
      }}
    >
      {visuals.hasTarget && (
        <Handle
          type="target"
          position={Position.Left}
          className="!w-3 !h-3 !rounded-[2px] !border-2"
          style={{
            backgroundColor: 'var(--arr-accent)',
            borderColor: 'var(--arr-card-bg)',
          }}
        />
      )}

      {/* Header */}
      <div
        className="flex items-center justify-between gap-2 px-3 py-1.5 border-b"
        style={{
          backgroundColor: 'var(--arr-toolbar-bg)',
          borderBottomColor: 'var(--arr-card-border)',
        }}
      >
        <div className="flex items-center gap-1.5 min-w-0">
          <Icon className="w-3.5 h-3.5 shrink-0" style={{ color: visuals.accentHex }} aria-hidden="true" />
          <span
            className="text-[10px] font-bold tracking-wider uppercase truncate"
            style={{ color: visuals.accentHex }}
          >
            {visuals.categoryLabel}
          </span>
        </div>
        <span
          className="text-[10px] font-mono truncate max-w-[125px]"
          style={{ color: 'var(--arr-muted)' }}
          title={nodeType}
        >
          {nodeType}
        </span>
      </div>

      {/* Body */}
      <div className="p-3 space-y-2">
        <div className="flex items-start justify-between gap-1.5">
          {editing && onLabelChange ? (
            <input
              ref={inputRef}
              type="text"
              aria-label="Node label"
              value={draftLabel}
              onChange={(e) => setDraftLabel(e.target.value)}
              onBlur={commitLabel}
              onKeyDown={handleKeyDown}
              className="nodrag arr-input !h-[24px] !py-0 !px-1.5 !text-xs font-semibold"
            />
          ) : (
            <div
              onDoubleClick={() => {
                if (onLabelChange) setEditing(true);
              }}
              className="text-xs font-semibold leading-snug break-words flex-1"
              style={{ color: 'var(--arr-text)' }}
              title={onLabelChange ? 'Double-click to rename' : undefined}
            >
              {label}
            </div>
          )}
          {onLabelChange && !editing && (
            <button
              type="button"
              onClick={() => setEditing(true)}
              aria-label={`Rename ${label}`}
              className="nodrag p-0.5 rounded opacity-60 hover:opacity-100 transition-opacity cursor-pointer shrink-0"
              style={{ color: 'var(--arr-muted)' }}
            >
              <Pencil className="w-3 h-3" aria-hidden="true" />
            </button>
          )}
        </div>

        {pills.length > 0 && (
          <div className="flex flex-wrap gap-1">
            {pills.map((pill, idx) => (
              <span
                key={`${pill}-${idx}`}
                className="px-1.5 py-0.5 text-[10px] font-mono rounded-[2px] border truncate max-w-[230px]"
                style={{
                  backgroundColor: 'var(--arr-input-bg)',
                  borderColor: 'var(--arr-card-border)',
                  color: 'var(--arr-text-dim)',
                }}
              >
                {pill}
              </span>
            ))}
          </div>
        )}

        {metric && (
          <div
            className="mt-2 pt-2 border-t flex items-center justify-between gap-2 text-[11px]"
            style={{ borderTopColor: 'var(--arr-card-border)' }}
          >
            <div className="flex items-center gap-1 font-mono">
              <span style={{ color: 'var(--arr-muted)' }}>IN {metric.inputCount}</span>
              <span style={{ color: 'var(--arr-muted)' }} aria-hidden="true">→</span>
              <span
                className="px-1.5 py-0.5 rounded-[2px] font-semibold border"
                style={
                  metric.outputCount > 0
                    ? {
                        backgroundColor: 'color-mix(in oklab, var(--arr-success) 16%, transparent)',
                        color: 'var(--arr-success)',
                        borderColor: 'color-mix(in oklab, var(--arr-success) 40%, transparent)',
                      }
                    : {
                        backgroundColor: 'var(--arr-input-bg)',
                        color: 'var(--arr-muted)',
                        borderColor: 'var(--arr-card-border)',
                      }
                }
              >
                OUT {metric.outputCount}
              </span>
            </div>
            <span className="text-[10px] font-mono font-semibold" style={{ color: 'var(--arr-gold)' }}>
              {formatReclaimableGb(metric.sizeBytes)}
            </span>
          </div>
        )}
      </div>

      {visuals.hasSource && (
        <Handle
          type="source"
          position={Position.Right}
          className="!w-3 !h-3 !rounded-[2px] !border-2"
          style={{
            backgroundColor: visuals.accentHex,
            borderColor: 'var(--arr-card-bg)',
          }}
        />
      )}
    </div>
  );
}
