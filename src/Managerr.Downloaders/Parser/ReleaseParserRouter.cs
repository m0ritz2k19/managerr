using System.Text.RegularExpressions;
using Managerr.Indexers.Categories;

namespace Managerr.Downloaders.Parser;

public sealed record ParsedReleaseInfo(
    string RawTitle,
    string MediaVertical,
    string CleanTitle,
    string? ArtistOrAuthor,
    int? Year,
    int? SeasonNumber,
    IReadOnlyList<int> EpisodeNumbers,
    IReadOnlyList<int> AbsoluteEpisodeNumbers,
    bool IsFullSeasonPack,
    string? MovieEdition,
    string? Resolution,
    string? Source,
    string? VideoCodec,
    string? HdrFormat,
    string? AudioCodec,
    string? MusicFormat,
    string? BookFormat,
    IReadOnlyList<string> DetectedLanguages,
    bool IsDualAudio,
    bool IsMultiSubs,
    string? ReleaseGroup);

public interface IReleaseParserRouter
{
    ParsedReleaseInfo Parse(string releaseTitle, string? mediaVerticalHint = null);
}

public static class ParsedReleaseQuality
{
    /// <summary>
    /// The one release-title-to-quality rule, shared by profile scoring and the importer so a release is
    /// named identically whether it is a candidate or the file it produced. Missing source/resolution fall
    /// back to WEBDL/1080p, matching how candidates have always been scored.
    /// </summary>
    public static string ToQualityName(this ParsedReleaseInfo parsed)
    {
        if (!string.IsNullOrWhiteSpace(parsed.MusicFormat))
        {
            return parsed.MusicFormat;
        }

        if (!string.IsNullOrWhiteSpace(parsed.BookFormat))
        {
            return parsed.BookFormat;
        }

        return Managerr.Common.Media.QualityNames.Canonicalize($"{parsed.Source ?? "WEBDL"}-{parsed.Resolution ?? "1080p"}");
    }
}

/// <summary>
/// Sonarr-adapted title normalizer (ported from NzbDrone.Core.Parser.Parser.CleanSeriesTitle)
/// stripping punctuation, colons, diacritics, and symbol noise so 'Avatar: Seven Havens' == 'Avatar.Seven.Havens'.
/// </summary>
public static partial class ReleaseTitleNormalizer
{
    [GeneratedRegex(@"^(?:the|a|an|der|die|das)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingArticleRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphaNumRegex();

    public static string CleanSeriesTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var working = title.Trim()
            .Replace("&", " and ", StringComparison.Ordinal)
            .Replace("+", " and ", StringComparison.Ordinal)
            .Replace("ß", "ss", StringComparison.Ordinal)
            .Replace("'", "", StringComparison.Ordinal)
            .Replace("’", "", StringComparison.Ordinal)
            .Normalize(System.Text.NormalizationForm.FormKD);

        var sb = new System.Text.StringBuilder(working.Length);
        foreach (var ch in working)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return NonAlphaNumRegex().Replace(sb.ToString(), string.Empty);
    }

    public static string CleanSeriesTitleWithoutArticle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var stripped = LeadingArticleRegex().Replace(title.Trim(), string.Empty);
        return CleanSeriesTitle(stripped);
    }

    public static bool TitlesMatch(string? candidateTitle, string? workTitle, int? workYear = null)
    {
        var a = CleanSeriesTitle(candidateTitle);
        var b = CleanSeriesTitle(workTitle);
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }

        if (workYear.HasValue)
        {
            var bWithYear = CleanSeriesTitle($"{workTitle} {workYear.Value}");
            if (string.Equals(a, bWithYear, StringComparison.Ordinal))
            {
                return true;
            }
        }

