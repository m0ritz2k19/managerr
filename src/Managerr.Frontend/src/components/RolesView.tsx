import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Copy, Plus, RefreshCw, Trash2 } from 'lucide-react';
import { apiFetch, ApiError, type RoleDefinition, type ScopeDescriptor } from '../lib/api';
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

interface RolesViewProps {
  canManageRoles: boolean;
}

export function RolesView({ canManageRoles }: RolesViewProps) {
  const qc = useQueryClient();
  const rolesQuery = useQuery({
    queryKey: ['roles'],
    queryFn: () => apiFetch<RoleDefinition[]>('/api/v1/roles'),
  });
  const scopesQuery = useQuery({
    queryKey: ['role-scopes'],
    queryFn: () => apiFetch<ScopeDescriptor[]>('/api/v1/roles/scopes'),
  });
  const librariesQuery = useQuery({
    queryKey: ['libraries'],
    queryFn: () => apiFetch<Array<{ id: number; name: string; mediaType: string }>>('/api/v1/libraries'),
  });

  const [editing, setEditing] = useState<RoleDefinition | null>(null);
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [color, setColor] = useState('#3b82f6');
  const [libMode, setLibMode] = useState<'inherit_from_plex' | 'explicit_libraries' | 'all_libraries'>(
    'inherit_from_plex',
  );
  const [allowedLibraryIds, setAllowedLibraryIds] = useState<Set<number>>(new Set());
  const [autoApprove, setAutoApprove] = useState(false);
  const [movieQuota, setMovieQuota] = useState<string>('');
  const [tvQuota, setTvQuota] = useState<string>('');
  const [musicBookQuota, setMusicBookQuota] = useState<string>('');
  const [cleanerrExclusionLimit, setCleanerrExclusionLimit] = useState<string>('');
  const [selectedScopes, setSelectedScopes] = useState<Set<string>>(new Set(['catalog:read', 'catalog:request']));
  const [errMsg, setErrMsg] = useState<string | null>(null);

  function openNewRole() {
    setEditing(null);
    setCreating(true);
    setName('');
    setDescription('');
    setColor('#3b82f6');
    setLibMode('inherit_from_plex');
    setAllowedLibraryIds(new Set());
    setAutoApprove(false);
    setMovieQuota('');
    setTvQuota('');
    setMusicBookQuota('');
    setCleanerrExclusionLimit('');
    setSelectedScopes(new Set(['catalog:read', 'catalog:request']));
    setErrMsg(null);
  }

  function openEditRole(role: RoleDefinition) {
    if (role.isImmutable) return;
    const extRole = role as RoleDefinition & {
      allowedLibraryIds?: number[];
      musicBookRequestQuota?: number | null;
    };
    setCreating(false);
    setEditing(role);
    setName(role.name);
    setDescription(role.description);
    setColor(role.color);
    setLibMode(role.libraryAccessMode);
    setAllowedLibraryIds(new Set(extRole.allowedLibraryIds ?? []));
    setAutoApprove(role.autoApproveRequests);
    setMovieQuota(role.movieRequestQuota?.toString() ?? '');
    setTvQuota(role.tvSeasonRequestQuota?.toString() ?? '');
    setMusicBookQuota(extRole.musicBookRequestQuota?.toString() ?? '');
    setCleanerrExclusionLimit(role.cleanerrExclusionLimit?.toString() ?? '');
    setSelectedScopes(new Set(role.scopes));
    setErrMsg(null);
  }

  const saveMutation = useMutation({
    mutationFn: async () => {
      const payload = {
        name,
        description,
        color,
        scopes: Array.from(selectedScopes),
        libraryAccessMode: libMode,
        allowedLibraryIds: libMode === 'explicit_libraries' ? Array.from(allowedLibraryIds) : [],
        autoApproveRequests: autoApprove,
        movieRequestQuota: movieQuota ? Number(movieQuota) : null,
        tvSeasonRequestQuota: tvQuota ? Number(tvQuota) : null,
        musicBookRequestQuota: musicBookQuota ? Number(musicBookQuota) : null,
        cleanerrExclusionLimit: cleanerrExclusionLimit ? Number(cleanerrExclusionLimit) : null,
      };
      if (editing) {
        return apiFetch<RoleDefinition>(`/api/v1/roles/${editing.id}`, {
          method: 'PUT',
          body: JSON.stringify(payload),
        });
      }
      return apiFetch<RoleDefinition>('/api/v1/roles', {
        method: 'POST',
        body: JSON.stringify(payload),
      });
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['roles'] });
      qc.invalidateQueries({ queryKey: ['me'] });
      setCreating(false);
      setEditing(null);
    },
    onError: (err) => setErrMsg(err instanceof ApiError ? err.message : 'Failed to save role.'),
  });

  const cloneMutation = useMutation({
    mutationFn: (role: RoleDefinition) =>
      apiFetch<RoleDefinition>(`/api/v1/roles/${role.id}/clone`, {
        method: 'POST',
        body: JSON.stringify({ name: `${role.name} Copy` }),
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['roles'] }),
  });

  const deleteMutation = useMutation({
    mutationFn: (roleId: number) => apiFetch<void>(`/api/v1/roles/${roleId}`, { method: 'DELETE' }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['roles'] });
      setEditing(null);
    },
  });

  const groupedScopes = (scopesQuery.data ?? []).reduce<Record<string, ScopeDescriptor[]>>((acc, item) => {
    (acc[item.category] ??= []).push(item);
    return acc;
  }, {});

  function toggleScope(scope: string) {
    setSelectedScopes((prev) => {
      const next = new Set(prev);
      if (next.has(scope)) next.delete(scope);
      else next.add(scope);
      return next;
    });
  }

  return (
    <div className="flex flex-col flex-1">
      <PageToolbar
        left={
          <>
            <PageToolbarButton
              icon={RefreshCw}
              label="Refresh"
              spinning={rolesQuery.isFetching}
              onClick={() => void rolesQuery.refetch()}
            />
            {canManageRoles && (
              <>
                <PageToolbarSeparator />
                <PageToolbarButton icon={Plus} label="Add Role" onClick={openNewRole} />
              </>
            )}
          </>
        }
      />

      <PageContentBody>
        <FieldSet legend="Permission Roles">
          <div className="flex flex-wrap gap-4">
            {(rolesQuery.data ?? []).map((role) => (
              <ArrSettingsCard
                key={role.id}
                title={role.name}
                onClick={canManageRoles && !role.isImmutable ? () => openEditRole(role) : undefined}
                topRight={
                  canManageRoles && (
                    <button
                      type="button"
                      title="Clone Role"
                      onClick={(e) => {
                        e.stopPropagation();
                        cloneMutation.mutate(role);
                      }}
                      className="p-1 text-[#909293] hover:text-[#eeeeee] cursor-pointer"
                    >
                      <Copy className="h-4 w-4" />
                    </button>
                  )
                }
                footer={
                  <>
                    <span className="text-[11px] font-mono text-[#909293]">{role.key}</span>
                    <span className="text-[11px] text-[#cccccc]">{role.assignedUserCount} user(s)</span>
                  </>
                }
              >
                <p className="line-clamp-2 text-[#b8b8b8]">{role.description}</p>
                <div className="flex flex-wrap gap-1 pt-1">
                  {role.isImmutable ? (
                    <ArrLabel kind="warning">Immutable</ArrLabel>
                  ) : role.isSystemPreset ? (
                    <ArrLabel kind="primary">Preset</ArrLabel>
                  ) : (
                    <ArrLabel kind="purple">Custom</ArrLabel>
                  )}
                  <ArrLabel kind="default">{role.scopes.length} Scopes</ArrLabel>
                  <ArrLabel kind={role.autoApproveRequests ? 'success' : 'default'}>
                    {role.autoApproveRequests ? 'Auto-Approve' : 'Manual Approval'}
                  </ArrLabel>
                </div>
              </ArrSettingsCard>
            ))}

            {canManageRoles && <ArrAddCard label="Add Custom Role" onClick={openNewRole} />}
          </div>
        </FieldSet>

        {(creating || editing) && (
          <ArrModal
            title={editing ? `Edit Role — ${editing.name}` : 'Add Role'}
            onClose={() => {
              setCreating(false);
              setEditing(null);
            }}
            maxWidth="max-w-3xl"
            footer={
              <div className="flex items-center justify-between w-full">
                <div>
                  {editing && !editing.isSystemPreset && !editing.isImmutable && (
                    <ArrButton
                      variant="danger"
                      busy={deleteMutation.isPending}
                      onClick={() => deleteMutation.mutate(editing.id)}
                    >
                      <Trash2 className="h-3.5 w-3.5" /> Delete
                    </ArrButton>
                  )}
                </div>
                <div className="flex items-center gap-2">
                  <ArrButton
                    onClick={() => {
                      setCreating(false);
                      setEditing(null);
                    }}
                  >
                    Cancel
                  </ArrButton>
                  <ArrButton
                    variant="primary"
                    busy={saveMutation.isPending}
                    disabled={!name.trim()}
                    onClick={() => saveMutation.mutate()}
                  >
                    Save
                  </ArrButton>
                </div>
              </div>
            }
          >
            {errMsg && (
              <ArrAlert kind="danger" className="mb-4">
                {errMsg}
              </ArrAlert>
            )}

            <FormGroup label="Name">
              <input
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="VIP Family"
                className="arr-input"
              />
            </FormGroup>

            <FormGroup label="Description">
              <input
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                placeholder="Role summary"
                className="arr-input"
              />
            </FormGroup>

            <FormGroup
              label="Library Access"
              helpText="Controls whether users inherit their Plex server library shares or access all libraries."
            >
              <select
                value={libMode}
                onChange={(e) => setLibMode(e.target.value as typeof libMode)}
                className="arr-input"
              >
                <option value="inherit_from_plex">Inherit Shared Plex Libraries</option>
                <option value="explicit_libraries">Explicit Library Allowlist</option>
                <option value="all_libraries">All Libraries</option>
              </select>
            </FormGroup>

            {libMode === 'explicit_libraries' && (
              <FormGroup
                label="Allowed Libraries"
                helpText="Select the libraries users with this role may view and request."
              >
                {(librariesQuery.data ?? []).length === 0 ? (
                  <div className="text-[12px] text-[#909293] pt-1.5">
                    {librariesQuery.isLoading ? 'Loading libraries...' : 'No libraries configured.'}
                  </div>
                ) : (
                  <div className="flex flex-wrap gap-2 pt-1">
                    {librariesQuery.data!.map((lib) => {
                      const checked = allowedLibraryIds.has(lib.id);
                      return (
                        <label
                          key={lib.id}
                          className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-[3px] border border-arr-card-border bg-arr-card text-[12px] cursor-pointer"
                        >
                          <input
                            type="checkbox"
                            checked={checked}
                            onChange={() =>
                              setAllowedLibraryIds((prev) => {
                                const next = new Set(prev);
                                if (next.has(lib.id)) next.delete(lib.id);
                                else next.add(lib.id);
                                return next;
                              })
                            }
                          />
                          <span>{lib.name}</span>
                          <span className="text-[10px] text-[#909293] uppercase">{lib.mediaType}</span>
                        </label>
                      );
                    })}
                  </div>
                )}
              </FormGroup>
            )}

            <FormGroup label="Badge Color">
              <input
                type="color"
                value={color}
                onChange={(e) => setColor(e.target.value)}
                className="h-[34px] w-24 rounded-[4px] border border-[#525860] bg-arr-card p-1 cursor-pointer"
              />
            </FormGroup>

            <FormGroup label="Auto-Approve Requests">
              <label className="inline-flex items-center gap-2 pt-1.5 text-[13px] cursor-pointer">
                <input
                  type="checkbox"
                  checked={autoApprove}
                  onChange={(e) => setAutoApprove(e.target.checked)}
                />
                Automatically approve media requests without manual queue review
              </label>
            </FormGroup>

            <FormGroup
              label="Quotas & Cleanerr Limits"
              helpText="Leave blank for unlimited (Rolling window (default 30-day) request quotas and Cleanerr active exclusion cap)."
            >
              <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-2">
                <input
                  type="number"
                  min={0}
                  value={movieQuota}
                  onChange={(e) => setMovieQuota(e.target.value)}
                  placeholder="Movies"
                  aria-label="Movies request quota"
                  className="arr-input"
                />
                <input
                  type="number"
                  min={0}
                  value={tvQuota}
                  onChange={(e) => setTvQuota(e.target.value)}
                  placeholder="TV Seasons"
                  aria-label="TV seasons request quota"
                  className="arr-input"
                />
                <input
                  type="number"
                  min={0}
                  value={musicBookQuota}
                  onChange={(e) => setMusicBookQuota(e.target.value)}
                  placeholder="Music / Books"
                  aria-label="Music and books request quota"
                  className="arr-input"
                />
                <input
                  type="number"
                  min={0}
                  value={cleanerrExclusionLimit}
                  onChange={(e) => setCleanerrExclusionLimit(e.target.value)}
                  placeholder="Cleanerr Exclusions"
                  aria-label="Cleanerr exclusion limit"
                  className="arr-input"
                />
              </div>
            </FormGroup>

            <div className="mt-5 pt-4 border-t border-arr-card-border">
              <div className="text-[14px] font-bold text-[#e1e2e3] mb-3">
                Permission Scopes ({selectedScopes.size} selected)
              </div>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
                {Object.entries(groupedScopes).map(([category, items]) => (
                  <div key={category} className="bg-arr-card border border-[#444444] rounded-[4px] p-3 space-y-2">
                    <div className="text-[12px] font-bold uppercase tracking-wide text-arr-accent">{category}</div>
                    {items.map((item) => (
                      <label key={item.scope} className="flex items-start gap-2 text-[12px] cursor-pointer">
                        <input
                          type="checkbox"
                          checked={selectedScopes.has(item.scope)}
                          onChange={() => toggleScope(item.scope)}
                          className="mt-0.5"
                        />
                        <div>
                          <span className="font-medium text-[#eeeeee]">{item.label}</span>{' '}
                          <code className="text-[11px] text-[#909293]">{item.scope}</code>
                        </div>
                      </label>
                    ))}
                  </div>
                ))}
              </div>
            </div>
          </ArrModal>
        )}
      </PageContentBody>
    </div>
  );
}
