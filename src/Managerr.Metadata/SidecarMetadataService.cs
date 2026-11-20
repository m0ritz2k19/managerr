using System.Xml.Linq;

namespace Managerr.Metadata;

public interface ISidecarMetadataService
{
    MetadataWorkCandidate ParseKodiJellyfinNfo(string xmlContent, string fallbackVertical = MetadataVerticals.Movies);
    string WriteKodiJellyfinNfo(MetadataWorkCandidate work);
    MetadataWorkCandidate ParseCalibreOpf(string xmlContent, string fallbackVertical = MetadataVerticals.Ebooks);
    string WriteCalibreOpf(MetadataWorkCandidate work);
}

/// <summary>
/// Offline local escape-hatch sidecar reader/writer for Kodi/Jellyfin .nfo and Calibre/EPUB metadata.opf XML files.
/// </summary>
public sealed class SidecarMetadataService : ISidecarMetadataService
{
    private static readonly XNamespace OpfNs = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace DcNs = "http://purl.org/dc/elements/1.1/";

    public MetadataWorkCandidate ParseKodiJellyfinNfo(
        string xmlContent,
        string fallbackVertical = MetadataVerticals.Movies)
    {
        var doc = XDocument.Parse(xmlContent, LoadOptions.None);
        var root = doc.Root ?? throw new FormatException("Empty .nfo XML document.");
        var rootName = root.Name.LocalName.ToLowerInvariant();

        var vertical = rootName switch
        {
            "tvshow" => MetadataVerticals.Tv,
            "movie" => MetadataVerticals.Movies,
            "artist" or "album" => MetadataVerticals.Music,
            _ => fallbackVertical,
        };

        var title = root.Element("title")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new FormatException("NFO document is missing <title>.");
        }

        var altTitles = new List<string>();
        var origTitle = root.Element("originaltitle")?.Value.Trim();
        if (!string.IsNullOrWhiteSpace(origTitle) && !string.Equals(origTitle, title, StringComparison.OrdinalIgnoreCase))
        {
            altTitles.Add(origTitle);
        }

        foreach (var aka in root.Elements("akatitle").Select(x => x.Value.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (!altTitles.Contains(aka, StringComparer.OrdinalIgnoreCase))
            {
                altTitles.Add(aka);
            }
        }

        int? year = null;
        if (int.TryParse(root.Element("year")?.Value.Trim(), out var parsedYear))
        {
            year = parsedYear;
        }
        else if (DateTimeOffset.TryParse(root.Element("premiered")?.Value.Trim(), out var prem))
        {
            year = prem.Year;
        }

        var overview = root.Element("plot")?.Value.Trim()
            ?? root.Element("outline")?.Value.Trim()
            ?? "";
        var lang = root.Element("original_language")?.Value.Trim()
            ?? root.Element("language")?.Value.Trim()
            ?? "en";
        var poster = root.Elements("thumb")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("aspect") ?? "poster", "poster", StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim();

        var extIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uid in root.Elements("uniqueid"))
        {
            var typeAttr = ((string?)uid.Attribute("type"))?.Trim().ToLowerInvariant();
            var val = uid.Value.Trim();
            if (!string.IsNullOrWhiteSpace(typeAttr) && !string.IsNullOrWhiteSpace(val))
            {
                extIds[typeAttr] = val;
            }
        }