        var aNoArt = CleanSeriesTitleWithoutArticle(candidateTitle);
        var bNoArt = CleanSeriesTitleWithoutArticle(workTitle);
        return aNoArt.Length >= 3 && string.Equals(aNoArt, bNoArt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sonarr-style series identity spec: the parsed release title must match the work title or one of its aliases.
    /// </summary>
    public static bool MatchesWork(string? candidateTitle, string workTitle, IEnumerable<string>? altTitles, int? workYear = null) =>
        TitlesMatch(candidateTitle, workTitle, workYear) ||
        (altTitles ?? []).Any(alt => TitlesMatch(candidateTitle, alt, workYear));
}

/// <summary>
/// Consolidated media-typed release title parser adapting Sonarr (TV/Anime), Radarr (Movies/Editions),
/// Lidarr (Music/Lossless), and Readarr (Ebook/Audiobook/Comic) regex grammar into one deterministic router.
/// </summary>
public sealed partial class ReleaseParserRouter : IReleaseParserRouter
{
    public ParsedReleaseInfo Parse(string releaseTitle, string? mediaVerticalHint = null)
    {
        var raw = releaseTitle.Trim();

        // Usenet/NZB names separate with '_' where scene names use '.', but '_' is a word character, so every
        // '\b' below (S01E02, years, EPUB, Season 2, ...) would miss after it. Match on the dotted spelling; the
        // replacement is one char for one char, so indexes still line up with the raw name.
        var text = raw.Replace('_', '.');
        var vertical = InferVertical(text, mediaVerticalHint);

        var languages = ExtractLanguages(text, out var isDualAudio, out var isMultiSubs);
        var (source, resolution) = ExtractQuality(text, vertical);
        var videoCodec = ExtractVideoCodec(text);
        var hdr = ExtractHdr(text);
        var audioCodec = ExtractAudioCodec(text);
        var musicFormat = vertical == MediaVerticals.Music ? ExtractMusicFormat(text) : null;
        var bookFormat = vertical is MediaVerticals.Ebooks or MediaVerticals.Audiobooks or MediaVerticals.Comics
            ? ExtractBookFormat(text)
            : null;
        var edition = vertical == MediaVerticals.Movies ? ExtractMovieEdition(text) : null;

        int? year = YearRegex().Match(text) is { Success: true } ym && int.TryParse(ym.Groups["year"].Value, out var yr)
            ? yr
            : null;

        string? releaseGroup = null;
        ParseEpisodeByVertical(
            raw,
            text,
            ref vertical,
            ref year,
            out var season,
            out var episodes,
            out var absEpisodes,
            out var isSeasonPack,
            ref releaseGroup,
            out var cleanTitle,
            out var artistOrAuthor);

        releaseGroup = ExtractReleaseGroup(raw, vertical, releaseGroup);

        var sortedEpisodes = episodes.Distinct().OrderBy(x => x).ToArray();
        var sortedAbsEpisodes = absEpisodes.Distinct().OrderBy(x => x).ToArray();
        var trimmedTitle = cleanTitle.TrimStart(' ', '-', '.', '[').TrimEnd(' ', '-', '.', '(', '[').Replace('§', '.');
        if (trimmedTitle.StartsWith('(') && !Regex.IsMatch(trimmedTitle, @"^\(\d+\)"))
        {
            trimmedTitle = trimmedTitle.TrimStart('(');
        }

        var normalizedCleanTitle = Regex.Replace(trimmedTitle, @"\s+", " ").Trim();

        return new ParsedReleaseInfo(
            RawTitle: raw,
            MediaVertical: vertical,
            CleanTitle: normalizedCleanTitle,
            ArtistOrAuthor: artistOrAuthor,
            Year: year,
            SeasonNumber: season,
            EpisodeNumbers: sortedEpisodes,
            AbsoluteEpisodeNumbers: sortedAbsEpisodes,
            IsFullSeasonPack: isSeasonPack,
            MovieEdition: edition,
            Resolution: resolution,
            Source: source,
            VideoCodec: videoCodec,
            HdrFormat: hdr,
            AudioCodec: audioCodec,
            MusicFormat: musicFormat,
            BookFormat: bookFormat,
            DetectedLanguages: languages,
            IsDualAudio: isDualAudio,
            IsMultiSubs: isMultiSubs,
            ReleaseGroup: releaseGroup);
    }

    private static string StripTrailingYearFromSeriesTitle(string cleanTitle, int? year)
    {
        if (!year.HasValue || year.Value > DateTimeOffset.UtcNow.Year + 2)
        {
            return cleanTitle;
        }

        var yr = year.Value.ToString();
        foreach (var suffix in new[] { $" ({yr})", $" [{yr}]", $" - {yr}", $" {yr}" })
        {
            if (cleanTitle.EndsWith(suffix, StringComparison.Ordinal) && cleanTitle.Length > suffix.Length)
            {
                var stripped = cleanTitle[..^suffix.Length].Trim(' ', '-', '.', '(', '[');
                if (stripped.Length >= 2)
                {
                    return stripped;
                }
            }
        }

        return cleanTitle;
    }

