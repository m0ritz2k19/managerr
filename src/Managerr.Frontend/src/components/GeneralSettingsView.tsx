import { useEffect, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router';
import {
  AlertTriangle,
  Check,
  CheckCircle2,
  Copy,
  Eye,
  EyeOff,
  RefreshCw,
  Save,
  ShieldCheck,
  X,
} from 'lucide-react';
import clsx from 'clsx';
import {
  apiFetch,
  ApiError,
  type AuthSettingsDto,
  type GeneralSettingsResponse,
  type ResetGeneralApiKeyResponse,
  type TestOidcResult,
} from '../lib/api';
import { ThemeCustomizerSection } from './ThemeCustomizerSection';
import {
  ArrAlert,
  ArrButton,
  PageContentBody,
  PageToolbar,
  PageToolbarButton,
  PageToolbarSeparator,
} from './arr/ArrPrimitives';

interface ExtendedGeneralSettingsResponse extends GeneralSettingsResponse {
  boundServerUrl?: string | null;
  security?: {
    trustForwardedHeaders: boolean;
    trustedProxyCidrs: string[];
    allowedHosts?: string[];
    managedBy?: {
      trustForwardedHeaders?: string;
      trustedProxyCidrs?: string;
      allowedHosts?: string;
    };
  };
  mcp?: {
    enabled: boolean;
    managedBy?: string;
  };
  rollingQuotaWindowDays?: number;
  backupSettings?: {
    maxRetainedBackups: number;
  };
}

interface ExtendedAuthSettingsDto extends AuthSettingsDto {
  local?: { enabled: boolean };
  passkeys?: {
    enabled: boolean;
    rpId?: string;
    rpName?: string;
    requireUserVerification?: boolean;
  };
  plex?: {
    enabled: boolean;
    boundServerUrl?: string | null;
  };
  oidc?: {
    enabled: boolean;
    displayName?: string;
    issuer?: string | null;
    clientId?: string | null;
    hasClientSecret?: boolean;
    scopes?: string;
    autoProvision?: boolean;
    autoRedirect?: boolean;
    trustVerifiedEmail?: boolean;
    usernameClaim?: string;
    emailClaim?: string;
    displayNameClaim?: string;
    groupsClaim?: string;
    adminGroups?: string[];
    managerGroups?: string[];
    requesterGroups?: string[];
  };
  managedBy?: Record<string, string>;
}

function parseCsvList(raw: string): string[] {
  return raw
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean);
}

