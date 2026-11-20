using Managerr.Host.Auth;
using Managerr.Host.Services;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Microsoft.AspNetCore.Mvc;

namespace Managerr.Host.Endpoints;

public static class FilesystemEndpoints
{
    public static IEndpointRouteBuilder MapFilesystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/filesystem")
            .WithTags("Filesystem");

        group.MapGet("/", async (
            [FromQuery] string? path,
            [FromQuery] bool? includeFiles,
            HttpContext httpContext,
            IFilesystemBrowseService fsService,
            CancellationToken ct) =>
        {
            PlexUserSummary? actor = null;
            try
            {
                actor = httpContext.GetRequiredPrincipal().User;
            }
            catch
            {
            }

            try
            {
                var response = await fsService.BrowseAsync(path, includeFiles.GetValueOrDefault(), actor, ct);
                return Results.Ok(response);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .RequireAnyScope(
            PermissionScopes.CatalogWrite,
            PermissionScopes.QueueManage,
            PermissionScopes.DownloadersWrite,
            PermissionScopes.SystemAdmin,
            PermissionScopes.MigrationExecute);

        group.MapPost("/directories", async (
            CreateFilesystemDirectoryRequest request,
            HttpContext httpContext,
            IFilesystemBrowseService fsService,
            CancellationToken ct) =>
        {
            PlexUserSummary? actor = null;
            try
            {
                actor = httpContext.GetRequiredPrincipal().User;
            }
            catch
            {
            }

            try
            {
                var response = await fsService.CreateDirectoryAsync(request.Path, actor, ct);
                return Results.Ok(response);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (DirectoryNotFoundException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (IOException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .RequireAnyScope(
            PermissionScopes.CatalogWrite,
            PermissionScopes.SystemAdmin,
            PermissionScopes.MigrationExecute);

        // Sonarr v3 compat filesystem endpoint
        app.MapGet("/compat/sonarr/api/v3/filesystem", async (
            [FromQuery] string? path,
            [FromQuery] bool? includeFiles,
            HttpContext httpContext,
            IFilesystemBrowseService fsService,
            CancellationToken ct) =>
        {
            try
            {
                PlexUserSummary? actor = null;
                try { actor = httpContext.GetRequiredPrincipal().User; } catch { }
                var res = await fsService.BrowseAsync(path, includeFiles.GetValueOrDefault(), actor: actor, ct: ct);
                return Results.Ok(new
                {
                    parent = res.Parent ?? string.Empty,
                    directories = res.Directories,
                    files = res.Files,
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithTags("Compat: Sonarr")
        .RequireScope(PermissionScopes.CompatSonarr);

        // Radarr v3 compat filesystem endpoint
        app.MapGet("/compat/radarr/api/v3/filesystem", async (
            [FromQuery] string? path,
            [FromQuery] bool? includeFiles,
            HttpContext httpContext,
            IFilesystemBrowseService fsService,
            CancellationToken ct) =>
        {
            try
            {
                PlexUserSummary? actor = null;
                try { actor = httpContext.GetRequiredPrincipal().User; } catch { }
                var res = await fsService.BrowseAsync(path, includeFiles.GetValueOrDefault(), actor: actor, ct: ct);
                return Results.Ok(new
                {
                    parent = res.Parent ?? string.Empty,
                    directories = res.Directories,
                    files = res.Files,
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithTags("Compat: Radarr")
        .RequireScope(PermissionScopes.CompatRadarr);

        return app;
    }
}
