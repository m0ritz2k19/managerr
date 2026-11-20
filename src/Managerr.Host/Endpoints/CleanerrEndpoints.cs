using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Managerr.Cleanerr;
using Managerr.Datastore.Services;
using Managerr.Host.Auth;
using Managerr.Host.Hubs;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;

namespace Managerr.Host.Endpoints;

public sealed record ExecuteCleanerrWorkflowHttpRequest(
    bool? DryRun = null,
    double? SimulatedFreeSpaceGb = null,
    double? SimulatedFreeSpacePercent = null);

public sealed record ProcessCleanerrQueueHttpRequest(
    DateTimeOffset? ReferenceNow = null,
    int? SimulateDaysAhead = null);

public sealed record QueueItemActionHttpRequest(string? Reason = null, string? ExpiresAt = null);

public sealed record PostponeAllCleanerrQueueRequest(
    int? Days = null,
    string? Reason = null,
    long? WorkflowId = null);

public static class CleanerrEndpoints
{
    public static IEndpointRouteBuilder MapCleanerrEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/v1/cleanerr").WithTags("Cleanerr Lifecycle Studio");

        // --- Status, Stats, Catalogs & Media ---
        grp.MapGet("/status", async (
            HttpContext ctx,
            ICleanerrLifecycleEngine engine,
            ICleanerrDiscordService discord,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var staged = await engine.ListDeletionQueueAsync("staged", ct);
            return Results.Ok(new
            {
                module = "cleanerr",
                defaultAccessPolicy = "rbac",
                canRead = HasScope(principal, PermissionScopes.CleanerrRead),
                canRescue = HasScope(principal, PermissionScopes.CleanerrRescue),
                canExclude = HasScope(principal, PermissionScopes.CleanerrExclude),
                canWriteWorkflows = HasScope(principal, PermissionScopes.CleanerrWrite),
                canExecute = HasScope(principal, PermissionScopes.CleanerrExecute),
                cleanerrExclusionLimit = principal.User?.EffectivePermissions.CleanerrExclusionLimit,
                gatewayConnected = discord.IsGatewayConnected,
                gatewayBotUsername = discord.GatewayBotUsername,
                stagedCount = staged.Count,
            });
        }).RequireScope(PermissionScopes.CleanerrRead);

        grp.MapGet("/stats", async (ICleanerrLifecycleEngine engine, CancellationToken ct) =>
            Results.Ok(await engine.GetOverviewStatsAsync(ct)))
            .RequireScope(PermissionScopes.CleanerrRead);

        grp.MapGet("/templates", (ICleanerrLifecycleEngine engine) =>
            Results.Ok(engine.ListTemplates()))
            .RequireScope(PermissionScopes.CleanerrRead);

        grp.MapGet("/rules/fields", (ICleanerrLifecycleEngine engine) =>
            Results.Ok(engine.GetRuleCatalog()))
            .RequireScope(PermissionScopes.CleanerrRead);

        grp.MapGet("/node-catalog", (ICleanerrLifecycleEngine engine) =>
            Results.Ok(engine.GetNodeCatalog()))
            .RequireScope(PermissionScopes.CleanerrRead);

        grp.MapPost("/rules/test-media", async (
            TestCleanerrMediaRequest req,
            ICleanerrLifecycleEngine engine,
            CancellationToken ct) =>
            Results.Ok(await engine.TestMediaAsync(req, ct)))
            .RequireScope(PermissionScopes.CleanerrRead);

        grp.MapGet("/media", async (
            string? search,
            string? q,
            string? mediaType,
            long? libraryId,
            int? page,
            int? pageSize,
            string? watched,
            string? sort,
            ICleanerrLifecycleEngine engine,
            CancellationToken ct) =>
            Results.Ok(await engine.QueryMediaItemsAsync(
                search ?? q,
                mediaType,
                libraryId,
                page ?? 1,
                pageSize ?? 50,
                watched,
                sort,
                ct)))
            .RequireScope(PermissionScopes.CleanerrRead);

