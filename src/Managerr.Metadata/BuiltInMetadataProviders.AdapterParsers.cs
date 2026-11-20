using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml.Linq;
using Managerr.Datastore.Services;

namespace Managerr.Metadata;

internal sealed partial class UniversalBuiltInMetadataProvider
{
    private IReadOnlyList<MetadataWorkCandidate> ParseDnbSruXml(string rawXml, string vertical, string? fallbackId)
    {
        try
        {
            var doc = XDocument.Parse(rawXml);
            var root = doc.Root;
            if (root is null)
            {
                return [];
            }

            var recordElements = root.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, "record", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (recordElements.Count == 0 && string.Equals(root.Name.LocalName, "dc", StringComparison.OrdinalIgnoreCase))
            {
                recordElements.Add(root);
            }

            var results = new List<MetadataWorkCandidate>();
            foreach (var rec in recordElements)
            {
                var cand = ParseSingleDnbRecord(rec, vertical, fallbackId);
                if (cand is not null)
                {
                    results.Add(cand);
                }
            }

            return results;
        }
        catch
        {
            return [];
        }
    }

    private MetadataWorkCandidate? ParseSingleDnbRecord(XElement recordEl, string vertical, string? fallbackId)
    {
        var dcContainer = recordEl.Descendants()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "dc", StringComparison.OrdinalIgnoreCase))
            ?? recordEl.Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, "recordData", StringComparison.OrdinalIgnoreCase))
            ?? recordEl;

        var rawTitles = dcContainer.Elements()
            .Where(e => string.Equals(e.Name.LocalName, "title", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        if (rawTitles.Count == 0)
        {
            return null;
        }

        var firstTitleRaw = rawTitles[0];
        string? responsibilityCreator = null;
        if (firstTitleRaw.Contains(" / ", StringComparison.Ordinal))
        {
            var split = firstTitleRaw.Split(" / ", 2, StringSplitOptions.TrimEntries);
            firstTitleRaw = split[0];
            responsibilityCreator = split[1];
        }

        var rawCreators = dcContainer.Elements()
            .Where(e => string.Equals(e.Name.LocalName, "creator", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        var creator = rawCreators.FirstOrDefault() ?? responsibilityCreator;
        if (!string.IsNullOrWhiteSpace(creator))
        {
            creator = Regex.Replace(creator, @"\s*\[[^\]]+\]", "").Trim();
            if (LatinTitleNormalizer.NormalizeMusicBrainzSortName(creator) is { Length: > 0 } normCreator)
            {
                creator = normCreator;
            }
        }

        int? year = null;
        foreach (var dateEl in dcContainer.Elements().Where(e => string.Equals(e.Name.LocalName, "date", StringComparison.OrdinalIgnoreCase)))
        {
            var m = FourDigitYearRegex().Match(dateEl.Value);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var yr))
            {
                year = yr;
                break;
            }
        }

        string? idn = recordEl.Descendants()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "recordIdentifier", StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim();

        string? isbn13 = null;
        string? isbn10 = null;

        foreach (var idEl in dcContainer.Elements().Where(e => string.Equals(e.Name.LocalName, "identifier", StringComparison.OrdinalIgnoreCase)))
        {
            var val = idEl.Value.Trim();
            if (string.IsNullOrWhiteSpace(val))
            {
                continue;
            }

            if (val.Contains("d-nb.info/", StringComparison.OrdinalIgnoreCase))
            {
                var idx = val.IndexOf("d-nb.info/", StringComparison.OrdinalIgnoreCase) + "d-nb.info/".Length;
                var candidateIdn = val[idx..].Split('/', '?', '#')[0].Trim();
                if (!string.IsNullOrWhiteSpace(candidateIdn))
                {
                    idn ??= candidateIdn;
                }
            }
            else if (val.StartsWith("(DE-101)", StringComparison.OrdinalIgnoreCase))
            {
                idn ??= val["(DE-101)".Length..].Trim();
            }
            else if (val.StartsWith("idn:", StringComparison.OrdinalIgnoreCase) || val.StartsWith("idn=", StringComparison.OrdinalIgnoreCase))
            {
                idn ??= val[4..].Trim();
            }

            var cleanedIsbn = Regex.Replace(val, @"(?i)^(?:isbn(?:-1[03])?[:\s]*)", "").Replace("-", "").Replace(" ", "").Trim();
            if (cleanedIsbn.Length == 13 && (cleanedIsbn.StartsWith("978", StringComparison.Ordinal) || cleanedIsbn.StartsWith("979", StringComparison.Ordinal)) && cleanedIsbn.All(char.IsDigit))
            {
                isbn13 ??= cleanedIsbn;
            }
            else if (cleanedIsbn.Length == 10 && cleanedIsbn[..9].All(char.IsDigit) && (char.IsDigit(cleanedIsbn[9]) || cleanedIsbn[9] is 'X' or 'x'))
            {
                isbn10 ??= cleanedIsbn.ToUpperInvariant();
            }
            else if (idn is null && cleanedIsbn.Length is >= 8 and <= 11 && cleanedIsbn[..^1].All(char.IsDigit) && (char.IsDigit(cleanedIsbn[^1]) || cleanedIsbn[^1] is 'X' or 'x'))
            {
                idn = cleanedIsbn.ToUpperInvariant();
            }
        }

        idn ??= fallbackId?.Replace("idn=", "", StringComparison.OrdinalIgnoreCase) ?? isbn13 ?? Slugify(firstTitleRaw);

        var rawLang = dcContainer.Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "language", StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim();
        var lang = NormalizeLanguageCode(rawLang ?? "de");

        var subjects = dcContainer.Elements()
            .Where(e => string.Equals(e.Name.LocalName, "subject", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var descriptions = dcContainer.Elements()
            .Where(e => string.Equals(e.Name.LocalName, "description", StringComparison.OrdinalIgnoreCase))
            .Select(e => CleanDescription(e.Value))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        var overview = descriptions.FirstOrDefault() ?? "";
        if (string.IsNullOrWhiteSpace(overview) && !string.IsNullOrWhiteSpace(creator))
        {
            var yrSuffix = year.HasValue ? $" ({year.Value})" : "";
            overview = $"By {creator}{yrSuffix}.";
        }

        var extIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["dnb"] = idn,
        };
        if (!string.IsNullOrWhiteSpace(isbn13))
        {
            extIds["isbn13"] = isbn13;
        }

        if (!string.IsNullOrWhiteSpace(isbn10))
        {
            extIds["isbn10"] = isbn10;
        }

        var primaryTitle = LatinTitleNormalizer.SelectBestEnglishOrLatinTitle(
            LatinTitleNormalizer.IsCleanLatinText(firstTitleRaw) ? [firstTitleRaw] : [],
            rawTitles.Where(LatinTitleNormalizer.IsCleanLatinText),
            rawTitles,
            $"DNB #{idn}");

        var altTitles = LatinTitleNormalizer.OrderAndCleanAltTitles(primaryTitle, rawTitles);

        var candidate = LatinTitleNormalizer.NormalizeCandidate(new MetadataWorkCandidate(
            ProviderKey: descriptor.Key,
            ProviderItemId: idn,
            Vertical: vertical,
            Title: primaryTitle,
            AltTitles: altTitles,
            OriginalLanguage: lang,
            Year: year,
            Status: "released",
            Overview: overview,
            PosterUrl: !string.IsNullOrWhiteSpace(isbn13) ? $"https://portal.dnb.de/opac/mvb/cover?isbn={isbn13}" : null,
            ExternalIds: extIds,
            Containers: DefaultContainersForVertical(vertical, primaryTitle, year),
            Genres: subjects));

        return MediaTaxonomyClassifier.EnrichCandidate(candidate);
    }

    private IReadOnlyList<MetadataWorkCandidate> ParseBookBrainzJson(string rawJson, string vertical, string? fallbackId)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            var results = new List<MetadataWorkCandidate>();

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.Object && ParseSingleBookBrainzObject(el, vertical, fallbackId) is { } c)
                    {
                        results.Add(c);
                    }
                }

                return results;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var arrName in new[] { "results", "hits", "works", "items", "data" })
                {
                    if (root.TryGetProperty(arrName, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in arr.EnumerateArray())
                        {
                            if (el.ValueKind == JsonValueKind.Object && ParseSingleBookBrainzObject(el, vertical, fallbackId: null) is { } c)
                            {
                                results.Add(c);
                            }
                        }

                        return results;
                    }
                }

                var workNode = root.TryGetProperty("work", out var wEl) && wEl.ValueKind == JsonValueKind.Object
                    ? wEl
                    : root;

                if (ParseSingleBookBrainzObject(workNode, vertical, fallbackId) is { } single)
                {
                    results.Add(single);
                }
            }

            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private MetadataWorkCandidate? ParseSingleBookBrainzObject(JsonElement obj, string vertical, string? fallbackId)
    {
        var bbid = TryString(obj, "bbid")
            ?? TryString(obj, "id")
            ?? fallbackId;

        var defaultAlias = obj.TryGetProperty("defaultAlias", out var da) && da.ValueKind == JsonValueKind.Object
            ? da
            : default;

        var rawTitle = (defaultAlias.ValueKind == JsonValueKind.Object ? TryString(defaultAlias, "name") : null)
            ?? TryString(obj, "name")
            ?? TryString(obj, "title");

        var aliasNames = new List<string>();
        var englishAliases = new List<string>();

        if (!string.IsNullOrWhiteSpace(rawTitle))
        {
            aliasNames.Add(rawTitle);
            if (LatinTitleNormalizer.IsCleanLatinText(rawTitle))
            {
                englishAliases.Add(rawTitle);
            }
        }

        JsonElement aliasesArr = default;
        if (obj.TryGetProperty("aliases", out var directAliases) && directAliases.ValueKind == JsonValueKind.Array)
        {
            aliasesArr = directAliases;
        }
        else if (obj.TryGetProperty("aliasSet", out var aliasSet) &&
                 aliasSet.ValueKind == JsonValueKind.Object &&
                 aliasSet.TryGetProperty("aliases", out var nestedAliases) &&
                 nestedAliases.ValueKind == JsonValueKind.Array)
        {
            aliasesArr = nestedAliases;
        }

        if (aliasesArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var al in aliasesArr.EnumerateArray())
            {
                if (al.ValueKind != JsonValueKind.Object) continue;
                var alName = TryString(al, "name");
                if (string.IsNullOrWhiteSpace(alName)) continue;
                aliasNames.Add(alName);
                var alLang = TryString(al, "language")
                    ?? (al.TryGetProperty("language", out var lObj) && lObj.ValueKind == JsonValueKind.Object
                        ? TryString(lObj, "isoCode3") ?? TryString(lObj, "isoCode1") ?? TryString(lObj, "name")
                        : null)
                    ?? "";
                if ((alLang.StartsWith("en", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(alLang)) &&
                    LatinTitleNormalizer.IsCleanLatinText(alName))
                {
                    englishAliases.Add(alName);
                }
            }
        }

        if (aliasNames.Count == 0)
        {
            return null;
        }

        bbid ??= Slugify(aliasNames[0]);

        var primaryTitle = LatinTitleNormalizer.SelectBestEnglishOrLatinTitle(
            englishAliases,
            aliasNames.Where(LatinTitleNormalizer.IsCleanLatinText),
            aliasNames,
            $"BookBrainz #{bbid}");

        string? rawLang = null;
        if (defaultAlias.ValueKind == JsonValueKind.Object)
        {
            rawLang = TryString(defaultAlias, "language")
                ?? (defaultAlias.TryGetProperty("language", out var dlObj) && dlObj.ValueKind == JsonValueKind.Object
                    ? TryString(dlObj, "isoCode3") ?? TryString(dlObj, "isoCode1") ?? TryString(dlObj, "name")
                    : null);
        }

        if (string.IsNullOrWhiteSpace(rawLang))
        {
            JsonElement langsArr = default;
            if (obj.TryGetProperty("languages", out var lArr) && lArr.ValueKind == JsonValueKind.Array)
            {
                langsArr = lArr;
            }
            else if (obj.TryGetProperty("languageSet", out var lSet) &&
                     lSet.ValueKind == JsonValueKind.Object &&
                     lSet.TryGetProperty("languages", out var nlArr) &&
                     nlArr.ValueKind == JsonValueKind.Array)
            {
                langsArr = nlArr;
            }

            if (langsArr.ValueKind == JsonValueKind.Array)
            {
                var firstLang = langsArr.EnumerateArray().FirstOrDefault();
                rawLang = firstLang.ValueKind switch
                {
                    JsonValueKind.String => firstLang.GetString(),
                    JsonValueKind.Object => TryString(firstLang, "isoCode1") ?? TryString(firstLang, "isoCode3") ?? TryString(firstLang, "name"),
                    _ => null,
                };
            }
        }

        var lang = NormalizeLanguageCode(rawLang ?? "en");

        int? year = TryInt(obj, "year") ?? TryInt(obj, "releaseYear");
        if (year is null)
        {
            var relDate = TryString(obj, "releaseDate") ?? TryString(obj, "firstReleaseDate");
            if (!string.IsNullOrWhiteSpace(relDate) && FourDigitYearRegex().Match(relDate) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var yr))
            {
                year = yr;
            }
        }

        var disambig = TryString(obj, "disambiguation")
            ?? (obj.TryGetProperty("disambiguation", out var dObj) && dObj.ValueKind == JsonValueKind.Object ? TryString(dObj, "comment") : null);
        var annotation = TryString(obj, "annotation")
            ?? (obj.TryGetProperty("annotation", out var aObj) && aObj.ValueKind == JsonValueKind.Object ? TryString(aObj, "content") : null);
        var overview = CleanDescription(annotation ?? disambig ?? TryString(obj, "description"));

        var extIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bookbrainz"] = bbid,
        };

        JsonElement identsArr = default;
        if (obj.TryGetProperty("identifiers", out var dIdents) && dIdents.ValueKind == JsonValueKind.Array)
        {
            identsArr = dIdents;
        }
        else if (obj.TryGetProperty("identifierSet", out var iSet) &&
                 iSet.ValueKind == JsonValueKind.Object &&
                 iSet.TryGetProperty("identifiers", out var nIdents) &&
                 nIdents.ValueKind == JsonValueKind.Array)
        {
            identsArr = nIdents;
        }

        if (identsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var ident in identsArr.EnumerateArray())
            {
                if (ident.ValueKind != JsonValueKind.Object) continue;
                var val = TryString(ident, "value");
                if (string.IsNullOrWhiteSpace(val)) continue;
                var label = TryString(ident, "type")
                    ?? (ident.TryGetProperty("type", out var tObj) && tObj.ValueKind == JsonValueKind.Object ? TryString(tObj, "label") : null)
                    ?? "";
                if (label.Contains("ISBN-13", StringComparison.OrdinalIgnoreCase) || (val.Length == 13 && val.All(char.IsDigit)))
                {
                    extIds["isbn13"] = val.Replace("-", "").Trim();
                }
                else if (label.Contains("OpenLibrary", StringComparison.OrdinalIgnoreCase))
                {
                    extIds["openlibrary"] = val.Trim();
                }
                else if (label.Contains("Wikidata", StringComparison.OrdinalIgnoreCase))
                {
                    extIds["wikidata"] = val.Trim();
                }
            }
        }

        var candidate = LatinTitleNormalizer.NormalizeCandidate(new MetadataWorkCandidate(
            ProviderKey: descriptor.Key,
            ProviderItemId: bbid,
            Vertical: vertical,
            Title: primaryTitle,
            AltTitles: LatinTitleNormalizer.OrderAndCleanAltTitles(primaryTitle, aliasNames),
            OriginalLanguage: lang,
            Year: year,
            Status: "released",
            Overview: overview,
            PosterUrl: null,
            ExternalIds: extIds,
            Containers: DefaultContainersForVertical(vertical, primaryTitle, year),
            Genres: []));

        return MediaTaxonomyClassifier.EnrichCandidate(candidate);
    }

    private IReadOnlyList<MetadataWorkCandidate> ParseDiscogsJson(string rawJson, string vertical, string? fallbackId)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            var results = new List<MetadataWorkCandidate>();

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("results", out var resArr) &&
                resArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in resArr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && ParseSingleDiscogsObject(item, vertical, fallbackId: null) is { } c)
                    {
                        results.Add(c);
                    }
                }

                return results;
            }

            if (root.ValueKind == JsonValueKind.Object && ParseSingleDiscogsObject(root, vertical, fallbackId) is { } single)
            {
                results.Add(single);
            }

            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private MetadataWorkCandidate? ParseSingleDiscogsObject(JsonElement obj, string vertical, string? fallbackId)
    {
        var rawTitle = TryString(obj, "title");
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return null;
        }

        var masterId = TryString(obj, "master_id");
        var id = (!string.IsNullOrWhiteSpace(masterId) && masterId != "0" ? masterId : null)
            ?? TryString(obj, "id")
            ?? fallbackId
            ?? Slugify(rawTitle);

        string? artist = null;
        var albumTitle = rawTitle.Trim();

        if (obj.TryGetProperty("artists", out var artistsArr) && artistsArr.ValueKind == JsonValueKind.Array)
        {
            var artistNames = new List<string>();
            foreach (var a in artistsArr.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.Object && TryString(a, "name") is { Length: > 0 } aName)
                {
                    artistNames.Add(DiscogsArtistNumberSuffixRegex().Replace(aName.Trim(), ""));
                }
            }

            if (artistNames.Count > 0)
            {
                artist = string.Join(", ", artistNames);
            }
        }
        else if (albumTitle.Contains(" - ", StringComparison.Ordinal))
        {
            var parts = albumTitle.Split(" - ", 2, StringSplitOptions.TrimEntries);
            artist = DiscogsArtistNumberSuffixRegex().Replace(parts[0], "");
            albumTitle = parts[1];
        }

        var displayTitle = !string.IsNullOrWhiteSpace(artist) && !albumTitle.Contains(artist, StringComparison.OrdinalIgnoreCase)
            ? $"{artist} — {albumTitle}"
            : albumTitle;

        var year = TryInt(obj, "year");
        var poster = TryString(obj, "cover_image") ?? TryString(obj, "thumb");
        if (string.IsNullOrWhiteSpace(poster) &&
            obj.TryGetProperty("images", out var imgArr) &&
            imgArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var img in imgArr.EnumerateArray())
            {
                if (img.ValueKind != JsonValueKind.Object) continue;
                var uri = TryString(img, "uri") ?? TryString(img, "resource_url") ?? TryString(img, "uri150");
                if (string.IsNullOrWhiteSpace(uri)) continue;
                poster ??= uri;
                if (string.Equals(TryString(img, "type"), "primary", StringComparison.OrdinalIgnoreCase))
                {
                    poster = uri;
                    break;
                }
            }
        }

        var genres = new List<string>();
        foreach (var gProp in new[] { "genres", "genre", "styles", "style" })
        {
            if (obj.TryGetProperty(gProp, out var gArr) && gArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var gEl in gArr.EnumerateArray())
                {
                    if (gEl.ValueKind == JsonValueKind.String && gEl.GetString() is { Length: > 0 } gs)
                    {
                        genres.Add(gs.Trim());
                    }
                }
            }
        }

        var notes = CleanDescription(TryString(obj, "notes"));
        if (string.IsNullOrWhiteSpace(notes))
        {
            var yrSuffix = year.HasValue ? $" ({year.Value})" : "";
            notes = !string.IsNullOrWhiteSpace(artist) ? $"Album by {artist}{yrSuffix}." : $"Discogs Master #{id}{yrSuffix}.";
        }

        var extIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["discogs"] = id,
        };
        if (TryString(obj, "main_release") is { Length: > 0 } mainRel && mainRel != "0")
        {
            extIds["discogs_release"] = mainRel;
        }

        IReadOnlyList<MetadataContainerSeed> containers;
        if (obj.TryGetProperty("tracklist", out var tracksArr) && tracksArr.ValueKind == JsonValueKind.Array)
        {
            var date = year.HasValue ? $"{year.Value}-01-01" : null;
            var units = new List<MetadataUnitSeed>();
            var trackIndex = 1;
            foreach (var tr in tracksArr.EnumerateArray())
            {
                if (tr.ValueKind != JsonValueKind.Object) continue;
                var type = TryString(tr, "type_");
                if (string.Equals(type, "heading", StringComparison.OrdinalIgnoreCase)) continue;
                var trTitle = TryString(tr, "title");
                if (string.IsNullOrWhiteSpace(trTitle)) continue;
                units.Add(new MetadataUnitSeed("track", trackIndex, trackIndex, trTitle.Trim(), date));
                trackIndex++;
            }

            containers = units.Count > 0
                ? [new MetadataContainerSeed("album", 1, albumTitle, date, units)]
                : DefaultContainersForVertical(vertical, displayTitle, year);
        }
        else
        {
            containers = DefaultContainersForVertical(vertical, displayTitle, year);
        }

        var candidate = LatinTitleNormalizer.NormalizeCandidate(new MetadataWorkCandidate(
            ProviderKey: descriptor.Key,
            ProviderItemId: id,
            Vertical: vertical,
            Title: displayTitle,
            AltTitles: LatinTitleNormalizer.OrderAndCleanAltTitles(displayTitle, [albumTitle, rawTitle]),
            OriginalLanguage: "en",
            Year: year,
            Status: "released",
            Overview: notes,
            PosterUrl: poster,
            ExternalIds: extIds,
            Containers: containers,
            Genres: genres.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));

        return MediaTaxonomyClassifier.EnrichCandidate(candidate);
    }

    private IReadOnlyList<MetadataWorkCandidate> ParseComicVineJson(string rawJson, string vertical, string? fallbackId)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            var results = new List<MetadataWorkCandidate>();

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var resEl))
            {
                if (resEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in resEl.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object && ParseSingleComicVineVolume(item, vertical, fallbackId: null) is { } c)
                        {
                            results.Add(c);
                        }
                    }

                    return results;
                }

                if (resEl.ValueKind == JsonValueKind.Object && ParseSingleComicVineVolume(resEl, vertical, fallbackId) is { } single)
                {
                    results.Add(single);
                    return results;
                }
            }

            if (root.ValueKind == JsonValueKind.Object && ParseSingleComicVineVolume(root, vertical, fallbackId) is { } direct)
            {
                results.Add(direct);
            }

            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private MetadataWorkCandidate? ParseSingleComicVineVolume(JsonElement obj, string vertical, string? fallbackId)
    {
        var rawTitle = TryString(obj, "name") ?? TryString(obj, "title");
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return null;
        }

        var cleanFallback = fallbackId?.StartsWith("4050-", StringComparison.OrdinalIgnoreCase) == true
            ? fallbackId["4050-".Length..]
            : fallbackId;
        var id = TryString(obj, "id") ?? cleanFallback ?? Slugify(rawTitle);

        var aliasesRaw = TryString(obj, "aliases");
        var aliasList = string.IsNullOrWhiteSpace(aliasesRaw)
            ? []
            : aliasesRaw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var year = TryInt(obj, "start_year") ?? TryInt(obj, "year");
        var overview = CleanDescription(TryString(obj, "deck") ?? TryString(obj, "description"));

        string? poster = null;
        if (obj.TryGetProperty("image", out var imgObj) && imgObj.ValueKind == JsonValueKind.Object)
        {
            poster = TryString(imgObj, "original_url")
                ?? TryString(imgObj, "super_url")
                ?? TryString(imgObj, "medium_url")
                ?? TryString(imgObj, "small_url");
        }

        var genres = new List<string> { "Comics" };
        if (obj.TryGetProperty("publisher", out var pubObj) &&
            pubObj.ValueKind == JsonValueKind.Object &&
            TryString(pubObj, "name") is { Length: > 0 } pubName)
        {
            genres.Add(pubName.Trim());
        }

        var extIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["comicvine"] = id,
            ["comicvine_volume"] = $"4050-{id}",
        };

        var date = year.HasValue ? $"{year.Value}-01-01" : null;
        IReadOnlyList<MetadataContainerSeed> containers;
        if (obj.TryGetProperty("issues", out var issuesArr) && issuesArr.ValueKind == JsonValueKind.Array)
        {
            var units = new List<MetadataUnitSeed>();
            var idx = 1;
            foreach (var iss in issuesArr.EnumerateArray())
            {
                if (iss.ValueKind != JsonValueKind.Object) continue;
                var issNumStr = TryString(iss, "issue_number");
                var unitNum = int.TryParse(issNumStr, out var n) && n > 0 ? n : idx;
                var issName = TryString(iss, "name");
                var unitTitle = !string.IsNullOrWhiteSpace(issName) ? issName.Trim() : $"Issue #{issNumStr ?? unitNum.ToString()}";
                units.Add(new MetadataUnitSeed("chapter", unitNum, unitNum, unitTitle, TryString(iss, "cover_date") ?? date));
                idx++;
            }

            containers = units.Count > 0
                ? [new MetadataContainerSeed("volume", 1, rawTitle.Trim(), date, units.OrderBy(u => u.UnitNumber).ToList())]
                : DefaultContainersForVertical(vertical, rawTitle.Trim(), year);
        }
        else if (TryInt(obj, "count_of_issues") is > 0 and var countOfIssues)
        {
            var capped = Math.Min(countOfIssues, 200);
            var units = Enumerable.Range(1, capped)
                .Select(i => new MetadataUnitSeed("chapter", i, i, $"Issue #{i}", date))
                .ToList();
            containers = [new MetadataContainerSeed("volume", 1, rawTitle.Trim(), date, units)];
        }
        else
        {
            containers = DefaultContainersForVertical(vertical, rawTitle.Trim(), year);
        }

        var candidate = LatinTitleNormalizer.NormalizeCandidate(new MetadataWorkCandidate(
            ProviderKey: descriptor.Key,
            ProviderItemId: id,
            Vertical: vertical,
            Title: rawTitle.Trim(),
            AltTitles: LatinTitleNormalizer.OrderAndCleanAltTitles(rawTitle.Trim(), aliasList),
            OriginalLanguage: "en",
            Year: year,
            Status: "released",
            Overview: overview,
            PosterUrl: poster,
            ExternalIds: extIds,
            Containers: containers,
            Genres: genres.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));

        return MediaTaxonomyClassifier.EnrichCandidate(candidate);
    }

    private IReadOnlyList<MetadataWorkCandidate> ParseAniDbXml(string rawXml, string query, string vertical, string? fallbackId)
    {
        try
        {
            var doc = XDocument.Parse(rawXml);
            var root = doc.Root;
            if (root is null)
            {
                return [];
            }

            if (string.Equals(root.Name.LocalName, "error", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            if (string.Equals(root.Name.LocalName, "anime", StringComparison.OrdinalIgnoreCase))
            {
                var single = ParseSingleAniDbElement(root, vertical, fallbackId);
                return single is not null ? [single] : [];
            }

            var animeNodes = root.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, "anime", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var matched = new List<MetadataWorkCandidate>();
            var allParsed = new List<MetadataWorkCandidate>();
            var cleanQ = query.Trim();

            foreach (var node in animeNodes)
            {
                var cand = ParseSingleAniDbElement(node, vertical, fallbackId: null);
                if (cand is null) continue;
                allParsed.Add(cand);

                if (string.IsNullOrWhiteSpace(cleanQ) ||
                    string.Equals(cand.ProviderItemId, cleanQ, StringComparison.OrdinalIgnoreCase) ||
                    cand.Title.Contains(cleanQ, StringComparison.OrdinalIgnoreCase) ||
                    cleanQ.Contains(cand.Title, StringComparison.OrdinalIgnoreCase) ||
                    cand.AltTitles.Any(a => a.Contains(cleanQ, StringComparison.OrdinalIgnoreCase) || cleanQ.Contains(a, StringComparison.OrdinalIgnoreCase)))
                {
                    matched.Add(cand);
                }
            }

            return matched.Count > 0 ? matched : allParsed;
        }
        catch
        {
            return [];
        }
    }

    private MetadataWorkCandidate? ParseSingleAniDbElement(XElement animeEl, string vertical, string? fallbackId)
    {
        var aid = animeEl.Attribute("id")?.Value
            ?? animeEl.Attribute("aid")?.Value
            ?? fallbackId?.Replace("aid:", "", StringComparison.OrdinalIgnoreCase).Replace("anidb:", "", StringComparison.OrdinalIgnoreCase)
            ?? "1";

        var titleElements = animeEl.Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "titles", StringComparison.OrdinalIgnoreCase))
            ?.Elements().Where(e => string.Equals(e.Name.LocalName, "title", StringComparison.OrdinalIgnoreCase)).ToList()
            ?? animeEl.Elements().Where(e => string.Equals(e.Name.LocalName, "title", StringComparison.OrdinalIgnoreCase)).ToList();

        var englishTitles = new List<string>();
        var romajiTitles = new List<string>();
        var allTitles = new List<string>();

        foreach (var tEl in titleElements)
        {
            var val = tEl.Value.Trim();
            if (string.IsNullOrWhiteSpace(val)) continue;
            allTitles.Add(val);

            var tType = tEl.Attribute("type")?.Value?.ToLowerInvariant() ?? "";
            var langAttr = tEl.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, "lang", StringComparison.OrdinalIgnoreCase))?.Value?.ToLowerInvariant() ?? "";

            if (langAttr == "en" && tType is "official" or "main" or "")
            {
                englishTitles.Add(val);
            }
            else if (tType == "main" || langAttr == "x-jat" || (langAttr == "en" && tType is "synonym" or "short"))
            {
                romajiTitles.Add(val);
            }
            else if (LatinTitleNormalizer.IsCleanLatinText(val))
            {
                romajiTitles.Add(val);
            }
        }

        if (allTitles.Count == 0)
        {
            return null;
        }

        var primaryTitle = LatinTitleNormalizer.SelectBestEnglishOrLatinTitle(
            englishTitles,
            romajiTitles,
            allTitles,
            $"Anime #{aid}");

        var altTitles = LatinTitleNormalizer.OrderAndCleanAltTitles(primaryTitle, allTitles);

        int? year = null;
        var startDateStr = animeEl.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "startdate", StringComparison.OrdinalIgnoreCase))?.Value.Trim();
        if (!string.IsNullOrWhiteSpace(startDateStr) && startDateStr.Length >= 4 && int.TryParse(startDateStr[..4], out var parsedYr))
        {
            year = parsedYr;
        }

        var endDateStr = animeEl.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "enddate", StringComparison.OrdinalIgnoreCase))?.Value.Trim();
        var status = string.IsNullOrWhiteSpace(endDateStr) ? "continuing" : "ended";

        var rawDesc = animeEl.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "description", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(rawDesc))
        {
            rawDesc = AniDbWikiLinkRegex().Replace(rawDesc, "$1");
        }

        var overview = CleanDescription(rawDesc);

        string? poster = null;
        var pic = animeEl.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "picture", StringComparison.OrdinalIgnoreCase))?.Value.Trim();
        if (!string.IsNullOrWhiteSpace(pic))
        {
            poster = pic.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pic.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? pic
                : $"https://cdn-eu.anidb.net/images/main/{pic}";
        }

        var extIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["anidb"] = aid,
        };

        var resourcesEl = animeEl.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "resources", StringComparison.OrdinalIgnoreCase));
        if (resourcesEl is not null)
        {
            foreach (var res in resourcesEl.Elements().Where(e => string.Equals(e.Name.LocalName, "resource", StringComparison.OrdinalIgnoreCase)))
            {
                var rType = res.Attribute("type")?.Value;
                var ident = res.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "identifier", StringComparison.OrdinalIgnoreCase))?.Value.Trim();
                if (string.IsNullOrWhiteSpace(ident)) continue;
                if (rType == "2") extIds["mal"] = ident;
                else if (rType == "1") extIds["ann"] = ident;
            }
        }

        var genres = new List<string> { "Anime", "Animation" };
        var tagsEl = animeEl.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "tags", StringComparison.OrdinalIgnoreCase));
        if (tagsEl is not null)
        {
            foreach (var tag in tagsEl.Elements().Where(e => string.Equals(e.Name.LocalName, "tag", StringComparison.OrdinalIgnoreCase)))
            {
                var name = tag.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "name", StringComparison.OrdinalIgnoreCase))?.Value.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    genres.Add(name);
                }
            }
        }

        var formatType = animeEl.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "type", StringComparison.OrdinalIgnoreCase))?.Value.Trim();

        IReadOnlyList<MetadataContainerSeed> containers;
        var episodesEl = animeEl.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "episodes", StringComparison.OrdinalIgnoreCase));
        if (episodesEl is not null)
        {
            var regularUnits = new List<MetadataUnitSeed>();
            var specialUnits = new List<MetadataUnitSeed>();
            foreach (var epEl in episodesEl.Elements().Where(e => string.Equals(e.Name.LocalName, "episode", StringComparison.OrdinalIgnoreCase)))
            {
                var epnoEl = epEl.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "epno", StringComparison.OrdinalIgnoreCase));
                var epnoType = epnoEl?.Attribute("type")?.Value ?? "1";
                var epnoText = epnoEl?.Value.Trim() ?? "";
                var isSpecial = epnoType == "2" || epnoText.StartsWith("S", StringComparison.OrdinalIgnoreCase);
                var cleanEpNumStr = isSpecial ? epnoText.TrimStart('S', 's') : epnoText;
                if ((epnoType != "1" && !isSpecial) || !int.TryParse(cleanEpNumStr, out var epNum) || epNum <= 0)
                {
                    continue;
                }

                var epTitles = epEl.Elements()
                    .Where(e => string.Equals(e.Name.LocalName, "title", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var enEpTitle = epTitles.FirstOrDefault(t =>
                    string.Equals(t.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, "lang", StringComparison.OrdinalIgnoreCase))?.Value, "en", StringComparison.OrdinalIgnoreCase))?.Value.Trim();
                var romajiEpTitle = epTitles.FirstOrDefault(t =>
                    string.Equals(t.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, "lang", StringComparison.OrdinalIgnoreCase))?.Value, "x-jat", StringComparison.OrdinalIgnoreCase))?.Value.Trim();
                var fallbackEpTitle = epTitles.FirstOrDefault()?.Value.Trim();

                var epTitle = LatinTitleNormalizer.SelectBestEnglishOrLatinTitle(
                    !string.IsNullOrWhiteSpace(enEpTitle) ? [enEpTitle] : [],
                    !string.IsNullOrWhiteSpace(romajiEpTitle) ? [romajiEpTitle] : [],
                    !string.IsNullOrWhiteSpace(fallbackEpTitle) ? [fallbackEpTitle] : [],
                    isSpecial ? $"Special {epNum}" : $"Episode {epNum}");

                var airDate = epEl.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "airdate", StringComparison.OrdinalIgnoreCase))?.Value.Trim();
                if (isSpecial)
                {
                    specialUnits.Add(new MetadataUnitSeed("episode", epNum, null, epTitle, airDate));
                }
                else
                {
                    regularUnits.Add(new MetadataUnitSeed("episode", epNum, epNum, epTitle, airDate));
                }
            }

            if (regularUnits.Count > 0 || specialUnits.Count > 0)
            {
                var builtContainers = new List<MetadataContainerSeed>();
                if (regularUnits.Count > 0)
                {
                    var ordered = regularUnits.OrderBy(u => u.UnitNumber).ToList();
                    builtContainers.Add(new MetadataContainerSeed(
                        "season",
                        1,
                        "Season 1",
                        ordered[0].AirOrReleaseDate ?? startDateStr,
                        ordered));
                }

                if (specialUnits.Count > 0)
                {
                    var orderedSpecials = specialUnits.OrderBy(u => u.UnitNumber).ToList();
                    builtContainers.Add(new MetadataContainerSeed(
                        "season",
                        0,
                        "Specials",
                        orderedSpecials[0].AirOrReleaseDate ?? startDateStr,
                        orderedSpecials));
                }

                containers = builtContainers;
            }
            else
            {
                containers = DefaultContainersForVertical(vertical, primaryTitle, year);
            }
        }
        else if (int.TryParse(animeEl.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "episodecount", StringComparison.OrdinalIgnoreCase))?.Value.Trim(), out var epCount) && epCount > 0)
        {
            var capped = Math.Min(epCount, 200);
            var relDate = startDateStr ?? (year.HasValue ? $"{year.Value}-01-01" : null);
            var units = Enumerable.Range(1, capped)
                .Select(i => new MetadataUnitSeed("episode", i, i, $"Episode {i}", relDate))
                .ToList();
            containers = [new MetadataContainerSeed("season", 1, "Season 1", relDate, units)];
        }
        else
        {
            containers = DefaultContainersForVertical(vertical, primaryTitle, year);
        }

        double popularity = 0;
        var ratingsEl = animeEl.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "ratings", StringComparison.OrdinalIgnoreCase));
        if (ratingsEl is not null)
        {
            foreach (var rChild in ratingsEl.Elements())
            {
                if (double.TryParse(rChild.Attribute("count")?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rCount) && rCount > popularity)
                {
                    popularity = rCount;
                }
            }
        }

        var candidate = LatinTitleNormalizer.NormalizeCandidate(new MetadataWorkCandidate(
            ProviderKey: descriptor.Key,
            ProviderItemId: aid,
            Vertical: vertical,
            Title: primaryTitle,
            AltTitles: altTitles,
            OriginalLanguage: "ja",
            Year: year,
            Status: status,
            Overview: overview,
            PosterUrl: poster,
            ExternalIds: extIds,
            Containers: containers,
            Genres: genres.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Popularity: popularity));

        return MediaTaxonomyClassifier.EnrichCandidate(candidate, formatHint: formatType);
    }

    private IReadOnlyList<MetadataWorkCandidate> ParseAcoustIdJson(string rawJson, string vertical, string? fallbackId)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("results", out var resArr) ||
                resArr.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<MetadataWorkCandidate>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var resItem in resArr.EnumerateArray())
            {
                if (resItem.ValueKind != JsonValueKind.Object) continue;
                var acoustId = TryString(resItem, "id") ?? fallbackId ?? Guid.NewGuid().ToString();

                if (!resItem.TryGetProperty("recordings", out var recArr) || recArr.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var rec in recArr.EnumerateArray())
                {
                    if (rec.ValueKind != JsonValueKind.Object) continue;
                    var recordingId = TryString(rec, "id");
                    var recordingTitle = TryString(rec, "title");
                    var recArtist = ExtractFirstAcoustIdArtist(rec);

                    if (rec.TryGetProperty("releasegroups", out var rgArr) && rgArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var rg in rgArr.EnumerateArray())
                        {
                            if (rg.ValueKind != JsonValueKind.Object) continue;
                            var rgId = TryString(rg, "id") ?? recordingId ?? acoustId;
                            if (!seenIds.Add(rgId)) continue;

                            var rgTitle = TryString(rg, "title") ?? recordingTitle;
                            if (string.IsNullOrWhiteSpace(rgTitle)) continue;

                            var artist = ExtractFirstAcoustIdArtist(rg) ?? recArtist;
                            var displayTitle = !string.IsNullOrWhiteSpace(artist) && !rgTitle.Contains(artist, StringComparison.OrdinalIgnoreCase)
                                ? $"{artist} — {rgTitle}"
                                : rgTitle;

                            var extIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["acoustid"] = acoustId,
                                ["musicbrainz"] = rgId,
                            };
                            if (!string.IsNullOrWhiteSpace(recordingId))
                            {
                                extIds["musicbrainz_recording"] = recordingId;
                            }

                            var trackTitle = !string.IsNullOrWhiteSpace(recordingTitle) ? recordingTitle : rgTitle;
                            var containers = new List<MetadataContainerSeed>
                            {
                                new("album", 1, rgTitle, null, [new MetadataUnitSeed("track", 1, 1, trackTitle, null)]),
                            };

                            var candidate = LatinTitleNormalizer.NormalizeCandidate(new MetadataWorkCandidate(
                                ProviderKey: descriptor.Key,
                                ProviderItemId: rgId,
                                Vertical: vertical,
                                Title: displayTitle,
                                AltTitles: LatinTitleNormalizer.OrderAndCleanAltTitles(displayTitle, [rgTitle, recordingTitle]),
                                OriginalLanguage: "en",
                                Year: null,
                                Status: "released",
                                Overview: !string.IsNullOrWhiteSpace(artist)
                                    ? $"{TryString(rg, "type") ?? "Album"} by {artist} (matched via AcoustID {acoustId})."
                                    : $"Matched via AcoustID {acoustId}.",
                                PosterUrl: Guid.TryParse(rgId, out _) ? $"https://coverartarchive.org/release-group/{rgId}/front-250" : null,
                                ExternalIds: extIds,
                                Containers: containers,
                                Genres: []));

                            results.Add(MediaTaxonomyClassifier.EnrichCandidate(candidate));
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(recordingTitle))
                    {
                        var itemId = recordingId ?? acoustId;
                        if (!seenIds.Add(itemId)) continue;

                        var displayTitle = !string.IsNullOrWhiteSpace(recArtist) && !recordingTitle.Contains(recArtist, StringComparison.OrdinalIgnoreCase)
                            ? $"{recArtist} — {recordingTitle}"
                            : recordingTitle;

                        var extIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["acoustid"] = acoustId,
                        };
                        if (!string.IsNullOrWhiteSpace(recordingId))
                        {
                            extIds["musicbrainz_recording"] = recordingId;
                        }

                        var candidate = LatinTitleNormalizer.NormalizeCandidate(new MetadataWorkCandidate(
                            ProviderKey: descriptor.Key,
                            ProviderItemId: itemId,
                            Vertical: vertical,
                            Title: displayTitle,
                            AltTitles: LatinTitleNormalizer.OrderAndCleanAltTitles(displayTitle, [recordingTitle]),
                            OriginalLanguage: "en",
                            Year: null,
                            Status: "released",
                            Overview: !string.IsNullOrWhiteSpace(recArtist)
                                ? $"Recording by {recArtist} (matched via AcoustID {acoustId})."
                                : $"Matched via AcoustID {acoustId}.",
                            PosterUrl: null,
                            ExternalIds: extIds,
                            Containers: DefaultContainersForVertical(vertical, displayTitle, null),
                            Genres: []));

                        results.Add(MediaTaxonomyClassifier.EnrichCandidate(candidate));
                    }
                }
            }

            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ExtractFirstAcoustIdArtist(JsonElement obj)
    {
        if (obj.TryGetProperty("artists", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var a in arr.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.Object && TryString(a, "name") is { Length: > 0 } name)
                {
                    names.Add(name.Trim());
                }
            }

            if (names.Count > 0)
            {
                return string.Join(", ", names);
            }
        }

        return null;
    }

    private MetadataWorkCandidate? ParseLookupJson(string rawJson, string externalId, string vertical)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var first = root.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
                return first.ValueKind == JsonValueKind.Object ? ParseSingleObject(first, vertical, externalId) : null;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                // AniList & Hardcover single GraphQL responses
                if (root.TryGetProperty("data", out var dataObj) && dataObj.ValueKind == JsonValueKind.Object)
                {
                    if (dataObj.TryGetProperty("Media", out var mediaNode) && mediaNode.ValueKind == JsonValueKind.Object)
                    {
                        return ParseAniListMediaNode(mediaNode, vertical);
                    }

                    if (dataObj.TryGetProperty("books_by_pk", out var hcBookNode) && hcBookNode.ValueKind == JsonValueKind.Object)
                    {
                        return ParseSingleObject(hcBookNode, vertical, externalId);
                    }

                    if (dataObj.TryGetProperty("books", out var hcBooksArr) && hcBooksArr.ValueKind == JsonValueKind.Array)
                    {
                        var firstBook = hcBooksArr.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
                        if (firstBook.ValueKind == JsonValueKind.Object)
                        {
                            return ParseSingleObject(firstBook, vertical, externalId);
                        }
                    }

                    // MangaDex single manga response: { data: { id: "...", attributes: { ... } } }
                    if (dataObj.TryGetProperty("attributes", out _))
                    {
                        return ParseSingleObject(dataObj, vertical, externalId);
                    }
                }

                if (root.TryGetProperty("results", out var resArr) && resArr.ValueKind == JsonValueKind.Array)
                {
                    var first = resArr.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
                    if (first.ValueKind == JsonValueKind.Object)
                    {
                        return ParseSingleObject(first, vertical, externalId);
                    }
                }

                return ParseSingleObject(root, vertical, externalId);
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

}
