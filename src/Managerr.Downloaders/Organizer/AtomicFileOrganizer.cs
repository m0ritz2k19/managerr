using System.Runtime.InteropServices;
using Managerr.Downloaders.Parser;
using Managerr.Indexers.Categories;

namespace Managerr.Downloaders.Organizer;

public sealed record NamingContextMetadata(
    string? EpisodeTitle = null,
    string? QualityTitle = null,
    IReadOnlyList<string>? CustomFormats = null,
    IReadOnlyDictionary<string, string>? ExternalIds = null,
    string ColonReplacement = "space",
    string? ExistingWorkFolderName = null,
    string? ExistingSeasonFolderName = null,
    bool PreferSeasonalAnimeLayout = false,
    string? AlbumTitle = null,
    int? UnitNumber = null);

public sealed record OrganizeFileRequest(
    string SourceFilePath,
    string LibraryRootPath,
    ParsedReleaseInfo ParsedInfo,
    string? NamingTemplateOverride = null,
    bool PreferHardlink = true,
    bool CanMoveSource = false,
    bool ImportCompanionSubtitles = true,
    NamingContextMetadata? NamingContext = null);

public sealed record OrganizeFileResult(
    string SourceFilePath,
    string DestinationFilePath,
    bool Hardlinked,
    string TransferMode,
    IReadOnlyList<string>? ImportedSidecarPaths = null);

public interface IAtomicFileOrganizer
{
    string RenderRelativeDestinationPath(ParsedReleaseInfo info, string extension, string? templateOverride = null);

    string RenderRelativeDestinationPathWithContext(
        ParsedReleaseInfo info,
        string extension,
        string? templateOverride,
        NamingContextMetadata? context) =>
        RenderRelativeDestinationPath(info, extension, templateOverride);

    Task<OrganizeFileResult> ImportFileAtomicallyAsync(OrganizeFileRequest request, CancellationToken ct = default);
}

public sealed partial class AtomicFileOrganizer : IAtomicFileOrganizer
{
    private static readonly string[] SubtitleSidecarExtensions = [".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt"];

    [System.Text.RegularExpressions.GeneratedRegex(@"\[\s*\]|\(\s*\)|\{\s*\}")]
    private static partial System.Text.RegularExpressions.Regex EmptyBracketsRegex();

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int UnixLink(string existingPath, string newPath);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WindowsCreateHardLink(string lpFileName, string lpExistingFileName, nint lpSecurityAttributes);

    public string RenderRelativeDestinationPath(ParsedReleaseInfo info, string extension, string? templateOverride = null) =>
        RenderRelativeDestinationPathWithContext(info, extension, templateOverride, context: null);

    public string RenderRelativeDestinationPathWithContext(
        ParsedReleaseInfo info,
        string extension,
        string? templateOverride,
        NamingContextMetadata? context)
    {
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        var colonMode = context?.ColonReplacement ?? "space";
        var qualityFull = string.Join(" ", new[] { info.Source, info.Resolution, info.VideoCodec, info.HdrFormat }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(qualityFull))
        {
            qualityFull = context?.QualityTitle ?? info.MusicFormat ?? info.BookFormat ?? "Standard";
        }

        var qualityTitle = !string.IsNullOrWhiteSpace(context?.QualityTitle)
            ? context!.QualityTitle!
            : string.Join("-", new[] { info.Source, info.Resolution }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(qualityTitle))
        {
            qualityTitle = qualityFull;
        }

        var safeTitle = SanitizeSegment(info.CleanTitle, colonMode);
        var safeCreator = SanitizeSegment(info.ArtistOrAuthor ?? "Unknown", colonMode);
        var yearStr = info.Year?.ToString() ?? "";
        var yearPart = info.Year.HasValue ? $" ({info.Year.Value})" : "";
        var titleYear = $"{safeTitle}{yearPart}";
        var seasonNum = info.SeasonNumber ?? 1;
        var epFirst = info.EpisodeNumbers.FirstOrDefault(1);
        var absFirst = info.AbsoluteEpisodeNumbers.FirstOrDefault(epFirst);
        var epMultiToken = info.EpisodeNumbers.Count > 1
            ? string.Concat(info.EpisodeNumbers.OrderBy(x => x).Select(e => $"E{e:D2}"))
            : $"E{epFirst:D2}";
        var epNumbersDash = info.EpisodeNumbers.Count > 1
            ? string.Join("-", info.EpisodeNumbers.OrderBy(x => x).Select(e => e.ToString("D2")))
            : epFirst.ToString("D2");

        if (!string.IsNullOrWhiteSpace(templateOverride) &&
            !(info.SeasonNumber == 0 && !templateOverride.Contains("{Season", StringComparison.OrdinalIgnoreCase)))
        {
            var epTitle = SanitizeSegment(context?.EpisodeTitle ?? "", colonMode);
            var audioLangs = info.DetectedLanguages.Count > 0
                ? "[" + string.Join("+", info.DetectedLanguages.Select(l => l.ToUpperInvariant())) + "]"
                : "";
            var customFormats = context?.CustomFormats is { Count: > 0 } cfs
                ? string.Join(" ", cfs.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => SanitizeSegment(x, colonMode)))
                : "";
            var editionTags = !string.IsNullOrWhiteSpace(info.MovieEdition)
                ? SanitizeSegment(info.MovieEdition, colonMode)
                : "";
            var releaseGroup = !string.IsNullOrWhiteSpace(info.ReleaseGroup)
                ? SanitizeSegment(info.ReleaseGroup, colonMode)
                : "";

            var extIds = context?.ExternalIds;
            string GetExtId(string key) =>
                extIds is not null && extIds.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val)
                    ? SanitizeSegment(val, colonMode)
                    : "";

