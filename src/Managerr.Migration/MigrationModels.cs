using Managerr.Common.Crypto;

namespace Managerr.Migration;

public static class MigrationSourceKinds
{
    public const string Prowlarr = "prowlarr";
    public const string NzbHydra2 = "nzbhydra2";
    public const string Sonarr = "sonarr";
    public const string Radarr = "radarr";
    public const string Lidarr = "lidarr";
    public const string Readarr = "readarr";
    public const string Overseerr = "overseerr";
}

public sealed record MigrationSourceSummary(
    long Id,
    string Name,
    string SourceKind,
    string TransportMode,
    string FileOrUrl,
    MaskedSecret ApiKeySecret,
    string ReadarrSlotHint,
    IReadOnlyDictionary<string, string> PathMappings,
    bool IsEnabled,
    string? LastSyncedAt,
    string CreatedAt,
    string UpdatedAt);

public sealed record UpsertMigrationSourceRequest(
    string Name,
    string SourceKind,
    string FileOrUrl,
    string TransportMode = "sqlite_file",
    string? ApiKey = null,
    string ReadarrSlotHint = "auto",
    IReadOnlyDictionary<string, string>? PathMappings = null,
    bool IsEnabled = true);

public sealed record AdHocMigrationSourceSpec(
    string Name,
    string SourceKind,
    string FileOrUrl,
    string TransportMode = "sqlite_file",
    string? ApiKey = null,
    string ReadarrSlotHint = "auto");

public sealed record MigrationPlanAndExecuteRequest(
    IReadOnlyList<AdHocMigrationSourceSpec>? Sources = null,
    IReadOnlyDictionary<string, string>? GlobalPathMappings = null,
    bool UpliftLanguageCustomFormats = true);

public sealed record SourceInspectionSummary(
    string Name,
    string SourceKind,
    string TransportMode,
    int? DetectedSchemaVersion,
    IReadOnlyList<string> DiscoveredRootFolders,
    int ItemsDiscovered,
    int FilesDiscovered);

public sealed record ConsolidatedDownloadClientPreview(
    string Name,
    string Implementation,
    string Host,
    int Port,
    IReadOnlyDictionary<string, string> CategoryMappings,
    IReadOnlyList<string> MergedFromSources);

public sealed record DeduplicatedIndexerPreview(
    string Name,
    string Implementation,
    string Protocol,
    string BaseUrl,
    string CanonicalSource);

public sealed record DualReadarrMergePreview(
    string BookTitle,
    string AuthorName,
    bool HasEbookSlotFile,
    bool HasAudiobookSlotFile,
    string? GoodreadsIdCrosswalked);

public sealed record MigrationReportSummary(
    long? RunId,
    string Mode, // "dry_run" | "execute"
    bool UpliftLanguageCustomFormats,
    string StartedAt,
    string FinishedAt,
    IReadOnlyList<SourceInspectionSummary> InspectedSources,
    int RawIndexerCount,
    int DeduplicatedIndexerCount,
    int SkippedProxyClonesCount,
    IReadOnlyList<DeduplicatedIndexerPreview> Indexers,
    int ProxiesImportedCount,
    int RawDownloadClientCount,
    int ConsolidatedDownloadClientCount,
    IReadOnlyList<ConsolidatedDownloadClientPreview> DownloadClients,
    int RemotePathMappingsCount,
    int QualityProfilesCompiledCount,
    int LanguageCustomFormatsUpliftedCount,
    int TvSeriesCount,
    int TvEpisodeFilesCount,
    int MoviesCount,
    int MovieFilesCount,
    int MusicArtistsCount,
    int MusicTrackFilesCount,
    int BooksUnifiedCount,
    int DualReadarrMergedBooksCount,
    int BookFilesCount,
    IReadOnlyList<DualReadarrMergePreview> DualReadarrMerges,
    int OverseerrUsersCorrelatedCount,
    int OverseerrRequestsCorrelatedCount,
    int BlocklistHashesImportedCount,
    int VerifiedOnDiskFilesCount,
    long BytesMovedOrModified,
    IReadOnlyList<string> UnmappedPaths,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Logs);

public sealed record ShadowModeEvaluateRequest(
    string MediaVertical,
    string WorkTitle,
    string ReleaseTitle,
    long ProfileId,
    string LegacyApp = "sonarr",
    string LegacyDecision = "Rejected (Cutoff Met)",
    string OriginalLanguage = "en",
    long? WorkId = null,
    long? UnitId = null,
    string? ExistingFileQuality = null,
    IReadOnlyList<string>? ExistingAudioLanguages = null);

public sealed record ShadowModeDecisionSummary(
    long Id,
    string OccurredAt,
    string MediaVertical,
    long? WorkId,
    string WorkTitle,
    string ReleaseTitle,
    string ManagerrDecision,
    double ManagerrScore,
    string LegacyApp,
    string LegacyDecision,
    bool WouldUpgradeLanguage,
    string Notes);

public sealed record CutoverExecuteRequest(
    bool RunFinalDeltaSync = true,
    bool GenerateScopedCompatKeys = true,
    bool GenerateSeparateReadarrAudiobookKey = true,
    IReadOnlyDictionary<string, string>? GlobalPathMappings = null);

public sealed record GeneratedCompatKeyInfo(
    string Label,
    string Scope,
    string MountUrl,
    string RawApiKeyOnce,
    string MaskedHint);

public sealed record CutoverExecutionResult(
    long RunId,
    string ExecutedAt,
    MigrationReportSummary? FinalDeltaReport,
    IReadOnlyList<GeneratedCompatKeyInfo> GeneratedCompatKeys,
    IReadOnlyList<string> EcosystemInstructions);

public sealed record ArrApiTokenProbeRequest(
    string BaseUrl,
    string ApiKey,
    string SourceKind = "auto",
    IReadOnlyDictionary<string, string>? PathMappings = null);

public sealed record ArrApiTokenProbeResult(
    bool Ok,
    string DetectedKind,
    string AppName,
    string Version,
    string InstanceName,
    string NormalizedBaseUrl,
    IReadOnlyList<string> RootFolders,
    int CatalogItemsCount,
    int MediaFilesCount,
    int IndexersCount,
    int DownloadClientsCount,
    int CustomFormatsCount,
    int BlocklistCount,
    string Message);

public sealed record ArrApiTokenImportRequest(
    string BaseUrl,
    string ApiKey,
    string SourceKind = "auto",
    string? Name = null,
    string ReadarrSlotHint = "auto",
    IReadOnlyDictionary<string, string>? PathMappings = null,
    bool DryRun = false,
    bool SaveAsSource = true,
    bool UpliftLanguageCustomFormats = true);

public sealed record MigrationRollbackRequest(
    bool RemoveSavedSources = true);

public sealed record MigrationRollbackResult(
    int WorksRemovedCount,
    int LibrariesRemovedCount,
    int IndexersRemovedCount,
    int DownloadClientsRemovedCount,
    int CustomFormatsRemovedCount,
    int BlocklistHashesRemovedCount,
    int SourcesRemovedCount,
    int RunsClearedCount,
    long BytesMovedOrModified,
    string Message);

