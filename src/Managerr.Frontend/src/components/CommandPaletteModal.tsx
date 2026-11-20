import { useEffect, useId, useRef, useState } from 'react';
import { useNavigate } from 'react-router';
import {
  Activity,
  ArrowRight,
  Calendar,
  Clock,
  Compass,
  Download,
  Film,
  Key,
  Moon,
  Palette,
  Radio,
  Search,
  Settings,
  Shield,
  Sliders,
  Sparkles,
  Users,
} from 'lucide-react';
import {
  applyColorScheme,
  applyTheme,
  getStoredAccent,
  getStoredTheme,
} from '../lib/theme';

export interface CommandItem {
  id: string;
  category: 'Navigation' | 'Actions' | 'Themes';
  title: string;
  subtitle?: string;
  icon: React.ComponentType<{ className?: string }>;
  action: () => void;
  keywords?: string[];
}

interface CommandPaletteModalProps {
  isOpen: boolean;
  onClose: () => void;
  canSystemAdmin?: boolean;
  canReadCleanerr?: boolean;
}

export function CommandPaletteModal({
  isOpen,
  onClose,
  canSystemAdmin: _canSystemAdmin,
  canReadCleanerr = false,
}: CommandPaletteModalProps) {
  const dialogRef = useRef<HTMLDialogElement | null>(null);
  const inputRef = useRef<HTMLInputElement | null>(null);
  const titleId = useId();
  const navigate = useNavigate();

  const [query, setQuery] = useState('');
  const [selectedIndex, setSelectedIndex] = useState(0);

  // Define commands
  const commands: CommandItem[] = [
    // Navigation
    {
      id: 'nav-catalog',
      category: 'Navigation',
      title: 'Media Library',
      subtitle: 'Browse movies, series, books, and music',
      icon: Film,
      action: () => navigate('/library'),
      keywords: ['movies', 'series', 'shows', 'tv', 'books', 'music', 'home'],
    },
    {
      id: 'nav-calendar',
      category: 'Navigation',
      title: 'Calendar',
      subtitle: 'Upcoming releases and air dates',
      icon: Calendar,
      action: () => navigate('/calendar'),
      keywords: ['schedule', 'releases', 'upcoming'],
    },
    {
      id: 'nav-queue',
      category: 'Navigation',
      title: 'Activity: Queue',
      subtitle: 'Monitor active downloads and imports',
      icon: Activity,
      action: () => navigate('/activity/queue'),
      keywords: ['downloads', 'progress', 'nzb', 'torrents'],
    },
    {
      id: 'nav-history',
      category: 'Navigation',
      title: 'Activity: History',
      subtitle: 'Recent acquisitions, grabs, and imports',
      icon: Clock,
      action: () => navigate('/activity/history'),
      keywords: ['logs', 'grabs', 'past'],
    },
    {
      id: 'nav-quarantine',
      category: 'Navigation',
      title: 'Activity: Quarantine',
      subtitle: 'Review audio/subtitle mismatches',
      icon: Shield,
      action: () => navigate('/activity/quarantine'),
      keywords: ['verification', 'audio', 'mismatch'],
    },
    {
      id: 'nav-appearance-theme',
      category: 'Navigation',
      title: 'Settings: Appearance & Theme',
      subtitle: 'Color schemes, OLED mode, JSON theme import/export',
      icon: Palette,
      action: () => navigate('/settings/general'),
      keywords: ['theme', 'appearance', 'palette', 'color', 'scheme', 'customizer'],
    },
    {
      id: 'nav-general-settings',
      category: 'Navigation',
      title: 'Settings: General',
      subtitle: 'Instance name, host endpoints, API key',
      icon: Settings,
      action: () => navigate('/settings/general'),
      keywords: ['api key', 'url bases', 'host', 'port'],
    },
    {
      id: 'nav-profiles',
      category: 'Navigation',
      title: 'Settings: Profiles & Custom Formats',
      subtitle: 'Quality cutoffs, upgrades, and TRaSH regex rules',
      icon: Sliders,
      action: () => navigate('/settings/profiles'),
      keywords: ['quality', 'trash', 'scores', 'upgrades'],
    },
    {
      id: 'nav-indexers',
      category: 'Navigation',
      title: 'Settings: Indexers',
      subtitle: 'Newznab and Torznab search providers',
      icon: Radio,
      action: () => navigate('/settings/indexers'),
      keywords: ['torznab', 'newznab', 'prowlarr', 'usenet', 'torrent'],
    },
    {
      id: 'nav-downloaders',
      category: 'Navigation',
      title: 'Settings: Download Clients',
      subtitle: 'SABnzbd, qBittorrent, Transmission',
      icon: Download,
      action: () => navigate('/settings/downloaders'),
      keywords: ['clients', 'sabnzbd', 'qbittorrent'],
    },
    {
      id: 'nav-users',
      category: 'Navigation',
      title: 'Settings: Users & Access',
      subtitle: 'Manage local and PMS users and roles',
      icon: Users,
      action: () => navigate('/settings/users'),
      keywords: ['accounts', 'permissions', 'roles', 'sso'],
    },
    {
      id: 'nav-security',
      category: 'Navigation',
      title: 'System: Security & Passkeys',
      subtitle: 'Scoped API keys, WebAuthn passkeys, profile',
      icon: Key,
      action: () => navigate('/system/security'),
      keywords: ['passkeys', 'tokens', 'credentials', 'password'],
    },
    ...(canReadCleanerr
      ? [
          {
            id: 'nav-cleanerr-overview',
            category: 'Navigation' as const,
            title: 'Cleanerr: Overview',
            subtitle: 'Disk usage, reclaim forecast, and cleanup summary',
            icon: Sparkles,
            action: () => navigate('/cleanerr'),
            keywords: ['cleanerr', 'cleanup', 'storage', 'reclaim', 'forecast'],
          },
          {
            id: 'nav-cleanerr-studio',
            category: 'Navigation' as const,
            title: 'Cleanerr: Workflow & DAG Studio',
            subtitle: 'Visual node editor, Maintainerr rule builder, and schedules',
            icon: Sparkles,
            action: () => navigate('/cleanerr/studio'),
            keywords: ['cleanerr', 'studio', 'dag', 'rules', 'workflows', 'cron'],
          },
          {
            id: 'nav-cleanerr-queue',
            category: 'Navigation' as const,
            title: 'Cleanerr: Leaving Soon Queue',
            subtitle: 'Grace-period items, auto-rescue, and postpones',
            icon: Clock,
            action: () => navigate('/cleanerr/queue'),
            keywords: ['cleanerr', 'leaving soon', 'queue', 'grace', 'rescue'],
          },
          {
            id: 'nav-cleanerr-exclusions',
            category: 'Navigation' as const,
            title: 'Cleanerr: Exclusions',
            subtitle: 'Protected movies, series, tags, and Plex labels',
            icon: Shield,
            action: () => navigate('/cleanerr/exclusions'),
            keywords: ['cleanerr', 'exclusions', 'protect', 'keep'],
          },
          {
            id: 'nav-cleanerr-discord',
            category: 'Navigation' as const,
            title: 'Cleanerr: Discord & Settings',
            subtitle: 'Discord bot gateway, slash commands, and Seerr settings',
            icon: Settings,
            action: () => navigate('/cleanerr/discord'),
            keywords: ['cleanerr', 'discord', 'seerr', 'settings'],
          },
        ]
      : []),

    // 5 Color Schemes
    {
      id: 'scheme-seafoam',
      category: 'Themes',
      title: 'Color Scheme: Verdigris (Seafoam Jade)',
      subtitle: 'Deep viridian header with seafoam jade accents (#2dd4bf)',
      icon: Palette,
      action: () => applyColorScheme('seafoam_jade', undefined, getStoredTheme()),
      keywords: ['scheme', 'verdigris', 'seafoam', 'jade', 'teal', 'green'],
    },
    {
      id: 'scheme-amethyst',
      category: 'Themes',
      title: 'Color Scheme: Royal Amethyst (Ultraviolet)',
      subtitle: 'Deep plum header with neon ultraviolet accents (#c084fc)',
      icon: Palette,
      action: () => applyColorScheme('royal_amethyst', undefined, getStoredTheme()),
      keywords: ['scheme', 'amethyst', 'purple', 'royal', 'ultraviolet'],
    },
    {
      id: 'scheme-chartreuse',
      category: 'Themes',
      title: 'Color Scheme: Cyber Chartreuse (Acid Olive)',
      subtitle: 'Dark olive canvas with electric lime accents (#a3e635)',
      icon: Palette,
      action: () => applyColorScheme('cyber_chartreuse', undefined, getStoredTheme()),
      keywords: ['scheme', 'chartreuse', 'cyber', 'lime', 'olive'],
    },
    {
      id: 'scheme-copper',
      category: 'Themes',
      title: 'Color Scheme: Brushed Copper (Terracotta)',
      subtitle: 'Charcoal brown canvas with warm copper accents (#f69373)',
      icon: Palette,
      action: () => applyColorScheme('brushed_copper', undefined, getStoredTheme()),
      keywords: ['scheme', 'copper', 'terracotta', 'brushed', 'orange', 'warm'],
    },
    {
      id: 'scheme-periwinkle',
      category: 'Themes',
      title: 'Color Scheme: Nordic Sapphire (Periwinkle Slate)',
      subtitle: 'Midnight navy canvas with periwinkle indigo accents (#93a8fe)',
      icon: Palette,
      action: () => applyColorScheme('nordic_periwinkle', undefined, getStoredTheme()),
      keywords: ['scheme', 'nordic', 'sapphire', 'periwinkle', 'blue', 'indigo'],
    },

    // Display Modes
    {
      id: 'theme-oled',
      category: 'Themes',
      title: 'Display Mode: OLED Pure Black',
      subtitle: '#000000 background paired with active color scheme',
      icon: Moon,
      action: () => applyTheme('oled', getStoredAccent()),
      keywords: ['oled', 'dark', 'black', 'pure black'],
    },
    {
      id: 'theme-dark',
      category: 'Themes',
      title: 'Display Mode: Dark',
      subtitle: 'Tinted header and deep surface palette',
      icon: Palette,
      action: () => applyTheme('dark', getStoredAccent()),
      keywords: ['dark', 'servarr', 'grey'],
    },
    {
      id: 'theme-light',
      category: 'Themes',
      title: 'Display Mode: Light',
      subtitle: 'High-contrast clean daylight canvas',
      icon: Palette,
      action: () => applyTheme('light', getStoredAccent()),
      keywords: ['light', 'white', 'day'],
    },
    {
      id: 'theme-system',
      category: 'Themes',
      title: 'Display Mode: System',
      subtitle: 'Automatically follow operating system preferences',
      icon: Compass,
      action: () => applyTheme('system', getStoredAccent()),
      keywords: ['auto', 'os', 'system'],
    },
  ];

  // Filter commands
  const filtered = commands.filter((cmd) => {
    if (!query.trim()) return true;
    const term = query.toLowerCase().trim();
    if (cmd.title.toLowerCase().includes(term)) return true;
    if (cmd.subtitle?.toLowerCase().includes(term)) return true;
    if (cmd.category.toLowerCase().includes(term)) return true;
    if (cmd.keywords?.some((k) => k.toLowerCase().includes(term))) return true;
    return false;
  });

  // Modal open / close effects
  useEffect(() => {
    const dialog = dialogRef.current;
    if (!dialog) return;

    if (isOpen) {
      if (!dialog.open) {
        dialog.showModal();
        setQuery('');
        setSelectedIndex(0);
        setTimeout(() => inputRef.current?.focus(), 50);
      }
    } else {
      if (dialog.open) {
        dialog.close();
      }
    }
  }, [isOpen]);

  // Keyboard navigation inside list
  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setSelectedIndex((prev) => (prev + 1 < filtered.length ? prev + 1 : 0));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setSelectedIndex((prev) => (prev - 1 >= 0 ? prev - 1 : filtered.length - 1));
    } else if (e.key === 'Enter') {
      e.preventDefault();
      const target = filtered[selectedIndex];
      if (target) {
        target.action();
        onClose();
      }
    } else if (e.key === 'Escape') {
      onClose();
    }
  };

  return (
    <dialog
      ref={dialogRef}
      aria-labelledby={titleId}
      {...({ closedby: 'any' } as Record<string, string>)}
      onClose={onClose}
      className="m-auto p-0 bg-transparent border-0 max-w-xl w-[calc(100%-2rem)] text-[#e1e2e3] backdrop:bg-black/70 open:flex open:flex-col overflow-hidden"
    >
      <h2 id={titleId} className="sr-only">
        Command Palette
      </h2>
      <div className="bg-arr-modal border border-arr-card-border rounded-[6px] shadow-[0_12px_36px_rgba(0,0,0,0.85)] overflow-hidden flex flex-col max-h-[80vh]">
        {/* Header Search Input */}
        <div className="flex items-center gap-3 px-4 py-3 border-b border-arr-card-border bg-arr-toolbar">
          <Search className="h-5 w-5 text-arr-accent shrink-0" />
          <input
            ref={inputRef}
            type="text"
            aria-label="Search commands"
            role="combobox"
            aria-expanded="true"
            aria-controls="command-results-list"
            autoComplete="off"
            spellCheck={false}
            placeholder="Type a command, page, or search term... (↑↓ to navigate, Enter to select)"
            value={query}
            onChange={(e) => {
              setQuery(e.target.value);
              setSelectedIndex(0);
            }}
            onKeyDown={handleKeyDown}
            className="w-full bg-transparent border-none text-[15px] text-[#eeeeee] placeholder-[#808285] focus:outline-none"
          />
          <kbd className="hidden sm:inline-block px-1.5 py-0.5 text-[11px] font-mono text-[#909293] bg-arr-card border border-arr-card-border rounded">
            ESC
          </kbd>
        </div>

        {/* Results List */}
        <div id="command-results-list" role="listbox" className="p-2 overflow-y-auto max-h-[420px] space-y-1">
          {filtered.length === 0 ? (
            <div className="p-6 text-center text-[13px] text-[#909293]">
              No commands matching &ldquo;{query}&rdquo;
            </div>
          ) : (
            filtered.map((cmd, idx) => {
              const Icon = cmd.icon;
              const isSelected = idx === selectedIndex;
              return (
                <button
                  key={cmd.id}
                  type="button"
                  role="option"
                  aria-selected={isSelected}
                  onClick={() => {
                    cmd.action();
                    onClose();
                  }}
                  onMouseEnter={() => setSelectedIndex(idx)}
                  className={`w-full flex items-center gap-3 px-3 py-2.5 rounded-[4px] text-left transition-colors cursor-pointer ${
                    isSelected
                      ? 'bg-arr-accent/20 border border-arr-accent/60 text-white'
                      : 'hover:bg-arr-sidebar-active border border-transparent text-[#cccccc]'
                  }`}
                >
                  <div
                    className={`p-1.5 rounded-[3px] shrink-0 ${
                      isSelected ? 'bg-arr-accent text-arr-accent-fg' : 'bg-arr-sidebar text-[#909293]'
                    }`}
                  >
                    <Icon className="h-4 w-4" />
                  </div>
                  <div className="flex-1 min-w-0">
                    <div className="text-[13px] font-medium leading-snug truncate text-[#eeeeee]">
                      {cmd.title}
                    </div>
                    {cmd.subtitle && (
                      <div className="text-[11px] text-[#909293] leading-snug truncate">
                        {cmd.subtitle}
                      </div>
                    )}
                  </div>
                  <span className="text-[10px] uppercase font-semibold text-[#808285] px-1.5 py-0.5 bg-arr-sidebar border border-arr-card-border rounded shrink-0">
                    {cmd.category}
                  </span>
                  {isSelected && <ArrowRight className="h-3.5 w-3.5 text-arr-accent shrink-0" />}
                </button>
              );
            })
          )}
        </div>

        {/* Footer shortcuts */}
        <div className="px-4 py-2 border-t border-arr-card-border bg-arr-toolbar flex items-center justify-between text-[11px] text-[#909293]">
          <div className="flex items-center gap-3">
            <span><kbd className="font-mono bg-arr-card px-1 py-0.5 rounded border border-arr-card-border">↑</kbd> <kbd className="font-mono bg-arr-card px-1 py-0.5 rounded border border-arr-card-border">↓</kbd> navigate</span>
            <span><kbd className="font-mono bg-arr-card px-1 py-0.5 rounded border border-arr-card-border">↵</kbd> select</span>
          </div>
          <div>Managerr Command Palette</div>
        </div>
      </div>
    </dialog>
  );
}
