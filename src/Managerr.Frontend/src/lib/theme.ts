export type ThemeMode = 'system' | 'dark' | 'oled' | 'light';

export type ColorSchemeId =
  | 'seafoam_jade'
  | 'royal_amethyst'
  | 'cyber_chartreuse'
  | 'brushed_copper'
  | 'nordic_periwinkle'
  | 'custom';

export interface ThemeTokens {
  headerBg: string;
  headerBgEnd: string;
  headerBorder: string;
  sidebarBg: string;
  sidebarBorder: string;
  sidebarActiveBg: string;
  headingBg: string;
  toolbarBg: string;
  filterBarBg: string;
  pageBg: string;
  cardBg: string;
  cardBorder: string;
  modalBg: string;
  inputBg: string;
  accent: string;
  logoSecondary: string;
  primary: string;
  primaryHover: string;
  primaryBorder: string;
}

export type CoreThemeTokens = Pick<
  ThemeTokens,
  'headerBg' | 'accent' | 'logoSecondary' | 'primary' | 'sidebarBg' | 'pageBg' | 'cardBg'
>;

export interface ColorSchemeDefinition {
  id: ColorSchemeId;
  name: string;
  subtitle: string;
  tokens: ThemeTokens;
}

export interface ManagerrThemeExportV1 {
  schema: 'managerr-theme/v1';
  name: string;
  baseMode?: ThemeMode;
  tokens: ThemeTokens;
}

export interface AccentColorPreset {
  id: string;
  name: string;
  value: string;
}

export const ACCENT_PRESETS: AccentColorPreset[] = [
  { id: 'verdigris', name: 'Verdigris Teal', value: '#2dd4bf' },
  { id: 'cyan', name: 'Cyan (Classic Servarr)', value: '#35c5f4' },
  { id: 'blue', name: 'Sky Blue', value: '#5d9cec' },
  { id: 'gold', name: 'Plex Gold', value: '#ffc230' },
  { id: 'emerald', name: 'Emerald Green', value: '#27c24c' },
  { id: 'purple', name: 'Amethyst Purple', value: '#c084fc' },
  { id: 'lime', name: 'Cyber Lime', value: '#a3e635' },
  { id: 'copper', name: 'Brushed Copper', value: '#f69373' },
  { id: 'sapphire', name: 'Nordic Sapphire', value: '#93a8fe' },
  { id: 'rose', name: 'Crimson Rose', value: '#f05050' },
];

