import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { GeneralSettingsView } from '../components/GeneralSettingsView';
import {
  COLOR_SCHEME_PRESETS,
  COLOR_SCHEMES,
  applyColorScheme,
  applyTheme,
  exportThemePayload,
  getStoredColorScheme,
  parseThemeJson,
  serializeThemeJson,
} from '../lib/theme';

function createMockFetch() {
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

    if (path === '/api/v1/settings/general') {
      if (method === 'PUT') {
        const body = JSON.parse(init?.body as string);
        return json({
          instanceName: body.instanceName || 'Managerr Test',
          authenticationMethod: 'Forms / Plex OAuth',
          authenticationRequired: 'Required',
          apiKey: '4f18b3e8c9d24a0fa1b2c3d4e5f6a7b8',
          boundServerName: 'HomePlex',
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
        apiKey: '4f18b3e8c9d24a0fa1b2c3d4e5f6a7b8',
        boundServerName: 'HomePlex',
        compatUrlBases: {
          sonarr: '/compat/sonarr',
          radarr: '/compat/radarr',
          lidarr: '/compat/lidarr',
          readarr: '/compat/readarr',
          prowlarr: '/compat/prowlarr',
        },
      });
    }

    if (path === '/api/v1/settings/auth') {
      return json({
        localAuthEnabled: true,
        passkeysEnabled: true,
        oidcEnabled: false,
      });
    }

    return json({});
  });
}

function renderSettingsView() {
  const fetchMock = createMockFetch();
  vi.stubGlobal('fetch', fetchMock);

  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
    },
  });

  const utils = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <GeneralSettingsView />
      </MemoryRouter>
    </QueryClientProvider>,
  );

  return { ...utils, fetchMock };
}

describe('Frontend Theme Engine & 5 Color Schemes', () => {
  beforeEach(() => {
    localStorage.clear();
    document.documentElement.style.cssText = '';
    delete document.documentElement.dataset.colorScheme;
    delete document.documentElement.dataset.themeVariant;
  });

  it('1. All 5 built-in schemes apply distinct CSS variables and dataset.colorScheme', () => {
    const schemes: Array<keyof typeof COLOR_SCHEMES> = [
      'seafoam_jade',
      'royal_amethyst',
      'cyber_chartreuse',
      'brushed_copper',
      'nordic_periwinkle',
    ];

    const appliedAccents = new Set<string>();
    const appliedHeaders = new Set<string>();

    for (const schemeId of schemes) {
      applyColorScheme(schemeId, undefined, 'dark');

      expect(document.documentElement.dataset.colorScheme).toBe(schemeId);

      const style = document.documentElement.style;
      const expected = COLOR_SCHEMES[schemeId].tokens;

      const accent = style.getPropertyValue('--arr-accent');
      const headerBg = style.getPropertyValue('--arr-header-bg-dark');
      const logoSecondary = style.getPropertyValue('--arr-logo-secondary');
      const primary = style.getPropertyValue('--arr-primary');

      expect(accent).toBe(expected.accent);
      expect(headerBg).toBe(expected.headerBg);
      expect(logoSecondary).toBe(expected.logoSecondary);
      expect(primary).toBe(expected.primary);

      appliedAccents.add(accent);
      appliedHeaders.add(headerBg);
    }

    // Every built-in scheme has a distinct accent and header background
    expect(appliedAccents.size).toBe(5);
    expect(appliedHeaders.size).toBe(5);
  });

  it('2. OLED Black mode pairs with any scheme by removing inline dark surface variables while keeping tinted header & accent', () => {
    applyTheme('oled');
    expect(document.documentElement.dataset.themeVariant).toBe('oled');

    applyColorScheme('cyber_chartreuse', undefined, 'oled');

    const style = document.documentElement.style;
    // Header & accent remain tinted to the Cyber Chartreuse scheme
    expect(style.getPropertyValue('--arr-header-bg-dark')).toBe('#232e11');
    expect(style.getPropertyValue('--arr-accent')).toBe('#a3e635');
    expect(style.getPropertyValue('--arr-primary')).toBe('#4d7c0f');

    // Surface variables are removed so :root[data-theme-variant="oled"] (#000000) takes full effect
    expect(style.getPropertyValue('--arr-page-bg-dark')).toBe('');
    expect(style.getPropertyValue('--arr-sidebar-bg-dark')).toBe('');
    expect(style.getPropertyValue('--arr-card-bg-dark')).toBe('');
  });

  it('3. Exporting theme payload and re-parsing JSON round-trips cleanly', () => {
    const baseTokens = COLOR_SCHEMES.royal_amethyst.tokens;
    const payload = exportThemePayload('Amethyst Dream', 'dark', baseTokens);
    const jsonStr = serializeThemeJson(payload);

    const parseResult = parseThemeJson(jsonStr);
    expect(parseResult.ok).toBe(true);
    if (!parseResult.ok) return;

    expect(parseResult.payload.name).toBe('Amethyst Dream');
    expect(parseResult.payload.baseMode).toBe('dark');
    expect(parseResult.payload.tokens.accent).toBe(baseTokens.accent);
    expect(parseResult.payload.tokens.headerBg).toBe(baseTokens.headerBg);
    expect(parseResult.payload.tokens.primary).toBe(baseTokens.primary);
  });

  it('4. Theme parser strictly validates hex colors and rejects CSS injection payloads', () => {
    // Empty / whitespace
    expect(parseThemeJson('').ok).toBe(false);
    expect(parseThemeJson('   ').ok).toBe(false);

    // Malformed JSON
    expect(parseThemeJson('{ broken: json').ok).toBe(false);

    // Unsupported schema
    expect(
      parseThemeJson(JSON.stringify({ schema: 'managerr-theme/v99', tokens: { accent: '#123456' } })).ok,
    ).toBe(false);

    // CSS / XSS injection attempt
    const injectionPayload = JSON.stringify({
      schema: 'managerr-theme/v1',
      name: 'Hacker Theme',
      tokens: {
        accent: '#123456; background-image: url(https://evil.example.com)',
        headerBg: '#0f332e',
      },
    });
    const injectResult = parseThemeJson(injectionPayload);
    expect(injectResult.ok).toBe(false);
    if (!injectResult.ok) {
      expect(injectResult.error).toContain('Must match #RRGGBB');
    }

    // Payload exceeding size limit (> 16 KB)
    const giantPayload = JSON.stringify({
      schema: 'managerr-theme/v1',
      tokens: { accent: '#123456' },
      garbage: 'a'.repeat(20000),
    });
    expect(parseThemeJson(giantPayload).ok).toBe(false);

    // Valid partial tokens import gracefully via derivation
    const validPartial = JSON.stringify({
      name: 'Partial Sunset',
      tokens: {
        headerBg: '#441122',
        accent: '#ff5577',
      },
    });
    const partialResult = parseThemeJson(validPartial);
    expect(partialResult.ok).toBe(true);
    if (partialResult.ok) {
      expect(partialResult.payload.name).toBe('Partial Sunset');
      expect(partialResult.payload.tokens.headerBg).toBe('#441122');
      expect(partialResult.payload.tokens.accent).toBe('#ff5577');
      // Secondary tokens derived automatically
      expect(partialResult.payload.tokens.cardBg).toBeDefined();
    }
  });
});

