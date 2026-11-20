using Managerr.Datastore.Services;

namespace Managerr.Metadata;

public sealed record MergedWorkMetadataFields(
    string Title,
    IReadOnlyList<string> AltTitles,
    string OriginalLanguage,
    int? Year,
    string Status,
    string Overview,
    string? PosterUrl,
    IReadOnlyDictionary<string, string> ExternalIds);

public interface IMetadataFederationService
{
    IReadOnlyList<MetadataProviderDescriptor> ListBuiltInProviders(string? vertical = null);
    Task<IReadOnlyList<MetadataProviderStatus>> ListProviderStatusesAsync(string? vertical = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MetadataProviderStatus>>(
            ListBuiltInProviders(vertical)
                .Select(d => new MetadataProviderStatus(
                    d.Key,
                    d.DisplayName,
                    d.IsBuiltInArrDefault,
                    d.RequiresApiKey,
                    d.ConfigurationStatus,
                    d.DefaultBaseUrl,
                    d.SupportedVerticals))
                .ToList());
    IReadOnlyList<string> GetDefaultWaterfall(string vertical);
    Task<IReadOnlyList<MetadataWorkCandidate>> SearchWaterfallAsync(
        string query,
        string vertical,
        IReadOnlyList<string>? customWaterfall = null,
        CancellationToken ct = default);
    Task<MetadataWorkCandidate?> ResolveWorkAsync(
        string vertical,
        string? preferredProvider,
        IReadOnlyDictionary<string, string> knownExternalIds,
        IReadOnlyList<string>? customWaterfall = null,
        CancellationToken ct = default);
    Task<MetadataWorkCandidate?> ResolveWorkAsync(
        string vertical,
        string? preferredProvider,
        IReadOnlyDictionary<string, string> knownExternalIds,
        IReadOnlyList<string>? customWaterfall,
        bool forceRefresh,
        CancellationToken ct = default);
    MergedWorkMetadataFields ApplyFieldMergeWithLocks(
        MergedWorkMetadataFields current,
        MetadataWorkCandidate incoming,
        IReadOnlySet<string> lockedFields);
}

/// <summary>
/// Pluggable multi-provider metadata waterfall backed by HybridSqliteCache (cache.db + IMemoryCache)
/// and ExternalIdCrosswalkService, with field-level lock enforcement, English/Latin normalization,
/// anti-spam positive/negative caching, and query-relevance ranking.
/// </summary>
public sealed class MetadataFederationService : IMetadataFederationService
{
    public static readonly TimeSpan SearchCacheTtl = TimeSpan.FromHours(24);
    public static readonly TimeSpan EmptySearchCacheTtl = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan LookupCacheTtl = TimeSpan.FromDays(7);

    private sealed record CachedSearchEnvelope(List<MetadataWorkCandidate> Items);

    private readonly Dictionary<string, IMetadataProvider> _providers;
    private readonly ICustomHttpMetadataProviderService _customWebhooks;
    private readonly IExternalIdCrosswalkService _crosswalk;
    private readonly IHybridSqliteCache _cache;

    public MetadataFederationService(
        IEnumerable<IMetadataProvider> builtInProviders,
        ICustomHttpMetadataProviderService customWebhooks,
        IExternalIdCrosswalkService crosswalk,
        IHybridSqliteCache cache)
    {
        _providers = builtInProviders.ToDictionary(p => p.Descriptor.Key, StringComparer.OrdinalIgnoreCase);
        _customWebhooks = customWebhooks;
        _crosswalk = crosswalk;
        _cache = cache;
    }

    public IReadOnlyList<MetadataProviderDescriptor> ListBuiltInProviders(string? vertical = null) =>
        string.IsNullOrWhiteSpace(vertical)
            ? _providers.Values.Select(p => p.Descriptor).ToList()
            : _providers.Values
                .Where(p => p.Descriptor.SupportedVerticals.Contains(vertical, StringComparer.OrdinalIgnoreCase))
                .Select(p => p.Descriptor)
                .ToList();

