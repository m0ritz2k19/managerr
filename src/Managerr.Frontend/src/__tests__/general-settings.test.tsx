import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, useLocation } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { App } from '../App';

const ADMIN_SCOPES = [
  'catalog:read',
  'catalog:write',
  'system:admin',
  'audit:read',
];

const NON_ADMIN_SCOPES = [
  'catalog:read',
];

function LocationProbe() {
  const loc = useLocation();
  return <div data-testid="location-probe">{loc.pathname + loc.search}</div>;
}

const INITIAL_API_KEY = '4f18b3e8c9d24a0fa1b2c3d4e5f6a7b8';
const RESET_API_KEY = '9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d';

function createMockFetch(scopes: string[] = ADMIN_SCOPES) {
  let currentApiKey = INITIAL_API_KEY;

  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
    const url = new URL(rawUrl, 'http://localhost');
    const path = url.pathname;
    const method = init?.method?.toUpperCase() ?? 'GET';

    const json = (data: unknown, status = 200) =>
      new Response(JSON.stringify(data), {
        status,
        headers: { 'Content-Type': 'application/json' },
      });

    if (path === '/api/v1/auth/status') {
      return json({
        isClaimed: true,
        boundServerName: 'HomePlex',
        boundMachineId: 'machine-123',
        plexClientIdentifier: 'client-123',
        authMode: 'forms_or_plex',
      });
    }

    if (path === '/api/v1/auth/me') {
      return json({
        user: {
          id: 1,
          plexUserId: '1001',
          plexUuid: 'uuid-1001',
          username: 'admin',
          email: 'admin@example.com',
          thumbUrl: null,
          accountType: 'owner',
          isHomeUser: false,
          isAdmin: scopes.includes('system:admin'),
          isActive: true,
          roles: ['Owner'],
        },
        csrfToken: 'csrf-test-token',
        scopes,
        isCookieSession: true,
        apiKeyPrefix: null,
      });
    }

    if (path === '/api/v1/settings/auth') {
      if (method === 'PUT') {
        const body = JSON.parse(init?.body as string);
        return json({
          local: { enabled: body.local?.enabled ?? true },
          passkeys: {
            enabled: body.passkeys?.enabled ?? true,
            rpId: body.passkeys?.rpId ?? 'localhost',
            rpName: body.passkeys?.rpName ?? 'Managerr',
            requireUserVerification: body.passkeys?.requireUserVerification ?? false,
          },
          plex: {
            enabled: body.plex?.enabled ?? true,
            boundServerUrl: body.plex?.boundServerUrl ?? null,
          },
          oidc: {
            enabled: false,
            displayName: 'Single Sign-On',
            issuer: '',
            clientId: '',
            hasClientSecret: false,
            scopes: 'openid profile email',
            autoProvision: true,
            autoRedirect: false,
            trustVerifiedEmail: false,
            usernameClaim: 'preferred_username',
            emailClaim: 'email',
            displayNameClaim: 'name',
            groupsClaim: 'groups',
            adminGroups: [],
            managerGroups: [],
            requesterGroups: [],
          },
          managedBy: {
            'oidc.enabled': 'database',
            'oidc.autoRedirect': 'database',
          },
          isOidcManagedByEnv: false,
        });
      }
      return json({
        local: { enabled: true },
        passkeys: {
          enabled: true,
          rpId: 'localhost',
          rpName: 'Managerr',
          requireUserVerification: false,
        },
        plex: {
          enabled: true,
          boundServerUrl: 'https://plex.example.com:32400',
        },
        oidc: {
          enabled: false,
          displayName: 'Keycloak',
          issuer: 'https://auth.example.com',
          clientId: 'managerr',
          hasClientSecret: true,
          scopes: 'openid profile email',
          autoProvision: true,
          autoRedirect: false,
          trustVerifiedEmail: false,
          usernameClaim: 'preferred_username',
          emailClaim: 'email',
          displayNameClaim: 'name',
          groupsClaim: 'groups',
          adminGroups: ['admins'],
          managerGroups: [],
          requesterGroups: [],
        },
        managedBy: {
          'oidc.enabled': 'database',
          'oidc.autoRedirect': 'database',
        },
        isOidcManagedByEnv: false,
      });
    }

    if (path === '/api/v1/settings/general') {
      if (method === 'PUT') {
        const body = JSON.parse(init?.body as string);
        return json({
          instanceName: body.instanceName,
          authenticationMethod: 'Forms / Plex OAuth',
          authenticationRequired: 'Required',
          apiKey: currentApiKey,
          boundServerName: 'HomePlex',
          boundServerUrl: body.boundServerUrl ?? 'https://plex.example.com:32400',
          security: {
            trustForwardedHeaders: body.trustForwardedHeaders ?? false,
            trustedProxyCidrs: body.trustedProxyCidrs ?? [],
            allowedHosts: body.allowedHosts ?? [],
            managedBy: {
              trustForwardedHeaders: 'database',
              trustedProxyCidrs: 'database',
              allowedHosts: 'database',
            },
          },
          mcp: {
            enabled: body.mcpEnabled ?? true,
            managedBy: 'database',
          },
          rollingQuotaWindowDays: body.rollingQuotaWindowDays ?? 30,
          compatUrlBases: {
            sonarr: '/compat/sonarr',
            radarr: '/compat/radarr',
            lidarr: '/compat/lidarr',
            readarr: '/compat/readarr',
            prowlarr: '/compat/prowlarr',
          },
        });
      }

      return json({
        instanceName: 'Managerr Test',
        authenticationMethod: 'Forms / Plex OAuth',
        authenticationRequired: 'Required',
        apiKey: currentApiKey,
        boundServerName: 'HomePlex',
        boundServerUrl: 'https://plex.example.com:32400',
        security: {
          trustForwardedHeaders: false,
          trustedProxyCidrs: ['10.0.0.0/8'],
          allowedHosts: [],
          managedBy: {
            trustForwardedHeaders: 'database',
            trustedProxyCidrs: 'database',
            allowedHosts: 'database',
          },
        },
        mcp: {
          enabled: true,
          managedBy: 'database',
        },
        rollingQuotaWindowDays: 30,
        compatUrlBases: {
          sonarr: '/compat/sonarr',
          radarr: '/compat/radarr',
          lidarr: '/compat/lidarr',
          readarr: '/compat/readarr',
          prowlarr: '/compat/prowlarr',
        },
      });
    }

    if (path === '/api/v1/settings/general/api-key/reset' && method === 'POST') {
      currentApiKey = RESET_API_KEY;
      return json({ apiKey: RESET_API_KEY });
    }

    if (path === '/api/v1/catalog/works') return json([]);
    if (path === '/api/v1/libraries') return json([]);
    if (path === '/api/v1/system/health') return json({ status: 'healthy', version: '0.2.0' });

    return json({});
  });
}

