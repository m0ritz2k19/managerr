using Managerr.Metadata;

namespace Managerr.Catalog;

public sealed record LibrarySummary(
    long Id,
    string Slug,
    string Name,
    string MediaType,
    string RootFolderPath,
    string? PlexSectionId,
    IReadOnlyList<string> MetadataWaterfall,
    long? DefaultProfileId,
    int WorkCount,
    string CreatedAt,
    string UpdatedAt,
    long? FreeSpaceBytes = null,
    long? TotalSpaceBytes = null,
    bool RootAccessible = true);

public sealed record CreateLibraryRequest(
    string Name,
    string MediaType,
    string RootFolderPath,
    string? Slug = null,
    string? PlexSectionId = null,
    IReadOnlyList<string>? MetadataWaterfall = null,
    long? DefaultProfileId = null);

public sealed record UpdateLibraryRequest(
    string? Name = null,
    string? RootFolderPath = null,
    string? PlexSectionId = null,
    IReadOnlyList<string>? MetadataWaterfall = null,
    long? DefaultProfileId = null);

public sealed record MediaFileVerificationSummary(
    string Verdict,
    double OverallConfidence,
    string? AudioMethod,
    string? SubMethod,
    IReadOnlyList<string> DetectedAudioLanguages,
    IReadOnlyList<string> DetectedSubLanguages,
    long DurationMs,
    string VerifiedAt,
    string? FailureReason = null,
    string? FailureCode = null,
    int? MatchedPolicyTier = null,
    string? MatchedPolicyTierLabel = null,
    bool? SatisfiesLanguageCutoff = null);

public sealed record MediaFileSummary(
    long Id,
    long WorkId,
    long UnitId,
    long? EditionId,
    string RelativePath,
    long SizeBytes,
    string QualityName,
    string? Resolution,
    string? VideoCodec,
    string? HdrFormat,
    string? AudioCodec,
    IReadOnlyList<string> DetectedAudioLangs,
    IReadOnlyList<string> DetectedSubLangs,
    string VerifiedStatus,
    bool InodeHardlinked,
    string ImportedAt,
    string? SourceQualityName = null,
    string? SourceVideoCodec = null,
    long? SourceSizeBytes = null,
    MediaFileVerificationSummary? LanguageVerification = null);

public sealed record MediaEditionSummary(
    long Id,
    long UnitId,
    string EditionName,
    bool IsPreferred,
    string CreatedAt);

public sealed record MediaUnitDetail(
    long Id,
    long WorkId,
    long ContainerId,
    string UnitKind,
    int UnitNumber,
    int? AbsoluteNumber,
    string Title,
    string? AirOrReleaseDate,
    bool Monitored,
    long? ProfileOverrideId,
    IReadOnlyList<string> RequiredAudioLangs,
    IReadOnlyList<string> RequiredSubLangs,
    bool QualityCutoffMet,
    bool LanguageCutoffMet,
    string? PlexRatingKey,
    int WatchCount,
    string? LastWatchedAt,
    IReadOnlyList<MediaEditionSummary> Editions,
    IReadOnlyList<MediaFileSummary> Files);

public sealed record MediaContainerDetail(
    long Id,
    long WorkId,
    string ContainerKind,
    int ContainerNumber,
    string Title,
    string? ReleaseDate,
    bool Monitored,
    IReadOnlyDictionary<string, string> ExternalIds,
    string? PlexRatingKey,
    IReadOnlyList<MediaUnitDetail> Units);

public sealed record MediaWorkSummary(
    long Id,
    long LibraryId,
    string WorkKind,
    string PrimaryProvider,
    string EpisodeOrderingScheme,
    string Title,
    string SortTitle,
    IReadOnlyList<string> AltTitles,
    string OriginalLanguage,
    int? Year,
    string Status,
    string Overview,
    string? PosterUrl,
    bool Monitored,
    IReadOnlyList<string> LockedFields,
    IReadOnlyDictionary<string, string> ExternalIds,
    IReadOnlyList<string> Tags,
    string? PlexRatingKey,
    long? AddedByUserId,
    int ContainerCount,
    int UnitCount,
    int FileCount,
    string AddedAt,
    string UpdatedAt,
    string MinimumAvailability = "released",
    long? QualityProfileId = null,
    long? LanguagePolicyId = null,
    int? MonitoredUnitCount = null,
    int? DownloadedUnitCount = null);