export const COLOR_SCHEMES: Record<Exclude<ColorSchemeId, 'custom'>, ColorSchemeDefinition> = {
  seafoam_jade: {
    id: 'seafoam_jade',
    name: 'Verdigris',
    subtitle: 'Seafoam Jade',
    tokens: {
      headerBg: '#0f332e',
      headerBgEnd: '#15443e',
      headerBorder: '#246b62',
      sidebarBg: '#1b2222',
      sidebarBorder: '#101515',
      sidebarActiveBg: '#243130',
      headingBg: '#1a2121',
      toolbarBg: '#1e2626',
      filterBarBg: '#171e1e',
      pageBg: '#141a1a',
      cardBg: '#1f2828',
      cardBorder: '#334242',
      modalBg: '#1d2626',
      inputBg: '#222c2c',
      accent: '#2dd4bf',
      logoSecondary: '#99f6e4',
      primary: '#0f766e',
      primaryHover: '#115e59',
      primaryBorder: '#14b8a6',
    },
  },
  royal_amethyst: {
    id: 'royal_amethyst',
    name: 'Royal Amethyst',
    subtitle: 'Ultraviolet',
    tokens: {
      headerBg: '#291742',
      headerBgEnd: '#382059',
      headerBorder: '#573487',
      sidebarBg: '#1d1925',
      sidebarBorder: '#110e17',
      sidebarActiveBg: '#292238',
      headingBg: '#1b1723',
      toolbarBg: '#201b2a',
      filterBarBg: '#181420',
      pageBg: '#15121c',
      cardBg: '#231e2e',
      cardBorder: '#3a324d',
      modalBg: '#211c2b',
      inputBg: '#272133',
      accent: '#c084fc',
      logoSecondary: '#e9d5ff',
      primary: '#7e22ce',
      primaryHover: '#6b21a8',
      primaryBorder: '#a855f7',
    },
  },
  cyber_chartreuse: {
    id: 'cyber_chartreuse',
    name: 'Cyber Chartreuse',
    subtitle: 'Acid Olive',
    tokens: {
      headerBg: '#232e11',
      headerBgEnd: '#2f3e17',
      headerBorder: '#4c6326',
      sidebarBg: '#1c2018',
      sidebarBorder: '#10130e',
      sidebarActiveBg: '#262d20',
      headingBg: '#1a1e16',
      toolbarBg: '#1f241a',
      filterBarBg: '#171b14',
      pageBg: '#141711',
      cardBg: '#22271d',
      cardBorder: '#37402f',
      modalBg: '#20251b',
      inputBg: '#252b20',
      accent: '#a3e635',
      logoSecondary: '#d9f99d',
      primary: '#4d7c0f',
      primaryHover: '#3f6212',
      primaryBorder: '#65a30d',
    },
  },
  brushed_copper: {
    id: 'brushed_copper',
    name: 'Brushed Copper',
    subtitle: 'Terracotta',
    tokens: {
      headerBg: '#3b1f18',
      headerBgEnd: '#4d2920',
      headerBorder: '#733f32',
      sidebarBg: '#221c1a',
      sidebarBorder: '#14100f',
      sidebarActiveBg: '#302522',
      headingBg: '#1f1918',
      toolbarBg: '#251e1c',
      filterBarBg: '#1c1615',
      pageBg: '#181312',
      cardBg: '#28211f',
      cardBorder: '#433633',
      modalBg: '#261f1d',
      inputBg: '#2d2422',
      accent: '#f69373',
      logoSecondary: '#fed7aa',
      primary: '#b84a27',
      primaryHover: '#9a3d1f',
      primaryBorder: '#e06d44',
    },
  },
  nordic_periwinkle: {
    id: 'nordic_periwinkle',
    name: 'Nordic Sapphire',
    subtitle: 'Periwinkle Slate',
    tokens: {
      headerBg: '#172340',
      headerBgEnd: '#1f3057',
      headerBorder: '#344f8c',
      sidebarBg: '#1a1e29',
      sidebarBorder: '#0f1219',
      sidebarActiveBg: '#242a3b',
      headingBg: '#181c26',
      toolbarBg: '#1d222e',
      filterBarBg: '#151923',
      pageBg: '#13161f',
      cardBg: '#202534',
      cardBorder: '#343d54',
      modalBg: '#1e2331',
      inputBg: '#242a3b',
      accent: '#93a8fe',
      logoSecondary: '#c7d2fe',
      primary: '#4338ca',
      primaryHover: '#3730a3',
      primaryBorder: '#6366f1',
    },
  },
};

export const COLOR_SCHEME_PRESETS: ColorSchemeDefinition[] = [
  COLOR_SCHEMES.seafoam_jade,
  COLOR_SCHEMES.royal_amethyst,
  COLOR_SCHEMES.cyber_chartreuse,
  COLOR_SCHEMES.brushed_copper,
  COLOR_SCHEMES.nordic_periwinkle,
];

export const DEFAULT_COLOR_SCHEME_ID: ColorSchemeId = 'seafoam_jade';

export function mixHex(hexA: string, hexB: string, ratio: number): string {
  const parse = (h: string) => {
    const clean = h.replace('#', '');
    const full = clean.length === 3 ? clean.split('').map((c) => c + c).join('') : clean;
    const num = parseInt(full, 16);
    return [(num >> 16) & 255, (num >> 8) & 255, num & 255];
  };
  const [rA, gA, bA] = parse(hexA);
  const [rB, gB, bB] = parse(hexB);
  const clamp = (v: number) => Math.max(0, Math.min(255, Math.round(v)));
  const r = clamp(rA * (1 - ratio) + rB * ratio);
  const g = clamp(gA * (1 - ratio) + gB * ratio);
  const b = clamp(bA * (1 - ratio) + bB * ratio);
  return `#${r.toString(16).padStart(2, '0')}${g.toString(16).padStart(2, '0')}${b.toString(16).padStart(2, '0')}`;
}

