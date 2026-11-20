import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Edit2,
  ExternalLink,
  Eye,
  EyeOff,
  Fingerprint,
  Laptop,
  Plus,
  Smartphone,
  Trash2,
} from 'lucide-react';
import {
  apiFetch,
  ApiError,
  type AuthProvidersResponse,
  type PasskeyDto,
  type PasskeySummaryResponse,
  type PlexUserSummary,
  type ProfileUpdateRequest,
  type ProfileUpdateResponse,
} from '../lib/api';
import {
  getAaguidInfo,
  isPlatformPasskeySupported,
  signalAcceptedCredentials,
  signalCurrentUserDetails,
  startPasskeyRegistration,
} from '../lib/passkeys';
import {
  ArrAlert,
  ArrButton,
  ArrLabel,
  ArrModal,
  FieldSet,
  FormGroup,
} from './arr/ArrPrimitives';

interface PasskeyManagementPanelProps {
  user?: PlexUserSummary;
  onUserUpdated?: (user: PlexUserSummary) => void;
}

function formatPasskeyTimestamp(val: number | string | null | undefined, fallback: string): string {
  if (val === null || val === undefined || val === '') return fallback;
  if (typeof val === 'number') {
    const ms = val < 1_000_000_000_000 ? val * 1000 : val;
    return new Date(ms).toLocaleDateString();
  }
  const trimmed = val.trim();
  if (/^\d+$/.test(trimmed)) {
    const num = Number(trimmed);
    const ms = num < 1_000_000_000_000 ? num * 1000 : num;
    return new Date(ms).toLocaleDateString();
  }
  return new Date(trimmed).toLocaleDateString();
}

