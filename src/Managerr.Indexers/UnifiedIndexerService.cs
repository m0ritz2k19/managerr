using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Dapper;
using Managerr.Common.Crypto;
using Managerr.Common.Net;
using Managerr.Datastore;
using Managerr.Indexers.Cardigann;
using Managerr.Indexers.Categories;
using Managerr.Indexers.Torznab;

namespace Managerr.Indexers;

public sealed record IndexerProxySummary(
    long Id,
    string Name,
    string ProxyType,
    string Host,
    int Port,
    string? Username,
    MaskedSecret PasswordSecret,
    int RequestTimeoutSec,
    IReadOnlyList<string> Tags);

public sealed record UpsertIndexerProxyRequest(
    string Name,
    string ProxyType,
    string Host,
    int Port,
    string? Username = null,
    string? Password = null,
    int RequestTimeoutSec = 60,
    IReadOnlyList<string>? Tags = null);

public sealed record IndexerSummary(
    long Id,
    string Name,
    string Implementation,
    string? DefinitionId,
    string Protocol,
    string BaseUrl,
    MaskedSecret ApiKeySecret,
    IReadOnlyList<int> Categories,
    IReadOnlyList<string> Tags,
    int Priority,
    bool RssEnabled,
    bool InteractiveSearchEnabled,
    bool AutoSearchEnabled,
    int RequestDelayMs,
    int ConsecutiveFailures,
    string? DisabledUntil,
    string? LastError,
    bool IsHealthy);

public sealed record UpsertIndexerRequest(
    string Name,
    string Implementation,
    string? DefinitionId,
    string Protocol,
    string BaseUrl,
    string? ApiKey = null,
    Dictionary<string, string>? ExtraSettings = null,
    IReadOnlyList<int>? Categories = null,
    IReadOnlyList<string>? Tags = null,
    int Priority = 25,
    bool RssEnabled = true,
    bool InteractiveSearchEnabled = true,
    bool AutoSearchEnabled = true,
    int RequestDelayMs = 1000);

public sealed record IndexerSearchQuery(
    string? Query = null,
    string? MediaVertical = null,
    IReadOnlyList<int>? Categories = null,
    string? ImdbId = null,
    int? TmdbId = null,
    int? TvdbId = null,
    int? Season = null,
    int? Episode = null,
    bool Interactive = true,
    long? WorkId = null,
    long? UnitId = null,
    long? ProfileId = null,
    string? Series = null,
    string? Album = null,
    string? Artist = null,
    string? Author = null,
    string? Title = null,
    int? Year = null,
    int? TvMazeId = null,
    IReadOnlyList<string>? AltTitles = null,
    string? FilterQuery = null,
    string? Protocol = null,
    bool? ApprovedOnly = null,
    bool? HideBlocklisted = null,
    string? Sort = null,
    string? Order = null,
    int? Limit = null);

public sealed record MultiCategoryRssSyncReport(
    int PolledIndexers,
    int TotalReleases,
    IReadOnlyDictionary<string, IReadOnlyList<IndexedReleaseCandidate>> ReleasesByVertical);

public sealed record IndexerHttpRequest(
    Uri TargetUri,
    string Method = "GET",
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyList<KeyValuePair<string, string>>? FormFields = null,
    string? RawBody = null,
    string? ContentType = null,
    string? CookieHeader = null,
    bool AllowAutoRedirect = true,
    string? EncodingName = null);

public sealed record IndexerHttpResponse(
    int StatusCode,
    string Body,
    IReadOnlyList<string> SetCookieHeaders,
    Uri? FinalUri = null,
    IReadOnlyDictionary<string, string>? Headers = null);

public interface IIndexerHttpGateway
{
    Task<string> FetchStringAsync(Uri targetUri, IndexerProxyInternal? matchedProxy, CancellationToken ct = default);

    async Task<IndexerHttpResponse> SendAsync(
        IndexerHttpRequest request,
        IndexerProxyInternal? matchedProxy,
        CancellationToken ct = default)
    {
        var body = await FetchStringAsync(request.TargetUri, matchedProxy, ct);
        return new IndexerHttpResponse(200, body, [], request.TargetUri);
    }
}

public sealed record IndexerProxyInternal(
    string ProxyType,
    string Host,
    int Port,
    string? Username,
    string? PlainPassword,
    int RequestTimeoutSec);

public sealed class IndexerHttpGateway(ISafeHttpClientFactory safeHttpFactory) : IIndexerHttpGateway
{
    static IndexerHttpGateway()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<string> FetchStringAsync(
        Uri targetUri,
        IndexerProxyInternal? matchedProxy,
        CancellationToken ct = default)
    {
        var resp = await SendAsync(new IndexerHttpRequest(targetUri), matchedProxy, ct);
        if (resp.StatusCode >= 400)
        {
            throw new HttpRequestException($"Response status code does not indicate success: {resp.StatusCode}.");
        }
        return resp.Body;
    }

    public async Task<IndexerHttpResponse> SendAsync(
        IndexerHttpRequest request,
        IndexerProxyInternal? matchedProxy,
        CancellationToken ct = default)
    {
        if (request.TargetUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException($"Invalid URI scheme '{request.TargetUri.Scheme}'. Only http and https are allowed.");
        }

        if (matchedProxy is { ProxyType: "flaresolverr" or "byparr" })
        {
            return await SendViaFlareSolverrAsync(request, matchedProxy, ct);
        }

        if (matchedProxy is { ProxyType: "http" or "socks4" or "socks5" })
        {
            var proxyUri = new Uri($"{matchedProxy.ProxyType}://{matchedProxy.Host}:{matchedProxy.Port}");
            var webProxy = new WebProxy(proxyUri);
            if (!string.IsNullOrWhiteSpace(matchedProxy.Username))
            {
                webProxy.Credentials = new NetworkCredential(matchedProxy.Username, matchedProxy.PlainPassword ?? "");
            }

            using var handler = new SocketsHttpHandler
            {
                Proxy = webProxy,
                UseProxy = true,
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
            };
            using var proxiedClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(matchedProxy.RequestTimeoutSec),
            };
            return await ExecuteHttpWithRedirectsAsync(proxiedClient, request, ct);
        }

