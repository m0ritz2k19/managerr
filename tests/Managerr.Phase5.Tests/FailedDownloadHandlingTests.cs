using Dapper;
using Managerr.Catalog;
using Managerr.Datastore;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
using Managerr.Host.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Managerr.Phase5.Tests;

public sealed class FailedDownloadHandlingTests
{
    private const string Ep34 = "Naruto.2002.S01E034.480p.NF.WEB-DL.H.264.AAC-UBWEB";
    private const string Ep35 = "Naruto.2002.S01E035.480p.NF.WEB-DL.H.264.AAC-UBWEB";

    [Fact]
    public async Task ClientFailedDownload_WhenClientRefusesRemoval_IsFailedOnceAndDoesNotStopTheRun()
    {
        await using var h = await Harness.StartAsync(removeFailed: true);
        h.Gateway.RemoveThrows = true;
        h.Gateway.QueueItems.Add(h.Item("bad-1", Ep34, "Failed"));
        h.Gateway.QueueItems.Add(h.Item("ok-2", Ep35, "Downloading"));

        var first = await h.Import.ProcessCompletedDownloadsAsync();

        Assert.Equal("ClientFailed", first.Items.Single(i => i.ExternalDownloadId == "bad-1").Verdict);
        var bad = await h.TrackedAsync("bad-1");
        Assert.Equal(TrackedDownloadStates.Failed, bad.State);
        Assert.Contains("Could not remove the download", bad.Messages);
        Assert.Contains("403", bad.Messages);
        Assert.Equal("ClientFailed", await h.LedgerVerdictAsync("bad-1"));
        // The item after the failed one is still processed
        Assert.Equal(TrackedDownloadStates.Downloading, (await h.TrackedAsync("ok-2")).State);

        // The next polls leave the settled item alone: no second blocklist, removal or replacement search
        await h.Import.ProcessCompletedDownloadsAsync();
        await h.Import.ProcessCompletedDownloadsAsync();
        Assert.Equal(1, h.Gateway.RemoveCalls);
        Assert.Equal(1, await h.CountAsync("SELECT COUNT(*) FROM recurring_blocklist_hashes"));
        Assert.Equal(TrackedDownloadStates.Failed, (await h.TrackedAsync("bad-1")).State);
    }

    [Fact]
    public async Task MalwareDownload_WhenClientRefusesRemoval_EndsFailedInsteadOfImportBlocked()
    {
        await using var h = await Harness.StartAsync(removeFailed: false);
        h.Gateway.RemoveThrows = true;
        var jobDir = Path.Combine(h.TempDir, "downloads", "Naruto.S01E034");
        Directory.CreateDirectory(jobDir);
        var files = new[] { Path.Combine(jobDir, "Naruto.S01E034.mkv"), Path.Combine(jobDir, "Naruto.S01E034.exe") };
        foreach (var f in files) await File.WriteAllTextAsync(f, "x");
        h.Gateway.QueueItems.Add(h.Item("mal-1", Ep34, "Completed", outputPath: jobDir, files: files));

        var first = await h.Import.ProcessCompletedDownloadsAsync();

        Assert.Equal("BlockedMalware", first.Items.Single().Verdict);
        var mal = await h.TrackedAsync("mal-1");
        Assert.Equal(TrackedDownloadStates.Failed, mal.State);
        Assert.Contains("Could not remove the download", mal.Messages);
        Assert.Equal("BlockedMalware", await h.LedgerVerdictAsync("mal-1"));

        await h.Import.ProcessCompletedDownloadsAsync();
        var sweep = await h.Orchestrator.EvaluateQueueHealthAndStrikesAsync(3);

        Assert.Empty(sweep);
        Assert.Equal(1, h.Gateway.RemoveCalls);
        Assert.Equal(1, await h.CountAsync("SELECT COUNT(*) FROM recurring_blocklist_hashes"));
    }

