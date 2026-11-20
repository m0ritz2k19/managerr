import { useEffect, useId, useRef, useState } from 'react';
import {
  Check,
  ChevronDown,
  ChevronUp,
  Code,
  Copy,
  Download,
  Palette,
  RotateCcw,
  Save,
  Upload,
} from 'lucide-react';
import clsx from 'clsx';
import { apiFetch } from '../lib/api';
import {
  COLOR_SCHEME_PRESETS,
  COLOR_SCHEMES,
  applyColorScheme,
  applyTheme,
  deriveTokensFromCore,
  downloadThemeJson,
  exportThemePayload,
  getStoredColorScheme,
  getStoredCustomTheme,
  getStoredTheme,
  isPresetColorScheme,
  isSystemDark,
  isValidHexColor,
  parseThemeJson,
  serializeThemeJson,
  subscribeSystemThemeChange,
  type ColorSchemeId,
  type ManagerrThemeExportV1,
  type ThemeMode,
  type ThemeTokens,
} from '../lib/theme';

const ADVANCED_THEME_TOKENS: Array<{ key: keyof ThemeTokens; label: string }> = [
  { key: 'headerBg', label: 'Header Start' },
  { key: 'headerBgEnd', label: 'Header End' },
  { key: 'headerBorder', label: 'Header Border' },
  { key: 'sidebarBg', label: 'Sidebar Bg' },
  { key: 'sidebarBorder', label: 'Sidebar Border' },
  { key: 'sidebarActiveBg', label: 'Sidebar Active' },
  { key: 'headingBg', label: 'Heading Bg' },
  { key: 'toolbarBg', label: 'Toolbar Bg' },
  { key: 'filterBarBg', label: 'Filter Bar Bg' },
  { key: 'pageBg', label: 'Page Canvas' },
  { key: 'cardBg', label: 'Card Surface' },
  { key: 'cardBorder', label: 'Card Border' },
  { key: 'modalBg', label: 'Modal Surface' },
  { key: 'inputBg', label: 'Input Surface' },
  { key: 'accent', label: 'Accent' },
  { key: 'logoSecondary', label: 'Logo Inner Ring' },
  { key: 'primary', label: 'Primary Button' },
  { key: 'primaryHover', label: 'Primary Hover' },
  { key: 'primaryBorder', label: 'Primary Border' },
];

interface ThemeCustomizerSectionProps {
  managedBy?: Record<string, string>;
}

