import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  AlertTriangle,
  ChevronRight,
  CornerLeftUp,
  Download,
  File,
  Film,
  Folder,
  FolderOpen,
  HardDrive,
  Loader2,
  Plus,
  Search,
} from 'lucide-react';
import clsx from 'clsx';
import { apiFetch, formatBytes } from '../../lib/api';
import { ArrButton, ArrModal } from './ArrPrimitives';

export interface FilesystemQuickRoot {
  name: string;
  path: string;
  kind: 'media' | 'downloads' | 'library' | 'mount';
}

export interface FilesystemDirectoryEntry {
  type: 'folder';
  name: string;
  path: string;
  lastModified: string | null;
}

export interface FilesystemFileEntry {
  type: 'file';
  name: string;
  path: string;
  extension: string;
  size: number;
  lastModified: string | null;
}

export interface FilesystemBrowseResponse {
  currentPath: string;
  parent: string | null;
  quickRoots: FilesystemQuickRoot[];
  directories: FilesystemDirectoryEntry[];
  files: FilesystemFileEntry[];
  truncated: boolean;
}

export interface PathBrowserModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSelect: (selectedPath: string) => void;
  initialPath?: string;
  title?: string;
  includeFiles?: boolean;
  allowFileSelection?: boolean;
  allowCreateFolder?: boolean;
}

function parseBreadcrumbs(fullPath: string): Array<{ name: string; path: string }> {
  if (!fullPath) return [{ name: '/', path: '/' }];
  const isWindows = /^[a-zA-Z]:/.test(fullPath);
  const sep = isWindows && fullPath.includes('\\') ? '\\' : '/';
  const parts = fullPath.split(/[/\\]/).filter(Boolean);

  const crumbs: Array<{ name: string; path: string }> = [];
  if (!isWindows) {
    crumbs.push({ name: '/', path: '/' });
    let accumulated = '';
    for (const part of parts) {
      accumulated = `${accumulated}/${part}`;
      crumbs.push({ name: part, path: accumulated });
    }
  } else {
    const rootDrive = fullPath.slice(0, 2) + sep;
    crumbs.push({ name: fullPath.slice(0, 2), path: rootDrive });
    let accumulated = fullPath.slice(0, 2);
    for (const part of parts.slice(1)) {
      accumulated = `${accumulated}${sep}${part}`;
      crumbs.push({ name: part, path: accumulated });
    }
  }
  return crumbs;
}

function formatDate(iso: string | null): string {
  if (!iso) return '-';
  try {
    const d = new Date(iso);
    if (isNaN(d.getTime())) return '-';
    return d.toLocaleDateString(undefined, {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
    });
  } catch {
    return '-';
  }
}

function renderQuickRootIcon(kind: FilesystemQuickRoot['kind']) {
  switch (kind) {
    case 'media':
      return <Film className="h-3.5 w-3.5 text-arr-accent shrink-0" aria-hidden="true" />;
    case 'downloads':
      return <Download className="h-3.5 w-3.5 text-[#23b7e5] shrink-0" aria-hidden="true" />;
    case 'mount':
      return <HardDrive className="h-3.5 w-3.5 text-[#ffa500] shrink-0" aria-hidden="true" />;
    case 'library':
    default:
      return <Folder className="h-3.5 w-3.5 text-[#27c24c] shrink-0" aria-hidden="true" />;
  }
}

