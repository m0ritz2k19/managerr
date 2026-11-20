using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dapper;
using Managerr.Catalog;
using Managerr.Cleanerr;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Metadata;
using Managerr.Profiles;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Managerr.Phase5.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Managerr.Phase5.Tests;

public sealed class CleanerrLifecycleAndRulesTests
{
    [Fact]
    public async Task MaintainerrRuleEngine_BooleanSections_Taxonomy_PropertyComparison_TestMedia_AndYamlRoundTrip()
    {
        await using var h = await HostTestHarness.StartAsync();
        var engine = h.Get<ICleanerrLifecycleEngine>();
        var catalog = h.Get<IPolymorphicCatalogService>();

        var ruleCatalog = engine.GetRuleCatalog();
        Assert.True(ruleCatalog.Fields.Count >= 35);
        Assert.True(ruleCatalog.Comparators.Count >= 20);
        Assert.Contains("delete_show_if_empty", ruleCatalog.ActionModes);
        Assert.Contains("add_import_exclusion", ruleCatalog.ActionModes);

        var presets = CleanerrMaintainerrRuleEngine.GetMaintainerrRulePresets();
        Assert.True(presets.Count >= 4);
        Assert.Contains(presets, p => p.Slug == "monday-movies-365d");

        var moviesLib = await h.LibraryAsync("movies");
        var animeWork = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
            moviesLib.Id,
            "Your Name",
            WorkKind: "movies",
            OriginalLanguage: "ja",
            Year: 2016,
            ExternalIds: new Dictionary<string, string> { ["anilist"] = "21519" },
            Tags: ["anime", "anime-movie", "animation"]));
        var liveWork = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
            moviesLib.Id,
            "Gladiator",
            WorkKind: "movies",
            Year: 2000,
            Tags: ["live-action", "live-action-movie", "movie"]));

        await engine.SyncCatalogItemsIntoCleanerrAsync();
        var synced = await engine.ListMediaItemsAsync();
        var animeItem = synced.Single(m => m.WorkId == animeWork.Work.Id);
        var liveItem = synced.Single(m => m.WorkId == liveWork.Work.Id);
        Assert.Equal("anime-movie", animeItem.ClassificationKind);
        Assert.Equal("live-action-movie", liveItem.ClassificationKind);

        var old120 = DateTimeOffset.UtcNow.AddDays(-120).ToString("O");
        animeItem = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
            animeItem.PlexRatingKey,
            "movie",
            "Your Name",
            LibraryId: moviesLib.Id,
            WorkId: animeWork.Work.Id,
            Year: 2016,
            SizeBytes: 18L * 1024 * 1024 * 1024,
            Tags: animeItem.Tags,
            LastViewedAt: old120,
            ViewCount: 3,
            AudioLanguages: ["ja", "de"],
            ClassificationKind: "anime-movie"));

        liveItem = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
            liveItem.PlexRatingKey,
            "movie",
            "Gladiator",
            LibraryId: moviesLib.Id,
            WorkId: liveWork.Work.Id,
            Year: 2000,
            SizeBytes: 28L * 1024 * 1024 * 1024,
            Tags: liveItem.Tags,
            LastViewedAt: old120,
            ViewCount: 3,
            AudioLanguages: ["en"],
            ClassificationKind: "live-action-movie"));

        // Section 1 (AND): catalog.classification == live-action-movie AND plex.daysSinceLastActivity > 90
        // Section 2 (AND across sections, OR inside section + property-vs-property):
        //   file.sizeGb > 100 (false) OR plex.viewCount > file.audioLanguageCount (3 > 1 = true)
        var ruleSpec = new CleanerrRuleGroupSpec(
            TargetMediaType: "movie",
            LibraryIds: null,
            GraceDays: 14,
            ActionMode: "delete_files_and_unmonitor",
            Sections:
            [
                new CleanerrRuleSectionDto(
                    SectionOperator: "AND",
                    Title: "Live-Action Inactivity",
                    Rules:
                    [
                        new(null, "catalog.classification", "EQUALS", "live-action-movie"),
                        new("AND", "plex.daysSinceLastActivity", "BIGGER", "90"),
                    ]),
                new CleanerrRuleSectionDto(
                    SectionOperator: "AND",
                    Title: "Size or Views vs Audio Tracks",
                    Rules:
                    [
                        new(null, "file.sizeGb", "BIGGER", "100"),
                        new("OR", "plex.viewCount", "BIGGER", null, SecondField: "file.audioLanguageCount"),
                    ]),
            ]);

        var ctx = CleanerrMaintainerrRuleEngine.BuildContext([animeItem, liveItem], DateTimeOffset.UtcNow, 500, 50);
        var (animeDirectMatch, _, _, _) = CleanerrMaintainerrRuleEngine.EvaluateItemAgainstRuleGroup(animeItem, ruleSpec, ctx);
        var (liveDirectMatch, _, liveDirectTraces, _) = CleanerrMaintainerrRuleEngine.EvaluateItemAgainstRuleGroup(liveItem, ruleSpec, ctx);
        Assert.False(animeDirectMatch);
        Assert.True(liveDirectMatch);
        Assert.True(liveDirectTraces[1].Rules[1].IsPropertyComparison);

        var rulesWf = await engine.UpsertWorkflowAsync(new UpsertCleanerrWorkflowRequest(
            Name: "Maintainerr Live-Action Cleanup",
            Description: "Protects Anime Movies while cleaning stale Live-Action",
            Enabled: true,
            DryRun: true,
            ScheduleCron: "0 3 * * 1",
            EditorMode: "rules",
            RuleGroup: ruleSpec));

        var animeTrace = await engine.TestMediaAsync(new TestCleanerrMediaRequest(animeItem.Id, WorkflowId: rulesWf.Id));
        Assert.False(animeTrace.WouldMatch);
        Assert.False(animeTrace.Sections[0].SectionPassed);
        Assert.False(animeTrace.Sections[0].Rules[0].Passed);

        var liveTrace = await engine.TestMediaAsync(new TestCleanerrMediaRequest(liveItem.Id, WorkflowId: rulesWf.Id));
        Assert.True(liveTrace.WouldMatch);
        Assert.True(liveTrace.Sections[0].SectionPassed);
        Assert.True(liveTrace.Sections[1].SectionPassed);
        Assert.True(liveTrace.Sections[1].Rules[1].IsPropertyComparison);
        Assert.True(liveTrace.Sections[1].Rules[1].Passed);

        var sim = await engine.SimulateWorkflowAsync(new SimulateCleanerrWorkflowRequest(WorkflowId: rulesWf.Id));
        Assert.Equal(1, sim.Summary.StagedCount);
        Assert.NotNull(sim.Summary.MatchedItems);
        Assert.Equal(liveItem.Id, Assert.Single(sim.Summary.MatchedItems!).Id);

        // YAML Export -> Import Roundtrip ("rules" mode)
        var exportedRules = await engine.ExportWorkflowYamlAsync(rulesWf.Id);
        Assert.Contains("editor_mode: \"rules\"", exportedRules.Yaml);
        Assert.Contains("secondField:", exportedRules.Yaml);
        Assert.Contains("live-action-movie", exportedRules.Yaml);

        var importedRules = await engine.ImportWorkflowYamlAsync(
            new ImportCleanerrRuleYamlRequest(exportedRules.Yaml, DryRun: true, Enabled: false));
        Assert.Equal("rules", importedRules.EditorMode);
        Assert.Equal("0 3 * * 1", importedRules.ScheduleCron);
        Assert.NotNull(importedRules.RuleGroup);
        Assert.Equal(2, importedRules.RuleGroup!.Sections.Count);
        Assert.Equal("OR", importedRules.RuleGroup.Sections[1].Rules[1].Operator);
        Assert.Equal("file.audioLanguageCount", importedRules.RuleGroup.Sections[1].Rules[1].SecondField);

        // YAML Export -> Import Roundtrip ("dag" mode)
        var dagGraph = JsonNode.Parse("""
            {
              "nodes": [
                { "id": "src", "data": { "label": "Movies", "node_type": "source.plex_library", "config": { "media_type": "movie" } } },
                { "id": "watch", "data": { "label": "Watched > 90d", "node_type": "filter.watch_state", "config": { "last_watched_older_than_days": 90 } } },
                { "id": "act", "data": { "label": "Delete", "node_type": "action.managerr", "config": { "mode": "delete_files_and_unmonitor" } } }
              ],
              "edges": [
                { "source": "src", "target": "watch" },
                { "source": "watch", "target": "act" }
              ]
            }
            """)!.AsObject();

        var dagWf = await engine.UpsertWorkflowAsync(new UpsertCleanerrWorkflowRequest(
            Name: "DAG Export Roundtrip",
            Description: "Visual DAG workflow",
            Enabled: false,
            DryRun: true,
            ScheduleCron: "0 4 * * *",
            GraphJson: dagGraph,
            EditorMode: "dag"));

        var exportedDag = await engine.ExportWorkflowYamlAsync(dagWf.Id);
        Assert.Contains("editor_mode: \"dag\"", exportedDag.Yaml);
        Assert.Contains("graph_json: |", exportedDag.Yaml);

        var importedDag = await engine.ImportWorkflowYamlAsync(
            new ImportCleanerrRuleYamlRequest(exportedDag.Yaml, DryRun: true, Enabled: false));
        Assert.Equal("dag", importedDag.EditorMode);
        Assert.Null(importedDag.RuleGroup);
        Assert.Equal("DAG Export Roundtrip", importedDag.Name);
        Assert.Equal("0 4 * * *", importedDag.ScheduleCron);
        Assert.Equal(3, importedDag.GraphJson["nodes"]!.AsArray().Count);
        Assert.Equal(2, importedDag.GraphJson["edges"]!.AsArray().Count);
    }

    [Fact]
    public async Task WholeSeriesOnly_RejectsSeasonEpisode_Templates_LegacyGranularityPassthrough_AndCatalogSyncRollup()
    {
        await using var h = await HostTestHarness.StartAsync();
        var engine = h.Get<ICleanerrLifecycleEngine>();
        var catalog = h.Get<IPolymorphicCatalogService>();
        var dbFactory = h.Get<ISqliteDbFactory>();
        var now = DateTimeOffset.UtcNow;
        var watched400d = now.AddDays(-400).ToString("O");
        var watched200d = now.AddDays(-200).ToString("O");
        var watched100d = now.AddDays(-100).ToString("O");
        var watched10d = now.AddDays(-10).ToString("O");

        // 1. UpsertMediaItemAsync MUST reject "season" and "episode" with ArgumentException
        await Assert.ThrowsAsync<ArgumentException>(() => engine.UpsertMediaItemAsync(
            new UpsertCleanerrMediaItemRequest("rk-rej-s1", "season", "Season 1", ParentRatingKey: "rk-rej-show")));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.UpsertMediaItemAsync(
            new UpsertCleanerrMediaItemRequest("rk-rej-e1", "episode", "Pilot", GrandparentRatingKey: "rk-rej-show")));

        // 2. Verify whole-series templates exist and target whole "show" items
        var templates = engine.ListTemplates();
        var staleTvTemplate = templates.Single(t => t.Slug == "stale-watched-tv-series");
        var endedTvTemplate = templates.Single(t => t.Slug == "ended-tv-series-cleanup");
        var stale365Preset = templates.Single(t => t.Slug == "stale-tv-series-365d");

        var endedVeryStale = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
            "rk-ws-1", "show", "Deadwood", Genres: ["Drama", "Western"], WorkStatus: "Ended",
            SizeBytes: 24L * 1024 * 1024 * 1024, ViewCount: 12, LastViewedAt: watched400d));
        var continuingStale = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
            "rk-ws-2", "show", "Severance", Genres: ["Sci-Fi"], WorkStatus: "Continuing",
            SizeBytes: 18L * 1024 * 1024 * 1024, ViewCount: 9, LastViewedAt: watched200d));
        var endedModeratelyStale = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
            "rk-ws-3", "show", "Fleabag", Genres: ["Comedy"], WorkStatus: "Ended",
            SizeBytes: 8L * 1024 * 1024 * 1024, ViewCount: 6, LastViewedAt: watched100d));
        var endedFresh = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
            "rk-ws-4", "show", "Chernobyl", Genres: ["Drama"], WorkStatus: "Ended",
            SizeBytes: 16L * 1024 * 1024 * 1024, ViewCount: 5, LastViewedAt: watched10d));
        var unwatchedShow = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
            "rk-ws-5", "show", "Andor", Genres: ["Sci-Fi"], WorkStatus: "Continuing",
            SizeBytes: 20L * 1024 * 1024 * 1024, ViewCount: 0, LastViewedAt: null));

        // stale-watched-tv-series (>180d) matches Deadwood & Severance
        var simStale180 = await engine.SimulateWorkflowAsync(new SimulateCleanerrWorkflowRequest(GraphJson: staleTvTemplate.GraphJson));
        var stale180Ids = simStale180.Summary.NodeMetrics["watch"].MatchedItemIds;
        Assert.Contains(endedVeryStale.Id, stale180Ids);
        Assert.Contains(continuingStale.Id, stale180Ids);
        Assert.DoesNotContain(endedModeratelyStale.Id, stale180Ids);
        Assert.DoesNotContain(endedFresh.Id, stale180Ids);
        Assert.DoesNotContain(unwatchedShow.Id, stale180Ids);

        // ended-tv-series-cleanup (>90d) matches Deadwood & Fleabag (& Severance), NOT fresh Chernobyl or unwatched Andor
        var simEnded = await engine.SimulateWorkflowAsync(new SimulateCleanerrWorkflowRequest(GraphJson: endedTvTemplate.GraphJson));
        var endedIds = simEnded.Summary.NodeMetrics["watch"].MatchedItemIds;
        Assert.Contains(endedVeryStale.Id, endedIds);
        Assert.Contains(endedModeratelyStale.Id, endedIds);
        Assert.DoesNotContain(endedFresh.Id, endedIds);
        Assert.DoesNotContain(unwatchedShow.Id, endedIds);

        // Empty upstream guard on filter.metadata -> 0 matches propagate 0 downstream
        var emptyMetaGraph = endedTvTemplate.GraphJson.DeepClone().AsObject();
        emptyMetaGraph["nodes"]!.AsArray()
            .Single(n => n!["id"]!.GetValue<string>() == "meta")!["data"]!["config"]!["genre_any"] = new JsonArray("Cooking");
        var emptyMetaSim = await engine.SimulateWorkflowAsync(new SimulateCleanerrWorkflowRequest(GraphJson: emptyMetaGraph));
        Assert.Equal(0, emptyMetaSim.Summary.NodeMetrics["meta"].OutputCount);
        Assert.Equal(0, emptyMetaSim.Summary.NodeMetrics["watch"].OutputCount);
        Assert.Equal(0, emptyMetaSim.Summary.NodeMetrics["act"].OutputCount);

        // stale-tv-series-365d (>365d) matches only Deadwood
        var sim365 = await engine.SimulateWorkflowAsync(new SimulateCleanerrWorkflowRequest(
            GraphJson: stale365Preset.GraphJson));
        Assert.Equal(1, sim365.Summary.StagedCount);
        Assert.Equal(endedVeryStale.Id, Assert.Single(sim365.Summary.MatchedItems!).Id);

        // 3. Legacy source.granularity node executes as a safe no-op passthrough keeping whole "show" items
        var legacyGranGraph = JsonNode.Parse("""
            {
              "nodes": [
                { "id": "src", "data": { "label": "Shows", "node_type": "source.plex_library", "config": { "media_type": "show" } } },
                { "id": "gran", "data": { "label": "Legacy Granularity", "node_type": "source.granularity", "config": { "level": "episode", "mode": "keep_latest_n_episodes", "keep_count": 2 } } },
                { "id": "act", "data": { "label": "Delete", "node_type": "action.managerr", "config": { "mode": "delete_files_and_unmonitor" } } }
              ],
              "edges": [
                { "source": "src", "target": "gran" },
                { "source": "gran", "target": "act" }
              ]
            }
            """)!.AsObject();
        var granSim = await engine.SimulateWorkflowAsync(new SimulateCleanerrWorkflowRequest(GraphJson: legacyGranGraph));
        Assert.Contains(granSim.Summary.Logs, l => l.Contains("Skipped source.granularity", StringComparison.Ordinal));
        var granMatched = granSim.Summary.NodeMetrics["gran"].MatchedItemIds;
        Assert.Contains(endedVeryStale.Id, granMatched);

        // Standalone source.granularity (no media upstream) also passes whole shows safely
        var standaloneGranGraph = JsonNode.Parse("""
            {
              "nodes": [
                { "id": "gran", "data": { "label": "Legacy Granularity", "node_type": "source.granularity", "config": { "level": "episode" } } }
              ],
              "edges": []
            }
            """)!.AsObject();
        var standaloneGranSim = await engine.SimulateWorkflowAsync(new SimulateCleanerrWorkflowRequest(GraphJson: standaloneGranGraph));
        Assert.Contains(endedVeryStale.Id, standaloneGranSim.Summary.NodeMetrics["gran"].MatchedItemIds);

        // 4. SyncCatalogItemsIntoCleanerrAsync syncs whole TV series as ONE "show" row with rolled-up SizeBytes + latest LastWatchedAt,
        //    promotes legacy episode/season exclusion rules to the parent "show", and prunes legacy season/episode rows.
        var tvLib = await h.LibraryAsync("tv");
        var darkWork = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
            tvLib.Id,
            "Dark",
            WorkKind: "tv",
            Year: 2017,
            Containers:
            [
                new MetadataContainerSeed("season", 1, "Season 1", "2017-12-01",
                [
                    new MetadataUnitSeed("episode", 1, 1, "Secrets", "2017-12-01"),
                    new MetadataUnitSeed("episode", 2, 2, "Lies", "2017-12-01"),
                ]),
                new MetadataContainerSeed("season", 2, "Season 2", "2019-06-21",
                [
                    new MetadataUnitSeed("episode", 1, 1, "Beginnings and Endings", "2019-06-21"),
                ]),
            ]));
        var s1Units = darkWork.Containers.Single(c => c.ContainerNumber == 1).Units;
        var s2Unit = darkWork.Containers.Single(c => c.ContainerNumber == 2).Units.Single();
        await catalog.AttachFileAsync(new AttachMediaFileRequest(s1Units[0].Id, "Dark/Season 01/S01E01.mkv", 3_000_000_000, "WEB-DL 1080p"));
        await catalog.AttachFileAsync(new AttachMediaFileRequest(s1Units[1].Id, "Dark/Season 01/S01E02.mkv", 4_000_000_000, "WEB-DL 1080p"));
        await catalog.AttachFileAsync(new AttachMediaFileRequest(s2Unit.Id, "Dark/Season 02/S02E01.mkv", 5_000_000_000, "WEB-DL 1080p"));

        var nowIso = now.ToString("O");
        var oldWatched = now.AddDays(-120).ToString("O");
        var latestWatched = now.AddDays(-5).ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    "UPDATE media_units SET watch_count = 1, last_watched_at = @W WHERE id = @Id;",
                    new { W = oldWatched, Id = s1Units[0].Id }, transaction: tx);
                await conn.ExecuteAsync(
                    "UPDATE media_units SET watch_count = 2, last_watched_at = @W WHERE id = @Id;",
                    new { W = latestWatched, Id = s2Unit.Id }, transaction: tx);

                // Seed legacy season/episode rows + a legacy exclusion rule pointing to legacyEpId (whose grandparent_rating_key is rk-ws-1 -> Deadwood)
                await conn.ExecuteAsync(
                    """
                    INSERT INTO cleanerr_media_items (
                        plex_rating_key, parent_rating_key, grandparent_rating_key, media_type, title,
                        size_bytes, monitored, added_at, last_viewed_at, view_count, updated_at
                    ) VALUES
                    ('rk-legacy-s1', 'rk-ws-1', 'rk-ws-1', 'season', 'Season 1', 1000, 1, @Now, @Old, 1, @Now),
                    ('rk-legacy-e1', 'rk-legacy-s1', 'rk-ws-1', 'episode', 'Pilot', 1000, 1, @Now, @Old, 1, @Now);
                    """,
                    new { Now = nowIso, Old = watched400d },
                    transaction: tx);
                var legacyEpId = await conn.ExecuteScalarAsync<long>(
                    "SELECT id FROM cleanerr_media_items WHERE plex_rating_key = 'rk-legacy-e1';",
                    transaction: tx);

                await conn.ExecuteAsync(
                    """
                    INSERT INTO cleanerr_exclusion_rules (
                        media_item_id, external_id, rule_type, value, granularity, reason, created_by, created_at
                    ) VALUES (
                        @MediaId, 'rk-legacy-e1', 'title', 'Pilot', 'episode', 'Legacy episode exclusion', 'admin', @Now
                    );
                    """,
                    new { MediaId = legacyEpId, Now = nowIso },
                    transaction: tx);
            });

        await engine.SyncCatalogItemsIntoCleanerrAsync();

        var allAfterSync = await engine.ListMediaItemsAsync();
        Assert.DoesNotContain(allAfterSync, m => m.MediaType is "season" or "episode");
        var darkShowItem = Assert.Single(allAfterSync, m => m.WorkId == darkWork.Work.Id);
        Assert.Equal("show", darkShowItem.MediaType);
        Assert.Equal(12_000_000_000L, darkShowItem.SizeBytes);
        Assert.Equal(3, darkShowItem.ViewCount);
        Assert.Equal(latestWatched, darkShowItem.LastViewedAt);

        var promotedRule = Assert.Single(await engine.ListExclusionRulesAsync());
        Assert.Equal(endedVeryStale.Id, promotedRule.MediaItemId);
        Assert.Equal("series", promotedRule.Granularity);
        Assert.Equal("Deadwood", promotedRule.Value);

        var children = await engine.GetMediaChildrenAsync(darkShowItem.Id);
        Assert.Equal(darkShowItem.Id, children.Parent.Id);
        Assert.Empty(children.Seasons);
        Assert.Empty(children.Episodes);
    }

    [Fact]
    public async Task CronEvaluator_FieldParsing_IntervalSuppression_InvalidCron_AndScheduledWorkflowEvaluation()
    {
        // Unit assertions for CleanerrCronEvaluator
        var mon0315 = new DateTimeOffset(2026, 11, 16, 3, 15, 0, TimeSpan.Zero); // Monday
        var mon0316 = new DateTimeOffset(2026, 11, 16, 3, 16, 0, TimeSpan.Zero);
        var sat1000 = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero); // Saturday
        var sun0300 = new DateTimeOffset(2026, 11, 15, 3, 0, 0, TimeSpan.Zero); // Sunday

        // Exact minute/hour
        Assert.True(CleanerrCronEvaluator.IsDue("15 3 * * *", null, mon0315));
        Assert.False(CleanerrCronEvaluator.IsDue("15 3 * * *", null, mon0316));

        // Step expression */6
        var h0600 = new DateTimeOffset(2026, 11, 16, 6, 0, 0, TimeSpan.Zero);
        var h0700 = new DateTimeOffset(2026, 11, 16, 7, 0, 0, TimeSpan.Zero);
        var h1200 = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        Assert.True(CleanerrCronEvaluator.IsDue("0 */6 * * *", null, h0600));
        Assert.False(CleanerrCronEvaluator.IsDue("0 */6 * * *", h0600, h0700));
        Assert.True(CleanerrCronEvaluator.IsDue("0 */6 * * *", h0600, h1200));

        // Range 1-5 (Mon-Fri)
        var mon1000 = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
        Assert.True(CleanerrCronEvaluator.IsDue("0 9-17 * * 1-5", null, mon1000));
        Assert.False(CleanerrCronEvaluator.IsDue("0 9-17 * * 1-5", null, sat1000));

        // List
        var m10 = new DateTimeOffset(2026, 11, 16, 4, 10, 0, TimeSpan.Zero);
        var m25 = new DateTimeOffset(2026, 11, 16, 4, 25, 0, TimeSpan.Zero);
        var m40 = new DateTimeOffset(2026, 11, 16, 4, 40, 0, TimeSpan.Zero);
        Assert.True(CleanerrCronEvaluator.IsDue("10,40 * * * *", null, m10));
        Assert.False(CleanerrCronEvaluator.IsDue("10,40 * * * *", m10, m25));
        Assert.True(CleanerrCronEvaluator.IsDue("10,40 * * * *", m10, m40));

        // Weekday (1 = Monday, 0/7 = Sunday)
        var mon0300 = new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.Zero);
        Assert.True(CleanerrCronEvaluator.IsDue("0 3 * * 1", null, mon0300));
        Assert.False(CleanerrCronEvaluator.IsDue("0 3 * * 1", null, sun0300));
        Assert.True(CleanerrCronEvaluator.IsDue("0 3 * * 7", null, sun0300));

        // Already-ran-inside-current-interval suppression
        var slot0300 = new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.Zero);
        var ran0301 = new DateTimeOffset(2026, 11, 16, 3, 1, 0, TimeSpan.Zero);
        var check0310 = new DateTimeOffset(2026, 11, 16, 3, 10, 0, TimeSpan.Zero);
        Assert.False(CleanerrCronEvaluator.IsDue("*/15 * * * *", slot0300, slot0300));
        Assert.False(CleanerrCronEvaluator.IsDue("*/15 * * * *", ran0301, check0310));

        // Invalid cron graceful handling
        Assert.False(CleanerrCronEvaluator.IsValidCron("invalid cron"));
        Assert.False(CleanerrCronEvaluator.IsValidCron("99 * * * *"));
        Assert.False(CleanerrCronEvaluator.IsDue("invalid cron", null, mon0315));
        Assert.False(CleanerrCronEvaluator.IsDue("", null, mon0315));
        Assert.Null(CleanerrCronEvaluator.GetNextOccurrenceUtc("bad-cron", mon0315));

        // Service test: EvaluateScheduledWorkflowsAsync executes only the due workflow
        await using var h = await HostTestHarness.StartAsync();
        var engine = h.Get<ICleanerrLifecycleEngine>();

        var nowUtc = DateTimeOffset.UtcNow;
        var dueCron = $"{nowUtc.Minute} {nowUtc.Hour} * * *";
        var notDueCron = $"{(nowUtc.Minute + 25) % 60} {(nowUtc.Hour + 5) % 24} * * *";

        var simpleGraph = JsonNode.Parse("""
            {
              "nodes": [
                { "id": "src", "data": { "label": "Movies", "node_type": "source.plex_library", "config": { "media_type": "movie" } } }
              ],
              "edges": []
            }
            """)!.AsObject();

        var dueWf = await engine.UpsertWorkflowAsync(new UpsertCleanerrWorkflowRequest(
            Name: "Due Cron Workflow",
            Enabled: true,
            DryRun: true,
            ScheduleCron: dueCron,
            GraphJson: simpleGraph));

        var notDueWf = await engine.UpsertWorkflowAsync(new UpsertCleanerrWorkflowRequest(
            Name: "Future Cron Workflow",
            Enabled: true,
            DryRun: true,
            ScheduleCron: notDueCron,
            GraphJson: simpleGraph));

        var firstPass = await engine.EvaluateScheduledWorkflowsAsync(nowUtc);
        var executed = Assert.Single(firstPass);
        Assert.Equal(dueWf.Id, executed.WorkflowId);
        Assert.Equal("scheduled", executed.TriggerType);

        var notDueAfter = (await engine.GetWorkflowByIdAsync(notDueWf.Id))!;
        Assert.Null(notDueAfter.LastRunAt);

        // Second evaluation within the same cron interval must not re-run the already-executed workflow
        var secondPass = await engine.EvaluateScheduledWorkflowsAsync(nowUtc.AddSeconds(30));
        Assert.Empty(secondPass);
    }

    [Fact]
    public async Task PhysicalDeletionSafety_SidecarsEmptyFolders_DowngradeProfileValidation_AndImportExclusion()
    {
        var outsideFile = Path.Combine(Path.GetTempPath(), $"cleanerr-outside-{Guid.NewGuid():N}.mkv");
        await File.WriteAllBytesAsync(outsideFile, new byte[4096]);

        try
        {
            await using var h = await HostTestHarness.StartAsync();
            var engine = h.Get<ICleanerrLifecycleEngine>();
            var catalog = h.Get<IPolymorphicCatalogService>();
            var profiles = h.Get<IComposableProfileService>();
            var dbFactory = h.Get<ISqliteDbFactory>();

            var tvLib = await h.LibraryAsync("tv");
            var moviesLib = await h.LibraryAsync("movies");

            // 1. File outside configured library root MUST NEVER be physically deleted
            var outsideItem = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                PlexRatingKey: "rk-outside-1",
                MediaType: "movie",
                Title: "Outside Root Movie",
                LibraryId: moviesLib.Id,
                SizeBytes: 4096,
                FilePath: outsideFile));

            var outsideStaged = await engine.ManualStageItemsAsync(new ManualStageCleanerrItemsRequest(
                [outsideItem.Id], GraceDays: 0, ActionMode: "delete_files_and_unmonitor"));
            await engine.DeleteQueueItemNowAsync(Assert.Single(outsideStaged).Id);
            Assert.True(File.Exists(outsideFile));

            // 2. Full-series physical deletion strictly inside library root deletes all files + sidecars (.srt/.nfo) & empty folders,
            //    keeps tvRoot intact, and unmonitors work + containers + units.
            var showDir = Path.Combine(tvLib.RootFolderPath, "Chernobyl (2019)");
            var seasonDir = Path.Combine(showDir, "Season 01");
            Directory.CreateDirectory(seasonDir);
            var ep1Mkv = Path.Combine(seasonDir, "Chernobyl - S01E01 - 1_23_45.mkv");
            var ep1Srt = Path.Combine(seasonDir, "Chernobyl - S01E01 - 1_23_45.de.srt");
            var ep1Nfo = Path.Combine(seasonDir, "Chernobyl - S01E01 - 1_23_45.nfo");
            var ep2Mkv = Path.Combine(seasonDir, "Chernobyl - S01E02 - Please Remain Calm.mkv");
            var ep2Nfo = Path.Combine(seasonDir, "Chernobyl - S01E02 - Please Remain Calm.nfo");
            await File.WriteAllBytesAsync(ep1Mkv, new byte[8192]);
            await File.WriteAllTextAsync(ep1Srt, "1\n00:00:01,000 --> 00:00:02,000\nSub");
            await File.WriteAllTextAsync(ep1Nfo, "<episodedetails/>");
            await File.WriteAllBytesAsync(ep2Mkv, new byte[4096]);
            await File.WriteAllTextAsync(ep2Nfo, "<episodedetails/>");

            var chernobyl = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                tvLib.Id,
                "Chernobyl",
                WorkKind: "tv",
                Year: 2019,
                Containers:
                [
                    new MetadataContainerSeed(
                        "season",
                        1,
                        "Season 1",
                        "2019-05-06",
                        [
                            new MetadataUnitSeed("episode", 1, 1, "1:23:45", "2019-05-06"),
                            new MetadataUnitSeed("episode", 2, 2, "Please Remain Calm", "2019-05-13"),
                        ]),
                ]));
            var chUnits = chernobyl.Containers.Single(c => c.ContainerNumber == 1).Units;
            await catalog.AttachFileAsync(new AttachMediaFileRequest(
                chUnits[0].Id, Path.GetRelativePath(tvLib.RootFolderPath, ep1Mkv), 8192, "Bluray-1080p", InodeHardlinked: false));
            await catalog.AttachFileAsync(new AttachMediaFileRequest(
                chUnits[1].Id, Path.GetRelativePath(tvLib.RootFolderPath, ep2Mkv), 4096, "Bluray-1080p", InodeHardlinked: false));

            await engine.SyncCatalogItemsIntoCleanerrAsync();
            var allSynced = await engine.ListMediaItemsAsync();
            Assert.DoesNotContain(allSynced, m => m.MediaType is "season" or "episode");
            var showItem = Assert.Single(allSynced, m => m.WorkId == chernobyl.Work.Id && m.MediaType == "show");
            Assert.Equal(8192L + 4096L, showItem.SizeBytes);

            var insideStaged = await engine.ManualStageItemsAsync(new ManualStageCleanerrItemsRequest(
                [showItem.Id], GraceDays: 5, ActionMode: "delete_files_and_unmonitor"));
            var deletedInside = await engine.DeleteQueueItemNowAsync(Assert.Single(insideStaged).Id);
            Assert.Equal("deleted", deletedInside.Status);

            Assert.False(File.Exists(ep1Mkv));
            Assert.False(File.Exists(ep1Srt));
            Assert.False(File.Exists(ep1Nfo));
            Assert.False(File.Exists(ep2Mkv));
            Assert.False(File.Exists(ep2Nfo));
            Assert.False(Directory.Exists(seasonDir));
            Assert.False(Directory.Exists(showDir));
            Assert.True(Directory.Exists(tvLib.RootFolderPath));

            var chernobylAfter = await catalog.GetWorkTreeAsync(chernobyl.Work.Id);
            Assert.NotNull(chernobylAfter);
            Assert.False(chernobylAfter!.Work.Monitored);
            Assert.All(chernobylAfter.Containers, c => Assert.False(c.Monitored));
            Assert.All(chernobylAfter.Containers.SelectMany(c => c.Units), u => Assert.False(u.Monitored));

            // 3. downgrade_profile validates against real IComposableProfileService profile & skips invalid IDs
            var createdProfile = await profiles.UpsertProfileAsync(new UpsertQualityProfileRequest(
                Name: "Efficient 1080p",
                Slug: "efficient-1080p-test",
                MediaVertical: "movies",
                AllowedQualities: [new AllowedQualityTier("WEB-DL 1080p", 1, 100.0)],
                QualityCutoff: "WEB-DL 1080p"));

            var remuxWork = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                moviesLib.Id,
                "Interstellar",
                WorkKind: "movies",
                Year: 2014,
                ExternalIds: new Dictionary<string, string> { ["tmdb"] = "157336" }));
            var remuxUnit = remuxWork.Containers[0].Units[0];
            await engine.SyncCatalogItemsIntoCleanerrAsync();
            var remuxItem = (await engine.ListMediaItemsAsync()).Single(m => m.WorkId == remuxWork.Work.Id);

            var invalidProfileWf = await engine.UpsertWorkflowAsync(new UpsertCleanerrWorkflowRequest(
                Name: "Downgrade Invalid Profile",
                Enabled: true,
                DryRun: false,
                GraphJson: BuildMovieDowngradeGraph(moviesLib.Id, 999_999L)));
            await engine.ExecuteWorkflowAsync(invalidProfileWf.Id, dryRunOverride: false);

            var unitAfterInvalid = (await catalog.GetWorkTreeAsync(remuxWork.Work.Id))!.Containers[0].Units[0];
            Assert.Null(unitAfterInvalid.ProfileOverrideId);

            var validProfileWf = await engine.UpsertWorkflowAsync(new UpsertCleanerrWorkflowRequest(
                Name: "Downgrade Valid Profile",
                Enabled: true,
                DryRun: false,
                GraphJson: BuildMovieDowngradeGraph(moviesLib.Id, createdProfile.Id)));
            var downgradeRun = await engine.ExecuteWorkflowAsync(validProfileWf.Id, dryRunOverride: false);
            Assert.Equal(1, downgradeRun.Summary.DowngradedCount);

            var unitAfterValid = (await catalog.GetWorkTreeAsync(remuxWork.Work.Id))!.Containers[0].Units[0];
            Assert.Equal(createdProfile.Id, unitAfterValid.ProfileOverrideId);
            Assert.False(unitAfterValid.QualityCutoffMet);

            // 4. action.add_import_exclusion inserts into import_list_exclusions
            var exclWf = await engine.UpsertWorkflowAsync(new UpsertCleanerrWorkflowRequest(
                Name: "Add Import Exclusion",
                Enabled: true,
                DryRun: false,
                GraphJson: JsonNode.Parse($$$"""
                    {
                      "nodes": [
                        { "id": "src", "data": { "label": "Movies", "node_type": "source.plex_library", "config": { "media_type": "movie", "library_ids": [{{{moviesLib.Id}}}] } } },
                        { "id": "act", "data": { "label": "Import Exclude", "node_type": "action.add_import_exclusion", "config": {} } }
                      ],
                      "edges": [{ "source": "src", "target": "act" }]
                    }
                    """)!.AsObject()));
            await engine.ExecuteWorkflowAsync(exclWf.Id, dryRunOverride: false);

            await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core);
            var extKey = await conn.ExecuteScalarAsync<string?>(
                "SELECT external_key FROM import_list_exclusions WHERE external_key = 'tmdb:157336';");
            Assert.Equal("tmdb:157336", extKey);
        }
        finally
        {
            try { if (File.Exists(outsideFile)) File.Delete(outsideFile); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task DeletionQueueLifecycle_Postpone_AutoRescue_ActiveStreamGuard_FinalWarningDedup_OverviewAndDuplicate()
    {
        var http = new RecordingHttp
        {
            Responder = (_, _) => RecordingHttp.Json("""{"id":"discord-msg-1"}"""),
        };

        await using var h = await HostTestHarness.StartAsync(http: http);
        var engine = h.Get<ICleanerrLifecycleEngine>();
        var discord = h.Get<ICleanerrDiscordService>();
        var moviesLib = await h.LibraryAsync("movies");

        await discord.UpdateSettingsAsync(new UpdateCleanerrDiscordSettingsRequest(
            WebhookUrl: "https://discord.example/api/webhooks/1/test-token",
            NotifyOnStaged: false,
            NotifyOnRescued: false,
            NotifyOnDeleted: false,
            NotifyOnFinalWarning: true,
            FinalWarningHours: 24));

        var gb = 1024L * 1024 * 1024;
        var movieA = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
            "rk-q-a", "movie", "Arrival", LibraryId: moviesLib.Id, SizeBytes: 10 * gb));
        var movieB = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
            "rk-q-b", "movie", "Heat", LibraryId: moviesLib.Id, SizeBytes: 12 * gb));
        var movieC = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
            "rk-q-c", "movie", "Blade Runner", LibraryId: moviesLib.Id, SizeBytes: 15 * gb, ActiveStreamCount: 1));
        var movieD = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
            "rk-q-d", "movie", "Dune", LibraryId: moviesLib.Id, SizeBytes: 20 * gb));

        var show = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
            "rk-fam-show", "show", "Severance", Year: 2022, SizeBytes: 8 * gb));

        // 1. Manual Stage + Postpone
        var stagedA = Assert.Single(await engine.ManualStageItemsAsync(new ManualStageCleanerrItemsRequest([movieA.Id], GraceDays: 7)));
        var initialDeleteAtA = DateTimeOffset.Parse(stagedA.DeleteAt);
        var postponedA = await engine.PostponeQueueItemAsync(stagedA.Id, days: 5, reason: "Keep a little longer");
        Assert.Equal(1, postponedA.PostponedCount);
        Assert.True(DateTimeOffset.Parse(postponedA.DeleteAt) >= initialDeleteAtA.AddDays(4.9));

        // 2. Manual Stage Movie B + Plex playback webhook auto-rescue
        await engine.ManualStageItemsAsync(new ManualStageCleanerrItemsRequest([movieB.Id], GraceDays: 7));
        await Task.Delay(20);
        var webhookRes = await engine.HandleInboundWebhookAsync("plex", new JsonObject
        {
            ["ratingKey"] = "rk-q-b",
            ["user"] = "alice",
        });
        Assert.Equal(1, webhookRes["rescued_count"]!.GetValue<int>());
        Assert.Equal("rescued", (await engine.ListDeletionQueueAsync(null)).Single(q => q.MediaItemId == movieB.Id).Status);

        // 3. Whole-series exclusion on Severance
        await engine.ManualStageItemsAsync(new ManualStageCleanerrItemsRequest([show.Id], GraceDays: 7));
        var escalatedTitles = await engine.ExcludeMediaIdsWithFamilyEscalationAsync([show.Id], "admin", "Series protection");
        Assert.Single(escalatedTitles, "Severance");
        var stagedAfterFam = await engine.ListDeletionQueueAsync("staged");
        Assert.DoesNotContain(stagedAfterFam, q => q.MediaItemId == show.Id);

        // 4. Stage Movie C (2d grace, ActiveStreamCount=1) and Movie D (2d grace, ActiveStreamCount=0)
        //    At +40h: both are within FinalWarningHours (24h) of 48h DeleteAt -> final warning sent ONCE!
        await engine.ManualStageItemsAsync(new ManualStageCleanerrItemsRequest([movieC.Id, movieD.Id], GraceDays: 2));
        http.Requests.Clear();

        var tickWarn1 = await engine.ProcessDeletionQueueAsync(DateTimeOffset.UtcNow.AddHours(40));
        Assert.Equal(2, tickWarn1.FinalWarningsSent);
        Assert.Equal(0, tickWarn1.DeletedCount);
        Assert.Contains(http.Requests, r => r.Uri.ToString().Contains("discord.example", StringComparison.OrdinalIgnoreCase));

        var queueAfterWarn = await engine.ListDeletionQueueAsync("staged");
        Assert.NotNull(queueAfterWarn.Single(q => q.MediaItemId == movieD.Id).FinalWarningSentAt);

        // 2nd pass 1 hour later (+41h) must deduplicate final warning -> FinalWarningsSent == 0
        var reqCountBefore = http.Requests.Count;
        var tickWarn2 = await engine.ProcessDeletionQueueAsync(DateTimeOffset.UtcNow.AddHours(41));
        Assert.Equal(0, tickWarn2.FinalWarningsSent);
        Assert.Equal(reqCountBefore, http.Requests.Count);

        // 5. Active Stream Guard at +3d (past 2d grace): Movie C deferred (ActiveStreamCount=1), Movie D deleted!
        var tickExpire = await engine.ProcessDeletionQueueAsync(DateTimeOffset.UtcNow.AddDays(3));
        Assert.Equal(1, tickExpire.DeferredActiveStreamCount);
        Assert.Equal(1, tickExpire.DeletedCount);
        var finalQueue = await engine.ListDeletionQueueAsync(null);
        Assert.Equal("staged", finalQueue.Single(q => q.MediaItemId == movieC.Id).Status);
        Assert.Equal("deleted", finalQueue.Single(q => q.MediaItemId == movieD.Id).Status);

        // 6. Overview Stats (LibraryStorage & ReclaimForecast) + DuplicateWorkflowAsync
        var wf = await engine.UpsertWorkflowAsync(new UpsertCleanerrWorkflowRequest(
            Name: "Original Workflow",
            Description: "Test wf",
            Enabled: true,
            DryRun: true,
            ScheduleCron: "0 3 * * *",
            GraphJson: new JsonObject { ["nodes"] = new JsonArray(), ["edges"] = new JsonArray() }));
        var dup = await engine.DuplicateWorkflowAsync(wf.Id, "Original Workflow (Clone)");
        Assert.NotEqual(wf.Id, dup.Id);
        Assert.Equal("Original Workflow (Clone)", dup.Name);
        Assert.False(dup.Enabled);

        var stats = await engine.GetOverviewStatsAsync();
        Assert.NotNull(stats.LibraryStorage);
        Assert.Contains(stats.LibraryStorage!, l => l.LibraryId == moviesLib.Id);
        Assert.NotNull(stats.ReclaimForecast);
        Assert.Equal(5, stats.ReclaimForecast!.Count);
        Assert.True(stats.StagedCount >= 2);
        Assert.Equal(1, stats.DeletedTotalCount);
    }

    [Fact]
    public async Task LiveEnableConfirmation_RequiredOnPostAndPut_EnforcesNameMatch_AndLogsAuditEntry()
    {
        // Unconditional guard: UpsertCleanerrWorkflowRequest must not expose a test bypass property
        Assert.Null(typeof(UpsertCleanerrWorkflowRequest).GetProperty("SkipConfirmationForTest"));

        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-live-confirm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var client = await CreateAdminClientAsync(factory);
            using var scope = factory.Services.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<ICleanerrLifecycleEngine>();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLogService>();

            var gb = 1024L * 1024 * 1024;
            await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                "rk-conf-1", "movie", "Unwatched Epic", Year: 2023, SizeBytes: 12 * gb));

            var graph = JsonNode.Parse("""
                {
                  "nodes": [
                    { "id": "src", "data": { "label": "All", "node_type": "source.managerr_catalog", "config": {} } },
                    { "id": "grace", "data": { "label": "Grace", "node_type": "queue.grace_period", "config": { "grace_days": 7 } } },
                    { "id": "act", "data": { "label": "Delete", "node_type": "action.managerr", "config": { "mode": "delete_files_and_unmonitor" } } }
                  ],
                  "edges": [
                    { "source": "src", "target": "grace" },
                    { "source": "grace", "target": "act" }
                  ]
                }
                """)!.AsObject();

            // 1. POST with DryRun=false and missing ConfirmWorkflowName -> 400
            var postMissing = await client.PostAsJsonAsync(
                "/api/v1/cleanerr/workflows",
                new UpsertCleanerrWorkflowRequest(
                    Name: "Live Post Sweep",
                    Enabled: true,
                    DryRun: false,
                    GraphJson: graph,
                    EditorMode: "dag"));
            Assert.Equal(HttpStatusCode.BadRequest, postMissing.StatusCode);
            var postMissingJson = JsonNode.Parse(await postMissing.Content.ReadAsStringAsync())!.AsObject();
            Assert.Equal("ERR_CLEANERR_LIVE_CONFIRMATION_REQUIRED", postMissingJson["errorCode"]?.GetValue<string>());

            // 2. POST with DryRun=false and wrong ConfirmWorkflowName -> 400
            var postWrong = await client.PostAsJsonAsync(
                "/api/v1/cleanerr/workflows",
                new UpsertCleanerrWorkflowRequest(
                    Name: "Live Post Sweep",
                    Enabled: true,
                    DryRun: false,
                    GraphJson: graph,
                    EditorMode: "dag",
                    ConfirmWorkflowName: "Wrong Name"));
            Assert.Equal(HttpStatusCode.BadRequest, postWrong.StatusCode);

            // 3. POST with DryRun=false and matching ConfirmWorkflowName -> 201 Created + live_enabled audit
            var postValid = await client.PostAsJsonAsync(
                "/api/v1/cleanerr/workflows",
                new UpsertCleanerrWorkflowRequest(
                    Name: "Live Post Sweep",
                    Enabled: true,
                    DryRun: false,
                    GraphJson: graph,
                    EditorMode: "dag",
                    ConfirmWorkflowName: "Live Post Sweep"));
            Assert.Equal(HttpStatusCode.Created, postValid.StatusCode);
            var createdLive = (await postValid.Content.ReadFromJsonAsync<CleanerrWorkflowSummary>())!;
            Assert.False(createdLive.DryRun);

            // 4. POST in DryRun=true, then PUT DryRun=true -> false
            var postDry = await client.PostAsJsonAsync(
                "/api/v1/cleanerr/workflows",
                new UpsertCleanerrWorkflowRequest(
                    Name: "Put Transition Sweep",
                    Enabled: true,
                    DryRun: true,
                    GraphJson: graph,
                    EditorMode: "dag"));
            Assert.Equal(HttpStatusCode.Created, postDry.StatusCode);
            var dryWf = (await postDry.Content.ReadFromJsonAsync<CleanerrWorkflowSummary>())!;
            Assert.True(dryWf.DryRun);

            var putUnconfirmed = await client.PutAsJsonAsync(
                $"/api/v1/cleanerr/workflows/{dryWf.Id}",
                new UpsertCleanerrWorkflowRequest(
                    Name: "Put Transition Sweep",
                    Enabled: true,
                    DryRun: false,
                    GraphJson: graph,
                    EditorMode: "dag"));
            Assert.Equal(HttpStatusCode.BadRequest, putUnconfirmed.StatusCode);
            var putErrJson = JsonNode.Parse(await putUnconfirmed.Content.ReadAsStringAsync())!.AsObject();
            Assert.Equal("ERR_CLEANERR_LIVE_CONFIRMATION_REQUIRED", putErrJson["errorCode"]?.GetValue<string>());
            Assert.True((await engine.GetWorkflowByIdAsync(dryWf.Id))!.DryRun);

            var putConfirmed = await client.PutAsJsonAsync(
                $"/api/v1/cleanerr/workflows/{dryWf.Id}",
                new UpsertCleanerrWorkflowRequest(
                    Name: "Put Transition Sweep",
                    Enabled: true,
                    DryRun: false,
                    GraphJson: graph,
                    EditorMode: "dag",
                    ConfirmWorkflowName: "  put transition sweep  "));
            Assert.Equal(HttpStatusCode.OK, putConfirmed.StatusCode);
            Assert.False((await putConfirmed.Content.ReadFromJsonAsync<CleanerrWorkflowSummary>())!.DryRun);

            var liveAudits = await audit.ListRecentAsync(limit: 20, actionFilter: "cleanerr.workflow.live_enabled");
            Assert.Equal(2, liveAudits.Count);
            Assert.Contains(liveAudits, a => a.ResourceId == createdLive.Id.ToString(CultureInfo.InvariantCulture));
            Assert.Contains(liveAudits, a => a.ResourceId == dryWf.Id.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task RbacScopes_PerRoleExclusionQuotas_AndCleanerrMcpTools()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-cleanerr-rbac-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var scope = factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IPlexUserService>();
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var auth = scope.ServiceProvider.GetRequiredService<IPlexAuthAndSessionService>();
            var engine = scope.ServiceProvider.GetRequiredService<ICleanerrLifecycleEngine>();

            // Seed media + workflow + queue item for RBAC & MCP tests
            var movie = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                "rk-rbac-m1",
                "movie",
                "Solaris",
                Year: 1972,
                SizeBytes: 15L * 1024 * 1024 * 1024,
                LastViewedAt: DateTimeOffset.UtcNow.AddDays(-120).ToString("O"),
                ViewCount: 1));
            var movie2 = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                "rk-rbac-m2",
                "movie",
                "Stalker",
                Year: 1979,
                SizeBytes: 14L * 1024 * 1024 * 1024));

            var wf = await engine.UpsertWorkflowAsync(new UpsertCleanerrWorkflowRequest(
                Name: "RBAC Test Workflow",
                Enabled: true,
                DryRun: true,
                EditorMode: "rules",
                RuleGroup: new CleanerrRuleGroupSpec(
                    "movie",
                    null,
                    7,
                    "delete_files_and_unmonitor",
                    [new CleanerrRuleSectionDto("AND", [new(null, "plex.isWatched", "EQUALS", "true")])])));

            var stagedRow = Assert.Single(await engine.ManualStageItemsAsync(new ManualStageCleanerrItemsRequest([movie.Id], GraceDays: 7)));

            // Create users with granular Cleanerr scopes + limited exclusion quota
            var admin = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
                100, "adm-u", "admin", "admin@managerr.local", null, true, true, true, ["1"]));
            var requesterRole = (await roles.ListRolesAsync()).Single(r => r.Key == "media:requester");
            var requester = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
                101, "req-u", "requester", "req@managerr.local", null, false, true, true, ["1"]));
            requester = await users.AssignRolesAsync(requester.Id, [requesterRole.Id]);

            var readOnlyRole = await roles.CreateCustomRoleAsync(new UpsertCustomRoleRequest(
                "Cleanerr Reader", "custom:clnr-read", "", "#38bdf8", [PermissionScopes.CleanerrRead]));
            var rescueRole = await roles.CreateCustomRoleAsync(new UpsertCustomRoleRequest(
                "Cleanerr Rescuer", "custom:clnr-rescue", "", "#22c55e", [PermissionScopes.CleanerrRead, PermissionScopes.CleanerrRescue]));
            var excludeQuotaRole = await roles.CreateCustomRoleAsync(new UpsertCustomRoleRequest(
                "Cleanerr Excluder", "custom:clnr-excl", "", "#a855f7", [PermissionScopes.CleanerrRead, PermissionScopes.CleanerrExclude], CleanerrExclusionLimit: 1));
            var writeRole = await roles.CreateCustomRoleAsync(new UpsertCustomRoleRequest(
                "Cleanerr Writer", "custom:clnr-write", "", "#f59e0b", [PermissionScopes.CleanerrRead, PermissionScopes.CleanerrWrite]));
            var executeRole = await roles.CreateCustomRoleAsync(new UpsertCustomRoleRequest(
                "Cleanerr Executor", "custom:clnr-exec", "", "#ef4444", [PermissionScopes.CleanerrRead, PermissionScopes.CleanerrExecute]));

            var readUser = await users.AssignRolesAsync((await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(102, "u-r", "reader", null, null, false, false, true, ["1"]))).Id, [readOnlyRole.Id]);
            var rescueUser = await users.AssignRolesAsync((await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(103, "u-s", "rescuer", null, null, false, false, true, ["1"]))).Id, [rescueRole.Id]);
            var exclUser = await users.AssignRolesAsync((await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(104, "u-e", "excluder", null, null, false, false, true, ["1"]))).Id, [excludeQuotaRole.Id]);
            var writeUser = await users.AssignRolesAsync((await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(105, "u-w", "writer", null, null, false, false, true, ["1"]))).Id, [writeRole.Id]);
            var execUser = await users.AssignRolesAsync((await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(106, "u-x", "executor", null, null, false, false, true, ["1"]))).Id, [executeRole.Id]);

            using var reqClient = await CreateSessionClientAsync(factory, auth, requester);
            using var readClient = await CreateSessionClientAsync(factory, auth, readUser);
            using var rescueClient = await CreateSessionClientAsync(factory, auth, rescueUser);
            using var exclClient = await CreateSessionClientAsync(factory, auth, exclUser);
            using var writeClient = await CreateSessionClientAsync(factory, auth, writeUser);
            using var execClient = await CreateSessionClientAsync(factory, auth, execUser);
            using var adminClient = await CreateSessionClientAsync(factory, auth, admin);

            // 1. Requester without Cleanerr scopes -> 403 Forbidden
            Assert.Equal(HttpStatusCode.Forbidden, (await reqClient.GetAsync("/api/v1/cleanerr/stats")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reqClient.GetAsync("/api/v1/cleanerr/workflows")).StatusCode);

            // 2. cleanerr:read only -> GETs allowed, mutations forbidden
            Assert.Equal(HttpStatusCode.OK, (await readClient.GetAsync("/api/v1/cleanerr/stats")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await readClient.GetAsync("/api/v1/cleanerr/workflows")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await readClient.GetAsync("/api/v1/cleanerr/queue")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await readClient.PostAsJsonAsync($"/api/v1/cleanerr/queue/{stagedRow.Id}/postpone", new { days = 3 })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await readClient.PostAsJsonAsync("/api/v1/cleanerr/exclusions", new CreateCleanerrExclusionRequest(RuleType: "tag", Value: "keep"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await readClient.PostAsJsonAsync("/api/v1/cleanerr/workflows", new UpsertCleanerrWorkflowRequest("NoWrite"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await readClient.PostAsJsonAsync($"/api/v1/cleanerr/workflows/{wf.Id}/execute", new { dryRun = true })).StatusCode);

            // 3. cleanerr:rescue -> postpone allowed, exclude/write/execute forbidden
            Assert.Equal(HttpStatusCode.OK, (await rescueClient.PostAsJsonAsync($"/api/v1/cleanerr/queue/{stagedRow.Id}/postpone", new { days = 3 })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await rescueClient.PostAsJsonAsync("/api/v1/cleanerr/exclusions", new CreateCleanerrExclusionRequest(RuleType: "tag", Value: "keep"))).StatusCode);

            // 4. cleanerr:exclude + CleanerrExclusionLimit = 1 -> 1st succeeds (201), 2nd hits quota (409), write forbidden
            var exOk = await exclClient.PostAsJsonAsync("/api/v1/cleanerr/exclusions", new CreateCleanerrExclusionRequest(RuleType: "tag", Value: "vip-keep-1"));
            Assert.Equal(HttpStatusCode.Created, exOk.StatusCode);
            var exOverQuota = await exclClient.PostAsJsonAsync("/api/v1/cleanerr/exclusions", new CreateCleanerrExclusionRequest(RuleType: "tag", Value: "vip-keep-2"));
            Assert.Equal(HttpStatusCode.Conflict, exOverQuota.StatusCode);
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CreateExclusionRuleAsync(
                new CreateCleanerrExclusionRequest(RuleType: "tag", Value: "vip-keep-3"), exclUser.Username, exclUser.Id, 1));
            Assert.Equal(HttpStatusCode.Forbidden, (await exclClient.PostAsJsonAsync("/api/v1/cleanerr/workflows", new UpsertCleanerrWorkflowRequest("NoWrite"))).StatusCode);

            // 5. cleanerr:write -> POST /workflows allowed, execute forbidden
            var wfCreate = await writeClient.PostAsJsonAsync("/api/v1/cleanerr/workflows", new UpsertCleanerrWorkflowRequest("Writer Created", DryRun: true));
            Assert.Equal(HttpStatusCode.Created, wfCreate.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await writeClient.PostAsJsonAsync($"/api/v1/cleanerr/workflows/{wf.Id}/execute", new { dryRun = true })).StatusCode);

            // 6. cleanerr:execute -> POST /execute allowed, POST /workflows forbidden
            Assert.Equal(HttpStatusCode.OK, (await execClient.PostAsJsonAsync($"/api/v1/cleanerr/workflows/{wf.Id}/execute", new { dryRun = true })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await execClient.PostAsJsonAsync("/api/v1/cleanerr/workflows", new UpsertCleanerrWorkflowRequest("NoWrite"))).StatusCode);

            // 7. MCP Tools (all 6 Cleanerr tools + RBAC scope filtering)
            var readTools = await PostMcpAsync(readClient, new { jsonrpc = "2.0", id = 1, method = "tools/list" });
            var readToolNames = readTools["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("cleanerr_get_overview", readToolNames);
            Assert.Contains("cleanerr_list_workflows", readToolNames);
            Assert.Contains("cleanerr_simulate_workflow", readToolNames);
            Assert.Contains("cleanerr_list_queue", readToolNames);
            Assert.Contains("cleanerr_test_media", readToolNames);
            Assert.DoesNotContain("cleanerr_exclude_media", readToolNames);

            var forbiddenMcp = await PostMcpAsync(readClient, new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new { name = "cleanerr_exclude_media", arguments = new { mediaItemIds = new[] { movie2.Id } } },
            });
            Assert.True(forbiddenMcp["result"]!["isError"]!.GetValue<bool>());
            Assert.Contains("ERR_RBAC_FORBIDDEN", forbiddenMcp["result"]!["content"]![0]!["text"]!.GetValue<string>());

            var adminTools = await PostMcpAsync(adminClient, new { jsonrpc = "2.0", id = 3, method = "tools/list" });
            var adminToolNames = adminTools["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            string[] allCleanerrTools =
            [
                "cleanerr_get_overview",
                "cleanerr_list_workflows",
                "cleanerr_simulate_workflow",
                "cleanerr_list_queue",
                "cleanerr_exclude_media",
                "cleanerr_test_media",
                "cleanerr_run_workflow",
                "cleanerr_rescue_queue_item",
                "cleanerr_postpone_queue_item",
                "cleanerr_delete_exclusion",
            ];
            foreach (var tool in allCleanerrTools)
            {
                Assert.Contains(tool, adminToolNames);
            }

            var overviewRes = ExtractMcpPayload(await PostMcpAsync(adminClient, new
            {
                jsonrpc = "2.0",
                id = 4,
                method = "tools/call",
                @params = new { name = "cleanerr_get_overview", arguments = new { } },
            })).AsObject();
            Assert.True(overviewRes["totalMediaItems"]!.GetValue<int>() >= 2);

            var listWfRes = ExtractMcpPayload(await PostMcpAsync(adminClient, new
            {
                jsonrpc = "2.0",
                id = 5,
                method = "tools/call",
                @params = new { name = "cleanerr_list_workflows", arguments = new { } },
            })).AsObject();
            Assert.True(listWfRes["count"]!.GetValue<int>() >= 1);

            var simWfRes = ExtractMcpPayload(await PostMcpAsync(adminClient, new
            {
                jsonrpc = "2.0",
                id = 6,
                method = "tools/call",
                @params = new { name = "cleanerr_simulate_workflow", arguments = new { workflowId = wf.Id } },
            })).AsObject();
            Assert.True(simWfRes["dryRun"]!.GetValue<bool>());
            Assert.Equal(1, simWfRes["summary"]!["stagedCount"]!.GetValue<int>());

            var listQueueRes = ExtractMcpPayload(await PostMcpAsync(adminClient, new
            {
                jsonrpc = "2.0",
                id = 7,
                method = "tools/call",
                @params = new { name = "cleanerr_list_queue", arguments = new { status = "staged" } },
            })).AsObject();
            Assert.Equal(1, listQueueRes["count"]!.GetValue<int>());

            var testMediaRes = ExtractMcpPayload(await PostMcpAsync(adminClient, new
            {
                jsonrpc = "2.0",
                id = 8,
                method = "tools/call",
                @params = new { name = "cleanerr_test_media", arguments = new { mediaItemId = movie.Id, workflowId = wf.Id } },
            })).AsObject();
            Assert.True(testMediaRes["wouldMatch"]!.GetValue<bool>());

            var excludeRes = ExtractMcpPayload(await PostMcpAsync(adminClient, new
            {
                jsonrpc = "2.0",
                id = 9,
                method = "tools/call",
                @params = new
                {
                    name = "cleanerr_exclude_media",
                    arguments = new { mediaItemIds = new[] { movie2.Id }, reason = "Keep Stalker via MCP" },
                },
            })).AsObject();
            Assert.Equal(1, excludeRes["excludedCount"]!.GetValue<int>());
            Assert.Contains("Stalker (1979)", excludeRes["excludedTitles"]!.ToJsonString());
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task AuditFixes_CrossSeriesIsolation_YamlLiveConfirm_QueryFilters_AndM1ToM4Behaviors()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-cleanerr-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var client = await CreateAdminClientAsync(factory);
            using var scope = factory.Services.CreateScope();

            var engine = scope.ServiceProvider.GetRequiredService<ICleanerrLifecycleEngine>();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var profiles = scope.ServiceProvider.GetRequiredService<IComposableProfileService>();
            var dbFactory = scope.ServiceProvider.GetRequiredService<ISqliteDbFactory>();
            var discord = scope.ServiceProvider.GetRequiredService<ICleanerrDiscordService>();

            // 0. POST /api/v1/cleanerr/media rejects "season" and "episode" with 400 ERR_CLEANERR_INVALID_MEDIA_TYPE
            foreach (var badType in new[] { "season", "episode" })
            {
                var badMediaResp = await client.PostAsJsonAsync(
                    "/api/v1/cleanerr/media",
                    new UpsertCleanerrMediaItemRequest($"rk-bad-{badType}", badType, $"Bad {badType}"));
                Assert.Equal(HttpStatusCode.BadRequest, badMediaResp.StatusCode);
                var badMediaJson = JsonNode.Parse(await badMediaResp.Content.ReadAsStringAsync())!.AsObject();
                Assert.Equal("ERR_CLEANERR_INVALID_MEDIA_TYPE", badMediaJson["errorCode"]?.GetValue<string>());
            }

            // 1. H2: Cross-series exclusion isolation (Show Alpha exclusion must NOT affect Show Beta)
            var showA = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                "rk-iso-a", "show", "Show Alpha",
                SizeBytes: 4L * 1024 * 1024 * 1024, ViewCount: 2,
                LastViewedAt: DateTimeOffset.UtcNow.AddDays(-5).ToString("O")));

            var showB = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                "rk-iso-b", "show", "Show Beta",
                SizeBytes: 1L * 1024 * 1024 * 1024, ViewCount: 0));

            await engine.ManualStageItemsAsync(new ManualStageCleanerrItemsRequest([showA.Id, showB.Id], GraceDays: 7));
            var stagedA = (await engine.ListDeletionQueueAsync("staged")).Single(q => q.MediaItemId == showA.Id);
            var (exclQueueA, exclRuleA) = await engine.ExcludeQueueItemAsync(stagedA.Id, "admin", null, null);
            Assert.Equal("excluded", exclQueueA.Status);
            // M4: ExcludeQueueItemAsync returns the rule created for Show Alpha (showA.Id), never an unrelated rule
            Assert.Equal(showA.Id, exclRuleA.MediaItemId);
            Assert.Equal("Show Alpha", exclRuleA.Value);

            var remainingStaged = await engine.ListDeletionQueueAsync("staged");
            Assert.Single(remainingStaged);
            Assert.Equal(showB.Id, remainingStaged[0].MediaItemId);

            // 2. H3: import-yaml live confirmation enforcement + cleanerr.workflow.live_enabled audit log
            var liveYaml = """
                name: "YAML Live Import Test"
                description: "Testing live confirmation"
                editor_mode: "dag"
                schedule_cron: "0 3 * * *"
                graph_json: |
                  {"nodes":[{"id":"src","data":{"label":"Movies","node_type":"source.plex_library","config":{"media_type":"movie"}}}],"edges":[]}
                """;

            var unconfirmedImport = await client.PostAsJsonAsync(
                "/api/v1/cleanerr/workflows/import-yaml",
                new ImportCleanerrRuleYamlRequest(liveYaml, DryRun: false, Enabled: true));
            Assert.Equal(HttpStatusCode.BadRequest, unconfirmedImport.StatusCode);
            var errObj = JsonNode.Parse(await unconfirmedImport.Content.ReadAsStringAsync())!.AsObject();
            Assert.Equal("ERR_CLEANERR_LIVE_CONFIRMATION_REQUIRED", errObj["errorCode"]?.GetValue<string>());

            var confirmedImport = await client.PostAsJsonAsync(
                "/api/v1/cleanerr/workflows/import-yaml",
                new ImportCleanerrRuleYamlRequest(liveYaml, DryRun: false, Enabled: true, ConfirmWorkflowName: "yaml live import test"));
            Assert.Equal(HttpStatusCode.Created, confirmedImport.StatusCode);
            var importedLiveWf = (await confirmedImport.Content.ReadFromJsonAsync<CleanerrWorkflowSummary>())!;
            Assert.False(importedLiveWf.DryRun);

            var importAudits = await audit.ListRecentAsync(limit: 20, actionFilter: "cleanerr.workflow.imported_yaml");
            Assert.Contains(importAudits, a => a.ResourceId == importedLiveWf.Id.ToString(CultureInfo.InvariantCulture));
            var liveAudits = await audit.ListRecentAsync(limit: 20, actionFilter: "cleanerr.workflow.live_enabled");
            Assert.Contains(liveAudits, a => a.ResourceId == importedLiveWf.Id.ToString(CultureInfo.InvariantCulture));

            // 3. M3: QueryMediaItemsAsync watched & sort filters + GET /api/v1/cleanerr/media
            var watchedRes = await engine.QueryMediaItemsAsync(watched: "watched", sort: "size_desc");
            Assert.All(watchedRes.Items, i => Assert.True(i.ViewCount > 0));
            Assert.Contains(watchedRes.Items, i => i.Id == showA.Id);
            Assert.DoesNotContain(watchedRes.Items, i => i.Id == showB.Id);

            var unwatchedHttp = await client.GetFromJsonAsync<CleanerrMediaListResponse>("/api/v1/cleanerr/media?watched=unwatched&sort=title");
            Assert.NotNull(unwatchedHttp);
            Assert.Contains(unwatchedHttp!.Items, i => i.Id == showB.Id);
            Assert.DoesNotContain(unwatchedHttp.Items, i => i.Id == showA.Id);

            // 4. H4: trigger.storage_pressure supports min_free_gb_trigger and does NOT trigger when no threshold is configured
            var noThreshGraph = JsonNode.Parse("""
                {
                  "nodes": [
                    { "id": "trig", "data": { "label": "No Threshold", "node_type": "trigger.storage_pressure", "config": {} } },
                    { "id": "src", "data": { "label": "Shows", "node_type": "source.plex_library", "config": { "media_type": "show" } } }
                  ],
                  "edges": [{ "source": "trig", "target": "src" }]
                }
                """)!.AsObject();
            var noThreshSim = await engine.SimulateWorkflowAsync(new SimulateCleanerrWorkflowRequest(
                GraphJson: noThreshGraph, SimulatedFreeSpaceGb: 50.0));
            Assert.Equal(0, noThreshSim.Summary.NodeMetrics["src"].OutputCount);

            var trigGraph = JsonNode.Parse("""
                {
                  "nodes": [
                    { "id": "trig", "data": { "label": "Low Disk", "node_type": "trigger.storage_pressure", "config": { "min_free_gb_trigger": 100 } } },
                    { "id": "src", "data": { "label": "Shows", "node_type": "source.plex_library", "config": { "media_type": "show" } } }
                  ],
                  "edges": [{ "source": "trig", "target": "src" }]
                }
                """)!.AsObject();
            var triggeredSim = await engine.SimulateWorkflowAsync(new SimulateCleanerrWorkflowRequest(
                GraphJson: trigGraph, SimulatedFreeSpaceGb: 40.0));
            Assert.True(triggeredSim.Summary.NodeMetrics["src"].OutputCount >= 2);
            var healthySim = await engine.SimulateWorkflowAsync(new SimulateCleanerrWorkflowRequest(
                GraphJson: trigGraph, SimulatedFreeSpaceGb: 250.0));
            Assert.Equal(0, healthySim.Summary.NodeMetrics["src"].OutputCount);

            // 5. M1: Newly created workflow catches a cron slot between CreatedAt and referenceNow
            var createdAt = new DateTimeOffset(2026, 5, 10, 3, 10, 0, TimeSpan.Zero);
            var wfCronCatch = await engine.UpsertWorkflowAsync(new UpsertCleanerrWorkflowRequest(
                Name: "M1 Missed Slot Catch",
                Enabled: true,
                DryRun: true,
                ScheduleCron: "15 3 * * *",
                GraphJson: trigGraph));
            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                (conn, tx) => conn.ExecuteAsync(
                    "UPDATE cleanerr_workflows SET created_at = @CreatedAt WHERE id = @Id;",
                    new { CreatedAt = createdAt.ToString("O"), Id = wfCronCatch.Id },
                    transaction: tx));
            var evalRuns = await engine.EvaluateScheduledWorkflowsAsync(new DateTimeOffset(2026, 5, 10, 3, 20, 0, TimeSpan.Zero));
            Assert.Contains(evalRuns, r => r.WorkflowId == wfCronCatch.Id);

            // 6. M2: Plex inbound playback webhook preserves IsHardlinked & ViewOffsetMs
            var hlMovie = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                "rk-m2-hl", "movie", "Hardlinked Movie",
                SizeBytes: 8L * 1024 * 1024 * 1024,
                ViewOffsetMs: 42_000L,
                IsHardlinked: true));
            await engine.HandleInboundWebhookAsync("plex", new JsonObject
            {
                ["ratingKey"] = "rk-m2-hl",
                ["user"] = "alice",
                ["event"] = "media.scrobble",
            });
            var afterWebhook = (await engine.ListMediaItemsAsync()).Single(m => m.Id == hlMovie.Id);
            Assert.True(afterWebhook.IsHardlinked);
            Assert.Equal(42_000L, afterWebhook.ViewOffsetMs);
            Assert.Equal(1, afterWebhook.ViewCount);

            // 7. M4: totalDeleted stays 0 for non-deleting actions (e.g. downgrade_profile, action.seerr_reset)
            //    and downgrade_profile works on show/season without UnitId
            var tvLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("TV", "tv", Path.Combine(tempDir, "tv-audit")));
            var lowProfile = await profiles.UpsertProfileAsync(new UpsertQualityProfileRequest(
                "TV 720p", "tv-720p-audit", "tv", [new AllowedQualityTier("WEB-DL 720p", 1, 50.0)], "WEB-DL 720p"));
            var tvWork = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                tvLib.Id, "Audit Show", WorkKind: "tv", Year: 2023,
                Containers: [new MetadataContainerSeed("season", 1, "Season 1", "2023-01-01", [new MetadataUnitSeed("episode", 1, 1, "Pilot", "2023-01-01")])]));
            _ = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                "rk-show-dg", "show", "Audit Show", LibraryId: tvLib.Id, WorkId: tvWork.Work.Id));
            var downgradeShowWf = await engine.UpsertWorkflowAsync(new UpsertCleanerrWorkflowRequest(
                Name: "Show Downgrade No DeleteCount",
                Enabled: false,
                DryRun: false,
                GraphJson: JsonNode.Parse($$$"""
                    {
                      "nodes": [
                        { "id": "src", "data": { "label": "Shows", "node_type": "source.plex_library", "config": { "media_type": "show", "library_ids": [{{{tvLib.Id}}}] } } },
                        { "id": "act", "data": { "label": "Downgrade", "node_type": "action.managerr", "config": { "mode": "downgrade_profile", "target_profile_id": {{{lowProfile.Id}}} } } }
                      ],
                      "edges": [{ "source": "src", "target": "act" }]
                    }
                    """)!.AsObject()));
            var dgRes = await engine.ExecuteWorkflowAsync(downgradeShowWf.Id, dryRunOverride: false);
            Assert.Equal(0, dgRes.Summary.DeletedCount);
            Assert.Equal(1, dgRes.Summary.DowngradedCount);
            var downgradedUnit = (await catalog.GetWorkTreeAsync(tvWork.Work.Id))!.Containers[0].Units[0];
            Assert.Equal(lowProfile.Id, downgradedUnit.ProfileOverrideId);

            // 8. M4: GET /webhooks/magic/rescue rescues a staged whole series
            var rescueSig = CleanerrCrypto.SignMagicToken(await discord.GetMagicSecretAsync(), $"rescue:{showB.Id}", DateTimeOffset.UtcNow.AddHours(1));
            var rescueResp = await client.GetAsync($"/api/v1/cleanerr/webhooks/magic/rescue?media_id={showB.Id}&sig={Uri.EscapeDataString(rescueSig)}");
            Assert.Equal(HttpStatusCode.OK, rescueResp.StatusCode);
            Assert.DoesNotContain(await engine.ListDeletionQueueAsync("staged"), q => q.MediaItemId == showB.Id);

            // 9. LOW: malformed JSON on webhook returns 400 ERR_INVALID_JSON
            using var badJsonContent = new StringContent("{bad-json", System.Text.Encoding.UTF8, "application/json");
            var badWebhookResp = await client.PostAsync("/api/v1/cleanerr/webhooks/plex", badJsonContent);
            Assert.Equal(HttpStatusCode.BadRequest, badWebhookResp.StatusCode);
            Assert.Contains("ERR_INVALID_JSON", await badWebhookResp.Content.ReadAsStringAsync());

            // 10. unmonitor_only preserves files on disk and rows in media_files while unmonitoring
            var unmonLibDir = Path.Combine(tempDir, "unmon-movies");
            Directory.CreateDirectory(unmonLibDir);
            var unmonMovieFile = Path.Combine(unmonLibDir, "Preserved.Film.2020.mkv");
            await File.WriteAllBytesAsync(unmonMovieFile, new byte[2048]);
            var unmonLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Unmon Movies", "movies", unmonLibDir));
            var unmonWork = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                unmonLib.Id, "Preserved Film", WorkKind: "movie", Year: 2020,
                Containers: [new MetadataContainerSeed("movie", 1, "Preserved Film", "2020-01-01", [new MetadataUnitSeed("movie", 1, 1, "Preserved Film", "2020-01-01")])]));
            var unmonUnit = unmonWork.Containers[0].Units[0];
            await catalog.AttachFileAsync(new AttachMediaFileRequest(
                unmonUnit.Id, "Preserved.Film.2020.mkv", 2048, "Bluray-1080p", Resolution: "1080p", VideoCodec: "hevc"));
            await engine.SyncCatalogItemsIntoCleanerrAsync();
            var unmonItem = (await engine.ListMediaItemsAsync()).Single(m => m.WorkId == unmonWork.Work.Id);

            var unmonWf = await engine.UpsertWorkflowAsync(new UpsertCleanerrWorkflowRequest(
                Name: "Unmonitor Only WF",
                Enabled: false,
                DryRun: false,
                EditorMode: "dag",
                ConfirmWorkflowName: "Unmonitor Only WF",
                GraphJson: JsonNode.Parse($$$"""
                    {
                      "nodes": [
                        { "id": "src", "data": { "label": "Movies", "node_type": "source.managerr_catalog", "config": { "media_type": "movie", "library_ids": [{{{unmonLib.Id}}}] } } },
                        { "id": "act", "data": { "label": "Unmonitor Only", "node_type": "action.managerr", "config": { "mode": "unmonitor_only" } } }
                      ],
                      "edges": [{ "source": "src", "target": "act" }]
                    }
                    """)!.AsObject()));

            var unmonRun = await engine.ExecuteWorkflowAsync(unmonWf.Id, dryRunOverride: false);
            Assert.Equal(0, unmonRun.Summary.DeletedCount);
            Assert.True(File.Exists(unmonMovieFile));
            var postUnmonItem = (await engine.ListMediaItemsAsync()).Single(m => m.Id == unmonItem.Id);
            Assert.False(postUnmonItem.Monitored);
            Assert.True(postUnmonItem.SizeBytes > 0);
            Assert.NotNull(postUnmonItem.FilePath);
            var postUnmonTree = await catalog.GetWorkTreeAsync(unmonWork.Work.Id);
            Assert.NotNull(postUnmonTree);
            Assert.False(postUnmonTree!.Work.Monitored);
            Assert.False(postUnmonTree.Containers[0].Units[0].Monitored);
            Assert.Single(postUnmonTree.Containers[0].Units[0].Files);

            // 11. auto_rescue_on_watch == false skips automatic playback rescue during ProcessDeletionQueueAsync
            var noRescueMovie = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                "rk-no-rescue", "movie", "No Auto Rescue Film",
                SizeBytes: 5L * 1024 * 1024 * 1024,
                LastViewedAt: DateTimeOffset.UtcNow.AddDays(-120).ToString("O"),
                ViewCount: 1));
            var noRescueWf = await engine.UpsertWorkflowAsync(new UpsertCleanerrWorkflowRequest(
                Name: "No Auto Rescue WF",
                Enabled: false,
                DryRun: false,
                EditorMode: "dag",
                ConfirmWorkflowName: "No Auto Rescue WF",
                GraphJson: JsonNode.Parse($$$"""
                    {
                      "nodes": [
                        { "id": "src", "data": { "label": "Movies", "node_type": "source.managerr_catalog", "config": { "media_type": "movie" } } },
                        { "id": "meta", "data": { "label": "Title Match", "node_type": "filter.watch_state", "config": { "last_watched_older_than_days": 90 } } },
                        { "id": "grace", "data": { "label": "Grace", "node_type": "queue.grace_period", "config": { "grace_days": 5, "auto_rescue_on_watch": false } } },
                        { "id": "act", "data": { "label": "Unmonitor", "node_type": "action.managerr", "config": { "mode": "unmonitor_only" } } }
                      ],
                      "edges": [
                        { "source": "src", "target": "meta" },
                        { "source": "meta", "target": "grace" },
                        { "source": "grace", "target": "act" }
                      ]
                    }
                    """)!.AsObject()));
            await engine.ExecuteWorkflowAsync(noRescueWf.Id, dryRunOverride: false);
            var stagedNoRescue = (await engine.ListDeletionQueueAsync("staged")).Single(q => q.MediaItemId == noRescueMovie.Id);
            Assert.False(stagedNoRescue.ActionConfigJson["auto_rescue_on_watch"]!.GetValue<bool>());

            await engine.HandleInboundWebhookAsync("plex", new JsonObject
            {
                ["ratingKey"] = "rk-no-rescue",
                ["user"] = "viewer",
                ["event"] = "media.scrobble",
            });
            var stillStaged = await engine.ListDeletionQueueAsync("staged");
            Assert.Contains(stillStaged, q => q.Id == stagedNoRescue.Id);

            // 12. DefaultGraceDays = 0, DefaultPostponeDays, and trigger.manual allow_api = false
            var updatedGen = await engine.UpdateGeneralSettingsAsync(new UpdateCleanerrGeneralSettingsRequest(
                DefaultGraceDays: 0,
                DefaultPostponeDays: 14,
                TorrentDownloadsPath: Path.Combine(tempDir, "torrents")));
            Assert.Equal(0, updatedGen.DefaultGraceDays);
            Assert.Equal(14, updatedGen.DefaultPostponeDays);

            var beforePostpone = DateTimeOffset.Parse(stagedNoRescue.DeleteAt, CultureInfo.InvariantCulture);
            var postponeResp = await client.PostAsJsonAsync($"/api/v1/cleanerr/queue/{stagedNoRescue.Id}/postpone", new { });
            Assert.Equal(HttpStatusCode.OK, postponeResp.StatusCode);
            var postponedItem = (await postponeResp.Content.ReadFromJsonAsync<CleanerrDeletionQueueItem>())!;
            var afterPostpone = DateTimeOffset.Parse(postponedItem.DeleteAt, CultureInfo.InvariantCulture);
            Assert.Equal(14, (int)Math.Round((afterPostpone - beforePostpone).TotalDays));

            var noApiWf = await engine.UpsertWorkflowAsync(new UpsertCleanerrWorkflowRequest(
                Name: "No API Trigger WF",
                Enabled: false,
                DryRun: true,
                EditorMode: "dag",
                GraphJson: JsonNode.Parse("""
                    {
                      "nodes": [
                        { "id": "trig", "data": { "label": "Manual Only", "node_type": "trigger.manual", "config": { "allow_api": false } } },
                        { "id": "src", "data": { "label": "Movies", "node_type": "source.managerr_catalog", "config": { "media_type": "movie" } } }
                      ],
                      "edges": [{ "source": "trig", "target": "src" }]
                    }
                    """)!.AsObject()));
            var runDisabledResp = await client.PostAsJsonAsync($"/api/v1/cleanerr/workflows/{noApiWf.Id}/run", new { dryRun = true });
            Assert.Equal(HttpStatusCode.BadRequest, runDisabledResp.StatusCode);
            Assert.Contains("ERR_WORKFLOW_API_DISABLED", await runDisabledResp.Content.ReadAsStringAsync());
            var simAllowedResp = await client.PostAsJsonAsync($"/api/v1/cleanerr/workflows/{noApiWf.Id}/simulate", new { });
            Assert.Equal(HttpStatusCode.OK, simAllowedResp.StatusCode);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private static JsonObject BuildMovieDowngradeGraph(long libraryId, long profileId) =>
        JsonNode.Parse($$$"""
            {
              "nodes": [
                { "id": "src", "data": { "label": "Movies", "node_type": "source.plex_library", "config": { "media_type": "movie", "library_ids": [{{{libraryId}}}] } } },
                { "id": "act", "data": { "label": "Downgrade", "node_type": "action.managerr", "config": { "mode": "downgrade_profile", "target_profile_id": {{{profileId}}} } } }
              ],
              "edges": [{ "source": "src", "target": "act" }]
            }
            """)!.AsObject();

    private static WebApplicationFactory<Program> CreateWebFactory(string tempDir) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                    ["Managerr:MasterKey"] = "phase5-cleanerr-rules-master-key!!",
                    ["Managerr:SchedulerEnabled"] = "false",
                });
            });
        });

    private static async Task<HttpClient> CreateAdminClientAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IPlexUserService>();
        var auth = scope.ServiceProvider.GetRequiredService<IPlexAuthAndSessionService>();

        var user = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
            101, "uuid-101", "admin", "admin@managerr.local", null, true, true, true, ["1"]));
        var key = await auth.CreateApiKeyAsync(
            user.Id, "test-admin", PermissionScopes.AllValidScopes.ToList(), null, null, null);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.RawTokenOnce);
        return client;
    }

    private static async Task<HttpClient> CreateSessionClientAsync(
        WebApplicationFactory<Program> factory,
        IPlexAuthAndSessionService auth,
        PlexUserSummary user)
    {
        var session = await auth.CreateSessionForUserAsync(user, "xunit", "127.0.0.1");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.RawSessionToken);
        return client;
    }

    private static async Task<JsonObject> PostMcpAsync(HttpClient client, object body)
    {
        using var resp = await client.PostAsJsonAsync("/api/v1/mcp", body);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonNode.Parse(raw)!.AsObject();
    }

    private static JsonNode ExtractMcpPayload(JsonObject rpcResp)
    {
        var result = rpcResp["result"]!.AsObject();
        Assert.False(result["isError"]?.GetValue<bool>() ?? false, $"Unexpected MCP tool error: {result.ToJsonString()}");
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        return JsonNode.Parse(text)!;
    }
}
