using Managerr.Catalog;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
using Managerr.Host.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Managerr.Phase5.Tests;

public sealed class TrackedDownloadReconciliationTests
{
    [Fact]
    public async Task VanishedDownload_OnReachableClient_IsMarkedRemoved_ButUnreachableClientKeepsState()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-vanished-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "storage", "media", "tv"));

        try
        {
            var gateway = new FlakyDriverGateway();

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                        ["Managerr:MasterKey"] = "vanished-download-test-master-key-32b",
                        ["Managerr:SchedulerEnabled"] = "false",
                        ["Managerr:TrackedDownloadVanishedGraceSeconds"] = "0",
                    });
                });
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<IDownloadClientDriverGateway>();
                    services.AddSingleton<IDownloadClientDriverGateway>(gateway);
                });
            });

            using var scope = factory.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var importSvc = scope.ServiceProvider.GetRequiredService<ICompletedDownloadImportService>();

            await catalog.CreateLibraryAsync(new CreateLibraryRequest(
                Name: "TV Shows",
                MediaType: "tv",
                RootFolderPath: Path.Combine(tempDir, "storage", "media", "tv")));

            var reachable = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SAB Reachable", Implementation: "sabnzbd", Host: "sab-a", Port: 8080, ApiKey: "a"));
            var flaky = await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SAB Offline", Implementation: "sabnzbd", Host: "sab-b", Port: 8080, ApiKey: "b"));

            gateway.QueueItems.AddRange(
            [
                Downloading(reachable, "gone-1", "Naruto.2002.S01E034.480p.NF.WEB-DL.H.264.AAC-UBWEB"),
                Downloading(reachable, "still-here", "Naruto.2002.S01E035.480p.NF.WEB-DL.H.264.AAC-UBWEB"),
                Downloading(flaky, "offline-1", "Naruto.2002.S01E036.480p.NF.WEB-DL.H.264.AAC-UBWEB"),
            ]);

            await importSvc.ProcessCompletedDownloadsAsync();
            var tracked = await importSvc.ListTrackedDownloadsAsync();
            Assert.All(tracked, t => Assert.Equal(TrackedDownloadStates.Downloading, t.State));

            // gone-1 is deleted from SABnzbd, the second client goes offline and returns nothing
            gateway.QueueItems.RemoveAll(q => q.ExternalId is "gone-1" or "offline-1");
            gateway.UnreachableClientIds.Add(flaky.Id);

            var report = await importSvc.ProcessCompletedDownloadsAsync();
            tracked = await importSvc.ListTrackedDownloadsAsync();

            Assert.Equal(1, report.RemovedCount);

            var gone = tracked.Single(t => t.ExternalDownloadId == "gone-1");
            Assert.Equal(TrackedDownloadStates.Removed, gone.State);
            Assert.Equal("warning", gone.TrackedStatus);
            Assert.Contains(gone.StatusMessages, m => m.Contains("SAB Reachable", StringComparison.Ordinal));

            Assert.Equal(TrackedDownloadStates.Downloading, tracked.Single(t => t.ExternalDownloadId == "still-here").State);
            Assert.Equal(TrackedDownloadStates.Downloading, tracked.Single(t => t.ExternalDownloadId == "offline-1").State);

            // Reappearing in the client resumes normal tracking
            gateway.QueueItems.Add(Downloading(reachable, "gone-1", "Naruto.2002.S01E034.480p.NF.WEB-DL.H.264.AAC-UBWEB"));
            await importSvc.ProcessCompletedDownloadsAsync();
            tracked = await importSvc.ListTrackedDownloadsAsync();
            Assert.Equal(TrackedDownloadStates.Downloading, tracked.Single(t => t.ExternalDownloadId == "gone-1").State);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private static ActiveDownloadQueueItem Downloading(DownloadClientSummary client, string externalId, string title) =>
        new(
            ClientId: client.Id,
            ClientName: client.Name,
            Protocol: "usenet",
            ExternalId: externalId,
            InfoHash: $"nzo:{externalId}",
            Title: title,
            Category: "tv-managerr",
            MediaVertical: "tv",
            Status: "Downloading",
            TotalBytes: 1_000_000,
            RemainingBytes: 500_000,
            OutputPath: "",
            ContainedFiles: [],
            IsStalled: false,
            IsMetadataStuck: false,
            ErrorMessage: null);

    private sealed class FlakyDriverGateway : IDownloadClientDriverGateway
    {
        public List<ActiveDownloadQueueItem> QueueItems { get; } = [];
        public HashSet<long> UnreachableClientIds { get; } = [];

        public IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers() => DownloadClientCatalog.All;

        public Task<string> EnqueueReleaseAsync(
            DownloadClientConnectionSpec client,
            string releaseTitle,
            string downloadUrlOrMagnet,
            string resolvedCategory,
            CancellationToken ct = default) => Task.FromResult($"nzo-{Guid.NewGuid():N}");

        public Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(
            DownloadClientConnectionSpec client,
            CancellationToken ct = default)
        {
            if (UnreachableClientIds.Contains(client.Id))
            {
                throw new HttpRequestException($"Connection refused ({client.Host})");
            }

            return Task.FromResult<IReadOnlyList<ActiveDownloadQueueItem>>(
                QueueItems.Where(q => q.ClientId == client.Id).ToList());
        }

        public Task RemoveDownloadAsync(
            DownloadClientConnectionSpec client,
            string externalId,
            bool deleteData,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