function renderAppAt(initialEntries: string[] = ['/settings/general'], scopes: string[] = ADMIN_SCOPES) {
  const fetchMock = createMockFetch(scopes);
  vi.stubGlobal('fetch', fetchMock);

  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });

  const utils = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={initialEntries}>
        <LocationProbe />
        <App />
      </MemoryRouter>
    </QueryClientProvider>,
  );

  return { ...utils, fetchMock };
}

describe('Settings → General & Security API Key', () => {
  it('1. Navigating Settings → General renders the Security fieldset with legend as first child and API Key read-only input displaying the 32-hex key', async () => {
    renderAppAt(['/settings/general']);

    await waitFor(() => {
      expect(screen.getByTestId('location-probe')).toHaveTextContent('/settings/general');
    });

    const apiKeyInput = await screen.findByLabelText('API Key');
    expect(apiKeyInput).toBeInTheDocument();
    await waitFor(() => {
      expect(apiKeyInput).toHaveValue(INITIAL_API_KEY);
    });
    expect(apiKeyInput).toHaveAttribute('readonly');

    // Verify Security fieldset and legend structure
    const legends = document.querySelectorAll('legend');
    const securityLegend = Array.from(legends).find((leg) => leg.textContent?.trim() === 'Security');
    expect(securityLegend).toBeDefined();

    const fieldset = securityLegend?.parentElement;
    expect(fieldset?.tagName).toBe('FIELDSET');
    expect(fieldset?.firstElementChild).toBe(securityLegend);

    // Also verify Host fieldset has legend as direct first child
    const hostLegend = Array.from(legends).find((leg) => leg.textContent?.trim() === 'Host');
    expect(hostLegend).toBeDefined();
    expect(hostLegend?.parentElement?.tagName).toBe('FIELDSET');
    expect(hostLegend?.parentElement?.firstElementChild).toBe(hostLegend);
  });

  it('2. Clicking Copy API key copies to navigator.clipboard.writeText, announces Copied in the sibling role="status" live region, and keeps the copy button enabled', async () => {
    const user = userEvent.setup();
    const writeTextMock = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      value: {
        writeText: writeTextMock,
      },
      writable: true,
      configurable: true,
    });

    renderAppAt(['/settings/general']);

    await waitFor(() => {
      expect(screen.getByTestId('location-probe')).toHaveTextContent('/settings/general');
    });

    const apiKeyInput = await screen.findByLabelText('API Key');
    await waitFor(() => {
      expect(apiKeyInput).toHaveValue(INITIAL_API_KEY);
    });

    const copyBtn = await screen.findByRole('button', { name: 'Copy API key' });
    expect(copyBtn).toBeInTheDocument();
    expect(copyBtn).not.toBeDisabled();

    // Check sibling role="status"
    const liveRegion = screen.getByRole('status');
    expect(liveRegion).toBeInTheDocument();
    // Live region must be OUTSIDE the button
    expect(copyBtn.contains(liveRegion)).toBe(false);

    await user.click(copyBtn);

    expect(writeTextMock).toHaveBeenCalledWith(INITIAL_API_KEY);
    expect(liveRegion).toHaveTextContent(/Copied/i);
    expect(copyBtn).not.toBeDisabled();
  });

  it('3. Clicking Reset API key opens native <dialog> confirmation modal (without role="dialog" or aria-modal), Cancel closes without reset call, and confirming Reset API Key calls reset and updates displayed key', async () => {
    const user = userEvent.setup();
    const { fetchMock } = renderAppAt(['/settings/general']);

    await waitFor(() => {
      expect(screen.getByTestId('location-probe')).toHaveTextContent('/settings/general');
    });

    const resetTriggerBtn = await screen.findByRole('button', { name: 'Reset API key' });
    expect(resetTriggerBtn).toBeInTheDocument();

    const dialog = document.querySelector('dialog');
    expect(dialog).toBeInTheDocument();
    expect(dialog).not.toHaveAttribute('role');
    expect(dialog).not.toHaveAttribute('aria-modal');
    expect(dialog).not.toHaveAttribute('open');

    // Click trigger to open modal
    await user.click(resetTriggerBtn);
    expect(dialog).toHaveAttribute('open');

    // Safe cancel action
    const cancelBtn = within(dialog!).getByRole('button', { name: 'Cancel' });
    expect(cancelBtn).toBeInTheDocument();

    // Click Cancel -> dialog closes without making POST /api/v1/settings/general/api-key/reset call
    await user.click(cancelBtn);
    expect(dialog).not.toHaveAttribute('open');

    const resetCallCountBefore = fetchMock.mock.calls.filter(
      (call) => typeof call[0] === 'string' && call[0].includes('/api-key/reset'),
    ).length;
    expect(resetCallCountBefore).toBe(0);

    // Open again and confirm Reset
    await user.click(resetTriggerBtn);
    expect(dialog).toHaveAttribute('open');

    const confirmResetBtn = within(dialog!).getByRole('button', { name: 'Reset API Key' });
    await user.click(confirmResetBtn);

    await waitFor(() => {
      expect(dialog).not.toHaveAttribute('open');
    });

    const resetCalls = fetchMock.mock.calls.filter(
      (call) => typeof call[0] === 'string' && call[0].includes('/api-key/reset'),
    );
    expect(resetCalls.length).toBe(1);

    // Check displayed API key input is updated
    const apiKeyInput = screen.getByLabelText('API Key');
    await waitFor(() => {
      expect(apiKeyInput).toHaveValue(RESET_API_KEY);
    });
  });

  it('4. Non-admin users visiting /settings/general are redirected back to /library', async () => {
    renderAppAt(['/settings/general'], NON_ADMIN_SCOPES);

    await waitFor(() => {
      expect(screen.getByTestId('location-probe')).toHaveTextContent('/library');
    });
  });

  it('5. Renders Plex, Passkeys, Reverse Proxy & MCP Server controls and loads settings values', async () => {
    renderAppAt(['/settings/general']);

    const plexUrlInput = await screen.findByLabelText('Plex Server URL Override');
    await waitFor(() => {
      expect(plexUrlInput).toHaveValue('https://plex.example.com:32400');
    });

    expect(await screen.findByLabelText('Relying Party ID (RP ID)')).toHaveValue('localhost');
    expect(await screen.findByLabelText('Relying Party Name')).toHaveValue('Managerr');
    expect(await screen.findByLabelText('Trusted Proxy CIDRs')).toHaveValue('10.0.0.0/8');
    expect(await screen.findByLabelText('Rolling Quota Window (Days)')).toHaveValue(30);
  });
});
