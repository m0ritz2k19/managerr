import { useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  AlertCircle,
  Check,
  CheckCircle2,
  ChevronRight,
  FileCheck,
  Loader2,
  RefreshCw,
  Sparkles,
  Trash2,
  Upload,
  X,
} from 'lucide-react';
import clsx from 'clsx';
import { apiFetch, formatBytes } from '../lib/api';
import {
  ArrAlert,
  ArrButton,
  ArrLabel,
  ArrModal,
  FieldSet,
  FormGroup,
  SmartBreakText,
} from './arr/ArrPrimitives';

export interface DiskScanReportDto {
  scannedFilesCount: number;
  autoAdoptedCount: number;
  missingRemovedCount: number;
  unmappedCount: number;
  messages?: string[];
}

export interface FileRenamePreviewItem {
  mediaFileId: number;
  workId?: number;
  workTitle?: string;
  unitId?: number;
  episodeCode?: string;
  currentPath?: string;
  newPath?: string;
  currentRelativePath?: string;
  newRelativePath?: string;
  needsRename?: boolean;
  existsOnDisk?: boolean;
  relativePath?: string;
}

export interface UnmappedDiscoveredFileDto {
  id: string | number;
  fileName: string;
  relativePath: string;
  libraryId?: number;
  libraryName: string;
  sizeBytes: number;
  parsedTitle?: string;
  parsedSeason?: number | null;
  parsedSeasonNumber?: number | null;
  parsedEpisodes?: number[];
  parsedEpisodeNumbers?: number[];
  parsedQuality?: string | null;
  reason?: string | null;
  suggestedWorkId?: number | null;
}

export interface NamingTokenDescriptor {
  token: string;
  description: string;
  example: string;
}

export interface NamingConfigDto {
  renameMediaFiles: boolean;
  useHardlinks?: boolean;
  colonReplacement: 'delete' | 'dash' | 'space_dash' | 'space';
  templates: Record<string, string>;
  availableTokens: NamingTokenDescriptor[];
}

const DEFAULT_STANDARD_TOKENS: NamingTokenDescriptor[] = [
  { token: '{Series TitleYear}', description: 'Series Title with Year', example: 'Breaking Bad (2008)' },
  { token: '{Series Title}', description: 'Series clean title', example: 'Breaking Bad' },
  { token: '{Movie TitleYear}', description: 'Movie Title with Year', example: 'Dune Part Two (2024)' },
  { token: '{Movie Title}', description: 'Movie Title', example: 'Dune: Part Two' },
  { token: '{Artist Name}', description: 'Music artist name', example: 'Daft Punk' },
  { token: '{Author Name}', description: 'Book or comic author name', example: 'Andy Weir' },
  { token: '{Album Title}', description: 'Music album title', example: 'Random Access Memories' },
  { token: '{Track:00}', description: '2-digit zero-padded track number', example: '01' },
  { token: '{Track Title}', description: 'Track or chapter title', example: 'Give Life Back to Music' },
  { token: '{Book Title}', description: 'Book or audiobook title', example: 'Project Hail Mary' },
  { token: '{Comic Title}', description: 'Comic or manga title', example: 'Saga Compendium One' },
  { token: '{Issue:000}', description: '3-digit zero-padded issue number', example: '012' },
  { token: '{Issue:00}', description: '2-digit zero-padded issue number', example: '12' },
  { token: '{Release Year}', description: 'Four digit year', example: '2024' },
  { token: '{Season:00}', description: '2-digit zero-padded season number', example: '01' },
  { token: '{Episode:00}', description: '2-digit zero-padded episode number', example: '05' },
  { token: '{Absolute:000}', description: '3-digit anime absolute episode number', example: '013' },
  { token: '{Episode Title}', description: 'Official Episode Title', example: 'Gray Matter' },
  { token: '{Quality Full}', description: 'Quality Source + Resolution + Codec', example: 'WEB-DL 1080p AVC' },
  { token: '{Quality Title}', description: 'Quality Source + Resolution tier', example: 'WEB-DL-1080p' },
  { token: '{MediaInfo VideoCodec}', description: 'Video codec tag', example: 'x265' },
  { token: '{MediaInfo AudioCodec}', description: 'Audio codec tag', example: 'EAC3' },
  { token: '{MediaInfo AudioLanguages}', description: 'Audio languages tag', example: '[DE+EN]' },
  { token: '{MediaInfo VideoDynamicRangeType}', description: 'HDR / DV format tag', example: 'DV' },
  { token: '{Custom Formats}', description: 'Matched custom format tags', example: 'HDR10+ Atmos' },
  { token: '{Edition Tags}', description: 'Movie edition tag', example: 'IMAX' },
  { token: '{Release Group}', description: 'Original scene/P2P release group', example: 'NTb' },
  { token: '{imdbId}', description: 'IMDb Identifier', example: 'tt0903747' },
  { token: '{tmdbId}', description: 'TMDB Identifier', example: '1396' },
  { token: '{tvdbId}', description: 'TVDB Identifier', example: '81189' },
];

