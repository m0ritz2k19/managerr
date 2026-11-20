using System.Net.Http.Headers;
using System.Text;
using Managerr.Common.Net;

namespace Managerr.Metadata;

public static class MetadataVerticals
{
    public const string Movies = "movies";
    public const string Tv = "tv";
    public const string Anime = "anime";
    public const string Music = "music";
    public const string Ebooks = "ebooks";
    public const string Audiobooks = "audiobooks";
    public const string Comics = "comics";

    public static readonly IReadOnlyList<string> All =
    [
        Movies, Tv, Anime, Music, Ebooks, Audiobooks, Comics,
    ];
}

public static class MetadataProviderConfigurationStates
{
    public const string Ready = "ready";
    public const string NotConfigured = "not_configured";
}

public sealed record MetadataProviderDescriptor(
    string Key,
    string DisplayName,
    bool IsBuiltInArrDefault,
    bool RequiresApiKey,
    IReadOnlyList<string> SupportedVerticals,
    string DefaultBaseUrl,
    string Description,
    string ConfigurationStatus = MetadataProviderConfigurationStates.Ready);

public sealed record MetadataProviderStatus(
    string Key,
    string DisplayName,
    bool IsBuiltInArrDefault,
    bool RequiresApiKey,
    string ConfigurationStatus,
    string EffectiveBaseUrl,
    IReadOnlyList<string> SupportedVerticals);

public sealed record MetadataUnitSeed(
    string UnitKind,
    int UnitNumber,
    int? AbsoluteNumber,
    string Title,
    string? AirOrReleaseDate = null);

public sealed record MetadataContainerSeed(
    string ContainerKind,
    int ContainerNumber,
    string Title,
    string? ReleaseDate,
    IReadOnlyList<MetadataUnitSeed> Units,
    IReadOnlyDictionary<string, string>? ExternalIds = null);

public sealed record MetadataWorkCandidate(
    string ProviderKey,
    string ProviderItemId,
    string Vertical,
    string Title,
    IReadOnlyList<string> AltTitles,
    string OriginalLanguage,
    int? Year,
    string Status,
    string Overview,
    string? PosterUrl,
    IReadOnlyDictionary<string, string> ExternalIds,
    IReadOnlyList<MetadataContainerSeed> Containers,
    IReadOnlyList<string>? Genres = null,
    string? ClassificationKind = null,
    IReadOnlyList<string>? SuggestedTags = null,
    double Popularity = 0);

public sealed record MediaClassificationOutcome(
    string EffectiveVertical,
    string ClassificationKind,
    IReadOnlyList<string> SuggestedTags);

