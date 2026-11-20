using System.Text.RegularExpressions;
using Managerr.Catalog;
using Managerr.Downloaders.Parser;
using Managerr.Indexers;
using Managerr.Indexers.Torznab;
using Managerr.Security.Users;
using Microsoft.Extensions.Logging;

namespace Managerr.Migration;

public sealed record ScopedReleaseCandidate(
    IndexedReleaseCandidate Release,
    ParsedReleaseInfo Parsed,
    int? EffectiveSeason,
    IReadOnlyList<int> EffectiveEpisodes,
    bool IsSeasonPack,
    bool IdentityVerified);

public sealed record OrchestratedReleaseSearchRequest(
    IndexerSearchQuery Query,
    MediaWorkDetailTree? WorkTree = null,
    long? ContainerId = null,
    long? UnitId = null,
    int? AbsoluteEpisodeNumber = null,
    bool IncludeSeasonPacks = true,
    PlexUserSummary? Actor = null);

public interface IReleaseSearchOrchestrator
{
    Task<IReadOnlyList<ScopedReleaseCandidate>> SearchScopedAsync(
        OrchestratedReleaseSearchRequest request,
        CancellationToken ct = default);
}

public sealed partial class ReleaseSearchOrchestrator(
    IUnifiedIndexerService indexerService,
    IReleaseParserRouter parserRouter,
    IPolymorphicCatalogService catalogService,
    ILogger<ReleaseSearchOrchestrator>? logger = null) : IReleaseSearchOrchestrator
{
    private static readonly Regex SequelSuffixPattern = new(
        @"^(?<base>.*[^\s._-])[\s._-]+(?:Season\s*(?<snum>[2-9])|S0?(?<snum>[2-9])|(?<snum>[2-9])(?:nd|rd|th)\s+Season|(?<snum>[2-9])|(?<roman>II|III|IV|V|VI|VII|VIII|IX))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex SpecialOrOvaRegex = new(
        @"(?<![A-Za-z0-9])(?:OVA|OAV|OAD|Bonus[\s._-]*Stage|NCOP\d*|NCED\d*)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex RegularSeasonEpRegex = new(
        @"\bS0?[1-9]\d*E\d+\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    public static string SanitizeSearchTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var s = title
            .Replace('–', ' ')
            .Replace('—', ' ')
            .Replace('’', '\'')
            .Replace('‘', '\'')
            .Replace('`', '\'')
            .Replace(":", "")
            .Replace("!", "")
            .Replace("?", "")
            .Replace("\"", "");

        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    public static IReadOnlyList<string> BuildQueryStems(string? primaryTitle, IEnumerable<string>? altTitles, bool isEpisodic)
    {
        var stems = new List<string>();
        var seenClean = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const int maxStems = 4;

        void TryAdd(string? stem)
        {
            if (string.IsNullOrWhiteSpace(stem)) return;
            var trimmed = stem.Trim();
            if (trimmed.Length == 0) return;
            var cleanKey = ReleaseTitleNormalizer.CleanSeriesTitle(trimmed);
            var dedupKey = cleanKey.Length > 0 ? cleanKey : trimmed;
            if (seenClean.Add(dedupKey))
            {
                stems.Add(trimmed);
            }
        }

        if (!string.IsNullOrWhiteSpace(primaryTitle))
        {
            // 1. If isEpisodic and primaryTitle contains " – " or " — " or ": " with prefix >= 4 chars
            if (isEpisodic)
            {
                int dashIdx = -1;
                string[] delims = [" – ", " — ", ": "];
                foreach (var d in delims)
                {
                    var idx = primaryTitle.IndexOf(d, StringComparison.Ordinal);
                    if (idx >= 4 && (dashIdx < 0 || idx < dashIdx))
                    {
                        dashIdx = idx;
                    }
                }

                if (dashIdx >= 4)
                {
                    var prefix = primaryTitle[..dashIdx];
                    var sanitizedPrefix = SanitizeSearchTitle(prefix);
                    if (sanitizedPrefix.Length >= 3)
                    {
                        TryAdd(sanitizedPrefix);
                    }
                }
            }

            // 2. SanitizeSearchTitle(primaryTitle)
            var sanitized = SanitizeSearchTitle(primaryTitle);
            if (sanitized.Length > 0)
            {
                TryAdd(sanitized);
            }
        }

        // 3. Distinct Latin-script AltTitles (contains at least 2 ASCII letters [A-Za-z],
        // skipping pure CJK/Cyrillic strings for Newznab q stems unless primary itself is non-Latin),
        // preserving priority order (Tier-1 aliases first) across the top 8 candidates.
        if (altTitles is not null)
        {
            var primaryHasLatin = !string.IsNullOrWhiteSpace(primaryTitle) && primaryTitle.Count(char.IsAsciiLetter) >= 2;
            var latinAlts = altTitles
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => (Raw: a.Trim(), Sanitized: SanitizeSearchTitle(a)))
                .Where(a => a.Sanitized.Length > 0)
                .Where(a => !primaryHasLatin || a.Sanitized.Count(char.IsAsciiLetter) >= 2)
                .Take(8)
                .ToList();

            foreach (var alt in latinAlts)
            {
                TryAdd(alt.Sanitized);
                if (stems.Count >= maxStems)
                {
                    break;
                }
            }
        }

        // 4. Raw primaryTitle.Trim() (if distinct from sanitized and space permits)
        if (!string.IsNullOrWhiteSpace(primaryTitle) && stems.Count < maxStems)
        {
            var raw = primaryTitle.Trim();
            if (raw.Length > 0 && !stems.Any(s => string.Equals(s, raw, StringComparison.OrdinalIgnoreCase)))
            {
                stems.Add(raw);
            }
        }

        return stems.Take(maxStems).ToList();
    }

    public static bool TryInferSequelSeason(ParsedReleaseInfo parsed, IReadOnlyList<string> knownTitles, out int inferredSeason)
    {
        inferredSeason = 0;
        if (parsed.SeasonNumber.HasValue ||
            string.IsNullOrWhiteSpace(parsed.CleanTitle) ||
            parsed.CleanTitle.Length > 512 ||
            knownTitles.Count == 0)
        {
            return false;
        }

        try
        {
            var match = SequelSuffixPattern.Match(parsed.CleanTitle);
            if (!match.Success)
            {
                return false;
            }

            int season = match.Groups["snum"].Success
                ? int.Parse(match.Groups["snum"].Value)
                : ParseRomanNumeral(match.Groups["roman"].Value);

            if (season < 2 || season > 9)
            {
                return false;
            }

            var basePart = match.Groups["base"].Value.Trim();
            if (Regex.IsMatch(basePart, @"(?i)[\s._-]+Season$"))
            {
                basePart = Regex.Replace(basePart, @"(?i)[\s._-]+Season$", "").Trim();
            }
            if (basePart.Length == 0)
            {
                return false;
            }

            foreach (var known in knownTitles)
            {
                if (string.IsNullOrWhiteSpace(known)) continue;

                var km = SequelSuffixPattern.Match(known.Trim());
                if (km.Success)
                {
                    var ks = km.Groups["snum"].Success
                        ? int.Parse(km.Groups["snum"].Value)
                        : ParseRomanNumeral(km.Groups["roman"].Value);
                    if (ks == season)
                    {
                        continue;
                    }
                }

                if (ReleaseTitleNormalizer.TitlesMatch(basePart, known))
                {
                    inferredSeason = season;
                    return true;
                }
            }

            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public static bool IsSpecialOrOvaTitle(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle)) return false;
        try
        {
            return SpecialOrOvaRegex.IsMatch(rawTitle) && !RegularSeasonEpRegex.IsMatch(rawTitle);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public static IReadOnlyList<string> MergeLanguages(
        IndexedReleaseCandidate candidate,
        ParsedReleaseInfo parsed,
        string? originalLanguage)
    {
        var langs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in parsed.DetectedLanguages)
        {
            if (!string.IsNullOrWhiteSpace(l))
            {
                langs.Add(l.Trim().ToLowerInvariant());
            }
        }

        if (parsed.IsDualAudio && !string.IsNullOrWhiteSpace(originalLanguage))
        {
            langs.Add(originalLanguage.Trim().ToLowerInvariant());
        }

        if (candidate.IndexerLanguages is not null)
        {
            foreach (var l in candidate.IndexerLanguages)
            {
                if (!string.IsNullOrWhiteSpace(l))
                {
                    langs.Add(TorznabFeedParser.NormalizeLanguageCode(l) ?? l.Trim().ToLowerInvariant());
                }
            }
        }

        return langs.ToList();
    }

    public static IReadOnlyList<string> MergeSubtitles(
        IndexedReleaseCandidate candidate,
        ParsedReleaseInfo parsed)
    {
        var subs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (parsed.IsMultiSubs)
        {
            subs.Add("multi");
        }

        if (candidate.IndexerSubtitles is not null)
        {
            foreach (var s in candidate.IndexerSubtitles)
            {
                if (!string.IsNullOrWhiteSpace(s))
                {
                    subs.Add(TorznabFeedParser.NormalizeLanguageCode(s) ?? s.Trim().ToLowerInvariant());
                }
            }
        }

        return subs.ToList();
    }

    public static bool EvaluateCandidateScope(
        IndexedReleaseCandidate candidate,
        ParsedReleaseInfo parsed,
        string? workTitle,
        IReadOnlyList<string> knownTitles,
        int? workYear,
        string? workOriginalLanguage,
        bool isEpisodic,
        bool isAnime,
        bool isMovie,
        string? workImdbId,
        int? workTmdbId,
        int? workTvdbId,
        int? workTvMazeId,
        int? requestedSeason,
        int? requestedEpisode,
        int? absoluteEpisodeNumber,
        bool includeSeasonPacks,
        out ScopedReleaseCandidate? scoped)
    {
        scoped = null;

        int? effectiveSeason;
        IReadOnlyList<int> effectiveEpisodes;
        bool sequelTitleMatched;

        if (!parsed.SeasonNumber.HasValue && TryInferSequelSeason(parsed, knownTitles, out var seqSeason))
        {
            effectiveSeason = seqSeason;
            effectiveEpisodes = parsed.EpisodeNumbers.Count > 0 ? parsed.EpisodeNumbers : parsed.AbsoluteEpisodeNumbers;
            sequelTitleMatched = true;
        }
        else
        {
            effectiveSeason = parsed.SeasonNumber;
            effectiveEpisodes = parsed.EpisodeNumbers;
            sequelTitleMatched = false;
        }

        bool isSeasonPack = parsed.IsFullSeasonPack && effectiveSeason.HasValue;

        // Work Identity Check
        bool hasWorkContext = !string.IsNullOrWhiteSpace(workTitle) ||
                              knownTitles.Count > 0 ||
                              !string.IsNullOrWhiteSpace(workImdbId) ||
                              workTmdbId.HasValue ||
                              workTvdbId.HasValue ||
                              workTvMazeId.HasValue;

        bool identityVerified = false;

        if (hasWorkContext)
        {
            bool directExternalIdMatch =
                (workTvdbId.HasValue && candidate.TvdbId.HasValue && candidate.TvdbId.Value == workTvdbId.Value) ||
                (!string.IsNullOrWhiteSpace(workImdbId) && !string.IsNullOrWhiteSpace(candidate.ImdbId) && string.Equals(candidate.ImdbId, workImdbId, StringComparison.OrdinalIgnoreCase)) ||
                (workTmdbId.HasValue && candidate.TmdbId.HasValue && candidate.TmdbId.Value == workTmdbId.Value) ||
                (workTvMazeId.HasValue && candidate.TvMazeId.HasValue && candidate.TvMazeId.Value == workTvMazeId.Value);

            bool titleMatch = sequelTitleMatched ||
                              (!string.IsNullOrWhiteSpace(workTitle) && ReleaseTitleNormalizer.MatchesWork(parsed.CleanTitle, workTitle, knownTitles, workYear));

            bool idContradicted =
                (workTvdbId.HasValue && candidate.TvdbId.HasValue && candidate.TvdbId.Value != workTvdbId.Value) ||
                (!string.IsNullOrWhiteSpace(workImdbId) && !string.IsNullOrWhiteSpace(candidate.ImdbId) && !string.Equals(candidate.ImdbId, workImdbId, StringComparison.OrdinalIgnoreCase)) ||
                (workTmdbId.HasValue && candidate.TmdbId.HasValue && candidate.TmdbId.Value != workTmdbId.Value) ||
                (workTvMazeId.HasValue && candidate.TvMazeId.HasValue && candidate.TvMazeId.Value != workTvMazeId.Value);

            if (idContradicted)
            {
                return false;
            }

            if (isMovie)
            {
                if (candidate.MatchedByExternalId)
                {
                    if (workYear.HasValue && parsed.Year.HasValue && Math.Abs(parsed.Year.Value - workYear.Value) > 1)
                    {
                        return false;
                    }
                    identityVerified = true;
                }
                else
                {
                    if (!titleMatch)
                    {
                        return false;
                    }

                    if (workYear.HasValue && parsed.Year.HasValue && Math.Abs(parsed.Year.Value - workYear.Value) > 1)
                    {
                        return false;
                    }

                    if (parsed.SeasonNumber.HasValue || parsed.EpisodeNumbers.Count > 0)
                    {
                        return false;
                    }

                    identityVerified = true;
                }
            }
            else
            {
                if (!directExternalIdMatch && !candidate.MatchedByExternalId && !titleMatch)
                {
                    return false;
                }

                identityVerified = directExternalIdMatch || candidate.MatchedByExternalId || titleMatch;
            }
        }

        // Season / Episode Scope Check
        if (requestedSeason.HasValue)
        {
            if (requestedSeason.Value > 0 && IsSpecialOrOvaTitle(candidate.Title))
            {
                return false;
            }

            if (effectiveSeason.HasValue)
            {
                if (effectiveSeason.Value != requestedSeason.Value)
                {
                    return false;
                }

                if (requestedEpisode.HasValue)
                {
                    if (isSeasonPack)
                    {
                        if (!includeSeasonPacks)
                        {
                            return false;
                        }
                    }
                    else if (!effectiveEpisodes.Contains(requestedEpisode.Value))
                    {
                        return false;
                    }
                }
                else
                {
                    if (!isSeasonPack && effectiveEpisodes.Count == 0 && effectiveSeason.Value != requestedSeason.Value)
                    {
                        return false;
                    }
                }
            }
            else
            {
                // effectiveSeason is null (no Sxx token and no sequel season in title)
                if (absoluteEpisodeNumber.HasValue && parsed.AbsoluteEpisodeNumbers.Contains(absoluteEpisodeNumber.Value))
                {
                    if (requestedSeason.Value >= 2 && candidate.IndexerSeason.HasValue && candidate.IndexerSeason.Value != requestedSeason.Value && candidate.IndexerSeason.Value != 1)
                    {
                        return false;
                    }

                    effectiveEpisodes = [requestedEpisode ?? absoluteEpisodeNumber.Value];
                }
                else if (requestedSeason.Value == 1)
                {
                    if (requestedEpisode.HasValue)
                    {
                        var hasEp = parsed.EpisodeNumbers.Contains(requestedEpisode.Value) || parsed.AbsoluteEpisodeNumbers.Contains(requestedEpisode.Value);
                        var validIndexerSeason = candidate.IndexerSeason is null or 1;
                        if (!hasEp || !validIndexerSeason)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        if (candidate.IndexerSeason != 1)
                        {
                            return false;
                        }
                    }
                }
                else
                {
                    // requestedSeason >= 2 with NO season number, NO sequel title, and NO matching absoluteEpisodeNumber -> REJECT!
                    return false;
                }
            }
        }
        else if (requestedEpisode.HasValue)
        {
            if (isSeasonPack)
            {
                if (!includeSeasonPacks)
                {
                    return false;
                }
            }
            else if (effectiveEpisodes.Count > 0 && !effectiveEpisodes.Contains(requestedEpisode.Value))
            {
                return false;
            }
        }

        scoped = new ScopedReleaseCandidate(
            Release: candidate,
            Parsed: parsed,
            EffectiveSeason: effectiveSeason,
            EffectiveEpisodes: effectiveEpisodes,
            IsSeasonPack: isSeasonPack,
            IdentityVerified: identityVerified);

        return true;
    }

    public async Task<IReadOnlyList<ScopedReleaseCandidate>> SearchScopedAsync(
        OrchestratedReleaseSearchRequest request,
        CancellationToken ct = default)
    {
        _ = logger;
        var tree = request.WorkTree;
        if (tree is null && request.Query.WorkId.HasValue)
        {
            tree = await catalogService.GetWorkTreeAsync(request.Query.WorkId.Value, request.Actor, ct);
        }

        var w = tree?.Work;
        var workTitle = w?.Title ?? request.Query.Title ?? request.Query.Query;
        var altTitles = w?.AltTitles ?? request.Query.AltTitles;
        var workYear = w?.Year ?? request.Query.Year;
        var workOriginalLanguage = w?.OriginalLanguage;
        var workKind = w?.WorkKind ?? request.Query.MediaVertical ?? "tv";

        string? imdbId = null;
        int? tmdbId = null;
        int? tvdbId = null;
        int? tvMazeId = null;

        if (w?.ExternalIds is not null)
        {
            if (w.ExternalIds.TryGetValue("imdb", out var sImdb) && !string.IsNullOrWhiteSpace(sImdb))
                imdbId = sImdb;
            if (w.ExternalIds.TryGetValue("tmdb", out var sTmdb) && int.TryParse(sTmdb, out var nTmdb))
                tmdbId = nTmdb;
            if (w.ExternalIds.TryGetValue("tvdb", out var sTvdb) && int.TryParse(sTvdb, out var nTvdb))
                tvdbId = nTvdb;
            if (w.ExternalIds.TryGetValue("tvmaze", out var sTvMaze) && int.TryParse(sTvMaze, out var nTvMaze))
                tvMazeId = nTvMaze;
        }

        imdbId ??= request.Query.ImdbId;
        tmdbId ??= request.Query.TmdbId;
        tvdbId ??= request.Query.TvdbId;
        tvMazeId ??= request.Query.TvMazeId;

        MediaContainerDetail? targetContainer = null;
        MediaUnitDetail? targetUnit = null;

        if (tree is not null)
        {
            var targetUnitId = request.UnitId ?? request.Query.UnitId;
            if (targetUnitId.HasValue)
            {
                foreach (var c in tree.Containers)
                {
                    var u = c.Units.FirstOrDefault(x => x.Id == targetUnitId.Value);
                    if (u is not null)
                    {
                        targetContainer = c;
                        targetUnit = u;
                        break;
                    }
                }
            }
            else if (request.ContainerId.HasValue)
            {
                targetContainer = tree.Containers.FirstOrDefault(c => c.Id == request.ContainerId.Value);
            }
        }

        bool isEpisodic = string.Equals(workKind, "tv", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(workKind, "anime", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(workKind, "series", StringComparison.OrdinalIgnoreCase) ||
                          (targetUnit is not null && string.Equals(targetUnit.UnitKind, "episode", StringComparison.OrdinalIgnoreCase)) ||
                          request.Query.Season.HasValue;

        int? requestedSeason = request.Query.Season ?? (isEpisodic ? targetContainer?.ContainerNumber : null);
        int? requestedEpisode = request.Query.Episode ?? (isEpisodic ? targetUnit?.UnitNumber : null);
        int? absoluteEpisodeNumber = request.AbsoluteEpisodeNumber ?? (isEpisodic ? targetUnit?.AbsoluteNumber : null);

        bool isAnime = string.Equals(workKind, "anime", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(request.Query.MediaVertical, "anime", StringComparison.OrdinalIgnoreCase);

        bool isMovie = string.Equals(workKind, "movies", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(request.Query.MediaVertical, "movies", StringComparison.OrdinalIgnoreCase);

        bool hasWorkContext = tree is not null;
        bool hasExternalIds = !string.IsNullOrWhiteSpace(imdbId) || tmdbId.HasValue || tvdbId.HasValue || tvMazeId.HasValue;

        if (!hasWorkContext && !hasExternalIds && !requestedSeason.HasValue)
        {
            var rawResults = await indexerService.SearchAsync(request.Query, ct);
            var directResults = new List<ScopedReleaseCandidate>(rawResults.Count);
            foreach (var r in rawResults)
            {
                var parsed = parserRouter.Parse(r.Title, r.MediaVertical ?? workKind);
                var mergedLangs = MergeLanguages(r, parsed, workOriginalLanguage);
                var enrichedParsed = parsed with { DetectedLanguages = mergedLangs };
                directResults.Add(new ScopedReleaseCandidate(
                    Release: r,
                    Parsed: enrichedParsed,
                    EffectiveSeason: parsed.SeasonNumber,
                    EffectiveEpisodes: parsed.EpisodeNumbers,
                    IsSeasonPack: parsed.IsFullSeasonPack,
                    IdentityVerified: false));
            }
            return directResults;
        }

        var candidateMap = new Dictionary<string, IndexedReleaseCandidate>(StringComparer.OrdinalIgnoreCase);

        void AddBatch(IEnumerable<IndexedReleaseCandidate>? batch)
        {
            if (batch is null) return;
            foreach (var item in batch)
            {
                var key = !string.IsNullOrWhiteSpace(item.Guid) ? item.Guid : item.DownloadUrl;
                candidateMap.TryAdd(key, item);
            }
        }

        var knownTitles = new List<string>();
        if (!string.IsNullOrWhiteSpace(workTitle))
        {
            knownTitles.Add(workTitle);
        }
        if (altTitles is not null)
        {
            foreach (var alt in altTitles)
            {
                if (!string.IsNullOrWhiteSpace(alt) && !knownTitles.Contains(alt, StringComparer.OrdinalIgnoreCase))
                {
                    knownTitles.Add(alt);
                }
            }
        }
        if (isEpisodic && !string.IsNullOrWhiteSpace(workTitle))
        {
            int dashIdx = -1;
            string[] delims = [" – ", " — "];
            foreach (var delim in delims)
            {
                var idx = workTitle.IndexOf(delim, StringComparison.Ordinal);
                if (idx >= 4 && (dashIdx < 0 || idx < dashIdx))
                {
                    dashIdx = idx;
                }
            }
            if (dashIdx >= 4)
            {
                var pfx = workTitle[..dashIdx].Trim();
                if (pfx.Length >= 4 && !knownTitles.Contains(pfx, StringComparer.OrdinalIgnoreCase))
                {
                    knownTitles.Add(pfx);
                }
            }
        }

        int CountScopedMatches()
        {
            int count = 0;
            foreach (var c in candidateMap.Values)
            {
                var p = parserRouter.Parse(c.Title, c.MediaVertical ?? workKind);
                if (EvaluateCandidateScope(
                    c, p, workTitle, knownTitles, workYear, workOriginalLanguage,
                    isEpisodic, isAnime, isMovie, imdbId, tmdbId, tvdbId, tvMazeId,
                    requestedSeason, requestedEpisode, absoluteEpisodeNumber, request.IncludeSeasonPacks,
                    out _))
                {
                    count++;
                }
            }
            return count;
        }

        // Wave 1 (External ID Search)
        if (hasExternalIds)
        {
            var sanitizedTitle = SanitizeSearchTitle(workTitle ?? request.Query.Query);
            var wave1Query = request.Query with
            {
                Query = sanitizedTitle,
                MediaVertical = request.Query.MediaVertical ?? workKind,
                ImdbId = imdbId,
                TmdbId = tmdbId,
                TvdbId = tvdbId,
                TvMazeId = tvMazeId,
                Season = requestedSeason,
                Episode = requestedEpisode
            };
            var wave1Results = await indexerService.SearchAsync(wave1Query, ct);
            AddBatch(wave1Results);

            if (isEpisodic && request.Query.Interactive && requestedSeason.HasValue && requestedEpisode.HasValue && request.IncludeSeasonPacks)
            {
                var packQuery = wave1Query with { Episode = null };
                var packResults = await indexerService.SearchAsync(packQuery, ct);
                AddBatch(packResults);
            }

            if (isMovie && !string.IsNullOrWhiteSpace(imdbId) && tmdbId.HasValue && CountScopedMatches() == 0)
            {
                var tmdbOnlyQuery = wave1Query with { ImdbId = null, TmdbId = tmdbId };
                var tmdbResults = await indexerService.SearchAsync(tmdbOnlyQuery, ct);
                AddBatch(tmdbResults);
            }
        }

        // Wave 2
        var rawStems = BuildQueryStems(workTitle ?? request.Query.Query, altTitles ?? request.Query.AltTitles, isEpisodic);
        var stems = !request.Query.Interactive
            ? rawStems.Take(2).ToList()
            : (rawStems.Count > 3 ? rawStems.Take(3).ToList() : rawStems);
        var scopedWave1 = CountScopedMatches();
        bool shouldRunWave2 = (scopedWave1 == 0 || !hasExternalIds) ||
                              (isAnime && requestedSeason.GetValueOrDefault() >= 2 && request.Query.Interactive);

        if (shouldRunWave2)
        {
            if (scopedWave1 == 0 || !hasExternalIds)
            {
                if (isEpisodic && requestedSeason.HasValue)
                {
                    foreach (var stem in stems)
                    {
                        var stemQuery = request.Query with
                        {
                            Query = stem,
                            MediaVertical = request.Query.MediaVertical ?? workKind,
                            Season = requestedSeason,
                            Episode = requestedEpisode,
                            ImdbId = null,
                            TmdbId = null,
                            TvdbId = null,
                            TvMazeId = null,
                            Year = null
                        };
                        var batch = await indexerService.SearchAsync(stemQuery, ct);
                        AddBatch(batch);

                        if (CountScopedMatches() > 0 && !(isAnime && requestedSeason.Value >= 2))
                        {
                            break;
                        }
                    }

                    if (isAnime && requestedSeason.Value >= 2)
                    {
                        var beforeAnimeCount = CountScopedMatches();
                        foreach (var stem in stems)
                        {
                            var animeSeqQuery = request.Query with
                            {
                                Query = $"{stem} {requestedSeason.Value}",
                                MediaVertical = request.Query.MediaVertical ?? workKind,
                                Season = requestedSeason,
                                Episode = requestedEpisode,
                                ImdbId = null,
                                TmdbId = null,
                                TvdbId = null,
                                TvMazeId = null,
                                Year = null
                            };
                            var batch = await indexerService.SearchAsync(animeSeqQuery, ct);
                            AddBatch(batch);

                            if (!request.Query.Interactive)
                            {
                                if (CountScopedMatches() > 0)
                                {
                                    break;
                                }
                            }
                            else if (CountScopedMatches() > beforeAnimeCount)
                            {
                                break;
                            }
                        }
                    }

                    // Wave 3 plain t=search (Season = null, Episode = null, NO IDs)
                    if (CountScopedMatches() == 0)
                    {
                        foreach (var stem in stems)
                        {
                            var seToken = requestedEpisode.HasValue
                                ? $"S{requestedSeason.Value:D2}E{requestedEpisode.Value:D2}"
                                : $"S{requestedSeason.Value:D2}";
                            var wave3Query = request.Query with
                            {
                                Query = $"{stem} {seToken}",
                                MediaVertical = request.Query.MediaVertical ?? workKind,
                                Season = null,
                                Episode = null,
                                ImdbId = null,
                                TmdbId = null,
                                TvdbId = null,
                                TvMazeId = null,
                                Year = null
                            };
                            var batch = await indexerService.SearchAsync(wave3Query, ct);
                            AddBatch(batch);

                            if (isAnime && absoluteEpisodeNumber.HasValue)
                            {
                                var absQuery = request.Query with
                                {
                                    Query = $"{stem} {absoluteEpisodeNumber.Value:D2}",
                                    MediaVertical = request.Query.MediaVertical ?? workKind,
                                    Season = null,
                                    Episode = null,
                                    ImdbId = null,
                                    TmdbId = null,
                                    TvdbId = null,
                                    TvMazeId = null,
                                    Year = null
                                };
                                var absBatch = await indexerService.SearchAsync(absQuery, ct);
                                AddBatch(absBatch);
                            }

                            if (CountScopedMatches() > 0)
                            {
                                break;
                            }
                        }
                    }
                }
                else
                {
                    // Movies / non-episodic
                    foreach (var stem in stems)
                    {
                        var stemQuery = request.Query with
                        {
                            Query = stem,
                            MediaVertical = request.Query.MediaVertical ?? workKind,
                            Year = workYear,
                            ImdbId = null,
                            TmdbId = null,
                            TvdbId = null,
                            TvMazeId = null,
                            Season = null,
                            Episode = null
                        };
                        var batch = await indexerService.SearchAsync(stemQuery, ct);
                        AddBatch(batch);

                        if (CountScopedMatches() == 0 && stem.Contains(' '))
                        {
                            var dotBatch = await indexerService.SearchAsync(stemQuery with { Query = stem.Replace(' ', '.') }, ct);
                            AddBatch(dotBatch);
                        }

                        if (CountScopedMatches() > 0)
                        {
                            break;
                        }
                    }

                    if (CountScopedMatches() == 0 && !string.IsNullOrWhiteSpace(workTitle))
                    {
                        var rawTitle = workTitle.Trim();
                        var rawBatch = await indexerService.SearchAsync(request.Query with
                        {
                            Query = rawTitle,
                            MediaVertical = request.Query.MediaVertical ?? workKind,
                            Year = null,
                            ImdbId = null,
                            TmdbId = null,
                            TvdbId = null,
                            TvMazeId = null,
                            Season = null,
                            Episode = null
                        }, ct);
                        AddBatch(rawBatch);

                        if (CountScopedMatches() == 0 && rawTitle.Contains(' '))
                        {
                            var rawDotBatch = await indexerService.SearchAsync(request.Query with
                            {
                                Query = rawTitle.Replace(' ', '.'),
                                MediaVertical = request.Query.MediaVertical ?? workKind,
                                Year = null,
                                ImdbId = null,
                                TmdbId = null,
                                TvdbId = null,
                                TvMazeId = null,
                                Season = null,
                                Episode = null
                            }, ct);
                            AddBatch(rawDotBatch);
                        }
                    }
                }
            }
            else if (isAnime && requestedSeason.GetValueOrDefault() >= 2 && request.Query.Interactive)
            {
                var reqSeasonVal = requestedSeason.GetValueOrDefault();
                foreach (var stem in stems)
                {
                    var animeSeqQuery = request.Query with
                    {
                        Query = $"{stem} {reqSeasonVal}",
                        MediaVertical = request.Query.MediaVertical ?? workKind,
                        Season = requestedSeason,
                        Episode = requestedEpisode,
                        ImdbId = null,
                        TmdbId = null,
                        TvdbId = null,
                        TvMazeId = null,
                        Year = null
                    };
                    var batch = await indexerService.SearchAsync(animeSeqQuery, ct);
                    AddBatch(batch);
                }
            }
        }

        var finalScoped = new List<ScopedReleaseCandidate>();
        foreach (var candidate in candidateMap.Values)
        {
            var parsed = parserRouter.Parse(candidate.Title, candidate.MediaVertical ?? workKind);
            if (EvaluateCandidateScope(
                candidate, parsed, workTitle, knownTitles, workYear, workOriginalLanguage,
                isEpisodic, isAnime, isMovie, imdbId, tmdbId, tvdbId, tvMazeId,
                requestedSeason, requestedEpisode, absoluteEpisodeNumber, request.IncludeSeasonPacks,
                out var scopedCandidate))
            {
                var mergedLangs = MergeLanguages(candidate, parsed, workOriginalLanguage);
                var enrichedParsed = parsed with { DetectedLanguages = mergedLangs };
                finalScoped.Add(scopedCandidate! with { Parsed = enrichedParsed });
            }
        }

        return finalScoped;
    }

    private static int ParseRomanNumeral(string roman) => roman.ToUpperInvariant() switch
    {
        "II" => 2,
        "III" => 3,
        "IV" => 4,
        "V" => 5,
        "VI" => 6,
        "VII" => 7,
        "VIII" => 8,
        "IX" => 9,
        _ => 0
    };
}
