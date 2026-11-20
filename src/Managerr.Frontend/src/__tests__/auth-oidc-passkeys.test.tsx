import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { LoginView } from '../components/LoginView';
import { PasskeyManagementPanel } from '../components/PasskeyManagementPanel';
import { CommandPaletteModal } from '../components/CommandPaletteModal';
import { applyTheme, getStoredAccent, getStoredTheme } from '../lib/theme';
import {
  isValidBase64Url,
  signalAcceptedCredentials,
  signalCurrentUserDetails,
  startPasskeyLogin,
} from '../lib/passkeys';
import type { PlexUserSummary } from '../lib/api';

const MOCK_USER: PlexUserSummary = {
  id: 1,
  plexUserId: 0,
  plexUuid: 'local-owner-uuid',
  username: 'admin',
  displayName: 'Super Admin',
  email: 'admin@managerr.local',
  avatarUrl: null,
  isServerOwner: true,
  isHomeUser: false,
  isActive: true,
  hasBreakGlassPassword: false,
  sharedPlexSectionIds: [],
  preferredAudioLang: 'de',
  preferredSubLang: 'de',
  discordUserId: null,
  discordUsername: null,
  seerrUserId: null,
  roles: [],
  effectivePermissions: {
    hasAllLibrariesAccess: true,
    sharedPlexSectionIds: [],
    explicitAllowedLibraryIds: [],
    scopes: ['system:admin', 'catalog:read', 'catalog:write'],
    autoApproveRequests: true,
    movieRequestQuota: null,
    tvSeasonRequestQuota: null,
    musicBookRequestQuota: null,
  },
  lastLoginAt: '2026-11-12T12:00:00Z',
  createdAt: '2026-01-01T00:00:00Z',
  authProvider: 'local',
  hasPasskeys: true,
  passkeyCount: 2,
};

