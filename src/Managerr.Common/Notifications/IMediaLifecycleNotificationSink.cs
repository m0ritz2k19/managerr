namespace Managerr.Common.Notifications;

public static class MediaNotificationEventTypes
{
    public const string Test = "Test";
    public const string OnGrab = "OnGrab";
    public const string OnImport = "OnImport";
    public const string OnUpgrade = "OnUpgrade";
    public const string OnRename = "OnRename";
    public const string OnDelete = "OnDelete";
    public const string OnHealthIssue = "OnHealthIssue";
    public const string OnApplicationUpdate = "OnApplicationUpdate";
}

public sealed record MediaLifecycleNotificationEvent(
    string EventType,
    string Title,
    string Message,
    long? WorkId = null,
    string? WorkTitle = null,
    string? MediaVertical = null,
    string? EpisodeCode = null,
    string? ReleaseTitle = null,
    string? QualityName = null,
    IReadOnlyList<string>? Languages = null,
    bool IsUpgrade = false,
    string? DestinationPath = null,
    IReadOnlyList<string>? WorkTags = null,
    IReadOnlyDictionary<string, string>? ExtraMetadata = null);

public interface IMediaLifecycleNotificationSink
{
    Task PublishAsync(MediaLifecycleNotificationEvent notification, CancellationToken ct = default);
}

public sealed class NullMediaLifecycleNotificationSink : IMediaLifecycleNotificationSink
{
    public static readonly NullMediaLifecycleNotificationSink Instance = new();

    public Task PublishAsync(MediaLifecycleNotificationEvent notification, CancellationToken ct = default) =>
        Task.CompletedTask;
}
