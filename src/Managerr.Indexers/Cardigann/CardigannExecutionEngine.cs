using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Managerr.Indexers.Cardigann;

public sealed class CardigannExecutionEngine(
    ICardigannDefinitionCatalog catalog,
    IIndexerHttpGateway httpGateway)
{
    private static readonly HashSet<string> SupportedLoginMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "post", "form", "cookie", "get", "oneurl",
    };

    private static readonly HashSet<string> CookieAttributeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "path", "domain", "expires", "max-age", "secure", "httponly", "samesite", "priority", "partitioned",
    };

    private sealed class CachedTrackerSession
    {
        public required string ConfigFingerprint { get; init; }
        public required Dictionary<string, string> Cookies { get; init; }
        public required Dictionary<string, string> DynamicConfig { get; init; }
    }

    private readonly ConcurrentDictionary<long, CachedTrackerSession> _sessions = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _loginLocks = new();

    public void InvalidateSession(long indexerId)
    {
        if (indexerId > 0)
        {
            _sessions.TryRemove(indexerId, out _);
        }
    }

    public static void ValidateDefinition(
        CardigannFullDefinition def,
        IReadOnlyDictionary<string, string>? extraSettings = null,
        IndexerProxyInternal? matchedProxy = null)
    {
        var paths = def.Search?.Paths;
        if ((paths is null || paths.Count == 0) && string.IsNullOrWhiteSpace(def.SearchPathTemplate))
        {
            throw new CardigannConfigException(
                $"Cardigann definition '{def.Summary.Id}' defines no search paths.");
        }

        // Validate HTML CSS selectors upfront so broken custom definitions fail clearly on Test
        var hasHtmlPath = paths is null || paths.Count == 0 ||
                          paths.Any(p => string.Equals(p.ResponseType, "html", StringComparison.OrdinalIgnoreCase));
        if (hasHtmlPath && def.Search is not null)
        {
            CardigannDefinitionCatalog.ValidateCssSelector(def.Search.Rows.Selector, "search.rows.selector");
            if (def.Search.Rows.DateHeaders?.Selector is { Length: > 0 } dhSel)
            {
                CardigannDefinitionCatalog.ValidateCssSelector(dhSel, "search.rows.dateheaders.selector");
            }

            foreach (var (fieldName, fieldSpec) in def.Search.Fields)
            {
                if (!string.IsNullOrWhiteSpace(fieldSpec.Selector))
                {
                    CardigannDefinitionCatalog.ValidateCssSelector(fieldSpec.Selector, $"search.fields.{fieldName}.selector");
                }
                if (!string.IsNullOrWhiteSpace(fieldSpec.Remove))
                {
                    CardigannDefinitionCatalog.ValidateCssSelector(fieldSpec.Remove, $"search.fields.{fieldName}.remove");
                }
                if (fieldSpec.CaseMap is { Count: > 0 } && string.IsNullOrWhiteSpace(fieldSpec.Attribute))
                {
                    foreach (var (pattern, _) in fieldSpec.CaseMap)
                    {
                        CardigannDefinitionCatalog.ValidateCssSelector(pattern, $"search.fields.{fieldName}.case");
                    }
                }
            }

            foreach (var err in def.Search.Errors)
            {
                if (!string.IsNullOrWhiteSpace(err.Selector))
                {
                    CardigannDefinitionCatalog.ValidateCssSelector(err.Selector, "search.error.selector");
                }
            }
        }

        if (def.Login is not null)
        {
            var manualCookie = extraSettings is not null &&
                               extraSettings.TryGetValue("cookie", out var cVal) &&
                               !string.IsNullOrWhiteSpace(cVal);

            var hasFlareSolverr = matchedProxy is { ProxyType: "flaresolverr" or "byparr" };

            if (def.Login.Captcha is not null && !manualCookie)
            {
                var capType = def.Login.Captcha.Type.Trim().ToLowerInvariant();
                var canAutoBypass = hasFlareSolverr &&
                                    capType is "cloudflare" or "turnstile" &&
                                    string.IsNullOrWhiteSpace(def.Login.Captcha.Input);
                if (!canAutoBypass)
                {
                    throw new CardigannConfigException(
                        $"Cardigann definition '{def.Summary.Id}' requires interactive CAPTCHA login (type '{def.Login.Captcha.Type}'), " +
                        "which is not supported for headless authentication. Configure a cookie-based session or FlareSolverr where supported.");
                }
            }

            if (!SupportedLoginMethods.Contains(def.Login.Method) && !manualCookie)
            {
                throw new CardigannConfigException(
                    $"Cardigann definition '{def.Summary.Id}' uses unsupported login method '{def.Login.Method}'. " +
                    $"Supported login methods: {string.Join(", ", SupportedLoginMethods)}.");
            }

            if (!string.IsNullOrWhiteSpace(def.Login.FormSelector))
            {
                CardigannDefinitionCatalog.ValidateCssSelector(def.Login.FormSelector, "login.form");
            }
            if (!string.IsNullOrWhiteSpace(def.Login.Test?.Selector))
            {
                CardigannDefinitionCatalog.ValidateCssSelector(def.Login.Test.Selector, "login.test.selector");
            }
            foreach (var err in def.Login.Errors)
            {
                if (!string.IsNullOrWhiteSpace(err.Selector))
                {
                    CardigannDefinitionCatalog.ValidateCssSelector(err.Selector, "login.error.selector");
                }
            }
            foreach (var (sKey, sSpec) in def.Login.SelectorInputs)
            {
                if (!string.IsNullOrWhiteSpace(sSpec.Selector))
                {
                    CardigannDefinitionCatalog.ValidateCssSelector(sSpec.Selector, $"login.selectorinputs.{sKey}");
                }
            }
        }
    }

    private static string NormalizeCardigannKeywords(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var s = raw.Replace('–', ' ').Replace('—', ' ')
                   .Replace('’', '\'').Replace('‘', '\'');
        s = System.Text.RegularExpressions.Regex.Replace(s, "[!]{2,}", "!");
        s = System.Text.RegularExpressions.Regex.Replace(s, "[?]{2,}", "?");
        return s;
    }

    private static bool SearchInputsReferenceSeason(CardigannFullDefinition def, CardigannSearchPathSpec pathSpec)
    {
        static bool Check(KeyValuePair<string, string> kvp) =>
            kvp.Key.Equals("season", StringComparison.OrdinalIgnoreCase) ||
            kvp.Value.Contains(".Query.Season", StringComparison.OrdinalIgnoreCase);

        if (def.Search?.Inputs is { Count: > 0 } searchInputs && searchInputs.Any(Check))
        {
            return true;
        }

        if (pathSpec.Inputs is { Count: > 0 } pathInputs && pathInputs.Any(Check))
        {
            return true;
        }

        return false;
    }

    private static bool SearchInputsReferenceEpisode(CardigannFullDefinition def, CardigannSearchPathSpec pathSpec)
    {
        static bool Check(KeyValuePair<string, string> kvp) =>
            kvp.Key.Equals("ep", StringComparison.OrdinalIgnoreCase) ||
            kvp.Key.Equals("episode", StringComparison.OrdinalIgnoreCase) ||
            kvp.Value.Contains(".Query.Ep", StringComparison.OrdinalIgnoreCase);

        if (def.Search?.Inputs is { Count: > 0 } searchInputs && searchInputs.Any(Check))
        {
            return true;
        }

        if (pathSpec.Inputs is { Count: > 0 } pathInputs && pathInputs.Any(Check))
        {
            return true;
        }

        return false;
    }

    public async Task<IReadOnlyList<CardigannExtractedRow>> ExecuteSearchAsync(
        long indexerId,
        CardigannFullDefinition def,
        string baseUrl,
        string? apiKey,
        IReadOnlyDictionary<string, string>? extraSettings,
        IndexerProxyInternal? matchedProxy,
        IndexerSearchQuery query,
        IReadOnlyList<int>? desiredCategories,
        bool forceFreshLogin = false,
        bool strictValidation = false,
        CancellationToken ct = default)
    {
        ValidateDefinition(def, extraSettings, matchedProxy);

        var baseUri = NormalizeBaseUri(baseUrl, def);
        var fingerprint = ComputeConfigFingerprint(def.Summary.Id, baseUri.ToString(), apiKey, extraSettings);

        if (forceFreshLogin && indexerId > 0)
        {
            InvalidateSession(indexerId);
        }

        CachedTrackerSession? session = null;
        var sessionFromCache = false;
        if (def.Login is not null)
        {
            (session, sessionFromCache) = await GetOrCreateSessionAsync(
                indexerId,
                def,
                baseUri,
                apiKey,
                extraSettings,
                matchedProxy,
                fingerprint,
                forceFreshLogin,
                ct);
        }

        var matchedTrackerCats = MapDesiredCategoriesToTrackerCategories(def, desiredCategories);
        var activePaths = ResolveMatchingSearchPaths(def, desiredCategories, matchedTrackerCats);
        if (activePaths.Count == 0)
        {
            return [];
        }

        var mergedResults = new List<CardigannExtractedRow>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (pathSpec, pathTrackerCats) in activePaths)
        {
            var effectiveQuery = query;
            if (!string.IsNullOrWhiteSpace(query.Query))
            {
                effectiveQuery = query with { Query = NormalizeCardigannKeywords(query.Query) };
            }

            var ctx = CardigannTemplateContext.Create(
                def,
                baseUri.ToString(),
                apiKey,
                extraSettings,
                effectiveQuery,
                pathTrackerCats);

            var seasonReferenced = SearchInputsReferenceSeason(def, pathSpec);
            var episodeReferenced = SearchInputsReferenceEpisode(def, pathSpec);
            if (query.Season.HasValue)
            {
                var seSuffix = query.Episode.HasValue
                    ? $"S{query.Season.Value:D2}E{query.Episode.Value:D2}"
                    : $"S{query.Season.Value:D2}";

                if (seasonReferenced)
                {
                    if (query.Episode.HasValue && !episodeReferenced)
                    {
                        var epSuffix = $"E{query.Episode.Value:D2}";
                        if (ctx.Keywords.EndsWith(seSuffix, StringComparison.OrdinalIgnoreCase))
                        {
                            var prefix = ctx.Keywords[..^seSuffix.Length].TrimEnd();
                            ctx.Keywords = string.IsNullOrWhiteSpace(prefix) ? epSuffix : $"{prefix} {epSuffix}";
                            ctx.Query = ctx.Query with { Keywords = ctx.Keywords };
                        }
                    }
                    else
                    {
                        if (ctx.Keywords.EndsWith(seSuffix, StringComparison.OrdinalIgnoreCase))
                        {
                            ctx.Keywords = ctx.Keywords[..^seSuffix.Length].TrimEnd();
                            ctx.Query = ctx.Query with { Keywords = ctx.Keywords };
                        }
                    }
                }
                else
                {
                    if (!ctx.Keywords.Contains(seSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.Keywords = string.IsNullOrWhiteSpace(ctx.Keywords) ? seSuffix : $"{ctx.Keywords} {seSuffix}";
                        ctx.Query = ctx.Query with { Keywords = ctx.Keywords };
                    }
                }
            }

            ctx.Keywords = NormalizeCardigannKeywords(ctx.Keywords);
            ctx.Query = ctx.Query with { Keywords = ctx.Keywords };

            if (session is not null)
            {
                foreach (var (dk, dv) in session.DynamicConfig)
                {
                    ctx.Config[dk] = dv;
                }
            }

            var resp = await SendSearchPathRequestAsync(
                def,
                baseUri,
                pathSpec,
                ctx,
                session,
                matchedProxy,
                ct);

            // Automatic session-expiry detection + single re-authentication retry
            if (def.Login is not null && sessionFromCache && IsSessionExpired(resp, def, pathSpec, baseUri))
            {
                InvalidateSession(indexerId);
                (session, _) = await GetOrCreateSessionAsync(
                    indexerId,
                    def,
                    baseUri,
                    apiKey,
                    extraSettings,
                    matchedProxy,
                    fingerprint,
                    forceFresh: true,
                    ct);
                sessionFromCache = false;

                foreach (var (dk, dv) in session.DynamicConfig)
                {
                    ctx.Config[dk] = dv;
                }

                resp = await SendSearchPathRequestAsync(
                    def,
                    baseUri,
                    pathSpec,
                    ctx,
                    session,
                    matchedProxy,
                    ct);
            }

            if (session is not null && resp.SetCookieHeaders.Count > 0)
            {
                lock (session.Cookies)
                {
                    MergeSetCookies(resp.SetCookieHeaders, session.Cookies);
                }
            }

            if (def.Search?.Errors is { Count: > 0 } && !string.IsNullOrWhiteSpace(resp.Body))
            {
                var parser = new HtmlParser();
                using var errDoc = parser.ParseDocument(resp.Body);
                CheckErrorSelectors(errDoc, resp.FinalUri ?? baseUri, def.Search.Errors, ctx, def.Summary.Name, isLogin: false);
            }

            var extracted = catalog.ExtractReleasesFromPayload(def, resp.Body, baseUri, pathSpec, ctx, strictValidation);
            foreach (var row in extracted)
            {
                var dedupKey = !string.IsNullOrWhiteSpace(row.InfoHash)
                    ? $"ih:{row.InfoHash}"
                    : $"url:{row.Guid ?? row.DownloadUrl}";
                if (seenKeys.Add(dedupKey))
                {
                    mergedResults.Add(row);
                }
            }
        }

        return mergedResults;
    }

    private async Task<(CachedTrackerSession Session, bool FromCache)> GetOrCreateSessionAsync(
        long indexerId,
        CardigannFullDefinition def,
        Uri baseUri,
        string? apiKey,
        IReadOnlyDictionary<string, string>? extraSettings,
        IndexerProxyInternal? matchedProxy,
        string fingerprint,
        bool forceFresh,
        CancellationToken ct)
    {
        if (indexerId > 0 && !forceFresh &&
            _sessions.TryGetValue(indexerId, out var existing) &&
            string.Equals(existing.ConfigFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return (existing, true);
        }

        if (indexerId <= 0)
        {
            var ephemeral = await AuthenticateAsync(def, baseUri, apiKey, extraSettings, matchedProxy, fingerprint, ct);
            return (ephemeral, false);
        }

        var gate = _loginLocks.GetOrAdd(indexerId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!forceFresh &&
                _sessions.TryGetValue(indexerId, out var rechecked) &&
                string.Equals(rechecked.ConfigFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return (rechecked, true);
            }

            var authenticated = await AuthenticateAsync(def, baseUri, apiKey, extraSettings, matchedProxy, fingerprint, ct);
            _sessions[indexerId] = authenticated;
            return (authenticated, false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CachedTrackerSession> AuthenticateAsync(
        CardigannFullDefinition def,
        Uri baseUri,
        string? apiKey,
        IReadOnlyDictionary<string, string>? extraSettings,
        IndexerProxyInternal? matchedProxy,
        string fingerprint,
        CancellationToken ct)
    {
        var login = def.Login!;
        var ctx = CardigannTemplateContext.Create(def, baseUri.ToString(), apiKey, extraSettings);
        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dynamicConfig = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var staticCookie in login.Cookies)
        {
            ParseRawCookieString(staticCookie, cookies);
        }

        var loginHeaders = EvaluateHeaders(login.Headers, ctx);

        // Allow manual session cookie override when supplied in ExtraSettings["cookie"]
        var manualCookieOverride = extraSettings is not null &&
                                   extraSettings.TryGetValue("cookie", out var mc) &&
                                   !string.IsNullOrWhiteSpace(mc)
            ? mc
            : null;

        var effectiveMethod = login.Method;
        if (!string.IsNullOrWhiteSpace(manualCookieOverride) &&
            (effectiveMethod == "cookie" || !SupportedLoginMethods.Contains(effectiveMethod) || login.Captcha is not null))
        {
            effectiveMethod = "cookie";
        }

        string? lastLoginHtml = null;
        Uri? lastLoginUri = null;

        switch (effectiveMethod)
        {
            case "cookie":
            {
                var cookieStr = manualCookieOverride ?? ctx.Config.GetValueOrDefault("cookie")?.ToString();
                if (string.IsNullOrWhiteSpace(cookieStr))
                {
                    throw new CardigannLoginException(
                        $"Tracker '{def.Summary.Name}' requires a session cookie ('cookie' setting), but none was configured.");
                }
                ParseRawCookieString(cookieStr, cookies);
                break;
            }

            case "form":
            {
                var loginPath = CardigannTemplateEvaluator.Evaluate(login.Path ?? "login.php", ctx);
                var loginPageUri = ResolveUri(baseUri, loginPath);
                var pageResp = await httpGateway.SendAsync(
                    new IndexerHttpRequest(
                        TargetUri: loginPageUri,
                        Method: "GET",
                        Headers: loginHeaders,
                        CookieHeader: FormatCookieHeader(cookies),
                        EncodingName: def.Summary.Encoding),
                    matchedProxy,
                    ct);

                MergeSetCookies(pageResp.SetCookieHeaders, cookies);

                var parser = new HtmlParser();
                using var pageDoc = parser.ParseDocument(pageResp.Body);

                // If login.test.selector already matches on the GET page, we're already logged in
                if (!string.IsNullOrWhiteSpace(login.Test?.Selector) &&
                    CardigannDefinitionCatalog.QuerySelectorWithContains(pageDoc, login.Test.Selector, strictValidation: true, "login.test.selector") is not null)
                {
                    return new CachedTrackerSession
                    {
                        ConfigFingerprint = fingerprint,
                        Cookies = cookies,
                        DynamicConfig = dynamicConfig,
                    };
                }

                var formSelector = string.IsNullOrWhiteSpace(login.FormSelector) ? "form" : login.FormSelector;
                var formEl = CardigannDefinitionCatalog.QuerySelectorWithContains(pageDoc, formSelector, strictValidation: true, "login.form");
                if (formEl is null)
                {
                    CheckErrorSelectors(pageDoc, pageResp.FinalUri ?? loginPageUri, login.Errors, ctx, def.Summary.Name, isLogin: true);
                    throw new CardigannLoginException(
                        $"Login form matching selector '{formSelector}' was not found on '{loginPageUri}'.");
                }

                // Extract existing form fields (including hidden CSRF tokens)
                var formFields = ExtractDefaultFormInputs(formEl);

                // Overlay login.inputs
                foreach (var (inputKey, expr) in login.Inputs)
                {
                    var evaluated = CardigannTemplateEvaluator.Evaluate(expr, ctx);
                    var resolvedFieldName = ResolveFormInputFieldName(formEl, formFields, inputKey);
                    formFields[resolvedFieldName] = evaluated;
                }

                // Overlay login.selectorinputs (scraped from login page HTML)
                foreach (var (selKey, selSpec) in login.SelectorInputs)
                {
                    var extracted = CardigannDefinitionCatalog.ExtractHtmlFieldValue(
                        pageDoc.DocumentElement,
                        selSpec,
                        ctx,
                        strictValidation: true,
                        $"login.selectorinputs.{selKey}");

                    if (string.IsNullOrEmpty(extracted) && !selSpec.Optional)
                    {
                        throw new CardigannLoginException(
                            $"Required login token '{selKey}' (selector '{selSpec.Selector}') was not found on '{loginPageUri}'.");
                    }

                    if (extracted is not null)
                    {
                        formFields[selKey] = extracted;
                        ctx.Config[selKey] = extracted;
                        dynamicConfig[selKey] = extracted;
                    }
                }

                var pageEffectiveUri = pageResp.FinalUri ?? loginPageUri;
                Uri submitUri;
                if (!string.IsNullOrWhiteSpace(login.SubmitPath))
                {
                    submitUri = ResolveUri(baseUri, CardigannTemplateEvaluator.Evaluate(login.SubmitPath, ctx));
                }
                else if (formEl.GetAttribute("action") is { Length: > 0 } formAction)
                {
                    submitUri = ResolveUri(pageEffectiveUri, formAction);
                }
                else
                {
                    submitUri = pageEffectiveUri;
                }

                if (login.GetSelectorInputs.Count > 0)
                {
                    var getExtra = new List<KeyValuePair<string, string>>();
                    foreach (var (gKey, gSpec) in login.GetSelectorInputs)
                    {
                        var gVal = CardigannDefinitionCatalog.ExtractHtmlFieldValue(
                            pageDoc.DocumentElement,
                            gSpec,
                            ctx,
                            strictValidation: true,
                            $"login.getselectorinputs.{gKey}");
                        if (gVal is not null)
                        {
                            getExtra.Add(new KeyValuePair<string, string>(gKey, gVal));
                        }
                    }
                    submitUri = AppendQueryParameters(submitUri, getExtra, rawSegments: null);
                }

                var formMethod = (formEl.GetAttribute("method") ?? "POST").Trim().ToUpperInvariant();
                var submitHeaders = new Dictionary<string, string>(loginHeaders, StringComparer.OrdinalIgnoreCase);
                submitHeaders.TryAdd("Referer", pageEffectiveUri.ToString());

                var orderedPairs = formFields.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)).ToList();
                IndexerHttpRequest submitReq = formMethod == "GET"
                    ? new IndexerHttpRequest(
                        TargetUri: AppendQueryParameters(submitUri, orderedPairs, rawSegments: null),
                        Method: "GET",
                        Headers: submitHeaders,
                        CookieHeader: FormatCookieHeader(cookies),
                        EncodingName: def.Summary.Encoding)
                    : new IndexerHttpRequest(
                        TargetUri: submitUri,
                        Method: "POST",
                        Headers: submitHeaders,
                        FormFields: orderedPairs,
                        CookieHeader: FormatCookieHeader(cookies),
                        EncodingName: def.Summary.Encoding);

                var submitResp = await httpGateway.SendAsync(submitReq, matchedProxy, ct);
                MergeSetCookies(submitResp.SetCookieHeaders, cookies);
                lastLoginHtml = submitResp.Body;
                lastLoginUri = submitResp.FinalUri ?? submitUri;

                if (!string.IsNullOrWhiteSpace(lastLoginHtml))
                {
                    using var submitDoc = parser.ParseDocument(lastLoginHtml);
                    CheckErrorSelectors(submitDoc, lastLoginUri, login.Errors, ctx, def.Summary.Name, isLogin: true);
                }
                break;
            }

            case "post":
            {
                var postPath = CardigannTemplateEvaluator.Evaluate(login.Path ?? "login.php", ctx);
                var postUri = ResolveUri(baseUri, postPath);
                var (pairs, rawSegments) = EvaluateInputPairs(login.Inputs, ctx);
                if (rawSegments.Count > 0)
                {
                    postUri = AppendQueryParameters(postUri, [], rawSegments);
                }

                var postResp = await httpGateway.SendAsync(
                    new IndexerHttpRequest(
                        TargetUri: postUri,
                        Method: "POST",
                        Headers: loginHeaders,
                        FormFields: pairs,
                        CookieHeader: FormatCookieHeader(cookies),
                        EncodingName: def.Summary.Encoding),
                    matchedProxy,
                    ct);

                MergeSetCookies(postResp.SetCookieHeaders, cookies);
                lastLoginHtml = postResp.Body;
                lastLoginUri = postResp.FinalUri ?? postUri;

                if (!string.IsNullOrWhiteSpace(lastLoginHtml))
                {
                    var parser = new HtmlParser();
                    using var postDoc = parser.ParseDocument(lastLoginHtml);
                    CheckErrorSelectors(postDoc, lastLoginUri, login.Errors, ctx, def.Summary.Name, isLogin: true);
                }
                break;
            }

            case "get":
            {
                var getPath = CardigannTemplateEvaluator.Evaluate(login.Path ?? "login.php", ctx);
                var (pairs, rawSegments) = EvaluateInputPairs(login.Inputs, ctx);
                var getUri = AppendQueryParameters(ResolveUri(baseUri, getPath), pairs, rawSegments);

                var getResp = await httpGateway.SendAsync(
                    new IndexerHttpRequest(
                        TargetUri: getUri,
                        Method: "GET",
                        Headers: loginHeaders,
                        CookieHeader: FormatCookieHeader(cookies),
                        EncodingName: def.Summary.Encoding),
                    matchedProxy,
                    ct);

                MergeSetCookies(getResp.SetCookieHeaders, cookies);
                lastLoginHtml = getResp.Body;
                lastLoginUri = getResp.FinalUri ?? getUri;

                if (!string.IsNullOrWhiteSpace(lastLoginHtml))
                {
                    var parser = new HtmlParser();
                    using var getDoc = parser.ParseDocument(lastLoginHtml);
                    CheckErrorSelectors(getDoc, lastLoginUri, login.Errors, ctx, def.Summary.Name, isLogin: true);
                }
                break;
            }

            case "oneurl":
            {
                var oneUrlExpr = login.Inputs
                    .FirstOrDefault(kv => string.Equals(kv.Key, "oneurl", StringComparison.OrdinalIgnoreCase))
                    .Value;
                var oneUrlPart = !string.IsNullOrWhiteSpace(oneUrlExpr)
                    ? CardigannTemplateEvaluator.Evaluate(oneUrlExpr, ctx)
                    : (ctx.Config.GetValueOrDefault("oneurl")?.ToString() ?? "");

                var combinedPath = CardigannTemplateEvaluator.Evaluate(login.Path ?? "", ctx) + oneUrlPart;
                var oneUri = ResolveUri(baseUri, combinedPath);

                var oneResp = await httpGateway.SendAsync(
                    new IndexerHttpRequest(
                        TargetUri: oneUri,
                        Method: "GET",
                        Headers: loginHeaders,
                        CookieHeader: FormatCookieHeader(cookies),
                        EncodingName: def.Summary.Encoding),
                    matchedProxy,
                    ct);

                MergeSetCookies(oneResp.SetCookieHeaders, cookies);
                lastLoginHtml = oneResp.Body;
                lastLoginUri = oneResp.FinalUri ?? oneUri;

                if (!string.IsNullOrWhiteSpace(lastLoginHtml))
                {
                    var parser = new HtmlParser();
                    using var oneDoc = parser.ParseDocument(lastLoginHtml);
                    CheckErrorSelectors(oneDoc, lastLoginUri, login.Errors, ctx, def.Summary.Name, isLogin: true);
                }
                break;
            }

            default:
                throw new CardigannConfigException(
                    $"Unsupported Cardigann login method '{effectiveMethod}' in definition '{def.Summary.Id}'.");
        }

        // Verify login.test if declared
        if (login.Test is not null)
        {
            if (!string.IsNullOrWhiteSpace(login.Test.Path))
            {
                var testPath = CardigannTemplateEvaluator.Evaluate(login.Test.Path, ctx);
                var testUri = ResolveUri(baseUri, testPath);
                var testResp = await httpGateway.SendAsync(
                    new IndexerHttpRequest(
                        TargetUri: testUri,
                        Method: "GET",
                        Headers: loginHeaders,
                        CookieHeader: FormatCookieHeader(cookies),
                        EncodingName: def.Summary.Encoding),
                    matchedProxy,
                    ct);

                MergeSetCookies(testResp.SetCookieHeaders, cookies);
                if (testResp.StatusCode is 401 or 403)
                {
                    throw new CardigannLoginException(
                        $"Login test failed on '{def.Summary.Name}': HTTP {testResp.StatusCode} at '{testUri}'.");
                }

                var parser = new HtmlParser();
                using var testDoc = parser.ParseDocument(testResp.Body);
                CheckErrorSelectors(testDoc, testResp.FinalUri ?? testUri, login.Errors, ctx, def.Summary.Name, isLogin: true);

                if (!string.IsNullOrWhiteSpace(login.Test.Selector))
                {
                    var matched = CardigannDefinitionCatalog.QuerySelectorWithContains(
                        testDoc,
                        login.Test.Selector,
                        strictValidation: true,
                        "login.test.selector");
                    if (matched is null)
                    {
                        throw new CardigannLoginException(
                            $"Login verification failed on '{def.Summary.Name}': authenticated selector '{login.Test.Selector}' was not found on '{testUri}'.");
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(login.Test.Selector) && lastLoginHtml is not null)
            {
                var parser = new HtmlParser();
                using var lastDoc = parser.ParseDocument(lastLoginHtml);
                var matched = CardigannDefinitionCatalog.QuerySelectorWithContains(
                    lastDoc,
                    login.Test.Selector,
                    strictValidation: true,
                    "login.test.selector");
                if (matched is null)
                {
                    throw new CardigannLoginException(
                        $"Login verification failed on '{def.Summary.Name}': authenticated selector '{login.Test.Selector}' was not found after login.");
                }
            }
        }

        return new CachedTrackerSession
        {
            ConfigFingerprint = fingerprint,
            Cookies = cookies,
            DynamicConfig = dynamicConfig,
        };
    }

    private async Task<IndexerHttpResponse> SendSearchPathRequestAsync(
        CardigannFullDefinition def,
        Uri baseUri,
        CardigannSearchPathSpec pathSpec,
        CardigannTemplateContext ctx,
        CachedTrackerSession? session,
        IndexerProxyInternal? matchedProxy,
        CancellationToken ct)
    {
        var mergedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (def.Login?.Headers is { Count: > 0 })
        {
            foreach (var (hk, hv) in EvaluateHeaders(def.Login.Headers, ctx))
            {
                mergedHeaders[hk] = hv;
            }
        }
        if (def.Search?.Headers is { Count: > 0 })
        {
            foreach (var (hk, hv) in EvaluateHeaders(def.Search.Headers, ctx))
            {
                mergedHeaders[hk] = hv;
            }
        }
        if (pathSpec.Headers is { Count: > 0 })
        {
            foreach (var (hk, hv) in EvaluateHeaders(pathSpec.Headers, ctx))
            {
                mergedHeaders[hk] = hv;
            }
        }

        var combinedInputs = CombineSearchInputs(def.Search?.Inputs, pathSpec);
        var (evaluatedPairs, rawSegments) = EvaluateInputPairs(combinedInputs, ctx);

        var evaluatedPath = CardigannTemplateEvaluator.Evaluate(pathSpec.Path, ctx);
        var targetUri = ResolveUri(baseUri, evaluatedPath);

        string? cookieHeader = null;
        if (session is not null)
        {
            lock (session.Cookies)
            {
                cookieHeader = FormatCookieHeader(session.Cookies);
            }
        }

        var isPost = string.Equals(pathSpec.Method, "post", StringComparison.OrdinalIgnoreCase);
        if (!isPost)
        {
            targetUri = AppendQueryParameters(targetUri, evaluatedPairs, rawSegments);
            return await httpGateway.SendAsync(
                new IndexerHttpRequest(
                    TargetUri: targetUri,
                    Method: "GET",
                    Headers: mergedHeaders,
                    CookieHeader: cookieHeader,
                    AllowAutoRedirect: pathSpec.FollowRedirect,
                    EncodingName: def.Summary.Encoding),
                matchedProxy,
                ct);
        }

        // POST search path: support both form-urlencoded and JSON bodies
        var isJsonContent = mergedHeaders.TryGetValue("Content-Type", out var ctHeader) &&
                            ctHeader.Contains("application/json", StringComparison.OrdinalIgnoreCase);
        if (isJsonContent && rawSegments.Count > 0)
        {
            return await httpGateway.SendAsync(
                new IndexerHttpRequest(
                    TargetUri: targetUri,
                    Method: "POST",
                    Headers: mergedHeaders,
                    RawBody: string.Concat(rawSegments),
                    ContentType: "application/json",
                    CookieHeader: cookieHeader,
                    AllowAutoRedirect: pathSpec.FollowRedirect,
                    EncodingName: def.Summary.Encoding),
                matchedProxy,
                ct);
        }

        if (rawSegments.Count > 0)
        {
            targetUri = AppendQueryParameters(targetUri, [], rawSegments);
        }

        return await httpGateway.SendAsync(
            new IndexerHttpRequest(
                TargetUri: targetUri,
                Method: "POST",
                Headers: mergedHeaders,
                FormFields: evaluatedPairs,
                CookieHeader: cookieHeader,
                AllowAutoRedirect: pathSpec.FollowRedirect,
                EncodingName: def.Summary.Encoding),
            matchedProxy,
            ct);
    }

    private static bool IsSessionExpired(
        IndexerHttpResponse resp,
        CardigannFullDefinition def,
        CardigannSearchPathSpec pathSpec,
        Uri baseUri)
    {
        if (resp.StatusCode is 401 or 403)
        {
            return true;
        }

        var login = def.Login!;
        if (!string.IsNullOrWhiteSpace(login.Path) && resp.FinalUri is not null)
        {
            var expectedLoginUri = ResolveUri(baseUri, login.Path);
            var searchUri = ResolveUri(baseUri, pathSpec.Path);
            if (!string.Equals(expectedLoginUri.AbsolutePath, searchUri.AbsolutePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(resp.FinalUri.AbsolutePath, expectedLoginUri.AbsolutePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (string.Equals(pathSpec.ResponseType, "json", StringComparison.OrdinalIgnoreCase) &&
            resp.Body.TrimStart().StartsWith('<'))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(resp.Body) && resp.Body.Contains('<', StringComparison.Ordinal))
        {
            var parser = new HtmlParser();
            using var doc = parser.ParseDocument(resp.Body);

            foreach (var err in login.Errors)
            {
                if (!string.IsNullOrWhiteSpace(err.Selector) &&
                    CardigannDefinitionCatalog.QuerySelectorWithContains(doc, err.Selector, strictValidation: false, "login.error") is not null)
                {
                    return true;
                }
            }

            var rowsSelector = pathSpec.RowsOverride?.Selector ?? def.Search?.Rows.Selector ?? def.RowsSelector;
            var hasRows = CardigannDefinitionCatalog.QuerySelectorAllWithContains(doc, rowsSelector, strictValidation: false, "rows").Count > 0;
            if (!hasRows)
            {
                if (!string.IsNullOrWhiteSpace(login.Test?.Selector) &&
                    CardigannDefinitionCatalog.QuerySelectorWithContains(doc, login.Test.Selector, strictValidation: false, "login.test") is null)
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(login.FormSelector) &&
                    CardigannDefinitionCatalog.QuerySelectorWithContains(doc, login.FormSelector, strictValidation: false, "login.form") is not null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static List<string> MapDesiredCategoriesToTrackerCategories(
        CardigannFullDefinition def,
        IReadOnlyList<int>? desiredCategories)
    {
        var mappings = def.CategoryMappings;
        if (mappings is null || mappings.Count == 0)
        {
            if (def.TrackerCategoryToNewznab.Count == 0)
            {
                return [];
            }

            mappings = def.TrackerCategoryToNewznab
                .Select(kv => new CardigannCategoryMapping(kv.Key, kv.Value))
                .ToList();
        }

        if (desiredCategories is null || desiredCategories.Count == 0)
        {
            return mappings
                .Select(m => m.TrackerId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var desiredSet = desiredCategories.ToHashSet();
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in mappings)
        {
            var directMatch = desiredSet.Contains(m.NewznabCategory);
            var parentGroup = (m.NewznabCategory / 1000) * 1000;
            var groupMatch = !directMatch &&
                             parentGroup > 0 &&
                             desiredSet.Contains(parentGroup) &&
                             !desiredSet.Any(c => c > parentGroup && c < parentGroup + 1000);

            if ((directMatch || groupMatch) && seen.Add(m.TrackerId))
            {
                result.Add(m.TrackerId);
            }
        }

        return result;
    }

    internal static List<(CardigannSearchPathSpec Path, IReadOnlyList<string> TrackerCategories)> ResolveMatchingSearchPaths(
        CardigannFullDefinition def,
        IReadOnlyList<int>? desiredCategories,
        IReadOnlyList<string> matchedTrackerCats)
    {
        var allPaths = def.Search?.Paths is { Count: > 0 } p
            ? p
            : [new CardigannSearchPathSpec(def.SearchPathTemplate, ResponseType: def.ResponseFormat)];

        var hasCategoryFilter = desiredCategories is { Count: > 0 } &&
                                (def.CategoryMappings?.Count > 0 || def.TrackerCategoryToNewznab.Count > 0);

        var resolved = new List<(CardigannSearchPathSpec, IReadOnlyList<string>)>();

        foreach (var pathSpec in allPaths)
        {
            if (pathSpec.Categories is null || pathSpec.Categories.Count == 0)
            {
                resolved.Add((pathSpec, matchedTrackerCats));
                continue;
            }

            var exclusions = pathSpec.Categories
                .Where(c => c.StartsWith('!'))
                .Select(c => c[1..].Trim())
                .Where(c => c.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var inclusions = pathSpec.Categories
                .Where(c => !c.StartsWith('!'))
                .ToList();

            if (inclusions.Count > 0)
            {
                if (hasCategoryFilter)
                {
                    var intersection = inclusions
                        .Where(inc => matchedTrackerCats.Contains(inc, StringComparer.OrdinalIgnoreCase) && !exclusions.Contains(inc))
                        .ToList();
                    if (intersection.Count == 0)
                    {
                        continue;
                    }
                    resolved.Add((pathSpec, intersection));
                }
                else
                {
                    var allowed = inclusions.Where(inc => !exclusions.Contains(inc)).ToList();
                    resolved.Add((pathSpec, allowed));
                }
            }
            else if (exclusions.Count > 0)
            {
                var remaining = matchedTrackerCats.Where(c => !exclusions.Contains(c)).ToList();
                if (hasCategoryFilter && remaining.Count == 0)
                {
                    continue;
                }
                resolved.Add((pathSpec, remaining));
            }
        }

        return resolved;
    }

    private static List<KeyValuePair<string, string>> CombineSearchInputs(
        IReadOnlyList<KeyValuePair<string, string>>? globalInputs,
        CardigannSearchPathSpec pathSpec)
    {
        var combined = new List<KeyValuePair<string, string>>();
        if (pathSpec.InheritInputs && globalInputs is { Count: > 0 })
        {
            combined.AddRange(globalInputs);
        }

        if (pathSpec.Inputs is { Count: > 0 })
        {
            foreach (var kv in pathSpec.Inputs)
            {
                var isMultiKey = kv.Key.Equals("$raw", StringComparison.OrdinalIgnoreCase) ||
                                 kv.Key.EndsWith("[]", StringComparison.Ordinal);
                if (!isMultiKey)
                {
                    combined.RemoveAll(existing => string.Equals(existing.Key, kv.Key, StringComparison.Ordinal));
                }
                combined.Add(kv);
            }
        }

        return combined;
    }

    private static (List<KeyValuePair<string, string>> Pairs, List<string> RawSegments) EvaluateInputPairs(
        IEnumerable<KeyValuePair<string, string>> inputs,
        CardigannTemplateContext ctx)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        var rawSegments = new List<string>();

        foreach (var (rawKey, rawExpr) in inputs)
        {
            var key = CardigannTemplateEvaluator.Evaluate(rawKey, ctx);
            var val = CardigannTemplateEvaluator.Evaluate(rawExpr, ctx);

            if (string.Equals(key, "$raw", StringComparison.OrdinalIgnoreCase))
            {
                var trimmedRaw = val.Trim().Trim('&');
                if (!string.IsNullOrEmpty(trimmedRaw))
                {
                    rawSegments.Add(trimmedRaw);
                }
                continue;
            }

            pairs.Add(new KeyValuePair<string, string>(key, val));
        }

        return (pairs, rawSegments);
    }

    private static Dictionary<string, string> EvaluateHeaders(
        IReadOnlyDictionary<string, string> headers,
        CardigannTemplateContext ctx)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in headers)
        {
            result[k] = CardigannTemplateEvaluator.Evaluate(v, ctx);
        }
        return result;
    }

    private static Dictionary<string, string> ExtractDefaultFormInputs(IElement formEl)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var input in formEl.QuerySelectorAll("input"))
        {
            if (input.HasAttribute("disabled"))
            {
                continue;
            }

            var name = input.GetAttribute("name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var type = (input.GetAttribute("type") ?? "text").Trim().ToLowerInvariant();
            if (type is "submit" or "button" or "image" or "reset" or "file")
            {
                continue;
            }

            if (type is "checkbox" or "radio" && !input.HasAttribute("checked"))
            {
                continue;
            }

            var val = input.GetAttribute("value") ?? (type is "checkbox" or "radio" ? "on" : "");
            fields[name] = val;
        }

        foreach (var select in formEl.QuerySelectorAll("select"))
        {
            if (select.HasAttribute("disabled"))
            {
                continue;
            }

            var name = select.GetAttribute("name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var selectedOpt = select.QuerySelector("option[selected]") ?? select.QuerySelector("option");
            if (selectedOpt is not null)
            {
                fields[name] = selectedOpt.GetAttribute("value") ?? selectedOpt.TextContent.Trim();
            }
        }

        return fields;
    }

    private static string ResolveFormInputFieldName(
        IElement formEl,
        Dictionary<string, string> existingFields,
        string inputKeyOrSelector)
    {
        if (existingFields.ContainsKey(inputKeyOrSelector))
        {
            return inputKeyOrSelector;
        }

        try
        {
            var matchedInput = formEl.QuerySelector(inputKeyOrSelector);
            var attrName = matchedInput?.GetAttribute("name");
            if (!string.IsNullOrWhiteSpace(attrName))
            {
                return attrName;
            }
        }
        catch
        {
            // Not a CSS selector; treat inputKeyOrSelector literally as the field name
        }

        return inputKeyOrSelector;
    }

    private static void CheckErrorSelectors(
        IDocument doc,
        Uri currentUri,
        IReadOnlyList<CardigannLoginErrorSpec> errors,
        CardigannTemplateContext ctx,
        string trackerName,
        bool isLogin)
    {
        foreach (var err in errors)
        {
            if (!string.IsNullOrWhiteSpace(err.Path) &&
                !currentUri.PathAndQuery.Contains(err.Path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(err.Selector))
            {
                var matched = CardigannDefinitionCatalog.QuerySelectorWithContains(
                    doc,
                    err.Selector,
                    strictValidation: true,
                    isLogin ? "login.error.selector" : "search.error.selector");

                if (matched is null)
                {
                    continue;
                }

                string? detail = null;
                if (err.Message is not null)
                {
                    detail = CardigannDefinitionCatalog.ExtractHtmlFieldValue(
                        doc.DocumentElement,
                        err.Message,
                        ctx,
                        strictValidation: true,
                        "error.message");
                }

                detail = string.IsNullOrWhiteSpace(detail) ? matched.TextContent?.Trim() : detail.Trim();
                var cleanDetail = string.IsNullOrWhiteSpace(detail)
                    ? $"matched error selector '{err.Selector}'"
                    : detail;

                if (isLogin)
                {
                    throw new CardigannLoginException($"Login failed on '{trackerName}': {cleanDetail}");
                }

                throw new CardigannEngineException($"Tracker '{trackerName}' returned an error: {cleanDetail}");
            }
        }
    }

    internal static void ParseRawCookieString(string rawCookieHeader, Dictionary<string, string> target)
    {
        if (string.IsNullOrWhiteSpace(rawCookieHeader))
        {
            return;
        }

        foreach (var part in rawCookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var name = part[..eq].Trim();
            var val = part[(eq + 1)..].Trim();
            if (!string.IsNullOrEmpty(name) && !CookieAttributeNames.Contains(name))
            {
                target[name] = val;
            }
        }
    }

    internal static void MergeSetCookies(IEnumerable<string> setCookieHeaders, Dictionary<string, string> target)
    {
        foreach (var header in setCookieHeaders)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                continue;
            }

            var segments = header.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            var firstEq = segments[0].IndexOf('=');
            if (firstEq <= 0)
            {
                continue;
            }

            var cookieName = segments[0][..firstEq].Trim();
            var cookieVal = segments[0][(firstEq + 1)..].Trim();
            var isExpired = string.IsNullOrEmpty(cookieVal) ||
                            string.Equals(cookieVal, "deleted", StringComparison.OrdinalIgnoreCase);

            for (var i = 1; i < segments.Length; i++)
            {
                var attr = segments[i].Trim();
                if (attr.StartsWith("Max-Age=0", StringComparison.OrdinalIgnoreCase) ||
                    attr.StartsWith("Max-Age=-", StringComparison.OrdinalIgnoreCase))
                {
                    isExpired = true;
                }
            }

            if (isExpired)
            {
                target.Remove(cookieName);
            }
            else if (!string.IsNullOrEmpty(cookieName))
            {
                target[cookieName] = cookieVal;
            }
        }
    }

    internal static string? FormatCookieHeader(Dictionary<string, string> cookies) =>
        cookies.Count == 0
            ? null
            : string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}"));

    private static Uri NormalizeBaseUri(string baseUrl, CardigannFullDefinition def)
    {
        var candidate = string.IsNullOrWhiteSpace(baseUrl)
            ? (def.Summary.Links.FirstOrDefault() ?? "https://localhost/")
            : baseUrl.Trim();
        if (!candidate.EndsWith('/'))
        {
            candidate += "/";
        }
        return new Uri(candidate);
    }

    private static Uri ResolveUri(Uri baseUri, string relativeOrAbsolute)
    {
        var trimmed = relativeOrAbsolute.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(trimmed);
        }

        return new Uri(baseUri, trimmed.TrimStart('/'));
    }

    private static Uri AppendQueryParameters(
        Uri baseTarget,
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        IReadOnlyList<string>? rawSegments)
    {
        if (pairs.Count == 0 && (rawSegments is null || rawSegments.Count == 0))
        {
            return baseTarget;
        }

        var ub = new UriBuilder(baseTarget);
        var existingQuery = ub.Query.TrimStart('?');
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(existingQuery))
        {
            parts.Add(existingQuery);
        }

        foreach (var (k, v) in pairs)
        {
            parts.Add($"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}");
        }

        if (rawSegments is not null)
        {
            foreach (var raw in rawSegments)
            {
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    parts.Add(raw.Trim('&'));
                }
            }
        }

        ub.Query = string.Join("&", parts);
        return ub.Uri;
    }

    private static string ComputeConfigFingerprint(
        string defId,
        string baseUrl,
        string? apiKey,
        IReadOnlyDictionary<string, string>? extraSettings)
    {
        var sb = new StringBuilder();
        sb.Append(defId).Append('|').Append(baseUrl).Append('|').Append(apiKey ?? "");
        if (extraSettings is not null)
        {
            foreach (var (k, v) in extraSettings.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append('|').Append(k.ToLowerInvariant()).Append('=').Append(v);
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }
}
