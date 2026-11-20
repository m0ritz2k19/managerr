import { useEffect, useId, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Check,
  Copy,
  ExternalLink,
  Eye,
  EyeOff,
  PlugZap,
  Radio,
  RefreshCw,
  Save,
  Search,
  Send,
  Settings,
  Terminal,
} from 'lucide-react';
import { apiFetch } from '../../lib/api';
import {
  ArrAlert,
  ArrButton,
  ArrLabel,
  FieldSet,
  FormGroup,
  PageToolbar,
  PageToolbarButton,
  PageToolbarSeparator,
} from '../arr/ArrPrimitives';
import type {
  CleanerrDiscordDiscoveryResult,
  CleanerrDiscordSettings,
  CleanerrGeneralSettings,
  CleanerrStatusResponse,
} from './cleanerrTypes';

interface Props {
  status?: CleanerrStatusResponse;
}

export function CleanerrDiscordAndSettingsSection({ status }: Props) {
  const qc = useQueryClient();
  const [feedback, setFeedback] = useState<{
    kind: 'info' | 'success' | 'danger';
    message: string;
  } | null>(null);

  // Discord Settings State — ZERO hardcoded personal IDs or secrets
  const [botToken, setBotToken] = useState('');
  const [showToken, setShowToken] = useState(false);
  const [applicationId, setApplicationId] = useState('');
  const [publicKeyHex, setPublicKeyHex] = useState('');
  const [guildId, setGuildId] = useState('');
  const [channelId, setChannelId] = useState('');
  const [webhookUrl, setWebhookUrl] = useState('');
  const [publicBaseUrl, setPublicBaseUrl] = useState('');
  const [authMode, setAuthMode] = useState<
    'channel_members' | 'discord_roles' | 'linked_plex_rbac'
  >('channel_members');
  const [allowedRoleIdsStr, setAllowedRoleIdsStr] = useState('');
  const [notifyOnStaged, setNotifyOnStaged] = useState(true);
  const [notifyOnFinalWarning, setNotifyOnFinalWarning] = useState(true);
  const [finalWarningHours, setFinalWarningHours] = useState(24);
  const [notifyOnRescued, setNotifyOnRescued] = useState(false);
  const [notifyOnDeleted, setNotifyOnDeleted] = useState(true);
  const [passiveShadowMode, setPassiveShadowMode] = useState(false);
  const [webhookBotUsername, setWebhookBotUsername] = useState('Cleanerr');
  const [copiedEndpoint, setCopiedEndpoint] = useState(false);

  // Guild/Channel Discovery state
  const [discovery, setDiscovery] =
    useState<CleanerrDiscordDiscoveryResult | null>(null);

  // General & Seerr Settings State
  const [defaultGraceDays, setDefaultGraceDays] = useState(7);
  const [defaultPostponeDays, setDefaultPostponeDays] = useState(7);
  const [torrentDownloadsPath, setTorrentDownloadsPath] =
    useState('/data/downloads');
  const [generalFinalWarningHours, setGeneralFinalWarningHours] = useState(24);
  const [seerrUrl, setSeerrUrl] = useState('');
  const [seerrApiKey, setSeerrApiKey] = useState('');
  const [showSeerrKey, setShowSeerrKey] = useState(false);

  // Accessible IDs
  const botTokenId = useId();
  const tokenWarningId = useId();
  const appIdInputId = useId();
  const publicKeyId = useId();
  const guildSelectId = useId();
  const guildIdInputId = useId();
  const channelSelectId = useId();
  const channelIdInputId = useId();
  const allowedRolesId = useId();
  const webhookUrlId = useId();
  const webhookBotUsernameId = useId();
  const publicBaseUrlId = useId();
  const interactionsUrlId = useId();
  const discordFinalHoursId = useId();

  const defaultGraceDaysId = useId();
  const defaultPostponeDaysId = useId();
  const torrentDownloadsPathId = useId();
  const generalFinalHoursId = useId();
  const seerrUrlId = useId();
  const seerrKeyId = useId();
  const seerrWarningId = useId();

  // Queries
  const discordQuery = useQuery({
    queryKey: ['cleanerr-discord'],
    queryFn: () =>
      apiFetch<CleanerrDiscordSettings>('/api/v1/cleanerr/discord/settings'),
  });

  const generalQuery = useQuery({
    queryKey: ['cleanerr-settings'],
    queryFn: () =>
      apiFetch<CleanerrGeneralSettings>('/api/v1/cleanerr/settings'),
  });

  useEffect(() => {
    if (discordQuery.data) {
      const d = discordQuery.data;
      setApplicationId(d.applicationId ?? '');
      setPublicKeyHex(d.publicKeyHex ?? '');
      setGuildId(d.guildId ?? '');
      setChannelId(d.channelId ?? '');
      setWebhookUrl(d.webhookUrl ?? '');
      setPublicBaseUrl(d.publicBaseUrl ?? '');
      setAuthMode(d.authMode ?? 'channel_members');
      setAllowedRoleIdsStr((d.allowedRoleIds ?? []).join(', '));
      setNotifyOnStaged(d.notifyOnStaged ?? true);
      setNotifyOnFinalWarning(d.notifyOnFinalWarning ?? true);
      setFinalWarningHours(d.finalWarningHours ?? 24);
      setNotifyOnRescued(d.notifyOnRescued ?? false);
      setNotifyOnDeleted(d.notifyOnDeleted ?? true);
      setPassiveShadowMode(d.gatewayPassiveShadowMode ?? false);
      setWebhookBotUsername(d.webhookBotUsername ?? 'Cleanerr');
    }
  }, [discordQuery.data]);

  useEffect(() => {
    if (generalQuery.data) {
      const g = generalQuery.data;
      setDefaultGraceDays(g.defaultGraceDays ?? 7);
      setDefaultPostponeDays(g.defaultPostponeDays ?? 7);
      setTorrentDownloadsPath(g.torrentDownloadsPath ?? '/data/downloads');
      setGeneralFinalWarningHours(g.finalWarningHours ?? 24);
      setSeerrUrl(g.seerrUrl ?? '');
    }
  }, [generalQuery.data]);

  // Mutations
  const saveDiscordMutation = useMutation({
    mutationFn: () => {
      const allowedRoles = allowedRoleIdsStr
        .split(',')
        .map((s) => s.trim())
        .filter(Boolean);

      const payload: Record<string, unknown> = {
        applicationId: applicationId.trim(),
        publicKeyHex: publicKeyHex.trim(),
        guildId: guildId.trim(),
        channelId: channelId.trim(),
        webhookUrl: webhookUrl.trim(),
        publicBaseUrl: publicBaseUrl.trim(),
        authMode,
        allowedRoleIds: allowedRoles,
        notifyOnStaged,
        notifyOnFinalWarning,
        finalWarningHours: Math.max(1, Number(finalWarningHours) || 24),
        notifyOnRescued,
        notifyOnDeleted,
        gatewayPassiveShadowMode: passiveShadowMode,
        webhookBotUsername: webhookBotUsername.trim() || 'Cleanerr',
      };

      if (botToken.trim()) {
        payload.botToken = botToken.trim();
      }

      return apiFetch<CleanerrDiscordSettings>(
        '/api/v1/cleanerr/discord/settings',
        {
          method: 'PUT',
          body: JSON.stringify(payload),
        },
      );
    },
    onSuccess: () => {
      setBotToken('');
      setFeedback({
        kind: 'success',
        message: 'Discord Gateway & Notification settings saved successfully.',
      });
      void qc.invalidateQueries({ queryKey: ['cleanerr-discord'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-settings'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr', 'discord'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr', 'settings'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-stats'] });
    },
    onError: (err: Error) => {
      setFeedback({
        kind: 'danger',
        message: err.message || 'Failed to save Discord settings.',
      });
    },
  });

  const reconnectMutation = useMutation({
    mutationFn: () =>
      apiFetch<{ connected?: boolean; message?: string }>(
        '/api/v1/cleanerr/discord/reconnect',
        { method: 'POST', body: JSON.stringify({}) },
      ),
    onSuccess: (res) => {
      setFeedback({
        kind: 'info',
        message:
          res.message ||
          'Discord Gateway reconnect triggered. Refreshing status...',
      });
      void qc.invalidateQueries({ queryKey: ['cleanerr-discord'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-stats'] });
    },
    onError: (err: Error) => {
      setFeedback({
        kind: 'danger',
        message: err.message || 'Gateway reconnect failed.',
      });
    },
  });

  const discoverMutation = useMutation({
    mutationFn: () =>
      apiFetch<CleanerrDiscordDiscoveryResult>(
        '/api/v1/cleanerr/discord/discover',
      ),
    onSuccess: (res) => {
      setDiscovery(res);
      if (res.ok) {
        setFeedback({
          kind: 'success',
          message: `Discovered ${res.guilds.length} guild(s) and ${res.channels.length} channel(s)${res.botUsername ? ` as ${res.botUsername}` : ''}.`,
        });
      } else {
        setFeedback({
          kind: 'danger',
          message: res.error || 'Discord guild/channel discovery failed.',
        });
      }
    },
    onError: (err: Error) => {
      setFeedback({
        kind: 'danger',
        message: err.message || 'Failed to discover Discord guilds & channels.',
      });
    },
  });

  const syncCommandsMutation = useMutation({
    mutationFn: () =>
      apiFetch<{
        ok: boolean;
        scope: string;
        commandsRegistered: number;
        message: string;
      }>('/api/v1/cleanerr/discord/sync-commands', {
        method: 'POST',
        body: JSON.stringify({}),
      }),
    onSuccess: (res) => {
      setFeedback({
        kind: 'success',
        message: `Slash commands registered (${res.scope}): ${res.commandsRegistered} commands live. ${res.message}`,
      });
    },
    onError: (err: Error) => {
      setFeedback({
        kind: 'danger',
        message: err.message || 'Failed to sync /cleanerr slash commands.',
      });
    },
  });

  const sendTestMutation = useMutation({
    mutationFn: () =>
      apiFetch<{ sent: boolean; deliveryMode?: string; message?: string }>(
        '/api/v1/cleanerr/discord/test',
        { method: 'POST', body: JSON.stringify({}) },
      ),
    onSuccess: (res) => {
      setFeedback({
        kind: 'success',
        message:
          res.message ||
          `Interactive test dropdown sent via ${res.deliveryMode ?? 'Gateway'}. Check your Discord channel.`,
      });
    },
    onError: (err: Error) => {
      setFeedback({
        kind: 'danger',
        message: err.message || 'Failed to send interactive test message.',
      });
    },
  });

  const saveGeneralMutation = useMutation({
    mutationFn: () => {
      const parsedGrace = Number(defaultGraceDays);
      const parsedPostpone = Number(defaultPostponeDays);
      const payload: Record<string, unknown> = {
        defaultGraceDays: Number.isFinite(parsedGrace)
          ? Math.max(0, Math.min(365, parsedGrace))
          : 7,
        defaultPostponeDays: Number.isFinite(parsedPostpone)
          ? Math.max(1, Math.min(365, parsedPostpone))
          : 7,
        torrentDownloadsPath: torrentDownloadsPath.trim() || '/data/downloads',
        finalWarningHours: Math.max(1, Number(generalFinalWarningHours) || 24),
        seerrUrl: seerrUrl.trim(),
      };
      if (seerrApiKey.trim()) {
        payload.seerrApiKey = seerrApiKey.trim();
      }
      return apiFetch<CleanerrGeneralSettings>('/api/v1/cleanerr/settings', {
        method: 'PUT',
        body: JSON.stringify(payload),
      });
    },
    onSuccess: () => {
      setSeerrApiKey('');
      setFeedback({
        kind: 'success',
        message: 'Cleanerr General & Seerr settings saved successfully.',
      });
      void qc.invalidateQueries({ queryKey: ['cleanerr-settings'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr-discord'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr', 'settings'] });
      void qc.invalidateQueries({ queryKey: ['cleanerr', 'discord'] });
    },
    onError: (err: Error) => {
      setFeedback({
        kind: 'danger',
        message: err.message || 'Failed to save general settings.',
      });
    },
  });

  const d = discordQuery.data;
  const g = generalQuery.data;
  const canAdmin = Boolean(status?.canWriteWorkflows);

  const botInviteUrl = useMemo(() => {
    const trimmed = applicationId.trim();
    if (!trimmed) return '';
    return `https://discord.com/oauth2/authorize?client_id=${encodeURIComponent(trimmed)}&permissions=274877991936&integration_type=0&scope=bot+applications.commands`;
  }, [applicationId]);

  const interactionsEndpointUrl = useMemo(() => {
    const origin =
      typeof window !== 'undefined' ? window.location.origin : '';
    const base = (publicBaseUrl.trim() || origin).replace(/\/+$/, '');
    return `${base}/api/v1/cleanerr/webhooks/discord-interactions`;
  }, [publicBaseUrl]);

  const discoveredTextChannels = useMemo(() => {
    const channels = discovery?.channels ?? [];
    return channels.filter(
      (ch) =>
        (ch.type === 0 || ch.type === 5) &&
        (!guildId.trim() || ch.guildId === guildId.trim()),
    );
  }, [discovery?.channels, guildId]);

  const handleCopyEndpoint = () => {
    void navigator.clipboard.writeText(interactionsEndpointUrl);
    setCopiedEndpoint(true);
    window.setTimeout(() => setCopiedEndpoint(false), 2000);
  };

  return (
    <div className="flex flex-col flex-1">
      <PageToolbar
        left={
          <>
            <PageToolbarButton
              icon={RefreshCw}
              label="Refresh"
              spinning={discordQuery.isFetching || generalQuery.isFetching}
              onClick={() => {
                void discordQuery.refetch();
                void generalQuery.refetch();
              }}
            />
            {canAdmin && (
              <>
                <PageToolbarSeparator />
                <PageToolbarButton
                  icon={Save}
                  label="Save Discord"
                  spinning={saveDiscordMutation.isPending}
                  onClick={() => saveDiscordMutation.mutate()}
                />
                <PageToolbarButton
                  icon={PlugZap}
                  label="Reconnect Gateway"
                  spinning={reconnectMutation.isPending}
                  onClick={() => reconnectMutation.mutate()}
                />
                <PageToolbarButton
                  icon={Terminal}
                  label="Sync /cleanerr"
                  spinning={syncCommandsMutation.isPending}
                  onClick={() => syncCommandsMutation.mutate()}
                />
                <PageToolbarButton
                  icon={Send}
                  label="Test Dropdown"
                  spinning={sendTestMutation.isPending}
                  onClick={() => sendTestMutation.mutate()}
                />
              </>
            )}
          </>
        }
        right={
          status && (
            <ArrLabel kind={status.canWriteWorkflows ? 'success' : 'default'}>
              {status.canWriteWorkflows ? 'Cleanerr Admin' : 'Read Only'}
            </ArrLabel>
          )
        }
      />

      <div className="p-5 lg:p-6 space-y-6 overflow-x-hidden">
        <div role="status" aria-live="polite">
          {feedback && (
            <ArrAlert kind={feedback.kind}>{feedback.message}</ArrAlert>
          )}
        </div>

        {/* Live Gateway Status Banner */}
        <div
          className={`p-4 rounded-[6px] border flex flex-wrap items-start justify-between gap-4 ${
            d?.gatewayConnected
              ? 'bg-emerald-500/10 border-emerald-500/50 text-[var(--arr-text)]'
              : 'bg-amber-500/10 border-amber-500/50 text-[var(--arr-text)]'
          }`}
        >
          <div className="flex items-start gap-3.5 min-w-0 flex-1">
            <Radio
              aria-hidden="true"
              className={`h-6 w-6 shrink-0 mt-0.5 ${
                d?.gatewayConnected
                  ? 'text-emerald-500 animate-pulse'
                  : 'text-amber-500'
              }`}
            />
            <div className="space-y-1 min-w-0 flex-1 text-[13px]">
              <div className="font-bold text-[15px] flex flex-wrap items-center gap-2">
                <span>Discord Gateway</span>
                <ArrLabel kind={d?.gatewayConnected ? 'success' : 'warning'}>
                  {d?.gatewayConnected ? 'Connected' : 'Disconnected / Standby'}
                </ArrLabel>
                {d?.gatewayBotUsername && (
                  <ArrLabel kind="info">@{d.gatewayBotUsername}</ArrLabel>
                )}
                {d?.gatewayPassiveShadowMode && (
                  <ArrLabel kind="purple">Passive Shadow Mode</ArrLabel>
                )}
              </div>
              <p className="text-[12px] text-[var(--arr-text-dim)] leading-relaxed">
                Outbound WebSocket connection handles <code>/cleanerr</code>{' '}
                slash commands and interactive Exclude / Postpone controls
                without requiring open inbound ports.
              </p>
              {d?.gatewayLastError && (
                <div className="text-[12px] text-[var(--arr-danger)] font-mono">
                  Last Gateway Error: {d.gatewayLastError}
                </div>
              )}
            </div>
          </div>

          {canAdmin && (
            <ArrButton
              variant="default"
              busy={reconnectMutation.isPending}
              onClick={() => reconnectMutation.mutate()}
            >
              <PlugZap aria-hidden="true" className="h-3.5 w-3.5" /> Reconnect
              Gateway
            </ArrButton>
          )}
        </div>

        <div className="grid grid-cols-1 xl:grid-cols-3 gap-6">
          {/* Discord Bot & RBAC & Notifications */}
          <form
            onSubmit={(e) => {
              e.preventDefault();
              saveDiscordMutation.mutate();
            }}
            className="xl:col-span-2 space-y-6"
          >
            <FieldSet
              legend="Discord Bot Credentials & Discovery"
              action={
                canAdmin && (
                  <ArrButton
                    variant="default"
                    busy={discoverMutation.isPending}
                    onClick={() => discoverMutation.mutate()}
                  >
                    <Search aria-hidden="true" className="h-3.5 w-3.5" />{' '}
                    Discover Guilds &amp; Channels
                  </ArrButton>
                )
              }
            >
              <FormGroup
                label="Discord Bot Token"
                htmlFor={botTokenId}
                helpText={
                  d?.hasBotToken
                    ? `Current token saved: ${d.maskedBotToken}. Leave blank to keep existing token.`
                    : 'Paste bot token from Discord Developer Portal.'
                }
              >
                <div className="flex items-center gap-2">
                  <input
                    id={botTokenId}
                    name="botToken"
                    type={showToken ? 'text' : 'password'}
                    value={botToken}
                    onChange={(e) => setBotToken(e.target.value)}
                    placeholder={
                      d?.hasBotToken ? d.maskedBotToken : 'Paste bot token...'
                    }
                    autoComplete="off"
                    autoCorrect="off"
                    autoCapitalize="none"
                    spellCheck={false}
                    className="arr-input font-mono text-[12px] flex-1"
                  />
                  <ArrButton
                    type="button"
                    variant="default"
                    aria-pressed={showToken}
                    aria-label={
                      showToken ? 'Hide bot token' : 'Show bot token'
                    }
                    aria-describedby={tokenWarningId}
                    onClick={() => setShowToken((prev) => !prev)}
                  >
                    {showToken ? (
                      <EyeOff aria-hidden="true" className="h-4 w-4" />
                    ) : (
                      <Eye aria-hidden="true" className="h-4 w-4" />
                    )}
                    <span>{showToken ? 'Hide' : 'Show'}</span>
                  </ArrButton>
                </div>
                <span id={tokenWarningId} className="sr-only">
                  Warning: toggling this will display the bot token on screen.
                </span>
              </FormGroup>

              <FormGroup
                label="Application ID"
                htmlFor={appIdInputId}
                helpText="Required for slash command registration and generating the Bot Invite URL."
              >
                <input
                  id={appIdInputId}
                  name="applicationId"
                  type="text"
                  value={applicationId}
                  onChange={(e) => setApplicationId(e.target.value)}
                  placeholder="Enter Discord Application ID..."
                  autoCorrect="off"
                  autoCapitalize="none"
                  spellCheck={false}
                  className="arr-input font-mono text-[12px]"
                />
              </FormGroup>

              <FormGroup
                label="Public Key (Hex)"
                htmlFor={publicKeyId}
                helpText="Ed25519 Public Key from Discord Developer Portal (required only for HTTP Interactions)."
              >
                <input
                  id={publicKeyId}
                  name="publicKeyHex"
                  type="text"
                  value={publicKeyHex}
                  onChange={(e) => setPublicKeyHex(e.target.value)}
                  placeholder="Enter Ed25519 Public Key hex..."
                  autoCorrect="off"
                  autoCapitalize="none"
                  spellCheck={false}
                  className="arr-input font-mono text-[12px]"
                />
              </FormGroup>

              <FormGroup label="Bot Invite URL">
                {botInviteUrl ? (
                  <a
                    href={botInviteUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="inline-flex items-center gap-1.5 text-[12px] font-medium text-[var(--arr-accent)] hover:underline break-all"
                  >
                    <ExternalLink
                      aria-hidden="true"
                      className="h-3.5 w-3.5 shrink-0"
                    />
                    <span>Invite Bot to Server (OAuth2)</span>
                  </a>
                ) : (
                  <span className="text-[12px] text-[var(--arr-muted)]">
                    Enter an Application ID above to generate a bot invite link.
                  </span>
                )}
              </FormGroup>

              {discovery && discovery.guilds.length > 0 && (
                <FormGroup
                  label="Discovered Guild"
                  htmlFor={guildSelectId}
                  helpText="Select a discovered server to populate Guild ID."
                >
                  <select
                    id={guildSelectId}
                    value={guildId}
                    onChange={(e) => setGuildId(e.target.value)}
                    className="arr-input text-[12px]"
                  >
                    <option value="">-- Select Discovered Guild --</option>
                    {discovery.guilds.map((gd) => (
                      <option key={gd.id} value={gd.id}>
                        {gd.name} ({gd.id}){gd.isOwner ? ' [Owner]' : ''}
                      </option>
                    ))}
                  </select>
                </FormGroup>
              )}

              <FormGroup
                label="Server (Guild) ID"
                htmlFor={guildIdInputId}
                helpText="Optional. Enables instant guild slash command sync."
              >
                <input
                  id={guildIdInputId}
                  name="guildId"
                  type="text"
                  value={guildId}
                  onChange={(e) => setGuildId(e.target.value)}
                  placeholder="Enter Guild ID..."
                  autoCorrect="off"
                  autoCapitalize="none"
                  spellCheck={false}
                  className="arr-input font-mono text-[12px]"
                />
              </FormGroup>

              {discovery && discoveredTextChannels.length > 0 && (
                <FormGroup
                  label="Discovered Text Channel"
                  htmlFor={channelSelectId}
                  helpText="Text & announcement channels (type 0 / 5) from the selected guild."
                >
                  <select
                    id={channelSelectId}
                    value={channelId}
                    onChange={(e) => setChannelId(e.target.value)}
                    className="arr-input text-[12px]"
                  >
                    <option value="">-- Select Discovered Channel --</option>
                    {discoveredTextChannels.map((ch) => (
                      <option key={ch.id} value={ch.id}>
                        #{ch.name} ({ch.id})
                      </option>
                    ))}
                  </select>
                </FormGroup>
              )}

              <FormGroup
                label="Notification Channel ID"
                htmlFor={channelIdInputId}
                helpText="Target Discord channel ID for Leaving Soon alerts and summaries."
              >
                <input
                  id={channelIdInputId}
                  name="channelId"
                  type="text"
                  value={channelId}
                  onChange={(e) => setChannelId(e.target.value)}
                  placeholder="Enter Channel ID..."
                  autoCorrect="off"
                  autoCapitalize="none"
                  spellCheck={false}
                  className="arr-input font-mono text-[12px]"
                />
              </FormGroup>
            </FieldSet>

            <FieldSet legend="Discord RBAC & Permission Mode">
              <div className="mb-4">
                <fieldset className="border border-[var(--arr-card-border)] rounded-[4px] p-4 bg-[var(--arr-card-bg)]">
                  <legend className="px-1.5 text-[13px] font-bold text-[var(--arr-text)]">
                    Authorization Mode
                  </legend>
                  <div className="space-y-3 text-[13px]">
                    <label className="flex items-start gap-2.5 cursor-pointer">
                      <input
                        type="radio"
                        name="authMode"
                        value="channel_members"
                        checked={authMode === 'channel_members'}
                        onChange={() => setAuthMode('channel_members')}
                        className="mt-1"
                      />
                      <div>
                        <span className="font-semibold text-[var(--arr-text)]">
                          Channel Members
                        </span>
                        <p className="text-[11px] text-[var(--arr-muted)]">
                          Any member in the notification channel can interact
                          with Exclude and Postpone controls.
                        </p>
                      </div>
                    </label>

                    <label className="flex items-start gap-2.5 cursor-pointer">
                      <input
                        type="radio"
                        name="authMode"
                        value="discord_roles"
                        checked={authMode === 'discord_roles'}
                        onChange={() => setAuthMode('discord_roles')}
                        className="mt-1"
                      />
                      <div>
                        <span className="font-semibold text-[var(--arr-text)]">
                          Discord Role Allowlist
                        </span>
                        <p className="text-[11px] text-[var(--arr-muted)]">
                          Only users holding one of the Allowed Role IDs below
                          may exclude or postpone items.
                        </p>
                      </div>
                    </label>

                    <label className="flex items-start gap-2.5 cursor-pointer">
                      <input
                        type="radio"
                        name="authMode"
                        value="linked_plex_rbac"
                        checked={authMode === 'linked_plex_rbac'}
                        onChange={() => setAuthMode('linked_plex_rbac')}
                        className="mt-1"
                      />
                      <div>
                        <span className="font-semibold text-[var(--arr-text)]">
                          Linked Plex / Managerr RBAC
                        </span>
                        <p className="text-[11px] text-[var(--arr-muted)]">
                          Requires linked Managerr account with{' '}
                          <code>cleanerr:exclude</code> scope and enforces user
                          exclusion quotas.
                        </p>
                      </div>
                    </label>
                  </div>
                </fieldset>
              </div>

              {authMode === 'discord_roles' && (
                <FormGroup
                  label="Allowed Role IDs"
                  htmlFor={allowedRolesId}
                  helpText="Comma-separated Discord role IDs permitted to interact with Cleanerr controls."
                >
                  <input
                    id={allowedRolesId}
                    name="allowedRoleIds"
                    type="text"
                    value={allowedRoleIdsStr}
                    onChange={(e) => setAllowedRoleIdsStr(e.target.value)}
                    placeholder="Comma-separated role IDs..."
                    autoCorrect="off"
                    autoCapitalize="none"
                    spellCheck={false}
                    className="arr-input font-mono text-[12px]"
                  />
                </FormGroup>
              )}
            </FieldSet>

            <FieldSet legend="Notifications & Endpoints">
              <div className="space-y-3 p-4 mb-5 bg-[var(--arr-card-bg)] border border-[var(--arr-card-border)] rounded-[4px] text-[13px]">
                <label className="flex items-center gap-2 cursor-pointer text-[var(--arr-text)]">
                  <input
                    type="checkbox"
                    checked={notifyOnStaged}
                    onChange={(e) => setNotifyOnStaged(e.target.checked)}
                  />
                  <span>
                    Notify When Items Staged (Leaving Soon countdown &amp;
                    buttons)
                  </span>
                </label>

                <div className="flex flex-wrap items-center gap-3">
                  <label className="flex items-center gap-2 cursor-pointer text-[var(--arr-text)]">
                    <input
                      type="checkbox"
                      checked={notifyOnFinalWarning}
                      onChange={(e) =>
                        setNotifyOnFinalWarning(e.target.checked)
                      }
                    />
                    <span>Notify on Final Warning</span>
                  </label>
                  {notifyOnFinalWarning && (
                    <div className="flex items-center gap-2">
                      <label
                        htmlFor={discordFinalHoursId}
                        className="text-[12px] text-[var(--arr-muted)]"
                      >
                        Hours before deletion:
                      </label>
                      <input
                        id={discordFinalHoursId}
                        type="number"
                        min={1}
                        max={168}
                        value={finalWarningHours}
                        onChange={(e) => {
                          const next = Math.max(1, Number(e.target.value) || 24);
                          setFinalWarningHours(next);
                          setGeneralFinalWarningHours(next);
                        }}
                        className="arr-input !h-[28px] !w-20 !text-[12px]"
                      />
                    </div>
                  )}
                </div>

                <label className="flex items-center gap-2 cursor-pointer text-[var(--arr-text)]">
                  <input
                    type="checkbox"
                    checked={notifyOnRescued}
                    onChange={(e) => setNotifyOnRescued(e.target.checked)}
                  />
                  <span>Notify When Playback Auto-Rescues Item</span>
                </label>

                <label className="flex items-center gap-2 cursor-pointer text-[var(--arr-text)]">
                  <input
                    type="checkbox"
                    checked={notifyOnDeleted}
                    onChange={(e) => setNotifyOnDeleted(e.target.checked)}
                  />
                  <span>Notify When Items Deleted (Summary Digest)</span>
                </label>

                <div className="pt-2 border-t border-[var(--arr-card-border)]">
                  <label className="flex items-center gap-2 cursor-pointer text-amber-400">
                    <input
                      type="checkbox"
                      checked={passiveShadowMode}
                      onChange={(e) => setPassiveShadowMode(e.target.checked)}
                    />
                    <span>
                      Passive Shadow Mode (Disables Gateway interaction handling
                      while keeping outbound status)
                    </span>
                  </label>
                </div>
              </div>

              <FormGroup
                label="Fallback Webhook URL"
                htmlFor={webhookUrlId}
                helpText="Optional Discord webhook URL for passive notifications when Gateway is offline."
              >
                <input
                  id={webhookUrlId}
                  name="webhookUrl"
                  type="url"
                  value={webhookUrl}
                  onChange={(e) => setWebhookUrl(e.target.value)}
                  placeholder="https://discord.com/api/webhooks/..."
                  autoCorrect="off"
                  autoCapitalize="none"
                  spellCheck={false}
                  className="arr-input font-mono text-[12px]"
                />
              </FormGroup>

              <FormGroup
                label="Webhook Bot Username"
                htmlFor={webhookBotUsernameId}
                helpText="Display name shown for Discord webhook embeds (defaults to Cleanerr)."
              >
                <input
                  id={webhookBotUsernameId}
                  name="webhookBotUsername"
                  type="text"
                  value={webhookBotUsername}
                  onChange={(e) => setWebhookBotUsername(e.target.value)}
                  placeholder="Cleanerr"
                  className="arr-input text-[12px]"
                />
              </FormGroup>

              <FormGroup
                label="Public Base URL"
                htmlFor={publicBaseUrlId}
                helpText="Public base URL used for HMAC magic links and Interactions Endpoint URL."
              >
                <input
                  id={publicBaseUrlId}
                  name="publicBaseUrl"
                  type="url"
                  value={publicBaseUrl}
                  onChange={(e) => setPublicBaseUrl(e.target.value)}
                  placeholder="https://managerr.example.com"
                  autoCorrect="off"
                  autoCapitalize="none"
                  spellCheck={false}
                  className="arr-input font-mono text-[12px]"
                />
              </FormGroup>

              <FormGroup
                label="Interactions Endpoint URL"
                htmlFor={interactionsUrlId}
                helpText="Copy into Discord Developer Portal if using HTTP Interactions alongside Ed25519 Public Key."
              >
                <div className="flex items-center gap-2">
                  <input
                    id={interactionsUrlId}
                    type="text"
                    readOnly
                    value={interactionsEndpointUrl}
                    className="arr-input font-mono text-[12px] flex-1 select-all"
                  />
                  <ArrButton
                    type="button"
                    variant="default"
                    onClick={handleCopyEndpoint}
                  >
                    {copiedEndpoint ? (
                      <Check aria-hidden="true" className="h-3.5 w-3.5" />
                    ) : (
                      <Copy aria-hidden="true" className="h-3.5 w-3.5" />
                    )}
                    <span>{copiedEndpoint ? 'Copied' : 'Copy'}</span>
                  </ArrButton>
                </div>
              </FormGroup>

              {canAdmin && (
                <div className="flex flex-wrap items-center gap-2 pt-2">
                  <ArrButton
                    type="submit"
                    variant="primary"
                    busy={saveDiscordMutation.isPending}
                  >
                    <Save aria-hidden="true" className="h-3.5 w-3.5" /> Save
                    Discord Settings
                  </ArrButton>
                  <ArrButton
                    type="button"
                    variant="default"
                    busy={syncCommandsMutation.isPending}
                    onClick={() => syncCommandsMutation.mutate()}
                  >
                    <Terminal aria-hidden="true" className="h-3.5 w-3.5" /> Sync
                    /cleanerr Slash Commands
                  </ArrButton>
                  <ArrButton
                    type="button"
                    variant="default"
                    busy={sendTestMutation.isPending}
                    onClick={() => sendTestMutation.mutate()}
                  >
                    <Send aria-hidden="true" className="h-3.5 w-3.5" /> Send
                    Interactive Test Dropdown
                  </ArrButton>
                </div>
              )}
            </FieldSet>
          </form>

          {/* Cleanerr General & Seerr Settings Panel */}
          <form
            onSubmit={(e) => {
              e.preventDefault();
              saveGeneralMutation.mutate();
            }}
            className="space-y-4"
          >
            <div className="p-4 rounded-[6px] border border-[var(--arr-card-border)] bg-[var(--arr-card-bg)] space-y-4">
              <div className="flex items-center gap-2 pb-2 border-b border-[var(--arr-card-border)]">
                <Settings
                  aria-hidden="true"
                  className="h-4 w-4 text-[var(--arr-accent)]"
                />
                <h3 className="text-[15px] font-semibold text-[var(--arr-text)]">
                  Cleanerr General &amp; Seerr Settings
                </h3>
              </div>

              <div className="space-y-3">
                <div>
                  <label
                    htmlFor={defaultGraceDaysId}
                    className="block text-[12px] font-bold text-[var(--arr-text)] mb-1"
                  >
                    Default Grace Days
                  </label>
                  <input
                    id={defaultGraceDaysId}
                    name="defaultGraceDays"
                    type="number"
                    min={0}
                    max={365}
                    value={defaultGraceDays}
                    onChange={(e) =>
                      setDefaultGraceDays(
                        e.target.value === ''
                          ? 0
                          : Math.max(0, Number(e.target.value)),
                      )
                    }
                    className="arr-input"
                  />
                </div>

                <div>
                  <label
                    htmlFor={defaultPostponeDaysId}
                    className="block text-[12px] font-bold text-[var(--arr-text)] mb-1"
                  >
                    Default Postpone Days
                  </label>
                  <input
                    id={defaultPostponeDaysId}
                    name="defaultPostponeDays"
                    type="number"
                    min={1}
                    max={365}
                    value={defaultPostponeDays}
                    onChange={(e) =>
                      setDefaultPostponeDays(
                        e.target.value === ''
                          ? 1
                          : Math.max(1, Number(e.target.value)),
                      )
                    }
                    className="arr-input"
                  />
                </div>

                <div>
                  <label
                    htmlFor={torrentDownloadsPathId}
                    className="block text-[12px] font-bold text-[var(--arr-text)] mb-1"
                  >
                    Torrent Downloads Path
                  </label>
                  <input
                    id={torrentDownloadsPathId}
                    name="torrentDownloadsPath"
                    type="text"
                    value={torrentDownloadsPath}
                    onChange={(e) => setTorrentDownloadsPath(e.target.value)}
                    placeholder="/data/downloads"
                    autoCorrect="off"
                    autoCapitalize="none"
                    spellCheck={false}
                    className="arr-input font-mono text-[12px]"
                  />
                </div>

                <ArrAlert kind="info">
                  Staged items are tracked in Managerr&apos;s native Leaving Soon
                  queue and communicated via Discord notifications (Plex
                  collections are not used or modified).
                </ArrAlert>

                <div>
                  <label
                    htmlFor={generalFinalHoursId}
                    className="block text-[12px] font-bold text-[var(--arr-text)] mb-1"
                  >
                    Final Warning Window (Hours)
                  </label>
                  <input
                    id={generalFinalHoursId}
                    name="generalFinalWarningHours"
                    type="number"
                    min={1}
                    max={168}
                    value={generalFinalWarningHours}
                    onChange={(e) => {
                      const next = Math.max(1, Number(e.target.value) || 24);
                      setGeneralFinalWarningHours(next);
                      setFinalWarningHours(next);
                    }}
                    className="arr-input"
                  />
                </div>

                <div>
                  <label
                    htmlFor={seerrUrlId}
                    className="block text-[12px] font-bold text-[var(--arr-text)] mb-1"
                  >
                    Jellyseerr / Overseerr URL
                  </label>
                  <input
                    id={seerrUrlId}
                    name="seerrUrl"
                    type="url"
                    value={seerrUrl}
                    onChange={(e) => setSeerrUrl(e.target.value)}
                    placeholder="http://localhost:5055"
                    autoCorrect="off"
                    autoCapitalize="none"
                    spellCheck={false}
                    className="arr-input font-mono text-[12px]"
                  />
                </div>

                <div>
                  <label
                    htmlFor={seerrKeyId}
                    className="block text-[12px] font-bold text-[var(--arr-text)] mb-1"
                  >
                    Seerr API Key
                  </label>
                  <div className="flex items-center gap-2">
                    <input
                      id={seerrKeyId}
                      name="seerrApiKey"
                      type={showSeerrKey ? 'text' : 'password'}
                      value={seerrApiKey}
                      onChange={(e) => setSeerrApiKey(e.target.value)}
                      placeholder={
                        g?.hasSeerrApiKey
                          ? g.maskedSeerrApiKey
                          : 'Paste Seerr API key...'
                      }
                      autoComplete="off"
                      autoCorrect="off"
                      autoCapitalize="none"
                      spellCheck={false}
                      className="arr-input font-mono text-[12px] flex-1"
                    />
                    <ArrButton
                      type="button"
                      variant="default"
                      aria-pressed={showSeerrKey}
                      aria-label={
                        showSeerrKey ? 'Hide Seerr API key' : 'Show Seerr API key'
                      }
                      aria-describedby={seerrWarningId}
                      onClick={() => setShowSeerrKey((prev) => !prev)}
                    >
                      {showSeerrKey ? (
                        <EyeOff aria-hidden="true" className="h-4 w-4" />
                      ) : (
                        <Eye aria-hidden="true" className="h-4 w-4" />
                      )}
                    </ArrButton>
                  </div>
                  <span id={seerrWarningId} className="sr-only">
                    Warning: toggling this will display the Seerr API key on
                    screen.
                  </span>
                  {g?.hasSeerrApiKey && (
                    <p className="mt-1 text-[11px] text-[var(--arr-muted)]">
                      Saved key: {g.maskedSeerrApiKey}. Leave blank to keep.
                    </p>
                  )}
                </div>
              </div>

              {canAdmin && (
                <div className="pt-3 border-t border-[var(--arr-card-border)] flex justify-end">
                  <ArrButton
                    type="submit"
                    variant="primary"
                    busy={saveGeneralMutation.isPending}
                  >
                    <Save aria-hidden="true" className="h-3.5 w-3.5" /> Save
                    General Settings
                  </ArrButton>
                </div>
              )}
            </div>
          </form>
        </div>
      </div>
    </div>
  );
}
