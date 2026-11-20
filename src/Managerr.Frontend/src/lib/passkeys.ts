import 'webauthn-polyfills';
import { apiFetch, ApiError } from './api';

/**
 * Validates that a string is a valid unpadded Base64URL string per RFC 4648 §5.
 * Required for WebAuthn Signal API parameters (userId, credentialId).
 */
export function isValidBase64Url(str: unknown): str is string {
  if (typeof str !== 'string' || str.length === 0) return false;
  // Unpadded Base64URL cannot have length % 4 === 1, and only contains [A-Za-z0-9_-]
  if (str.length % 4 === 1) return false;
  return /^[A-Za-z0-9_-]+$/.test(str);
}

export function base64UrlToUint8Array(str: string): Uint8Array {
  const base64 = str.replace(/-/g, '+').replace(/_/g, '/');
  const pad = base64.length % 4 === 0 ? '' : '='.repeat(4 - (base64.length % 4));
  const bin = atob(base64 + pad);
  const arr = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) {
    arr[i] = bin.charCodeAt(i);
  }
  return arr;
}

export function uint8ArrayToBase64Url(bytes: Uint8Array): string {
  let bin = '';
  for (let i = 0; i < bytes.byteLength; i++) {
    bin += String.fromCharCode(bytes[i]);
  }
  return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/**
 * Curated AAGUID dictionary for user-friendly authenticator names and badges.
 */
const AAGUID_MAP: Record<string, string> = {
  'ea9b8d66-4d01-1d21-3ce4-b6b48cb575d4': 'Google Password Manager',
  'ad59a589-a841-4b1f-9fa8-b3291242b180': 'Google Chrome',
  'b85627da-4e79-4aad-ad29-28fa76df9320': 'iCloud Keychain',
  'b539762a-8859-4028-a43e-726555531947': '1Password',
  '607a9728-7740-4965-b1ab-217684074815': 'Bitwarden',
  '607b04e6-ee3a-44a6-ac78-1a5202482329': 'Windows Hello',
  '08987058-cadc-4b81-b6e1-30de50dcbe96': 'Windows Hello',
  'cb69481e-8ff7-4039-93ec-0a2729a1e67d': 'YubiKey 5 Series',
  'ee882879-7216-4633-9e45-7561f364024c': 'YubiKey Bio',
  '329ff096-7729-4556-9a2c-f60cf17c1bf2': 'Dashlane',
  '784eb857-4180-4978-9e6b-e854fa164746': 'Proton Pass',
  'dd4ec289-e01d-41c9-bb89-70fa845d4bf2': 'KeePassXC',
};

/**
 * Resolves an AAGUID to a human-readable display name and icon indicator.
 * If AAGUID is all-zeros (masked/anonymous) or unknown, returns safe fallbacks.
 */
export function getAaguidInfo(aaguid?: string | null): { name: string; isMasked: boolean } {
  if (!aaguid || aaguid === '00000000-0000-0000-0000-000000000000') {
    return { name: 'Passkey / Platform Authenticator', isMasked: true };
  }
  const key = aaguid.toLowerCase();
  const known = AAGUID_MAP[key];
  if (known) {
    return { name: known, isMasked: false };
  }
  return { name: 'Security Key / Authenticator', isMasked: false };
}

/**
 * Checks whether WebAuthn is supported by the client browser.
 */
export function isPasskeySupported(): boolean {
  return typeof window !== 'undefined' && typeof window.PublicKeyCredential !== 'undefined';
}

type WebAuthnClientCapabilities = Record<string, boolean | undefined>;

interface PublicKeyCredentialExtended {
  getClientCapabilities?: () => Promise<WebAuthnClientCapabilities>;
  parseRequestOptionsFromJSON?: (options: unknown) => PublicKeyCredentialRequestOptions;
  parseCreationOptionsFromJSON?: (options: unknown) => PublicKeyCredentialCreationOptions;
}

/**
 * Checks whether a platform passkey authenticator is supported via PublicKeyCredential.getClientCapabilities().
 */
export async function isPlatformPasskeySupported(): Promise<boolean> {
  if (!isPasskeySupported()) return false;
  const pkc = PublicKeyCredential as unknown as PublicKeyCredentialExtended;
  if (typeof pkc.getClientCapabilities === 'function') {
    try {
      const capabilities = await pkc.getClientCapabilities();
      return Boolean(capabilities?.passkeyPlatformAuthenticator);
    } catch {
      return false;
    }
  }
  return true;
}

/**
 * Checks whether WebAuthn Conditional UI (autofill mediation) is supported.
 */
export async function isConditionalUiSupported(): Promise<boolean> {
  if (!isPasskeySupported()) return false;
  try {
    const pkc = PublicKeyCredential as unknown as PublicKeyCredentialExtended;
    if (typeof pkc.getClientCapabilities === 'function') {
      const capabilities = await pkc.getClientCapabilities();
      if (typeof capabilities?.conditionalGet === 'boolean') {
        return capabilities.conditionalGet;
      }
    }
    if (typeof PublicKeyCredential.isConditionalMediationAvailable === 'function') {
      return await PublicKeyCredential.isConditionalMediationAvailable();
    }
  } catch (err) {
    console.warn('[Passkeys] Error checking conditional mediation:', err);
  }
  return false;
}

// ==========================================
// WebAuthn Signal API Integrations
// Rule: MUST catch errors and log with console.warn (NEVER console.error).
// Rule: IDs must be valid unpadded Base64URL.
// ==========================================

export function signalAcceptedCredentials(rpId: string, userId: string, credentialIds: string[]): void {
  try {
    if (typeof PublicKeyCredential === 'undefined') return;
    const signalFn = (PublicKeyCredential as unknown as { signalAllAcceptedCredentials?: (arg: unknown) => Promise<void> }).signalAllAcceptedCredentials;
    if (typeof signalFn !== 'function') return;

    if (!rpId || !isValidBase64Url(userId)) {
      console.warn('[Passkeys] signalAllAcceptedCredentials skipped: invalid rpId or non-base64url userId');
      return;
    }

    const validCredentialIds = credentialIds.filter(isValidBase64Url);
    signalFn.call(PublicKeyCredential, {
      rpId,
      userId,
      allAcceptedCredentialIds: validCredentialIds,
    })?.catch((err: unknown) => {
      console.warn('[Passkeys] signalAllAcceptedCredentials rejected:', err);
    });
  } catch (err) {
    console.warn('[Passkeys] signalAllAcceptedCredentials threw:', err);
  }
}

export function signalCurrentUserDetails(rpId: string, userId: string, name: string, displayName?: string): void {
  try {
    if (typeof PublicKeyCredential === 'undefined') return;
    const signalFn = (PublicKeyCredential as unknown as { signalCurrentUserDetails?: (arg: unknown) => Promise<void> }).signalCurrentUserDetails;
    if (typeof signalFn !== 'function') return;

    if (!rpId || !isValidBase64Url(userId)) {
      console.warn('[Passkeys] signalCurrentUserDetails skipped: invalid rpId or non-base64url userId');
      return;
    }

    signalFn.call(PublicKeyCredential, {
      rpId,
      userId,
      name,
      displayName: displayName || name,
    })?.catch((err: unknown) => {
      console.warn('[Passkeys] signalCurrentUserDetails rejected:', err);
    });
  } catch (err) {
    console.warn('[Passkeys] signalCurrentUserDetails threw:', err);
  }
}

export function signalUnknownCredential(rpId: string, credentialId: string): void {
  try {
    if (typeof PublicKeyCredential === 'undefined') return;
    const signalFn = (PublicKeyCredential as unknown as { signalUnknownCredential?: (arg: unknown) => Promise<void> }).signalUnknownCredential;
    if (typeof signalFn !== 'function') return;

    if (!rpId || !isValidBase64Url(credentialId)) {
      console.warn('[Passkeys] signalUnknownCredential skipped: invalid rpId or non-base64url credentialId');
      return;
    }

    signalFn.call(PublicKeyCredential, {
      rpId,
      credentialId,
    })?.catch((err: unknown) => {
      console.warn('[Passkeys] signalUnknownCredential rejected:', err);
    });
  } catch (err) {
    console.warn('[Passkeys] signalUnknownCredential threw:', err);
  }
}

// ==========================================
// WebAuthn Creation & Request Helpers
// ==========================================

interface RawCreationOptions {
  challenge: string | ArrayBuffer;
  rp?: { id?: string; name?: string };
  user?: { id: string | ArrayBuffer; name?: string; displayName?: string };
  excludeCredentials?: Array<{ id: string | ArrayBuffer; type?: PublicKeyCredentialType }>;
  [key: string]: unknown;
}

interface RawRequestOptions {
  challenge: string | ArrayBuffer;
  rpId?: string;
  rp?: { id?: string; name?: string };
  allowCredentials?: Array<{ id: string | ArrayBuffer; type?: PublicKeyCredentialType }>;
  [key: string]: unknown;
}

function transformCreationOptions(raw: RawCreationOptions): PublicKeyCredentialCreationOptions {
  const options = { ...raw } as unknown as PublicKeyCredentialCreationOptions;
  if (typeof raw.challenge === 'string') {
    (options as { challenge: BufferSource }).challenge = base64UrlToUint8Array(raw.challenge);
  }
  if (raw.user && typeof raw.user.id === 'string') {
    (options as { user: { id: BufferSource } }).user = {
      ...(raw.user as { name?: string; displayName?: string }),
      id: base64UrlToUint8Array(raw.user.id),
    };
  }
  if (Array.isArray(raw.excludeCredentials)) {
    (options as { excludeCredentials: Array<{ id: BufferSource }> }).excludeCredentials = raw.excludeCredentials.map((c) => ({
      ...c,
      id: typeof c.id === 'string' ? base64UrlToUint8Array(c.id) : (c.id as BufferSource),
    }));
  }
  return options;
}

function transformRequestOptions(raw: RawRequestOptions): PublicKeyCredentialRequestOptions {
  const options = { ...raw } as unknown as PublicKeyCredentialRequestOptions;
  if (typeof raw.challenge === 'string') {
    (options as { challenge: BufferSource }).challenge = base64UrlToUint8Array(raw.challenge);
  }
  if (Array.isArray(raw.allowCredentials)) {
    (options as { allowCredentials: Array<{ id: BufferSource }> }).allowCredentials = raw.allowCredentials.map((c) => ({
      ...c,
      id: typeof c.id === 'string' ? base64UrlToUint8Array(c.id) : (c.id as BufferSource),
    }));
  }
  return options;
}

export function credentialToJSON(credential: PublicKeyCredential): Record<string, unknown> {
  const c = credential as unknown as { toJSON?: () => Record<string, unknown> };
  if (typeof c.toJSON === 'function') {
    return c.toJSON();
  }

  // Fallback serialization if toJSON polyfill is not active
  const res = credential.response as unknown as Record<string, unknown>;
  const json: Record<string, unknown> = {
    id: credential.id,
    rawId: uint8ArrayToBase64Url(new Uint8Array(credential.rawId)),
    type: credential.type,
    response: {
      clientDataJSON: uint8ArrayToBase64Url(new Uint8Array(res.clientDataJSON as ArrayBuffer)),
    },
  };

  if ('attestationObject' in res && res.attestationObject) {
    (json.response as Record<string, unknown>).attestationObject = uint8ArrayToBase64Url(new Uint8Array(res.attestationObject as ArrayBuffer));
  }
  if ('authenticatorData' in res && res.authenticatorData) {
    (json.response as Record<string, unknown>).authenticatorData = uint8ArrayToBase64Url(new Uint8Array(res.authenticatorData as ArrayBuffer));
  }
  if ('signature' in res && res.signature) {
    (json.response as Record<string, unknown>).signature = uint8ArrayToBase64Url(new Uint8Array(res.signature as ArrayBuffer));
  }
  if ('userHandle' in res && res.userHandle) {
    (json.response as Record<string, unknown>).userHandle = uint8ArrayToBase64Url(new Uint8Array(res.userHandle as ArrayBuffer));
  }

  return json;
}

export interface StartPasskeyLoginOptions {
  optionsUrl?: string;
  verifyUrl?: string;
  conditional?: boolean;
  signal?: AbortSignal;
}

export async function startPasskeyLogin<T = Record<string, unknown>>(opts: StartPasskeyLoginOptions = {}): Promise<T> {
  const optionsUrl = opts.optionsUrl || '/api/v1/auth/passkeys/login/options';
  const verifyUrl = opts.verifyUrl || '/api/v1/auth/passkeys/login/verify';

  // 1. Fetch challenge & options from server (always reads rpId from server)
  const rawOptions = await apiFetch<RawRequestOptions>(optionsUrl, {
    method: 'POST',
    body: JSON.stringify({}),
    signal: opts.signal,
  });

  const rpId = rawOptions.rpId || rawOptions.rp?.id;
  const pkc = PublicKeyCredential as unknown as PublicKeyCredentialExtended;
  const publicKey =
    typeof pkc.parseRequestOptionsFromJSON === 'function'
      ? pkc.parseRequestOptionsFromJSON(rawOptions)
      : transformRequestOptions(rawOptions);

  // 2. Call navigator.credentials.get
  const credential = (await navigator.credentials.get({
    publicKey,
    mediation: opts.conditional ? 'conditional' : 'optional',
    signal: opts.signal,
  })) as PublicKeyCredential | null;

  if (!credential) {
    throw new Error('No passkey credential returned');
  }

  // 3. Verify credential with server
  const credentialPayload = credentialToJSON(credential);
  const encodedId = typeof credentialPayload.id === 'string' ? credentialPayload.id : credential.id;

  try {
    const result = await apiFetch<T>(verifyUrl, {
      method: 'POST',
      body: JSON.stringify(credentialPayload),
      signal: opts.signal,
    });
    return result;
  } catch (err: unknown) {
    // Modern Web Guidance: Signal unknown credential ONLY on HTTP 404
    if (err instanceof ApiError && err.status === 404 && rpId && encodedId) {
      signalUnknownCredential(rpId, encodedId);
    }
    throw err;
  }
}

export interface RegisterPasskeyOptions {
  optionsUrl?: string;
  verifyUrl?: string;
  name: string;
  promotion?: boolean;
}

export async function startPasskeyRegistration<T = Record<string, unknown>>(opts: RegisterPasskeyOptions): Promise<T> {
  const optionsUrl = opts.optionsUrl || '/api/v1/auth/passkeys/register/options';
  const verifyUrl = opts.verifyUrl || '/api/v1/auth/passkeys/register/verify';

  // 1. Fetch creation options from server
  const rawOptions = await apiFetch<RawCreationOptions>(optionsUrl, {
    method: 'POST',
    body: JSON.stringify({ promotion: opts.promotion ?? false }),
  });

  const rpId = rawOptions.rp?.id;
  const pkc = PublicKeyCredential as unknown as PublicKeyCredentialExtended;
  const publicKey =
    typeof pkc.parseCreationOptionsFromJSON === 'function'
      ? pkc.parseCreationOptionsFromJSON(rawOptions)
      : transformCreationOptions(rawOptions);

  // 2. Call navigator.credentials.create
  const credential = (await navigator.credentials.create({
    publicKey,
  })) as PublicKeyCredential | null;

  if (!credential) {
    throw new Error('Failed to create passkey credential');
  }

  // 3. Verify registration (segregated try/catch to signal orphaned credentials on server failure)
  const encoded = credentialToJSON(credential);
  const encodedId = typeof encoded.id === 'string' ? encoded.id : credential.id;
  const credentialPayload = {
    ...encoded,
    name: opts.name,
  };

  try {
    return await apiFetch<T>(verifyUrl, {
      method: 'POST',
      body: JSON.stringify(credentialPayload),
    });
  } catch (err: unknown) {
    if (rpId && encodedId) {
      signalUnknownCredential(rpId, encodedId);
    }
    throw err;
  }
}
