using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml;
using System.Xml.Linq;
using Managerr.Indexers.Cardigann;
using Managerr.Indexers.Categories;

namespace Managerr.Indexers.Torznab;

public sealed class IndexerApiException(int? code, string description)
    : Exception($"Indexer API error{(code.HasValue ? $" {code.Value}" : "")}: {description}")
{
    public int? ErrorCode { get; } = code;
    public string Description { get; } = description;
}

public sealed record SearchCapability(bool Available, HashSet<string> SupportedParams)
{
    public bool Supports(string param) => Available && SupportedParams.Contains(param);
}

public sealed record IndexerCapabilities(
    SearchCapability Search,
    SearchCapability TvSearch,
    SearchCapability MovieSearch,
    SearchCapability MusicSearch,
    SearchCapability AudioSearch,
    SearchCapability BookSearch,
    IReadOnlySet<int> SupportedCategories)
{
    public bool SearchAvailable => Search.Available;
    public bool TvSearchAvailable => TvSearch.Available;
    public bool MovieSearchAvailable => MovieSearch.Available;
    public bool MusicSearchAvailable => MusicSearch.Available;
    public bool AudioSearchAvailable => AudioSearch.Available;
    public bool BookSearchAvailable => BookSearch.Available;

    public bool SupportsParam(string searchType, string paramName)
    {
        var cap = searchType.ToLowerInvariant() switch
        {
            "search" => Search,
            "tv-search" or "tvsearch" => TvSearch,
            "movie-search" or "moviesearch" => MovieSearch,
            "music-search" or "musicsearch" => MusicSearch,
            "audio-search" or "audiosearch" => AudioSearch,
            "book-search" or "booksearch" => BookSearch,
            _ => null,
        };
        return cap != null && cap.Supports(paramName);
    }
}

public sealed record IndexedReleaseCandidate(
    string Guid,
    long IndexerId,
    string IndexerName,
    string Protocol,
    string MediaVertical,
    string Title,
    string DownloadUrl,
    string? MagnetUrl,
    string? InfoUrl,
    string? InfoHash,
    long SizeBytes,
    int? Seeders,
    int? Peers,
    double DownloadVolumeFactor,
    double UploadVolumeFactor,
    DateTimeOffset PublishedAt,
    IReadOnlyList<int> Categories,
    string? ImdbId,
    int? TmdbId,
    int? TvdbId,
    IReadOnlyList<string>? IndexerLanguages = null,
    IReadOnlyList<string>? IndexerSubtitles = null,
    int? IndexerSeason = null,
    int? IndexerEpisode = null,
    int? TvMazeId = null,
    bool MatchedByExternalId = false);

public static class TorznabFeedParser
{
    private static readonly Regex Rfc2047AdjacentRegex = new(
        @"(?<=\=\?[^?]+\?[bBqQ]\?[^?]+\?\=)\s+(?=\=\?[^?]+\?[bBqQ]\?[^?]+\?\=)",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex Rfc2047WordRegex = new(
        @"\=\?(?<charset>[^?]+)\?(?<enc>[bBqQ])\?(?<data>[^?]+)\?\=",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static XDocument ParseXmlSafe(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = 10_000_000
        });
        return XDocument.Load(reader);
    }