    private static string InferVertical(string raw, string? hint)
    {
        var normalizedHint = hint?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedHint) && MediaVerticals.All.Contains(normalizedHint))
        {
            // Uncategorized '*' downloads often default to 'movies'; promote explicit TV/Anime markers and
            // book/comic/audiobook formats (an EPUB under a movies category became a movie work)
            if (normalizedHint == MediaVerticals.Movies)
            {
                if (AnimeSubGroupRegex().IsMatch(raw)) return MediaVerticals.Anime;
                if (StandardSeasonEpisodeRegex().IsMatch(raw) || AltXSeasonEpisodeRegex().IsMatch(raw) || WordSeasonEpisodeRegex().IsMatch(raw) || WordSeasonPackRegex().IsMatch(raw))
                {
                    return MediaVerticals.Tv;
                }

                if (Regex.IsMatch(raw, @"\b(M4B|Audiobook|Unabridged)\b", RegexOptions.IgnoreCase)) return MediaVerticals.Audiobooks;
                if (Regex.IsMatch(raw, @"\b(CBZ|CBR)\b", RegexOptions.IgnoreCase)) return MediaVerticals.Comics;
                if (Regex.IsMatch(raw, @"\b(EPUB|AZW3|MOBI)\b", RegexOptions.IgnoreCase)) return MediaVerticals.Ebooks;
            }

            return normalizedHint;
        }
        if (AnimeSubGroupRegex().IsMatch(raw)) return MediaVerticals.Anime;
        if (Regex.IsMatch(raw, @"\b(M4B|Audiobook|Unabridged)\b", RegexOptions.IgnoreCase)) return MediaVerticals.Audiobooks;
        if (Regex.IsMatch(raw, @"\b(CBZ|CBR)\b", RegexOptions.IgnoreCase)) return MediaVerticals.Comics;
        if (Regex.IsMatch(raw, @"\b(EPUB|AZW3|MOBI)\b", RegexOptions.IgnoreCase)) return MediaVerticals.Ebooks;
        if (StandardSeasonEpisodeRegex().IsMatch(raw) || AltXSeasonEpisodeRegex().IsMatch(raw) || WordSeasonEpisodeRegex().IsMatch(raw) || WordSeasonPackRegex().IsMatch(raw)) return MediaVerticals.Tv;
        if (Regex.IsMatch(raw, @"\b(FLAC|24bit|MP3\s*320|WEB\s*FLAC|V0)\b", RegexOptions.IgnoreCase) &&
            !Regex.IsMatch(raw, @"\b(2160p|1080p|720p)\b", RegexOptions.IgnoreCase))
        {
            return MediaVerticals.Music;
        }
        return MediaVerticals.Movies;
    }

    private static string CleanDotsAndUnderscores(string s) =>
        Regex.Replace(s.Replace('.', ' ').Replace('_', ' '), @"\s+", " ").Trim();

    private static string StripBracketMetadata(string s) =>
        Regex.Replace(Regex.Replace(s, @"\([^)]*\)|\[[^\]]*\]", " "), @"\s+", " ").Trim();

    [GeneratedRegex(@" - |\.-\.|_-_")]
    private static partial Regex AuthorTitleSeparatorRegex();

    [GeneratedRegex(@"(?<=^|[ ._(\[])(?:19|20)\d{2}(?=$|[ ._)\]-])")]
    private static partial Regex TitleYearTokenRegex();

    [GeneratedRegex(@"(?<=[ ._(\[])(?:German|English|Deutsch|French|Multi|Retail|Unabridged|Abridged|EPUB|AZW3|MOBI|PDF|CBZ|CBR|M4B|MP3|FLAC|AAC|WEB|24bit|16bit|eBook|Audiobook)(?=$|[ ._)\]-])", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseTagTokenRegex();

    // 'Wind.and.Truth.2024.German.EPUB-TESTLAB' -> 'Wind.and.Truth'. A year at the very start belongs to the
    // title ('1984', '2001 A Space Odyssey'); with neither year nor tag nothing is cut (keeps 'Spider-Man').
    private static string CutReleaseTail(string titlePart)
    {
        var year = TitleYearTokenRegex().Matches(titlePart).FirstOrDefault(m => m.Index > 0);
        var tag = ReleaseTagTokenRegex().Match(titlePart);
        var cut = new[] { year?.Index, tag.Success ? tag.Index : (int?)null }.Where(i => i is > 0).Min();
        return cut is { } at ? titlePart[..at].TrimEnd(' ', '.', '_', '-', '(', '[') : titlePart;
    }
}
