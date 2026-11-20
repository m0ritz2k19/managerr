using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml.Linq;
using Managerr.Datastore.Services;

namespace Managerr.Metadata;

public static class BuiltInMetadataProviderCatalog
{
    public static readonly IReadOnlyList<MetadataProviderDescriptor> Descriptors =
    [
        // --- Built-in *arr Default Providers ---
        new(
            Key: "tvdb",
            DisplayName: "TheTVDB v4 / SkyHook (Sonarr Default)",
            IsBuiltInArrDefault: true,
            RequiresApiKey: false,
            SupportedVerticals: [MetadataVerticals.Tv, MetadataVerticals.Anime, MetadataVerticals.Movies],
            DefaultBaseUrl: "https://api4.thetvdb.com/v4",
            Description: "Sonarr default series/season/episode metadata via zero-key SkyHook v2 or direct TVDB v4 key."),
        new(
            Key: "tmdb",
            DisplayName: "TMDB / SkyHook (Radarr Default)",
            IsBuiltInArrDefault: true,
            RequiresApiKey: false,
            SupportedVerticals: [MetadataVerticals.Movies, MetadataVerticals.Tv, MetadataVerticals.Anime],
            DefaultBaseUrl: "https://api.themoviedb.org/3",
            Description: "Radarr default movie & multi-language metadata, posters, and IMDb/TMDB lookup (zero-key SkyHook + optional TMDB key)."),
        new(
            Key: "musicbrainz",
            DisplayName: "MusicBrainz + CoverArtArchive (Lidarr Default)",
            IsBuiltInArrDefault: true,
            RequiresApiKey: false,
            SupportedVerticals: [MetadataVerticals.Music],
            DefaultBaseUrl: "https://musicbrainz.org/ws/2",
            Description: "Lidarr default artist, release-group, and tracklist catalog with CoverArtArchive."),
        new(
            Key: "acoustid",
            DisplayName: "Chromaprint / AcoustID (Lidarr Audio Fingerprint)",
            IsBuiltInArrDefault: true,
            RequiresApiKey: true,
            SupportedVerticals: [MetadataVerticals.Music],
            DefaultBaseUrl: "https://api.acoustid.org/v2",
            Description: "Acoustic fingerprint lookup bridging Chromaprint hashes to MusicBrainz recording IDs.",
            ConfigurationStatus: MetadataProviderConfigurationStates.NotConfigured),
        new(
            Key: "openlibrary",
            DisplayName: "Open Library (Readarr Default Replacement)",
            IsBuiltInArrDefault: true,
            RequiresApiKey: false,
            SupportedVerticals: [MetadataVerticals.Ebooks, MetadataVerticals.Audiobooks, MetadataVerticals.Comics],
            DefaultBaseUrl: "https://openlibrary.org",
            Description: "Zero-key open catalog for books, ISBN-13 editions, covers, and authors."),

        // --- Built-in Alternative TV & Anime Providers ---
        new(
            Key: "anilist",
            DisplayName: "AniList GraphQL",
            IsBuiltInArrDefault: false,
            RequiresApiKey: false,
            SupportedVerticals: [MetadataVerticals.Anime, MetadataVerticals.Comics],
            DefaultBaseUrl: "https://graphql.anilist.co",
            Description: "Zero-key GraphQL anime & manga catalog with Romaji/English/Native aliases and cour splits."),
        new(
            Key: "anidb",
            DisplayName: "AniDB + TheXEM Scene Mapping",
            IsBuiltInArrDefault: false,
            RequiresApiKey: true,
            SupportedVerticals: [MetadataVerticals.Anime],
            DefaultBaseUrl: "http://api.anidb.net:9001/httpapi",
            Description: "Canonical absolute anime numbering with Fribb anime-lists & TheXEM scene mapping.",
            ConfigurationStatus: MetadataProviderConfigurationStates.NotConfigured),
        new(
            Key: "tvmaze",
            DisplayName: "TVMaze",
            IsBuiltInArrDefault: false,
            RequiresApiKey: false,
            SupportedVerticals: [MetadataVerticals.Tv, MetadataVerticals.Anime],
            DefaultBaseUrl: "https://api.tvmaze.com",
            Description: "Fast, credential-free TV & web-series schedule and episode catalog."),
        new(
            Key: "trakt",
            DisplayName: "Trakt",
            IsBuiltInArrDefault: false,
            RequiresApiKey: true,
            SupportedVerticals: [MetadataVerticals.Tv, MetadataVerticals.Movies, MetadataVerticals.Anime],
            DefaultBaseUrl: "https://api.trakt.tv",
            Description: "Movies & TV metadata, ratings, lists, and TMDB/TVDB/IMDb ID bridges.",
            ConfigurationStatus: MetadataProviderConfigurationStates.NotConfigured),

        // --- Built-in Alternative Music Providers ---
        new(
            Key: "discogs",
            DisplayName: "Discogs",
            IsBuiltInArrDefault: false,
            RequiresApiKey: true,
            SupportedVerticals: [MetadataVerticals.Music],
            DefaultBaseUrl: "https://api.discogs.com",
            Description: "Vinyl, indie, electronic, remix, and EP catalog absent from MusicBrainz.",
            ConfigurationStatus: MetadataProviderConfigurationStates.NotConfigured),
        new(
            Key: "deezer",
            DisplayName: "Deezer Catalog API",
            IsBuiltInArrDefault: false,
            RequiresApiKey: false,
            SupportedVerticals: [MetadataVerticals.Music],
            DefaultBaseUrl: "https://api.deezer.com",
            Description: "Zero-key commercial music, high-res album art, and tracklist resolver."),
        new(
            Key: "indie_folder_tags",
            DisplayName: "Unlisted / Indie Folder-Tag Mode",
            IsBuiltInArrDefault: false,
            RequiresApiKey: false,
            SupportedVerticals: [MetadataVerticals.Music],
            DefaultBaseUrl: "local://indie-tags",
            Description: "Manage Bandcamp, SoundCloud, DJ mixes, and OSTs directly from ID3/Vorbis tags without MusicBrainz gating."),

        // --- Built-in Alternative Books, Audiobooks & Comics Providers ---
        new(
            Key: "hardcover",
            DisplayName: "Hardcover GraphQL",
            IsBuiltInArrDefault: false,
            RequiresApiKey: true,
            SupportedVerticals: [MetadataVerticals.Ebooks, MetadataVerticals.Audiobooks],
            DefaultBaseUrl: "https://api.hardcover.app/v1/graphql",
            Description: "Modern Goodreads replacement with rich book series ordering, ISBNs, and audio/print editions.",
            ConfigurationStatus: MetadataProviderConfigurationStates.NotConfigured),
        new(
            Key: "audnexus",
            DisplayName: "Audnexus / Apple Books Audio",
            IsBuiltInArrDefault: false,
            RequiresApiKey: false,
            SupportedVerticals: [MetadataVerticals.Audiobooks],
            DefaultBaseUrl: "https://api.audnex.us",
            Description: "ASIN audiobook metadata, narrators, chapters, and zero-key Apple Books / Audible catalog."),
        new(
            Key: "bookbrainz",
            DisplayName: "BookBrainz",
            IsBuiltInArrDefault: false,
            RequiresApiKey: false,
            SupportedVerticals: [MetadataVerticals.Ebooks, MetadataVerticals.Audiobooks],
            DefaultBaseUrl: "https://api.bookbrainz.org/1",
            Description: "MetaBrainz open bibliographic database for works, editions, and authors."),
        new(
            Key: "googlebooks",
            DisplayName: "Google Books",
            IsBuiltInArrDefault: false,
            RequiresApiKey: false,
            SupportedVerticals: [MetadataVerticals.Ebooks],
            DefaultBaseUrl: "https://www.googleapis.com/books/v1",
            Description: "Global ISBN-10/ISBN-13 print & ebook catalog."),
        new(
            Key: "dnb",
            DisplayName: "Deutsche Nationalbibliothek (DNB)",
            IsBuiltInArrDefault: false,
            RequiresApiKey: false,
            SupportedVerticals: [MetadataVerticals.Ebooks, MetadataVerticals.Audiobooks],
            DefaultBaseUrl: "https://services.dnb.de/sru/dnb",
            Description: "Authoritative German-language book and Hörbuch national bibliography."),
        new(
            Key: "mangadex",
            DisplayName: "MangaDex",
            IsBuiltInArrDefault: false,
            RequiresApiKey: false,
            SupportedVerticals: [MetadataVerticals.Comics],
            DefaultBaseUrl: "https://api.mangadex.org",
            Description: "Multi-language manga volumes, chapters, and localized title aliases."),
        new(
            Key: "comicvine",
            DisplayName: "ComicVine",
            IsBuiltInArrDefault: false,
            RequiresApiKey: true,
            SupportedVerticals: [MetadataVerticals.Comics],
            DefaultBaseUrl: "https://comicvine.gamespot.com/api",
            Description: "Western comics, graphic novels, volumes, and issue numbering.",
            ConfigurationStatus: MetadataProviderConfigurationStates.NotConfigured),

        // --- Offline Sidecar Escape Hatch ---
        new(
            Key: "local_sidecar",
            DisplayName: "Local Sidecar (.nfo / metadata.opf)",
            IsBuiltInArrDefault: false,
            RequiresApiKey: false,
            SupportedVerticals: MetadataVerticals.All,
            DefaultBaseUrl: "local://sidecar",
            Description: "Zero-network Kodi/Jellyfin .nfo and Calibre metadata.opf XML reader/writer."),
    ];