describe('Theme Studio UI inside General Settings', () => {
  beforeEach(() => {
    localStorage.clear();
    document.documentElement.style.cssText = '';
    delete document.documentElement.dataset.colorScheme;
    delete document.documentElement.dataset.themeVariant;
  });

  it('1. Renders all 5 preset cards in General Settings with accessible names', async () => {
    renderSettingsView();

    for (const preset of COLOR_SCHEME_PRESETS) {
      const button = await screen.findByRole('button', { name: new RegExp(preset.name, 'i') });
      expect(button).toBeInTheDocument();
    }
  });

  it('2. Clicking a color scheme card activates it immediately and persists in background', async () => {
    const user = userEvent.setup();
    const { fetchMock } = renderSettingsView();

    const amethystBtn = await screen.findByRole('button', { name: /Royal Amethyst/i });
    expect(amethystBtn).toBeInTheDocument();

    await user.click(amethystBtn);

    expect(document.documentElement.dataset.colorScheme).toBe('royal_amethyst');
    expect(document.documentElement.style.getPropertyValue('--arr-accent')).toBe('#c084fc');
    expect(getStoredColorScheme()).toBe('royal_amethyst');

    // Verified background persist to server
    await waitFor(() => {
      const putCalls = fetchMock.mock.calls.filter(
        (c) => typeof c[0] === 'string' && c[0].includes('/settings/general') && c[1]?.method === 'PUT',
      );
      expect(putCalls.length).toBeGreaterThanOrEqual(1);
    });
  });

  it('3. Expanding Customize Colors allows editing core hex and previews in real time', async () => {
    const user = userEvent.setup();
    renderSettingsView();

    const customizeBtn = await screen.findByRole('button', { name: /Customize Colors/i });
    await user.click(customizeBtn);

    // Header bar hex input
    const headerHexInput = await screen.findByLabelText(/Header Bar hex code/i);
    expect(headerHexInput).toBeInTheDocument();

    // Accent hex input
    const accentHexInput = await screen.findByLabelText(/Accent hex code/i);
    expect(accentHexInput).toBeInTheDocument();

    // Type new accent color
    await user.clear(accentHexInput);
    await user.type(accentHexInput, '#e11d48');

    await waitFor(() => {
      expect(document.documentElement.dataset.colorScheme).toBe('custom');
      expect(document.documentElement.style.getPropertyValue('--arr-accent')).toBe('#e11d48');
    });
  });

  it('4. Paste JSON workflow validates input and applies imported theme', async () => {
    const user = userEvent.setup();
    renderSettingsView();

    const pasteBtn = await screen.findByRole('button', { name: /Paste JSON/i });
    await user.click(pasteBtn);

    const textarea = await screen.findByLabelText(/Paste Managerr Theme JSON/i);
    expect(textarea).toBeInTheDocument();

    // Pasting valid theme
    const themeJson = JSON.stringify({
      schema: 'managerr-theme/v1',
      name: 'Midnight Neon',
      tokens: {
        headerBg: '#091e2b',
        accent: '#06b6d4',
        logoSecondary: '#67e8f9',
        primary: '#0284c7',
        sidebarBg: '#0b1620',
        pageBg: '#070f17',
        cardBg: '#0f1d2a',
      },
    });

    await user.click(textarea);
    await user.paste(themeJson);

    const applyBtn = screen.getByRole('button', { name: /Apply Imported Theme/i });
    await user.click(applyBtn);

    await waitFor(() => {
      expect(document.documentElement.dataset.colorScheme).toBe('custom');
      expect(document.documentElement.style.getPropertyValue('--arr-accent')).toBe('#06b6d4');
      expect(document.documentElement.style.getPropertyValue('--arr-header-bg-dark')).toBe('#091e2b');
    });
  });
});
