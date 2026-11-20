import { useId, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Check, Copy, Download, FileCode, Upload } from 'lucide-react';
import { apiFetch } from '../../lib/api';
import { ArrAlert, ArrButton, ArrModal } from '../arr/ArrPrimitives';
import type { CleanerrWorkflowSummary } from './cleanerrTypes';

interface Props {
  workflowId: number | null;
  canImport?: boolean;
  onClose: () => void;
  onImportSuccess: (imported: CleanerrWorkflowSummary) => void;
}

function extractWorkflowNameFromSpec(raw: string): string {
  const trimmed = raw.trim();
  if (!trimmed) return '';
  if (trimmed.startsWith('{')) {
    try {
      const parsed = JSON.parse(trimmed) as { name?: unknown };
      if (typeof parsed.name === 'string') return parsed.name.trim();
    } catch {
      // fall back to YAML regex
    }
  }
  const match = trimmed.match(/^name:\s*["']?([^"'\r\n]+)["']?\s*$/m);
  return match?.[1]?.trim() ?? '';
}

const RULES_YAML_EXAMPLE = `name: "Stale Watched Movies"
editorMode: rules
targetMediaType: movie
graceDays: 7
actionMode: delete_files_and_unmonitor
sections:
  - sectionOperator: AND
    rules:
      - field: plex.lastViewedDaysAgo
        comparator: greater_than
        value: "90"`;

const DAG_JSON_EXAMPLE = JSON.stringify(
  {
    name: 'DAG Cleanup Pipeline',
    editorMode: 'dag',
    description: 'Visual DAG workflow imported via JSON/YAML',
    graphJson: {
      nodes: [
        {
          id: 'n1',
          type: 'cleanerrNode',
          position: { x: 80, y: 120 },
          data: {
            node_type: 'source.plex_library',
            label: 'Movies Library',
            config: { media_type: 'movie', library_ids: [] },
          },
        },
        {
          id: 'n2',
          type: 'cleanerrNode',
          position: { x: 360, y: 120 },
          data: {
            node_type: 'queue.grace_period',
            label: 'Stage Leaving Soon',
            config: { grace_days: 7 },
          },
        },
        {
          id: 'n3',
          type: 'cleanerrNode',
          position: { x: 640, y: 120 },
          data: {
            node_type: 'action.managerr',
            label: 'Delete & Unmonitor',
            config: { mode: 'delete_files_and_unmonitor' },
          },
        },
      ],
      edges: [
        { id: 'e1', source: 'n1', target: 'n2' },
        { id: 'e2', source: 'n2', target: 'n3' },
      ],
    },
  },
  null,
  2,
);

