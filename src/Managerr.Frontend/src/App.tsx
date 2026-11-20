import { Suspense, lazy, useEffect, useMemo, useState } from 'react';
import {
  Link,
  NavLink,
  Navigate,
  Route,
  Routes,
  useLocation,
  useNavigate,
  useParams,
  useSearchParams,
} from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  AlertCircle,
  Calendar,
  Clock,
  Film,
  LogOut,
  Menu,
  Search,
  Server,
  Settings,
  Sparkles,
  User,
  X,
  type LucideIcon,
} from 'lucide-react';
import clsx from 'clsx';
import {
  apiFetch,
  type AuthStatus,
  type MeResponse,
  type SystemHealthPingResponse,
  setCsrfToken,
} from './lib/api';
import {
  matchesCatalogWorkQuery,
  type CatalogSearchWorkSummary,
} from './lib/catalogSearch';
import {
  buildSavedCatalogLibraryPath,
  saveCatalogScrollY,
  stripHealthAndLegacyFilterParams,
} from './components/catalog/catalogFilterPersistence';
import { LoginView } from './components/LoginView';
import { CommandPaletteModal } from './components/CommandPaletteModal';
import { useRealtimeStream } from './lib/useRealtimeStream';
import { syncServerThemeIfUnset } from './lib/theme';
import { ManagerrLogo, PageContentBody, PageToolbar } from './components/arr/ArrPrimitives';

const CatalogView = lazy(() =>
  import('./components/CatalogView').then((m) => ({ default: m.CatalogView })),
);
const AcquisitionView = lazy(() =>
  import('./components/AcquisitionView').then((m) => ({ default: m.AcquisitionView })),
);
const ActivityQueueAndHistoryView = lazy(() =>
  import('./components/ActivityQueueAndHistoryView').then((m) => ({
    default: m.ActivityQueueAndHistoryView,
  })),
);
const ProfilesAndQuarantineView = lazy(() =>
  import('./components/ProfilesAndQuarantineView').then((m) => ({
    default: m.ProfilesAndQuarantineView,
  })),
);
const EcosystemConnectAndListsView = lazy(() =>
  import('./components/EcosystemConnectAndListsView').then((m) => ({
    default: m.EcosystemConnectAndListsView,
  })),
);
const MigrationAndCompatView = lazy(() =>
  import('./components/MigrationAndCompatView').then((m) => ({
    default: m.MigrationAndCompatView,
  })),
);
const SystemView = lazy(() =>
  import('./components/SystemView').then((m) => ({ default: m.SystemView })),
);
const WantedView = lazy(() =>
  import('./components/WantedView').then((m) => ({ default: m.WantedView })),
);
const UsersView = lazy(() =>
  import('./components/UsersView').then((m) => ({ default: m.UsersView })),
);
const RolesView = lazy(() =>
  import('./components/RolesView').then((m) => ({ default: m.RolesView })),
);
const GeneralSettingsView = lazy(() =>
  import('./components/GeneralSettingsView').then((m) => ({ default: m.GeneralSettingsView })),
);
const CleanerrView = lazy(() =>
  import('./components/CleanerrView').then((m) => ({ default: m.CleanerrView })),
);

const ROUTE_TITLES: Record<string, string> = {
  '/library': 'Library',
  '/library/add': 'Add New Media',
  '/library/requests': 'Requests',
  '/calendar': 'Calendar',
  '/wanted/missing': 'Wanted: Missing',
  '/wanted/cutoff': 'Wanted: Cutoff Unmet',
  '/activity/queue': 'Activity Queue',
  '/activity/history': 'History',
  '/activity/blocklist': 'Blocklist',
  '/activity/manual-import': 'Manual Import',
  '/activity/parser': 'Release Parser',
  '/activity/quarantine': 'Quarantine',
  '/settings': 'Settings',
  '/settings/general': 'General Settings',
  '/settings/media': 'Media Management',
  '/settings/profiles': 'Profiles',
  '/settings/indexers': 'Indexers',
  '/settings/downloaders': 'Download Clients',
  '/settings/connect': 'Connect',
  '/settings/import-lists': 'Import Lists',
  '/settings/metadata': 'Metadata',
  '/settings/migration': 'Import / Migration',
  '/settings/users': 'Users',
  '/settings/roles': 'Roles',
  '/system/status': 'System Status',
  '/system/backup': 'Backup',
  '/system/security': 'Security',
  '/system/events': 'System Events',
  '/cleanerr': 'Cleanerr Overview',
  '/cleanerr/studio': 'Cleanerr Studio',
  '/cleanerr/queue': 'Leaving Soon Queue',
  '/cleanerr/media': 'Cleanerr Media Catalog',
  '/cleanerr/exclusions': 'Cleanerr Exclusions',
  '/cleanerr/discord': 'Cleanerr Discord & Settings',
};

interface SidebarGroup {
  id: string;
  label: string;
  icon: LucideIcon;
  defaultPath: string;
  children?: Array<{
    path: string;
    label: string;
  }>;
}

function ViewLoadingFallback() {
  return (
    <div
      role="progressbar"
      aria-label="Loading view"
      className="flex-1 py-16 flex items-center justify-center text-[#909293] text-[14px]"
    >
      Loading…
    </div>
  );
}

interface WorkDetailRouteProps {
  onSearchInAddNew: (q: string) => void;
  canWriteCatalog: boolean;
  canRequestCatalog: boolean;
  canApproveCatalog: boolean;
  canSearchIndexers: boolean;
  canReadProfiles: boolean;
}

