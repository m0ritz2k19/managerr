using Managerr.Profiles;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Managerr.Phase4.Tests;

public sealed class ExistingQualityNameUpgradeTests
{
    private const string GermanDlWebDl1080p = "Pioneer.One.2010.S01E02.The.Man.From.Mars.German.DL.1080p.WEB-DL.H264-TESTLAB";

    [Theory]
    // Disk-scan / rename spelling of the same quality the candidate has
    [InlineData(GermanDlWebDl1080p, "WEB-DL-1080p")]
    [InlineData(GermanDlWebDl1080p, "WEBDL-1080p")]
    // 'WEBDL-480p' (old importer spelling) vs profiles' 'SD-480p' is covered by QualityNameCanonicalizationTests;
    // no seeded profile allows 480p, so it can't be exercised here without a custom profile.
    public async Task SameRelease_IsNotAnUpgrade_RegardlessOfStoredQualitySpelling(string releaseTitle, string storedQuality)
    {
        var card = await SimulateAgainstExistingAsync(releaseTitle, storedQuality);

        Assert.False(card.Approved, $"{card.DecisionKind}: {card.Explanation}");
        Assert.Equal("CutoffsAlreadyMet", card.DecisionKind);
    }

    [Theory]
    [InlineData("MKV")]
    [InlineData("Standard")]
    public async Task ExistingFileWithUnknownQuality_IsUpgradeable(string storedQuality)
    {
        var card = await SimulateAgainstExistingAsync(GermanDlWebDl1080p, storedQuality);

        Assert.True(card.Approved, $"{card.DecisionKind}: {card.Explanation}");
    }

    private static async Task<ReleaseSimulationScorecard> SimulateAgainstExistingAsync(string releaseTitle, string storedQuality)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-quality-names-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Managerr:ConfigDirectory"] = tempDir,
                        ["Managerr:MasterKey"] = "quality-names-test-master-key-32byte",
                        ["Managerr:SchedulerEnabled"] = "false",
                    });
                });
            });

            using var scope = factory.Services.CreateScope();
            var profileSvc = scope.ServiceProvider.GetRequiredService<IComposableProfileService>();
            var germanBest = Assert.Single(await profileSvc.ListProfilesAsync(), p => p.Slug == "german-first-best-quality");

            return await profileSvc.SimulateAsync(new SimulateReleaseRequest(
                ProfileId: germanBest.Id,
                ReleaseTitle: releaseTitle,
                OriginalLanguage: "en",
                ExistingFileQuality: storedQuality,
                ExistingFileAudioLanguages: ["de", "en"],
                ExistingFileSubtitleLanguages: ["de"],
                ExistingVideoCodec: "AVC",
                MediaVertical: "tv"));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }
}
