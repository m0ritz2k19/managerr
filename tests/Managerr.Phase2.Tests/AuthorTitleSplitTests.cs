using Managerr.Downloaders.Parser;
using Xunit;

namespace Managerr.Phase2.Tests;

public sealed class AuthorTitleSplitTests
{
    [Theory]
    // The testlab TC-U8 release that was filed under author "Unknown"
    [InlineData("Brandon.Sanderson.-.Wind.and.Truth.2024.German.EPUB-TESTLAB", "ebooks", "Brandon Sanderson", "Wind and Truth")]
    [InlineData("Brandon_Sanderson_-_Wind_and_Truth_2024_EPUB", "ebooks", "Brandon Sanderson", "Wind and Truth")]
    [InlineData("Brandon Sanderson - Wind and Truth 2024 German EPUB", "ebooks", "Brandon Sanderson", "Wind and Truth")]
    [InlineData("Brandon Sanderson - Wind and Truth (2024) [EPUB]", "ebooks", "Brandon Sanderson", "Wind and Truth")]
    [InlineData("Stephen.King.-.The.Stand.Unabridged.M4B-GRP", "audiobooks", "Stephen King", "The Stand")]
    [InlineData("Imagine.Dragons.-.Night.Visions.2012.FLAC.24bit-GRP", "music", "Imagine Dragons", "Night Visions")]
    // Titles that start with a year keep it
    [InlineData("George.Orwell.-.1984.2021.German.EPUB-GRP", "ebooks", "George Orwell", "1984")]
    [InlineData("Arthur.C.Clarke.-.2001.A.Space.Odyssey.1968.EPUB", "ebooks", "Arthur C Clarke", "2001 A Space Odyssey")]
    // No year and no tag: nothing is cut, hyphenated titles survive
    [InlineData("Stan Lee - Spider-Man", "comics", "Stan Lee", "Spider-Man")]
    public void AuthorAndTitle_AreSplitForSpacedDottedAndUnderscoredNames(string release, string vertical, string author, string title)
    {
        var parsed = new ReleaseParserRouter().Parse(release, vertical);

        Assert.Equal(author, parsed.ArtistOrAuthor);
        Assert.Equal(title, parsed.CleanTitle);
    }

    [Fact]
    public void Movies_AreNotSplitOnDashes()
    {
        var parsed = new ReleaseParserRouter().Parse("Spider-Man.-.Far.From.Home.2019.1080p.BluRay.x264-GRP", "movies");

        Assert.Null(parsed.ArtistOrAuthor);
    }
}
