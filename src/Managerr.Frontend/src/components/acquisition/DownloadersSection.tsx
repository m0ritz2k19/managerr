import { useState } from 'react';
import {
  AlertCircle,
  AlertTriangle,
  CheckCircle2,
  EyeOff,
  FlaskConical,
  Plus,
  RefreshCw,
  RotateCw,
  Trash2,
} from 'lucide-react';
import {
  formatBytes,
  type TrackedDownloadDto,
} from '../../lib/api';
import {
  ArrAddCard,
  ArrAlert,
  ArrButton,
  ArrLabel,
  ArrModal,
  ArrSettingsCard,
  FieldSet,
  FormGroup,
} from '../arr/ArrPrimitives';
import { PathBrowserInput } from '../arr/PathBrowserModal';
import type {
  ActiveQueueItemDto,
  DownloadClientDto,
  DriverDto,
  ProcessedImportRow,
  RemotePathMappingDto,
} from './acquisitionTypes';

interface DownloadersSectionProps {
  canWriteDownloaders: boolean;
  downloaderBanner: string | null;
  clients: DownloadClientDto[];
  drivers: DriverDto[];
  trackedDownloads: TrackedDownloadDto[];
  isTrackedFetching: boolean;
  onRefetchTracked: () => void;
  queueItems: ActiveQueueItemDto[];
  imports: ProcessedImportRow[];
  catalogWorks: Array<{ id: number; title: string; year: number | null; workKind: string }>;
  onOpenEditClient: (c: DownloadClientDto) => void;
  onOpenAddClient: () => void;
  onDeleteClient: (id: number) => void;
  isDeleteClientPending: boolean;
  showAddClientPicker: boolean;
  editingClientId: number | null;
  selectedDriver: DriverDto | null;
  onSelectDriver: (dr: DriverDto | null) => void;
  onCloseClientModal: () => void;
  clientName: string;
  onChangeClientName: (v: string) => void;
  clientHost: string;
  onChangeClientHost: (v: string) => void;
  clientPort: string;
  onChangeClientPort: (v: string) => void;
  clientApiKey: string;
  onChangeClientApiKey: (v: string) => void;
  clientUsername: string;
  onChangeClientUsername: (v: string) => void;
  clientPassword: string;
  onChangeClientPassword: (v: string) => void;
  clientUseSsl: boolean;
  onChangeClientUseSsl: (v: boolean) => void;
  clientIsEnabled?: boolean;
  onChangeClientIsEnabled?: (v: boolean) => void;
  clientUrlBase?: string;
  onChangeClientUrlBase?: (v: string) => void;
  clientPriority?: string;
  onChangeClientPriority?: (v: string) => void;
  clientRemoveCompleted?: boolean;
  onChangeClientRemoveCompleted?: (v: boolean) => void;
  clientRemoveFailed?: boolean;
  onChangeClientRemoveFailed?: (v: boolean) => void;
  clientCategoryMappings?: Record<string, string>;
  onChangeClientCategoryMappings?: (v: Record<string, string>) => void;
  clientWatchFolder?: string;
  onChangeClientWatchFolder?: (v: string) => void;
  onTestClient?: () => void;
  isTestClientPending?: boolean;
  clientTestStatus?: { ok: boolean; message: string } | null;
  clientHasSavedKey: boolean;
  clientHasSavedPass: boolean;
  onSaveClient: () => void;
  isSaveClientPending: boolean;
  selectedTrackedForImport: TrackedDownloadDto | null;
  onOpenManualImportModal: (item: TrackedDownloadDto) => void;
  onCloseManualImportModal: () => void;
  onIgnoreTracked: (id: number) => void;
  isIgnoreTrackedPending: boolean;
  ignoringTrackedId: number | null;
  overrideWorkId: number | null;
  onChangeOverrideWorkId: (v: number | null) => void;
  overrideSeason: string;
  onChangeOverrideSeason: (v: string) => void;
  overrideEpisodes: string;
  onChangeOverrideEpisodes: (v: string) => void;
  bypassUpgrade: boolean;
  onChangeBypassUpgrade: (v: boolean) => void;
  confirmedAudioLangs: string;
  onChangeConfirmedAudioLangs: (v: string) => void;
  manualImportError: string | null;
  onExecuteManualImport: () => void;
  isRetryImportPending: boolean;
  remotePaths: RemotePathMappingDto[];
  onAddRemotePath: (data: {
    downloadClientId: number | null;
    host: string;
    remotePath: string;
    localPath: string;
  }) => Promise<void>;
  isAddRemotePathPending: boolean;
  onDeleteRemotePath: (id: number) => void;
  isDeleteRemotePathPending: boolean;
}

