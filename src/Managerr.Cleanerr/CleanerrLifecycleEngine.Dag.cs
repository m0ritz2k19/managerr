using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Managerr.Datastore;

namespace Managerr.Cleanerr;

public sealed partial class CleanerrLifecycleEngine
{
    public async Task<CleanerrWorkflowRunResult> SimulateWorkflowAsync(
        SimulateCleanerrWorkflowRequest request,
        CancellationToken ct = default)
    {
        var ruleGroup = request.RuleGroup;
        var graph = request.GraphJson;

        if (ruleGroup is not null && graph is null)
        {
            graph = CleanerrMaintainerrRuleEngine.CompileRuleGroupToSummaryGraph(ruleGroup);
        }
        else if (graph is null && request.WorkflowId.HasValue)
        {
            var existing = await GetWorkflowByIdAsync(request.WorkflowId.Value, ct);
            if (existing is not null)
            {
                ruleGroup ??= existing.RuleGroup;
                graph = existing.EditorMode == "rules" && existing.RuleGroup is not null
                    ? CleanerrMaintainerrRuleEngine.CompileRuleGroupToSummaryGraph(existing.RuleGroup, existing.ScheduleCron)
                    : existing.GraphJson;
            }
        }

        graph ??= new JsonObject { ["nodes"] = new JsonArray(), ["edges"] = new JsonArray() };

        return await RunWorkflowGraphCoreAsync(
            graphJson: graph,
            workflowId: request.WorkflowId,
            workflowName: "Simulation Preview",
            dryRun: true,
            triggerType: "simulate",
            recordRun: false,
            simulatedFreeGb: request.SimulatedFreeSpaceGb,
            simulatedFreePct: request.SimulatedFreeSpacePercent,
            ruleGroupOverride: ruleGroup,
            ct: ct);
    }

    public async Task<CleanerrLiveImpactPreview> PreviewWorkflowLiveImpactAsync(
        long? workflowId,
        PreviewCleanerrLiveImpactRequest? request = null,
        CancellationToken ct = default)
    {
        CleanerrWorkflowSummary? existing = null;
        if (workflowId.HasValue)
        {
            existing = await GetWorkflowByIdAsync(workflowId.Value, ct);
            if (existing is null && request?.GraphJson is null && request?.RuleGroup is null)
            {
                throw new KeyNotFoundException($"Cleanerr workflow #{workflowId.Value} not found.");
            }
        }

        var workflowName = !string.IsNullOrWhiteSpace(request?.WorkflowName)
            ? request.WorkflowName.Trim()
            : (existing?.Name ?? "Cleanerr Workflow");
        var currentlyDryRun = existing?.DryRun ?? true;

        var sim = await SimulateWorkflowAsync(
            new SimulateCleanerrWorkflowRequest(
                GraphJson: request?.GraphJson,
                WorkflowId: workflowId,
                SimulatedFreeSpaceGb: request?.SimulatedFreeSpaceGb,
                SimulatedFreeSpacePercent: request?.SimulatedFreeSpacePercent,
                RuleGroup: request?.RuleGroup),
            ct);

        var matchedPreviews = sim.Summary.MatchedItems ?? [];
        var matchedIds = matchedPreviews.Select(m => m.Id).Distinct().ToList();
        var allMediaById = (await ListMediaItemsAsync(ct)).ToDictionary(m => m.Id);
        var matchedItems = matchedIds
            .Select(id => allMediaById.GetValueOrDefault(id))
            .Where(m => m is not null)
            .Select(m => m!)
            .ToList();

        var itemCount = matchedItems.Count;
        var totalBytes = matchedItems.Sum(m => m.SizeBytes);
        var totalGigabytes = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 2);
        var affectedMovieCount = matchedItems.Count(m => string.Equals(m.MediaType, "movie", StringComparison.OrdinalIgnoreCase));

