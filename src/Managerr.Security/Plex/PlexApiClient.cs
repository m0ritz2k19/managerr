using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using System.Xml.Linq;
using Managerr.Common.Net;
using Managerr.Common.Paths;

namespace Managerr.Security.Plex;

public sealed record PlexPinInfo(
    long PinId,
    string Code,
    string AuthUrl,
    string ClientIdentifier);

public sealed record PlexAccountProfile(
    long PlexUserId,
    string Uuid,
    string Username,
    string? Email,
    string? AvatarUrl,
    string? DefaultAudioLanguage,
    string? DefaultSubtitleLanguage);

public sealed record PlexServerConnection(
    string Uri,
    string DirectUrl,
    string? Address,
    int? Port,
    string Protocol,
    bool Local);

public sealed record PlexServerResource(
    string Name,
    string MachineIdentifier,
    bool Owned,
    string AccessToken,
    IReadOnlyList<PlexServerConnection> Connections);

public sealed record PlexPinPollResult(
    bool Claimed,
    string? AuthToken,
    PlexAccountProfile? Account,
    IReadOnlyList<PlexServerResource> Servers);

public sealed record PlexSharedUserEntry(
    long PlexUserId,
    string Uuid,
    string Username,
    string? Email,
    string? AvatarUrl,
    bool IsHomeUser,
    bool AllLibrariesShared,
    IReadOnlyList<string> SharedSectionIds);

public interface IPlexApiClient
{
    string GetClientIdentifier();
    Task<PlexPinInfo> CreateStrongPinAsync(string? forwardUrl = null, CancellationToken ct = default);
    Task<PlexPinPollResult> CheckPinAsync(long pinId, string? expectedCode = null, CancellationToken ct = default);
    Task<PlexAccountProfile> GetUserProfileAsync(string plexAuthToken, CancellationToken ct = default);
    Task<IReadOnlyList<PlexSharedUserEntry>> GetSharedServerUsersAsync(string ownerAdminToken, string boundMachineIdentifier, CancellationToken ct = default);
    Task<string> SwitchHomeUserAsync(string ownerOrAdminToken, string userUuid, string? pin = null, CancellationToken ct = default);
}

/// <summary>
/// Ported & extended from Cleanerr backend/app/clients/plex.py:
/// Handles plex.tv v2 strong PIN OAuth, server discovery, Plex Home profile switching,
/// and PMS shared-user + shared-library-section directory discovery.
/// </summary>
public sealed class PlexApiClient(IAppPaths paths, ISafeHttpClientFactory httpFactory) : IPlexApiClient
{
    private const string PlexProduct = "Managerr";
    private string? _cachedClientId;

    public string GetClientIdentifier()
    {
        if (!string.IsNullOrWhiteSpace(_cachedClientId))
        {
            return _cachedClientId;
        }

        if (File.Exists(paths.PlexClientIdFilePath))
        {
            var existing = File.ReadAllText(paths.PlexClientIdFilePath).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                _cachedClientId = existing;
                return _cachedClientId;
            }
        }

