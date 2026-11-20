using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace Managerr.Indexers.Cardigann;

public static partial class CardigannFilterPipeline
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static readonly (string GoToken, string DotNetToken)[] GoDateTokens =
    [
        ("January", "MMMM"),
        ("Monday", "dddd"),
        ("-07:00:00", "zzz"),
        ("-070000", "zzz"),
        ("Z07:00:00", "K"),
        ("Z070000", "K"),
        ("-07:00", "zzz"),
        ("Z07:00", "K"),
        ("-0700", "zzz"),
        ("Z0700", "K"),
        (".999999999", ".FFFFFFF"),
        (".000000000", ".fffffff"),
        (".999999", ".FFFFFF"),
        (".000000", ".ffffff"),
        (".999", ".FFF"),
        (".000", ".fff"),
        ("2006", "yyyy"),
        ("Jan", "MMM"),
        ("Mon", "ddd"),
        ("MST", "zzz"),
        ("-07", "zz"),
        ("Z07", "K"),
        ("15", "HH"),
        ("03", "hh"),
        ("04", "mm"),
        ("05", "ss"),
        ("06", "yy"),
        ("01", "MM"),
        ("02", "dd"),
        ("_2", "d"),
        ("PM", "tt"),
        ("pm", "tt"),
    ];

    private static readonly Dictionary<string, string> TimeZoneAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UTC"] = "+00:00",
        ["GMT"] = "+00:00",
        ["CET"] = "+01:00",
        ["CEST"] = "+02:00",
        ["EET"] = "+02:00",
        ["EEST"] = "+03:00",
        ["EST"] = "-05:00",
        ["EDT"] = "-04:00",
        ["CST"] = "-06:00",
        ["CDT"] = "-05:00",
        ["MST"] = "-07:00",
        ["MDT"] = "-06:00",
        ["PST"] = "-08:00",
        ["PDT"] = "-07:00",
    };

    [GeneratedRegex(@"(?<sign>[+-])(?<hh>\d{2})(?<mm>\d{2})(?!\d|:)")]
    private static partial Regex CompactOffsetRegex();

    [GeneratedRegex(@"\b(?<tz>UTC|GMT|CEST|CET|EEST|EET|EDT|EST|CDT|CST|MDT|MST|PDT|PST)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TzAbbrevRegex();

    [GeneratedRegex(@"\b(?:a|an)\s+(?<unit>second|sec|minute|min|hour|hr|day|week|wk|month|year|yr)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArticleUnitRegex();

    [GeneratedRegex(@"(?<num>\d+(?:\.\d+)?)\s*(?<unit>years?|yrs?|y|months?|mos?|mo|weeks?|wks?|w|days?|d|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex RelativeSpanRegex();

    [GeneratedRegex(@"^(?<word>today|yesterday|tomorrow)(?:[\s,]+(?:at\s+)?(?<time>.+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex DayWordRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();

    public static string ApplyFilters(
        string? rawValue,
        IEnumerable<CardigannFilterSpec>? filters,
        CardigannTemplateContext? templateContext = null,
        DateTimeOffset? referenceUtc = null)
    {
        var current = rawValue ?? "";
        if (filters is null)
        {
            return current;
        }

        foreach (var filter in filters)
        {
            var resolvedArgs = filter.Args;
            if (templateContext is not null && filter.Args.Any(a => a.Contains("{{", StringComparison.Ordinal)))
            {
                resolvedArgs = filter.Args
                    .Select(a => CardigannTemplateEvaluator.Evaluate(a, templateContext))
                    .ToList();
            }

            current = ApplyFilter(current, filter.Name, resolvedArgs, referenceUtc);
        }

        return current;
    }

    public static string ApplyFilter(
        string? value,
        string filterName,
        IReadOnlyList<string> args,
        DateTimeOffset? referenceUtc = null)
    {
        var input = value ?? "";
        var name = filterName.Trim().ToLowerInvariant();

        switch (name)
        {
            case "replace":
            {
                if (args.Count < 2 || string.IsNullOrEmpty(args[0]))
                {
                    return input;
                }
                return input.Replace(args[0], args[1], StringComparison.Ordinal);
            }

            case "re_replace":
            {
                if (args.Count < 2 || string.IsNullOrEmpty(args[0]))
                {
                    return input;
                }
                return CardigannTemplateEvaluator.ApplyGoRegexReplace(input, args[0], args[1]);
            }

            case "regexp":
            {
                if (args.Count < 1 || string.IsNullOrEmpty(args[0]))
                {
                    return input;
                }
                Match match;
                try
                {
                    match = Regex.Match(input, args[0], RegexOptions.CultureInvariant | RegexOptions.Singleline, RegexTimeout);
                }
                catch (RegexMatchTimeoutException)
                {
                    return "";
                }
                if (!match.Success)
                {
                    return "";
                }
                if (args.Count >= 2 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var grpIdx))
                {
                    return grpIdx >= 0 && grpIdx < match.Groups.Count ? match.Groups[grpIdx].Value : "";
                }
                return match.Groups.Count > 1 ? match.Groups[1].Value : match.Groups[0].Value;
            }

            case "split":
            {
                if (args.Count < 2)
                {
                    return input;
                }
                var sep = args[0];
                if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                {
                    return input;
                }
                var parts = string.IsNullOrEmpty(sep)
                    ? [input]
                    : input.Split(sep, StringSplitOptions.None);
                var resolvedIdx = index < 0 ? parts.Length + index : index;
                return resolvedIdx >= 0 && resolvedIdx < parts.Length ? parts[resolvedIdx] : "";
            }

            case "trim":
            {
                if (args.Count == 0 || string.IsNullOrEmpty(args[0]))
                {
                    return input.Trim();
                }
                return input.Trim(args[0].ToCharArray());
            }

            case "lstrip":
            case "trimleft":
            {
                return args.Count == 0 || string.IsNullOrEmpty(args[0])
                    ? input.TrimStart()
                    : input.TrimStart(args[0].ToCharArray());
            }

            case "rstrip":
            case "trimright":
            {
                return args.Count == 0 || string.IsNullOrEmpty(args[0])
                    ? input.TrimEnd()
                    : input.TrimEnd(args[0].ToCharArray());
            }

            case "prepend":
            case "strprepend":
                return args.Count >= 1 ? args[0] + input : input;

            case "append":
            case "strappend":
                return args.Count >= 1 ? input + args[0] : input;

            case "tolower":
                return input.ToLowerInvariant();

            case "toupper":
                return input.ToUpperInvariant();

            case "urldecode":
                return SafeUrlDecode(input);

            case "urlencode":
                return Uri.EscapeDataString(input);

            case "htmldecode":
                return WebUtility.HtmlDecode(input);

            case "querystring":
            {
                if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]) || string.IsNullOrWhiteSpace(input))
                {
                    return "";
                }
                return ExtractQueryStringParam(input, args[0]);
            }

            case "dateparse":
            case "strtime":
            {
                var layout = args.FirstOrDefault();
                if (TryParseGoDate(input, layout, out var parsed))
                {
                    return parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                }
                return input;
            }

            case "timeago":
            case "fuzzytime":
            case "reltime":
            {
                var now = referenceUtc ?? DateTimeOffset.UtcNow;
                if (TryParseFuzzyTime(input, now, out var parsed))
                {
                    return parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                }
                return input;
            }

            case "hexdump":
            case "strdump":
                // Cardigann diagnostic pass-through filter: inspects hex byte sequence without mutating field value
                _ = FormatHexDump(input);
                return input;

            case "validfilename":
                return MakeValidFileName(input);

            case "diacritics":
                return StripDiacritics(input);

            case "humanize":
                return HumanizeValue(input);

            default:
                return input;
        }
    }

    public static string FormatHexDump(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static string SafeUrlDecode(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "";
        }

        try
        {
            return Uri.UnescapeDataString(input.Replace('+', ' '));
        }
        catch
        {
            return WebUtility.UrlDecode(input);
        }
    }

    internal static string ExtractQueryStringParam(string rawUrlOrQuery, string paramName)
    {
        var trimmed = rawUrlOrQuery.Trim();
        var hashIdx = trimmed.IndexOf('#');
        if (hashIdx >= 0)
        {
            trimmed = trimmed[..hashIdx];
        }

        var qIdx = trimmed.IndexOf('?');
        var queryPart = qIdx >= 0 ? trimmed[(qIdx + 1)..] : trimmed;
        if (string.IsNullOrWhiteSpace(queryPart))
        {
            return "";
        }

        var collection = HttpUtility.ParseQueryString(queryPart);
        foreach (var key in collection.AllKeys)
        {
            if (string.Equals(key, paramName, StringComparison.OrdinalIgnoreCase))
            {
                return collection[key] ?? "";
            }
        }

        return "";
    }

    public static string ConvertGoDateLayoutToDotNet(string goLayout)
    {
        var sb = new StringBuilder(goLayout.Length + 8);
        var i = 0;
        var len = goLayout.Length;

        while (i < len)
        {
            var matched = false;
            foreach (var (goTok, netTok) in GoDateTokens)
            {
                if (i + goTok.Length <= len &&
                    string.CompareOrdinal(goLayout, i, goTok, 0, goTok.Length) == 0)
                {
                    // Guard word tokens like "Mon", "Jan", "PM", "pm" against matching inside longer words
                    if (char.IsLetter(goTok[0]))
                    {
                        var prevIsLetter = i > 0 && char.IsLetter(goLayout[i - 1]);
                        var nextIdx = i + goTok.Length;
                        var nextIsLetter = nextIdx < len && char.IsLetter(goLayout[nextIdx]);
                        if (prevIsLetter || nextIsLetter)
                        {
                            continue;
                        }
                    }

                    sb.Append(netTok);
                    i += goTok.Length;
                    matched = true;
                    break;
                }
            }

            if (matched)
            {
                continue;
            }

            var c = goLayout[i];
            var prevDigit = i > 0 && char.IsDigit(goLayout[i - 1]);
            var nextDigit = i + 1 < len && char.IsDigit(goLayout[i + 1]);

            if (!prevDigit && !nextDigit)
            {
                switch (c)
                {
                    case '1':
                        sb.Append('M');
                        i++;
                        continue;
                    case '2':
                        sb.Append('d');
                        i++;
                        continue;
                    case '3':
                        sb.Append('h');
                        i++;
                        continue;
                    case '4':
                        sb.Append('m');
                        i++;
                        continue;
                    case '5':
                        sb.Append('s');
                        i++;
                        continue;
                }
            }

            if (c == 'Z' && (i + 1 == len || !char.IsLetter(goLayout[i + 1])))
            {
                sb.Append('K');
                i++;
                continue;
            }

            // Escape literal characters that have special meaning in .NET custom DateTime format strings
            if (c is '/' or ':' or '\\' or '"' or '\'' || char.IsLetter(c))
            {
                if (c == '\'')
                {
                    sb.Append("\\'");
                }
                else
                {
                    sb.Append('\'').Append(c).Append('\'');
                }
            }
            else
            {
                sb.Append(c);
            }

            i++;
        }

        return sb.ToString();
    }

    public static bool TryParseGoDate(string? rawInput, string? goLayout, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return false;
        }

        var cleaned = MultiSpaceRegex().Replace(rawInput.Trim(), " ");

        // Handle explicit Unix timestamp layouts or pure epoch numbers
        if (string.Equals(goLayout, "unix", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(goLayout, "unixtime", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(goLayout, "unixms", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(goLayout, "1136239445", StringComparison.Ordinal))
        {
            if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var epoch))
            {
                var secs = string.Equals(goLayout, "unixms", StringComparison.OrdinalIgnoreCase) || epoch > 1e11
                    ? (long)(epoch / 1000.0)
                    : (long)epoch;
                result = DateTimeOffset.FromUnixTimeSeconds(secs);
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(goLayout))
        {
            var netFormat = ConvertGoDateLayoutToDotNet(goLayout.Trim());
            var candidateInput = cleaned;

            // Normalize timezone abbreviations (e.g. UTC, CET, EST) when layout uses MST / zzz
            if (goLayout.Contains("MST", StringComparison.Ordinal))
            {
                candidateInput = TzAbbrevRegex().Replace(candidateInput, static m =>
                    TimeZoneAbbreviations.TryGetValue(m.Groups["tz"].Value, out var offset) ? offset : "+00:00");
            }

            // Normalize compact numeric offsets (+0100 -> +01:00) and trailing Z when format uses zzz
            if (netFormat.Contains("zzz", StringComparison.Ordinal))
            {
                candidateInput = CompactOffsetRegex().Replace(candidateInput, "${sign}${hh}:${mm}");
                if (candidateInput.EndsWith('Z'))
                {
                    candidateInput = candidateInput[..^1] + "+00:00";
                }
            }

            var styles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
            if (DateTimeOffset.TryParseExact(candidateInput, netFormat, CultureInfo.InvariantCulture, styles, out result))
            {
                return true;
            }

            // Also support direct .NET format strings if a custom definition wrote "yyyy-MM-dd HH:mm:ss" directly
            if (DateTimeOffset.TryParseExact(cleaned, goLayout.Trim(), CultureInfo.InvariantCulture, styles, out result))
            {
                return true;
            }
        }

        return TryParseFlexibleDateTimeOffset(cleaned, out result);
    }

    public static bool TryParseFuzzyTime(string? rawInput, DateTimeOffset nowUtc, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return false;
        }

        var cleaned = MultiSpaceRegex().Replace(rawInput.Trim(), " ");
        if (cleaned.Equals("now", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("just now", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("moments ago", StringComparison.OrdinalIgnoreCase))
        {
            result = nowUtc;
            return true;
        }

        // Handle "Today [at HH:mm[:ss] [AM/PM]]", "Yesterday [at HH:mm]", "Tomorrow [at HH:mm]"
        var dayMatch = DayWordRegex().Match(cleaned);
        if (dayMatch.Success)
        {
            var word = dayMatch.Groups["word"].Value.ToLowerInvariant();
            var dayOffset = word switch
            {
                "yesterday" => -1,
                "tomorrow" => 1,
                _ => 0,
            };

            var baseDate = nowUtc.UtcDateTime.Date.AddDays(dayOffset);
            var timePart = dayMatch.Groups["time"].Value.Trim();
            if (!string.IsNullOrEmpty(timePart) &&
                DateTime.TryParse(timePart, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var parsedTime))
            {
                result = new DateTimeOffset(
                    baseDate.Year,
                    baseDate.Month,
                    baseDate.Day,
                    parsedTime.Hour,
                    parsedTime.Minute,
                    parsedTime.Second,
                    TimeSpan.Zero);
                return true;
            }

            result = dayOffset == 0
                ? nowUtc
                : new DateTimeOffset(baseDate, TimeSpan.Zero);
            return true;
        }

        // Normalize "an hour ago" / "a day ago" -> "1 hour ago" / "1 day ago"
        var normalizedRel = ArticleUnitRegex().Replace(cleaned, "1 ${unit}");
        var matches = RelativeSpanRegex().Matches(normalizedRel);
        if (matches.Count > 0 &&
            (normalizedRel.Contains("ago", StringComparison.OrdinalIgnoreCase) ||
             !cleaned.Contains('-', StringComparison.Ordinal)))
        {
            var cursor = nowUtc;
            var anyApplied = false;

            foreach (Match m in matches)
            {
                if (!double.TryParse(m.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var qty))
                {
                    continue;
                }

                var unit = m.Groups["unit"].Value.ToLowerInvariant();
                cursor = unit switch
                {
                    "y" or "yr" or "yrs" or "year" or "years" => cursor.AddDays(-qty * 365.25),
                    "mo" or "mos" or "month" or "months" => cursor.AddDays(-qty * 30.4375),
                    "w" or "wk" or "wks" or "week" or "weeks" => cursor.AddDays(-qty * 7.0),
                    "d" or "day" or "days" => cursor.AddDays(-qty),
                    "h" or "hr" or "hrs" or "hour" or "hours" => cursor.AddHours(-qty),
                    "m" or "min" or "mins" or "minute" or "minutes" => cursor.AddMinutes(-qty),
                    "s" or "sec" or "secs" or "second" or "seconds" => cursor.AddSeconds(-qty),
                    _ => cursor,
                };
                anyApplied = true;
            }

            if (anyApplied)
            {
                result = cursor;
                return true;
            }
        }

        return TryParseFlexibleDateTimeOffset(cleaned, out result);
    }

    public static bool TryParseFlexibleDateTimeOffset(string? raw, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        {
            if (unix is >= 100_000_000 and < 100_000_000_000)
            {
                result = DateTimeOffset.FromUnixTimeSeconds(unix);
                return true;
            }
            if (unix >= 100_000_000_000)
            {
                result = DateTimeOffset.FromUnixTimeMilliseconds(unix);
                return true;
            }
        }

        return DateTimeOffset.TryParse(
            trimmed,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces,
            out result);
    }

    internal static string MakeValidFileName(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "";
        }

        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (char.IsControl(ch) || ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            {
                continue;
            }
            sb.Append(ch);
        }

        return MultiSpaceRegex().Replace(sb.ToString(), " ").Trim().TrimEnd('.');
    }

    internal static string StripDiacritics(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "";
        }

        var pre = new StringBuilder(input.Length + 4);
        foreach (var c in input)
        {
            switch (c)
            {
                case 'ß': pre.Append("ss"); break;
                case 'ẞ': pre.Append("SS"); break;
                case 'æ': pre.Append("ae"); break;
                case 'Æ': pre.Append("AE"); break;
                case 'œ': pre.Append("oe"); break;
                case 'Œ': pre.Append("OE"); break;
                case 'ø': pre.Append('o'); break;
                case 'Ø': pre.Append('O'); break;
                case 'ł': pre.Append('l'); break;
                case 'Ł': pre.Append('L'); break;
                case 'đ' or 'ð': pre.Append('d'); break;
                case 'Đ' or 'Ð': pre.Append('D'); break;
                case 'þ': pre.Append("th"); break;
                case 'Þ': pre.Append("TH"); break;
                default: pre.Append(c); break;
            }
        }

        var decomposed = pre.ToString().Normalize(NormalizationForm.FormD);
        var clean = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                clean.Append(ch);
            }
        }

        return clean.ToString().Normalize(NormalizationForm.FormC);
    }

    internal static string HumanizeValue(string input)
    {
        var trimmed = input.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "";
        }

        if (ulong.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
        {
            string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
            double val = bytes;
            var uIdx = 0;
            while (val >= 1024.0 && uIdx < units.Length - 1)
            {
                val /= 1024.0;
                uIdx++;
            }
            return $"{val.ToString("0.##", CultureInfo.InvariantCulture)} {units[uIdx]}";
        }

        return MultiSpaceRegex().Replace(trimmed.Replace('_', ' '), " ").Trim();
    }
}
