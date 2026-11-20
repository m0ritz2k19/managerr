using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Managerr.Catalog;
using Managerr.Common.Media;
using Managerr.Datastore;
using Managerr.Downloaders.Parser;
using Managerr.Security.Users;

namespace Managerr.Profiles;

public interface IComposableProfileService
{
    Task EnsureBuiltInProfilesSeededAsync(CancellationToken ct = default);

    Task<IReadOnlyList<LanguagePolicySummary>> ListLanguagePoliciesAsync(CancellationToken ct = default);
    Task<LanguagePolicySummary> UpsertLanguagePolicyAsync(UpsertLanguagePolicyRequest request, long? id = null, CancellationToken ct = default);
    Task DeleteLanguagePolicyAsync(long id, CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<ProfileMixinSummary>> ListMixinsAsync(CancellationToken ct = default);
    Task<ProfileMixinSummary> UpsertMixinAsync(UpsertProfileMixinRequest request, long? id = null, CancellationToken ct = default);
    Task DeleteMixinAsync(long id, CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<QualityProfileSummary>> ListProfilesAsync(CancellationToken ct = default);
    Task<QualityProfileSummary?> GetProfileByIdAsync(long id, CancellationToken ct = default);
    Task<QualityProfileSummary> UpsertProfileAsync(UpsertQualityProfileRequest request, long? id = null, CancellationToken ct = default);
    Task DeleteProfileAsync(long id, CancellationToken ct = default);

    Task<ReleaseSimulationScorecard> SimulateAsync(SimulateReleaseRequest request, CancellationToken ct = default);
    Task<MediaWorkDetailTree> PromotePlexUserLanguageAsync(long workId, PlexUserSummary user, CancellationToken ct = default);

    LanguagePolicyOnDiskEvaluation EvaluateOnDiskLanguagesAgainstPolicy(
        LanguagePolicySummary? policy,
        string? originalLanguage,
        IReadOnlyCollection<string> detectedAudioLangs,
        IReadOnlyCollection<string> detectedSubLangs);

    Task<IReadOnlyList<ReleaseProfileSummary>> ListReleaseProfilesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ReleaseProfileSummary>>([]);
    Task<ReleaseProfileSummary> UpsertReleaseProfileAsync(UpsertReleaseProfileRequest request, long? id = null, CancellationToken ct = default) =>
        throw new NotSupportedException();
    Task DeleteReleaseProfileAsync(long id, CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<DelayProfileSummary>> ListDelayProfilesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DelayProfileSummary>>([]);
    Task<DelayProfileSummary> UpsertDelayProfileAsync(UpsertDelayProfileRequest request, long? id = null, CancellationToken ct = default) =>
        throw new NotSupportedException();
    Task DeleteDelayProfileAsync(long id, CancellationToken ct = default) =>
        throw new NotSupportedException();
    Task<DelayEvaluationDecision> EvaluateDelayAsync(
        string protocol,
        DateTimeOffset? publishedAt,
        IReadOnlyList<string>? workTags,
        ReleaseSimulationScorecard scorecard,
        CancellationToken ct = default) =>
        Task.FromResult(new DelayEvaluationDecision(false, 0, 0, "Default", "any", true, "No delay profile active."));

    Task<IReadOnlyList<QualitySizeLimitDto>> ListQualitySizeLimitsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<QualitySizeLimitDto>>([]);
    Task<IReadOnlyList<QualitySizeLimitDto>> UpsertQualitySizeLimitsAsync(
        IReadOnlyList<UpsertQualitySizeLimitItem> items,
        CancellationToken ct = default) =>
        throw new NotSupportedException();
}

/// <summary>
/// Evaluates Composable Quality Profiles + Trait Mixins + TRaSH Custom Formats + First-Class Language Policies
/// using independent QualityCutoffMet and LanguageCutoffMet flags and quality-floor guardrails.
/// </summary>
public sealed partial class ComposableProfileService(
    ISqliteDbFactory dbFactory,
    IReleaseParserRouter parserRouter,
    ITrashGuidesImporter trashImporter,
    IPolymorphicCatalogService catalogService) : IComposableProfileService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"[^a-z0-9-]+")]
    private static partial Regex SlugSanitizer();

