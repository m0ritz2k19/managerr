using System.Text.Json;
using System.Text.Json.Nodes;

namespace Managerr.Phase6.Tests.ArrCompat;

/// <summary>
/// ArrCompat/seerr-contract.json: every Sonarr/Radarr call Seerr's servarr client makes, generated from Seerr's source by
/// scripts/update-arr-compat-contract.py, plus the response fields Seerr reads (reviewed by hand).
/// </summary>
public static class SeerrContract
{
    private static readonly Lazy<JsonObject> Doc = new(() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ArrCompat", "seerr-contract.json")))!.AsObject());

    public static string Commit => Doc.Value["seerr"]!["commit"]!.GetValue<string>();

    public static IReadOnlyList<SeerrCall> Calls { get; } = Doc.Value["calls"]!.AsArray()
        .Select(c => new SeerrCall(
            c!["app"]!.GetValue<string>(),
            c["method"]!.GetValue<string>(),
            c["path"]!.GetValue<string>(),
            c["command"]?.GetValue<string>(),
            c["query"]!.AsArray().Select(q => q!.GetValue<string>()).ToArray(),
            c["usedBy"]!.AsArray().Select(q => q!.GetValue<string>()).ToArray(),
            c["reads"] is JsonArray reads ? reads.Select(r => r!.GetValue<string>()).ToArray() : null,
            c["notes"]?.GetValue<string>()))
        .ToList();

    public static IEnumerable<SeerrCall> For(string app) => Calls.Where(c => c.App == app);
}

public sealed record SeerrCall(
    string App,
    string Method,
    string Path,
    string? Command,
    IReadOnlyList<string> Query,
    IReadOnlyList<string> UsedBy,
    IReadOnlyList<string>? Reads,
    string? Notes)
{
    /// <summary>"sonarr PUT /series/{id}" or "radarr POST /command MoviesSearch".</summary>
    public string Key => $"{App} {Method} {Path}" + (Command is null ? "" : $" {Command}");

    public override string ToString() => Key;

    /// <summary>
    /// Checks the fields Seerr reads ("[].seasons[].statistics.episodeFileCount") against a response, with the exact
    /// property casing JavaScript needs. Returns the problems and whether any value was reached at all, since an
    /// empty array proves nothing.
    /// </summary>
    public static (List<string> Problems, bool Exercised) CheckRead(JsonNode? body, string read)
    {
        var tokens = read.Replace("[]", ".[]").Split('.', StringSplitOptions.RemoveEmptyEntries);
        var current = new List<(JsonNode? Node, string Path)> { (body, "$") };
        var problems = new List<string>();

        for (var t = 0; t < tokens.Length; t++)
        {
            var token = tokens[t];
            var next = new List<(JsonNode?, string)>();
            foreach (var (node, path) in current)
            {
                if (token == "[]")
                {
                    if (node is JsonArray arr)
                    {
                        next.AddRange(arr.Select(item => (item, $"{path}[]")));
                    }
                    else
                    {
                        problems.Add($"{path} is {Describe(node)}, Seerr expects an array");
                    }

                    continue;
                }

                if (node is not JsonObject obj)
                {
                    problems.Add($"{path} is {Describe(node)}, Seerr reads .{token} from it");
                    continue;
                }

                if (!obj.TryGetPropertyValue(token, out var child))
                {
                    var near = obj.FirstOrDefault(p => string.Equals(p.Key, token, StringComparison.OrdinalIgnoreCase)).Key;
                    problems.Add(near is null
                        ? $"{path}.{token} is missing"
                        : $"{path}.{token} is missing (returned as '{near}'; JavaScript property names are case-sensitive)");
                    continue;
                }

                if (child is null)
                {
                    problems.Add($"{path}.{token} is null");
                    continue;
                }

                next.Add((child, $"{path}.{token}"));
            }

            current = next;
        }

        return (problems.Distinct().ToList(), current.Count > 0);
    }

    private static string Describe(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "an object",
        JsonArray => "an array",
        _ => $"a {node.GetValueKind().ToString().ToLowerInvariant()}",
    };
}

/// <summary>One response Managerr gave to a replayed Seerr call.</summary>
public sealed record SeerrObservation(SeerrCall Call, string Url, int Status, JsonNode? Body)
{
    public override string ToString() => $"{Call.Method} {Url} -> {Status}";

    public static JsonNode? ParseBody(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return JsonValue.Create(raw);
        }
    }
}
