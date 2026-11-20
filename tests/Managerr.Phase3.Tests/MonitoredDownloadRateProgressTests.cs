using Dapper;
using Managerr.Catalog;
using Managerr.Common.Crypto;
using Managerr.Common.Paths;
using Managerr.Datastore;
using Managerr.Datastore.Migrations;
using Managerr.Datastore.Services;
using Managerr.Metadata;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Managerr.Phase3.Tests;

public sealed class MonitoredDownloadRateProgressTests
{
    [Fact]
    public async Task ListWorks_And_GetWorkTree_Compute_Monitored_And_Downloaded_Unit_Counts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-mon-rate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = new AppPaths(tempDir);
            var protector = new EnvelopeSecretProtector(paths, "unit-test-master-key-32bytes-ok!");
            var dbFactory = new SqliteDbFactory(paths);
            await new SchemaMigrator(dbFactory).MigrateAllAsync();

            var cache = new HybridSqliteCache(dbFactory, new MemoryCache(new MemoryCacheOptions()));
            var settings = new AppSettingsService(dbFactory, protector);
            var fts = new FtsSearchService(dbFactory);
            var crosswalk = new ExternalIdCrosswalkService(dbFactory);
            await crosswalk.EnsureSeededAsync();

            var sidecars = new SidecarMetadataService();
            var gateway = new NullMetadataGateway();
            var webhooks = new CustomHttpMetadataProviderService(dbFactory, protector, gateway);
            var federation = new MetadataFederationService(
                BuiltInMetadataProviderCatalog.CreateAll(gateway, settings, sidecars),
                webhooks,
                crosswalk,
                cache);
            var catalog = new PolymorphicCatalogService(dbFactory, fts, federation, crosswalk);

            var tvLib = await catalog.CreateLibraryAsync(new CreateLibraryRequest(
                Name: "Series",
                MediaType: "tv",
                RootFolderPath: Path.Combine(tempDir, "tv"),
                PlexSectionId: "2"));

