using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Dapper;
using Managerr.Catalog;
using Managerr.Datastore;
using Managerr.Host.Endpoints;
using Managerr.Profiles;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Managerr.Verification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Managerr.Phase4.Tests;

public sealed class Phase4ProfilesAndVerificationTests
{
    [Fact]
    public async Task MultiAxisProfiles_DualCutoffLanguageUpgrade_QualityFloorGuard_DynamicOriginalToken_AndTrashImport()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var scope = factory.Services.CreateScope();
            var profileSvc = scope.ServiceProvider.GetRequiredService<IComposableProfileService>();
            var trashImporter = scope.ServiceProvider.GetRequiredService<ITrashGuidesImporter>();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var users = scope.ServiceProvider.GetRequiredService<IPlexUserService>();

            var profiles = await profileSvc.ListProfilesAsync();
            var uhdDualProfile = Assert.Single(profiles, p => p.Slug == "hd-uhd-de-en-dual");

            // 1. Day-1 English 2160p WEB-DL grab -> Approved as NewGrab; QualityCutoffMet=true, LanguageCutoffMet=false
            var day1Card = await profileSvc.SimulateAsync(new SimulateReleaseRequest(
                ProfileId: uhdDualProfile.Id,
                ReleaseTitle: "Dune.Part.Two.2024.2160p.AMZN.WEB-DL.DDP5.1.Atmos.H.265-FLUX.mkv",
                OriginalLanguage: "en",
                ExplicitAudioLanguages: ["en"]));
            Assert.True(day1Card.Approved);
            Assert.Equal(ProfileDecisionKinds.NewGrab, day1Card.DecisionKind);
            Assert.True(day1Card.CandidateSatisfiesQualityCutoff);
            Assert.False(day1Card.CandidateSatisfiesLanguageCutoff);

            // 2. German + English Dual-Audio 2160p WEB-DL arrives later -> Approved as LanguageUpgrade!
            var dualUpgradeCard = await profileSvc.SimulateAsync(new SimulateReleaseRequest(
                ProfileId: uhdDualProfile.Id,
                ReleaseTitle: "Dune.Part.Two.2024.German.DL.2160p.WEB-DL.DV.HDR10Plus.Atmos.H265-GRP.mkv",
                OriginalLanguage: "en",
                ExplicitAudioLanguages: ["de", "en"],
                ExistingFileQuality: "WEBDL-2160p",
                ExistingFileAudioLanguages: ["en"]));
            Assert.True(dualUpgradeCard.Approved);
            Assert.Equal(ProfileDecisionKinds.LanguageUpgrade, dualUpgradeCard.DecisionKind);
            Assert.True(dualUpgradeCard.CandidateSatisfiesLanguageCutoff);
            Assert.Equal(1, dualUpgradeCard.MatchedLanguageTier);

            // 3. Low-quality German 720p HDTV arrives against existing 2160p WEB-DL [EN] -> BlockedByQualityFloor!
            var floorBlockedCard = await profileSvc.SimulateAsync(new SimulateReleaseRequest(
                ProfileId: uhdDualProfile.Id,
                ReleaseTitle: "Dune.Part.Two.2024.German.720p.HDTV.x264-GRP.mkv",
                OriginalLanguage: "en",
                ExplicitAudioLanguages: ["de"],
                ExistingFileQuality: "WEBDL-2160p",
                ExistingFileAudioLanguages: ["en"]));
            Assert.False(floorBlockedCard.Approved);
            Assert.Equal(ProfileDecisionKinds.BlockedByQualityFloor, floorBlockedCard.DecisionKind);

            // 4. Dynamic "original" token resolves to "ja" on an Anime work so German + Japanese satisfies Tier 1 (["de", "original"])
            var animeCard = await profileSvc.SimulateAsync(new SimulateReleaseRequest(
                ProfileId: uhdDualProfile.Id,
                ReleaseTitle: "Frieren.S01E01.German.DL.2160p.WEB-DL.HEVC-ANIME.mkv",
                OriginalLanguage: "ja",
                ExplicitAudioLanguages: ["de", "ja"]));
            Assert.True(animeCard.Approved);
            Assert.True(animeCard.CandidateSatisfiesLanguageCutoff);
            Assert.Equal(1, animeCard.MatchedLanguageTier);