    public static IReadOnlyList<IMetadataProvider> CreateAll(
        IMetadataHttpGateway httpGateway,
        IAppSettingsService settingsService,
        ISidecarMetadataService sidecarService) =>
        Descriptors
            .Select(d => (IMetadataProvider)new UniversalBuiltInMetadataProvider(d, httpGateway, settingsService, sidecarService))
            .ToList();

    public static MetadataWorkCandidate? ParseCandidateFromJson(
        JsonElement obj,
        string providerKey = "tmdb",
        string vertical = MetadataVerticals.Anime,
        string? fallbackId = null)
    {
        var desc = Descriptors.FirstOrDefault(d => d.Key == providerKey)
            ?? new MetadataProviderDescriptor(providerKey, providerKey, false, false, [vertical], "https://localhost", "");
        var provider = new UniversalBuiltInMetadataProvider(desc, null!, null!, null!);
        return provider.ParseSingleObject(obj, vertical, fallbackId);
    }
}

internal sealed partial class UniversalBuiltInMetadataProvider(
    MetadataProviderDescriptor descriptor,
    IMetadataHttpGateway httpGateway,
    IAppSettingsService settingsService,
    ISidecarMetadataService sidecarService) : IMetadataProvider
{
    private const string RadarrSkyHookBase = "https://api.radarr.video/v1";
    private const string SonarrSkyHookBase = "https://skyhook.sonarr.tv/v2/tvdb";

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"^tt\d{6,}$", RegexOptions.IgnoreCase)]
    private static partial Regex ImdbIdRegex();

    [GeneratedRegex(@"^B0[A-Z0-9]{8}$", RegexOptions.IgnoreCase)]
    private static partial Regex AsinRegex();

    [GeneratedRegex(@"\b(1\d{3}|20\d{2})\b")]
    private static partial Regex FourDigitYearRegex();

    [GeneratedRegex(@"http://anidb\.net/[a-z]+\d+\s+\[([^\]]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex AniDbWikiLinkRegex();

    [GeneratedRegex(@"\s+\(\d+\)$")]
    private static partial Regex DiscogsArtistNumberSuffixRegex();

    private static readonly (string Aid, string PrimaryTitle, string[] AltTitles, int Year)[] BuiltInAniDbTitleCrosswalk =
    [
        ("1", "Cowboy Bebop", ["Cowboy Bebop", "カウボーイビバップ"], 1998),
        ("22", "Neon Genesis Evangelion", ["Shinseiki Evangelion", "新世紀エヴァンゲリオン"], 1995),
        ("28", "Akira", ["AKIRA", "アキラ"], 1988),
        ("69", "One Piece", ["ONE PIECE", "ワンピース"], 1999),
        ("112", "Spirited Away", ["Sen to Chihiro no Kamikakushi", "千と千尋の神隠し"], 2001),
        ("239", "Naruto", ["NARUTO", "ナルト"], 2002),
        ("1539", "Monster", ["MONSTER", "モンスター"], 2004),
        ("2369", "Bleach", ["BLEACH", "ブリーチ"], 2004),
        ("4563", "Death Note", ["DEATH NOTE", "デスノート"], 2006),
        ("4521", "Code Geass: Lelouch of the Rebellion", ["Code Geass: Hangyaku no Lelouch"], 2006),
        ("6107", "Fullmetal Alchemist: Brotherhood", ["Hagane no Renkinjutsushi: Fullmetal Alchemist"], 2009),
        ("7729", "Steins;Gate", ["STEINS;GATE", "シュタインズ・ゲート"], 2011),
        ("9541", "Attack on Titan", ["Shingeki no Kyojin", "進撃の巨人"], 2013),
        ("14543", "Demon Slayer: Kimetsu no Yaiba", ["Kimetsu no Yaiba", "鬼滅の刃"], 2019),
        ("15615", "Cyberpunk: Edgerunners", ["Cyberpunk Edgerunners"], 2022),
        ("17617", "Frieren: Beyond Journey's End", ["Sousou no Frieren", "葬送のフリーレン"], 2023),
        ("18382", "Dan Da Dan", ["Dandadan", "ダンダダン"], 2024),
    ];

    public MetadataProviderDescriptor Descriptor
    {
        get
        {
            if (!descriptor.RequiresApiKey)
            {
                return descriptor with { ConfigurationStatus = MetadataProviderConfigurationStates.Ready };
            }

            var apiKey = GetConfiguredApiKeyAsync(CancellationToken.None).GetAwaiter().GetResult();
            var status = string.IsNullOrWhiteSpace(apiKey)
                ? MetadataProviderConfigurationStates.NotConfigured
                : MetadataProviderConfigurationStates.Ready;
            return descriptor with { ConfigurationStatus = status };
        }
    }

    public async Task<MetadataProviderStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var apiKey = await GetConfiguredApiKeyAsync(ct);
        var customUrl = await settingsService.GetAsync($"metadata.{descriptor.Key}.base_url", ct);
        var baseUrl = !string.IsNullOrWhiteSpace(customUrl) ? customUrl.TrimEnd('/') : descriptor.DefaultBaseUrl;
        var configStatus = !descriptor.RequiresApiKey || !string.IsNullOrWhiteSpace(apiKey)
            ? MetadataProviderConfigurationStates.Ready
            : MetadataProviderConfigurationStates.NotConfigured;

        return new MetadataProviderStatus(
            Key: descriptor.Key,
            DisplayName: descriptor.DisplayName,
            IsBuiltInArrDefault: descriptor.IsBuiltInArrDefault,
            RequiresApiKey: descriptor.RequiresApiKey,
            ConfigurationStatus: configStatus,
            EffectiveBaseUrl: baseUrl,
            SupportedVerticals: descriptor.SupportedVerticals);
    }

    private async Task<string?> GetConfiguredApiKeyAsync(CancellationToken ct)
    {
        var apiKey = await settingsService.GetSecretAsync($"metadata.{descriptor.Key}.api_key", ct)
            ?? await settingsService.GetAsync($"metadata.{descriptor.Key}.api_key", ct);
        if (string.IsNullOrWhiteSpace(apiKey) && descriptor.Key == "anidb")
        {
            apiKey = await settingsService.GetSecretAsync("metadata.anidb.client", ct)
                ?? await settingsService.GetAsync("metadata.anidb.client", ct);
        }

        return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }

    private async Task<(string Client, string Version)> ResolveAniDbClientAndVersionAsync(string? apiKey, CancellationToken ct)
    {
        var raw = (apiKey ?? "").Trim();
        var explicitVer = await settingsService.GetAsync("metadata.anidb.clientver", ct);
        if (raw.Contains(':'))
        {
            var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
            return (parts[0], !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : (!string.IsNullOrWhiteSpace(explicitVer) ? explicitVer.Trim() : "1"));
        }

        if (raw.Contains('/'))
        {
            var parts = raw.Split('/', 2, StringSplitOptions.TrimEntries);
            return (parts[0], !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : (!string.IsNullOrWhiteSpace(explicitVer) ? explicitVer.Trim() : "1"));
        }

        return (raw, !string.IsNullOrWhiteSpace(explicitVer) ? explicitVer.Trim() : "1");
    }

    public async Task<IReadOnlyList<MetadataWorkCandidate>> SearchAsync(
        string query,
        string vertical,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) ||
            !descriptor.SupportedVerticals.Contains(vertical, StringComparer.OrdinalIgnoreCase))
        {
            return [];
        }

        if (descriptor.Key == "local_sidecar")
        {
            var trimmed = query.TrimStart();
            if (trimmed.StartsWith('<'))
            {
                var parsed = vertical is MetadataVerticals.Ebooks or MetadataVerticals.Audiobooks
                    ? sidecarService.ParseCalibreOpf(trimmed, vertical)
                    : sidecarService.ParseKodiJellyfinNfo(trimmed, vertical);
                return [parsed];
            }

            return [];
        }

        if (descriptor.Key == "indie_folder_tags")
        {
            return [BuildSyntheticCandidate(query, vertical, $"indie-{Slugify(query)}")];
        }

        var live = await TryExecuteLiveSearchAsync(query, vertical, ct);
        return live.Count > 0 ? live : [];
    }

    public async Task<MetadataWorkCandidate?> GetByExternalIdAsync(
        string externalId,
        string vertical,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(externalId) ||
            !descriptor.SupportedVerticals.Contains(vertical, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        if (descriptor.Key == "indie_folder_tags")
        {
            return BuildSyntheticCandidate(externalId, vertical, externalId);
        }

        if (descriptor.Key == "local_sidecar")
        {
            return null;
        }

        var apiKey = await GetConfiguredApiKeyAsync(ct);
        if (descriptor.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var customUrl = await settingsService.GetAsync($"metadata.{descriptor.Key}.base_url", ct);
        var hasCustomUrl = !string.IsNullOrWhiteSpace(customUrl);
        var baseUrl = hasCustomUrl ? customUrl!.TrimEnd('/') : descriptor.DefaultBaseUrl;
        var cleanExternalId = externalId.Trim();

        string? aniDbClient = null;
        string? aniDbClientVer = null;
        if (descriptor.Key == "anidb")
        {
            (aniDbClient, aniDbClientVer) = await ResolveAniDbClientAndVersionAsync(apiKey, ct);
        }

        var req = BuildLookupRequest(baseUrl, hasCustomUrl, cleanExternalId, vertical, apiKey, aniDbClient, aniDbClientVer);
        if (req is null)
        {
            return null;
        }

        var raw = await httpGateway.SendAsync(req, ct);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return ParseLookupResponse(raw, cleanExternalId, vertical);
    }

    private async Task<IReadOnlyList<MetadataWorkCandidate>> TryExecuteLiveSearchAsync(
        string query,
        string vertical,
        CancellationToken ct)
    {
        var apiKey = await GetConfiguredApiKeyAsync(ct);
        if (descriptor.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        var customUrl = await settingsService.GetAsync($"metadata.{descriptor.Key}.base_url", ct);
        var hasCustomUrl = !string.IsNullOrWhiteSpace(customUrl);
        var baseUrl = hasCustomUrl ? customUrl!.TrimEnd('/') : descriptor.DefaultBaseUrl;
        var cleanQuery = query.Trim();
        var enc = HttpUtility.UrlEncode(cleanQuery);

        if (descriptor.Key == "anidb")
        {
            var (client, clientVer) = await ResolveAniDbClientAndVersionAsync(apiKey, ct);
            var titleSearchUrl = await settingsService.GetAsync("metadata.anidb.title_search_url", ct);
            return await ExecuteAniDbSearchAsync(baseUrl, hasCustomUrl, titleSearchUrl, cleanQuery, enc, vertical, client, clientVer, ct);
        }

        MetadataHttpRequest? req = descriptor.Key switch
        {
            "anilist" => new MetadataHttpRequest(
                baseUrl,
                HttpMethod.Post,
                JsonBody: JsonSerializer.Serialize(new
                {
                    query = "query ($search: String, $type: MediaType) { Page(perPage: 10) { media(search: $search, type: $type) { id idMal format genres countryOfOrigin popularity averageScore title { romaji english native } synonyms startDate { year } status episodes description coverImage { large } } } }",
                    variables = new { search = cleanQuery, type = vertical == MetadataVerticals.Comics ? "MANGA" : "ANIME" },
                })),
            "hardcover" => new MetadataHttpRequest(
                baseUrl,
                HttpMethod.Post,
                Headers: new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" },
                JsonBody: JsonSerializer.Serialize(new
                {
                    query = "query ($q: String!) { books(where: { title: { _ilike: $q } }, limit: 10) { id slug title release_year users_count ratings_count description image { url } contributions { author { name } } } }",
                    variables = new { q = $"%{cleanQuery}%" },
                })),
            "tmdb" => BuildTmdbSearchRequest(baseUrl, hasCustomUrl, cleanQuery, enc, vertical, apiKey),
            "tvdb" => BuildTvdbSearchRequest(baseUrl, hasCustomUrl, enc, vertical, apiKey),
            "tvmaze" => new MetadataHttpRequest($"{baseUrl}/search/shows?q={enc}", HttpMethod.Get),
            "openlibrary" => new MetadataHttpRequest($"{baseUrl}/search.json?q={enc}&limit=12", HttpMethod.Get),
            "audnexus" => BuildAudnexusSearchRequest(baseUrl, hasCustomUrl, cleanQuery, enc),
            "musicbrainz" => new MetadataHttpRequest($"{baseUrl}/release-group/?query={enc}&limit=12&fmt=json", HttpMethod.Get),
            "deezer" => new MetadataHttpRequest($"{baseUrl}/search/album?q={enc}&limit=12", HttpMethod.Get),
            "mangadex" => new MetadataHttpRequest($"{baseUrl}/manga?title={enc}&limit=12&includes[]=cover_art", HttpMethod.Get),
            "googlebooks" => new MetadataHttpRequest($"{baseUrl}/volumes?q={enc}&maxResults=12", HttpMethod.Get),
            "dnb" => new MetadataHttpRequest(
                $"{baseUrl}?version=1.1&operation=searchRetrieve&query={enc}&maximumRecords=15&recordSchema=oai_dc",
                HttpMethod.Get),
            "bookbrainz" => new MetadataHttpRequest(
                $"{baseUrl}/search?q={enc}&collection=work",
                HttpMethod.Get),
            "discogs" => new MetadataHttpRequest(
                $"{baseUrl}/database/search?q={enc}&type=master",
                HttpMethod.Get,
                Headers: new Dictionary<string, string>
                {
                    ["Authorization"] = $"Discogs token={apiKey}",
                    ["User-Agent"] = "Managerr/1.0 +https://managerr.local",
                }),
            "comicvine" => new MetadataHttpRequest(
                $"{baseUrl}/search/?api_key={HttpUtility.UrlEncode(apiKey!)}&format=json&resources=volume&query={enc}",
                HttpMethod.Get,
                Headers: new Dictionary<string, string>
                {
                    ["User-Agent"] = "Managerr/1.0 +https://managerr.local",
                }),
            "acoustid" => BuildAcoustIdRequest(baseUrl, cleanQuery, apiKey!, isLookup: false),
            "trakt" => new MetadataHttpRequest(
                $"{baseUrl}/search/{(vertical == MetadataVerticals.Movies ? "movie" : "show")}?query={enc}",
                HttpMethod.Get,
                Headers: new Dictionary<string, string>
                {
                    ["trakt-api-version"] = "2",
                    ["trakt-api-key"] = apiKey ?? "",
                }),
            _ => new MetadataHttpRequest(
                $"{baseUrl}/search?q={enc}&vertical={HttpUtility.UrlEncode(vertical)}",
                HttpMethod.Get,
                Headers: string.IsNullOrWhiteSpace(apiKey)
                    ? null
                    : new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" }),
        };

        if (req is null)
        {
            return [];
        }

        var raw = await httpGateway.SendAsync(req, ct);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return ParseSearchResponse(raw, cleanQuery, vertical);
    }

    private async Task<IReadOnlyList<MetadataWorkCandidate>> ExecuteAniDbSearchAsync(
        string baseUrl,
        bool hasCustomUrl,
        string? titleSearchUrl,
        string cleanQuery,
        string enc,
        string vertical,
        string client,
        string clientVer,
        CancellationToken ct)
    {
        var aidCandidate = cleanQuery;
        if (aidCandidate.StartsWith("aid:", StringComparison.OrdinalIgnoreCase))
        {
            aidCandidate = aidCandidate["aid:".Length..].Trim();
        }
        else if (aidCandidate.StartsWith("anidb:", StringComparison.OrdinalIgnoreCase))
        {
            aidCandidate = aidCandidate["anidb:".Length..].Trim();
        }

        if (int.TryParse(aidCandidate, out var directAid) && directAid > 0)
        {
            var req = new MetadataHttpRequest(
                $"{baseUrl}?request=anime&client={HttpUtility.UrlEncode(client)}&clientver={HttpUtility.UrlEncode(clientVer)}&protover=1&aid={directAid}",
                HttpMethod.Get);
            var raw = await httpGateway.SendAsync(req, ct);
            return string.IsNullOrWhiteSpace(raw) ? [] : ParseAniDbXml(raw, cleanQuery, vertical, directAid.ToString());
        }

        if (!string.IsNullOrWhiteSpace(titleSearchUrl))
        {
            var sep = titleSearchUrl.Contains('?') ? "&" : "?";
            var req = new MetadataHttpRequest(
                $"{titleSearchUrl.TrimEnd('/')}{sep}q={enc}&client={HttpUtility.UrlEncode(client)}&clientver={HttpUtility.UrlEncode(clientVer)}",
                HttpMethod.Get);
            var raw = await httpGateway.SendAsync(req, ct);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return ParseSearchResponse(raw, cleanQuery, vertical);
            }
        }
        else if (hasCustomUrl)
        {
            var req = new MetadataHttpRequest(
                $"{baseUrl}?request=anime&client={HttpUtility.UrlEncode(client)}&clientver={HttpUtility.UrlEncode(clientVer)}&protover=1&q={enc}",
                HttpMethod.Get);
            var raw = await httpGateway.SendAsync(req, ct);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return ParseSearchResponse(raw, cleanQuery, vertical);
            }
        }

        // Safe built-in AniDB title crosswalk -> resolves title to aid without sending bogus free-text queries to api.anidb.net
        var crosswalkMatches = MatchAniDbCrosswalk(cleanQuery);
        if (crosswalkMatches.Count > 0)
        {
            var primaryMatch = crosswalkMatches[0];
            var req = new MetadataHttpRequest(
                $"{baseUrl}?request=anime&client={HttpUtility.UrlEncode(client)}&clientver={HttpUtility.UrlEncode(clientVer)}&protover=1&aid={HttpUtility.UrlEncode(primaryMatch.Aid)}",
                HttpMethod.Get);
            var raw = await httpGateway.SendAsync(req, ct);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var parsed = ParseAniDbXml(raw, cleanQuery, vertical, primaryMatch.Aid);
                if (parsed.Count > 0)
                {
                    return parsed;
                }
            }

            return crosswalkMatches
                .Select(m => BuildAniDbCrosswalkCandidate(m, vertical))
                .ToList();
        }

        return [];
    }

    private static List<(string Aid, string PrimaryTitle, string[] AltTitles, int Year)> MatchAniDbCrosswalk(string query)
    {
        var q = query.Trim();
        var matches = new List<(string Aid, string PrimaryTitle, string[] AltTitles, int Year)>();
        foreach (var entry in BuiltInAniDbTitleCrosswalk)
        {
            if (entry.PrimaryTitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                q.Contains(entry.PrimaryTitle, StringComparison.OrdinalIgnoreCase) ||
                entry.AltTitles.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase) || q.Contains(a, StringComparison.OrdinalIgnoreCase)))
            {
                matches.Add(entry);
            }
        }

        return matches;
    }

    private MetadataWorkCandidate BuildAniDbCrosswalkCandidate(
        (string Aid, string PrimaryTitle, string[] AltTitles, int Year) entry,
        string vertical)
    {
        var extIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["anidb"] = entry.Aid,
        };

        var candidate = LatinTitleNormalizer.NormalizeCandidate(new MetadataWorkCandidate(
            ProviderKey: descriptor.Key,
            ProviderItemId: entry.Aid,
            Vertical: vertical,
            Title: entry.PrimaryTitle,
            AltTitles: LatinTitleNormalizer.OrderAndCleanAltTitles(entry.PrimaryTitle, entry.AltTitles),
            OriginalLanguage: "ja",
            Year: entry.Year,
            Status: "ended",
            Overview: $"{entry.PrimaryTitle} ({entry.Year}) via AniDB title crosswalk.",
            PosterUrl: null,
            ExternalIds: extIds,
            Containers: DefaultContainersForVertical(vertical, entry.PrimaryTitle, entry.Year),
            Genres: ["Anime", "Animation"]));

        return MediaTaxonomyClassifier.EnrichCandidate(candidate, formatHint: "TV Series");
    }

    private static MetadataHttpRequest? BuildAcoustIdRequest(string baseUrl, string queryOrId, string apiKey, bool isLookup)
    {
        var clean = queryOrId.Trim();
        if (clean.StartsWith("trackid:", StringComparison.OrdinalIgnoreCase))
        {
            var tid = clean["trackid:".Length..].Trim();
            return string.IsNullOrWhiteSpace(tid)
                ? null
                : new MetadataHttpRequest(
                    $"{baseUrl}/lookup?client={HttpUtility.UrlEncode(apiKey)}&meta=recordings+releasegroups&trackid={HttpUtility.UrlEncode(tid)}",
                    HttpMethod.Get);
        }

        if (Guid.TryParse(clean, out var trackUuid))
        {
            return new MetadataHttpRequest(
                $"{baseUrl}/lookup?client={HttpUtility.UrlEncode(apiKey)}&meta=recordings+releasegroups&trackid={HttpUtility.UrlEncode(trackUuid.ToString())}",
                HttpMethod.Get);
        }

        if (clean.StartsWith("fp:", StringComparison.OrdinalIgnoreCase))
        {
            var payload = clean["fp:".Length..].Trim();
            var duration = "180";
            var fingerprint = payload;

            if (payload.Contains(':'))
            {
                var parts = payload.Split(':', 2, StringSplitOptions.TrimEntries);
                if (int.TryParse(parts[0], out _) && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    duration = parts[0];
                    fingerprint = parts[1];
                }
                else if (int.TryParse(parts[1], out _) && !string.IsNullOrWhiteSpace(parts[0]))
                {
                    fingerprint = parts[0];
                    duration = parts[1];
                }
            }
            else if (payload.Contains('@'))
            {
                var parts = payload.Split('@', 2, StringSplitOptions.TrimEntries);
                if (!string.IsNullOrWhiteSpace(parts[0]) && int.TryParse(parts[1], out _))
                {
                    fingerprint = parts[0];
                    duration = parts[1];
                }
            }

            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                return null;
            }

            return new MetadataHttpRequest(
                $"{baseUrl}/lookup?client={HttpUtility.UrlEncode(apiKey)}&meta=recordings+releasegroups&duration={HttpUtility.UrlEncode(duration)}&fingerprint={HttpUtility.UrlEncode(fingerprint)}",
                HttpMethod.Get);
        }

        if (clean.Contains("fingerprint=", StringComparison.OrdinalIgnoreCase))
        {
            var parsedQs = HttpUtility.ParseQueryString(clean.TrimStart('?'));
            var fp = parsedQs["fingerprint"];
            var dur = parsedQs["duration"] ?? "180";
            if (!string.IsNullOrWhiteSpace(fp))
            {
                return new MetadataHttpRequest(
                    $"{baseUrl}/lookup?client={HttpUtility.UrlEncode(apiKey)}&meta=recordings+releasegroups&duration={HttpUtility.UrlEncode(dur)}&fingerprint={HttpUtility.UrlEncode(fp)}",
                    HttpMethod.Get);
            }
        }

        if (isLookup && !clean.Contains(' '))
        {
            return new MetadataHttpRequest(
                $"{baseUrl}/lookup?client={HttpUtility.UrlEncode(apiKey)}&meta=recordings+releasegroups&trackid={HttpUtility.UrlEncode(clean)}",
                HttpMethod.Get);
        }

        // Never send a bogus plain-text search to AcoustID!
        return null;
    }

    private static MetadataHttpRequest? BuildTmdbSearchRequest(
        string baseUrl,
        bool hasCustomUrl,
        string cleanQuery,
        string enc,
        string vertical,
        string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey) || hasCustomUrl)
        {
            return new MetadataHttpRequest(
                $"{baseUrl}/search/{(vertical == MetadataVerticals.Movies ? "movie" : "tv")}?query={enc}&language=en-US&api_key={HttpUtility.UrlEncode(apiKey ?? "")}",
                HttpMethod.Get);
        }

        // Zero-key Radarr SkyHook proxy for Movies
        if (vertical == MetadataVerticals.Movies)
        {
            if (ImdbIdRegex().IsMatch(cleanQuery))
            {
                return new MetadataHttpRequest($"{RadarrSkyHookBase}/movie/imdb/{Uri.EscapeDataString(cleanQuery)}", HttpMethod.Get);
            }

            return new MetadataHttpRequest($"{RadarrSkyHookBase}/search?q={enc}&year=", HttpMethod.Get);
        }

        return null;
    }

    private static MetadataHttpRequest? BuildTvdbSearchRequest(
        string baseUrl,
        bool hasCustomUrl,
        string enc,
        string vertical,
        string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey) || hasCustomUrl)
        {
            return new MetadataHttpRequest(
                $"{baseUrl}/search?query={enc}&language=eng&type={(vertical == MetadataVerticals.Movies ? "movie" : "series")}",
                HttpMethod.Get,
                Headers: string.IsNullOrWhiteSpace(apiKey)
                    ? null
                    : new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" });
        }

        // Zero-key Sonarr SkyHook v2 proxy for TV & Anime
        if (vertical is MetadataVerticals.Tv or MetadataVerticals.Anime)
        {
            return new MetadataHttpRequest($"{SonarrSkyHookBase}/search?language=eng&term={enc}", HttpMethod.Get);
        }

        return null;
    }

    private static MetadataHttpRequest BuildAudnexusSearchRequest(
        string baseUrl,
        bool hasCustomUrl,
        string cleanQuery,
        string enc)
    {
        if (hasCustomUrl)
        {
            return new MetadataHttpRequest($"{baseUrl}/books?title={enc}", HttpMethod.Get);
        }

        if (AsinRegex().IsMatch(cleanQuery))
        {
            return new MetadataHttpRequest($"{baseUrl}/books/{Uri.EscapeDataString(cleanQuery)}", HttpMethod.Get);
        }

        return new MetadataHttpRequest(
            $"https://itunes.apple.com/search?term={enc}&media=audiobook&limit=12",
            HttpMethod.Get);
    }

    private MetadataHttpRequest? BuildLookupRequest(
        string baseUrl,
        bool hasCustomUrl,
        string externalId,
        string vertical,
        string? apiKey,
        string? aniDbClient = null,
        string? aniDbClientVer = null)
    {
        if (descriptor.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var enc = Uri.EscapeDataString(externalId);
        return descriptor.Key switch
        {
            "anilist" when int.TryParse(externalId, out var anilistId) => new MetadataHttpRequest(
                baseUrl,
                HttpMethod.Post,
                JsonBody: JsonSerializer.Serialize(new
                {
                    query = "query ($id: Int) { Media(id: $id) { id idMal format genres countryOfOrigin title { romaji english native } synonyms startDate { year month day } status episodes description coverImage { extraLarge large } streamingEpisodes { title } } }",
                    variables = new { id = anilistId },
                })),
            "tmdb" when !string.IsNullOrWhiteSpace(apiKey) || hasCustomUrl => new MetadataHttpRequest(
                $"{baseUrl}/{(vertical == MetadataVerticals.Movies ? "movie" : "tv")}/{enc}?language=en-US&append_to_response=alternative_titles,translations,external_ids&api_key={HttpUtility.UrlEncode(apiKey ?? "")}",
                HttpMethod.Get),
            "tmdb" when vertical == MetadataVerticals.Movies => ImdbIdRegex().IsMatch(externalId)
                ? new MetadataHttpRequest($"{RadarrSkyHookBase}/movie/imdb/{enc}", HttpMethod.Get)
                : new MetadataHttpRequest($"{RadarrSkyHookBase}/movie/{enc}", HttpMethod.Get),
            "tmdb" => null,
            "tvdb" when string.IsNullOrWhiteSpace(apiKey) && !hasCustomUrl && vertical is MetadataVerticals.Tv or MetadataVerticals.Anime =>
                new MetadataHttpRequest($"{SonarrSkyHookBase}/shows/eng/{enc}/official", HttpMethod.Get),
            "tvdb" when string.IsNullOrWhiteSpace(apiKey) && !hasCustomUrl => null,
            "tvmaze" => new MetadataHttpRequest($"{baseUrl}/shows/{enc}?embed=episodes", HttpMethod.Get),
            "audnexus" when !hasCustomUrl && !AsinRegex().IsMatch(externalId) =>
                new MetadataHttpRequest($"https://itunes.apple.com/lookup?id={HttpUtility.UrlEncode(externalId)}", HttpMethod.Get),
            "audnexus" => new MetadataHttpRequest($"{baseUrl}/books/{enc}", HttpMethod.Get),
            "openlibrary" => new MetadataHttpRequest($"{baseUrl}/works/{enc}.json", HttpMethod.Get),
            "musicbrainz" => new MetadataHttpRequest($"{baseUrl}/release-group/{enc}?inc=artist-credits+aliases+releases&fmt=json", HttpMethod.Get),
            "mangadex" => new MetadataHttpRequest($"{baseUrl}/manga/{enc}?includes[]=cover_art", HttpMethod.Get),
            "googlebooks" => new MetadataHttpRequest($"{baseUrl}/volumes/{enc}", HttpMethod.Get),
            "dnb" => new MetadataHttpRequest(
                $"{baseUrl}?version=1.1&operation=searchRetrieve&query={HttpUtility.UrlEncode(externalId.StartsWith("idn=", StringComparison.OrdinalIgnoreCase) ? externalId : $"idn={externalId}")}&maximumRecords=1&recordSchema=oai_dc",
                HttpMethod.Get),
            "bookbrainz" => new MetadataHttpRequest($"{baseUrl}/work/{enc}", HttpMethod.Get),
            "discogs" => BuildDiscogsLookupRequest(baseUrl, externalId, apiKey!),
            "comicvine" => BuildComicVineLookupRequest(baseUrl, externalId, apiKey!),
            "anidb" => BuildAniDbLookupRequest(baseUrl, externalId, aniDbClient ?? apiKey!, aniDbClientVer ?? "1"),
            "acoustid" => BuildAcoustIdRequest(baseUrl, externalId, apiKey!, isLookup: true),
            "trakt" => new MetadataHttpRequest(
                $"{baseUrl}/{(vertical == MetadataVerticals.Movies ? "movies" : "shows")}/{enc}?extended=full",
                HttpMethod.Get,
                Headers: new Dictionary<string, string>
                {
                    ["trakt-api-version"] = "2",
                    ["trakt-api-key"] = apiKey ?? "",
                }),
            "hardcover" => new MetadataHttpRequest(
                baseUrl,
                HttpMethod.Post,
                Headers: new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" },
                JsonBody: int.TryParse(externalId, out var hcBookId)
                    ? JsonSerializer.Serialize(new
                    {
                        query = "query ($id: Int!) { books_by_pk(id: $id) { id slug title release_year description image { url } contributions { author { name } } } }",
                        variables = new { id = hcBookId },
                    })
                    : JsonSerializer.Serialize(new
                    {
                        query = "query ($slug: String!) { books(where: { slug: { _eq: $slug } }, limit: 1) { id slug title release_year description image { url } contributions { author { name } } } }",
                        variables = new { slug = externalId },
                    })),
            "tvdb" => new MetadataHttpRequest(
                $"{baseUrl}/{(vertical == MetadataVerticals.Movies ? "movies" : "series")}/{enc}/extended?meta=translations",
                HttpMethod.Get,
                Headers: string.IsNullOrWhiteSpace(apiKey)
                    ? null
                    : new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" }),
            _ => new MetadataHttpRequest(
                $"{baseUrl}/lookup/{enc}?vertical={HttpUtility.UrlEncode(vertical)}",
                HttpMethod.Get,
                Headers: string.IsNullOrWhiteSpace(apiKey)
                    ? null
                    : new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" }),
        };
    }

    private static MetadataHttpRequest BuildDiscogsLookupRequest(string baseUrl, string externalId, string apiKey)
    {
        var clean = externalId.Trim();
        var isRelease = false;
        if (clean.StartsWith("release:", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["release:".Length..].Trim();
            isRelease = true;
        }
        else if (clean.StartsWith("releases/", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["releases/".Length..].Trim();
            isRelease = true;
        }
        else if (clean.Length > 1 && (clean[0] == 'r' || clean[0] == 'R') && clean[1..].All(char.IsDigit))
        {
            clean = clean[1..];
            isRelease = true;
        }
        else if (clean.StartsWith("master:", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["master:".Length..].Trim();
        }
        else if (clean.StartsWith("masters/", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["masters/".Length..].Trim();
        }
        else if (clean.Length > 1 && (clean[0] == 'm' || clean[0] == 'M') && clean[1..].All(char.IsDigit))
        {
            clean = clean[1..];
        }

        var endpoint = isRelease ? "releases" : "masters";
        return new MetadataHttpRequest(
            $"{baseUrl}/{endpoint}/{Uri.EscapeDataString(clean)}",
            HttpMethod.Get,
            Headers: new Dictionary<string, string>
            {
                ["Authorization"] = $"Discogs token={apiKey}",
                ["User-Agent"] = "Managerr/1.0 +https://managerr.local",
            });
    }

    private static MetadataHttpRequest BuildComicVineLookupRequest(string baseUrl, string externalId, string apiKey)
    {
        var clean = externalId.Trim();
        var volumeId = clean.StartsWith("4050-", StringComparison.OrdinalIgnoreCase)
            ? clean
            : $"4050-{clean}";
        return new MetadataHttpRequest(
            $"{baseUrl}/volume/{Uri.EscapeDataString(volumeId)}/?api_key={HttpUtility.UrlEncode(apiKey)}&format=json",
            HttpMethod.Get,
            Headers: new Dictionary<string, string>
            {
                ["User-Agent"] = "Managerr/1.0 +https://managerr.local",
            });
    }

    private static MetadataHttpRequest BuildAniDbLookupRequest(string baseUrl, string externalId, string client, string clientVer)
    {
        var aid = externalId.Trim();
        if (aid.StartsWith("aid:", StringComparison.OrdinalIgnoreCase))
        {
            aid = aid["aid:".Length..].Trim();
        }
        else if (aid.StartsWith("anidb:", StringComparison.OrdinalIgnoreCase))
        {
            aid = aid["anidb:".Length..].Trim();
        }

        return new MetadataHttpRequest(
            $"{baseUrl}?request=anime&client={HttpUtility.UrlEncode(client)}&clientver={HttpUtility.UrlEncode(clientVer)}&protover=1&aid={HttpUtility.UrlEncode(aid)}",
            HttpMethod.Get);
    }

    private IReadOnlyList<MetadataWorkCandidate> ParseSearchResponse(string raw, string query, string vertical)
    {
        var trimmed = raw.TrimStart();
        if (descriptor.Key == "dnb" || (trimmed.StartsWith('<') && descriptor.Key != "anidb"))
        {
            return ParseDnbSruXml(raw, vertical, fallbackId: null);
        }

        if (descriptor.Key == "anidb")
        {
            return trimmed.StartsWith('<')
                ? ParseAniDbXml(raw, query, vertical, fallbackId: null)
                : ParseSearchJsonSafe(raw, vertical);
        }

        if (descriptor.Key == "bookbrainz")
        {
            return ParseBookBrainzJson(raw, vertical, fallbackId: null);
        }

        if (descriptor.Key == "discogs")
        {
            return ParseDiscogsJson(raw, vertical, fallbackId: null);
        }

        if (descriptor.Key == "comicvine")
        {
            return ParseComicVineJson(raw, vertical, fallbackId: null);
        }

        if (descriptor.Key == "acoustid")
        {
            return ParseAcoustIdJson(raw, vertical, fallbackId: null);
        }

        return ParseSearchJsonSafe(raw, vertical);
    }

    private MetadataWorkCandidate? ParseLookupResponse(string raw, string externalId, string vertical)
    {
        var trimmed = raw.TrimStart();
        if (descriptor.Key == "dnb" || (trimmed.StartsWith('<') && descriptor.Key != "anidb"))
        {
            return ParseDnbSruXml(raw, vertical, externalId).FirstOrDefault();
        }

        if (descriptor.Key == "anidb")
        {
            return trimmed.StartsWith('<')
                ? ParseAniDbXml(raw, externalId, vertical, externalId).FirstOrDefault()
                : ParseLookupJson(raw, externalId, vertical);
        }

        if (descriptor.Key == "bookbrainz")
        {
            return ParseBookBrainzJson(raw, vertical, externalId).FirstOrDefault();
        }

        if (descriptor.Key == "discogs")
        {
            return ParseDiscogsJson(raw, vertical, externalId).FirstOrDefault();
        }

        if (descriptor.Key == "comicvine")
        {
            return ParseComicVineJson(raw, vertical, externalId).FirstOrDefault();
        }

        if (descriptor.Key == "acoustid")
        {
            return ParseAcoustIdJson(raw, vertical, externalId).FirstOrDefault();
        }

        return ParseLookupJson(raw, externalId, vertical);
    }

    private IReadOnlyList<MetadataWorkCandidate> ParseSearchJsonSafe(string rawJson, string vertical)
    {
        try
        {
            return ParseSearchJson(rawJson, vertical);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private IReadOnlyList<MetadataWorkCandidate> ParseSearchJson(string rawJson, string vertical)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        var results = new List<MetadataWorkCandidate>();

        // 1. AniList & Hardcover GraphQL shapes: { data: { Page: { media: [...] } } } or { data: { books: [...] } }
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var dataEl) &&
            dataEl.ValueKind == JsonValueKind.Object)
        {
            if (dataEl.TryGetProperty("Page", out var pageEl) &&
                pageEl.TryGetProperty("media", out var mediaArr) &&
                mediaArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in mediaArr.EnumerateArray())
                {
                    results.Add(ParseAniListMediaNode(item, vertical));
                }

                return results;
            }

            if (dataEl.TryGetProperty("books", out var hcBooksArr) &&
                hcBooksArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in hcBooksArr.EnumerateArray())
                {
                    var cand = ParseSingleObject(item, vertical, fallbackId: null);
                    if (cand is not null)
                    {
                        results.Add(cand);
                    }
                }

                return results;
            }
        }

        // 2. Single object lookup shape (e.g. Audnexus single ASIN lookup)
        if (root.ValueKind == JsonValueKind.Object &&
            !root.TryGetProperty("results", out _) &&
            !root.TryGetProperty("docs", out _) &&
            !root.TryGetProperty("items", out _) &&
            !root.TryGetProperty("data", out _) &&
            !root.TryGetProperty("release-groups", out _) &&
            !root.TryGetProperty("books", out _))
        {
            var single = ParseSingleObject(root, vertical, fallbackId: null);
            return single is not null ? [single] : [];
        }

        // 3. Generic array or { results | docs | items | data | release-groups | books: [...] }
        JsonElement itemsArray = default;
        if (root.ValueKind == JsonValueKind.Array)
        {
            itemsArray = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var propName in new[] { "results", "docs", "items", "data", "release-groups", "books" })
            {
                if (root.TryGetProperty(propName, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    itemsArray = arr;
                    break;
                }
            }
        }

        if (itemsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var rawItem in itemsArray.EnumerateArray())
            {
                var item = rawItem;
                if (rawItem.ValueKind == JsonValueKind.Object)
                {
                    if (rawItem.TryGetProperty("show", out var showEl) && showEl.ValueKind == JsonValueKind.Object)
                    {
                        item = showEl;
                    }
                    else if (rawItem.TryGetProperty("movie", out var movieEl) && movieEl.ValueKind == JsonValueKind.Object)
                    {
                        item = movieEl;
                    }
                }

                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var cand = ParseSingleObject(item, vertical, fallbackId: null);
                if (cand is not null)
                {
                    results.Add(cand);
                }
            }
        }

        return results;
    }

    private MetadataWorkCandidate ParseAniListMediaNode(JsonElement item, string vertical)
    {
        var id = item.TryGetProperty("id", out var idEl) ? idEl.ToString() : Guid.NewGuid().ToString("N");
        var titleObj = item.TryGetProperty("title", out var tEl) ? tEl : default;
        var romaji = titleObj.ValueKind == JsonValueKind.Object && titleObj.TryGetProperty("romaji", out var r) ? r.GetString() : null;
        var english = titleObj.ValueKind == JsonValueKind.Object && titleObj.TryGetProperty("english", out var en) ? en.GetString() : null;
        var nativeTitle = titleObj.ValueKind == JsonValueKind.Object && titleObj.TryGetProperty("native", out var nat) ? nat.GetString() : null;

        var synonyms = new List<string>();
        if (item.TryGetProperty("synonyms", out var synArr) && synArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var syn in synArr.EnumerateArray())
            {
                if (syn.ValueKind == JsonValueKind.String && syn.GetString() is { Length: > 0 } sVal)
                {
                    synonyms.Add(sVal);
                }
            }
        }

        var primaryTitle = LatinTitleNormalizer.SelectBestEnglishOrLatinTitle(
            englishCandidates: [english],
            latinOrRomajiCandidates: [romaji, ..synonyms],
            fallbackCandidates: [nativeTitle],
            fallbackLabel: $"Anime #{id}");

        var alts = LatinTitleNormalizer.OrderAndCleanAltTitles(
            primaryTitle,
            [english, romaji, ..synonyms, nativeTitle]);

        int? year = item.TryGetProperty("startDate", out var sd) && sd.TryGetProperty("year", out var yr) && yr.ValueKind == JsonValueKind.Number
            ? yr.GetInt32()
            : null;

        var extIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["anilist"] = id };
        if (item.TryGetProperty("idMal", out var mal) && mal.ValueKind == JsonValueKind.Number)
        {
            extIds["mal"] = mal.ToString();
        }

        var rawStatus = item.TryGetProperty("status", out var st) ? (st.GetString()?.ToUpperInvariant() ?? "") : "";
        var normalizedStatus = rawStatus switch
        {
            "RELEASING" or "NOT_YET_RELEASED" => "continuing",
            "FINISHED" or "CANCELLED" => "ended",
            _ => "released",
        };

        string? poster = null;
        if (item.TryGetProperty("coverImage", out var ci) && ci.ValueKind == JsonValueKind.Object)
        {
            poster = TryString(ci, "extraLarge") ?? TryString(ci, "large");
        }

        var format = TryString(item, "format")?.ToUpperInvariant();
        var countryOfOrigin = TryString(item, "countryOfOrigin")?.ToUpperInvariant();
        var origLang = countryOfOrigin switch
        {
            "CN" or "TW" or "HK" => "zh",
            "KR" => "ko",
            _ => "ja",
        };
        var genres = ExtractGenres(item, default);
        var containers = BuildAniListContainers(item, vertical, primaryTitle, year);
        var popularity = ExtractPopularity(item, default);

        var candidate = LatinTitleNormalizer.NormalizeCandidate(new MetadataWorkCandidate(
            ProviderKey: descriptor.Key,
            ProviderItemId: id,
            Vertical: vertical,
            Title: primaryTitle,
            AltTitles: alts,
            OriginalLanguage: origLang,
            Year: year,
            Status: normalizedStatus,
            Overview: CleanDescription(item.TryGetProperty("description", out var d) ? d.GetString() : null),
            PosterUrl: poster,
            ExternalIds: extIds,
            Containers: containers,
            Genres: genres,
            Popularity: popularity));

        return MediaTaxonomyClassifier.EnrichCandidate(candidate, formatHint: format);
    }

    private static IReadOnlyList<MetadataContainerSeed> BuildAniListContainers(
        JsonElement item,
        string vertical,
        string primaryTitle,
        int? year)
    {
        if (vertical is not (MetadataVerticals.Anime or MetadataVerticals.Tv))
        {
            return DefaultContainersForVertical(vertical, primaryTitle, year);
        }

        var streamTitles = new List<string>();
        if (item.TryGetProperty("streamingEpisodes", out var seArr) && seArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var se in seArr.EnumerateArray())
            {
                if (se.ValueKind == JsonValueKind.Object && TryString(se, "title") is { Length: > 0 } stTitle)
                {
                    streamTitles.Add(stTitle);
                }
            }
        }

        var epCount = TryInt(item, "episodes") ?? streamTitles.Count;
        if (epCount <= 0)
        {
            return DefaultContainersForVertical(vertical, primaryTitle, year);
        }

        var capped = Math.Min(epCount, 200);
        var date = year.HasValue ? $"{year.Value}-01-01" : null;
        var units = new List<MetadataUnitSeed>(capped);
        for (var i = 1; i <= capped; i++)
        {
            var rawEpTitle = i <= streamTitles.Count ? streamTitles[i - 1] : $"Episode {i}";
            var cleanEpTitle = LatinTitleNormalizer.SelectBestEnglishOrLatinTitle(
                LatinTitleNormalizer.IsCleanLatinText(rawEpTitle) ? [rawEpTitle] : [],
                [],
                [rawEpTitle],
                $"Episode {i}");
            units.Add(new MetadataUnitSeed("episode", i, i, cleanEpTitle, date));
        }

        return
        [
            new MetadataContainerSeed(
                ContainerKind: "season",
                ContainerNumber: 1,
                Title: "Season 1",
                ReleaseDate: date,
                Units: units),
        ];
    }

}
