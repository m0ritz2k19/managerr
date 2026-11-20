using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Managerr.Catalog;
using Managerr.Datastore;

namespace Managerr.Cleanerr;

public sealed partial class CleanerrLifecycleEngine
{
    public async Task<IReadOnlyList<CleanerrDeletionQueueItem>> ListDeletionQueueAsync(
        string? statusFilter = null,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var sql = string.IsNullOrWhiteSpace(statusFilter)
            ? """
              SELECT q.*,
                     m.title AS media_title,
                     m.media_type,
                     m.size_bytes,
                     m.classification_kind,
                     m.poster_url,
                     m.parent_title,
                     m.grandparent_title,
                     m.year,
                     m.last_viewed_at,
                     m.view_count,
                     m.seerr_requested_by
              FROM cleanerr_deletion_queue q
              JOIN cleanerr_media_items m ON m.id = q.media_item_id
              ORDER BY q.id DESC;
              """
            : """
              SELECT q.*,
                     m.title AS media_title,
                     m.media_type,
                     m.size_bytes,
                     m.classification_kind,
                     m.poster_url,
                     m.parent_title,
                     m.grandparent_title,
                     m.year,
                     m.last_viewed_at,
                     m.view_count,
                     m.seerr_requested_by
              FROM cleanerr_deletion_queue q
              JOIN cleanerr_media_items m ON m.id = q.media_item_id
              WHERE q.status = @Status
              ORDER BY q.id DESC;
              """;
        var rows = await conn.QueryAsync<QueueRow>(sql, new { Status = statusFilter?.Trim().ToLowerInvariant() });
        return rows.Select(MapQueue).ToList();
    }

