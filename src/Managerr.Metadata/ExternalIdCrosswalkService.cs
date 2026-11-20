using Dapper;
using Managerr.Datastore;

namespace Managerr.Metadata;

public sealed record ExternalIdCrosswalkEdge(
    string SourceProvider,
    string SourceId,
    string TargetProvider,
    string TargetId,
    string MediaVertical,
    double Confidence = 1.0);

public interface IExternalIdCrosswalkService
{
    Task EnsureSeededAsync(CancellationToken ct = default);
    Task LinkPairAsync(
        string providerA,
        string idA,
        string providerB,
        string idB,
        string mediaVertical,
        double confidence = 1.0,
        CancellationToken ct = default);
    Task LinkClusterAsync(
        IReadOnlyDictionary<string, string> externalIds,
        string mediaVertical,
        double confidence = 1.0,
        CancellationToken ct = default);
    Task<int> ImportBatchAsync(IReadOnlyList<ExternalIdCrosswalkEdge> edges, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> ResolveConnectedIdsAsync(
        IReadOnlyDictionary<string, string> seedIds,
        CancellationToken ct = default);
}

/// <summary>
/// Bidirectional external ID crosswalk graph in managerr.db (external_id_links).
/// Bridges anime-lists (anidb/anilist/tvdb/tmdb/imdb/xem), Wikidata film/TV IDs,
/// MusicBrainz/AcoustID/Discogs IDs, and ISBN13/ASIN/Hardcover/OpenLibrary book IDs.
/// </summary>
public sealed class ExternalIdCrosswalkService(ISqliteDbFactory dbFactory) : IExternalIdCrosswalkService
{
    private static readonly IReadOnlyList<(string Vertical, Dictionary<string, string> Cluster)> BuiltInCrosswalkSeeds =
    [
        // Anime: Fribb anime-lists + TheXEM scene mapping examples
        (
            MetadataVerticals.Anime,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["anilist"] = "154587",
                ["anidb"] = "17617",
                ["tvdb"] = "424536",
                ["tmdb"] = "209867",
                ["imdb"] = "tt22248376",
                ["xem"] = "frieren-s01",
            }
        ),
        (
            MetadataVerticals.Anime,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["anilist"] = "16498",
                ["anidb"] = "9541",
                ["tvdb"] = "267440",
                ["tmdb"] = "1429",
                ["imdb"] = "tt2560140",
                ["xem"] = "shingeki-no-kyojin",
            }
        ),
        // TV Series: Wikidata / TVDB / TMDB / IMDb / TVMaze / Trakt bridge
        (
            MetadataVerticals.Tv,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tvdb"] = "371980",
                ["tmdb"] = "95396",
                ["imdb"] = "tt11280740",
                ["tvmaze"] = "44933",
                ["trakt"] = "154999",
                ["wikidata"] = "Q100148244",
            }
        ),
        // Movies: TMDB / TVDB / IMDb / Wikidata bridge
        (
            MetadataVerticals.Movies,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tmdb"] = "693134",
                ["tvdb"] = "290272",
                ["imdb"] = "tt15239678",
                ["trakt"] = "652495",
                ["wikidata"] = "Q109321897",
            }
        ),
        // Music: MusicBrainz / AcoustID / Discogs / Deezer bridge
        (
            MetadataVerticals.Music,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["musicbrainz"] = "f27ec8db-af05-4f36-916e-3d57f91ecf5e",
                ["acoustid"] = "acoustid-daft-punk-ram",
                ["discogs"] = "556257",
                ["deezer"] = "6575789",
            }
        ),
        // Books & Audiobooks: Open Library / Hardcover / Audnexus ASIN / ISBN-13 / DNB bridge
        (
            MetadataVerticals.Ebooks,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["openlibrary"] = "OL20885734W",
                ["hardcover"] = "hc-project-hail-mary",
                ["audnexus"] = "B08G9PRS1K",
                ["asin"] = "B08G9PRS1K",
                ["isbn13"] = "9780593135204",
                ["dnb"] = "1228636895",
            }
        ),
    ];

    public async Task EnsureSeededAsync(CancellationToken ct = default)
    {
        await using (var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct))
        {
            var existing = await readConn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM external_id_links;");
            if (existing > 0)
            {
                return;
            }
        }

        foreach (var (vertical, cluster) in BuiltInCrosswalkSeeds)
        {
            await LinkClusterAsync(cluster, vertical, confidence: 1.0, ct);
        }
    }

    public Task LinkPairAsync(
        string providerA,
        string idA,
        string providerB,
        string idB,
        string mediaVertical,
        double confidence = 1.0,
        CancellationToken ct = default) =>
        ImportBatchAsync(
        [
            new ExternalIdCrosswalkEdge(providerA, idA, providerB, idB, mediaVertical, confidence),
        ], ct);

    public async Task LinkClusterAsync(
        IReadOnlyDictionary<string, string> externalIds,
        string mediaVertical,
        double confidence = 1.0,
        CancellationToken ct = default)
    {
        var clean = externalIds
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => (Provider: kv.Key.Trim().ToLowerInvariant(), Id: kv.Value.Trim()))
            .ToList();

        if (clean.Count < 2)
        {
            return;
        }

        var edges = new List<ExternalIdCrosswalkEdge>();
        for (var i = 0; i < clean.Count; i++)
        {
            for (var j = i + 1; j < clean.Count; j++)
            {
                edges.Add(new ExternalIdCrosswalkEdge(
                    clean[i].Provider,
                    clean[i].Id,
                    clean[j].Provider,
                    clean[j].Id,
                    mediaVertical,
                    confidence));
            }
        }

        await ImportBatchAsync(edges, ct);
    }

    public Task<int> ImportBatchAsync(IReadOnlyList<ExternalIdCrosswalkEdge> edges, CancellationToken ct = default)
    {
        if (edges.Count == 0)
        {
            return Task.FromResult(0);
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        return dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                var written = 0;
                foreach (var e in edges)
                {
                    if (string.IsNullOrWhiteSpace(e.SourceProvider) ||
                        string.IsNullOrWhiteSpace(e.SourceId) ||
                        string.IsNullOrWhiteSpace(e.TargetProvider) ||
                        string.IsNullOrWhiteSpace(e.TargetId))
                    {
                        continue;
                    }

                    var srcP = e.SourceProvider.Trim().ToLowerInvariant();
                    var dstP = e.TargetProvider.Trim().ToLowerInvariant();
                    if (srcP == dstP)
                    {
                        continue;
                    }

                    const string upsertSql = """
                        INSERT INTO external_id_links (
                            source_provider, source_id, target_provider, target_id,
                            media_vertical, confidence, updated_at
                        ) VALUES (
                            @SrcP, @SrcId, @DstP, @DstId, @Vertical, @Confidence, @Now
                        )
                        ON CONFLICT(source_provider, source_id, target_provider) DO UPDATE SET
                            target_id  = excluded.target_id,
                            confidence = MAX(external_id_links.confidence, excluded.confidence),
                            updated_at = excluded.updated_at;
                        """;

                    written += await conn.ExecuteAsync(
                        upsertSql,
                        new
                        {
                            SrcP = srcP,
                            SrcId = e.SourceId.Trim(),
                            DstP = dstP,
                            DstId = e.TargetId.Trim(),
                            Vertical = string.IsNullOrWhiteSpace(e.MediaVertical) ? MetadataVerticals.Tv : e.MediaVertical.Trim().ToLowerInvariant(),
                            Confidence = Math.Clamp(e.Confidence, 0.0, 1.0),
                            Now = now,
                        },
                        transaction: tx);

                    written += await conn.ExecuteAsync(
                        upsertSql,
                        new
                        {
                            SrcP = dstP,
                            SrcId = e.TargetId.Trim(),
                            DstP = srcP,
                            DstId = e.SourceId.Trim(),
                            Vertical = string.IsNullOrWhiteSpace(e.MediaVertical) ? MetadataVerticals.Tv : e.MediaVertical.Trim().ToLowerInvariant(),
                            Confidence = Math.Clamp(e.Confidence, 0.0, 1.0),
                            Now = now,
                        },
                        transaction: tx);
                }

                return written;
            },
            ct);
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveConnectedIdsAsync(
        IReadOnlyDictionary<string, string> seedIds,
        CancellationToken ct = default)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var frontier = new Queue<(string Provider, string Id)>();

        foreach (var (k, v) in seedIds)
        {
            if (string.IsNullOrWhiteSpace(k) || string.IsNullOrWhiteSpace(v))
            {
                continue;
            }

            var normP = k.Trim().ToLowerInvariant();
            var normId = v.Trim();
            resolved[normP] = normId;
            frontier.Enqueue((normP, normId));
        }

        if (frontier.Count == 0)
        {
            return resolved;
        }

        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (frontier.Count > 0 && visited.Count < 32)
        {
            var (prov, extId) = frontier.Dequeue();
            var visitKey = $"{prov}:{extId}";
            if (!visited.Add(visitKey))
            {
                continue;
            }

            var rows = await conn.QueryAsync<LinkRow>(
                """
                SELECT target_provider, target_id, confidence
                FROM external_id_links
                WHERE source_provider = @Prov AND source_id = @ExtId
                ORDER BY confidence DESC;
                """,
                new { Prov = prov, ExtId = extId });

            foreach (var row in rows)
            {
                if (!resolved.ContainsKey(row.TargetProvider))
                {
                    resolved[row.TargetProvider] = row.TargetId;
                    frontier.Enqueue((row.TargetProvider, row.TargetId));
                }
            }
        }

        return resolved;
    }

    private sealed class LinkRow
    {
        public string TargetProvider { get; init; } = "";
        public string TargetId { get; init; } = "";
        public double Confidence { get; init; }
    }
}