    public static string DecodeRfc2047MimeWords(string input)
    {
        if (string.IsNullOrEmpty(input) || input.Length > 4096 || !input.Contains("=?")) return input;

        try
        {
            var collapsed = Rfc2047AdjacentRegex.Replace(input, "");

            return Rfc2047WordRegex.Replace(collapsed, match =>
            {
                var charset = match.Groups["charset"].Value;
                var enc = match.Groups["enc"].Value;
                var data = match.Groups["data"].Value;

                try
                {
                    Encoding encoding;
                    try { encoding = Encoding.GetEncoding(charset); }
                    catch { encoding = Encoding.UTF8; }

                    if (enc.Equals("B", StringComparison.OrdinalIgnoreCase))
                    {
                        var bytes = Convert.FromBase64String(data);
                        return encoding.GetString(bytes);
                    }
                    if (enc.Equals("Q", StringComparison.OrdinalIgnoreCase))
                    {
                        var bytes = new List<byte>();
                        for (int i = 0; i < data.Length; i++)
                        {
                            if (data[i] == '_')
                            {
                                bytes.Add((byte)' ');
                            }
                            else if (data[i] == '=' && i + 2 < data.Length &&
                                     byte.TryParse(data.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                            {
                                bytes.Add(b);
                                i += 2;
                            }
                            else
                            {
                                bytes.Add((byte)data[i]);
                            }
                        }
                        return encoding.GetString(bytes.ToArray());
                    }
                }
                catch
                {
                    // return raw match if decode fails
                }
                return match.Value;
            });
        }
        catch (RegexMatchTimeoutException)
        {
            return input;
        }
    }

    public static string? NormalizeLanguageCode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim().ToLowerInvariant();
        return s switch
        {
            "german" or "deutsch" or "ger" or "deu" or "de" => "de",
            "japanese" or "jpn" or "jap" or "ja" => "ja",
            "english" or "eng" or "en" => "en",
            "french" or "fra" or "fre" or "fr" => "fr",
            "spanish (latin america)" or "spanish" or "spa" or "es" => "es",
            "italian" or "ita" or "it" => "it",
            "portuguese" or "por" or "pt" => "pt",
            "russian" or "rus" or "ru" => "ru",
            "polish" or "pol" or "pl" => "pl",
            "arabic" or "ara" or "ar" => "ar",
            "hindi" or "hin" or "hi" => "hi",
            "korean" or "kor" or "ko" => "ko",
            "chinese" or "chi" or "zho" or "zh" => "zh",
            "dutch" or "nld" or "dut" or "nl" => "nl",
            "turkish" or "tur" or "tr" => "tr",
            "indonesian" or "ind" or "id" => "id",
            "thai" or "tha" or "th" => "th",
            "vietnamese" or "vie" or "vi" => "vi",
            "malay" or "msa" or "may" or "ms" => "ms",
            _ when s.Length == 2 => s,
            _ => null,
        };
    }

    public static IndexerCapabilities? TryParseCapabilitiesXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var doc = ParseXmlSafe(xml);
            if (doc.Root?.Name.LocalName != "caps") return null;

            var searchingEl = doc.Root.Element("searching");
            SearchCapability ParseCap(string elName)
            {
                var el = searchingEl?.Element(elName);
                if (el == null) return new SearchCapability(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                var avail = el.Attribute("available")?.Value?.Trim() ?? "";
                bool isAvail = avail.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                               avail.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                               avail == "1";
                var paramsCsv = el.Attribute("supportedParams")?.Value ?? "";
                var set = new HashSet<string>(
                    paramsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase);
                return new SearchCapability(isAvail, set);
            }

            var search = ParseCap("search");
            var tvSearch = ParseCap("tv-search");
            var movieSearch = ParseCap("movie-search");
            var musicSearch = ParseCap("music-search");
            var audioSearch = ParseCap("audio-search");
            var bookSearch = ParseCap("book-search");

            var catsSet = new HashSet<int>();
            var categoriesEl = doc.Root.Element("categories");
            if (categoriesEl != null)
            {
                foreach (var el in categoriesEl.Descendants())
                {
                    if (el.Name.LocalName is "category" or "subcat")
                    {
                        if (int.TryParse(el.Attribute("id")?.Value, out var cid))
                        {
                            catsSet.Add(cid);
                        }
                    }
                }
            }

            return new IndexerCapabilities(
                Search: search,
                TvSearch: tvSearch,
                MovieSearch: movieSearch,
                MusicSearch: musicSearch,
                AudioSearch: audioSearch,
                BookSearch: bookSearch,
                SupportedCategories: catsSet);
        }
        catch
        {
            return null;
        }
    }

    public static Uri BuildEndpointUri(string baseUrl, string? apiPath = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(baseUri.Host) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new ArgumentException("Invalid baseUrl", nameof(baseUrl));
        }