function WorkDetailRoute({
  onSearchInAddNew,
  canWriteCatalog,
  canRequestCatalog,
  canApproveCatalog,
  canSearchIndexers,
  canReadProfiles,
}: WorkDetailRouteProps) {
  const { workId } = useParams<{ workId: string }>();
  const location = useLocation();
  const navigate = useNavigate();
  const parsedId = Number(workId);

  if (!Number.isFinite(parsedId) || parsedId <= 0) {
    return <Navigate to="/library" replace />;
  }

  const routeState = location.state as { fromCatalog?: boolean; returnTo?: string } | null;

  function returnToLibrary() {
    if (routeState?.fromCatalog) {
      navigate(-1);
      return;
    }
    navigate(buildSavedCatalogLibraryPath());
  }

  return (
    <CatalogView
      section="catalog"
      focusedWorkId={parsedId}
      onClearFocusedWork={returnToLibrary}
      onSelectWorkAndNavigate={(id) => navigate(`/works/${id}`)}
      onSearchInAddNew={onSearchInAddNew}
      onNavigate={(s) => {
        if (s === 'add') navigate('/library/add');
        if (s === 'catalog') returnToLibrary();
      }}
      canWriteCatalog={canWriteCatalog}
      canRequest={canRequestCatalog}
      canApprove={canApproveCatalog}
      canSearchIndexers={canSearchIndexers}
      canReadProfiles={canReadProfiles}
    />
  );
}

