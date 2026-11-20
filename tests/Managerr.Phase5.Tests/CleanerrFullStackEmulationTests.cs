using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Dapper;
using Managerr.Catalog;
using Managerr.Cleanerr;
using Managerr.Common.Net;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Downloaders;
using Managerr.Host.Services;
using Managerr.Metadata;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Managerr.Phase5.Tests;

public sealed class CleanerrFullStackEmulationTests
{
    [Fact]
    public async Task FullStackEmulation_SabnzbdFfmpegImport_PlexSync_DiscordInteractions_ActiveStreamGuard_AndPhysicalDeletion()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-cleanerr-e2e-{Guid.NewGuid():N}");
        var moviesRoot = Path.Combine(tempDir, "libraries", "movies");
        var tvRoot = Path.Combine(tempDir, "libraries", "tv");
        var sabRoot = Path.Combine(tempDir, "sab-completed");
        var httpFactory = new LoopbackAndDiscordCapturingHttpClientFactory();
        EmulatedExternalStack? extStack = null;

        try
        {
            Directory.CreateDirectory(moviesRoot);
            Directory.CreateDirectory(tvRoot);
            Directory.CreateDirectory(sabRoot);
            extStack = await EmulatedExternalStack.StartAsync();

            // 1. Generate 10 real H.264/AAC .mkv files via ffmpeg in individual SABnzbd completed job folders (5 Movies + 5 Episodes across 3 TV Series)
            var jobSpecs = new[]
            {
                (NzoId: "sab-nzo-1", Name: "Old.Stale.Movie.2020.1080p.BluRay.x264-EMUL", Category: "movies", AddSrt: true),
                (NzoId: "sab-nzo-2", Name: "Protected.Favorite.2021.1080p.BluRay.x264-EMUL", Category: "movies", AddSrt: false),
                (NzoId: "sab-nzo-3", Name: "Rewatched.Movie.2022.1080p.BluRay.x264-EMUL", Category: "movies", AddSrt: false),
                (NzoId: "sab-nzo-4", Name: "Active.Stream.Movie.2023.1080p.BluRay.x264-EMUL", Category: "movies", AddSrt: false),
                (NzoId: "sab-nzo-5", Name: "Fresh.Movie.2024.1080p.BluRay.x264-EMUL", Category: "movies", AddSrt: false),
                (NzoId: "sab-nzo-6", Name: "Stale.Series.S01E01.1080p.WEB-DL.x264-EMUL", Category: "tv", AddSrt: true),
                (NzoId: "sab-nzo-7", Name: "Stale.Series.S02E01.1080p.WEB-DL.x264-EMUL", Category: "tv", AddSrt: false),
                (NzoId: "sab-nzo-8", Name: "Protected.Show.S01E01.1080p.WEB-DL.x264-EMUL", Category: "tv", AddSrt: false),
                (NzoId: "sab-nzo-9", Name: "Andor.S01E01.1080p.WEB-DL.x264-EMUL", Category: "tv", AddSrt: false),
                (NzoId: "sab-nzo-10", Name: "Andor.S01E02.1080p.WEB-DL.x264-EMUL", Category: "tv", AddSrt: false),
            };

            var ffmpegTasks = jobSpecs.Select(async spec =>
            {
                var jobFolder = Path.Combine(sabRoot, spec.Name);
                Directory.CreateDirectory(jobFolder);
                var mkvPath = Path.Combine(jobFolder, $"{spec.Name}.mkv");
                await GenerateTinyMkvWithFfmpegAsync(mkvPath);
                if (spec.AddSrt)
                {
                    var srtPath = Path.Combine(jobFolder, $"{spec.Name}.en.srt");
                    await File.WriteAllTextAsync(srtPath, "1\n00:00:00,000 --> 00:00:00,900\nSample subtitle.\n");
                }

                var size = new FileInfo(mkvPath).Length;
                extStack.AddSabJob(spec.NzoId, spec.Name, spec.Category, jobFolder, size);
            });
            await Task.WhenAll(ffmpegTasks);

            // 2. Boot real ASP.NET Core host with real services + Loopback/Discord HTTP factory
            await using var factory = CreateWebFactory(tempDir, httpFactory);
            using var client = await CreateAdminClientAsync(factory);

            // 3. Configure Plex settings, Cleanerr General settings, and Discord Bot settings (Ed25519 keypair)
            var (pubKeyHex, privSeed32) = CleanerrCrypto.GenerateEd25519KeyPair();
            const string discordGuildId = "111222333444555";
            const string discordChannelId = "666777888999000";

            using (var cfgScope = factory.Services.CreateScope())
            {
                var appSettings = cfgScope.ServiceProvider.GetRequiredService<IAppSettingsService>();
                await appSettings.SetAsync("plex.bound_server_url", extStack.PlexUrl, isSecret: false);
                await appSettings.SetAsync("plex.owner_admin_token", "plex-emul-owner-token", isSecret: true);
            }

            using (var genResp = await client.PutAsJsonAsync("/api/v1/cleanerr/settings", new UpdateCleanerrGeneralSettingsRequest(
                DefaultGraceDays: 7,
                SeerrUrl: extStack.SeerrUrl,
                SeerrApiKey: "seerr-emul-api-key",
                FinalWarningHours: 24)))
            {
                Assert.Equal(HttpStatusCode.OK, genResp.StatusCode);
            }

            using (var discResp = await client.PutAsJsonAsync("/api/v1/cleanerr/discord/settings", new UpdateCleanerrDiscordSettingsRequest(
                BotToken: "emul.bot.token.for.testing",
                ChannelId: discordChannelId,
                ApplicationId: "999888777666555",
                PublicKeyHex: pubKeyHex,
                GatewayPassiveShadowMode: true,
                GuildId: discordGuildId,
                AuthMode: "channel_members",
                NotifyOnStaged: true,
                NotifyOnRescued: true,
                NotifyOnDeleted: true,
                NotifyOnFinalWarning: true,
                FinalWarningHours: 24)))
            {
                Assert.Equal(HttpStatusCode.OK, discResp.StatusCode);
            }

            // 4. Create Libraries via HTTP
            var moviesLib = await PostJsonAndReadAsync<LibrarySummary>(client, "/api/v1/libraries/", new CreateLibraryRequest(
                Name: "Movies",
                MediaType: "movies",
                RootFolderPath: moviesRoot,
                PlexSectionId: "1"));
            var tvLib = await PostJsonAndReadAsync<LibrarySummary>(client, "/api/v1/libraries/", new CreateLibraryRequest(
                Name: "TV",
                MediaType: "tv",
                RootFolderPath: tvRoot,
                PlexSectionId: "2"));

            // 5. Pre-create 5 Movie works & 3 TV Series works via HTTP (QualityProfileId: null, InitialRequiredAudioLangs: ["en"])
            var oldStaleWork = await PostJsonAndReadAsync<MediaWorkDetailTree>(client, "/api/v1/catalog/works", new CreateMediaWorkRequest(
                LibraryId: moviesLib.Id,
                Title: "Old Stale Movie",
                WorkKind: "movies",
                OriginalLanguage: "en",
                Year: 2020,
                InitialRequiredAudioLangs: ["en"],
                QualityProfileId: null,
                SkipUpstreamHydration: true));

            _ = await PostJsonAndReadAsync<MediaWorkDetailTree>(client, "/api/v1/catalog/works", new CreateMediaWorkRequest(
                LibraryId: moviesLib.Id,
                Title: "Protected Favorite",
                WorkKind: "movies",
                OriginalLanguage: "en",
                Year: 2021,
                InitialRequiredAudioLangs: ["en"],
                QualityProfileId: null,
                SkipUpstreamHydration: true));

            _ = await PostJsonAndReadAsync<MediaWorkDetailTree>(client, "/api/v1/catalog/works", new CreateMediaWorkRequest(
                LibraryId: moviesLib.Id,
                Title: "Rewatched Movie",
                WorkKind: "movies",
                OriginalLanguage: "en",
                Year: 2022,
                InitialRequiredAudioLangs: ["en"],
                QualityProfileId: null,
                SkipUpstreamHydration: true));

            _ = await PostJsonAndReadAsync<MediaWorkDetailTree>(client, "/api/v1/catalog/works", new CreateMediaWorkRequest(
                LibraryId: moviesLib.Id,
                Title: "Active Stream Movie",
                WorkKind: "movies",
                OriginalLanguage: "en",
                Year: 2023,
                InitialRequiredAudioLangs: ["en"],
                QualityProfileId: null,
                SkipUpstreamHydration: true));

            _ = await PostJsonAndReadAsync<MediaWorkDetailTree>(client, "/api/v1/catalog/works", new CreateMediaWorkRequest(
                LibraryId: moviesLib.Id,
                Title: "Fresh Movie",
                WorkKind: "movies",
                OriginalLanguage: "en",
                Year: 2024,
                InitialRequiredAudioLangs: ["en"],
                QualityProfileId: null,
                SkipUpstreamHydration: true));

            var staleSeriesWork = await PostJsonAndReadAsync<MediaWorkDetailTree>(client, "/api/v1/catalog/works", new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: "Stale Series",
                WorkKind: "tv",
                OriginalLanguage: "en",
                Year: 2019,
                InitialRequiredAudioLangs: ["en"],
                QualityProfileId: null,
                SkipUpstreamHydration: true,
                Containers:
                [
                    new MetadataContainerSeed(
                        ContainerKind: "season",
                        ContainerNumber: 1,
                        Title: "Season 1",
                        ReleaseDate: "2019-04-01",
                        Units: [new MetadataUnitSeed("episode", 1, 1, "Pilot", "2019-04-01")]),
                    new MetadataContainerSeed(
                        ContainerKind: "season",
                        ContainerNumber: 2,
                        Title: "Season 2",
                        ReleaseDate: "2020-04-01",
                        Units: [new MetadataUnitSeed("episode", 1, 1, "Return", "2020-04-01")]),
                ]));

