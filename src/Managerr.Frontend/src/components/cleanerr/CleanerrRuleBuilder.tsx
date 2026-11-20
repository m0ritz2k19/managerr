import { useId, useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { ArrowRightLeft, Plus, SlidersHorizontal, Trash2 } from 'lucide-react';
import { apiFetch } from '../../lib/api';
import { ArrButton } from '../arr/ArrPrimitives';
import type { QualityProfileSummary } from '../catalog/catalogTypesAndHelpers';
import type {
  CleanerrComparatorDescriptor,
  CleanerrRuleCatalogResponse,
  CleanerrRuleConditionDto,
  CleanerrRuleFieldDescriptor,
  CleanerrRuleGroupSpec,
  CleanerrRuleSectionDto,
  LibraryOption,
} from './cleanerrTypes';

export interface CleanerrRuleBuilderProps {
  ruleGroup: CleanerrRuleGroupSpec;
  onChange: (spec: CleanerrRuleGroupSpec) => void;
  catalog?: CleanerrRuleCatalogResponse;
  libraries?: LibraryOption[];
  profiles?: QualityProfileSummary[];
}

const DEFAULT_COMPARATORS: CleanerrComparatorDescriptor[] = [
  { key: 'equals', label: 'Equals (=)', supportedValueTypes: ['number', 'text', 'boolean', 'date', 'list'], requiresValue: true },
  { key: 'not_equals', label: 'Not Equals (!=)', supportedValueTypes: ['number', 'text', 'boolean', 'date', 'list'], requiresValue: true },
  { key: 'greater_than', label: 'Greater Than (>)', supportedValueTypes: ['number', 'date'], requiresValue: true },
  { key: 'greater_than_or_equal', label: 'Greater Than or Equal (>=)', supportedValueTypes: ['number', 'date'], requiresValue: true },
  { key: 'less_than', label: 'Less Than (<)', supportedValueTypes: ['number', 'date'], requiresValue: true },
  { key: 'less_than_or_equal', label: 'Less Than or Equal (<=)', supportedValueTypes: ['number', 'date'], requiresValue: true },
  { key: 'contains', label: 'Contains', supportedValueTypes: ['text', 'list'], requiresValue: true },
  { key: 'not_contains', label: 'Does Not Contain', supportedValueTypes: ['text', 'list'], requiresValue: true },
  { key: 'is_true', label: 'Is True', supportedValueTypes: ['boolean'], requiresValue: false },
  { key: 'is_false', label: 'Is False', supportedValueTypes: ['boolean'], requiresValue: false },
  { key: 'is_empty', label: 'Is Empty / Unset', supportedValueTypes: ['text', 'list'], requiresValue: false },
  { key: 'is_not_empty', label: 'Is Not Empty', supportedValueTypes: ['text', 'list'], requiresValue: false },
];

const DEFAULT_FIELDS: CleanerrRuleFieldDescriptor[] = [
  {
    key: 'plex.lastViewedDaysAgo',
    category: 'Plex Watch History',
    label: 'Days Since Last Viewed',
    valueType: 'number',
    supportedMediaTypes: ['movie', 'show'],
    description: 'Days since anyone watched this item',
    suggestedValues: ['30', '60', '90', '180', '365'],
  },
  {
    key: 'plex.viewCount',
    category: 'Plex Watch History',
    label: 'Total View Count',
    valueType: 'number',
    supportedMediaTypes: ['movie', 'show'],
    suggestedValues: ['0', '1', '2', '5'],
  },
  {
    key: 'plex.inProgress',
    category: 'Plex Watch History',
    label: 'Currently In-Progress (viewOffset > 0)',
    valueType: 'boolean',
    supportedMediaTypes: ['movie', 'show'],
  },
  {
    key: 'plex.addedDaysAgo',
    category: 'Plex Metadata',
    label: 'Days Since Added to Plex',
    valueType: 'number',
    supportedMediaTypes: ['movie', 'show'],
    suggestedValues: ['14', '30', '60', '90', '180'],
  },
  {
    key: 'plex.userRating',
    category: 'Plex Metadata',
    label: 'Plex / Critic Rating',
    valueType: 'number',
    supportedMediaTypes: ['movie', 'show'],
    suggestedValues: ['5.0', '6.0', '7.0', '8.0'],
  },
  {
    key: 'plex.label',
    category: 'Plex Metadata',
    label: 'Plex Label',
    valueType: 'text',
    supportedMediaTypes: ['movie', 'show'],
    suggestedValues: ['keep', 'archive', 'kids', 'favorite'],
  },
  {
    key: 'classification.kind',
    category: 'Classification & Family',
    label: 'Classification Kind',
    valueType: 'text',
    supportedMediaTypes: ['movie', 'show'],
    suggestedValues: ['Anime Movie', 'Anime Series', 'Cartoon', 'Live-Action'],
  },
  {
    key: 'seerr.daysSinceRequest',
    category: 'Overseerr / Seerr',
    label: 'Days Since Requested',
    valueType: 'number',
    supportedMediaTypes: ['movie', 'show'],
    suggestedValues: ['30', '60', '90', '180'],
  },
  {
    key: 'seerr.requestedBy',
    category: 'Overseerr / Seerr',
    label: 'Requested By Username',
    valueType: 'text',
    supportedMediaTypes: ['movie', 'show'],
  },
  {
    key: 'arr.fileSizeGb',
    category: 'Managerr / Storage',
    label: 'File Size (GB)',
    valueType: 'number',
    supportedMediaTypes: ['movie', 'show'],
    suggestedValues: ['5', '10', '20', '50'],
  },
  {
    key: 'arr.monitored',
    category: 'Managerr / Storage',
    label: 'Is Monitored in Managerr',
    valueType: 'boolean',
    supportedMediaTypes: ['movie', 'show'],
  },
  {
    key: 'media.isHardlinked',
    category: 'Managerr / Storage',
    label: 'Is Hardlinked Seeding (st_nlink > 1)',
    valueType: 'boolean',
    supportedMediaTypes: ['movie', 'show'],
  },
  {
    key: 'media.resolution',
    category: 'Media Codec & Quality',
    label: 'Resolution',
    valueType: 'text',
    supportedMediaTypes: ['movie', 'show'],
    suggestedValues: ['4K', '1080p', '720p', 'SD'],
  },
  {
    key: 'media.videoCodec',
    category: 'Media Codec & Quality',
    label: 'Video Codec',
    valueType: 'text',
    supportedMediaTypes: ['movie', 'show'],
    suggestedValues: ['h264', 'hevc', 'vp9', 'mpeg4'],
  },
  {
    key: 'system.diskFreeSpaceGb',
    category: 'System Watermark',
    label: 'Host Free Space (GB)',
    valueType: 'number',
    supportedMediaTypes: ['movie', 'show'],
    suggestedValues: ['100', '250', '500', '1000'],
  },
];

const SEASON_EPISODE_ONLY_FIELDS = new Set([
  'tv.seasonNumber',
  'tv.episodeNumber',
  'tv.partOfLatestSeason',
  'tv.seasonEpisodeCount',
  'tv.seasonWatchedEpisodes',
  'tv.seasonWatchedPercent',
  'tv.isCompletedSeason',
  'tv.allEpisodesSeenByUsers',
  'tv.seasonFileRankFromNewest',
  'tv.totalSeasons',
  'tv.totalseasons',
]);

const DEFAULT_ACTION_MODES = [
  'delete_files_and_unmonitor',
  'unmonitor_and_delete_existing',
  'delete_work_and_files',
  'delete_show_if_empty',
  'unmonitor_show_if_empty',
  'downgrade_profile',
  'add_import_exclusion',
];

const DISALLOWED_ACTION_MODES = new Set([
  'compress_av1',
  'tiered_compress_or_downgrade_before_delete',
]);

function formatActionModeLabel(mode: string): string {
  return mode
    .split('_')
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ');
}

export function CleanerrRuleBuilder({
  ruleGroup,
  onChange,
  catalog,
  libraries: librariesProp,
  profiles = [],
}: CleanerrRuleBuilderProps) {
  const baseId = useId();
  const librariesQuery = useQuery({
    queryKey: ['libraries'],
    queryFn: () => apiFetch<LibraryOption[]>('/api/v1/libraries'),
    enabled: librariesProp === undefined,
  });

  const libraries = librariesProp ?? librariesQuery.data ?? [];
  const fields = useMemo(() => {
    const raw = catalog?.fields?.length ? catalog.fields : DEFAULT_FIELDS;
    return raw
      .filter((f) => !SEASON_EPISODE_ONLY_FIELDS.has(f.key))
      .map((f) => {
        const normalizedTypes = (f.supportedMediaTypes ?? []).filter(
          (mt) => mt === 'movie' || mt === 'show',
        );
        return {
          ...f,
          supportedMediaTypes: normalizedTypes.length > 0 ? normalizedTypes : ['movie', 'show'],
        };
      });
  }, [catalog?.fields]);
  const comparators = catalog?.comparators?.length ? catalog.comparators : DEFAULT_COMPARATORS;
  const actionModes = useMemo(() => {
    const raw = catalog?.actionModes?.length ? catalog.actionModes : DEFAULT_ACTION_MODES;
    const filtered = raw.filter((mode) => !DISALLOWED_ACTION_MODES.has(mode));
    return filtered.length > 0 ? filtered : DEFAULT_ACTION_MODES;
  }, [catalog?.actionModes]);

  const groupedFields = useMemo(() => {
    const map = new Map<string, CleanerrRuleFieldDescriptor[]>();
    for (const f of fields) {
      const cat = f.category || 'General';
      if (!map.has(cat)) map.set(cat, []);
      map.get(cat)!.push(f);
    }
    return Array.from(map.entries());
  }, [fields]);

  const updateSection = (secIdx: number, newSec: CleanerrRuleSectionDto) => {
    const nextSections = [...ruleGroup.sections];
    nextSections[secIdx] = newSec;
    onChange({ ...ruleGroup, sections: nextSections });
  };

  const addSection = () => {
    const defaultField = fields[0]?.key ?? 'plex.lastViewedDaysAgo';
    const newSection: CleanerrRuleSectionDto = {
      sectionOperator: 'AND',
      title: `Section ${ruleGroup.sections.length + 1}`,
      rules: [
        {
          operator: null,
          field: defaultField,
          comparator: 'greater_than',
          value: '90',
        },
      ],
    };
    onChange({ ...ruleGroup, sections: [...ruleGroup.sections, newSection] });
  };

  const removeSection = (secIdx: number) => {
    const nextSections = ruleGroup.sections.filter((_, idx) => idx !== secIdx);
    onChange({ ...ruleGroup, sections: nextSections });
  };

  const addRule = (secIdx: number) => {
    const sec = ruleGroup.sections[secIdx];
    const defaultField = fields[0]?.key ?? 'plex.lastViewedDaysAgo';
    const newRule: CleanerrRuleConditionDto = {
      operator: 'AND',
      field: defaultField,
      comparator: 'greater_than',
      value: '30',
    };
    updateSection(secIdx, { ...sec, rules: [...sec.rules, newRule] });
  };

  const updateRule = (secIdx: number, ruleIdx: number, newRule: CleanerrRuleConditionDto) => {
    const sec = ruleGroup.sections[secIdx];
    const nextRules = [...sec.rules];
    nextRules[ruleIdx] = newRule;
    updateSection(secIdx, { ...sec, rules: nextRules });
  };

  const removeRule = (secIdx: number, ruleIdx: number) => {
    const sec = ruleGroup.sections[secIdx];
    const nextRules = sec.rules.filter((_, idx) => idx !== ruleIdx);
    updateSection(secIdx, { ...sec, rules: nextRules });
  };

  const targetMediaId = `${baseId}-target-media`;
  const targetLibraryId = `${baseId}-target-library`;
  const graceDaysId = `${baseId}-grace-days`;
  const graceHelpId = `${baseId}-grace-help`;
  const actionModeId = `${baseId}-action-mode`;
  const downgradeProfileId = `${baseId}-downgrade-profile`;
  const minFreeId = `${baseId}-min-free-gb`;
  const minFreeHelpId = `${baseId}-min-free-help`;
  const targetFreeId = `${baseId}-target-free-gb`;
  const targetFreeHelpId = `${baseId}-target-free-help`;
  const maxReclaimId = `${baseId}-max-reclaim-gb`;
  const maxReclaimHelpId = `${baseId}-max-reclaim-help`;
  const cleanFoldersId = `${baseId}-clean-folders`;
  const resetRequestsId = `${baseId}-reset-requests`;
  const pruneTorrentId = `${baseId}-prune-torrent`;
  const useRulesId = `${baseId}-use-rules`;
  const useRulesEnabled = ruleGroup.useRules !== false;
  const selectedLibraryIds = ruleGroup.libraryIds ?? [];

  const normalizedTargetMediaType: 'movie' | 'show' =
    ruleGroup.targetMediaType === 'movie' ? 'movie' : 'show';

  return (
    <div className="space-y-6">
      {/* Scope, Staging & Cleanup Action */}
      <fieldset
        className="m-0 p-4 rounded-[4px] border"
        style={{ backgroundColor: 'var(--arr-card-bg)', borderColor: 'var(--arr-card-border)' }}
      >
        <legend className="px-1.5 text-[12px] font-bold uppercase tracking-wider text-arr-accent">
          Scope &amp; Lifecycle Action
        </legend>

        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 mt-1">
          <div>
            <label htmlFor={targetMediaId} className="block text-[12px] font-bold text-[#e1e2e3] mb-1">
              Target Media Type
            </label>
            <select
              id={targetMediaId}
              value={normalizedTargetMediaType}
              onChange={(e) =>
                onChange({
                  ...ruleGroup,
                  targetMediaType: e.target.value as 'movie' | 'show',
                })
              }
              className="arr-input"
            >
              <option value="movie">Movies (Managerr / Plex Movie)</option>
              <option value="show">TV Series (Full)</option>
            </select>
          </div>

          <div>
            <fieldset className="m-0 p-0 border-0">
              <legend
                id={targetLibraryId}
                className="block text-[12px] font-bold text-[#e1e2e3] mb-1"
              >
                Target Library Filter
              </legend>
              {libraries.length === 0 ? (
                <div className="text-[11px] text-[#909293] py-1.5">
                  All Applicable Libraries
                </div>
              ) : (
                <div
                  className="max-h-24 overflow-y-auto rounded-[4px] border p-2 space-y-1"
                  style={{
                    backgroundColor: 'var(--arr-input-bg)',
                    borderColor: 'var(--arr-card-border)',
                  }}
                >
                  {libraries.map((lib) => {
                    const cbId = `${targetLibraryId}-${lib.id}`;
                    const checked = selectedLibraryIds.includes(lib.id);
                    return (
                      <label
                        key={lib.id}
                        htmlFor={cbId}
                        className="flex items-center gap-2 text-[12px] text-[#e1e2e3] cursor-pointer"
                      >
                        <input
                          id={cbId}
                          type="checkbox"
                          checked={checked}
                          onChange={(e) => {
                            const next = e.target.checked
                              ? [...selectedLibraryIds, lib.id]
                              : selectedLibraryIds.filter((id) => id !== lib.id);
                            onChange({
                              ...ruleGroup,
                              libraryIds: next.length > 0 ? next : undefined,
                            });
                          }}
                        />
                        <span className="truncate">
                          {lib.name} (
                          {lib.mediaVertical ??
                            lib.libraryType ??
                            lib.mediaType ??
                            'media'}
                          )
                        </span>
                      </label>
                    );
                  })}
                </div>
              )}
              <span className="text-[10px] text-[#909293] mt-1 block">
                {selectedLibraryIds.length === 0
                  ? 'All applicable libraries selected'
                  : `${selectedLibraryIds.length} librar${selectedLibraryIds.length === 1 ? 'y' : 'ies'} selected`}
              </span>
            </fieldset>
          </div>

          <div>
            <label htmlFor={graceDaysId} className="block text-[12px] font-bold text-[#e1e2e3] mb-1">
              Grace Period (Days)
            </label>
            <input
              id={graceDaysId}
              type="number"
              min={0}
              max={365}
              aria-describedby={graceHelpId}
              value={ruleGroup.graceDays}
              onChange={(e) =>
                onChange({ ...ruleGroup, graceDays: Math.max(0, Number(e.target.value) || 0) })
              }
              className="arr-input"
            />
            <span id={graceHelpId} className="text-[11px] text-[#909293] mt-1 block">
              Days staged in Leaving Soon before final cleanup execution.
            </span>
          </div>

          <div>
            <label htmlFor={actionModeId} className="block text-[12px] font-bold text-[#e1e2e3] mb-1">
              Action Mode
            </label>
            <select
              id={actionModeId}
              value={ruleGroup.actionMode}
              onChange={(e) => onChange({ ...ruleGroup, actionMode: e.target.value })}
              className="arr-input"
            >
              {actionModes.map((mode) => (
                <option key={mode} value={mode}>
                  {formatActionModeLabel(mode)}
                </option>
              ))}
            </select>
          </div>

          {ruleGroup.actionMode === 'downgrade_profile' && (
            <div>
              <label
                htmlFor={downgradeProfileId}
                className="block text-[12px] font-bold text-[#e1e2e3] mb-1"
              >
                Target Downgrade Quality Profile
              </label>
              {profiles.length > 0 ? (
                <select
                  id={downgradeProfileId}
                  value={ruleGroup.downgradeProfileId ?? ''}
                  onChange={(e) =>
                    onChange({
                      ...ruleGroup,
                      downgradeProfileId: e.target.value ? Number(e.target.value) : null,
                    })
                  }
                  className="arr-input"
                >
                  <option value="">Select quality profile…</option>
                  {profiles.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.name} ({p.qualityCutoff})
                    </option>
                  ))}
                </select>
              ) : (
                <input
                  id={downgradeProfileId}
                  type="number"
                  min={1}
                  placeholder="Profile ID"
                  value={ruleGroup.downgradeProfileId ?? ''}
                  onChange={(e) =>
                    onChange({
                      ...ruleGroup,
                      downgradeProfileId: e.target.value ? Number(e.target.value) : null,
                    })
                  }
                  className="arr-input font-mono"
                />
              )}
            </div>
          )}
        </div>
      </fieldset>

      {/* Watermark, Reclaim Cap & Hygiene Options */}
      <fieldset
        className="m-0 p-4 rounded-[4px] border text-[12px]"
        style={{ backgroundColor: 'var(--arr-toolbar-bg)', borderColor: 'var(--arr-card-border)' }}
      >
        <legend className="px-1.5 text-[12px] font-bold uppercase tracking-wider text-arr-accent">
          Storage Watermarks &amp; Hygiene
        </legend>

        <div className="grid grid-cols-1 md:grid-cols-3 gap-4 mt-1">
          <div>
            <label htmlFor={minFreeId} className="font-semibold text-[#cccccc] block mb-1">
              Disk Watermark Trigger (GB)
            </label>
            <input
              id={minFreeId}
              type="number"
              min={0}
              aria-describedby={minFreeHelpId}
              placeholder="Disabled (runs on any disk size)"
              value={ruleGroup.minFreeSpaceGbTrigger ?? ''}
              onChange={(e) =>
                onChange({
                  ...ruleGroup,
                  minFreeSpaceGbTrigger: e.target.value ? Number(e.target.value) : null,
                })
              }
              className="arr-input !h-[30px]"
            />
            <span id={minFreeHelpId} className="text-[11px] text-[#909293] mt-1 block">
              Only clean when free space drops below this threshold.
            </span>
          </div>

          <div>
            <label htmlFor={targetFreeId} className="font-semibold text-[#cccccc] block mb-1">
              Target Free Watermark (GB)
            </label>
            <input
              id={targetFreeId}
              type="number"
              min={0}
              aria-describedby={targetFreeHelpId}
              placeholder="Watermark stop threshold"
              value={ruleGroup.targetFreeSpaceGbWatermark ?? ''}
              onChange={(e) =>
                onChange({
                  ...ruleGroup,
                  targetFreeSpaceGbWatermark: e.target.value ? Number(e.target.value) : null,
                })
              }
              className="arr-input !h-[30px]"
            />
            <span id={targetFreeHelpId} className="text-[11px] text-[#909293] mt-1 block">
              Stop staging once target free GB is achieved.
            </span>
          </div>

          <div>
            <label htmlFor={maxReclaimId} className="font-semibold text-[#cccccc] block mb-1">
              Max Reclaim Per Run (GB)
            </label>
            <input
              id={maxReclaimId}
              type="number"
              min={0}
              aria-describedby={maxReclaimHelpId}
              placeholder="No limit"
              value={ruleGroup.maxReclaimGb ?? ''}
              onChange={(e) =>
                onChange({
                  ...ruleGroup,
                  maxReclaimGb: e.target.value ? Number(e.target.value) : null,
                })
              }
              className="arr-input !h-[30px]"
            />
            <span id={maxReclaimHelpId} className="text-[11px] text-[#909293] mt-1 block">
              Safety ceiling on total GB cleaned per execution.
            </span>
          </div>
        </div>

        <div
          className="flex flex-wrap items-center gap-6 pt-3 mt-3 border-t"
          style={{ borderTopColor: 'var(--arr-card-border)' }}
        >
          <div className="flex items-center gap-2">
            <input
              id={cleanFoldersId}
              type="checkbox"
              checked={ruleGroup.cleanEmptyFolders ?? false}
              onChange={(e) => onChange({ ...ruleGroup, cleanEmptyFolders: e.target.checked })}
            />
            <label htmlFor={cleanFoldersId} className="cursor-pointer text-[#e1e2e3]">
              Clean Empty Folders
            </label>
          </div>

          <div className="flex items-center gap-2">
            <input
              id={resetRequestsId}
              type="checkbox"
              checked={ruleGroup.resetRequests ?? false}
              onChange={(e) => onChange({ ...ruleGroup, resetRequests: e.target.checked })}
            />
            <label htmlFor={resetRequestsId} className="cursor-pointer text-[#e1e2e3]">
              Reset Seerr / Overseerr Requests on Cleanup
            </label>
          </div>

          <div className="flex items-center gap-2">
            <input
              id={pruneTorrentId}
              type="checkbox"
              checked={ruleGroup.pruneTorrentClient ?? false}
              onChange={(e) => onChange({ ...ruleGroup, pruneTorrentClient: e.target.checked })}
            />
            <label htmlFor={pruneTorrentId} className="cursor-pointer text-[#e1e2e3]">
              Prune Download Client Torrents &amp; Files
            </label>
          </div>

          <div className="flex items-center gap-2">
            <input
              id={useRulesId}
              type="checkbox"
              checked={useRulesEnabled}
              onChange={(e) => onChange({ ...ruleGroup, useRules: e.target.checked })}
            />
            <label htmlFor={useRulesId} className="cursor-pointer text-[#e1e2e3]">
              Evaluate Rule Sections (uncheck to match all items in scope)
            </label>
          </div>
        </div>
      </fieldset>

      {/* Multi-Section Boolean Rules */}
      <fieldset className="m-0 p-0 border-0 space-y-4">
        <div className="flex items-center justify-between gap-3">
          <legend className="p-0 text-[14px] font-bold text-[#e1e2e3] flex items-center gap-2">
            <SlidersHorizontal className="h-4 w-4 text-arr-accent" aria-hidden="true" />
            <span>Rule Evaluation Sections ({ruleGroup.sections.length})</span>
          </legend>
          {useRulesEnabled && (
            <ArrButton variant="primary" className="!h-[28px] !text-[12px]" onClick={addSection}>
              <Plus className="h-3.5 w-3.5" aria-hidden="true" />
              Add Section
            </ArrButton>
          )}
        </div>

        {!useRulesEnabled && (
          <div
            className="p-3 rounded-[4px] border text-[12px] text-[#cccccc]"
            style={{
              backgroundColor: 'var(--arr-toolbar-bg)',
              borderColor: 'var(--arr-card-border)',
            }}
          >
            Rule section evaluation is disabled — all items matching the selected media type and library scope will be targeted.
          </div>
        )}

        {useRulesEnabled && ruleGroup.sections.map((section, secIdx) => (
          <div key={secIdx} className="space-y-3">
            {secIdx > 0 && (
              <div className="flex items-center justify-center my-2">
                <div
                  role="group"
                  aria-label={`Connector before section ${secIdx + 1}`}
                  className="flex items-center rounded-full p-0.5 border"
                  style={{
                    backgroundColor: 'var(--arr-toolbar-bg)',
                    borderColor: 'var(--arr-card-border)',
                  }}
                >
                  <button
                    type="button"
                    aria-pressed={section.sectionOperator === 'AND'}
                    onClick={() => updateSection(secIdx, { ...section, sectionOperator: 'AND' })}
                    className={`px-3 py-1 rounded-full text-[11px] font-bold cursor-pointer transition-colors ${
                      section.sectionOperator === 'AND'
                        ? 'bg-[#5d9cec] text-white'
                        : 'text-[#909293] hover:text-[#e1e2e3]'
                    }`}
                  >
                    AND
                  </button>
                  <button
                    type="button"
                    aria-pressed={section.sectionOperator === 'OR'}
                    onClick={() => updateSection(secIdx, { ...section, sectionOperator: 'OR' })}
                    className={`px-3 py-1 rounded-full text-[11px] font-bold cursor-pointer transition-colors ${
                      section.sectionOperator === 'OR'
                        ? 'bg-[#9b6ef3] text-white'
                        : 'text-[#909293] hover:text-[#e1e2e3]'
                    }`}
                  >
                    OR
                  </button>
                </div>
              </div>
            )}

            <div
              className="rounded-[6px] border overflow-hidden"
              style={{ backgroundColor: 'var(--arr-card-bg)', borderColor: 'var(--arr-card-border)' }}
            >
              <div
                className="px-4 py-2.5 border-b flex items-center justify-between gap-2"
                style={{
                  backgroundColor: 'var(--arr-toolbar-bg)',
                  borderBottomColor: 'var(--arr-card-border)',
                }}
              >
                <div className="flex items-center gap-2">
                  <span className="font-bold text-[13px] text-[#eeeeee]">
                    Section {secIdx + 1}
                  </span>
                  <span className="text-[11px] text-[#909293]">
                    ({section.rules.length} condition{section.rules.length === 1 ? '' : 's'})
                  </span>
                </div>
                <div className="flex items-center gap-2">
                  <ArrButton
                    variant="default"
                    className="!h-[24px] !px-2 !text-[11px]"
                    onClick={() => addRule(secIdx)}
                  >
                    <Plus className="h-3 w-3" aria-hidden="true" />
                    Add Rule
                  </ArrButton>
                  {ruleGroup.sections.length > 1 && (
                    <button
                      type="button"
                      aria-label={`Delete section ${secIdx + 1}`}
                      onClick={() => removeSection(secIdx)}
                      className="text-[#909293] hover:text-[#f05050] p-1 cursor-pointer transition-colors"
                    >
                      <Trash2 className="h-4 w-4" aria-hidden="true" />
                    </button>
                  )}
                </div>
              </div>

              <div className="p-3 space-y-2.5">
                {section.rules.map((rule, ruleIdx) => {
                  const selectedField = fields.find((f) => f.key === rule.field);
                  const selectedValType = selectedField?.valueType ?? 'text';
                  const availableComparators = comparators.filter((c) =>
                    c.supportedValueTypes.includes(selectedValType),
                  );
                  const activeComparator = comparators.find((c) => c.key === rule.comparator);
                  const requiresVal = activeComparator ? activeComparator.requiresValue : true;
                  const isCompareField = Boolean(rule.secondField);
                  const rowPrefix = `${baseId}-s${secIdx}-r${ruleIdx}`;

                  return (
                    <div
                      key={ruleIdx}
                      className="p-2.5 rounded-[4px] border flex flex-wrap items-start gap-2.5 text-[12px]"
                      style={{
                        backgroundColor: 'var(--arr-page-bg)',
                        borderColor: 'var(--arr-card-border)',
                      }}
                    >
                      {ruleIdx > 0 ? (
                        <div className="w-20">
                          <label htmlFor={`${rowPrefix}-op`} className="visually-hidden">
                            Rule {ruleIdx + 1} operator
                          </label>
                          <select
                            id={`${rowPrefix}-op`}
                            value={rule.operator ?? 'AND'}
                            onChange={(e) =>
                              updateRule(secIdx, ruleIdx, {
                                ...rule,
                                operator: e.target.value as 'AND' | 'OR',
                              })
                            }
                            className="arr-input !h-[28px] !w-20 !py-0 !px-2 font-bold !text-[11px]"
                          >
                            <option value="AND">AND</option>
                            <option value="OR">OR</option>
                          </select>
                        </div>
                      ) : (
                        <span className="w-20 h-[28px] inline-flex items-center justify-center font-bold text-[#909293] text-[11px]">
                          IF
                        </span>
                      )}

                      <div className="w-64 max-w-full">
                        <label htmlFor={`${rowPrefix}-field`} className="visually-hidden">
                          Condition field
                        </label>
                        <select
                          id={`${rowPrefix}-field`}
                          value={rule.field}
                          onChange={(e) => {
                            const newFieldKey = e.target.value;
                            const nf = fields.find((f) => f.key === newFieldKey);
                            const compatibleComp = comparators.find((c) =>
                              c.supportedValueTypes.includes(nf?.valueType ?? 'text'),
                            );
                            updateRule(secIdx, ruleIdx, {
                              ...rule,
                              field: newFieldKey,
                              comparator: compatibleComp?.key ?? 'equals',
                              value: nf?.suggestedValues?.[0] ?? '',
                              secondField: undefined,
                            });
                          }}
                          className="arr-input !h-[28px] !w-full !py-0 !px-2 font-medium"
                        >
                          {groupedFields.map(([cat, fList]) => (
                            <optgroup key={cat} label={cat}>
                              {fList.map((f) => (
                                <option key={f.key} value={f.key}>
                                  {f.label} ({f.valueType})
                                </option>
                              ))}
                            </optgroup>
                          ))}
                        </select>
                      </div>

                      <div className="w-44 max-w-full">
                        <label htmlFor={`${rowPrefix}-comp`} className="visually-hidden">
                          Comparator
                        </label>
                        <select
                          id={`${rowPrefix}-comp`}
                          value={rule.comparator}
                          onChange={(e) =>
                            updateRule(secIdx, ruleIdx, { ...rule, comparator: e.target.value })
                          }
                          className="arr-input !h-[28px] !w-full !py-0 !px-2 font-mono text-[11px]"
                        >
                          {(availableComparators.length > 0 ? availableComparators : comparators).map(
                            (c) => (
                              <option key={c.key} value={c.key}>
                                {c.label}
                              </option>
                            ),
                          )}
                        </select>
                      </div>

                      {requiresVal ? (
                        <div className="flex-1 min-w-[200px] flex items-start gap-2">
                          <button
                            type="button"
                            aria-pressed={isCompareField}
                            aria-label={
                              isCompareField
                                ? 'Switch to literal value comparison'
                                : 'Switch to compare against another property'
                            }
                            title={
                              isCompareField
                                ? 'Switch to literal value'
                                : 'Compare against another property'
                            }
                            onClick={() => {
                              if (isCompareField) {
                                updateRule(secIdx, ruleIdx, {
                                  ...rule,
                                  secondField: undefined,
                                  value: '',
                                });
                              } else {
                                const matchingField = fields.find(
                                  (f) => f.key !== rule.field && f.valueType === selectedValType,
                                );
                                updateRule(secIdx, ruleIdx, {
                                  ...rule,
                                  value: null,
                                  secondField: matchingField?.key ?? fields[0]?.key,
                                });
                              }
                            }}
                            className={`h-[28px] px-2 rounded-[4px] border cursor-pointer transition-colors shrink-0 ${
                              isCompareField
                                ? 'bg-[#9b6ef3]/20 border-[#9b6ef3] text-[#c084fc]'
                                : 'bg-arr-input border-arr-card-border text-[#909293] hover:text-white'
                            }`}
                          >
                            <ArrowRightLeft className="h-3.5 w-3.5" aria-hidden="true" />
                          </button>

                          {isCompareField ? (
                            <div className="flex-1">
                              <label htmlFor={`${rowPrefix}-second-field`} className="visually-hidden">
                                Comparison target field
                              </label>
                              <select
                                id={`${rowPrefix}-second-field`}
                                value={rule.secondField ?? ''}
                                onChange={(e) =>
                                  updateRule(secIdx, ruleIdx, {
                                    ...rule,
                                    secondField: e.target.value,
                                  })
                                }
                                className="arr-input !h-[28px] w-full !py-0 !px-2 text-[#c084fc] font-medium"
                              >
                                {groupedFields.map(([cat, fList]) => (
                                  <optgroup key={cat} label={`Field: ${cat}`}>
                                    {fList.map((f) => (
                                      <option key={f.key} value={f.key}>
                                        [Field] {f.label} ({f.valueType})
                                      </option>
                                    ))}
                                  </optgroup>
                                ))}
                              </select>
                            </div>
                          ) : (
                            <div className="flex-1 flex flex-col gap-1">
                              <label htmlFor={`${rowPrefix}-val`} className="visually-hidden">
                                Condition value
                              </label>
                              <input
                                id={`${rowPrefix}-val`}
                                type={selectedValType === 'number' ? 'number' : 'text'}
                                value={rule.value ?? ''}
                                onChange={(e) =>
                                  updateRule(secIdx, ruleIdx, { ...rule, value: e.target.value })
                                }
                                placeholder={
                                  selectedField?.suggestedValues?.[0]
                                    ? `e.g. ${selectedField.suggestedValues[0]}`
                                    : 'Condition value'
                                }
                                className="arr-input !h-[28px] !py-0 !px-2 font-mono text-[12px]"
                              />
                              {selectedField?.suggestedValues &&
                                selectedField.suggestedValues.length > 0 && (
                                  <div className="flex flex-wrap items-center gap-1">
                                    <span className="text-[10px] text-[#909293]">Presets:</span>
                                    {selectedField.suggestedValues.map((val) => (
                                      <button
                                        key={val}
                                        type="button"
                                        onClick={() =>
                                          updateRule(secIdx, ruleIdx, { ...rule, value: val })
                                        }
                                        className={`px-1.5 py-[1px] rounded text-[10px] font-mono cursor-pointer transition-colors ${
                                          rule.value === val
                                            ? 'bg-arr-accent text-[#062420] font-bold'
                                            : 'bg-arr-card text-[#909293] hover:text-white'
                                        }`}
                                      >
                                        {val}
                                      </button>
                                    ))}
                                  </div>
                                )}
                            </div>
                          )}
                        </div>
                      ) : (
                        <div className="flex-1 h-[28px] inline-flex items-center text-[11px] text-[#909293] italic">
                          No value required for this comparator.
                        </div>
                      )}

                      {section.rules.length > 1 && (
                        <button
                          type="button"
                          aria-label={`Remove rule ${ruleIdx + 1} from section ${secIdx + 1}`}
                          onClick={() => removeRule(secIdx, ruleIdx)}
                          className="h-[28px] px-1.5 text-[#909293] hover:text-[#f05050] cursor-pointer transition-colors shrink-0"
                        >
                          <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />
                        </button>
                      )}
                    </div>
                  );
                })}
              </div>
            </div>
          </div>
        ))}
      </fieldset>
    </div>
  );
}