export function computeContrastingForeground(hex: string): string {
  const clean = hex.replace('#', '');
  const full = clean.length === 3 ? clean.split('').map((c) => c + c).join('') : clean;
  const r = parseInt(full.slice(0, 2), 16) / 255;
  const g = parseInt(full.slice(2, 4), 16) / 255;
  const b = parseInt(full.slice(4, 6), 16) / 255;
  const a = [r, g, b].map((v) => (v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4)));
  const lum = 0.2126 * a[0] + 0.7152 * a[1] + 0.0722 * a[2];
  return lum > 0.42 ? '#061816' : '#ffffff';
}

export function deriveTokensFromCore(
  core: Partial<ThemeTokens>,
  baseTokens: ThemeTokens = COLOR_SCHEMES.seafoam_jade.tokens,
): ThemeTokens {
  const headerBg = core.headerBg || baseTokens.headerBg;
  const accent = core.accent || baseTokens.accent;
  const logoSecondary = core.logoSecondary || mixHex(accent, '#ffffff', 0.45);
  const primary = core.primary || baseTokens.primary;
  const sidebarBg = core.sidebarBg || baseTokens.sidebarBg;
  const pageBg = core.pageBg || baseTokens.pageBg;
  const cardBg = core.cardBg || baseTokens.cardBg;

  const headerBgEnd = core.headerBgEnd || mixHex(headerBg, '#ffffff', 0.08);
  const headerBorder = core.headerBorder || mixHex(headerBg, accent, 0.28);
  const sidebarBorder = core.sidebarBorder || mixHex(sidebarBg, '#000000', 0.45);
  const sidebarActiveBg = core.sidebarActiveBg || mixHex(sidebarBg, accent, 0.09);
  const headingBg = core.headingBg || mixHex(pageBg, '#ffffff', 0.04);
  const toolbarBg = core.toolbarBg || mixHex(pageBg, '#ffffff', 0.07);
  const filterBarBg = core.filterBarBg || mixHex(pageBg, '#ffffff', 0.02);
  const cardBorder = core.cardBorder || mixHex(cardBg, '#ffffff', 0.15);
  const modalBg = core.modalBg || mixHex(cardBg, '#000000', 0.08);
  const inputBg = core.inputBg || mixHex(cardBg, '#ffffff', 0.03);
  const primaryHover = core.primaryHover || mixHex(primary, '#000000', 0.15);
  const primaryBorder = core.primaryBorder || mixHex(primary, accent, 0.35);

  return {
    headerBg,
    headerBgEnd,
    headerBorder,
    sidebarBg,
    sidebarBorder,
    sidebarActiveBg,
    headingBg,
    toolbarBg,
    filterBarBg,
    pageBg,
    cardBg,
    cardBorder,
    modalBg,
    inputBg,
    accent,
    logoSecondary,
    primary,
    primaryHover,
    primaryBorder,
  };
}

export function getStoredTheme(): ThemeMode {
  if (typeof window === 'undefined') return 'system';
  try {
    const variant = localStorage.getItem('managerr-theme-variant');
    if (variant === 'oled') return 'oled';
    const cs = localStorage.getItem('color-scheme');
    if (cs === 'dark') return 'dark';
    if (cs === 'light') return 'light';
  } catch {
    // Ignore storage errors in restricted contexts
  }
  return 'system';
}

const HEX_COLOR_REGEX = /^#[0-9a-fA-F]{6}$/;

export const KNOWN_THEME_TOKEN_KEYS: ReadonlyArray<keyof ThemeTokens> = [
  'headerBg',
  'headerBgEnd',
  'headerBorder',
  'sidebarBg',
  'sidebarBorder',
  'sidebarActiveBg',
  'headingBg',
  'toolbarBg',
  'filterBarBg',
  'pageBg',
  'cardBg',
  'cardBorder',
  'modalBg',
  'inputBg',
  'accent',
  'logoSecondary',
  'primary',
  'primaryHover',
  'primaryBorder',
];

export function isValidHexColor(value: unknown): value is string {
  return typeof value === 'string' && HEX_COLOR_REGEX.test(value);
}

export function isPresetColorScheme(id: unknown): id is Exclude<ColorSchemeId, 'custom'> {
  return typeof id === 'string' && Object.prototype.hasOwnProperty.call(COLOR_SCHEMES, id);
}