export function PathBrowserModal({
  isOpen,
  onClose,
  onSelect,
  initialPath = '',
  title,
  includeFiles = false,
  allowFileSelection = false,
  allowCreateFolder = true,
}: PathBrowserModalProps) {
  const qc = useQueryClient();
  const pathInputId = useId();
  const tableCaptionId = useId();

  const [browsePath, setBrowsePath] = useState(initialPath.trim() || '/');
  const [typedPath, setTypedPath] = useState(browsePath);
  const [filterText, setFilterText] = useState('');
  const [selectedFilePath, setSelectedFilePath] = useState<string | null>(null);

  const [showNewFolder, setShowNewFolder] = useState(false);
  const [newFolderName, setNewFolderName] = useState('');
  const [createFolderError, setCreateFolderError] = useState<string | null>(null);
  const newFolderInputRef = useRef<HTMLInputElement | null>(null);

  // Sync initial path when modal opens
  useEffect(() => {
    if (isOpen) {
      const starting = initialPath.trim() || '/';
      setBrowsePath(starting);
      setTypedPath(starting);
      setFilterText('');
      setSelectedFilePath(null);
      setShowNewFolder(false);
      setNewFolderName('');
      setCreateFolderError(null);
    }
  }, [isOpen, initialPath]);

  const { data, isLoading, isError, error, refetch } = useQuery<FilesystemBrowseResponse>({
    queryKey: ['filesystem', browsePath, includeFiles],
    queryFn: () =>
      apiFetch<FilesystemBrowseResponse>(
        `/api/v1/filesystem?path=${encodeURIComponent(browsePath)}&includeFiles=${
          includeFiles ? 'true' : 'false'
        }`,
      ),
    enabled: isOpen,
  });

  // When data loads, sync typed path if different
  useEffect(() => {
    if (data?.currentPath) {
      setTypedPath(data.currentPath);
    }
  }, [data?.currentPath]);

  // Focus new folder input when revealed
  useEffect(() => {
    if (showNewFolder) {
      newFolderInputRef.current?.focus();
    }
  }, [showNewFolder]);

  const createDirMutation = useMutation({
    mutationFn: (targetPath: string) =>
      apiFetch<{ success: boolean; path: string }>('/api/v1/filesystem/directories', {
        method: 'POST',
        body: JSON.stringify({ path: targetPath }),
      }),
    onSuccess: (res, targetPath) => {
      const createdPath = res?.path || targetPath;
      setShowNewFolder(false);
      setNewFolderName('');
      setCreateFolderError(null);
      setFilterText('');
      setBrowsePath(createdPath);
      setTypedPath(createdPath);
      void qc.invalidateQueries({ queryKey: ['filesystem'] });
    },
    onError: (err: unknown) => {
      setCreateFolderError(err instanceof Error ? err.message : 'Failed to create directory');
    },
  });

  const navigateTo = (newPath: string) => {
    const trimmed = newPath.trim();
    if (!trimmed) return;
    setBrowsePath(trimmed);
    setTypedPath(trimmed);
    setSelectedFilePath(null);
    setShowNewFolder(false);
    setCreateFolderError(null);
    setFilterText('');
  };

  const handleCreateFolder = () => {
    const trimmedName = newFolderName.trim();
    if (!trimmedName) return;

    const current = data?.currentPath || browsePath;
    const isWindows = /^[a-zA-Z]:/.test(current);
    const sep = isWindows && current.includes('\\') ? '\\' : '/';
    const targetPath =
      current === '/' || current.endsWith(sep)
        ? `${current}${trimmedName}`
        : `${current}${sep}${trimmedName}`;

    createDirMutation.mutate(targetPath);
  };

  const handleFileClick = (filePath: string) => {
    if (!allowFileSelection) return;
    setSelectedFilePath((prev) => (prev === filePath ? null : filePath));
  };

  const activePath = data?.currentPath || browsePath;
  const breadcrumbs = useMemo(() => parseBreadcrumbs(activePath), [activePath]);

  const filteredDirectories = useMemo(() => {
    if (!data?.directories) return [];
    if (!filterText.trim()) return data.directories;
    const term = filterText.toLowerCase();
    return data.directories.filter((d) => d.name.toLowerCase().includes(term));
  }, [data?.directories, filterText]);

  const filteredFiles = useMemo(() => {
    if (!data?.files || !includeFiles) return [];
    if (!filterText.trim()) return data.files;
    const term = filterText.toLowerCase();
    return data.files.filter((f) => f.name.toLowerCase().includes(term));
  }, [data?.files, filterText, includeFiles]);

  const chosenPath = selectedFilePath ?? activePath;
  const isFolderSelection = !selectedFilePath;
  const isSelectDisabled =
    isLoading ||
    isError ||
    !chosenPath ||
    chosenPath.trim() === '' ||
    (isFolderSelection && (chosenPath === '/' || chosenPath.trim() === ''));

  const handleConfirm = () => {
    if (isSelectDisabled) return;
    onSelect(chosenPath);
    onClose();
  };

  if (!isOpen) return null;

  const defaultTitle = includeFiles
    ? allowFileSelection
      ? 'Select File or Folder'
      : 'Browse Filesystem'
    : 'Select Folder';

  return (
    <ArrModal
      title={title ?? defaultTitle}
      onClose={onClose}
      maxWidth="max-w-3xl"
      bodyClassName="p-4 sm:p-5 flex flex-col gap-3"
      footer={
        <>
          <div className="flex-1 min-w-0 mr-3 text-left">
            <span className="text-[11px] text-[#909293] uppercase font-semibold mr-1.5">
              Selected:
            </span>
            <span className="font-mono text-[12px] text-[#e1e2e3] select-all break-all">
              {chosenPath || '(none)'}
            </span>
          </div>
          <ArrButton type="button" onClick={onClose}>
            Cancel
          </ArrButton>
          <ArrButton
            variant="primary"
            type="button"
            onClick={handleConfirm}
            disabled={isSelectDisabled}
          >
            {allowFileSelection
              ? selectedFilePath
                ? 'Select File'
                : 'Select Path'
              : 'Select Folder'}
          </ArrButton>
        </>
      }
    >
      {/* Path Input & Up Button */}
      <div className="flex items-center gap-2">
        <label
          htmlFor={pathInputId}
          className="text-[12px] font-bold text-[#e1e2e3] shrink-0"
        >
          Path
        </label>
        <input
          id={pathInputId}
          type="text"
          value={typedPath}
          onChange={(e) => setTypedPath(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              navigateTo(typedPath);
            }
          }}
          className="arr-input font-mono text-[12.5px] flex-1 min-w-0"
          placeholder="/media or /downloads"
        />
        <ArrButton
          type="button"
          onClick={() => navigateTo(typedPath)}
          title="Go to path"
          className="px-3 shrink-0"
        >
          Go
        </ArrButton>
        <ArrButton
          type="button"
          onClick={() => data?.parent && navigateTo(data.parent)}
          disabled={!data?.parent}
          title="Up to parent directory"
          aria-label="Up to parent directory"
          className="px-3 shrink-0"
        >
          <CornerLeftUp className="h-4 w-4" aria-hidden="true" />
        </ArrButton>
      </div>

      {/* Quick Locations */}
      {data?.quickRoots && data.quickRoots.length > 0 && (
        <div className="flex flex-wrap items-center gap-1.5 pt-0.5">
          <span className="text-[11px] font-semibold text-[#909293] uppercase tracking-wider mr-1">
            Quick:
          </span>
          {data.quickRoots.map((root) => {
            const isCurrent = activePath === root.path;
            return (
              <button
                key={root.path}
                type="button"
                onClick={() => navigateTo(root.path)}
                className={clsx(
                  'inline-flex items-center gap-1.5 px-2 py-0.5 text-[11px] rounded-[3px] border transition-colors cursor-pointer',
                  isCurrent
                    ? 'bg-arr-accent/20 border-arr-accent text-white font-medium shadow-[0_0_0_1px_var(--arr-accent)]'
                    : 'bg-[#222c2c] hover:bg-[#2c3838] border-arr-card-border text-[#cccccc] hover:text-[#eeeeee]',
                )}
                title={root.path}
              >
                {renderQuickRootIcon(root.kind)}
                <span>{root.name}</span>
              </button>
            );
          })}
        </div>
      )}

      {/* Clickable Breadcrumbs */}
      <nav
        aria-label="Directory breadcrumbs"
        className="flex items-center flex-wrap gap-1 px-3 py-1.5 bg-arr-toolbar border border-arr-card-border rounded-[4px] text-[12px] font-mono text-[#cccccc]"
      >
        {breadcrumbs.map((crumb, idx) => {
          const isLast = idx === breadcrumbs.length - 1;
          return (
            <div key={crumb.path} className="inline-flex items-center gap-1">
              {idx > 0 && (
                <ChevronRight className="h-3 w-3 text-[#666666] shrink-0" aria-hidden="true" />
              )}
              {isLast ? (
                <span className="text-white font-semibold">{crumb.name}</span>
              ) : (
                <button
                  type="button"
                  onClick={() => navigateTo(crumb.path)}
                  className="hover:text-arr-accent hover:underline text-[#cccccc] cursor-pointer"
                >
                  {crumb.name}
                </button>
              )}
            </div>
          );
        })}
      </nav>

      {/* Filter and New Folder Toolbar */}
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="relative flex-1 min-w-[200px] max-w-sm">
          <Search
            className="absolute left-2.5 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-[#888888] pointer-events-none"
            aria-hidden="true"
          />
          <input
            type="text"
            value={filterText}
            onChange={(e) => setFilterText(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.preventDefault();
              }
            }}
            placeholder="Filter current folder..."
            className="arr-input text-xs pl-8 w-full"
            aria-label="Filter current folder contents"
          />
        </div>

        {allowCreateFolder && !isLoading && !isError && activePath !== '/' && (
          <div className="shrink-0">
            {!showNewFolder ? (
              <ArrButton
                type="button"
                onClick={() => {
                  setShowNewFolder(true);
                  setCreateFolderError(null);
                }}
                className="text-xs h-[30px] px-2.5 gap-1"
              >
                <Plus className="h-3.5 w-3.5" aria-hidden="true" />
                <span>+ New Folder</span>
              </ArrButton>
            ) : (
              <div className="flex items-center gap-1.5">
                <input
                  ref={newFolderInputRef}
                  type="text"
                  value={newFolderName}
                  onChange={(e) => setNewFolderName(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') {
                      e.preventDefault();
                      handleCreateFolder();
                    } else if (e.key === 'Escape') {
                      setShowNewFolder(false);
                      setNewFolderName('');
                    }
                  }}
                  placeholder="New folder name"
                  className="arr-input font-mono text-xs w-44 h-[30px]"
                  aria-label="New folder name"
                />
                <ArrButton
                  variant="primary"
                  type="button"
                  onClick={handleCreateFolder}
                  busy={createDirMutation.isPending}
                  disabled={!newFolderName.trim()}
                  className="text-xs h-[30px] px-2.5"
                >
                  Create
                </ArrButton>
                <ArrButton
                  type="button"
                  onClick={() => {
                    setShowNewFolder(false);
                    setNewFolderName('');
                    setCreateFolderError(null);
                  }}
                  className="text-xs h-[30px] px-2"
                >
                  Cancel
                </ArrButton>
              </div>
            )}
          </div>
        )}
      </div>

      {createFolderError && (
        <div className="text-[12px] text-[#f05050] bg-[#f05050]/10 border border-[#f05050]/30 rounded px-2.5 py-1">
          {createFolderError}
        </div>
      )}

      {/* Directory Contents Table */}
      <div
        tabIndex={0}
        role="region"
        aria-labelledby={tableCaptionId}
        className="overflow-auto max-h-[44vh] border border-arr-card-border rounded-[4px] bg-arr-input [container-type:inline-size]"
      >
        <table className="w-full text-left border-collapse text-[12.5px]">
          <caption id={tableCaptionId} className="sr-only">
            Directory contents
          </caption>
          <thead className="sticky top-0 z-10 bg-arr-toolbar">
            <tr className="border-b border-arr-card-border">
              <th
                scope="col"
                className="px-3 py-2 text-[#909293] font-semibold text-[11px] uppercase tracking-wider"
              >
                Name
              </th>
              <th
                scope="col"
                className="px-3 py-2 text-[#909293] font-semibold text-[11px] uppercase tracking-wider w-[100px]"
              >
                Type
              </th>
              {includeFiles && (
                <th
                  scope="col"
                  className="px-3 py-2 text-[#909293] font-semibold text-[11px] uppercase tracking-wider w-[100px] text-right"
                >
                  Size
                </th>
              )}
              <th
                scope="col"
                className="px-3 py-2 text-[#909293] font-semibold text-[11px] uppercase tracking-wider w-[130px] text-right"
              >
                Modified
              </th>
            </tr>
          </thead>
          <tbody>
            {isLoading ? (
              <tr>
                <td
                  colSpan={includeFiles ? 4 : 3}
                  className="px-4 py-8 text-center text-[#909293]"
                >
                  <Loader2 className="h-5 w-5 animate-spin mx-auto mb-2 text-arr-accent" />
                  <span>Loading directory contents...</span>
                </td>
              </tr>
            ) : isError ? (
              <tr>
                <td
                  colSpan={includeFiles ? 4 : 3}
                  className="px-4 py-6 text-center text-[#f05050]"
                >
                  <div className="flex flex-col items-center justify-center gap-2">
                    <span>{(error as Error)?.message || 'Failed to load directory.'}</span>
                    <div className="flex items-center justify-center gap-2 mt-1">
                      <ArrButton
                        type="button"
                        onClick={() => void refetch()}
                        className="text-xs h-7 px-2.5"
                      >
                        Retry
                      </ArrButton>
                      <ArrButton
                        type="button"
                        onClick={() => navigateTo('/')}
                        className="text-xs h-7 px-2.5 text-arr-accent font-semibold"
                      >
                        Go to /
                      </ArrButton>
                    </div>
                  </div>
                </td>
              </tr>
            ) : (
              <>
                {/* Parent Row */}
                {data?.parent !== null && data?.parent !== undefined && (
                  <tr className="hover:bg-white/5 border-b border-arr-card-border/40 transition-colors">
                    <td className="px-3 py-2 font-mono">
                      <button
                        type="button"
                        onClick={() => navigateTo(data.parent!)}
                        className="flex items-center gap-2 text-arr-accent hover:underline text-left w-full cursor-pointer"
                      >
                        <CornerLeftUp
                          className="h-4 w-4 shrink-0 text-arr-accent"
                          aria-hidden="true"
                        />
                        <span className="font-semibold">..</span>
                        <span className="text-[11px] text-[#909293] font-sans">
                          (Parent Directory)
                        </span>
                      </button>
                    </td>
                    <td className="px-3 py-2 text-[#909293] text-[11px]">Folder</td>
                    {includeFiles && (
                      <td className="px-3 py-2 text-[#909293] text-right text-[11px]">-</td>
                    )}
                    <td className="px-3 py-2 text-[#909293] text-right text-[11px]">-</td>
                  </tr>
                )}

                {/* Directory Rows */}
                {filteredDirectories.map((dir) => (
                  <tr
                    key={dir.path}
                    className="hover:bg-white/5 border-b border-arr-card-border/40 transition-colors"
                  >
                    <td className="px-3 py-2">
                      <button
                        type="button"
                        onClick={() => navigateTo(dir.path)}
                        className="flex items-center gap-2 text-left w-full text-[#e1e2e3] hover:text-arr-accent cursor-pointer group"
                      >
                        <Folder
                          className="h-4 w-4 text-arr-accent shrink-0 group-hover:scale-105 transition-transform"
                          aria-hidden="true"
                        />
                        <span className="font-mono text-[12px] group-hover:underline truncate">
                          {dir.name}
                        </span>
                      </button>
                    </td>
                    <td className="px-3 py-2 text-[#909293] text-[11px]">Folder</td>
                    {includeFiles && (
                      <td className="px-3 py-2 text-[#909293] text-right text-[11px]">-</td>
                    )}
                    <td className="px-3 py-2 text-[#909293] text-right text-[11px] font-mono">
                      {formatDate(dir.lastModified)}
                    </td>
                  </tr>
                ))}

                {/* File Rows */}
                {includeFiles &&
                  filteredFiles.map((file) => {
                    const isSelected = selectedFilePath === file.path;
                    return (
                      <tr
                        key={file.path}
                        className={clsx(
                          'hover:bg-white/5 border-b border-arr-card-border/40 transition-colors',
                          isSelected && 'bg-arr-accent/20 text-white font-medium',
                        )}
                      >
                        <td className="px-3 py-2">
                          <button
                            type="button"
                            onClick={() => handleFileClick(file.path)}
                            disabled={!allowFileSelection}
                            className={clsx(
                              'flex items-center gap-2 text-left w-full text-[#e1e2e3]',
                              allowFileSelection
                                ? 'hover:text-arr-accent cursor-pointer group'
                                : 'cursor-default opacity-85',
                            )}
                          >
                            <File
                              className={clsx(
                                'h-4 w-4 shrink-0',
                                isSelected ? 'text-arr-accent' : 'text-[#909293]',
                              )}
                              aria-hidden="true"
                            />
                            <span
                              className={clsx(
                                'font-mono text-[12px] truncate',
                                allowFileSelection && 'group-hover:underline',
                              )}
                            >
                              {file.name}
                            </span>
                          </button>
                        </td>
                        <td className="px-3 py-2 text-[#909293] text-[11px] uppercase">
                          {file.extension.replace(/^\./, '') || 'File'}
                        </td>
                        <td className="px-3 py-2 text-right text-[11px] font-mono text-[#d0d2d4]">
                          {formatBytes(file.size)}
                        </td>
                        <td className="px-3 py-2 text-[#909293] text-right text-[11px] font-mono">
                          {formatDate(file.lastModified)}
                        </td>
                      </tr>
                    );
                  })}

                {filteredDirectories.length === 0 &&
                  (!includeFiles || filteredFiles.length === 0) && (
                    <tr>
                      <td
                        colSpan={includeFiles ? 4 : 3}
                        className="px-4 py-8 text-center text-[#909293] text-[12px]"
                      >
                        {filterText.trim()
                          ? `No entries match "${filterText}"`
                          : 'This folder is empty.'}
                      </td>
                    </tr>
                  )}
              </>
            )}
          </tbody>
        </table>
      </div>

      {data?.truncated && (
        <div className="text-[11px] text-[#ff902b] flex items-center gap-1.5 pt-0.5">
          <AlertTriangle className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
          <span>
            Directory listing contains more items than the server maximum and was truncated.
          </span>
        </div>
      )}
    </ArrModal>
  );
}

