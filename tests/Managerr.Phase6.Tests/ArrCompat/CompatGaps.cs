using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Managerr.Phase6.Tests.ArrCompat;

/// <summary>
/// ArrCompat/known-compat-gaps.json: the places where a compat response differs from the upstream schema today,
/// per Seerr call. Like the parser corpus baseline it is a ratchet: a new difference fails, and so does a fixed one
/// until it is removed. Fields Seerr reads may never be listed here.
/// Regenerate with <c>MANAGERR_UPDATE_COMPAT_GAPS=1 dotnet test tests/Managerr.Phase6.Tests --filter FullyQualifiedName~ArrCompat</c>.
/// </summary>
public static class CompatGaps
{
    public const string UpdateVariable = "MANAGERR_UPDATE_COMPAT_GAPS";
    public const string FileName = "known-compat-gaps.json";

    public static bool Updating => Environment.GetEnvironmentVariable(UpdateVariable) == "1";

    public static IReadOnlyDictionary<string, string[]> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ArrCompat", FileName);
        if (!File.Exists(path))
        {
            return new Dictionary<string, string[]>();
        }

        return JsonNode.Parse(File.ReadAllText(path))!.AsObject()
            .Where(kv => !kv.Key.StartsWith("//", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
    }

    /// <summary>Replaces the entries of one app in the checked-in baseline.</summary>
    public static void Write(string app, IReadOnlyDictionary<string, string[]> gaps)
    {
        var path = Path.Combine(SourceDirectory(), FileName);
        var current = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path))!.AsObject()
                .Where(kv => !kv.Key.StartsWith("//", StringComparison.Ordinal) && !kv.Key.StartsWith(app + " ", StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value!.AsArray().Select(x => x!.GetValue<string>()).ToArray())
            : new Dictionary<string, string[]>();

        foreach (var (k, v) in gaps)
        {
            current[k] = v;
        }

        var doc = new JsonObject
        {
            ["// generated"] = $"Upstream schema differences per Seerr call; see CompatGaps.cs. Regenerate with {UpdateVariable}=1.",
        };
        foreach (var (k, v) in current.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            doc[k] = new JsonArray(v.Order(StringComparer.Ordinal).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        }

        File.WriteAllText(path, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static string SourceDirectory([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;
}