            var protectedShowWork = await PostJsonAndReadAsync<MediaWorkDetailTree>(client, "/api/v1/catalog/works", new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: "Protected Show",
                WorkKind: "tv",
                OriginalLanguage: "en",
                Year: 2020,
                InitialRequiredAudioLangs: ["en"],
                QualityProfileId: null,
                SkipUpstreamHydration: true,
                Containers:
                [
                    new MetadataContainerSeed(
                        ContainerKind: "season",
                        ContainerNumber: 1,
                        Title: "Season 1",
                        ReleaseDate: "2020-06-01",
                        Units: [new MetadataUnitSeed("episode", 1, 1, "Opening", "2020-06-01")]),
                ]));

            var andorWork = await PostJsonAndReadAsync<MediaWorkDetailTree>(client, "/api/v1/catalog/works", new CreateMediaWorkRequest(
                LibraryId: tvLib.Id,
                Title: "Andor",
                WorkKind: "tv",
                OriginalLanguage: "en",
                Year: 2022,
                InitialRequiredAudioLangs: ["en"],
                QualityProfileId: null,
                SkipUpstreamHydration: true,
                Containers:
                [
                    new MetadataContainerSeed(
                        ContainerKind: "season",
                        ContainerNumber: 1,
                        Title: "Season 1",
                        ReleaseDate: "2022-09-21",
                        Units:
                        [
                            new MetadataUnitSeed("episode", 1, 1, "Kassa", "2022-09-21"),
                            new MetadataUnitSeed("episode", 2, 2, "That Would Be Me", "2022-09-21"),
                        ]),
                ]));

            // 6. Create Media Requests for Old Stale Movie & Stale Series via HTTP (creates media_requests rows linked to their works)
            var oldStaleRequest = await PostJsonAndReadAsync<MediaRequestSummary>(client, "/api/v1/requests/", new SubmitMediaRequestCommand(
                LibraryId: moviesLib.Id,
                Title: "Old Stale Movie",
                MediaVertical: "movies",
                RequestedAudioLang: "en"));
            Assert.Equal(oldStaleWork.Work.Id, oldStaleRequest.WorkId);

            var staleSeriesRequest = await PostJsonAndReadAsync<MediaRequestSummary>(client, "/api/v1/requests/", new SubmitMediaRequestCommand(
                LibraryId: tvLib.Id,
                Title: "Stale Series",
                MediaVertical: "tv",
                RequestedAudioLang: "en"));
            Assert.Equal(staleSeriesWork.Work.Id, staleSeriesRequest.WorkId);

            // 7. Register real SABnzbd download client pointing to loopback HttpListener & run process-completed
            _ = await PostJsonAndReadAsync<DownloadClientSummary>(client, "/api/v1/downloaders/", new UpsertDownloadClientRequest(
                Name: "Emulated SABnzbd",
                Implementation: "sabnzbd",
                Host: "127.0.0.1",
                Port: extStack.SabPort,
                UseSsl: false,
                ApiKey: "sab-emul-api-key",
                CategoryMappings: new Dictionary<string, string>
                {
                    ["movies"] = "movies",
                    ["tv"] = "tv",
                },
                RemoveCompleted: true));

            var importReport = await PostJsonAndReadAsync<ProcessCompletedDownloadsReport>(client, "/api/v1/downloaders/process-completed", new { });
            Assert.Equal(10, importReport.ImportedCount);
            Assert.Equal(0, importReport.QuarantinedCount);
            Assert.Equal(0, importReport.RejectedCount);
            Assert.Equal(10, extStack.RemovedSabJobIds.Count);

            var importedOldStalePath = Assert.Single(importReport.Items, i => i.Title.Contains("Old.Stale.Movie", StringComparison.OrdinalIgnoreCase)).DestinationFilePath!;
            var importedProtectedPath = Assert.Single(importReport.Items, i => i.Title.Contains("Protected.Favorite", StringComparison.OrdinalIgnoreCase)).DestinationFilePath!;
            var importedRewatchedPath = Assert.Single(importReport.Items, i => i.Title.Contains("Rewatched.Movie", StringComparison.OrdinalIgnoreCase)).DestinationFilePath!;
            var importedActiveStreamPath = Assert.Single(importReport.Items, i => i.Title.Contains("Active.Stream.Movie", StringComparison.OrdinalIgnoreCase)).DestinationFilePath!;
            var importedFreshPath = Assert.Single(importReport.Items, i => i.Title.Contains("Fresh.Movie", StringComparison.OrdinalIgnoreCase)).DestinationFilePath!;
            var importedStaleSeriesS1E1Path = Assert.Single(importReport.Items, i => i.Title.Contains("Stale.Series.S01E01", StringComparison.OrdinalIgnoreCase)).DestinationFilePath!;
            var importedStaleSeriesS2E1Path = Assert.Single(importReport.Items, i => i.Title.Contains("Stale.Series.S02E01", StringComparison.OrdinalIgnoreCase)).DestinationFilePath!;
            var importedProtectedShowE1Path = Assert.Single(importReport.Items, i => i.Title.Contains("Protected.Show.S01E01", StringComparison.OrdinalIgnoreCase)).DestinationFilePath!;
            var importedAndorE1Path = Assert.Single(importReport.Items, i => i.Title.Contains("Andor.S01E01", StringComparison.OrdinalIgnoreCase)).DestinationFilePath!;
            var importedAndorE2Path = Assert.Single(importReport.Items, i => i.Title.Contains("Andor.S01E02", StringComparison.OrdinalIgnoreCase)).DestinationFilePath!;

            Assert.True(File.Exists(importedOldStalePath));
            Assert.True(File.Exists(importedProtectedPath));
            Assert.True(File.Exists(importedRewatchedPath));
            Assert.True(File.Exists(importedActiveStreamPath));
            Assert.True(File.Exists(importedFreshPath));
            Assert.True(File.Exists(importedStaleSeriesS1E1Path));
            Assert.True(File.Exists(importedStaleSeriesS2E1Path));
            Assert.True(File.Exists(importedProtectedShowE1Path));
            Assert.True(File.Exists(importedAndorE1Path));
            Assert.True(File.Exists(importedAndorE2Path));

            // Ensure companion .srt sidecars sit next to Old Stale Movie and Stale Series S01E01 on disk
            var oldStaleSrtPath = Directory.EnumerateFiles(Path.GetDirectoryName(importedOldStalePath)!, "*.srt").FirstOrDefault();
            if (oldStaleSrtPath is null)
            {
                oldStaleSrtPath = Path.ChangeExtension(importedOldStalePath, ".en.srt");
                await File.WriteAllTextAsync(oldStaleSrtPath, "1\n00:00:00,000 --> 00:00:00,900\nSidecar subtitle.\n");
            }
            Assert.True(File.Exists(oldStaleSrtPath));

            var staleSeriesS1SrtPath = Directory.EnumerateFiles(Path.GetDirectoryName(importedStaleSeriesS1E1Path)!, "*.srt").FirstOrDefault();
            if (staleSeriesS1SrtPath is null)
            {
                staleSeriesS1SrtPath = Path.ChangeExtension(importedStaleSeriesS1E1Path, ".en.srt");
                await File.WriteAllTextAsync(staleSeriesS1SrtPath, "1\n00:00:00,000 --> 00:00:00,900\nSeries sidecar.\n");
            }
            Assert.True(File.Exists(staleSeriesS1SrtPath));

            // 8. Sync Catalog into Cleanerr -> creates 8 mgr-* whole items (5 movies + 3 shows, 0 season/episode) & populates seerr_id from media_requests
            using (var catSyncResp = await client.PostAsync("/api/v1/cleanerr/media/sync-catalog", null))
            {
                Assert.Equal(HttpStatusCode.OK, catSyncResp.StatusCode);
            }

            var prePlexMedia = (await client.GetFromJsonAsync<CleanerrMediaListResponse>("/api/v1/cleanerr/media?pageSize=50"))!.Items;
            Assert.Equal(8, prePlexMedia.Count);
            Assert.DoesNotContain(prePlexMedia, m => m.MediaType is "season" or "episode");
            Assert.Contains(prePlexMedia, m => m.PlexRatingKey.StartsWith("mgr-", StringComparison.Ordinal));
            var preOldStale = prePlexMedia.Single(m => m.WorkId == oldStaleWork.Work.Id && m.MediaType == "movie");
            Assert.Equal(oldStaleRequest.Id, preOldStale.SeerrId);
            var preStaleSeries = prePlexMedia.Single(m => m.WorkId == staleSeriesWork.Work.Id && m.MediaType == "show");
            Assert.Equal(staleSeriesRequest.Id, preStaleSeries.SeerrId);

            // 9. Populate Plex HttpListener using actual imported disk paths & realistic view timestamps
            var now = DateTimeOffset.UtcNow;
            extStack.SetPlexMovies(
                new PlexMovieSpec("1001", "Old Stale Movie", 2020, now.AddDays(-200), now.AddDays(-150), 1, importedOldStalePath),
                new PlexMovieSpec("1002", "Protected Favorite", 2021, now.AddDays(-200), now.AddDays(-140), 1, importedProtectedPath),
                new PlexMovieSpec("1003", "Rewatched Movie", 2022, now.AddDays(-200), now.AddDays(-130), 1, importedRewatchedPath),
                new PlexMovieSpec("1004", "Active Stream Movie", 2023, now.AddDays(-200), now.AddDays(-160), 1, importedActiveStreamPath),
                new PlexMovieSpec("1005", "Fresh Movie", 2024, now.AddDays(-30), now.AddDays(-5), 1, importedFreshPath));

            extStack.SetPlexTv(
                shows:
                [
                    new PlexShowSpec("3000", "Stale Series", 2019, now.AddDays(-200), now.AddDays(-120), 2),
                    new PlexShowSpec("4000", "Protected Show", 2020, now.AddDays(-200), now.AddDays(-140), 1),
                    new PlexShowSpec("2000", "Andor", 2022, now.AddDays(-180), now.AddDays(-2), 2),
                ],
                seasons:
                [
                    new PlexSeasonSpec("3001", "3000", "Stale Series", "Season 1", 1, now.AddDays(-200), now.AddDays(-135), 1),
                    new PlexSeasonSpec("3003", "3000", "Stale Series", "Season 2", 2, now.AddDays(-200), now.AddDays(-120), 1),
                    new PlexSeasonSpec("4001", "4000", "Protected Show", "Season 1", 1, now.AddDays(-200), now.AddDays(-140), 1),
                    new PlexSeasonSpec("2001", "2000", "Andor", "Season 1", 1, now.AddDays(-180), now.AddDays(-2), 2),
                ],
                episodes:
                [
                    new PlexEpisodeSpec("3002", "3001", "3000", "Season 1", "Stale Series", "Pilot", 2019, 1, 1, now.AddDays(-200), now.AddDays(-135), 1, importedStaleSeriesS1E1Path),
                    new PlexEpisodeSpec("3004", "3003", "3000", "Season 2", "Stale Series", "Return", 2020, 2, 1, now.AddDays(-200), now.AddDays(-120), 1, importedStaleSeriesS2E1Path),
                    new PlexEpisodeSpec("4002", "4001", "4000", "Season 1", "Protected Show", "Opening", 2020, 1, 1, now.AddDays(-200), now.AddDays(-140), 1, importedProtectedShowE1Path),
                    new PlexEpisodeSpec("2002", "2001", "2000", "Season 1", "Andor", "Kassa", 2022, 1, 1, now.AddDays(-180), now.AddDays(-120), 1, importedAndorE1Path),
                    new PlexEpisodeSpec("2003", "2001", "2000", "Season 1", "Andor", "That Would Be Me", 2022, 1, 2, now.AddDays(-180), now.AddDays(-2), 1, importedAndorE2Path),
                ]);

            // 10. Sync Plex -> upgrades mgr-* rows in place to Plex numeric ratingKeys with 0 season/episode rows, 0 type=3 requests & 0 Plex collection requests
            var plexSync1 = await PostJsonAndReadAsync<CleanerrPlexSyncResult>(client, "/api/v1/cleanerr/sync-plex", new { });
            Assert.Equal(5, plexSync1.MoviesSynced);
            Assert.Equal(3, plexSync1.ShowsSynced);
            Assert.Equal(0, plexSync1.SeasonsSynced);
            Assert.Equal(0, plexSync1.EpisodesSynced);

            var postPlexMedia = (await client.GetFromJsonAsync<CleanerrMediaListResponse>("/api/v1/cleanerr/media?pageSize=50"))!.Items;
            Assert.Equal(8, postPlexMedia.Count);
            Assert.DoesNotContain(postPlexMedia, m => m.MediaType is "season" or "episode");
            Assert.DoesNotContain(postPlexMedia, m => m.PlexRatingKey.StartsWith("mgr-", StringComparison.Ordinal));

            var oldStaleMedia = postPlexMedia.Single(m => m.PlexRatingKey == "1001");
            var protectedFavMedia = postPlexMedia.Single(m => m.PlexRatingKey == "1002");
            var rewatchedMedia = postPlexMedia.Single(m => m.PlexRatingKey == "1003");
            var activeStreamMedia = postPlexMedia.Single(m => m.PlexRatingKey == "1004");
            var freshMedia = postPlexMedia.Single(m => m.PlexRatingKey == "1005");
            var staleSeriesMedia = postPlexMedia.Single(m => m.PlexRatingKey == "3000");
            var protectedShowMedia = postPlexMedia.Single(m => m.PlexRatingKey == "4000");
            var andorShowMedia = postPlexMedia.Single(m => m.PlexRatingKey == "2000");

            Assert.Equal(oldStaleRequest.Id, oldStaleMedia.SeerrId);
            Assert.Equal(staleSeriesRequest.Id, staleSeriesMedia.SeerrId);
            Assert.Equal(staleSeriesWork.Work.Id, staleSeriesMedia.WorkId);
            Assert.Equal(
                new FileInfo(importedStaleSeriesS1E1Path).Length + new FileInfo(importedStaleSeriesS2E1Path).Length,
                staleSeriesMedia.SizeBytes);
            Assert.Equal(protectedShowWork.Work.Id, protectedShowMedia.WorkId);
            Assert.Equal(andorWork.Work.Id, andorShowMedia.WorkId);
            Assert.True(DateTimeOffset.Parse(andorShowMedia.LastViewedAt!, CultureInfo.InvariantCulture) >= now.AddDays(-3));

            AssertZeroPlexSeasonAndCollectionRequests(extStack);

            // 11. Send Plex scrobble webhook as multipart/form-data ("payload") for Fresh Movie
            using (var freshWebhookResp = await SendPlexMultipartWebhookAsync(client, "media.scrobble", "1005", "admin"))
            {
                Assert.Equal(HttpStatusCode.OK, freshWebhookResp.StatusCode);
            }

            var freshAfterHook = (await client.GetFromJsonAsync<CleanerrMediaListResponse>("/api/v1/cleanerr/media?search=Fresh"))!.Items
                .Single(m => m.PlexRatingKey == "1005");
            Assert.Equal(2, freshAfterHook.ViewCount);
            Assert.NotNull(freshAfterHook.LastViewedAt);
            Assert.True(DateTimeOffset.Parse(freshAfterHook.LastViewedAt!, CultureInfo.InvariantCulture) >= now.AddMinutes(-5));

            // 12. Create & live-enable workflow (watched > 90d, 7d grace, delete_files_and_unmonitor + clean_empty_folders + reset_requests + add_import_exclusion)
            const string wfName = "FullStack 90d Cleanup";
            var wfGraph = JsonNode.Parse("""
                {
                  "nodes": [
                    { "id": "src", "data": { "label": "Movies & TV Series", "node_type": "source.plex_library", "config": { "mediaTypes": ["movie", "show"] } } },
                    { "id": "watch", "data": { "label": "Watched > 90d", "node_type": "filter.watch_state", "config": { "last_watched_older_than_days": 90, "protect_in_progress": true } } },
                    { "id": "grace", "data": { "label": "7d Grace", "node_type": "queue.grace_period", "config": { "grace_days": 7 } } },
                    { "id": "act", "data": { "label": "Delete & Reset", "node_type": "action.managerr", "config": { "mode": "delete_files_and_unmonitor", "clean_empty_folders": true, "reset_requests": true, "add_import_exclusion": true } } }
                  ],
                  "edges": [
                    { "source": "src", "target": "watch" },
                    { "source": "watch", "target": "grace" },
                    { "source": "grace", "target": "act" }
                  ]
                }
                """)!.AsObject();

            var workflow = await PostJsonAndReadAsync<CleanerrWorkflowSummary>(client, "/api/v1/cleanerr/workflows", new UpsertCleanerrWorkflowRequest(
                Name: wfName,
                Description: "End-to-end emulated workflow",
                Enabled: true,
                DryRun: false,
                GraphJson: wfGraph,
                EditorMode: "dag",
                ConfirmWorkflowName: wfName), expectedStatus: HttpStatusCode.Created);

            httpFactory.ClearDiscordRequests();
            var runResult = await PostJsonAndReadAsync<CleanerrWorkflowRunResult>(client, $"/api/v1/cleanerr/workflows/{workflow.Id}/execute", new
            {
                dryRun = false,
            });
            Assert.False(runResult.DryRun);
            Assert.Equal(6, runResult.Summary.StagedCount);

            var stagedQueue = (await client.GetFromJsonAsync<List<CleanerrDeletionQueueItem>>("/api/v1/cleanerr/queue?status=staged"))!;
            var stagedMediaIds = stagedQueue.Select(q => q.MediaItemId).ToHashSet();
            Assert.Contains(oldStaleMedia.Id, stagedMediaIds);
            Assert.Contains(protectedFavMedia.Id, stagedMediaIds);
            Assert.Contains(rewatchedMedia.Id, stagedMediaIds);
            Assert.Contains(activeStreamMedia.Id, stagedMediaIds);
            Assert.Contains(staleSeriesMedia.Id, stagedMediaIds);
            Assert.Contains(protectedShowMedia.Id, stagedMediaIds);
            Assert.DoesNotContain(freshMedia.Id, stagedMediaIds);
            Assert.DoesNotContain(andorShowMedia.Id, stagedMediaIds);

            // Verify Discord Bot API received interactive staged announcement listing whole items with zero S01E01/Season episode entries
            var stagedDiscordMsg = Assert.Single(httpFactory.DiscordRequests);
            Assert.Contains($"/channels/{discordChannelId}/messages", stagedDiscordMsg.Uri.AbsolutePath, StringComparison.Ordinal);
            var stagedDiscordJson = JsonNode.Parse(stagedDiscordMsg.Body)!.AsObject();
            var componentsJson = stagedDiscordJson["components"]!.ToJsonString();
            Assert.Contains("clnr_sel_excl:", componentsJson, StringComparison.Ordinal);
            Assert.Contains("clnr_excl:", componentsJson, StringComparison.Ordinal);
            Assert.Contains("clnr_postpone:", componentsJson, StringComparison.Ordinal);
            Assert.Contains("Stale Series", stagedDiscordMsg.Body, StringComparison.Ordinal);
            Assert.Contains("Protected Show", stagedDiscordMsg.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("S01E01", stagedDiscordMsg.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("S02E01", stagedDiscordMsg.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Season", stagedDiscordMsg.Body, StringComparison.OrdinalIgnoreCase);

            // 13. Sign Ed25519 Discord Interaction selecting Protected Favorite + Protected Show via clnr_sel_excl
            var interactionPayload = new JsonObject
            {
                ["type"] = 3,
                ["channel_id"] = discordChannelId,
                ["member"] = new JsonObject
                {
                    ["user"] = new JsonObject
                    {
                        ["id"] = "445566778899",
                        ["username"] = "homelab_member",
                    },
                },
                ["data"] = new JsonObject
                {
                    ["component_type"] = 3,
                    ["custom_id"] = $"clnr_sel_excl:{workflow.Id}:0",
                    ["values"] = new JsonArray(
                        protectedFavMedia.Id.ToString(CultureInfo.InvariantCulture),
                        protectedShowMedia.Id.ToString(CultureInfo.InvariantCulture)),
                },
            };
            using (var discInterResp = await SendSignedDiscordInteractionAsync(client, privSeed32, interactionPayload))
            {
                Assert.Equal(HttpStatusCode.OK, discInterResp.StatusCode);
            }

            var exclusions = (await client.GetFromJsonAsync<List<CleanerrExclusionRule>>("/api/v1/cleanerr/exclusions"))!;
            Assert.Contains(exclusions, e => e.MediaItemId == protectedFavMedia.Id);
            Assert.Contains(exclusions, e => e.MediaItemId == protectedShowMedia.Id);

            var mediaAfterExcl = (await client.GetFromJsonAsync<CleanerrMediaListResponse>("/api/v1/cleanerr/media?pageSize=50"))!.Items;
            Assert.True(mediaAfterExcl.Single(m => m.Id == protectedFavMedia.Id).IsExcluded);
            Assert.True(mediaAfterExcl.Single(m => m.Id == protectedShowMedia.Id).IsExcluded);

            var stagedAfterExcl = (await client.GetFromJsonAsync<List<CleanerrDeletionQueueItem>>("/api/v1/cleanerr/queue?status=staged"))!;
            Assert.DoesNotContain(stagedAfterExcl, q => q.MediaItemId == protectedFavMedia.Id || q.MediaItemId == protectedShowMedia.Id);

            // 14. Send Plex scrobble webhook for Rewatched Movie -> auto-rescued
            await Task.Delay(25);
            using (var rewatchHookResp = await SendPlexMultipartWebhookAsync(client, "media.scrobble", "1003", "admin"))
            {
                Assert.Equal(HttpStatusCode.OK, rewatchHookResp.StatusCode);
                var hookJson = JsonNode.Parse(await rewatchHookResp.Content.ReadAsStringAsync())!.AsObject();
                Assert.Equal(1, hookJson["rescued_count"]!.GetValue<int>());
            }

            var allQueueAfterRewatch = (await client.GetFromJsonAsync<List<CleanerrDeletionQueueItem>>("/api/v1/cleanerr/queue"))!;
            Assert.Equal("rescued", allQueueAfterRewatch.Single(q => q.MediaItemId == rewatchedMedia.Id).Status);

            // 15. Turn ON active stream for Active Stream Movie on Plex HttpListener & sync-plex
            extStack.SetActiveSessions("1004");
            var plexSyncActive = await PostJsonAndReadAsync<CleanerrPlexSyncResult>(client, "/api/v1/cleanerr/sync-plex", new { });
            Assert.True(plexSyncActive.ActiveStreamsDetected > 0);
            var activeStreamAfterSync = (await client.GetFromJsonAsync<CleanerrMediaListResponse>("/api/v1/cleanerr/media?search=Active"))!.Items
                .Single(m => m.PlexRatingKey == "1004");
            Assert.Equal(1, activeStreamAfterSync.ActiveStreamCount);

            // 16. Process queue at +6.5d -> Final Warning sent ONCE; deduplicated at +6.51d
            httpFactory.ClearDiscordRequests();
            var warnTick1 = await PostJsonAndReadAsync<CleanerrQueueProcessResult>(client, "/api/v1/cleanerr/queue/process", new
            {
                referenceNow = now.AddDays(6.5),
            });
            Assert.True(warnTick1.FinalWarningsSent >= 1);
            Assert.Equal(0, warnTick1.DeletedCount);
            Assert.NotEmpty(httpFactory.DiscordRequests);

            var discordCountAfterWarn1 = httpFactory.DiscordRequests.Count;
            var warnTick2 = await PostJsonAndReadAsync<CleanerrQueueProcessResult>(client, "/api/v1/cleanerr/queue/process", new
            {
                referenceNow = now.AddDays(6.51),
            });
            Assert.Equal(0, warnTick2.FinalWarningsSent);
            Assert.Equal(discordCountAfterWarn1, httpFactory.DiscordRequests.Count);

            // 17. Process queue at +8d -> Old Stale Movie AND whole Stale Series deleted (files + sidecars + empty Season/Series folders + Seerr + catalog unmonitored + import exclusion + Discord),
            //     Active Stream Movie deferred!
            httpFactory.ClearDiscordRequests();
            var expireTick = await PostJsonAndReadAsync<CleanerrQueueProcessResult>(client, "/api/v1/cleanerr/queue/process", new
            {
                referenceNow = now.AddDays(8),
            });
            Assert.Equal(2, expireTick.DeletedCount);
            Assert.Equal(1, expireTick.DeferredActiveStreamCount);

            var queueAfterExpire = (await client.GetFromJsonAsync<List<CleanerrDeletionQueueItem>>("/api/v1/cleanerr/queue"))!;
            Assert.Equal("deleted", queueAfterExpire.Single(q => q.MediaItemId == oldStaleMedia.Id).Status);
            Assert.Equal("deleted", queueAfterExpire.Single(q => q.MediaItemId == staleSeriesMedia.Id).Status);
            Assert.Equal("staged", queueAfterExpire.Single(q => q.MediaItemId == activeStreamMedia.Id).Status);

            Assert.False(File.Exists(importedOldStalePath));
            Assert.False(File.Exists(oldStaleSrtPath));
            Assert.False(Directory.Exists(Path.GetDirectoryName(importedOldStalePath)!));
            Assert.True(Directory.Exists(moviesRoot));

            var staleSeriesS1Dir = Path.GetDirectoryName(importedStaleSeriesS1E1Path)!;
            var staleSeriesS2Dir = Path.GetDirectoryName(importedStaleSeriesS2E1Path)!;
            var staleSeriesShowDir = Path.GetDirectoryName(staleSeriesS1Dir)!;
            Assert.False(File.Exists(importedStaleSeriesS1E1Path));
            Assert.False(File.Exists(staleSeriesS1SrtPath));
            Assert.False(File.Exists(importedStaleSeriesS2E1Path));
            Assert.False(Directory.Exists(staleSeriesS1Dir));
            Assert.False(Directory.Exists(staleSeriesS2Dir));
            Assert.False(Directory.Exists(staleSeriesShowDir));
            Assert.True(Directory.Exists(tvRoot));

            Assert.True(File.Exists(importedActiveStreamPath));

            // Verify catalog work/containers/units unmonitored, media_requests reset, Seerr DELETE called, import_list_exclusions populated, Discord deleted summary sent
            var oldStaleTreeAfterDelete = await client.GetFromJsonAsync<MediaWorkDetailTree>($"/api/v1/catalog/works/{oldStaleWork.Work.Id}");
            Assert.NotNull(oldStaleTreeAfterDelete);
            Assert.False(oldStaleTreeAfterDelete!.Containers[0].Units[0].Monitored);

            var staleSeriesTreeAfterDelete = await client.GetFromJsonAsync<MediaWorkDetailTree>($"/api/v1/catalog/works/{staleSeriesWork.Work.Id}");
            Assert.NotNull(staleSeriesTreeAfterDelete);
            Assert.False(staleSeriesTreeAfterDelete!.Work.Monitored);
            Assert.All(staleSeriesTreeAfterDelete.Containers, c => Assert.False(c.Monitored));
            Assert.All(staleSeriesTreeAfterDelete.Containers.SelectMany(c => c.Units), u => Assert.False(u.Monitored));

            var requestsAfterDelete = (await client.GetFromJsonAsync<List<MediaRequestSummary>>("/api/v1/requests/"))!;
            Assert.Equal("cleaned_reset", requestsAfterDelete.Single(r => r.Id == oldStaleRequest.Id).Status);
            Assert.Equal("cleaned_reset", requestsAfterDelete.Single(r => r.Id == staleSeriesRequest.Id).Status);

            Assert.Contains($"/api/v1/request/{oldStaleRequest.Id}", extStack.DeletedSeerrPaths);
            Assert.Contains($"/api/v1/request/{staleSeriesRequest.Id}", extStack.DeletedSeerrPaths);

            using (var dbScope = factory.Services.CreateScope())
            {
                var dbFactory = dbScope.ServiceProvider.GetRequiredService<ISqliteDbFactory>();
                await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core);
                var movieExclTitle = await conn.ExecuteScalarAsync<string?>(
                    "SELECT title FROM import_list_exclusions WHERE title LIKE 'Old Stale Movie%';");
                Assert.NotNull(movieExclTitle);
                var seriesExclTitle = await conn.ExecuteScalarAsync<string?>(
                    "SELECT title FROM import_list_exclusions WHERE title LIKE 'Stale Series%';");
                Assert.NotNull(seriesExclTitle);
            }

            Assert.Contains(httpFactory.DiscordRequests, d => d.Body.Contains("Reclaimed", StringComparison.OrdinalIgnoreCase));

            // 18. Stop active stream via Plex webhook + clear active session on Plex HttpListener -> process queue at +9d -> Active Stream Movie deleted!
            extStack.SetActiveSessions();
            using (var stopHookResp = await SendPlexMultipartWebhookAsync(client, "media.stop", "1004", "admin"))
            {
                Assert.Equal(HttpStatusCode.OK, stopHookResp.StatusCode);
            }

            var finalTick = await PostJsonAndReadAsync<CleanerrQueueProcessResult>(client, "/api/v1/cleanerr/queue/process", new
            {
                referenceNow = now.AddDays(9),
            });
            Assert.Equal(1, finalTick.DeletedCount);
            Assert.False(File.Exists(importedActiveStreamPath));

            // 19. Protected Favorite, Rewatched Movie, Fresh Movie, Protected Show, and Andor E1/E2 files ALL still exist intact on disk!
            Assert.True(File.Exists(importedProtectedPath));
            Assert.True(File.Exists(importedRewatchedPath));
            Assert.True(File.Exists(importedFreshPath));
            Assert.True(File.Exists(importedProtectedShowE1Path));
            Assert.True(File.Exists(importedAndorE1Path));
            Assert.True(File.Exists(importedAndorE2Path));

            // 20. Final verification: ZERO Plex season (type=3) requests and ZERO Plex collection requests ever issued
            AssertZeroPlexSeasonAndCollectionRequests(extStack);
        }
        finally
        {
            if (extStack is not null)
            {
                try { await extStack.DisposeAsync(); } catch { /* ignore */ }
            }
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task PlexEpisodeWebhook_MapsToWholeShow_PreservesViewCountOnPlayResume_AndAggregatesConcurrentEpisodeStreams()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-cleanerr-gapc-{Guid.NewGuid():N}");
        var tvRoot = Path.Combine(tempDir, "libraries", "tv");
        var httpFactory = new LoopbackAndDiscordCapturingHttpClientFactory();
        EmulatedExternalStack? extStack = null;

        try
        {
            Directory.CreateDirectory(tvRoot);
            extStack = await EmulatedExternalStack.StartAsync();

            var showDir = Path.Combine(tvRoot, "Andor (2022)", "Season 01");
            Directory.CreateDirectory(showDir);
            var e1Path = Path.Combine(showDir, "Andor.S01E01.mkv");
            var e2Path = Path.Combine(showDir, "Andor.S01E02.mkv");
            await File.WriteAllBytesAsync(e1Path, [0x01, 0x02]);
            await File.WriteAllBytesAsync(e2Path, [0x01, 0x02]);

            await using var factory = CreateWebFactory(tempDir, httpFactory);
            using var client = await CreateAdminClientAsync(factory);

            using (var cfgScope = factory.Services.CreateScope())
            {
                var appSettings = cfgScope.ServiceProvider.GetRequiredService<IAppSettingsService>();
                await appSettings.SetAsync("plex.bound_server_url", extStack.PlexUrl, isSecret: false);
                await appSettings.SetAsync("plex.owner_admin_token", "plex-emul-owner-token", isSecret: true);
            }

            _ = await PostJsonAndReadAsync<LibrarySummary>(client, "/api/v1/libraries/", new CreateLibraryRequest(
                Name: "TV",
                MediaType: "tv",
                RootFolderPath: tvRoot,
                PlexSectionId: "2"));

            var now = DateTimeOffset.UtcNow;
            extStack.SetPlexTv(
                show: new PlexShowSpec("2000", "Andor", 2022, now.AddDays(-30), now.AddDays(-2), 2),
                season: new PlexSeasonSpec("2001", "2000", "Andor", "Season 1", 1, now.AddDays(-30), now.AddDays(-2), 2),
                episodes:
                [
                    new PlexEpisodeSpec("2002", "2001", "2000", "Season 1", "Andor", "Kassa", 2022, 1, 1, now.AddDays(-30), now.AddDays(-5), 1, e1Path),
                    new PlexEpisodeSpec("2003", "2001", "2000", "Season 1", "Andor", "That Would Be Me", 2022, 1, 2, now.AddDays(-30), now.AddDays(-2), 1, e2Path),
                ]);

            var syncResult = await PostJsonAndReadAsync<CleanerrPlexSyncResult>(client, "/api/v1/cleanerr/sync-plex", new { });
            Assert.Equal(1, syncResult.ShowsSynced);
            Assert.Equal(0, syncResult.SeasonsSynced);
            Assert.Equal(0, syncResult.EpisodesSynced);

            async Task<IReadOnlyList<CleanerrMediaItem>> GetMediaAsync() =>
                (await client.GetFromJsonAsync<CleanerrMediaListResponse>("/api/v1/cleanerr/media?pageSize=50"))!.Items;

            var initialList = await GetMediaAsync();
            var andorShow = Assert.Single(initialList);
            Assert.Equal("2000", andorShow.PlexRatingKey);
            Assert.Equal("show", andorShow.MediaType);
            Assert.Equal(2, andorShow.ViewCount);

            // (a) media.play followed by media.resume on an episode maps to whole show and must NOT inflate view_count
            using (var playResp = await SendPlexMultipartWebhookAsync(
                client, "media.play", "2002", "admin",
                mediaType: "episode", grandparentRatingKey: "2000", parentIndex: 1, index: 1, grandparentTitle: "Andor"))
            {
                Assert.Equal(HttpStatusCode.OK, playResp.StatusCode);
            }
            var afterPlay = Assert.Single(await GetMediaAsync());
            Assert.Equal(2, afterPlay.ViewCount);
            Assert.Equal(1, afterPlay.ActiveStreamCount);

            using (var resumeResp = await SendPlexMultipartWebhookAsync(
                client, "media.resume", "2002", "admin",
                mediaType: "episode", grandparentRatingKey: "2000", parentIndex: 1, index: 1, grandparentTitle: "Andor"))
            {
                Assert.Equal(HttpStatusCode.OK, resumeResp.StatusCode);
            }
            var afterResume = Assert.Single(await GetMediaAsync());
            Assert.Equal(2, afterResume.ViewCount);
            Assert.Equal(1, afterResume.ActiveStreamCount);

            // media.scrobble increments whole show view_count by 1
            using (var scrobbleResp = await SendPlexMultipartWebhookAsync(
                client, "media.scrobble", "2002", "admin",
                mediaType: "episode", grandparentRatingKey: "2000", parentIndex: 1, index: 1, grandparentTitle: "Andor"))
            {
                Assert.Equal(HttpStatusCode.OK, scrobbleResp.StatusCode);
            }
            var afterScrobble = Assert.Single(await GetMediaAsync());
            Assert.Equal(3, afterScrobble.ViewCount);

            using (var resetStopResp = await SendPlexMultipartWebhookAsync(
                client, "media.stop", "2002", "admin",
                mediaType: "episode", grandparentRatingKey: "2000", parentIndex: 1, index: 1, grandparentTitle: "Andor"))
            {
                Assert.Equal(HttpStatusCode.OK, resetStopResp.StatusCode);
            }
            Assert.Equal(0, Assert.Single(await GetMediaAsync()).ActiveStreamCount);

            // (b) Concurrent episode active streams: E01 + E02 both active -> Show ActiveStreamCount == 2;
            //     stopping E01 keeps Show ActiveStreamCount == 1 (protected) until E02 stops -> 0.
            using (var playE1 = await SendPlexMultipartWebhookAsync(
                client, "media.play", "2002", "admin",
                mediaType: "episode", grandparentRatingKey: "2000", parentIndex: 1, index: 1, grandparentTitle: "Andor"))
            {
                Assert.Equal(HttpStatusCode.OK, playE1.StatusCode);
            }
            using (var playE2 = await SendPlexMultipartWebhookAsync(
                client, "media.play", "2003", "admin",
                mediaType: "episode", grandparentRatingKey: "2000", parentIndex: 1, index: 2, grandparentTitle: "Andor"))
            {
                Assert.Equal(HttpStatusCode.OK, playE2.StatusCode);
            }

            Assert.Equal(2, Assert.Single(await GetMediaAsync()).ActiveStreamCount);

            using (var stopE1 = await SendPlexMultipartWebhookAsync(
                client, "media.stop", "2002", "admin",
                mediaType: "episode", grandparentRatingKey: "2000", parentIndex: 1, index: 1, grandparentTitle: "Andor"))
            {
                Assert.Equal(HttpStatusCode.OK, stopE1.StatusCode);
            }
            Assert.Equal(1, Assert.Single(await GetMediaAsync()).ActiveStreamCount);

            using (var stopE2 = await SendPlexMultipartWebhookAsync(
                client, "media.stop", "2003", "admin",
                mediaType: "episode", grandparentRatingKey: "2000", parentIndex: 1, index: 2, grandparentTitle: "Andor"))
            {
                Assert.Equal(HttpStatusCode.OK, stopE2.StatusCode);
            }
            Assert.Equal(0, Assert.Single(await GetMediaAsync()).ActiveStreamCount);
        }
        finally
        {
            if (extStack is not null)
            {
                try { await extStack.DisposeAsync(); } catch { /* ignore */ }
            }
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private static void AssertZeroPlexSeasonAndCollectionRequests(EmulatedExternalStack extStack)
    {
        foreach (var req in extStack.PlexRequests)
        {
            Assert.DoesNotContain("type=3", req, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/library/collections", req, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("collection[", req, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task GenerateTinyMkvWithFfmpegAsync(string outputPath)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        string[] args =
        [
            "-y",
            "-f", "lavfi", "-i", "testsrc=duration=1:size=320x240:rate=1",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
            "-c:v", "libx264", "-preset", "ultrafast",
            "-c:a", "aac",
            "-metadata:s:a:0", "language=eng",
            outputPath,
        ];
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg process.");
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var stderr = await stderrTask;
        _ = await stdoutTask;

        Assert.True(proc.ExitCode == 0, $"ffmpeg failed (exit={proc.ExitCode}) for {outputPath}: {stderr}");
        Assert.True(File.Exists(outputPath));
        Assert.True(new FileInfo(outputPath).Length > 0);
    }

    private static async Task<HttpResponseMessage> SendPlexMultipartWebhookAsync(
        HttpClient client,
        string eventName,
        string ratingKey,
        string accountTitle,
        string? mediaType = null,
        string? grandparentRatingKey = null,
        int? parentIndex = null,
        int? index = null,
        string? grandparentTitle = null)
    {
        var metadata = new JsonObject { ["ratingKey"] = ratingKey };
        if (!string.IsNullOrWhiteSpace(mediaType)) metadata["type"] = mediaType;
        if (!string.IsNullOrWhiteSpace(grandparentRatingKey)) metadata["grandparentRatingKey"] = grandparentRatingKey;
        if (parentIndex.HasValue) metadata["parentIndex"] = parentIndex.Value;
        if (index.HasValue) metadata["index"] = index.Value;
        if (!string.IsNullOrWhiteSpace(grandparentTitle)) metadata["grandparentTitle"] = grandparentTitle;

        var payloadJson = new JsonObject
        {
            ["event"] = eventName,
            ["Account"] = new JsonObject { ["title"] = accountTitle },
            ["Metadata"] = metadata,
        }.ToJsonString();

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(payloadJson, Encoding.UTF8, "application/json"), "payload");
        return await client.PostAsync("/api/v1/cleanerr/webhooks/plex", form);
    }

    private static async Task<HttpResponseMessage> SendSignedDiscordInteractionAsync(
        HttpClient client,
        byte[] privateSeed32,
        JsonObject payload)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var rawBody = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var sigHex = CleanerrCrypto.SignDiscordEd25519Payload(privateSeed32, timestamp, rawBody);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cleanerr/webhooks/discord-interactions")
        {
            Content = new ByteArrayContent(rawBody),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.TryAddWithoutValidation("X-Signature-Ed25519", sigHex);
        req.Headers.TryAddWithoutValidation("X-Signature-Timestamp", timestamp);
        return await client.SendAsync(req);
    }

    private static async Task<T> PostJsonAndReadAsync<T>(
        HttpClient client,
        string url,
        object body,
        HttpStatusCode? expectedStatus = null)
    {
        using var resp = await client.PostAsJsonAsync(url, body);
        var raw = await resp.Content.ReadAsStringAsync();
        if (expectedStatus.HasValue)
        {
            Assert.True(resp.StatusCode == expectedStatus.Value, $"POST {url} expected {expectedStatus.Value}, got {resp.StatusCode}: {raw}");
        }
        else
        {
            Assert.True(resp.IsSuccessStatusCode, $"POST {url} failed ({resp.StatusCode}): {raw}");
        }

        return JsonSerializer.Deserialize<T>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static WebApplicationFactory<Program> CreateWebFactory(string tempDir, ISafeHttpClientFactory httpFactory) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                    ["Managerr:MasterKey"] = "phase5-cleanerr-e2e-emulation-key!",
                    ["Managerr:SchedulerEnabled"] = "false",
                });
            });
            b.ConfigureServices(services =>
            {
                services.RemoveAll<ISafeHttpClientFactory>();
                services.AddSingleton(httpFactory);
            });
        });

    private static async Task<HttpClient> CreateAdminClientAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IPlexUserService>();
        var auth = scope.ServiceProvider.GetRequiredService<IPlexAuthAndSessionService>();

        var user = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
            101, "uuid-101", "admin", "admin@managerr.local", null, true, true, true, ["1", "2"]));
        var key = await auth.CreateApiKeyAsync(
            user.Id, "test-admin-e2e", PermissionScopes.AllValidScopes.ToList(), null, null, null);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.RawTokenOnce);
        return client;
    }

    private sealed record CapturedDiscordRequest(
        string Method,
        Uri Uri,
        IReadOnlyDictionary<string, string> Headers,
        string Body);

    private sealed class LoopbackAndDiscordCapturingHttpClientFactory : ISafeHttpClientFactory
    {
        private readonly SocketsHttpHandler _socketsHandler = new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        private readonly InterceptingHandler _handler;
        private readonly ConcurrentQueue<CapturedDiscordRequest> _discordRequests = new();

        public LoopbackAndDiscordCapturingHttpClientFactory()
        {
            _handler = new InterceptingHandler(_socketsHandler, _discordRequests);
        }

        public IReadOnlyList<CapturedDiscordRequest> DiscordRequests => _discordRequests.ToList();

        public void ClearDiscordRequests() => _discordRequests.Clear();

        public HttpClient CreatePublicClient(bool ignoreSslErrors = false) => new(_handler, disposeHandler: false);

        public HttpClient CreateHomelabClient(bool allowPrivateLan = true, bool ignoreSslErrors = false) => new(_handler, disposeHandler: false);

        public void EnsureAddressPermitted(IPAddress address, string? targetHost, bool allowPrivateLan)
        {
        }

        private sealed class InterceptingHandler(
            HttpMessageHandler innerHandler,
            ConcurrentQueue<CapturedDiscordRequest> capturedDiscord) : DelegatingHandler(innerHandler)
        {
            private int _discordMsgSeq;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var host = request.RequestUri?.Host ?? "";
                if (host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) ||
                    host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase))
                {
                    var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                    var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
                    capturedDiscord.Enqueue(new CapturedDiscordRequest(request.Method.Method, request.RequestUri!, headers, body));

                    var msgId = Interlocked.Increment(ref _discordMsgSeq);
                    var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var chIdx = Array.IndexOf(segments, "channels");
                    var channelId = chIdx >= 0 && chIdx + 1 < segments.Length ? segments[chIdx + 1] : "0";

                    var json = JsonSerializer.Serialize(new
                    {
                        id = $"discord-emul-msg-{msgId}",
                        channel_id = channelId,
                    });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                }

                return await base.SendAsync(request, cancellationToken);
            }
        }
    }

    private sealed record SabJobState(string NzoId, string Name, string Category, string StoragePath, long Bytes)
    {
        public bool Removed { get; set; }
    }

    private sealed record PlexMovieSpec(
        string RatingKey,
        string Title,
        int Year,
        DateTimeOffset AddedAt,
        DateTimeOffset? LastViewedAt,
        int ViewCount,
        string FilePath);

    private sealed record PlexShowSpec(
        string RatingKey,
        string Title,
        int Year,
        DateTimeOffset AddedAt,
        DateTimeOffset? LastViewedAt,
        int ViewedLeafCount);

    private sealed record PlexSeasonSpec(
        string RatingKey,
        string ParentRatingKey,
        string ParentTitle,
        string Title,
        int Index,
        DateTimeOffset AddedAt,
        DateTimeOffset? LastViewedAt,
        int ViewedLeafCount);

    private sealed record PlexEpisodeSpec(
        string RatingKey,
        string ParentRatingKey,
        string GrandparentRatingKey,
        string ParentTitle,
        string GrandparentTitle,
        string Title,
        int Year,
        int SeasonNumber,
        int EpisodeNumber,
        DateTimeOffset AddedAt,
        DateTimeOffset? LastViewedAt,
        int ViewCount,
        string FilePath);

    private sealed class EmulatedExternalStack : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Lock _stateLock = new();

        private readonly HttpListener _sabListener;
        private readonly HttpListener _plexListener;
        private readonly HttpListener _seerrListener;

        private readonly Task _sabLoop;
        private readonly Task _plexLoop;
        private readonly Task _seerrLoop;

        private readonly List<SabJobState> _sabJobs = [];
        private readonly ConcurrentDictionary<string, byte> _removedSabJobIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> _plexRequests = new();
        private readonly ConcurrentQueue<string> _deletedSeerrPaths = new();

        private List<PlexMovieSpec> _plexMovies = [];
        private List<PlexShowSpec> _plexShows = [];
        private List<PlexSeasonSpec> _plexSeasons = [];
        private List<PlexEpisodeSpec> _plexEpisodes = [];
        private HashSet<string> _activeSessionKeys = new(StringComparer.Ordinal);

        private EmulatedExternalStack(
            int sabPort, HttpListener sabListener,
            int plexPort, HttpListener plexListener,
            int seerrPort, HttpListener seerrListener)
        {
            SabPort = sabPort;
            PlexPort = plexPort;
            SeerrPort = seerrPort;
            _sabListener = sabListener;
            _plexListener = plexListener;
            _seerrListener = seerrListener;

            _sabLoop = Task.Run(() => RunLoopAsync(_sabListener, HandleSabRequestAsync, _cts.Token));
            _plexLoop = Task.Run(() => RunLoopAsync(_plexListener, HandlePlexRequestAsync, _cts.Token));
            _seerrLoop = Task.Run(() => RunLoopAsync(_seerrListener, HandleSeerrRequestAsync, _cts.Token));
        }

        public int SabPort { get; }
        public int PlexPort { get; }
        public int SeerrPort { get; }

        public string PlexUrl => $"http://127.0.0.1:{PlexPort}";
        public string SeerrUrl => $"http://127.0.0.1:{SeerrPort}";

        public IReadOnlyCollection<string> RemovedSabJobIds => _removedSabJobIds.Keys.ToArray();
        public IReadOnlyCollection<string> PlexRequests => _plexRequests.ToArray();
        public IReadOnlyCollection<string> DeletedSeerrPaths => _deletedSeerrPaths.ToArray();

        public static Task<EmulatedExternalStack> StartAsync()
        {
            const int maxAttempts = 5;
            for (var attempt = 1; ; attempt++)
            {
                HttpListener? sabListener = null;
                HttpListener? plexListener = null;
                HttpListener? seerrListener = null;

                try
                {
                    var sabPort = GetFreeTcpPort();
                    sabListener = StartListener(sabPort);

                    var plexPort = GetFreeTcpPort();
                    plexListener = StartListener(plexPort);

                    var seerrPort = GetFreeTcpPort();
                    seerrListener = StartListener(seerrPort);

                    return Task.FromResult(new EmulatedExternalStack(
                        sabPort, sabListener,
                        plexPort, plexListener,
                        seerrPort, seerrListener));
                }
                catch (HttpListenerException) when (attempt < maxAttempts)
                {
                    CloseListenerQuietly(seerrListener);
                    CloseListenerQuietly(plexListener);
                    CloseListenerQuietly(sabListener);
                }
                catch
                {
                    CloseListenerQuietly(seerrListener);
                    CloseListenerQuietly(plexListener);
                    CloseListenerQuietly(sabListener);
                    throw;
                }
            }
        }

        public void AddSabJob(string nzoId, string name, string category, string storagePath, long bytes)
        {
            lock (_stateLock)
            {
                _sabJobs.Add(new SabJobState(nzoId, name, category, storagePath, bytes));
            }
        }

        public void SetPlexMovies(params PlexMovieSpec[] movies)
        {
            lock (_stateLock)
            {
                _plexMovies = movies.ToList();
            }
        }

        public void SetPlexTv(PlexShowSpec show, PlexSeasonSpec season, IReadOnlyList<PlexEpisodeSpec> episodes)
        {
            SetPlexTv([show], [season], episodes);
        }

        public void SetPlexTv(
            IReadOnlyList<PlexShowSpec> shows,
            IReadOnlyList<PlexSeasonSpec> seasons,
            IReadOnlyList<PlexEpisodeSpec> episodes)
        {
            lock (_stateLock)
            {
                _plexShows = shows.ToList();
                _plexSeasons = seasons.ToList();
                _plexEpisodes = episodes.ToList();
            }
        }

        public void SetActiveSessions(params string[] ratingKeys)
        {
            lock (_stateLock)
            {
                _activeSessionKeys = new HashSet<string>(ratingKeys, StringComparer.Ordinal);
            }
        }

        private static HttpListener StartListener(int port)
        {
            var listener = new HttpListener();
            try
            {
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
                return listener;
            }
            catch
            {
                CloseListenerQuietly(listener);
                throw;
            }
        }

        private static void CloseListenerQuietly(HttpListener? listener)
        {
            if (listener is null)
            {
                return;
            }

            try
            {
                if (listener.IsListening)
                {
                    listener.Stop();
                }
            }
            catch
            {
                // Ignore cleanup errors
            }

            try
            {
                listener.Close();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        private static int GetFreeTcpPort()
        {
            using var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            return ((IPEndPoint)tcp.LocalEndpoint).Port;
        }

        private static async Task RunLoopAsync(
            HttpListener listener,
            Func<HttpListenerContext, Task> handler,
            CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().WaitAsync(ct);
                }
                catch
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await handler(ctx);
                    }
                    catch
                    {
                        try
                        {
                            ctx.Response.StatusCode = 500;
                            ctx.Response.Close();
                        }
                        catch
                        {
                            // Ignore listener shutdown race
                        }
                    }
                }, ct);
            }
        }

        private async Task HandleSabRequestAsync(HttpListenerContext ctx)
        {
            var query = HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "");
            var mode = (query["mode"] ?? "").ToLowerInvariant();
            var name = (query["name"] ?? "").ToLowerInvariant();
            var value = query["value"] ?? "";

            string json;
            if (mode == "version")
            {
                json = """{"version":"4.3.2"}""";
            }
            else if (name == "delete" && (mode is "queue" or "history"))
            {
                lock (_stateLock)
                {
                    foreach (var j in _sabJobs.Where(j => string.Equals(j.NzoId, value, StringComparison.OrdinalIgnoreCase)))
                    {
                        j.Removed = true;
                    }
                }
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _removedSabJobIds.TryAdd(value, 0);
                }
                json = """{"status":true}""";
            }
            else if (mode == "queue")
            {
                json = """{"queue":{"slots":[]}}""";
            }
            else if (mode == "history")
            {
                List<SabJobState> activeHist;
                lock (_stateLock)
                {
                    activeHist = _sabJobs.Where(j => !j.Removed).ToList();
                }

                var slots = activeHist.Select(j => new
                {
                    nzo_id = j.NzoId,
                    name = j.Name,
                    category = j.Category,
                    status = "Completed",
                    bytes = j.Bytes,
                    storage = j.StoragePath,
                });
                json = JsonSerializer.Serialize(new { history = new { slots } });
            }
            else
            {
                json = """{"status":true}""";
            }

            await WriteJsonAsync(ctx.Response, 200, json);
        }

        private async Task HandlePlexRequestAsync(HttpListenerContext ctx)
        {
            var pathAndQuery = ctx.Request.Url?.PathAndQuery ?? "/";
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var query = HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "");
            _plexRequests.Enqueue($"{ctx.Request.HttpMethod} {pathAndQuery}");

            if (path == "/status/sessions")
            {
                List<object> activeMeta = [];
                lock (_stateLock)
                {
                    foreach (var rk in _activeSessionKeys)
                    {
                        var movie = _plexMovies.FirstOrDefault(m => m.RatingKey == rk);
                        if (movie is not null)
                        {
                            activeMeta.Add(new { ratingKey = movie.RatingKey, type = "movie", title = movie.Title });
                            continue;
                        }
                        var ep = _plexEpisodes.FirstOrDefault(e => e.RatingKey == rk);
                        if (ep is not null)
                        {
                            activeMeta.Add(new
                            {
                                ratingKey = ep.RatingKey,
                                parentRatingKey = ep.ParentRatingKey,
                                grandparentRatingKey = ep.GrandparentRatingKey,
                                type = "episode",
                                title = ep.Title,
                            });
                        }
                    }
                }

                var sessJson = JsonSerializer.Serialize(new
                {
                    MediaContainer = new
                    {
                        size = activeMeta.Count,
                        Metadata = activeMeta,
                    },
                });
                await WriteJsonAsync(ctx.Response, 200, sessJson);
                return;
            }

            if (path == "/library/sections")
            {
                var secJson = JsonSerializer.Serialize(new
                {
                    MediaContainer = new
                    {
                        Directory = new object[]
                        {
                            new { key = "1", type = "movie", title = "Movies" },
                            new { key = "2", type = "show", title = "TV" },
                        },
                    },
                });
                await WriteJsonAsync(ctx.Response, 200, secJson);
                return;
            }

            if (path == "/library/sections/1/all")
            {
                List<PlexMovieSpec> movies;
                lock (_stateLock)
                {
                    movies = _plexMovies.ToList();
                }

                var meta = movies.Select(BuildPlexMovieObject).ToArray();
                await WriteJsonAsync(ctx.Response, 200, JsonSerializer.Serialize(new { MediaContainer = new { Metadata = meta } }));
                return;
            }

            if (path == "/library/sections/2/all")
            {
                var type = query["type"] ?? "";
                object[] meta;
                lock (_stateLock)
                {
                    meta = type switch
                    {
                        "2" => _plexShows.Select(sh => (object)new
                        {
                            ratingKey = sh.RatingKey,
                            title = sh.Title,
                            year = sh.Year,
                            addedAt = sh.AddedAt.ToUnixTimeSeconds(),
                            lastViewedAt = sh.LastViewedAt?.ToUnixTimeSeconds() ?? 0,
                            viewedLeafCount = sh.ViewedLeafCount,
                            Genre = new[] { new { tag = "Sci-Fi" } },
                            Label = Array.Empty<object>(),
                        }).ToArray(),
                        "3" => _plexSeasons.Select(se => (object)new
                        {
                            ratingKey = se.RatingKey,
                            parentRatingKey = se.ParentRatingKey,
                            parentTitle = se.ParentTitle,
                            title = se.Title,
                            index = se.Index,
                            addedAt = se.AddedAt.ToUnixTimeSeconds(),
                            lastViewedAt = se.LastViewedAt?.ToUnixTimeSeconds() ?? 0,
                            viewedLeafCount = se.ViewedLeafCount,
                        }).ToArray(),
                        "4" => _plexEpisodes.Select(ep =>
                        {
                            var size = File.Exists(ep.FilePath) ? new FileInfo(ep.FilePath).Length : 1024L;
                            return (object)new
                            {
                                ratingKey = ep.RatingKey,
                                parentRatingKey = ep.ParentRatingKey,
                                grandparentRatingKey = ep.GrandparentRatingKey,
                                parentTitle = ep.ParentTitle,
                                grandparentTitle = ep.GrandparentTitle,
                                title = ep.Title,
                                year = ep.Year,
                                parentIndex = ep.SeasonNumber,
                                index = ep.EpisodeNumber,
                                addedAt = ep.AddedAt.ToUnixTimeSeconds(),
                                lastViewedAt = ep.LastViewedAt?.ToUnixTimeSeconds() ?? 0,
                                viewCount = ep.ViewCount,
                                Media = new[]
                                {
                                    new
                                    {
                                        videoResolution = "1080",
                                        Part = new[] { new { file = ep.FilePath, size } },
                                    },
                                },
                            };
                        }).ToArray(),
                        _ => [],
                    };
                }

                await WriteJsonAsync(ctx.Response, 200, JsonSerializer.Serialize(new { MediaContainer = new { Metadata = meta } }));
                return;
            }

            if (path.StartsWith("/library/metadata/", StringComparison.Ordinal))
            {
                var idsPart = path["/library/metadata/".Length..].Split('/')[0];
                var idSet = idsPart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.Ordinal);

                var matched = new List<object>();
                lock (_stateLock)
                {
                    foreach (var m in _plexMovies.Where(m => idSet.Contains(m.RatingKey)))
                    {
                        matched.Add(BuildPlexMovieObject(m));
                    }

                    foreach (var sh in _plexShows.Where(s => idSet.Contains(s.RatingKey)))
                    {
                        matched.Add(new
                        {
                            ratingKey = sh.RatingKey,
                            title = sh.Title,
                            Label = Array.Empty<object>(),
                        });
                    }
                }

                await WriteJsonAsync(ctx.Response, 200, JsonSerializer.Serialize(new { MediaContainer = new { Metadata = matched } }));
                return;
            }

            await WriteJsonAsync(ctx.Response, 200, "{}");
        }

        private static object BuildPlexMovieObject(PlexMovieSpec m)
        {
            var size = File.Exists(m.FilePath) ? new FileInfo(m.FilePath).Length : 1024L;
            return new
            {
                ratingKey = m.RatingKey,
                title = m.Title,
                year = m.Year,
                addedAt = m.AddedAt.ToUnixTimeSeconds(),
                lastViewedAt = m.LastViewedAt?.ToUnixTimeSeconds() ?? 0,
                viewCount = m.ViewCount,
                Media = new[]
                {
                    new
                    {
                        videoResolution = "1080",
                        Part = new[] { new { file = m.FilePath, size } },
                    },
                },
                Genre = new[] { new { tag = "Drama" } },
                Label = Array.Empty<object>(),
            };
        }

        private async Task HandleSeerrRequestAsync(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (string.Equals(ctx.Request.HttpMethod, "DELETE", StringComparison.OrdinalIgnoreCase) &&
                (path.StartsWith("/api/v1/request/", StringComparison.Ordinal) ||
                 path.StartsWith("/api/v1/media/", StringComparison.Ordinal)))
            {
                _deletedSeerrPaths.Enqueue(path);
                await WriteJsonAsync(ctx.Response, 200, "{}");
                return;
            }

            await WriteJsonAsync(ctx.Response, 200, "{}");
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { _sabListener.Stop(); _sabListener.Close(); } catch { /* ignore */ }
            try { _plexListener.Stop(); _plexListener.Close(); } catch { /* ignore */ }
            try { _seerrListener.Stop(); _seerrListener.Close(); } catch { /* ignore */ }

            try { await Task.WhenAll(_sabLoop, _plexLoop, _seerrLoop); } catch { /* ignore */ }
            _cts.Dispose();
        }
    }
}
