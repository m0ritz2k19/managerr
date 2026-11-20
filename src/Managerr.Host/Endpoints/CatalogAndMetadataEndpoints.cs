using System.Text.Json.Serialization;
using Managerr.Catalog;
using Managerr.Datastore.Services;
using Managerr.Downloaders;
using Managerr.Downloaders.Parser;
using Managerr.Host.Auth;
using Managerr.Host.Services;
using Managerr.Indexers;
using Managerr.Indexers.Torznab;
using Managerr.Metadata;
using Managerr.Migration;
using Managerr.Profiles;
using Managerr.Security.Rbac;
using Managerr.Security.Users;

namespace Managerr.Host.Endpoints;

public sealed record RefreshWorkMetadataHttpRequest(
    string? SwitchProvider = null,
    string? EpisodeOrderingScheme = null);

public sealed record DecideMediaRequestHttpRequest(bool Approve);

public sealed record SidecarParseHttpRequest(string Format, string XmlContent, string? Vertical = null);

public sealed record UpdateMetadataProviderSettingsHttpRequest(
    string? ApiKey = null,
    bool? ClearApiKey = null,
    string? CustomBaseUrl = null,
    string? AnidbClientVer = null,
    string? AnidbTitleSearchUrl = null);

public sealed record CatalogAutoSearchResultDto(
    long WorkId,
    int CandidatesFound,
    bool Grabbed,
    string Message,
    string? SelectedRelease = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ContainerId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? UnitId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SeasonNumber = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? GrabbedCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GrabReleaseOutcome? GrabOutcome = null);