export function ThemeCustomizerSection({ managedBy }: ThemeCustomizerSectionProps = {}) {
  const defaultThemeSource = managedBy?.['ui.defaultTheme'];
  const accentColorSource = managedBy?.['ui.accentColor'];
  const colorSchemeSource = managedBy?.['ui.colorScheme'];

  const isDefaultThemeLocked = Boolean(defaultThemeSource && defaultThemeSource !== 'database');
  const isAccentColorLocked = Boolean(accentColorSource && accentColorSource !== 'database');
  const isColorSchemeLocked = Boolean(colorSchemeSource && colorSchemeSource !== 'database');

  const [activeTheme, setActiveTheme] = useState<ThemeMode>(() => getStoredTheme());
  const [activeScheme, setActiveScheme] = useState<ColorSchemeId>(() => getStoredColorScheme());
  const [isOsDark, setIsOsDark] = useState<boolean>(() => isSystemDark());
  const [customTheme, setCustomTheme] = useState<ManagerrThemeExportV1 | null>(() => getStoredCustomTheme());

  // Panel toggles
  const [isCustomizerOpen, setIsCustomizerOpen] = useState(() => getStoredColorScheme() === 'custom');
  const [isAdvancedTokensOpen, setIsAdvancedTokensOpen] = useState(false);
  const [isPasteOpen, setIsPasteOpen] = useState(false);

  // Paste JSON state
  const [pasteRaw, setPasteRaw] = useState('');
  const [pasteError, setPasteError] = useState<string | null>(null);

  // Live announcer state (accessible via aria-live="polite", NEVER role="status"!)
  const [announcement, setAnnouncement] = useState<string | null>(null);
  const announceTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const fileInputRef = useRef<HTMLInputElement | null>(null);

  // Active tokens being previewed/edited (always valid 7-char #RRGGBB hex values)
  const [tokens, setTokens] = useState<ThemeTokens>(() => {
    const scheme = getStoredColorScheme();
    if (scheme === 'custom') {
      const stored = getStoredCustomTheme();
      if (stored) return stored.tokens;
    } else if (isPresetColorScheme(scheme)) {
      return COLOR_SCHEMES[scheme].tokens;
    }
    return COLOR_SCHEMES.seafoam_jade.tokens;
  });

  // Separate draft text state while typing in hex inputs so invalid partial strings never corrupt `tokens`
  const [hexDrafts, setHexDrafts] = useState<Partial<Record<keyof ThemeTokens, string>>>({});

  const [customName, setCustomName] = useState<string>(() => {
    return customTheme?.name || 'My Custom Theme';
  });

  const notify = (msg: string) => {
    if (announceTimerRef.current) clearTimeout(announceTimerRef.current);
    setAnnouncement(msg);
    announceTimerRef.current = setTimeout(() => {
      setAnnouncement(null);
    }, 3500);
  };

  useEffect(() => {
    return subscribeSystemThemeChange(setIsOsDark);
  }, []);

  useEffect(() => {
    return () => {
      if (announceTimerRef.current) clearTimeout(announceTimerRef.current);
    };
  }, []);

  const persistServerTheme = async (variables: {
    defaultTheme: ThemeMode;
    colorScheme: string;
    accentColor: string;
    customTheme?: ManagerrThemeExportV1;
    clearCustomTheme?: boolean;
  }): Promise<boolean> => {
    try {
      await apiFetch('/api/v1/settings/general', {
        method: 'PUT',
        body: JSON.stringify(variables),
      });
      return true;
    } catch {
      // Background synchronization fails gracefully in mock/offline environments
      return false;
    }
  };

  const handleSchemeSelect = (schemeId: ColorSchemeId) => {
    setActiveScheme(schemeId);
    setHexDrafts({});
    if (schemeId === 'custom') {
      const targetTokens = customTheme ? customTheme.tokens : tokens;
      setTokens(targetTokens);
      applyColorScheme('custom', customTheme || targetTokens, activeTheme);
      setIsCustomizerOpen(true);
      void persistServerTheme({
        defaultTheme: activeTheme,
        colorScheme: 'custom',
        accentColor: targetTokens.accent,
        customTheme: customTheme || exportThemePayload(customName, activeTheme, targetTokens),
      });
      notify('Switched to Custom Theme.');
    } else if (isPresetColorScheme(schemeId)) {
      const preset = COLOR_SCHEMES[schemeId];
      setTokens(preset.tokens);
      applyColorScheme(schemeId, undefined, activeTheme);
      void persistServerTheme({
        defaultTheme: activeTheme,
        colorScheme: schemeId,
        accentColor: preset.tokens.accent,
        clearCustomTheme: true,
      });
      notify(`Applied ${preset.name} (${preset.subtitle}) theme.`);
    }
  };

  const handleThemeModeChange = (mode: ThemeMode) => {
    setActiveTheme(mode);
    applyTheme(mode, tokens.accent);
    const customPayload =
      activeScheme === 'custom'
        ? customTheme
          ? { ...customTheme, baseMode: mode }
          : exportThemePayload(customName, mode, tokens)
        : undefined;
    applyColorScheme(activeScheme, customPayload, mode);
    void persistServerTheme({
      defaultTheme: mode,
      colorScheme: activeScheme,
      accentColor: tokens.accent,
      customTheme: customPayload,
    });
  };

  const handleCoreColorChange = (key: keyof ThemeTokens, value: string) => {
    if (isColorSchemeLocked || (key === 'accent' && isAccentColorLocked)) return;
    if (!isValidHexColor(value)) return;
    const normalized = value.toLowerCase();
    const updated = deriveTokensFromCore({ [key]: normalized }, tokens);
    setTokens(updated);
    setHexDrafts((prev) => {
      if (!(key in prev)) return prev;
      const next = { ...prev };
      delete next[key];
      return next;
    });
    setActiveScheme('custom');
    applyColorScheme('custom', updated, activeTheme);
  };

  const handleExactTokenChange = (key: keyof ThemeTokens, value: string) => {
    if (isColorSchemeLocked || (key === 'accent' && isAccentColorLocked)) return;
    if (!isValidHexColor(value)) return;
    const normalized = value.toLowerCase();
    const updated: ThemeTokens = { ...tokens, [key]: normalized };
    setTokens(updated);
    setHexDrafts((prev) => {
      if (!(key in prev)) return prev;
      const next = { ...prev };
      delete next[key];
      return next;
    });
    setActiveScheme('custom');
    applyColorScheme('custom', updated, activeTheme);
  };

  const handleHexInputChange = (key: keyof ThemeTokens, rawValue: string, exact = false) => {
    if (isColorSchemeLocked || (key === 'accent' && isAccentColorLocked)) return;
    let val = rawValue.trim();
    if (val && !val.startsWith('#')) val = '#' + val;
    if (val === '' || /^#[0-9a-fA-F]{0,6}$/.test(val)) {
      if (val.length === 7 && isValidHexColor(val)) {
        if (exact) handleExactTokenChange(key, val);
        else handleCoreColorChange(key, val);
      } else {
        setHexDrafts((prev) => ({ ...prev, [key]: val }));
      }
    }
  };

  const handleHexInputBlur = (key: keyof ThemeTokens) => {
    setHexDrafts((prev) => {
      if (!(key in prev)) return prev;
      const next = { ...prev };
      delete next[key];
      return next;
    });
  };

  const handleSaveCustom = async () => {
    const hasPendingInvalidDraft = Object.values(hexDrafts).some(
      (draft) => draft !== undefined && !isValidHexColor(draft),
    );
    if (hasPendingInvalidDraft) {
      notify('Please enter complete 6-digit #RRGGBB hex codes before saving.');
      return;
    }
    const payload = exportThemePayload(customName, activeTheme, tokens);
    setCustomTheme(payload);
    setActiveScheme('custom');
    applyColorScheme('custom', payload, activeTheme);
    const savedOnServer = await persistServerTheme({
      defaultTheme: activeTheme,
      colorScheme: 'custom',
      accentColor: payload.tokens.accent,
      customTheme: payload,
    });
    notify(
      savedOnServer
        ? `Saved "${payload.name}" to instance.`
        : `Applied "${payload.name}" locally (server setting is locked or unreachable).`,
    );
  };

  const handleResetToPreset = () => {
    const defaultTokens = COLOR_SCHEMES.seafoam_jade.tokens;
    setTokens(defaultTokens);
    setHexDrafts({});
    setCustomName('My Custom Theme');
    handleSchemeSelect('seafoam_jade');
    notify('Reset theme to Verdigris defaults.');
  };

  const handleExportJson = () => {
    const schemeName =
      activeScheme === 'custom'
        ? customName
        : isPresetColorScheme(activeScheme)
          ? COLOR_SCHEMES[activeScheme].name
          : 'Theme';
    const payload = exportThemePayload(schemeName, activeTheme, tokens);
    downloadThemeJson(payload);
    notify(`Downloaded ${payload.name} JSON.`);
  };

  const handleCopyJson = async () => {
    const schemeName =
      activeScheme === 'custom'
        ? customName
        : isPresetColorScheme(activeScheme)
          ? COLOR_SCHEMES[activeScheme].name
          : 'Theme';
    const payload = exportThemePayload(schemeName, activeTheme, tokens);
    const jsonStr = serializeThemeJson(payload);
    try {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        await navigator.clipboard.writeText(jsonStr);
        notify('Theme JSON copied to clipboard!');
      } else {
        notify('Clipboard API not available.');
      }
    } catch {
      notify('Failed to copy theme JSON.');
    }
  };

  const handleFileImport = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;

    if (file.size > 16384) {
      notify('Import error: Theme payload exceeds maximum allowed size of 16 KB.');
      e.target.value = '';
      return;
    }

    const reader = new FileReader();
    reader.onload = (evt) => {
      const content = evt.target?.result;
      if (typeof content !== 'string') return;

      const result = parseThemeJson(content);
      if (result.ok) {
        setCustomTheme(result.payload);
        setCustomName(result.payload.name);
        setTokens(result.payload.tokens);
        setHexDrafts({});
        setActiveScheme('custom');
        setIsCustomizerOpen(true);
        const modeToApply = result.payload.baseMode || activeTheme;
        if (result.payload.baseMode) {
          setActiveTheme(result.payload.baseMode);
          applyTheme(result.payload.baseMode, result.payload.tokens.accent);
        }
        applyColorScheme('custom', result.payload, modeToApply);
        void persistServerTheme({
          defaultTheme: modeToApply,
          colorScheme: 'custom',
          accentColor: result.payload.tokens.accent,
          customTheme: result.payload,
        });
        notify(`Successfully imported theme "${result.payload.name}".`);
      } else {
        notify(`Import error: ${result.error}`);
      }
    };
    reader.readAsText(file);
    // Reset input
    e.target.value = '';
  };

  const handlePasteApply = () => {
    setPasteError(null);
    const result = parseThemeJson(pasteRaw);
    if (result.ok) {
      setCustomTheme(result.payload);
      setCustomName(result.payload.name);
      setTokens(result.payload.tokens);
      setHexDrafts({});
      setActiveScheme('custom');
      setIsCustomizerOpen(true);
      setIsPasteOpen(false);
      setPasteRaw('');
      const modeToApply = result.payload.baseMode || activeTheme;
      if (result.payload.baseMode) {
        setActiveTheme(result.payload.baseMode);
        applyTheme(result.payload.baseMode, result.payload.tokens.accent);
      }
      applyColorScheme('custom', result.payload, modeToApply);
      void persistServerTheme({
        defaultTheme: modeToApply,
        colorScheme: 'custom',
        accentColor: result.payload.tokens.accent,
        customTheme: result.payload,
      });
      notify(`Successfully imported pasted theme "${result.payload.name}".`);
    } else {
      setPasteError(result.error);
    }
  };

  const nameInputId = useId();
  const headerBgId = useId();
  const accentId = useId();
  const logoSecondaryId = useId();
  const primaryId = useId();
  const sidebarBgId = useId();
  const pageBgId = useId();
  const cardBgId = useId();
  const pasteAreaId = useId();

  return (
    <div className="space-y-6">
      {/* 1. Theme Mode (Display Mode Row) */}
      <div className="grid grid-cols-1 md:grid-cols-[220px_minmax(0,680px)] items-start gap-x-5 gap-y-1">
        <div className="md:text-right md:pt-1 text-[13px] font-bold text-[#e1e2e3] leading-snug">
          Display Mode
        </div>
        <div>
          {isDefaultThemeLocked && (
            <div className="mb-2 inline-flex items-center gap-1.5 px-2 py-0.5 rounded text-[11px] font-semibold bg-[#2e3e4e] text-[#69c0ff] border border-[#1b3a57]">
              Managed by {defaultThemeSource}
            </div>
          )}
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-2">
            {[
              { id: 'system' as const, label: 'System', desc: `Auto (${isOsDark ? 'Dark' : 'Light'})` },
              { id: 'dark' as const, label: 'Dark', desc: 'Tinted Header' },
              { id: 'oled' as const, label: 'OLED Black', desc: 'True Black #000' },
              { id: 'light' as const, label: 'Light', desc: 'Clean White' },
            ].map((mode) => (
              <button
                key={mode.id}
                type="button"
                disabled={isDefaultThemeLocked}
                aria-pressed={activeTheme === mode.id}
                onClick={() => handleThemeModeChange(mode.id)}
                className={clsx(
                  'p-2.5 rounded-[4px] border text-left transition-colors cursor-pointer disabled:opacity-55 disabled:cursor-not-allowed',
                  activeTheme === mode.id
                    ? 'border-arr-accent bg-arr-card shadow'
                    : 'border-arr-card-border bg-arr-sidebar hover:bg-arr-card',
                )}
              >
                <div className="flex items-center justify-between">
                  <span className="text-[13px] font-semibold text-[#eeeeee]">{mode.label}</span>
                  {activeTheme === mode.id && <Check className="h-3.5 w-3.5 text-arr-accent" />}
                </div>
                <div className="text-[11px] text-[#909293] mt-0.5">{mode.desc}</div>
              </button>
            ))}
          </div>
          <p className="mt-2 text-[12px] text-[#909293] leading-snug">
            Display mode coordinates canvas lightness while preserving your chosen color scheme.
          </p>
        </div>
      </div>

      {/* 2. Color Schemes Grid */}
      <div className="grid grid-cols-1 md:grid-cols-[220px_minmax(0,680px)] items-start gap-x-5 gap-y-1">
        <div className="md:text-right md:pt-1 text-[13px] font-bold text-[#e1e2e3] leading-snug">
          Color Scheme
        </div>
        <div>
          {(isColorSchemeLocked || isAccentColorLocked) && (
            <div className="mb-2 flex flex-wrap gap-2">
              {isColorSchemeLocked && (
                <span className="inline-flex items-center px-2 py-0.5 rounded text-[11px] font-semibold bg-[#2e3e4e] text-[#69c0ff] border border-[#1b3a57]">
                  Color scheme managed by {colorSchemeSource}
                </span>
              )}
              {isAccentColorLocked && (
                <span className="inline-flex items-center px-2 py-0.5 rounded text-[11px] font-semibold bg-[#2e3e4e] text-[#69c0ff] border border-[#1b3a57]">
                  Accent color managed by {accentColorSource}
                </span>
              )}
            </div>
          )}
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-2.5">
            {COLOR_SCHEME_PRESETS.map((preset) => {
              const isActive = activeScheme === preset.id;
              return (
                <button
                  key={preset.id}
                  type="button"
                  disabled={isColorSchemeLocked}
                  aria-pressed={isActive}
                  onClick={() => handleSchemeSelect(preset.id)}
                  className={clsx(
                    'p-3 rounded-[4px] border text-left transition-all cursor-pointer relative flex flex-col justify-between disabled:opacity-55 disabled:cursor-not-allowed',
                    isActive
                      ? 'border-arr-accent bg-arr-card shadow-[0_0_0_1px_var(--arr-accent)]'
                      : 'border-arr-card-border bg-arr-sidebar hover:bg-arr-card hover:border-[#525860]',
                  )}
                >
                  <div className="flex items-start justify-between gap-2 mb-2">
                    <div>
                      <div className="text-[13px] font-semibold text-[#eeeeee] leading-tight">
                        {preset.name}
                      </div>
                      <div className="text-[11px] text-[#909293] mt-0.5">{preset.subtitle}</div>
                    </div>
                    {isActive && <Check className="h-4 w-4 text-arr-accent shrink-0" />}
                  </div>

                  {/* 4-Color Swatch Strip: headerBgEnd, accent, primary, cardBg */}
                  <div className="flex items-center gap-1.5 pt-1 border-t border-arr-card-border/60">
                    <span
                      title={`Header (${preset.tokens.headerBgEnd})`}
                      className="h-4 w-5 rounded-[2px] border border-black/40 shrink-0"
                      style={{ backgroundColor: preset.tokens.headerBgEnd }}
                    />
                    <span
                      title={`Accent (${preset.tokens.accent})`}
                      className="h-4 w-5 rounded-[2px] border border-black/40 shrink-0"
                      style={{ backgroundColor: preset.tokens.accent }}
                    />
                    <span
                      title={`Primary (${preset.tokens.primary})`}
                      className="h-4 w-5 rounded-[2px] border border-black/40 shrink-0"
                      style={{ backgroundColor: preset.tokens.primary }}
                    />
                    <span
                      title={`Surface (${preset.tokens.cardBg})`}
                      className="h-4 w-5 rounded-[2px] border border-black/40 shrink-0"
                      style={{ backgroundColor: preset.tokens.cardBg }}
                    />
                  </div>
                </button>
              );
            })}

            {/* Active Custom Theme Card */}
            {(activeScheme === 'custom' || customTheme !== null) && (
              <button
                type="button"
                aria-pressed={activeScheme === 'custom'}
                onClick={() => handleSchemeSelect('custom')}
                className={clsx(
                  'p-3 rounded-[4px] border text-left transition-all cursor-pointer relative flex flex-col justify-between',
                  activeScheme === 'custom'
                    ? 'border-arr-accent bg-arr-card shadow-[0_0_0_1px_var(--arr-accent)]'
                    : 'border-arr-card-border bg-arr-sidebar hover:bg-arr-card',
                )}
              >
                <div className="flex items-start justify-between gap-2 mb-2">
                  <div>
                    <div className="text-[13px] font-semibold text-[#eeeeee] leading-tight truncate">
                      {customTheme?.name || customName || 'Custom Palette'}
                    </div>
                    <div className="text-[11px] text-[#909293] mt-0.5">User Tailored</div>
                  </div>
                  {activeScheme === 'custom' && <Check className="h-4 w-4 text-arr-accent shrink-0" />}
                </div>

                <div className="flex items-center gap-1.5 pt-1 border-t border-arr-card-border/60">
                  <span
                    title={`Header (${tokens.headerBgEnd})`}
                    className="h-4 w-5 rounded-[2px] border border-black/40 shrink-0"
                    style={{ backgroundColor: tokens.headerBgEnd }}
                  />
                  <span
                    title={`Accent (${tokens.accent})`}
                    className="h-4 w-5 rounded-[2px] border border-black/40 shrink-0"
                    style={{ backgroundColor: tokens.accent }}
                  />
                  <span
                    title={`Primary (${tokens.primary})`}
                    className="h-4 w-5 rounded-[2px] border border-black/40 shrink-0"
                    style={{ backgroundColor: tokens.primary }}
                  />
                  <span
                    title={`Surface (${tokens.cardBg})`}
                    className="h-4 w-5 rounded-[2px] border border-black/40 shrink-0"
                    style={{ backgroundColor: tokens.cardBg }}
                  />
                </div>
              </button>
            )}
          </div>

          <p className="mt-2 text-[12px] text-[#909293] leading-snug">
            Each scheme tints top navigation, sidebars, buttons, and badges harmoniously across the interface.
          </p>
        </div>
      </div>

      {/* 3. Compact Palette Customizer + Exporter / Importer Toolbar */}
      <div className="grid grid-cols-1 md:grid-cols-[220px_minmax(0,680px)] items-start gap-x-5 gap-y-1">
        <div className="md:text-right md:pt-1 text-[13px] font-bold text-[#e1e2e3] leading-snug">
          Theme Studio
        </div>
        <div className="space-y-3">
          {/* Action Toolbar */}
          <div className="flex flex-wrap items-center gap-1.5">
            <button
              type="button"
              onClick={() => setIsCustomizerOpen((v) => !v)}
              className={clsx(
                'inline-flex items-center gap-1.5 h-[32px] px-2.5 text-[12px] font-medium rounded-[4px] border transition-colors cursor-pointer',
                isCustomizerOpen
                  ? 'bg-arr-card border-arr-accent text-white'
                  : 'bg-arr-sidebar hover:bg-arr-card border-arr-card-border text-[#cccccc]',
              )}
            >
              <Palette className="h-3.5 w-3.5 text-arr-accent" />
              <span>Customize Colors</span>
              {isCustomizerOpen ? <ChevronUp className="h-3 w-3 ml-0.5" /> : <ChevronDown className="h-3 w-3 ml-0.5" />}
            </button>

            <button
              type="button"
              onClick={handleExportJson}
              className="inline-flex items-center gap-1.5 h-[32px] px-2.5 text-[12px] font-medium rounded-[4px] border border-arr-card-border bg-arr-sidebar hover:bg-arr-card text-[#cccccc] transition-colors cursor-pointer"
            >
              <Download className="h-3.5 w-3.5 text-arr-accent" />
              <span>Export JSON</span>
            </button>

            <button
              type="button"
              onClick={handleCopyJson}
              className="inline-flex items-center gap-1.5 h-[32px] px-2.5 text-[12px] font-medium rounded-[4px] border border-arr-card-border bg-arr-sidebar hover:bg-arr-card text-[#cccccc] transition-colors cursor-pointer"
            >
              <Copy className="h-3.5 w-3.5 text-arr-accent" />
              <span>Copy JSON</span>
            </button>

            <button
              type="button"
              onClick={() => fileInputRef.current?.click()}
              className="inline-flex items-center gap-1.5 h-[32px] px-2.5 text-[12px] font-medium rounded-[4px] border border-arr-card-border bg-arr-sidebar hover:bg-arr-card text-[#cccccc] transition-colors cursor-pointer"
            >
              <Upload className="h-3.5 w-3.5 text-arr-accent" />
              <span>Import File</span>
            </button>

            <button
              type="button"
              onClick={() => setIsPasteOpen((v) => !v)}
              className={clsx(
                'inline-flex items-center gap-1.5 h-[32px] px-2.5 text-[12px] font-medium rounded-[4px] border transition-colors cursor-pointer',
                isPasteOpen
                  ? 'bg-arr-card border-arr-accent text-white'
                  : 'bg-arr-sidebar hover:bg-arr-card border-arr-card-border text-[#cccccc]',
              )}
            >
              <Code className="h-3.5 w-3.5 text-arr-accent" />
              <span>Paste JSON</span>
            </button>

            {/* Hidden File Input */}
            <input
              ref={fileInputRef}
              type="file"
              accept=".json,application/json"
              aria-label="Import theme file"
              onChange={handleFileImport}
              className="visually-hidden"
            />
          </div>

          {/* Accessible Live Region: Uses aria-live="polite" WITHOUT role="status" so test 2 remains unique */}
          {announcement && (
            <div className="py-1">
              <span aria-live="polite" className="text-[12px] font-medium text-arr-accent">
                {announcement}
              </span>
            </div>
          )}

          {/* Paste JSON Panel */}
          {isPasteOpen && (
            <div className="p-3.5 rounded-[4px] border border-arr-card-border bg-arr-card space-y-2.5">
              <div className="flex items-center justify-between">
                <label htmlFor={pasteAreaId} className="text-[12px] font-bold text-[#eeeeee]">
                  Paste Managerr Theme JSON
                </label>
                <span className="text-[11px] text-[#909293]">JSON object with hex colors</span>
              </div>
              <textarea
                id={pasteAreaId}
                value={pasteRaw}
                onChange={(e) => {
                  setPasteRaw(e.target.value);
                  setPasteError(null);
                }}
                rows={5}
                placeholder='{"schema": "managerr-theme/v1", "name": "Ocean Deep", "tokens": { "headerBg": "#0e2a38", "accent": "#38bdf8", "primary": "#0369a1", ... }}'
                className="arr-input font-mono text-[12px] resize-y"
              />
              {pasteError && (
                <div className="text-[12px] text-[#f05050] font-medium leading-snug">{pasteError}</div>
              )}
              <div className="flex items-center gap-2 pt-1">
                <button
                  type="button"
                  onClick={handlePasteApply}
                  disabled={!pasteRaw.trim()}
                  className="inline-flex items-center gap-1.5 h-[30px] px-3 text-[12px] font-medium rounded-[4px] bg-arr-primary hover:bg-arr-primary-hover border border-arr-primary-border text-white transition-colors cursor-pointer disabled:opacity-45 disabled:pointer-events-none"
                >
                  <Check className="h-3.5 w-3.5" />
                  <span>Apply Imported Theme</span>
                </button>
                <button
                  type="button"
                  onClick={() => {
                    setIsPasteOpen(false);
                    setPasteError(null);
                  }}
                  className="inline-flex items-center h-[30px] px-2.5 text-[12px] font-medium rounded-[4px] border border-arr-card-border bg-arr-sidebar hover:bg-arr-card text-[#cccccc] cursor-pointer"
                >
                  Cancel
                </button>
              </div>
            </div>
          )}

          {/* Customize Colors Panel */}
          {isCustomizerOpen && (
            <div className="p-4 rounded-[4px] border border-arr-card-border bg-arr-card space-y-4">
              <div className="flex flex-wrap items-center justify-between gap-3 border-b border-arr-card-border pb-3">
                <div className="flex items-center gap-2 flex-1 min-w-[200px]">
                  <label htmlFor={nameInputId} className="text-[12px] font-bold text-[#eeeeee] whitespace-nowrap">
                    Theme Name:
                  </label>
                  <input
                    id={nameInputId}
                    type="text"
                    maxLength={64}
                    value={customName}
                    onChange={(e) => setCustomName(e.target.value)}
                    className="arr-input h-[28px] py-1 text-[12px] max-w-[240px]"
                  />
                </div>
                <div className="flex items-center gap-2">
                  <button
                    type="button"
                    onClick={handleSaveCustom}
                    className="inline-flex items-center gap-1.5 h-[28px] px-2.5 text-[12px] font-medium rounded-[4px] bg-arr-primary hover:bg-arr-primary-hover border border-arr-primary-border text-white transition-colors cursor-pointer"
                  >
                    <Save className="h-3.5 w-3.5" />
                    <span>Save to Instance</span>
                  </button>
                  <button
                    type="button"
                    onClick={handleResetToPreset}
                    className="inline-flex items-center gap-1.5 h-[28px] px-2.5 text-[12px] font-medium rounded-[4px] border border-arr-card-border bg-arr-sidebar hover:bg-arr-card text-[#cccccc] transition-colors cursor-pointer"
                  >
                    <RotateCcw className="h-3.5 w-3.5" />
                    <span>Reset</span>
                  </button>
                </div>
              </div>

              {/* 7 Core Color Pickers Grid */}
              <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-2.5">
                {/* 1. Header Bar */}
                <div className="p-2.5 rounded-[4px] border border-arr-card-border/80 bg-arr-sidebar/60 space-y-1.5">
                  <label htmlFor={headerBgId} className="text-[11px] font-semibold text-[#eeeeee] block">
                    Header Bar
                  </label>
                  <div className="flex items-center gap-2">
                    <input
                      id={headerBgId}
                      type="color"
                      aria-label="Header Bar color picker"
                      value={tokens.headerBg}
                      onChange={(e) => handleCoreColorChange('headerBg', e.target.value)}
                      className="h-7 w-8 shrink-0 rounded cursor-pointer border border-black/40 bg-transparent p-0"
                    />
                    <input
                      type="text"
                      aria-label="Header Bar hex code"
                      maxLength={7}
                      value={hexDrafts.headerBg ?? tokens.headerBg}
                      onChange={(e) => handleHexInputChange('headerBg', e.target.value)}
                      onBlur={() => handleHexInputBlur('headerBg')}
                      className="arr-input h-7 min-w-0 px-2 font-mono text-[12px]"
                    />
                  </div>
                </div>

                {/* 2. Accent */}
                <div className="p-2.5 rounded-[4px] border border-arr-card-border/80 bg-arr-sidebar/60 space-y-1.5">
                  <label htmlFor={accentId} className="text-[11px] font-semibold text-[#eeeeee] block">
                    Accent
                  </label>
                  <div className="flex items-center gap-2">
                    <input
                      id={accentId}
                      type="color"
                      aria-label="Accent color picker"
                      value={tokens.accent}
                      onChange={(e) => handleCoreColorChange('accent', e.target.value)}
                      className="h-7 w-8 shrink-0 rounded cursor-pointer border border-black/40 bg-transparent p-0"
                    />
                    <input
                      type="text"
                      aria-label="Accent hex code"
                      maxLength={7}
                      value={hexDrafts.accent ?? tokens.accent}
                      onChange={(e) => handleHexInputChange('accent', e.target.value)}
                      onBlur={() => handleHexInputBlur('accent')}
                      className="arr-input h-7 min-w-0 px-2 font-mono text-[12px]"
                    />
                  </div>
                </div>

                {/* 3. Logo Inner Ring */}
                <div className="p-2.5 rounded-[4px] border border-arr-card-border/80 bg-arr-sidebar/60 space-y-1.5">
                  <label htmlFor={logoSecondaryId} className="text-[11px] font-semibold text-[#eeeeee] block">
                    Logo Inner Ring
                  </label>
                  <div className="flex items-center gap-2">
                    <input
                      id={logoSecondaryId}
                      type="color"
                      aria-label="Logo Inner Ring color picker"
                      value={tokens.logoSecondary}
                      onChange={(e) => handleCoreColorChange('logoSecondary', e.target.value)}
                      className="h-7 w-8 shrink-0 rounded cursor-pointer border border-black/40 bg-transparent p-0"
                    />
                    <input
                      type="text"
                      aria-label="Logo Inner Ring hex code"
                      maxLength={7}
                      value={hexDrafts.logoSecondary ?? tokens.logoSecondary}
                      onChange={(e) => handleHexInputChange('logoSecondary', e.target.value)}
                      onBlur={() => handleHexInputBlur('logoSecondary')}
                      className="arr-input h-7 min-w-0 px-2 font-mono text-[12px]"
                    />
                  </div>
                </div>

                {/* 4. Primary Button */}
                <div className="p-2.5 rounded-[4px] border border-arr-card-border/80 bg-arr-sidebar/60 space-y-1.5">
                  <label htmlFor={primaryId} className="text-[11px] font-semibold text-[#eeeeee] block">
                    Primary Button
                  </label>
                  <div className="flex items-center gap-2">
                    <input
                      id={primaryId}
                      type="color"
                      aria-label="Primary Button color picker"
                      value={tokens.primary}
                      onChange={(e) => handleCoreColorChange('primary', e.target.value)}
                      className="h-7 w-8 shrink-0 rounded cursor-pointer border border-black/40 bg-transparent p-0"
                    />
                    <input
                      type="text"
                      aria-label="Primary Button hex code"
                      maxLength={7}
                      value={hexDrafts.primary ?? tokens.primary}
                      onChange={(e) => handleHexInputChange('primary', e.target.value)}
                      onBlur={() => handleHexInputBlur('primary')}
                      className="arr-input h-7 min-w-0 px-2 font-mono text-[12px]"
                    />
                  </div>
                </div>

                {/* 5. Sidebar */}
                <div className="p-2.5 rounded-[4px] border border-arr-card-border/80 bg-arr-sidebar/60 space-y-1.5">
                  <label htmlFor={sidebarBgId} className="text-[11px] font-semibold text-[#eeeeee] block">
                    Sidebar
                  </label>
                  <div className="flex items-center gap-2">
                    <input
                      id={sidebarBgId}
                      type="color"
                      aria-label="Sidebar color picker"
                      value={tokens.sidebarBg}
                      onChange={(e) => handleCoreColorChange('sidebarBg', e.target.value)}
                      className="h-7 w-8 shrink-0 rounded cursor-pointer border border-black/40 bg-transparent p-0"
                    />
                    <input
                      type="text"
                      aria-label="Sidebar hex code"
                      maxLength={7}
                      value={hexDrafts.sidebarBg ?? tokens.sidebarBg}
                      onChange={(e) => handleHexInputChange('sidebarBg', e.target.value)}
                      onBlur={() => handleHexInputBlur('sidebarBg')}
                      className="arr-input h-7 min-w-0 px-2 font-mono text-[12px]"
                    />
                  </div>
                </div>

                {/* 6. Page Canvas */}
                <div className="p-2.5 rounded-[4px] border border-arr-card-border/80 bg-arr-sidebar/60 space-y-1.5">
                  <label htmlFor={pageBgId} className="text-[11px] font-semibold text-[#eeeeee] block">
                    Page Canvas
                  </label>
                  <div className="flex items-center gap-2">
                    <input
                      id={pageBgId}
                      type="color"
                      aria-label="Page Canvas color picker"
                      value={tokens.pageBg}
                      onChange={(e) => handleCoreColorChange('pageBg', e.target.value)}
                      className="h-7 w-8 shrink-0 rounded cursor-pointer border border-black/40 bg-transparent p-0"
                    />
                    <input
                      type="text"
                      aria-label="Page Canvas hex code"
                      maxLength={7}
                      value={hexDrafts.pageBg ?? tokens.pageBg}
                      onChange={(e) => handleHexInputChange('pageBg', e.target.value)}
                      onBlur={() => handleHexInputBlur('pageBg')}
                      className="arr-input h-7 min-w-0 px-2 font-mono text-[12px]"
                    />
                  </div>
                </div>

                {/* 7. Card Surface */}
                <div className="p-2.5 rounded-[4px] border border-arr-card-border/80 bg-arr-sidebar/60 space-y-1.5">
                  <label htmlFor={cardBgId} className="text-[11px] font-semibold text-[#eeeeee] block">
                    Card Surface
                  </label>
                  <div className="flex items-center gap-2">
                    <input
                      id={cardBgId}
                      type="color"
                      aria-label="Card Surface color picker"
                      value={tokens.cardBg}
                      onChange={(e) => handleCoreColorChange('cardBg', e.target.value)}
                      className="h-7 w-8 shrink-0 rounded cursor-pointer border border-black/40 bg-transparent p-0"
                    />
                    <input
                      type="text"
                      aria-label="Card Surface hex code"
                      maxLength={7}
                      value={hexDrafts.cardBg ?? tokens.cardBg}
                      onChange={(e) => handleHexInputChange('cardBg', e.target.value)}
                      onBlur={() => handleHexInputBlur('cardBg')}
                      className="arr-input h-7 min-w-0 px-2 font-mono text-[12px]"
                    />
                  </div>
                </div>
              </div>

              <p className="text-[11px] text-[#909293] leading-snug">
                Modifying any of the 7 core colors smoothly recomputes matching hover, border, and input shades in real time.
              </p>

              {/* Expandable Advanced Token Overrides (19 Tokens) */}
              <div className="pt-2 border-t border-arr-card-border">
                <button
                  type="button"
                  onClick={() => setIsAdvancedTokensOpen((v) => !v)}
                  className="inline-flex items-center gap-1.5 text-[12px] font-semibold text-arr-accent hover:underline cursor-pointer"
                >
                  <span>Advanced Token Overrides (19 Tokens)</span>
                  {isAdvancedTokensOpen ? <ChevronUp className="h-3.5 w-3.5" /> : <ChevronDown className="h-3.5 w-3.5" />}
                </button>

                {isAdvancedTokensOpen && (
                  <div className="mt-3 grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-2.5">
                    {ADVANCED_THEME_TOKENS.map(({ key, label }) => {
                      const locked = isColorSchemeLocked || (key === 'accent' && isAccentColorLocked);
                      return (
                        <div key={key} className="p-2 rounded-[4px] border border-arr-card-border/70 bg-arr-sidebar/40 space-y-1">
                          <div className="flex items-center justify-between gap-1">
                            <span className="text-[11px] font-semibold text-[#eeeeee]">{label}</span>
                            <code className="text-[10px] text-[#909293]">{key}</code>
                          </div>
                          <div className="flex items-center gap-2">
                            <input
                              type="color"
                              disabled={locked}
                              aria-label={`${label} (${key}) color picker`}
                              value={tokens[key]}
                              onChange={(e) => handleExactTokenChange(key, e.target.value)}
                              className="h-6 w-7 shrink-0 rounded cursor-pointer border border-black/40 bg-transparent p-0 disabled:opacity-50 disabled:cursor-not-allowed"
                            />
                            <input
                              type="text"
                              disabled={locked}
                              aria-label={`${label} (${key}) hex code`}
                              maxLength={7}
                              value={hexDrafts[key] ?? tokens[key]}
                              onChange={(e) => handleHexInputChange(key, e.target.value, true)}
                              onBlur={() => handleHexInputBlur(key)}
                              className="arr-input h-6 min-w-0 px-2 font-mono text-[11px]"
                            />
                          </div>
                        </div>
                      );
                    })}
                  </div>
                )}
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
