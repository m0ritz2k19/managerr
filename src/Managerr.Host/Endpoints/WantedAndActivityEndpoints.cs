using Managerr.Catalog;
using Managerr.Downloaders;
using Managerr.Downloaders.Parser;
using Managerr.Host.Auth;
using Managerr.Host.Services;
using Managerr.Indexers;
using Managerr.Metadata;
using Managerr.Migration;
using Managerr.Security.Rbac;

namespace Managerr.Host.Endpoints;

public static class WantedAndActivityEndpoints
{
    public static IEndpointRouteBuilder MapWantedAndActivityEndpoints(this IEndpointRouteBuilder app)
    {
        // --- #27 Wanted: Missing, Cutoff Unmet & Bulk Search ---
        var wanted = app.MapGroup("/api/v1/wanted").WithTags("Wanted (Missing & Cutoff Unmet)");

        wanted.MapGet("/missing", async (
            HttpContext ctx,
            int? page,
            int? pageSize,
            string? vertical,
            long? libraryId,
            string? libraryIds,
            bool? monitoredOnly,
            bool? releasedOnly,
            string? language,
            IWantedAndActivityService wantedService,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var filter = new WantedFilterQuery(
                Page: page ?? 1,
                PageSize: pageSize ?? 50,
                Vertical: vertical,
                LibraryId: libraryId,
                MonitoredOnly: monitoredOnly ?? true,
                ReleasedOnly: releasedOnly ?? false,
                Language: language,
                LibraryIds: libraryIds);
            return Results.Ok(await wantedService.GetWantedMissingAsync(filter, principal.User, ct));
        }).RequireScope(PermissionScopes.CatalogRead);

        wanted.MapGet("/cutoff-unmet", async (
            HttpContext ctx,
            int? page,
            int? pageSize,
            string? vertical,
            long? libraryId,
            string? libraryIds,
            bool? monitoredOnly,
            bool? releasedOnly,
            string? language,
            IWantedAndActivityService wantedService,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var filter = new WantedFilterQuery(
                Page: page ?? 1,
                PageSize: pageSize ?? 50,
                Vertical: vertical,
                LibraryId: libraryId,
                MonitoredOnly: monitoredOnly ?? true,
                ReleasedOnly: releasedOnly ?? false,
                Language: language,
                LibraryIds: libraryIds);
            return Results.Ok(await wantedService.GetWantedCutoffUnmetAsync(filter, principal.User, ct));
        }).RequireScope(PermissionScopes.CatalogRead);

        wanted.MapPost("/search", async (
            HttpContext ctx,
            WantedBulkSearchRequest req,
            IWantedAndActivityService wantedService,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var report = await wantedService.ExecuteBulkWantedSearchAsync(req, principal.User, ct);
            return Results.Ok(report);
        }).RequireScope(PermissionScopes.IndexersSearch);

        // --- #27 Scoped Interactive Work/Season/Episode Releases with Rejection Breakdown ---
        app.MapGet("/api/v1/catalog/works/{id:long}/releases", async (
            long id,
            long? containerId,
            long? unitId,
            int? season,
            int? episode,
            string? q,
            string? protocol,
            bool? approvedOnly,
            bool? hideBlocklisted,
            string? sort,
            string? order,
            int? limit,
            HttpContext ctx,
            IPolymorphicCatalogService catalogService,
            IReleaseSearchOrchestrator searchOrchestrator,
            IWantedAndActivityService wantedService,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var tree = await catalogService.GetWorkTreeAsync(id, principal.User, ct);
            if (tree is null)
            {
                return Results.NotFound();
            }

            var w = tree.Work;
            MediaContainerDetail? targetContainer = null;
            MediaUnitDetail? targetUnit = null;
            if (unitId.HasValue)
            {
                foreach (var c in tree.Containers)
                {
                    var found = c.Units.FirstOrDefault(u => u.Id == unitId.Value);
                    if (found is not null)
                    {
                        targetContainer = c;
                        targetUnit = found;
                        break;
                    }
                }
            }
            else if (containerId.HasValue)
            {
                targetContainer = tree.Containers.FirstOrDefault(c => c.Id == containerId.Value);
            }

            var isEpisodic = w.WorkKind is MetadataVerticals.Tv or MetadataVerticals.Anime;
            var effectiveSeason = season ?? (isEpisodic ? targetContainer?.ContainerNumber : null);
            var effectiveEpisode = episode ?? (isEpisodic && unitId.HasValue ? targetUnit?.UnitNumber : null);

            var query = new IndexerSearchQuery(
                Query: w.Title,
                MediaVertical: w.WorkKind,
                Season: effectiveSeason,
                Episode: effectiveEpisode,
                Interactive: true,
                WorkId: id,
                UnitId: targetUnit?.Id);

            var scoped = await searchOrchestrator.SearchScopedAsync(
                new OrchestratedReleaseSearchRequest(
                    Query: query,
                    WorkTree: tree,
                    ContainerId: targetContainer?.Id ?? containerId,
                    UnitId: targetUnit?.Id ?? unitId,
                    AbsoluteEpisodeNumber: targetUnit?.AbsoluteNumber,
                    IncludeSeasonPacks: true,
                    Actor: principal.User),
                ct);

            var parsedPairs = scoped.Select(s => (s.Release, s.Parsed)).ToList();

            var enriched = await wantedService.EnrichInteractiveReleasesAsync(
                parsedPairs,
                workId: id,
                containerId: targetContainer?.Id ?? containerId,
                unitId: targetUnit?.Id ?? unitId,
                profileId: null,
                actor: principal.User,
                ct: ct);

            var filtered = InteractiveReleaseFilterHelper.ApplyReleaseFilters(
                enriched,
                q: q,
                protocol: protocol,
                approvedOnly: approvedOnly,
                hideBlocklisted: hideBlocklisted,
                sort: sort,
                order: order,
                limit: limit);

            return Results.Ok(filtered);
        }).RequireScope(PermissionScopes.IndexersSearch).WithTags("Catalog");

        // --- #29 Real-Time Queue Controls & Manual Folder Import ---
        var dl = app.MapGroup("/api/v1/downloaders").WithTags("Unified Downloaders & Queue Cleaner");

        dl.MapPost("/queue/{clientId:long}/{externalId}/control", async (
            long clientId,
            string externalId,
            QueueControlRequest req,
            IUnifiedDownloadOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Action))
            {
                return Results.BadRequest(new { ok = false, message = "Queue control action is required." });
            }

            await orchestrator.ControlQueueItemAsync(clientId, externalId, req.Action, ct);
            return Results.Ok(new { ok = true, message = $"Executed '{req.Action}' on download '{externalId}'." });
        }).RequireScope(PermissionScopes.QueueManage);

        dl.MapDelete("/queue/{clientId:long}/{externalId}", async (
            long clientId,
            string externalId,
            bool? removeFromClient,
            bool? deleteData,
            bool? blocklist,
            bool? searchReplacement,
            string? title,
            string? infoHash,
            IWantedAndActivityService wantedService,
            CancellationToken ct) =>
        {
            var outcome = await wantedService.RemoveQueueItemWithOptionsAsync(
                clientId,
                externalId,
                new RemoveQueueItemOptions(
                    RemoveFromClient: removeFromClient ?? true,
                    DeleteData: deleteData ?? false,
                    Blocklist: blocklist ?? false,
                    SearchReplacement: searchReplacement ?? false,
                    Title: title,
                    InfoHash: infoHash),
                ct);
            return Results.Ok(outcome);
        }).RequireScope(PermissionScopes.QueueManage);

        dl.MapGet("/manual-import", async (
            string? folder,
            string? vertical,
            long? workId,
            IWantedAndActivityService wantedService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return Results.BadRequest(new { message = "Query parameter 'folder' is required." });
            }

            var items = await wantedService.ScanManualImportFolderAsync(folder, vertical, workId, ct);
            return Results.Ok(items);
        }).RequireScope(PermissionScopes.QueueRead);

        dl.MapPost("/manual-import", async (
            ExecuteManualFolderImportRequest req,
            IWantedAndActivityService wantedService,
            CancellationToken ct) =>
        {
            var report = await wantedService.ExecuteManualFolderImportAsync(req, ct);
            return Results.Ok(report);
        }).RequireScope(PermissionScopes.QueueManage);

        // --- #29 Activity History & Blocklist Management ---
        var activity = app.MapGroup("/api/v1/activity").WithTags("Activity (History & Blocklist)");

        activity.MapGet("/history", async (
            int? page,
            int? pageSize,
            string? eventType,
            string? vertical,
            long? workId,
            IWantedAndActivityService wantedService,
            CancellationToken ct) =>
        {
            var res = await wantedService.GetMediaHistoryAsync(
                page: page ?? 1,
                pageSize: pageSize ?? 50,
                eventType: eventType,
                vertical: vertical,
                workId: workId,
                ct: ct);
            return Results.Ok(res);
        }).RequireScope(PermissionScopes.QueueRead);

        activity.MapPost("/history/{id:long}/mark-failed", async (
            long id,
            MarkHistoryFailedRequest? req,
            IWantedAndActivityService wantedService,
            CancellationToken ct) =>
        {
            try
            {
                var outcome = await wantedService.MarkHistoryItemFailedAsync(
                    id,
                    req ?? new MarkHistoryFailedRequest(SearchReplacement: true),
                    ct);
                return Results.Ok(outcome);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        }).RequireScope(PermissionScopes.QueueManage);

        activity.MapGet("/blocklist", async (
            string? q,
            string? protocol,
            long? workId,
            int? limit,
            int? offset,
            IUnifiedDownloadOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            var rows = await orchestrator.ListBlocklistedHashesAsync(ct);
            return Results.Ok(InteractiveReleaseFilterHelper.ApplyBlocklistFilters(rows, q, protocol, workId, limit, offset));
        }).RequireScope(PermissionScopes.QueueRead);

        activity.MapDelete("/blocklist/{**infoHash}", async (
            string infoHash,
            IUnifiedDownloadOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            var decoded = Uri.UnescapeDataString(infoHash);
            var deleted = await orchestrator.RemoveBlocklistedHashAsync(decoded, ct);
            return deleted ? Results.NoContent() : Results.NotFound(new { message = $"Blocklisted entry '{decoded}' not found." });
        }).RequireScope(PermissionScopes.QueueManage);

        activity.MapPost("/blocklist/bulk-delete", async (
            BulkDeleteBlocklistRequest req,
            IUnifiedDownloadOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            var deletedCount = await orchestrator.BulkRemoveBlocklistedHashesAsync(req.InfoHashes, req.ClearAll, ct);
            return Results.Ok(new { deletedCount });
        }).RequireScope(PermissionScopes.QueueManage);

        return app;
    }
}
