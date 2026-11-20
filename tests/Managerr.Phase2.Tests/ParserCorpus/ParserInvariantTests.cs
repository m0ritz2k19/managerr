using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Managerr.Common.Media;
using Managerr.Downloaders.Parser;
using Managerr.Indexers.Categories;
using Xunit;

namespace Managerr.Phase2.Tests.ParserCorpus;

/// <summary>
/// Properties the parser must keep for every input, checked over the whole corpus (including the upstream
/// CrapParserFixture junk), every vertical hint, and a seeded stream of generated and mangled names.
/// A failure prints the offending input, so it can be added to managerr.jsonl as a regression.
/// </summary>
public sealed partial class ParserInvariantTests
{
    private const int FuzzSeed = 20261105;
    private const int GeneratedCount = 4000;
    private const int ShownViolations = 40;

    /// <summary>
    /// Invariants the parser breaks today. They are reported by PendingInvariants_AreStillBroken instead of failing
    /// the build; once one holds everywhere that test fails, so it can be removed from here and stays enforced.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> PendingInvariants =
        new Dictionary<string, string>();

    private sealed record Violation(string Invariant, string Message);

    private static readonly string?[] Hints = [null, .. MediaVerticals.All];

    private static readonly Lazy<Violation[]> CorpusViolations = new(() => ParserCorpus.Cases
        .Select(c => c.Release)
        .Distinct(StringComparer.Ordinal)
        .SelectMany(r => Hints.Select(h => (Release: r, Hint: h)))
        .SelectMany(x => Violations(x.Release, x.Hint))
        .ToArray());

    private static readonly Lazy<Violation[]> GeneratedViolations = new(FindGeneratedViolations);

    [Fact]
    public void CorpusReleases_UnderEveryHint_KeepTheInvariants() => AssertNone(CorpusViolations.Value);

    [Fact]
    public void GeneratedAndMangledReleases_KeepTheInvariants() => AssertNone(GeneratedViolations.Value);

    [Fact]
    public void PendingInvariants_AreStillBroken()
    {
        var all = CorpusViolations.Value.Concat(GeneratedViolations.Value).ToArray();
        var fixedNow = PendingInvariants.Keys.Where(k => all.All(v => v.Invariant != k)).ToArray();
        Assert.True(fixedNow.Length == 0,
            $"No input breaks [{string.Join(", ", fixedNow)}] any more. Remove it from PendingInvariants so it stays enforced.");
    }

    [Theory]
    [InlineData("EPUB")]
    [InlineData("AZW3")]
    [InlineData("MOBI")]
    [InlineData("M4B")]
    [InlineData("CBZ")]
    [InlineData("CBR")]
    public void BookFormatMarker_IsNeverFiledAsAMovie(string format)
    {
        // #53: an EPUB arriving under a movies category (or none) became a movie work
        var random = new Random(FuzzSeed);
        var misfiled = Enumerable.Range(0, 200)
            .Select(_ => $"{Words(random, 1, 3, '.')}.-.{Words(random, 1, 4, '.')}.{random.Next(1950, 2027)}.{format}-{Group(random)}")
            .SelectMany(r => new[] { null, MediaVerticals.Movies }.Select(h => (Release: r, Hint: h)))
            .Where(x => Parse(x.Release, x.Hint).MediaVertical == MediaVerticals.Movies)
            .Select(x => new Violation("book-not-movie", $"{Describe(x.Release, x.Hint)} parsed as movies"))
            .ToArray();

        AssertNone(misfiled);
    }

    // ---------------------------------------------------------------------------------------------------

    private static Violation[] FindGeneratedViolations()
    {
        var random = new Random(FuzzSeed);
        var corpus = ParserCorpus.Cases.Select(c => c.Release).Distinct(StringComparer.Ordinal).ToArray();
        var generated = Enumerable.Range(0, GeneratedCount)
            .Select(i => i % 2 == 0 ? Generate(random) : Mangle(corpus[random.Next(corpus.Length)], random))
            .Select(r => (Release: r, Hint: Hints[random.Next(Hints.Length)]))
            .ToArray();

        // Edge cases (and the reproducers of pending invariants) run under every hint, independent of the seed
        var edges = EdgeCases.SelectMany(r => Hints.Select(h => (Release: r, Hint: h)));
        return generated.Concat(edges).SelectMany(x => Violations(x.Release, x.Hint)).ToArray();
    }

