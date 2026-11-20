using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml.Linq;
using Managerr.Datastore.Services;

namespace Managerr.Metadata;

internal sealed partial class UniversalBuiltInMetadataProvider
{
    internal MetadataWorkCandidate? ParseSingleObject(JsonElement obj, string vertical, string? fallbackId)
    {
        if (obj.ValueKind == JsonValueKind.Object &&
            !obj.TryGetProperty("title", out _) &&
            !obj.TryGetProperty("Title", out _) &&
            !obj.TryGetProperty("name", out _) &&
            !obj.TryGetProperty("collectionName", out _) &&
            !obj.TryGetProperty("attributes", out _) &&
            !obj.TryGetProperty("volumeInfo", out _) &&
            obj.TryGetProperty("data", out var tvdbEnvelopeData) &&
            tvdbEnvelopeData.ValueKind == JsonValueKind.Object)
        {
            obj = tvdbEnvelopeData;
        }

        var hasVolumeInfo = obj.TryGetProperty("volumeInfo", out var vi) && vi.ValueKind == JsonValueKind.Object;

        var rawTitle = TryString(obj, "title")
            ?? TryString(obj, "Title")
            ?? TryString(obj, "name")
            ?? TryString(obj, "collectionName")
            ?? (obj.TryGetProperty("attributes", out var attr) && attr.TryGetProperty("title", out var mdTitle) && mdTitle.ValueKind == JsonValueKind.Object
                ? TryString(mdTitle, "en")
                  ?? TryString(mdTitle, "en-us")
                  ?? TryString(mdTitle, "ja-ro")
                  ?? mdTitle.EnumerateObject().FirstOrDefault().Value.GetString()
                : null)
            ?? (hasVolumeInfo ? TryString(vi, "title") : null);

        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return null;
        }

        var lang = ExtractNormalizedLanguage(obj, hasVolumeInfo ? vi : default, rawTitle);
        HarvestStructuredTitles(
            obj,
            lang,
            out var englishTitles,
            out var latinTitles,
            out var tier1Structured,
            out var tier2Structured);
        if (LatinTitleNormalizer.IsCleanLatinText(rawTitle))
        {
            englishTitles.Insert(0, rawTitle.Trim());
        }

        var traktIdsObj = obj.TryGetProperty("ids", out var tIds) && tIds.ValueKind == JsonValueKind.Object
            ? tIds
            : default;

        var id = TryString(obj, "TmdbId")
            ?? TryString(obj, "tvdbId")
            ?? TryString(obj, "TvdbId")
            ?? TryString(obj, "id")
            ?? (traktIdsObj.ValueKind == JsonValueKind.Object ? TryString(traktIdsObj, "trakt") ?? TryString(traktIdsObj, "slug") : null)
            ?? TryString(obj, "asin")
            ?? TryString(obj, "collectionId")
            ?? TryString(obj, "key")?.Replace("/works/", "", StringComparison.Ordinal)
            ?? fallbackId
            ?? Slugify(rawTitle);

        var tier1Alts = new List<string> { rawTitle.Trim() };

        foreach (var altProp in new[] { "original_title", "original_name", "originalTitle", "OriginalTitle" })
        {
            var v = TryString(obj, altProp);
            if (!string.IsNullOrWhiteSpace(v))
            {
                tier1Alts.Add(v.Trim());
                if (LatinTitleNormalizer.IsCleanLatinText(v))
                {
                    latinTitles.Add(v.Trim());
                }
            }
        }