    private static readonly Dictionary<string, int> GlobalQualityHierarchy = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Remux-2160p"] = 100,
        ["BluRay-2160p"] = 90,
        ["WEBDL-2160p"] = 85,
        ["WEBRip-2160p"] = 80,
        ["Remux-1080p"] = 75,
        ["BluRay-1080p"] = 70,
        ["WEBDL-1080p"] = 65,
        ["WEBRip-1080p"] = 60,
        ["HDTV-1080p"] = 55,
        ["BluRay-720p"] = 45,
        ["WEBDL-720p"] = 40,
        ["HDTV-720p"] = 35,
        ["SD-480p"] = 15,
        [QualityNames.Unknown] = 0,
        ["FLAC 24bit"] = 95,
        ["FLAC"] = 85,
        ["MP3-320"] = 60,
        ["MP3-V0"] = 50,
        ["EPUB"] = 90,
        ["AZW3"] = 80,
        ["PDF"] = 55,
        ["M4B"] = 90,
        ["CBZ"] = 90,
        ["CBR"] = 75,
    };

    public async Task EnsureBuiltInProfilesSeededAsync(CancellationToken ct = default)
    {
        await EnsureSystemLanguagePoliciesSeededAsync(ct);

        int existingCount;
        await using (var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            existingCount = await readConn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM quality_profiles;");
        }

        if (existingCount > 0)
        {
            await EnsureGermanFirstBestQualityProfileAsync(ct);
            await EnsureWave4DefaultsSeededAsync(ct);
            return;
        }

        // 1. Fetch Built-In Language Policies
        var allPolicies = await ListLanguagePoliciesAsync(ct);
        var deDualPolicy = allPolicies.First(p => p.Slug == "de-orig-dual-fast-en");
        var origSubPolicy = allPolicies.First(p => p.Slug == "original-plus-subs");

        // 2. Seed Built-In Trait Mixins
        var codecMixin = await UpsertMixinAsync(
            new UpsertProfileMixinRequest(
                Name: "Modern Codec Efficiency & Anti-LQ",
                Slug: "codec-efficiency-av1-hevc",
                Category: "codec",
                Rules:
                [
                    new TraitMixinRule("av1", "AV1 Video Codec", "VideoCodec", "^AV1$", 150),
                    new TraitMixinRule("hevc", "HEVC / x265 Video Codec", "VideoCodec", "^HEVC$", 100),
                    new TraitMixinRule("reject-cam", "Reject CAM/TS/TELECINE", "RawTitle", @"\b(CAM|HDCAM|TELESYNC|HDTS|TELECINE)\b", -10000, RequiredReject: true),
                ]),
            id: null,
            ct);

        var hdrAudioMixin = await UpsertMixinAsync(
            new UpsertProfileMixinRequest(
                Name: "Dolby Vision / HDR10+ & Spatial Audio",
                Slug: "atmos-hdr-cinema",
                Category: "hdr_audio",
                Rules:
                [
                    new TraitMixinRule("dv-hdr10p", "DV + HDR10+ Hybrid", "HdrFormat", "DV HDR10\\+", 250),
                    new TraitMixinRule("dv-or-hdr", "Dolby Vision / HDR10", "HdrFormat", "^(DV|DV HDR10|HDR10\\+|HDR10)$", 150),
                    new TraitMixinRule("truehd-atmos", "TrueHD Atmos", "AudioCodec", "^TrueHD Atmos$", 200),
                    new TraitMixinRule("ddp-atmos", "DD+ Atmos", "AudioCodec", "^DDP Atmos$", 100),
                ]),
            id: null,
            ct);

        // 3. Seed Built-In Composable Quality Profiles across verticals
        await UpsertProfileInternalAsync(
            new UpsertQualityProfileRequest(
                Name: "HD / UltraHD Balanced (DE+Orig Dual Upgrade)",
                Slug: "hd-uhd-de-en-dual",
                MediaVertical: "video",
                AllowedQualities:
                [
                    new AllowedQualityTier("Remux-2160p", 7, 1000, 35, 120),
                    new AllowedQualityTier("BluRay-2160p", 6, 900, 15, 75),
                    new AllowedQualityTier("WEBDL-2160p", 5, 850, 10, 45),
                    new AllowedQualityTier("Remux-1080p", 4, 750, 15, 50),
                    new AllowedQualityTier("BluRay-1080p", 3, 700, 6, 30),
                    new AllowedQualityTier("WEBDL-1080p", 2, 650, 3, 20),
                    new AllowedQualityTier("WEBRip-1080p", 1, 580, 2, 16),
                ],
                QualityCutoff: "WEBDL-2160p",
                LanguagePolicyId: deDualPolicy.Id,
                MixinIds: [codecMixin.Id, hdrAudioMixin.Id],
                RetainAudioLangs: ["de", "original", "en"],
                RetainSubLangs: ["de", "en"]),
            isPreset: true,
            id: null,
            ct);

        await UpsertProfileInternalAsync(
            new UpsertQualityProfileRequest(
                Name: "Anime 1080p Original + Subs",
                Slug: "anime-orig-sub",
                MediaVertical: "video",
                AllowedQualities:
                [
                    new AllowedQualityTier("BluRay-1080p", 3, 900, 4, 28),
                    new AllowedQualityTier("WEBDL-1080p", 2, 800, 2, 18),
                    new AllowedQualityTier("WEBRip-1080p", 1, 720, 1.5, 14),
                ],
                QualityCutoff: "BluRay-1080p",
                LanguagePolicyId: origSubPolicy.Id,
                MixinIds: [codecMixin.Id]),
            isPreset: true,
            id: null,
            ct);

        await UpsertProfileInternalAsync(
            new UpsertQualityProfileRequest(
                Name: "Music Lossless FLAC",
                Slug: "music-lossless",
                MediaVertical: "music",
                AllowedQualities:
                [
                    new AllowedQualityTier("FLAC 24bit", 3, 1000),
                    new AllowedQualityTier("FLAC", 2, 900),
                    new AllowedQualityTier("MP3-320", 1, 500),
                ],
                QualityCutoff: "FLAC"),
            isPreset: true,
            id: null,
            ct);

        await UpsertProfileInternalAsync(
            new UpsertQualityProfileRequest(
                Name: "Books Retail EPUB & Audiobooks M4B",
                Slug: "books-dual-retail",
                MediaVertical: "ebooks",
                AllowedQualities:
                [
                    new AllowedQualityTier("EPUB", 3, 950),
                    new AllowedQualityTier("M4B", 3, 950),
                    new AllowedQualityTier("AZW3", 2, 800),
                    new AllowedQualityTier("PDF", 1, 500),
                ],
                QualityCutoff: "EPUB",
                LanguagePolicyId: deDualPolicy.Id),
            isPreset: true,
            id: null,
            ct);

        await EnsureGermanFirstBestQualityProfileAsync(ct);
        await EnsureWave4DefaultsSeededAsync(ct);
    }

    private async Task EnsureSystemLanguagePoliciesSeededAsync(CancellationToken ct)
    {
        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var existingSlugs = (await readConn.QueryAsync<string>("SELECT slug FROM language_policies;")).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. german-first
        if (!existingSlugs.Contains("german-first"))
        {
            await UpsertLanguagePolicyInternalAsync(
                new UpsertLanguagePolicyRequest(
                    Name: "German First (DE+Orig / DE • Orig+Sub Fallback)",
                    Slug: "german-first",
                    Description: "Prioritizes German + Original Dual-Audio (DL) and German Audio above all non-German releases, falling back to Original/English only when no German release exists and upgrading automatically.",
                    Tiers:
                    [
                        new LanguagePolicyTier(1, "de-orig-dl", "German + Original Dual-Audio (DL)", ["de", "original"], [], [], 1100, true),
                        new LanguagePolicyTier(2, "de-audio", "German Audio (Deutsch)", ["de"], [], [], 950, true),
                        new LanguagePolicyTier(3, "orig-de-subs", "Original / English + German Subtitles", [], ["original", "en"], ["de"], 300, false),
                        new LanguagePolicyTier(4, "orig-en-fallback", "Original / English Fallback", [], ["original", "en"], [], 150, false),
                    ],
                    EnableLanguageUpgrades: true,
                    LanguageCutoffTier: 2,
                    TradeoffMode: "bounded_stepdown",
                    MaxQualityTierDrop: 2,
                    MinimumAbsoluteQuality: "WEBDL-1080p"),
                isPreset: true,
                id: null,
                ct);
        }

        // 2. german-only
        if (!existingSlugs.Contains("german-only"))
        {
            await UpsertLanguagePolicyInternalAsync(
                new UpsertLanguagePolicyRequest(
                    Name: "German Audio Only (Strict DE / DE+Orig)",
                    Slug: "german-only",
                    Description: "Strictly requires German audio track (DE or DE+Original). Rejects any release without German audio, no fallback tiers.",
                    Tiers:
                    [
                        new LanguagePolicyTier(1, "de-orig-strict", "German + Original Dual-Audio", ["de", "original"], [], [], 2500, true),
                        new LanguagePolicyTier(2, "de-audio-strict", "German Audio Only", ["de"], [], [], 2000, true),
                    ],
                    EnableLanguageUpgrades: true,
                    LanguageCutoffTier: 2,
                    TradeoffMode: "never_downgrade_quality",
                    MaxQualityTierDrop: 0,
                    MinimumAbsoluteQuality: "WEBDL-1080p"),
                isPreset: true,
                id: null,
                ct);
        }

        // 3. de-orig-dual-fast-en
        if (!existingSlugs.Contains("de-orig-dual-fast-en"))
        {
            await UpsertLanguagePolicyInternalAsync(
                new UpsertLanguagePolicyRequest(
                    Name: "German + Original Dual-Audio (Fast EN/Orig Fallback)",
                    Slug: "de-orig-dual-fast-en",
                    Description: "Grabs Day-1 English/Original immediately, then upgrades to German + Original Dual-Audio when released without dropping below 1080p WEB-DL.",
                    Tiers:
                    [
                        new LanguagePolicyTier(1, "dual-de-orig", "German + Original Dual Audio", ["de", "original"], [], [], 1000, true),
                        new LanguagePolicyTier(2, "de-audio-only", "German Audio", [], ["de"], [], 750, false),
                        new LanguagePolicyTier(3, "orig-with-de-subs", "Original/EN Audio + German Subtitles", [], ["original", "en"], ["de"], 400, false),
                        new LanguagePolicyTier(4, "day1-en-or-orig", "Original or English Audio (Day-1 Grab)", [], ["original", "en"], [], 200, false),
                    ],
                    EnableLanguageUpgrades: true,
                    LanguageCutoffTier: 1,
                    TradeoffMode: "bounded_stepdown",
                    MaxQualityTierDrop: 1,
                    MinimumAbsoluteQuality: "WEBDL-1080p"),
                isPreset: true,
                id: null,
                ct);
        }

        // 4. original-plus-subs
        if (!existingSlugs.Contains("original-plus-subs"))
        {
            await UpsertLanguagePolicyInternalAsync(
                new UpsertLanguagePolicyRequest(
                    Name: "Original Audio + DE/EN Subtitles",
                    Slug: "original-plus-subs",
                    Description: "Prefers original spoken language with German or English subtitles.",
                    Tiers:
                    [
                        new LanguagePolicyTier(1, "orig-de-en-sub", "Original Audio + DE/EN Subs", ["original"], [], ["de", "en"], 1000, true),
                        new LanguagePolicyTier(2, "orig-audio", "Original Audio", [], ["original"], [], 700, true),
                    ],
                    EnableLanguageUpgrades: true,
                    LanguageCutoffTier: 1,
                    TradeoffMode: "never_downgrade_quality",
                    MaxQualityTierDrop: 0,
                    MinimumAbsoluteQuality: "WEBDL-1080p"),
                isPreset: true,
                id: null,
                ct);
        }

        // 5. original-only
        if (!existingSlugs.Contains("original-only"))
        {
            await UpsertLanguagePolicyInternalAsync(
                new UpsertLanguagePolicyRequest(
                    Name: "Original Language Audio (O-Ton)",
                    Slug: "original-only",
                    Description: "Strictly requires original language audio track.",
                    Tiers:
                    [
                        new LanguagePolicyTier(1, "orig-strict", "Original Language Audio", ["original"], [], [], 2000, true),
                    ],
                    EnableLanguageUpgrades: true,
                    LanguageCutoffTier: 1,
                    TradeoffMode: "never_downgrade_quality",
                    MaxQualityTierDrop: 0,
                    MinimumAbsoluteQuality: "WEBDL-1080p"),
                isPreset: true,
                id: null,
                ct);
        }

        // 6. english-first
        if (!existingSlugs.Contains("english-first"))
        {
            await UpsertLanguagePolicyInternalAsync(
                new UpsertLanguagePolicyRequest(
                    Name: "English First (EN+Orig / EN • Orig+EN Sub Fallback)",
                    Slug: "english-first",
                    Description: "Prioritizes English + Original and English audio, falling back to original audio with English subtitles.",
                    Tiers:
                    [
                        new LanguagePolicyTier(1, "en-orig-dl", "English + Original Dual-Audio", ["en", "original"], [], [], 2200, true),
                        new LanguagePolicyTier(2, "en-audio", "English Audio", ["en"], [], [], 1800, true),
                        new LanguagePolicyTier(3, "orig-en-subs", "Original Audio + English Subtitles", [], ["original"], ["en"], 600, false),
                    ],
                    EnableLanguageUpgrades: true,
                    LanguageCutoffTier: 2,
                    TradeoffMode: "bounded_stepdown",
                    MaxQualityTierDrop: 2,
                    MinimumAbsoluteQuality: "WEBDL-1080p"),
                isPreset: true,
                id: null,
                ct);
        }

        await dbFactory.ExecuteWriteAsync(DatabaseKind.Core, async (conn, tx) =>
        {
            await conn.ExecuteAsync(
                "UPDATE language_policies SET language_cutoff_tier = 2 WHERE is_system_preset = 1 AND slug IN ('german-first', 'german-only', 'english-first') AND language_cutoff_tier = 1;",
                null,
                tx);
        }, ct);
    }

    private async Task EnsureGermanFirstBestQualityProfileAsync(CancellationToken ct)
    {
        long? profileId;
        var createdNow = false;
        await using (var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            profileId = await readConn.ExecuteScalarAsync<long?>(
                "SELECT id FROM quality_profiles WHERE slug = 'german-first-best-quality' LIMIT 1;");
        }

        if (!profileId.HasValue)
        {
            long? langPolicyId;
            await using (var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
            {
                langPolicyId = await readConn.ExecuteScalarAsync<long?>(
                    "SELECT id FROM language_policies WHERE slug = 'german-first' LIMIT 1;");
            }

            if (!langPolicyId.HasValue)
            {
                var germanFirstPolicy = await UpsertLanguagePolicyInternalAsync(
                    new UpsertLanguagePolicyRequest(
                        Name: "German First (DE+Orig / DE • Orig+Sub Fallback)",
                        Slug: "german-first",
                        Description: "Prioritizes German + Original Dual-Audio (DL) and German Audio above all non-German releases, falling back to Original/English only when no German release exists and upgrading automatically.",
                        Tiers:
                        [
                            new LanguagePolicyTier(1, "de-orig-dl", "German + Original Dual-Audio (DL)", ["de", "original"], [], [], 1100, true),
                            new LanguagePolicyTier(2, "de-audio", "German Audio (Deutsch)", ["de"], [], [], 950, true),
                            new LanguagePolicyTier(3, "orig-de-subs", "Original / English + German Subtitles", [], ["original", "en"], ["de"], 300, false),
                            new LanguagePolicyTier(4, "orig-en-fallback", "Original / English Fallback", [], ["original", "en"], [], 150, false),
                        ],
                        EnableLanguageUpgrades: true,
                        LanguageCutoffTier: 2,
                        TradeoffMode: "bounded_stepdown",
                        MaxQualityTierDrop: 2,
                        MinimumAbsoluteQuality: "WEBDL-1080p"),
                    isPreset: true,
                    id: null,
                    ct);
                langPolicyId = germanFirstPolicy.Id;
            }

            long[] mixinIds;
            await using (var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
            {
                mixinIds = (await readConn.QueryAsync<long>(
                    "SELECT id FROM profile_mixins WHERE slug IN ('codec-efficiency-av1-hevc', 'atmos-hdr-cinema') ORDER BY id ASC;")).ToArray();
            }

            var createdProfile = await UpsertProfileInternalAsync(
                new UpsertQualityProfileRequest(
                    Name: "German First — Best Quality",
                    Slug: "german-first-best-quality",
                    MediaVertical: "video",
                    AllowedQualities:
                    [
                        new AllowedQualityTier("Remux-2160p", 9, 1000),
                        new AllowedQualityTier("BluRay-2160p", 8, 920),
                        new AllowedQualityTier("WEBDL-2160p", 7, 860),
                        new AllowedQualityTier("WEBRip-2160p", 6, 800),
                        new AllowedQualityTier("Remux-1080p", 5, 750),
                        new AllowedQualityTier("BluRay-1080p", 4, 700),
                        new AllowedQualityTier("WEBDL-1080p", 3, 650),
                        new AllowedQualityTier("WEBRip-1080p", 2, 580),
                        new AllowedQualityTier("HDTV-1080p", 1, 520),
                    ],
                    QualityCutoff: "Remux-2160p",
                    LanguagePolicyId: langPolicyId,
                    MixinIds: mixinIds,
                    WeightQuality: 1.0,
                    WeightLanguage: 2.0,
                    WeightTraits: 1.0,
                    WeightEfficiency: 1.0,
                    RetainAudioLangs: ["de", "original", "en"],
                    RetainSubLangs: ["de", "en"]),
                isPreset: false,
                id: null,
                ct);

            profileId = createdProfile.Id;
            createdNow = true;
        }

        // Bind Movies, TV Series & Anime libraries to 'German First — Best Quality' when unset (or on initial creation from system preset)
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                createdNow
                    ? """
                      UPDATE libraries
                      SET default_profile_id = @ProfileId, updated_at = @Now
                      WHERE media_type IN ('movies', 'tv', 'anime')
                        AND (default_profile_id IS NULL OR default_profile_id IN (SELECT id FROM quality_profiles WHERE is_system_preset = 1));
                      """
                    : """
                      UPDATE libraries
                      SET default_profile_id = @ProfileId, updated_at = @Now
                      WHERE media_type IN ('movies', 'tv', 'anime')
                        AND default_profile_id IS NULL;
                      """,
                new { ProfileId = profileId.Value, Now = DateTimeOffset.UtcNow.ToString("O") },
                transaction: tx),
            ct);
    }

    public async Task<IReadOnlyList<LanguagePolicySummary>> ListLanguagePoliciesAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<LangPolicyRow>("SELECT * FROM language_policies ORDER BY id ASC;");
        return rows.Select(MapLanguagePolicy).ToList();
    }

    public Task<LanguagePolicySummary> UpsertLanguagePolicyAsync(
        UpsertLanguagePolicyRequest request,
        long? id = null,
        CancellationToken ct = default) =>
        UpsertLanguagePolicyInternalAsync(request, isPreset: false, id, ct);

    public async Task DeleteLanguagePolicyAsync(long id, CancellationToken ct = default)
    {
        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var existing = await readConn.QuerySingleOrDefaultAsync<(long Id, string Name, int IsSystemPreset)>(
            "SELECT id AS Id, name AS Name, is_system_preset AS IsSystemPreset FROM language_policies WHERE id = @Id;",
            new { Id = id });
        if (existing == default)
        {
            throw new KeyNotFoundException($"Language policy #{id} not found.");
        }

        if (existing.IsSystemPreset != 0)
        {
            throw new InvalidOperationException($"System preset language policy '{existing.Name}' cannot be deleted.");
        }

        var inUseCount = await readConn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM quality_profiles WHERE language_policy_id = @Id;",
            new { Id = id });
        if (inUseCount > 0)
        {
            throw new InvalidOperationException(
                $"Language policy '{existing.Name}' is in active use by {inUseCount} quality profile(s) and cannot be deleted.");
        }

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    "UPDATE media_works SET language_policy_id = NULL WHERE language_policy_id = @Id;",
                    new { Id = id },
                    transaction: tx);
                await conn.ExecuteAsync(
                    "DELETE FROM language_policies WHERE id = @Id;",
                    new { Id = id },
                    transaction: tx);
            },
            ct);
    }

    public async Task<IReadOnlyList<ProfileMixinSummary>> ListMixinsAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<MixinRow>("SELECT * FROM profile_mixins ORDER BY id ASC;");
        return rows.Select(MapMixin).ToList();
    }

    public async Task<ProfileMixinSummary> UpsertMixinAsync(
        UpsertProfileMixinRequest request,
        long? id = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Mixin name is required.");
        }

        var slug = NormalizeSlug(request.Slug, request.Name);
        var rulesJson = JsonSerializer.Serialize(request.Rules, JsonOpts);
        var now = DateTimeOffset.UtcNow.ToString("O");

        var savedId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                if (id.HasValue)
                {
                    var affected = await conn.ExecuteAsync(
                        """
                        UPDATE profile_mixins
                        SET name = @Name, category = @Cat, rules_json = @RulesJson, updated_at = @Now
                        WHERE id = @Id;
                        """,
                        new { Id = id.Value, Name = request.Name.Trim(), Cat = request.Category.Trim(), RulesJson = rulesJson, Now = now },
                        transaction: tx);
                    if (affected == 0)
                    {
                        throw new KeyNotFoundException($"Trait mixin #{id.Value} not found.");
                    }
                    return id.Value;
                }

                return await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO profile_mixins (slug, name, category, rules_json, created_at, updated_at)
                    VALUES (@Slug, @Name, @Cat, @RulesJson, @Now, @Now);
                    SELECT last_insert_rowid();
                    """,
                    new { Slug = slug, Name = request.Name.Trim(), Cat = request.Category.Trim(), RulesJson = rulesJson, Now = now },
                    transaction: tx);
            },
            ct);

        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await readConn.QuerySingleAsync<MixinRow>("SELECT * FROM profile_mixins WHERE id = @Id;", new { Id = savedId });
        return MapMixin(row);
    }

    public async Task DeleteMixinAsync(long id, CancellationToken ct = default)
    {
        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var existing = await readConn.QuerySingleOrDefaultAsync<(long Id, string Slug, string Name)>(
            "SELECT id AS Id, slug AS Slug, name AS Name FROM profile_mixins WHERE id = @Id;",
            new { Id = id });
        if (existing == default)
        {
            throw new KeyNotFoundException($"Trait mixin #{id} not found.");
        }

        if (string.Equals(existing.Slug, "codec-efficiency-av1-hevc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(existing.Slug, "atmos-hdr-cinema", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"System preset trait mixin '{existing.Name}' cannot be deleted.");
        }

        var profiles = await ListProfilesAsync(ct);
        var inUse = profiles.Where(p => p.MixinIds.Contains(id)).ToList();
        if (inUse.Count > 0)
        {
            throw new InvalidOperationException(
                $"Trait mixin '{existing.Name}' is in active use by quality profile '{inUse[0].Name}' and cannot be deleted.");
        }

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                "DELETE FROM profile_mixins WHERE id = @Id;",
                new { Id = id },
                transaction: tx),
            ct);
    }

    public async Task<IReadOnlyList<QualityProfileSummary>> ListProfilesAsync(CancellationToken ct = default)
    {
        var policies = (await ListLanguagePoliciesAsync(ct)).ToDictionary(p => p.Id);
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<ProfileRow>("SELECT * FROM quality_profiles ORDER BY id ASC;");
        return rows.Select(r => MapProfile(r, r.LanguagePolicyId.HasValue ? policies.GetValueOrDefault(r.LanguagePolicyId.Value) : null)).ToList();
    }

    public async Task<QualityProfileSummary?> GetProfileByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await conn.QuerySingleOrDefaultAsync<ProfileRow>("SELECT * FROM quality_profiles WHERE id = @Id;", new { Id = id });
        if (row is null)
        {
            return null;
        }

        LanguagePolicySummary? langPolicy = null;
        if (row.LanguagePolicyId.HasValue)
        {
            var lpRow = await conn.QuerySingleOrDefaultAsync<LangPolicyRow>(
                "SELECT * FROM language_policies WHERE id = @Id;",
                new { Id = row.LanguagePolicyId.Value });
            if (lpRow is not null)
            {
                langPolicy = MapLanguagePolicy(lpRow);
            }
        }

        return MapProfile(row, langPolicy);
    }

    public Task<QualityProfileSummary> UpsertProfileAsync(
        UpsertQualityProfileRequest request,
        long? id = null,
        CancellationToken ct = default) =>
        UpsertProfileInternalAsync(request, isPreset: false, id, ct);

    public Task DeleteProfileAsync(long id, CancellationToken ct = default) =>
        dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    "UPDATE libraries SET default_profile_id = NULL WHERE default_profile_id = @Id;",
                    new { Id = id },
                    transaction: tx);
                await conn.ExecuteAsync(
                    "UPDATE media_units SET profile_override_id = NULL WHERE profile_override_id = @Id;",
                    new { Id = id },
                    transaction: tx);
                await conn.ExecuteAsync(
                    "DELETE FROM quality_profiles WHERE id = @Id;",
                    new { Id = id },
                    transaction: tx);
            },
            ct);

    public async Task<ReleaseSimulationScorecard> SimulateAsync(SimulateReleaseRequest request, CancellationToken ct = default)
    {
        var profile = await GetProfileByIdAsync(request.ProfileId, ct)
            ?? throw new KeyNotFoundException($"QualityProfile #{request.ProfileId} not found.");

        var parsed = parserRouter.Parse(request.ReleaseTitle);
        var origLang = string.IsNullOrWhiteSpace(request.OriginalLanguage) ? "en" : request.OriginalLanguage.Trim().ToLowerInvariant();

        // Hydrate optional MediaUnit context if UnitId was supplied
        string? existingQuality = request.ExistingSourceQuality ?? request.ExistingFileQuality;
        string? existingVideoCodec = request.ExistingVideoCodec;
        string? existingSourceVideoCodec = null;
        string? existingHdrFormat = null;
        string? existingAudioCodec = null;
        string? existingRelativePath = null;
        var existingAudio = (request.ExistingFileAudioLanguages ?? []).Select(l => l.ToLowerInvariant()).ToList();
        var existingSubs = (request.ExistingFileSubtitleLanguages ?? []).Select(l => l.ToLowerInvariant()).ToList();
        var unitRequiredAudio = new List<string>();
        var effectiveWorkTags = (request.WorkTags ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var effectiveMediaVertical = !string.IsNullOrWhiteSpace(request.MediaVertical)
            ? request.MediaVertical.Trim()
            : profile.MediaVertical;

        long? effectiveWorkId = request.WorkId;
        if (request.UnitId.HasValue)
        {
            await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
            var unitInfo = await conn.QuerySingleOrDefaultAsync<(long WorkId, string ReqAudioJson)>(
                "SELECT work_id AS WorkId, required_audio_langs_json AS ReqAudioJson FROM media_units WHERE id = @Id;",
                new { Id = request.UnitId.Value });

            if (unitInfo != default)
            {
                effectiveWorkId ??= unitInfo.WorkId;
                unitRequiredAudio.AddRange(JsonSerializer.Deserialize<string[]>(unitInfo.ReqAudioJson, JsonOpts) ?? []);
                var workMeta = await conn.QuerySingleOrDefaultAsync<(string? OriginalLanguage, string? WorkKind, string? TagsJson)>(
                    "SELECT original_language AS OriginalLanguage, work_kind AS WorkKind, tags_json AS TagsJson FROM media_works WHERE id = @Id;",
                    new { Id = unitInfo.WorkId });
                if (!string.IsNullOrWhiteSpace(workMeta.OriginalLanguage))
                {
                    origLang = workMeta.OriginalLanguage.Trim().ToLowerInvariant();
                }

                if (string.IsNullOrWhiteSpace(request.MediaVertical) && !string.IsNullOrWhiteSpace(workMeta.WorkKind))
                {
                    effectiveMediaVertical = workMeta.WorkKind.Trim();
                }

                if (!string.IsNullOrWhiteSpace(workMeta.TagsJson))
                {
                    foreach (var tg in JsonSerializer.Deserialize<string[]>(workMeta.TagsJson, JsonOpts) ?? [])
                    {
                        if (!string.IsNullOrWhiteSpace(tg))
                        {
                            effectiveWorkTags.Add(tg.Trim());
                        }
                    }
                }

                if (existingQuality is null || existingVideoCodec is null)
                {
                    var latestFile = await conn.QueryFirstOrDefaultAsync<(
                        string EffectiveQuality,
                        string? VideoCodec,
                        string? SourceVideoCodec,
                        string? HdrFormat,
                        string? AudioCodec,
                        string? RelativePath,
                        string AudioJson,
                        string SubJson)>(
                        """
                        SELECT COALESCE(source_quality_name, quality_name) AS EffectiveQuality,
                               video_codec AS VideoCodec,
                               source_video_codec AS SourceVideoCodec,
                               hdr_format AS HdrFormat,
                               audio_codec AS AudioCodec,
                               relative_path AS RelativePath,
                               detected_audio_langs_json AS AudioJson,
                               detected_sub_langs_json AS SubJson
                        FROM media_files
                        WHERE unit_id = @UnitId
                        ORDER BY id DESC
                        LIMIT 1;
                        """,
                        new { UnitId = request.UnitId.Value });

                    if (latestFile != default)
                    {
                        existingQuality ??= latestFile.EffectiveQuality;
                        existingVideoCodec ??= latestFile.VideoCodec;
                        existingSourceVideoCodec = latestFile.SourceVideoCodec;
                        existingHdrFormat = latestFile.HdrFormat;
                        existingAudioCodec = latestFile.AudioCodec;
                        existingRelativePath = latestFile.RelativePath;
                        if (request.ExistingFileAudioLanguages is null)
                        {
                            existingAudio = (JsonSerializer.Deserialize<string[]>(latestFile.AudioJson, JsonOpts) ?? []).ToList();
                        }

                        if (request.ExistingFileSubtitleLanguages is null)
                        {
                            existingSubs = (JsonSerializer.Deserialize<string[]>(latestFile.SubJson, JsonOpts) ?? []).ToList();
                        }
                    }
                }
            }
        }

        if (effectiveWorkId.HasValue)
        {
            await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
            var customLangPolicyId = await conn.ExecuteScalarAsync<long?>(
                "SELECT language_policy_id FROM media_works WHERE id = @Id;",
                new { Id = effectiveWorkId.Value });
            if (customLangPolicyId.HasValue)
            {
                var policies = await ListLanguagePoliciesAsync(ct);
                var customPolicy = policies.FirstOrDefault(p => p.Id == customLangPolicyId.Value);
                if (customPolicy is not null)
                {
                    profile = profile with { LanguagePolicy = customPolicy };
                }
            }
        }

        var hardGateFailures = new List<string>();
        var matchedTraits = new List<string>();

        // Stored names may predate canonical naming (e.g. 'WEB-DL-1080p', 'WEBDL-480p', 'MKV')
        if (existingQuality is not null)
        {
            existingQuality = QualityNames.Canonicalize(existingQuality);
        }

        // 1. Quality Axis
        var canonicalQuality = parsed.ToQualityName();
        var allowedTier = profile.AllowedQualities.FirstOrDefault(q =>
            string.Equals(q.Name, canonicalQuality, StringComparison.OrdinalIgnoreCase));

        var qualityScore = allowedTier?.Score ?? 0;
        var qualityRank = allowedTier?.Rank ?? -1;
        var cutoffTier = profile.AllowedQualities.FirstOrDefault(q =>
            string.Equals(q.Name, profile.QualityCutoff, StringComparison.OrdinalIgnoreCase));
        var candidateSatisfiesQualityCutoff = allowedTier is not null && cutoffTier is not null && allowedTier.Rank >= cutoffTier.Rank;

        // Check quality floor BEFORE generic allowed-qualities rejection so Cam/720p dub downgrade traps report BlockedByQualityFloor explicitly!
        var floorBlocked = false;
        string? floorReason = null;
        if (!string.IsNullOrWhiteSpace(existingQuality) && profile.LanguagePolicy is { } lp)
        {
            var existingAllowed = profile.AllowedQualities.FirstOrDefault(q =>
                string.Equals(q.Name, existingQuality, StringComparison.OrdinalIgnoreCase));
            var existingGlobalRank = GlobalQualityHierarchy.GetValueOrDefault(existingQuality, existingAllowed?.Rank * 10 ?? 50);
            var candGlobalRank = GlobalQualityHierarchy.GetValueOrDefault(canonicalQuality, allowedTier?.Rank * 10 ?? 0);

            if (!string.IsNullOrWhiteSpace(lp.MinimumAbsoluteQuality))
            {
                var minFloorGlobalRank = GlobalQualityHierarchy.GetValueOrDefault(lp.MinimumAbsoluteQuality, 0);
                if (candGlobalRank < minFloorGlobalRank)
                {
                    floorBlocked = true;
                    floorReason = $"Candidate quality '{canonicalQuality}' violates minimumAbsoluteQuality floor '{lp.MinimumAbsoluteQuality}'.";
                }
            }

            if (!floorBlocked && existingAllowed is not null && allowedTier is not null)
            {
                var rankDrop = existingAllowed.Rank - allowedTier.Rank;
                if (lp.TradeoffMode == "never_downgrade_quality" && rankDrop > 0)
                {
                    floorBlocked = true;
                    floorReason = $"Candidate quality '{canonicalQuality}' steps down from existing '{existingQuality}' under never_downgrade_quality policy.";
                }
                else if (lp.TradeoffMode == "bounded_stepdown" && rankDrop > lp.MaxQualityTierDrop)
                {
                    floorBlocked = true;
                    floorReason = $"Quality drop ({rankDrop} tiers) exceeds maxQualityTierDrop={lp.MaxQualityTierDrop}.";
                }
            }
        }

        if (allowedTier is null && !floorBlocked)
        {
            hardGateFailures.Add($"Quality '{canonicalQuality}' is not permitted in profile '{profile.Name}'.");
        }

        // 2. Language Axis (resolving dynamic "original" token + PlexUser unit language promotions)
        var candAudio = (request.ExplicitAudioLanguages ?? parsed.DetectedLanguages)
            .Select(l => l.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (candAudio.Count == 0)
        {
            candAudio.Add(origLang);
        }

        if (request.ExplicitAudioLanguages is null && parsed.IsDualAudio && !string.IsNullOrWhiteSpace(origLang))
        {
            if (candAudio.Contains("de") && candAudio.Contains("en") &&
                !ReleaseParserRouter.HasExplicitEnglishToken(parsed.RawTitle) &&
                !string.Equals(origLang, "en", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(origLang, "de", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(origLang, "und", StringComparison.OrdinalIgnoreCase))
            {
                candAudio.Remove("en");
                candAudio.Add(origLang);
            }
            else
            {
                candAudio.Add(origLang);
            }
        }

        var candSubs = (request.ExplicitSubtitleLanguages ?? (parsed.IsMultiSubs ? ["en", "de"] : Array.Empty<string>()))
            .Select(l => l.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int? matchedLangTier = null;
        string? matchedLangLabel = null;
        var langScore = 0.0;
        var candidateSatisfiesLanguageCutoff = true;

        int? existingLangTier = null;
        var existingLangCutoffMet = string.IsNullOrWhiteSpace(existingQuality) ? false : true;

        if (profile.LanguagePolicy is { } policy && policy.Tiers.Count > 0)
        {
            var candMatch = EvaluateLanguagePolicyTiers(policy, origLang, candAudio, candSubs, unitRequiredAudio);
            if (candMatch is null)
            {
                hardGateFailures.Add($"Release audio [{string.Join(',', candAudio)}] / subs [{string.Join(',', candSubs)}] did not satisfy any tier in '{policy.Name}'.");
                candidateSatisfiesLanguageCutoff = false;
            }
            else
            {
                matchedLangTier = candMatch.Tier;
                matchedLangLabel = candMatch.Label;
                langScore = candMatch.Score;
                candidateSatisfiesLanguageCutoff = candMatch.SatisfiesLanguageCutoff && candMatch.Tier <= policy.LanguageCutoffTier;
                // If unit has promoted PlexUser required audio langs, ensure all promoted langs are present before declaring LanguageCutoffMet!
                if (unitRequiredAudio.Any(req => !candAudio.Contains(ResolveLangToken(req, origLang))))
                {
                    candidateSatisfiesLanguageCutoff = false;
                }
            }

            if (!string.IsNullOrWhiteSpace(existingQuality))
            {
                var exAudioSet = existingAudio.Count > 0
                    ? existingAudio.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { origLang };
                var exSubSet = existingSubs.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var exMatch = EvaluateLanguagePolicyTiers(policy, origLang, exAudioSet, exSubSet, unitRequiredAudio);
                existingLangTier = exMatch?.Tier ?? int.MaxValue;
                existingLangCutoffMet = exMatch is not null &&
                                        exMatch.SatisfiesLanguageCutoff &&
                                        exMatch.Tier <= policy.LanguageCutoffTier &&
                                        unitRequiredAudio.All(req => exAudioSet.Contains(ResolveLangToken(req, origLang)));
            }
        }

        // 3. Traits & Custom Formats Axis
        var traitScore = 0.0;
        var derivedExistingCurrentCodecScore = 0.0;
        var derivedExistingSourceCodecScore = 0.0;
        if (profile.MixinIds.Count > 0)
        {
            var allMixins = (await ListMixinsAsync(ct)).Where(m => profile.MixinIds.Contains(m.Id)).ToList();
            foreach (var mixin in allMixins)
            {
                foreach (var rule in mixin.Rules)
                {
                    var normField = rule.Field.Trim().ToLowerInvariant();
                    var fieldVal = normField switch
                    {
                        "videocodec" or "video_codec" => parsed.VideoCodec ?? "",
                        "hdrformat" or "hdr_format" => parsed.HdrFormat ?? "",
                        "audiocodec" or "audio_codec" => parsed.AudioCodec ?? "",
                        "releasegroup" or "release_group" => parsed.ReleaseGroup ?? "",
                        "movieedition" or "movie_edition" => parsed.MovieEdition ?? "",
                        _ => parsed.RawTitle,
                    };

                    if (!string.IsNullOrEmpty(fieldVal) && Regex.IsMatch(fieldVal, rule.Pattern, RegexOptions.IgnoreCase))
                    {
                        traitScore += rule.Score;
                        matchedTraits.Add($"{mixin.Name}: {rule.Label} ({rule.Score:+#;-#;0})");
                        if (rule.RequiredReject)
                        {
                            hardGateFailures.Add($"Rejected by required mixin rule '{rule.Label}'.");
                        }
                    }

                    if (!request.ExistingTraitScore.HasValue)
                    {
                        var existingCurVal = normField switch
                        {
                            "videocodec" or "video_codec" => existingVideoCodec ?? "",
                            "hdrformat" or "hdr_format" => existingHdrFormat ?? "",
                            "audiocodec" or "audio_codec" => existingAudioCodec ?? "",
                            _ => existingRelativePath ?? "",
                        };

                        if (!string.IsNullOrEmpty(existingCurVal) &&
                            Regex.IsMatch(existingCurVal, rule.Pattern, RegexOptions.IgnoreCase))
                        {
                            derivedExistingCurrentCodecScore += rule.Score;
                        }

                        var existingSrcVal = normField switch
                        {
                            "videocodec" or "video_codec" => existingSourceVideoCodec ?? existingVideoCodec ?? "",
                            "hdrformat" or "hdr_format" => existingHdrFormat ?? "",
                            "audiocodec" or "audio_codec" => existingAudioCodec ?? "",
                            _ => existingRelativePath ?? "",
                        };

                        if (!string.IsNullOrEmpty(existingSrcVal) &&
                            Regex.IsMatch(existingSrcVal, rule.Pattern, RegexOptions.IgnoreCase))
                        {
                            derivedExistingSourceCodecScore += rule.Score;
                        }
                    }
                }
            }
        }
        var derivedExistingTraitScore = Math.Max(derivedExistingCurrentCodecScore, derivedExistingSourceCodecScore);

        var customFormats = await trashImporter.ListCustomFormatsAsync(ct);
        foreach (var cf in customFormats)
        {
            var cfScore = profile.CustomFormatScores.TryGetValue(cf.Id, out var overrideScore)
                ? overrideScore
                : cf.DefaultScore;

            if (cfScore == 0 && cf.Specifications.All(s => !s.Required))
            {
                continue;
            }

            if (MatchesCustomFormat(cf, parsed, candAudio, origLang, request.SizeBytes, request.IndexerFlags))
            {
                traitScore += cfScore;
                matchedTraits.Add($"CF '{cf.Name}' ({cfScore:+#;-#;0})");
                if (cfScore <= -10000)
                {
                    hardGateFailures.Add($"Rejected by Custom Format '{cf.Name}' ({cfScore}).");
                }
            }
        }

        // 3b. Release Profiles / Restrictions (Must Contain, Must Not Contain, Preferred Terms)
        var releaseProfiles = await ListReleaseProfilesAsync(ct);
        foreach (var rp in releaseProfiles.Where(r => r.IsEnabled))
        {
            if (!string.IsNullOrWhiteSpace(rp.MediaVertical) &&
                !string.Equals(rp.MediaVertical, "all", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(effectiveMediaVertical) &&
                !string.Equals(effectiveMediaVertical, "all", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(rp.MediaVertical, effectiveMediaVertical, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (rp.IndexerId.HasValue && (!request.IndexerId.HasValue || request.IndexerId.Value != rp.IndexerId.Value))
            {
                continue;
            }

            if (rp.Tags.Count > 0 && !rp.Tags.Any(t => effectiveWorkTags.Contains(t)))
            {
                continue;
            }

            if (rp.MustContain.Count > 0 &&
                !rp.MustContain.Any(term => MatchesReleaseRestrictionTerm(parsed.RawTitle, term)))
            {
                hardGateFailures.Add(
                    $"Release Profile '{rp.Name}': release did not match any required MustContain term [{string.Join(", ", rp.MustContain)}].");
            }

            foreach (var forbidden in rp.MustNotContain)
            {
                if (MatchesReleaseRestrictionTerm(parsed.RawTitle, forbidden))
                {
                    hardGateFailures.Add(
                        $"Release Profile '{rp.Name}': matched forbidden MustNotContain term '{forbidden}'.");
                }
            }

            foreach (var pref in rp.PreferredTerms)
            {
                if (!string.IsNullOrWhiteSpace(pref.Term) && MatchesReleaseRestrictionTerm(parsed.RawTitle, pref.Term))
                {
                    traitScore += pref.Score;
                    matchedTraits.Add($"ReleaseProfile '{rp.Name}': '{pref.Term}' ({pref.Score:+#;-#;0})");
                }
            }
        }

        if (traitScore < profile.MinTraitScore)
        {
            hardGateFailures.Add($"Trait score {traitScore} is below profile minimum {profile.MinTraitScore}.");
        }

        // 4. Bitrate / Size Efficiency Bell-Curve Axis + Quality Size Limits (MB/min)
        var efficiencyScore = 0.0;
        if (request.SizeBytes.HasValue && request.RuntimeMinutes is > 0)
        {
            if (allowedTier is not null)
            {
                var mbps = (request.SizeBytes.Value * 8.0 / 1_000_000.0) / (request.RuntimeMinutes.Value * 60.0);
                if (allowedTier.MinMbps.HasValue && mbps < allowedTier.MinMbps.Value)
                {
                    hardGateFailures.Add($"Bitrate {mbps:F1} Mbps is below minimum {allowedTier.MinMbps.Value:F1} Mbps for {canonicalQuality}.");
                }
                else if (allowedTier.MaxMbps.HasValue && mbps > allowedTier.MaxMbps.Value)
                {
                    hardGateFailures.Add($"Bitrate {mbps:F1} Mbps exceeds maximum {allowedTier.MaxMbps.Value:F1} Mbps for {canonicalQuality}.");
                }
                else if (allowedTier.MinMbps.HasValue && allowedTier.MaxMbps.HasValue)
                {
                    var midpoint = (allowedTier.MinMbps.Value + allowedTier.MaxMbps.Value) / 2.0;
                    var halfSpan = Math.Max(1.0, (allowedTier.MaxMbps.Value - allowedTier.MinMbps.Value) / 2.0);
                    var normalizedDist = Math.Abs(mbps - midpoint) / halfSpan;
                    efficiencyScore = Math.Round(Math.Max(0.0, 100.0 * (1.0 - (normalizedDist * normalizedDist))), 1);
                }
            }

            var sizeLimits = await ListQualitySizeLimitsAsync(ct);
            var matchedSizeLimit = sizeLimits.FirstOrDefault(l =>
                string.Equals(l.QualityName, canonicalQuality, StringComparison.OrdinalIgnoreCase));
            if (matchedSizeLimit is not null && (matchedSizeLimit.MinMbPerMin > 0 || matchedSizeLimit.MaxMbPerMin > 0 || matchedSizeLimit.PreferredMbPerMin > 0))
            {
                var mbPerMin = (request.SizeBytes.Value / 1_000_000.0) / request.RuntimeMinutes.Value;
                if (matchedSizeLimit.MinMbPerMin > 0 && mbPerMin < matchedSizeLimit.MinMbPerMin)
                {
                    hardGateFailures.Add(
                        $"Size rate {mbPerMin:F1} MB/min is below Quality Size Limit minimum {matchedSizeLimit.MinMbPerMin:F1} MB/min for {canonicalQuality}.");
                }
                else if (matchedSizeLimit.MaxMbPerMin > 0 && mbPerMin > matchedSizeLimit.MaxMbPerMin)
                {
                    hardGateFailures.Add(
                        $"Size rate {mbPerMin:F1} MB/min exceeds Quality Size Limit maximum {matchedSizeLimit.MaxMbPerMin:F1} MB/min for {canonicalQuality}.");
                }
                else if (matchedSizeLimit.PreferredMbPerMin > 0 && efficiencyScore == 0)
                {
                    var span = matchedSizeLimit.MaxMbPerMin > matchedSizeLimit.PreferredMbPerMin
                        ? Math.Max(1.0, matchedSizeLimit.MaxMbPerMin - matchedSizeLimit.PreferredMbPerMin)
                        : Math.Max(10.0, matchedSizeLimit.PreferredMbPerMin * 0.5);
                    var normalizedDist = Math.Min(1.0, Math.Abs(mbPerMin - matchedSizeLimit.PreferredMbPerMin) / span);
                    efficiencyScore = Math.Round(Math.Max(0.0, 100.0 * (1.0 - (normalizedDist * normalizedDist))), 1);
                }
            }
        }

        var totalWeighted = Math.Round(
            (profile.WeightQuality * qualityScore) +
            (profile.WeightLanguage * langScore) +
            (profile.WeightTraits * traitScore) +
            (profile.WeightEfficiency * efficiencyScore),
            2);

        // 5. Dual-Cutoff Upgrade Decision State Machine
        var existingQualityTier = string.IsNullOrWhiteSpace(existingQuality)
            ? null
            : profile.AllowedQualities.FirstOrDefault(q => string.Equals(q.Name, existingQuality, StringComparison.OrdinalIgnoreCase));
        var existingQualityCutoffMet = existingQualityTier is not null && cutoffTier is not null && existingQualityTier.Rank >= cutoffTier.Rank;

        if (floorBlocked)
        {
            return new ReleaseSimulationScorecard(
                Approved: false,
                DecisionKind: ProfileDecisionKinds.BlockedByQualityFloor,
                Explanation: floorReason!,
                CanonicalQualityName: canonicalQuality,
                QualityRank: qualityRank,
                MatchedLanguageTier: matchedLangTier,
                MatchedLanguageTierLabel: matchedLangLabel,
                candidateSatisfiesQualityCutoff,
                candidateSatisfiesLanguageCutoff,
                existingQualityCutoffMet,
                existingLangCutoffMet,
                qualityScore,
                langScore,
                traitScore,
                efficiencyScore,
                totalWeighted,
                matchedTraits,
                [floorReason!]);
        }

        if (hardGateFailures.Count > 0)
        {
            return new ReleaseSimulationScorecard(
                Approved: false,
                DecisionKind: ProfileDecisionKinds.RejectedByHardGate,
                Explanation: string.Join(" ", hardGateFailures),
                CanonicalQualityName: canonicalQuality,
                QualityRank: qualityRank,
                MatchedLanguageTier: matchedLangTier,
                MatchedLanguageTierLabel: matchedLangLabel,
                candidateSatisfiesQualityCutoff,
                candidateSatisfiesLanguageCutoff,
                existingQualityCutoffMet,
                existingLangCutoffMet,
                qualityScore,
                langScore,
                traitScore,
                efficiencyScore,
                totalWeighted,
                matchedTraits,
                hardGateFailures);
        }

        if (string.IsNullOrWhiteSpace(existingQuality))
        {
            return new ReleaseSimulationScorecard(
                Approved: true,
                DecisionKind: ProfileDecisionKinds.NewGrab,
                Explanation: $"Unit has no file on disk; '{canonicalQuality}' ({matchedLangLabel ?? "Default Language"}) passes all gates.",
                CanonicalQualityName: canonicalQuality,
                QualityRank: qualityRank,
                MatchedLanguageTier: matchedLangTier,
                MatchedLanguageTierLabel: matchedLangLabel,
                candidateSatisfiesQualityCutoff,
                candidateSatisfiesLanguageCutoff,
                false,
                false,
                qualityScore,
                langScore,
                traitScore,
                efficiencyScore,
                totalWeighted,
                matchedTraits,
                []);
        }

        var isBetterLanguage = (profile.LanguagePolicy?.EnableLanguageUpgrades ?? true) &&
                               !existingLangCutoffMet &&
                               matchedLangTier.HasValue &&
                               existingLangTier.HasValue &&
                               matchedLangTier.Value < existingLangTier.Value;

        var existingRank = existingQualityTier?.Rank ?? 0;
        var isBetterQuality = !existingQualityCutoffMet &&
                              qualityRank > existingRank &&
                              (!matchedLangTier.HasValue || !existingLangTier.HasValue || matchedLangTier.Value <= existingLangTier.Value);

        if (isBetterLanguage)
        {
            return new ReleaseSimulationScorecard(
                Approved: true,
                DecisionKind: ProfileDecisionKinds.LanguageUpgrade,
                Explanation: $"Upgrades language from Tier {existingLangTier} to Tier {matchedLangTier} ({matchedLangLabel}) while respecting quality guardrails ({existingQuality} -> {canonicalQuality}).",
                CanonicalQualityName: canonicalQuality,
                QualityRank: qualityRank,
                MatchedLanguageTier: matchedLangTier,
                MatchedLanguageTierLabel: matchedLangLabel,
                candidateSatisfiesQualityCutoff,
                candidateSatisfiesLanguageCutoff,
                existingQualityCutoffMet,
                existingLangCutoffMet,
                qualityScore,
                langScore,
                traitScore,
                efficiencyScore,
                totalWeighted,
                matchedTraits,
                []);
        }

        if (isBetterQuality)
        {
            return new ReleaseSimulationScorecard(
                Approved: true,
                DecisionKind: ProfileDecisionKinds.QualityUpgrade,
                Explanation: $"Upgrades quality from '{existingQuality}' to '{canonicalQuality}' without regressing language tier.",
                CanonicalQualityName: canonicalQuality,
                QualityRank: qualityRank,
                MatchedLanguageTier: matchedLangTier,
                MatchedLanguageTierLabel: matchedLangLabel,
                candidateSatisfiesQualityCutoff,
                candidateSatisfiesLanguageCutoff,
                existingQualityCutoffMet,
                existingLangCutoffMet,
                qualityScore,
                langScore,
                traitScore,
                efficiencyScore,
                totalWeighted,
                matchedTraits,
                []);
        }

        var effectiveExistingTraitScore = request.ExistingTraitScore ?? derivedExistingTraitScore;
        if (qualityRank >= existingRank &&
            (!matchedLangTier.HasValue || !existingLangTier.HasValue || matchedLangTier.Value <= existingLangTier.Value) &&
            traitScore > effectiveExistingTraitScore)
        {
            return new ReleaseSimulationScorecard(
                Approved: true,
                DecisionKind: ProfileDecisionKinds.TraitUpgrade,
                Explanation: $"Upgrades trait/custom-format score ({effectiveExistingTraitScore:F0} -> {traitScore:F0}) at equal or higher quality/language.",
                CanonicalQualityName: canonicalQuality,
                QualityRank: qualityRank,
                MatchedLanguageTier: matchedLangTier,
                MatchedLanguageTierLabel: matchedLangLabel,
                candidateSatisfiesQualityCutoff,
                candidateSatisfiesLanguageCutoff,
                existingQualityCutoffMet,
                existingLangCutoffMet,
                qualityScore,
                langScore,
                traitScore,
                efficiencyScore,
                totalWeighted,
                matchedTraits,
                []);
        }

        return new ReleaseSimulationScorecard(
            Approved: false,
            DecisionKind: ProfileDecisionKinds.CutoffsAlreadyMet,
            Explanation: "Existing file already meets or exceeds candidate across Quality, Language, and Trait axes.",
            CanonicalQualityName: canonicalQuality,
            QualityRank: qualityRank,
            MatchedLanguageTier: matchedLangTier,
            MatchedLanguageTierLabel: matchedLangLabel,
            candidateSatisfiesQualityCutoff,
            candidateSatisfiesLanguageCutoff,
            existingQualityCutoffMet,
            existingLangCutoffMet,
            qualityScore,
            langScore,
            traitScore,
            efficiencyScore,
            totalWeighted,
            matchedTraits,
            []);
    }

    public async Task<MediaWorkDetailTree> PromotePlexUserLanguageAsync(
        long workId,
        PlexUserSummary user,
        CancellationToken ct = default)
    {
        var tree = await catalogService.GetWorkTreeAsync(workId, actor: null, ct)
            ?? throw new KeyNotFoundException($"MediaWork #{workId} not found.");

        var prefAudio = user.PreferredAudioLang?.Trim().ToLowerInvariant();
        var prefSub = user.PreferredSubLang?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(prefAudio) && string.IsNullOrWhiteSpace(prefSub))
        {
            return tree;
        }

        foreach (var unit in tree.Containers.SelectMany(c => c.Units))
        {
            var nextAudio = unit.RequiredAudioLangs.ToList();
            if (!string.IsNullOrWhiteSpace(prefAudio) && !nextAudio.Contains(prefAudio, StringComparer.OrdinalIgnoreCase))
            {
                nextAudio.Add(prefAudio);
            }

            var nextSubs = unit.RequiredSubLangs.ToList();
            if (!string.IsNullOrWhiteSpace(prefSub) && !nextSubs.Contains(prefSub, StringComparer.OrdinalIgnoreCase))
            {
                nextSubs.Add(prefSub);
            }

            var verifiedAudioOnDisk = unit.Files
                .Where(f => f.VerifiedStatus is "verified" or "VerifiedPass" or "VerifiedPassAutoTagged")
                .SelectMany(f => f.DetectedAudioLangs)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var langMet = nextAudio.Count == 0 || nextAudio.All(req => verifiedAudioOnDisk.Contains(req));

            await catalogService.UpdateUnitAsync(
                unit.Id,
                new UpdateMediaUnitRequest(
                    RequiredAudioLangs: nextAudio,
                    RequiredSubLangs: nextSubs,
                    LanguageCutoffMet: langMet),
                ct);
        }

        return (await catalogService.GetWorkTreeAsync(workId, actor: null, ct))!;
    }

    private static bool TerritorySafeBool(bool v) => v;

    private static LanguagePolicyTier? EvaluateLanguagePolicyTiers(
        LanguagePolicySummary policy,
        string origLang,
        HashSet<string> audioLangs,
        HashSet<string> subLangs,
        IReadOnlyList<string> unitRequiredAudio)
    {
        // Dynamic promotion: if a PlexUser promoted an extra language (e.g. "de") onto a unit whose original is "en",
        // reward dual-audio containing all unitRequiredAudio + origLang first if Tier 1 matches!
        foreach (var tier in policy.Tiers.OrderBy(t => t.Tier))
        {
            var allOk = tier.AudioContainsAll.Count == 0 ||
                        tier.AudioContainsAll.All(tok => audioLangs.Contains(ResolveLangToken(tok, origLang)));
            var anyOk = tier.AudioContainsAny.Count == 0 ||
                        tier.AudioContainsAny.Any(tok => audioLangs.Contains(ResolveLangToken(tok, origLang)));
            var subsOk = tier.SubtitlesContainsAny.Count == 0 ||
                         tier.SubtitlesContainsAny.Any(tok => subLangs.Contains(ResolveLangToken(tok, origLang)));

            if (allOk && anyOk && subsOk)
            {
                return tier;
            }
        }

        // Also allow promoted unit languages directly if present
        if (unitRequiredAudio.Count > 0 && unitRequiredAudio.All(audioLangs.Contains))
        {
            return new LanguagePolicyTier(1, "promoted-user-lang", "PlexUser Promoted Language Match", unitRequiredAudio, [], [], 900, true);
        }

        return null;
    }

    private static string ResolveLangToken(string token, string origLang) =>
        string.Equals(token, "original", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(token, "orig", StringComparison.OrdinalIgnoreCase)
            ? origLang
            : token.ToLowerInvariant();

    public LanguagePolicyOnDiskEvaluation EvaluateOnDiskLanguagesAgainstPolicy(
        LanguagePolicySummary? policy,
        string? originalLanguage,
        IReadOnlyCollection<string> detectedAudioLangs,
        IReadOnlyCollection<string> detectedSubLangs)
    {
        if (policy is null || policy.Tiers.Count == 0)
        {
            return new LanguagePolicyOnDiskEvaluation(true, true, null, null, null);
        }

        var orig = string.IsNullOrWhiteSpace(originalLanguage) ? "en" : NormalizeLanguageCode(originalLanguage);
        var normAudio = (detectedAudioLangs ?? [])
            .Select(NormalizeLanguageCode)
            .Where(l => l != "und")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normSubs = (detectedSubLangs ?? [])
            .Select(NormalizeLanguageCode)
            .Where(l => l != "und")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var tier in policy.Tiers.OrderBy(t => t.Tier))
        {
            var allOk = tier.AudioContainsAll.Count == 0 ||
                        tier.AudioContainsAll.All(tok => normAudio.Contains(ResolveLangToken(tok, orig)));
            var anyOk = tier.AudioContainsAny.Count == 0 ||
                        tier.AudioContainsAny.Any(tok => normAudio.Contains(ResolveLangToken(tok, orig)));
            var subsOk = tier.SubtitlesContainsAny.Count == 0 ||
                         tier.SubtitlesContainsAny.Any(tok => normSubs.Contains(ResolveLangToken(tok, orig)));

            if (allOk && anyOk && subsOk)
            {
                var satisfiesCutoff = tier.SatisfiesLanguageCutoff && tier.Tier <= policy.LanguageCutoffTier;
                return new LanguagePolicyOnDiskEvaluation(
                    PolicyAllowed: true,
                    SatisfiesCutoff: satisfiesCutoff,
                    MatchedTier: tier.Tier,
                    MatchedTierLabel: tier.Label,
                    FailureReason: null);
            }
        }

        var reason = $"File tracks (audio: [{string.Join(", ", normAudio)}], subs: [{string.Join(", ", normSubs)}]) do not satisfy wanted language policy '{policy.Name}'.";
        return new LanguagePolicyOnDiskEvaluation(
            PolicyAllowed: false,
            SatisfiesCutoff: false,
            MatchedTier: null,
            MatchedTierLabel: null,
            FailureReason: reason);
    }

    private static string NormalizeLanguageCode(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return "und";
        var l = lang.Trim().ToLowerInvariant();
        return l switch
        {
            "ger" or "deu" or "german" or "deutsch" or "de-de" => "de",
            "eng" or "english" or "en-us" or "en-gb" => "en",
            "jpn" or "jap" or "japanese" => "ja",
            "fra" or "fre" or "french" => "fr",
            "spa" or "esp" or "spanish" or "castellano" => "es",
            "ita" or "italian" => "it",
            "kor" or "korean" => "ko",
            "zho" or "chi" or "chinese" or "cmn" => "zh",
            "rus" or "russian" => "ru",
            "por" or "portuguese" => "pt",
            "nld" or "dut" or "dutch" => "nl",
            "pol" or "polish" => "pl",
            _ => l.Length > 2 && l.Contains('-') ? l.Split('-')[0] : l
        };
    }

    private static bool MatchesCustomFormat(
        CustomFormatSummary cf,
        ParsedReleaseInfo parsed,
        HashSet<string> candAudio,
        string origLang,
        long? sizeBytes,
        IReadOnlyList<string>? indexerFlags)
    {
        if (cf.Specifications.Count == 0)
        {
            return false;
        }

        var requiredSpecs = cf.Specifications.Where(s => s.Required).ToList();
        var optionalSpecs = cf.Specifications.Where(s => !s.Required).ToList();

        bool EvalSpec(CustomFormatSpecItem s)
        {
            var pat = s.Pattern?.Trim() ?? "";
            var hit = s.Implementation switch
            {
                var i when i.Contains("ReleaseGroup", StringComparison.OrdinalIgnoreCase) =>
                    !string.IsNullOrEmpty(parsed.ReleaseGroup) && SafeRegexOrContains(parsed.ReleaseGroup, pat),
                var i when i.Contains("Resolution", StringComparison.OrdinalIgnoreCase) =>
                    MatchesResolutionSpec(parsed.Resolution, pat),
                var i when i.Contains("Source", StringComparison.OrdinalIgnoreCase) =>
                    MatchesSourceSpec(parsed.Source, pat),
                var i when i.Contains("Language", StringComparison.OrdinalIgnoreCase) =>
                    MatchesLanguageSpec(candAudio, origLang, pat),
                var i when i.Contains("IndexerFlag", StringComparison.OrdinalIgnoreCase) =>
                    MatchesIndexerFlagSpec(indexerFlags, parsed.RawTitle, pat),
                var i when i.Contains("Size", StringComparison.OrdinalIgnoreCase) =>
                    MatchesSizeSpec(sizeBytes, pat),
                _ => !string.IsNullOrEmpty(pat) && SafeRegexOrContains(parsed.RawTitle, pat),
            };
            return s.Negate ? !hit : hit;
        }

        if (requiredSpecs.Any(s => !EvalSpec(s)))
        {
            return false;
        }

        return optionalSpecs.Count == 0 || optionalSpecs.Any(EvalSpec);
    }

    private static bool MatchesResolutionSpec(string? resolution, string pattern)
    {
        if (string.IsNullOrWhiteSpace(resolution) || string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var normPat = pattern.Trim().TrimEnd('p', 'P');
        return resolution.Contains(normPat, StringComparison.OrdinalIgnoreCase) ||
               SafeRegexOrContains(resolution, pattern);
    }

    private static bool MatchesSourceSpec(string? parsedSource, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var effectiveSource = parsedSource ?? "WEB-DL";
        var mappedPattern = pattern.Trim() switch
        {
            "6" or "7" => "WEB-DL|WEBDL",
            "8" => "WEBRip",
            "9" => "BluRay",
            "10" => "Remux",
            "4" => "HDTV",
            _ => pattern.Trim(),
        };

        var compactSource = effectiveSource.Replace("-", "", StringComparison.Ordinal);
        return SafeRegexOrContains(effectiveSource, mappedPattern) ||
               SafeRegexOrContains(compactSource, mappedPattern.Replace("-", "", StringComparison.Ordinal));
    }

    private static bool MatchesLanguageSpec(HashSet<string> candAudio, string origLang, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || candAudio.Count == 0)
        {
            return false;
        }

        var resolved = ResolveLangToken(pattern.Trim(), origLang);
        if (candAudio.Contains(resolved))
        {
            return true;
        }

        return candAudio.Any(a => SafeRegexOrContains(a, pattern));
    }

    private static bool MatchesIndexerFlagSpec(IReadOnlyList<string>? indexerFlags, string rawTitle, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        if (indexerFlags is { Count: > 0 } &&
            indexerFlags.Any(f => !string.IsNullOrWhiteSpace(f) && SafeRegexOrContains(f, pattern)))
        {
            return true;
        }

        return SafeRegexOrContains(rawTitle, pattern);
    }

    private static bool MatchesSizeSpec(long? sizeBytes, string pattern)
    {
        if (!sizeBytes.HasValue || sizeBytes.Value <= 0 || string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var gib = sizeBytes.Value / (1024.0 * 1024.0 * 1024.0);
        var norm = pattern.Replace("GB", "", StringComparison.OrdinalIgnoreCase)
            .Replace("GiB", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        string[]? rangeParts = norm.Contains("..", StringComparison.Ordinal)
            ? norm.Split("..", 2, StringSplitOptions.TrimEntries)
            : norm.Contains('-', StringComparison.Ordinal)
                ? norm.Split('-', 2, StringSplitOptions.TrimEntries)
                : null;

        if (rangeParts is { Length: 2 })
        {
            var hasMin = double.TryParse(rangeParts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minGb);
            var hasMax = double.TryParse(rangeParts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var maxGb);
            if (hasMin && gib < minGb)
            {
                return false;
            }

            if (hasMax && maxGb > 0 && gib > maxGb)
            {
                return false;
            }

            return hasMin || hasMax;
        }

        if (norm.StartsWith(">=", StringComparison.Ordinal) &&
            double.TryParse(norm[2..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gte))
        {
            return gib >= gte;
        }

        if (norm.StartsWith("<=", StringComparison.Ordinal) &&
            double.TryParse(norm[2..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lte))
        {
            return gib <= lte;
        }

        if (double.TryParse(norm, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var maxSingle))
        {
            return gib <= maxSingle;
        }

        return false;
    }

    private static bool MatchesReleaseRestrictionTerm(string rawTitle, string term)
    {
        if (string.IsNullOrWhiteSpace(rawTitle) || string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        var trimmed = term.Trim();
        if (trimmed.Length >= 2 && trimmed.StartsWith('/') && trimmed.EndsWith('/'))
        {
            return SafeRegexOrContains(rawTitle, trimmed[1..^1]);
        }

        return SafeRegexOrContains(rawTitle, trimmed);
    }

    private static bool SafeRegexOrContains(string input, string pattern)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException)
        {
            return input.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }
    }

}
