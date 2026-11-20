using System.Text;
using Managerr.Host.Auth;
using Managerr.Host.Services;
using Managerr.Security.Rbac;

namespace Managerr.Host.Endpoints;

public static class EcosystemEndpoints
{
    public static IEndpointRouteBuilder MapEcosystemEndpoints(this IEndpointRouteBuilder app)
    {
        // --- 1. Connect / Notification Providers ---
        var notifications = app.MapGroup("/api/v1/notifications").WithTags("Notifications & Connect");

        notifications.MapGet("", async (INotificationConnectionService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListConnectionsAsync(ct)))
            .RequireScope(PermissionScopes.CatalogRead);

        notifications.MapPost("", async (
            HttpContext ctx,
            UpsertNotificationConnectionRequest req,
            INotificationConnectionService svc,
            CancellationToken ct) =>
        {
            RequireCatalogWriteOrSystemAdmin(ctx);
            var created = await svc.UpsertConnectionAsync(req, null, ct);
            return Results.Created($"/api/v1/notifications/{created.Id}", created);
        }).RequireAuthorization();

        notifications.MapPut("/{id:long}", async (
            long id,
            HttpContext ctx,
            UpsertNotificationConnectionRequest req,
            INotificationConnectionService svc,
            CancellationToken ct) =>
        {
            RequireCatalogWriteOrSystemAdmin(ctx);
            return Results.Ok(await svc.UpsertConnectionAsync(req, id, ct));
        }).RequireAuthorization();

        notifications.MapDelete("/{id:long}", async (
            long id,
            HttpContext ctx,
            INotificationConnectionService svc,
            CancellationToken ct) =>
        {
            RequireCatalogWriteOrSystemAdmin(ctx);
            await svc.DeleteConnectionAsync(id, ct);
            return Results.NoContent();
        }).RequireAuthorization();

        notifications.MapPost("/test", async (
            HttpContext ctx,
            UpsertNotificationConnectionRequest req,
            INotificationConnectionService svc,
            CancellationToken ct) =>
        {
            RequireCatalogWriteOrSystemAdmin(ctx);
            return Results.Ok(await svc.TestDraftAsync(req, ct));
        }).RequireAuthorization();

        notifications.MapPost("/{id:long}/test", async (
            long id,
            HttpContext ctx,
            INotificationConnectionService svc,
            CancellationToken ct) =>
        {
            RequireCatalogWriteOrSystemAdmin(ctx);
            return Results.Ok(await svc.TestConnectionAsync(id, ct));
        }).RequireAuthorization();

        // --- 2. Automated Import Lists & Exclusions ---
        var lists = app.MapGroup("/api/v1/import-lists").WithTags("Import Lists");

        lists.MapGet("", async (IImportListSyncService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListImportListsAsync(ct)))
            .RequireScope(PermissionScopes.CatalogRead);

        lists.MapPost("", async (
            HttpContext ctx,
            UpsertImportListRequest req,
            IImportListSyncService svc,
            CancellationToken ct) =>
        {
            RequireCatalogWriteOrSystemAdmin(ctx);
            var created = await svc.UpsertImportListAsync(req, null, ct);
            return Results.Created($"/api/v1/import-lists/{created.Id}", created);
        }).RequireAuthorization();

        lists.MapPut("/{id:long}", async (
            long id,
            HttpContext ctx,
            UpsertImportListRequest req,
            IImportListSyncService svc,
            CancellationToken ct) =>
        {
            RequireCatalogWriteOrSystemAdmin(ctx);
            return Results.Ok(await svc.UpsertImportListAsync(req, id, ct));
        }).RequireAuthorization();

        lists.MapDelete("/{id:long}", async (
            long id,
            HttpContext ctx,
            IImportListSyncService svc,
            CancellationToken ct) =>
        {
            RequireCatalogWriteOrSystemAdmin(ctx);
            await svc.DeleteImportListAsync(id, ct);
            return Results.NoContent();
        }).RequireAuthorization();

        lists.MapPost("/{id:long}/sync", async (
            long id,
            HttpContext ctx,
            IImportListSyncService svc,
            CancellationToken ct) =>
        {
            RequireCatalogWriteOrSystemAdmin(ctx);
            return Results.Ok(await svc.SyncImportListAsync(id, ct));
        }).RequireAuthorization();

        lists.MapPost("/sync-all", async (
            HttpContext ctx,
            IImportListSyncService svc,
            CancellationToken ct) =>
        {
            RequireCatalogWriteOrSystemAdmin(ctx);
            return Results.Ok(await svc.SyncAllEnabledListsAsync(ct));
        }).RequireAuthorization();

        lists.MapGet("/exclusions", async (IImportListSyncService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListExclusionsAsync(ct)))
            .RequireScope(PermissionScopes.CatalogRead);

        lists.MapPost("/exclusions", async (
            HttpContext ctx,
            CreateImportListExclusionRequest req,
            IImportListSyncService svc,
            CancellationToken ct) =>
        {
            RequireCatalogWriteOrSystemAdmin(ctx);
            var created = await svc.AddExclusionAsync(req, ct);
            return Results.Created($"/api/v1/import-lists/exclusions/{created.Id}", created);
        }).RequireAuthorization();

        lists.MapDelete("/exclusions/{id:long}", async (
            long id,
            HttpContext ctx,
            IImportListSyncService svc,
            CancellationToken ct) =>
        {
            RequireCatalogWriteOrSystemAdmin(ctx);
            await svc.DeleteExclusionAsync(id, ct);
            return Results.NoContent();
        }).RequireAuthorization();

        // --- 3. RFC 5545 Calendar iCal (.ics) Feed ---
        static async Task<IResult> HandleCalendarIcsAsync(
            HttpContext ctx,
            ICalendarFeedService feedService,
            int? pastDays,
            int? futureDays,
            string? vertical,
            bool? unmonitored,
            string? tags,
            CancellationToken ct)
        {
            var principal = ctx.GetRequiredPrincipal();
            var hasCalendarAccess = principal.Scopes.Contains(PermissionScopes.CatalogRead) ||
                                    principal.Scopes.Contains(PermissionScopes.CompatSonarr) ||
                                    principal.Scopes.Contains(PermissionScopes.CompatRadarr) ||
                                    principal.Scopes.Contains(PermissionScopes.SystemAdmin);
            if (!hasCalendarAccess)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var tagList = string.IsNullOrWhiteSpace(tags)
                ? null
                : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var ics = await feedService.RenderIcsFeedAsync(
                new CalendarFeedFilter(
                    PastDays: pastDays ?? 7,
                    FutureDays: futureDays ?? 60,
                    Vertical: vertical,
                    IncludeUnmonitored: unmonitored ?? false,
                    Tags: tagList),
                ct);

            ctx.Response.Headers.ContentDisposition = "inline; filename=\"managerr.ics\"";
            return Results.Text(ics, "text/calendar; charset=utf-8", Encoding.UTF8);
        }

        app.MapGet("/feed/v1/calendar/managerr.ics", HandleCalendarIcsAsync)
            .WithTags("Calendar")
            .RequireAuthorization();

        app.MapGet("/api/v1/calendar/managerr.ics", HandleCalendarIcsAsync)
            .WithTags("Calendar")
            .RequireAuthorization();

        return app;
    }

    private static void RequireCatalogWriteOrSystemAdmin(HttpContext ctx)
    {
        var principal = ctx.GetRequiredPrincipal();
        if (!principal.Scopes.Contains(PermissionScopes.CatalogWrite) &&
            !principal.Scopes.Contains(PermissionScopes.SystemAdmin))
        {
            throw new UnauthorizedAccessException("Requires 'catalog:write' or 'system:admin' scope.");
        }
    }
}
