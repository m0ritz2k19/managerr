using Managerr.Catalog;
using Managerr.Datastore.Services;
using Managerr.Downloaders;
using Managerr.Downloaders.Organizer;
using Managerr.Downloaders.Parser;
using Managerr.Host.Auth;
using Managerr.Host.Services;
using Managerr.Indexers;
using Managerr.Indexers.Cardigann;
using Managerr.Indexers.Torznab;
using Managerr.Migration;
using Managerr.Security.Rbac;

namespace Managerr.Host.Endpoints;

public sealed record ParseTitlePreviewRequest(string Title, string? MediaVerticalHint = null, string Extension = ".mkv");
public sealed record SweepQueueRequest(int StrikeThreshold = 3);
public sealed record PushReleaseRequest(
    string Title,
    string DownloadUrl,
    string? Protocol = "usenet",
    string? MediaVertical = null,
    string? Guid = null,
    long? IndexerId = null,
    string? Indexer = null,
    long Size = 0,
    int? Seeders = null,
    int? Peers = null,
    string? PublishDate = null,
    string? ImdbId = null,
    int? TmdbId = null,
    int? TvdbId = null);
public sealed record TestIndexerHttpRequest(
    UpsertIndexerRequest? Indexer = null,
    long? ExistingId = null,
    long? Id = null,
    string? Name = null,
    string? Implementation = null,
    string? DefinitionId = null,
    string? Protocol = null,
    string? BaseUrl = null,
    string? ApiKey = null,
    IReadOnlyList<int>? Categories = null,
    int Priority = 25);