const NAMING_VERTICAL_DEFAULTS: Record<string, { label: string; defaultTemplate: string }> = {
  tv: {
    label: 'TV Series',
    defaultTemplate:
      '{Series TitleYear}/Season {Season:00}/{Series TitleYear} - S{Season:00}E{Episode:00} - {Episode Title} [{Quality Full}][{MediaInfo VideoCodec}][{MediaInfo AudioLanguages}]',
  },
  movies: {
    label: 'Movies',
    defaultTemplate:
      '{Movie Title} ({Release Year})/{Movie Title} ({Release Year}) [{Quality Full}][{MediaInfo VideoCodec}][{MediaInfo AudioLanguages}]',
  },
  anime: {
    label: 'Anime',
    defaultTemplate:
      '{Series TitleYear}/Season {Season:00}/{Series TitleYear} - S{Season:00}E{Episode:00} - {Episode Title} [{Quality Full}]',
  },
  music: {
    label: 'Music',
    defaultTemplate:
      '{Artist Name}/{Album Title} ({Release Year})/{Track:00} - {Track Title}',
  },
  ebooks: {
    label: 'Books',
    defaultTemplate:
      '{Author Name}/{Book Title} ({Release Year}) - {Author Name}',
  },
  audiobooks: {
    label: 'Audiobooks',
    defaultTemplate:
      '{Author Name}/{Book Title} ({Release Year})/{Track:00} - {Track Title}',
  },
  comics: {
    label: 'Comics / Manga',
    defaultTemplate:
      '{Comic Title} ({Release Year})/Issue #{Issue:000}',
  },
};

/* ========================================================================= */
/*  1. BULK FILE RENAME PREVIEW MODAL                                        */
/* ========================================================================= */

export interface BulkFileRenameModalProps {
  isOpen: boolean;
  onClose: () => void;
  title: string;
  previewUrl: string;
  executeUrl: string;
  onSuccess?: (renamedCount: number) => void;
}

