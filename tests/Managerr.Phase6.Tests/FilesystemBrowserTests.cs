using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Managerr.Host.Auth;
using Managerr.Host.Services;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Managerr.Phase6.Tests;

public sealed class FilesystemBrowserTests : IDisposable
{
    private readonly string _testRoot;

    public FilesystemBrowserTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"managerr-fs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        TryDeleteDir(_testRoot);
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore test cleanup exceptions
        }
    }

    private WebApplicationFactory<Program> CreateWebFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = Path.Combine(_testRoot, "config"),
                    ["Managerr:MasterKey"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                    ["Managerr:SchedulerEnabled"] = "false",
                    ["AllowMissingPrunePackageData"] = "true",
                });
            });
        });

    private static async Task<HttpClient> CreateClientWithScopesAsync(WebApplicationFactory<Program> factory, params string[] scopes)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IPlexUserService>();
        var auth = scope.ServiceProvider.GetRequiredService<IPlexAuthAndSessionService>();

        var owner = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
            PlexUserId: 1,
            PlexUuid: "test-owner-uuid",
            Username: "testowner",
            Email: "owner@test.local",
            AvatarUrl: null,
            IsServerOwner: true,
            IsHomeUser: true,
            IsActive: true,
            SharedPlexSectionIds: ["1", "2", "3", "4"]));

        var key = await auth.CreateApiKeyAsync(owner.Id, $"Key-{string.Join("-", scopes)}-{Guid.NewGuid():N}", scopes.ToList());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.RawTokenOnce);
        return client;
    }

    [Fact]
    public void RequireAnyScope_ThrowsArgumentException_WhenNoScopesProvided()
    {
        IEndpointConventionBuilder builder = new DummyEndpointBuilder();
        Assert.Throws<ArgumentException>(() => builder.RequireAnyScope());
        Assert.Throws<ArgumentException>(() => builder.RequireAnyScope((string[])null!));
    }

    private sealed class DummyEndpointBuilder : IEndpointConventionBuilder
    {
        public void Add(Action<EndpointBuilder> convention) { }
    }

    [Fact]
    public async Task BrowseAsync_NavigatesFoldersAndFiles_WithSortingAndParent()
    {
        var browseDir = Path.Combine(_testRoot, "browse_sample");
        Directory.CreateDirectory(browseDir);
        Directory.CreateDirectory(Path.Combine(browseDir, "zeta_folder"));
        Directory.CreateDirectory(Path.Combine(browseDir, "alpha_folder"));
        Directory.CreateDirectory(Path.Combine(browseDir, ".hidden_folder"));

        File.WriteAllText(Path.Combine(browseDir, "video_b.mkv"), "sample-video-bytes");
        File.WriteAllText(Path.Combine(browseDir, "audio_a.flac"), "sample-audio");
        File.WriteAllText(Path.Combine(browseDir, ".hidden_file.txt"), "secret");

        using var factory = CreateWebFactory();
        using var client = await CreateClientWithScopesAsync(factory, PermissionScopes.CatalogWrite);

        // 1. Browse folders only (includeFiles = false)
        var respFolders = await client.GetAsync($"/api/v1/filesystem?path={Uri.EscapeDataString(browseDir)}&includeFiles=false");
        Assert.Equal(HttpStatusCode.OK, respFolders.StatusCode);
        var jsonFolders = (await respFolders.Content.ReadFromJsonAsync<JsonObject>())!;

        Assert.Equal(browseDir.Replace('\\', '/'), jsonFolders["currentPath"]!.GetValue<string>());
        Assert.Equal(_testRoot.Replace('\\', '/'), jsonFolders["parent"]!.GetValue<string>());

        var dirsOnly = jsonFolders["directories"]!.AsArray();
        Assert.Equal(2, dirsOnly.Count);
        Assert.Equal("alpha_folder", dirsOnly[0]!["name"]!.GetValue<string>());
        Assert.Equal("folder", dirsOnly[0]!["type"]!.GetValue<string>());
        Assert.Equal("zeta_folder", dirsOnly[1]!["name"]!.GetValue<string>());

        var filesOnly = jsonFolders["files"]!.AsArray();
        Assert.Empty(filesOnly);

        // 2. Browse with files (includeFiles = true)
        var respFiles = await client.GetAsync($"/api/v1/filesystem?path={Uri.EscapeDataString(browseDir)}&includeFiles=true");
        Assert.Equal(HttpStatusCode.OK, respFiles.StatusCode);
        var jsonFiles = (await respFiles.Content.ReadFromJsonAsync<JsonObject>())!;

        var filesArray = jsonFiles["files"]!.AsArray();
        Assert.Equal(2, filesArray.Count);
        Assert.Equal("audio_a.flac", filesArray[0]!["name"]!.GetValue<string>());
        Assert.Equal(".flac", filesArray[0]!["extension"]!.GetValue<string>());
        Assert.Equal("file", filesArray[0]!["type"]!.GetValue<string>());
        Assert.Equal("video_b.mkv", filesArray[1]!["name"]!.GetValue<string>());
        Assert.Equal(".mkv", filesArray[1]!["extension"]!.GetValue<string>());
    }

    [Fact]
    public async Task BrowseAsync_NonExistentSubpathFallback_WalksUpCleanly()
    {
        var baseDir = Path.Combine(_testRoot, "movies");
        Directory.CreateDirectory(baseDir);
        var nonExistentDeep = Path.Combine(baseDir, "nested", "nonexistent", "path");

        using var factory = CreateWebFactory();
        using var client = await CreateClientWithScopesAsync(factory, PermissionScopes.CatalogWrite);

        var resp = await client.GetAsync($"/api/v1/filesystem?path={Uri.EscapeDataString(nonExistentDeep)}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = (await resp.Content.ReadFromJsonAsync<JsonObject>())!;

        Assert.Equal(baseDir.Replace('\\', '/'), json["currentPath"]!.GetValue<string>());
        Assert.Equal(_testRoot.Replace('\\', '/'), json["parent"]!.GetValue<string>());

        // Non existent from root falls back to /
        var nonExistentRoot = "/some-random-safe-root-candidate-xyz";
        var respRoot = await client.GetAsync($"/api/v1/filesystem?path={Uri.EscapeDataString(nonExistentRoot)}");
        Assert.Equal(HttpStatusCode.OK, respRoot.StatusCode);
        var jsonRoot = (await respRoot.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("/", jsonRoot["currentPath"]!.GetValue<string>());
        Assert.Null(jsonRoot["parent"]);
    }

    [Fact]
    public async Task BrowseAsync_SecurityBlocks_RestrictsSystemPathsAndSymlinks()
    {
        var browseDir = Path.Combine(_testRoot, "symlink_test");
        Directory.CreateDirectory(browseDir);
        var safeSub = Path.Combine(browseDir, "safe_folder");
        Directory.CreateDirectory(safeSub);

        var symlinkToEtc = Path.Combine(browseDir, "link_to_etc");
        var symlinkToConfig = Path.Combine(browseDir, "link_to_config");

        try
        {
            Directory.CreateSymbolicLink(symlinkToEtc, "/etc");
            Directory.CreateSymbolicLink(symlinkToConfig, "/config");
        }
        catch
        {
            // If OS environment restricts link creation, skip symlink creation assertions
        }

        using var factory = CreateWebFactory();
        using var client = await CreateClientWithScopesAsync(factory, PermissionScopes.CatalogWrite);

        // 1. Browsing browseDir omits symlinks to /etc or /config
        var listResp = await client.GetAsync($"/api/v1/filesystem?path={Uri.EscapeDataString(browseDir)}");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var listJson = (await listResp.Content.ReadFromJsonAsync<JsonObject>())!;
        var dirs = listJson["directories"]!.AsArray();
        Assert.DoesNotContain(dirs, d => d!["name"]!.GetValue<string>() == "link_to_etc");
        Assert.DoesNotContain(dirs, d => d!["name"]!.GetValue<string>() == "link_to_config");
        Assert.Contains(dirs, d => d!["name"]!.GetValue<string>() == "safe_folder");

        // 2. Direct browse to system paths returns 403 Forbidden
        string[] forbiddenPaths = ["/etc", "/config", "/proc", "/root", "/sys"];
        foreach (var forbidden in forbiddenPaths)
        {
            var fResp = await client.GetAsync($"/api/v1/filesystem?path={Uri.EscapeDataString(forbidden)}");
            Assert.Equal(HttpStatusCode.Forbidden, fResp.StatusCode);
        }

        // 3. Direct browse to symlink pointing to /etc returns 403 Forbidden
        if (File.Exists(symlinkToEtc) || Directory.Exists(symlinkToEtc))
        {
            var symResp = await client.GetAsync($"/api/v1/filesystem?path={Uri.EscapeDataString(symlinkToEtc)}");
            Assert.Equal(HttpStatusCode.Forbidden, symResp.StatusCode);
        }

        // 4. Non-existent child under forbidden path rejects directly (never walks up!)
        var forbiddenChild = await client.GetAsync("/api/v1/filesystem?path=/config/managerr.db");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenChild.StatusCode);

        // 5. Relative path ?path=etc/passwd returns 400 BadRequest
        var relResp = await client.GetAsync("/api/v1/filesystem?path=etc/passwd");
        Assert.Equal(HttpStatusCode.BadRequest, relResp.StatusCode);

        // 6. Dynamic ConfigDirectory returns 403 Forbidden
        var dynamicConfigDir = Path.Combine(_testRoot, "config");
        Directory.CreateDirectory(dynamicConfigDir);
        var dynConfigResp = await client.GetAsync($"/api/v1/filesystem?path={Uri.EscapeDataString(dynamicConfigDir)}");
        Assert.Equal(HttpStatusCode.Forbidden, dynConfigResp.StatusCode);

        // 7. Hidden dot-folder ?path=/tmp/<guid>/.ssh returns 403 Forbidden
        var dotSsh = Path.Combine(browseDir, ".ssh");
        Directory.CreateDirectory(dotSsh);
        var dotSshResp = await client.GetAsync($"/api/v1/filesystem?path={Uri.EscapeDataString(dotSsh)}");
        Assert.Equal(HttpStatusCode.Forbidden, dotSshResp.StatusCode);
    }

    [Fact]
    public async Task CreateDirectoryAsync_ValidatesInputsAndSecurity()
    {
        var parentDir = Path.Combine(_testRoot, "parent_target");
        Directory.CreateDirectory(parentDir);

        using var factory = CreateWebFactory();
        using var client = await CreateClientWithScopesAsync(factory, PermissionScopes.CatalogWrite);

        // 1. Safe creation
        var newSafePath = Path.Combine(parentDir, "my_new_library_dir");
        var createResp = await client.PostAsJsonAsync("/api/v1/filesystem/directories", new { path = newSafePath });
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var createJson = (await createResp.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(newSafePath.Replace('\\', '/'), createJson["path"]!.GetValue<string>());
        Assert.True(createJson["created"]!.GetValue<bool>());
        Assert.True(Directory.Exists(newSafePath));

        // 2. Folder with ellipsis like 'Once Upon a Time... in Hollywood (2019)' succeeds
        var ellipsisPath = Path.Combine(parentDir, "Once Upon a Time... in Hollywood (2019)");
        var ellipsisResp = await client.PostAsJsonAsync("/api/v1/filesystem/directories", new { path = ellipsisPath });
        Assert.Equal(HttpStatusCode.OK, ellipsisResp.StatusCode);
        var ellipsisJson = (await ellipsisResp.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True(ellipsisJson["created"]!.GetValue<bool>());
        Assert.True(Directory.Exists(ellipsisPath));

        // 3. Idempotent repeat creation returns created: false
        var repeatResp = await client.PostAsJsonAsync("/api/v1/filesystem/directories", new { path = newSafePath });
        Assert.Equal(HttpStatusCode.OK, repeatResp.StatusCode);
        var repeatJson = (await repeatResp.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.False(repeatJson["created"]!.GetValue<bool>());

        // 4. Reject /config/evil -> 403 Forbidden
        var evilConfigResp = await client.PostAsJsonAsync("/api/v1/filesystem/directories", new { path = "/config/evil" });
        Assert.Equal(HttpStatusCode.Forbidden, evilConfigResp.StatusCode);

        // 5. Reject .. traversal -> 400 BadRequest
        var evilTraversalResp = await client.PostAsJsonAsync("/api/v1/filesystem/directories", new { path = $"{parentDir}/../evil" });
        Assert.Equal(HttpStatusCode.BadRequest, evilTraversalResp.StatusCode);

        // 6. Reject creating directly under container root '/' -> 400 BadRequest
        var evilRootResp = await client.PostAsJsonAsync("/api/v1/filesystem/directories", new { path = "/brand_new_root_mount" });
        Assert.Equal(HttpStatusCode.BadRequest, evilRootResp.StatusCode);

        // 7. Reject relative path -> 400 BadRequest
        var relResp = await client.PostAsJsonAsync("/api/v1/filesystem/directories", new { path = "relative/subfolder" });
        Assert.Equal(HttpStatusCode.BadRequest, relResp.StatusCode);

        // 8. Reject empty path -> 400 BadRequest
        var emptyResp = await client.PostAsJsonAsync("/api/v1/filesystem/directories", new { path = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, emptyResp.StatusCode);

        // 9. Reject hidden dot directory -> 400 BadRequest
        var dotResp = await client.PostAsJsonAsync("/api/v1/filesystem/directories", new { path = $"{parentDir}/.hidden" });
        Assert.Equal(HttpStatusCode.BadRequest, dotResp.StatusCode);
    }

    [Fact]
    public async Task Rbac_EnforcesScopesAcrossFilesystemEndpoints()
    {
        var testDir = Path.Combine(_testRoot, "rbac_test");
        Directory.CreateDirectory(testDir);

        using var factory = CreateWebFactory();

        // 1. Unauthenticated -> 401
        using var anonClient = factory.CreateClient();
        var anonGet = await anonClient.GetAsync("/api/v1/filesystem");
        Assert.Equal(HttpStatusCode.Unauthorized, anonGet.StatusCode);

        var anonPost = await anonClient.PostAsJsonAsync("/api/v1/filesystem/directories", new { path = $"{testDir}/anon" });
        Assert.Equal(HttpStatusCode.Unauthorized, anonPost.StatusCode);

        var anonSonarr = await anonClient.GetAsync("/compat/sonarr/api/v3/filesystem");
        Assert.Equal(HttpStatusCode.Unauthorized, anonSonarr.StatusCode);

        var anonRadarr = await anonClient.GetAsync("/compat/radarr/api/v3/filesystem");
        Assert.Equal(HttpStatusCode.Unauthorized, anonRadarr.StatusCode);

        // 2. User with only catalog:read -> 403 on all filesystem endpoints
        using var readClient = await CreateClientWithScopesAsync(factory, PermissionScopes.CatalogRead);
        var readGet = await readClient.GetAsync("/api/v1/filesystem");
        Assert.Equal(HttpStatusCode.Forbidden, readGet.StatusCode);

        var readPost = await readClient.PostAsJsonAsync("/api/v1/filesystem/directories", new { path = $"{testDir}/read" });
        Assert.Equal(HttpStatusCode.Forbidden, readPost.StatusCode);

        // 3. User with queue:manage -> 200 on GET /api/v1/filesystem, but 403 on POST /directories
        using var queueClient = await CreateClientWithScopesAsync(factory, PermissionScopes.QueueManage);
        var queueGet = await queueClient.GetAsync("/api/v1/filesystem");
        Assert.Equal(HttpStatusCode.OK, queueGet.StatusCode);

        var queuePost = await queueClient.PostAsJsonAsync("/api/v1/filesystem/directories", new { path = $"{testDir}/queue" });
        Assert.Equal(HttpStatusCode.Forbidden, queuePost.StatusCode);

        // 4. User with downloaders:write -> 200 on GET /api/v1/filesystem, 403 on POST /directories
        using var downClient = await CreateClientWithScopesAsync(factory, PermissionScopes.DownloadersWrite);
        var downGet = await downClient.GetAsync("/api/v1/filesystem");
        Assert.Equal(HttpStatusCode.OK, downGet.StatusCode);

        var downPost = await downClient.PostAsJsonAsync("/api/v1/filesystem/directories", new { path = $"{testDir}/down" });
        Assert.Equal(HttpStatusCode.Forbidden, downPost.StatusCode);

        // 5. User with migration:execute -> 200 on both GET and POST
        using var migClient = await CreateClientWithScopesAsync(factory, PermissionScopes.MigrationExecute);
        var migGet = await migClient.GetAsync($"/api/v1/filesystem?path={Uri.EscapeDataString(testDir)}");
        Assert.Equal(HttpStatusCode.OK, migGet.StatusCode);

        var migPost = await migClient.PostAsJsonAsync("/api/v1/filesystem/directories", new { path = $"{testDir}/mig_folder" });
        Assert.Equal(HttpStatusCode.OK, migPost.StatusCode);

        // 6. User with catalog:write -> 200 on both GET and POST
        using var writeClient = await CreateClientWithScopesAsync(factory, PermissionScopes.CatalogWrite);
        var writeGet = await writeClient.GetAsync($"/api/v1/filesystem?path={Uri.EscapeDataString(testDir)}");
        Assert.Equal(HttpStatusCode.OK, writeGet.StatusCode);

        var writePost = await writeClient.PostAsJsonAsync("/api/v1/filesystem/directories", new { path = $"{testDir}/write_folder" });
        Assert.Equal(HttpStatusCode.OK, writePost.StatusCode);

        // 7. User with system:admin -> 200 on both GET and POST
        using var adminClient = await CreateClientWithScopesAsync(factory, PermissionScopes.SystemAdmin);
        var adminGet = await adminClient.GetAsync($"/api/v1/filesystem?path={Uri.EscapeDataString(testDir)}");
        Assert.Equal(HttpStatusCode.OK, adminGet.StatusCode);

        var adminPost = await adminClient.PostAsJsonAsync("/api/v1/filesystem/directories", new { path = $"{testDir}/admin_folder" });
        Assert.Equal(HttpStatusCode.OK, adminPost.StatusCode);

        // 8. Compat Sonarr & Radarr scopes
        using var sonarrClient = await CreateClientWithScopesAsync(factory, PermissionScopes.CompatSonarr);
        var sonarrResp = await sonarrClient.GetAsync($"/compat/sonarr/api/v3/filesystem?path={Uri.EscapeDataString(testDir)}");
        Assert.Equal(HttpStatusCode.OK, sonarrResp.StatusCode);
        var sonarrJson = (await sonarrResp.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.NotNull(sonarrJson["parent"]);
        Assert.NotNull(sonarrJson["directories"]);
        Assert.NotNull(sonarrJson["files"]);

        // Sonarr client forbidden on Radarr endpoint
        var sonarrOnRadarr = await sonarrClient.GetAsync("/compat/radarr/api/v3/filesystem");
        Assert.Equal(HttpStatusCode.Forbidden, sonarrOnRadarr.StatusCode);

        using var radarrClient = await CreateClientWithScopesAsync(factory, PermissionScopes.CompatRadarr);
        var radarrResp = await radarrClient.GetAsync($"/compat/radarr/api/v3/filesystem?path={Uri.EscapeDataString(testDir)}");
        Assert.Equal(HttpStatusCode.OK, radarrResp.StatusCode);
        var radarrJson = (await radarrResp.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.NotNull(radarrJson["parent"]);
        Assert.NotNull(radarrJson["directories"]);
        Assert.NotNull(radarrJson["files"]);

        // Radarr client forbidden on Sonarr endpoint
        var radarrOnSonarr = await radarrClient.GetAsync("/compat/sonarr/api/v3/filesystem");
        Assert.Equal(HttpStatusCode.Forbidden, radarrOnSonarr.StatusCode);
    }
}
