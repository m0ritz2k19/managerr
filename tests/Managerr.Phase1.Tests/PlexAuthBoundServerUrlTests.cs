using Managerr.Common.Crypto;
using Managerr.Common.Paths;
using Managerr.Datastore;
using Managerr.Datastore.Migrations;
using Managerr.Datastore.Services;
using Managerr.Security.Auth;
using Managerr.Security.Plex;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Xunit;

namespace Managerr.Phase1.Tests;

public sealed class PlexAuthBoundServerUrlTests
{
    private static async Task<(SqliteDbFactory DbFactory, AppSettingsService AppSettings, PlexAuthAndSessionService AuthService, FakePlexApiClient FakePlex, string TempDir)> CreateTestContextAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"managerr-p1-url-{Guid.NewGuid():N}");
        var paths = new AppPaths(tempDir);
        var dbFactory = new SqliteDbFactory(paths);
        var migrator = new SchemaMigrator(dbFactory);
        await migrator.MigrateAllAsync();

        var protector = new EnvelopeSecretProtector(paths);
        var appSettings = new AppSettingsService(dbFactory, protector);
        var audit = new AuditLogService(dbFactory);
        var roles = new RoleService(dbFactory);
        var users = new PlexUserService(dbFactory, roles);
        var fakePlex = new FakePlexApiClient();

        var authService = new PlexAuthAndSessionService(
            dbFactory,
            appSettings,
            fakePlex,
            users,
            protector,
            audit);

        return (dbFactory, appSettings, authService, fakePlex, tempDir);
    }

    [Fact]
    public async Task InitialClaim_WhenBoundServerUrlAlreadySet_PreservesExistingUrl()
    {
        var (dbFactory, appSettings, authService, fakePlex, tempDir) = await CreateTestContextAsync();
        try
        {
            const string existingUrl = "http://plex-test:32400";
            const string discoveredConnUrl = "http://172.18.0.2:32400";

            // Pre-seed plex.bound_server_url (e.g. via bootstrap configuration)
            await appSettings.SetAsync(PlexSettingsKeys.BoundServerUrl, existingUrl);

            fakePlex.PinResults[101] = new PlexPinPollResult(
                Claimed: true,
                AuthToken: "plex-token-101",
                Account: new PlexAccountProfile(1001, "uuid-owner", "owner", "owner@local.lan", null, "en", "en"),
                Servers:
                [
                    new PlexServerResource(
                        Name: "HomelabPMS",
                        MachineIdentifier: "machine-pms-01",
                        Owned: true,
                        AccessToken: "plex-token-101",
                        Connections:
                        [
                            new PlexServerConnection(discoveredConnUrl, discoveredConnUrl, "172.18.0.2", 32400, "http", true),
                        ]),
                ]);

            var outcome = await authService.CompletePlexPinAsync(101, null, null, "test-agent", "127.0.0.1");

            Assert.True(outcome.Claimed);
            Assert.NotNull(outcome.Session);

            var boundUrl = await appSettings.GetAsync(PlexSettingsKeys.BoundServerUrl);
            Assert.Equal(existingUrl, boundUrl);
        }
        finally
        {
            await dbFactory.DisposeAsync();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public async Task InitialClaim_WhenBoundServerUrlEmpty_UsesPreferredConnection()
    {
        var (dbFactory, appSettings, authService, fakePlex, tempDir) = await CreateTestContextAsync();
        try
        {
            const string discoveredConnUrl = "http://172.18.0.2:32400";

            // Do NOT pre-seed plex.bound_server_url; verify it is empty/null
            var existingUrl = await appSettings.GetAsync(PlexSettingsKeys.BoundServerUrl);
            Assert.True(string.IsNullOrWhiteSpace(existingUrl));

            fakePlex.PinResults[101] = new PlexPinPollResult(
                Claimed: true,
                AuthToken: "plex-token-101",
                Account: new PlexAccountProfile(1001, "uuid-owner", "owner", "owner@local.lan", null, "en", "en"),
                Servers:
                [
                    new PlexServerResource(
                        Name: "HomelabPMS",
                        MachineIdentifier: "machine-pms-01",
                        Owned: true,
                        AccessToken: "plex-token-101",
                        Connections:
                        [
                            new PlexServerConnection(discoveredConnUrl, discoveredConnUrl, "172.18.0.2", 32400, "http", true),
                        ]),
                ]);

            var outcome = await authService.CompletePlexPinAsync(101, null, null, "test-agent", "127.0.0.1");

            Assert.True(outcome.Claimed);
            Assert.NotNull(outcome.Session);

            var boundUrl = await appSettings.GetAsync(PlexSettingsKeys.BoundServerUrl);
            Assert.Equal(discoveredConnUrl, boundUrl);
        }
        finally
        {
            await dbFactory.DisposeAsync();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public async Task InitialClaim_WhenMultipleOwnedServersAndBoundMachineIdPreseeded_AutoSelectsMatchingServer()
    {
        var (dbFactory, appSettings, authService, fakePlex, tempDir) = await CreateTestContextAsync();
        try
        {
            const string existingUrl = "http://plex-test:32400";
            const string targetMachineId = "machine-testlab-02";

            await appSettings.SetAsync(PlexSettingsKeys.BoundServerUrl, existingUrl);
            await appSettings.SetAsync(PlexSettingsKeys.BoundMachineId, targetMachineId);

            fakePlex.PinResults[102] = new PlexPinPollResult(
                Claimed: true,
                AuthToken: "plex-token-102",
                Account: new PlexAccountProfile(1001, "uuid-owner", "owner", "owner@local.lan", null, "en", "en"),
                Servers:
                [
                    new PlexServerResource(
                        Name: "ProdPMS",
                        MachineIdentifier: "machine-prod-01",
                        Owned: true,
                        AccessToken: "plex-token-102",
                        Connections:
                        [
                            new PlexServerConnection("http://192.168.1.10:32400", "http://192.168.1.10:32400", "192.168.1.10", 32400, "http", true),
                        ]),
                    new PlexServerResource(
                        Name: "TestLabPMS",
                        MachineIdentifier: targetMachineId,
                        Owned: true,
                        AccessToken: "plex-token-102",
                        Connections:
                        [
                            new PlexServerConnection("http://172.18.0.5:32400", "http://172.18.0.5:32400", "172.18.0.5", 32400, "http", true),
                        ]),
                ]);

            var outcome = await authService.CompletePlexPinAsync(102, null, null, "test-agent", "127.0.0.1");

            Assert.True(outcome.Claimed);
            Assert.False(outcome.RequiresServerSelection);
            Assert.NotNull(outcome.Session);
            Assert.Equal(targetMachineId, await appSettings.GetAsync(PlexSettingsKeys.BoundMachineId));
            Assert.Equal("TestLabPMS", await appSettings.GetAsync(PlexSettingsKeys.BoundServerName));
            Assert.Equal(existingUrl, await appSettings.GetAsync(PlexSettingsKeys.BoundServerUrl));
        }
        finally
        {
            await dbFactory.DisposeAsync();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            }
        }
    }
}