    public async Task<IReadOnlyList<MetadataProviderStatus>> ListProviderStatusesAsync(
        string? vertical = null,
        CancellationToken ct = default)
    {
        var matching = string.IsNullOrWhiteSpace(vertical)
            ? _providers.Values.ToList()
            : _providers.Values
                .Where(p => p.Descriptor.SupportedVerticals.Contains(vertical, StringComparer.OrdinalIgnoreCase))
                .ToList();

        var list = new List<MetadataProviderStatus>(matching.Count);
        foreach (var p in matching)
        {
            list.Add(await p.GetStatusAsync(ct));
        }

        return list;
    }

    public IReadOnlyList<string> GetDefaultWaterfall(string vertical) =>
        vertical.ToLowerInvariant() switch
        {
            MetadataVerticals.Tv => ["tvdb", "tmdb", "tvmaze", "trakt", "local_sidecar"],
            MetadataVerticals.Anime => ["tvdb", "anilist", "anidb", "tmdb", "tvmaze", "local_sidecar"],
            MetadataVerticals.Movies => ["tmdb", "tvdb", "trakt", "local_sidecar"],
            MetadataVerticals.Music => ["musicbrainz", "deezer", "acoustid", "discogs", "indie_folder_tags", "local_sidecar"],
            MetadataVerticals.Ebooks => ["openlibrary", "hardcover", "googlebooks", "dnb", "bookbrainz", "local_sidecar"],
            MetadataVerticals.Audiobooks => ["openlibrary", "audnexus", "hardcover", "dnb", "bookbrainz", "local_sidecar"],
            MetadataVerticals.Comics => ["comicvine", "mangadex", "anilist", "openlibrary", "local_sidecar"],
            _ => ["tmdb", "tvdb", "local_sidecar"],
        };