            var imdbId = GetExtId("imdb");
            var tmdbId = GetExtId("tmdb");
            var tvdbId = GetExtId("tvdb");
            var unitOrEpNum = context?.UnitNumber ?? epFirst;
            var albumTitle = !string.IsNullOrWhiteSpace(context?.AlbumTitle)
                ? SanitizeSegment(context.AlbumTitle, colonMode)
                : safeTitle;
            var trackTitle = !string.IsNullOrWhiteSpace(epTitle) ? epTitle : safeTitle;

            var rendered = templateOverride
                .Replace("{Series TitleYear}", titleYear, StringComparison.OrdinalIgnoreCase)
                .Replace("{Movie TitleYear}", titleYear, StringComparison.OrdinalIgnoreCase)
                .Replace("{Series Title}", safeTitle, StringComparison.OrdinalIgnoreCase)
                .Replace("{Series CleanTitle}", safeTitle, StringComparison.OrdinalIgnoreCase)
                .Replace("{Movie Title}", safeTitle, StringComparison.OrdinalIgnoreCase)
                .Replace("{Movie CleanTitle}", safeTitle, StringComparison.OrdinalIgnoreCase)
                .Replace("{Album Title}", albumTitle, StringComparison.OrdinalIgnoreCase)
                .Replace("{Book Title}", safeTitle, StringComparison.OrdinalIgnoreCase)
                .Replace("{Comic Title}", safeTitle, StringComparison.OrdinalIgnoreCase)
                .Replace("{Track:00}", unitOrEpNum.ToString("D2"), StringComparison.OrdinalIgnoreCase)
                .Replace("{Track Title}", trackTitle, StringComparison.OrdinalIgnoreCase)
                .Replace("{Issue:000}", unitOrEpNum.ToString("D3"), StringComparison.OrdinalIgnoreCase)
                .Replace("{Issue:00}", unitOrEpNum.ToString("D2"), StringComparison.OrdinalIgnoreCase)
                .Replace("{Title}", safeTitle, StringComparison.OrdinalIgnoreCase)
                .Replace("{Creator}", safeCreator, StringComparison.OrdinalIgnoreCase)
                .Replace("{Artist Name}", safeCreator, StringComparison.OrdinalIgnoreCase)
                .Replace("{Author Name}", safeCreator, StringComparison.OrdinalIgnoreCase)
                .Replace("{Release Year}", yearStr, StringComparison.OrdinalIgnoreCase)
                .Replace("{Year}", yearStr, StringComparison.OrdinalIgnoreCase)
                .Replace("{Season:00}", seasonNum.ToString("D2"), StringComparison.OrdinalIgnoreCase)
                .Replace("{Season:0}", seasonNum.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{Episode:00}", epNumbersDash, StringComparison.OrdinalIgnoreCase)
                .Replace("{Episode:000}", epFirst.ToString("D3"), StringComparison.OrdinalIgnoreCase)
                .Replace("{Absolute:000}", absFirst.ToString("D3"), StringComparison.OrdinalIgnoreCase)
                .Replace("{Episode Title}", epTitle, StringComparison.OrdinalIgnoreCase)
                .Replace("{Episode CleanTitle}", epTitle, StringComparison.OrdinalIgnoreCase)
                .Replace("{Quality Full}", SanitizeSegment(qualityFull, colonMode), StringComparison.OrdinalIgnoreCase)
                .Replace("{Quality Title}", SanitizeSegment(qualityTitle, colonMode), StringComparison.OrdinalIgnoreCase)
                .Replace("{MediaInfo VideoCodec}", SanitizeSegment(info.VideoCodec ?? "", colonMode), StringComparison.OrdinalIgnoreCase)
                .Replace("{MediaInfo AudioCodec}", SanitizeSegment(info.AudioCodec ?? "", colonMode), StringComparison.OrdinalIgnoreCase)
                .Replace("{MediaInfo AudioLanguages}", audioLangs, StringComparison.OrdinalIgnoreCase)
                .Replace("{MediaInfo VideoDynamicRangeType}", SanitizeSegment(info.HdrFormat ?? "", colonMode), StringComparison.OrdinalIgnoreCase)
                .Replace("{Custom Formats}", customFormats, StringComparison.OrdinalIgnoreCase)
                .Replace("{Edition Tags}", editionTags, StringComparison.OrdinalIgnoreCase)
                .Replace("{Release Group}", releaseGroup, StringComparison.OrdinalIgnoreCase)
                .Replace("{imdb-{imdbId}}", string.IsNullOrEmpty(imdbId) ? "" : $"{{imdb-{imdbId}}}", StringComparison.OrdinalIgnoreCase)
                .Replace("{tmdb-{tmdbId}}", string.IsNullOrEmpty(tmdbId) ? "" : $"{{tmdb-{tmdbId}}}", StringComparison.OrdinalIgnoreCase)
                .Replace("{tvdb-{tvdbId}}", string.IsNullOrEmpty(tvdbId) ? "" : $"{{tvdb-{tvdbId}}}", StringComparison.OrdinalIgnoreCase)
                .Replace("{imdbId}", imdbId, StringComparison.OrdinalIgnoreCase)
                .Replace("{tmdbId}", tmdbId, StringComparison.OrdinalIgnoreCase)
                .Replace("{tvdbId}", tvdbId, StringComparison.OrdinalIgnoreCase);

            var segments = rendered
                .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Select(CleanRenderedPathSegment)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();

            var joined = Path.Combine(segments);
            return joined.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? joined : joined + ext;
        }

