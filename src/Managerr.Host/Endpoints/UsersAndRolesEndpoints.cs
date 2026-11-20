using Managerr.Datastore.Services;
using Managerr.Host.Auth;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Security.Users;

namespace Managerr.Host.Endpoints;

public sealed record AssignUserRolesRequest(IReadOnlyList<long> RoleIds);

public sealed record CreateLocalUserRequest(
    string Username,
    string? Email = null,
    string? DisplayName = null,
    string Password = "",
    IReadOnlyList<long>? RoleIds = null);

public sealed record UpdateUserAdminRequest(
    string? PreferredAudioLang = null,
    string? PreferredSubLang = null,
    string? DiscordUserId = null,
    string? DiscordUsername = null,
    bool? IsActive = null);

public sealed record UpdateOwnPreferencesRequest(
    string? PreferredAudioLang = null,
    string? PreferredSubLang = null,
    string? DiscordUserId = null,
    string? DiscordUsername = null);

public sealed record CloneRoleRequest(string Name, string? Key = null);

public static class UsersAndRolesEndpoints
{
    public static IEndpointRouteBuilder MapUsersAndRolesEndpoints(this IEndpointRouteBuilder app)
    {
        var usersGroup = app.MapGroup("/api/v1/users").WithTags("Plex Users");

        usersGroup.MapGet("/", async (IPlexUserService userService, CancellationToken ct) =>
        {
            var users = await userService.ListUsersAsync(ct);
            return Results.Ok(users);
        }).RequireScope(PermissionScopes.UsersRead);

        usersGroup.MapPost("/local", async (
            CreateLocalUserRequest req,
            HttpContext httpContext,
            IPlexUserService userService,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            if (principal.ApiKeyPrefix is not null)
            {
                return Results.Json(new
                {
                    type = "https://managerr.dev/problems/err_forbidden",
                    title = "Interactive admin session required",
                    status = StatusCodes.Status403Forbidden,
                    detail = "API keys cannot provision local user accounts; sign in interactively as an administrator.",
                    code = "ERR_SCOPED_KEY_FORBIDDEN",
                    traceId = httpContext.TraceIdentifier,
                }, contentType: "application/problem+json", statusCode: StatusCodes.Status403Forbidden);
            }

            var user = await userService.CreateLocalUserAsync(req.Username, req.Email, req.DisplayName, req.Password, req.RoleIds, ct);

            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "users.local_created",
                ResourceType: "user",
                ResourceId: user.Id.ToString(),
                Details: new { user.Username, user.Email },
                ActorUserId: principal.User?.Id,
                ActorUsername: principal.User?.Username,
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                TraceId: httpContext.TraceIdentifier), ct);

            return Results.Created($"/api/v1/users/{user.Id}", user);
        }).RequireScope(PermissionScopes.SystemAdmin);

        usersGroup.MapPost("/sync-plex", async (
            HttpContext httpContext,
            IPlexAuthAndSessionService authService,
            IPlexUserService userService,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            var syncedCount = await authService.SyncPlexServerUsersAsync(ct);
            var users = await userService.ListUsersAsync(ct);

            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "users.plex_synced",
                ResourceType: "plex_server",
                Details: new { SyncedSharedUsers = syncedCount, TotalLocalUsers = users.Count },
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                TraceId: httpContext.TraceIdentifier), ct);

            return Results.Ok(new { syncedCount, users });
        }).RequireScope(PermissionScopes.UsersManage);

        usersGroup.MapPut("/{id:long}/roles", AssignRolesHandlerAsync)
            .RequireScope(PermissionScopes.UsersManage);
        usersGroup.MapPatch("/{id:long}/roles", AssignRolesHandlerAsync)
            .RequireScope(PermissionScopes.UsersManage);

        usersGroup.MapPatch("/{id:long}", async (
            long id,
            UpdateUserAdminRequest req,
            HttpContext httpContext,
            IPlexUserService userService,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            var updated = await userService.UpdatePreferencesAndLinksAsync(
                userId: id,
                preferredAudioLang: req.PreferredAudioLang,
                preferredSubLang: req.PreferredSubLang,
                discordUserId: req.DiscordUserId,
                discordUsername: req.DiscordUsername,
                isActive: req.IsActive,
                ct: ct);

            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "users.updated",
                ResourceType: "user",
                ResourceId: id.ToString(),
                Details: new { updated.PreferredAudioLang, updated.PreferredSubLang, updated.DiscordUserId, updated.IsActive },
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                TraceId: httpContext.TraceIdentifier), ct);

            return Results.Ok(updated);
        }).RequireScope(PermissionScopes.UsersManage);

        usersGroup.MapPatch("/me/preferences", async (
            UpdateOwnPreferencesRequest req,
            HttpContext httpContext,
            IPlexUserService userService,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            var me = principal.User ?? throw new UnauthorizedAccessException("User session required.");

            var updated = await userService.UpdatePreferencesAndLinksAsync(
                userId: me.Id,
                preferredAudioLang: req.PreferredAudioLang,
                preferredSubLang: req.PreferredSubLang,
                discordUserId: req.DiscordUserId,
                discordUsername: req.DiscordUsername,
                isActive: null,
                ct: ct);

            return Results.Ok(updated);
        }).RequireAuthorization();

        var rolesGroup = app.MapGroup("/api/v1/roles").WithTags("Preset & Custom Roles");

        rolesGroup.MapGet("/", async (IRoleService roleService, CancellationToken ct) =>
        {
            var roles = await roleService.ListRolesAsync(ct);
            return Results.Ok(roles);
        }).RequireScope(PermissionScopes.UsersRead);

        rolesGroup.MapGet("/scopes", () => Results.Ok(PermissionScopes.Catalog))
            .RequireScope(PermissionScopes.UsersRead);

        rolesGroup.MapPost("/", async (
            UpsertCustomRoleRequest req,
            HttpContext httpContext,
            IRoleService roleService,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            var created = await roleService.CreateCustomRoleAsync(req, ct);

            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "roles.created",
                ResourceType: "role",
                ResourceId: created.Id.ToString(),
                Details: new { created.Key, created.Name, created.Scopes },
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                TraceId: httpContext.TraceIdentifier), ct);

            return Results.Created($"/api/v1/roles/{created.Id}", created);
        }).RequireScope(PermissionScopes.RolesManage);

        rolesGroup.MapPost("/{id:long}/clone", async (
            long id,
            CloneRoleRequest req,
            HttpContext httpContext,
            IRoleService roleService,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            var cloned = await roleService.CloneRoleAsync(id, req.Name, req.Key, ct);

            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "roles.cloned",
                ResourceType: "role",
                ResourceId: cloned.Id.ToString(),
                Details: new { SourceRoleId = id, cloned.Key, cloned.Name },
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                TraceId: httpContext.TraceIdentifier), ct);

            return Results.Created($"/api/v1/roles/{cloned.Id}", cloned);
        }).RequireScope(PermissionScopes.RolesManage);

        rolesGroup.MapPut("/{id:long}", async (
            long id,
            UpsertCustomRoleRequest req,
            HttpContext httpContext,
            IRoleService roleService,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            var updated = await roleService.UpdateRoleAsync(id, req, ct);

            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "roles.updated",
                ResourceType: "role",
                ResourceId: updated.Id.ToString(),
                Details: new { updated.Key, updated.Name, updated.Scopes },
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                TraceId: httpContext.TraceIdentifier), ct);

            return Results.Ok(updated);
        }).RequireScope(PermissionScopes.RolesManage);

        rolesGroup.MapDelete("/{id:long}", async (
            long id,
            HttpContext httpContext,
            IRoleService roleService,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            await roleService.DeleteCustomRoleAsync(id, ct);

            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "roles.deleted",
                ResourceType: "role",
                ResourceId: id.ToString(),
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                TraceId: httpContext.TraceIdentifier), ct);

            return Results.NoContent();
        }).RequireScope(PermissionScopes.RolesManage);

        return app;
    }

    private static async Task<IResult> AssignRolesHandlerAsync(
        long id,
        AssignUserRolesRequest req,
        HttpContext httpContext,
        IPlexUserService userService,
        IRoleService roleService,
        IAuditLogService auditLog,
        CancellationToken ct)
    {
        var principal = httpContext.GetRequiredPrincipal();
        if (!principal.Scopes.Contains(PermissionScopes.SystemAdmin))
        {
            var allRoles = await roleService.ListRolesAsync(ct);
            var requestedRoles = allRoles.Where(r => req.RoleIds.Contains(r.Id));
            if (requestedRoles.Any(r => r.Scopes.Contains(PermissionScopes.SystemAdmin)))
            {
                return Results.Json(new
                {
                    type = "https://managerr.dev/problems/err_forbidden",
                    title = "Insufficient permissions",
                    status = StatusCodes.Status403Forbidden,
                    detail = "Only administrators holding system.admin can assign roles containing system.admin.",
                    code = "ERR_RBAC_FORBIDDEN",
                    traceId = httpContext.TraceIdentifier,
                }, contentType: "application/problem+json", statusCode: StatusCodes.Status403Forbidden);
            }
        }

        var updated = await userService.AssignRolesAsync(id, req.RoleIds, ct);

        await auditLog.RecordAsync(new AuditWriteRequest(
            Action: "users.roles_updated",
            ResourceType: "user",
            ResourceId: id.ToString(),
            Details: new { RoleKeys = updated.Roles.Select(r => r.Key).ToArray() },
            ActorUserId: principal.User?.Id,
            ActorPlexUserId: principal.User?.PlexUserId,
            ActorUsername: principal.User?.Username,
            ActorApiKeyPrefix: principal.ApiKeyPrefix,
            IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
            TraceId: httpContext.TraceIdentifier), ct);

        return Results.Ok(updated);
    }
}
