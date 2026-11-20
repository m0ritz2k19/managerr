using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Managerr.Common.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Managerr.Phase6.Tests;

public sealed class ThemeSettingsEndpointTests : IAsyncLifetime
{
    private readonly string _tempConfigDir = Path.Combine(Path.GetTempPath(), $"managerr-p6-theme-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _ownerSessionToken = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempConfigDir);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = _tempConfigDir,
                    ["Managerr:MasterKey"] = "integration-test-master-key-material-2026",
                    ["Managerr:SchedulerEnabled"] = "false",
                    ["AllowMissingPrunePackageData"] = "true",
                });
            });
        });

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var claimResp = await _client.PostAsJsonAsync("/api/v1/auth/local/claim-owner", new
        {
            username = "admin",
            password = "AdminPassword123!",
            displayName = "Admin User"
        });
        claimResp.EnsureSuccessStatusCode();
        var claimJson = await claimResp.Content.ReadFromJsonAsync<JsonElement>();
        _ownerSessionToken = claimJson.GetProperty("sessionToken").GetString()!;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        if (Directory.Exists(_tempConfigDir))
        {
            try { Directory.Delete(_tempConfigDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private HttpRequestMessage CreateAdminRequest(HttpMethod method, string uri, object? body = null)
    {
        var req = new HttpRequestMessage(method, uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _ownerSessionToken);
        if (body != null)
        {
            req.Content = JsonContent.Create(body);
        }
        return req;
    }

    [Fact]
    public async Task DefaultProvidersEndpoint_ReturnsSeafoamJadeAndDefaultAccent()
    {
        var resp = await _client.GetAsync("/api/v1/auth/providers");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("ui", out var ui));
        Assert.Equal("seafoam_jade", ui.GetProperty("colorScheme").GetString());
        Assert.Equal("#2dd4bf", ui.GetProperty("accentColor").GetString());
        Assert.Equal("dark", ui.GetProperty("defaultTheme").GetString());
        Assert.Equal(JsonValueKind.Null, ui.GetProperty("customTheme").ValueKind);
    }

    [Fact]
    public async Task PutGeneralSettings_SavesAllFivePresets_AndPreservesExistingInstanceName()
    {
        // 1. Set a custom instance name
        using (var setInstanceReq = CreateAdminRequest(HttpMethod.Put, "/api/v1/settings/general", new
        {
            instanceName = "Custom Cluster Name",
            colorScheme = "seafoam_jade",
        }))
        {
            var setResp = await _client.SendAsync(setInstanceReq);
            Assert.Equal(HttpStatusCode.OK, setResp.StatusCode);
            var setJson = await setResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Custom Cluster Name", setJson.GetProperty("instanceName").GetString());
            Assert.Equal("seafoam_jade", setJson.GetProperty("ui").GetProperty("colorScheme").GetString());
        }

        // 2. Cycle through each of the 5 presets with instanceName omitted
        var presets = new[]
        {
            "royal_amethyst",
            "cyber_chartreuse",
            "brushed_copper",
            "nordic_periwinkle",
            "seafoam_jade",
        };

        foreach (var preset in presets)
        {
            using var putReq = CreateAdminRequest(HttpMethod.Put, "/api/v1/settings/general", new
            {
                colorScheme = preset,
            });

            var putResp = await _client.SendAsync(putReq);
            Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

            var putJson = await putResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Custom Cluster Name", putJson.GetProperty("instanceName").GetString());
            Assert.Equal(preset, putJson.GetProperty("ui").GetProperty("colorScheme").GetString());

            // Verify GET /api/v1/settings/general also preserves instanceName and returns preset
            using var getReq = CreateAdminRequest(HttpMethod.Get, "/api/v1/settings/general");
            var getResp = await _client.SendAsync(getReq);
            Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

            var getJson = await getResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Custom Cluster Name", getJson.GetProperty("instanceName").GetString());
            Assert.Equal(preset, getJson.GetProperty("ui").GetProperty("colorScheme").GetString());
        }
    }

    [Fact]
    public async Task PutGeneralSettings_CustomThemeRoundTrips_SyncsAccent_AndClears()
    {
        var validTokens = new Dictionary<string, string>
        {
            ["headerBg"] = "#1a1b26",
            ["headerBgEnd"] = "#16161e",
            ["headerBorder"] = "#24283b",
            ["sidebarBg"] = "#1f2335",
            ["sidebarBorder"] = "#292e42",
            ["sidebarActiveBg"] = "#3b4261",
            ["headingBg"] = "#1a1b26",
            ["toolbarBg"] = "#1a1b26",
            ["filterBarBg"] = "#1a1b26",
            ["pageBg"] = "#24283b",
            ["cardBg"] = "#1f2335",
            ["cardBorder"] = "#292e42",
            ["modalBg"] = "#1f2335",
            ["inputBg"] = "#16161e",
            ["accent"] = "#ff007f",
            ["logoSecondary"] = "#7aa2f7",
            ["primary"] = "#ff007f",
            ["primaryHover"] = "#ff3399",
            ["primaryBorder"] = "#e60073"
        };

        var customPayload = new
        {
            colorScheme = "custom",
            customTheme = new
            {
                schema = "managerr-theme/v1",
                name = "My Cyberpunk",
                baseMode = "dark",
                tokens = validTokens
            }
        };

        // 1. PUT custom theme (without explicit accentColor -> should sync accent to #ff007f)
        using (var putReq = CreateAdminRequest(HttpMethod.Put, "/api/v1/settings/general", customPayload))
        {
            var putResp = await _client.SendAsync(putReq);
            Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

            var putJson = await putResp.Content.ReadFromJsonAsync<JsonElement>();
            var ui = putJson.GetProperty("ui");
            Assert.Equal("custom", ui.GetProperty("colorScheme").GetString());
            Assert.Equal("#ff007f", ui.GetProperty("accentColor").GetString());

            var ct = ui.GetProperty("customTheme");
            Assert.Equal("managerr-theme/v1", ct.GetProperty("schema").GetString());
            Assert.Equal("My Cyberpunk", ct.GetProperty("name").GetString());
            Assert.Equal("dark", ct.GetProperty("baseMode").GetString());

            var tokens = ct.GetProperty("tokens");
            Assert.Equal("#1a1b26", tokens.GetProperty("headerBg").GetString());
            Assert.Equal("#ff007f", tokens.GetProperty("accent").GetString());
            Assert.Equal("#e60073", tokens.GetProperty("primaryBorder").GetString());
        }

        // 2. Round-trip in GET /api/v1/settings/general
        using (var getReq = CreateAdminRequest(HttpMethod.Get, "/api/v1/settings/general"))
        {
            var getResp = await _client.SendAsync(getReq);
            Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

            var getJson = await getResp.Content.ReadFromJsonAsync<JsonElement>();
            var ui = getJson.GetProperty("ui");
            Assert.Equal("custom", ui.GetProperty("colorScheme").GetString());
            Assert.Equal("#ff007f", ui.GetProperty("accentColor").GetString());

            var ct = ui.GetProperty("customTheme");
            Assert.Equal("managerr-theme/v1", ct.GetProperty("schema").GetString());
            Assert.Equal("My Cyberpunk", ct.GetProperty("name").GetString());
            Assert.Equal("#ff007f", ct.GetProperty("tokens").GetProperty("accent").GetString());
        }

        // 3. Round-trip in anonymous GET /api/v1/auth/providers
        var anonResp = await _client.GetAsync("/api/v1/auth/providers");
        Assert.Equal(HttpStatusCode.OK, anonResp.StatusCode);
        var anonJson = await anonResp.Content.ReadFromJsonAsync<JsonElement>();
        var anonUi = anonJson.GetProperty("ui");
        Assert.Equal("custom", anonUi.GetProperty("colorScheme").GetString());
        Assert.Equal("#ff007f", anonUi.GetProperty("accentColor").GetString());
        Assert.Equal("managerr-theme/v1", anonUi.GetProperty("customTheme").GetProperty("schema").GetString());
        Assert.Equal("My Cyberpunk", anonUi.GetProperty("customTheme").GetProperty("name").GetString());

        // 4. Clear custom theme
        using (var clearReq = CreateAdminRequest(HttpMethod.Put, "/api/v1/settings/general", new
        {
            clearCustomTheme = true,
            colorScheme = "seafoam_jade"
        }))
        {
            var clearResp = await _client.SendAsync(clearReq);
            Assert.Equal(HttpStatusCode.OK, clearResp.StatusCode);

            var clearJson = await clearResp.Content.ReadFromJsonAsync<JsonElement>();
            var ui = clearJson.GetProperty("ui");
            Assert.Equal("seafoam_jade", ui.GetProperty("colorScheme").GetString());
            Assert.Equal(JsonValueKind.Null, ui.GetProperty("customTheme").ValueKind);
        }

        // 5. Verify cleared in GET /api/v1/auth/providers
        var anonCleared = await _client.GetAsync("/api/v1/auth/providers");
        var anonClearedJson = await anonCleared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, anonClearedJson.GetProperty("ui").GetProperty("customTheme").ValueKind);
    }

    [Theory]
    [InlineData("neon_pink")]
    [InlineData("seafoam_jade; DROP TABLE")]
    [InlineData("random_value")]
    [InlineData("")]
    public async Task PutGeneralSettings_InvalidColorScheme_Returns400(string invalidScheme)
    {
        using var req = CreateAdminRequest(HttpMethod.Put, "/api/v1/settings/general", new
        {
            colorScheme = invalidScheme,
        });

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ERR_VALIDATION_FAILED", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task PutGeneralSettings_CssOrXssInjectionInToken_Returns400()
    {
        var injectionTokens = new Dictionary<string, string>
        {
            ["accent"] = "#2dd4bf;background:url(x)",
            ["pageBg"] = "#1f2335"
        };

        using var req = CreateAdminRequest(HttpMethod.Put, "/api/v1/settings/general", new
        {
            customTheme = new
            {
                name = "Malicious Theme",
                tokens = injectionTokens
            }
        });

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ERR_VALIDATION_FAILED", problem.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#fff")]
    [InlineData("rgb(0,0,0)")]
    [InlineData("#zzzzzz")]
    [InlineData("<script>alert(1)</script>")]
    public async Task PutGeneralSettings_NonHexTokenValue_Returns400(string badColor)
    {
        var tokens = new Dictionary<string, string>
        {
            ["accent"] = badColor
        };

        using var req = CreateAdminRequest(HttpMethod.Put, "/api/v1/settings/general", new
        {
            customTheme = new
            {
                tokens
            }
        });

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData("__proto__")]
    [InlineData("evilKey")]
    [InlineData("constructor")]
    [InlineData("HeaderBg")] // case sensitive
    public async Task PutGeneralSettings_UnknownTokenKey_Returns400(string unknownKey)
    {
        var tokens = new Dictionary<string, string>
        {
            [unknownKey] = "#2dd4bf"
        };

        using var req = CreateAdminRequest(HttpMethod.Put, "/api/v1/settings/general", new
        {
            customTheme = new
            {
                tokens
            }
        });

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ERR_VALIDATION_FAILED", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task PutGeneralSettings_EmptyTokens_Returns400()
    {
        using var req = CreateAdminRequest(HttpMethod.Put, "/api/v1/settings/general", new
        {
            customTheme = new
            {
                tokens = new Dictionary<string, string>()
            }
        });

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PutGeneralSettings_InvalidBaseMode_Returns400()
    {
        var tokens = new Dictionary<string, string>
        {
            ["accent"] = "#2dd4bf"
        };

        using var req = CreateAdminRequest(HttpMethod.Put, "/api/v1/settings/general", new
        {
            customTheme = new
            {
                baseMode = "matrix_mode",
                tokens
            }
        });

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
