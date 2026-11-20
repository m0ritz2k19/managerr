import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Bell,
  Check,
  Edit2,
  ListFilter,
  Play,
  Plus,
  RefreshCw,
  Send,
  Trash2,
  X,
} from 'lucide-react';
import { apiFetch } from '../lib/api';
import {
  ArrAddCard,
  ArrAlert,
  ArrButton,
  ArrLabel,
  ArrModal,
  ArrSettingsCard,
  FieldSet,
  FormGroup,
  PageContentBody,
  PageToolbar,
  PageToolbarButton,
  PageToolbarSeparator,
} from './arr/ArrPrimitives';

// ============================================================================
// Types & Contracts for Notifications & Import Lists (Issue #32)
// ============================================================================

export type EcosystemSubSection = 'connect' | 'import-lists';

export type NotificationProviderType =
  | 'discord'
  | 'telegram'
  | 'gotify'
  | 'ntfy'
  | 'apprise'
  | 'webhook'
  | 'plex'
  | 'jellyfin';

export interface NotificationConnectionDto {
  id: number;
  name: string;
  providerType: NotificationProviderType;
  settings: Record<string, string>;
  onGrab: boolean;
  onImport: boolean;
  onUpgrade: boolean;
  onRename: boolean;
  onDelete: boolean;
  onHealthIssue: boolean;
  onApplicationUpdate: boolean;
  tags: string[];
  isEnabled: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface NotificationTestResult {
  success: boolean;
  providerType: string;
  statusCode: number | null;
  message: string;
}

export type ImportListType =
  | 'tmdb_popular'
  | 'tmdb_trending'
  | 'stevenlu'
  | 'anilist_trending'
  | 'trakt_popular'
  | 'openlibrary_subject'
  | 'custom_json';

export interface ImportListSummaryDto {
  id: number;
  name: string;
  listType: ImportListType;
  mediaVertical: string;
  libraryId: number;
  qualityProfileId: number | null;
  monitorMode: string;
  minimumAvailability: string;
  searchOnAdd: boolean;
  settings: Record<string, string>;
  tags: string[];
  syncIntervalHours: number;
  isEnabled: boolean;
  lastSyncedAt: string | null;
  lastResultMessage: string | null;
}

export interface ImportListExclusionDto {
  id: number;
  externalKey: string;
  title: string;
  mediaVertical: string;
  year: number | null;
  createdAt?: string;
}

export interface ImportListSyncResult {
  listId: number;
  listName: string;
  fetchedCount: number;
  addedCount: number;
  skippedExistingCount: number;
  skippedExcludedCount: number;
  message: string;
}

interface LibraryOption {
  id: number;
  name: string;
  mediaVertical?: string;
  libraryType?: string;
}

interface ProfileOption {
  id: number;
  name: string;
}

const PROVIDER_METADATA: Record<
  NotificationProviderType,
  { label: string; badgeKind: 'purple' | 'primary' | 'success' | 'warning' | 'default'; description: string }
> = {
  discord: { label: 'Discord', badgeKind: 'purple', description: 'Discord rich webhook notifications with embed cards' },
  telegram: { label: 'Telegram', badgeKind: 'primary', description: 'Telegram bot notifications with markdown' },
  gotify: { label: 'Gotify', badgeKind: 'primary', description: 'Self-hosted Gotify push notification server' },
  ntfy: { label: 'ntfy', badgeKind: 'primary', description: 'Simple HTTP-based pub-sub push notifications' },
  apprise: { label: 'Apprise', badgeKind: 'success', description: 'Apprise multi-service notification gateway' },
  webhook: { label: 'Webhook', badgeKind: 'default', description: 'Generic HTTP POST / PUT webhook endpoint with HMAC signing' },
  plex: { label: 'Plex', badgeKind: 'warning', description: 'Trigger Plex Media Server library section scans on import/upgrade' },
  jellyfin: { label: 'Jellyfin', badgeKind: 'purple', description: 'Trigger Jellyfin library scans and active push alerts' },
};

const IMPORT_LIST_TYPE_INFO: Record<
  ImportListType,
  { label: string; defaultVertical: string; description: string }
> = {
  tmdb_popular: {
    label: 'TMDB — Popular',
    defaultVertical: 'movies',
    description: 'Import top popular movies or series from The Movie Database',
  },
  tmdb_trending: {
    label: 'TMDB — Trending',
    defaultVertical: 'movies',
    description: 'Weekly trending movies or TV series on TMDB',
  },
  stevenlu: {
    label: 'StevenLu Popular Movies',
    defaultVertical: 'movies',
    description: 'Aggregated top movie releases curated by Steven Lu',
  },
  anilist_trending: {
    label: 'AniList — Trending Anime',
    defaultVertical: 'anime',
    description: 'Trending and seasonal anime releases from AniList',
  },
  trakt_popular: {
    label: 'Trakt — Popular',
    defaultVertical: 'movies',
    description: 'Trakt.tv popular community watchlists and charts',
  },
  openlibrary_subject: {
    label: 'OpenLibrary — Genre Subject',
    defaultVertical: 'ebooks',
    description: 'Subject-specific book recommendations from OpenLibrary',
  },
  custom_json: {
    label: 'Custom JSON / Webhook Feed',
    defaultVertical: 'movies',
    description: 'Custom URL returning JSON array of titles or external keys',
  },
};

export function EcosystemConnectAndListsView({
  section = 'connect',
}: {
  section?: EcosystemSubSection;
}) {
  const qc = useQueryClient();

  // ============================================================================
  // NOTIFICATIONS (CONNECT) STATE & MUTATIONS
  // ============================================================================
  const notificationsQuery = useQuery({
    queryKey: ['notifications'],
    queryFn: () => apiFetch<NotificationConnectionDto[]>('/api/v1/notifications'),
    enabled: section === 'connect',
  });

  const [notifModalOpen, setNotifModalOpen] = useState(false);
  const [editingNotifId, setEditingNotifId] = useState<number | null>(null);
  const [notifName, setNotifName] = useState('');
  const [notifProvider, setNotifProvider] = useState<NotificationProviderType>('discord');
  const [notifEnabled, setNotifEnabled] = useState(true);
  const [notifSettings, setNotifSettings] = useState<Record<string, string>>({});
  const [notifTags, setNotifTags] = useState('');
  const [onGrab, setOnGrab] = useState(true);
  const [onImport, setOnImport] = useState(true);
  const [onUpgrade, setOnUpgrade] = useState(true);
  const [onRename, setOnRename] = useState(false);
  const [onDelete, setOnDelete] = useState(false);
  const [onHealthIssue, setOnHealthIssue] = useState(true);
  const [onApplicationUpdate, setOnApplicationUpdate] = useState(false);
  const [testResultAlert, setTestResultAlert] = useState<NotificationTestResult | null>(null);

  const saveNotifMutation = useMutation({
    mutationFn: async () => {
      const payload = {
        name: notifName.trim(),
        providerType: notifProvider,
        settings: notifSettings,
        onGrab,
        onImport,
        onUpgrade,
        onRename,
        onDelete,
        onHealthIssue,
        onApplicationUpdate,
        tags: notifTags
          .split(',')
          .map((t) => t.trim())
          .filter(Boolean),
        isEnabled: notifEnabled,
      };

      if (editingNotifId !== null) {
        return apiFetch<NotificationConnectionDto>(`/api/v1/notifications/${editingNotifId}`, {
          method: 'PUT',
          body: JSON.stringify(payload),
        });
      }
      return apiFetch<NotificationConnectionDto>('/api/v1/notifications', {
        method: 'POST',
        body: JSON.stringify(payload),
      });
    },
    onSuccess: () => {
      setNotifModalOpen(false);
      qc.invalidateQueries({ queryKey: ['notifications'] });
    },
  });

  const deleteNotifMutation = useMutation({
    mutationFn: (id: number) => apiFetch(`/api/v1/notifications/${id}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['notifications'] }),
  });

  const testNotifMutation = useMutation({
    mutationFn: async (id?: number) => {
      if (id !== undefined) {
        return apiFetch<NotificationTestResult>(`/api/v1/notifications/${id}/test`, {
          method: 'POST',
        });
      }
      return apiFetch<NotificationTestResult>('/api/v1/notifications/test', {
        method: 'POST',
        body: JSON.stringify({
          name: notifName.trim() || 'Test Connection',
          providerType: notifProvider,
          settings: notifSettings,
        }),
      });
    },
    onSuccess: (res) => {
      setTestResultAlert(res);
    },
    onError: (err: unknown) => {
      setTestResultAlert({
        success: false,
        providerType: notifProvider,
        statusCode: null,
        message: err instanceof Error ? err.message : 'Test failed',
      });
    },
  });

  function openNewNotif() {
    setEditingNotifId(null);
    setNotifName('');
    setNotifProvider('discord');
    setNotifEnabled(true);
    setNotifSettings({
      webhookUrl: '',
      username: 'Managerr',
    });
    setNotifTags('');
    setOnGrab(true);
    setOnImport(true);
    setOnUpgrade(true);
    setOnRename(false);
    setOnDelete(false);
    setOnHealthIssue(true);
    setOnApplicationUpdate(false);
    setTestResultAlert(null);
    setNotifModalOpen(true);
  }

  function openEditNotif(conn: NotificationConnectionDto) {
    setEditingNotifId(conn.id);
    setNotifName(conn.name);
    setNotifProvider(conn.providerType);
    setNotifEnabled(conn.isEnabled);
    setNotifSettings(conn.settings ?? {});
    setNotifTags(conn.tags?.join(', ') ?? '');
    setOnGrab(conn.onGrab);
    setOnImport(conn.onImport);
    setOnUpgrade(conn.onUpgrade);
    setOnRename(conn.onRename);
    setOnDelete(conn.onDelete);
    setOnHealthIssue(conn.onHealthIssue);
    setOnApplicationUpdate(conn.onApplicationUpdate);
    setTestResultAlert(null);
    setNotifModalOpen(true);
  }

  function updateSetting(key: string, value: string) {
    setNotifSettings((prev) => ({ ...prev, [key]: value }));
  }

  // ============================================================================
  // IMPORT LISTS & EXCLUSIONS STATE & MUTATIONS
  // ============================================================================
  const importListsQuery = useQuery({
    queryKey: ['import-lists'],
    queryFn: () => apiFetch<ImportListSummaryDto[]>('/api/v1/import-lists'),
    enabled: section === 'import-lists',
  });

  const exclusionsQuery = useQuery({
    queryKey: ['import-list-exclusions'],
    queryFn: () => apiFetch<ImportListExclusionDto[]>('/api/v1/import-lists/exclusions'),
    enabled: section === 'import-lists',
  });

  const librariesQuery = useQuery({
    queryKey: ['libraries'],
    queryFn: () => apiFetch<LibraryOption[]>('/api/v1/libraries'),
    enabled: section === 'import-lists',
  });

  const profilesQuery = useQuery({
    queryKey: ['profiles'],
    queryFn: () => apiFetch<ProfileOption[]>('/api/v1/profiles'),
    enabled: section === 'import-lists',
  });

  const [listModalOpen, setListModalOpen] = useState(false);
  const [editingListId, setEditingListId] = useState<number | null>(null);
  const [listName, setListName] = useState('');
  const [listType, setListType] = useState<ImportListType>('tmdb_popular');
  const [listVertical, setListVertical] = useState('movies');
  const [listLibraryId, setListLibraryId] = useState<number>(0);
  const [listProfileId, setListProfileId] = useState<number | null>(null);
  const [listMonitorMode, setListMonitorMode] = useState('all');
  const [listMinAvailability, setListMinAvailability] = useState('released');
  const [listSearchOnAdd, setListSearchOnAdd] = useState(true);
  const [listSyncHours, setListSyncHours] = useState(24);
  const [listEnabled, setListEnabled] = useState(true);
  const [listSettings, setListSettings] = useState<Record<string, string>>({});
  const [listTags, setListTags] = useState('');
  const [syncToast, setSyncToast] = useState<string | null>(null);

  // Quick exclusion state
  const [exKey, setExKey] = useState('');
  const [exTitle, setExTitle] = useState('');
  const [exVertical, setExVertical] = useState('movies');
  const [exYear, setExYear] = useState('');

  const saveListMutation = useMutation({
    mutationFn: async () => {
      const payload = {
        name: listName.trim(),
        listType,
        mediaVertical: listVertical,
        libraryId: listLibraryId || (librariesQuery.data?.[0]?.id ?? 1),
        qualityProfileId: listProfileId,
        monitorMode: listMonitorMode,
        minimumAvailability: listMinAvailability,
        searchOnAdd: listSearchOnAdd,
        settings: listSettings,
        tags: listTags
          .split(',')
          .map((t) => t.trim())
          .filter(Boolean),
        syncIntervalHours: Number(listSyncHours) || 24,
        isEnabled: listEnabled,
      };

      if (editingListId !== null) {
        return apiFetch<ImportListSummaryDto>(`/api/v1/import-lists/${editingListId}`, {
          method: 'PUT',
          body: JSON.stringify(payload),
        });
      }
      return apiFetch<ImportListSummaryDto>('/api/v1/import-lists', {
        method: 'POST',
        body: JSON.stringify(payload),
      });
    },
    onSuccess: () => {
      setListModalOpen(false);
      qc.invalidateQueries({ queryKey: ['import-lists'] });
    },
  });

  const deleteListMutation = useMutation({
    mutationFn: (id: number) => apiFetch(`/api/v1/import-lists/${id}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['import-lists'] }),
  });

