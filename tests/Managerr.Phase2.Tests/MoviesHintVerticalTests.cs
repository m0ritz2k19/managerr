using Managerr.Downloaders.Parser;
using Xunit;

namespace Managerr.Phase2.Tests;

public sealed class MoviesHintVerticalTests
{
    [Theory]
    [InlineData("Brandon.Sanderson.-.Wind.and.Truth.2024.German.EPUB-TESTLAB", "ebooks")]
    [InlineData("Some.Author.-.Some.Book.2023.AZW3-GRP", "ebooks")]
    [InlineData("Some.Author.-.Some.Book.2023.Unabridged.M4B-GRP", "audiobooks")]
    [InlineData("Saga.Vol.01.2012.Digital.CBZ-GRP", "comics")]
    [InlineData("Pioneer.One.2010.S01E02.German.DL.1080p.WEB-DL.H264-TESTLAB", "tv")]
    [InlineData("Neon.Horizon.2025.German.DL.1080p.BluRay.x265-TESTLAB", "movies")]
    public void MoviesCategoryHint_IsOverriddenByExplicitFormatMarkers(string title, string expectedVertical)
    {
        Assert.Equal(expectedVertical, new ReleaseParserRouter().Parse(title, "movies").MediaVertical);
    }
}