public static class MediaTaxonomyClassifier
{
    public static readonly IReadOnlySet<string> AllManagedTaxonomyTags =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "anime",
            "anime-series",
            "anime-movie",
            "cartoon",
            "cartoon-series",
            "cartoon-movie",
            "live-action",
            "live-action-series",
            "live-action-movie",
            "animation",
            "tv",
            "movie",
            "donghua",
            "aeni",
        };

    private static readonly HashSet<string> EastAsianLangs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ja", "jp", "jpn", "japanese",
        "zh", "cn", "zho", "chi", "chinese",
        "ko", "kr", "kor", "korean",
    };

    private static readonly string[] KnownWesternCartoonTitleHints =
    [
        "avatar: the last airbender",
        "avatar: seven havens",
        "legend of korra",
        "rick and morty",
        "arcane",
        "invincible",
        "family guy",
        "the simpsons",
        "south park",
        "futurama",
        "archer",
        "bojack horseman",
        "bob's burgers",
        "adventure time",
        "gravity falls",
        "spongebob",
        "spider-man: into the spider-verse",
        "spider-man: across the spider-verse",
        "teenage mutant ninja turtles",
        "x-men '97",
        "blue eye samurai",
        "vox machina",
        "hazbin hotel",
        "helluva boss",
        "smiling friends",
        "star wars: the clone wars",
        "star wars: the bad batch",
        "batman: the animated series",
    ];

    public static MetadataWorkCandidate EnrichCandidate(
        MetadataWorkCandidate candidate,
        string? formatHint = null)
    {
        var outcome = Classify(
            verticalOrWorkKind: candidate.Vertical,
            originalLanguage: candidate.OriginalLanguage,
            externalIds: candidate.ExternalIds,
            genres: candidate.Genres,
            existingTags: candidate.SuggestedTags,
            rootFolderPath: null,
            formatHint: formatHint,
            providerKey: candidate.ProviderKey,
            title: candidate.Title,
            overview: candidate.Overview);

        return candidate with
        {
            ClassificationKind = outcome.ClassificationKind,
            SuggestedTags = outcome.SuggestedTags,
        };
    }

    public static MediaClassificationOutcome Classify(
        string vertical,
        string? originalLanguage,
        IReadOnlyList<string>? genres,
        IReadOnlyDictionary<string, string>? externalIds,
        string? title,
        IReadOnlyList<string>? altTitles = null,
        string? formatHint = null,
        string? overview = null,
        IReadOnlyList<string>? existingTags = null,
        string? rootFolderPath = null,
        string? providerKey = null) =>
        Classify(
            verticalOrWorkKind: vertical,
            originalLanguage: originalLanguage,
            externalIds: externalIds,
            genres: genres,
            existingTags: existingTags,
            rootFolderPath: rootFolderPath,
            formatHint: formatHint,
            providerKey: providerKey,
            title: altTitles is { Count: > 0 } ? $"{title} | {string.Join(" | ", altTitles)}" : title,
            overview: overview);

    public static MediaClassificationOutcome Classify(
        string? verticalOrWorkKind,
        string? originalLanguage,
        IReadOnlyDictionary<string, string>? externalIds = null,
        IReadOnlyList<string>? genres = null,
        IReadOnlyList<string>? existingTags = null,
        string? rootFolderPath = null,
        string? formatHint = null,
        string? providerKey = null,
        string? title = null,
        string? overview = null)
    {
        var normVert = (verticalOrWorkKind ?? "").Trim().ToLowerInvariant();
        if (normVert is MetadataVerticals.Music or MetadataVerticals.Ebooks or MetadataVerticals.Audiobooks or MetadataVerticals.Comics
            or "ebook" or "audiobook" or "book" or "comic")
        {
            var nonVideoKind = normVert switch
            {
                "ebook" or "book" => MetadataVerticals.Ebooks,
                "audiobook" => MetadataVerticals.Audiobooks,
                "comic" => MetadataVerticals.Comics,
                _ => normVert,
            };
            return new MediaClassificationOutcome(nonVideoKind, nonVideoKind, [nonVideoKind]);
        }

        var lang = (originalLanguage ?? "").Trim().ToLowerInvariant();
        var fmt = (formatHint ?? "").Trim().ToUpperInvariant();
        var rootLower = (rootFolderPath ?? "").Replace('\\', '/').ToLowerInvariant();
        var titleLower = (title ?? "").Trim().ToLowerInvariant();
        var overviewLower = (overview ?? "").Trim().ToLowerInvariant();

        var genreSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (genres is not null)
        {
            foreach (var g in genres)
            {
                if (!string.IsNullOrWhiteSpace(g))
                {
                    genreSet.Add(g.Trim());
                }
            }
        }

        var tagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existingTags is not null)
        {
            foreach (var t in existingTags)
            {
                if (!string.IsNullOrWhiteSpace(t))
                {
                    tagSet.Add(t.Trim());
                }
            }
        }

        var isMovie = normVert is "movies" or "movie"
                      || fmt == "MOVIE"
                      || rootLower.Contains("animemov", StringComparison.Ordinal)
                      || rootLower.Contains("animefilm", StringComparison.Ordinal)
                      || rootLower.Contains("/movies", StringComparison.Ordinal);

        var hasAnimeId = (providerKey is not null &&
                          (providerKey.Equals("anilist", StringComparison.OrdinalIgnoreCase) ||
                           providerKey.Equals("anidb", StringComparison.OrdinalIgnoreCase)))
                         || (externalIds is not null &&
                             (HasNonEmptyKey(externalIds, "anilist") ||
                              HasNonEmptyKey(externalIds, "anidb") ||
                              HasNonEmptyKey(externalIds, "mal") ||
                              HasNonEmptyKey(externalIds, "kitsu") ||
                              HasNonEmptyKey(externalIds, "xem")));

        var isKnownWesternCartoon = KnownWesternCartoonTitleHints.Any(h =>
            titleLower.Equals(h, StringComparison.OrdinalIgnoreCase) ||
            titleLower.StartsWith(h + " ", StringComparison.OrdinalIgnoreCase));

        var hasExplicitAnimeGenre = genreSet.Contains("anime") ||
                                    genreSet.Any(g => g.Contains("anime", StringComparison.OrdinalIgnoreCase));

        var hasAnimationSignal = hasExplicitAnimeGenre ||
                                 genreSet.Contains("animation") ||
                                 genreSet.Contains("animated") ||
                                 genreSet.Contains("cartoon") ||
                                 genreSet.Contains("16") ||
                                 genreSet.Any(g => g.Contains("animation", StringComparison.OrdinalIgnoreCase) ||
                                                   g.Contains("cartoon", StringComparison.OrdinalIgnoreCase)) ||
                                 isKnownWesternCartoon ||
                                 overviewLower.Contains("animated series", StringComparison.Ordinal) ||
                                 overviewLower.Contains("animated film", StringComparison.Ordinal) ||
                                 overviewLower.Contains("animated movie", StringComparison.Ordinal);

        var isEastAsianLang = EastAsianLangs.Contains(lang);

        var hasAnimeOverviewSignal = isEastAsianLang &&
                                     (overviewLower.Contains("anime", StringComparison.Ordinal) ||
                                      overviewLower.Contains("manga", StringComparison.Ordinal) ||
                                      overviewLower.Contains("light novel", StringComparison.Ordinal) ||
                                      overviewLower.Contains("shonen", StringComparison.Ordinal) ||
                                      overviewLower.Contains("high school", StringComparison.Ordinal));

        var isAnime = !isKnownWesternCartoon &&
                      (hasAnimeId ||
                       hasExplicitAnimeGenre ||
                       (hasAnimationSignal && isEastAsianLang) ||
                       hasAnimeOverviewSignal ||
                       rootLower.Contains("/anime", StringComparison.Ordinal) ||
                       (normVert == MetadataVerticals.Anime && (isEastAsianLang || string.IsNullOrEmpty(lang))));

        var isCartoon = !isAnime &&
                        (hasAnimationSignal ||
                         tagSet.Contains("cartoon") ||
                         tagSet.Contains("cartoon-series") ||
                         tagSet.Contains("cartoon-movie"));

        var tags = new List<string>();
        string classificationKind;
        string effectiveVertical;

        if (isMovie)
        {
            effectiveVertical = MetadataVerticals.Movies;
            if (isAnime)
            {
                classificationKind = "anime-movie";
                tags.AddRange(["anime", "anime-movie", "animation", "movie"]);
                if (lang is "zh" or "cn" or "zho" or "chi" or "chinese") tags.Add("donghua");
                else if (lang is "ko" or "kr" or "kor" or "korean") tags.Add("aeni");
            }
            else if (isCartoon)
            {
                classificationKind = "cartoon-movie";
                tags.AddRange(["cartoon", "cartoon-movie", "animation", "movie"]);
            }
            else
            {
                classificationKind = "live-action-movie";
                tags.AddRange(["movie", "live-action", "live-action-movie"]);
            }
        }
        else
        {
            if (isAnime)
            {
                effectiveVertical = MetadataVerticals.Anime;
                classificationKind = "anime-series";
                tags.AddRange(["anime", "anime-series", "animation"]);
                if (lang is "zh" or "cn" or "zho" or "chi" or "chinese") tags.Add("donghua");
                else if (lang is "ko" or "kr" or "kor" or "korean") tags.Add("aeni");
            }
            else if (isCartoon)
            {
                effectiveVertical = MetadataVerticals.Tv;
                classificationKind = "cartoon-series";
                tags.AddRange(["cartoon", "cartoon-series", "animation", "tv"]);
            }
            else
            {
                effectiveVertical = MetadataVerticals.Tv;
                classificationKind = "live-action-series";
                tags.AddRange(["tv", "live-action", "live-action-series"]);
            }
        }

        return new MediaClassificationOutcome(
            EffectiveVertical: effectiveVertical,
            ClassificationKind: classificationKind,
            SuggestedTags: tags);
    }

    public static IReadOnlyList<string> MergeWithUserTags(
        IReadOnlyList<string>? currentTags,
        IReadOnlyList<string> suggestedTaxonomyTags)
    {
        var preservedUserTags = (currentTags ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t) && !AllManagedTaxonomyTags.Contains(t.Trim()))
            .Select(t => t.Trim());

        return suggestedTaxonomyTags
            .Concat(preservedUserTags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool HasNonEmptyKey(IReadOnlyDictionary<string, string> dict, string key) =>
        dict.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) && v != "0";
}