  const syncListMutation = useMutation({
    mutationFn: (id: number) =>
      apiFetch<ImportListSyncResult>(`/api/v1/import-lists/${id}/sync`, { method: 'POST' }),
    onSuccess: (data) => {
      setSyncToast(
        `Synced "${data.listName}": ${data.addedCount} added, ${data.skippedExistingCount} existing, ${data.skippedExcludedCount} excluded.`,
      );
      qc.invalidateQueries({ queryKey: ['import-lists'] });
    },
  });

  const syncAllMutation = useMutation({
    mutationFn: () => apiFetch<ImportListSyncResult[]>('/api/v1/import-lists/sync-all', { method: 'POST' }),
    onSuccess: (reports) => {
      const count = Array.isArray(reports) ? reports.length : 0;
      setSyncToast(
        count > 0
          ? `Synced ${count} due import list${count === 1 ? '' : 's'}.`
          : 'All enabled import lists are within their sync interval (no lists due).',
      );
      qc.invalidateQueries({ queryKey: ['import-lists'] });
    },
  });

  const addExclusionMutation = useMutation({
    mutationFn: async () => {
      return apiFetch<ImportListExclusionDto>('/api/v1/import-lists/exclusions', {
        method: 'POST',
        body: JSON.stringify({
          externalKey: exKey.trim(),
          title: exTitle.trim(),
          mediaVertical: exVertical,
          year: exYear ? Number(exYear) : null,
        }),
      });
    },
    onSuccess: () => {
      setExKey('');
      setExTitle('');
      setExYear('');
      qc.invalidateQueries({ queryKey: ['import-list-exclusions'] });
    },
  });

