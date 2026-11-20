using Dapper;
using Managerr.Catalog;
using Managerr.Datastore;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
using Managerr.Downloaders.Parser;
using Managerr.Host.Services;
using Managerr.Indexers.Torznab;
using Managerr.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Managerr.Phase5.Tests;

public sealed class AlreadyImportedReleaseGuardTests
{
    private const string ImportedE1 = "Pioneer.One.2010.S01E01.Earthbound.German.DL.1080p.WEB-DL.H264-TESTLAB";
    private const string ImportedE2 = "Pioneer.One.2010.S01E02.The.Man.From.Mars.German.DL.1080p.WEB-DL.H264-TESTLAB";
    private const string FailedE1 = "Pioneer.One.2010.S01E01.Earthbound.German.DL.1080p.WEB-DL.H264-BROKEN";
    private const string FallbackE1 = "Pioneer.One.2010.S01E01.Earthbound.1080p.WEB-DL.H264-NOUNITS";
    private const string FreshE1 = "Pioneer.One.2010.S01E01.Earthbound.German.DL.2160p.WEB-DL.H265-NEWGRP";

    [Fact]
    public async Task ImportedRelease_IsRejectedOnlyForUnitsThatStillHaveItsFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-imported-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "storage", "media", "tv"));

        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                        ["Managerr:MasterKey"] = "imported-guard-test-master-key-32byt",
                        ["Managerr:SchedulerEnabled"] = "false",
                    });
                });
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<IDownloadClientDriverGateway>();
                    services.AddSingleton<IDownloadClientDriverGateway>(new EnqueueOnlyDriverGateway());
                });
            });

            using var scope = factory.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var wanted = scope.ServiceProvider.GetRequiredService<IWantedAndActivityService>();
            var dbFactory = scope.ServiceProvider.GetRequiredService<ISqliteDbFactory>();

            var lib = await catalog.CreateLibraryAsync(new CreateLibraryRequest(
                Name: "TV", MediaType: "tv", RootFolderPath: Path.Combine(tempDir, "storage", "media", "tv")));
            var tree = await catalog.AddWorkAsync(new CreateMediaWorkRequest(
                LibraryId: lib.Id,
                Title: "Pioneer One",
                WorkKind: "series",
                AltTitles: [],
                OriginalLanguage: "en",
                Year: 2010,
                Status: "ended",
                Monitored: true,
                ExternalIds: new Dictionary<string, string> { ["tvdb"] = "170551" },
                Containers:
                [
                    new MetadataContainerSeed("season", 1, "Season 1", "2010-06-16",
                    [
                        new MetadataUnitSeed("episode", 1, 1, "Earthbound", "2010-06-16"),
                        new MetadataUnitSeed("episode", 2, 2, "The Man From Mars", "2010-07-01"),
                    ]),
                ]));
            var season = tree.Containers.Single();
            var e1 = season.Units.Single(u => u.UnitNumber == 1);
            var e2 = season.Units.Single(u => u.UnitNumber == 2);

            await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SAB", Implementation: "sabnzbd", Host: "sab-test", Port: 8080, ApiKey: "k"));

            // E1 has the file its release produced; E2's imported file has since been deleted
            await catalog.AttachFileAsync(new AttachMediaFileRequest(
                e1.Id, "Pioneer One (2010)/Season 01/Pioneer One (2010) - S01E01.mkv", 88_000, "WEBDL-1080p"));

            async Task<GrabReleaseOutcome> GrabAsync(string title, string guid, IReadOnlyList<long> units) =>
                await orchestrator.GrabReleaseAsync(new GrabReleaseRequest(
                    Title: title, Protocol: "usenet", MediaVertical: "tv",
                    DownloadUrlOrMagnet: $"https://indexer.invalid/{guid}", WorkId: tree.Work.Id, ContainerId: season.Id,
                    UnitIds: units, IndexerId: 1, ReleaseGuid: guid, IndexerName: "Stub"));

            var grabE1 = await GrabAsync(ImportedE1, "guid-e1", [e1.Id]);
            var grabE2 = await GrabAsync(ImportedE2, "guid-e2", [e2.Id]);
            var grabFailed = await GrabAsync(FailedE1, "guid-broken", [e1.Id]);
            var grabFallback = await GrabAsync(FallbackE1, "guid-nounits", []);

            await dbFactory.ExecuteWriteAsync(DatabaseKind.Core, async (conn, tx) =>
            {
                foreach (var (grab, title, verdict) in new[]
                         {
                             (grabE1, ImportedE1, "VerifiedPass"),
                             (grabE2, ImportedE2, "VerifiedPassAutoTagged"),
                             (grabFailed, FailedE1, "VerifiedFail"),
                             (grabFallback, FallbackE1, "VerifiedPass"),
                         })
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO processed_download_imports (download_client_id, external_download_id, title, verdict, processed_at)
                        VALUES (@ClientId, @ExtId, @Title, @Verdict, '2026-11-04T18:00:00+00:00');
                        """,
                        new { grab.ClientId, ExtId = grab.ExternalDownloadId, Title = title, Verdict = verdict },
                        tx);
                }

                // The grab had no target units; CDH resolved the import to E1 on its tracked download
                await conn.ExecuteAsync(
                    """
                    INSERT INTO tracked_downloads (download_client_id, external_download_id, title, unit_ids_json, state, first_seen_at, updated_at)
                    VALUES (@ClientId, @ExtId, @Title, @Units, 'Imported', '2026-11-04T18:00:00+00:00', '2026-11-04T18:00:00+00:00');
                    """,
                    new { grabFallback.ClientId, ExtId = grabFallback.ExternalDownloadId, Title = FallbackE1, Units = $"[{e1.Id}]" },
                    tx);
            });

            var snapshot = await orchestrator.GetImportedReleaseSnapshotAsync();
            var parser = new ReleaseParserRouter();

            // Same release under a different GUID (re-upload) is still recognised by title
            Assert.NotNull(snapshot.FindImportedFor(Candidate(ImportedE1, "reupload-guid"), [e1.Id]));
            Assert.NotNull(snapshot.FindImportedFor(Candidate("Totally.Different.Title", "guid-e1"), [e1.Id]));
            Assert.NotNull(snapshot.FindImportedFor(Candidate(FallbackE1, "x"), [e1.Id]));
            Assert.Null(snapshot.FindImportedFor(Candidate(FailedE1, "guid-broken"), [e1.Id]));
            Assert.Null(snapshot.FindImportedFor(Candidate(FreshE1, "guid-new"), [e1.Id]));
            // Release imported for E1 says nothing about E2
            Assert.Null(snapshot.FindImportedFor(Candidate(ImportedE1, "guid-e1"), [e2.Id]));

            // Interactive search: E1 still has the file -> rejected; E2's file is gone -> allowed again
            var e1Rows = await wanted.EnrichInteractiveReleasesAsync(
                [(Candidate(ImportedE1, "reupload-guid"), parser.Parse(ImportedE1, "tv")),
                 (Candidate(FreshE1, "guid-new"), parser.Parse(FreshE1, "tv"))],
                workId: tree.Work.Id, containerId: season.Id, unitId: e1.Id);
            var e1Imported = e1Rows.Single(r => r.Release.Title == ImportedE1);
            Assert.False(e1Imported.Approved);
            Assert.Equal("AlreadyImported", e1Imported.DecisionKind);
            Assert.StartsWith("AlreadyImported:", e1Imported.Rejections[0]);
            Assert.DoesNotContain(e1Rows.Single(r => r.Release.Title == FreshE1).Rejections, r => r.StartsWith("AlreadyImported"));

            var e2Rows = await wanted.EnrichInteractiveReleasesAsync(
                [(Candidate(ImportedE2, "guid-e2"), parser.Parse(ImportedE2, "tv"))],
                workId: tree.Work.Id, containerId: season.Id, unitId: e2.Id);
            Assert.NotEqual("AlreadyImported", e2Rows.Single().DecisionKind);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private static IndexedReleaseCandidate Candidate(string title, string guid) =>
        new(
            Guid: guid,
            IndexerId: 1,
            IndexerName: "Stub",
            Protocol: "usenet",
            MediaVertical: "tv",
            Title: title,
            DownloadUrl: $"https://indexer.invalid/{guid}",
            MagnetUrl: null,
            InfoUrl: null,
            InfoHash: null,
            SizeBytes: 1_000_000_000,
            Seeders: null,
            Peers: null,
            DownloadVolumeFactor: 1,
            UploadVolumeFactor: 1,
            PublishedAt: DateTimeOffset.UtcNow.AddDays(-1),
            Categories: [5040],
            ImdbId: null,
            TmdbId: null,
            TvdbId: null);

    private sealed class EnqueueOnlyDriverGateway : IDownloadClientDriverGateway
    {
        private int _count;

        public IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers() => DownloadClientCatalog.All;

        public Task<string> EnqueueReleaseAsync(
            DownloadClientConnectionSpec client,
            string releaseTitle,
            string downloadUrlOrMagnet,
            string resolvedCategory,
            CancellationToken ct = default) => Task.FromResult($"nzo-{Interlocked.Increment(ref _count)}");

        public Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(
            DownloadClientConnectionSpec client,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ActiveDownloadQueueItem>>([]);

        public Task RemoveDownloadAsync(
            DownloadClientConnectionSpec client,
            string externalId,
            bool deleteData,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
