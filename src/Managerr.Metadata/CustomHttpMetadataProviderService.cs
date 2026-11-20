using System.Text.Json;
using System.Web;
using Dapper;
using Managerr.Common.Crypto;
using Managerr.Datastore;

namespace Managerr.Metadata;

public sealed record CustomMetadataWebhookDefinition(
    long Id,
    string ProviderKey,
    string DisplayName,
    IReadOnlyList<string> SupportedVerticals,
    string SearchUrlTemplate,
    string? LookupUrlTemplate,
    bool HasApiKey,
    string CreatedAt);

public sealed record UpsertCustomMetadataWebhookRequest(
    string ProviderKey,
    string DisplayName,
    IReadOnlyList<string> SupportedVerticals,
    string SearchUrlTemplate,
    string? LookupUrlTemplate = null,
    string? ApiKey = null);

public interface ICustomHttpMetadataProviderService
{
    Task<IReadOnlyList<CustomMetadataWebhookDefinition>> ListAsync(CancellationToken ct = default);
    Task<CustomMetadataWebhookDefinition> CreateAsync(UpsertCustomMetadataWebhookRequest request, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<MetadataWorkCandidate>> SearchWebhookAsync(string providerKey, string query, string vertical, CancellationToken ct = default);
    Task<MetadataWorkCandidate?> LookupWebhookAsync(string providerKey, string externalId, string vertical, CancellationToken ct = default);
}

public sealed class CustomHttpMetadataProviderService(
    ISqliteDbFactory dbFactory,
    ISecretProtector secretProtector,
    IMetadataHttpGateway httpGateway) : ICustomHttpMetadataProviderService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<CustomMetadataWebhookDefinition>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<WebhookRow>(
            "SELECT * FROM custom_metadata_webhooks ORDER BY id ASC;");
        return rows.Select(MapRow).ToList();
    }

    public async Task<CustomMetadataWebhookDefinition> CreateAsync(
        UpsertCustomMetadataWebhookRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderKey) || string.IsNullOrWhiteSpace(request.SearchUrlTemplate))
        {
            throw new ArgumentException("ProviderKey and SearchUrlTemplate are required.");
        }

        var normKey = request.ProviderKey.Trim().ToLowerInvariant();
        if (!normKey.StartsWith("custom_", StringComparison.Ordinal))
        {
            normKey = $"custom_{normKey}";
        }

        var verticals = request.SupportedVerticals
            .Where(v => MetadataVerticals.All.Contains(v, StringComparer.OrdinalIgnoreCase))
            .Select(v => v.ToLowerInvariant())
            .Distinct()
            .ToArray();

        if (verticals.Length == 0)
        {
            verticals = MetadataVerticals.All.ToArray();
        }

        var encKey = string.IsNullOrWhiteSpace(request.ApiKey)
            ? null
            : secretProtector.Protect(request.ApiKey.Trim());
        var now = DateTimeOffset.UtcNow.ToString("O");

        var id = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO custom_metadata_webhooks (
                    provider_key, display_name, supported_verticals_json,
                    search_url_template, lookup_url_template, api_key_enc, created_at
                ) VALUES (
                    @Key, @Name, @VerticalsJson,
                    @SearchTpl, @LookupTpl, @ApiKeyEnc, @Now
                );
                SELECT last_insert_rowid();
                """,
                new
                {
                    Key = normKey,
                    Name = string.IsNullOrWhiteSpace(request.DisplayName) ? normKey : request.DisplayName.Trim(),
                    VerticalsJson = JsonSerializer.Serialize(verticals, JsonOpts),
                    SearchTpl = request.SearchUrlTemplate.Trim(),
                    LookupTpl = string.IsNullOrWhiteSpace(request.LookupUrlTemplate) ? null : request.LookupUrlTemplate.Trim(),
                    ApiKeyEnc = encKey,
                    Now = now,
                },
                transaction: tx),
            ct);

        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await readConn.QuerySingleAsync<WebhookRow>(
            "SELECT * FROM custom_metadata_webhooks WHERE id = @Id;",
            new { Id = id });
        return MapRow(row);
    }

    public Task DeleteAsync(long id, CancellationToken ct = default) =>
        dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                "DELETE FROM custom_metadata_webhooks WHERE id = @Id;",
                new { Id = id },
                transaction: tx),
            ct);

    public async Task<IReadOnlyList<MetadataWorkCandidate>> SearchWebhookAsync(
        string providerKey,
        string query,
        string vertical,
        CancellationToken ct = default)
    {
        var row = await GetRowByKeyAsync(providerKey, ct);
        if (row is null)
        {
            return [];
        }

        var verticals = JsonSerializer.Deserialize<string[]>(row.SupportedVerticalsJson, JsonOpts) ?? [];
        if (!verticals.Contains(vertical, StringComparer.OrdinalIgnoreCase))
        {
            return [];
        }

        var url = row.SearchUrlTemplate
            .Replace("{query}", HttpUtility.UrlEncode(query.Trim()), StringComparison.OrdinalIgnoreCase)
            .Replace("{vertical}", HttpUtility.UrlEncode(vertical), StringComparison.OrdinalIgnoreCase);

        var raw = await ExecuteWebhookRequestAsync(row, url, ct);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return ParseWebhookCandidates(row.ProviderKey, vertical, raw);
    }

    public async Task<MetadataWorkCandidate?> LookupWebhookAsync(
        string providerKey,
        string externalId,
        string vertical,
        CancellationToken ct = default)
    {
        var row = await GetRowByKeyAsync(providerKey, ct);
        if (row is null || string.IsNullOrWhiteSpace(row.LookupUrlTemplate))
        {
            return null;
        }

        var url = row.LookupUrlTemplate
            .Replace("{id}", HttpUtility.UrlEncode(externalId.Trim()), StringComparison.OrdinalIgnoreCase)
            .Replace("{vertical}", HttpUtility.UrlEncode(vertical), StringComparison.OrdinalIgnoreCase);

        var raw = await ExecuteWebhookRequestAsync(row, url, ct);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return ParseWebhookCandidates(row.ProviderKey, vertical, raw).FirstOrDefault();
    }

    private async Task<WebhookRow?> GetRowByKeyAsync(string providerKey, CancellationToken ct)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        return await conn.QuerySingleOrDefaultAsync<WebhookRow>(
            "SELECT * FROM custom_metadata_webhooks WHERE provider_key = @Key;",
            new { Key = providerKey.Trim().ToLowerInvariant() });
    }

    private Task<string?> ExecuteWebhookRequestAsync(WebhookRow row, string url, CancellationToken ct)
    {
        Dictionary<string, string>? headers = null;
        if (!string.IsNullOrWhiteSpace(row.ApiKeyEnc))
        {
            var plainKey = secretProtector.Unprotect(row.ApiKeyEnc);
            headers = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {plainKey}",
                ["X-Api-Key"] = plainKey,
            };
        }

        return httpGateway.SendAsync(
            new MetadataHttpRequest(url, HttpMethod.Get, Headers: headers, AllowPrivateLan: true),
            ct);
    }

    private static IReadOnlyList<MetadataWorkCandidate> ParseWebhookCandidates(
        string providerKey,
        string vertical,
        string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        var list = new List<MetadataWorkCandidate>();

        var elements = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToList()
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var rArr) && rArr.ValueKind == JsonValueKind.Array
                ? rArr.EnumerateArray().ToList()
                : root.ValueKind == JsonValueKind.Object
                    ? [root]
                    : [];

        foreach (var el in elements)
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = el.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var id = el.TryGetProperty("id", out var idEl) ? idEl.ToString() : title;
            int? year = el.TryGetProperty("year", out var yrEl) && yrEl.ValueKind == JsonValueKind.Number ? yrEl.GetInt32() : null;
            var overview = el.TryGetProperty("overview", out var ovEl) ? (ovEl.GetString() ?? "") : "";
            var lang = el.TryGetProperty("originalLanguage", out var lEl) ? (lEl.GetString() ?? "en") : "en";
            var poster = el.TryGetProperty("posterUrl", out var pEl) ? pEl.GetString() : null;

            var alts = new List<string>();
            if (el.TryGetProperty("altTitles", out var altArr) && altArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in altArr.EnumerateArray())
                {
                    if (a.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(a.GetString()))
                    {
                        alts.Add(a.GetString()!);
                    }
                }
            }

            var extIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [providerKey] = id,
            };
            if (el.TryGetProperty("externalIds", out var extObj) && extObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in extObj.EnumerateObject())
                {
                    var val = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        extIds[prop.Name.ToLowerInvariant()] = val;
                    }
                }
            }

            IReadOnlyList<MetadataContainerSeed>? parsedContainers = null;
            if (el.TryGetProperty("containers", out var cArr) && cArr.ValueKind == JsonValueKind.Array)
            {
                var cList = new List<MetadataContainerSeed>();
                foreach (var cEl in cArr.EnumerateArray())
                {
                    if (cEl.ValueKind != JsonValueKind.Object) continue;
                    var cKind = cEl.TryGetProperty("containerKind", out var ck) ? (ck.GetString() ?? "season") : "season";
                    var cNum = cEl.TryGetProperty("containerNumber", out var cn) && cn.ValueKind == JsonValueKind.Number ? cn.GetInt32() : cList.Count + 1;
                    var cTitle = cEl.TryGetProperty("title", out var ctEl) ? (ctEl.GetString() ?? $"Season {cNum}") : $"Season {cNum}";
                    var cRel = cEl.TryGetProperty("releaseDate", out var cr) && cr.ValueKind == JsonValueKind.String ? cr.GetString() : null;

                    var uList = new List<MetadataUnitSeed>();
                    if (cEl.TryGetProperty("units", out var uArr) && uArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var uEl in uArr.EnumerateArray())
                        {
                            if (uEl.ValueKind != JsonValueKind.Object) continue;
                            var uKind = uEl.TryGetProperty("unitKind", out var uk) ? (uk.GetString() ?? "episode") : "episode";
                            var uNum = uEl.TryGetProperty("unitNumber", out var un) && un.ValueKind == JsonValueKind.Number ? un.GetInt32() : uList.Count + 1;
                            int? absNum = uEl.TryGetProperty("absoluteNumber", out var an) && an.ValueKind == JsonValueKind.Number ? an.GetInt32() : null;
                            var uTitle = uEl.TryGetProperty("title", out var ut) ? (ut.GetString() ?? $"Episode {uNum}") : $"Episode {uNum}";
                            var uAir = uEl.TryGetProperty("airOrReleaseDate", out var ua) && ua.ValueKind == JsonValueKind.String ? ua.GetString() : null;
                            uList.Add(new MetadataUnitSeed(uKind, uNum, absNum, uTitle, uAir));
                        }
                    }

                    cList.Add(new MetadataContainerSeed(cKind, cNum, cTitle, cRel, uList));
                }

                if (cList.Count > 0)
                {
                    parsedContainers = cList;
                }
            }

            list.Add(new MetadataWorkCandidate(
                ProviderKey: providerKey,
                ProviderItemId: id,
                Vertical: vertical,
                Title: title,
                AltTitles: alts,
                OriginalLanguage: lang,
                Year: year,
                Status: "released",
                Overview: overview,
                PosterUrl: poster,
                ExternalIds: extIds,
                Containers: parsedContainers ?? UniversalBuiltInMetadataProvider.DefaultContainersForVertical(vertical, title, year)));
        }

        return list;
    }

    private static CustomMetadataWebhookDefinition MapRow(WebhookRow row) =>
        new(
            Id: row.Id,
            ProviderKey: row.ProviderKey,
            DisplayName: row.DisplayName,
            SupportedVerticals: JsonSerializer.Deserialize<string[]>(row.SupportedVerticalsJson, JsonOpts) ?? [],
            SearchUrlTemplate: row.SearchUrlTemplate,
            LookupUrlTemplate: row.LookupUrlTemplate,
            HasApiKey: !string.IsNullOrEmpty(row.ApiKeyEnc),
            CreatedAt: row.CreatedAt);

    private sealed class WebhookRow
    {
        public long Id { get; init; }
        public string ProviderKey { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string SupportedVerticalsJson { get; init; } = "[]";
        public string SearchUrlTemplate { get; init; } = "";
        public string? LookupUrlTemplate { get; init; }
        public string? ApiKeyEnc { get; init; }
        public string CreatedAt { get; init; } = "";
    }
}
