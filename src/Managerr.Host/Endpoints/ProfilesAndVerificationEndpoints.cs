using System.Globalization;
using Dapper;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Host.Auth;
using Managerr.Profiles;
using Managerr.Security.Rbac;
using Managerr.Verification;

namespace Managerr.Host.Endpoints;

public sealed record ImportTrashFormatsHttpRequest(string RawJson);

public sealed record ApproveQuarantineHttpRequest(string ConfirmedLanguage);

public sealed record VerificationSettingsHttpDto(
    double AutoPassConfidence = 0.80,
    double QuarantineMinConfidence = 0.45,
    double AutoPatchHeaderConfidence = 0.85,
    double FakeDubCorrelationThreshold = 0.97,
    string DefaultProfileSlug = "german-first-best-quality");

public static class ProfilesAndVerificationEndpoints
{
    public static IEndpointRouteBuilder MapProfilesAndVerificationEndpoints(this IEndpointRouteBuilder app)
    {
        var profiles = app.MapGroup("/api/v1/profiles").WithTags("Profiles & Language Policies");

        profiles.MapGet("/", async (IComposableProfileService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListProfilesAsync(ct)))
            .RequireScope(PermissionScopes.ProfilesRead);

        profiles.MapGet("/quality", async (IComposableProfileService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListProfilesAsync(ct)))
            .RequireScope(PermissionScopes.ProfilesRead);

        profiles.MapGet("/{id:long}", async (long id, IComposableProfileService svc, CancellationToken ct) =>
        {
            var item = await svc.GetProfileByIdAsync(id, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        }).RequireScope(PermissionScopes.ProfilesRead);

        profiles.MapPost("/", async (
            HttpContext ctx,
            UpsertQualityProfileRequest req,
            IComposableProfileService svc,
            IAuditLogService audit,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var created = await svc.UpsertProfileAsync(req, null, ct);
            await audit.RecordAsync(new AuditWriteRequest(
                Action: "profile.created",
                ResourceType: "quality_profile",
                ResourceId: created.Id.ToString(),
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                Details: new { created.Name, created.MediaVertical, created.QualityCutoff, created.LanguagePolicyId },
                TraceId: ctx.TraceIdentifier), ct);
            return Results.Created($"/api/v1/profiles/{created.Id}", created);
        }).RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapPut("/{id:long}", async (
            long id,
            HttpContext ctx,
            UpsertQualityProfileRequest req,
            IComposableProfileService svc,
            IAuditLogService audit,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var updated = await svc.UpsertProfileAsync(req, id, ct);
            await audit.RecordAsync(new AuditWriteRequest(
                Action: "profile.updated",
                ResourceType: "quality_profile",
                ResourceId: updated.Id.ToString(),
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                Details: new { updated.Name, updated.QualityCutoff, updated.LanguagePolicyId },
                TraceId: ctx.TraceIdentifier), ct);
            return Results.Ok(updated);
        }).RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapDelete("/{id:long}", async (
            long id,
            HttpContext ctx,
            IComposableProfileService svc,
            IAuditLogService audit,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            await svc.DeleteProfileAsync(id, ct);
            await audit.RecordAsync(new AuditWriteRequest(
                Action: "profile.deleted",
                ResourceType: "quality_profile",
                ResourceId: id.ToString(),
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                Details: new { id },
                TraceId: ctx.TraceIdentifier), ct);
            return Results.NoContent();
        }).RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapGet("/language-policies", async (IComposableProfileService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListLanguagePoliciesAsync(ct)))
            .RequireAnyScope(PermissionScopes.ProfilesRead, PermissionScopes.CatalogRead, PermissionScopes.CatalogWrite);

        app.MapGet("/api/v1/language-policies", async (IComposableProfileService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListLanguagePoliciesAsync(ct)))
            .WithTags("Profiles & Language Policies")
            .RequireAnyScope(PermissionScopes.ProfilesRead, PermissionScopes.CatalogRead, PermissionScopes.CatalogWrite);

        profiles.MapPost("/language-policies", async (
            UpsertLanguagePolicyRequest req,
            IComposableProfileService svc,
            CancellationToken ct) =>
        {
            var created = await svc.UpsertLanguagePolicyAsync(req, null, ct);
            return Results.Created($"/api/v1/profiles/language-policies/{created.Id}", created);
        }).RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapPut("/language-policies/{id:long}", async (
            long id,
            UpsertLanguagePolicyRequest req,
            IComposableProfileService svc,
            CancellationToken ct) =>
            Results.Ok(await svc.UpsertLanguagePolicyAsync(req, id, ct)))
            .RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapDelete("/language-policies/{id:long}", async (
            long id,
            IComposableProfileService svc,
            CancellationToken ct) =>
        {
            try
            {
                await svc.DeleteLanguagePolicyAsync(id, ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        }).RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapGet("/mixins", async (IComposableProfileService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListMixinsAsync(ct)))
            .RequireScope(PermissionScopes.ProfilesRead);

        profiles.MapPost("/mixins", async (
            UpsertProfileMixinRequest req,
            IComposableProfileService svc,
            CancellationToken ct) =>
        {
            var created = await svc.UpsertMixinAsync(req, null, ct);
            return Results.Created($"/api/v1/profiles/mixins/{created.Id}", created);
        }).RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapPut("/mixins/{id:long}", async (
            long id,
            UpsertProfileMixinRequest req,
            IComposableProfileService svc,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await svc.UpsertMixinAsync(req, id, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        }).RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapDelete("/mixins/{id:long}", async (
            long id,
            IComposableProfileService svc,
            CancellationToken ct) =>
        {
            try
            {
                await svc.DeleteMixinAsync(id, ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        }).RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapGet("/custom-formats", async (ITrashGuidesImporter importer, CancellationToken ct) =>
            Results.Ok(await importer.ListCustomFormatsAsync(ct)))
            .RequireScope(PermissionScopes.ProfilesRead);

        profiles.MapPost("/custom-formats", async (
            UpsertCustomFormatRequest req,
            ITrashGuidesImporter importer,
            CancellationToken ct) =>
        {
            var created = await importer.UpsertCustomFormatAsync(req, null, ct);
            return Results.Created($"/api/v1/profiles/custom-formats/{created.Id}", created);
        }).RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapPut("/custom-formats/{id:long}", async (
            long id,
            UpsertCustomFormatRequest req,
            ITrashGuidesImporter importer,
            CancellationToken ct) =>
            Results.Ok(await importer.UpsertCustomFormatAsync(req, id, ct)))
            .RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapDelete("/custom-formats/{id:long}", async (
            long id,
            ITrashGuidesImporter importer,
            CancellationToken ct) =>
        {
            await importer.DeleteCustomFormatAsync(id, ct);
            return Results.NoContent();
        }).RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapGet("/release-profiles", async (IComposableProfileService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListReleaseProfilesAsync(ct)))
            .RequireScope(PermissionScopes.ProfilesRead);

        profiles.MapPost("/release-profiles", async (
            UpsertReleaseProfileRequest req,
            IComposableProfileService svc,
            CancellationToken ct) =>
        {
            var created = await svc.UpsertReleaseProfileAsync(req, null, ct);
            return Results.Created($"/api/v1/profiles/release-profiles/{created.Id}", created);
        }).RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapPut("/release-profiles/{id:long}", async (
            long id,
            UpsertReleaseProfileRequest req,
            IComposableProfileService svc,
            CancellationToken ct) =>
            Results.Ok(await svc.UpsertReleaseProfileAsync(req, id, ct)))
            .RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapDelete("/release-profiles/{id:long}", async (
            long id,
            IComposableProfileService svc,
            CancellationToken ct) =>
        {
            await svc.DeleteReleaseProfileAsync(id, ct);
            return Results.NoContent();
        }).RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapGet("/delay-profiles", async (IComposableProfileService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListDelayProfilesAsync(ct)))
            .RequireScope(PermissionScopes.ProfilesRead);

        profiles.MapPost("/delay-profiles", async (
            UpsertDelayProfileRequest req,
            IComposableProfileService svc,
            CancellationToken ct) =>
        {
            var created = await svc.UpsertDelayProfileAsync(req, null, ct);
            return Results.Created($"/api/v1/profiles/delay-profiles/{created.Id}", created);
        }).RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapPut("/delay-profiles/{id:long}", async (
            long id,
            UpsertDelayProfileRequest req,
            IComposableProfileService svc,
            CancellationToken ct) =>
            Results.Ok(await svc.UpsertDelayProfileAsync(req, id, ct)))
            .RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapDelete("/delay-profiles/{id:long}", async (
            long id,
            IComposableProfileService svc,
            CancellationToken ct) =>
        {
            await svc.DeleteDelayProfileAsync(id, ct);
            return Results.NoContent();
        }).RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapGet("/quality-sizes", async (IComposableProfileService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListQualitySizeLimitsAsync(ct)))
            .RequireScope(PermissionScopes.ProfilesRead);

        profiles.MapPut("/quality-sizes", async (
            BulkUpsertQualitySizeLimitsRequest req,
            IComposableProfileService svc,
            CancellationToken ct) =>
            Results.Ok(await svc.UpsertQualitySizeLimitsAsync(req.Items ?? [], ct)))
            .RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapPost("/import-trash", async (
            HttpContext ctx,
            ImportTrashFormatsHttpRequest req,
            ITrashGuidesImporter importer,
            IAuditLogService audit,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var result = await importer.ImportJsonAsync(req.RawJson, ct);
            await audit.RecordAsync(new AuditWriteRequest(
                Action: "profile.trash_imported",
                ResourceType: "custom_format",
                ResourceId: "batch",
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                Details: new { result.ImportedCount, result.UpdatedCount, Warnings = result.LanguageRulesDetectedForUplift.Count },
                TraceId: ctx.TraceIdentifier), ct);
            return Results.Ok(result);
        }).RequireScope(PermissionScopes.ProfilesWrite);

        profiles.MapPost("/simulate", async (
            SimulateReleaseRequest req,
            IComposableProfileService svc,
            CancellationToken ct) =>
            Results.Ok(await svc.SimulateAsync(req, ct)))
            .RequireScope(PermissionScopes.ProfilesRead);

        profiles.MapPost("/promote-user-language/{workId:long}", async (
            long workId,
            HttpContext ctx,
            IComposableProfileService svc,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            if (principal.User is null)
            {
                throw new ArgumentException("PlexUser language promotion requires an interactive Plex user session.");
            }

            var updated = await svc.PromotePlexUserLanguageAsync(workId, principal.User, ct);
            return Results.Ok(updated);
        }).RequireScope(PermissionScopes.CatalogRequest);

        // --- Language Verification & Quarantine ---
        var verify = app.MapGroup("/api/v1/verification").WithTags("Language Verification");

        verify.MapGet("/settings", async (IAppSettingsService appSettings, CancellationToken ct) =>
            Results.Ok(await ReadVerificationSettingsAsync(appSettings, ct)))
            .RequireAnyScope(PermissionScopes.LangVerifyRead, PermissionScopes.ProfilesRead);

        verify.MapPut("/settings", async (
            HttpContext ctx,
            VerificationSettingsHttpDto req,
            ISqliteDbFactory dbFactory,
            IAppSettingsService appSettings,
            IAuditLogService audit,
            CancellationToken ct) =>
        {
            var autoPass = Math.Clamp(req.AutoPassConfidence, 0.50, 0.99);
            var quarantineMin = Math.Clamp(req.QuarantineMinConfidence, 0.10, Math.Max(0.10, autoPass - 0.01));
            var autoPatch = Math.Clamp(req.AutoPatchHeaderConfidence, 0.50, 0.99);
            var fakeDub = Math.Clamp(req.FakeDubCorrelationThreshold, 0.80, 0.999);
            var defaultSlug = string.IsNullOrWhiteSpace(req.DefaultProfileSlug)
                ? "german-first-best-quality"
                : req.DefaultProfileSlug.Trim();

            var now = DateTimeOffset.UtcNow.ToString("O");
            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                async (conn, tx) =>
                {
                    await UpsertSettingAsync(conn, tx, "verification.auto_pass_confidence", autoPass.ToString("0.###", CultureInfo.InvariantCulture), now);
                    await UpsertSettingAsync(conn, tx, "verification.quarantine_min_confidence", quarantineMin.ToString("0.###", CultureInfo.InvariantCulture), now);
                    await UpsertSettingAsync(conn, tx, "verification.auto_patch_header_confidence", autoPatch.ToString("0.###", CultureInfo.InvariantCulture), now);
                    await UpsertSettingAsync(conn, tx, "verification.fake_dub_correlation_threshold", fakeDub.ToString("0.###", CultureInfo.InvariantCulture), now);
                    await UpsertSettingAsync(conn, tx, "catalog.default_profile_slug", defaultSlug, now);
                },
                ct);

            var updated = await ReadVerificationSettingsAsync(appSettings, ct);
            var principal = ctx.GetRequiredPrincipal();
            await audit.RecordAsync(new AuditWriteRequest(
                Action: "verification.settings_updated",
                ResourceType: "verification_settings",
                ResourceId: "global",
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                Details: updated,
                TraceId: ctx.TraceIdentifier), ct);

            return Results.Ok(updated);
        }).RequireAnyScope(PermissionScopes.ProfilesWrite, PermissionScopes.LangVerifyExecute, PermissionScopes.SystemAdmin);

        verify.MapGet("/reports", async (int? limit, ILanguageVerificationService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListRecentReportsAsync(limit ?? 50, ct)))
            .RequireScope(PermissionScopes.LangVerifyRead);

        verify.MapPost("/scan", async (
            HttpContext ctx,
            RunVerificationRequest req,
            ILanguageVerificationService svc,
            IAuditLogService audit,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var report = await svc.VerifyStagedFileAsync(req, ct);
            await audit.RecordAsync(new AuditWriteRequest(
                Action: "verification.executed",
                ResourceType: "verification_job",
                ResourceId: report.JobId.ToString(),
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                Details: new { report.Verdict, report.OverallConfidence, report.HeaderAutoPatched, report.QuarantineId },
                TraceId: ctx.TraceIdentifier), ct);
            return Results.Ok(report);
        }).RequireScope(PermissionScopes.LangVerifyExecute);

        var quarantine = app.MapGroup("/api/v1/quarantine").WithTags("Quarantine Review");

        quarantine.MapGet("/", async (string? status, ILanguageVerificationService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListQuarantineAsync(status, ct)))
            .RequireScope(PermissionScopes.QuarantineReview);

        quarantine.MapGet("/{id:long}/snippets/{index:int}", async (
            long id,
            int index,
            ILanguageVerificationService svc,
            Managerr.Common.Paths.IAppPaths appPaths,
            CancellationToken ct) =>
        {
            var items = await svc.ListQuarantineAsync(null, ct);
            var target = items.FirstOrDefault(x => x.Id == id)
                ?? throw new KeyNotFoundException($"Quarantined import #{id} not found.");

            if (index < 0 || index >= target.SnippetPaths.Count)
            {
                throw new ArgumentException($"Snippet index {index} is out of range.");
            }

            var path = target.SnippetPaths[index];
            var quarantineDir = Path.Combine(appPaths.ConfigDirectory, "quarantine");
            var fullRoot = Path.GetFullPath(quarantineDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(fullRoot, StringComparison.Ordinal) ||
                !fullPath.EndsWith(".opus", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }

            if (!File.Exists(fullPath))
            {
                throw new KeyNotFoundException($"Quarantine audio snippet file not found at '{path}'.");
            }

            return Results.File(fullPath, "audio/ogg; codecs=opus", Path.GetFileName(fullPath), enableRangeProcessing: true);
        }).RequireScope(PermissionScopes.QuarantineReview);

        quarantine.MapPost("/{id:long}/approve", async (
            long id,
            HttpContext ctx,
            ApproveQuarantineHttpRequest req,
            ILanguageVerificationService svc,
            IAuditLogService audit,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var reviewerId = principal.User?.Id ?? 0;
            var result = await svc.ApproveQuarantineAsync(id, reviewerId, req.ConfirmedLanguage, ct);
            await audit.RecordAsync(new AuditWriteRequest(
                Action: "quarantine.approved",
                ResourceType: "quarantined_import",
                ResourceId: id.ToString(),
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                Details: new { req.ConfirmedLanguage, result.ReleaseTitle },
                TraceId: ctx.TraceIdentifier), ct);
            return Results.Ok(result);
        }).RequireScope(PermissionScopes.QuarantineReview);

        quarantine.MapPost("/{id:long}/reject", async (
            long id,
            HttpContext ctx,
            ILanguageVerificationService svc,
            IAuditLogService audit,
            CancellationToken ct) =>
        {
            var principal = ctx.GetRequiredPrincipal();
            var reviewerId = principal.User?.Id ?? 0;
            var result = await svc.RejectAndBlocklistQuarantineAsync(id, reviewerId, ct);
            await audit.RecordAsync(new AuditWriteRequest(
                Action: "quarantine.rejected",
                ResourceType: "quarantined_import",
                ResourceId: id.ToString(),
                ActorUserId: principal.User?.Id,
                ActorPlexUserId: principal.User?.PlexUserId,
                ActorUsername: principal.User?.Username,
                ActorApiKeyPrefix: principal.ApiKeyPrefix,
                IpAddress: ctx.Connection.RemoteIpAddress?.ToString(),
                Details: new { result.ReleaseTitle, result.InfoHash },
                TraceId: ctx.TraceIdentifier), ct);
            return Results.Ok(result);
        }).RequireScope(PermissionScopes.QuarantineReview);

        return app;
    }

    private static async Task<VerificationSettingsHttpDto> ReadVerificationSettingsAsync(
        IAppSettingsService appSettings,
        CancellationToken ct)
    {
        var autoPass = ParseBoundedDouble(await appSettings.GetAsync("verification.auto_pass_confidence", ct), 0.80, 0.50, 0.99);
        var quarantineMin = ParseBoundedDouble(await appSettings.GetAsync("verification.quarantine_min_confidence", ct), 0.45, 0.10, Math.Max(0.10, autoPass - 0.01));
        var autoPatch = ParseBoundedDouble(await appSettings.GetAsync("verification.auto_patch_header_confidence", ct), 0.85, 0.50, 0.99);
        var fakeDub = ParseBoundedDouble(await appSettings.GetAsync("verification.fake_dub_correlation_threshold", ct), 0.97, 0.80, 0.999);
        var slug = await appSettings.GetAsync("catalog.default_profile_slug", ct);
        return new VerificationSettingsHttpDto(
            AutoPassConfidence: autoPass,
            QuarantineMinConfidence: quarantineMin,
            AutoPatchHeaderConfidence: autoPatch,
            FakeDubCorrelationThreshold: fakeDub,
            DefaultProfileSlug: string.IsNullOrWhiteSpace(slug) ? "german-first-best-quality" : slug.Trim());
    }

    private static double ParseBoundedDouble(string? raw, double fallback, double min, double max) =>
        !string.IsNullOrWhiteSpace(raw) && double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, min, max)
            : fallback;

    private static Task UpsertSettingAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        string key,
        string value,
        string nowIso) =>
        conn.ExecuteAsync(
            """
            INSERT INTO app_settings (key, value, is_secret, updated_at)
            VALUES (@Key, @Val, 0, @Now)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, is_secret = 0, updated_at = excluded.updated_at;
            """,
            new { Key = key, Val = value, Now = nowIso },
            transaction: tx);
}
