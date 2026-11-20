using System.Text.Json;
using Dapper;

namespace Managerr.Datastore.Services;

public sealed record AuditEventEntry(
    long Id,
    string OccurredAt,
    long? ActorUserId,
    long? ActorPlexUserId,
    string? ActorUsername,
    string? ActorApiKeyPrefix,
    string? IpAddress,
    string Action,
    string ResourceType,
    string? ResourceId,
    string DetailsJson,
    string? TraceId);

public sealed record AuditWriteRequest(
    string Action,
    string ResourceType,
    string? ResourceId = null,
    object? Details = null,
    long? ActorUserId = null,
    long? ActorPlexUserId = null,
    string? ActorUsername = null,
    string? ActorApiKeyPrefix = null,
    string? IpAddress = null,
    string? TraceId = null);

public interface IAuditLogService
{
    Task RecordAsync(AuditWriteRequest entry, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEventEntry>> ListRecentAsync(int limit = 100, string? actionFilter = null, CancellationToken ct = default);
}

public sealed class AuditLogService(ISqliteDbFactory dbFactory) : IAuditLogService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task RecordAsync(AuditWriteRequest entry, CancellationToken ct = default)
    {
        var detailsJson = entry.Details is null
            ? "{}"
            : JsonSerializer.Serialize(entry.Details, JsonOpts);

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Telemetry,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO audit_events (
                        occurred_at, actor_user_id, actor_plex_user_id, actor_username,
                        actor_api_key_prefix, ip_address, action, resource_type,
                        resource_id, details_json, trace_id
                    ) VALUES (
                        @OccurredAt, @ActorUserId, @ActorPlexUserId, @ActorUsername,
                        @ActorApiKeyPrefix, @IpAddress, @Action, @ResourceType,
                        @ResourceId, @DetailsJson, @TraceId
                    );
                    """,
                    new
                    {
                        OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
                        entry.ActorUserId,
                        entry.ActorPlexUserId,
                        entry.ActorUsername,
                        entry.ActorApiKeyPrefix,
                        entry.IpAddress,
                        entry.Action,
                        entry.ResourceType,
                        entry.ResourceId,
                        DetailsJson = detailsJson,
                        entry.TraceId,
                    },
                    transaction: tx);
            },
            ct);
    }

    public async Task<IReadOnlyList<AuditEventEntry>> ListRecentAsync(
        int limit = 100,
        string? actionFilter = null,
        CancellationToken ct = default)
    {
        var bounded = Math.Clamp(limit, 1, 500);
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Telemetry, ct);

        IEnumerable<AuditRow> rows;
        if (!string.IsNullOrWhiteSpace(actionFilter))
        {
            rows = await conn.QueryAsync<AuditRow>(
                """
                SELECT id, occurred_at, actor_user_id, actor_plex_user_id, actor_username,
                       actor_api_key_prefix, ip_address, action, resource_type,
                       resource_id, details_json, trace_id
                FROM audit_events
                WHERE action = @Action
                ORDER BY id DESC
                LIMIT @Limit;
                """,
                new { Action = actionFilter, Limit = bounded });
        }
        else
        {
            rows = await conn.QueryAsync<AuditRow>(
                """
                SELECT id, occurred_at, actor_user_id, actor_plex_user_id, actor_username,
                       actor_api_key_prefix, ip_address, action, resource_type,
                       resource_id, details_json, trace_id
                FROM audit_events
                ORDER BY id DESC
                LIMIT @Limit;
                """,
                new { Limit = bounded });
        }

        return rows.Select(r => new AuditEventEntry(
            Id: r.Id,
            OccurredAt: r.OccurredAt,
            ActorUserId: r.ActorUserId,
            ActorPlexUserId: r.ActorPlexUserId,
            ActorUsername: r.ActorUsername,
            ActorApiKeyPrefix: r.ActorApiKeyPrefix,
            IpAddress: r.IpAddress,
            Action: r.Action,
            ResourceType: r.ResourceType,
            ResourceId: r.ResourceId,
            DetailsJson: r.DetailsJson,
            TraceId: r.TraceId)).ToList();
    }

    private sealed class AuditRow
    {
        public long Id { get; init; }
        public string OccurredAt { get; init; } = "";
        public long? ActorUserId { get; init; }
        public long? ActorPlexUserId { get; init; }
        public string? ActorUsername { get; init; }
        public string? ActorApiKeyPrefix { get; init; }
        public string? IpAddress { get; init; }
        public string Action { get; init; } = "";
        public string ResourceType { get; init; } = "";
        public string? ResourceId { get; init; }
        public string DetailsJson { get; init; } = "{}";
        public string? TraceId { get; init; }
    }
}