public interface IMetadataProvider
{
    MetadataProviderDescriptor Descriptor { get; }
    Task<MetadataProviderStatus> GetStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(new MetadataProviderStatus(
            Descriptor.Key,
            Descriptor.DisplayName,
            Descriptor.IsBuiltInArrDefault,
            Descriptor.RequiresApiKey,
            Descriptor.ConfigurationStatus,
            Descriptor.DefaultBaseUrl,
            Descriptor.SupportedVerticals));
    Task<IReadOnlyList<MetadataWorkCandidate>> SearchAsync(string query, string vertical, CancellationToken ct = default);
    Task<MetadataWorkCandidate?> GetByExternalIdAsync(string externalId, string vertical, CancellationToken ct = default);
}

public sealed record MetadataHttpRequest(
    string Url,
    HttpMethod Method,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? JsonBody = null,
    bool AllowPrivateLan = false);

public interface IMetadataHttpGateway
{
    Task<string?> SendAsync(MetadataHttpRequest request, CancellationToken ct = default);
}

public sealed class MetadataHttpGateway(ISafeHttpClientFactory httpFactory) : IMetadataHttpGateway
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HostThrottleState> HostThrottles =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string?>> _inFlight =
        new(StringComparer.Ordinal);

    public async Task<string?> SendAsync(MetadataHttpRequest request, CancellationToken ct = default)
    {
        var coalesceKey = $"{request.Method.Method}:{request.Url}:{request.JsonBody ?? ""}";
        Task<string?> sharedTask;
        lock (_inFlight)
        {
            if (!_inFlight.TryGetValue(coalesceKey, out var existing))
            {
                existing = ExecuteThrottledAsync(request, coalesceKey);
                _inFlight[coalesceKey] = existing;
            }

            sharedTask = existing;
        }

        return await sharedTask.WaitAsync(ct);
    }

    private async Task<string?> ExecuteThrottledAsync(MetadataHttpRequest request, string coalesceKey)
    {
        try
        {
            if (!request.AllowPrivateLan && Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
            {
                var minInterval = ResolveMinIntervalForHost(uri.Host);
                if (minInterval > TimeSpan.Zero)
                {
                    var state = HostThrottles.GetOrAdd(uri.Host, _ => new HostThrottleState());
                    await state.Gate.WaitAsync();
                    try
                    {
                        var elapsed = DateTimeOffset.UtcNow - state.LastRequestAt;
                        if (elapsed < minInterval)
                        {
                            await Task.Delay(minInterval - elapsed);
                        }

                        state.LastRequestAt = DateTimeOffset.UtcNow;
                    }
                    finally
                    {
                        state.Gate.Release();
                    }
                }
            }

            using var client = request.AllowPrivateLan
                ? httpFactory.CreateHomelabClient(allowPrivateLan: true)
                : httpFactory.CreatePublicClient();

            using var msg = new HttpRequestMessage(request.Method, request.Url);
            msg.Headers.UserAgent.Add(new ProductInfoHeaderValue("Managerr", "1.0.0"));
            msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.9));
            msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml", 0.9));

            if (request.Headers is not null)
            {
                foreach (var (k, v) in request.Headers)
                {
                    msg.Headers.TryAddWithoutValidation(k, v);
                }
            }

            if (request.JsonBody is not null)
            {
                msg.Content = new StringContent(request.JsonBody, Encoding.UTF8, "application/json");
            }

            using var resp = await client.SendAsync(msg);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            return await resp.Content.ReadAsStringAsync();
        }
        finally
        {
            lock (_inFlight)
            {
                _inFlight.TryRemove(coalesceKey, out _);
            }
        }
    }

    public static TimeSpan ResolveMinIntervalForHost(string host)
    {
        if (host.Contains("anidb.net", StringComparison.OrdinalIgnoreCase))
        {
            // AniDB HTTP API enforces >= 2 sec between requests to prevent client bans
            return TimeSpan.FromMilliseconds(2100);
        }

        if (host.Contains("musicbrainz.org", StringComparison.OrdinalIgnoreCase))
        {
            // MusicBrainz enforces a strict 1 req/sec policy per User-Agent
            return TimeSpan.FromMilliseconds(1100);
        }

        if (host.Contains("discogs.com", StringComparison.OrdinalIgnoreCase))
        {
            // Discogs allows 60 req/min authenticated
            return TimeSpan.FromMilliseconds(1000);
        }

        return TimeSpan.FromMilliseconds(150);
    }

    private sealed class HostThrottleState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset LastRequestAt { get; set; } = DateTimeOffset.MinValue;
    }
}