export function DownloadersSection({
  canWriteDownloaders,
  downloaderBanner,
  clients,
  drivers,
  trackedDownloads,
  isTrackedFetching,
  onRefetchTracked,
  queueItems,
  imports,
  catalogWorks,
  onOpenEditClient,
  onOpenAddClient,
  onDeleteClient,
  isDeleteClientPending,
  showAddClientPicker,
  editingClientId,
  selectedDriver,
  onSelectDriver,
  onCloseClientModal,
  clientName,
  onChangeClientName,
  clientHost,
  onChangeClientHost,
  clientPort,
  onChangeClientPort,
  clientApiKey,
  onChangeClientApiKey,
  clientUsername,
  onChangeClientUsername,
  clientPassword,
  onChangeClientPassword,
  clientUseSsl,
  onChangeClientUseSsl,
  clientIsEnabled = true,
  onChangeClientIsEnabled,
  clientUrlBase = '',
  onChangeClientUrlBase,
  clientPriority = '1',
  onChangeClientPriority,
  clientRemoveCompleted = true,
  onChangeClientRemoveCompleted,
  clientRemoveFailed = true,
  onChangeClientRemoveFailed,
  clientCategoryMappings = {},
  onChangeClientCategoryMappings,
  clientWatchFolder = '',
  onChangeClientWatchFolder,
  onTestClient,
  isTestClientPending = false,
  clientTestStatus = null,
  clientHasSavedKey,
  clientHasSavedPass,
  onSaveClient,
  isSaveClientPending,
  selectedTrackedForImport,
  onOpenManualImportModal,
  onCloseManualImportModal,
  onIgnoreTracked,
  isIgnoreTrackedPending,
  ignoringTrackedId,
  overrideWorkId,
  onChangeOverrideWorkId,
  overrideSeason,
  onChangeOverrideSeason,
  overrideEpisodes,
  onChangeOverrideEpisodes,
  bypassUpgrade,
  onChangeBypassUpgrade,
  confirmedAudioLangs,
  onChangeConfirmedAudioLangs,
  manualImportError,
  onExecuteManualImport,
  isRetryImportPending,
  remotePaths,
  onAddRemotePath,
  isAddRemotePathPending,
  onDeleteRemotePath,
  isDeleteRemotePathPending,
}: DownloadersSectionProps) {
  const [showAddRpmModal, setShowAddRpmModal] = useState(false);
  const [rpmHost, setRpmHost] = useState('');
  const [rpmRemotePath, setRpmRemotePath] = useState('');
  const [rpmLocalPath, setRpmLocalPath] = useState('/downloads/usenet/');
  const [rpmClientId, setRpmClientId] = useState<string>('all');
  const [rpmError, setRpmError] = useState<string | null>(null);
  return (
    <>
      {downloaderBanner && (
        <ArrAlert kind="info" className="mb-4">
          {downloaderBanner}
        </ArrAlert>
      )}

      <FieldSet legend="Download Clients">
        <div className="flex flex-wrap gap-4">
          {clients.map((c) => (
            <ArrSettingsCard
              key={c.id}
              title={c.name}
              onClick={canWriteDownloaders ? () => onOpenEditClient(c) : undefined}
              footer={
                <>
                  <span className="font-mono text-[12px] text-[#cccccc]">
                    {c.host}:{c.port}
                  </span>
                  <div className="inline-flex items-center gap-1.5">
                    <ArrLabel kind={(c as DownloadClientDto & { isEnabled?: boolean }).isEnabled ? 'success' : 'default'}>
                      {(c as DownloadClientDto & { isEnabled?: boolean }).isEnabled ? 'Enabled' : 'Disabled'}
                    </ArrLabel>
                    {canWriteDownloaders && (
                      <button
                        type="button"
                        title="Remove download client"
                        onClick={(e) => {
                          e.stopPropagation();
                          onDeleteClient(c.id);
                        }}
                        className="text-[#909293] hover:text-[#f05050] cursor-pointer p-0.5"
                      >
                        <Trash2 className="h-3.5 w-3.5" />
                      </button>
                    )}
                  </div>
                </>
              }
            >
              <div className="flex flex-wrap gap-1 pt-1">
                <ArrLabel kind={c.protocol.toLowerCase() === 'usenet' ? 'usenet' : 'torrent'}>
                  {c.protocol.toLowerCase() === 'usenet' ? 'Usenet' : 'Torrent'}
                </ArrLabel>
                <ArrLabel kind="default">{c.implementation}</ArrLabel>
              </div>
              <div className="text-[11px] text-[#909293] pt-1 truncate">
                Categories: {Object.values(c.categoryMappings ?? {}).slice(0, 4).join(', ') || 'managerr-*'}
              </div>
            </ArrSettingsCard>
          ))}

          {canWriteDownloaders && (
            <ArrAddCard label="Add Download Client" onClick={onOpenAddClient} />
          )}
        </div>
      </FieldSet>

      <FieldSet
        legend={`Remote Path Mappings (${remotePaths.length})`}
        action={
          canWriteDownloaders && (
            <ArrButton
              variant="default"
              onClick={() => {
                setRpmHost('');
                setRpmRemotePath('');
                setRpmLocalPath('/downloads/usenet/');
                setRpmClientId('all');
                setRpmError(null);
                setShowAddRpmModal(true);
              }}
              title="Add Remote Path Mapping"
            >
              <Plus className="h-3.5 w-3.5 mr-1" aria-hidden="true" /> Add Mapping
            </ArrButton>
          )
        }
      >
        <p className="text-[12px] text-[#909293] mb-3">
          Map remote download paths from separate container hosts or seedboxes into local container mounts (e.g. <code>/downloads/</code> → <code>/downloads/usenet/</code> or <code>/downloads/torrents/</code>).
        </p>

        {remotePaths.length === 0 ? (
          <div className="text-[13px] text-[#888888] py-2 italic">
            No remote path mappings configured. Managerr will use downloaded paths directly.
          </div>
        ) : (
          <div className="overflow-x-auto border border-arr-card-border rounded-[4px]">
            <table className="arr-table min-w-full text-left">
              <thead>
                <tr>
                  <th scope="col" className="w-[180px]">Host</th>
                  <th scope="col">Remote Path</th>
                  <th scope="col">Local Path</th>
                  <th scope="col" className="w-[180px]">Download Client</th>
                  {canWriteDownloaders && <th scope="col" className="w-[60px] text-right">Actions</th>}
                </tr>
              </thead>
              <tbody>
                {remotePaths.map((rpm) => {
                  const client = clients.find((c) => c.id === rpm.downloadClientId);
                  return (
                    <tr key={rpm.id} className="hover:bg-white/5 transition-colors">
                      <td className="font-mono text-[12px] text-[#e1e2e3] font-semibold">{rpm.host}</td>
                      <td className="font-mono text-[12px] text-[#cccccc]">{rpm.remotePath}</td>
                      <td className="font-mono text-[12px] text-arr-accent">{rpm.localPath}</td>
                      <td className="text-[12px] text-[#909293]">{client ? client.name : 'All Clients'}</td>
                      {canWriteDownloaders && (
                        <td className="text-right">
                          <button
                            type="button"
                            onClick={() => onDeleteRemotePath(rpm.id)}
                            disabled={isDeleteRemotePathPending}
                            className="p-1 text-[#f05050] hover:text-white hover:bg-[#f05050]/20 rounded cursor-pointer transition-colors"
                            title={`Delete mapping for ${rpm.host}`}
                            aria-label={`Delete mapping ${rpm.id}`}
                          >
                            <Trash2 className="h-4 w-4" />
                          </button>
                        </td>
                      )}
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </FieldSet>

      <FieldSet
        legend={`Tracked Downloads (Sonarr CDH State Machine) (${trackedDownloads.length})`}
        action={
          <ArrButton
            variant="default"
            busy={isTrackedFetching}
            onClick={onRefetchTracked}
            title="Refresh Tracked Downloads Pipeline"
          >
            <RefreshCw className={`h-3.5 w-3.5 ${isTrackedFetching ? 'animate-spin' : ''}`} /> Refresh Pipeline
          </ArrButton>
        }
      >
        {trackedDownloads.length === 0 ? (
          <div className="text-[13px] text-[#909293]">
            No tracked downloads in the Completed Download Handling pipeline. Active and recently grabbed jobs will appear here automatically.
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="arr-table">
              <thead>
                <tr>
                  <th>Title / Client</th>
                  <th>State</th>
                  <th>Matched Work</th>
                  <th>File Transfer</th>
                  <th>Updated</th>
                  <th className="w-56 text-right">Actions</th>
                </tr>
              </thead>
              <tbody>
                {trackedDownloads.map((item) => {
                  const stateLower = item.state.toLowerCase();
                  const stateKind =
                    stateLower === 'imported'
                      ? 'success'
                      : stateLower === 'downloading' || stateLower === 'importing'
                        ? 'primary'
                        : stateLower === 'importpending'
                          ? 'usenet'
                          : stateLower === 'importblocked'
                            ? 'warning'
                            : stateLower === 'failed' || stateLower === 'failedpending'
                              ? 'danger'
                              : 'default';

                  const isIgnoring = isIgnoreTrackedPending && ignoringTrackedId === item.id;

                  return (
                    <tr key={item.id}>
                      <td>
                        <div className="font-mono text-[12px] text-[#eeeeee] font-semibold break-all">
                          {item.title}
                        </div>
                        <div className="flex flex-wrap items-center gap-2 mt-1 text-[11px] text-[#909293]">
                          <ArrLabel kind={item.protocol.toLowerCase() === 'usenet' ? 'usenet' : 'torrent'}>
                            {item.clientName}
                          </ArrLabel>
                          <span className="font-mono bg-[#1e2022] px-1 py-0.5 rounded border border-[#303438]">
                            {item.mediaVertical}
                          </span>
                          {item.outputPath && (
                            <span className="font-mono truncate max-w-xs" title={item.outputPath}>
                              {item.outputPath}
                            </span>
                          )}
                        </div>
                        {item.statusMessages && item.statusMessages.length > 0 && (
                          <div className="mt-1.5 flex flex-col gap-1">
                            {item.statusMessages.map((msg, i) => (
                              <div
                                key={i}
                                className="inline-flex items-center gap-1.5 px-2 py-0.5 rounded text-[11px] bg-[#3d3214]/80 text-[#f8ac59] border border-[#f8ac59]/30 font-mono"
                              >
                                <AlertTriangle className="h-3 w-3 shrink-0 text-[#ffa500]" />
                                <span>{msg}</span>
                              </div>
                            ))}
                          </div>
                        )}
                      </td>
                      <td>
                        <div className="flex flex-col gap-1 items-start">
                          <ArrLabel kind={stateKind}>
                            {item.state === 'ImportBlocked' && (
                              <AlertTriangle className="h-3 w-3 inline mr-1 text-[#191919]" />
                            )}
                            {item.state === 'Failed' && (
                              <AlertCircle className="h-3 w-3 inline mr-1 text-white" />
                            )}
                            {item.state}
                          </ArrLabel>
                          {item.trackedStatus && item.trackedStatus !== 'ok' && (
                            <span
                              className={`text-[10px] uppercase font-mono px-1 py-0.5 rounded ${
                                item.trackedStatus === 'error'
                                  ? 'bg-[#3b1c1c] text-[#f05050]'
                                  : 'bg-[#3d3214] text-[#f8ac59]'
                              }`}
                            >
                              Status: {item.trackedStatus}
                            </span>
                          )}
                        </div>
                      </td>
                      <td>
                        {item.workTitle ? (
                          <div>
                            <div className="font-semibold text-[13px] text-arr-accent">
                              {item.workTitle}
                            </div>
                            {item.workId && (
                              <div className="text-[11px] text-[#909293] font-mono">
                                Work #{item.workId}
                                {item.unitIds?.length > 0 ? ` (Units: ${item.unitIds.join(', ')})` : ''}
                              </div>
                            )}
                          </div>
                        ) : (
                          <span className="text-[12px] text-[#909293] italic">
                            Unmapped / Pending Match
                          </span>
                        )}
                      </td>
                      <td>
                        <ArrLabel kind={item.canMoveFiles ? 'success' : 'primary'}>
                          {item.canMoveFiles ? 'Move / Hardlink' : 'Seeding Hardlink'}
                        </ArrLabel>
                      </td>
                      <td className="font-mono text-[12px] text-[#909293] whitespace-nowrap">
                        {new Date(item.updatedAt || item.firstSeenAt).toLocaleTimeString()}
                      </td>
                      <td className="text-right">
                        <div className="inline-flex items-center gap-1.5 justify-end">
                          <ArrButton
                            variant="default"
                            onClick={() => onOpenManualImportModal(item)}
                            title="Retry or manually override target work/episode mapping"
                          >
                            <RotateCw className="h-3 w-3" /> Retry / Import
                          </ArrButton>
                          {item.state !== 'Ignored' && item.state !== 'Imported' && (
                            <ArrButton
                              variant="default"
                              busy={isIgnoring}
                              onClick={() => onIgnoreTracked(item.id)}
                              title="Mark download ignored by CDH pipeline"
                            >
                              <EyeOff className="h-3 w-3 text-[#909293]" /> Ignore
                            </ArrButton>
                          )}
                        </div>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </FieldSet>

      <FieldSet legend={`Unified Queue & History (${queueItems.length})`}>
        {queueItems.length === 0 ? (
          <div className="text-[13px] text-[#909293]">
            No active or recent jobs in configured download clients.
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="arr-table">
              <thead>
                <tr>
                  <th>Client</th>
                  <th>Title</th>
                  <th>Vertical</th>
                  <th>Status</th>
                  <th>Size</th>
                  <th>Output Path</th>
                </tr>
              </thead>
              <tbody>
                {queueItems.map((q) => (
                  <tr key={`${q.clientId}-${q.externalId}`}>
                    <td>
                      <ArrLabel kind={q.protocol.toLowerCase() === 'usenet' ? 'usenet' : 'torrent'}>
                        {q.clientName}
                      </ArrLabel>
                    </td>
                    <td className="font-mono text-[12px] text-[#eeeeee]">
                      {q.title}
                      {q.errorMessage && (
                        <div className="text-[11px] text-[#f05050] mt-0.5">{q.errorMessage}</div>
                      )}
                    </td>
                    <td>
                      <ArrLabel kind="default">{q.mediaVertical}</ArrLabel>
                    </td>
                    <td>
                      <ArrLabel
                        kind={
                          q.status === 'Completed'
                            ? 'success'
                            : q.status === 'Failed' || q.isStalled
                              ? 'danger'
                              : 'primary'
                        }
                      >
                        {q.status}
                      </ArrLabel>
                    </td>
                    <td>{formatBytes(q.totalBytes)}</td>
                    <td className="font-mono text-[11px] text-[#909293] max-w-xs truncate">
                      {q.outputPath || '-'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </FieldSet>

      <FieldSet legend={`Processed Download Verifications & Imports (${imports.length})`}>
        {imports.length === 0 ? (
          <div className="text-[13px] text-[#909293]">
            No completed downloads processed yet. Click &ldquo;Import Completed&rdquo; in the toolbar to verify and hardlink finished jobs.
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="arr-table">
              <thead>
                <tr>
                  <th>Title</th>
                  <th>Verdict</th>
                  <th>Import Mode</th>
                  <th>Destination / Notes</th>
                </tr>
              </thead>
              <tbody>
                {imports.map((imp) => (
                  <tr key={imp.id}>
                    <td className="font-mono text-[12px] text-[#eeeeee]">{imp.title}</td>
                    <td>
                      <ArrLabel
                        kind={
                          imp.verdict.startsWith('VerifiedPass')
                            ? 'success'
                            : imp.verdict === 'QuarantineReview'
                              ? 'warning'
                              : 'danger'
                        }
                      >
                        {imp.verdict}
                      </ArrLabel>
                    </td>
                    <td>
                      {imp.hardlinked ? (
                        <ArrLabel kind="primary">
                          <CheckCircle2 className="h-3 w-3 inline mr-1" />
                          Hardlink
                        </ArrLabel>
                      ) : imp.destinationPath ? (
                        <ArrLabel kind="default">Copy</ArrLabel>
                      ) : (
                        <span className="text-[#909293]">-</span>
                      )}
                    </td>
                    <td className="text-[12px] text-[#cccccc]">
                      {imp.destinationPath ? (
                        <div className="font-mono text-[11px] text-arr-accent">{imp.destinationPath}</div>
                      ) : null}
                      <div className="text-[11px] text-[#909293]">{imp.message}</div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </FieldSet>

      {(showAddClientPicker || editingClientId !== null) && (
        <ArrModal
          title={
            editingClientId !== null
              ? `Edit Download Client — ${clientName || selectedDriver?.displayName || 'Client'}`
              : selectedDriver
                ? `Add Download Client — ${selectedDriver.displayName}`
                : 'Add Download Client'
          }
          onClose={onCloseClientModal}
          maxWidth="max-w-3xl"
          footer={
            selectedDriver ? (
              <div className="w-full flex items-center justify-between gap-2">
                <div>
                  {editingClientId !== null && (
                    <ArrButton
                      variant="danger"
                      busy={isDeleteClientPending}
                      onClick={() => onDeleteClient(editingClientId)}
                    >
                      <Trash2 className="h-3.5 w-3.5" /> Delete
                    </ArrButton>
                  )}
                </div>
                <div className="flex items-center gap-2 ml-auto">
                  {editingClientId === null && (
                    <ArrButton onClick={() => onSelectDriver(null)}>Back</ArrButton>
                  )}
                  {onTestClient && (
                    <ArrButton
                      busy={isTestClientPending}
                      onClick={onTestClient}
                    >
                      <FlaskConical className="h-3.5 w-3.5" /> Test Connection
                    </ArrButton>
                  )}
                  <ArrButton onClick={onCloseClientModal}>Cancel</ArrButton>
                  <ArrButton
                    variant="primary"
                    busy={isSaveClientPending}
                    onClick={onSaveClient}
                  >
                    Save
                  </ArrButton>
                </div>
              </div>
            ) : (
              <ArrButton onClick={onCloseClientModal}>Close</ArrButton>
            )
          }
        >
          {!selectedDriver ? (
            <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 gap-3">
              {drivers.map((dr) => (
                <button
                  key={dr.implementation}
                  type="button"
                  onClick={() => {
                    onSelectDriver(dr);
                    onChangeClientName(dr.displayName);
                    onChangeClientHost('localhost');
                    onChangeClientPort(String(dr.defaultPort));
                  }}
                  className="text-left p-3.5 bg-arr-card hover:bg-[#3d3d3d] border border-[#444444] rounded-[4px] transition-colors cursor-pointer"
                >
                  <div className="flex items-center justify-between gap-2 mb-1.5">
                    <span className="text-[14px] font-medium text-[#eeeeee] truncate">{dr.displayName}</span>
                    <ArrLabel kind={dr.protocol.toLowerCase() === 'usenet' ? 'usenet' : 'torrent'}>
                      {dr.protocol}
                    </ArrLabel>
                  </div>
                  <div className="text-[11px] font-mono text-[#909293]">Default Port: {dr.defaultPort}</div>
                </button>
              ))}
            </div>
          ) : (
            <div>
              {clientTestStatus && (
                <ArrAlert kind={clientTestStatus.ok ? 'success' : 'danger'} className="mb-4">
                  {clientTestStatus.message}
                </ArrAlert>
              )}
              <FormGroup label="Enabled">
                <label className="inline-flex items-center gap-2 pt-1.5 text-[13px] cursor-pointer">
                  <input
                    type="checkbox"
                    checked={clientIsEnabled}
                    onChange={(e) => onChangeClientIsEnabled?.(e.target.checked)}
                  />
                  Enable this download client
                </label>
              </FormGroup>
              <FormGroup label="Name">
                <input
                  value={clientName}
                  onChange={(e) => onChangeClientName(e.target.value)}
                  className="arr-input"
                />
              </FormGroup>
              <FormGroup label="Host" helpText="Hostname or IP address of the download client.">
                <input
                  value={clientHost}
                  onChange={(e) => onChangeClientHost(e.target.value)}
                  placeholder="localhost"
                  className="arr-input font-mono"
                />
              </FormGroup>
              <FormGroup label="Port">
                <input
                  type="number"
                  value={clientPort}
                  onChange={(e) => onChangeClientPort(e.target.value)}
                  className="arr-input font-mono"
                />
              </FormGroup>
              <FormGroup label="URL Base" helpText="Optional URL path prefix (e.g. /sabnzbd or /qbittorrent).">
                <input
                  value={clientUrlBase}
                  onChange={(e) => onChangeClientUrlBase?.(e.target.value)}
                  placeholder="/qbittorrent"
                  className="arr-input font-mono"
                />
              </FormGroup>
              <FormGroup label="API Key" helpText="Required for SABnzbd, Tribler, Aria2 secret, etc.">
                <input
                  type="password"
                  value={clientApiKey}
                  onChange={(e) => onChangeClientApiKey(e.target.value)}
                  placeholder={
                    editingClientId !== null && clientHasSavedKey
                      ? '•••••••• (saved — leave blank to keep)'
                      : 'API Key (if required by client)'
                  }
                  className="arr-input font-mono"
                />
              </FormGroup>
              <FormGroup label="Username">
                <input
                  value={clientUsername}
                  onChange={(e) => onChangeClientUsername(e.target.value)}
                  placeholder="Optional username (NZBGet, qBittorrent, etc.)"
                  className="arr-input"
                />
              </FormGroup>
              <FormGroup label="Password">
                <input
                  type="password"
                  value={clientPassword}
                  onChange={(e) => onChangeClientPassword(e.target.value)}
                  placeholder={
                    editingClientId !== null && clientHasSavedPass
                      ? '•••••••• (saved — leave blank to keep)'
                      : 'Optional password'
                  }
                  className="arr-input"
                />
              </FormGroup>
              <FormGroup label="Use SSL">
                <label className="inline-flex items-center gap-2 pt-1.5 text-[13px] cursor-pointer">
                  <input
                    type="checkbox"
                    checked={clientUseSsl}
                    onChange={(e) => onChangeClientUseSsl(e.target.checked)}
                  />
                  Connect via HTTPS
                </label>
              </FormGroup>
              <FormGroup label="Priority" helpText="1 (highest priority) to 50.">
                <input
                  type="number"
                  min="1"
                  max="50"
                  value={clientPriority}
                  onChange={(e) => onChangeClientPriority?.(e.target.value)}
                  className="arr-input font-mono !w-28"
                />
              </FormGroup>
              {(selectedDriver.implementation.toLowerCase().includes('blackhole') ||
                selectedDriver.implementation.toLowerCase().includes('watch') ||
                Boolean(clientWatchFolder)) && (
                <FormGroup
                  label="Watch Folder"
                  helpText="Directory where .torrent / .nzb files are written for blackhole watch-folder clients."
                >
                  <PathBrowserInput
                    value={clientWatchFolder}
                    onChange={(v) => onChangeClientWatchFolder?.(v)}
                    placeholder="/downloads/watch"
                    label="Watch Folder"
                    modalTitle="Select Watch Folder"
                    allowCreateFolder
                  />
                </FormGroup>
              )}
              <FormGroup
                label="Category Mappings"
                helpText="Categories assigned per media vertical in the download client."
              >
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
                  {['movies', 'tv', 'anime', 'music', 'ebooks', 'audiobooks', 'comics'].map((vert) => (
                    <div key={vert} className="flex items-center gap-2">
                      <span className="w-24 text-[11px] font-mono text-[#909293] uppercase">{vert}</span>
                      <input
                        value={clientCategoryMappings[vert] ?? ''}
                        onChange={(e) =>
                          onChangeClientCategoryMappings?.({
                            ...clientCategoryMappings,
                            [vert]: e.target.value,
                          })
                        }
                        placeholder={`managerr-${vert}`}
                        className="arr-input !h-[28px] !py-0.5 font-mono text-[12px] flex-1"
                      />
                    </div>
                  ))}
                </div>
              </FormGroup>
              <FormGroup label="Completed Download Handling">
                <div className="space-y-2 pt-1 text-[13px]">
                  <label className="flex items-center gap-2 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={clientRemoveCompleted}
                      onChange={(e) => onChangeClientRemoveCompleted?.(e.target.checked)}
                    />
                    <span>Remove completed downloads from client history</span>
                  </label>
                  <label className="flex items-center gap-2 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={clientRemoveFailed}
                      onChange={(e) => onChangeClientRemoveFailed?.(e.target.checked)}
                    />
                    <span>Remove failed downloads from client</span>
                  </label>
                </div>
              </FormGroup>
            </div>
          )}
        </ArrModal>
      )}

      {selectedTrackedForImport !== null && (
        <ArrModal
          title={`Manual Override / Retry Import — ${selectedTrackedForImport.title}`}
          onClose={onCloseManualImportModal}
          maxWidth="max-w-2xl"
          footer={
            <>
              <ArrButton onClick={onCloseManualImportModal}>Cancel</ArrButton>
              <ArrButton
                variant="primary"
                busy={isRetryImportPending}
                onClick={onExecuteManualImport}
              >
                <RotateCw className="h-3.5 w-3.5" /> Import Now
              </ArrButton>
            </>
          }
        >
          <div className="space-y-4">
            {manualImportError && (
              <ArrAlert kind="danger">
                <div className="font-semibold">{manualImportError}</div>
              </ArrAlert>
            )}

            {selectedTrackedForImport.statusMessages &&
              selectedTrackedForImport.statusMessages.length > 0 && (
                <ArrAlert kind="warning">
                  <div className="font-bold mb-1 flex items-center gap-1.5">
                    <AlertTriangle className="h-4 w-4 text-[#ffa500]" />
                    CDH Pipeline Status / Blocked Warnings:
                  </div>
                  <ul className="list-disc list-inside text-[12px] space-y-0.5 font-mono">
                    {selectedTrackedForImport.statusMessages.map((msg, i) => (
                      <li key={i}>{msg}</li>
                    ))}
                  </ul>
                </ArrAlert>
              )}

            <div className="bg-[#1b1d1f] p-3 rounded border border-[#2e3236] space-y-1.5 text-[12px]">
              <div className="grid grid-cols-[140px_1fr] gap-2">
                <span className="text-[#909293]">Download Client:</span>
                <span className="font-semibold text-[#eeeeee]">
                  {selectedTrackedForImport.clientName} ({selectedTrackedForImport.protocol})
                </span>
              </div>
              <div className="grid grid-cols-[140px_1fr] gap-2">
                <span className="text-[#909293]">Media Vertical:</span>
                <span className="font-mono text-arr-accent">
                  {selectedTrackedForImport.mediaVertical}
                </span>
              </div>
              <div className="grid grid-cols-[140px_1fr] gap-2">
                <span className="text-[#909293]">Current State:</span>
                <span>
                  <ArrLabel kind="default">{selectedTrackedForImport.state}</ArrLabel>
                </span>
              </div>
              <div className="grid grid-cols-[140px_1fr] gap-2">
                <span className="text-[#909293]">Output Path:</span>
                <span className="font-mono text-[#909293] break-all">
                  {selectedTrackedForImport.outputPath || 'Default client download folder'}
                </span>
              </div>
            </div>

            <FormGroup
              label="Target Media Work (Catalog)"
              helpText="Map this download directly to an existing MediaWork in your library."
            >
              <select
                className="arr-input"
                value={overrideWorkId ?? ''}
                onChange={(e) =>
                  onChangeOverrideWorkId(e.target.value ? Number(e.target.value) : null)
                }
              >
                <option value="">
                  {selectedTrackedForImport.workTitle
                    ? `-- Keep Current (${selectedTrackedForImport.workTitle}) --`
                    : '-- Select Matched Media Work (Optional) --'}
                </option>
                {catalogWorks.map((w) => (
                  <option key={w.id} value={w.id}>
                    {w.title} {w.year ? `(${w.year})` : ''} [{w.workKind}]
                  </option>
                ))}
              </select>
            </FormGroup>

            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
              <FormGroup
                label="Season Number"
                helpText="Series season number (e.g. 1). Leave empty for single movie or auto-detect."
              >
                <input
                  type="number"
                  min="0"
                  value={overrideSeason}
                  onChange={(e) => onChangeOverrideSeason(e.target.value)}
                  placeholder="e.g. 1"
                  className="arr-input"
                />
              </FormGroup>

              <FormGroup
                label="Episode Number(s)"
                helpText="Comma-separated episode index (e.g. 13 or 1,2)."
              >
                <input
                  value={overrideEpisodes}
                  onChange={(e) => onChangeOverrideEpisodes(e.target.value)}
                  placeholder="e.g. 13 or 1,2"
                  className="arr-input font-mono"
                />
              </FormGroup>
            </div>

            <FormGroup
              label="Confirmed Audio Languages"
              helpText="Comma-separated ISO codes (e.g. de,en) to override release audio language detection."
            >
              <input
                value={confirmedAudioLangs}
                onChange={(e) => onChangeConfirmedAudioLangs(e.target.value)}
                placeholder="de,en"
                className="arr-input font-mono"
              />
            </FormGroup>

            <FormGroup
              label="Upgrade Guard Override"
              helpText="Bypass custom formats and quality score comparison guards to force import."
            >
              <label className="inline-flex items-center gap-2 pt-1 text-[13px] text-[#cccccc] cursor-pointer">
                <input
                  type="checkbox"
                  checked={bypassUpgrade}
                  onChange={(e) => onChangeBypassUpgrade(e.target.checked)}
                  className="rounded bg-[#1a1a1a] border-[#4a5158] text-arr-primary accent-arr-accent"
                />
                <span>Bypass Quality and Score Upgrade Check (Force Import)</span>
              </label>
            </FormGroup>
          </div>
        </ArrModal>
      )}

      {showAddRpmModal && (
        <ArrModal
          title="Add Remote Path Mapping"
          onClose={() => setShowAddRpmModal(false)}
          maxWidth="max-w-xl"
          footer={
            <>
              <ArrButton type="button" onClick={() => setShowAddRpmModal(false)}>
                Cancel
              </ArrButton>
              <ArrButton
                variant="primary"
                type="button"
                busy={isAddRemotePathPending}
                disabled={!rpmHost.trim() || !rpmRemotePath.trim() || !rpmLocalPath.trim()}
                onClick={async () => {
                  try {
                    await onAddRemotePath({
                      host: rpmHost.trim(),
                      remotePath: rpmRemotePath.trim(),
                      localPath: rpmLocalPath.trim(),
                      downloadClientId: rpmClientId === 'all' ? null : Number(rpmClientId),
                    });
                    setShowAddRpmModal(false);
                  } catch (err: unknown) {
                    setRpmError(err instanceof Error ? err.message : 'Failed to save remote path mapping');
                  }
                }}
              >
                Save Mapping
              </ArrButton>
            </>
          }
        >
          {rpmError && (
            <ArrAlert kind="danger" className="mb-4">
              {rpmError}
            </ArrAlert>
          )}

          <FormGroup
            label="Host"
            required
            helpText="Host or IP of the download client (e.g. localhost, 192.168.1.50, or transmission)."
          >
            <input
              type="text"
              value={rpmHost}
              onChange={(e) => setRpmHost(e.target.value)}
              placeholder="e.g. 192.168.1.50 or localhost"
              className="arr-input font-mono"
            />
          </FormGroup>

          <FormGroup
            label="Remote Path"
            required
            helpText="The path reported by the download client (e.g. /downloads/complete/ or D:\downloads\)."
          >
            <input
              type="text"
              value={rpmRemotePath}
              onChange={(e) => setRpmRemotePath(e.target.value)}
              placeholder="/downloads/ or D:\downloads\"
              className="arr-input font-mono"
            />
          </FormGroup>

          <FormGroup
            label="Local Path"
            required
            helpText="The local path accessible to Managerr containers."
          >
            <PathBrowserInput
              value={rpmLocalPath}
              onChange={setRpmLocalPath}
              placeholder="/downloads/usenet/ or /downloads/torrents/"
              label="Local Path"
              modalTitle="Select Local Download Folder"
              allowCreateFolder={false}
            />
          </FormGroup>

          <FormGroup
            label="Download Client"
            helpText="Apply this mapping only to a specific download client or all clients."
          >
            <select
              value={rpmClientId}
              onChange={(e) => setRpmClientId(e.target.value)}
              className="arr-input"
            >
              <option value="all">All Download Clients</option>
              {clients.map((c) => (
                <option key={c.id} value={String(c.id)}>
                  {c.name} ({c.host})
                </option>
              ))}
            </select>
          </FormGroup>
        </ArrModal>
      )}
    </>
  );
}