            // 5. 1-Click TRaSH-Guides Custom Format JSON Importer + LanguageSpecification uplift warning
            const string trashJson = """
                [
                  {
                    "trash_id": "e7718d7a3ce595f289bfee26adc178f5",
                    "name": "Repack/Proper",
                    "includeCustomFormatWhenRenaming": false,
                    "trash_scores": { "default": 5 },
                    "specifications": [
                      {
                        "name": "Repack",
                        "implementation": "ReleaseTitleSpecification",
                        "negate": false,
                        "required": false,
                        "fields": { "value": "\\b(REPACK|PROPER)\\b" }
                      }
                    ]
                  },
                  {
                    "trash_id": "lang-hack-de",
                    "name": "Legacy German CF",
                    "trash_scores": { "default": 500 },
                    "specifications": [
                      {
                        "name": "Not German",
                        "implementation": "LanguageSpecification",
                        "negate": true,
                        "required": true,
                        "fields": { "value": 4 }
                      }
                    ]
                  }
                ]
                """;
            var importedTrash = await trashImporter.ImportJsonAsync(trashJson);
            Assert.Equal(2, importedTrash.ImportedCount);
            Assert.Single(importedTrash.LanguageRulesDetectedForUplift);

            // 6. PlexUser Language Promotion re-arms LanguageCutoffMet=false when a French-preferring Plex user subscribes
            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Movies", "movies", Path.Combine(tempDir, "movies"), PlexSectionId: "1"));
            var work = await catalog.AddWorkAsync(new CreateMediaWorkRequest(lib.Id, "Anatomy of a Fall", Year: 2023, InitialRequiredAudioLangs: ["en"]));
            var unit = work.Containers[0].Units[0];
            await catalog.AttachFileAsync(new AttachMediaFileRequest(unit.Id, "Anatomy.mkv", 5_000_000, "WEBDL-2160p", DetectedAudioLangs: ["en"]));
            await catalog.UpdateUnitAsync(unit.Id, new UpdateMediaUnitRequest(QualityCutoffMet: true, LanguageCutoffMet: true));

