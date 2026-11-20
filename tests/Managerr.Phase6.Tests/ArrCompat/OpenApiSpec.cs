using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Managerr.Phase6.Tests.ArrCompat;

/// <summary>
/// One upstream *arr OpenAPI document (ArrCompat/upstream/*.openapi.json) and a small structural validator for it:
/// enough of OpenAPI 3.0 to check that a compat response has the shape the real app would return.
/// </summary>
public sealed class OpenApiSpec
{
    private const string ApiPrefix = "/api/v3";
    private readonly JsonObject _doc;
    private readonly Dictionary<string, JsonObject> _operations = new(StringComparer.Ordinal);

    private OpenApiSpec(string app, JsonObject doc)
    {
        App = app;
        _doc = doc;
        foreach (var (path, item) in doc["paths"]!.AsObject())
        {
            foreach (var (method, op) in item!.AsObject())
            {
                _operations[Key(method, path)] = op!.AsObject();
            }
        }

        Commit = doc["info"]?["x-managerr-source"]?["commit"]?.GetValue<string>() ?? "unknown";
    }

    public string App { get; }

    public string Commit { get; }

    public IEnumerable<string> OperationKeys => _operations.Keys;

    public static OpenApiSpec Load(string app) =>
        new(app, JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ArrCompat", "upstream", $"{app}-v3.openapi.json")))!.AsObject());

    /// <summary>"GET /api/v3/series/{}": lower-case, parameter names and constraints dropped.</summary>
    public static string Key(string method, string path) =>
        $"{method.ToUpperInvariant()} {NormalizePath(path)}";

    public static string NormalizePath(string path)
    {
        var segments = path.Trim().TrimEnd('/').Split('/')
            .Select(s => s.StartsWith('{') ? "{}" : s.ToLowerInvariant());
        return string.Join('/', segments);
    }

    /// <summary>The operation for a compat path relative to /api/v3 (as Seerr writes it), or null.</summary>
    public JsonObject? FindOperation(string method, string relativePath) =>
        _operations.GetValueOrDefault(Key(method, ApiPrefix + relativePath)) ??
        // The *arr REST controllers declare PUT as "{id?}" ([RestPutById]) and take the id from the body when the
        // route has none; the generated document only lists the "/{id}" form. Seerr uses the id-less one.
        (method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
            ? _operations.GetValueOrDefault(Key(method, ApiPrefix + relativePath.TrimEnd('/') + "/{id}"))
            : null);

    public bool HasOperation(string method, string relativePath) => FindOperation(method, relativePath) is not null;

    /// <summary>The JSON schema of the operation's first 2xx response, if it declares one.</summary>
    public JsonObject? SuccessSchema(string method, string relativePath)
    {
        var responses = FindOperation(method, relativePath)?["responses"]?.AsObject();
        var success = responses?.FirstOrDefault(r => r.Key.StartsWith('2')).Value;
        return success?["content"]?["application/json"]?["schema"]?.AsObject();
    }

    public IReadOnlyList<SchemaIssue> Validate(JsonNode? value, JsonObject schema)
    {
        var issues = new List<SchemaIssue>();
        Validate(value, schema, "$", issues, depth: 0);
        return issues.Distinct().OrderBy(i => i.ToString(), StringComparer.Ordinal).ToList();
    }

    private void Validate(JsonNode? value, JsonObject schema, string path, List<SchemaIssue> issues, int depth)
    {
        schema = Resolve(schema);
        if (depth > 12)
        {
            return;
        }

        if (schema["allOf"] is JsonArray allOf)
        {
            foreach (var part in allOf)
            {
                Validate(value, part!.AsObject(), path, issues, depth + 1);
            }

            return;
        }

        if (value is null)
        {
            var nullable = schema["nullable"]?.GetValue<bool>() == true;
            if (!nullable && schema["type"] is not null)
            {
                issues.Add(new SchemaIssue("null", path, $"is null, upstream type is non-nullable {schema["type"]}"));
            }

            return;
        }

        if (schema["enum"] is JsonArray enumValues && value is JsonValue ev && ev.TryGetValue<string>(out var s))
        {
            if (!enumValues.Any(e => e?.GetValue<string>() == s))
            {
                issues.Add(new SchemaIssue("enum", path, $"'{s}' is not one of {string.Join(", ", enumValues.Select(e => e?.ToString()))}"));
            }

            return;
        }

        var type = schema["type"]?.GetValue<string>();
        switch (type)
        {
            case "object" or null when schema["properties"] is JsonObject properties:
                if (value is not JsonObject obj)
                {
                    issues.Add(new SchemaIssue("type", path, $"expected object, got {Kind(value)}"));
                    return;
                }

                foreach (var (name, propSchema) in properties)
                {
                    var match = obj.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));
                    if (match.Key is null)
                    {
                        issues.Add(new SchemaIssue("missing", $"{path}.{name}", "not returned"));
                    }
                    else
                    {
                        Validate(match.Value, propSchema!.AsObject(), $"{path}.{name}", issues, depth + 1);
                    }
                }

                return;
            case "array":
                if (value is not JsonArray arr)
                {
                    issues.Add(new SchemaIssue("type", path, $"expected array, got {Kind(value)}"));
                    return;
                }

                foreach (var item in arr)
                {
                    Validate(item, schema["items"]!.AsObject(), $"{path}[]", issues, depth + 1);
                }

                return;
            case "integer":
                if (!(value is JsonValue iv && iv.GetValueKind() == JsonValueKind.Number && decimal.TryParse(iv.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == decimal.Truncate(d)))
                {
                    issues.Add(new SchemaIssue("type", path, $"expected integer, got {Kind(value)}"));
                }

                return;
            case "number":
                if (value.GetValueKind() != JsonValueKind.Number)
                {
                    issues.Add(new SchemaIssue("type", path, $"expected number, got {Kind(value)}"));
                }

                return;
            case "boolean":
                if (value.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                {
                    issues.Add(new SchemaIssue("type", path, $"expected boolean, got {Kind(value)}"));
                }

                return;
            case "string":
                if (value.GetValueKind() != JsonValueKind.String)
                {
                    issues.Add(new SchemaIssue("type", path, $"expected string, got {Kind(value)}"));
                }
                else if (schema["format"]?.GetValue<string>() == "date-time" &&
                         !DateTimeOffset.TryParse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                {
                    issues.Add(new SchemaIssue("format", path, $"'{value.GetValue<string>()}' is not a date-time"));
                }

                return;
        }
    }

    private JsonObject Resolve(JsonObject schema)
    {
        for (var i = 0; i < 20 && schema["$ref"] is JsonValue r; i++)
        {
            var name = r.GetValue<string>().Split('/').Last();
            schema = _doc["components"]!["schemas"]![name]!.AsObject();
        }

        return schema;
    }

    private static string Kind(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        _ => value.GetValueKind().ToString().ToLowerInvariant(),
    };
}

/// <summary>A difference between a compat response and the upstream schema. Array indices are collapsed to [].</summary>
public sealed record SchemaIssue(string Kind, string Path, string Detail)
{
    public override string ToString() => $"{Kind} {Path}: {Detail}";

    /// <summary>"missing" only means Managerr returns less than the real app; the other kinds are wrong values.</summary>
    public bool IsWrongValue => Kind != "missing";
}
