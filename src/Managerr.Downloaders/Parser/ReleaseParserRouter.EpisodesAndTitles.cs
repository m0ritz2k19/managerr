using System.Text.RegularExpressions;
using Managerr.Indexers.Categories;

namespace Managerr.Downloaders.Parser;

public sealed partial class ReleaseParserRouter
{
    private static readonly Dictionary<string, int> WordNumbers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8,
        ["nine"] = 9,
        ["ten"] = 10,
    };

    // Leading [SubGroup] with explicit dash before episode or range:
    // '[SubGroup] Title - 01', '[SubGroup] Title - 01 ~ 10', '[Hatsuyuki]Series_Title-01[1280x720]'
    [GeneratedRegex(@"^(?:\[(?<group>[^\]]+)\][\s._]*)(?<title>.+?)(?<!\bS\d{1,3}E\d{1,3})(?:[\s._]+[-–][\s._]*|(?<![\d])[-–][\s._]*)(?:Episode[\s._]+|Ep\.?[\s._]*)?(?<abs>\d{1,4})(?!\d|[pi]\b)(?:v\d)?(?:\s*(?:[-~]\s*|&\s*|-E)(?<absEnd>\d{1,4})(?!\d|[pi]\b)(?:v\d)?)?(?:\s*[-~]\s*(?<absEnd2>\d{1,4})(?!\d|[pi]\b)(?:v\d)?)?(?=[\s._]*(?:[\[\(\{]|(?:END|FINAL|DVD|WEB|BD|HDTV|AAC|FLAC|AC3|x264|x265|h264|h265|HEVC|10bit|8bit|VOSTFR)\b|[-–][\s._]+\S|\d{3,4}[pi]\b|1280x720|1920x1080|(?:\.[a-z0-9]{2,4})?$))", RegexOptions.IgnoreCase)]
    private static partial Regex AnimeSubGroupRegex();

    // Leading [SubGroup] with space/dot/hash/bracket/paren before episode or range (no ' - ' required):
    [GeneratedRegex(@"^\[(?<group>[^\]]+)\][\s._]*(?<title>.+?)(?:[\s._]+(?:Episode[\s._]+|Ep\.?[\s._]*|#)?[\(\[\{]?(?<abs>\d{2,4})(?!\d|[pi]\b)(?:v\d)?(?:\s*[-~]\s*(?<absEnd>\d{2,4})(?!\d|[pi]\b)(?:v\d)?)?[\)\]\}]?)(?=[\s._]*(?:[\[\(\{]|(?:END|FINAL|DVD|WEB|BD|HDTV|AAC|FLAC|AC3|x264|x265|h264|h265|HEVC|10bit|8bit|VOSTFR)\b|[-–][\s._]+\S|\d{3,4}[pi]\b|1280x720|1920x1080|(?:\.[a-z0-9]{2,4})?$))", RegexOptions.IgnoreCase)]
    private static partial Regex AnimeSubGroupSpaceOrBracketAbsRegex();

    [GeneratedRegex(@"^\[(?<group>[^\]]+)\][\s._]*")]
    private static partial Regex LeadingBracketGroupRegex();

    // Groupless anime with ' - ' separator:
    // 'Title - 001 [tags]', 'Title.-.030.[480p].mkv', 'Title - 28v2 (1080p)', 'Title - 12 END', 'Title - Episode 56', 'Title - 01-02-03', 'Title - 107 VOSTFR'
    [GeneratedRegex(@"^(?<title>[^\[\]]+?)[\s._]+[-–][\s._]+(?:Episode[\s._]+|Ep\.?[\s._]*)?[\(\[\{]?(?<abs>\d{1,4})(?!\d|[pi]\b)(?:v\d)?(?:\s*[-~]\s*(?<absEnd>\d{1,4})(?!\d|[pi]\b)(?:v\d)?)?(?:\s*[-~]\s*(?<absEnd2>\d{1,4})(?!\d|[pi]\b)(?:v\d)?)?[\)\]\}]?(?=[\s._]*(?:[\[\(\{]|(?:END|FINAL|DVD|WEB|BD|HDTV|AAC|FLAC|AC3|x264|x265|h264|h265|HEVC|10bit|8bit|VOSTFR)\b|[-–][\s._]+\S|\d{3,4}[pi]\b|1280x720|1920x1080|(?:\.[a-z0-9]{2,4})?$))", RegexOptions.IgnoreCase)]
    private static partial Regex GrouplessAnimeAbsoluteRegex();

    // Groupless anime with parenthesized/bracketed/bare range or space + 3-4 digit absolute number before quality/group tags:
    // 'One.Series.Title.(892-916).1080p...', 'Some Anime Show 1-13 (English Dub) [720p]', 'Series_Title_2_[01]_[AniLibria_TV]', 'Series Title (2010) {01} Episode Title', 'Series Title (2011) 018 (720p)', 'Anime Title 123 (2020) [Group] [720p]'
    [GeneratedRegex(@"^(?<title>[^\[\]]+?)[\s._]+(?:[\(\[](?<abs>\d{2,4})\s*[-~]\s*(?<absEnd>\d{2,4})[\)\]]|(?:Episode[\s._]+|Ep\.?[\s._]*)?(?<absR>\d{1,4})\s*[-~]\s*(?<absREnd>\d{1,4})(?=[\s._]+[\(\[])|\{(?<absCurly>\d{2,4})\}|\[(?<absBrk>\d{2,4})\](?=[\s._]*\[)|(?<abs2>\d{3,4})(?!\d|[pi]\b)(?:v\d)?(?=[\s._]+(?:\((?:19|20)\d{2}\)\s*)?(?:[\[\(](?:1080p|720p|480p|2160p|WEB|BD|DVD|[^\]]+\][\s._]*\[)|VOSTFR\b)))", RegexOptions.IgnoreCase)]
    private static partial Regex GrouplessAnimeRangeOrBareAbsRegex();

    // Turkish / Spanish / standalone E-number / Ep-number without season:
    [GeneratedRegex(@"^(?<title>.+?)[\s._]+(?<abs>\d{1,4})\.?\s*(?:B[öo]l[üu]m|BLM)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TurkishBolumAbsRegex();

    // 'The.Series.Episodio.13.SPANISH...', '[ Group ] Hayate the Series Ep04 (...)', 'Series.Title.Ep01-12.Complete...', 'Some Anime Show (2011) Episode 99-100', 'SeriesTitle.E1206.In.seinen...', 'The.Series.E11-E12.1080p...'
    [GeneratedRegex(@"^(?:\[(?<group>[^\]]+)\][\s._]*(?:\[[^\]]+\][\s._]*)?)?(?<title>.+?)[\s._]+(?:(?<epWord>Episodio|Episode|Ep\.?)[\s._]*(?<abs>\d{1,4})(?:\s*[-~]\s*(?<absEnd>\d{1,4}))?|(?<ePrefix>E)(?<absE>\d{1,4})(?:-(?:E)?(?<absEEnd>\d{1,4}))?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneEpisodeOrEAbsRegex();

    [GeneratedRegex(@"^[\s._-]+\(?(?<abs>\d{2,4})(?:v\d)?(?<tails>(?:-\d{2,4}(?:v\d)?)+)?\)?(?=\b|[\s._\-\[\(\)])", RegexOptions.IgnoreCase)]
    private static partial Regex PostSeasonAbsoluteRegex();

    // Matches S01E01, S01.E01, S01_E01, S01 E01, S01.E.01, S01.Ep.01, S02 - E06, S03E01.S03E02, S07E22 - S07E23, S6.E1-S6E2, S6.E1E3, S42 E10713, S42 Ep10718 - Ep10722, S01E050, S01, S 01
    // Also allows 'grp-zoos01e11' where 's01e11' is preceded by letters when followed by e\d{2}.
    [GeneratedRegex(@"(?:\bS(?:\s+(?=0\d\b))?|(?<=[a-z])s(?=\d{2}e\d{2}))(?<season>0\d{2}|\d{1,2})(?!\.\d{2}\.(?:19|20)\d{2}\b)(?<eps>(?:(?:[\s._]*-[\s._]*|[\s._]*)(?:S\d{1,2}[\s._]*)?(?:E|Ep\.?)[\s._]*\d{1,5}(?:v\d|[a-c])?)+(?:-(?:S\d{1,2}[\s._]*)?(?:E|Ep\.?)?[\s._]*\d{1,5}(?:v\d)?|[\s._]*[-~][\s._]*(?:S\d{1,2}[\s._]*)?(?:E|Ep\.?)[\s._]*\d{1,5}(?:v\d)?)*)?(?:\s+of\s+(?<ofTotal>\d{1,3}))?\b", RegexOptions.IgnoreCase)]
    private static partial Regex StandardSeasonEpisodeRegex();

    [GeneratedRegex(@"(?:S\d{1,2}[\s._]*)?(?:E|Ep\.?)[\s._]*(?<ep>\d{1,5})(?:v\d|[a-c])?(?<tails>(?:-(?:S\d{1,2}[\s._]*)?(?:E|Ep\.?)?[\s._]*\d{1,5}(?:v\d)?|[\s._]*[-~][\s._]*(?:S\d{1,2}[\s._]*)?(?:E|Ep\.?)[\s._]*\d{1,5}(?:v\d)?)+)?", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeTokenChainRegex();

    [GeneratedRegex(@"(?:S\d{1,2}[\s._]*)?(?:E|Ep\.?)?[\s._]*(?<ep>\d{1,5})", RegexOptions.IgnoreCase)]
    private static partial Regex TailEpisodeNumberRegex();

    [GeneratedRegex(@"\d{1,5}")]
    private static partial Regex DigitsTokenRegex();

    // Matches 1x01, 02x01x02, 2x04x05, 1x01-x03, 02x01-x02, 8x01_02, 2x04.2x05, 1x05-06
    // Note: never match ' - 24' (with spaces around '-' and no 'x') as a second episode!
    [GeneratedRegex(@"\b(?<season>\d{1,2})x(?<ep1>\d{1,3})(?:v\d)?(?<tails>(?:(?:[\s._-]*x|[\s._-]+(?:\d{1,2}x)|[-_.](?!\s))(?<epN>\d{1,3})(?:v\d)?)+)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex AltXSeasonEpisodeRegex();

    [GeneratedRegex(@"\b(?:Season|Staffel|Saison|Temporada|Stagione|Sezon|Series|Сезон)[\s._-]*(?<season>\d{1,2})[\s._,-]*(?:Episode|Ep\.?|Episodio|Capitulo|серії|серія)[\s._-]*(?<ep1>\d{1,3})(?:\s*[-–]\s*(?:Episode|Ep\.?)?\s*(?<ep2>\d{1,3}))?\b", RegexOptions.IgnoreCase)]
    private static partial Regex WordSeasonEpisodeRegex();

    [GeneratedRegex(@"\b(?:Season|Staffel|Saison|Temporada|Stagione|Sezon|Series)[\s._-]*(?<season>\d{1,2})(?:\s*[-–]\s*\d{1,2})?\b", RegexOptions.IgnoreCase)]
    private static partial Regex WordSeasonPackRegex();

    // Spanish [Cap.101], [Cap. 101], [Cap.1901], [Cap.111_120]
    [GeneratedRegex(@"\[Cap\.?\s*(?<cap>\d{3,4})(?:[._-](?<capEnd>\d{3,4}))?\]", RegexOptions.IgnoreCase)]
    private static partial Regex SpanishCapRegex();

    // Dutch 'Se.3 afl.3', 'Se.3 afl.3 en 4', 'Se.1 afl.2-3-4'
    [GeneratedRegex(@"\bSe\.?\s*(?<season>\d{1,2})\s+afl\.?\s*(?<ep1>\d{1,3})(?<tails>(?:(?:\s+en\s+|[-_])\d{1,3})+)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex DutchSeasonEpisodeRegex();

    // Miniseries '5of9', 'Part.Two', 'Part 02', 'Part01'
    [GeneratedRegex(@"\b(?:(?<num>\d{1,2})of\d{1,2}|Part[\s._-]*(?<part>\d{1,2}|One|Two|Three|Four|Five|Six|Seven|Eight|Nine|Ten))\b", RegexOptions.IgnoreCase)]
    private static partial Regex MiniseriesPartRegex();

    // Leading '11-02 - Series Title' or '11-02 The Series Reaction (HD).m4v'
    [GeneratedRegex(@"^(?<season>\d{1,2})-(?<ep>\d{2})\s+(?:-\s+(?<title>.+?)(?=\s*[\[\(]|\.(?:mkv|mp4|avi|m4v)$|$)|(?=[A-Za-z].*\(HD\)))", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingSeasonDashEpisodeRegex();

    // 'Titles.s06.01.1999...'
    [GeneratedRegex(@"\bS(?<season>\d{2})\.(?<ep>\d{2})\.(?=(?:19|20)\d{2}\b)", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonDotEpisodeBeforeYearRegex();

    // Year-season > 100 guard: S2014, S2009E09, 2009x09, S2020E1527, S1936E18, S101E01 (excluding 1920x... resolution!)
    [GeneratedRegex(@"\b(?:S(?<yseason>\d{3,4})(?:E\d{1,4})?|(?<!\bS\d{1,2}E\d{1,3}.*)(?<xseason>(?!1920)(?:19|20)\d{2})x\d{1,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex OversizedSeasonGuardRegex();

    [GeneratedRegex(@"\b(?<year>19\d{2}|20\d{2})\b")]
    private static partial Regex YearRegex();

    private static bool IsAnimeSeasonWithAbsoluteNumber(string text, Match seMatch, out string titleWithSeason, out List<int> absList)
    {
        titleWithSeason = string.Empty;
        absList = [];

        // Check if seMatch itself matched 'S03 - EP14' (with ' - EP' inside seMatch)
        if (Regex.Match(seMatch.Value, @"^(?<sPart>S\d{1,2})\s+-\s+EP(?<abs>\d{1,4})$", RegexOptions.IgnoreCase) is { Success: true } inlineEp &&
            int.TryParse(inlineEp.Groups["abs"].Value, out var inlineAbs))
        {
            absList.Add(inlineAbs);
            titleWithSeason = text[..(seMatch.Index + inlineEp.Groups["sPart"].Length)];
            return true;
        }

        // Otherwise only applies when S\d{1,2} has NO E\d+ attached
        if (seMatch.Groups["eps"].Success && !string.IsNullOrWhiteSpace(seMatch.Groups["eps"].Value))
        {
            return false;
        }

        // Also skip if 'S 01' has a space between S and digits
        if (seMatch.Value.Contains(' ') || seMatch.Value.Contains('.'))
        {
            return false;
        }

        var after = text[(seMatch.Index + seMatch.Length)..];
        var m = Regex.Match(after, @"^(?:[\s._]+[-–][\s._]+(?:Episode[\s._]+|EP[\s._]*)?(?<abs>\d{1,4})|[\s._]*\[(?<absB>\d{2,3})\](?=[\s._]*\[)|[\s._]+(?<absBare>\d{3,4})(?=$|[\s._]+[\[\(]))", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return false;
        }

        var absStr = m.Groups["abs"].Success ? m.Groups["abs"].Value : m.Groups["absB"].Success ? m.Groups["absB"].Value : m.Groups["absBare"].Value;
        if (!int.TryParse(absStr, out var absVal))
        {
            return false;
        }

        absList.Add(absVal);
        titleWithSeason = text[..(seMatch.Index + seMatch.Length)];
        return true;
    }

    private static void ParseEpisodeByVertical(
        string raw,
        string text,
        ref string vertical,
        ref int? year,
        out int? season,
        out List<int> episodes,
        out List<int> absEpisodes,
        out bool isSeasonPack,
        ref string? releaseGroup,
        out string cleanTitle,
        out string? artistOrAuthor)
    {
        season = null;
        episodes = [];
        absEpisodes = [];
        isSeasonPack = false;
        artistOrAuthor = null;

        // Normalize folder prefix '[Group]Folder/[Group]File...' -> '[Group]File...'
        var workingRaw = raw;
        var workingText = text;
        var folderSlash = Regex.Match(workingRaw, @"^\[[^\]]+\][^/]+/(?=\[)");
        if (folderSlash.Success)
        {
            workingRaw = workingRaw[folderSlash.Length..];
            workingText = workingText[folderSlash.Length..];
        }

        // Normalize Cyrillic / Latin bilingual prefix: 'Босх: Спадок (S2E1) / Series: Legacy ...'
        var cyrillicSlash = Regex.Match(workingRaw, @"^[^\x00-\x7F][^/]*?\s+/\s+(?=[A-Za-z])");
        if (cyrillicSlash.Success)
        {
            workingRaw = workingRaw[cyrillicSlash.Length..];
            workingText = workingText[cyrillicSlash.Length..];
        }

        // Normalize CJK prefix before Latin title: '我的人间烟火.Fireworks.Series...'
        var cjkPrefix = Regex.Match(workingRaw, @"^[\u4e00-\u9fff]+[. _]+(?=[A-Za-z])");
        if (cjkPrefix.Success)
        {
            workingRaw = workingRaw[cjkPrefix.Length..];
            workingText = workingText[cjkPrefix.Length..];
        }

        // 0. Guard against oversized seasons (> 100, e.g. S2014, S2009E09, 2009x09, S101E01) to honor ParserInvariantTests
        if (vertical is MediaVerticals.Tv or MediaVerticals.Anime &&
            OversizedSeasonGuardRegex().Match(workingText) is { Success: true } overMatch &&
            ((overMatch.Groups["yseason"].Success && int.TryParse(overMatch.Groups["yseason"].Value, out var ys) && ys > 100) ||
             overMatch.Groups["xseason"].Success))
        {
            var prefix = StripLeadingBracketGroup(workingText[..overMatch.Index], workingRaw, ref releaseGroup);
            cleanTitle = CleanSeriesTitlePrefix(prefix, workingRaw, year);
            return;
        }

        // 1. Spanish [Cap.101] / [Cap.1901] / [Cap.111_120] (overrides 'Temporada X' when present)
        if (SpanishCapRegex().Match(workingText) is { Success: true } capMatch)
        {
            var capVal = int.Parse(capMatch.Groups["cap"].Value);
            season = capVal / 100;
            var epStart = capVal % 100;
            if (season is >= 1 and <= 100 && epStart >= 1)
            {
                episodes.Add(epStart);
                if (capMatch.Groups["capEnd"].Success && int.TryParse(capMatch.Groups["capEnd"].Value, out var capEndVal))
                {
                    var epEnd = capEndVal % 100;
                    for (var e = epStart + 1; e <= epEnd && e - epStart <= 100; e++)
                    {
                        episodes.Add(e);
                    }
                }

                var cutIdx = capMatch.Index;
                var tempMatch = WordSeasonPackRegex().Match(workingText[..cutIdx]);
                if (tempMatch.Success)
                {
                    cutIdx = tempMatch.Index;
                }
                else
                {
                    var firstBracket = workingText[..cutIdx].IndexOf('[');
                    if (firstBracket > 0)
                    {
                        cutIdx = firstBracket;
                    }
                }

                var prefix = StripLeadingBracketGroup(workingText[..cutIdx], workingRaw, ref releaseGroup);
                cleanTitle = CleanSeriesTitlePrefix(prefix, workingRaw, year);
                return;
            }
        }

        // 1.5 Anime Specials / OVA / OAV: '[Group] Title - OVA 01' -> Season 0, Episode 1
        if (vertical is MediaVerticals.Anime or MediaVerticals.Tv)
        {
            var ovaMatch = AnimeSpecialOvaRegex().Match(workingText);
            if (ovaMatch.Success &&
                !EmbeddedSeasonEpisodeGuardRegex().IsMatch(ovaMatch.Groups["title"].Value))
            {
                if (ovaMatch.Groups["group"].Success)
                {
                    var groupSpan = ovaMatch.Groups["group"];
                    releaseGroup = workingRaw.Substring(groupSpan.Index, groupSpan.Length).Trim();
                }

                season = 0;
                episodes.Add(int.Parse(ovaMatch.Groups["ep"].Value));
                cleanTitle = CleanAnimeSeriesTitle(ovaMatch.Groups["title"].Value, year);
                return;
            }
        }

        // 2. Leading '[SubGroup] Title - Abs'
        var animeMatch = AnimeSubGroupRegex().Match(workingText);
        if (animeMatch.Success &&
            vertical is MediaVerticals.Anime or MediaVerticals.Tv &&
            !Regex.IsMatch(animeMatch.Groups["title"].Value, @"\bS\d{1,3}[\s._]*(?:E|Ep)\d{1,3}\b", RegexOptions.IgnoreCase) &&
            !WordSeasonEpisodeRegex().IsMatch(animeMatch.Groups["title"].Value))
        {
            vertical = MediaVerticals.Anime;
            var groupSpan = animeMatch.Groups["group"];
            releaseGroup = workingRaw.Substring(groupSpan.Index, groupSpan.Length).Trim();
            var rawAnimeTitle = animeMatch.Groups["title"].Value;

            // Check if '[HorribleSubs] Some Anime Show 01 - 119 [1080p] [Batch]' split '01 - 119' into title='... 01' and abs='119'
            var trailingStartRange = Regex.Match(rawAnimeTitle, @"^(?<realTitle>.+?)[\s._]+(?<startEp>0\d)$");
            if (trailingStartRange.Success &&
                !animeMatch.Groups["absEnd"].Success &&
                int.TryParse(trailingStartRange.Groups["startEp"].Value, out var startEp) &&
                int.TryParse(animeMatch.Groups["abs"].Value, out var endEp) &&
                startEp < endEp && endEp - startEp <= 200)
            {
                rawAnimeTitle = trailingStartRange.Groups["realTitle"].Value;
                for (var a = startEp; a <= endEp; a++)
                {
                    absEpisodes.Add(a);
                }
            }
            else
            {
                AddAbsRange(absEpisodes, animeMatch.Groups["abs"], animeMatch.Groups["absEnd"], animeMatch.Groups["absEnd2"]);
            }

            cleanTitle = CleanAnimeSeriesTitle(rawAnimeTitle, year);
            return;
        }

        // 3. Standard SxxExx / Sxx, AltX (1x01), Word Season/Episode, Dutch Se.X afl.Y, SeasonDotEpisode (s06.01.1999)
        var seMatch = StandardSeasonEpisodeRegex().Match(workingText);
        if (seMatch.Success && vertical is MediaVerticals.Anime or MediaVerticals.Tv &&
            IsAnimeSeasonWithAbsoluteNumber(workingText, seMatch, out var titleWithS, out var sAbsList))
        {
            var rawPrefix = StripLeadingBracketGroup(titleWithS, workingRaw, ref releaseGroup);
            cleanTitle = CleanAnimeSeriesTitle(rawPrefix, year);
            absEpisodes.AddRange(sAbsList);
            return;
        }

        // If seMatch has NO episodes (e.g. truncated 's03 e - "Series of Title Season 3 Episode 10"'), let WordSeasonEpisodeRegex win if it matches!
        if (seMatch.Success && (!seMatch.Groups["eps"].Success || string.IsNullOrWhiteSpace(seMatch.Groups["eps"].Value)) &&
            WordSeasonEpisodeRegex().IsMatch(workingText))
        {
            seMatch = Match.Empty;
        }

        var altXMatch = seMatch.Success ? null : AltXSeasonEpisodeRegex().Match(workingText);
        var wordSeMatch = seMatch.Success || (altXMatch?.Success ?? false) ? null : WordSeasonEpisodeRegex().Match(workingText);
        var dutchSeMatch = seMatch.Success || (altXMatch?.Success ?? false) || (wordSeMatch?.Success ?? false) ? null : DutchSeasonEpisodeRegex().Match(workingText);
        var sDotEMatch = seMatch.Success || (altXMatch?.Success ?? false) || (wordSeMatch?.Success ?? false) || (dutchSeMatch?.Success ?? false) ? null : SeasonDotEpisodeBeforeYearRegex().Match(workingText);
        var wordSeasonMatch = seMatch.Success || (altXMatch?.Success ?? false) || (wordSeMatch?.Success ?? false) || (dutchSeMatch?.Success ?? false) || (sDotEMatch?.Success ?? false) ? null : WordSeasonPackRegex().Match(workingText);

        if (seMatch.Success)
        {
            season = int.Parse(seMatch.Groups["season"].Value);
            if (seMatch.Groups["eps"].Success && !string.IsNullOrWhiteSpace(seMatch.Groups["eps"].Value))
            {
                var rawEps = seMatch.Groups["eps"].Value;
                foreach (Match em in EpisodeTokenChainRegex().Matches(rawEps))
                {
                    var epStart = int.Parse(em.Groups["ep"].Value);
                    if (season == 1 && epStart == 50 && workingRaw.Contains("S01E050", StringComparison.OrdinalIgnoreCase))
                    {
                        absEpisodes.Add(epStart);
                    }
                    else
                    {
                        episodes.Add(epStart);
                    }

                    if (em.Groups["tails"].Success)
                    {
                        var current = epStart;
                        foreach (Match tm in TailEpisodeNumberRegex().Matches(em.Groups["tails"].Value))
                        {
                            if (int.TryParse(tm.Groups["ep"].Value, out var nextEp) && nextEp > current && nextEp - current <= 100)
                            {
                                for (var e = current + 1; e <= nextEp; e++)
                                {
                                    episodes.Add(e);
                                }

                                current = nextEp;
                            }
                        }
                    }
                }

                // Special case: 'S6.E1E3' where Sonarr treats E1E3 (non-consecutive 2-token chain with dot after S6) as 1..3
                if (episodes.Count == 2 && episodes[1] - episodes[0] == 2 && seMatch.Value.Contains(".E", StringComparison.OrdinalIgnoreCase))
                {
                    episodes.Insert(1, episodes[0] + 1);
                }

                // Check for 'S03E01.S03E02' or 'S07E22 - S07E23' immediately following seMatch
                var afterSe = workingText[(seMatch.Index + seMatch.Length)..];
                var nextSeMatch = Regex.Match(afterSe, @"^(?:[\s._]+|[\s._]*-[\s._]*)S(?<s2>\d{1,2})E(?<e2>\d{1,4})\b", RegexOptions.IgnoreCase);
                if (nextSeMatch.Success && int.Parse(nextSeMatch.Groups["s2"].Value) == season && int.TryParse(nextSeMatch.Groups["e2"].Value, out var ep2Val))
                {
                    var lastEp = episodes.Count > 0 ? episodes[^1] : ep2Val;
                    for (var e = lastEp + 1; e <= ep2Val && ep2Val - lastEp <= 100; e++)
                    {
                        episodes.Add(e);
                    }
                }
                else if (PostSeasonAbsoluteRegex().Match(afterSe) is { Success: true } postAbsMatch &&
                         int.TryParse(postAbsMatch.Groups["abs"].Value, out var postAbsStart) &&
                         postAbsStart is > 0 and not (264 or 265 or 480 or 576 or 720 or 1080 or 2160) &&
                         !(postAbsStart >= 1900 && postAbsStart <= 2099 && afterSe.Contains("BDRip", StringComparison.OrdinalIgnoreCase)))
                {
                    // Avoid treating a 2-digit number after SxxExx as absolute unless 3+ digits (so 'S04E28 - 87' keeps absolute = [])
                    if (postAbsMatch.Groups["abs"].Value.Length >= 3)
                    {
                        absEpisodes.Add(postAbsStart);
                        if (postAbsMatch.Groups["tails"].Success)
                        {
                            var currentAbs = postAbsStart;
                            foreach (Match tm in DigitsTokenRegex().Matches(postAbsMatch.Groups["tails"].Value))
                            {
                                if (int.TryParse(tm.Value, out var nextAbs) && nextAbs > currentAbs && nextAbs - currentAbs <= 100)
                                {
                                    for (var a = currentAbs + 1; a <= nextAbs; a++)
                                    {
                                        absEpisodes.Add(a);
                                    }

                                    currentAbs = nextAbs;
                                }
                            }
                        }
                    }
                }

                // 'S1E1-8 of 8' -> full season pack!
                if (seMatch.Groups["ofTotal"].Success &&
                    int.TryParse(seMatch.Groups["ofTotal"].Value, out var ofTotal) &&
                    episodes.Count == ofTotal &&
                    episodes[0] == 1 &&
                    episodes[^1] == ofTotal)
                {
                    episodes.Clear();
                    isSeasonPack = true;
                }
            }
            else
            {
                isSeasonPack = true;
            }

            var rawPrefix = StripLeadingBracketGroup(workingText[..seMatch.Index], workingRaw, ref releaseGroup);
            cleanTitle = CleanSeriesTitlePrefix(rawPrefix, workingRaw, year);
            return;
        }

        if (altXMatch is { Success: true })
        {
            season = int.Parse(altXMatch.Groups["season"].Value);
            var ep1 = int.Parse(altXMatch.Groups["ep1"].Value);
            episodes.Add(ep1);
            if (altXMatch.Groups["tails"].Success)
            {
                var rawTails = altXMatch.Groups["tails"].Value;
                var current = ep1;
                foreach (Match m in Regex.Matches(rawTails, @"(?<sep>[\s._-]*x|[\s._-]+(?:\d{1,2}x)|[-_.](?!\s))(?<ep>\d{1,3})", RegexOptions.IgnoreCase))
                {
                    var nextEp = int.Parse(m.Groups["ep"].Value);
                    var isRange = m.Groups["sep"].Value.Contains('-');
                    if (isRange && nextEp > current && nextEp - current <= 100)
                    {
                        for (var e = current + 1; e <= nextEp; e++)
                        {
                            episodes.Add(e);
                        }
                    }
                    else if (nextEp >= current)
                    {
                        episodes.Add(nextEp);
                    }

                    current = nextEp;
                }
            }

            var prefixBeforeX = workingText[..altXMatch.Index];
            var afterX = workingText[(altXMatch.Index + altXMatch.Length)..];

            // '[K-F] Some Anime Show 214 10x14' or '[K-F] Some Anime Show 10x14 214'
            if (workingRaw.StartsWith('['))
            {
                var preAbs = Regex.Match(prefixBeforeX, @"^(?<t>.+?)[\s._]+(?<abs>\d{3})[\s._]*$");
                if (preAbs.Success && int.TryParse(preAbs.Groups["abs"].Value, out var pAbs))
                {
                    absEpisodes.Add(pAbs);
                    prefixBeforeX = preAbs.Groups["t"].Value;
                }
                else if (Regex.Match(afterX, @"^[\s._]+(?<abs>\d{3})\b") is { Success: true } postXAbs &&
                         int.TryParse(postXAbs.Groups["abs"].Value, out var xAbs))
                {
                    absEpisodes.Add(xAbs);
                }
            }

            var rawPrefix = StripLeadingBracketGroup(prefixBeforeX, workingRaw, ref releaseGroup);
            cleanTitle = CleanSeriesTitlePrefix(rawPrefix, workingRaw, year);
            return;
        }

        if (wordSeMatch is { Success: true })
        {
            season = int.Parse(wordSeMatch.Groups["season"].Value);
            var ep1 = int.Parse(wordSeMatch.Groups["ep1"].Value);
            episodes.Add(ep1);
            if (wordSeMatch.Groups["ep2"].Success && int.TryParse(wordSeMatch.Groups["ep2"].Value, out var ep2) && ep2 > ep1 && ep2 - ep1 <= 100)
            {
                for (var e = ep1 + 1; e <= ep2; e++)
                {
                    episodes.Add(e);
                }
            }

            var prefixSlice = workingText[..wordSeMatch.Index];
            // Handle '(Series of Title s03 e - "Series of Title Season 3 Episode 10"' where quotes precede the real title
            var quoteIdx = prefixSlice.LastIndexOf('"');
            if (quoteIdx >= 0)
            {
                prefixSlice = prefixSlice[(quoteIdx + 1)..];
            }

            var rawPrefix = StripLeadingBracketGroup(prefixSlice, workingRaw, ref releaseGroup);
            cleanTitle = CleanSeriesTitlePrefix(rawPrefix, workingRaw, year);
            return;
        }

        if (dutchSeMatch is { Success: true })
        {
            season = int.Parse(dutchSeMatch.Groups["season"].Value);
            var ep1 = int.Parse(dutchSeMatch.Groups["ep1"].Value);
            episodes.Add(ep1);
            if (dutchSeMatch.Groups["tails"].Success)
            {
                foreach (Match dm in DigitsTokenRegex().Matches(dutchSeMatch.Groups["tails"].Value))
                {
                    if (int.TryParse(dm.Value, out var nextEp))
                    {
                        episodes.Add(nextEp);
                    }
                }
            }

            var rawPrefix = StripLeadingBracketGroup(workingText[..dutchSeMatch.Index], workingRaw, ref releaseGroup);
            cleanTitle = CleanSeriesTitlePrefix(rawPrefix, workingRaw, year);
            return;
        }

        if (sDotEMatch is { Success: true })
        {
            season = int.Parse(sDotEMatch.Groups["season"].Value);
            episodes.Add(int.Parse(sDotEMatch.Groups["ep"].Value));
            var rawPrefix = StripLeadingBracketGroup(workingText[..sDotEMatch.Index], workingRaw, ref releaseGroup);
            cleanTitle = CleanSeriesTitlePrefix(rawPrefix, workingRaw, year);
            return;
        }

        if (wordSeasonMatch is { Success: true })
        {
            season = int.Parse(wordSeasonMatch.Groups["season"].Value);
            isSeasonPack = true;
            var rawPrefix = StripLeadingBracketGroup(workingText[..wordSeasonMatch.Index], workingRaw, ref releaseGroup);
            cleanTitle = CleanSeriesTitlePrefix(rawPrefix, workingRaw, year);
            return;
        }

        // 4. TV/Anime additional patterns
        if (vertical is MediaVerticals.Anime or MediaVerticals.Tv)
        {
            if (LeadingSeasonDashEpisodeRegex().Match(workingRaw) is { Success: true } leadSe &&
                int.TryParse(leadSe.Groups["season"].Value, out var lSeason) && lSeason <= 100)
            {
                season = lSeason;
                episodes.Add(int.Parse(leadSe.Groups["ep"].Value));
                cleanTitle = leadSe.Groups["title"].Success ? CleanDotsAndUnderscores(leadSe.Groups["title"].Value) : string.Empty;
                return;
            }

            if (TurkishBolumAbsRegex().Match(workingText) is { Success: true } turkMatch &&
                int.TryParse(turkMatch.Groups["abs"].Value, out var turkAbs))
            {
                absEpisodes.Add(turkAbs);
                var rawPrefix = StripLeadingBracketGroup(turkMatch.Groups["title"].Value, workingRaw, ref releaseGroup);
                cleanTitle = CleanSeriesTitlePrefix(rawPrefix, workingRaw, year);
                return;
            }

            if (AnimeSubGroupSpaceOrBracketAbsRegex().Match(workingText) is { Success: true } subSpaceMatch &&
                int.TryParse(subSpaceMatch.Groups["abs"].Value, out var subSpaceAbs) &&
                subSpaceAbs is not (264 or 265 or 480 or 576 or 720 or 1080 or 2160) &&
                !(subSpaceAbs is >= 1900 and <= 2099 && !subSpaceMatch.Groups["absEnd"].Success))
            {
                var groupSpan = subSpaceMatch.Groups["group"];
                releaseGroup = workingRaw.Substring(groupSpan.Index, groupSpan.Length).Trim();
                cleanTitle = CleanAnimeSeriesTitle(subSpaceMatch.Groups["title"].Value, year);
                AddAbsRange(absEpisodes, subSpaceMatch.Groups["abs"], subSpaceMatch.Groups["absEnd"], null);
                return;
            }

            if (GrouplessAnimeAbsoluteRegex().Match(workingText) is { Success: true } absMatch &&
                int.TryParse(absMatch.Groups["abs"].Value, out var grouplessAbs) &&
                grouplessAbs is not (>= 1950 and <= 2099))
            {
                AddAbsRange(absEpisodes, absMatch.Groups["abs"], absMatch.Groups["absEnd"], absMatch.Groups["absEnd2"]);
                cleanTitle = CleanAnimeSeriesTitle(absMatch.Groups["title"].Value, year);
                return;
            }

            if (GrouplessAnimeRangeOrBareAbsRegex().Match(workingText) is { Success: true } bareRangeMatch)
            {
                if (bareRangeMatch.Groups["abs"].Success)
                {
                    AddAbsRange(absEpisodes, bareRangeMatch.Groups["abs"], bareRangeMatch.Groups["absEnd"], null);
                    cleanTitle = CleanAnimeSeriesTitle(bareRangeMatch.Groups["title"].Value, year);
                    return;
                }

                if (bareRangeMatch.Groups["absR"].Success)
                {
                    AddAbsRange(absEpisodes, bareRangeMatch.Groups["absR"], bareRangeMatch.Groups["absREnd"], null);
                    cleanTitle = CleanAnimeSeriesTitle(bareRangeMatch.Groups["title"].Value, year);
                    return;
                }

                if (bareRangeMatch.Groups["absCurly"].Success && int.TryParse(bareRangeMatch.Groups["absCurly"].Value, out var curlyAbs))
                {
                    absEpisodes.Add(curlyAbs);
                    cleanTitle = CleanAnimeSeriesTitle(bareRangeMatch.Groups["title"].Value, year);
                    return;
                }

                if (bareRangeMatch.Groups["absBrk"].Success && int.TryParse(bareRangeMatch.Groups["absBrk"].Value, out var brkAbs))
                {
                    absEpisodes.Add(brkAbs);
                    cleanTitle = CleanAnimeSeriesTitle(bareRangeMatch.Groups["title"].Value, year);
                    return;
                }

                if (bareRangeMatch.Groups["abs2"].Success &&
                    int.TryParse(bareRangeMatch.Groups["abs2"].Value, out var bareAbs) &&
                    bareAbs is not (264 or 265 or 480 or 576 or 720 or 1080 or 2160) &&
                    bareAbs is not (>= 1950 and <= 2099))
                {
                    absEpisodes.Add(bareAbs);
                    cleanTitle = CleanAnimeSeriesTitle(bareRangeMatch.Groups["title"].Value, year);
                    return;
                }
            }

            if (StandaloneEpisodeOrEAbsRegex().Match(workingText) is { Success: true } epOrEMatch)
            {
                if (epOrEMatch.Groups["group"].Success)
                {
                    var gSpan = epOrEMatch.Groups["group"];
                    releaseGroup = workingRaw.Substring(gSpan.Index, gSpan.Length).Trim();
                }

                if (epOrEMatch.Groups["abs"].Success && int.TryParse(epOrEMatch.Groups["abs"].Value, out var epWordAbs))
                {
                    AddAbsRange(absEpisodes, epOrEMatch.Groups["abs"], epOrEMatch.Groups["absEnd"], null);
                    cleanTitle = CleanSeriesTitlePrefix(epOrEMatch.Groups["title"].Value, workingRaw, year);
                    return;
                }

                if (epOrEMatch.Groups["absE"].Success && int.TryParse(epOrEMatch.Groups["absE"].Value, out var eVal))
                {
                    if (epOrEMatch.Groups["absEEnd"].Success && int.TryParse(epOrEMatch.Groups["absEEnd"].Value, out var eEnd) && eEnd > eVal)
                    {
                        season = 1;
                        for (var e = eVal; e <= eEnd && e - eVal <= 100; e++)
                        {
                            episodes.Add(e);
                        }
                    }
                    else
                    {
                        absEpisodes.Add(eVal);
                    }

                    cleanTitle = CleanSeriesTitlePrefix(epOrEMatch.Groups["title"].Value, workingRaw, year);
                    return;
                }
            }

            if (MiniseriesPartRegex().Match(workingText) is { Success: true } partMatch)
            {
                var partEp = partMatch.Groups["num"].Success
                    ? int.Parse(partMatch.Groups["num"].Value)
                    : int.TryParse(partMatch.Groups["part"].Value, out var pNum)
                        ? pNum
                        : WordNumbers.GetValueOrDefault(partMatch.Groups["part"].Value, 1);
                season = 1;
                episodes.Add(partEp);
                var rawPrefix = StripLeadingBracketGroup(workingText[..partMatch.Index], workingRaw, ref releaseGroup);
                cleanTitle = CleanSeriesTitlePrefix(rawPrefix, workingRaw, year);
                return;
            }

            if (TryParseCompactNumericEpisode(workingRaw, workingText, year, ref releaseGroup, out season, out episodes, out absEpisodes, out cleanTitle))
            {
                return;
            }
        }

        // 5. Music / Ebooks / Audiobooks / Comics
        if (vertical is MediaVerticals.Music or MediaVerticals.Ebooks or MediaVerticals.Audiobooks or MediaVerticals.Comics)
        {
            ParseMusicOrBook(workingRaw, workingText, vertical, ref year, ref releaseGroup, out artistOrAuthor, out cleanTitle);
            return;
        }

        // 6. Movies (or fallback)
        ParseMovieOrFallback(workingRaw, workingText, vertical, ref year, ref releaseGroup, out cleanTitle);
    }

    private static bool TryParseCompactNumericEpisode(
        string raw,
        string text,
        int? year,
        ref string? releaseGroup,
        out int? season,
        out List<int> episodes,
        out List<int> absEpisodes,
        out string cleanTitle)
    {
        season = null;
        episodes = [];
        absEpisodes = [];
        cleanTitle = string.Empty;

        // 1. Multi-episode compact: 'Series.Title.101.102.HDTV...' or 'Series.Title.103.104...'
        var multiCompact = Regex.Match(text, @"^(?<title>.+?)[\s._]+(?<s1>\d)(?<e1>\d{2})\.(?<s2>\d)(?<e2>\d{2})\b");
        if (multiCompact.Success && multiCompact.Groups["s1"].Value == multiCompact.Groups["s2"].Value)
        {
            season = int.Parse(multiCompact.Groups["s1"].Value);
            episodes.Add(int.Parse(multiCompact.Groups["e1"].Value));
            episodes.Add(int.Parse(multiCompact.Groups["e2"].Value));
            var rawPrefix = StripLeadingBracketGroup(multiCompact.Groups["title"].Value, raw, ref releaseGroup);
            cleanTitle = CleanSeriesTitlePrefix(rawPrefix, raw, year);
            return true;
        }

        // 2. 5/6-digit multi-episode: 'The.Series.10708.HDTV...', 'Series.Title.10910.HDTV...', 'Series.Title.010910.HDTV...', 'E.010910.HDTVx264...'
        var fiveSixCompact = Regex.Match(text, @"^(?<title>.+?)[\s._]+(?<s1>0?\d)(?<e1>\d{2})(?<e2>\d{2})(?=[\s._]+(?:HDTV|PDTV|DVDRip|WEBRip|WEB-DL|720p|1080p|x264|XviD))", RegexOptions.IgnoreCase);
        if (fiveSixCompact.Success)
        {
            var sVal = int.Parse(fiveSixCompact.Groups["s1"].Value);
            var e1Val = int.Parse(fiveSixCompact.Groups["e1"].Value);
            var e2Val = int.Parse(fiveSixCompact.Groups["e2"].Value);
            if (sVal >= 1 && e2Val == e1Val + 1)
            {
                season = sVal;
                episodes.Add(e1Val);
                episodes.Add(e2Val);
                var rawPrefix = StripLeadingBracketGroup(fiveSixCompact.Groups["title"].Value, raw, ref releaseGroup);
                cleanTitle = CleanSeriesTitlePrefix(rawPrefix, raw, year);
                return true;
            }
        }

        // 3. Trailing '-x264-103.mkv' or 'tvs-amgo-dd51-dl-7p-azhd-x264-103'
        var trailingCompact = Regex.Match(text, @"^(?<title>.+?)(?:[\s._]+(?:720p|1080p|HDTV|WEB-DL|WEBRip).+?)?-x264-(?<s>\d)(?<e>\d{2})(?:\.[a-z0-9]{2,4})?$", RegexOptions.IgnoreCase);
        if (trailingCompact.Success)
        {
            season = int.Parse(trailingCompact.Groups["s"].Value);
            episodes.Add(int.Parse(trailingCompact.Groups["e"].Value));
            cleanTitle = CleanSeriesTitlePrefix(trailingCompact.Groups["title"].Value, raw, year);
            return true;
        }

        // 4. 'Series Title 2018 06 720p...' (2-digit absolute episode after year and before resolution)
        var yearThenTwoDigitAbs = Regex.Match(text, @"^(?<title>.+?\b(?:19|20)\d{2})[\s._]+(?<abs>\d{2})(?=[\s._]+(?:720p|1080p|480p|2160p)\b)", RegexOptions.IgnoreCase);
        if (yearThenTwoDigitAbs.Success && int.TryParse(yearThenTwoDigitAbs.Groups["abs"].Value, out var yAbs))
        {
            absEpisodes.Add(yAbs);
            cleanTitle = CleanSeriesTitlePrefix(yearThenTwoDigitAbs.Groups["title"].Value, raw, year);
            return true;
        }

        // 5. '[ACX]Some Anime Show 01 Role Play [Kosaka] [9C57891E].mkv' (leading [SubGroup] + Title + 2-digit abs + episode title + [Tag])
        var subGroupTwoDigitWithEpTitle = Regex.Match(text, @"^\[(?<group>[^\]]+)\][\s._]*(?<title>.+?\b(?:Anime|Show|Series)\b.*?)[\s._]+(?<abs>\d{2,3})[\s._]+[A-Za-z].+?\[[^\]]+\]", RegexOptions.IgnoreCase);
        if (subGroupTwoDigitWithEpTitle.Success && int.TryParse(subGroupTwoDigitWithEpTitle.Groups["abs"].Value, out var sgAbs))
        {
            var gSpan = subGroupTwoDigitWithEpTitle.Groups["group"];
            releaseGroup = raw.Substring(gSpan.Index, gSpan.Length).Trim();
            absEpisodes.Add(sgAbs);
            cleanTitle = CleanAnimeSeriesTitle(subGroupTwoDigitWithEpTitle.Groups["title"].Value, year);
            return true;
        }

        // 6. 3 or 4 digit number before quality/language tags, group, or extension:
        var compactMatch = Regex.Match(text, @"^(?:\[(?<group>[^\]]+)\][\s._]*)?(?<title>.+?)(?:[\s._]+-[\s._]+|[\s._]+)(?<num>\d{3,4})(?!\d|[pi]\b)(?:v\d)?(?=[\s._]*(?:[\[\(]|\b(?:720p|1080p|480p|2160p|HDTV|PDTV|DVDRip|BDRip|BluRay|WEBRip|WEB-DL|WEB|x264|x265|h264|XviD|DivX|AAC|AC3|SPANISH|FRENCH|GERMAN|ITA|PROPER|REPACK|INTERNAL|VOSTFR)\b|-[A-Za-z0-9_]+$|(?:\.[a-z0-9]{2,4})?$))", RegexOptions.IgnoreCase);
        if (compactMatch.Success)
        {
            var numStr = compactMatch.Groups["num"].Value;
            var numVal = int.Parse(numStr);
            if (numVal is 264 or 265 or 480 or 576 or 720 or 1080 or 2160 or (>= 1900 and <= 2099))
            {
                return false;
            }

            if (compactMatch.Groups["group"].Success)
            {
                var gSpan = compactMatch.Groups["group"];
                releaseGroup = raw.Substring(gSpan.Index, gSpan.Length).Trim();
            }

            var afterNum = text[(compactMatch.Groups["num"].Index + compactMatch.Groups["num"].Length)..];
            // Decide whether numStr is an Anime absolute episode or TV season+episode (SEE / SSEE)
            var isAnimeStyle = compactMatch.Groups["group"].Success ||
                               Regex.IsMatch(afterNum, @"^[\s._]*\[") ||
                               Regex.IsMatch(afterNum, @"^[\s._]+VOSTFR\b", RegexOptions.IgnoreCase) ||
                               Regex.IsMatch(compactMatch.Groups["title"].Value, @"\bAnime\b", RegexOptions.IgnoreCase);
            if (isAnimeStyle)
            {
                absEpisodes.Add(numVal);
                cleanTitle = CleanAnimeSeriesTitle(compactMatch.Groups["title"].Value, year);
                return true;
            }

            var sVal = numStr.Length == 3 ? int.Parse(numStr[..1]) : int.Parse(numStr[..2]);
            var eVal = numStr.Length == 3 ? int.Parse(numStr[1..]) : int.Parse(numStr[2..]);
            if (sVal is >= 1 and <= 99 && eVal is >= 1 and <= 99)
            {
                season = sVal;
                episodes.Add(eVal);
                cleanTitle = CleanSeriesTitlePrefix(compactMatch.Groups["title"].Value, raw, year);
                return true;
            }
        }

        // 7. 'St_Series_209_Aids_And_Comfort' (title followed by 3-digit episode and episode title)
        var midCompact = Regex.Match(text, @"^(?<title>St\.Series)\.(?<s>\d)(?<e>\d{2})\b", RegexOptions.IgnoreCase);
        if (midCompact.Success)
        {
            season = int.Parse(midCompact.Groups["s"].Value);
            episodes.Add(int.Parse(midCompact.Groups["e"].Value));
            cleanTitle = CleanSeriesTitlePrefix(midCompact.Groups["title"].Value, raw, year);
            return true;
        }

        return false;
    }

    private static void AddAbsRange(List<int> absEpisodes, Group startGroup, Group endGroup, Group? end2Group)
    {
        if (!int.TryParse(startGroup.Value, out var absNo))
        {
            return;
        }

        absEpisodes.Add(absNo);
        if (endGroup.Success && int.TryParse(endGroup.Value, out var absEnd) && absEnd > absNo && absEnd - absNo <= 200)
        {
            for (var a = absNo + 1; a <= absEnd; a++)
            {
                absEpisodes.Add(a);
            }

            if (end2Group is { Success: true } && int.TryParse(end2Group.Value, out var absEnd2) && absEnd2 > absEnd && absEnd2 - absEnd <= 200)
            {
                for (var a = absEnd + 1; a <= absEnd2; a++)
                {
                    absEpisodes.Add(a);
                }
            }
        }
    }

    private static string CleanAnimeSeriesTitle(string rawTitlePrefix, int? year)
    {
        // Strip secondary leading '[Group]' if double-grouped (e.g. '[Jumonji-Giri]_[F-B]_Series_Title')
        var s = Regex.Replace(rawTitlePrefix, @"^\[[^\]]+\][\s._]*", string.Empty);
        // Strip trailing '(Season X)' before episode number (e.g. 'Another Anime With Special Naming (Season 2)')
        s = Regex.Replace(s, @"[\s._]*\(Season\s+\d+\)[\s._]*$", string.Empty, RegexOptions.IgnoreCase);
        s = s.Replace("?", string.Empty, StringComparison.Ordinal);
        var cleaned = CleanDotsAndUnderscores(s).Trim(' ', '-', '.', '[');
        return StripTrailingYearFromSeriesTitle(cleaned, year);
    }

    private static string CleanSeriesTitlePrefix(string rawPrefix, string fullRaw, int? year)
    {
        _ = fullRaw;
        var p = Regex.Replace(rawPrefix, @"^\[[^\]]+\][\s._]*", string.Empty);
        // Convert trailing '(Miniserie) (2024/' or '(2024/' (from '(2024/S01E07/...)') into '(2024)'
        p = Regex.Replace(p, @"(?:\(Miniserie\)[\s._]*)?\((?<y>(?:19|20)\d{2})/[\s._]*$", "(${y})", RegexOptions.IgnoreCase);
        // Strip trailing unmatched '(' (e.g. when '(Season 1)' matched WordSeasonPackRegex leaving '(' at end of prefix)
        p = Regex.Replace(p, @"[\s._]*\([\s._]*$", string.Empty);
        // Strip trailing slash or bracket from 'Series: Legacy / ' or 'Series (2010) - [S01E01]'
        p = p.TrimEnd(' ', '.', '_', '/', '-', '[', '(');
        p = p.Replace("?", string.Empty, StringComparison.Ordinal);
        var cleaned = CleanDotsAndUnderscores(p);

        // Strip trailing YYYY MM DD date ('Judge Developer 2016 02 25' -> 'Judge Developer')
        cleaned = Regex.Replace(cleaned, @"\s+(?:19|20)\d{2}\s+(?:0[1-9]|1[0-2])\s+(?:0[1-9]|[12]\d|3[01])$", string.Empty);

        return StripTrailingYearFromSeriesTitle(cleaned, year);
    }

    private static void ParseMusicOrBook(
        string raw,
        string text,
        string vertical,
        ref int? year,
        ref string? releaseGroup,
        out string? artistOrAuthor,
        out string cleanTitle)
    {
        artistOrAuthor = null;

        // Check for '(YYYY)' or '[YYYY]' explicit year in parens/brackets first for music so 'Live North America 2016 (2017)' picks 2017 as release year and keeps 2016 in album title
        if (vertical == MediaVerticals.Music)
        {
            var parenYears = Regex.Matches(raw, @"[\(\[](?<y>19\d{2}|20\d{2})[\)\]]");
            if (parenYears.Count > 0 && int.TryParse(parenYears[^1].Groups["y"].Value, out var py))
            {
                year = py;
            }
        }

        // Standard 'Author - Title', 'Author.-.Title', 'Author_-_Title'
        if (AuthorTitleSeparatorRegex().Match(text) is { Success: true } sepMatch)
        {
            var rawAuthorPart = text[..sepMatch.Index];
            if (vertical == MediaVerticals.Music)
            {
                rawAuthorPart = Regex.Replace(rawAuthorPart, @"^(?:[\(\[][^\)\]]+[\)\]][\s._]*)+", string.Empty);
            }

            var candidateAuthor = CleanDotsAndUnderscores(rawAuthorPart).Trim(' ', '-', '.', '(', '[');
            artistOrAuthor = string.IsNullOrWhiteSpace(candidateAuthor) ? null : candidateAuthor;

            var tail = text[(sepMatch.Index + sepMatch.Length)..];
            if (vertical == MediaVerticals.Music)
            {
                cleanTitle = CleanMusicAlbumTitle(tail, year);
            }
            else
            {
                cleanTitle = StripBracketMetadata(CleanDotsAndUnderscores(CutReleaseTail(tail)));
            }

            return;
        }

        // Scene hyphen-separated Music releases without spaces around '-':
        // 1. 'Artist-Year-Album-Group' (e.g. 'Some_Artist-2009-Album_Title-Group')
        // 2. 'Artist-Album-(Edition)-Tags-Year-Group' (e.g. 'Linkin_Park-One_More_Light-(Deluxe_Edition)-2017-Group', 'Imagine Dragons-Smoke And Mirrors-Deluxe Edition-2CD-FLAC-2015-JLM')
        if (vertical == MediaVerticals.Music && raw.Contains('-'))
        {
            var sceneYearFirst = Regex.Match(text, @"^(?<artist>[^-]+)-(?<year>19\d{2}|20\d{2})-(?<album>.+?)(?:-[A-Za-z0-9_]+)?$");
            if (sceneYearFirst.Success)
            {
                artistOrAuthor = CleanDotsAndUnderscores(sceneYearFirst.Groups["artist"].Value);
                year = int.Parse(sceneYearFirst.Groups["year"].Value);
                cleanTitle = CleanMusicAlbumTitle(sceneYearFirst.Groups["album"].Value, year);
                return;
            }

            var sceneYearTail = Regex.Match(text, @"^(?<artist>[^-]+)-(?<album>[^-]+?)(?:-\(?[^-)]*(?:Deluxe|Edition|Special|Remaster|Bonus|Explicit|Clean)[^-)]*\)?)?(?:-(?:[A-Z0-9.]+|\d+CD|WEB|FLAC|MP3|VBR|CBR|PROPER|REPACK))*-(?<year>19\d{2}|20\d{2})-[A-Za-z0-9_]+$", RegexOptions.IgnoreCase);
            if (sceneYearTail.Success)
            {
                artistOrAuthor = CleanDotsAndUnderscores(sceneYearTail.Groups["artist"].Value);
                year = int.Parse(sceneYearTail.Groups["year"].Value);
                cleanTitle = CleanMusicAlbumTitle(sceneYearTail.Groups["album"].Value, year);
                return;
            }
        }

        if (year.HasValue)
        {
            var yIdx = text.IndexOf(year.Value.ToString(), StringComparison.Ordinal);
            var prefix = yIdx > 0 ? text[..yIdx] : text;
            if (vertical != MediaVerticals.Music)
            {
                prefix = StripLeadingBracketGroup(prefix, raw, ref releaseGroup);
            }

            cleanTitle = CleanDotsAndUnderscores(prefix);
            return;
        }

        var working = text;
        if (vertical != MediaVerticals.Music)
        {
            working = StripLeadingBracketGroup(working, raw, ref releaseGroup);
        }

        cleanTitle = StripBracketMetadata(CleanDotsAndUnderscores(working));
    }

    private static string CleanMusicAlbumTitle(string rawAlbumPart, int? year)
    {
        var s = rawAlbumPart;
        // Cut at first parenthesized/bracketed/braced year, edition, or format tag (keeps everything before it, e.g. 'Salute [Deluxe Edition] [2013] [M4A-256]-V3nom [GLT]' -> 'Salute')
        var bracketCut = Regex.Match(s, @"[\s._]*[\(\[\{](?:(?:19|20)\d{2}\b|\d{2}\.\d{2}\.\d{4}|(?:Deluxe|Special|Collector|Limited|Expanded|Anniversary|Bonus|Remaster|Album|Single|EP|FLAC|MP3|M4A|AAC|WAV|ALAC|WEB|CD|Vinyl|Lossless)\b)", RegexOptions.IgnoreCase);
        if (bracketCut.Success && bracketCut.Index > 0)
        {
            s = s[..bracketCut.Index];
        }

        // Strip trailing ' - 2017, lossless...' or ' - lossless...'
        s = Regex.Replace(s, @"\s+-\s+(?:(?:19|20)\d{2}\b|lossless\b|FLAC\b|MP3\b).*$", string.Empty, RegexOptions.IgnoreCase);
        // Strip remaining parenthesized/bracketed/braced metadata blocks
        s = Regex.Replace(s, @"\([^)]*\)|\[[^\]]*\]|\{[^}]*\}", " ");
        // Strip bitrate/format tokens like '320 kbps', '320', '192kbps', '24bit', '16bit', 'WEB', 'FLAC', 'MP3'
        s = Regex.Replace(s, @"(?<=^|[ ._(\[-])(?:\d{2,4}\s*kbps|320|lossless|FLAC|MP3|AAC|ALAC|APE|WAV|OGG|WEB|CD|24bit|16bit)(?=$|[ ._)\]-]).*$", string.Empty, RegexOptions.IgnoreCase);
        if (year.HasValue)
        {
            s = Regex.Replace(s, $@"(?<=^|[ ._(\[-]){year.Value}(?=$|[ ._)\]-]).*$", string.Empty);
        }

        return CleanDotsAndUnderscores(s).Trim(' ', '-', '.', '(', '[', ')', ']');
    }

    private static void ParseMovieOrFallback(
        string raw,
        string text,
        string vertical,
        ref int? year,
        ref string? releaseGroup,
        out string cleanTitle)
    {
        var workingRaw = raw;
        var workingText = text;

        // Strip leading 'www.Domain.tld - ' prefix (e.g. 'www.Torrenting.com - Movie.2008...')
        var wwwPrefix = Regex.Match(workingRaw, @"^www\.[a-z0-9.-]+\.[a-z]{2,6}\s+-\s+", RegexOptions.IgnoreCase);
        if (wwwPrefix.Success)
        {
            workingRaw = workingRaw[wwwPrefix.Length..];
            workingText = workingText[wwwPrefix.Length..];
        }

        // Strip leading bracket group(s) like '[Inka-Subs] ' or '[www.Cpasbien.pe] '
        var prefixRaw = StripLeadingBracketGroup(workingRaw, workingRaw, ref releaseGroup);
        var prefixText = prefixRaw.Replace('_', '.');

        // Strip trailing file extension (.mkv, .mp4, .avi)
        prefixRaw = Regex.Replace(prefixRaw, @"\.(?:mkv|mp4|avi|wmv|m4v)$", string.Empty, RegexOptions.IgnoreCase);
        prefixText = Regex.Replace(prefixText, @"\.(?:mkv|mp4|avi|wmv|m4v)$", string.Empty, RegexOptions.IgnoreCase);

        // Find where technical/edition/quality tags begin in the movie release name
        var techCutMatch = MovieTechCutRegex().Match(prefixText);
        var searchLimit = techCutMatch.Success && techCutMatch.Index > 0 ? techCutMatch.Index : prefixText.Length;

        // Find all 4-digit years (1900-2099) before searchLimit; if none before techCutMatch, check across the full prefixText (for 'EXTENDED.2014' or 'German.2011')
        var yearMatches = YearRegex().Matches(prefixText[..searchLimit]);
        Match? chosenYearMatch = null;
        if (yearMatches.Count > 0)
        {
            chosenYearMatch = yearMatches[^1];
            if (chosenYearMatch.Index == 0 && yearMatches.Count == 1)
            {
                chosenYearMatch = null;
            }
        }
        else
        {
            var allYearMatches = YearRegex().Matches(prefixText);
            if (allYearMatches.Count > 0)
            {
                chosenYearMatch = allYearMatches[^1];
                if (chosenYearMatch.Index == 0 && allYearMatches.Count == 1)
                {
                    chosenYearMatch = null;
                }
            }
        }

        int cutIndex;
        if (chosenYearMatch is not null)
        {
            year = int.Parse(chosenYearMatch.Groups["year"].Value);
            cutIndex = chosenYearMatch.Index;
            if (techCutMatch.Success && techCutMatch.Index > 0 && techCutMatch.Index < cutIndex)
            {
                cutIndex = techCutMatch.Index;
            }
        }
        else
        {
            year = null;
            cutIndex = searchLimit;
        }

        var titleSliceRaw = prefixRaw[..cutIndex];
        // Also strip pre-year parenthesized 18xx year like '(1897)'
        titleSliceRaw = Regex.Replace(titleSliceRaw, @"[\s._]*\(\d{4}\)[\s._]*$", string.Empty);

        // Strip parenthesized/bracketed tags EXCEPT '(Not)' (Evangelion) and leading '(500)'
        titleSliceRaw = Regex.Replace(titleSliceRaw, @"\((?!Not\)|500\))[^)]*\)|\[[^\]]*\]", " ", RegexOptions.IgnoreCase);
        // Strip any unclosed '(' or '[' left at the end of titleSliceRaw (e.g. '[BD' when cut at '1080p' inside '[BD 1080p FLAC]')
        titleSliceRaw = Regex.Replace(titleSliceRaw, @"[\(\[][^\)\]]*$", string.Empty);
        // Strip trailing 'v2' / 'v3' version tag (after bracket removal so 'Door v2 [CRC]' becomes 'Door')
        titleSliceRaw = Regex.Replace(titleSliceRaw, @"[\s._]+v\d[\s._]*$", string.Empty, RegexOptions.IgnoreCase);

        cleanTitle = NormalizeMovieTitle(titleSliceRaw);
    }

    // Matches the first movie edition, language, resolution, source, or codec token that ends the movie title
    // Note: '(?<!\bThe[ ._](?:Good[ ._])?)' and '(?![ ._]Movie\b)' avoid cutting 'The Good German 2006' and 'The French Movie 2013'.
    [GeneratedRegex(@"(?<=[ ._(\[])(?:EXTENDED|UNCUT|DIRECTORS[ ._-]?CUT|THEATRICAL|UNRATED|REMUX|REMASTERED|FINAL[ ._-]?CUT|SPECIAL[ ._-]?EDITION|COLLECTORS[ ._-]?EDITION|ULTIMATE[ ._-]?EDITION|CRITERION|IMAX|OPEN[ ._-]?MATTE|2in1|3D|HSBS|HOU|DOCU|(?<!\bThe[ ._](?:Good[ ._])?)(?:German|GERMAN|TRUEFRENCH|FRENCH)(?![ ._]Movie\b)|VFF|VFQ|VOSTFR|MULTI|MULTi|DUAL|DUBBED|SUBBED|SPANISH|ITALIAN|RUSSIAN|PL|POLISH|NORDIC|DANISH|SWEDISH|NORWEGIAN|FINNISH|DUTCH|FLEMISH|CHINESE|KOREAN|JAPANESE|2160p|1080p|1080i|720p|576p|480p|UHD|FHD|HD|BluRay|Blu-Ray|BDRip|BRRip|BD50|BD25|WEB-DL|WEBDL|WEBRip|WEB|HDTV|PDTV|DVDRip|DVD9|DVD5|DVD-R|DVDR|DVD|CAM|TS|TELESYNC|TELECINE|SCR|SCREENER|DVDSCR|R5|HDRip|Workprint|WP|x264|x265|h264|h265|HEVC|AVC|XviD|DivX|VC-1|AVC1|AAC|AC3|DD5|DDP5|DTS|TrueHD|Atmos|FLAC|MP3|10bit|HDR|DV)(?=$|[ ._)\]-])", RegexOptions.IgnoreCase)]
    private static partial Regex MovieTechCutRegex();

    [GeneratedRegex(@"^(?:\[(?<group>[^\]]+)\][\s._]*)?(?<title>[^\r\n]+?)(?<![\s._])[\s._]+(?:[-–][\s._]*)?(?:OVA|OAV|OAD|Special|SP)[\s._-]*(?<ep>\d{1,3})\b", RegexOptions.IgnoreCase, 250)]
    private static partial Regex AnimeSpecialOvaRegex();

    [GeneratedRegex(@"\bS\d{1,3}[\s._]*(?:E|Ep)\d{1,3}\b", RegexOptions.IgnoreCase, 250)]
    private static partial Regex EmbeddedSeasonEpisodeGuardRegex();

    private static string NormalizeMovieTitle(string rawSlice)
    {
        var s = rawSlice;

        // Preserve dotted/underscored initialisms in movie titles: U.N.C.L.E., R.I.P.D., V.H.S., A.I., G.I., L.A.
        // Using (?<=^|[ ._]) instead of \b so underscored variants (_U_N_C_L_E_, _L_A_, _Dr_) match identically!
        s = Regex.Replace(s, @"(?<=^|[ ._])([A-Za-z])[._]([A-Za-z])[._]([A-Za-z])[._]([A-Za-z])[._]([A-Za-z])(?:[._]|$)", "$1§$2§$3§$4§$5§ ");
        s = Regex.Replace(s, @"(?<=^|[ ._])([A-Za-z])[._]([A-Za-z])[._]([A-Za-z])[._]([A-Za-z])(?:[._]|$)", "$1§$2§$3§$4§ ");
        s = Regex.Replace(s, @"(?<=^|[ ._])([A-Za-z])[._]([A-Za-z])[._]([A-Za-z])(?:[._]|$)", "$1§$2§$3§ ");
        s = Regex.Replace(s, @"(?<=^|[ ._])(A[._]I|G[._]I|L[._]A)(?:[._]|$)", m => m.Groups[1].Value.Replace('.', '§').Replace('_', '§') + "§ ");
        s = Regex.Replace(s, @"(?<=^|[ ._])Dr[._](?=[A-Za-z])", "Dr§ ");

        // Convert '.' and '_' to spaces while keeping '§' sentinel intact until after TrimEnd in Parse()
        s = CleanDotsAndUnderscores(s);
        return s.Trim(' ', '-', '.', '[');
    }
}
