using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Managerr.Common.Crypto;
using Managerr.Datastore;
using Managerr.Host.Auth;
using Managerr.Security.Auth;
using Managerr.Security.Plex;
using Managerr.Security.Rbac;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Managerr.Phase1.Tests;

public sealed class GeneralSettingsApiKeyTests : IAsyncLifetime
{
    private readonly string _tempConfigDir = Path.Combine(Path.GetTempPath(), $"managerr-p1-gen-{Guid.NewGuid():N}");
    private readonly FakePlexApiClient _fakePlex = new();
    private WebApplicationFactory<Program> _factory = null!;

    public Task InitializeAsync()
    {
        _fakePlex.SharedUsers.Add(new PlexSharedUserEntry(
            PlexUserId: 2002,
            Uuid: "uuid-alice",
            Username: "friend_alice",
            Email: "alice@example.com",
            AvatarUrl: null,
            IsHomeUser: false,
            AllLibrariesShared: false,
            SharedSectionIds: ["1", "2"]));

        _fakePlex.PinResults[101] = new PlexPinPollResult(
            Claimed: true,
            AuthToken: "plex-owner-secret-token",
            Account: new PlexAccountProfile(1001, "uuid-owner", "plexowner", "owner@homelab.lan", null, "de", "en"),
            Servers:
            [
                new PlexServerResource(
                    Name: "HomelabPMS",
                    MachineIdentifier: "machine-homelab-pms-01",
                    Owned: true,
                    AccessToken: "plex-owner-secret-token",
                    Connections:
                    [
                        new PlexServerConnection("http://192.168.1.10:32400", "http://192.168.1.10:32400", "192.168.1.10", 32400, "http", true),
                    ]),
            ]);

        _fakePlex.PinResults[202] = new PlexPinPollResult(
            Claimed: true,
            AuthToken: "plex-alice-token",
            Account: new PlexAccountProfile(2002, "uuid-alice", "friend_alice", "alice@example.com", null, "de", "de"),
            Servers: []);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = _tempConfigDir,
                    ["Managerr:MasterKey"] = "integration-test-master-key-material-2026",
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPlexApiClient>();
                services.AddSingleton<IPlexApiClient>(_fakePlex);
            });
        });

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        if (Directory.Exists(_tempConfigDir))
        {
            try { Directory.Delete(_tempConfigDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task GeneralSettings_InstanceApiKey_FullLifecycleAndSecurity()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        // 1. Startup provisions a 32-char lowercase hex instance API key stored encrypted
        using (var scope = _factory.Services.CreateScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<ISqliteDbFactory>();
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            var auth = scope.ServiceProvider.GetRequiredService<IPlexAuthAndSessionService>();

            var instanceApiKey = await auth.GetInstanceApiKeyAsync();
            Assert.Equal(32, instanceApiKey.Length);
            Assert.True(instanceApiKey.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')));

            await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core);
            var row = await conn.QuerySingleOrDefaultAsync<(string Value, int IsSecret)>(
                "SELECT value AS Value, is_secret AS IsSecret FROM app_settings WHERE key = @Key;",
                new { Key = GeneralSettingsKeys.InstanceApiKeyEnc });

            Assert.Equal(1, row.IsSecret);
            Assert.True(protector.IsProtectedEnvelope(row.Value));
            Assert.NotEqual(instanceApiKey, row.Value);
            Assert.Equal(instanceApiKey, protector.Unprotect(row.Value));
        }

        // Claim Plex owner
        var ownerPoll = await client.GetAsync("/api/v1/auth/plex/poll/101");
        Assert.Equal(HttpStatusCode.OK, ownerPoll.StatusCode);
        var ownerJson = await ownerPoll.Content.ReadFromJsonAsync<JsonElement>();
        var ownerToken = ownerJson.GetProperty("sessionToken").GetString()!;
        var ownerCsrf = ownerJson.GetProperty("csrfToken").GetString()!;
        var setCookie = ownerPoll.Headers.GetValues("Set-Cookie").First();
        var ownerCookiePair = setCookie.Split(';')[0];

        // 2. GET /api/v1/settings/general security checks:
        // Anonymous -> 401
        using var anonClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var anonResp = await anonClient.GetAsync("/api/v1/settings/general");
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        // Non-admin user (Alice) -> 403
        var alicePoll = await anonClient.GetAsync("/api/v1/auth/plex/poll/202");
        Assert.Equal(HttpStatusCode.OK, alicePoll.StatusCode);
        var aliceJson = await alicePoll.Content.ReadFromJsonAsync<JsonElement>();
        var aliceToken = aliceJson.GetProperty("sessionToken").GetString()!;

        using var aliceClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        aliceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aliceToken);
        var aliceResp = await aliceClient.GetAsync("/api/v1/settings/general");
        Assert.Equal(HttpStatusCode.Forbidden, aliceResp.StatusCode);

        // Scoped PAT with SystemAdmin -> 403 ERR_SCOPED_KEY_FORBIDDEN
        using var ownerBearerClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        ownerBearerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        var patResp = await ownerBearerClient.PostAsJsonAsync("/api/v1/auth/tokens", new
        {
            name = "Admin Scoped PAT",
            scopes = new[] { PermissionScopes.SystemAdmin },
        });
        Assert.Equal(HttpStatusCode.Created, patResp.StatusCode);
        var patJson = await patResp.Content.ReadFromJsonAsync<JsonElement>();
        var patToken = patJson.GetProperty("rawTokenOnce").GetString()!;
        Assert.StartsWith("mgr_live_", patToken);

        using var patClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        patClient.DefaultRequestHeaders.Add(ManagerrAuthConstants.ApiKeyHeaderName, patToken);
        var patGeneralResp = await patClient.GetAsync("/api/v1/settings/general");
        Assert.Equal(HttpStatusCode.Forbidden, patGeneralResp.StatusCode);
        var patProblem = await patGeneralResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ERR_SCOPED_KEY_FORBIDDEN", patProblem.GetProperty("code").GetString());

        // 3. Interactive Admin session: GET returns 200 OK with Cache-Control: no-store and 32-hex key
        var getResp = await ownerBearerClient.GetAsync("/api/v1/settings/general");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var cacheControl = getResp.Headers.CacheControl;
        Assert.True(cacheControl?.NoStore == true || getResp.Headers.GetValues("Cache-Control").Any(h => h.Contains("no-store")));

        var generalJson = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        var apiKey = generalJson.GetProperty("apiKey").GetString()!;
        Assert.Equal(32, apiKey.Length);
        Assert.True(apiKey.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')));
        Assert.Equal("Managerr", generalJson.GetProperty("instanceName").GetString());

        // CSRF-enforced PUT /api/v1/settings/general updating instanceName
        using (var noCsrfReq = new HttpRequestMessage(HttpMethod.Put, "/api/v1/settings/general")
        {
            Content = JsonContent.Create(new { instanceName = "My Homelab Media" }),
        })
        {
            noCsrfReq.Headers.Add("Cookie", ownerCookiePair);
            var noCsrfResp = await client.SendAsync(noCsrfReq);
            Assert.Equal(HttpStatusCode.Unauthorized, noCsrfResp.StatusCode);
        }

        using (var csrfReq = new HttpRequestMessage(HttpMethod.Put, "/api/v1/settings/general")
        {
            Content = JsonContent.Create(new { instanceName = "My Homelab Media" }),
        })
        {
            csrfReq.Headers.Add("Cookie", ownerCookiePair);
            csrfReq.Headers.Add(ManagerrAuthConstants.CsrfHeaderName, ownerCsrf);
            var csrfResp = await client.SendAsync(csrfReq);
            Assert.Equal(HttpStatusCode.OK, csrfResp.StatusCode);
            var putJson = await csrfResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("My Homelab Media", putJson.GetProperty("instanceName").GetString());
            Assert.Equal(apiKey, putJson.GetProperty("apiKey").GetString());
        }

        // 4. Authenticate requests via 32-hex apiKey:
        // via X-Api-Key
        using var apiKeyHeaderClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        apiKeyHeaderClient.DefaultRequestHeaders.Add(ManagerrAuthConstants.ApiKeyHeaderName, apiKey);
        var h1 = await apiKeyHeaderClient.GetAsync("/api/v1/system/storage");
        Assert.Equal(HttpStatusCode.OK, h1.StatusCode);

        // Non-interactive 32-hex key cannot read or rotate /api/v1/settings/general*
        var selfGetAttempt = await apiKeyHeaderClient.GetAsync("/api/v1/settings/general");
        Assert.Equal(HttpStatusCode.Forbidden, selfGetAttempt.StatusCode);
        var selfResetAttempt = await apiKeyHeaderClient.PostAsync("/api/v1/settings/general/api-key/reset", null);
        Assert.Equal(HttpStatusCode.Forbidden, selfResetAttempt.StatusCode);

        // via Authorization: Bearer <apiKey>
        using var apiKeyBearerClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        apiKeyBearerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var h2 = await apiKeyBearerClient.GetAsync("/api/v1/system/storage");
        Assert.Equal(HttpStatusCode.OK, h2.StatusCode);

        // via /compat/sonarr/api/v3/system/status?apikey=<apiKey>
        var sonarrResp = await anonClient.GetAsync($"/compat/sonarr/api/v3/system/status?apikey={apiKey}");
        Assert.Equal(HttpStatusCode.OK, sonarrResp.StatusCode);
        var sonarrJson = await sonarrResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Sonarr", sonarrJson.GetProperty("appName").GetString());

        // 5. POST /api/v1/settings/general/api-key/reset
        var resetResp = await ownerBearerClient.PostAsync("/api/v1/settings/general/api-key/reset", null);
        Assert.Equal(HttpStatusCode.OK, resetResp.StatusCode);
        var resetJson = await resetResp.Content.ReadFromJsonAsync<JsonElement>();
        var newApiKey = resetJson.GetProperty("apiKey").GetString()!;
        Assert.Equal(32, newApiKey.Length);
        Assert.NotEqual(apiKey, newApiKey);

        // Old key immediately invalidated
        using var oldKeyClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        oldKeyClient.DefaultRequestHeaders.Add(ManagerrAuthConstants.ApiKeyHeaderName, apiKey);
        var oldKeyResp = await oldKeyClient.GetAsync("/api/v1/system/storage");
        Assert.Equal(HttpStatusCode.Unauthorized, oldKeyResp.StatusCode);

        // New key works
        using var newKeyClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        newKeyClient.DefaultRequestHeaders.Add(ManagerrAuthConstants.ApiKeyHeaderName, newApiKey);
        var newKeyResp = await newKeyClient.GetAsync("/api/v1/system/storage");
        Assert.Equal(HttpStatusCode.OK, newKeyResp.StatusCode);

        // Verify audit log has system.general.apikey.reset without raw newKey
        var auditListResp = await ownerBearerClient.GetFromJsonAsync<JsonElement>("/api/v1/system/audit?action=system.general.apikey.reset");
        var auditItems = auditListResp.EnumerateArray().ToList();
        Assert.NotEmpty(auditItems);
        var latestReset = auditItems.First();
        var detailsStr = latestReset.GetProperty("detailsJson").GetString()!;
        Assert.DoesNotContain(newApiKey, detailsStr);
        Assert.Contains($"****{newApiKey[^4..]}", detailsStr);
    }
}