export function sanitizeThemeName(raw: unknown, fallback = 'Custom Theme'): string {
  if (typeof raw !== 'string') return fallback;
  let cleaned = '';
  for (let i = 0; i < raw.length; i++) {
    const code = raw.charCodeAt(i);
    const ch = raw[i];
    if (code >= 0x20 && code !== 0x7f && ch !== '<' && ch !== '>') {
      cleaned += ch;
    }
  }
  const trimmed = cleaned.trim().slice(0, 64);
  return trimmed || fallback;
}

export function sanitizeThemeTokens(
  candidate: Partial<Record<keyof ThemeTokens, unknown>> | null | undefined,
  fallback: ThemeTokens = COLOR_SCHEMES.seafoam_jade.tokens,
): ThemeTokens {
  const out = { ...fallback };
  if (!candidate || typeof candidate !== 'object') return out;
  for (const key of KNOWN_THEME_TOKEN_KEYS) {
    if (Object.prototype.hasOwnProperty.call(candidate, key)) {
      const val = candidate[key];
      if (isValidHexColor(val)) {
        out[key] = val.toLowerCase();
      }
    }
  }
  return out;
}

export function getStoredAccent(): string {
  if (typeof window === 'undefined') return '#2dd4bf';
  try {
    const raw = localStorage.getItem('managerr-accent');
    return isValidHexColor(raw) ? raw : '#2dd4bf';
  } catch {
    return '#2dd4bf';
  }
}

export function getStoredColorScheme(): ColorSchemeId {
  if (typeof window === 'undefined') return DEFAULT_COLOR_SCHEME_ID;
  try {
    const cs = localStorage.getItem('managerr-color-scheme');
    if (cs === 'custom' || isPresetColorScheme(cs)) {
      return cs;
    }
  } catch {
    // Ignore storage access errors in restricted contexts
  }
  return DEFAULT_COLOR_SCHEME_ID;
}

export function getStoredCustomTheme(): ManagerrThemeExportV1 | null {
  if (typeof window === 'undefined') return null;
  try {
    const raw = localStorage.getItem('managerr-custom-theme');
    if (!raw) return null;
    const res = parseThemeJson(raw);
    if (res.ok) return res.payload;
  } catch {
    // Ignore storage access errors in restricted contexts
  }
  return null;
}

export function isSystemDark(): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return true;
  return window.matchMedia('(prefers-color-scheme: dark)').matches;
}

export function subscribeSystemThemeChange(onChange: (isDark: boolean) => void): () => void {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return () => {};
  const mql = window.matchMedia('(prefers-color-scheme: dark)');
  const handler = (ev: MediaQueryListEvent) => onChange(ev.matches);
  mql.addEventListener('change', handler);
  return () => mql.removeEventListener('change', handler);
}

function updateFavicon(accent: string, logoSecondary: string): void {
  if (typeof document === 'undefined') return;
  const link = document.querySelector<HTMLLinkElement>('link[rel="icon"]');
  if (!link) return;
  const safeAccent = isValidHexColor(accent) ? accent : '#2dd4bf';
  const safeSecondary = isValidHexColor(logoSecondary) ? logoSecondary : '#99f6e4';
  const svg = `<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'><rect x='1' y='1' width='30' height='30' rx='5' fill='%231b1b1b' stroke='%23393f45' stroke-width='1.5'/><circle cx='16' cy='16' r='10.5' fill='none' stroke='${encodeURIComponent(safeAccent)}' stroke-width='2.2' stroke-dasharray='46 18'/><circle cx='16' cy='16' r='6.5' fill='none' stroke='${encodeURIComponent(safeSecondary)}' stroke-width='2' stroke-dasharray='28 12'/><polygon points='14,12.2 20.5,16 14,19.8' fill='${encodeURIComponent(safeAccent)}'/></svg>`;
  link.href = `data:image/svg+xml,${svg}`;
}