        var existingWorkFolder = IsSafeSinglePathSegment(context?.ExistingWorkFolderName) ? CleanRenderedPathSegment(context!.ExistingWorkFolderName!) : null;
        if (!IsSafeSinglePathSegment(existingWorkFolder)) existingWorkFolder = null;

        var existingSeasonFolder = IsSafeSinglePathSegment(context?.ExistingSeasonFolderName) ? CleanRenderedPathSegment(context!.ExistingSeasonFolderName!) : null;
        if (!IsSafeSinglePathSegment(existingSeasonFolder)) existingSeasonFolder = null;

        return info.MediaVertical switch
        {
            MediaVerticals.Tv => Path.Combine(
                !string.IsNullOrWhiteSpace(existingWorkFolder) ? existingWorkFolder : $"{safeTitle}{yearPart}",
                !string.IsNullOrWhiteSpace(existingSeasonFolder) ? existingSeasonFolder : $"Season {seasonNum:D2}",
                $"{safeTitle}{(string.IsNullOrWhiteSpace(existingWorkFolder) ? yearPart : "")} - S{seasonNum:D2}{epMultiToken} [{SanitizeSegment(qualityFull, colonMode)}]{ext}"),

            MediaVerticals.Anime => (seasonNum == 0 || context?.PreferSeasonalAnimeLayout == true || !string.IsNullOrWhiteSpace(existingSeasonFolder))
                ? Path.Combine(
                    !string.IsNullOrWhiteSpace(existingWorkFolder) ? existingWorkFolder : safeTitle,
                    !string.IsNullOrWhiteSpace(existingSeasonFolder) ? existingSeasonFolder : (seasonNum == 0 ? "Season 00" : $"Season {seasonNum:D2}"),
                    $"{safeTitle} - S{seasonNum:D2}{epMultiToken} [{SanitizeSegment(qualityFull, colonMode)}]{ext}")
                : Path.Combine(
                    !string.IsNullOrWhiteSpace(existingWorkFolder) ? existingWorkFolder : safeTitle,
                    $"{safeTitle} - {absFirst:D3} [{SanitizeSegment(qualityFull, colonMode)}]{ext}"),

            MediaVerticals.Music => Path.Combine(
                safeCreator,
                $"{safeTitle}{yearPart}",
                $"{safeCreator} - {safeTitle} [{SanitizeSegment(qualityFull, colonMode)}]{ext}"),

            MediaVerticals.Ebooks or MediaVerticals.Audiobooks or MediaVerticals.Comics => Path.Combine(
                safeCreator,
                safeTitle,
                $"{safeCreator} - {safeTitle} [{SanitizeSegment(qualityFull, colonMode)}]{ext}"),

            _ => Path.Combine(
                !string.IsNullOrWhiteSpace(existingWorkFolder) ? existingWorkFolder : $"{safeTitle}{yearPart}",
                $"{safeTitle}{yearPart}{(info.MovieEdition is not null ? $" [{SanitizeSegment(info.MovieEdition, colonMode)}]" : "")} [{SanitizeSegment(qualityFull, colonMode)}]{ext}"),
        };
    }

    public async Task<OrganizeFileResult> ImportFileAtomicallyAsync(OrganizeFileRequest request, CancellationToken ct = default)
    {
        if (!File.Exists(request.SourceFilePath))
        {
            throw new FileNotFoundException("Source staging file does not exist.", request.SourceFilePath);
        }

        var ext = Path.GetExtension(request.SourceFilePath);
        var relDest = RenderRelativeDestinationPathWithContext(request.ParsedInfo, ext, request.NamingTemplateOverride, request.NamingContext);
        var fullDest = Path.GetFullPath(Path.Combine(request.LibraryRootPath, relDest));
        var normRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.LibraryRootPath)) + Path.DirectorySeparatorChar;
        if (!fullDest.StartsWith(normRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Resolved destination '{fullDest}' escapes library root '{normRoot}'.");
        }
        var destDir = Path.GetDirectoryName(fullDest)!;
        Directory.CreateDirectory(destDir);

        var sidecars = request.ImportCompanionSubtitles
            ? await ImportMatchingSubtitleSidecarsAsync(request.SourceFilePath, fullDest, request.PreferHardlink, request.CanMoveSource, ct)
            : [];

        if (File.Exists(fullDest))
        {
            File.Delete(fullDest);
        }

        if (request.PreferHardlink && TryCreateHardLink(request.SourceFilePath, fullDest))
        {
            return new OrganizeFileResult(request.SourceFilePath, fullDest, Hardlinked: true, TransferMode: "hardlink", ImportedSidecarPaths: sidecars);
        }

        if (request.CanMoveSource && !request.PreferHardlink)
        {
            try
            {
                File.Move(request.SourceFilePath, fullDest, overwrite: true);
                return new OrganizeFileResult(request.SourceFilePath, fullDest, Hardlinked: false, TransferMode: "atomic_move", ImportedSidecarPaths: sidecars);
            }
            catch (IOException)
            {
                // Cross-device move: fall through to atomic temp-copy + rename + source delete below
            }
        }

        // Cross-device fallback: write to temporary file in target directory and rename atomically
        var tempDest = Path.Combine(destDir, $".{Path.GetFileName(fullDest)}.managerr-tmp-{Guid.NewGuid():N}");
        await using (var srcStream = new FileStream(request.SourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true))
        await using (var dstStream = new FileStream(tempDest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
        {
            await srcStream.CopyToAsync(dstStream, ct);
            await dstStream.FlushAsync(ct);
        }

        File.Move(tempDest, fullDest, overwrite: true);
        if (request.CanMoveSource && !request.PreferHardlink)
        {
            try { File.Delete(request.SourceFilePath); } catch { /* Best-effort cleanup when explicit move requested */ }
            return new OrganizeFileResult(request.SourceFilePath, fullDest, Hardlinked: false, TransferMode: "atomic_move", ImportedSidecarPaths: sidecars);
        }

        return new OrganizeFileResult(request.SourceFilePath, fullDest, Hardlinked: false, TransferMode: "atomic_copy", ImportedSidecarPaths: sidecars);
    }

    private static async Task<IReadOnlyList<string>> ImportMatchingSubtitleSidecarsAsync(
        string sourceMediaFile,
        string fullDestMediaFile,
        bool preferHardlink,
        bool canMoveSource,
        CancellationToken ct)
    {
        var imported = new List<string>();
        var srcDir = Path.GetDirectoryName(sourceMediaFile);
        if (string.IsNullOrWhiteSpace(srcDir) || !Directory.Exists(srcDir))
        {
            return imported;
        }

        var srcStem = Path.GetFileNameWithoutExtension(sourceMediaFile);
        var destDir = Path.GetDirectoryName(fullDestMediaFile)!;
        var destStem = Path.GetFileNameWithoutExtension(fullDestMediaFile);

        foreach (var candidate in Directory.EnumerateFiles(srcDir, "*", SearchOption.TopDirectoryOnly))
        {
            var ext = Path.GetExtension(candidate);
            if (!SubtitleSidecarExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var candFile = Path.GetFileName(candidate);
            string suffix;
            if (candFile.StartsWith(srcStem, StringComparison.OrdinalIgnoreCase))
            {
                suffix = candFile[srcStem.Length..]; // e.g. ".de.srt" or ".srt"
            }
            else
            {
                continue;
            }

            var targetSubPath = Path.Combine(destDir, destStem + suffix);
            try
            {
                if (File.Exists(targetSubPath))
                {
                    File.Delete(targetSubPath);
                }

                if (preferHardlink && TryCreateHardLink(candidate, targetSubPath))
                {
                    imported.Add(targetSubPath);
                    continue;
                }

                if (canMoveSource && !preferHardlink)
                {
                    try
                    {
                        File.Move(candidate, targetSubPath, overwrite: true);
                        imported.Add(targetSubPath);
                        continue;
                    }
                    catch (IOException)
                    {
                        // Cross-device fallback below
                    }
                }

                await using (var sIn = File.OpenRead(candidate))
                await using (var sOut = File.Create(targetSubPath))
                {
                    await sIn.CopyToAsync(sOut, ct);
                }

                if (canMoveSource && !preferHardlink)
                {
                    try { File.Delete(candidate); } catch { }
                }

                imported.Add(targetSubPath);
            }
            catch
            {
                // Non-fatal sidecar import failure
            }
        }

        return imported;
    }

    private static bool TryCreateHardLink(string existingFile, string targetFile)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsCreateHardLink(targetFile, existingFile, 0);
            }

            return UnixLink(existingFile, targetFile) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static string SanitizeSegment(string raw, string colonMode = "space")
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var colonReplaced = (colonMode?.Trim().ToLowerInvariant()) switch
        {
            "delete" => raw.Replace(":", "", StringComparison.Ordinal),
            "dash" => raw.Replace(":", "-", StringComparison.Ordinal),
            "space_dash" => raw.Replace(":", " -", StringComparison.Ordinal),
            _ => raw.Replace(":", " ", StringComparison.Ordinal),
        };

        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(colonReplaced.Select(c => invalid.Contains(c) || c is ':' or '/' or '\\' ? ' ' : c).ToArray());
        return string.Join(" ", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    public static bool IsSafeSinglePathSegment(string? seg) =>
        !string.IsNullOrWhiteSpace(seg) &&
        seg is not ("." or "..") &&
        !Path.IsPathRooted(seg) &&
        seg.IndexOfAny(['/', '\\', '\0']) < 0;

    public static string CleanRenderedPathSegment(string segment)
    {
        var sanitizedChars = segment.Where(c => c is not ('/' or '\\' or '\0')).ToArray();
        var sanitized = new string(sanitizedChars);

        var stripped = EmptyBracketsRegex().Replace(sanitized, "");
        while (stripped.Contains(" - - ", StringComparison.Ordinal))
        {
            stripped = stripped.Replace(" - - ", " - ", StringComparison.Ordinal);
        }

        stripped = string.Join(" ", stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (stripped.EndsWith(" -", StringComparison.Ordinal))
        {
            stripped = stripped[..^2].TrimEnd();
        }

        if (stripped is "." or "..")
        {
            return "";
        }

        return stripped;
    }
}
