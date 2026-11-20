using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Managerr.Common.Configuration;
using Managerr.Common.Net;
using Managerr.Datastore;
using Managerr.Host.Auth;
using Managerr.Security.Auth;
using Managerr.Security.Plex;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Managerr.Phase1.Tests;

public sealed class MockOidcHttpMessageHandler : HttpMessageHandler
{
    private readonly RSA _rsa;
    public string Issuer { get; } = "https://mock-oidc.homelab.test";
    public string? ExpectedNonce { get; set; }

    public MockOidcHttpMessageHandler(RSA rsa)
    {
        _rsa = rsa;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri?.ToString() ?? "";

        if (uri.EndsWith("/.well-known/openid-configuration", StringComparison.OrdinalIgnoreCase))
        {
            var discovery = new
            {
                issuer = Issuer,
                authorization_endpoint = $"{Issuer}/oauth/authorize",
                token_endpoint = $"{Issuer}/oauth/token",
                jwks_uri = $"{Issuer}/oauth/jwks",
                userinfo_endpoint = $"{Issuer}/oauth/userinfo"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(discovery), Encoding.UTF8, "application/json")
            });
        }

        if (uri.EndsWith("/oauth/jwks", StringComparison.OrdinalIgnoreCase))
        {
            var rsaParams = _rsa.ExportParameters(false);
            var jwks = new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "RSA",
                        use = "sig",
                        alg = "RS256",
                        kid = "mock-key-1",
                        n = Base64Url.Encode(rsaParams.Modulus!),
                        e = Base64Url.Encode(rsaParams.Exponent!)
                    }
                }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(jwks), Encoding.UTF8, "application/json")
            });
        }

        if (uri.EndsWith("/oauth/token", StringComparison.OrdinalIgnoreCase))
        {
            var headerJson = JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT", kid = "mock-key-1" });
            var payloadJson = JsonSerializer.Serialize(new
            {
                iss = Issuer,
                sub = "oidc-sub-alice-001",
                aud = "managerr-oidc-client",
                nonce = ExpectedNonce,
                exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
                iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                preferred_username = "alice_oidc",
                email = "alice@oidc.homelab.test",
                email_verified = true,
                name = "Alice OIDC Admin",
                groups = new[] { "managerr-admins" }
            });

            var headerB64 = Base64Url.Encode(Encoding.UTF8.GetBytes(headerJson));
            var payloadB64 = Base64Url.Encode(Encoding.UTF8.GetBytes(payloadJson));
            var signBytes = Encoding.UTF8.GetBytes($"{headerB64}.{payloadB64}");
            var sig = _rsa.SignData(signBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var sigB64 = Base64Url.Encode(sig);

            var idToken = $"{headerB64}.{payloadB64}.{sigB64}";

            var tokenResp = new
            {
                access_token = "mock-access-token-abc",
                token_type = "Bearer",
                id_token = idToken,
                expires_in = 3600
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(tokenResp), Encoding.UTF8, "application/json")
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public sealed class FakeSafeHttpClientFactory(HttpMessageHandler handler) : ISafeHttpClientFactory
{
    public HttpClient CreatePublicClient(bool ignoreSslErrors = false) => new(handler, disposeHandler: false);
    public HttpClient CreateHomelabClient(bool allowPrivateLan = true, bool ignoreSslErrors = false) => new(handler, disposeHandler: false);
    public void EnsureAddressPermitted(IPAddress address, string? targetHost, bool allowPrivateLan) { }
}

public sealed class MultiProviderAuthOidcPasskeyTests : IAsyncLifetime
{
    private readonly string _tempConfigDir = Path.Combine(Path.GetTempPath(), $"managerr-auth-test-{Guid.NewGuid():N}");
    private readonly RSA _rsa = RSA.Create(2048);
    private WebApplicationFactory<Program> _factory = null!;
    private FakePlexApiClient _fakePlex = new();
    private MockOidcHttpMessageHandler _mockOidc = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempConfigDir);
        _mockOidc = new MockOidcHttpMessageHandler(_rsa);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = _tempConfigDir,
                    ["Managerr:MasterKey"] = "integration-test-master-key-material-2026",
                    ["Managerr:Auth:Local:Enabled"] = "true",
                    ["Managerr:Auth:Passkeys:Enabled"] = "true",
                    ["Managerr:Auth:Passkeys:RpName"] = "Managerr Test",
                    ["Managerr:Auth:Oidc:Enabled"] = "true",
                    ["Managerr:Auth:Oidc:Issuer"] = "https://mock-oidc.homelab.test",
                    ["Managerr:Auth:Oidc:ClientId"] = "managerr-oidc-client",
                    ["Managerr:Auth:Oidc:ClientSecret"] = "mock-client-secret-xyz",
                    ["Managerr:Auth:Oidc:AdminGroups:0"] = "managerr-admins",
                    ["Managerr:Auth:Oidc:UserGroups:0"] = "managerr-users"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPlexApiClient>();
                services.AddSingleton<IPlexApiClient>(_fakePlex);

                services.RemoveAll<ISafeHttpClientFactory>();
                services.AddSingleton<ISafeHttpClientFactory>(new FakeSafeHttpClientFactory(_mockOidc));
            });
        });

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        _rsa.Dispose();
        if (Directory.Exists(_tempConfigDir))
        {
            try { Directory.Delete(_tempConfigDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void FileSecretConfigurationProvider_And_ConfigSourceInspector_FunctionCorrectly()
    {
        var tempSecretFile = Path.Combine(_tempConfigDir, "test_secret.txt");
        File.WriteAllText(tempSecretFile, "  super_secret_file_value_123  \r\n");

        var envVarName = $"Managerr__TestSecret_{Guid.NewGuid():N}_FILE";
        var unscopedVarName = $"UNSCOPED_OS_TEST_{Guid.NewGuid():N}_FILE";
        Environment.SetEnvironmentVariable(envVarName, tempSecretFile);
        Environment.SetEnvironmentVariable(unscopedVarName, "/nonexistent/os/cert.pem");

        try
        {
            var config = new ConfigurationBuilder()
                .AddFileSecrets()
                .Build();

            var expectedKey = envVarName[..^5].Replace("__", ":");
            Assert.Equal("  super_secret_file_value_123  ", config[expectedKey]);
            Assert.Null(config[unscopedVarName[..^5]]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVarName, null);
            Environment.SetEnvironmentVariable(unscopedVarName, null);
            try { File.Delete(tempSecretFile); } catch { /* ignore */ }
        }

        // ConfigSourceInspector test
        var jsonConfigFile = Path.Combine(_tempConfigDir, "managerr.json");
        File.WriteAllText(jsonConfigFile, """{"Managerr": {"Ui": {"AccentColor": "#123456"}}}""");

        var memConfig = new ConfigurationBuilder()
            .AddJsonFile(jsonConfigFile, optional: true)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Managerr:Auth:Oidc:Issuer"] = "https://env.example.com"
            })
            .Build();

        var inspector = new ConfigSourceInspector(memConfig, jsonConfigFile);
        Assert.Equal("environment", inspector.GetSource("Managerr:Auth:Oidc:Issuer"));
        Assert.Equal("file", inspector.GetSource("Managerr:Ui:AccentColor"));
        Assert.Equal("database", inspector.GetSource("Managerr:NonExistentSetting"));
    }

    [Fact]
    public async Task LocalAuth_OwnerClaim_AdminCreateUser_Login_Profile_And_PasswordChange_Flow()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        // 1. Providers endpoint before claim
        var providersBefore = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/providers");
        Assert.False(providersBefore.GetProperty("isClaimed").GetBoolean());
        Assert.True(providersBefore.GetProperty("local").GetProperty("enabled").GetBoolean());

        // 2. Claim owner via local user
        var claimReq = new
        {
            username = "owner_admin",
            email = "admin@example.com",
            displayName = "Owner Administrator",
            password = "Password12345!"
        };
        var claimResp = await client.PostAsJsonAsync("/api/v1/auth/local/claim-owner", claimReq);
        Assert.Equal(HttpStatusCode.OK, claimResp.StatusCode);

        var claimSetCookie = claimResp.Headers.GetValues("Set-Cookie").First();
        Assert.Contains("managerr_session=", claimSetCookie);
        var adminCookiePair = claimSetCookie.Split(';')[0];

        var claimJson = await claimResp.Content.ReadFromJsonAsync<JsonElement>();
        var adminToken = claimJson.GetProperty("sessionToken").GetString()!;
        var adminCsrf = claimJson.GetProperty("csrfToken").GetString()!;
        Assert.True(claimJson.GetProperty("user").GetProperty("isServerOwner").GetBoolean());
        var userRoles = claimJson.GetProperty("user").GetProperty("roles");
        Assert.Equal("system:owner", userRoles[0].GetProperty("key").GetString());

        // Calling claim-owner again must fail
        var secondClaim = await client.PostAsJsonAsync("/api/v1/auth/local/claim-owner", claimReq);
        Assert.Equal(HttpStatusCode.Conflict, secondClaim.StatusCode);

        // Providers endpoint after claim
        var providersAfter = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/providers");
        Assert.True(providersAfter.GetProperty("isClaimed").GetBoolean());

        // 3. Verify Database Migration 21 tables & columns
        var dbFactory = _factory.Services.GetRequiredService<ISqliteDbFactory>();
        await using (var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core))
        {
            var userRow = await conn.QuerySingleAsync<(long id, string username, string? displayName, string passkeyUserId, long? plexUserId)>(
                "SELECT id, username, display_name, passkey_user_id, plex_user_id FROM users WHERE username = 'owner_admin'");
            Assert.Equal("Owner Administrator", userRow.displayName);
            Assert.Null(userRow.plexUserId);
            Assert.Equal(22, userRow.passkeyUserId.Length);

            var identity = await conn.QuerySingleAsync<(string provider, string subjectId)>(
                "SELECT provider, subject_id FROM user_identities WHERE user_id = @UserId",
                new { UserId = userRow.id });
            Assert.Equal("local", identity.provider);
            Assert.Equal(userRow.id.ToString(), identity.subjectId);
        }

        // 4. Admin creates a second local user (Bob)
        var createBobMsg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/users/local")
        {
            Content = JsonContent.Create(new
            {
                username = "bob_user",
                email = "bob@example.com",
                displayName = "Bob Regular",
                password = "BobPassword123!"
            })
        };
        createBobMsg.Headers.Add("Cookie", adminCookiePair);
        createBobMsg.Headers.Add(ManagerrAuthConstants.CsrfHeaderName, adminCsrf);

        var createBobResp = await client.SendAsync(createBobMsg);
        Assert.Equal(HttpStatusCode.Created, createBobResp.StatusCode);
        var bobJson = await createBobResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("bob_user", bobJson.GetProperty("username").GetString());
        Assert.False(bobJson.GetProperty("isServerOwner").GetBoolean());

        // 5. Bob logs in via /api/v1/auth/local/login
        using var anonClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        // Invalid password
        var badLogin = await anonClient.PostAsJsonAsync("/api/v1/auth/local/login", new
        {
            username = "bob_user",
            password = "WrongPassword!"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, badLogin.StatusCode);

        // Correct password
        var goodLogin = await anonClient.PostAsJsonAsync("/api/v1/auth/local/login", new
        {
            username = "bob_user",
            password = "BobPassword123!"
        });
        Assert.Equal(HttpStatusCode.OK, goodLogin.StatusCode);
        var bobSetCookie = goodLogin.Headers.GetValues("Set-Cookie").First();
        Assert.Contains("managerr_session=", bobSetCookie);
        var bobCookiePair = bobSetCookie.Split(';')[0];
        var bobLoginJson = await goodLogin.Content.ReadFromJsonAsync<JsonElement>();
        var bobCsrf = bobLoginJson.GetProperty("csrfToken").GetString()!;

        // 6. Bob updates his profile
        var updateProfileMsg = new HttpRequestMessage(HttpMethod.Put, "/api/v1/auth/profile")
        {
            Content = JsonContent.Create(new
            {
                username = "bob_user",
                displayName = "Bob Updated",
                email = "bob.updated@example.com"
            })
        };
        updateProfileMsg.Headers.Add("Cookie", bobCookiePair);
        updateProfileMsg.Headers.Add(ManagerrAuthConstants.CsrfHeaderName, bobCsrf);

        var updateProfileResp = await anonClient.SendAsync(updateProfileMsg);
        Assert.Equal(HttpStatusCode.OK, updateProfileResp.StatusCode);
        var updatedBob = await updateProfileResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Bob Updated", updatedBob.GetProperty("displayName").GetString());
        Assert.Equal("bob.updated@example.com", updatedBob.GetProperty("email").GetString());

        // 7. Open a second session for Bob, then change Bob's password -> second session must be revoked while current session stays valid
        var secondSessionResp = await anonClient.PostAsJsonAsync("/api/v1/auth/local/login", new
        {
            username = "bob_user",
            password = "BobPassword123!"
        });
        Assert.Equal(HttpStatusCode.OK, secondSessionResp.StatusCode);
        var secondSessionJson = await secondSessionResp.Content.ReadFromJsonAsync<JsonElement>();
        var secondSessionBearer = secondSessionJson.GetProperty("sessionToken").GetString()!;

        var changePwMsg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/local/password")
        {
            Content = JsonContent.Create(new
            {
                currentPassword = "BobPassword123!",
                newPassword = "NewSecretBobPassword456!"
            })
        };
        changePwMsg.Headers.Add("Cookie", bobCookiePair);
        changePwMsg.Headers.Add(ManagerrAuthConstants.CsrfHeaderName, bobCsrf);

        var changePwResp = await anonClient.SendAsync(changePwMsg);
        Assert.Equal(HttpStatusCode.OK, changePwResp.StatusCode);

        // Other session must now be revoked (401), while active session remains valid (200)
        var revokedCheck = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        revokedCheck.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secondSessionBearer);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonClient.SendAsync(revokedCheck)).StatusCode);

        var activeCheck = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        activeCheck.Headers.Add("Cookie", bobCookiePair);
        Assert.Equal(HttpStatusCode.OK, (await anonClient.SendAsync(activeCheck)).StatusCode);

        // Bob verifies login with old password fails, new password succeeds
        var oldLoginRetry = await anonClient.PostAsJsonAsync("/api/v1/auth/local/login", new
        {
            username = "bob_user",
            password = "BobPassword123!"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLoginRetry.StatusCode);

        var newLoginRetry = await anonClient.PostAsJsonAsync("/api/v1/auth/local/login", new
        {
            username = "bob_user",
            password = "NewSecretBobPassword456!"
        });
        Assert.Equal(HttpStatusCode.OK, newLoginRetry.StatusCode);
    }

    [Fact]
    public async Task Oidc_FullPkceFlow_SignatureVerification_GroupMapping_And_SessionCreation()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        // 1. Start OIDC Login
        var loginResp = await client.GetAsync("/api/v1/auth/oidc/login");
        Assert.Equal(HttpStatusCode.Redirect, loginResp.StatusCode);
        var authUrl = loginResp.Headers.Location!.ToString();
        Assert.Contains("mock-oidc.homelab.test/oauth/authorize", authUrl);
        Assert.Contains("code_challenge=", authUrl);
        Assert.Contains("code_challenge_method=S256", authUrl);
        Assert.Contains("response_type=code", authUrl);
        Assert.Contains("client_id=managerr-oidc-client", authUrl);

        var query = System.Web.HttpUtility.ParseQueryString(loginResp.Headers.Location!.Query);
        var state = query["state"]!;
        var nonce = query["nonce"]!;
        Assert.False(string.IsNullOrWhiteSpace(state));
        Assert.False(string.IsNullOrWhiteSpace(nonce));
        _mockOidc.ExpectedNonce = nonce;

        var startCookies = loginResp.Headers.GetValues("Set-Cookie").ToList();
        var stateCookieHeader = startCookies.FirstOrDefault(c => c.StartsWith("managerr_oidc_state=", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"managerr_oidc_state cookie not found in: {string.Join("; ", startCookies)}");
        var stateCookiePair = stateCookieHeader.Split(';')[0];

        // 2a. Callback without state correlation cookie must fail (Login-CSRF guard) without burning challenge
        var noCookieCallback = await client.GetAsync($"/api/v1/auth/oidc/callback?code=mock-code-123&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.BadRequest, noCookieCallback.StatusCode);

        // 2b. Callback with matching state correlation cookie succeeds
        var cbReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/auth/oidc/callback?code=mock-code-123&state={Uri.EscapeDataString(state)}");
        cbReq.Headers.Add("Cookie", stateCookiePair);
        var callbackResp = await client.SendAsync(cbReq);
        Assert.Equal(HttpStatusCode.Redirect, callbackResp.StatusCode);
        var setCookieHeaders = callbackResp.Headers.GetValues("Set-Cookie").ToList();
        var sessionSetCookie = setCookieHeaders.FirstOrDefault(c => c.StartsWith("managerr_session=", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"managerr_session cookie not found in: {string.Join("; ", setCookieHeaders)}");
        var sessionCookiePair = sessionSetCookie.Split(';')[0];
        var rawToken = sessionCookiePair["managerr_session=".Length..];

        // 3. Verify Authenticated Session with Me endpoint using Bearer auth
        var meReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);
        var meResp = await client.SendAsync(meReq);
        Assert.Equal(HttpStatusCode.OK, meResp.StatusCode);

        var meJson = await meResp.Content.ReadFromJsonAsync<JsonElement>();
        var userObj = meJson.GetProperty("user");
        Assert.Equal("alice_oidc", userObj.GetProperty("username").GetString());
        Assert.Equal("Alice OIDC Admin", userObj.GetProperty("displayName").GetString());
        Assert.True(userObj.GetProperty("isServerOwner").GetBoolean());

        // 4. Verify test connection endpoint using the authenticated OIDC admin's token
        var testReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/settings/auth/oidc/test")
        {
            Content = JsonContent.Create(new { })
        };
        testReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);
        var testResp = await client.SendAsync(testReq);
        Assert.Equal(HttpStatusCode.OK, testResp.StatusCode);
        var testJson = await testResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(testJson.GetProperty("success").GetBoolean());
        Assert.Equal("https://mock-oidc.homelab.test", testJson.GetProperty("issuer").GetString());

        // 5. Verify user_identities table in DB
        var dbFactory = _factory.Services.GetRequiredService<ISqliteDbFactory>();
        await using (var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core))
        {
            var identity = await conn.QuerySingleAsync<(string provider, string subjectId)>(
                "SELECT provider, subject_id FROM user_identities WHERE provider = 'oidc'");
            Assert.Equal("oidc-sub-alice-001", identity.subjectId);
        }
    }

    [Fact]
    public async Task Passkey_Endpoints_And_UnknownCredential_Returns404NotFound()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        // 1. Anonymous Passkey login options
        var loginOptionsResp = await client.PostAsync("/api/v1/auth/passkeys/login/options", null);
        Assert.Equal(HttpStatusCode.OK, loginOptionsResp.StatusCode);
        var loginOptions = await loginOptionsResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(loginOptions.TryGetProperty("challenge", out var challengeProp));
        Assert.False(string.IsNullOrWhiteSpace(challengeProp.GetString()));

        // 2. Unknown credential assertion returns 404 ERR_PASSKEY_NOT_FOUND
        var dummyAssertion = new
        {
            id = "non-existent-credential-id",
            rawId = Base64Url.Encode(new byte[] { 1, 2, 3, 4 }),
            type = "public-key",
            response = new
            {
                authenticatorData = Base64Url.Encode(new byte[] { 1, 2, 3 }),
                clientDataJson = Base64Url.Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    type = "webauthn.get",
                    challenge = challengeProp.GetString(),
                    origin = "http://localhost"
                }))),
                signature = Base64Url.Encode(new byte[] { 1, 2, 3 })
            }
        };

        var verifyUnknownResp = await client.PostAsJsonAsync("/api/v1/auth/passkeys/login/verify", dummyAssertion);
        Assert.Equal(HttpStatusCode.NotFound, verifyUnknownResp.StatusCode);
        var unknownErrJson = await verifyUnknownResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ERR_PASSKEY_NOT_FOUND", unknownErrJson.GetProperty("error").GetString());

        // 3. Claim owner so we have an authenticated user for registration options and CRUD
        var claimResp = await client.PostAsJsonAsync("/api/v1/auth/local/claim-owner", new
        {
            username = "passkey_admin",
            password = "Password123!"
        });
        Assert.Equal(HttpStatusCode.OK, claimResp.StatusCode);
        var setCookie = claimResp.Headers.GetValues("Set-Cookie").First();
        var adminCookiePair = setCookie.Split(';')[0];
        var adminJson = await claimResp.Content.ReadFromJsonAsync<JsonElement>();
        var adminCsrf = adminJson.GetProperty("csrfToken").GetString()!;

        // 4. Registration options for authenticated user
        var regOptionsMsg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/passkeys/register/options")
        {
            Content = JsonContent.Create(new { promotion = false })
        };
        regOptionsMsg.Headers.Add("Cookie", adminCookiePair);
        regOptionsMsg.Headers.Add(ManagerrAuthConstants.CsrfHeaderName, adminCsrf);
        var regOptionsResp = await client.SendAsync(regOptionsMsg);
        Assert.Equal(HttpStatusCode.OK, regOptionsResp.StatusCode);
        var regJson = await regOptionsResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(regJson.TryGetProperty("challenge", out _));
        Assert.Equal("passkey_admin", regJson.GetProperty("user").GetProperty("name").GetString());

        // 5. Passkey CRUD: seed credential directly into DB
        var dbFactory = _factory.Services.GetRequiredService<ISqliteDbFactory>();
        var credId = Base64Url.Encode(new byte[] { 10, 20, 30, 40, 50 });
        await dbFactory.ExecuteWriteAsync(DatabaseKind.Core, (conn, tx) =>
            conn.ExecuteAsync(
                """
                INSERT INTO webauthn_credentials (
                    credential_id, user_id, public_key_b64, sign_count, transports_json, aaguid, name,
                    device_type, is_backed_up, registered_at, last_used_at
                ) VALUES (
                    @CredId, 1, @PublicKey, 0, '["usb"]', '00000000-0000-0000-0000-000000000000', 'Work YubiKey',
                    'singleDevice', 0, @RegisteredAt, NULL
                );
                """,
                new
                {
                    CredId = credId,
                    PublicKey = Base64Url.Encode(new byte[] { 0x01, 0x02, 0x03 }),
                    RegisteredAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                },
                transaction: tx));

        // List credentials
        var listMsg = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/passkeys");
        listMsg.Headers.Add("Cookie", adminCookiePair);
        var listResp = await client.SendAsync(listMsg);
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var listJson = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        var credsArray = listJson.GetProperty("credentials");
        Assert.Equal(1, credsArray.GetArrayLength());
        Assert.Equal(credId, credsArray[0].GetProperty("id").GetString());
        Assert.Equal("Work YubiKey", credsArray[0].GetProperty("name").GetString());

        // Rename credential
        var renameMsg = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/auth/passkeys/{credId}")
        {
            Content = JsonContent.Create(new { name = "Renamed YubiKey 5C" })
        };
        renameMsg.Headers.Add("Cookie", adminCookiePair);
        renameMsg.Headers.Add(ManagerrAuthConstants.CsrfHeaderName, adminCsrf);
        var renameResp = await client.SendAsync(renameMsg);
        Assert.Equal(HttpStatusCode.OK, renameResp.StatusCode);
        var renameJson = await renameResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Renamed YubiKey 5C", renameJson.GetProperty("name").GetString());

        // Delete credential
        var deleteMsg = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/auth/passkeys/{credId}");
        deleteMsg.Headers.Add("Cookie", adminCookiePair);
        deleteMsg.Headers.Add(ManagerrAuthConstants.CsrfHeaderName, adminCsrf);
        var deleteResp = await client.SendAsync(deleteMsg);
        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);

        // List credentials is now empty
        var listEmptyMsg = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/passkeys");
        listEmptyMsg.Headers.Add("Cookie", adminCookiePair);
        var listEmptyResp = await client.SendAsync(listEmptyMsg);
        Assert.Equal(HttpStatusCode.OK, listEmptyResp.StatusCode);
        var listEmptyJson = await listEmptyResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, listEmptyJson.GetProperty("credentials").GetArrayLength());
    }

    [Fact]
    public async Task SettingsAuth_ReturnsEffectiveConfig_And_PreventsLockedSettingModification()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        // Claim owner first
        var claimResp = await client.PostAsJsonAsync("/api/v1/auth/local/claim-owner", new
        {
            username = "settings_admin",
            password = "Password123!"
        });
        Assert.Equal(HttpStatusCode.OK, claimResp.StatusCode);
        var setCookie = claimResp.Headers.GetValues("Set-Cookie").First();
        var adminCookiePair = setCookie.Split(';')[0];
        var adminJson = await claimResp.Content.ReadFromJsonAsync<JsonElement>();
        var adminCsrf = adminJson.GetProperty("csrfToken").GetString()!;

        // 1. GET /api/v1/settings/auth
        var getSettingsMsg = new HttpRequestMessage(HttpMethod.Get, "/api/v1/settings/auth");
        getSettingsMsg.Headers.Add("Cookie", adminCookiePair);
        var getSettingsResp = await client.SendAsync(getSettingsMsg);
        Assert.Equal(HttpStatusCode.OK, getSettingsResp.StatusCode);

        var settingsJson = await getSettingsResp.Content.ReadFromJsonAsync<JsonElement>();
        var managedBy = settingsJson.GetProperty("managedBy");
        Assert.Equal("environment", managedBy.GetProperty("oidc.issuer").GetString());

        // 2. Attempt to modify locked oidc.issuer setting -> must return 400 with ERR_SETTING_LOCKED
        var putLockedMsg = new HttpRequestMessage(HttpMethod.Put, "/api/v1/settings/auth")
        {
            Content = JsonContent.Create(new
            {
                oidc = new
                {
                    issuer = "https://different-issuer.attacker.com"
                }
            })
        };
        putLockedMsg.Headers.Add("Cookie", adminCookiePair);
        putLockedMsg.Headers.Add(ManagerrAuthConstants.CsrfHeaderName, adminCsrf);
        var putLockedResp = await client.SendAsync(putLockedMsg);
        Assert.Equal(HttpStatusCode.BadRequest, putLockedResp.StatusCode);
        var lockedErr = await putLockedResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ERR_SETTING_LOCKED", lockedErr.GetProperty("code").GetString());
    }
}
