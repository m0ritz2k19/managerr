using System.Text.RegularExpressions;

namespace Managerr.Common.Media;

/// <summary>
/// Single spelling for quality names. The profile engine ranks files by exact name, so every writer
/// (importer, disk scan, migration) and every comparison must go through <see cref="Canonicalize"/>;
/// otherwise 'WEB-DL-1080p' vs 'WEBDL-1080p' looks like an upgrade and the same release is re-grabbed.
/// </summary>
public static partial class QualityNames
{
    public const string Unknown = "Unknown";

    [GeneratedRegex(@"^(?<source>[A-Za-z][A-Za-z \-]*?)[ \-]+(?<res>\d{3,4}[pi])$", RegexOptions.IgnoreCase)]
    private static partial Regex SourceResolutionRegex();

    // Container extensions and placeholders that say nothing about quality
    private static readonly HashSet<string> NonQualityLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "Standard", "Unknown", "MKV", "MP4", "M4V", "AVI", "TS", "WEBM", "WMV", "MOV",
    };

    private static readonly Dictionary<string, string> SourceAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WEBDL"] = "WEBDL",
        ["WEB-DL"] = "WEBDL",
        ["WEB DL"] = "WEBDL",
        ["WEB"] = "WEBDL",
        ["WEBRIP"] = "WEBRip",
        ["WEB-RIP"] = "WEBRip",
        ["BLURAY"] = "BluRay",
        ["BLU-RAY"] = "BluRay",
        ["BDRIP"] = "BluRay",
        ["REMUX"] = "Remux",
        ["BLURAY REMUX"] = "Remux",
        ["HDTV"] = "HDTV",
        ["DVD"] = "DVD",
        ["SD"] = "SD",
    };

    public static string Canonicalize(string? qualityName)
    {
        var trimmed = qualityName?.Trim() ?? "";
        if (NonQualityLabels.Contains(trimmed))
        {
            return Unknown;
        }

        var match = SourceResolutionRegex().Match(trimmed);
        if (!match.Success)
        {
            // Music/book formats (FLAC, EPUB, M4B, ...) are already canonical
            return trimmed;
        }

        var res = match.Groups["res"].Value.ToLowerInvariant();
        if (res is "480p" or "480i" or "576p" or "576i")
        {
            return "SD-480p";
        }

        var source = SourceAliases.GetValueOrDefault(match.Groups["source"].Value.Trim(), match.Groups["source"].Value.Trim());
        return $"{source}-{res}";
    }
}