    public async Task<IReadOnlyList<MetadataWorkCandidate>> SearchWaterfallAsync(
        string query,
        string vertical,
        IReadOnlyList<string>? customWaterfall = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var trimmedQuery = query.Trim();
        var normVertical = string.IsNullOrWhiteSpace(vertical) ? "all" : vertical.Trim().ToLowerInvariant();

        // Cross-vertical unified search ("all" / "*") across Movies, TV, Anime, Books & Music
        if (normVertical is "all" or "*")
        {
            if (TryParseIdPrefix(trimmedQuery, out var pfxProvider, out var pfxValue, out var inferredVertical))
            {
                if (pfxProvider == "imdb")
                {
                    var movieImdb = await SearchWaterfallAsync(pfxValue, MetadataVerticals.Movies, ["tmdb"], ct);
                    if (movieImdb.Count > 0)
                    {
                        return movieImdb;
                    }

                    return await SearchWaterfallAsync(pfxValue, MetadataVerticals.Tv, ["tvdb", "tvmaze"], ct);
                }

                if (inferredVertical is not null)
                {
                    return await SearchWaterfallAsync($"{pfxProvider}:{pfxValue}", inferredVertical, customWaterfall, ct);
                }
            }

            var targetSearches = new (string Vertical, IReadOnlyList<string>? Waterfall)[]
            {
                (MetadataVerticals.Movies, null),
                (MetadataVerticals.Tv, null),
                // Query AniList/AniDB directly for Anime so we don't duplicate Tv's tvdb/tmdb calls
                (MetadataVerticals.Anime, ["anilist", "anidb"]),
                (MetadataVerticals.Ebooks, null),
                (MetadataVerticals.Music, null),
            };

            var perVerticalLists = await Task.WhenAll(
                targetSearches.Select(t => SearchWaterfallAsync(trimmedQuery, t.Vertical, t.Waterfall, ct)));

            var combinedRaw = new List<MetadataWorkCandidate>();
            var filteredLists = perVerticalLists
                .Select(list => list
                    .Where(c => !string.Equals(c.ProviderKey, "indie_folder_tags", StringComparison.OrdinalIgnoreCase))
                    .ToList())
                .ToArray();

            var maxLen = filteredLists.Length == 0 ? 0 : filteredLists.Max(l => l.Count);
            for (var i = 0; i < maxLen; i++)
            {
                foreach (var list in filteredLists)
                {
                    if (i < list.Count)
                    {
                        combinedRaw.Add(list[i]);
                    }
                }
            }

            var dedupedAll = DeduplicateAndMergeCandidates(combinedRaw);
            return RankAndFilterCandidates(dedupedAll, trimmedQuery, maxResults: 24);
        }

        // Direct ID-prefix lookup (e.g. tmdb:693134, tvdb:371980, imdb:tt15239678, openlibrary:OL20885734W)
        var effectiveQuery = trimmedQuery;
        if (TryParseIdPrefix(trimmedQuery, out var idProvider, out var idVal, out _))
        {
            if (idProvider == "imdb")
            {
                effectiveQuery = idVal;
            }
            else if (idProvider == "isbn")
            {
                effectiveQuery = $"isbn:{idVal}";
            }
            else
            {
                var directHit = await LookupCachedOrFetchAsync(idProvider, idVal, normVertical, forceRefresh: false, ct);
                if (directHit is not null)
                {
                    await _crosswalk.LinkClusterAsync(directHit.ExternalIds, normVertical, 1.0, ct);
                    var exp = await _crosswalk.ResolveConnectedIdsAsync(directHit.ExternalIds, ct);
                    return [directHit with { ExternalIds = exp }];
                }

                effectiveQuery = idVal;
            }
        }

        IReadOnlyList<string> waterfall;
        if (customWaterfall is { Count: > 0 })
        {
            waterfall = ResolveWaterfallOrder(normVertical, preferredProvider: null, customWaterfall);
        }
        else
        {
            var registeredWebhooks = await _customWebhooks.ListAsync(ct);
            var matchingWebhooks = registeredWebhooks
                .Where(w => w.SupportedVerticals.Contains(normVertical, StringComparer.OrdinalIgnoreCase))
                .Select(w => w.ProviderKey);
            var combined = matchingWebhooks.Concat(GetDefaultWaterfall(normVertical)).ToList();
            waterfall = ResolveWaterfallOrder(normVertical, preferredProvider: null, combined);
        }

        var collected = new List<MetadataWorkCandidate>();

        foreach (var providerKey in waterfall)
        {
            if (string.Equals(providerKey, "indie_folder_tags", StringComparison.OrdinalIgnoreCase) &&
                collected.Count > 0 &&
                customWaterfall is not { Count: > 0 })
            {
                continue;
            }

            if (_providers.TryGetValue(providerKey, out var builtInProv))
            {
                var status = await builtInProv.GetStatusAsync(ct);
                if (string.Equals(status.ConfigurationStatus, MetadataProviderConfigurationStates.NotConfigured, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            var cacheKey = $"meta:v4:search:{normVertical}:{providerKey}:{effectiveQuery.ToLowerInvariant()}";
            var envelope = await _cache.GetAsync<CachedSearchEnvelope>(cacheKey, ct);
            List<MetadataWorkCandidate> hits;
            if (envelope is null)
            {
                var fetched = await SearchSingleProviderAsync(providerKey, effectiveQuery, normVertical, ct);
                hits = fetched
                    .Select(LatinTitleNormalizer.NormalizeCandidate)
                    .Select(c => MediaTaxonomyClassifier.EnrichCandidate(c))
                    .ToList();
                var ttl = hits.Count > 0 ? SearchCacheTtl : EmptySearchCacheTtl;
                await _cache.SetAsync(cacheKey, new CachedSearchEnvelope(hits), ttl, ct);
            }
            else
            {
                hits = envelope.Items
                    .Select(c => MediaTaxonomyClassifier.EnrichCandidate(c))
                    .ToList();
            }

            foreach (var hit in hits)
            {
                await _crosswalk.LinkClusterAsync(hit.ExternalIds, normVertical, 1.0, ct);
                var expandedIds = await _crosswalk.ResolveConnectedIdsAsync(hit.ExternalIds, ct);
                collected.Add(hit with { ExternalIds = expandedIds });
            }

            // In the Anime vertical, always allow AniList to enrich Romaji/English synonyms even if TVDB returned >= 12 fuzzy hits
            var mustAlsoQueryAniList = normVertical == MetadataVerticals.Anime &&
                                       customWaterfall is not { Count: > 0 } &&
                                       string.Equals(providerKey, "tvdb", StringComparison.OrdinalIgnoreCase) &&
                                       waterfall.Contains("anilist", StringComparer.OrdinalIgnoreCase);

            if (collected.Count >= 12 && !mustAlsoQueryAniList)
            {
                break;
            }
        }

        var deduped = DeduplicateAndMergeCandidates(collected);
        return RankAndFilterCandidates(deduped, effectiveQuery, maxResults: 24);
    }

    private static bool TryParseIdPrefix(
        string query,
        out string provider,
        out string value,
        out string? inferredVertical)
    {
        provider = "";
        value = "";
        inferredVertical = null;

        if (query.StartsWith("tt", StringComparison.OrdinalIgnoreCase) &&
            query.Length >= 8 &&
            query[2..].All(char.IsDigit))
        {
            provider = "imdb";
            value = query.ToLowerInvariant();
            return true;
        }

        var colonIdx = query.IndexOf(':');
        if (colonIdx <= 1 || colonIdx >= query.Length - 1)
        {
            return false;
        }

        var rawPrefix = query[..colonIdx].Trim().ToLowerInvariant();
        var rawVal = query[(colonIdx + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(rawVal))
        {
            return false;
        }

        switch (rawPrefix)
        {
            case "imdb" or "imdbid":
                provider = "imdb";
                value = rawVal.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? rawVal.ToLowerInvariant() : $"tt{rawVal}";
                return true;
            case "tmdb" or "tmdbid":
                provider = "tmdb";
                value = rawVal;
                inferredVertical = MetadataVerticals.Movies;
                return true;
            case "tvdb" or "tvdbid":
                provider = "tvdb";
                value = rawVal;
                inferredVertical = MetadataVerticals.Tv;
                return true;
            case "tvmaze":
                provider = "tvmaze";
                value = rawVal;
                inferredVertical = MetadataVerticals.Tv;
                return true;
            case "anilist":
                provider = "anilist";
                value = rawVal;
                inferredVertical = MetadataVerticals.Anime;
                return true;
            case "anidb" or "aid":
                provider = "anidb";
                value = rawVal;
                inferredVertical = MetadataVerticals.Anime;
                return true;
            case "trakt":
                provider = "trakt";
                value = rawVal;
                inferredVertical = MetadataVerticals.Tv;
                return true;
            case "openlibrary" or "ol":
                provider = "openlibrary";
                value = rawVal;
                inferredVertical = MetadataVerticals.Ebooks;
                return true;
            case "hardcover":
                provider = "hardcover";
                value = rawVal;
                inferredVertical = MetadataVerticals.Ebooks;
                return true;
            case "dnb":
                provider = "dnb";
                value = rawVal;
                inferredVertical = MetadataVerticals.Ebooks;
                return true;
            case "bookbrainz" or "bbid":
                provider = "bookbrainz";
                value = rawVal;
                inferredVertical = MetadataVerticals.Ebooks;
                return true;
            case "discogs":
                provider = "discogs";
                value = rawVal;
                inferredVertical = MetadataVerticals.Music;
                return true;
            case "acoustid" or "trackid":
                provider = "acoustid";
                value = rawVal;
                inferredVertical = MetadataVerticals.Music;
                return true;
            case "comicvine":
                provider = "comicvine";
                value = rawVal;
                inferredVertical = MetadataVerticals.Comics;
                return true;
            case "asin":
                provider = "audnexus";
                value = rawVal;
                inferredVertical = MetadataVerticals.Audiobooks;
                return true;
            case "isbn" or "isbn13":
                provider = "isbn";
                value = rawVal.Replace("-", "", StringComparison.Ordinal);
                inferredVertical = MetadataVerticals.Ebooks;
                return true;
            default:
                return false;
        }
    }

    public Task<MetadataWorkCandidate?> ResolveWorkAsync(
        string vertical,
        string? preferredProvider,
        IReadOnlyDictionary<string, string> knownExternalIds,
        IReadOnlyList<string>? customWaterfall = null,
        CancellationToken ct = default) =>
        ResolveWorkAsync(vertical, preferredProvider, knownExternalIds, customWaterfall, forceRefresh: false, ct);

    public async Task<MetadataWorkCandidate?> ResolveWorkAsync(
        string vertical,
        string? preferredProvider,
        IReadOnlyDictionary<string, string> knownExternalIds,
        IReadOnlyList<string>? customWaterfall,
        bool forceRefresh,
        CancellationToken ct = default)
    {
        var expandedIds = await _crosswalk.ResolveConnectedIdsAsync(knownExternalIds, ct);
        var waterfall = ResolveWaterfallOrder(vertical, preferredProvider, customWaterfall);

        MetadataWorkCandidate? primary = null;
        var harvestedAltTitles = new List<string>();
        var harvestedGenres = new List<string>();
        string? formatHint = null;
        var mergedExternalIds = new Dictionary<string, string>(expandedIds, StringComparer.OrdinalIgnoreCase);

        foreach (var providerKey in waterfall)
        {
            if (!mergedExternalIds.TryGetValue(providerKey, out var extId) || string.IsNullOrWhiteSpace(extId))
            {
                continue;
            }

            var candidate = await LookupCachedOrFetchAsync(providerKey, extId, vertical, forceRefresh, ct);
            if (candidate is null)
            {
                continue;
            }

            foreach (var (k, v) in candidate.ExternalIds)
            {
                mergedExternalIds.TryAdd(k, v);
            }

            harvestedAltTitles.Add(candidate.Title);
            harvestedAltTitles.AddRange(candidate.AltTitles);
            if (candidate.Genres is { Count: > 0 })
            {
                harvestedGenres.AddRange(candidate.Genres);
            }

            if (string.Equals(candidate.ClassificationKind, "anime-movie", StringComparison.OrdinalIgnoreCase))
            {
                formatHint = "MOVIE";
            }

            if (primary is null)
            {
                primary = candidate;
            }
            else
            {
                // Enrich missing fields down the waterfall without overwriting structural authority
                primary = primary with
                {
                    Overview = string.IsNullOrWhiteSpace(primary.Overview) ? candidate.Overview : primary.Overview,
                    PosterUrl = string.IsNullOrWhiteSpace(primary.PosterUrl) ? candidate.PosterUrl : primary.PosterUrl,
                    Year = primary.Year ?? candidate.Year,
                    Popularity = Math.Max(primary.Popularity, candidate.Popularity),
                    Containers = MergeContainers(primary.Containers, candidate.Containers),
                };
            }
        }

        if (primary is null)
        {
            return null;
        }

        await _crosswalk.LinkClusterAsync(mergedExternalIds, vertical, 1.0, ct);
        var finalIds = await _crosswalk.ResolveConnectedIdsAsync(mergedExternalIds, ct);

        var distinctAlts = LatinTitleNormalizer.OrderAndCleanAltTitles(primary.Title, harvestedAltTitles);
        var distinctGenres = harvestedGenres.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var resolved = LatinTitleNormalizer.NormalizeCandidate(primary with
        {
            AltTitles = distinctAlts,
            ExternalIds = finalIds,
            Genres = distinctGenres,
        });

        return MediaTaxonomyClassifier.EnrichCandidate(resolved, formatHint: formatHint);
    }

    private async Task<MetadataWorkCandidate?> LookupCachedOrFetchAsync(
        string providerKey,
        string extId,
        string vertical,
        bool forceRefresh,
        CancellationToken ct)
    {
        var cacheKey = $"meta:v5:lookup:{vertical}:{providerKey}:{extId}";
        if (!forceRefresh)
        {
            var cached = await _cache.GetAsync<MetadataWorkCandidate>(cacheKey, ct);
            if (cached is not null)
            {
                return MediaTaxonomyClassifier.EnrichCandidate(cached);
            }
        }

        var rawCandidate = await LookupSingleProviderAsync(providerKey, extId, vertical, ct);
        if (rawCandidate is null)
        {
            return null;
        }

        var normalized = MediaTaxonomyClassifier.EnrichCandidate(LatinTitleNormalizer.NormalizeCandidate(rawCandidate));
        await _cache.SetAsync(cacheKey, normalized, LookupCacheTtl, ct);
        return normalized;
    }

    public MergedWorkMetadataFields ApplyFieldMergeWithLocks(
        MergedWorkMetadataFields current,
        MetadataWorkCandidate incoming,
        IReadOnlySet<string> lockedFields)
    {
        bool IsLocked(string field) => lockedFields.Contains(field);

        var normalizedIncoming = LatinTitleNormalizer.NormalizeCandidate(incoming);
        var mergedTitle = IsLocked("title") ? current.Title : normalizedIncoming.Title;
        var mergedOverview = IsLocked("overview")
            ? current.Overview
            : (!string.IsNullOrWhiteSpace(normalizedIncoming.Overview) ? normalizedIncoming.Overview : current.Overview);
        var mergedPoster = IsLocked("posterUrl")
            ? current.PosterUrl
            : (normalizedIncoming.PosterUrl ?? current.PosterUrl);
        var mergedYear = IsLocked("year") ? current.Year : (normalizedIncoming.Year ?? current.Year);
        var mergedStatus = IsLocked("status") ? current.Status : normalizedIncoming.Status;
        var mergedLang = IsLocked("originalLanguage") ? current.OriginalLanguage : normalizedIncoming.OriginalLanguage;

        var mergedAlts = IsLocked("altTitles")
            ? current.AltTitles
            : LatinTitleNormalizer.OrderAndCleanAltTitles(
                mergedTitle,
                current.AltTitles.Concat(normalizedIncoming.AltTitles));

        var mergedIds = new Dictionary<string, string>(current.ExternalIds, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in normalizedIncoming.ExternalIds)
        {
            mergedIds[k] = v;
        }

        return new MergedWorkMetadataFields(
            Title: mergedTitle,
            AltTitles: mergedAlts,
            OriginalLanguage: mergedLang,
            Year: mergedYear,
            Status: mergedStatus,
            Overview: mergedOverview,
            PosterUrl: mergedPoster,
            ExternalIds: mergedIds);
    }

    private IReadOnlyList<string> ResolveWaterfallOrder(
        string vertical,
        string? preferredProvider,
        IReadOnlyList<string>? customWaterfall)
    {
        var baseOrder = customWaterfall is { Count: > 0 }
            ? customWaterfall
            : GetDefaultWaterfall(vertical);

        var ordered = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredProvider) &&
            !string.Equals(preferredProvider, "auto", StringComparison.OrdinalIgnoreCase))
        {
            ordered.Add(preferredProvider.Trim().ToLowerInvariant());
        }

        foreach (var p in baseOrder)
        {
            var norm = p.Trim().ToLowerInvariant();
            if (!ordered.Contains(norm, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(norm);
            }
        }

        return ordered;
    }

    private Task<IReadOnlyList<MetadataWorkCandidate>> SearchSingleProviderAsync(
        string providerKey,
        string query,
        string vertical,
        CancellationToken ct)
    {
        if (_providers.TryGetValue(providerKey, out var builtIn))
        {
            return builtIn.SearchAsync(query, vertical, ct);
        }

        return _customWebhooks.SearchWebhookAsync(providerKey, query, vertical, ct);
    }

    private Task<MetadataWorkCandidate?> LookupSingleProviderAsync(
        string providerKey,
        string externalId,
        string vertical,
        CancellationToken ct)
    {
        if (_providers.TryGetValue(providerKey, out var builtIn))
        {
            return builtIn.GetByExternalIdAsync(externalId, vertical, ct);
        }

        return _customWebhooks.LookupWebhookAsync(providerKey, externalId, vertical, ct);
    }

    private static IReadOnlyList<MetadataWorkCandidate> DeduplicateAndMergeCandidates(
        IReadOnlyList<MetadataWorkCandidate> raw)
    {
        var merged = new List<MetadataWorkCandidate>();
        foreach (var item in raw)
        {
            var matchIdx = merged.FindIndex(existing =>
                (IsCompatibleVerticalMerge(existing.Vertical, item.Vertical) &&
                 existing.ExternalIds.Any(kv =>
                     item.ExternalIds.TryGetValue(kv.Key, out var otherVal) &&
                     string.Equals(kv.Value, otherVal, StringComparison.OrdinalIgnoreCase))) ||
                (IsEpisodicVideoVertical(existing.Vertical) &&
                 IsEpisodicVideoVertical(item.Vertical) &&
                 CollapseAlphanumeric(existing.Title).Length >= 4 &&
                 string.Equals(CollapseAlphanumeric(existing.Title), CollapseAlphanumeric(item.Title), StringComparison.Ordinal) &&
                 (existing.Year is null || item.Year is null || Math.Abs(existing.Year.Value - item.Year.Value) <= 1)));

            if (matchIdx < 0)
            {
                merged.Add(item);
                continue;
            }

            var cur = merged[matchIdx];
            var combinedIds = new Dictionary<string, string>(cur.ExternalIds, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in item.ExternalIds)
            {
                combinedIds.TryAdd(k, v);
            }

            var bestTitle = IsPlaceholderFallbackTitle(cur) && !IsPlaceholderFallbackTitle(item)
                ? item.Title
                : cur.Title;

            var combinedAlts = LatinTitleNormalizer.OrderAndCleanAltTitles(
                bestTitle,
                cur.AltTitles.Concat([cur.Title, item.Title]).Concat(item.AltTitles));

            var combinedGenres = (cur.Genres ?? [])
                .Concat(item.Genres ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var formatHint = string.Equals(cur.ClassificationKind, "anime-movie", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(item.ClassificationKind, "anime-movie", StringComparison.OrdinalIgnoreCase)
                ? "MOVIE"
                : null;

            merged[matchIdx] = MediaTaxonomyClassifier.EnrichCandidate(
                cur with
                {
                    Title = bestTitle,
                    AltTitles = combinedAlts,
                    Overview = string.IsNullOrWhiteSpace(cur.Overview) ? item.Overview : cur.Overview,
                    PosterUrl = cur.PosterUrl ?? item.PosterUrl,
                    Year = cur.Year ?? item.Year,
                    ExternalIds = combinedIds,
                    Genres = combinedGenres,
                    Popularity = Math.Max(cur.Popularity, item.Popularity),
                    Containers = MergeContainers(cur.Containers, item.Containers),
                },
                formatHint: formatHint);
        }

        return merged;
    }

    private static IReadOnlyList<MetadataContainerSeed> MergeContainers(
        IReadOnlyList<MetadataContainerSeed> primary,
        IReadOnlyList<MetadataContainerSeed> secondary)
    {
        if (primary.Count == 0)
        {
            return secondary;
        }

        if (secondary.Count == 0)
        {
            return primary;
        }

        var byNumber = primary.ToDictionary(c => c.ContainerNumber);
        var added = false;
        foreach (var sec in secondary)
        {
            if (!byNumber.ContainsKey(sec.ContainerNumber))
            {
                byNumber[sec.ContainerNumber] = sec;
                added = true;
            }
        }

        return added
            ? byNumber.Values.OrderBy(c => c.ContainerNumber).ToArray()
            : primary;
    }

    private static bool IsEpisodicVideoVertical(string vertical) =>
        string.Equals(vertical, MetadataVerticals.Tv, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(vertical, MetadataVerticals.Anime, StringComparison.OrdinalIgnoreCase);

    private static bool IsCompatibleVerticalMerge(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
        (IsEpisodicVideoVertical(a) && IsEpisodicVideoVertical(b));

    private static bool IsPlaceholderFallbackTitle(MetadataWorkCandidate c) =>
        c.Title.Contains($"({c.ProviderKey}:", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<MetadataWorkCandidate> RankAndFilterCandidates(
        IReadOnlyList<MetadataWorkCandidate> candidates,
        string query,
        int maxResults)
    {
        if (candidates.Count <= 1)
        {
            return candidates;
        }

        var hasRealTitledHits = candidates.Any(c => !IsPlaceholderFallbackTitle(c));
        var filtered = hasRealTitledHits
            ? candidates.Where(c => !IsPlaceholderFallbackTitle(c)).ToList()
            : candidates.ToList();

        return filtered
            .Select((cand, index) =>
            {
                var score = ScoreCandidateRelevance(cand, query);
                var tier = score >= 2000 ? 2 : score >= 200 ? 1 : 0;
                return (Candidate: cand, Tier: tier, Score: score, Index: index);
            })
            .OrderByDescending(x => x.Tier)
            .ThenByDescending(x => x.Candidate.Popularity)
            .ThenByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Take(maxResults)
            .Select(x => x.Candidate)
            .ToList();
    }

    private static int ScoreCandidateRelevance(MetadataWorkCandidate c, string rawQuery)
    {
        if (IsPlaceholderFallbackTitle(c))
        {
            return -500;
        }

        var q = rawQuery.Trim().ToLowerInvariant();
        if (q.Length == 0)
        {
            return 0;
        }

        // Direct external ID query match
        foreach (var (_, extVal) in c.ExternalIds)
        {
            if (string.Equals(extVal, q, StringComparison.OrdinalIgnoreCase) ||
                q.EndsWith($":{extVal.ToLowerInvariant()}", StringComparison.Ordinal))
            {
                return 2000;
            }
        }

        var qCollapsed = CollapseAlphanumeric(q);
        var qTokens = SplitWordTokens(q);

        var titleScore = ScoreSingleTextMatch(c.Title, q, qCollapsed, qTokens, isPrimaryTitle: true);
        var bestAltScore = c.AltTitles.Count == 0
            ? 0
            : c.AltTitles.Max(alt => ScoreSingleTextMatch(alt, q, qCollapsed, qTokens, isPrimaryTitle: false));

        var matchScore = Math.Max(titleScore, bestAltScore);
        if (titleScore > 0 && bestAltScore > 0)
        {
            matchScore += 60;
        }

        // Primary visual/literary works slightly outrank character-song singles when query relevance is equal
        var verticalBonus = c.Vertical.ToLowerInvariant() switch
        {
            MetadataVerticals.Movies or MetadataVerticals.Tv or MetadataVerticals.Anime => 60,
            MetadataVerticals.Ebooks or MetadataVerticals.Audiobooks or MetadataVerticals.Comics => 45,
            _ => 15,
        };

        var posterBonus = !string.IsNullOrWhiteSpace(c.PosterUrl) ? 30 : 0;
        var yearBonus = c.Year is >= 1900 ? 10 : 0;

        return matchScore + verticalBonus + posterBonus + yearBonus;
    }

    private static int ScoreSingleTextMatch(
        string? text,
        string qLower,
        string qCollapsed,
        string[] qTokens,
        bool isPrimaryTitle)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var lower = text.Trim().ToLowerInvariant();
        var collapsed = CollapseAlphanumeric(lower);
        var primaryDelta = isPrimaryTitle ? 40 : 0;

        if (string.Equals(lower, qLower, StringComparison.Ordinal) ||
            (qCollapsed.Length >= 2 && string.Equals(collapsed, qCollapsed, StringComparison.Ordinal)))
        {
            return 1000 + primaryDelta;
        }

        if (lower.StartsWith(qLower, StringComparison.Ordinal) ||
            (qCollapsed.Length >= 3 && collapsed.StartsWith(qCollapsed, StringComparison.Ordinal)))
        {
            return 720 + primaryDelta;
        }

        var titleTokens = SplitWordTokens(lower);
        if (qTokens.Length > 0 && qTokens.All(qt => titleTokens.Any(tt => string.Equals(tt, qt, StringComparison.Ordinal))))
        {
            return 560 + primaryDelta;
        }

        if (qTokens.Length > 0 && qTokens.All(qt => titleTokens.Any(tt => tt.StartsWith(qt, StringComparison.Ordinal))))
        {
            return 460 + primaryDelta;
        }

        if (qCollapsed.Length >= 3 && collapsed.Contains(qCollapsed, StringComparison.Ordinal))
        {
            return 340 + primaryDelta;
        }

        if (lower.Contains(qLower, StringComparison.Ordinal))
        {
            return 240 + primaryDelta;
        }

        return 0;
    }

    private static string CollapseAlphanumeric(string input)
    {
        var sb = new System.Text.StringBuilder(input.Length);
        foreach (var ch in input.Normalize(System.Text.NormalizationForm.FormKD))
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    private static string[] SplitWordTokens(string input) =>
        input
            .Split([' ', '\t', '-', '_', ':', '.', ',', '/', '!', '?', '\'', '"', '(', ')', '[', ']', '—', '–'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