public sealed record MediaWorkDetailTree(
    MediaWorkSummary Work,
    IReadOnlyList<MediaContainerDetail> Containers);

public sealed record CreateMediaWorkRequest(
    long LibraryId,
    string Title,
    string? WorkKind = null,
    string PrimaryProvider = "auto",
    string EpisodeOrderingScheme = "aired",
    IReadOnlyList<string>? AltTitles = null,
    string OriginalLanguage = "en",
    int? Year = null,
    string Status = "released",
    string Overview = "",
    string? PosterUrl = null,
    bool Monitored = true,
    IReadOnlyList<string>? LockedFields = null,
    IReadOnlyDictionary<string, string>? ExternalIds = null,
    IReadOnlyList<string>? Tags = null,
    string? PlexRatingKey = null,
    IReadOnlyList<MetadataContainerSeed>? Containers = null,
    bool DualFormatBookSlots = true,
    IReadOnlyList<string>? InitialRequiredAudioLangs = null,
    IReadOnlyList<string>? InitialRequiredSubLangs = null,
    string? MonitorMode = null,
    long? InitialProfileOverrideId = null,
    string MinimumAvailability = "released",
    long? QualityProfileId = null,
    bool SkipUpstreamHydration = false,
    long? LanguagePolicyId = null);

public sealed record UpdateMediaWorkRequest(
    string? Title = null,
    string? Overview = null,
    string? PosterUrl = null,
    bool? Monitored = null,
    string? PrimaryProvider = null,
    string? EpisodeOrderingScheme = null,
    IReadOnlyList<string>? LockedFields = null,
    IReadOnlyList<string>? Tags = null,
    string? MinimumAvailability = null,
    long? QualityProfileId = null,
    bool ClearQualityProfile = false,
    string? MonitorPreset = null,
    long? LanguagePolicyId = null,
    bool ClearLanguagePolicy = false,
    string? OriginalLanguage = null);

public sealed record BulkEditWorksRequest(
    IReadOnlyList<long> WorkIds,
    bool? Monitored = null,
    string? MonitorPreset = null,
    string? MinimumAvailability = null,
    long? QualityProfileId = null,
    bool ClearQualityProfile = false,
    long? LibraryId = null,
    bool MoveFilesOnLibraryChange = false,
    IReadOnlyList<string>? Tags = null,
    string? TagMode = null,
    bool DeleteWorks = false,
    bool DeleteFilesOnDisk = false,
    long? LanguagePolicyId = null,
    bool ClearLanguagePolicy = false,
    string? OriginalLanguage = null);

public sealed record BulkEditWorksReportDto(
    int TargetedCount,
    int UpdatedCount,
    int DeletedCount,
    int MovedFilesCount,
    IReadOnlyList<string> Messages);

