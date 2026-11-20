using System.Net;
using System.Security.Cryptography;
using Managerr.Common.Crypto;
using Managerr.Common.Net;
using Managerr.Common.Paths;
using Managerr.Datastore;
using Managerr.Datastore.Migrations;
using Managerr.Datastore.Services;
using Managerr.Host.Hubs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Managerr.Phase1.Tests;

public sealed class SqliteAndCryptoFoundationTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"managerr-p1-unit-{Guid.NewGuid():N}");
    private AppPaths _paths = null!;
    private SqliteDbFactory _dbFactory = null!;

    public async Task InitializeAsync()
    {
        _paths = new AppPaths(_tempDir);
        _dbFactory = new SqliteDbFactory(_paths);
        var migrator = new SchemaMigrator(_dbFactory);
        await migrator.MigrateAllAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbFactory.DisposeAsync();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore temp cleanup */ }
        }
    }

    [Fact]
    public async Task EmbeddedSqlite_ThreeDatabaseLayout_RunsInWalModeWithZeroBusyErrors()
    {
        var stats = await _dbFactory.GetStorageStatsAsync();
        Assert.Equal(3, stats.Count);
        Assert.All(stats, s =>
        {
            Assert.Equal("wal", s.JournalMode.ToLowerInvariant());
            Assert.True(s.SchemaVersion >= 1);
            Assert.True(File.Exists(s.FilePath));
        });

        var protector = new EnvelopeSecretProtector(_paths);
        var settings = new AppSettingsService(_dbFactory, protector);
        var audit = new AuditLogService(_dbFactory);

        // Hammer concurrent writes across Core and Telemetry SQLite databases
        var tasks = Enumerable.Range(0, 30).Select(async i =>
        {
            await settings.SetAsync($"key.{i}", $"secret-value-{i}", isSecret: i % 2 == 0);
            await audit.RecordAsync(new AuditWriteRequest(
                Action: "test.concurrent_write",
                ResourceType: "setting",
                ResourceId: i.ToString(),
                Details: new { Index = i }));
        });

        await Task.WhenAll(tasks);

        Assert.Equal("secret-value-4", await settings.GetSecretAsync("key.4"));
        var masked = await settings.GetMaskedSecretAsync("key.4");
        Assert.True(masked.IsSet);
        Assert.DoesNotContain("secret-value-4", masked.MaskedHint ?? "");

        var recentAudit = await audit.ListRecentAsync(50, "test.concurrent_write");
        Assert.Equal(30, recentAudit.Count);
    }

    [Fact]
    public async Task Fts5Search_And_HybridSqliteCache_And_VacuumIntoHotBackup_WorkEndToEnd()
    {
        var fts = new FtsSearchService(_dbFactory);
        await fts.IndexItemAsync("movie", "Dune: Part Two", "Dune Deuxième Partie", 2024, "tmdb:693134 imdb:tt15239678");
        await fts.IndexItemAsync("series", "Severance", "Lumon", 2022, "tvdb:371980");

        var movieHits = await fts.SearchAsync("dun part", "movie");
        Assert.Single(movieHits);
        Assert.Equal("Dune: Part Two", movieHits[0].Title);

        var idHits = await fts.SearchAsync("tt15239678");
        Assert.Single(idHits);

        // Verify L1 + L2 cache persistence across separate MemoryCache instances (zero Redis)
        using (var l1First = new MemoryCache(new MemoryCacheOptions()))
        {
            var cache1 = new HybridSqliteCache(_dbFactory, l1First);
            await cache1.SetAsync("meta:tmdb:693134", new { Title = "Dune: Part Two", Rating = 8.6 }, TimeSpan.FromMinutes(10));
        }

        using (var l1Second = new MemoryCache(new MemoryCacheOptions()))
        {
            var cache2 = new HybridSqliteCache(_dbFactory, l1Second);
            var hit = await cache2.GetAsync<Dictionary<string, object>>("meta:tmdb:693134");
            Assert.NotNull(hit);
            Assert.True(hit.ContainsKey("title"));
        }

        // Verify online VACUUM INTO hot backup and retention pruning
        var backupSvc = new SqliteBackupService(_dbFactory, _paths);
        var b1 = await backupSvc.CreateHotBackupAsync("pre-migration", maxRetainedBackups: 2);
        await Task.Delay(5);
        var b2 = await backupSvc.CreateHotBackupAsync("daily", maxRetainedBackups: 2);
        await Task.Delay(5);
        var b3 = await backupSvc.CreateHotBackupAsync("post-sync", maxRetainedBackups: 2);

        Assert.True(File.Exists(b3.FullPath));
        Assert.True(b3.SizeBytes > 0);
        var listed = backupSvc.ListBackups();
        Assert.Equal(2, listed.Count);
        Assert.DoesNotContain(listed, x => x.FileName == b1.FileName);
    }

    [Fact]
    public void EnvelopeAesGcm_DetectsTampering_And_UsesUniqueNonces()
    {
        var protector = new EnvelopeSecretProtector(_paths, "unit-test-master-seed-material-2026");
        var c1 = protector.Protect("qbittorrent-super-secret-pass");
        var c2 = protector.Protect("qbittorrent-super-secret-pass");

        Assert.NotEqual(c1, c2);
        Assert.StartsWith($"enc:v1:{protector.KeyId}:", c1);
        Assert.Equal("qbittorrent-super-secret-pass", protector.Unprotect(c1));
        Assert.Equal("qbittorrent-super-secret-pass", protector.Unprotect(c2));

        // Flip final character of payload tag -> must fail GCM authentication
        var tampered = c1[..^2] + (c1[^2] == 'A' ? "B" : "A") + c1[^1];
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(tampered));
    }

    [Fact]
    public void SafeHttpClientFactory_BlocksCloudMetadataAndUnallowlistedRfc1918_AndSignsWebhooks()
    {
        var opts = Options.Create(new SafeHttpEgressOptions
        {
            AllowedPrivateCidrs = ["192.168.1.0/24"],
            AllowedInternalHosts = ["plex.lan.example.com"],
        });
        var factory = new SafeHttpClientFactory(opts);

        // Cloud metadata IP 169.254.169.254 is ALWAYS blocked, even in homelab mode
        Assert.Throws<SsrfBlockedException>(() =>
            factory.EnsureAddressPermitted(IPAddress.Parse("169.254.169.254"), "metadata.internal", allowPrivateLan: true));

        // Loopback and RFC1918 are blocked on public client
        Assert.Throws<SsrfBlockedException>(() =>
            factory.EnsureAddressPermitted(IPAddress.Loopback, "evil.example.com", allowPrivateLan: false));
        Assert.Throws<SsrfBlockedException>(() =>
            factory.EnsureAddressPermitted(IPAddress.Parse("192.168.1.2"), "nas.lan", allowPrivateLan: false));

        // Unallowlisted subnet 10.0.0.5 is blocked even in homelab mode when AllowedPrivateCidrs is specified
        Assert.Throws<SsrfBlockedException>(() =>
            factory.EnsureAddressPermitted(IPAddress.Parse("10.0.0.5"), "unknown-box", allowPrivateLan: true));

        // Allowlisted homelab CIDR 192.168.1.0/24 and public IPs succeed
        factory.EnsureAddressPermitted(IPAddress.Parse("192.168.1.2"), "192.168.1.2", allowPrivateLan: true);
        factory.EnsureAddressPermitted(IPAddress.Parse("1.1.1.1"), "plex.tv", allowPrivateLan: false);

        var webhook = new SignedWebhookDispatcher(factory);
        var signed = webhook.SignPayload(new { Event = "cleanerr.staged", Count = 3 }, "whsec_test_123");
        Assert.StartsWith("sha256=", signed.SignatureHeader);
        Assert.NotEmpty(signed.DeliveryId);
    }
}