export function applyColorScheme(
  schemeId: ColorSchemeId,
  customTheme?: ManagerrThemeExportV1 | ThemeTokens,
  mode?: ThemeMode,
  accentOverride?: string,
): void {
  if (typeof document === 'undefined') return;

  const currentMode = mode ?? getStoredTheme();
  const safeSchemeId: ColorSchemeId =
    schemeId === 'custom' ? 'custom' : isPresetColorScheme(schemeId) ? schemeId : 'seafoam_jade';
  let tokens: ThemeTokens;

  if (safeSchemeId === 'custom') {
    if (customTheme && typeof customTheme === 'object') {
      const rawTok = 'tokens' in customTheme ? customTheme.tokens : customTheme;
      tokens = deriveTokensFromCore(sanitizeThemeTokens(rawTok as Partial<Record<keyof ThemeTokens, unknown>>));
    } else {
      const stored = getStoredCustomTheme();
      tokens = { ...(stored ? stored.tokens : COLOR_SCHEMES.seafoam_jade.tokens) };
    }
  } else {
    tokens = { ...COLOR_SCHEMES[safeSchemeId].tokens };
  }

  if (isValidHexColor(accentOverride)) {
    tokens.accent = accentOverride;
  }

  document.documentElement.dataset.colorScheme = safeSchemeId;

  const style = document.documentElement.style;
  const accentFg = computeContrastingForeground(tokens.accent);

  style.setProperty('--arr-accent', tokens.accent);
  style.setProperty('--arr-accent-fg', accentFg);
  style.setProperty('--arr-logo-secondary', tokens.logoSecondary);
  style.setProperty('--arr-primary', tokens.primary);
  style.setProperty('--arr-primary-hover', tokens.primaryHover);
  style.setProperty('--arr-primary-border', tokens.primaryBorder);

  // Tinted header is preserved across ALL modes (including OLED and Light)
  style.setProperty('--arr-header-bg-dark', tokens.headerBg);
  style.setProperty('--arr-header-bg-end-dark', tokens.headerBgEnd);
  style.setProperty('--arr-header-border-dark', tokens.headerBorder);

  if (currentMode === 'oled') {
    // In OLED Black mode, remove inline dark surfaces so :root[data-theme-variant="oled"] takes full effect
    style.removeProperty('--arr-page-bg-dark');
    style.removeProperty('--arr-sidebar-bg-dark');
    style.removeProperty('--arr-sidebar-border-dark');
    style.removeProperty('--arr-sidebar-active-bg-dark');
    style.removeProperty('--arr-heading-bg-dark');
    style.removeProperty('--arr-toolbar-bg-dark');
    style.removeProperty('--arr-filterbar-bg-dark');
    style.removeProperty('--arr-card-bg-dark');
    style.removeProperty('--arr-card-border-dark');
    style.removeProperty('--arr-modal-bg-dark');
    style.removeProperty('--arr-input-bg-dark');
  } else {
    style.setProperty('--arr-page-bg-dark', tokens.pageBg);
    style.setProperty('--arr-sidebar-bg-dark', tokens.sidebarBg);
    style.setProperty('--arr-sidebar-border-dark', tokens.sidebarBorder);
    style.setProperty('--arr-sidebar-active-bg-dark', tokens.sidebarActiveBg);
    style.setProperty('--arr-heading-bg-dark', tokens.headingBg);
    style.setProperty('--arr-toolbar-bg-dark', tokens.toolbarBg);
    style.setProperty('--arr-filterbar-bg-dark', tokens.filterBarBg);
    style.setProperty('--arr-card-bg-dark', tokens.cardBg);
    style.setProperty('--arr-card-border-dark', tokens.cardBorder);
    style.setProperty('--arr-modal-bg-dark', tokens.modalBg);
    style.setProperty('--arr-input-bg-dark', tokens.inputBg);
  }

  const metaThemeColor = document.querySelector<HTMLMetaElement>('meta[name="theme-color"]');
  if (metaThemeColor) {
    metaThemeColor.content = currentMode === 'oled' ? '#000000' : tokens.headerBg;
  }

  updateFavicon(tokens.accent, tokens.logoSecondary);

  try {
    localStorage.setItem('managerr-color-scheme', safeSchemeId);
    localStorage.setItem('managerr-accent', tokens.accent);
    if (safeSchemeId === 'custom' && customTheme) {
      const customName = 'name' in customTheme ? customTheme.name : 'Custom Theme';
      const payload = exportThemePayload(customName, currentMode, tokens);
      localStorage.setItem('managerr-custom-theme', JSON.stringify(payload));
    }
  } catch {
    // Storage might be restricted
  }
}