export function BulkFileRenameModal({
  isOpen,
  onClose,
  title,
  previewUrl,
  executeUrl,
  onSuccess,
}: BulkFileRenameModalProps) {
  const qc = useQueryClient();
  const [selectedIds, setSelectedIds] = useState<Set<number>>(new Set());
  const [executeBanner, setExecuteBanner] = useState<{ kind: 'success' | 'danger'; text: string } | null>(null);

  const previewQuery = useQuery({
    queryKey: ['bulk-rename-preview', previewUrl],
    queryFn: async () => {
      const resp = await apiFetch<FileRenamePreviewItem[] | { items: FileRenamePreviewItem[] }>(previewUrl);
      const items = Array.isArray(resp) ? resp : resp?.items ?? [];
      return items;
    },
    enabled: isOpen,
  });

  const items = useMemo(() => previewQuery.data ?? [], [previewQuery.data]);

  // When new items arrive, select all by default
  useEffect(() => {
    if (items.length > 0) {
      setSelectedIds(new Set(items.map((i) => i.mediaFileId)));
    } else {
      setSelectedIds(new Set());
    }
  }, [items]);

  const toggleSelectAll = () => {
    if (selectedIds.size === items.length) {
      setSelectedIds(new Set());
    } else {
      setSelectedIds(new Set(items.map((i) => i.mediaFileId)));
    }
  };

  const toggleItem = (id: number) => {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  };

  const executeMutation = useMutation({
    mutationFn: async () => {
      return apiFetch<{ renamedCount?: number; messages?: string[] }>(executeUrl, {
        method: 'POST',
        body: JSON.stringify({ mediaFileIds: Array.from(selectedIds) }),
      });
    },
    onSuccess: (res) => {
      const count = res?.renamedCount ?? selectedIds.size;
      setExecuteBanner({
        kind: 'success',
        text: `Successfully renamed ${count} media file${count === 1 ? '' : 's'} on disk.`,
      });
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
      qc.invalidateQueries({ queryKey: ['catalog-tree'] });
      qc.invalidateQueries({ queryKey: ['libraries'] });
      onSuccess?.(count);
      setTimeout(() => {
        onClose();
        setExecuteBanner(null);
      }, 1200);
    },
    onError: (err: Error) => {
      setExecuteBanner({
        kind: 'danger',
        text: `Rename execution failed: ${err.message}`,
      });
    },
  });

  if (!isOpen) return null;

  return (
    <ArrModal
      title={title}
      onClose={onClose}
      maxWidth="max-w-[94vw] xl:max-w-[1380px]"
      footer={
        <div className="flex items-center justify-between w-full">
          <div className="text-[12px] text-[#909293]">
            {items.length > 0 ? (
              <span>
                Selected <strong>{selectedIds.size}</strong> of {items.length} file{items.length === 1 ? '' : 's'}
              </span>
            ) : null}
          </div>
          <div className="flex items-center gap-2">
            <ArrButton variant="default" onClick={onClose}>
              Cancel
            </ArrButton>
            <ArrButton
              variant="primary"
              disabled={selectedIds.size === 0 || executeMutation.isPending}
              busy={executeMutation.isPending}
              onClick={() => executeMutation.mutate()}
            >
              <FileCheck className="h-4 w-4" /> Rename Selected ({selectedIds.size})
            </ArrButton>
          </div>
        </div>
      }
    >
      <div className="space-y-4">
        {executeBanner && (
          <ArrAlert kind={executeBanner.kind}>
            <div className="flex items-center gap-2">
              {executeBanner.kind === 'success' ? (
                <CheckCircle2 className="h-4 w-4 shrink-0 text-[#27c24c]" />
              ) : (
                <AlertCircle className="h-4 w-4 shrink-0 text-[#f05050]" />
              )}
              <span>{executeBanner.text}</span>
            </div>
          </ArrAlert>
        )}

        {previewQuery.isLoading ? (
          <div className="py-12 flex flex-col items-center justify-center gap-2 text-[#909293]">
            <Loader2 className="h-6 w-6 animate-spin text-arr-accent" />
            <span className="text-[13px]">Evaluating naming templates and previewing renames...</span>
          </div>
        ) : previewQuery.isError ? (
          <ArrAlert kind="danger">
            Failed to load rename preview: {(previewQuery.error as Error).message}
          </ArrAlert>
        ) : items.length === 0 ? (
          <div className="py-10 text-center space-y-2">
            <CheckCircle2 className="h-10 w-10 text-[#27c24c] mx-auto" />
            <div className="text-[15px] font-medium text-[#eeeeee]">All files already match naming convention</div>
            <p className="text-[12px] text-[#909293] max-w-md mx-auto">
              No files require renaming according to current naming templates and colon replacement rules.
            </p>
          </div>
        ) : (
          <div className="space-y-3">
            <div className="flex items-center justify-between text-[12px] text-[#cccccc] pb-1 border-b border-arr-card-border">
              <label className="inline-flex items-center gap-2 cursor-pointer select-none">
                <input
                  type="checkbox"
                  checked={selectedIds.size === items.length && items.length > 0}
                  onChange={toggleSelectAll}
                  className="rounded border-[#4a5158] bg-[#1e1e1e] text-arr-accent focus:ring-0 cursor-pointer"
                />
                <span className="font-semibold">Select All Visible ({items.length})</span>
              </label>
              <ArrButton
                variant="default"
                className="!h-[26px] !px-2 !text-[11px]"
                onClick={() => void previewQuery.refetch()}
              >
                <RefreshCw className={clsx('h-3 w-3', previewQuery.isFetching && 'animate-spin')} /> Refresh Preview
              </ArrButton>
            </div>

            <div className="max-h-[540px] overflow-y-auto overflow-x-auto border border-arr-card-border rounded-[4px]">
              <table className="arr-table !text-[12px]">
                <thead>
                  <tr>
                    <th className="w-8"></th>
                    <th>Current Path</th>
                    <th className="w-8 text-center"></th>
                    <th>Proposed New Path</th>
                  </tr>
                </thead>
                <tbody>
                  {items.map((item) => {
                    const isChecked = selectedIds.has(item.mediaFileId);
                    return (
                      <tr
                        key={item.mediaFileId}
                        onClick={() => toggleItem(item.mediaFileId)}
                        className="cursor-pointer hover:bg-white/[0.04]"
                      >
                        <td onClick={(e) => e.stopPropagation()}>
                          <input
                            type="checkbox"
                            checked={isChecked}
                            onChange={() => toggleItem(item.mediaFileId)}
                            className="rounded border-[#4a5158] bg-[#1e1e1e] text-arr-accent focus:ring-0 cursor-pointer"
                          />
                        </td>
                        <td className="font-mono text-[11.5px] text-[#c2c4c6] min-w-[280px]">
                          <SmartBreakText text={item.currentRelativePath || item.currentPath || '-'} />
                        </td>
                        <td className="text-center text-arr-accent">
                          <ChevronRight className="h-4 w-4 inline" />
                        </td>
                        <td className="font-mono text-[11.5px] text-[#27c24c] min-w-[300px] font-semibold">
                          <SmartBreakText text={item.newRelativePath || item.newPath || '-'} />
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </div>
    </ArrModal>
  );
}

/* ========================================================================= */
/*  2. UNMAPPED FILES INTERACTIVE MAPPER PANEL                               */
/* ========================================================================= */

export interface UnmappedFilesPanelProps {
  libraries: Array<{ id: number; name: string; mediaType: string }>;
  works: Array<{ id: number; libraryId: number; title: string; year: number | null; workKind: string }>;
  canWrite: boolean;
}

interface RowMappingState {
  workId: number | '';
  seasonNumber: string;
  episodeNumbers: string;
  renameToTemplate: boolean;
}

export function UnmappedFilesPanel({ libraries, works, canWrite }: UnmappedFilesPanelProps) {
  const qc = useQueryClient();
  const [selectedLibId, setSelectedLibId] = useState<number | null>(null);
  const [rowStates, setRowStates] = useState<Record<string | number, RowMappingState>>({});
  const [actionBanner, setActionBanner] = useState<{ kind: 'info' | 'success' | 'danger'; text: string } | null>(null);

  const unmappedQuery = useQuery({
    queryKey: ['unmapped-files', selectedLibId],
    queryFn: async () => {
      const url = selectedLibId
        ? `/api/v1/libraries/unmapped-files?libraryId=${selectedLibId}`
        : '/api/v1/libraries/unmapped-files';
      const resp = await apiFetch<UnmappedDiscoveredFileDto[] | { items: UnmappedDiscoveredFileDto[] }>(url);
      return Array.isArray(resp) ? resp : resp?.items ?? [];
    },
  });

  const unmappedFiles = useMemo(() => unmappedQuery.data ?? [], [unmappedQuery.data]);

  // Seed row mapping state when files load or update
  useEffect(() => {
    if (unmappedFiles.length > 0) {
      setRowStates((prev) => {
        const next = { ...prev };
        for (const f of unmappedFiles) {
          const seasonVal = f.parsedSeason ?? f.parsedSeasonNumber;
          const epsVal = f.parsedEpisodes ?? f.parsedEpisodeNumbers;
          if (!next[f.id]) {
            next[f.id] = {
              workId: f.suggestedWorkId ?? '',
              seasonNumber: seasonVal !== null && seasonVal !== undefined ? String(seasonVal) : '',
              episodeNumbers: epsVal?.length ? epsVal.join(', ') : '',
              renameToTemplate: true,
            };
          }
        }
        return next;
      });
    }
  }, [unmappedFiles]);

  const updateRowState = (id: string | number, patch: Partial<RowMappingState>) => {
    setRowStates((prev) => ({
      ...prev,
      [id]: {
        ...(prev[id] ?? {
          workId: '',
          seasonNumber: '',
          episodeNumbers: '',
          renameToTemplate: true,
        }),
        ...patch,
      },
    }));
  };

  const mapSingleMutation = useMutation({
    mutationFn: async ({ file, state }: { file: UnmappedDiscoveredFileDto; state: RowMappingState }) => {
      if (!state.workId) {
        throw new Error('Please select a target Work to map this file.');
      }
      const seasonNum = state.seasonNumber.trim() ? Number(state.seasonNumber) : null;
      const epNums = state.episodeNumbers
        .split(',')
        .map((s) => Number(s.trim()))
        .filter((n) => Number.isFinite(n) && n > 0);

      return apiFetch<{ mappedCount?: number; messages?: string[] }>('/api/v1/libraries/unmapped-files/map', {
        method: 'POST',
        body: JSON.stringify({
          items: [
            {
              unmappedFileId: file.id,
              workId: Number(state.workId),
              seasonNumber: seasonNum,
              episodeNumbers: epNums.length ? epNums : undefined,
              renameToTemplate: state.renameToTemplate,
            },
          ],
        }),
      });
    },
    onSuccess: (_, vars) => {
      setActionBanner({
        kind: 'success',
        text: `Successfully mapped and imported '${vars.file.fileName}'!`,
      });
      qc.invalidateQueries({ queryKey: ['unmapped-files'] });
      qc.invalidateQueries({ queryKey: ['catalog-works'] });
      qc.invalidateQueries({ queryKey: ['libraries'] });
    },
    onError: (err: Error) => {
      setActionBanner({
        kind: 'danger',
        text: `Mapping failed: ${err.message}`,
      });
    },
  });

  const dismissMutation = useMutation({
    mutationFn: async (id: string | number) => {
      return apiFetch<void>(`/api/v1/libraries/unmapped-files/${id}`, {
        method: 'DELETE',
      });
    },
    onSuccess: () => {
      setActionBanner({
        kind: 'info',
        text: 'Unmapped file record dismissed.',
      });
      qc.invalidateQueries({ queryKey: ['unmapped-files'] });
    },
    onError: (err: Error) => {
      setActionBanner({
        kind: 'danger',
        text: `Dismiss failed: ${err.message}`,
      });
    },
  });

  // Group works by vertical or name for clean dropdown
  const sortedWorks = useMemo(() => {
    return [...works].sort((a, b) => a.title.localeCompare(b.title));
  }, [works]);

  return (
    <FieldSet
      legend="Unmapped Discovered Files"
      action={
        <div className="flex items-center gap-2">
          <select
            value={selectedLibId ?? ''}
            onChange={(e) => setSelectedLibId(e.target.value ? Number(e.target.value) : null)}
            className="arr-input !h-[30px] !py-1 !text-[12px] !w-48"
          >
            <option value="">All Libraries</option>
            {libraries.map((lib) => (
              <option key={lib.id} value={lib.id}>
                {lib.name} ({lib.mediaType})
              </option>
            ))}
          </select>
          <ArrButton
            variant="default"
            className="!h-[30px] !px-2.5 !text-[12px]"
            onClick={() => void unmappedQuery.refetch()}
          >
            <RefreshCw className={clsx('h-3.5 w-3.5', unmappedQuery.isFetching && 'animate-spin')} /> Refresh
          </ArrButton>
        </div>
      }
    >
      <div className="space-y-4">
        {actionBanner && (
          <ArrAlert kind={actionBanner.kind}>
            <div className="flex items-center justify-between gap-2">
              <span>{actionBanner.text}</span>
              <button
                type="button"
                onClick={() => setActionBanner(null)}
                className="opacity-75 hover:opacity-100 cursor-pointer"
              >
                <X className="h-4 w-4" />
              </button>
            </div>
          </ArrAlert>
        )}

        {unmappedQuery.isLoading ? (
          <div className="py-8 flex items-center justify-center gap-2 text-[#909293]">
            <Loader2 className="h-5 w-5 animate-spin text-arr-accent" />
            <span className="text-[13px]">Loading discovered unmapped files...</span>
          </div>
        ) : unmappedFiles.length === 0 ? (
          <ArrAlert kind="info">
            <div className="flex items-center gap-2">
              <Check className="h-4 w-4 text-[#27c24c] shrink-0" />
              <span>
                No unmapped files discovered on disk. Click <strong>Refresh &amp; Scan</strong> on any library
                above to discover and catalog unmapped files.
              </span>
            </div>
          </ArrAlert>
        ) : (
          <div className="overflow-x-auto border border-arr-card-border rounded-[4px]">
            <table className="arr-table !text-[12px]">
              <thead>
                <tr>
                  <th>File Details</th>
                  <th>Parsed Info</th>
                  <th>Target Work</th>
                  <th>Season / Ep</th>
                  <th>Template</th>
                  <th className="text-right">Actions</th>
                </tr>
              </thead>
              <tbody>
                {unmappedFiles.map((file) => {
                  const state = rowStates[file.id] ?? {
                    workId: file.suggestedWorkId ?? '',
                    seasonNumber: '',
                    episodeNumbers: '',
                    renameToTemplate: true,
                  };
                  const isBusy = mapSingleMutation.isPending && mapSingleMutation.variables?.file.id === file.id;
                  const isDismissBusy = dismissMutation.isPending && dismissMutation.variables === file.id;

                  return (
                    <tr key={file.id}>
                      <td className="max-w-[280px]">
                        <div className="font-semibold text-[#eeeeee] break-all leading-tight">
                          {file.fileName}
                        </div>
                        <div className="font-mono text-[11px] text-[#909293] truncate" title={file.relativePath}>
                          {file.relativePath}
                        </div>
                        <div className="flex items-center gap-1.5 mt-1">
                          <ArrLabel kind="default">{file.libraryName}</ArrLabel>
                          <span className="text-[11px] text-[#777777] font-mono">
                            {formatBytes(file.sizeBytes)}
                          </span>
                          {file.reason && (
                            <span className="text-[10px] text-[#ffa500] truncate max-w-[120px]" title={file.reason}>
                              {file.reason}
                            </span>
                          )}
                        </div>
                      </td>

                      <td className="max-w-[180px]">
                        <div className="space-y-1">
                          {file.parsedTitle && (
                            <div className="text-[12px] text-arr-accent font-medium truncate" title={file.parsedTitle}>
                              {file.parsedTitle}
                            </div>
                          )}
                          <div className="flex flex-wrap gap-1">
                            {(() => {
                              const s = file.parsedSeason ?? file.parsedSeasonNumber;
                              const eps = file.parsedEpisodes ?? file.parsedEpisodeNumbers;
                              return (
                                <>
                                  {s !== null && s !== undefined && (
                                    <ArrLabel kind="primary">S{String(s).padStart(2, '0')}</ArrLabel>
                                  )}
                                  {eps && eps.length > 0 && (
                                    <ArrLabel kind="default">
                                      {eps.map((ep) => `E${String(ep).padStart(2, '0')}`).join(', ')}
                                    </ArrLabel>
                                  )}
                                </>
                              );
                            })()}
                            {file.parsedQuality && (
                              <ArrLabel kind="success">{file.parsedQuality}</ArrLabel>
                            )}
                          </div>
                        </div>
                      </td>

                      <td className="min-w-[200px]">
                        <select
                          value={state.workId}
                          onChange={(e) =>
                            updateRowState(file.id, {
                              workId: e.target.value ? Number(e.target.value) : '',
                            })
                          }
                          className="arr-input !h-[30px] !py-0.5 !text-[12px] !w-full"
                        >
                          <option value="">— Select Target Work —</option>
                          {sortedWorks.map((w) => (
                            <option key={w.id} value={w.id}>
                              [{w.workKind}] {w.title} {w.year ? `(${w.year})` : ''}
                            </option>
                          ))}
                        </select>
                      </td>

                      <td className="w-36">
                        <div className="flex items-center gap-1.5">
                          <input
                            type="number"
                            min="0"
                            placeholder="S#"
                            title="Season Number (optional)"
                            value={state.seasonNumber}
                            onChange={(e) => updateRowState(file.id, { seasonNumber: e.target.value })}
                            className="arr-input !h-[30px] !py-0.5 !text-[12px] !w-14"
                          />
                          <input
                            placeholder="E1, E2"
                            title="Episode numbers (comma separated, optional)"
                            value={state.episodeNumbers}
                            onChange={(e) => updateRowState(file.id, { episodeNumbers: e.target.value })}
                            className="arr-input !h-[30px] !py-0.5 !text-[12px] !w-20"
                          />
                        </div>
                      </td>

                      <td className="w-24">
                        <label className="inline-flex items-center gap-1.5 text-[11px] text-[#cccccc] cursor-pointer select-none">
                          <input
                            type="checkbox"
                            checked={state.renameToTemplate}
                            onChange={(e) => updateRowState(file.id, { renameToTemplate: e.target.checked })}
                            className="rounded border-[#4a5158] bg-[#1e1e1e] text-arr-accent focus:ring-0 cursor-pointer"
                          />
                          <span>Rename</span>
                        </label>
                      </td>

                      <td className="text-right whitespace-nowrap">
                        <div className="inline-flex items-center gap-1.5">
                          <ArrButton
                            variant="success"
                            className="!h-[28px] !px-2.5 !text-[11px]"
                            disabled={!canWrite || !state.workId || isBusy}
                            busy={isBusy}
                            onClick={() => mapSingleMutation.mutate({ file, state })}
                          >
                            <Upload className="h-3 w-3" /> Map &amp; Import
                          </ArrButton>
                          <ArrButton
                            variant="default"
                            className="!h-[28px] !px-2 !text-[11px] text-[#f05050] hover:text-white hover:bg-[#f05050]"
                            disabled={!canWrite || isDismissBusy}
                            busy={isDismissBusy}
                            onClick={() => dismissMutation.mutate(file.id)}
                            title="Dismiss unmapped file"
                          >
                            <Trash2 className="h-3 w-3" />
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
      </div>
    </FieldSet>
  );
}

/* ========================================================================= */
/*  3. NAMING TEMPLATES & TOKEN EDITOR PANEL                                 */
/* ========================================================================= */

export interface NamingTemplatesEditorPanelProps {
  canWrite: boolean;
}

export function NamingTemplatesEditorPanel({ canWrite }: NamingTemplatesEditorPanelProps) {
  const qc = useQueryClient();
  const [activeVertical, setActiveVertical] = useState<string>('tv');
  const [renameMediaFiles, setRenameMediaFiles] = useState<boolean>(true);
  const [useHardlinks, setUseHardlinks] = useState<boolean>(false);
  const [colonReplacement, setColonReplacement] = useState<'delete' | 'dash' | 'space_dash' | 'space'>('space_dash');
  const [templates, setTemplates] = useState<Record<string, string>>({});
  const [previewSamplePath, setPreviewSamplePath] = useState<string>('');
  const [previewLoading, setPreviewLoading] = useState<boolean>(false);
  const [saveBanner, setSaveBanner] = useState<{ kind: 'success' | 'danger'; text: string } | null>(null);

  const inputRef = useRef<HTMLInputElement>(null);

  const namingQuery = useQuery({
    queryKey: ['settings-naming'],
    queryFn: async () => {
      return apiFetch<NamingConfigDto>('/api/v1/settings/naming');
    },
  });

  // Synchronize state when query data arrives
  useEffect(() => {
    if (namingQuery.data) {
      setRenameMediaFiles(namingQuery.data.renameMediaFiles ?? true);
      setUseHardlinks(namingQuery.data.useHardlinks ?? false);
      setColonReplacement(namingQuery.data.colonReplacement ?? 'space_dash');

      const initialTemplates: Record<string, string> = {};
      for (const [key, meta] of Object.entries(NAMING_VERTICAL_DEFAULTS)) {
        initialTemplates[key] = namingQuery.data.templates?.[key] || meta.defaultTemplate;
      }
      setTemplates(initialTemplates);
    } else if (!namingQuery.isLoading && !namingQuery.data) {
      // Initialize with defaults if empty
      const initialTemplates: Record<string, string> = {};
      for (const [key, meta] of Object.entries(NAMING_VERTICAL_DEFAULTS)) {
        initialTemplates[key] = meta.defaultTemplate;
      }
      setTemplates(initialTemplates);
    }
  }, [namingQuery.data, namingQuery.isLoading]);

  // Combined available tokens (API + defaults)
  const allTokens = useMemo(() => {
    const list = [...DEFAULT_STANDARD_TOKENS];
    if (namingQuery.data?.availableTokens) {
      for (const tokenItem of namingQuery.data.availableTokens) {
        if (!list.some((t) => t.token.toLowerCase() === tokenItem.token.toLowerCase())) {
          list.push(tokenItem);
        }
      }
    }
    return list;
  }, [namingQuery.data?.availableTokens]);

  const currentTemplate = templates[activeVertical] ?? NAMING_VERTICAL_DEFAULTS[activeVertical]?.defaultTemplate ?? '';

  // Live preview query
  useEffect(() => {
    let active = true;
    const timer = setTimeout(async () => {
      if (!currentTemplate) return;
      setPreviewLoading(true);
      try {
        const resp = await apiFetch<{
          sampleRelativePath?: string;
          previewPath?: string;
          samplePath?: string;
          path?: string;
        }>(
          '/api/v1/settings/naming/preview',
          {
            method: 'POST',
            body: JSON.stringify({
              vertical: activeVertical,
              template: currentTemplate,
              colonReplacement,
            }),
          },
        );
        if (active) {
          setPreviewSamplePath(
            resp.sampleRelativePath ?? resp.previewPath ?? resp.samplePath ?? resp.path ?? '',
          );
        }
      } catch {
        // Fallback simulated preview if backend preview endpoint is still starting
        if (active) {
          const simulated = currentTemplate
            .replace('{Series TitleYear}', 'Breaking Bad (2008)')
            .replace('{Series Title}', 'Breaking Bad')
            .replace('{Movie TitleYear}', 'Dune Part Two (2024)')
            .replace('{Movie Title}', 'Dune: Part Two')
            .replace('{Artist Name}', 'Daft Punk')
            .replace('{Author Name}', 'Andy Weir')
            .replace('{Album Title}', 'Random Access Memories')
            .replace('{Track:00}', '01')
            .replace('{Track Title}', 'Give Life Back to Music')
            .replace('{Book Title}', 'Project Hail Mary')
            .replace('{Comic Title}', 'Saga Compendium One')
            .replace('{Issue:000}', '012')
            .replace('{Issue:00}', '12')
            .replace('{Release Year}', '2024')
            .replace('{Season:00}', '01')
            .replace('{Episode:00}', '01')
            .replace('{Absolute:000}', '013')
            .replace('{Episode Title}', 'Pilot')
            .replace('{Quality Full}', 'WEBDL-1080p')
            .replace('{Quality Title}', 'WEBDL-1080p')
            .replace('{MediaInfo VideoCodec}', 'x264')
            .replace('{MediaInfo AudioCodec}', 'EAC3')
            .replace('{MediaInfo AudioLanguages}', '[DE+EN]')
            .replace('{MediaInfo VideoDynamicRangeType}', 'DV')
            .replace('{Custom Formats}', 'HDR')
            .replace('{Edition Tags}', 'IMAX')
            .replace('{Release Group}', 'NTb')
            .replace('{imdbId}', 'tt0903747')
            .replace('{tmdbId}', '1396')
            .replace('{tvdbId}', '81189');
          setPreviewSamplePath(simulated);
        }
      } finally {
        if (active) setPreviewLoading(false);
      }
    }, 280);

    return () => {
      active = false;
      clearTimeout(timer);
    };
  }, [activeVertical, currentTemplate, colonReplacement]);

  const insertToken = (tokenStr: string) => {
    const el = inputRef.current;
    if (el) {
      const start = el.selectionStart ?? el.value.length;
      const end = el.selectionEnd ?? el.value.length;
      const prevVal = currentTemplate;
      const newVal = prevVal.substring(0, start) + tokenStr + prevVal.substring(end);
      setTemplates((prev) => ({ ...prev, [activeVertical]: newVal }));
      setTimeout(() => {
        el.focus();
        el.setSelectionRange(start + tokenStr.length, start + tokenStr.length);
      }, 0);
    } else {
      setTemplates((prev) => ({
        ...prev,
        [activeVertical]: (prev[activeVertical] || '') + tokenStr,
      }));
    }
  };

  const saveMutation = useMutation({
    mutationFn: async () => {
      return apiFetch<void>('/api/v1/settings/naming', {
        method: 'PUT',
        body: JSON.stringify({
          renameMediaFiles,
          useHardlinks,
          colonReplacement,
          templates,
        }),
      });
    },
    onSuccess: () => {
      setSaveBanner({
        kind: 'success',
        text: 'Naming templates and replacement rules saved successfully.',
      });
      qc.invalidateQueries({ queryKey: ['settings-naming'] });
      setTimeout(() => setSaveBanner(null), 3000);
    },
    onError: (err: Error) => {
      setSaveBanner({
        kind: 'danger',
        text: `Failed to save naming settings: ${err.message}`,
      });
    },
  });

  return (
    <FieldSet legend="Naming Templates & Token Rules">
      <div className="space-y-6">
        {saveBanner && (
          <ArrAlert kind={saveBanner.kind}>
            <div className="flex items-center justify-between gap-2">
              <span>{saveBanner.text}</span>
              <button
                type="button"
                onClick={() => setSaveBanner(null)}
                className="opacity-75 hover:opacity-100 cursor-pointer"
              >
                <X className="h-4 w-4" />
              </button>
            </div>
          </ArrAlert>
        )}

        {/* Global Naming Options */}
        <div className="bg-arr-heading border border-arr-card-border rounded-[4px] p-4 space-y-4">
          <FormGroup
            label="Rename Media Files"
            helpText="Automatically rename incoming media files to standardized formats on import and scan."
          >
            <label className="inline-flex items-center gap-2 cursor-pointer select-none">
              <input
                type="checkbox"
                checked={renameMediaFiles}
                disabled={!canWrite}
                onChange={(e) => setRenameMediaFiles(e.target.checked)}
                className="rounded border-[#4a5158] bg-[#1e1e1e] text-arr-accent focus:ring-0 cursor-pointer"
              />
              <span className="text-[13px] text-[#eeeeee]">Enable Media File Renaming</span>
            </label>
          </FormGroup>

          <FormGroup
            label="Import Transfer Mode"
            helpText="Move completed downloads directly into the library by default, or create hardlinks instead of moving."
          >
            <select
              aria-label="Import Transfer Mode"
              value={useHardlinks ? 'hardlink' : 'move'}
              disabled={!canWrite}
              onChange={(e) => setUseHardlinks(e.target.value === 'hardlink')}
              className="arr-input !max-w-xs"
            >
              <option value="move">Move Files on Import (Default)</option>
              <option value="hardlink">Create Hardlinks Instead of Moving</option>
            </select>
          </FormGroup>

          <FormGroup
            label="Colon Replacement"
            helpText="Determines how colons (:) in media titles are replaced on filesystems that forbid them."
          >
            <select
              value={colonReplacement}
              disabled={!canWrite}
              onChange={(e) =>
                setColonReplacement(e.target.value as 'delete' | 'dash' | 'space_dash' | 'space')
              }
              className="arr-input !max-w-xs"
            >
              <option value="delete">Delete (eg. Dune Part Two)</option>
              <option value="dash">Dash (-) (eg. Dune-Part Two)</option>
              <option value="space_dash">Space Dash ( - ) (eg. Dune - Part Two)</option>
              <option value="space">Space (eg. Dune  Part Two)</option>
            </select>
          </FormGroup>
        </div>

        {/* Per-Vertical Template Tabs & Editor */}
        <div className="bg-arr-heading border border-arr-card-border rounded-[4px] p-4 space-y-4">
          <div>
            <div className="text-[14px] font-semibold text-[#eeeeee] mb-2">Vertical Naming Templates</div>
            <div className="flex flex-wrap items-center gap-1.5 border-b border-arr-card-border pb-2">
              {Object.entries(NAMING_VERTICAL_DEFAULTS).map(([key, meta]) => {
                const active = activeVertical === key;
                return (
                  <button
                    key={key}
                    type="button"
                    onClick={() => setActiveVertical(key)}
                    className={clsx(
                      'px-3 py-1 text-[12px] rounded-[3px] border transition-colors cursor-pointer',
                      active
                        ? 'bg-arr-accent text-arr-accent-fg border-arr-accent font-semibold'
                        : 'bg-arr-card text-[#cccccc] border-[#3d4349] hover:border-arr-accent hover:text-white',
                    )}
                  >
                    {meta.label}
                  </button>
                );
              })}
            </div>
          </div>

          <div>
            <label className="block text-[12px] font-medium text-[#cccccc] mb-1">
              Template Pattern for {NAMING_VERTICAL_DEFAULTS[activeVertical]?.label}
            </label>
            <input
              ref={inputRef}
              value={currentTemplate}
              disabled={!canWrite}
              onChange={(e) =>
                setTemplates((prev) => ({
                  ...prev,
                  [activeVertical]: e.target.value,
                }))
              }
              className="arr-input font-mono !text-[13px]"
              placeholder="e.g. {Series TitleYear}/Season {Season:00}/{Series TitleYear} - S{Season:00}E{Episode:00} - {Episode Title}"
            />
          </div>

          {/* Clickable Token Pills */}
          <div>
            <div className="flex items-center justify-between mb-1.5">
              <span className="text-[12px] font-medium text-[#909293]">Available Token Pills (Click to Insert):</span>
              <button
                type="button"
                onClick={() =>
                  setTemplates((prev) => ({
                    ...prev,
                    [activeVertical]: NAMING_VERTICAL_DEFAULTS[activeVertical]?.defaultTemplate ?? '',
                  }))
                }
                className="text-[11px] text-arr-accent hover:underline cursor-pointer"
              >
                Reset to Default
              </button>
            </div>
            <div className="flex flex-wrap gap-1.5 p-2 bg-[#1b1b1b] border border-arr-card-border rounded-[3px] max-h-36 overflow-y-auto">
              {allTokens.map((t) => (
                <button
                  key={t.token}
                  type="button"
                  title={`${t.description} (e.g. ${t.example})`}
                  onClick={() => insertToken(t.token)}
                  disabled={!canWrite}
                  className="px-2 py-0.5 rounded-[3px] bg-[#2d3238] border border-[#444c54] hover:border-arr-accent hover:bg-arr-accent/15 text-[#eeeeee] hover:text-arr-accent font-mono text-[11px] transition-colors cursor-pointer"
                >
                  {t.token}
                </button>
              ))}
            </div>
          </div>

          {/* Live Sample Preview */}
          <div className="bg-[#1a1a1a] border border-arr-card-border rounded-[4px] p-3">
            <div className="flex items-center justify-between text-[11px] text-[#888888] mb-1">
              <span className="inline-flex items-center gap-1 font-semibold text-arr-accent">
                <Sparkles className="h-3 w-3" /> Live Sample Output Path:
              </span>
              {previewLoading && <Loader2 className="h-3 w-3 animate-spin text-arr-accent" />}
            </div>
            <div className="font-mono text-[12px] text-[#27c24c] break-all bg-[#121212] p-2 rounded border border-arr-card-border">
              {previewSamplePath || 'No preview generated.'}
            </div>
          </div>

          {/* Save Button */}
          {canWrite && (
            <div className="pt-2 flex justify-end">
              <ArrButton
                variant="success"
                busy={saveMutation.isPending}
                disabled={saveMutation.isPending}
                onClick={() => saveMutation.mutate()}
              >
                <Check className="h-4 w-4" /> Save Naming Settings
              </ArrButton>
            </div>
          )}
        </div>
      </div>
    </FieldSet>
  );
}

/* ========================================================================= */
/*  4. DISK SCAN REPORT BANNER                                               */
/* ========================================================================= */

export function DiskScanReportBanner({
  libraryName,
  report,
  onDismiss,
}: {
  libraryName: string;
  report: DiskScanReportDto;
  onDismiss: () => void;
}) {
  return (
    <ArrAlert kind="info">
      <div className="flex items-start justify-between gap-3">
        <div className="space-y-1">
          <div className="font-semibold text-white flex items-center gap-1.5">
            <CheckCircle2 className="h-4 w-4 text-[#27c24c]" />
            Disk Scan Finished for &ldquo;{libraryName}&rdquo;
          </div>
          <div className="text-[12px] text-[#dddddd] flex flex-wrap items-center gap-3">
            <span>
              Scanned: <strong>{report.scannedFilesCount}</strong>
            </span>
            <span>•</span>
            <span>
              Auto-Adopted: <strong className="text-[#27c24c]">{report.autoAdoptedCount}</strong>
            </span>
            <span>•</span>
            <span>
              Missing Removed: <strong className="text-[#f05050]">{report.missingRemovedCount}</strong>
            </span>
            <span>•</span>
            <span>
              Unmapped Discovered: <strong className="text-[#ffc230]">{report.unmappedCount}</strong>
            </span>
          </div>
          {report.messages && report.messages.length > 0 && (
            <div className="pt-1 text-[11px] text-[#b8b8b8] max-h-24 overflow-y-auto space-y-0.5">
              {report.messages.map((msg, i) => (
                <div key={i} className="font-mono">
                  {msg}
                </div>
              ))}
            </div>
          )}
        </div>
        <button
          type="button"
          onClick={onDismiss}
          className="opacity-75 hover:opacity-100 cursor-pointer text-current"
          aria-label="Dismiss scan banner"
        >
          <X className="h-4 w-4" />
        </button>
      </div>
    </ArrAlert>
  );
}

/* ========================================================================= */
/*  5. COMPOSITE MEDIA MANAGEMENT TOOLS PANEL                                */
/* ========================================================================= */

export function MediaManagementToolsPanel({
  libraries,
  works,
  canWrite,
}: {
  libraries: Array<{ id: number; name: string; mediaType: string }>;
  works: Array<{ id: number; libraryId: number; title: string; year: number | null; workKind: string }>;
  canWrite: boolean;
}) {
  return (
    <div className="space-y-7 mt-7">
      <UnmappedFilesPanel libraries={libraries} works={works} canWrite={canWrite} />
      <NamingTemplatesEditorPanel canWrite={canWrite} />
    </div>
  );
}