        using var direct = safeHttpFactory.CreateHomelabClient(allowPrivateLan: true);
        return await ExecuteHttpWithRedirectsAsync(direct, request, ct);
    }

    private async Task<IndexerHttpResponse> SendViaFlareSolverrAsync(
        IndexerHttpRequest request,
        IndexerProxyInternal matchedProxy,
        CancellationToken ct)
    {
        using var fsClient = safeHttpFactory.CreateHomelabClient(allowPrivateLan: true);
        var fsEndpoint = new Uri($"http://{matchedProxy.Host}:{matchedProxy.Port}/v1");

        var isPost = string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase);
        var payload = new Dictionary<string, object?>
        {
            ["cmd"] = isPost ? "request.post" : "request.get",
            ["url"] = request.TargetUri.ToString(),
            ["maxTimeout"] = matchedProxy.RequestTimeoutSec * 1000,
        };

        if (!string.IsNullOrWhiteSpace(request.CookieHeader))
        {
            var cookieDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            CardigannExecutionEngine.ParseRawCookieString(request.CookieHeader, cookieDict);
            if (cookieDict.Count > 0)
            {
                payload["cookies"] = cookieDict
                    .Select(kv => new { name = kv.Key, value = kv.Value, domain = request.TargetUri.Host })
                    .ToArray();
            }
        }

        if (isPost)
        {
            if (request.FormFields is { Count: > 0 })
            {
                payload["postData"] = string.Join(
                    "&",
                    request.FormFields.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            }
            else if (request.RawBody is not null)
            {
                payload["postData"] = request.RawBody;
            }
        }

        using var jsonContent = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        using var resp = await fsClient.PostAsync(fsEndpoint, jsonContent, ct);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        var solution = doc.GetProperty("solution");
        var body = solution.TryGetProperty("response", out var rProp) ? rProp.GetString() ?? "" : "";
        var status = solution.TryGetProperty("status", out var sProp) && sProp.TryGetInt32(out var sc) ? sc : 200;
        var finalUrlStr = solution.TryGetProperty("url", out var uProp) ? uProp.GetString() : null;

        var setCookies = new List<string>();
        if (solution.TryGetProperty("cookies", out var cArr) && cArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var cItem in cArr.EnumerateArray())
            {
                var cName = cItem.TryGetProperty("name", out var np) ? np.GetString() : null;
                var cVal = cItem.TryGetProperty("value", out var vp) ? vp.GetString() : null;
                if (!string.IsNullOrWhiteSpace(cName) && cVal is not null)
                {
                    setCookies.Add($"{cName}={cVal}");
                }
            }
        }

        var finalUri = Uri.TryCreate(finalUrlStr, UriKind.Absolute, out var fu) ? fu : request.TargetUri;
        return new IndexerHttpResponse(status, body, setCookies, finalUri);
    }

    private static async Task<IndexerHttpResponse> ExecuteHttpWithRedirectsAsync(
        HttpClient client,
        IndexerHttpRequest request,
        CancellationToken ct)
    {
        var currentUri = request.TargetUri;
        var currentMethod = new HttpMethod(string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method.Trim().ToUpperInvariant());
        var currentFormFields = request.FormFields;
        var currentRawBody = request.RawBody;

        var workingCookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(request.CookieHeader))
        {
            CardigannExecutionEngine.ParseRawCookieString(request.CookieHeader, workingCookies);
        }

        var accumulatedSetCookies = new List<string>();
        const int maxRedirects = 6;

        for (var hop = 0; hop <= maxRedirects; hop++)
        {
            using var msg = new HttpRequestMessage(currentMethod, currentUri);
            var isSameOriginOrSubdomain = IsSameOrSubdomainHost(currentUri.Host, request.TargetUri.Host);

            var hasCustomUserAgent = false;
            if (request.Headers is not null)
            {
                foreach (var (hk, hv) in request.Headers)
                {
                    if (string.Equals(hk, "Content-Type", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(hk, "Cookie", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!isSameOriginOrSubdomain &&
                        (string.Equals(hk, "Authorization", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(hk, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    if (string.Equals(hk, "User-Agent", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(hv))
                    {
                        hasCustomUserAgent = true;
                    }
                    msg.Headers.TryAddWithoutValidation(hk, hv);
                }
            }

            if (!hasCustomUserAgent)
            {
                msg.Headers.TryAddWithoutValidation("User-Agent", SafeHttpClientFactory.DefaultUserAgent);
            }

            if (isSameOriginOrSubdomain)
            {
                var cookieHeader = CardigannExecutionEngine.FormatCookieHeader(workingCookies);
                if (!string.IsNullOrWhiteSpace(cookieHeader))
                {
                    msg.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                }
            }

            if (currentMethod == HttpMethod.Post || currentMethod == HttpMethod.Put)
            {
                if (currentFormFields is { Count: > 0 })
                {
                    msg.Content = new FormUrlEncodedContent(currentFormFields);
                }
                else if (currentRawBody is not null)
                {
                    var contentType = request.ContentType
                        ?? (request.Headers is not null && request.Headers.TryGetValue("Content-Type", out var ctHdr) ? ctHdr : null)
                        ?? "application/x-www-form-urlencoded";
                    msg.Content = new StringContent(currentRawBody, Encoding.UTF8, contentType);
                }
            }

            using var resp = await client.SendAsync(msg, HttpCompletionOption.ResponseContentRead, ct);
            if (isSameOriginOrSubdomain && resp.Headers.TryGetValues("Set-Cookie", out var hopSetCookies))
            {
                var list = hopSetCookies.ToList();
                accumulatedSetCookies.AddRange(list);
                CardigannExecutionEngine.MergeSetCookies(list, workingCookies);
            }

            var status = (int)resp.StatusCode;
            if (request.AllowAutoRedirect &&
                hop < maxRedirects &&
                status is 301 or 302 or 303 or 307 or 308 &&
                resp.Headers.Location is not null)
            {
                var nextUri = resp.Headers.Location.IsAbsoluteUri
                    ? resp.Headers.Location
                    : new Uri(currentUri, resp.Headers.Location);

                if (nextUri.Scheme is not ("http" or "https"))
                {
                    throw new HttpRequestException($"Redirected to unsupported scheme '{nextUri.Scheme}'.");
                }

                if (!nextUri.Authority.Equals(currentUri.Authority, StringComparison.OrdinalIgnoreCase))
                {
                    var ub = new UriBuilder(nextUri);
                    var qs = HttpUtility.ParseQueryString(ub.Query);
                    qs.Remove("apikey");
                    qs.Remove("passkey");
                    qs.Remove("api_key");
                    ub.Query = qs.ToString();
                    currentUri = ub.Uri;
                }
                else
                {
                    currentUri = nextUri;
                }

                if (status is 301 or 302 or 303)
                {
                    currentMethod = HttpMethod.Get;
                    currentFormFields = null;
                    currentRawBody = null;
                }
                continue;
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            var encoding = ResolveEncoding(request.EncodingName, resp.Content.Headers.ContentType?.CharSet);
            var body = encoding.GetString(bytes);

            var respHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in resp.Headers)
            {
                respHeaders[h.Key] = string.Join(", ", h.Value);
            }

            return new IndexerHttpResponse(status, body, accumulatedSetCookies, currentUri, respHeaders);
        }

        return new IndexerHttpResponse(310, "", accumulatedSetCookies, currentUri);
    }

    internal static bool IsSameOrSubdomainHost(string candidateHost, string originHost)
    {
        if (string.IsNullOrWhiteSpace(candidateHost) || string.IsNullOrWhiteSpace(originHost))
        {
            return false;
        }

        if (string.Equals(candidateHost, originHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidateHost.EndsWith("." + originHost, StringComparison.OrdinalIgnoreCase);
    }

    private static Encoding ResolveEncoding(string? requestedEncoding, string? responseCharSet)
    {
        var candidate = !string.IsNullOrWhiteSpace(requestedEncoding) ? requestedEncoding : responseCharSet;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            try
            {
                return Encoding.GetEncoding(candidate.Trim().Trim('"'));
            }
            catch
            {
                // Fall back to UTF-8
            }
        }
        return Encoding.UTF8;
    }
}

public interface IUnifiedIndexerService
{
    Task<IReadOnlyList<IndexerProxySummary>> ListProxiesAsync(CancellationToken ct = default);
    Task<IndexerProxySummary> CreateProxyAsync(UpsertIndexerProxyRequest req, CancellationToken ct = default);
    Task DeleteProxyAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<IndexerSummary>> ListIndexersAsync(CancellationToken ct = default);
    Task<IndexerSummary> CreateIndexerAsync(UpsertIndexerRequest req, CancellationToken ct = default);
    Task<IndexerSummary> UpdateIndexerAsync(long id, UpsertIndexerRequest req, CancellationToken ct = default);
    Task<(bool Ok, string Message)> TestIndexerAsync(UpsertIndexerRequest req, long? existingId = null, CancellationToken ct = default);
    Task DeleteIndexerAsync(long id, CancellationToken ct = default);

    Task RecordSuccessAsync(long indexerId, CancellationToken ct = default);
    Task<DateTimeOffset> RecordFailureAsync(long indexerId, string errorMessage, CancellationToken ct = default);

    Task<IReadOnlyList<IndexedReleaseCandidate>> SearchAsync(IndexerSearchQuery query, CancellationToken ct = default);
    Task<MultiCategoryRssSyncReport> SyncMultiCategoryRssAsync(CancellationToken ct = default);
}

public sealed class UnifiedIndexerService(
    ISqliteDbFactory dbFactory,
    ISecretProtector secretProtector,
    ICardigannDefinitionCatalog cardigannCatalog,
    IIndexerHttpGateway httpGateway) : IUnifiedIndexerService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly Regex SecretQueryParamRegex = new(
        @"(?i)((?:[?&]|\b)(?:apikey|api_key|passkey|token|password|secret)=)[^&\s'""<>]+",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    internal static string RedactSecrets(
        string? message,
        string? apiKey = null,
        IReadOnlyDictionary<string, string>? extraSettings = null)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "";
        }

        string result;
        try
        {
            result = SecretQueryParamRegex.Replace(message, "$1[REDACTED]");
        }
        catch (RegexMatchTimeoutException)
        {
            result = message;
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            result = result.Replace(apiKey, "[REDACTED]", StringComparison.OrdinalIgnoreCase);
            var escaped = Uri.EscapeDataString(apiKey);
            if (!string.Equals(escaped, apiKey, StringComparison.OrdinalIgnoreCase))
            {
                result = result.Replace(escaped, "[REDACTED]", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (extraSettings != null)
        {
            foreach (var (k, v) in extraSettings)
            {
                if (string.IsNullOrWhiteSpace(v) || v.Length < 4) continue;
                var isSecretKey = k.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                                  k.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
                                  k.Contains("passkey", StringComparison.OrdinalIgnoreCase) ||
                                  k.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
                                  k.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                                  k.Contains("secret", StringComparison.OrdinalIgnoreCase);
                if (isSecretKey)
                {
                    result = result.Replace(v, "[REDACTED]", StringComparison.OrdinalIgnoreCase);
                    var esc = Uri.EscapeDataString(v);
                    if (!string.Equals(esc, v, StringComparison.OrdinalIgnoreCase))
                    {
                        result = result.Replace(esc, "[REDACTED]", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        }

        return result;
    }

    private static readonly TimeSpan[] BackoffLadder =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(24),
    ];

    private readonly ConcurrentDictionary<long, (SemaphoreSlim Gate, DateTimeOffset LastRequestUtc)> _rateGates = new();
    private readonly ConcurrentDictionary<long, (IndexerCapabilities? Caps, DateTimeOffset ExpiresAt)> _capsCache = new();
    private readonly CardigannExecutionEngine _cardigannEngine = new(cardigannCatalog, httpGateway);

    public async Task<IReadOnlyList<IndexerProxySummary>> ListProxiesAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<ProxyRow>("SELECT * FROM indexer_proxies ORDER BY id ASC;");
        return rows.Select(MapProxySummary).ToList();
    }

    public async Task<IndexerProxySummary> CreateProxyAsync(UpsertIndexerProxyRequest req, CancellationToken ct = default)
    {
        var type = req.ProxyType.Trim().ToLowerInvariant();
        if (type is not ("flaresolverr" or "byparr" or "http" or "socks4" or "socks5"))
        {
            throw new ArgumentException($"Unsupported indexer proxy type '{req.ProxyType}'.");
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var passEnc = string.IsNullOrWhiteSpace(req.Password) ? null : secretProtector.Protect(req.Password);
        var tagsJson = JsonSerializer.Serialize(req.Tags?.Distinct().ToArray() ?? [], JsonOpts);

        var id = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO indexer_proxies (name, proxy_type, host, port, username, password_enc, request_timeout_sec, tags_json, created_at, updated_at)
                VALUES (@Name, @Type, @Host, @Port, @Username, @PassEnc, @Timeout, @TagsJson, @Now, @Now);
                SELECT last_insert_rowid();
                """,
                new
                {
                    Name = req.Name.Trim(),
                    Type = type,
                    Host = req.Host.Trim(),
                    req.Port,
                    Username = req.Username,
                    PassEnc = passEnc,
                    Timeout = Math.Clamp(req.RequestTimeoutSec, 5, 300),
                    TagsJson = tagsJson,
                    Now = now,
                },
                transaction: tx),
            ct);

        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await readConn.QuerySingleAsync<ProxyRow>("SELECT * FROM indexer_proxies WHERE id = @Id;", new { Id = id });
        return MapProxySummary(row);
    }

    public Task DeleteProxyAsync(long id, CancellationToken ct = default) =>
        dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync("DELETE FROM indexer_proxies WHERE id = @Id;", new { Id = id }, transaction: tx),
            ct);

    public async Task<IReadOnlyList<IndexerSummary>> ListIndexersAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<IndexerRow>("SELECT * FROM indexers ORDER BY priority ASC, name COLLATE NOCASE ASC;");
        return rows.Select(MapIndexerSummary).ToList();
    }

    public async Task<IndexerSummary> CreateIndexerAsync(UpsertIndexerRequest req, CancellationToken ct = default)
    {
        ValidateIndexerRequest(req);
        var impl = req.Implementation.Trim().ToLowerInvariant();
        var proto = req.Protocol.Trim().ToLowerInvariant();

        var def = impl == "cardigann" && !string.IsNullOrWhiteSpace(req.DefinitionId)
            ? cardigannCatalog.GetDefinition(req.DefinitionId)
            : null;
        if (impl == "cardigann" && def is null)
        {
            throw new ArgumentException($"Unknown Cardigann definition '{req.DefinitionId}'.");
        }

        var baseUrl = string.IsNullOrWhiteSpace(req.BaseUrl) && def?.Summary.Links.Count > 0
            ? def.Summary.Links[0]
            : req.BaseUrl.Trim();

        var cats = req.Categories is { Count: > 0 }
            ? req.Categories.Distinct().OrderBy(x => x).ToArray()
            : def?.Summary.NewznabCategories.ToArray() ?? [2000, 3000, 3030, 5000, 5070, 7000, 7020, 7030];

        var apiKeyEnc = string.IsNullOrWhiteSpace(req.ApiKey) ? null : secretProtector.Protect(req.ApiKey);
        var protectedExtra = ProtectExtraSettings(req.ExtraSettings, existingProtected: null);
        var now = DateTimeOffset.UtcNow.ToString("O");

        var id = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO indexers (
                    name, implementation, definition_id, protocol, base_url, api_key_enc,
                    extra_settings_json, categories_json, tags_json, priority,
                    rss_enabled, interactive_search_enabled, auto_search_enabled,
                    request_delay_ms, created_at, updated_at
                ) VALUES (
                    @Name, @Impl, @DefId, @Proto, @BaseUrl, @ApiKeyEnc,
                    @ExtraJson, @CatsJson, @TagsJson, @Priority,
                    @Rss, @Interactive, @Auto,
                    @DelayMs, @Now, @Now
                );
                SELECT last_insert_rowid();
                """,
                new
                {
                    Name = req.Name.Trim(),
                    Impl = impl,
                    DefId = req.DefinitionId?.Trim(),
                    Proto = proto,
                    BaseUrl = baseUrl,
                    ApiKeyEnc = apiKeyEnc,
                    ExtraJson = JsonSerializer.Serialize(protectedExtra, JsonOpts),
                    CatsJson = JsonSerializer.Serialize(cats, JsonOpts),
                    TagsJson = JsonSerializer.Serialize(req.Tags?.Distinct().ToArray() ?? [], JsonOpts),
                    Priority = Math.Clamp(req.Priority, 1, 50),
                    Rss = req.RssEnabled ? 1 : 0,
                    Interactive = req.InteractiveSearchEnabled ? 1 : 0,
                    Auto = req.AutoSearchEnabled ? 1 : 0,
                    DelayMs = Math.Max(0, req.RequestDelayMs),
                    Now = now,
                },
                transaction: tx),
            ct);

        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await conn.QuerySingleAsync<IndexerRow>("SELECT * FROM indexers WHERE id = @Id;", new { Id = id });
        return MapIndexerSummary(row);
    }

    public async Task<IndexerSummary> UpdateIndexerAsync(long id, UpsertIndexerRequest req, CancellationToken ct = default)
    {
        ValidateIndexerRequest(req);
        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var existing = await readConn.QuerySingleOrDefaultAsync<IndexerRow>("SELECT * FROM indexers WHERE id = @Id;", new { Id = id })
            ?? throw new KeyNotFoundException($"Indexer #{id} not found.");

        var impl = req.Implementation.Trim().ToLowerInvariant();
        var proto = req.Protocol.Trim().ToLowerInvariant();
        var defId = string.IsNullOrWhiteSpace(req.DefinitionId) ? existing.DefinitionId : req.DefinitionId.Trim();
        var apiKeyEnc = string.IsNullOrWhiteSpace(req.ApiKey)
            ? existing.ApiKeyEnc
            : secretProtector.Protect(req.ApiKey);

        var existingExtra = JsonSerializer.Deserialize<Dictionary<string, string>>(existing.ExtraSettingsJson, JsonOpts) ?? [];
        var mergedExtra = req.ExtraSettings is not null
            ? ProtectExtraSettings(req.ExtraSettings, existingExtra)
            : existingExtra;
        var extraJson = JsonSerializer.Serialize(mergedExtra, JsonOpts);

        var catsJson = req.Categories is { Count: > 0 }
            ? JsonSerializer.Serialize(req.Categories.Distinct().OrderBy(x => x).ToArray(), JsonOpts)
            : existing.CategoriesJson;
        var tagsJson = req.Tags is not null
            ? JsonSerializer.Serialize(req.Tags.Distinct().ToArray(), JsonOpts)
            : existing.TagsJson;

        _cardigannEngine.InvalidateSession(id);

        var now = DateTimeOffset.UtcNow.ToString("O");
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                UPDATE indexers
                SET name = @Name,
                    implementation = @Impl,
                    definition_id = @DefId,
                    protocol = @Proto,
                    base_url = @BaseUrl,
                    api_key_enc = @ApiKeyEnc,
                    extra_settings_json = @ExtraJson,
                    categories_json = @CatsJson,
                    tags_json = @TagsJson,
                    priority = @Priority,
                    rss_enabled = @Rss,
                    interactive_search_enabled = @Interactive,
                    auto_search_enabled = @Auto,
                    request_delay_ms = @DelayMs,
                    consecutive_failures = 0,
                    disabled_until = NULL,
                    last_error = NULL,
                    updated_at = @Now
                WHERE id = @Id;
                """,
                new
                {
                    Id = id,
                    Name = req.Name.Trim(),
                    Impl = impl,
                    DefId = defId,
                    Proto = proto,
                    BaseUrl = req.BaseUrl.Trim(),
                    ApiKeyEnc = apiKeyEnc,
                    ExtraJson = extraJson,
                    CatsJson = catsJson,
                    TagsJson = tagsJson,
                    Priority = Math.Clamp(req.Priority, 1, 50),
                    Rss = req.RssEnabled ? 1 : 0,
                    Interactive = req.InteractiveSearchEnabled ? 1 : 0,
                    Auto = req.AutoSearchEnabled ? 1 : 0,
                    DelayMs = Math.Max(0, req.RequestDelayMs),
                    Now = now,
                },
                transaction: tx),
            ct);

        var updated = await readConn.QuerySingleAsync<IndexerRow>("SELECT * FROM indexers WHERE id = @Id;", new { Id = id });
        return MapIndexerSummary(updated);
    }

    public async Task<(bool Ok, string Message)> TestIndexerAsync(
        UpsertIndexerRequest req,
        long? existingId = null,
        CancellationToken ct = default)
    {
        string? apiKey = req.ApiKey?.Trim();
        var effectiveExtraSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            ValidateIndexerRequest(req);
            if (string.IsNullOrWhiteSpace(req.BaseUrl))
            {
                return (false, "Base URL is required.");
            }

            var impl = req.Implementation.Trim().ToLowerInvariant();

            if (existingId.HasValue)
            {
                await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
                var existing = await readConn.QuerySingleOrDefaultAsync<IndexerRow>(
                    "SELECT * FROM indexers WHERE id = @Id;",
                    new { Id = existingId.Value });
                if (existing is not null)
                {
                    if (string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(existing.ApiKeyEnc))
                    {
                        apiKey = secretProtector.IsProtectedEnvelope(existing.ApiKeyEnc)
                            ? secretProtector.Unprotect(existing.ApiKeyEnc)
                            : existing.ApiKeyEnc;
                    }

                    foreach (var (ek, ev) in UnprotectExtraSettings(existing.ExtraSettingsJson))
                    {
                        effectiveExtraSettings[ek] = ev;
                    }
                }
            }

            if (req.ExtraSettings is not null)
            {
                foreach (var (rk, rv) in req.ExtraSettings)
                {
                    if (!string.IsNullOrEmpty(rv) || !effectiveExtraSettings.ContainsKey(rk))
                    {
                        effectiveExtraSettings[rk] = rv ?? "";
                    }
                }
            }

            var (_, proxies) = await LoadActiveIndexersAndProxiesAsync(ct);
            var tempRow = new IndexerRow
            {
                Id = existingId ?? 0,
                Name = req.Name.Trim(),
                Implementation = impl,
                DefinitionId = req.DefinitionId,
                Protocol = req.Protocol.Trim().ToLowerInvariant(),
                BaseUrl = req.BaseUrl.Trim(),
                TagsJson = JsonSerializer.Serialize(req.Tags ?? [], JsonOpts),
            };
            var matchedProxy = MatchProxyForIndexer(tempRow, proxies);

            if (impl is "torznab" or "newznab")
            {
                string? apiPath = effectiveExtraSettings.TryGetValue("apiPath", out var ap) && !string.IsNullOrWhiteSpace(ap) ? ap : null;
                var capsUri = TorznabFeedParser.BuildCapabilitiesUri(req.BaseUrl, apiKey, apiPath);

                var capsXml = await httpGateway.FetchStringAsync(capsUri, matchedProxy, ct);
                if (capsXml.Contains("<error", StringComparison.OrdinalIgnoreCase))
                {
                    string errMsg = "Indexer returned an API error on t=caps check (verify API key and URL).";
                    try
                    {
                        TorznabFeedParser.ParseXmlFeed(capsXml, 0, tempRow.Name, tempRow.Protocol);
                    }
                    catch (IndexerApiException apiEx)
                    {
                        errMsg = apiEx.Message;
                    }
                    catch
                    {
                    }
                    return (false, RedactSecrets(errMsg, apiKey, effectiveExtraSettings));
                }

                if (existingId.HasValue)
                {
                    var parsedCaps = TorznabFeedParser.TryParseCapabilitiesXml(capsXml);
                    if (parsedCaps != null)
                    {
                        _capsCache[existingId.Value] = (parsedCaps, DateTimeOffset.UtcNow.AddHours(12));
                    }
                    await RecordSuccessAsync(existingId.Value, ct);
                }

                return (true, $"Connected to {tempRow.Name} ({impl.ToUpperInvariant()} endpoint verified).");
            }

            var def = cardigannCatalog.GetDefinition(req.DefinitionId ?? "")
                ?? throw new ArgumentException($"Unknown Cardigann definition '{req.DefinitionId}'.");

            var rows = await _cardigannEngine.ExecuteSearchAsync(
                indexerId: existingId ?? 0,
                def: def,
                baseUrl: req.BaseUrl,
                apiKey: apiKey,
                extraSettings: effectiveExtraSettings,
                matchedProxy: matchedProxy,
                query: new IndexerSearchQuery(Query: null, Categories: req.Categories, Interactive: true),
                desiredCategories: req.Categories,
                forceFreshLogin: true,
                strictValidation: true,
                ct: ct);

            if (existingId.HasValue)
            {
                await RecordSuccessAsync(existingId.Value, ct);
            }

            return (true, $"Connected to Cardigann indexer {tempRow.Name} ({rows.Count} items parsed).");
        }
        catch (Exception ex)
        {
            return (false, $"Connection test failed: {RedactSecrets(ex.Message, apiKey, effectiveExtraSettings)}");
        }
    }

    public Task DeleteIndexerAsync(long id, CancellationToken ct = default)
    {
        _cardigannEngine.InvalidateSession(id);
        return dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync("DELETE FROM indexers WHERE id = @Id;", new { Id = id }, transaction: tx),
            ct);
    }

    public Task RecordSuccessAsync(long indexerId, CancellationToken ct = default) =>
        dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                UPDATE indexers
                SET consecutive_failures = 0, disabled_until = NULL, last_error = NULL, updated_at = @Now
                WHERE id = @Id;
                """,
                new { Id = indexerId, Now = DateTimeOffset.UtcNow.ToString("O") },
                transaction: tx),
            ct);

    public async Task<DateTimeOffset> RecordFailureAsync(long indexerId, string errorMessage, CancellationToken ct = default)
    {
        var redactedMessage = RedactSecrets(errorMessage);
        return await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                var currentFailures = await conn.ExecuteScalarAsync<int>(
                    "SELECT consecutive_failures FROM indexers WHERE id = @Id;",
                    new { Id = indexerId },
                    transaction: tx);
                var nextFailures = currentFailures + 1;
                var backoff = BackoffLadder[Math.Min(nextFailures - 1, BackoffLadder.Length - 1)];
                var disabledUntil = DateTimeOffset.UtcNow.Add(backoff);

                await conn.ExecuteAsync(
                    """
                    UPDATE indexers
                    SET consecutive_failures = @Failures,
                        disabled_until = @DisabledUntil,
                        last_error = @LastError,
                        updated_at = @Now
                    WHERE id = @Id;
                    """,
                    new
                    {
                        Id = indexerId,
                        Failures = nextFailures,
                        DisabledUntil = disabledUntil.ToString("O"),
                        LastError = redactedMessage,
                        Now = DateTimeOffset.UtcNow.ToString("O"),
                    },
                    transaction: tx);

                return disabledUntil;
            },
            ct);
    }

    public async Task<IReadOnlyList<IndexedReleaseCandidate>> SearchAsync(IndexerSearchQuery query, CancellationToken ct = default)
    {
        var (indexers, proxies) = await LoadActiveIndexersAndProxiesAsync(ct);
        var desiredCats = query.Categories is { Count: > 0 }
            ? query.Categories
            : NewznabCategoryMapper.GetCategoriesForVertical(query.MediaVertical);

        var eligible = indexers
            .Where(ix => query.Interactive ? ix.InteractiveSearchEnabled != 0 : ix.AutoSearchEnabled != 0)
            .Where(IsCurrentlyHealthy)
            .Where(ix =>
            {
                var ixCats = JsonSerializer.Deserialize<int[]>(ix.CategoriesJson, JsonOpts) ?? [];
                return NewznabCategoryMapper.IndexerSupportsCategories(desiredCats, ixCats);
            })
            .ToList();

        var tasks = eligible.Select(ix => ExecuteIndexerQuerySafeAsync(ix, proxies, query, desiredCats, ct));
        var batches = await Task.WhenAll(tasks);

        return batches
            .SelectMany(b => b)
            .OrderByDescending(r => r.Seeders ?? 0)
            .ThenByDescending(r => r.PublishedAt)
            .ToList();
    }

    public async Task<MultiCategoryRssSyncReport> SyncMultiCategoryRssAsync(CancellationToken ct = default)
    {
        var (indexers, proxies) = await LoadActiveIndexersAndProxiesAsync(ct);
        var rssIndexers = indexers
            .Where(ix => ix.RssEnabled != 0 && IsCurrentlyHealthy(ix))
            .ToList();

        // Single-pass poll per indexer requesting ALL its configured categories across all verticals simultaneously
        var tasks = rssIndexers.Select(ix =>
        {
            var ixCats = JsonSerializer.Deserialize<int[]>(ix.CategoriesJson, JsonOpts) ?? [];
            var q = new IndexerSearchQuery(Query: null, Categories: ixCats, Interactive: false);
            return ExecuteIndexerQuerySafeAsync(ix, proxies, q, ixCats, ct);
        });

        var allReleases = (await Task.WhenAll(tasks)).SelectMany(x => x).ToList();
        var byVertical = MediaVerticals.All.ToDictionary(
            v => v,
            v => (IReadOnlyList<IndexedReleaseCandidate>)allReleases
                .Where(r => string.Equals(r.MediaVertical, v, StringComparison.OrdinalIgnoreCase))
                .ToList(),
            StringComparer.OrdinalIgnoreCase);

        return new MultiCategoryRssSyncReport(
            PolledIndexers: rssIndexers.Count,
            TotalReleases: allReleases.Count,
            ReleasesByVertical: byVertical);
    }

    private async Task<IReadOnlyList<IndexedReleaseCandidate>> ExecuteIndexerQuerySafeAsync(
        IndexerRow ix,
        IReadOnlyList<ProxyRow> proxies,
        IndexerSearchQuery query,
        IReadOnlyList<int> desiredCats,
        CancellationToken ct)
    {
        try
        {
            await EnforceIndexerRateLimitAsync(ix.Id, ix.RequestDelayMs, ct);
            var matchedProxy = MatchProxyForIndexer(ix, proxies);
            var apiKey = !string.IsNullOrWhiteSpace(ix.ApiKeyEnc) && secretProtector.IsProtectedEnvelope(ix.ApiKeyEnc)
                ? secretProtector.Unprotect(ix.ApiKeyEnc)
                : ix.ApiKeyEnc;

            IReadOnlyList<IndexedReleaseCandidate> releases;
            if (ix.Implementation is "torznab" or "newznab")
            {
                var extraSettings = UnprotectExtraSettings(ix.ExtraSettingsJson);
                string? apiPath = null;
                if (extraSettings != null && extraSettings.TryGetValue("apiPath", out var p) && !string.IsNullOrWhiteSpace(p))
                {
                    apiPath = p;
                }

                bool isRssQuery = query.Categories is { Count: > 0 } &&
                                  query.Query is null &&
                                  query.TvdbId is null &&
                                  query.ImdbId is null &&
                                  query.TmdbId is null &&
                                  query.TvMazeId is null;

                IndexerCapabilities? caps = null;
                if (!isRssQuery)
                {
                    caps = await TryGetOrFetchCapabilitiesAsync(ix, apiKey, matchedProxy, apiPath, ct);
                    await EnforceIndexerRateLimitAsync(ix.Id, ix.RequestDelayMs, ct);
                }

                var ixCats = JsonSerializer.Deserialize<int[]>(ix.CategoriesJson, JsonOpts) ?? [];
                var effectiveCats = NewznabCategoryMapper.ResolveEffectiveCategoriesForIndexer(desiredCats, ixCats, caps?.SupportedCategories);

                var uri = TorznabFeedParser.BuildQueryUri(
                    baseUrl: ix.BaseUrl,
                    apiKey: apiKey,
                    query: query.Query,
                    vertical: query.MediaVertical,
                    explicitCategories: effectiveCats,
                    imdbId: query.ImdbId,
                    tmdbId: query.TmdbId,
                    tvdbId: query.TvdbId,
                    season: query.Season,
                    episode: query.Episode,
                    tvMazeId: query.TvMazeId,
                    year: query.Year,
                    artist: query.Artist,
                    album: query.Album,
                    author: query.Author,
                    bookTitle: query.Title,
                    apiPath: apiPath,
                    capabilities: caps);

                var queryStr = uri.Query;
                bool hasExternalId = queryStr.Contains("tvdbid=", StringComparison.OrdinalIgnoreCase) ||
                                     queryStr.Contains("tvmazeid=", StringComparison.OrdinalIgnoreCase) ||
                                     queryStr.Contains("imdbid=", StringComparison.OrdinalIgnoreCase) ||
                                     queryStr.Contains("tmdbid=", StringComparison.OrdinalIgnoreCase);
                bool hasQ = queryStr.Contains("q=", StringComparison.OrdinalIgnoreCase);
                bool matchedByExtId = hasExternalId && !hasQ;

                var xml = await httpGateway.FetchStringAsync(uri, matchedProxy, ct);
                var parsedReleases = TorznabFeedParser.ParseXmlFeed(xml, ix.Id, ix.Name, ix.Protocol);
                if (matchedByExtId)
                {
                    releases = parsedReleases.Select(r => r with { MatchedByExternalId = true }).ToList();
                }
                else
                {
                    releases = parsedReleases;
                }
            }
            else
            {
                var def = cardigannCatalog.GetDefinition(ix.DefinitionId ?? "")
                    ?? throw new InvalidOperationException($"Missing Cardigann definition '{ix.DefinitionId}'.");

                var extraSettings = UnprotectExtraSettings(ix.ExtraSettingsJson);
                var rows = await _cardigannEngine.ExecuteSearchAsync(
                    indexerId: ix.Id,
                    def: def,
                    baseUrl: ix.BaseUrl,
                    apiKey: apiKey,
                    extraSettings: extraSettings,
                    matchedProxy: matchedProxy,
                    query: query,
                    desiredCategories: desiredCats,
                    forceFreshLogin: false,
                    strictValidation: false,
                    ct: ct);

                releases = rows.Select(r => new IndexedReleaseCandidate(
                    Guid: r.Guid ?? r.InfoUrl ?? r.DownloadUrl,
                    IndexerId: ix.Id,
                    IndexerName: ix.Name,
                    Protocol: ix.Protocol,
                    MediaVertical: NewznabCategoryMapper.ResolveVerticalFromCategories(r.Categories, r.Title),
                    Title: r.Title,
                    DownloadUrl: r.DownloadUrl,
                    MagnetUrl: r.MagnetUrl,
                    InfoUrl: r.InfoUrl,
                    InfoHash: r.InfoHash,
                    SizeBytes: r.SizeBytes,
                    Seeders: r.Seeders,
                    Peers: r.Peers ?? (r.Seeders.HasValue && r.Leechers.HasValue ? r.Seeders + r.Leechers : r.Seeders),
                    DownloadVolumeFactor: r.DownloadVolumeFactor,
                    UploadVolumeFactor: r.UploadVolumeFactor,
                    PublishedAt: r.PublishedAt,
                    Categories: r.Categories,
                    ImdbId: r.ImdbId,
                    TmdbId: r.TmdbId,
                    TvdbId: r.TvdbId)).ToList();
            }

            if (ix.ConsecutiveFailures > 0)
            {
                await RecordSuccessAsync(ix.Id, ct);
            }

            return releases;
        }
        catch (IndexerApiException apiEx)
        {
            if (apiEx.ErrorCode is >= 200 and < 400)
            {
                return [];
            }

            var extraSettings = UnprotectExtraSettings(ix.ExtraSettingsJson);
            var apiKey = !string.IsNullOrWhiteSpace(ix.ApiKeyEnc) && secretProtector.IsProtectedEnvelope(ix.ApiKeyEnc)
                ? secretProtector.Unprotect(ix.ApiKeyEnc)
                : ix.ApiKeyEnc;

            await RecordFailureAsync(ix.Id, RedactSecrets(apiEx.Message, apiKey, extraSettings), ct);
            return [];
        }
        catch (Exception ex)
        {
            var extraSettings = UnprotectExtraSettings(ix.ExtraSettingsJson);
            var apiKey = !string.IsNullOrWhiteSpace(ix.ApiKeyEnc) && secretProtector.IsProtectedEnvelope(ix.ApiKeyEnc)
                ? secretProtector.Unprotect(ix.ApiKeyEnc)
                : ix.ApiKeyEnc;

            await RecordFailureAsync(ix.Id, RedactSecrets(ex.Message, apiKey, extraSettings), ct);
            return [];
        }
    }

    private Dictionary<string, string> ProtectExtraSettings(
        IReadOnlyDictionary<string, string>? incoming,
        IReadOnlyDictionary<string, string>? existingProtected)
    {
        var result = existingProtected is not null
            ? new Dictionary<string, string>(existingProtected, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (incoming is null)
        {
            return result;
        }

        foreach (var (key, val) in incoming)
        {
            if (string.IsNullOrEmpty(val))
            {
                if (!result.ContainsKey(key))
                {
                    result[key] = "";
                }
                continue;
            }

            var isSecretKey = key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                              key.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
                              key.Contains("passkey", StringComparison.OrdinalIgnoreCase) ||
                              key.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
                              key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                              key.Contains("secret", StringComparison.OrdinalIgnoreCase);

            result[key] = isSecretKey && !secretProtector.IsProtectedEnvelope(val)
                ? secretProtector.Protect(val)
                : val;
        }

        return result;
    }

    private Dictionary<string, string> UnprotectExtraSettings(string? extraSettingsJson)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(extraSettingsJson))
        {
            return result;
        }

        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(extraSettingsJson, JsonOpts);
        if (raw is null)
        {
            return result;
        }

        foreach (var (key, val) in raw)
        {
            if (val is null)
            {
                continue;
            }

            result[key] = secretProtector.IsProtectedEnvelope(val)
                ? secretProtector.Unprotect(val)
                : val;
        }

        return result;
    }

    private async Task EnforceIndexerRateLimitAsync(long indexerId, int delayMs, CancellationToken ct)
    {
        if (delayMs <= 0)
        {
            return;
        }

        var entry = _rateGates.GetOrAdd(indexerId, _ => (new SemaphoreSlim(1, 1), DateTimeOffset.MinValue));
        await entry.Gate.WaitAsync(ct);
        try
        {
            var current = _rateGates[indexerId];
            var elapsed = DateTimeOffset.UtcNow - current.LastRequestUtc;
            var needed = TimeSpan.FromMilliseconds(delayMs) - elapsed;
            var wait = needed > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : needed;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct);
            }
            _rateGates[indexerId] = (entry.Gate, DateTimeOffset.UtcNow);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private IndexerProxyInternal? MatchProxyForIndexer(IndexerRow ix, IReadOnlyList<ProxyRow> proxies)
    {
        var ixTags = JsonSerializer.Deserialize<string[]>(ix.TagsJson, JsonOpts) ?? [];
        if (ixTags.Length == 0)
        {
            return null;
        }

        foreach (var p in proxies)
        {
            var pTags = JsonSerializer.Deserialize<string[]>(p.TagsJson, JsonOpts) ?? [];
            if (pTags.Intersect(ixTags, StringComparer.OrdinalIgnoreCase).Any())
            {
                var plainPass = !string.IsNullOrWhiteSpace(p.PasswordEnc) && secretProtector.IsProtectedEnvelope(p.PasswordEnc)
                    ? secretProtector.Unprotect(p.PasswordEnc)
                    : p.PasswordEnc;
                return new IndexerProxyInternal(p.ProxyType, p.Host, p.Port, p.Username, plainPass, p.RequestTimeoutSec);
            }
        }

        return null;
    }

    private async Task<(IReadOnlyList<IndexerRow> Indexers, IReadOnlyList<ProxyRow> Proxies)> LoadActiveIndexersAndProxiesAsync(CancellationToken ct)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var ix = (await conn.QueryAsync<IndexerRow>("SELECT * FROM indexers ORDER BY priority ASC;")).ToList();
        var px = (await conn.QueryAsync<ProxyRow>("SELECT * FROM indexer_proxies ORDER BY id ASC;")).ToList();
        return (ix, px);
    }

    private static bool IsCurrentlyHealthy(IndexerRow row) =>
        string.IsNullOrWhiteSpace(row.DisabledUntil) ||
        !DateTimeOffset.TryParse(row.DisabledUntil, out var until) ||
        until <= DateTimeOffset.UtcNow;

    private static void ValidateIndexerRequest(UpsertIndexerRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            throw new ArgumentException("Indexer name is required.");
        }
        var impl = req.Implementation.Trim().ToLowerInvariant();
        if (impl is not ("torznab" or "newznab" or "cardigann"))
        {
            throw new ArgumentException($"Unsupported indexer implementation '{req.Implementation}'.");
        }
        var proto = req.Protocol.Trim().ToLowerInvariant();
        if (proto is not ("torrent" or "usenet"))
        {
            throw new ArgumentException($"Unsupported protocol '{req.Protocol}'.");
        }
        if (!string.IsNullOrWhiteSpace(req.BaseUrl))
        {
            if (!Uri.TryCreate(req.BaseUrl.Trim(), UriKind.Absolute, out var u) ||
                u.Scheme is not ("http" or "https") ||
                string.IsNullOrWhiteSpace(u.Host))
            {
                throw new ArgumentException("Invalid baseUrl");
            }

            if (req.ExtraSettings != null &&
                req.ExtraSettings.TryGetValue("apiPath", out var apiPath) &&
                !string.IsNullOrWhiteSpace(apiPath))
            {
                TorznabFeedParser.BuildEndpointUri(req.BaseUrl, apiPath);
            }
        }
    }

    private IndexerProxySummary MapProxySummary(ProxyRow r) =>
        new(
            Id: r.Id,
            Name: r.Name,
            ProxyType: r.ProxyType,
            Host: r.Host,
            Port: r.Port,
            Username: r.Username,
            PasswordSecret: secretProtector.Mask(r.PasswordEnc),
            RequestTimeoutSec: r.RequestTimeoutSec,
            Tags: JsonSerializer.Deserialize<string[]>(r.TagsJson, JsonOpts) ?? []);

    private IndexerSummary MapIndexerSummary(IndexerRow r) =>
        new(
            Id: r.Id,
            Name: r.Name,
            Implementation: r.Implementation,
            DefinitionId: r.DefinitionId,
            Protocol: r.Protocol,
            BaseUrl: r.BaseUrl,
            ApiKeySecret: secretProtector.Mask(r.ApiKeyEnc),
            Categories: JsonSerializer.Deserialize<int[]>(r.CategoriesJson, JsonOpts) ?? [],
            Tags: JsonSerializer.Deserialize<string[]>(r.TagsJson, JsonOpts) ?? [],
            Priority: r.Priority,
            RssEnabled: r.RssEnabled != 0,
            InteractiveSearchEnabled: r.InteractiveSearchEnabled != 0,
            AutoSearchEnabled: r.AutoSearchEnabled != 0,
            RequestDelayMs: r.RequestDelayMs,
            ConsecutiveFailures: r.ConsecutiveFailures,
            DisabledUntil: r.DisabledUntil,
            LastError: r.LastError,
            IsHealthy: IsCurrentlyHealthy(r));

    private async Task<IndexerCapabilities?> TryGetOrFetchCapabilitiesAsync(
        IndexerRow ix,
        string? apiKey,
        IndexerProxyInternal? matchedProxy,
        string? apiPath,
        CancellationToken ct)
    {
        if (_capsCache.TryGetValue(ix.Id, out var cached) && DateTimeOffset.UtcNow < cached.ExpiresAt)
        {
            return cached.Caps;
        }

        try
        {
            var capsUri = TorznabFeedParser.BuildCapabilitiesUri(ix.BaseUrl, apiKey, apiPath);
            await EnforceIndexerRateLimitAsync(ix.Id, ix.RequestDelayMs, ct);
            var xml = await httpGateway.FetchStringAsync(capsUri, matchedProxy, ct);
            var parsed = TorznabFeedParser.TryParseCapabilitiesXml(xml);
            if (parsed != null)
            {
                _capsCache[ix.Id] = (parsed, DateTimeOffset.UtcNow.AddHours(12));
                return parsed;
            }

            _capsCache[ix.Id] = (null, DateTimeOffset.UtcNow.AddMinutes(10));
            return null;
        }
        catch
        {
            _capsCache[ix.Id] = (null, DateTimeOffset.UtcNow.AddMinutes(10));
            return null;
        }
    }

    private sealed class ProxyRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string ProxyType { get; init; } = "";
        public string Host { get; init; } = "";
        public int Port { get; init; }
        public string? Username { get; init; }
        public string? PasswordEnc { get; init; }
        public int RequestTimeoutSec { get; init; }
        public string TagsJson { get; init; } = "[]";
    }

    private sealed class IndexerRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string Implementation { get; init; } = "";
        public string? DefinitionId { get; init; }
        public string Protocol { get; init; } = "";
        public string BaseUrl { get; init; } = "";
        public string? ApiKeyEnc { get; init; }
        public string ExtraSettingsJson { get; init; } = "{}";
        public string CategoriesJson { get; init; } = "[]";
        public string TagsJson { get; init; } = "[]";
        public int Priority { get; init; }
        public int RssEnabled { get; init; }
        public int InteractiveSearchEnabled { get; init; }
        public int AutoSearchEnabled { get; init; }
        public int RequestDelayMs { get; init; }
        public int ConsecutiveFailures { get; init; }
        public string? DisabledUntil { get; init; }
        public string? LastError { get; init; }
    }
}