export function PasskeyManagementPanel({ user, onUserUpdated }: PasskeyManagementPanelProps) {
  const qc = useQueryClient();
  const [passkeySupported, setPasskeySupported] = useState(false);

  // Modals
  const [showAddModal, setShowAddModal] = useState(false);
  const [newKeyName, setNewKeyName] = useState('');
  const [addBusy, setAddBusy] = useState(false);
  const [addError, setAddError] = useState<string | null>(null);

  const [renameTarget, setRenameTarget] = useState<PasskeyDto | null>(null);
  const [renameValue, setRenameValue] = useState('');
  const [renameBusy, setRenameBusy] = useState(false);
  const [renameError, setRenameError] = useState<string | null>(null);

  // Profile Form State
  const [profileUsername, setProfileUsername] = useState(user?.username ?? '');
  const [profileDisplayName, setProfileDisplayName] = useState(user?.displayName ?? '');
  const [profileEmail, setProfileEmail] = useState(user?.email ?? '');
  const [profileSuccess, setProfileSuccess] = useState<string | null>(null);
  const [profileError, setProfileError] = useState<string | null>(null);

  // Password Change Form State
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [passwordSuccess, setPasswordSuccess] = useState<string | null>(null);
  const [passwordError, setPasswordError] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    void isPlatformPasskeySupported().then((supported) => {
      if (active) setPasskeySupported(supported);
    });
    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    if (user) {
      setProfileUsername(user.username ?? '');
      setProfileDisplayName(user.displayName ?? '');
      setProfileEmail(user.email ?? '');
    }
  }, [user]);

  // Query Auth Providers (for OIDC linking info)
  const providersQuery = useQuery({
    queryKey: ['auth-providers'],
    queryFn: async () => {
      try {
        return await apiFetch<AuthProvidersResponse>('/api/v1/auth/providers');
      } catch {
        return null;
      }
    },
  });

  // Query User's Passkeys
  const passkeysQuery = useQuery({
    queryKey: ['auth-passkeys'],
    queryFn: async () => {
      return await apiFetch<PasskeySummaryResponse>('/api/v1/auth/passkeys');
    },
    enabled: Boolean(user),
  });

  // Signal API synchronization whenever passkeys list is loaded/changed
  useEffect(() => {
    if (passkeysQuery.data) {
      const { rpId, userId, credentials } = passkeysQuery.data;
      if (rpId && userId && Array.isArray(credentials)) {
        signalAcceptedCredentials(
          rpId,
          userId,
          credentials.map((c) => c.id),
        );
      }
    }
  }, [passkeysQuery.data]);

  // Profile Update Mutation
  const profileMutation = useMutation({
    mutationFn: async (payload: ProfileUpdateRequest) => {
      return await apiFetch<ProfileUpdateResponse>('/api/v1/auth/profile', {
        method: 'PUT',
        body: JSON.stringify(payload),
      });
    },
    onSuccess: (data) => {
      setProfileSuccess('Profile updated successfully.');
      setProfileError(null);
      const updatedUser = data.user ?? (data as unknown as PlexUserSummary);
      if (updatedUser?.username) {
        onUserUpdated?.(updatedUser);
      }
      if (data.rpId && data.userId && updatedUser?.username) {
        signalCurrentUserDetails(
          data.rpId,
          data.userId,
          updatedUser.username,
          updatedUser.displayName || undefined,
        );
      }
      void qc.invalidateQueries({ queryKey: ['me'] });
    },
    onError: (err) => {
      setProfileError(err instanceof ApiError ? err.message : 'Failed to update profile.');
      setProfileSuccess(null);
    },
  });

  // Password Change Mutation
  const passwordMutation = useMutation({
    mutationFn: async () => {
      if (newPassword !== confirmPassword) {
        throw new Error('New passwords do not match.');
      }
      if (newPassword.length < 8) {
        throw new Error('New password must be at least 8 characters long.');
      }
      await apiFetch<{ success: boolean }>('/api/v1/auth/local/password', {
        method: 'POST',
        body: JSON.stringify({
          currentPassword: currentPassword || undefined,
          newPassword,
        }),
      });
    },
    onSuccess: () => {
      setPasswordSuccess('Password changed successfully.');
      setPasswordError(null);
      setCurrentPassword('');
      setNewPassword('');
      setConfirmPassword('');
    },
    onError: (err) => {
      setPasswordError(err instanceof Error ? err.message : 'Failed to change password.');
      setPasswordSuccess(null);
    },
  });

  // Delete Passkey Mutation
  const deletePasskeyMutation = useMutation({
    mutationFn: async (credentialId: string) => {
      await apiFetch<void>(`/api/v1/auth/passkeys/${encodeURIComponent(credentialId)}`, {
        method: 'DELETE',
      });
    },
    onSuccess: () => {
      void passkeysQuery.refetch();
    },
  });

  // Rename Passkey Action
  async function handleRenameSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!renameTarget || !renameValue.trim()) return;
    setRenameBusy(true);
    setRenameError(null);
    try {
      await apiFetch<PasskeyDto>(`/api/v1/auth/passkeys/${encodeURIComponent(renameTarget.id)}`, {
        method: 'PUT',
        body: JSON.stringify({ name: renameValue.trim() }),
      });
      setRenameTarget(null);
      setRenameValue('');
      void passkeysQuery.refetch();
    } catch (err) {
      setRenameError(err instanceof ApiError ? err.message : 'Failed to rename passkey.');
    } finally {
      setRenameBusy(false);
    }
  }

  // Add Passkey Action
  async function handleAddPasskeySubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!newKeyName.trim()) return;
    setAddBusy(true);
    setAddError(null);
    try {
      await startPasskeyRegistration({
        name: newKeyName.trim(),
      });
      setShowAddModal(false);
      setNewKeyName('');
      void passkeysQuery.refetch();
    } catch (err: unknown) {
      if (err instanceof DOMException && err.name === 'NotAllowedError') {
        setAddError('Passkey creation was cancelled on your device.');
      } else {
        setAddError(err instanceof Error ? err.message : 'Failed to register passkey.');
      }
    } finally {
      setAddBusy(false);
    }
  }

  const oidcEnabled = Boolean(providersQuery.data?.oidc?.enabled);
  const oidcName = providersQuery.data?.oidc?.displayName || 'Single Sign-On';

  return (
    <div className="space-y-6">
      {/* SECTION 1: PASSKEYS */}
      <FieldSet
        legend="Passkeys & Fast Sign-In"
        action={
          passkeySupported ? (
            <ArrButton
              variant="primary"
              onClick={() => {
                setNewKeyName('');
                setAddError(null);
                setShowAddModal(true);
              }}
            >
              <Plus className="h-3.5 w-3.5" /> Add Passkey
            </ArrButton>
          ) : undefined
        }
      >
        <p className="text-[13px] text-[#909293] mb-4">
          Passkeys enable biometric (Face ID, Touch ID, Windows Hello) and security key logins with no passwords required.
        </p>

        {!passkeySupported && (
          <ArrAlert kind="warning" className="mb-4">
            WebAuthn Passkeys are not supported in this browser or context (requires secure HTTPS or localhost).
          </ArrAlert>
        )}

        {passkeysQuery.isLoading ? (
          <div className="text-[13px] text-[#909293]">Loading registered passkeys...</div>
        ) : (passkeysQuery.data?.credentials ?? []).length === 0 ? (
          <div className="p-4 bg-arr-card border border-arr-card-border rounded-[4px] text-[13px] text-[#909293] flex items-center justify-between">
            <span>No passkeys registered for your account yet.</span>
            {passkeySupported && (
              <ArrButton
                variant="default"
                onClick={() => {
                  setNewKeyName('');
                  setAddError(null);
                  setShowAddModal(true);
                }}
              >
                <Fingerprint className="h-4 w-4 text-arr-accent" /> Register a Passkey
              </ArrButton>
            )}
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="arr-table">
              <thead>
                <tr>
                  <th>Passkey</th>
                  <th>Authenticator</th>
                  <th>Type</th>
                  <th>Backup Status</th>
                  <th>Registered</th>
                  <th>Last Used</th>
                  <th className="w-24 text-right">Actions</th>
                </tr>
              </thead>
              <tbody>
                {(passkeysQuery.data?.credentials ?? []).map((pk) => {
                  const aaguidInfo = getAaguidInfo(pk.aaguid);
                  const isPlatform = pk.credentialDeviceType === 'platform' || pk.credentialDeviceType === 'single_device';
                  return (
                    <tr key={pk.id}>
                      <td className="font-semibold text-[#eeeeee]">
                        <div className="flex items-center gap-2">
                          <Fingerprint className="h-4 w-4 text-arr-accent shrink-0" />
                          <span>{pk.name || 'Unnamed Passkey'}</span>
                        </div>
                      </td>
                      <td className="text-[13px] text-[#cccccc]">
                        <span className="inline-flex items-center gap-1.5">
                          {aaguidInfo.name}
                        </span>
                      </td>
                      <td>
                        <span className="inline-flex items-center gap-1 text-[12px] text-[#909293]">
                          {isPlatform ? <Laptop className="h-3.5 w-3.5" /> : <Smartphone className="h-3.5 w-3.5" />}
                          {pk.credentialDeviceType || 'platform'}
                        </span>
                      </td>
                      <td>
                        {pk.credentialBackedUp ? (
                          <ArrLabel kind="success">Synced (Cloud)</ArrLabel>
                        ) : (
                          <ArrLabel kind="info">Hardware / Single-Device</ArrLabel>
                        )}
                      </td>
                      <td className="text-[12px] text-[#909293]">
                        {formatPasskeyTimestamp(pk.registeredAt, '—')}
                      </td>
                      <td className="text-[12px] text-[#909293]">
                        {formatPasskeyTimestamp(pk.lastUsedAt, 'Never')}
                      </td>
                      <td className="text-right">
                        <div className="inline-flex items-center gap-1">
                          <button
                            type="button"
                            title="Rename Passkey"
                            onClick={() => {
                              setRenameTarget(pk);
                              setRenameValue(pk.name);
                              setRenameError(null);
                            }}
                            className="p-1 text-[#909293] hover:text-arr-accent cursor-pointer"
                          >
                            <Edit2 className="h-3.5 w-3.5" />
                          </button>
                          <button
                            type="button"
                            title="Delete Passkey"
                            onClick={() => {
                              if (confirm(`Delete passkey "${pk.name}"?`)) {
                                deletePasskeyMutation.mutate(pk.id);
                              }
                            }}
                            className="p-1 text-[#f05050] hover:text-white cursor-pointer"
                          >
                            <Trash2 className="h-3.5 w-3.5" />
                          </button>
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

      {/* SECTION 2: USER PROFILE & IDENTITIES */}
      <FieldSet legend="Account Details & Profile">
        {profileSuccess && <ArrAlert kind="success" className="mb-4">{profileSuccess}</ArrAlert>}
        {profileError && <ArrAlert kind="danger" className="mb-4">{profileError}</ArrAlert>}

        <form
          onSubmit={(e) => {
            e.preventDefault();
            profileMutation.mutate({
              username: profileUsername.trim(),
              displayName: profileDisplayName.trim() || undefined,
              email: profileEmail.trim() || undefined,
            });
          }}
          className="space-y-4 max-w-xl"
        >
          <FormGroup label="Username" helpText="Unique identifier used for sign-in and audit logging.">
            <input
              type="text"
              required
              autoComplete="username"
              autoCorrect="off"
              autoCapitalize="none"
              spellCheck={false}
              value={profileUsername}
              onChange={(e) => {
                setProfileSuccess(null);
                setProfileUsername(e.target.value);
              }}
              className="arr-input"
            />
          </FormGroup>

          <FormGroup label="Display Name" helpText="Friendly name displayed across navigation and reviews.">
            <input
              type="text"
              autoComplete="name"
              value={profileDisplayName}
              onChange={(e) => {
                setProfileSuccess(null);
                setProfileDisplayName(e.target.value);
              }}
              className="arr-input"
            />
          </FormGroup>

          <FormGroup label="Email Address" helpText="Used for system notifications and account recovery.">
            <input
              type="email"
              autoComplete="email"
              spellCheck={false}
              value={profileEmail}
              onChange={(e) => {
                setProfileSuccess(null);
                setProfileEmail(e.target.value);
              }}
              className="arr-input"
            />
          </FormGroup>

          <div className="pt-1 flex items-center gap-3">
            <ArrButton type="submit" variant="primary" busy={profileMutation.isPending}>
              Save Profile Changes
            </ArrButton>
            {oidcEnabled && (
              <a
                href="/api/v1/auth/oidc/start?mode=link"
                className="inline-flex items-center gap-1.5 px-3 py-1.5 bg-arr-card hover:bg-[#3d3d3d] border border-[#4a5158] rounded-[3px] text-[13px] font-medium text-[#eeeeee] transition-colors"
              >
                <ExternalLink className="h-3.5 w-3.5 text-[#27c24c]" />
                Link {oidcName} Account
              </a>
            )}
          </div>
        </form>
      </FieldSet>

      {/* SECTION 3: CHANGE PASSWORD (LOCAL AUTH) */}
      <FieldSet legend="Change Password">
        {passwordSuccess && <ArrAlert kind="success" className="mb-4">{passwordSuccess}</ArrAlert>}
        {passwordError && <ArrAlert kind="danger" className="mb-4">{passwordError}</ArrAlert>}

        <form
          onSubmit={(e) => {
            e.preventDefault();
            passwordMutation.mutate();
          }}
          className="space-y-4 max-w-xl"
        >
          <FormGroup label="Current Password" helpText="Required if a local password was previously set.">
            <input
              type={showPassword ? 'text' : 'password'}
              autoComplete="current-password"
              value={currentPassword}
              onChange={(e) => {
                setPasswordSuccess(null);
                setCurrentPassword(e.target.value);
              }}
              className="arr-input"
            />
          </FormGroup>

          <FormGroup label="New Password" helpText="Must be at least 8 characters long.">
            <div className="relative">
              <input
                type={showPassword ? 'text' : 'password'}
                autoComplete="new-password"
                required
                minLength={8}
                value={newPassword}
                onChange={(e) => {
                  setPasswordSuccess(null);
                  setNewPassword(e.target.value);
                }}
                className="arr-input pr-10"
              />
              <button
                type="button"
                onClick={() => setShowPassword((v) => !v)}
                aria-label={showPassword ? 'Hide password' : 'Show password'}
                aria-pressed={showPassword}
                className="absolute right-2.5 top-1/2 -translate-y-1/2 text-[#909293] hover:text-[#e1e2e3] cursor-pointer"
              >
                {showPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
              </button>
            </div>
          </FormGroup>

          <FormGroup label="Confirm New Password" helpText="Re-type the new password to confirm.">
            <input
              type={showPassword ? 'text' : 'password'}
              autoComplete="new-password"
              required
              minLength={8}
              value={confirmPassword}
              onChange={(e) => {
                setPasswordSuccess(null);
                setConfirmPassword(e.target.value);
              }}
              className="arr-input"
            />
          </FormGroup>

          <div className="pt-1">
            <ArrButton type="submit" variant="default" busy={passwordMutation.isPending}>
              Update Password
            </ArrButton>
          </div>
        </form>
      </FieldSet>

      {/* MODAL: ADD PASSKEY */}
      {showAddModal && (
        <ArrModal
          title="Add New Passkey"
          onClose={() => setShowAddModal(false)}
          maxWidth="max-w-md"
          footer={
            <>
              <ArrButton variant="default" onClick={() => setShowAddModal(false)} disabled={addBusy}>
                Cancel
              </ArrButton>
              <ArrButton
                variant="primary"
                form="add-passkey-form"
                type="submit"
                busy={addBusy}
                disabled={!newKeyName.trim()}
              >
                Prompt Device
              </ArrButton>
            </>
          }
        >
          {addError && <ArrAlert kind="danger" className="mb-4">{addError}</ArrAlert>}

          <form id="add-passkey-form" onSubmit={handleAddPasskeySubmit} className="space-y-3">
            <p className="text-[13px] text-[#cccccc]">
              Enter a friendly nickname for this passkey (e.g. &ldquo;MacBook Touch ID&rdquo;, &ldquo;YubiKey 5 NFC&rdquo;). Your browser or operating system will prompt you to complete registration.
            </p>

            <FormGroup label="Passkey Nickname" required>
              <input
                id="new-passkey-name"
                name="passkeyName"
                type="text"
                required
                autoFocus
                placeholder="Work Laptop Touch ID"
                value={newKeyName}
                onChange={(e) => setNewKeyName(e.target.value)}
                className="arr-input"
              />
            </FormGroup>
          </form>
        </ArrModal>
      )}

      {/* MODAL: RENAME PASSKEY */}
      {renameTarget && (
        <ArrModal
          title="Rename Passkey"
          onClose={() => setRenameTarget(null)}
          maxWidth="max-w-md"
          footer={
            <>
              <ArrButton variant="default" onClick={() => setRenameTarget(null)} disabled={renameBusy}>
                Cancel
              </ArrButton>
              <ArrButton
                variant="primary"
                form="rename-passkey-form"
                type="submit"
                busy={renameBusy}
                disabled={!renameValue.trim()}
              >
                Save Name
              </ArrButton>
            </>
          }
        >
          {renameError && <ArrAlert kind="danger" className="mb-4">{renameError}</ArrAlert>}

          <form id="rename-passkey-form" onSubmit={handleRenameSubmit} className="space-y-3">
            <FormGroup label="New Passkey Name" required>
              <input
                id="rename-passkey-name"
                name="name"
                type="text"
                required
                autoFocus
                value={renameValue}
                onChange={(e) => setRenameValue(e.target.value)}
                className="arr-input"
              />
            </FormGroup>
          </form>
        </ArrModal>
      )}
    </div>
  );
}
