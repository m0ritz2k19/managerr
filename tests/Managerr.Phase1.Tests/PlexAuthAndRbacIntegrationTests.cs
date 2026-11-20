using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Managerr.Host.Auth;
using Managerr.Security.Plex;
using Managerr.Security.Rbac;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Managerr.Phase1.Tests;

public sealed class FakePlexApiClient : IPlexApiClient
{
    public Dictionary<long, PlexPinPollResult> PinResults { get; } = new();
    public List<PlexSharedUserEntry> SharedUsers { get; } = [];
    public Dictionary<string, PlexAccountProfile> HomeProfilesByUuid { get; } = new();

    public string GetClientIdentifier() => "test-managerr-plex-client-id";

    public Task<PlexPinInfo> CreateStrongPinAsync(string? forwardUrl = null, CancellationToken ct = default) =>
        Task.FromResult(new PlexPinInfo(101, "ABCD1234", "https://app.plex.tv/auth#?code=ABCD1234", GetClientIdentifier()));

    public Task<PlexPinPollResult> CheckPinAsync(long pinId, string? expectedCode = null, CancellationToken ct = default) =>
        Task.FromResult(PinResults.TryGetValue(pinId, out var res)
            ? res
            : new PlexPinPollResult(false, null, null, []));

    public Task<PlexAccountProfile> GetUserProfileAsync(string plexAuthToken, CancellationToken ct = default)
    {
        var uuid = plexAuthToken.Replace("home-tok-", "", StringComparison.Ordinal);
        return Task.FromResult(HomeProfilesByUuid[uuid]);
    }

    public Task<IReadOnlyList<PlexSharedUserEntry>> GetSharedServerUsersAsync(
        string ownerAdminToken,
        string boundMachineIdentifier,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PlexSharedUserEntry>>(SharedUsers.ToList());

    public Task<string> SwitchHomeUserAsync(string ownerOrAdminToken, string userUuid, string? pin = null, CancellationToken ct = default) =>
        Task.FromResult($"home-tok-{userUuid}");
}

public sealed class PlexAuthAndRbacIntegrationTests : IAsyncLifetime
{
    private readonly string _tempConfigDir = Path.Combine(Path.GetTempPath(), $"managerr-p1-api-{Guid.NewGuid():N}");
    private readonly FakePlexApiClient _fakePlex = new();
    private WebApplicationFactory<Program> _factory = null!;