        _cachedClientId = Guid.NewGuid().ToString("D");
        File.WriteAllText(paths.PlexClientIdFilePath, _cachedClientId);
        return _cachedClientId;
    }

    public async Task<PlexPinInfo> CreateStrongPinAsync(string? forwardUrl = null, CancellationToken ct = default)
    {
        using var client = httpFactory.CreatePublicClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://plex.tv/api/v2/pins?strong=true");
        AddPlexHeaders(req, authToken: null, acceptJson: true);

        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var pinId = doc.RootElement.GetProperty("id").GetInt64();
        var code = doc.RootElement.GetProperty("code").GetString() ?? throw new InvalidOperationException("Missing PIN code from plex.tv.");
        var clientId = GetClientIdentifier();

        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["clientID"] = clientId;
        qs["code"] = code;
        qs["context[device][product]"] = PlexProduct;
        qs["context[device][platform]"] = "Web";
        qs["context[device][device]"] = "Managerr";
        if (!string.IsNullOrWhiteSpace(forwardUrl))
        {
            qs["forwardUrl"] = forwardUrl;
        }

        return new PlexPinInfo(
            PinId: pinId,
            Code: code,
            AuthUrl: $"https://app.plex.tv/auth#?{qs}",
            ClientIdentifier: clientId);
    }

    public async Task<PlexPinPollResult> CheckPinAsync(long pinId, string? expectedCode = null, CancellationToken ct = default)
    {
        using var client = httpFactory.CreatePublicClient();
        var url = $"https://plex.tv/api/v2/pins/{pinId}";
        if (!string.IsNullOrWhiteSpace(expectedCode))
        {
            url += $"?code={Uri.EscapeDataString(expectedCode)}";
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddPlexHeaders(req, authToken: null, acceptJson: true);

        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var authToken = doc.RootElement.TryGetProperty("authToken", out var tokEl) && tokEl.ValueKind == JsonValueKind.String
            ? tokEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(authToken))
        {
            return new PlexPinPollResult(false, null, null, []);
        }

        var profile = await GetUserProfileAsync(authToken, ct);
        var servers = await DiscoverServersAsync(client, authToken, ct);
        return new PlexPinPollResult(true, authToken, profile, servers);
    }

    public async Task<PlexAccountProfile> GetUserProfileAsync(string plexAuthToken, CancellationToken ct = default)
    {
        using var client = httpFactory.CreatePublicClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://plex.tv/api/v2/user");
        AddPlexHeaders(req, plexAuthToken, acceptJson: true);

        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;

        var id = root.GetProperty("id").GetInt64();
        var uuid = root.TryGetProperty("uuid", out var uEl) ? (uEl.GetString() ?? id.ToString()) : id.ToString();
        var username = (root.TryGetProperty("username", out var unEl) ? unEl.GetString() : null)
            ?? (root.TryGetProperty("title", out var tEl) ? tEl.GetString() : null)
            ?? $"plex-{id}";
        var email = root.TryGetProperty("email", out var emEl) ? emEl.GetString() : null;
        var thumb = root.TryGetProperty("thumb", out var thEl) ? thEl.GetString() : null;

        string? audioLang = null;
        string? subLang = null;
        if (root.TryGetProperty("profile", out var profEl) && profEl.ValueKind == JsonValueKind.Object)
        {
            audioLang = profEl.TryGetProperty("defaultAudioLanguage", out var al) ? al.GetString() : null;
            subLang = profEl.TryGetProperty("defaultSubtitleLanguage", out var sl) ? sl.GetString() : null;
        }

        return new PlexAccountProfile(id, uuid, username, email, thumb, audioLang, subLang);
    }

    public async Task<IReadOnlyList<PlexSharedUserEntry>> GetSharedServerUsersAsync(
        string ownerAdminToken,
        string boundMachineIdentifier,
        CancellationToken ct = default)
    {
        using var client = httpFactory.CreatePublicClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://plex.tv/api/users");
        AddPlexHeaders(req, ownerAdminToken, acceptJson: false);

        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var xmlText = await resp.Content.ReadAsStringAsync(ct);

        var xdoc = XDocument.Parse(xmlText);
        var results = new List<PlexSharedUserEntry>();

        foreach (var userEl in xdoc.Descendants("User"))
        {
            var serverEl = userEl.Elements("Server")
                .FirstOrDefault(s => string.Equals(
                    (string?)s.Attribute("machineIdentifier"),
                    boundMachineIdentifier,
                    StringComparison.OrdinalIgnoreCase));

            if (serverEl is null)
            {
                continue;
            }

            if (!long.TryParse((string?)userEl.Attribute("id"), out var userId))
            {
                continue;
            }

            var username = (string?)userEl.Attribute("username")
                ?? (string?)userEl.Attribute("title")
                ?? $"plex-{userId}";
            var email = (string?)userEl.Attribute("email");
            var thumb = (string?)userEl.Attribute("thumb");
            var isHome = (string?)userEl.Attribute("home") == "1";
            var allLibs = (string?)serverEl.Attribute("allLibraries") == "1";
            var sectionIds = serverEl.Elements("Section")
                .Where(sec => (string?)sec.Attribute("shared") != "0")
                .Select(sec => (string?)sec.Attribute("key") ?? (string?)sec.Attribute("id"))
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Cast<string>()
                .Distinct()
                .ToArray();

            results.Add(new PlexSharedUserEntry(
                PlexUserId: userId,
                Uuid: (string?)userEl.Attribute("uuid") ?? userId.ToString(),
                Username: username,
                Email: email,
                AvatarUrl: thumb,
                IsHomeUser: isHome,
                AllLibrariesShared: allLibs,
                SharedSectionIds: sectionIds));
        }

        return results;
    }

    public async Task<string> SwitchHomeUserAsync(
        string ownerOrAdminToken,
        string userUuid,
        string? pin = null,
        CancellationToken ct = default)
    {
        using var client = httpFactory.CreatePublicClient();
        var url = $"https://plex.tv/api/v2/home/users/{Uri.EscapeDataString(userUuid)}/switch";
        if (!string.IsNullOrWhiteSpace(pin))
        {
            url += $"?pin={Uri.EscapeDataString(pin)}";
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        AddPlexHeaders(req, ownerOrAdminToken, acceptJson: true);

        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("authToken").GetString()
            ?? throw new InvalidOperationException("Plex Home switch response did not return an authToken.");
    }

    private async Task<IReadOnlyList<PlexServerResource>> DiscoverServersAsync(
        HttpClient client,
        string authToken,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            "https://plex.tv/api/v2/resources?includeHttps=1&includeRelay=0");
        AddPlexHeaders(req, authToken, acceptJson: true);

        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return [];
        }

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var servers = new List<PlexServerResource>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var provides = item.TryGetProperty("provides", out var pEl) ? (pEl.GetString() ?? "") : "";
            if (!provides.Contains("server", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var machineId = item.TryGetProperty("clientIdentifier", out var cidEl) ? cidEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(machineId))
            {
                continue;
            }

            var name = item.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "Plex Media Server") : "Plex Media Server";
            var owned = item.TryGetProperty("owned", out var oEl) && oEl.ValueKind == JsonValueKind.True;
            var accessToken = item.TryGetProperty("accessToken", out var atEl) && atEl.ValueKind == JsonValueKind.String
                ? (atEl.GetString() ?? authToken)
                : authToken;

            var conns = new List<PlexServerConnection>();
            if (item.TryGetProperty("connections", out var connsEl) && connsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in connsEl.EnumerateArray())
                {
                    var uri = c.TryGetProperty("uri", out var uEl) ? (uEl.GetString() ?? "") : "";
                    var addr = c.TryGetProperty("address", out var aEl) ? aEl.GetString() : null;
                    int? port = c.TryGetProperty("port", out var prtEl) && prtEl.TryGetInt32(out var pv) ? pv : null;
                    var proto = c.TryGetProperty("protocol", out var prEl) ? (prEl.GetString() ?? "http") : "http";
                    var local = c.TryGetProperty("local", out var lEl) && lEl.ValueKind == JsonValueKind.True;
                    var directUrl = (!string.IsNullOrWhiteSpace(addr) && port.HasValue) ? $"http://{addr}:{port.Value}" : uri;

                    conns.Add(new PlexServerConnection(uri, directUrl, addr, port, proto, local));
                }
            }

            servers.Add(new PlexServerResource(name, machineId, owned, accessToken, conns));
        }

        return servers;
    }

    private void AddPlexHeaders(HttpRequestMessage req, string? authToken, bool acceptJson)
    {
        if (acceptJson)
        {
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
        }
        req.Headers.TryAddWithoutValidation("X-Plex-Product", PlexProduct);
        req.Headers.TryAddWithoutValidation("X-Plex-Version", "0.1.0");
        req.Headers.TryAddWithoutValidation("X-Plex-Device", "Managerr Engine");
        req.Headers.TryAddWithoutValidation("X-Plex-Platform", "Web");
        req.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", GetClientIdentifier());
        if (!string.IsNullOrWhiteSpace(authToken))
        {
            req.Headers.TryAddWithoutValidation("X-Plex-Token", authToken);
        }
    }
}
