using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;
using Managerr.Catalog;
using Managerr.Common.Net;
using Managerr.Datastore;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
using Managerr.Host.Services;
using Managerr.Indexers;
using Managerr.Metadata;
using Managerr.Migration;
using Managerr.Profiles;
using Managerr.Verification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Managerr.Phase6.Tests;

public sealed class LocalizedReleaseImportAndCategoryIsolationTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GrabbedLocalizedMovie_WithObfuscatedInnerFile_CorrelatedViaGrabHistory_ImportsAutomaticallyWithVerifiedPass()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"loc-grab-movie-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var moviesRoot = Path.Combine(tempDir, "storage", "movies");
            var stagingDir = Path.Combine(tempDir, "downloads", "movies-managerr", "Hafenlinie.German.2000.DVDRiP.XviD-GRP.1");
            Directory.CreateDirectory(moviesRoot);
            Directory.CreateDirectory(stagingDir);

            var innerFile = Path.Combine(stagingDir, "grp-hafen.avi");
            await File.WriteAllBytesAsync(innerFile, new byte[65_536]);

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
            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Movies", "movies", moviesRoot));

            var movieTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "The Harbor Line",
                WorkKind: "movies",
                AltTitles: [],
                OriginalLanguage: "en",
                Year: 2000,
                Monitored: true));

            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd",
                Implementation: "sabnzbd",
                Host: "sab",
                Port: 8080,
                ApiKey: "apikey"));

            await RecordGrabHistoryAsync(
                db,
                sabClient.Id,
                "nzo-harbor-de",
                null,
                "Hafenlinie.German.2000.DVDRiP.XviD-GRP.nzb",
                "movies",
                movieTree.Work.Id,
                [movieTree.Containers[0].Units[0].Id]);

            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "nzo-harbor-de",
                InfoHash: "nzo:nzo-harbor-de",
                Title: "Hafenlinie.German.2000.DVDRiP.XviD-GRP",
                Category: "movies-managerr",
                MediaVertical: "movies",
                Status: "Completed",
                TotalBytes: 65_536,
                RemainingBytes: 0,
                OutputPath: stagingDir,
                ContainedFiles: [innerFile],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            var report = await importSvc.ProcessCompletedDownloadsAsync();

            Assert.Equal(1, report.ImportedCount);
            Assert.Equal(0, report.RejectedCount);
            var outcome = Assert.Single(report.Items);
            Assert.Equal(VerificationVerdicts.VerifiedPass, outcome.Verdict);

            var updated = await catalog.GetWorkTreeAsync(movieTree.Work.Id);
            Assert.NotNull(updated);
            Assert.Single(updated.Containers[0].Units[0].Files);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task GenuineClientSlotIdCollision_UnrelatedReleaseAndYear_StillBlocksWithGrabMismatchReason()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"loc-collision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var moviesRoot = Path.Combine(tempDir, "storage", "movies");
            var stagingDir = Path.Combine(tempDir, "downloads", "movies-managerr", "Galactic.Outpost.2024.1080p.WEB-DL-OTHER");
            Directory.CreateDirectory(moviesRoot);
            Directory.CreateDirectory(stagingDir);

            var mkvFile = Path.Combine(stagingDir, "Galactic.Outpost.2024.1080p.WEB-DL-OTHER.mkv");
            await File.WriteAllBytesAsync(mkvFile, new byte[65_536]);

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
            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Movies", "movies", moviesRoot));

            var movieTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "The Harbor Line",
                WorkKind: "movies",
                AltTitles: [],
                OriginalLanguage: "en",
                Year: 2000,
                Monitored: true));

            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd",
                Implementation: "sabnzbd",
                Host: "sab",
                Port: 8080,
                ApiKey: "apikey"));

            await RecordGrabHistoryAsync(
                db,
                sabClient.Id,
                "1",
                null,
                "The.Harbor.Line.2000.1080p.BluRay-GRP",
                "movies",
                movieTree.Work.Id,
                [movieTree.Containers[0].Units[0].Id]);

            // Sequential client slot ID "1" now holds a completely unrelated movie
            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "1",
                InfoHash: "nzo:slot-1",
                Title: "Galactic.Outpost.2024.1080p.WEB-DL-OTHER",
                Category: "movies-managerr",
                MediaVertical: "movies",
                Status: "Completed",
                TotalBytes: 65_536,
                RemainingBytes: 0,
                OutputPath: stagingDir,
                ContainedFiles: [mkvFile],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            var report = await importSvc.ProcessCompletedDownloadsAsync();

            Assert.Equal(0, report.ImportedCount);
            Assert.Equal(1, report.RejectedCount);
            var outcome = Assert.Single(report.Items);
            Assert.Equal("ImportBlocked", outcome.Verdict);
            Assert.Contains("GrabHistory", outcome.Message, StringComparison.Ordinal);

            var updated = await catalog.GetWorkTreeAsync(movieTree.Work.Id);
            Assert.NotNull(updated);
            Assert.Empty(updated.Containers[0].Units[0].Files);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task UngrabbedLocalizedSeriesAndMovie_InOwnedCategories_MatchAltTitles_AndManualScanParsesFolder()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"loc-alttitle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var tvRoot = Path.Combine(tempDir, "storage", "tv");
            var moviesRoot = Path.Combine(tempDir, "storage", "movies");
            var tvStagingDir = Path.Combine(tempDir, "downloads", "tv-managerr", "Urzeit.Horizonte.S01E01.German.DL.1080p.WEB.H264-GRP.1");
            var movieStagingDir = Path.Combine(tempDir, "downloads", "movies-managerr", "Silbertal.2019.German.1080p.BluRay-GRP");
            Directory.CreateDirectory(tvRoot);
            Directory.CreateDirectory(moviesRoot);
            Directory.CreateDirectory(tvStagingDir);
            Directory.CreateDirectory(movieStagingDir);

            // Obfuscated inner filename in TV folder to verify ScanManualImportFolderAsync directory fallback against AltTitles
            var tvInnerMkv = Path.Combine(tvStagingDir, "grp-uh-file.mkv");
            var movieMkv = Path.Combine(movieStagingDir, "Silbertal.2019.German.1080p.BluRay-GRP.mkv");
            await File.WriteAllBytesAsync(tvInnerMkv, new byte[65_536]);
            await File.WriteAllBytesAsync(movieMkv, new byte[65_536]);

            var fakeGateway = new FakeGateway();
            var fakeVerifier = new FakeLanguageVerificationService();

            await using var factory = CreateFactory(tempDir, fakeGateway, fakeVerifier);
            using var scope = factory.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var catalog = sp.GetRequiredService<IPolymorphicCatalogService>();
            var profiles = sp.GetRequiredService<IComposableProfileService>();
            var orchestrator = sp.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var importSvc = sp.GetRequiredService<ICompletedDownloadImportService>();
            var wantedSvc = sp.GetRequiredService<IWantedAndActivityService>();

            await profiles.EnsureBuiltInProfilesSeededAsync();
            var tvLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("TV", "tv", tvRoot));
            var moviesLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Movies", "movies", moviesRoot));

            var seriesTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: "Ancient Horizons",
                WorkKind: "tv",
                AltTitles: ["Urzeit Horizonte"],
                OriginalLanguage: "en",
                Year: 2022,
                Monitored: true,
                Containers:
                [
                    new MetadataContainerSeed("season", 1, "Season 1", "2022-01-01",
                    [
                        new MetadataUnitSeed("episode", 1, 1, "Episode 01", "2022-01-01")
                    ])
                ]));

            var movieTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: moviesLib.Id,
                Title: "Silver Valley",
                WorkKind: "movies",
                AltTitles: ["Silbertal"],
                OriginalLanguage: "en",
                Year: 2019,
                Monitored: true));

            // Verify manual import folder scan resolves localized directory name (with trailing .1 collision suffix) via AltTitles
            var scanned = await wantedSvc.ScanManualImportFolderAsync(tvStagingDir, "tv");
            var candidate = Assert.Single(scanned);
            Assert.Equal(seriesTree.Work.Id, candidate.SuggestedWorkId);
            Assert.Equal(1, candidate.SuggestedSeasonNumber);
            Assert.Equal([1], candidate.SuggestedEpisodeNumbers);

            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd",
                Implementation: "sabnzbd",
                Host: "sab",
                Port: 8080,
                ApiKey: "apikey"));

            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "nzo-uh-s01e01",
                InfoHash: "nzo:nzo-uh-s01e01",
                Title: "Urzeit.Horizonte.S01E01.German.DL.1080p.WEB.H264-GRP",
                Category: "tv-managerr",
                MediaVertical: "tv",
                Status: "Completed",
                TotalBytes: 65_536,
                RemainingBytes: 0,
                OutputPath: tvStagingDir,
                ContainedFiles: [tvInnerMkv],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "nzo-silbertal",
                InfoHash: "nzo:nzo-silbertal",
                Title: "Silbertal.2019.German.1080p.BluRay-GRP",
                Category: "movies-managerr",
                MediaVertical: "movies",
                Status: "Completed",
                TotalBytes: 65_536,
                RemainingBytes: 0,
                OutputPath: movieStagingDir,
                ContainedFiles: [movieMkv],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            var report = await importSvc.ProcessCompletedDownloadsAsync();

            Assert.Equal(2, report.ImportedCount);
            Assert.Equal(0, report.RejectedCount);
            Assert.All(report.Items, i => Assert.Equal(VerificationVerdicts.VerifiedPass, i.Verdict));

            var updatedSeries = await catalog.GetWorkTreeAsync(seriesTree.Work.Id);
            Assert.NotNull(updatedSeries);
            Assert.Single(updatedSeries.Containers[0].Units[0].Files);

            var updatedMovie = await catalog.GetWorkTreeAsync(movieTree.Work.Id);
            Assert.NotNull(updatedMovie);
            Assert.Single(updatedMovie.Containers[0].Units[0].Files);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task UngrabbedForeignCategory_WithExistingNonEmptyDirAndMkv_IgnoredByQueueAndImport_AndSweepsStaleBlockedRow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"loc-foreign-cat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var tvRoot = Path.Combine(tempDir, "storage", "tv");
            var foreignDir = Path.Combine(tempDir, "downloads", "series", "Foreign.Show.S01E01.1080p.WEB-DL");
            Directory.CreateDirectory(tvRoot);
            Directory.CreateDirectory(foreignDir);

            var foreignMkv = Path.Combine(foreignDir, "Foreign.Show.S01E01.1080p.WEB-DL.mkv");
            await File.WriteAllBytesAsync(foreignMkv, new byte[65_536]);

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
            await catalog.CreateLibraryAsync(new CreateLibraryRequest("TV", "tv", tvRoot));

            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd",
                Implementation: "sabnzbd",
                Host: "sab",
                Port: 8080,
                ApiKey: "apikey",
                CategoryMappings: new Dictionary<string, string>
                {
                    ["tv"] = "tv-managerr",
                    ["movies"] = "movies-managerr",
                }));

            // Seed a pre-existing stale ungrabbed ImportBlocked tracked_downloads row from before strict category scoping
            var now = DateTimeOffset.UtcNow.ToString("O");
            await db.ExecuteWriteAsync(DatabaseKind.Core, (conn, tx) => conn.ExecuteAsync(
                """
                INSERT INTO tracked_downloads (
                    download_client_id, client_name, external_download_id, protocol, info_hash, title,
                    media_vertical, work_id, work_title, unit_ids_json, state, tracked_status,
                    status_messages_json, output_path, quarantine_id, can_move_files, first_seen_at, updated_at
                ) VALUES (
                    @ClientId, @ClientName, 'nzo-foreign-series-disk', 'usenet', 'nzo:foreign-disk',
                    'Foreign.Show.S01E01.1080p.WEB-DL', 'tv', NULL, NULL, '[]', 'ImportBlocked', 'warning',
                    '["Download completed, but work could not be matched."]', @OutPath, NULL, 1, @Now, @Now
                );
                """,
                new { ClientId = sabClient.Id, ClientName = sabClient.Name, OutPath = foreignDir, Now = now },
                transaction: tx));

            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "nzo-foreign-series-disk",
                InfoHash: "nzo:foreign-disk",
                Title: "Foreign.Show.S01E01.1080p.WEB-DL",
                Category: "series",
                MediaVertical: "tv",
                Status: "Completed",
                TotalBytes: 65_536,
                RemainingBytes: 0,
                OutputPath: foreignDir,
                ContainedFiles: [foreignMkv],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            var snapshot = await orchestrator.GetUnifiedQueueSnapshotAsync();
            Assert.DoesNotContain(snapshot.Items, i => i.ExternalId == "nzo-foreign-series-disk");

            var report = await importSvc.ProcessCompletedDownloadsAsync();
            Assert.Equal(0, report.EvaluatedCount);
            Assert.Equal(0, report.ImportedCount);
            Assert.True(File.Exists(foreignMkv), "Foreign category file on disk must never be moved or deleted.");

            var tracked = await importSvc.ListTrackedDownloadsAsync();
            Assert.DoesNotContain(tracked, t => t.ExternalDownloadId == "nzo-foreign-series-disk");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task RetryTrackedDownloadImportAsync_WithNullOverrideWorkId_PreservesGrabHistoryWorkId_AndImports()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"loc-retry-null-workid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var moviesRoot = Path.Combine(tempDir, "storage", "movies");
            var stagingDir = Path.Combine(tempDir, "downloads", "movies-managerr", "Hafenlinie.German.2000.1080p.WEB-DL");
            Directory.CreateDirectory(moviesRoot);
            Directory.CreateDirectory(stagingDir);

            var mkvFile = Path.Combine(stagingDir, "Hafenlinie.German.2000.1080p.WEB-DL.mkv");
            await File.WriteAllBytesAsync(mkvFile, new byte[65_536]);

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
            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Movies", "movies", moviesRoot));

            var movieTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "The Harbor Line",
                WorkKind: "movies",
                AltTitles: [],
                OriginalLanguage: "en",
                Year: 2000,
                Monitored: true));

            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd",
                Implementation: "sabnzbd",
                Host: "sab",
                Port: 8080,
                ApiKey: "apikey"));

            await RecordGrabHistoryAsync(
                db,
                sabClient.Id,
                "nzo-retry-preserve",
                null,
                "Hafenlinie.German.2000.1080p.WEB-DL",
                "movies",
                movieTree.Work.Id,
                [movieTree.Containers[0].Units[0].Id]);

            var now = DateTimeOffset.UtcNow.ToString("O");
            var trackedId = await db.ExecuteWriteAsync(DatabaseKind.Core, (conn, tx) => conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO tracked_downloads (
                    download_client_id, client_name, external_download_id, protocol, info_hash, title,
                    media_vertical, work_id, work_title, unit_ids_json, state, tracked_status,
                    status_messages_json, output_path, quarantine_id, can_move_files, first_seen_at, updated_at
                ) VALUES (
                    @ClientId, @ClientName, 'nzo-retry-preserve', 'usenet', 'nzo:retry-preserve',
                    'Hafenlinie.German.2000.1080p.WEB-DL', 'movies', NULL, NULL, '[]', 'ImportBlocked', 'warning',
                    '["Manual retry confirmation needed."]', @OutPath, NULL, 1, @Now, @Now
                );
                SELECT last_insert_rowid();
                """,
                new { ClientId = sabClient.Id, ClientName = sabClient.Name, OutPath = stagingDir, Now = now },
                transaction: tx));

            var outcome = await importSvc.RetryTrackedDownloadImportAsync(
                trackedId,
                new ManualOverrideImportRequest(WorkId: null, BypassUpgradeCheck: true));

            Assert.Equal(VerificationVerdicts.VerifiedPass, outcome.Verdict);

            var updated = await catalog.GetWorkTreeAsync(movieTree.Work.Id);
            Assert.NotNull(updated);
            Assert.Single(updated.Containers[0].Units[0].Files);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task MultiFileGermanSeasonPack_WithShortSceneInnerFilenames_PassesUpgradeSimulation_AndImports()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"loc-pack-sim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var tvRoot = Path.Combine(tempDir, "storage", "tv");
            var packDir = Path.Combine(tempDir, "downloads", "tv-managerr", "Urzeit.Horizonte.S01.German.DL.1080p.WEB.H264-GRP");
            Directory.CreateDirectory(tvRoot);
            Directory.CreateDirectory(packDir);

            var ep1File = Path.Combine(packDir, "grp-uh-s01e01.mkv");
            var ep2File = Path.Combine(packDir, "grp-uh-s01e02.mkv");
            await File.WriteAllBytesAsync(ep1File, new byte[65_536]);
            await File.WriteAllBytesAsync(ep2File, new byte[65_536]);

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

            var tvLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("TV", "tv", tvRoot, DefaultProfileId: deDualProfile.Id));
            var seriesTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: "Ancient Horizons",
                WorkKind: "tv",
                AltTitles: ["Urzeit Horizonte"],
                OriginalLanguage: "en",
                Year: 2022,
                Monitored: true,
                QualityProfileId: deDualProfile.Id,
                LanguagePolicyId: deDualProfile.LanguagePolicyId,
                Containers:
                [
                    new MetadataContainerSeed("season", 1, "Season 1", "2022-01-01",
                    [
                        new MetadataUnitSeed("episode", 1, 1, "Episode 01", "2022-01-01"),
                        new MetadataUnitSeed("episode", 2, 2, "Episode 02", "2022-01-08"),
                    ])
                ]));

            // Attach an existing lower-quality English-only file on S01E01 so SimulateAsync upgrade check executes
            var ep1Unit = seriesTree.Containers[0].Units.Single(u => u.UnitNumber == 1);
            var ep2Unit = seriesTree.Containers[0].Units.Single(u => u.UnitNumber == 2);
            await catalog.AttachFileAsync(new AttachMediaFileRequest(
                UnitId: ep1Unit.Id,
                RelativePath: "Ancient Horizons (2022)/Season 01/Ancient Horizons - S01E01.mkv",
                SizeBytes: 32_768,
                QualityName: "HDTV-720p",
                DetectedAudioLangs: ["en"]));

            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd",
                Implementation: "sabnzbd",
                Host: "sab",
                Port: 8080,
                ApiKey: "apikey"));

            await RecordGrabHistoryAsync(
                db,
                sabClient.Id,
                "nzo-uh-pack",
                null,
                "Urzeit.Horizonte.S01.German.DL.1080p.WEB.H264-GRP",
                "tv",
                seriesTree.Work.Id,
                [ep1Unit.Id, ep2Unit.Id]);

            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "nzo-uh-pack",
                InfoHash: "nzo:nzo-uh-pack",
                Title: "Urzeit.Horizonte.S01.German.DL.1080p.WEB.H264-GRP",
                Category: "tv-managerr",
                MediaVertical: "tv",
                Status: "Completed",
                TotalBytes: 131_072,
                RemainingBytes: 0,
                OutputPath: packDir,
                ContainedFiles: [ep1File, ep2File],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            var report = await importSvc.ProcessCompletedDownloadsAsync();

            Assert.Equal(1, report.ImportedCount);
            Assert.Equal(0, report.RejectedCount);
            var outcome = Assert.Single(report.Items);
            Assert.Equal(VerificationVerdicts.VerifiedPass, outcome.Verdict);

            var updated = await catalog.GetWorkTreeAsync(seriesTree.Work.Id);
            Assert.NotNull(updated);
            Assert.All(updated.Containers[0].Units, u => Assert.Single(u.Files));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task EndToEnd_ExternalIdSearch_AutoGrabsLocalizedRelease_SkipsSample_AndImportsObfuscatedInnerFileToCanonicalWork()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"loc-e2e-grab-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var moviesRoot = Path.Combine(tempDir, "storage", "movies");
            var stagingDir = Path.Combine(tempDir, "downloads", "movies-managerr", "Sonnenwanderer.German.2000.DVDRiP.XviD-GRP.1");
            var sampleDir = Path.Combine(stagingDir, "Sample");
            Directory.CreateDirectory(moviesRoot);
            Directory.CreateDirectory(stagingDir);
            Directory.CreateDirectory(sampleDir);

            var mainAvi = Path.Combine(stagingDir, "grp-sonnen.avi");
            var nfoFile = Path.Combine(stagingDir, "grp-sonnen.nfo");
            var sampleAvi = Path.Combine(sampleDir, "grp-sonnen-sample.avi");
            await File.WriteAllBytesAsync(mainAvi, new byte[65_536]);
            await File.WriteAllBytesAsync(nfoFile, new byte[512]);
            await File.WriteAllBytesAsync(sampleAvi, new byte[8_192]);

            var fakeGateway = new FakeGateway();
            var fakeVerifier = new FakeLanguageVerificationService();
            var fakeIndexerHttp = new FakeMovieImdbNewznabGateway();

            await using var factory = CreateFactory(
                tempDir,
                fakeGateway,
                fakeVerifier,
                indexerHttpGateway: fakeIndexerHttp);

            using var scope = factory.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var catalog = sp.GetRequiredService<IPolymorphicCatalogService>();
            var profiles = sp.GetRequiredService<IComposableProfileService>();
            var indexers = sp.GetRequiredService<IUnifiedIndexerService>();
            var searchOrchestrator = sp.GetRequiredService<IReleaseSearchOrchestrator>();
            var orchestrator = sp.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var importSvc = sp.GetRequiredService<ICompletedDownloadImportService>();

            await profiles.EnsureBuiltInProfilesSeededAsync();
            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Movies", "movies", moviesRoot));

            var movieTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "Solar Drifters",
                WorkKind: "movies",
                AltTitles: [],
                OriginalLanguage: "es",
                Year: 2000,
                Monitored: true,
                ExternalIds: new Dictionary<string, string>
                {
                    ["tmdb"] = "990010",
                    ["imdb"] = "tt9900010",
                },
                SkipUpstreamHydration: true));

            await indexers.CreateIndexerAsync(new UpsertIndexerRequest(
                Name: "MockNewznab",
                Implementation: "newznab",
                DefinitionId: null,
                Protocol: "usenet",
                BaseUrl: "https://mock-newznab.invalid",
                ApiKey: "newznab-key",
                Categories: [2000, 2030, 2040],
                Priority: 25,
                RequestDelayMs: 0));

            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd",
                Implementation: "sabnzbd",
                Host: "sab",
                Port: 8080,
                ApiKey: "apikey",
                CategoryMappings: new Dictionary<string, string>
                {
                    ["movies"] = "movies-managerr",
                }));

            // Run REAL search + grab flow so GrabReleaseAsync persists download_grab_history
            var scopedCandidates = await searchOrchestrator.SearchScopedAsync(new OrchestratedReleaseSearchRequest(
                Query: new IndexerSearchQuery(
                    Query: movieTree.Work.Title,
                    MediaVertical: "movies",
                    Interactive: false,
                    WorkId: movieTree.Work.Id),
                WorkTree: movieTree,
                IncludeSeasonPacks: false));

            var grabbedCandidate = Assert.Single(scopedCandidates);
            Assert.Equal("Sonnenwanderer.German.2000.DVDRiP.XviD-GRP", grabbedCandidate.Release.Title);
            Assert.True(grabbedCandidate.IdentityVerified);

            var grabOutcome = await orchestrator.GrabReleaseAsync(new GrabReleaseRequest(
                Title: grabbedCandidate.Release.Title,
                Protocol: grabbedCandidate.Release.Protocol,
                MediaVertical: "movies",
                DownloadUrlOrMagnet: grabbedCandidate.Release.DownloadUrl,
                InfoHash: grabbedCandidate.Release.InfoHash,
                WorkId: movieTree.Work.Id,
                ContainerId: movieTree.Containers[0].Id,
                UnitIds: [movieTree.Containers[0].Units[0].Id],
                IndexerId: grabbedCandidate.Release.IndexerId,
                ReleaseGuid: grabbedCandidate.Release.Guid,
                SourceType: "auto_search",
                IndexerName: grabbedCandidate.Release.IndexerName,
                SizeBytes: grabbedCandidate.Release.SizeBytes,
                PublishedAt: grabbedCandidate.Release.PublishedAt));

            Assert.Equal("movies-managerr", grabOutcome.ResolvedCategory);
            Assert.Equal(sabClient.Id, grabOutcome.ClientId);

            // Simulate SABnzbd completing into movies-managerr/Sonnenwanderer.German.2000.DVDRiP.XviD-GRP.1/
            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: grabOutcome.ExternalDownloadId,
                InfoHash: $"nzo:{grabOutcome.ExternalDownloadId}",
                Title: "Sonnenwanderer.German.2000.DVDRiP.XviD-GRP",
                Category: "movies-managerr",
                MediaVertical: "movies",
                Status: "Completed",
                TotalBytes: 74_240,
                RemainingBytes: 0,
                OutputPath: stagingDir,
                ContainedFiles: [mainAvi, nfoFile, sampleAvi],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            var report = await importSvc.ProcessCompletedDownloadsAsync();

            Assert.Equal(1, report.ImportedCount);
            Assert.Equal(0, report.RejectedCount);
            var outcome = Assert.Single(report.Items);
            Assert.Equal(VerificationVerdicts.VerifiedPass, outcome.Verdict);
            Assert.NotNull(outcome.DestinationFilePath);

            var destFileName = Path.GetFileName(outcome.DestinationFilePath!);
            var destDirName = Path.GetFileName(Path.GetDirectoryName(outcome.DestinationFilePath!)!);
            Assert.Equal("Solar Drifters (2000)", destDirName);
            Assert.StartsWith("Solar Drifters (2000)", destFileName, StringComparison.Ordinal);
            Assert.EndsWith(".avi", destFileName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sonnenwanderer", outcome.DestinationFilePath!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("grp-sonnen", outcome.DestinationFilePath!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sample", outcome.DestinationFilePath!, StringComparison.OrdinalIgnoreCase);

            var importedDiskFiles = Directory.GetFiles(moviesRoot, "*", SearchOption.AllDirectories);
            Assert.Single(importedDiskFiles);
            Assert.DoesNotContain(importedDiskFiles, f => f.Contains("sample", StringComparison.OrdinalIgnoreCase));

            var tracked = await importSvc.ListTrackedDownloadsAsync();
            var trackedRow = Assert.Single(tracked, t => t.ExternalDownloadId == grabOutcome.ExternalDownloadId);
            Assert.Equal("Imported", trackedRow.State);
            Assert.DoesNotContain(trackedRow.StatusMessages, m => m.Contains("GrabHistory", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task GrabbedLocalizedEpisode_WithSpuriousNumbersInObfuscatedInnerStem_ImportsExactJobSeasonAndEpisode()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"loc-spurious-ep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var tvRoot = Path.Combine(tempDir, "storage", "tv");
            var stagingDir = Path.Combine(tempDir, "downloads", "tv-managerr", "Orbitaler.Horizont.2024.S03E08.GERMAN.DL.1080p.WEB.H264-GRP");
            Directory.CreateDirectory(tvRoot);
            Directory.CreateDirectory(stagingDir);

            // Obfuscated stem ending with "-01" that naive parsing could misread as episode 1
            var innerMkv = Path.Combine(stagingDir, "8f3a12b9-01.mkv");
            await File.WriteAllBytesAsync(innerMkv, new byte[65_536]);

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
            var tvLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("TV", "tv", tvRoot));

            var seriesTree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: "Orbital Horizon",
                WorkKind: "tv",
                AltTitles: [],
                OriginalLanguage: "en",
                Year: 2024,
                Monitored: true,
                Containers:
                [
                    new MetadataContainerSeed("season", 3, "Season 3", "2024-01-01",
                    [
                        new MetadataUnitSeed("episode", 1, 1, "Episode 01", "2024-01-01"),
                        new MetadataUnitSeed("episode", 8, 8, "Episode 08", "2024-02-19"),
                    ])
                ],
                SkipUpstreamHydration: true));

            var ep1Unit = seriesTree.Containers[0].Units.Single(u => u.UnitNumber == 1);
            var ep8Unit = seriesTree.Containers[0].Units.Single(u => u.UnitNumber == 8);

            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd",
                Implementation: "sabnzbd",
                Host: "sab",
                Port: 8080,
                ApiKey: "apikey",
                CategoryMappings: new Dictionary<string, string>
                {
                    ["tv"] = "tv-managerr",
                }));

            await RecordGrabHistoryAsync(
                db,
                sabClient.Id,
                "nzo-orbital-s03e08",
                null,
                "Orbitaler.Horizont.2024.S03E08.GERMAN.DL.1080p.WEB.H264-GRP",
                "tv",
                seriesTree.Work.Id,
                [ep8Unit.Id]);

            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "nzo-orbital-s03e08",
                InfoHash: "nzo:nzo-orbital-s03e08",
                Title: "Orbitaler.Horizont.2024.S03E08.GERMAN.DL.1080p.WEB.H264-GRP",
                Category: "tv-managerr",
                MediaVertical: "tv",
                Status: "Completed",
                TotalBytes: 65_536,
                RemainingBytes: 0,
                OutputPath: stagingDir,
                ContainedFiles: [innerMkv],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            var report = await importSvc.ProcessCompletedDownloadsAsync();

            Assert.Equal(1, report.ImportedCount);
            Assert.Equal(0, report.RejectedCount);
            var outcome = Assert.Single(report.Items);
            Assert.Equal(VerificationVerdicts.VerifiedPass, outcome.Verdict);

            var updated = await catalog.GetWorkTreeAsync(seriesTree.Work.Id);
            Assert.NotNull(updated);
            var updatedEp1 = updated!.Containers[0].Units.Single(u => u.Id == ep1Unit.Id);
            var updatedEp8 = updated.Containers[0].Units.Single(u => u.Id == ep8Unit.Id);
            Assert.Empty(updatedEp1.Files);
            Assert.Single(updatedEp8.Files);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task EndedSeriesWithEmptyAltTitles_SelfHealsViaMetadataRefresh_AndMigratorPreservesLocalizedAliases()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"loc-heal-migrate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Part A: Ended series with empty AltTitles self-heals via RefreshCatalogMetadata and imports ungrabbed release
            var tvRoot = Path.Combine(tempDir, "storage", "tv");
            var stagingDir = Path.Combine(tempDir, "downloads", "tv-managerr", "Urzeit.Horizonte.2022.S01E01.German.DL.2160p.WEB.H265-GRP");
            Directory.CreateDirectory(tvRoot);
            Directory.CreateDirectory(stagingDir);

            var innerMkv = Path.Combine(stagingDir, "Urzeit.Horizonte.2022.S01E01.German.DL.2160p.WEB.H265-GRP.mkv");
            await File.WriteAllBytesAsync(innerMkv, new byte[65_536]);

            var fakeGateway = new FakeGateway();
            var fakeVerifier = new FakeLanguageVerificationService();
            var fakeMetadataHttp = new FakeSkyHookMetadataGateway();
            var fakeSafeHttp = new FakeArrAliasMigrationSafeHttpClientFactory();

            await using var factory = CreateFactory(
                tempDir,
                fakeGateway,
                fakeVerifier,
                metadataHttpGateway: fakeMetadataHttp,
                safeHttpClientFactory: fakeSafeHttp);

            using var scope = factory.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var catalog = sp.GetRequiredService<IPolymorphicCatalogService>();
            var profiles = sp.GetRequiredService<IComposableProfileService>();
            var orchestrator = sp.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var importSvc = sp.GetRequiredService<ICompletedDownloadImportService>();
            var taskManager = sp.GetRequiredService<ITaskManagerService>();
            var migrator = sp.GetRequiredService<ILegacyMigrationService>();
            var db = sp.GetRequiredService<ISqliteDbFactory>();

            await profiles.EnsureBuiltInProfilesSeededAsync();
            await taskManager.EnsureTasksSeededAsync();

            var tvLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest("TV", "tv", tvRoot));
            var endedSeries = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: "Ancient Horizons",
                WorkKind: "tv",
                AltTitles: [],
                OriginalLanguage: "en",
                Year: 2022,
                Status: "ended",
                Monitored: true,
                ExternalIds: new Dictionary<string, string>
                {
                    ["tvdb"] = "880077",
                },
                Containers:
                [
                    new MetadataContainerSeed("season", 1, "Season 1", "2022-01-01",
                    [
                        new MetadataUnitSeed("episode", 1, 1, "Episode 01", "2022-01-01"),
                    ])
                ],
                SkipUpstreamHydration: true));

            Assert.Empty(endedSeries.Work.AltTitles);

            var taskSummary = await taskManager.RunTaskNowAsync(ScheduledTaskKeys.RefreshCatalogMetadata);
            Assert.Equal("completed", taskSummary.LastStatus);

            var healedSeries = await catalog.GetWorkTreeAsync(endedSeries.Work.Id);
            Assert.NotNull(healedSeries);
            Assert.Contains("Urzeit Horizonte", healedSeries!.Work.AltTitles, StringComparer.OrdinalIgnoreCase);

            var sabClient = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SABnzbd",
                Implementation: "sabnzbd",
                Host: "sab",
                Port: 8080,
                ApiKey: "apikey",
                CategoryMappings: new Dictionary<string, string>
                {
                    ["tv"] = "tv-managerr",
                }));

            fakeGateway.QueueItems.Add(new ActiveDownloadQueueItem(
                ClientId: sabClient.Id,
                ClientName: sabClient.Name,
                Protocol: "usenet",
                ExternalId: "nzo-ungrabbed-healed",
                InfoHash: "nzo:nzo-ungrabbed-healed",
                Title: "Urzeit.Horizonte.2022.S01E01.German.DL.2160p.WEB.H265-GRP",
                Category: "tv-managerr",
                MediaVertical: "tv",
                Status: "Completed",
                TotalBytes: 65_536,
                RemainingBytes: 0,
                OutputPath: stagingDir,
                ContainedFiles: [innerMkv],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null,
                CanMoveFiles: true));

            var importReport = await importSvc.ProcessCompletedDownloadsAsync();
            Assert.Equal(1, importReport.ImportedCount);
            Assert.Equal(0, importReport.RejectedCount);
            var importOutcome = Assert.Single(importReport.Items);
            Assert.Equal(VerificationVerdicts.VerifiedPass, importOutcome.Verdict);

            var afterImport = await catalog.GetWorkTreeAsync(endedSeries.Work.Id);
            Assert.Single(afterImport!.Containers[0].Units[0].Files);

            // Part B: LegacyArrAndCleanerrMigrator preserves Sonarr alternateTitles and Radarr originalTitle + alternateTitles into media_works.alt_titles_json
            var migratedTvRoot = Path.Combine(tempDir, "migrated", "tv");
            var migratedMoviesRoot = Path.Combine(tempDir, "migrated", "movies");
            Directory.CreateDirectory(migratedTvRoot);
            Directory.CreateDirectory(migratedMoviesRoot);

            var pathMappings = new Dictionary<string, string>
            {
                ["/arr-tv"] = migratedTvRoot,
                ["/arr-movies"] = migratedMoviesRoot,
            };

            var sonarrReport = await migrator.ImportFromArrApiTokenAsync(new ArrApiTokenImportRequest(
                BaseUrl: "http://sonarr.mock.lan:8989",
                ApiKey: "sonarr-alias-key",
                SourceKind: "sonarr",
                PathMappings: pathMappings,
                DryRun: false,
                SaveAsSource: false));
            Assert.Equal(1, sonarrReport.TvSeriesCount);

            var radarrReport = await migrator.ImportFromArrApiTokenAsync(new ArrApiTokenImportRequest(
                BaseUrl: "http://radarr.mock.lan:7878",
                ApiKey: "radarr-alias-key",
                SourceKind: "radarr",
                PathMappings: pathMappings,
                DryRun: false,
                SaveAsSource: false));
            Assert.Equal(1, radarrReport.MoviesCount);

            await using var readConn = await db.OpenReadConnectionAsync(DatabaseKind.Core);
            var migratedRows = (await readConn.QueryAsync<(string Title, string AltTitlesJson)>(
                "SELECT title AS Title, alt_titles_json AS AltTitlesJson FROM media_works WHERE title IN ('Orbital Horizon', 'Solar Drifters');"))
                .ToDictionary(r => r.Title, r => JsonSerializer.Deserialize<string[]>(r.AltTitlesJson, JsonOpts) ?? []);

            Assert.True(migratedRows.TryGetValue("Orbital Horizon", out var sonarrAlts));
            Assert.Contains("Orbitaler Horizont", sonarrAlts, StringComparer.OrdinalIgnoreCase);

            Assert.True(migratedRows.TryGetValue("Solar Drifters", out var radarrAlts));
            Assert.Contains("Viajeros del Sol", radarrAlts, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("Sonnenwanderer", radarrAlts, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string tempDir,
        IDownloadClientDriverGateway gateway,
        ILanguageVerificationService verifier,
        IIndexerHttpGateway? indexerHttpGateway = null,
        IMetadataHttpGateway? metadataHttpGateway = null,
        ISafeHttpClientFactory? safeHttpClientFactory = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                ["Managerr:MasterKey"] = "localized-import-isolation-key-32b",
                ["Managerr:SchedulerEnabled"] = "false",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IDownloadClientDriverGateway>();
                s.AddSingleton(gateway);

                s.RemoveAll<ILanguageVerificationService>();
                s.AddSingleton(verifier);

                if (indexerHttpGateway is not null)
                {
                    s.RemoveAll<IIndexerHttpGateway>();
                    s.AddSingleton(indexerHttpGateway);
                }

                if (metadataHttpGateway is not null)
                {
                    s.RemoveAll<IMetadataHttpGateway>();
                    s.AddSingleton(metadataHttpGateway);
                }

                if (safeHttpClientFactory is not null)
                {
                    s.RemoveAll<ISafeHttpClientFactory>();
                    s.AddSingleton(safeHttpClientFactory);
                }
            });
        });

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

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private sealed class FakeGateway : IDownloadClientDriverGateway
    {
        public List<ActiveDownloadQueueItem> QueueItems { get; } = [];

        public IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers() => DownloadClientCatalog.All;

        public Task<string> EnqueueReleaseAsync(DownloadClientConnectionSpec client, string releaseTitle, string downloadUrlOrMagnet, string resolvedCategory, CancellationToken ct = default)
            => Task.FromResult($"q-{Guid.NewGuid():N}");

        public Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(DownloadClientConnectionSpec client, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ActiveDownloadQueueItem>>(QueueItems.Where(q => q.ClientId == client.Id).ToList());

        public Task RemoveDownloadAsync(DownloadClientConnectionSpec client, string externalId, bool deleteData, CancellationToken ct = default)
        {
            QueueItems.RemoveAll(q => q.ExternalId == externalId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLanguageVerificationService : ILanguageVerificationService
    {
        public Task<MediaFileVerificationReport> VerifyStagedFileAsync(RunVerificationRequest request, CancellationToken ct = default)
        {
            var langs = (request.ExpectedAudioLanguages is { Count: > 0 } ? request.ExpectedAudioLanguages : ["de", "en"]).ToArray();
            return Task.FromResult(new MediaFileVerificationReport(
                JobId: 1,
                UnitId: request.UnitId,
                StagedFilePath: request.StagedFilePath,
                MediaVertical: request.MediaVertical,
                Verdict: VerificationVerdicts.VerifiedPass,
                OverallConfidence: 0.98,
                HeaderAutoPatched: false,
                ExistingFilePreservedOnFailure: false,
                HashBlocklisted: false,
                QuarantineId: null,
                DurationMs: 1,
                VerifiedAudioLanguages: langs,
                VerifiedSubtitleLanguages: ["de"],
                Streams: [],
                VerifiedAt: DateTimeOffset.UtcNow.ToString("O")));
        }

        public Task<IReadOnlyList<MediaFileVerificationReport>> ListRecentReportsAsync(int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MediaFileVerificationReport>>([]);
        public Task<IReadOnlyList<QuarantinedImportSummary>> ListQuarantineAsync(string? statusFilter = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<QuarantinedImportSummary>>([]);
        public Task<QuarantinedImportSummary> ApproveQuarantineAsync(long quarantineId, long reviewerUserId, string confirmedLanguage, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<QuarantinedImportSummary> RejectAndBlocklistQuarantineAsync(long quarantineId, long reviewerUserId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeMovieImdbNewznabGateway : IIndexerHttpGateway
    {
        public Task<string> FetchStringAsync(Uri targetUri, IndexerProxyInternal? matchedProxy, CancellationToken ct = default)
        {
            var q = targetUri.Query;
            if (q.Contains("t=caps", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult("""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <caps>
                      <searching>
                        <search available="yes" supportedParams="q"/>
                        <movie-search available="yes" supportedParams="q,imdbid,tmdbid"/>
                      </searching>
                      <categories>
                        <category id="2000" name="Movies"><subcat id="2030" name="SD"/><subcat id="2040" name="HD"/></category>
                      </categories>
                    </caps>
                    """);
            }

            if (q.Contains("imdbid=9900010", StringComparison.OrdinalIgnoreCase) ||
                q.Contains("imdbid=09900010", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult("""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
                      <channel>
                        <item>
                          <title>Sonnenwanderer.German.2000.DVDRiP.XviD-GRP</title>
                          <guid>sonnenwanderer-2000-dvdrip-grp</guid>
                          <link>https://mock-newznab.invalid/get/sonnenwanderer-2000.nzb</link>
                          <enclosure url="https://mock-newznab.invalid/get/sonnenwanderer-2000.nzb" length="734003200" type="application/x-nzb"/>
                          <newznab:attr name="category" value="2030"/>
                          <newznab:attr name="imdb" value="9900010"/>
                        </item>
                      </channel>
                    </rss>
                    """);
            }

            return Task.FromResult("""
                <?xml version="1.0" encoding="UTF-8"?>
                <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
                  <channel></channel>
                </rss>
                """);
        }
    }

    private sealed class FakeSkyHookMetadataGateway : IMetadataHttpGateway
    {
        public Task<string?> SendAsync(MetadataHttpRequest request, CancellationToken ct = default)
        {
            if (request.Url.Contains("skyhook.sonarr.tv/v2/tvdb/shows/eng/880077", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<string?>("""
                    {
                      "tvdbId": 880077,
                      "title": "Ancient Horizons",
                      "originalLanguage": "en",
                      "year": 2022,
                      "status": "ended",
                      "translations": [
                        { "title": "Urzeit Horizonte", "language": "deu" }
                      ],
                      "seasons": [
                        { "seasonNumber": 1, "monitored": true }
                      ],
                      "episodes": [
                        { "seasonNumber": 1, "episodeNumber": 1, "title": "Episode 01", "airDate": "2022-01-01" }
                      ]
                    }
                    """);
            }

            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FakeArrAliasMigrationSafeHttpClientFactory : ISafeHttpClientFactory
    {
        public HttpClient CreatePublicClient(bool ignoreSslErrors = false) =>
            new(new FakeArrAliasMigrationHandler(), disposeHandler: true);

        public HttpClient CreateHomelabClient(bool allowPrivateLan = true, bool ignoreSslErrors = false) =>
            new(new FakeArrAliasMigrationHandler(), disposeHandler: true);

        public void EnsureAddressPermitted(IPAddress address, string? targetHost = null, bool allowPrivateLan = false) { }
    }

    private sealed class FakeArrAliasMigrationHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var host = uri.Host.ToLowerInvariant();
            var path = uri.AbsolutePath.ToLowerInvariant();

            if (host == "sonarr.mock.lan")
            {
                string? json = path switch
                {
                    "/api/v3/system/status" => """{"appName":"Sonarr","instanceName":"Mock Sonarr","version":"4.0.10.2544"}""",
                    "/api/v3/rootfolder" => """[{"id":1,"path":"/arr-tv"}]""",
                    "/api/v3/system/backup" => "[]",
                    "/api/v3/downloadclient" => "[]",
                    "/api/v3/indexer" => "[]",
                    "/api/v3/customformat" => "[]",
                    "/api/v3/blocklist" => """{"page":1,"pageSize":500,"records":[]}""",
                    "/api/v3/series" => """
                        [
                          {
                            "id": 10,
                            "title": "Orbital Horizon",
                            "year": 2024,
                            "tvdbId": 880099,
                            "seriesType": "standard",
                            "monitored": true,
                            "path": "/arr-tv/Orbital Horizon",
                            "rootFolderPath": "/arr-tv",
                            "originalLanguage": { "id": 1, "name": "English" },
                            "alternateTitles": [
                              { "title": "Orbitaler Horizont" }
                            ]
                          }
                        ]
                        """,
                    "/api/v3/episodefile" => "[]",
                    "/api/v3/episode" => "[]",
                    _ => null,
                };

                return Task.FromResult(json is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    });
            }

            if (host == "radarr.mock.lan")
            {
                string? json = path switch
                {
                    "/api/v3/system/status" => """{"appName":"Radarr","instanceName":"Mock Radarr","version":"5.14.0.9383"}""",
                    "/api/v3/rootfolder" => """[{"id":1,"path":"/arr-movies"}]""",
                    "/api/v3/system/backup" => "[]",
                    "/api/v3/downloadclient" => "[]",
                    "/api/v3/indexer" => "[]",
                    "/api/v3/customformat" => "[]",
                    "/api/v3/blocklist" => """{"page":1,"pageSize":500,"records":[]}""",
                    "/api/v3/movie" => """
                        [
                          {
                            "id": 20,
                            "title": "Solar Drifters",
                            "originalTitle": "Viajeros del Sol",
                            "year": 2000,
                            "tmdbId": 990010,
                            "imdbId": "tt9900010",
                            "monitored": true,
                            "minimumAvailability": "released",
                            "originalLanguage": { "id": 3, "name": "Spanish" },
                            "path": "/arr-movies/Solar Drifters (2000)",
                            "rootFolderPath": "/arr-movies",
                            "alternateTitles": [
                              { "title": "Sonnenwanderer" }
                            ]
                          }
                        ]
                        """,
                    _ => null,
                };

                return Task.FromResult(json is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