            var pastDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)).ToString("yyyy-MM-dd");
            var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)).ToString("yyyy-MM-dd");
            var now = DateTimeOffset.UtcNow.ToString("O");

            // Case 1: Fallout-like series (19 total units: 3 unmonitored Specials w/ 0 files, 8 S01 unmonitored w/ 4 files, 8 S02 unmonitored w/ 8 files) -> 12 / 12
            // Case 2: Ongoing monitored series (3 unmonitored Specials w/ 0 files, 10 monitored aired S01 w/ 7 files + 1 duplicate file, 2 monitored future unaired S02) -> 7 / 10
            long falloutWorkId = 0;
            long ongoingWorkId = 0;

            await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                async (conn, tx) =>
                {
                    falloutWorkId = await conn.ExecuteScalarAsync<long>(
                        """
                        INSERT INTO media_works (
                            library_id, work_kind, primary_provider, episode_ordering_scheme,
                            title, sort_title, alt_titles_json, original_language, year, status,
                            overview, monitored, locked_fields_json, external_ids_json, tags_json,
                            added_at, updated_at
                        ) VALUES (
                            @LibId, 'tv', 'tvdb', 'aired',
                            'Fallout', 'fallout', '[]', 'en', 2024, 'continuing',
                            '', 1, '[]', '{}', '[]',
                            @Now, @Now
                        );
                        SELECT last_insert_rowid();
                        """,
                        new { LibId = tvLib.Id, Now = now },
                        tx);

                    for (var s = 0; s <= 2; s++)
                    {
                        var cId = await conn.ExecuteScalarAsync<long>(
                            """
                            INSERT INTO media_containers (work_id, container_kind, container_number, title, monitored, external_ids_json, created_at)
                            VALUES (@WorkId, 'season', @Num, @Title, 0, '{}', @Now);
                            SELECT last_insert_rowid();
                            """,
                            new { WorkId = falloutWorkId, Num = s, Title = s == 0 ? "Specials" : $"Season {s}", Now = now },
                            tx);

                        var epTotal = s == 0 ? 3 : 8;
                        var filesToCreate = s == 0 ? 0 : s == 1 ? 4 : 8;
                        for (var e = 1; e <= epTotal; e++)
                        {
                            var uId = await conn.ExecuteScalarAsync<long>(
                                """
                                INSERT INTO media_units (
                                    work_id, container_id, unit_kind, unit_number, title,
                                    air_or_release_date, monitored, required_audio_langs_json, required_sub_langs_json,
                                    quality_cutoff_met, language_cutoff_met, created_at
                                ) VALUES (
                                    @WorkId, @ContainerId, 'episode', @Ep, @Title,
                                    @AirDate, 0, '[]', '[]', 0, 0, @Now
                                );
                                SELECT last_insert_rowid();
                                """,
                                new { WorkId = falloutWorkId, ContainerId = cId, Ep = e, Title = $"Ep {e}", AirDate = pastDate, Now = now },
                                tx);

                            if (e <= filesToCreate)
                            {
                                await conn.ExecuteAsync(
                                    """
                                    INSERT INTO media_files (
                                        work_id, unit_id, relative_path, size_bytes, quality_name,
                                        detected_audio_langs_json, detected_sub_langs_json, verified_status,
                                        inode_hardlinked, imported_at
                                    ) VALUES (
                                        @WorkId, @UnitId, @RelPath, 1000000, 'WEBDL-1080p',
                                        '["de"]', '[]', 'VerifiedPass', 0, @Now
                                    );
                                    """,
                                    new { WorkId = falloutWorkId, UnitId = uId, RelPath = $"Fallout/S{s:D2}E{e:D2}.mkv", Now = now },
                                    tx);
                            }
                        }
                    }

                    ongoingWorkId = await conn.ExecuteScalarAsync<long>(
                        """
                        INSERT INTO media_works (
                            library_id, work_kind, primary_provider, episode_ordering_scheme,
                            title, sort_title, alt_titles_json, original_language, year, status,
                            overview, monitored, locked_fields_json, external_ids_json, tags_json,
                            added_at, updated_at
                        ) VALUES (
                            @LibId, 'tv', 'tvdb', 'aired',
                            'Ongoing Show', 'ongoing show', '[]', 'ja', 2025, 'continuing',
                            '', 1, '[]', '{}', '[]',
                            @Now, @Now
                        );
                        SELECT last_insert_rowid();
                        """,
                        new { LibId = tvLib.Id, Now = now },
                        tx);

                    // Specials: 3 unmonitored
                    var specId = await conn.ExecuteScalarAsync<long>(
                        """
                        INSERT INTO media_containers (work_id, container_kind, container_number, title, monitored, external_ids_json, created_at)
                        VALUES (@WorkId, 'season', 0, 'Specials', 0, '{}', @Now);
                        SELECT last_insert_rowid();
                        """,
                        new { WorkId = ongoingWorkId, Now = now },
                        tx);
                    for (var e = 1; e <= 3; e++)
                    {
                        await conn.ExecuteAsync(
                            """
                            INSERT INTO media_units (
                                work_id, container_id, unit_kind, unit_number, title,
                                air_or_release_date, monitored, required_audio_langs_json, required_sub_langs_json,
                                quality_cutoff_met, language_cutoff_met, created_at
                            ) VALUES (
                                @WorkId, @ContainerId, 'episode', @Ep, @Title,
                                @AirDate, 0, '[]', '[]', 0, 0, @Now
                            );
                            """,
                            new { WorkId = ongoingWorkId, ContainerId = specId, Ep = e, Title = $"Special {e}", AirDate = pastDate, Now = now },
                            tx);
                    }

                    // Season 1: 10 monitored aired episodes, 7 downloaded (episode 1 has 2 duplicate files -> 8 media_files rows)
                    var s1Id = await conn.ExecuteScalarAsync<long>(
                        """
                        INSERT INTO media_containers (work_id, container_kind, container_number, title, monitored, external_ids_json, created_at)
                        VALUES (@WorkId, 'season', 1, 'Season 1', 1, '{}', @Now);
                        SELECT last_insert_rowid();
                        """,
                        new { WorkId = ongoingWorkId, Now = now },
                        tx);
                    for (var e = 1; e <= 10; e++)
                    {
                        var uId = await conn.ExecuteScalarAsync<long>(
                            """
                            INSERT INTO media_units (
                                work_id, container_id, unit_kind, unit_number, title,
                                air_or_release_date, monitored, required_audio_langs_json, required_sub_langs_json,
                                quality_cutoff_met, language_cutoff_met, created_at
                            ) VALUES (
                                @WorkId, @ContainerId, 'episode', @Ep, @Title,
                                @AirDate, 1, '[]', '[]', 0, 0, @Now
                            );
                            SELECT last_insert_rowid();
                            """,
                            new { WorkId = ongoingWorkId, ContainerId = s1Id, Ep = e, Title = $"Episode {e}", AirDate = pastDate, Now = now },
                            tx);

                        if (e <= 7)
                        {
                            var copies = e == 1 ? 2 : 1;
                            for (var c = 1; c <= copies; c++)
                            {
                                await conn.ExecuteAsync(
                                    """
                                    INSERT INTO media_files (
                                        work_id, unit_id, relative_path, size_bytes, quality_name,
                                        detected_audio_langs_json, detected_sub_langs_json, verified_status,
                                        inode_hardlinked, imported_at
                                    ) VALUES (
                                        @WorkId, @UnitId, @RelPath, 1000000, 'WEBDL-1080p',
                                        '["de"]', '[]', 'VerifiedPass', 0, @Now
                                    );
                                    """,
                                    new { WorkId = ongoingWorkId, UnitId = uId, RelPath = $"Ongoing/S01E{e:D2}-{c}.mkv", Now = now },
                                    tx);
                            }
                        }
                    }

                    // Season 2: 2 future unaired monitored episodes + 1 TBA monitored episode
                    var s2Id = await conn.ExecuteScalarAsync<long>(
                        """
                        INSERT INTO media_containers (work_id, container_kind, container_number, title, monitored, external_ids_json, created_at)
                        VALUES (@WorkId, 'season', 2, 'Season 2', 1, '{}', @Now);
                        SELECT last_insert_rowid();
                        """,
                        new { WorkId = ongoingWorkId, Now = now },
                        tx);
                    for (var e = 1; e <= 3; e++)
                    {
                        await conn.ExecuteAsync(
                            """
                            INSERT INTO media_units (
                                work_id, container_id, unit_kind, unit_number, title,
                                air_or_release_date, monitored, required_audio_langs_json, required_sub_langs_json,
                                quality_cutoff_met, language_cutoff_met, created_at
                            ) VALUES (
                                @WorkId, @ContainerId, 'episode', @Ep, @Title,
                                @AirDate, 1, '[]', '[]', 0, 0, @Now
                            );
                            """,
                            new
                            {
                                WorkId = ongoingWorkId,
                                ContainerId = s2Id,
                                Ep = e,
                                Title = e == 3 ? "TBA" : $"Future {e}",
                                AirDate = e == 3 ? "" : futureDate,
                                Now = now,
                            },
                            tx);
                    }
                });

            var works = await catalog.ListWorksAsync();
            var fallout = Assert.Single(works, w => w.Id == falloutWorkId);
            Assert.Equal(19, fallout.UnitCount);
            Assert.Equal(12, fallout.FileCount);
            Assert.Equal(12, fallout.DownloadedUnitCount);
            Assert.Equal(12, fallout.MonitoredUnitCount);

            var ongoing = Assert.Single(works, w => w.Id == ongoingWorkId);
            Assert.Equal(16, ongoing.UnitCount);
            Assert.Equal(8, ongoing.FileCount); // includes duplicate file on E01
            Assert.Equal(7, ongoing.DownloadedUnitCount); // distinct downloaded units (X = 7)
            Assert.Equal(10, ongoing.MonitoredUnitCount); // monitored aired episodes (Y = 10, excluding Specials and future/TBA)

            var tree = await catalog.GetWorkTreeAsync(ongoingWorkId);
            Assert.NotNull(tree);
            Assert.Equal(7, tree!.Work.DownloadedUnitCount);
            Assert.Equal(10, tree.Work.MonitoredUnitCount);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup race
            }
        }
    }

    private sealed class NullMetadataGateway : IMetadataHttpGateway
    {
        public Task<string?> SendAsync(MetadataHttpRequest request, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
    }
}
