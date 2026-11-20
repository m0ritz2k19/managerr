using System.Net;
using System.Reflection;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Managerr.Common.Configuration;
using Managerr.Common.Crypto;
using Managerr.Common.Paths;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Host.Auth;
using Managerr.Host.Middleware;
using Managerr.Host.Services;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Managerr.Host.Endpoints;

public sealed record TriggerBackupRequest(string? Reason = null);
public sealed record UpdateScheduledTaskRequest(int? IntervalSeconds = null, bool? IsEnabled = null);
public sealed record UpdateBackupSettingsRequest(int MaxRetainedBackups);
public sealed record UpdateGeneralSettingsRequest(
    string? InstanceName = null,
    string? DefaultTheme = null,
    string? AccentColor = null,
    string? ColorScheme = null,
    ManagerrCustomThemePayload? CustomTheme = null,
    bool? ClearCustomTheme = null,
    string? BoundServerUrl = null,
    bool? TrustForwardedHeaders = null,
    string[]? TrustedProxyCidrs = null,
    bool? McpEnabled = null,
    int? RollingQuotaWindowDays = null,
    int? MaxRetainedBackups = null,
    string[]? AllowedHosts = null);

public sealed class UpdateAuthSettingsRequest
{
    public UpdateLocalAuthSettings? Local { get; set; }
    public UpdatePasskeysAuthSettings? Passkeys { get; set; }
    public UpdatePlexAuthSettings? Plex { get; set; }
    public UpdateOidcAuthSettings? Oidc { get; set; }
    public UpdateUiSettings? Ui { get; set; }
    public UpdateSecuritySettings? Security { get; set; }
    public UpdateMcpSettings? Mcp { get; set; }

    // Flat UI aliases (GeneralSettingsView)
    public bool? LocalAuthEnabled { get; set; }
    public bool? PasskeysEnabled { get; set; }
    public bool? OidcEnabled { get; set; }
    public string? OidcDisplayName { get; set; }
    public string? OidcAuthority { get; set; }
    public string? OidcClientId { get; set; }
    public string? OidcClientSecret { get; set; }
    public string? OidcScope { get; set; }
    public bool? OidcAutoRedirect { get; set; }
    public string? ThemeMode { get; set; }
    public string? AccentColor { get; set; }
    public string? ColorScheme { get; set; }
    public bool? TrustForwardedHeaders { get; set; }
    public string[]? TrustedProxyCidrs { get; set; }
    public string[]? AllowedHosts { get; set; }
    public bool? McpEnabled { get; set; }
}

public sealed class UpdateLocalAuthSettings { public bool? Enabled { get; set; } }
public sealed class UpdatePasskeysAuthSettings
{
    public bool? Enabled { get; set; }
    public string? RpId { get; set; }
    public string? RpName { get; set; }
    public bool? RequireUserVerification { get; set; }
}
public sealed class UpdatePlexAuthSettings
{
    public bool? Enabled { get; set; }
    public string? BoundServerUrl { get; set; }
}
public sealed class UpdateSecuritySettings
{
    public bool? TrustForwardedHeaders { get; set; }
    public string[]? TrustedProxyCidrs { get; set; }
    public string[]? AllowedHosts { get; set; }
}
public sealed class UpdateMcpSettings
{
    public bool? Enabled { get; set; }
}
public sealed class UpdateOidcAuthSettings
{
    public bool? Enabled { get; set; }
    public string? DisplayName { get; set; }
    public string? Issuer { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? Scopes { get; set; }
    public bool? AutoProvision { get; set; }
    public bool? AutoRedirect { get; set; }
    public bool? TrustVerifiedEmail { get; set; }
    public string? UsernameClaim { get; set; }
    public string? EmailClaim { get; set; }
    public string? DisplayNameClaim { get; set; }
    public string? GroupsClaim { get; set; }
    public string[]? AdminGroups { get; set; }
    public string[]? ManagerGroups { get; set; }
    public string[]? RequesterGroups { get; set; }
}

public sealed class UpdateUiSettings
{
    public string? DefaultTheme { get; set; }
    public string? AccentColor { get; set; }
    public string? ColorScheme { get; set; }
}

public sealed class TestOidcSettingsRequest
{
    public string? Issuer { get; set; }
    public string? Authority { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

public static class SystemEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static (string Version, string InformationalVersion, string? GitCommit) ResolveBuildVersionInfo()
    {
        var asm = typeof(Program).Assembly;
        var rawInfo = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var envSha = Environment.GetEnvironmentVariable("MANAGERR_GIT_SHA")?.Trim();

        string cleanVersion;
        string? gitCommit = null;

        if (!string.IsNullOrWhiteSpace(rawInfo))
        {
            var plusIdx = rawInfo.IndexOf('+', StringComparison.Ordinal);
            if (plusIdx > 0)
            {
                cleanVersion = rawInfo[..plusIdx].Trim();
                var suffix = rawInfo[(plusIdx + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(suffix))
                {
                    gitCommit = suffix;
                }
            }
            else
            {
                cleanVersion = rawInfo.Trim();
            }
        }
        else
        {
            var asmVer = asm.GetName().Version;
            cleanVersion = asmVer is not null
                ? $"{asmVer.Major}.{asmVer.Minor}.{Math.Max(0, asmVer.Build)}"
                : "0.2.0";
        }

        if (!string.IsNullOrWhiteSpace(envSha))
        {
            gitCommit = envSha;
        }

        var informationalVersion = !string.IsNullOrWhiteSpace(gitCommit)
            ? $"{cleanVersion}+{gitCommit}"
            : cleanVersion;

        return (cleanVersion, informationalVersion, gitCommit);
    }

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var sys = app.MapGroup("/api/v1/system").WithTags("System, SQLite Storage & Audit");

        sys.MapGet("/health", async (
            IPlexAuthAndSessionService authService,
            CancellationToken ct) =>
        {
            var (isClaimed, boundServerName, _) = await authService.GetSetupStatusAsync(ct);
            var (version, informationalVersion, gitCommit) = ResolveBuildVersionInfo();
            return Results.Ok(new
            {
                status = "healthy",
                version,
                informationalVersion,
                gitCommit,
                databaseEngine = "sqlite-wal-embedded",
                externalDatabaseRequired = false,
                isClaimed,
                boundServerName,
                timestampUtc = DateTimeOffset.UtcNow,
            });
        }).AllowAnonymous();

        sys.MapGet("/health/checks", async (
            ITaskManagerService taskManager,
            CancellationToken ct) =>
        {
            var checks = await taskManager.GetSystemHealthChecksAsync(ct);
            return Results.Ok(checks);
        }).RequireScope(PermissionScopes.QueueRead);

        sys.MapPost("/health/resolve/{actionKey}", async (
            string actionKey,
            HttpContext httpContext,
            ITaskManagerService taskManager,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            try
            {
                var report = await taskManager.ResolveHealthIssueAsync(actionKey, ct);
                await auditLog.RecordAsync(new AuditWriteRequest(
                    Action: "system.health.resolved",
                    ResourceType: "health_check",
                    ResourceId: report.ActionKey,
                    Details: new { report.ActionKey, report.UpdatedCount, report.Message },
                    ActorUserId: principal.User?.Id,
                    ActorUsername: principal.User?.Username,
                    TraceId: httpContext.TraceIdentifier), ct);
                return Results.Ok(report);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        }).RequireAuthorization(policy =>
        {
            policy.AddAuthenticationSchemes(ManagerrAuthConstants.SchemeName);
            policy.RequireAuthenticatedUser();
            policy.RequireClaim("scope", PermissionScopes.CatalogWrite, PermissionScopes.SystemAdmin);
        });

        sys.MapGet("/retention", async (
            ITaskManagerService taskManager,
            CancellationToken ct) =>
        {
            var policy = await taskManager.GetRetentionPolicyAsync(ct);
            return Results.Ok(policy);
        }).RequireAuthorization(policy =>
        {
            policy.AddAuthenticationSchemes(ManagerrAuthConstants.SchemeName);
            policy.RequireAuthenticatedUser();
            policy.RequireClaim("scope", PermissionScopes.SystemAdmin, PermissionScopes.AuditRead);
        });

        sys.MapPut("/retention", async (
            UpdateSystemRetentionPolicyRequest req,
            HttpContext httpContext,
            ITaskManagerService taskManager,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            var updated = await taskManager.UpdateRetentionPolicyAsync(req, ct);
            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "system.retention.updated",
                ResourceType: "retention_policy",
                ResourceId: SystemRetentionAndHealthService.RetentionPolicySettingKey,
                Details: updated,
                ActorUserId: principal.User?.Id,
                ActorUsername: principal.User?.Username,
                TraceId: httpContext.TraceIdentifier), ct);
            return Results.Ok(updated);
        }).RequireScope(PermissionScopes.SystemAdmin);

        sys.MapPost("/retention/run", async (
            HttpContext httpContext,
            ITaskManagerService taskManager,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            var forceVacuum = false;
            if (httpContext.Request.ContentLength is > 0 &&
                httpContext.Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
            {
                try
                {
                    var body = await JsonSerializer.DeserializeAsync<RunSystemRetentionRequest>(httpContext.Request.Body, JsonOpts, ct);
                    forceVacuum = body?.ForceVacuum ?? false;
                }
                catch
                {
                    // Treat malformed or empty body as default run
                }
            }

            var summary = await taskManager.RunRetentionCleanupAsync(forceVacuum, ct);
            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "system.retention.executed",
                ResourceType: "retention_policy",
                ResourceId: SystemRetentionAndHealthService.RetentionPolicySettingKey,
                Details: summary,
                ActorUserId: principal.User?.Id,
                ActorUsername: principal.User?.Username,
                TraceId: httpContext.TraceIdentifier), ct);
            return Results.Ok(summary);
        }).RequireScope(PermissionScopes.SystemAdmin);