            var frenchFriend = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
                PlexUserId: 7777,
                PlexUuid: "fr-uuid",
                Username: "amélie",
                Email: "amelie@example.com",
                AvatarUrl: null,
                IsServerOwner: false,
                IsHomeUser: false,
                IsActive: true,
                SharedPlexSectionIds: ["1"],
                PreferredAudioLang: "fr"));

            var promotedTree = await profileSvc.PromotePlexUserLanguageAsync(work.Work.Id, frenchFriend);
            var promotedUnit = promotedTree.Containers[0].Units[0];
            Assert.Contains("fr", promotedUnit.RequiredAudioLangs);
            Assert.False(promotedUnit.LanguageCutoffMet);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task ThreeTierVerification_RealEpubZip_ForcedVsFullSubtitles_HybridExtendedDub_FakeDuplicateDub_AndMkvAutoPatch()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var scope = factory.Services.CreateScope();
            var verifier = scope.ServiceProvider.GetRequiredService<ILanguageVerificationService>();
            var dbFactory = scope.ServiceProvider.GetRequiredService<ISqliteDbFactory>();

            // 1. Real staged EPUB ZIP with lying <dc:language>en</dc:language> OPF tag but actual German XHTML chapters
            var epubPath = Path.Combine(tempDir, "Mislabeled-Book.epub");
            CreateRealGermanEpubWithEnglishOpfLie(epubPath);

            var epubFailReport = await verifier.VerifyStagedFileAsync(new RunVerificationRequest(
                StagedFilePath: epubPath,
                MediaVertical: "ebooks",
                ReleaseTitle: "Book.Retail.English.EPUB",
                ExpectedAudioLanguages: ["en"]));
            Assert.Equal(VerificationVerdicts.VerifiedFail, epubFailReport.Verdict);
            Assert.Contains("de", epubFailReport.VerifiedAudioLanguages);

            var epubPassReport = await verifier.VerifyStagedFileAsync(new RunVerificationRequest(
                StagedFilePath: epubPath,
                MediaVertical: "ebooks",
                ReleaseTitle: "Book.Retail.German.EPUB",
                ExpectedAudioLanguages: ["de"]));
            Assert.Equal(VerificationVerdicts.VerifiedPass, epubPassReport.Verdict);

            // 2. Forced (12 cues) vs Full (140 cues) German SRT subtitle verification
            var forcedSrtPath = Path.Combine(tempDir, "Movie.de.forced.srt");
            await File.WriteAllTextAsync(forcedSrtPath, BuildGermanSrt(cueCount: 12));
            var forcedReject = await verifier.VerifyStagedFileAsync(new RunVerificationRequest(
                StagedFilePath: forcedSrtPath,
                MediaVertical: "movies",
                ExpectedSubtitleLanguages: ["de"],
                RequireFullSubtitles: true));
            Assert.Equal(VerificationVerdicts.VerifiedFail, forcedReject.Verdict);
            Assert.Equal("forced", forcedReject.Streams[0].CoverageClassification);

            var fullSrtPath = Path.Combine(tempDir, "Movie.de.full.srt");
            await File.WriteAllTextAsync(fullSrtPath, BuildGermanSrt(cueCount: 140));
            var fullPass = await verifier.VerifyStagedFileAsync(new RunVerificationRequest(
                StagedFilePath: fullSrtPath,
                MediaVertical: "movies",
                ExpectedSubtitleLanguages: ["de"],
                RequireFullSubtitles: true));
            Assert.Equal(VerificationVerdicts.VerifiedPass, fullPass.Verdict);
            Assert.Equal("full", fullPass.Streams[0].CoverageClassification);

            // 3. Stratified 5-window German Extended-Cut Hybrid Dub (3/5 'de' + 2/5 'en') -> VerifiedPass with IsHybridExtendedCutDub!
            var customSpeechAdapter = new ScriptedSpeechAdapter(stream =>
                stream.StreamIndex == 1
                    ? [
                        new SpeechWindowSample(600, 9.5, "de", 0.96, [0.1f, -0.2f, 0.4f, -0.1f, 0.3f, -0.5f, 0.2f, 0.1f]),
                        new SpeechWindowSample(1200, 9.5, "en", 0.93, [0.1f, -0.2f, 0.4f, -0.1f, 0.3f, -0.5f, 0.2f, 0.1f]),
                        new SpeechWindowSample(1800, 9.5, "de", 0.95, [0.1f, -0.2f, 0.4f, -0.1f, 0.3f, -0.5f, 0.2f, 0.1f]),
                        new SpeechWindowSample(2400, 9.5, "en", 0.91, [0.1f, -0.2f, 0.4f, -0.1f, 0.3f, -0.5f, 0.2f, 0.1f]),
                        new SpeechWindowSample(3000, 9.5, "de", 0.97, [0.1f, -0.2f, 0.4f, -0.1f, 0.3f, -0.5f, 0.2f, 0.1f]),
                    ]
                    : [
                        new SpeechWindowSample(600, 9.5, "en", 0.98, [-0.4f, 0.5f, 0.1f, 0.3f, -0.2f, 0.1f, -0.3f, 0.4f]),
                        new SpeechWindowSample(1200, 9.5, "en", 0.98, [-0.4f, 0.5f, 0.1f, 0.3f, -0.2f, 0.1f, -0.3f, 0.4f]),
                        new SpeechWindowSample(1800, 9.5, "en", 0.98, [-0.4f, 0.5f, 0.1f, 0.3f, -0.2f, 0.1f, -0.3f, 0.4f]),
                    ]);

            var customVerifier = new LanguageVerificationService(
                dbFactory,
                scope.ServiceProvider.GetRequiredService<IContainerProbeAdapter>(),
                scope.ServiceProvider.GetRequiredService<ITextLanguageDetector>(),
                scope.ServiceProvider.GetRequiredService<ISubtitleOcrAdapter>(),
                customSpeechAdapter,
                scope.ServiceProvider.GetRequiredService<IMkvHeaderPatcher>(),
                scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>());

            var mkvPath = Path.Combine(tempDir, "Aliens.Extended.Cut.mkv");
            await File.WriteAllBytesAsync(mkvPath, [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01]);

            var hybridReport = await customVerifier.VerifyStagedFileAsync(new RunVerificationRequest(
                StagedFilePath: mkvPath,
                MediaVertical: "movies",
                OriginalLanguage: "en",
                ExpectedAudioLanguages: ["de", "original"],
                SyntheticProbeOverride: new ContainerProbeResult(
                    IntegrityPassed: true,
                    IntegrityFailureReason: null,
                    ContainerFormat: "mkv",
                    DurationSeconds: 7200,
                    Streams:
                    [
                        new ProbedRawStream(1, "audio", "eac3", 6, "und"),
                        new ProbedRawStream(2, "audio", "eac3", 6, "en"),
                    ])));

            // Track 1 had DeclaredLanguage="und", detected as Hybrid Extended Cut 'de', and auto-patched via mkvpropedit!
            Assert.Equal(VerificationVerdicts.VerifiedPassAutoTagged, hybridReport.Verdict);
            Assert.True(hybridReport.HeaderAutoPatched);
            Assert.True(hybridReport.Streams[0].IsHybridExtendedCutDub);
            Assert.Equal("deu", hybridReport.Streams[0].AppliedTagPatch);

            // 4. Fake Duplicate MULTI Dub (identical PCM waveform across Track 1 'en' and Track 2 'de') -> VerifiedFail + Hash Blocklisted!
            float[] identicalPcm = [0.25f, -0.40f, 0.65f, -0.15f, 0.50f, -0.75f, 0.30f, -0.10f, 0.45f, -0.20f];
            var fakeDubReport = await verifier.VerifyStagedFileAsync(new RunVerificationRequest(
                StagedFilePath: mkvPath,
                MediaVertical: "movies",
                ReleaseTitle: "Movie.2025.MULTI.1080p.WEB-DL-FAKEGRP",
                InfoHash: "deadbeefcafebabe00112233445566778899aabb",
                ExpectedAudioLanguages: ["de", "en"],
                SyntheticProbeOverride: new ContainerProbeResult(
                    IntegrityPassed: true,
                    IntegrityFailureReason: null,
                    ContainerFormat: "mkv",
                    DurationSeconds: 5400,
                    Streams:
                    [
                        new ProbedRawStream(1, "audio", "aac", 2, "en", AudioWaveformPreview: identicalPcm),
                        new ProbedRawStream(2, "audio", "aac", 2, "de", AudioWaveformPreview: identicalPcm),
                    ])));

            Assert.Equal(VerificationVerdicts.VerifiedFail, fakeDubReport.Verdict);
            Assert.True(fakeDubReport.ExistingFilePreservedOnFailure);
            Assert.True(fakeDubReport.HashBlocklisted);
            Assert.Contains(fakeDubReport.Streams, s => s.IsDuplicateFakeDub);

            await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core);
            var blockedCount = await readConn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM recurring_blocklist_hashes WHERE info_hash = 'deadbeefcafebabe00112233445566778899aabb';");
            Assert.Equal(1, blockedCount);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task HttpEndpoints_ProfileSimulation_QuarantineOpusSnippets_ApproveAndReject_EnforceRbac()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var client = factory.CreateClient();

            using var scope = factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IPlexUserService>();
            var auth = scope.ServiceProvider.GetRequiredService<IPlexAuthAndSessionService>();
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();

            var admin = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
                1, "admin-uuid", "plexowner", "owner@plex.local", null, true, true, true, ["1"]));
            var requester = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
                2, "req-uuid", "plexfriend", "friend@plex.local", null, false, false, true, ["1"]));
            var requesterRole = (await roles.ListRolesAsync()).Single(r => r.Key == "media:requester");
            requester = await users.AssignRolesAsync(requester.Id, [requesterRole.Id]);

            var adminKey = await auth.CreateApiKeyAsync(admin.Id, "Admin Test Key", PermissionScopes.AllValidScopes.ToList(), null, null, null);
            var friendSession = await auth.CreateSessionForUserAsync(requester, "xunit", "127.0.0.1");

            // Requester is forbidden from Quarantine Review
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", friendSession.RawSessionToken);
            var forbiddenQuarantine = await client.GetAsync("/api/v1/quarantine");
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenQuarantine.StatusCode);

            // Switch to Admin API key
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminKey.RawTokenOnce);

            var profiles = await client.GetFromJsonAsync<List<QualityProfileSummary>>("/api/v1/profiles");
            Assert.NotNull(profiles);
            Assert.True(profiles.Count >= 4);

            var simResp = await client.PostAsJsonAsync("/api/v1/profiles/simulate", new SimulateReleaseRequest(
                ProfileId: profiles[0].Id,
                ReleaseTitle: "Blade.Runner.2049.German.DL.2160p.UHD.BluRay.Remux.HEVC-GRP.mkv",
                OriginalLanguage: "en",
                ExplicitAudioLanguages: ["de", "en"]));
            Assert.Equal(HttpStatusCode.OK, simResp.StatusCode);
            var scorecard = (await simResp.Content.ReadFromJsonAsync<ReleaseSimulationScorecard>())!;
            Assert.True(scorecard.Approved);

            // Trigger an ambiguous 'und' scan -> lands in QuarantineReview with .opus audio snippets!
            var stagedMkv = Path.Combine(tempDir, "Ambiguous.Release.mkv");
            await File.WriteAllBytesAsync(stagedMkv, [0x1A, 0x45, 0xDF, 0xA3, 0x01, 0x02]);

            var scanResp = await client.PostAsJsonAsync("/api/v1/verification/scan", new RunVerificationRequest(
                StagedFilePath: stagedMkv,
                MediaVertical: "movies",
                ReleaseTitle: "Ambiguous.MULTI.1080p.WEB-DL",
                InfoHash: "11223344556677889900aabbccddeeff00112233",
                ExpectedAudioLanguages: ["de"],
                SyntheticProbeOverride: new ContainerProbeResult(
                    true,
                    null,
                    "mkv",
                    3600,
                    [new ProbedRawStream(1, "audio", "ac3", 6, "und")])));
            Assert.Equal(HttpStatusCode.OK, scanResp.StatusCode);
            var scanReport = (await scanResp.Content.ReadFromJsonAsync<MediaFileVerificationReport>())!;
            Assert.Equal(VerificationVerdicts.QuarantineReview, scanReport.Verdict);
            Assert.NotNull(scanReport.QuarantineId);

            // Stream .opus snippet 0 from browser review endpoint
            var snippetResp = await client.GetAsync($"/api/v1/quarantine/{scanReport.QuarantineId}/snippets/0");
            Assert.Equal(HttpStatusCode.OK, snippetResp.StatusCode);
            Assert.Contains("audio/ogg", snippetResp.Content.Headers.ContentType?.ToString() ?? "");

            // Reject & blocklist hash via endpoint
            var rejectResp = await client.PostAsync($"/api/v1/quarantine/{scanReport.QuarantineId}/reject", null);
            Assert.Equal(HttpStatusCode.OK, rejectResp.StatusCode);
            var rejected = (await rejectResp.Content.ReadFromJsonAsync<QuarantinedImportSummary>())!;
            Assert.Equal("rejected_blocklisted", rejected.Status);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task GermanFirstBestQualityCustomProfile_AutoBindsToMoviesAndTvLibraries_AndPrefersGermanThenBestQuality()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var scope = factory.Services.CreateScope();
            var profileSvc = scope.ServiceProvider.GetRequiredService<IComposableProfileService>();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();

            var profiles = await profileSvc.ListProfilesAsync();
            var germanBest = Assert.Single(profiles, p => p.Slug == "german-first-best-quality");
            Assert.Equal("German First — Best Quality", germanBest.Name);
            Assert.False(germanBest.IsSystemPreset);
            Assert.Equal("Remux-2160p", germanBest.QualityCutoff);
            Assert.Equal(2.0, germanBest.WeightLanguage);

            // Both Movies and TV Series libraries auto-bind to German First — Best Quality when created without explicit DefaultProfileId
            var moviesLib = await catalog.CreateLibraryAsync(
                new CreateLibraryRequest("Movies", "movies", Path.Combine(tempDir, "movies")));
            var tvLib = await catalog.CreateLibraryAsync(
                new CreateLibraryRequest("Series", "tv", Path.Combine(tempDir, "tv")));
            Assert.Equal(germanBest.Id, moviesLib.DefaultProfileId);
            Assert.Equal(germanBest.Id, tvLib.DefaultProfileId);

            // Movie simulation: German 1080p beats English 2160p (German first!), and German DL 2160p Remux beats German 1080p (Best quality!)
            var movieEn4k = await profileSvc.SimulateAsync(new SimulateReleaseRequest(
                ProfileId: germanBest.Id,
                ReleaseTitle: "Dune.Part.Two.2024.2160p.WEB-DL.DDP5.1.Atmos.HEVC-GRP.mkv",
                OriginalLanguage: "en",
                ExplicitAudioLanguages: ["en"]));
            var movieDe1080p = await profileSvc.SimulateAsync(new SimulateReleaseRequest(
                ProfileId: germanBest.Id,
                ReleaseTitle: "Dune.Part.Two.2024.German.1080p.WEB-DL.DDP5.1.H264-GRP.mkv",
                OriginalLanguage: "en",
                ExplicitAudioLanguages: ["de"]));
            var movieDeRemux4k = await profileSvc.SimulateAsync(new SimulateReleaseRequest(
                ProfileId: germanBest.Id,
                ReleaseTitle: "Dune.Part.Two.2024.German.DL.2160p.UHD.BluRay.Remux.DV.HDR10Plus.TrueHD.Atmos.HEVC-GRP.mkv",
                OriginalLanguage: "en",
                ExplicitAudioLanguages: ["de", "en"]));

            Assert.True(movieDe1080p.TotalWeightedScore > movieEn4k.TotalWeightedScore);
            Assert.True(movieDeRemux4k.TotalWeightedScore > movieDe1080p.TotalWeightedScore);
            Assert.True(movieDeRemux4k.CandidateSatisfiesQualityCutoff);
            Assert.True(movieDeRemux4k.CandidateSatisfiesLanguageCutoff);

            // Series simulation: same German First — Best Quality scoring applies to TV episodes/packs
            var tvEn4k = await profileSvc.SimulateAsync(new SimulateReleaseRequest(
                ProfileId: germanBest.Id,
                ReleaseTitle: "Severance.S02E01.2160p.WEB-DL.DDP5.1.Atmos.H.265-FLUX.mkv",
                OriginalLanguage: "en",
                ExplicitAudioLanguages: ["en"]));
            var tvDe1080p = await profileSvc.SimulateAsync(new SimulateReleaseRequest(
                ProfileId: germanBest.Id,
                ReleaseTitle: "Severance.S02E01.GERMAN.DL.1080p.WEB-DL.DDP5.1.H.265-GRP.mkv",
                OriginalLanguage: "en",
                ExplicitAudioLanguages: ["de", "en"]));
            var tvDe4k = await profileSvc.SimulateAsync(new SimulateReleaseRequest(
                ProfileId: germanBest.Id,
                ReleaseTitle: "Severance.S02E01.GERMAN.DL.2160p.WEB-DL.DV.HDR10.DDP5.1.Atmos.H.265-GRP.mkv",
                OriginalLanguage: "en",
                ExplicitAudioLanguages: ["de", "en"]));

            Assert.True(tvDe1080p.TotalWeightedScore > tvEn4k.TotalWeightedScore);
            Assert.True(tvDe4k.TotalWeightedScore > tvDe1080p.TotalWeightedScore);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private sealed class ScriptedSpeechAdapter(Func<ProbedRawStream, IReadOnlyList<SpeechWindowSample>> handler)
        : IAudioSpeechLidAdapter
    {
        public Task<IReadOnlyList<SpeechWindowSample>> AnalyzeAudioStreamAsync(
            string filePath,
            ProbedRawStream audioStream,
            double durationSec,
            CancellationToken ct = default) =>
            Task.FromResult(handler(audioStream));
    }

    private static WebApplicationFactory<Program> CreateWebFactory(string tempDir) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = tempDir,
                    ["Managerr:MasterKey"] = "phase4-integration-master-key-32b!",
                });
            });
        });

    private static void CreateRealGermanEpubWithEnglishOpfLie(string epubPath)
    {
        using var fs = File.Create(epubPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        var opf = zip.CreateEntry("OEBPS/content.opf");
        using (var w = new StreamWriter(opf.Open(), Encoding.UTF8))
        {
            w.Write("""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:title>Der Schwarm</dc:title>
                    <dc:language>en</dc:language>
                  </metadata>
                </package>
                """);
        }

        for (var ch = 1; ch <= 3; ch++)
        {
            var entry = zip.CreateEntry($"OEBPS/chapter-{ch}.xhtml");
            using var cw = new StreamWriter(entry.Open(), Encoding.UTF8);
            cw.Write("""
                <html xmlns="http://www.w3.org/1999/xhtml">
                <body>
                  <p>Ich weiß nicht, was wir dort unten im tiefen Ozean gefunden haben, aber der Bericht für die Wissenschaftler ist eindeutig und nicht mehr aufzuhalten.</p>
                  <p>Wenn wir die Signale der Meere nicht verstehen, werden sie sich gegen uns wenden, und auch die Schiffe auf dem Wasser sind nicht sicher.</p>
                </body>
                </html>
                """);
        }
    }

    private static string BuildGermanSrt(int cueCount)
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= cueCount; i++)
        {
            var min = i / 60;
            var sec = i % 60;
            sb.AppendLine(i.ToString());
            sb.AppendLine($"00:{min:D2}:{sec:D2},000 --> 00:{min:D2}:{sec:D2},800");
            sb.AppendLine("Wir müssen sofort gehen, weil der Sturm über der Stadt immer stärker wird und wir keine Zeit mehr haben.");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"managerr-p4-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Ignore cleanup race
        }
    }
}
