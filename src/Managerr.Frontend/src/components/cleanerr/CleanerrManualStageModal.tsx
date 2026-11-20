import { useId, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Search, ShieldAlert, Trash2 } from 'lucide-react';
import { apiFetch, formatBytes } from '../../lib/api';
import {
  ArrAlert,
  ArrButton,
  ArrLabel,
  ArrModal,
  FormGroup,
} from '../arr/ArrPrimitives';
import type {
  CleanerrMediaItemDto,
  CleanerrMediaListResponse,
} from './cleanerrTypes';

interface Props {
  onClose: () => void;
  onSuccess: (count: number) => void;
  initialSelectedIds?: number[];
}

function normalizeMediaList(
  data: CleanerrMediaListResponse | CleanerrMediaItemDto[] | undefined,
): CleanerrMediaItemDto[] {
  if (!data) return [];
  return Array.isArray(data) ? data : (data.items ?? []);
}

export function CleanerrManualStageModal({
  onClose,
  onSuccess,
  initialSelectedIds,
}: Props) {
  const qc = useQueryClient();
  const graceDaysId = useId();
  const actionModeId = useId();
  const reasonId = useId();
  const searchId = useId();

  const [search, setSearch] = useState('');
  const [selectedIds, setSelectedIds] = useState<number[]>(
    () => initialSelectedIds ?? [],
  );
  const [graceDays, setGraceDays] = useState(7);
  const [actionMode, setActionMode] = useState('delete_files_and_unmonitor');
  const [reason, setReason] = useState('Manually staged by administrator');

  const mediaQuery = useQuery({
    queryKey: ['cleanerr-media', 'modal-list', 200],
    queryFn: () =>
      apiFetch<CleanerrMediaListResponse | CleanerrMediaItemDto[]>(
        '/api/v1/cleanerr/media?pageSize=200',
      ),
  });

  const allMedia = useMemo(
    () => normalizeMediaList(mediaQuery.data),
    [mediaQuery.data],
  );

  const filteredMedia = useMemo(() => {
    if (!search.trim()) return allMedia.slice(0, 30);
    const q = search.toLowerCase();
    return allMedia
      .filter(
        (m) =>
          m.title.toLowerCase().includes(q) ||
          m.parentTitle?.toLowerCase().includes(q) ||
          m.grandparentTitle?.toLowerCase().includes(q) ||
          m.mediaType.toLowerCase().includes(q) ||
          (m.tags ?? []).some((t) => t.toLowerCase().includes(q)),
      )
      .slice(0, 50);
  }, [allMedia, search]);

  const selectedMediaItems = useMemo(() => {
    const map = new Map(allMedia.map((m) => [m.id, m]));
    return selectedIds
      .map((id) => map.get(id))
      .filter((item): item is CleanerrMediaItemDto => Boolean(item));
  }, [allMedia, selectedIds]);

  const stageMutation = useMutation({
    mutationFn: () =>
      apiFetch<{ stagedCount?: number; count?: number }>(
        '/api/v1/cleanerr/queue/manual-stage',
        {
          method: 'POST',
          body: JSON.stringify({
            mediaItemIds: selectedIds,
            graceDays,
            actionMode,
            reason: reason.trim() || undefined,
          }),
        },
      ),
    onSuccess: (res) => {
      void qc.invalidateQueries({ queryKey: ['cleanerr-queue'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-stats'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-media'] });
      onSuccess(res.stagedCount ?? res.count ?? selectedIds.length);
      onClose();
    },
  });

  const toggleSelect = (id: number) => {
    setSelectedIds((prev) =>
      prev.includes(id) ? prev.filter((i) => i !== id) : [...prev, id],
    );
  };

  return (
    <ArrModal
      title="Manually Stage Media (Leaving Soon)"
      onClose={onClose}
      maxWidth="max-w-4xl"
      footer={
        <>
          <ArrButton variant="default" onClick={onClose}>
            Cancel
          </ArrButton>
          <ArrButton
            variant="warning"
            disabled={selectedIds.length === 0}
            busy={stageMutation.isPending}
            onClick={() => stageMutation.mutate()}
          >
            Stage {selectedIds.length} Item{selectedIds.length === 1 ? '' : 's'}{' '}
            (Leaving Soon)
          </ArrButton>
        </>
      }
    >
      <div className="space-y-4">
        <ArrAlert kind="info">
          <div className="flex items-start gap-2">
            <ShieldAlert
              aria-hidden="true"
              className="h-4 w-4 shrink-0 mt-0.5 text-[var(--arr-accent)]"
            />
            <span className="text-[12px]">
              Staged items enter the grace countdown in the Leaving Soon queue
              and trigger Discord alerts if configured. Excluded items remain
              protected.
            </span>
          </div>
        </ArrAlert>

        {stageMutation.isError && (
          <ArrAlert kind="danger">
            {(stageMutation.error as Error)?.message ??
              'Failed to stage selected media items.'}
          </ArrAlert>
        )}

        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <FormGroup
            label="Grace Countdown (Days)"
            htmlFor={graceDaysId}
            helpText="Days before the action is executed automatically."
          >
            <input
              id={graceDaysId}
              name="graceDays"
              type="number"
              min={1}
              max={365}
              value={graceDays}
              onChange={(e) =>
                setGraceDays(Math.max(1, Number(e.target.value) || 1))
              }
              className="arr-input"
            />
          </FormGroup>

          <FormGroup
            label="Action Mode"
            htmlFor={actionModeId}
            helpText="Action executed when the grace countdown expires."
          >
            <select
              id={actionModeId}
              name="actionMode"
              value={actionMode}
              onChange={(e) => setActionMode(e.target.value)}
              className="arr-input"
            >
              <option value="delete_files_and_unmonitor">
                Delete Files &amp; Unmonitor
              </option>
              <option value="unmonitor_and_delete_existing">
                Unmonitor &amp; Delete Existing
              </option>
              <option value="delete_work_and_files">
                Delete Work &amp; Files
              </option>
              <option value="delete_show_if_empty">
                Delete Show If Empty
              </option>
              <option value="unmonitor_show_if_empty">
                Unmonitor Show If Empty
              </option>
              <option value="downgrade_profile">Downgrade Profile</option>
              <option value="add_import_exclusion">
                Add Import Exclusion
              </option>
            </select>
          </FormGroup>
        </div>

        <FormGroup
          label="Staging Reason"
          htmlFor={reasonId}
          helpText="Visible in Leaving Soon table and Discord embed."
        >
          <input
            id={reasonId}
            name="reason"
            type="text"
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            placeholder="Reason for manual staging..."
            className="arr-input"
          />
        </FormGroup>

        {selectedIds.length > 0 && (
          <div>
            <div className="text-[12px] font-bold text-[var(--arr-text)] mb-1.5 flex items-center justify-between">
              <span>Selected for Staging ({selectedIds.length})</span>
              <button
                type="button"
                onClick={() => setSelectedIds([])}
                className="text-[11px] text-[var(--arr-danger)] hover:underline cursor-pointer"
              >
                Clear selection
              </button>
            </div>
            <div className="flex flex-wrap gap-1.5 p-2 bg-[var(--arr-card-center-bg)] border border-[var(--arr-card-border)] rounded max-h-28 overflow-y-auto">
              {selectedMediaItems.length > 0
                ? selectedMediaItems.map((m) => (
                    <span
                      key={m.id}
                      className="inline-flex items-center gap-1.5 px-2 py-0.5 bg-[var(--arr-card-bg)] text-[11px] text-[var(--arr-text)] rounded border border-[var(--arr-card-border)]"
                    >
                      <span className="truncate max-w-[200px]" title={m.title}>
                        {m.parentTitle ? `${m.parentTitle} - ${m.title}` : m.title}
                      </span>
                      <span className="text-[var(--arr-muted)]">
                        ({formatBytes(m.sizeBytes)})
                      </span>
                      <button
                        type="button"
                        aria-label={`Remove ${m.title} from selection`}
                        onClick={() => toggleSelect(m.id)}
                        className="text-[var(--arr-muted)] hover:text-[var(--arr-danger)] cursor-pointer ml-0.5"
                      >
                        <Trash2 aria-hidden="true" className="h-3 w-3" />
                      </button>
                    </span>
                  ))
                : selectedIds.map((id) => (
                    <span
                      key={id}
                      className="inline-flex items-center gap-1 px-2 py-0.5 bg-[var(--arr-card-bg)] text-[11px] text-[var(--arr-text)] rounded border border-[var(--arr-card-border)]"
                    >
                      Item #{id}
                    </span>
                  ))}
            </div>
          </div>
        )}

        <div>
          <label
            htmlFor={searchId}
            className="block text-[12px] font-bold text-[var(--arr-text)] mb-1.5"
          >
            Search &amp; Select Media Items
          </label>
          <search>
            <div className="relative mb-2">
              <Search
                aria-hidden="true"
                className="absolute left-2.5 top-2.5 h-4 w-4 text-[var(--arr-muted)]"
              />
              <input
                id={searchId}
                type="search"
                autoCorrect="off"
                autoCapitalize="none"
                spellCheck={false}
                placeholder="Search catalog media by title, series, tag..."
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                className="arr-input pl-9"
              />
            </div>
          </search>

          <div className="border border-[var(--arr-card-border)] rounded bg-[var(--arr-card-center-bg)] max-h-60 overflow-y-auto">
            {mediaQuery.isLoading ? (
              <div className="p-4 text-center text-[12px] text-[var(--arr-muted)]">
                Loading media catalog...
              </div>
            ) : filteredMedia.length === 0 ? (
              <div className="p-4 text-center text-[12px] text-[var(--arr-muted)]">
                No media items found.
              </div>
            ) : (
              <table className="arr-table w-full text-[12px]">
                <thead>
                  <tr>
                    <th className="w-10">Select</th>
                    <th>Title</th>
                    <th>Type</th>
                    <th>Classification</th>
                    <th>Size</th>
                    <th>Watched</th>
                  </tr>
                </thead>
                <tbody>
                  {filteredMedia.map((m) => {
                    const isSelected = selectedIds.includes(m.id);
                    const displayTitle = m.parentTitle
                      ? `${m.parentTitle} - ${m.title}`
                      : m.title;
                    return (
                      <tr
                        key={m.id}
                        onClick={() => toggleSelect(m.id)}
                        className={`cursor-pointer ${
                          isSelected ? 'bg-[var(--arr-primary)]/20' : ''
                        }`}
                      >
                        <td
                          className="text-center"
                          onClick={(e) => e.stopPropagation()}
                        >
                          <input
                            type="checkbox"
                            aria-label={`Select ${displayTitle}`}
                            checked={isSelected}
                            onChange={() => toggleSelect(m.id)}
                          />
                        </td>
                        <th scope="row" className="font-semibold text-[var(--arr-text)] text-left">
                          <div className="truncate max-w-[280px]" title={displayTitle}>
                            {displayTitle} {m.year ? `(${m.year})` : ''}
                          </div>
                        </th>
                        <td>
                          <ArrLabel kind="default">{m.mediaType}</ArrLabel>
                        </td>
                        <td>
                          {m.classificationKind ? (
                            <ArrLabel kind="primary">
                              {m.classificationKind}
                            </ArrLabel>
                          ) : (
                            <span className="text-[var(--arr-muted)]">-</span>
                          )}
                        </td>
                        <td className="whitespace-nowrap">
                          {formatBytes(m.sizeBytes)}
                        </td>
                        <td className="text-[var(--arr-muted)] whitespace-nowrap text-[11px]">
                          {m.viewCount > 0 ? `${m.viewCount}x` : 'Unwatched'}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            )}
          </div>
        </div>
      </div>
    </ArrModal>
  );
}
