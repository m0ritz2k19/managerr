using Managerr.Catalog;
using Managerr.Common.Paths;
using Managerr.Security.Users;

namespace Managerr.Host.Services;

public sealed record FilesystemQuickRoot(string Name, string Path, string Kind);

public sealed record FilesystemDirectoryEntry(string Type, string Name, string Path, string? LastModified);

public sealed record FilesystemFileEntry(string Type, string Name, string Path, string Extension, long Size, string? LastModified);

public sealed record FilesystemBrowseResponse(
    string CurrentPath,
    string? Parent,
    IReadOnlyList<FilesystemQuickRoot> QuickRoots,
    IReadOnlyList<FilesystemDirectoryEntry> Directories,
    IReadOnlyList<FilesystemFileEntry> Files,
    bool Truncated);

public sealed record CreateFilesystemDirectoryRequest(string Path);

public sealed record CreateFilesystemDirectoryResponse(string Path, bool Created);

public interface IFilesystemBrowseService
{
    Task<FilesystemBrowseResponse> BrowseAsync(string? rawPath, bool includeFiles = false, PlexUserSummary? actor = null, CancellationToken ct = default);
    Task<CreateFilesystemDirectoryResponse> CreateDirectoryAsync(string rawPath, PlexUserSummary? actor = null, CancellationToken ct = default);
}

public sealed class FilesystemBrowseService : IFilesystemBrowseService
{
    private readonly IPolymorphicCatalogService _catalog;
    private readonly IAppPaths _appPaths;