public static class AcquisitionEndpoints
{
    public static IEndpointRouteBuilder MapAcquisitionEndpoints(this IEndpointRouteBuilder app)
    {
        var ixGroup = app.MapGroup("/api/v1/indexers").WithTags("Unified Indexers (Cardigann / Torznab / Newznab)");

        ixGroup.MapGet("/definitions", (
            string? search,
            string? trackerType,
            string? vertical,
            ICardigannDefinitionCatalog catalog) =>
        {
            var defs = catalog.ListDefinitions(search, trackerType, vertical);
            return Results.Ok(new
            {
                totalCount = defs.Count,
                items = defs,
            });
        }).RequireScope(PermissionScopes.IndexersRead);

        ixGroup.MapGet("/", async (IUnifiedIndexerService indexerService, CancellationToken ct) =>
        {
            var list = await indexerService.ListIndexersAsync(ct);
            return Results.Ok(list);
        }).RequireScope(PermissionScopes.IndexersRead);

        ixGroup.MapPost("/", async (
            UpsertIndexerRequest req,
            HttpContext httpContext,
            IUnifiedIndexerService indexerService,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            var created = await indexerService.CreateIndexerAsync(req, ct);
            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "indexers.created",
                ResourceType: "indexer",
                ResourceId: created.Id.ToString(),
                Details: new { created.Name, created.Implementation, created.DefinitionId, created.Protocol },
                ActorUserId: principal.User?.Id,
                ActorUsername: principal.User?.Username,
                TraceId: httpContext.TraceIdentifier), ct);
            return Results.Created($"/api/v1/indexers/{created.Id}", created);
        }).RequireScope(PermissionScopes.IndexersWrite);

        ixGroup.MapPut("/{id:long}", async (
            long id,
            UpsertIndexerRequest req,
            HttpContext httpContext,
            IUnifiedIndexerService indexerService,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            var updated = await indexerService.UpdateIndexerAsync(id, req, ct);
            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "indexers.updated",
                ResourceType: "indexer",
                ResourceId: updated.Id.ToString(),
                Details: new { updated.Name, updated.Implementation, updated.BaseUrl, updated.Priority },
                ActorUserId: principal.User?.Id,
                ActorUsername: principal.User?.Username,
                TraceId: httpContext.TraceIdentifier), ct);
            return Results.Ok(updated);
        }).RequireScope(PermissionScopes.IndexersWrite);

        ixGroup.MapPost("/test", async (
            TestIndexerHttpRequest req,
            IUnifiedIndexerService indexerService,
            CancellationToken ct) =>
        {
            var effectiveIndexer = req.Indexer;
            if (effectiveIndexer is null && !string.IsNullOrWhiteSpace(req.BaseUrl))
            {
                var impl = string.IsNullOrWhiteSpace(req.Implementation) ? "newznab" : req.Implementation;
                effectiveIndexer = new UpsertIndexerRequest(
                    Name: string.IsNullOrWhiteSpace(req.Name) ? "Indexer Test" : req.Name,
                    Implementation: impl,
                    DefinitionId: req.DefinitionId,
                    Protocol: string.IsNullOrWhiteSpace(req.Protocol)
                        ? (impl.Equals("newznab", StringComparison.OrdinalIgnoreCase) ? "usenet" : "torrent")
                        : req.Protocol,
                    BaseUrl: req.BaseUrl,
                    ApiKey: req.ApiKey,
                    Categories: req.Categories,
                    Priority: req.Priority <= 0 ? 25 : req.Priority);
            }

            if (effectiveIndexer is null)
            {
                return Results.BadRequest(new { ok = false, message = "Indexer configuration is required." });
            }

            var (ok, message) = await indexerService.TestIndexerAsync(effectiveIndexer, req.ExistingId ?? req.Id, ct);
            return Results.Ok(new { ok, message });
        }).RequireScope(PermissionScopes.IndexersWrite);

        ixGroup.MapDelete("/{id:long}", async (long id, IUnifiedIndexerService indexerService, CancellationToken ct) =>
        {
            await indexerService.DeleteIndexerAsync(id, ct);
            return Results.NoContent();
        }).RequireScope(PermissionScopes.IndexersWrite);

        ixGroup.MapGet("/proxies", async (IUnifiedIndexerService indexerService, CancellationToken ct) =>
        {
            var proxies = await indexerService.ListProxiesAsync(ct);
            return Results.Ok(proxies);
        }).RequireScope(PermissionScopes.IndexersRead);

        ixGroup.MapPost("/proxies", async (
            UpsertIndexerProxyRequest req,
            IUnifiedIndexerService indexerService,
            CancellationToken ct) =>
        {
            var created = await indexerService.CreateProxyAsync(req, ct);
            return Results.Created($"/api/v1/indexers/proxies/{created.Id}", created);
        }).RequireScope(PermissionScopes.IndexersWrite);

        ixGroup.MapDelete("/proxies/{id:long}", async (long id, IUnifiedIndexerService indexerService, CancellationToken ct) =>
        {
            await indexerService.DeleteProxyAsync(id, ct);
            return Results.NoContent();
        }).RequireScope(PermissionScopes.IndexersWrite);

        ixGroup.MapPost("/search", async (
            HttpContext httpContext,
            IndexerSearchQuery query,
            string? q,
            string? protocol,
            bool? approvedOnly,
            bool? hideBlocklisted,
            string? sort,
            string? order,
            int? limit,
            IReleaseSearchOrchestrator searchOrchestrator,
            IWantedAndActivityService wantedService,
            IPolymorphicCatalogService catalogService,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            MediaWorkDetailTree? tree = null;
            if (query.WorkId.HasValue)
            {
                tree = await catalogService.GetWorkTreeAsync(query.WorkId.Value, principal.User, ct);
                if (tree is null)
                {
                    return Results.NotFound(new { error = "Work not found." });
                }
            }

            long? resolvedContainerId = query.Season.HasValue && tree is not null
                ? tree.Containers.FirstOrDefault(c => c.ContainerNumber == query.Season.Value)?.Id
                : null;

            var scoped = await searchOrchestrator.SearchScopedAsync(
                new OrchestratedReleaseSearchRequest(
                    Query: query,
                    WorkTree: tree,
                    ContainerId: resolvedContainerId,
                    UnitId: query.UnitId,
                    IncludeSeasonPacks: true,
                    Actor: principal.User),
                ct);

            var scopedPairs = scoped.Select(x => (x.Release, x.Parsed)).ToList();

            var enriched = await wantedService.EnrichInteractiveReleasesAsync(
                scopedPairs,
                workId: query.WorkId,
                containerId: resolvedContainerId,
                unitId: query.UnitId,
                profileId: query.ProfileId,
                actor: principal.User,
                ct: ct);

            var scopedByCandidateKey = new Dictionary<string, ScopedReleaseCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in scoped)
            {
                if (!string.IsNullOrWhiteSpace(s.Release.Guid))
                {
                    scopedByCandidateKey.TryAdd(s.Release.Guid, s);
                }
                if (!string.IsNullOrWhiteSpace(s.Release.DownloadUrl))
                {
                    scopedByCandidateKey.TryAdd(s.Release.DownloadUrl, s);
                }
            }

            var ordered = enriched
                .OrderByDescending(x => x.Approved)
                .ThenByDescending(x =>
                {
                    if (!query.Season.HasValue) return 0;
                    var candKey = !string.IsNullOrWhiteSpace(x.Release.Guid) ? x.Release.Guid : x.Release.DownloadUrl;
                    scopedByCandidateKey.TryGetValue(candKey, out var sc);

                    var isPack = sc?.IsSeasonPack ?? x.Parsed.IsFullSeasonPack;
                    var effSeason = sc?.EffectiveSeason ?? x.Parsed.SeasonNumber;
                    var effEpisodes = sc?.EffectiveEpisodes ?? x.Parsed.EpisodeNumbers;

                    if (query.Episode.HasValue)
                    {
                        if (!isPack && effEpisodes.Contains(query.Episode.Value)) return 3;
                        if (isPack && effSeason == query.Season.Value) return 2;
                        return 1;
                    }

                    if (isPack && effSeason == query.Season.Value) return 3;
                    if (effSeason == query.Season.Value) return 2;
                    return 1;
                })
                .ThenByDescending(x => x.TotalWeightedScore)
                .ThenByDescending(x => x.Release.Seeders ?? 0)
                .ThenByDescending(x => x.Release.SizeBytes)
                .ToList();

            var filtered = InteractiveReleaseFilterHelper.ApplyReleaseFilters(
                ordered,
                q: q ?? query.FilterQuery,
                protocol: protocol ?? query.Protocol,
                approvedOnly: approvedOnly ?? query.ApprovedOnly,
                hideBlocklisted: hideBlocklisted ?? query.HideBlocklisted,
                sort: sort ?? query.Sort,
                order: order ?? query.Order,
                limit: limit ?? query.Limit);

            return Results.Ok(filtered);
        }).RequireScope(PermissionScopes.IndexersSearch);

        ixGroup.MapPost("/rss-sync", async (IAutomatedRssSyncService rssSyncService, CancellationToken ct) =>
        {
            var report = await rssSyncService.ExecuteRssSyncAndGrabAsync(ct);
            return Results.Ok(report);
        }).RequireScope(PermissionScopes.IndexersSearch);

        ixGroup.MapPost("/release/push", async (
            PushReleaseRequest req,
            IReleaseParserRouter parserRouter,
            IAutomatedRssSyncService rssSyncService,
            CancellationToken ct) =>
        {
            var parsed = parserRouter.Parse(req.Title, req.MediaVertical);
            var pubDate = DateTimeOffset.TryParse(req.PublishDate, out var parsedDt) ? parsedDt : DateTimeOffset.UtcNow;
            var candidate = new IndexedReleaseCandidate(
                Guid: !string.IsNullOrWhiteSpace(req.Guid) ? req.Guid : req.DownloadUrl,
                IndexerId: req.IndexerId ?? 1,
                IndexerName: !string.IsNullOrWhiteSpace(req.Indexer) ? req.Indexer : "Push",
                Protocol: !string.IsNullOrWhiteSpace(req.Protocol) ? req.Protocol.ToLowerInvariant() : "usenet",
                MediaVertical: !string.IsNullOrWhiteSpace(req.MediaVertical) ? req.MediaVertical.ToLowerInvariant() : parsed.MediaVertical,
                Title: req.Title,
                DownloadUrl: req.DownloadUrl,
                MagnetUrl: req.DownloadUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? req.DownloadUrl : null,
                InfoUrl: null,
                InfoHash: null,
                SizeBytes: req.Size,
                Seeders: req.Seeders,
                Peers: req.Peers,
                DownloadVolumeFactor: 1.0,
                UploadVolumeFactor: 1.0,
                PublishedAt: pubDate,
                Categories: [],
                ImdbId: req.ImdbId,
                TmdbId: req.TmdbId,
                TvdbId: req.TvdbId);

            var report = await rssSyncService.EvaluatePushedReleasesAsync([candidate], ct);
            return Results.Ok(report);
        }).RequireScope(PermissionScopes.IndexersSearch);

        var dlGroup = app.MapGroup("/api/v1/downloaders").WithTags("Unified Downloaders & Queue Cleaner");

        dlGroup.MapGet("/drivers", (IUnifiedDownloadOrchestrator orchestrator) =>
        {
            return Results.Ok(new
            {
                drivers = orchestrator.ListSupportedDrivers(),
                defaultCategoryMappings = Managerr.Downloaders.Clients.DownloadClientCatalog.DefaultCategoryMappings(),
            });
        }).RequireScope(PermissionScopes.DownloadersRead);

        dlGroup.MapGet("/", async (IUnifiedDownloadOrchestrator orchestrator, CancellationToken ct) =>
        {
            return Results.Ok(await orchestrator.ListClientsAsync(ct));
        }).RequireScope(PermissionScopes.DownloadersRead);

        dlGroup.MapPost("/", async (
            UpsertDownloadClientRequest req,
            HttpContext httpContext,
            IUnifiedDownloadOrchestrator orchestrator,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            var created = await orchestrator.CreateClientAsync(req, ct);
            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "downloaders.created",
                ResourceType: "download_client",
                ResourceId: created.Id.ToString(),
                Details: new { created.Name, created.Implementation, created.Protocol, created.Host, created.Port },
                ActorUserId: principal.User?.Id,
                ActorUsername: principal.User?.Username,
                TraceId: httpContext.TraceIdentifier), ct);
            return Results.Created($"/api/v1/downloaders/{created.Id}", created);
        }).RequireScope(PermissionScopes.DownloadersWrite);

        dlGroup.MapPut("/{id:long}", async (
            long id,
            UpsertDownloadClientRequest req,
            HttpContext httpContext,
            IUnifiedDownloadOrchestrator orchestrator,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            var updated = await orchestrator.UpdateClientAsync(id, req, ct);
            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "downloaders.updated",
                ResourceType: "download_client",
                ResourceId: updated.Id.ToString(),
                Details: new { updated.Name, updated.Implementation, updated.Protocol, updated.Host, updated.Port },
                ActorUserId: principal.User?.Id,
                ActorUsername: principal.User?.Username,
                TraceId: httpContext.TraceIdentifier), ct);
            return Results.Ok(updated);
        }).RequireScope(PermissionScopes.DownloadersWrite);

        dlGroup.MapDelete("/{id:long}", async (long id, IUnifiedDownloadOrchestrator orchestrator, CancellationToken ct) =>
        {
            await orchestrator.DeleteClientAsync(id, ct);
            return Results.NoContent();
        }).RequireScope(PermissionScopes.DownloadersWrite);

        dlGroup.MapGet("/remote-paths", async (IUnifiedDownloadOrchestrator orchestrator, CancellationToken ct) =>
        {
            return Results.Ok(await orchestrator.ListRemotePathMappingsAsync(ct));
        }).RequireScope(PermissionScopes.DownloadersRead);

        dlGroup.MapPost("/remote-paths", async (
            CreateRemotePathMappingRequest req,
            IUnifiedDownloadOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            var created = await orchestrator.CreateRemotePathMappingAsync(req, ct);
            return Results.Created($"/api/v1/downloaders/remote-paths/{created.Id}", created);
        }).RequireScope(PermissionScopes.DownloadersWrite);

        dlGroup.MapDelete("/remote-paths/{id:long}", async (long id, IUnifiedDownloadOrchestrator orchestrator, CancellationToken ct) =>
        {
            await orchestrator.DeleteRemotePathMappingAsync(id, ct);
            return Results.NoContent();
        }).RequireScope(PermissionScopes.DownloadersWrite);

        dlGroup.MapPost("/grab", async (
            GrabReleaseRequest req,
            IUnifiedDownloadOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            var outcome = await orchestrator.GrabReleaseAsync(req, ct);
            return Results.Ok(outcome);
        }).RequireScope(PermissionScopes.IndexersSearch);

        dlGroup.MapGet("/queue", async (IUnifiedDownloadOrchestrator orchestrator, CancellationToken ct) =>
        {
            return Results.Ok(await orchestrator.GetUnifiedQueueAsync(ct));
        }).RequireScope(PermissionScopes.QueueRead);

        dlGroup.MapPost("/queue/sweep", async (
            SweepQueueRequest? req,
            IUnifiedDownloadOrchestrator orchestrator,
            IWantedAndActivityService wantedService,
            CancellationToken ct) =>
        {
            var rawActions = await orchestrator.EvaluateQueueHealthAndStrikesAsync(req?.StrikeThreshold ?? 3, ct);
            var enrichedActions = new List<QueueSweepAction>(rawActions.Count);
            foreach (var action in rawActions)
            {
                if (action.AddedToRecurringBlocklist)
                {
                    var repNote = await wantedService.RedownloadFailedWorkOrUnitsAsync(
                        action.WorkId,
                        action.UnitIds,
                        action.Title,
                        action.MediaVertical ?? "tv",
                        ct);
                    enrichedActions.Add(action with { ReplacementMessage = repNote });
                }
                else
                {
                    enrichedActions.Add(action);
                }
            }

            return Results.Ok(enrichedActions);
        }).RequireScope(PermissionScopes.QueueManage);

        dlGroup.MapGet("/blocklist", async (
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

        dlGroup.MapPost("/process-completed", async (
            ICompletedDownloadImportService importService,
            CancellationToken ct) =>
        {
            var report = await importService.ProcessCompletedDownloadsAsync(ct);
            return Results.Ok(report);
        }).RequireScope(PermissionScopes.QueueManage);

        dlGroup.MapMethods("/notify", ["GET", "POST"], async (
            ICompletedDownloadImportService importService,
            CancellationToken ct) =>
        {
            var report = await importService.ProcessCompletedDownloadsAsync(ct);
            return Results.Ok(report);
        }).RequireScope(PermissionScopes.QueueManage);

        dlGroup.MapGet("/imports", async (
            int? limit,
            ICompletedDownloadImportService importService,
            CancellationToken ct) =>
        {
            var list = await importService.ListRecentImportsAsync(limit ?? 50, ct);
            return Results.Ok(list);
        }).RequireScope(PermissionScopes.QueueRead);

        dlGroup.MapGet("/tracked", async (
            int? limit,
            ICompletedDownloadImportService importService,
            CancellationToken ct) =>
        {
            var list = await importService.ListTrackedDownloadsAsync(limit ?? 100, ct);
            return Results.Ok(list);
        }).RequireScope(PermissionScopes.QueueRead);

        dlGroup.MapPost("/tracked/{id:long}/retry-import", async (
            long id,
            ManualOverrideImportRequest? req,
            ICompletedDownloadImportService importService,
            CancellationToken ct) =>
        {
            var outcome = await importService.RetryTrackedDownloadImportAsync(id, req, ct);
            return Results.Ok(outcome);
        }).RequireScope(PermissionScopes.QueueManage);

        dlGroup.MapPost("/tracked/{id:long}/ignore", async (
            long id,
            ICompletedDownloadImportService importService,
            CancellationToken ct) =>
        {
            var updated = await importService.IgnoreTrackedDownloadAsync(id, ct);
            return updated is null
                ? Results.NotFound(new { message = $"Tracked download #{id} not found." })
                : Results.Ok(updated);
        }).RequireScope(PermissionScopes.QueueManage);

        var parserGroup = app.MapGroup("/api/v1/parser").WithTags("Release Title Parser & Organizer");

        parserGroup.MapPost("/parse", (
            ParseTitlePreviewRequest req,
            IReleaseParserRouter parserRouter,
            IAtomicFileOrganizer organizer) =>
        {
            var parsed = parserRouter.Parse(req.Title, req.MediaVerticalHint);
            var previewRelativePath = organizer.RenderRelativeDestinationPath(parsed, req.Extension);
            return Results.Ok(new
            {
                parsed,
                previewRelativePath,
            });
        }).RequireScope(PermissionScopes.CatalogRead);

        return app;
    }
}
