import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import {
  AlertTriangle,
  CheckCircle2,
  Clock,
  FileSearch,
  Play,
  Search,
  Shield,
  XCircle,
} from 'lucide-react';
import { apiFetch, formatBytes } from '../../lib/api';
import { ArrAlert, ArrButton, ArrLabel, ArrModal } from '../arr/ArrPrimitives';
import type {
  CleanerrMediaItemDto,
  CleanerrMediaListResponse,
  CleanerrRuleGroupSpec,
  CleanerrTestMediaResult,
} from './cleanerrTypes';

interface Props {
  ruleGroup?: CleanerrRuleGroupSpec | null;
  graphJson?: Record<string, unknown> | null;
  workflowId?: number | null;
  initialMediaItemId?: number | null;
  onClose: () => void;
}

function normalizeMediaList(
  data: CleanerrMediaListResponse | CleanerrMediaItemDto[] | undefined,
): CleanerrMediaItemDto[] {
  if (!data) return [];
  return Array.isArray(data) ? data : (data.items ?? []);
}

export function CleanerrTestMediaModal({
  ruleGroup,
  graphJson,
  workflowId,
  initialMediaItemId,
  onClose,
}: Props) {
  const simulatedFreeId = useId();
  const simulatedFreePctId = useId();
  const searchId = useId();

  const [searchTerm, setSearchTerm] = useState('');
  const [selectedMedia, setSelectedMedia] =
    useState<CleanerrMediaItemDto | null>(null);
  const [simulatedFreeGb, setSimulatedFreeGb] = useState<string>('');
  const [simulatedFreePct, setSimulatedFreePct] = useState<string>('');

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
    if (!searchTerm.trim()) {
      const top = allMedia.slice(0, 20);
      if (
        initialMediaItemId != null &&
        !top.some((m) => m.id === initialMediaItemId)
      ) {
        const preselected = allMedia.find((m) => m.id === initialMediaItemId);
        if (preselected) {
          return [preselected, ...top.slice(0, 19)];
        }
      }
      return top;
    }
    const q = searchTerm.toLowerCase();
    return allMedia
      .filter(
        (m) =>
          m.title.toLowerCase().includes(q) ||
          m.parentTitle?.toLowerCase().includes(q) ||
          m.grandparentTitle?.toLowerCase().includes(q) ||
          m.mediaType.toLowerCase().includes(q) ||
          (m.tags ?? []).some((t) => t.toLowerCase().includes(q)),
      )
      .slice(0, 30);
  }, [allMedia, searchTerm, initialMediaItemId]);

  const testMediaMutation = useMutation({
    mutationFn: (mediaItemId: number) => {
      const freeGb = simulatedFreeGb.trim() ? Number(simulatedFreeGb) : null;
      const freePct = simulatedFreePct.trim() ? Number(simulatedFreePct) : null;
      return apiFetch<CleanerrTestMediaResult>(
        '/api/v1/cleanerr/rules/test-media',
        {
          method: 'POST',
          body: JSON.stringify({
            mediaItemId,
            ruleGroup: ruleGroup ?? null,
            graphJson: graphJson ?? null,
            workflowId: workflowId ?? null,
            simulatedFreeSpaceGb:
              freeGb !== null && Number.isFinite(freeGb) ? freeGb : null,
            simulatedFreeSpacePercent:
              freePct !== null && Number.isFinite(freePct) ? freePct : null,
          }),
        },
      );
    },
  });

  const { mutate: runTestMedia } = testMediaMutation;
  const autoTestedIdRef = useRef<number | null>(null);

  useEffect(() => {
    if (initialMediaItemId == null) return;
    if (autoTestedIdRef.current !== initialMediaItemId) {
      autoTestedIdRef.current = initialMediaItemId;
      runTestMedia(initialMediaItemId);
    }
    if (!selectedMedia) {
      const match = allMedia.find((m) => m.id === initialMediaItemId);
      if (match) {
        setSelectedMedia(match);
      }
    }
  }, [initialMediaItemId, allMedia, selectedMedia, runTestMedia]);

  const handleSelectMedia = (m: CleanerrMediaItemDto) => {
    setSelectedMedia(m);
    runTestMedia(m.id);
  };

  const result = testMediaMutation.data;

  return (
    <ArrModal
      title={
        <div className="flex items-center gap-2">
          <FileSearch
            aria-hidden="true"
            className="h-5 w-5 text-[var(--arr-accent)]"
          />
          <span>Cleanerr Test Media Explainer</span>
        </div>
      }
      onClose={onClose}
      maxWidth="max-w-4xl"
      footer={
        <ArrButton variant="default" onClick={onClose}>
          Close
        </ArrButton>
      }
    >
      <div className="space-y-4">
        <div>
          <div className="flex flex-wrap items-center justify-between gap-2 text-[12px] font-semibold text-[var(--arr-text)] mb-1.5">
            <label htmlFor={searchId}>
              Select Tracked Media to Trace Against Rules
            </label>
            <div className="flex flex-wrap items-center gap-3">
              <div className="flex items-center gap-2">
                <label
                  htmlFor={simulatedFreeId}
                  className="text-[var(--arr-muted)] font-normal"
                >
                  Simulate Free Disk (GB):
                </label>
                <input
                  id={simulatedFreeId}
                  type="number"
                  placeholder="Live"
                  value={simulatedFreeGb}
                  onChange={(e) => setSimulatedFreeGb(e.target.value)}
                  className="arr-input !h-[26px] !w-24 !text-[11px] !py-0.5 !px-2"
                />
              </div>
              <div className="flex items-center gap-2">
                <label
                  htmlFor={simulatedFreePctId}
                  className="text-[var(--arr-muted)] font-normal"
                >
                  Free (%):
                </label>
                <input
                  id={simulatedFreePctId}
                  type="number"
                  min={0}
                  max={100}
                  placeholder="Live"
                  value={simulatedFreePct}
                  onChange={(e) => setSimulatedFreePct(e.target.value)}
                  className="arr-input !h-[26px] !w-20 !text-[11px] !py-0.5 !px-2"
                />
              </div>
            </div>
          </div>

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
                placeholder="Search Movie or TV Series title..."
                value={searchTerm}
                onChange={(e) => setSearchTerm(e.target.value)}
                className="arr-input pl-9"
              />
            </div>
          </search>

          <div className="border border-[var(--arr-card-border)] rounded bg-[var(--arr-card-center-bg)] max-h-40 overflow-y-auto">
            {mediaQuery.isLoading ? (
              <div className="p-3 text-center text-[12px] text-[var(--arr-muted)]">
                Loading tracked media catalog...
              </div>
            ) : filteredMedia.length === 0 ? (
              <div className="p-3 text-center text-[12px] text-[var(--arr-muted)]">
                No media matches found.
              </div>
            ) : (
              <div className="divide-y divide-[var(--arr-divider)]">
                {filteredMedia.map((m) => {
                  const isSelected =
                    (selectedMedia?.id ?? initialMediaItemId ?? null) === m.id;
                  const displayTitle = m.parentTitle
                    ? `${m.parentTitle} - ${m.title}`
                    : m.title;
                  return (
                    <div
                      key={m.id}
                      className={`p-2 flex items-center justify-between gap-2 text-[12px] transition-colors ${
                        isSelected
                          ? 'bg-[var(--arr-primary)]/20 text-[var(--arr-text)]'
                          : 'hover:bg-white/5 text-[var(--arr-text-dim)]'
                      }`}
                    >
                      <div className="min-w-0 flex items-center gap-2">
                        <ArrLabel kind="default">{m.mediaType}</ArrLabel>
                        <span
                          className="font-semibold truncate max-w-[340px]"
                          title={displayTitle}
                        >
                          {displayTitle}
                        </span>
                        {m.year && (
                          <span className="text-[var(--arr-muted)]">
                            ({m.year})
                          </span>
                        )}
                        {m.classificationKind && (
                          <ArrLabel kind="primary">
                            {m.classificationKind}
                          </ArrLabel>
                        )}
                      </div>
                      <div className="shrink-0 flex items-center gap-3 text-[11px] text-[var(--arr-muted)]">
                        <span>{formatBytes(m.sizeBytes)}</span>
                        <span>
                          {m.viewCount > 0 ? `${m.viewCount}x viewed` : 'Unwatched'}
                        </span>
                        <ArrButton
                          variant={isSelected ? 'primary' : 'default'}
                          aria-pressed={isSelected}
                          className="!h-[24px] !px-2.5 !text-[11px]"
                          onClick={() => handleSelectMedia(m)}
                        >
                          <Play aria-hidden="true" className="h-2.5 w-2.5" /> Test
                        </ArrButton>
                      </div>
                    </div>
                  );
                })}
              </div>
            )}
          </div>
        </div>

        {testMediaMutation.isPending && (
          <div
            role="status"
            aria-live="polite"
            className="p-8 text-center text-[13px] text-[var(--arr-muted)] bg-[var(--arr-card-center-bg)] rounded border border-[var(--arr-card-border)]"
          >
            Evaluating rule conditions and guard rails...
          </div>
        )}

        {testMediaMutation.isError && (
          <ArrAlert kind="danger">
            {(testMediaMutation.error as Error)?.message ??
              'Failed to evaluate media against rule specification.'}
          </ArrAlert>
        )}

        {result && (
          <div className="space-y-3.5">
            <div
              className={`p-3.5 rounded border flex items-start gap-3 ${
                result.wouldMatch
                  ? 'bg-amber-500/15 border-amber-500/60 text-[var(--arr-text)]'
                  : 'bg-emerald-500/15 border-emerald-500/60 text-[var(--arr-text)]'
              }`}
            >
              {result.wouldMatch ? (
                <AlertTriangle
                  aria-hidden="true"
                  className="h-5 w-5 text-amber-500 shrink-0 mt-0.5"
                />
              ) : (
                <CheckCircle2
                  aria-hidden="true"
                  className="h-5 w-5 text-emerald-500 shrink-0 mt-0.5"
                />
              )}
              <div className="flex-1 min-w-0">
                <div className="font-bold text-[14px]">
                  {result.wouldMatch
                    ? 'Target WOULD match and be staged in Leaving Soon queue'
                    : 'Target WOULD NOT match this rule group'}
                </div>
                <div className="text-[12px] opacity-90 mt-0.5">
                  {result.summary}
                </div>

                <div className="flex flex-wrap gap-2 mt-2">
                  {result.isExcluded && (
                    <ArrLabel kind="success">
                      <Shield aria-hidden="true" className="h-3 w-3" /> Excluded:{' '}
                      {result.exclusionReason ?? 'Protected'}
                    </ArrLabel>
                  )}
                  {result.isCurrentlyStaged && (
                    <ArrLabel kind="warning">
                      <Clock aria-hidden="true" className="h-3 w-3" /> Currently
                      Staged: Deletes {result.stagedDeleteAt?.slice(0, 10)}
                    </ArrLabel>
                  )}
                  {result.classificationKind && (
                    <ArrLabel kind="primary">
                      {result.classificationKind}
                    </ArrLabel>
                  )}
                </div>
              </div>
            </div>

            {result.guardNotes && result.guardNotes.length > 0 && (
              <div className="p-3 bg-[var(--arr-card-center-bg)] border border-[var(--arr-card-border)] rounded text-[12px] text-[var(--arr-text-dim)] space-y-1">
                <div className="font-semibold text-[var(--arr-text)] flex items-center gap-1.5 mb-1">
                  <Shield
                    aria-hidden="true"
                    className="h-3.5 w-3.5 text-[var(--arr-accent)]"
                  />{' '}
                  Guard Rail Evaluations:
                </div>
                {result.guardNotes.map((note, idx) => (
                  <div key={idx} className="flex items-start gap-1.5 pl-2">
                    <span className="text-[var(--arr-accent)]">•</span>
                    <span>{note}</span>
                  </div>
                ))}
              </div>
            )}

            <div className="space-y-3">
              <div className="text-[12px] font-bold text-[var(--arr-text)]">
                Rule Evaluation Trace:
              </div>
              {result.sections && result.sections.length > 0 ? (
                result.sections.map((sec, secIdx) => (
                  <div
                    key={sec.sectionIndex}
                    className={`rounded border overflow-hidden ${
                      sec.sectionPassed
                        ? 'border-emerald-500/50 bg-emerald-500/10'
                        : 'border-red-500/50 bg-red-500/10'
                    }`}
                  >
                    <div className="px-3.5 py-2 bg-black/20 flex items-center justify-between border-b border-[var(--arr-card-border)]">
                      <div className="flex items-center gap-2">
                        {secIdx > 0 && (
                          <ArrLabel kind="purple">
                            {sec.sectionOperator}
                          </ArrLabel>
                        )}
                        <span className="font-bold text-[13px] text-[var(--arr-text)]">
                          Section {sec.sectionIndex + 1}
                        </span>
                      </div>
                      <ArrLabel kind={sec.sectionPassed ? 'success' : 'danger'}>
                        {sec.sectionPassed
                          ? 'Section Passed'
                          : 'Section Failed'}
                      </ArrLabel>
                    </div>

                    <div className="p-2 space-y-1.5">
                      {sec.rules.map((r, rIdx) => (
                        <div
                          key={r.ruleIndex}
                          className={`p-2.5 rounded border text-[12px] flex items-start justify-between gap-3 ${
                            r.passed
                              ? 'bg-emerald-500/10 border-emerald-500/35 text-[var(--arr-text)]'
                              : 'bg-red-500/10 border-red-500/35 text-[var(--arr-text)]'
                          }`}
                        >
                          <div className="space-y-1 min-w-0 flex-1">
                            <div className="flex items-center gap-2 flex-wrap">
                              {rIdx > 0 && (
                                <ArrLabel kind="default">
                                  {r.ruleConnector ?? 'AND'}
                                </ArrLabel>
                              )}
                              <span className="font-semibold">
                                {r.fieldLabel}{' '}
                                <span className="text-[11px] font-mono text-[var(--arr-muted)]">
                                  ({r.fieldKey})
                                </span>
                              </span>
                              <span className="px-1.5 py-0.5 rounded bg-black/20 font-mono text-[11px]">
                                {r.comparator}
                              </span>
                              <span className="font-medium text-amber-400">
                                {r.isPropertyComparison
                                  ? `[Field: ${r.expectedTarget}]`
                                  : `"${r.expectedTarget ?? ''}"`}
                              </span>
                            </div>

                            <div className="text-[11px] text-[var(--arr-text-dim)]">
                              Actual Value:{' '}
                              <strong className="font-mono text-[var(--arr-text)]">
                                {r.actualValue ?? 'null / unset'}
                              </strong>
                            </div>

                            <div className="text-[11px] text-[var(--arr-muted)] italic">
                              {r.explanation}
                            </div>
                          </div>

                          <div className="shrink-0 pt-0.5">
                            {r.passed ? (
                              <CheckCircle2
                                aria-label="Rule passed"
                                className="h-5 w-5 text-emerald-500"
                              />
                            ) : (
                              <XCircle
                                aria-label="Rule failed"
                                className="h-5 w-5 text-red-500"
                              />
                            )}
                          </div>
                        </div>
                      ))}
                    </div>
                  </div>
                ))
              ) : (
                <div className="p-4 bg-[var(--arr-card-center-bg)] border border-[var(--arr-card-border)] rounded text-center text-[12px] text-[var(--arr-muted)]">
                  No rule conditions evaluated. Workflow may be configured in
                  DAG mode or has no active sections.
                </div>
              )}
            </div>
          </div>
        )}
      </div>
    </ArrModal>
  );
}
