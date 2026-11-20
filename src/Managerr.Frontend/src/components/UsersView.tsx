import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Fingerprint, KeyRound, Pencil, RefreshCw, ShieldCheck, SlidersHorizontal, UserPlus, Users as UsersIcon } from 'lucide-react';
import {
  apiFetch,
  ApiError,
  type LocalUserCreateRequest,
  type PlexUserSummary,
  type RoleDefinition,
} from '../lib/api';
import {
  ArrAlert,
  ArrButton,
  ArrLabel,
  ArrModal,
  FieldSet,
  FormGroup,
  PageContentBody,
  PageToolbar,
  PageToolbarButton,
  PageToolbarSeparator,
} from './arr/ArrPrimitives';

interface UsersViewProps {
  canManageUsers: boolean;
}

export function UsersView({ canManageUsers }: UsersViewProps) {
  const qc = useQueryClient();
  const usersQuery = useQuery({
    queryKey: ['users'],
    queryFn: () => apiFetch<PlexUserSummary[]>('/api/v1/users'),
  });
  const rolesQuery = useQuery({
    queryKey: ['roles'],
    queryFn: () => apiFetch<RoleDefinition[]>('/api/v1/roles'),
  });

  const [roleModalUser, setRoleModalUser] = useState<PlexUserSummary | null>(null);
  const [checkedRoleIds, setCheckedRoleIds] = useState<Set<number>>(new Set());
  const [syncMsg, setSyncMsg] = useState<{ kind: 'info' | 'danger'; text: string } | null>(null);

  // Edit User Modal State
  const [editUser, setEditUser] = useState<PlexUserSummary | null>(null);
  const [editAudioLang, setEditAudioLang] = useState('');
  const [editSubLang, setEditSubLang] = useState('');
  const [editDiscordUserId, setEditDiscordUserId] = useState('');
  const [editDiscordUsername, setEditDiscordUsername] = useState('');
  const [editIsActive, setEditIsActive] = useState(true);
  const [editError, setEditError] = useState<string | null>(null);

  // Add Local User Modal State
  const [showAddUserModal, setShowAddUserModal] = useState(false);
  const [addUsername, setAddUsername] = useState('');
  const [addDisplayName, setAddDisplayName] = useState('');
  const [addEmail, setAddEmail] = useState('');
  const [addPassword, setAddPassword] = useState('');
  const [addPasswordConfirm, setAddPasswordConfirm] = useState('');
  const [addUserRoleIds, setAddUserRoleIds] = useState<Set<number>>(new Set());
  const [addError, setAddError] = useState<string | null>(null);

  const syncMutation = useMutation({
    mutationFn: () => apiFetch<{ syncedCount: number }>('/api/v1/users/sync-plex', { method: 'POST' }),
    onSuccess: (data) => {
      setSyncMsg({ kind: 'info', text: `Synchronized ${data.syncedCount} shared Plex Media Server user(s).` });
      qc.invalidateQueries({ queryKey: ['users'] });
    },
    onError: (err) =>
      setSyncMsg({
        kind: 'danger',
        text: err instanceof ApiError ? err.message : 'Plex user synchronization failed.',
      }),
  });

  const assignMutation = useMutation({
    mutationFn: ({ userId, roleIds }: { userId: number; roleIds: number[] }) =>
      apiFetch<PlexUserSummary>(`/api/v1/users/${userId}/roles`, {
        method: 'PUT',
        body: JSON.stringify({ roleIds }),
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['users'] });
      qc.invalidateQueries({ queryKey: ['roles'] });
      qc.invalidateQueries({ queryKey: ['me'] });
      setRoleModalUser(null);
    },
  });

  const createUserMutation = useMutation({
    mutationFn: async (payload: LocalUserCreateRequest) => {
      return await apiFetch<PlexUserSummary>('/api/v1/users/local', {
        method: 'POST',
        body: JSON.stringify(payload),
      });
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['users'] });
      setShowAddUserModal(false);
      setAddUsername('');
      setAddDisplayName('');
      setAddEmail('');
      setAddPassword('');
      setAddPasswordConfirm('');
      setAddUserRoleIds(new Set());
      setAddError(null);
    },
    onError: (err) => {
      setAddError(err instanceof ApiError ? err.message : 'Failed to create local user.');
    },
  });

  const updateUserMutation = useMutation({
    mutationFn: async (payload: {
      userId: number;
      preferredAudioLang: string | null;
      preferredSubLang: string | null;
      discordUserId: string | null;
      discordUsername: string | null;
      isActive: boolean;
    }) => {
      const { userId, ...body } = payload;
      return await apiFetch<PlexUserSummary>(`/api/v1/users/${userId}`, {
        method: 'PATCH',
        body: JSON.stringify(body),
      });
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['users'] });
      qc.invalidateQueries({ queryKey: ['me'] });
      setEditUser(null);
      setEditError(null);
    },
    onError: (err) => {
      setEditError(err instanceof ApiError ? err.message : 'Failed to update user.');
    },
  });

  function openEditUserModal(u: PlexUserSummary) {
    setEditUser(u);
    setEditAudioLang(u.preferredAudioLang ?? '');
    setEditSubLang(u.preferredSubLang ?? '');
    setEditDiscordUserId(u.discordUserId ?? '');
    setEditDiscordUsername(u.discordUsername ?? '');
    setEditIsActive(u.isActive);
    setEditError(null);
  }

  function openAssignModal(u: PlexUserSummary) {
    setRoleModalUser(u);
    setCheckedRoleIds(new Set(u.roles.map((r) => r.id)));
  }

  function toggleRoleId(id: number) {
    setCheckedRoleIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  function toggleAddRoleId(id: number) {
    setAddUserRoleIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  function handleCreateUserSubmit(e: React.FormEvent) {
    e.preventDefault();
    setAddError(null);

    if (addPassword !== addPasswordConfirm) {
      setAddError('Passwords do not match.');
      return;
    }
    if (addPassword.length < 8) {
      setAddError('Password must be at least 8 characters long.');
      return;
    }

    createUserMutation.mutate({
      username: addUsername.trim(),
      displayName: addDisplayName.trim() || undefined,
      email: addEmail.trim() || undefined,
      password: addPassword,
      roleIds: Array.from(addUserRoleIds),
    });
  }

  function renderProviderBadges(u: PlexUserSummary) {
    const provs = u.providers ?? [];
    const isLocal =
      provs.includes('local') || u.authProvider === 'local' || (!u.plexUserId && !u.authProvider && provs.length === 0);
    const isOidc = provs.includes('oidc') || u.authProvider === 'oidc';
    const isPlex = provs.includes('plex') || u.authProvider === 'plex' || Boolean(u.plexUserId);
    const hasPasskeys = Boolean(u.hasPasskeys || (u.passkeyCount && u.passkeyCount > 0));

    return (
      <div className="flex flex-wrap gap-1 items-center">
        {isLocal && (
          <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded-[3px] text-[10px] font-semibold bg-[#2e3e4e] text-[#69c0ff] border border-[#1b3a57]">
            <KeyRound className="h-2.5 w-2.5" /> Local
          </span>
        )}
        {isOidc && (
          <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded-[3px] text-[10px] font-semibold bg-[#1c392b] text-[#52c41a] border border-[#276738]">
            <ShieldCheck className="h-2.5 w-2.5" /> OIDC
          </span>
        )}
        {isPlex && (
          <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded-[3px] text-[10px] font-semibold bg-[#3d321d] text-[#e5a00d] border border-[#5c4714]">
            Plex
          </span>
        )}
        {hasPasskeys && (
          <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded-[3px] text-[10px] font-semibold bg-[#2a2845] text-[#b37feb] border border-[#3e3863]">
            <Fingerprint className="h-2.5 w-2.5" /> Passkey
          </span>
        )}
      </div>
    );
  }

  return (
    <div className="flex flex-col flex-1">
      <PageToolbar
        left={
          <>
            <PageToolbarButton
              icon={RefreshCw}
              label="Refresh"
              spinning={usersQuery.isFetching}
              onClick={() => void usersQuery.refetch()}
            />
            {canManageUsers && (
              <>
                <PageToolbarSeparator />
                <PageToolbarButton
                  icon={UserPlus}
                  label="Add Local User"
                  onClick={() => {
                    setAddError(null);
                    setShowAddUserModal(true);
                  }}
                />
                <PageToolbarButton
                  icon={UsersIcon}
                  label="Sync Plex Users"
                  spinning={syncMutation.isPending}
                  onClick={() => syncMutation.mutate()}
                />
              </>
            )}
          </>
        }
      />

      <PageContentBody>
        {syncMsg && (
          <ArrAlert kind={syncMsg.kind} className="mb-4">
            {syncMsg.text}
          </ArrAlert>
        )}

        <FieldSet legend="Users & Access">
          <div className="overflow-x-auto">
            <table className="arr-table">
              <thead>
                <tr>
                  <th>Username</th>
                  <th>Authentication</th>
                  <th>Account Type</th>
                  <th>Shared PMS Sections</th>
                  <th>Languages</th>
                  <th>Roles</th>
                  {canManageUsers && <th className="w-16 text-right">Edit</th>}
                </tr>
              </thead>
              <tbody>
                {(usersQuery.data ?? []).map((u) => (
                  <tr key={u.id} className={!u.isActive ? 'opacity-50' : ''}>
                    <td>
                      <div className="font-semibold text-[#eeeeee]">
                        {u.displayName ? (
                          <span>
                            {u.displayName} <span className="text-[12px] font-normal text-[#909293]">({u.username})</span>
                          </span>
                        ) : (
                          u.username
                        )}
                      </div>
                      <div className="text-[11px] text-[#909293]">
                        {u.email ?? (u.plexUserId ? `Plex #${u.plexUserId}` : 'Local Account')}
                      </div>
                    </td>
                    <td>{renderProviderBadges(u)}</td>
                    <td>
                      {u.isServerOwner ? (
                        <ArrLabel kind="warning">Server Owner</ArrLabel>
                      ) : u.isHomeUser ? (
                        <ArrLabel kind="primary">Plex Home</ArrLabel>
                      ) : (
                        <ArrLabel kind="default">Standard User</ArrLabel>
                      )}
                    </td>
                    <td className="font-mono text-[12px]">
                      {u.effectivePermissions.hasAllLibrariesAccess
                        ? 'All (*)'
                        : u.sharedPlexSectionIds.length > 0
                          ? u.sharedPlexSectionIds.join(', ')
                          : 'None'}
                    </td>
                    <td className="text-[12px]">
                      Audio: <code className="text-[#eeeeee]">{u.preferredAudioLang ?? 'auto'}</code> / Sub:{' '}
                      <code className="text-[#eeeeee]">{u.preferredSubLang ?? 'auto'}</code>
                    </td>
                    <td>
                      <div className="flex flex-wrap gap-1">
                        {u.roles.map((r) => (
                          <span
                            key={r.id}
                            className="inline-flex items-center px-1.5 py-[2px] text-[11px] font-semibold rounded-[3px] text-white"
                            style={{ backgroundColor: r.color || 'var(--arr-primary)' }}
                          >
                            {r.name}
                          </span>
                        ))}
                      </div>
                    </td>
                    {canManageUsers && (
                      <td className="text-right">
                        <div className="inline-flex items-center gap-1">
                          <button
                            type="button"
                            title="Edit User"
                            aria-label={`Edit User ${u.username}`}
                            onClick={() => openEditUserModal(u)}
                            className="p-1.5 text-[#cccccc] hover:text-arr-accent cursor-pointer"
                          >
                            <SlidersHorizontal className="h-4 w-4" />
                          </button>
                          <button
                            type="button"
                            title="Edit User Roles"
                            aria-label={`Edit Roles for ${u.username}`}
                            onClick={() => openAssignModal(u)}
                            className="p-1.5 text-[#cccccc] hover:text-arr-accent cursor-pointer"
                          >
                            <Pencil className="h-4 w-4" />
                          </button>
                        </div>
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </FieldSet>

        {/* MODAL: ADD LOCAL USER */}
        {showAddUserModal && (
          <ArrModal
            title="Add Local User"
            onClose={() => setShowAddUserModal(false)}
            maxWidth="max-w-lg"
            footer={
              <>
                <ArrButton variant="default" onClick={() => setShowAddUserModal(false)} disabled={createUserMutation.isPending}>
                  Cancel
                </ArrButton>
                <ArrButton
                  variant="primary"
                  form="add-user-form"
                  type="submit"
                  busy={createUserMutation.isPending}
                >
                  Create User
                </ArrButton>
              </>
            }
          >
            {addError && <ArrAlert kind="danger" className="mb-4">{addError}</ArrAlert>}

            <form id="add-user-form" onSubmit={handleCreateUserSubmit} className="space-y-3">
              <FormGroup label="Username" required>
                <input
                  id="add-user-username"
                  name="username"
                  type="text"
                  required
                  autoFocus
                  autoComplete="off"
                  autoCorrect="off"
                  autoCapitalize="none"
                  spellCheck={false}
                  value={addUsername}
                  onChange={(e) => setAddUsername(e.target.value)}
                  className="arr-input"
                />
              </FormGroup>

              <FormGroup label="Display Name">
                <input
                  id="add-user-display-name"
                  name="displayName"
                  type="text"
                  autoComplete="name"
                  value={addDisplayName}
                  onChange={(e) => setAddDisplayName(e.target.value)}
                  className="arr-input"
                  placeholder="e.g. Jane Doe"
                />
              </FormGroup>

              <FormGroup label="Email Address">
                <input
                  id="add-user-email"
                  name="email"
                  type="email"
                  autoComplete="off"
                  spellCheck={false}
                  value={addEmail}
                  onChange={(e) => setAddEmail(e.target.value)}
                  className="arr-input"
                  placeholder="jane@example.com"
                />
              </FormGroup>

              <FormGroup label="Password" required helpText="Must be at least 8 characters.">
                <input
                  id="add-user-password"
                  name="password"
                  type="password"
                  required
                  minLength={8}
                  autoComplete="new-password"
                  value={addPassword}
                  onChange={(e) => setAddPassword(e.target.value)}
                  className="arr-input"
                />
              </FormGroup>

              <FormGroup label="Confirm Password" required>
                <input
                  id="add-user-confirm-password"
                  name="confirmPassword"
                  type="password"
                  required
                  minLength={8}
                  autoComplete="new-password"
                  value={addPasswordConfirm}
                  onChange={(e) => setAddPasswordConfirm(e.target.value)}
                  className="arr-input"
                />
              </FormGroup>

              <div>
                <label className="block text-[12px] font-bold text-[#cccccc] mb-1.5">
                  Assigned Roles
                </label>
                <div className="space-y-1.5 max-h-48 overflow-y-auto pr-1">
                  {(rolesQuery.data ?? []).map((r) => (
                    <label
                      key={r.id}
                      className="flex items-center gap-2.5 px-2.5 py-1.5 bg-arr-card border border-arr-card-border rounded-[3px] cursor-pointer hover:bg-arr-input"
                    >
                      <input
                        type="checkbox"
                        checked={addUserRoleIds.has(r.id)}
                        onChange={() => toggleAddRoleId(r.id)}
                      />
                      <span className="text-[13px] font-medium text-[#eeeeee]">{r.name}</span>
                      <span className="text-[11px] text-[#909293] ml-auto">{r.description}</span>
                    </label>
                  ))}
                </div>
              </div>
            </form>
          </ArrModal>
        )}

        {/* MODAL: EDIT USER */}
        {editUser && (
          <ArrModal
            title={`Edit User — ${editUser.username}`}
            onClose={() => setEditUser(null)}
            maxWidth="max-w-lg"
            footer={
              <>
                <ArrButton onClick={() => setEditUser(null)} disabled={updateUserMutation.isPending}>
                  Cancel
                </ArrButton>
                <ArrButton
                  variant="primary"
                  form="edit-user-form"
                  type="submit"
                  busy={updateUserMutation.isPending}
                >
                  Save Changes
                </ArrButton>
              </>
            }
          >
            {editError && <ArrAlert kind="danger" className="mb-4">{editError}</ArrAlert>}

            <form
              id="edit-user-form"
              onSubmit={(e) => {
                e.preventDefault();
                updateUserMutation.mutate({
                  userId: editUser.id,
                  preferredAudioLang: editAudioLang.trim() || null,
                  preferredSubLang: editSubLang.trim() || null,
                  discordUserId: editDiscordUserId.trim() || null,
                  discordUsername: editDiscordUsername.trim() || null,
                  isActive: editIsActive,
                });
              }}
              className="space-y-3"
            >
              <FormGroup label="Preferred Audio Language" helpText="ISO 639-1 code (e.g. en, de, ja) or blank for default.">
                <input
                  type="text"
                  value={editAudioLang}
                  onChange={(e) => setEditAudioLang(e.target.value)}
                  placeholder="en"
                  className="arr-input"
                />
              </FormGroup>

              <FormGroup label="Preferred Subtitle Language" helpText="ISO 639-1 code (e.g. en, de) or blank for default.">
                <input
                  type="text"
                  value={editSubLang}
                  onChange={(e) => setEditSubLang(e.target.value)}
                  placeholder="en"
                  className="arr-input"
                />
              </FormGroup>

              <FormGroup label="Discord User ID" helpText="Numeric Discord snowflake ID for mentions and Cleanerr actions.">
                <input
                  type="text"
                  value={editDiscordUserId}
                  onChange={(e) => setEditDiscordUserId(e.target.value)}
                  placeholder="123456789012345678"
                  className="arr-input font-mono"
                />
              </FormGroup>

              <FormGroup label="Discord Username" helpText="Discord handle shown in notifications.">
                <input
                  type="text"
                  value={editDiscordUsername}
                  onChange={(e) => setEditDiscordUsername(e.target.value)}
                  placeholder="username"
                  className="arr-input"
                />
              </FormGroup>

              <FormGroup label="Account Status">
                <label className="inline-flex items-center gap-2 pt-1.5 text-[13px] cursor-pointer">
                  <input
                    type="checkbox"
                    disabled={editUser.isServerOwner}
                    checked={editIsActive}
                    onChange={(e) => setEditIsActive(e.target.checked)}
                  />
                  <span>Account Active</span>
                  {editUser.isServerOwner && (
                    <span className="text-[11px] text-[#909293]">(Server Owner cannot be deactivated)</span>
                  )}
                </label>
              </FormGroup>
            </form>
          </ArrModal>
        )}

        {/* MODAL: EDIT USER ROLES */}
        {roleModalUser && (
          <ArrModal
            title={`Edit Roles — ${roleModalUser.username}`}
            onClose={() => setRoleModalUser(null)}
            maxWidth="max-w-xl"
            footer={
              <>
                <ArrButton onClick={() => setRoleModalUser(null)}>Cancel</ArrButton>
                <ArrButton
                  variant="primary"
                  busy={assignMutation.isPending}
                  disabled={checkedRoleIds.size === 0}
                  onClick={() =>
                    assignMutation.mutate({
                      userId: roleModalUser.id,
                      roleIds: Array.from(checkedRoleIds),
                    })
                  }
                >
                  Save Roles
                </ArrButton>
              </>
            }
          >
            <div className="space-y-2">
              {(rolesQuery.data ?? []).map((r) => {
                const lockedOwnerAdmin = roleModalUser.isServerOwner && r.key === 'system:owner';
                return (
                  <label
                    key={r.id}
                    className="flex items-start gap-3 px-3 py-2.5 bg-arr-card border border-[#444444] rounded-[4px] cursor-pointer"
                  >
                    <input
                      type="checkbox"
                      disabled={lockedOwnerAdmin}
                      checked={checkedRoleIds.has(r.id)}
                      onChange={() => toggleRoleId(r.id)}
                      className="mt-1"
                    />
                    <div>
                      <div className="text-[13px] font-bold text-[#eeeeee]">{r.name}</div>
                      <div className="text-[12px] text-[#909293]">{r.description}</div>
                    </div>
                  </label>
                );
              })}
            </div>
          </ArrModal>
        )}
      </PageContentBody>
    </div>
  );
}