        var legacyImdb = root.Element("imdbid")?.Value.Trim() ?? root.Element("id")?.Value.Trim();
        if (!string.IsNullOrWhiteSpace(legacyImdb) && legacyImdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase) && !extIds.ContainsKey("imdb"))
        {
            extIds["imdb"] = legacyImdb;
        }

        var localId = extIds.Values.FirstOrDefault() ?? $"local-{Slugify(title)}";
        extIds.TryAdd("local_sidecar", localId);

        var containers = vertical == MetadataVerticals.Tv || vertical == MetadataVerticals.Anime
            ? new[]
            {
                new MetadataContainerSeed("season", 1, "Season 1", year is not null ? $"{year}-01-01" : null,
                [
                    new MetadataUnitSeed("episode", 1, 1, "Episode 1", year is not null ? $"{year}-01-01" : null),
                ]),
            }
            : new[]
            {
                new MetadataContainerSeed("feature_collection", 1, title, year is not null ? $"{year}-01-01" : null,
                [
                    new MetadataUnitSeed("feature", 1, null, title, year is not null ? $"{year}-01-01" : null),
                ]),
            };

        return new MetadataWorkCandidate(
            ProviderKey: "local_sidecar",
            ProviderItemId: localId,
            Vertical: vertical,
            Title: title,
            AltTitles: altTitles,
            OriginalLanguage: lang,
            Year: year,
            Status: root.Element("status")?.Value.Trim().ToLowerInvariant() ?? "released",
            Overview: overview,
            PosterUrl: poster,
            ExternalIds: extIds,
            Containers: containers);
    }

    public string WriteKodiJellyfinNfo(MetadataWorkCandidate work)
    {
        var rootTag = work.Vertical is MetadataVerticals.Tv or MetadataVerticals.Anime ? "tvshow" : "movie";
        var root = new XElement(
            rootTag,
            new XElement("title", work.Title),
            new XElement("original_language", work.OriginalLanguage),
            new XElement("status", work.Status),
            new XElement("plot", work.Overview));

        if (work.AltTitles.Count > 0)
        {
            root.Add(new XElement("originaltitle", work.AltTitles[0]));
            foreach (var aka in work.AltTitles.Skip(1))
            {
                root.Add(new XElement("akatitle", aka));
            }
        }

        if (work.Year.HasValue)
        {
            root.Add(new XElement("year", work.Year.Value));
        }

        if (!string.IsNullOrWhiteSpace(work.PosterUrl))
        {
            root.Add(new XElement("thumb", new XAttribute("aspect", "poster"), work.PosterUrl));
        }

        var first = true;
        foreach (var (prov, val) in work.ExternalIds.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var uid = new XElement("uniqueid", new XAttribute("type", prov), val);
            if (first)
            {
                uid.Add(new XAttribute("default", "true"));
                first = false;
            }
            root.Add(uid);
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
        return doc.ToString();
    }

    public MetadataWorkCandidate ParseCalibreOpf(
        string xmlContent,
        string fallbackVertical = MetadataVerticals.Ebooks)
    {
        var doc = XDocument.Parse(xmlContent, LoadOptions.None);
        var root = doc.Root ?? throw new FormatException("Empty OPF XML document.");
        var metadata = root.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata") ?? root;

        var title = metadata.Elements(DcNs + "title").FirstOrDefault()?.Value.Trim()
            ?? metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new FormatException("OPF document is missing dc:title.");
        }

        var creator = metadata.Elements(DcNs + "creator").FirstOrDefault()?.Value.Trim()
            ?? metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "creator")?.Value.Trim();

        var lang = metadata.Elements(DcNs + "language").FirstOrDefault()?.Value.Trim()
            ?? metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "language")?.Value.Trim()
            ?? "en";

        var description = metadata.Elements(DcNs + "description").FirstOrDefault()?.Value.Trim()
            ?? metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "description")?.Value.Trim()
            ?? "";

        var dateRaw = metadata.Elements(DcNs + "date").FirstOrDefault()?.Value.Trim()
            ?? metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "date")?.Value.Trim();
        int? year = null;
        if (!string.IsNullOrWhiteSpace(dateRaw))
        {
            if (DateTimeOffset.TryParse(dateRaw, out var dt))
            {
                year = dt.Year;
            }
            else if (dateRaw.Length >= 4 && int.TryParse(dateRaw[..4], out var yr))
            {
                year = yr;
            }
        }

        var extIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var idEl in metadata.Elements().Where(e => e.Name.LocalName == "identifier"))
        {
            var scheme = idEl.Attributes().FirstOrDefault(a => a.Name.LocalName == "scheme")?.Value.Trim().ToLowerInvariant()
                ?? "opf_id";
            var val = idEl.Value.Trim();
            if (!string.IsNullOrWhiteSpace(val))
            {
                var mappedKey = scheme switch
                {
                    "isbn" => val.Length == 13 ? "isbn13" : "isbn",
                    _ => scheme,
                };
                extIds[mappedKey] = val;
            }
        }

        var altTitles = new List<string>();
        if (!string.IsNullOrWhiteSpace(creator))
        {
            altTitles.Add($"{creator} - {title}");
        }

        var localId = extIds.Values.FirstOrDefault() ?? $"opf-{Slugify(title)}";
        extIds.TryAdd("local_sidecar", localId);

        var containers = new[]
        {
            new MetadataContainerSeed(
                ContainerKind: "book",
                ContainerNumber: 1,
                Title: title,
                ReleaseDate: year is not null ? $"{year}-01-01" : null,
                Units:
                [
                    new MetadataUnitSeed("ebook_slot", 1, null, $"{title} (Ebook)", year is not null ? $"{year}-01-01" : null),
                    new MetadataUnitSeed("audiobook_slot", 2, null, $"{title} (Audiobook)", year is not null ? $"{year}-01-01" : null),
                ]),
        };

        return new MetadataWorkCandidate(
            ProviderKey: "local_sidecar",
            ProviderItemId: localId,
            Vertical: fallbackVertical,
            Title: title,
            AltTitles: altTitles,
            OriginalLanguage: lang.Split('-')[0].ToLowerInvariant(),
            Year: year,
            Status: "released",
            Overview: description,
            PosterUrl: null,
            ExternalIds: extIds,
            Containers: containers);
    }

    public string WriteCalibreOpf(MetadataWorkCandidate work)
    {
        var metadata = new XElement(
            OpfNs + "metadata",
            new XAttribute(XNamespace.Xmlns + "dc", DcNs),
            new XAttribute(XNamespace.Xmlns + "opf", OpfNs),
            new XElement(DcNs + "title", work.Title),
            new XElement(DcNs + "language", work.OriginalLanguage),
            new XElement(DcNs + "description", work.Overview));

        if (work.Year.HasValue)
        {
            metadata.Add(new XElement(DcNs + "date", $"{work.Year.Value}-01-01"));
        }

        foreach (var (prov, val) in work.ExternalIds.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            metadata.Add(new XElement(
                DcNs + "identifier",
                new XAttribute(OpfNs + "scheme", prov.ToUpperInvariant()),
                val));
        }

        var pkg = new XElement(
            OpfNs + "package",
            new XAttribute("version", "2.0"),
            metadata);

        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), pkg).ToString();
    }

    private static string Slugify(string value) =>
        string.Concat(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');
}