export function applyTheme(theme: ThemeMode, accent?: string): void {
  if (typeof document === 'undefined') return;

  const metaColorScheme = document.querySelector<HTMLMetaElement>('meta[name="color-scheme"]');

  if (theme === 'system') {
    if (metaColorScheme) metaColorScheme.content = 'light dark';
    delete document.documentElement.dataset.themeVariant;
  } else if (theme === 'oled') {
    if (metaColorScheme) metaColorScheme.content = 'dark';
    document.documentElement.dataset.themeVariant = 'oled';
  } else if (theme === 'dark') {
    if (metaColorScheme) metaColorScheme.content = 'dark';
    delete document.documentElement.dataset.themeVariant;
  } else if (theme === 'light') {
    if (metaColorScheme) metaColorScheme.content = 'light';
    delete document.documentElement.dataset.themeVariant;
  }

  const validAccent = isValidHexColor(accent) ? accent : undefined;

  if (validAccent) {
    document.documentElement.style.setProperty('--arr-accent', validAccent);
    document.documentElement.style.setProperty('--arr-accent-fg', computeContrastingForeground(validAccent));
  }

  try {
    if (theme === 'system') {
      localStorage.removeItem('color-scheme');
      localStorage.removeItem('managerr-theme-variant');
    } else if (theme === 'oled') {
      localStorage.setItem('color-scheme', 'dark');
      localStorage.setItem('managerr-theme-variant', 'oled');
    } else if (theme === 'dark') {
      localStorage.setItem('color-scheme', 'dark');
      localStorage.removeItem('managerr-theme-variant');
    } else if (theme === 'light') {
      localStorage.setItem('color-scheme', 'light');
      localStorage.removeItem('managerr-theme-variant');
    }

    if (validAccent) {
      localStorage.setItem('managerr-accent', validAccent);
    }
  } catch {
    // Storage might be blocked
  }

  // Synchronize active scheme surface variables (e.g. OLED clearing)
  const activeScheme = getStoredColorScheme();
  const customTheme = activeScheme === 'custom' ? getStoredCustomTheme() : undefined;
  applyColorScheme(activeScheme, customTheme || undefined, theme, validAccent);
}

export function exportThemePayload(
  name: string,
  mode: ThemeMode = 'dark',
  tokens: ThemeTokens,
): ManagerrThemeExportV1 {
  const cleanName = sanitizeThemeName(name, 'Custom Theme');
  const safeMode: ThemeMode =
    mode === 'oled' || mode === 'dark' || mode === 'light' || mode === 'system' ? mode : 'dark';
  return {
    schema: 'managerr-theme/v1',
    name: cleanName,
    baseMode: safeMode,
    tokens: sanitizeThemeTokens(tokens),
  };
}

export function serializeThemeJson(payload: ManagerrThemeExportV1): string {
  return JSON.stringify(payload, null, 2);
}

