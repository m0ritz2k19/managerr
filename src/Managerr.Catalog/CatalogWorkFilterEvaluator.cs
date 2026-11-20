using System.Globalization;
using System.Text;

namespace Managerr.Catalog;

public static class CatalogWorkFilterEvaluator
{
    public static string NormalizeVertical(string? raw)
    {
        var v = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return v switch
        {
            "movie" => "movies",
            "series" => "tv",
            "book" => "ebooks",
            _ => v,
        };
    }

    public static CatalogWorkProgress ComputeWorkProgress(MediaWorkSummary work)
    {
        var totalUnits = Math.Max(0, work.UnitCount);
        var targetUnits = work.MonitoredUnitCount.HasValue
            ? Math.Max(0, work.MonitoredUnitCount.Value)
            : totalUnits;
        var downloadedUnits = work.DownloadedUnitCount.HasValue
            ? Math.Max(0, work.DownloadedUnitCount.Value)
            : Math.Max(0, Math.Min(work.FileCount, targetUnits > 0 ? targetUnits : Math.Max(work.FileCount, totalUnits)));
        var missingMonitoredUnits = Math.Max(0, targetUnits - downloadedUnits);
        var isComplete = targetUnits > 0 && downloadedUnits >= targetUnits;
        var hasMissingMonitored = missingMonitoredUnits > 0;
        var isMissing = !isComplete && (hasMissingMonitored || (work.Monitored && work.UnitCount == 0));

        return new CatalogWorkProgress(
            DownloadedUnits: downloadedUnits,
            TargetUnits: targetUnits,
            TotalUnits: totalUnits,
            MissingMonitoredUnits: missingMonitoredUnits,
            IsComplete: isComplete,
            HasMissingMonitored: hasMissingMonitored,
            IsMissing: isMissing);
    }

    public static string? ResolveClassificationKey(MediaWorkSummary work)
    {
        var tagsSet = work.Tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var kind = (work.WorkKind ?? string.Empty).Trim().ToLowerInvariant();
        var isMovie = kind is "movie" or "movies";

        if (tagsSet.Contains("anime-movie") || (tagsSet.Contains("anime") && isMovie))
        {
            return "anime-movie";
        }

        if (tagsSet.Contains("anime-series") || tagsSet.Contains("anime") || (!isMovie && kind == "anime"))
        {
            return "anime-series";
        }

        if (tagsSet.Contains("cartoon-movie") || (tagsSet.Contains("cartoon") && isMovie))
        {
            return "cartoon-movie";
        }

        if (tagsSet.Contains("cartoon-series") || tagsSet.Contains("cartoon"))
        {
            return "cartoon";
        }

        if (tagsSet.Contains("live-action-movie") || (tagsSet.Contains("live-action") && isMovie))
        {
            return "live-action-movie";
        }

        if (tagsSet.Contains("live-action-series") || tagsSet.Contains("live-action"))
        {
            return "live-action-series";
        }

        return null;
    }