        var seriesKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in matchedItems.Where(m => m.MediaType is "show" or "season" or "episode"))
        {
            var key = it.WorkId.HasValue
                ? $"work:{it.WorkId.Value}"
                : it.MediaType switch
                {
                    "show" => it.PlexRatingKey,
                    "season" => it.ParentRatingKey ?? it.ParentTitle ?? it.PlexRatingKey,
                    _ => it.GrandparentRatingKey ?? it.GrandparentTitle ?? it.ParentRatingKey ?? it.PlexRatingKey,
                };
            seriesKeys.Add(key);
        }

        var affectedSeriesCount = seriesKeys.Count;
        var otherMediaCount = matchedItems.Count(m => m.MediaType is not ("movie" or "show" or "season" or "episode"));
        var affectedWorkCount = affectedMovieCount + affectedSeriesCount + otherMediaCount;
        var unwatchedCount = matchedItems.Count(m => m.ViewCount == 0 && string.IsNullOrWhiteSpace(m.LastViewedAt));

        var adminUsernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "admin", "plexowner" };
        if (!string.IsNullOrWhiteSpace(request?.ActorUsername))
        {
            adminUsernames.Add(request.ActorUsername.Trim());
        }

        await using (var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            var dbAdmins = await conn.QueryAsync<string>("SELECT username FROM users WHERE is_server_owner = 1;");
            foreach (var u in dbAdmins)
            {
                if (!string.IsNullOrWhiteSpace(u))
                {
                    adminUsernames.Add(u.Trim());
                }
            }
        }

        var otherUsersRequestedCount = matchedItems.Count(m =>
            !string.IsNullOrWhiteSpace(m.SeerrRequestedBy) &&
            !adminUsernames.Contains(m.SeerrRequestedBy.Trim()));

        var requiresNameConfirmation =
            itemCount > 0 ||
            unwatchedCount > 0 ||
            totalBytes >= 10L * 1024 * 1024 * 1024;

        return new CleanerrLiveImpactPreview(
            WorkflowId: workflowId ?? existing?.Id,
            WorkflowName: workflowName,
            CurrentlyDryRun: currentlyDryRun,
            ItemCount: itemCount,
            TotalBytes: totalBytes,
            TotalGigabytes: totalGigabytes,
            AffectedSeriesCount: affectedSeriesCount,
            AffectedMovieCount: affectedMovieCount,
            AffectedWorkCount: affectedWorkCount,
            UnwatchedCount: unwatchedCount,
            OtherUsersRequestedCount: otherUsersRequestedCount,
            RequiresNameConfirmation: requiresNameConfirmation,
            MatchedItems: matchedPreviews);
    }

    public async Task<CleanerrWorkflowRunResult> ExecuteWorkflowAsync(
        long workflowId,
        bool? dryRunOverride = null,
        string triggerType = "manual",
        double? simulatedFreeSpaceGb = null,
        double? simulatedFreeSpacePercent = null,
        CancellationToken ct = default)
    {
        var wf = await GetWorkflowByIdAsync(workflowId, ct)
            ?? throw new KeyNotFoundException($"Cleanerr workflow #{workflowId} not found.");

        var effectiveDryRun = dryRunOverride ?? wf.DryRun;

        // Maintainerr Transient-Failure Guard: if Plex is configured and a scheduled run cannot reach Plex,
        // abort cleanly without staging or deleting anything based on stale playback history.
        if (string.Equals(triggerType, "scheduled", StringComparison.OrdinalIgnoreCase))
        {
            var (plexUrl, _) = await TryResolvePlexConnectionAsync(ct);
            if (!string.IsNullOrWhiteSpace(plexUrl))
            {
                try
                {
                    await SyncFromPlexServerAsync(null, ct);
                }
                catch (Exception ex)
                {
                    var nowIso = DateTimeOffset.UtcNow.ToString("O");
                    var abortSummary = new CleanerrWorkflowRunSummary(
                        NodeMetrics: new Dictionary<string, CleanerrNodeExecutionMetric>(),
                        StagedCount: 0,
                        DeletedCount: 0,
                        SpaceBytes: 0,
                        Logs: [$"Aborted scheduled run: Plex server '{plexUrl}' unreachable ({ex.Message}) — protecting library against transient downtime."],
                        DryRun: effectiveDryRun,
                        TriggerType: triggerType);
                    var abortJson = JsonSerializer.Serialize(abortSummary, JsonOpts);
                    var abortRunId = await dbFactory.ExecuteWriteAsync(
                        DatabaseKind.Core,
                        async (conn, tx) =>
                        {
                            var rid = await conn.ExecuteScalarAsync<long>(
                                """
                                INSERT INTO cleanerr_runs (
                                    workflow_id, trigger_type, dry_run, status, started_at, finished_at, summary_json
                                ) VALUES (
                                    @WfId, @Trig, @DryRun, 'skipped_transient_plex_error', @Now, @Now, @Sum
                                );
                                SELECT last_insert_rowid();
                                """,
                                new { WfId = wf.Id, Trig = triggerType, DryRun = effectiveDryRun ? 1 : 0, Now = nowIso, Sum = abortJson },
                                transaction: tx);
                            await conn.ExecuteAsync(
                                "UPDATE cleanerr_workflows SET last_run_at = @Now, last_run_status = 'skipped_transient_plex_error' WHERE id = @WfId;",
                                new { WfId = wf.Id, Now = nowIso },
                                transaction: tx);
                            return rid;
                        },
                        ct);

                    return new CleanerrWorkflowRunResult(
                        RunId: abortRunId,
                        WorkflowId: wf.Id,
                        TriggerType: triggerType,
                        DryRun: effectiveDryRun,
                        Status: "skipped_transient_plex_error",
                        StartedAt: nowIso,
                        FinishedAt: nowIso,
                        Summary: abortSummary);
                }
            }
        }

        var effectiveGraph = wf.EditorMode == "rules" && wf.RuleGroup is not null
            ? CleanerrMaintainerrRuleEngine.CompileRuleGroupToSummaryGraph(wf.RuleGroup, wf.ScheduleCron)
            : wf.GraphJson;

        if (triggerType is "manual" or "api" or "mcp")
        {
            var graphNodes = effectiveGraph["nodes"]?.AsArray()?.OfType<JsonObject>() ?? [];
            foreach (var rawNode in graphNodes)
            {
                var nodeData = rawNode["data"]?.AsObject();
                var nType = nodeData?["node_type"]?.GetValue<string>()
                            ?? rawNode["node_type"]?.GetValue<string>()
                            ?? "";
                if (string.Equals(nType, "trigger.manual", StringComparison.OrdinalIgnoreCase))
                {
                    var nodeCfg = nodeData?["config"]?.AsObject()
                                  ?? rawNode["config"]?.AsObject()
                                  ?? new JsonObject();
                    if (GetBool(nodeCfg, "allow_api") == false)
                    {
                        throw new CleanerrWorkflowApiDisabledException(
                            $"Workflow '{wf.Name}' has trigger.manual allow_api disabled.");
                    }
                }
            }
        }

        return await RunWorkflowGraphCoreAsync(
            graphJson: effectiveGraph,
            workflowId: wf.Id,
            workflowName: wf.Name,
            dryRun: effectiveDryRun,
            triggerType: triggerType,
            recordRun: true,
            simulatedFreeGb: simulatedFreeSpaceGb,
            simulatedFreePct: simulatedFreeSpacePercent,
            ruleGroupOverride: wf.EditorMode == "rules" ? wf.RuleGroup : null,
            ct: ct);
    }

    private async Task<CleanerrWorkflowRunResult> RunWorkflowGraphCoreAsync(
        JsonObject graphJson,
        long? workflowId,
        string workflowName,
        bool dryRun,
        string triggerType,
        bool recordRun,
        double? simulatedFreeGb,
        double? simulatedFreePct,
        CleanerrRuleGroupSpec? ruleGroupOverride,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        await SyncCatalogItemsIntoCleanerrAsync(ct);

        var nodesArr = graphJson["nodes"]?.AsArray() ?? [];
        var edgesArr = graphJson["edges"]?.AsArray() ?? [];

        var nodesById = new Dictionary<string, ParsedGraphNode>(StringComparer.Ordinal);
        foreach (var rawNode in nodesArr.OfType<JsonObject>())
        {
            var nid = rawNode["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(nid)) continue;
            var data = rawNode["data"]?.AsObject();
            var nodeType = data?["node_type"]?.GetValue<string>()
                           ?? rawNode["node_type"]?.GetValue<string>()
                           ?? rawNode["type"]?.GetValue<string>()
                           ?? "";
            var cfg = data?["config"]?.AsObject()
                      ?? rawNode["config"]?.AsObject()
                      ?? new JsonObject();
            var label = data?["label"]?.GetValue<string>()
                        ?? rawNode["label"]?.GetValue<string>()
                        ?? nodeType;
            nodesById[nid] = new ParsedGraphNode(nid, label, nodeType, cfg);
        }

        var adj = nodesById.Keys.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
        var revAdj = nodesById.Keys.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
        var inDegree = nodesById.Keys.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);

        foreach (var rawEdge in edgesArr.OfType<JsonObject>())
        {
            var src = rawEdge["source"]?.ToString() ?? "";
            var tgt = rawEdge["target"]?.ToString() ?? "";
            if (nodesById.ContainsKey(src) && nodesById.ContainsKey(tgt))
            {
                adj[src].Add(tgt);
                revAdj[tgt].Add(src);
                inDegree[tgt]++;
            }
        }

        // Kahn's topological sort
        var q = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var topoOrder = new List<string>();
        while (q.Count > 0)
        {
            var curr = q.Dequeue();
            topoOrder.Add(curr);
            foreach (var nxt in adj[curr])
            {
                inDegree[nxt]--;
                if (inDegree[nxt] == 0) q.Enqueue(nxt);
            }
        }
        foreach (var nid in nodesById.Keys.Where(nid => !topoOrder.Contains(nid)))
        {
            topoOrder.Add(nid);
        }

        var allMedia = await ListMediaItemsAsync(ct);
        var itemsByRk = allMedia.ToDictionary(m => m.PlexRatingKey, StringComparer.Ordinal);
        var rules = await ListExclusionRulesAsync(ct);
        var libraries = await catalogService.ListLibrariesAsync(null, ct);
        var generalSettings = await GetGeneralSettingsAsync(ct);

        var nodeOutputs = new Dictionary<string, List<CleanerrMediaItem>>(StringComparer.Ordinal);
        var stagedViaGrace = new Dictionary<string, bool>(StringComparer.Ordinal);
        var haltedByTrigger = new Dictionary<string, bool>(StringComparer.Ordinal);
        var nodeMetrics = new Dictionary<string, CleanerrNodeExecutionMetric>(StringComparer.Ordinal);
        var matchedReasonsById = new Dictionary<long, string>();
        var runLogs = new List<string>(FindUnscopedDeleteActions(nodesById.Values));

        var totalStaged = 0;
        var totalDeleted = 0;
        var totalSpaceBytes = 0L;
        var totalReclaimableBytes = 0L;
        var totalHardlinkedCount = 0;
        var totalDowngraded = 0;
        long? activeStorageDeficitBytes = null;
        var activeStorageSortBy = "oldest_watched";

        List<JsonObject> CollectDownstreamActions(string startId)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var actions = new List<JsonObject>();
            var bfs = new Queue<string>(adj[startId]);
            while (bfs.Count > 0)
            {
                var cur = bfs.Dequeue();
                if (!visited.Add(cur)) continue;
                if (nodesById.TryGetValue(cur, out var cn) && cn.NodeType.StartsWith("action.", StringComparison.Ordinal))
                {
                    actions.Add(new JsonObject
                    {
                        ["node_id"] = cur,
                        ["label"] = cn.Label,
                        ["node_type"] = cn.NodeType,
                        ["config"] = cn.Config.DeepClone(),
                    });
                }
                foreach (var ch in adj[cur]) bfs.Enqueue(ch);
            }
            return actions;
        }

        foreach (var nid in topoOrder)
        {
            var node = nodesById[nid];
            var ntype = node.NodeType;
            var cfg = node.Config;
            var parents = revAdj[nid];
            var parentStreams = parents.Select(p => nodeOutputs.GetValueOrDefault(p) ?? []).ToList();
            var isBehindGrace = parents.Any(p => stagedViaGrace.GetValueOrDefault(p));
            var isHalted = parents.Count > 0 && parents.All(p => haltedByTrigger.GetValueOrDefault(p));

            var incoming = new List<CleanerrMediaItem>();
            if (!isHalted)
            {
                var seen = new HashSet<long>();
                foreach (var s in parentStreams)
                {
                    foreach (var it in s)
                    {
                        if (seen.Add(it.Id)) incoming.Add(it);
                    }
                }
            }

            var output = new List<CleanerrMediaItem>();
            var nodeLogs = new List<string>();

            if (isHalted)
            {
                haltedByTrigger[nid] = true;
                nodeLogs.Add("Skipped because upstream disk/trigger condition is healthy.");
            }
            else if (ntype is "trigger.storage_pressure" or "filter.disk_space")
            {
                var minFreeGb = GetDouble(cfg, "min_free_gb_trigger") ?? GetDouble(cfg, "min_free_gb");
                var minFreePct = GetDouble(cfg, "min_free_percent_trigger") ?? GetDouble(cfg, "min_free_percent");
                var targetFreeGb = GetDouble(cfg, "target_free_gb") ?? GetDouble(cfg, "reclaim_until_free_gb");
                activeStorageSortBy = (GetString(cfg, "sort_by") ?? "oldest_watched").ToLowerInvariant();
                var (actualFreeGb, actualFreePct) = InspectDiskSpace(simulatedFreeGb, simulatedFreePct, libraries);

                var pressureTriggered = (minFreeGb is > 0 && actualFreeGb < minFreeGb.Value) ||
                                        (minFreePct is > 0 && actualFreePct < minFreePct.Value);
                if (!pressureTriggered)
                {
                    haltedByTrigger[nid] = true;
                    nodeLogs.Add($"Storage healthy ({actualFreeGb:F1} GB / {actualFreePct:F1}% free) — skipping downstream cleanup.");
                }
                else
                {
                    if (ntype == "filter.disk_space")
                    {
                        output = incoming.ToList();
                    }

                    if (targetFreeGb.HasValue && targetFreeGb.Value > actualFreeGb)
                    {
                        var deficitGb = targetFreeGb.Value - actualFreeGb;
                        activeStorageDeficitBytes = (long)(deficitGb * 1024.0 * 1024.0 * 1024.0);
                        nodeLogs.Add($"Storage pressure active ({actualFreeGb:F1} GB free < {minFreeGb ?? 0:F1} GB) — reclaiming up to {deficitGb:F1} GB to reach {targetFreeGb.Value:F1} GB target watermark.");
                    }
                    else
                    {
                        nodeLogs.Add($"Storage pressure active ({actualFreeGb:F1} GB / {actualFreePct:F1}% free) — proceeding with cleanup.");
                    }
                }
            }
            else if (ntype.StartsWith("trigger.", StringComparison.Ordinal))
            {
                nodeLogs.Add($"Triggered via {triggerType} ({ntype})");
            }
            else if (ntype is "source.plex_library" or "source.media_server" or "source.managerr_catalog"
                     or "source.managerr_library" or "source.arr_library" or "source.tautulli"
                     or "source.overseerr" or "source.library")
            {
                var mediaType = NormalizeSourceMediaType(GetString(cfg, "media_type") ?? GetString(cfg, "mediaType"));
                var mediaTypes = GetStringList(cfg, "mediaTypes")
                    .Select(t => NormalizeSourceMediaType(t)!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var libIds = GetLongList(cfg, "library_ids");
                if (libIds.Count == 0) libIds = GetLongList(cfg, "libraryIds");
                var reqByFilter = GetString(cfg, "requested_by")?.Trim();
                var watchedOnly = GetBool(cfg, "watched_only") ?? false;

                foreach (var m in allMedia)
                {
                    if (m.MediaType is "season" or "episode")
                        continue;
                    if (!string.IsNullOrWhiteSpace(mediaType) && !string.Equals(m.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (mediaTypes.Count > 0 && !mediaTypes.Contains(m.MediaType, StringComparer.OrdinalIgnoreCase))
                        continue;
                    if (libIds.Count > 0 && (!m.LibraryId.HasValue || !libIds.Contains(m.LibraryId.Value)))
                        continue;
                    if (ntype == "source.overseerr")
                    {
                        if (!m.SeerrId.HasValue && string.IsNullOrWhiteSpace(m.SeerrRequestedBy))
                            continue;
                        if (!string.IsNullOrWhiteSpace(reqByFilter) &&
                            !string.Equals(m.SeerrRequestedBy, reqByFilter, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    if (ntype == "source.tautulli" && watchedOnly && m.ViewCount == 0 && string.IsNullOrWhiteSpace(m.LastViewedAt))
                        continue;

                    output.Add(m);
                }
                nodeLogs.Add($"Loaded {output.Count} '{mediaType ?? "all"}' items via {ntype}");
            }
            else if (ntype == "source.granularity")
            {
                var hasMediaUpstream = parents.Any(p => !nodesById[p].NodeType.StartsWith("trigger.", StringComparison.Ordinal));
                var passthrough = hasMediaUpstream
                    ? incoming.Where(m => m.MediaType is not ("season" or "episode"))
                    : allMedia.Where(m => m.MediaType is not ("season" or "episode"));
                output.AddRange(passthrough);
                nodeLogs.Add("Skipped source.granularity: Cleanerr handles TV series in full (no season/episode splitting).");
            }
            else if (ntype is "filter.maintainerr_rules" or "logic.rule_group")
            {
                var spec = ruleGroupOverride;
                if (spec is null)
                {
                    try
                    {
                        spec = JsonSerializer.Deserialize<CleanerrRuleGroupSpec>(cfg.ToJsonString(), JsonOpts);
                    }
                    catch
                    {
                        spec = null;
                    }
                }
                spec ??= new CleanerrRuleGroupSpec("movie", null, generalSettings.DefaultGraceDays, "delete_files_and_unmonitor", []);

                var (freeGb, freePct) = InspectDiskSpace(simulatedFreeGb, simulatedFreePct, libraries);
                var evalCtx = CleanerrMaintainerrRuleEngine.BuildContext(allMedia, startedAt, freeGb, freePct);

                if (spec.MinFreeSpaceGbTrigger is > 0 && freeGb >= spec.MinFreeSpaceGbTrigger.Value)
                {
                    haltedByTrigger[nid] = true;
                    nodeLogs.Add($"Disk free space ({freeGb:F1} GB) >= trigger ({spec.MinFreeSpaceGbTrigger.Value:F1} GB) — skipping rule group.");
                }
                else
                {
                    var protectedExclCount = 0;
                    foreach (var it in incoming)
                    {
                        var (matched, reason, _, _) = CleanerrMaintainerrRuleEngine.EvaluateItemAgainstRuleGroup(it, spec, evalCtx);
                        if (!matched) continue;

                        var (isExcl, exclReason) = CheckItemExcluded(it, rules, itemsByRk, workflowId, null, null, startedAt);
                        if (isExcl)
                        {
                            protectedExclCount++;
                            nodeLogs.Add($"Protected '{it.Title}': {exclReason}");
                            continue;
                        }

                        matchedReasonsById[it.Id] = reason;
                        output.Add(it);
                    }

                    long? budgetBytes = null;
                    if (spec.TargetFreeSpaceGbWatermark is > 0)
                    {
                        var deficitGb = Math.Max(0.0, spec.TargetFreeSpaceGbWatermark.Value - freeGb);
                        budgetBytes = (long)(deficitGb * 1024.0 * 1024.0 * 1024.0);
                    }
                    else if (spec.MaxReclaimGb is > 0)
                    {
                        budgetBytes = (long)(spec.MaxReclaimGb.Value * 1024.0 * 1024.0 * 1024.0);
                    }

                    if (budgetBytes.HasValue)
                    {
                        output = ApplyReclaimBudgetAndOrdering(output, budgetBytes, "oldest_watched", out var cappedBytes);
                        nodeLogs.Add($"Rule sections matched {output.Count} item(s) within {cappedBytes / (1024.0 * 1024.0 * 1024.0):F1} GB cap ({protectedExclCount} protected by exclusions)");
                    }
                    else
                    {
                        nodeLogs.Add($"Rule sections ({spec.Sections.Count}) matched {output.Count}/{incoming.Count} item(s) ({protectedExclCount} protected by exclusions)");
                    }
                }
            }
            else if (ntype == "filter.active_stream_guard")
            {
                var protectInProgress = GetBool(cfg, "protect_in_progress") ?? true;
                var protectSeries = GetBool(cfg, "protect_series") ?? GetBool(cfg, "protect_active_series") ?? false;
                var protectedCount = 0;

                foreach (var it in incoming)
                {
                    var isStreaming = it.ActiveStreamCount > 0 || (protectInProgress && it.ViewOffsetMs > 0);
                    if (!isStreaming && protectSeries && it.MediaType is "show" or "season" or "episode")
                    {
                        var (_, familyIds) = ResolveMediaToSeriesFamily(allMedia, it);
                        var famSet = familyIds.ToHashSet();
                        isStreaming = allMedia.Any(m =>
                            famSet.Contains(m.Id) &&
                            (m.ActiveStreamCount > 0 || (protectInProgress && m.ViewOffsetMs > 0)));
                    }

                    if (isStreaming)
                    {
                        protectedCount++;
                    }
                    else
                    {
                        output.Add(it);
                    }
                }
                nodeLogs.Add($"Active stream guard passed {output.Count}/{incoming.Count} items ({protectedCount} actively streaming/in-progress protected)");
            }
            else if (ntype == "filter.watch_state")
            {
                var olderDays = GetInt(cfg, "last_watched_older_than_days");
                var unwatchedOlderDays = GetInt(cfg, "unwatched_added_older_than_days");
                var includeUnwatched = GetBool(cfg, "include_unwatched_if_added_older") ?? false;
                var viewGte = GetInt(cfg, "view_count_gte");
                var viewLte = GetInt(cfg, "view_count_lte");
                var watchedBy = GetString(cfg, "watched_by_user")?.Trim().ToLowerInvariant();
                var protectUntilRequesterWatched = GetBool(cfg, "protect_until_requester_watched") ?? false;
                var protectInProgress = GetBool(cfg, "protect_in_progress") ?? true;
                var protectActiveSeriesDays = GetInt(cfg, "protect_active_series_days") ?? GetInt(cfg, "protect_if_series_watched_within_days");
                var protectedInProgressCount = 0;
                var protectedActiveSeriesCount = 0;

                foreach (var it in incoming)
                {
                    var ok = true;

                    if (protectInProgress && (it.ViewOffsetMs > 0 || it.ActiveStreamCount > 0))
                    {
                        ok = false;
                        protectedInProgressCount++;
                    }

                    if (ok && protectActiveSeriesDays is > 0 && it.MediaType is "show" or "season" or "episode")
                    {
                        var activeCutoff = startedAt.AddDays(-protectActiveSeriesDays.Value);
                        var (_, familyIds) = ResolveMediaToSeriesFamily(allMedia, it);
                        var familySet = familyIds.ToHashSet();
                        var seriesRecentlyActive = allMedia.Any(m =>
                            familySet.Contains(m.Id) &&
                            (m.ViewOffsetMs > 0 ||
                             m.ActiveStreamCount > 0 ||
                             (ParseDto(m.LastViewedAt) is { } famLv && famLv > activeCutoff)));
                        if (seriesRecentlyActive)
                        {
                            ok = false;
                            protectedActiveSeriesCount++;
                        }
                    }

                    if (ok && protectUntilRequesterWatched && !string.IsNullOrWhiteSpace(it.SeerrRequestedBy))
                    {
                        var reqWatched = it.RequesterWatched ||
                                         it.ViewedBy.Any(u => string.Equals(u, it.SeerrRequestedBy, StringComparison.OrdinalIgnoreCase));
                        if (!reqWatched) ok = false;
                    }

                    if (ok && olderDays is > 0)
                    {
                        var cutoff = startedAt.AddDays(-olderDays.Value);
                        var lv = ParseDto(it.LastViewedAt);
                        var ad = ParseDto(it.AddedAt);
                        if (lv.HasValue)
                        {
                            if (lv.Value > cutoff) ok = false;
                        }
                        else if (includeUnwatched)
                        {
                            if (ad.HasValue && ad.Value > cutoff) ok = false;
                        }
                        else
                        {
                            ok = false;
                        }
                    }

                    if (ok && unwatchedOlderDays is > 0 && !includeUnwatched)
                    {
                        var cutoffAdd = startedAt.AddDays(-unwatchedOlderDays.Value);
                        var ad = ParseDto(it.AddedAt);
                        if (it.ViewCount > 0 || (ad.HasValue && ad.Value > cutoffAdd)) ok = false;
                    }

                    if (ok && viewGte.HasValue && it.ViewCount < viewGte.Value) ok = false;
                    if (ok && viewLte.HasValue && it.ViewCount > viewLte.Value) ok = false;
                    if (ok && !string.IsNullOrWhiteSpace(watchedBy) && !it.ViewedBy.Any(u => string.Equals(u, watchedBy, StringComparison.OrdinalIgnoreCase)))
                        ok = false;

                    if (ok) output.Add(it);
                }
                nodeLogs.Add($"Watch filter matched {output.Count}/{incoming.Count} items (in-progress protected: {protectedInProgressCount}, active-binge series protected: {protectedActiveSeriesCount})");
            }
            else if (ntype == "filter.age")
            {
                var addedOlderDays = GetInt(cfg, "added_older_than_days") ?? GetInt(cfg, "older_than_days") ?? GetInt(cfg, "min_age_days");
                var yearLt = GetInt(cfg, "release_year_lt") ?? GetInt(cfg, "year_lt");
                var yearGt = GetInt(cfg, "release_year_gt") ?? GetInt(cfg, "year_gt");

                foreach (var it in incoming)
                {
                    var ok = true;
                    if (addedOlderDays is > 0)
                    {
                        var cutoff = startedAt.AddDays(-addedOlderDays.Value);
                        var added = ParseDto(it.AddedAt);
                        if (!added.HasValue || added.Value > cutoff) ok = false;
                    }
                    if (ok && yearLt.HasValue && (!it.Year.HasValue || it.Year.Value >= yearLt.Value)) ok = false;
                    if (ok && yearGt.HasValue && (!it.Year.HasValue || it.Year.Value <= yearGt.Value)) ok = false;
                    if (ok) output.Add(it);
                }
                nodeLogs.Add($"Age filter matched {output.Count}/{incoming.Count} items");
            }
            else if (ntype is "filter.metadata" or "filter.size_codec" or "filter.codec_density")
            {
                var sizeGtGb = GetDouble(cfg, "size_gt_gb") ?? GetDouble(cfg, "min_size_gb");
                var sizeLtGb = GetDouble(cfg, "size_lt_gb") ?? GetDouble(cfg, "max_size_gb");
                var res = GetString(cfg, "resolution")?.Trim();
                var resolutions = GetStringList(cfg, "resolution_in");
                if (resolutions.Count == 0) resolutions = GetStringList(cfg, "resolutions");
                var codecs = GetStringList(cfg, "codec_in");
                if (codecs.Count == 0) codecs = GetStringList(cfg, "codecs");
                var excludeDv5 = GetBool(cfg, "exclude_dv5") ?? false;
                var minBitrateMbps = GetDouble(cfg, "min_bitrate_mbps");
                var ratingLt = GetDouble(cfg, "rating_lt");
                var yearLt = GetInt(cfg, "year_lt");
                var genre = (GetString(cfg, "genre_contains") ?? (cfg["genre"] is JsonArray ? null : GetString(cfg, "genre")))?.Trim();
                var genreAny = GetStringList(cfg, "genre_any");
                if (genreAny.Count == 0) genreAny = GetStringList(cfg, "genres_any");
                if (genreAny.Count == 0 && cfg["genre"] is JsonArray) genreAny = GetStringList(cfg, "genre");
                var skipHardlinked = GetBool(cfg, "skip_hardlinked_seeding") ?? false;
                var protectDualAudio = GetBool(cfg, "protect_dual_audio") ?? false;
                var protectAudioLangs = GetStringList(cfg, "protect_audio_languages");
                var maxCfScore = GetInt(cfg, "max_custom_format_score");
                var skippedHardlinks = 0;
                var protectedAudioOrScore = 0;

                foreach (var it in incoming)
                {
                    var ok = true;
                    if (sizeGtGb is > 0 && it.SizeBytes < sizeGtGb.Value * 1024.0 * 1024.0 * 1024.0) ok = false;
                    if (sizeLtGb is > 0 && it.SizeBytes > sizeLtGb.Value * 1024.0 * 1024.0 * 1024.0) ok = false;
                    if (!string.IsNullOrWhiteSpace(res) && !string.Equals(it.Resolution, res, StringComparison.OrdinalIgnoreCase)) ok = false;
                    if (resolutions.Count > 0 && (string.IsNullOrWhiteSpace(it.Resolution) || !resolutions.Contains(it.Resolution, StringComparer.OrdinalIgnoreCase))) ok = false;
                    if (codecs.Count > 0 && (string.IsNullOrWhiteSpace(it.VideoCodec) || !codecs.Any(c => it.VideoCodec.Contains(c, StringComparison.OrdinalIgnoreCase)))) ok = false;
                    if (ok && excludeDv5 && IsDolbyVisionProfile5(it)) ok = false;
                    if (ok && minBitrateMbps is > 0)
                    {
                        var assumedSeconds = it.MediaType == "episode" ? 2700.0 : it.MediaType == "season" ? 27000.0 : 6600.0;
                        var estMbps = (it.SizeBytes * 8.0) / (assumedSeconds * 1_000_000.0);
                        if (estMbps < minBitrateMbps.Value) ok = false;
                    }
                    if (ratingLt is > 0 && (!it.Rating.HasValue || it.Rating.Value >= ratingLt.Value)) ok = false;
                    if (yearLt is > 0 && (!it.Year.HasValue || it.Year.Value >= yearLt.Value)) ok = false;
                    if (!string.IsNullOrWhiteSpace(genre) && !it.Genres.Any(g => g.Contains(genre, StringComparison.OrdinalIgnoreCase))) ok = false;
                    if (genreAny.Count > 0 && !it.Genres.Any(g => genreAny.Any(want => g.Contains(want, StringComparison.OrdinalIgnoreCase)))) ok = false;
                    if (ok && skipHardlinked && it.IsHardlinked)
                    {
                        ok = false;
                        skippedHardlinks++;
                    }
                    if (ok && protectDualAudio && ((it.AudioLanguages?.Count ?? 0) >= 2 || it.Tags.Any(t => t.Contains("german dl", StringComparison.OrdinalIgnoreCase) || t.Contains("dual", StringComparison.OrdinalIgnoreCase))))
                    {
                        ok = false;
                        protectedAudioOrScore++;
                    }
                    if (ok && protectAudioLangs.Count > 0 && (it.AudioLanguages ?? []).Any(al => protectAudioLangs.Contains(al, StringComparer.OrdinalIgnoreCase)))
                    {
                        ok = false;
                        protectedAudioOrScore++;
                    }
                    if (ok && maxCfScore.HasValue && it.CustomFormatScore > maxCfScore.Value)
                    {
                        ok = false;
                        protectedAudioOrScore++;
                    }
                    if (ok) output.Add(it);
                }
                nodeLogs.Add($"{ntype} matched {output.Count}/{incoming.Count} items (skipped hardlinks: {skippedHardlinks}, protected audio/CF: {protectedAudioOrScore})");
            }
            else if (ntype == "filter.tags_labels")
            {
                var hasTags = GetStringList(cfg, "has_tags");
                if (hasTags.Count == 0) hasTags = GetStringList(cfg, "include_tags");
                var excludeTags = GetStringList(cfg, "exclude_tags");
                var genreAny = GetStringList(cfg, "genre_any");
                if (genreAny.Count == 0) genreAny = GetStringList(cfg, "genres_any");
                var classFilter = GetString(cfg, "classification")?.Trim();

                foreach (var it in incoming)
                {
                    var ok = true;
                    if (hasTags.Count > 0 && !hasTags.Any(t => it.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))) ok = false;
                    if (ok && excludeTags.Count > 0 && excludeTags.Any(t => it.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))) ok = false;
                    if (ok && genreAny.Count > 0 && !it.Genres.Any(g => genreAny.Any(want => g.Contains(want, StringComparison.OrdinalIgnoreCase)))) ok = false;
                    if (ok && !string.IsNullOrWhiteSpace(classFilter) && !string.Equals(it.ClassificationKind, classFilter, StringComparison.OrdinalIgnoreCase)) ok = false;
                    if (ok) output.Add(it);
                }
                nodeLogs.Add($"Tags/Labels filter matched {output.Count}/{incoming.Count} items");
            }
            else if (ntype is "limit.reclaim_budget" or "filter.reclaim_budget" or "filter.limit")
            {
                var maxReclaimGb = GetDouble(cfg, "max_reclaim_gb");
                var targetFreeGb = GetDouble(cfg, "target_free_gb") ?? GetDouble(cfg, "target_free_space_gb_watermark");
                var minFreeGb = GetDouble(cfg, "min_free_gb") ?? GetDouble(cfg, "min_free_space_gb_trigger");
                var maxItems = GetInt(cfg, "max_items") ?? GetInt(cfg, "limit");
                var sortBy = (GetString(cfg, "sort_by") ?? activeStorageSortBy ?? "oldest_watched").ToLowerInvariant();
                var (actualFreeGb, _) = InspectDiskSpace(simulatedFreeGb, simulatedFreePct, libraries);

                if (minFreeGb is > 0 && actualFreeGb >= minFreeGb.Value)
                {
                    haltedByTrigger[nid] = true;
                    output = [];
                    nodeLogs.Add($"Disk free space ({actualFreeGb:F1} GB) >= trigger ({minFreeGb.Value:F1} GB) — budget limit halted downstream.");
                }
                else
                {
                    long? budgetBytes = maxReclaimGb is > 0 ? (long)(maxReclaimGb.Value * 1024.0 * 1024.0 * 1024.0) : activeStorageDeficitBytes;
                    if (targetFreeGb is > 0)
                    {
                        var deficitGb = Math.Max(0.0, targetFreeGb.Value - actualFreeGb);
                        var targetBudgetBytes = (long)(deficitGb * 1024.0 * 1024.0 * 1024.0);
                        budgetBytes = budgetBytes.HasValue ? Math.Min(budgetBytes.Value, targetBudgetBytes) : targetBudgetBytes;
                    }

                    output = ApplyReclaimBudgetAndOrdering(incoming, budgetBytes, sortBy, out var cappedBytes);
                    if (maxItems is > 0 && output.Count > maxItems.Value)
                    {
                        output = output.Take(maxItems.Value).ToList();
                        cappedBytes = output.Sum(x => x.IsHardlinked ? 0L : x.SizeBytes);
                    }

                    nodeLogs.Add(budgetBytes.HasValue || maxItems.HasValue
                        ? $"Limit/Budget ({sortBy}) selected {output.Count}/{incoming.Count} items ({cappedBytes / (1024.0 * 1024.0 * 1024.0):F1} GB)"
                        : $"Reclaim ordering ({sortBy}) ordered {output.Count} items");
                }
                activeStorageDeficitBytes = null;
            }
            else if (ntype == "filter.arr_seerr")
            {
                var hasTags = GetStringList(cfg, "has_tags");
                if (hasTags.Count == 0) hasTags = GetStringList(cfg, "include_tags");
                var excludeTags = GetStringList(cfg, "exclude_tags");
                var monitored = GetBool(cfg, "monitored");
                var monitoredOnly = GetBool(cfg, "monitored_only");
                var reqBy = (GetString(cfg, "requested_by") ?? GetString(cfg, "seerr_requested_by"))?.Trim();
                var reqOlderDays = GetInt(cfg, "request_older_than_days") ?? GetInt(cfg, "seerr_requested_older_than_days");
                var isRequested = GetBool(cfg, "is_requested");
                var skipHardlinked = GetBool(cfg, "skip_hardlinked_seeding") ?? false;

                foreach (var it in incoming)
                {
                    var ok = true;
                    if (hasTags.Count > 0 && !hasTags.Any(t => it.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))) ok = false;
                    if (excludeTags.Count > 0 && excludeTags.Any(t => it.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))) ok = false;
                    if (monitored.HasValue && it.Monitored != monitored.Value) ok = false;
                    if (monitoredOnly == true && !it.Monitored) ok = false;
                    if (isRequested.HasValue && (it.SeerrId.HasValue || !string.IsNullOrWhiteSpace(it.SeerrRequestedBy)) != isRequested.Value) ok = false;
                    if (!string.IsNullOrWhiteSpace(reqBy) && !string.Equals(it.SeerrRequestedBy, reqBy, StringComparison.OrdinalIgnoreCase)) ok = false;
                    if (reqOlderDays is > 0)
                    {
                        var ra = ParseDto(it.SeerrRequestedAt);
                        if (!ra.HasValue || ra.Value > startedAt.AddDays(-reqOlderDays.Value)) ok = false;
                    }
                    if (ok && skipHardlinked && it.IsHardlinked) ok = false;
                    if (ok) output.Add(it);
                }
                nodeLogs.Add($"Catalog/Request filter matched {output.Count}/{incoming.Count} items");
            }
            else if (ntype == "filter.exclusion_check")
            {
                var respectGlobalExclusions = GetBool(cfg, "respect_global_exclusions") ?? true;
                var extraTags = GetStringList(cfg, "excluded_tags");
                var extraUsers = GetStringList(cfg, "excluded_users");
                var protectedCount = 0;

                foreach (var it in incoming)
                {
                    var (isExcl, reason) = CheckItemExcluded(
                        it,
                        rules,
                        itemsByRk,
                        workflowId,
                        extraTags,
                        extraUsers,
                        startedAt,
                        respectGlobalExclusions);
                    if (isExcl)
                    {
                        protectedCount++;
                        nodeLogs.Add($"Protected '{it.Title}': {reason}");
                    }
                    else
                    {
                        output.Add(it);
                    }
                }
                nodeLogs.Add($"Exclusion check passed {output.Count} items ({protectedCount} protected)");
            }
            else if (ntype is "logic.combinator" or "logic.and" or "logic.or" or "logic.not")
            {
                var op = ntype switch
                {
                    "logic.and" => "AND",
                    "logic.or" => "OR",
                    "logic.not" => "NOT",
                    _ => (GetString(cfg, "operator") ?? "AND").ToUpperInvariant(),
                };

                if (parentStreams.Count == 0)
                {
                    output = [];
                }
                else if (op is "AND" or "INTERSECT")
                {
                    var common = parentStreams[0].Select(x => x.Id).ToHashSet();
                    foreach (var s in parentStreams.Skip(1)) common.IntersectWith(s.Select(x => x.Id));
                    output = parentStreams[0].Where(x => common.Contains(x.Id)).ToList();
                }
                else if (op is "NOT" or "DIFFERENCE")
                {
                    if (parentStreams.Count >= 2)
                    {
                        var subtract = parentStreams.Skip(1).SelectMany(s => s.Select(x => x.Id)).ToHashSet();
                        output = parentStreams[0].Where(x => !subtract.Contains(x.Id)).ToList();
                    }
                    else
                    {
                        var subtract = parentStreams[0].Select(x => x.Id).ToHashSet();
                        output = allMedia.Where(x => !subtract.Contains(x.Id)).ToList();
                    }
                }
                else
                {
                    output = incoming;
                }
                nodeLogs.Add($"Combinator ({op}) produced {output.Count} items");
            }
            else if (ntype == "queue.grace_period")
            {
                var graceDays = GetInt(cfg, "grace_days") ?? generalSettings.DefaultGraceDays;
                var autoRescueOnWatch = GetBool(cfg, "auto_rescue_on_watch") ?? true;
                var stageLabel = "Leaving Soon";
                var downstreamActions = CollectDownstreamActions(nid);
                output = activeStorageDeficitBytes.HasValue
                    ? ApplyReclaimBudgetAndOrdering(incoming, activeStorageDeficitBytes, activeStorageSortBy, out _)
                    : incoming.ToList();
                activeStorageDeficitBytes = null;
                stagedViaGrace[nid] = true;

                if (dryRun)
                {
                    totalStaged += output.Count;
                    totalSpaceBytes += output.Sum(x => x.SizeBytes);
                    totalReclaimableBytes += output.Where(x => !x.IsHardlinked).Sum(x => x.SizeBytes);
                    totalHardlinkedCount += output.Count(x => x.IsHardlinked);
                    nodeLogs.Add($"[Dry-Run] Would stage {output.Count} items for {graceDays} days in Deletion Queue");
                }
                else
                {
                    var deleteAt = startedAt.AddDays(graceDays).ToString("O");
                    var newlyStaged = new List<CleanerrMediaItem>();
                    var actionCfg = new JsonObject
                    {
                        ["auto_rescue_on_watch"] = autoRescueOnWatch,
                        ["actions"] = new JsonArray(downstreamActions.Select(a => (JsonNode)a).ToArray()),
                    }.ToJsonString();
                    var initLogs = new JsonArray
                    {
                        new JsonObject
                        {
                            ["timestamp"] = startedAt.ToString("O"),
                            ["event"] = "staged",
                            ["message"] = $"Staged by '{workflowName}' (grace={graceDays}d)",
                        },
                    }.ToJsonString();

                    await dbFactory.ExecuteWriteAsync(
                        DatabaseKind.Core,
                        async (conn, tx) =>
                        {
                            foreach (var it in output)
                            {
                                var already = await conn.ExecuteScalarAsync<int>(
                                    "SELECT COUNT(1) FROM cleanerr_deletion_queue WHERE media_item_id = @Mid AND status = 'staged';",
                                    new { Mid = it.Id },
                                    transaction: tx);
                                if (already > 0) continue;

                                var gran = it.MediaType == "show" ? "series" : it.MediaType;
                                var itemReason = matchedReasonsById.GetValueOrDefault(it.Id) ?? $"Matched workflow '{workflowName}'";
                                await conn.ExecuteAsync(
                                    """
                                    INSERT INTO cleanerr_deletion_queue (
                                        media_item_id, workflow_id, granularity, staged_at, delete_at,
                                        grace_days, status, plex_collection_name, action_config_json, logs_json,
                                        is_manual, postponed_count, matched_reason
                                    ) VALUES (
                                        @Mid, @WfId, @Gran, @StagedAt, @DeleteAt,
                                        @Grace, 'staged', NULL, @ActionCfg, @Logs,
                                        0, 0, @MatchedReason
                                    );
                                    """,
                                    new
                                    {
                                        Mid = it.Id,
                                        WfId = workflowId,
                                        Gran = gran,
                                        StagedAt = startedAt.ToString("O"),
                                        DeleteAt = deleteAt,
                                        Grace = graceDays,
                                        ActionCfg = actionCfg,
                                        Logs = initLogs,
                                        MatchedReason = itemReason,
                                    },
                                    transaction: tx);
                                newlyStaged.Add(it);
                            }
                        },
                        ct);

                    totalStaged += newlyStaged.Count;
                    totalSpaceBytes += newlyStaged.Sum(x => x.SizeBytes);
                    totalReclaimableBytes += newlyStaged.Where(x => !x.IsHardlinked).Sum(x => x.SizeBytes);
                    totalHardlinkedCount += newlyStaged.Count(x => x.IsHardlinked);
                    nodeLogs.Add($"Staged {newlyStaged.Count} new items in Deletion DB (grace={graceDays}d)");

                    if (newlyStaged.Count > 0)
                    {
                        var whOverride = GetString(cfg, "discord_webhook_url");
                        var discordCfg = await discordService.GetSettingsAsync(ct);
                        if (discordCfg.NotifyOnStaged || !string.IsNullOrWhiteSpace(whOverride))
                        {
                            var dRes = await discordService.SendStagedNotificationAsync(
                                newlyStaged,
                                graceDays,
                                stageLabel,
                                workflowId,
                                workflowName,
                                whOverride,
                                ct);
                            if (dRes["sent"]?.GetValue<bool>() == true)
                            {
                                nodeLogs.Add($"Sent Discord notification ({dRes["delivery_mode"]?.GetValue<string>()}) for {newlyStaged.Count} staged items");
                            }
                        }
                    }
                }
            }
            else if (ntype == "queue.plex_collection")
            {
                output = incoming.ToList();
                stagedViaGrace[nid] = isBehindGrace;
                nodeLogs.Add("Skipped queue.plex_collection: Plex collections are disabled; Cleanerr uses Managerr's native Leaving Soon queue.");
            }
            else if (ntype == "notify.discord")
            {
                output = incoming.ToList();
                stagedViaGrace[nid] = isBehindGrace;
                var graceDays = GetInt(cfg, "grace_days") ?? generalSettings.DefaultGraceDays;
                var colName = "Leaving Soon";
                var whOverride = GetString(cfg, "discord_webhook_url") ?? GetString(cfg, "webhook_url");

                if (dryRun)
                {
                    nodeLogs.Add($"[Dry-Run] Would send Discord notification for {output.Count} item(s)");
                }
                else if (output.Count > 0)
                {
                    try
                    {
                        var dRes = await discordService.SendStagedNotificationAsync(
                            output,
                            graceDays,
                            colName,
                            workflowId,
                            workflowName,
                            whOverride,
                            ct);
                        nodeLogs.Add(dRes["sent"]?.GetValue<bool>() == true
                            ? $"Sent Discord notification for {output.Count} item(s)"
                            : $"Discord notification skipped ({dRes["reason"]?.GetValue<string>() ?? "not configured"})");
                    }
                    catch (Exception ex)
                    {
                        nodeLogs.Add($"Discord notification failed (non-fatal): {ex.Message}");
                    }
                }
            }
            else if (ntype == "notify.webhook")
            {
                output = incoming.ToList();
                stagedViaGrace[nid] = isBehindGrace;
                var url = GetString(cfg, "url");
                if (dryRun)
                {
                    nodeLogs.Add($"[Dry-Run] Would POST webhook for {output.Count} item(s)");
                }
                else if (output.Count > 0 && !string.IsNullOrWhiteSpace(url) &&
                         (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                          url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    var body = JsonSerializer.Serialize(new
                    {
                        @event = "cleanerr_workflow_match",
                        workflow_id = workflowId,
                        workflow_name = workflowName,
                        item_count = output.Count,
                        total_bytes = output.Sum(x => x.SizeBytes),
                        items = output.Take(50).Select(x => new { x.Id, x.Title, x.MediaType, x.SizeBytes }),
                    }, JsonOpts);
                    try
                    {
                        await httpGateway.SendJsonAsync(HttpMethod.Post, url, body, null, ct);
                        nodeLogs.Add($"Dispatched outbound webhook for {output.Count} item(s)");
                    }
                    catch (Exception ex)
                    {
                        nodeLogs.Add($"Outbound webhook failed (non-fatal): {ex.Message}");
                    }
                }
            }
            else if (ntype.StartsWith("action.", StringComparison.Ordinal))
            {
                output = (!isBehindGrace && activeStorageDeficitBytes.HasValue)
                    ? ApplyReclaimBudgetAndOrdering(incoming, activeStorageDeficitBytes, activeStorageSortBy, out _)
                    : incoming.ToList();
                if (!isBehindGrace) activeStorageDeficitBytes = null;
                stagedViaGrace[nid] = isBehindGrace;
                if (isBehindGrace)
                {
                    nodeLogs.Add($"Deferred {ntype} for {output.Count} staged items until grace period expires");
                }
                else if (dryRun)
                {
                    var mode = (GetString(cfg, "mode") ?? "").ToLowerInvariant();
                    var isDeleting = IsDeletingActionNode(ntype, cfg);
                    if (mode == "downgrade_profile")
                    {
                        var (resolvedPid, skipMsg) = await ResolveAndValidateDowngradeProfileAsync(cfg, ct);
                        if (!resolvedPid.HasValue && skipMsg is not null)
                        {
                            nodeLogs.Add(skipMsg);
                        }
                        else
                        {
                            totalDowngraded += output.Count;
                        }
                    }

                    if (isDeleting)
                    {
                        totalDeleted += output.Count;
                    }
                    totalSpaceBytes += output.Sum(x => x.SizeBytes);
                    totalReclaimableBytes += output.Where(x => !x.IsHardlinked).Sum(x => x.SizeBytes);
                    totalHardlinkedCount += output.Count(x => x.IsHardlinked);
                    nodeLogs.Add($"[Dry-Run] Would execute {ntype} on {output.Count} items (downgraded: {totalDowngraded})");
                }
                else
                {
                    var nodeJson = new JsonObject { ["node_type"] = ntype, ["config"] = cfg.DeepClone() };
                    var mode = (GetString(cfg, "mode") ?? "").ToLowerInvariant();
                    var isDeleting = IsDeletingActionNode(ntype, cfg);
                    foreach (var it in output)
                    {
                        var actionLogs = await ExecuteActionsForItemAsync(it, [nodeJson], ct);
                        foreach (var al in actionLogs)
                        {
                            nodeLogs.Add(al);
                        }

                        if (mode == "downgrade_profile" && !actionLogs.Any(l => l.StartsWith("Skipped downgrade_profile", StringComparison.OrdinalIgnoreCase)))
                        {
                            totalDowngraded++;
                        }

                        if (isDeleting)
                        {
                            totalDeleted++;
                        }
                        totalSpaceBytes += it.SizeBytes;
                        if (!it.IsHardlinked) totalReclaimableBytes += it.SizeBytes;
                        else totalHardlinkedCount++;
                    }
                    nodeLogs.Add($"Executed {ntype} on {output.Count} items immediately (downgraded: {totalDowngraded})");
                }
            }
            else
            {
                output = incoming.ToList();
                nodeLogs.Add($"Passthrough node '{ntype}' ({output.Count} items)");
            }

            nodeOutputs[nid] = output;
            foreach (var lg in nodeLogs) runLogs.Add($"[{node.Label}] {lg}");

            nodeMetrics[nid] = new CleanerrNodeExecutionMetric(
                NodeId: nid,
                Label: node.Label,
                NodeType: ntype,
                InputCount: incoming.Count,
                OutputCount: output.Count,
                SizeBytes: output.Sum(x => x.SizeBytes),
                MatchedItemsPreview: output.Take(250).Select(it =>
                {
                    var rsn = matchedReasonsById.GetValueOrDefault(it.Id);
                    return new CleanerrMatchedItemPreview(
                        it.Id, it.Title, it.MediaType, it.Year, it.SizeBytes, it.Resolution, it.LastViewedAt,
                        it.IsHardlinked, it.AudioLanguages, it.ViewOffsetMs, it.CustomFormatScore,
                        it.ClassificationKind, rsn, it.ParentTitle ?? it.GrandparentTitle, rsn);
                }).ToList(),
                MatchedItemIds: output.Select(it => it.Id).ToList(),
                Logs: nodeLogs);
        }

        var terminalMatchedItems = nodeMetrics.Values
            .Where(m => m.NodeType == "queue.grace_period" || m.NodeType.StartsWith("action.", StringComparison.Ordinal))
            .SelectMany(m => m.MatchedItemsPreview)
            .GroupBy(m => m.Id)
            .Select(g => g.First())
            .ToList();
        if (terminalMatchedItems.Count == 0)
        {
            terminalMatchedItems = nodeMetrics.Values
                .Where(m => adj.TryGetValue(m.NodeId, out var children) && children.Count == 0 && !m.NodeType.StartsWith("trigger.", StringComparison.Ordinal))
                .SelectMany(m => m.MatchedItemsPreview)
                .GroupBy(m => m.Id)
                .Select(g => g.First())
                .ToList();
        }

        var finishedAt = DateTimeOffset.UtcNow;
        var summary = new CleanerrWorkflowRunSummary(
            NodeMetrics: nodeMetrics,
            StagedCount: totalStaged,
            DeletedCount: totalDeleted,
            SpaceBytes: totalSpaceBytes,
            Logs: runLogs,
            DryRun: dryRun,
            TriggerType: triggerType,
            ReclaimableSpaceBytes: totalReclaimableBytes,
            HardlinkedCount: totalHardlinkedCount,
            DowngradedCount: totalDowngraded,
            MatchedItems: terminalMatchedItems);

        long? runId = null;
        if (recordRun)
        {
            var summaryStr = JsonSerializer.Serialize(summary, JsonOpts);
            runId = await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                async (conn, tx) =>
                {
                    var rid = await conn.ExecuteScalarAsync<long>(
                        """
                        INSERT INTO cleanerr_runs (
                            workflow_id, trigger_type, dry_run, status, started_at, finished_at, summary_json
                        ) VALUES (
                            @WfId, @Trig, @DryRun, 'completed', @Start, @Fin, @Sum
                        );
                        SELECT last_insert_rowid();
                        """,
                        new
                        {
                            WfId = workflowId,
                            Trig = triggerType,
                            DryRun = dryRun ? 1 : 0,
                            Start = startedAt.ToString("O"),
                            Fin = finishedAt.ToString("O"),
                            Sum = summaryStr,
                        },
                        transaction: tx);

                    if (workflowId.HasValue)
                    {
                        await conn.ExecuteAsync(
                            """
                            UPDATE cleanerr_workflows
                            SET last_run_at = @Fin,
                                last_run_status = @Status
                            WHERE id = @WfId;
                            """,
                            new
                            {
                                WfId = workflowId.Value,
                                Fin = finishedAt.ToString("O"),
                                Status = dryRun ? "dry_run_ok" : "completed",
                            },
                            transaction: tx);
                    }
                    return rid;
                },
                ct);
        }

        return new CleanerrWorkflowRunResult(
            RunId: runId,
            WorkflowId: workflowId,
            TriggerType: triggerType,
            DryRun: dryRun,
            Status: "completed",
            StartedAt: startedAt.ToString("O"),
            FinishedAt: finishedAt.ToString("O"),
            Summary: summary);
    }

    public async Task<CleanerrTestMediaResult> TestMediaAsync(
        TestCleanerrMediaRequest request,
        CancellationToken ct = default)
    {
        await SyncCatalogItemsIntoCleanerrAsync(ct);
        var allMedia = await ListMediaItemsAsync(ct);
        var target = allMedia.FirstOrDefault(m => m.Id == request.MediaItemId)
            ?? throw new KeyNotFoundException($"Cleanerr media item #{request.MediaItemId} not found.");

        var now = DateTimeOffset.UtcNow;
        var libraries = await catalogService.ListLibrariesAsync(null, ct);
        var (freeGb, freePct) = InspectDiskSpace(request.SimulatedFreeSpaceGb, request.SimulatedFreeSpacePercent, libraries);
        var evalCtx = CleanerrMaintainerrRuleEngine.BuildContext(allMedia, now, freeGb, freePct);
        var itemsByRk = evalCtx.MediaByRatingKey;
        var rules = await ListExclusionRulesAsync(ct);
        var stagedQueue = await ListDeletionQueueAsync("staged", ct);
        var stagedRow = stagedQueue.FirstOrDefault(q => q.MediaItemId == target.Id);

        CleanerrRuleGroupSpec? effectiveRuleGroup = request.RuleGroup;
        CleanerrWorkflowSummary? wf = null;
        if (request.WorkflowId.HasValue)
        {
            wf = await GetWorkflowByIdAsync(request.WorkflowId.Value, ct);
            effectiveRuleGroup ??= wf?.RuleGroup;
        }

        if (effectiveRuleGroup is null && request.GraphJson is null && wf is null)
        {
            var allWfs = await ListWorkflowsAsync(ct);
            var enabledRuleWf = allWfs.FirstOrDefault(w => w.Enabled && w.RuleGroup is not null)
                                ?? allWfs.FirstOrDefault(w => w.RuleGroup is not null);
            if (enabledRuleWf is not null)
            {
                wf = enabledRuleWf;
                effectiveRuleGroup = enabledRuleWf.RuleGroup;
            }
        }

        var (isExcl, exclReason) = CheckItemExcluded(target, rules, itemsByRk, wf?.Id, null, null, now);

        bool overallMatched;
        string matchedReason;
        IReadOnlyList<CleanerrSectionEvaluationTrace> sectionTraces;
        IReadOnlyList<string> guardNotes;

        if (effectiveRuleGroup is not null)
        {
            (overallMatched, matchedReason, sectionTraces, guardNotes) =
                CleanerrMaintainerrRuleEngine.EvaluateItemAgainstRuleGroup(target, effectiveRuleGroup, evalCtx);
        }
        else if (request.GraphJson is not null || wf is not null)
        {
            var graphToTest = request.GraphJson ?? wf!.GraphJson;
            var wfTitle = wf?.Name ?? "Ad-Hoc DAG";
            var sim = await RunWorkflowGraphCoreAsync(
                graphToTest,
                wf?.Id,
                wfTitle,
                dryRun: true,
                triggerType: "test_media",
                recordRun: false,
                simulatedFreeGb: request.SimulatedFreeSpaceGb,
                simulatedFreePct: request.SimulatedFreeSpacePercent,
                ruleGroupOverride: null,
                ct: ct);
            var terminalHit = sim.Summary.NodeMetrics.Values
                .Where(m => m.NodeType == "queue.grace_period" || m.NodeType.StartsWith("action.", StringComparison.Ordinal))
                .Any(m => m.MatchedItemIds.Contains(target.Id));
            overallMatched = terminalHit;
            matchedReason = terminalHit
                ? $"Matched DAG workflow '{wfTitle}'"
                : $"Did not pass DAG filters in '{wfTitle}'";
            sectionTraces = [];
            guardNotes = sim.Summary.Logs.Take(6).ToList();
        }
        else
        {
            var defaultSpec = new CleanerrRuleGroupSpec(
                TargetMediaType: target.MediaType,
                LibraryIds: null,
                GraceDays: 7,
                ActionMode: "delete_files_and_unmonitor",
                Sections:
                [
                    new CleanerrRuleSectionDto(
                        SectionOperator: "AND",
                        Title: "Default Inactivity Diagnostic (> 365d)",
                        Rules:
                        [
                            new(null, "plex.daysSinceLastActivity", "BIGGER", "365"),
                            new("AND", "plex.isInProgress", "EQUALS", "false"),
                            new("AND", "file.isHardlinked", "EQUALS", "false"),
                        ]),
                ]);
            (overallMatched, matchedReason, sectionTraces, guardNotes) =
                CleanerrMaintainerrRuleEngine.EvaluateItemAgainstRuleGroup(target, defaultSpec, evalCtx);
        }

        if (isExcl)
        {
            overallMatched = false;
            matchedReason = exclReason ?? "Protected by active Cleanerr exclusion rule";
        }

        var displayTitle = !string.IsNullOrWhiteSpace(target.ParentTitle) && target.MediaType is "season" or "episode"
            ? $"{target.ParentTitle} — {target.Title}"
            : target.Year.HasValue
                ? $"{target.Title} ({target.Year.Value})"
                : target.Title;

        return new CleanerrTestMediaResult(
            MediaItemId: target.Id,
            Title: displayTitle,
            MediaType: target.MediaType,
            ClassificationKind: target.ClassificationKind,
            WouldMatch: overallMatched,
            IsExcluded: isExcl,
            ExclusionReason: exclReason,
            IsCurrentlyStaged: stagedRow is not null,
            StagedDeleteAt: stagedRow?.DeleteAt,
            Sections: sectionTraces,
            GuardNotes: guardNotes,
            Summary: stagedRow?.MatchedReason ?? matchedReason);
    }

    public async Task<ExportCleanerrRuleYamlResponse> ExportWorkflowYamlAsync(
        long workflowId,
        CancellationToken ct = default)
    {
        var wf = await GetWorkflowByIdAsync(workflowId, ct)
            ?? throw new KeyNotFoundException($"Cleanerr workflow #{workflowId} not found.");
        var yaml = CleanerrMaintainerrRuleEngine.ExportToYaml(
            wf.Name,
            wf.Description,
            wf.ScheduleCron,
            wf.EditorMode,
            wf.RuleGroup,
            wf.GraphJson);
        return new ExportCleanerrRuleYamlResponse(wf.Id, wf.Name, yaml);
    }

    public async Task<CleanerrWorkflowSummary> ImportWorkflowYamlAsync(
        ImportCleanerrRuleYamlRequest request,
        CancellationToken ct = default)
    {
        var (name, desc, cron, editorMode, spec, graphJson) = CleanerrMaintainerrRuleEngine.ImportFromYaml(request.Yaml);
        return await UpsertWorkflowAsync(
            new UpsertCleanerrWorkflowRequest(
                Name: name,
                Description: desc,
                Enabled: request.Enabled ?? true,
                DryRun: request.DryRun ?? true,
                ScheduleCron: cron,
                GraphJson: graphJson,
                EditorMode: editorMode,
                RuleGroup: spec),
            null,
            ct);
    }

    private sealed record ParsedGraphNode(string Id, string Label, string NodeType, JsonObject Config);

    private static bool IsDeletingActionNode(string ntype, JsonObject cfg)
    {
        if (ntype == "action.delete_files")
        {
            return true;
        }

        if (ntype is "action.managerr" or "action.radarr" or "action.sonarr")
        {
            var mode = (GetString(cfg, "mode") ?? "delete_files_and_unmonitor").ToLowerInvariant();
            return mode is "delete_files_and_unmonitor"
                or "unmonitor_and_delete_existing"
                or "delete_files_only"
                or "delete_work_and_files"
                or "delete_show_if_empty"
                or "unmonitor_show_if_empty";
        }

        if (ntype == "action.plex")
        {
            var mode = (GetString(cfg, "mode") ?? GetString(cfg, "action") ?? "refresh_metadata").ToLowerInvariant();
            return mode is "delete_from_plex" or "remove_media";
        }

        return false;
    }

    private static bool IsDolbyVisionProfile5(CleanerrMediaItem it)
    {
        static bool HasDv5Token(string? s) =>
            !string.IsNullOrWhiteSpace(s) &&
            (s.Contains("dv5", StringComparison.OrdinalIgnoreCase) ||
             s.Contains("dvhe.05", StringComparison.OrdinalIgnoreCase) ||
             s.Contains("dvh1.05", StringComparison.OrdinalIgnoreCase) ||
             s.Contains("profile 5", StringComparison.OrdinalIgnoreCase) ||
             s.Contains("profile5", StringComparison.OrdinalIgnoreCase));

        return HasDv5Token(it.VideoCodec) ||
               HasDv5Token(it.FilePath) ||
               HasDv5Token(it.Title) ||
               it.Tags.Any(HasDv5Token);
    }

    private static List<CleanerrMediaItem> ApplyReclaimBudgetAndOrdering(
        IReadOnlyList<CleanerrMediaItem> items,
        long? budgetBytes,
        string? sortBy,
        out long accumulatedBytes)
    {
        IEnumerable<CleanerrMediaItem> ordered = (sortBy ?? "oldest_watched").ToLowerInvariant() switch
        {
            "largest_first" or "size_desc" => items
                .OrderByDescending(i => i.SizeBytes)
                .ThenBy(i => ParseDto(i.LastViewedAt) ?? DateTimeOffset.MinValue),
            "lowest_rating" or "rating_asc" => items
                .OrderBy(i => i.Rating ?? 0.0)
                .ThenByDescending(i => i.SizeBytes),
            "largest_gb_per_play" => items
                .OrderByDescending(i => (double)i.SizeBytes / Math.Max(1, i.ViewCount))
                .ThenBy(i => ParseDto(i.LastViewedAt) ?? DateTimeOffset.MinValue),
            _ => items
                .OrderBy(i => ParseDto(i.LastViewedAt) ?? ParseDto(i.AddedAt) ?? DateTimeOffset.MinValue)
                .ThenByDescending(i => i.SizeBytes),
        };

        var result = new List<CleanerrMediaItem>();
        accumulatedBytes = 0L;
        foreach (var it in ordered)
        {
            if (budgetBytes is > 0 && accumulatedBytes >= budgetBytes.Value && result.Count > 0)
            {
                break;
            }
            result.Add(it);
            var effectiveBytes = it.IsHardlinked ? 0L : it.SizeBytes;
            accumulatedBytes += effectiveBytes > 0 ? effectiveBytes : it.SizeBytes;
        }

        return result;
    }

    // A delete fed only by sources, granularity and exclusions applies to a whole media type
    private static IEnumerable<string> FindUnscopedDeleteActions(IEnumerable<ParsedGraphNode> nodes)
    {
        var all = nodes.ToList();
        var isScoped = all.Any(n =>
            (n.NodeType.StartsWith("filter.", StringComparison.Ordinal) && n.NodeType != "filter.exclusion_check") ||
            n.NodeType is "logic.rule_group" or "trigger.storage_pressure" ||
            (n.NodeType.StartsWith("source.", StringComparison.Ordinal) &&
             (GetLongList(n.Config, "library_ids").Count > 0 || GetLongList(n.Config, "libraryIds").Count > 0)));
        if (isScoped)
        {
            return [];
        }

        return all
            .Where(n => n.NodeType.StartsWith("action.", StringComparison.Ordinal) &&
                        (n.NodeType == "action.delete_files" ||
                         (GetString(n.Config, "mode") ?? "").Contains("delete", StringComparison.OrdinalIgnoreCase)))
            .Select(n => $"Warning: '{n.Label}' deletes media, but nothing in this workflow narrows what it applies to " +
                         "(only sources, granularity and exclusion checks). Add a filter or a library selection.")
            .ToList();
    }

    private static string? NormalizeSourceMediaType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "season" or "seasons" or "episode" or "episodes" or "series" or "tv" or "shows" or "show" => "show",
            "movies" or "movie" => "movie",
            var t => t,
        };
    }
}