        sys.MapGet("/tasks", async (
            ITaskManagerService taskManager,
            CancellationToken ct) =>
        {
            var tasks = await taskManager.ListTasksAsync(ct);
            return Results.Ok(tasks);
        }).RequireScope(PermissionScopes.QueueRead);

        sys.MapPost("/tasks/{taskKey}/run", async (
            string taskKey,
            HttpContext httpContext,
            ITaskManagerService taskManager,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            try
            {
                var summary = await taskManager.RunTaskNowAsync(taskKey, ct);
                await auditLog.RecordAsync(new AuditWriteRequest(
                    Action: "system.task.executed",
                    ResourceType: "scheduled_task",
                    ResourceId: summary.TaskKey,
                    Details: new { summary.TaskKey, summary.LastStatus, summary.LastDurationMs, summary.LastMessage },
                    ActorUserId: principal.User?.Id,
                    ActorUsername: principal.User?.Username,
                    TraceId: httpContext.TraceIdentifier), ct);
                return Results.Ok(summary);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        }).RequireScope(PermissionScopes.QueueManage);

        sys.MapPatch("/tasks/{taskKey}", async (
            string taskKey,
            UpdateScheduledTaskRequest req,
            HttpContext httpContext,
            ITaskManagerService taskManager,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            try
            {
                var summary = await taskManager.UpdateTaskScheduleAsync(taskKey, req.IntervalSeconds, req.IsEnabled, ct);
                await auditLog.RecordAsync(new AuditWriteRequest(
                    Action: "system.task.updated",
                    ResourceType: "scheduled_task",
                    ResourceId: summary.TaskKey,
                    Details: new { summary.TaskKey, summary.IntervalSeconds, summary.IsEnabled },
                    ActorUserId: principal.User?.Id,
                    ActorUsername: principal.User?.Username,
                    IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                    TraceId: httpContext.TraceIdentifier), ct);
                return Results.Ok(summary);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        }).RequireScope(PermissionScopes.SystemAdmin);

        sys.MapGet("/metrics", async (ISqliteDbFactory dbFactory, CancellationToken ct) =>
        {
            var stats = await dbFactory.GetStorageStatsAsync(ct);
            var sb = new StringBuilder();
            sb.AppendLine("# HELP managerr_up Whether the Managerr modular monolith is healthy.");
            sb.AppendLine("# TYPE managerr_up gauge");
            sb.AppendLine("managerr_up 1");
            sb.AppendLine("# HELP managerr_sqlite_size_bytes Embedded SQLite database file size in bytes.");
            sb.AppendLine("# TYPE managerr_sqlite_size_bytes gauge");
            foreach (var s in stats)
            {
                sb.AppendLine($"managerr_sqlite_size_bytes{{db=\"{s.Kind}\"}} {s.SizeBytes}");
                sb.AppendLine($"managerr_sqlite_wal_size_bytes{{db=\"{s.Kind}\"}} {s.WalSizeBytes}");
            }
            return Results.Text(sb.ToString(), "text/plain; version=0.0.4; charset=utf-8");
        }).RequireScope(PermissionScopes.MetricsRead);

        sys.MapGet("/storage", async (
            ISqliteDbFactory dbFactory,
            IAppPaths appPaths,
            ISecretProtector secretProtector,
            CancellationToken ct) =>
        {
            var databases = await dbFactory.GetStorageStatsAsync(ct);
            return Results.Ok(new
            {
                configDirectory = appPaths.ConfigDirectory,
                backupsDirectory = appPaths.BackupsDirectory,
                envelopeMasterKeyId = secretProtector.KeyId,
                externalDatabaseRequired = false,
                databases,
            });
        }).RequireScope(PermissionScopes.SystemAdmin);

        sys.MapGet("/backups", (ISqliteBackupService backupService) =>
        {
            var backups = backupService.ListBackups();
            return Results.Ok(backups);
        }).RequireScope(PermissionScopes.SystemAdmin);

        sys.MapPost("/backups", async (
            TriggerBackupRequest? req,
            HttpContext httpContext,
            ISqliteBackupService backupService,
            IAppSettingsService appSettings,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            var maxRetained = await GetEffectiveMaxRetainedBackupsAsync(appSettings, ct);
            var backup = await backupService.CreateHotBackupAsync(req?.Reason ?? "manual", maxRetainedBackups: maxRetained, ct);

            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "system.backup.created",
                ResourceType: "sqlite_backup",
                ResourceId: backup.FileName,
                Details: new { backup.FileName, backup.SizeBytes },
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                TraceId: httpContext.TraceIdentifier), ct);

            return Results.Created($"/api/v1/system/backups/{backup.FileName}", backup);
        }).RequireScope(PermissionScopes.SystemAdmin);

        sys.MapGet("/backups/settings", async (
            IAppSettingsService appSettings,
            CancellationToken ct) =>
        {
            var maxRetainedBackups = await GetEffectiveMaxRetainedBackupsAsync(appSettings, ct);
            return Results.Ok(new { maxRetainedBackups });
        }).RequireScope(PermissionScopes.SystemAdmin);

        sys.MapPut("/backups/settings", async (
            UpdateBackupSettingsRequest req,
            HttpContext httpContext,
            IAppSettingsService appSettings,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            var maxRetainedBackups = Math.Clamp(req.MaxRetainedBackups, 1, 100);
            await appSettings.SetAsync("system.backups.max_retained", maxRetainedBackups.ToString(), isSecret: false, ct);

            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "system.backups.settings_updated",
                ResourceType: "system_settings",
                ResourceId: "backups",
                Details: new { maxRetainedBackups },
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                TraceId: httpContext.TraceIdentifier), ct);