        string combinedPath;
        if (!string.IsNullOrWhiteSpace(apiPath))
        {
            if (apiPath.Contains('\\') || apiPath.Contains('?') || apiPath.Contains('#') ||
                apiPath.Any(char.IsControl) ||
                apiPath.Split('/').Any(seg => seg == ".."))
            {
                throw new ArgumentException("Invalid apiPath", nameof(apiPath));
            }

            var norm = apiPath.Trim().Trim('/');
            var existing = baseUri.AbsolutePath.TrimEnd('/');
            if (existing.EndsWith("/" + norm, StringComparison.OrdinalIgnoreCase) ||
                existing.Equals("/" + norm, StringComparison.OrdinalIgnoreCase))
            {
                combinedPath = existing;
            }
            else
            {
                combinedPath = string.IsNullOrEmpty(existing) ? "/" + norm : existing + "/" + norm;
            }
        }
        else
        {
            var existing = baseUri.AbsolutePath.TrimEnd('/');
            if (existing.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ||
                existing.EndsWith("/torznab", StringComparison.OrdinalIgnoreCase) ||
                existing.EndsWith("/nabapi", StringComparison.OrdinalIgnoreCase))
            {
                combinedPath = existing;
            }
            else
            {
                combinedPath = string.IsNullOrEmpty(existing) ? "/api" : existing + "/api";
            }
        }

        var ub = new UriBuilder(baseUri)
        {
            Path = combinedPath
        };

        var result = ub.Uri;
        if (!result.Authority.Equals(baseUri.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Authority changed after combining path.");
        }

        return result;
    }