        grp.MapPost("/media", async (
            UpsertCleanerrMediaItemRequest req,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            try
            {
                var item = await engine.UpsertMediaItemAsync(req, ct);
                await BroadcastCleanerrUpdatedAsync(realtime, "media.upserted", ct);
                return Results.Ok(item);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { errorCode = "ERR_CLEANERR_INVALID_MEDIA_TYPE", message = ex.Message });
            }
        }).RequireScope(PermissionScopes.CleanerrWrite);

        grp.MapPost("/media/sync-catalog", async (
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            await engine.SyncCatalogItemsIntoCleanerrAsync(ct);
            var items = await engine.ListMediaItemsAsync(ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "media.synced_catalog", ct);
            return Results.Ok(new { synced = true, count = items.Count });
        }).RequireScope(PermissionScopes.CleanerrWrite);

        grp.MapPost("/sync-plex", async (
            HttpContext ctx,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            var req = await TryReadOptionalJsonAsync<SyncCleanerrPlexRequest>(ctx, ct);
            var result = await engine.SyncFromPlexServerAsync(req, ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "media.synced_plex", ct);
            return Results.Ok(result);
        }).RequireScope(PermissionScopes.CleanerrWrite);

        grp.MapGet("/media/{id:long}/children", async (
            long id,
            ICleanerrLifecycleEngine engine,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await engine.GetMediaChildrenAsync(id, ct));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        }).RequireScope(PermissionScopes.CleanerrRead);

        // --- Workflows, Maintainerr Rule Groups & Topological DAG Simulator ---
        grp.MapGet("/workflows", async (ICleanerrLifecycleEngine engine, CancellationToken ct) =>
            Results.Ok(await engine.ListWorkflowsAsync(ct)))
            .RequireScope(PermissionScopes.CleanerrRead);

        grp.MapGet("/workflows/{id:long}", async (long id, ICleanerrLifecycleEngine engine, CancellationToken ct) =>
        {
            var wf = await engine.GetWorkflowByIdAsync(id, ct);
            return wf is null ? Results.NotFound() : Results.Ok(wf);
        }).RequireScope(PermissionScopes.CleanerrRead);

        grp.MapGet("/workflows/{id:long}/yaml", async (long id, ICleanerrLifecycleEngine engine, CancellationToken ct) =>
            Results.Ok(await engine.ExportWorkflowYamlAsync(id, ct)))
            .RequireScope(PermissionScopes.CleanerrRead);

        grp.MapPost("/workflows/import-yaml", async (
            HttpContext ctx,
            ImportCleanerrRuleYamlRequest req,
            ICleanerrLifecycleEngine engine,
            IAuditLogService audit,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var (parsedName, _, _, _, parsedSpec, parsedGraphJson) = CleanerrMaintainerrRuleEngine.ImportFromYaml(req.Yaml);
            CleanerrLiveImpactPreview? liveImpact = null;
            if (!(req.DryRun ?? true))
            {
                liveImpact = await engine.PreviewWorkflowLiveImpactAsync(
                    null,
                    new PreviewCleanerrLiveImpactRequest(
                        GraphJson: parsedGraphJson,
                        RuleGroup: parsedSpec,
                        WorkflowName: parsedName,
                        ActorUsername: principal.User?.Username),
                    ct);
                var confirmedName = ResolveConfirmedWorkflowName(ctx, req.ConfirmWorkflowName);
                if (!string.Equals(confirmedName?.Trim(), parsedName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new
                    {
                        errorCode = "ERR_CLEANERR_LIVE_CONFIRMATION_REQUIRED",
                        message = $"Importing workflow '{parsedName.Trim()}' in live mode requires confirming the workflow name.",
                        impact = liveImpact,
                    });
                }
            }

            var created = await engine.ImportWorkflowYamlAsync(req, ct);
            await audit.RecordAsync(new AuditWriteRequest(
                Action: "cleanerr.workflow.imported_yaml",
                ResourceType: "cleanerr_workflow",
                ResourceId: created.Id.ToString(),
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                Details: new { created.Name, created.EditorMode, created.DryRun, created.Enabled },
                TraceId: ctx.TraceIdentifier), ct);

            if (!created.DryRun && liveImpact is not null)
            {
                await audit.RecordAsync(new AuditWriteRequest(
                    Action: "cleanerr.workflow.live_enabled",
                    ResourceType: "cleanerr_workflow",
                    ResourceId: created.Id.ToString(),
                    ActorUserId: principal.User?.Id,
                    ActorPlexUserId: principal.User?.PlexUserId,
                    ActorUsername: principal.User?.Username,
                    ActorApiKeyPrefix: principal.ApiKeyPrefix,
                    IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                    Details: new
                    {
                        WorkflowId = created.Id,
                        WorkflowName = created.Name,
                        Impact = liveImpact,
                    },
                    TraceId: ctx.TraceIdentifier), ct);
            }

            await BroadcastCleanerrUpdatedAsync(realtime, "workflow.imported_yaml", ct);
            return Results.Created($"/api/v1/cleanerr/workflows/{created.Id}", created);
        }).RequireScope(PermissionScopes.CleanerrWrite);

        grp.MapPost("/workflows/{id:long}/duplicate", async (
            long id,
            HttpContext ctx,
            ICleanerrLifecycleEngine engine,
            IAuditLogService audit,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var req = await TryReadOptionalJsonAsync<DuplicateCleanerrWorkflowRequest>(ctx, ct);
            var duplicated = await engine.DuplicateWorkflowAsync(id, req?.Name, ct);
            await audit.RecordAsync(new AuditWriteRequest(
                Action: "cleanerr.workflow.duplicated",
                ResourceType: "cleanerr_workflow",
                ResourceId: duplicated.Id.ToString(),
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                Details: new { SourceWorkflowId = id, duplicated.Name, duplicated.EditorMode, duplicated.DryRun },
                TraceId: ctx.TraceIdentifier), ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "workflow.duplicated", ct);
            return Results.Created($"/api/v1/cleanerr/workflows/{duplicated.Id}", duplicated);
        }).RequireScope(PermissionScopes.CleanerrWrite);

        grp.MapPost("/workflows/{id:long}/test-media", async (
            long id,
            TestCleanerrMediaRequest req,
            ICleanerrLifecycleEngine engine,
            CancellationToken ct) =>
            Results.Ok(await engine.TestMediaAsync(req with { WorkflowId = id }, ct)))
            .RequireScope(PermissionScopes.CleanerrRead);

        grp.MapPost("/workflows/preview-live-impact", (
            HttpContext ctx,
            ICleanerrLifecycleEngine engine,
            CancellationToken ct) =>
            HandlePreviewImpactAsync(null, ctx, engine, ct))
            .RequireScope(PermissionScopes.CleanerrRead);

        grp.MapPost("/workflows/preview/preview-impact", (
            HttpContext ctx,
            ICleanerrLifecycleEngine engine,
            CancellationToken ct) =>
            HandlePreviewImpactAsync(null, ctx, engine, ct))
            .RequireScope(PermissionScopes.CleanerrRead);

        grp.MapPost("/workflows/{id:long}/preview-live-impact", (
            long id,
            HttpContext ctx,
            ICleanerrLifecycleEngine engine,
            CancellationToken ct) =>
            HandlePreviewImpactAsync(id, ctx, engine, ct))
            .RequireScope(PermissionScopes.CleanerrRead);

        grp.MapPost("/workflows/{id:long}/preview-impact", (
            long id,
            HttpContext ctx,
            ICleanerrLifecycleEngine engine,
            CancellationToken ct) =>
            HandlePreviewImpactAsync(id, ctx, engine, ct))
            .RequireScope(PermissionScopes.CleanerrRead);

        grp.MapGet("/workflows/{id:long}/live-impact", async (
            long id,
            HttpContext ctx,
            ICleanerrLifecycleEngine engine,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            return Results.Ok(await engine.PreviewWorkflowLiveImpactAsync(
                id,
                new PreviewCleanerrLiveImpactRequest(ActorUsername: principal.User?.Username),
                ct));
        }).RequireScope(PermissionScopes.CleanerrRead);

        grp.MapPost("/workflows", async (
            HttpContext ctx,
            UpsertCleanerrWorkflowRequest req,
            ICleanerrLifecycleEngine engine,
            IAuditLogService audit,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            CleanerrLiveImpactPreview? liveImpact = null;
            if (!req.DryRun)
            {
                liveImpact = await engine.PreviewWorkflowLiveImpactAsync(
                    null,
                    new PreviewCleanerrLiveImpactRequest(
                        GraphJson: req.GraphJson,
                        RuleGroup: req.RuleGroup,
                        WorkflowName: req.Name,
                        ActorUsername: principal.User?.Username),
                    ct);
                var confirmedName = ResolveConfirmedWorkflowName(ctx, req.ConfirmWorkflowName);
                if (!string.Equals(confirmedName?.Trim(), req.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new
                    {
                        errorCode = "ERR_CLEANERR_LIVE_CONFIRMATION_REQUIRED",
                        message = $"Creating workflow '{req.Name.Trim()}' in live mode requires confirming the workflow name.",
                        impact = liveImpact,
                    });
                }
            }

            var created = await engine.UpsertWorkflowAsync(req, null, ct);
            await audit.RecordAsync(new AuditWriteRequest(
                Action: "cleanerr.workflow.created",
                ResourceType: "cleanerr_workflow",
                ResourceId: created.Id.ToString(),
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                Details: new { created.Name, created.EditorMode, created.DryRun, created.Enabled },
                TraceId: ctx.TraceIdentifier), ct);

            if (!created.DryRun && liveImpact is not null)
            {
                await audit.RecordAsync(new AuditWriteRequest(
                    Action: "cleanerr.workflow.live_enabled",
                    ResourceType: "cleanerr_workflow",
                    ResourceId: created.Id.ToString(),
                    ActorUserId: principal.User?.Id,
                    ActorPlexUserId: principal.User?.PlexUserId,
                    ActorUsername: principal.User?.Username,
                    ActorApiKeyPrefix: principal.ApiKeyPrefix,
                    IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                    Details: new
                    {
                        WorkflowId = created.Id,
                        WorkflowName = created.Name,
                        Impact = liveImpact,
                    },
                    TraceId: ctx.TraceIdentifier), ct);
            }

            await BroadcastCleanerrUpdatedAsync(realtime, "workflow.created", ct);
            return Results.Created($"/api/v1/cleanerr/workflows/{created.Id}", created);
        }).RequireScope(PermissionScopes.CleanerrWrite);

        grp.MapPut("/workflows/{id:long}", async (
            long id,
            HttpContext ctx,
            UpsertCleanerrWorkflowRequest req,
            ICleanerrLifecycleEngine engine,
            IAuditLogService audit,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var existing = await engine.GetWorkflowByIdAsync(id, ct);
            if (existing is null)
            {
                return Results.NotFound();
            }

            var requiresLiveConfirmation = !req.DryRun && (existing.DryRun || (req.Enabled && !existing.Enabled));
            CleanerrLiveImpactPreview? liveImpact = null;
            if (requiresLiveConfirmation)
            {
                liveImpact = await engine.PreviewWorkflowLiveImpactAsync(
                    id,
                    new PreviewCleanerrLiveImpactRequest(
                        GraphJson: req.GraphJson,
                        RuleGroup: req.RuleGroup,
                        WorkflowName: req.Name,
                        ActorUsername: principal.User?.Username),
                    ct);
                var confirmedName = ResolveConfirmedWorkflowName(ctx, req.ConfirmWorkflowName);
                if (!string.Equals(confirmedName?.Trim(), req.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new
                    {
                        errorCode = "ERR_CLEANERR_LIVE_CONFIRMATION_REQUIRED",
                        message = $"Enabling or switching workflow '{req.Name.Trim()}' into live mode requires confirming the workflow name.",
                        impact = liveImpact,
                    });
                }
            }

            var updated = await engine.UpsertWorkflowAsync(req, id, ct);
            await audit.RecordAsync(new AuditWriteRequest(
                Action: "cleanerr.workflow.updated",
                ResourceType: "cleanerr_workflow",
                ResourceId: updated.Id.ToString(),
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                Details: new { updated.Name, updated.EditorMode, updated.DryRun, updated.Enabled },
                TraceId: ctx.TraceIdentifier), ct);

            if (requiresLiveConfirmation && liveImpact is not null)
            {
                await audit.RecordAsync(new AuditWriteRequest(
                    Action: "cleanerr.workflow.live_enabled",
                    ResourceType: "cleanerr_workflow",
                    ResourceId: updated.Id.ToString(),
                    ActorUserId: principal.User?.Id,
                    ActorPlexUserId: principal.User?.PlexUserId,
                    ActorUsername: principal.User?.Username,
                    ActorApiKeyPrefix: principal.ApiKeyPrefix,
                    IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                    Details: new
                    {
                        WorkflowId = updated.Id,
                        WorkflowName = updated.Name,
                        Impact = liveImpact,
                    },
                    TraceId: ctx.TraceIdentifier), ct);
            }

            await BroadcastCleanerrUpdatedAsync(realtime, "workflow.updated", ct);
            return Results.Ok(updated);
        }).RequireScope(PermissionScopes.CleanerrWrite);

        grp.MapDelete("/workflows/{id:long}", async (
            long id,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            await engine.DeleteWorkflowAsync(id, ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "workflow.deleted", ct);
            return Results.NoContent();
        }).RequireScope(PermissionScopes.CleanerrWrite);

        grp.MapPost("/workflows/simulate", async (
            SimulateCleanerrWorkflowRequest req,
            ICleanerrLifecycleEngine engine,
            CancellationToken ct) =>
            Results.Ok(await engine.SimulateWorkflowAsync(req, ct)))
            .RequireScope(PermissionScopes.CleanerrRead);

        grp.MapPost("/workflows/{id:long}/simulate", async (
            long id,
            HttpContext ctx,
            ICleanerrLifecycleEngine engine,
            CancellationToken ct) =>
        {
            var req = await TryReadOptionalJsonAsync<SimulateCleanerrWorkflowRequest>(ctx, ct);
            var effective = (req ?? new SimulateCleanerrWorkflowRequest()) with { WorkflowId = id };
            return Results.Ok(await engine.SimulateWorkflowAsync(effective, ct));
        }).RequireScope(PermissionScopes.CleanerrRead);

        grp.MapPost("/workflows/{id:long}/execute", (
            long id,
            HttpContext ctx,
            ICleanerrLifecycleEngine engine,
            IAuditLogService audit,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
            HandleExecuteWorkflowAsync(id, ctx, engine, audit, realtime, ct))
            .RequireScope(PermissionScopes.CleanerrExecute);

        grp.MapPost("/workflows/{id:long}/run", (
            long id,
            HttpContext ctx,
            ICleanerrLifecycleEngine engine,
            IAuditLogService audit,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
            HandleExecuteWorkflowAsync(id, ctx, engine, audit, realtime, ct))
            .RequireScope(PermissionScopes.CleanerrExecute);

        grp.MapGet("/runs", async (long? workflowId, int? limit, ICleanerrLifecycleEngine engine, CancellationToken ct) =>
            Results.Ok(await engine.ListRunsAsync(workflowId, limit ?? 25, ct)))
            .RequireScope(PermissionScopes.CleanerrRead);

        // --- Deletion Queue, Manual Staging, Postpone & Grace Worker ---
        grp.MapGet("/queue", async (string? status, ICleanerrLifecycleEngine engine, CancellationToken ct) =>
            Results.Ok(await engine.ListDeletionQueueAsync(status, ct)))
            .RequireScope(PermissionScopes.CleanerrRead);

        grp.MapPost("/queue/manual-stage", async (
            HttpContext ctx,
            ManualStageCleanerrItemsRequest req,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var actor = principal.User?.Username ?? "admin";
            var staged = await engine.ManualStageItemsAsync(req, actor, ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "queue.manual_staged", ct);
            return Results.Ok(staged);
        }).RequireScope(PermissionScopes.CleanerrWrite);

        grp.MapPost("/queue/process", async (
            HttpContext ctx,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            var req = await TryReadOptionalJsonAsync<ProcessCleanerrQueueHttpRequest>(ctx, ct);
            var refNow = req?.ReferenceNow ?? (req?.SimulateDaysAhead is int days ? DateTimeOffset.UtcNow.AddDays(days) : null);
            var result = await engine.ProcessDeletionQueueAsync(refNow, ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "queue.processed", ct);
            return Results.Ok(result);
        }).RequireScope(PermissionScopes.CleanerrExecute);

        grp.MapPost("/queue/exclude-all", async (
            HttpContext ctx,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var req = await TryReadOptionalJsonAsync<BulkQueueExcludeRequest>(ctx, ct) ?? new BulkQueueExcludeRequest();
            var quota = principal.User?.EffectivePermissions.CleanerrExclusionLimit;
            var count = await engine.BulkExcludeQueueAsync(
                req,
                principal.User?.Username ?? "api",
                principal.User?.Id,
                quota,
                ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "queue.excluded_all", ct);
            return Results.Ok(new { excludedCount = count, count });
        }).RequireScope(PermissionScopes.CleanerrExclude);

        grp.MapPost("/queue/postpone-all", async (
            HttpContext ctx,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var req = await TryReadOptionalJsonAsync<PostponeAllCleanerrQueueRequest>(ctx, ct);
            var defaultDays = req?.Days ?? (await engine.GetGeneralSettingsAsync(ct)).DefaultPostponeDays;
            var days = Math.Clamp(defaultDays, 1, 365);
            var reason = string.IsNullOrWhiteSpace(req?.Reason)
                ? $"Postponed all staged +{days}d by {principal.User?.Username ?? "api"}"
                : req.Reason.Trim();
            var count = await engine.PostponeAllStagedAsync(req?.WorkflowId, days, reason, ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "queue.postponed_all", ct);
            return Results.Ok(new { postponedCount = count, count });
        }).RequireScope(PermissionScopes.CleanerrRescue);

        grp.MapPost("/queue/{id:long}/postpone", async (
            long id,
            HttpContext ctx,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var req = await TryReadOptionalJsonAsync<PostponeCleanerrQueueRequest>(ctx, ct);
            var defaultDays = req?.Days ?? (await engine.GetGeneralSettingsAsync(ct)).DefaultPostponeDays;
            var days = Math.Clamp(defaultDays, 1, 365);
            var reason = req?.Reason ?? $"Postponed +{days}d by {principal.User?.Username ?? "api"}";
            var updated = await engine.PostponeQueueItemAsync(id, days, reason, ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "queue.postponed", ct);
            return Results.Ok(updated);
        }).RequireScope(PermissionScopes.CleanerrRescue);

        grp.MapPost("/queue/{id:long}/rescue", async (
            long id,
            HttpContext ctx,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var req = await TryReadOptionalJsonAsync<QueueItemActionHttpRequest>(ctx, ct);
            var reason = req?.Reason ?? $"Rescued by {principal.User?.Username ?? "api"}";
            var updated = await engine.RescueQueueItemAsync(id, reason, ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "queue.rescued", ct);
            return Results.Ok(updated);
        }).RequireScope(PermissionScopes.CleanerrRescue);

        grp.MapPost("/queue/{id:long}/exclude", async (
            long id,
            HttpContext ctx,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var req = await TryReadOptionalJsonAsync<QueueItemActionHttpRequest>(ctx, ct);
            var quota = principal.User?.EffectivePermissions.CleanerrExclusionLimit;
            var (qItem, rule) = await engine.ExcludeQueueItemAsync(
                id,
                principal.User?.Username ?? "api",
                principal.User?.Id,
                quota,
                req?.Reason ?? "Excluded from Deletion Queue",
                req?.ExpiresAt,
                ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "queue.excluded", ct);
            return Results.Ok(new { queueItem = qItem, rule });
        }).RequireScope(PermissionScopes.CleanerrExclude);

        grp.MapPost("/queue/{id:long}/delete-now", async (
            long id,
            HttpContext ctx,
            ICleanerrLifecycleEngine engine,
            IAuditLogService audit,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var actor = principal.User?.Username ?? "admin";
            var deleted = await engine.DeleteQueueItemNowAsync(id, actor, ct);
            await audit.RecordAsync(new AuditWriteRequest(
                Action: "cleanerr.queue.deleted_now",
                ResourceType: "cleanerr_queue_item",
                ResourceId: id.ToString(),
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: actor,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                Details: new { deleted.MediaTitle, deleted.MediaType, deleted.SizeBytes },
                TraceId: ctx.TraceIdentifier), ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "queue.deleted_now", ct);
            return Results.Ok(deleted);
        }).RequireScope(PermissionScopes.CleanerrExecute);

        grp.MapDelete("/queue/{id:long}", async (
            long id,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            await engine.DeleteQueueItemRecordAsync(id, ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "queue.record_deleted", ct);
            return Results.NoContent();
        }).RequireScope(PermissionScopes.CleanerrWrite);

        // --- Exclusions & Bulk Simulation Exclusion ---
        grp.MapGet("/exclusions", async (ICleanerrLifecycleEngine engine, CancellationToken ct) =>
            Results.Ok(await engine.ListExclusionRulesAsync(ct)))
            .RequireScope(PermissionScopes.CleanerrRead);

        grp.MapPost("/exclusions", async (
            HttpContext ctx,
            CreateCleanerrExclusionRequest req,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var quota = principal.User?.EffectivePermissions.CleanerrExclusionLimit;
            var rule = await engine.CreateExclusionRuleAsync(
                req,
                principal.User?.Username ?? "api",
                principal.User?.Id,
                quota,
                ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "exclusions.created", ct);
            return Results.Created($"/api/v1/cleanerr/exclusions/{rule.Id}", rule);
        }).RequireScope(PermissionScopes.CleanerrExclude);

        grp.MapPost("/exclusions/bulk", async (
            HttpContext ctx,
            BulkExcludeCleanerrItemsRequest req,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var quota = principal.User?.EffectivePermissions.CleanerrExclusionLimit;
            var titles = await engine.ExcludeMediaIdsWithFamilyEscalationAsync(
                req.MediaItemIds,
                principal.User?.Username ?? "api",
                req.Reason ?? "Bulk excluded from simulation preview",
                principal.User?.Id,
                quota,
                req.ExpiresAt,
                ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "exclusions.bulk_created", ct);
            return Results.Ok(new { excludedCount = titles.Count, excludedTitles = titles });
        }).RequireScope(PermissionScopes.CleanerrExclude);

        grp.MapDelete("/exclusions/{id:long}", async (
            long id,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            await engine.DeleteExclusionRuleAsync(id, ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "exclusions.deleted", ct);
            return Results.NoContent();
        }).RequireScope(PermissionScopes.CleanerrWrite);

        // --- General Settings ---
        grp.MapGet("/settings", async (ICleanerrLifecycleEngine engine, CancellationToken ct) =>
            Results.Ok(await engine.GetGeneralSettingsAsync(ct)))
            .RequireScope(PermissionScopes.CleanerrRead);

        grp.MapPut("/settings", async (
            UpdateCleanerrGeneralSettingsRequest req,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            var updated = await engine.UpdateGeneralSettingsAsync(req, ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "settings.updated", ct);
            return Results.Ok(updated);
        }).RequireScope(PermissionScopes.CleanerrWrite);

        // --- Discord Bot Settings, Discovery, Gateway Reconnect, Slash Command Sync & Test Notification ---
        grp.MapGet("/discord/settings", async (ICleanerrDiscordService discord, CancellationToken ct) =>
            Results.Ok(await discord.GetSettingsAsync(ct)))
            .RequireScope(PermissionScopes.CleanerrRead);

        grp.MapPut("/discord/settings", async (
            UpdateCleanerrDiscordSettingsRequest req,
            ICleanerrDiscordService discord,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            var updated = await discord.UpdateSettingsAsync(req, ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "discord.settings_updated", ct);
            return Results.Ok(updated);
        }).RequireScope(PermissionScopes.CleanerrWrite);

        grp.MapGet("/discord/discover", async (ICleanerrDiscordService discord, CancellationToken ct) =>
            Results.Ok(await discord.DiscoverGuildsAndChannelsAsync(ct)))
            .RequireScope(PermissionScopes.CleanerrWrite);

        grp.MapPost("/discord/reconnect", async (
            ICleanerrDiscordService discord,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            discord.RequestGatewayReconnect();
            await BroadcastCleanerrUpdatedAsync(realtime, "discord.reconnect_requested", ct);
            return Results.Ok(new
            {
                reconnecting = true,
                connected = discord.IsGatewayConnected,
                message = "Discord Gateway reconnect requested.",
            });
        }).RequireScope(PermissionScopes.CleanerrWrite);

        grp.MapPost("/discord/sync-commands", async (
            ICleanerrDiscordService discord,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            var result = await discord.SyncSlashCommandsAsync(ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "discord.commands_synced", ct);
            return Results.Ok(result);
        }).RequireScope(PermissionScopes.CleanerrWrite);

        grp.MapPost("/discord/test", async (ICleanerrDiscordService discord, CancellationToken ct) =>
        {
            var res = await discord.SendTestNotificationAsync(ct);
            return Results.Content(res.ToJsonString(), "application/json; charset=utf-8");
        }).RequireScope(PermissionScopes.CleanerrWrite);

        // --- Discord Ed25519 Interactions Endpoint (No auth header; Ed25519 verified!) ---
        grp.MapPost("/webhooks/discord-interactions", async (
            HttpContext ctx,
            ICleanerrDiscordService discord,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ct);
            var rawBody = ms.ToArray();

            var sigHex = ctx.Request.Headers["X-Signature-Ed25519"].ToString();
            var timestamp = ctx.Request.Headers["X-Signature-Timestamp"].ToString();
            var cfg = await discord.GetSettingsAsync(ct);

            if (!CleanerrCrypto.VerifyDiscordEd25519Signature(cfg.PublicKeyHex, sigHex, timestamp, rawBody))
            {
                return Results.Unauthorized();
            }

            JsonObject json;
            try
            {
                json = JsonNode.Parse(Encoding.UTF8.GetString(rawBody))?.AsObject()
                       ?? throw new JsonException("Discord interaction payload must be a JSON object.");
            }
            catch (JsonException)
            {
                return Results.BadRequest(new
                {
                    errorCode = "ERR_INVALID_JSON",
                    message = "Malformed Discord interaction JSON payload.",
                });
            }
            var responseObj = await engine.HandleDiscordInteractionAsync(json, ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "discord.interaction", ct);
            return Results.Content(responseObj.ToJsonString(), "application/json; charset=utf-8");
        }).AllowAnonymous();

        // --- HMAC-SHA256 1-Click Magic Links (Single-Use & Expiring) ---
        grp.MapGet("/webhooks/magic/exclude", async (
            HttpContext httpContext,
            long media_id,
            string sig,
            ICleanerrDiscordService discord,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            if (PrepareMagicResponseAndCheckPreview(httpContext, "exclude this item") is { } previewResult)
            {
                return previewResult;
            }

            if (!await discord.VerifyAndConsumeMagicTokenAsync($"exclude:{media_id}", sig, null, ct))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var titles = await engine.ExcludeMediaIdsWithFamilyEscalationAsync(
                [media_id],
                "magic_link",
                "Excluded via 1-Click HMAC Magic Link",
                null,
                null,
                null,
                ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "magic.excluded", ct);
            var label = titles.FirstOrDefault() ?? $"Item #{media_id}";
            return Results.Content(
                RenderMagicHtml(
                    $"Excluded: {label}",
                    $"'{label}' has been added to Cleanerr Exclusions.",
                    "#22c55e"),
                "text/html; charset=utf-8");
        }).AllowAnonymous();

        grp.MapGet("/webhooks/magic/postpone", async (
            HttpContext httpContext,
            long media_id,
            int? days,
            string sig,
            ICleanerrDiscordService discord,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            if (PrepareMagicResponseAndCheckPreview(httpContext, "postpone this item") is { } previewResult)
            {
                return previewResult;
            }

            var defaultDays = days ?? (await engine.GetGeneralSettingsAsync(ct)).DefaultPostponeDays;
            var extendDays = Math.Clamp(defaultDays, 1, 365);
            if (!await discord.VerifyAndConsumeMagicTokenAsync($"postpone:{media_id}:{extendDays}", sig, null, ct))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var postponed = await engine.PostponeMediaIdsWithFamilyAsync(
                [media_id],
                extendDays,
                $"Postponed +{extendDays}d via 1-Click HMAC Magic Link",
                ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "magic.postponed", ct);
            return Results.Content(
                RenderMagicHtml(
                    $"Postponed +{extendDays}d ({postponed.Count})",
                    $"Extended grace period by {extendDays} day(s) for {postponed.Count} staged item(s).",
                    "#f59e0b"),
                "text/html; charset=utf-8");
        }).AllowAnonymous();

        grp.MapGet("/webhooks/magic/rescue", async (
            HttpContext httpContext,
            long media_id,
            string sig,
            ICleanerrDiscordService discord,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            if (PrepareMagicResponseAndCheckPreview(httpContext, "rescue this item") is { } previewResult)
            {
                return previewResult;
            }

            if (!await discord.VerifyAndConsumeMagicTokenAsync($"rescue:{media_id}", sig, null, ct))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var rescued = await engine.RescueMediaIdsWithFamilyAsync(
                [media_id],
                "Rescued via 1-Click HMAC Magic Link",
                ct);

            await BroadcastCleanerrUpdatedAsync(realtime, "magic.rescued", ct);
            return Results.Content(
                RenderMagicHtml(
                    $"Rescued ({rescued.Count})",
                    $"Rescued {rescued.Count} queued item(s) for series/media #{media_id}.",
                    "#38bdf8"),
                "text/html; charset=utf-8");
        }).AllowAnonymous();

        grp.MapGet("/webhooks/magic/exclude-all", async (
            HttpContext httpContext,
            long? workflow_id,
            string sig,
            ICleanerrDiscordService discord,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            if (PrepareMagicResponseAndCheckPreview(httpContext, "exclude all staged items") is { } previewResult)
            {
                return previewResult;
            }

            var wfVal = workflow_id ?? 0;
            if (!await discord.VerifyAndConsumeMagicTokenAsync($"exclude_all:{wfVal}", sig, null, ct))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var cnt = await engine.ExcludeAllStagedAsync(
                wfVal > 0 ? wfVal : null,
                "Bulk Excluded via 1-Click HMAC Magic Link",
                null,
                null,
                ct);
            await BroadcastCleanerrUpdatedAsync(realtime, "magic.excluded_all", ct);
            return Results.Content(
                RenderMagicHtml(
                    $"Excluded Whole List ({cnt})",
                    $"All {cnt} staged item(s) have been permanently excluded.",
                    "#a855f7"),
                "text/html; charset=utf-8");
        }).AllowAnonymous();

        // --- Inbound Plex Playback Auto-Rescue & n8n/External Webhooks ---
        grp.MapPost("/webhooks/{source}", async (
            string source,
            HttpContext ctx,
            ICleanerrLifecycleEngine engine,
            IRealtimeBroadcaster realtime,
            CancellationToken ct) =>
        {
            JsonObject payload;
            try
            {
                if (ctx.Request.HasFormContentType)
                {
                    var form = await ctx.Request.ReadFormAsync(ct);
                    var rawPayload = form["payload"].ToString();
                    payload = string.IsNullOrWhiteSpace(rawPayload)
                        ? new JsonObject()
                        : JsonNode.Parse(rawPayload)?.AsObject() ?? new JsonObject();
                }
                else
                {
                    using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                    var raw = await sr.ReadToEndAsync(ct);
                    payload = string.IsNullOrWhiteSpace(raw)
                        ? new JsonObject()
                        : JsonNode.Parse(raw)?.AsObject() ?? new JsonObject();
                }
            }
            catch (JsonException)
            {
                return Results.BadRequest(new
                {
                    errorCode = "ERR_INVALID_JSON",
                    message = "Malformed inbound webhook JSON payload.",
                });
            }

            var res = await engine.HandleInboundWebhookAsync(source, payload, ct);
            await BroadcastCleanerrUpdatedAsync(realtime, $"webhook.{source}", ct);
            return Results.Content(res.ToJsonString(), "application/json; charset=utf-8");
        }).RequireScope(PermissionScopes.CleanerrExecute);

        return app;
    }

    private static async Task<IResult> HandlePreviewImpactAsync(
        long? workflowId,
        HttpContext ctx,
        ICleanerrLifecycleEngine engine,
        CancellationToken ct)
    {
        var principal = ctx.GetRequiredPrincipal();
        var req = await TryReadOptionalJsonAsync<PreviewCleanerrLiveImpactRequest>(ctx, ct);
        var effectiveReq = (req ?? new PreviewCleanerrLiveImpactRequest()) with
        {
            ActorUsername = req?.ActorUsername ?? principal.User?.Username,
        };
        return Results.Ok(await engine.PreviewWorkflowLiveImpactAsync(workflowId, effectiveReq, ct));
    }

    private static async Task<IResult> HandleExecuteWorkflowAsync(
        long id,
        HttpContext ctx,
        ICleanerrLifecycleEngine engine,
        IAuditLogService audit,
        IRealtimeBroadcaster realtime,
        CancellationToken ct)
    {
        var principal = ctx.GetRequiredPrincipal();
        var req = await TryReadOptionalJsonAsync<ExecuteCleanerrWorkflowHttpRequest>(ctx, ct);
        CleanerrWorkflowRunResult run;
        try
        {
            run = await engine.ExecuteWorkflowAsync(
                id,
                req?.DryRun,
                "manual",
                req?.SimulatedFreeSpaceGb,
                req?.SimulatedFreeSpacePercent,
                ct);
        }
        catch (CleanerrWorkflowApiDisabledException ex)
        {
            return Results.BadRequest(new
            {
                errorCode = "ERR_WORKFLOW_API_DISABLED",
                message = ex.Message,
            });
        }

        await audit.RecordAsync(new AuditWriteRequest(
            Action: "cleanerr.workflow.executed",
            ResourceType: "cleanerr_run",
            ResourceId: run.RunId?.ToString(),
            ActorUserId: principal.User?.Id,
            ActorPlexUserId: principal.User?.PlexUserId,
            ActorUsername: principal.User?.Username,
            ActorApiKeyPrefix: principal.ApiKeyPrefix,
            IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
            Details: new { WorkflowId = id, run.DryRun, run.Summary.StagedCount, run.Summary.DeletedCount },
            TraceId: ctx.TraceIdentifier), ct);
        await BroadcastCleanerrUpdatedAsync(realtime, "workflow.executed", ct);
        return Results.Ok(run);
    }

    private static bool HasScope(AuthenticatedPrincipal principal, string scope) =>
        principal.Scopes.Any(s => string.Equals(s, scope, StringComparison.OrdinalIgnoreCase));

    private static string? ResolveConfirmedWorkflowName(HttpContext ctx, string? confirmWorkflowName) =>
        !string.IsNullOrWhiteSpace(confirmWorkflowName)
            ? confirmWorkflowName
            : ctx.Request.Query["confirmWorkflowName"].FirstOrDefault()
              ?? ctx.Request.Headers["X-Confirm-Workflow-Name"].FirstOrDefault();

    private static async Task<T?> TryReadOptionalJsonAsync<T>(HttpContext ctx, CancellationToken ct)
        where T : class
    {
        if (ctx.Request.ContentLength is > 0 ||
            ctx.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                return await ctx.Request.ReadFromJsonAsync<T>(cancellationToken: ct);
            }
            catch
            {
                // Allow empty or malformed optional bodies to fall back cleanly
            }
        }

        return null;
    }

    private static async Task BroadcastCleanerrUpdatedAsync(
        IRealtimeBroadcaster realtime,
        string action,
        CancellationToken ct)
    {
        try
        {
            await realtime.PublishScopedEventAsync(
                PermissionScopes.CleanerrRead,
                "cleanerr.updated",
                new { action, timestampUtc = DateTimeOffset.UtcNow },
                ct);
        }
        catch
        {
            // Realtime broadcast is best-effort and must not fail the HTTP request
        }
    }

    private static string RenderMagicHtml(string title, string subtitle, string accentHex) =>
        $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <title>Managerr Cleanerr — {{WebUtility.HtmlEncode(title)}}</title>
          <style>
            body { margin:0; min-height:100vh; display:flex; align-items:center; justify-content:center; background:#090d16; color:#e2e8f0; font-family:system-ui,sans-serif; }
            .card { background:#0f172a; border:1px solid #1e293b; border-top:4px solid {{accentHex}}; border-radius:14px; padding:28px 32px; max-width:440px; text-align:center; }
            h2 { margin:0 0 8px; font-size:18px; color:#fff; }
            p { margin:0 0 18px; font-size:13px; color:#94a3b8; line-height:1.5; }
            a { display:inline-block; padding:8px 16px; background:#f59e0b; color:#090d16; font-weight:700; font-size:12px; border-radius:8px; text-decoration:none; }
          </style>
        </head>
        <body>
          <div class="card">
            <h2>{{WebUtility.HtmlEncode(title)}}</h2>
            <p>{{WebUtility.HtmlEncode(subtitle)}}</p>
            <a href="/">Open Managerr</a>
          </div>
        </body>
        </html>
        """;

    private static readonly string[] LinkPreviewBotTokens =
    [
        "Discordbot",
        "Slackbot",
        "Twitterbot",
        "facebookexternalhit",
        "WhatsApp",
        "TelegramBot",
        "LinkedInBot",
    ];

    private static IResult? PrepareMagicResponseAndCheckPreview(HttpContext httpContext, string actionDescription)
    {
        httpContext.Response.Headers.CacheControl = "no-store, no-cache";
        httpContext.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";

        var userAgent = httpContext.Request.Headers.UserAgent.ToString();
        var purpose = httpContext.Request.Headers["Purpose"].ToString();
        var secPurpose = httpContext.Request.Headers["Sec-Purpose"].ToString();

        var isBotPreview = LinkPreviewBotTokens.Any(tok => userAgent.Contains(tok, StringComparison.OrdinalIgnoreCase))
            || purpose.Contains("prefetch", StringComparison.OrdinalIgnoreCase)
            || secPurpose.Contains("prefetch", StringComparison.OrdinalIgnoreCase);

        if (!isBotPreview)
        {
            return null;
        }

        return Results.Content(
            RenderMagicHtml(
                "Cleanerr Action Link",
                $"Open this link in your browser to {actionDescription}. Automated link previews do not consume this token.",
                "#38bdf8"),
            "text/html; charset=utf-8");
    }
}
