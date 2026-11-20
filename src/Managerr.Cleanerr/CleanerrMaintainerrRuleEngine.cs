using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Managerr.Cleanerr;

public sealed record CleanerrEvaluationContext(
    IReadOnlyList<CleanerrMediaItem> AllMedia,
    IReadOnlyDictionary<string, CleanerrMediaItem> MediaByRatingKey,
    IReadOnlyDictionary<string, IReadOnlyList<CleanerrMediaItem>> ChildrenByParentRk,
    IReadOnlyDictionary<string, IReadOnlyList<CleanerrMediaItem>> EpisodesByShowRk,
    DateTimeOffset ReferenceNow,
    double DiskFreeGb,
    double DiskFreePercent);

/// <summary>
/// Maintainerr-compatible Boolean Section Rule Engine + 21-Operator Comparator + Property-vs-Property
/// Evaluator + "Test Media" Runtime Explainer + Portable Rules &amp; DAG YAML/JSON Serializer/Importer.
/// </summary>
public static class CleanerrMaintainerrRuleEngine
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly IReadOnlyList<string> AllMediaTypes = ["movie", "show"];
    private static readonly IReadOnlyList<string> TvMediaTypes = ["show"];

    private static readonly IReadOnlyList<CleanerrRuleFieldDescriptor> FieldCatalog =
    [
        // --- Managerr Catalog ---
        new("catalog.title", "Managerr Catalog", "Title", "text", AllMediaTypes, "Movie or Series title"),
        new("catalog.mediaType", "Managerr Catalog", "Media Type", "text", AllMediaTypes, "Media type", ["movie", "show"]),
        new("catalog.classification", "Managerr Catalog", "Taxonomy Classification", "text", AllMediaTypes,
            "Auto-classified media taxonomy",
            ["anime-series", "anime-movie", "cartoon-series", "cartoon-movie", "live-action-series", "live-action-movie"]),
        new("catalog.tags", "Managerr Catalog", "Tags & Labels", "list", AllMediaTypes,
            "Catalog tags, auto-taxonomy tags, and read-only Plex labels",
            ["anime", "anime-series", "anime-movie", "cartoon", "cartoon-series", "cartoon-movie", "live-action", "keep", "kids"]),
        new("catalog.genres", "Managerr Catalog", "Genres", "list", AllMediaTypes, "Metadata genres", ["Animation", "Action", "Comedy", "Drama", "Sci-Fi", "Documentary"]),
        new("catalog.year", "Managerr Catalog", "Release Year", "number", AllMediaTypes, "Release or premiere year"),
        new("catalog.addedAt", "Managerr Catalog", "Added Date", "date", AllMediaTypes, "ISO timestamp when added to library"),
        new("catalog.addedAgeDays", "Managerr Catalog", "Added Age (Days)", "number", AllMediaTypes, "Days elapsed since item was added"),
        new("catalog.monitored", "Managerr Catalog", "Is Monitored", "boolean", AllMediaTypes, "Whether item is monitored in Managerr", ["true", "false"]),
        new("catalog.status", "Managerr Catalog", "Series / Work Status", "text", AllMediaTypes, "Lifecycle status of the series or movie", ["continuing", "ended", "released", "upcoming"]),
        new("catalog.rating", "Managerr Catalog", "Audience / Critic Rating", "number", AllMediaTypes, "Rating score (0.0 - 10.0)"),
        new("catalog.customFormatScore", "Managerr Catalog", "Custom Format Score", "number", AllMediaTypes, "Weighted TRaSH / Custom Format score"),
        new("catalog.libraryId", "Managerr Catalog", "Library ID", "number", AllMediaTypes, "Managerr root library numeric ID"),

        // --- Media Files ---
        new("file.sizeGb", "Media Files", "File Size (GB)", "number", AllMediaTypes, "Total disk usage in GiB"),
        new("file.sizeBytes", "Media Files", "File Size (Bytes)", "number", AllMediaTypes, "Total disk usage in bytes"),
        new("file.resolution", "Media Files", "Video Resolution", "text", AllMediaTypes, "Detected video resolution", ["2160p", "1080p", "720p", "480p"]),
        new("file.videoCodec", "Media Files", "Video Codec", "text", AllMediaTypes, "Primary video stream codec", ["h264", "hevc", "av1", "mpeg4"]),
        new("file.audioLanguages", "Media Files", "Audio Languages", "list", AllMediaTypes, "Detected audio language codes", ["de", "en", "ja", "fr", "es"]),
        new("file.audioLanguageCount", "Media Files", "Audio Track Count", "number", AllMediaTypes, "Number of distinct audio languages (e.g. >= 2 for Dual-Audio)"),
        new("file.isHardlinked", "Media Files", "Is Hardlinked (Seeding)", "boolean", AllMediaTypes, "True when file shares an active torrent seeding hardlink", ["true", "false"]),
        new("file.hasFile", "Media Files", "Has Media File on Disk", "boolean", AllMediaTypes, "True when SizeBytes > 0 or FilePath is populated", ["true", "false"]),

        // --- Plex & Playback (Read-Only) ---
        new("plex.viewCount", "Plex & Playback", "Watch / View Count", "number", AllMediaTypes, "Total play count across Managerr & Plex"),
        new("plex.isWatched", "Plex & Playback", "Has Been Watched", "boolean", AllMediaTypes, "True when ViewCount > 0 or LastViewedAt is set", ["true", "false"]),
        new("plex.lastViewedAt", "Plex & Playback", "Last Watched Date", "date", AllMediaTypes, "Timestamp of most recent playback"),
        new("plex.daysSinceLastViewed", "Plex & Playback", "Days Since Last Watched", "number", AllMediaTypes, "Days since last playback (empty if never watched)"),
        new("plex.daysSinceLastActivity", "Plex & Playback", "Days Since Last Activity (Watched or Added)", "number", AllMediaTypes, "Days since last watch, or days since added if never watched (Monday Cleanup metric)"),
        new("plex.viewedByUsers", "Plex & Playback", "Watched By Users", "list", AllMediaTypes, "Usernames who watched this item"),
        new("plex.viewedByCount", "Plex & Playback", "Unique Viewers Count", "number", AllMediaTypes, "Count of distinct users who watched this item"),
        new("plex.isInProgress", "Plex & Playback", "Is Partially Watched (In Progress)", "boolean", AllMediaTypes, "True when playback is paused mid-stream (viewOffset > 0)", ["true", "false"]),
        new("plex.activeStreamCount", "Plex & Playback", "Active Live Streams", "number", AllMediaTypes, "Number of users currently streaming this item"),
        new("plex.isWatchlisted", "Plex & Playback", "On User Watchlist", "boolean", AllMediaTypes, "True when item is on a synced Plex Watchlist", ["true", "false"]),

        // --- TV Hierarchy & Rollups (Whole-Series) ---
        new("tv.seriesActiveWatchDays", "TV Hierarchy", "Days Since Any Episode in Series Watched", "number", TvMediaTypes, "Protects series while someone is actively binging"),

        // --- Requests (Seerr / Managerr Native) ---
        new("request.isRequested", "Requests", "Was Requested", "boolean", AllMediaTypes, "True when item originated from a user request", ["true", "false"]),
        new("request.requestedBy", "Requests", "Requested By Username", "text", AllMediaTypes, "Username of requester (Managerr or Overseerr/Jellyseerr)"),
        new("request.requestedAt", "Requests", "Requested Date", "date", AllMediaTypes, "Timestamp when the request was submitted"),
        new("request.requestAgeDays", "Requests", "Request Age (Days)", "number", AllMediaTypes, "Days elapsed since the request was created"),
        new("request.requesterWatched", "Requests", "Requester Has Watched", "boolean", AllMediaTypes, "True when the requesting user has watched the item", ["true", "false"]),

        // --- System & Storage ---
        new("system.diskFreeGb", "System & Storage", "Free Disk Space (GB)", "number", AllMediaTypes, "Available free disk space in GiB"),
        new("system.diskFreePercent", "System & Storage", "Free Disk Space (%)", "number", AllMediaTypes, "Available free disk percentage (0 - 100)"),
    ];

    private static readonly IReadOnlyList<CleanerrComparatorDescriptor> ComparatorCatalog =
    [
        new("EQUALS", "Equals (=)", ["number", "text", "boolean", "date"]),
        new("NOT_EQUALS", "Not Equals (!=)", ["number", "text", "boolean", "date"]),
        new("BIGGER", "Bigger Than (>)", ["number"]),
        new("SMALLER", "Smaller Than (<)", ["number"]),
        new("CONTAINS", "Contains (Exact Item / Substring)", ["text", "list"]),
        new("NOT_CONTAINS", "Does Not Contain", ["text", "list"]),
        new("CONTAINS_PARTIAL", "Contains Partial Match", ["text", "list"]),
        new("NOT_CONTAINS_PARTIAL", "Does Not Contain Partial", ["text", "list"]),
        new("CONTAINS_ALL", "Contains All (Comma-Separated)", ["list"]),
        new("NOT_CONTAINS_ALL", "Does Not Contain All", ["list"]),
        new("BEFORE", "Before Date / Older Than", ["date"]),
        new("AFTER", "After Date / Newer Than", ["date"]),
        new("IN_LAST", "In Last N Days", ["date"]),
        new("NOT_IN_LAST", "Not In Last N Days (Older Than N Days)", ["date"]),
        new("IN_NEXT", "In Next N Days", ["date"]),
        new("COUNT_EQUALS", "List Count Equals", ["list"]),
        new("COUNT_NOT_EQUALS", "List Count Not Equals", ["list"]),
        new("COUNT_BIGGER", "List Count Bigger Than", ["list"]),
        new("COUNT_SMALLER", "List Count Smaller Than", ["list"]),
        new("EXISTS", "Exists / Is Not Empty", ["number", "text", "boolean", "date", "list"], RequiresValue: false),
        new("NOT_EXISTS", "Does Not Exist / Is Empty", ["number", "text", "boolean", "date", "list"], RequiresValue: false),
    ];

    private static readonly IReadOnlyList<string> ActionModes =
    [
        "delete_files_and_unmonitor",
        "unmonitor_and_delete_existing",
        "delete_work_and_files",
        "delete_show_if_empty",
        "unmonitor_show_if_empty",
        "downgrade_profile",
        "add_import_exclusion",
    ];

    private static readonly IReadOnlyDictionary<string, CleanerrRuleFieldDescriptor> FieldByKey =
        FieldCatalog.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

    public static CleanerrRuleCatalogResponse GetRuleCatalog() =>
        new(FieldCatalog, ComparatorCatalog, ActionModes);

    public static CleanerrEvaluationContext BuildContext(
        IReadOnlyList<CleanerrMediaItem> allMedia,
        DateTimeOffset referenceNow,
        double diskFreeGb,
        double diskFreePercent)
    {
        var byRk = new Dictionary<string, CleanerrMediaItem>(StringComparer.Ordinal);
        var childrenByParent = new Dictionary<string, List<CleanerrMediaItem>>(StringComparer.Ordinal);
        var episodesByShow = new Dictionary<string, List<CleanerrMediaItem>>(StringComparer.Ordinal);

        foreach (var m in allMedia)
        {
            byRk[m.PlexRatingKey] = m;
            if (!string.IsNullOrWhiteSpace(m.ParentRatingKey))
            {
                if (!childrenByParent.TryGetValue(m.ParentRatingKey, out var list))
                {
                    list = [];
                    childrenByParent[m.ParentRatingKey] = list;
                }
                list.Add(m);
            }

            if (m.MediaType == "episode")
            {
                var showRk = m.GrandparentRatingKey ?? m.ParentRatingKey;
                if (!string.IsNullOrWhiteSpace(showRk))
                {
                    if (!episodesByShow.TryGetValue(showRk, out var epList))
                    {
                        epList = [];
                        episodesByShow[showRk] = epList;
                    }
                    epList.Add(m);
                }
            }
        }

        return new CleanerrEvaluationContext(
            allMedia,
            byRk,
            childrenByParent.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<CleanerrMediaItem>)kv.Value, StringComparer.Ordinal),
            episodesByShow.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<CleanerrMediaItem>)kv.Value, StringComparer.Ordinal),
            referenceNow,
            diskFreeGb,
            diskFreePercent);
    }

    public static IReadOnlyList<CleanerrWorkflowTemplate> GetMaintainerrRulePresets()
    {
        var mondayMoviesSpec = new CleanerrRuleGroupSpec(
            TargetMediaType: "movie",
            LibraryIds: null,
            GraceDays: 7,
            ActionMode: "delete_files_and_unmonitor",
            Sections:
            [
                new CleanerrRuleSectionDto(
                    SectionOperator: "AND",
                    Title: "Inactive Movies (> 365d) Without Active Playback or Hardlinks",
                    Rules:
                    [
                        new(null, "plex.daysSinceLastActivity", "BIGGER", "365"),
                        new("AND", "plex.isInProgress", "EQUALS", "false"),
                        new("AND", "plex.isWatchlisted", "EQUALS", "false"),
                        new("AND", "file.isHardlinked", "EQUALS", "false"),
                    ]),
            ],
            CleanEmptyFolders: true,
            ResetRequests: true);

        var staleSeriesSpec = new CleanerrRuleGroupSpec(
            TargetMediaType: "show",
            LibraryIds: null,
            GraceDays: 7,
            ActionMode: "delete_files_and_unmonitor",
            Sections:
            [
                new CleanerrRuleSectionDto(
                    SectionOperator: "AND",
                    Title: "Stale Watched TV Series (> 365d) Without Hardlinks",
                    Rules:
                    [
                        new(null, "plex.viewCount", "BIGGER", "0"),
                        new("AND", "plex.daysSinceLastViewed", "BIGGER", "365"),
                        new("AND", "plex.isInProgress", "EQUALS", "false"),
                        new("AND", "file.isHardlinked", "EQUALS", "false"),
                    ]),
            ],
            CleanEmptyFolders: true,
            ResetRequests: true);

        var liveActionVsAnimeSpec = new CleanerrRuleGroupSpec(
            TargetMediaType: "movie",
            LibraryIds: null,
            GraceDays: 14,
            ActionMode: "delete_files_and_unmonitor",
            Sections:
            [
                new CleanerrRuleSectionDto(
                    SectionOperator: "AND",
                    Title: "Watched Live-Action Movies > 90d (Keep Anime & Favorites)",
                    Rules:
                    [
                        new(null, "catalog.classification", "EQUALS", "live-action-movie"),
                        new("AND", "plex.isWatched", "EQUALS", "true"),
                        new("AND", "plex.daysSinceLastViewed", "BIGGER", "90"),
                        new("AND", "catalog.tags", "NOT_CONTAINS", "keep"),
                    ]),
            ],
            CleanEmptyFolders: true,
            ResetRequests: true);

        var diskPressureReliefSpec = new CleanerrRuleGroupSpec(
            TargetMediaType: "movie",
            LibraryIds: null,
            GraceDays: 3,
            ActionMode: "delete_files_and_unmonitor",
            Sections:
            [
                new CleanerrRuleSectionDto(
                    SectionOperator: "AND",
                    Title: "Trigger Only Under Disk Pressure (< 250 GB Free)",
                    Rules:
                    [
                        new(null, "system.diskFreeGb", "SMALLER", "250"),
                        new("AND", "plex.isWatched", "EQUALS", "true"),
                        new("AND", "plex.daysSinceLastViewed", "BIGGER", "30"),
                        new("AND", "file.sizeGb", "BIGGER", "8"),
                        new("AND", "file.isHardlinked", "EQUALS", "false"),
                        new("AND", "catalog.tags", "NOT_CONTAINS", "keep"),
                    ]),
            ],
            CleanEmptyFolders: true,
            ResetRequests: true,
            MinFreeSpaceGbTrigger: 250,
            TargetFreeSpaceGbWatermark: 350);

        return
        [
            new CleanerrWorkflowTemplate(
                Slug: "monday-movies-365d",
                Name: "Monday Movies Cleanup (365d Inactive + 7d Grace)",
                Category: "Monday Presets",
                Description: "Cleanerr + Maintainerr hybrid preset: runs Mondays at 03:00, stages movies unwatched/inactive for >365 days into Leaving Soon (7d grace) with Discord Exclude/Postpone buttons.",
                GraphJson: CompileRuleGroupToSummaryGraph(mondayMoviesSpec, "0 3 * * 1"),
                EditorMode: "rules",
                RuleGroup: mondayMoviesSpec,
                DefaultScheduleCron: "0 3 * * 1"),
            new CleanerrWorkflowTemplate(
                Slug: "stale-tv-series-365d",
                Name: "Stale TV Series Cleanup (Watched >365d + 7d Grace)",
                Category: "Monday Presets",
                Description: "Stages whole TV series watched >365 days ago into Leaving Soon (7d grace), then deletes all series files and unmonitors the series.",
                GraphJson: CompileRuleGroupToSummaryGraph(staleSeriesSpec, "0 3 * * 1"),
                EditorMode: "rules",
                RuleGroup: staleSeriesSpec,
                DefaultScheduleCron: "0 3 * * 1"),
            new CleanerrWorkflowTemplate(
                Slug: "live-action-movies-keep-anime",
                Name: "Watched Live-Action Movies (90d) — Protect Anime & Cartoons",
                Category: "Maintainerr Rules",
                Description: "Uses Managerr's native media taxonomy to clean watched live-action movies after 90 days while leaving Anime Movies and Cartoons untouched.",
                GraphJson: CompileRuleGroupToSummaryGraph(liveActionVsAnimeSpec, "0 4 * * *"),
                EditorMode: "rules",
                RuleGroup: liveActionVsAnimeSpec,
                DefaultScheduleCron: "0 4 * * *"),
            new CleanerrWorkflowTemplate(
                Slug: "disk-pressure-relief-250gb",
                Name: "Disk Pressure Relief (Free Space < 250 GB)",
                Category: "Storage Guards",
                Description: "Storage pressure guard: activates only when system.diskFreeGb drops below 250 GB, staging large (>8 GB) watched movies (>30d) with a 3-day grace period until free space recovers to 350 GB.",
                GraphJson: CompileRuleGroupToSummaryGraph(diskPressureReliefSpec, "0 */6 * * *"),
                EditorMode: "rules",
                RuleGroup: diskPressureReliefSpec,
                DefaultScheduleCron: "0 */6 * * *"),
        ];
    }

    public static JsonObject CompileRuleGroupToSummaryGraph(CleanerrRuleGroupSpec spec, string? scheduleCron = null)
    {
        var nodes = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "src",
                ["data"] = new JsonObject
                {
                    ["label"] = $"Target: {spec.TargetMediaType.ToUpperInvariant()}",
                    ["node_type"] = "source.managerr_catalog",
                    ["config"] = new JsonObject { ["media_type"] = spec.TargetMediaType },
                },
            },
            new JsonObject
            {
                ["id"] = "rules",
                ["data"] = new JsonObject
                {
                    ["label"] = $"Maintainerr Rules ({spec.Sections.Count} section(s))",
                    ["node_type"] = "filter.maintainerr_rules",
                    ["config"] = JsonSerializer.SerializeToNode(spec, JsonOpts)?.AsObject() ?? new JsonObject(),
                },
            },
        };

        var edges = new JsonArray
        {
            new JsonObject { ["source"] = "src", ["target"] = "rules" },
        };

        var prevId = "rules";
        if (spec.GraceDays > 0)
        {
            var graceConfig = new JsonObject { ["grace_days"] = spec.GraceDays };

            nodes.Add(new JsonObject
            {
                ["id"] = "grace",
                ["data"] = new JsonObject
                {
                    ["label"] = $"{spec.GraceDays}d Leaving Soon Grace",
                    ["node_type"] = "queue.grace_period",
                    ["config"] = graceConfig,
                },
            });
            edges.Add(new JsonObject { ["source"] = prevId, ["target"] = "grace" });
            prevId = "grace";
        }

        var actionConfig = new JsonObject
        {
            ["mode"] = spec.ActionMode,
            ["prune_torrent_client"] = spec.PruneTorrentClient,
            ["clean_empty_folders"] = spec.CleanEmptyFolders,
            ["reset_requests"] = spec.ResetRequests,
            ["target_profile_id"] = spec.DowngradeProfileId,
        };
        if (string.Equals(spec.ActionMode, "add_import_exclusion", StringComparison.OrdinalIgnoreCase))
        {
            actionConfig["add_import_exclusion"] = true;
        }

        var actionLabel = string.Equals(spec.ActionMode, "add_import_exclusion", StringComparison.OrdinalIgnoreCase)
            ? "Action: Add Import Exclusion"
            : $"Action: {spec.ActionMode}";

        nodes.Add(new JsonObject
        {
            ["id"] = "act",
            ["data"] = new JsonObject
            {
                ["label"] = actionLabel,
                ["node_type"] = "action.managerr",
                ["config"] = actionConfig,
            },
        });
        edges.Add(new JsonObject { ["source"] = prevId, ["target"] = "act" });

        if (!string.IsNullOrWhiteSpace(scheduleCron))
        {
            nodes.Insert(0, new JsonObject
            {
                ["id"] = "trig",
                ["data"] = new JsonObject
                {
                    ["label"] = $"Schedule ({scheduleCron})",
                    ["node_type"] = "trigger.schedule",
                    ["config"] = new JsonObject { ["cron"] = scheduleCron },
                },
            });
            edges.Insert(0, new JsonObject { ["source"] = "trig", ["target"] = "src" });
        }

        return new JsonObject
        {
            ["nodes"] = nodes,
            ["edges"] = edges,
        };
    }

    public static (bool Matched, string MatchedReason, IReadOnlyList<CleanerrSectionEvaluationTrace> SectionTraces, IReadOnlyList<string> GuardNotes)
        EvaluateItemAgainstRuleGroup(
            CleanerrMediaItem item,
            CleanerrRuleGroupSpec spec,
            CleanerrEvaluationContext ctx)
    {
        var guardNotes = new List<string>();
        var targetType = (spec.TargetMediaType ?? "movie").Trim().ToLowerInvariant();
        if (!string.Equals(item.MediaType, targetType, StringComparison.OrdinalIgnoreCase))
        {
            guardNotes.Add($"Item mediaType '{item.MediaType}' does not match rule target '{targetType}'.");
            return (false, $"Target mediaType mismatch ({item.MediaType} != {targetType})", [], guardNotes);
        }

        if (spec.LibraryIds is { Count: > 0 } && (!item.LibraryId.HasValue || !spec.LibraryIds.Contains(item.LibraryId.Value)))
        {
            guardNotes.Add($"Item library #{item.LibraryId?.ToString(CultureInfo.InvariantCulture) ?? "none"} is outside rule library filter [{string.Join(", ", spec.LibraryIds)}].");
            return (false, "Outside selected libraries", [], guardNotes);
        }

        if (spec.MinFreeSpaceGbTrigger is > 0 && ctx.DiskFreeGb >= spec.MinFreeSpaceGbTrigger.Value)
        {
            guardNotes.Add($"Disk free space ({ctx.DiskFreeGb:F1} GB) is healthy (>= {spec.MinFreeSpaceGbTrigger.Value:F1} GB trigger).");
            return (false, "Disk free space above trigger threshold", [], guardNotes);
        }

        if (item.ActiveStreamCount > 0)
        {
            guardNotes.Add($"Item currently has {item.ActiveStreamCount} active live stream(s).");
        }

        if (!spec.UseRules || spec.Sections.Count == 0)
        {
            return (true, $"Matched target '{targetType}' (all items)", [], guardNotes);
        }

        var sectionTraces = new List<CleanerrSectionEvaluationTrace>(spec.Sections.Count);
        bool? overallPassed = null;
        var matchedSummaries = new List<string>();

        for (var sIdx = 0; sIdx < spec.Sections.Count; sIdx++)
        {
            var sec = spec.Sections[sIdx];
            var secOp = sIdx == 0 ? "AND" : NormalizeLogicalOp(sec.SectionOperator);
            var ruleTraces = new List<CleanerrRuleEvaluationTraceItem>(sec.Rules.Count);
            bool? secPassed = null;

            for (var rIdx = 0; rIdx < sec.Rules.Count; rIdx++)
            {
                var rule = sec.Rules[rIdx];
                var ruleOp = rIdx == 0 ? null : NormalizeLogicalOp(rule.Operator);
                var trace = EvaluateSingleCondition(item, rule, sIdx, rIdx, secOp, ruleOp, ctx);
                ruleTraces.Add(trace);

                secPassed = secPassed is null
                    ? trace.Passed
                    : ruleOp == "OR"
                        ? (secPassed.Value || trace.Passed)
                        : (secPassed.Value && trace.Passed);
            }

            var resolvedSecPassed = secPassed ?? true;
            sectionTraces.Add(new CleanerrSectionEvaluationTrace(
                SectionIndex: sIdx,
                SectionOperator: secOp,
                SectionPassed: resolvedSecPassed,
                Rules: ruleTraces));

            if (resolvedSecPassed && ruleTraces.Count > 0)
            {
                var passedParts = ruleTraces.Where(t => t.Passed).Take(2).Select(t => $"{t.FieldLabel} {t.Comparator.ToLowerInvariant()} {t.ExpectedTarget ?? ""}".Trim());
                matchedSummaries.Add(string.Join(" & ", passedParts));
            }

            overallPassed = overallPassed is null
                ? resolvedSecPassed
                : secOp == "OR"
                    ? (overallPassed.Value || resolvedSecPassed)
                    : (overallPassed.Value && resolvedSecPassed);
        }

        var finalMatch = overallPassed ?? false;
        var reason = finalMatch
            ? (matchedSummaries.Count > 0 ? string.Join(" | ", matchedSummaries) : $"Matched {sectionTraces.Count} rule section(s)")
            : "Did not satisfy boolean rule sections";

        return (finalMatch, reason, sectionTraces, guardNotes);
    }

    private static CleanerrRuleEvaluationTraceItem EvaluateSingleCondition(
        CleanerrMediaItem item,
        CleanerrRuleConditionDto rule,
        int sectionIdx,
        int ruleIdx,
        string sectionOp,
        string? ruleOp,
        CleanerrEvaluationContext ctx)
    {
        var fieldKey = NormalizeFieldAlias((rule.Field ?? "").Trim());
        var descriptor = FieldByKey.GetValueOrDefault(fieldKey);
        var fieldLabel = descriptor?.Label ?? fieldKey;
        var (comparator, coercedValueOverride) = NormalizeComparatorAlias(rule.Comparator, rule.Value);

        var actualObj = ResolveFieldValue(item, fieldKey, ctx);
        var actualDisplay = FormatValueForDisplay(actualObj);

        var isPropComparison = !string.IsNullOrWhiteSpace(rule.SecondField);
        object? targetObj;
        string? targetDisplay;

        if (isPropComparison)
        {
            var secondKey = NormalizeFieldAlias(rule.SecondField!.Trim());
            var secondDesc = FieldByKey.GetValueOrDefault(secondKey);
            targetObj = ResolveFieldValue(item, secondKey, ctx);
            targetDisplay = $"{secondDesc?.Label ?? secondKey} ({FormatValueForDisplay(targetObj)})";
        }
        else
        {
            targetObj = coercedValueOverride ?? rule.Value;
            targetDisplay = coercedValueOverride ?? rule.Value;
        }

        var (passed, explanation) = CompareValues(actualObj, comparator, targetObj, isPropComparison, ctx.ReferenceNow);

        return new CleanerrRuleEvaluationTraceItem(
            SectionIndex: sectionIdx,
            RuleIndex: ruleIdx,
            SectionConnector: sectionOp,
            RuleConnector: ruleOp,
            FieldKey: fieldKey,
            FieldLabel: fieldLabel,
            ActualValue: actualDisplay,
            Comparator: comparator,
            ExpectedTarget: targetDisplay,
            IsPropertyComparison: isPropComparison,
            Passed: passed,
            Explanation: explanation);
    }

    private static string NormalizeFieldAlias(string rawKey) =>
        rawKey.Trim().ToLowerInvariant() switch
        {
            "plex.lastvieweddaysago" => "plex.daysSinceLastActivity",
            "plex.lastvieweddays" => "plex.daysSinceLastViewed",
            "plex.inprogress" => "plex.isInProgress",
            "plex.addeddaysago" => "catalog.addedAgeDays",
            "plex.userrating" => "catalog.rating",
            "plex.label" => "catalog.tags",
            "classification.kind" => "catalog.classification",
            "seerr.dayssincerequest" => "request.requestAgeDays",
            "seerr.requestedby" => "request.requestedBy",
            "arr.filesizegb" => "file.sizeGb",
            "arr.monitored" => "catalog.monitored",
            "media.ishardlinked" => "file.isHardlinked",
            "media.resolution" => "file.resolution",
            "media.videocodec" => "file.videoCodec",
            "system.diskfreespacegb" => "system.diskFreeGb",
            _ => rawKey.Trim(),
        };

    private static (string Comparator, string? CoercedValue) NormalizeComparatorAlias(string? rawComp, string? rawVal)
    {
        var norm = (rawComp ?? "EQUALS").Trim().ToUpperInvariant();
        return norm switch
        {
            "GREATER_THAN" or "GT" or ">" => ("BIGGER", null),
            "GREATER_THAN_OR_EQUAL" or "GTE" or ">=" => ("BIGGER_OR_EQUAL", null),
            "LESS_THAN" or "LT" or "<" => ("SMALLER", null),
            "LESS_THAN_OR_EQUAL" or "LTE" or "<=" => ("SMALLER_OR_EQUAL", null),
            "IS_TRUE" => ("EQUALS", "true"),
            "IS_FALSE" => ("EQUALS", "false"),
            "IS_EMPTY" => ("NOT_EXISTS", null),
            "IS_NOT_EMPTY" => ("EXISTS", null),
            _ => (norm, rawVal),
        };
    }

    public static object? ResolveFieldValue(
        CleanerrMediaItem item,
        string fieldKey,
        CleanerrEvaluationContext ctx)
    {
        var ancestors = GetItemAncestors(item, ctx);
        var parentShow = ancestors.FirstOrDefault(a => a.MediaType == "show");
        var key = NormalizeFieldAlias(fieldKey).ToLowerInvariant();

        return key switch
        {
            "catalog.title" => item.Title,
            "catalog.mediatype" => item.MediaType,
            "catalog.classification" => ResolveClassification(item, ancestors),
            "catalog.tags" => ancestors.SelectMany(a => a.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            "catalog.genres" => ancestors.SelectMany(a => a.Genres).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            "catalog.year" => item.Year ?? parentShow?.Year,
            "catalog.addedat" => ParseIso(item.AddedAt),
            "catalog.addedagedays" => ParseIso(item.AddedAt) is { } addDto ? Math.Max(0, (ctx.ReferenceNow - addDto).TotalDays) : null,
            "catalog.monitored" => item.Monitored,
            "catalog.status" => item.WorkStatus ?? parentShow?.WorkStatus ?? "released",
            "catalog.rating" => item.Rating ?? parentShow?.Rating,
            "catalog.customformatscore" => (double)item.CustomFormatScore,
            "catalog.libraryid" => item.LibraryId.HasValue ? (double)item.LibraryId.Value : null,

            "file.sizegb" => Math.Round(item.SizeBytes / (1024.0 * 1024.0 * 1024.0), 3),
            "file.sizebytes" => (double)item.SizeBytes,
            "file.resolution" => item.Resolution,
            "file.videocodec" => item.VideoCodec,
            "file.audiolanguages" => (item.AudioLanguages ?? []).ToList(),
            "file.audiolanguagecount" => (double)(item.AudioLanguages?.Count ?? 0),
            "file.ishardlinked" => item.IsHardlinked,
            "file.hasfile" => item.SizeBytes > 0 || !string.IsNullOrWhiteSpace(item.FilePath),

            "plex.viewcount" => (double)ResolveEffectiveViewCount(item, ctx),
            "plex.iswatched" => ResolveEffectiveViewCount(item, ctx) > 0 || ResolveEffectiveLastViewed(item, ctx).HasValue,
            "plex.lastviewedat" => ResolveEffectiveLastViewed(item, ctx),
            "plex.dayssincelastviewed" => ResolveEffectiveLastViewed(item, ctx) is { } lv
                ? Math.Max(0, Math.Round((ctx.ReferenceNow - lv).TotalDays, 2))
                : null,
            "plex.dayssincelastactivity" => ResolveEffectiveLastViewed(item, ctx) is { } lvAct
                ? Math.Max(0, Math.Round((ctx.ReferenceNow - lvAct).TotalDays, 2))
                : ParseIso(item.AddedAt) is { } addAct
                    ? Math.Max(0, Math.Round((ctx.ReferenceNow - addAct).TotalDays, 2))
                    : 0.0,
            "plex.viewedbyusers" => ResolveEffectiveViewedBy(item, ctx),
            "plex.viewedbycount" => (double)ResolveEffectiveViewedBy(item, ctx).Count,
            "plex.isinprogress" => ResolveIsInProgress(item, ctx),
            "plex.activestreamcount" => (double)item.ActiveStreamCount,
            "plex.iswatchlisted" => ancestors.Any(a => a.IsWatchlisted),

            "tv.seasonnumber" => item.SeasonNumber.HasValue ? (double)item.SeasonNumber.Value : null,
            "tv.episodenumber" => item.EpisodeNumber.HasValue ? (double)item.EpisodeNumber.Value : null,
            "tv.totalseasons" => ResolveTotalSeasons(item, ctx),
            "tv.partoflatestseason" => ResolveIsPartOfLatestSeason(item, ctx),
            "tv.seasonepisodecount" => (double)GetChildEpisodes(item, ctx).Count,
            "tv.seasonwatchedepisodes" => (double)GetChildEpisodes(item, ctx).Count(IsEpisodeWatched),
            "tv.seasonwatchedpercent" => ComputeWatchedPercent(item, ctx),
            "tv.iscompletedseason" => ComputeIsCompletedSeason(item, ctx),
            "tv.seriesactivewatchdays" => ComputeSeriesActiveWatchDays(item, ctx),
            "tv.allepisodesseenbyusers" => ComputeAllEpisodesSeenByUsers(item, ctx),
            "tv.seasonfilerankfromnewest" => ComputeSeasonRankFromNewest(item, ctx),

            "request.isrequested" => ancestors.Any(a => !string.IsNullOrWhiteSpace(a.SeerrRequestedBy) || a.SeerrId.HasValue),
            "request.requestedby" => ancestors.Select(a => a.SeerrRequestedBy).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
            "request.requestedat" => ancestors.Select(a => ParseIso(a.SeerrRequestedAt)).FirstOrDefault(d => d.HasValue),
            "request.requestagedays" => ancestors.Select(a => ParseIso(a.SeerrRequestedAt)).FirstOrDefault(d => d.HasValue) is { } reqDto
                ? Math.Max(0, Math.Round((ctx.ReferenceNow - reqDto).TotalDays, 2))
                : null,
            "request.requesterwatched" => ancestors.Any(a => a.RequesterWatched),

            "system.diskfreegb" => Math.Round(ctx.DiskFreeGb, 2),
            "system.diskfreepercent" => Math.Round(ctx.DiskFreePercent, 2),
            _ => null,
        };
    }

    private static (bool Passed, string Explanation) CompareValues(
        object? actual,
        string comparator,
        object? expectedRaw,
        bool isPropComparison,
        DateTimeOffset now)
    {
        if (comparator == "EXISTS")
        {
            var exists = ValueExists(actual);
            return (exists, exists ? "Value exists" : "Value is null or empty");
        }

        if (comparator == "NOT_EXISTS")
        {
            var empty = !ValueExists(actual);
            return (empty, empty ? "Value is empty" : "Value is present");
        }

        var expectedStr = expectedRaw?.ToString()?.Trim() ?? "";

        // List count comparators
        if (comparator is "COUNT_EQUALS" or "COUNT_NOT_EQUALS" or "COUNT_BIGGER" or "COUNT_SMALLER")
        {
            var count = actual is IReadOnlyCollection<string> col ? col.Count : (ValueExists(actual) ? 1 : 0);
            if (!TryCoerceDouble(expectedRaw, out var targetNum))
            {
                return (false, $"Invalid numeric count threshold '{expectedStr}'");
            }

            var ok = comparator switch
            {
                "COUNT_EQUALS" => Math.Abs(count - targetNum) < 0.0001,
                "COUNT_NOT_EQUALS" => Math.Abs(count - targetNum) >= 0.0001,
                "COUNT_BIGGER" => count > targetNum,
                "COUNT_SMALLER" => count < targetNum,
                _ => false,
            };
            return (ok, $"List count is {count} ({comparator} {targetNum})");
        }

        // Date comparators
        if (actual is DateTimeOffset || comparator is "BEFORE" or "AFTER" or "IN_LAST" or "NOT_IN_LAST" or "IN_NEXT")
        {
            var dto = actual as DateTimeOffset? ?? ParseIso(actual?.ToString());
            if (!dto.HasValue)
            {
                if (comparator == "NOT_IN_LAST")
                {
                    return (true, "Timestamp is never/empty (satisfies NOT_IN_LAST)");
                }
                return (false, "Timestamp is empty/null");
            }

            if (comparator is "IN_LAST" or "NOT_IN_LAST" or "IN_NEXT")
            {
                var days = ParseDurationDays(expectedStr);
                if (!days.HasValue)
                {
                    return (false, $"Invalid day duration '{expectedStr}'");
                }

                var ok = comparator switch
                {
                    "IN_LAST" => dto.Value >= now.AddDays(-days.Value) && dto.Value <= now.AddDays(1),
                    "NOT_IN_LAST" => dto.Value < now.AddDays(-days.Value),
                    "IN_NEXT" => dto.Value >= now && dto.Value <= now.AddDays(days.Value),
                    _ => false,
                };
                return (ok, $"Timestamp {dto.Value:yyyy-MM-dd} compared against {days.Value}d window ({comparator})");
            }

            DateTimeOffset? targetDto = expectedRaw is DateTimeOffset d2
                ? d2
                : ParseIso(expectedStr) ?? (ParseDurationDays(expectedStr) is { } relDays ? now.AddDays(-relDays) : null);

            if (!targetDto.HasValue)
            {
                return (false, $"Could not parse target date '{expectedStr}'");
            }

            var dateOk = comparator switch
            {
                "BEFORE" => dto.Value < targetDto.Value,
                "AFTER" => dto.Value > targetDto.Value,
                "EQUALS" => Math.Abs((dto.Value - targetDto.Value).TotalDays) < 1.0,
                "NOT_EQUALS" => Math.Abs((dto.Value - targetDto.Value).TotalDays) >= 1.0,
                _ => false,
            };
            return (dateOk, $"Date {dto.Value:yyyy-MM-dd} {comparator} {targetDto.Value:yyyy-MM-dd}");
        }

        // List membership comparators
        if (actual is IReadOnlyList<string> listVal)
        {
            var needleTokens = expectedStr
                .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (needleTokens.Count == 0 && !string.IsNullOrWhiteSpace(expectedStr))
            {
                needleTokens.Add(expectedStr);
            }

            var ok = comparator switch
            {
                "CONTAINS" or "EQUALS" => needleTokens.Any(n => listVal.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))),
                "NOT_CONTAINS" or "NOT_EQUALS" => needleTokens.All(n => listVal.All(x => !string.Equals(x, n, StringComparison.OrdinalIgnoreCase))),
                "CONTAINS_PARTIAL" => needleTokens.Any(n => listVal.Any(x => x.Contains(n, StringComparison.OrdinalIgnoreCase))),
                "NOT_CONTAINS_PARTIAL" => needleTokens.All(n => listVal.All(x => !x.Contains(n, StringComparison.OrdinalIgnoreCase))),
                "CONTAINS_ALL" => needleTokens.Count > 0 && needleTokens.All(n => listVal.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))),
                "NOT_CONTAINS_ALL" => needleTokens.Count == 0 || !needleTokens.All(n => listVal.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))),
                _ => false,
            };
            return (ok, $"[{string.Join(", ", listVal)}] {comparator} [{string.Join(", ", needleTokens)}]");
        }

        // Boolean comparison
        if (actual is bool boolVal)
        {
            var targetBool = expectedRaw is bool b2 ? b2 : ParseBoolFlexible(expectedStr);
            if (!targetBool.HasValue)
            {
                return (false, $"Invalid boolean target '{expectedStr}'");
            }

            var ok = comparator switch
            {
                "EQUALS" => boolVal == targetBool.Value,
                "NOT_EQUALS" => boolVal != targetBool.Value,
                _ => false,
            };
            return (ok, $"{boolVal.ToString().ToLowerInvariant()} {comparator} {targetBool.Value.ToString().ToLowerInvariant()}");
        }

        // Numeric comparison
        if (TryCoerceDouble(actual, out var actualNum) && TryCoerceDouble(expectedRaw, out var expectedNum))
        {
            var ok = comparator switch
            {
                "EQUALS" => Math.Abs(actualNum - expectedNum) < 0.0001,
                "NOT_EQUALS" => Math.Abs(actualNum - expectedNum) >= 0.0001,
                "BIGGER" => actualNum > expectedNum,
                "BIGGER_OR_EQUAL" => actualNum >= expectedNum - 0.0001,
                "SMALLER" => actualNum < expectedNum,
                "SMALLER_OR_EQUAL" => actualNum <= expectedNum + 0.0001,
                _ => false,
            };
            return (ok, $"{actualNum:0.##} {comparator} {expectedNum:0.##}");
        }

        if (actual is null)
        {
            var nullOk = comparator is "NOT_EQUALS" or "NOT_CONTAINS" or "NOT_CONTAINS_PARTIAL";
            return (nullOk, $"Actual value is null ({comparator} '{expectedStr}')");
        }

        // Text fallback comparison
        var actualText = actual.ToString() ?? "";
        var textOk = comparator switch
        {
            "EQUALS" => string.Equals(actualText, expectedStr, StringComparison.OrdinalIgnoreCase),
            "NOT_EQUALS" => !string.Equals(actualText, expectedStr, StringComparison.OrdinalIgnoreCase),
            "CONTAINS" or "CONTAINS_PARTIAL" => actualText.Contains(expectedStr, StringComparison.OrdinalIgnoreCase),
            "NOT_CONTAINS" or "NOT_CONTAINS_PARTIAL" => !actualText.Contains(expectedStr, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        return (textOk, $"'{actualText}' {comparator} '{expectedStr}'" + (isPropComparison ? " (property comparison)" : ""));
    }

    public static string ExportToYaml(
        string name,
        string description,
        string? scheduleCron,
        CleanerrRuleGroupSpec spec) =>
        ExportToYaml(name, description, scheduleCron, "rules", spec, null);

    public static string ExportToYaml(
        string name,
        string description,
        string? scheduleCron,
        string editorMode,
        CleanerrRuleGroupSpec? spec,
        JsonObject? graphJson)
    {
        var isDag = string.Equals(editorMode?.Trim(), "dag", StringComparison.OrdinalIgnoreCase) || spec is null;
        var sb = new StringBuilder();

        if (isDag)
        {
            sb.AppendLine("# Managerr Cleanerr 2.0 Workflow (DAG Mode)");
            sb.AppendLine("editor_mode: \"dag\"");
            sb.AppendLine($"name: {YamlQuote(name)}");
            sb.AppendLine($"description: {YamlQuote(description)}");
            if (!string.IsNullOrWhiteSpace(scheduleCron))
            {
                sb.AppendLine($"schedule_cron: {YamlQuote(scheduleCron)}");
            }

            var graph = graphJson ?? CreateEmptyGraph();
            var formattedJson = graph.ToJsonString(JsonOpts).Replace("\r\n", "\n");
            sb.AppendLine("graph_json: |");
            foreach (var line in formattedJson.Split('\n'))
            {
                sb.AppendLine($"  {line}");
            }

            return sb.ToString();
        }

        sb.AppendLine("# Managerr Cleanerr 2.0 Rule Group (Maintainerr-Compatible YAML)");
        sb.AppendLine("editor_mode: \"rules\"");
        sb.AppendLine($"name: {YamlQuote(name)}");
        sb.AppendLine($"description: {YamlQuote(description)}");
        sb.AppendLine($"mediaType: {YamlQuote(spec!.TargetMediaType)}");
        sb.AppendLine($"graceDays: {spec.GraceDays}");
        sb.AppendLine($"actionMode: {YamlQuote(spec.ActionMode)}");
        if (!string.IsNullOrWhiteSpace(scheduleCron))
        {
            sb.AppendLine($"schedule_cron: {YamlQuote(scheduleCron)}");
        }
        if (spec.LibraryIds is { Count: > 0 })
        {
            sb.AppendLine($"libraryIds: {YamlQuote(string.Join(", ", spec.LibraryIds))}");
        }
        if (!spec.UseRules)
        {
            sb.AppendLine("useRules: false");
        }
        sb.AppendLine($"cleanEmptyFolders: {(spec.CleanEmptyFolders ? "true" : "false")}");
        sb.AppendLine($"resetRequests: {(spec.ResetRequests ? "true" : "false")}");
        sb.AppendLine($"pruneTorrentClient: {(spec.PruneTorrentClient ? "true" : "false")}");
        if (spec.DowngradeProfileId.HasValue)
            sb.AppendLine($"downgradeProfileId: {spec.DowngradeProfileId.Value}");
        if (spec.MaxReclaimGb.HasValue)
            sb.AppendLine($"maxReclaimGb: {spec.MaxReclaimGb.Value.ToString(CultureInfo.InvariantCulture)}");
        if (spec.MinFreeSpaceGbTrigger.HasValue)
            sb.AppendLine($"minFreeSpaceGbTrigger: {spec.MinFreeSpaceGbTrigger.Value.ToString(CultureInfo.InvariantCulture)}");
        if (spec.TargetFreeSpaceGbWatermark.HasValue)
            sb.AppendLine($"targetFreeSpaceGbWatermark: {spec.TargetFreeSpaceGbWatermark.Value.ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine("sections:");
        foreach (var sec in spec.Sections)
        {
            sb.AppendLine($"  - sectionOperator: {YamlQuote(sec.SectionOperator)}");
            if (!string.IsNullOrWhiteSpace(sec.Title))
                sb.AppendLine($"    title: {YamlQuote(sec.Title)}");
            sb.AppendLine("    rules:");
            foreach (var r in sec.Rules)
            {
                sb.AppendLine($"      - field: {YamlQuote(r.Field)}");
                sb.AppendLine($"        comparator: {YamlQuote(r.Comparator)}");
                if (!string.IsNullOrWhiteSpace(r.Operator))
                    sb.AppendLine($"        operator: {YamlQuote(r.Operator)}");
                if (r.Value is not null)
                    sb.AppendLine($"        value: {YamlQuote(r.Value)}");
                if (!string.IsNullOrWhiteSpace(r.SecondField))
                    sb.AppendLine($"        secondField: {YamlQuote(r.SecondField)}");
            }
        }

        return sb.ToString();
    }

    public static (string Name, string Description, string? ScheduleCron, string EditorMode, CleanerrRuleGroupSpec? Spec, JsonObject? GraphJson)
        ImportFromYaml(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            throw new ArgumentException("YAML/JSON workflow or rule definition cannot be empty.");
        }

        var trimmed = rawText.Trim();
        if (trimmed.StartsWith('{'))
        {
            return ImportFromJson(trimmed);
        }

        string name = "Imported Maintainerr Rule";
        string description = "";
        string? editorMode = null;
        string targetMediaType = "movie";
        int graceDays = 7;
        string actionMode = "delete_files_and_unmonitor";
        string? scheduleCron = null;
        List<long>? libraryIds = null;
        bool? useRulesOverride = null;
        bool cleanEmptyFolders = true;
        bool resetRequests = true;
        bool pruneTorrent = false;
        long? downgradeProfileId = null;
        double? maxReclaimGb = null;
        double? minFreeGb = null;
        double? targetFreeGb = null;
        JsonObject? parsedGraphJson = null;
        var hasRuleFields = false;

        var sections = new List<(string Op, string? Title, List<Dictionary<string, string>> Rules)>();
        Dictionary<string, string>? currentRule = null;

        var lines = rawText.Replace("\r\n", "\n").Split('\n');
        var inSections = false;
        var inRules = false;
        var inGraphJsonBlock = false;
        var graphJsonLines = new List<string>();

        void FlushGraphJsonBlock()
        {
            if (!inGraphJsonBlock) return;
            inGraphJsonBlock = false;
            var blockText = string.Join('\n', graphJsonLines).Trim();
            graphJsonLines.Clear();
            if (!string.IsNullOrWhiteSpace(blockText))
            {
                parsedGraphJson = ParseGraphJsonObject(blockText);
            }
        }

        foreach (var rawLine in lines)
        {
            if (inGraphJsonBlock)
            {
                var lineIndent = rawLine.TakeWhile(c => c == ' ').Count();
                if (string.IsNullOrWhiteSpace(rawLine) || lineIndent > 0)
                {
                    graphJsonLines.Add(rawLine.Trim());
                    continue;
                }

                FlushGraphJsonBlock();
            }

            var commentFree = StripYamlComment(rawLine);
            if (string.IsNullOrWhiteSpace(commentFree)) continue;

            var indent = commentFree.TakeWhile(c => c == ' ').Count();
            var content = commentFree.Trim();

            if (indent == 0)
            {
                inRules = false;
                if (content.Equals("sections:", StringComparison.OrdinalIgnoreCase) || content.Equals("rules:", StringComparison.OrdinalIgnoreCase))
                {
                    inSections = true;
                    hasRuleFields = true;
                    if (content.Equals("rules:", StringComparison.OrdinalIgnoreCase) && sections.Count == 0)
                    {
                        sections.Add(("AND", null, []));
                        inRules = true;
                    }
                    continue;
                }

                inSections = false;
                if (TrySplitYamlKv(content, out var k, out var v))
                {
                    switch (k.ToLowerInvariant())
                    {
                        case "editormode":
                        case "editor_mode":
                            editorMode = v.Trim().ToLowerInvariant();
                            break;
                        case "name":
                            name = v;
                            break;
                        case "description":
                            description = v;
                            break;
                        case "graphjson":
                        case "graph_json":
                            if (v is "|" or "|-" or ">" or ">-" or "")
                            {
                                inGraphJsonBlock = true;
                                graphJsonLines.Clear();
                            }
                            else
                            {
                                parsedGraphJson = ParseGraphJsonObject(v);
                            }
                            break;
                        case "mediatype":
                        case "targetmediatype":
                            targetMediaType = NormalizeMediaType(v);
                            hasRuleFields = true;
                            break;
                        case "gracedays":
                        case "deleteafterdays":
                        case "collectiondeleteafterdays":
                            if (int.TryParse(v, out var gd)) graceDays = Math.Max(0, gd);
                            hasRuleFields = true;
                            break;
                        case "actionmode":
                            actionMode = v;
                            hasRuleFields = true;
                            break;
                        case "schedulecron":
                        case "schedule_cron":
                        case "cron":
                            scheduleCron = string.IsNullOrWhiteSpace(v) ? null : v;
                            break;
                        case "plexcollectionname":
                        case "plex_collection_name":
                        case "collectionname":
                        case "collection_name":
                            // Legacy collection keys are ignored; Cleanerr uses native Leaving Soon queue
                            break;
                        case "libraryids":
                        case "library_ids":
                            libraryIds = ParseLongList(v);
                            hasRuleFields = true;
                            break;
                        case "userules":
                        case "use_rules":
                            useRulesOverride = ParseBoolFlexible(v);
                            hasRuleFields = true;
                            break;
                        case "cleanemptyfolders":
                            cleanEmptyFolders = ParseBoolFlexible(v) ?? true;
                            break;
                        case "resetrequests":
                            resetRequests = ParseBoolFlexible(v) ?? true;
                            break;
                        case "prunetorrentclient":
                            pruneTorrent = ParseBoolFlexible(v) ?? false;
                            break;
                        case "downgradeprofileid":
                            if (long.TryParse(v, out var dpid)) downgradeProfileId = dpid;
                            break;
                        case "maxreclaimgb":
                            if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var mrg)) maxReclaimGb = mrg;
                            break;
                        case "minfreespacegbtrigger":
                            if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var mfg)) minFreeGb = mfg;
                            break;
                        case "targetfreespacegbwatermark":
                            if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var tfg)) targetFreeGb = tfg;
                            break;
                    }
                }
                continue;
            }

            if (!inSections) continue;

            if (indent is >= 2 and <= 4 && content.StartsWith("- ", StringComparison.Ordinal) && (!inRules || content.Contains("sectionOperator", StringComparison.OrdinalIgnoreCase)))
            {
                inRules = false;
                currentRule = null;
                var rest = content[2..].Trim();
                var secOp = "AND";
                string? secTitle = null;
                if (TrySplitYamlKv(rest, out var sk, out var sv))
                {
                    if (sk.Equals("sectionOperator", StringComparison.OrdinalIgnoreCase)) secOp = NormalizeLogicalOp(sv);
                    else if (sk.Equals("title", StringComparison.OrdinalIgnoreCase)) secTitle = sv;
                    else if (sk.Equals("field", StringComparison.OrdinalIgnoreCase))
                    {
                        if (sections.Count == 0) sections.Add(("AND", null, []));
                        currentRule = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [sk] = sv };
                        sections[^1].Rules.Add(currentRule);
                        inRules = true;
                        continue;
                    }
                }
                sections.Add((secOp, secTitle, []));
                continue;
            }

            if (content.Equals("rules:", StringComparison.OrdinalIgnoreCase))
            {
                if (sections.Count == 0) sections.Add(("AND", null, []));
                inRules = true;
                currentRule = null;
                continue;
            }

            if (sections.Count > 0 && !inRules && TrySplitYamlKv(content, out var secPropK, out var secPropV))
            {
                var last = sections[^1];
                if (secPropK.Equals("sectionOperator", StringComparison.OrdinalIgnoreCase))
                    sections[^1] = (NormalizeLogicalOp(secPropV), last.Title, last.Rules);
                else if (secPropK.Equals("title", StringComparison.OrdinalIgnoreCase))
                    sections[^1] = (last.Op, secPropV, last.Rules);
                continue;
            }

            if (content.StartsWith("- ", StringComparison.Ordinal))
            {
                if (sections.Count == 0) sections.Add(("AND", null, []));
                inRules = true;
                currentRule = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[^1].Rules.Add(currentRule);
                var rest = content[2..].Trim();
                if (TrySplitYamlKv(rest, out var rk, out var rv))
                {
                    currentRule[rk] = rv;
                }
                continue;
            }

            if (currentRule is not null && TrySplitYamlKv(content, out var propK, out var propV))
            {
                currentRule[propK] = propV;
            }
        }

        FlushGraphJsonBlock();

        var isDag = string.Equals(editorMode, "dag", StringComparison.OrdinalIgnoreCase) ||
                    (parsedGraphJson is not null && !hasRuleFields && sections.Count == 0);
        if (isDag)
        {
            if (name == "Imported Maintainerr Rule")
            {
                name = "Imported DAG Workflow";
            }
            return (name, description, scheduleCron, "dag", null, parsedGraphJson ?? CreateEmptyGraph());
        }

        var builtSections = sections
            .Where(s => s.Rules.Count > 0)
            .Select(s => new CleanerrRuleSectionDto(
                SectionOperator: s.Op,
                Title: s.Title,
                Rules: s.Rules.Select((r, idx) => new CleanerrRuleConditionDto(
                    Operator: idx == 0 ? null : NormalizeLogicalOp(r.GetValueOrDefault("operator") ?? r.GetValueOrDefault("action")),
                    Field: r.GetValueOrDefault("field") ?? r.GetValueOrDefault("firstVal") ?? "plex.daysSinceLastActivity",
                    Comparator: (r.GetValueOrDefault("comparator") ?? r.GetValueOrDefault("operatorType") ?? "EQUALS").ToUpperInvariant(),
                    Value: r.GetValueOrDefault("value") ?? r.GetValueOrDefault("customVal"),
                    SecondField: r.GetValueOrDefault("secondField") ?? r.GetValueOrDefault("lastVal"))).ToList()))
            .ToList();

        var spec = new CleanerrRuleGroupSpec(
            TargetMediaType: targetMediaType,
            LibraryIds: libraryIds is { Count: > 0 } ? libraryIds : null,
            GraceDays: graceDays,
            ActionMode: actionMode,
            Sections: builtSections,
            PruneTorrentClient: pruneTorrent,
            CleanEmptyFolders: cleanEmptyFolders,
            ResetRequests: resetRequests,
            DowngradeProfileId: downgradeProfileId,
            MaxReclaimGb: maxReclaimGb,
            MinFreeSpaceGbTrigger: minFreeGb,
            TargetFreeSpaceGbWatermark: targetFreeGb,
            UseRules: useRulesOverride ?? (builtSections.Count > 0));

        return (name, description, scheduleCron, "rules", spec, parsedGraphJson ?? CompileRuleGroupToSummaryGraph(spec, scheduleCron));
    }

    private static (string Name, string Description, string? ScheduleCron, string EditorMode, CleanerrRuleGroupSpec? Spec, JsonObject? GraphJson)
        ImportFromJson(string trimmed)
    {
        var json = JsonNode.Parse(trimmed)?.AsObject()
                   ?? throw new ArgumentException("Invalid JSON workflow/rule definition.");

        var jName = json["name"]?.ToString() ?? "Imported Rule Group";
        var jDesc = json["description"]?.ToString() ?? "";
        var rawCron = (json["scheduleCron"] ?? json["schedule_cron"] ?? json["cron"])?.ToString();
        var jCron = string.IsNullOrWhiteSpace(rawCron) ? null : rawCron;
        var jMode = (json["editorMode"] ?? json["editor_mode"])?.ToString()?.Trim().ToLowerInvariant();

        JsonObject? jGraph = null;
        if ((json["graphJson"] ?? json["graph_json"]) is JsonObject gObj)
        {
            jGraph = gObj.DeepClone().AsObject();
        }
        else if (json["nodes"] is JsonArray nodesArr)
        {
            jGraph = new JsonObject
            {
                ["nodes"] = nodesArr.DeepClone(),
                ["edges"] = json["edges"]?.DeepClone() ?? new JsonArray(),
            };
        }

        var hasRulePayload = json["ruleGroup"] is not null
            || json["sections"] is not null
            || json["rules"] is not null
            || json["targetMediaType"] is not null
            || json["TargetMediaType"] is not null;

        if (jMode == "dag" || (jGraph is not null && !hasRulePayload))
        {
            if (json["name"] is null)
            {
                jName = "Imported DAG Workflow";
            }
            return (jName, jDesc, jCron, "dag", null, jGraph ?? CreateEmptyGraph());
        }

        CleanerrRuleGroupSpec parsedSpec;
        if (json["ruleGroup"] is JsonObject rgNode)
        {
            parsedSpec = JsonSerializer.Deserialize<CleanerrRuleGroupSpec>(rgNode.ToJsonString(), JsonOpts)
                         ?? throw new ArgumentException("Could not parse ruleGroup from JSON.");
        }
        else if (json["targetMediaType"] is not null || json["TargetMediaType"] is not null)
        {
            parsedSpec = JsonSerializer.Deserialize<CleanerrRuleGroupSpec>(json.ToJsonString(), JsonOpts)
                         ?? throw new ArgumentException("Could not parse ruleGroup from JSON.");
        }
        else
        {
            parsedSpec = ParseMaintainerrJsonRuleGroup(json);
        }

        parsedSpec = parsedSpec with { TargetMediaType = NormalizeMediaType(parsedSpec.TargetMediaType) };

        return (jName, jDesc, jCron, "rules", parsedSpec, jGraph ?? CompileRuleGroupToSummaryGraph(parsedSpec, jCron));
    }

    private static CleanerrRuleGroupSpec ParseMaintainerrJsonRuleGroup(JsonObject json)
    {
        var mediaRaw = (json["mediaType"] ?? json["dataType"])?.ToString() ?? "movie";
        var targetMediaType = NormalizeMediaType(mediaRaw);
        var graceDays = ReadJsonInt(json["graceDays"] ?? json["deleteAfterDays"] ?? json["collectionDeleteAfterDays"]) ?? 7;
        var actionMode = json["actionMode"]?.ToString() ?? "delete_files_and_unmonitor";
        var cleanEmptyFolders = ReadJsonBool(json["cleanEmptyFolders"]) ?? true;
        var resetRequests = ReadJsonBool(json["resetRequests"]) ?? true;
        var pruneTorrent = ReadJsonBool(json["pruneTorrentClient"]) ?? false;
        var useRules = ReadJsonBool(json["useRules"]);

        var builtSections = new List<CleanerrRuleSectionDto>();

        if (json["sections"] is JsonArray secArr)
        {
            foreach (var secNode in secArr.OfType<JsonObject>())
            {
                var secOp = NormalizeLogicalOp((secNode["sectionOperator"] ?? secNode["operator"])?.ToString());
                var secTitle = secNode["title"]?.ToString();
                var secRules = new List<CleanerrRuleConditionDto>();
                if (secNode["rules"] is JsonArray rArr)
                {
                    foreach (var rObj in rArr.OfType<JsonObject>())
                    {
                        secRules.Add(ParseJsonRuleCondition(rObj, secRules.Count == 0));
                    }
                }
                if (secRules.Count > 0)
                {
                    builtSections.Add(new CleanerrRuleSectionDto(secOp, secRules, secTitle));
                }
            }
        }
        else if (json["rules"] is JsonArray flatRules)
        {
            var grouped = new Dictionary<int, List<CleanerrRuleConditionDto>>();
            foreach (var rObj in flatRules.OfType<JsonObject>())
            {
                var sectionIdx = ReadJsonInt(rObj["section"]) ?? 0;
                if (!grouped.TryGetValue(sectionIdx, out var list))
                {
                    list = [];
                    grouped[sectionIdx] = list;
                }
                list.Add(ParseJsonRuleCondition(rObj, list.Count == 0));
            }

            foreach (var kv in grouped.OrderBy(k => k.Key))
            {
                if (kv.Value.Count > 0)
                {
                    builtSections.Add(new CleanerrRuleSectionDto("AND", kv.Value));
                }
            }
        }

        if (builtSections.Count == 0 && json["ruleGroup"] is null && json["rules"] is null && json["sections"] is null)
        {
            throw new ArgumentException("Could not parse ruleGroup or Maintainerr rules from JSON.");
        }

        return new CleanerrRuleGroupSpec(
            TargetMediaType: targetMediaType,
            LibraryIds: null,
            GraceDays: Math.Max(0, graceDays),
            ActionMode: actionMode,
            Sections: builtSections,
            PruneTorrentClient: pruneTorrent,
            CleanEmptyFolders: cleanEmptyFolders,
            ResetRequests: resetRequests,
            UseRules: useRules ?? (builtSections.Count > 0));
    }

    private static CleanerrRuleConditionDto ParseJsonRuleCondition(JsonObject rObj, bool isFirst)
    {
        var opRaw = (rObj["operator"] ?? rObj["action"])?.ToString();
        var field = (rObj["field"] ?? rObj["firstVal"])?.ToString() ?? "plex.daysSinceLastActivity";
        var comp = ((rObj["comparator"] ?? rObj["operatorType"])?.ToString() ?? "EQUALS").ToUpperInvariant();
        var val = (rObj["value"] ?? rObj["customVal"])?.ToString();
        var secondField = (rObj["secondField"] ?? rObj["lastVal"])?.ToString();

        return new CleanerrRuleConditionDto(
            Operator: isFirst ? null : NormalizeLogicalOp(opRaw),
            Field: field,
            Comparator: comp,
            Value: val,
            SecondField: string.IsNullOrWhiteSpace(secondField) ? null : secondField);
    }

    private static JsonObject ParseGraphJsonObject(string rawJson)
    {
        var node = JsonNode.Parse(rawJson)?.AsObject()
                   ?? throw new ArgumentException("Invalid graph_json object in YAML.");
        return node;
    }

    private static JsonObject CreateEmptyGraph() =>
        new()
        {
            ["nodes"] = new JsonArray(),
            ["edges"] = new JsonArray(),
        };

    private static int? ReadJsonInt(JsonNode? node) =>
        node is not null && int.TryParse(node.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var i)
            ? i
            : null;

    private static bool? ReadJsonBool(JsonNode? node) =>
        node is null ? null : ParseBoolFlexible(node.ToString());

    private static List<long>? ParseLongList(string raw)
    {
        var trimmed = raw.Trim().Trim('[', ']');
        var list = new List<long>();
        foreach (var token in trimmed.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                list.Add(id);
            }
        }
        return list.Count > 0 ? list : null;
    }

    private static List<CleanerrMediaItem> GetItemAncestors(CleanerrMediaItem item, CleanerrEvaluationContext ctx)
    {
        var list = new List<CleanerrMediaItem> { item };
        if (!string.IsNullOrWhiteSpace(item.ParentRatingKey) && ctx.MediaByRatingKey.TryGetValue(item.ParentRatingKey, out var p))
            list.Add(p);
        if (!string.IsNullOrWhiteSpace(item.GrandparentRatingKey) && ctx.MediaByRatingKey.TryGetValue(item.GrandparentRatingKey, out var gp))
            list.Add(gp);
        return list;
    }

    private static string ResolveClassification(CleanerrMediaItem item, IReadOnlyList<CleanerrMediaItem> ancestors)
    {
        foreach (var a in ancestors)
        {
            if (!string.IsNullOrWhiteSpace(a.ClassificationKind))
                return a.ClassificationKind!;
        }

        var tags = ancestors.SelectMany(a => a.Tags).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isMovie = item.MediaType == "movie";
        if (tags.Contains("anime-movie") || (tags.Contains("anime") && isMovie)) return "anime-movie";
        if (tags.Contains("anime-series") || tags.Contains("anime")) return "anime-series";
        if (tags.Contains("cartoon-movie") || (tags.Contains("cartoon") && isMovie)) return "cartoon-movie";
        if (tags.Contains("cartoon-series") || tags.Contains("cartoon")) return "cartoon-series";
        return isMovie ? "live-action-movie" : "live-action-series";
    }

    private static IReadOnlyList<CleanerrMediaItem> GetChildEpisodes(CleanerrMediaItem item, CleanerrEvaluationContext ctx)
    {
        if (item.MediaType == "season")
        {
            if (ctx.ChildrenByParentRk.TryGetValue(item.PlexRatingKey, out var children))
            {
                var directEps = children.Where(c => c.MediaType == "episode").ToList();
                if (directEps.Count > 0)
                {
                    return directEps;
                }
            }

            if (!string.IsNullOrWhiteSpace(item.ParentRatingKey) &&
                item.SeasonNumber.HasValue &&
                ctx.EpisodesByShowRk.TryGetValue(item.ParentRatingKey, out var showEps))
            {
                return showEps.Where(e => e.SeasonNumber == item.SeasonNumber.Value).ToList();
            }

            return [];
        }

        if (item.MediaType == "show")
        {
            return ctx.EpisodesByShowRk.TryGetValue(item.PlexRatingKey, out var eps) ? eps : [];
        }

        return [];
    }

    private static bool IsEpisodeWatched(CleanerrMediaItem ep) =>
        ep.ViewCount > 0 || !string.IsNullOrWhiteSpace(ep.LastViewedAt);

    private static int ResolveEffectiveViewCount(CleanerrMediaItem item, CleanerrEvaluationContext ctx)
    {
        if (item.ViewCount > 0) return item.ViewCount;
        var eps = GetChildEpisodes(item, ctx);
        return eps.Count > 0 ? eps.Count(IsEpisodeWatched) : 0;
    }

    private static DateTimeOffset? ResolveEffectiveLastViewed(CleanerrMediaItem item, CleanerrEvaluationContext ctx)
    {
        var direct = ParseIso(item.LastViewedAt);
        var eps = GetChildEpisodes(item, ctx);
        if (eps.Count == 0) return direct;

        DateTimeOffset? maxEp = null;
        foreach (var ep in eps)
        {
            if (ParseIso(ep.LastViewedAt) is { } dt && (!maxEp.HasValue || dt > maxEp.Value))
                maxEp = dt;
        }

        if (direct.HasValue && maxEp.HasValue) return direct.Value > maxEp.Value ? direct : maxEp;
        return maxEp ?? direct;
    }

    private static IReadOnlyList<string> ResolveEffectiveViewedBy(CleanerrMediaItem item, CleanerrEvaluationContext ctx)
    {
        if (item.ViewedBy.Count > 0) return item.ViewedBy;
        var eps = GetChildEpisodes(item, ctx);
        return eps.Count == 0
            ? []
            : eps.SelectMany(e => e.ViewedBy).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool ResolveIsInProgress(CleanerrMediaItem item, CleanerrEvaluationContext ctx)
    {
        if (item.ViewOffsetMs > 0) return true;
        var eps = GetChildEpisodes(item, ctx);
        return eps.Any(e => e.ViewOffsetMs > 0);
    }

    private static string? ResolveShowRatingKey(CleanerrMediaItem item) =>
        item.MediaType switch
        {
            "show" => item.PlexRatingKey,
            "season" => item.ParentRatingKey ?? item.GrandparentRatingKey,
            "episode" => item.GrandparentRatingKey ?? item.ParentRatingKey,
            _ => null,
        };

    private static double? ResolveTotalSeasons(CleanerrMediaItem item, CleanerrEvaluationContext ctx)
    {
        var showRk = ResolveShowRatingKey(item);
        if (string.IsNullOrWhiteSpace(showRk)) return null;
        if (!ctx.ChildrenByParentRk.TryGetValue(showRk, out var children)) return 0.0;
        return children.Count(c => c.MediaType == "season" && (c.SeasonNumber ?? 1) > 0);
    }

    private static bool ResolveIsPartOfLatestSeason(CleanerrMediaItem item, CleanerrEvaluationContext ctx)
    {
        var showRk = ResolveShowRatingKey(item);
        if (string.IsNullOrWhiteSpace(showRk) || !item.SeasonNumber.HasValue) return false;
        if (!ctx.ChildrenByParentRk.TryGetValue(showRk, out var children)) return false;
        var seasons = children.Where(c => c.MediaType == "season" && c.SeasonNumber.HasValue).Select(c => c.SeasonNumber!.Value).ToList();
        return seasons.Count > 0 && item.SeasonNumber.Value >= seasons.Max();
    }

    private static double ComputeWatchedPercent(CleanerrMediaItem item, CleanerrEvaluationContext ctx)
    {
        var eps = GetChildEpisodes(item, ctx);
        if (eps.Count == 0) return IsEpisodeWatched(item) ? 100.0 : 0.0;
        return Math.Round((double)eps.Count(IsEpisodeWatched) / eps.Count * 100.0, 1);
    }

    private static bool ComputeIsCompletedSeason(CleanerrMediaItem item, CleanerrEvaluationContext ctx)
    {
        var eps = GetChildEpisodes(item, ctx);
        if (eps.Count == 0) return item.ViewCount > 0 && !string.IsNullOrWhiteSpace(item.LastViewedAt);
        return eps.All(IsEpisodeWatched);
    }

    private static double? ComputeSeriesActiveWatchDays(CleanerrMediaItem item, CleanerrEvaluationContext ctx)
    {
        var showRk = ResolveShowRatingKey(item);
        if (string.IsNullOrWhiteSpace(showRk)) return null;

        DateTimeOffset? latest = null;
        if (ctx.EpisodesByShowRk.TryGetValue(showRk, out var eps))
        {
            foreach (var ep in eps)
            {
                if (ParseIso(ep.LastViewedAt) is { } dt && (!latest.HasValue || dt > latest.Value))
                    latest = dt;
            }
        }

        if (!latest.HasValue && ctx.MediaByRatingKey.TryGetValue(showRk, out var show) && ParseIso(show.LastViewedAt) is { } sdt)
        {
            latest = sdt;
        }

        return latest.HasValue
            ? Math.Max(0, Math.Round((ctx.ReferenceNow - latest.Value).TotalDays, 2))
            : 99999.0;
    }

    private static IReadOnlyList<string> ComputeAllEpisodesSeenByUsers(CleanerrMediaItem item, CleanerrEvaluationContext ctx)
    {
        var eps = GetChildEpisodes(item, ctx);
        if (eps.Count == 0) return item.ViewedBy;
        HashSet<string>? intersection = null;
        foreach (var ep in eps)
        {
            if (intersection is null)
                intersection = new HashSet<string>(ep.ViewedBy, StringComparer.OrdinalIgnoreCase);
            else
                intersection.IntersectWith(ep.ViewedBy);
        }
        return intersection?.ToList() ?? [];
    }

    private static double? ComputeSeasonRankFromNewest(CleanerrMediaItem item, CleanerrEvaluationContext ctx)
    {
        if (item.MediaType != "season" || !item.SeasonNumber.HasValue) return null;
        var showRk = ResolveShowRatingKey(item);
        if (string.IsNullOrWhiteSpace(showRk) || !ctx.ChildrenByParentRk.TryGetValue(showRk, out var children)) return 1.0;

        var ordered = children
            .Where(c => c.MediaType == "season" && c.SeasonNumber.HasValue)
            .Select(c => c.SeasonNumber!.Value)
            .Distinct()
            .OrderByDescending(n => n)
            .ToList();
        var idx = ordered.IndexOf(item.SeasonNumber.Value);
        return idx >= 0 ? idx + 1.0 : 1.0;
    }

    private static string FormatValueForDisplay(object? val) =>
        val switch
        {
            null => "null",
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            double d => d.ToString("0.##", CultureInfo.InvariantCulture),
            IReadOnlyList<string> list => $"[{string.Join(", ", list)}]",
            _ => val.ToString() ?? "",
        };

    private static bool ValueExists(object? val) =>
        val switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            IReadOnlyCollection<string> col => col.Count > 0,
            _ => true,
        };

    private static bool TryCoerceDouble(object? val, out double number)
    {
        switch (val)
        {
            case double d:
                number = d;
                return true;
            case int i:
                number = i;
                return true;
            case long l:
                number = l;
                return true;
            case float f:
                number = f;
                return true;
            case string s when double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed):
                number = parsed;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    private static double? ParseDurationDays(string raw)
    {
        var s = raw.Trim().ToLowerInvariant();
        if (s.EndsWith('d')) s = s[..^1].Trim();
        if (s.EndsWith("days", StringComparison.Ordinal)) s = s[..^4].Trim();
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static bool? ParseBoolFlexible(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => null,
        };
    }

    private static DateTimeOffset? ParseIso(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto
            : null;

    private static string NormalizeLogicalOp(string? op) =>
        string.Equals(op?.Trim(), "OR", StringComparison.OrdinalIgnoreCase) ? "OR" : "AND";

    private static string NormalizeMediaType(string? raw) =>
        (raw ?? "movie").Trim().ToLowerInvariant() switch
        {
            "movies" or "movie" or "1" => "movie",
            "shows" or "show" or "series" or "tv" or "seasons" or "season" or "episodes" or "episode" or "2" or "3" or "4" => "show",
            _ => "movie",
        };

    private static string YamlQuote(string? val)
    {
        var v = val ?? "";
        return $"\"{v.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    private static string StripYamlComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
                inQuotes = !inQuotes;
            else if (line[i] == '#' && !inQuotes)
                return line[..i];
        }
        return line;
    }

    private static bool TrySplitYamlKv(string text, out string key, out string val)
    {
        var idx = text.IndexOf(':');
        if (idx <= 0)
        {
            key = "";
            val = "";
            return false;
        }

        key = text[..idx].Trim();
        val = text[(idx + 1)..].Trim();
        if (val.Length >= 2 && ((val.StartsWith('"') && val.EndsWith('"')) || (val.StartsWith('\'') && val.EndsWith('\''))))
        {
            val = val[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
        return true;
    }
}