    public static string CollapseQueryText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    public static bool MatchesQuery(MediaWorkSummary work, string? rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
        {
            return true;
        }

        var q = rawQuery.Trim();
        if (work.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (work.AltTitles.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (work.Year.HasValue && work.Year.Value.ToString(CultureInfo.InvariantCulture).Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (work.ExternalIds.Any(kvp =>
                $"{kvp.Key}:{kvp.Value}".Contains(q, StringComparison.OrdinalIgnoreCase) ||
                kvp.Value.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (work.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var collapsedQ = CollapseQueryText(q);
        if (collapsedQ.Length >= 2)
        {
            if (CollapseQueryText(work.Title).Contains(collapsedQ, StringComparison.Ordinal))
            {
                return true;
            }

            if (work.AltTitles.Any(a => CollapseQueryText(a).Contains(collapsedQ, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<MediaWorkSummary> Apply(
        IEnumerable<MediaWorkSummary> works,
        IReadOnlyDictionary<long, LibrarySummary> accessibleLibsById,
        CatalogWorkQueryFilter filter)
    {
        var libraryIdsSet = ParseLongSet(filter.LibraryIds);
        var verticalTokens = ParseStringSet(filter.Vertical, NormalizeVertical);
        var availabilityTokens = ParseStringSet(filter.Availability, s => s.Trim().ToLowerInvariant());
        var classificationTokens = ParseStringSet(filter.Classification, s => s.Trim().ToLowerInvariant());
        classificationTokens?.Remove("all");
        var normalizedHealth = string.IsNullOrWhiteSpace(filter.Health) ? null : filter.Health.Trim().ToLowerInvariant();

        var filtered = works.Where(w =>
        {
            if (!accessibleLibsById.ContainsKey(w.LibraryId))
            {
                return false;
            }

            if (filter.LibraryId.HasValue && w.LibraryId != filter.LibraryId.Value)
            {
                return false;
            }

            if (libraryIdsSet is { Count: > 0 } && !libraryIdsSet.Contains(w.LibraryId))
            {
                return false;
            }

            if (verticalTokens is { Count: > 0 } &&
                !verticalTokens.Contains(NormalizeVertical(w.WorkKind)) &&
                !verticalTokens.Contains(w.WorkKind.Trim().ToLowerInvariant()))
            {
                return false;
            }

            if (filter.Monitored.HasValue && w.Monitored != filter.Monitored.Value)
            {
                return false;
            }

            if (availabilityTokens is { Count: > 0 } && !MatchesAvailability(w, availabilityTokens))
            {
                return false;
            }

            if (classificationTokens is { Count: > 0 } && !MatchesClassification(w, classificationTokens))
            {
                return false;
            }

            if (normalizedHealth is not null && !MatchesHealth(w, normalizedHealth, accessibleLibsById))
            {
                return false;
            }

            if (!MatchesQuery(w, filter.Query))
            {
                return false;
            }

            return true;
        });

        IEnumerable<MediaWorkSummary> ordered = filtered;
        if (!string.IsNullOrWhiteSpace(filter.Sort) || !string.IsNullOrWhiteSpace(filter.Order))
        {
            var sortField = (filter.Sort ?? "title").Trim().ToLowerInvariant();
            var desc = string.Equals(filter.Order?.Trim(), "desc", StringComparison.OrdinalIgnoreCase);

            ordered = sortField switch
            {
                "year" => desc
                    ? filtered.OrderByDescending(w => w.Year ?? 0).ThenByDescending(w => w.Title, StringComparer.OrdinalIgnoreCase)
                    : filtered.OrderBy(w => w.Year ?? 0).ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase),
                "missing" => desc
                    ? filtered.OrderByDescending(w => ComputeWorkProgress(w).MissingMonitoredUnits).ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
                    : filtered.OrderBy(w => ComputeWorkProgress(w).MissingMonitoredUnits).ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase),
                "added" or "addedat" or "added_at" or "dateadded" or "date_added" => desc
                    ? filtered.OrderByDescending(w => ParseInstantTicks(w.AddedAt))
                        .ThenByDescending(w => w.AddedAt, StringComparer.Ordinal)
                        .ThenByDescending(w => w.Id)
                    : filtered.OrderBy(w => ParseInstantTicks(w.AddedAt))
                        .ThenBy(w => w.AddedAt, StringComparer.Ordinal)
                        .ThenBy(w => w.Id),
                "updated" or "updatedat" or "updated_at" => desc
                    ? filtered.OrderByDescending(w => ParseInstantTicks(w.UpdatedAt))
                        .ThenByDescending(w => w.UpdatedAt, StringComparer.Ordinal)
                        .ThenByDescending(w => w.Id)
                    : filtered.OrderBy(w => ParseInstantTicks(w.UpdatedAt))
                        .ThenBy(w => w.UpdatedAt, StringComparer.Ordinal)
                        .ThenBy(w => w.Id),
                _ => desc
                    ? filtered.OrderByDescending(w => w.Title, StringComparer.OrdinalIgnoreCase).ThenByDescending(w => w.Id)
                    : filtered.OrderBy(w => w.Title, StringComparer.OrdinalIgnoreCase).ThenBy(w => w.Id),
            };
        }

        if (filter.Offset is > 0)
        {
            ordered = ordered.Skip(filter.Offset.Value);
        }

        if (filter.Limit is > 0)
        {
            ordered = ordered.Take(filter.Limit.Value);
        }

        return ordered.ToList();
    }

    private static bool MatchesAvailability(MediaWorkSummary work, HashSet<string> tokens)
    {
        var progress = ComputeWorkProgress(work);
        var statusNorm = (work.Status ?? string.Empty).Trim().ToLowerInvariant();
        foreach (var token in tokens)
        {
            var matched = token switch
            {
                "missing" => progress.IsMissing,
                "complete" or "downloaded" => progress.IsComplete,
                "continuing" => statusNorm == "continuing",
                _ => statusNorm == token,
            };
            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesClassification(MediaWorkSummary work, HashSet<string> tokens)
    {
        var key = ResolveClassificationKey(work);
        foreach (var token in tokens)
        {
            var matched = token switch
            {
                "cartoon" => key is "cartoon" or "cartoon-series" or "cartoon-movie" ||
                    work.Tags.Any(t => t.Contains("cartoon", StringComparison.OrdinalIgnoreCase)),
                "cartoon-series" => key is "cartoon" or "cartoon-series",
                "cartoon-movie" => key == "cartoon-movie",
                "anime-series" => key == "anime-series",
                "anime-movie" => key == "anime-movie",
                "live-action-series" => key == "live-action-series",
                "live-action-movie" => key == "live-action-movie",
                _ => string.Equals(key, token, StringComparison.OrdinalIgnoreCase),
            };
            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesHealth(
        MediaWorkSummary work,
        string normalizedHealth,
        IReadOnlyDictionary<long, LibrarySummary> libsById)
    {
        var progress = ComputeWorkProgress(work);
        return normalizedHealth switch
        {
            "duplicates" => work.FileCount > work.UnitCount,
            "wrong-kind" => libsById.TryGetValue(work.LibraryId, out var lib) &&
                            !string.Equals(NormalizeVertical(lib.MediaType), NormalizeVertical(work.WorkKind), StringComparison.Ordinal),
            "non-canonical-quality" => work.FileCount > 0,
            "missing" or "missing-files" or "warning" or "error" or "unhealthy" or "issues" => !progress.IsComplete,
            "ok" or "healthy" or "complete" => progress.IsComplete,
            "unmonitored" => !work.Monitored,
            _ => true,
        };
    }

    private static HashSet<long>? ParseLongSet(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return null;
        }

        var set = new HashSet<long>();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                set.Add(id);
            }
        }

        return set.Count == 0 ? null : set;
    }

    private static HashSet<string>? ParseStringSet(string? csv, Func<string, string> normalize)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return null;
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var norm = normalize(part);
            if (!string.IsNullOrWhiteSpace(norm))
            {
                set.Add(norm);
            }
        }

        return set.Count == 0 ? null : set;
    }

    private static long ParseInstantTicks(string? raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
            ? dto.UtcTicks
            : long.MinValue;
}
