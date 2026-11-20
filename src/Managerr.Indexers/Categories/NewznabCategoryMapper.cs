namespace Managerr.Indexers.Categories;

public static class MediaVerticals
{
    public const string Movies = "movies";
    public const string Tv = "tv";
    public const string Anime = "anime";
    public const string Music = "music";
    public const string Ebooks = "ebooks";
    public const string Audiobooks = "audiobooks";
    public const string Comics = "comics";

    public static readonly IReadOnlyList<string> All =
    [
        Movies, Tv, Anime, Music, Ebooks, Audiobooks, Comics,
    ];
}

public static class NewznabCategoryMapper
{
    public static IReadOnlyList<int> GetCategoriesForVertical(string? vertical) =>
        vertical?.Trim().ToLowerInvariant() switch
        {
            MediaVerticals.Movies or "movie" => [2000, 2010, 2020, 2030, 2040, 2045, 2050, 2060, 2070, 2080, 2100, 2110, 2120, 2130, 2140, 2145, 2150, 2160, 2200, 2240, 2300, 2340],
            MediaVerticals.Tv or "series" => [5000, 5010, 5020, 5030, 5040, 5045, 5050, 5060, 5080, 5100, 5120, 5130, 5140, 5145, 5150, 5160, 5180, 5200, 5230, 5240, 5300, 5330, 5340],
            MediaVerticals.Anime => [5000, 5020, 5030, 5040, 5045, 5050, 5070, 5100, 5130, 5140, 5145, 5170, 5200, 5270, 5300, 5370],
            MediaVerticals.Music => [3000, 3010, 3020, 3040, 3050, 3060, 3100, 3110, 3140],
            MediaVerticals.Audiobooks => [3030, 3130, 3230, 3330],
            MediaVerticals.Ebooks => [7000, 7010, 7020, 7040, 7050, 7100, 7110, 7120, 7200, 7300],
            MediaVerticals.Comics => [7030, 7130, 7230, 7330],
            _ => [2000, 3000, 3030, 5000, 5070, 7000, 7020, 7030],
        };

