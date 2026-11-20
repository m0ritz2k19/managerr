namespace Managerr.Profiles;

public sealed record AllowedQualityTier(
    string Name,
    int Rank,
    double Score,
    double? MinMbps = null,
    double? MaxMbps = null);

public sealed record LanguagePolicyTier(
    int Tier,
    string Id,
    string Label,
    IReadOnlyList<string> AudioContainsAll,
    IReadOnlyList<string> AudioContainsAny,
    IReadOnlyList<string> SubtitlesContainsAny,
    double Score,
    bool SatisfiesLanguageCutoff);

public sealed record LanguagePolicyOnDiskEvaluation(
    bool PolicyAllowed,
    bool SatisfiesCutoff,
    int? MatchedTier,
    string? MatchedTierLabel,
    string? FailureReason);

public sealed record LanguagePolicySummary(
    long Id,
    string Slug,
    string Name,
    string Description,
    bool IsSystemPreset,
    IReadOnlyList<LanguagePolicyTier> Tiers,
    bool EnableLanguageUpgrades,
    int LanguageCutoffTier,
    string TradeoffMode,
    int MaxQualityTierDrop,
    string? MinimumAbsoluteQuality,
    bool PreservePreviousAsAlternate,
    string VerificationMode,
    string CreatedAt,
    string UpdatedAt);

public sealed record UpsertLanguagePolicyRequest(
    string Name,
    string? Slug,
    string Description,
    IReadOnlyList<LanguagePolicyTier> Tiers,
    bool EnableLanguageUpgrades = true,
    int LanguageCutoffTier = 1,
    string TradeoffMode = "bounded_stepdown",
    int MaxQualityTierDrop = 1,
    string? MinimumAbsoluteQuality = "WEBDL-1080p",
    bool PreservePreviousAsAlternate = false,
    string VerificationMode = "verify_content_on_upgrades_and_ambiguous");

public sealed record TraitMixinRule(
    string Id,
    string Label,
    string Field,
    string Pattern,
    double Score,
    bool RequiredReject = false);

public sealed record ProfileMixinSummary(
    long Id,
    string Slug,
    string Name,
    string Category,
    IReadOnlyList<TraitMixinRule> Rules,
    string CreatedAt,
    string UpdatedAt);

public sealed record UpsertProfileMixinRequest(
    string Name,
    string? Slug,
    string Category,
    IReadOnlyList<TraitMixinRule> Rules);

public sealed record CustomFormatSpecItem(
    string Name,
    string Implementation,
    bool Negate,
    bool Required,
    string Pattern);

public sealed record CustomFormatSummary(
    long Id,
    string? TrashId,
    string Name,
    bool IncludeWhenRenaming,
    IReadOnlyList<CustomFormatSpecItem> Specifications,
    int DefaultScore,
    bool ContainsLanguageRule,
    string CreatedAt,
    string UpdatedAt);

public sealed record UpsertCustomFormatRequest(
    string Name,
    bool IncludeWhenRenaming = false,
    int DefaultScore = 0,
    IReadOnlyList<CustomFormatSpecItem>? Specifications = null,
    string? TrashId = null);

public sealed record PreferredTermScore(
    string Term,
    int Score);

public sealed record ReleaseProfileSummary(
    long Id,
    string Name,
    bool IsEnabled,
    IReadOnlyList<string> MustContain,
    IReadOnlyList<string> MustNotContain,
    IReadOnlyList<PreferredTermScore> PreferredTerms,
    long? IndexerId,
    IReadOnlyList<string> Tags,
    string? MediaVertical,
    string CreatedAt,
    string UpdatedAt);

public sealed record UpsertReleaseProfileRequest(
    string Name,
    bool IsEnabled = true,
    IReadOnlyList<string>? MustContain = null,
    IReadOnlyList<string>? MustNotContain = null,
    IReadOnlyList<PreferredTermScore>? PreferredTerms = null,
    long? IndexerId = null,
    IReadOnlyList<string>? Tags = null,
    string? MediaVertical = null);

public sealed record DelayProfileSummary(
    long Id,
    string Name,
    string PreferredProtocol,
    int UsenetDelayMinutes,
    int TorrentDelayMinutes,
    bool BypassIfHighestQuality,
    bool BypassIfAboveCustomFormatScore,
    int MinCustomFormatScoreForBypass,
    IReadOnlyList<string> Tags,
    int SortOrder,
    bool IsDefault,
    string CreatedAt,
    string UpdatedAt);