public static class MinimumAvailabilityPolicy
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "released";
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "announced" => "announced",
            "incinemas" or "in_cinemas" or "in cinemas" => "inCinemas",
            _ => "released",
        };
    }

    public static bool IsWorkAvailableForAutoGrab(MediaWorkSummary work, string? unitAirOrReleaseDate = null)
    {
        if (!string.Equals(work.WorkKind, MetadataVerticals.Movies, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var minAvail = Normalize(work.MinimumAvailability);
        if (string.Equals(minAvail, "announced", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var statusNorm = (work.Status ?? "released").Trim().ToLowerInvariant();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly? parsedRelease = null;
        if (!string.IsNullOrWhiteSpace(unitAirOrReleaseDate) &&
            unitAirOrReleaseDate.Length >= 10 &&
            DateOnly.TryParse(unitAirOrReleaseDate[..10], out var dt))
        {
            parsedRelease = dt;
        }

        if (string.Equals(minAvail, "inCinemas", StringComparison.OrdinalIgnoreCase))
        {
            if (statusNorm is "incinemas" or "in cinemas" or "released")
            {
                return true;
            }

            if (parsedRelease.HasValue && parsedRelease.Value <= today.AddDays(60))
            {
                return true;
            }

            return false;
        }

        // Default: 'released'
        if (statusNorm is "announced" or "in production" or "post production" or "incinemas" or "in cinemas" or "upcoming")
        {
            return parsedRelease.HasValue && parsedRelease.Value <= today;
        }

        if (parsedRelease.HasValue)
        {
            return parsedRelease.Value <= today;
        }

        return !work.Year.HasValue || work.Year.Value <= today.Year;
    }
}

public sealed record UpdateMediaContainerRequest(
    bool Monitored,
    bool CascadeUnits = true);

public sealed record UpdateMediaUnitRequest(
    bool? Monitored = null,
    long? ProfileOverrideId = null,
    bool ClearProfileOverride = false,
    IReadOnlyList<string>? RequiredAudioLangs = null,
    IReadOnlyList<string>? RequiredSubLangs = null,
    bool? QualityCutoffMet = null,
    bool? LanguageCutoffMet = null,
    string? PlexRatingKey = null,
    int? WatchCount = null,
    string? LastWatchedAt = null);

public sealed record AttachMediaFileRequest(
    long UnitId,
    string RelativePath,
    long SizeBytes,
    string QualityName,
    string? EditionName = null,
    string? Resolution = null,
    string? VideoCodec = null,
    string? HdrFormat = null,
    string? AudioCodec = null,
    IReadOnlyList<string>? DetectedAudioLangs = null,
    IReadOnlyList<string>? DetectedSubLangs = null,
    string VerifiedStatus = "verified",
    bool InodeHardlinked = true,
    string? SourceQualityName = null,
    string? SourceVideoCodec = null,
    long? SourceSizeBytes = null);

public sealed record CalendarEntry(
    long UnitId,
    long WorkId,
    long LibraryId,
    string WorkTitle,
    string WorkKind,
    string ContainerTitle,
    int ContainerNumber,
    string UnitKind,
    int UnitNumber,
    string UnitTitle,
    string AirOrReleaseDate,
    bool Monitored,
    bool HasFile);

public sealed record MediaRequestSummary(
    long Id,
    long RequesterUserId,
    string RequesterUsername,
    long LibraryId,
    long? WorkId,
    string MediaVertical,
    string Title,
    IReadOnlyDictionary<string, string> ExternalIds,
    string Status,
    string? RequestedAudioLang,
    string? RequestedSubLang,
    string CreatedAt);

public sealed record SubmitMediaRequestCommand(
    long LibraryId,
    string Title,
    string? MediaVertical = null,
    IReadOnlyDictionary<string, string>? ExternalIds = null,
    string? RequestedAudioLang = null,
    string? RequestedSubLang = null);

public sealed record CatalogAutoClassifyItemDto(
    long WorkId,
    string Title,
    string WorkKind,
    string ClassificationKind,
    IReadOnlyList<string> AddedTags,
    IReadOnlyList<string> AllTags);

public sealed record CatalogAutoClassifyReportDto(
    int EvaluatedCount,
    int UpdatedCount,
    IReadOnlyDictionary<string, int> CountsByClassification,
    IReadOnlyList<CatalogAutoClassifyItemDto> Items);

public sealed record CatalogWorkQueryFilter(
    long? LibraryId = null,
    string? LibraryIds = null,
    string? Vertical = null,
    string? Query = null,
    bool? Monitored = null,
    string? Availability = null,
    string? Classification = null,
    string? Health = null,
    string? Sort = null,
    string? Order = null,
    int? Limit = null,
    int? Offset = null);

public readonly record struct CatalogWorkProgress(
    int DownloadedUnits,
    int TargetUnits,
    int TotalUnits,
    int MissingMonitoredUnits,
    bool IsComplete,
    bool HasMissingMonitored,
    bool IsMissing);