describe('Stream 3: Frontend Auth, Passkeys, OIDC & Theme Engine', () => {
  let queryClient: QueryClient;

  beforeEach(() => {
    queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
      },
    });

    // Ensure meta[name="color-scheme"] exists in document head
    let meta = document.querySelector('meta[name="color-scheme"]') as HTMLMetaElement | null;
    if (!meta) {
      meta = document.createElement('meta');
      meta.name = 'color-scheme';
      meta.content = 'light dark';
      document.head.appendChild(meta);
    }
  });

  afterEach(() => {
    queryClient.clear();
    vi.restoreAllMocks();
  });

  describe('LoginView: Setup Wizard, Local Auth & OIDC', () => {
    it('1. Renders Initial Setup Wizard when instance is unclaimed, adhering to modern forms guidance', async () => {
      const mockFetch = vi.fn(async (url: RequestInfo | URL) => {
        const u = url.toString();
        if (u.includes('/api/v1/auth/providers')) {
          return new Response(
            JSON.stringify({
              isClaimed: false,
              instanceName: 'Managerr New',
              ui: { defaultTheme: 'dark', accentColor: '#35c5f4' },
              local: { enabled: true },
              passkeys: { enabled: true, rpId: 'localhost' },
              plex: { enabled: true, isBound: false, boundServerName: null },
              oidc: { enabled: false, displayName: 'OIDC', autoRedirect: false },
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          );
        }
        return new Response(JSON.stringify({ isClaimed: false }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      });
      vi.stubGlobal('fetch', mockFetch);

      const onAuth = vi.fn();
      render(
        <MemoryRouter>
          <QueryClientProvider client={queryClient}>
            <LoginView status={undefined} onAuthenticated={onAuth} />
          </QueryClientProvider>
        </MemoryRouter>,
      );

      // Verify Initial Setup title
      await waitFor(() => {
        expect(screen.getByText(/Initial Setup/i)).toBeInTheDocument();
      });

      // Verify form elements and autocomplete attributes per modern-web-guidance
      const usernameInput = screen.getByLabelText(/Username/i);
      expect(usernameInput).toHaveAttribute('autocomplete', 'username');

      const passwordInput = screen.getByLabelText(/^Password/i);
      expect(passwordInput).toHaveAttribute('type', 'password');
      expect(passwordInput).toHaveAttribute('autocomplete', 'new-password');

      // Test Password Reveal Button
      const toggleBtn = screen.getByRole('button', { name: /Show password/i });
      expect(toggleBtn).toBeInTheDocument();
      expect(toggleBtn).toHaveAttribute('aria-pressed', 'false');

      await userEvent.click(toggleBtn);
      expect(passwordInput).toHaveAttribute('type', 'text');
      expect(toggleBtn).toHaveAttribute('aria-pressed', 'true');
    });

    it('2. Renders Claimed Local Sign-In with autoComplete="username webauthn" and Passkey trigger', async () => {
      const mockFetch = vi.fn(async (url: RequestInfo | URL) => {
        const u = url.toString();
        if (u.includes('/api/v1/auth/providers')) {
          return new Response(
            JSON.stringify({
              isClaimed: true,
              instanceName: 'Managerr Production',
              ui: { defaultTheme: 'dark', accentColor: '#35c5f4' },
              local: { enabled: true },
              passkeys: { enabled: true, rpId: 'localhost' },
              plex: { enabled: false, isBound: false, boundServerName: null },
              oidc: { enabled: true, displayName: 'Authentik SSO', autoRedirect: false },
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          );
        }
        return new Response(JSON.stringify({ isClaimed: true }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      });
      vi.stubGlobal('fetch', mockFetch);

      const onAuth = vi.fn();
      render(
        <MemoryRouter>
          <QueryClientProvider client={queryClient}>
            <LoginView status={undefined} onAuthenticated={onAuth} />
          </QueryClientProvider>
        </MemoryRouter>,
      );

      await waitFor(() => {
        expect(screen.getByRole('heading', { name: 'Sign In' })).toBeInTheDocument();
      });

      // Check username webauthn autocomplete for Conditional UI
      const usernameInput = screen.getByLabelText(/Username or Email/i);
      expect(usernameInput).toHaveAttribute('autocomplete', 'username webauthn');

      // Check OIDC button
      expect(screen.getByText(/Sign in with Authentik SSO/i)).toBeInTheDocument();
    });
  });

  describe('Passkeys & WebAuthn Signal API', () => {
    it('1. Validates unpadded Base64URL string per RFC 4648 §5', () => {
      // Valid Base64URL
      expect(isValidBase64Url('dGVzdA')).toBe(true);
      expect(isValidBase64Url('c29tZS11dWlkXzEyMzQ')).toBe(true);
      expect(isValidBase64Url('abc_123-XYZ')).toBe(true);

      // Invalid: padded with =
      expect(isValidBase64Url('dGVzdA==')).toBe(false);
      // Invalid: illegal characters
      expect(isValidBase64Url('test+plus/slash')).toBe(false);
      // Invalid: length % 4 === 1
      expect(isValidBase64Url('a')).toBe(false);
      expect(isValidBase64Url('')).toBe(false);
      expect(isValidBase64Url(null)).toBe(false);
    });

    it('2. Signal API: Invokes signalAllAcceptedCredentials safely and warns on rejection', () => {
      const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});

      const mockSignalAllAccepted = vi.fn().mockResolvedValue(undefined);
      (window as unknown as { PublicKeyCredential: unknown }).PublicKeyCredential = {
        signalAllAcceptedCredentials: mockSignalAllAccepted,
      };

      // Valid call with unpadded Base64URL userId and credentials
      const rpId = 'localhost';
      const userId = 'dXNlci0xMjM0';
      const credIds = ['Y3JlZC0wMDE', 'Y3JlZC0wMDI'];

      signalAcceptedCredentials(rpId, userId, credIds);
      expect(mockSignalAllAccepted).toHaveBeenCalledWith({
        rpId,
        userId,
        allAcceptedCredentialIds: credIds,
      });

      // Calling with invalid userId skips cleanly without throwing
      signalAcceptedCredentials(rpId, 'invalid=padded=', credIds);
      expect(warnSpy).toHaveBeenCalled();
    });

    it('3. Signal API: Invokes signalUnknownCredential on HTTP 404', async () => {
      const mockSignalUnknown = vi.fn().mockResolvedValue(undefined);
      (window as unknown as { PublicKeyCredential: unknown }).PublicKeyCredential = {
        signalUnknownCredential: mockSignalUnknown,
      };

      const mockCredentialsGet = vi.fn().mockResolvedValue({
        id: 'dW5rbm93bi1jcmVk',
        rawId: new Uint8Array([1, 2, 3]).buffer,
        type: 'public-key',
        response: {
          clientDataJSON: new Uint8Array([1, 2, 3]).buffer,
        },
        toJSON: () => ({ id: 'dW5rbm93bi1jcmVk' }),
      });

      Object.defineProperty(navigator, 'credentials', {
        value: { get: mockCredentialsGet },
        configurable: true,
      });

      const mockFetch = vi.fn(async (url: RequestInfo | URL) => {
        const u = url.toString();
        if (u.includes('/options')) {
          return new Response(
            JSON.stringify({
              rpId: 'localhost',
              challenge: 'Y2hhbGxlbmdl',
              allowCredentials: [],
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          );
        }
        if (u.includes('/verify')) {
          // Server returns 404 for unknown credential
          return new Response(JSON.stringify({ detail: 'Credential not recognized' }), {
            status: 404,
            headers: { 'Content-Type': 'application/json' },
          });
        }
        return new Response('{}', { status: 200 });
      });
      vi.stubGlobal('fetch', mockFetch);

      await expect(startPasskeyLogin()).rejects.toThrow();

      // Verify signalUnknownCredential was called with the credential id
      expect(mockSignalUnknown).toHaveBeenCalledWith({
        rpId: 'localhost',
        credentialId: 'dW5rbm93bi1jcmVk',
      });
    });

    it('4. PasskeyManagementPanel: Renders passkeys with AAGUID lookup and manages credentials', async () => {
      const mockSignalAllAccepted = vi.fn().mockResolvedValue(undefined);
      (window as unknown as { PublicKeyCredential: unknown }).PublicKeyCredential = {
        signalAllAcceptedCredentials: mockSignalAllAccepted,
      };

      const mockFetch = vi.fn(async (url: RequestInfo | URL, init?: RequestInit) => {
        const u = url.toString();
        const method = init?.method?.toUpperCase() ?? 'GET';

        if (u.includes('/api/v1/auth/passkeys')) {
          if (method === 'DELETE') {
            return new Response(null, { status: 204 });
          }
          return new Response(
            JSON.stringify({
              rpId: 'localhost',
              userId: 'dXNlci1pZA',
              credentials: [
                {
                  id: 'Y3JlZC1nb29nbGU',
                  name: 'Pixel 8 Pro',
                  aaguid: 'ea9b8d66-4d01-1d21-3ce4-b6b48cb575d4', // Google Password Manager
                  credentialDeviceType: 'platform',
                  credentialBackedUp: true,
                  registeredAt: Date.now() - 100000,
                  lastUsedAt: Date.now() - 5000,
                },
                {
                  id: 'Y3JlZC15dWJp',
                  name: 'Backup YubiKey',
                  aaguid: 'cb69481e-8ff7-4039-93ec-0a2729a1e67d', // YubiKey 5 Series
                  credentialDeviceType: 'cross-platform',
                  credentialBackedUp: false,
                  registeredAt: Date.now() - 500000,
                  lastUsedAt: null,
                },
              ],
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          );
        }

        if (u.includes('/api/v1/auth/providers')) {
          return new Response(
            JSON.stringify({
              oidc: { enabled: true, displayName: 'Keycloak' },
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          );
        }

        return new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } });
      });
      vi.stubGlobal('fetch', mockFetch);

      render(
        <MemoryRouter>
          <QueryClientProvider client={queryClient}>
            <PasskeyManagementPanel user={MOCK_USER} />
          </QueryClientProvider>
        </MemoryRouter>,
      );

      // Verify passkey list items and human-friendly AAGUID resolution
      await waitFor(() => {
        expect(screen.getByText('Pixel 8 Pro')).toBeInTheDocument();
        expect(screen.getByText('Google Password Manager')).toBeInTheDocument();
        expect(screen.getByText('Backup YubiKey')).toBeInTheDocument();
        expect(screen.getByText('YubiKey 5 Series')).toBeInTheDocument();
      });

      // Verify Signal API was synced with valid IDs
      expect(mockSignalAllAccepted).toHaveBeenCalledWith(
        expect.objectContaining({
          rpId: 'localhost',
          userId: 'dXNlci1pZA',
          allAcceptedCredentialIds: ['Y3JlZC1nb29nbGU', 'Y3JlZC15dWJp'],
        }),
      );
    });

    it('5. Signal API: Invokes signalCurrentUserDetails safely with valid user details', () => {
      const mockSignalUser = vi.fn().mockResolvedValue(undefined);
      (window as unknown as { PublicKeyCredential: unknown }).PublicKeyCredential = {
        signalCurrentUserDetails: mockSignalUser,
      };

      signalCurrentUserDetails('localhost', 'dXNlci1pZA', 'admin', 'Super Admin');
      expect(mockSignalUser).toHaveBeenCalledWith({
        rpId: 'localhost',
        userId: 'dXNlci1pZA',
        name: 'admin',
        displayName: 'Super Admin',
      });
    });
  });

  describe('Multi-Scheme Theme Engine & Accents', () => {
    it('1. Switches theme modes and sets meta[name="color-scheme"] and dataset attributes', () => {
      const meta = document.querySelector('meta[name="color-scheme"]') as HTMLMetaElement;

      // Dark
      applyTheme('dark');
      expect(getStoredTheme()).toBe('dark');
      expect(meta.content).toBe('dark');
      expect(document.documentElement.dataset.themeVariant).toBeUndefined();

      // OLED Pure Black
      applyTheme('oled');
      expect(getStoredTheme()).toBe('oled');
      expect(meta.content).toBe('dark');
      expect(document.documentElement.dataset.themeVariant).toBe('oled');

      // Light
      applyTheme('light');
      expect(getStoredTheme()).toBe('light');
      expect(meta.content).toBe('light');
      expect(document.documentElement.dataset.themeVariant).toBeUndefined();

      // System
      applyTheme('system');
      expect(getStoredTheme()).toBe('system');
      expect(meta.content).toBe('light dark');
      expect(document.documentElement.dataset.themeVariant).toBeUndefined();
    });

    it('2. Switches accent color preset and updates CSS variable --arr-accent', () => {
      applyTheme('dark', '#27c24c');
      expect(getStoredAccent()).toBe('#27c24c');
      expect(document.documentElement.style.getPropertyValue('--arr-accent')).toBe('#27c24c');

      applyTheme('dark', '#ffc230');
      expect(getStoredAccent()).toBe('#ffc230');
      expect(document.documentElement.style.getPropertyValue('--arr-accent')).toBe('#ffc230');
    });
  });

  describe('Command Palette Modal', () => {
    it('1. Opens native <dialog> with no role="dialog" or aria-modal, filters commands, and navigates', async () => {
      const onClose = vi.fn();
      const { container } = render(
        <MemoryRouter>
          <CommandPaletteModal isOpen={true} onClose={onClose} />
        </MemoryRouter>,
      );

      const dialog = container.querySelector('dialog');
      expect(dialog).toBeInTheDocument();
      // MANDATORY per modern-web-guidance dialog: no role="dialog" or aria-modal on native dialog
      expect(dialog).not.toHaveAttribute('role', 'dialog');
      expect(dialog).not.toHaveAttribute('aria-modal');

      const searchInput = screen.getByPlaceholderText(/Type a command/i);
      expect(searchInput).toBeInTheDocument();

      // Filter by "Security"
      await userEvent.type(searchInput, 'Security');

      await waitFor(() => {
        expect(screen.getByText('System: Security & Passkeys')).toBeInTheDocument();
      });

      // Press Enter to select command
      await userEvent.keyboard('{Enter}');
      expect(onClose).toHaveBeenCalled();
    });
  });
});