export function GeneralSettingsView() {
  const qc = useQueryClient();
  const resetDialogRef = useRef<HTMLDialogElement | null>(null);
  const apiKeyInputRef = useRef<HTMLInputElement | null>(null);
  const copyTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  // General Settings State
  const [draftInstanceName, setDraftInstanceName] = useState<string | null>(null);
  const [boundServerUrl, setBoundServerUrl] = useState('');
  const [trustForwardedHeaders, setTrustForwardedHeaders] = useState(false);
  const [trustedProxyCidrsText, setTrustedProxyCidrsText] = useState('');
  const [allowedHostsText, setAllowedHostsText] = useState('');
  const [mcpEnabled, setMcpEnabled] = useState(true);
  const [rollingQuotaWindowDays, setRollingQuotaWindowDays] = useState(30);
  const [generalExtrasDirty, setGeneralExtrasDirty] = useState(false);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [copyState, setCopyState] = useState<'idle' | 'copied' | 'failed'>('idle');
  const [statusAnnouncement, setStatusAnnouncement] = useState('');

  // Auth / Plex / Passkeys / OIDC Settings State
  const [localAuthEnabled, setLocalAuthEnabled] = useState(true);
  const [plexEnabled, setPlexEnabled] = useState(true);
  const [passkeysEnabled, setPasskeysEnabled] = useState(true);
  const [passkeysRpId, setPasskeysRpId] = useState('');
  const [passkeysRpName, setPasskeysRpName] = useState('Managerr');
  const [passkeysRequireUserVerification, setPasskeysRequireUserVerification] = useState(false);

  const [oidcEnabled, setOidcEnabled] = useState(false);
  const [oidcDisplayName, setOidcDisplayName] = useState('');
  const [oidcAuthority, setOidcAuthority] = useState('');
  const [oidcClientId, setOidcClientId] = useState('');
  const [oidcClientSecret, setOidcClientSecret] = useState('');
  const [hasOidcClientSecret, setHasOidcClientSecret] = useState(false);
  const [oidcScope, setOidcScope] = useState('openid profile email');
  const [oidcAutoRedirect, setOidcAutoRedirect] = useState(false);
  const [oidcAutoProvision, setOidcAutoProvision] = useState(true);
  const [oidcTrustVerifiedEmail, setOidcTrustVerifiedEmail] = useState(false);
  const [oidcUsernameClaim, setOidcUsernameClaim] = useState('preferred_username');
  const [oidcEmailClaim, setOidcEmailClaim] = useState('email');
  const [oidcDisplayNameClaim, setOidcDisplayNameClaim] = useState('name');
  const [oidcGroupsClaim, setOidcGroupsClaim] = useState('groups');
  const [oidcAdminGroupsText, setOidcAdminGroupsText] = useState('');
  const [oidcManagerGroupsText, setOidcManagerGroupsText] = useState('');
  const [oidcRequesterGroupsText, setOidcRequesterGroupsText] = useState('');

  const [isOidcManagedByEnv, setIsOidcManagedByEnv] = useState(false);
  const [managedBy, setManagedBy] = useState<Record<string, string>>({});
  const [showSecret, setShowSecret] = useState(false);
  const [authSettingsDirty, setAuthSettingsDirty] = useState(false);
  const [oidcTestResult, setOidcTestResult] = useState<TestOidcResult | null>(null);
  const [oidcTestBusy, setOidcTestBusy] = useState(false);

  const generalQuery = useQuery({
    queryKey: ['settings-general'],
    queryFn: () => apiFetch<ExtendedGeneralSettingsResponse>('/api/v1/settings/general'),
  });

  const authSettingsQuery = useQuery({
    queryKey: ['settings-auth'],
    queryFn: async () => {
      try {
        return await apiFetch<ExtendedAuthSettingsDto>('/api/v1/settings/auth');
      } catch {
        return null;
      }
    },
  });

  useEffect(() => {
    if (generalQuery.data) {
      const g = generalQuery.data;
      if (typeof g.boundServerUrl === 'string') setBoundServerUrl(g.boundServerUrl);
      else if (g.boundServerUrl === null) setBoundServerUrl('');
      if (g.security) {
        setTrustForwardedHeaders(Boolean(g.security.trustForwardedHeaders));
        setTrustedProxyCidrsText((g.security.trustedProxyCidrs ?? []).join(', '));
        setAllowedHostsText((g.security.allowedHosts ?? []).join(', '));
      }
      if (g.mcp && typeof g.mcp.enabled === 'boolean') {
        setMcpEnabled(g.mcp.enabled);
      }
      if (typeof g.rollingQuotaWindowDays === 'number') {
        setRollingQuotaWindowDays(g.rollingQuotaWindowDays);
      }
    }
  }, [generalQuery.data]);

  useEffect(() => {
    if (authSettingsQuery.data) {
      const s = authSettingsQuery.data;
      const effLocal = s.local?.enabled ?? s.localAuthEnabled;
      if (typeof effLocal === 'boolean') setLocalAuthEnabled(effLocal);

      if (s.plex) {
        if (typeof s.plex.enabled === 'boolean') setPlexEnabled(s.plex.enabled);
        if (typeof s.plex.boundServerUrl === 'string') setBoundServerUrl(s.plex.boundServerUrl);
      }

      const effPkEnabled = s.passkeys?.enabled ?? s.passkeysEnabled;
      if (typeof effPkEnabled === 'boolean') setPasskeysEnabled(effPkEnabled);
      if (s.passkeys?.rpId !== undefined) setPasskeysRpId(s.passkeys.rpId ?? '');
      if (s.passkeys?.rpName !== undefined) setPasskeysRpName(s.passkeys.rpName ?? 'Managerr');
      if (typeof s.passkeys?.requireUserVerification === 'boolean') {
        setPasskeysRequireUserVerification(s.passkeys.requireUserVerification);
      }

      const effOidcEnabled = s.oidc?.enabled ?? s.oidcEnabled;
      if (typeof effOidcEnabled === 'boolean') setOidcEnabled(effOidcEnabled);
      const effDisplayName = s.oidc?.displayName ?? s.oidcDisplayName;
      if (effDisplayName !== undefined) setOidcDisplayName(effDisplayName ?? '');
      const effIssuer = s.oidc?.issuer ?? s.oidcAuthority;
      if (effIssuer !== undefined) setOidcAuthority(effIssuer ?? '');
      const effClientId = s.oidc?.clientId ?? s.oidcClientId;
      if (effClientId !== undefined) setOidcClientId(effClientId ?? '');
      const effHasSecret = s.oidc?.hasClientSecret ?? s.hasOidcClientSecret;
      if (typeof effHasSecret === 'boolean') setHasOidcClientSecret(effHasSecret);
      const effScopes = s.oidc?.scopes ?? s.oidcScope;
      if (effScopes !== undefined) setOidcScope(effScopes ?? 'openid profile email');
      const effAutoRedirect = s.oidc?.autoRedirect ?? s.oidcAutoRedirect;
      if (typeof effAutoRedirect === 'boolean') setOidcAutoRedirect(effAutoRedirect);

      if (typeof s.oidc?.autoProvision === 'boolean') setOidcAutoProvision(s.oidc.autoProvision);
      if (typeof s.oidc?.trustVerifiedEmail === 'boolean') setOidcTrustVerifiedEmail(s.oidc.trustVerifiedEmail);
      if (s.oidc?.usernameClaim) setOidcUsernameClaim(s.oidc.usernameClaim);
      if (s.oidc?.emailClaim) setOidcEmailClaim(s.oidc.emailClaim);
      if (s.oidc?.displayNameClaim) setOidcDisplayNameClaim(s.oidc.displayNameClaim);
      if (s.oidc?.groupsClaim) setOidcGroupsClaim(s.oidc.groupsClaim);
      if (Array.isArray(s.oidc?.adminGroups)) setOidcAdminGroupsText(s.oidc.adminGroups.join(', '));
      if (Array.isArray(s.oidc?.managerGroups)) setOidcManagerGroupsText(s.oidc.managerGroups.join(', '));
      if (Array.isArray(s.oidc?.requesterGroups)) setOidcRequesterGroupsText(s.oidc.requesterGroups.join(', '));

      if (typeof s.isOidcManagedByEnv === 'boolean') setIsOidcManagedByEnv(s.isOidcManagedByEnv);
      if (s.managedBy) setManagedBy(s.managedBy);
    }
  }, [authSettingsQuery.data]);

  const isFieldEnvLocked = (key: string) => Boolean(managedBy[key] && managedBy[key] !== 'database');
  const isTfhLocked = Boolean(
    generalQuery.data?.security?.managedBy?.trustForwardedHeaders &&
      generalQuery.data.security.managedBy.trustForwardedHeaders !== 'database',
  );
  const isCidrsLocked = Boolean(
    generalQuery.data?.security?.managedBy?.trustedProxyCidrs &&
      generalQuery.data.security.managedBy.trustedProxyCidrs !== 'database',
  );
  const isAllowedHostsLocked = Boolean(
    generalQuery.data?.security?.managedBy?.allowedHosts &&
      generalQuery.data.security.managedBy.allowedHosts !== 'database',
  );
  const isMcpLocked = Boolean(
    generalQuery.data?.mcp?.managedBy && generalQuery.data.mcp.managedBy !== 'database',
  );

  const data = generalQuery.data;
  const currentInstanceName = draftInstanceName ?? data?.instanceName ?? '';
  const isDirty =
    (draftInstanceName !== null && data !== undefined && draftInstanceName !== data.instanceName) ||
    generalExtrasDirty ||
    authSettingsDirty;

  useEffect(() => {
    return () => {
      if (copyTimerRef.current) {
        clearTimeout(copyTimerRef.current);
      }
    };
  }, []);

  const saveSettingsMutation = useMutation({
    mutationFn: async (variables: { instanceName: string }) => {
      const generalPayload: Record<string, unknown> = {
        instanceName: variables.instanceName,
      };
      if (generalExtrasDirty) {
        if (!isFieldEnvLocked('plex.boundServerUrl')) {
          generalPayload.boundServerUrl = boundServerUrl.trim();
        }
        if (!isTfhLocked) {
          generalPayload.trustForwardedHeaders = trustForwardedHeaders;
        }
        if (!isCidrsLocked) {
          generalPayload.trustedProxyCidrs = parseCsvList(trustedProxyCidrsText);
        }
        if (!isAllowedHostsLocked) {
          generalPayload.allowedHosts = parseCsvList(allowedHostsText);
        }
        if (!isMcpLocked) {
          generalPayload.mcpEnabled = mcpEnabled;
        }
        generalPayload.rollingQuotaWindowDays = Math.max(1, Math.min(365, Number(rollingQuotaWindowDays) || 30));
      }

      const generalResult = await apiFetch<ExtendedGeneralSettingsResponse>('/api/v1/settings/general', {
        method: 'PUT',
        body: JSON.stringify(generalPayload),
      });

      if (authSettingsDirty) {
        try {
          const oidcPayload: Record<string, unknown> = {
            displayName: oidcDisplayName.trim(),
            scopes: oidcScope.trim(),
            autoProvision: oidcAutoProvision,
            trustVerifiedEmail: oidcTrustVerifiedEmail,
            usernameClaim: oidcUsernameClaim.trim(),
            emailClaim: oidcEmailClaim.trim(),
            displayNameClaim: oidcDisplayNameClaim.trim(),
            groupsClaim: oidcGroupsClaim.trim(),
            adminGroups: parseCsvList(oidcAdminGroupsText),
            managerGroups: parseCsvList(oidcManagerGroupsText),
            requesterGroups: parseCsvList(oidcRequesterGroupsText),
          };
          if (!isOidcManagedByEnv && !isFieldEnvLocked('oidc.enabled')) {
            oidcPayload.enabled = oidcEnabled;
          }
          if (!isOidcManagedByEnv && !isFieldEnvLocked('oidc.autoRedirect')) {
            oidcPayload.autoRedirect = oidcAutoRedirect;
          }
          if (!isOidcManagedByEnv && !isFieldEnvLocked('oidc.issuer')) {
            oidcPayload.issuer = oidcAuthority.trim();
          }
          if (!isOidcManagedByEnv && !isFieldEnvLocked('oidc.clientId')) {
            oidcPayload.clientId = oidcClientId.trim();
          }
          if (!isOidcManagedByEnv && !isFieldEnvLocked('oidc.clientSecret') && oidcClientSecret) {
            oidcPayload.clientSecret = oidcClientSecret;
          }

          await apiFetch<ExtendedAuthSettingsDto>('/api/v1/settings/auth', {
            method: 'PUT',
            body: JSON.stringify({
              local: { enabled: localAuthEnabled },
              plex: {
                enabled: plexEnabled,
                boundServerUrl: isFieldEnvLocked('plex.boundServerUrl') ? undefined : boundServerUrl.trim(),
              },
              passkeys: {
                enabled: passkeysEnabled,
                rpId: passkeysRpId.trim(),
                rpName: passkeysRpName.trim(),
                requireUserVerification: passkeysRequireUserVerification,
              },
              oidc: oidcPayload,
            }),
          });
        } catch (err) {
          if (!(err instanceof ApiError && err.status === 404)) {
            throw err;
          }
        }
      }

      return generalResult;
    },
    onSuccess: (updated) => {
      setDraftInstanceName(null);
      setGeneralExtrasDirty(false);
      setAuthSettingsDirty(false);
      setOidcClientSecret('');
      setSuccessMessage('General settings saved successfully.');
      setErrorMessage(null);
      qc.setQueryData<ExtendedGeneralSettingsResponse>(['settings-general'], updated);
      void qc.invalidateQueries({ queryKey: ['settings-general'] });
      void qc.invalidateQueries({ queryKey: ['settings-auth'] });
      void qc.invalidateQueries({ queryKey: ['auth-providers'] });
    },
    onError: (err) => {
      setErrorMessage(err instanceof Error ? err.message : 'Failed to save general settings');
    },
  });

  const resetApiKeyMutation = useMutation({
    mutationFn: () =>
      apiFetch<ResetGeneralApiKeyResponse>('/api/v1/settings/general/api-key/reset', {
        method: 'POST',
      }),
    onSuccess: (res) => {
      resetDialogRef.current?.close('confirmed');
      setSuccessMessage('API Key has been reset successfully.');
      setErrorMessage(null);
      qc.setQueryData<GeneralSettingsResponse>(['settings-general'], (old) =>
        old ? { ...old, apiKey: res.apiKey } : old,
      );
      void qc.invalidateQueries({ queryKey: ['settings-general'] });
    },
    onError: (err) => {
      setErrorMessage(err instanceof Error ? err.message : 'Failed to reset API key');
    },
  });

  const handleCopyApiKey = async () => {
    if (copyTimerRef.current) {
      clearTimeout(copyTimerRef.current);
    }
    const keyToCopy = data?.apiKey ?? '';
    try {
      await navigator.clipboard.writeText(keyToCopy);
      setCopyState('copied');
      const nextText = 'Copied';
      setStatusAnnouncement((prev) => (prev === nextText ? `${nextText}\u00A0` : nextText));
    } catch {
      setCopyState('failed');
      const nextText = 'Copy failed';
      setStatusAnnouncement((prev) => (prev === nextText ? `${nextText}\u00A0` : nextText));
      apiKeyInputRef.current?.select();
    }
    copyTimerRef.current = setTimeout(() => {
      setCopyState('idle');
      setStatusAnnouncement('');
    }, 2000);
  };

  const openResetDialog = () => {
    resetDialogRef.current?.showModal();
  };

  const handleDialogClose = () => {
    // Clean-up on close
  };

  const handleSave = () => {
    saveSettingsMutation.mutate({
      instanceName: currentInstanceName,
    });
  };

  // Test OIDC Discovery handler
  const handleTestOidc = async () => {
    if (!oidcAuthority.trim()) return;
    setOidcTestBusy(true);
    setOidcTestResult(null);
    try {
      const res = await apiFetch<TestOidcResult>('/api/v1/settings/auth/test-oidc', {
        method: 'POST',
        body: JSON.stringify({
          authority: oidcAuthority.trim(),
          issuer: oidcAuthority.trim(),
          clientId: oidcClientId.trim() || undefined,
          clientSecret: oidcClientSecret || undefined,
        }),
      });
      setOidcTestResult(res);
    } catch (err) {
      setOidcTestResult({
        success: false,
        error: err instanceof Error ? err.message : 'OIDC Discovery failed',
      });
    } finally {
      setOidcTestBusy(false);
    }
  };

  const apiKey = data?.apiKey ?? '••••••••••••••••••••••••••••••••';
  const authDisplayValue = data
    ? `${data.authenticationMethod}${data.authenticationRequired ? ` (${data.authenticationRequired})` : ''}`
    : 'Forms / Plex OAuth (Required)';

  return (
    <div className="flex flex-col flex-1">
      <PageToolbar
        left={
          <>
            <PageToolbarButton
              icon={Save}
              label="Save Settings"
              title="Save settings"
              disabled={!isDirty || saveSettingsMutation.isPending}
              spinning={saveSettingsMutation.isPending}
              onClick={handleSave}
            />
            <PageToolbarSeparator />
            <PageToolbarButton
              icon={RefreshCw}
              label="Refresh"
              title="Refresh settings"
              onClick={() => {
                void generalQuery.refetch();
                void authSettingsQuery.refetch();
              }}
              spinning={generalQuery.isFetching || authSettingsQuery.isFetching}
            />
          </>
        }
      />

      <PageContentBody>
        <div className="max-w-4xl">
          {successMessage && (
            <ArrAlert kind="success" className="mb-5">
              {successMessage}
            </ArrAlert>
          )}

          {(errorMessage || generalQuery.isError) && (
            <ArrAlert kind="danger" className="mb-5">
              {errorMessage ||
                (generalQuery.error instanceof Error
                  ? generalQuery.error.message
                  : 'Failed to load general settings')}
            </ArrAlert>
          )}

          <form
            onSubmit={(e) => {
              e.preventDefault();
              handleSave();
            }}
          >
            {/* Fieldset 1: Host */}
            <fieldset className="m-0 mb-7 p-0 min-w-0 border-0">
              <legend className="w-full p-0 pb-1.5 mb-5 text-[20px] font-normal text-[#e1e2e3] leading-snug border-b border-[#444444]">
                Host
              </legend>

              {/* Row 1: Instance Name */}
              <div className="grid grid-cols-1 md:grid-cols-[220px_minmax(0,440px)] items-start gap-x-5 gap-y-1 mb-4">
                <label
                  htmlFor="general-instance-name"
                  className="md:text-right md:pt-1.5 text-[13px] font-bold text-[#e1e2e3] leading-snug"
                >
                  Instance Name
                </label>
                <div>
                  <input
                    id="general-instance-name"
                    name="instanceName"
                    type="text"
                    className="arr-input"
                    value={draftInstanceName ?? data?.instanceName ?? ''}
                    onChange={(e) => {
                      setDraftInstanceName(e.target.value);
                      setSuccessMessage(null);
                    }}
                  />
                  <p className="mt-1 text-[12px] text-[#909293] leading-snug">
                    Instance name shown in notifications and client integrations.
                  </p>
                </div>
              </div>

              {/* Row 2: Compat URL Bases */}
              <div className="grid grid-cols-1 md:grid-cols-[220px_minmax(0,440px)] items-start gap-x-5 gap-y-1 mb-4">
                <div className="md:text-right md:pt-1.5 text-[13px] font-bold text-[#e1e2e3] leading-snug">
                  Compat URL Bases
                </div>
                <div>
                  <div className="flex flex-wrap gap-1.5 pt-1">
                    {Object.entries(
                      data?.compatUrlBases ?? {
                        sonarr: '/compat/sonarr',
                        radarr: '/compat/radarr',
                        lidarr: '/compat/lidarr',
                        readarr: '/compat/readarr',
                        prowlarr: '/compat/prowlarr',
                      },
                    ).map(([service, basePath]) => (
                      <span
                        key={service}
                        className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-[#222222] border border-[#393f45] text-[12px] font-mono text-arr-accent"
                      >
                        <span className="text-[#909293] font-sans capitalize">{service}:</span>
                        {basePath}
                      </span>
                    ))}
                  </div>
                  <p className="mt-1 text-[12px] text-[#909293] leading-snug">
                    Zero-configuration compatibility endpoints for connecting Seerr, Bazarr, Recyclarr, and *arr tools.
                  </p>
                </div>
              </div>
            </fieldset>

            {/* Fieldset 2: Security */}
            <fieldset className="m-0 mb-7 p-0 min-w-0 border-0">
              <legend className="w-full p-0 pb-1.5 mb-5 text-[20px] font-normal text-[#e1e2e3] leading-snug border-b border-[#444444]">
                Security
              </legend>

              {/* Row 1: Authentication */}
              <div className="grid grid-cols-1 md:grid-cols-[220px_minmax(0,440px)] items-start gap-x-5 gap-y-1 mb-4">
                <label
                  htmlFor="general-auth-method"
                  className="md:text-right md:pt-1.5 text-[13px] font-bold text-[#e1e2e3] leading-snug"
                >
                  Authentication
                </label>
                <div>
                  <input
                    id="general-auth-method"
                    type="text"
                    readOnly
                    value={authDisplayValue}
                    className="arr-input !bg-[#222222] text-[#cccccc]"
                  />
                  <p className="mt-1 text-[12px] text-[#909293] leading-snug">
                    Authentication method enforced across the web UI and API.
                  </p>
                </div>
              </div>

              {/* Row 2: API Key */}
              <div className="grid grid-cols-1 md:grid-cols-[220px_minmax(0,440px)] items-start gap-x-5 gap-y-1 mb-4">
                <label
                  htmlFor="general-api-key"
                  className="md:text-right md:pt-1.5 text-[13px] font-bold text-[#e1e2e3] leading-snug"
                >
                  API Key
                </label>
                <div>
                  <div className="flex items-stretch">
                    <input
                      id="general-api-key"
                      name="apiKey"
                      ref={apiKeyInputRef}
                      type="text"
                      readOnly
                      value={apiKey}
                      onFocus={(e) => e.currentTarget.select()}
                      autoCorrect="off"
                      autoCapitalize="none"
                      spellCheck={false}
                      className="arr-input !rounded-r-none !bg-[#222222] font-mono text-[13px] select-all"
                    />

                    <div className="relative inline-flex items-stretch">
                      <button
                        type="button"
                        aria-label="Copy API key"
                        title="Copy to clipboard"
                        onClick={handleCopyApiKey}
                        className="inline-flex items-center justify-center min-w-[36px] min-h-[34px] px-2.5 bg-[#333333] hover:bg-[#444444] border-y border-r border-[#525860] text-[#eeeeee] transition-colors cursor-pointer"
                      >
                        {copyState === 'copied' ? (
                          <Check className="h-4 w-4 text-[#27c24c]" aria-hidden="true" />
                        ) : copyState === 'failed' ? (
                          <AlertTriangle className="h-4 w-4 text-[#f05050]" aria-hidden="true" />
                        ) : (
                          <Copy className="h-4 w-4" aria-hidden="true" />
                        )}
                      </button>

                      <span
                        role="status"
                        className={clsx(
                          'pointer-events-none select-none absolute -top-8 left-1/2 -translate-x-1/2 whitespace-nowrap rounded px-2 py-0.5 text-[11px] font-medium bg-[#191919] text-[#eeeeee] border border-[#4a5158] shadow transition-opacity duration-150 z-20',
                          copyState !== 'idle' ? 'opacity-100' : 'opacity-0',
                        )}
                      >
                        {statusAnnouncement}
                      </span>
                    </div>

                    <button
                      type="button"
                      aria-label="Reset API key"
                      title="Reset API Key"
                      onClick={openResetDialog}
                      className="inline-flex items-center justify-center min-w-[36px] min-h-[34px] px-2.5 bg-[#f05050] hover:bg-[#ee3d3d] border border-l-0 border-[#ee3d3d] rounded-r-[4px] text-white transition-colors cursor-pointer"
                    >
                      <RefreshCw
                        className={clsx('h-4 w-4', resetApiKeyMutation.isPending && 'animate-spin')}
                        aria-hidden="true"
                      />
                    </button>
                  </div>

                  <div className="mt-1.5 space-y-1 text-[12px] leading-snug">
                    <p className="text-[#ffa500]">
                      Resetting your API Key requires updating Seerr, Prowlarr, Bazarr, and any third-party apps using it.
                    </p>
                    <p className="text-[#909293]">
                      Scoped, least-privilege tokens can also be managed in{' '}
                      <Link to="/system/security" className="text-arr-accent hover:underline">
                        System → Security
                      </Link>
                      .
                    </p>
                  </div>
                </div>
              </div>
            </fieldset>

            {/* Fieldset 3: Appearance & Theme */}
            <fieldset className="m-0 mb-7 p-0 min-w-0 border-0">
              <legend className="w-full p-0 pb-1.5 mb-5 text-[20px] font-normal text-[#e1e2e3] leading-snug border-b border-[#444444]">
                Appearance & Theme
              </legend>
              <ThemeCustomizerSection managedBy={managedBy} />
            </fieldset>

            {/* Fieldset 4: Authentication & Single Sign-On */}
            <fieldset className="m-0 mb-7 p-0 min-w-0 border-0">
              <legend className="w-full p-0 pb-1.5 mb-5 text-[20px] font-normal text-[#e1e2e3] leading-snug border-b border-[#444444]">
                Authentication & Single Sign-On
              </legend>

              {/* Providers Status Flags */}
              <div className="grid grid-cols-1 md:grid-cols-[220px_minmax(0,440px)] items-start gap-x-5 gap-y-2 mb-4">
                <div className="md:text-right md:pt-1 text-[13px] font-bold text-[#e1e2e3] leading-snug">
                  Enabled Providers
                </div>
                <div className="space-y-2 pt-0.5">
                  <label className="flex items-center gap-2.5 text-[13px] text-[#eeeeee] cursor-pointer">
                    <input
                      type="checkbox"
                      disabled={isFieldEnvLocked('plex.enabled')}
                      checked={plexEnabled}
                      onChange={(e) => {
                        setPlexEnabled(e.target.checked);
                        setAuthSettingsDirty(true);
                        setSuccessMessage(null);
                      }}
                    />
                    <span>Plex OAuth Authentication</span>
                    {isFieldEnvLocked('plex.enabled') && (
                      <span className="ml-1 px-1.5 py-0.5 rounded text-[10px] font-semibold bg-[#2e3e4e] text-[#69c0ff] border border-[#1b3a57]">
                        Managed by {managedBy['plex.enabled']}
                      </span>
                    )}
                  </label>

                  <label className="flex items-center gap-2.5 text-[13px] text-[#eeeeee] cursor-pointer">
                    <input
                      type="checkbox"
                      disabled={isFieldEnvLocked('local.enabled')}
                      checked={localAuthEnabled}
                      onChange={(e) => {
                        setLocalAuthEnabled(e.target.checked);
                        setAuthSettingsDirty(true);
                        setSuccessMessage(null);
                      }}
                    />
                    <span>Local Username & Password Authentication</span>
                  </label>

                  <label className="flex items-center gap-2.5 text-[13px] text-[#eeeeee] cursor-pointer">
                    <input
                      type="checkbox"
                      disabled={isFieldEnvLocked('passkeys.enabled')}
                      checked={passkeysEnabled}
                      onChange={(e) => {
                        setPasskeysEnabled(e.target.checked);
                        setAuthSettingsDirty(true);
                        setSuccessMessage(null);
                      }}
                    />
                    <span>WebAuthn Biometrics & Hardware Passkeys</span>
                  </label>

                  <label className="flex items-center gap-2.5 text-[13px] text-[#eeeeee] cursor-pointer">
                    <input
                      type="checkbox"
                      disabled={isOidcManagedByEnv || isFieldEnvLocked('oidc.enabled')}
                      checked={oidcEnabled}
                      onChange={(e) => {
                        setOidcEnabled(e.target.checked);
                        setAuthSettingsDirty(true);
                        setSuccessMessage(null);
                      }}
                    />
                    <span>OpenID Connect (OIDC) Single Sign-On</span>
                    {(isOidcManagedByEnv || isFieldEnvLocked('oidc.enabled')) && (
                      <span className="ml-1 px-1.5 py-0.5 rounded text-[10px] font-semibold bg-[#2e3e4e] text-[#69c0ff] border border-[#1b3a57]">
                        Managed by Compose
                      </span>
                    )}
                  </label>
                </div>
              </div>

              {/* Plex Server Binding URL */}
              <div className="grid grid-cols-1 md:grid-cols-[220px_minmax(0,440px)] items-start gap-x-5 gap-y-1 mb-4">
                <label
                  htmlFor="plex-bound-server-url"
                  className="md:text-right md:pt-1.5 text-[13px] font-bold text-[#e1e2e3] leading-snug"
                >
                  Plex Server URL Override
                </label>
                <div>
                  <input
                    id="plex-bound-server-url"
                    type="url"
                    disabled={isFieldEnvLocked('plex.boundServerUrl')}
                    placeholder="https://plex.example.com:32400"
                    value={boundServerUrl}
                    onChange={(e) => {
                      setBoundServerUrl(e.target.value);
                      setAuthSettingsDirty(true);
                      setGeneralExtrasDirty(true);
                      setSuccessMessage(null);
                    }}
                    className="arr-input"
                  />
                  <p className="mt-1 text-[12px] text-[#909293] leading-snug">
                    Optional direct Plex Media Server URL override for Cleanerr/metadata sync (leave blank to auto-discover).
                  </p>
                </div>
              </div>

              {/* Passkeys / WebAuthn Settings */}
              {passkeysEnabled && (
                <div className="p-4 bg-[#232323] border border-[#393f45] rounded-[4px] space-y-4 mb-4">
                  <div className="text-[14px] font-semibold text-[#eeeeee] pb-2 border-b border-[#333333]">
                    Passkeys / WebAuthn Configuration
                  </div>

                  <div className="grid grid-cols-1 sm:grid-cols-[180px_1fr] items-start gap-3">
                    <label htmlFor="passkeys-rp-id" className="sm:text-right pt-1.5 text-[12px] font-bold text-[#cccccc]">
                      Relying Party ID (RP ID)
                    </label>
                    <div>
                      <input
                        id="passkeys-rp-id"
                        type="text"
                        disabled={isFieldEnvLocked('passkeys.rpId')}
                        placeholder="managerr.example.com"
                        value={passkeysRpId}
                        onChange={(e) => {
                          setPasskeysRpId(e.target.value);
                          setAuthSettingsDirty(true);
                          setSuccessMessage(null);
                        }}
                        className="arr-input"
                      />
                      <p className="mt-1 text-[11px] text-[#909293]">
                        Effective domain for WebAuthn credentials (defaults to request host when empty).
                      </p>
                    </div>
                  </div>

                  <div className="grid grid-cols-1 sm:grid-cols-[180px_1fr] items-start gap-3">
                    <label htmlFor="passkeys-rp-name" className="sm:text-right pt-1.5 text-[12px] font-bold text-[#cccccc]">
                      Relying Party Name
                    </label>
                    <input
                      id="passkeys-rp-name"
                      type="text"
                      disabled={isFieldEnvLocked('passkeys.rpName')}
                      placeholder="Managerr"
                      value={passkeysRpName}
                      onChange={(e) => {
                        setPasskeysRpName(e.target.value);
                        setAuthSettingsDirty(true);
                        setSuccessMessage(null);
                      }}
                      className="arr-input"
                    />
                  </div>

                  <div className="grid grid-cols-1 sm:grid-cols-[180px_1fr] items-start gap-3">
                    <div className="sm:text-right text-[12px] font-bold text-[#cccccc]">User Verification</div>
                    <label className="flex items-center gap-2 text-[12px] text-[#cccccc] cursor-pointer">
                      <input
                        type="checkbox"
                        disabled={isFieldEnvLocked('passkeys.requireUserVerification')}
                        checked={passkeysRequireUserVerification}
                        onChange={(e) => {
                          setPasskeysRequireUserVerification(e.target.checked);
                          setAuthSettingsDirty(true);
                          setSuccessMessage(null);
                        }}
                      />
                      <span>Require biometric/PIN user verification on passkey prompts</span>
                    </label>
                  </div>
                </div>
              )}

              {/* OIDC Config Rows (Visible when OIDC enabled) */}
              {oidcEnabled && (
                <div className="p-4 bg-[#232323] border border-[#393f45] rounded-[4px] space-y-4 mb-4">
                  <div className="flex items-center justify-between pb-2 border-b border-[#333333]">
                    <div className="text-[14px] font-semibold text-[#eeeeee] flex items-center gap-2">
                      <ShieldCheck className="h-4 w-4 text-[#27c24c]" />
                      OIDC Identity Provider Configuration
                    </div>
                    {isOidcManagedByEnv && (
                      <span className="text-[11px] text-[#909293]">Configured via environment variables</span>
                    )}
                  </div>

                  {/* OIDC Display Name */}
                  <div className="grid grid-cols-1 sm:grid-cols-[180px_1fr] items-start gap-3">
                    <label htmlFor="oidc-display-name" className="sm:text-right pt-1.5 text-[12px] font-bold text-[#cccccc]">
                      Provider Display Name
                    </label>
                    <div>
                      <input
                        id="oidc-display-name"
                        type="text"
                        disabled={isOidcManagedByEnv || isFieldEnvLocked('oidc.displayName')}
                        placeholder="Keycloak / Authentik / Google"
                        value={oidcDisplayName}
                        onChange={(e) => {
                          setOidcDisplayName(e.target.value);
                          setAuthSettingsDirty(true);
                          setSuccessMessage(null);
                        }}
                        className="arr-input"
                      />
                      <p className="mt-1 text-[11px] text-[#909293]">Name shown on the Sign In button.</p>
                    </div>
                  </div>

                  {/* OIDC Authority / Issuer */}
                  <div className="grid grid-cols-1 sm:grid-cols-[180px_1fr] items-start gap-3">
                    <label htmlFor="oidc-authority" className="sm:text-right pt-1.5 text-[12px] font-bold text-[#cccccc]">
                      Issuer / Authority URL
                    </label>
                    <div>
                      <div className="flex items-stretch gap-2">
                        <input
                          id="oidc-authority"
                          type="url"
                          disabled={isOidcManagedByEnv || isFieldEnvLocked('oidc.issuer')}
                          placeholder="https://auth.example.com/realms/master"
                          value={oidcAuthority}
                          onChange={(e) => {
                            setOidcAuthority(e.target.value);
                            setAuthSettingsDirty(true);
                            setSuccessMessage(null);
                          }}
                          className="arr-input"
                        />
                        <ArrButton
                          type="button"
                          variant="default"
                          busy={oidcTestBusy}
                          disabled={!oidcAuthority.trim()}
                          onClick={handleTestOidc}
                          className="shrink-0"
                        >
                          Test Discovery
                        </ArrButton>
                      </div>
                      <p className="mt-1 text-[11px] text-[#909293]">
                        Base URL of the IdP hosting <code>/.well-known/openid-configuration</code>.
                      </p>

                      {oidcTestResult && (
                        <div className="mt-2">
                          {oidcTestResult.success ? (
                            <div className="p-2.5 bg-[#1c392b] border border-[#276738] rounded text-[12px] text-[#d9f7be] flex items-start gap-2">
                              <CheckCircle2 className="h-4 w-4 text-[#52c41a] shrink-0 mt-0.5" />
                              <div>
                                <div className="font-semibold">OIDC Discovery Successful</div>
                                <div className="text-[11px] text-[#b7eb8f] font-mono mt-0.5">
                                  Issuer: {oidcTestResult.issuer}
                                </div>
                              </div>
                            </div>
                          ) : (
                            <div className="p-2.5 bg-[#431418] border border-[#791a1f] rounded text-[12px] text-[#ffccc7] flex items-start gap-2">
                              <AlertTriangle className="h-4 w-4 text-[#ff4d4f] shrink-0 mt-0.5" />
                              <div>
                                <div className="font-semibold">OIDC Discovery Failed</div>
                                <div className="text-[11px] text-[#ffa39e] mt-0.5">
                                  {oidcTestResult.error}
                                </div>
                              </div>
                            </div>
                          )}
                        </div>
                      )}
                    </div>
                  </div>

                  {/* OIDC Client ID */}
                  <div className="grid grid-cols-1 sm:grid-cols-[180px_1fr] items-start gap-3">
                    <label htmlFor="oidc-client-id" className="sm:text-right pt-1.5 text-[12px] font-bold text-[#cccccc]">
                      Client ID
                    </label>
                    <input
                      id="oidc-client-id"
                      type="text"
                      disabled={isOidcManagedByEnv || isFieldEnvLocked('oidc.clientId')}
                      value={oidcClientId}
                      onChange={(e) => {
                        setOidcClientId(e.target.value);
                        setAuthSettingsDirty(true);
                        setSuccessMessage(null);
                      }}
                      className="arr-input"
                    />
                  </div>

                  {/* OIDC Client Secret */}
                  <div className="grid grid-cols-1 sm:grid-cols-[180px_1fr] items-start gap-3">
                    <label htmlFor="oidc-client-secret" className="sm:text-right pt-1.5 text-[12px] font-bold text-[#cccccc]">
                      Client Secret
                    </label>
                    <div className="relative">
                      <input
                        id="oidc-client-secret"
                        type={showSecret ? 'text' : 'password'}
                        disabled={isOidcManagedByEnv || isFieldEnvLocked('oidc.clientSecret')}
                        placeholder={hasOidcClientSecret ? '•••••••• (Saved — leave blank to keep)' : ''}
                        value={oidcClientSecret}
                        onChange={(e) => {
                          setOidcClientSecret(e.target.value);
                          setAuthSettingsDirty(true);
                          setSuccessMessage(null);
                        }}
                        className="arr-input pr-10"
                      />
                      <button
                        type="button"
                        onClick={() => setShowSecret((v) => !v)}
                        aria-label={showSecret ? 'Hide secret' : 'Show secret'}
                        className="absolute right-2.5 top-1/2 -translate-y-1/2 text-[#909293] hover:text-[#e1e2e3] cursor-pointer"
                      >
                        {showSecret ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
                      </button>
                    </div>
                  </div>

                  {/* OIDC Scopes */}
                  <div className="grid grid-cols-1 sm:grid-cols-[180px_1fr] items-start gap-3">
                    <label htmlFor="oidc-scopes" className="sm:text-right pt-1.5 text-[12px] font-bold text-[#cccccc]">
                      Requested Scopes
                    </label>
                    <input
                      id="oidc-scopes"
                      type="text"
                      disabled={isOidcManagedByEnv || isFieldEnvLocked('oidc.scopes')}
                      value={oidcScope}
                      onChange={(e) => {
                        setOidcScope(e.target.value);
                        setAuthSettingsDirty(true);
                        setSuccessMessage(null);
                      }}
                      className="arr-input"
                    />
                  </div>

                  {/* Auto-Redirect & Provisioning Checkboxes */}
                  <div className="grid grid-cols-1 sm:grid-cols-[180px_1fr] items-start gap-3">
                    <div className="sm:text-right text-[12px] font-bold text-[#cccccc]">Provisioning & Login</div>
                    <div className="space-y-2">
                      <label className="flex items-center gap-2 text-[12px] text-[#cccccc] cursor-pointer">
                        <input
                          type="checkbox"
                          disabled={isOidcManagedByEnv || isFieldEnvLocked('oidc.autoRedirect')}
                          checked={oidcAutoRedirect}
                          onChange={(e) => {
                            setOidcAutoRedirect(e.target.checked);
                            setAuthSettingsDirty(true);
                            setSuccessMessage(null);
                          }}
                        />
                        <span>Automatically redirect unauthenticated users to OIDC IdP (bypass via <code>?local=1</code>)</span>
                      </label>

                      <label className="flex items-center gap-2 text-[12px] text-[#cccccc] cursor-pointer">
                        <input
                          type="checkbox"
                          disabled={isFieldEnvLocked('oidc.autoProvision')}
                          checked={oidcAutoProvision}
                          onChange={(e) => {
                            setOidcAutoProvision(e.target.checked);
                            setAuthSettingsDirty(true);
                            setSuccessMessage(null);
                          }}
                        />
                        <span>Auto-provision new Managerr users on first OIDC sign-in</span>
                      </label>

                      <label className="flex items-center gap-2 text-[12px] text-[#cccccc] cursor-pointer">
                        <input
                          type="checkbox"
                          disabled={isFieldEnvLocked('oidc.trustVerifiedEmail')}
                          checked={oidcTrustVerifiedEmail}
                          onChange={(e) => {
                            setOidcTrustVerifiedEmail(e.target.checked);
                            setAuthSettingsDirty(true);
                            setSuccessMessage(null);
                          }}
                        />
                        <span>Trust verified email claim to match existing accounts</span>
                      </label>
                    </div>
                  </div>

                  {/* OIDC Claims Mapping */}
                  <div className="grid grid-cols-1 sm:grid-cols-[180px_1fr] items-start gap-3">
                    <div className="sm:text-right pt-1.5 text-[12px] font-bold text-[#cccccc]">Claim Mapping</div>
                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-2.5">
                      <div>
                        <label htmlFor="oidc-username-claim" className="block text-[11px] text-[#909293] mb-1">
                          Username Claim
                        </label>
                        <input
                          id="oidc-username-claim"
                          type="text"
                          disabled={isFieldEnvLocked('oidc.usernameClaim')}
                          value={oidcUsernameClaim}
                          onChange={(e) => {
                            setOidcUsernameClaim(e.target.value);
                            setAuthSettingsDirty(true);
                            setSuccessMessage(null);
                          }}
                          className="arr-input"
                        />
                      </div>
                      <div>
                        <label htmlFor="oidc-email-claim" className="block text-[11px] text-[#909293] mb-1">
                          Email Claim
                        </label>
                        <input
                          id="oidc-email-claim"
                          type="text"
                          disabled={isFieldEnvLocked('oidc.emailClaim')}
                          value={oidcEmailClaim}
                          onChange={(e) => {
                            setOidcEmailClaim(e.target.value);
                            setAuthSettingsDirty(true);
                            setSuccessMessage(null);
                          }}
                          className="arr-input"
                        />
                      </div>
                      <div>
                        <label htmlFor="oidc-displayname-claim" className="block text-[11px] text-[#909293] mb-1">
                          Display Name Claim
                        </label>
                        <input
                          id="oidc-displayname-claim"
                          type="text"
                          disabled={isFieldEnvLocked('oidc.displayNameClaim')}
                          value={oidcDisplayNameClaim}
                          onChange={(e) => {
                            setOidcDisplayNameClaim(e.target.value);
                            setAuthSettingsDirty(true);
                            setSuccessMessage(null);
                          }}
                          className="arr-input"
                        />
                      </div>
                      <div>
                        <label htmlFor="oidc-groups-claim" className="block text-[11px] text-[#909293] mb-1">
                          Groups Claim
                        </label>
                        <input
                          id="oidc-groups-claim"
                          type="text"
                          disabled={isFieldEnvLocked('oidc.groupsClaim')}
                          value={oidcGroupsClaim}
                          onChange={(e) => {
                            setOidcGroupsClaim(e.target.value);
                            setAuthSettingsDirty(true);
                            setSuccessMessage(null);
                          }}
                          className="arr-input"
                        />
                      </div>
                    </div>
                  </div>

                  {/* OIDC Role Group Mappings */}
                  <div className="grid grid-cols-1 sm:grid-cols-[180px_1fr] items-start gap-3">
                    <div className="sm:text-right pt-1.5 text-[12px] font-bold text-[#cccccc]">Group Role Mapping</div>
                    <div className="space-y-2.5">
                      <div>
                        <label htmlFor="oidc-admin-groups" className="block text-[11px] text-[#909293] mb-1">
                          Admin Groups (comma-separated)
                        </label>
                        <input
                          id="oidc-admin-groups"
                          type="text"
                          disabled={isFieldEnvLocked('oidc.adminGroups')}
                          placeholder="managerr-admins, admins"
                          value={oidcAdminGroupsText}
                          onChange={(e) => {
                            setOidcAdminGroupsText(e.target.value);
                            setAuthSettingsDirty(true);
                            setSuccessMessage(null);
                          }}
                          className="arr-input"
                        />
                      </div>
                      <div>
                        <label htmlFor="oidc-manager-groups" className="block text-[11px] text-[#909293] mb-1">
                          Manager Groups (comma-separated)
                        </label>
                        <input
                          id="oidc-manager-groups"
                          type="text"
                          disabled={isFieldEnvLocked('oidc.managerGroups')}
                          placeholder="managerr-managers"
                          value={oidcManagerGroupsText}
                          onChange={(e) => {
                            setOidcManagerGroupsText(e.target.value);
                            setAuthSettingsDirty(true);
                            setSuccessMessage(null);
                          }}
                          className="arr-input"
                        />
                      </div>
                      <div>
                        <label htmlFor="oidc-requester-groups" className="block text-[11px] text-[#909293] mb-1">
                          Requester Groups (comma-separated)
                        </label>
                        <input
                          id="oidc-requester-groups"
                          type="text"
                          disabled={isFieldEnvLocked('oidc.requesterGroups')}
                          placeholder="plex-users"
                          value={oidcRequesterGroupsText}
                          onChange={(e) => {
                            setOidcRequesterGroupsText(e.target.value);
                            setAuthSettingsDirty(true);
                            setSuccessMessage(null);
                          }}
                          className="arr-input"
                        />
                      </div>
                    </div>
                  </div>
                </div>
              )}
            </fieldset>

            {/* Fieldset 5: Reverse Proxy, MCP Server & Quotas */}
            <fieldset className="m-0 mb-7 p-0 min-w-0 border-0">
              <legend className="w-full p-0 pb-1.5 mb-5 text-[20px] font-normal text-[#e1e2e3] leading-snug border-b border-[#444444]">
                Reverse Proxy &amp; MCP Server
              </legend>

              <div className="grid grid-cols-1 md:grid-cols-[220px_minmax(0,440px)] items-start gap-x-5 gap-y-1 mb-4">
                <div className="md:text-right md:pt-1 text-[13px] font-bold text-[#e1e2e3] leading-snug">
                  Forwarded Headers
                </div>
                <div>
                  <label className="flex items-center gap-2.5 text-[13px] text-[#eeeeee] cursor-pointer">
                    <input
                      type="checkbox"
                      disabled={isTfhLocked}
                      checked={trustForwardedHeaders}
                      onChange={(e) => {
                        setTrustForwardedHeaders(e.target.checked);
                        setGeneralExtrasDirty(true);
                        setSuccessMessage(null);
                      }}
                    />
                    <span>Trust X-Forwarded-For / X-Forwarded-Proto headers from reverse proxy</span>
                    {isTfhLocked && (
                      <span className="ml-1 px-1.5 py-0.5 rounded text-[10px] font-semibold bg-[#2e3e4e] text-[#69c0ff] border border-[#1b3a57]">
                        Managed by {generalQuery.data?.security?.managedBy?.trustForwardedHeaders}
                      </span>
                    )}
                  </label>
                </div>
              </div>

              <div className="grid grid-cols-1 md:grid-cols-[220px_minmax(0,440px)] items-start gap-x-5 gap-y-1 mb-4">
                <label
                  htmlFor="trusted-proxy-cidrs"
                  className="md:text-right md:pt-1.5 text-[13px] font-bold text-[#e1e2e3] leading-snug"
                >
                  Trusted Proxy CIDRs
                </label>
                <div>
                  <input
                    id="trusted-proxy-cidrs"
                    type="text"
                    disabled={isCidrsLocked}
                    placeholder="10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16"
                    value={trustedProxyCidrsText}
                    onChange={(e) => {
                      setTrustedProxyCidrsText(e.target.value);
                      setGeneralExtrasDirty(true);
                      setSuccessMessage(null);
                    }}
                    className="arr-input font-mono"
                  />
                  <p className="mt-1 text-[12px] text-[#909293] leading-snug">
                    Comma-separated IPs or CIDRs allowed to supply forwarded headers.
                  </p>
                </div>
              </div>

              <div className="grid grid-cols-1 md:grid-cols-[220px_minmax(0,440px)] items-start gap-x-5 gap-y-1 mb-4">
                <label
                  htmlFor="allowed-proxy-hosts"
                  className="md:text-right md:pt-1.5 text-[13px] font-bold text-[#e1e2e3] leading-snug"
                >
                  Allowed Proxy Hosts
                </label>
                <div>
                  <input
                    id="allowed-proxy-hosts"
                    type="text"
                    disabled={isAllowedHostsLocked}
                    placeholder="managerr.example.com, *.example.com"
                    value={allowedHostsText}
                    onChange={(e) => {
                      setAllowedHostsText(e.target.value);
                      setGeneralExtrasDirty(true);
                      setSuccessMessage(null);
                    }}
                    className="arr-input font-mono"
                  />
                  {isAllowedHostsLocked && (
                    <span className="inline-block mt-1 px-1.5 py-0.5 rounded text-[10px] font-semibold bg-[#2e3e4e] text-[#69c0ff] border border-[#1b3a57]">
                      Managed by {generalQuery.data?.security?.managedBy?.allowedHosts}
                    </span>
                  )}
                  <p className="mt-1 text-[12px] text-[#909293] leading-snug">
                    Comma-separated hostnames or wildcard domains (e.g. *.example.com) accepted in X-Forwarded-Host.
                  </p>
                </div>
              </div>

              <div className="grid grid-cols-1 md:grid-cols-[220px_minmax(0,440px)] items-start gap-x-5 gap-y-1 mb-4">
                <div className="md:text-right md:pt-1 text-[13px] font-bold text-[#e1e2e3] leading-snug">
                  MCP Server
                </div>
                <div>
                  <label className="flex items-center gap-2.5 text-[13px] text-[#eeeeee] cursor-pointer">
                    <input
                      type="checkbox"
                      disabled={isMcpLocked}
                      checked={mcpEnabled}
                      onChange={(e) => {
                        setMcpEnabled(e.target.checked);
                        setGeneralExtrasDirty(true);
                        setSuccessMessage(null);
                      }}
                    />
                    <span>Enable Model Context Protocol (MCP) endpoints (/mcp, /api/v1/mcp)</span>
                    {isMcpLocked && (
                      <span className="ml-1 px-1.5 py-0.5 rounded text-[10px] font-semibold bg-[#2e3e4e] text-[#69c0ff] border border-[#1b3a57]">
                        Managed by {generalQuery.data?.mcp?.managedBy}
                      </span>
                    )}
                  </label>
                </div>
              </div>

              <div className="grid grid-cols-1 md:grid-cols-[220px_minmax(0,440px)] items-start gap-x-5 gap-y-1 mb-4">
                <label
                  htmlFor="rolling-quota-window-days"
                  className="md:text-right md:pt-1.5 text-[13px] font-bold text-[#e1e2e3] leading-snug"
                >
                  Rolling Quota Window (Days)
                </label>
                <div>
                  <input
                    id="rolling-quota-window-days"
                    type="number"
                    min={1}
                    max={365}
                    value={rollingQuotaWindowDays}
                    onChange={(e) => {
                      setRollingQuotaWindowDays(Math.max(1, Math.min(365, Number(e.target.value) || 30)));
                      setGeneralExtrasDirty(true);
                      setSuccessMessage(null);
                    }}
                    className="arr-input max-w-[160px]"
                  />
                  <p className="mt-1 text-[12px] text-[#909293] leading-snug">
                    Number of days evaluated for role-based media request quotas (1–365, default 30).
                  </p>
                </div>
              </div>
            </fieldset>
          </form>
        </div>
      </PageContentBody>

      {/* Native <dialog> Reset API Key Confirmation Modal */}
      <dialog
        ref={resetDialogRef}
        aria-labelledby="reset-api-key-title"
        onClose={handleDialogClose}
        onClick={(e) => {
          if (e.target === e.currentTarget) e.currentTarget.close('cancel');
        }}
        className="m-auto p-0 bg-transparent border-0 max-w-md w-[calc(100%-2rem)] text-[#e1e2e3]"
      >
        <div className="bg-arr-modal border border-arr-card-border rounded-[4px] shadow-[0_8px_28px_rgba(0,0,0,0.85)] overflow-hidden">
          <div className="flex items-center justify-between px-4 py-3 border-b border-arr-card-border bg-arr-toolbar">
            <h2 id="reset-api-key-title" className="text-[17px] font-normal text-[#e1e2e3]">
              Reset API Key
            </h2>
            <button
              type="button"
              aria-label="Close"
              onClick={() => resetDialogRef.current?.close('cancel')}
              className="inline-flex items-center justify-center min-w-[28px] min-h-[28px] text-[#909293] hover:text-[#e1e2e3] transition-colors p-1 cursor-pointer"
            >
              <X className="h-4 w-4" aria-hidden="true" />
            </button>
          </div>

          <div className="p-4 text-[13px] text-[#cccccc] leading-relaxed">
            <p>
              Are you sure you want to reset your API Key? Resetting your API Key will require all
              third-party applications (such as Seerr, Prowlarr, or scripts) using this key to be
              updated immediately.
            </p>
          </div>

          <div className="flex items-center justify-end gap-2.5 px-4 py-3 border-t border-arr-card-border bg-arr-toolbar">
            <button
              type="button"
              autoFocus
              onClick={() => resetDialogRef.current?.close('cancel')}
              className="inline-flex items-center justify-center h-[34px] px-3.5 text-[13px] font-medium rounded-[4px] border border-[#4a5158] bg-[#333333] hover:bg-[#444444] text-[#eeeeee] transition-colors cursor-pointer"
            >
              Cancel
            </button>
            <ArrButton
              variant="danger"
              busy={resetApiKeyMutation.isPending}
              onClick={() => resetApiKeyMutation.mutate()}
            >
              Reset API Key
            </ArrButton>
          </div>
        </div>
      </dialog>
    </div>
  );
}
