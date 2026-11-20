using Managerr.Downloaders.Parser;
using Xunit;

namespace Managerr.Phase2.Tests;

public sealed class AnimeAbsoluteNumberingParserTests
{
    private readonly ReleaseParserRouter _parser = new();

    [Theory]
    [InlineData("Naruto - 001 [Triple-Audio][Multi-Sub][HEVC 10Bits] 800p", "Naruto", 1)]
    [InlineData("Frieren - 28v2 [1080p]", "Frieren", 28)]
    [InlineData("One Piece - 1080 (1080p)", "One Piece", 1080)]
    [InlineData("Dandadan - 05 (WEB 1080p HEVC AAC)", "Dandadan", 5)]
    [InlineData("Mob Psycho 100 - 03 [720p]", "Mob Psycho 100", 3)]
    [InlineData("Spy x Family (2022) - 12 [1080p]", "Spy x Family", 12)]
    [InlineData("Naruto.-.030.[480p].mkv", "Naruto", 30)]
    [InlineData("Bocchi the Rock! - 12 END", "Bocchi the Rock!", 12)]
    [InlineData("[uP] Naruto - 030 (BDRip 1080p x264 AC3 Multi).mkv", "Naruto", 30)]
    public void AbsoluteEpisode_IsParsed_WithOrWithoutLeadingGroup(string title, string expectedClean, int expectedAbsolute)
    {
        var parsed = _parser.Parse(title, "anime");

        Assert.Equal(expectedClean, parsed.CleanTitle);
        Assert.Equal([expectedAbsolute], parsed.AbsoluteEpisodeNumbers);
        Assert.Null(parsed.SeasonNumber);
        Assert.Empty(parsed.EpisodeNumbers);
    }

    [Theory]
    [InlineData("Blade Runner - 2049 (2017) [1080p]")]
    [InlineData("Some Show - 1080p [WEB-DL]")]
    [InlineData("Some Show - 720i (HDTV)")]
    public void YearsAndResolutions_AreNotAbsoluteEpisodes(string title)
    {
        var parsed = _parser.Parse(title, "anime");

        Assert.Empty(parsed.AbsoluteEpisodeNumbers);
    }

    [Fact]
    public void SeasonEpisodeNumbering_TakesPrecedenceOverDashNumber()
    {
        var parsed = _parser.Parse("Attack on Titan - S04E28 - 87 [1080p]", "anime");

        Assert.Equal(4, parsed.SeasonNumber);
        Assert.Equal([28], parsed.EpisodeNumbers);
        Assert.Empty(parsed.AbsoluteEpisodeNumbers);
    }

    [Fact]
    public void MoviesVertical_IsNotParsedAsAbsoluteEpisode()
    {
        var parsed = _parser.Parse("Spider-Man - 2002 Remastered (1080p)", "movies");

        Assert.Empty(parsed.AbsoluteEpisodeNumbers);
    }
}