    private static IEnumerable<Violation> Violations(string release, string? hint)
    {
        ParsedReleaseInfo first;
        try
        {
            first = Parse(release, hint);
        }
        catch (Exception ex)
        {
            return [new Violation("no-throw", $"{Describe(release, hint)} threw {ex.GetType().Name}: {ex.Message}")];
        }

        var problems = new List<Violation>();
        void Check(string invariant, bool ok, string what)
        {
            if (!ok)
            {
                problems.Add(new Violation(invariant, $"{Describe(release, hint)} {what}"));
            }
        }

        var again = Parse(release, hint);
        Check("deterministic", JsonSerializer.Serialize(first) == JsonSerializer.Serialize(again), "parses differently on a second call");

        Check("vertical-known", MediaVerticals.All.Contains(first.MediaVertical), $"has unknown vertical '{first.MediaVertical}'");
        Check("title-trimmed", first.CleanTitle == first.CleanTitle.Trim(), $"has untrimmed title '{first.CleanTitle}'");
        Check("title-double-space", !first.CleanTitle.Contains("  ", StringComparison.Ordinal), $"has a double space in title '{first.CleanTitle}'");
        Check("author-empty", first.ArtistOrAuthor is null || first.ArtistOrAuthor.Length > 0, "has an empty author");
        Check("year-plausible", first.Year is null or (>= 1900 and <= 2099), $"has implausible year {first.Year}");
        Check("season-plausible", first.SeasonNumber is null or (>= 0 and <= 100), $"has implausible season {first.SeasonNumber}");

        Check("episodes-ascending", IsStrictlyAscending(first.EpisodeNumbers), $"has unordered or duplicate episodes [{string.Join(",", first.EpisodeNumbers)}]");
        Check("absolute-ascending", IsStrictlyAscending(first.AbsoluteEpisodeNumbers), $"has unordered or duplicate absolute numbers [{string.Join(",", first.AbsoluteEpisodeNumbers)}]");
        Check("episodes-need-season", first.EpisodeNumbers.Count == 0 || first.SeasonNumber is not null, "has episodes without a season");
        Check("season-pack-shape", !first.IsFullSeasonPack || (first.SeasonNumber is not null && first.EpisodeNumbers.Count == 0),
            "is a season pack without a season, or with episodes");

        Check("languages-sorted", first.DetectedLanguages.SequenceEqual(first.DetectedLanguages.Distinct().Order(StringComparer.Ordinal)), "has unsorted or duplicate languages");

        var quality = first.ToQualityName();
        Check("quality-present", !string.IsNullOrWhiteSpace(quality), "has an empty quality name");
        Check("quality-canonical", QualityNames.Canonicalize(quality) == quality, $"has non-canonical quality '{quality}' (#48)");

        // Format fields belong to their verticals only
        Check("music-format-vertical", first.MusicFormat is null || first.MediaVertical == MediaVerticals.Music, $"has a music format on a {first.MediaVertical} release");
        Check("book-format-vertical", first.BookFormat is null || first.MediaVertical is MediaVerticals.Ebooks or MediaVerticals.Audiobooks or MediaVerticals.Comics,
            $"has a book format on a {first.MediaVertical} release");
        Check("edition-vertical", first.MovieEdition is null || first.MediaVertical == MediaVerticals.Movies, $"has an edition on a {first.MediaVertical} release");

        // The release group is taken from the name; a trailing '-x264'/'-EPUB' is a tag, not a group
        Check("group-from-name", first.ReleaseGroup is null || (first.ReleaseGroup.Length > 0 && release.Contains(first.ReleaseGroup, StringComparison.Ordinal)),
            $"has release group '{first.ReleaseGroup}' that is not in the name");
        Check("group-not-codec", first.ReleaseGroup is null || release.TrimStart().StartsWith('[') || !CodecTokenRegex().IsMatch(first.ReleaseGroup),
            $"has codec '{first.ReleaseGroup}' as release group");

        return problems;
    }

    [GeneratedRegex(@"^(x264|x265|EPUB)$", RegexOptions.IgnoreCase)]
    private static partial Regex CodecTokenRegex();

    private static ParsedReleaseInfo Parse(string release, string? hint) => new ReleaseParserRouter().Parse(release, hint);