export function App() {
  const qc = useQueryClient();
  const location = useLocation();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();

  const [sidebarOpenMobile, setSidebarOpenMobile] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const [catalogResetNonce, setCatalogResetNonce] = useState<number>(0);
  const [pendingAddSearch, setPendingAddSearch] = useState<string>('');
  const [externalCatalogFilter, setExternalCatalogFilter] = useState<{
    text: string;
    openWorkId?: number | null;
    nonce: number;
  }>({
    text: '',
    openWorkId: null,
    nonce: 0,
  });

  const [hasSessionHint, setHasSessionHint] = useState<boolean>(() => {
    if (typeof window === 'undefined') return false;
    return window.localStorage?.getItem?.('managerr.session.active') === '1';
  });

  const [isPaletteOpen, setIsPaletteOpen] = useState(false);

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        setIsPaletteOpen((prev) => !prev);
      }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, []);

  const statusQuery = useQuery({
    queryKey: ['auth-status'],
    queryFn: () => apiFetch<AuthStatus>('/api/v1/auth/status'),
  });

  const providersThemeQuery = useQuery({
    queryKey: ['auth-providers-theme'],
    queryFn: () => apiFetch<{ ui?: { defaultTheme?: 'system' | 'dark' | 'oled' | 'light'; accentColor?: string; colorScheme?: string; customTheme?: import('./lib/theme').ManagerrThemeExportV1 | null } }>('/api/v1/auth/providers'),
    staleTime: 60_000,
  });

  useEffect(() => {
    const ui = providersThemeQuery.data?.ui;
    if (ui && typeof ui === 'object') {
      syncServerThemeIfUnset({
        defaultTheme: ui.defaultTheme,
        accentColor: ui.accentColor,
        colorScheme: ui.colorScheme,
        customTheme: ui.customTheme ?? undefined,
      });
    }
  }, [providersThemeQuery.data]);

  const shouldCheckSession = Boolean(statusQuery.data?.isClaimed) || hasSessionHint;

  const meQuery = useQuery({
    queryKey: ['me'],
    queryFn: async () => {
      try {
        const me = await apiFetch<MeResponse>('/api/v1/auth/me');
        setCsrfToken(me.csrfToken);
        if (typeof window !== 'undefined') {
          window.localStorage?.setItem?.('managerr.session.active', '1');
        }
        return me;
      } catch (err) {
        if (typeof window !== 'undefined') {
          window.localStorage?.removeItem?.('managerr.session.active');
        }
        setHasSessionHint(false);
        throw err;
      }
    },
    enabled: statusQuery.isSuccess && shouldCheckSession,
    retry: false,
  });

  useRealtimeStream({ enabled: Boolean(meQuery.data?.user) });

  const scopes = useMemo(() => new Set(meQuery.data?.scopes ?? []), [meQuery.data?.scopes]);
  const canReadCatalog = scopes.has('catalog:read');
  const canWriteCatalog = scopes.has('catalog:write');

  const catalogWorksQuery = useQuery({
    queryKey: ['catalog-works'],
    queryFn: () => apiFetch<CatalogSearchWorkSummary[]>('/api/v1/catalog/works'),
    enabled: Boolean(meQuery.data?.user && canReadCatalog),
  });

  const healthPingQuery = useQuery({
    queryKey: ['system-health-ping'],
    queryFn: async () => {
      try {
        return await apiFetch<SystemHealthPingResponse>('/api/v1/system/health');
      } catch {
        return { status: 'healthy', version: '0.2.0' } as SystemHealthPingResponse;
      }
    },
    enabled: Boolean(meQuery.data?.user),
  });

  const matchingCatalogWorks = useMemo(() => {
    const q = searchQuery.trim();
    if (!q) return [];
    return (catalogWorksQuery.data ?? []).filter((w) => matchesCatalogWorkQuery(w, q));
  }, [catalogWorksQuery.data, searchQuery]);

  const logoutMutation = useMutation({
    mutationFn: () => apiFetch<void>('/api/v1/auth/logout', { method: 'POST' }),
    onSuccess: () => {
      setCsrfToken(null);
      if (typeof window !== 'undefined') {
        window.localStorage?.removeItem?.('managerr.session.active');
      }
      setHasSessionHint(false);
      qc.clear();
      void statusQuery.refetch();
    },
  });

  const canRequestCatalog = scopes.has('catalog:request');
  const canApproveCatalog = scopes.has('catalog:auto_approve');
  const canReadProfiles = scopes.has('profiles:read');
  const canWriteProfiles = scopes.has('profiles:write');
  const canReadVerification = scopes.has('langverify:read');
  const canReviewQuarantine = scopes.has('quarantine:review');
  const canReadIndexers = scopes.has('indexers:read');
  const canWriteIndexers = scopes.has('indexers:write');
  const canSearchIndexers = scopes.has('indexers:search');
  const canReadDownloaders = scopes.has('downloaders:read');
  const canWriteDownloaders = scopes.has('downloaders:write');
  const canReadQueue = scopes.has('queue:read');
  const canManageQueue = scopes.has('queue:manage');
  const canReadMigration = scopes.has('migration:read');
  const canExecuteMigration = scopes.has('migration:execute');
  const canReadUsers = scopes.has('users:read');
  const canManageUsers = scopes.has('users:manage');
  const canManageRoles = scopes.has('roles:manage');
  const canSystemAdmin = scopes.has('system:admin');
  const canReadAudit = scopes.has('audit:read');
  const canReadCleanerr = scopes.has('cleanerr:read');

  const navGroups = useMemo<SidebarGroup[]>(() => {
    const groups: SidebarGroup[] = [];

    if (canReadCatalog) {
      groups.push({
        id: 'library',
        label: 'Library',
        icon: Film,
        defaultPath: '/library',
        children: [
          { path: '/library', label: 'Catalog' },
          ...(canWriteCatalog ? [{ path: '/library/add', label: 'Add New' }] : []),
          { path: '/library/requests', label: 'Requests' },
        ],
      });

      groups.push({
        id: 'calendar',
        label: 'Calendar',
        icon: Calendar,
        defaultPath: '/calendar',
      });

      groups.push({
        id: 'wanted',
        label: 'Wanted',
        icon: AlertCircle,
        defaultPath: '/wanted/missing',
        children: [
          { path: '/wanted/missing', label: 'Missing' },
          { path: '/wanted/cutoff', label: 'Cutoff Unmet' },
        ],
      });
    }

    const activityChildren: Array<{ path: string; label: string }> = [];

    if (canReadQueue || canReadDownloaders) {
      activityChildren.push({ path: '/activity/queue', label: 'Queue' });
      activityChildren.push({ path: '/activity/history', label: 'History' });
      activityChildren.push({ path: '/activity/blocklist', label: 'Blocklist' });
      activityChildren.push({ path: '/activity/manual-import', label: 'Manual Import' });
    }

    activityChildren.push({ path: '/activity/parser', label: 'Release Parser' });

    if (canReviewQuarantine || canReadVerification) {
      activityChildren.push({ path: '/activity/quarantine', label: 'Quarantine' });
    }
    groups.push({
      id: 'activity',
      label: 'Activity',
      icon: Clock,
      defaultPath: activityChildren[0].path,
      children: activityChildren,
    });

    const settingsChildren: Array<{ path: string; label: string }> = [];
    if (canSystemAdmin) settingsChildren.push({ path: '/settings/general', label: 'General' });
    if (canReadCatalog) settingsChildren.push({ path: '/settings/media', label: 'Media Management' });
    if (canReadProfiles) settingsChildren.push({ path: '/settings/profiles', label: 'Profiles' });
    if (canReadIndexers) settingsChildren.push({ path: '/settings/indexers', label: 'Indexers' });
    if (canReadDownloaders) settingsChildren.push({ path: '/settings/downloaders', label: 'Download Clients' });
    if (canReadCatalog) settingsChildren.push({ path: '/settings/connect', label: 'Connect' });
    if (canReadCatalog) settingsChildren.push({ path: '/settings/import-lists', label: 'Import Lists' });
    if (canReadCatalog) settingsChildren.push({ path: '/settings/metadata', label: 'Metadata' });
    if (canReadMigration) settingsChildren.push({ path: '/settings/migration', label: 'Import / Migration' });
    if (canReadUsers) {
      settingsChildren.push({ path: '/settings/users', label: 'Users' });
      settingsChildren.push({ path: '/settings/roles', label: 'Roles' });
    }

    if (settingsChildren.length > 0) {
      groups.push({
        id: 'settings',
        label: 'Settings',
        icon: Settings,
        defaultPath: '/settings',
        children: settingsChildren,
      });
    }

    groups.push({
      id: 'system',
      label: 'System',
      icon: Server,
      defaultPath: '/system/status',
      children: [
        { path: '/system/status', label: 'Status' },
        { path: '/system/backup', label: 'Backup' },
        { path: '/system/security', label: 'Security' },
        ...(canReadAudit ? [{ path: '/system/events', label: 'Events' }] : []),
      ],
    });

    if (canReadCleanerr) {
      groups.push({
        id: 'cleanerr',
        label: 'Cleanerr',
        icon: Sparkles,
        defaultPath: '/cleanerr',
        children: [
          { path: '/cleanerr', label: 'Overview' },
          { path: '/cleanerr/studio', label: 'Studio' },
          { path: '/cleanerr/queue', label: 'Leaving Soon' },
          { path: '/cleanerr/media', label: 'Media Catalog' },
          { path: '/cleanerr/exclusions', label: 'Exclusions' },
          { path: '/cleanerr/discord', label: 'Discord & Settings' },
        ],
      });
    }

    return groups;
  }, [
    canReadAudit,
    canReadCatalog,
    canReadCleanerr,
    canReadDownloaders,
    canReadIndexers,
    canReadMigration,
    canReadProfiles,
    canReadQueue,
    canReadUsers,
    canReadVerification,
    canReviewQuarantine,
    canSystemAdmin,
    canWriteCatalog,
  ]);

  const pathname = location.pathname;
  const isWorkDetailRoute = pathname.startsWith('/works/');

  const currentPageTitle = useMemo(() => {
    if (!meQuery.data?.user && !statusQuery.isLoading && !(shouldCheckSession && meQuery.isPending)) {
      return 'Sign In';
    }
    if (isWorkDetailRoute) {
      const idStr = pathname.slice('/works/'.length).split('/')[0];
      const wid = Number(idStr);
      const matched = (catalogWorksQuery.data ?? []).find((w) => w.id === wid);
      return matched ? matched.title : 'Media Details';
    }
    return ROUTE_TITLES[pathname] ?? 'Library';
  }, [
    catalogWorksQuery.data,
    isWorkDetailRoute,
    meQuery.data?.user,
    meQuery.isPending,
    pathname,
    shouldCheckSession,
    statusQuery.isLoading,
  ]);

  useEffect(() => {
    document.title = `${currentPageTitle} | Managerr`;
  }, [currentPageTitle]);

  if (statusQuery.isLoading || (shouldCheckSession && meQuery.isPending)) {
    return (
      <div
        role="progressbar"
        aria-label="Loading application"
        className="min-h-screen flex items-center justify-center bg-arr-page text-[#909293] text-[14px]"
      >
        Loading Managerr…
      </div>
    );
  }

  if (meQuery.isError || !meQuery.data?.user) {
    return (
      <LoginView
        status={statusQuery.data}
        onAuthenticated={() => {
          setHasSessionHint(true);
          void statusQuery.refetch();
          void meQuery.refetch();
        }}
      />
    );
  }

  const user = meQuery.data.user;
  const fallbackPath = canReadCatalog ? '/library' : '/system/status';

  function isGroupActive(g: SidebarGroup): boolean {
    if (g.id === 'library' && isWorkDetailRoute) return true;
    if (g.defaultPath === pathname) return true;
    return Boolean(g.children?.some((c) => c.path === pathname));
  }

  function resetLibraryAndCloseMobile() {
    setCatalogResetNonce((n) => n + 1);
    setExternalCatalogFilter({ text: '', nonce: 0 });
    setSidebarOpenMobile(false);
  }

  function openWorkInCatalog(workId: number) {
    setSidebarOpenMobile(false);
    const fromCatalog = pathname === '/library';
    if (fromCatalog && typeof window !== 'undefined') {
      saveCatalogScrollY(window.scrollY);
    }
    navigate(`/works/${workId}`, {
      state: fromCatalog
        ? { fromCatalog: true, returnTo: `${pathname}${location.search}` }
        : undefined,
    });
  }

  function handleSearchInAddNew(q: string) {
    setPendingAddSearch(q);
    setSidebarOpenMobile(false);
    navigate('/library/add');
  }

  const settingsOverviewItems: Array<{ path: string; title: string; summary: string; show: boolean }> = [
    {
      path: '/settings/general',
      title: 'General',
      summary: 'Host identity, authentication mode, and the instance API key for Sonarr, Radarr, Seerr, and third-party apps',
      show: canSystemAdmin,
    },
    {
      path: '/settings/media',
      title: 'Media Management',
      summary: 'Root folders, Plex Media Server library section bindings, and hardlink naming',
      show: canReadCatalog,
    },
    {
      path: '/settings/profiles',
      title: 'Profiles',
      summary: 'Quality profiles, dual-cutoff language upgrade policies, TRaSH Custom Formats, and decision simulator',
      show: canReadProfiles,
    },
    {
      path: '/settings/indexers',
      title: 'Indexers',
      summary: 'Newznab and Torznab indexers with single-pass RSS sync',
      show: canReadIndexers,
    },
    {
      path: '/settings/downloaders',
      title: 'Download Clients',
      summary: 'Torrent and Usenet download clients, category routing, and stalled queue handling',
      show: canReadDownloaders,
    },
    {
      path: '/settings/connect',
      title: 'Connect',
      summary: 'Notification providers: Discord, Telegram, Gotify, ntfy, Apprise, Webhook, Plex, and Jellyfin',
      show: canReadCatalog,
    },
    {
      path: '/settings/import-lists',
      title: 'Import Lists & Exclusions',
      summary: 'Automated list imports: TMDB, AniList, Trakt, StevenLu, OpenLibrary, and Custom JSON with exclusions',
      show: canReadCatalog,
    },
    {
      path: '/settings/metadata',
      title: 'Metadata',
      summary: 'TMDB, TVDB, MusicBrainz, OpenLibrary, AniList, Hardcover, Audnexus, and ID crosswalk',
      show: canReadCatalog,
    },
    {
      path: '/settings/migration',
      title: 'Import / Migration',
      summary: 'Zero-touch import from Sonarr, Radarr, Lidarr, Readarr, Prowlarr, and /compat/* API shims',
      show: canReadMigration,
    },
    {
      path: '/settings/users',
      title: 'Users',
      summary: 'Plex Server Owner, Home Users, and shared PMS accounts',
      show: canReadUsers,
    },
    {
      path: '/settings/roles',
      title: 'Roles',
      summary: 'Preset and custom RBAC roles, and request quotas',
      show: canReadUsers,
    },
  ];

  return (
    <div className="min-h-screen flex flex-col bg-arr-page text-[#e1e2e3]">
      {/* Zone 1: Classic 60px Servarr Top Header */}
      <header
        className="h-[60px] border-b border-arr-header-border flex items-center pr-4 lg:pr-5 shrink-0 z-30"
        style={{ background: 'linear-gradient(90deg, var(--arr-header-bg) 0%, var(--arr-header-bg-end) 100%)' }}
      >
        <div className="flex items-center justify-between lg:justify-start w-auto lg:w-[210px] pl-4 lg:pl-5 pr-3 shrink-0">
          <button
            type="button"
            onClick={() => setSidebarOpenMobile((v) => !v)}
            className="lg:hidden mr-2.5 p-1.5 text-[#cccccc] hover:text-white cursor-pointer"
            aria-label="Toggle sidebar menu"
            aria-expanded={sidebarOpenMobile}
          >
            {sidebarOpenMobile ? <X className="h-5 w-5" /> : <Menu className="h-5 w-5" />}
          </button>

          <Link
            to={fallbackPath}
            onClick={resetLibraryAndCloseMobile}
            className="inline-flex items-center gap-2.5 cursor-pointer text-left"
          >
            <ManagerrLogo size={26} />
            <span className="text-[16px] font-semibold tracking-tight text-[#e1e2e3] hidden sm:inline">
              Managerr
            </span>
          </Link>
        </div>

        {/* Classic Sonarr / Radarr Header Quick Search */}
        <div className="relative flex-1 max-w-[340px] ml-1 lg:ml-3">
          <div className="flex items-center border-b border-white/20 focus-within:border-arr-accent transition-colors">
            <Search className="h-4 w-4 text-[#909293] shrink-0 mr-2" />
            <input
              aria-label="Search library"
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Escape') {
                  setSearchQuery('');
                  (e.target as HTMLInputElement).blur();
                } else if (e.key === 'Enter') {
                  const q = searchQuery.trim();
                  if (q.length >= 1) {
                    e.preventDefault();
                    setSearchQuery('');
                    (e.target as HTMLInputElement).blur();

                    if (matchingCatalogWorks.length === 0) {
                      if (canWriteCatalog) {
                        handleSearchInAddNew(q);
                      } else {
                        setExternalCatalogFilter((prev) => ({ text: q, openWorkId: null, nonce: prev.nonce + 1 }));
                        navigate(`/library?filter=${encodeURIComponent(q)}`);
                      }
                    } else if (matchingCatalogWorks.length === 1) {
                      const hitId = matchingCatalogWorks[0].id;
                      openWorkInCatalog(hitId);
                    } else {
                      setExternalCatalogFilter((prev) => ({ text: q, openWorkId: null, nonce: prev.nonce + 1 }));
                      navigate(`/library?filter=${encodeURIComponent(q)}`);
                    }
                  }
                }
              }}
              placeholder="Search"
              className="w-full bg-transparent py-1.5 text-[13px] text-[#eeeeee] placeholder-[#888888] focus:outline-none"
            />
            {searchQuery && (
              <button
                type="button"
                aria-label="Clear search"
                onClick={() => setSearchQuery('')}
                className="text-[#888888] hover:text-[#eeeeee] p-0.5 cursor-pointer"
              >
                <X className="h-3.5 w-3.5" />
              </button>
            )}
          </div>

          {searchQuery.trim().length >= 1 && (
            <div className="absolute left-0 right-0 top-[38px] bg-arr-card border border-arr-card-border rounded-[4px] shadow-[0_6px_18px_rgba(0,0,0,0.75)] z-50 overflow-hidden">
              {matchingCatalogWorks.length === 0 ? (
                <div className="px-3 py-2 text-[12px] text-[#909293] border-b border-arr-card-border/60">
                  No library items match &ldquo;{searchQuery}&rdquo;
                </div>
              ) : (
                matchingCatalogWorks.slice(0, 8).map((hit) => (
                  <button
                    key={hit.id}
                    type="button"
                    onClick={() => {
                      setSearchQuery('');
                      openWorkInCatalog(hit.id);
                    }}
                    className="w-full px-3 py-2 text-left flex items-center justify-between hover:bg-arr-sidebar-active text-[13px] border-b border-arr-card-border/60 cursor-pointer"
                  >
                    <span className="text-[#eeeeee] truncate">
                      {hit.title} {hit.year ? `(${hit.year})` : ''}
                    </span>
                    <span className="text-[10px] uppercase font-semibold px-1.5 py-0.5 rounded-[2px] bg-arr-sidebar text-arr-accent ml-2">
                      {hit.workKind ?? hit.mediaType ?? 'Media'}
                    </span>
                  </button>
                ))
              )}

              {canWriteCatalog && (
                <button
                  type="button"
                  onClick={() => {
                    const q = searchQuery.trim();
                    setSearchQuery('');
                    handleSearchInAddNew(q);
                  }}
                  className="w-full px-3 py-2 text-left flex items-center justify-between bg-arr-sidebar hover:bg-arr-card text-[12px] text-arr-accent font-medium cursor-pointer"
                >
                  <span className="truncate">+ Search &ldquo;{searchQuery.trim()}&rdquo; in Add New</span>
                  <span className="text-[10px] uppercase text-[#909293] ml-2">SkyHook / *arr</span>
                </button>
              )}
            </div>
          )}
        </div>

        {/* Right Header Controls */}
        <div className="ml-auto flex items-center gap-3 text-[12px]">
          <button
            type="button"
            title="Open Command Palette (Ctrl+K / Cmd+K)"
            aria-label="Open command palette"
            onClick={() => setIsPaletteOpen(true)}
            className="hidden sm:inline-flex items-center gap-1.5 px-2.5 py-1 bg-arr-sidebar hover:bg-arr-card border border-arr-card-border rounded-[3px] text-[#909293] hover:text-[#e1e2e3] transition-colors cursor-pointer text-[11px]"
          >
            <Search className="h-3 w-3 text-arr-accent" />
            <span>Search</span>
            <kbd className="font-mono bg-arr-card px-1 rounded border border-arr-card-border text-[10px]">
              ⌘K
            </kbd>
          </button>

          {statusQuery.data?.boundServerName && (
            <span className="hidden md:inline-flex items-center gap-1.5 px-2.5 py-1 bg-arr-sidebar border border-arr-card-border rounded-[3px] text-[#cccccc]">
              <span className="h-2 w-2 rounded-full bg-[#27c24c]" />
              {statusQuery.data.boundServerName}
            </span>
          )}

          <Link
            to="/system/security"
            title="User Preferences & API Keys"
            onClick={() => setSidebarOpenMobile(false)}
            className="inline-flex items-center gap-1.5 px-2 py-1 text-[#cccccc] hover:text-arr-accent transition-colors cursor-pointer"
          >
            <User className="h-4 w-4" />
            <span className="hidden sm:inline font-medium">{user.username}</span>
          </Link>

          <button
            type="button"
            title="Sign Out"
            aria-label="Sign out"
            onClick={() => logoutMutation.mutate()}
            className="p-1.5 text-[#909293] hover:text-[#f05050] transition-colors cursor-pointer"
          >
            <LogOut className="h-4 w-4" />
          </button>
        </div>
      </header>

      <div className="flex flex-1 min-h-0 relative">
        {/* Zone 2: Classic 210px Servarr Tree Sidebar */}
        <aside
          className={clsx(
            'w-[210px] bg-arr-sidebar border-r border-arr-sidebar-border flex flex-col justify-between shrink-0 select-none z-20',
            sidebarOpenMobile ? 'fixed inset-y-[60px] left-0 shadow-2xl' : 'hidden lg:flex',
          )}
        >
          <nav aria-label="Primary" className="py-1 overflow-y-auto">
            {navGroups.map((group) => {
              const Icon = group.icon;
              const active = isGroupActive(group);
              return (
                <div
                  key={group.id}
                  className={clsx(
                    'border-l-[3px] transition-colors',
                    active ? 'border-l-arr-accent bg-arr-sidebar-active' : 'border-l-transparent',
                  )}
                >
                  <NavLink
                    to={group.defaultPath}
                    end
                    onClick={() => {
                      if (group.defaultPath === '/library') {
                        resetLibraryAndCloseMobile();
                      } else {
                        setSidebarOpenMobile(false);
                      }
                    }}
                    className={({ isActive }) =>
                      clsx(
                        'w-full flex items-center gap-2.5 px-5 py-3 text-[14px] text-left transition-colors cursor-pointer',
                        isActive && !group.children
                          ? 'text-arr-accent font-medium'
                          : active
                            ? 'text-[#eeeeee] font-medium'
                            : 'text-[#e1e2e3] hover:text-arr-accent',
                      )
                    }
                  >
                    <Icon className="h-[17px] w-[17px] shrink-0" />
                    <span>{group.label}</span>
                  </NavLink>

                  {active && group.children && (
                    <div className="pb-1.5">
                      {group.children.map((child) => (
                        <NavLink
                          key={child.path}
                          to={child.path}
                          end
                          onClick={() => {
                            if (child.path === '/library') {
                              resetLibraryAndCloseMobile();
                            } else {
                              setSidebarOpenMobile(false);
                            }
                          }}
                          className={({ isActive }) =>
                            clsx(
                              'w-full block text-left pl-[46px] pr-4 py-[7px] text-[13px] transition-colors cursor-pointer',
                              isActive
                                ? 'text-arr-accent font-medium'
                                : 'text-[#cccccc] hover:text-arr-accent',
                            )
                          }
                        >
                          {child.label}
                        </NavLink>
                      ))}
                    </div>
                  )}
                </div>
              );
            })}
          </nav>

          <div className="px-5 py-3 border-t border-arr-sidebar-border text-[11px] text-[#888888] flex items-center justify-between">
            <span className="inline-flex items-center gap-1.5">
              <span>
                Managerr{' '}
                {healthPingQuery.data?.version && healthPingQuery.data.version !== '0.1.0-phase1'
                  ? healthPingQuery.data.version
                  : '0.2.0'}
              </span>
              {healthPingQuery.data?.gitCommit && (
                <span className="font-mono text-[10px] px-1 py-0.2 rounded bg-arr-card text-[#aaaaaa] border border-arr-card-border">
                  {healthPingQuery.data.gitCommit.slice(0, 7)}
                </span>
              )}
            </span>
            <span className="inline-flex items-center gap-1 text-[#27c24c]">
              <span className="h-2 w-2 rounded-full bg-[#27c24c]" /> SQLite
            </span>
          </div>
        </aside>

        {/* Zone 3: Content Area (Heading + PageToolbar + PageContentBody) */}
        <main className="flex-1 flex flex-col min-w-0 bg-arr-page">
          {!isWorkDetailRoute && (
            <div className="px-5 lg:px-7 py-2.5 bg-arr-heading border-b border-arr-card-border shrink-0">
              <h1 className="text-[16px] font-medium text-[#e1e2e3] tracking-tight">
                {currentPageTitle}
              </h1>
            </div>
          )}

          <Suspense fallback={<ViewLoadingFallback />}>
            <Routes>
              <Route path="/" element={<Navigate to={fallbackPath} replace />} />

              {/* Library & Deep-Linked Work Detail */}
              <Route
                path="/library"
                element={
                  canReadCatalog ? (
                    <CatalogView
                      section="catalog"
                      resetNonce={catalogResetNonce}
                      externalFilterRequest={externalCatalogFilter}
                      urlSearch={location.search}
                      onSyncUrlSearch={(nextSearch) =>
                        setSearchParams(
                          nextSearch.startsWith('?') ? nextSearch.slice(1) : nextSearch,
                          { replace: true },
                        )
                      }
                      urlFilter={searchParams.get('filter')}
                      urlHealth={searchParams.get('health')}
                      onClearUrlParams={() =>
                        setSearchParams((prev) => stripHealthAndLegacyFilterParams(prev), { replace: true })
                      }
                      focusedWorkId={null}
                      onSelectWorkAndNavigate={(id) => openWorkInCatalog(id)}
                      onClearFocusedWork={() => navigate('/library')}
                      onSearchInAddNew={(q) => handleSearchInAddNew(q)}
                      onNavigate={(s) => {
                        if (s === 'add') navigate('/library/add');
                        if (s === 'catalog') navigate('/library');
                      }}
                      canWriteCatalog={canWriteCatalog}
                      canRequest={canRequestCatalog}
                      canApprove={canApproveCatalog}
                      canSearchIndexers={canSearchIndexers}
                      canReadProfiles={canReadProfiles}
                    />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              <Route
                path="/library/add"
                element={
                  canReadCatalog ? (
                    <CatalogView
                      section="add"
                      initialAddSearchQuery={pendingAddSearch}
                      onConsumeInitialAddSearch={() => setPendingAddSearch('')}
                      onSelectWorkAndNavigate={(workId) => openWorkInCatalog(workId)}
                      onNavigate={(s) => {
                        if (s === 'catalog') navigate('/library');
                      }}
                      canWriteCatalog={canWriteCatalog}
                      canRequest={canRequestCatalog}
                      canApprove={canApproveCatalog}
                      canSearchIndexers={canSearchIndexers}
                      canReadProfiles={canReadProfiles}
                    />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              <Route
                path="/library/requests"
                element={
                  canReadCatalog ? (
                    <CatalogView
                      section="requests"
                      canWriteCatalog={canWriteCatalog}
                      canRequest={canRequestCatalog}
                      canApprove={canApproveCatalog}
                    />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              <Route
                path="/works/:workId"
                element={
                  canReadCatalog ? (
                    <WorkDetailRoute
                      onSearchInAddNew={handleSearchInAddNew}
                      canWriteCatalog={canWriteCatalog}
                      canRequestCatalog={canRequestCatalog}
                      canApproveCatalog={canApproveCatalog}
                      canSearchIndexers={canSearchIndexers}
                      canReadProfiles={canReadProfiles}
                    />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              {/* Calendar */}
              <Route
                path="/calendar"
                element={
                  canReadCatalog ? (
                    <CatalogView
                      section="calendar"
                      onSelectWorkAndNavigate={(workId) => openWorkInCatalog(workId)}
                      canWriteCatalog={canWriteCatalog}
                      canRequest={canRequestCatalog}
                      canApprove={canApproveCatalog}
                    />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              {/* Wanted */}
              <Route
                path="/wanted/missing"
                element={
                  canReadCatalog ? (
                    <WantedView
                      section="missing"
                      onOpenWork={(workId) => openWorkInCatalog(workId)}
                      canSearchIndexers={canSearchIndexers}
                      canWriteCatalog={canWriteCatalog}
                    />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              <Route
                path="/wanted/cutoff"
                element={
                  canReadCatalog ? (
                    <WantedView
                      section="cutoff"
                      onOpenWork={(workId) => openWorkInCatalog(workId)}
                      canSearchIndexers={canSearchIndexers}
                      canWriteCatalog={canWriteCatalog}
                    />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              {/* Activity */}
              <Route
                path="/activity/queue"
                element={
                  canReadQueue || canReadDownloaders ? (
                    <ActivityQueueAndHistoryView
                      section="queue"
                      canManageQueue={canManageQueue}
                      canReadQueue={canReadQueue || canReadDownloaders}
                      canWriteCatalog={canWriteCatalog}
                      onOpenWork={(workId) => openWorkInCatalog(workId)}
                    />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              <Route
                path="/activity/history"
                element={
                  canReadQueue || canReadDownloaders ? (
                    <ActivityQueueAndHistoryView
                      section="history"
                      canManageQueue={canManageQueue}
                      canReadQueue={canReadQueue || canReadDownloaders}
                      canWriteCatalog={canWriteCatalog}
                      onOpenWork={(workId) => openWorkInCatalog(workId)}
                    />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              <Route
                path="/activity/blocklist"
                element={
                  canReadQueue || canReadDownloaders ? (
                    <ActivityQueueAndHistoryView
                      section="blocklist"
                      canManageQueue={canManageQueue}
                      canReadQueue={canReadQueue || canReadDownloaders}
                      canWriteCatalog={canWriteCatalog}
                      onOpenWork={(workId) => openWorkInCatalog(workId)}
                    />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              <Route
                path="/activity/manual-import"
                element={
                  canReadQueue || canReadDownloaders ? (
                    <ActivityQueueAndHistoryView
                      section="manual-import"
                      canManageQueue={canManageQueue}
                      canReadQueue={canReadQueue || canReadDownloaders}
                      canWriteCatalog={canWriteCatalog}
                      onOpenWork={(workId) => openWorkInCatalog(workId)}
                    />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              <Route
                path="/activity/parser"
                element={
                  <AcquisitionView
                    section="parser"
                    canReadIndexers={canReadIndexers}
                    canWriteIndexers={canWriteIndexers}
                    canReadDownloaders={canReadDownloaders}
                    canWriteDownloaders={canWriteDownloaders}
                  />
                }
              />

              <Route
                path="/activity/quarantine"
                element={
                  canReviewQuarantine || canReadVerification ? (
                    <ProfilesAndQuarantineView
                      section="quarantine"
                      canReadProfiles={canReadProfiles}
                      canWriteProfiles={canWriteProfiles}
                      canReadVerification={canReadVerification}
                      canReviewQuarantine={canReviewQuarantine}
                    />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              {/* Settings */}
              <Route
                path="/settings"
                element={
                  <div className="flex flex-col flex-1">
                    <PageToolbar />
                    <PageContentBody>
                      <div className="max-w-4xl space-y-6">
                        {settingsOverviewItems
                          .filter((item) => item.show)
                          .map((item) => (
                            <div key={item.path}>
                              <Link
                                to={item.path}
                                className="block w-full text-left text-[20px] font-normal text-[#e1e2e3] hover:text-arr-accent border-b border-[#444444] pb-1 mb-1.5 transition-colors cursor-pointer"
                              >
                                {item.title}
                              </Link>
                              <p className="text-[13px] text-[#909293]">{item.summary}</p>
                            </div>
                          ))}
                      </div>
                    </PageContentBody>
                  </div>
                }
              />

              <Route
                path="/settings/general"
                element={canSystemAdmin ? <GeneralSettingsView /> : <Navigate to={fallbackPath} replace />}
              />

              <Route
                path="/settings/media"
                element={
                  canReadCatalog ? (
                    <CatalogView
                      section="media-management"
                      canWriteCatalog={canWriteCatalog}
                      canRequest={canRequestCatalog}
                      canApprove={canApproveCatalog}
                    />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              <Route
                path="/settings/profiles"
                element={
                  canReadProfiles ? (
                    <ProfilesAndQuarantineView
                      section="profiles"
                      canReadProfiles={canReadProfiles}
                      canWriteProfiles={canWriteProfiles}
                      canReadVerification={canReadVerification}
                      canReviewQuarantine={canReviewQuarantine}
                    />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              <Route
                path="/settings/indexers"
                element={
                  canReadIndexers ? (
                    <AcquisitionView
                      section="indexers"
                      canReadIndexers={canReadIndexers}
                      canWriteIndexers={canWriteIndexers}
                      canReadDownloaders={canReadDownloaders}
                      canWriteDownloaders={canWriteDownloaders}
                    />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              <Route
                path="/settings/downloaders"
                element={
                  canReadDownloaders ? (
                    <AcquisitionView
                      section="downloaders"
                      canReadIndexers={canReadIndexers}
                      canWriteIndexers={canWriteIndexers}
                      canReadDownloaders={canReadDownloaders}
                      canWriteDownloaders={canWriteDownloaders}
                    />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              <Route
                path="/settings/connect"
                element={
                  canReadCatalog ? (
                    <EcosystemConnectAndListsView section="connect" />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              <Route
                path="/settings/import-lists"
                element={
                  canReadCatalog ? (
                    <EcosystemConnectAndListsView section="import-lists" />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              <Route
                path="/settings/metadata"
                element={
                  canReadCatalog ? (
                    <CatalogView
                      section="metadata"
                      canWriteCatalog={canWriteCatalog}
                      canRequest={canRequestCatalog}
                      canApprove={canApproveCatalog}
                      canSystemAdmin={canSystemAdmin}
                    />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              <Route
                path="/settings/migration"
                element={
                  canReadMigration ? (
                    <MigrationAndCompatView canExecute={canExecuteMigration} />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              <Route
                path="/settings/users"
                element={
                  canReadUsers ? (
                    <UsersView canManageUsers={canManageUsers} />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              <Route
                path="/settings/roles"
                element={
                  canReadUsers ? (
                    <RolesView canManageRoles={canManageRoles} />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              {/* System */}
              <Route
                path="/system/status"
                element={
                  <SystemView
                    section="status"
                    availableScopes={meQuery.data.scopes}
                    canSystemAdmin={canSystemAdmin}
                    canReadAudit={canReadAudit}
                    user={user}
                    authStatus={statusQuery.data}
                    healthInfo={healthPingQuery.data}
                  />
                }
              />

              <Route
                path="/system/backup"
                element={
                  <SystemView
                    section="backup"
                    availableScopes={meQuery.data.scopes}
                    canSystemAdmin={canSystemAdmin}
                    canReadAudit={canReadAudit}
                    user={user}
                    authStatus={statusQuery.data}
                    healthInfo={healthPingQuery.data}
                  />
                }
              />

              <Route
                path="/system/security"
                element={
                  <SystemView
                    section="security"
                    availableScopes={meQuery.data.scopes}
                    canSystemAdmin={canSystemAdmin}
                    canReadAudit={canReadAudit}
                    user={user}
                    authStatus={statusQuery.data}
                    healthInfo={healthPingQuery.data}
                  />
                }
              />

              <Route
                path="/system/events"
                element={
                  canReadAudit ? (
                    <SystemView
                      section="events"
                      availableScopes={meQuery.data.scopes}
                      canSystemAdmin={canSystemAdmin}
                      canReadAudit={canReadAudit}
                      user={user}
                      authStatus={statusQuery.data}
                      healthInfo={healthPingQuery.data}
                    />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              {/* Cleanerr Lifecycle & Discord */}
              <Route
                path="/cleanerr"
                element={
                  canReadCleanerr ? (
                    <CleanerrView section="overview" />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />
              <Route
                path="/cleanerr/studio"
                element={
                  canReadCleanerr ? (
                    <CleanerrView section="studio" />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />
              <Route
                path="/cleanerr/queue"
                element={
                  canReadCleanerr ? (
                    <CleanerrView section="queue" />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />
              <Route
                path="/cleanerr/media"
                element={
                  canReadCleanerr ? (
                    <CleanerrView section="media" />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />
              <Route
                path="/cleanerr/exclusions"
                element={
                  canReadCleanerr ? (
                    <CleanerrView section="exclusions" />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />
              <Route
                path="/cleanerr/discord"
                element={
                  canReadCleanerr ? (
                    <CleanerrView section="discord" />
                  ) : (
                    <Navigate to={fallbackPath} replace />
                  )
                }
              />

              <Route path="*" element={<Navigate to={fallbackPath} replace />} />
            </Routes>
          </Suspense>
        </main>
      </div>

      <CommandPaletteModal
        isOpen={isPaletteOpen}
        onClose={() => setIsPaletteOpen(false)}
        canSystemAdmin={canSystemAdmin}
        canReadCleanerr={canReadCleanerr}
      />
    </div>
  );
}
