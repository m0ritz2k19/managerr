using System.Globalization;

namespace Managerr.Cleanerr;

/// <summary>
/// Lightweight standard 5-field UTC cron expression parser and evaluator
/// supporting *, */step, ranges (a-b), stepped ranges (a-b/step), comma lists,
/// and 0/7 Sunday equivalence.
/// </summary>
public static class CleanerrCronEvaluator
{
    public static bool IsValidCron(string? cron) => TryParse(cron, out _);

    public static bool IsDue(string? cron, DateTimeOffset? lastRunAtUtc, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(cron) || !TryParse(cron, out var schedule))
        {
            return false;
        }

        var nowMinute = TruncateToMinute(nowUtc.ToUniversalTime());

        if (lastRunAtUtc is null)
        {
            return schedule.Matches(nowMinute);
        }

        var lastMinute = TruncateToMinute(lastRunAtUtc.Value.ToUniversalTime());
        if (lastMinute >= nowMinute)
        {
            return false;
        }

        // Check up to 7 days of missed minute slots so server downtime doesn't skip scheduled runs,
        // while never executing more than once per cron slot.
        var scanStart = lastMinute.AddMinutes(1);
        var minWindow = nowMinute.AddDays(-7);
        if (scanStart < minWindow)
        {
            scanStart = minWindow;
        }

        for (var cursor = nowMinute; cursor >= scanStart; cursor = cursor.AddMinutes(-1))
        {
            if (schedule.Matches(cursor))
            {
                return true;
            }
        }

        return false;
    }

    public static DateTimeOffset? GetNextOccurrenceUtc(string? cron, DateTimeOffset afterUtc)
    {
        if (string.IsNullOrWhiteSpace(cron) || !TryParse(cron, out var schedule))
        {
            return null;
        }

        var cursor = TruncateToMinute(afterUtc.ToUniversalTime()).AddMinutes(1);
        var maxScan = cursor.AddYears(2);
        while (cursor <= maxScan)
        {
            if (!schedule.Months.Contains(cursor.Month))
            {
                cursor = new DateTimeOffset(cursor.Year, cursor.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
                continue;
            }

            if (!schedule.DaysOfMonth.Contains(cursor.Day) || !schedule.DaysOfWeek.Contains((int)cursor.DayOfWeek))
            {
                cursor = new DateTimeOffset(cursor.Year, cursor.Month, cursor.Day, 0, 0, 0, TimeSpan.Zero).AddDays(1);
                continue;
            }

            if (!schedule.Hours.Contains(cursor.Hour))
            {
                cursor = new DateTimeOffset(cursor.Year, cursor.Month, cursor.Day, cursor.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
                continue;
            }

            if (schedule.Minutes.Contains(cursor.Minute))
            {
                return cursor;
            }

            cursor = cursor.AddMinutes(1);
        }

        return null;
    }

    private static DateTimeOffset TruncateToMinute(DateTimeOffset dt) =>
        new(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, TimeSpan.Zero);

    private static bool TryParse(string? cron, out ParsedSchedule schedule)
    {
        schedule = default;
        if (string.IsNullOrWhiteSpace(cron))
        {
            return false;
        }

        var trimmed = cron.Trim();
        var expanded = trimmed.ToLowerInvariant() switch
        {
            "@hourly" => "0 * * * *",
            "@daily" or "@midnight" => "0 0 * * *",
            "@weekly" => "0 0 * * 0",
            "@monthly" => "0 0 1 * *",
            "@yearly" or "@annually" => "0 0 1 1 *",
            _ => trimmed,
        };

        var parts = expanded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
        {
            return false;
        }

        if (!TryParseField(parts[0], 0, 59, isDayOfWeek: false, out var minutes) ||
            !TryParseField(parts[1], 0, 23, isDayOfWeek: false, out var hours) ||
            !TryParseField(parts[2], 1, 31, isDayOfWeek: false, out var dom) ||
            !TryParseField(parts[3], 1, 12, isDayOfWeek: false, out var months) ||
            !TryParseField(parts[4], 0, 7, isDayOfWeek: true, out var dow))
        {
            return false;
        }

        schedule = new ParsedSchedule(minutes, hours, dom, months, dow);
        return true;
    }

    private static bool TryParseField(string expr, int min, int max, bool isDayOfWeek, out HashSet<int> values)
    {
        values = [];
        var segments = expr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        foreach (var seg in segments)
        {
            var step = 1;
            var rangePart = seg;
            var slashIdx = seg.IndexOf('/');
            if (slashIdx >= 0)
            {
                rangePart = seg[..slashIdx];
                if (!int.TryParse(seg[(slashIdx + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out step) || step <= 0)
                {
                    return false;
                }
            }

            int start;
            int end;
            if (rangePart == "*")
            {
                start = min;
                end = isDayOfWeek ? 6 : max;
            }
            else if (rangePart.Contains('-'))
            {
                var bounds = rangePart.Split('-');
                if (bounds.Length != 2 ||
                    !int.TryParse(bounds[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out start) ||
                    !int.TryParse(bounds[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out end) ||
                    start < min || end > max || start > end)
                {
                    return false;
                }
            }
            else
            {
                if (!int.TryParse(rangePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out start) ||
                    start < min || start > max)
                {
                    return false;
                }
                end = slashIdx >= 0 ? (isDayOfWeek ? 6 : max) : start;
            }

            for (var v = start; v <= end; v += step)
            {
                var normalized = isDayOfWeek && v == 7 ? 0 : v;
                values.Add(normalized);
            }
        }

        return values.Count > 0;
    }

    private readonly record struct ParsedSchedule(
        HashSet<int> Minutes,
        HashSet<int> Hours,
        HashSet<int> DaysOfMonth,
        HashSet<int> Months,
        HashSet<int> DaysOfWeek)
    {
        public bool Matches(DateTimeOffset utc) =>
            Minutes.Contains(utc.Minute) &&
            Hours.Contains(utc.Hour) &&
            DaysOfMonth.Contains(utc.Day) &&
            Months.Contains(utc.Month) &&
            DaysOfWeek.Contains((int)utc.DayOfWeek);
    }
}
