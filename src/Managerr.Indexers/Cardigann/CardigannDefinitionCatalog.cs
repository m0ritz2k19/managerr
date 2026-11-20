using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Managerr.Common.Paths;
using Managerr.Indexers.Categories;
using YamlDotNet.RepresentationModel;

namespace Managerr.Indexers.Cardigann;

public sealed record CardigannSettingField(
    string Name,
    string Type,
    string Label,
    string? DefaultValue);

public sealed record CardigannFieldSelector(
    string Selector,
    string? Attribute,
    bool Optional);

public sealed record CardigannDefinitionSummary(
    string Id,
    string SchemaVersion,
    string Name,
    string Description,
    string Language,
    string TrackerType,
    string Encoding,
    double RequestDelaySeconds,
    bool IsCustomOverride,
    IReadOnlyList<string> Links,
    IReadOnlyList<int> NewznabCategories,
    IReadOnlyList<string> SupportedVerticals,
    IReadOnlyList<CardigannSettingField> Settings);

public sealed record CardigannFullDefinition(
    CardigannDefinitionSummary Summary,
    string SearchPathTemplate,
    string ResponseFormat,
    string RowsSelector,
    IReadOnlyDictionary<string, int> TrackerCategoryToNewznab,
    IReadOnlyDictionary<string, CardigannFieldSelector> Fields,
    CardigannLoginSpec? Login = null,
    CardigannSearchSpec? Search = null,
    IReadOnlyList<CardigannCategoryMapping>? CategoryMappings = null);

public interface ICardigannDefinitionCatalog
{
    IReadOnlyList<CardigannDefinitionSummary> ListDefinitions(string? search = null, string? trackerType = null, string? vertical = null);
    CardigannFullDefinition? GetDefinition(string definitionId);
    void ReloadDefinitions();
    IReadOnlyList<CardigannExtractedRow> ExtractReleasesFromPayload(CardigannFullDefinition def, string rawPayload, Uri baseUri);

    IReadOnlyList<CardigannExtractedRow> ExtractReleasesFromPayload(
        CardigannFullDefinition def,
        string rawPayload,
        Uri baseUri,
        CardigannSearchPathSpec? pathSpec,
        CardigannTemplateContext? templateContext,
        bool strictValidation = false) =>
        ExtractReleasesFromPayload(def, rawPayload, baseUri);
}

public sealed record CardigannExtractedRow(
    string Title,
    string DownloadUrl,
    string? InfoUrl,
    string? MagnetUrl,
    string? InfoHash,
    long SizeBytes,
    int? Seeders,
    int? Leechers,
    DateTimeOffset PublishedAt,
    IReadOnlyList<int> Categories,
    double DownloadVolumeFactor = 1.0,
    double UploadVolumeFactor = 1.0,
    string? Guid = null,
    int? Peers = null,
    string? ImdbId = null,
    int? TmdbId = null,
    int? TvdbId = null);

/// <summary>
/// The Cardigann YAML definitions the user provides. Managerr ships none: tracker definitions name specific sites,
/// and only generic Newznab/Torznab feeds and the Cardigann engine are built in. Definitions are read from
/// /config/Definitions/Custom and, optionally, one more directory (Managerr:CardigannDefinitionsDirectory or
/// MANAGERR_DEFINITIONS_DIR), which may use Prowlarr's v1..vN layout.
/// </summary>
public sealed partial class CardigannDefinitionCatalog : ICardigannDefinitionCatalog
{
    private readonly IAppPaths _appPaths;
    private readonly string? _extraDefinitionsDirectory;
    private readonly Lock _sync = new();
    private Dictionary<string, CardigannFullDefinition>? _cache;

    [GeneratedRegex(@"^(?<num>\d+(?:\.\d+)?)\s*(?<unit>B|KB|KiB|MB|MiB|GB|GiB|TB|TiB)?$", RegexOptions.IgnoreCase)]
    private static partial Regex HumanSizeRegex();