    public async Task<IReadOnlyList<CleanerrDeletionQueueItem>> ManualStageItemsAsync(
        ManualStageCleanerrItemsRequest request,
        string actorUsername = "admin",
        CancellationToken ct = default)
    {
        if (request.MediaItemIds is null || request.MediaItemIds.Count == 0)
        {
            return [];
        }

        await SyncCatalogItemsIntoCleanerrAsync(ct);
        var allMedia = await ListMediaItemsAsync(ct);
        var byId = allMedia.ToDictionary(m => m.Id);

        var now = DateTimeOffset.UtcNow;
        var graceDays = Math.Clamp(request.GraceDays, 0, 365);
        var deleteAt = now.AddDays(graceDays).ToString("O");
        var actionMode = string.IsNullOrWhiteSpace(request.ActionMode) ? "delete_files_and_unmonitor" : request.ActionMode.Trim();
        var colName = "Leaving Soon";
        var reason = string.IsNullOrWhiteSpace(request.Reason)
            ? $"Manually staged by {actorUsername}"
            : request.Reason.Trim();

        var actionCfg = new JsonObject
        {
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["node_type"] = "action.managerr",
                    ["config"] = new JsonObject
                    {
                        ["mode"] = actionMode,
                        ["clean_empty_folders"] = true,
                        ["reset_requests"] = true,
                    },
                },
            },
        }.ToJsonString();

        var initLogs = new JsonArray
        {
            new JsonObject
            {
                ["timestamp"] = now.ToString("O"),
                ["event"] = "manual_staged",
                ["actor"] = actorUsername,
                ["reason"] = reason,
            },
        }.ToJsonString();

        var newlyStagedMedia = new List<CleanerrMediaItem>();

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                foreach (var mid in request.MediaItemIds.Distinct())
                {
                    if (!byId.TryGetValue(mid, out var it)) continue;

                    var already = await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(1) FROM cleanerr_deletion_queue WHERE media_item_id = @Mid AND status = 'staged';",
                        new { Mid = it.Id },
                        transaction: tx);
                    if (already > 0) continue;

                    var gran = it.MediaType == "show" ? "series" : it.MediaType;
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO cleanerr_deletion_queue (
                            media_item_id, workflow_id, granularity, staged_at, delete_at,
                            grace_days, status, plex_collection_name, action_config_json, logs_json,
                            is_manual, postponed_count, matched_reason
                        ) VALUES (
                            @Mid, NULL, @Gran, @StagedAt, @DeleteAt,
                            @Grace, 'staged', NULL, @ActionCfg, @Logs,
                            1, 0, @MatchedReason
                        );
                        """,
                        new
                        {
                            Mid = it.Id,
                            Gran = gran,
                            StagedAt = now.ToString("O"),
                            DeleteAt = deleteAt,
                            Grace = graceDays,
                            ActionCfg = actionCfg,
                            Logs = initLogs,
                            MatchedReason = reason,
                        },
                        transaction: tx);
                    newlyStagedMedia.Add(it);
                }
            },
            ct);

        if (newlyStagedMedia.Count > 0)
        {
            var dSettings = await discordService.GetSettingsAsync(ct);
            if (dSettings.NotifyOnStaged)
            {
                try
                {
                    await discordService.SendStagedNotificationAsync(
                        newlyStagedMedia,
                        graceDays,
                        colName,
                        null,
                        $"Manual Stage ({actorUsername})",
                        null,
                        ct);
                }
                catch
                {
                    // Best-effort notification
                }
            }
        }

        var stagedSet = request.MediaItemIds.ToHashSet();
        var currentStaged = await ListDeletionQueueAsync("staged", ct);
        return currentStaged.Where(q => stagedSet.Contains(q.MediaItemId)).ToList();
    }

    public async Task<CleanerrDeletionQueueItem> PostponeQueueItemAsync(
        long queueId,
        int? days = null,
        string? reason = null,
        CancellationToken ct = default)
    {
        var rawDays = days ?? (await GetGeneralSettingsAsync(ct)).DefaultPostponeDays;
        var extendDays = Math.Clamp(rawDays, 1, 365);
        var all = await ListDeletionQueueAsync(null, ct);
        var qitem = all.FirstOrDefault(x => x.Id == queueId)
            ?? throw new KeyNotFoundException($"Queue item #{queueId} not found.");

        var now = DateTimeOffset.UtcNow;
        var currentDeleteAt = ParseDto(qitem.DeleteAt) ?? now;
        var baseAnchor = currentDeleteAt > now ? currentDeleteAt : now;
        var nextDeleteAt = baseAnchor.AddDays(extendDays);
        var note = reason ?? $"Postponed +{extendDays}d";

        var clonedLogs = qitem.LogsJson.DeepClone().AsArray();
        clonedLogs.Add(new JsonObject
        {
            ["timestamp"] = now.ToString("O"),
            ["event"] = "postponed",
            ["days"] = extendDays,
            ["new_delete_at"] = nextDeleteAt.ToString("O"),
            ["reason"] = note,
        });

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                UPDATE cleanerr_deletion_queue
                SET status = 'staged',
                    delete_at = @DeleteAt,
                    grace_days = grace_days + @Days,
                    postponed_count = postponed_count + 1,
                    final_warning_sent_at = NULL,
                    logs_json = @Logs
                WHERE id = @Id;
                """,
                new
                {
                    Id = queueId,
                    DeleteAt = nextDeleteAt.ToString("O"),
                    Days = extendDays,
                    Logs = clonedLogs.ToJsonString(),
                },
                transaction: tx),
            ct);

        return (await ListDeletionQueueAsync(null, ct)).First(x => x.Id == queueId);
    }

    public async Task<IReadOnlyList<CleanerrDeletionQueueItem>> PostponeMediaIdsWithFamilyAsync(
        IReadOnlyList<long> mediaIds,
        int days,
        string reason,
        CancellationToken ct = default)
    {
        if (mediaIds.Count == 0) return [];
        var allMedia = await ListMediaItemsAsync(ct);
        var byId = allMedia.ToDictionary(m => m.Id);
        var targetFamilyIds = new HashSet<long>();

        foreach (var mid in mediaIds)
        {
            if (byId.TryGetValue(mid, out var media))
            {
                var (_, fam) = ResolveMediaToSeriesFamily(allMedia, media);
                foreach (var fid in fam) targetFamilyIds.Add(fid);
            }
            else
            {
                targetFamilyIds.Add(mid);
            }
        }

        var staged = await ListDeletionQueueAsync("staged", ct);
        var updated = new List<CleanerrDeletionQueueItem>();
        foreach (var q in staged.Where(q => targetFamilyIds.Contains(q.MediaItemId)))
        {
            updated.Add(await PostponeQueueItemAsync(q.Id, days, reason, ct));
        }

        return updated;
    }

    public async Task<int> PostponeAllStagedAsync(
        long? workflowId,
        int days,
        string reason,
        CancellationToken ct = default)
    {
        var staged = await ListDeletionQueueAsync("staged", ct);
        if (workflowId is > 0)
        {
            staged = staged.Where(q => q.WorkflowId == workflowId.Value).ToList();
        }

        foreach (var q in staged)
        {
            await PostponeQueueItemAsync(q.Id, days, reason, ct);
        }

        return staged.Count;
    }

    public async Task<CleanerrDeletionQueueItem> DeleteQueueItemNowAsync(
        long queueId,
        string actorUsername = "admin",
        CancellationToken ct = default)
    {
        var all = await ListDeletionQueueAsync(null, ct);
        var qitem = all.FirstOrDefault(x => x.Id == queueId)
            ?? throw new KeyNotFoundException($"Queue item #{queueId} not found.");
        var media = (await ListMediaItemsAsync(ct)).FirstOrDefault(m => m.Id == qitem.MediaItemId)
            ?? throw new KeyNotFoundException($"Media item #{qitem.MediaItemId} not found.");

        var actionsArr = qitem.ActionConfigJson["actions"]?.AsArray()?.OfType<JsonObject>().ToList() ?? [];
        if (actionsArr.Count == 0)
        {
            actionsArr.Add(new JsonObject
            {
                ["node_type"] = "action.managerr",
                ["config"] = new JsonObject { ["mode"] = "delete_files_and_unmonitor", ["clean_empty_folders"] = true },
            });
        }

        var executedNow = DateTimeOffset.UtcNow;
        var actionLogs = await ExecuteActionsForItemAsync(media, actionsArr, ct);
        var reason = actionLogs.Count > 0
            ? $"Deleted immediately by {actorUsername}: {string.Join("; ", actionLogs)}"
            : $"Deleted immediately by {actorUsername}";

        await UpdateQueueStateAsync(
            qitem.Id,
            "deleted",
            reason,
            executedNow,
            qitem.LogsJson,
            "deleted_now",
            ct);

        return (await ListDeletionQueueAsync(null, ct)).FirstOrDefault(x => x.Id == queueId)
               ?? qitem with
               {
                   Status = "deleted",
                   ExecutedAt = executedNow.ToString("O"),
                   RescuedReason = reason,
               };
    }

    public async Task<CleanerrQueueProcessResult> ProcessDeletionQueueAsync(
        DateTimeOffset? referenceNow = null,
        CancellationToken ct = default)
    {
        var now = referenceNow ?? DateTimeOffset.UtcNow;
        var staged = await ListDeletionQueueAsync("staged", ct);
        var allMedia = await ListMediaItemsAsync(ct);
        var byId = allMedia.ToDictionary(m => m.Id);
        var byRk = allMedia.ToDictionary(m => m.PlexRatingKey, StringComparer.Ordinal);
        var rules = await ListExclusionRulesAsync(ct);
        var discordCfg = await discordService.GetSettingsAsync(ct);
        var generalSettings = await GetGeneralSettingsAsync(ct);

        var rescued = 0;
        var deleted = 0;
        var deferredStream = 0;
        var reclaimed = 0L;
        var deletedMediaItems = new List<CleanerrMediaItem>();
        var details = new List<object>();
        var stillStaged = new List<(CleanerrDeletionQueueItem Queue, CleanerrMediaItem Media)>();

        foreach (var qitem in staged)
        {
            if (!byId.TryGetValue(qitem.MediaItemId, out var media))
            {
                await UpdateQueueStateAsync(qitem.Id, "failed", "Media item no longer in database", now, qitem.LogsJson, "missing", ct);
                continue;
            }

            var stagedAt = ParseDto(qitem.StagedAt) ?? now;
            var lastViewed = ParseDto(media.LastViewedAt);
            var deleteAt = ParseDto(qitem.DeleteAt) ?? now;
            var autoRescueOnWatch = GetBool(qitem.ActionConfigJson, "auto_rescue_on_watch") ?? true;

            // 1. Auto-Rescue due to Plex Playback during Grace Period
            if (autoRescueOnWatch && lastViewed.HasValue && lastViewed.Value > stagedAt)
            {
                var rReason = $"Watched in Plex during grace period ({lastViewed.Value:O})";
                await UpdateQueueStateAsync(qitem.Id, "rescued", rReason, now, qitem.LogsJson, "auto_rescued", ct);
                rescued++;
                details.Add(new { queueId = qitem.Id, title = media.Title, action = "rescued", reason = rReason });

                if (discordCfg.NotifyOnRescued)
                {
                    try { await discordService.SendRescuedNotificationAsync(media, rReason, ct); } catch { /* Best effort */ }
                }
                continue;
            }

            // 2. Auto-Rescue due to active Exclusion Rule (including TTL & Series ancestor inheritance)
            var (isExcl, exclReason) = CheckItemExcluded(media, rules, byRk, qitem.WorkflowId, null, null, now);
            if (isExcl)
            {
                var rReason = exclReason ?? "Matched active Exclusion Rule";
                await UpdateQueueStateAsync(qitem.Id, "rescued", rReason, now, qitem.LogsJson, "auto_rescued_exclusion", ct);
                rescued++;
                details.Add(new { queueId = qitem.Id, title = media.Title, action = "rescued", reason = rReason });
                continue;
            }

            // 3. Grace Period Expired -> Check Active Stream Safety Guard, then Execute Downstream Actions
            if (now >= deleteAt)
            {
                if (media.ActiveStreamCount > 0)
                {
                    deferredStream++;
                    details.Add(new { queueId = qitem.Id, title = media.Title, action = "deferred_active_stream", activeStreams = media.ActiveStreamCount });
                    stillStaged.Add((qitem, media));
                    continue;
                }

                var actionsArr = qitem.ActionConfigJson["actions"]?.AsArray()?.OfType<JsonObject>().ToList() ?? [];
                if (actionsArr.Count == 0)
                {
                    actionsArr.Add(new JsonObject
                    {
                        ["node_type"] = "action.managerr",
                        ["config"] = new JsonObject { ["mode"] = "delete_files_and_unmonitor" },
                    });
                }

                var actionLogs = await ExecuteActionsForItemAsync(media, actionsArr, ct);
                var execNote = actionLogs.Count > 0 ? string.Join("; ", actionLogs) : null;
                await UpdateQueueStateAsync(qitem.Id, "deleted", execNote, now, qitem.LogsJson, "deleted", ct);
                deleted++;
                reclaimed += media.SizeBytes;
                deletedMediaItems.Add(media);
                details.Add(new { queueId = qitem.Id, title = media.Title, action = "deleted", sizeBytes = media.SizeBytes });
            }
            else
            {
                stillStaged.Add((qitem, media));
            }
        }

        if (deletedMediaItems.Count > 0 && discordCfg.NotifyOnDeleted)
        {
            try { await discordService.SendDeletedSummaryNotificationAsync(deletedMediaItems, reclaimed, ct); } catch { /* Best effort */ }
        }

        // 4. 24h Final Warning Reminders for remaining staged items approaching deletion
        var finalWarningsSent = 0;
        var warningCutoff = now.AddHours(generalSettings.FinalWarningHours);
        var dueSoon = stillStaged
            .Where(x => string.IsNullOrWhiteSpace(x.Queue.FinalWarningSentAt) &&
                        (ParseDto(x.Queue.DeleteAt) ?? now) <= warningCutoff)
            .ToList();

        if (dueSoon.Count > 0)
        {
            var dueSoonMedia = dueSoon.Select(x => x.Media).ToList();
            var earliestDeleteAt = dueSoon
                .Select(x => ParseDto(x.Queue.DeleteAt) ?? now)
                .Min();

            try
            {
                var warnRes = await discordService.SendFinalWarningNotificationAsync(dueSoonMedia, earliestDeleteAt, ct);
                if (warnRes["sent"]?.GetValue<bool>() == true)
                {
                    var warnIds = dueSoon.Select(x => x.Queue.Id).ToArray();
                    var warnNowIso = now.ToString("O");
                    await dbFactory.ExecuteWriteAsync(
                        DatabaseKind.Core,
                        (conn, tx) => conn.ExecuteAsync(
                            "UPDATE cleanerr_deletion_queue SET final_warning_sent_at = @Now WHERE id IN @Ids;",
                            new { Now = warnNowIso, Ids = warnIds },
                            transaction: tx),
                        ct);
                    finalWarningsSent = warnIds.Length;
                    details.Add(new { action = "final_warning_sent", count = finalWarningsSent, earliestDeleteAt = earliestDeleteAt.ToString("O") });
                }
            }
            catch
            {
                // Best-effort final warning notification
            }
        }

        return new CleanerrQueueProcessResult(staged.Count, rescued, deleted, deferredStream, reclaimed, details, finalWarningsSent);
    }

    public async Task<CleanerrDeletionQueueItem> RescueQueueItemAsync(
        long queueId,
        string reason = "Manually rescued by user",
        CancellationToken ct = default)
    {
        var all = await ListDeletionQueueAsync(null, ct);
        var item = all.FirstOrDefault(x => x.Id == queueId)
            ?? throw new KeyNotFoundException($"Queue item #{queueId} not found.");
        await UpdateQueueStateAsync(queueId, "rescued", reason, DateTimeOffset.UtcNow, item.LogsJson, "manual_rescue", ct);

        return (await ListDeletionQueueAsync(null, ct)).First(x => x.Id == queueId);
    }

    public async Task<IReadOnlyList<CleanerrDeletionQueueItem>> RescueMediaIdsWithFamilyAsync(
        IReadOnlyList<long> mediaIds,
        string reason,
        CancellationToken ct = default)
    {
        if (mediaIds.Count == 0) return [];
        var allMedia = await ListMediaItemsAsync(ct);
        var byId = allMedia.ToDictionary(m => m.Id);
        var targetFamilyIds = new HashSet<long>();

        foreach (var mid in mediaIds)
        {
            if (byId.TryGetValue(mid, out var media))
            {
                var (_, fam) = ResolveMediaToSeriesFamily(allMedia, media);
                foreach (var fid in fam) targetFamilyIds.Add(fid);
            }
            else
            {
                targetFamilyIds.Add(mid);
            }
        }

        var staged = await ListDeletionQueueAsync("staged", ct);
        var rescued = new List<CleanerrDeletionQueueItem>();
        foreach (var q in staged.Where(q => targetFamilyIds.Contains(q.MediaItemId)))
        {
            rescued.Add(await RescueQueueItemAsync(q.Id, reason, ct));
        }

        return rescued;
    }

    public async Task<(CleanerrDeletionQueueItem QueueItem, CleanerrExclusionRule Rule)> ExcludeQueueItemAsync(
        long queueId,
        string createdBy,
        long? createdByUserId,
        int? exclusionQuotaLimit,
        string reason = "Excluded from Deletion Queue",
        string? expiresAt = null,
        CancellationToken ct = default)
    {
        var all = await ListDeletionQueueAsync(null, ct);
        var qitem = all.FirstOrDefault(x => x.Id == queueId)
            ?? throw new KeyNotFoundException($"Queue item #{queueId} not found.");

        await ExcludeMediaIdsWithFamilyEscalationAsync(
            [qitem.MediaItemId],
            createdBy,
            reason,
            createdByUserId,
            exclusionQuotaLimit,
            expiresAt,
            ct);

        var updatedQ = (await ListDeletionQueueAsync(null, ct)).First(x => x.Id == queueId);
        var rules = await ListExclusionRulesAsync(ct);
        var allMedia = await ListMediaItemsAsync(ct);
        var qMedia = allMedia.FirstOrDefault(m => m.Id == qitem.MediaItemId);
        var famSet = qMedia is not null
            ? ResolveMediaToSeriesFamily(allMedia, qMedia).FamilyIds.ToHashSet()
            : [qitem.MediaItemId];
        var rule = rules.FirstOrDefault(r => r.MediaItemId == qitem.MediaItemId)
                   ?? rules.FirstOrDefault(r => r.MediaItemId.HasValue && famSet.Contains(r.MediaItemId.Value))
                   ?? rules.First();
        return (updatedQ, rule);
    }

    public async Task<int> BulkExcludeQueueAsync(
        BulkQueueExcludeRequest request,
        string createdBy,
        long? createdByUserId,
        int? exclusionQuotaLimit,
        CancellationToken ct = default)
    {
        var staged = await ListDeletionQueueAsync("staged", ct);
        var targets = request.QueueIds is { Count: > 0 }
            ? staged.Where(q => request.QueueIds.Contains(q.Id)).ToList()
            : staged.ToList();

        if (targets.Count == 0)
        {
            return 0;
        }

        var mediaIds = targets.Select(q => q.MediaItemId).Distinct().ToList();
        var reason = string.IsNullOrWhiteSpace(request.Reason)
            ? "Bulk excluded from Leaving Soon queue"
            : request.Reason.Trim();

        await ExcludeMediaIdsWithFamilyEscalationAsync(
            mediaIds,
            createdBy,
            reason,
            createdByUserId,
            exclusionQuotaLimit,
            null,
            ct);

        return targets.Count;
    }

    public async Task DeleteQueueItemRecordAsync(long queueId, CancellationToken ct = default)
    {
        var affected = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync("DELETE FROM cleanerr_deletion_queue WHERE id = @Id;", new { Id = queueId }, transaction: tx),
            ct);

        if (affected == 0)
        {
            throw new KeyNotFoundException($"Queue item #{queueId} not found.");
        }
    }

    private async Task<IReadOnlyList<string>> ExecuteActionsForItemAsync(
        CleanerrMediaItem item,
        IReadOnlyList<JsonObject> actionNodes,
        CancellationToken ct)
    {
        var logs = new List<string>();

        foreach (var act in actionNodes)
        {
            var ntype = act["node_type"]?.GetValue<string>() ?? "";
            var cfg = act["config"]?.AsObject() ?? new JsonObject();

            if (ntype == "action.add_import_exclusion")
            {
                await AddImportListExclusionAsync(item, ct);
                logs.Add($"Added import list exclusion for '{item.Title}'");
                continue;
            }

            if (ntype is "action.seerr_reset" or "action.seerr")
            {
                var resetExternal = GetBool(cfg, "reset_external_seerr") ?? true;
                var seerrMode = (GetString(cfg, "mode") ?? "reset_media_status").Trim().ToLowerInvariant();
                await TryResetSeerrRequestAsync(item, resetExternal, ct, seerrMode);
                logs.Add($"Executed Seerr action '{seerrMode}' for '{item.Title}'");
                continue;
            }

            if (ntype == "action.plex")
            {
                var plexMode = (GetString(cfg, "mode") ?? GetString(cfg, "action") ?? "refresh_metadata").ToLowerInvariant();
                await TryExecutePlexItemActionAsync(item, plexMode, ct);
                logs.Add($"Executed Plex action '{plexMode}' for '{item.Title}'");
                continue;
            }

            if (ntype is "action.webhook" or "notify.webhook")
            {
                var url = GetString(cfg, "url");
                if (!string.IsNullOrWhiteSpace(url) &&
                    (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    var includeItems = GetBool(cfg, "include_items") ?? true;
                    var body = includeItems
                        ? JsonSerializer.Serialize(new
                        {
                            @event = "media_cleanup",
                            title = item.Title,
                            media_type = item.MediaType,
                            size_bytes = item.SizeBytes,
                        }, JsonOpts)
                        : JsonSerializer.Serialize(new
                        {
                            @event = "media_cleanup",
                        }, JsonOpts);
                    try { await httpGateway.SendJsonAsync(HttpMethod.Post, url, body, null, ct); } catch { /* Best effort */ }
                }
                continue;
            }

            if (ntype is "action.managerr" or "action.radarr" or "action.sonarr" or "action.delete_files")
            {
                var defaultMode = ntype == "action.delete_files" ? "delete_files_only" : "delete_files_and_unmonitor";
                var mode = (GetString(cfg, "mode") ?? defaultMode).ToLowerInvariant();
                var skipHardlinked = GetBool(cfg, "skip_hardlinked_seeding") ?? false;
                if (skipHardlinked && item.IsHardlinked)
                {
                    logs.Add($"Skipped '{item.Title}': hardlinked seeding protection");
                    continue;
                }

                if (mode == "add_import_exclusion" || (GetBool(cfg, "add_import_exclusion") ?? false))
                {
                    await AddImportListExclusionAsync(item, ct);
                    logs.Add($"Added import list exclusion for '{item.Title}'");
                    if (mode == "add_import_exclusion")
                    {
                        continue;
                    }
                }

                if (mode == "downgrade_profile")
                {
                    var (targetProfileId, skipReason) = await ResolveAndValidateDowngradeProfileAsync(cfg, ct);
                    if (!targetProfileId.HasValue)
                    {
                        if (skipReason is not null) logs.Add(skipReason);
                        continue;
                    }

                    var downgradeUnitIds = await CollectTargetUnitIdsAsync(item, ct);
                    if (downgradeUnitIds.Count > 0)
                    {
                        foreach (var uid in downgradeUnitIds)
                        {
                            await catalogService.UpdateUnitAsync(
                                uid,
                                new UpdateMediaUnitRequest(ProfileOverrideId: targetProfileId.Value, QualityCutoffMet: false),
                                ct);
                        }
                        logs.Add($"Downgraded {downgradeUnitIds.Count} unit(s) for '{item.Title}' to profile #{targetProfileId.Value}");
                    }
                    else
                    {
                        logs.Add($"Skipped downgrade_profile for '{item.Title}': no linked media_unit");
                    }
                    continue;
                }

                if (mode == "unmonitor_only")
                {
                    var resetReqOnly = GetBool(cfg, "reset_requests") ?? false;
                    var unmonitorUnitIds = await CollectTargetUnitIdsAsync(item, ct);

                    await dbFactory.ExecuteWriteAsync(
                        DatabaseKind.Core,
                        async (conn, tx) =>
                        {
                            if (unmonitorUnitIds.Count > 0)
                            {
                                await conn.ExecuteAsync(
                                    "UPDATE media_units SET monitored = 0 WHERE id IN @Uids;",
                                    new { Uids = unmonitorUnitIds.ToArray() },
                                    transaction: tx);
                            }

                            if (item.WorkId.HasValue && item.MediaType is "movie" or "show")
                            {
                                await conn.ExecuteAsync(
                                    "UPDATE media_containers SET monitored = 0 WHERE work_id = @Wid;",
                                    new { Wid = item.WorkId.Value },
                                    transaction: tx);
                                await conn.ExecuteAsync(
                                    "UPDATE media_works SET monitored = 0 WHERE id = @Wid;",
                                    new { Wid = item.WorkId.Value },
                                    transaction: tx);
                            }

                            await conn.ExecuteAsync(
                                "UPDATE cleanerr_media_items SET monitored = 0 WHERE id = @Id;",
                                new { Id = item.Id },
                                transaction: tx);

                            if (item.MediaType is "show" or "season")
                            {
                                await conn.ExecuteAsync(
                                    """
                                    UPDATE cleanerr_media_items
                                    SET monitored = 0
                                    WHERE parent_rating_key = @Rk OR grandparent_rating_key = @Rk;
                                    """,
                                    new { Rk = item.PlexRatingKey },
                                    transaction: tx);
                            }
                        },
                        ct);

                    if (resetReqOnly)
                    {
                        await TryResetSeerrRequestAsync(item, resetExternalSeerr: true, ct);
                    }

                    logs.Add($"Unmonitored '{item.Title}' (files preserved)");
                    continue;
                }

                var unmonitor = ntype == "action.delete_files"
                    ? (GetBool(cfg, "unmonitor") ?? false)
                    : (mode != "delete_files_only" && (GetBool(cfg, "unmonitor") ?? true));
                var cleanEmptyFolders = GetBool(cfg, "clean_empty_folders") ?? true;
                var resetRequests = GetBool(cfg, "reset_requests") ?? false;

                if (item.UnitId.HasValue && unmonitor)
                {
                    await catalogService.UpdateUnitAsync(item.UnitId.Value, new UpdateMediaUnitRequest(Monitored: false), ct);
                }

                var libraries = await catalogService.ListLibrariesAsync(null, ct);
                var candidateFiles = new HashSet<string>(StringComparer.Ordinal);
                if (!string.IsNullOrWhiteSpace(item.FilePath))
                {
                    candidateFiles.Add(item.FilePath);
                }

                var targetUnitIds = await CollectTargetUnitIdsAsync(item, ct);
                await using (var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
                {
                    if (item.MediaType is "show" or "season")
                    {
                        var childPaths = await readConn.QueryAsync<string>(
                            """
                            SELECT file_path FROM cleanerr_media_items
                            WHERE file_path IS NOT NULL
                              AND (parent_rating_key = @Rk OR grandparent_rating_key = @Rk);
                            """,
                            new { Rk = item.PlexRatingKey });
                        foreach (var cp in childPaths)
                        {
                            if (!string.IsNullOrWhiteSpace(cp)) candidateFiles.Add(cp);
                        }
                    }

                    var libRoot = libraries.FirstOrDefault(l => l.Id == item.LibraryId)?.RootFolderPath;
                    if (targetUnitIds.Count > 0)
                    {
                        var relPaths = await readConn.QueryAsync<string>(
                            "SELECT relative_path FROM media_files WHERE unit_id IN @Uids;",
                            new { Uids = targetUnitIds.ToArray() });
                        foreach (var rp in relPaths)
                        {
                            if (string.IsNullOrWhiteSpace(rp)) continue;
                            candidateFiles.Add(Path.IsPathRooted(rp) || string.IsNullOrWhiteSpace(libRoot)
                                ? rp
                                : Path.Combine(libRoot, rp));
                        }
                    }

                    if (item.WorkId.HasValue && item.MediaType is "movie" or "show")
                    {
                        var relPaths = await readConn.QueryAsync<string>(
                            "SELECT relative_path FROM media_files WHERE work_id = @Wid;",
                            new { Wid = item.WorkId.Value });
                        foreach (var rp in relPaths)
                        {
                            if (string.IsNullOrWhiteSpace(rp)) continue;
                            candidateFiles.Add(Path.IsPathRooted(rp) || string.IsNullOrWhiteSpace(libRoot)
                                ? rp
                                : Path.Combine(libRoot, rp));
                        }
                    }
                }

                if (item.MediaType == "show" && !item.WorkId.HasValue && !string.IsNullOrWhiteSpace(item.PlexRatingKey))
                {
                    try
                    {
                        var (serverUrl, plexHeaders) = await TryResolvePlexConnectionAsync(ct);
                        if (!string.IsNullOrWhiteSpace(serverUrl))
                        {
                            var (st, body) = await httpGateway.SendJsonAsync(
                                HttpMethod.Get,
                                $"{serverUrl}/library/metadata/{Uri.EscapeDataString(item.PlexRatingKey)}/allLeaves",
                                null,
                                plexHeaders,
                                ct);
                            if (st is >= 200 and < 300 && !string.IsNullOrWhiteSpace(body))
                            {
                                foreach (var ep in EnumeratePlexMetadata(body))
                                {
                                    var (_, _, epPath) = ExtractPlexMediaPartInfo(ep);
                                    if (!string.IsNullOrWhiteSpace(epPath))
                                    {
                                        candidateFiles.Add(epPath);
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Ignore if Plex URL is unreachable
                    }
                }

                // For unmonitor_and_delete_existing, only unmonitor units that actually had files; leave future unaired episodes monitored
                var unitsWithFiles = new HashSet<long>();
                if (mode == "unmonitor_and_delete_existing" && targetUnitIds.Count > 0)
                {
                    await using var uConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
                    var withF = await uConn.QueryAsync<long>(
                        "SELECT DISTINCT unit_id FROM media_files WHERE unit_id IN @Uids;",
                        new { Uids = targetUnitIds.ToArray() });
                    foreach (var uid in withF) unitsWithFiles.Add(uid);
                }

                await dbFactory.ExecuteWriteAsync(
                    DatabaseKind.Core,
                    async (conn, tx) =>
                    {
                        if (targetUnitIds.Count > 0)
                        {
                            await conn.ExecuteAsync(
                                "DELETE FROM media_files WHERE unit_id IN @Uids;",
                                new { Uids = targetUnitIds.ToArray() },
                                transaction: tx);

                            if (mode == "unmonitor_and_delete_existing" && unitsWithFiles.Count > 0)
                            {
                                await conn.ExecuteAsync(
                                    "UPDATE media_units SET monitored = 0 WHERE id IN @Uids;",
                                    new { Uids = unitsWithFiles.ToArray() },
                                    transaction: tx);
                            }
                            else if (unmonitor)
                            {
                                await conn.ExecuteAsync(
                                    "UPDATE media_units SET monitored = 0 WHERE id IN @Uids;",
                                    new { Uids = targetUnitIds.ToArray() },
                                    transaction: tx);
                            }
                        }

                        if (item.WorkId.HasValue && item.MediaType is "movie" or "show")
                        {
                            await conn.ExecuteAsync(
                                "DELETE FROM media_files WHERE work_id = @Wid;",
                                new { Wid = item.WorkId.Value },
                                transaction: tx);
                        }

                        if (item.MediaType == "show" && item.WorkId.HasValue && unmonitor)
                        {
                            await conn.ExecuteAsync(
                                "UPDATE media_containers SET monitored = 0 WHERE work_id = @Wid;",
                                new { Wid = item.WorkId.Value },
                                transaction: tx);
                            await conn.ExecuteAsync(
                                "UPDATE media_works SET monitored = 0 WHERE id = @Wid;",
                                new { Wid = item.WorkId.Value },
                                transaction: tx);
                        }

                        await conn.ExecuteAsync(
                            "UPDATE cleanerr_media_items SET monitored = @Mon, size_bytes = 0, file_path = NULL WHERE id = @Id;",
                            new { Id = item.Id, Mon = unmonitor ? 0 : 1 },
                            transaction: tx);

                        if (item.MediaType is "show" or "season")
                        {
                            await conn.ExecuteAsync(
                                """
                                UPDATE cleanerr_media_items
                                SET monitored = @Mon, size_bytes = 0, file_path = NULL
                                WHERE parent_rating_key = @Rk OR grandparent_rating_key = @Rk;
                                """,
                                new { Rk = item.PlexRatingKey, Mon = unmonitor ? 0 : 1 },
                                transaction: tx);
                        }
                    },
                    ct);

                if (resetRequests)
                {
                    await TryResetSeerrRequestAsync(item, resetExternalSeerr: true, ct);
                }

                // Strict library root safety: ONLY configured library root folders are permitted for physical deletion
                var allowedRoots = libraries
                    .Select(l => l.RootFolderPath)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => EnsureTrailingSlash(Path.GetFullPath(p)))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var anyDeletedOnDisk = false;
                if (allowedRoots.Count == 0)
                {
                    if (candidateFiles.Count > 0)
                    {
                        logs.Add($"Skipped physical File.Delete for '{item.Title}': no configured library roots.");
                    }
                }
                else
                {
                    var touchedDirs = new HashSet<string>(StringComparer.Ordinal);
                    string[] sidecarExts = [".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".nfo"];

                    foreach (var rawCandidate in candidateFiles)
                    {
                        try
                        {
                            var fullCandidate = Path.GetFullPath(rawCandidate);
                            if (!allowedRoots.Any(root => fullCandidate.StartsWith(root, StringComparison.Ordinal)))
                            {
                                continue;
                            }

                            var parentDir = Path.GetDirectoryName(fullCandidate);
                            if (!string.IsNullOrWhiteSpace(parentDir))
                            {
                                touchedDirs.Add(parentDir);
                            }

                            if (File.Exists(fullCandidate))
                            {
                                File.Delete(fullCandidate);
                                anyDeletedOnDisk = true;
                            }

                            if (cleanEmptyFolders && !string.IsNullOrWhiteSpace(parentDir) && Directory.Exists(parentDir))
                            {
                                var stem = Path.GetFileNameWithoutExtension(fullCandidate);
                                foreach (var f in Directory.EnumerateFiles(parentDir))
                                {
                                    var ext = Path.GetExtension(f);
                                    var nameNoExt = Path.GetFileNameWithoutExtension(f);
                                    if (sidecarExts.Contains(ext, StringComparer.OrdinalIgnoreCase) &&
                                        (string.Equals(nameNoExt, stem, StringComparison.OrdinalIgnoreCase) ||
                                         nameNoExt.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)))
                                    {
                                        try { File.Delete(f); } catch { /* Best effort sidecar cleanup */ }
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // Best-effort physical file deletion
                        }
                    }

                    if (cleanEmptyFolders && touchedDirs.Count > 0)
                    {
                        foreach (var dir in touchedDirs.OrderByDescending(d => d.Length))
                        {
                            TryDeleteEmptyAncestorsUnderRoots(dir, allowedRoots);
                        }
                    }
                }

                // Maintainerr show-level empty check (delete_show_if_empty / unmonitor_show_if_empty / delete_work_and_files)
                if (mode == "delete_work_and_files" && item.WorkId.HasValue)
                {
                    try { await catalogService.DeleteWorkAsync(item.WorkId.Value, ct); } catch { /* Ignore if already deleted */ }
                }
                else if (mode is "delete_show_if_empty" or "unmonitor_show_if_empty")
                {
                    var showRk = item.MediaType switch
                    {
                        "show" => item.PlexRatingKey,
                        "season" => item.ParentRatingKey ?? item.GrandparentRatingKey,
                        _ => item.GrandparentRatingKey ?? item.ParentRatingKey,
                    };

                    var remainingFiles = 0;
                    await using (var chkConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
                    {
                        if (item.WorkId.HasValue)
                        {
                            remainingFiles += await chkConn.ExecuteScalarAsync<int>(
                                "SELECT COUNT(1) FROM media_files WHERE work_id = @Wid;",
                                new { Wid = item.WorkId.Value });
                        }

                        if (!string.IsNullOrWhiteSpace(showRk))
                        {
                            remainingFiles += await chkConn.ExecuteScalarAsync<int>(
                                """
                                SELECT COUNT(1) FROM cleanerr_media_items
                                WHERE media_type = 'episode'
                                  AND (parent_rating_key = @Rk OR grandparent_rating_key = @Rk)
                                  AND (size_bytes > 0 OR file_path IS NOT NULL);
                                """,
                                new { Rk = showRk });
                        }
                    }

                    if (remainingFiles == 0)
                    {
                        if (mode == "delete_show_if_empty")
                        {
                            if (item.WorkId.HasValue)
                            {
                                try { await catalogService.DeleteWorkAsync(item.WorkId.Value, ct); } catch { /* Best effort */ }
                            }

                            if (!string.IsNullOrWhiteSpace(showRk))
                            {
                                await dbFactory.ExecuteWriteAsync(
                                    DatabaseKind.Core,
                                    (conn, tx) => conn.ExecuteAsync(
                                        """
                                        DELETE FROM cleanerr_media_items
                                        WHERE plex_rating_key = @Rk OR parent_rating_key = @Rk OR grandparent_rating_key = @Rk;
                                        """,
                                        new { Rk = showRk },
                                        transaction: tx),
                                    ct);
                            }
                        }
                        else if (mode == "unmonitor_show_if_empty")
                        {
                            if (item.WorkId.HasValue)
                            {
                                try
                                {
                                    await catalogService.UpdateWorkAsync(
                                        item.WorkId.Value,
                                        new UpdateMediaWorkRequest(Monitored: false, MonitorPreset: "none"),
                                        ct);
                                }
                                catch { /* Best effort */ }
                            }

                            if (!string.IsNullOrWhiteSpace(showRk))
                            {
                                await dbFactory.ExecuteWriteAsync(
                                    DatabaseKind.Core,
                                    (conn, tx) => conn.ExecuteAsync(
                                        """
                                        UPDATE cleanerr_media_items
                                        SET monitored = 0
                                        WHERE plex_rating_key = @Rk OR parent_rating_key = @Rk OR grandparent_rating_key = @Rk;
                                        """,
                                        new { Rk = showRk },
                                        transaction: tx),
                                    ct);
                            }
                        }
                    }
                }

                if (GetBool(cfg, "prune_torrent_client") == true &&
                    mode is not ("unmonitor_only" or "downgrade_profile"))
                {
                    await TryPruneTorrentClientAsync(item, candidateFiles, logs, ct);
                }

                if (anyDeletedOnDisk)
                {
                    await TryRefreshPlexSectionsAsync(ct);
                }
            }
        }

        return logs;
    }

    private async Task<(long? ProfileId, string? SkipReason)> ResolveAndValidateDowngradeProfileAsync(
        JsonObject cfg,
        CancellationToken ct)
    {
        var rawProfileId = GetLong(cfg, "target_profile_id")
                           ?? GetLong(cfg, "targetProfileId")
                           ?? GetLong(cfg, "downgrade_profile_id")
                           ?? GetLong(cfg, "downgradeProfileId");

        if (!rawProfileId.HasValue || rawProfileId.Value <= 0)
        {
            return (null, "Skipped downgrade_profile: no target_profile_id specified.");
        }

        if (profileService is not null)
        {
            var profile = await profileService.GetProfileByIdAsync(rawProfileId.Value, ct);
            if (profile is null)
            {
                return (null, $"Skipped downgrade_profile: quality profile #{rawProfileId.Value} was not found.");
            }
        }

        return (rawProfileId.Value, null);
    }

    private async Task AddImportListExclusionAsync(CleanerrMediaItem item, CancellationToken ct)
    {
        var extKey = $"plex:{item.PlexRatingKey}";
        if (item.WorkId.HasValue)
        {
            await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
            var extJson = await readConn.ExecuteScalarAsync<string?>(
                "SELECT external_ids_json FROM media_works WHERE id = @Wid;",
                new { Wid = item.WorkId.Value });
            if (!string.IsNullOrWhiteSpace(extJson))
            {
                try
                {
                    var obj = JsonNode.Parse(extJson)?.AsObject();
                    var tmdb = obj?["tmdb"]?.ToString();
                    var tvdb = obj?["tvdb"]?.ToString();
                    var imdb = obj?["imdb"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(tmdb)) extKey = $"tmdb:{tmdb}";
                    else if (!string.IsNullOrWhiteSpace(tvdb)) extKey = $"tvdb:{tvdb}";
                    else if (!string.IsNullOrWhiteSpace(imdb)) extKey = $"imdb:{imdb}";
                    else extKey = $"work:{item.WorkId.Value}";
                }
                catch
                {
                    extKey = $"work:{item.WorkId.Value}";
                }
            }
        }

        var vertical = item.MediaType switch
        {
            "movie" => "movies",
            "show" or "season" or "episode" => "tv",
            "music" => "music",
            _ => "ebooks",
        };
        var title = CleanerrDiscordService.FormatSeriesOrMovieTitle(item);
        var now = DateTimeOffset.UtcNow.ToString("O");

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                INSERT INTO import_list_exclusions (external_key, title, media_vertical, year, created_at)
                VALUES (@ExtKey, @Title, @Vertical, @Year, @Now)
                ON CONFLICT(external_key) DO NOTHING;
                """,
                new { ExtKey = extKey, Title = title, Vertical = vertical, item.Year, Now = now },
                transaction: tx),
            ct);
    }

    private async Task TryPruneTorrentClientAsync(
        CleanerrMediaItem item,
        IReadOnlyCollection<string> candidateFiles,
        List<string> logs,
        CancellationToken ct)
    {
        if (downloadOrchestrator is null)
        {
            return;
        }

        try
        {
            var queueItems = await downloadOrchestrator.GetUnifiedQueueAsync(ct);
            if (queueItems.Count == 0)
            {
                return;
            }

            var normalizedCandidates = candidateFiles
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var removedCount = 0;
            foreach (var qItem in queueItems)
            {
                if (!string.Equals(qItem.Protocol, "torrent", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var outputFull = !string.IsNullOrWhiteSpace(qItem.OutputPath) ? Path.GetFullPath(qItem.OutputPath) : null;
                var outputDirSlash = outputFull is not null ? EnsureTrailingSlash(outputFull) : null;

                var matched =
                    (outputFull is not null &&
                     (normalizedCandidates.Contains(outputFull) ||
                      normalizedCandidates.Any(c => c.StartsWith(outputDirSlash!, StringComparison.OrdinalIgnoreCase)))) ||
                    qItem.ContainedFiles.Any(cf =>
                    {
                        if (string.IsNullOrWhiteSpace(cf)) return false;
                        var resolved = Path.IsPathRooted(cf)
                            ? Path.GetFullPath(cf)
                            : (outputFull is not null ? Path.GetFullPath(Path.Combine(outputFull, cf)) : null);
                        return resolved is not null && normalizedCandidates.Contains(resolved);
                    });

                if (!matched)
                {
                    continue;
                }

                await downloadOrchestrator.RemoveDownloadFromClientAsync(
                    qItem.ClientId,
                    qItem.ExternalId,
                    deleteData: true,
                    ct);
                removedCount++;
            }

            if (removedCount > 0)
            {
                logs.Add($"Pruned {removedCount} torrent(s) from download client for '{item.Title}'");
            }
        }
        catch (Exception ex)
        {
            logs.Add($"Torrent client prune skipped/failed for '{item.Title}': {ex.Message}");
        }
    }

    private async Task TryResetSeerrRequestAsync(
        CleanerrMediaItem item,
        bool resetExternalSeerr,
        CancellationToken ct,
        string mode = "reset_media_status")
    {
        if (item.WorkId.HasValue)
        {
            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                (conn, tx) => conn.ExecuteAsync(
                    "UPDATE media_requests SET status = 'cleaned_reset' WHERE work_id = @Wid;",
                    new { Wid = item.WorkId.Value },
                    transaction: tx),
                ct);
        }

        if (!resetExternalSeerr || !item.SeerrId.HasValue || appSettings is null)
        {
            return;
        }

        try
        {
            var seerrUrl = (await appSettings.GetAsync(SettingSeerrUrl, ct))?.Trim().TrimEnd('/');
            var seerrApiKey = (await appSettings.GetSecretAsync(SettingSeerrApiKey, ct))?.Trim();
            if (string.IsNullOrWhiteSpace(seerrUrl) || string.IsNullOrWhiteSpace(seerrApiKey))
            {
                return;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Api-Key"] = seerrApiKey,
                ["Accept"] = "application/json",
            };

            var normalizedMode = mode.Trim().ToLowerInvariant();
            var idStr = item.SeerrId.Value.ToString(CultureInfo.InvariantCulture);

            await httpGateway.SendJsonAsync(
                HttpMethod.Delete,
                $"{seerrUrl}/api/v1/request/{idStr}",
                null,
                headers,
                ct);

            if (normalizedMode != "delete_request")
            {
                await httpGateway.SendJsonAsync(
                    HttpMethod.Delete,
                    $"{seerrUrl}/api/v1/media/{idStr}",
                    null,
                    headers,
                    ct);
            }
        }
        catch
        {
            // Best-effort external Seerr reset; never fail core cleanup
        }
    }

    private static void TryDeleteEmptyAncestorsUnderRoots(string startDir, IReadOnlyList<string> allowedRoots)
    {
        var current = startDir;
        for (var depth = 0; depth < 3 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            try
            {
                var full = Path.GetFullPath(current);
                var fullWithSlash = EnsureTrailingSlash(full);

                // Strictly inside an allowed root, and NEVER equal to the root folder itself
                var underRoot = allowedRoots.Any(root =>
                    full.StartsWith(root, StringComparison.Ordinal) &&
                    !string.Equals(fullWithSlash, root, StringComparison.Ordinal));
                if (!underRoot || !Directory.Exists(full))
                {
                    break;
                }

                if (Directory.EnumerateFileSystemEntries(full).Any())
                {
                    break;
                }

                Directory.Delete(full, recursive: false);
                current = Path.GetDirectoryName(full);
            }
            catch
            {
                break;
            }
        }
    }

    private async Task<HashSet<long>> CollectTargetUnitIdsAsync(CleanerrMediaItem item, CancellationToken ct)
    {
        var targetUnitIds = new HashSet<long>();
        if (item.UnitId.HasValue)
        {
            targetUnitIds.Add(item.UnitId.Value);
        }

        if (item.MediaType is "show" or "season")
        {
            await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
            var childUnits = await readConn.QueryAsync<long>(
                """
                SELECT DISTINCT unit_id FROM cleanerr_media_items
                WHERE unit_id IS NOT NULL
                  AND (parent_rating_key = @Rk OR grandparent_rating_key = @Rk);
                """,
                new { Rk = item.PlexRatingKey });
            foreach (var cuid in childUnits) targetUnitIds.Add(cuid);

            if (item.WorkId.HasValue && item.MediaType == "season" && item.SeasonNumber.HasValue)
            {
                var nativeSeasonUnits = await readConn.QueryAsync<long>(
                    """
                    SELECT u.id
                    FROM media_units u
                    JOIN media_containers c ON c.id = u.container_id
                    WHERE c.work_id = @Wid AND c.container_number = @Sn;
                    """,
                    new { Wid = item.WorkId.Value, Sn = item.SeasonNumber.Value });
                foreach (var nuid in nativeSeasonUnits) targetUnitIds.Add(nuid);
            }
            else if (item.WorkId.HasValue && item.MediaType == "show")
            {
                var nativeShowUnits = await readConn.QueryAsync<long>(
                    "SELECT id FROM media_units WHERE work_id = @Wid;",
                    new { Wid = item.WorkId.Value });
                foreach (var nuid in nativeShowUnits) targetUnitIds.Add(nuid);
            }
        }

        return targetUnitIds;
    }

    private async Task UpdateQueueStateAsync(
        long queueId,
        string newStatus,
        string? reason,
        DateTimeOffset now,
        JsonArray existingLogs,
        string eventName,
        CancellationToken ct)
    {
        var cloned = existingLogs.DeepClone().AsArray();
        cloned.Add(new JsonObject
        {
            ["timestamp"] = now.ToString("O"),
            ["event"] = eventName,
            ["reason"] = reason,
        });

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                UPDATE cleanerr_deletion_queue
                SET status = @Status,
                    rescued_reason = COALESCE(@Reason, rescued_reason),
                    executed_at = @Now,
                    logs_json = @Logs
                WHERE id = @Id;
                """,
                new
                {
                    Id = queueId,
                    Status = newStatus,
                    Reason = reason,
                    Now = now.ToString("O"),
                    Logs = cloned.ToJsonString(),
                },
                transaction: tx),
            ct);
    }
}
