using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Managerr.Cleanerr;
using Managerr.Security.Rbac;

namespace Managerr.Host.Mcp;

public sealed partial class ManagerrMcpServer
{
    private void RegisterCleanerrTools(List<McpToolDefinition> list)
    {
        list.Add(new McpToolDefinition(
            Name: "cleanerr_get_overview",
            Description: "Get Cleanerr storage overview, staged Leaving Soon counts, exclusions count, enabled workflows, and Discord Gateway status.",
            RequiredAnyScopes: [PermissionScopes.CleanerrRead],
            InputSchema: ObjSchema(_ => { }),
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: async (_, _, ct) =>
            {
                var engine = RequireCleanerrEngine();
                return await engine.GetOverviewStatsAsync(ct);
            }));

        list.Add(new McpToolDefinition(
            Name: "cleanerr_list_workflows",
            Description: "List configured Cleanerr lifecycle workflows (both Maintainerr rule-group and visual DAG workflows) with dry-run, schedule, and last run status.",
            RequiredAnyScopes: [PermissionScopes.CleanerrRead],
            InputSchema: ObjSchema(_ => { }),
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: async (_, _, ct) =>
            {
                var engine = RequireCleanerrEngine();
                var workflows = await engine.ListWorkflowsAsync(ct);
                return new { count = workflows.Count, workflows };
            }));

        list.Add(new McpToolDefinition(
            Name: "cleanerr_simulate_workflow",
            Description: "Dry-run simulate a Cleanerr workflow by workflowId or inline graphJson/ruleGroup (with optional simulated free disk space overrides) without staging or deleting any media.",
            RequiredAnyScopes: [PermissionScopes.CleanerrRead],
            InputSchema: ObjSchema(p =>
            {
                p["workflowId"] = Prop("integer", "Optional Cleanerr workflow ID to simulate.");
                p["graphJson"] = Prop("object", "Optional inline DAG graph JSON to simulate.");
                p["ruleGroup"] = Prop("object", "Optional inline Maintainerr ruleGroup JSON to simulate.");
                p["simulatedFreeSpaceGb"] = Prop("number", "Optional simulated free disk space in GB for threshold node testing.");
                p["simulatedFreeSpacePercent"] = Prop("number", "Optional simulated free disk space percentage (0-100) for threshold node testing.");
            }),
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: async (_, args, ct) =>
            {
                var engine = RequireCleanerrEngine();
                var workflowId = GetOptLong(args, "workflowId");
                var inlineGraph = GetOptJsonObject(args, "graphJson");
                var inlineRuleGroup = GetOptRuleGroup(args, "ruleGroup");

                if (!workflowId.HasValue && inlineGraph is null && inlineRuleGroup is null)
                {
                    throw new ArgumentException("Either 'workflowId', 'graphJson', or 'ruleGroup' must be provided.");
                }

                if (workflowId.HasValue)
                {
                    var existing = await engine.GetWorkflowByIdAsync(workflowId.Value, ct)
                        ?? throw new KeyNotFoundException($"Cleanerr workflow #{workflowId.Value} not found.");
                    workflowId = existing.Id;
                }

                var simReq = new SimulateCleanerrWorkflowRequest(
                    GraphJson: inlineGraph,
                    WorkflowId: workflowId,
                    SimulatedFreeSpaceGb: GetOptDouble(args, "simulatedFreeSpaceGb"),
                    SimulatedFreeSpacePercent: GetOptDouble(args, "simulatedFreeSpacePercent"),
                    RuleGroup: inlineRuleGroup);
                return await engine.SimulateWorkflowAsync(simReq, ct);
            }));

        list.Add(new McpToolDefinition(
            Name: "cleanerr_list_queue",
            Description: "List items in Cleanerr's Leaving Soon deletion queue, optionally filtered by status.",
            RequiredAnyScopes: [PermissionScopes.CleanerrRead],
            InputSchema: ObjSchema(p =>
            {
                p["status"] = Prop("string", "Optional status filter.", "staged", "rescued", "deleted", "excluded", "failed");
            }),
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: async (_, args, ct) =>
            {
                var engine = RequireCleanerrEngine();
                var status = GetOptString(args, "status");
                var items = await engine.ListDeletionQueueAsync(status, ct);
                return new { count = items.Count, items };
            }));

        list.Add(new McpToolDefinition(
            Name: "cleanerr_exclude_media",
            Description: "Exclude one or more Cleanerr media items from deletion (with whole-series family escalation for TV shows).",
            RequiredAnyScopes: [PermissionScopes.CleanerrExclude],
            InputSchema: ObjSchema(p =>
            {
                p["mediaItemIds"] = ArrayProp("integer", "Cleanerr media item IDs to permanently or temporarily exclude.");
                p["reason"] = Prop("string", "Optional exclusion reason.");
                p["expiresAt"] = Prop("string", "Optional ISO-8601 expiration timestamp for temporary exclusions.");
            }, "mediaItemIds"),
            ReadOnlyHint: false,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: async (principal, args, ct) =>
            {
                var engine = RequireCleanerrEngine();
                var mediaIds = GetOptLongArray(args, "mediaItemIds");
                if (mediaIds is null || mediaIds.Count == 0)
                {
                    throw new ArgumentException("Argument 'mediaItemIds' is required and must contain at least one integer ID.");
                }

                var reason = GetOptString(args, "reason") ?? "Excluded via MCP tool";
                var expiresAt = GetOptString(args, "expiresAt");
                var actor = principal.User?.Username ?? principal.ApiKeyPrefix ?? "mcp";
                var quota = principal.User?.EffectivePermissions.CleanerrExclusionLimit;

                var excludedTitles = await engine.ExcludeMediaIdsWithFamilyEscalationAsync(
                    mediaIds,
                    actor,
                    reason,
                    principal.User?.Id,
                    quota,
                    expiresAt,
                    ct);

                return new { excludedCount = excludedTitles.Count, excludedTitles };
            }));

        list.Add(new McpToolDefinition(
            Name: "cleanerr_test_media",
            Description: "Evaluate a specific Cleanerr media item against a workflow's rules and return detailed per-condition pass/fail traces and guard notes.",
            RequiredAnyScopes: [PermissionScopes.CleanerrRead],
            InputSchema: ObjSchema(p =>
            {
                p["mediaItemId"] = Prop("integer", "Cleanerr media item ID to evaluate.");
                p["workflowId"] = Prop("integer", "Optional Cleanerr workflow ID to test against.");
                p["simulatedFreeSpaceGb"] = Prop("number", "Optional simulated free disk space in GB.");
                p["simulatedFreeSpacePercent"] = Prop("number", "Optional simulated free disk space percentage (0-100).");
            }, "mediaItemId"),
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: async (_, args, ct) =>
            {
                var engine = RequireCleanerrEngine();
                var mediaItemId = GetReqLong(args, "mediaItemId");
                var workflowId = GetOptLong(args, "workflowId");
                return await engine.TestMediaAsync(
                    new TestCleanerrMediaRequest(
                        MediaItemId: mediaItemId,
                        WorkflowId: workflowId,
                        SimulatedFreeSpaceGb: GetOptDouble(args, "simulatedFreeSpaceGb"),
                        SimulatedFreeSpacePercent: GetOptDouble(args, "simulatedFreeSpacePercent")),
                    ct);
            }));

        list.Add(new McpToolDefinition(
            Name: "cleanerr_run_workflow",
            Description: "Execute a saved Cleanerr workflow by workflowId (respecting trigger.manual allow_api settings and optional dryRun override).",
            RequiredAnyScopes: [PermissionScopes.CleanerrExecute],
            InputSchema: ObjSchema(p =>
            {
                p["workflowId"] = Prop("integer", "Cleanerr workflow ID to execute.");
                p["dryRun"] = Prop("boolean", "Optional dry-run override (true = preview only, false = live execution).");
            }, "workflowId"),
            ReadOnlyHint: false,
            DestructiveHint: true,
            IdempotentHint: false,
            Handler: async (_, args, ct) =>
            {
                var engine = RequireCleanerrEngine();
                var workflowId = GetReqLong(args, "workflowId");
                var dryRun = GetOptBool(args, "dryRun");
                return await engine.ExecuteWorkflowAsync(workflowId, dryRunOverride: dryRun, triggerType: "mcp", ct: ct);
            }));

        list.Add(new McpToolDefinition(
            Name: "cleanerr_rescue_queue_item",
            Description: "Rescue a staged item from Cleanerr's Leaving Soon deletion queue.",
            RequiredAnyScopes: [PermissionScopes.CleanerrRescue],
            InputSchema: ObjSchema(p =>
            {
                p["queueItemId"] = Prop("integer", "Deletion queue item ID to rescue.");
                p["reason"] = Prop("string", "Optional rescue reason.");
            }, "queueItemId"),
            ReadOnlyHint: false,
            DestructiveHint: false,
            IdempotentHint: true,
            Handler: async (principal, args, ct) =>
            {
                var engine = RequireCleanerrEngine();
                var queueItemId = GetReqLong(args, "queueItemId");
                var actor = principal.User?.Username ?? principal.ApiKeyPrefix ?? "mcp";
                var reason = GetOptString(args, "reason") ?? $"Rescued via MCP tool by {actor}";
                return await engine.RescueQueueItemAsync(queueItemId, reason, ct);
            }));

        list.Add(new McpToolDefinition(
            Name: "cleanerr_postpone_queue_item",
            Description: "Postpone a staged Cleanerr Leaving Soon queue item by N days (defaults to configured DefaultPostponeDays).",
            RequiredAnyScopes: [PermissionScopes.CleanerrRescue],
            InputSchema: ObjSchema(p =>
            {
                p["queueItemId"] = Prop("integer", "Deletion queue item ID to postpone.");
                p["days"] = Prop("integer", "Optional number of days to postpone (1-365).");
            }, "queueItemId"),
            ReadOnlyHint: false,
            DestructiveHint: false,
            IdempotentHint: false,
            Handler: async (principal, args, ct) =>
            {
                var engine = RequireCleanerrEngine();
                var queueItemId = GetReqLong(args, "queueItemId");
                var days = GetOptInt(args, "days") ?? (await engine.GetGeneralSettingsAsync(ct)).DefaultPostponeDays;
                var actor = principal.User?.Username ?? principal.ApiKeyPrefix ?? "mcp";
                return await engine.PostponeQueueItemAsync(queueItemId, days, $"Postponed +{days}d via MCP tool by {actor}", ct);
            }));

        list.Add(new McpToolDefinition(
            Name: "cleanerr_delete_exclusion",
            Description: "Delete an existing Cleanerr exclusion rule by exclusionId.",
            RequiredAnyScopes: [PermissionScopes.CleanerrWrite],
            InputSchema: ObjSchema(p =>
            {
                p["exclusionId"] = Prop("integer", "Cleanerr exclusion rule ID to remove.");
            }, "exclusionId"),
            ReadOnlyHint: false,
            DestructiveHint: true,
            IdempotentHint: true,
            Handler: async (_, args, ct) =>
            {
                var engine = RequireCleanerrEngine();
                var exclusionId = GetReqLong(args, "exclusionId");
                await engine.DeleteExclusionRuleAsync(exclusionId, ct);
                return new { deleted = true, exclusionId };
            }));
    }

    private ICleanerrLifecycleEngine RequireCleanerrEngine() =>
        _cleanerrEngine ?? throw new InvalidOperationException("Cleanerr lifecycle engine is not configured.");

    private static JsonObject? GetOptJsonObject(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return JsonNode.Parse(el.GetRawText())?.AsObject();
    }

    private static CleanerrRuleGroupSpec? GetOptRuleGroup(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return JsonSerializer.Deserialize<CleanerrRuleGroupSpec>(el.GetRawText(), JsonOpts);
    }

    private static double? GetOptDouble(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el))
        {
            return null;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d))
        {
            return d;
        }

        if (el.ValueKind == JsonValueKind.String &&
            double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