export function downloadThemeJson(payload: ManagerrThemeExportV1): void {
  if (typeof window === 'undefined' || typeof document === 'undefined') return;
  const jsonStr = serializeThemeJson(payload);
  const blob = new Blob([jsonStr], { type: 'application/json;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const safeSlug = payload.name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '') || 'custom';
  const link = document.createElement('a');
  link.href = url;
  link.download = `managerr-theme-${safeSlug}.json`;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  URL.revokeObjectURL(url);
}

export function parseThemeJson(
  raw: string,
): { ok: true; payload: ManagerrThemeExportV1 } | { ok: false; error: string } {
  if (!raw || !raw.trim()) {
    return { ok: false, error: 'Theme JSON cannot be empty.' };
  }
  if (raw.length > 16384) {
    return { ok: false, error: 'Theme payload exceeds maximum allowed size of 16 KB.' };
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return { ok: false, error: 'Malformed JSON payload.' };
  }

  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    return { ok: false, error: 'Theme payload must be a JSON object.' };
  }

  const obj = parsed as Record<string, unknown>;

  // Check schema compatibility safely without calling ToString on arbitrary objects
  if (
    Object.prototype.hasOwnProperty.call(obj, 'schema') &&
    obj.schema !== undefined &&
    obj.schema !== null &&
    obj.schema !== '' &&
    obj.schema !== 'managerr-theme/v1'
  ) {
    const schemaLabel = typeof obj.schema === 'string' ? obj.schema.slice(0, 64) : typeof obj.schema;
    return { ok: false, error: `Unsupported theme schema: ${schemaLabel}` };
  }

  let rawTokens: Record<string, unknown>;
  if (obj.tokens && typeof obj.tokens === 'object' && !Array.isArray(obj.tokens)) {
    rawTokens = obj.tokens as Record<string, unknown>;
  } else {
    // Support flat token objects for user convenience
    rawTokens = obj;
  }

  const validatedTokens: Partial<ThemeTokens> = {};
  for (const key of KNOWN_THEME_TOKEN_KEYS) {
    if (Object.prototype.hasOwnProperty.call(rawTokens, key)) {
      const val = rawTokens[key];
      if (typeof val !== 'string') {
        return {
          ok: false,
          error: `Invalid color value for token "${key}". Must be a #RRGGBB string.`,
        };
      }
      const normalized = val.trim();
      if (!HEX_COLOR_REGEX.test(normalized)) {
        return {
          ok: false,
          error: `Invalid hex color value "${normalized.slice(0, 32)}" for token "${key}". Must match #RRGGBB.`,
        };
      }
      validatedTokens[key] = normalized.toLowerCase();
    }
  }

  if (Object.keys(validatedTokens).length === 0) {
    return { ok: false, error: 'Theme payload contains no valid color tokens.' };
  }

  // Derive all remaining tokens smoothly based on the provided core / partial tokens
  const fullTokens = deriveTokensFromCore(validatedTokens);
  const name = sanitizeThemeName(obj.name, 'Imported Theme');

  const rawMode = typeof obj.baseMode === 'string' ? obj.baseMode : undefined;
  const baseMode: ThemeMode =
    rawMode === 'oled' || rawMode === 'dark' || rawMode === 'light' || rawMode === 'system'
      ? rawMode
      : 'dark';

  return {
    ok: true,
    payload: {
      schema: 'managerr-theme/v1',
      name,
      baseMode,
      tokens: fullTokens,
    },
  };
}

export function syncServerThemeIfUnset(serverConfig: {
  defaultTheme?: ThemeMode;
  accentColor?: string;
  colorScheme?: string;
  customTheme?: ManagerrThemeExportV1;
}): void {
  if (typeof window === 'undefined') return;
  try {
    const hasLocalTheme = Boolean(localStorage.getItem('color-scheme') || localStorage.getItem('managerr-theme-variant'));
    const hasLocalScheme = Boolean(localStorage.getItem('managerr-color-scheme'));

    if (!hasLocalTheme && !hasLocalScheme) {
      const rawMode = serverConfig.defaultTheme;
      const targetMode: ThemeMode =
        rawMode === 'oled' || rawMode === 'dark' || rawMode === 'light' || rawMode === 'system'
          ? rawMode
          : 'dark';
      const rawScheme = serverConfig.colorScheme;
      const targetScheme: ColorSchemeId =
        rawScheme === 'custom'
          ? 'custom'
          : isPresetColorScheme(rawScheme)
            ? rawScheme
            : 'seafoam_jade';
      const validAccent = isValidHexColor(serverConfig.accentColor) ? serverConfig.accentColor : undefined;
      applyTheme(targetMode, validAccent);
      applyColorScheme(targetScheme, serverConfig.customTheme, targetMode, validAccent);
    }
  } catch {
    // Ignore storage access errors in restricted contexts
  }
}

export function initTheme(): () => void {
  if (typeof window === 'undefined') return () => {};

  const sync = () => {
    const currentTheme = getStoredTheme();
    const currentScheme = getStoredColorScheme();
    const customTheme = currentScheme === 'custom' ? getStoredCustomTheme() : undefined;
    applyTheme(currentTheme, getStoredAccent());
    applyColorScheme(currentScheme, customTheme || undefined, currentTheme);
  };

  sync();

  const handleStorage = (e: StorageEvent) => {
    if (
      e.key === 'color-scheme' ||
      e.key === 'managerr-theme-variant' ||
      e.key === 'managerr-accent' ||
      e.key === 'managerr-color-scheme' ||
      e.key === 'managerr-custom-theme'
    ) {
      sync();
    }
  };

  window.addEventListener('storage', handleStorage);
  return () => {
    window.removeEventListener('storage', handleStorage);
  };
}