    [GeneratedRegex(@":contains\(\s*(?:""(?<dq>(?:\\.|[^""])*)""|'(?<sq>(?:\\.|[^'])*)'|(?<raw>[^)]+))\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex ContainsPseudoRegex();

    [GeneratedRegex(@"^(?<prop>[^\[]+)(?:\[(?<idx>\d+)\])?$")]
    private static partial Regex JsonSegmentRegex();

    [GeneratedRegex(@"^(?<tag>[a-zA-Z0-9_:\-*]+)(?:\[\s*(?<attr>[a-zA-Z0-9_:\-]+)\s*=\s*(?:""(?<val>[^""]*)""|'(?<val2>[^']*)'|(?<val3>[^\]]*))\s*\])?$")]
    private static partial Regex XmlSelectorTokenRegex();

    public CardigannDefinitionCatalog(IAppPaths appPaths, string? extraDefinitionsDirectory = null)
    {
        _appPaths = appPaths;
        _extraDefinitionsDirectory = extraDefinitionsDirectory;
    }

    public void ReloadDefinitions()
    {
        lock (_sync)
        {
            _cache = null;
        }
    }

    public IReadOnlyList<CardigannDefinitionSummary> ListDefinitions(
        string? search = null,
        string? trackerType = null,
        string? vertical = null)
    {
        var map = EnsureLoaded();
        IEnumerable<CardigannDefinitionSummary> query = map.Values.Select(d => d.Summary);

        if (!string.IsNullOrWhiteSpace(trackerType))
        {
            query = query.Where(d => string.Equals(d.TrackerType, trackerType.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(vertical))
        {
            var v = vertical.Trim().ToLowerInvariant();
            query = query.Where(d => d.SupportedVerticals.Contains(v));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim();
            query = query.Where(d =>
                d.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                d.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                d.Description.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        return query.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public CardigannFullDefinition? GetDefinition(string definitionId)
    {
        if (string.IsNullOrWhiteSpace(definitionId))
        {
            return null;
        }
        var map = EnsureLoaded();
        return map.GetValueOrDefault(definitionId.Trim());
    }

    public IReadOnlyList<CardigannExtractedRow> ExtractReleasesFromPayload(
        CardigannFullDefinition def,
        string rawPayload,
        Uri baseUri) =>
        ExtractReleasesFromPayload(def, rawPayload, baseUri, pathSpec: null, templateContext: null, strictValidation: false);

    public IReadOnlyList<CardigannExtractedRow> ExtractReleasesFromPayload(
        CardigannFullDefinition def,
        string rawPayload,
        Uri baseUri,
        CardigannSearchPathSpec? pathSpec,
        CardigannTemplateContext? templateContext,
        bool strictValidation = false)
    {
        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return [];
        }

        var ctx = templateContext ?? CardigannTemplateContext.Create(def, baseUri.ToString(), apiKey: null, extraSettings: null);
        var responseType = (pathSpec?.ResponseType ?? def.ResponseFormat ?? "html").Trim().ToLowerInvariant();
        var rowsSpec = pathSpec?.RowsOverride ?? def.Search?.Rows ?? new CardigannRowsSpec(def.RowsSelector);
        var fieldsList = pathSpec?.FieldsOverride ?? def.Search?.Fields ?? BuildLegacyFieldsList(def.Fields);

        if (pathSpec?.NoResultsMessage is { Length: > 0 } noResMsg &&
            rawPayload.Contains(noResMsg, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var trimmedPayload = rawPayload.TrimStart();
        if (responseType == "json" ||
            (responseType != "xml" && (trimmedPayload.StartsWith('{') || trimmedPayload.StartsWith('['))))
        {
            return ExtractJsonRows(def, rowsSpec, fieldsList, rawPayload, baseUri, ctx);
        }

        if (responseType is "xml" or "rss" ||
            trimmedPayload.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
            trimmedPayload.StartsWith("<rss", StringComparison.OrdinalIgnoreCase) ||
            trimmedPayload.StartsWith("<feed", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractXmlRows(def, rowsSpec, fieldsList, rawPayload, baseUri, ctx, strictValidation);
        }

        return ExtractHtmlRows(def, rowsSpec, fieldsList, rawPayload, baseUri, ctx, strictValidation);
    }

    private Dictionary<string, CardigannFullDefinition> EnsureLoaded()
    {
        lock (_sync)
        {
            if (_cache is not null)
            {
                return _cache;
            }

            var loaded = new Dictionary<string, CardigannFullDefinition>(StringComparer.OrdinalIgnoreCase);
            var extraRoot = ResolveExtraDefinitionsDirectory();
            if (extraRoot is not null && Directory.Exists(extraRoot))
            {
                // Order v1 .. v12 ascending so newer schema versions naturally win on duplicate IDs
                var versionDirs = Directory.EnumerateDirectories(extraRoot, "v*")
                    .OrderBy(p => int.TryParse(Path.GetFileName(p).TrimStart('v', 'V'), out var n) ? n : 0)
                    .ToList();

                foreach (var vDir in versionDirs)
                {
                    var schemaVersion = Path.GetFileName(vDir);
                    foreach (var ymlFile in Directory.EnumerateFiles(vDir, "*.yml"))
                    {
                        var parsed = TryParseYamlFile(ymlFile, schemaVersion, isCustom: false);
                        if (parsed is not null)
                        {
                            loaded[parsed.Summary.Id] = parsed;
                        }
                    }
                }

                // Also load flat top-level *.yml definitions
                foreach (var ymlFile in Directory.EnumerateFiles(extraRoot, "*.yml"))
                {
                    var parsed = TryParseYamlFile(ymlFile, "latest", isCustom: false);
                    if (parsed is not null)
                    {
                        loaded[parsed.Summary.Id] = parsed;
                    }
                }
            }

            // Overlay /config/Definitions/Custom/*.yml overrides
            if (Directory.Exists(_appPaths.CustomDefinitionsDirectory))
            {
                foreach (var customYml in Directory.EnumerateFiles(_appPaths.CustomDefinitionsDirectory, "*.yml"))
                {
                    var parsed = TryParseYamlFile(customYml, "custom", isCustom: true);
                    if (parsed is not null)
                    {
                        loaded[parsed.Summary.Id] = parsed;
                    }
                }
            }

            _cache = loaded;
            return _cache;
        }
    }

    private string? ResolveExtraDefinitionsDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_extraDefinitionsDirectory) && Directory.Exists(_extraDefinitionsDirectory))
        {
            return _extraDefinitionsDirectory;
        }

        var env = Environment.GetEnvironmentVariable("MANAGERR_DEFINITIONS_DIR");
        return !string.IsNullOrWhiteSpace(env) && Directory.Exists(env) ? env : null;
    }

    public static CardigannFullDefinition? TryParseYamlFile(string filePath, string schemaVersion = "custom", bool isCustom = true)
    {
        try
        {
            using var reader = new StreamReader(filePath);
            return ParseYamlReader(reader, Path.GetFileNameWithoutExtension(filePath), schemaVersion, isCustom);
        }
        catch
        {
            return null;
        }
    }

    public static CardigannFullDefinition? TryParseYamlText(string yamlText, string fallbackId = "custom", string schemaVersion = "custom", bool isCustom = true)
    {
        try
        {
            using var reader = new StringReader(yamlText);
            return ParseYamlReader(reader, fallbackId, schemaVersion, isCustom);
        }
        catch
        {
            return null;
        }
    }

    private static CardigannFullDefinition? ParseYamlReader(TextReader reader, string fallbackId, string schemaVersion, bool isCustom)
    {
        var yaml = new YamlStream();
        yaml.Load(reader);
        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            return null;
        }

        var id = GetScalar(root, "id") ?? fallbackId;
        var name = GetScalar(root, "name") ?? id;
        var description = GetScalar(root, "description") ?? "";
        var language = GetScalar(root, "language") ?? "en-US";
        var trackerType = GetScalar(root, "type") ?? "public";
        var encoding = GetScalar(root, "encoding") ?? "UTF-8";
        var delay = double.TryParse(GetScalar(root, "requestDelay"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 1.0;

        var links = new List<string>();
        if (TryGetNode(root, "links", out var linksNode) && linksNode is YamlSequenceNode linkSeq)
        {
            foreach (var item in linkSeq.Children.OfType<YamlScalarNode>())
            {
                if (!string.IsNullOrWhiteSpace(item.Value))
                {
                    links.Add(item.Value.Trim());
                }
            }
        }

        var catMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var categoryMappings = new List<CardigannCategoryMapping>();
        var newznabCats = new HashSet<int>();

        if (TryGetNode(root, "caps", out var capsNode) && capsNode is YamlMappingNode capsMap)
        {
            if (TryGetNode(capsMap, "categorymappings", out var cmNode) && cmNode is YamlSequenceNode cmSeq)
            {
                foreach (var entry in cmSeq.Children.OfType<YamlMappingNode>())
                {
                    var trackerCatId = GetScalar(entry, "id");
                    var catStr = GetScalar(entry, "cat");
                    var descStr = GetScalar(entry, "desc");
                    var isDef = ParseBool(GetScalar(entry, "default"), false);
                    var mapped = NewznabCategoryMapper.MapCardigannCategoryNameToId(catStr);
                    newznabCats.Add(mapped);
                    if (!string.IsNullOrWhiteSpace(trackerCatId))
                    {
                        catMap[trackerCatId.Trim()] = mapped;
                        categoryMappings.Add(new CardigannCategoryMapping(trackerCatId.Trim(), mapped, descStr, isDef));
                    }
                    if (!string.IsNullOrWhiteSpace(descStr) && !catMap.ContainsKey(descStr.Trim()))
                    {
                        catMap[descStr.Trim()] = mapped;
                    }
                }
            }
            else if (TryGetNode(capsMap, "categories", out var directCats) && directCats is YamlMappingNode dcMap)
            {
                foreach (var kv in dcMap.Children)
                {
                    var k = (kv.Key as YamlScalarNode)?.Value;
                    var v = (kv.Value as YamlScalarNode)?.Value;
                    var mapped = NewznabCategoryMapper.MapCardigannCategoryNameToId(v);
                    newznabCats.Add(mapped);
                    if (!string.IsNullOrWhiteSpace(k))
                    {
                        catMap[k.Trim()] = mapped;
                        categoryMappings.Add(new CardigannCategoryMapping(k.Trim(), mapped, v, false));
                    }
                }
            }
        }

        if (newznabCats.Count == 0)
        {
            newznabCats.Add(2000);
            newznabCats.Add(5000);
        }

        var supportedVerticals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in MediaVerticals.All)
        {
            var vCats = NewznabCategoryMapper.GetCategoriesForVertical(v);
            if (newznabCats.Overlaps(vCats))
            {
                supportedVerticals.Add(v);
            }
        }

        var settings = new List<CardigannSettingField>();
        if (TryGetNode(root, "settings", out var settingsNode) && settingsNode is YamlSequenceNode setSeq)
        {
            foreach (var sEntry in setSeq.Children.OfType<YamlMappingNode>())
            {
                var sName = GetScalar(sEntry, "name");
                var sType = GetScalar(sEntry, "type") ?? "text";
                if (string.IsNullOrWhiteSpace(sName) || sType.StartsWith("info", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                settings.Add(new CardigannSettingField(
                    Name: sName,
                    Type: sType,
                    Label: GetScalar(sEntry, "label") ?? sName,
                    DefaultValue: GetScalar(sEntry, "default")));
            }
        }

        CardigannLoginSpec? loginSpec = null;
        if (TryGetNode(root, "login", out var loginNode) && loginNode is YamlMappingNode loginMap)
        {
            loginSpec = ParseLoginSpec(loginMap);
        }

        var searchPath = "/";
        var responseFormat = "html";
        var rowsSelector = "table tbody tr";
        var legacyFields = new Dictionary<string, CardigannFieldSelector>(StringComparer.OrdinalIgnoreCase);
        CardigannSearchSpec? searchSpec = null;

        if (TryGetNode(root, "search", out var searchNode) && searchNode is YamlMappingNode searchMap)
        {
            searchSpec = ParseSearchSpec(searchMap);
            if (searchSpec.Paths.Count > 0)
            {
                searchPath = searchSpec.Paths[0].Path;
                responseFormat = searchSpec.Paths[0].ResponseType;
            }
            rowsSelector = searchSpec.Rows.Selector;

            foreach (var (fKey, fSpec) in searchSpec.Fields)
            {
                var sel = fSpec.Selector ?? fSpec.Text ?? "";
                legacyFields[fKey] = new CardigannFieldSelector(sel, fSpec.Attribute, fSpec.Optional);
            }
        }

        var summary = new CardigannDefinitionSummary(
            Id: id,
            SchemaVersion: schemaVersion,
            Name: name,
            Description: description,
            Language: language,
            TrackerType: trackerType,
            Encoding: encoding,
            RequestDelaySeconds: delay,
            IsCustomOverride: isCustom,
            Links: links,
            NewznabCategories: newznabCats.OrderBy(x => x).ToArray(),
            SupportedVerticals: supportedVerticals.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            Settings: settings);

        return new CardigannFullDefinition(
            Summary: summary,
            SearchPathTemplate: searchPath,
            ResponseFormat: responseFormat,
            RowsSelector: rowsSelector,
            TrackerCategoryToNewznab: catMap,
            Fields: legacyFields,
            Login: loginSpec,
            Search: searchSpec,
            CategoryMappings: categoryMappings);
    }

    private static CardigannLoginSpec ParseLoginSpec(YamlMappingNode loginMap)
    {
        var path = GetScalar(loginMap, "path");
        var submitPath = GetScalar(loginMap, "submitpath");
        var formSelector = GetScalar(loginMap, "form");
        var cookies = ParseScalarList(loginMap, "cookies");
        var inputs = ParseKeyValueList(loginMap, "inputs");
        var selectorInputs = ParseFieldDictionary(loginMap, "selectorinputs");
        var getSelectorInputs = ParseFieldDictionary(loginMap, "getselectorinputs");
        var headers = ParseHeadersMap(loginMap, "headers");
        var errors = ParseErrorList(loginMap, "error");

        CardigannLoginTestSpec? testSpec = null;
        if (TryGetNode(loginMap, "test", out var testNode) && testNode is YamlMappingNode testMap)
        {
            testSpec = new CardigannLoginTestSpec(
                Path: GetScalar(testMap, "path"),
                Selector: GetScalar(testMap, "selector"));
        }

        CardigannCaptchaSpec? captchaSpec = null;
        if (TryGetNode(loginMap, "captcha", out var capNode) && capNode is YamlMappingNode capMap)
        {
            captchaSpec = new CardigannCaptchaSpec(
                Type: GetScalar(capMap, "type") ?? "image",
                Selector: GetScalar(capMap, "selector"),
                Input: GetScalar(capMap, "input"));
        }

        var method = GetScalar(loginMap, "method");
        if (string.IsNullOrWhiteSpace(method))
        {
            method = captchaSpec is not null ? "captcha" : "post";
        }

        return new CardigannLoginSpec(
            Path: path,
            SubmitPath: submitPath,
            Method: method.Trim().ToLowerInvariant(),
            FormSelector: formSelector,
            Cookies: cookies,
            Inputs: inputs,
            SelectorInputs: selectorInputs,
            GetSelectorInputs: getSelectorInputs,
            Headers: headers,
            Errors: errors,
            Test: testSpec,
            Captcha: captchaSpec);
    }

    private static CardigannSearchSpec ParseSearchSpec(YamlMappingNode searchMap)
    {
        var headers = ParseHeadersMap(searchMap, "headers");
        var keywordsFilters = ParseFilterList(searchMap, "keywordsfilters");
        var inputs = ParseKeyValueList(searchMap, "inputs");
        var errors = ParseErrorList(searchMap, "error");

        var defaultResponseType = "html";
        string? defaultNoResultsMsg = null;
        if (TryGetNode(searchMap, "response", out var searchRespNode) && searchRespNode is YamlMappingNode searchRespMap)
        {
            defaultResponseType = GetScalar(searchRespMap, "type") ?? "html";
            defaultNoResultsMsg = GetScalar(searchRespMap, "noResultsMessage");
        }

        var rowsSpec = TryGetNode(searchMap, "rows", out var rowsNode) && rowsNode is YamlMappingNode rowsMap
            ? ParseRowsSpec(rowsMap)
            : new CardigannRowsSpec();

        var fieldsList = TryGetNode(searchMap, "fields", out var fieldsNode) && fieldsNode is YamlMappingNode fieldsMap
            ? ParseFieldsList(fieldsMap)
            : [];

        var paths = new List<CardigannSearchPathSpec>();
        if (TryGetNode(searchMap, "paths", out var pathsNode) && pathsNode is YamlSequenceNode pathsSeq)
        {
            foreach (var pMap in pathsSeq.Children.OfType<YamlMappingNode>())
            {
                var pPath = GetScalar(pMap, "path") ?? "/";
                var pMethod = (GetScalar(pMap, "method") ?? "get").Trim().ToLowerInvariant();
                var pCats = ParseScalarList(pMap, "categories");
                var inheritInputs = ParseBool(GetScalar(pMap, "inheritinputs"), true);
                var followRedirect = ParseBool(GetScalar(pMap, "followredirect"), true);
                var pInputs = ParseKeyValueList(pMap, "inputs");
                var pHeaders = ParseHeadersMap(pMap, "headers");

                var respType = defaultResponseType;
                var noResMsg = defaultNoResultsMsg;
                if (TryGetNode(pMap, "response", out var respNode) && respNode is YamlMappingNode respMap)
                {
                    respType = GetScalar(respMap, "type") ?? respType;
                    noResMsg = GetScalar(respMap, "noResultsMessage") ?? noResMsg;
                }

                CardigannRowsSpec? rowsOverride = null;
                if (TryGetNode(pMap, "rows", out var pRowsNode) && pRowsNode is YamlMappingNode pRowsMap)
                {
                    rowsOverride = ParseRowsSpec(pRowsMap);
                }

                IReadOnlyList<KeyValuePair<string, CardigannFieldSpec>>? fieldsOverride = null;
                if (TryGetNode(pMap, "fields", out var pFieldsNode) && pFieldsNode is YamlMappingNode pFieldsMap)
                {
                    fieldsOverride = ParseFieldsList(pFieldsMap);
                }

                paths.Add(new CardigannSearchPathSpec(
                    Path: pPath,
                    Method: pMethod,
                    Categories: pCats,
                    InheritInputs: inheritInputs,
                    FollowRedirect: followRedirect,
                    Inputs: pInputs,
                    Headers: pHeaders,
                    ResponseType: respType.Trim().ToLowerInvariant(),
                    NoResultsMessage: noResMsg,
                    RowsOverride: rowsOverride,
                    FieldsOverride: fieldsOverride));
            }
        }
        else if (TryGetNode(searchMap, "path", out var singlePathNode) && singlePathNode is YamlScalarNode singleScalar && !string.IsNullOrWhiteSpace(singleScalar.Value))
        {
            paths.Add(new CardigannSearchPathSpec(
                Path: singleScalar.Value,
                ResponseType: defaultResponseType));
        }

        return new CardigannSearchSpec(
            Headers: headers,
            KeywordsFilters: keywordsFilters,
            Inputs: inputs,
            Paths: paths,
            Rows: rowsSpec,
            Fields: fieldsList,
            Errors: errors);
    }

    private static CardigannRowsSpec ParseRowsSpec(YamlMappingNode rowsMap)
    {
        var selector = GetScalar(rowsMap, "selector") ?? "table tbody tr";
        var attr = GetScalar(rowsMap, "attribute");
        var after = int.TryParse(GetScalar(rowsMap, "after"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? Math.Max(0, a) : 0;
        var missingAttrNoRes = ParseBool(GetScalar(rowsMap, "missingAttributeEqualsNoResults"), false);

        CardigannFieldSpec? dateHeaders = null;
        if (TryGetNode(rowsMap, "dateheaders", out var dhNode) && dhNode is YamlMappingNode dhMap)
        {
            dateHeaders = ParseFieldSpec(dhMap);
        }

        CardigannFieldSpec? countSpec = null;
        if (TryGetNode(rowsMap, "count", out var cntNode) && cntNode is YamlMappingNode cntMap)
        {
            countSpec = ParseFieldSpec(cntMap);
        }

        var filters = ParseFilterList(rowsMap, "filters");
        return new CardigannRowsSpec(selector, attr, after, missingAttrNoRes, dateHeaders, countSpec, filters);
    }

    private static List<KeyValuePair<string, CardigannFieldSpec>> ParseFieldsList(YamlMappingNode fieldsMap)
    {
        var list = new List<KeyValuePair<string, CardigannFieldSpec>>();
        foreach (var kv in fieldsMap.Children)
        {
            var fKey = (kv.Key as YamlScalarNode)?.Value;
            if (string.IsNullOrWhiteSpace(fKey))
            {
                continue;
            }

            if (kv.Value is YamlMappingNode fMap)
            {
                list.Add(new KeyValuePair<string, CardigannFieldSpec>(fKey.Trim(), ParseFieldSpec(fMap)));
            }
            else if (kv.Value is YamlScalarNode fScalar)
            {
                list.Add(new KeyValuePair<string, CardigannFieldSpec>(
                    fKey.Trim(),
                    new CardigannFieldSpec(Text: fScalar.Value)));
            }
        }
        return list;
    }

    private static Dictionary<string, CardigannFieldSpec> ParseFieldDictionary(YamlMappingNode parentMap, string key)
    {
        var dict = new Dictionary<string, CardigannFieldSpec>(StringComparer.OrdinalIgnoreCase);
        if (!TryGetNode(parentMap, key, out var node) || node is not YamlMappingNode map)
        {
            return dict;
        }

        foreach (var (k, v) in ParseFieldsList(map))
        {
            dict[k] = v;
        }
        return dict;
    }

    private static CardigannFieldSpec ParseFieldSpec(YamlMappingNode fMap)
    {
        var selector = GetScalar(fMap, "selector");
        var attr = GetScalar(fMap, "attribute");
        var remove = GetScalar(fMap, "remove");
        var text = GetScalar(fMap, "text");
        var defaultVal = GetScalar(fMap, "default");
        var optional = ParseBool(GetScalar(fMap, "optional"), defaultVal is not null);
        var filters = ParseFilterList(fMap, "filters");

        List<KeyValuePair<string, string>>? caseMap = null;
        if (TryGetNode(fMap, "case", out var caseNode) && caseNode is YamlMappingNode cMap)
        {
            caseMap = [];
            foreach (var kv in cMap.Children)
            {
                var pattern = (kv.Key as YamlScalarNode)?.Value;
                var val = (kv.Value as YamlScalarNode)?.Value ?? "";
                if (!string.IsNullOrWhiteSpace(pattern))
                {
                    caseMap.Add(new KeyValuePair<string, string>(pattern, val));
                }
            }
        }

        return new CardigannFieldSpec(
            Selector: selector,
            Attribute: attr,
            Remove: remove,
            Text: text,
            DefaultValue: defaultVal,
            Optional: optional,
            CaseMap: caseMap,
            Filters: filters);
    }

    private static List<CardigannFilterSpec> ParseFilterList(YamlMappingNode parentMap, string key)
    {
        var filters = new List<CardigannFilterSpec>();
        if (!TryGetNode(parentMap, key, out var fNode) || fNode is not YamlSequenceNode fSeq)
        {
            return filters;
        }

        foreach (var item in fSeq.Children.OfType<YamlMappingNode>())
        {
            var fName = GetScalar(item, "name");
            if (string.IsNullOrWhiteSpace(fName))
            {
                continue;
            }

            var args = new List<string>();
            if (TryGetNode(item, "args", out var argsNode))
            {
                if (argsNode is YamlScalarNode argScalar && argScalar.Value is not null)
                {
                    args.Add(argScalar.Value);
                }
                else if (argsNode is YamlSequenceNode argSeq)
                {
                    foreach (var child in argSeq.Children.OfType<YamlScalarNode>())
                    {
                        args.Add(child.Value ?? "");
                    }
                }
            }

            filters.Add(new CardigannFilterSpec(fName.Trim(), args));
        }

        return filters;
    }

    private static List<CardigannLoginErrorSpec> ParseErrorList(YamlMappingNode parentMap, string key)
    {
        var errors = new List<CardigannLoginErrorSpec>();
        if (!TryGetNode(parentMap, key, out var errNode) || errNode is not YamlSequenceNode errSeq)
        {
            return errors;
        }

        foreach (var errMap in errSeq.Children.OfType<YamlMappingNode>())
        {
            var path = GetScalar(errMap, "path");
            var selector = GetScalar(errMap, "selector");
            CardigannFieldSpec? msgSpec = null;
            if (TryGetNode(errMap, "message", out var msgNode) && msgNode is YamlMappingNode msgMap)
            {
                msgSpec = ParseFieldSpec(msgMap);
            }
            errors.Add(new CardigannLoginErrorSpec(path, selector, msgSpec));
        }

        return errors;
    }

    private static List<string> ParseScalarList(YamlMappingNode parentMap, string key)
    {
        var list = new List<string>();
        if (!TryGetNode(parentMap, key, out var node))
        {
            return list;
        }

        if (node is YamlSequenceNode seq)
        {
            foreach (var item in seq.Children.OfType<YamlScalarNode>())
            {
                if (!string.IsNullOrWhiteSpace(item.Value))
                {
                    list.Add(item.Value.Trim());
                }
            }
        }
        else if (node is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value))
        {
            list.Add(scalar.Value.Trim());
        }

        return list;
    }

    private static List<KeyValuePair<string, string>> ParseKeyValueList(YamlMappingNode parentMap, string key)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (!TryGetNode(parentMap, key, out var node) || node is not YamlMappingNode map)
        {
            return list;
        }

        foreach (var kv in map.Children)
        {
            var k = (kv.Key as YamlScalarNode)?.Value;
            var v = (kv.Value as YamlScalarNode)?.Value ?? "";
            if (!string.IsNullOrWhiteSpace(k))
            {
                list.Add(new KeyValuePair<string, string>(k.Trim(), v));
            }
        }

        return list;
    }

    private static Dictionary<string, string> ParseHeadersMap(YamlMappingNode parentMap, string key)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!TryGetNode(parentMap, key, out var node) || node is not YamlMappingNode map)
        {
            return dict;
        }

        foreach (var kv in map.Children)
        {
            var hName = (kv.Key as YamlScalarNode)?.Value;
            if (string.IsNullOrWhiteSpace(hName))
            {
                continue;
            }

            if (kv.Value is YamlScalarNode hScalar && hScalar.Value is not null)
            {
                dict[hName.Trim()] = hScalar.Value;
            }
            else if (kv.Value is YamlSequenceNode hSeq)
            {
                var first = hSeq.Children.OfType<YamlScalarNode>().FirstOrDefault()?.Value;
                if (first is not null)
                {
                    dict[hName.Trim()] = first;
                }
            }
        }

        return dict;
    }

    private static IReadOnlyList<KeyValuePair<string, CardigannFieldSpec>> BuildLegacyFieldsList(
        IReadOnlyDictionary<string, CardigannFieldSelector> fields) =>
        fields.Select(kv => new KeyValuePair<string, CardigannFieldSpec>(
            kv.Key,
            new CardigannFieldSpec(
                Selector: kv.Value.Selector,
                Attribute: kv.Value.Attribute,
                Optional: kv.Value.Optional))).ToList();

    // -------------------------------------------------------------------------
    // HTML Row & Field Extraction
    // -------------------------------------------------------------------------
    private static IReadOnlyList<CardigannExtractedRow> ExtractHtmlRows(
        CardigannFullDefinition def,
        CardigannRowsSpec rowsSpec,
        IReadOnlyList<KeyValuePair<string, CardigannFieldSpec>> fields,
        string html,
        Uri baseUri,
        CardigannTemplateContext ctx,
        bool strictValidation)
    {
        var parser = new HtmlParser();
        using var doc = parser.ParseDocument(html);
        var rawRows = QuerySelectorAllWithContains(doc, rowsSpec.Selector, strictValidation, "rows.selector");
        if (rawRows.Count == 0)
        {
            return [];
        }

        var results = new List<CardigannExtractedRow>();
        DateTimeOffset? currentHeaderDate = null;
        var step = 1 + Math.Max(0, rowsSpec.After);

        for (var i = 0; i < rawRows.Count; i += step)
        {
            var primaryRow = rawRows[i];

            if (rowsSpec.DateHeaders is not null && !string.IsNullOrWhiteSpace(rowsSpec.DateHeaders.Selector))
            {
                if (MatchesOrContainsSelector(primaryRow, rowsSpec.DateHeaders.Selector, strictValidation, "rows.dateheaders"))
                {
                    var dhVal = ExtractHtmlFieldValue(primaryRow, rowsSpec.DateHeaders, ctx, strictValidation, "rows.dateheaders");
                    if (CardigannFilterPipeline.TryParseFlexibleDateTimeOffset(dhVal, out var parsedDh))
                    {
                        currentHeaderDate = parsedDh;
                    }
                }
                else
                {
                    var sib = primaryRow.PreviousElementSibling;
                    while (sib is not null)
                    {
                        if (MatchesOrContainsSelector(sib, rowsSpec.DateHeaders.Selector, strictValidation, "rows.dateheaders"))
                        {
                            var dhVal = ExtractHtmlFieldValue(sib, rowsSpec.DateHeaders, ctx, strictValidation, "rows.dateheaders");
                            if (CardigannFilterPipeline.TryParseFlexibleDateTimeOffset(dhVal, out var parsedDh))
                            {
                                currentHeaderDate = parsedDh;
                            }
                            break;
                        }
                        sib = sib.PreviousElementSibling;
                    }
                }
            }

            IElement workingRow = primaryRow;
            if (rowsSpec.After > 0 && i + 1 < rawRows.Count)
            {
                var clonedRow = (IElement)primaryRow.Clone(true);
                var maxMerge = Math.Min(rawRows.Count - 1, i + rowsSpec.After);
                for (var m = i + 1; m <= maxMerge; m++)
                {
                    var nextCloned = (IElement)rawRows[m].Clone(true);
                    while (nextCloned.FirstChild is not null)
                    {
                        clonedRow.AppendChild(nextCloned.FirstChild);
                    }
                }
                workingRow = clonedRow;
            }

            ctx.Result.Clear();
            foreach (var (fieldName, fieldSpec) in fields)
            {
                var extracted = ExtractHtmlFieldValue(workingRow, fieldSpec, ctx, strictValidation, fieldName);
                if (!string.IsNullOrEmpty(extracted) || !ctx.Result.ContainsKey(fieldName))
                {
                    ctx.Result[fieldName] = extracted;
                }
            }

            var built = BuildExtractedRowFromFieldMap(def, rowsSpec, ctx, baseUri, currentHeaderDate);
            if (built is not null)
            {
                results.Add(built);
            }
        }

        return results;
    }

    internal static string? ExtractHtmlFieldValue(
        IElement scopeRoot,
        CardigannFieldSpec spec,
        CardigannTemplateContext ctx,
        bool strictValidation,
        string fieldLabel)
    {
        IElement? targetEl = scopeRoot;
        if (!string.IsNullOrWhiteSpace(spec.Selector))
        {
            targetEl = QuerySelectorWithContains(scopeRoot, spec.Selector, strictValidation, fieldLabel);
            if (targetEl is null && MatchesOrContainsSelector(scopeRoot, spec.Selector, strictValidation, fieldLabel))
            {
                targetEl = scopeRoot;
            }
        }

        string? rawValue = null;

        if (spec.CaseMap is { Count: > 0 })
        {
            if (targetEl is not null)
            {
                foreach (var (pattern, caseOutput) in spec.CaseMap)
                {
                    if (pattern == "*")
                    {
                        rawValue = CardigannTemplateEvaluator.Evaluate(caseOutput, ctx);
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(spec.Attribute))
                    {
                        var attrVal = targetEl.GetAttribute(spec.Attribute)?.Trim();
                        if (string.Equals(attrVal, pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            rawValue = CardigannTemplateEvaluator.Evaluate(caseOutput, ctx);
                            break;
                        }
                    }

                    if (MatchesOrContainsSelector(targetEl, pattern, strictValidation, $"{fieldLabel}.case"))
                    {
                        rawValue = CardigannTemplateEvaluator.Evaluate(caseOutput, ctx);
                        break;
                    }
                }
            }
        }
        else if (spec.Text is not null && (string.IsNullOrWhiteSpace(spec.Selector) || targetEl is not null))
        {
            rawValue = CardigannTemplateEvaluator.Evaluate(spec.Text, ctx);
        }
        else if (targetEl is not null)
        {
            if (!string.IsNullOrWhiteSpace(spec.Attribute))
            {
                rawValue = targetEl.GetAttribute(spec.Attribute)?.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(spec.Remove))
            {
                var cloned = (IElement)targetEl.Clone(true);
                var toRemove = QuerySelectorAllWithContains(cloned, spec.Remove, strictValidation, $"{fieldLabel}.remove");
                foreach (var rem in toRemove)
                {
                    rem.Remove();
                }
                rawValue = cloned.TextContent?.Trim();
            }
            else
            {
                rawValue = targetEl.TextContent?.Trim();
            }
        }

        if (string.IsNullOrEmpty(rawValue) && spec.DefaultValue is not null)
        {
            rawValue = CardigannTemplateEvaluator.Evaluate(spec.DefaultValue, ctx);
        }

        if (rawValue is not null && spec.Filters is { Count: > 0 })
        {
            rawValue = CardigannFilterPipeline.ApplyFilters(rawValue, spec.Filters, ctx, ctx.Today);
            if (string.IsNullOrEmpty(rawValue) && spec.DefaultValue is not null)
            {
                rawValue = CardigannTemplateEvaluator.Evaluate(spec.DefaultValue, ctx);
            }
        }

        return rawValue;
    }

    // -------------------------------------------------------------------------
    // JSON Row & Field Extraction (with nested dot-paths & $ root array)
    // -------------------------------------------------------------------------
    private static IReadOnlyList<CardigannExtractedRow> ExtractJsonRows(
        CardigannFullDefinition def,
        CardigannRowsSpec rowsSpec,
        IReadOnlyList<KeyValuePair<string, CardigannFieldSpec>> fields,
        string json,
        Uri baseUri,
        CardigannTemplateContext ctx)
    {
        using var doc = JsonDocument.Parse(json);
        var selectorPath = rowsSpec.Selector;
        if (!string.IsNullOrWhiteSpace(rowsSpec.Attribute))
        {
            selectorPath = string.IsNullOrWhiteSpace(selectorPath) || selectorPath == "$"
                ? rowsSpec.Attribute
                : $"{selectorPath}.{rowsSpec.Attribute}";
        }

        var rowItems = SelectJsonElementsByPath(doc.RootElement, selectorPath);
        if (rowItems.Count == 0)
        {
            return [];
        }

        var results = new List<CardigannExtractedRow>();
        foreach (var item in rowItems)
        {
            ctx.Result.Clear();
            foreach (var (fieldName, fieldSpec) in fields)
            {
                var extracted = ExtractJsonFieldValue(item, fieldSpec, fieldName, ctx);
                if (!string.IsNullOrEmpty(extracted) || !ctx.Result.ContainsKey(fieldName))
                {
                    ctx.Result[fieldName] = extracted;
                }
            }

            var built = BuildExtractedRowFromFieldMap(def, rowsSpec, ctx, baseUri, headerDateFallback: null);
            if (built is not null)
            {
                results.Add(built);
            }
        }

        return results;
    }

    private static List<JsonElement> SelectJsonElementsByPath(JsonElement root, string? path)
    {
        var current = new List<JsonElement> { root };
        if (!string.IsNullOrWhiteSpace(path) && path.Trim() != "$" && path.Trim() != ".")
        {
            var normalized = path.Trim();
            if (normalized.StartsWith("$.", StringComparison.Ordinal))
            {
                normalized = normalized[2..];
            }

            var segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
            foreach (var seg in segments)
            {
                var next = new List<JsonElement>();
                var m = JsonSegmentRegex().Match(seg);
                var propName = m.Success ? m.Groups["prop"].Value : seg;
                var explicitIdx = m.Success && m.Groups["idx"].Success
                    ? int.Parse(m.Groups["idx"].Value, CultureInfo.InvariantCulture)
                    : (int?)null;

                foreach (var el in current)
                {
                    if (el.ValueKind == JsonValueKind.Object && TryGetJsonPropertyInsensitive(el, propName, out var propVal))
                    {
                        if (explicitIdx.HasValue)
                        {
                            if (propVal.ValueKind == JsonValueKind.Array &&
                                explicitIdx.Value >= 0 &&
                                explicitIdx.Value < propVal.GetArrayLength())
                            {
                                next.Add(propVal[explicitIdx.Value]);
                            }
                        }
                        else
                        {
                            next.Add(propVal);
                        }
                    }
                    else if (el.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var arrItem in el.EnumerateArray())
                        {
                            if (arrItem.ValueKind == JsonValueKind.Object &&
                                TryGetJsonPropertyInsensitive(arrItem, propName, out var childVal))
                            {
                                next.Add(childVal);
                            }
                        }
                    }
                }

                current = next;
                if (current.Count == 0)
                {
                    return [];
                }
            }
        }

        var flattened = new List<JsonElement>();
        foreach (var el in current)
        {
            if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    flattened.Add(item);
                }
            }
            else if (el.ValueKind == JsonValueKind.Object)
            {
                flattened.Add(el);
            }
        }

        return flattened;
    }

    private static string? ExtractJsonFieldValue(
        JsonElement item,
        CardigannFieldSpec spec,
        string fallbackFieldName,
        CardigannTemplateContext ctx)
    {
        string? rawValue = null;
        var selector = spec.Selector;
        if (string.IsNullOrWhiteSpace(selector) && spec.Text is null && spec.CaseMap is null)
        {
            selector = fallbackFieldName;
        }

        if (!string.IsNullOrWhiteSpace(selector))
        {
            rawValue = ResolveJsonScalarByPath(item, selector);
        }

        if (spec.CaseMap is { Count: > 0 })
        {
            string? matchedCase = null;
            foreach (var (pattern, caseOutput) in spec.CaseMap)
            {
                if (pattern == "*" ||
                    string.Equals(rawValue, pattern, StringComparison.OrdinalIgnoreCase) ||
                    (rawValue is null && TryGetJsonPropertyInsensitive(item, pattern, out var pEl) && IsJsonElementTruthy(pEl)))
                {
                    matchedCase = CardigannTemplateEvaluator.Evaluate(caseOutput, ctx);
                    break;
                }
            }
            rawValue = matchedCase;
        }
        else if (spec.Text is not null && (string.IsNullOrWhiteSpace(spec.Selector) || rawValue is not null))
        {
            rawValue = CardigannTemplateEvaluator.Evaluate(spec.Text, ctx);
        }

        if (string.IsNullOrEmpty(rawValue) && spec.DefaultValue is not null)
        {
            rawValue = CardigannTemplateEvaluator.Evaluate(spec.DefaultValue, ctx);
        }

        if (rawValue is not null && spec.Filters is { Count: > 0 })
        {
            rawValue = CardigannFilterPipeline.ApplyFilters(rawValue, spec.Filters, ctx, ctx.Today);
            if (string.IsNullOrEmpty(rawValue) && spec.DefaultValue is not null)
            {
                rawValue = CardigannTemplateEvaluator.Evaluate(spec.DefaultValue, ctx);
            }
        }

        return rawValue;
    }

    private static string? ResolveJsonScalarByPath(JsonElement item, string path)
    {
        var current = item;
        var normalized = path.Trim();
        if (normalized == "." || normalized == "$")
        {
            return JsonElementToScalarString(current);
        }
        if (normalized.StartsWith("$.", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        var segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            var m = JsonSegmentRegex().Match(seg);
            var propName = m.Success ? m.Groups["prop"].Value : seg;
            var explicitIdx = m.Success && m.Groups["idx"].Success
                ? int.Parse(m.Groups["idx"].Value, CultureInfo.InvariantCulture)
                : (int?)null;

            if (current.ValueKind != JsonValueKind.Object || !TryGetJsonPropertyInsensitive(current, propName, out var next))
            {
                return null;
            }

            if (explicitIdx.HasValue)
            {
                if (next.ValueKind != JsonValueKind.Array || explicitIdx.Value < 0 || explicitIdx.Value >= next.GetArrayLength())
                {
                    return null;
                }
                current = next[explicitIdx.Value];
            }
            else
            {
                current = next;
            }
        }

        return JsonElementToScalarString(current);
    }

    private static string? JsonElementToScalarString(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => el.GetArrayLength() > 0 ? JsonElementToScalarString(el[0]) : null,
            _ => null,
        };

    private static bool IsJsonElementTruthy(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => el.TryGetDouble(out var d) && Math.Abs(d) > 1e-9,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(el.GetString()) && el.GetString() != "0" &&
                                    !string.Equals(el.GetString(), "false", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Array => el.GetArrayLength() > 0,
            JsonValueKind.Object => true,
            _ => false,
        };

    private static bool TryGetJsonPropertyInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    // -------------------------------------------------------------------------
    // XML / RSS Row & Field Extraction
    // -------------------------------------------------------------------------
    private static IReadOnlyList<CardigannExtractedRow> ExtractXmlRows(
        CardigannFullDefinition def,
        CardigannRowsSpec rowsSpec,
        IReadOnlyList<KeyValuePair<string, CardigannFieldSpec>> fields,
        string xml,
        Uri baseUri,
        CardigannTemplateContext ctx,
        bool strictValidation)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch
        {
            return ExtractHtmlRows(def, rowsSpec, fields, xml, baseUri, ctx, strictValidation);
        }

        var rowElements = SelectXmlElements(doc.Root, rowsSpec.Selector);
        if (rowElements.Count == 0)
        {
            return [];
        }

        var results = new List<CardigannExtractedRow>();
        foreach (var item in rowElements)
        {
            ctx.Result.Clear();
            foreach (var (fieldName, fieldSpec) in fields)
            {
                var extracted = ExtractXmlFieldValue(item, fieldSpec, fieldName, ctx);
                if (!string.IsNullOrEmpty(extracted) || !ctx.Result.ContainsKey(fieldName))
                {
                    ctx.Result[fieldName] = extracted;
                }
            }

            var built = BuildExtractedRowFromFieldMap(def, rowsSpec, ctx, baseUri, headerDateFallback: null);
            if (built is not null)
            {
                results.Add(built);
            }
        }

        return results;
    }

    private static List<XElement> SelectXmlElements(XElement? root, string? selector)
    {
        if (root is null)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(selector))
        {
            return root.Descendants("item").ToList();
        }

        var tokens = selector
            .Replace(">", " ", StringComparison.Ordinal)
            .Replace("/", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var lastToken = tokens.LastOrDefault() ?? "item";
        var m = XmlSelectorTokenRegex().Match(lastToken);
        var tagName = m.Success ? m.Groups["tag"].Value : lastToken;
        var localTag = tagName.Contains(':', StringComparison.Ordinal)
            ? tagName[(tagName.IndexOf(':') + 1)..]
            : tagName;
        var attrName = m.Success && m.Groups["attr"].Success ? m.Groups["attr"].Value : null;
        var attrVal = m.Success
            ? (m.Groups["val"].Success ? m.Groups["val"].Value :
               m.Groups["val2"].Success ? m.Groups["val2"].Value :
               m.Groups["val3"].Success ? m.Groups["val3"].Value : null)
            : null;

        IEnumerable<XElement> candidates = string.Equals(root.Name.LocalName, localTag, StringComparison.OrdinalIgnoreCase)
            ? [root]
            : root.Descendants().Where(e => localTag == "*" || string.Equals(e.Name.LocalName, localTag, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(attrName))
        {
            candidates = candidates.Where(e =>
            {
                var attr = e.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, attrName, StringComparison.OrdinalIgnoreCase));
                return attr is not null && (attrVal is null || string.Equals(attr.Value, attrVal, StringComparison.OrdinalIgnoreCase));
            });
        }

        return candidates.ToList();
    }

    private static string? ExtractXmlFieldValue(
        XElement item,
        CardigannFieldSpec spec,
        string fallbackFieldName,
        CardigannTemplateContext ctx)
    {
        string? rawValue = null;
        var selector = spec.Selector;
        if (string.IsNullOrWhiteSpace(selector) && spec.Text is null && spec.CaseMap is null)
        {
            selector = fallbackFieldName;
        }

        if (!string.IsNullOrWhiteSpace(selector))
        {
            var matchedEl = SelectXmlElements(item, selector).FirstOrDefault();
            if (matchedEl is not null)
            {
                if (!string.IsNullOrWhiteSpace(spec.Attribute))
                {
                    rawValue = matchedEl.Attributes()
                        .FirstOrDefault(a => string.Equals(a.Name.LocalName, spec.Attribute, StringComparison.OrdinalIgnoreCase))
                        ?.Value.Trim();
                }
                else
                {
                    rawValue = matchedEl.Value.Trim();
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(spec.Attribute))
        {
            rawValue = item.Attributes()
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, spec.Attribute, StringComparison.OrdinalIgnoreCase))
                ?.Value.Trim();
        }

        if (spec.CaseMap is { Count: > 0 })
        {
            foreach (var (pattern, caseOutput) in spec.CaseMap)
            {
                if (pattern == "*" ||
                    string.Equals(rawValue, pattern, StringComparison.OrdinalIgnoreCase) ||
                    SelectXmlElements(item, pattern).Count > 0)
                {
                    rawValue = CardigannTemplateEvaluator.Evaluate(caseOutput, ctx);
                    break;
                }
            }
        }
        else if (spec.Text is not null && (string.IsNullOrWhiteSpace(spec.Selector) || rawValue is not null))
        {
            rawValue = CardigannTemplateEvaluator.Evaluate(spec.Text, ctx);
        }

        if (string.IsNullOrEmpty(rawValue) && spec.DefaultValue is not null)
        {
            rawValue = CardigannTemplateEvaluator.Evaluate(spec.DefaultValue, ctx);
        }

        if (rawValue is not null && spec.Filters is { Count: > 0 })
        {
            rawValue = CardigannFilterPipeline.ApplyFilters(rawValue, spec.Filters, ctx, ctx.Today);
            if (string.IsNullOrEmpty(rawValue) && spec.DefaultValue is not null)
            {
                rawValue = CardigannTemplateEvaluator.Evaluate(spec.DefaultValue, ctx);
            }
        }

        return rawValue;
    }

    // -------------------------------------------------------------------------
    // Shared Row Builder
    // -------------------------------------------------------------------------
    private static CardigannExtractedRow? BuildExtractedRowFromFieldMap(
        CardigannFullDefinition def,
        CardigannRowsSpec rowsSpec,
        CardigannTemplateContext ctx,
        Uri baseUri,
        DateTimeOffset? headerDateFallback)
    {
        string? Field(string key) =>
            ctx.Result.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val) ? val.Trim() : null;

        var title = Field("title");
        var downloadRaw = Field("download") ?? Field("magnet");
        var magnetRaw = Field("magnet");
        var infoHashRaw = NormalizeInfoHash(Field("infohash"));

        if (string.IsNullOrWhiteSpace(downloadRaw) && !string.IsNullOrWhiteSpace(infoHashRaw) && !string.IsNullOrWhiteSpace(title))
        {
            downloadRaw = $"magnet:?xt=urn:btih:{infoHashRaw}&dn={Uri.EscapeDataString(title)}";
            magnetRaw ??= downloadRaw;
        }

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(downloadRaw))
        {
            return null;
        }

        // Apply rows.filters (such as "andmatch")
        if (rowsSpec.Filters is { Count: > 0 })
        {
            foreach (var rf in rowsSpec.Filters)
            {
                if (string.Equals(rf.Name, "andmatch", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(ctx.Keywords))
                {
                    var tokens = ctx.Keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Any(t => t.Length > 2 && !title.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    {
                        return null;
                    }
                }
            }
        }

        var resolvedDownload = ResolveRelativeOrMagnet(baseUri, downloadRaw);
        var detailsRaw = Field("details") ?? Field("comments");
        var infoUrl = detailsRaw is not null ? ResolveRelativeOrMagnet(baseUri, detailsRaw) : null;
        var guid = Field("guid") ?? infoUrl ?? resolvedDownload;

        var magnet = resolvedDownload.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
            ? resolvedDownload
            : (magnetRaw is not null ? ResolveRelativeOrMagnet(baseUri, magnetRaw) : null);

        var infoHash = infoHashRaw
            ?? ExtractInfoHashFromMagnet(magnet)
            ?? ExtractInfoHashFromMagnet(resolvedDownload);

        var sizeStr = Field("size");
        var seedersStr = Field("seeders");
        var leechersStr = Field("leechers");
        var peersStr = Field("peers");
        var dateStr = Field("date") ?? Field("publishdate");
        var catKey = Field("category") ?? Field("categorydesc");
        var dlFactorStr = Field("downloadvolumefactor");
        var ulFactorStr = Field("uploadvolumefactor");
        var imdbStr = Field("imdb") ?? Field("imdbid");
        var tmdbStr = Field("tmdbid");
        var tvdbStr = Field("tvdbid");

        var cats = ResolveRowCategories(def, catKey);

        int? seeders = ParseOptionalInt(seedersStr);
        int? leechers = ParseOptionalInt(leechersStr);
        int? peers = ParseOptionalInt(peersStr) ?? (seeders.HasValue && leechers.HasValue ? seeders + leechers : seeders);

        var publishedAt = headerDateFallback ?? ctx.Today;
        if (!string.IsNullOrWhiteSpace(dateStr))
        {
            if (CardigannFilterPipeline.TryParseFlexibleDateTimeOffset(dateStr, out var parsedDt) ||
                CardigannFilterPipeline.TryParseFuzzyTime(dateStr, ctx.Today, out parsedDt))
            {
                publishedAt = parsedDt;
            }
        }

        var dlFactor = double.TryParse(dlFactorStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var df) ? df : 1.0;
        var ulFactor = double.TryParse(ulFactorStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var uf) ? uf : 1.0;
        var (imdbFull, _) = CardigannTemplateContext.NormalizeImdbIds(imdbStr);

        return new CardigannExtractedRow(
            Title: title,
            DownloadUrl: resolvedDownload,
            InfoUrl: infoUrl,
            MagnetUrl: magnet,
            InfoHash: infoHash,
            SizeBytes: ParseSizeBytes(sizeStr),
            Seeders: seeders,
            Leechers: leechers,
            PublishedAt: publishedAt,
            Categories: cats,
            DownloadVolumeFactor: dlFactor,
            UploadVolumeFactor: ulFactor,
            Guid: guid,
            Peers: peers,
            ImdbId: string.IsNullOrEmpty(imdbFull) ? null : imdbFull,
            TmdbId: ParseOptionalInt(tmdbStr),
            TvdbId: ParseOptionalInt(tvdbStr));
    }

    private static List<int> ResolveRowCategories(CardigannFullDefinition def, string? catKey)
    {
        var cats = new List<int>();
        if (!string.IsNullOrWhiteSpace(catKey))
        {
            var trimmed = catKey.Trim();
            if (def.TrackerCategoryToNewznab.TryGetValue(trimmed, out var mappedCat))
            {
                cats.Add(mappedCat);
                return cats;
            }

            if (def.CategoryMappings is { Count: > 0 })
            {
                var descMatch = def.CategoryMappings.FirstOrDefault(m =>
                    string.Equals(m.TrackerId, trimmed, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(m.Description, trimmed, StringComparison.OrdinalIgnoreCase));
                if (descMatch is not null)
                {
                    cats.Add(descMatch.NewznabCategory);
                    return cats;
                }
            }
        }

        if (def.CategoryMappings is { Count: > 0 })
        {
            var defaultMapping = def.CategoryMappings.FirstOrDefault(m => m.IsDefault);
            if (defaultMapping is not null)
            {
                cats.Add(defaultMapping.NewznabCategory);
                return cats;
            }
        }

        if (def.Summary.NewznabCategories.Count > 0)
        {
            cats.Add(def.Summary.NewznabCategories[0]);
        }

        return cats;
    }

    // -------------------------------------------------------------------------
    // AngleSharp CSS Selector Helpers (supporting :contains(...) + validation)
    // -------------------------------------------------------------------------
    internal static void ValidateCssSelector(string? selector, string contextLabel)
    {
        if (string.IsNullOrWhiteSpace(selector) || selector.Trim() == "*")
        {
            return;
        }

        var (cleanedCss, _) = StripContainsClauses(selector);
        try
        {
            var parser = new HtmlParser();
            using var doc = parser.ParseDocument("<html><body></body></html>");
            _ = doc.QuerySelectorAll(cleanedCss);
        }
        catch (DomException ex)
        {
            throw new CardigannConfigException($"Invalid CSS selector '{selector}' in '{contextLabel}': {ex.Message}", ex);
        }
    }

    internal static List<IElement> QuerySelectorAllWithContains(
        IParentNode root,
        string selector,
        bool strictValidation,
        string contextLabel)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return [];
        }

        var (cleanedCss, containsNeedles) = StripContainsClauses(selector);
        IHtmlCollection<IElement> matched;
        try
        {
            matched = root.QuerySelectorAll(cleanedCss);
        }
        catch (DomException ex)
        {
            if (strictValidation)
            {
                throw new CardigannConfigException($"Invalid CSS selector '{selector}' in '{contextLabel}': {ex.Message}", ex);
            }
            return [];
        }

        if (containsNeedles.Count == 0)
        {
            return matched.ToList();
        }

        return matched
            .Where(el => containsNeedles.All(needle =>
                (el.TextContent ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    internal static IElement? QuerySelectorWithContains(
        IParentNode root,
        string selector,
        bool strictValidation,
        string contextLabel) =>
        QuerySelectorAllWithContains(root, selector, strictValidation, contextLabel).FirstOrDefault();

    internal static bool MatchesOrContainsSelector(
        IElement element,
        string selector,
        bool strictValidation,
        string contextLabel)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return false;
        }

        if (selector.Trim() == "*")
        {
            return true;
        }

        var (cleanedCss, containsNeedles) = StripContainsClauses(selector);
        try
        {
            var selfMatches = element.Matches(cleanedCss) &&
                containsNeedles.All(n => (element.TextContent ?? "").Contains(n, StringComparison.OrdinalIgnoreCase));
            if (selfMatches)
            {
                return true;
            }

            return QuerySelectorAllWithContains(element, selector, strictValidation, contextLabel).Count > 0;
        }
        catch (DomException ex)
        {
            if (strictValidation)
            {
                throw new CardigannConfigException($"Invalid CSS selector '{selector}' in '{contextLabel}': {ex.Message}", ex);
            }
            return false;
        }
    }

    private static (string CleanedCss, List<string> Needles) StripContainsClauses(string rawSelector)
    {
        if (!rawSelector.Contains(":contains(", StringComparison.OrdinalIgnoreCase))
        {
            return (rawSelector, []);
        }

        var needles = new List<string>();
        var cleaned = ContainsPseudoRegex().Replace(rawSelector, m =>
        {
            var val = m.Groups["dq"].Success ? m.Groups["dq"].Value.Replace("\\\"", "\"", StringComparison.Ordinal) :
                      m.Groups["sq"].Success ? m.Groups["sq"].Value.Replace("\\'", "'", StringComparison.Ordinal) :
                      m.Groups["raw"].Value.Trim();
            needles.Add(val);
            return "";
        }).Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "*";
        }

        return (cleaned, needles);
    }

    private static string ResolveRelativeOrMagnet(Uri baseUri, string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }
        return new Uri(baseUri, trimmed).ToString();
    }

    private static string? NormalizeInfoHash(string? rawHash)
    {
        if (string.IsNullOrWhiteSpace(rawHash))
        {
            return null;
        }

        var h = rawHash.Trim();
        if (h.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractInfoHashFromMagnet(h);
        }
        if (h.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
        {
            h = h["urn:btih:".Length..];
        }
        var amp = h.IndexOf('&');
        if (amp >= 0)
        {
            h = h[..amp];
        }
        return string.IsNullOrWhiteSpace(h) ? null : h.Trim().ToLowerInvariant();
    }

    internal static string? ExtractInfoHashFromMagnet(string? magnet)
    {
        if (string.IsNullOrWhiteSpace(magnet))
        {
            return null;
        }
        var idx = magnet.IndexOf("xt=urn:btih:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }
        var rest = magnet[(idx + "xt=urn:btih:".Length)..];
        var amp = rest.IndexOf('&');
        return (amp >= 0 ? rest[..amp] : rest).Trim().ToLowerInvariant();
    }

    internal static long ParseSizeBytes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }
        if (long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var exact))
        {
            return exact;
        }
        var m = HumanSizeRegex().Match(raw.Trim().Replace(",", ""));
        if (!m.Success || !double.TryParse(m.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
        {
            return 0;
        }
        var mult = m.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "KB" or "KIB" => 1024d,
            "MB" or "MIB" => 1024d * 1024d,
            "GB" or "GIB" => 1024d * 1024d * 1024d,
            "TB" or "TIB" => 1024d * 1024d * 1024d * 1024d,
            _ => 1d,
        };
        return (long)(val * mult);
    }

    private static int? ParseOptionalInt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var cleaned = raw.Trim().Replace(",", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        return int.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static bool ParseBool(string? raw, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        return raw.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => defaultValue,
        };
    }

    private static bool TryGetNode(YamlMappingNode map, string key, out YamlNode node) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out node!);

    private static string? GetScalar(YamlMappingNode map, string key) =>
        TryGetNode(map, key, out var n) && n is YamlScalarNode s ? s.Value : null;
}