    public static int MapCardigannCategoryNameToId(string? catName)
    {
        if (string.IsNullOrWhiteSpace(catName))
        {
            return 8000;
        }

        var c = catName.Trim();
        if (c.StartsWith("TV/Anime", StringComparison.OrdinalIgnoreCase)) return 5070;
        if (c.StartsWith("TV/UHD", StringComparison.OrdinalIgnoreCase)) return 5045;
        if (c.StartsWith("TV/HD", StringComparison.OrdinalIgnoreCase)) return 5040;
        if (c.StartsWith("TV/SD", StringComparison.OrdinalIgnoreCase)) return 5030;
        if (c.StartsWith("TV", StringComparison.OrdinalIgnoreCase)) return 5000;

        if (c.StartsWith("Movies/UHD", StringComparison.OrdinalIgnoreCase)) return 2045;
        if (c.StartsWith("Movies/HD", StringComparison.OrdinalIgnoreCase)) return 2040;
        if (c.StartsWith("Movies/SD", StringComparison.OrdinalIgnoreCase)) return 2030;
        if (c.StartsWith("Movies", StringComparison.OrdinalIgnoreCase)) return 2000;

        if (c.StartsWith("Audio/Audiobook", StringComparison.OrdinalIgnoreCase)) return 3030;
        if (c.StartsWith("Audio/Lossless", StringComparison.OrdinalIgnoreCase)) return 3040;
        if (c.StartsWith("Audio/MP3", StringComparison.OrdinalIgnoreCase)) return 3010;
        if (c.StartsWith("Audio", StringComparison.OrdinalIgnoreCase)) return 3000;

        if (c.StartsWith("Books/Comics", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("Books/Manga", StringComparison.OrdinalIgnoreCase)) return 7030;
        if (c.StartsWith("Books/EBook", StringComparison.OrdinalIgnoreCase)) return 7020;
        if (c.StartsWith("Books", StringComparison.OrdinalIgnoreCase)) return 7000;

        return 8000;
    }

    public static string ResolveVerticalFromCategories(IEnumerable<int> categoryIds, string? fallbackTitle = null)
    {
        var cats = categoryIds.ToArray();
        if (cats.Any(c => c is 5070 or 5170 or 5270 or 5370)) return MediaVerticals.Anime;
        if (cats.Any(c => c is 3030 or 3130 or 3230 or 3330)) return MediaVerticals.Audiobooks;
        if (cats.Any(c => c is 7030 or 7130 or 7230 or 7330)) return MediaVerticals.Comics;
        if (cats.Any(c => c is >= 7000 and < 8000)) return MediaVerticals.Ebooks;
        if (cats.Any(c => c is >= 3000 and < 4000)) return MediaVerticals.Music;
        if (cats.Any(c => c is >= 5000 and < 6000)) return MediaVerticals.Tv;
        if (cats.Any(c => c is >= 2000 and < 3000)) return MediaVerticals.Movies;

        if (!string.IsNullOrWhiteSpace(fallbackTitle))
        {
            var lower = fallbackTitle.ToLowerInvariant();
            if (lower.Contains(".epub") || lower.Contains(" epub")) return MediaVerticals.Ebooks;
            if (lower.Contains(".m4b") || lower.Contains("audiobook")) return MediaVerticals.Audiobooks;
            if (lower.Contains(".cbz") || lower.Contains(".cbr")) return MediaVerticals.Comics;
            if (lower.Contains("flac") && !lower.Contains("1080p") && !lower.Contains("2160p")) return MediaVerticals.Music;
        }

        return MediaVerticals.Movies;
    }

    public static bool IndexerSupportsCategories(IReadOnlyList<int> desiredCats, IReadOnlyList<int> indexerCats)
    {
        if (indexerCats.Count == 0 || desiredCats.Count == 0) return true;
        foreach (var d in desiredCats)
        {
            foreach (var ixCat in indexerCats)
            {
                if (d == ixCat || (d / 100) * 100 == ixCat)
                    return true;
            }
        }
        return false;
    }

    public static IReadOnlyList<int> ResolveEffectiveCategoriesForIndexer(
        IReadOnlyList<int> desiredCats,
        IReadOnlyList<int> indexerCats,
        IReadOnlySet<int>? capsSupportedCats = null)
    {
        if (desiredCats.Count == 0)
        {
            return indexerCats;
        }

        if (indexerCats.Count == 0)
        {
            if (capsSupportedCats is { Count: > 0 })
            {
                var intersected = desiredCats.Where(c => capsSupportedCats.Contains(c)).Distinct().ToList();
                if (intersected.Count > 0)
                {
                    return intersected;
                }
            }

            return desiredCats.Where(c => (c % 1000) < 100).Distinct().ToList();
        }

        static bool IsRegionalBlock(int cat)
        {
            var hundredDigit = (cat / 100) % 10;
            return hundredDigit is 1 or 2 or 3;
        }

        bool IsRegionalSupported(int cat)
        {
            var block = (cat / 100) * 100;
            return indexerCats.Any(x => (x / 100) * 100 == block)
                || (capsSupportedCats != null && capsSupportedCats.Any(x => (x / 100) * 100 == block));
        }

        var candidateSet = new HashSet<int>();

        foreach (var d in desiredCats)
        {
            if (IsRegionalBlock(d) && !IsRegionalSupported(d)) continue;
            var hundredParent = (d / 100) * 100;
            if (indexerCats.Contains(d) || indexerCats.Contains(hundredParent))
            {
                candidateSet.Add(d);
            }
        }

        if (capsSupportedCats is { Count: > 0 })
        {
            var filtered = new HashSet<int>();
            foreach (var c in candidateSet)
            {
                if (capsSupportedCats.Contains(c))
                {
                    filtered.Add(c);
                }
                else
                {
                    var hundredParent = (c / 100) * 100;
                    if (capsSupportedCats.Contains(hundredParent) && desiredCats.Contains(hundredParent))
                    {
                        filtered.Add(hundredParent);
                    }
                }
            }

            if (filtered.Count > 0)
            {
                return filtered.Distinct().ToList();
            }

            var hundredOrDirect = candidateSet.Where(c => indexerCats.Contains(c) || indexerCats.Contains((c / 100) * 100)).ToList();
            if (hundredOrDirect.Count > 0)
            {
                return hundredOrDirect.Distinct().ToList();
            }
        }

        return candidateSet.Distinct().ToList();
    }
}
