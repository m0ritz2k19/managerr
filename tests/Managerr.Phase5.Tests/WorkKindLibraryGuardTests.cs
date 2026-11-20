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

public sealed class WorkKindLibraryGuardTests
{
    private const string EpubRelease = "Brandon.Sanderson.-.Wind.and.Truth.2024.German.EPUB-TESTLAB";

    [Theory]
    [InlineData("movies", "movies", true)]
    [InlineData("tv", "anime", true)]
    [InlineData("tv", "series", true)]
    [InlineData("ebooks", "audiobooks", true)]
    [InlineData("ebooks", "comics", true)]
    [InlineData("movies", "ebooks", false)]
    [InlineData("tv", "movies", false)]
    [InlineData("music", "ebooks", false)]
    public void LibraryAcceptsWorkKind_GroupsCompatibleKinds(string libraryType, string workKind, bool expected)
    {
        Assert.Equal(expected, PolymorphicCatalogService.LibraryAcceptsWorkKind(libraryType, workKind));
    }

    [Fact]
    public async Task EpubUnderMoviesCategory_BecomesEbookWorkInBooksLibrary_OrIsBlockedWithoutOne()
    {
        // Without a books library the import is blocked instead of landing in Movies
        var blocked = await RunEpubImportAsync(withBooksLibrary: false);
        Assert.Empty(blocked.Works);
        var tracked = Assert.Single(blocked.Tracked);
        Assert.Equal(TrackedDownloadStates.ImportBlocked, tracked.State);
        Assert.Contains("No library for 'ebooks' is configured", tracked.StatusMessages.Single());

        // With one, the auto-created work is an ebook in the books library (never a movie)
        var routed = await RunEpubImportAsync(withBooksLibrary: true);
        var work = Assert.Single(routed.Works);
        Assert.Equal("ebooks", work.WorkKind);
        Assert.Equal(routed.BooksLibraryId, work.LibraryId);
    }

    [Fact]
    public async Task AddingABookToAMovieLibrary_IsRejected()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateFactory(tempDir, gateway: null);
            using var scope = factory.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var movies = await catalog.CreateLibraryAsync(new CreateLibraryRequest("Movies", "movies", Path.Combine(tempDir, "movies")));

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => catalog.AddWorkAsync(
                new CreateMediaWorkRequest(movies.Id, "Wind and Truth", WorkKind: "ebooks", Year: 2024)));
            Assert.Contains("does not belong in library 'Movies'", ex.Message);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private static async Task<(IReadOnlyList<MediaWorkSummary> Works, IReadOnlyList<TrackedDownloadDto> Tracked, long? BooksLibraryId)> RunEpubImportAsync(bool withBooksLibrary)
    {
        var tempDir = CreateTempDir();
        try
        {
            var jobDir = Path.Combine(tempDir, "storage", "usenet", "complete", "movies", EpubRelease);
            Directory.CreateDirectory(jobDir);
            WriteMinimalEpub(Path.Combine(jobDir, "Brandon Sanderson - Wind and Truth.epub"));

            await using var factory = CreateFactory(tempDir, new EpubJobGateway(jobDir));
            using var scope = factory.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IPolymorphicCatalogService>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IUnifiedDownloadOrchestrator>();
            var importSvc = scope.ServiceProvider.GetRequiredService<ICompletedDownloadImportService>();

            // Movies is created first, so the old "first library" fallback would pick it
            await catalog.CreateLibraryAsync(new CreateLibraryRequest("Test Movies", "movies", Path.Combine(tempDir, "storage", "media", "movies")));
            long? booksId = withBooksLibrary
                ? (await catalog.CreateLibraryAsync(new CreateLibraryRequest("Test E-Books", "ebooks", Path.Combine(tempDir, "storage", "media", "books")))).Id
                : null;
            await orchestrator.CreateClientAsync(new UpsertDownloadClientRequest(
                Name: "SAB", Implementation: "sabnzbd", Host: "sab-test", Port: 8080, ApiKey: "k"));

            await importSvc.ProcessCompletedDownloadsAsync();
            return (await catalog.ListWorksAsync(), await importSvc.ListTrackedDownloadsAsync(), booksId);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(string tempDir, IDownloadClientDriverGateway? gateway) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = Path.Combine(tempDir, "config"),
                    ["Managerr:MasterKey"] = "work-kind-guard-test-master-key-32by",
                    ["Managerr:SchedulerEnabled"] = "false",
                });
            });
            if (gateway is not null)
            {
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<IDownloadClientDriverGateway>();
                    services.AddSingleton(gateway);
                });
            }
        });

    private static void WriteMinimalEpub(string path)
    {
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
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

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"managerr-work-kind-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
    }

    // A completed SABnzbd job whose category (and so vertical hint) is 'movies', like the testlab TC-U8 grab
    private sealed class EpubJobGateway(string jobDir) : IDownloadClientDriverGateway
    {
        public IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers() => DownloadClientCatalog.All;

        public Task<string> EnqueueReleaseAsync(DownloadClientConnectionSpec client, string releaseTitle, string downloadUrlOrMagnet, string resolvedCategory, CancellationToken ct = default) =>
            Task.FromResult("nzo-epub");

        public Task<IReadOnlyList<ActiveDownloadQueueItem>> FetchQueueAsync(DownloadClientConnectionSpec client, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActiveDownloadQueueItem>>(
            [
                new(
                    ClientId: client.Id,
                    ClientName: client.Name,
                    Protocol: "usenet",
                    ExternalId: "nzo-epub",
                    InfoHash: "nzo:nzo-epub",
                    Title: EpubRelease,
                    Category: "movies-managerr",
                    MediaVertical: "movies",
                    Status: "Completed",
                    TotalBytes: 4096,
                    RemainingBytes: 0,
                    OutputPath: jobDir,
                    ContainedFiles: [],
                    IsStalled: false,
                    IsMetadataStuck: false,
                    ErrorMessage: null),
            ]);

        public Task RemoveDownloadAsync(DownloadClientConnectionSpec client, string externalId, bool deleteData, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