    [Theory]
    [InlineData(false, "failed_after_3_strikes", 0)]
    [InlineData(true, "removed_after_3_strikes", 1)]
    public async Task StrikeSweep_ActsOnAStalledDownloadOnce(bool removeFailed, string expectedAction, int expectedRemovals)
    {
        await using var h = await Harness.StartAsync(removeFailed);
        h.Gateway.QueueItems.Add(h.Item("stall-1", Ep34, "stalledDL", stalled: true));

        var all = new List<QueueSweepAction>();
        for (var sweep = 1; sweep <= 6; sweep++)
        {
            var actions = await h.Orchestrator.EvaluateQueueHealthAndStrikesAsync(3);
            if (sweep > 3) Assert.Empty(actions);
            all.AddRange(actions);
        }

        var fired = Assert.Single(all, a => a.AddedToRecurringBlocklist);
        Assert.Equal(expectedAction, fired.ActionTaken);
        Assert.Equal(3, fired.StrikeCount);
        Assert.Equal(expectedRemovals, h.Gateway.RemoveCalls);
        Assert.Equal(1, await h.CountAsync("SELECT COUNT(*) FROM media_history WHERE event_type = 'DownloadFailed'"));
        Assert.Equal(TrackedDownloadStates.Failed, (await h.TrackedAsync("stall-1")).State);
    }

    [Fact]
    public async Task StrikeSweep_WhenClientRefusesRemoval_ReportsNotRemovedAndKeepsSweeping()
    {
        await using var h = await Harness.StartAsync(removeFailed: true);
        h.Gateway.RemoveThrows = true;
        h.Gateway.QueueItems.Add(h.Item("stall-1", Ep34, "stalledDL", stalled: true));
        h.Gateway.QueueItems.Add(h.Item("stall-2", Ep35, "stalledDL", stalled: true));

        await h.Orchestrator.EvaluateQueueHealthAndStrikesAsync(3);
        await h.Orchestrator.EvaluateQueueHealthAndStrikesAsync(3);
        var third = await h.Orchestrator.EvaluateQueueHealthAndStrikesAsync(3);

        Assert.Equal(2, third.Count);
        Assert.All(third, a =>
        {
            Assert.Equal("failed_after_3_strikes", a.ActionTaken);
            Assert.True(a.AddedToRecurringBlocklist);
        });
        Assert.Contains("removal from the download client failed", (await h.TrackedAsync("stall-1")).Messages);
    }