  const deleteExclusionMutation = useMutation({
    mutationFn: (id: number) =>
      apiFetch(`/api/v1/import-lists/exclusions/${id}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['import-list-exclusions'] }),
  });

  function openNewList() {
    setEditingListId(null);
    setListName('');
    setListType('tmdb_popular');
    setListVertical('movies');
    setListLibraryId(librariesQuery.data?.[0]?.id ?? 1);
    setListProfileId(profilesQuery.data?.[0]?.id ?? null);
    setListMonitorMode('all');
    setListMinAvailability('released');
    setListSearchOnAdd(true);
    setListSyncHours(24);
    setListEnabled(true);
    setListSettings({ limit: '50' });
    setListTags('');
    setListModalOpen(true);
  }

  function openEditList(item: ImportListSummaryDto) {
    setEditingListId(item.id);
    setListName(item.name);
    setListType(item.listType);
    setListVertical(item.mediaVertical);
    setListLibraryId(item.libraryId);
    setListProfileId(item.qualityProfileId);
    setListMonitorMode(item.monitorMode);
    setListMinAvailability(item.minimumAvailability);
    setListSearchOnAdd(item.searchOnAdd);
    setListSyncHours(item.syncIntervalHours);
    setListEnabled(item.isEnabled);
    setListSettings(item.settings ?? {});
    setListTags(item.tags?.join(', ') ?? '');
    setListModalOpen(true);
  }

  return (
    <div className="flex flex-col flex-1">
      <PageToolbar
        left={
          <>
            <PageToolbarButton
              icon={RefreshCw}
              label="Refresh"
              spinning={
                notificationsQuery.isFetching ||
                importListsQuery.isFetching ||
                exclusionsQuery.isFetching
              }
              onClick={() => {
                if (section === 'connect') void notificationsQuery.refetch();
                if (section === 'import-lists') {
                  void importListsQuery.refetch();
                  void exclusionsQuery.refetch();
                }
              }}
            />

            {section === 'connect' && (
              <>
                <PageToolbarSeparator />
                <PageToolbarButton
                  icon={Plus}
                  label="Add Notification"
                  onClick={openNewNotif}
                />
              </>
            )}

            {section === 'import-lists' && (
              <>
                <PageToolbarSeparator />
                <PageToolbarButton
                  icon={Plus}
                  label="Add Import List"
                  onClick={openNewList}
                />
                <PageToolbarButton
                  icon={Play}
                  label="Sync All"
                  spinning={syncAllMutation.isPending}
                  onClick={() => syncAllMutation.mutate()}
                />
              </>
            )}
          </>
        }
      />

      <PageContentBody>
        {syncToast && (
          <ArrAlert kind="info" className="mb-4">
            <div className="flex items-center justify-between">
              <span>{syncToast}</span>
              <button
                type="button"
                onClick={() => setSyncToast(null)}
                className="text-[#909293] hover:text-white"
              >
                <X className="h-4 w-4" />
              </button>
            </div>
          </ArrAlert>
        )}

        {/* ================================================================== */}
        {/* SECTION: CONNECT / NOTIFICATION PROVIDERS                          */}
        {/* ================================================================== */}
        {section === 'connect' && (
          <FieldSet
            legend={
              <div className="flex items-center gap-2">
                <Bell className="h-5 w-5 text-arr-accent" />
                <span>Connect &amp; Notification Providers</span>
              </div>
            }
          >
            <p className="text-[13px] text-[#909293] mb-5">
              Send notifications to Discord, Telegram, Gotify, ntfy, Apprise, or trigger media library scans on Plex &amp; Jellyfin when releases are grabbed, imported, upgraded, or renamed.
            </p>

            <div className="flex flex-wrap gap-4">
              {(notificationsQuery.data ?? []).map((conn) => {
                const meta = PROVIDER_METADATA[conn.providerType] ?? {
                  label: conn.providerType,
                  badgeKind: 'default',
                  description: '',
                };

                const triggerCount = [
                  conn.onGrab,
                  conn.onImport,
                  conn.onUpgrade,
                  conn.onRename,
                  conn.onDelete,
                  conn.onHealthIssue,
                  conn.onApplicationUpdate,
                ].filter(Boolean).length;

                return (
                  <ArrSettingsCard
                    key={conn.id}
                    title={conn.name}
                    onClick={() => openEditNotif(conn)}
                    topRight={
                      <div className="flex items-center gap-1.5">
                        <ArrLabel kind={meta.badgeKind}>{meta.label}</ArrLabel>
                        {!conn.isEnabled && <ArrLabel kind="default">Disabled</ArrLabel>}
                      </div>
                    }
                    footer={
                      <div className="w-full flex items-center justify-between text-[11px] pt-1">
                        <div className="flex items-center gap-1">
                          <button
                            type="button"
                            title="Send Test Notification"
                            onClick={(e) => {
                              e.stopPropagation();
                              testNotifMutation.mutate(conn.id);
                            }}
                            className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-arr-card hover:bg-[#444444] border border-[#4a5158] text-arr-accent cursor-pointer"
                          >
                            <Send className="h-3 w-3" /> Test
                          </button>
                          <button
                            type="button"
                            title="Edit"
                            onClick={(e) => {
                              e.stopPropagation();
                              openEditNotif(conn);
                            }}
                            className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-arr-card hover:bg-[#444444] border border-[#4a5158] text-[#eeeeee] cursor-pointer"
                          >
                            <Edit2 className="h-3 w-3" /> Edit
                          </button>
                        </div>
                        <span className="text-[#888888]">
                          {triggerCount} {triggerCount === 1 ? 'trigger' : 'triggers'}
                        </span>
                      </div>
                    }
                  >
                    <div className="space-y-1.5">
                      <div className="text-[11px] text-[#909293] truncate">
                        {conn.settings?.webhookUrl ||
                          conn.settings?.serverUrl ||
                          conn.settings?.apiUrl ||
                          conn.settings?.url ||
                          meta.description}
                      </div>

                      <div className="flex flex-wrap gap-1 pt-1">
                        {conn.onGrab && <ArrLabel kind="default" className="text-[10px]">Grab</ArrLabel>}
                        {conn.onImport && <ArrLabel kind="success" className="text-[10px]">Import</ArrLabel>}
                        {conn.onUpgrade && <ArrLabel kind="primary" className="text-[10px]">Upgrade</ArrLabel>}
                        {conn.onHealthIssue && <ArrLabel kind="danger" className="text-[10px]">Health</ArrLabel>}
                      </div>
                    </div>
                  </ArrSettingsCard>
                );
              })}

              <ArrAddCard onClick={openNewNotif} label="Add Notification Connection" />
            </div>
          </FieldSet>
        )}

        {/* ================================================================== */}
        {/* SECTION: IMPORT LISTS & EXCLUSIONS                                 */}
        {/* ================================================================== */}
        {section === 'import-lists' && (
          <div className="space-y-8">
            <FieldSet
              legend={
                <div className="flex items-center gap-2">
                  <ListFilter className="h-5 w-5 text-[#27c24c]" />
                  <span>Import Lists</span>
                </div>
              }
              action={
                <div className="flex items-center gap-2">
                  <ArrButton
                    variant="primary"
                    busy={syncAllMutation.isPending}
                    onClick={() => syncAllMutation.mutate()}
                  >
                    <Play className="h-3.5 w-3.5" /> Sync All
                  </ArrButton>
                  <ArrButton variant="success" onClick={openNewList}>
                    <Plus className="h-3.5 w-3.5" /> Add Import List
                  </ArrButton>
                </div>
              }
            >
              <p className="text-[13px] text-[#909293] mb-4">
                Automate your media library by automatically importing trending movies, anime, popular series, or reading lists from community sources.
              </p>

              {(importListsQuery.data ?? []).length === 0 ? (
                <div className="bg-arr-card border border-arr-card-border rounded-[4px] p-6 text-center text-[13px] text-[#909293]">
                  No Import Lists configured yet. Click <strong>Add Import List</strong> to connect TMDB, AniList, Trakt, StevenLu, or Custom JSON.
                </div>
              ) : (
                <div className="overflow-x-auto border border-arr-card-border rounded-[4px]">
                  <table className="arr-table w-full">
                    <thead>
                      <tr>
                        <th>List Name</th>
                        <th>Type / Vertical</th>
                        <th>Target Library</th>
                        <th>Profile</th>
                        <th>Sync Schedule</th>
                        <th>Last Synced</th>
                        <th className="text-right">Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {(importListsQuery.data ?? []).map((list) => {
                        const typeInfo = IMPORT_LIST_TYPE_INFO[list.listType] ?? {
                          label: list.listType,
                          description: '',
                        };
                        const targetLib = librariesQuery.data?.find((l) => l.id === list.libraryId);
                        const targetProf = profilesQuery.data?.find((p) => p.id === list.qualityProfileId);

                        return (
                          <tr key={list.id} className="hover:bg-[#2b2b2b]">
                            <td>
                              <div className="font-semibold text-[#eeeeee] flex items-center gap-2">
                                <span>{list.name}</span>
                                {!list.isEnabled && <ArrLabel kind="default">Disabled</ArrLabel>}
                              </div>
                              {list.lastResultMessage && (
                                <div className="text-[11px] text-[#909293] truncate max-w-[320px]">
                                  {list.lastResultMessage}
                                </div>
                              )}
                            </td>
                            <td>
                              <div className="space-y-0.5">
                                <ArrLabel kind="primary">{typeInfo.label}</ArrLabel>
                                <div className="text-[11px] text-[#888888]">{list.mediaVertical}</div>
                              </div>
                            </td>
                            <td>
                              <span className="text-[13px] text-[#eeeeee]">
                                {targetLib?.name ?? `Library #${list.libraryId}`}
                              </span>
                            </td>
                            <td>
                              <span className="text-[12px] text-[#b8b8b8]">
                                {targetProf?.name ?? 'Default Profile'}
                              </span>
                            </td>
                            <td className="text-[12px] font-mono text-[#888888]">
                              Every {list.syncIntervalHours}h
                            </td>
                            <td>
                              <span className="text-[12px] text-[#909293]">
                                {list.lastSyncedAt
                                  ? new Date(list.lastSyncedAt).toLocaleString()
                                  : 'Never'}
                              </span>
                            </td>
                            <td className="text-right">
                              <div className="inline-flex items-center gap-1.5">
                                <ArrButton
                                  variant="primary"
                                  className="h-[28px] px-2 text-[11px]"
                                  busy={
                                    syncListMutation.isPending &&
                                    syncListMutation.variables === list.id
                                  }
                                  onClick={() => syncListMutation.mutate(list.id)}
                                >
                                  <Play className="h-3 w-3" /> Sync Now
                                </ArrButton>
                                <ArrButton
                                  className="h-[28px] px-2 text-[11px]"
                                  onClick={() => openEditList(list)}
                                >
                                  <Edit2 className="h-3 w-3" /> Edit
                                </ArrButton>
                                <ArrButton
                                  variant="danger"
                                  className="h-[28px] px-2 text-[11px]"
                                  busy={
                                    deleteListMutation.isPending &&
                                    deleteListMutation.variables === list.id
                                  }
                                  onClick={() => {
                                    if (window.confirm(`Delete Import List "${list.name}"?`)) {
                                      deleteListMutation.mutate(list.id);
                                    }
                                  }}
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
            </FieldSet>

            {/* ==================== EXCLUSIONS SECTION ==================== */}
            <FieldSet
              legend={
                <div className="flex items-center gap-2">
                  <X className="h-5 w-5 text-[#f05050]" />
                  <span>Import List Exclusions</span>
                </div>
              }
            >
              <p className="text-[13px] text-[#909293] mb-4">
                Titles and external IDs in this list will never be added by any automated import list, even if newly trending or popular.
              </p>

              {/* Quick Add Exclusion Form */}
              <div className="p-3.5 bg-[#252525] border border-arr-card-border rounded-[4px] mb-4">
                <div className="text-[12px] font-semibold text-[#cccccc] mb-2">Quick Add Exclusion</div>
                <div className="flex flex-wrap items-center gap-2">
                  <input
                    value={exKey}
                    onChange={(e) => setExKey(e.target.value)}
                    placeholder="External Key (e.g. tmdb:1234 or imdb:tt5678)"
                    className="arr-input !h-[32px] text-[12px] font-mono flex-1 min-w-[200px]"
                  />
                  <input
                    value={exTitle}
                    onChange={(e) => setExTitle(e.target.value)}
                    placeholder="Title (e.g. Unwanted Movie)"
                    className="arr-input !h-[32px] text-[12px] flex-1 min-w-[180px]"
                  />
                  <select
                    value={exVertical}
                    onChange={(e) => setExVertical(e.target.value)}
                    className="arr-input !h-[32px] text-[12px] !w-[130px]"
                  >
                    <option value="movies">Movies</option>
                    <option value="tv">TV Series</option>
                    <option value="anime">Anime</option>
                    <option value="music">Music</option>
                    <option value="ebooks">Books</option>
                  </select>
                  <input
                    type="number"
                    value={exYear}
                    onChange={(e) => setExYear(e.target.value)}
                    placeholder="Year"
                    className="arr-input !h-[32px] text-[12px] !w-[90px] font-mono"
                  />
                  <ArrButton
                    variant="success"
                    className="h-[32px] px-3 text-[12px]"
                    disabled={!exKey.trim() || !exTitle.trim()}
                    busy={addExclusionMutation.isPending}
                    onClick={() => addExclusionMutation.mutate()}
                  >
                    <Plus className="h-3.5 w-3.5" /> Exclude
                  </ArrButton>
                </div>
              </div>

              {(exclusionsQuery.data ?? []).length === 0 ? (
                <div className="text-[13px] text-[#888888]">No exclusions registered.</div>
              ) : (
                <div className="overflow-x-auto border border-arr-card-border rounded-[4px]">
                  <table className="arr-table w-full">
                    <thead>
                      <tr>
                        <th>External Key</th>
                        <th>Title</th>
                        <th>Vertical</th>
                        <th>Year</th>
                        <th className="text-right">Action</th>
                      </tr>
                    </thead>
                    <tbody>
                      {(exclusionsQuery.data ?? []).map((ex) => (
                        <tr key={ex.id} className="hover:bg-[#2b2b2b]">
                          <td className="font-mono text-[12px] text-arr-accent">{ex.externalKey}</td>
                          <td className="font-semibold text-[#eeeeee]">{ex.title}</td>
                          <td>
                            <ArrLabel kind="default">{ex.mediaVertical}</ArrLabel>
                          </td>
                          <td className="font-mono text-[12px]">{ex.year ?? '—'}</td>
                          <td className="text-right">
                            <ArrButton
                              variant="danger"
                              className="h-[26px] px-2 text-[11px]"
                              busy={
                                deleteExclusionMutation.isPending &&
                                deleteExclusionMutation.variables === ex.id
                              }
                              onClick={() => deleteExclusionMutation.mutate(ex.id)}
                            >
                              <Trash2 className="h-3 w-3" /> Remove
                            </ArrButton>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </FieldSet>
          </div>
        )}
      </PageContentBody>

      {/* ====================================================================== */}
      {/* MODAL: ADD / EDIT NOTIFICATION CONNECTION                               */}
      {/* ====================================================================== */}
      {notifModalOpen && (
        <ArrModal
          title={editingNotifId !== null ? `Edit Connection — ${notifName}` : 'Add Notification Connection'}
          onClose={() => setNotifModalOpen(false)}
          maxWidth="max-w-2xl"
          footer={
            <div className="w-full flex items-center justify-between">
              <div>
                {editingNotifId !== null && (
                  <ArrButton
                    variant="danger"
                    busy={deleteNotifMutation.isPending}
                    onClick={() => {
                      if (window.confirm(`Delete connection "${notifName}"?`)) {
                        deleteNotifMutation.mutate(editingNotifId);
                        setNotifModalOpen(false);
                      }
                    }}
                  >
                    <Trash2 className="h-3.5 w-3.5" /> Delete
                  </ArrButton>
                )}
              </div>
              <div className="flex items-center gap-2">
                <ArrButton
                  variant="default"
                  busy={testNotifMutation.isPending}
                  onClick={() => testNotifMutation.mutate(editingNotifId ?? undefined)}
                >
                  <Send className="h-3.5 w-3.5" /> Test
                </ArrButton>
                <ArrButton onClick={() => setNotifModalOpen(false)}>Cancel</ArrButton>
                <ArrButton
                  variant="primary"
                  busy={saveNotifMutation.isPending}
                  disabled={!notifName.trim()}
                  onClick={() => saveNotifMutation.mutate()}
                >
                  Save Connection
                </ArrButton>
              </div>
            </div>
          }
        >
          <div className="space-y-4">
            {testResultAlert && (
              <ArrAlert kind={testResultAlert.success ? 'success' : 'danger'}>
                <div className="flex items-center gap-2">
                  {testResultAlert.success ? <Check className="h-4 w-4" /> : <X className="h-4 w-4" />}
                  <span>
                    <strong>{testResultAlert.success ? 'Test Succeeded' : 'Test Failed'}:</strong>{' '}
                    {testResultAlert.message}
                    {testResultAlert.statusCode && ` (HTTP ${testResultAlert.statusCode})`}
                  </span>
                </div>
              </ArrAlert>
            )}

            <FormGroup label="Provider Type">
              <select
                value={notifProvider}
                onChange={(e) => {
                  const val = e.target.value as NotificationProviderType;
                  setNotifProvider(val);
                  setTestResultAlert(null);
                }}
                className="arr-input"
              >
                {(Object.keys(PROVIDER_METADATA) as NotificationProviderType[]).map((key) => (
                  <option key={key} value={key}>
                    {PROVIDER_METADATA[key].label} — {PROVIDER_METADATA[key].description}
                  </option>
                ))}
              </select>
            </FormGroup>

            <FormGroup label="Connection Name" helpText="e.g. Home Discord Alerts, Telegram Broadcast, or PMS Library Scan">
              <input
                value={notifName}
                onChange={(e) => setNotifName(e.target.value)}
                placeholder="e.g. Discord #announcements"
                className="arr-input"
              />
            </FormGroup>

            <FormGroup label="Status">
              <label className="flex items-center gap-2 cursor-pointer mt-1">
                <input
                  type="checkbox"
                  checked={notifEnabled}
                  onChange={(e) => setNotifEnabled(e.target.checked)}
                  className="accent-arr-accent h-4 w-4"
                />
                <span className="text-[13px] text-[#eeeeee]">Enable this notification connection</span>
              </label>
            </FormGroup>

            {/* Provider-specific settings */}
            <div className="pt-2 border-t border-arr-card-border">
              <div className="text-[12px] font-semibold text-arr-accent mb-3 uppercase tracking-wide">
                {PROVIDER_METADATA[notifProvider].label} Settings
              </div>

              {notifProvider === 'discord' && (
                <>
                  <FormGroup label="Webhook URL" helpText="Full Discord Webhook URL copied from channel integrations.">
                    <input
                      value={notifSettings.webhookUrl ?? ''}
                      onChange={(e) => updateSetting('webhookUrl', e.target.value)}
                      placeholder="https://discord.com/api/webhooks/..."
                      className="arr-input font-mono text-[12px]"
                    />
                  </FormGroup>
                  <FormGroup label="Bot Username" helpText="Custom username to display on Discord embeds (optional)">
                    <input
                      value={notifSettings.username ?? ''}
                      onChange={(e) => updateSetting('username', e.target.value)}
                      placeholder="Managerr"
                      className="arr-input"
                    />
                  </FormGroup>
                </>
              )}

              {notifProvider === 'telegram' && (
                <>
                  <FormGroup label="Bot Token" helpText="Telegram Bot API Token from @BotFather (e.g. 123456:ABC-DEF...)">
                    <input
                      value={notifSettings.botToken ?? ''}
                      onChange={(e) => updateSetting('botToken', e.target.value)}
                      placeholder="123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11"
                      className="arr-input font-mono text-[12px]"
                    />
                  </FormGroup>
                  <FormGroup label="Chat ID" helpText="Chat ID or channel name (e.g. -100123456789 or @channel)">
                    <input
                      value={notifSettings.chatId ?? ''}
                      onChange={(e) => updateSetting('chatId', e.target.value)}
                      placeholder="-100123456789"
                      className="arr-input font-mono text-[12px]"
                    />
                  </FormGroup>
                </>
              )}

              {notifProvider === 'gotify' && (
                <>
                  <FormGroup label="Server URL" helpText="Gotify host URL">
                    <input
                      value={notifSettings.serverUrl ?? ''}
                      onChange={(e) => updateSetting('serverUrl', e.target.value)}
                      placeholder="https://gotify.yourdomain.com"
                      className="arr-input font-mono text-[12px]"
                    />
                  </FormGroup>
                  <FormGroup label="App Token" helpText="Application token generated in Gotify Web UI">
                    <input
                      value={notifSettings.appToken ?? ''}
                      onChange={(e) => updateSetting('appToken', e.target.value)}
                      placeholder="Axxxxxx"
                      className="arr-input font-mono text-[12px]"
                    />
                  </FormGroup>
                  <FormGroup label="Priority" helpText="Gotify notification priority (1-10)">
                    <input
                      type="number"
                      min="1"
                      max="10"
                      value={notifSettings.priority ?? '5'}
                      onChange={(e) => updateSetting('priority', e.target.value)}
                      className="arr-input font-mono !w-[90px]"
                    />
                  </FormGroup>
                </>
              )}

              {notifProvider === 'ntfy' && (
                <>
                  <FormGroup label="Server URL" helpText="ntfy server (default: https://ntfy.sh)">
                    <input
                      value={notifSettings.serverUrl ?? ''}
                      onChange={(e) => updateSetting('serverUrl', e.target.value)}
                      placeholder="https://ntfy.sh"
                      className="arr-input font-mono text-[12px]"
                    />
                  </FormGroup>
                  <FormGroup label="Topic" helpText="Target topic to publish alerts to">
                    <input
                      value={notifSettings.topic ?? ''}
                      onChange={(e) => updateSetting('topic', e.target.value)}
                      placeholder="my-secret-managerr-topic"
                      className="arr-input font-mono text-[12px]"
                    />
                  </FormGroup>
                  <FormGroup label="Access Token" helpText="Optional Bearer token for protected ntfy topics">
                    <input
                      value={notifSettings.accessToken ?? ''}
                      onChange={(e) => updateSetting('accessToken', e.target.value)}
                      placeholder="tk_..."
                      className="arr-input font-mono text-[12px]"
                    />
                  </FormGroup>
                </>
              )}

              {notifProvider === 'apprise' && (
                <>
                  <FormGroup label="API URL" helpText="Apprise microservice URL (e.g. http://localhost:8000/notify)">
                    <input
                      value={notifSettings.apiUrl ?? ''}
                      onChange={(e) => updateSetting('apiUrl', e.target.value)}
                      placeholder="http://127.0.0.1:8000/notify"
                      className="arr-input font-mono text-[12px]"
                    />
                  </FormGroup>
                  <FormGroup label="URLs / Endpoints" helpText="Comma-separated Apprise notification URIs (e.g. mailto://..., poushover://...)">
                    <textarea
                      rows={2}
                      value={notifSettings.urls ?? ''}
                      onChange={(e) => updateSetting('urls', e.target.value)}
                      placeholder="mailto://user:pass@gmail.com, pover://..."
                      className="arr-input font-mono text-[12px]"
                    />
                  </FormGroup>
                </>
              )}

              {notifProvider === 'webhook' && (
                <>
                  <FormGroup label="Target URL" helpText="HTTP URL to deliver notification payload">
                    <input
                      value={notifSettings.url ?? ''}
                      onChange={(e) => updateSetting('url', e.target.value)}
                      placeholder="https://homeassistant.local/api/webhook/managerr"
                      className="arr-input font-mono text-[12px]"
                    />
                  </FormGroup>
                  <FormGroup label="Method">
                    <select
                      value={notifSettings.method ?? 'POST'}
                      onChange={(e) => updateSetting('method', e.target.value)}
                      className="arr-input !w-[120px]"
                    >
                      <option value="POST">POST</option>
                      <option value="PUT">PUT</option>
                    </select>
                  </FormGroup>
                  <FormGroup label="HMAC Secret" helpText="Optional SHA256 secret for X-Managerr-Signature verification">
                    <input
                      value={notifSettings.hmacSecret ?? ''}
                      onChange={(e) => updateSetting('hmacSecret', e.target.value)}
                      placeholder="Optional secret string"
                      className="arr-input font-mono text-[12px]"
                    />
                  </FormGroup>
                </>
              )}

              {notifProvider === 'plex' && (
                <>
                  <FormGroup label="Server URL" helpText="Plex Media Server address">
                    <input
                      value={notifSettings.serverUrl ?? ''}
                      onChange={(e) => updateSetting('serverUrl', e.target.value)}
                      placeholder="http://127.0.0.1:32400"
                      className="arr-input font-mono text-[12px]"
                    />
                  </FormGroup>
                  <FormGroup label="Plex Token" helpText="X-Plex-Token">
                    <input
                      value={notifSettings.token ?? ''}
                      onChange={(e) => updateSetting('token', e.target.value)}
                      placeholder="Your Plex authentication token"
                      className="arr-input font-mono text-[12px]"
                    />
                  </FormGroup>
                  <FormGroup label="Section ID" helpText="Optional library section ID to selectively refresh">
                    <input
                      value={notifSettings.sectionId ?? ''}
                      onChange={(e) => updateSetting('sectionId', e.target.value)}
                      placeholder="e.g. 1"
                      className="arr-input font-mono !w-[100px]"
                    />
                  </FormGroup>
                </>
              )}

              {notifProvider === 'jellyfin' && (
                <>
                  <FormGroup label="Server URL" helpText="Jellyfin server address">
                    <input
                      value={notifSettings.serverUrl ?? ''}
                      onChange={(e) => updateSetting('serverUrl', e.target.value)}
                      placeholder="http://127.0.0.1:8096"
                      className="arr-input font-mono text-[12px]"
                    />
                  </FormGroup>
                  <FormGroup label="API Key" helpText="Jellyfin API token generated in Dashboard -> API Keys">
                    <input
                      value={notifSettings.apiKey ?? ''}
                      onChange={(e) => updateSetting('apiKey', e.target.value)}
                      placeholder="Jellyfin API key"
                      className="arr-input font-mono text-[12px]"
                    />
                  </FormGroup>
                </>
              )}
            </div>

            {/* Notification Triggers */}
            <div className="pt-3 border-t border-arr-card-border">
              <div className="text-[13px] font-bold text-[#e1e2e3] mb-2">Notification Triggers</div>
              <div className="grid grid-cols-2 sm:grid-cols-3 gap-2 text-[12px]">
                <label className="flex items-center gap-2 cursor-pointer p-1 rounded hover:bg-[#222222]">
                  <input
                    type="checkbox"
                    checked={onGrab}
                    onChange={(e) => setOnGrab(e.target.checked)}
                    className="accent-arr-accent"
                  />
                  <span>On Grab</span>
                </label>
                <label className="flex items-center gap-2 cursor-pointer p-1 rounded hover:bg-[#222222]">
                  <input
                    type="checkbox"
                    checked={onImport}
                    onChange={(e) => setOnImport(e.target.checked)}
                    className="accent-arr-accent"
                  />
                  <span>On Import</span>
                </label>
                <label className="flex items-center gap-2 cursor-pointer p-1 rounded hover:bg-[#222222]">
                  <input
                    type="checkbox"
                    checked={onUpgrade}
                    onChange={(e) => setOnUpgrade(e.target.checked)}
                    className="accent-arr-accent"
                  />
                  <span>On Upgrade</span>
                </label>
                <label className="flex items-center gap-2 cursor-pointer p-1 rounded hover:bg-[#222222]">
                  <input
                    type="checkbox"
                    checked={onRename}
                    onChange={(e) => setOnRename(e.target.checked)}
                    className="accent-arr-accent"
                  />
                  <span>On Rename</span>
                </label>
                <label className="flex items-center gap-2 cursor-pointer p-1 rounded hover:bg-[#222222]">
                  <input
                    type="checkbox"
                    checked={onDelete}
                    onChange={(e) => setOnDelete(e.target.checked)}
                    className="accent-arr-accent"
                  />
                  <span>On Delete</span>
                </label>
                <label className="flex items-center gap-2 cursor-pointer p-1 rounded hover:bg-[#222222]">
                  <input
                    type="checkbox"
                    checked={onHealthIssue}
                    onChange={(e) => setOnHealthIssue(e.target.checked)}
                    className="accent-[#f05050]"
                  />
                  <span>On Health Issue</span>
                </label>
                <label className="flex items-center gap-2 cursor-pointer p-1 rounded hover:bg-[#222222]">
                  <input
                    type="checkbox"
                    checked={onApplicationUpdate}
                    onChange={(e) => setOnApplicationUpdate(e.target.checked)}
                    className="accent-arr-accent"
                  />
                  <span>On App Update</span>
                </label>
              </div>
            </div>

            <FormGroup label="Tags" helpText="Apply this notification connection only to works tagged with these terms.">
              <input
                value={notifTags}
                onChange={(e) => setNotifTags(e.target.value)}
                placeholder="e.g. 4k, discord-notify"
                className="arr-input text-[12px]"
              />
            </FormGroup>
          </div>
        </ArrModal>
      )}

      {/* ====================================================================== */}
      {/* MODAL: ADD / EDIT IMPORT LIST                                           */}
      {/* ====================================================================== */}
      {listModalOpen && (
        <ArrModal
          title={editingListId !== null ? `Edit Import List — ${listName}` : 'Add Import List'}
          onClose={() => setListModalOpen(false)}
          maxWidth="max-w-2xl"
          footer={
            <div className="w-full flex items-center justify-between">
              <div>
                {editingListId !== null && (
                  <ArrButton
                    variant="danger"
                    busy={deleteListMutation.isPending}
                    onClick={() => {
                      if (window.confirm(`Delete import list "${listName}"?`)) {
                        deleteListMutation.mutate(editingListId);
                        setListModalOpen(false);
                      }
                    }}
                  >
                    <Trash2 className="h-3.5 w-3.5" /> Delete
                  </ArrButton>
                )}
              </div>
              <div className="flex items-center gap-2">
                <ArrButton onClick={() => setListModalOpen(false)}>Cancel</ArrButton>
                <ArrButton
                  variant="primary"
                  busy={saveListMutation.isPending}
                  disabled={!listName.trim()}
                  onClick={() => saveListMutation.mutate()}
                >
                  Save Import List
                </ArrButton>
              </div>
            </div>
          }
        >
          <div className="space-y-4">
            <FormGroup label="List Type">
              <select
                value={listType}
                onChange={(e) => {
                  const nextType = e.target.value as ImportListType;
                  setListType(nextType);
                  setListVertical(IMPORT_LIST_TYPE_INFO[nextType]?.defaultVertical ?? 'movies');
                  if (!listName) {
                    setListName(IMPORT_LIST_TYPE_INFO[nextType]?.label ?? '');
                  }
                }}
                className="arr-input"
              >
                {(Object.keys(IMPORT_LIST_TYPE_INFO) as ImportListType[]).map((key) => (
                  <option key={key} value={key}>
                    {IMPORT_LIST_TYPE_INFO[key].label}
                  </option>
                ))}
              </select>
            </FormGroup>

            <FormGroup label="List Name">
              <input
                value={listName}
                onChange={(e) => setListName(e.target.value)}
                placeholder="e.g. TMDB Popular Movies"
                className="arr-input"
              />
            </FormGroup>

            <FormGroup label="Media Vertical">
              <select
                value={listVertical}
                onChange={(e) => setListVertical(e.target.value)}
                className="arr-input"
              >
                <option value="movies">Movies</option>
                <option value="tv">TV Series</option>
                <option value="anime">Anime</option>
                <option value="music">Music</option>
                <option value="ebooks">Books &amp; Audiobooks</option>
              </select>
            </FormGroup>

            <FormGroup label="Target Root Folder / Library" helpText="Media added from this list will be placed into this library folder.">
              <select
                value={listLibraryId}
                onChange={(e) => setListLibraryId(Number(e.target.value))}
                className="arr-input"
              >
                {(librariesQuery.data ?? []).map((lib) => (
                  <option key={lib.id} value={lib.id}>
                    {lib.name}
                  </option>
                ))}
              </select>
            </FormGroup>

            <FormGroup label="Quality Profile">
              <select
                value={listProfileId ?? ''}
                onChange={(e) => setListProfileId(e.target.value ? Number(e.target.value) : null)}
                className="arr-input"
              >
                <option value="">Default Profile</option>
                {(profilesQuery.data ?? []).map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name}
                  </option>
                ))}
              </select>
            </FormGroup>

            <FormGroup label="Monitor Mode">
              <select
                value={listMonitorMode}
                onChange={(e) => setListMonitorMode(e.target.value)}
                className="arr-input"
              >
                <option value="all">All Episodes / Media</option>
                <option value="future">Future Seasons / Episodes Only</option>
                <option value="missing">Missing Media Only</option>
                <option value="latest">Latest Season Only</option>
                <option value="firstSeason">First Season Only</option>
                <option value="none">Unmonitored</option>
              </select>
            </FormGroup>

            <FormGroup label="Minimum Availability">
              <select
                value={listMinAvailability}
                onChange={(e) => setListMinAvailability(e.target.value)}
                className="arr-input"
              >
                <option value="announced">Announced</option>
                <option value="inCinemas">In Cinemas</option>
                <option value="released">Released / Physical</option>
                <option value="deleted">PreDB / Verified</option>
              </select>
            </FormGroup>

            <FormGroup label="Search on Add" helpText="Automatically trigger automated indexer search when a new work is imported.">
              <label className="flex items-center gap-2 cursor-pointer mt-1">
                <input
                  type="checkbox"
                  checked={listSearchOnAdd}
                  onChange={(e) => setListSearchOnAdd(e.target.checked)}
                  className="accent-arr-accent h-4 w-4"
                />
                <span className="text-[13px] text-[#eeeeee]">Trigger search immediately on import</span>
              </label>
            </FormGroup>

            <FormGroup label="Sync Interval" helpText="Hours between automatic background list synchronizations.">
              <div className="flex items-center gap-2">
                <input
                  type="number"
                  min="1"
                  max="168"
                  value={listSyncHours}
                  onChange={(e) => setListSyncHours(Number(e.target.value) || 24)}
                  className="arr-input font-mono !w-[90px]"
                />
                <span className="text-[12px] text-[#888888]">hours</span>
              </div>
            </FormGroup>

            {/* List-type settings */}
            <div className="pt-2 border-t border-arr-card-border">
              <div className="text-[12px] font-semibold text-arr-accent mb-3 uppercase tracking-wide">
                List Parameters
              </div>

              {(listType === 'custom_json' || listType === 'stevenlu') && (
                <FormGroup label="Feed URL" helpText="URL providing JSON list data">
                  <input
                    value={
                      listSettings.url ??
                      (listType === 'stevenlu'
                        ? 'https://raw.githubusercontent.com/stevenlu/stevenlu.com/master/build/movies.json'
                        : '')
                    }
                    onChange={(e) =>
                      setListSettings((prev) => ({ ...prev, url: e.target.value }))
                    }
                    placeholder="https://..."
                    className="arr-input font-mono text-[12px]"
                  />
                </FormGroup>
              )}

              {listType === 'openlibrary_subject' && (
                <FormGroup label="Subject / Genre" helpText="e.g. science_fiction, thriller, fantasy, history">
                  <input
                    value={listSettings.subject ?? 'science_fiction'}
                    onChange={(e) =>
                      setListSettings((prev) => ({ ...prev, subject: e.target.value }))
                    }
                    placeholder="science_fiction"
                    className="arr-input font-mono text-[12px]"
                  />
                </FormGroup>
              )}

              {listType === 'trakt_popular' && (
                <>
                  <FormGroup label="Trakt Client ID" helpText="Optional Trakt OAuth Client ID">
                    <input
                      value={listSettings.clientId ?? ''}
                      onChange={(e) =>
                        setListSettings((prev) => ({ ...prev, clientId: e.target.value }))
                      }
                      placeholder="Trakt API Client ID"
                      className="arr-input font-mono text-[12px]"
                    />
                  </FormGroup>
                  <FormGroup label="List Path" helpText="Trakt endpoint e.g. /movies/popular or /shows/trending">
                    <input
                      value={listSettings.listPath ?? '/movies/popular'}
                      onChange={(e) =>
                        setListSettings((prev) => ({ ...prev, listPath: e.target.value }))
                      }
                      placeholder="/movies/popular"
                      className="arr-input font-mono text-[12px]"
                    />
                  </FormGroup>
                </>
              )}

              {(listType.startsWith('tmdb_') ||
                listType === 'anilist_trending' ||
                listType === 'openlibrary_subject' ||
                listType === 'trakt_popular') && (
                <FormGroup label="Item Limit" helpText="Maximum number of items to import per sync (e.g. 50)">
                  <input
                    type="number"
                    min="1"
                    max="500"
                    value={listSettings.limit ?? '50'}
                    onChange={(e) =>
                      setListSettings((prev) => ({ ...prev, limit: e.target.value }))
                    }
                    className="arr-input font-mono !w-[90px]"
                  />
                </FormGroup>
              )}
            </div>

            <FormGroup label="Status">
              <label className="flex items-center gap-2 cursor-pointer mt-1">
                <input
                  type="checkbox"
                  checked={listEnabled}
                  onChange={(e) => setListEnabled(e.target.checked)}
                  className="accent-arr-accent h-4 w-4"
                />
                <span className="text-[13px] text-[#eeeeee]">Enable this import list</span>
              </label>
            </FormGroup>

            <FormGroup label="Tags" helpText="Comma-separated tags to assign to imported works.">
              <input
                value={listTags}
                onChange={(e) => setListTags(e.target.value)}
                placeholder="e.g. trending, tmdb"
                className="arr-input text-[12px]"
              />
            </FormGroup>
          </div>
        </ArrModal>
      )}
    </div>
  );
}