    private static bool IsStrictlyAscending(IReadOnlyList<int> values)
    {
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] <= values[i - 1])
            {
                return false;
            }
        }

        return true;
    }

    private static string Describe(string release, string? hint) => $"'{release}' (hint {hint ?? "none"})";

    private static void AssertNone(IEnumerable<Violation> violations)
    {
        var enforced = violations.Where(v => !PendingInvariants.ContainsKey(v.Invariant)).ToArray();
        if (enforced.Length > 0)
        {
            Assert.Fail($"{enforced.Length} invariant violation(s):\n  " +
                        string.Join("\n  ", enforced.Take(ShownViolations).Select(v => $"[{v.Invariant}] {v.Message}")) +
                        (enforced.Length > ShownViolations ? $"\n  ... and {enforced.Length - ShownViolations} more" : ""));
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Input generation (seeded, so a failure reproduces)
    // ---------------------------------------------------------------------------------------------------

    private static readonly string[] TitleWords =
    [
        "The", "Series", "Title", "Movie", "of", "and", "Night", "Wind", "Truth", "Spider-Man", "100", "2", "II", "Mr", "Dr",
        "1984", "2001", "Ünïcödé", "Café", "Shōgun", "進撃の巨人", "Español", "S.H.I.E.L.D", "A.I", "24", "1080", "Part",
    ];

    private static readonly string[] Tags =
    [
        "1080p", "720p", "2160p", "480p", "4K", "UHD", "WEB-DL", "WEBRip", "WEB", "BluRay", "REMUX", "HDTV", "BDRip", "x264", "x265",
        "HEVC", "AVC", "AV1", "H.264", "DDP5.1", "DD+", "Atmos", "TrueHD", "DTS-HD.MA", "AAC2.0", "FLAC", "24bit", "320", "V0",
        "DV", "HDR10+", "HDR", "German", "DL", "MULTi", "ENG", "VOSTFR", "Dual-Audio", "PROPER", "REPACK", "iNTERNAL", "Extended",
        "Directors.Cut", "IMAX", "Unabridged", "EPUB", "M4B", "CBZ", "AZW3", "PDF", "MOBI", "Retail", "COMPLETE", "Season.3", "Staffel.2",
    ];

    private static readonly string[] EdgeCases =
    [
        "", " ", ".", "-", "_", "[]", "()", "[", "(", "- -", ".-.", "_-_", " - ", "S01E01", "1x01", "2024", "-GRP", "[Group]", "[Group] - 01",
        "S99E999", "S01E01-E99", "S01E99-E01", "S1E1E2E3E4E5E6E7E8E9E10", "Title - 0000 [1080p]", "Title - 9999 (1080p)",
        "Title.S01E01.S01E01.1080p", "Title 1x01-1x05", "Series.S01E02E01.720p.HDTV-GRP", new string('a', 5000), new string('.', 1000), string.Concat(Enumerable.Repeat("S01E01.", 300)),
        "Title\tS01E01\n1080p", "Title.S01E01.1080p\u0000", "‮Title.S01E01", "Ｔｉｔｌｅ．Ｓ０１Ｅ０１", "Title.(((((2024", "Title.[[[[[2024",
    ];

    private static string Words(Random r, int min, int max, char sep) =>
        string.Join(sep, Enumerable.Range(0, r.Next(min, max + 1)).Select(_ => TitleWords[r.Next(TitleWords.Length)]));

    private static string Group(Random r) => new[] { "GRP", "FLUX", "NTb", "TESTLAB", "x264", "EPUB", "Ab_C", "1", "LONGGROUPNAMEOVERTWENTYCHARS" }[r.Next(9)];

    private static string Generate(Random r)
    {
        var sep = new[] { '.', ' ', '_' }[r.Next(3)];
        var sb = new StringBuilder();
        if (r.Next(5) == 0)
        {
            sb.Append($"[{Group(r)}] ");
        }

        sb.Append(Words(r, 1, 5, sep));
        sb.Append(r.Next(6) switch
        {
            0 => $"{sep}S{r.Next(0, 30):00}E{r.Next(0, 150):00}",
            1 => $"{sep}S{r.Next(0, 30):00}E{r.Next(1, 30):00}-E{r.Next(1, 30):00}",
            2 => $"{sep}{r.Next(1, 30)}x{r.Next(1, 30):00}",
            3 => $"{sep}-{sep}{r.Next(1, 1200):000}",
            4 => $"{sep}-{sep}{Words(r, 1, 3, sep)}",
            _ => "",
        });
        if (r.Next(2) == 0)
        {
            sb.Append($"{sep}{(r.Next(4) == 0 ? "(" : "")}{r.Next(1890, 2110)}{(r.Next(4) == 0 ? ")" : "")}");
        }

        for (var i = r.Next(0, 7); i > 0; i--)
        {
            sb.Append(sep).Append(Tags[r.Next(Tags.Length)]);
        }

        if (r.Next(3) > 0)
        {
            sb.Append('-').Append(Group(r));
        }

        if (r.Next(4) == 0)
        {
            sb.Append(new[] { ".mkv", ".epub", ".nzb", " [ABCD1234]", "-xpost", ".m4b" }[r.Next(6)]);
        }

        return sb.ToString();
    }

    private static string Mangle(string release, Random r)
    {
        var chars = release.ToList();
        for (var i = r.Next(1, 4); i > 0 && chars.Count > 0; i--)
        {
            var at = r.Next(chars.Count);
            switch (r.Next(5))
            {
                case 0: chars.RemoveAt(at); break;
                case 1: chars.Insert(at, "-._ []()0123456789Ee"[r.Next(20)]); break;
                case 2: chars[at] = char.IsUpper(chars[at]) ? char.ToLowerInvariant(chars[at]) : char.ToUpperInvariant(chars[at]); break;
                case 3: chars = chars.Take(at).ToList(); break;
                default: chars.InsertRange(at, Tags[r.Next(Tags.Length)] + "."); break;
            }
        }

        return new string(chars.ToArray());
    }
}
