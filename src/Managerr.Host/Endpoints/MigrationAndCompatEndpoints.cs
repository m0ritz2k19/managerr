using System.Text.Json.Nodes;
using Dapper;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Downloaders.Parser;
using Managerr.Host.Auth;
using Managerr.Host.Services;
using Managerr.Indexers;
using Managerr.Indexers.Torznab;
using Managerr.Metadata;
using Managerr.Migration;
using Managerr.Profiles;
using Managerr.Security.Rbac;

namespace Managerr.Host.Endpoints;

public sealed record CompatSettingsHttpDto(bool ExpandSingleSeasonAnime = false);

public static class MigrationAndCompatEndpoints
{
    public static void MapMigrationAndCompatEndpoints(this IEndpointRouteBuilder app)
    {
        // =====================================================================
        // 1. Native Migration Wizard, Shadow Mode & Cutover (/api/v1/migration)
        // =====================================================================
        var mig = app.MapGroup("/api/v1/migration").WithTags("Migration");

        mig.MapGet("/sources", async (ILegacyMigrationService svc, CancellationToken ct) =>
                Results.Ok(await svc.ListSourcesAsync(ct)))
            .RequireScope(PermissionScopes.MigrationRead);

        mig.MapPost("/sources", async (UpsertMigrationSourceRequest req, ILegacyMigrationService svc, CancellationToken ct) =>
            {
                var created = await svc.UpsertSourceAsync(req, null, ct);
                return Results.Created($"/api/v1/migration/sources/{created.Id}", created);
            })
            .RequireScope(PermissionScopes.MigrationExecute);

        mig.MapPut("/sources/{id:long}", async (long id, UpsertMigrationSourceRequest req, ILegacyMigrationService svc, CancellationToken ct) =>
                Results.Ok(await svc.UpsertSourceAsync(req, id, ct)))
            .RequireScope(PermissionScopes.MigrationExecute);

        mig.MapDelete("/sources/{id:long}", async (long id, ILegacyMigrationService svc, CancellationToken ct) =>
            {
                await svc.DeleteSourceAsync(id, ct);
                return Results.NoContent();
            })
            .RequireScope(PermissionScopes.MigrationExecute);

        mig.MapPost("/arr-api/probe", async (ArrApiTokenProbeRequest req, ILegacyMigrationService svc, CancellationToken ct) =>
                Results.Ok(await svc.ProbeArrApiSourceAsync(req, ct)))
            .RequireScope(PermissionScopes.MigrationExecute);

        mig.MapPost("/arr-api/import", async (
                HttpContext ctx,
                ArrApiTokenImportRequest req,
                ILegacyMigrationService svc,
                IAuditLogService audit,
                CancellationToken ct) =>
            {
                var principal = ctx.GetRequiredPrincipal();
                var report = await svc.ImportFromArrApiTokenAsync(req, ct);
                await audit.RecordAsync(
                    new AuditWriteRequest(
                        Action: req.DryRun ? "migration.arr_api_preview" : "migration.arr_api_imported",
                        ResourceType: "migration_run",
                        ResourceId: report.RunId?.ToString(),
                        Details: new
                        {
                            req.BaseUrl,
                            req.SourceKind,
                            req.DryRun,
                            report.DeduplicatedIndexerCount,
                            report.ConsolidatedDownloadClientCount,
                            report.TvSeriesCount,
                            report.TvEpisodeFilesCount,
                            report.MoviesCount,
                            report.MovieFilesCount,
                            report.MusicArtistsCount,
                            report.BooksUnifiedCount,
                            report.BlocklistHashesImportedCount,
                        },
                        ActorUserId: principal.User?.Id,
                        ActorPlexUserId: principal.User?.PlexUserId,
                        ActorUsername: principal.User?.Username,
                        ActorApiKeyPrefix: principal.ApiKeyPrefix,
                        IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                        TraceId: ctx.TraceIdentifier),
                    ct);
                return Results.Ok(report);
            })
            .RequireScope(PermissionScopes.MigrationExecute);

        mig.MapPost("/plan", async (HttpContext ctx, MigrationPlanAndExecuteRequest req, ILegacyMigrationService svc, CancellationToken ct) =>
            {
                if (req.Sources is { Count: > 0 } && !ctx.User.HasClaim("scope", PermissionScopes.MigrationExecute))
                {
                    return Results.Forbid();
                }

                return Results.Ok(await svc.PlanMigrationAsync(req, ct));
            })
            .RequireScope(PermissionScopes.MigrationRead);

        mig.MapPost("/execute", async (
                HttpContext ctx,
                MigrationPlanAndExecuteRequest req,
                ILegacyMigrationService svc,
                IAuditLogService audit,
                CancellationToken ct) =>
            {
                var principal = ctx.GetRequiredPrincipal();
                var report = await svc.ExecuteMigrationAsync(req, ct);
                await audit.RecordAsync(
                    new AuditWriteRequest(
                        Action: "migration.executed",
                        ResourceType: "migration_run",
                        ResourceId: report.RunId?.ToString(),
                        Details: new
                        {
                            report.DeduplicatedIndexerCount,
                            report.SkippedProxyClonesCount,
                            report.ConsolidatedDownloadClientCount,
                            report.TvSeriesCount,
                            report.MoviesCount,
                            report.BooksUnifiedCount,
                            report.BytesMovedOrModified,
                        },
                        ActorUserId: principal.User?.Id,
                        ActorPlexUserId: principal.User?.PlexUserId,
                        ActorUsername: principal.User?.Username,
                        ActorApiKeyPrefix: principal.ApiKeyPrefix,
                        IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                        TraceId: ctx.TraceIdentifier),
                    ct);
                return Results.Ok(report);
            })
            .RequireScope(PermissionScopes.MigrationExecute);

        mig.MapGet("/runs", async (int? limit, ILegacyMigrationService svc, CancellationToken ct) =>
                Results.Ok(await svc.ListMigrationRunsAsync(limit ?? 25, ct)))
            .RequireScope(PermissionScopes.MigrationRead);

        mig.MapPost("/rollback", async (
                HttpContext ctx,
                MigrationRollbackRequest? req,
                ILegacyMigrationService svc,
                IAuditLogService audit,
                CancellationToken ct) =>
            {
                var principal = ctx.GetRequiredPrincipal();
                var res = await svc.RollbackMigratedDataAsync(req, ct);
                await audit.RecordAsync(
                    new AuditWriteRequest(
                        Action: "migration.rollback",
                        ResourceType: "migration_run",
                        Details: res,
                        ActorUserId: principal.User?.Id,
                        ActorPlexUserId: principal.User?.PlexUserId,
                        ActorUsername: principal.User?.Username,
                        ActorApiKeyPrefix: principal.ApiKeyPrefix,
                        IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                        TraceId: ctx.TraceIdentifier),
                    ct);
                return Results.Ok(res);
            })
            .RequireScope(PermissionScopes.MigrationExecute);

        mig.MapGet("/shadow-decisions", async (int? limit, ILegacyMigrationService svc, CancellationToken ct) =>
                Results.Ok(await svc.ListShadowModeDecisionsAsync(limit ?? 50, ct)))
            .RequireScope(PermissionScopes.MigrationRead);

        mig.MapPost("/shadow-decisions/evaluate", async (
                ShadowModeEvaluateRequest req,
                ILegacyMigrationService svc,
                CancellationToken ct) =>
                Results.Ok(await svc.EvaluateShadowModeReleaseAsync(req, ct)))
            .RequireScope(PermissionScopes.MigrationExecute);

        mig.MapPost("/cutover", async (
                HttpContext ctx,
                CutoverExecuteRequest req,
                ILegacyMigrationService svc,
                IAuditLogService audit,
                CancellationToken ct) =>
            {
                var principal = ctx.GetRequiredPrincipal();
                var ownerUserId = principal.User?.Id ?? 1;
                var cutover = await svc.ExecuteCutoverAsync(ownerUserId, req, ct);
                await audit.RecordAsync(
                    new AuditWriteRequest(
                        Action: "migration.cutover",
                        ResourceType: "migration_run",
                        ResourceId: cutover.RunId.ToString(),
                        Details: new { KeysIssued = cutover.GeneratedCompatKeys.Count },
                        ActorUserId: principal.User?.Id,
                        ActorPlexUserId: principal.User?.PlexUserId,
                        ActorUsername: principal.User?.Username,
                        ActorApiKeyPrefix: principal.ApiKeyPrefix,
                        IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                        TraceId: ctx.TraceIdentifier),
                    ct);
                return Results.Ok(cutover);
            })
            .RequireScope(PermissionScopes.MigrationExecute);

        // =====================================================================
        // 2. Sonarr v3/v5 Compatibility Shim (/compat/sonarr/api/v3/*)
        // =====================================================================
        var sonarr = app.MapGroup("/compat/sonarr/api/v3")
            .WithTags("Compat: Sonarr")
            .RequireScope(PermissionScopes.CompatSonarr);

        sonarr.MapGet("/system/status", (ICompatShimService shim) =>
            Results.Ok(shim.BuildSystemStatus("Sonarr", "4.0.10.2544", "/compat/sonarr")));

        sonarr.MapGet("/rootfolder", async (ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.ListRootFoldersAsync("sonarr", ct)));

        sonarr.MapGet("/qualityprofile", async (ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.ListQualityProfilesAsync("sonarr", ct)));

        sonarr.MapGet("/languageprofile", (ICompatShimService shim) =>
            Results.Ok(shim.ListLegacyLanguageOrMetadataProfiles("language")));

        sonarr.MapGet("/customformat", async (ITrashGuidesImporter trash, CancellationToken ct) =>
            Results.Ok(await trash.ListCustomFormatsAsync(ct)));

        sonarr.MapPost("/customformat", async (HttpRequest req, ITrashGuidesImporter trash, CancellationToken ct) =>
        {
            using var sr = new StreamReader(req.Body);
            var raw = await sr.ReadToEndAsync(ct);
            var imported = await trash.ImportJsonAsync(raw, ct);
            return Results.Created("/compat/sonarr/api/v3/customformat", imported.Formats.LastOrDefault());
        });

        sonarr.MapGet("/tag", async (ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.ListTagsAsync(ct)));

        sonarr.MapGet("/tag/{id:int}", async (int id, ICompatShimService shim, CancellationToken ct) =>
        {
            var tag = await shim.GetTagByIdAsync(id, ct);
            return tag is null ? Results.NotFound() : Results.Ok(tag);
        });

        sonarr.MapPut("/tag/{id:int}", async (int id, JsonObject body, ICompatShimService shim, CancellationToken ct) =>
        {
            var label = body["label"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(label))
            {
                return Results.BadRequest(new { message = "label is required" });
            }

            var renamed = await shim.RenameTagAsync(id, label, ct);
            return renamed is null ? Results.NotFound() : Results.Accepted($"/compat/sonarr/api/v3/tag/{id}", renamed);
        });

        sonarr.MapPost("/tag", async (JsonObject body, ICompatShimService shim, CancellationToken ct) =>
        {
            var label = body["label"]?.GetValue<string>() ?? "tag";
            var created = await shim.GetOrCreateTagAsync(label, ct);
            return Results.Created($"/compat/sonarr/api/v3/tag/{created.Id}", created);
        });

        sonarr.MapGet("/series", async (int? tvdbId, ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.ListSonarrSeriesAsync(tvdbId, ct)));

        sonarr.MapGet("/series/{id:long}", async (long id, ICompatShimService shim, CancellationToken ct) =>
        {
            var item = await shim.GetSonarrSeriesByIdAsync(id, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        sonarr.MapGet("/series/lookup", async (string? term, ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.LookupSonarrSeriesAsync(term, ct)));

        sonarr.MapPost("/series", async (HttpContext ctx, JsonObject body, ICompatShimService shim, CancellationToken ct) =>
        {
            long? headerPlexId = long.TryParse(ctx.Request.Headers["X-Overseerr-Plex-User-Id"].FirstOrDefault(), out var hp) ? hp : null;
            var created = await shim.AddSonarrSeriesAsync(body, headerPlexId, ct);
            return Results.Created("/compat/sonarr/api/v3/series", created);
        });

        sonarr.MapPut("/series", async (HttpContext ctx, JsonObject body, ICompatShimService shim, CancellationToken ct) =>
        {
            long? headerPlexId = long.TryParse(ctx.Request.Headers["X-Overseerr-Plex-User-Id"].FirstOrDefault(), out var hp) ? hp : null;
            var updated = await shim.UpdateSonarrSeriesAsync(null, body, headerPlexId, ct);
            return updated is null ? Results.NotFound() : Results.Accepted("/compat/sonarr/api/v3/series", updated);
        });

        sonarr.MapPut("/series/{id:long}", async (long id, HttpContext ctx, JsonObject body, ICompatShimService shim, CancellationToken ct) =>
        {
            long? headerPlexId = long.TryParse(ctx.Request.Headers["X-Overseerr-Plex-User-Id"].FirstOrDefault(), out var hp) ? hp : null;
            var updated = await shim.UpdateSonarrSeriesAsync(id, body, headerPlexId, ct);
            return updated is null ? Results.NotFound() : Results.Accepted($"/compat/sonarr/api/v3/series/{id}", updated);
        });

        sonarr.MapDelete("/series/{id:long}", async (long id, ICompatShimService shim, CancellationToken ct) =>
        {
            var deleted = await shim.DeleteSonarrSeriesAsync(id, ct);
            return deleted ? Results.Ok(new { }) : Results.NotFound();
        });

        sonarr.MapGet("/episode", async (long? seriesId, int? seasonNumber, ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.ListSonarrEpisodesAsync(seriesId, seasonNumber, ct)));

        sonarr.MapPut("/episode/monitor", async (JsonObject body, ICompatShimService shim, CancellationToken ct) =>
        {
            var ids = (body["episodeIds"] as JsonArray ?? []).Select(x => x!.GetValue<long>()).ToList();
            var monitored = body["monitored"]?.GetValue<bool>() ?? true;
            return Results.Accepted("/compat/sonarr/api/v3/episode", await shim.MonitorSonarrEpisodesAsync(ids, monitored, ct));
        });

        sonarr.MapGet("/episodefile", async (long? seriesId, ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.ListSonarrEpisodeFilesAsync(seriesId, ct)));

        sonarr.MapGet("/queue", async (int? page, int? pageSize, ICompatShimService shim, CancellationToken ct) =>
        {
            var records = await shim.ListCompatQueueAsync("sonarr", ct);
            return Results.Ok(new { page = page ?? 1, pageSize = pageSize ?? 50, totalRecords = records.Count, records });
        });

        sonarr.MapGet("/command", () => Results.Ok(Array.Empty<object>()));

        sonarr.MapGet("/command/{id:int}", (int id) =>
            Results.Ok(new { id, status = "completed", stateChangeTime = DateTimeOffset.UtcNow.ToString("O") }));

        sonarr.MapPost("/command", async (JsonObject body, ICompatShimService shim, IAutomatedRssSyncService rssSync, CancellationToken ct) =>
        {
            if (string.Equals(body["name"]?.GetValue<string>(), "RssSync", StringComparison.OrdinalIgnoreCase))
            {
                await rssSync.ExecuteRssSyncAndGrabAsync(ct);
            }

            var cmd = await shim.ExecuteSonarrCommandAsync(body, ct);
            return Results.Created("/compat/sonarr/api/v3/command/1", cmd);
        });

        sonarr.MapPost("/release/push", async (PushReleaseRequest req, IReleaseParserRouter parserRouter, IAutomatedRssSyncService rssSync, CancellationToken ct) =>
        {
            var parsed = parserRouter.Parse(req.Title, req.MediaVertical ?? "tv");
            var pubDate = DateTimeOffset.TryParse(req.PublishDate, out var parsedDt) ? parsedDt : DateTimeOffset.UtcNow;
            var candidate = new IndexedReleaseCandidate(
                Guid: !string.IsNullOrWhiteSpace(req.Guid) ? req.Guid : req.DownloadUrl,
                IndexerId: req.IndexerId ?? 1,
                IndexerName: !string.IsNullOrWhiteSpace(req.Indexer) ? req.Indexer : "Push",
                Protocol: !string.IsNullOrWhiteSpace(req.Protocol) ? req.Protocol.ToLowerInvariant() : "usenet",
                MediaVertical: parsed.MediaVertical is "anime" ? "anime" : "tv",
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

            var report = await rssSync.EvaluatePushedReleasesAsync([candidate], ct);
            return Results.Ok(report.Decisions);
        });

        // =====================================================================
        // 3. Radarr v3/v5 Compatibility Shim (/compat/radarr/api/v3/*)
        // =====================================================================
        var radarr = app.MapGroup("/compat/radarr/api/v3")
            .WithTags("Compat: Radarr")
            .RequireScope(PermissionScopes.CompatRadarr);

        radarr.MapGet("/system/status", (ICompatShimService shim) =>
            Results.Ok(shim.BuildSystemStatus("Radarr", "5.14.0.9383", "/compat/radarr")));

        radarr.MapGet("/rootfolder", async (ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.ListRootFoldersAsync("radarr", ct)));

        radarr.MapGet("/qualityprofile", async (ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.ListQualityProfilesAsync("radarr", ct)));

        radarr.MapGet("/customformat", async (ITrashGuidesImporter trash, CancellationToken ct) =>
            Results.Ok(await trash.ListCustomFormatsAsync(ct)));

        radarr.MapPost("/customformat", async (HttpRequest req, ITrashGuidesImporter trash, CancellationToken ct) =>
        {
            using var sr = new StreamReader(req.Body);
            var raw = await sr.ReadToEndAsync(ct);
            var imported = await trash.ImportJsonAsync(raw, ct);
            return Results.Created("/compat/radarr/api/v3/customformat", imported.Formats.LastOrDefault());
        });

        radarr.MapGet("/tag", async (ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.ListTagsAsync(ct)));

        radarr.MapGet("/tag/{id:int}", async (int id, ICompatShimService shim, CancellationToken ct) =>
        {
            var tag = await shim.GetTagByIdAsync(id, ct);
            return tag is null ? Results.NotFound() : Results.Ok(tag);
        });

        radarr.MapPut("/tag/{id:int}", async (int id, JsonObject body, ICompatShimService shim, CancellationToken ct) =>
        {
            var label = body["label"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(label))
            {
                return Results.BadRequest(new { message = "label is required" });
            }

            var renamed = await shim.RenameTagAsync(id, label, ct);
            return renamed is null ? Results.NotFound() : Results.Accepted($"/compat/radarr/api/v3/tag/{id}", renamed);
        });

        radarr.MapPost("/tag", async (JsonObject body, ICompatShimService shim, CancellationToken ct) =>
        {
            var label = body["label"]?.GetValue<string>() ?? "tag";
            var created = await shim.GetOrCreateTagAsync(label, ct);
            return Results.Created($"/compat/radarr/api/v3/tag/{created.Id}", created);
        });

        radarr.MapGet("/movie", async (int? tmdbId, ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.ListRadarrMoviesAsync(tmdbId, ct)));

        radarr.MapGet("/movie/{id:long}", async (long id, ICompatShimService shim, CancellationToken ct) =>
        {
            var item = await shim.GetRadarrMovieByIdAsync(id, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        radarr.MapGet("/movie/lookup", async (string? term, ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.LookupRadarrMoviesAsync(term, ct)));

        radarr.MapGet("/movie/lookup/tmdb", async (int? tmdbId, ICompatShimService shim, CancellationToken ct) =>
        {
            if (tmdbId is not > 0)
            {
                return Results.BadRequest(new { message = "tmdbId is required." });
            }
            var item = await shim.LookupRadarrMovieByTmdbAsync(tmdbId.Value, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        radarr.MapGet("/movie/lookup/imdb", async (string? imdbId, ICompatShimService shim, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(imdbId))
            {
                return Results.BadRequest(new { message = "imdbId is required." });
            }
            var item = await shim.LookupRadarrMovieByImdbAsync(imdbId, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        radarr.MapPost("/movie", async (HttpContext ctx, JsonObject body, ICompatShimService shim, CancellationToken ct) =>
        {
            long? headerPlexId = long.TryParse(ctx.Request.Headers["X-Overseerr-Plex-User-Id"].FirstOrDefault(), out var hp) ? hp : null;
            var created = await shim.AddRadarrMovieAsync(body, headerPlexId, ct);
            return Results.Created("/compat/radarr/api/v3/movie", created);
        });

        radarr.MapPut("/movie", async (HttpContext ctx, JsonObject body, ICompatShimService shim, CancellationToken ct) =>
        {
            long? headerPlexId = long.TryParse(ctx.Request.Headers["X-Overseerr-Plex-User-Id"].FirstOrDefault(), out var hp) ? hp : null;
            var updated = await shim.UpdateRadarrMovieAsync(null, body, headerPlexId, ct);
            return updated is null ? Results.NotFound() : Results.Accepted("/compat/radarr/api/v3/movie", updated);
        });

        radarr.MapPut("/movie/{id:long}", async (long id, HttpContext ctx, JsonObject body, ICompatShimService shim, CancellationToken ct) =>
        {
            long? headerPlexId = long.TryParse(ctx.Request.Headers["X-Overseerr-Plex-User-Id"].FirstOrDefault(), out var hp) ? hp : null;
            var updated = await shim.UpdateRadarrMovieAsync(id, body, headerPlexId, ct);
            return updated is null ? Results.NotFound() : Results.Accepted($"/compat/radarr/api/v3/movie/{id}", updated);
        });

        radarr.MapDelete("/movie/{id:long}", async (long id, ICompatShimService shim, CancellationToken ct) =>
        {
            var deleted = await shim.DeleteRadarrMovieAsync(id, ct);
            return deleted ? Results.Ok(new { }) : Results.NotFound();
        });

        radarr.MapGet("/moviefile", async (long? movieId, ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.ListRadarrMovieFilesAsync(movieId, ct)));

        radarr.MapGet("/queue", async (int? page, int? pageSize, ICompatShimService shim, CancellationToken ct) =>
        {
            var records = await shim.ListCompatQueueAsync("radarr", ct);
            return Results.Ok(new { page = page ?? 1, pageSize = pageSize ?? 50, totalRecords = records.Count, records });
        });

        radarr.MapGet("/command", () => Results.Ok(Array.Empty<object>()));

        radarr.MapGet("/command/{id:int}", (int id) =>
            Results.Ok(new { id, status = "completed", stateChangeTime = DateTimeOffset.UtcNow.ToString("O") }));

        radarr.MapPost("/command", async (JsonObject body, ICompatShimService shim, IAutomatedRssSyncService rssSync, CancellationToken ct) =>
        {
            if (string.Equals(body["name"]?.GetValue<string>(), "RssSync", StringComparison.OrdinalIgnoreCase))
            {
                await rssSync.ExecuteRssSyncAndGrabAsync(ct);
            }

            var cmd = await shim.ExecuteRadarrCommandAsync(body, ct);
            return Results.Created("/compat/radarr/api/v3/command/1", cmd);
        });

        radarr.MapPost("/release/push", async (PushReleaseRequest req, IAutomatedRssSyncService rssSync, CancellationToken ct) =>
        {
            var pubDate = DateTimeOffset.TryParse(req.PublishDate, out var parsedDt) ? parsedDt : DateTimeOffset.UtcNow;
            var candidate = new IndexedReleaseCandidate(
                Guid: !string.IsNullOrWhiteSpace(req.Guid) ? req.Guid : req.DownloadUrl,
                IndexerId: req.IndexerId ?? 1,
                IndexerName: !string.IsNullOrWhiteSpace(req.Indexer) ? req.Indexer : "Push",
                Protocol: !string.IsNullOrWhiteSpace(req.Protocol) ? req.Protocol.ToLowerInvariant() : "usenet",
                MediaVertical: "movies",
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

            var report = await rssSync.EvaluatePushedReleasesAsync([candidate], ct);
            return Results.Ok(report.Decisions);
        });

        // =====================================================================
        // 4. Lidarr v1 Compatibility Shim (/compat/lidarr/api/v1/*)
        // =====================================================================
        var lidarr = app.MapGroup("/compat/lidarr/api/v1")
            .WithTags("Compat: Lidarr")
            .RequireScope(PermissionScopes.CompatLidarr);

        lidarr.MapGet("/system/status", (ICompatShimService shim) =>
            Results.Ok(shim.BuildSystemStatus("Lidarr", "2.7.1.4410", "/compat/lidarr")));

        lidarr.MapGet("/rootfolder", async (ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.ListRootFoldersAsync("lidarr", ct)));

        lidarr.MapGet("/qualityprofile", async (ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.ListQualityProfilesAsync("lidarr", ct)));

        lidarr.MapGet("/metadataprofile", (ICompatShimService shim) =>
            Results.Ok(shim.ListLegacyLanguageOrMetadataProfiles("metadata")));

        lidarr.MapGet("/artist", async (ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.ListLidarrArtistsAsync(ct)));

        lidarr.MapPost("/artist", async (JsonObject body, ICompatShimService shim, CancellationToken ct) =>
        {
            var created = await shim.AddLidarrArtistAsync(body, ct);
            return Results.Created("/compat/lidarr/api/v1/artist", created);
        });

        lidarr.MapGet("/album", async (long? artistId, ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.ListLidarrAlbumsAsync(artistId, ct)));

        lidarr.MapGet("/queue", async (ICompatShimService shim, CancellationToken ct) =>
        {
            var records = await shim.ListCompatQueueAsync("lidarr", ct);
            return Results.Ok(new { page = 1, pageSize = 50, totalRecords = records.Count, records });
        });

        // =====================================================================
        // 5. Readarr v1 Compatibility Shim (/compat/readarr/api/v1/*)
        //    Supports dual Ebook vs Audiobook slot keys on unified Book works!
        // =====================================================================
        var readarr = app.MapGroup("/compat/readarr/api/v1")
            .WithTags("Compat: Readarr")
            .RequireScope(PermissionScopes.CompatReadarr);

        readarr.MapGet("/system/status", (ICompatShimService shim) =>
            Results.Ok(shim.BuildSystemStatus("Readarr", "0.4.4.2680", "/compat/readarr")));

        readarr.MapGet("/rootfolder", async (ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.ListRootFoldersAsync("readarr", ct)));

        readarr.MapGet("/qualityprofile", async (ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.ListQualityProfilesAsync("readarr", ct)));

        readarr.MapGet("/metadataprofile", (ICompatShimService shim) =>
            Results.Ok(shim.ListLegacyLanguageOrMetadataProfiles("metadata")));

        readarr.MapGet("/author", async (ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.ListReadarrAuthorsAsync(ct)));

        readarr.MapGet("/book", async (HttpContext ctx, string? slot, ICompatShimService shim, CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var headerSlot = ctx.Request.Headers["X-Readarr-Slot"].FirstOrDefault();
            var targetSlot = await shim.ResolveReadarrTargetSlotAsync(slot, headerSlot, principal.ApiKeyPrefix, ct);
            return Results.Ok(await shim.ListReadarrBooksAsync(targetSlot, ct));
        });

        readarr.MapPost("/book", async (HttpContext ctx, string? slot, JsonObject body, ICompatShimService shim, CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var headerSlot = ctx.Request.Headers["X-Readarr-Slot"].FirstOrDefault();
            var targetSlot = await shim.ResolveReadarrTargetSlotAsync(slot, headerSlot, principal.ApiKeyPrefix, ct);
            var created = await shim.AddReadarrBookAsync(body, targetSlot, ct);
            return Results.Created("/compat/readarr/api/v1/book", created);
        });

        // =====================================================================
        // 6. Prowlarr v1 + Torznab/Newznab Feeds (/compat/prowlarr & /api/v1/torznab)
        // =====================================================================
        var prowlarr = app.MapGroup("/compat/prowlarr/api/v1")
            .WithTags("Compat: Prowlarr")
            .RequireScope(PermissionScopes.CompatProwlarr);

        prowlarr.MapGet("/system/status", (ICompatShimService shim) =>
            Results.Ok(shim.BuildSystemStatus("Prowlarr", "1.27.2.4891", "/compat/prowlarr")));

        prowlarr.MapGet("/indexer", async (ICompatShimService shim, CancellationToken ct) =>
            Results.Ok(await shim.ListProwlarrIndexersAsync(ct)));

        prowlarr.MapGet("/search", async (string? query, int[]? categories, IUnifiedIndexerService idxSvc, CancellationToken ct) =>
        {
            var hits = await idxSvc.SearchAsync(
                new IndexerSearchQuery(Query: query, Categories: categories, Interactive: true),
                ct);
            return Results.Ok(hits);
        });

        // Standard Torznab / Newznab feed endpoints (/api/v1/torznab/{indexerId}/api AND /compat/prowlarr/{indexerId}/api)
        async Task<IResult> HandleTorznabAsync(
            long indexerId,
            HttpRequest req,
            ICompatShimService shim,
            CancellationToken ct)
        {
            var mode = req.Query["t"].FirstOrDefault() ?? "caps";
            var q = req.Query["q"].FirstOrDefault();
            var cat = req.Query["cat"].FirstOrDefault();
            var imdbId = req.Query["imdbid"].FirstOrDefault();
            int? tmdbId = int.TryParse(req.Query["tmdbid"].FirstOrDefault(), out var tm) ? tm : null;
            int? tvdbId = int.TryParse(req.Query["tvdbid"].FirstOrDefault(), out var tv) ? tv : null;
            int? season = int.TryParse(req.Query["season"].FirstOrDefault(), out var sn) ? sn : null;
            int? ep = int.TryParse(req.Query["ep"].FirstOrDefault(), out var en) ? en : null;
            var output = req.Query["o"].FirstOrDefault();

            var (contentType, payload) = await shim.ExecuteTorznabRequestAsync(
                indexerId, mode, q, cat, imdbId, tmdbId, tvdbId, season, ep, output, ct);
            return Results.Text(payload, contentType);
        }

        app.MapGet("/api/v1/torznab/{indexerId:long}/api", HandleTorznabAsync)
            .WithTags("Compat: Torznab")
            .RequireScope(PermissionScopes.CompatProwlarr);

        app.MapGet("/compat/prowlarr/{indexerId:long}/api", HandleTorznabAsync)
            .WithTags("Compat: Torznab")
            .RequireScope(PermissionScopes.CompatProwlarr);

        // =====================================================================
        // 7. Compatibility Shim Settings (/api/v1/compat/settings)
        // =====================================================================
        var compatSettings = app.MapGroup("/api/v1/compat").WithTags("Compat: Settings");

        compatSettings.MapGet("/settings", async (IAppSettingsService appSettings, CancellationToken ct) =>
        {
            var raw = await appSettings.GetAsync("compat.sonarr.expand_single_season_anime", ct);
            var expand = bool.TryParse(raw, out var b) && b;
            return Results.Ok(new CompatSettingsHttpDto(expand));
        }).RequireAnyScope(PermissionScopes.MigrationRead, PermissionScopes.CompatSonarr);

        compatSettings.MapPut("/settings", async (
            HttpContext ctx,
            CompatSettingsHttpDto req,
            ISqliteDbFactory dbFactory,
            IAuditLogService audit,
            CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            var val = req.ExpandSingleSeasonAnime ? "true" : "false";
            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                (conn, tx) => conn.ExecuteAsync(
                    """
                    INSERT INTO app_settings (key, value, is_secret, updated_at)
                    VALUES ('compat.sonarr.expand_single_season_anime', @Val, 0, @Now)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value, is_secret = 0, updated_at = excluded.updated_at;
                    """,
                    new { Val = val, Now = now },
                    transaction: tx),
                ct);

            var principal = ctx.GetRequiredPrincipal();
            await audit.RecordAsync(new AuditWriteRequest(
                Action: "compat.settings_updated",
                ResourceType: "compat_settings",
                ResourceId: "global",
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                Details: new { req.ExpandSingleSeasonAnime },
                TraceId: ctx.TraceIdentifier), ct);

            return Results.Ok(new CompatSettingsHttpDto(req.ExpandSingleSeasonAnime));
        }).RequireAnyScope(PermissionScopes.MigrationExecute, PermissionScopes.CompatSonarr);
    }
}
