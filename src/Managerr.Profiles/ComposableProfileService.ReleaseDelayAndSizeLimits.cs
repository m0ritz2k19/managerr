using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Managerr.Catalog;
using Managerr.Common.Media;
using Managerr.Datastore;
using Managerr.Downloaders.Parser;
using Managerr.Security.Users;

namespace Managerr.Profiles;

public sealed partial class ComposableProfileService
{
    private async Task<LanguagePolicySummary> UpsertLanguagePolicyInternalAsync(
        UpsertLanguagePolicyRequest request,
        bool isPreset,
        long? id,
        CancellationToken ct)
    {
        var slug = NormalizeSlug(request.Slug, request.Name);
        var tiersJson = JsonSerializer.Serialize(request.Tiers.OrderBy(t => t.Tier).ToArray(), JsonOpts);
        var now = DateTimeOffset.UtcNow.ToString("O");

        var savedId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                if (id.HasValue)
                {
                    await conn.ExecuteAsync(
                        """
                        UPDATE language_policies
                        SET name                           = @Name,
                            description                    = @Desc,
                            tiers_json                     = @TiersJson,
                            enable_language_upgrades       = @EnableUpgrades,
                            language_cutoff_tier           = @CutoffTier,
                            tradeoff_mode                  = @Tradeoff,
                            max_quality_tier_drop          = @MaxDrop,
                            minimum_absolute_quality       = @MinFloor,
                            preserve_previous_as_alternate = @PreserveAlt,
                            verification_mode              = @VerifyMode,
                            updated_at                     = @Now
                        WHERE id = @Id;
                        """,
                        new
                        {
                            Id = id.Value,
                            Name = request.Name.Trim(),
                            Desc = request.Description.Trim(),
                            TiersJson = tiersJson,
                            EnableUpgrades = request.EnableLanguageUpgrades ? 1 : 0,
                            CutoffTier = request.LanguageCutoffTier,
                            Tradeoff = request.TradeoffMode,
                            MaxDrop = request.MaxQualityTierDrop,
                            MinFloor = request.MinimumAbsoluteQuality,
                            PreserveAlt = request.PreservePreviousAsAlternate ? 1 : 0,
                            VerifyMode = request.VerificationMode,
                            Now = now,
                        },
                        transaction: tx);
                    return id.Value;
                }

                return await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO language_policies (
                        slug, name, description, is_system_preset, tiers_json,
                        enable_language_upgrades, language_cutoff_tier, tradeoff_mode,
                        max_quality_tier_drop, minimum_absolute_quality,
                        preserve_previous_as_alternate, verification_mode, created_at, updated_at
                    ) VALUES (
                        @Slug, @Name, @Desc, @IsPreset, @TiersJson,
                        @EnableUpgrades, @CutoffTier, @Tradeoff,
                        @MaxDrop, @MinFloor,
                        @PreserveAlt, @VerifyMode, @Now, @Now
                    );
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        Slug = slug,
                        Name = request.Name.Trim(),
                        Desc = request.Description.Trim(),
                        IsPreset = isPreset ? 1 : 0,
                        TiersJson = tiersJson,
                        EnableUpgrades = request.EnableLanguageUpgrades ? 1 : 0,
                        CutoffTier = request.LanguageCutoffTier,
                        Tradeoff = request.TradeoffMode,
                        MaxDrop = request.MaxQualityTierDrop,
                        MinFloor = request.MinimumAbsoluteQuality,
                        PreserveAlt = request.PreservePreviousAsAlternate ? 1 : 0,
                        VerifyMode = request.VerificationMode,
                        Now = now,
                    },
                    transaction: tx);
            },
            ct);

        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await readConn.QuerySingleAsync<LangPolicyRow>("SELECT * FROM language_policies WHERE id = @Id;", new { Id = savedId });
        return MapLanguagePolicy(row);
    }

    private async Task<QualityProfileSummary> UpsertProfileInternalAsync(
        UpsertQualityProfileRequest request,
        bool isPreset,
        long? id,
        CancellationToken ct)
    {
        var slug = NormalizeSlug(request.Slug, request.Name);
        var qualitiesJson = JsonSerializer.Serialize(request.AllowedQualities, JsonOpts);
        var mixinsJson = JsonSerializer.Serialize(request.MixinIds ?? [], JsonOpts);
        var cfScoresJson = JsonSerializer.Serialize(request.CustomFormatScores ?? new Dictionary<long, int>(), JsonOpts);
        var retainAudioJson = JsonSerializer.Serialize(request.RetainAudioLangs ?? [], JsonOpts);
        var retainSubJson = JsonSerializer.Serialize(request.RetainSubLangs ?? [], JsonOpts);
        var now = DateTimeOffset.UtcNow.ToString("O");

        var savedId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                if (id.HasValue)
                {
                    await conn.ExecuteAsync(
                        """
                        UPDATE quality_profiles
                        SET name                        = @Name,
                            media_vertical              = @Vertical,
                            allowed_qualities_json      = @QualitiesJson,
                            quality_cutoff              = @QualityCutoff,
                            language_policy_id          = @LangPolicyId,
                            mixin_ids_json              = @MixinsJson,
                            custom_format_scores_json   = @CfScoresJson,
                            min_trait_score             = @MinTrait,
                            weight_quality              = @Wq,
                            weight_language             = @Wl,
                            weight_traits               = @Wt,
                            weight_efficiency           = @We,
                            retain_audio_langs_json     = @RetainAudio,
                            retain_sub_langs_json       = @RetainSub,
                            updated_at                  = @Now
                        WHERE id = @Id;
                        """,
                        new
                        {
                            Id = id.Value,
                            Name = request.Name.Trim(),
                            Vertical = request.MediaVertical.Trim().ToLowerInvariant(),
                            QualitiesJson = qualitiesJson,
                            request.QualityCutoff,
                            LangPolicyId = request.LanguagePolicyId,
                            MixinsJson = mixinsJson,
                            CfScoresJson = cfScoresJson,
                            MinTrait = request.MinTraitScore,
                            Wq = request.WeightQuality,
                            Wl = request.WeightLanguage,
                            Wt = request.WeightTraits,
                            We = request.WeightEfficiency,
                            RetainAudio = retainAudioJson,
                            RetainSub = retainSubJson,
                            Now = now,
                        },
                        transaction: tx);
                    return id.Value;
                }

                return await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO quality_profiles (
                        slug, name, media_vertical, is_system_preset, allowed_qualities_json,
                        quality_cutoff, language_policy_id, mixin_ids_json, custom_format_scores_json,
                        min_trait_score, weight_quality, weight_language, weight_traits, weight_efficiency,
                        retain_audio_langs_json, retain_sub_langs_json, created_at, updated_at
                    ) VALUES (
                        @Slug, @Name, @Vertical, @IsPreset, @QualitiesJson,
                        @QualityCutoff, @LangPolicyId, @MixinsJson, @CfScoresJson,
                        @MinTrait, @Wq, @Wl, @Wt, @We,
                        @RetainAudio, @RetainSub, @Now, @Now
                    );
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        Slug = slug,
                        Name = request.Name.Trim(),
                        Vertical = request.MediaVertical.Trim().ToLowerInvariant(),
                        IsPreset = isPreset ? 1 : 0,
                        QualitiesJson = qualitiesJson,
                        request.QualityCutoff,
                        LangPolicyId = request.LanguagePolicyId,
                        MixinsJson = mixinsJson,
                        CfScoresJson = cfScoresJson,
                        MinTrait = request.MinTraitScore,
                        Wq = request.WeightQuality,
                        Wl = request.WeightLanguage,
                        Wt = request.WeightTraits,
                        We = request.WeightEfficiency,
                        RetainAudio = retainAudioJson,
                        RetainSub = retainSubJson,
                        Now = now,
                    },
                    transaction: tx);
            },
            ct);

        return (await GetProfileByIdAsync(savedId, ct))!;
    }

    private static string NormalizeSlug(string? explicitSlug, string name)
    {
        var raw = string.IsNullOrWhiteSpace(explicitSlug) ? name : explicitSlug;
        var clean = SlugSanitizer().Replace(raw.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(clean) ? $"prof-{Guid.NewGuid():N}"[..12] : clean;
    }

    private static LanguagePolicySummary MapLanguagePolicy(LangPolicyRow row) =>
        new(
            Id: row.Id,
            Slug: row.Slug,
            Name: row.Name,
            Description: row.Description,
            IsSystemPreset: row.IsSystemPreset != 0,
            Tiers: JsonSerializer.Deserialize<LanguagePolicyTier[]>(row.TiersJson, JsonOpts) ?? [],
            EnableLanguageUpgrades: row.EnableLanguageUpgrades != 0,
            LanguageCutoffTier: row.LanguageCutoffTier,
            TradeoffMode: row.TradeoffMode,
            MaxQualityTierDrop: row.MaxQualityTierDrop,
            MinimumAbsoluteQuality: row.MinimumAbsoluteQuality,
            PreservePreviousAsAlternate: row.PreservePreviousAsAlternate != 0,
            VerificationMode: row.VerificationMode,
            CreatedAt: row.CreatedAt,
            UpdatedAt: row.UpdatedAt);

    private static ProfileMixinSummary MapMixin(MixinRow row) =>
        new(
            Id: row.Id,
            Slug: row.Slug,
            Name: row.Name,
            Category: row.Category,
            Rules: JsonSerializer.Deserialize<TraitMixinRule[]>(row.RulesJson, JsonOpts) ?? [],
            CreatedAt: row.CreatedAt,
            UpdatedAt: row.UpdatedAt);

    private static QualityProfileSummary MapProfile(ProfileRow row, LanguagePolicySummary? langPolicy) =>
        new(
            Id: row.Id,
            Slug: row.Slug,
            Name: row.Name,
            MediaVertical: row.MediaVertical,
            IsSystemPreset: row.IsSystemPreset != 0,
            AllowedQualities: JsonSerializer.Deserialize<AllowedQualityTier[]>(row.AllowedQualitiesJson, JsonOpts) ?? [],
            QualityCutoff: row.QualityCutoff,
            LanguagePolicyId: row.LanguagePolicyId,
            LanguagePolicy: langPolicy,
            MixinIds: JsonSerializer.Deserialize<long[]>(row.MixinIdsJson, JsonOpts) ?? [],
            CustomFormatScores: JsonSerializer.Deserialize<Dictionary<long, int>>(row.CustomFormatScoresJson, JsonOpts) ?? new Dictionary<long, int>(),
            MinTraitScore: row.MinTraitScore,
            WeightQuality: row.WeightQuality,
            WeightLanguage: row.WeightLanguage,
            WeightTraits: row.WeightTraits,
            WeightEfficiency: row.WeightEfficiency,
            RetainAudioLangs: JsonSerializer.Deserialize<string[]>(row.RetainAudioLangsJson, JsonOpts) ?? [],
            RetainSubLangs: JsonSerializer.Deserialize<string[]>(row.RetainSubLangsJson, JsonOpts) ?? [],
            CreatedAt: row.CreatedAt,
            UpdatedAt: row.UpdatedAt);

    private sealed class LangPolicyRow
    {
        public long Id { get; init; }
        public string Slug { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public int IsSystemPreset { get; init; }
        public string TiersJson { get; init; } = "[]";
        public int EnableLanguageUpgrades { get; init; }
        public int LanguageCutoffTier { get; init; }
        public string TradeoffMode { get; init; } = "bounded_stepdown";
        public int MaxQualityTierDrop { get; init; }
        public string? MinimumAbsoluteQuality { get; init; }
        public int PreservePreviousAsAlternate { get; init; }
        public string VerificationMode { get; init; } = "";
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private sealed class MixinRow
    {
        public long Id { get; init; }
        public string Slug { get; init; } = "";
        public string Name { get; init; } = "";
        public string Category { get; init; } = "";
        public string RulesJson { get; init; } = "[]";
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private sealed class ProfileRow
    {
        public long Id { get; init; }
        public string Slug { get; init; } = "";
        public string Name { get; init; } = "";
        public string MediaVertical { get; init; } = "";
        public int IsSystemPreset { get; init; }
        public string AllowedQualitiesJson { get; init; } = "[]";
        public string QualityCutoff { get; init; } = "";
        public long? LanguagePolicyId { get; init; }
        public string MixinIdsJson { get; init; } = "[]";
        public string CustomFormatScoresJson { get; init; } = "{}";
        public int MinTraitScore { get; init; }
        public double WeightQuality { get; init; }
        public double WeightLanguage { get; init; }
        public double WeightTraits { get; init; }
        public double WeightEfficiency { get; init; }
        public string RetainAudioLangsJson { get; init; } = "[]";
        public string RetainSubLangsJson { get; init; } = "[]";
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private async Task EnsureWave4DefaultsSeededAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        int delayCount;
        int sizeCount;
        await using (var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            delayCount = await readConn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM delay_profiles;");
            sizeCount = await readConn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM quality_size_limits;");
        }

        if (delayCount == 0)
        {
            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                (conn, tx) => conn.ExecuteAsync(
                    """
                    INSERT INTO delay_profiles (
                        name, preferred_protocol, usenet_delay_minutes, torrent_delay_minutes,
                        bypass_if_highest_quality, bypass_if_above_custom_format_score, min_custom_format_score,
                        tags_json, sort_order, is_default, created_at, updated_at
                    ) VALUES (
                        'Default (Prefer Usenet)', 'usenet', 0, 0,
                        1, 0, 0,
                        '[]', 10000, 1, @Now, @Now
                    );
                    """,
                    new { Now = now },
                    transaction: tx),
                ct);
        }

        if (sizeCount == 0)
        {
            var defaultTiers = new (string Quality, string Vertical)[]
            {
                ("SD-480p", "video"),
                ("HDTV-720p", "video"),
                ("WEBDL-720p", "video"),
                ("BluRay-720p", "video"),
                ("HDTV-1080p", "video"),
                ("WEBRip-1080p", "video"),
                ("WEBDL-1080p", "video"),
                ("BluRay-1080p", "video"),
                ("Remux-1080p", "video"),
                ("WEBRip-2160p", "video"),
                ("WEBDL-2160p", "video"),
                ("BluRay-2160p", "video"),
                ("Remux-2160p", "video"),
                ("MP3-V0", "music"),
                ("MP3-320", "music"),
                ("FLAC", "music"),
                ("FLAC 24bit", "music"),
            };

            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                async (conn, tx) =>
                {
                    foreach (var (quality, vertical) in defaultTiers)
                    {
                        await conn.ExecuteAsync(
                            """
                            INSERT OR IGNORE INTO quality_size_limits (
                                quality_name, media_vertical, min_mb_per_min, preferred_mb_per_min, max_mb_per_min, updated_at
                            ) VALUES (
                                @Quality, @Vertical, 0, 0, 0, @Now
                            );
                            """,
                            new { Quality = quality, Vertical = vertical, Now = now },
                            transaction: tx);
                    }
                },
                ct);
        }
    }

    public async Task<IReadOnlyList<ReleaseProfileSummary>> ListReleaseProfilesAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<ReleaseProfileRow>("SELECT * FROM release_profiles ORDER BY id ASC;");
        return rows.Select(MapReleaseProfile).ToList();
    }

    public async Task<ReleaseProfileSummary> UpsertReleaseProfileAsync(
        UpsertReleaseProfileRequest request,
        long? id = null,
        CancellationToken ct = default)
    {
        var name = string.IsNullOrWhiteSpace(request.Name) ? "Release Restriction" : request.Name.Trim();
        var mustContain = (request.MustContain ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray();
        var mustNotContain = (request.MustNotContain ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray();
        var preferred = (request.PreferredTerms ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Term))
            .Select(p => new PreferredTermScore(p.Term.Trim(), p.Score))
            .ToArray();
        var tags = (request.Tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var vertical = string.IsNullOrWhiteSpace(request.MediaVertical) ? null : request.MediaVertical.Trim();
        var now = DateTimeOffset.UtcNow.ToString("O");

        var savedId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                if (id.HasValue)
                {
                    var affected = await conn.ExecuteAsync(
                        """
                        UPDATE release_profiles
                        SET name                  = @Name,
                            is_enabled            = @Enabled,
                            must_contain_json     = @MustContainJson,
                            must_not_contain_json = @MustNotContainJson,
                            preferred_terms_json  = @PreferredJson,
                            indexer_id            = @IndexerId,
                            tags_json             = @TagsJson,
                            media_vertical        = @Vertical,
                            updated_at            = @Now
                        WHERE id = @Id;
                        """,
                        new
                        {
                            Id = id.Value,
                            Name = name,
                            Enabled = request.IsEnabled ? 1 : 0,
                            MustContainJson = JsonSerializer.Serialize(mustContain, JsonOpts),
                            MustNotContainJson = JsonSerializer.Serialize(mustNotContain, JsonOpts),
                            PreferredJson = JsonSerializer.Serialize(preferred, JsonOpts),
                            IndexerId = request.IndexerId is > 0 ? request.IndexerId : null,
                            TagsJson = JsonSerializer.Serialize(tags, JsonOpts),
                            Vertical = vertical,
                            Now = now,
                        },
                        transaction: tx);
                    if (affected == 0)
                    {
                        throw new KeyNotFoundException($"Release Profile #{id.Value} not found.");
                    }

                    return id.Value;
                }

                return await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO release_profiles (
                        name, is_enabled, must_contain_json, must_not_contain_json,
                        preferred_terms_json, indexer_id, tags_json, media_vertical,
                        created_at, updated_at
                    ) VALUES (
                        @Name, @Enabled, @MustContainJson, @MustNotContainJson,
                        @PreferredJson, @IndexerId, @TagsJson, @Vertical,
                        @Now, @Now
                    );
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        Name = name,
                        Enabled = request.IsEnabled ? 1 : 0,
                        MustContainJson = JsonSerializer.Serialize(mustContain, JsonOpts),
                        MustNotContainJson = JsonSerializer.Serialize(mustNotContain, JsonOpts),
                        PreferredJson = JsonSerializer.Serialize(preferred, JsonOpts),
                        IndexerId = request.IndexerId is > 0 ? request.IndexerId : null,
                        TagsJson = JsonSerializer.Serialize(tags, JsonOpts),
                        Vertical = vertical,
                        Now = now,
                    },
                    transaction: tx);
            },
            ct);

        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await readConn.QuerySingleAsync<ReleaseProfileRow>("SELECT * FROM release_profiles WHERE id = @Id;", new { Id = savedId });
        return MapReleaseProfile(row);
    }

    public async Task DeleteReleaseProfileAsync(long id, CancellationToken ct = default)
    {
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                var affected = await conn.ExecuteAsync(
                    "DELETE FROM release_profiles WHERE id = @Id;",
                    new { Id = id },
                    transaction: tx);
                if (affected == 0)
                {
                    throw new KeyNotFoundException($"Release Profile #{id} not found.");
                }
            },
            ct);
    }

    public async Task<IReadOnlyList<DelayProfileSummary>> ListDelayProfilesAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<DelayProfileRow>(
            "SELECT * FROM delay_profiles ORDER BY is_default ASC, sort_order ASC, id ASC;");
        return rows.Select(MapDelayProfile).ToList();
    }

    public async Task<DelayProfileSummary> UpsertDelayProfileAsync(
        UpsertDelayProfileRequest request,
        long? id = null,
        CancellationToken ct = default)
    {
        var name = string.IsNullOrWhiteSpace(request.Name) ? "Delay Profile" : request.Name.Trim();
        var prefProtocol = (request.PreferredProtocol ?? "usenet").Trim().ToLowerInvariant() switch
        {
            "torrent" or "torrents" => "torrent",
            "any" or "none" => "any",
            _ => "usenet",
        };
        var usenetDelay = Math.Max(0, request.UsenetDelayMinutes);
        var torrentDelay = Math.Max(0, request.TorrentDelayMinutes);
        var tags = (request.Tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var now = DateTimeOffset.UtcNow.ToString("O");

        var savedId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                if (id.HasValue)
                {
                    var isDef = await conn.ExecuteScalarAsync<int?>(
                        "SELECT is_default FROM delay_profiles WHERE id = @Id;",
                        new { Id = id.Value },
                        transaction: tx);
                    if (!isDef.HasValue)
                    {
                        throw new KeyNotFoundException($"Delay Profile #{id.Value} not found.");
                    }

                    var effectiveTags = isDef.Value != 0 ? Array.Empty<string>() : tags;
                    await conn.ExecuteAsync(
                        """
                        UPDATE delay_profiles
                        SET name                                = @Name,
                            preferred_protocol                  = @PrefProtocol,
                            usenet_delay_minutes                = @UsenetDelay,
                            torrent_delay_minutes               = @TorrentDelay,
                            bypass_if_highest_quality           = @BypassQuality,
                            bypass_if_above_custom_format_score = @BypassCf,
                            min_custom_format_score             = @MinCf,
                            tags_json                           = @TagsJson,
                            sort_order                          = @SortOrder,
                            updated_at                          = @Now
                        WHERE id = @Id;
                        """,
                        new
                        {
                            Id = id.Value,
                            Name = name,
                            PrefProtocol = prefProtocol,
                            UsenetDelay = usenetDelay,
                            TorrentDelay = torrentDelay,
                            BypassQuality = request.BypassIfHighestQuality ? 1 : 0,
                            BypassCf = request.BypassIfAboveCustomFormatScore ? 1 : 0,
                            MinCf = request.MinCustomFormatScoreForBypass,
                            TagsJson = JsonSerializer.Serialize(effectiveTags, JsonOpts),
                            SortOrder = isDef.Value != 0 ? 10000 : request.SortOrder,
                            Now = now,
                        },
                        transaction: tx);
                    return id.Value;
                }

                return await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO delay_profiles (
                        name, preferred_protocol, usenet_delay_minutes, torrent_delay_minutes,
                        bypass_if_highest_quality, bypass_if_above_custom_format_score, min_custom_format_score,
                        tags_json, sort_order, is_default, created_at, updated_at
                    ) VALUES (
                        @Name, @PrefProtocol, @UsenetDelay, @TorrentDelay,
                        @BypassQuality, @BypassCf, @MinCf,
                        @TagsJson, @SortOrder, 0, @Now, @Now
                    );
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        Name = name,
                        PrefProtocol = prefProtocol,
                        UsenetDelay = usenetDelay,
                        TorrentDelay = torrentDelay,
                        BypassQuality = request.BypassIfHighestQuality ? 1 : 0,
                        BypassCf = request.BypassIfAboveCustomFormatScore ? 1 : 0,
                        MinCf = request.MinCustomFormatScoreForBypass,
                        TagsJson = JsonSerializer.Serialize(tags, JsonOpts),
                        SortOrder = request.SortOrder,
                        Now = now,
                    },
                    transaction: tx);
            },
            ct);

        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await readConn.QuerySingleAsync<DelayProfileRow>("SELECT * FROM delay_profiles WHERE id = @Id;", new { Id = savedId });
        return MapDelayProfile(row);
    }

    public async Task DeleteDelayProfileAsync(long id, CancellationToken ct = default)
    {
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                var isDefault = await conn.ExecuteScalarAsync<int?>(
                    "SELECT is_default FROM delay_profiles WHERE id = @Id;",
                    new { Id = id },
                    transaction: tx);
                if (!isDefault.HasValue)
                {
                    throw new KeyNotFoundException($"Delay Profile #{id} not found.");
                }

                if (isDefault.Value != 0)
                {
                    throw new InvalidOperationException("Cannot delete the default Delay Profile.");
                }

                await conn.ExecuteAsync("DELETE FROM delay_profiles WHERE id = @Id;", new { Id = id }, transaction: tx);
            },
            ct);
    }

    public async Task<DelayEvaluationDecision> EvaluateDelayAsync(
        string protocol,
        DateTimeOffset? publishedAt,
        IReadOnlyList<string>? workTags,
        ReleaseSimulationScorecard scorecard,
        CancellationToken ct = default)
    {
        var profiles = await ListDelayProfilesAsync(ct);
        if (profiles.Count == 0)
        {
            return new DelayEvaluationDecision(false, 0, 0, "Default", "any", true, "No delay profiles configured.");
        }

        var tagSet = (workTags ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matched = profiles
            .Where(p => !p.IsDefault && p.Tags.Count > 0 && p.Tags.Any(tagSet.Contains))
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Id)
            .FirstOrDefault()
            ?? profiles.FirstOrDefault(p => p.IsDefault)
            ?? profiles[0];

        var normProto = (protocol ?? "torrent").Trim().ToLowerInvariant() switch
        {
            "usenet" or "nzb" => "usenet",
            _ => "torrent",
        };

        var protocolPreferred = string.Equals(matched.PreferredProtocol, "any", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(matched.PreferredProtocol, normProto, StringComparison.OrdinalIgnoreCase);

        var requiredMinutes = normProto == "usenet" ? matched.UsenetDelayMinutes : matched.TorrentDelayMinutes;
        var ageMinutes = publishedAt.HasValue
            ? Math.Max(0, (DateTimeOffset.UtcNow - publishedAt.Value).TotalMinutes)
            : double.MaxValue;

        if (requiredMinutes <= 0)
        {
            return new DelayEvaluationDecision(
                IsDelayed: false,
                RequiredDelayMinutes: 0,
                AgeMinutes: double.IsInfinity(ageMinutes) || ageMinutes > 1_000_000 ? 0 : Math.Round(ageMinutes, 1),
                MatchedProfileName: matched.Name,
                PreferredProtocol: matched.PreferredProtocol,
                ProtocolPreferred: protocolPreferred,
                Reason: $"No delay required for {normProto} under '{matched.Name}'.");
        }

        if (matched.BypassIfHighestQuality &&
            scorecard.CandidateSatisfiesQualityCutoff &&
            scorecard.CandidateSatisfiesLanguageCutoff)
        {
            return new DelayEvaluationDecision(
                IsDelayed: false,
                RequiredDelayMinutes: requiredMinutes,
                AgeMinutes: ageMinutes > 1_000_000 ? 0 : Math.Round(ageMinutes, 1),
                MatchedProfileName: matched.Name,
                PreferredProtocol: matched.PreferredProtocol,
                ProtocolPreferred: protocolPreferred,
                Reason: $"Bypassed {requiredMinutes}m {normProto} delay because candidate satisfies quality and language cutoffs.");
        }

        if (matched.BypassIfAboveCustomFormatScore &&
            scorecard.TraitScore >= matched.MinCustomFormatScoreForBypass)
        {
            return new DelayEvaluationDecision(
                IsDelayed: false,
                RequiredDelayMinutes: requiredMinutes,
                AgeMinutes: ageMinutes > 1_000_000 ? 0 : Math.Round(ageMinutes, 1),
                MatchedProfileName: matched.Name,
                PreferredProtocol: matched.PreferredProtocol,
                ProtocolPreferred: protocolPreferred,
                Reason: $"Bypassed {requiredMinutes}m {normProto} delay because trait/CF score ({scorecard.TraitScore:F0}) >= {matched.MinCustomFormatScoreForBypass}.");
        }

        if (ageMinutes < requiredMinutes)
        {
            return new DelayEvaluationDecision(
                IsDelayed: true,
                RequiredDelayMinutes: requiredMinutes,
                AgeMinutes: Math.Round(ageMinutes, 1),
                MatchedProfileName: matched.Name,
                PreferredProtocol: matched.PreferredProtocol,
                ProtocolPreferred: protocolPreferred,
                Reason: $"Pending {normProto} delay under '{matched.Name}': release age {ageMinutes:F1}m < required {requiredMinutes}m.");
        }

        return new DelayEvaluationDecision(
            IsDelayed: false,
            RequiredDelayMinutes: requiredMinutes,
            AgeMinutes: ageMinutes > 1_000_000 ? 0 : Math.Round(ageMinutes, 1),
            MatchedProfileName: matched.Name,
            PreferredProtocol: matched.PreferredProtocol,
            ProtocolPreferred: protocolPreferred,
            Reason: $"Release age ({ageMinutes:F1}m) satisfies {requiredMinutes}m {normProto} delay under '{matched.Name}'.");
    }

    public async Task<IReadOnlyList<QualitySizeLimitDto>> ListQualitySizeLimitsAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<QualitySizeLimitRow>(
            "SELECT * FROM quality_size_limits ORDER BY media_vertical ASC, quality_name ASC;");
        return rows.Select(r => new QualitySizeLimitDto(
            QualityName: r.QualityName,
            MediaVertical: r.MediaVertical,
            MinMbPerMin: r.MinMbPerMin,
            PreferredMbPerMin: r.PreferredMbPerMin,
            MaxMbPerMin: r.MaxMbPerMin,
            UpdatedAt: r.UpdatedAt)).ToList();
    }

    public async Task<IReadOnlyList<QualitySizeLimitDto>> UpsertQualitySizeLimitsAsync(
        IReadOnlyList<UpsertQualitySizeLimitItem> items,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                foreach (var item in items ?? [])
                {
                    if (string.IsNullOrWhiteSpace(item.QualityName))
                    {
                        continue;
                    }

                    var minVal = Math.Max(0, item.MinMbPerMin);
                    var maxVal = Math.Max(0, item.MaxMbPerMin);
                    if (maxVal > 0 && maxVal < minVal)
                    {
                        maxVal = minVal;
                    }

                    var prefVal = Math.Max(0, item.PreferredMbPerMin);
                    if (prefVal > 0 && minVal > 0 && prefVal < minVal)
                    {
                        prefVal = minVal;
                    }

                    if (prefVal > 0 && maxVal > 0 && prefVal > maxVal)
                    {
                        prefVal = maxVal;
                    }

                    await conn.ExecuteAsync(
                        """
                        INSERT INTO quality_size_limits (
                            quality_name, media_vertical, min_mb_per_min, preferred_mb_per_min, max_mb_per_min, updated_at
                        ) VALUES (
                            @Quality, @Vertical, @Min, @Pref, @Max, @Now
                        )
                        ON CONFLICT(quality_name) DO UPDATE SET
                            media_vertical       = excluded.media_vertical,
                            min_mb_per_min       = excluded.min_mb_per_min,
                            preferred_mb_per_min = excluded.preferred_mb_per_min,
                            max_mb_per_min       = excluded.max_mb_per_min,
                            updated_at           = excluded.updated_at;
                        """,
                        new
                        {
                            Quality = item.QualityName.Trim(),
                            Vertical = string.IsNullOrWhiteSpace(item.MediaVertical) ? "video" : item.MediaVertical.Trim(),
                            Min = minVal,
                            Pref = prefVal,
                            Max = maxVal,
                            Now = now,
                        },
                        transaction: tx);
                }
            },
            ct);

        return await ListQualitySizeLimitsAsync(ct);
    }

    private static ReleaseProfileSummary MapReleaseProfile(ReleaseProfileRow row) =>
        new(
            Id: row.Id,
            Name: row.Name,
            IsEnabled: row.IsEnabled != 0,
            MustContain: JsonSerializer.Deserialize<string[]>(row.MustContainJson, JsonOpts) ?? [],
            MustNotContain: JsonSerializer.Deserialize<string[]>(row.MustNotContainJson, JsonOpts) ?? [],
            PreferredTerms: JsonSerializer.Deserialize<PreferredTermScore[]>(row.PreferredTermsJson, JsonOpts) ?? [],
            IndexerId: row.IndexerId,
            Tags: JsonSerializer.Deserialize<string[]>(row.TagsJson, JsonOpts) ?? [],
            MediaVertical: row.MediaVertical,
            CreatedAt: row.CreatedAt,
            UpdatedAt: row.UpdatedAt);

    private static DelayProfileSummary MapDelayProfile(DelayProfileRow row) =>
        new(
            Id: row.Id,
            Name: row.Name,
            PreferredProtocol: row.PreferredProtocol,
            UsenetDelayMinutes: row.UsenetDelayMinutes,
            TorrentDelayMinutes: row.TorrentDelayMinutes,
            BypassIfHighestQuality: row.BypassIfHighestQuality != 0,
            BypassIfAboveCustomFormatScore: row.BypassIfAboveCustomFormatScore != 0,
            MinCustomFormatScoreForBypass: row.MinCustomFormatScore,
            Tags: JsonSerializer.Deserialize<string[]>(row.TagsJson, JsonOpts) ?? [],
            SortOrder: row.SortOrder,
            IsDefault: row.IsDefault != 0,
            CreatedAt: row.CreatedAt,
            UpdatedAt: row.UpdatedAt);

    private sealed class ReleaseProfileRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public int IsEnabled { get; init; }
        public string MustContainJson { get; init; } = "[]";
        public string MustNotContainJson { get; init; } = "[]";
        public string PreferredTermsJson { get; init; } = "[]";
        public long? IndexerId { get; init; }
        public string TagsJson { get; init; } = "[]";
        public string? MediaVertical { get; init; }
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private sealed class DelayProfileRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string PreferredProtocol { get; init; } = "usenet";
        public int UsenetDelayMinutes { get; init; }
        public int TorrentDelayMinutes { get; init; }
        public int BypassIfHighestQuality { get; init; }
        public int BypassIfAboveCustomFormatScore { get; init; }
        public int MinCustomFormatScore { get; init; }
        public string TagsJson { get; init; } = "[]";
        public int SortOrder { get; init; }
        public int IsDefault { get; init; }
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private sealed class QualitySizeLimitRow
    {
        public string QualityName { get; init; } = "";
        public string MediaVertical { get; init; } = "video";
        public double MinMbPerMin { get; init; }
        public double PreferredMbPerMin { get; init; }
        public double MaxMbPerMin { get; init; }
        public string UpdatedAt { get; init; } = "";
    }
}
