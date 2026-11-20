using System.Text.Json;
using Dapper;
using Managerr.Catalog;
using Managerr.Datastore;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
using Managerr.Downloaders.Organizer;
using Managerr.Downloaders.Parser;
using Managerr.Host.Services;
using Managerr.Indexers.Categories;
using Managerr.Metadata;
using Managerr.Profiles;
using Managerr.Verification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Managerr.Phase6.Tests;

public sealed class CompletedDownloadLanguageAndFolderPreservationTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public void BuildExpectedAudioLanguages_ResolvesGermanDL_AccuratelyPerOriginalLanguage()
    {
        var parser = new ReleaseParserRouter();

        // 1. Anime Japanese release with .German.ANiME.DL.
        // OriginalLanguage = "ja" -> Expected audio must be ["de", "ja"] (not falsely claiming "en"!)
        var animeParsed = parser.Parse(
            "FantasyQuest.2017.S02E02.German.ANiME.DL.AAC.1080p.BDRiP.AV1-SD7",
            MediaVerticals.Anime);
        var animeExpected = CompletedDownloadImportService.BuildExpectedAudioLanguages(animeParsed, [], "ja");
        Assert.Contains("de", animeExpected);
        Assert.Contains("ja", animeExpected);
        Assert.DoesNotContain("en", animeExpected);

        // 2. Explicit English token present in release title (.German.English.DL.) -> Must retain "en"
        var enExplicitParsed = parser.Parse(
            "FantasyQuest.S02E02.German.English.DL.1080p.BluRay",
            MediaVerticals.Anime);
        var enExplicitExpected = CompletedDownloadImportService.BuildExpectedAudioLanguages(enExplicitParsed, [], "ja");
        Assert.Contains("de", enExplicitExpected);
        Assert.Contains("en", enExplicitExpected);

        // 3. Breaking Bad Western TV release with .German.DL.
        // OriginalLanguage = "en" -> Expected audio must be ["de", "en"]
        var tvParsed = parser.Parse(
            "Breaking.Bad.S01E01.German.DL.1080p",
            MediaVerticals.Tv);
        var tvExpected = CompletedDownloadImportService.BuildExpectedAudioLanguages(tvParsed, [], "en");
        Assert.Contains("de", tvExpected);
        Assert.Contains("en", tvExpected);
    }

    [Fact]
    public void BuildExpectedAudioLanguages_WithEffectivePolicy_DemandsOnlyPrimaryWantedLanguage()
    {
        var parser = new ReleaseParserRouter();
        var germanFirstPolicy = new LanguagePolicySummary(
            Id: 1,
            Slug: "german-first",
            Name: "German First",
            Description: "",
            IsSystemPreset: true,
            Tiers:
            [
                new LanguagePolicyTier(1, "de-orig", "German + Orig", ["de", "original"], [], [], 1100, true),
                new LanguagePolicyTier(2, "de", "German Only", ["de"], [], [], 950, true),
            ],
            EnableLanguageUpgrades: true,
            LanguageCutoffTier: 2,
            TradeoffMode: "bounded_stepdown",
            MaxQualityTierDrop: 2,
            MinimumAbsoluteQuality: "WEBDL-1080p",
            PreservePreviousAsAlternate: false,
            VerificationMode: "verify_content_on_upgrades_and_ambiguous",
            CreatedAt: "",
            UpdatedAt: "");

        // When German is wanted, a German release (.German.DL., .German.English.DL., etc.) only demands ["de"] in ExpectedAudioLanguages!
        var animeDl = parser.Parse("FantasyQuest.2017.S02E02.German.ANiME.DL.AAC.1080p.BDRiP.AV1-SD7", MediaVerticals.Anime);
        var animeExpected = CompletedDownloadImportService.BuildExpectedAudioLanguages(animeDl, [], "ja", germanFirstPolicy);
        Assert.Equal(["de"], animeExpected);

        var animeEnDl = parser.Parse("FantasyQuest.S02E02.German.English.DL.1080p.BluRay", MediaVerticals.Anime);
        var animeEnExpected = CompletedDownloadImportService.BuildExpectedAudioLanguages(animeEnDl, [], "ja", germanFirstPolicy);
        Assert.Equal(["de"], animeEnExpected);

        // Day-1 English release that does NOT claim German still verifies what it claims ("en")
        var day1En = parser.Parse("FantasyQuest.S02E02.English.1080p.BluRay", MediaVerticals.Anime);
        var day1Expected = CompletedDownloadImportService.BuildExpectedAudioLanguages(day1En, [], "ja", germanFirstPolicy);
        Assert.Equal(["en"], day1Expected);
    }

    [Fact]
    public async Task ComposableProfileService_SimulateAsync_ScoresAnimeGermanDL_AsGermanJapaneseDual()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"anime-sim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await using var factory = CreateFactory(tempDir, new FakeGateway(), new FakeLanguageVerificationService());
            using var scope = factory.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var profiles = sp.GetRequiredService<IComposableProfileService>();
            await profiles.EnsureBuiltInProfilesSeededAsync();

            var allProfiles = await profiles.ListProfilesAsync();
            var profile = allProfiles.First(p => p.Slug == "hd-uhd-de-en-dual");

            var scorecard = await profiles.SimulateAsync(new SimulateReleaseRequest(
                ProfileId: profile.Id,
                ReleaseTitle: "FantasyQuest.2017.S02E02.German.ANiME.DL.AAC.1080p.BDRiP.AV1-SD7.mkv",
                OriginalLanguage: "ja",
                ExplicitAudioLanguages: null));

            Assert.True(scorecard.Approved);
            // Matches Tier 1 (German + Original Dual Audio ["de", "ja"])
            Assert.Equal(1, scorecard.MatchedLanguageTier);
            Assert.True(scorecard.CandidateSatisfiesLanguageCutoff);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ProcessCompletedDownloads_GermanDualAudioAnime_ImportsIntoExistingSeriesFolderAndUnpaddedSeason_WithZeroRedownloads()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"anime-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var animeRoot = Path.Combine(tempDir, "storage", "anime");
            var usenetRoot = Path.Combine(tempDir, "downloads", "usenet");
            Directory.CreateDirectory(animeRoot);
            Directory.CreateDirectory(usenetRoot);

            // Pre-existing series folder on disk with straight apostrophe (') and unpadded 'Season 2' (as Sonarr / real library has)
            var existingSeriesFolder = Path.Combine(animeRoot, "FantasyQuest – Hero's blessing on this world!!");
            var existingSeasonFolder = Path.Combine(existingSeriesFolder, "Season 2");
            Directory.CreateDirectory(existingSeasonFolder);

            // Pre-existing S02E01 file on disk
            var existingS02E01File = Path.Combine(existingSeasonFolder, "FantasyQuest - S02E01.mkv");
            await File.WriteAllBytesAsync(existingS02E01File, [0x1A, 0x45, 0xDF, 0xA3, 0x01, 0x02]);

            // Staged unpacked Usenet download from SABnzbd
            var jobFolderName = "FantasyQuest.2017.S02E02.German.ANiME.DL.AAC.1080p.BDRiP.AV1-SD7";
            var jobDir = Path.Combine(usenetRoot, jobFolderName);
            Directory.CreateDirectory(jobDir);
            var stagedFile = Path.Combine(jobDir, $"{jobFolderName}.mkv");
            await File.WriteAllBytesAsync(stagedFile, new byte[65_536]);

            var fakeGateway = new FakeGateway();
            var fakeVerifier = new FakeLanguageVerificationService();

            await using var factory = CreateFactory(tempDir, fakeGateway, fakeVerifier);
            using var scope = factory.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var catalog = sp.GetRequiredService<IPolymorphicCatalogService>();
            var profiles = sp.GetRequiredService<IComposableProfileService>();
            var orchestrator = sp.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var importSvc = sp.GetRequiredService<ICompletedDownloadImportService>();
            var db = sp.GetRequiredService<ISqliteDbFactory>();

            await profiles.EnsureBuiltInProfilesSeededAsync();

            // Create Anime library
            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Anime", "anime", animeRoot));

            // Create FantasyQuest work with canonical title (containing curly apostrophe ’)
            var createdTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "FantasyQuest – Hero’s blessing on this world!!",
                WorkKind: "anime",
                OriginalLanguage: "ja",
                Year: 2016,
                AltTitles: ["FantasyQuest", "FantasyQuest 2", "Subarashii Fantasy Sekai ni Syukufuku o!"],
                Monitored: true,
                Containers:
                [
                    new MetadataContainerSeed("season", 2, "Season 2", "2017-01-12",
                    [
                        new MetadataUnitSeed("episode", 1, 1, "God's Blessing on This Freezing Order!", "2017-01-12"),
                        new MetadataUnitSeed("episode", 2, 2, "A Friend for This Crimson Demon Girl!", "2017-01-19"),
                        new MetadataUnitSeed("episode", 3, 3, "Peace for This Master of the Labyrinth!", "2017-01-26"),
                        new MetadataUnitSeed("episode", 4, 4, "A Betrothed for This Noble Daughter!", "2017-02-02"),
                    ])
                ]));

            var s2Container = createdTree.Containers.First(c => c.ContainerNumber == 2);
            var s02e01Unit = s2Container.Units.First(u => u.UnitNumber == 1);
            var s02e02Unit = s2Container.Units.First(u => u.UnitNumber == 2);

            // Attach pre-existing S02E01 file to database
            var relS02E01 = Path.GetRelativePath(animeRoot, existingS02E01File);
            await catalog.AttachFileAsync(new AttachMediaFileRequest(
                UnitId: s02e01Unit.Id,
                RelativePath: relS02E01,
                SizeBytes: 6,
                QualityName: "HDTV-720p",
                DetectedAudioLangs: ["de", "ja"],
                DetectedSubLangs: ["de"],
                VerifiedStatus: VerificationVerdicts.VerifiedPass));

            // Configure SABnzbd client
            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd",
                Implementation: "sabnzbd",
                Host: "sab",
                Port: 8080,
                ApiKey: "k",
                RemoveCompleted: true,
                RemoveFailed: true));

            // Record grab history for S02E02
            await RecordGrabHistoryAsync(
                db,
                sabClient.Id,
                "nzo-sab-1",
                null,
                jobFolderName,
                "anime",
                createdTree.Work.Id,
                [s02e02Unit.Id]);

            // Enqueue completed download in SABnzbd:
            // Notice: client vertical is "movies" (SABnzbd category "*"), but grabHistory specifies "anime"
            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "nzo-sab-1",
                InfoHash: "nzo-sab-1",
                Title: jobFolderName,
                Category: "*",
                MediaVertical: "movies",
                Status: "Completed",
                TotalBytes: 65_536,
                RemainingBytes: 0,
                OutputPath: jobDir,
                ContainedFiles: [stagedFile],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            // Configure verifier
            fakeVerifier.OnVerify = req =>
            {
                // Assert that ExpectedAudioLanguages contains "de" and does NOT contain "en"
                Assert.Contains("de", req.ExpectedAudioLanguages!);
                Assert.DoesNotContain("en", req.ExpectedAudioLanguages!);

                return new MediaFileVerificationReport(
                    JobId: 101,
                    UnitId: req.UnitId,
                    StagedFilePath: req.StagedFilePath,
                    MediaVertical: req.MediaVertical,
                    Verdict: VerificationVerdicts.VerifiedPass,
                    OverallConfidence: 0.98,
                    HeaderAutoPatched: false,
                    ExistingFilePreservedOnFailure: false,
                    HashBlocklisted: false,
                    QuarantineId: null,
                    DurationMs: 12,
                    VerifiedAudioLanguages: ["de", "ja"],
                    VerifiedSubtitleLanguages: ["de"],
                    Streams: [],
                    VerifiedAt: DateTimeOffset.UtcNow.ToString("O"));
            };

            // Run Completed Download Import
            var report = await importSvc.ProcessCompletedDownloadsAsync();

            Assert.Single(report.Items);
            var itemOutcome = report.Items[0];
            Assert.Equal("VerifiedPass", itemOutcome.Verdict);

            // Assert file was imported into the existing series folder with straight apostrophe (') and unpadded "Season 2"
            var expectedImportDir = Path.Combine(animeRoot, "FantasyQuest – Hero's blessing on this world!!", "Season 2");
            Assert.True(Directory.Exists(expectedImportDir), $"Expected directory {expectedImportDir} does not exist!");

            var importedFiles = Directory.GetFiles(expectedImportDir, "*S02E02*");
            Assert.Single(importedFiles);

            // Assert staging directory in downloads/usenet was cleaned up
            Assert.False(Directory.Exists(jobDir), "Unpacked Usenet job directory was not deleted after successful import!");

            // Assert unit attached file in database
            var updatedTree = await catalog.GetWorkTreeAsync(createdTree.Work.Id);
            Assert.NotNull(updatedTree);
            var updatedS2 = updatedTree.Containers.First(c => c.ContainerNumber == 2);
            var updatedS02E02 = updatedS2.Units.First(u => u.UnitNumber == 2);
            Assert.Single(updatedS02E02.Files);
            Assert.Equal(["de", "ja"], updatedS02E02.Files[0].DetectedAudioLangs);
            Assert.True(updatedS02E02.LanguageCutoffMet);

            // Assert 0 redownloads triggered in ledger
            await using var conn = await db.OpenReadConnectionAsync(DatabaseKind.Core);
            var blocklistCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recurring_blocklist_hashes;");
            Assert.Equal(0, blocklistCount);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ProcessCompletedDownloads_UngrabbedSceneAnimeAndSequelNumbering_ResolvesCleanly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"anime-ungrabbed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var animeRoot = Path.Combine(tempDir, "storage", "anime");
            var usenetRoot = Path.Combine(tempDir, "downloads", "usenet");
            Directory.CreateDirectory(animeRoot);
            Directory.CreateDirectory(usenetRoot);

            var existingSeriesFolder = Path.Combine(animeRoot, "FantasyQuest – Hero's blessing on this world!!");
            var existingSeasonFolder = Path.Combine(existingSeriesFolder, "Season 2");
            Directory.CreateDirectory(existingSeasonFolder);

            var fakeGateway = new FakeGateway();
            var fakeVerifier = new FakeLanguageVerificationService();

            await using var factory = CreateFactory(tempDir, fakeGateway, fakeVerifier);
            using var scope = factory.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var catalog = sp.GetRequiredService<IPolymorphicCatalogService>();
            var profiles = sp.GetRequiredService<IComposableProfileService>();
            var orchestrator = sp.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var importSvc = sp.GetRequiredService<ICompletedDownloadImportService>();

            await profiles.EnsureBuiltInProfilesSeededAsync();

            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Anime", "anime", animeRoot));

            var createdTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "FantasyQuest – Hero’s blessing on this world!!",
                WorkKind: "anime",
                OriginalLanguage: "ja",
                Year: 2016,
                AltTitles: ["FantasyQuest", "FantasyQuest 2"],
                Monitored: true,
                Containers:
                [
                    new MetadataContainerSeed("season", 2, "Season 2", "2017-01-12",
                    [
                        new MetadataUnitSeed("episode", 3, 3, "Peace for This Master of the Labyrinth!", "2017-01-26"),
                        new MetadataUnitSeed("episode", 4, 4, "A Betrothed for This Noble Daughter!", "2017-02-02"),
                    ])
                ]));

            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd",
                Implementation: "sabnzbd",
                Host: "sab",
                Port: 8080,
                ApiKey: "k",
                RemoveCompleted: true,
                RemoveFailed: true));

            // 1. Ungrabbed German scene anime release in "tv" category without [SubGroup] tag
            var job3Name = "FantasyQuest.S02E03.German.DL.DTS.1080p.BluRay.x264-ABJ";
            var job3Dir = Path.Combine(usenetRoot, job3Name);
            Directory.CreateDirectory(job3Dir);
            var file3 = Path.Combine(job3Dir, $"{job3Name}.mkv");
            await File.WriteAllBytesAsync(file3, new byte[10_000]);

            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "nzo-job-3",
                InfoHash: "nzo-job-3",
                Title: job3Name,
                Category: "tv-managerr",
                MediaVertical: "tv", // parsed as TV from client, no grabHistory!
                Status: "Completed",
                TotalBytes: 10_000,
                RemainingBytes: 0,
                OutputPath: job3Dir,
                ContainedFiles: [file3],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            // 2. Sequel-numbered anime release FantasyQuest.2.E04 (CleanTitle = "FantasyQuest 2")
            var job4Name = "FantasyQuest.2.E04.German.AC3.DL.1080p.BluRay.x264-Fuuko";
            var job4Dir = Path.Combine(usenetRoot, job4Name);
            Directory.CreateDirectory(job4Dir);
            var file4 = Path.Combine(job4Dir, $"{job4Name}.mkv");
            await File.WriteAllBytesAsync(file4, new byte[10_000]);

            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "nzo-job-4",
                InfoHash: "nzo-job-4",
                Title: job4Name,
                Category: "tv-managerr",
                MediaVertical: "tv",
                Status: "Completed",
                TotalBytes: 10_000,
                RemainingBytes: 0,
                OutputPath: job4Dir,
                ContainedFiles: [file4],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            fakeVerifier.OnVerify = req => new MediaFileVerificationReport(
                JobId: 102,
                UnitId: req.UnitId,
                StagedFilePath: req.StagedFilePath,
                MediaVertical: req.MediaVertical,
                Verdict: VerificationVerdicts.VerifiedPass,
                OverallConfidence: 0.99,
                HeaderAutoPatched: false,
                ExistingFilePreservedOnFailure: false,
                HashBlocklisted: false,
                QuarantineId: null,
                DurationMs: 5,
                VerifiedAudioLanguages: ["de", "ja"],
                VerifiedSubtitleLanguages: ["de"],
                Streams: [],
                VerifiedAt: DateTimeOffset.UtcNow.ToString("O"));

            var report = await importSvc.ProcessCompletedDownloadsAsync();

            Assert.Equal(2, report.Items.Count);
            Assert.All(report.Items, i => Assert.Equal("VerifiedPass", i.Verdict));

            var updatedTree = await catalog.GetWorkTreeAsync(createdTree.Work.Id);
            Assert.NotNull(updatedTree);
            var updatedS2 = updatedTree.Containers.First(c => c.ContainerNumber == 2);
            var ep3 = updatedS2.Units.First(u => u.UnitNumber == 3);
            var ep4 = updatedS2.Units.First(u => u.UnitNumber == 4);

            Assert.Single(ep3.Files);
            Assert.Single(ep4.Files);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ProcessCompletedDownloads_UserRequirement_SecondLanguageCorrectionWithoutRedownload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"anime-userrule-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var animeRoot = Path.Combine(tempDir, "storage", "anime");
            var usenetRoot = Path.Combine(tempDir, "downloads", "usenet");
            Directory.CreateDirectory(animeRoot);
            Directory.CreateDirectory(usenetRoot);

            var fakeGateway = new FakeGateway();
            var fakeVerifier = new FakeLanguageVerificationService();

            await using var factory = CreateFactory(tempDir, fakeGateway, fakeVerifier);
            using var scope = factory.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var catalog = sp.GetRequiredService<IPolymorphicCatalogService>();
            var profiles = sp.GetRequiredService<IComposableProfileService>();
            var orchestrator = sp.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var importSvc = sp.GetRequiredService<ICompletedDownloadImportService>();
            var db = sp.GetRequiredService<ISqliteDbFactory>();

            await profiles.EnsureBuiltInProfilesSeededAsync();

            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Anime", "anime", animeRoot));

            var createdTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "FantasyQuest",
                WorkKind: "anime",
                OriginalLanguage: "ja",
                Year: 2016,
                Containers:
                [
                    new MetadataContainerSeed("season", 1, "Season 1", "2016-01-14",
                    [
                        new MetadataUnitSeed("episode", 1, 1, "Episode 1", "2016-01-14"),
                        new MetadataUnitSeed("episode", 2, 2, "Episode 2", "2016-01-21"),
                    ])
                ]));

            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd",
                Implementation: "sabnzbd",
                Host: "sab",
                Port: 8080,
                ApiKey: "k",
                RemoveCompleted: true,
                RemoveFailed: true));

            // CASE B: Release claims German.English.DL, but verified file has French & German ["de", "fr"]
            // Because German is wanted and verified, it must NOT fail or redownload!
            // It imports cleanly, sets LanguageCutoffMet = true, and corrects detected languages to ["de", "fr"] in series overview!
            var jobDir = Path.Combine(usenetRoot, "FantasyQuest.S01E01.German.English.DL.1080p.BluRay");
            Directory.CreateDirectory(jobDir);
            var file = Path.Combine(jobDir, "FantasyQuest.S01E01.German.English.DL.1080p.BluRay.mkv");
            await File.WriteAllBytesAsync(file, new byte[10_000]);

            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "nzo-case-b",
                InfoHash: "nzo-case-b",
                Title: "FantasyQuest.S01E01.German.English.DL.1080p.BluRay",
                Category: "anime-managerr",
                MediaVertical: "anime",
                Status: "Completed",
                TotalBytes: 10_000,
                RemainingBytes: 0,
                OutputPath: jobDir,
                ContainedFiles: [file],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            fakeVerifier.OnVerify = req =>
            {
                // Verify that only "de" was expected by the import service under German First policy
                Assert.Equal(["de"], req.ExpectedAudioLanguages);

                // File verified with German + French
                return new MediaFileVerificationReport(
                    JobId: 201,
                    UnitId: req.UnitId,
                    StagedFilePath: req.StagedFilePath,
                    MediaVertical: req.MediaVertical,
                    Verdict: VerificationVerdicts.VerifiedPass,
                    OverallConfidence: 0.99,
                    HeaderAutoPatched: false,
                    ExistingFilePreservedOnFailure: false,
                    HashBlocklisted: false,
                    QuarantineId: null,
                    DurationMs: 8,
                    VerifiedAudioLanguages: ["de", "fr"],
                    VerifiedSubtitleLanguages: ["de"],
                    Streams: [],
                    VerifiedAt: DateTimeOffset.UtcNow.ToString("O"));
            };

            var report = await importSvc.ProcessCompletedDownloadsAsync();
            Assert.Single(report.Items);
            Assert.Equal("VerifiedPass", report.Items[0].Verdict);

            var updatedTree = await catalog.GetWorkTreeAsync(createdTree.Work.Id);
            Assert.NotNull(updatedTree);
            var ep1 = updatedTree.Containers[0].Units.First(u => u.UnitNumber == 1);
            Assert.Single(ep1.Files);
            // Displayed languages corrected to actual detected languages ["de", "fr"]!
            Assert.Equal(["de", "fr"], ep1.Files[0].DetectedAudioLangs);
            Assert.True(ep1.LanguageCutoffMet);

            // CASE C: Fake .German.DL. release that actually contains ONLY ["en"] or ["ja"] without "de"
            // Must trigger VerifiedFail and cleanup
            var jobDirC = Path.Combine(usenetRoot, "FantasyQuest.S01E02.German.DL.1080p.BluRay");
            Directory.CreateDirectory(jobDirC);
            var fileC = Path.Combine(jobDirC, "FantasyQuest.S01E02.German.DL.1080p.BluRay.mkv");
            await File.WriteAllBytesAsync(fileC, new byte[10_000]);

            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "nzo-case-c",
                InfoHash: "nzo-case-c",
                Title: "FantasyQuest.S01E02.German.DL.1080p.BluRay",
                Category: "anime-managerr",
                MediaVertical: "anime",
                Status: "Completed",
                TotalBytes: 10_000,
                RemainingBytes: 0,
                OutputPath: jobDirC,
                ContainedFiles: [fileC],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            fakeVerifier.OnVerify = req => new MediaFileVerificationReport(
                JobId: 202,
                UnitId: req.UnitId,
                StagedFilePath: req.StagedFilePath,
                MediaVertical: req.MediaVertical,
                Verdict: VerificationVerdicts.VerifiedFail,
                OverallConfidence: 0.99,
                HeaderAutoPatched: false,
                ExistingFilePreservedOnFailure: false,
                HashBlocklisted: true,
                QuarantineId: null,
                DurationMs: 8,
                VerifiedAudioLanguages: ["ja"],
                VerifiedSubtitleLanguages: [],
                Streams: [],
                VerifiedAt: DateTimeOffset.UtcNow.ToString("O"),
                FailureReason: "Missing expected German audio track");

            var reportC = await importSvc.ProcessCompletedDownloadsAsync();
            Assert.Single(reportC.Items);
            Assert.Equal("VerifiedFail", reportC.Items[0].Verdict);

            // Staging folder for failed item is cleaned up
            Assert.False(Directory.Exists(jobDirC));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ProcessCompletedDownloads_AnimeTitlesWithoutBarePrefix_MatchesWithSequelYearAndSpecials()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"anime-stem-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var animeRoot = Path.Combine(tempDir, "storage", "anime");
            var usenetRoot = Path.Combine(tempDir, "downloads", "usenet");
            Directory.CreateDirectory(animeRoot);
            Directory.CreateDirectory(usenetRoot);

            var jobDir = Path.Combine(usenetRoot, "FantasyQuest.2017.S00E03.OVA.German.DL.1080p");
            Directory.CreateDirectory(jobDir);
            var stagingFile = Path.Combine(jobDir, "FantasyQuest.2017.S00E03.OVA.German.DL.1080p.mkv");
            File.WriteAllText(stagingFile, "dummy");

            var fakeGateway = new FakeGateway();
            var fakeVerifier = new FakeLanguageVerificationService();

            await using var factory = CreateFactory(tempDir, fakeGateway, fakeVerifier);
            using var scope = factory.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var catalog = sp.GetRequiredService<IPolymorphicCatalogService>();
            var profiles = sp.GetRequiredService<IComposableProfileService>();
            var importer = sp.GetRequiredService<ICompletedDownloadImportService>();
            var orchestrator = sp.GetRequiredService<IUnifiedDownloadOrchestrator>();

            await profiles.EnsureBuiltInProfilesSeededAsync();

            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd",
                Implementation: "sabnzbd",
                Host: "sab",
                Port: 8080,
                ApiKey: "k",
                RemoveCompleted: true,
                RemoveFailed: true));

            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Anime", "anime", animeRoot));

            // Work registered with Japanese title and long English title WITH colon, but NO bare "FantasyQuest" in titles!
            var created = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "Subarashii Fantasy Sekai ni Syukufuku wo!",
                WorkKind: "anime",
                OriginalLanguage: "ja",
                Year: 2016,
                AltTitles: ["FantasyQuest: Hero's Blessing on this World!"],
                Monitored: true,
                Containers: []));

            var queueItem = new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "sab-fantasyquest-ova",
                InfoHash: "sab-fantasyquest-ova",
                Title: "FantasyQuest.2017.S00E03.OVA.German.DL.1080p",
                Category: "anime-managerr",
                MediaVertical: "anime",
                Status: "Completed",
                TotalBytes: 1000,
                RemainingBytes: 0,
                OutputPath: jobDir,
                ContainedFiles: [stagingFile],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true);

            fakeGateway.QueueItems.Add(queueItem);

            var report = await importer.ProcessCompletedDownloadsAsync();
            Assert.Single(report.Items);
            var outcome = report.Items[0];

            Assert.Equal(VerificationVerdicts.VerifiedPass, outcome.Verdict);
            Assert.NotNull(outcome.DestinationFilePath);
            Assert.True(File.Exists(outcome.DestinationFilePath));

            // Verify S00E03 Specials unit and container were created and imported into Specials folder
            var tree = await catalog.GetWorkTreeAsync(created.Work.Id);
            Assert.NotNull(tree);
            var specialsContainer = tree.Containers.FirstOrDefault(c => c.ContainerNumber == 0);
            Assert.NotNull(specialsContainer);
            var specialUnit = specialsContainer.Units.FirstOrDefault(u => u.UnitNumber == 3);
            Assert.NotNull(specialUnit);
            Assert.NotEmpty(specialUnit.Files);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryDeleteImportedUsenetJobFolder_GuardsAgainstCategoryRootAndSingleFile_DeletesCorrelatedJobFolder()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"usenet-cleanup-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var usenetRoot = Path.Combine(tempRoot, "downloads", "usenet");
            var seriesCatDir = Path.Combine(usenetRoot, "series");
            Directory.CreateDirectory(seriesCatDir);

            var jobDir = Path.Combine(seriesCatDir, "FantasyQuest.2017.S02E02.German.ANiME.DL.1080p.1");
            Directory.CreateDirectory(jobDir);
            var stagingFile = Path.Combine(jobDir, "episode.mkv");
            File.WriteAllText(stagingFile, "dummy");

            var libraries = new List<LibrarySummary>
            {
                new(1, "anime", "Anime", "anime", Path.Combine(tempRoot, "storage", "anime"), null, [], 1, 0, DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"))
            };

            // Case 1: Basename is category root "series" -> MUST NOT be deleted
            var catItem = new ActiveDownloadQueueItem(
                ClientId: 1,
                ClientName: "SABnzbd",
                Protocol: "usenet",
                ExternalId: "sab-1",
                InfoHash: "sab-1",
                Title: "FantasyQuest.2017.S02E02.German.ANiME.DL.1080p",
                Category: "series",
                MediaVertical: "anime",
                Status: "Completed",
                TotalBytes: 1000,
                RemainingBytes: 0,
                OutputPath: seriesCatDir,
                ContainedFiles: [stagingFile],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true);

            CompletedDownloadImportService.TryDeleteImportedUsenetJobFolder(catItem, libraries);
            Assert.True(Directory.Exists(seriesCatDir));

            // Case 2: OutputPath is a single file -> MUST delete the file, but NEVER Directory.Delete on parent directory!
            var singleFileItem = catItem with { OutputPath = stagingFile };
            CompletedDownloadImportService.TryDeleteImportedUsenetJobFolder(singleFileItem, libraries);
            Assert.False(File.Exists(stagingFile));
            Assert.True(Directory.Exists(jobDir)); // Parent folder jobDir remains!

            // Case 3: OutputPath is jobDir with correlation to Title -> MUST delete the job directory!
            var jobDirItem = catItem with { OutputPath = jobDir };
            CompletedDownloadImportService.TryDeleteImportedUsenetJobFolder(jobDirItem, libraries);
            Assert.False(Directory.Exists(jobDir));
            Assert.True(Directory.Exists(seriesCatDir)); // Category root series remains intact!
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AtomicFileOrganizer_RejectsPathTraversal_AndEscapingLibraryRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"traversal-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var libRoot = Path.Combine(tempRoot, "storage", "anime");
            var stagingDir = Path.Combine(tempRoot, "staging");
            Directory.CreateDirectory(libRoot);
            Directory.CreateDirectory(stagingDir);

            var sourceFile = Path.Combine(stagingDir, "test.mkv");
            File.WriteAllText(sourceFile, "dummy");

            var organizer = new AtomicFileOrganizer();
            var parsed = new ParsedReleaseInfo(
                RawTitle: "FantasyQuest.S02E02.mkv",
                MediaVertical: MediaVerticals.Anime,
                CleanTitle: "FantasyQuest",
                ArtistOrAuthor: null,
                Year: 2017,
                SeasonNumber: 2,
                EpisodeNumbers: [2],
                AbsoluteEpisodeNumbers: [],
                IsFullSeasonPack: false,
                MovieEdition: null,
                Resolution: "1080p",
                Source: "WEB-DL",
                VideoCodec: "x264",
                HdrFormat: null,
                AudioCodec: "AAC",
                MusicFormat: null,
                BookFormat: null,
                DetectedLanguages: ["de"],
                IsDualAudio: false,
                IsMultiSubs: false,
                ReleaseGroup: null);

            // ExistingWorkFolderName containing traversal "../../evil"
            var traversalContext = new NamingContextMetadata(
                EpisodeTitle: null,
                ExistingWorkFolderName: "../../evil",
                ExistingSeasonFolderName: "../evil_season",
                PreferSeasonalAnimeLayout: true);

            // IsSafeSinglePathSegment should be false
            Assert.False(AtomicFileOrganizer.IsSafeSinglePathSegment("../../evil"));
            Assert.False(AtomicFileOrganizer.IsSafeSinglePathSegment("."));
            Assert.False(AtomicFileOrganizer.IsSafeSinglePathSegment(".."));
            Assert.False(AtomicFileOrganizer.IsSafeSinglePathSegment("/evil"));

            // Organizer should ignore unsafe ExistingWorkFolderName and sanitize destination within libRoot
            var res = await organizer.ImportFileAtomicallyAsync(new OrganizeFileRequest(
                SourceFilePath: sourceFile,
                LibraryRootPath: libRoot,
                ParsedInfo: parsed,
                PreferHardlink: false,
                CanMoveSource: false,
                ImportCompanionSubtitles: false,
                NamingContext: traversalContext));

            Assert.StartsWith(libRoot, res.DestinationFilePath, StringComparison.Ordinal);
            Assert.DoesNotContain("evil", res.DestinationFilePath);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ProcessCompletedDownloads_UnrequestedLowConfidenceSecondAudio_DoesNotCauseQuarantineOrRedownload_WhenWantedAudioHighConfidence()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"low-conf-2nd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var animeRoot = Path.Combine(tempDir, "storage", "anime");
            var usenetRoot = Path.Combine(tempDir, "downloads", "usenet");
            Directory.CreateDirectory(animeRoot);
            Directory.CreateDirectory(usenetRoot);

            var jobDir = Path.Combine(usenetRoot, "FantasyQuest.2017.S02E02.German.DL.1080p");
            Directory.CreateDirectory(jobDir);
            var stagingFile = Path.Combine(jobDir, "FantasyQuest.2017.S02E02.German.DL.1080p.mkv");
            File.WriteAllText(stagingFile, "dummy");

            var fakeGateway = new FakeGateway();
            var fakeVerifier = new FakeLanguageVerificationService
            {
                OnVerify = req => new MediaFileVerificationReport(
                    JobId: 42,
                    UnitId: req.UnitId,
                    StagedFilePath: req.StagedFilePath,
                    MediaVertical: req.MediaVertical,
                    // Verifier initially returned QuarantineReview because overall confidence was dragged down by low-confidence track 2!
                    Verdict: VerificationVerdicts.QuarantineReview,
                    OverallConfidence: 0.65,
                    HeaderAutoPatched: false,
                    ExistingFilePreservedOnFailure: false,
                    HashBlocklisted: false,
                    QuarantineId: 101,
                    DurationMs: 50,
                    VerifiedAudioLanguages: ["de", "ja"],
                    VerifiedSubtitleLanguages: ["de"],
                    Streams:
                    [
                        new VerifiedStreamReport(0, "audio", "aac", 2, "de", "de", 0.95, "whisper_lid", CueCount: 10),
                        new VerifiedStreamReport(1, "audio", "aac", 2, "ja", "ja", 0.35, "whisper_lid", CueCount: 2) // Unrequested 2nd stream low confidence
                    ],
                    VerifiedAt: DateTimeOffset.UtcNow.ToString("O"))
            };

            await using var factory = CreateFactory(tempDir, fakeGateway, fakeVerifier);
            using var scope = factory.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var catalog = sp.GetRequiredService<IPolymorphicCatalogService>();
            var profiles = sp.GetRequiredService<IComposableProfileService>();
            var importer = sp.GetRequiredService<ICompletedDownloadImportService>();
            var orchestrator = sp.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var db = sp.GetRequiredService<ISqliteDbFactory>();

            await profiles.EnsureBuiltInProfilesSeededAsync();
            var germanFirstPolicy = (await profiles.ListLanguagePoliciesAsync()).First(p => p.Slug == "german-first");

            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd",
                Implementation: "sabnzbd",
                Host: "sab",
                Port: 8080,
                ApiKey: "k",
                RemoveCompleted: true,
                RemoveFailed: true));

            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Anime", "anime", animeRoot));

            var created = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "Subarashii Fantasy Sekai ni Syukufuku wo!",
                WorkKind: "anime",
                OriginalLanguage: "ja",
                Year: 2016,
                AltTitles: ["FantasyQuest: Hero's Blessing on this World!"],
                Monitored: true,
                Containers:
                [
                    new MetadataContainerSeed("season", 2, "Season 2", "2017-01-12",
                    [
                        new MetadataUnitSeed("episode", 2, 2, "A Friend for This Crimson Demon Girl!", "2017-01-19"),
                    ])
                ]));

            var tree = await catalog.GetWorkTreeAsync(created.Work.Id);
            Assert.NotNull(tree);
            var season2Container = tree.Containers.First(c => c.ContainerNumber == 2);
            var s02e02Unit = season2Container.Units.First(u => u.UnitNumber == 2);

            await catalog.UpdateWorkAsync(created.Work.Id, new UpdateMediaWorkRequest(LanguagePolicyId: germanFirstPolicy.Id));

            // Pre-insert verification job and quarantine row so we can verify it gets cleaned up on override!
            await db.ExecuteWriteAsync(DatabaseKind.Core, async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO verification_jobs (
                        id, unit_id, staged_file_path, media_vertical, verdict, overall_confidence,
                        header_auto_patched, detected_audio_langs_json, detected_sub_langs_json, report_json, duration_ms, created_at
                    ) VALUES (
                        42, @UnitId, @Path, 'anime', 'QuarantineReview', 0.65,
                        0, '["de","ja"]', '["de"]', '{}', 50, @Now
                    );
                    INSERT INTO quarantined_imports (
                        id, verification_job_id, unit_id, release_title, staged_file_path,
                        expected_languages_json, detected_languages_json, confidence, reason,
                        snippet_paths_json, status, created_at
                    ) VALUES (
                        101, 42, @UnitId, 'FantasyQuest.2017.S02E02.German.DL.1080p', @Path,
                        '["de"]', '["de","ja"]', 0.65, 'Low confidence track 2',
                        '[]', 'held_ambiguous', @Now
                    );
                    """,
                    new { UnitId = s02e02Unit.Id, Path = stagingFile, Now = DateTimeOffset.UtcNow.ToString("O") }, tx);
            });

            var queueItem = new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "sab-fantasyquest-lowconf",
                InfoHash: "sab-fantasyquest-lowconf",
                Title: "FantasyQuest.2017.S02E02.German.DL.1080p",
                Category: "anime-managerr",
                MediaVertical: "anime",
                Status: "Completed",
                TotalBytes: 1000,
                RemainingBytes: 0,
                OutputPath: jobDir,
                ContainedFiles: [stagingFile],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true);

            fakeGateway.QueueItems.Add(queueItem);

            var report = await importer.ProcessCompletedDownloadsAsync();
            Assert.Single(report.Items);
            var outcome = report.Items[0];

            // Overridden to VerifiedPass because wanted "de" had 0.95 confidence!
            Assert.Equal(VerificationVerdicts.VerifiedPass, outcome.Verdict);
            Assert.NotNull(outcome.DestinationFilePath);
            Assert.True(File.Exists(outcome.DestinationFilePath));

            // Quarantine row was deleted
            await using (var readConn = await db.OpenReadConnectionAsync(DatabaseKind.Core))
            {
                var qCount = await readConn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM quarantined_imports WHERE id = 101;");
                Assert.Equal(0, qCount);
            }

            // Unit marked with LanguageCutoffMet = true
            var updatedTree = await catalog.GetWorkTreeAsync(created.Work.Id);
            Assert.NotNull(updatedTree);
            var updatedUnit = updatedTree.Containers.First(c => c.ContainerNumber == 2).Units.First(u => u.UnitNumber == 2);
            Assert.True(updatedUnit.LanguageCutoffMet);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string tempDir,
        IDownloadClientDriverGateway gateway,
        ILanguageVerificationService verifier)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                ["Managerr:MasterKey"] = "anime-import-test-master-key-32b",
                ["Managerr:SchedulerEnabled"] = "false",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IDownloadClientDriverGateway>();
                s.AddSingleton<IDownloadClientDriverGateway>(gateway);

                s.RemoveAll<ILanguageVerificationService>();
                s.AddSingleton<ILanguageVerificationService>(verifier);
            });
        });
    }

    private static async Task RecordGrabHistoryAsync(
        ISqliteDbFactory db,
        long clientId,
        string externalDownloadId,
        string? infoHash,
        string releaseTitle,
        string mediaVertical,
        long workId,
        IReadOnlyList<long>? unitIds = null)
    {
        await db.ExecuteWriteAsync(DatabaseKind.Core, async (conn, tx) =>
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO download_grab_history (
                    download_client_id, external_download_id, info_hash, release_title,
                    media_vertical, work_id, unit_ids_json, download_url, source_type, grabbed_at
                ) VALUES (
                    @ClientId, @ExtId, @Hash, @Title,
                    @Vert, @WorkId, @UnitIdsJson, 'http://test', 'interactive', @Now
                );
                """,
                new
                {
                    ClientId = clientId,
                    ExtId = externalDownloadId,
                    Hash = infoHash,
                    Title = releaseTitle,
                    Vert = mediaVertical,
                    WorkId = workId,
                    UnitIdsJson = JsonSerializer.Serialize(unitIds ?? [], JsonOpts),
                    Now = DateTimeOffset.UtcNow.ToString("O")
                }, tx);
        });
    }

    private sealed class FakeGateway : IDownloadClientDriverGateway
    {
        public List<ActiveDownloadQueueItem> QueueItems { get; } = [];
        public List<string> RemovedDownloads { get; } = [];

        public IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers() => DownloadClientCatalog.All;

        public Task<string> EnqueueReleaseAsync(DownloadClientConnectionSpec client, string releaseTitle, string downloadUrlOrMagnet, string resolvedCategory, CancellationToken ct = default)
            => Task.FromResult($"q-{Guid.NewGuid():N}");

        public Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(DownloadClientConnectionSpec client, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ActiveDownloadQueueItem>>(QueueItems.Where(q => q.ClientId == client.Id).ToList());

        public Task RemoveDownloadAsync(DownloadClientConnectionSpec client, string externalId, bool deleteData, CancellationToken ct = default)
        {
            RemovedDownloads.Add(externalId);
            QueueItems.RemoveAll(q => q.ExternalId == externalId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLanguageVerificationService : ILanguageVerificationService
    {
        public Func<RunVerificationRequest, MediaFileVerificationReport>? OnVerify { get; set; }
        public List<RunVerificationRequest> RecordedRequests { get; } = [];

        public Task<MediaFileVerificationReport> VerifyStagedFileAsync(RunVerificationRequest request, CancellationToken ct = default)
        {
            RecordedRequests.Add(request);
            if (OnVerify is not null)
            {
                return Task.FromResult(OnVerify(request));
            }

            return Task.FromResult(new MediaFileVerificationReport(
                JobId: 1,
                UnitId: request.UnitId,
                StagedFilePath: request.StagedFilePath,
                MediaVertical: request.MediaVertical,
                Verdict: VerificationVerdicts.VerifiedPass,
                OverallConfidence: 0.99,
                HeaderAutoPatched: false,
                ExistingFilePreservedOnFailure: false,
                HashBlocklisted: false,
                QuarantineId: null,
                DurationMs: 1,
                VerifiedAudioLanguages: request.ExpectedAudioLanguages ?? ["de", "ja"],
                VerifiedSubtitleLanguages: ["de"],
                Streams: [],
                VerifiedAt: DateTimeOffset.UtcNow.ToString("O")));
        }

        public Task<IReadOnlyList<MediaFileVerificationReport>> ListRecentReportsAsync(int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MediaFileVerificationReport>>([]);
        public Task<IReadOnlyList<QuarantinedImportSummary>> ListQuarantineAsync(string? statusFilter = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<QuarantinedImportSummary>>([]);
        public Task<QuarantinedImportSummary> ApproveQuarantineAsync(long quarantineId, long reviewerUserId, string confirmedLanguage, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<QuarantinedImportSummary> RejectAndBlocklistQuarantineAsync(long quarantineId, long reviewerUserId, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