public sealed record UpsertDelayProfileRequest(
    string Name,
    string PreferredProtocol = "usenet",
    int UsenetDelayMinutes = 0,
    int TorrentDelayMinutes = 0,
    bool BypassIfHighestQuality = true,
    bool BypassIfAboveCustomFormatScore = false,
    int MinCustomFormatScoreForBypass = 0,
    IReadOnlyList<string>? Tags = null,
    int SortOrder = 100);

public sealed record DelayEvaluationDecision(
    bool IsDelayed,
    int RequiredDelayMinutes,
    double AgeMinutes,
    string MatchedProfileName,
    string PreferredProtocol,
    bool ProtocolPreferred,
    string Reason);

public sealed record QualitySizeLimitDto(
    string QualityName,
    string MediaVertical,
    double MinMbPerMin,
    double PreferredMbPerMin,
    double MaxMbPerMin,
    string UpdatedAt);

public sealed record UpsertQualitySizeLimitItem(
    string QualityName,
    string MediaVertical,
    double MinMbPerMin,
    double PreferredMbPerMin,
    double MaxMbPerMin);

public sealed record BulkUpsertQualitySizeLimitsRequest(
    IReadOnlyList<UpsertQualitySizeLimitItem> Items);

public sealed record QualityProfileSummary(
    long Id,
    string Slug,
    string Name,
    string MediaVertical,
    bool IsSystemPreset,
    IReadOnlyList<AllowedQualityTier> AllowedQualities,
    string QualityCutoff,
    long? LanguagePolicyId,
    LanguagePolicySummary? LanguagePolicy,
    IReadOnlyList<long> MixinIds,
    IReadOnlyDictionary<long, int> CustomFormatScores,
    int MinTraitScore,
    double WeightQuality,
    double WeightLanguage,
    double WeightTraits,
    double WeightEfficiency,
    IReadOnlyList<string> RetainAudioLangs,
    IReadOnlyList<string> RetainSubLangs,
    string CreatedAt,
    string UpdatedAt);

public sealed record UpsertQualityProfileRequest(
    string Name,
    string? Slug,
    string MediaVertical,
    IReadOnlyList<AllowedQualityTier> AllowedQualities,
    string QualityCutoff,
    long? LanguagePolicyId = null,
    IReadOnlyList<long>? MixinIds = null,
    IReadOnlyDictionary<long, int>? CustomFormatScores = null,
    int MinTraitScore = 0,
    double WeightQuality = 1.0,
    double WeightLanguage = 1.0,
    double WeightTraits = 1.0,
    double WeightEfficiency = 1.0,
    IReadOnlyList<string>? RetainAudioLangs = null,
    IReadOnlyList<string>? RetainSubLangs = null);

public static class ProfileDecisionKinds
{
    public const string NewGrab = "NewGrab";
    public const string QualityUpgrade = "QualityUpgrade";
    public const string LanguageUpgrade = "LanguageUpgrade";
    public const string TraitUpgrade = "TraitUpgrade";
    public const string BlockedByQualityFloor = "BlockedByQualityFloor";
    public const string CutoffsAlreadyMet = "CutoffsAlreadyMet";
    public const string RejectedByHardGate = "RejectedByHardGate";
}

public sealed record SimulateReleaseRequest(
    long ProfileId,
    string ReleaseTitle,
    string? OriginalLanguage = "en",
    long? UnitId = null,
    long? SizeBytes = null,
    int? RuntimeMinutes = null,
    IReadOnlyList<string>? ExplicitAudioLanguages = null,
    IReadOnlyList<string>? ExplicitSubtitleLanguages = null,
    string? ExistingFileQuality = null,
    IReadOnlyList<string>? ExistingFileAudioLanguages = null,
    IReadOnlyList<string>? ExistingFileSubtitleLanguages = null,
    double? ExistingTraitScore = null,
    string? ExistingSourceQuality = null,
    string? ExistingVideoCodec = null,
    IReadOnlyList<string>? IndexerFlags = null,
    long? IndexerId = null,
    IReadOnlyList<string>? WorkTags = null,
    string? MediaVertical = null,
    long? WorkId = null);

public sealed record ReleaseSimulationScorecard(
    bool Approved,
    string DecisionKind,
    string Explanation,
    string CanonicalQualityName,
    int QualityRank,
    int? MatchedLanguageTier,
    string? MatchedLanguageTierLabel,
    bool CandidateSatisfiesQualityCutoff,
    bool CandidateSatisfiesLanguageCutoff,
    bool ExistingQualityCutoffMet,
    bool ExistingLanguageCutoffMet,
    double QualityScore,
    double LanguageScore,
    double TraitScore,
    double EfficiencyScore,
    double TotalWeightedScore,
    IReadOnlyList<string> MatchedTraitRules,
    IReadOnlyList<string> HardGateFailures);
