using Managerr.Common.Media;
using Managerr.Downloaders.Parser;
using Xunit;

namespace Managerr.Phase2.Tests;

public sealed class QualityNameCanonicalizationTests
{
    [Theory]
    [InlineData("WEB-DL-1080p", "WEBDL-1080p")]
    [InlineData("WEBDL-1080p", "WEBDL-1080p")]
    [InlineData("web-dl 2160p", "WEBDL-2160p")]
    [InlineData("WEBRip-720p", "WEBRip-720p")]
    [InlineData("Blu-Ray-1080p", "BluRay-1080p")]
    [InlineData("Remux-2160p", "Remux-2160p")]
    [InlineData("WEBDL-480p", "SD-480p")]
    [InlineData("HDTV-576p", "SD-480p")]
    [InlineData("SD-480p", "SD-480p")]
    [InlineData("MKV", QualityNames.Unknown)]
    [InlineData("Standard", QualityNames.Unknown)]
    [InlineData("", QualityNames.Unknown)]
    [InlineData(null, QualityNames.Unknown)]
    [InlineData("FLAC 24bit", "FLAC 24bit")]
    [InlineData("MP3-320", "MP3-320")]
    [InlineData("EPUB", "EPUB")]
    public void Canonicalize_MapsLegacyAndAliasNames(string? stored, string expected)
    {
        Assert.Equal(expected, QualityNames.Canonicalize(stored));
    }

    [Theory]
    [InlineData("Pioneer.One.2010.S01E02.The.Man.From.Mars.German.DL.1080p.WEB-DL.H264-TESTLAB", "WEBDL-1080p")]
    [InlineData("Naruto.2002.S01E035.480p.NF.WEB-DL.H.264.AAC.2Audios-UBWEB", "SD-480p")]
    [InlineData("Blade.Runner.2049.German.DL.2160p.UHD.BluRay.Remux.HEVC-GRP", "Remux-2160p")]
    [InlineData("Pioneer One S01E02-The Man From Mars", "WEBDL-1080p")]
    public void ReleaseTitle_MapsToCanonicalQuality(string releaseTitle, string expected)
    {
        var parsed = new ReleaseParserRouter().Parse(releaseTitle, "tv");

        Assert.Equal(expected, parsed.ToQualityName());
        // Whatever a release is named as a candidate is already canonical when stored for its file
        Assert.Equal(parsed.ToQualityName(), QualityNames.Canonicalize(parsed.ToQualityName()));
    }
}
