using Managerr.Downloaders;
using Managerr.Host.Services;

namespace Managerr.Host.Endpoints;

internal static class InteractiveReleaseFilterHelper
{
    public static IReadOnlyList<EnrichedInteractiveReleaseRow> ApplyReleaseFilters(
        IEnumerable<EnrichedInteractiveReleaseRow> rows,
        string? q,
        string? protocol,
        bool? approvedOnly,
        bool? hideBlocklisted,
        string? sort,
        string? order,
        int? limit)
    {
        var queryNorm = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var protocolNorm = string.IsNullOrWhiteSpace(protocol) ? null : protocol.Trim();

        IEnumerable<EnrichedInteractiveReleaseRow> filtered = rows.Where(row =>
        {
            if (approvedOnly == true && (!row.Approved || row.IsBlocklisted))
            {
                return false;
            }

            if (hideBlocklisted == true && row.IsBlocklisted)
            {
                return false;
            }

            if (protocolNorm is not null &&
                !string.Equals(row.Release.Protocol, protocolNorm, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (queryNorm is not null)
            {
                var matchedTitle = row.Release.Title.Contains(queryNorm, StringComparison.OrdinalIgnoreCase);
                var matchedIndexer = !string.IsNullOrEmpty(row.Release.IndexerName) &&
                                     row.Release.IndexerName.Contains(queryNorm, StringComparison.OrdinalIgnoreCase);
                var matchedQuality = row.QualityName.Contains(queryNorm, StringComparison.OrdinalIgnoreCase);
                var matchedLang = row.Parsed.DetectedLanguages.Any(l =>
                    l.Contains(queryNorm, StringComparison.OrdinalIgnoreCase));
                var matchedCf = row.MatchedCustomFormats.Any(cf =>
                    cf.Contains(queryNorm, StringComparison.OrdinalIgnoreCase));

                if (!matchedTitle && !matchedIndexer && !matchedQuality && !matchedLang && !matchedCf)
                {
                    return false;
                }
            }

            return true;
        });

        if (!string.IsNullOrWhiteSpace(sort))
        {
            var sortField = sort.Trim().ToLowerInvariant();
            var defaultAsc = sortField is "age" or "title";
            var asc = string.IsNullOrWhiteSpace(order)
                ? defaultAsc
                : !string.Equals(order.Trim(), "desc", StringComparison.OrdinalIgnoreCase);

            filtered = sortField switch
            {
                "age" => asc
                    ? filtered.OrderBy(r => r.AgeHours).ThenByDescending(r => r.TotalWeightedScore)
                    : filtered.OrderByDescending(r => r.AgeHours).ThenByDescending(r => r.TotalWeightedScore),
                "size" => asc
                    ? filtered.OrderBy(r => r.Release.SizeBytes).ThenByDescending(r => r.TotalWeightedScore)
                    : filtered.OrderByDescending(r => r.Release.SizeBytes).ThenByDescending(r => r.TotalWeightedScore),
                "seeders" => asc
                    ? filtered.OrderBy(r => r.Release.Seeders ?? 0).ThenByDescending(r => r.TotalWeightedScore)
                    : filtered.OrderByDescending(r => r.Release.Seeders ?? 0).ThenByDescending(r => r.TotalWeightedScore),
                "title" => asc
                    ? filtered.OrderBy(r => r.Release.Title, StringComparer.OrdinalIgnoreCase)
                    : filtered.OrderByDescending(r => r.Release.Title, StringComparer.OrdinalIgnoreCase),
                _ => asc
                    ? filtered.OrderBy(r => r.TotalWeightedScore).ThenBy(r => r.Release.SizeBytes)
                    : filtered.OrderByDescending(r => r.TotalWeightedScore).ThenByDescending(r => r.Release.SizeBytes),
            };
        }

        if (limit is > 0)
        {
            filtered = filtered.Take(limit.Value);
        }

        return filtered.ToList();
    }

    public static IReadOnlyList<RecurringBlockedHashEntry> ApplyBlocklistFilters(
        IEnumerable<RecurringBlockedHashEntry> rows,
        string? q,
        string? protocol,
        long? workId,
        int? limit,
        int? offset)
    {
        var queryNorm = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var protocolNorm = string.IsNullOrWhiteSpace(protocol) ? null : protocol.Trim();

        IEnumerable<RecurringBlockedHashEntry> filtered = rows.Where(row =>
        {
            if (workId.HasValue && row.WorkId != workId.Value)
            {
                return false;
            }

            if (protocolNorm is not null &&
                !string.Equals(row.Protocol, protocolNorm, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (queryNorm is not null)
            {
                var matchedTitle = row.Title.Contains(queryNorm, StringComparison.OrdinalIgnoreCase);
                var matchedReason = row.Reason.Contains(queryNorm, StringComparison.OrdinalIgnoreCase);
                var matchedHash = row.InfoHash.Contains(queryNorm, StringComparison.OrdinalIgnoreCase);
                var matchedIndexer = !string.IsNullOrEmpty(row.IndexerName) &&
                                     row.IndexerName.Contains(queryNorm, StringComparison.OrdinalIgnoreCase);
                if (!matchedTitle && !matchedReason && !matchedHash && !matchedIndexer)
                {
                    return false;
                }
            }

            return true;
        });

        if (offset is > 0)
        {
            filtered = filtered.Skip(offset.Value);
        }

        if (limit is > 0)
        {
            filtered = filtered.Take(limit.Value);
        }

        return filtered.ToList();
    }
}