            return Results.Ok(new { maxRetainedBackups });
        }).RequireScope(PermissionScopes.SystemAdmin);

        sys.MapGet("/audit", async (
            int? limit,
            string? action,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var events = await auditLog.ListRecentAsync(limit ?? 100, action, ct);
            return Results.Ok(events);
        }).RequireScope(PermissionScopes.AuditRead);

        sys.MapGet("/search", async (
            string? q,
            string? mediaType,
            int? limit,
            IFtsSearchService ftsSearch,
            CancellationToken ct) =>
        {
            var hits = await ftsSearch.SearchAsync(q ?? "", mediaType, limit ?? 25, ct);
            return Results.Ok(hits);
        }).RequireScope(PermissionScopes.CatalogRead);

        var general = app.MapGroup("/api/v1/settings/general").WithTags("General Settings");

        general.MapGet("", async (
            HttpContext httpContext,
            IPlexAuthAndSessionService authService,
            IAppSettingsService appSettings,
            IConfigSourceInspector inspector,
            IConfiguration configuration,
            IOptions<ManagerrUiOptions> uiOptions,
            CancellationToken ct) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var principal = httpContext.GetRequiredPrincipal();
            if (CheckScopedKeyForbidden(principal, httpContext) is { } forbidden)
            {
                return forbidden;
            }

            return await BuildGeneralSettingsResultAsync(authService, appSettings, inspector, configuration, uiOptions.Value, ct);
        }).RequireScope(PermissionScopes.SystemAdmin);

        general.MapPut("", async (
            UpdateGeneralSettingsRequest? req,
            HttpContext httpContext,
            IPlexAuthAndSessionService authService,
            IAppSettingsService appSettings,
            IConfigSourceInspector inspector,
            IConfiguration configuration,
            IMemoryCache memoryCache,
            IOptions<ManagerrUiOptions> uiOptions,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var principal = httpContext.GetRequiredPrincipal();
            if (CheckScopedKeyForbidden(principal, httpContext) is { } forbidden)
            {
                return forbidden;
            }

            // 1. Validation
            string? normTheme = null;
            if (!string.IsNullOrWhiteSpace(req?.DefaultTheme))
            {
                normTheme = NormalizeThemeOrNull(req.DefaultTheme);
                if (normTheme is null)
                {
                    return InvalidThemeOrAccentError("defaultTheme must be one of: system, dark, oled, light.", httpContext);
                }
            }

            string? normAccent = null;
            if (!string.IsNullOrWhiteSpace(req?.AccentColor))
            {
                normAccent = NormalizeHexColorOrNull(req.AccentColor);
                if (normAccent is null)
                {
                    return InvalidThemeOrAccentError("accentColor must be a 6-digit hex color (#RRGGBB).", httpContext);
                }
            }

            string? normScheme = null;
            if (req?.ColorScheme is not null)
            {
                normScheme = NormalizeColorSchemeOrNull(req.ColorScheme);
                if (normScheme is null)
                {
                    return InvalidThemeOrAccentError("colorScheme must be one of: seafoam_jade, royal_amethyst, cyber_chartreuse, brushed_copper, nordic_periwinkle, custom.", httpContext);
                }
            }

            (ManagerrCustomThemePayload? NormalizedPayload, string? SerializedJson, IResult? ErrorResult) customThemeResult = default;
            if (req?.CustomTheme is not null)
            {
                customThemeResult = ValidateAndSerializeCustomTheme(req.CustomTheme, httpContext);
                if (customThemeResult.ErrorResult is not null)
                {
                    return customThemeResult.ErrorResult;
                }
            }

            string? validatedBoundServerUrl = null;
            if (req?.BoundServerUrl is not null)
            {
                if (!TryNormalizeOptionalHttpUrl(req.BoundServerUrl, out validatedBoundServerUrl))
                {
                    return ValidationError("boundServerUrl must be empty or a valid http(s) URL.", httpContext);
                }
            }

            string[]? validatedCidrs = null;
            if (req?.TrustedProxyCidrs is not null)
            {
                if (!TryValidateCidrs(req.TrustedProxyCidrs, out validatedCidrs, out var badCidr))
                {
                    return ValidationError($"Invalid trusted proxy CIDR or IP address: '{badCidr}'.", httpContext);
                }
            }

            string[]? validatedAllowedHosts = null;
            if (req?.AllowedHosts is not null)
            {
                if (!TryValidateAllowedHosts(req.AllowedHosts, out validatedAllowedHosts, out var badHost))
                {
                    return ValidationError($"Invalid allowed host entry: '{badHost}'.", httpContext);
                }
            }

            var currentSecurity = await ResolveEffectiveSecurityProxySettingsAsync(appSettings, inspector, configuration, ct);
            if (req?.TrustForwardedHeaders.HasValue == true
                && currentSecurity.TfhManagedBy != "database"
                && req.TrustForwardedHeaders.Value != currentSecurity.TrustForwardedHeaders)
            {
                return SettingLockedError("security.trustForwardedHeaders", currentSecurity.TfhManagedBy, httpContext);
            }
            if (validatedCidrs is not null
                && currentSecurity.CidrsManagedBy != "database"
                && !StringSetsEqual(validatedCidrs, currentSecurity.TrustedProxyCidrs))
            {
                return SettingLockedError("security.trustedProxyCidrs", currentSecurity.CidrsManagedBy, httpContext);
            }
            if (validatedAllowedHosts is not null
                && currentSecurity.AllowedHostsManagedBy != "database"
                && !StringSetsEqual(validatedAllowedHosts, currentSecurity.AllowedHosts))
            {
                return SettingLockedError("security.allowedHosts", currentSecurity.AllowedHostsManagedBy, httpContext);
            }
            var mcpManagedBy = inspector.GetSource("Managerr:Mcp:Enabled");
            if (req?.McpEnabled.HasValue == true)
            {
                var currentMcpEnabled = await ResolveEffectiveMcpEnabledAsync(appSettings, configuration, mcpManagedBy, ct);
                if (mcpManagedBy != "database" && req.McpEnabled.Value != currentMcpEnabled)
                {
                    return SettingLockedError("mcp.enabled", mcpManagedBy, httpContext);
                }
            }

            // 2. Persistence
            string cleanName;
            if (req?.InstanceName is not null)
            {
                cleanName = SanitizeInstanceName(req.InstanceName);
                await appSettings.SetAsync(GeneralSettingsKeys.InstanceName, cleanName, isSecret: false, ct);
            }
            else
            {
                var existingName = await appSettings.GetAsync(GeneralSettingsKeys.InstanceName, ct);
                cleanName = string.IsNullOrWhiteSpace(existingName) ? "Managerr" : existingName;
            }

            if (normTheme is not null && inspector.GetSource("ui.default_theme") == "database")
            {
                await appSettings.SetAsync("ui.default_theme", normTheme, isSecret: false, ct);
            }
            if (normAccent is not null && inspector.GetSource("ui.accent_color") == "database")
            {
                await appSettings.SetAsync("ui.accent_color", normAccent, isSecret: false, ct);
            }
            if (normScheme is not null && inspector.GetSource("ui.color_scheme") == "database")
            {
                await appSettings.SetAsync("ui.color_scheme", normScheme, isSecret: false, ct);
            }

            if (req?.ClearCustomTheme == true)
            {
                await appSettings.SetAsync("ui.custom_theme_json", "", isSecret: false, ct);
            }
            else if (customThemeResult.SerializedJson is not null)
            {
                await appSettings.SetAsync("ui.custom_theme_json", customThemeResult.SerializedJson, isSecret: false, ct);

                if (string.IsNullOrWhiteSpace(req?.AccentColor) &&
                    inspector.GetSource("ui.accent_color") == "database" &&
                    customThemeResult.NormalizedPayload?.Tokens is not null &&
                    customThemeResult.NormalizedPayload.Tokens.TryGetValue("accent", out var customAccent))
                {
                    await appSettings.SetAsync("ui.accent_color", customAccent, isSecret: false, ct);
                }
            }

            if (validatedBoundServerUrl is not null && inspector.GetSource("plex.bound_server_url") == "database")
            {
                await appSettings.SetAsync("plex.bound_server_url", validatedBoundServerUrl, isSecret: false, ct);
            }

            var forwardedHeadersChanged = false;
            if (req?.TrustForwardedHeaders.HasValue == true && currentSecurity.TfhManagedBy == "database")
            {
                var val = req.TrustForwardedHeaders.Value.ToString().ToLowerInvariant();
                await appSettings.SetAsync("system.security.trust_forwarded_headers", val, isSecret: false, ct);
                forwardedHeadersChanged = true;
            }
            if (validatedCidrs is not null && currentSecurity.CidrsManagedBy == "database")
            {
                var json = JsonSerializer.Serialize(validatedCidrs, JsonOpts);
                await appSettings.SetAsync("system.security.trusted_proxy_cidrs", json, isSecret: false, ct);
                forwardedHeadersChanged = true;
            }
            if (validatedAllowedHosts is not null && currentSecurity.AllowedHostsManagedBy == "database")
            {
                var json = JsonSerializer.Serialize(validatedAllowedHosts, JsonOpts);
                await appSettings.SetAsync("system.security.allowed_hosts", json, isSecret: false, ct);
                forwardedHeadersChanged = true;
            }
            if (forwardedHeadersChanged)
            {
                ConditionalForwardedHeadersMiddleware.InvalidateCache(memoryCache);
            }

            if (req?.McpEnabled.HasValue == true && mcpManagedBy == "database")
            {
                await appSettings.SetAsync("system.mcp.enabled", req.McpEnabled.Value.ToString().ToLowerInvariant(), isSecret: false, ct);
            }

            if (req?.RollingQuotaWindowDays.HasValue == true)
            {
                var clampedDays = Math.Clamp(req.RollingQuotaWindowDays.Value, 1, 365);
                await appSettings.SetAsync("system.quotas.rolling_window_days", clampedDays.ToString(), isSecret: false, ct);
            }

            if (req?.MaxRetainedBackups.HasValue == true)
            {
                var clampedBackups = Math.Clamp(req.MaxRetainedBackups.Value, 1, 100);
                await appSettings.SetAsync("system.backups.max_retained", clampedBackups.ToString(), isSecret: false, ct);
            }

            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "system.general.updated",
                ResourceType: "system_settings",
                ResourceId: "general",
                Details: new { InstanceName = cleanName },
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                TraceId: httpContext.TraceIdentifier), ct);

            return await BuildGeneralSettingsResultAsync(authService, appSettings, inspector, configuration, uiOptions.Value, ct);
        }).RequireScope(PermissionScopes.SystemAdmin);

        general.MapPost("/api-key/reset", async (
            HttpContext httpContext,
            IPlexAuthAndSessionService authService,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var principal = httpContext.GetRequiredPrincipal();
            if (CheckScopedKeyForbidden(principal, httpContext) is { } forbidden)
            {
                return forbidden;
            }

            var newKey = await authService.ResetInstanceApiKeyAsync(ct);
            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "system.general.apikey.reset",
                ResourceType: "system_settings",
                ResourceId: "api_key",
                Details: new { MaskedHint = $"****{newKey[^4..]}" },
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                TraceId: httpContext.TraceIdentifier), ct);

            return Results.Ok(new { apiKey = newKey });
        }).RequireScope(PermissionScopes.SystemAdmin);

        var authSettingsGroup = app.MapGroup("/api/v1/settings/auth").WithTags("Auth Settings");

        authSettingsGroup.MapGet("", async (
            HttpContext httpContext,
            IPlexAuthAndSessionService authService,
            IOidcAuthenticationService oidcService,
            IAppSettingsService appSettings,
            IConfigSourceInspector inspector,
            IConfiguration configuration,
            IOptions<ManagerrAuthOptions> authOptions,
            IOptions<ManagerrUiOptions> uiOptions,
            CancellationToken ct) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var principal = httpContext.GetRequiredPrincipal();
            if (CheckScopedKeyForbidden(principal, httpContext) is { } forbidden) return forbidden;

            return await BuildAuthSettingsResultAsync(httpContext, oidcService, appSettings, inspector, configuration, authOptions, uiOptions, ct);
        }).RequireScope(PermissionScopes.SystemAdmin);

        authSettingsGroup.MapPut("", async (
            UpdateAuthSettingsRequest req,
            HttpContext httpContext,
            IPlexAuthAndSessionService authService,
            IOidcAuthenticationService oidcService,
            IAppSettingsService appSettings,
            IConfigSourceInspector inspector,
            IConfiguration configuration,
            IMemoryCache memoryCache,
            IOptions<ManagerrAuthOptions> authOptions,
            IOptions<ManagerrUiOptions> uiOptions,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var principal = httpContext.GetRequiredPrincipal();
            if (CheckScopedKeyForbidden(principal, httpContext) is { } forbidden) return forbidden;

            var managedBy = BuildAuthManagedBy(inspector);
            var oidcConfig = await oidcService.GetEffectiveConfigAsync(ct);

            var effLocalEnabled = req.Local?.Enabled ?? req.LocalAuthEnabled;
            var effPasskeysEnabled = req.Passkeys?.Enabled ?? req.PasskeysEnabled;
            var effOidcEnabled = req.Oidc?.Enabled ?? req.OidcEnabled;
            var effOidcDisplayName = req.Oidc?.DisplayName ?? req.OidcDisplayName;
            var effOidcIssuer = req.Oidc?.Issuer ?? req.OidcAuthority;
            var effOidcClientId = req.Oidc?.ClientId ?? req.OidcClientId;
            var effOidcClientSecret = req.Oidc?.ClientSecret ?? req.OidcClientSecret;
            var effOidcScopes = req.Oidc?.Scopes ?? req.OidcScope;
            var effOidcAutoRedirect = req.Oidc?.AutoRedirect ?? req.OidcAutoRedirect;
            var effDefaultTheme = req.Ui?.DefaultTheme ?? req.ThemeMode;
            var effAccentColor = req.Ui?.AccentColor ?? req.AccentColor;
            var effColorScheme = req.Ui?.ColorScheme ?? req.ColorScheme;
            var effTrustForwardedHeaders = req.Security?.TrustForwardedHeaders ?? req.TrustForwardedHeaders;
            var effTrustedProxyCidrs = req.Security?.TrustedProxyCidrs ?? req.TrustedProxyCidrs;
            var effAllowedHosts = req.Security?.AllowedHosts ?? req.AllowedHosts;
            var effMcpEnabled = req.Mcp?.Enabled ?? req.McpEnabled;

            // Validate URL, CIDRs & AllowedHosts before persisting
            string? validatedBoundServerUrl = null;
            if (req.Plex?.BoundServerUrl is not null)
            {
                if (!TryNormalizeOptionalHttpUrl(req.Plex.BoundServerUrl, out validatedBoundServerUrl))
                {
                    return ValidationError("plex.boundServerUrl must be empty or a valid http(s) URL.", httpContext);
                }
            }

            string[]? validatedCidrs = null;
            if (effTrustedProxyCidrs is not null)
            {
                if (!TryValidateCidrs(effTrustedProxyCidrs, out validatedCidrs, out var badCidr))
                {
                    return ValidationError($"Invalid trusted proxy CIDR or IP address: '{badCidr}'.", httpContext);
                }
            }

            string[]? validatedAllowedHosts = null;
            if (effAllowedHosts is not null)
            {
                if (!TryValidateAllowedHosts(effAllowedHosts, out validatedAllowedHosts, out var badHost))
                {
                    return ValidationError($"Invalid allowed host entry: '{badHost}'.", httpContext);
                }
            }

            // Reject modifying locked fields
            if (managedBy["oidc.issuer"] != "database" && effOidcIssuer != null && effOidcIssuer != (oidcConfig.Issuer ?? ""))
                return SettingLockedError("oidc.issuer", managedBy["oidc.issuer"], httpContext);
            if (managedBy["oidc.clientId"] != "database" && effOidcClientId != null && effOidcClientId != (oidcConfig.ClientId ?? ""))
                return SettingLockedError("oidc.clientId", managedBy["oidc.clientId"], httpContext);
            if (managedBy["oidc.clientSecret"] != "database" && !string.IsNullOrWhiteSpace(effOidcClientSecret))
                return SettingLockedError("oidc.clientSecret", managedBy["oidc.clientSecret"], httpContext);
            if (managedBy["oidc.enabled"] != "database" && effOidcEnabled.HasValue && effOidcEnabled.Value != oidcConfig.Enabled)
                return SettingLockedError("oidc.enabled", managedBy["oidc.enabled"], httpContext);

            var currentSecurity = await ResolveEffectiveSecurityProxySettingsAsync(appSettings, inspector, configuration, ct);
            if (effTrustForwardedHeaders.HasValue
                && managedBy["security.trustForwardedHeaders"] != "database"
                && effTrustForwardedHeaders.Value != currentSecurity.TrustForwardedHeaders)
                return SettingLockedError("security.trustForwardedHeaders", managedBy["security.trustForwardedHeaders"], httpContext);
            if (validatedCidrs is not null
                && managedBy["security.trustedProxyCidrs"] != "database"
                && !StringSetsEqual(validatedCidrs, currentSecurity.TrustedProxyCidrs))
                return SettingLockedError("security.trustedProxyCidrs", managedBy["security.trustedProxyCidrs"], httpContext);
            if (validatedAllowedHosts is not null
                && managedBy["security.allowedHosts"] != "database"
                && !StringSetsEqual(validatedAllowedHosts, currentSecurity.AllowedHosts))
                return SettingLockedError("security.allowedHosts", managedBy["security.allowedHosts"], httpContext);
            if (effMcpEnabled.HasValue && managedBy["mcp.enabled"] != "database")
            {
                var currentMcpEnabled = await ResolveEffectiveMcpEnabledAsync(appSettings, configuration, managedBy["mcp.enabled"], ct);
                if (effMcpEnabled.Value != currentMcpEnabled)
                    return SettingLockedError("mcp.enabled", managedBy["mcp.enabled"], httpContext);
            }

            // Save unlocked database fields
            if (managedBy["local.enabled"] == "database" && effLocalEnabled.HasValue)
                await appSettings.SetAsync("auth.local.enabled", effLocalEnabled.Value.ToString().ToLowerInvariant(), isSecret: false, ct);
            if (managedBy["passkeys.enabled"] == "database" && effPasskeysEnabled.HasValue)
                await appSettings.SetAsync("auth.passkeys.enabled", effPasskeysEnabled.Value.ToString().ToLowerInvariant(), isSecret: false, ct);

            if (req.Plex != null)
            {
                if (managedBy["plex.enabled"] == "database" && req.Plex.Enabled.HasValue)
                    await appSettings.SetAsync("auth.plex.enabled", req.Plex.Enabled.Value.ToString().ToLowerInvariant(), isSecret: false, ct);
                if (managedBy["plex.boundServerUrl"] == "database" && validatedBoundServerUrl != null)
                    await appSettings.SetAsync("plex.bound_server_url", validatedBoundServerUrl, isSecret: false, ct);
            }

            if (managedBy["oidc.enabled"] == "database" && effOidcEnabled.HasValue)
                await appSettings.SetAsync(OidcAuthenticationService.SettingKeyEnabled, effOidcEnabled.Value.ToString().ToLowerInvariant(), isSecret: false, ct);
            if (managedBy["oidc.displayName"] == "database" && effOidcDisplayName != null)
                await appSettings.SetAsync(OidcAuthenticationService.SettingKeyDisplayName, effOidcDisplayName, isSecret: false, ct);
            if (managedBy["oidc.issuer"] == "database" && effOidcIssuer != null)
                await appSettings.SetAsync(OidcAuthenticationService.SettingKeyIssuer, effOidcIssuer, isSecret: false, ct);
            if (managedBy["oidc.clientId"] == "database" && effOidcClientId != null)
                await appSettings.SetAsync(OidcAuthenticationService.SettingKeyClientId, effOidcClientId, isSecret: false, ct);
            if (managedBy["oidc.clientSecret"] == "database" && !string.IsNullOrWhiteSpace(effOidcClientSecret))
                await appSettings.SetAsync(OidcAuthenticationService.SettingKeyClientSecretEnc, effOidcClientSecret, isSecret: true, ct);
            if (managedBy["oidc.scopes"] == "database" && effOidcScopes != null)
                await appSettings.SetAsync(OidcAuthenticationService.SettingKeyScopes, effOidcScopes, isSecret: false, ct);
            if (managedBy["oidc.autoRedirect"] == "database" && effOidcAutoRedirect.HasValue)
                await appSettings.SetAsync(OidcAuthenticationService.SettingKeyAutoRedirect, effOidcAutoRedirect.Value.ToString().ToLowerInvariant(), isSecret: false, ct);

            if (req.Oidc != null)
            {
                if (managedBy["oidc.autoProvision"] == "database" && req.Oidc.AutoProvision.HasValue)
                    await appSettings.SetAsync(OidcAuthenticationService.SettingKeyAutoProvision, req.Oidc.AutoProvision.Value.ToString().ToLowerInvariant(), isSecret: false, ct);
                if (managedBy["oidc.trustVerifiedEmail"] == "database" && req.Oidc.TrustVerifiedEmail.HasValue)
                    await appSettings.SetAsync(OidcAuthenticationService.SettingKeyTrustVerifiedEmail, req.Oidc.TrustVerifiedEmail.Value.ToString().ToLowerInvariant(), isSecret: false, ct);
                if (managedBy["oidc.usernameClaim"] == "database" && req.Oidc.UsernameClaim != null)
                    await appSettings.SetAsync(OidcAuthenticationService.SettingKeyUsernameClaim, req.Oidc.UsernameClaim, isSecret: false, ct);
                if (managedBy["oidc.emailClaim"] == "database" && req.Oidc.EmailClaim != null)
                    await appSettings.SetAsync(OidcAuthenticationService.SettingKeyEmailClaim, req.Oidc.EmailClaim, isSecret: false, ct);
                if (managedBy["oidc.displayNameClaim"] == "database" && req.Oidc.DisplayNameClaim != null)
                    await appSettings.SetAsync(OidcAuthenticationService.SettingKeyDisplayNameClaim, req.Oidc.DisplayNameClaim, isSecret: false, ct);
                if (managedBy["oidc.groupsClaim"] == "database" && req.Oidc.GroupsClaim != null)
                    await appSettings.SetAsync(OidcAuthenticationService.SettingKeyGroupsClaim, req.Oidc.GroupsClaim, isSecret: false, ct);
                if (managedBy["oidc.adminGroups"] == "database" && req.Oidc.AdminGroups != null)
                    await appSettings.SetAsync(OidcAuthenticationService.SettingKeyAdminGroupsJson, JsonSerializer.Serialize(req.Oidc.AdminGroups, JsonOpts), isSecret: false, ct);
                if (managedBy["oidc.managerGroups"] == "database" && req.Oidc.ManagerGroups != null)
                    await appSettings.SetAsync(OidcAuthenticationService.SettingKeyManagerGroupsJson, JsonSerializer.Serialize(req.Oidc.ManagerGroups, JsonOpts), isSecret: false, ct);
                if (managedBy["oidc.requesterGroups"] == "database" && req.Oidc.RequesterGroups != null)
                    await appSettings.SetAsync(OidcAuthenticationService.SettingKeyRequesterGroupsJson, JsonSerializer.Serialize(req.Oidc.RequesterGroups, JsonOpts), isSecret: false, ct);
            }

            if (req.Passkeys != null)
            {
                if (managedBy["passkeys.rpId"] == "database" && req.Passkeys.RpId != null)
                    await appSettings.SetAsync("auth.passkeys.rp_id", req.Passkeys.RpId.Trim(), isSecret: false, ct);
                if (managedBy["passkeys.rpName"] == "database" && req.Passkeys.RpName != null)
                    await appSettings.SetAsync("auth.passkeys.rp_name", req.Passkeys.RpName.Trim(), isSecret: false, ct);
                if (managedBy["passkeys.requireUserVerification"] == "database" && req.Passkeys.RequireUserVerification.HasValue)
                    await appSettings.SetAsync("auth.passkeys.require_user_verification", req.Passkeys.RequireUserVerification.Value.ToString().ToLowerInvariant(), isSecret: false, ct);
            }

            var forwardedHeadersChanged = false;
            if (effTrustForwardedHeaders.HasValue && managedBy["security.trustForwardedHeaders"] == "database")
            {
                var val = effTrustForwardedHeaders.Value.ToString().ToLowerInvariant();
                await appSettings.SetAsync("system.security.trust_forwarded_headers", val, isSecret: false, ct);
                forwardedHeadersChanged = true;
            }
            if (validatedCidrs != null && managedBy["security.trustedProxyCidrs"] == "database")
            {
                var json = JsonSerializer.Serialize(validatedCidrs, JsonOpts);
                await appSettings.SetAsync("system.security.trusted_proxy_cidrs", json, isSecret: false, ct);
                forwardedHeadersChanged = true;
            }
            if (validatedAllowedHosts != null && managedBy["security.allowedHosts"] == "database")
            {
                var json = JsonSerializer.Serialize(validatedAllowedHosts, JsonOpts);
                await appSettings.SetAsync("system.security.allowed_hosts", json, isSecret: false, ct);
                forwardedHeadersChanged = true;
            }
            if (forwardedHeadersChanged)
            {
                ConditionalForwardedHeadersMiddleware.InvalidateCache(memoryCache);
            }

            if (effMcpEnabled.HasValue && managedBy["mcp.enabled"] == "database")
            {
                await appSettings.SetAsync("system.mcp.enabled", effMcpEnabled.Value.ToString().ToLowerInvariant(), isSecret: false, ct);
            }

            if (managedBy["ui.defaultTheme"] == "database" && effDefaultTheme != null)
            {
                var normTheme = NormalizeThemeOrNull(effDefaultTheme);
                if (normTheme is null)
                {
                    return InvalidThemeOrAccentError("defaultTheme must be one of: system, dark, oled, light.", httpContext);
                }
                await appSettings.SetAsync("ui.default_theme", normTheme, isSecret: false, ct);
            }
            if (managedBy["ui.accentColor"] == "database" && effAccentColor != null)
            {
                var normAccent = NormalizeHexColorOrNull(effAccentColor);
                if (normAccent is null)
                {
                    return InvalidThemeOrAccentError("accentColor must be a 6-digit hex color (#RRGGBB).", httpContext);
                }
                await appSettings.SetAsync("ui.accent_color", normAccent, isSecret: false, ct);
            }
            if (managedBy.TryGetValue("ui.colorScheme", out var csSource) && csSource == "database" && effColorScheme != null)
            {
                var normScheme = NormalizeColorSchemeOrNull(effColorScheme);
                if (normScheme is null)
                {
                    return InvalidThemeOrAccentError("colorScheme must be one of: seafoam_jade, royal_amethyst, cyber_chartreuse, brushed_copper, nordic_periwinkle, custom.", httpContext);
                }
                await appSettings.SetAsync("ui.color_scheme", normScheme, isSecret: false, ct);
            }

            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "settings.auth.updated",
                ResourceType: "system_settings",
                ResourceId: "auth",
                Details: new { UpdatedBy = principal.User?.Username },
                ActorUserId: principal.User?.Id,
                ActorUsername: principal.User?.Username,
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                TraceId: httpContext.TraceIdentifier), ct);

            return await BuildAuthSettingsResultAsync(httpContext, oidcService, appSettings, inspector, configuration, authOptions, uiOptions, ct);
        }).RequireScope(PermissionScopes.SystemAdmin);

        var handleTestOidc = async (
            TestOidcSettingsRequest? req,
            HttpContext httpContext,
            IOidcAuthenticationService oidcService,
            CancellationToken ct) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var principal = httpContext.GetRequiredPrincipal();
            if (CheckScopedKeyForbidden(principal, httpContext) is { } forbidden) return forbidden;

            var targetIssuer = !string.IsNullOrWhiteSpace(req?.Issuer) ? req!.Issuer : req?.Authority;
            var res = await oidcService.TestConnectionAsync(targetIssuer, req?.ClientId, req?.ClientSecret, ct);
            return Results.Ok(new
            {
                success = res.Success,
                error = res.Error,
                issuer = res.Issuer,
                authorizationEndpoint = res.AuthorizationEndpoint,
                tokenEndpoint = res.TokenEndpoint,
                jwksUri = res.JwksUri
            });
        };

        authSettingsGroup.MapPost("/oidc/test", handleTestOidc).RequireScope(PermissionScopes.SystemAdmin);
        authSettingsGroup.MapPost("/test-oidc", handleTestOidc).RequireScope(PermissionScopes.SystemAdmin);

        return app;
    }

    private static async Task<IResult> BuildGeneralSettingsResultAsync(
        IPlexAuthAndSessionService authService,
        IAppSettingsService appSettings,
        IConfigSourceInspector inspector,
        IConfiguration configuration,
        ManagerrUiOptions uiOptions,
        CancellationToken ct)
    {
        var apiKey = await authService.GetInstanceApiKeyAsync(ct);
        var rawInstanceName = await appSettings.GetAsync(GeneralSettingsKeys.InstanceName, ct);
        var (_, boundServerName, _) = await authService.GetSetupStatusAsync(ct);
        var (uiTheme, uiAccent, uiColorScheme, uiCustomTheme) =
            await ResolveEffectiveUiSettingsAsync(appSettings, inspector, uiOptions, ct);

        var boundServerUrl = await ResolveEffectiveBoundServerUrlAsync(appSettings, inspector, configuration, ct);
        var (trustForwardedHeaders, trustedProxyCidrs, allowedHosts, tfhManagedBy, cidrsManagedBy, allowedHostsManagedBy) =
            await ResolveEffectiveSecurityProxySettingsAsync(appSettings, inspector, configuration, ct);
        var mcpManagedBy = inspector.GetSource("Managerr:Mcp:Enabled");
        var mcpEnabled = await ResolveEffectiveMcpEnabledAsync(appSettings, configuration, mcpManagedBy, ct);
        var rollingQuotaWindowDays = await GetEffectiveRollingQuotaWindowDaysAsync(appSettings, ct);
        var maxRetainedBackups = await GetEffectiveMaxRetainedBackupsAsync(appSettings, ct);

        return Results.Ok(new
        {
            instanceName = string.IsNullOrWhiteSpace(rawInstanceName) ? "Managerr" : rawInstanceName,
            authenticationMethod = "plex_primary_with_lan_breakglass",
            authenticationRequired = "enabled",
            apiKey,
            boundServerName,
            boundServerUrl,
            ui = new
            {
                defaultTheme = uiTheme,
                accentColor = uiAccent,
                colorScheme = uiColorScheme,
                customTheme = uiCustomTheme,
            },
            security = new
            {
                trustForwardedHeaders,
                trustedProxyCidrs,
                allowedHosts,
                managedBy = new
                {
                    trustForwardedHeaders = tfhManagedBy,
                    trustedProxyCidrs = cidrsManagedBy,
                    allowedHosts = allowedHostsManagedBy,
                },
            },
            mcp = new
            {
                enabled = mcpEnabled,
                managedBy = mcpManagedBy,
            },
            rollingQuotaWindowDays,
            backupSettings = new
            {
                maxRetainedBackups,
            },
            compatUrlBases = new
            {
                sonarr = "/compat/sonarr",
                radarr = "/compat/radarr",
                lidarr = "/compat/lidarr",
                readarr = "/compat/readarr",
                prowlarr = "/compat/prowlarr",
            },
        });
    }

    private static async Task<IResult> BuildAuthSettingsResultAsync(
        HttpContext httpContext,
        IOidcAuthenticationService oidcService,
        IAppSettingsService appSettings,
        IConfigSourceInspector inspector,
        IConfiguration configuration,
        IOptions<ManagerrAuthOptions> authOptions,
        IOptions<ManagerrUiOptions> uiOptions,
        CancellationToken ct)
    {
        var oidcConfig = await oidcService.GetEffectiveConfigAsync(ct);
        var managedBy = BuildAuthManagedBy(inspector);

        var dbRpId = await appSettings.GetAsync("auth.passkeys.rp_id", ct);
        var passkeyRpId = managedBy["passkeys.rpId"] == "database" && !string.IsNullOrWhiteSpace(dbRpId)
            ? dbRpId.Trim()
            : (!string.IsNullOrWhiteSpace(authOptions.Value.Passkeys.RpId) ? authOptions.Value.Passkeys.RpId : httpContext.Request.Host.Host);

        var dbRpName = await appSettings.GetAsync("auth.passkeys.rp_name", ct);
        var passkeyRpName = managedBy["passkeys.rpName"] == "database" && !string.IsNullOrWhiteSpace(dbRpName)
            ? dbRpName.Trim()
            : (!string.IsNullOrWhiteSpace(authOptions.Value.Passkeys.RpName) ? authOptions.Value.Passkeys.RpName : "Managerr");

        var dbRequireUv = await appSettings.GetAsync("auth.passkeys.require_user_verification", ct);
        var passkeyRequireUv = managedBy["passkeys.requireUserVerification"] == "database" && bool.TryParse(dbRequireUv, out var parsedUv)
            ? parsedUv
            : authOptions.Value.Passkeys.RequireUserVerification;

        var (defaultTheme, accentColor, colorScheme, customTheme) =
            await ResolveEffectiveUiSettingsAsync(appSettings, inspector, uiOptions.Value, ct);

        var localEnabled = managedBy["local.enabled"] != "database"
            ? authOptions.Value.Local.Enabled
            : (bool.TryParse(await appSettings.GetAsync("auth.local.enabled", ct), out var dbLocal) ? dbLocal : authOptions.Value.Local.Enabled);

        var passkeysEnabled = managedBy["passkeys.enabled"] != "database"
            ? authOptions.Value.Passkeys.Enabled
            : (bool.TryParse(await appSettings.GetAsync("auth.passkeys.enabled", ct), out var dbPk) ? dbPk : authOptions.Value.Passkeys.Enabled);

        var plexEnabled = managedBy["plex.enabled"] != "database"
            ? authOptions.Value.Plex.Enabled
            : (bool.TryParse(await appSettings.GetAsync("auth.plex.enabled", ct), out var dbPlex) ? dbPlex : authOptions.Value.Plex.Enabled);

        var boundServerUrl = await ResolveEffectiveBoundServerUrlAsync(appSettings, inspector, configuration, ct);
        var (trustForwardedHeaders, trustedProxyCidrs, allowedHosts, _, _, _) =
            await ResolveEffectiveSecurityProxySettingsAsync(appSettings, inspector, configuration, ct);
        var mcpEnabled = await ResolveEffectiveMcpEnabledAsync(appSettings, configuration, managedBy["mcp.enabled"], ct);

        var isOidcManagedByEnv = managedBy["oidc.issuer"] != "database"
            || managedBy["oidc.clientId"] != "database"
            || managedBy["oidc.clientSecret"] != "database"
            || managedBy["oidc.enabled"] != "database";

        return Results.Ok(new
        {
            local = new { enabled = localEnabled },
            passkeys = new
            {
                enabled = passkeysEnabled,
                rpId = passkeyRpId,
                rpName = passkeyRpName,
                requireUserVerification = passkeyRequireUv,
            },
            plex = new
            {
                enabled = plexEnabled,
                boundServerUrl,
            },
            oidc = new
            {
                enabled = oidcConfig.Enabled,
                displayName = oidcConfig.DisplayName,
                issuer = oidcConfig.Issuer,
                clientId = oidcConfig.ClientId,
                hasClientSecret = oidcConfig.HasClientSecret,
                scopes = oidcConfig.Scopes,
                autoProvision = oidcConfig.AutoProvision,
                autoRedirect = oidcConfig.AutoRedirect,
                trustVerifiedEmail = oidcConfig.TrustVerifiedEmail,
                usernameClaim = oidcConfig.UsernameClaim,
                emailClaim = oidcConfig.EmailClaim,
                displayNameClaim = oidcConfig.DisplayNameClaim,
                groupsClaim = oidcConfig.GroupsClaim,
                adminGroups = oidcConfig.AdminGroups,
                managerGroups = oidcConfig.ManagerGroups,
                requesterGroups = oidcConfig.RequesterGroups,
            },
            security = new
            {
                trustForwardedHeaders,
                trustedProxyCidrs,
                allowedHosts,
            },
            mcp = new
            {
                enabled = mcpEnabled,
            },
            ui = new { defaultTheme, accentColor, colorScheme, customTheme },
            managedBy,
            // Flat aliases for GeneralSettingsView
            localAuthEnabled = localEnabled,
            passkeysEnabled,
            oidcEnabled = oidcConfig.Enabled,
            oidcDisplayName = oidcConfig.DisplayName,
            oidcAuthority = oidcConfig.Issuer ?? "",
            oidcClientId = oidcConfig.ClientId ?? "",
            hasOidcClientSecret = oidcConfig.HasClientSecret,
            oidcScope = oidcConfig.Scopes,
            oidcAutoRedirect = oidcConfig.AutoRedirect,
            isOidcManagedByEnv,
            themeMode = defaultTheme,
            accentColor,
            colorScheme,
        });
    }

    private static IResult SettingLockedError(string field, string source, HttpContext httpContext) =>
        Results.Json(new
        {
            type = "https://managerr.dev/problems/err_setting_locked",
            title = "Setting is locked",
            status = StatusCodes.Status400BadRequest,
            detail = $"Setting '{field}' is managed by {source} and cannot be modified via API.",
            code = "ERR_SETTING_LOCKED",
            traceId = httpContext.TraceIdentifier
        }, contentType: "application/problem+json", statusCode: StatusCodes.Status400BadRequest);

    private static IResult ValidationError(string detail, HttpContext httpContext) =>
        Results.Json(new
        {
            type = "https://managerr.dev/problems/err_validation_failed",
            title = "Validation failed",
            status = StatusCodes.Status400BadRequest,
            detail,
            code = "ERR_VALIDATION_FAILED",
            traceId = httpContext.TraceIdentifier,
        }, contentType: "application/problem+json", statusCode: StatusCodes.Status400BadRequest);

    private static Dictionary<string, string> BuildAuthManagedBy(IConfigSourceInspector inspector) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["local.enabled"] = inspector.GetSource("local.enabled"),
            ["passkeys.enabled"] = inspector.GetSource("passkeys.enabled"),
            ["passkeys.rpId"] = inspector.GetSource("passkeys.rp_id"),
            ["passkeys.rpName"] = inspector.GetSource("passkeys.rp_name"),
            ["passkeys.requireUserVerification"] = inspector.GetSource("passkeys.require_user_verification"),
            ["plex.enabled"] = inspector.GetSource("plex.enabled"),
            ["plex.boundServerUrl"] = inspector.GetSource("plex.bound_server_url"),
            ["oidc.enabled"] = inspector.GetSource("oidc.enabled"),
            ["oidc.displayName"] = inspector.GetSource("oidc.display_name"),
            ["oidc.issuer"] = inspector.GetSource("oidc.issuer"),
            ["oidc.clientId"] = inspector.GetSource("oidc.client_id"),
            ["oidc.clientSecret"] = inspector.GetSource("oidc.client_secret"),
            ["oidc.scopes"] = inspector.GetSource("oidc.scopes"),
            ["oidc.autoProvision"] = inspector.GetSource("oidc.auto_provision"),
            ["oidc.autoRedirect"] = inspector.GetSource("oidc.auto_redirect"),
            ["oidc.trustVerifiedEmail"] = inspector.GetSource("oidc.trust_verified_email"),
            ["oidc.usernameClaim"] = inspector.GetSource("oidc.username_claim"),
            ["oidc.emailClaim"] = inspector.GetSource("oidc.email_claim"),
            ["oidc.displayNameClaim"] = inspector.GetSource("oidc.display_name_claim"),
            ["oidc.groupsClaim"] = inspector.GetSource("oidc.groups_claim"),
            ["oidc.adminGroups"] = inspector.GetSource("oidc.admin_groups_json"),
            ["oidc.managerGroups"] = inspector.GetSource("oidc.manager_groups_json"),
            ["oidc.requesterGroups"] = inspector.GetSource("oidc.requester_groups_json"),
            ["security.trustForwardedHeaders"] = ResolveTrustForwardedHeadersSource(inspector),
            ["security.trustedProxyCidrs"] = ResolveTrustedProxyCidrsSource(inspector),
            ["security.allowedHosts"] = ResolveAllowedHostsSource(inspector),
            ["mcp.enabled"] = inspector.GetSource("Managerr:Mcp:Enabled"),
            ["ui.defaultTheme"] = inspector.GetSource("ui.default_theme"),
            ["ui.accentColor"] = inspector.GetSource("ui.accent_color"),
            ["ui.colorScheme"] = inspector.GetSource("ui.color_scheme"),
        };

    private static string ResolveTrustForwardedHeadersSource(IConfigSourceInspector inspector) =>
        inspector.GetSource("Managerr:Auth:TrustForwardedHeaders");

    private static string ResolveTrustedProxyCidrsSource(IConfigSourceInspector inspector) =>
        inspector.GetSource("Managerr:Auth:TrustedProxyCidrs");

    private static string ResolveAllowedHostsSource(IConfigSourceInspector inspector) =>
        inspector.GetSource("Managerr:Auth:AllowedHosts");

    private static async Task<int> GetEffectiveMaxRetainedBackupsAsync(IAppSettingsService appSettings, CancellationToken ct)
    {
        var raw = await appSettings.GetAsync("system.backups.max_retained", ct);
        return int.TryParse(raw, out var parsed) ? Math.Clamp(parsed, 1, 100) : 10;
    }

    private static async Task<int> GetEffectiveRollingQuotaWindowDaysAsync(IAppSettingsService appSettings, CancellationToken ct)
    {
        var raw = await appSettings.GetAsync("system.quotas.rolling_window_days", ct);
        return int.TryParse(raw, out var parsed) ? Math.Clamp(parsed, 1, 365) : 30;
    }

    private static async Task<string?> ResolveEffectiveBoundServerUrlAsync(
        IAppSettingsService appSettings,
        IConfigSourceInspector inspector,
        IConfiguration configuration,
        CancellationToken ct)
    {
        if (inspector.GetSource("plex.bound_server_url") == "database")
        {
            var dbUrl = await appSettings.GetAsync("plex.bound_server_url", ct);
            if (!string.IsNullOrWhiteSpace(dbUrl))
            {
                return dbUrl.Trim();
            }
        }
        var cfgUrl = configuration["Managerr:Auth:Plex:BoundServerUrl"] ?? configuration["Managerr:Plex:BoundServerUrl"];
        return string.IsNullOrWhiteSpace(cfgUrl) ? null : cfgUrl.Trim();
    }

    private static async Task<(
        bool TrustForwardedHeaders,
        string[] TrustedProxyCidrs,
        string[] AllowedHosts,
        string TfhManagedBy,
        string CidrsManagedBy,
        string AllowedHostsManagedBy)> ResolveEffectiveSecurityProxySettingsAsync(
        IAppSettingsService appSettings,
        IConfigSourceInspector inspector,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var tfhSource = ResolveTrustForwardedHeadersSource(inspector);
        bool trustForwardedHeaders = configuration.GetValue("Managerr:Auth:TrustForwardedHeaders", false);
        if (tfhSource == "database")
        {
            var rawTfh = await appSettings.GetAsync("system.security.trust_forwarded_headers", ct)
                ?? await appSettings.GetAsync("security.trust_forwarded_headers", ct);
            if (bool.TryParse(rawTfh, out var parsedTfh))
            {
                trustForwardedHeaders = parsedTfh;
            }
        }

        var cidrsSource = ResolveTrustedProxyCidrsSource(inspector);
        var trustedProxyCidrs = configuration.GetSection("Managerr:Auth:TrustedProxyCidrs").Get<string[]>()
            ?? Array.Empty<string>();
        if (cidrsSource == "database")
        {
            var rawCidrs = await appSettings.GetAsync("system.security.trusted_proxy_cidrs", ct)
                ?? await appSettings.GetAsync("security.trusted_proxy_cidrs", ct);
            trustedProxyCidrs = ParseConfiguredStringArray(rawCidrs);
        }

        var allowedHostsSource = ResolveAllowedHostsSource(inspector);
        var allowedHosts = configuration.GetSection("Managerr:Auth:AllowedHosts").Get<string[]>()
            ?? Array.Empty<string>();
        if (allowedHostsSource == "database")
        {
            var rawHosts = await appSettings.GetAsync("system.security.allowed_hosts", ct);
            allowedHosts = ParseConfiguredStringArray(rawHosts);
        }

        return (trustForwardedHeaders, trustedProxyCidrs, allowedHosts, tfhSource, cidrsSource, allowedHostsSource);
    }

    private static string[] ParseConfiguredStringArray(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                return JsonSerializer.Deserialize<string[]>(trimmed, JsonOpts) ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        return trimmed.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static async Task<bool> ResolveEffectiveMcpEnabledAsync(
        IAppSettingsService appSettings,
        IConfiguration configuration,
        string mcpManagedBy,
        CancellationToken ct)
    {
        var cfgMcp = configuration.GetValue("Managerr:Mcp:Enabled", true);
        if (mcpManagedBy != "database")
        {
            return cfgMcp;
        }
        var raw = await appSettings.GetAsync("system.mcp.enabled", ct);
        return bool.TryParse(raw, out var parsed) ? parsed : cfgMcp;
    }

    private static bool TryNormalizeOptionalHttpUrl(string raw, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            normalized = "";
            return true;
        }

        var trimmed = raw.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            normalized = trimmed;
            return true;
        }

        normalized = "";
        return false;
    }

    private static bool TryValidateCidrs(IEnumerable<string> rawCidrs, out string[] validCidrs, out string? invalidEntry)
    {
        var list = new List<string>();
        foreach (var raw in rawCidrs)
        {
            var entry = raw?.Trim();
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (IPAddress.TryParse(entry, out _))
            {
                list.Add(entry);
                continue;
            }

            var parts = entry.Split('/');
            if (parts.Length == 2 &&
                IPAddress.TryParse(parts[0], out var ip) &&
                int.TryParse(parts[1], out var prefix))
            {
                var maxPrefix = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
                if (prefix >= 0 && prefix <= maxPrefix)
                {
                    list.Add(entry);
                    continue;
                }
            }

            validCidrs = Array.Empty<string>();
            invalidEntry = entry;
            return false;
        }

        validCidrs = list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        invalidEntry = null;
        return true;
    }

    private static bool TryValidateAllowedHosts(IEnumerable<string> rawHosts, out string[] validHosts, out string? invalidEntry)
    {
        var list = new List<string>();
        foreach (var raw in rawHosts)
        {
            var entry = raw?.Trim();
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (entry.Length > 255 ||
                entry.Any(char.IsWhiteSpace) ||
                entry.IndexOfAny(['/', '\\', '@', '?', '#', ':']) >= 0)
            {
                validHosts = Array.Empty<string>();
                invalidEntry = entry;
                return false;
            }

            var candidate = entry;
            if (entry.StartsWith("*.", StringComparison.Ordinal))
            {
                candidate = entry[2..];
                if (candidate.Length == 0 || candidate.Contains('*') || candidate.StartsWith('.'))
                {
                    validHosts = Array.Empty<string>();
                    invalidEntry = entry;
                    return false;
                }
            }
            else if (entry.Contains('*'))
            {
                validHosts = Array.Empty<string>();
                invalidEntry = entry;
                return false;
            }

            var hostType = Uri.CheckHostName(candidate);
            if (hostType is UriHostNameType.Dns or UriHostNameType.IPv4)
            {
                list.Add(entry.ToLowerInvariant());
                continue;
            }

            validHosts = Array.Empty<string>();
            invalidEntry = entry;
            return false;
        }

        validHosts = list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        invalidEntry = null;
        return true;
    }

    private static bool StringSetsEqual(IEnumerable<string> left, IEnumerable<string> right)
    {
        var setA = new HashSet<string>(left.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(right.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        return setA.SetEquals(setB);
    }

    private static IResult? CheckScopedKeyForbidden(AuthenticatedPrincipal principal, HttpContext httpContext)
    {
        if (principal.ApiKeyPrefix is not null)
        {
            return Results.Json(new
            {
                type = "https://managerr.dev/problems/err_forbidden",
                title = "Interactive admin session required",
                status = StatusCodes.Status403Forbidden,
                detail = "API keys cannot read or rotate the master instance API key; sign in interactively as an administrator.",
                code = "ERR_SCOPED_KEY_FORBIDDEN",
                traceId = httpContext.TraceIdentifier,
            }, contentType: "application/problem+json", statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    private static string? NormalizeThemeOrNull(string raw)
    {
        var t = raw.Trim().ToLowerInvariant();
        return t is "system" or "dark" or "oled" or "light" ? t : null;
    }

    private static string? NormalizeHexColorOrNull(string raw)
    {
        var c = raw.Trim();
        if (c.Length != 7 || c[0] != '#') return null;
        for (var i = 1; i < 7; i++)
        {
            var ch = c[i];
            var isHex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
            if (!isHex) return null;
        }
        return c.ToLowerInvariant();
    }

    private static IResult InvalidThemeOrAccentError(string detail, HttpContext httpContext) =>
        Results.Json(new
        {
            type = "https://managerr.dev/problems/err_validation_failed",
            title = "Invalid UI theme setting",
            status = StatusCodes.Status400BadRequest,
            detail,
            code = "ERR_VALIDATION_FAILED",
            traceId = httpContext.TraceIdentifier,
        }, contentType: "application/problem+json", statusCode: StatusCodes.Status400BadRequest);

    private static string SanitizeInstanceName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Managerr";
        }

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (!char.IsControl(ch))
            {
                sb.Append(ch);
            }
        }

        var trimmed = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "Managerr";
        }

        return trimmed.Length > 64 ? trimmed[..64] : trimmed;
    }

    private static readonly HashSet<string> AllowedCustomThemeTokenKeys = new(StringComparer.Ordinal)
    {
        "headerBg",
        "headerBgEnd",
        "headerBorder",
        "sidebarBg",
        "sidebarBorder",
        "sidebarActiveBg",
        "headingBg",
        "toolbarBg",
        "filterBarBg",
        "pageBg",
        "cardBg",
        "cardBorder",
        "modalBg",
        "inputBg",
        "accent",
        "logoSecondary",
        "primary",
        "primaryHover",
        "primaryBorder",
    };

    internal static async Task<(string DefaultTheme, string AccentColor, string ColorScheme, ManagerrCustomThemePayload? CustomTheme)> ResolveEffectiveUiSettingsAsync(
        IAppSettingsService appSettings,
        IConfigSourceInspector inspector,
        ManagerrUiOptions uiOptions,
        CancellationToken ct)
    {
        var rawTheme = inspector.GetSource("ui.default_theme") != "database"
            ? uiOptions.DefaultTheme
            : (await appSettings.GetAsync("ui.default_theme", ct) ?? uiOptions.DefaultTheme);
        var defaultTheme = NormalizeThemeOrNull(rawTheme ?? "") ?? "dark";

        var rawAccent = inspector.GetSource("ui.accent_color") != "database"
            ? uiOptions.AccentColor
            : (await appSettings.GetAsync("ui.accent_color", ct) ?? uiOptions.AccentColor);
        var accentColor = NormalizeHexColorOrNull(rawAccent ?? "") ?? "#2dd4bf";

        var rawScheme = inspector.GetSource("ui.color_scheme") != "database"
            ? uiOptions.ColorScheme
            : (await appSettings.GetAsync("ui.color_scheme", ct) ?? uiOptions.ColorScheme);
        var colorScheme = NormalizeColorSchemeOrNull(rawScheme ?? "") ?? "seafoam_jade";

        var customThemeJson = await appSettings.GetAsync("ui.custom_theme_json", ct);
        var parsedCustom = ManagerrCustomThemePayload.FromJsonOrNull(customThemeJson);
        ManagerrCustomThemePayload? safeCustomTheme = null;
        if (parsedCustom?.Tokens is { Count: >= 1 and <= 19 })
        {
            var validTokens = new Dictionary<string, string>(StringComparer.Ordinal);
            var allValid = true;
            foreach (var (k, v) in parsedCustom.Tokens)
            {
                var normHex = string.IsNullOrWhiteSpace(v) ? null : NormalizeHexColorOrNull(v);
                if (!AllowedCustomThemeTokenKeys.Contains(k) || normHex is null)
                {
                    allValid = false;
                    break;
                }
                validTokens[k] = normHex;
            }
            if (allValid)
            {
                var safeMode = string.IsNullOrWhiteSpace(parsedCustom.BaseMode)
                    ? null
                    : NormalizeThemeOrNull(parsedCustom.BaseMode);
                safeCustomTheme = new ManagerrCustomThemePayload(
                    Schema: "managerr-theme/v1",
                    Name: SanitizeCustomThemeName(parsedCustom.Name),
                    BaseMode: safeMode,
                    Tokens: validTokens);
            }
        }

        return (defaultTheme, accentColor, colorScheme, safeCustomTheme);
    }

    private static string? NormalizeColorSchemeOrNull(string raw)
    {
        var s = raw.Trim().ToLowerInvariant();
        return s is "seafoam_jade" or "royal_amethyst" or "cyber_chartreuse" or "brushed_copper" or "nordic_periwinkle" or "custom" ? s : null;
    }

    private static string SanitizeCustomThemeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Custom Theme";
        }

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (!char.IsControl(ch) && ch != '<' && ch != '>')
            {
                sb.Append(ch);
            }
        }

        var trimmed = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "Custom Theme";
        }

        return trimmed.Length > 64 ? trimmed[..64] : trimmed;
    }

    private static (ManagerrCustomThemePayload? NormalizedPayload, string? SerializedJson, IResult? ErrorResult) ValidateAndSerializeCustomTheme(
        ManagerrCustomThemePayload payload,
        HttpContext httpContext)
    {
        if (payload.Tokens is null || payload.Tokens.Count < 1 || payload.Tokens.Count > 19)
        {
            return (null, null, InvalidThemeOrAccentError("customTheme.tokens must contain between 1 and 19 valid color tokens.", httpContext));
        }

        var normalizedTokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, rawVal) in payload.Tokens)
        {
            if (!AllowedCustomThemeTokenKeys.Contains(key))
            {
                return (null, null, InvalidThemeOrAccentError($"customTheme.tokens contains unknown token key: '{key}'.", httpContext));
            }

            if (string.IsNullOrWhiteSpace(rawVal))
            {
                return (null, null, InvalidThemeOrAccentError($"customTheme.tokens token '{key}' must be a 6-digit hex color (#RRGGBB).", httpContext));
            }

            var normHex = NormalizeHexColorOrNull(rawVal);
            if (normHex is null)
            {
                return (null, null, InvalidThemeOrAccentError($"customTheme.tokens token '{key}' must be a 6-digit hex color (#RRGGBB).", httpContext));
            }

            normalizedTokens[key] = normHex;
        }

        string? normBaseMode = null;
        if (!string.IsNullOrWhiteSpace(payload.BaseMode))
        {
            normBaseMode = NormalizeThemeOrNull(payload.BaseMode);
            if (normBaseMode is null)
            {
                return (null, null, InvalidThemeOrAccentError("customTheme.baseMode must be one of: system, dark, oled, light.", httpContext));
            }
        }

        var cleanThemeName = SanitizeCustomThemeName(payload.Name);

        var normalizedPayload = new ManagerrCustomThemePayload(
            Schema: "managerr-theme/v1",
            Name: cleanThemeName,
            BaseMode: normBaseMode,
            Tokens: normalizedTokens);

        var serializedJson = normalizedPayload.ToJson();
        if (Encoding.UTF8.GetByteCount(serializedJson) > 4096)
        {
            return (null, null, InvalidThemeOrAccentError("customTheme JSON exceeds maximum allowed size of 4096 bytes.", httpContext));
        }

        return (normalizedPayload, serializedJson, null);
    }
}
