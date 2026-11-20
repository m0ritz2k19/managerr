import { useCallback, useEffect, useRef, useState } from 'react';
import { Eye, EyeOff, Fingerprint, KeyRound, Server, ShieldCheck } from 'lucide-react';
import {
  apiFetch,
  ApiError,
  type AuthProvidersResponse,
  type AuthStatus,
  type LocalClaimRequest,
  type LocalLoginRequest,
  type PlexOwnedServerOption,
  type PlexPinInfo,
  type PlexPollResponse,
  setCsrfToken,
} from '../lib/api';
import {
  isConditionalUiSupported,
  isPasskeySupported,
  startPasskeyLogin,
} from '../lib/passkeys';
import { syncServerThemeIfUnset } from '../lib/theme';
import { ArrAlert, ArrButton, ManagerrLogo } from './arr/ArrPrimitives';

interface LoginViewProps {
  status: AuthStatus | undefined;
  onAuthenticated: () => void;
}

export function LoginView({ status, onAuthenticated }: LoginViewProps) {
  // Providers info
  const [providers, setProviders] = useState<AuthProvidersResponse | null>(null);
  const [providersLoading, setProvidersLoading] = useState(true);

  // Setup mode tab: 'local' (Create Owner) or 'plex' (Claim with Plex)
  const [claimMode, setClaimMode] = useState<'local' | 'plex'>('local');

  // Local Claim Form
  const [claimUsername, setClaimUsername] = useState('');
  const [claimDisplayName, setClaimDisplayName] = useState('');
  const [claimEmail, setClaimEmail] = useState('');
  const [claimPassword, setClaimPassword] = useState('');
  const [showClaimPassword, setShowClaimPassword] = useState(false);
  const [claimBusy, setClaimBusy] = useState(false);

  // Local Sign-In Form
  const [loginIdent, setLoginIdent] = useState('');
  const [loginPassword, setLoginPassword] = useState('');
  const [showLoginPassword, setShowLoginPassword] = useState(false);
  const [loginBusy, setLoginBusy] = useState(false);

  // Passkey State
  const [passkeyBusy, setPasskeyBusy] = useState(false);
  const [passkeySupported, setPasskeySupported] = useState(false);
  const autofillAbortRef = useRef<AbortController | null>(null);
  const [conditionalNonce, setConditionalNonce] = useState(0);

  // Plex State
  const [pin, setPin] = useState<PlexPinInfo | null>(null);
  const [ownedServers, setOwnedServers] = useState<PlexOwnedServerOption[] | null>(null);
  const [polling, setPolling] = useState(false);
  const popupRef = useRef<Window | null>(null);
  const pollInFlightRef = useRef(false);

  // Break-Glass State
  const [showBreakGlass, setShowBreakGlass] = useState(false);
  const [bgIdent, setBgIdent] = useState('');
  const [bgPass, setBgPass] = useState('');
  const [bgBusy, setBgBusy] = useState(false);

  const [error, setError] = useState<string | null>(null);
  const [infoMessage, setInfoMessage] = useState<string | null>(null);

  // 1. Fetch Providers with fallback to /status
  useEffect(() => {
    let mounted = true;
    async function loadProviders() {
      try {
        const res = await apiFetch<AuthProvidersResponse>('/api/v1/auth/providers');
        if (mounted) {
          setProviders(res);
          if (res.ui) {
            syncServerThemeIfUnset({
              defaultTheme: res.ui.defaultTheme,
              accentColor: res.ui.accentColor,
              colorScheme: res.ui.colorScheme,
              customTheme: res.ui.customTheme ?? undefined,
            });
          }
        }
      } catch {
        // Fallback gracefully for older backend / mocks
        try {
          const s = await apiFetch<AuthStatus>('/api/v1/auth/status');
          if (mounted) {
            setProviders({
              isClaimed: Boolean(s.isClaimed),
              instanceName: 'Managerr',
              ui: { defaultTheme: 'dark', accentColor: '#2dd4bf' },
              local: { enabled: true },
              passkeys: { enabled: true, rpId: '' },
              plex: {
                enabled: true,
                isBound: Boolean(s.boundServerName),
                boundServerName: s.boundServerName ?? null,
              },
              oidc: {
                enabled: false,
                displayName: 'OIDC',
                autoRedirect: false,
              },
            });
          }
        } catch {
          // If both fail, use status prop
          if (mounted) {
            setProviders({
              isClaimed: Boolean(status?.isClaimed),
              instanceName: 'Managerr',
              ui: { defaultTheme: 'dark', accentColor: '#2dd4bf' },
              local: { enabled: true },
              passkeys: { enabled: false, rpId: '' },
              plex: {
                enabled: true,
                isBound: Boolean(status?.boundServerName),
                boundServerName: status?.boundServerName ?? null,
              },
              oidc: {
                enabled: false,
                displayName: 'OIDC',
                autoRedirect: false,
              },
            });
          }
        }
      } finally {
        if (mounted) setProvidersLoading(false);
      }
    }
    void loadProviders();
    return () => {
      mounted = false;
    };
  }, [status]);

  // 2. Check Passkey Support
  useEffect(() => {
    setPasskeySupported(isPasskeySupported());
  }, []);

  // 3. Handle OIDC Auto-Redirect if configured
  useEffect(() => {
    if (!providers || providersLoading) return;
    const isClaimed = providers.isClaimed;
    if (!isClaimed) return;

    if (providers.oidc?.enabled && providers.oidc?.autoRedirect) {
      const params = new URLSearchParams(window.location.search);
      const bypassLocal = params.get('local') === '1' || params.get('skip_sso') === '1';
      if (!bypassLocal) {
        window.location.href = '/api/v1/auth/oidc/start';
      } else {
        setInfoMessage('Single Sign-On auto-redirect bypassed via ?local=1 query parameter.');
      }
    }
  }, [providers, providersLoading]);

  // 4. WebAuthn Conditional UI (autofill mediation on username input)
  useEffect(() => {
    if (!providers?.isClaimed || !providers.passkeys?.enabled) return;

    let active = true;

    async function setupConditionalUi() {
      const supported = await isConditionalUiSupported();
      if (!supported || !active) return;

      const ctrl = new AbortController();
      autofillAbortRef.current = ctrl;
      try {
        const res = await startPasskeyLogin<{ authenticated: boolean; csrfToken?: string }>({
          conditional: true,
          signal: ctrl.signal,
        });
        if (!active) return;
        if (res.authenticated) {
          setCsrfToken(res.csrfToken ?? null);
          window.localStorage?.setItem?.('managerr.session.active', '1');
          onAuthenticated();
        }
      } catch (err: unknown) {
        // AbortError / NotAllowedError are normal when cancelled on unmount or explicit passkey click
        if (err instanceof DOMException && (err.name === 'AbortError' || err.name === 'NotAllowedError')) return;
      }
    }

    void setupConditionalUi();

    return () => {
      active = false;
      autofillAbortRef.current?.abort();
      autofillAbortRef.current = null;
    };
  }, [providers, onAuthenticated, conditionalNonce]);

  // Close Plex popup
  const closePopupIfOpen = useCallback(() => {
    try {
      if (popupRef.current && !popupRef.current.closed) {
        popupRef.current.close();
      }
    } catch {
      // Ignore COOP
    }
    popupRef.current = null;
  }, []);

  // Poll Plex PIN
  const pollPinOnce = useCallback(async () => {
    if (!pin || !polling || pollInFlightRef.current) return;
    pollInFlightRef.current = true;
    try {
      const res = await apiFetch<PlexPollResponse>(
        `/api/v1/auth/plex/poll/${pin.pinId}?code=${encodeURIComponent(pin.code)}`,
      );
      if (res.authenticated) {
        setCsrfToken(res.csrfToken ?? null);
        window.localStorage?.setItem?.('managerr.session.active', '1');
        setPolling(false);
        closePopupIfOpen();
        onAuthenticated();
      } else if (res.requiresServerSelection && res.availableOwnedServers) {
        setPolling(false);
        closePopupIfOpen();
        setOwnedServers(res.availableOwnedServers);
      }
    } catch (err) {
      setPolling(false);
      closePopupIfOpen();
      setError(err instanceof ApiError ? err.message : 'Plex authentication failed.');
    } finally {
      pollInFlightRef.current = false;
    }
  }, [pin, polling, closePopupIfOpen, onAuthenticated]);

  useEffect(() => {
    if (!pin || !polling) return;

    const timer = window.setInterval(() => {
      void pollPinOnce();
    }, 1500);

    const onWindowMessage = (ev: MessageEvent) => {
      if (ev.origin === window.location.origin && ev.data?.type === 'managerr-plex-auth-complete') {
        void pollPinOnce();
      }
    };
    window.addEventListener('message', onWindowMessage);

    let bc: BroadcastChannel | null = null;
    if (typeof BroadcastChannel !== 'undefined') {
      bc = new BroadcastChannel('managerr-plex-auth');
      bc.onmessage = (ev) => {
        if (ev.data?.type === 'managerr-plex-auth-complete') {
          void pollPinOnce();
        }
      };
    }

    return () => {
      window.clearInterval(timer);
      window.removeEventListener('message', onWindowMessage);
      bc?.close();
    };
  }, [pin, polling, pollPinOnce]);

  async function startPlexSignIn() {
    setError(null);
    setOwnedServers(null);
    try {
      const callbackUrl = `${window.location.origin}/api/v1/auth/plex/callback`;
      const created = await apiFetch<PlexPinInfo>('/api/v1/auth/plex/pin', {
        method: 'POST',
        body: JSON.stringify({ forwardUrl: callbackUrl }),
      });
      setPin(created);
      setPolling(true);
      const opened = window.open(created.authUrl, 'ManagerrPlexAuth', 'width=820,height=720');
      popupRef.current = opened;
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not initiate Plex PIN flow.');
    }
  }

  async function bindSelectedServer(machineIdentifier: string) {
    if (!pin) return;
    setError(null);
    try {
      const res = await apiFetch<PlexPollResponse>(
        `/api/v1/auth/plex/poll/${pin.pinId}?code=${encodeURIComponent(pin.code)}&machineId=${encodeURIComponent(machineIdentifier)}`,
      );
      if (res.authenticated) {
        setCsrfToken(res.csrfToken ?? null);
        window.localStorage?.setItem?.('managerr.session.active', '1');
        onAuthenticated();
      }
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to bind Plex Media Server.');
    }
  }

  // Handle Local Claim Submission
  async function submitClaim(e: React.FormEvent) {
    e.preventDefault();
    setError(null);

    if (claimPassword.length < 8) {
      setError('Password must be at least 8 characters.');
      return;
    }

    setClaimBusy(true);
    try {
      const payload: LocalClaimRequest = {
        username: claimUsername.trim(),
        displayName: claimDisplayName.trim() || undefined,
        email: claimEmail.trim() || undefined,
        password: claimPassword,
      };

      const res = await apiFetch<{ authenticated: boolean; csrfToken?: string }>(
        '/api/v1/auth/local/claim',
        {
          method: 'POST',
          body: JSON.stringify(payload),
        },
      );

      if (res.authenticated) {
        setCsrfToken(res.csrfToken ?? null);
        window.localStorage?.setItem?.('managerr.session.active', '1');
        onAuthenticated();
      }
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to initialize owner account.');
    } finally {
      setClaimBusy(false);
    }
  }

  // Handle Local Login Submission
  async function submitLocalLogin(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    setLoginBusy(true);
    try {
      const payload: LocalLoginRequest = {
        usernameOrEmail: loginIdent.trim(),
        password: loginPassword,
      };
      const res = await apiFetch<{ authenticated: boolean; csrfToken?: string }>(
        '/api/v1/auth/local/login',
        {
          method: 'POST',
          body: JSON.stringify(payload),
        },
      );
      if (res.authenticated) {
        setCsrfToken(res.csrfToken ?? null);
        window.localStorage?.setItem?.('managerr.session.active', '1');
        onAuthenticated();
      }
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Sign-in failed. Check username and password.');
    } finally {
      setLoginBusy(false);
    }
  }

  // Handle Explicit Passkey Login Button Click
  async function handlePasskeyLogin() {
    setError(null);
    // Abort any pending Conditional Get request to avoid WebAuthn prompt collisions
    autofillAbortRef.current?.abort();
    autofillAbortRef.current = null;
    setPasskeyBusy(true);
    try {
      const res = await startPasskeyLogin<{ authenticated: boolean; csrfToken?: string }>();
      if (res.authenticated) {
        setCsrfToken(res.csrfToken ?? null);
        window.localStorage?.setItem?.('managerr.session.active', '1');
        onAuthenticated();
      }
    } catch (err: unknown) {
      if (err instanceof DOMException && (err.name === 'NotAllowedError' || err.name === 'AbortError')) {
        // User cancelled passkey prompt — re-arm Conditional UI autofill
        setConditionalNonce((n) => n + 1);
        return;
      }
      setError(err instanceof Error ? err.message : 'Passkey authentication failed.');
      setConditionalNonce((n) => n + 1);
    } finally {
      setPasskeyBusy(false);
    }
  }

  // Handle Break-Glass Login
  async function submitBreakGlass(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    setBgBusy(true);
    try {
      const res = await apiFetch<{ csrfToken: string }>('/api/v1/auth/breakglass/login', {
        method: 'POST',
        body: JSON.stringify({ usernameOrEmail: bgIdent, password: bgPass }),
      });
      setCsrfToken(res.csrfToken);
      window.localStorage?.setItem?.('managerr.session.active', '1');
      onAuthenticated();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Break-glass login failed.');
    } finally {
      setBgBusy(false);
    }
  }

  const isClaimed = providers ? providers.isClaimed : Boolean(status?.isClaimed);
  const isFirstRun = !isClaimed;
  const boundName = providers?.plex?.boundServerName ?? status?.boundServerName;

  return (
    <div className="min-h-screen flex flex-col bg-arr-page text-[#e1e2e3]">
      <header
        className="h-[60px] border-b border-arr-header-border px-5 flex items-center justify-between"
        style={{ background: 'linear-gradient(90deg, var(--arr-header-bg) 0%, var(--arr-header-bg-end) 100%)' }}
      >
        <div className="flex items-center gap-2.5">
          <ManagerrLogo size={26} />
          <span className="text-[16px] font-semibold tracking-tight text-[#e1e2e3]">
            {providers?.instanceName || 'Managerr'}
          </span>
        </div>
        {boundName && (
          <span className="inline-flex items-center gap-1.5 text-[12px] text-[#909293]">
            <Server className="h-3.5 w-3.5 text-[#27c24c]" />
            {boundName}
          </span>
        )}
      </header>

      <div className="flex-1 flex items-center justify-center px-4 py-10">
        <div className="w-full max-w-[440px] bg-arr-card border border-arr-card-border rounded-[4px] shadow-[0_0_14px_1px_#111111] overflow-hidden">
          <div className="px-5 py-3.5 bg-arr-toolbar border-b border-arr-card-border flex items-center justify-between">
            <h1 id="signin-heading" className="text-[16px] font-semibold text-[#e1e2e3]">
              {isFirstRun ? 'Welcome to Managerr — Initial Setup' : 'Sign In'}
            </h1>
          </div>

          <div className="p-5 space-y-4">
            {error && <ArrAlert kind="danger">{error}</ArrAlert>}
            {infoMessage && <ArrAlert kind="info">{infoMessage}</ArrAlert>}

            {/* SETUP WIZARD (WHEN UNCLAIMED) */}
            {isFirstRun && (
              <div className="space-y-4">
                <ArrAlert kind="warning">
                  <strong className="block mb-0.5">Setup Required</strong>
                  Configure your primary administrator account or connect an existing Plex Media Server to claim this instance.
                </ArrAlert>

                <div className="grid grid-cols-2 gap-1.5 p-1 bg-[#1f1f1f] border border-[#383838] rounded-[4px]">
                  <button
                    type="button"
                    onClick={() => { setClaimMode('local'); setError(null); }}
                    className={`py-1.5 text-[13px] font-medium rounded-[3px] transition-colors cursor-pointer ${
                      claimMode === 'local'
                        ? 'bg-arr-accent text-arr-accent-fg font-semibold shadow'
                        : 'text-[#909293] hover:text-[#e1e2e3]'
                    }`}
                  >
                    Local Account (Recommended)
                  </button>
                  <button
                    type="button"
                    onClick={() => { setClaimMode('plex'); setError(null); }}
                    className={`py-1.5 text-[13px] font-medium rounded-[3px] transition-colors cursor-pointer ${
                      claimMode === 'plex'
                        ? 'bg-[#e5a00d] text-white shadow'
                        : 'text-[#909293] hover:text-[#e1e2e3]'
                    }`}
                  >
                    Claim with Plex
                  </button>
                </div>

                {claimMode === 'local' && (
                  <form onSubmit={submitClaim} className="space-y-3" method="post" action="/api/v1/auth/local/claim">
                    <div>
                      <label htmlFor="claim-username" className="block text-[12px] font-bold text-[#cccccc] mb-1">
                        Username <span className="text-[#f05050]">*</span>
                      </label>
                      <input
                        id="claim-username"
                        name="username"
                        type="text"
                        autoComplete="username"
                        required
                        autoCorrect="off"
                        autoCapitalize="none"
                        spellCheck={false}
                        value={claimUsername}
                        onChange={(e) => setClaimUsername(e.target.value)}
                        className="arr-input"
                        placeholder="admin"
                      />
                    </div>

                    <div>
                      <label htmlFor="claim-display-name" className="block text-[12px] font-bold text-[#cccccc] mb-1">
                        Display Name
                      </label>
                      <input
                        id="claim-display-name"
                        name="displayName"
                        type="text"
                        autoComplete="name"
                        value={claimDisplayName}
                        onChange={(e) => setClaimDisplayName(e.target.value)}
                        className="arr-input"
                        placeholder="Administrator"
                      />
                    </div>

                    <div>
                      <label htmlFor="claim-email" className="block text-[12px] font-bold text-[#cccccc] mb-1">
                        Email Address
                      </label>
                      <input
                        id="claim-email"
                        name="email"
                        type="email"
                        autoComplete="email"
                        autoCorrect="off"
                        autoCapitalize="none"
                        spellCheck={false}
                        value={claimEmail}
                        onChange={(e) => setClaimEmail(e.target.value)}
                        className="arr-input"
                        placeholder="admin@example.local"
                      />
                    </div>

                    <div>
                      <div className="flex items-center justify-between mb-1">
                        <label htmlFor="claim-password" className="text-[12px] font-bold text-[#cccccc]">
                          Password <span className="text-[#f05050]">*</span>
                        </label>
                        <button
                          type="button"
                          onClick={() => setShowClaimPassword((v) => !v)}
                          aria-label={showClaimPassword ? 'Hide password' : 'Show password'}
                          aria-pressed={showClaimPassword}
                          className="inline-flex items-center gap-1 text-[11px] text-[#909293] hover:text-arr-accent cursor-pointer"
                        >
                          {showClaimPassword ? <EyeOff className="h-3 w-3" /> : <Eye className="h-3 w-3" />}
                          {showClaimPassword ? 'Hide' : 'Show'}
                        </button>
                      </div>
                      <input
                        id="claim-password"
                        name="password"
                        type={showClaimPassword ? 'text' : 'password'}
                        autoComplete="new-password"
                        required
                        minLength={8}
                        value={claimPassword}
                        onChange={(e) => setClaimPassword(e.target.value)}
                        className="arr-input"
                      />
                    </div>

                    <div className="pt-2">
                      <ArrButton type="submit" variant="primary" busy={claimBusy} className="w-full h-[38px] text-[14px]">
                        Create Owner Account & Claim
                      </ArrButton>
                    </div>
                  </form>
                )}

                {claimMode === 'plex' && (
                  <div className="space-y-3">
                    {ownedServers ? (
                      <div className="space-y-2">
                        <div className="text-[13px] font-bold text-[#e1e2e3]">Select Plex Media Server:</div>
                        {ownedServers.map((srv) => (
                          <button
                            key={srv.machineIdentifier}
                            type="button"
                            onClick={() => bindSelectedServer(srv.machineIdentifier)}
                            className="w-full text-left px-3.5 py-2.5 bg-arr-sidebar hover:bg-arr-card border border-arr-card-border rounded-[4px] transition-colors cursor-pointer"
                          >
                            <div className="text-[14px] font-medium text-[#eeeeee]">{srv.name}</div>
                            <div className="text-[11px] font-mono text-[#909293]">{srv.machineIdentifier}</div>
                          </button>
                        ))}
                      </div>
                    ) : (
                      <ArrButton
                        variant="plex"
                        busy={polling}
                        onClick={startPlexSignIn}
                        className="w-full h-[38px] text-[14px]"
                      >
                        {polling ? `Waiting for Plex (${pin?.code})...` : 'Sign in with Plex to Claim'}
                      </ArrButton>
                    )}
                  </div>
                )}
              </div>
            )}

            {/* NORMAL SIGN-IN (WHEN CLAIMED) */}
            {!isFirstRun && (
              <div className="space-y-4">
                {/* Local Sign-in Form */}
                {providers?.local?.enabled !== false && (
                  <form
                    onSubmit={submitLocalLogin}
                    method="post"
                    action="/api/v1/auth/local/login"
                    className="space-y-3"
                  >
                    <div>
                      <label htmlFor="signin-username" className="block text-[12px] font-bold text-[#cccccc] mb-1">
                        Username or Email
                      </label>
                      <input
                        id="signin-username"
                        name="username"
                        type="text"
                        autoFocus
                        aria-describedby="signin-heading"
                        autoComplete="username webauthn"
                        required
                        autoCorrect="off"
                        autoCapitalize="none"
                        spellCheck={false}
                        value={loginIdent}
                        onChange={(e) => setLoginIdent(e.target.value)}
                        className="arr-input"
                        placeholder="username or email"
                      />
                    </div>

                    <div>
                      <div className="flex items-center justify-between mb-1">
                        <label htmlFor="signin-password" className="text-[12px] font-bold text-[#cccccc]">
                          Password
                        </label>
                        <button
                          type="button"
                          onClick={() => setShowLoginPassword((v) => !v)}
                          aria-label={showLoginPassword ? 'Hide password' : 'Show password'}
                          aria-pressed={showLoginPassword}
                          className="inline-flex items-center gap-1 text-[11px] text-[#909293] hover:text-arr-accent cursor-pointer"
                        >
                          {showLoginPassword ? <EyeOff className="h-3 w-3" /> : <Eye className="h-3 w-3" />}
                          {showLoginPassword ? 'Hide' : 'Show'}
                        </button>
                      </div>
                      <input
                        id="signin-password"
                        name="password"
                        type={showLoginPassword ? 'text' : 'password'}
                        autoComplete="current-password"
                        required
                        value={loginPassword}
                        onChange={(e) => setLoginPassword(e.target.value)}
                        className="arr-input"
                      />
                    </div>

                    <div className="pt-1">
                      <ArrButton
                        type="submit"
                        variant="primary"
                        busy={loginBusy}
                        className="w-full h-[38px] text-[14px]"
                      >
                        Sign In
                      </ArrButton>
                    </div>
                  </form>
                )}

                {/* Additional Authentication Providers Divider */}
                {(passkeySupported || providers?.oidc?.enabled || providers?.plex?.enabled) && (
                  <div className="relative my-4 flex items-center justify-center">
                    <div className="absolute inset-0 flex items-center">
                      <div className="w-full border-t border-[#383838]" />
                    </div>
                    <div className="relative bg-arr-card px-3 text-[11px] text-[#909293] uppercase tracking-wider font-semibold">
                      Or sign in with
                    </div>
                  </div>
                )}

                <div className="space-y-2">
                  {/* Passkey Button */}
                  {passkeySupported && providers?.passkeys?.enabled !== false && (
                    <ArrButton
                      type="button"
                      variant="default"
                      busy={passkeyBusy}
                      onClick={handlePasskeyLogin}
                      className="w-full h-[38px] text-[14px] flex items-center justify-center gap-2 border border-arr-card-border hover:border-arr-accent"
                    >
                      <Fingerprint className="h-4 w-4 text-arr-accent" />
                      Sign in with Passkey
                    </ArrButton>
                  )}

                  {/* OIDC SSO Button */}
                  {providers?.oidc?.enabled && (
                    <a
                      href="/api/v1/auth/oidc/start"
                      className="w-full h-[38px] px-3.5 bg-arr-sidebar hover:bg-arr-card border border-arr-card-border rounded-[4px] text-[14px] font-medium text-[#eeeeee] flex items-center justify-center gap-2 transition-colors cursor-pointer"
                    >
                      <ShieldCheck className="h-4 w-4 text-[#27c24c]" />
                      Sign in with {providers.oidc.displayName || 'SSO'}
                    </a>
                  )}

                  {/* Plex Button */}
                  {providers?.plex?.enabled !== false && (
                    <>
                      {ownedServers ? (
                        <div className="space-y-2 mt-2">
                          <div className="text-[13px] font-bold text-[#e1e2e3]">Select Plex Media Server:</div>
                          {ownedServers.map((srv) => (
                            <button
                              key={srv.machineIdentifier}
                              type="button"
                              onClick={() => bindSelectedServer(srv.machineIdentifier)}
                              className="w-full text-left px-3.5 py-2.5 bg-arr-sidebar hover:bg-arr-card border border-arr-card-border rounded-[4px] transition-colors cursor-pointer"
                            >
                              <div className="text-[14px] font-medium text-[#eeeeee]">{srv.name}</div>
                              <div className="text-[11px] font-mono text-[#909293]">{srv.machineIdentifier}</div>
                            </button>
                          ))}
                        </div>
                      ) : (
                        <ArrButton
                          variant="plex"
                          busy={polling}
                          onClick={startPlexSignIn}
                          className="w-full h-[38px] text-[14px]"
                        >
                          {polling ? `Waiting for Plex (${pin?.code})...` : 'Sign in with Plex'}
                        </ArrButton>
                      )}
                    </>
                  )}
                </div>

                {/* Local Break-Glass Login (Accordion) */}
                <div className="pt-3 border-t border-[#383838]">
                  <button
                    type="button"
                    onClick={() => setShowBreakGlass((v) => !v)}
                    className="inline-flex items-center gap-1.5 text-[12px] text-[#909293] hover:text-arr-accent transition-colors cursor-pointer"
                  >
                    <KeyRound className="h-3.5 w-3.5" />
                    {showBreakGlass ? 'Hide Break-Glass Login' : 'Local Break-Glass Login'}
                  </button>

                  {showBreakGlass && (
                    <form onSubmit={submitBreakGlass} className="mt-3 space-y-2.5">
                      <div>
                        <label htmlFor="breakglass-ident" className="block text-[12px] font-bold text-[#cccccc] mb-1">
                          Username or Email
                        </label>
                        <input
                          id="breakglass-ident"
                          name="username"
                          type="text"
                          autoComplete="username"
                          autoCorrect="off"
                          autoCapitalize="none"
                          spellCheck={false}
                          required
                          value={bgIdent}
                          onChange={(e) => setBgIdent(e.target.value)}
                          className="arr-input"
                        />
                      </div>
                      <div>
                        <label htmlFor="breakglass-password" className="block text-[12px] font-bold text-[#cccccc] mb-1">
                          Break-Glass Password
                        </label>
                        <input
                          id="breakglass-password"
                          type="password"
                          autoComplete="current-password"
                          required
                          value={bgPass}
                          onChange={(e) => setBgPass(e.target.value)}
                          className="arr-input"
                        />
                      </div>
                      <div className="pt-1 flex justify-end">
                        <ArrButton type="submit" variant="primary" busy={bgBusy} className="w-full">
                          Sign In via Break-Glass
                        </ArrButton>
                      </div>
                    </form>
                  )}
                </div>
              </div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