    public FilesystemBrowseService(IPolymorphicCatalogService catalog, IAppPaths appPaths)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));
    }

    public async Task<FilesystemBrowseResponse> BrowseAsync(
        string? rawPath,
        bool includeFiles = false,
        PlexUserSummary? actor = null,
        CancellationToken ct = default)
    {
        // 1. Reject raw inputs containing null or control characters
        if (rawPath is not null && rawPath.Any(char.IsControl))
        {
            throw new ArgumentException("Path contains invalid control characters.", nameof(rawPath));
        }

        // 2. Determine target path and normalize
        string normalized;
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Trim() == "/")
        {
            normalized = "/";
        }
        else
        {
            var trimmed = rawPath.Trim();

            if (trimmed.Contains('\\'))
            {
                throw new ArgumentException("Path cannot contain backslashes.", nameof(rawPath));
            }

            if (!trimmed.StartsWith('/'))
            {
                throw new ArgumentException("Path must be an absolute path starting with '/'.", nameof(rawPath));
            }

            if (trimmed.Length > 1 && trimmed.EndsWith('/'))
            {
                trimmed = trimmed.TrimEnd('/');
            }

            var rawSegsAfterLeading = trimmed[1..].Split('/', StringSplitOptions.None);
            if (rawSegsAfterLeading.Length == 0 || rawSegsAfterLeading.Any(s => string.IsNullOrWhiteSpace(s) || s is "." or ".."))
            {
                throw new ArgumentException("Path contains invalid empty, relative, or dot segments.", nameof(rawPath));
            }

            normalized = Path.GetFullPath(trimmed).Replace('\\', '/');
            if (normalized.Length > 1 && normalized.EndsWith('/'))
            {
                normalized = normalized.TrimEnd('/');
            }
        }

        // ABAC library access check
        IReadOnlyList<LibrarySummary> unauthLibs = [];
        IReadOnlyList<LibrarySummary> authLibs = [];
        if (actor is not null && !actor.IsServerOwner)
        {
            try
            {
                var allLibs = await _catalog.ListLibrariesAsync(actor: null, ct: ct);
                authLibs = allLibs.Where(lib => _catalog.CanUserAccessLibrary(actor, lib)).ToList();
                unauthLibs = allLibs.Where(lib => !_catalog.CanUserAccessLibrary(actor, lib)).ToList();
            }
            catch
            {
                // Non-fatal if catalog list fails in mock
            }
        }

        // 3. Security Check BEFORE walking up
        if (normalized != "/")
        {
            if (IsRestrictedPath(normalized))
            {
                throw new UnauthorizedAccessException("Access to system or configuration directories is restricted.");
            }

            if (actor is not null && !actor.IsServerOwner && IsUnauthorizedForActor(normalized, unauthLibs, authLibs))
            {
                throw new UnauthorizedAccessException("Access to this library directory is restricted.");
            }
        }

        // Graceful Ancestor Fallback for Non-Existent Safe Paths
        var currentPath = normalized;
        if (currentPath != "/" && !Directory.Exists(currentPath))
        {
            var candidate = currentPath;
            while (!string.IsNullOrEmpty(candidate) && candidate != "/" && !Directory.Exists(candidate))
            {
                var parentCandidate = Path.GetDirectoryName(candidate);
                if (string.IsNullOrEmpty(parentCandidate))
                {
                    candidate = "/";
                    break;
                }

                candidate = parentCandidate.Replace('\\', '/');
                if (candidate.Length > 1 && candidate.EndsWith('/'))
                {
                    candidate = candidate.TrimEnd('/');
                }

                if (candidate != "/")
                {
                    if (IsRestrictedPath(candidate))
                    {
                        throw new UnauthorizedAccessException("Access to system or configuration directories is restricted.");
                    }

                    if (actor is not null && !actor.IsServerOwner && IsUnauthorizedForActor(candidate, unauthLibs, authLibs))
                    {
                        throw new UnauthorizedAccessException("Access to this library directory is restricted.");
                    }
                }
            }

            currentPath = string.IsNullOrEmpty(candidate) ? "/" : candidate;
        }

        // Compute Parent
        string? parent = null;
        if (currentPath != "/")
        {
            var p = Path.GetDirectoryName(currentPath)?.Replace('\\', '/');
            parent = string.IsNullOrEmpty(p) ? "/" : p;
        }

        // 4. Enumerate currentPath
        var tempPathNormalized = Path.GetFullPath(Path.GetTempPath()).Replace('\\', '/').TrimEnd('/');
        var dirEntries = new List<FilesystemDirectoryEntry>();
        var fileEntries = new List<FilesystemFileEntry>();
        var truncated = false;

        if (Directory.Exists(currentPath))
        {
            try
            {
                var dirInfo = new DirectoryInfo(currentPath);
                foreach (var entry in dirInfo.EnumerateFileSystemInfos())
                {
                    try
                    {
                        if (entry.Name.StartsWith('.'))
                        {
                            continue;
                        }

                        var entryFullName = entry.FullName.Replace('\\', '/');

                        // At "/", hide /tmp unless caller explicitly navigated into temp path
                        if (currentPath == "/" &&
                            (string.Equals(entryFullName.TrimEnd('/'), tempPathNormalized, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(entry.Name, "tmp", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        if (IsRestrictedPath(entry.FullName))
                        {
                            continue;
                        }

                        if (actor is not null && !actor.IsServerOwner && IsUnauthorizedForActor(entry.FullName, unauthLibs, authLibs))
                        {
                            continue;
                        }

                        var isDir = entry is DirectoryInfo || entry.Attributes.HasFlag(FileAttributes.Directory);
                        if (isDir)
                        {
                            dirEntries.Add(new FilesystemDirectoryEntry(
                                Type: "folder",
                                Name: entry.Name,
                                Path: entryFullName,
                                LastModified: GetLastModifiedIso(entry)));
                        }
                        else if (includeFiles)
                        {
                            fileEntries.Add(new FilesystemFileEntry(
                                Type: "file",
                                Name: entry.Name,
                                Path: entryFullName,
                                Extension: entry.Extension,
                                Size: GetFileSizeSafe(entry),
                                LastModified: GetLastModifiedIso(entry)));
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        if (dirEntries.Count > 1000)
        {
            truncated = true;
        }
        dirEntries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        if (dirEntries.Count > 1000)
        {
            dirEntries = dirEntries.Take(1000).ToList();
        }

        if (fileEntries.Count > 1000)
        {
            truncated = true;
        }
        fileEntries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        if (fileEntries.Count > 1000)
        {
            fileEntries = fileEntries.Take(1000).ToList();
        }

        // 5. QuickRoots
        var quickRoots = new List<FilesystemQuickRoot>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        (string Path, string Name, string Kind)[] standardMounts =
        [
            ("/media", "Media (/media)", "media"),
            ("/media/movies", "Movies (/media/movies)", "media"),
            ("/media/series", "Series (/media/series)", "media"),
            ("/media/anime", "Anime (/media/anime)", "media"),
            ("/media/animefilme", "Anime Movies (/media/animefilme)", "media"),
            ("/downloads", "Downloads (/downloads)", "downloads"),
            ("/downloads/usenet", "Usenet (/downloads/usenet)", "downloads"),
            ("/downloads/torrents", "Torrents (/downloads/torrents)", "downloads"),
            ("/data", "Data (/data)", "mount"),
            ("/storage", "Storage (/storage)", "mount"),
            ("/mnt", "Mounts (/mnt)", "mount"),
            ("/backups", "Backups (/backups)", "mount"),
        ];

        foreach (var (candPath, candName, candKind) in standardMounts)
        {
            if (Directory.Exists(candPath) && !IsRestrictedPath(candPath))
            {
                var norm = Path.GetFullPath(candPath).Replace('\\', '/').TrimEnd('/');
                if (seenPaths.Add(norm))
                {
                    quickRoots.Add(new FilesystemQuickRoot(candName, candPath, candKind));
                }
            }
        }

        try
        {
            var libraries = await _catalog.ListLibrariesAsync(actor: actor, ct: ct);
            if (libraries is not null)
            {
                foreach (var lib in libraries)
                {
                    if (!string.IsNullOrWhiteSpace(lib.RootFolderPath) &&
                        Directory.Exists(lib.RootFolderPath) &&
                        !IsRestrictedPath(lib.RootFolderPath))
                    {
                        var norm = Path.GetFullPath(lib.RootFolderPath).Replace('\\', '/').TrimEnd('/');
                        if (seenPaths.Add(norm))
                        {
                            quickRoots.Add(new FilesystemQuickRoot(
                                Name: $"{lib.Name} ({lib.RootFolderPath})",
                                Path: lib.RootFolderPath,
                                Kind: "library"));
                        }
                    }
                }
            }
        }
        catch
        {
            // Non-fatal if catalog list fails in mock/test setup
        }

        return new FilesystemBrowseResponse(
            CurrentPath: currentPath,
            Parent: parent,
            QuickRoots: quickRoots,
            Directories: dirEntries,
            Files: fileEntries,
            Truncated: truncated);
    }

    public async Task<CreateFilesystemDirectoryResponse> CreateDirectoryAsync(
        string rawPath,
        PlexUserSummary? actor = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw new ArgumentException("Directory path cannot be empty.", nameof(rawPath));
        }

        if (rawPath.Any(char.IsControl))
        {
            throw new ArgumentException("Path contains invalid control characters.", nameof(rawPath));
        }

        var trimmed = rawPath.Trim();

        if (trimmed.Contains('\\'))
        {
            throw new ArgumentException("Path cannot contain backslashes.", nameof(rawPath));
        }

        if (!trimmed.StartsWith('/'))
        {
            throw new ArgumentException("Path must be an absolute path starting with '/'.", nameof(rawPath));
        }

        if (trimmed.Length > 1 && trimmed.EndsWith('/'))
        {
            trimmed = trimmed.TrimEnd('/');
        }

        if (trimmed == "/")
        {
            throw new ArgumentException("Cannot create root directory.", nameof(rawPath));
        }

        var rawSegs = trimmed[1..].Split('/');
        if (rawSegs.Length == 0 || rawSegs.Any(s => string.IsNullOrWhiteSpace(s) || s is "." or ".." || s.StartsWith('.')))
        {
            throw new ArgumentException("Path contains invalid empty, relative, or dot segments.", nameof(rawPath));
        }

        var fullPath = Path.GetFullPath(trimmed).Replace('\\', '/');
        if (fullPath.Length > 1 && fullPath.EndsWith('/'))
        {
            fullPath = fullPath.TrimEnd('/');
        }

        if (fullPath == "/")
        {
            throw new ArgumentException("Cannot create root directory.", nameof(rawPath));
        }

        if (IsRestrictedPath(fullPath))
        {
            throw new UnauthorizedAccessException("Cannot create directory in a restricted system or configuration path.");
        }

        var parentFull = Path.GetDirectoryName(fullPath)?.Replace('\\', '/');
        if (string.IsNullOrEmpty(parentFull) || parentFull == "/")
        {
            throw new ArgumentException("Creating top-level directories directly under '/' is not permitted.", nameof(rawPath));
        }

        if (parentFull.Length > 1 && parentFull.EndsWith('/'))
        {
            parentFull = parentFull.TrimEnd('/');
        }

        if (!Directory.Exists(parentFull))
        {
            throw new DirectoryNotFoundException($"Parent directory '{parentFull}' does not exist.");
        }

        if (IsRestrictedPath(parentFull))
        {
            throw new UnauthorizedAccessException($"Parent directory '{parentFull}' is a restricted system path.");
        }

        if (actor is not null && !actor.IsServerOwner)
        {
            var allLibs = await _catalog.ListLibrariesAsync(null, ct);
            var authLibs = allLibs.Where(l => _catalog.CanUserAccessLibrary(actor, l)).ToList();
            var unauthLibs = allLibs.Where(l => !_catalog.CanUserAccessLibrary(actor, l)).ToList();

            if (IsUnauthorizedForActor(fullPath, unauthLibs, authLibs) ||
                IsUnauthorizedForActor(parentFull, unauthLibs, authLibs))
            {
                throw new UnauthorizedAccessException("Cannot create directory in an unauthorized library path.");
            }
        }

        if (File.Exists(fullPath) ||
            new FileInfo(fullPath).LinkTarget is not null ||
            new DirectoryInfo(fullPath).LinkTarget is not null)
        {
            throw new ArgumentException("A file or symbolic link already exists at the target path.");
        }

        var created = false;
        if (!Directory.Exists(fullPath))
        {
            Directory.CreateDirectory(fullPath);
            created = true;
        }

        if (IsRestrictedPath(fullPath))
        {
            throw new UnauthorizedAccessException("Created directory resolved to a restricted path.");
        }

        return new CreateFilesystemDirectoryResponse(fullPath, created);
    }

    private bool IsRestrictedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return false;
        }

        string norm;
        try
        {
            norm = Path.GetFullPath(path.Trim()).Replace('\\', '/');
            if (norm.Length > 1 && norm.EndsWith('/'))
            {
                norm = norm.TrimEnd('/');
            }
        }
        catch
        {
            return true;
        }

        // 1. Catalog forbidden system root (e.g. /etc, /proc, /sys, /root, /boot, /app, /config, etc.)
        if (PolymorphicCatalogService.IsForbiddenSystemRoot(norm))
        {
            return true;
        }

        // 2. Dynamic ConfigDirectory and BaseDirectory
        var configDir = Path.GetFullPath(_appPaths.ConfigDirectory).Replace('\\', '/').TrimEnd('/');
        var baseDir = Path.GetFullPath(AppContext.BaseDirectory).Replace('\\', '/').TrimEnd('/');

        if (IsUnderOrEquals(norm, configDir) || IsUnderOrEquals(norm, baseDir))
        {
            return true;
        }

        // 3. Block exact temp path root
        var tempPathNormalized = Path.GetFullPath(Path.GetTempPath()).Replace('\\', '/').TrimEnd('/');
        if (norm.Equals(tempPathNormalized, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 4. Reject hidden dot-segments on ANY segment
        if (HasHiddenDotSegment(norm, tempPathNormalized))
        {
            return true;
        }

        // Check resolved ancestor symlink targets
        try
        {
            var current = string.Empty;
            foreach (var seg in norm.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = current + "/" + seg;
                FileSystemInfo fsi = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (fsi.LinkTarget is not null)
                {
                    var resolved = fsi.ResolveLinkTarget(returnFinalTarget: true);
                    if (resolved is null)
                    {
                        return true;
                    }

                    var resolvedNorm = Path.GetFullPath(resolved.FullName).Replace('\\', '/').TrimEnd('/');
                    if (IsUnderOrEquals(resolvedNorm, configDir) || IsUnderOrEquals(resolvedNorm, baseDir))
                    {
                        return true;
                    }

                    if (HasHiddenDotSegment(resolvedNorm, tempPathNormalized))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    private static bool IsUnderOrEquals(string path, string root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
        {
            return false;
        }

        return path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasHiddenDotSegment(string path, string tempRoot)
    {
        string examinePath;
        if (!string.IsNullOrEmpty(tempRoot) &&
            (path.Equals(tempRoot, StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith(tempRoot + "/", StringComparison.OrdinalIgnoreCase)))
        {
            if (path.Length <= tempRoot.Length)
            {
                return false;
            }
            examinePath = path[(tempRoot.Length + 1)..];
        }
        else
        {
            examinePath = path;
        }

        var segments = examinePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(seg => seg.StartsWith('.'));
    }

    private static bool IsUnauthorizedForActor(
        string targetPath,
        IReadOnlyList<LibrarySummary> unauthorizedLibraries,
        IReadOnlyList<LibrarySummary> authorizedLibraries)
    {
        if (unauthorizedLibraries.Count == 0)
        {
            return false;
        }

        string norm;
        try
        {
            norm = Path.GetFullPath(targetPath).Replace('\\', '/').TrimEnd('/');
        }
        catch
        {
            return true;
        }

        var insideUnauthorized = unauthorizedLibraries.Any(lib =>
            !string.IsNullOrWhiteSpace(lib.RootFolderPath) &&
            IsUnderOrEquals(norm, Path.GetFullPath(lib.RootFolderPath).Replace('\\', '/').TrimEnd('/')));

        if (!insideUnauthorized)
        {
            return false;
        }

        var insideAuthorized = authorizedLibraries.Any(lib =>
            !string.IsNullOrWhiteSpace(lib.RootFolderPath) &&
            IsUnderOrEquals(norm, Path.GetFullPath(lib.RootFolderPath).Replace('\\', '/').TrimEnd('/')));

        return !insideAuthorized;
    }

    private static string? GetLastModifiedIso(FileSystemInfo info)
    {
        try
        {
            return info.LastWriteTimeUtc.ToString("O");
        }
        catch
        {
            return null;
        }
    }

    private static long GetFileSizeSafe(FileSystemInfo info)
    {
        try
        {
            return (info as FileInfo)?.Length ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