    public static Uri BuildCapabilitiesUri(string baseUrl, string? apiKey, string? apiPath = null)
    {
        var endpoint = BuildEndpointUri(baseUrl, apiPath);
        var ub = new UriBuilder(endpoint);
        var qs = HttpUtility.ParseQueryString(ub.Query);
        qs["t"] = "caps";
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            qs["apikey"] = apiKey;
        }
        ub.Query = qs.ToString();
        return ub.Uri;
    }

    public static Uri BuildQueryUri(
        string baseUrl,
        string? apiKey,
        string? query,
        string? vertical,
        IReadOnlyList<int>? explicitCategories,
        string? imdbId = null,
        int? tmdbId = null,
        int? tvdbId = null,
        int? season = null,
        int? episode = null,
        int? tvMazeId = null,
        int? year = null,
        string? artist = null,
        string? album = null,
        string? author = null,
        string? bookTitle = null,
        string? apiPath = null,
        IndexerCapabilities? capabilities = null)
    {
        var endpoint = BuildEndpointUri(baseUrl, apiPath);
        var ub = new UriBuilder(endpoint);
        var qs = HttpUtility.ParseQueryString(ub.Query);

        var vert = vertical?.Trim().ToLowerInvariant();

        if (vert is MediaVerticals.Movies or "movie")
        {
            bool movieSearchAvail = capabilities?.MovieSearchAvailable ?? true;
            bool canUseImdb = !string.IsNullOrWhiteSpace(imdbId) && (capabilities?.SupportsParam("movie-search", "imdbid") ?? true);
            bool canUseTmdb = !canUseImdb && tmdbId.HasValue && (capabilities?.SupportsParam("movie-search", "tmdbid") ?? true);

            if (canUseImdb)
            {
                qs["t"] = "movie";
                qs["imdbid"] = imdbId!.TrimStart('t');
                // DO NOT emit q!
            }
            else if (canUseTmdb)
            {
                qs["t"] = "movie";
                qs["tmdbid"] = tmdbId!.Value.ToString(CultureInfo.InvariantCulture);
                // DO NOT emit q!
            }
            else
            {
                bool supportsQ = capabilities?.SupportsParam("movie-search", "q") ?? true;
                if (movieSearchAvail && supportsQ)
                {
                    qs["t"] = "movie";
                    if (!string.IsNullOrWhiteSpace(query)) qs["q"] = query;
                    if (year.HasValue && (capabilities?.SupportsParam("movie-search", "year") ?? true))
                    {
                        qs["year"] = year.Value.ToString(CultureInfo.InvariantCulture);
                    }
                }
                else
                {
                    qs["t"] = "search";
                    if (!string.IsNullOrWhiteSpace(query)) qs["q"] = query;
                }
            }
        }
        else if (vert is MediaVerticals.Tv or MediaVerticals.Anime or "series")
        {
            bool tvSearchAvail = capabilities?.TvSearchAvailable ?? true;
            bool useTvdb = tvdbId.HasValue && (capabilities?.SupportsParam("tv-search", "tvdbid") ?? true);
            bool useTvMaze = !useTvdb && tvMazeId.HasValue && (capabilities?.SupportsParam("tv-search", "tvmazeid") ?? true);
            bool useTmdb = !useTvdb && !useTvMaze && tmdbId.HasValue && capabilities != null && capabilities.SupportsParam("tv-search", "tmdbid");
            bool useImdb = !useTvdb && !useTvMaze && !useTmdb && !string.IsNullOrWhiteSpace(imdbId) && capabilities != null && capabilities.SupportsParam("tv-search", "imdbid");

            if (useTvdb || useTvMaze || useTmdb || useImdb)
            {
                qs["t"] = "tvsearch";
                if (useTvdb) qs["tvdbid"] = tvdbId!.Value.ToString(CultureInfo.InvariantCulture);
                else if (useTvMaze) qs["tvmazeid"] = tvMazeId!.Value.ToString(CultureInfo.InvariantCulture);
                else if (useTmdb) qs["tmdbid"] = tmdbId!.Value.ToString(CultureInfo.InvariantCulture);
                else if (useImdb) qs["imdbid"] = imdbId!.TrimStart('t');

                if (season.HasValue && (capabilities?.SupportsParam("tv-search", "season") ?? true))
                {
                    qs["season"] = season.Value.ToString(CultureInfo.InvariantCulture);
                }
                if (episode.HasValue && (capabilities?.SupportsParam("tv-search", "ep") ?? true))
                {
                    qs["ep"] = episode.Value.ToString(CultureInfo.InvariantCulture);
                }
                // DO NOT emit q!
            }
            else if (season.HasValue && tvSearchAvail)
            {
                qs["t"] = "tvsearch";
                if (!string.IsNullOrWhiteSpace(query)) qs["q"] = query;
                if (capabilities?.SupportsParam("tv-search", "season") ?? true)
                {
                    qs["season"] = season.Value.ToString(CultureInfo.InvariantCulture);
                }
                if (episode.HasValue && (capabilities?.SupportsParam("tv-search", "ep") ?? true))
                {
                    qs["ep"] = episode.Value.ToString(CultureInfo.InvariantCulture);
                }
            }
            else
            {
                qs["t"] = "search";
                if (!string.IsNullOrWhiteSpace(query)) qs["q"] = query;
            }
        }
        else if (vert == MediaVerticals.Music)
        {
            if (capabilities == null || capabilities.MusicSearchAvailable)
            {
                qs["t"] = "music";
                bool hasArtist = !string.IsNullOrWhiteSpace(artist) && (capabilities?.SupportsParam("music-search", "artist") ?? true);
                bool hasAlbum = !string.IsNullOrWhiteSpace(album) && (capabilities?.SupportsParam("music-search", "album") ?? true);
                if (hasArtist || hasAlbum)
                {
                    if (hasArtist) qs["artist"] = artist;
                    if (hasAlbum) qs["album"] = album;
                    if (year.HasValue && (capabilities?.SupportsParam("music-search", "year") ?? true))
                    {
                        qs["year"] = year.Value.ToString(CultureInfo.InvariantCulture);
                    }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(query)) qs["q"] = query;
                }
            }
            else if (capabilities.AudioSearchAvailable)
            {
                qs["t"] = "audio";
                if (!string.IsNullOrWhiteSpace(query)) qs["q"] = query;
            }
            else
            {
                qs["t"] = "search";
                if (!string.IsNullOrWhiteSpace(query)) qs["q"] = query;
            }
        }
        else if (vert is MediaVerticals.Ebooks or MediaVerticals.Audiobooks or MediaVerticals.Comics)
        {
            if (capabilities == null || capabilities.BookSearchAvailable)
            {
                qs["t"] = "book";
                bool hasAuthor = !string.IsNullOrWhiteSpace(author) && (capabilities?.SupportsParam("book-search", "author") ?? true);
                bool hasTitle = !string.IsNullOrWhiteSpace(bookTitle) && (capabilities?.SupportsParam("book-search", "title") ?? true);
                if (hasAuthor || hasTitle)
                {
                    if (hasAuthor) qs["author"] = author;
                    if (hasTitle) qs["title"] = bookTitle;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(query)) qs["q"] = query;
                }
            }
            else
            {
                qs["t"] = "search";
                if (!string.IsNullOrWhiteSpace(query)) qs["q"] = query;
            }
        }
        else
        {
            qs["t"] = "search";
            if (!string.IsNullOrWhiteSpace(query)) qs["q"] = query;
        }

        var cats = explicitCategories ?? NewznabCategoryMapper.GetCategoriesForVertical(vertical);
        if (cats.Count > 0)
        {
            qs["cat"] = string.Join(",", cats);
        }

        if (!string.IsNullOrWhiteSpace(apiKey)) qs["apikey"] = apiKey;

        ub.Query = qs.ToString();
        return ub.Uri;
    }

    public static IReadOnlyList<IndexedReleaseCandidate> ParseXmlFeed(
        string xmlContent,
        long indexerId,
        string indexerName,
        string protocol)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
        {
            return [];
        }

        var doc = ParseXmlSafe(xmlContent);
        var items = doc.Descendants("item").ToList();

        var errorElem = doc.Root?.Name.LocalName == "error" ? doc.Root : doc.Root?.Element("error");
        if (errorElem != null && items.Count == 0)
        {
            int? code = int.TryParse(errorElem.Attribute("code")?.Value, out var c) ? c : null;
            var desc = errorElem.Attribute("description")?.Value ?? errorElem.Value?.Trim() ?? "Unknown error";
            throw new IndexerApiException(code, desc);
        }

        var list = new List<IndexedReleaseCandidate>();

        foreach (var item in items)
        {
            var rawTitle = item.Element("title")?.Value;
            var enclosure = item.Element("enclosure");
            var link = enclosure?.Attribute("url")?.Value ?? item.Element("link")?.Value;
            if (string.IsNullOrWhiteSpace(rawTitle) || string.IsNullOrWhiteSpace(link))
            {
                continue;
            }

            var title = DecodeRfc2047MimeWords(rawTitle);
            title = WebUtility.HtmlDecode(title).Trim();
            if (title.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase) && title.Length > 4)
            {
                title = title[..^4].Trim();
            }
            else if (title.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && title.Length > 8)
            {
                title = title[..^8].Trim();
            }

            var guid = item.Element("guid")?.Value ?? link;
            var comments = item.Element("comments")?.Value;
            var pubDateRaw = item.Element("pubDate")?.Value;
            var publishedAt = DateTimeOffset.TryParse(pubDateRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)
                ? dt
                : DateTimeOffset.UtcNow;

            long size = long.TryParse(enclosure?.Attribute("length")?.Value, out var encLen)
                ? encLen
                : long.TryParse(item.Element("size")?.Value, out var elLen) ? elLen : 0;

            var attrs = item.Elements()
                .Where(e => e.Name.LocalName == "attr")
                .Select(e => (
                    Name: e.Attribute("name")?.Value?.ToLowerInvariant() ?? "",
                    Value: e.Attribute("value")?.Value ?? ""))
                .Where(a => !string.IsNullOrEmpty(a.Name))
                .ToList();

            var cats = new HashSet<int>();
            foreach (var cAttr in attrs.Where(a => a.Name == "category"))
            {
                if (int.TryParse(cAttr.Value, out var cid))
                {
                    cats.Add(cid);
                }
            }

            string? Attr(string name) => attrs.FirstOrDefault(a => a.Name == name).Value is { Length: > 0 } v ? v : null;

            if (size == 0 && long.TryParse(Attr("size"), out var attrSize))
            {
                size = attrSize;
            }

            var magnet = Attr("magneturl") ?? (link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? link : null);
            var infoHash = (Attr("infohash") ?? CardigannDefinitionCatalog.ExtractInfoHashFromMagnet(magnet))?.ToLowerInvariant();
            int? seeders = int.TryParse(Attr("seeders"), out var sd) ? sd : null;
            int? peers = int.TryParse(Attr("peers"), out var pr) ? pr : null;
            var dlFactor = double.TryParse(Attr("downloadvolumefactor"), NumberStyles.Float, CultureInfo.InvariantCulture, out var df) ? df : 1.0;
            var ulFactor = double.TryParse(Attr("uploadvolumefactor"), NumberStyles.Float, CultureInfo.InvariantCulture, out var uf) ? uf : 1.0;

            var rawImdb = Attr("imdb") ?? Attr("imdbid");
            var imdbId = string.IsNullOrWhiteSpace(rawImdb)
                ? null
                : (rawImdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? rawImdb : $"tt{rawImdb.PadLeft(7, '0')}");
            int? tmdbId = int.TryParse(Attr("tmdbid"), out var tm) ? tm : null;
            int? tvdbId = int.TryParse(Attr("tvdbid"), out var tv) ? tv : null;

            var languages = new List<string>();
            foreach (var langAttr in attrs.Where(a => a.Name == "language"))
            {
                foreach (var part in langAttr.Value.Split([',', ';', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var norm = NormalizeLanguageCode(part);
                    if (norm != null && !languages.Contains(norm)) languages.Add(norm);
                }
            }

            var subtitles = new List<string>();
            foreach (var subAttr in attrs.Where(a => a.Name is "subs" or "subtitles"))
            {
                foreach (var part in subAttr.Value.Split([',', ';', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var norm = NormalizeLanguageCode(part);
                    if (norm != null && !subtitles.Contains(norm)) subtitles.Add(norm);
                }
            }

            int? parsedSeason = null;
            var rawSeason = Attr("season");
            if (!string.IsNullOrWhiteSpace(rawSeason))
            {
                var sVal = rawSeason.Trim();
                if (sVal.StartsWith("S", StringComparison.OrdinalIgnoreCase)) sVal = sVal[1..];
                if (int.TryParse(sVal, out var sn)) parsedSeason = sn;
            }

            int? parsedEpisode = null;
            var rawEp = Attr("episode");
            if (!string.IsNullOrWhiteSpace(rawEp))
            {
                var eVal = rawEp.Trim();
                if (eVal.StartsWith("E", StringComparison.OrdinalIgnoreCase)) eVal = eVal[1..];
                if (int.TryParse(eVal, out var ep)) parsedEpisode = ep;
            }

            int? parsedTvMazeId = int.TryParse(Attr("tvmazeid"), out var tmz) ? tmz : null;

            var orderedCats = cats.OrderBy(x => x).ToArray();
            var vertical = NewznabCategoryMapper.ResolveVerticalFromCategories(orderedCats, title);

            list.Add(new IndexedReleaseCandidate(
                Guid: guid,
                IndexerId: indexerId,
                IndexerName: indexerName,
                Protocol: protocol,
                MediaVertical: vertical,
                Title: title,
                DownloadUrl: link,
                MagnetUrl: magnet,
                InfoUrl: comments,
                InfoHash: infoHash,
                SizeBytes: size,
                Seeders: seeders,
                Peers: peers,
                DownloadVolumeFactor: dlFactor,
                UploadVolumeFactor: ulFactor,
                PublishedAt: publishedAt,
                Categories: orderedCats,
                ImdbId: imdbId,
                TmdbId: tmdbId,
                TvdbId: tvdbId,
                IndexerLanguages: languages.Count > 0 ? languages : null,
                IndexerSubtitles: subtitles.Count > 0 ? subtitles : null,
                IndexerSeason: parsedSeason,
                IndexerEpisode: parsedEpisode,
                TvMazeId: parsedTvMazeId,
                MatchedByExternalId: false));
        }

        return list;
    }
}
