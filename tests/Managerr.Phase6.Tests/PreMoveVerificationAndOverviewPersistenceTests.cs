using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Managerr.Catalog;
using Managerr.Datastore;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
using Managerr.Host.Services;
using Managerr.Metadata;
using Managerr.Profiles;
using Managerr.Verification;

namespace Managerr.Phase6.Tests;

public sealed class PreMoveVerificationAndOverviewPersistenceTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task MultiEpisodeSeasonPack_PreMoveVerification_MovesAndPersistsOverview()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"premove-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var tvRoot = Path.Combine(tempDir, "storage", "tv");
            var usenetRoot = Path.Combine(tempDir, "downloads", "usenet");
            Directory.CreateDirectory(tvRoot);
            Directory.CreateDirectory(usenetRoot);

            var jobFolderName = "Example.Series.2026.S01.GERMAN.DL.2160p.DV.HDR.WEB.H265-GRP";
            var jobDir = Path.Combine(usenetRoot, jobFolderName);
            Directory.CreateDirectory(jobDir);

            // Stage 8 S01E01..S01E08 files in job folder
            var stagedFiles = new List<string>();
            for (var ep = 1; ep <= 8; ep++)
            {
                var filePath = Path.Combine(jobDir, $"Example.Series.2026.S01E{ep:D2}.GERMAN.DL.2160p.DV.HDR.WEB.H265-GRP.mkv");
                await File.WriteAllBytesAsync(filePath, new byte[65_536]);
                stagedFiles.Add(filePath);
            }

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
            var allProfiles = await profiles.ListProfilesAsync();
            var deDualProfile = allProfiles.First(p => p.Slug == "hd-uhd-de-en-dual");

            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("TV", "tv", tvRoot, DefaultProfileId: deDualProfile.Id));

            // Create TV series "Example Series" (OriginalLanguage = "en", Year = 2026, Season 1 with 8 monitored episodes, RequiredAudioLangs = ["de"])
            var createdTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "Example Series",
                WorkKind: "tv",
                OriginalLanguage: "en",
                Year: 2026,
                Monitored: true,
                QualityProfileId: deDualProfile.Id,
                LanguagePolicyId: deDualProfile.LanguagePolicyId,
                InitialRequiredAudioLangs: ["de"],
                Containers:
                [
                    new MetadataContainerSeed("season", 1, "Season 1", "2026-01-01",
                        Enumerable.Range(1, 8).Select(ep =>
                            new MetadataUnitSeed("episode", ep, ep, $"Episode {ep}", "2026-01-01")).ToList())
                ]));

            var s1Container = createdTree.Containers.First(c => c.ContainerNumber == 1);
            var episodeUnits = s1Container.Units.OrderBy(u => u.UnitNumber).ToList();
            Assert.Equal(8, episodeUnits.Count);

            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd",
                Implementation: "sabnzbd",
                Host: "sab",
                Port: 8080,
                ApiKey: "apikey",
                RemoveCompleted: true,
                RemoveFailed: true));

            await RecordGrabHistoryAsync(
                db,
                sabClient.Id,
                "nzo-pack-01",
                null,
                jobFolderName,
                "tv",
                createdTree.Work.Id,
                episodeUnits.Select(u => u.Id).ToList());

            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "nzo-pack-01",
                InfoHash: "nzo-pack-01",
                Title: jobFolderName,
                Category: "tv",
                MediaVertical: "tv",
                Status: "Completed",
                TotalBytes: 8 * 65_536,
                RemainingBytes: 0,
                OutputPath: jobDir,
                ContainedFiles: stagedFiles,
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            fakeVerifier.OnVerify = req => new MediaFileVerificationReport(
                JobId: 200 + (req.UnitId ?? 1),
                UnitId: req.UnitId,
                StagedFilePath: req.StagedFilePath,
                MediaVertical: req.MediaVertical,
                Verdict: VerificationVerdicts.VerifiedPass,
                OverallConfidence: 0.95,
                HeaderAutoPatched: false,
                ExistingFilePreservedOnFailure: false,
                HashBlocklisted: false,
                QuarantineId: null,
                DurationMs: 14,
                VerifiedAudioLanguages: ["de", "en"],
                VerifiedSubtitleLanguages: ["de", "en"],
                Streams:
                [
                    new VerifiedStreamReport(0, "video", "hevc", 0, "und", "und", 1.0, "header"),
                    new VerifiedStreamReport(1, "audio", "eac3", 6, "de", "de", 0.95, "content"),
                    new VerifiedStreamReport(2, "audio", "eac3", 6, "en", "en", 0.94, "content"),
                    new VerifiedStreamReport(3, "subtitle", "subrip", 0, "de", "de", 0.98, "cues"),
                    new VerifiedStreamReport(4, "subtitle", "subrip", 0, "en", "en", 0.98, "cues"),
                ],
                VerifiedAt: DateTimeOffset.UtcNow.ToString("O"));

            var report = await importSvc.ProcessCompletedDownloadsAsync();

            Assert.Equal(1, report.ImportedCount);
            Assert.Equal(0, report.RejectedCount);
            Assert.Equal(0, report.QuarantinedCount);

            // Staging files moved out
            Assert.False(Directory.Exists(jobDir), "Unpacked job folder was not deleted after successful import!");

            // Check series tree in catalog
            var workTree = await catalog.GetWorkTreeAsync(createdTree.Work.Id);
            Assert.NotNull(workTree);
            var updatedUnits = workTree.Containers.First(c => c.ContainerNumber == 1).Units.OrderBy(u => u.UnitNumber).ToList();
            Assert.Equal(8, updatedUnits.Count);

            foreach (var u in updatedUnits)
            {
                Assert.True(u.QualityCutoffMet, $"Unit {u.UnitNumber} did not meet quality cutoff!");
                Assert.True(u.LanguageCutoffMet, $"Unit {u.UnitNumber} did not meet language cutoff!");
                Assert.Single(u.Files);

                var file = u.Files[0];
                Assert.Equal("WEBDL-2160p", file.QualityName);
                Assert.Equal("hevc", file.VideoCodec, ignoreCase: true);
                Assert.Contains("de", file.DetectedAudioLangs);
                Assert.Contains("en", file.DetectedAudioLangs);
                Assert.Contains("de", file.DetectedSubLangs);
                Assert.Contains("en", file.DetectedSubLangs);
                Assert.Equal(VerificationVerdicts.VerifiedPass, file.VerifiedStatus);

                Assert.NotNull(file.LanguageVerification);
                Assert.Equal(VerificationVerdicts.VerifiedPass, file.LanguageVerification.Verdict);
                Assert.True(file.LanguageVerification.OverallConfidence >= 0.80);
                Assert.Contains("de", file.LanguageVerification.DetectedAudioLanguages);
                Assert.Contains("en", file.LanguageVerification.DetectedAudioLanguages);
                Assert.Contains("de", file.LanguageVerification.DetectedSubLanguages);
                Assert.Contains("en", file.LanguageVerification.DetectedSubLanguages);
                Assert.True(file.LanguageVerification.SatisfiesLanguageCutoff == true);
                Assert.NotNull(file.LanguageVerification.MatchedPolicyTier);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PreMoveRejection_WhenLanguagePolicyFails_EvenWithExplicitEnglishReleaseTitle()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"premove-reject-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var tvRoot = Path.Combine(tempDir, "storage", "tv");
            var usenetRoot = Path.Combine(tempDir, "downloads", "usenet");
            Directory.CreateDirectory(tvRoot);
            Directory.CreateDirectory(usenetRoot);

            var jobFolderName = "Example.Series.S01E01.2160p.WEB-DL.English";
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

            // Strict German-only language policy (ONLY Tier 1 requiring "de", NO fallback tier)
            var strictGermanPolicy = await profiles.UpsertLanguagePolicyAsync(new UpsertLanguagePolicyRequest(
                Name: "Strict German Only",
                Slug: "strict-german-only",
                Description: "German audio required; reject English-only",
                Tiers:
                [
                    new LanguagePolicyTier(1, "german-audio", "German Audio", ["de"], [], [], 100.0, true)
                ],
                EnableLanguageUpgrades: false,
                LanguageCutoffTier: 1,
                TradeoffMode: "prefer_language"));

            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("TV", "tv", tvRoot));

            var createdTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "Example Series",
                WorkKind: "tv",
                OriginalLanguage: "en",
                Year: 2026,
                Monitored: true,
                LanguagePolicyId: strictGermanPolicy.Id,
                Containers:
                [
                    new MetadataContainerSeed("season", 1, "Season 1", "2026-01-01",
                    [
                        new MetadataUnitSeed("episode", 1, 1, "Episode 01", "2026-01-01")
                    ])
                ]));

            var unitId = createdTree.Containers[0].Units[0].Id;

            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd",
                Implementation: "sabnzbd",
                Host: "sab",
                Port: 8080,
                ApiKey: "apikey"));

            await RecordGrabHistoryAsync(
                db,
                sabClient.Id,
                "nzo-en-only",
                null,
                jobFolderName,
                "tv",
                createdTree.Work.Id,
                [unitId]);

            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "nzo-en-only",
                InfoHash: "nzo-en-only",
                Title: jobFolderName,
                Category: "tv",
                MediaVertical: "tv",
                Status: "Completed",
                TotalBytes: 65_536,
                RemainingBytes: 0,
                OutputPath: jobDir,
                ContainedFiles: [stagedFile],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            fakeVerifier.OnVerify = req =>
            {
                // Simulate LanguageVerificationService.VerifyStagedFileAsync setting LanguageCutoffMet=true and inserting verification_jobs row on VerifiedPass
                if (req.UnitId.HasValue)
                {
                    catalog.UpdateUnitAsync(req.UnitId.Value, new UpdateMediaUnitRequest(LanguageCutoffMet: true)).GetAwaiter().GetResult();
                }

                var insertedJobId = db.ExecuteWriteAsync(DatabaseKind.Core, (conn, tx) => conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO verification_jobs (
                        unit_id, media_file_id, staged_file_path, media_vertical, verdict,
                        overall_confidence, header_auto_patched, detected_audio_langs_json, detected_sub_langs_json,
                        report_json, duration_ms, created_at
                    ) VALUES (
                        @Uid, NULL, @Path, @Vert, 'VerifiedPass', 0.98, 0, '["en"]', '[]', '{}', 10, @Now
                    );
                    SELECT last_insert_rowid();
                    """,
                    new { Uid = req.UnitId, Path = req.StagedFilePath, Vert = req.MediaVertical, Now = DateTimeOffset.UtcNow.ToString("O") },
                    tx)).GetAwaiter().GetResult();

                return new MediaFileVerificationReport(
                    JobId: insertedJobId,
                    UnitId: req.UnitId,
                    StagedFilePath: req.StagedFilePath,
                    MediaVertical: req.MediaVertical,
                    Verdict: VerificationVerdicts.VerifiedPass,
                    OverallConfidence: 0.98,
                    HeaderAutoPatched: false,
                    ExistingFilePreservedOnFailure: false,
                    HashBlocklisted: false,
                    QuarantineId: null,
                    DurationMs: 10,
                    VerifiedAudioLanguages: ["en"],
                    VerifiedSubtitleLanguages: [],
                    Streams:
                    [
                        new VerifiedStreamReport(0, "video", "hevc", 0, "und", "und", 1.0, "header"),
                        new VerifiedStreamReport(1, "audio", "eac3", 6, "en", "en", 0.98, "content"),
                    ],
                    VerifiedAt: DateTimeOffset.UtcNow.ToString("O"));
            };

            var report = await importSvc.ProcessCompletedDownloadsAsync();

            Assert.Equal(0, report.ImportedCount);
            Assert.Equal(1, report.RejectedCount);
            Assert.Equal(VerificationVerdicts.VerifiedFail, report.Items[0].Verdict);

            // Staged file was NOT moved into the library root, LanguageCutoffMet was rolled back, and verification_jobs reflects VerifiedFail
            var updatedTree = await catalog.GetWorkTreeAsync(createdTree.Work.Id);
            Assert.NotNull(updatedTree);
            var updatedUnit = updatedTree.Containers[0].Units[0];
            Assert.Empty(updatedUnit.Files);
            Assert.False(updatedUnit.LanguageCutoffMet);

            await using var readConn = await db.OpenReadConnectionAsync(DatabaseKind.Core);
            var persistedVerdict = await readConn.ExecuteScalarAsync<string>(
                "SELECT verdict FROM verification_jobs WHERE unit_id = @Uid ORDER BY id DESC LIMIT 1;",
                new { Uid = unitId });
            Assert.Equal(VerificationVerdicts.VerifiedFail, persistedVerdict);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CompanionSidecarSubtitles_DetectedPreMove_AndPersistedToOverview()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sidecar-sub-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var animeRoot = Path.Combine(tempDir, "storage", "anime");
            var usenetRoot = Path.Combine(tempDir, "downloads", "usenet");
            Directory.CreateDirectory(animeRoot);
            Directory.CreateDirectory(usenetRoot);

            var jobFolderName = "AnimeShow.S01E01.1080p.WEB";
            var jobDir = Path.Combine(usenetRoot, jobFolderName);
            Directory.CreateDirectory(jobDir);
            var stagedVideoFile = Path.Combine(jobDir, $"{jobFolderName}.mkv");
            var stagedSubFile = Path.Combine(jobDir, $"{jobFolderName}.de.srt");
            await File.WriteAllBytesAsync(stagedVideoFile, new byte[65_536]);
            await File.WriteAllTextAsync(stagedSubFile, "1\n00:00:01,000 --> 00:00:04,000\nHallo Welt\n");

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

            // Policy requiring Japanese audio + German subtitles
            var subPolicy = await profiles.UpsertLanguagePolicyAsync(new UpsertLanguagePolicyRequest(
                Name: "Japanese with German Subs",
                Slug: "ja-de-subs",
                Description: "Japanese audio with German subtitles",
                Tiers:
                [
                    new LanguagePolicyTier(1, "ja-audio-de-subs", "Japanese Audio + German Subs", ["ja"], [], ["de"], 100.0, true)
                ],
                EnableLanguageUpgrades: false,
                LanguageCutoffTier: 1,
                TradeoffMode: "prefer_language"));

            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Anime", "anime", animeRoot));

            var createdTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "AnimeShow",
                WorkKind: "anime",
                OriginalLanguage: "ja",
                Year: 2026,
                Monitored: true,
                LanguagePolicyId: subPolicy.Id,
                Containers:
                [
                    new MetadataContainerSeed("season", 1, "Season 1", "2026-01-01",
                    [
                        new MetadataUnitSeed("episode", 1, 1, "Episode 01", "2026-01-01")
                    ])
                ]));

            var unitId = createdTree.Containers[0].Units[0].Id;

            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd",
                Implementation: "sabnzbd",
                Host: "sab",
                Port: 8080,
                ApiKey: "apikey",
                RemoveCompleted: true));

            await RecordGrabHistoryAsync(
                db,
                sabClient.Id,
                "nzo-anime-sub",
                null,
                jobFolderName,
                "anime",
                createdTree.Work.Id,
                [unitId]);

            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "nzo-anime-sub",
                InfoHash: "nzo-anime-sub",
                Title: jobFolderName,
                Category: "anime",
                MediaVertical: "anime",
                Status: "Completed",
                TotalBytes: 65_536,
                RemainingBytes: 0,
                OutputPath: jobDir,
                ContainedFiles: [stagedVideoFile, stagedSubFile],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            // Verifier detects genuine Japanese audio in the mkv, but no internal subtitles
            fakeVerifier.OnVerify = req => new MediaFileVerificationReport(
                JobId: 401,
                UnitId: req.UnitId,
                StagedFilePath: req.StagedFilePath,
                MediaVertical: req.MediaVertical,
                Verdict: VerificationVerdicts.VerifiedPass,
                OverallConfidence: 0.97,
                HeaderAutoPatched: false,
                ExistingFilePreservedOnFailure: false,
                HashBlocklisted: false,
                QuarantineId: null,
                DurationMs: 8,
                VerifiedAudioLanguages: ["ja"],
                VerifiedSubtitleLanguages: [],
                Streams:
                [
                    new VerifiedStreamReport(0, "video", "h264", 0, "und", "und", 1.0, "header"),
                    new VerifiedStreamReport(1, "audio", "aac", 2, "ja", "ja", 0.97, "content"),
                ],
                VerifiedAt: DateTimeOffset.UtcNow.ToString("O"));

            var report = await importSvc.ProcessCompletedDownloadsAsync();

            Assert.Equal(1, report.ImportedCount);
            Assert.Equal(VerificationVerdicts.VerifiedPass, report.Items[0].Verdict);

            // Overview shows companion sidecar language detected and cutoff satisfied!
            var workTree = await catalog.GetWorkTreeAsync(createdTree.Work.Id);
            Assert.NotNull(workTree);
            var unit = workTree.Containers[0].Units[0];
            Assert.Single(unit.Files);
            var file = unit.Files[0];

            Assert.Contains("de", file.DetectedSubLangs);
            Assert.True(unit.LanguageCutoffMet);
            Assert.NotNull(file.LanguageVerification);
            Assert.Contains("de", file.LanguageVerification.DetectedSubLanguages);
            Assert.True(file.LanguageVerification.SatisfiesLanguageCutoff == true);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ForeignClientCategory_IgnoredWhenUngrabbed_AndReconcilesWhenExternallyMoved()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"foreign-cat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var tvRoot = Path.Combine(tempDir, "storage", "tv");
            Directory.CreateDirectory(tvRoot);

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

            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("TV", "tv", tvRoot));

            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd",
                Implementation: "sabnzbd",
                Host: "sab",
                Port: 8080,
                ApiKey: "apikey",
                CategoryMappings: new Dictionary<string, string>
                {
                    ["tv"] = "tv-managerr"
                }));

            // Part A: Ungrabbed download in foreign category "series"
            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "nzo-foreign-series",
                InfoHash: "nzo-foreign-series",
                Title: "SonarrManagedSeries.S01E01.720p",
                Category: "series", // Foreign category not configured in CategoryMappings
                MediaVertical: "tv",
                Status: "Completed",
                TotalBytes: 50_000,
                RemainingBytes: 0,
                OutputPath: Path.Combine(tempDir, "nonexistent"),
                ContainedFiles: [],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            var reportA = await importSvc.ProcessCompletedDownloadsAsync();
            Assert.Equal(0, reportA.EvaluatedCount);
            Assert.Equal(0, reportA.ImportedCount);

            var trackedDownloads = await importSvc.ListTrackedDownloadsAsync();
            Assert.DoesNotContain(trackedDownloads, t => t.ExternalDownloadId == "nzo-foreign-series");

            // Part B: Correlated grab history download whose .mkv was already moved into the series folder on disk
            var seriesTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "ExternalShow",
                WorkKind: "tv",
                OriginalLanguage: "en",
                Year: 2026,
                Monitored: true,
                Containers:
                [
                    new MetadataContainerSeed("season", 1, "Season 1", "2026-01-01",
                    [
                        new MetadataUnitSeed("episode", 1, 1, "Episode 01", "2026-01-01")
                    ])
                ]));

            var extUnit = seriesTree.Containers[0].Units[0];

            // Put file directly into library on disk
            var seriesDir = Path.Combine(tvRoot, "ExternalShow (2026)", "Season 01");
            Directory.CreateDirectory(seriesDir);
            var diskFile = Path.Combine(seriesDir, "ExternalShow - S01E01.mkv");
            await File.WriteAllBytesAsync(diskFile, new byte[65_536]);

            await RecordGrabHistoryAsync(
                db,
                sabClient.Id,
                "nzo-externally-moved",
                null,
                "ExternalShow.S01E01.1080p.WEB",
                "tv",
                seriesTree.Work.Id,
                [extUnit.Id]);

            // Staging folder does NOT contain files (was already emptied / moved externally)
            var emptyStagingDir = Path.Combine(tempDir, "downloads", "ExternalShow.S01E01.1080p.WEB");
            Directory.CreateDirectory(emptyStagingDir);

            fakeGateway.QueueItems.Clear();
            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "nzo-externally-moved",
                InfoHash: "nzo-externally-moved",
                Title: "ExternalShow.S01E01.1080p.WEB",
                Category: "tv-managerr",
                MediaVertical: "tv",
                Status: "Completed",
                TotalBytes: 65_536,
                RemainingBytes: 0,
                OutputPath: emptyStagingDir,
                ContainedFiles: [],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            fakeVerifier.OnVerify = req => new MediaFileVerificationReport(
                JobId: 501,
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
                VerifiedAudioLanguages: ["en"],
                VerifiedSubtitleLanguages: [],
                Streams:
                [
                    new VerifiedStreamReport(0, "video", "h264", 0, "und", "und", 1.0, "header"),
                    new VerifiedStreamReport(1, "audio", "aac", 2, "en", "en", 0.99, "content"),
                ],
                VerifiedAt: DateTimeOffset.UtcNow.ToString("O"));

            var reportB = await importSvc.ProcessCompletedDownloadsAsync();

            Assert.Equal(1, reportB.ImportedCount);
            Assert.Equal(VerificationVerdicts.VerifiedPass, reportB.Items[0].Verdict);

            // Tracked download marked as Imported (ok)
            var trackedB = (await importSvc.ListTrackedDownloadsAsync()).FirstOrDefault(t => t.ExternalDownloadId == "nzo-externally-moved");
            Assert.NotNull(trackedB);
            Assert.Equal(TrackedDownloadStates.Imported, trackedB.State);
            Assert.Equal("ok", trackedB.TrackedStatus);

            // Unit in overview has attached file
            var refreshedWork = await catalog.GetWorkTreeAsync(seriesTree.Work.Id);
            Assert.NotNull(refreshedWork);
            Assert.Single(refreshedWork.Containers[0].Units[0].Files);
            Assert.Equal(VerificationVerdicts.VerifiedPass, refreshedWork.Containers[0].Units[0].Files[0].VerifiedStatus);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EmptyUpgradeStaging_WithPreExistingBaselineFile_DoesNotFalselyReconcileAsImported()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"stale-baseline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var tvRoot = Path.Combine(tempDir, "storage", "tv");
            Directory.CreateDirectory(tvRoot);

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
            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("TV", "tv", tvRoot));

            var seriesTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "BaselineShow",
                WorkKind: "tv",
                OriginalLanguage: "en",
                Year: 2026,
                Monitored: true,
                Containers:
                [
                    new MetadataContainerSeed("season", 1, "Season 1", "2026-01-01",
                    [
                        new MetadataUnitSeed("episode", 1, 1, "Episode 01", "2026-01-01")
                    ])
                ]));

            var unit = seriesTree.Containers[0].Units[0];

            // Pre-existing 720p baseline file on disk from 10 days ago
            var seriesDir = Path.Combine(tvRoot, "BaselineShow (2026)", "Season 01");
            Directory.CreateDirectory(seriesDir);
            var oldDiskFile = Path.Combine(seriesDir, "BaselineShow - S01E01 - WEBDL-720p.mkv");
            await File.WriteAllBytesAsync(oldDiskFile, new byte[65_536]);
            var tenDaysAgo = DateTimeOffset.UtcNow.AddDays(-10);
            File.SetLastWriteTimeUtc(oldDiskFile, tenDaysAgo.UtcDateTime);

            var attachedOld = await catalog.AttachFileAsync(new AttachMediaFileRequest(
                UnitId: unit.Id,
                RelativePath: Path.GetRelativePath(tvRoot, oldDiskFile),
                SizeBytes: 65_536,
                QualityName: "WEBDL-720p",
                DetectedAudioLangs: ["en"],
                DetectedSubLangs: [],
                VerifiedStatus: VerificationVerdicts.VerifiedPass));

            await db.ExecuteWriteAsync(DatabaseKind.Core, (conn, tx) => conn.ExecuteAsync(
                "UPDATE media_files SET imported_at = @OldAt WHERE id = @Fid;",
                new { OldAt = tenDaysAgo.ToString("O"), Fid = attachedOld.Id }, tx));

            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd",
                Implementation: "sabnzbd",
                Host: "sab",
                Port: 8080,
                ApiKey: "apikey",
                RemoveCompleted: true));

            // Grab a 2160p upgrade NOW, whose staging directory has NO media files (broken/empty unpack)
            await RecordGrabHistoryAsync(
                db,
                sabClient.Id,
                "nzo-empty-upgrade",
                null,
                "BaselineShow.S01E01.GERMAN.DL.2160p.WEB",
                "tv",
                seriesTree.Work.Id,
                [unit.Id]);

            var emptyStagingDir = Path.Combine(tempDir, "downloads", "BaselineShow.S01E01.GERMAN.DL.2160p.WEB");
            Directory.CreateDirectory(emptyStagingDir);

            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "nzo-empty-upgrade",
                InfoHash: "nzo-empty-upgrade",
                Title: "BaselineShow.S01E01.GERMAN.DL.2160p.WEB",
                Category: "tv-managerr",
                MediaVertical: "tv",
                Status: "Completed",
                TotalBytes: 65_536,
                RemainingBytes: 0,
                OutputPath: emptyStagingDir,
                ContainedFiles: [],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            var report = await importSvc.ProcessCompletedDownloadsAsync();

            Assert.Equal(0, report.ImportedCount);
            Assert.Equal(1, report.RejectedCount);
            Assert.Equal("ImportBlocked", report.Items[0].Verdict);
            Assert.DoesNotContain("nzo-empty-upgrade", fakeGateway.RemovedDownloads);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task TryDeleteImportedUsenetJobFolder_RefusesSymlinkAndSymlinkedAncestorTargetingLibrary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"symlink-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var tvRoot = Path.Combine(tempDir, "storage", "tv");
            var seriesFolder = Path.Combine(tvRoot, "Example.Series.2026.S01");
            Directory.CreateDirectory(seriesFolder);
            var protectedEpisode = Path.Combine(seriesFolder, "Example.Series - S01E01.mkv");
            await File.WriteAllBytesAsync(protectedEpisode, new byte[4096]);

            var downloadsDir = Path.Combine(tempDir, "downloads", "usenet");
            Directory.CreateDirectory(downloadsDir);

            // 1. Direct directory symlink pointing into the library
            var directSymlink = Path.Combine(downloadsDir, "Example.Series.2026.S01");
            Directory.CreateSymbolicLink(directSymlink, seriesFolder);

            // 2. Ancestor directory symlink whose child is the real series folder inside the library
            var ancestorSymlink = Path.Combine(tempDir, "downloads", "tv-alias");
            Directory.CreateSymbolicLink(ancestorSymlink, tvRoot);
            var viaAncestorPath = Path.Combine(ancestorSymlink, "Example.Series.2026.S01");

            var libs = new List<LibrarySummary>
            {
                new(1, "tv", "TV", "tv", tvRoot, null, ["tmdb"], null, 0, "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z")
            };

            foreach (var dangerousOutPath in new[] { directSymlink, viaAncestorPath })
            {
                var item = new ActiveDownloadQueueItem(
                    ClientId: 1,
                    ClientName: "SABnzbd",
                    Protocol: "usenet",
                    ExternalId: "nzo-symlink",
                    InfoHash: null,
                    Title: "Example.Series.2026.S01",
                    Category: "tv-managerr",
                    MediaVertical: "tv",
                    Status: "Completed",
                    TotalBytes: 4096,
                    RemainingBytes: 0,
                    OutputPath: dangerousOutPath,
                    ContainedFiles: [],
                    IsStalled: false,
                    IsMetadataStuck: false,
                    ErrorMessage: null,
                    CanMoveFiles: true);

                CompletedDownloadImportService.TryDeleteImportedUsenetJobFolder(item, libs);
                Assert.True(File.Exists(protectedEpisode), $"Library episode was deleted via '{dangerousOutPath}'!");
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Concurrent_RequestImmediateDownloadCheck_Burst_NeverThrowsObjectDisposedException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"concurrent-burst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var fakeGateway = new FakeGateway();
            var fakeVerifier = new FakeLanguageVerificationService();

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                    ["Managerr:MasterKey"] = "concurrent-burst-test-master-key-32b-length",
                    ["Managerr:SchedulerEnabled"] = "true",
                    ["Managerr:PostGrabDebounceMs"] = "50",
                    ["Managerr:SchedulerTickMs"] = "50",
                }));
                b.ConfigureServices(s =>
                {
                    s.RemoveAll<IDownloadClientDriverGateway>();
                    s.AddSingleton<IDownloadClientDriverGateway>(fakeGateway);

                    s.RemoveAll<ILanguageVerificationService>();
                    s.AddSingleton<ILanguageVerificationService>(fakeVerifier);
                });
            });

            // Trigger background service
            _ = factory.Server;

            using var scope = factory.Services.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();

            // Trigger 30 times concurrently across parallel tasks
            var burstTasks = Enumerable.Range(0, 30)
                .Select(_ => Task.Run(() => orchestrator.RequestImmediateDownloadCheck()))
                .ToArray();

            await Task.WhenAll(burstTasks);

            // Wait a moment for debounced Task.Run in scheduler to complete without unhandled exception
            await Task.Delay(250);
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
                ["Managerr:MasterKey"] = "premove-pack-master-key-32b-len",
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
                VerifiedAudioLanguages: request.ExpectedAudioLanguages ?? ["de", "en"],
                VerifiedSubtitleLanguages: ["de", "en"],
                Streams: [],
                VerifiedAt: DateTimeOffset.UtcNow.ToString("O")));
        }

        public Task<IReadOnlyList<MediaFileVerificationReport>> ListRecentReportsAsync(int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MediaFileVerificationReport>>([]);
        public Task<IReadOnlyList<QuarantinedImportSummary>> ListQuarantineAsync(string? statusFilter = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<QuarantinedImportSummary>>([]);
        public Task<QuarantinedImportSummary> ApproveQuarantineAsync(long quarantineId, long reviewerUserId, string confirmedLanguage, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<QuarantinedImportSummary> RejectAndBlocklistQuarantineAsync(long quarantineId, long reviewerUserId, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
