using Managerr.Catalog;
using Managerr.Host.Auth;
using Managerr.Host.Hubs;
using Managerr.Host.Services;
using Managerr.Security.Rbac;
using Microsoft.AspNetCore.Authorization;

namespace Managerr.Host.Endpoints;

public sealed record ApplyMonitorPresetHttpRequest(string MonitorPreset);

public static class MediaManagementEndpoints
{
    public static IEndpointRouteBuilder MapMediaManagementEndpoints(this IEndpointRouteBuilder app)
    {
        // --- Naming Settings & Token Preview ---
        var naming = app.MapGroup("/api/v1/settings/naming").WithTags("MediaManagement");

        naming.MapGet("/", async (IMediaManagementAndDiskScanService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetNamingConfigAsync(ct)))
            .RequireScope(PermissionScopes.CatalogRead);

        naming.MapPut("/", async (UpdateNamingConfigRequest req, IMediaManagementAndDiskScanService svc, CancellationToken ct) =>
            Results.Ok(await svc.UpdateNamingConfigAsync(req, ct)))
            .RequireScope(PermissionScopes.CatalogWrite);

        naming.MapPost("/preview", async (NamingPreviewRequest req, IMediaManagementAndDiskScanService svc, CancellationToken ct) =>
            Results.Ok(await svc.PreviewNamingTemplateAsync(req, ct)))
            .RequireScope(PermissionScopes.CatalogRead);

        // --- Library Disk Scan, Unmapped Files & Library Bulk Rename ---
        var libs = app.MapGroup("/api/v1/libraries").WithTags("MediaManagement");

        libs.MapPost("/{id:long}/scan", async (
            long id,
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            IMediaManagementAndDiskScanService svc,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var lib = await catalog.GetLibraryByIdAsync(id, ct);
            if (lib is null || !catalog.CanUserAccessLibrary(principal.User, lib)) return Results.NotFound();
            return Results.Ok(await svc.ScanLibraryAsync(id, ct));
        })
        .RequireScope(PermissionScopes.CatalogWrite);

        libs.MapGet("/{id:long}/rename-preview", async (
            long id,
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            IMediaManagementAndDiskScanService svc,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var lib = await catalog.GetLibraryByIdAsync(id, ct);
            if (lib is null || !catalog.CanUserAccessLibrary(principal.User, lib)) return Results.NotFound();
            return Results.Ok(await svc.PreviewLibraryRenameAsync(id, ct));
        })
        .RequireScope(PermissionScopes.CatalogRead);

        libs.MapPost("/{id:long}/rename-execute", async (
            long id,
            RenameExecuteRequest? req,
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            IMediaManagementAndDiskScanService svc,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var lib = await catalog.GetLibraryByIdAsync(id, ct);
            if (lib is null || !catalog.CanUserAccessLibrary(principal.User, lib)) return Results.NotFound();
            return Results.Ok(await svc.ExecuteLibraryRenameAsync(id, req?.MediaFileIds, ct));
        })
        .RequireScope(PermissionScopes.CatalogWrite);

        libs.MapGet("/unmapped-files", async (
            long? libraryId,
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            IMediaManagementAndDiskScanService svc,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var accessibleLibIds = (await catalog.ListLibrariesAsync(principal.User, ct)).Select(l => l.Id).ToHashSet();
            if (libraryId.HasValue && !accessibleLibIds.Contains(libraryId.Value)) return Results.NotFound();
            var all = await svc.ListUnmappedFilesAsync(libraryId, ct);
            return Results.Ok(all.Where(u => accessibleLibIds.Contains(u.LibraryId)).ToList());
        })
        .RequireScope(PermissionScopes.CatalogRead);

        libs.MapPost("/unmapped-files/map", async (
            MapUnmappedFilesRequest req,
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            IMediaManagementAndDiskScanService svc,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var accessibleLibIds = (await catalog.ListLibrariesAsync(principal.User, ct)).Select(l => l.Id).ToHashSet();
            var unmappedById = (await svc.ListUnmappedFilesAsync(null, ct)).ToDictionary(u => u.Id);
            var filteredItems = new List<MapUnmappedFileItemRequest>();
            foreach (var item in req.Items ?? [])
            {
                if (!unmappedById.TryGetValue(item.UnmappedFileId, out var uf) || !accessibleLibIds.Contains(uf.LibraryId))
                {
                    continue;
                }
                if (await catalog.GetWorkTreeAsync(item.WorkId, principal.User, ct) is null)
                {
                    continue;
                }
                filteredItems.Add(item);
            }
            return Results.Ok(await svc.MapUnmappedFilesAsync(new MapUnmappedFilesRequest(filteredItems), ct));
        })
        .RequireScope(PermissionScopes.CatalogWrite);

        libs.MapDelete("/unmapped-files/{id:long}", async (
            long id,
            bool? deleteFromDisk,
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            IMediaManagementAndDiskScanService svc,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var accessibleLibIds = (await catalog.ListLibrariesAsync(principal.User, ct)).Select(l => l.Id).ToHashSet();
            var target = (await svc.ListUnmappedFilesAsync(null, ct)).FirstOrDefault(u => u.Id == id);
            if (target is null || !accessibleLibIds.Contains(target.LibraryId))
            {
                return Results.NotFound();
            }
            var removed = await svc.DismissUnmappedFileAsync(id, deleteFromDisk == true, ct);
            return removed ? Results.NoContent() : Results.NotFound();
        }).RequireScope(PermissionScopes.CatalogWrite);

        // --- Work-Level Disk Scan, Rename Preview/Execute, Monitor Presets & Mass Editor ---
        var cat = app.MapGroup("/api/v1/catalog/works").WithTags("Catalog");

        cat.MapPost("/{id:long}/scan", async (
            long id,
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            IMediaManagementAndDiskScanService svc,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var tree = await catalog.GetWorkTreeAsync(id, principal.User, ct);
            if (tree is null) return Results.NotFound();
            return Results.Ok(await svc.ScanWorkAsync(id, ct));
        })
        .RequireScope(PermissionScopes.CatalogWrite);

        cat.MapPost("/{id:long}/scan-and-verify", async (
            long id,
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            IMediaManagementAndDiskScanService svc,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var tree = await catalog.GetWorkTreeAsync(id, principal.User, ct);
            if (tree is null) return Results.NotFound();
            return Results.Ok(await svc.ScanAndVerifyWorkAsync(id, ct));
        })
        .RequireScope(PermissionScopes.CatalogWrite);

        cat.MapPost("/bulk-scan-and-verify", async (
            BulkScanAndVerifyWorksRequest req,
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            IMediaManagementAndDiskScanService svc,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var candidateIds = (req.WorkIds ?? []).Distinct().Take(50).ToList();
            var accessibleIds = new List<long>();
            foreach (var wid in candidateIds)
            {
                var tree = await catalog.GetWorkTreeAsync(wid, principal.User, ct);
                if (tree is not null)
                {
                    accessibleIds.Add(wid);
                }
            }
            return Results.Ok(await svc.ScanAndVerifyWorksAsync(accessibleIds, ct));
        })
        .RequireScope(PermissionScopes.CatalogWrite);

        cat.MapGet("/{id:long}/rename-preview", async (
            long id,
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            IMediaManagementAndDiskScanService svc,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            if (await catalog.GetWorkTreeAsync(id, principal.User, ct) is null) return Results.NotFound();
            return Results.Ok(await svc.PreviewWorkRenameAsync(id, ct));
        })
        .RequireScope(PermissionScopes.CatalogRead);

        cat.MapPost("/{id:long}/rename-execute", async (
            long id,
            RenameExecuteRequest? req,
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            IMediaManagementAndDiskScanService svc,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            if (await catalog.GetWorkTreeAsync(id, principal.User, ct) is null) return Results.NotFound();
            return Results.Ok(await svc.ExecuteWorkRenameAsync(id, req?.MediaFileIds, ct));
        })
        .RequireScope(PermissionScopes.CatalogWrite);

        cat.MapPost("/{id:long}/monitor-preset", async (
            long id,
            ApplyMonitorPresetHttpRequest req,
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            if (await catalog.GetWorkTreeAsync(id, principal.User, ct) is null) return Results.NotFound();
            return Results.Ok(await catalog.ApplyMonitorPresetAsync(id, req.MonitorPreset, ct));
        })
        .RequireScope(PermissionScopes.CatalogWrite);

        cat.MapPost("/bulk-edit", async (
            BulkEditWorksRequest req,
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            IMediaManagementAndDiskScanService svc,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            if (req.LibraryId.HasValue)
            {
                var targetLib = await catalog.GetLibraryByIdAsync(req.LibraryId.Value, ct);
                if (targetLib is null || !catalog.CanUserAccessLibrary(principal.User, targetLib))
                {
                    return Results.Forbid();
                }
            }

            var accessibleIds = new List<long>();
            foreach (var wid in (req.WorkIds ?? []).Distinct())
            {
                if (await catalog.GetWorkTreeAsync(wid, principal.User, ct) is not null)
                {
                    accessibleIds.Add(wid);
                }
            }

            try
            {
                var result = await catalog.BulkEditWorksAsync(req with { WorkIds = accessibleIds }, ct);
                if (req.LanguagePolicyId.HasValue || req.ClearLanguagePolicy || req.QualityProfileId.HasValue || req.ClearQualityProfile || !string.IsNullOrWhiteSpace(req.OriginalLanguage))
                {
                    foreach (var wid in accessibleIds)
                    {
                        await svc.ReevaluateWorkFilesAgainstPolicyAsync(wid, ct);
                    }
                }

                return Results.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .RequireScope(PermissionScopes.CatalogWrite);

        // --- Server-Sent Events Realtime Stream ---
        app.MapGet("/api/v1/realtime/stream", async (
            HttpContext httpContext,
            IRealtimeBroadcaster broadcaster,
            CancellationToken ct) =>
        {
            httpContext.Response.ContentType = "text/event-stream";
            httpContext.Response.Headers.CacheControl = "no-cache";
            httpContext.Response.Headers["X-Accel-Buffering"] = "no";

            var scopes = httpContext.User?.FindAll("scope").Select(c => c.Value).ToList() ?? [];
            var (subId, reader) = broadcaster.SubscribeSse(scopes);

            try
            {
                await httpContext.Response.WriteAsync("event: connected\ndata: {\"ok\":true}\n\n", ct);
                await httpContext.Response.Body.FlushAsync(ct);

                while (!ct.IsCancellationRequested)
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                    try
                    {
                        if (await reader.WaitToReadAsync(linkedCts.Token))
                        {
                            while (reader.TryRead(out var ev))
                            {
                                var safeEvent = (ev.EventName ?? "message").Replace("\r", "").Replace("\n", "");
                                var safeData = (ev.JsonPayload ?? "{}").Replace("\r", "").Replace("\n", "");
                                await httpContext.Response.WriteAsync($"event: {safeEvent}\ndata: {safeData}\n\n", ct);
                                await httpContext.Response.Body.FlushAsync(ct);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        await httpContext.Response.WriteAsync(": heartbeat\n\n", ct);
                        await httpContext.Response.Body.FlushAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Disconnected
            }
            finally
            {
                broadcaster.UnsubscribeSse(subId);
            }
        }).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = ManagerrAuthConstants.SchemeName });

        return app;
    }
}