    public Task InitializeAsync()
    {
        // Seed fake Plex owner, shared friend Alice, Home user Kid, and an unshared stranger
        _fakePlex.SharedUsers.Add(new PlexSharedUserEntry(
            PlexUserId: 2002,
            Uuid: "uuid-alice",
            Username: "friend_alice",
            Email: "alice@example.com",
            AvatarUrl: null,
            IsHomeUser: false,
            AllLibrariesShared: false,
            SharedSectionIds: ["1", "2"]));

        _fakePlex.SharedUsers.Add(new PlexSharedUserEntry(
            PlexUserId: 3003,
            Uuid: "uuid-home-kid",
            Username: "home_kid",
            Email: null,
            AvatarUrl: null,
            IsHomeUser: true,
            AllLibrariesShared: false,
            SharedSectionIds: ["3"]));

        _fakePlex.HomeProfilesByUuid["uuid-home-kid"] = new PlexAccountProfile(
            3003, "uuid-home-kid", "home_kid", null, null, "de", "de");

        // PIN 101: Plex Server Owner
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

        // PIN 202: Shared Plex Friend Alice
        _fakePlex.PinResults[202] = new PlexPinPollResult(
            Claimed: true,
            AuthToken: "plex-alice-token",
            Account: new PlexAccountProfile(2002, "uuid-alice", "friend_alice", "alice@example.com", null, "de", "de"),
            Servers: []);

        // PIN 999: Unshared random plex.tv stranger!
        _fakePlex.PinResults[999] = new PlexPinPollResult(
            Claimed: true,
            AuthToken: "plex-stranger-token",
            Account: new PlexAccountProfile(9999, "uuid-stranger", "random_stranger", "stranger@internet.com", null, "en", "en"),
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
    public async Task FullPhase1SecurityFlow_PlexOwnerClaim_ServerMembershipGate_Presets_CustomRoles_AdminOnlyIndexers_Csrf_BreakGlass_And_ApiKeys()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        // 1. Health & OpenAPI v1 are reachable before setup
        var healthResp = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/health");
        Assert.Equal("healthy", healthResp.GetProperty("status").GetString());
        Assert.Equal("sqlite-wal-embedded", healthResp.GetProperty("databaseEngine").GetString());
        Assert.False(healthResp.GetProperty("externalDatabaseRequired").GetBoolean());
        Assert.False(healthResp.GetProperty("isClaimed").GetBoolean());

        var openApiResp = await client.GetAsync("/api/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, openApiResp.StatusCode);

        // 2. First-Run Claim by Non-Owner Stranger (PIN 999 has 0 owned servers) -> Rejected with 403!
        var strangerClaimAttempt = await client.GetAsync("/api/v1/auth/plex/poll/999");
        Assert.Equal(HttpStatusCode.Forbidden, strangerClaimAttempt.StatusCode);

        // 3. First-Run Claim by Plex Server Owner (PIN 101) -> Binds HomelabPMS, assigns Admin, syncs shared friends!
        var ownerPoll = await client.GetAsync("/api/v1/auth/plex/poll/101");
        Assert.Equal(HttpStatusCode.OK, ownerPoll.StatusCode);
        var setCookie = ownerPoll.Headers.GetValues("Set-Cookie").First();
        Assert.Contains("managerr_session=", setCookie);
        Assert.Contains("httponly", setCookie.ToLowerInvariant());

        var ownerJson = await ownerPoll.Content.ReadFromJsonAsync<JsonElement>();
        var ownerToken = ownerJson.GetProperty("sessionToken").GetString()!;
        var ownerCsrf = ownerJson.GetProperty("csrfToken").GetString()!;
        var ownerCookiePair = setCookie.Split(';')[0];

        // 4. Cookie CSRF Protection: Cookie POST without X-Managerr-Csrf MUST fail (401); with X-Managerr-Csrf MUST pass!
        using (var noCsrfReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/users/sync-plex"))
        {
            noCsrfReq.Headers.Add("Cookie", ownerCookiePair);
            var noCsrfResp = await client.SendAsync(noCsrfReq);
            Assert.Equal(HttpStatusCode.Unauthorized, noCsrfResp.StatusCode);
        }

        using (var withCsrfReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/users/sync-plex"))
        {
            withCsrfReq.Headers.Add("Cookie", ownerCookiePair);
            withCsrfReq.Headers.Add(ManagerrAuthConstants.CsrfHeaderName, ownerCsrf);
            var withCsrfResp = await client.SendAsync(withCsrfReq);
            Assert.Equal(HttpStatusCode.OK, withCsrfResp.StatusCode);
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        // 5. Strict PMS Server-Membership Gate: Stranger (PIN 999) is STILL denied after server is claimed!
        using var anonClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var postClaimStranger = await anonClient.GetAsync("/api/v1/auth/plex/poll/999");
        Assert.Equal(HttpStatusCode.Forbidden, postClaimStranger.StatusCode);
        var problemJson = await postClaimStranger.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ERR_PLEX_SERVER_MEMBERSHIP_DENIED", problemJson.GetProperty("code").GetString());

        // 6. Shared Plex Friend Alice (PIN 202) signs in -> auto-assigned Requester role + shared sections ["1","2"]
        var alicePoll = await anonClient.GetAsync("/api/v1/auth/plex/poll/202");
        Assert.Equal(HttpStatusCode.OK, alicePoll.StatusCode);
        var aliceJson = await alicePoll.Content.ReadFromJsonAsync<JsonElement>();
        var aliceToken = aliceJson.GetProperty("sessionToken").GetString()!;
        var aliceUserId = aliceJson.GetProperty("user").GetProperty("id").GetInt64();
        var aliceRoleKey = aliceJson.GetProperty("user").GetProperty("roles")[0].GetProperty("key").GetString();
        Assert.Equal(BuiltInRoleKeys.Requester, aliceRoleKey);

        // 7. Indexer configuration is Plex Admin ONLY by default:
        //    - Owner (Admin) -> 200 OK on /api/v1/indexers
        //    - Alice (Requester) -> 403 ERR_RBAC_FORBIDDEN on /api/v1/indexers
        var ownerIndexers = await client.GetAsync("/api/v1/indexers");
        Assert.Equal(HttpStatusCode.OK, ownerIndexers.StatusCode);

        using var aliceClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        aliceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aliceToken);

        var aliceIndexersBefore = await aliceClient.GetAsync("/api/v1/indexers");
        Assert.Equal(HttpStatusCode.Forbidden, aliceIndexersBefore.StatusCode);

        // Alice CAN update her own audio/subtitle language preferences
        var prefResp = await aliceClient.PatchAsJsonAsync("/api/v1/users/me/preferences", new
        {
            preferredAudioLang = "ja",
            preferredSubLang = "de",
            discordUsername = "alice#1234",
        });
        Assert.Equal(HttpStatusCode.OK, prefResp.StatusCode);

        // 8. Admin Immutability Lock-Out Guard: Built-in Admin role cannot be modified or deleted
        var rolesList = await client.GetFromJsonAsync<JsonElement>("/api/v1/roles");
        var adminRoleElem = rolesList.EnumerateArray().First(r => r.GetProperty("key").GetString() == BuiltInRoleKeys.Admin);
        var requesterRoleElem = rolesList.EnumerateArray().First(r => r.GetProperty("key").GetString() == BuiltInRoleKeys.Requester);
        var adminRoleId = adminRoleElem.GetProperty("id").GetInt64();
        var requesterRoleId = requesterRoleElem.GetProperty("id").GetInt64();

        var mutateAdminAttempt = await client.PutAsJsonAsync($"/api/v1/roles/{adminRoleId}", new
        {
            name = "Broken Admin",
            description = "Attempt to strip admin scopes",
            color = "#000000",
            scopes = new[] { PermissionScopes.CatalogRead },
        });
        Assert.Equal(HttpStatusCode.Conflict, mutateAdminAttempt.StatusCode);

        var deletePresetAttempt = await client.DeleteAsync($"/api/v1/roles/{requesterRoleId}");
        Assert.Equal(HttpStatusCode.Conflict, deletePresetAttempt.StatusCode);

        // 9. Custom Role Builder: Admin creates "VIP Family" role delegating read-only Indexers & Queue access & assigns to Alice!
        var createVipResp = await client.PostAsJsonAsync("/api/v1/roles", new
        {
            name = "VIP Family",
            key = "custom:vip-family",
            description = "Trusted household members allowed to look at indexers and the download queue.",
            color = "#ec4899",
            scopes = new[]
            {
                PermissionScopes.CatalogRead,
                PermissionScopes.CatalogRequest,
                PermissionScopes.CatalogAutoApprove,
                PermissionScopes.IndexersRead,
                PermissionScopes.QueueRead,
            },
            libraryAccessMode = "inherit_from_plex",
            autoApproveRequests = true,
            movieRequestQuota = 15,
        });
        Assert.Equal(HttpStatusCode.Created, createVipResp.StatusCode);
        var vipRole = await createVipResp.Content.ReadFromJsonAsync<JsonElement>();
        var vipRoleId = vipRole.GetProperty("id").GetInt64();

        // Also test Clone Role endpoint
        var cloneResp = await client.PostAsJsonAsync($"/api/v1/roles/{vipRoleId}/clone", new { name = "VIP Plus" });
        Assert.Equal(HttpStatusCode.Created, cloneResp.StatusCode);

        var assignResp = await client.PutAsJsonAsync($"/api/v1/users/{aliceUserId}/roles", new
        {
            roleIds = new[] { requesterRoleId, vipRoleId },
        });
        Assert.Equal(HttpStatusCode.OK, assignResp.StatusCode);

        // Now Alice immediately has Indexers Read + Queue Read, but NOT Indexers Write!
        Assert.Equal(HttpStatusCode.OK, (await aliceClient.GetAsync("/api/v1/indexers")).StatusCode);
        var aliceMe = await aliceClient.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        var aliceScopes = aliceMe.GetProperty("scopes").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains(PermissionScopes.IndexersRead, aliceScopes);
        Assert.Contains(PermissionScopes.QueueRead, aliceScopes);
        Assert.DoesNotContain(PermissionScopes.IndexersWrite, aliceScopes);

        // 10. Break-Glass LAN Auth & Scoped API Keys & Idempotency-Key Replay
        var bgSetup = await client.PostAsJsonAsync("/api/v1/auth/breakglass/setup", new
        {
            newPassword = "HomelabOfflineEmergency!2026",
        });
        Assert.Equal(HttpStatusCode.OK, bgSetup.StatusCode);

        var bgBad = await anonClient.PostAsJsonAsync("/api/v1/auth/breakglass/login", new
        {
            usernameOrEmail = "plexowner",
            password = "wrong-password",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, bgBad.StatusCode);

        var bgGood = await anonClient.PostAsJsonAsync("/api/v1/auth/breakglass/login", new
        {
            usernameOrEmail = "plexowner",
            password = "HomelabOfflineEmergency!2026",
        });
        Assert.Equal(HttpStatusCode.OK, bgGood.StatusCode);

        // Issue a narrow metrics-only API key
        var keyCreateResp = await client.PostAsJsonAsync("/api/v1/auth/tokens", new
        {
            name = "Homepage Widget",
            scopes = new[] { PermissionScopes.MetricsRead },
            expiresInDays = 30,
        });
        Assert.Equal(HttpStatusCode.Created, keyCreateResp.StatusCode);
        var createdKey = await keyCreateResp.Content.ReadFromJsonAsync<JsonElement>();
        var rawApiKey = createdKey.GetProperty("rawTokenOnce").GetString()!;
        Assert.StartsWith("mgr_live_", rawApiKey);

        // Listing tokens never leaks rawTokenOnce
        var listedKeysRaw = await client.GetStringAsync("/api/v1/auth/tokens");
        Assert.DoesNotContain(rawApiKey, listedKeysRaw);

        using var metricsClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        metricsClient.DefaultRequestHeaders.Add(ManagerrAuthConstants.ApiKeyHeaderName, rawApiKey);
        Assert.Equal(HttpStatusCode.OK, (await metricsClient.GetAsync("/api/v1/system/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await metricsClient.GetAsync("/api/v1/system/storage")).StatusCode);

        // Verify Idempotency-Key replay on POST /api/v1/system/backups
        string backupFile1;
        using (var idem1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/system/backups")
        {
            Content = JsonContent.Create(new { reason = "idemtest" }),
        })
        {
            idem1.Headers.Add("Idempotency-Key", "backup-op-001");
            var r1 = await client.SendAsync(idem1);
            Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
            backupFile1 = (await r1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fileName").GetString()!;
        }

        using (var idem2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/system/backups")
        {
            Content = JsonContent.Create(new { reason = "idemtest" }),
        })
        {
            idem2.Headers.Add("Idempotency-Key", "backup-op-001");
            var r2 = await client.SendAsync(idem2);
            Assert.Equal(HttpStatusCode.Created, r2.StatusCode);
            Assert.Equal("true", r2.Headers.GetValues("X-Idempotency-Replayed").First());
            var backupFile2 = (await r2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fileName").GetString()!;
            Assert.Equal(backupFile1, backupFile2);
        }

        // 11. Unsharing Alice on PMS + Syncing Plex Users immediately revokes Alice's active session!
        _fakePlex.SharedUsers.RemoveAll(u => u.PlexUserId == 2002);
        var resyncResp = await client.PostAsync("/api/v1/users/sync-plex", null);
        Assert.Equal(HttpStatusCode.OK, resyncResp.StatusCode);

        var aliceAfterUnshare = await aliceClient.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, aliceAfterUnshare.StatusCode);

        // 12. Verify GET /api/v1/auth/plex/callback self-closing popup page & ForwardedHeaders SameSite=Lax; Secure cookie
        var cbResp = await anonClient.GetAsync("/api/v1/auth/plex/callback");
        Assert.Equal(HttpStatusCode.OK, cbResp.StatusCode);
        var cbHtml = await cbResp.Content.ReadAsStringAsync();
        Assert.Contains("managerr-plex-auth-complete", cbHtml);
        Assert.Contains("window.close()", cbHtml);

        using var httpsReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/breakglass/login")
        {
            Content = JsonContent.Create(new
            {
                usernameOrEmail = "plexowner",
                password = "HomelabOfflineEmergency!2026",
            }),
        };
        httpsReq.Headers.Add("X-Forwarded-Proto", "https");
        var httpsLoginResp = await anonClient.SendAsync(httpsReq);
        Assert.Equal(HttpStatusCode.OK, httpsLoginResp.StatusCode);
        var httpsSetCookie = string.Join("; ", httpsLoginResp.Headers.GetValues("Set-Cookie"));
        Assert.Contains("SameSite=Lax", httpsSetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Secure", httpsSetCookie, StringComparison.OrdinalIgnoreCase);
    }
}