    [Fact]
    public async Task ErroredTorrent_FailedByImport_IsNotStruckAgainBySweep()
    {
        await using var h = await Harness.StartAsync(removeFailed: false);
        h.Gateway.QueueItems.Add(h.Item("err-1", Ep34, "error", error: "error"));

        for (var round = 1; round <= 4; round++)
        {
            var report = await h.Import.ProcessCompletedDownloadsAsync();
            var sweep = await h.Orchestrator.EvaluateQueueHealthAndStrikesAsync(3);

            if (round == 1) Assert.Equal("ClientFailed", report.Items.Single().Verdict);
            Assert.Empty(sweep);
        }

        Assert.Equal(0, await h.CountAsync("SELECT COUNT(*) FROM download_queue_strikes"));
        Assert.Equal(1, await h.CountAsync("SELECT COUNT(*) FROM recurring_blocklist_hashes"));
        Assert.Equal(TrackedDownloadStates.Failed, (await h.TrackedAsync("err-1")).State);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private WebApplicationFactory<Program> _factory = null!;
        private IServiceScope _scope = null!;
        private ISqliteDbFactory _db = null!;

        public string TempDir { get; } = Path.Combine(Path.GetTempPath(), $"managerr-failed-dl-{Guid.NewGuid():N}");
        public FakeGateway Gateway { get; } = new();
        public ICompletedDownloadImportService Import { get; private set; } = null!;
        public IUnifiedDownloadOrchestrator Orchestrator { get; private set; } = null!;
        public DownloadClientSummary Client { get; private set; } = null!;

        public static async Task<Harness> StartAsync(bool removeFailed)
        {
            var h = new Harness();
            Directory.CreateDirectory(Path.Combine(h.TempDir, "storage", "media", "tv"));
            h._factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = Path.Combine(h.TempDir, "config"),
                    ["Managerr:MasterKey"] = "failed-download-test-master-key-32b",
                    ["Managerr:SchedulerEnabled"] = "false",
                }));
                b.ConfigureServices(s =>
                {
                    s.RemoveAll<IDownloadClientDriverGateway>();
                    s.AddSingleton<IDownloadClientDriverGateway>(h.Gateway);
                });
            });
            h._scope = h._factory.Services.CreateScope();
            var sp = h._scope.ServiceProvider;
            h.Import = sp.GetRequiredService<ICompletedDownloadImportService>();
            h.Orchestrator = sp.GetRequiredService<IUnifiedDownloadOrchestrator>();
            h._db = sp.GetRequiredService<ISqliteDbFactory>();
            await sp.GetRequiredService<IPolymorphicCatalogService>().CreateLibraryAsync(new CreateLibraryRequest(
                Name: "TV Shows", MediaType: "tv", RootFolderPath: Path.Combine(h.TempDir, "storage", "media", "tv")));
            h.Client = await h.Orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "qBit", Implementation: "qbittorrent", Host: "qbit", Port: 8080, RemoveFailed: removeFailed));
            return h;
        }

        public ActiveDownloadQueueItem Item(
            string id,
            string title,
            string status,
            bool stalled = false,
            string? error = null,
            string outputPath = "",
            IReadOnlyList<string>? files = null) =>
            new(Client.Id, Client.Name, "torrent", id, id.PadRight(40, '0'), title, "tv-managerr", "tv", status,
                1_000_000, status == "Downloading" ? 500_000 : 0, outputPath, files ?? [], stalled, false, error);

        public async Task<(string State, string Messages)> TrackedAsync(string externalId)
        {
            await using var c = await _db.OpenReadConnectionAsync(DatabaseKind.Core);
            return await c.QuerySingleAsync<(string, string)>(
                "SELECT state, status_messages_json FROM tracked_downloads WHERE external_download_id = @Id",
                new { Id = externalId });
        }

        public async Task<string?> LedgerVerdictAsync(string externalId)
        {
            await using var c = await _db.OpenReadConnectionAsync(DatabaseKind.Core);
            return await c.QuerySingleOrDefaultAsync<string>(
                "SELECT verdict FROM processed_download_imports WHERE external_download_id = @Id",
                new { Id = externalId });
        }

        public async Task<int> CountAsync(string sql)
        {
            await using var c = await _db.OpenReadConnectionAsync(DatabaseKind.Core);
            return await c.ExecuteScalarAsync<int>(sql);
        }

        public async ValueTask DisposeAsync()
        {
            _scope.Dispose();
            await _factory.DisposeAsync();
            try { Directory.Delete(TempDir, true); } catch { }
        }
    }

    private sealed class FakeGateway : IDownloadClientDriverGateway
    {
        public List<ActiveDownloadQueueItem> QueueItems { get; } = [];
        public bool RemoveThrows { get; set; }
        public int RemoveCalls { get; private set; }

        public IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers() => DownloadClientCatalog.All;

        public Task<string> EnqueueReleaseAsync(DownloadClientConnectionSpec client, string releaseTitle, string downloadUrlOrMagnet, string resolvedCategory, CancellationToken ct = default)
            => Task.FromResult($"q-{Guid.NewGuid():N}");

        public Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(DownloadClientConnectionSpec client, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ActiveDownloadQueueItem>>(QueueItems.Where(q => q.ClientId == client.Id).ToList());

        public Task RemoveDownloadAsync(DownloadClientConnectionSpec client, string externalId, bool deleteData, CancellationToken ct = default)
        {
            RemoveCalls++;
            if (RemoveThrows)
            {
                throw new HttpRequestException("Response status code does not indicate success: 403 (Forbidden).");
            }

            QueueItems.RemoveAll(q => q.ExternalId == externalId);
            return Task.CompletedTask;
        }
    }
}
