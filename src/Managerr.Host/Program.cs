using System.Threading.RateLimiting;
using Managerr.Catalog;
using Managerr.Cleanerr;
using Managerr.Common.Configuration;
using Managerr.Common.Crypto;
using Managerr.Common.Net;
using Managerr.Common.Paths;
using Managerr.Datastore;
using Managerr.Datastore.Migrations;
using Managerr.Datastore.Services;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
using Managerr.Downloaders.Organizer;
using Managerr.Downloaders.Parser;
using Managerr.Host.Auth;
using Managerr.Host.Endpoints;
using Managerr.Host.Hubs;
using Managerr.Host.Middleware;
using Managerr.Host.Services;
using Managerr.Indexers;
using Managerr.Indexers.Cardigann;
using Managerr.Metadata;
using Managerr.Migration;
using Managerr.Profiles;
using Managerr.Security.Auth;
using Managerr.Security.Plex;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Managerr.Verification;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

var configDir = Environment.GetEnvironmentVariable("MANAGERR_CONFIG_DIR")
    ?? (Directory.Exists("/config") ? "/config" : Path.Combine(Directory.GetCurrentDirectory(), "config"));
var managerrJsonPath = Path.Combine(configDir, "managerr.json");

builder.Configuration.AddJsonFile(managerrJsonPath, optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddFileSecrets();

var inspector = new ConfigSourceInspector(builder.Configuration, managerrJsonPath);
builder.Services.AddSingleton<IConfigSourceInspector>(inspector);

builder.Services.Configure<ManagerrAuthOptions>(builder.Configuration.GetSection(ManagerrAuthOptions.SectionName));
builder.Services.Configure<ManagerrUiOptions>(builder.Configuration.GetSection(ManagerrUiOptions.SectionName));

builder.Services.Configure<SafeHttpEgressOptions>(builder.Configuration.GetSection("SafeHttpEgress"));
builder.Services.AddMemoryCache();
builder.Services.AddOpenApi();
builder.Services.AddSignalR();

builder.Services.AddSingleton<IAppPaths>(_ =>
    new AppPaths(builder.Configuration["Managerr:ConfigDirectory"]));
builder.Services.AddSingleton<ISecretProtector>(sp =>
    new EnvelopeSecretProtector(
        sp.GetRequiredService<IAppPaths>(),
        builder.Configuration["Managerr:MasterKey"]));
builder.Services.AddSingleton<ISafeHttpClientFactory, SafeHttpClientFactory>();

builder.Services.AddSingleton<ISqliteDbFactory, SqliteDbFactory>();
builder.Services.AddSingleton<ISchemaMigrator, SchemaMigrator>();
builder.Services.AddSingleton<ISqliteBackupService, SqliteBackupService>();
builder.Services.AddSingleton<IHybridSqliteCache, HybridSqliteCache>();
builder.Services.AddSingleton<IAuditLogService, AuditLogService>();
builder.Services.AddSingleton<IAppSettingsService, AppSettingsService>();
builder.Services.AddSingleton<IFtsSearchService, FtsSearchService>();

builder.Services.AddSingleton<IRoleService, RoleService>();
builder.Services.AddSingleton<IPlexUserService, PlexUserService>();
builder.Services.AddSingleton<IPlexApiClient, PlexApiClient>();
builder.Services.AddSingleton<IPlexAuthAndSessionService, PlexAuthAndSessionService>();
builder.Services.AddSingleton<IOidcAuthenticationService, OidcAuthenticationService>();
builder.Services.AddSingleton<IPasskeyAuthenticationService, PasskeyAuthenticationService>();

builder.Services.AddSingleton<ICardigannDefinitionCatalog>(sp =>
    new CardigannDefinitionCatalog(
        sp.GetRequiredService<IAppPaths>(),
        builder.Configuration["Managerr:CardigannDefinitionsDirectory"]));
builder.Services.AddSingleton<IIndexerHttpGateway, IndexerHttpGateway>();
builder.Services.AddSingleton<IUnifiedIndexerService, UnifiedIndexerService>();

builder.Services.AddSingleton<IDownloadClientDriverGateway, DownloadClientDriverGateway>();
builder.Services.AddSingleton<IUnifiedDownloadOrchestrator, UnifiedDownloadOrchestrator>();
builder.Services.AddSingleton<IReleaseParserRouter, ReleaseParserRouter>();
builder.Services.AddSingleton<IAtomicFileOrganizer, AtomicFileOrganizer>();
builder.Services.AddSingleton<ICompletedDownloadImportService, CompletedDownloadImportService>();

builder.Services.AddSingleton<IMetadataHttpGateway, MetadataHttpGateway>();
builder.Services.AddSingleton<ISidecarMetadataService, SidecarMetadataService>();
builder.Services.AddSingleton<IExternalIdCrosswalkService, ExternalIdCrosswalkService>();
builder.Services.AddSingleton<ICustomHttpMetadataProviderService, CustomHttpMetadataProviderService>();
builder.Services.AddSingleton<IEnumerable<IMetadataProvider>>(sp =>
    BuiltInMetadataProviderCatalog.CreateAll(
        sp.GetRequiredService<IMetadataHttpGateway>(),
        sp.GetRequiredService<IAppSettingsService>(),
        sp.GetRequiredService<ISidecarMetadataService>()));
builder.Services.AddSingleton<IMetadataFederationService, MetadataFederationService>();
builder.Services.AddSingleton<IPolymorphicCatalogService, PolymorphicCatalogService>();

builder.Services.AddSingleton<ITrashGuidesImporter, TrashGuidesImporter>();
builder.Services.AddSingleton<IComposableProfileService, ComposableProfileService>();
builder.Services.AddSingleton<IContainerProbeAdapter, DefaultContainerProbeAdapter>();
builder.Services.AddSingleton<ITextLanguageDetector, StatisticalTextLanguageDetector>();
builder.Services.AddSingleton<ISubtitleOcrAdapter, DefaultSubtitleOcrAdapter>();
builder.Services.AddSingleton<IAudioSpeechLidAdapter, DefaultAudioSpeechLidAdapter>();
builder.Services.AddSingleton<IMkvHeaderPatcher, DefaultMkvHeaderPatcher>();
builder.Services.AddSingleton<ILanguageVerificationService, LanguageVerificationService>();

builder.Services.AddSingleton<ILegacyMigrationService, LegacyArrAndCleanerrMigrator>();
builder.Services.AddSingleton<ICompatShimService, CompatShimService>();

builder.Services.AddSingleton<IRealtimeBroadcaster, RealtimeBroadcaster>();
builder.Services.AddSingleton<ISignedWebhookDispatcher, SignedWebhookDispatcher>();
builder.Services.AddSingleton<NotificationConnectionService>();
builder.Services.AddSingleton<INotificationConnectionService>(sp => sp.GetRequiredService<NotificationConnectionService>());
builder.Services.AddSingleton<Managerr.Common.Notifications.IMediaLifecycleNotificationSink>(sp => sp.GetRequiredService<NotificationConnectionService>());
builder.Services.AddSingleton<ICalendarFeedService, CalendarFeedService>();
builder.Services.AddSingleton<IAutomatedRssSyncService, AutomatedRssSyncService>();
builder.Services.AddSingleton<IReleaseSearchOrchestrator, ReleaseSearchOrchestrator>();
builder.Services.AddSingleton<IWantedAndActivityService, WantedAndActivityService>();
builder.Services.AddSingleton<IImportListSyncService, ImportListSyncService>();
builder.Services.AddSingleton<MediaManagementAndDiskScanService>();
builder.Services.AddSingleton<IMediaManagementAndDiskScanService>(sp => sp.GetRequiredService<MediaManagementAndDiskScanService>());
builder.Services.AddSingleton<ICatalogDiskScanHook>(sp => sp.GetRequiredService<MediaManagementAndDiskScanService>());
builder.Services.AddSingleton<IFilesystemBrowseService, FilesystemBrowseService>();
builder.Services.AddSingleton<ICleanerrHttpGateway, CleanerrHttpGateway>();
builder.Services.AddSingleton<ICleanerrDiscordService, CleanerrDiscordService>();
builder.Services.AddSingleton<ICleanerrLifecycleEngine, CleanerrLifecycleEngine>();
builder.Services.AddHostedService<CleanerrDiscordGatewayHostedService>();
builder.Services.AddSingleton<BackgroundTaskSchedulerService>();
builder.Services.AddSingleton<ITaskManagerService>(sp => sp.GetRequiredService<BackgroundTaskSchedulerService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackgroundTaskSchedulerService>());
builder.Services.AddSingleton<Managerr.Host.Mcp.ManagerrMcpServer>();

builder.Services
    .AddAuthentication(ManagerrAuthConstants.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ManagerrAuthenticationHandler>(
        ManagerrAuthConstants.SchemeName,
        _ => { });

builder.Services.AddAuthorization();
var rateLimitPerMin = Math.Clamp(builder.Configuration.GetValue("Managerr:Auth:RateLimitPerMinute", 60), 10, 10_000);
builder.Services.AddRateLimiter(rateOpts =>
{
    rateOpts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateOpts.AddPolicy("auth_endpoints", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "local",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimitPerMin,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var app = builder.Build();

// Initialize embedded 3-file SQLite WAL schema, built-in RBAC preset roles, ExternalId crosswalk seeds and Composable Profiles on startup
using (var scope = app.Services.CreateScope())
{
    var migrator = scope.ServiceProvider.GetRequiredService<ISchemaMigrator>();
    await migrator.MigrateAllAsync();

    var roleService = scope.ServiceProvider.GetRequiredService<IRoleService>();
    await roleService.EnsureBuiltInPresetsSeededAsync();

    var crosswalk = scope.ServiceProvider.GetRequiredService<IExternalIdCrosswalkService>();
    await crosswalk.EnsureSeededAsync();

    var profileService = scope.ServiceProvider.GetRequiredService<IComposableProfileService>();
    await profileService.EnsureBuiltInProfilesSeededAsync();

    var taskManager = scope.ServiceProvider.GetRequiredService<ITaskManagerService>();
    await taskManager.EnsureTasksSeededAsync();

    var authService = scope.ServiceProvider.GetRequiredService<IPlexAuthAndSessionService>();
    await authService.GetInstanceApiKeyAsync();

    var bootstrapAdmin = builder.Configuration.GetSection("Managerr:Auth:Local:BootstrapAdmin").Get<BootstrapAdminOptions>();
    if (bootstrapAdmin != null && !string.IsNullOrWhiteSpace(bootstrapAdmin.Username) && !string.IsNullOrWhiteSpace(bootstrapAdmin.Password))
    {
        var userService = scope.ServiceProvider.GetRequiredService<IPlexUserService>();
        var roleSvc = scope.ServiceProvider.GetRequiredService<IRoleService>();
        var hasOwner = await userService.HasServerOwnerAsync();
        var allUsers = await userService.ListUsersAsync();
        var existing = allUsers.FirstOrDefault(u => string.Equals(u.Username, bootstrapAdmin.Username, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            if (!hasOwner)
            {
                await userService.ClaimLocalOwnerAsync(
                    bootstrapAdmin.Username,
                    bootstrapAdmin.Email,
                    bootstrapAdmin.DisplayName,
                    bootstrapAdmin.Password);
            }
            else
            {
                var adminRole = await roleSvc.GetByKeyAsync(BuiltInRoleKeys.Admin);
                await userService.CreateLocalUserAsync(
                    bootstrapAdmin.Username,
                    bootstrapAdmin.Email,
                    bootstrapAdmin.DisplayName,
                    bootstrapAdmin.Password,
                    adminRole != null ? [adminRole.Id] : null);
            }
        }
        else
        {
            await userService.SetBreakGlassPasswordAsync(existing.Id, bootstrapAdmin.Password);
        }
    }

    if (builder.Configuration.GetValue<bool>("Managerr:TestbedBootstrap:Enabled"))
    {
        var userService = scope.ServiceProvider.GetRequiredService<IPlexUserService>();
        var appSettings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        var tbUsername = builder.Configuration["Managerr:TestbedBootstrap:Username"] ?? "testadmin";
        var tbPassword = builder.Configuration["Managerr:TestbedBootstrap:Password"];
        var tbPlexUrl = builder.Configuration["Managerr:TestbedBootstrap:PlexServerUrl"];

        if (!string.IsNullOrWhiteSpace(tbPassword))
        {
            var hasOwner = await userService.HasServerOwnerAsync();
            var tbUser = await userService.UpsertFromPlexAsync(new UpsertPlexUserCommand(
                PlexUserId: 999_000_001L,
                PlexUuid: "testlab-bootstrap-admin",
                Username: tbUsername,
                Email: $"{tbUsername}@testlab.local",
                AvatarUrl: null,
                IsServerOwner: !hasOwner,
                IsHomeUser: true,
                IsActive: true,
                SharedPlexSectionIds: ["1", "2", "3", "4"]));

            var adminRole = await roleService.GetByKeyAsync(BuiltInRoleKeys.Admin);
            if (adminRole is not null)
            {
                await userService.AssignRolesAsync(tbUser.Id, [adminRole.Id]);
            }

            await userService.SetBreakGlassPasswordAsync(tbUser.Id, tbPassword);
        }

        if (!string.IsNullOrWhiteSpace(tbPlexUrl))
        {
            var existingPlexUrl = await appSettings.GetAsync("plex.bound_server_url");
            if (string.IsNullOrWhiteSpace(existingPlexUrl))
            {
                await appSettings.SetAsync("plex.bound_server_url", tbPlexUrl);
            }
        }
    }

    _ = Task.Run(async () =>
    {
        try
        {
            using var bgScope = app.Services.CreateScope();
            var catalogSvc = bgScope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var classifyReport = await catalogSvc.AutoClassifyCatalogWorksAsync();
            if (classifyReport.UpdatedCount > 0)
            {
                app.Logger.LogInformation(
                    "Auto-classified {UpdatedCount}/{EvaluatedCount} catalog works on startup.",
                    classifyReport.UpdatedCount,
                    classifyReport.EvaluatedCount);
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogDebug(ex, "Startup catalog auto-classification skipped.");
        }
    });
}

app.UseMiddleware<ConditionalForwardedHeadersMiddleware>();
app.Use((ctx, next) =>
{
    var rawPath = ctx.Request.Path.Value;
    if (!string.IsNullOrEmpty(rawPath) && rawPath.Contains("//", StringComparison.Ordinal))
    {
        var collapsed = System.Text.RegularExpressions.Regex.Replace(rawPath, "/{2,}", "/");
        ctx.Request.Path = new PathString(collapsed);
    }

    return next();
});
app.UseMiddleware<ProblemDetailsExceptionMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<IdempotencyMiddleware>();

app.MapOpenApi("/api/v1/openapi.json");
app.MapHub<RealtimeHub>("/api/v1/realtime");

app.MapAuthEndpoints();
app.MapUsersAndRolesEndpoints();
app.MapCatalogAndMetadataEndpoints();
app.MapMediaManagementEndpoints();
app.MapProfilesAndVerificationEndpoints();
app.MapAcquisitionEndpoints();
app.MapWantedAndActivityEndpoints();
app.MapEcosystemEndpoints();
app.MapMigrationAndCompatEndpoints();
app.MapFilesystemEndpoints();
app.MapSystemEndpoints();
app.MapCleanerrEndpoints();
app.MapMcpEndpoints();

// SPA fallback for React 19 + Vite 8 frontend bundle when wwwroot/index.html is present
app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api") ||
        context.Request.Path.StartsWithSegments("/compat") ||
        context.Request.Path.StartsWithSegments("/feed") ||
        context.Request.Path.StartsWithSegments("/mcp"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new
        {
            type = "https://managerr.dev/problems/err_not_found",
            title = "API endpoint not found",
            status = StatusCodes.Status404NotFound,
            code = "ERR_NOT_FOUND",
            traceId = context.TraceIdentifier,
        });
        return;
    }

    var indexPath = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html");
    if (File.Exists(indexPath))
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(indexPath);
        return;
    }

    context.Response.StatusCode = StatusCodes.Status200OK;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new
    {
        service = "Managerr",
        status = "running",
        openApi = "/api/v1/openapi.json",
    });
});

app.Run();

public partial class Program;
