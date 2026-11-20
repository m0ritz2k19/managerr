using System.Text.Json;
using Dapper;
using Managerr.Datastore;

namespace Managerr.Profiles;

public sealed record TrashImportResult(
    int ImportedCount,
    int UpdatedCount,
    IReadOnlyList<CustomFormatSummary> Formats,
    IReadOnlyList<string> LanguageRulesDetectedForUplift);

public interface ITrashGuidesImporter
{
    Task<TrashImportResult> ImportJsonAsync(string rawTrashJson, CancellationToken ct = default);
    Task<IReadOnlyList<CustomFormatSummary>> ListCustomFormatsAsync(CancellationToken ct = default);

    Task<CustomFormatSummary> UpsertCustomFormatAsync(
        UpsertCustomFormatRequest request,
        long? id = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException("Custom Format CRUD not implemented on this adapter.");

    Task DeleteCustomFormatAsync(long id, CancellationToken ct = default) =>
        throw new NotSupportedException("Custom Format deletion not implemented on this adapter.");
}

/// <summary>
/// Native 1-click importer for TRaSH-Guides / Recyclarr Custom Format JSON definitions (single object or array)
/// plus full visual Custom Format specification builder CRUD.
/// Detects LanguageSpecification rules so users can uplift brittle CF hacks into first-class Language Policies.
/// </summary>
public sealed class TrashGuidesImporter(ISqliteDbFactory dbFactory) : ITrashGuidesImporter
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<TrashImportResult> ImportJsonAsync(string rawTrashJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawTrashJson))
        {
            throw new ArgumentException("TRaSH Custom Format JSON payload is empty.");
        }

        using var doc = JsonDocument.Parse(rawTrashJson);
        var elements = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList()
            : doc.RootElement.ValueKind == JsonValueKind.Object
                ? [doc.RootElement]
                : throw new ArgumentException("Expected JSON object or array of TRaSH Custom Formats.");

        var imported = 0;
        var updated = 0;
        var languageWarnings = new List<string>();
        var now = DateTimeOffset.UtcNow.ToString("O");

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                foreach (var el in elements)
                {
                    if (el.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var name = el.TryGetProperty("name", out var nEl) ? nEl.GetString()?.Trim() : null;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var trashId = el.TryGetProperty("trash_id", out var tEl) ? tEl.GetString()?.Trim() : null;
                    var rename = el.TryGetProperty("includeCustomFormatWhenRenaming", out var rEl) && rEl.ValueKind == JsonValueKind.True;

                    var defaultScore = 0;
                    if (el.TryGetProperty("trash_scores", out var tsEl) && tsEl.ValueKind == JsonValueKind.Object)
                    {
                        if (tsEl.TryGetProperty("default", out var defScoreEl) && defScoreEl.TryGetInt32(out var ds))
                        {
                            defaultScore = ds;
                        }
                        else if (tsEl.EnumerateObject().FirstOrDefault() is { Value.ValueKind: JsonValueKind.Number } firstScore &&
                                 firstScore.Value.TryGetInt32(out var fs))
                        {
                            defaultScore = fs;
                        }
                    }
                    else if (el.TryGetProperty("score", out var scEl) && scEl.TryGetInt32(out var explicitSc))
                    {
                        defaultScore = explicitSc;
                    }

                    var specs = new List<CustomFormatSpecItem>();
                    if (el.TryGetProperty("specifications", out var spArr) && spArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var sp in spArr.EnumerateArray())
                        {
                            var spName = sp.TryGetProperty("name", out var sn) ? (sn.GetString() ?? "Spec") : "Spec";
                            var impl = sp.TryGetProperty("implementation", out var im) ? (im.GetString() ?? "ReleaseTitleSpecification") : "ReleaseTitleSpecification";
                            var negate = sp.TryGetProperty("negate", out var ng) && ng.ValueKind == JsonValueKind.True;
                            var required = sp.TryGetProperty("required", out var rq) && rq.ValueKind == JsonValueKind.True;
                            var pattern = ExtractSpecPattern(sp);

                            if (impl.Contains("Language", StringComparison.OrdinalIgnoreCase))
                            {
                                languageWarnings.Add(
                                    $"Custom Format '{name}' contains LanguageSpecification ('{spName}') — consider uplifting to a first-class Managerr Language Policy.");
                            }

                            specs.Add(new CustomFormatSpecItem(spName, impl, negate, required, pattern));
                        }
                    }

                    var specsJson = JsonSerializer.Serialize(specs, JsonOpts);
                    long? existingId = null;
                    if (!string.IsNullOrWhiteSpace(trashId))
                    {
                        existingId = await conn.ExecuteScalarAsync<long?>(
                            "SELECT id FROM custom_formats WHERE trash_id = @TrashId;",
                            new { TrashId = trashId },
                            transaction: tx);
                    }

                    existingId ??= await conn.ExecuteScalarAsync<long?>(
                        "SELECT id FROM custom_formats WHERE LOWER(name) = LOWER(@Name);",
                        new { Name = name },
                        transaction: tx);

                    if (existingId.HasValue)
                    {
                        await conn.ExecuteAsync(
                            """
                            UPDATE custom_formats
                            SET trash_id              = COALESCE(@TrashId, trash_id),
                                name                  = @Name,
                                include_when_renaming = @Rename,
                                specifications_json   = @SpecsJson,
                                default_score         = @DefaultScore,
                                updated_at            = @Now
                            WHERE id = @Id;
                            """,
                            new
                            {
                                Id = existingId.Value,
                                TrashId = trashId,
                                Name = name,
                                Rename = rename ? 1 : 0,
                                SpecsJson = specsJson,
                                DefaultScore = defaultScore,
                                Now = now,
                            },
                            transaction: tx);
                        updated++;
                    }
                    else
                    {
                        await conn.ExecuteAsync(
                            """
                            INSERT INTO custom_formats (
                                trash_id, name, include_when_renaming,
                                specifications_json, default_score, created_at, updated_at
                            ) VALUES (
                                @TrashId, @Name, @Rename,
                                @SpecsJson, @DefaultScore, @Now, @Now
                            );
                            """,
                            new
                            {
                                TrashId = trashId,
                                Name = name,
                                Rename = rename ? 1 : 0,
                                SpecsJson = specsJson,
                                DefaultScore = defaultScore,
                                Now = now,
                            },
                            transaction: tx);
                        imported++;
                    }
                }
            },
            ct);

        var all = await ListCustomFormatsAsync(ct);
        return new TrashImportResult(imported, updated, all, languageWarnings.Distinct().ToList());
    }

    public async Task<IReadOnlyList<CustomFormatSummary>> ListCustomFormatsAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<CfRow>("SELECT * FROM custom_formats ORDER BY name COLLATE NOCASE ASC;");
        return rows.Select(MapRow).ToList();
    }

    public async Task<CustomFormatSummary> UpsertCustomFormatAsync(
        UpsertCustomFormatRequest request,
        long? id = null,
        CancellationToken ct = default)
    {
        var name = request.Name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Custom Format name is required.");
        }

        var specs = (request.Specifications ?? [])
            .Select(s => new CustomFormatSpecItem(
                Name: string.IsNullOrWhiteSpace(s.Name) ? "Spec" : s.Name.Trim(),
                Implementation: string.IsNullOrWhiteSpace(s.Implementation) ? "ReleaseTitleSpecification" : s.Implementation.Trim(),
                Negate: s.Negate,
                Required: s.Required,
                Pattern: s.Pattern?.Trim() ?? ""))
            .ToArray();

        var specsJson = JsonSerializer.Serialize(specs, JsonOpts);
        var trashId = string.IsNullOrWhiteSpace(request.TrashId) ? null : request.TrashId.Trim();
        var now = DateTimeOffset.UtcNow.ToString("O");

        var savedId = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                if (id.HasValue)
                {
                    var affected = await conn.ExecuteAsync(
                        """
                        UPDATE custom_formats
                        SET trash_id              = COALESCE(@TrashId, trash_id),
                            name                  = @Name,
                            include_when_renaming = @Rename,
                            specifications_json   = @SpecsJson,
                            default_score         = @DefaultScore,
                            updated_at            = @Now
                        WHERE id = @Id;
                        """,
                        new
                        {
                            Id = id.Value,
                            TrashId = trashId,
                            Name = name,
                            Rename = request.IncludeWhenRenaming ? 1 : 0,
                            SpecsJson = specsJson,
                            DefaultScore = request.DefaultScore,
                            Now = now,
                        },
                        transaction: tx);

                    if (affected == 0)
                    {
                        throw new KeyNotFoundException($"Custom Format #{id.Value} not found.");
                    }

                    return id.Value;
                }

                return await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO custom_formats (
                        trash_id, name, include_when_renaming,
                        specifications_json, default_score, created_at, updated_at
                    ) VALUES (
                        @TrashId, @Name, @Rename,
                        @SpecsJson, @DefaultScore, @Now, @Now
                    );
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        TrashId = trashId,
                        Name = name,
                        Rename = request.IncludeWhenRenaming ? 1 : 0,
                        SpecsJson = specsJson,
                        DefaultScore = request.DefaultScore,
                        Now = now,
                    },
                    transaction: tx);
            },
            ct);

        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await readConn.QuerySingleAsync<CfRow>("SELECT * FROM custom_formats WHERE id = @Id;", new { Id = savedId });
        return MapRow(row);
    }

    public async Task DeleteCustomFormatAsync(long id, CancellationToken ct = default)
    {
        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                var affected = await conn.ExecuteAsync(
                    "DELETE FROM custom_formats WHERE id = @Id;",
                    new { Id = id },
                    transaction: tx);
                if (affected == 0)
                {
                    throw new KeyNotFoundException($"Custom Format #{id} not found.");
                }
            },
            ct);
    }

    private static string ExtractSpecPattern(JsonElement sp)
    {
        if (!sp.TryGetProperty("fields", out var fields))
        {
            return "";
        }

        if (fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("value", out var valEl))
        {
            return valEl.ValueKind == JsonValueKind.String ? (valEl.GetString() ?? "") : valEl.ToString();
        }

        if (fields.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fields.EnumerateArray())
            {
                if (f.ValueKind == JsonValueKind.Object && f.TryGetProperty("value", out var fVal))
                {
                    return fVal.ValueKind == JsonValueKind.String ? (fVal.GetString() ?? "") : fVal.ToString();
                }
            }
        }

        return "";
    }

    private static CustomFormatSummary MapRow(CfRow row)
    {
        var specs = JsonSerializer.Deserialize<CustomFormatSpecItem[]>(row.SpecificationsJson, JsonOpts) ?? [];
        var hasLang = specs.Any(s => s.Implementation.Contains("Language", StringComparison.OrdinalIgnoreCase));
        return new CustomFormatSummary(
            Id: row.Id,
            TrashId: row.TrashId,
            Name: row.Name,
            IncludeWhenRenaming: row.IncludeWhenRenaming != 0,
            Specifications: specs,
            DefaultScore: row.DefaultScore,
            ContainsLanguageRule: hasLang,
            CreatedAt: row.CreatedAt,
            UpdatedAt: row.UpdatedAt);
    }

    private sealed class CfRow
    {
        public long Id { get; init; }
        public string? TrashId { get; init; }
        public string Name { get; init; } = "";
        public int IncludeWhenRenaming { get; init; }
        public string SpecificationsJson { get; init; } = "[]";
        public int DefaultScore { get; init; }
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }
}
