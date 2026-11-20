using System.IO.Compression;
using Managerr.Catalog;
using Managerr.Downloaders;
using Managerr.Downloaders.Clients;
using Managerr.Host.Services;
using Managerr.Verification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Managerr.Phase5.Tests;

public sealed class CorruptDownloadIsolationTests
{
    private const string GoodRelease = "Brandon.Sanderson.-.Wind.and.Truth.2024.German.EPUB-TESTLAB";
    private const string CorruptRelease = "Brandon.Sanderson.-.Mistborn.2006.German.EPUB-BROKEN";

    [Fact]
    public async Task UnreadableEpub_IsAFailedIntegrityCheck_NotAnException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"managerr-corrupt-{Guid.NewGuid():N}.epub");
        await File.WriteAllBytesAsync(path, new byte[4096]);
        try
        {
            var probe = await new DefaultContainerProbeAdapter().ProbeAsync(path);

            Assert.False(probe.IntegrityPassed);
            Assert.Equal("corrupt_container", probe.ContainerFormat);
            Assert.StartsWith("File is unreadable as EPUB", probe.IntegrityFailureReason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CorruptEpubInTheSamePoll_FailsAlone_WhileTheGoodDownloadImports()
    {
        var run = await RunPollAsync(throwingProbeFor: null);

        Assert.Equal(VerificationVerdicts.VerifiedPass, run.Report.Items.Single(i => i.ExternalDownloadId == "nzo-good").Verdict);
        Assert.Equal(VerificationVerdicts.VerifiedFail, run.Report.Items.Single(i => i.ExternalDownloadId == "nzo-corrupt").Verdict);
        Assert.Contains(run.Works, w => w.Title.Contains("Wind and Truth", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnexpectedExceptionForOneDownload_BlocksOnlyThatDownload()
    {
        var run = await RunPollAsync(throwingProbeFor: CorruptRelease);

        Assert.Equal(VerificationVerdicts.VerifiedPass, run.Report.Items.Single(i => i.ExternalDownloadId == "nzo-good").Verdict);
        var broken = run.Tracked.Single(t => t.ExternalDownloadId == "nzo-corrupt");
        Assert.Equal(TrackedDownloadStates.ImportBlocked, broken.State);
        Assert.Equal("error", broken.TrackedStatus);
        Assert.StartsWith("Import failed unexpectedly: InvalidOperationException: probe exploded", broken.StatusMessages.Single());
    }

    private static async Task<(ProcessCompletedDownloadsReport Report, IReadOnlyList<MediaWorkSummary> Works, IReadOnlyList<TrackedDownloadDto> Tracked)> RunPollAsync(string? throwingProbeFor)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-corrupt-poll-{Guid.NewGuid():N}");
        try
        {
            // Corrupt job first, so a crash on it would have prevented the good one from being reached
            var corruptDir = Path.Combine(tempDir, "storage", "usenet", "complete", "ebooks", CorruptRelease);
            var goodDir = Path.Combine(tempDir, "storage", "usenet", "complete", "ebooks", GoodRelease);
            Directory.CreateDirectory(corruptDir);
            Directory.CreateDirectory(goodDir);
            await File.WriteAllBytesAsync(Path.Combine(corruptDir, CorruptRelease + ".epub"), new byte[4096]);
            WriteMinimalEpub(Path.Combine(goodDir, GoodRelease + ".epub"));

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                        ["Managerr:MasterKey"] = "corrupt-poll-test-master-key-32bytes",
                        ["Managerr:SchedulerEnabled"] = "false",
                    });
                });
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<IDownloadClientDriverGateway>();
                    services.AddSingleton<IDownloadClientDriverGateway>(new TwoJobGateway(corruptDir, goodDir));
                    if (throwingProbeFor is not null)
                    {
                        services.RemoveAll<IContainerProbeAdapter>();
                        services.AddSingleton<IContainerProbeAdapter>(new ThrowingProbe(throwingProbeFor));
                    }
                });
            });

            using var scope = factory.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var importSvc = scope.ServiceProvider.GetRequiredService<ICompletedDownloadImportService>();

            await catalog.CreateLibraryAsync(new CreateLibraryRequest("Books", "ebooks", Path.Combine(tempDir, "storage", "media", "books")));
            await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SAB", Implementation: "sabnzbd", Host: "sab-test", Port: 8080, ApiKey: "k"));

            var report = await importSvc.ProcessCompletedDownloadsAsync();
            return (report, await catalog.ListWorksAsync(), await importSvc.ListTrackedDownloadsAsync());
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private static void WriteMinimalEpub(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(content);
        }

        Add("mimetype", "application/epub+zip");
        Add("META-INF/container.xml",
            """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""");
        Add("OEBPS/content.opf",
            """<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Wind and Truth</dc:title><dc:language>de</dc:language></metadata><manifest><item id="c1" href="c1.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="c1"/></spine></package>""");
        Add("OEBPS/c1.xhtml",
            """<html xmlns="http://www.w3.org/1999/xhtml"><body><p>Der Sturm zog über das Land, und die Menschen suchten Schutz in ihren Häusern. Es war eine lange und dunkle Nacht.</p></body></html>""");
    }

    private sealed class ThrowingProbe(string releaseMarker) : IContainerProbeAdapter
    {
        private readonly DefaultContainerProbeAdapter _inner = new();

        public Task<ContainerProbeResult> ProbeAsync(string filePath, double? expectedRuntimeSec = null, CancellationToken ct = default) =>
            filePath.Contains(releaseMarker, StringComparison.Ordinal)
                ? throw new InvalidOperationException("probe exploded")
                : _inner.ProbeAsync(filePath, expectedRuntimeSec, ct);
    }

    private sealed class TwoJobGateway(string corruptDir, string goodDir) : IDownloadClientDriverGateway
    {
        public IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers() => DownloadClientCatalog.All;

        public Task<string> EnqueueReleaseAsync(DownloadClientConnectionSpec client, string releaseTitle, string downloadUrlOrMagnet, string resolvedCategory, CancellationToken ct = default) =>
            Task.FromResult("nzo-x");

        public Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(DownloadClientConnectionSpec client, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActiveDownloadQueueItem>>(
            [
                Job(client, "nzo-corrupt", CorruptRelease, corruptDir),
                Job(client, "nzo-good", GoodRelease, goodDir),
            ]);

        public Task RemoveDownloadAsync(DownloadClientConnectionSpec client, string externalId, bool deleteData, CancellationToken ct = default) =>
            Task.CompletedTask;

        private static ActiveDownloadQueueItem Job(DownloadClientConnectionSpec client, string id, string title, string dir) =>
            new(
                ClientId: client.Id,
                ClientName: client.Name,
                Protocol: "usenet",
                ExternalId: id,
                InfoHash: $"nzo:{id}",
                Title: title,
                Category: "ebooks-managerr",
                MediaVertical: "ebooks",
                Status: "Completed",
                TotalBytes: 4096,
                RemainingBytes: 0,
                OutputPath: dir,
                ContainedFiles: [],
                IsStalled: false,
                IsMetadataStuck: false,
                ErrorMessage: null);
    }
}