export interface PathBrowserInputProps {
  id?: string;
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  label?: string;
  'aria-label'?: string;
  includeFiles?: boolean;
  allowFileSelection?: boolean;
  allowCreateFolder?: boolean;
  modalTitle?: string;
  className?: string;
  disabled?: boolean;
}

export function PathBrowserInput({
  id,
  value,
  onChange,
  placeholder,
  label,
  'aria-label': ariaLabel,
  includeFiles = false,
  allowFileSelection = false,
  allowCreateFolder = true,
  modalTitle,
  className,
  disabled,
}: PathBrowserInputProps) {
  const [open, setOpen] = useState(false);
  const effectiveLabel = ariaLabel || label;

  return (
    <div className="flex items-stretch gap-1.5 w-full">
      <input
        id={id}
        type="text"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        disabled={disabled}
        aria-label={effectiveLabel}
        className={clsx('arr-input font-mono flex-1 min-w-0', className)}
      />
      <ArrButton
        type="button"
        onClick={() => setOpen(true)}
        disabled={disabled}
        aria-label={`Browse ${label || 'path'}`}
        title={`Browse ${label || 'path'}`}
        className="px-2.5 shrink-0"
      >
        <FolderOpen className="h-4 w-4" aria-hidden="true" />
      </ArrButton>
      {open && (
        <PathBrowserModal
          isOpen={open}
          onClose={() => setOpen(false)}
          onSelect={(selectedPath) => {
            onChange(selectedPath);
            setOpen(false);
          }}
          initialPath={value}
          title={modalTitle ?? (includeFiles ? 'Select File or Folder' : 'Select Folder')}
          includeFiles={includeFiles}
          allowFileSelection={allowFileSelection}
          allowCreateFolder={allowCreateFolder}
        />
      )}
    </div>
  );
}
