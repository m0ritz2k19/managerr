import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { App } from '../App';
import { CleanerrConfirmModal } from '../components/cleanerr/CleanerrConfirmModal';
import { CleanerrDagStudio } from '../components/cleanerr/CleanerrDagStudio';
import { CleanerrDiscordAndSettingsSection } from '../components/cleanerr/CleanerrDiscordAndSettingsSection';
import { CleanerrMediaAndExclusionsSection } from '../components/cleanerr/CleanerrMediaAndExclusionsSection';
import { CleanerrQueueSection } from '../components/cleanerr/CleanerrQueueSection';
import { CleanerrRuleBuilder } from '../components/cleanerr/CleanerrRuleBuilder';
import type {
  CleanerrNodeCatalogEntry,
  CleanerrRuleGroupSpec,
} from '../components/cleanerr/cleanerrTypes';

const CLEANERR_ADMIN_SCOPES = [
  'catalog:read',
  'catalog:write',
  'cleanerr:read',
  'cleanerr:write',
  'cleanerr:execute',
  'cleanerr:rescue',
  'cleanerr:exclude',
  'profiles:read',
  'system:admin',
];

function createTestQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
      mutations: {
        retry: false,
      },
    },
  });
}

function jsonResponse(data: unknown, status = 200) {
  return new Response(JSON.stringify(data), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function createCleanerrAppFetchMock() {
  return vi.fn(async (input: RequestInfo | URL) => {
    const rawUrl =
      typeof input === 'string'
        ? input
        : input instanceof URL
          ? input.toString()
          : input.url;
    const url = new URL(rawUrl, 'http://localhost');
    const path = url.pathname;

    if (path === '/api/v1/auth/status') {
      return jsonResponse({
        isClaimed: true,
        boundServerName: 'HomePlex',
        boundMachineId: 'machine-123',
        plexClientIdentifier: 'client-123',
        authMode: 'plex_primary_with_lan_breakglass',
      });
    }

    if (path === '/api/v1/auth/providers') {
      return jsonResponse({ ui: {} });
    }

    if (path === '/api/v1/auth/me') {
      return jsonResponse({
        user: {
          id: 1,
          plexUserId: '1001',
          plexUuid: 'uuid-1001',
          username: 'admin',
          email: 'admin@example.com',
          thumbUrl: null,
          accountType: 'owner',
          isHomeUser: false,
          isAdmin: true,
          isActive: true,
          roles: ['Owner'],
        },
        csrfToken: 'csrf-cleanerr-token',
        scopes: CLEANERR_ADMIN_SCOPES,
        isCookieSession: true,
        apiKeyPrefix: null,
      });
    }

    if (path === '/api/v1/system/health') {
      return jsonResponse({ status: 'healthy', version: '0.2.0' });
    }

    if (path === '/api/v1/catalog/works') {
      return jsonResponse([]);
    }

    if (path === '/api/v1/libraries') {
      return jsonResponse([
        { id: 1, name: 'Movies', mediaType: 'movies' },
        { id: 2, name: 'TV Shows', mediaType: 'tv' },
      ]);
    }

    if (path === '/api/v1/cleanerr/status') {
      return jsonResponse({
        module: 'cleanerr',
        defaultAccessPolicy: 'admin_only',
        canRead: true,
        canRescue: true,
        canExclude: true,
        canWriteWorkflows: true,
        canExecute: true,
        cleanerrExclusionLimit: null,
        gatewayConnected: true,
        gatewayBotUsername: 'CleanerrBot',
        stagedCount: 3,
      });
    }

    if (path === '/api/v1/cleanerr/stats') {
      return jsonResponse({
        totalMediaItems: 142,
        totalMediaSizeBytes: 500 * 1024 * 1024 * 1024,
        stagedCount: 3,
        stagedSizeBytes: 25 * 1024 * 1024 * 1024,
        stagedReclaimableBytes: 25 * 1024 * 1024 * 1024,
        activeExclusionsCount: 7,
        enabledWorkflowsCount: 2,
        rescuedTotalCount: 11,
        deletedTotalCount: 29,
        diskFreeGb: 420.5,
        diskFreePercent: 42,
        lastRunAt: '2026-11-19T12:00:00Z',
        discordConfigured: true,
        discordGatewayConnected: true,
        discordAuthMode: 'channel_members',
        libraryStorage: [
          {
            libraryId: 1,
            libraryTitle: '4K Movies',
            mediaType: 'movie',
            rootFolder: '/media/movies-4k',
            itemCount: 84,
            libraryBytes: 320 * 1024 * 1024 * 1024,
            driveFreeGb: 512.0,
            driveTotalGb: 1000,
            driveFreePercent: 51,
          },
          {
            libraryId: 2,
            libraryTitle: 'TV Archive',
            mediaType: 'show',
            rootFolder: '/media/tv',
            itemCount: 58,
            libraryBytes: 180 * 1024 * 1024 * 1024,
            driveFreeGb: 180,
            driveTotalGb: 1000,
            driveFreePercent: 18,
          },
        ],
        reclaimForecast: [
          {
            bucketKey: 'due_now',
            label: 'Due Now',
            itemCount: 1,
            reclaimableBytes: 10 * 1024 * 1024 * 1024,
          },
          {
            bucketKey: 'next_7d',
            label: 'Next 7 Days',
            itemCount: 2,
            reclaimableBytes: 15 * 1024 * 1024 * 1024,
          },
        ],
      });
    }

    return jsonResponse([]);
  });
}

describe('Cleanerr Frontend Components & Routing', () => {
  describe('CleanerrView Routing & Overview', () => {
    it('renders /cleanerr overview stats, per-library disk stats, reclaim forecast, and navigation links', async () => {
      const fetchMock = createCleanerrAppFetchMock();
      vi.stubGlobal('fetch', fetchMock);
      const qc = createTestQueryClient();

      render(
        <QueryClientProvider client={qc}>
          <MemoryRouter initialEntries={['/cleanerr']}>
            <App />
          </MemoryRouter>
        </QueryClientProvider>,
      );

      // Sub-navigation bar links
      const subNav = await screen.findByRole('navigation', {
        name: 'Cleanerr sections',
      });
      const expectedLinks = [
        { label: 'Overview', href: '/cleanerr' },
        { label: 'Studio', href: '/cleanerr/studio' },
        { label: 'Leaving Soon', href: '/cleanerr/queue' },
        { label: 'Media Catalog', href: '/cleanerr/media' },
        { label: 'Exclusions', href: '/cleanerr/exclusions' },
        { label: 'Discord & Settings', href: '/cleanerr/discord' },
      ];

      for (const item of expectedLinks) {
        const link = within(subNav).getByRole('link', {
          name: new RegExp(`^${item.label.replace('&', '&')}`),
        });
        expect(link).toHaveAttribute('href', item.href);
      }

      expect(
        within(subNav).getByRole('link', { name: 'Overview' }),
      ).toHaveAttribute('aria-current', 'page');

      // Primary sidebar nav also exposes Cleanerr child links
      const primaryNav = screen.getByRole('navigation', { name: 'Primary' });
      for (const item of expectedLinks) {
        const sidebarLink = within(primaryNav).getByRole('link', {
          name: item.label,
        });
        expect(sidebarLink).toHaveAttribute('href', item.href);
      }

      // Overview KPI stats
      expect(await screen.findByText('142')).toBeInTheDocument();
      expect(screen.getByText(/420\.5 GB/)).toBeInTheDocument();
      expect(screen.getByText('Gateway Connected')).toBeInTheDocument();

      // Reclaim Forecast
      const forecastList = await screen.findByRole('list', {
        name: 'Reclaim Forecast',
      });
      expect(within(forecastList).getByText('Due Now')).toBeInTheDocument();
      expect(within(forecastList).getByText('Next 7 Days')).toBeInTheDocument();
      expect(within(forecastList).getByText('10.00 GB')).toBeInTheDocument();
      expect(within(forecastList).getByText('15.00 GB')).toBeInTheDocument();

      // Per-Library Disk Stats
      expect(await screen.findByText('4K Movies')).toBeInTheDocument();
      expect(screen.getByText('TV Archive')).toBeInTheDocument();
      expect(screen.getByText('320.0 GB')).toBeInTheDocument();
      expect(screen.getByText('51% free')).toBeInTheDocument();
      expect(screen.getByText('18% free')).toBeInTheDocument();
    });
  });

  describe('CleanerrRuleBuilder', () => {
    it('adds sections and conditions, toggles property comparison, selects downgrade_profile, and excludes disallowed action modes', async () => {
      const user = userEvent.setup();
      const qc = createTestQueryClient();

      const initialRuleGroup: CleanerrRuleGroupSpec = {
        targetMediaType: 'movie',
        graceDays: 7,
        actionMode: 'delete_files_and_unmonitor',
        sections: [
          {
            sectionOperator: 'AND',
            title: 'Section 1',
            rules: [
              {
                operator: null,
                field: 'plex.lastViewedDaysAgo',
                comparator: 'greater_than',
                value: '90',
              },
            ],
          },
        ],
      };

      function RuleBuilderHarness() {
        const [spec, setSpec] = useState<CleanerrRuleGroupSpec>(initialRuleGroup);
        return (
          <CleanerrRuleBuilder
            ruleGroup={spec}
            onChange={setSpec}
            libraries={[{ id: 1, name: 'Movies', mediaType: 'movies' }]}
            profiles={[
              {
                id: 10,
                slug: 'hd-720p-compact',
                name: 'HD-720p Compact',
                mediaVertical: 'video',
                qualityCutoff: 'HDTV-720p',
                languagePolicyId: null,
              },
            ]}
            catalog={{
              fields: [],
              comparators: [],
              actionModes: [
                'delete_files_and_unmonitor',
                'downgrade_profile',
                'compress_av1',
                'tiered_compress_or_downgrade_before_delete',
              ],
            }}
          />
        );
      }

      render(
        <QueryClientProvider client={qc}>
          <RuleBuilderHarness />
        </QueryClientProvider>,
      );

      // Initial section & condition rendered
      expect(screen.getByText('Rule Evaluation Sections (1)')).toBeInTheDocument();
      expect(screen.getByText('Section 1')).toBeInTheDocument();
      expect(screen.getByText('(1 condition)')).toBeInTheDocument();

      // Add a second condition to Section 1
      await user.click(screen.getByRole('button', { name: /Add Rule/i }));
      expect(screen.getByText('(2 conditions)')).toBeInTheDocument();

      // Add a second section
      await user.click(screen.getByRole('button', { name: /Add Section/i }));
      expect(screen.getByText('Rule Evaluation Sections (2)')).toBeInTheDocument();
      expect(screen.getByText('Section 2')).toBeInTheDocument();

      // Toggle property comparison (secondField) on the first condition
      const togglePropertyButtons = screen.getAllByRole('button', {
        name: 'Switch to compare against another property',
      });
      await user.click(togglePropertyButtons[0]);

      const activeToggleBtn = screen.getByRole('button', {
        name: 'Switch to literal value comparison',
      });
      expect(activeToggleBtn).toHaveAttribute('aria-pressed', 'true');
      expect(screen.getByLabelText('Comparison target field')).toBeInTheDocument();

      // Target Media Type only exposes whole movies and full TV series (no season/episode)
      const targetMediaSelect = screen.getByLabelText('Target Media Type') as HTMLSelectElement;
      const mediaTypeValues = Array.from(targetMediaSelect.options).map((o) => o.value);
      expect(mediaTypeValues).toEqual(['movie', 'show']);
      expect(mediaTypeValues).not.toContain('season');
      expect(mediaTypeValues).not.toContain('episode');

      // Check Action Mode options: no compress_av1 or tiered_compress_or_downgrade_before_delete
      const actionModeSelect = screen.getByLabelText('Action Mode') as HTMLSelectElement;
      const optionValues = Array.from(actionModeSelect.options).map((o) => o.value);
      expect(optionValues).not.toContain('compress_av1');
      expect(optionValues).not.toContain('tiered_compress_or_downgrade_before_delete');
      expect(optionValues).toContain('downgrade_profile');

      // Select downgrade_profile and verify target downgrade quality profile selector appears
      await user.selectOptions(actionModeSelect, 'downgrade_profile');
      expect(actionModeSelect.value).toBe('downgrade_profile');
      expect(
        screen.getByLabelText('Target Downgrade Quality Profile'),
      ).toBeInTheDocument();
    });

    it('excludes tv.totalSeasons / Total Seasons from selectable rule builder fields', () => {
      const qc = createTestQueryClient();

      const ruleGroup: CleanerrRuleGroupSpec = {
        targetMediaType: 'show',
        graceDays: 7,
        actionMode: 'delete_files_and_unmonitor',
        sections: [
          {
            sectionOperator: 'AND',
            title: 'Section 1',
            rules: [
              {
                operator: null,
                field: 'plex.lastViewedDaysAgo',
                comparator: 'greater_than',
                value: '90',
                secondField: 'sonarr.sizeGb',
              },
            ],
          },
        ],
      };

      render(
        <QueryClientProvider client={qc}>
          <CleanerrRuleBuilder
            ruleGroup={ruleGroup}
            onChange={vi.fn()}
            libraries={[{ id: 2, name: 'TV Shows', mediaType: 'shows' }]}
            catalog={{
              fields: [
                {
                  key: 'plex.lastViewedDaysAgo',
                  label: 'Last Viewed (Days Ago)',
                  category: 'Plex',
                  valueType: 'number',
                  supportedMediaTypes: ['movie', 'show'],
                },
                {
                  key: 'tv.totalSeasons',
                  label: 'Total Seasons',
                  category: 'TV',
                  valueType: 'number',
                  supportedMediaTypes: ['show'],
                },
                {
                  key: 'tv.totalseasons',
                  label: 'Total Seasons',
                  category: 'TV',
                  valueType: 'number',
                  supportedMediaTypes: ['show'],
                },
              ],
              comparators: [],
              actionModes: ['delete_files_and_unmonitor'],
            }}
          />
        </QueryClientProvider>,
      );

      const fieldSelect = screen.getByLabelText('Condition field') as HTMLSelectElement;
      const fieldOptionValues = Array.from(fieldSelect.options).map((o) => o.value);
      const fieldOptionTexts = Array.from(fieldSelect.options).map((o) => o.textContent ?? '');
      expect(fieldOptionValues).toContain('plex.lastViewedDaysAgo');
      expect(fieldOptionValues).not.toContain('tv.totalSeasons');
      expect(fieldOptionValues).not.toContain('tv.totalseasons');
      expect(fieldOptionTexts.some((t) => t.includes('Total Seasons'))).toBe(false);

      const secondFieldSelect = screen.getByLabelText(
        'Comparison target field',
      ) as HTMLSelectElement;
      const secondFieldOptionValues = Array.from(secondFieldSelect.options).map((o) => o.value);
      expect(secondFieldOptionValues).not.toContain('tv.totalSeasons');
      expect(secondFieldOptionValues).not.toContain('tv.totalseasons');
    });
  });

  describe('CleanerrConfirmModal', () => {
    it('places Cancel with autoFocus before destructive confirm action and invokes callbacks', async () => {
      const user = userEvent.setup();
      const onConfirm = vi.fn();
      const onClose = vi.fn();

      render(
        <CleanerrConfirmModal
          title="Confirm Cleanup Execution"
          description="Are you sure you want to delete 4 media items?"
          confirmLabel="Delete Forever"
          confirmVariant="danger"
          onConfirm={onConfirm}
          onClose={onClose}
        />,
      );

      const dialog = document.querySelector('dialog[open]') as HTMLDialogElement | null;
      expect(dialog).not.toBeNull();

      const cancelBtn = screen.getByRole('button', { name: 'Cancel' });
      const confirmBtn = screen.getByRole('button', { name: 'Delete Forever' });

      expect(cancelBtn).toHaveFocus();
      const buttonsInDialog = within(dialog!).getAllByRole('button');
      const cancelIndex = buttonsInDialog.indexOf(cancelBtn);
      const confirmIndex = buttonsInDialog.indexOf(confirmBtn);
      expect(cancelIndex).toBeGreaterThan(-1);
      expect(confirmIndex).toBeGreaterThan(cancelIndex);

      await user.click(cancelBtn);
      expect(onClose).toHaveBeenCalledTimes(1);
      expect(onConfirm).not.toHaveBeenCalled();

      await user.click(confirmBtn);
      expect(onConfirm).toHaveBeenCalledTimes(1);
    });
  });

  describe('CleanerrMediaAndExclusionsSection', () => {
    it('renders only All, Movies, and Series filter tabs without Seasons or Episodes', async () => {
      const qc = createTestQueryClient();
      const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
        const rawUrl =
          typeof input === 'string'
            ? input
            : input instanceof URL
              ? input.toString()
              : input.url;
        const url = new URL(rawUrl, 'http://localhost');

        if (url.pathname === '/api/v1/cleanerr/media') {
          return jsonResponse({ items: [], total: 0, page: 1, pageSize: 50 });
        }
        return jsonResponse([]);
      });
      vi.stubGlobal('fetch', fetchMock);

      render(
        <QueryClientProvider client={qc}>
          <CleanerrMediaAndExclusionsSection
            status={{
              module: 'cleanerr',
              defaultAccessPolicy: 'admin_only',
              canRead: true,
              canRescue: true,
              canExclude: true,
              canWriteWorkflows: true,
              canExecute: true,
              cleanerrExclusionLimit: null,
            }}
          />
        </QueryClientProvider>,
      );

      const mediaTypeGroup = await screen.findByRole('group', {
        name: 'Media Type',
      });
      expect(within(mediaTypeGroup).getByRole('button', { name: /^All\b/i })).toBeInTheDocument();
      expect(within(mediaTypeGroup).getByRole('button', { name: /^Movies\b/i })).toBeInTheDocument();
      expect(within(mediaTypeGroup).getByRole('button', { name: /^Series\b/i })).toBeInTheDocument();
      expect(within(mediaTypeGroup).queryByRole('button', { name: /^Seasons\b/i })).not.toBeInTheDocument();
      expect(within(mediaTypeGroup).queryByRole('button', { name: /^Episodes\b/i })).not.toBeInTheDocument();
    });
  });

  describe('CleanerrDiscordAndSettingsSection', () => {
    it('initializes IDs empty, masks secrets with accessible toggle, exposes radio auth modes, and populates discovery dropdowns', async () => {
      const user = userEvent.setup();
      const qc = createTestQueryClient();

      const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
        const rawUrl =
          typeof input === 'string'
            ? input
            : input instanceof URL
              ? input.toString()
              : input.url;
        const url = new URL(rawUrl, 'http://localhost');

        if (url.pathname === '/api/v1/cleanerr/discord/settings') {
          return jsonResponse({
            webhookUrl: '',
            hasBotToken: false,
            maskedBotToken: '',
            channelId: '',
            applicationId: '',
            publicKeyHex: '',
            publicBaseUrl: '',
            gatewayPassiveShadowMode: false,
            gatewayConnected: false,
            guildId: '',
            authMode: 'channel_members',
            allowedRoleIds: [],
            webhookBotUsername: 'Cleanerr',
          });
        }

        if (url.pathname === '/api/v1/cleanerr/settings') {
          return jsonResponse({
            defaultGraceDays: 7,
            seerrUrl: '',
            hasSeerrApiKey: false,
            maskedSeerrApiKey: '',
            finalWarningHours: 24,
            defaultPostponeDays: 7,
            torrentDownloadsPath: '/data/downloads',
          });
        }

        if (url.pathname === '/api/v1/cleanerr/discord/discover') {
          return jsonResponse({
            ok: true,
            botUserId: 'bot-1',
            botUsername: 'CleanerrBot',
            guilds: [
              { id: 'guild-100', name: 'Media Guild', isOwner: true },
              { id: 'guild-200', name: 'Backup Guild', isOwner: false },
            ],
            channels: [
              { id: 'chan-10', guildId: 'guild-100', name: 'leaving-soon', type: 0 },
              { id: 'chan-20', guildId: 'guild-100', name: 'announcements', type: 5 },
              { id: 'chan-30', guildId: 'guild-100', name: 'voice-room', type: 2 },
            ],
          });
        }

        return jsonResponse({});
      });

      vi.stubGlobal('fetch', fetchMock);

      render(
        <QueryClientProvider client={qc}>
          <CleanerrDiscordAndSettingsSection
            status={{
              module: 'cleanerr',
              defaultAccessPolicy: 'admin_only',
              canRead: true,
              canRescue: true,
              canExclude: true,
              canWriteWorkflows: true,
              canExecute: true,
              cleanerrExclusionLimit: null,
            }}
          />
        </QueryClientProvider>,
      );

      // Verify Application ID, Public Key, Guild ID, Channel ID initialize to ""
      const appIdInput = (await screen.findByLabelText(
        'Application ID',
      )) as HTMLInputElement;
      const publicKeyInput = screen.getByLabelText(
        'Public Key (Hex)',
      ) as HTMLInputElement;
      const guildIdInput = screen.getByLabelText(
        'Server (Guild) ID',
      ) as HTMLInputElement;
      const channelIdInput = screen.getByLabelText(
        'Notification Channel ID',
      ) as HTMLInputElement;

      expect(appIdInput.value).toBe('');
      expect(publicKeyInput.value).toBe('');
      expect(guildIdInput.value).toBe('');
      expect(channelIdInput.value).toBe('');

      // Bot Token and Seerr API Key start masked and toggle cleanly
      const botTokenInput = screen.getByLabelText(
        'Discord Bot Token',
      ) as HTMLInputElement;
      const toggleBotTokenBtn = screen.getByRole('button', {
        name: 'Show bot token',
      });
      expect(botTokenInput.type).toBe('password');
      expect(toggleBotTokenBtn).toHaveAttribute('aria-pressed', 'false');

      await user.click(toggleBotTokenBtn);
      expect(botTokenInput.type).toBe('text');
      expect(
        screen.getByRole('button', { name: 'Hide bot token' }),
      ).toHaveAttribute('aria-pressed', 'true');

      const seerrKeyInput = screen.getByLabelText(
        'Seerr API Key',
      ) as HTMLInputElement;
      const toggleSeerrKeyBtn = screen.getByRole('button', {
        name: 'Show Seerr API key',
      });
      expect(seerrKeyInput.type).toBe('password');
      expect(toggleSeerrKeyBtn).toHaveAttribute('aria-pressed', 'false');

      await user.click(toggleSeerrKeyBtn);
      expect(seerrKeyInput.type).toBe('text');
      expect(
        screen.getByRole('button', { name: 'Hide Seerr API key' }),
      ).toHaveAttribute('aria-pressed', 'true');

      // Auth Mode uses accessible <input type="radio"> options
      const authModeFieldset = screen.getByRole('group', {
        name: 'Authorization Mode',
      });
      const radios = within(authModeFieldset).getAllByRole(
        'radio',
      ) as HTMLInputElement[];
      expect(radios.map((r) => r.value)).toEqual([
        'channel_members',
        'discord_roles',
        'linked_plex_rbac',
      ]);
      expect(radios[0].checked).toBe(true);

      // Click Discover Guilds & Channels -> populates Guild & Channel dropdowns
      await user.click(
        screen.getByRole('button', { name: /Discover Guilds & Channels/i }),
      );

      const guildDropdown = (await screen.findByLabelText(
        'Discovered Guild',
      )) as HTMLSelectElement;
      const channelDropdown = screen.getByLabelText(
        'Discovered Text Channel',
      ) as HTMLSelectElement;

      expect(
        within(guildDropdown).getByRole('option', {
          name: /Media Guild \(guild-100\)/,
        }),
      ).toBeInTheDocument();
      expect(
        within(guildDropdown).getByRole('option', {
          name: /Backup Guild \(guild-200\)/,
        }),
      ).toBeInTheDocument();

      // Only text/announcement channels (type 0 / 5) should populate
      expect(
        within(channelDropdown).getByRole('option', {
          name: /#leaving-soon \(chan-10\)/,
        }),
      ).toBeInTheDocument();
      expect(
        within(channelDropdown).getByRole('option', {
          name: /#announcements \(chan-20\)/,
        }),
      ).toBeInTheDocument();
      expect(
        within(channelDropdown).queryByRole('option', {
          name: /voice-room/,
        }),
      ).not.toBeInTheDocument();

      // Plex collection settings must not be rendered
      expect(
        screen.queryByText(/Sync Plex Collections/i),
      ).not.toBeInTheDocument();
      expect(
        screen.queryByLabelText(/Leaving Soon Collection Name/i),
      ).not.toBeInTheDocument();

      // Configurable Cleanerr & Discord fields exist and Default Grace Days allows 0
      const graceInput = screen.getByLabelText('Default Grace Days') as HTMLInputElement;
      expect(graceInput.min).toBe('0');
      expect(screen.getByLabelText('Default Postpone Days')).toBeInTheDocument();
      expect(screen.getByLabelText('Torrent Downloads Path')).toBeInTheDocument();
      expect(screen.getByLabelText('Webhook Bot Username')).toBeInTheDocument();
    });
  });

  describe('CleanerrDagStudio', () => {
    it('normalizes backend /node-catalog categories into palette category tabs', async () => {
      vi.stubGlobal(
        'ResizeObserver',
        class ResizeObserver {
          observe() {}
          unobserve() {}
          disconnect() {}
        },
      );
      const user = userEvent.setup();

      const backendCatalog: CleanerrNodeCatalogEntry[] = [
        {
          nodeType: 'trigger.schedule',
          category: 'Triggers',
          label: 'Schedule (Cron)',
          description: 'Runs workflow on a cron schedule.',
          color: '#38bdf8',
          defaultConfig: { cron: '0 3 * * *' },
        },
        {
          nodeType: 'source.plex_library',
          category: 'Sources',
          label: 'Plex Library',
          description: 'Loads synced Plex items.',
          color: '#6366f1',
          defaultConfig: { media_type: 'movie' },
        },
        {
          nodeType: 'logic.and',
          category: 'Logic & Filters',
          label: 'Logical AND',
          description: 'Intersection of upstream branches.',
          color: '#8b5cf6',
          defaultConfig: {},
        },
        {
          nodeType: 'filter.watch_state',
          category: 'Logic & Filters',
          label: 'Watch State',
          description: 'Filters by last watched age.',
          color: '#a855f7',
          defaultConfig: { last_watched_older_than_days: 90 },
        },
        {
          nodeType: 'filter.limit',
          category: 'Logic & Filters',
          label: 'Limit / Reclaim Budget',
          description: 'Caps item count or reclaimable GB.',
          color: '#a855f7',
          defaultConfig: { max_items: 50 },
        },
        {
          nodeType: 'queue.grace_period',
          category: 'Queue & Notify',
          label: 'Leaving Soon Grace Queue',
          description: 'Stages matched items for N days.',
          color: '#f59e0b',
          defaultConfig: { grace_days: 7 },
        },
        {
          nodeType: 'notify.discord',
          category: 'Queue & Notify',
          label: 'Discord Notification',
          description: 'Sends a Discord summary alert.',
          color: '#ec4899',
          defaultConfig: { grace_days: 7 },
        },
        {
          nodeType: 'action.managerr',
          category: 'Actions',
          label: 'Managerr Action',
          description: 'Deletes files or downgrades quality.',
          color: '#ef4444',
          defaultConfig: { mode: 'delete_files_and_unmonitor' },
        },
      ];

      render(
        <CleanerrDagStudio
          graphJson={{ nodes: [], edges: [] }}
          onChange={vi.fn()}
          nodeCatalog={backendCatalog}
        />,
      );

      await user.click(screen.getByRole('button', { name: /Add Node/i }));

      const categoryGroup = screen.getByRole('group', {
        name: 'Node category filter',
      });

      await user.click(within(categoryGroup).getByRole('button', { name: 'Triggers' }));
      expect(screen.getByText('Schedule (Cron)')).toBeInTheDocument();
      expect(screen.queryByText('Plex Library')).not.toBeInTheDocument();

      await user.click(within(categoryGroup).getByRole('button', { name: 'Logic' }));
      expect(screen.getByText('Logical AND')).toBeInTheDocument();
      expect(screen.queryByText('Watch State')).not.toBeInTheDocument();

      await user.click(within(categoryGroup).getByRole('button', { name: 'Filters' }));
      expect(screen.getByText('Watch State')).toBeInTheDocument();
      expect(screen.queryByText('Logical AND')).not.toBeInTheDocument();

      await user.click(within(categoryGroup).getByRole('button', { name: 'Limits' }));
      expect(screen.getByText('Limit / Reclaim Budget')).toBeInTheDocument();
      expect(screen.queryByText('Watch State')).not.toBeInTheDocument();

      await user.click(within(categoryGroup).getByRole('button', { name: 'Queue' }));
      expect(screen.getByText('Leaving Soon Grace Queue')).toBeInTheDocument();
      expect(screen.queryByText('Discord Notification')).not.toBeInTheDocument();

      await user.click(within(categoryGroup).getByRole('button', { name: 'Actions' }));
      expect(screen.getByText('Managerr Action')).toBeInTheDocument();
      expect(screen.getByText('Discord Notification')).toBeInTheDocument();
      expect(
        screen.queryByText('Leaving Soon Grace Queue'),
      ).not.toBeInTheDocument();
    });

    it('excludes TV Granularity Selector from palette and season/episode options from source node inspector', async () => {
      vi.stubGlobal(
        'ResizeObserver',
        class ResizeObserver {
          observe() {}
          unobserve() {}
          disconnect() {}
        },
      );
      const user = userEvent.setup();

      const backendCatalogWithGranularity: CleanerrNodeCatalogEntry[] = [
        {
          nodeType: 'source.managerr_catalog',
          category: 'Sources',
          label: 'Managerr Catalog Source',
          description: 'Loads movies and whole TV series directly from the Managerr catalog.',
          color: '#35c5f4',
          defaultConfig: { media_type: 'movie', library_ids: [] },
        },
        {
          nodeType: 'source.granularity',
          category: 'Sources',
          label: 'TV Granularity Selector',
          description: 'Legacy granularity node',
          color: '#35c5f4',
          defaultConfig: {},
        },
      ];

      render(
        <CleanerrDagStudio
          graphJson={{
            nodes: [
              {
                id: 'n_src',
                type: 'cleanerrNode',
                position: { x: 80, y: 120 },
                data: {
                  label: 'Catalog Source',
                  node_type: 'source.managerr_catalog',
                  config: { media_type: 'season', library_ids: [] },
                },
              },
            ],
            edges: [],
          }}
          onChange={vi.fn()}
          nodeCatalog={backendCatalogWithGranularity}
        />,
      );

      // TV Granularity Selector must not appear in the palette
      await user.click(screen.getByRole('button', { name: /Add Node/i }));
      expect(screen.getByText('Managerr Catalog Source')).toBeInTheDocument();
      expect(screen.queryByText('TV Granularity Selector')).not.toBeInTheDocument();

      // Selecting the source node reveals Media Type without season/episode options, normalized to 'show'
      fireEvent.click(screen.getByText('Catalog Source'));
      const mediaTypeSelect = (await screen.findByLabelText('Media Type')) as HTMLSelectElement;
      const optionValues = Array.from(mediaTypeSelect.options).map((o) => o.value);
      expect(optionValues).toEqual(['movie', 'show']);
      expect(optionValues).not.toContain('season');
      expect(optionValues).not.toContain('episode');
      expect(mediaTypeSelect.value).toBe('show');
    });
  });

  describe('CleanerrQueueSection', () => {
    it('opens CleanerrTestMediaModal from "Why?" with the queued mediaItemId preselected', async () => {
      const user = userEvent.setup();
      const qc = createTestQueryClient();

      const fetchMock = vi.fn(
        async (input: RequestInfo | URL, init?: RequestInit) => {
          const rawUrl =
            typeof input === 'string'
              ? input
              : input instanceof URL
                ? input.toString()
                : input.url;
          const url = new URL(rawUrl, 'http://localhost');

          if (url.pathname === '/api/v1/cleanerr/queue') {
            return jsonResponse([
              {
                id: 301,
                mediaItemId: 777,
                mediaTitle: 'Dune: Part Two',
                mediaType: 'movie',
                sizeBytes: 24 * 1024 * 1024 * 1024,
                workflowId: 12,
                granularity: 'movie',
                stagedAt: '2026-11-18T00:00:00Z',
                deleteAt: '2026-11-25T00:00:00Z',
                graceDays: 7,
                status: 'staged',
                rescuedReason: null,
                executedAt: null,
                matchedReason: 'Watched > 90 days ago',
                finalWarningSentAt: '2026-11-24T00:00:00Z',
              },
            ]);
          }

          if (url.pathname === '/api/v1/cleanerr/settings') {
            return jsonResponse({
              defaultGraceDays: 7,
              seerrUrl: '',
              hasSeerrApiKey: false,
              maskedSeerrApiKey: '',
              finalWarningHours: 24,
              defaultPostponeDays: 14,
              torrentDownloadsPath: '/data/downloads',
            });
          }

          if (url.pathname === '/api/v1/cleanerr/workflows') {
            return jsonResponse([
              {
                id: 12,
                name: 'Stale Movies',
                description: '',
                enabled: true,
                dryRun: false,
                scheduleCron: '0 3 * * *',
                editorMode: 'rules',
              },
            ]);
          }

          if (url.pathname === '/api/v1/cleanerr/media') {
            return jsonResponse({
              items: [
                {
                  id: 101,
                  plexRatingKey: 'rk-101',
                  mediaType: 'movie',
                  title: 'Other Movie',
                  year: 2020,
                  sizeBytes: 10 * 1024 * 1024 * 1024,
                  tags: [],
                  viewCount: 1,
                  activeStreamCount: 0,
                },
                {
                  id: 777,
                  plexRatingKey: 'rk-777',
                  mediaType: 'movie',
                  title: 'Dune: Part Two',
                  year: 2024,
                  sizeBytes: 24 * 1024 * 1024 * 1024,
                  tags: [],
                  viewCount: 2,
                  activeStreamCount: 0,
                },
              ],
              total: 2,
              page: 1,
              pageSize: 200,
            });
          }

          if (
            url.pathname === '/api/v1/cleanerr/rules/test-media' &&
            init?.method === 'POST'
          ) {
            return jsonResponse({
              mediaItemId: 777,
              mediaTitle: 'Dune: Part Two',
              wouldMatch: true,
              summary: 'Matched rule group Stale Movies',
              isExcluded: false,
              isCurrentlyStaged: true,
              stagedDeleteAt: '2026-11-25T00:00:00Z',
              guardNotes: [],
              sections: [],
            });
          }

          return jsonResponse([]);
        },
      );

      vi.stubGlobal('fetch', fetchMock);

      render(
        <QueryClientProvider client={qc}>
          <CleanerrQueueSection
            status={{
              module: 'cleanerr',
              defaultAccessPolicy: 'admin_only',
              canRead: true,
              canRescue: true,
              canExclude: true,
              canWriteWorkflows: true,
              canExecute: true,
              cleanerrExclusionLimit: null,
            }}
          />
        </QueryClientProvider>,
      );

      const whyBtn = await screen.findByRole('button', { name: /Why\?/i });
      await user.click(whyBtn);

      expect(
        await screen.findByText('Cleanerr Test Media Explainer'),
      ).toBeInTheDocument();

      await waitFor(() => {
        const testMediaCall = fetchMock.mock.calls.find(
          ([reqUrl, init]) =>
            String(reqUrl).includes('/api/v1/cleanerr/rules/test-media') &&
            init?.method === 'POST',
        );
        expect(testMediaCall).toBeDefined();
        const body = JSON.parse(String(testMediaCall![1]?.body));
        expect(body).toMatchObject({
          mediaItemId: 777,
          workflowId: 12,
        });
      });

      expect(
        await screen.findByText('Matched rule group Stale Movies'),
      ).toBeInTheDocument();

      const modal = document.querySelector('dialog[open]') as HTMLElement;
      expect(modal).not.toBeNull();
      const mediaCalls = fetchMock.mock.calls.filter(([reqUrl]) =>
        String(reqUrl).includes('/api/v1/cleanerr/media'),
      );
      expect(
        mediaCalls.some(([reqUrl]) => String(reqUrl).includes('pageSize=200')),
      ).toBe(true);

      const duneRow = within(modal)
        .getByText('Dune: Part Two')
        .closest('div')?.parentElement;
      expect(duneRow).not.toBeNull();
      expect(
        within(duneRow!).getByRole('button', { name: /Test/i }),
      ).toHaveAttribute('aria-pressed', 'true');

      // Queue renders "Final Warning Sent" and configured +14d postpone label
      expect(screen.getByText('Final Warning Sent')).toBeInTheDocument();
      expect(
        screen.getByRole('button', { name: /Postpone All \+14d/i }),
      ).toBeInTheDocument();
      expect(
        screen.getByRole('button', { name: '+14d' }),
      ).toBeInTheDocument();
    });
  });
});
