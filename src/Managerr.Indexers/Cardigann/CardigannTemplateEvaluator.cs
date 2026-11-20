using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Managerr.Indexers.Cardigann;

public sealed record CardigannQueryContext
{
    public string Keywords { get; init; } = "";
    public string Q { get; init; } = "";
    public string Type { get; init; } = "search";
    public string Season { get; init; } = "";
    public string Ep { get; init; } = "";
    public string Episode => Ep;
    public string Series { get; init; } = "";
    public string Album { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Author { get; init; } = "";
    public string Title { get; init; } = "";
    public string Year { get; init; } = "";
    public string IMDBID { get; init; } = "";
    public string IMDBIDShort { get; init; } = "";
    public string TMDBID { get; init; } = "";
    public string TVDBID { get; init; } = "";
    public string Genre { get; init; } = "";
    public int Page { get; init; } = 1;
    public int Limit { get; init; } = 100;
    public int Offset { get; init; }
}

public sealed class CardigannTemplateContext
{
    public string Keywords { get; set; } = "";
    public IReadOnlyList<string> Categories { get; set; } = [];
    public CardigannQueryContext Query { get; set; } = new();
    public Dictionary<string, object?> Config { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> Result { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset Today { get; set; } = DateTimeOffset.UtcNow;

    public static CardigannTemplateContext Create(
        CardigannFullDefinition? def,
        string baseUrl,
        string? apiKey,
        IReadOnlyDictionary<string, string>? extraSettings,
        IndexerSearchQuery? query = null,
        IReadOnlyList<string>? trackerCategories = null,
        DateTimeOffset? nowUtc = null)
    {
        var ctx = new CardigannTemplateContext
        {
            Today = nowUtc ?? DateTimeOffset.UtcNow,
            Categories = trackerCategories ?? [],
        };

        var normalizedSiteLink = string.IsNullOrWhiteSpace(baseUrl)
            ? (def?.Summary.Links.FirstOrDefault() ?? "")
            : (baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        ctx.Config["sitelink"] = normalizedSiteLink;

        if (def is not null)
        {
            foreach (var setting in def.Summary.Settings)
            {
                if (setting.DefaultValue is not null)
                {
                    ctx.Config[setting.Name] = ConvertSettingValue(setting.Type, setting.DefaultValue);
                }
                else if (string.Equals(setting.Type, "checkbox", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Config[setting.Name] = false;
                }
                else
                {
                    ctx.Config[setting.Name] = "";
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            ctx.Config["apikey"] = apiKey;
        }

        if (extraSettings is not null)
        {
            var settingTypes = def?.Summary.Settings.ToDictionary(
                s => s.Name,
                s => s.Type,
                StringComparer.OrdinalIgnoreCase);

            foreach (var (key, val) in extraSettings)
            {
                if (val is null)
                {
                    continue;
                }

                var sType = settingTypes is not null && settingTypes.TryGetValue(key, out var t) ? t : "text";
                ctx.Config[key] = ConvertSettingValue(sType, val);
            }
        }

        if (query is not null)
        {
            var rawQ = (query.Query ?? "").Trim();
            var rawKeywords = rawQ;
            if (query.Season.HasValue)
            {
                var seSuffix = query.Episode.HasValue
                    ? $"S{query.Season.Value:D2}E{query.Episode.Value:D2}"
                    : $"S{query.Season.Value:D2}";
                if (!rawKeywords.Contains(seSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    rawKeywords = string.IsNullOrWhiteSpace(rawKeywords) ? seSuffix : $"{rawKeywords} {seSuffix}";
                }
            }

            var (imdbFull, imdbShort) = NormalizeImdbIds(query.ImdbId);
            var vertical = query.MediaVertical?.Trim().ToLowerInvariant() ?? "";
            var queryType = vertical switch
            {
                "movies" when !string.IsNullOrEmpty(imdbFull) || query.TmdbId.HasValue => "movie",
                "tv" or "anime" when query.TvdbId.HasValue || query.Season.HasValue => "tvsearch",
                "music" => "music",
                "ebooks" or "audiobooks" or "comics" => "book",
                _ => "search",
            };

            ctx.Keywords = rawKeywords;
            ctx.Query = new CardigannQueryContext
            {
                Keywords = rawKeywords,
                Q = rawQ,
                Type = queryType,
                Season = query.Season?.ToString(CultureInfo.InvariantCulture) ?? "",
                Ep = query.Episode?.ToString(CultureInfo.InvariantCulture) ?? "",
                Series = query.Series ?? (vertical is "tv" or "anime" ? rawQ : ""),
                Album = query.Album ?? "",
                Artist = query.Artist ?? "",
                Author = query.Author ?? "",
                Title = query.Title ?? (vertical is "movies" or "ebooks" or "audiobooks" or "comics" ? rawQ : ""),
                Year = query.Year?.ToString(CultureInfo.InvariantCulture) ?? "",
                IMDBID = imdbFull,
                IMDBIDShort = imdbShort,
                TMDBID = query.TmdbId?.ToString(CultureInfo.InvariantCulture) ?? "",
                TVDBID = query.TvdbId?.ToString(CultureInfo.InvariantCulture) ?? "",
            };

            if (def?.Search is { KeywordsFilters.Count: > 0 } searchSpec && !string.IsNullOrEmpty(rawKeywords))
            {
                var filtered = CardigannFilterPipeline.ApplyFilters(rawKeywords, searchSpec.KeywordsFilters, ctx, ctx.Today);
                ctx.Keywords = filtered;
                ctx.Query = ctx.Query with { Keywords = filtered };
            }
        }

        return ctx;
    }

    internal static object ConvertSettingValue(string settingType, string rawValue)
    {
        if (string.Equals(settingType, "checkbox", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(rawValue, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(rawValue, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(rawValue, "yes", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(rawValue, "on", StringComparison.OrdinalIgnoreCase);
        }

        return rawValue;
    }

    internal static (string Full, string Short) NormalizeImdbIds(string? rawImdb)
    {
        if (string.IsNullOrWhiteSpace(rawImdb))
        {
            return ("", "");
        }

        var trimmed = rawImdb.Trim();
        if (trimmed.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            var digits = trimmed[2..].Trim();
            return string.IsNullOrEmpty(digits) ? ("", "") : ($"tt{digits}", digits);
        }

        var padded = trimmed.Length < 7 && trimmed.All(char.IsDigit) ? trimmed.PadLeft(7, '0') : trimmed;
        return ($"tt{padded}", padded);
    }
}

public static partial class CardigannTemplateEvaluator
{
    public const int MaxEvaluationSteps = 10_000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> BuiltinFunctions = new(StringComparer.Ordinal)
    {
        "and", "or", "not",
        "eq", "ne", "gt", "ge", "lt", "le",
        "join", "replace", "re_replace",
        "tolower", "toupper", "trim",
        "urlencode", "urlquery", "urldecode",
        "printf", "print", "println",
        "len", "split", "index",
        "contains", "hasPrefix", "hasSuffix", "html",
    };

    [GeneratedRegex(@"\$(\d+|\{\d+\})")]
    private static partial Regex GoZeroGroupRegex();

    public static string Evaluate(string? template, CardigannTemplateContext context)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("{{", StringComparison.Ordinal))
        {
            return template ?? "";
        }

        var segments = TokenizeTemplateSegments(template);
        var idx = 0;
        var ast = ParseBlockList(segments, ref idx, stopTokens: null);

        var sb = new StringBuilder(template.Length);
        var budget = new EvalBudget();
        var scope = new EvalScope(context, CurrentDot: context, Variables: new Dictionary<string, object?>(StringComparer.Ordinal), Budget: budget);
        foreach (var node in ast)
        {
            EvaluateNode(node, scope, sb);
        }

        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Segment Tokenizer (handles {{ ... }}, {{- ... -}}, {{/* comments */}}, quoted "}}")
    // -------------------------------------------------------------------------
    private readonly record struct RawSegment(bool IsAction, string Content);

    private static List<RawSegment> TokenizeTemplateSegments(string template)
    {
        var raw = new List<(bool IsAction, string Content, bool TrimLeft, bool TrimRight)>();
        var pos = 0;
        var len = template.Length;

        while (pos < len)
        {
            var openIdx = template.IndexOf("{{", pos, StringComparison.Ordinal);
            if (openIdx < 0)
            {
                raw.Add((false, template[pos..], false, false));
                break;
            }

            if (openIdx > pos)
            {
                raw.Add((false, template[pos..openIdx], false, false));
            }

            var actionStart = openIdx + 2;
            var trimLeft = false;
            if (actionStart < len && template[actionStart] == '-' &&
                actionStart + 1 < len && char.IsWhiteSpace(template[actionStart + 1]))
            {
                trimLeft = true;
                actionStart++;
            }

            var closeIdx = FindActionClose(template, actionStart);
            if (closeIdx < 0)
            {
                // Unclosed '{{' -> treat rest as literal so malformed snippets degrade safely
                raw.Add((false, template[openIdx..], false, false));
                break;
            }

            var actionEnd = closeIdx;
            var trimRight = false;
            if (actionEnd > actionStart && template[actionEnd - 1] == '-' &&
                actionEnd - 2 >= actionStart && char.IsWhiteSpace(template[actionEnd - 2]))
            {
                trimRight = true;
                actionEnd--;
            }

            var inner = template[actionStart..actionEnd].Trim();
            pos = closeIdx + 2;

            if (inner.StartsWith("/*", StringComparison.Ordinal) && inner.EndsWith("*/", StringComparison.Ordinal))
            {
                // Comment node -> still honor {{- and -}} whitespace trimming
                raw.Add((true, "", trimLeft, trimRight));
                continue;
            }

            raw.Add((true, inner, trimLeft, trimRight));
        }

        // Apply {{- and -}} trimming to adjacent literal text segments
        var texts = raw.Select(r => r.Content).ToArray();
        for (var i = 0; i < raw.Count; i++)
        {
            if (!raw[i].IsAction)
            {
                continue;
            }

            if (raw[i].TrimLeft && i > 0 && !raw[i - 1].IsAction)
            {
                texts[i - 1] = texts[i - 1].TrimEnd();
            }

            if (raw[i].TrimRight && i + 1 < raw.Count && !raw[i + 1].IsAction)
            {
                texts[i + 1] = texts[i + 1].TrimStart();
            }
        }

        var result = new List<RawSegment>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            if (raw[i].IsAction && string.IsNullOrEmpty(texts[i]))
            {
                continue;
            }
            result.Add(new RawSegment(raw[i].IsAction, texts[i]));
        }

        return result;
    }

    private static int FindActionClose(string template, int start)
    {
        var i = start;
        var len = template.Length;
        while (i < len - 1)
        {
            var c = template[i];
            if (c == '"')
            {
                i++;
                while (i < len)
                {
                    if (template[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }
                    if (template[i] == '"')
                    {
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            if (c == '`')
            {
                i++;
                while (i < len && template[i] != '`')
                {
                    i++;
                }
                i++;
                continue;
            }

            if (c == '}' && template[i + 1] == '}')
            {
                return i;
            }

            i++;
        }

        return -1;
    }

    // -------------------------------------------------------------------------
    // Expression Lexer
    // -------------------------------------------------------------------------
    private enum ExprTokenKind
    {
        Identifier,
        DotPath,
        Variable,
        StringLiteral,
        NumberLiteral,
        Pipe,
        LParen,
        RParen,
        AssignDecl,
        AssignEq,
        Comma,
        Eof,
    }

    private readonly record struct ExprToken(ExprTokenKind Kind, string Value);

    private static List<ExprToken> LexExpression(string expr)
    {
        var tokens = new List<ExprToken>();
        var i = 0;
        var len = expr.Length;

        while (i < len)
        {
            if (char.IsWhiteSpace(expr[i]))
            {
                i++;
                continue;
            }

            var c = expr[i];
            if (c == '|')
            {
                tokens.Add(new ExprToken(ExprTokenKind.Pipe, "|"));
                i++;
                continue;
            }
            if (c == '(')
            {
                tokens.Add(new ExprToken(ExprTokenKind.LParen, "("));
                i++;
                continue;
            }
            if (c == ')')
            {
                tokens.Add(new ExprToken(ExprTokenKind.RParen, ")"));
                i++;
                continue;
            }
            if (c == ',')
            {
                tokens.Add(new ExprToken(ExprTokenKind.Comma, ","));
                i++;
                continue;
            }
            if (c == ':' && i + 1 < len && expr[i + 1] == '=')
            {
                tokens.Add(new ExprToken(ExprTokenKind.AssignDecl, ":="));
                i += 2;
                continue;
            }
            if (c == '=')
            {
                tokens.Add(new ExprToken(ExprTokenKind.AssignEq, "="));
                i++;
                continue;
            }

            if (c is '"' or '\'')
            {
                var quote = c;
                i++;
                var sb = new StringBuilder();
                while (i < len && expr[i] != quote)
                {
                    if (expr[i] == '\\' && i + 1 < len)
                    {
                        var next = expr[i + 1];
                        sb.Append(next switch
                        {
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            '\\' => '\\',
                            '"' => '"',
                            '\'' => '\'',
                            _ => next,
                        });
                        i += 2;
                        continue;
                    }
                    sb.Append(expr[i]);
                    i++;
                }
                if (i < len && expr[i] == quote)
                {
                    i++;
                }
                tokens.Add(new ExprToken(ExprTokenKind.StringLiteral, sb.ToString()));
                continue;
            }

            if (c == '`')
            {
                i++;
                var start = i;
                while (i < len && expr[i] != '`')
                {
                    i++;
                }
                var rawStr = expr[start..i];
                if (i < len)
                {
                    i++;
                }
                tokens.Add(new ExprToken(ExprTokenKind.StringLiteral, rawStr));
                continue;
            }

            if (char.IsDigit(c) || (c == '-' && i + 1 < len && char.IsDigit(expr[i + 1])))
            {
                var start = i;
                i++;
                while (i < len && (char.IsDigit(expr[i]) || expr[i] == '.'))
                {
                    i++;
                }
                tokens.Add(new ExprToken(ExprTokenKind.NumberLiteral, expr[start..i]));
                continue;
            }

            if (c == '$')
            {
                var start = i;
                i++;
                while (i < len && (char.IsLetterOrDigit(expr[i]) || expr[i] is '_' or '.'))
                {
                    i++;
                }
                tokens.Add(new ExprToken(ExprTokenKind.Variable, expr[start..i]));
                continue;
            }

            if (c == '.')
            {
                var start = i;
                i++;
                while (i < len && (char.IsLetterOrDigit(expr[i]) || expr[i] is '_' or '.' or '-'))
                {
                    i++;
                }
                tokens.Add(new ExprToken(ExprTokenKind.DotPath, expr[start..i]));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                i++;
                while (i < len && (char.IsLetterOrDigit(expr[i]) || expr[i] is '_' or '.'))
                {
                    i++;
                }
                tokens.Add(new ExprToken(ExprTokenKind.Identifier, expr[start..i]));
                continue;
            }

            i++;
        }

        tokens.Add(new ExprToken(ExprTokenKind.Eof, ""));
        return tokens;
    }

    // -------------------------------------------------------------------------
    // AST Nodes & Parser
    // -------------------------------------------------------------------------
    private abstract record TemplateNode;
    private sealed record TextNode(string Text) : TemplateNode;
    private sealed record EvalActionNode(PipelineExpr Pipeline) : TemplateNode;
    private sealed record IfNode(PipelineExpr Condition, List<TemplateNode> ThenBranch, List<TemplateNode> ElseBranch) : TemplateNode;
    private sealed record RangeNode(string? IndexVar, string? ValueVar, PipelineExpr Collection, List<TemplateNode> Body, List<TemplateNode> ElseBranch) : TemplateNode;

    private sealed record PipelineExpr(string? AssignVar, List<CommandExpr> Stages);
    private abstract record CommandExpr;
    private sealed record ValueCommandExpr(AtomExpr Atom) : CommandExpr;
    private sealed record CallCommandExpr(string FunctionName, List<AtomExpr> Args) : CommandExpr;

    private abstract record AtomExpr;
    private sealed record LiteralAtom(object? Value) : AtomExpr;
    private sealed record DotPathAtom(string Path) : AtomExpr;
    private sealed record VariableAtom(string VarPath) : AtomExpr;
    private sealed record ParenAtom(PipelineExpr Inner) : AtomExpr;

    private static List<TemplateNode> ParseBlockList(
        List<RawSegment> segments,
        ref int idx,
        HashSet<string>? stopTokens)
    {
        var nodes = new List<TemplateNode>();

        while (idx < segments.Count)
        {
            var seg = segments[idx];
            if (!seg.IsAction)
            {
                nodes.Add(new TextNode(seg.Content));
                idx++;
                continue;
            }

            var tokens = LexExpression(seg.Content);
            var head = tokens[0];

            if (head.Kind == ExprTokenKind.Identifier && stopTokens is not null && stopTokens.Contains(head.Value))
            {
                return nodes;
            }

            if (head is { Kind: ExprTokenKind.Identifier, Value: "if" })
            {
                idx++;
                var condPos = 1;
                var condPipe = ParsePipeline(tokens, ref condPos);
                nodes.Add(ParseIfContinuation(condPipe, segments, ref idx));
                continue;
            }

            if (head is { Kind: ExprTokenKind.Identifier, Value: "range" })
            {
                idx++;
                var rPos = 1;
                string? indexVar = null;
                string? valueVar = null;

                if (tokens[rPos].Kind == ExprTokenKind.Variable &&
                    rPos + 1 < tokens.Count &&
                    tokens[rPos + 1].Kind is ExprTokenKind.AssignDecl or ExprTokenKind.AssignEq)
                {
                    valueVar = tokens[rPos].Value;
                    rPos += 2;
                }
                else if (tokens[rPos].Kind == ExprTokenKind.Variable &&
                         rPos + 3 < tokens.Count &&
                         tokens[rPos + 1].Kind == ExprTokenKind.Comma &&
                         tokens[rPos + 2].Kind == ExprTokenKind.Variable &&
                         tokens[rPos + 3].Kind is ExprTokenKind.AssignDecl or ExprTokenKind.AssignEq)
                {
                    indexVar = tokens[rPos].Value;
                    valueVar = tokens[rPos + 2].Value;
                    rPos += 4;
                }

                var rangePipe = ParsePipeline(tokens, ref rPos);
                var body = ParseBlockList(segments, ref idx, new HashSet<string>(StringComparer.Ordinal) { "else", "end" });
                var elseBranch = new List<TemplateNode>();

                if (idx < segments.Count && segments[idx].IsAction)
                {
                    var stopLex = LexExpression(segments[idx].Content);
                    if (stopLex[0] is { Kind: ExprTokenKind.Identifier, Value: "else" })
                    {
                        idx++;
                        elseBranch = ParseBlockList(segments, ref idx, new HashSet<string>(StringComparer.Ordinal) { "end" });
                    }
                    if (idx < segments.Count && segments[idx].IsAction)
                    {
                        var endLex = LexExpression(segments[idx].Content);
                        if (endLex[0] is { Kind: ExprTokenKind.Identifier, Value: "end" })
                        {
                            idx++;
                        }
                    }
                }

                nodes.Add(new RangeNode(indexVar, valueVar, rangePipe, body, elseBranch));
                continue;
            }

            // Regular pipeline expression action
            idx++;
            var pPos = 0;
            var pipe = ParsePipeline(tokens, ref pPos);
            nodes.Add(new EvalActionNode(pipe));
        }

        return nodes;
    }

    private static IfNode ParseIfContinuation(
        PipelineExpr condition,
        List<RawSegment> segments,
        ref int idx)
    {
        var stopSet = new HashSet<string>(StringComparer.Ordinal) { "else", "end" };
        var thenBranch = ParseBlockList(segments, ref idx, stopSet);
        var elseBranch = new List<TemplateNode>();

        if (idx < segments.Count && segments[idx].IsAction)
        {
            var stopTokens = LexExpression(segments[idx].Content);
            if (stopTokens[0] is { Kind: ExprTokenKind.Identifier, Value: "else" })
            {
                idx++;
                if (stopTokens.Count > 1 && stopTokens[1] is { Kind: ExprTokenKind.Identifier, Value: "if" })
                {
                    // {{ else if <cond> }} -> nested IfNode
                    var elseIfPos = 2;
                    var elseIfCond = ParsePipeline(stopTokens, ref elseIfPos);
                    elseBranch.Add(ParseIfContinuation(elseIfCond, segments, ref idx));
                    return new IfNode(condition, thenBranch, elseBranch);
                }

                elseBranch = ParseBlockList(segments, ref idx, new HashSet<string>(StringComparer.Ordinal) { "end" });
            }

            if (idx < segments.Count && segments[idx].IsAction)
            {
                var endTokens = LexExpression(segments[idx].Content);
                if (endTokens[0] is { Kind: ExprTokenKind.Identifier, Value: "end" })
                {
                    idx++;
                }
            }
        }

        return new IfNode(condition, thenBranch, elseBranch);
    }

    private static PipelineExpr ParsePipeline(List<ExprToken> tokens, ref int pos)
    {
        string? assignVar = null;
        if (tokens[pos].Kind == ExprTokenKind.Variable &&
            pos + 1 < tokens.Count &&
            tokens[pos + 1].Kind is ExprTokenKind.AssignDecl or ExprTokenKind.AssignEq)
        {
            assignVar = tokens[pos].Value;
            pos += 2;
        }

        var stages = new List<CommandExpr> { ParseCommand(tokens, ref pos) };
        while (tokens[pos].Kind == ExprTokenKind.Pipe)
        {
            pos++;
            stages.Add(ParseCommand(tokens, ref pos));
        }

        return new PipelineExpr(assignVar, stages);
    }

    private static CommandExpr ParseCommand(List<ExprToken> tokens, ref int pos)
    {
        var tok = tokens[pos];
        if (tok.Kind == ExprTokenKind.Identifier && BuiltinFunctions.Contains(tok.Value))
        {
            var fnName = tok.Value;
            pos++;
            var args = new List<AtomExpr>();
            while (tokens[pos].Kind is not (ExprTokenKind.Pipe or ExprTokenKind.RParen or ExprTokenKind.Eof))
            {
                args.Add(ParseAtom(tokens, ref pos));
            }
            return new CallCommandExpr(fnName, args);
        }

        return new ValueCommandExpr(ParseAtom(tokens, ref pos));
    }

    private static AtomExpr ParseAtom(List<ExprToken> tokens, ref int pos)
    {
        var tok = tokens[pos];
        switch (tok.Kind)
        {
            case ExprTokenKind.LParen:
            {
                pos++;
                var inner = ParsePipeline(tokens, ref pos);
                if (tokens[pos].Kind == ExprTokenKind.RParen)
                {
                    pos++;
                }
                return new ParenAtom(inner);
            }
            case ExprTokenKind.StringLiteral:
                pos++;
                return new LiteralAtom(tok.Value);

            case ExprTokenKind.NumberLiteral:
                pos++;
                if (long.TryParse(tok.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                {
                    return new LiteralAtom(l);
                }
                if (double.TryParse(tok.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    return new LiteralAtom(d);
                }
                return new LiteralAtom(tok.Value);

            case ExprTokenKind.DotPath:
                pos++;
                return new DotPathAtom(tok.Value);

            case ExprTokenKind.Variable:
                pos++;
                return new VariableAtom(tok.Value);

            case ExprTokenKind.Identifier:
                pos++;
                return tok.Value switch
                {
                    "true" => new LiteralAtom(true),
                    "false" => new LiteralAtom(false),
                    "nil" => new LiteralAtom(null),
                    _ when tok.Value.Contains('.', StringComparison.Ordinal) => new DotPathAtom("." + tok.Value),
                    _ => new LiteralAtom(tok.Value),
                };

            default:
                pos++;
                return new LiteralAtom("");
        }
    }

    // -------------------------------------------------------------------------
    // AST Evaluator
    // -------------------------------------------------------------------------
    private sealed class EvalBudget
    {
        public int Steps { get; set; }

        public void Step(int count = 1)
        {
            Steps += count;
            if (Steps > MaxEvaluationSteps)
            {
                throw new CardigannConfigException(
                    $"Cardigann template exceeded maximum evaluation step budget ({MaxEvaluationSteps}).");
            }
        }
    }

    private sealed record EvalScope(
        CardigannTemplateContext Root,
        object? CurrentDot,
        Dictionary<string, object?> Variables,
        EvalBudget Budget);

    private static void EvaluateNode(TemplateNode node, EvalScope scope, StringBuilder sb)
    {
        scope.Budget.Step();

        switch (node)
        {
            case TextNode t:
                sb.Append(t.Text);
                break;

            case EvalActionNode act:
            {
                var val = EvaluatePipeline(act.Pipeline, scope);
                if (act.Pipeline.AssignVar is null && val is not null)
                {
                    sb.Append(FormatScalar(val));
                }
                break;
            }

            case IfNode ifNode:
            {
                var condVal = EvaluatePipeline(ifNode.Condition, scope);
                var branch = IsTruthy(condVal) ? ifNode.ThenBranch : ifNode.ElseBranch;
                foreach (var child in branch)
                {
                    EvaluateNode(child, scope, sb);
                }
                break;
            }

            case RangeNode rangeNode:
            {
                var collVal = EvaluatePipeline(rangeNode.Collection, scope);
                var items = ExpandRangeItems(collVal, scope.Budget);
                if (items.Count == 0)
                {
                    foreach (var child in rangeNode.ElseBranch)
                    {
                        EvaluateNode(child, scope, sb);
                    }
                    break;
                }

                for (var i = 0; i < items.Count; i++)
                {
                    scope.Budget.Step();
                    var childVars = new Dictionary<string, object?>(scope.Variables, StringComparer.Ordinal);
                    if (rangeNode.IndexVar is not null)
                    {
                        childVars[rangeNode.IndexVar] = i;
                    }
                    if (rangeNode.ValueVar is not null)
                    {
                        childVars[rangeNode.ValueVar] = items[i];
                    }

                    var iterScope = new EvalScope(scope.Root, items[i], childVars, scope.Budget);
                    foreach (var child in rangeNode.Body)
                    {
                        EvaluateNode(child, iterScope, sb);
                    }
                }
                break;
            }
        }
    }

    private static List<object?> ExpandRangeItems(object? collVal, EvalBudget budget)
    {
        if (collVal is null or string)
        {
            return [];
        }

        if (collVal is IEnumerable enumerable)
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
            {
                budget.Step();
                list.Add(item);
            }
            return list;
        }

        return [];
    }

    private static object? EvaluatePipeline(PipelineExpr pipeline, EvalScope scope)
    {
        object? current = null;
        for (var i = 0; i < pipeline.Stages.Count; i++)
        {
            var stage = pipeline.Stages[i];
            var hasPipedInput = i > 0;
            current = stage switch
            {
                ValueCommandExpr v => EvaluateAtom(v.Atom, scope),
                CallCommandExpr c => EvaluateFunction(c.FunctionName, c.Args, hasPipedInput, current, scope),
                _ => null,
            };
        }

        if (pipeline.AssignVar is not null)
        {
            scope.Variables[pipeline.AssignVar] = current;
        }

        return current;
    }

    private static object? EvaluateAtom(AtomExpr atom, EvalScope scope) =>
        atom switch
        {
            LiteralAtom lit => lit.Value,
            ParenAtom p => EvaluatePipeline(p.Inner, scope),
            DotPathAtom dp => ResolveDotPath(dp.Path, scope.CurrentDot, scope.Root),
            VariableAtom vp => ResolveVariable(vp.VarPath, scope),
            _ => null,
        };

    private static object? ResolveVariable(string varPath, EvalScope scope)
    {
        if (varPath == "$")
        {
            return scope.Root;
        }

        if (varPath.StartsWith("$.", StringComparison.Ordinal))
        {
            return ResolveDotPath(varPath[1..], scope.Root, scope.Root);
        }

        var dotIdx = varPath.IndexOf('.');
        if (dotIdx < 0)
        {
            return scope.Variables.GetValueOrDefault(varPath);
        }

        var rootVar = varPath[..dotIdx];
        var subPath = varPath[dotIdx..];
        var val = scope.Variables.GetValueOrDefault(rootVar);
        return ResolveDotPath(subPath, val, scope.Root);
    }

    private static object? ResolveDotPath(string path, object? target, CardigannTemplateContext root)
    {
        if (path == ".")
        {
            return target;
        }

        var segments = path.TrimStart('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        var current = target;

        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (!TryResolveProperty(current, seg, out var resolved))
            {
                // If inside a {{ range .Categories }} block where '.' is a scalar, allow direct access to root (.Config, .Query, etc.)
                if (i == 0 && !ReferenceEquals(current, root) && TryResolveProperty(root, seg, out var rootResolved))
                {
                    current = rootResolved;
                    continue;
                }
                return "";
            }
            current = resolved;
        }

        return current;
    }

    private static bool TryResolveProperty(object? obj, string name, out object? value)
    {
        value = null;
        if (obj is null)
        {
            return false;
        }

        if (obj is CardigannTemplateContext ctx)
        {
            switch (name.ToLowerInvariant())
            {
                case "keywords":
                    value = ctx.Keywords;
                    return true;
                case "categories":
                    value = ctx.Categories;
                    return true;
                case "query":
                    value = ctx.Query;
                    return true;
                case "config":
                    value = ctx.Config;
                    return true;
                case "result":
                    value = ctx.Result;
                    return true;
                case "today":
                    value = ctx.Today;
                    return true;
                case "true":
                    value = true;
                    return true;
                case "false":
                    value = false;
                    return true;
                default:
                    return false;
            }
        }

        if (obj is CardigannQueryContext q)
        {
            value = name.ToLowerInvariant() switch
            {
                "keywords" => q.Keywords,
                "q" => q.Q,
                "type" => q.Type,
                "season" => q.Season,
                "ep" or "episode" => q.Ep,
                "series" => q.Series,
                "album" => q.Album,
                "artist" => q.Artist,
                "author" => q.Author,
                "title" => q.Title,
                "year" => q.Year,
                "imdbid" => q.IMDBID,
                "imdbidshort" => q.IMDBIDShort,
                "tmdbid" => q.TMDBID,
                "tvdbid" => q.TVDBID,
                "genre" => q.Genre,
                "page" => q.Page,
                "limit" => q.Limit,
                "offset" => q.Offset,
                _ => "",
            };
            return true;
        }

        if (obj is DateTimeOffset dto)
        {
            switch (name.ToLowerInvariant())
            {
                case "year":
                    value = dto.Year;
                    return true;
                case "month":
                    value = dto.Month;
                    return true;
                case "day":
                    value = dto.Day;
                    return true;
                default:
                    return false;
            }
        }

        if (obj is IDictionary<string, object?> objDict)
        {
            value = objDict.TryGetValue(name, out var v) ? v : "";
            return true;
        }

        if (obj is IDictionary<string, string?> strDict)
        {
            value = strDict.TryGetValue(name, out var v) ? v ?? "" : "";
            return true;
        }

        if (obj is IReadOnlyDictionary<string, string> roStrDict)
        {
            value = roStrDict.TryGetValue(name, out var v) ? v : "";
            return true;
        }

        return false;
    }

    // -------------------------------------------------------------------------
    // Built-in Operators & Pipeline Functions
    // -------------------------------------------------------------------------
    private static object? EvaluateFunction(
        string fn,
        List<AtomExpr> rawArgs,
        bool hasPipedInput,
        object? pipedInput,
        EvalScope scope)
    {
        // Short-circuiting logical operators
        if (fn == "and")
        {
            object? last = true;
            foreach (var arg in rawArgs)
            {
                last = EvaluateAtom(arg, scope);
                if (!IsTruthy(last))
                {
                    return last;
                }
            }
            if (hasPipedInput)
            {
                return IsTruthy(pipedInput) ? pipedInput : false;
            }
            return last;
        }

        if (fn == "or")
        {
            object? last = false;
            foreach (var arg in rawArgs)
            {
                last = EvaluateAtom(arg, scope);
                if (IsTruthy(last))
                {
                    return last;
                }
            }
            if (hasPipedInput && IsTruthy(pipedInput))
            {
                return pipedInput;
            }
            return last;
        }

        var evalArgs = new List<object?>(rawArgs.Count + (hasPipedInput ? 1 : 0));
        foreach (var a in rawArgs)
        {
            evalArgs.Add(EvaluateAtom(a, scope));
        }

        switch (fn)
        {
            case "not":
            {
                var target = hasPipedInput && evalArgs.Count == 0 ? pipedInput : evalArgs.FirstOrDefault();
                return !IsTruthy(target);
            }

            case "eq":
            {
                if (hasPipedInput) evalArgs.Add(pipedInput);
                if (evalArgs.Count < 2) return false;
                var first = evalArgs[0];
                for (var i = 1; i < evalArgs.Count; i++)
                {
                    if (AreValuesEqual(first, evalArgs[i]))
                    {
                        return true;
                    }
                }
                return false;
            }

            case "ne":
            {
                if (hasPipedInput) evalArgs.Add(pipedInput);
                return evalArgs.Count >= 2 && !AreValuesEqual(evalArgs[0], evalArgs[1]);
            }

            case "gt":
            case "ge":
            case "lt":
            case "le":
            {
                if (hasPipedInput) evalArgs.Add(pipedInput);
                if (evalArgs.Count < 2) return false;
                var cmp = CompareValues(evalArgs[0], evalArgs[1]);
                return fn switch
                {
                    "gt" => cmp > 0,
                    "ge" => cmp >= 0,
                    "lt" => cmp < 0,
                    "le" => cmp <= 0,
                    _ => false,
                };
            }

            case "join":
            {
                // Supports both {{ join .Categories "," }} and {{ .Categories | join "," }}
                object? listObj;
                string sep;
                if (hasPipedInput && evalArgs.Count >= 1)
                {
                    listObj = pipedInput;
                    sep = FormatScalar(evalArgs[0]);
                }
                else if (evalArgs.Count >= 2)
                {
                    if (evalArgs[0] is IEnumerable and not string)
                    {
                        listObj = evalArgs[0];
                        sep = FormatScalar(evalArgs[1]);
                    }
                    else
                    {
                        sep = FormatScalar(evalArgs[0]);
                        listObj = evalArgs[1];
                    }
                }
                else
                {
                    return "";
                }

                if (listObj is IEnumerable enumerable and not string)
                {
                    var parts = new List<string>();
                    foreach (var item in enumerable)
                    {
                        parts.Add(FormatScalar(item));
                    }
                    return string.Join(sep, parts);
                }

                return FormatScalar(listObj);
            }

            case "replace":
            {
                // Direct: {{ replace src old new [count] }} | Piped: {{ src | replace old new }}
                string src, oldVal, newVal;
                int maxCount = -1;
                if (hasPipedInput && evalArgs.Count >= 2)
                {
                    src = FormatScalar(pipedInput);
                    oldVal = FormatScalar(evalArgs[0]);
                    newVal = FormatScalar(evalArgs[1]);
                    if (evalArgs.Count >= 3 && int.TryParse(FormatScalar(evalArgs[2]), out var c)) maxCount = c;
                }
                else if (evalArgs.Count >= 3)
                {
                    src = FormatScalar(evalArgs[0]);
                    oldVal = FormatScalar(evalArgs[1]);
                    newVal = FormatScalar(evalArgs[2]);
                    if (evalArgs.Count >= 4 && int.TryParse(FormatScalar(evalArgs[3]), out var c)) maxCount = c;
                }
                else
                {
                    return hasPipedInput ? FormatScalar(pipedInput) : FormatScalar(evalArgs.FirstOrDefault());
                }

                if (string.IsNullOrEmpty(oldVal) || maxCount == 0)
                {
                    return src;
                }

                if (maxCount < 0)
                {
                    return src.Replace(oldVal, newVal, StringComparison.Ordinal);
                }

                var sb = new StringBuilder();
                var searchFrom = 0;
                var replaced = 0;
                while (replaced < maxCount)
                {
                    var found = src.IndexOf(oldVal, searchFrom, StringComparison.Ordinal);
                    if (found < 0) break;
                    sb.Append(src, searchFrom, found - searchFrom);
                    sb.Append(newVal);
                    searchFrom = found + oldVal.Length;
                    replaced++;
                }
                sb.Append(src, searchFrom, src.Length - searchFrom);
                return sb.ToString();
            }

            case "re_replace":
            {
                // Direct: {{ re_replace src pattern replacement }} | Piped: {{ src | re_replace pattern replacement }}
                string src, pattern, replacement;
                if (hasPipedInput && evalArgs.Count >= 2)
                {
                    src = FormatScalar(pipedInput);
                    pattern = FormatScalar(evalArgs[0]);
                    replacement = FormatScalar(evalArgs[1]);
                }
                else if (evalArgs.Count >= 3)
                {
                    src = FormatScalar(evalArgs[0]);
                    pattern = FormatScalar(evalArgs[1]);
                    replacement = FormatScalar(evalArgs[2]);
                }
                else
                {
                    return hasPipedInput ? FormatScalar(pipedInput) : FormatScalar(evalArgs.FirstOrDefault());
                }

                return ApplyGoRegexReplace(src, pattern, replacement);
            }

            case "tolower":
            {
                var src = hasPipedInput && evalArgs.Count == 0 ? FormatScalar(pipedInput) : FormatScalar(evalArgs.FirstOrDefault());
                return src.ToLowerInvariant();
            }

            case "toupper":
            {
                var src = hasPipedInput && evalArgs.Count == 0 ? FormatScalar(pipedInput) : FormatScalar(evalArgs.FirstOrDefault());
                return src.ToUpperInvariant();
            }

            case "trim":
            {
                if (hasPipedInput)
                {
                    var src = FormatScalar(pipedInput);
                    return evalArgs.Count >= 1
                        ? src.Trim(FormatScalar(evalArgs[0]).ToCharArray())
                        : src.Trim();
                }

                if (evalArgs.Count == 1)
                {
                    return FormatScalar(evalArgs[0]).Trim();
                }

                if (evalArgs.Count >= 2)
                {
                    return FormatScalar(evalArgs[0]).Trim(FormatScalar(evalArgs[1]).ToCharArray());
                }

                return "";
            }

            case "urlencode":
            case "urlquery":
            {
                var src = hasPipedInput && evalArgs.Count == 0 ? FormatScalar(pipedInput) : FormatScalar(evalArgs.FirstOrDefault());
                return Uri.EscapeDataString(src);
            }

            case "urldecode":
            {
                var src = hasPipedInput && evalArgs.Count == 0 ? FormatScalar(pipedInput) : FormatScalar(evalArgs.FirstOrDefault());
                return CardigannFilterPipeline.SafeUrlDecode(src);
            }

            case "printf":
            {
                if (hasPipedInput)
                {
                    evalArgs.Add(pipedInput);
                }
                if (evalArgs.Count == 0)
                {
                    return "";
                }
                var fmt = FormatScalar(evalArgs[0]);
                return FormatGoPrintf(fmt, evalArgs.Skip(1).ToList());
            }

            case "print":
            {
                if (hasPipedInput) evalArgs.Add(pipedInput);
                return string.Concat(evalArgs.Select(FormatScalar));
            }

            case "println":
            {
                if (hasPipedInput) evalArgs.Add(pipedInput);
                return string.Join(" ", evalArgs.Select(FormatScalar)) + "\n";
            }

            case "len":
            {
                var target = hasPipedInput && evalArgs.Count == 0 ? pipedInput : evalArgs.FirstOrDefault();
                if (target is null) return 0L;
                if (target is string s) return (long)s.Length;
                if (target is ICollection coll) return (long)coll.Count;
                if (target is IEnumerable en)
                {
                    long count = 0;
                    foreach (var _ in en) count++;
                    return count;
                }
                return (long)FormatScalar(target).Length;
            }

            case "split":
            {
                string src, sep;
                if (hasPipedInput && evalArgs.Count >= 1)
                {
                    src = FormatScalar(pipedInput);
                    sep = FormatScalar(evalArgs[0]);
                }
                else if (evalArgs.Count >= 2)
                {
                    src = FormatScalar(evalArgs[0]);
                    sep = FormatScalar(evalArgs[1]);
                }
                else
                {
                    return Array.Empty<string>();
                }
                return src.Split(sep, StringSplitOptions.None);
            }

            case "index":
            {
                if (hasPipedInput) evalArgs.Add(pipedInput);
                if (evalArgs.Count < 2) return "";
                if (evalArgs[0] is IList list && int.TryParse(FormatScalar(evalArgs[1]), out var idx) && idx >= 0 && idx < list.Count)
                {
                    return list[idx];
                }
                if (evalArgs[0] is IDictionary<string, object?> dict)
                {
                    return dict.TryGetValue(FormatScalar(evalArgs[1]), out var dv) ? dv ?? "" : "";
                }
                return "";
            }

            case "contains":
            {
                var (s, sub) = ExtractBinaryStrings(evalArgs, hasPipedInput, pipedInput);
                return s.Contains(sub, StringComparison.Ordinal);
            }

            case "hasPrefix":
            {
                var (s, sub) = ExtractBinaryStrings(evalArgs, hasPipedInput, pipedInput);
                return s.StartsWith(sub, StringComparison.Ordinal);
            }

            case "hasSuffix":
            {
                var (s, sub) = ExtractBinaryStrings(evalArgs, hasPipedInput, pipedInput);
                return s.EndsWith(sub, StringComparison.Ordinal);
            }

            case "html":
            {
                var src = hasPipedInput && evalArgs.Count == 0 ? FormatScalar(pipedInput) : FormatScalar(evalArgs.FirstOrDefault());
                return System.Net.WebUtility.HtmlEncode(src);
            }

            default:
                return "";
        }
    }

    internal static string ApplyGoRegexReplace(string input, string pattern, string replacement)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return input;
        }

        // In Go regexp.ReplaceAllString, $0 / ${0} refers to the whole match ($& in .NET),
        // and ${name} / $1 work in .NET Regex.Replace.
        var normalizedReplacement = GoZeroGroupRegex().Replace(replacement, static m =>
        {
            var g = m.Groups[1].Value.Trim('{', '}');
            return g == "0" ? "$&" : $"${{{g}}}";
        });

        try
        {
            return Regex.Replace(input, pattern, normalizedReplacement, RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return input;
        }
    }

    internal static string FormatGoPrintf(string format, IReadOnlyList<object?> args)
    {
        var sb = new StringBuilder(format.Length + 16);
        var argIdx = 0;
        var i = 0;

        while (i < format.Length)
        {
            if (format[i] != '%')
            {
                sb.Append(format[i]);
                i++;
                continue;
            }

            if (i + 1 < format.Length && format[i + 1] == '%')
            {
                sb.Append('%');
                i += 2;
                continue;
            }

            i++; // skip '%'
            var zeroPad = false;
            var leftAlign = false;
            var plusSign = false;

            while (i < format.Length && format[i] is '0' or '-' or '+' or ' ')
            {
                if (format[i] == '0') zeroPad = true;
                if (format[i] == '-') leftAlign = true;
                if (format[i] == '+') plusSign = true;
                i++;
            }

            var widthStart = i;
            while (i < format.Length && char.IsDigit(format[i]))
            {
                i++;
            }
            var width = i > widthStart ? int.Parse(format[widthStart..i], CultureInfo.InvariantCulture) : 0;

            var precision = -1;
            if (i < format.Length && format[i] == '.')
            {
                i++;
                var precStart = i;
                while (i < format.Length && char.IsDigit(format[i]))
                {
                    i++;
                }
                precision = i > precStart ? int.Parse(format[precStart..i], CultureInfo.InvariantCulture) : 0;
            }

            if (i >= format.Length)
            {
                break;
            }

            var verb = format[i];
            i++;

            var arg = argIdx < args.Count ? args[argIdx++] : null;
            string rendered;

            switch (verb)
            {
                case 'd':
                case 'i':
                {
                    var num = CoerceLong(arg);
                    var sign = num < 0 ? "-" : (plusSign ? "+" : "");
                    var absDigits = Math.Abs(num).ToString(CultureInfo.InvariantCulture);
                    if (zeroPad && !leftAlign && width > sign.Length + absDigits.Length)
                    {
                        rendered = sign + absDigits.PadLeft(width - sign.Length, '0');
                    }
                    else
                    {
                        rendered = ApplyWidth(sign + absDigits, width, leftAlign);
                    }
                    break;
                }

                case 'f':
                {
                    var num = CoerceDouble(arg);
                    var fmtSpec = precision >= 0 ? $"F{precision}" : "F6";
                    var raw = num.ToString(fmtSpec, CultureInfo.InvariantCulture);
                    if (plusSign && num >= 0) raw = "+" + raw;
                    rendered = ApplyWidth(raw, width, leftAlign, zeroPad && !leftAlign ? '0' : ' ');
                    break;
                }

                case 'x':
                case 'X':
                {
                    var hexFmt = verb == 'X' ? "X" : "x";
                    var raw = arg is long or int
                        ? CoerceLong(arg).ToString(hexFmt, CultureInfo.InvariantCulture)
                        : Convert.ToHexString(Encoding.UTF8.GetBytes(FormatScalar(arg)));
                    if (verb == 'x') raw = raw.ToLowerInvariant();
                    rendered = ApplyWidth(raw, width, leftAlign, zeroPad && !leftAlign ? '0' : ' ');
                    break;
                }

                case 'q':
                {
                    var s = FormatScalar(arg).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
                    rendered = ApplyWidth($"\"{s}\"", width, leftAlign);
                    break;
                }

                case 't':
                    rendered = IsTruthy(arg) ? "true" : "false";
                    break;

                default: // 's', 'v', etc.
                {
                    var s = FormatScalar(arg);
                    if (precision >= 0 && precision < s.Length)
                    {
                        s = s[..precision];
                    }
                    rendered = ApplyWidth(s, width, leftAlign);
                    break;
                }
            }

            sb.Append(rendered);
        }

        return sb.ToString();
    }

    private static string ApplyWidth(string text, int width, bool leftAlign, char padChar = ' ')
    {
        if (width <= text.Length)
        {
            return text;
        }
        return leftAlign ? text.PadRight(width, ' ') : text.PadLeft(width, padChar);
    }

    private static (string Source, string Sub) ExtractBinaryStrings(
        List<object?> evalArgs,
        bool hasPipedInput,
        object? pipedInput)
    {
        if (hasPipedInput && evalArgs.Count >= 1)
        {
            return (FormatScalar(pipedInput), FormatScalar(evalArgs[0]));
        }
        if (evalArgs.Count >= 2)
        {
            return (FormatScalar(evalArgs[0]), FormatScalar(evalArgs[1]));
        }
        return ("", "");
    }

    public static bool IsTruthy(object? val) =>
        val switch
        {
            null => false,
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            double d => d != 0.0 && !double.IsNaN(d),
            string s => !string.IsNullOrEmpty(s),
            ICollection c => c.Count > 0,
            IEnumerable en => en.Cast<object?>().Any(),
            _ => true,
        };

    private static bool AreValuesEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is bool ba && b is bool bb) return ba == bb;
        if (a is bool b1 && b is string s1 && bool.TryParse(s1, out var pb1)) return b1 == pb1;
        if (b is bool b2 && a is string s2 && bool.TryParse(s2, out var pb2)) return pb2 == b2;

        if (TryCoerceNumber(a, out var da) && TryCoerceNumber(b, out var db))
        {
            return Math.Abs(da - db) < 1e-9;
        }

        return string.Equals(FormatScalar(a), FormatScalar(b), StringComparison.Ordinal);
    }

    private static int CompareValues(object? a, object? b)
    {
        if (TryCoerceNumber(a, out var da) && TryCoerceNumber(b, out var db))
        {
            return da.CompareTo(db);
        }

        return StringComparer.Ordinal.Compare(FormatScalar(a), FormatScalar(b));
    }

    private static bool TryCoerceNumber(object? val, out double result)
    {
        switch (val)
        {
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case double d:
                result = d;
                return true;
            case string s when !string.IsNullOrWhiteSpace(s) &&
                               double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static long CoerceLong(object? val) =>
        val switch
        {
            int i => i,
            long l => l,
            double d => (long)d,
            bool b => b ? 1 : 0,
            string s when long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            string s when double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dp) => (long)dp,
            _ => 0,
        };

    private static double CoerceDouble(object? val) =>
        TryCoerceNumber(val, out var d) ? d : 0.0;

    internal static string FormatScalar(object? val) =>
        val switch
        {
            null => "",
            string s => s,
            bool b => b ? "true" : "false",
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString(CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => val.ToString() ?? "",
        };
}
