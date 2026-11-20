using Dapper;

namespace Managerr.Datastore.Services;

public sealed record CatalogFtsDocument(
    long Id,
    string MediaType,
    string Title,
    string AltTitles,
    int? Year,
    string ExternalIds);

public interface IFtsSearchService
{
    Task<long> IndexItemAsync(string mediaType, string title, string altTitles = "", int? year = null, string externalIds = "", CancellationToken ct = default);
    Task UpdateItemAsync(long id, string mediaType, string title, string altTitles = "", int? year = null, string externalIds = "", CancellationToken ct = default);
    Task DeleteItemAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<CatalogFtsDocument>> SearchAsync(string query, string? mediaType = null, int limit = 25, CancellationToken ct = default);
}

public sealed class FtsSearchService(ISqliteDbFactory dbFactory) : IFtsSearchService
{
    public Task<long> IndexItemAsync(
        string mediaType,
        string title,
        string altTitles = "",
        int? year = null,
        string externalIds = "",
        CancellationToken ct = default) =>
        dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO catalog_search_items (media_type, title, alt_titles, year, external_ids)
                VALUES (@MediaType, @Title, @AltTitles, @Year, @ExternalIds);
                SELECT last_insert_rowid();
                """,
                new
                {
                    MediaType = mediaType,
                    Title = title,
                    AltTitles = altTitles,
                    Year = year,
                    ExternalIds = externalIds,
                },
                transaction: tx),
            ct);

    public Task UpdateItemAsync(
        long id,
        string mediaType,
        string title,
        string altTitles = "",
        int? year = null,
        string externalIds = "",
        CancellationToken ct = default) =>
        dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                UPDATE catalog_search_items
                SET media_type   = @MediaType,
                    title        = @Title,
                    alt_titles   = @AltTitles,
                    year         = @Year,
                    external_ids = @ExternalIds
                WHERE id = @Id;
                """,
                new
                {
                    Id = id,
                    MediaType = mediaType,
                    Title = title,
                    AltTitles = altTitles,
                    Year = year,
                    ExternalIds = externalIds,
                },
                transaction: tx),
            ct);

    public Task DeleteItemAsync(long id, CancellationToken ct = default) =>
        dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                "DELETE FROM catalog_search_items WHERE id = @Id;",
                new { Id = id },
                transaction: tx),
            ct);

    public async Task<IReadOnlyList<CatalogFtsDocument>> SearchAsync(
        string query,
        string? mediaType = null,
        int limit = 25,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        // Sanitize tokens into prefix-matching FTS5 query (e.g. "dun part" -> "\"dun\"* AND \"part\"*")
        var tokens = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => $"\"{t.Replace("\"", "", StringComparison.Ordinal)}\"*")
            .ToArray();

        if (tokens.Length == 0)
        {
            return [];
        }

        var ftsExpr = string.Join(" AND ", tokens);
        var bounded = Math.Clamp(limit, 1, 100);

        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        IEnumerable<FtsRow> rows;
        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            rows = await conn.QueryAsync<FtsRow>(
                """
                SELECT c.id, c.media_type, c.title, c.alt_titles, c.year, c.external_ids
                FROM catalog_search_fts f
                JOIN catalog_search_items c ON c.id = f.rowid
                WHERE catalog_search_fts MATCH @FtsExpr
                  AND c.media_type = @MediaType
                ORDER BY bm25(catalog_search_fts)
                LIMIT @Limit;
                """,
                new { FtsExpr = ftsExpr, MediaType = mediaType, Limit = bounded });
        }
        else
        {
            rows = await conn.QueryAsync<FtsRow>(
                """
                SELECT c.id, c.media_type, c.title, c.alt_titles, c.year, c.external_ids
                FROM catalog_search_fts f
                JOIN catalog_search_items c ON c.id = f.rowid
                WHERE catalog_search_fts MATCH @FtsExpr
                ORDER BY bm25(catalog_search_fts)
                LIMIT @Limit;
                """,
                new { FtsExpr = ftsExpr, Limit = bounded });
        }

        return rows.Select(r => new CatalogFtsDocument(
            Id: r.Id,
            MediaType: r.MediaType,
            Title: r.Title,
            AltTitles: r.AltTitles,
            Year: r.Year,
            ExternalIds: r.ExternalIds)).ToList();
    }

    private sealed class FtsRow
    {
        public long Id { get; init; }
        public string MediaType { get; init; } = "";
        public string Title { get; init; } = "";
        public string AltTitles { get; init; } = "";
        public int? Year { get; init; }
        public string ExternalIds { get; init; } = "";
    }
}