public static partial class CatalogAndMetadataEndpoints
{
    public static IEndpointRouteBuilder MapCatalogAndMetadataEndpoints(this IEndpointRouteBuilder app)
    {
        // --- Libraries ---
        var libs = app.MapGroup("/api/v1/libraries").WithTags("Libraries");

        libs.MapGet("/", async (HttpContext ctx, IPolymorphicCatalogService catalog, CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var list = await catalog.ListLibrariesAsync(principal.User, ct);
            return Results.Ok(list);
        }).RequireScope(PermissionScopes.CatalogRead);

        libs.MapPost("/", async (
            HttpContext ctx,
            CreateLibraryRequest req,
            IPolymorphicCatalogService catalog,
            IAuditLogService audit,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var created = await catalog.CreateLibraryAsync(req, ct);
            await audit.RecordAsync(new AuditWriteRequest(
                Action: "library.created",
                ResourceType: "library",
                ResourceId: created.Id.ToString(),
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                Details: new { created.Name, created.MediaType, created.PlexSectionId },
                TraceId: ctx.TraceIdentifier), ct);
            return Results.Created($"/api/v1/libraries/{created.Id}", created);
        }).RequireScope(PermissionScopes.CatalogWrite);

        libs.MapPatch("/{id:long}", async (
            long id,
            UpdateLibraryRequest req,
            IPolymorphicCatalogService catalog,
            CancellationToken ct) =>
        {
            var updated = await catalog.UpdateLibraryAsync(id, req, ct);
            return Results.Ok(updated);
        }).RequireScope(PermissionScopes.CatalogWrite);

        libs.MapDelete("/{id:long}", async (long id, IPolymorphicCatalogService catalog, CancellationToken ct) =>
        {
            await catalog.DeleteLibraryAsync(id, ct);
            return Results.NoContent();
        }).RequireScope(PermissionScopes.CatalogDelete);

        // --- 4-Tier Polymorphic Catalog ---
        var cat = app.MapGroup("/api/v1/catalog").WithTags("Catalog");

        cat.MapGet("/works", async (
            HttpContext ctx,
            long? libraryId,
            string? libraryIds,
            string? vertical,
            string? verticals,
            string? q,
            bool? monitored,
            string? availability,
            string? classification,
            string? health,
            string? sort,
            string? order,
            int? limit,
            int? offset,
            IPolymorphicCatalogService catalog,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var filter = new CatalogWorkQueryFilter(
                LibraryId: libraryId,
                LibraryIds: libraryIds,
                Vertical: vertical ?? verticals,
                Query: q,
                Monitored: monitored,
                Availability: availability,
                Classification: classification,
                Health: health,
                Sort: sort,
                Order: order,
                Limit: limit,
                Offset: offset);
            var works = await catalog.QueryWorksAsync(filter, principal.User, ct);
            return Results.Ok(works);
        }).RequireScope(PermissionScopes.CatalogRead);

        cat.MapGet("/works/{id:long}", async (
            long id,
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var tree = await catalog.GetWorkTreeAsync(id, principal.User, ct);
            return tree is null ? Results.NotFound() : Results.Ok(tree);
        }).RequireScope(PermissionScopes.CatalogRead);

        cat.MapPost("/works", async (
            HttpContext ctx,
            CreateMediaWorkRequest req,
            IPolymorphicCatalogService catalog,
            IAuditLogService audit,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            try
            {
                var created = await catalog.AddWorkAsync(req, principal.User, ct);
                await audit.RecordAsync(new AuditWriteRequest(
                    Action: "catalog.work_added",
                    ResourceType: "media_work",
                    ResourceId: created.Work.Id.ToString(),
                    ActorUserId: principal.User?.Id,
                    ActorPlexUserId: principal.User?.PlexUserId,
                    ActorUsername: principal.User?.Username,
                    ActorApiKeyPrefix: principal.ApiKeyPrefix,
                    IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                    Details: new { created.Work.Title, created.Work.WorkKind, created.Work.LibraryId },
                    TraceId: ctx.TraceIdentifier), ct);
                return Results.Created($"/api/v1/catalog/works/{created.Work.Id}", created);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireScope(PermissionScopes.CatalogWrite);

        cat.MapPost("/works/auto-classify", async (
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            IAuditLogService audit,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var report = await catalog.AutoClassifyCatalogWorksAsync(ct);
            await audit.RecordAsync(new AuditWriteRequest(
                Action: "catalog.works_auto_classified",
                ResourceType: "media_work",
                ResourceId: "bulk",
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                Details: new { report.EvaluatedCount, report.UpdatedCount, report.CountsByClassification },
                TraceId: ctx.TraceIdentifier), ct);
            return Results.Ok(report);
        }).RequireScope(PermissionScopes.CatalogWrite);

        cat.MapPatch("/works/{id:long}", async (
            long id,
            UpdateMediaWorkRequest req,
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            IMediaManagementAndDiskScanService diskScanService,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            if (await catalog.GetWorkTreeAsync(id, principal.User, ct) is null)
            {
                return Results.NotFound();
            }

            try
            {
                var updated = await catalog.UpdateWorkAsync(id, req, ct);
                if (req.LanguagePolicyId.HasValue || req.ClearLanguagePolicy || req.QualityProfileId.HasValue || req.ClearQualityProfile || !string.IsNullOrWhiteSpace(req.OriginalLanguage))
                {
                    await diskScanService.ReevaluateWorkFilesAgainstPolicyAsync(id, ct);
                    updated = (await catalog.GetWorkTreeAsync(id, principal.User, ct)) ?? updated;
                }
                return Results.Ok(updated);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireScope(PermissionScopes.CatalogWrite);

        cat.MapPost("/works/{id:long}/refresh", async (
            long id,
            RefreshWorkMetadataHttpRequest? req,
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            if (await catalog.GetWorkTreeAsync(id, principal.User, ct) is null)
            {
                return Results.NotFound();
            }

            try
            {
                var refreshed = await catalog.RefreshWorkMetadataAsync(
                    id,
                    req?.SwitchProvider,
                    req?.EpisodeOrderingScheme,
                    ct);
                return Results.Ok(refreshed);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireScope(PermissionScopes.CatalogWrite);

        cat.MapPost("/works/{id:long}/auto-search", async (
            long id,
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            IComposableProfileService profileService,
            IReleaseSearchOrchestrator searchOrchestrator,
            IUnifiedDownloadOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var result = await ExecuteWorkAutoSearchAsync(
                id,
                principal.User,
                catalog,
                profileService,
                searchOrchestrator,
                orchestrator,
                ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireScope(PermissionScopes.IndexersSearch);

        cat.MapPost("/works/{id:long}/containers/{containerId:long}/auto-search", async (
            long id,
            long containerId,
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            IComposableProfileService profileService,
            IReleaseSearchOrchestrator searchOrchestrator,
            IUnifiedDownloadOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var result = await ExecuteContainerAutoSearchAsync(
                id,
                containerId,
                principal.User,
                catalog,
                profileService,
                searchOrchestrator,
                orchestrator,
                ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireScope(PermissionScopes.IndexersSearch);

        cat.MapPost("/works/{id:long}/units/{unitId:long}/auto-search", async (
            long id,
            long unitId,
            HttpContext ctx,
            IPolymorphicCatalogService catalog,
            IComposableProfileService profileService,
            IReleaseSearchOrchestrator searchOrchestrator,
            IUnifiedDownloadOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var result = await ExecuteUnitAutoSearchAsync(
                id,
                unitId,
                principal.User,
                catalog,
                profileService,
                searchOrchestrator,
                orchestrator,
                ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireScope(PermissionScopes.IndexersSearch);

        cat.MapDelete("/works/{id:long}", async (
            long id,
            bool? deleteFilesOnDisk,
            IPolymorphicCatalogService catalog,
            CancellationToken ct) =>
        {
            await catalog.DeleteWorkWithOptionsAsync(id, deleteFilesOnDisk == true, ct);
            return Results.NoContent();
        }).RequireScope(PermissionScopes.CatalogDelete);

        cat.MapPatch("/containers/{containerId:long}", async (
            long containerId,
            UpdateMediaContainerRequest req,
            IPolymorphicCatalogService catalog,
            CancellationToken ct) =>
        {
            var updatedTree = await catalog.UpdateContainerAsync(containerId, req, ct);
            return Results.Ok(updatedTree);
        }).RequireScope(PermissionScopes.CatalogWrite);

        cat.MapPatch("/units/{unitId:long}", async (
            long unitId,
            UpdateMediaUnitRequest req,
            IPolymorphicCatalogService catalog,
            CancellationToken ct) =>
        {
            var updated = await catalog.UpdateUnitAsync(unitId, req, ct);
            return Results.Ok(updated);
        }).RequireScope(PermissionScopes.CatalogWrite);

        cat.MapPost("/files", async (
            HttpContext ctx,
            AttachMediaFileRequest req,
            IPolymorphicCatalogService catalog,
            Managerr.Datastore.ISqliteDbFactory dbFactory,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            await using (var conn = await dbFactory.OpenReadConnectionAsync(Managerr.Datastore.DatabaseKind.Core, ct))
            {
                var workId = await Dapper.SqlMapper.ExecuteScalarAsync<long?>(
                    conn,
                    "SELECT work_id FROM media_units WHERE id = @Id;",
                    new { Id = req.UnitId });
                if (!workId.HasValue || await catalog.GetWorkTreeAsync(workId.Value, principal.User, ct) is null)
                {
                    return Results.NotFound();
                }
            }

            var attached = await catalog.AttachFileAsync(req, ct);
            return Results.Created($"/api/v1/catalog/files/{attached.Id}", attached);
        }).RequireScope(PermissionScopes.CatalogWrite);

        // --- Calendar ---
        app.MapGet("/api/v1/calendar", async (
            HttpContext ctx,
            string? start,
            string? end,
            IPolymorphicCatalogService catalog,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var startDate = DateOnly.TryParse(start, out var s) ? s : DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7));
            var endDate = DateOnly.TryParse(end, out var e) ? e : DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
            var items = await catalog.GetCalendarAsync(startDate, endDate, principal.User, ct);
            return Results.Ok(items);
        }).WithTags("Calendar").RequireScope(PermissionScopes.CatalogRead);

        // --- PlexUser Media Requests ---
        var reqGroup = app.MapGroup("/api/v1/requests").WithTags("Requests");

        reqGroup.MapGet("/", async (HttpContext ctx, IPolymorphicCatalogService catalog, CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var list = await catalog.ListRequestsAsync(principal.User, ct);
            return Results.Ok(list);
        }).RequireScope(PermissionScopes.CatalogRead);

        reqGroup.MapPost("/", async (
            HttpContext ctx,
            SubmitMediaRequestCommand cmd,
            IPolymorphicCatalogService catalog,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            if (principal.User is null)
            {
                throw new ArgumentException("Media requests require an authenticated Plex user context.");
            }

            var created = await catalog.SubmitRequestAsync(principal.User, cmd, ct);
            return Results.Created($"/api/v1/requests/{created.Id}", created);
        }).RequireScope(PermissionScopes.CatalogRequest);

        reqGroup.MapPost("/{id:long}/decide", async (
            long id,
            HttpContext ctx,
            DecideMediaRequestHttpRequest body,
            IPolymorphicCatalogService catalog,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            if (principal.User is null)
            {
                throw new ArgumentException("Approving or declining requests requires an authenticated Plex user.");
            }

            var updated = await catalog.DecideRequestAsync(id, body.Approve, principal.User, ct);
            return Results.Ok(updated);
        }).RequireScope(PermissionScopes.CatalogAutoApprove);

        // --- Pluggable Metadata Federation, Crosswalk, Sidecars & Custom Webhooks ---
        var meta = app.MapGroup("/api/v1/metadata").WithTags("Metadata");

        meta.MapGet("/providers", async (
            string? vertical,
            IMetadataFederationService federation,
            ICustomHttpMetadataProviderService webhooks,
            Managerr.Datastore.Services.IHybridSqliteCache cache,
            CancellationToken ct) =>
        {
            var builtIn = federation.ListBuiltInProviders(vertical);
            var custom = await webhooks.ListAsync(ct);
            var defaultWaterfall = string.IsNullOrWhiteSpace(vertical) ? [] : federation.GetDefaultWaterfall(vertical);
            var stats = await cache.GetStatsAsync(ct);
            return Results.Ok(new
            {
                builtInProviders = builtIn,
                customWebhooks = custom,
                defaultWaterfall,
                cacheStats = new
                {
                    totalEntries = stats.TotalEntries,
                    activeEntries = stats.ActiveEntries,
                    expiredEntries = stats.ExpiredEntries,
                    searchTtlHours = 24,
                    lookupTtlHours = 168,
                    nightlyRefreshHours = 24,
                },
            });
        }).RequireScope(PermissionScopes.CatalogRead);

        meta.MapGet("/search", async (
            string q,
            string? vertical,
            string? providers,
            IMetadataFederationService federation,
            CancellationToken ct) =>
        {
            var customWaterfall = string.IsNullOrWhiteSpace(providers)
                ? null
                : providers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var results = await federation.SearchWaterfallAsync(q, vertical ?? "all", customWaterfall, ct);
            return Results.Ok(results);
        }).RequireScope(PermissionScopes.CatalogRead);

        meta.MapGet("/crosswalk", async (
            string provider,
            string externalId,
            IExternalIdCrosswalkService crosswalk,
            CancellationToken ct) =>
        {
            var connected = await crosswalk.ResolveConnectedIdsAsync(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [provider] = externalId },
                ct);
            return Results.Ok(connected);
        }).RequireScope(PermissionScopes.CatalogRead);

        meta.MapPost("/crosswalk", async (
            IReadOnlyList<ExternalIdCrosswalkEdge> edges,
            IExternalIdCrosswalkService crosswalk,
            CancellationToken ct) =>
        {
            var count = await crosswalk.ImportBatchAsync(edges, ct);
            return Results.Ok(new { written = count });
        }).RequireScope(PermissionScopes.CatalogWrite);

        meta.MapPost("/sidecar/parse", (
            SidecarParseHttpRequest req,
            ISidecarMetadataService sidecars) =>
        {
            var fmt = req.Format.Trim().ToLowerInvariant();
            var candidate = fmt == "opf"
                ? sidecars.ParseCalibreOpf(req.XmlContent, req.Vertical ?? MetadataVerticals.Ebooks)
                : sidecars.ParseKodiJellyfinNfo(req.XmlContent, req.Vertical ?? MetadataVerticals.Movies);
            return Results.Ok(candidate);
        }).RequireScope(PermissionScopes.CatalogRead);

        meta.MapPost("/sidecar/export/{workId:long}", async (
            HttpContext ctx,
            long workId,
            string? format,
            IPolymorphicCatalogService catalog,
            ISidecarMetadataService sidecars,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var tree = await catalog.GetWorkTreeAsync(workId, principal.User, ct);
            if (tree is null)
            {
                return Results.NotFound();
            }

            var w = tree.Work;
            var candidate = new MetadataWorkCandidate(
                ProviderKey: w.PrimaryProvider,
                ProviderItemId: w.Id.ToString(),
                Vertical: w.WorkKind,
                Title: w.Title,
                AltTitles: w.AltTitles,
                OriginalLanguage: w.OriginalLanguage,
                Year: w.Year,
                Status: w.Status,
                Overview: w.Overview,
                PosterUrl: w.PosterUrl,
                ExternalIds: w.ExternalIds,
                Containers: []);

            var useOpf = string.Equals(format, "opf", StringComparison.OrdinalIgnoreCase) ||
                         w.WorkKind is MetadataVerticals.Ebooks or MetadataVerticals.Audiobooks;
            var xml = useOpf ? sidecars.WriteCalibreOpf(candidate) : sidecars.WriteKodiJellyfinNfo(candidate);
            return Results.Ok(new { format = useOpf ? "opf" : "nfo", xml });
        }).RequireScope(PermissionScopes.CatalogRead);

        meta.MapGet("/webhooks", async (ICustomHttpMetadataProviderService webhooks, CancellationToken ct) =>
        {
            return Results.Ok(await webhooks.ListAsync(ct));
        }).RequireScope(PermissionScopes.CatalogRead);

        meta.MapPost("/webhooks", async (
            UpsertCustomMetadataWebhookRequest req,
            ICustomHttpMetadataProviderService webhooks,
            CancellationToken ct) =>
        {
            var created = await webhooks.CreateAsync(req, ct);
            return Results.Created($"/api/v1/metadata/webhooks/{created.Id}", created);
        }).RequireScope(PermissionScopes.SystemAdmin);

        meta.MapDelete("/webhooks/{id:long}", async (
            long id,
            ICustomHttpMetadataProviderService webhooks,
            CancellationToken ct) =>
        {
            await webhooks.DeleteAsync(id, ct);
            return Results.NoContent();
        }).RequireScope(PermissionScopes.SystemAdmin);

        meta.MapGet("/providers/settings", async (
            IMetadataFederationService federation,
            IAppSettingsService appSettings,
            CancellationToken ct) =>
        {
            var providers = federation.ListBuiltInProviders(null);
            var list = new List<object>(providers.Count);
            foreach (var p in providers)
            {
                list.Add(await BuildMetadataProviderSettingsDtoAsync(p, appSettings, ct));
            }
            return Results.Ok(list);
        }).RequireScope(PermissionScopes.SystemAdmin);

        meta.MapPut("/providers/{providerKey}/settings", async (
            string providerKey,
            HttpContext ctx,
            UpdateMetadataProviderSettingsHttpRequest req,
            IMetadataFederationService federation,
            IAppSettingsService appSettings,
            Managerr.Datastore.ISqliteDbFactory dbFactory,
            Managerr.Common.Crypto.ISecretProtector secretProtector,
            IAuditLogService audit,
            CancellationToken ct) =>
        {
            var key = providerKey.Trim().ToLowerInvariant();
            var matched = federation.ListBuiltInProviders(null)
                .FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (matched is null)
            {
                return Results.NotFound(new { error = $"Unknown metadata provider '{providerKey}'." });
            }

            if (!string.IsNullOrWhiteSpace(req.CustomBaseUrl) && !IsValidHttpUrl(req.CustomBaseUrl))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid Custom Base URL",
                    detail: "customBaseUrl must be empty or a valid http:// or https:// URL.",
                    extensions: new Dictionary<string, object?> { ["errorCode"] = "ERR_INVALID_URL" });
            }

            if (string.Equals(key, "anidb", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(req.AnidbTitleSearchUrl)
                && !IsValidHttpUrl(req.AnidbTitleSearchUrl))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid AniDB Title Search URL",
                    detail: "anidbTitleSearchUrl must be empty or a valid http:// or https:// URL.",
                    extensions: new Dictionary<string, object?> { ["errorCode"] = "ERR_INVALID_URL" });
            }

            var hasNewApiKey = !string.IsNullOrWhiteSpace(req.ApiKey)
                && !req.ApiKey.Trim().StartsWith("****", StringComparison.Ordinal);

            var now = DateTimeOffset.UtcNow.ToString("O");
            await dbFactory.ExecuteWriteAsync(
                Managerr.Datastore.DatabaseKind.Core,
                async (conn, tx) =>
                {
                    if (req.ClearApiKey == true || (req.ApiKey is not null && string.IsNullOrWhiteSpace(req.ApiKey)))
                    {
                        await Dapper.SqlMapper.ExecuteAsync(
                            conn,
                            "DELETE FROM app_settings WHERE key = @Key;",
                            new { Key = $"metadata.{key}.api_key" },
                            transaction: tx);
                    }
                    else if (hasNewApiKey)
                    {
                        var protectedKey = secretProtector.Protect(req.ApiKey!.Trim());
                        await UpsertMetadataSettingAsync(conn, tx, $"metadata.{key}.api_key", protectedKey, isSecret: true, now);
                    }

                    if (req.CustomBaseUrl is not null)
                    {
                        if (string.IsNullOrWhiteSpace(req.CustomBaseUrl))
                        {
                            await Dapper.SqlMapper.ExecuteAsync(
                                conn,
                                "DELETE FROM app_settings WHERE key = @Key;",
                                new { Key = $"metadata.{key}.base_url" },
                                transaction: tx);
                        }
                        else
                        {
                            await UpsertMetadataSettingAsync(conn, tx, $"metadata.{key}.base_url", req.CustomBaseUrl.Trim(), isSecret: false, now);
                        }
                    }

                    if (string.Equals(key, "anidb", StringComparison.OrdinalIgnoreCase))
                    {
                        if (req.AnidbClientVer is not null)
                        {
                            if (string.IsNullOrWhiteSpace(req.AnidbClientVer))
                            {
                                await Dapper.SqlMapper.ExecuteAsync(
                                    conn,
                                    "DELETE FROM app_settings WHERE key = @Key;",
                                    new { Key = "metadata.anidb.clientver" },
                                    transaction: tx);
                            }
                            else
                            {
                                await UpsertMetadataSettingAsync(conn, tx, "metadata.anidb.clientver", req.AnidbClientVer.Trim(), isSecret: false, now);
                            }
                        }

                        if (req.AnidbTitleSearchUrl is not null)
                        {
                            if (string.IsNullOrWhiteSpace(req.AnidbTitleSearchUrl))
                            {
                                await Dapper.SqlMapper.ExecuteAsync(
                                    conn,
                                    "DELETE FROM app_settings WHERE key = @Key;",
                                    new { Key = "metadata.anidb.title_search_url" },
                                    transaction: tx);
                            }
                            else
                            {
                                await UpsertMetadataSettingAsync(conn, tx, "metadata.anidb.title_search_url", req.AnidbTitleSearchUrl.Trim(), isSecret: false, now);
                            }
                        }
                    }
                },
                ct);

            var refreshedProvider = federation.ListBuiltInProviders(null)
                .FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) ?? matched;
            var result = await BuildMetadataProviderSettingsDtoAsync(
                refreshedProvider,
                appSettings,
                ct);

            var principal = ctx.GetRequiredPrincipal();
            await audit.RecordAsync(new AuditWriteRequest(
                Action: "metadata.provider_settings_updated",
                ResourceType: "metadata_provider",
                ResourceId: key,
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                Details: new
                {
                    ProviderKey = key,
                    ClearedApiKey = req.ClearApiKey == true,
                    UpdatedApiKey = hasNewApiKey,
                    HasCustomBaseUrl = !string.IsNullOrWhiteSpace(req.CustomBaseUrl),
                },
                TraceId: ctx.TraceIdentifier), ct);

            return Results.Ok(result);
        }).RequireScope(PermissionScopes.SystemAdmin);

        return app;
    }

    private static async Task<object> BuildMetadataProviderSettingsDtoAsync(
        MetadataProviderDescriptor descriptor,
        IAppSettingsService appSettings,
        CancellationToken ct)
    {
        var key = descriptor.Key.Trim().ToLowerInvariant();
        var maskedSecret = await appSettings.GetMaskedSecretAsync($"metadata.{key}.api_key", ct);
        var plainFallback = maskedSecret.IsSet ? null : await appSettings.GetAsync($"metadata.{key}.api_key", ct);
        var customBaseUrl = await appSettings.GetAsync($"metadata.{key}.base_url", ct);
        var providerReady = string.Equals(descriptor.ConfigurationStatus, MetadataProviderConfigurationStates.Ready, StringComparison.OrdinalIgnoreCase);
        var hasApiKey = maskedSecret.IsSet || !string.IsNullOrWhiteSpace(plainFallback) || (descriptor.RequiresApiKey && providerReady);
        var configStatus = descriptor.RequiresApiKey && !hasApiKey
            ? MetadataProviderConfigurationStates.NotConfigured
            : MetadataProviderConfigurationStates.Ready;
        var isAnidb = string.Equals(key, "anidb", StringComparison.OrdinalIgnoreCase);
        var anidbClientVer = isAnidb ? await appSettings.GetAsync("metadata.anidb.clientver", ct) : null;
        var anidbTitleSearchUrl = isAnidb ? await appSettings.GetAsync("metadata.anidb.title_search_url", ct) : null;

        return new
        {
            key = descriptor.Key,
            displayName = descriptor.DisplayName,
            requiresApiKey = descriptor.RequiresApiKey,
            configurationStatus = configStatus,
            defaultBaseUrl = !string.IsNullOrWhiteSpace(descriptor.DefaultBaseUrl)
                ? descriptor.DefaultBaseUrl
                : GetDefaultMetadataProviderBaseUrl(key),
            customBaseUrl = string.IsNullOrWhiteSpace(customBaseUrl) ? null : customBaseUrl.Trim(),
            hasApiKey,
            maskedApiKey = maskedSecret.IsSet
                ? maskedSecret.MaskedHint
                : MaskMetadataApiKey(plainFallback, hasApiKey && descriptor.RequiresApiKey),
            anidbClientVer = isAnidb ? (string.IsNullOrWhiteSpace(anidbClientVer) ? null : anidbClientVer.Trim()) : null,
            anidbTitleSearchUrl = isAnidb ? (string.IsNullOrWhiteSpace(anidbTitleSearchUrl) ? null : anidbTitleSearchUrl.Trim()) : null,
        };
    }

    private static string? GetDefaultMetadataProviderBaseUrl(string key) =>
        key.ToLowerInvariant() switch
        {
            "tmdb" => "https://api.themoviedb.org/3",
            "tvdb" => "https://api4.thetvdb.com/v4",
            "skyhook" => "https://skyhook.sonarr.tv/v1/tvdb",
            "tvmaze" => "https://api.tvmaze.com",
            "anilist" => "https://graphql.anilist.co",
            "anidb" => "https://api.anidb.net:9001",
            "musicbrainz" => "https://musicbrainz.org/ws/2",
            "deezer" => "https://api.deezer.com",
            "discogs" => "https://api.discogs.com",
            "openlibrary" => "https://openlibrary.org",
            "googlebooks" => "https://www.googleapis.com/books/v1",
            "audnexus" => "https://api.audnex.us",
            "applebooks" => "https://itunes.apple.com",
            "comicvine" => "https://comicvine.gamespot.com/api",
            "mangadex" => "https://api.mangadex.org",
            _ => null,
        };

    private static string? MaskMetadataApiKey(string? apiKey, bool hasProviderKeyFallback)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return hasProviderKeyFallback ? "********" : null;
        }

        var trimmed = apiKey.Trim();
        if (trimmed.Length <= 4)
        {
            return "****";
        }

        return new string('*', Math.Min(8, trimmed.Length - 4)) + trimmed[^4..];
    }

    private static Task UpsertMetadataSettingAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        string key,
        string value,
        bool isSecret,
        string nowIso) =>
        Dapper.SqlMapper.ExecuteAsync(
            conn,
            """
            INSERT INTO app_settings (key, value, is_secret, updated_at)
            VALUES (@Key, @Val, @IsSecret, @Now)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                is_secret = excluded.is_secret,
                updated_at = excluded.updated_at;
            """,
            new { Key = key, Val = value, IsSecret = isSecret ? 1 : 0, Now = nowIso },
            transaction: tx);

    private static (string? ImdbId, int? TmdbId, int? TvdbId) ExtractExternalNumericIds(
        IReadOnlyDictionary<string, string> externalIds)
    {
        externalIds.TryGetValue("imdb", out var imdbId);
        var tmdbId = externalIds.TryGetValue("tmdb", out var tmStr) && int.TryParse(tmStr, out var tm) ? tm : (int?)null;
        var tvdbId = externalIds.TryGetValue("tvdb", out var tvStr) && int.TryParse(tvStr, out var tv) ? tv : (int?)null;
        return (imdbId, tmdbId, tvdbId);
    }

    private static async Task<IReadOnlyList<IndexedReleaseCandidate>> SearchSeasonOrEpisodeWithFallbackAsync(
        IUnifiedIndexerService indexerService,
        IReleaseParserRouter parserRouter,
        string title,
        string vertical,
        string? imdbId,
        int? tmdbId,
        int? tvdbId,
        int season,
        int? episode,
        int? absoluteEpisode,
        CancellationToken ct)
    {
        var primary = await indexerService.SearchAsync(
            new IndexerSearchQuery(
                Query: title,
                MediaVertical: vertical,
                ImdbId: imdbId,
                TmdbId: tmdbId,
                TvdbId: tvdbId,
                Season: season,
                Episode: episode,
                Interactive: false),
            ct);

        var combined = primary.ToList();
        var seen = combined
            .Select(r => r.Guid ?? r.DownloadUrl)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var hasScopedMatch = combined.Any(r =>
        {
            var p = parserRouter.Parse(r.Title, r.MediaVertical);
            return episode.HasValue
                ? MatchesEpisodeUnit(p, season, episode.Value, absoluteEpisode, allowSeasonPack: true)
                : p.SeasonNumber == season;
        });

        if (!hasScopedMatch)
        {
            var seToken = episode.HasValue ? $"S{season:D2}E{episode.Value:D2}" : $"S{season:D2}";
            var fallback = await indexerService.SearchAsync(
                new IndexerSearchQuery(
                    Query: $"{title} {seToken}",
                    MediaVertical: vertical,
                    Interactive: false),
                ct);
            foreach (var r in fallback)
            {
                if (seen.Add(r.Guid ?? r.DownloadUrl))
                {
                    combined.Add(r);
                }
            }

            if (combined.Count == 0 &&
                string.Equals(vertical, MetadataVerticals.Anime, StringComparison.OrdinalIgnoreCase) &&
                absoluteEpisode.HasValue)
            {
                var absFallback = await indexerService.SearchAsync(
                    new IndexerSearchQuery(
                        Query: $"{title} {absoluteEpisode.Value:D2}",
                        MediaVertical: vertical,
                        Interactive: false),
                    ct);
                foreach (var r in absFallback)
                {
                    if (seen.Add(r.Guid ?? r.DownloadUrl))
                    {
                        combined.Add(r);
                    }
                }
            }
        }

        return combined;
    }

    private static bool MatchesEpisodeUnit(
        ParsedReleaseInfo parsed,
        int seasonNumber,
        int episodeNumber,
        int? absoluteNumber,
        bool allowSeasonPack)
    {
        if (absoluteNumber.HasValue && parsed.AbsoluteEpisodeNumbers.Contains(absoluteNumber.Value))
        {
            return true;
        }

        if (parsed.SeasonNumber != seasonNumber)
        {
            return false;
        }

        if (parsed.EpisodeNumbers.Contains(episodeNumber))
        {
            return true;
        }

        if (allowSeasonPack && parsed.IsFullSeasonPack)
        {
            return true;
        }

        return false;
    }

    private static async Task<QualityProfileSummary?> ResolveEffectiveProfileAsync(
        IComposableProfileService profileService,
        IPolymorphicCatalogService catalog,
        MediaWorkSummary work,
        long? unitProfileOverrideId,
        CancellationToken ct)
    {
        QualityProfileSummary? baseProfile = null;
        if (unitProfileOverrideId.HasValue)
        {
            var overridden = await profileService.GetProfileByIdAsync(unitProfileOverrideId.Value, ct);
            if (overridden is not null)
            {
                baseProfile = overridden;
            }
        }

        if (baseProfile is null && work.QualityProfileId.HasValue)
        {
            var workProf = await profileService.GetProfileByIdAsync(work.QualityProfileId.Value, ct);
            if (workProf is not null)
            {
                baseProfile = workProf;
            }
        }

        if (baseProfile is null)
        {
            var lib = await catalog.GetLibraryByIdAsync(work.LibraryId, ct);
            if (lib?.DefaultProfileId.HasValue == true)
            {
                var libProf = await profileService.GetProfileByIdAsync(lib.DefaultProfileId.Value, ct);
                if (libProf is not null)
                {
                    baseProfile = libProf;
                }
            }
        }

        if (baseProfile is null)
        {
            var allProfiles = await profileService.ListProfilesAsync(ct);
            if (work.WorkKind is MetadataVerticals.Movies or MetadataVerticals.Tv or MetadataVerticals.Anime)
            {
                var germanBest = allProfiles.FirstOrDefault(p =>
                    string.Equals(p.Slug, "german-first-best-quality", StringComparison.OrdinalIgnoreCase));
                if (germanBest is not null)
                {
                    baseProfile = germanBest;
                }
            }

            if (baseProfile is null)
            {
                var verticalKey = work.WorkKind is MetadataVerticals.Movies or MetadataVerticals.Tv or MetadataVerticals.Anime
                    ? "video"
                    : work.WorkKind;
                baseProfile = allProfiles.FirstOrDefault(p =>
                    string.Equals(p.MediaVertical, verticalKey, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (baseProfile is not null && work.LanguagePolicyId is { } workLpId)
        {
            var allPolicies = await profileService.ListLanguagePoliciesAsync(ct);
            var workLp = allPolicies.FirstOrDefault(p => p.Id == workLpId);
            if (workLp is not null)
            {
                return baseProfile with { LanguagePolicyId = workLp.Id, LanguagePolicy = workLp };
            }
        }

        return baseProfile;
    }

    private static string ResolvePolicyLangToken(string token, string origLang) =>
        string.Equals(token, "original", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(token, "orig", StringComparison.OrdinalIgnoreCase)
            ? origLang
            : token.ToLowerInvariant();

    private static int ScoreReleaseCandidate(
        IndexedReleaseCandidate release,
        ParsedReleaseInfo parsed,
        HashSet<string> reqAudio,
        string? originalLanguage = "en",
        QualityProfileSummary? profile = null)
    {
        var score = 0.0;
        var origLang = string.IsNullOrWhiteSpace(originalLanguage) ? "en" : originalLanguage.Trim().ToLowerInvariant();

        if (release.Title.Contains("FAKE", StringComparison.OrdinalIgnoreCase) ||
            release.Title.Contains("INCOMPLETE", StringComparison.OrdinalIgnoreCase) ||
            release.Title.Contains("CODECPACK", StringComparison.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(release.Title, @"\b(CAM|HDCAM|TELESYNC|HDTS|TELECINE)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return -5000;
        }

        var canonicalQuality = parsed.ToQualityName();
        var candAudio = parsed.DetectedLanguages
            .Select(l => l.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (release.IndexerLanguages is { Count: > 0 })
        {
            foreach (var l in release.IndexerLanguages)
            {
                if (!string.IsNullOrWhiteSpace(l)) candAudio.Add(l.Trim().ToLowerInvariant());
            }
        }
        if (candAudio.Count == 0)
        {
            candAudio.Add(origLang);
        }

        var candSubs = (parsed.IsMultiSubs ? new[] { "de", "en" } : Array.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (release.IndexerSubtitles is { Count: > 0 })
        {
            foreach (var s in release.IndexerSubtitles)
            {
                if (!string.IsNullOrWhiteSpace(s)) candSubs.Add(s.Trim().ToLowerInvariant());
            }
        }

        if (profile is not null)
        {
            var matchedQuality = profile.AllowedQualities.FirstOrDefault(q =>
                string.Equals(q.Name, canonicalQuality, StringComparison.OrdinalIgnoreCase));
            if (matchedQuality is not null)
            {
                score += matchedQuality.Score * profile.WeightQuality;
            }
            else
            {
                score -= 1500;
            }

            if (profile.LanguagePolicy is { Tiers.Count: > 0 } lp)
            {
                LanguagePolicyTier? matchedTier = null;
                foreach (var tier in lp.Tiers.OrderBy(t => t.Tier))
                {
                    var allOk = tier.AudioContainsAll.Count == 0 ||
                                tier.AudioContainsAll.All(tok => candAudio.Contains(ResolvePolicyLangToken(tok, origLang)));
                    var anyOk = tier.AudioContainsAny.Count == 0 ||
                                tier.AudioContainsAny.Any(tok => candAudio.Contains(ResolvePolicyLangToken(tok, origLang)));
                    var subsOk = tier.SubtitlesContainsAny.Count == 0 ||
                                 tier.SubtitlesContainsAny.Any(tok => candSubs.Contains(ResolvePolicyLangToken(tok, origLang)));
                    if (allOk && anyOk && subsOk)
                    {
                        matchedTier = tier;
                        break;
                    }
                }

                if (matchedTier is not null)
                {
                    score += matchedTier.Score * profile.WeightLanguage;
                }
                else
                {
                    score -= 1200;
                }
            }
        }
        else
        {
            score += canonicalQuality switch
            {
                "Remux-2160p" => 1000,
                "BluRay-2160p" => 920,
                "WEBDL-2160p" => 860,
                "WEBRip-2160p" => 800,
                "Remux-1080p" => 750,
                "BluRay-1080p" => 700,
                "WEBDL-1080p" => 650,
                "WEBRip-1080p" => 580,
                "HDTV-1080p" => 520,
                _ => 200,
            };
        }

        // Explicit unit audio requirements boost
        if (reqAudio.Count > 0 && reqAudio.All(candAudio.Contains))
        {
            score += 1200;
        }
        else if (reqAudio.Count > 0 && reqAudio.Any(candAudio.Contains))
        {
            score += 400;
        }

        if (parsed.IsDualAudio)
        {
            score += 150;
        }

        // Cinema HDR & Lossless Audio trait bonuses
        var weightTraits = profile?.WeightTraits ?? 1.0;
        if (!string.IsNullOrEmpty(parsed.HdrFormat))
        {
            score += (parsed.HdrFormat.Contains("DV", StringComparison.OrdinalIgnoreCase) ? 200 : 120) * weightTraits;
        }
        if (!string.IsNullOrEmpty(parsed.AudioCodec))
        {
            if (parsed.AudioCodec.Contains("TrueHD Atmos", StringComparison.OrdinalIgnoreCase)) score += 200 * weightTraits;
            else if (parsed.AudioCodec.Contains("Atmos", StringComparison.OrdinalIgnoreCase)) score += 100 * weightTraits;
        }
        if (string.Equals(parsed.VideoCodec, "AV1", StringComparison.OrdinalIgnoreCase))
        {
            score += 120 * weightTraits;
        }
        else if (string.Equals(parsed.VideoCodec, "HEVC", StringComparison.OrdinalIgnoreCase))
        {
            score += 80 * weightTraits;
        }

        return (int)Math.Round(score);
    }

    private static bool IsValidHttpUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