export function CleanerrYamlModal({
  workflowId,
  canImport = true,
  onClose,
  onImportSuccess,
}: Props) {
  const qc = useQueryClient();
  const exportTabId = useId();
  const importTabId = useId();
  const yamlTextareaId = useId();
  const confirmNameInputId = useId();
  const fileInputRef = useRef<HTMLInputElement | null>(null);

  const [activeTab, setActiveTab] = useState<'export' | 'import'>(
    workflowId || !canImport ? 'export' : 'import',
  );
  const [yamlInput, setYamlInput] = useState('');
  const [importDryRun, setImportDryRun] = useState(true);
  const [importEnabled, setImportEnabled] = useState(false);
  const [confirmWorkflowName, setConfirmWorkflowName] = useState('');
  const [copied, setCopied] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);

  const exportQuery = useQuery({
    queryKey: ['cleanerr-workflow-yaml', workflowId],
    queryFn: () =>
      apiFetch<{ workflowId: number; name: string; yaml: string }>(
        `/api/v1/cleanerr/workflows/${workflowId}/yaml`,
      ),
    enabled: Boolean(workflowId) && activeTab === 'export',
  });

  const extractedWorkflowName = extractWorkflowNameFromSpec(yamlInput);
  const liveConfirmValid =
    importDryRun ||
    (confirmWorkflowName.trim().length > 0 &&
      (!extractedWorkflowName ||
        confirmWorkflowName.trim().toLowerCase() ===
          extractedWorkflowName.toLowerCase()));

  const importMutation = useMutation({
    mutationFn: () =>
      apiFetch<CleanerrWorkflowSummary>('/api/v1/cleanerr/workflows/import-yaml', {
        method: 'POST',
        body: JSON.stringify({
          yaml: yamlInput,
          dryRun: importDryRun,
          enabled: importEnabled,
          confirmWorkflowName: !importDryRun
            ? confirmWorkflowName.trim()
            : undefined,
        }),
      }),
    onSuccess: (res) => {
      void qc.invalidateQueries({ queryKey: ['cleanerr-workflows'] });
      onImportSuccess(res);
      onClose();
    },
    onError: (err: Error) => {
      setErrorMessage(
        err.message || 'Failed to parse and import workflow YAML/JSON.',
      );
    },
  });

  const handleCopy = () => {
    const text = exportQuery.data?.yaml;
    if (text) {
      void navigator.clipboard.writeText(text);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    }
  };

  const handleDownload = () => {
    const data = exportQuery.data;
    if (data?.yaml) {
      const blob = new Blob([data.yaml], { type: 'text/yaml' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `${(data.name || 'cleanerr-workflow').replace(/[^a-z0-9-_]+/gi, '_')}.yaml`;
      a.click();
      URL.revokeObjectURL(url);
    }
  };

  const handleFileLoad = async (file: File | undefined) => {
    if (!file) return;
    setErrorMessage(null);
    const text = await file.text();
    setYamlInput(text);
  };

  return (
    <ArrModal
      title={
        <div className="flex items-center gap-2">
          <FileCode
            aria-hidden="true"
            className="h-5 w-5 text-[var(--arr-accent)]"
          />
          <span>Workflow YAML / JSON Import &amp; Export</span>
        </div>
      }
      onClose={onClose}
      maxWidth="max-w-4xl"
      footer={
        <>
          <ArrButton variant="default" onClick={onClose}>
            Close
          </ArrButton>
          {activeTab === 'export' ? (
            <>
              <ArrButton
                variant="default"
                disabled={!exportQuery.data?.yaml}
                onClick={handleDownload}
              >
                <Download aria-hidden="true" className="h-3.5 w-3.5" /> Download
                .yaml
              </ArrButton>
              <ArrButton
                variant="primary"
                disabled={!exportQuery.data?.yaml}
                onClick={handleCopy}
              >
                {copied ? (
                  <Check aria-hidden="true" className="h-3.5 w-3.5" />
                ) : (
                  <Copy aria-hidden="true" className="h-3.5 w-3.5" />
                )}
                {copied ? 'Copied' : 'Copy YAML'}
              </ArrButton>
            </>
          ) : canImport ? (
            <ArrButton
              variant="success"
              disabled={!yamlInput.trim() || !liveConfirmValid}
              busy={importMutation.isPending}
              onClick={() => {
                setErrorMessage(null);
                importMutation.mutate();
              }}
            >
              <Upload aria-hidden="true" className="h-3.5 w-3.5" /> Import
              Workflow
            </ArrButton>
          ) : null}
        </>
      }
    >
      <div className="space-y-4">
        <div
          role="tablist"
          aria-label="Workflow YAML actions"
          className="flex items-center border-b border-[var(--arr-card-border)] gap-4 mb-3"
        >
          {workflowId && (
            <button
              id={exportTabId}
              type="button"
              role="tab"
              aria-selected={activeTab === 'export'}
              onClick={() => setActiveTab('export')}
              className={`relative pb-2 text-[13px] font-medium transition-colors cursor-pointer ${
                activeTab === 'export'
                  ? 'text-[var(--arr-accent)] after:content-[""] after:absolute after:inset-x-0 after:bottom-0 after:border-b-2 after:border-[var(--arr-accent)]'
                  : 'text-[var(--arr-muted)] hover:text-[var(--arr-text)]'
              }`}
            >
              Export Workflow (Rules / DAG)
            </button>
          )}
          {canImport && (
            <button
              id={importTabId}
              type="button"
              role="tab"
              aria-selected={activeTab === 'import'}
              onClick={() => setActiveTab('import')}
              className={`relative pb-2 text-[13px] font-medium transition-colors cursor-pointer ${
                activeTab === 'import'
                  ? 'text-[var(--arr-accent)] after:content-[""] after:absolute after:inset-x-0 after:bottom-0 after:border-b-2 after:border-[var(--arr-accent)]'
                  : 'text-[var(--arr-muted)] hover:text-[var(--arr-text)]'
              }`}
            >
              Import Rules or DAG (YAML / JSON)
            </button>
          )}
        </div>

        {errorMessage && <ArrAlert kind="danger">{errorMessage}</ArrAlert>}

        {activeTab === 'export' ? (
          <div role="tabpanel" aria-labelledby={exportTabId}>
            <div className="flex flex-wrap items-center justify-between gap-2 mb-1.5">
              <span className="text-[12px] text-[var(--arr-muted)]">
                Workflow:{' '}
                <strong className="text-[var(--arr-text)]">
                  {exportQuery.data?.name ?? 'Loading...'}
                </strong>{' '}
                (ID #{workflowId})
              </span>
              <span className="text-[11px] text-[var(--arr-muted)]">
                Supports both Rule Group &amp; DAG workflow definitions
              </span>
            </div>
            {exportQuery.isLoading ? (
              <div className="p-8 text-center text-[var(--arr-muted)] text-[13px]">
                Exporting workflow specification...
              </div>
            ) : exportQuery.isError ? (
              <ArrAlert kind="danger">
                {(exportQuery.error as Error)?.message ??
                  'Failed to export workflow YAML.'}
              </ArrAlert>
            ) : (
              <textarea
                readOnly
                aria-label="Exported workflow YAML"
                value={exportQuery.data?.yaml ?? ''}
                rows={16}
                className="arr-input w-full font-mono text-[12px] select-all"
              />
            )}
          </div>
        ) : (
          <div
            role="tabpanel"
            aria-labelledby={importTabId}
            className="space-y-3"
          >
            <div className="flex flex-wrap items-center justify-between gap-2">
              <p className="text-[12px] text-[var(--arr-muted)]">
                Paste YAML or JSON for either a <strong>Rules</strong>{' '}
                specification or a Visual <strong>DAG</strong> graph workflow.
              </p>
              <div className="flex flex-wrap items-center gap-1.5">
                <input
                  ref={fileInputRef}
                  type="file"
                  accept=".yaml,.yml,.json"
                  className="sr-only"
                  onChange={(e) => {
                    void handleFileLoad(e.target.files?.[0]);
                    e.target.value = '';
                  }}
                />
                <ArrButton
                  variant="default"
                  className="!h-[26px] !px-2.5 !text-[11px]"
                  onClick={() => fileInputRef.current?.click()}
                >
                  <Upload aria-hidden="true" className="h-3 w-3" /> Load File
                </ArrButton>
                <ArrButton
                  variant="default"
                  className="!h-[26px] !px-2.5 !text-[11px]"
                  onClick={() => setYamlInput(RULES_YAML_EXAMPLE)}
                >
                  Rules Example
                </ArrButton>
                <ArrButton
                  variant="default"
                  className="!h-[26px] !px-2.5 !text-[11px]"
                  onClick={() => setYamlInput(DAG_JSON_EXAMPLE)}
                >
                  DAG Example
                </ArrButton>
              </div>
            </div>

            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4 p-3 bg-[var(--arr-card-center-bg)] border border-[var(--arr-card-border)] rounded">
              <label className="flex items-center gap-2 text-[12px] text-[var(--arr-text)] cursor-pointer">
                <input
                  type="checkbox"
                  checked={importDryRun}
                  onChange={(e) => {
                    const nextDryRun = e.target.checked;
                    setImportDryRun(nextDryRun);
                    if (nextDryRun) setConfirmWorkflowName('');
                  }}
                />
                <span>Set Dry-Run Mode on Import (Safe Preview)</span>
              </label>

              <label className="flex items-center gap-2 text-[12px] text-[var(--arr-text)] cursor-pointer">
                <input
                  type="checkbox"
                  checked={importEnabled}
                  onChange={(e) => setImportEnabled(e.target.checked)}
                />
                <span>Enable Workflow Immediately</span>
              </label>
            </div>

            {!importDryRun && (
              <div className="p-3 rounded border border-[#f05050]/50 bg-[#f05050]/10 space-y-1.5">
                <label
                  htmlFor={confirmNameInputId}
                  className="block text-[12px] font-semibold text-[var(--arr-text)]"
                >
                  Confirm workflow name{' '}
                  {extractedWorkflowName ? (
                    <>
                      (type{' '}
                      <code className="px-1 py-0.5 rounded bg-black/30 font-mono text-[var(--arr-accent)]">
                        {extractedWorkflowName}
                      </code>
                      )
                    </>
                  ) : null}{' '}
                  to import with Dry-Run disabled:
                </label>
                <input
                  id={confirmNameInputId}
                  type="text"
                  required
                  value={confirmWorkflowName}
                  onChange={(e) => setConfirmWorkflowName(e.target.value)}
                  placeholder={
                    extractedWorkflowName || 'Enter workflow name from YAML/JSON'
                  }
                  className="arr-input"
                />
              </div>
            )}

            <div>
              <label
                htmlFor={yamlTextareaId}
                className="block text-[12px] font-semibold text-[var(--arr-text)] mb-1"
              >
                Workflow Specification (YAML or JSON)
              </label>
              <textarea
                id={yamlTextareaId}
                placeholder={RULES_YAML_EXAMPLE}
                value={yamlInput}
                onChange={(e) => setYamlInput(e.target.value)}
                rows={14}
                autoCorrect="off"
                autoCapitalize="none"
                spellCheck={false}
                className="arr-input w-full font-mono text-[12px]"
              />
            </div>
          </div>
        )}
      </div>
    </ArrModal>
  );
}