        foreach (var altArrName in new[] { "altTitles", "AlternativeTitles", "alternativeTitles" })
        {
            if (obj.TryGetProperty(altArrName, out var altArr) && altArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in altArr.EnumerateArray())
                {
                    var s = el.ValueKind switch
                    {
                        JsonValueKind.String => el.GetString(),
                        JsonValueKind.Object => TryString(el, "Title") ?? TryString(el, "title") ?? TryString(el, "name"),
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        var trimmed = s.Trim();
                        tier1Alts.Add(trimmed);
                        if (LatinTitleNormalizer.IsCleanLatinText(trimmed))
                        {
                            latinTitles.Add(trimmed);
                        }
                    }
                }
            }
        }

        tier1Alts.AddRange(tier1Structured);

        var altTitles = new List<string>(tier1Alts.Count + tier2Structured.Count);
        altTitles.AddRange(tier1Alts);
        altTitles.AddRange(tier2Structured);

        var coreTitle = LatinTitleNormalizer.SelectBestEnglishOrLatinTitle(
            englishCandidates: englishTitles,
            latinOrRomajiCandidates: latinTitles,
            fallbackCandidates: altTitles,
            fallbackLabel: $"{descriptor.Key}:{id}");

        var creator = ExtractCreatorOrArtist(obj, hasVolumeInfo ? vi : default);
        var title = coreTitle;

        if (vertical == MetadataVerticals.Music &&
            !string.IsNullOrWhiteSpace(creator) &&
            !title.Contains(creator, StringComparison.OrdinalIgnoreCase))
        {
            altTitles.Insert(0, coreTitle);
            title = $"{creator} — {coreTitle}";
        }

        int? year = TryInt(obj, "year")
            ?? TryInt(obj, "Year")
            ?? TryInt(obj, "first_publish_year")
            ?? TryInt(obj, "release_year");

        if (year is null)
        {
            var dateStr = TryString(obj, "release_date")
                ?? TryString(obj, "first_air_date")
                ?? TryString(obj, "firstAired")
                ?? TryString(obj, "Premier")
                ?? TryString(obj, "InCinema")
                ?? TryString(obj, "premiered")
                ?? TryString(obj, "first-release-date")
                ?? TryString(obj, "releaseDate")
                ?? (hasVolumeInfo ? TryString(vi, "publishedDate") : null);

            if (!string.IsNullOrWhiteSpace(dateStr) && dateStr.Length >= 4 && int.TryParse(dateStr[..4], out var parsedYr))
            {
                year = parsedYr;
            }
        }

        var extIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [descriptor.Key] = id,
        };

        if (traktIdsObj.ValueKind == JsonValueKind.Object)
        {
            if (TryString(traktIdsObj, "trakt") is { Length: > 0 } trId) extIds["trakt"] = trId;
            if (TryString(traktIdsObj, "slug") is { Length: > 0 } trSlug) extIds["slug"] = trSlug;
            if (TryString(traktIdsObj, "tvdb") is { Length: > 0 } trTvdb && trTvdb != "0") extIds["tvdb"] = trTvdb;
            if (TryString(traktIdsObj, "tmdb") is { Length: > 0 } trTmdb && trTmdb != "0") extIds["tmdb"] = trTmdb;
            if (TryString(traktIdsObj, "imdb") is { Length: > 0 } trImdb) extIds["imdb"] = trImdb;
        }

        if (TryString(obj, "slug") is { Length: > 0 } directSlug)
        {
            extIds["slug"] = directSlug;
        }

        var tmdbId = TryString(obj, "TmdbId") ?? TryString(obj, "tmdbId");
        var tvdbId = TryString(obj, "TvdbId") ?? TryString(obj, "tvdbId");
        var imdbId = TryString(obj, "ImdbId") ?? TryString(obj, "imdbId");
        var tvMazeId = TryString(obj, "tvMazeId");
        if (!string.IsNullOrWhiteSpace(tmdbId) && tmdbId != "0") extIds["tmdb"] = tmdbId;
        if (!string.IsNullOrWhiteSpace(tvdbId) && tvdbId != "0") extIds["tvdb"] = tvdbId;
        if (!string.IsNullOrWhiteSpace(imdbId)) extIds["imdb"] = imdbId;
        if (!string.IsNullOrWhiteSpace(tvMazeId) && tvMazeId != "0") extIds["tvmaze"] = tvMazeId;

        ExtractFirstIntArrayId(obj, "malIds", "mal", extIds);
        ExtractFirstIntArrayId(obj, "anidbIds", "anidb", extIds);
        ExtractFirstIntArrayId(obj, "aniListIds", "anilist", extIds);

        if (obj.TryGetProperty("externalIds", out var extObj) && extObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in extObj.EnumerateObject())
            {
                var sv = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
                if (!string.IsNullOrWhiteSpace(sv))
                {
                    extIds[p.Name.ToLowerInvariant()] = sv;
                }
            }
        }

        if (obj.TryGetProperty("external_ids", out var tmdbExtObj) && tmdbExtObj.ValueKind == JsonValueKind.Object)
        {
            if (TryString(tmdbExtObj, "imdb_id") is { Length: > 0 } tImdb) extIds.TryAdd("imdb", tImdb);
            if (TryString(tmdbExtObj, "tvdb_id") is { Length: > 0 } tTvdb && tTvdb != "0") extIds.TryAdd("tvdb", tTvdb);
        }

        if (obj.TryGetProperty("externals", out var tvMazeExt) && tvMazeExt.ValueKind == JsonValueKind.Object)
        {
            var tvdb = TryString(tvMazeExt, "thetvdb");
            var imdb = TryString(tvMazeExt, "imdb");
            if (!string.IsNullOrWhiteSpace(tvdb)) extIds["tvdb"] = tvdb;
            if (!string.IsNullOrWhiteSpace(imdb)) extIds["imdb"] = imdb;
        }

        var asin = TryString(obj, "asin");
        if (!string.IsNullOrWhiteSpace(asin))
        {
            extIds["asin"] = asin;
        }

        if (obj.TryGetProperty("isbn", out var isbnArr) && isbnArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var isbnEl in isbnArr.EnumerateArray())
            {
                var s = isbnEl.ValueKind == JsonValueKind.String ? isbnEl.GetString()?.Trim() : null;
                if (s is { Length: 13 } && s.All(char.IsDigit))
                {
                    extIds["isbn13"] = s;
                    break;
                }
            }
        }

        if (hasVolumeInfo && vi.TryGetProperty("industryIdentifiers", out var indArr) && indArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var ident in indArr.EnumerateArray())
            {
                if (string.Equals(TryString(ident, "type"), "ISBN_13", StringComparison.OrdinalIgnoreCase) &&
                    TryString(ident, "identifier") is { Length: > 0 } isbn13)
                {
                    extIds["isbn13"] = isbn13;
                    break;
                }
            }
        }

        var overview = CleanDescription(
            TryString(obj, "overview")
            ?? TryString(obj, "Overview")
            ?? TryString(obj, "summary")
            ?? TryString(obj, "description")
            ?? (hasVolumeInfo ? TryString(vi, "description") : null));

        if (string.IsNullOrWhiteSpace(overview) && !string.IsNullOrWhiteSpace(creator))
        {
            var yrSuffix = year.HasValue ? $" ({year.Value})" : "";
            overview = vertical == MetadataVerticals.Music
                ? $"{TryString(obj, "primary-type") ?? "Album"} by {creator}{yrSuffix}."
                : $"By {creator}{yrSuffix}.";
        }

        var poster = ExtractPosterUrl(obj, hasVolumeInfo ? vi : default, id);
        var status = (TryString(obj, "status") ?? TryString(obj, "Status") ?? "released").ToLowerInvariant();
        var genres = ExtractGenres(obj, hasVolumeInfo ? vi : default);
        var formatHint = TryString(obj, "format") ?? TryString(obj, "seriesType") ?? TryString(obj, "SeriesType") ?? TryString(obj, "type");
        var containers = ParseContainersOrDefault(obj, vertical, title, year);
        var popularity = ExtractPopularity(obj, hasVolumeInfo ? vi : default);

        var candidate = LatinTitleNormalizer.NormalizeCandidate(new MetadataWorkCandidate(
            ProviderKey: descriptor.Key,
            ProviderItemId: id,
            Vertical: vertical,
            Title: title,
            AltTitles: LatinTitleNormalizer.OrderAndCleanAltTitles(title, altTitles),
            OriginalLanguage: lang,
            Year: year,
            Status: status,
            Overview: overview,
            PosterUrl: poster,
            ExternalIds: extIds,
            Containers: containers,
            Genres: genres,
            Popularity: popularity));

        if (candidate.AltTitles.Count > 60)
        {
            candidate = candidate with { AltTitles = candidate.AltTitles.Take(60).ToArray() };
        }

        return MediaTaxonomyClassifier.EnrichCandidate(candidate, formatHint: formatHint);
    }

    private static double ExtractPopularity(JsonElement obj, JsonElement volumeInfo)
    {
        var maxSignal = 0.0;

        foreach (var prop in new[]
                 {
                     "popularity", "Popularity",
                     "vote_count", "VoteCount", "votes", "Votes",
                     "weight", "Weight",
                     "users_count", "want_to_read_count", "readinglog_count",
                     "ratings_count", "ratingsCount",
                     "nb_fan", "fans", "followedCount", "follows",
                 })
        {
            if (TryDouble(obj, prop) is { } val && val > maxSignal)
            {
                maxSignal = val;
            }
        }

        foreach (var ratingObjName in new[] { "rating", "Rating", "ratings", "Ratings" })
        {
            if (obj.TryGetProperty(ratingObjName, out var rObj) && rObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var voteProp in new[] { "votes", "Votes", "count", "Count", "popularity", "Popularity" })
                {
                    if (TryDouble(rObj, voteProp) is { } rVal && rVal > maxSignal)
                    {
                        maxSignal = rVal;
                    }
                }

                foreach (var child in rObj.EnumerateObject())
                {
                    if (child.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var voteProp in new[] { "votes", "Votes", "count", "Count" })
                        {
                            if (TryDouble(child.Value, voteProp) is { } cVal && cVal > maxSignal)
                            {
                                maxSignal = cVal;
                            }
                        }
                    }
                }
            }
        }

        if (volumeInfo.ValueKind == JsonValueKind.Object &&
            TryDouble(volumeInfo, "ratingsCount") is { } viCount &&
            viCount > maxSignal)
        {
            maxSignal = viCount;
        }

        if (maxSignal <= 0 && TryDouble(obj, "edition_count") is { } edCount && edCount > 0)
        {
            maxSignal = edCount;
        }

        return Math.Round(maxSignal, 2);
    }

    private static IReadOnlyList<string> ExtractGenres(JsonElement obj, JsonElement volumeInfo)
    {
        var list = new List<string>();
        foreach (var prop in new[] { "genres", "Genres", "categories", "Categories", "tags", "Tags" })
        {
            if (obj.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var g = el.ValueKind switch
                    {
                        JsonValueKind.String => el.GetString(),
                        JsonValueKind.Object => TryString(el, "name") ?? TryString(el, "Name") ?? TryString(el, "title"),
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(g))
                    {
                        list.Add(g.Trim());
                    }
                }
            }
        }

        if (volumeInfo.ValueKind == JsonValueKind.Object &&
            volumeInfo.TryGetProperty("categories", out var catArr) &&
            catArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in catArr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } c)
                {
                    list.Add(c.Trim());
                }
            }
        }

        // TMDB genre_ids where 16 = Animation
        if (obj.TryGetProperty("genre_ids", out var genreIds) && genreIds.ValueKind == JsonValueKind.Array)
        {
            foreach (var gid in genreIds.EnumerateArray())
            {
                if (gid.ValueKind == JsonValueKind.Number && gid.TryGetInt32(out var idNum) && idNum == 16)
                {
                    list.Add("Animation");
                }
            }
        }

        // TVMaze show.type == "Animation" or SkyHook seriesType == "anime"
        var showType = TryString(obj, "type") ?? TryString(obj, "seriesType") ?? TryString(obj, "SeriesType");
        if (string.Equals(showType, "Animation", StringComparison.OrdinalIgnoreCase))
        {
            list.Add("Animation");
        }
        else if (string.Equals(showType, "anime", StringComparison.OrdinalIgnoreCase))
        {
            list.Add("Anime");
            list.Add("Animation");
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void HarvestStructuredTitles(
        JsonElement obj,
        string originalLanguage,
        out List<string> englishTitles,
        out List<string> latinTitles,
        out List<string> tier1Titles,
        out List<string> tier2Titles)
    {
        var eng = new List<string>();
        var lat = new List<string>();
        var t1 = new List<string>();
        var t2 = new List<string>();

        void ClassifyLocalizedTitle(string? titleVal, string? langCode, bool forceTier1 = false)
        {
            if (string.IsNullOrWhiteSpace(titleVal)) return;
            var trimmed = titleVal.Trim();
            if (!HasUsableTitleChars(trimmed)) return;

            var cleanLang = (langCode ?? "").Trim().ToLowerInvariant();
            var isCleanLatin = LatinTitleNormalizer.IsCleanLatinText(trimmed);

            if ((cleanLang is "eng" || cleanLang.StartsWith("en", StringComparison.Ordinal)) && isCleanLatin)
            {
                eng.Add(trimmed);
            }
            else if (IsRomajiLocale(cleanLang) && isCleanLatin)
            {
                lat.Add(trimmed);
            }

            if (forceTier1 || IsTier1Language(cleanLang, originalLanguage))
            {
                t1.Add(trimmed);
            }
            else
            {
                t2.Add(trimmed);
            }
        }

        // 1. MangaDex attributes.title + attributes.altTitles
        if (obj.TryGetProperty("attributes", out var attr) && attr.ValueKind == JsonValueKind.Object)
        {
            if (attr.TryGetProperty("title", out var mdTitle) && mdTitle.ValueKind == JsonValueKind.Object)
            {
                CollectLocaleObjectStrings(mdTitle, originalLanguage, eng, lat, t1, t2);
            }

            if (attr.TryGetProperty("altTitles", out var mdAlts) && mdAlts.ValueKind == JsonValueKind.Array)
            {
                foreach (var altObj in mdAlts.EnumerateArray())
                {
                    if (altObj.ValueKind == JsonValueKind.Object)
                    {
                        CollectLocaleObjectStrings(altObj, originalLanguage, eng, lat, t1, t2);
                    }
                }
            }
        }

        // 2. MusicBrainz / TVDB aliases: [{ name, sort-name, locale, language }]
        if (obj.TryGetProperty("aliases", out var aliasesArr) && aliasesArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var al in aliasesArr.EnumerateArray())
            {
                if (al.ValueKind == JsonValueKind.String && al.GetString() is { Length: > 0 } plainAlias)
                {
                    if (LatinTitleNormalizer.IsCleanLatinText(plainAlias)) lat.Add(plainAlias.Trim());
                    t1.Add(plainAlias.Trim());
                    continue;
                }

                if (al.ValueKind != JsonValueKind.Object) continue;
                var alName = TryString(al, "name") ?? TryString(al, "title");
                var locale = TryString(al, "locale") ?? TryString(al, "language") ?? "";
                ClassifyLocalizedTitle(alName, locale, forceTier1: true);

                if (TryString(al, "sort-name") is { Length: > 0 } sortAlias && LatinTitleNormalizer.IsCleanLatinText(sortAlias))
                {
                    lat.Add(sortAlias);
                    t1.Add(sortAlias);
                }
            }
        }

        // 3. MusicBrainz release-group releases[] (captures official English Pseudo-Releases & Latin editions!)
        if (obj.TryGetProperty("releases", out var relArr) && relArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var rel in relArr.EnumerateArray())
            {
                if (rel.ValueKind != JsonValueKind.Object) continue;
                var relTitle = TryString(rel, "title");
                if (string.IsNullOrWhiteSpace(relTitle)) continue;

                var relStatus = TryString(rel, "status") ?? "";
                var script = rel.TryGetProperty("text-representation", out var tr) && tr.ValueKind == JsonValueKind.Object
                    ? TryString(tr, "script") ?? ""
                    : "";

                if (string.Equals(relStatus, "Pseudo-Release", StringComparison.OrdinalIgnoreCase) &&
                    LatinTitleNormalizer.IsCleanLatinText(relTitle))
                {
                    eng.Add(relTitle);
                    t1.Add(relTitle);
                }
                else if (string.Equals(script, "Latn", StringComparison.OrdinalIgnoreCase) ||
                         LatinTitleNormalizer.IsCleanLatinText(relTitle))
                {
                    lat.Add(relTitle);
                    t1.Add(relTitle);
                }
                else
                {
                    t2.Add(relTitle);
                }
            }
        }

        // 4. TMDB alternative_titles: { titles: [...] } or { results: [...] }
        if (obj.TryGetProperty("alternative_titles", out var tmdbAltObj) && tmdbAltObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var arrKey in new[] { "titles", "results" })
            {
                if (tmdbAltObj.TryGetProperty(arrKey, out var tArr) && tArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tItem in tArr.EnumerateArray())
                    {
                        if (tItem.ValueKind != JsonValueKind.Object) continue;
                        var tVal = TryString(tItem, "title") ?? TryString(tItem, "name");
                        if (string.IsNullOrWhiteSpace(tVal)) continue;
                        var iso = TryString(tItem, "iso_3166_1") ?? "";
                        if (iso is "US" or "GB" or "AU" or "CA" && LatinTitleNormalizer.IsCleanLatinText(tVal))
                        {
                            eng.Add(tVal.Trim());
                        }
                        else if (LatinTitleNormalizer.IsCleanLatinText(tVal))
                        {
                            lat.Add(tVal.Trim());
                        }
                        t1.Add(tVal.Trim());
                    }
                }
            }
        }

        // 5. Radarr / Sonarr SkyHook Translations / translations, TMDB v3 translations, and TVDB v4 translations
        foreach (var transProp in new[] { "Translations", "translations" })
        {
            if (!obj.TryGetProperty(transProp, out var transNode)) continue;

            if (transNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var tItem in transNode.EnumerateArray())
                {
                    if (tItem.ValueKind != JsonValueKind.Object) continue;

                    // TMDB v3 translation item: { iso_639_1, iso_3166_1, data: { title, name } }
                    if (tItem.TryGetProperty("data", out var dataObj) && dataObj.ValueKind == JsonValueKind.Object)
                    {
                        var tmdbTitle = TryString(dataObj, "title") ?? TryString(dataObj, "name");
                        var iso639 = TryString(tItem, "iso_639_1") ?? ExtractTranslationLang(tItem);
                        ClassifyLocalizedTitle(tmdbTitle, iso639);
                        continue;
                    }

                    // SkyHook translation item: { Title/title/name, Language/language }
                    var skyTitle = TryString(tItem, "Title") ?? TryString(tItem, "title") ?? TryString(tItem, "name");
                    var skyLang = ExtractTranslationLang(tItem);
                    ClassifyLocalizedTitle(skyTitle, skyLang);
                }
            }
            else if (transNode.ValueKind == JsonValueKind.Object)
            {
                // TMDB v3 append_to_response=translations envelope: { translations: [ ... ] }
                if (transNode.TryGetProperty("translations", out var innerArr) && innerArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tItem in innerArr.EnumerateArray())
                    {
                        if (tItem.ValueKind != JsonValueKind.Object) continue;
                        var tmdbTitle = tItem.TryGetProperty("data", out var dataObj) && dataObj.ValueKind == JsonValueKind.Object
                            ? (TryString(dataObj, "title") ?? TryString(dataObj, "name"))
                            : (TryString(tItem, "title") ?? TryString(tItem, "name"));
                        var iso639 = TryString(tItem, "iso_639_1") ?? ExtractTranslationLang(tItem);
                        ClassifyLocalizedTitle(tmdbTitle, iso639);
                    }
                }

                // TVDB v4 translations envelope: { nameTranslations: [{ name, language }] }
                if (transNode.TryGetProperty("nameTranslations", out var tvdbTransArr) && tvdbTransArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tItem in tvdbTransArr.EnumerateArray())
                    {
                        if (tItem.ValueKind != JsonValueKind.Object) continue;
                        var tvdbTitle = TryString(tItem, "name") ?? TryString(tItem, "title");
                        var tvdbLang = TryString(tItem, "language") ?? TryString(tItem, "locale");
                        ClassifyLocalizedTitle(tvdbTitle, tvdbLang);
                    }
                }
            }
        }

        // 6. Top-level TVDB v4 nameTranslations array: [{ name, language }]
        if (obj.TryGetProperty("nameTranslations", out var topNameTrans) && topNameTrans.ValueKind == JsonValueKind.Array)
        {
            foreach (var tItem in topNameTrans.EnumerateArray())
            {
                if (tItem.ValueKind != JsonValueKind.Object) continue;
                var tvdbTitle = TryString(tItem, "name") ?? TryString(tItem, "title");
                var tvdbLang = TryString(tItem, "language") ?? TryString(tItem, "locale");
                ClassifyLocalizedTitle(tvdbTitle, tvdbLang);
            }
        }

        englishTitles = eng;
        latinTitles = lat;
        tier1Titles = t1;
        tier2Titles = t2;
    }

    private static string? ExtractTranslationLang(JsonElement item)
    {
        foreach (var key in new[] { "Language", "language", "iso_639_1", "locale" })
        {
            if (!item.TryGetProperty(key, out var prop)) continue;
            if (prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }

            if (prop.ValueKind == JsonValueKind.Object)
            {
                return TryString(prop, "iso_639_1")
                    ?? TryString(prop, "code")
                    ?? TryString(prop, "Name")
                    ?? TryString(prop, "name");
            }
        }

        return null;
    }

    private static bool HasUsableTitleChars(string title) =>
        title.Count(char.IsAsciiLetterOrDigit) >= 2 || title.Count(char.IsLetter) >= 2;

    private static bool IsRomajiLocale(string? langCode)
    {
        if (string.IsNullOrWhiteSpace(langCode)) return false;
        var norm = langCode.Trim().ToLowerInvariant();
        return norm.EndsWith("-ro", StringComparison.Ordinal) || norm is "ja-latn" or "x-jat";
    }

    private static bool IsTier1Language(string? langCode, string originalLanguage)
    {
        if (string.IsNullOrWhiteSpace(langCode)) return false;
        var norm = langCode.Trim().ToLowerInvariant();
        if (IsRomajiLocale(norm)) return true;

        var baseToken = norm.Split('-', '_')[0];
        if (baseToken is "en" or "eng" or "de" or "deu" or "ger")
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(originalLanguage) &&
               string.Equals(NormalizeLanguageCode(baseToken), originalLanguage, StringComparison.OrdinalIgnoreCase);
    }

    private static void CollectLocaleObjectStrings(
        JsonElement localeObj,
        string originalLanguage,
        List<string> englishTitles,
        List<string> latinTitles,
        List<string> tier1Titles,
        List<string> tier2Titles)
    {
        foreach (var prop in localeObj.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String) continue;
            var val = prop.Value.GetString();
            if (string.IsNullOrWhiteSpace(val)) continue;
            var trimmed = val.Trim();
            if (!HasUsableTitleChars(trimmed)) continue;

            var key = prop.Name.ToLowerInvariant();
            if (key.StartsWith("en", StringComparison.Ordinal) && LatinTitleNormalizer.IsCleanLatinText(trimmed))
            {
                englishTitles.Add(trimmed);
            }
            else if ((IsRomajiLocale(key) || LatinTitleNormalizer.IsCleanLatinText(trimmed)) &&
                     LatinTitleNormalizer.IsCleanLatinText(trimmed))
            {
                latinTitles.Add(trimmed);
            }

            if (IsTier1Language(key, originalLanguage))
            {
                tier1Titles.Add(trimmed);
            }
            else
            {
                tier2Titles.Add(trimmed);
            }
        }
    }

    private string? ExtractPosterUrl(JsonElement obj, JsonElement volumeInfo, string itemId)
    {
        if (TryString(obj, "poster_path") is { } pp && pp.StartsWith('/'))
        {
            return $"https://image.tmdb.org/t/p/w500{pp}";
        }

        if (TryString(obj, "posterUrl") is { Length: > 0 } directPoster)
        {
            return directPoster;
        }

        // Radarr / Sonarr SkyHook Images array: [{ CoverType/coverType: "Poster", RemoteUrl/Url/url: "..." }]
        foreach (var imgArrProp in new[] { "Images", "images" })
        {
            if (obj.TryGetProperty(imgArrProp, out var imgArr) && imgArr.ValueKind == JsonValueKind.Array)
            {
                string? fallbackImg = null;
                foreach (var img in imgArr.EnumerateArray())
                {
                    if (img.ValueKind != JsonValueKind.Object) continue;
                    var coverType = TryString(img, "CoverType") ?? TryString(img, "coverType") ?? "";
                    var url = TryString(img, "RemoteUrl") ?? TryString(img, "remoteUrl") ?? TryString(img, "Url") ?? TryString(img, "url");
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    fallbackImg ??= url;
                    if (string.Equals(coverType, "Poster", StringComparison.OrdinalIgnoreCase))
                    {
                        return url.Replace("image.tmdb.org/t/p/original/", "image.tmdb.org/t/p/w500/", StringComparison.Ordinal);
                    }
                }

                if (fallbackImg is not null)
                {
                    return fallbackImg.Replace("image.tmdb.org/t/p/original/", "image.tmdb.org/t/p/w500/", StringComparison.Ordinal);
                }
            }
        }

        // OpenLibrary cover_i
        if (TryInt(obj, "cover_i") is > 0 and var coverId)
        {
            return $"https://covers.openlibrary.org/b/id/{coverId}-L.jpg";
        }

        // Deezer album art
        if ((TryString(obj, "cover_big") ?? TryString(obj, "cover_medium")) is { Length: > 0 } deezerCover)
        {
            return deezerCover;
        }

        // iTunes / Apple Books artwork
        if (TryString(obj, "artworkUrl100") is { Length: > 0 } iTunesArt)
        {
            return iTunesArt.Replace("100x100bb", "600x600bb", StringComparison.Ordinal);
        }

        // TVMaze / Hardcover image object or plain string
        if (obj.TryGetProperty("image", out var imgProp))
        {
            if (imgProp.ValueKind == JsonValueKind.String && imgProp.GetString() is { Length: > 0 } sImg)
            {
                return sImg;
            }

            if (imgProp.ValueKind == JsonValueKind.Object)
            {
                var tvMazeImg = TryString(imgProp, "original")
                    ?? TryString(imgProp, "medium")
                    ?? TryString(imgProp, "url");
                if (!string.IsNullOrWhiteSpace(tvMazeImg))
                {
                    return tvMazeImg;
                }
            }
        }

        // Google Books imageLinks
        if (volumeInfo.ValueKind == JsonValueKind.Object &&
            volumeInfo.TryGetProperty("imageLinks", out var il) &&
            il.ValueKind == JsonValueKind.Object)
        {
            var thumb = TryString(il, "thumbnail") ?? TryString(il, "smallThumbnail");
            if (!string.IsNullOrWhiteSpace(thumb))
            {
                return thumb.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
            }
        }

        // MusicBrainz CoverArtArchive release-group front cover
        if (descriptor.Key == "musicbrainz" && Guid.TryParse(itemId, out _))
        {
            return $"https://coverartarchive.org/release-group/{itemId}/front-250";
        }

        return null;
    }

    private static string? ExtractCreatorOrArtist(JsonElement obj, JsonElement volumeInfo)
    {
        if (obj.TryGetProperty("artist-credit", out var mbArtists) && mbArtists.ValueKind == JsonValueKind.Array)
        {
            var first = mbArtists.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
            {
                var engArtistCandidates = new List<string?>();
                var latArtistCandidates = new List<string?>();
                var rawArtistCandidates = new List<string?>();

                var creditName = TryString(first, "name");
                rawArtistCandidates.Add(creditName);
                if (LatinTitleNormalizer.IsCleanLatinText(creditName))
                {
                    engArtistCandidates.Add(creditName);
                }

                if (first.TryGetProperty("artist", out var inner) && inner.ValueKind == JsonValueKind.Object)
                {
                    var innerName = TryString(inner, "name");
                    rawArtistCandidates.Add(innerName);
                    if (LatinTitleNormalizer.IsCleanLatinText(innerName))
                    {
                        engArtistCandidates.Add(innerName);
                    }

                    if (inner.TryGetProperty("aliases", out var artistAliases) && artistAliases.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var al in artistAliases.EnumerateArray())
                        {
                            if (al.ValueKind != JsonValueKind.Object) continue;
                            var alName = TryString(al, "name");
                            var locale = TryString(al, "locale") ?? "";
                            if (string.IsNullOrWhiteSpace(alName)) continue;
                            if (locale.StartsWith("en", StringComparison.OrdinalIgnoreCase) && LatinTitleNormalizer.IsCleanLatinText(alName))
                            {
                                engArtistCandidates.Add(alName);
                            }
                            else if (LatinTitleNormalizer.IsCleanLatinText(alName))
                            {
                                latArtistCandidates.Add(alName);
                            }
                        }
                    }

                    if (LatinTitleNormalizer.NormalizeMusicBrainzSortName(TryString(inner, "sort-name")) is { Length: > 0 } normSort)
                    {
                        latArtistCandidates.Add(normSort);
                    }
                }

                var resolvedArtist = LatinTitleNormalizer.SelectBestEnglishOrLatinTitle(
                    engArtistCandidates,
                    latArtistCandidates,
                    rawArtistCandidates,
                    fallbackLabel: "");
                if (!string.IsNullOrWhiteSpace(resolvedArtist))
                {
                    return resolvedArtist;
                }
            }
        }

        string? rawCreator = null;
        if (obj.TryGetProperty("artist", out var dzArtist) && dzArtist.ValueKind == JsonValueKind.Object)
        {
            rawCreator = TryString(dzArtist, "name");
        }
        else if (TryString(obj, "artistName") is { Length: > 0 } itunesArtist)
        {
            rawCreator = itunesArtist;
        }
        else if (obj.TryGetProperty("author_name", out var olAuthors) && olAuthors.ValueKind == JsonValueKind.Array)
        {
            var first = olAuthors.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.String);
            if (first.ValueKind == JsonValueKind.String) rawCreator = first.GetString();
        }
        else if (obj.TryGetProperty("authors", out var audAuthors) && audAuthors.ValueKind == JsonValueKind.Array)
        {
            var first = audAuthors.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && TryString(first, "name") is { Length: > 0 } an) rawCreator = an;
            else if (first.ValueKind == JsonValueKind.String) rawCreator = first.GetString();
        }
        else if (obj.TryGetProperty("contributions", out var hcContribs) && hcContribs.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in hcContribs.EnumerateArray())
            {
                if (c.ValueKind == JsonValueKind.Object &&
                    c.TryGetProperty("author", out var authObj) &&
                    authObj.ValueKind == JsonValueKind.Object &&
                    TryString(authObj, "name") is { Length: > 0 } hcAuthor)
                {
                    rawCreator = hcAuthor;
                    break;
                }
            }
        }
        else if (volumeInfo.ValueKind == JsonValueKind.Object &&
                 volumeInfo.TryGetProperty("authors", out var gbAuthors) &&
                 gbAuthors.ValueKind == JsonValueKind.Array)
        {
            var first = gbAuthors.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.String);
            if (first.ValueKind == JsonValueKind.String) rawCreator = first.GetString();
        }

        if (string.IsNullOrWhiteSpace(rawCreator))
        {
            return null;
        }

        var cleanCreator = LatinTitleNormalizer.SelectBestEnglishOrLatinTitle(
            LatinTitleNormalizer.IsCleanLatinText(rawCreator) ? [rawCreator] : [],
            [],
            [rawCreator],
            fallbackLabel: "");
        return string.IsNullOrWhiteSpace(cleanCreator) ? null : cleanCreator;
    }

    private static string ExtractNormalizedLanguage(JsonElement obj, JsonElement volumeInfo, string? rawTitleHint = null)
    {
        string? raw = TryString(obj, "original_language")
            ?? TryString(obj, "originalLanguage")
            ?? TryString(obj, "language")
            ?? (volumeInfo.ValueKind == JsonValueKind.Object ? TryString(volumeInfo, "language") : null);

        if (string.IsNullOrWhiteSpace(raw) &&
            obj.TryGetProperty("text-representation", out var mbTextRep) &&
            mbTextRep.ValueKind == JsonValueKind.Object)
        {
            raw = TryString(mbTextRep, "language");
        }

        if (string.IsNullOrWhiteSpace(raw) &&
            obj.TryGetProperty("OriginalLanguage", out var radarrLang))
        {
            raw = radarrLang.ValueKind switch
            {
                JsonValueKind.String => radarrLang.GetString(),
                JsonValueKind.Object => TryString(radarrLang, "Name") ?? TryString(radarrLang, "name"),
                _ => null,
            };
        }

        if (string.IsNullOrWhiteSpace(raw) &&
            obj.TryGetProperty("language", out var olLangArr) &&
            olLangArr.ValueKind == JsonValueKind.Array)
        {
            var first = olLangArr.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.String);
            if (first.ValueKind == JsonValueKind.String) raw = first.GetString();
        }

        if (string.IsNullOrWhiteSpace(raw) &&
            obj.TryGetProperty("releases", out var relArr) &&
            relArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var rel in relArr.EnumerateArray())
            {
                if (rel.ValueKind == JsonValueKind.Object &&
                    rel.TryGetProperty("text-representation", out var tr) &&
                    tr.ValueKind == JsonValueKind.Object &&
                    TryString(tr, "language") is { Length: > 0 } relLang &&
                    !string.Equals(relLang, "mul", StringComparison.OrdinalIgnoreCase))
                {
                    raw = relLang;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = LatinTitleNormalizer.InferLanguageFromScript(rawTitleHint, TryString(obj, "country"));
        }

        return NormalizeLanguageCode(raw);
    }

    private static string NormalizeLanguageCode(string? raw)
    {
        var norm = (raw ?? "en").Trim().Split('-', '_')[0].ToLowerInvariant();
        return norm switch
        {
            "eng" or "english" => "en",
            "deu" or "ger" or "german" or "deutsch" => "de",
            "jpn" or "japanese" => "ja",
            "fra" or "fre" or "french" => "fr",
            "spa" or "spanish" => "es",
            "ita" or "italian" => "it",
            "kor" or "korean" => "ko",
            "zho" or "chi" or "chinese" => "zh",
            "rus" or "russian" => "ru",
            "por" or "portuguese" => "pt",
            "nld" or "dut" or "dutch" => "nl",
            "pol" or "polish" => "pl",
            "swe" or "swedish" => "sv",
            "nor" or "nob" or "nno" or "norwegian" => "no",
            "dan" or "danish" => "da",
            "fin" or "finnish" => "fi",
            "tur" or "turkish" => "tr",
            "ara" or "arabic" => "ar",
            "heb" or "hebrew" => "he",
            "ell" or "gre" or "greek" => "el",
            "tha" or "thai" => "th",
            "hin" or "hindi" => "hi",
            "ces" or "cze" or "czech" => "cs",
            "hun" or "hungarian" => "hu",
            "ron" or "rum" or "romanian" => "ro",
            "ukr" or "ukrainian" => "uk",
            _ => norm.Length is >= 2 and <= 3 ? norm : "en",
        };
    }

    private static void ExtractFirstIntArrayId(
        JsonElement obj,
        string propName,
        string targetKey,
        Dictionary<string, string> extIds)
    {
        if (obj.TryGetProperty(propName, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var first = arr.EnumerateArray().FirstOrDefault();
            if (first.ValueKind is JsonValueKind.Number or JsonValueKind.String)
            {
                var s = first.ToString();
                if (!string.IsNullOrWhiteSpace(s) && s != "0")
                {
                    extIds[targetKey] = s;
                }
            }
        }
    }

    private static string CleanDescription(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        var withoutTags = HtmlTagRegex().Replace(raw, " ");
        var decoded = HttpUtility.HtmlDecode(withoutTags);
        return string.Join(' ', decoded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static IReadOnlyList<MetadataContainerSeed> ParseContainersOrDefault(
        JsonElement obj,
        string vertical,
        string title,
        int? year)
    {
        // 1. Explicit webhook/sidecar containers array
        if (obj.TryGetProperty("containers", out var cArr) && cArr.ValueKind == JsonValueKind.Array)
        {
            var parsed = new List<MetadataContainerSeed>();
            foreach (var c in cArr.EnumerateArray())
            {
                var units = new List<MetadataUnitSeed>();
                if (c.TryGetProperty("units", out var uArr) && uArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var u in uArr.EnumerateArray())
                    {
                        units.Add(new MetadataUnitSeed(
                            UnitKind: TryString(u, "unitKind") ?? "episode",
                            UnitNumber: TryInt(u, "unitNumber") ?? 1,
                            AbsoluteNumber: TryInt(u, "absoluteNumber"),
                            Title: TryString(u, "title") ?? "Unit 1",
                            AirOrReleaseDate: TryString(u, "airOrReleaseDate")));
                    }
                }

                parsed.Add(new MetadataContainerSeed(
                    ContainerKind: TryString(c, "containerKind") ?? "season",
                    ContainerNumber: TryInt(c, "containerNumber") ?? 1,
                    Title: TryString(c, "title") ?? "Season 1",
                    ReleaseDate: TryString(c, "releaseDate"),
                    Units: units));
            }

            if (parsed.Count > 0)
            {
                return parsed;
            }
        }

        // 2. Sonarr SkyHook seasons + episodes
        if (vertical is MetadataVerticals.Tv or MetadataVerticals.Anime &&
            obj.TryGetProperty("seasons", out var seasonsArr) &&
            seasonsArr.ValueKind == JsonValueKind.Array)
        {
            var episodesBySeason = new Dictionary<int, List<MetadataUnitSeed>>();
            if (obj.TryGetProperty("episodes", out var epArr) && epArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var ep in epArr.EnumerateArray())
                {
                    if (ep.ValueKind != JsonValueKind.Object) continue;
                    var sNum = TryInt(ep, "seasonNumber") ?? 0;
                    if (sNum < 0) continue;
                    var eNum = TryInt(ep, "episodeNumber") ?? 1;
                    var absNum = sNum == 0 ? null : TryInt(ep, "absoluteEpisodeNumber");
                    var epTitle = TryString(ep, "title") ?? (sNum == 0 ? $"Special {eNum}" : $"Episode {eNum}");
                    var airDate = TryString(ep, "airDate");
                    if (!episodesBySeason.TryGetValue(sNum, out var list))
                    {
                        list = [];
                        episodesBySeason[sNum] = list;
                    }

                    list.Add(new MetadataUnitSeed("episode", eNum, absNum, epTitle, airDate));
                }
            }

            var seasonSeeds = new List<MetadataContainerSeed>();
            foreach (var s in seasonsArr.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                var sNum = TryInt(s, "seasonNumber") ?? TryInt(s, "season_number") ?? 0;
                if (sNum < 0) continue;

                var sYear = TryInt(s, "year") ?? year;
                var relDate = sYear.HasValue ? $"{sYear.Value}-01-01" : null;
                var airDate = TryString(s, "air_date") ?? relDate;

                List<MetadataUnitSeed> units;
                if (episodesBySeason.TryGetValue(sNum, out var eps) && eps.Count > 0)
                {
                    units = eps.OrderBy(u => u.UnitNumber).ToList();
                }
                else
                {
                    var epCount = TryInt(s, "episode_count") ?? TryInt(s, "episodeCount");
                    if (sNum == 0 && (epCount is null or <= 0))
                    {
                        continue;
                    }

                    var count = Math.Clamp(epCount ?? 1, 1, 2000);
                    units = new List<MetadataUnitSeed>(count);
                    for (var i = 1; i <= count; i++)
                    {
                        var epTitle = epCount is null
                            ? (sNum == 0 ? "TBA / Special 1" : "TBA / Episode 1")
                            : (sNum == 0 ? $"Special {i}" : $"Episode {i}");
                        units.Add(new MetadataUnitSeed("episode", i, null, epTitle, airDate));
                    }
                }

                seasonSeeds.Add(new MetadataContainerSeed(
                    ContainerKind: "season",
                    ContainerNumber: sNum,
                    Title: sNum == 0 ? "Specials" : $"Season {sNum}",
                    ReleaseDate: units[0].AirOrReleaseDate ?? airDate,
                    Units: units));
            }

            if (episodesBySeason.TryGetValue(0, out var extraSpecials) &&
                extraSpecials.Count > 0 &&
                seasonSeeds.All(s => s.ContainerNumber != 0))
            {
                var orderedSpecials = extraSpecials.OrderBy(u => u.UnitNumber).ToList();
                seasonSeeds.Add(new MetadataContainerSeed(
                    ContainerKind: "season",
                    ContainerNumber: 0,
                    Title: "Specials",
                    ReleaseDate: orderedSpecials[0].AirOrReleaseDate,
                    Units: orderedSpecials));
            }

            if (seasonSeeds.Count > 0)
            {
                return seasonSeeds.OrderBy(s => s.ContainerNumber).ToList();
            }
        }

        // 3. TVMaze _embedded.episodes
        if (vertical is MetadataVerticals.Tv or MetadataVerticals.Anime &&
            obj.TryGetProperty("_embedded", out var emb) &&
            emb.ValueKind == JsonValueKind.Object &&
            emb.TryGetProperty("episodes", out var tvmEps) &&
            tvmEps.ValueKind == JsonValueKind.Array)
        {
            var grouped = new SortedDictionary<int, List<MetadataUnitSeed>>();
            foreach (var ep in tvmEps.EnumerateArray())
            {
                var sNum = TryInt(ep, "season") ?? 0;
                if (sNum < 0) continue;
                var eNum = TryInt(ep, "number") ?? 1;
                if (!grouped.TryGetValue(sNum, out var list))
                {
                    list = [];
                    grouped[sNum] = list;
                }

                list.Add(new MetadataUnitSeed(
                    "episode",
                    eNum,
                    null,
                    TryString(ep, "name") ?? (sNum == 0 ? $"Special {eNum}" : $"Episode {eNum}"),
                    TryString(ep, "airdate")));
            }

            if (grouped.Count > 0)
            {
                return grouped
                    .Select(kv => new MetadataContainerSeed(
                        "season",
                        kv.Key,
                        kv.Key == 0 ? "Specials" : $"Season {kv.Key}",
                        kv.Value.FirstOrDefault()?.AirOrReleaseDate,
                        kv.Value))
                    .ToList();
            }
        }

        return DefaultContainersForVertical(vertical, title, year);
    }

    internal static IReadOnlyList<MetadataContainerSeed> DefaultContainersForVertical(
        string vertical,
        string title,
        int? year)
    {
        var date = year.HasValue ? $"{year.Value}-01-01" : null;
        return vertical switch
        {
            MetadataVerticals.Tv or MetadataVerticals.Anime =>
            [
                new MetadataContainerSeed(
                    "season",
                    1,
                    "Season 1",
                    date,
                    [new MetadataUnitSeed("episode", 1, 1, "Episode 1", date)]),
            ],
            MetadataVerticals.Music =>
            [
                new MetadataContainerSeed(
                    "album",
                    1,
                    title,
                    date,
                    [new MetadataUnitSeed("track", 1, 1, "Track 1", date)]),
            ],
            MetadataVerticals.Ebooks or MetadataVerticals.Audiobooks =>
            [
                new MetadataContainerSeed(
                    "book",
                    1,
                    title,
                    date,
                    [
                        new MetadataUnitSeed("ebook_slot", 1, null, $"{title} (Ebook)", date),
                        new MetadataUnitSeed("audiobook_slot", 2, null, $"{title} (Audiobook)", date),
                    ]),
            ],
            MetadataVerticals.Comics =>
            [
                new MetadataContainerSeed(
                    "volume",
                    1,
                    "Volume 1",
                    date,
                    [new MetadataUnitSeed("chapter", 1, 1, "Chapter 1", date)]),
            ],
            _ =>
            [
                new MetadataContainerSeed(
                    "feature_collection",
                    1,
                    title,
                    date,
                    [new MetadataUnitSeed("feature", 1, null, title, date)]),
            ],
        };
    }

    private MetadataWorkCandidate BuildSyntheticCandidate(string title, string vertical, string id) =>
        new(
            ProviderKey: descriptor.Key,
            ProviderItemId: id,
            Vertical: vertical,
            Title: title.Trim(),
            AltTitles: [],
            OriginalLanguage: "en",
            Year: null,
            Status: "released",
            Overview: $"Managed via {descriptor.DisplayName} without external catalog lock-in.",
            PosterUrl: null,
            ExternalIds: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [descriptor.Key] = id },
            Containers: DefaultContainersForVertical(vertical, title.Trim(), null));

    private static string? TryString(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el))
        {
            return null;
        }

        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.ToString(),
            _ => null,
        };
    }

    private static int? TryInt(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el))
        {
            return null;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
        {
            return n;
        }

        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static double? TryDouble(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el))
        {
            return null;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d))
        {
            return d;
        }

        if (el.ValueKind == JsonValueKind.String &&
            double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string Slugify(string value) =>
        string.Concat(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');
}
